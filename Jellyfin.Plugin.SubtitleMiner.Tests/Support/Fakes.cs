using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Jellyfin.Plugin.SubtitleMiner.Playback;
using Jellyfin.Plugin.SubtitleMiner.Subtitles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Support;

internal sealed class FakeCallers : ICallerResolver
{
    public Caller? Caller { get; set; }

    public Task<Caller?> ResolveAsync(HttpContext context) => Task.FromResult(Caller);
}

/// <summary>Page files held in memory, by name.</summary>
internal sealed class FakePageFileSource : IPageFileSource
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public FakePageFileSource(params string[] names)
    {
        foreach (var name in names)
        {
            _files[name] = "content of " + name;
        }
    }

    public IReadOnlyCollection<string> Names => _files.Keys;

    public Stream? Open(string name) => _files.TryGetValue(name, out var content) ? new MemoryStream(Encoding.UTF8.GetBytes(content)) : null;
}

/// <summary>A clock that moves when a test says so.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 20, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;

    public void AdvanceMs(long milliseconds) => Now += TimeSpan.FromMilliseconds(milliseconds);
}

/// <summary>A library that holds what a test puts into it, per user.</summary>
internal sealed class FakeMediaLibrary : IMediaLibrary
{
    private readonly List<(Guid UserId, MediaInfo Media)> _videos = [];

    public void Add(Guid userId, MediaInfo media) => _videos.Add((userId, media));

    public void Clear() => _videos.Clear();

    public MediaInfo? Find(Guid userId, Guid itemId, string? mediaSourceId) => _videos
        .Where(video => video.UserId == userId && video.Media.ItemId == itemId)
        .Select(video => video.Media)
        .FirstOrDefault(media => mediaSourceId is null || string.Equals(media.MediaSourceId, mediaSourceId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An ffmpeg that does what a test tells it and remembers how it was called.</summary>
internal sealed class FakeFfmpeg : IFfmpeg
{
    private readonly object _lock = new();

    /// <summary>Gets the calls so far, oldest first.</summary>
    public List<IReadOnlyList<string>> Runs { get; } = [];

    /// <summary>Gets or sets what a run does. By default it writes a few bytes into the file named last.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<bool>> OnRun { get; set; } = (arguments, _) =>
    {
        File.WriteAllBytes(arguments[^1], [1, 2, 3]);
        return Task.FromResult(true);
    };

    /// <summary>Gets or sets what ffmpeg says about itself.</summary>
    public string? Encoders { get; set; } = " V....D libsvtav1            SVT-AV1 \n V....D libwebp              WebP \n";

    public int Asked { get; private set; }

    public async Task<bool> RunAsync(IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Runs.Add(arguments);
        }

        return await OnRun(arguments, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> AskAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Asked++;
        return Task.FromResult(Encoders);
    }
}

/// <summary>The plugin's services over fakes, with a scratch folder that goes away with the test.</summary>
internal sealed class TestServices : IDisposable
{
    public TestServices()
    {
        Scratch = Path.Combine(Path.GetTempPath(), "subtitle-miner-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Scratch);
        Tracker = new PlaybackTracker(Time);
        Subtitles = new SubtitleStore(Ffmpeg, Path.Combine(Scratch, "work"), NullLogger<SubtitleStore>.Instance);
        Cutter = new MediaCutter(Ffmpeg, Path.Combine(Scratch, "work"), NullLogger<MediaCutter>.Instance);
    }

    public static Guid UserId { get; } = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static Guid OtherUserId { get; } = Guid.Parse("99999999-8888-7777-6666-555555555555");

    public string Scratch { get; }

    public FakeCallers Callers { get; } = new() { Caller = new Caller(UserId, "mika", false) };

    public ManualTime Time { get; } = new();

    public FakeMediaLibrary Library { get; } = new();

    public FakeFfmpeg Ffmpeg { get; } = new();

    public PlaybackTracker Tracker { get; }

    public SubtitleStore Subtitles { get; }

    public MediaCutter Cutter { get; }

    public PluginServices Build(params string[] pageFiles) => new(
        new PageFiles(new FakePageFileSource(pageFiles)),
        Callers,
        new ServerInfo("Test server", "12.4.0"),
        Tracker,
        Library,
        Subtitles,
        Cutter,
        Time);

    /// <summary>Makes a file in the scratch folder and returns its path.</summary>
    public string WriteFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(Scratch, name);
        File.WriteAllBytes(path, (encoding ?? Encoding.UTF8).GetBytes(content));
        return path;
    }

    public void Dispose()
    {
        Subtitles.Dispose();
        Cutter.Dispose();
        Directory.Delete(Scratch, recursive: true);
    }
}

/// <summary>Videos for tests.</summary>
internal static class Sample
{
    public static Guid ItemId { get; } = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public static MediaStreamInfo Stream(string key, StreamKind kind, int index, int ordinal, string codec, string? language = null, string? path = null, string? title = null) =>
        new(key, kind, index, ordinal, codec, language, title, ordinal < 0, false, false, path);

    /// <summary>
    /// An episode as Jellyfin 12.4 numbers it: the subtitle file next to the video first, then the
    /// streams inside it.
    /// </summary>
    public static MediaInfo Episode(string? videoPath, string? externalPath = null, Guid? itemId = null) => new(
        itemId ?? ItemId,
        "source-1",
        videoPath,
        "stamp-1",
        new MediaTitle("The Promise", "Sample Show", 1, 3, null),
        1_420_000,
        [
            Stream("x0a1b2c3d4e5f", StreamKind.Subtitle, 0, -1, "srt", "jpn", externalPath ?? "/media/ep3.ja.srt"),
            Stream("v0", StreamKind.Video, 1, 0, "h264"),
            Stream("a0", StreamKind.Audio, 2, 0, "aac", "jpn"),
            Stream("a1", StreamKind.Audio, 3, 1, "ac3", "eng", title: "Commentary"),
            Stream("s0", StreamKind.Subtitle, 4, 0, "ass", "eng", title: "Full"),
            Stream("s1", StreamKind.Subtitle, 5, 1, "subrip", "eng", title: "Signs"),
            Stream("s2", StreamKind.Subtitle, 6, 2, "pgssub", "jpn"),
        ]);

    public static PlaybackReport Report(
        string session = "session-1",
        Guid? userId = null,
        Guid? itemId = null,
        long positionMs = 0,
        bool paused = false,
        int? audio = 2,
        int? subtitle = null,
        string source = "source-1") =>
        new(session, userId ?? TestServices.UserId, "Living room", "mpv", itemId ?? ItemId, source, positionMs, paused, audio, subtitle);
}
