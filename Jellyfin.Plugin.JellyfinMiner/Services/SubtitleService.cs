using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyfinMiner.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinMiner.Services;

public sealed class SubtitleService
{
    internal const int MaxCacheEntries = 64;
    internal static readonly TimeSpan CacheEntryTtl = TimeSpan.FromMinutes(30);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<SubtitleService> _logger;
    private readonly string _tempDirectory;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();

    public SubtitleService(IMediaEncoder mediaEncoder, IApplicationPaths paths, ILogger<SubtitleService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _tempDirectory = Path.Combine(paths.TempDirectory, "jellyfin-miner", "subtitles");
    }

    public async Task<SubtitleResult> GetAsync(SessionSnapshot session, int streamIndex, CancellationToken cancellationToken)
    {
        var track = session.SubtitleTracks.FirstOrDefault(x => x.Index == streamIndex)
            ?? throw new KeyNotFoundException("Subtitle track was not found.");
        var key = new CacheKey(session.ItemId, session.MediaSourceId, streamIndex);
        var now = DateTimeOffset.UtcNow;
        PruneCache(now);
        var entry = _cache.GetOrAdd(key, _ => new CacheEntry(
            new Lazy<Task<SubtitleResult>>(
                () => LoadAsync(session, track, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication),
            now));
        if (_cache.Count > MaxCacheEntries)
        {
            PruneCache(now);
        }

        try
        {
            return await entry.Lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    public void Invalidate(Guid itemId, string mediaSourceId)
    {
        foreach (var key in _cache.Keys.Where(x => x.ItemId == itemId && x.MediaSourceId == mediaSourceId))
        {
            _cache.TryRemove(key, out _);
        }
    }

    private void PruneCache(DateTimeOffset now)
    {
        var entries = _cache.Select(x => new KeyValuePair<CacheKey, DateTimeOffset>(x.Key, x.Value.CreatedAtUtc));
        foreach (var key in CachePruning.SelectKeysToPrune(entries, now, CacheEntryTtl, MaxCacheEntries))
        {
            _cache.TryRemove(key, out _);
        }
    }

    private async Task<SubtitleResult> LoadAsync(SessionSnapshot session, SubtitleTrackSource track, CancellationToken cancellationToken)
    {
        string content;
        if (track.IsExternal && !string.IsNullOrWhiteSpace(track.Path) && File.Exists(track.Path))
        {
            var extension = Path.GetExtension(track.Path);
            if (extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            {
                content = await File.ReadAllTextAsync(track.Path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                content = await ConvertAsync(track.Path, null, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (File.Exists(session.VideoPath))
        {
            // Jellyfin inserts external subtitle tracks into its absolute stream index list.
            // FFmpeg only sees streams physically present in the container.
            var ffmpegIndex = track.Index - session.SubtitleTracks.Count(x => x.IsExternal && x.Index < track.Index);
            content = await ConvertAsync(session.VideoPath, ffmpegIndex, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new SubtitleUnavailableException("The subtitle source is not locally accessible.");
        }

        var cues = SubtitleParser.Parse(content);
        if (cues.Count == 0)
        {
            throw new SubtitleUnavailableException("The subtitle track contains no readable text cues.");
        }

        var hashInput = $"{session.ItemId:N}:{session.MediaSourceId}:{track.Index}:{content}";
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant()}\"";
        return new SubtitleResult(cues, etag);
    }

    private async Task<string> ConvertAsync(string input, int? streamIndex, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_tempDirectory);
        var output = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.vtt");
        try
        {
            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", FfmpegHelper.ToFfmpegInputPath(input) };
            if (streamIndex.HasValue)
            {
                args.AddRange(["-map", $"0:{streamIndex.Value}"]);
            }

            args.AddRange(["-f", "webvtt", output]);
            if (!await FfmpegHelper.RunAsync(_mediaEncoder, _logger, "subtitle conversion", [.. args], cancellationToken).ConfigureAwait(false)
                || !File.Exists(output))
            {
                throw new SubtitleUnavailableException("FFmpeg could not convert the subtitle track.");
            }

            return await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    private sealed record CacheKey(Guid ItemId, string MediaSourceId, int StreamIndex);
    private sealed record CacheEntry(Lazy<Task<SubtitleResult>> Lazy, DateTimeOffset CreatedAtUtc);
}

public sealed record SubtitleResult(IReadOnlyList<SubtitleCue> Cues, string ETag);

internal static class CachePruning
{
    public static IReadOnlySet<TKey> SelectKeysToPrune<TKey>(
        IEnumerable<KeyValuePair<TKey, DateTimeOffset>> entries,
        DateTimeOffset now,
        TimeSpan ttl,
        int maxEntries)
        where TKey : notnull
    {
        var ordered = entries.OrderBy(x => x.Value).ToList();
        var prune = new HashSet<TKey>();

        foreach (var entry in ordered.Where(x => now - x.Value >= ttl))
        {
            prune.Add(entry.Key);
        }

        var remainingCount = ordered.Count - prune.Count;
        var overflow = Math.Max(0, remainingCount - maxEntries);
        if (overflow == 0)
        {
            return prune;
        }

        foreach (var entry in ordered.Where(x => !prune.Contains(x.Key)).Take(overflow))
        {
            prune.Add(entry.Key);
        }

        return prune;
    }
}
