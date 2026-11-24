using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Jellyfin.Plugin.SubtitleMiner.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// The settings of the signed-in user, kept on the server so they follow the user from device to device, and
/// the ones an administrator makes for everyone.
/// </summary>
[ApiController]
[Route("SubtitleMiner/Api/Settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public SettingsController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Gets the user's settings.
    /// </summary>
    /// <returns>The settings.</returns>
    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SettingsView>> GetSettings()
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        Response.Headers.CacheControl = "no-store";
        return View(caller, _services.Settings.Load(caller.UserId));
    }

    /// <summary>
    /// Changes the user's settings. What the change leaves out stays as it is.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <returns>The settings as they are now.</returns>
    [HttpPut]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SettingsView>> PutSettings([FromBody] SettingsChange change)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        var settings = _services.Settings.Load(caller.UserId);
        var keys = _services.Settings.Keys;
        var mayUseServer = _services.Anki.MayUseServer(caller);
        if (change.Anki is { } anki)
        {
            if (anki.Where is not null)
            {
                if (!AnkiWhere.IsKnown(anki.Where))
                {
                    return Problem("Cards go to \"off\", \"device\" or \"server\".", statusCode: StatusCodes.Status400BadRequest);
                }

                if (anki.Where == AnkiWhere.Server && !mayUseServer)
                {
                    return Problem(AnkiGate.NotAllowed, statusCode: StatusCodes.Status403Forbidden);
                }

                settings.Where = anki.Where;
            }

            if (anki.DeviceAddress is not null)
            {
                if (!AnkiAddress.TryRead(anki.DeviceAddress, out var address))
                {
                    return Problem("The address of the Anki on this device must be an http or https address, such as " + UserSettings.DefaultDeviceAddress + ".", statusCode: StatusCodes.Status400BadRequest);
                }

                settings.DeviceAddress = address!.OriginalString;
            }

            if (anki.ServerAddress is not null)
            {
                if (anki.ServerAddress.Length == 0)
                {
                    settings.ServerAddress = string.Empty;
                }
                else if (!mayUseServer)
                {
                    return Problem(AnkiGate.NotAllowed, statusCode: StatusCodes.Status403Forbidden);
                }
                else if (!AnkiAddress.TryRead(anki.ServerAddress, out var address))
                {
                    return Problem("The address of the AnkiConnect on your server must be an http or https address, without a name and password in it.", statusCode: StatusCodes.Status400BadRequest);
                }
                else
                {
                    settings.ServerAddress = address!.OriginalString;
                }
            }

            if (anki.DeviceKey?.Length > AnkiAddress.MaxKeyLength || anki.ServerKey?.Length > AnkiAddress.MaxKeyLength)
            {
                return Problem("That key is too long to be an AnkiConnect key.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (anki.DeviceKey is not null)
            {
                settings.DeviceKey = keys.Protect(anki.DeviceKey.Trim());
            }

            if (anki.ServerKey is not null)
            {
                settings.ServerKey = keys.Protect(anki.ServerKey.Trim());
            }
        }

        if (change.Page is { } page)
        {
            if (page.ValueKind != JsonValueKind.Object)
            {
                return Problem("The page's settings are an object.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (JsonSerializer.SerializeToUtf8Bytes(page).Length > UserSettingsStore.MaxPageBytes)
            {
                return Problem("The page's settings are too large.", statusCode: StatusCodes.Status400BadRequest);
            }

            settings.Page = page.Clone();
        }

        _services.Settings.Save(caller.UserId, settings);
        return View(caller, settings);
    }

    /// <summary>
    /// Changes the settings for everyone on the server. Only an administrator may.
    /// </summary>
    /// <param name="change">The settings.</param>
    /// <returns>The user's settings, with the new ones for everyone.</returns>
    [HttpPut("Everyone")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SettingsView>> PutEveryone([FromBody] EveryoneView change)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!caller.IsAdministrator)
        {
            return Problem("Only an administrator of this Jellyfin server may change this.", statusCode: StatusCodes.Status403Forbidden);
        }

        _services.Shared.SetAnkiThroughServerForEveryone(change.AnkiThroughServer);
        return View(caller, _services.Settings.Load(caller.UserId));
    }

    private SettingsView View(Caller caller, UserSettings settings)
    {
        var keys = _services.Settings.Keys;
        return new SettingsView
        {
            Anki = new AnkiView
            {
                Where = settings.Where,
                DeviceAddress = settings.DeviceAddress,
                DeviceKey = keys.Reveal(settings.DeviceKey),
                ServerAddress = settings.ServerAddress,

                // A key this server cannot read any more counts as none, so the page asks for it again.
                ServerKeySet = keys.Reveal(settings.ServerKey).Length > 0,
                ServerAllowed = _services.Anki.MayUseServer(caller),
            },
            Page = settings.Page,
            Everyone = caller.IsAdministrator
                ? new EveryoneView { AnkiThroughServer = _services.Shared.AnkiThroughServerForEveryone }
                : null,
        };
    }
}
