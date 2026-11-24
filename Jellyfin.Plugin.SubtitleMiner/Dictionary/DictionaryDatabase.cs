using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

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
internal sealed record TermRow(long Id, string Expression, string Reading, string DefinitionTags, string Rules, long Score, long Sequence, string TermTags);

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
/// fails leaves nothing behind. Words are found by how they are written and how they are read; their definitions
/// are kept compressed.
/// </summary>
internal static class DictionaryDatabase
{
    /// <summary>The schema; a file with another one is read as no dictionary.</summary>
    internal const int SchemaVersion = 1;

    private const byte Plain = 0;
    private const byte Brotli = 1;

    /// <summary>Definitions shorter than this are kept as they are: compressing them saves nothing.</summary>
    private const int CompressFrom = 96;

    /// <summary>
    /// Keeps a definition: as it is when short, else compressed with Brotli after its length.
    /// </summary>
    /// <param name="utf8">The definition as UTF-8 JSON.</param>
    /// <returns>What is stored.</returns>
    internal static byte[] Pack(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= CompressFrom)
        {
            var packed = new byte[5 + BrotliEncoder.GetMaxCompressedLength(utf8.Length)];
            if (BrotliEncoder.TryCompress(utf8, packed.AsSpan(5), out var written, quality: 5, window: 22) && written + 5 < utf8.Length + 1)
            {
                packed[0] = Brotli;
                BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(1), utf8.Length);
                return packed[..(written + 5)];
            }
        }

        var plain = new byte[utf8.Length + 1];
        plain[0] = Plain;
        utf8.CopyTo(plain.AsSpan(1));
        return plain;
    }

    /// <summary>Reads back what <see cref="Pack"/> kept.</summary>
    /// <param name="packed">What is stored.</param>
    /// <returns>The definition as JSON.</returns>
    internal static string Unpack(ReadOnlySpan<byte> packed)
    {
        if (packed.Length == 0)
        {
            return "[]";
        }

        if (packed[0] == Plain)
        {
            return Encoding.UTF8.GetString(packed[1..]);
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(packed[1..]);
        var utf8 = new byte[length];
        if (packed[0] != Brotli || !BrotliDecoder.TryDecompress(packed[5..], utf8, out var written) || written != length)
        {
            throw new InvalidDataException("A definition in the dictionary is damaged.");
        }

        return Encoding.UTF8.GetString(utf8);
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
/// </summary>
internal sealed class DictionaryWriter : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqliteCommand _term;
    private readonly SqliteCommand _meta;
    private readonly SqliteCommand _tag;
    private readonly SqliteCommand _media;
    private bool _finished;

    private DictionaryWriter(SqliteConnection connection)
    {
        _connection = connection;

        // The file is new and is thrown away if the import fails, so it needs no journal.
        DictionaryDatabase.Execute(connection, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA page_size = 8192;");
        DictionaryDatabase.Execute(connection, """
            CREATE TABLE info (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE terms (
                expression TEXT NOT NULL, reading TEXT NOT NULL, definition_tags TEXT NOT NULL, rules TEXT NOT NULL,
                score INTEGER NOT NULL, glossary BLOB NOT NULL, sequence INTEGER NOT NULL, term_tags TEXT NOT NULL);
            CREATE TABLE metas (expression TEXT NOT NULL, mode TEXT NOT NULL, data TEXT NOT NULL);
            CREATE TABLE tags (name TEXT PRIMARY KEY, category TEXT NOT NULL, sort INTEGER NOT NULL, notes TEXT NOT NULL, score INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE media (path TEXT PRIMARY KEY, data BLOB NOT NULL) WITHOUT ROWID;
            """);
        _transaction = connection.BeginTransaction();
        _term = Prepare("INSERT INTO terms VALUES ($e, $r, $d, $u, $s, $g, $q, $t)", "$e", "$r", "$d", "$u", "$s", "$g", "$q", "$t");
        _meta = Prepare("INSERT INTO metas VALUES ($e, $m, $d)", "$e", "$m", "$d");
        _tag = Prepare("INSERT OR REPLACE INTO tags VALUES ($n, $c, $o, $t, $s)", "$n", "$c", "$o", "$t", "$s");
        _media = Prepare("INSERT OR REPLACE INTO media VALUES ($p, $d)", "$p", "$d");
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

        return new DictionaryWriter(DictionaryDatabase.Open(path, write: true));
    }

    /// <summary>Adds a word.</summary>
    public void AddTerm(string expression, string reading, string definitionTags, string rules, long score, ReadOnlySpan<byte> glossary, long sequence, string termTags)
    {
        var p = _term.Parameters;
        p[0].Value = expression;
        p[1].Value = reading;
        p[2].Value = definitionTags;
        p[3].Value = rules;
        p[4].Value = score;
        p[5].Value = DictionaryDatabase.Pack(glossary);
        p[6].Value = sequence;
        p[7].Value = termTags;
        _term.ExecuteNonQuery();
    }

    /// <summary>Adds a frequency, pitch accent or pronunciation.</summary>
    public void AddMeta(string expression, string mode, string data)
    {
        var p = _meta.Parameters;
        p[0].Value = expression;
        p[1].Value = mode;
        p[2].Value = data;
        _meta.ExecuteNonQuery();
    }

    /// <summary>Adds a tag; a later one of the same name wins.</summary>
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

    /// <summary>Adds a picture.</summary>
    public void AddMedia(string path, byte[] data)
    {
        var p = _media.Parameters;
        p[0].Value = path;
        p[1].Value = data;
        _media.ExecuteNonQuery();
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
        _meta.Dispose();
        _tag.Dispose();
        _media.Dispose();
        if (!_finished)
        {
            _transaction.Dispose();
        }

        _connection.Dispose();
    }

    private SqliteCommand Prepare(string sql, params string[] names)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var name in names)
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        command.Prepare();
        return command;
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

    private DictionaryReader(string path, DictionaryInfo info, string styles)
    {
        Path = path;
        Info = info;
        Styles = styles;
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

            if (values.GetValueOrDefault("schema") != DictionaryDatabase.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || !values.TryGetValue("info", out var json)
                || JsonSerializer.Deserialize(json, DictionaryJsonContext.Default.DictionaryInfo) is not { } info)
            {
                return null;
            }

            return new DictionaryReader(path, info, values.GetValueOrDefault("styles") ?? string.Empty);
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
        using var connection = DictionaryDatabase.Open(Path, write: false);
        foreach (var chunk in keys.Chunk(KeysPerQuery))
        {
            using var command = connection.CreateCommand();
            var names = Parameters(command, chunk);
            command.CommandText =
                "SELECT rowid, expression, reading, definition_tags, rules, score, sequence, term_tags FROM terms WHERE expression IN (" + names + ")"
                + " UNION SELECT rowid, expression, reading, definition_tags, rules, score, sequence, term_tags FROM terms WHERE reading IN (" + names + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new TermRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7));
                found[row.Id] = row;
            }
        }

        return found.Values.ToList();
    }

    /// <summary>The definitions of some words, by their rows.</summary>
    /// <param name="ids">The rows.</param>
    /// <returns>Each row's definitions, as JSON.</returns>
    public Dictionary<long, string> Glossaries(IReadOnlyCollection<long> ids)
    {
        var found = new Dictionary<long, string>();
        using var connection = DictionaryDatabase.Open(Path, write: false);
        foreach (var chunk in ids.Chunk(KeysPerQuery))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rowid, glossary FROM terms WHERE rowid IN (" + string.Join(',', chunk) + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found[reader.GetInt64(0)] = DictionaryDatabase.Unpack((byte[])reader.GetValue(1));
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
