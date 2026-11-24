using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class AnkiControllerTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;

    public AnkiControllerTests()
    {
        _test.SignInAs(administrator: true);
        _test.UseServerAnki();
    }

    public void Dispose() => _test.Dispose();

    private AnkiController Controller() => new(_test.Build())
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

    private static AnkiCall Call(string action, string? parameters = null) =>
        new() { Action = action, Params = parameters is null ? null : JsonDocument.Parse(parameters).RootElement };

    [Fact]
    public async Task A_call_reaches_the_users_AnkiConnect_with_the_key_and_its_answer_comes_back_whole()
    {
        _test.AnkiNetwork.AnswerWith(_ => "[1700000000000]");

        var result = await Controller().PostCall(Call("findNotes", """{"query":"added:1"}"""), _none);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("""{"result":[1700000000000],"error":null}""", content.Content);
        var sent = Assert.Single(_test.AnkiNetwork.Requests);
        Assert.Equal("https://anki.example.org/", sent.Address.ToString());
        Assert.Equal("server-key", sent.Json.GetProperty("key").GetString());
        Assert.Equal("added:1", sent.Json.GetProperty("params").GetProperty("query").GetString());
    }

    [Fact]
    public async Task AnkiConnects_error_comes_back_in_its_own_words()
    {
        _test.AnkiNetwork.OnRequest = (_, _) => Task.FromResult(FakeAnkiHandler.Answer("""{"result":null,"error":"cannot create note because it is a duplicate"}"""));

        var content = Assert.IsType<ContentResult>(await Controller().PostCall(Call("addNote", """{"note":{"deckName":"Mining"}}"""), _none));

        Assert.Equal("""{"result":null,"error":"cannot create note because it is a duplicate"}""", content.Content);
    }

    [Theory]
    [InlineData("deleteNotes")]
    [InlineData("deleteDecks")]
    [InlineData("storeMediaFile")]
    [InlineData("retrieveMediaFile")]
    [InlineData("multi")]
    [InlineData("guiBrowse")]
    [InlineData("exportPackage")]
    [InlineData("DeckNames")]
    public async Task Calls_that_delete_open_windows_or_touch_files_do_not_go_through(string action)
    {
        Problem(await Controller().PostCall(Call(action), _none), StatusCodes.Status400BadRequest);

        Assert.Empty(_test.AnkiNetwork.Requests);
    }

    [Theory]
    [InlineData("addNote", """{"note":{"deckName":"Mining","picture":[{"url":"http://192.168.1.1/","filename":"a.jpg","fields":["Picture"]}]}}""")]
    [InlineData("updateNoteFields", """{"note":{"id":1,"fields":{},"audio":[{"path":"/etc/passwd","filename":"a.mp3","fields":["Audio"]}]}}""")]
    [InlineData("addNote", """{"note":{"video":[]}}""")]
    public async Task A_note_may_not_bring_files_that_AnkiConnect_would_fetch_or_read(string action, string parameters)
    {
        Problem(await Controller().PostCall(Call(action, parameters), _none), StatusCodes.Status400BadRequest);

        Assert.Empty(_test.AnkiNetwork.Requests);
    }

    [Fact]
    public async Task Nothing_is_called_for_a_user_whose_cards_do_not_go_to_a_server()
    {
        var settings = _test.Settings.Load(TestServices.UserId);
        settings.Where = "device";
        _test.Settings.Save(TestServices.UserId, settings);
        Problem(await Controller().PostCall(Call("version"), _none), StatusCodes.Status409Conflict);

        settings.Where = "server";
        settings.ServerAddress = string.Empty;
        _test.Settings.Save(TestServices.UserId, settings);
        Problem(await Controller().PostCall(Call("version"), _none), StatusCodes.Status409Conflict);

        Assert.Empty(_test.AnkiNetwork.Requests);
    }

    [Fact]
    public async Task A_user_who_is_not_an_administrator_is_stopped_once_the_leave_is_taken_back()
    {
        _test.SignInAs(administrator: false);
        _test.Shared.AnkiThroughServerForEveryone = true;
        Assert.IsType<ContentResult>(await Controller().PostCall(Call("version"), _none));

        _test.Shared.AnkiThroughServerForEveryone = false;
        Problem(await Controller().PostCall(Call("version"), _none), StatusCodes.Status403Forbidden);

        Assert.Single(_test.AnkiNetwork.Requests);
    }

    [Fact]
    public async Task An_address_that_does_not_answer_gets_a_sentence_that_names_it()
    {
        _test.AnkiNetwork.OnRequest = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");

        var problem = Problem(await Controller().PostCall(Call("version"), _none), StatusCodes.Status502BadGateway);

        Assert.Contains("anki.example.org", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_user_has_only_a_few_calls_on_their_way_at_a_time()
    {
        var release = new TaskCompletionSource();
        _test.AnkiNetwork.OnRequest = async (_, _) =>
        {
            await release.Task;
            return FakeAnkiHandler.Answer("""{"result":6,"error":null}""");
        };

        var waiting = Enumerable.Range(0, AnkiGate.MaxCallsPerUser).Select(_ => Controller().PostCall(Call("version"), _none)).ToList();
        var controller = Controller();
        Problem(await controller.PostCall(Call("version"), _none), StatusCodes.Status429TooManyRequests);
        Assert.Equal("1", controller.Response.Headers.RetryAfter);

        release.SetResult();
        foreach (var call in waiting)
        {
            Assert.IsType<ContentResult>(await call);
        }

        Assert.IsType<ContentResult>(await Controller().PostCall(Call("version"), _none));
    }
}
