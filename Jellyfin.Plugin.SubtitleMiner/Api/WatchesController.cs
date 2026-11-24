using System;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Tells the page what the signed-in user's players are playing, once or as a stream of news.
/// </summary>
[ApiController]
[Route("SubtitleMiner/Api")]
[Authorize]
public class WatchesController : ControllerBase
{
    /// <summary>How long the stream stays quiet before it sends a sign of life.</summary>
    internal static readonly TimeSpan Beat = TimeSpan.FromSeconds(15);

    /// <summary>How many streams one user may hold open.</summary>
    internal const int MaxStreamsPerUser = 12;

    private static readonly JsonSerializerOptions _json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly PluginServices _services;

    /// <summary>
    /// Gets how long this stream stays quiet before it sends a sign of life. Tests shorten it.
    /// </summary>
    internal TimeSpan BeatEvery { get; init; } = Beat;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchesController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public WatchesController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Lists what the user's players play now and what was watched in the last hours.
    /// </summary>
    /// <returns>The list.</returns>
    [HttpGet("Watches")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<WatchListView>> GetWatches()
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        Response.Headers.CacheControl = "no-store";
        return List(caller);
    }

    /// <summary>
    /// Keeps the connection open and sends the list again each time something changes that the page
    /// cannot work out itself: a start, a stop, a pause, a jump, another track. The answer is a stream
    /// of server-sent events named <c>watches</c>, each with the list as JSON.
    /// </summary>
    /// <param name="cancellationToken">Ends with the connection.</param>
    /// <returns>Nothing: the answer is written as it happens.</returns>
    [HttpGet("Events")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task GetEvents(CancellationToken cancellationToken)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var listener = _services.Tracker.Listen(caller.UserId);
        if (_services.Tracker.ListenerCountOf(caller.UserId) > MaxStreamsPerUser)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        // The stream also ends when the server goes down, or the server would wait for it.
        var stopping = HttpContext.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-store";

        // Tells a reverse proxy that would collect the answer first to pass it on as it comes.
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await SendAsync(caller, ended.Token).ConfigureAwait(false);
            while (!ended.IsCancellationRequested)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ended.Token);
                quiet.CancelAfter(BeatEvery);
                try
                {
                    await listener.News.ReadAsync(quiet.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ended.IsCancellationRequested)
                {
                    // Nothing happened for a while. A comment keeps the connection alive and shows the page it is.
                    // A player that went silent ends here, and the next read has that news.
                    await WriteAsync(": beat\n\n", ended.Token).ConfigureAwait(false);
                    _services.Tracker.Sweep();
                    continue;
                }

                await SendAsync(caller, ended.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The page closed the connection, or the server is going down.
        }
    }

    private WatchListView List(Caller caller) =>
        WatchViews.Build(_services.Tracker.ListFor(caller.UserId), _services.Library, caller.UserId, _services.Time.GetUtcNow());

    private Task SendAsync(Caller caller, CancellationToken cancellationToken) =>
        WriteAsync("event: watches\ndata: " + JsonSerializer.Serialize(List(caller), _json) + "\n\n", cancellationToken);

    private async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
