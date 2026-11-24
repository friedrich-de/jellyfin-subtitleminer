using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

public sealed class DictionaryLookupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "subtitle-miner-lookup-" + Guid.NewGuid().ToString("N"));

    public DictionaryLookupTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private static YomitanZip Words(string title = "Words") => new()
    {
        Title = title,
        Terms =
        {
            YomitanZip.Term("食べる", "たべる", "v1 vt", "v1", 10, ["to eat"]),
            YomitanZip.Term("食", "しょく", "n", string.Empty, 1, ["food"]),
            YomitanZip.Term("日本語", "にほんご", "n", string.Empty, 5, ["Japanese"]),
            YomitanZip.Term("日本", "にほん", "n", string.Empty, 5, ["Japan"]),
            YomitanZip.Term("日", "ひ", "n", string.Empty, 5, ["day"]),
            YomitanZip.Term("書く", "かく", "v5", "v5", 5, ["to write"]),
            YomitanZip.Term("書いる", "かいる", "n", string.Empty, 5, ["a noun no verb reaches"]),
            YomitanZip.Term("白い", "しろい", "adj-i", "adj-i", 5, ["white"]),
            YomitanZip.Term("買う", "かう", "v5", "v5", 1, ["to buy"]),
            YomitanZip.Term("飼う", "かう", "v5", "v5", 1, ["to keep an animal"]),
            YomitanZip.Term("ケーキ", "ケーキ", "n", string.Empty, 1, ["cake"]),
            YomitanZip.Term("ございます", "ございます", "exp", string.Empty, 1, ["to be (polite)"]),
            YomitanZip.Term("ござる", "ござる", "v5", "v5", 1, ["to be (archaic)"]),
        },
        Tags = { new object[] { "v1", "partOfSpeech", 0, "Ichidan verb", 0 } },
    };

    private DictionaryReader Open(YomitanZip zip)
    {
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_folder, id + ".db");
        YomitanArchive.Import(zip.Write(_folder, id + ".zip"), path, id, null, TestContext.Current.CancellationToken);
        return DictionaryReader.TryOpen(path)!;
    }

    private LookupView Find(string text, params YomitanZip[] zips) => DictionaryLookup.Find(text, zips.Select(Open).ToList());

    [Fact]
    public void A_conjugated_verb_is_found_as_its_dictionary_form_with_what_was_undone()
    {
        var first = Find("食べられなかったの。", Words()).Results[0];

        Assert.Equal("食べる", first.Term.Expression);
        Assert.Equal("たべる", first.Term.Reading);
        Assert.Equal("食べられなかった", first.Matched);
        Assert.Equal("食べる", first.Deinflected);
        Assert.Equal(["-た", "negative", "potential or passive"], first.Trace.Select(step => step.Name));
        Assert.Equal("[\"to eat\"]", Assert.Single(first.Term.Glossaries).Glossary);
        Assert.Equal("Ichidan verb", Assert.Single(first.Term.Glossaries[0].Tags).Notes);
    }

    [Fact]
    public void The_longest_word_comes_first()
    {
        var results = Find("日本語を話す", Words()).Results;

        Assert.Equal(["日本語", "日本", "日"], results.Select(result => result.Term.Expression));
        Assert.Equal(["日本語", "日本", "日"], results.Select(result => result.Matched));
    }

    [Fact]
    public void A_word_is_reached_by_undoing_an_inflection_only_when_it_is_that_kind_of_word()
    {
        var results = Find("書いた", Words()).Results;

        Assert.Equal("書く", results[0].Term.Expression);
        Assert.DoesNotContain(results, result => result.Term.Expression == "書いる");
    }

    [Fact]
    public void An_adjective_is_found_from_its_negative()
    {
        var first = Find("白くない", Words()).Results[0];

        Assert.Equal(("白い", "白くない"), (first.Term.Expression, first.Matched));
    }

    [Theory]
    [InlineData("たべる")]
    [InlineData("タベル")]
    [InlineData("ﾀﾍﾞﾙ")]
    public void A_word_is_found_by_its_reading_in_hiragana_katakana_or_half_width(string text)
    {
        Assert.Equal("食べる", Find(text, Words()).Results[0].Term.Expression);
    }

    [Fact]
    public void A_katakana_word_is_found_from_hiragana_with_a_long_mark()
    {
        Assert.Equal("ケーキ", Find("けーき", Words()).Results[0].Term.Expression);
    }

    [Fact]
    public void A_polite_form_a_rule_reads_whole_finds_its_dictionary_form_after_the_word_as_written()
    {
        var results = Find("ございます", Words()).Results;

        Assert.Equal(["ございます", "ござる"], results.Take(2).Select(result => result.Term.Expression));
        Assert.Equal(["-ます"], results[1].Trace.Select(step => step.Name));
    }

    [Fact]
    public void Words_that_tie_come_in_the_order_of_the_first_frequency_dictionary()
    {
        var ranks = new YomitanZip
        {
            Title = "Ranks",
            FrequencyMode = "rank-based",
            Metas =
            {
                new object[] { "飼う", "freq", new { reading = "かう", frequency = 5000 } },
                new object[] { "買う", "freq", new { reading = "かう", frequency = new { value = 100, displayValue = "100" } } },
                new object[] { "買う", "freq", new { reading = "ばいう", frequency = 1 } },
            },
        };
        var counts = new YomitanZip
        {
            Title = "Counts",
            FrequencyMode = "occurrence-based",
            Metas = { new object[] { "飼う", "freq", 900 }, new object[] { "買う", "freq", 10 } },
        };

        Assert.Equal(["買う", "飼う"], Find("かう", Words(), ranks).Results.Take(2).Select(result => result.Term.Expression));
        Assert.Equal(["飼う", "買う"], Find("かう", Words(), counts).Results.Take(2).Select(result => result.Term.Expression));

        var buy = Find("かう", Words(), ranks, counts).Results[0];
        Assert.Equal(["Ranks", "Counts"], buy.Term.Frequencies.Select(group => group.Dictionary));
        Assert.Equal([100d], buy.Term.Frequencies[0].Frequencies.Select(frequency => frequency.Value));
        Assert.Equal("100", buy.Term.Frequencies[0].Frequencies[0].DisplayValue);
    }

    [Fact]
    public void A_word_has_the_pitch_accents_of_its_reading_only()
    {
        var pitches = new YomitanZip
        {
            Title = "Pitches",
            Metas =
            {
                new object[] { "食べる", "pitch", new { reading = "たべる", pitches = new object[] { new { position = 2, devoice = 1 }, new { position = "LHL" } } } },
                new object[] { "食べる", "pitch", new { reading = "くらべる", pitches = new object[] { new { position = 0 } } } },
                new object[] { "食べる", "ipa", new { reading = "たべる", transcriptions = new object[] { new { ipa = "[ta̠be̞ɾɯ̟]" } } } },
            },
        };

        var group = Assert.Single(Find("食べる", Words(), pitches).Results[0].Term.Pitches);

        Assert.Equal("Pitches", group.Dictionary);
        Assert.Equal([2, 2], group.Pitches.Select(pitch => pitch.Position));
        Assert.Equal([1], group.Pitches[0].Devoice!);
        Assert.Equal("LHL", group.Pitches[1].Pattern);
        Assert.Equal(["[ta̠be̞ɾɯ̟]"], group.Transcriptions!);
    }

    [Fact]
    public void A_word_in_two_dictionaries_is_one_result_with_both_definitions_in_their_order()
    {
        var other = new YomitanZip { Title = "Other", Terms = { YomitanZip.Term("食べる", "たべる", "v1", "v1", 1, ["to consume"]) } };

        var first = Find("食べる", other, Words()).Results[0];

        Assert.Equal(["Other", "Words"], first.Term.Glossaries.Select(glossary => glossary.Dictionary));
        Assert.Single(Find("食べる", other, Words()).Results, result => result.Term.Expression == "食べる");
    }

    [Fact]
    public void The_text_is_read_up_to_a_space_and_no_further_than_the_scan_length()
    {
        var forms = DictionaryLookup.Forms("日本 語", Deinflector.Japanese);
        Assert.DoesNotContain("日本 語", forms.Keys);
        Assert.Contains("日本", forms.Keys);

        var longText = new string('あ', DictionaryLookup.ScanLength + 10);
        Assert.All(DictionaryLookup.Forms(longText, Deinflector.Japanese).Values.SelectMany(list => list), form => Assert.True(form.Length <= DictionaryLookup.ScanLength));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 食べる")]
    [InlineData("xyz")]
    public void A_text_without_a_word_at_its_start_finds_nothing(string text)
    {
        Assert.Empty(Find(text, Words()).Results);
    }

    [Fact]
    public void No_more_words_are_given_than_the_most_a_lookup_gives()
    {
        var many = new YomitanZip { Title = "Many" };
        for (var i = 0; i < DictionaryLookup.MaxEntries + 5; i++)
        {
            many.Terms.Add(YomitanZip.Term("か", "か" + new string('ー', i), "n", string.Empty, i, [$"meaning {i}"]));
        }

        var results = Find("か", many).Results;

        Assert.Equal(DictionaryLookup.MaxEntries, results.Count);
        Assert.Equal("meaning " + (DictionaryLookup.MaxEntries + 4), results[0].Term.Glossaries[0].Glossary.Trim('[', ']', '"'));
    }

    [Theory]
    [InlineData("12", 12d, null)]
    [InlineData("\"3㋕\"", 3d, "3㋕")]
    [InlineData("{\"value\":7,\"displayValue\":\"7 (rare)\"}", 7d, "7 (rare)")]
    [InlineData("{\"reading\":\"たべる\",\"frequency\":9}", 9d, null)]
    public void Frequencies_are_read_in_each_way_yomitan_writes_them(string json, double value, string? display)
    {
        var frequency = DictionaryLookup.ReadFrequency(json, "たべる")!;

        Assert.Equal((value, display), (frequency.Value, frequency.DisplayValue));
    }

    [Theory]
    [InlineData("{\"reading\":\"くう\",\"frequency\":9}")]
    [InlineData("{\"value\":\"many\"}")]
    [InlineData("null")]
    public void A_frequency_for_another_reading_or_of_no_number_is_none(string json)
    {
        Assert.Null(DictionaryLookup.ReadFrequency(json, "たべる"));
    }

    [Fact]
    public void A_lookup_is_quick_enough_to_follow_a_tap()
    {
        var words = Open(Words());
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            DictionaryLookup.Find("食べさせられなかったんですけど本当に", [words]);
        }

        Assert.True(started.ElapsedMilliseconds < 20 * 200, $"{started.ElapsedMilliseconds} ms for 20 lookups");
    }
}
