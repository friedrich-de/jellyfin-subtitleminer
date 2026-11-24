using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Jellyfin.Plugin.SubtitleMiner.Playback;
using Jellyfin.Plugin.SubtitleMiner.Settings;
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

/// <summary>The settings for everyone, held in memory.</summary>
internal sealed class FakeSharedSettings : ISharedSettings
{
    public bool AnkiThroughServerForEveryone { get; set; }

    public void SetAnkiThroughServerForEveryone(bool allowed) => AnkiThroughServerForEveryone = allowed;
}

/// <summary>A request an AnkiConnect got: where it went and its body.</summary>
internal sealed record AnkiRequest(Uri Address, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string Action => Json.GetProperty("action").GetString()!;
}

/// <summary>
/// The network as an AnkiConnect sees it: every request is remembered, and the test says what is answered. By
/// default it answers as AnkiConnect does, with a null result.
/// </summary>
internal sealed class FakeAnkiHandler : HttpMessageHandler
{
    private readonly object _lock = new();

    public List<AnkiRequest> Requests { get; } = [];

    /// <summary>Gets or sets what is answered to a request.</summary>
    public Func<AnkiRequest, CancellationToken, Task<HttpResponseMessage>> OnRequest { get; set; } = (_, _) => Task.FromResult(Answer("""{"result":null,"error":null}"""));

    public static HttpResponseMessage Answer(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Answers every request with a result, as AnkiConnect would.</summary>
    public void AnswerWith(Func<AnkiRequest, string> result) =>
        OnRequest = (request, _) => Task.FromResult(Answer("{\"result\":" + result(request) + ",\"error\":null}"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var seen = new AnkiRequest(request.RequestUri!, body);
        lock (_lock)
        {
            Requests.Add(seen);
        }

        return await OnRequest(seen, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Answers downloads with what a test gave for each address, or 404.</summary>
internal sealed class FakeDownloadHandler : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public List<Uri> Asked { get; } = [];

    /// <summary>Gets the addresses that cannot be reached, as a server that is not there.</summary>
    public HashSet<string> Unreachable { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Asked.Add(request.RequestUri!);
        if (Unreachable.Contains(request.RequestUri!.AbsoluteUri))
        {
            throw new HttpRequestException("Connection refused (dictionaries.example:443)");
        }

        return Task.FromResult(Files.TryGetValue(request.RequestUri!.AbsoluteUri, out var data)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
            : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });
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
        Keys = new KeyProtector(RandomNumberGenerator.GetBytes(32));
        Settings = new UserSettingsStore(Path.Combine(Scratch, "users"), Keys, NullLogger<UserSettingsStore>.Instance);
        AnkiClient = new AnkiClient(AnkiNetwork, NullLogger<AnkiClient>.Instance);
        Anki = new AnkiGate(Settings, Shared);
        Dictionaries = new DictionaryLibrary(Path.Combine(Scratch, "dictionaries"), NullLogger<DictionaryLibrary>.Instance);
        DictionaryDownloads = new DictionaryDownloader(DownloadNetwork);
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

    public KeyProtector Keys { get; }

    public UserSettingsStore Settings { get; }

    public FakeSharedSettings Shared { get; } = new();

    public FakeAnkiHandler AnkiNetwork { get; } = new();

    public AnkiClient AnkiClient { get; }

    /// <summary>Gets the one gate of the server, as there is one in Jellyfin.</summary>
    public AnkiGate Anki { get; }

    public DictionaryLibrary Dictionaries { get; }

    /// <summary>Gets what answers the downloads of dictionaries.</summary>
    public FakeDownloadHandler DownloadNetwork { get; } = new();

    public DictionaryDownloader DictionaryDownloads { get; }

    /// <summary>Signs the test in as an administrator, or as a user who is not one.</summary>
    public void SignInAs(bool administrator) => Callers.Caller = new Caller(TestServices.UserId, "mika", administrator);

    /// <summary>Makes the user's cards go to an AnkiConnect on a server.</summary>
    public void UseServerAnki(string address = "https://anki.example.org/", string key = "server-key")
    {
        var settings = Settings.Load(UserId);
        settings.Where = AnkiWhere.Server;
        settings.ServerAddress = address;
        settings.ServerKey = Keys.Protect(key);
        Settings.Save(UserId, settings);
    }

    public PluginServices Build(params string[] pageFiles) => new(
        new PageFiles(new FakePageFileSource(pageFiles)),
        Callers,
        new ServerInfo("Test server", "12.4.0"),
        Tracker,
        Library,
        Subtitles,
        Cutter,
        Settings,
        Shared,
        AnkiClient,
        Anki,
        new DictionaryServices(Dictionaries, DictionaryDownloads, Path.Combine(Scratch, "dictionary-uploads")),
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
        AnkiClient.Dispose();
        Dictionaries.Dispose();
        DictionaryDownloads.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
