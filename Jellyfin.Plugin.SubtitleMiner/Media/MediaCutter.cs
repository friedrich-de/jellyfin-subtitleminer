using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleMiner.Media;

/// <summary>
/// A picture to cut from a video.
/// </summary>
/// <param name="AtMs">The moment of a still, or where a moving picture starts.</param>
/// <param name="EndMs">Where a moving picture ends. <c>null</c> for a still.</param>
/// <param name="Format"><c>jpeg</c>, <c>webp</c> or <c>avif</c>.</param>
/// <param name="Width">The widest the picture may be, in pixels. A smaller video is not enlarged.</param>
/// <param name="Quality">From 1, smallest file, to 100, best picture.</param>
internal sealed record PictureRequest(long AtMs, long? EndMs, string Format, int Width, int Quality);

/// <summary>
/// A piece of sound to cut from a video.
/// </summary>
/// <param name="StartMs">Where it starts.</param>
/// <param name="EndMs">Where it ends.</param>
/// <param name="Format"><c>mp3</c> or <c>opus</c>.</param>
/// <param name="Bitrate">Kilobits per second.</param>
/// <param name="Normalize">Whether to bring the loudness to a common level.</param>
/// <param name="Track">The audio track, one of the video's streams. <c>null</c> for the file's first.</param>
internal sealed record AudioRequest(long StartMs, long EndMs, string Format, int Bitrate, bool Normalize, MediaStreamInfo? Track);

/// <summary>
/// How a cut ended.
/// </summary>
internal enum CutStatus
{
    /// <summary>The file is there.</summary>
    Done,

    /// <summary>What was asked for makes no sense or this server cannot make it.</summary>
    Refused,

    /// <summary>Too many cuts wait already.</summary>
    Busy,

    /// <summary>ffmpeg failed or made nothing.</summary>
    Failed,
}

/// <summary>
/// The outcome of a cut.
/// </summary>
/// <param name="Status">How it ended.</param>
/// <param name="Path">The file, for a cut that is done. Whoever takes it deletes it.</param>
/// <param name="ContentType">The file's media type.</param>
/// <param name="Extension">The file's extension, without a dot.</param>
/// <param name="Message">A sentence for the user when there is no file.</param>
internal sealed record CutResult(CutStatus Status, string? Path, string? ContentType, string? Extension, string? Message)
{
    public static CutResult Refuse(string message) => new(CutStatus.Refused, null, null, null, message);
}

/// <summary>
/// Cuts pictures and sound out of videos with ffmpeg. Two cuts run at a time and the rest wait in line;
/// each has a time limit and is stopped when the page that asked for it is gone.
/// </summary>
internal sealed class MediaCutter : IDisposable
{
    /// <summary>The longest piece of sound.</summary>
    internal const long MaxAudioMs = 60_000;

    /// <summary>The longest moving picture.</summary>
    internal const long MaxMovingMs = 15_000;

    /// <summary>How many cuts run at the same time.</summary>
    internal const int Running = 2;

    /// <summary>How many cuts of one user may wait or run before more are turned away.</summary>
    internal const int MaxPerUser = 6;

    private static readonly string[] _avifEncoders = ["libsvtav1", "libaom-av1"];

    private readonly IFfmpeg _ffmpeg;
    private readonly string _scratchDirectory;
    private readonly ILogger<MediaCutter> _logger;
    private readonly SemaphoreSlim _slots = new(Running, Running);
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, int> _perUser = [];
    private Task<string?>? _avifEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaCutter"/> class.
    /// </summary>
    /// <param name="ffmpeg">Runs ffmpeg.</param>
    /// <param name="scratchDirectory">A folder for the files that are made.</param>
    /// <param name="logger">The logger.</param>
    public MediaCutter(IFfmpeg ffmpeg, string scratchDirectory, ILogger<MediaCutter> logger)
    {
        _ffmpeg = ffmpeg;
        _scratchDirectory = scratchDirectory;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// Cuts a picture.
    /// </summary>
    /// <param name="media">The video.</param>
    /// <param name="request">What to cut.</param>
    /// <param name="userId">Who asks.</param>
    /// <param name="cancellationToken">Stops the cut.</param>
    /// <returns>The outcome.</returns>
    public async Task<CutResult> PictureAsync(MediaInfo media, PictureRequest request, Guid userId, CancellationToken cancellationToken)
    {
        if (media.Path is null)
        {
            return CutResult.Refuse("The server cannot read this video as a file, so nothing can be cut from it.");
        }

        var format = request.Format.ToLowerInvariant();
        if (format == "jpg")
        {
            format = "jpeg";
        }

        if (format is not ("jpeg" or "webp" or "avif"))
        {
            return CutResult.Refuse("A picture is made as jpeg, webp or avif.");
        }

        var moving = request.EndMs is not null;
        if (moving && format == "jpeg")
        {
            return CutResult.Refuse("A moving picture is made as webp or avif.");
        }

        if (request.AtMs < 0 || (moving && request.EndMs <= request.AtMs))
        {
            return CutResult.Refuse("The picture's time is not within the video.");
        }

        string? encoder = null;
        if (format == "avif")
        {
            encoder = await AvifEncoderAsync(cancellationToken).ConfigureAwait(false);
            if (encoder is null)
            {
                return CutResult.Refuse("The ffmpeg of this server cannot make avif pictures. Choose jpeg or webp.");
            }
        }

        var extension = format == "jpeg" ? "jpg" : format;
        var contentType = "image/" + format;
        return await RunAsync(
            userId,
            extension,
            contentType,
            moving ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(30),
            output => PictureArguments(media.Path, output, request with { Format = format }, encoder),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cuts a piece of sound.
    /// </summary>
    /// <param name="media">The video.</param>
    /// <param name="request">What to cut.</param>
    /// <param name="userId">Who asks.</param>
    /// <param name="cancellationToken">Stops the cut.</param>
    /// <returns>The outcome.</returns>
    public async Task<CutResult> AudioAsync(MediaInfo media, AudioRequest request, Guid userId, CancellationToken cancellationToken)
    {
        if (media.Path is null)
        {
            return CutResult.Refuse("The server cannot read this video as a file, so nothing can be cut from it.");
        }

        var format = request.Format.ToLowerInvariant();
        if (format is not ("mp3" or "opus"))
        {
            return CutResult.Refuse("Sound is made as mp3 or opus.");
        }

        if (request.StartMs < 0 || request.EndMs <= request.StartMs)
        {
            return CutResult.Refuse("The sound's time is not within the video.");
        }

        if (!media.Streams.Any(stream => stream.Kind == StreamKind.Audio && !stream.IsExternal))
        {
            return CutResult.Refuse("This video has no sound.");
        }

        return await RunAsync(
            userId,
            format == "mp3" ? "mp3" : "ogg",
            format == "mp3" ? "audio/mpeg" : "audio/ogg",
            TimeSpan.FromSeconds(60),
            output => AudioArguments(media.Path, output, request with { Format = format }),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The ffmpeg call for a picture.
    /// </summary>
    internal static List<string> PictureArguments(string videoPath, string output, PictureRequest request, string? avifEncoder)
    {
        var width = Math.Clamp(request.Width, 16, 3840);
        var quality = Math.Clamp(request.Quality, 1, 100);
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-ss", Seconds(request.AtMs) };
        if (request.EndMs is { } end)
        {
            arguments.AddRange(["-t", Seconds(Math.Min(end - request.AtMs, MaxMovingMs))]);
        }

        arguments.AddRange(["-i", videoPath, "-map", "0:v:0", "-an", "-sn", "-dn"]);

        // A video narrower than the wanted width keeps its size. The height follows and stays even.
        var scale = string.Create(CultureInfo.InvariantCulture, $"scale='min({width},iw)':-2");
        if (request.EndMs is null)
        {
            arguments.AddRange(["-frames:v", "1", "-vf", scale]);
        }
        else
        {
            arguments.AddRange(["-vf", "fps=12," + scale]);
        }

        switch (request.Format)
        {
            case "jpeg":
                // ffmpeg counts the other way round: 2 is the best picture, 31 the smallest file.
                arguments.AddRange(["-c:v", "mjpeg", "-q:v", Number(Math.Clamp((int)Math.Round(31 - (quality * 0.29)), 2, 31)), "-f", "image2"]);
                break;
            case "webp":
                arguments.AddRange(["-c:v", "libwebp", "-quality", Number(quality), "-compression_level", "3"]);
                if (request.EndMs is not null)
                {
                    arguments.AddRange(["-loop", "0"]);
                }

                arguments.AddRange(["-f", "webp"]);
                break;
            default:
                // For AV1 a lower number is a better picture too: 63 is the smallest file.
                arguments.AddRange(["-c:v", avifEncoder ?? _avifEncoders[0], "-crf", Number(Math.Clamp((int)Math.Round(60 - (quality * 0.4)), 18, 60)), "-pix_fmt", "yuv420p"]);
                arguments.AddRange(avifEncoder == "libaom-av1" ? ["-cpu-used", "8"] : ["-preset", "10"]);
                if (request.EndMs is null)
                {
                    arguments.AddRange(["-still-picture", "1"]);
                }

                arguments.AddRange(["-f", "avif"]);
                break;
        }

        arguments.Add(output);
        return arguments;
    }

    /// <summary>
    /// The ffmpeg call for a piece of sound.
    /// </summary>
    internal static List<string> AudioArguments(string videoPath, string output, AudioRequest request)
    {
        var duration = Math.Min(request.EndMs - request.StartMs, MaxAudioMs);
        var track = request.Track is { IsExternal: false, Kind: StreamKind.Audio, Ordinal: >= 0 } chosen ? chosen.Ordinal : 0;
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-ss", Seconds(request.StartMs), "-t", Seconds(duration),
            "-i", videoPath,
            "-map", string.Create(CultureInfo.InvariantCulture, $"0:a:{track}"),
            "-vn", "-sn", "-dn",

            // Two channels play everywhere, and a film's six would triple the file.
            "-ac", "2",
        };

        // A cut in the middle of a sound starts with a click unless it fades in, however briefly.
        var filters = new List<string> { "afade=t=in:d=0.01" };
        if (request.Normalize)
        {
            filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        arguments.AddRange(["-af", string.Join(',', filters)]);
        if (request.Format == "mp3")
        {
            arguments.AddRange(["-c:a", "libmp3lame", "-b:a", Number(Math.Clamp(request.Bitrate, 32, 320)) + "k", "-f", "mp3"]);
        }
        else
        {
            // loudnorm works at 192 kHz, and Opus only takes its own rates.
            arguments.AddRange(["-c:a", "libopus", "-b:a", Number(Math.Clamp(request.Bitrate, 16, 256)) + "k", "-ar", "48000", "-f", "ogg"]);
        }

        arguments.Add(output);
        return arguments;
    }

    private static string Seconds(long milliseconds) => (milliseconds / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<CutResult> RunAsync(
        Guid userId,
        string extension,
        string contentType,
        TimeSpan limit,
        Func<string, List<string>> arguments,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var mine = _perUser.GetValueOrDefault(userId);
            if (mine >= MaxPerUser)
            {
                return new CutResult(CutStatus.Busy, null, null, null, "Too many pictures and sounds are being made for you at the moment. Try again in a few seconds.");
            }

            _perUser[userId] = mine + 1;
        }

        var output = Path.Combine(_scratchDirectory, Guid.NewGuid().ToString("N") + "." + extension);
        var done = false;
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_scratchDirectory);
                var ran = await _ffmpeg.RunAsync(arguments(output), limit, cancellationToken).ConfigureAwait(false);
                if (!ran || !File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    return new CutResult(CutStatus.Failed, null, null, null, "ffmpeg could not make this. The Jellyfin log says why.");
                }

                done = true;
                return new CutResult(CutStatus.Done, output, contentType, extension, null);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            lock (_lock)
            {
                var mine = _perUser.GetValueOrDefault(userId) - 1;
                if (mine <= 0)
                {
                    _perUser.Remove(userId);
                }
                else
                {
                    _perUser[userId] = mine;
                }
            }

            if (!done)
            {
                try
                {
                    File.Delete(output);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "A file that was not finished could not be removed");
                }
            }
        }
    }

    /// <summary>
    /// Finds out, once, which AV1 encoder this server's ffmpeg has.
    /// </summary>
    private Task<string?> AvifEncoderAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _avifEncoder ??= FindAsync();
        }

        async Task<string?> FindAsync()
        {
            try
            {
                var encoders = await _ffmpeg.AskAsync(["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false) ?? string.Empty;
                return _avifEncoders.FirstOrDefault(name => encoders.Contains(" " + name + " ", StringComparison.Ordinal));
            }
            catch (OperationCanceledException)
            {
                // Nobody waited for the answer. The next request asks again.
                lock (_lock)
                {
                    _avifEncoder = null;
                }

                throw;
            }
        }
    }
}
