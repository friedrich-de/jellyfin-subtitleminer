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

/// <summary>What the import that runs or ran last is doing.</summary>
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

    /// <summary>Gets a value indicating whether an import is running.</summary>
    public bool Running => State is "downloading" or "importing";
}

/// <summary>
/// The dictionaries the server has: one file each in the plugin's data folder, a list of their order and which
/// are on, and one import at a time. Lookups read the dictionaries that are on, in that order.
/// </summary>
internal sealed class DictionaryLibrary : IDisposable
{
    /// <summary>The list's file in the folder.</summary>
    internal const string ListFileName = "dictionaries.json";

    private const string Extension = ".db";
    private const string Unfinished = ".importing";

    private readonly string _folder;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private List<InstalledDictionary> _installed = [];
    private ImportState _import = ImportState.Idle;
    private CancellationTokenSource? _running;
    private Task _job = Task.CompletedTask;

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

        _installed = Read();
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

    /// <summary>Gets the dictionaries lookups use, in their order.</summary>
    public IReadOnlyList<DictionaryReader> Enabled => Installed.Where(item => item.Enabled).Select(item => item.Reader).ToList();

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
    public LookupView Lookup(string text) => DictionaryLookup.Find(text, Enabled);

    /// <summary>
    /// Starts an import, unless one runs. <paramref name="fetch"/> gives the path of the zip (downloading it, or
    /// where an upload was saved); the zip is removed when the import ends.
    /// </summary>
    /// <param name="source">What is imported, as the page shows it.</param>
    /// <param name="fetch">Gets the zip, telling how far it is.</param>
    /// <returns>Whether it started.</returns>
    public bool TryStartImport(string source, Func<IProgress<double>, CancellationToken, Task<string>> fetch)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_import.Running)
            {
                return false;
            }

            _running?.Dispose();
            _running = new CancellationTokenSource();
            token = _running.Token;
            _import = new ImportState { State = "downloading", Source = source };
            _job = Task.Run(() => RunImport(source, fetch, token), CancellationToken.None);
        }

        return true;
    }

    /// <summary>Stops the import that runs, if one does.</summary>
    public void CancelImport()
    {
        lock (_gate)
        {
            if (_import.Running)
            {
                _running?.Cancel();
            }
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

        SqliteConnection.ClearAllPools();
        TryDelete(removed.Reader.Path);
        return true;
    }

    /// <summary>A picture of a dictionary, or <c>null</c> when it has none at that path.</summary>
    /// <param name="id">The dictionary's name.</param>
    /// <param name="path">The picture's path in it.</param>
    /// <returns>The picture and its type.</returns>
    public (byte[] Data, string Type)? Media(string id, string path)
    {
        var reader = Installed.FirstOrDefault(item => item.Reader.Info.Id == id)?.Reader;
        if (reader is null || !YomitanArchive.MediaTypes.TryGetValue(Path.GetExtension(path), out var type))
        {
            return null;
        }

        return reader.Media(path) is { } data ? (data, type) : null;
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

    /// <summary>Stops an import that runs.</summary>
    public void Dispose()
    {
        CancellationTokenSource? running;
        lock (_gate)
        {
            running = _running;
            _running = null;
        }

        if (running is not null)
        {
            running.Cancel();
            running.Dispose();
        }
    }

    private async Task RunImport(string source, Func<IProgress<double>, CancellationToken, Task<string>> fetch, CancellationToken token)
    {
        string? zip = null;
        var id = Guid.NewGuid().ToString("N");
        var unfinished = Path.Combine(_folder, id + Unfinished);
        try
        {
            zip = await fetch(new Progress(this, "downloading", source), token).ConfigureAwait(false);
            Update(new ImportState { State = "importing", Source = source });
            var info = YomitanArchive.Import(zip, unfinished, id, new Progress(this, "importing", source), token);
            Install(info, unfinished);
            Update(new ImportState { State = "done", Source = source, Progress = 1, Title = info.Title });
            _logger.LogInformation("Imported the dictionary {Title} ({Terms} words, {Frequencies} frequencies, {Pitches} pitch accents)", info.Title, info.Terms, info.Frequencies, info.Pitches);
        }
        catch (OperationCanceledException)
        {
            Update(new ImportState { State = "failed", Source = source, Error = "The import was stopped." });
        }
        catch (Exception error) when (error is InvalidDataException or DictionaryFetchException)
        {
            _logger.LogWarning("Could not import the dictionary from {Source}: {Reason}", source, error.Message);
            Update(new ImportState { State = "failed", Source = source, Error = error.Message });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Whatever else went wrong, the import ends, so that the next one may start.
            _logger.LogWarning(error, "Could not import the dictionary from {Source}", source);
            Update(new ImportState { State = "failed", Source = source, Error = "The dictionary could not be imported: " + error.Message });
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
            SqliteConnection.ClearAllPools();
            TryDelete(replaced.Reader.Path);
        }
    }

    private static bool SameDictionary(DictionaryInfo installed, DictionaryInfo imported) =>
        installed.Title == imported.Title
        || (installed.IndexUrl is { Length: > 0 } index && index == imported.IndexUrl);

    private List<InstalledDictionary> Read()
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

    private void Update(ImportState state)
    {
        lock (_gate)
        {
            _import = state;
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
