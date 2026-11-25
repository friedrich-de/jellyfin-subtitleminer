using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// Reads a dictionary in Yomitan's format, a zip of <c>index.json</c>, term banks, term meta banks (frequencies,
/// pitch accents, pronunciations), tag banks, a stylesheet and pictures, into a dictionary file. Kanji banks are left
/// out: the popup looks kanji up as words.
/// <para>
/// The banks and pictures are unpacked and read on as many threads as the server has processors, each with its own
/// view of the zip, while this thread writes what they read into the file in the dictionary's own order, so the
/// file is the same however many threads there were.
/// </para>
/// </summary>
internal static partial class YomitanArchive
{
    /// <summary>A bank larger than this is refused.</summary>
    internal const long MaxBankBytes = 256L * 1024 * 1024;

    /// <summary>A picture larger than this is left out.</summary>
    internal const long MaxMediaBytes = 16L * 1024 * 1024;

    /// <summary>A stylesheet or <c>index.json</c> larger than this is refused.</summary>
    internal const long MaxSmallFileBytes = 1024 * 1024;

    /// <summary>A dictionary that unpacks to more than this is refused.</summary>
    internal const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>A word written or read longer than this is left out.</summary>
    internal const int MaxKeyLength = 256;

    /// <summary>No more threads than this read a dictionary: the one that writes the file cannot keep up with more.</summary>
    internal const int MaxThreads = 8;

    /// <summary>
    /// The banks and pictures that are unpacked but not in the file yet take no more memory than about this,
    /// unless one alone is larger.
    /// </summary>
    internal const long MaxBytesUnderWay = 384L * 1024 * 1024;

    /// <summary>The kinds of picture a definition may show, and how they are served.</summary>
    internal static readonly IReadOnlyDictionary<string, string> MediaTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
    };

    private static readonly JsonDocumentOptions Json = new() { MaxDepth = 1024 };

    /// <summary>
    /// Imports the dictionary in <paramref name="zipPath"/> into a new file at <paramref name="databasePath"/>. On
    /// any failure the new file is removed.
    /// </summary>
    /// <param name="zipPath">The zip.</param>
    /// <param name="databasePath">The dictionary file to make.</param>
    /// <param name="id">The name the dictionary gets in the plugin's folder.</param>
    /// <param name="progress">Told how far the import is, from 0 to 1.</param>
    /// <param name="cancellationToken">Stops the import.</param>
    /// <returns>What the dictionary says of itself and what was found in it.</returns>
    /// <exception cref="InvalidDataException">The zip is not a dictionary the plugin can read; the message says why.</exception>
    public static DictionaryInfo Import(string zipPath, string databasePath, string id, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            return ImportInto(zipPath, databasePath, id, progress, cancellationToken);
        }
        catch
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            throw;
        }
    }

    private static DictionaryInfo ImportInto(string zipPath, string databasePath, string id, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("That file is not a zip. A Yomitan dictionary is a zip with an index.json in it.");
        }

        using (zip)
        {
            // Each file with its place in the zip, by which another view of the same zip finds it.
            var entries = zip.Entries.Select((entry, place) => new Piece(entry, place)).Where(piece => !piece.Entry.FullName.EndsWith('/')).ToList();
            if (entries.Sum(piece => piece.Entry.Length) > MaxTotalBytes)
            {
                throw new InvalidDataException("That dictionary is too large to import.");
            }

            var index = entries.FirstOrDefault(piece => piece.Entry.FullName == "index.json")?.Entry
                ?? throw new InvalidDataException("That zip has no index.json at its top, so it is not a Yomitan dictionary.");
            var info = ReadIndex(Read(index, MaxSmallFileBytes));
            info.Id = id;

            var terms = Banks(entries, TermBank(), Kind.Terms);
            var metas = Banks(entries, TermMetaBank(), Kind.Metas);
            var tags = Banks(entries, TagBank(), Kind.Tags);
            if (terms.Count == 0 && metas.Count == 0)
            {
                throw new InvalidDataException("That dictionary has no words, frequencies or pitch accents. Kanji dictionaries are not read: the popup looks kanji up as words.");
            }

            var styles = entries.FirstOrDefault(piece => piece.Entry.FullName == "styles.css") is { } css
                ? Encoding.UTF8.GetString(Read(css.Entry, MaxSmallFileBytes))
                : null;

            // Pictures go in by their paths' order: the file keeps them in that order, and adding at its end is
            // much faster than adding in between.
            var media = entries
                .Where(piece => MediaTypes.ContainsKey(Path.GetExtension(piece.Entry.Name)) && piece.Entry.Length <= MaxMediaBytes)
                .OrderBy(piece => piece.Entry.FullName, StringComparer.Ordinal)
                .Select(piece => piece with { Kind = Kind.Media })
                .ToList();

            var total = Math.Max(1, terms.Concat(metas).Concat(tags).Concat(media).Sum(piece => piece.Entry.CompressedLength));
            var done = 0L;
            void Advance(ZipArchiveEntry entry)
            {
                done += entry.CompressedLength;
                progress?.Report(Math.Min(0.99, (double)done / total));
            }

            using var writer = DictionaryWriter.Create(databasePath);
            foreach (var bank in tags)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadTags(bank.Entry, writer, info);
                Advance(bank.Entry);
            }

            var pieces = terms.Concat(metas).Concat(media).ToList();
            var format = info.Format;
            OrderedWork.Run(
                pieces.Count,
                Math.Min(Environment.ProcessorCount, MaxThreads),
                place => pieces[place].Entry.Length,
                MaxBytesUnderWay,
                () => ZipFile.OpenRead(zipPath),
                (view, place) => Load(view.Entries[pieces[place].Place], pieces[place].Kind, format),
                (place, loaded) =>
                {
                    switch (loaded)
                    {
                        case TermBank bank:
                            writer.AddTerms(bank);
                            info.Terms += bank.Count;
                            info.Skipped += bank.Skipped;
                            break;
                        case MetaBank bank:
                            writer.AddMetas(bank);
                            info.Frequencies += bank.Frequencies;
                            info.Pitches += bank.Pitches;
                            info.Skipped += bank.Skipped;
                            break;
                        case byte[] picture:
                            writer.AddMedia(pieces[place].Entry.FullName, picture);
                            info.Media++;
                            break;
                    }

                    Advance(pieces[place].Entry);
                },
                cancellationToken);

            if (info.Terms == 0 && info.Frequencies == 0 && info.Pitches == 0)
            {
                throw new InvalidDataException("No row of that dictionary could be read.");
            }

            info.Imported = DateTimeOffset.UtcNow;
            writer.Finish(info, styles);
            progress?.Report(1);
            return info;
        }
    }

    /// <summary>Unpacks one file of the zip and reads it, on a thread of its own: a bank into its rows, a picture as it is.</summary>
    private static object Load(ZipArchiveEntry entry, Kind kind, int format)
    {
        if (kind == Kind.Media)
        {
            return Read(entry, MaxMediaBytes);
        }

        if (entry.Length > MaxBankBytes)
        {
            throw new InvalidDataException($"{entry.FullName} in that dictionary is too large.");
        }

        // A bank is a few megabytes, read once and dropped: borrowed, so that a hundred of them do not each
        // leave a large array behind for the garbage collector.
        var bytes = ArrayPool<byte>.Shared.Rent((int)entry.Length);
        try
        {
            var json = bytes.AsSpan(0, ReadInto(entry, bytes.AsSpan(0, (int)entry.Length)));
            return kind == Kind.Terms ? YomitanBank.ReadTerms(json, format, entry.FullName) : YomitanBank.ReadMetas(json, entry.FullName);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static DictionaryInfo ReadIndex(byte[] json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json, Json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The index.json of that dictionary is not JSON.");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The index.json of that dictionary is not an object.");
        }

        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var title = Text("title")?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 256)
        {
            throw new InvalidDataException("That dictionary has no title in its index.json.");
        }

        var format = 0;
        foreach (var name in new[] { "format", "version" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                format = number;
                break;
            }
        }

        if (format is < 1 or > 3)
        {
            throw new InvalidDataException($"That dictionary is in a format this plugin does not read (format {format}; it reads 1 to 3).");
        }

        return new DictionaryInfo
        {
            Title = title,
            Revision = Text("revision") ?? string.Empty,
            Format = format,
            Author = Text("author"),
            Url = Text("url"),
            Description = Text("description"),
            Attribution = Text("attribution"),
            FrequencyMode = Text("frequencyMode"),
            SourceLanguage = Text("sourceLanguage"),
            TargetLanguage = Text("targetLanguage"),
            IndexUrl = root.TryGetProperty("isUpdatable", out var updatable) && updatable.ValueKind == JsonValueKind.True ? Text("indexUrl") : null,
            DownloadUrl = Text("downloadUrl"),
        };
    }

    private static void ReadTags(ZipArchiveEntry bank, DictionaryWriter writer, DictionaryInfo info)
    {
        using var document = Parse(bank);
        foreach (var row in Rows(document, bank))
        {
            // [name, category, order, notes, score]
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5 || row[0].ValueKind != JsonValueKind.String)
            {
                info.Skipped++;
                continue;
            }

            string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
            long Whole(JsonElement value) => Number(value, out var number) ? (long)Math.Round(number) : 0;
            writer.AddTag(new TagRow(row[0].GetString()!, Text(row[1]), Whole(row[2]), Text(row[3]), Whole(row[4])));
        }
    }

    /// <summary>Reads a number that fits a long, or says it is none.</summary>
    internal static bool Number(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && FitsLong(number);
    }

    /// <summary>Whether a number can be kept as a whole number without losing any of it.</summary>
    internal static bool FitsLong(double number) => Math.Abs(number) < 9e15;

    private static JsonElement.ArrayEnumerator Rows(JsonDocument document, ZipArchiveEntry bank)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{bank.FullName} in that dictionary is not a list of rows.");
        }

        return document.RootElement.EnumerateArray();
    }

    private static JsonDocument Parse(ZipArchiveEntry bank)
    {
        try
        {
            return JsonDocument.Parse(Read(bank, MaxBankBytes), Json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{bank.FullName} in that dictionary is not JSON.");
        }
    }

    /// <summary>Reads a file of the zip whole, unless it is larger than <paramref name="limit"/>.</summary>
    private static byte[] Read(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit)
        {
            throw new InvalidDataException($"{entry.FullName} in that dictionary is too large.");
        }

        var bytes = new byte[entry.Length];
        var read = ReadInto(entry, bytes);
        return read == bytes.Length ? bytes : bytes[..read];
    }

    /// <summary>
    /// Unpacks a file of the zip into <paramref name="room"/>, which is as large as the zip says the file is. A
    /// file that unpacks to more than the zip says is refused: the limits are checked against what the zip says.
    /// </summary>
    private static int ReadInto(ZipArchiveEntry entry, Span<byte> room)
    {
        try
        {
            using var stream = entry.Open();
            var read = 0;
            while (read < room.Length)
            {
                var count = stream.Read(room[read..]);
                if (count == 0)
                {
                    return read;
                }

                read += count;
            }

            if (stream.ReadByte() >= 0)
            {
                throw new InvalidDataException($"{entry.FullName} in that dictionary is larger than the zip says it is.");
            }

            return read;
        }
        catch (InvalidDataException error) when (!error.Message.StartsWith(entry.FullName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{entry.FullName} in that dictionary cannot be unpacked: the zip is damaged.");
        }
    }

    /// <summary>The banks of one kind, in the order of their numbers.</summary>
    private static List<Piece> Banks(List<Piece> entries, Regex name, Kind kind) => entries
        .Select(piece => (piece, match: name.Match(piece.Entry.FullName)))
        .Where(pair => pair.match.Success)
        .OrderBy(pair => long.Parse(pair.match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture))
        .Select(pair => pair.piece with { Kind = kind })
        .ToList();

    [GeneratedRegex(@"^term_bank_(\d{1,9})\.json$")]
    private static partial Regex TermBank();

    [GeneratedRegex(@"^term_meta_bank_(\d{1,9})\.json$")]
    private static partial Regex TermMetaBank();

    [GeneratedRegex(@"^tag_bank_(\d{1,9})\.json$")]
    private static partial Regex TagBank();

    /// <summary>What a file of the zip is.</summary>
    private enum Kind
    {
        /// <summary>Nothing the import reads.</summary>
        Other,

        /// <summary>A bank of words.</summary>
        Terms,

        /// <summary>A bank of frequencies, pitch accents and pronunciations.</summary>
        Metas,

        /// <summary>A bank of tags.</summary>
        Tags,

        /// <summary>A picture.</summary>
        Media,
    }

    /// <summary>A file of the zip.</summary>
    /// <param name="Entry">The file.</param>
    /// <param name="Place">Its place among the zip's files, the same in every view of the zip.</param>
    /// <param name="Kind">What it is.</param>
    private sealed record Piece(ZipArchiveEntry Entry, int Place, Kind Kind = Kind.Other);
}
