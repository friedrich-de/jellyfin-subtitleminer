using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Playback;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class WatchesControllerTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;

    public WatchesControllerTests()
    {
        _test.Library.Add(TestServices.UserId, Sample.Episode("/media/ep3.mkv"));
    }

    public void Dispose() => _test.Dispose();

    private WatchesController Controller(Stream? body = null, TimeSpan? beat = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
        };
        if (body is not null)
        {
            context.Response.Body = body;
        }

        return new WatchesController(_test.Build())
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            BeatEvery = beat ?? WatchesController.Beat,
        };
    }

    private async Task<WatchListView> ListAsync() => Assert.IsType<WatchListView>((await Controller().GetWatches()).Value);

    /// <summary>Reads the stream up to the next empty row, which ends an event.</summary>
    private async Task<string> NextAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(_none);
        patience.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var row = await reader.ReadLineAsync(patience.Token) ?? throw new EndOfStreamException("The stream ended.");
            if (row.Length == 0)
            {
                return text.ToString();
            }

            text.Append(row).Append('\n');
        }
    }

    private static JsonElement Data(string @event)
    {
        var rows = @event.TrimEnd('\n').Split('\n');
        Assert.Equal("event: watches", rows[0]);
        Assert.StartsWith("data: ", rows[1], StringComparison.Ordinal);
        Assert.Equal(2, rows.Length);
        return JsonDocument.Parse(rows[1][6..]).RootElement;
    }

    [Fact]
    public async Task A_playing_episode_is_listed_with_its_name_its_tracks_and_what_the_player_chose()
    {
        _test.Tracker.Report(Sample.Report(positionMs: 60_000, audio: 3, subtitle: 4));
        _test.Time.AdvanceMs(2_500);

        var watch = Assert.Single((await ListAsync()).Watches);

        Assert.True(watch.Playing);
        Assert.False(watch.Paused);
        Assert.Equal(62_500, watch.PositionMs);
        Assert.Equal(1_420_000, watch.RuntimeMs);
        Assert.Null(watch.StoppedAgoMs);
        Assert.Equal(("Living room", "mpv"), (watch.Player, watch.Client));
        Assert.Equal(("The Promise", "Sample Show", 1, 3), (watch.Name, watch.SeriesName, watch.Season, watch.Episode));
        Assert.Equal(Sample.ItemId.ToString("N"), watch.ItemId);
        Assert.Equal("source-1", watch.MediaSourceId);
        Assert.True(watch.CanCut);

        // The player names tracks by Jellyfin's numbers, which move. The page gets the names that stay.
        Assert.Equal("a1", watch.AudioKey);
        Assert.Equal("s0", watch.SubtitleKey);
        Assert.Equal(["x0a1b2c3d4e5f", "s0", "s1", "s2"], watch.Subtitles.Select(track => track.Key));
        Assert.Equal([true, true, true, false], watch.Subtitles.Select(track => track.Readable));
        Assert.Equal([true, false, false, false], watch.Subtitles.Select(track => track.External));
        Assert.Equal(["a0", "a1"], watch.Audio.Select(track => track.Key));
        Assert.Equal(("eng", "Commentary", "ac3"), (watch.Audio[1].Language, watch.Audio[1].Title, watch.Audio[1].Codec));
    }

    [Fact]
    public async Task A_paused_episode_stays_where_it_is_and_a_stopped_one_says_how_long_ago()
    {
        _test.Tracker.Report(Sample.Report(positionMs: 60_000, paused: true));
        _test.Time.AdvanceMs(30_000);

        var paused = Assert.Single((await ListAsync()).Watches);
        Assert.True(paused.Paused);
        Assert.Equal(60_000, paused.PositionMs);

        _test.Tracker.Stop("session-1", 61_000);
        _test.Time.AdvanceMs(90_000);

        var stopped = Assert.Single((await ListAsync()).Watches);
        Assert.False(stopped.Playing);
        Assert.Equal(61_000, stopped.PositionMs);
        Assert.Equal(90_000, stopped.StoppedAgoMs);
    }

    [Fact]
    public async Task A_player_that_shows_no_subtitles_or_a_track_the_video_does_not_have_names_none()
    {
        _test.Tracker.Report(Sample.Report(audio: 42, subtitle: null));

        var watch = Assert.Single((await ListAsync()).Watches);

        Assert.Null(watch.SubtitleKey);
        Assert.Null(watch.AudioKey);
    }

    [Fact]
    public async Task A_video_without_a_file_on_the_server_can_show_only_its_subtitle_files()
    {
        _test.Library.Clear();
        _test.Library.Add(TestServices.UserId, Sample.Episode(null));
        _test.Tracker.Report(Sample.Report());

        var watch = Assert.Single((await ListAsync()).Watches);

        Assert.False(watch.CanCut);
        Assert.Equal([true, false, false, false], watch.Subtitles.Select(track => track.Readable));
    }

    [Fact]
    public async Task Nobody_sees_what_another_user_watches_or_a_video_they_may_no_longer_see()
    {
        var other = Guid.NewGuid();
        _test.Library.Add(TestServices.OtherUserId, Sample.Episode("/media/other.mkv", itemId: other));
        _test.Tracker.Report(Sample.Report(session: "theirs", userId: TestServices.OtherUserId, itemId: other));
        Assert.Empty((await ListAsync()).Watches);

        // The user watched it, then lost the library it is in.
        _test.Tracker.Report(Sample.Report());
        Assert.Single((await ListAsync()).Watches);
        _test.Library.Clear();
        Assert.Empty((await ListAsync()).Watches);
    }

    [Fact]
    public async Task A_version_that_is_gone_falls_back_to_the_video_s_first()
    {
        _test.Tracker.Report(Sample.Report(source: "source-that-was-replaced"));

        Assert.Equal("source-1", Assert.Single((await ListAsync()).Watches).MediaSourceId);
    }

    [Fact]
    public async Task A_request_without_a_user_gets_no_list_and_no_stream()
    {
        _test.Callers.Caller = null;
        _test.Tracker.Report(Sample.Report());

        var result = Assert.IsType<ObjectResult>((await Controller().GetWatches()).Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);

        using var body = new MemoryStream();
        var controller = Controller(body);
        await controller.GetEvents(_none);
        Assert.Equal(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task The_stream_sends_the_list_at_once_then_again_on_news_and_a_sign_of_life_in_between()
    {
        _test.Tracker.Report(Sample.Report(positionMs: 60_000));
        var pipe = new Pipe();
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(_none);
        var controller = Controller(pipe.Writer.AsStream(), TimeSpan.FromMilliseconds(40));

        var stream = controller.GetEvents(gone.Token);

        var first = Data(await NextAsync(reader)).GetProperty("Watches")[0];
        Assert.Equal("The Promise", first.GetProperty("Name").GetString());
        Assert.False(first.GetProperty("Paused").GetBoolean());

        // What has no value is left out, as in Jellyfin's own answers.
        Assert.False(first.TryGetProperty("StoppedAgoMs", out _));
        Assert.False(first.TryGetProperty("SubtitleKey", out _));

        Assert.Equal("text/event-stream", controller.Response.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.Equal("no", controller.Response.Headers["X-Accel-Buffering"]);

        // Nothing happens: a comment row, which a page's reader skips.
        Assert.Equal(": beat\n", await NextAsync(reader));

        _test.Tracker.Report(Sample.Report(positionMs: 60_000, paused: true));
        string next;
        do
        {
            next = await NextAsync(reader);
        }
        while (next == ": beat\n");
        Assert.True(Data(next).GetProperty("Watches")[0].GetProperty("Paused").GetBoolean());

        // The page goes away: the stream ends and the tracker has one listener less.
        Assert.Equal(1, _test.Tracker.ListenerCountOf(TestServices.UserId));
        await gone.CancelAsync();
        await stream.WaitAsync(TimeSpan.FromSeconds(10), _none);
        Assert.Equal(0, _test.Tracker.ListenerCountOf(TestServices.UserId));
    }

    [Fact]
    public async Task The_stream_says_so_when_a_player_went_silent_while_nothing_else_happened()
    {
        _test.Tracker.Report(Sample.Report(positionMs: 60_000));
        var pipe = new Pipe();
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(_none);
        var stream = Controller(pipe.Writer.AsStream(), TimeSpan.FromMilliseconds(40)).GetEvents(gone.Token);

        Assert.True(Data(await NextAsync(reader)).GetProperty("Watches")[0].GetProperty("Playing").GetBoolean());
        Assert.Equal(": beat\n", await NextAsync(reader));

        // The player lost its network: no stop comes. The stream ends the watch by itself.
        _test.Time.Advance(PlaybackTracker.SilentAfter + TimeSpan.FromSeconds(1));
        string next;
        do
        {
            next = await NextAsync(reader);
        }
        while (next == ": beat\n");
        Assert.False(Data(next).GetProperty("Watches")[0].GetProperty("Playing").GetBoolean());

        await gone.CancelAsync();
        await stream.WaitAsync(TimeSpan.FromSeconds(10), _none);
    }

    [Fact]
    public async Task One_user_cannot_hold_more_streams_open_than_the_limit()
    {
        var open = Enumerable.Range(0, WatchesController.MaxStreamsPerUser).Select(_ => _test.Tracker.Listen(TestServices.UserId)).ToList();
        using var body = new MemoryStream();
        var controller = Controller(body);

        await controller.GetEvents(_none);

        Assert.Equal(StatusCodes.Status429TooManyRequests, controller.Response.StatusCode);
        Assert.Equal(0, body.Length);
        Assert.Equal(WatchesController.MaxStreamsPerUser, _test.Tracker.ListenerCountOf(TestServices.UserId));
        open.ForEach(listener => listener.Dispose());
    }

    [Fact]
    public void Every_address_of_the_watches_needs_a_login()
    {
        Assert.NotNull(typeof(WatchesController).GetCustomAttributes(typeof(AuthorizeAttribute), false).SingleOrDefault());
        Assert.NotNull(typeof(ItemsController).GetCustomAttributes(typeof(AuthorizeAttribute), false).SingleOrDefault());
        Assert.DoesNotContain(
            typeof(WatchesController).GetMethods().Concat(typeof(ItemsController).GetMethods()),
            method => method.GetCustomAttributes(typeof(AllowAnonymousAttribute), false).Length > 0);
    }
}
