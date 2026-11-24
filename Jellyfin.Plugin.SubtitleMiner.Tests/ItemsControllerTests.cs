using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class ItemsControllerTests : IDisposable
{
    private const string Srt = "1\n00:00:01,000 --> 00:00:02,500\n昨日の話\n\n2\n00:00:03,000 --> 00:00:04,000\n♪ 歌 ♪\n";

    private readonly TestServices _test = new();
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;
    private readonly MediaInfo _media;

    public ItemsControllerTests()
    {
        _media = Sample.Episode(_test.WriteFile("ep3.mkv", "video"), _test.WriteFile("ep3.ja.srt", Srt));
        _test.Library.Add(TestServices.UserId, _media);
    }

    public void Dispose() => _test.Dispose();

    private ItemsController Controller() => new(_test.Build())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
            }
        }
    };

    private static ProblemDetails Problem(ActionResult? result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private static string After(IReadOnlyList<string> arguments, string option) => arguments[arguments.ToList().IndexOf(option) + 1];

    [Fact]
    public async Task The_lines_of_a_track_come_with_their_times_and_what_they_are()
    {
        var view = (await Controller().GetSubtitles(Sample.ItemId, "x0a1b2c3d4e5f", null, _none)).Value!;

        Assert.Equal(("x0a1b2c3d4e5f", "srt"), (view.Key, view.Format));
        Assert.Equal(2, view.Lines.Count);
        Assert.Equal((0, 1_000L, 2_500L, "昨日の話", "dialogue"), (view.Lines[0].Id, view.Lines[0].StartMs, view.Lines[0].EndMs, view.Lines[0].Text, view.Lines[0].Kind));
        Assert.Equal("song", view.Lines[1].Kind);
    }

    [Fact]
    public async Task A_video_of_another_user_s_library_answers_like_one_that_does_not_exist()
    {
        var theirs = Guid.NewGuid();
        _test.Library.Add(TestServices.OtherUserId, Sample.Episode(_media.Path, _media.Streams[0].Path, theirs));
        var controller = Controller();

        var hidden = Problem((await controller.GetSubtitles(theirs, "x0a1b2c3d4e5f", null, _none)).Result, StatusCodes.Status404NotFound);
        var missing = Problem((await controller.GetSubtitles(Guid.NewGuid(), "x0a1b2c3d4e5f", null, _none)).Result, StatusCodes.Status404NotFound);
        Assert.Equal(missing.Detail, hidden.Detail);

        Problem(await controller.GetPicture(theirs, 1_000, null, cancellationToken: _none), StatusCodes.Status404NotFound);
        Problem(await controller.GetAudio(theirs, 1_000, 2_000, cancellationToken: _none), StatusCodes.Status404NotFound);
        Assert.Empty(_test.Ffmpeg.Runs);
    }

    [Fact]
    public async Task A_request_without_a_user_gets_nothing()
    {
        _test.Callers.Caller = null;
        var controller = Controller();

        Problem((await controller.GetSubtitles(Sample.ItemId, "x0a1b2c3d4e5f", null, _none)).Result, StatusCodes.Status403Forbidden);
        Problem(await controller.GetPicture(Sample.ItemId, 1_000, null, cancellationToken: _none), StatusCodes.Status403Forbidden);
        Problem(await controller.GetAudio(Sample.ItemId, 1_000, 2_000, cancellationToken: _none), StatusCodes.Status403Forbidden);
        Assert.Empty(_test.Ffmpeg.Runs);
    }

    [Theory]
    [InlineData("nothing", StatusCodes.Status404NotFound, "no such subtitle track")]
    [InlineData("a0", StatusCodes.Status404NotFound, "no such subtitle track")]
    [InlineData("../../etc/passwd", StatusCodes.Status404NotFound, "no such subtitle track")]
    [InlineData("s2", StatusCodes.Status422UnprocessableEntity, "made of pictures")]
    public async Task A_track_that_is_not_there_or_cannot_be_read_is_refused_with_the_reason(string track, int status, string reason)
    {
        var problem = Problem((await Controller().GetSubtitles(Sample.ItemId, track, null, _none)).Result, status);

        Assert.Contains(reason, problem.Detail, StringComparison.Ordinal);
        Assert.Empty(_test.Ffmpeg.Runs);
    }

    [Fact]
    public async Task A_track_ffmpeg_cannot_cut_out_is_refused_and_another_version_of_the_video_is_not_found()
    {
        _test.Ffmpeg.OnRun = (_, _) => Task.FromResult(false);
        var controller = Controller();

        var problem = Problem((await controller.GetSubtitles(Sample.ItemId, "s0", null, _none)).Result, StatusCodes.Status422UnprocessableEntity);
        Assert.Contains("could not be read", problem.Detail, StringComparison.Ordinal);

        Problem((await controller.GetSubtitles(Sample.ItemId, "s0", "another-source", _none)).Result, StatusCodes.Status404NotFound);
        Assert.NotNull((await controller.GetSubtitles(Sample.ItemId, "x0a1b2c3d4e5f", "source-1", _none)).Value);
    }

    [Fact]
    public async Task A_picture_is_sent_as_a_file_that_is_gone_once_it_was_read()
    {
        var controller = Controller();

        var file = Assert.IsType<FileStreamResult>(await controller.GetPicture(Sample.ItemId, 453_250, null, cancellationToken: _none));

        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions);
        var run = Assert.Single(_test.Ffmpeg.Runs);
        Assert.Equal("453.25", After(run, "-ss"));
        Assert.Equal(_media.Path, After(run, "-i"));
        Assert.Equal("scale='min(960,iw)':-2", After(run, "-vf"));

        var path = run[^1];
        Assert.True(File.Exists(path));
        using (var memory = new MemoryStream())
        {
            await file.FileStream.CopyToAsync(memory, _none);
            Assert.Equal([1, 2, 3], memory.ToArray());
        }

        await file.FileStream.DisposeAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Sound_is_cut_from_the_track_asked_for_and_from_the_first_when_none_is_named()
    {
        var controller = Controller();

        var named = Assert.IsType<FileStreamResult>(await controller.GetAudio(Sample.ItemId, 445_000, 451_500, "a1", "opus", 96, true, cancellationToken: _none));
        await named.FileStream.DisposeAsync();
        var first = Assert.IsType<FileStreamResult>(await controller.GetAudio(Sample.ItemId, 445_000, 451_500, cancellationToken: _none));
        await first.FileStream.DisposeAsync();

        Assert.Equal("audio/ogg", named.ContentType);
        Assert.Equal("audio/mpeg", first.ContentType);
        Assert.Equal("0:a:1", After(_test.Ffmpeg.Runs[0], "-map"));
        Assert.Equal("96k", After(_test.Ffmpeg.Runs[0], "-b:a"));
        Assert.Contains("loudnorm", After(_test.Ffmpeg.Runs[0], "-af"), StringComparison.Ordinal);
        Assert.Equal("0:a:0", After(_test.Ffmpeg.Runs[1], "-map"));
        Assert.Equal("6.5", After(_test.Ffmpeg.Runs[1], "-t"));
    }

    [Fact]
    public async Task What_cannot_be_cut_is_refused_before_ffmpeg_starts()
    {
        var controller = Controller();

        // After the video's end, an audio track it does not have, a subtitle track as sound, an unknown format, the wrong way round.
        Problem(await controller.GetPicture(Sample.ItemId, 1_420_000, null, cancellationToken: _none), StatusCodes.Status422UnprocessableEntity);
        Problem(await controller.GetAudio(Sample.ItemId, 1_420_000, 1_421_000, cancellationToken: _none), StatusCodes.Status422UnprocessableEntity);
        Problem(await controller.GetAudio(Sample.ItemId, 1_000, 2_000, "a7", cancellationToken: _none), StatusCodes.Status404NotFound);
        Problem(await controller.GetAudio(Sample.ItemId, 1_000, 2_000, "s0", cancellationToken: _none), StatusCodes.Status404NotFound);
        Problem(await controller.GetPicture(Sample.ItemId, 1_000, null, "bmp", cancellationToken: _none), StatusCodes.Status422UnprocessableEntity);
        Problem(await controller.GetAudio(Sample.ItemId, 2_000, 1_000, cancellationToken: _none), StatusCodes.Status422UnprocessableEntity);

        Assert.Empty(_test.Ffmpeg.Runs);
    }

    [Fact]
    public async Task A_cut_that_fails_answers_500_and_too_many_at_once_ask_to_come_back()
    {
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _test.Ffmpeg.OnRun = (_, _) => hold.Task;
        var waiting = Enumerable.Range(0, MediaCutter.MaxPerUser)
            .Select(i => Controller().GetPicture(Sample.ItemId, 1_000 * (i + 1), null, cancellationToken: _none))
            .ToList();

        var controller = Controller();
        var busy = Problem(await controller.GetPicture(Sample.ItemId, 9_000, null, cancellationToken: _none), StatusCodes.Status429TooManyRequests);
        Assert.Contains("Try again", busy.Detail, StringComparison.Ordinal);
        Assert.Equal("2", controller.Response.Headers.RetryAfter);

        // ffmpeg gives up on all of them.
        hold.SetResult(false);
        foreach (var result in await Task.WhenAll(waiting))
        {
            Problem(result, StatusCodes.Status500InternalServerError);
        }
    }

    [Fact]
    public async Task A_picture_for_a_card_goes_straight_into_the_users_Anki_on_a_server()
    {
        _test.SignInAs(administrator: true);
        _test.UseServerAnki();
        _test.AnkiNetwork.AnswerWith(request => "\"" + request.Json.GetProperty("params").GetProperty("filename").GetString() + "\"");

        var view = (await Controller().PostPictureToAnki(Sample.ItemId, 2_250, null, "jpeg", 640, 85, null, _none)).Value!;

        var sent = Assert.Single(_test.AnkiNetwork.Requests);
        Assert.Equal("storeMediaFile", sent.Action);
        var parameters = sent.Json.GetProperty("params");
        Assert.Equal(view.FileName, parameters.GetProperty("filename").GetString());
        Assert.StartsWith("subtitle-miner_aaaaaaaa_2250_", view.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", view.FileName, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3 }, parameters.GetProperty("data").GetBytesFromBase64());
        Assert.Equal("scale='min(640,iw)':-2", After(Assert.Single(_test.Ffmpeg.Runs), "-vf"));

        // The cut is gone once it is sent.
        Assert.Empty(Directory.GetFiles(Path.Combine(_test.Scratch, "work")));
    }

    [Fact]
    public async Task Sound_for_a_card_goes_into_Anki_under_the_name_Anki_gives_it()
    {
        _test.SignInAs(administrator: true);
        _test.UseServerAnki();
        _test.AnkiNetwork.AnswerWith(_ => "\"renamed.mp3\"");

        var view = (await Controller().PostAudioToAnki(Sample.ItemId, 1_000, 6_000, "a1", "mp3", 128, false, null, _none)).Value!;

        Assert.Equal("renamed.mp3", view.FileName);
        Assert.Equal("0:a:1", After(Assert.Single(_test.Ffmpeg.Runs), "-map"));
    }

    [Fact]
    public void The_same_cut_gets_the_same_name_and_another_cut_another()
    {
        var one = ItemsController.MediaName(Sample.ItemId, null, 1_000, "audio|1000|6000|a1|mp3|128|False");

        Assert.Equal(one, ItemsController.MediaName(Sample.ItemId, null, 1_000, "audio|1000|6000|a1|mp3|128|False"));
        Assert.NotEqual(one, ItemsController.MediaName(Sample.ItemId, null, 1_000, "audio|1000|6000|a0|mp3|128|False"));
        Assert.Matches("^subtitle-miner_aaaaaaaa_1000_[0-9a-f]{8}$", one);
    }

    [Fact]
    public async Task Nothing_is_cut_for_Anki_when_it_could_not_go_anywhere()
    {
        // No server Anki set, then one the user may not use.
        Problem((await Controller().PostPictureToAnki(Sample.ItemId, 2_250, null, "jpeg", 640, 85, null, _none)).Result, StatusCodes.Status403Forbidden);
        _test.SignInAs(administrator: true);
        Problem((await Controller().PostAudioToAnki(Sample.ItemId, 1_000, 6_000, null, "mp3", 128, false, null, _none)).Result, StatusCodes.Status409Conflict);

        // A video the user may not see answers as before.
        _test.UseServerAnki();
        Problem((await Controller().PostPictureToAnki(Guid.NewGuid(), 2_250, null, "jpeg", 640, 85, null, _none)).Result, StatusCodes.Status404NotFound);

        Assert.Empty(_test.Ffmpeg.Runs);
        Assert.Empty(_test.AnkiNetwork.Requests);
    }

    [Fact]
    public async Task A_refused_key_is_put_into_words()
    {
        _test.SignInAs(administrator: true);
        _test.UseServerAnki();
        _test.AnkiNetwork.OnRequest = (_, _) => Task.FromResult(FakeAnkiHandler.Answer("""{"result":null,"error":"valid api key must be provided"}"""));

        var problem = Problem((await Controller().PostPictureToAnki(Sample.ItemId, 2_250, null, "jpeg", 640, 85, null, _none)).Result, StatusCodes.Status422UnprocessableEntity);

        Assert.Contains("did not take the key", problem.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_test.Scratch, "work")));
    }
}
