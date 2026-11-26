using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

/// <summary>
/// Dictionaries of your own against the importer, outside Jellyfin: for one that does not import, or shows
/// "Image failed to load" in the popup.
/// </summary>
public sealed class RealDictionaryTests
{
    /// <summary>
    /// Imports each dictionary and looks for every picture its definitions ask for. Set
    /// SUBTITLE_MINER_DICTIONARIES to a folder of zips, or to zips with the system's path separator between them,
    /// to run it. What it found is written to the file SUBTITLE_MINER_DICTIONARIES_REPORT names, if that is set,
    /// and is the message of a failure.
    /// </summary>
    [Fact]
    public void Real_dictionaries_import_with_every_picture_their_definitions_ask_for()
    {
        var where = Environment.GetEnvironmentVariable("SUBTITLE_MINER_DICTIONARIES");
        Assert.SkipWhen(string.IsNullOrEmpty(where), "SUBTITLE_MINER_DICTIONARIES is not set.");

        var zips = Directory.Exists(where)
            ? Directory.GetFiles(where, "*.zip").Order(StringComparer.Ordinal).ToArray()
            : where!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(zips);

        var folder = Path.Combine(Path.GetTempPath(), "subtitle-miner-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        var failed = false;
        try
        {
            for (var i = 0; i < zips.Length; i++)
            {
                failed |= !Check(zips[i], Path.Combine(folder, $"{i}.db"), report);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }

        if (Environment.GetEnvironmentVariable("SUBTITLE_MINER_DICTIONARIES_REPORT") is { Length: > 0 } file)
        {
            File.WriteAllText(file, report.ToString());
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        Assert.False(failed, report.ToString());
    }

    /// <summary>Imports one dictionary and writes what was found; <c>false</c> when it did not import or lacks a picture.</summary>
    private static bool Check(string zip, string database, StringBuilder report)
    {
        string Count(long number) => number.ToString("N0", CultureInfo.InvariantCulture);

        report.AppendLine(Path.GetFileName(zip));
        var watch = Stopwatch.StartNew();
        DictionaryInfo info;
        try
        {
            info = YomitanArchive.Import(zip, database, "real", null, TestContext.Current.CancellationToken);
        }
        catch (InvalidDataException error)
        {
            report.AppendLine("  not imported: " + error.Message).AppendLine();
            return false;
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"  \"{info.Title}\" imported in {watch.Elapsed.TotalSeconds:F1} s: {Count(info.Terms)} words, {Count(info.Frequencies)} frequencies, {Count(info.Pitches)} pitch accents, {Count(info.Media)} pictures, {Count(info.Skipped)} rows left out");

        var reader = DictionaryReader.TryOpen(database)!;
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TermRow>();
        using (var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly"))
        {
            connection.Open();
            using var pictures = connection.CreateCommand();
            pictures.CommandText = "SELECT path FROM media";
            using (var row = pictures.ExecuteReader())
            {
                while (row.Read())
                {
                    kept.Add(row.GetString(0));
                }
            }

            using var terms = connection.CreateCommand();
            terms.CommandText = "SELECT rowid, block, start, length FROM terms ORDER BY block, start";
            using (var row = terms.ExecuteReader())
            {
                while (row.Read())
                {
                    rows.Add(new TermRow(row.GetInt64(0), string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, string.Empty, row.GetInt64(1), row.GetInt32(2), row.GetInt32(3)));
                }
            }
        }

        // How often each picture is asked for, by its path.
        var asked = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var chunk in rows.Chunk(4096))
        {
            foreach (var json in reader.Glossaries(chunk).Values)
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1024 });
                Pictures(document.RootElement, asked);
            }
        }

        var missing = asked.Where(pair => !kept.Contains(pair.Key)).OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        report.AppendLine(CultureInfo.InvariantCulture, $"  its definitions ask for a picture {Count(asked.Values.Sum())} times, for {Count(asked.Count)} different pictures");
        report.AppendLine(CultureInfo.InvariantCulture, $"  missing: {Count(missing.Sum(pair => pair.Value))} times, {Count(missing.Count)} different pictures");
        foreach (var (path, times) in missing.Take(40))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"    {path} ({Count(times)} times)");
        }

        var unreadable = kept.Count(path => path.Contains('�', StringComparison.Ordinal));
        if (unreadable > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  pictures kept under a name with characters that could not be read: {Count(unreadable)}");
        }

        report.AppendLine();
        return missing.Count == 0;
    }

    /// <summary>Counts the pictures in a definition: <c>{"tag": "img", "path": …}</c> and <c>{"type": "image", "path": …}</c>.</summary>
    private static void Pictures(JsonElement node, Dictionary<string, long> asked)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Pictures(item, asked);
                }

                break;
            case JsonValueKind.Object:
                static bool Says(JsonElement node, string name, string value) =>
                    node.TryGetProperty(name, out var said) && said.ValueKind == JsonValueKind.String && said.ValueEquals(value);

                if (node.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && (Says(node, "tag", "img") || Says(node, "type", "image")))
                {
                    var key = path.GetString()!;
                    asked[key] = asked.GetValueOrDefault(key) + 1;
                }

                foreach (var property in node.EnumerateObject())
                {
                    Pictures(property.Value, asked);
                }

                break;
        }
    }
}
