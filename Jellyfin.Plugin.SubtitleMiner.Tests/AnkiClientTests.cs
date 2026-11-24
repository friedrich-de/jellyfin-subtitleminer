using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class AnkiClientTests : IDisposable
{
    private static readonly AnkiTarget _target = new(new Uri("https://anki.example.org/"), "server-key");

    private readonly FakeAnkiHandler _network = new();
    private readonly AnkiClient _client;
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;

    public AnkiClientTests()
    {
        _client = new AnkiClient(_network, NullLogger<AnkiClient>.Instance);
    }

    public void Dispose() => _client.Dispose();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public async Task A_call_is_sent_in_version_6_with_the_key_in_the_body()
    {
        _network.AnswerWith(_ => """["Default","Mining"]""");

        var answer = await _client.InvokeAsync(_target, "deckNames", null, AnkiClient.CallLimit, _none);

        Assert.Equal(AnkiOutcome.Answered, answer.Outcome);
        Assert.Null(answer.Error);
        Assert.Equal("Mining", answer.Result[1].GetString());
        var sent = Assert.Single(_network.Requests);
        Assert.Equal(_target.Address, sent.Address);
        Assert.Equal("""{"action":"deckNames","version":6,"key":"server-key"}""", sent.Body);

        await _client.InvokeAsync(_target with { Key = string.Empty }, "findNotes", Json("""{"query":"added:1"}"""), AnkiClient.CallLimit, _none);
        Assert.Equal("""{"action":"findNotes","version":6,"params":{"query":"added:1"}}""", _network.Requests[1].Body);
    }

    [Fact]
    public async Task AnkiConnects_own_error_comes_back_as_it_said_it()
    {
        _network.OnRequest = (_, _) => Task.FromResult(FakeAnkiHandler.Answer("""{"result":null,"error":"valid api key must be provided"}"""));

        var answer = await _client.InvokeAsync(_target, "version", null, AnkiClient.CallLimit, _none);

        Assert.Equal((AnkiOutcome.Answered, "valid api key must be provided"), (answer.Outcome, answer.Error));
    }

    [Theory]
    [InlineData("<html>Welcome to my router</html>", HttpStatusCode.OK)]
    [InlineData("""{"result":6}""", HttpStatusCode.OK)]
    [InlineData("""6""", HttpStatusCode.OK)]
    [InlineData("""{"result":null,"error":null}""", HttpStatusCode.InternalServerError)]
    [InlineData("", HttpStatusCode.Found)]
    public async Task What_does_not_answer_as_AnkiConnect_is_told_apart(string body, HttpStatusCode status)
    {
        _network.OnRequest = (_, _) => Task.FromResult(FakeAnkiHandler.Answer(body, status));

        var answer = await _client.InvokeAsync(_target, "version", null, AnkiClient.CallLimit, _none);

        Assert.Equal(AnkiOutcome.NotAnkiConnect, answer.Outcome);
    }

    [Fact]
    public async Task An_answer_too_large_to_be_AnkiConnects_is_not_read()
    {
        var large = "{\"result\":\"" + new string('x', AnkiClient.MaxAnswerBytes) + "\",\"error\":null}";
        _network.OnRequest = (_, _) => Task.FromResult(FakeAnkiHandler.Answer(large));

        Assert.Equal(AnkiOutcome.NotAnkiConnect, (await _client.InvokeAsync(_target, "notesInfo", null, AnkiClient.CallLimit, _none)).Outcome);
    }

    [Fact]
    public async Task No_answer_and_a_slow_answer_are_told_apart()
    {
        _network.OnRequest = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");
        Assert.Equal(AnkiOutcome.Unreachable, (await _client.InvokeAsync(_target, "version", null, AnkiClient.CallLimit, _none)).Outcome);

        _network.OnRequest = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return FakeAnkiHandler.Answer("{}");
        };
        Assert.Equal(AnkiOutcome.TimedOut, (await _client.InvokeAsync(_target, "sync", null, TimeSpan.FromMilliseconds(50), _none)).Outcome);
    }

    [Fact]
    public async Task A_call_that_found_the_collection_busy_is_made_once_more()
    {
        var answers = new Queue<string>([
            """{"result":null,"error":"The collection is busy (syncing); try again shortly"}""",
            """{"result":1700000000001,"error":null}""",
            """{"result":null,"error":"The collection is busy (syncing); try again shortly"}""",
            """{"result":null,"error":"The collection is busy (syncing); try again shortly"}""",
        ]);
        _network.OnRequest = (_, _) => Task.FromResult(FakeAnkiHandler.Answer(answers.Dequeue()));

        var first = await _client.InvokeAsync(_target, "addNote", Json("""{"note":{}}"""), AnkiClient.CallLimit, _none);
        Assert.Equal((1700000000001, null), (first.Result.GetInt64(), first.Error));

        // Busy twice: the user is told.
        var second = await _client.InvokeAsync(_target, "addNote", Json("""{"note":{}}"""), AnkiClient.CallLimit, _none);
        Assert.StartsWith("The collection is busy", second.Error, StringComparison.Ordinal);
        Assert.Equal(4, _network.Requests.Count);
    }

    [Fact]
    public async Task A_file_is_sent_as_base64_under_its_name()
    {
        _network.AnswerWith(_ => "\"stored-name.jpg\"");

        var answer = await _client.StoreMediaAsync(_target, "subtitle-miner_a.jpg", Encoding.ASCII.GetBytes("picture"), _none);

        Assert.Equal("stored-name.jpg", answer.Result.GetString());
        var parameters = Assert.Single(_network.Requests).Json.GetProperty("params");
        Assert.Equal("storeMediaFile", _network.Requests[0].Action);
        Assert.Equal("subtitle-miner_a.jpg", parameters.GetProperty("filename").GetString());
        Assert.Equal("picture", Encoding.ASCII.GetString(parameters.GetProperty("data").GetBytesFromBase64()));
    }

    [Fact]
    public void The_server_follows_no_redirects_and_keeps_no_cookies()
    {
        using var handler = AnkiClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }
}
