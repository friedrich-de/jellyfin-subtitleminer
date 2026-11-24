using System;
using System.Linq;
using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Serves the plugin's page under <c>/SubtitleMiner/</c> at the Jellyfin address. The page's files are the same
/// for everyone and hold no data, so they need no login. Everything the page then asks for does.
/// </summary>
[ApiController]
[Route("SubtitleMiner")]
[AllowAnonymous]
public class PageController : ControllerBase
{
    /// <summary>
    /// What the page may do: run its own script and styles, show pictures and play sound from this server or
    /// made in the page, and call this server and an AnkiConnect on the user's own device. Nothing from
    /// elsewhere, and no other site may frame it. An AnkiConnect anywhere else is called by the server.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; "
        + "font-src 'self'; connect-src 'self' " + DeviceAddresses + "; manifest-src 'self'; worker-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// The addresses of the user's own device, on any port: where desktop Anki's AnkiConnect, and the Android
    /// app's, listen.
    /// </summary>
    internal const string DeviceAddresses =
        "http://127.0.0.1:* http://localhost:* http://[::1]:* https://127.0.0.1:* https://localhost:* https://[::1]:*";

    /// <summary>
    /// Where the page lives, as an address relative to the folder it sits in.
    /// </summary>
    internal const string PageAddress = "SubtitleMiner/";

    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public PageController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Serves the page. It signs in with the Jellyfin login the browser already has and then calls the
    /// plugin's endpoints.
    /// </summary>
    /// <returns>The page.</returns>
    [HttpGet("")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetPage()
    {
        // The page links its files relative to its own address, which only works with a slash at the end.
        // The address in the answer is relative too, so it holds behind a reverse proxy with a base path.
        var request = HttpContext.Request;
        if (request.Path.Value?.EndsWith('/') != true)
        {
            return Redirect(PageAddress + request.QueryString);
        }

        if (!_services.Page.HasPage)
        {
            return Problem("This build of the plugin holds no page. Build it with dev/package.sh.", statusCode: StatusCodes.Status404NotFound);
        }

        return GetFile(string.Empty);
    }

    /// <summary>
    /// Serves one of the page's files: its script, styles, icons, app manifest or service worker.
    /// </summary>
    /// <param name="path">The file's path below the page's address.</param>
    /// <returns>The file, or 404 for anything that is not one of the page's files.</returns>
    [HttpGet("{**path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetFile([FromRoute] string? path)
    {
        var file = _services.Page.Find(path);
        var stream = file is null ? null : _services.Page.Open(file);
        if (file is null || stream is null)
        {
            return NotFound();
        }

        // A file with a hash in its name never changes. The others change with the plugin version, so the
        // browser should always ask again.
        Response.Headers.CacheControl = file.Immutable ? "public, max-age=31536000, immutable" : "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";

        // The page reads the Jellyfin login from the browser, so it must not load anything from
        // elsewhere, sit inside another site's frame, or tell another site where it was opened from.
        Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return File(stream, file.ContentType);
    }

    /// <summary>
    /// Sends a browser that opens the page's address of the versions before 2.0 on to the page. The old
    /// endpoints are gone, so calls to them are not sent anywhere.
    /// </summary>
    /// <param name="path">What follows the old address.</param>
    /// <returns>A redirect to the page, or 404 for one of the old endpoints.</returns>
    [HttpGet("/Plugins/JellyfinMiner/{**path}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetOldAddress([FromRoute] string? path)
    {
        if (path is not null && (path.Equals("api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound();
        }

        // One step up for every folder between the server's root and the address that was asked for.
        var folders = (HttpContext.Request.Path.Value ?? string.Empty).Count(character => character == '/') - 1;
        return Redirect(string.Concat(Enumerable.Repeat("../", Math.Max(folders, 0))) + PageAddress);
    }
}
