using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleMiner.Subtitles;

/// <summary>
/// Hands out the lines of a subtitle track. A subtitle file next to the video is read as it is. A track
/// inside the video file is cut out with ffmpeg, together with the file's other text tracks, because
/// that takes one pass over the whole file however many tracks are wanted. What was read is kept for a
/// while, so a page that asks again, or another page, gets it at once. The tracks of one video are kept
/// and let go of together: a streaming release can carry forty of them, one for each language.
/// </summary>
/// <remarks>
/// Jellyfin's own subtitle extraction is not used: it keeps its results under the track's number, and
/// that number moves when a subtitle file is saved next to the video, so it can hand out another
/// track's lines afterwards. It also converts to WebVTT, which loses the styles that tell signs from
/// dialogue.
/// </remarks>
internal sealed class SubtitleStore : IDisposable
{
    /// <summary>A subtitle file larger than this is not read.</summary>
    internal const long MaxFileBytes = 32 * 1024 * 1024;

    /// <summary>How many subtitle files and videos the lines are kept of.</summary>
    internal const int MaxKept = 16;

    /// <summary>How many lines are kept in all. The newest file or video is kept even when it alone has more.</summary>
    internal const int MaxKeptLines = 100_000;

    /// <summary>The key a subtitle file's one track is kept under.</summary>
    private const string FileTrack = "file";

    private static readonly TimeSpan _cutLimit = TimeSpan.FromMinutes(10);

    /// <summary>How long a track that did not come out of a video file is left alone before it is tried again.</summary>
    private static readonly TimeSpan _retryAfter = TimeSpan.FromMinutes(10);

    private readonly IFfmpeg _ffmpeg;
    private readonly string _scratchDirectory;
    private readonly ILogger<SubtitleStore> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Kept> _kept = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyDictionary<string, SubtitleDocument?>>> _cutting = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _oneCut = new(1, 1);
    private long _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleStore"/> class.
    /// </summary>
    /// <param name="ffmpeg">Runs ffmpeg.</param>
    /// <param name="scratchDirectory">A folder for files that live as long as one cut.</param>
    /// <param name="logger">The logger.</param>
    public SubtitleStore(IFfmpeg ffmpeg, string scratchDirectory, ILogger<SubtitleStore> logger)
    {
        _ffmpeg = ffmpeg;
        _scratchDirectory = scratchDirectory;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _oneCut.Dispose();

    /// <summary>
    /// Gets the lines of a subtitle track.
    /// </summary>
    /// <param name="media">The video.</param>
    /// <param name="track">The track, one of the video's streams.</param>
    /// <param name="cancellationToken">Stops the wait. A cut that others wait for goes on.</param>
    /// <returns>The lines, or <c>null</c> when the track cannot be read.</returns>
    public async Task<SubtitleDocument?> GetAsync(MediaInfo media, MediaStreamInfo track, CancellationToken cancellationToken)
    {
        if (!track.IsReadable)
        {
            return null;
        }

        if (track.IsExternal)
        {
            return await ReadFileAsync(track, cancellationToken).ConfigureAwait(false);
        }

        if (media.Path is null)
        {
            return null;
        }

        var fileKey = media.Path + "|" + media.Stamp;
        if (TryGetKept(fileKey, track.Key, out var kept))
        {
            return kept;
        }

        Task<IReadOnlyDictionary<string, SubtitleDocument?>> cut;
        lock (_lock)
        {
            if (!_cutting.TryGetValue(fileKey, out cut!))
            {
                // The cut is not tied to the request that started it: the next request for this video waits for the same one.
                cut = Task.Run(() => CutAsync(media, fileKey), CancellationToken.None);
                _cutting[fileKey] = cut;
                _ = cut.ContinueWith(
                    _ =>
                    {
                        lock (_lock)
                        {
                            _cutting.Remove(fileKey);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        // What the cut gave is handed out as it is, also when other cuts have pushed it out of what is kept since.
        var tracks = await cut.WaitAsync(cancellationToken).ConfigureAwait(false);
        return tracks.GetValueOrDefault(track.Key);
    }

    private static string FormatOf(MediaStreamInfo track) => track.Codec is "ass" or "ssa" ? "ass" : "srt";

    private async Task<SubtitleDocument?> ReadFileAsync(MediaStreamInfo track, CancellationToken cancellationToken)
    {
        var file = new FileInfo(track.Path ?? string.Empty);
        if (!file.Exists)
        {
            _logger.LogWarning("The subtitle file {Path} is gone", file.FullName);
            return null;
        }

        if (file.Length > MaxFileBytes)
        {
            _logger.LogWarning("The subtitle file {Path} is too large to read: {Bytes} bytes", file.FullName, file.Length);
            return null;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{file.FullName}|{file.Length}-{file.LastWriteTimeUtc.Ticks}");
        if (TryGetKept(key, FileTrack, out var kept))
        {
            return kept;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            var document = SubtitleReader.Read(bytes, track.Codec);
            Keep(key, new Dictionary<string, SubtitleDocument?>(StringComparer.Ordinal) { [FileTrack] = document });
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The subtitle file {Path} could not be read", file.FullName);
            return null;
        }
    }

    /// <summary>
    /// Cuts every text track out of a video file and keeps their lines.
    /// </summary>
    /// <returns>The lines of each track, by its key; <c>null</c> for a track that did not come out.</returns>
    private async Task<IReadOnlyDictionary<string, SubtitleDocument?>> CutAsync(MediaInfo media, string fileKey)
    {
        var tracks = media.Streams.Where(stream => stream is { IsExternal: false, IsReadable: true }).ToList();
        var documents = tracks.ToDictionary(track => track.Key, _ => (SubtitleDocument?)null, StringComparer.Ordinal);
        var folder = Path.Combine(_scratchDirectory, "cut-" + Guid.NewGuid().ToString("N"));
        string PathOf(MediaStreamInfo track) => Path.Combine(folder, track.Key + "." + FormatOf(track));

        await _oneCut.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);
            if (!await _ffmpeg.RunAsync(CutArguments(media.Path!, tracks, PathOf), _cutLimit, CancellationToken.None).ConfigureAwait(false))
            {
                // One track that cannot be converted fails the whole pass, so each track gets its own.
                foreach (var track in tracks.Where(_ => tracks.Count > 1))
                {
                    await _ffmpeg.RunAsync(CutArguments(media.Path!, [track], PathOf), _cutLimit, CancellationToken.None).ConfigureAwait(false);
                }
            }

            foreach (var track in tracks)
            {
                var path = PathOf(track);
                if (!File.Exists(path))
                {
                    _logger.LogWarning("Subtitle track {Ordinal} ({Codec}) of {Path} did not come out of ffmpeg", track.Ordinal, track.Codec, media.Path);
                }
                else if (new FileInfo(path).Length > MaxFileBytes)
                {
                    _logger.LogWarning("Subtitle track {Ordinal} of {Path} is too large to read", track.Ordinal, media.Path);
                }
                else
                {
                    documents[track.Key] = SubtitleReader.Read(await File.ReadAllBytesAsync(path).ConfigureAwait(false), FormatOf(track));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The subtitle tracks of {Path} could not be cut out", media.Path);
        }
        finally
        {
            _oneCut.Release();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // What stays behind lies in Jellyfin's folder for temporary files.
            }
        }

        // A track that did not come out is remembered as such. Asking again would read the whole video file
        // again, to no end.
        Keep(fileKey, documents);
        return documents;
    }

    /// <summary>
    /// The ffmpeg call that writes each wanted track of a video file into a file of its own.
    /// </summary>
    internal static List<string> CutArguments(string videoPath, IReadOnlyList<MediaStreamInfo> tracks, Func<MediaStreamInfo, string> pathOf)
    {
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", videoPath };
        foreach (var track in tracks)
        {
            var format = FormatOf(track);
            arguments.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"0:s:{track.Ordinal}"), "-c:s", format, "-f", format, pathOf(track)]);
        }

        return arguments;
    }

    private bool TryGetKept(string source, string track, out SubtitleDocument? document)
    {
        lock (_lock)
        {
            if (_kept.TryGetValue(source, out var entry) && entry.Tracks.TryGetValue(track, out var found))
            {
                if (found is null && Environment.TickCount64 - entry.At > _retryAfter.TotalMilliseconds)
                {
                    _kept.Remove(source);
                }
                else
                {
                    entry.Used = ++_clock;
                    document = found;
                    return true;
                }
            }
        }

        document = null;
        return false;
    }

    /// <summary>
    /// Keeps what was read of one subtitle file or one video, and lets go of what was used longest ago, a
    /// file or a video as a whole, while too much is kept.
    /// </summary>
    private void Keep(string source, IReadOnlyDictionary<string, SubtitleDocument?> tracks)
    {
        lock (_lock)
        {
            _kept[source] = new Kept(tracks, Environment.TickCount64) { Used = ++_clock };
            while (_kept.Count > 1 && (_kept.Count > MaxKept || _kept.Values.Sum(entry => entry.Lines) > MaxKeptLines))
            {
                _kept.Remove(_kept.MinBy(pair => pair.Value.Used).Key);
            }
        }
    }

    /// <summary>What was read of one subtitle file or one video file.</summary>
    private sealed class Kept(IReadOnlyDictionary<string, SubtitleDocument?> tracks, long at)
    {
        /// <summary>Gets the lines of each track, by its key; <c>null</c> for a track that could not be read.</summary>
        public IReadOnlyDictionary<string, SubtitleDocument?> Tracks { get; } = tracks;

        /// <summary>Gets when it was read, in <see cref="Environment.TickCount64"/>.</summary>
        public long At { get; } = at;

        /// <summary>Gets how many lines it holds.</summary>
        public int Lines { get; } = tracks.Values.Sum(document => document?.Lines.Count ?? 0);

        /// <summary>Gets or sets when it was last asked for, by the store's own clock.</summary>
        public long Used { get; set; }
    }
}
