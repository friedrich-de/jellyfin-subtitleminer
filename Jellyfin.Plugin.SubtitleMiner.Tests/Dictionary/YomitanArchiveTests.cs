using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

public sealed class YomitanArchiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "subtitle-miner-dictionary-" + Guid.NewGuid().ToString("N"));

    public YomitanArchiveTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private static readonly object[] Structured =
    [
        new
        {
            type = "structured-content",
            content = new object[]
            {
                new { tag = "span", style = new { fontWeight = "bold" }, content = "to eat" },
                new { tag = "img", path = "pictures/bowl.png", width = 2, height = 2 },
                string.Concat(Enumerable.Repeat("a long example sentence that is worth compressing. ", 8)),
            },
        },
    ];

    private string Import(YomitanZip zip, string id = "test")
    {
        var path = Path.Combine(_folder, id + ".db");
        YomitanArchive.Import(zip.Write(_folder, id + ".zip"), path, id, null, TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public void A_dictionary_is_imported_with_its_words_tags_frequencies_pitches_styles_and_pictures()
    {
        var progress = new System.Collections.Generic.List<double>();
        var zip = new YomitanZip
        {
            Title = "Test [2026-11-24]",
            FrequencyMode = "rank-based",
            Terms =
            {
                YomitanZip.Term("食べる", "たべる", "v1 vt", "v1", 100, Structured, 1, "P"),
                YomitanZip.Term("食", "しょく", "n", string.Empty, 5, ["food"]),
                YomitanZip.Term("ごはん", string.Empty, "n", string.Empty, 3, ["rice", "meal"]),
            },
            Metas =
            {
                new object[] { "食べる", "freq", new { reading = "たべる", frequency = new { value = 512, displayValue = "512㋕" } } },
                new object[] { "食べる", "pitch", new { reading = "たべる", pitches = new[] { new { position = 2 } } } },
                new object[] { "食べる", "ipa", new { reading = "たべる", transcriptions = new[] { new { ipa = "[ta̠be̞ɾɯ̟]", tags = Array.Empty<string>() } } } },
            },
            Tags = { new object[] { "v1", "partOfSpeech", 0, "Ichidan verb", 0 }, new object[] { "P", "popular", -10, "common word", 10 } },
            Styles = "span { color: red; }",
            Files = { ["pictures/bowl.png"] = [137, 80, 78, 71], ["notes.txt"] = Encoding.UTF8.GetBytes("not a picture") },
        };
        var path = Path.Combine(_folder, "test.db");

        var info = YomitanArchive.Import(zip.Write(_folder), path, "test", new Progress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal("test", info.Id);
        Assert.Equal("Test [2026-11-24]", info.Title);
        Assert.Equal(3, info.Format);
        Assert.Equal("rank-based", info.FrequencyMode);
        Assert.Equal((3L, 1L, 2L, 1L, 0L), (info.Terms, info.Frequencies, info.Pitches, info.Media, info.Skipped));
        Assert.Equal(1, progress[^1]);

        var reader = DictionaryReader.TryOpen(path)!;
        Assert.Equal(info.Title, reader.Info.Title);
        Assert.Equal("span { color: red; }", reader.Styles);
        Assert.Equal("Ichidan verb", reader.Tags["v1"].Notes);
        Assert.Equal("popular", reader.Tags["P"].Category);

        var eat = Assert.Single(reader.FindTerms(["食べる"]));
        Assert.Equal(("食べる", "たべる", "v1 vt", "v1", 100L, 1L, "P"), (eat.Expression, eat.Reading, eat.DefinitionTags, eat.Rules, eat.Score, eat.Sequence, eat.TermTags));
        Assert.Equal(eat.Id, Assert.Single(reader.FindTerms(["たべる"])).Id);
        Assert.Equal(JsonSerializer.Serialize(Structured), reader.Glossaries([eat.Id])[eat.Id]);

        // A word without a reading is read as it is written.
        var rice = Assert.Single(reader.FindTerms(["ごはん"]));
        Assert.Equal("ごはん", rice.Reading);
        Assert.Equal("[\"rice\",\"meal\"]", reader.Glossaries([rice.Id])[rice.Id]);

        Assert.Equal(3, reader.FindMetas(["食べる"]).Count);
        Assert.Equal([137, 80, 78, 71], reader.Media("pictures/bowl.png"));
        Assert.Null(reader.Media("notes.txt"));
    }

    [Fact]
    public void Long_definitions_are_kept_compressed_and_short_ones_as_they_are()
    {
        var long_ = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Structured));
        var short_ = Encoding.UTF8.GetBytes("[\"food\"]");

        var packedLong = DictionaryDatabase.Pack(long_);
        var packedShort = DictionaryDatabase.Pack(short_);

        Assert.True(packedLong.Length < long_.Length / 2, $"{packedLong.Length} of {long_.Length}");
        Assert.Equal(short_.Length + 1, packedShort.Length);
        Assert.Equal(Encoding.UTF8.GetString(long_), DictionaryDatabase.Unpack(packedLong));
        Assert.Equal("[\"food\"]", DictionaryDatabase.Unpack(packedShort));
    }

    [Fact]
    public void A_dictionary_of_format_1_lists_its_definitions_after_the_score()
    {
        var path = Import(new YomitanZip { Format = 1, Terms = { new object?[] { "犬", "いぬ", "n", string.Empty, 1, "dog", "hound" } } });

        var reader = DictionaryReader.TryOpen(path)!;
        var dog = Assert.Single(reader.FindTerms(["犬"]));
        Assert.Equal("[\"dog\",\"hound\"]", reader.Glossaries([dog.Id])[dog.Id]);
    }

    [Fact]
    public void Rows_that_cannot_be_read_are_left_out_and_counted()
    {
        var path = Path.Combine(_folder, "rows.db");
        var zip = new YomitanZip
        {
            Terms =
            {
                YomitanZip.Term("猫", "ねこ", string.Empty, ["cat"]),
                new object?[] { 5, "ねこ", string.Empty, string.Empty, 0, new[] { "not a word" }, 0, string.Empty },
                new object?[] { string.Empty, "ねこ", string.Empty, string.Empty, 0, new[] { "no word" }, 0, string.Empty },
                new object?[] { "猫", "ねこ", string.Empty, string.Empty, "high", new[] { "no score" }, 0, string.Empty },
                new object?[] { new string('長', YomitanArchive.MaxKeyLength + 1), "ながい", string.Empty, string.Empty, 0, new[] { "too long" }, 0, string.Empty },
            },
            Metas = { new object[] { "猫", "sound", 1 }, new object[] { "猫", "freq", 12 } },
        };

        var info = YomitanArchive.Import(zip.Write(_folder), path, "rows", null, TestContext.Current.CancellationToken);

        Assert.Equal((1L, 1L, 5L), (info.Terms, info.Frequencies, info.Skipped));
    }

    [Theory]
    [InlineData("", "That zip has no index.json at its top, so it is not a Yomitan dictionary.")]
    [InlineData("not json", "The index.json of that dictionary is not JSON.")]
    [InlineData("{\"revision\":\"1\",\"format\":3}", "That dictionary has no title in its index.json.")]
    [InlineData("{\"title\":\"New\",\"format\":4}", "That dictionary is in a format this plugin does not read (format 4; it reads 1 to 3).")]
    public void A_zip_that_is_not_a_dictionary_is_refused_and_leaves_no_file(string index, string message)
    {
        var path = Path.Combine(_folder, "refused.db");
        var zip = new YomitanZip { Index = index, Terms = { YomitanZip.Term("猫", "ねこ", string.Empty, ["cat"]) } };

        var error = Assert.Throws<InvalidDataException>(() => YomitanArchive.Import(zip.Write(_folder), path, "refused", null, TestContext.Current.CancellationToken));

        Assert.Equal(message, error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_kanji_dictionary_alone_is_refused()
    {
        var zip = new YomitanZip { Files = { ["kanji_bank_1.json"] = Encoding.UTF8.GetBytes("[[\"日\",\"ニチ\",\"ひ\",\"\",[\"day\"],{}]]") } };

        var error = Assert.Throws<InvalidDataException>(() => YomitanArchive.Import(zip.Write(_folder), Path.Combine(_folder, "kanji.db"), "kanji", null, TestContext.Current.CancellationToken));

        Assert.StartsWith("That dictionary has no words, frequencies or pitch accents.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_refused()
    {
        var file = Path.Combine(_folder, "not.zip");
        File.WriteAllText(file, "hello");

        var error = Assert.Throws<InvalidDataException>(() => YomitanArchive.Import(file, Path.Combine(_folder, "not.db"), "not", null, TestContext.Current.CancellationToken));

        Assert.StartsWith("That file is not a zip.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_import_that_is_stopped_leaves_no_file()
    {
        var path = Path.Combine(_folder, "stopped.db");
        using var stop = new System.Threading.CancellationTokenSource();
        stop.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => YomitanArchive.Import(new YomitanZip { Terms = { YomitanZip.Term("猫", "ねこ", string.Empty, ["cat"]) } }.Write(_folder), path, "stopped", null, stop.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_file_that_is_not_a_dictionary_file_is_not_opened()
    {
        var path = Path.Combine(_folder, "other.db");
        File.WriteAllText(path, "not sqlite");

        Assert.Null(DictionaryReader.TryOpen(path));
    }

    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
