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
        Assert.Equal(JsonSerializer.Serialize(Structured), reader.Glossaries([eat])[eat.Id]);

        // A word without a reading is read as it is written.
        var rice = Assert.Single(reader.FindTerms(["ごはん"]));
        Assert.Equal("ごはん", rice.Reading);
        Assert.Equal("[\"rice\",\"meal\"]", reader.Glossaries([rice])[rice.Id]);

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
        Assert.Equal(long_, DictionaryDatabase.Unpack(packedLong));
        Assert.Equal(short_, DictionaryDatabase.Unpack(packedShort));
        Assert.Empty(DictionaryDatabase.Unpack([]));
    }

    [Theory]
    [InlineData(new byte[] { 1, 200, 0, 0, 0, 1, 2, 3 })]
    [InlineData(new byte[] { 1, 255, 255, 255, 255, 1, 2, 3 })]
    [InlineData(new byte[] { 1, 0, 0 })]
    [InlineData(new byte[] { 7, 1, 2, 3, 4, 5, 6 })]
    public void Damaged_definitions_are_refused(byte[] packed)
    {
        Assert.Throws<InvalidDataException>(() => DictionaryDatabase.Unpack(packed));
    }

    [Fact]
    public void A_dictionary_of_format_1_lists_its_definitions_after_the_score()
    {
        var path = Import(new YomitanZip { Format = 1, Terms = { new object?[] { "犬", "いぬ", "n", string.Empty, 1, "dog", "hound" } } });

        var reader = DictionaryReader.TryOpen(path)!;
        var dog = Assert.Single(reader.FindTerms(["犬"]));
        Assert.Equal("[\"dog\",\"hound\"]", reader.Glossaries([dog])[dog.Id]);
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

    [Fact]
    public void Banks_read_side_by_side_go_into_the_file_in_the_order_of_their_numbers()
    {
        // 150 banks of 2 words: far more than there are threads, and bank 10 comes after bank 9.
        var zip = new YomitanZip();
        for (var i = 0; i < 300; i++)
        {
            zip.Terms.Add(YomitanZip.Term("w" + i, "r" + i, string.Empty, ["definition " + i, new string((char)('a' + (i % 26)), 700)]));
            zip.Metas.Add(["w" + i, "freq", i]);
        }

        var path = Path.Combine(_folder, "many.db");
        var progress = new System.Collections.Generic.List<double>();

        var info = YomitanArchive.Import(zip.Write(_folder), path, "many", new Progress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal((300L, 300L, 0L), (info.Terms, info.Frequencies, info.Skipped));
        Assert.Equal(progress.Order(), progress);
        Assert.Equal(301, progress.Count);
        var reader = DictionaryReader.TryOpen(path)!;
        var words = reader.FindTerms(Enumerable.Range(0, 300).Select(i => "w" + i).ToList()).OrderBy(word => word.Id).ToList();
        Assert.Equal(Enumerable.Range(0, 300).Select(i => ((long)i + 1, "w" + i, "r" + i)), words.Select(word => (word.Id, word.Expression, word.Reading)));
        var definitions = reader.Glossaries(words);
        Assert.All(Enumerable.Range(0, 300), i => Assert.Equal(JsonSerializer.Serialize(new[] { "definition " + i, new string((char)('a' + (i % 26)), 700) }), definitions[i + 1]));

        // Each bank ends its own block, so there are as many blocks as banks, numbered in their order.
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*), min(id), max(id), (SELECT group_concat(data, ',') FROM (SELECT data FROM metas ORDER BY rowid)) FROM blocks";
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal((150L, 1L, 150L), (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2)));
        Assert.Equal(string.Join(',', Enumerable.Range(0, 300)), row.GetString(3));
        Assert.Equal(words.Select(word => word.Block).Order(), words.Select(word => word.Block));
    }

    [Fact]
    public void A_word_whose_definitions_are_larger_than_a_block_is_read_whole()
    {
        var large = Enumerable.Range(0, 9000).Select(i => "meaning number " + i).ToArray<object>();
        var path = Import(new YomitanZip
        {
            Terms =
            {
                YomitanZip.Term("小", "しょう", string.Empty, ["small"]),
                YomitanZip.Term("大", "だい", string.Empty, large),
                YomitanZip.Term("中", "ちゅう", string.Empty, ["middle"]),
            },
        });

        var reader = DictionaryReader.TryOpen(path)!;
        var words = reader.FindTerms(["小", "大", "中"]).OrderBy(word => word.Id).ToList();
        var definitions = reader.Glossaries(words);

        Assert.True(words[1].Length > DictionaryDatabase.BlockBytes);
        Assert.Equal(["[\"small\"]", JsonSerializer.Serialize(large), "[\"middle\"]"], words.Select(word => definitions[word.Id]));
    }

    [Fact]
    public void Pictures_go_in_whatever_their_order_in_the_zip_and_a_later_one_of_the_same_path_wins()
    {
        var zip = new YomitanZip { Terms = { YomitanZip.Term("猫", "ねこ", string.Empty, ["cat"]) } };
        var random = new Random(5);
        var pictures = Enumerable.Range(0, 400).Select(i => (Path: $"img/{random.Next(100000):D5}-{i}.png", Data: new byte[] { (byte)i, (byte)(i >> 8), 1, 2, 3 })).ToList();
        foreach (var (name, data) in pictures)
        {
            zip.Files[name] = data;
        }

        zip.Files["IMG/Photo.JPG"] = [9, 9];
        zip.Files["img/empty.gif"] = [];
        var path = Path.Combine(_folder, "pictures.db");

        var info = YomitanArchive.Import(zip.Write(_folder), path, "pictures", null, TestContext.Current.CancellationToken);

        Assert.Equal(402, info.Media);
        var reader = DictionaryReader.TryOpen(path)!;
        Assert.All(pictures, picture => Assert.Equal(picture.Data, reader.Media(picture.Path)));
        Assert.Equal([9, 9], reader.Media("IMG/Photo.JPG"));
        Assert.Empty(reader.Media("img/empty.gif")!);
    }

    [Theory]
    [InlineData("term_bank_3.json", "not json", "term_bank_3.json in that dictionary is not JSON.")]
    [InlineData("term_bank_3.json", "{}", "term_bank_3.json in that dictionary is not a list of rows.")]
    [InlineData("term_meta_bank_2.json", "[[\"猫\",\"freq\",1]", "term_meta_bank_2.json in that dictionary is not JSON.")]
    [InlineData("tag_bank_1.json", "[[\"n\",", "tag_bank_1.json in that dictionary is not JSON.")]
    public void A_bank_that_cannot_be_read_ends_the_import_by_its_name_and_leaves_no_file(string bank, string json, string message)
    {
        var path = Path.Combine(_folder, "broken.db");
        var zip = new YomitanZip { Files = { [bank] = Encoding.UTF8.GetBytes(json) } };
        for (var i = 0; i < 40; i++)
        {
            zip.Terms.Add(YomitanZip.Term("w" + i, "r" + i, string.Empty, ["definition " + i]));
        }

        var error = Assert.Throws<InvalidDataException>(() => YomitanArchive.Import(zip.Write(_folder), path, "broken", null, TestContext.Current.CancellationToken));

        Assert.Equal(message, error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_file_that_unpacks_to_more_than_the_zip_says_is_refused()
    {
        // The limits on a bank's size are checked against what the zip says of it, so the zip must not lie.
        var zip = new YomitanZip { Terms = { YomitanZip.Term("猫", "ねこ", string.Empty, [new string('c', 5000)]) } }.Write(_folder);
        var bytes = File.ReadAllBytes(zip);
        var name = Encoding.ASCII.GetBytes("term_bank_1.json");
        var patched = 0;
        for (var at = 0; at + 46 + name.Length <= bytes.Length; at++)
        {
            // The central directory's entry of the bank: its unpacked size is at 24.
            if (bytes.AsSpan(at, 4).SequenceEqual(new byte[] { 0x50, 0x4B, 0x01, 0x02 }) && bytes.AsSpan(at + 46, name.Length).SequenceEqual(name))
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(at + 24, 4), 100);
                patched++;
            }
        }

        Assert.Equal(1, patched);
        File.WriteAllBytes(zip, bytes);
        var path = Path.Combine(_folder, "lies.db");

        var error = Assert.Throws<InvalidDataException>(() => YomitanArchive.Import(zip, path, "lies", null, TestContext.Current.CancellationToken));

        Assert.StartsWith("term_bank_1.json in that dictionary ", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_dictionary_file_written_before_definitions_were_kept_in_blocks_is_still_read()
    {
        // As 2.0 wrote it: each word's definitions in its own row, short ones plain and long ones compressed.
        var path = Path.Combine(_folder, "first.db");
        var longDefinitions = JsonSerializer.Serialize(Structured);
        var compressed = new byte[5 + System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(longDefinitions.Length)];
        compressed[0] = 1;
        BitConverter.TryWriteBytes(compressed.AsSpan(1), Encoding.UTF8.GetByteCount(longDefinitions));
        Assert.True(System.IO.Compression.BrotliEncoder.TryCompress(Encoding.UTF8.GetBytes(longDefinitions), compressed.AsSpan(5), out var written, quality: 5, window: 22));
        using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE info (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
                CREATE TABLE terms (
                    expression TEXT NOT NULL, reading TEXT NOT NULL, definition_tags TEXT NOT NULL, rules TEXT NOT NULL,
                    score INTEGER NOT NULL, glossary BLOB NOT NULL, sequence INTEGER NOT NULL, term_tags TEXT NOT NULL);
                CREATE TABLE metas (expression TEXT NOT NULL, mode TEXT NOT NULL, data TEXT NOT NULL);
                CREATE TABLE tags (name TEXT PRIMARY KEY, category TEXT NOT NULL, sort INTEGER NOT NULL, notes TEXT NOT NULL, score INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TABLE media (path TEXT PRIMARY KEY, data BLOB NOT NULL) WITHOUT ROWID;
                INSERT INTO info VALUES ('schema', '1'), ('info', $info), ('styles', 'b { color: red; }');
                INSERT INTO terms VALUES ('食べる', 'たべる', 'v1', 'v1', 10, $long, 1, 'P'), ('食', 'しょく', 'n', '', 1, $short, 2, '');
                INSERT INTO metas VALUES ('食べる', 'freq', '512');
                INSERT INTO tags VALUES ('v1', 'partOfSpeech', 0, 'Ichidan verb', 0);
                INSERT INTO media VALUES ('pictures/bowl.png', x'89504E47');
                CREATE INDEX terms_expression ON terms (expression);
                CREATE INDEX terms_reading ON terms (reading);
                CREATE INDEX metas_expression ON metas (expression);
                """;
            command.Parameters.AddWithValue("$info", JsonSerializer.Serialize(new DictionaryInfo { Id = "first", Title = "First", Revision = "1", Format = 3, Terms = 2, Frequencies = 1 }, DictionaryJsonContext.Default.DictionaryInfo));
            command.Parameters.AddWithValue("$long", compressed[..(written + 5)]);
            command.Parameters.AddWithValue("$short", new byte[] { 0 }.Concat(Encoding.UTF8.GetBytes("[\"food\"]")).ToArray());
            command.ExecuteNonQuery();
        }

        var reader = DictionaryReader.TryOpen(path)!;

        Assert.Equal(("First", 2L, "b { color: red; }"), (reader.Info.Title, reader.Info.Terms, reader.Styles));
        var words = reader.FindTerms(["食べる", "しょく"]).OrderBy(word => word.Id).ToList();
        Assert.Equal([("食べる", "たべる", "P"), ("食", "しょく", string.Empty)], words.Select(word => (word.Expression, word.Reading, word.TermTags)));
        var definitions = reader.Glossaries(words);
        Assert.Equal([longDefinitions, "[\"food\"]"], words.Select(word => definitions[word.Id]));
        Assert.Equal("512", Assert.Single(reader.FindMetas(["食べる"])).Data);
        Assert.Equal("Ichidan verb", reader.Tags["v1"].Notes);
        Assert.Equal([137, 80, 78, 71], reader.Media("pictures/bowl.png"));

        var found = DictionaryLookup.Find("食べた", [reader]).Results[0];
        Assert.Equal(("食べる", longDefinitions), (found.Term.Expression, found.Term.Glossaries[0].Glossary));
    }

    [Fact]
    public void A_dictionary_file_of_a_later_schema_is_not_opened()
    {
        var path = Import(new YomitanZip { Terms = { YomitanZip.Term("猫", "ねこ", string.Empty, ["cat"]) } }, "later");
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE info SET value = '3' WHERE key = 'schema'";
            command.ExecuteNonQuery();
        }

        Assert.Null(DictionaryReader.TryOpen(path));
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
