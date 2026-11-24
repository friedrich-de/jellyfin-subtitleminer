using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Subtitles;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class SubtitleStoreTests : IDisposable
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,000\n昨日の話\n";
    private const string Ass = "[Events]\nDialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,full line\n";
    private const string Signs = "1\n00:00:05,000 --> 00:00:06,000\nsign line\n";

    private readonly TestServices _test = new();
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;

    public void Dispose() => _test.Dispose();

    /// <summary>An ffmpeg that writes the tracks of the sample episode into the files it is asked for.</summary>
    private void FfmpegWritesTracks()
    {
        _test.Ffmpeg.OnRun = (arguments, _) =>
        {
            for (var i = 0; i < arguments.Count - 1; i++)
            {
                if (arguments[i] == "-map")
                {
                    // -map 0:s:N -c:s format -f format path
                    File.WriteAllText(arguments[i + 6], arguments[i + 1] == "0:s:0" ? Ass : Signs);
                }
            }

            return Task.FromResult(true);
        };
    }

    private MediaInfo Episode() => Sample.Episode(_test.WriteFile("ep3.mkv", "video"), _test.WriteFile("ep3.ja.srt", Srt));

    [Fact]
    public async Task A_subtitle_file_next_to_the_video_is_read_without_ffmpeg()
    {
        var media = Episode();

        var document = await _test.Subtitles.GetAsync(media, media.Streams[0], _none);

        Assert.Equal("昨日の話", Assert.Single(document!.Lines).Text);
        Assert.Empty(_test.Ffmpeg.Runs);
    }

    [Fact]
    public async Task A_subtitle_file_is_read_again_only_after_it_changed()
    {
        var media = Episode();
        var first = await _test.Subtitles.GetAsync(media, media.Streams[0], _none);
        Assert.Same(first, await _test.Subtitles.GetAsync(media, media.Streams[0], _none));

        File.WriteAllText(media.Streams[0].Path!, Srt.Replace("昨日の話", "明日の話", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(media.Streams[0].Path!, DateTime.UtcNow.AddSeconds(5));

        var second = await _test.Subtitles.GetAsync(media, media.Streams[0], _none);
        Assert.Equal("明日の話", Assert.Single(second!.Lines).Text);
    }

    [Fact]
    public async Task The_text_tracks_inside_a_video_are_cut_out_in_one_pass_and_kept()
    {
        FfmpegWritesTracks();
        var media = Episode();

        var full = await _test.Subtitles.GetAsync(media, media.Stream("s0", StreamKind.Subtitle)!, _none);
        var signs = await _test.Subtitles.GetAsync(media, media.Stream("s1", StreamKind.Subtitle)!, _none);
        var again = await _test.Subtitles.GetAsync(media, media.Stream("s0", StreamKind.Subtitle)!, _none);

        Assert.Equal(("ass", "full line"), (full!.Format, full.Lines[0].Text));
        Assert.Equal(("srt", "sign line"), (signs!.Format, signs.Lines[0].Text));
        Assert.Same(full, again);

        // ffmpeg counts the subtitle tracks inside the file from 0, whatever numbers Jellyfin gives them.
        var run = Assert.Single(_test.Ffmpeg.Runs);
        var maps = run.Select((argument, index) => (argument, index)).Where(pair => pair.argument == "-map").Select(pair => run[pair.index + 1]);
        Assert.Equal(["0:s:0", "0:s:1"], maps);
        Assert.Equal(media.Path, run[run.ToList().IndexOf("-i") + 1]);
        Assert.Contains("-nostdin", run);

        // Nothing is left of the files the tracks were cut into.
        Assert.False(Directory.Exists(Path.Combine(_test.Scratch, "work")) && Directory.EnumerateFileSystemEntries(Path.Combine(_test.Scratch, "work")).Any());
    }

    [Fact]
    public async Task Two_pages_that_ask_at_once_share_one_cut()
    {
        var release = new TaskCompletionSource();
        _test.Ffmpeg.OnRun = async (arguments, _) =>
        {
            await release.Task;
            File.WriteAllText(arguments[13], Ass);
            return true;
        };
        var media = Episode();
        var track = media.Stream("s0", StreamKind.Subtitle)!;

        var first = _test.Subtitles.GetAsync(media, track, _none);
        var second = _test.Subtitles.GetAsync(media, track, _none);
        release.SetResult();

        Assert.NotNull(await first);
        Assert.Same(await first, await second);
        Assert.Single(_test.Ffmpeg.Runs);
    }

    [Fact]
    public async Task A_page_that_gives_up_does_not_stop_the_cut_for_the_others()
    {
        var release = new TaskCompletionSource();
        _test.Ffmpeg.OnRun = async (arguments, _) =>
        {
            await release.Task;
            File.WriteAllText(arguments[13], Ass);
            return true;
        };
        var media = Episode();
        var track = media.Stream("s0", StreamKind.Subtitle)!;
        using var impatient = new CancellationTokenSource();

        var first = _test.Subtitles.GetAsync(media, track, impatient.Token);
        var second = _test.Subtitles.GetAsync(media, track, _none);
        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();

        Assert.Equal("full line", (await second)!.Lines[0].Text);
    }

    [Fact]
    public async Task When_one_track_fails_the_pass_each_track_is_tried_alone()
    {
        _test.Ffmpeg.OnRun = (arguments, _) =>
        {
            var maps = arguments.Count(argument => argument == "-map");
            if (maps == 1 && arguments.Contains("0:s:0"))
            {
                File.WriteAllText(arguments[^1], Ass);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        var media = Episode();

        Assert.NotNull(await _test.Subtitles.GetAsync(media, media.Stream("s0", StreamKind.Subtitle)!, _none));

        // The track that failed is not tried again on every request: a pass reads the whole video file.
        Assert.Null(await _test.Subtitles.GetAsync(media, media.Stream("s1", StreamKind.Subtitle)!, _none));
        Assert.Null(await _test.Subtitles.GetAsync(media, media.Stream("s1", StreamKind.Subtitle)!, _none));
        Assert.Equal(3, _test.Ffmpeg.Runs.Count);
    }

    [Fact]
    public async Task A_changed_video_file_is_cut_again()
    {
        FfmpegWritesTracks();
        var media = Episode();
        await _test.Subtitles.GetAsync(media, media.Stream("s0", StreamKind.Subtitle)!, _none);

        await _test.Subtitles.GetAsync(media with { Stamp = "stamp-2" }, media.Stream("s0", StreamKind.Subtitle)!, _none);

        Assert.Equal(2, _test.Ffmpeg.Runs.Count);
    }

    [Fact]
    public async Task What_cannot_be_read_gives_nothing()
    {
        FfmpegWritesTracks();
        var media = Episode();

        // A track made of pictures, a track inside a video the server has no file of, a file that is gone.
        Assert.Null(await _test.Subtitles.GetAsync(media, media.Stream("s2", StreamKind.Subtitle)!, _none));
        Assert.Null(await _test.Subtitles.GetAsync(media with { Path = null }, media.Stream("s0", StreamKind.Subtitle)!, _none));
        File.Delete(media.Streams[0].Path!);
        Assert.Null(await _test.Subtitles.GetAsync(media, media.Streams[0], _none));
        Assert.Empty(_test.Ffmpeg.Runs);
    }

    [Fact]
    public async Task A_shift_jis_file_next_to_the_video_is_read()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var media = Sample.Episode(null, _test.WriteFile("ep3.sjis.srt", Srt, Encoding.GetEncoding(932)));

        var document = await _test.Subtitles.GetAsync(media, media.Streams[0], _none);

        Assert.Equal("昨日の話", Assert.Single(document!.Lines).Text);
    }
}
