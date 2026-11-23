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
/// while, so a page that asks again, or another page, gets it at once.
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

    /// <summary>How many tracks are kept.</summary>
    internal const int MaxKept = 24;

    private static readonly TimeSpan _cutLimit = TimeSpan.FromMinutes(10);

    /// <summary>How long a track that did not come out of a video file is left alone before it is tried again.</summary>
    private static readonly TimeSpan _retryAfter = TimeSpan.FromMinutes(10);

    private readonly IFfmpeg _ffmpeg;
    private readonly string _scratchDirectory;
    private readonly ILogger<SubtitleStore> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (SubtitleDocument? Document, long Used, long At)> _kept = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _cutting = new(StringComparer.Ordinal);
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
        if (TryGetKept(KeyOf(fileKey, track), out var kept))
        {
            return kept;
        }

        Task cut;
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

        await cut.WaitAsync(cancellationToken).ConfigureAwait(false);
        return TryGetKept(KeyOf(fileKey, track), out kept) ? kept : null;
    }

    private static string KeyOf(string fileKey, MediaStreamInfo track) => fileKey + "|" + track.Key;

    private static string FormatOf(MediaStreamInfo track) => track.Codec is "ass" or "ssa" ? "ass" : "srt";

    private async Task<SubtitleDocument?> ReadFileAsync(MediaStreamInfo track, CancellationToken cancellationToken)
    {
        var file = new FileInfo(track.Path ?? string.Empty);
        if (!file.Exists || file.Length > MaxFileBytes)
        {
            return null;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{file.FullName}|{file.Length}-{file.LastWriteTimeUtc.Ticks}");
        if (TryGetKept(key, out var kept))
        {
            return kept;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            var document = SubtitleReader.Read(bytes, track.Codec);
            Keep(key, document);
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
    private async Task CutAsync(MediaInfo media, string fileKey)
    {
        var tracks = media.Streams.Where(stream => stream is { IsExternal: false, IsReadable: true }).ToList();
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
                // A track that did not come out is remembered as such. Asking again would read the whole
                // video file again, to no end.
                var path = PathOf(track);
                var readable = File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes;
                Keep(KeyOf(fileKey, track), readable ? SubtitleReader.Read(await File.ReadAllBytesAsync(path).ConfigureAwait(false), FormatOf(track)) : null);
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

    private bool TryGetKept(string key, out SubtitleDocument? document)
    {
        lock (_lock)
        {
            if (_kept.TryGetValue(key, out var entry))
            {
                if (entry.Document is null && Environment.TickCount64 - entry.At > _retryAfter.TotalMilliseconds)
                {
                    _kept.Remove(key);
                }
                else
                {
                    _kept[key] = (entry.Document, ++_clock, entry.At);
                    document = entry.Document;
                    return true;
                }
            }
        }

        document = null;
        return false;
    }

    private void Keep(string key, SubtitleDocument? document)
    {
        lock (_lock)
        {
            _kept[key] = (document, ++_clock, Environment.TickCount64);
            while (_kept.Count > MaxKept)
            {
                _kept.Remove(_kept.MinBy(pair => pair.Value.Used).Key);
            }
        }
    }
}
