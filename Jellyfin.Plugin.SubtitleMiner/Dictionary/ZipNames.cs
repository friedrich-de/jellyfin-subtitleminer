using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// The names of a zip's files as Yomitan reads them, which real dictionaries count on.
/// <para>
/// A zip says with a flag that a file's name is written in UTF-8. Without the flag the name is in the packer's own
/// encoding: a packer on a Japanese Windows writes Shift-JIS, and puts the same name in UTF-8 into the "Unicode
/// Path" field beside it. .NET's reader does not look at that field and takes the Shift-JIS bytes for UTF-8, so a
/// picture named 漢-default.svg would be kept under a name no definition asks for, and the popup would say that
/// the picture failed to load.
/// </para>
/// </summary>
internal static class ZipNames
{
    private const uint EndRecord = 0x06054B50;
    private const uint LargeEndLocator = 0x07064B50;
    private const uint LargeEndRecord = 0x06064B50;
    private const uint FileRecord = 0x02014B50;

    /// <summary>The flag of a file whose name is written in UTF-8.</summary>
    private const int NameInUtf8 = 0x800;

    /// <summary>
    /// Info-ZIP's "Unicode Path" field: a version (1), the checksum of the name in the record, and the name in
    /// UTF-8.
    /// </summary>
    private const int UnicodePath = 0x7075;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly uint[] ChecksumTable = MakeChecksumTable();

    /// <summary>
    /// The name of each file of a zip, at its place among <see cref="ZipArchive.Entries"/>: the name in the file's
    /// "Unicode Path" field where the zip does not say the name itself in UTF-8 and the field is for that name,
    /// otherwise the name as .NET reads it.
    /// </summary>
    /// <param name="zipPath">The zip.</param>
    /// <param name="zip">The same zip, opened.</param>
    /// <returns>The names.</returns>
    public static string[] Read(string zipPath, ZipArchive zip)
    {
        var names = new string[zip.Entries.Count];
        for (var place = 0; place < names.Length; place++)
        {
            names[place] = zip.Entries[place].FullName;
        }

        try
        {
            using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            if (FromFields(file, zip) is { } fields)
            {
                for (var place = 0; place < names.Length; place++)
                {
                    names[place] = fields[place] ?? names[place];
                }
            }
        }
        catch (EndOfStreamException)
        {
            // .NET read the zip's list of files and this did not: the names stay as .NET gave them.
        }

        return names;
    }

    /// <summary>The checksum a zip keeps of a file (CRC-32), here of a name.</summary>
    internal static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        var checksum = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            checksum = ChecksumTable[(checksum ^ value) & 0xFF] ^ (checksum >> 8);
        }

        return ~checksum;
    }

    /// <summary>
    /// The names in the "Unicode Path" fields, by the files' places; <c>null</c> at a file that needs none, and
    /// <c>null</c> for all when the list of files is not the one <paramref name="zip"/> holds.
    /// </summary>
    private static string?[]? FromFields(FileStream file, ZipArchive zip)
    {
        if (!FindFileList(file, out var start, out var count) || count != zip.Entries.Count)
        {
            return null;
        }

        var names = new string?[zip.Entries.Count];
        Span<byte> record = stackalloc byte[46];
        file.Position = start;
        for (var place = 0; place < names.Length; place++)
        {
            file.ReadExactly(record);

            // The checksum of the file's content tells that this is the file .NET has at the same place.
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != FileRecord
                || BinaryPrimitives.ReadUInt32LittleEndian(record[16..]) != zip.Entries[place].Crc32)
            {
                return null;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            int name = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
            int extra = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            int comment = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            if ((flags & NameInUtf8) != 0 || extra == 0)
            {
                file.Seek(name + extra + comment, SeekOrigin.Current);
                continue;
            }

            var bytes = ArrayPool<byte>.Shared.Rent(name + extra);
            try
            {
                file.ReadExactly(bytes, 0, name + extra);
                names[place] = FromField(bytes.AsSpan(0, name), bytes.AsSpan(name, extra));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }

            file.Seek(comment, SeekOrigin.Current);
        }

        return names;
    }

    /// <summary>The name in the "Unicode Path" field among a file's extra fields, if it has one for this name.</summary>
    private static string? FromField(ReadOnlySpan<byte> name, ReadOnlySpan<byte> fields)
    {
        while (fields.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(fields);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(fields[2..]);
            fields = fields[4..];
            if (fields.Length < length)
            {
                break;
            }

            // The field holds for the name it was written beside, which its checksum tells: a tool that renamed
            // the file and kept the field leaves one that no longer fits.
            if (id == UnicodePath && length > 5 && fields[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(fields[1..]) == Checksum(name))
            {
                try
                {
                    return StrictUtf8.GetString(fields[5..length]);
                }
                catch (DecoderFallbackException)
                {
                    return null;
                }
            }

            fields = fields[length..];
        }

        return null;
    }

    /// <summary>Finds where a zip's list of files starts and how many files it lists.</summary>
    private static bool FindFileList(FileStream file, out long start, out long count)
    {
        start = 0;
        count = 0;

        // The end record is the last thing in the file, before a comment of at most 64 KB.
        const int EndLength = 22;
        var length = file.Length;
        if (length < EndLength)
        {
            return false;
        }

        var tail = new byte[(int)Math.Min(length, EndLength + ushort.MaxValue)];
        file.Position = length - tail.Length;
        file.ReadExactly(tail);
        var at = tail.Length - EndLength;
        while (at >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at)) != EndRecord)
        {
            at--;
        }

        if (at < 0)
        {
            return false;
        }

        var end = tail.AsSpan(at);
        count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        start = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);

        // A large zip says the same in a longer record before it.
        var endAt = length - tail.Length + at;
        if ((count == ushort.MaxValue || size == uint.MaxValue || start == uint.MaxValue) && endAt >= 20)
        {
            Span<byte> locator = stackalloc byte[20];
            file.Position = endAt - 20;
            file.ReadExactly(locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == LargeEndLocator)
            {
                var where = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
                Span<byte> large = stackalloc byte[56];
                if (where > (ulong)length)
                {
                    return false;
                }

                file.Position = (long)where;
                file.ReadExactly(large);
                if (BinaryPrimitives.ReadUInt32LittleEndian(large) != LargeEndRecord)
                {
                    return false;
                }

                var files = BinaryPrimitives.ReadUInt64LittleEndian(large[32..]);
                var from = BinaryPrimitives.ReadUInt64LittleEndian(large[48..]);
                if (files > int.MaxValue || from > (ulong)length)
                {
                    return false;
                }

                count = (long)files;
                start = (long)from;
            }
        }

        return start <= length;
    }

    private static uint[] MakeChecksumTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
