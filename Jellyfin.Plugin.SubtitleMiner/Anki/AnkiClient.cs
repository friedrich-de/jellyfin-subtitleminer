using System;
using System.Collections.Frozen;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleMiner.Anki;

/// <summary>
/// An AnkiConnect to call, and its key.
/// </summary>
/// <param name="Address">Where it answers.</param>
/// <param name="Key">Its key. Empty when it asks for none.</param>
internal sealed record AnkiTarget(Uri Address, string Key);

/// <summary>
/// How a call to AnkiConnect ended.
/// </summary>
internal enum AnkiOutcome
{
    /// <summary>AnkiConnect answered, with a result or with an error of its own.</summary>
    Answered,

    /// <summary>Nothing answered at the address.</summary>
    Unreachable,

    /// <summary>The call took too long.</summary>
    TimedOut,

    /// <summary>Something answered, but not as AnkiConnect does.</summary>
    NotAnkiConnect,
}

/// <summary>
/// What AnkiConnect answered.
/// </summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Result">AnkiConnect's result, when it answered.</param>
/// <param name="Error">AnkiConnect's error, when it answered with one.</param>
internal sealed record AnkiAnswer(AnkiOutcome Outcome, JsonElement Result, string? Error)
{
    public static AnkiAnswer Failed(AnkiOutcome outcome) => new(outcome, default, null);
}

/// <summary>
/// Calls an AnkiConnect for a user, in AnkiConnect's version 6, with the key in the body as AnkiConnect and
/// Yomitan send it. Redirects are not followed and answers are cut off at a size, so an address cannot be used
/// to reach further than it says.
/// </summary>
internal sealed class AnkiClient : IDisposable
{
    /// <summary>The version of AnkiConnect's interface the plugin speaks.</summary>
    internal const int Version = 6;

    /// <summary>The largest answer read.</summary>
    internal const int MaxAnswerBytes = 8 * 1024 * 1024;

    /// <summary>
    /// How long a call may take. A server AnkiConnect that is syncing holds a call for up to 30 seconds before it
    /// says it is busy, so this is longer.
    /// </summary>
    internal static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(45);

    /// <summary>How long a sync with AnkiWeb, or a large file, may take.</summary>
    internal static readonly TimeSpan LongCallLimit = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The calls the page may make through the server. They read decks, note types, notes and cards, and add and
    /// change notes. Nothing that deletes, nothing that opens windows, no files but the plugin's own cuts.
    /// </summary>
    internal static readonly FrozenSet<string> PageActions = FrozenSet.Create(
        StringComparer.Ordinal,
        "version",
        "requestPermission",
        "deckNames",
        "modelNames",
        "modelFieldNames",
        "findNotes",
        "notesInfo",
        "cardsInfo",
        "canAddNotes",
        "canAddNotesWithErrorDetail",
        "addNote",
        "updateNoteFields",
        "sync");

    private readonly HttpClient _http;
    private readonly ILogger<AnkiClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnkiClient"/> class.
    /// </summary>
    /// <param name="handler">What sends the requests. Tests give a fake one.</param>
    /// <param name="logger">The logger.</param>
    public AnkiClient(HttpMessageHandler handler, ILogger<AnkiClient> logger)
    {
        _http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxAnswerBytes };
        _logger = logger;
    }

    /// <summary>
    /// The handler the server uses: no redirects, no cookies, a short time to connect.
    /// </summary>
    /// <returns>The handler.</returns>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Makes one call.
    /// </summary>
    /// <param name="target">The AnkiConnect.</param>
    /// <param name="action">The action, as AnkiConnect names it.</param>
    /// <param name="parameters">Its parameters, or <c>null</c> for none.</param>
    /// <param name="limit">How long the call may take.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>The answer.</returns>
    public Task<AnkiAnswer> InvokeAsync(AnkiTarget target, string action, JsonElement? parameters, TimeSpan limit, CancellationToken cancellationToken) =>
        SendAsync(target, action, limit, writer =>
        {
            if (parameters is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } value)
            {
                writer.WritePropertyName("params");
                value.WriteTo(writer);
            }
        }, cancellationToken);

    /// <summary>
    /// Stores a file in Anki's media folder.
    /// </summary>
    /// <param name="target">The AnkiConnect.</param>
    /// <param name="fileName">The name to store it under.</param>
    /// <param name="content">The file.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>The answer. Its result is the name the file was stored under.</returns>
    public Task<AnkiAnswer> StoreMediaAsync(AnkiTarget target, string fileName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        SendAsync(target, "storeMediaFile", LongCallLimit, writer =>
        {
            writer.WriteStartObject("params");
            writer.WriteString("filename", fileName);
            writer.WriteBase64String("data", content.Span);
            writer.WriteEndObject();
        }, cancellationToken);

    /// <summary>
    /// Tells whether AnkiConnect said it could not do the call because its collection was in use, by a sync for
    /// one. Nothing was done then, so the call can be made again.
    /// </summary>
    internal static bool IsBusy(AnkiAnswer answer) =>
        answer.Outcome == AnkiOutcome.Answered && answer.Error?.StartsWith("The collection is busy", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<AnkiAnswer> SendAsync(AnkiTarget target, string action, TimeSpan limit, Action<Utf8JsonWriter> writeParameters, CancellationToken cancellationToken)
    {
        var answer = await SendOnceAsync(target, action, limit, writeParameters, cancellationToken).ConfigureAwait(false);
        if (IsBusy(answer))
        {
            _logger.LogInformation("AnkiConnect at {Host} was busy with {Action}. Trying once more.", target.Address.Authority, action);
            answer = await SendOnceAsync(target, action, limit, writeParameters, cancellationToken).ConfigureAwait(false);
        }

        return answer;
    }

    private async Task<AnkiAnswer> SendOnceAsync(AnkiTarget target, string action, TimeSpan limit, Action<Utf8JsonWriter> writeParameters, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WriteNumber("version", Version);
            if (!string.IsNullOrEmpty(target.Key))
            {
                writer.WriteString("key", target.Key);
            }

            writeParameters(writer);
            writer.WriteEndObject();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, target.Address)
        {
            Content = new ByteArrayContent(body.GetBuffer(), 0, (int)body.Length),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(limit);
        byte[] answer;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timer.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("AnkiConnect at {Host} answered {Action} with HTTP {Status}.", target.Address.Authority, action, (int)response.StatusCode);
                return AnkiAnswer.Failed(AnkiOutcome.NotAnkiConnect);
            }

            answer = await response.Content.ReadAsByteArrayAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("AnkiConnect at {Host} did not answer {Action} within {Seconds} seconds.", target.Address.Authority, action, limit.TotalSeconds);
            return AnkiAnswer.Failed(AnkiOutcome.TimedOut);
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            _logger.LogInformation("AnkiConnect at {Host} answered {Action} with more than {Bytes} bytes.", target.Address.Authority, action, MaxAnswerBytes);
            return AnkiAnswer.Failed(AnkiOutcome.NotAnkiConnect);
        }
        catch (HttpRequestException error)
        {
            _logger.LogInformation("AnkiConnect at {Host} could not be reached for {Action}: {Reason}", target.Address.Authority, action, error.Message);
            return AnkiAnswer.Failed(AnkiOutcome.Unreachable);
        }

        return Read(answer, target, action);
    }

    private AnkiAnswer Read(byte[] answer, AnkiTarget target, string action)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;

            // Version 6 always answers with both, the one not used being null.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("result", out var result)
                && root.TryGetProperty("error", out var error)
                && error.ValueKind is JsonValueKind.Null or JsonValueKind.String)
            {
                return new AnkiAnswer(AnkiOutcome.Answered, result.Clone(), error.ValueKind == JsonValueKind.String ? error.GetString() : null);
            }
        }
        catch (JsonException)
        {
            // Not JSON at all.
        }

        _logger.LogInformation("What answered {Action} at {Host} is not AnkiConnect version {Version}.", action, target.Address.Authority, Version);
        return AnkiAnswer.Failed(AnkiOutcome.NotAnkiConnect);
    }
}
