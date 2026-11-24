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
            var entries = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
            if (entries.Sum(entry => entry.Length) > MaxTotalBytes)
            {
                throw new InvalidDataException("That dictionary is too large to import.");
            }

            var index = entries.FirstOrDefault(entry => entry.FullName == "index.json")
                ?? throw new InvalidDataException("That zip has no index.json at its top, so it is not a Yomitan dictionary.");
            var info = ReadIndex(Read(index, MaxSmallFileBytes));
            info.Id = id;

            var terms = Banks(entries, TermBank());
            var metas = Banks(entries, TermMetaBank());
            var tags = Banks(entries, TagBank());
            if (terms.Count == 0 && metas.Count == 0)
            {
                throw new InvalidDataException("That dictionary has no words, frequencies or pitch accents. Kanji dictionaries are not read: the popup looks kanji up as words.");
            }

            var styles = entries.FirstOrDefault(entry => entry.FullName == "styles.css") is { } css
                ? Encoding.UTF8.GetString(Read(css, MaxSmallFileBytes))
                : null;
            var media = entries
                .Where(entry => MediaTypes.ContainsKey(Path.GetExtension(entry.Name)) && entry.Length <= MaxMediaBytes)
                .ToList();

            var total = Math.Max(1, terms.Concat(metas).Concat(tags).Concat(media).Sum(entry => entry.CompressedLength));
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
                ReadTags(bank, writer, info);
                Advance(bank);
            }

            foreach (var bank in terms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadTerms(bank, writer, info);
                Advance(bank);
            }

            foreach (var bank in metas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadMetas(bank, writer, info);
                Advance(bank);
            }

            foreach (var entry in media)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.AddMedia(entry.FullName, Read(entry, MaxMediaBytes));
                info.Media++;
                Advance(entry);
            }

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

    private static void ReadTerms(ZipArchiveEntry bank, DictionaryWriter writer, DictionaryInfo info)
    {
        using var document = Parse(bank);
        var glossary = new ArrayBufferWriter<byte>();
        foreach (var row in Rows(document, bank))
        {
            // Format 3: [expression, reading, definitionTags, rules, score, glossary[], sequence, termTags];
            // format 1: [expression, reading, definitionTags, rules, score, ...glossary strings].
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5
                || !Key(row[0], out var expression) || row[1].ValueKind != JsonValueKind.String || !Number(row[4], out var score))
            {
                info.Skipped++;
                continue;
            }

            var reading = row[1].GetString()!;
            if (reading.Length == 0)
            {
                reading = expression;
            }
            else if (reading.Length > MaxKeyLength)
            {
                info.Skipped++;
                continue;
            }

            glossary.Clear();
            long sequence = 0;
            var termTags = string.Empty;
            if (info.Format == 1)
            {
                using var json = new Utf8JsonWriter(glossary);
                json.WriteStartArray();
                foreach (var definition in row.EnumerateArray().Skip(5))
                {
                    definition.WriteTo(json);
                }

                json.WriteEndArray();
            }
            else
            {
                if (row.GetArrayLength() < 6 || row[5].ValueKind != JsonValueKind.Array)
                {
                    info.Skipped++;
                    continue;
                }

                using (var json = new Utf8JsonWriter(glossary))
                {
                    row[5].WriteTo(json);
                }

                if (row.GetArrayLength() > 6 && Number(row[6], out var number))
                {
                    sequence = (long)Math.Round(number);
                }

                if (row.GetArrayLength() > 7)
                {
                    termTags = Tags(row[7]);
                }
            }

            writer.AddTerm(expression, reading, Tags(row[2]), Tags(row[3]), (long)Math.Round(score), glossary.WrittenSpan, sequence, termTags);
            info.Terms++;
        }
    }

    private static void ReadMetas(ZipArchiveEntry bank, DictionaryWriter writer, DictionaryInfo info)
    {
        using var document = Parse(bank);
        foreach (var row in Rows(document, bank))
        {
            // [expression, "freq" | "pitch" | "ipa", data]
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3 || !Key(row[0], out var expression)
                || row[1].ValueKind != JsonValueKind.String || row[1].GetString() is not ("freq" or "pitch" or "ipa") || row[2].ValueKind == JsonValueKind.Null)
            {
                info.Skipped++;
                continue;
            }

            var mode = row[1].GetString()!;
            writer.AddMeta(expression, mode, row[2].GetRawText());
            if (mode == "freq")
            {
                info.Frequencies++;
            }
            else
            {
                info.Pitches++;
            }
        }
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
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && Math.Abs(number) < 9e15;
    }

    private static bool Key(JsonElement value, out string key)
    {
        key = value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
        return key.Length > 0 && key.Length <= MaxKeyLength;
    }

    private static string Tags(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;

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

    /// <summary>Reads a file of the zip whole, but never more of it than <paramref name="limit"/>.</summary>
    private static byte[] Read(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit)
        {
            throw new InvalidDataException($"{entry.FullName} in that dictionary is too large.");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Max(0, entry.Length));
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidDataException($"{entry.FullName} in that dictionary is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The banks of one kind, in the order of their numbers.</summary>
    private static List<ZipArchiveEntry> Banks(List<ZipArchiveEntry> entries, Regex name) => entries
        .Select(entry => (entry, match: name.Match(entry.FullName)))
        .Where(pair => pair.match.Success)
        .OrderBy(pair => long.Parse(pair.match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture))
        .Select(pair => pair.entry)
        .ToList();

    [GeneratedRegex(@"^term_bank_(\d{1,9})\.json$")]
    private static partial Regex TermBank();

    [GeneratedRegex(@"^term_meta_bank_(\d{1,9})\.json$")]
    private static partial Regex TermMetaBank();

    [GeneratedRegex(@"^tag_bank_(\d{1,9})\.json$")]
    private static partial Regex TagBank();
}
