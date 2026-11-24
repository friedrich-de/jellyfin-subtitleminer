using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// The dictionaries on the server: every signed-in user may look words up in them; administrators add them, from a
/// zip they upload or an address the server downloads from, put them in order, turn them off and remove them.
/// </summary>
[ApiController]
[Route("SubtitleMiner/Api/Dictionaries")]
[Authorize]
[CallerGone]
public class DictionariesController : ControllerBase
{
    /// <summary>A lookup reads at most this much of the text it is given.</summary>
    internal const int MaxLookupText = 64;

    internal const string OnlyAdministrators = "Only an administrator of this Jellyfin server may change its dictionaries.";

    internal const string ImportRuns = "A dictionary is being imported. Wait until it is done, or stop it.";

    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="DictionariesController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public DictionariesController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Gets the dictionaries and what an import does.
    /// </summary>
    /// <returns>The dictionaries.</returns>
    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DictionariesView>> GetDictionaries()
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        Response.Headers.CacheControl = "no-store";
        return View(caller);
    }

    /// <summary>
    /// Looks up the words a text starts with, in the dictionaries that are on. The answer is in the shape the
    /// page's dictionary popup draws.
    /// </summary>
    /// <param name="text">The text, from where the word starts.</param>
    /// <returns>The words found, the best first.</returns>
    [HttpGet("Lookup")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> GetLookup([FromQuery] string? text)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        text ??= string.Empty;
        if (text.Length > MaxLookupText)
        {
            text = text[..MaxLookupText];
        }

        var found = _services.Dictionaries.Lookup(text);
        Response.Headers.CacheControl = "no-store";
        return Content(JsonSerializer.Serialize(found, DictionaryJsonContext.Default.LookupView), MediaTypeNames.Application.Json);
    }

    /// <summary>
    /// Gets a picture a dictionary's definitions show.
    /// </summary>
    /// <param name="id">The dictionary.</param>
    /// <param name="path">The picture's path in the dictionary.</param>
    /// <returns>The picture.</returns>
    [HttpGet("{id}/Media")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetMedia([FromRoute] string id, [FromQuery] string? path)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrEmpty(path) || _services.Dictionaries.Media(id, path) is not { } media)
        {
            return Problem("That dictionary has no such picture.", statusCode: StatusCodes.Status404NotFound);
        }

        // A picture is only ever shown in the page. Opened on its own, an SVG may not run anything.
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        return File(media.Data, media.Type);
    }

    /// <summary>
    /// Imports a dictionary from a zip in Yomitan's format, sent as the request's body. The import runs on the
    /// server; <see cref="GetDictionaries"/> says how far it is.
    /// </summary>
    /// <param name="name">The zip's file name, to show while it is imported.</param>
    /// <param name="cancellationToken">Stops the upload.</param>
    /// <returns>What the import does.</returns>
    [HttpPost]
    [DisableRequestSizeLimit]
    [Consumes("application/zip", "application/x-zip-compressed", MediaTypeNames.Application.Octet)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<DictionariesView>> PostDictionary([FromQuery] string? name, CancellationToken cancellationToken)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!caller.IsAdministrator)
        {
            return Problem(OnlyAdministrators, statusCode: StatusCodes.Status403Forbidden);
        }

        if (_services.Dictionaries.Import.Running)
        {
            return Problem(ImportRuns, statusCode: StatusCodes.Status409Conflict);
        }

        Directory.CreateDirectory(_services.DictionaryScratch);
        var zip = Path.Combine(_services.DictionaryScratch, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var target = System.IO.File.Create(zip);
            await using (target.ConfigureAwait(false))
            {
                await DictionaryDownloader.CopyAsync(Request.Body, target, null, null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DictionaryFetchException error)
        {
            System.IO.File.Delete(zip);
            return Problem(error.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch
        {
            System.IO.File.Delete(zip);
            throw;
        }

        var source = string.IsNullOrWhiteSpace(name) ? "the uploaded file" : Path.GetFileName(name.Trim());
        if (!_services.Dictionaries.TryStartImport(source, (_, _) => Task.FromResult(zip)))
        {
            System.IO.File.Delete(zip);
            return Problem(ImportRuns, statusCode: StatusCodes.Status409Conflict);
        }

        return Accepted(View(caller));
    }

    /// <summary>
    /// Downloads a dictionary's zip from an address and imports it. The import runs on the server;
    /// <see cref="GetDictionaries"/> says how far it is.
    /// </summary>
    /// <param name="download">The address.</param>
    /// <returns>What the import does.</returns>
    [HttpPost("Download")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DictionariesView>> PostDownload([FromBody] DictionaryDownload download)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!caller.IsAdministrator)
        {
            return Problem(OnlyAdministrators, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!DictionaryDownloader.TryRead(download.Url, out var address))
        {
            return Problem("A dictionary is downloaded from an http or https address, without a name and password in it.", statusCode: StatusCodes.Status400BadRequest);
        }

        var scratch = _services.DictionaryScratch;
        var downloads = _services.DictionaryDownloads;
        if (!_services.Dictionaries.TryStartImport(address!.OriginalString, (progress, token) => downloads.DownloadAsync(address, scratch, progress, token)))
        {
            return Problem(ImportRuns, statusCode: StatusCodes.Status409Conflict);
        }

        return Accepted(View(caller));
    }

    /// <summary>
    /// Stops the import that runs.
    /// </summary>
    /// <returns>What the import does.</returns>
    [HttpDelete("Import")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DictionariesView>> DeleteImport()
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!caller.IsAdministrator)
        {
            return Problem(OnlyAdministrators, statusCode: StatusCodes.Status403Forbidden);
        }

        _services.Dictionaries.CancelImport();
        return View(caller);
    }

    /// <summary>
    /// Puts the dictionaries in a new order and turns them on or off.
    /// </summary>
    /// <param name="change">The order, naming every dictionary once, and those that are off.</param>
    /// <returns>The dictionaries.</returns>
    [HttpPut]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DictionariesView>> PutDictionaries([FromBody] DictionaryOrderChange change)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!caller.IsAdministrator)
        {
            return Problem(OnlyAdministrators, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!_services.Dictionaries.Arrange(change.Order, change.Disabled.ToHashSet(StringComparer.Ordinal)))
        {
            return Problem("The order must name every dictionary once.", statusCode: StatusCodes.Status400BadRequest);
        }

        return View(caller);
    }

    /// <summary>
    /// Removes a dictionary.
    /// </summary>
    /// <param name="id">The dictionary.</param>
    /// <returns>The dictionaries left.</returns>
    [HttpDelete("{id}")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DictionariesView>> DeleteDictionary([FromRoute] string id)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!caller.IsAdministrator)
        {
            return Problem(OnlyAdministrators, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!_services.Dictionaries.Remove(id))
        {
            return Problem("There is no such dictionary.", statusCode: StatusCodes.Status404NotFound);
        }

        return View(caller);
    }

    private DictionariesView View(Caller caller)
    {
        var import = _services.Dictionaries.Import;
        return new DictionariesView
        {
            Dictionaries = _services.Dictionaries.Installed.Select(item =>
            {
                var info = item.Reader.Info;
                return new DictionaryView
                {
                    Id = info.Id,
                    Title = info.Title,
                    Revision = info.Revision,
                    Enabled = item.Enabled,
                    Terms = info.Terms,
                    Frequencies = info.Frequencies,
                    Pitches = info.Pitches,
                    FrequencyMode = info.FrequencyMode,
                    Author = info.Author,
                    Url = info.Url,
                    Attribution = info.Attribution,
                    DownloadUrl = info.DownloadUrl,
                    Styles = item.Reader.Styles,
                    Bytes = DictionaryLibrary.SizeOf(item.Reader),
                    Imported = info.Imported,
                };
            }).ToList(),
            Import = new DictionaryImportView
            {
                State = import.State,
                Source = import.Source,
                Progress = import.Progress,
                Title = import.Title,
                Error = import.Error,
            },
            MayManage = caller.IsAdministrator,
        };
    }
}
