using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>The dictionaries in the order the administrator gave them, as the plugin keeps the list.</summary>
internal sealed class DictionaryList
{
    /// <summary>Gets the dictionaries.</summary>
    public List<DictionaryListEntry> Dictionaries { get; init; } = [];
}

/// <summary>One dictionary of the list.</summary>
internal sealed class DictionaryListEntry
{
    /// <summary>Gets the dictionary's name in the folder.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether lookups use it.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>An installed dictionary and whether lookups use it.</summary>
/// <param name="Reader">The dictionary.</param>
/// <param name="Enabled">Whether lookups use it.</param>
internal sealed record InstalledDictionary(DictionaryReader Reader, bool Enabled);

/// <summary>What an import came to.</summary>
/// <param name="Number">Which import of this run of the server it was, counted from 1: a page tells by it which ones it has not shown yet.</param>
/// <param name="Source">What was imported: a file's name or an address.</param>
/// <param name="Title">The dictionary's title, when it went in.</param>
/// <param name="Error">Why it did not, when it did not.</param>
internal sealed record ImportOutcome(long Number, string Source, string? Title, string? Error);

/// <summary>What the import that runs or ran last is doing, and what waits behind it.</summary>
internal sealed record ImportState
{
    /// <summary>Gets nothing imported yet.</summary>
    public static ImportState Idle { get; } = new() { State = "idle" };

    /// <summary>Gets <c>idle</c>, <c>downloading</c>, <c>importing</c>, <c>done</c> or <c>failed</c>.</summary>
    public required string State { get; init; }

    /// <summary>Gets what is imported: a file's name or an address.</summary>
    public string? Source { get; init; }

    /// <summary>Gets how far it is, from 0 to 1.</summary>
    public double Progress { get; init; }

    /// <summary>Gets the dictionary's title once it is known.</summary>
    public string? Title { get; init; }

    /// <summary>Gets why it failed.</summary>
    public string? Error { get; init; }

    /// <summary>Gets how many more dictionaries wait to be imported after this one.</summary>
    public int Waiting { get; init; }

    /// <summary>Gets what the last imports came to, the oldest first, at most <see cref="DictionaryLibrary.OutcomesKept"/>.</summary>
    public IReadOnlyList<ImportOutcome> Finished { get; init; } = [];

    /// <summary>Gets a value indicating whether an import is running.</summary>
    public bool Running => State is "downloading" or "importing";
}

/// <summary>
/// The dictionaries the server has: one file each in the plugin's data folder, a list of their order and which
/// are on, and one import at a time, with those asked for meanwhile waiting their turn. Lookups read the
/// dictionaries that are on, in that order.
/// </summary>
internal sealed class DictionaryLibrary : IDisposable
{
    /// <summary>The list's file in the folder.</summary>
    internal const string ListFileName = "dictionaries.json";

    /// <summary>At most this many dictionaries wait to be imported.</summary>
    internal const int MaxWaiting = 64;

    /// <summary>What this many of the last imports came to is kept for the page.</summary>
    internal const int OutcomesKept = 2 * MaxWaiting;

    /// <summary>Why an import that was stopped did not go in, and those that waited behind it.</summary>
    internal const string Stopped = "The import was stopped.";

    private const string BrokeOff = "The dictionary could not be imported: the server ran out of memory.";
    private const string Extension = ".db";
    private const string Unfinished = ".importing";

    private readonly string _folder;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private List<InstalledDictionary> _installed = [];
    private ImportState _import = ImportState.Idle;
    private CancellationTokenSource? _running;
    private Task _job = Task.CompletedTask;
    private bool _disposed;
    private long _imports;

    /// <summary>The imports asked for while one runs, in the order they were asked for, under <see cref="_gate"/>.</summary>
    private readonly Queue<Asked> _waiting = new();

    /// <summary>How many lookups and pictures read each dictionary now, under <see cref="_gate"/>.</summary>
    private readonly Dictionary<DictionaryReader, int> _readers = [];

    /// <summary>Dictionaries replaced or removed while read: their files go when the last read ends.</summary>
    private readonly HashSet<DictionaryReader> _retired = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DictionaryLibrary"/> class, reading what is in
    /// <paramref name="folder"/>. An import that a stop of the server cut short is thrown away.
    /// </summary>
    /// <param name="folder">The plugin's folder for dictionaries.</param>
    /// <param name="logger">The logger.</param>
    public DictionaryLibrary(string folder, ILogger logger)
    {
        _folder = folder;
        _logger = logger;
        Directory.CreateDirectory(folder);
        foreach (var unfinished in Directory.EnumerateFiles(folder, "*" + Unfinished))
        {
            TryDelete(unfinished);
        }

        _installed = ReadList();
    }

    /// <summary>Gets the dictionaries in their order.</summary>
    public IReadOnlyList<InstalledDictionary> Installed
    {
        get
        {
            lock (_gate)
            {
                return _installed;
            }
        }
    }

    /// <summary>Gets what the import that runs or ran last is doing.</summary>
    public ImportState Import
    {
        get
        {
            lock (_gate)
            {
                return _import;
            }
        }
    }

    /// <summary>Gets the import's work, for tests that wait for it.</summary>
    internal Task Job
    {
        get
        {
            lock (_gate)
            {
                return _job;
            }
        }
    }

    /// <summary>Looks up the words <paramref name="text"/> starts with in the dictionaries that are on.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The words.</returns>
    public LookupView Lookup(string text)
    {
        using var reading = Read(item => item.Enabled);
        return DictionaryLookup.Find(text, reading.Readers);
    }

    /// <summary>
    /// The dictionaries that <paramref name="which"/> picks, kept for reading until the result is disposed: one
    /// replaced or removed meanwhile keeps its file until then, since a lookup opens it more than once.
    /// </summary>
    /// <param name="which">Which dictionaries.</param>
    /// <returns>The dictionaries, in their order.</returns>
    internal Reading Read(Func<InstalledDictionary, bool> which)
    {
        lock (_gate)
        {
            var readers = _installed.Where(which).Select(item => item.Reader).ToList();
            foreach (var reader in readers)
            {
                _readers[reader] = _readers.GetValueOrDefault(reader) + 1;
            }

            return new Reading(this, readers);
        }
    }

    /// <summary>Gets a value indicating whether as many dictionaries wait to be imported as may.</summary>
    public bool ImportsFull
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count >= MaxWaiting;
            }
        }
    }

    /// <summary>
    /// Starts an import, or has it wait its turn when one runs. <paramref name="fetch"/> gives the path of the zip
    /// (downloading it, or where an upload was saved); the zip is removed when the import ends.
    /// </summary>
    /// <param name="source">What is imported, as the page shows it.</param>
    /// <param name="fetch">Gets the zip, telling how far it is.</param>
    /// <param name="uploaded">The zip, when it is on the server already: it is removed too if the import never gets its turn.</param>
    /// <returns>Whether it started or waits; <c>false</c> when too many wait already.</returns>
    public bool TryStartImport(string source, Func<IProgress<double>, CancellationToken, Task<string>> fetch, string? uploaded = null)
    {
        var asked = new Asked(source, fetch, uploaded);
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_import.Running)
            {
                if (_waiting.Count >= MaxWaiting)
                {
                    return false;
                }

                _waiting.Enqueue(asked);
                _import = _import with { Waiting = _waiting.Count };
                return true;
            }

            var token = Begin(asked);
            _job = Task.Run(() => RunImports(asked, token), CancellationToken.None);
        }

        return true;
    }

    /// <summary>Stops the import that runs, if one does, and lets go of those that wait.</summary>
    public void CancelImport()
    {
        List<Asked> dropped;
        lock (_gate)
        {
            if (!_import.Running)
            {
                return;
            }

            dropped = Drop(Stopped);
            _running?.Cancel();
        }

        foreach (var asked in dropped)
        {
            Discard(asked);
        }
    }

    /// <summary>
    /// Puts the dictionaries in a new order and turns them on or off. The order must name each dictionary once.
    /// </summary>
    /// <param name="order">The dictionaries' names, the first first.</param>
    /// <param name="disabled">Those lookups do not use.</param>
    /// <returns>Whether the order named each dictionary once.</returns>
    public bool Arrange(IReadOnlyList<string> order, IReadOnlyCollection<string> disabled)
    {
        lock (_gate)
        {
            var byId = _installed.ToDictionary(item => item.Reader.Info.Id, StringComparer.Ordinal);
            if (order.Count != byId.Count || order.Distinct(StringComparer.Ordinal).Count() != order.Count || !order.All(byId.ContainsKey))
            {
                return false;
            }

            _installed = order.Select(id => byId[id] with { Enabled = !disabled.Contains(id) }).ToList();
            Save();
            return true;
        }
    }

    /// <summary>Removes a dictionary.</summary>
    /// <param name="id">Its name.</param>
    /// <returns>Whether there was one of that name.</returns>
    public bool Remove(string id)
    {
        InstalledDictionary? removed;
        lock (_gate)
        {
            removed = _installed.FirstOrDefault(item => item.Reader.Info.Id == id);
            if (removed is null)
            {
                return false;
            }

            _installed = _installed.Where(item => item != removed).ToList();
            Save();
        }

        Retire(removed.Reader);
        return true;
    }

    /// <summary>A picture of a dictionary, or <c>null</c> when it has none at that path.</summary>
    /// <param name="id">The dictionary's name.</param>
    /// <param name="path">The picture's path in it.</param>
    /// <returns>The picture and its type.</returns>
    public (byte[] Data, string Type)? Media(string id, string path)
    {
        if (!YomitanArchive.MediaTypes.TryGetValue(Path.GetExtension(path), out var type))
        {
            return null;
        }

        using var reading = Read(item => item.Reader.Info.Id == id);
        return reading.Readers.Count > 0 && reading.Readers[0].Media(path) is { } data ? (data, type) : null;
    }

    /// <summary>The size of a dictionary's file.</summary>
    /// <param name="reader">The dictionary.</param>
    /// <returns>Its size in bytes, or 0 when it cannot be read.</returns>
    public static long SizeOf(DictionaryReader reader)
    {
        try
        {
            return new FileInfo(reader.Path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Stops an import that runs, and lets go of those that wait.</summary>
    public void Dispose()
    {
        CancellationTokenSource? running;
        List<Asked> dropped;
        lock (_gate)
        {
            _disposed = true;
            running = _running;
            _running = null;
            dropped = Drop(Stopped);
        }

        if (running is not null)
        {
            running.Cancel();
            running.Dispose();
        }

        foreach (var asked in dropped)
        {
            Discard(asked);
        }
    }

    /// <summary>Makes <paramref name="asked"/> the import that runs. Under <see cref="_gate"/>.</summary>
    private CancellationToken Begin(Asked asked)
    {
        _running?.Dispose();
        _running = new CancellationTokenSource();
        _import = new ImportState { State = "downloading", Source = asked.Source, Waiting = _waiting.Count, Finished = _import.Finished };
        return _running.Token;
    }

    /// <summary>Takes every import that waits out of the line, each noted as not done. Under <see cref="_gate"/>.</summary>
    private List<Asked> Drop(string why)
    {
        var dropped = _waiting.ToList();
        _waiting.Clear();
        foreach (var asked in dropped)
        {
            Note(asked.Source, null, why);
        }

        _import = _import with { Waiting = 0 };
        return dropped;
    }

    /// <summary>Notes what an import came to. Under <see cref="_gate"/>.</summary>
    private void Note(string source, string? title, string? error)
    {
        var finished = _import.Finished.Append(new ImportOutcome(++_imports, source, title, error)).TakeLast(OutcomesKept).ToList();
        _import = _import with { Finished = finished };
    }

    private void Discard(Asked asked)
    {
        if (asked.Uploaded is not null)
        {
            TryDelete(asked.Uploaded);
        }
    }

    /// <summary>Imports <paramref name="first"/>, then whatever was asked for meanwhile, one after another.</summary>
    private async Task RunImports(Asked first, CancellationToken token)
    {
        var asked = first;
        while (true)
        {
            string? title;
            string? error;
            try
            {
                (title, error) = await RunImport(asked, token).ConfigureAwait(false);
            }
            catch
            {
                // The server ran out of memory. The import ends all the same, with what waited, so that the
                // next one asked for may start.
                List<Asked> dropped;
                lock (_gate)
                {
                    Note(asked.Source, null, BrokeOff);
                    dropped = Drop(BrokeOff);
                    _import = new ImportState { State = "failed", Source = asked.Source, Error = BrokeOff, Finished = _import.Finished };
                }

                foreach (var left in dropped)
                {
                    Discard(left);
                }

                throw;
            }

            lock (_gate)
            {
                Note(asked.Source, title, error);
                if (_disposed || _waiting.Count == 0)
                {
                    // Only here does the import stop being one that runs, under the lock that looked for more to
                    // do: one asked for from now on starts its own work, and none is left waiting for nobody.
                    _import = new ImportState
                    {
                        State = error is null ? "done" : "failed",
                        Source = asked.Source,
                        Progress = error is null ? 1 : 0,
                        Title = title,
                        Error = error,
                        Finished = _import.Finished,
                    };
                    return;
                }

                // The next one starts under the same lock, so that the import is seen to run from the first to
                // the last.
                asked = _waiting.Dequeue();
                token = Begin(asked);
            }
        }
    }

    /// <summary>Imports one dictionary. What it came to is for <see cref="RunImports"/> to tell.</summary>
    private async Task<(string? Title, string? Error)> RunImport(Asked asked, CancellationToken token)
    {
        var source = asked.Source;
        string? zip = asked.Uploaded;
        var id = Guid.NewGuid().ToString("N");
        var unfinished = Path.Combine(_folder, id + Unfinished);
        try
        {
            zip = await asked.Fetch(new Progress(this, "downloading", source), token).ConfigureAwait(false);
            Update(new ImportState { State = "importing", Source = source });
            var info = YomitanArchive.Import(zip, unfinished, id, new Progress(this, "importing", source), token);
            Install(info, unfinished);
            _logger.LogInformation("Imported the dictionary {Title} ({Terms} words, {Frequencies} frequencies, {Pitches} pitch accents)", info.Title, info.Terms, info.Frequencies, info.Pitches);
            return (info.Title, null);
        }
        catch (OperationCanceledException)
        {
            return (null, Stopped);
        }
        catch (Exception error) when (error is InvalidDataException or DictionaryFetchException)
        {
            _logger.LogWarning("Could not import the dictionary from {Source}: {Reason}", source, error.Message);
            return (null, error.Message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Whatever else went wrong, the import ends, so that the next one may start.
            _logger.LogWarning(error, "Could not import the dictionary from {Source}", source);
            return (null, "The dictionary could not be imported: " + error.Message);
        }
        finally
        {
            TryDelete(unfinished);
            if (zip is not null)
            {
                TryDelete(zip);
            }
        }
    }

    /// <summary>
    /// Puts an imported dictionary in place. One with the same title, or a newer revision of one from the same
    /// address, takes the place of the old one, on or off as it was.
    /// </summary>
    private void Install(DictionaryInfo info, string unfinished)
    {
        var path = Path.Combine(_folder, info.Id + Extension);
        File.Move(unfinished, path);
        var reader = DictionaryReader.TryOpen(path) ?? throw new InvalidDataException("The imported dictionary could not be opened.");
        InstalledDictionary? replaced;
        lock (_gate)
        {
            replaced = _installed.FirstOrDefault(item => SameDictionary(item.Reader.Info, info));
            var added = new InstalledDictionary(reader, replaced?.Enabled ?? true);
            _installed = replaced is null ? [.. _installed, added] : _installed.Select(item => item == replaced ? added : item).ToList();
            Save();
        }

        if (replaced is not null)
        {
            Retire(replaced.Reader);
        }
    }

    /// <summary>Removes the file of a dictionary that is no longer listed, now or when the last read of it ends.</summary>
    private void Retire(DictionaryReader reader)
    {
        lock (_gate)
        {
            if (_readers.ContainsKey(reader))
            {
                _retired.Add(reader);
                return;
            }
        }

        SqliteConnection.ClearAllPools();
        TryDelete(reader.Path);
    }

    private void Done(IReadOnlyList<DictionaryReader> readers)
    {
        var gone = new List<DictionaryReader>();
        lock (_gate)
        {
            foreach (var reader in readers)
            {
                var left = _readers[reader] - 1;
                if (left > 0)
                {
                    _readers[reader] = left;
                    continue;
                }

                _readers.Remove(reader);
                if (_retired.Remove(reader))
                {
                    gone.Add(reader);
                }
            }
        }

        foreach (var reader in gone)
        {
            Retire(reader);
        }
    }

    private static bool SameDictionary(DictionaryInfo installed, DictionaryInfo imported) =>
        installed.Title == imported.Title
        || (installed.IndexUrl is { Length: > 0 } index && index == imported.IndexUrl);

    private List<InstalledDictionary> ReadList()
    {
        var listed = new DictionaryList();
        var file = Path.Combine(_folder, ListFileName);
        var hasList = false;
        try
        {
            if (File.Exists(file))
            {
                listed = JsonSerializer.Deserialize(File.ReadAllText(file), DictionaryJsonContext.Default.DictionaryList) ?? listed;
                hasList = true;
            }
        }
        catch (JsonException error)
        {
            _logger.LogWarning(error, "The list of dictionaries could not be read; the dictionaries are listed anew");
        }

        // A file the list does not name was removed, but could not be deleted then. Without a list, every file
        // is a dictionary, on.
        var entries = listed.Dictionaries.ToList();
        foreach (var path in Directory.EnumerateFiles(_folder, "*" + Extension).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (entries.Any(entry => entry.Id == id))
            {
                continue;
            }

            if (hasList)
            {
                TryDelete(path);
            }
            else
            {
                entries.Add(new DictionaryListEntry { Id = id });
            }
        }

        var installed = new List<InstalledDictionary>();
        foreach (var entry in entries.DistinctBy(entry => entry.Id))
        {
            var path = Path.Combine(_folder, entry.Id + Extension);
            if (!File.Exists(path))
            {
                continue;
            }

            if (DictionaryReader.TryOpen(path) is { } reader && reader.Info.Id == entry.Id)
            {
                installed.Add(new InstalledDictionary(reader, entry.Enabled));
            }
            else
            {
                _logger.LogWarning("The dictionary file {Path} could not be read and is left out", path);
            }
        }

        return installed;
    }

    private void Save()
    {
        var list = new DictionaryList
        {
            Dictionaries = _installed.Select(item => new DictionaryListEntry { Id = item.Reader.Info.Id, Enabled = item.Enabled }).ToList(),
        };
        var file = Path.Combine(_folder, ListFileName);
        var next = file + ".new";
        File.WriteAllText(next, JsonSerializer.Serialize(list, DictionaryJsonContext.Default.DictionaryList));
        File.Move(next, file, overwrite: true);
    }

    /// <summary>Says what the import that runs is doing; what waits and what ended stay as they are.</summary>
    private void Update(ImportState state)
    {
        lock (_gate)
        {
            _import = state with { Waiting = _waiting.Count, Finished = _import.Finished };
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Could not remove {Path}", path);
        }
    }

    /// <summary>An import that was asked for.</summary>
    /// <param name="Source">What is imported, as the page shows it.</param>
    /// <param name="Fetch">Gets the zip, telling how far it is.</param>
    /// <param name="Uploaded">The zip, when it is on the server already.</param>
    private sealed record Asked(string Source, Func<IProgress<double>, CancellationToken, Task<string>> Fetch, string? Uploaded);

    /// <summary>Dictionaries kept for reading until this is disposed.</summary>
    internal sealed class Reading(DictionaryLibrary library, IReadOnlyList<DictionaryReader> readers) : IDisposable
    {
        private int _done;

        /// <summary>Gets the dictionaries, in their order.</summary>
        public IReadOnlyList<DictionaryReader> Readers => readers;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                library.Done(readers);
            }
        }
    }

    /// <summary>Tells the import's state how far it is, at most every hundredth.</summary>
    private sealed class Progress(DictionaryLibrary library, string state, string source) : IProgress<double>
    {
        private double _shown = -1;

        public void Report(double value)
        {
            if (value - _shown < 0.01 && value < 1)
            {
                return;
            }

            _shown = value;
            library.Update(new ImportState { State = state, Source = source, Progress = Math.Clamp(value, 0, 1) });
        }
    }
}

/// <summary>A dictionary could not be fetched; the message says why, in words for the page.</summary>
internal sealed class DictionaryFetchException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DictionaryFetchException"/> class.</summary>
    public DictionaryFetchException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DictionaryFetchException"/> class.</summary>
    /// <param name="message">Why.</param>
    public DictionaryFetchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DictionaryFetchException"/> class.</summary>
    /// <param name="message">Why.</param>
    /// <param name="inner">What went wrong underneath.</param>
    public DictionaryFetchException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
