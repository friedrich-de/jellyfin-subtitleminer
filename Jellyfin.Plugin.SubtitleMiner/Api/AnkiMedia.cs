using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Puts a file into the user's Anki on a server, for the controllers that make one: cut pictures and sound, and
/// how a word sounds.
/// </summary>
internal static class AnkiMedia
{
    /// <summary>
    /// Stores a file in the media of the user's AnkiConnect on a server.
    /// </summary>
    /// <param name="controller">The controller that answers.</param>
    /// <param name="services">The plugin's services.</param>
    /// <param name="caller">Whose Anki.</param>
    /// <param name="fileName">The name to store it under.</param>
    /// <param name="content">The file.</param>
    /// <param name="cancellationToken">Ends with the request.</param>
    /// <returns>The name Anki stored it under, or a problem.</returns>
    public static async Task<ActionResult<StoredMediaView>> StoreAsync(ControllerBase controller, PluginServices services, Caller caller, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        // Read again: an administrator may have changed the settings while the file was made.
        if (!services.Anki.TryFind(caller, out var target, out var status, out var message))
        {
            return controller.Problem(message, statusCode: status);
        }

        if (!services.Anki.TryEnter(caller.UserId))
        {
            controller.Response.Headers.RetryAfter = "1";
            return controller.Problem("Too many calls to Anki are on their way for you. Try again in a moment.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        AnkiAnswer answer;
        try
        {
            answer = await services.AnkiClient.StoreMediaAsync(target!, fileName, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            services.Anki.Leave(caller.UserId);
        }

        if (answer.Outcome != AnkiOutcome.Answered)
        {
            var (failure, text) = AnkiGate.Failure(answer.Outcome, target!);
            return controller.Problem(text, statusCode: failure);
        }

        if (answer.Error is not null)
        {
            return controller.Problem(AnkiGate.Explain(answer.Error), statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // AnkiConnect answers with the name it stored the file under, which may differ from the one asked for.
        var stored = answer.Result.ValueKind == JsonValueKind.String ? answer.Result.GetString() : null;
        controller.Response.Headers.CacheControl = "no-store";
        return new StoredMediaView { FileName = string.IsNullOrEmpty(stored) ? fileName : stored };
    }
}
