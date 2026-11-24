using System.IO;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Passes the page's calls on to the user's AnkiConnect on a server, with the key the page never sees. Only
/// calls that read and add notes go through; pictures and sound go through the plugin's own cuts.
/// </summary>
[ApiController]
[Route("SubtitleMiner/Api/Anki")]
[Authorize]
[CallerGone]
public class AnkiController : ControllerBase
{
    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnkiController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public AnkiController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Makes one call to the user's AnkiConnect. The answer is AnkiConnect's own, <c>result</c> and
    /// <c>error</c>; an address that does not answer as AnkiConnect gets a problem instead.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Ends with the request.</param>
    /// <returns>AnkiConnect's answer.</returns>
    [HttpPost]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult> PostCall([FromBody] AnkiCall call, CancellationToken cancellationToken)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!AnkiClient.PageActions.Contains(call.Action))
        {
            return Problem("The page cannot ask Anki for that through the server.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (CarriesFiles(call.Params))
        {
            // AnkiConnect would fetch a "url" or read a "path" on the server it runs on.
            return Problem("Pictures and sound reach Anki through the plugin's own cuts, not inside a note.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!_services.Anki.TryFind(caller, out var target, out var status, out var message))
        {
            return Problem(message, statusCode: status);
        }

        if (!_services.Anki.TryEnter(caller.UserId))
        {
            Response.Headers.RetryAfter = "1";
            return Problem("Too many calls to Anki are on their way for you. Try again in a moment.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        AnkiAnswer answer;
        try
        {
            var limit = call.Action == "sync" ? AnkiClient.LongCallLimit : AnkiClient.CallLimit;
            answer = await _services.AnkiClient.InvokeAsync(target!, call.Action, call.Params, limit, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _services.Anki.Leave(caller.UserId);
        }

        if (answer.Outcome != AnkiOutcome.Answered)
        {
            var (failure, text) = AnkiGate.Failure(answer.Outcome, target!);
            return Problem(text, statusCode: failure);
        }

        Response.Headers.CacheControl = "no-store";
        return Content(Envelope(answer), MediaTypeNames.Application.Json);
    }

    /// <summary>
    /// Writes an answer the way AnkiConnect does.
    /// </summary>
    internal static string Envelope(AnkiAnswer answer)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("result");
            if (answer.Result.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
            }
            else
            {
                answer.Result.WriteTo(writer);
            }

            writer.WriteString("error", answer.Error);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Tells whether a note in the parameters brings files of its own.
    /// </summary>
    private static bool CarriesFiles(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("note", out var note)
            || note.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return note.TryGetProperty("picture", out _) || note.TryGetProperty("audio", out _) || note.TryGetProperty("video", out _);
    }
}
