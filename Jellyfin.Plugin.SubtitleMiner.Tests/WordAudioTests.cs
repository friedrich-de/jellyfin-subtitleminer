using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class WordAudioTests : IDisposable
{
    /// <summary>An address as Yomitan's "Custom URL (JSON)" takes it, with a key in it as many sources want.</summary>
    private const string Source = "https://audio.example/audio/list?term={term}&reading={reading}&apiKey=secret-123";

    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 4, 0, 0, 1, 2, 3];

    private readonly TestServices _test = new();
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;

    public void Dispose() => _test.Dispose();

    private static string ListAddress(string term, string reading) =>
        "https://audio.example/audio/list?term=" + Uri.EscapeDataString(term) + "&reading=" + Uri.EscapeDataString(reading) + "&apiKey=secret-123";

    /// <summary>Makes the source answer for a word with a list of sounds, as Yomitan's custom audio JSON does.</summary>
    private void List(string term, string reading, params string[] urls)
    {
        var sources = new StringBuilder();
        foreach (var url in urls)
        {
            sources.Append(sources.Length == 0 ? string.Empty : ",").Append("{\"name\":\"one\",\"url\":\"").Append(url).Append("\"}");
        }

        var address = ListAddress(term, reading);
        _test.AudioNetwork.Files[address] = Encoding.UTF8.GetBytes("{\"type\":\"audioSourceList\",\"audioSources\":[" + sources + "]}");
        _test.AudioNetwork.Types[address] = "application/json";
    }

    private void Sound(string url, byte[] content, string? type = "audio/mpeg")
    {
        _test.AudioNetwork.Files[url] = content;
        if (type is not null)
        {
            _test.AudioNetwork.Types[url] = type;
        }
    }

    private WordAudioController Controller() => new(_test.Build())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider(),
            },
        },
    };

    private static ProblemDetails Problem(IActionResult? result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private async Task<WordAudioSaved> Set(string address)
    {
        _test.SignInAs(administrator: true);
        return (await Controller().PutWordAudio(new WordAudioSetting { Address = address }, _none)).Value!;
    }

    [Fact]
    public async Task The_first_sound_of_the_list_that_can_be_fetched_is_the_words()
    {
        List("食べる", "たべる", "https://audio.example/missing.mp3", "https://audio.example/nhk16/taberu.mp3", "https://audio.example/forvo/taberu.mp3");
        Sound("https://audio.example/nhk16/taberu.mp3", Mp3);
        await Set(Source);
        _test.AudioNetwork.Asked.Clear();

        var file = Assert.IsType<FileContentResult>(await Controller().GetWordAudio("食べる", "たべる", _none));

        Assert.Equal("audio/mpeg", file.ContentType);
        Assert.Equal(Mp3, file.FileContents);
        Assert.Equal(
            [ListAddress("食べる", "たべる"), "https://audio.example/missing.mp3", "https://audio.example/nhk16/taberu.mp3"],
            _test.AudioNetwork.Asked.ConvertAll(address => address.AbsoluteUri));
    }

    [Fact]
    public async Task An_address_that_answers_with_the_sound_itself_works_too()
    {
        // Yomitan's plain "Custom URL": the address is the sound.
        Sound("https://audio.example/sound/%E7%8C%AB.ogg", [1, 2, 3], type: null);
        await Set("https://audio.example/sound/{term}.ogg");

        var file = Assert.IsType<FileContentResult>(await Controller().GetWordAudio("猫", null, _none));

        Assert.Equal("audio/ogg", file.ContentType);
    }

    [Fact]
    public async Task A_word_without_a_sound_a_source_that_fails_and_no_source_each_say_so()
    {
        Problem(await Controller().GetWordAudio("猫", "ねこ", _none), StatusCodes.Status404NotFound);

        List("食べる", "たべる", "https://audio.example/nhk16/taberu.mp3");
        Sound("https://audio.example/nhk16/taberu.mp3", Mp3);
        await Set(Source);

        // The source knows the word but none of its sounds can be fetched.
        List("猫", "ねこ", "https://audio.example/gone.mp3", "https://audio.example/page.html");
        Sound("https://audio.example/page.html", Encoding.UTF8.GetBytes("<html></html>"), "text/html");
        Assert.Equal(WordAudioController.NoSound, Problem(await Controller().GetWordAudio("猫", "ねこ", _none), StatusCodes.Status404NotFound).Detail);

        // A wrong key, as the worker answers it.
        _test.AudioNetwork.Errors[ListAddress("犬", "いぬ")] = HttpStatusCode.Forbidden;
        Assert.Equal(WordAudioController.SourceFailed, Problem(await Controller().GetWordAudio("犬", "いぬ", _none), StatusCodes.Status502BadGateway).Detail);

        // Something that is neither a sound nor the list.
        _test.AudioNetwork.Files[ListAddress("鳥", "とり")] = Encoding.UTF8.GetBytes("{\"sounds\":[]}");
        _test.AudioNetwork.Types[ListAddress("鳥", "とり")] = "application/json";
        Problem(await Controller().GetWordAudio("鳥", "とり", _none), StatusCodes.Status502BadGateway);

        Problem(await Controller().GetWordAudio(new string('語', 65), null, _none), StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Only_an_administrator_sets_the_source_and_nobody_sees_its_key()
    {
        _test.SignInAs(administrator: false);
        Problem((await Controller().PutWordAudio(new WordAudioSetting { Address = Source }, _none)).Result, StatusCodes.Status403Forbidden);
        Assert.Equal(string.Empty, _test.Shared.WordAudioAddress);

        List("食べる", "たべる", "https://audio.example/nhk16/taberu.mp3");
        Sound("https://audio.example/nhk16/taberu.mp3", Mp3);
        var saved = await Set(Source);

        Assert.Equal("found", saved.Tried);
        Assert.True(saved.WordAudio.Set);
        Assert.Equal("audio.example", saved.WordAudio.Host);

        // Kept encrypted.
        Assert.StartsWith("enc:v1:", _test.Shared.WordAudioAddress, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-123", _test.Shared.WordAudioAddress, StringComparison.Ordinal);

        // A user who is not an administrator learns only that there is one.
        _test.SignInAs(administrator: false);
        var view = WordAudioController.View(_test.WordAudio, new Caller(TestServices.UserId, "mika", false));
        Assert.True(view.Set);
        Assert.Null(view.Host);

        // Taking it away.
        var removed = await Set("  ");
        Assert.Equal("none", removed.Tried);
        Assert.False(removed.WordAudio.Set);
        Assert.Equal(string.Empty, _test.Shared.WordAudioAddress);
    }

    [Fact]
    public async Task A_source_is_saved_even_when_its_try_fails_and_the_administrator_is_told()
    {
        _test.AudioNetwork.Errors[ListAddress("食べる", "たべる")] = HttpStatusCode.Forbidden;

        var saved = await Set(Source);

        Assert.Equal("failed", saved.Tried);
        Assert.True(saved.WordAudio.Set);
    }

    [Theory]
    [InlineData("https://audio.example/list?reading={reading}")]
    [InlineData("ftp://audio.example/{term}.mp3")]
    [InlineData("https://name:password@audio.example/{term}.mp3")]
    [InlineData("audio.example/{term}")]
    public async Task An_address_the_source_cannot_be_is_refused(string address)
    {
        _test.SignInAs(administrator: true);

        Problem((await Controller().PutWordAudio(new WordAudioSetting { Address = address }, _none)).Result, StatusCodes.Status400BadRequest);

        Assert.Equal(string.Empty, _test.Shared.WordAudioAddress);
    }

    [Fact]
    public async Task A_words_sound_goes_into_the_users_Anki_on_a_server_under_a_name_of_the_word()
    {
        List("食べる", "たべる", "https://audio.example/nhk16/taberu.mp3");
        Sound("https://audio.example/nhk16/taberu.mp3", Mp3);
        await Set(Source);
        _test.SignInAs(administrator: false);
        _test.Shared.AnkiThroughServerForEveryone = true;
        _test.UseServerAnki();
        _test.AnkiNetwork.AnswerWith(request => "\"" + request.Json.GetProperty("params").GetProperty("filename").GetString() + "\"");

        var view = (await Controller().PostWordAudioToAnki("食べる", "たべる", _none)).Value!;

        var sent = Assert.Single(_test.AnkiNetwork.Requests);
        Assert.Equal("storeMediaFile", sent.Action);
        Assert.Equal(WordAudioController.MediaName("食べる", "たべる") + ".mp3", view.FileName);
        Assert.Matches("^subtitle-miner_word_[0-9a-f]{8}\\.mp3$", view.FileName);
        Assert.Equal(Mp3, sent.Json.GetProperty("params").GetProperty("data").GetBytesFromBase64());
    }

    [Fact]
    public async Task Nothing_is_fetched_for_Anki_when_the_sound_could_not_go_anywhere()
    {
        List("食べる", "たべる", "https://audio.example/nhk16/taberu.mp3");
        Sound("https://audio.example/nhk16/taberu.mp3", Mp3);
        await Set(Source);
        _test.AudioNetwork.Asked.Clear();

        // No Anki on a server is set.
        Problem((await Controller().PostWordAudioToAnki("食べる", "たべる", _none)).Result, StatusCodes.Status409Conflict);

        Assert.Empty(_test.AudioNetwork.Asked);
        Assert.Empty(_test.AnkiNetwork.Requests);
    }

    [Fact]
    public void The_same_word_gets_the_same_name()
    {
        Assert.Equal(WordAudioController.MediaName("食べる", "たべる"), WordAudioController.MediaName(" 食べる", "たべる "));
        Assert.Equal(WordAudioController.MediaName("ねこ", null), WordAudioController.MediaName("ねこ", "ねこ"));
        Assert.NotEqual(WordAudioController.MediaName("食べる", "たべる"), WordAudioController.MediaName("食べる", "くべる"));
    }

    [Fact]
    public void The_word_and_its_reading_are_filled_in_escaped()
    {
        var address = WordAudioSource.Fill("https://audio.example/list?term={term}&reading={reading}&language={language}", "a&b", "x y")!;

        Assert.Equal("https://audio.example/list?term=a%26b&reading=x%20y&language=ja", address.AbsoluteUri);
    }

    [Fact]
    public async Task The_log_names_the_sources_host_and_never_its_address()
    {
        var log = new ListLogger();
        using var source = new WordAudioSource(_test.AudioNetwork, _test.Shared, _test.Keys, log);
        source.Set(Source);
        _test.AudioNetwork.Errors[ListAddress("犬", "いぬ")] = HttpStatusCode.Forbidden;
        _test.AudioNetwork.Unreachable.Add(ListAddress("猫", "ねこ"));
        _test.AudioNetwork.Files[ListAddress("鳥", "とり")] = Encoding.UTF8.GetBytes("nonsense");

        Assert.Equal(WordAudioOutcome.Failed, (await source.FindAsync("犬", "いぬ", _none)).Outcome);
        Assert.Equal(WordAudioOutcome.Failed, (await source.FindAsync("猫", "ねこ", _none)).Outcome);
        Assert.Equal(WordAudioOutcome.Failed, (await source.FindAsync("鳥", "とり", _none)).Outcome);

        Assert.Equal(3, log.Lines.Count);
        Assert.All(log.Lines, line =>
        {
            Assert.Contains("audio.example", line, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-123", line, StringComparison.Ordinal);
            Assert.DoesNotContain("apiKey", line, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Fetches a word's sound from a real Yomitan audio source, to check that its answers are read as they come. Set
    /// SUBTITLE_MINER_WORD_AUDIO to the source's address, key and all, to run it; the address is never written out.
    /// </summary>
    [Fact]
    public async Task A_real_audio_source_gives_the_sound_of_a_common_word()
    {
        var address = Environment.GetEnvironmentVariable("SUBTITLE_MINER_WORD_AUDIO");
        Assert.SkipWhen(string.IsNullOrEmpty(address), "SUBTITLE_MINER_WORD_AUDIO is not set.");
        Assert.True(WordAudioSource.TryRead(address, out var read, out _));
        using var source = new WordAudioSource(WordAudioSource.CreateHandler(), _test.Shared, _test.Keys, new ListLogger());
        source.Set(read);

        var sound = await source.FindAsync("食べる", "たべる", _none);

        Assert.Equal(WordAudioOutcome.Found, sound.Outcome);
        Assert.StartsWith("audio/", sound.ContentType, StringComparison.Ordinal);
        Assert.True(sound.Content!.Length > 1_000);
    }

    private sealed class ListLogger : ILogger<WordAudioSource>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception) + " " + exception);
        }
    }
}
