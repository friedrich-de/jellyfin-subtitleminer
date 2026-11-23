using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class MediaCutterTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;
    private readonly MediaInfo _media;

    public MediaCutterTests()
    {
        _media = Sample.Episode(_test.WriteFile("ep3.mkv", "video"));
    }

    public void Dispose() => _test.Dispose();

    private static string After(IReadOnlyList<string> arguments, string option) => arguments[arguments.ToList().IndexOf(option) + 1];

    private static PictureRequest Still(string format = "jpeg", int width = 960, int quality = 85) => new(453_250, null, format, width, quality);

    private static AudioRequest Sound(long start = 445_000, long end = 451_500, string format = "mp3", bool normalize = false, MediaStreamInfo? track = null) =>
        new(start, end, format, 128, normalize, track);

    [Fact]
    public void A_still_is_one_frame_at_the_moment_asked_for_no_wider_than_wanted()
    {
        var arguments = MediaCutter.PictureArguments("/media/ep3.mkv", "/tmp/out.jpg", Still(), null);

        // The seek comes before the input, so ffmpeg jumps there instead of decoding from the start.
        Assert.True(arguments.IndexOf("-ss") < arguments.IndexOf("-i"));
        Assert.Equal("453.25", After(arguments, "-ss"));
        Assert.Equal("/media/ep3.mkv", After(arguments, "-i"));
        Assert.Equal("1", After(arguments, "-frames:v"));
        Assert.Equal("scale='min(960,iw)':-2", After(arguments, "-vf"));
        Assert.Equal("mjpeg", After(arguments, "-c:v"));
        Assert.Equal("6", After(arguments, "-q:v"));
        Assert.Equal("0:v:0", After(arguments, "-map"));
        Assert.Contains("-an", arguments);
        Assert.Contains("-nostdin", arguments);
        Assert.Equal("/tmp/out.jpg", arguments[^1]);
        Assert.DoesNotContain("-t", arguments);
    }

    [Theory]
    [InlineData(100, "2")]
    [InlineData(50, "16")]
    [InlineData(1, "31")]
    [InlineData(-5, "31")]
    [InlineData(900, "2")]
    public void Quality_runs_from_1_to_100_whatever_ffmpeg_calls_it(int quality, string jpegScale)
    {
        Assert.Equal(jpegScale, After(MediaCutter.PictureArguments("in", "out", Still(quality: quality), null), "-q:v"));
    }

    [Fact]
    public void A_moving_picture_runs_from_the_start_to_the_end_of_the_lines_but_not_for_long()
    {
        var moving = new PictureRequest(10_000, 13_500, "webp", 480, 70);
        var arguments = MediaCutter.PictureArguments("in", "out.webp", moving, null);

        Assert.Equal("10", After(arguments, "-ss"));
        Assert.Equal("3.5", After(arguments, "-t"));
        Assert.Equal("fps=12,scale='min(480,iw)':-2", After(arguments, "-vf"));
        Assert.Equal("libwebp", After(arguments, "-c:v"));
        Assert.Equal("0", After(arguments, "-loop"));
        Assert.DoesNotContain("-frames:v", arguments);

        var long1 = MediaCutter.PictureArguments("in", "out.webp", moving with { EndMs = 100_000 }, null);
        Assert.Equal("15", After(long1, "-t"));
    }

    [Theory]
    [InlineData("libsvtav1", "-preset")]
    [InlineData("libaom-av1", "-cpu-used")]
    public void An_avif_picture_uses_the_encoder_the_server_has(string encoder, string speedOption)
    {
        var arguments = MediaCutter.PictureArguments("in", "out.avif", Still("avif", quality: 80), encoder);

        Assert.Equal(encoder, After(arguments, "-c:v"));
        Assert.Equal("28", After(arguments, "-crf"));
        Assert.Contains(speedOption, arguments);
        Assert.Equal("avif", After(arguments, "-f"));
    }

    [Fact]
    public void Sound_is_cut_from_the_chosen_track_counted_as_ffmpeg_counts()
    {
        var commentary = _media.Stream("a1", StreamKind.Audio)!;
        var arguments = MediaCutter.AudioArguments("/media/ep3.mkv", "/tmp/out.mp3", Sound(track: commentary));

        Assert.Equal("445", After(arguments, "-ss"));
        Assert.Equal("6.5", After(arguments, "-t"));
        Assert.True(arguments.IndexOf("-t") < arguments.IndexOf("-i"));

        // Jellyfin calls this track number 3; inside the file it is the second audio track.
        Assert.Equal(3, commentary.Index);
        Assert.Equal("0:a:1", After(arguments, "-map"));
        Assert.Equal("libmp3lame", After(arguments, "-c:a"));
        Assert.Equal("128k", After(arguments, "-b:a"));
        Assert.Equal("2", After(arguments, "-ac"));
        Assert.Equal("afade=t=in:d=0.01", After(arguments, "-af"));
        Assert.Contains("-vn", arguments);
    }

    [Fact]
    public void Sound_comes_from_the_first_track_when_none_is_named_and_is_never_longer_than_a_minute()
    {
        var arguments = MediaCutter.AudioArguments("in", "out.ogg", Sound(start: 0, end: 600_000, format: "opus", normalize: true));

        Assert.Equal("0:a:0", After(arguments, "-map"));
        Assert.Equal("60", After(arguments, "-t"));
        Assert.Equal("libopus", After(arguments, "-c:a"));
        Assert.Equal("ogg", After(arguments, "-f"));
        Assert.Equal("afade=t=in:d=0.01,loudnorm=I=-16:TP=-1.5:LRA=11", After(arguments, "-af"));
    }

    [Fact]
    public async Task A_cut_gives_a_file_of_the_right_kind()
    {
        var picture = await _test.Cutter.PictureAsync(_media, Still("JPG"), TestServices.UserId, _none);
        var sound = await _test.Cutter.AudioAsync(_media, Sound(format: "opus"), TestServices.UserId, _none);

        Assert.Equal((CutStatus.Done, "image/jpeg", "jpg"), (picture.Status, picture.ContentType, picture.Extension));
        Assert.Equal((CutStatus.Done, "audio/ogg", "ogg"), (sound.Status, sound.ContentType, sound.Extension));
        Assert.True(File.Exists(picture.Path));
        Assert.EndsWith(".jpg", picture.Path, StringComparison.Ordinal);
        Assert.Equal(_media.Path, After(_test.Ffmpeg.Runs[0], "-i"));
    }

    [Fact]
    public async Task What_makes_no_sense_is_refused_before_ffmpeg_runs()
    {
        var refused = new[]
        {
            await _test.Cutter.PictureAsync(_media, Still("gif"), TestServices.UserId, _none),
            await _test.Cutter.PictureAsync(_media, new PictureRequest(5_000, 8_000, "jpeg", 640, 80), TestServices.UserId, _none),
            await _test.Cutter.PictureAsync(_media, new PictureRequest(5_000, 5_000, "webp", 640, 80), TestServices.UserId, _none),
            await _test.Cutter.PictureAsync(_media, new PictureRequest(-1, null, "webp", 640, 80), TestServices.UserId, _none),
            await _test.Cutter.PictureAsync(_media with { Path = null }, Still(), TestServices.UserId, _none),
            await _test.Cutter.AudioAsync(_media, Sound(format: "wav"), TestServices.UserId, _none),
            await _test.Cutter.AudioAsync(_media, Sound(start: 10, end: 10), TestServices.UserId, _none),
            await _test.Cutter.AudioAsync(_media with { Path = null }, Sound(), TestServices.UserId, _none),
            await _test.Cutter.AudioAsync(_media with { Streams = [.. _media.Streams.Where(s => s.Kind != StreamKind.Audio)] }, Sound(), TestServices.UserId, _none),
        };

        Assert.All(refused, result =>
        {
            Assert.Equal(CutStatus.Refused, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Message));
            Assert.Null(result.Path);
        });
        Assert.Empty(_test.Ffmpeg.Runs);
    }

    [Fact]
    public async Task Avif_is_refused_on_a_server_whose_ffmpeg_cannot_make_it_and_ffmpeg_is_asked_once()
    {
        _test.Ffmpeg.Encoders = " V....D libwebp              WebP \n";

        var first = await _test.Cutter.PictureAsync(_media, Still("avif"), TestServices.UserId, _none);
        var second = await _test.Cutter.PictureAsync(_media, Still("avif"), TestServices.UserId, _none);

        Assert.Equal(CutStatus.Refused, first.Status);
        Assert.Contains("avif", first.Message, StringComparison.Ordinal);
        Assert.Equal(CutStatus.Refused, second.Status);
        Assert.Equal(1, _test.Ffmpeg.Asked);
    }

    [Fact]
    public async Task A_failed_or_empty_cut_leaves_no_file_behind()
    {
        _test.Ffmpeg.OnRun = (arguments, _) =>
        {
            File.WriteAllBytes(arguments[^1], []);
            return Task.FromResult(true);
        };
        var empty = await _test.Cutter.PictureAsync(_media, Still(), TestServices.UserId, _none);

        _test.Ffmpeg.OnRun = (arguments, _) =>
        {
            File.WriteAllBytes(arguments[^1], [1]);
            return Task.FromResult(false);
        };
        var failed = await _test.Cutter.AudioAsync(_media, Sound(), TestServices.UserId, _none);

        Assert.Equal(CutStatus.Failed, empty.Status);
        Assert.Equal(CutStatus.Failed, failed.Status);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_test.Scratch, "work")));
    }

    [Fact]
    public async Task Two_cuts_run_at_a_time_and_the_rest_wait_their_turn()
    {
        var running = 0;
        var most = 0;
        var gate = new SemaphoreSlim(0);
        _test.Ffmpeg.OnRun = async (arguments, token) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await gate.WaitAsync(token);
            Interlocked.Decrement(ref running);
            File.WriteAllBytes(arguments[^1], [1]);
            return true;
        };

        var cuts = Enumerable.Range(0, 5).Select(_ => _test.Cutter.PictureAsync(_media, Still(), TestServices.UserId, _none)).ToList();
        await Task.Delay(100, _none);
        Assert.Equal(MediaCutter.Running, _test.Ffmpeg.Runs.Count);

        gate.Release(5);
        var results = await Task.WhenAll(cuts);

        Assert.All(results, result => Assert.Equal(CutStatus.Done, result.Status));
        Assert.Equal(MediaCutter.Running, most);
        gate.Dispose();
    }

    [Fact]
    public async Task One_user_cannot_fill_the_line_and_another_user_still_gets_in()
    {
        var gate = new SemaphoreSlim(0);
        _test.Ffmpeg.OnRun = async (arguments, token) =>
        {
            await gate.WaitAsync(token);
            File.WriteAllBytes(arguments[^1], [1]);
            return true;
        };

        var mine = Enumerable.Range(0, MediaCutter.MaxPerUser).Select(_ => _test.Cutter.PictureAsync(_media, Still(), TestServices.UserId, _none)).ToList();
        var tooMany = await _test.Cutter.PictureAsync(_media, Still(), TestServices.UserId, _none);
        var theirs = _test.Cutter.PictureAsync(_media, Still(), TestServices.OtherUserId, _none);

        Assert.Equal(CutStatus.Busy, tooMany.Status);
        gate.Release(MediaCutter.MaxPerUser + 1);
        Assert.Equal(CutStatus.Done, (await theirs).Status);
        Assert.All(await Task.WhenAll(mine), result => Assert.Equal(CutStatus.Done, result.Status));

        // The line is free again once the cuts are through.
        gate.Release();
        Assert.Equal(CutStatus.Done, (await _test.Cutter.PictureAsync(_media, Still(), TestServices.UserId, _none)).Status);
        gate.Dispose();
    }

    [Fact]
    public async Task A_page_that_goes_away_stops_its_cut_and_frees_its_place()
    {
        using var gone = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        _test.Ffmpeg.OnRun = async (arguments, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        };

        var cut = _test.Cutter.AudioAsync(_media, Sound(), TestServices.UserId, gone.Token);
        await started.Task;
        await gone.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cut);

        _test.Ffmpeg.OnRun = (arguments, _) =>
        {
            File.WriteAllBytes(arguments[^1], [1]);
            return Task.FromResult(true);
        };
        var results = await Task.WhenAll(Enumerable.Range(0, MediaCutter.MaxPerUser).Select(_ => _test.Cutter.AudioAsync(_media, Sound(), TestServices.UserId, _none)));
        Assert.All(results, result => Assert.Equal(CutStatus.Done, result.Status));
    }
}
