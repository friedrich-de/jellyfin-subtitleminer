using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// What a dictionary says of itself (its <c>index.json</c>), and what the import found in it.
/// </summary>
internal sealed class DictionaryInfo
{
    /// <summary>Gets or sets the name the dictionary goes by in the plugin's folder.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the dictionary's title, as results name it.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the dictionary's revision.</summary>
    public string Revision { get; set; } = string.Empty;

    /// <summary>Gets or sets the version of Yomitan's format it was written in.</summary>
    public int Format { get; set; }

    /// <summary>Gets or sets who made it.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets its web site.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets what it says of itself.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets its licence and sources.</summary>
    public string? Attribution { get; set; }

    /// <summary>Gets or sets whether its frequencies are ranks ("rank-based") or counts ("occurrence-based").</summary>
    public string? FrequencyMode { get; set; }

    /// <summary>Gets or sets the language of its words.</summary>
    public string? SourceLanguage { get; set; }

    /// <summary>Gets or sets the language of its definitions.</summary>
    public string? TargetLanguage { get; set; }

    /// <summary>Gets or sets where a newer revision is announced, for a dictionary that can be updated.</summary>
    public string? IndexUrl { get; set; }

    /// <summary>Gets or sets where it is downloaded.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Gets or sets the number of words.</summary>
    public long Terms { get; set; }

    /// <summary>Gets or sets the number of frequencies.</summary>
    public long Frequencies { get; set; }

    /// <summary>Gets or sets the number of pitch accents and pronunciations.</summary>
    public long Pitches { get; set; }

    /// <summary>Gets or sets the number of pictures.</summary>
    public long Media { get; set; }

    /// <summary>Gets or sets the number of rows the import could not read and left out.</summary>
    public long Skipped { get; set; }

    /// <summary>Gets or sets when it was imported.</summary>
    public DateTimeOffset Imported { get; set; }
}

/// <summary>A word as a dictionary has it.</summary>
/// <param name="Id">Its row.</param>
/// <param name="Expression">How it is written.</param>
/// <param name="Reading">How it is read; the written form when the dictionary gives none.</param>
/// <param name="DefinitionTags">The tags of its definitions, space-separated.</param>
/// <param name="Rules">Its parts of speech for deinflection ("v1", "v5", "adj-i"), space-separated.</param>
/// <param name="Score">How the dictionary ranks it; higher first.</param>
/// <param name="Sequence">The entry it belongs to, in a dictionary that groups its rows.</param>
/// <param name="TermTags">The tags of the word itself, space-separated.</param>
/// <param name="Block">The block its definitions are in; 0 in a file of the first schema, which keeps them in the row.</param>
/// <param name="Start">Where its definitions start in that block.</param>
/// <param name="Length">How many bytes they are.</param>
internal sealed record TermRow(long Id, string Expression, string Reading, string DefinitionTags, string Rules, long Score, long Sequence, string TermTags, long Block = 0, int Start = 0, int Length = 0);

/// <summary>A frequency, pitch accent or pronunciation of a word.</summary>
/// <param name="Expression">The word.</param>
/// <param name="Mode">"freq", "pitch" or "ipa".</param>
/// <param name="Data">What it says, as JSON.</param>
internal sealed record MetaRow(string Expression, string Mode, string Data);

/// <summary>A tag as the dictionary's tag bank explains it.</summary>
/// <param name="Name">Its name, as entries use it.</param>
/// <param name="Category">Its kind, which gives it its colour.</param>
/// <param name="Order">Where it sorts.</param>
/// <param name="Notes">What it stands for.</param>
/// <param name="Score">Its score.</param>
internal sealed record TagRow(string Name, string Category, long Order, string Notes, long Score);

/// <summary>
/// One dictionary in one SQLite file of its own, so that removing it is removing the file and an import that
/// fails leaves nothing behind. Words are found by how they are written and how they are read. Their definitions
/// are kept in blocks of a few dozen of them, each block compressed as one: definitions of one dictionary are
/// much alike, so a block packs to a fraction of what each definition packs to alone, in a fraction of the time.
/// </summary>
internal static class DictionaryDatabase
{
    /// <summary>The schema new files are written in. A file with a schema this version does not know is read as no dictionary.</summary>
    internal const int SchemaVersion = 2;

    /// <summary>The schema of files written before 2.1: each word's definitions compressed in its own row.</summary>
    internal const int FirstSchemaVersion = 1;

    /// <summary>
    /// Definitions are gathered into blocks of about this many bytes. Larger blocks pack better and import faster,
    /// but a lookup unpacks each block it reads a definition from: at this size that takes about 0.05 ms.
    /// </summary>
    internal const int BlockBytes = 32 * 1024;

    private const byte Plain = 0;
    private const byte Brotli = 1;

    /// <summary>Less than this is kept as it is: compressing it saves nothing.</summary>
    private const int CompressFrom = 96;

    /// <summary>
    /// Keeps definitions: as they are when short or when compressing gains nothing, else compressed with Brotli
    /// after their length.
    /// </summary>
    /// <param name="utf8">The definitions as UTF-8 JSON.</param>
    /// <returns>What is stored.</returns>
    internal static byte[] Pack(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= CompressFrom)
        {
            // Quality 3 is where the curve turns for blocks of definitions: 4 and 5 make Jitendex's blocks 4 and
            // 9 percent smaller and its import a tenth and a third longer, 1 and 2 make them a half and a seventh
            // larger for a tenth less time at most. The window only has to span the block.
            var window = Math.Clamp(BitOperations.Log2((uint)utf8.Length) + 1, 16, 22);
            var room = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(utf8.Length));
            try
            {
                if (BrotliEncoder.TryCompress(utf8, room, out var written, quality: 3, window: window) && written + 5 < utf8.Length + 1)
                {
                    var packed = new byte[written + 5];
                    packed[0] = Brotli;
                    BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(1), utf8.Length);
                    room.AsSpan(0, written).CopyTo(packed.AsSpan(5));
                    return packed;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(room);
            }
        }

        var plain = new byte[utf8.Length + 1];
        plain[0] = Plain;
        utf8.CopyTo(plain.AsSpan(1));
        return plain;
    }

    /// <summary>Reads back what <see cref="Pack"/> kept.</summary>
    /// <param name="packed">What is stored.</param>
    /// <returns>The definitions as UTF-8 JSON.</returns>
    internal static byte[] Unpack(ReadOnlySpan<byte> packed)
    {
        if (packed.Length == 0)
        {
            return [];
        }

        if (packed[0] == Plain)
        {
            return packed[1..].ToArray();
        }

        var length = packed.Length >= 5 ? BinaryPrimitives.ReadInt32LittleEndian(packed[1..]) : -1;
        if (packed[0] != Brotli || length < 0 || length > YomitanArchive.MaxBankBytes)
        {
            throw new InvalidDataException("A definition in the dictionary is damaged.");
        }

        var utf8 = new byte[length];
        if (!BrotliDecoder.TryDecompress(packed[5..], utf8, out var written) || written != length)
        {
            throw new InvalidDataException("A definition in the dictionary is damaged.");
        }

        return utf8;
    }

    internal static SqliteConnection Open(string path, bool write)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = !write,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Writes a new dictionary file. Nothing is in it for anyone else until <see cref="Finish"/> has run.
/// Rows go in through SQLite's own calls with their text as the UTF-8 the bank had it in: a large dictionary has
/// over a million rows, and making a string and a command of each takes three times as long as the insert.
/// </summary>
internal sealed class DictionaryWriter : IDisposable
{
    private static readonly byte[] Frequency = "freq"u8.ToArray();
    private static readonly byte[] Pitch = "pitch"u8.ToArray();
    private static readonly byte[] Pronunciation = "ipa"u8.ToArray();

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly sqlite3 _database;
    private readonly sqlite3_stmt _term;
    private readonly sqlite3_stmt _block;
    private readonly sqlite3_stmt _meta;
    private readonly sqlite3_stmt _media;
    private readonly SqliteCommand _tag;
    private long _blocks;
    private bool _finished;

    private DictionaryWriter(SqliteConnection connection)
    {
        _connection = connection;
        _database = connection.Handle ?? throw new InvalidOperationException("The dictionary file is not open.");

        // The file is new and is thrown away if the import fails, so it needs no journal.
        DictionaryDatabase.Execute(connection, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA page_size = 8192; PRAGMA locking_mode = EXCLUSIVE;");
        DictionaryDatabase.Execute(connection, """
            CREATE TABLE info (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE terms (
                expression TEXT NOT NULL, reading TEXT NOT NULL, definition_tags TEXT NOT NULL, rules TEXT NOT NULL,
                score INTEGER NOT NULL, sequence INTEGER NOT NULL, term_tags TEXT NOT NULL,
                block INTEGER NOT NULL, start INTEGER NOT NULL, length INTEGER NOT NULL);
            CREATE TABLE blocks (id INTEGER PRIMARY KEY, data BLOB NOT NULL);
            CREATE TABLE metas (expression TEXT NOT NULL, mode TEXT NOT NULL, data TEXT NOT NULL);
            CREATE TABLE tags (name TEXT PRIMARY KEY, category TEXT NOT NULL, sort INTEGER NOT NULL, notes TEXT NOT NULL, score INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE media (path TEXT PRIMARY KEY, data BLOB NOT NULL) WITHOUT ROWID;
            """);
        _transaction = connection.BeginTransaction();
        _term = Prepare("INSERT INTO terms VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)");
        _block = Prepare("INSERT INTO blocks VALUES (?1, ?2)");
        _meta = Prepare("INSERT INTO metas VALUES (?1, ?2, ?3)");
        _media = Prepare("INSERT OR REPLACE INTO media VALUES (?1, ?2)");
        _tag = connection.CreateCommand();
        _tag.Transaction = _transaction;
        _tag.CommandText = "INSERT OR REPLACE INTO tags VALUES ($n, $c, $o, $t, $s)";
        foreach (var name in new[] { "$n", "$c", "$o", "$t", "$s" })
        {
            _tag.Parameters.Add(new SqliteParameter { ParameterName = name });
        }
    }

    /// <summary>Starts a dictionary file at <paramref name="path"/>, which must not exist.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The writer.</returns>
    public static DictionaryWriter Create(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException("A dictionary file is already there: " + path);
        }

        var connection = DictionaryDatabase.Open(path, write: true);
        try
        {
            return new DictionaryWriter(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Adds the words of a bank, with the blocks their definitions are in, and gives the bank's memory back.</summary>
    /// <param name="bank">The words, as <see cref="YomitanBank.ReadTerms"/> read them.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void AddTerms(TermBank bank)
    {
        var first = _blocks + 1;
        foreach (var block in bank.Blocks)
        {
            _blocks++;
            Check(raw.sqlite3_bind_int64(_block, 1, _blocks));
            Check(raw.sqlite3_bind_blob(_block, 2, block));
            Step(_block);
        }

        var rows = bank.Rows.Span;
        while (rows.Length > 0)
        {
            for (var column = 1; column <= 4; column++)
            {
                Check(raw.sqlite3_bind_text(_term, column, YomitanBank.Take(ref rows)));
            }

            Check(raw.sqlite3_bind_text(_term, 7, YomitanBank.Take(ref rows)));
            Check(raw.sqlite3_bind_int64(_term, 5, BinaryPrimitives.ReadInt64LittleEndian(rows)));
            Check(raw.sqlite3_bind_int64(_term, 6, BinaryPrimitives.ReadInt64LittleEndian(rows[8..])));
            Check(raw.sqlite3_bind_int64(_term, 8, first + BinaryPrimitives.ReadInt32LittleEndian(rows[16..])));
            Check(raw.sqlite3_bind_int(_term, 9, BinaryPrimitives.ReadInt32LittleEndian(rows[20..])));
            Check(raw.sqlite3_bind_int(_term, 10, BinaryPrimitives.ReadInt32LittleEndian(rows[24..])));
            rows = rows[28..];
            Step(_term);
        }

        bank.Release();
    }

    /// <summary>Adds the frequencies, pitch accents and pronunciations of a bank, and gives the bank's memory back.</summary>
    /// <param name="bank">The rows, as <see cref="YomitanBank.ReadMetas"/> read them.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void AddMetas(MetaBank bank)
    {
        var rows = bank.Rows.Span;
        while (rows.Length > 0)
        {
            Check(raw.sqlite3_bind_text(_meta, 1, YomitanBank.Take(ref rows)));
            var mode = rows[0] switch { YomitanBank.FrequencyMode => Frequency, YomitanBank.PitchMode => Pitch, _ => Pronunciation };
            rows = rows[1..];
            Check(raw.sqlite3_bind_text(_meta, 2, mode));
            Check(raw.sqlite3_bind_text(_meta, 3, YomitanBank.Take(ref rows)));
            Step(_meta);
        }

        bank.Release();
    }

    /// <summary>Adds a tag; a later one of the same name wins.</summary>
    /// <param name="tag">The tag.</param>
    public void AddTag(TagRow tag)
    {
        var p = _tag.Parameters;
        p[0].Value = tag.Name;
        p[1].Value = tag.Category;
        p[2].Value = tag.Order;
        p[3].Value = tag.Notes;
        p[4].Value = tag.Score;
        _tag.ExecuteNonQuery();
    }

    /// <summary>Adds a picture; a later one at the same path wins.</summary>
    /// <param name="path">Its path in the dictionary.</param>
    /// <param name="data">The picture.</param>
    public void AddMedia(string path, ReadOnlySpan<byte> data)
    {
        Check(raw.sqlite3_bind_text(_media, 1, path));
        Check(raw.sqlite3_bind_blob(_media, 2, data));
        Step(_media);
    }

    /// <summary>Indexes what was added, writes what the dictionary says of itself and closes the file.</summary>
    /// <param name="info">The dictionary's information.</param>
    /// <param name="styles">Its stylesheet, if it has one.</param>
    public void Finish(DictionaryInfo info, string? styles)
    {
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = _transaction;
            command.CommandText = "INSERT INTO info VALUES ('schema', $v), ('info', $i), ('styles', $s)";
            command.Parameters.AddWithValue("$v", DictionaryDatabase.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$i", JsonSerializer.Serialize(info, DictionaryJsonContext.Default.DictionaryInfo));
            command.Parameters.AddWithValue("$s", styles ?? string.Empty);
            command.ExecuteNonQuery();
        }

        _transaction.Commit();

        // The indexes are built once all rows are in: sorting them once is several times faster than keeping
        // two indexes in order through a million inserts. SQLite sorts on several threads when it may.
        DictionaryDatabase.Execute(_connection, "PRAGMA threads = " + Math.Min(Environment.ProcessorCount, YomitanArchive.MaxThreads).ToString(System.Globalization.CultureInfo.InvariantCulture));
        DictionaryDatabase.Execute(_connection, """
            CREATE INDEX terms_expression ON terms (expression);
            CREATE INDEX terms_reading ON terms (reading);
            CREATE INDEX metas_expression ON metas (expression);
            ANALYZE;
            """);
        _finished = true;
        Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _term.Dispose();
        _block.Dispose();
        _meta.Dispose();
        _media.Dispose();
        _tag.Dispose();
        if (!_finished)
        {
            _transaction.Dispose();
        }

        _connection.Dispose();
    }

    private sqlite3_stmt Prepare(string sql)
    {
        Check(raw.sqlite3_prepare_v2(_database, sql, out var statement));
        return statement;
    }

    private void Step(sqlite3_stmt statement)
    {
        var result = raw.sqlite3_step(statement);
        if (result != raw.SQLITE_DONE)
        {
            raw.sqlite3_reset(statement);
            Check(result == raw.SQLITE_OK ? raw.SQLITE_ERROR : result);
        }

        raw.sqlite3_reset(statement);
    }

    private void Check(int result)
    {
        if (result != raw.SQLITE_OK)
        {
            throw new SqliteException("The dictionary file could not be written: " + raw.sqlite3_errmsg(_database).utf8_to_string(), result);
        }
    }
}

/// <summary>
/// Reads one dictionary file. Each call takes a connection from the pool, so lookups may run side by side.
/// </summary>
internal sealed class DictionaryReader
{
    /// <summary>At most this many texts are asked for in one query.</summary>
    private const int KeysPerQuery = 500;

    private readonly Lazy<IReadOnlyDictionary<string, TagRow>> _tags;

    /// <summary>Whether definitions are in blocks; in a file of the first schema each is in its word's row.</summary>
    private readonly bool _blocks;

    private DictionaryReader(string path, DictionaryInfo info, string styles, bool blocks)
    {
        Path = path;
        Info = info;
        Styles = styles;
        _blocks = blocks;
        _tags = new Lazy<IReadOnlyDictionary<string, TagRow>>(ReadTags);
    }

    /// <summary>Gets the file.</summary>
    public string Path { get; }

    /// <summary>Gets what the dictionary says of itself.</summary>
    public DictionaryInfo Info { get; }

    /// <summary>Gets its stylesheet, or "" when it has none.</summary>
    public string Styles { get; }

    /// <summary>Gets its tags by name.</summary>
    public IReadOnlyDictionary<string, TagRow> Tags => _tags.Value;

    /// <summary>Opens a dictionary file, or gives <c>null</c> when it is not one this version can read.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The reader.</returns>
    public static DictionaryReader? TryOpen(string path)
    {
        try
        {
            using var connection = DictionaryDatabase.Open(path, write: false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM info";
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    values[reader.GetString(0)] = reader.GetString(1);
                }
            }

            if (!int.TryParse(values.GetValueOrDefault("schema"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var schema)
                || schema is not (DictionaryDatabase.SchemaVersion or DictionaryDatabase.FirstSchemaVersion)
                || !values.TryGetValue("info", out var json)
                || JsonSerializer.Deserialize(json, DictionaryJsonContext.Default.DictionaryInfo) is not { } info)
            {
                return null;
            }

            return new DictionaryReader(path, info, values.GetValueOrDefault("styles") ?? string.Empty, blocks: schema == DictionaryDatabase.SchemaVersion);
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The words written or read as any of <paramref name="keys"/>, without their definitions.</summary>
    /// <param name="keys">The texts.</param>
    /// <returns>The words, each once.</returns>
    public List<TermRow> FindTerms(IReadOnlyCollection<string> keys)
    {
        var found = new Dictionary<long, TermRow>();
        var columns = "rowid, expression, reading, definition_tags, rules, score, sequence, term_tags" + (_blocks ? ", block, start, length" : ", 0, 0, 0");
        using var connection = DictionaryDatabase.Open(Path, write: false);
        foreach (var chunk in keys.Chunk(KeysPerQuery))
        {
            using var command = connection.CreateCommand();
            var names = Parameters(command, chunk);
            command.CommandText =
                "SELECT " + columns + " FROM terms WHERE expression IN (" + names + ")"
                + " UNION SELECT " + columns + " FROM terms WHERE reading IN (" + names + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new TermRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetInt64(8),
                    reader.GetInt32(9),
                    reader.GetInt32(10));
                found[row.Id] = row;
            }
        }

        return found.Values.ToList();
    }

    /// <summary>The definitions of some words.</summary>
    /// <param name="rows">The words, as <see cref="FindTerms"/> gave them.</param>
    /// <returns>Each word's definitions as JSON, by its row.</returns>
    public Dictionary<long, string> Glossaries(IReadOnlyCollection<TermRow> rows)
    {
        var found = new Dictionary<long, string>();
        using var connection = DictionaryDatabase.Open(Path, write: false);
        if (!_blocks)
        {
            foreach (var chunk in rows.Select(row => row.Id).Chunk(KeysPerQuery))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT rowid, glossary FROM terms WHERE rowid IN (" + string.Join(',', chunk) + ")";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var utf8 = DictionaryDatabase.Unpack((byte[])reader.GetValue(1));
                    found[reader.GetInt64(0)] = utf8.Length == 0 ? "[]" : Encoding.UTF8.GetString(utf8);
                }
            }

            return found;
        }

        // Words that stand together in a dictionary share a block, so a lookup unpacks few of them.
        var byBlock = rows.ToLookup(row => row.Block);
        foreach (var chunk in byBlock.Select(group => group.Key).Chunk(KeysPerQuery))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, data FROM blocks WHERE id IN (" + string.Join(',', chunk) + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var block = DictionaryDatabase.Unpack((byte[])reader.GetValue(1));
                foreach (var row in byBlock[reader.GetInt64(0)])
                {
                    if (row.Start < 0 || row.Length < 0 || (long)row.Start + row.Length > block.Length)
                    {
                        throw new InvalidDataException("A definition in the dictionary is damaged.");
                    }

                    found[row.Id] = Encoding.UTF8.GetString(block, row.Start, row.Length);
                }
            }
        }

        return found;
    }

    /// <summary>The frequencies, pitch accents and pronunciations of words.</summary>
    /// <param name="expressions">The words, as written.</param>
    /// <returns>What the dictionary has of them.</returns>
    public List<MetaRow> FindMetas(IReadOnlyCollection<string> expressions)
    {
        var found = new List<MetaRow>();
        using var connection = DictionaryDatabase.Open(Path, write: false);
        foreach (var chunk in expressions.Chunk(KeysPerQuery))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT expression, mode, data FROM metas WHERE expression IN (" + Parameters(command, chunk) + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found.Add(new MetaRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return found;
    }

    /// <summary>A picture of the dictionary, or <c>null</c> when it has none at that path.</summary>
    /// <param name="path">Its path in the dictionary.</param>
    /// <returns>The picture.</returns>
    public byte[]? Media(string path)
    {
        using var connection = DictionaryDatabase.Open(Path, write: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM media WHERE path = $p";
        command.Parameters.AddWithValue("$p", path);
        return command.ExecuteScalar() as byte[];
    }

    private static string Parameters(SqliteCommand command, string[] values)
    {
        var names = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            var name = "$k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(name, values[i]);
            names.Append(i == 0 ? string.Empty : ",").Append(name);
        }

        return names.ToString();
    }

    private Dictionary<string, TagRow> ReadTags()
    {
        var tags = new Dictionary<string, TagRow>(StringComparer.Ordinal);
        using var connection = DictionaryDatabase.Open(Path, write: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, category, sort, notes, score FROM tags";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tags[reader.GetString(0)] = new TagRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4));
        }

        return tags;
    }
}
