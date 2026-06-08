using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinMiner.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinMiner.Services;

public sealed partial class MediaExtractionService
{
    private const int SchemaVersion = 1;
    private const double DefaultAudioOffsetSeconds = 0.25;
    private const double MaxOffsetSeconds = 5;
    private const double MaxAudioDurationSeconds = 60;
    private const double MaxAnimatedImageDurationSeconds = 12;
    private static readonly string[] AvifEncoderPreference = ["libaom-av1", "libsvtav1"];

    private readonly SubtitleService _subtitles;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<MediaExtractionService> _logger;
    private readonly string _tempDirectory;

    public MediaExtractionService(
        SubtitleService subtitles,
        IMediaEncoder mediaEncoder,
        IApplicationPaths paths,
        ILogger<MediaExtractionService> logger)
    {
        _subtitles = subtitles;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _tempDirectory = Path.Combine(paths.TempDirectory, "jellyfin-miner", "media");
    }

    public async Task<MediaResponse> CreateImageAsync(
        SessionSnapshot session,
        ImageMediaRequest request,
        CancellationToken cancellationToken)
    {
        EnsureLocalVideo(session);
        var cues = (await _subtitles.GetAsync(session, request.StreamIndex, cancellationToken).ConfigureAwait(false)).Cues;
        var range = ResolveCueRange(cues, request.CueIndex, request.EndCueIndex);
        var config = NormalizeImageConfig(request);
        var extension = config.Extension;
        var output = CreateTempPath("image", extension);

        try
        {
            var avifEncoder = config.Format == "avif"
                ? await ResolveAvifEncoderAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var args = BuildImageArgs(session.VideoPath, output, range, config, avifEncoder);
            await RunFfmpegAsync(args, output, cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync("image", config.MimeType, extension, request.CueIndex, output, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(output);
        }
    }

    public async Task<MediaResponse> CreateAudioAsync(
        SessionSnapshot session,
        AudioMediaRequest request,
        CancellationToken cancellationToken)
    {
        EnsureLocalVideo(session);
        var cues = (await _subtitles.GetAsync(session, request.StreamIndex, cancellationToken).ConfigureAwait(false)).Cues;
        var range = ResolveCueRange(cues, request.StartCueIndex, request.EndCueIndex);
        var config = NormalizeAudioConfig(request);
        var extension = config.Extension;
        var output = CreateTempPath("audio", extension);

        try
        {
            var args = BuildAudioArgs(
                session.VideoPath,
                output,
                range,
                config,
                ResolveFfmpegStreamIndex(session, session.ActiveAudioStreamIndex));
            await RunFfmpegAsync(args, output, cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync("audio", config.MimeType, extension, request.StartCueIndex, output, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(output);
        }
    }

    public static ImageMediaConfig NormalizeImageConfig(ImageMediaRequest request)
    {
        var format = NormalizeToken(request.Format, "jpeg");
        var animated = request.Animated == true;
        var (extension, mimeType, normalizedFormat) = format switch
        {
            "jpg" or "jpeg" when !animated => ("jpg", "image/jpeg", "jpeg"),
            "webp" => ("webp", "image/webp", "webp"),
            "avif" => ("avif", "image/avif", "avif"),
            "jpg" or "jpeg" => throw new MediaExtractionException("media_invalid_config", "Animated JPEG output is not supported."),
            _ => throw new MediaExtractionException("media_invalid_config", "Image format must be jpeg, webp, or avif.")
        };

        var quality = normalizedFormat switch
        {
            "jpeg" => Clamp(request.Quality ?? 5, 1, 31),
            "avif" => Clamp(request.Quality ?? (animated ? 35 : 25), 0, 63),
            _ => Clamp(request.Quality ?? (animated ? 75 : 80), 0, 100)
        };

        return new ImageMediaConfig(normalizedFormat, extension, mimeType, quality, animated, NormalizeSize(request.Size));
    }

    public static AudioMediaConfig NormalizeAudioConfig(AudioMediaRequest request)
    {
        var format = NormalizeToken(request.Format, "mp3");
        var (extension, mimeType, normalizedFormat, maxQuality) = format switch
        {
            "mp3" => ("mp3", "audio/mpeg", "mp3", 320),
            "opus" or "ogg" => ("opus", "audio/ogg; codecs=opus", "opus", 512),
            _ => throw new MediaExtractionException("media_invalid_config", "Audio format must be mp3 or opus.")
        };

        var offsetStart = ClampFinite(request.OffsetStartSeconds ?? DefaultAudioOffsetSeconds, -MaxOffsetSeconds, MaxOffsetSeconds);
        var offsetEnd = ClampFinite(request.OffsetEndSeconds ?? DefaultAudioOffsetSeconds, -MaxOffsetSeconds, MaxOffsetSeconds);
        var filters = NormalizeAudioFilters(request.AudioFilterPreset);

        return new AudioMediaConfig(
            normalizedFormat,
            extension,
            mimeType,
            Clamp(request.Quality ?? 128, 8, maxQuality),
            offsetStart,
            offsetEnd,
            filters);
    }

    public static CueTimeRange ResolveCueRange(IReadOnlyList<SubtitleCue> cues, int startIndex, int? endIndex)
    {
        if (startIndex < 0 || startIndex >= cues.Count)
        {
            throw new MediaExtractionException("media_invalid_range", "The start cue was not found.");
        }

        var resolvedEndIndex = endIndex ?? startIndex;
        if (resolvedEndIndex < startIndex || resolvedEndIndex >= cues.Count)
        {
            throw new MediaExtractionException("media_invalid_range", "The end cue must be at or after the start cue.");
        }

        var startCue = cues[startIndex];
        var endCue = cues[resolvedEndIndex];
        return new CueTimeRange(startIndex, resolvedEndIndex, startCue.StartMs / 1000d, endCue.EndMs / 1000d);
    }

    public static string GenerateFileNameHint(string mediaType, int cueIndex, string extension)
    {
        return $"jellyfin_miner_{mediaType}_{cueIndex}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{extension}";
    }

    internal static int? ResolveFfmpegStreamIndex(SessionSnapshot session, int? jellyfinStreamIndex)
    {
        if (jellyfinStreamIndex is not >= 0)
        {
            return null;
        }

        var externalTracksBeforeStream = session.SubtitleTracks.Count(x => x.IsExternal && x.Index < jellyfinStreamIndex.Value);
        return Math.Max(0, jellyfinStreamIndex.Value - externalTracksBeforeStream);
    }

    internal static string[] BuildImageArgs(
        string input,
        string output,
        CueTimeRange range,
        ImageMediaConfig config,
        string? avifEncoder)
    {
        var start = config.Animated ? range.StartSeconds : (range.StartSeconds + range.EndSeconds) / 2;
        var duration = Math.Min(range.DurationSeconds, MaxAnimatedImageDurationSeconds);
        var args = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-ss",
            FormatSeconds(start),
            "-i",
            FfmpegHelper.ToFfmpegInputPath(input),
            "-an"
        };

        if (config.Animated)
        {
            args.AddRange(["-t", FormatSeconds(duration)]);
        }
        else
        {
            args.AddRange(["-frames:v", "1"]);
        }

        if (config.Size is not null)
        {
            args.AddRange(["-vf", $"scale={config.Size}"]);
        }

        switch (config.Format)
        {
            case "jpeg":
                args.AddRange(["-c:v", "mjpeg", "-q:v", config.Quality.ToString(CultureInfo.InvariantCulture)]);
                break;
            case "avif":
                AddAvifEncoderArgs(args, config, avifEncoder);
                break;
            default:
                args.AddRange(["-c:v", "libwebp", "-quality", config.Quality.ToString(CultureInfo.InvariantCulture)]);
                if (config.Animated)
                {
                    args.AddRange(["-loop", "0"]);
                }
                break;
        }

        args.Add(output);
        return [.. args];
    }

    private static void AddAvifEncoderArgs(List<string> args, ImageMediaConfig config, string? avifEncoder)
    {
        var encoder = avifEncoder ?? throw new MediaExtractionException(
            "media_generation_failed",
            "FFmpeg does not provide a supported AV1 encoder for AVIF output.");
        args.AddRange(["-c:v", encoder, "-crf", config.Quality.ToString(CultureInfo.InvariantCulture)]);

        if (encoder == "libsvtav1")
        {
            args.AddRange(["-preset", "10", "-pix_fmt", "yuv420p"]);
            if (!config.Animated)
            {
                args.AddRange(["-svtav1-params", "avif=1"]);
            }
            return;
        }

        args.AddRange(["-cpu-used", "8", "-pix_fmt", "yuv420p"]);
    }

    internal static string[] BuildAudioArgs(
        string input,
        string output,
        CueTimeRange range,
        AudioMediaConfig config,
        int? audioStreamIndex)
    {
        var start = Math.Max(0, range.StartSeconds - config.OffsetStartSeconds);
        var end = range.EndSeconds + config.OffsetEndSeconds;
        var duration = Math.Min(end - start, MaxAudioDurationSeconds);
        if (duration <= 0)
        {
            throw new MediaExtractionException("media_invalid_range", "The configured audio range is empty.");
        }

        var args = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-ss",
            FormatSeconds(start),
            "-i",
            FfmpegHelper.ToFfmpegInputPath(input),
            "-t",
            FormatSeconds(duration),
            "-vn"
        };

        if (audioStreamIndex is >= 0)
        {
            args.AddRange(["-map", $"0:{audioStreamIndex.Value}"]);
        }

        if (config.Format == "mp3")
        {
            args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{config.Quality}k"]);
        }
        else
        {
            args.AddRange(["-c:a", "libopus", "-b:a", $"{config.Quality}k"]);
        }

        if (config.AudioFilters.Count > 0)
        {
            args.AddRange(["-af", string.Join(",", config.AudioFilters)]);
        }

        args.Add(output);
        return [.. args];
    }

    private async Task<string?> ResolveAvifEncoderAsync(CancellationToken cancellationToken)
        => await FfmpegHelper.FindFirstEncoderAsync(_mediaEncoder, _logger, AvifEncoderPreference, cancellationToken)
            .ConfigureAwait(false);

    private async Task RunFfmpegAsync(string[] args, string output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_tempDirectory);
        if (!await FfmpegHelper.RunAsync(_mediaEncoder, _logger, "media extraction", args, cancellationToken).ConfigureAwait(false)
            || !File.Exists(output))
        {
            throw new MediaExtractionException("media_generation_failed", "FFmpeg could not generate the requested media.");
        }
    }

    private static async Task<MediaResponse> ReadResponseAsync(
        string mediaType,
        string mimeType,
        string extension,
        int cueIndex,
        string output,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new MediaExtractionException("media_generation_failed", "FFmpeg generated an empty media file.");
        }

        return new MediaResponse(
            SchemaVersion,
            mediaType,
            mimeType,
            extension,
            GenerateFileNameHint(mediaType, cueIndex, extension),
            Convert.ToBase64String(bytes));
    }

    private string CreateTempPath(string prefix, string extension)
        => Path.Combine(_tempDirectory, $"{prefix}-{Guid.NewGuid():N}.{extension}");

    private static string? NormalizeSize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = SizeRegex().Match(raw.Trim());
        if (!match.Success)
        {
            throw new MediaExtractionException("media_invalid_config", "Image size must look like 640:-2.");
        }

        return $"{NormalizeDimension(match.Groups[1].Value)}:{NormalizeDimension(match.Groups[2].Value)}";
    }

    private static int NormalizeDimension(string raw)
    {
        var value = int.Parse(raw, CultureInfo.InvariantCulture);
        if (value is -1 or -2)
        {
            return value;
        }

        if (value < 1)
        {
            throw new MediaExtractionException("media_invalid_config", "Image dimensions must be positive, -1, or -2.");
        }

        return Clamp(value, 16, 3840);
    }

    private static IReadOnlyList<string> NormalizeAudioFilters(string? preset)
    {
        var filters = new List<string> { "afade=t=in:d=0.005" };
        switch (NormalizeToken(preset, "none"))
        {
            case "none":
                break;
            case "loudnorm":
                filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
                break;
            case "dynaudnorm":
                filters.Add("dynaudnorm");
                break;
            case "voice_boost":
                filters.Add("highpass=f=80");
                filters.Add("lowpass=f=12000");
                filters.Add("volume=1.6");
                break;
            default:
                throw new MediaExtractionException("media_invalid_config", "Audio filter preset must be none, loudnorm, dynaudnorm, or voice_boost.");
        }

        return filters;
    }

    private static string NormalizeToken(string? raw, string fallback)
        => string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim().TrimStart('.').ToLowerInvariant();

    private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    private static double ClampFinite(double value, double min, double max)
    {
        if (!double.IsFinite(value))
        {
            return min;
        }

        return Math.Min(max, Math.Max(min, value));
    }

    private static string FormatSeconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static void EnsureLocalVideo(SessionSnapshot session)
    {
        if (string.IsNullOrWhiteSpace(session.VideoPath) || !File.Exists(session.VideoPath))
        {
            throw new MediaExtractionException("media_unavailable", "The video file is not locally accessible to Jellyfin.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    [GeneratedRegex("^(-?\\d+):(-?\\d+)$", RegexOptions.Compiled)]
    private static partial Regex SizeRegex();
}

public sealed record CueTimeRange(int StartCueIndex, int EndCueIndex, double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

public sealed record ImageMediaConfig(
    string Format,
    string Extension,
    string MimeType,
    int Quality,
    bool Animated,
    string? Size);

public sealed record AudioMediaConfig(
    string Format,
    string Extension,
    string MimeType,
    int Quality,
    double OffsetStartSeconds,
    double OffsetEndSeconds,
    IReadOnlyList<string> AudioFilters);

public sealed class MediaExtractionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
