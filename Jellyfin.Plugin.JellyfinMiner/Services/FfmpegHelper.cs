using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinMiner.Services;

internal static partial class FfmpegHelper
{
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> EncoderCache = new(StringComparer.Ordinal);

    public static async Task<bool> RunAsync(
        IMediaEncoder mediaEncoder,
        ILogger logger,
        string operation,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaEncoder.EncoderPath))
        {
            logger.LogDebug("FFmpeg {Operation} failed: encoder path is unavailable.", operation);
            return false;
        }

        var info = new ProcessStartInfo(mediaEncoder.EncoderPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            logger.LogDebug("FFmpeg {Operation} failed: {Error}", operation, error.Trim());
        }

        return process.ExitCode == 0;
    }

    public static async Task<string?> FindFirstEncoderAsync(
        IMediaEncoder mediaEncoder,
        ILogger logger,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaEncoder.EncoderPath))
        {
            logger.LogDebug("FFmpeg encoder detection failed: encoder path is unavailable.");
            return null;
        }

        if (!EncoderCache.TryGetValue(mediaEncoder.EncoderPath, out var encoders))
        {
            encoders = await ReadEncodersAsync(mediaEncoder, logger, cancellationToken).ConfigureAwait(false);
            EncoderCache[mediaEncoder.EncoderPath] = encoders;
        }

        return candidates.FirstOrDefault(encoders.Contains);
    }

    internal static IReadOnlySet<string> ParseEncoderNames(string output)
    {
        var encoders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var match = EncoderLineRegex().Match(line);
            if (match.Success)
            {
                encoders.Add(match.Groups["name"].Value);
            }
        }

        return encoders;
    }

    private static async Task<IReadOnlySet<string>> ReadEncodersAsync(
        IMediaEncoder mediaEncoder,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-encoders");

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogDebug("FFmpeg encoder detection failed: {Error}", error.Trim());
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return ParseEncoderNames(output);
    }

    /// <summary>
    /// Prepends a <c>file:</c> protocol prefix to a local file path so that
    /// ffmpeg does not misinterpret a Windows drive letter (e.g. <c>C:</c>)
    /// as an unknown URI scheme. On non-Windows paths this is harmless.
    /// </summary>
    internal static string ToFfmpegInputPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? path : path.Contains("://", StringComparison.Ordinal) ? path : $"file:{path}";

    [GeneratedRegex(@"^\s*[A-Z.]{6}\s+(?<name>\S+)\s", RegexOptions.Compiled)]
    private static partial Regex EncoderLineRegex();
}
