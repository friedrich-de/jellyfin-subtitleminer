using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

/// <summary>
/// Writes a zip byte by byte, as packers in the wild write them and .NET's own writer does not: a name in
/// Shift-JIS with the same name in UTF-8 in a "Unicode Path" field beside it, a checksum that does not fit.
/// </summary>
internal sealed class RawZip
{
    /// <summary>漢-default.svg as a Japanese Windows writes it, in Shift-JIS.</summary>
    public static readonly byte[] Kan = [0x8A, 0xBF, .. "-default.svg"u8];

    /// <summary>文-default.svg in Shift-JIS. Read as UTF-8 it comes out as the same text as <see cref="Kan"/>.</summary>
    public static readonly byte[] Bun = [0x95, 0xB6, .. "-default.svg"u8];

    /// <summary>表記-default.svg in Shift-JIS, whose second byte is a backslash to a reader that takes it for ASCII.</summary>
    public static readonly byte[] Hyouki = [0x95, 0x5C, 0x8B, 0x4C, .. "-default.svg"u8];

    private readonly List<File> _files = [];

    /// <summary>Gets or sets a value indicating whether the zip ends as one with more than 65,535 files does.</summary>
    public bool Large { get; set; }

    /// <summary>The "Unicode Path" field a packer writes beside <paramref name="rawName"/>.</summary>
    public static byte[] UnicodePath(byte[] rawName, string name, byte version = 1)
    {
        var utf8 = Encoding.UTF8.GetBytes(name);
        var field = new byte[9 + utf8.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(field, 0x7075);
        BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(2), (ushort)(5 + utf8.Length));
        field[4] = version;
        BinaryPrimitives.WriteUInt32LittleEndian(field.AsSpan(5), ZipNames.Checksum(rawName));
        utf8.CopyTo(field, 9);
        return field;
    }

    /// <summary>A file whose name is written in UTF-8 and says so.</summary>
    public RawZip Add(string name, byte[] data, byte[]? extra = null, bool wrongChecksum = false, bool stored = false) =>
        Add(Encoding.UTF8.GetBytes(name), nameInUtf8: true, data, extra, wrongChecksum, stored);

    /// <summary>A file whose name is written in Shift-JIS, with the name in UTF-8 in a "Unicode Path" field.</summary>
    public RawZip AddShiftJis(byte[] rawName, string name, byte[] data) =>
        Add(rawName, nameInUtf8: false, data, UnicodePath(rawName, name));

    /// <summary>A file with its name, flag and extra fields as given.</summary>
    public RawZip Add(byte[] rawName, bool nameInUtf8, byte[] data, byte[]? extra = null, bool wrongChecksum = false, bool stored = false)
    {
        var packed = data;
        if (!stored)
        {
            using var memory = new MemoryStream();
            using (var deflate = new DeflateStream(memory, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data);
            }

            packed = memory.ToArray();
        }

        var checksum = ZipNames.Checksum(data);
        _files.Add(new File(rawName, (ushort)(nameInUtf8 ? 0x800 : 0), extra ?? [], wrongChecksum ? checksum ^ 0xDEADBEEF : checksum, packed, data.Length, (ushort)(stored ? 0 : 8)));
        return this;
    }

    public string Write(string folder, string name = "raw.zip")
    {
        var path = Path.Combine(folder, name);
        using var zip = new MemoryStream();
        using var list = new MemoryStream();
        Span<byte> local = stackalloc byte[30];
        Span<byte> record = stackalloc byte[46];
        foreach (var file in _files)
        {
            var at = (uint)zip.Position;
            local.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034B50);
            BinaryPrimitives.WriteUInt16LittleEndian(local[4..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(local[6..], file.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(local[8..], file.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(local[12..], 0x21);
            BinaryPrimitives.WriteUInt32LittleEndian(local[14..], file.Checksum);
            BinaryPrimitives.WriteUInt32LittleEndian(local[18..], (uint)file.Packed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local[22..], (uint)file.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local[26..], (ushort)file.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local[28..], (ushort)file.Extra.Length);
            zip.Write(local);
            zip.Write(file.Name);
            zip.Write(file.Extra);
            zip.Write(file.Packed);

            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014B50);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record[8..], file.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(record[10..], file.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(record[14..], 0x21);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], file.Checksum);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], (uint)file.Packed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record[24..], (uint)file.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record[28..], (ushort)file.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record[30..], (ushort)file.Extra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record[42..], at);
            list.Write(record);
            list.Write(file.Name);
            list.Write(file.Extra);
        }

        var start = zip.Position;
        list.WriteTo(zip);
        Span<byte> end = stackalloc byte[22];
        end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054B50);
        if (Large)
        {
            // The longer record of a large zip and the pointer to it; the short record then says "see there".
            Span<byte> large = stackalloc byte[56 + 20];
            large.Clear();
            var at = zip.Position;
            BinaryPrimitives.WriteUInt32LittleEndian(large, 0x06064B50);
            BinaryPrimitives.WriteUInt64LittleEndian(large[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(large[12..], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(large[14..], 45);
            BinaryPrimitives.WriteUInt64LittleEndian(large[24..], (ulong)_files.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(large[32..], (ulong)_files.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(large[40..], (ulong)list.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(large[48..], (ulong)start);
            BinaryPrimitives.WriteUInt32LittleEndian(large[56..], 0x07064B50);
            BinaryPrimitives.WriteUInt64LittleEndian(large[64..], (ulong)at);
            BinaryPrimitives.WriteUInt32LittleEndian(large[72..], 1);
            zip.Write(large);
            BinaryPrimitives.WriteUInt16LittleEndian(end[8..], ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(end[10..], ushort.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(end[12..], uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(end[16..], uint.MaxValue);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)_files.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)_files.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)list.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)start);
        }

        zip.Write(end);
        System.IO.File.WriteAllBytes(path, zip.ToArray());
        return path;
    }

    private sealed record File(byte[] Name, ushort Flags, byte[] Extra, uint Checksum, byte[] Packed, int Length, ushort Method);
}
