using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

public sealed class YomitanBankTests
{
    private sealed record Word(string Expression, string Reading, string DefinitionTags, string Rules, string TermTags, long Score, long Sequence, int Block, int Start, int Length, string Definitions);

    private sealed record Meta(string Expression, byte Mode, string Data);

    private static List<Word> Terms(string json, int format = 3) => Terms(Encoding.UTF8.GetBytes(json), format, out _);

    private static List<Word> Terms(byte[] json, int format, out TermBank bank)
    {
        bank = YomitanBank.ReadTerms(json, format, "term_bank_1.json");
        var blocks = bank.Blocks.Select(block => DictionaryDatabase.Unpack(block)).ToList();
        var words = new List<Word>();
        var rows = bank.Rows.Span;
        while (rows.Length > 0)
        {
            var texts = new string[5];
            for (var i = 0; i < texts.Length; i++)
            {
                texts[i] = Encoding.UTF8.GetString(YomitanBank.Take(ref rows));
            }

            var (score, sequence) = (BinaryPrimitives.ReadInt64LittleEndian(rows), BinaryPrimitives.ReadInt64LittleEndian(rows[8..]));
            var (block, start, length) = (BinaryPrimitives.ReadInt32LittleEndian(rows[16..]), BinaryPrimitives.ReadInt32LittleEndian(rows[20..]), BinaryPrimitives.ReadInt32LittleEndian(rows[24..]));
            rows = rows[28..];
            words.Add(new Word(texts[0], texts[1], texts[2], texts[3], texts[4], score, sequence, block, start, length, Encoding.UTF8.GetString(blocks[block], start, length)));
        }

        Assert.Equal(bank.Count, words.Count);
        return words;
    }

    private static List<Meta> Metas(string json, out MetaBank bank)
    {
        bank = YomitanBank.ReadMetas(Encoding.UTF8.GetBytes(json), "term_meta_bank_1.json");
        var metas = new List<Meta>();
        var rows = bank.Rows.Span;
        while (rows.Length > 0)
        {
            var expression = Encoding.UTF8.GetString(YomitanBank.Take(ref rows));
            var mode = rows[0];
            rows = rows[1..];
            metas.Add(new Meta(expression, mode, Encoding.UTF8.GetString(YomitanBank.Take(ref rows))));
        }

        return metas;
    }

    [Fact]
    public void A_row_is_read_with_its_tags_score_sequence_and_definitions()
    {
        var word = Assert.Single(Terms("""[["食べる","たべる","v1 vt","v1",100,["to eat",{"type":"text","text":"食う"}],7,"P news"]]"""));

        Assert.Equal(new Word("食べる", "たべる", "v1 vt", "v1", "P news", 100, 7, 0, 0, word.Length, """["to eat",{"type":"text","text":"食う"}]"""), word);
    }

    [Fact]
    public void Definitions_are_kept_as_the_bank_writes_them()
    {
        // Neither the escapes nor the spaces are touched: nothing reads the definitions on the way in.
        const string Written = """[ "caf\u00e9" , {"a" : [1, 2.50, true, null]} ]""";

        var word = Assert.Single(Terms("[[\"a\",\"b\",\"\",\"\",0," + Written + ",0,\"\"]]"));

        Assert.Equal(Written, word.Definitions);
    }

    [Fact]
    public void Escapes_in_a_word_are_undone()
    {
        var words = Terms("""[["\u98df\u3079\u308b","\u305f\u3079\u308b","\u540d",  "v1",0,["x"],0,""],["\ud83d\ude00\"\\\/","a\tb","","",0,["y"],0,""]]""");

        Assert.Equal(("食べる", "たべる", "名", "v1"), (words[0].Expression, words[0].Reading, words[0].DefinitionTags, words[0].Rules));
        Assert.Equal(("😀\"\\/", "a\tb"), (words[1].Expression, words[1].Reading));
    }

    [Fact]
    public void A_word_without_a_reading_is_read_as_it_is_written()
    {
        var words = Terms("""[["ごはん","","n","",3,["rice"],0,""],["\u3054\u306f\u3093","","","",0,["rice"],0,""]]""");

        Assert.All(words, word => Assert.Equal(("ごはん", "ごはん"), (word.Expression, word.Reading)));
        Assert.Equal("n", words[0].DefinitionTags);
    }

    [Fact]
    public void Tags_lose_the_white_space_around_them()
    {
        var word = Assert.Single(Terms("[[\"a\",\"b\",\"  n  \",\"\\u3000v5\\t\",0,[\"x\"],0,\" \\u3000 \"]]"));

        Assert.Equal(("n", "v5", string.Empty), (word.DefinitionTags, word.Rules, word.TermTags));
    }

    [Fact]
    public void Tags_that_are_no_text_are_read_as_none()
    {
        var word = Assert.Single(Terms("""[["a","b",null,5,0,["x"],"seven",["P"]]]"""));

        Assert.Equal((string.Empty, string.Empty, string.Empty, 0L), (word.DefinitionTags, word.Rules, word.TermTags, word.Sequence));
    }

    [Fact]
    public void A_row_may_end_after_its_definitions_or_go_on_past_its_tags()
    {
        var words = Terms("""[["a","b","","",1.6,["x"]],["c","d","","",-2.5,["y"],3.4,"t",{"more":[1,2]},"and more"]]""");

        Assert.Equal((2L, 0L, string.Empty), (words[0].Score, words[0].Sequence, words[0].TermTags));
        Assert.Equal((-2L, 3L, "t", "[\"y\"]"), (words[1].Score, words[1].Sequence, words[1].TermTags, words[1].Definitions));
    }

    [Fact]
    public void A_row_of_format_1_lists_its_definitions_after_the_score()
    {
        var words = Terms("""[["犬","いぬ","n","",1,"dog", "hound"],["猫","ねこ","n","",1],["鳥","とり","n","",1,{"a":1}]]""", format: 1);

        Assert.Equal(["[\"dog\", \"hound\"]", "[]", "[{\"a\":1}]"], words.Select(word => word.Definitions));
        Assert.All(words, word => Assert.Equal(string.Empty, word.TermTags));
    }

    [Fact]
    public void Rows_that_cannot_be_read_are_left_out_and_counted()
    {
        var longWord = new string('長', YomitanArchive.MaxKeyLength + 1);
        var json = "[" + string.Join(
            ',',
            "[\"猫\",\"ねこ\",\"\",\"\",0,[\"cat\"],0,\"\"]",
            "5",
            "{\"a\":[1,2]}",
            "\"text\"",
            "[]",
            "[\"猫\",\"ねこ\",\"\",\"\",0]",
            "[5,\"ねこ\",\"\",\"\",0,[\"not a word\"],0,\"\"]",
            "[\"\",\"ねこ\",\"\",\"\",0,[\"no word\"],0,\"\"]",
            "[\"猫\",null,\"\",\"\",0,[\"no reading\"],0,\"\"]",
            "[\"猫\",\"ねこ\",\"\",\"\",\"high\",[\"no score\"],0,\"\"]",
            "[\"猫\",\"ねこ\",\"\",\"\",1e300,[\"too high a score\"],0,\"\"]",
            "[\"猫\",\"ねこ\",\"\",\"\",0,\"not a list\",0,\"\"]",
            "[\"" + longWord + "\",\"ながい\",\"\",\"\",0,[\"too long\"],0,\"\"]",
            "[\"長い\",\"" + longWord + "\",\"\",\"\",0,[\"too long a reading\"],0,\"\"]",
            "[\"\\ud83d\",\"half a pair\",\"\",\"\",0,[\"no text\"],0,\"\"]",
            "[\"犬\",\"いぬ\",\"\",\"\",0,[\"dog\"],0,\"\"]") + "]";

        var words = Terms(Encoding.UTF8.GetBytes(json), 3, out var bank);

        Assert.Equal(["猫", "犬"], words.Select(word => word.Expression));
        Assert.Equal(["[\"cat\"]", "[\"dog\"]"], words.Select(word => word.Definitions));
        Assert.Equal((2, 14), (bank.Count, bank.Skipped));
    }

    [Fact]
    public void A_word_of_256_characters_is_kept_however_many_bytes_it_is()
    {
        var longest = new string('長', YomitanArchive.MaxKeyLength);

        var word = Assert.Single(Terms("[[\"" + longest + "\",\"" + longest + "\",\"\",\"\",0,[\"x\"],0,\"\"]]"));

        Assert.Equal((longest, longest), (word.Expression, word.Reading));
    }

    [Fact]
    public void Rows_with_the_definitions_of_the_row_before_share_them()
    {
        // One entry written three ways, as dictionaries have them, then another entry.
        var words = Terms("""[["見る","みる","","",0,["to see"],1,""],["観る","みる","","",0,["to see"],1,""],["視る","みる","","",0,["to see"],1,""],["聞く","きく","","",0,["to hear"],2,""],["見る","みる","","",0,["to see"],1,""]]""");

        Assert.Equal((words[0].Block, words[0].Start), (words[1].Block, words[1].Start));
        Assert.Equal((words[0].Block, words[0].Start), (words[2].Block, words[2].Start));
        Assert.Equal(words[0].Start + words[0].Length, words[3].Start);
        Assert.Equal(words[3].Start + words[3].Length, words[4].Start);
        Assert.Equal(["[\"to see\"]", "[\"to see\"]", "[\"to see\"]", "[\"to hear\"]", "[\"to see\"]"], words.Select(word => word.Definitions));
    }

    [Fact]
    public void Definitions_fill_blocks_of_a_fixed_size_and_a_larger_one_has_a_block_to_itself()
    {
        var rows = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            rows.Add($"[\"w{i}\",\"r{i}\",\"\",\"\",0,[\"{new string((char)('a' + (i % 26)), 1000)} {i}\"],0,\"\"]");
        }

        var huge = "[\"" + string.Concat(Enumerable.Range(0, 20000).Select(i => $"entry {i} ")) + "\"]";
        rows.Insert(100, "[\"huge\",\"huge\",\"\",\"\",0," + huge + ",0,\"\"]");

        var words = Terms(Encoding.UTF8.GetBytes("[" + string.Join(',', rows) + "]"), 3, out var bank);

        Assert.Equal(201, words.Count);
        Assert.Equal(huge, words[100].Definitions);
        Assert.Equal(0, words[100].Start);
        Assert.NotEqual(words[99].Block, words[100].Block);
        Assert.NotEqual(words[101].Block, words[100].Block);
        Assert.True(bank.Blocks.Count > 7, $"{bank.Blocks.Count} blocks");
        Assert.All(bank.Blocks.Where((_, index) => index != words[100].Block), block => Assert.True(DictionaryDatabase.Unpack(block).Length <= DictionaryDatabase.BlockBytes));

        // Blocks are in the bank's order, each word behind the one before it.
        Assert.Equal(words.Select(word => word.Block).Order(), words.Select(word => word.Block));
        Assert.True(bank.Blocks.Sum(block => block.Length) < 150_000, $"{bank.Blocks.Sum(block => block.Length)} bytes");
    }

    [Fact]
    public void A_bank_may_start_with_a_byte_order_mark_and_be_written_over_many_lines()
    {
        var json = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("\n[\n  [\n    \"猫\",\n    \"ねこ\",\n    \"n\",\n    \"\",\n    2,\n    [\n      \"cat\"\n    ],\n    0,\n    \"\"\n  ]\n]\n")).ToArray();

        var word = Assert.Single(Terms(json, 3, out _));

        Assert.Equal(("猫", "ねこ", "n", 2L, "[\n      \"cat\"\n    ]"), (word.Expression, word.Reading, word.DefinitionTags, word.Score, word.Definitions));
    }

    [Fact]
    public void Definitions_may_be_nested_deeply()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 500)) + "1" + string.Concat(Enumerable.Repeat("]", 500));

        var word = Assert.Single(Terms("[[\"a\",\"b\",\"\",\"\",0," + deep + ",0,\"\"]]"));

        Assert.Equal(deep, word.Definitions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[[\"a\",\"b\",\"\",\"\",0,[\"x\"],0,\"\"]")]
    [InlineData("[[\"a\",\"b\",\"\",\"\",0,[\"x\"],0,\"\"]] trailing")]
    [InlineData("[[\"a\",\"b\",\"\",\"\",0,[\"x\",],0,\"\"]]")]
    [InlineData("[[\"a\",\"b\",\"\",\"\",0,[\"x\"],0,\"\"],]")]
    public void A_bank_that_is_not_json_is_refused_by_its_name(string json)
    {
        var terms = Assert.Throws<InvalidDataException>(() => YomitanBank.ReadTerms(Encoding.UTF8.GetBytes(json), 3, "term_bank_7.json"));
        var metas = Assert.Throws<InvalidDataException>(() => YomitanBank.ReadMetas(Encoding.UTF8.GetBytes(json), "term_meta_bank_7.json"));

        Assert.Equal("term_bank_7.json in that dictionary is not JSON.", terms.Message);
        Assert.Equal("term_meta_bank_7.json in that dictionary is not JSON.", metas.Message);
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("5")]
    [InlineData("\"rows\"")]
    public void A_bank_that_is_not_a_list_is_refused_by_its_name(string json)
    {
        var terms = Assert.Throws<InvalidDataException>(() => YomitanBank.ReadTerms(Encoding.UTF8.GetBytes(json), 3, "term_bank_7.json"));
        var metas = Assert.Throws<InvalidDataException>(() => YomitanBank.ReadMetas(Encoding.UTF8.GetBytes(json), "term_meta_bank_7.json"));

        Assert.Equal("term_bank_7.json in that dictionary is not a list of rows.", terms.Message);
        Assert.Equal("term_meta_bank_7.json in that dictionary is not a list of rows.", metas.Message);
    }

    [Fact]
    public void An_empty_bank_has_no_words()
    {
        Assert.Empty(Terms(" [ ] "));
        Assert.Empty(Metas("[]", out _));
    }

    [Fact]
    public void Frequencies_pitches_and_pronunciations_are_read_with_what_they_say_as_written()
    {
        var metas = Metas("""[["食べる","freq",{"reading":"たべる","frequency":{"value":512,"displayValue":"512㋕"}}],["食べる","freq", 12 ],["食べる","freq","often"],["\u98df\u3079\u308b","pitch",{"reading":"たべる","pitches":[{"position":2}]}],["食べる","\u0069pa",{"reading":"たべる","transcriptions":[]}]]""", out var bank);

        Assert.Equal(
            [
                new Meta("食べる", YomitanBank.FrequencyMode, """{"reading":"たべる","frequency":{"value":512,"displayValue":"512㋕"}}"""),
                new Meta("食べる", YomitanBank.FrequencyMode, "12"),
                new Meta("食べる", YomitanBank.FrequencyMode, "\"often\""),
                new Meta("食べる", YomitanBank.PitchMode, """{"reading":"たべる","pitches":[{"position":2}]}"""),
                new Meta("食べる", YomitanBank.PronunciationMode, """{"reading":"たべる","transcriptions":[]}"""),
            ],
            metas);
        Assert.Equal((3, 2, 0), (bank.Frequencies, bank.Pitches, bank.Skipped));
    }

    [Fact]
    public void Meta_rows_that_cannot_be_read_are_left_out_and_counted()
    {
        var longWord = new string('長', YomitanArchive.MaxKeyLength + 1);

        var metas = Metas("[[\"猫\",\"sound\",1],[\"猫\",\"freq\",null],[\"猫\",\"freq\"],[\"\",\"freq\",1],[7,\"freq\",1],[\"猫\",5,1],[\"" + longWord + "\",\"freq\",1],7,[\"猫\",\"freq\",12,\"more\"]]", out var bank);

        Assert.Equal([new Meta("猫", YomitanBank.FrequencyMode, "12")], metas);
        Assert.Equal((1, 0, 8), (bank.Frequencies, bank.Pitches, bank.Skipped));
    }
}
