using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// What the page calls, under <c>/SubtitleMiner/Api</c> at the Jellyfin address. Every endpoint needs a
/// Jellyfin user's login.
/// </summary>
[ApiController]
[Route("SubtitleMiner/Api")]
[Authorize]
public class SubtitleMinerController : ControllerBase
{
    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleMinerController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public SubtitleMinerController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Says who is signed in and what the page is talking to.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> GetStatus()
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem("Sign in to Jellyfin as a user to use this page.", statusCode: StatusCodes.Status403Forbidden);
        }

        return Ok(new StatusView
        {
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            UserId = caller.UserId,
            UserName = caller.Name,
            IsAdministrator = caller.IsAdministrator,
            ServerName = _services.Server.Name,
            ServerVersion = _services.Server.Version,
        });
    }
}
