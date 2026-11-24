using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

/// <summary>
/// Writes a small dictionary in Yomitan's format, as the tests need it.
/// </summary>
internal sealed class YomitanZip
{
    public string Title { get; init; } = "Test dictionary";

    public string Revision { get; init; } = "1";

    public int Format { get; init; } = 3;

    public string? FrequencyMode { get; init; }

    public string? IndexUrl { get; init; }

    /// <summary>Gets the rows of the term banks, 2 rows to a bank.</summary>
    public List<object?[]> Terms { get; init; } = [];

    public List<object?[]> Metas { get; init; } = [];

    public List<object?[]> Tags { get; init; } = [];

    public string? Styles { get; init; }

    public Dictionary<string, byte[]> Files { get; init; } = [];

    /// <summary>Gets the index.json to write in place of the one made from the fields, or "" for none.</summary>
    public string? Index { get; init; }

    /// <summary>A format 3 term row.</summary>
    public static object?[] Term(string expression, string reading, string rules, params object[] glossary) =>
        [expression, reading, string.Empty, rules, 0, glossary, 0, string.Empty];

    /// <summary>A format 3 term row with tags and a score.</summary>
    public static object?[] Term(string expression, string reading, string definitionTags, string rules, int score, object[] glossary, int sequence = 0, string termTags = "") =>
        [expression, reading, definitionTags, rules, score, glossary, sequence, termTags];

    public string Write(string folder, string name = "dictionary.zip")
    {
        var path = Path.Combine(folder, name);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        void Add(string entry, string text)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }

        void Banks(string prefix, List<object?[]> rows)
        {
            for (var i = 0; i * 2 < rows.Count; i++)
            {
                Add($"{prefix}_{i + 1}.json", JsonSerializer.Serialize(rows.GetRange(i * 2, Math.Min(2, rows.Count - (i * 2)))));
            }
        }

        if (Index is null)
        {
            var index = new Dictionary<string, object?>
            {
                ["title"] = Title,
                ["revision"] = Revision,
                ["format"] = Format,
                ["author"] = "Tests",
                ["attribution"] = "Made for the tests",
            };
            if (FrequencyMode is not null)
            {
                index["frequencyMode"] = FrequencyMode;
            }

            if (IndexUrl is not null)
            {
                index["isUpdatable"] = true;
                index["indexUrl"] = IndexUrl;
            }

            Add("index.json", JsonSerializer.Serialize(index));
        }
        else if (Index.Length > 0)
        {
            Add("index.json", Index);
        }

        Banks("term_bank", Terms);
        Banks("term_meta_bank", Metas);
        Banks("tag_bank", Tags);
        if (Styles is not null)
        {
            Add("styles.css", Styles);
        }

        foreach (var (entry, data) in Files)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(data);
        }

        return path;
    }
}
