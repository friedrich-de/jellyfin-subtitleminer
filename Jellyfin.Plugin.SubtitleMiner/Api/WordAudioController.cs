using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// How words sound, from the audio source an administrator set as in Yomitan: for the dictionary popup to play, and
/// for a word's card, either to the page or straight into the user's Anki on a server. The source's address stays on
/// the server.
/// </summary>
[ApiController]
[Route("SubtitleMiner/Api/WordAudio")]
[Authorize]
[CallerGone]
public class WordAudioController : ControllerBase
{
    /// <summary>A word or a reading longer than this is not looked for.</summary>
    internal const int MaxWord = 64;

    /// <summary>The word an administrator's new audio source is tried with: common, and with a sound in every source.</summary>
    internal const string TryTerm = "食べる";

    /// <summary>The reading of <see cref="TryTerm"/>.</summary>
    internal const string TryReading = "たべる";

    internal const string NoSource = "No audio source is set for words. An administrator sets one in the dictionary settings.";

    internal const string NoSound = "The audio source has no sound for this word.";

    internal const string SourceFailed = "The audio source for words could not be reached, or did not answer as Yomitan's custom audio sources do.";

    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="WordAudioController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public WordAudioController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Gets how a word sounds.
    /// </summary>
    /// <param name="term">The word as the dictionary writes it.</param>
    /// <param name="reading">Its reading. Leave it out when it is the word itself.</param>
    /// <param name="cancellationToken">Ends with the request.</param>
    /// <returns>The sound.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> GetWordAudio([FromQuery, Required] string term, [FromQuery] string? reading = null, CancellationToken cancellationToken = default)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        var (sound, refusal) = await FindAsync(term, reading, cancellationToken).ConfigureAwait(false);
        if (sound is null)
        {
            return refusal!;
        }

        // The same word sounds the same for a while; the browser may keep it.
        Response.Headers.CacheControl = "private, max-age=3600";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(sound.Content!, sound.ContentType!);
    }

    /// <summary>
    /// Puts how a word sounds into the user's Anki on a server, for a card.
    /// </summary>
    /// <param name="term">The word as the dictionary writes it.</param>
    /// <param name="reading">Its reading. Leave it out when it is the word itself.</param>
    /// <param name="cancellationToken">Ends with the request.</param>
    /// <returns>The name Anki stored the sound under.</returns>
    [HttpPost("Anki")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StoredMediaView>> PostWordAudioToAnki([FromQuery, Required] string term, [FromQuery] string? reading = null, CancellationToken cancellationToken = default)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        // Whether the file can go anywhere is settled before the source is asked.
        if (!_services.Anki.TryFind(caller, out _, out var status, out var message))
        {
            return Problem(message, statusCode: status);
        }

        var (sound, refusal) = await FindAsync(term, reading, cancellationToken).ConfigureAwait(false);
        if (sound is null)
        {
            return refusal!;
        }

        return await AnkiMedia.StoreAsync(this, _services, caller, MediaName(term, reading) + "." + sound.Extension, sound.Content!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the audio source for words, or takes it away, and tries it with a common word.
    /// </summary>
    /// <param name="setting">The address, as in Yomitan's custom audio sources; empty takes it away.</param>
    /// <param name="cancellationToken">Ends with the request.</param>
    /// <returns>The source as set, and what the try found.</returns>
    [HttpPut]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<WordAudioSaved>> PutWordAudio([FromBody] WordAudioSetting setting, CancellationToken cancellationToken = default)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!caller.IsAdministrator)
        {
            return Problem("Only an administrator of this Jellyfin server may set where words' sounds come from.", statusCode: StatusCodes.Status403Forbidden);
        }

        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(setting.Address))
        {
            _services.WordAudio.Set(string.Empty);
            return new WordAudioSaved { WordAudio = View(_services.WordAudio, caller), Tried = "none" };
        }

        if (!WordAudioSource.TryRead(setting.Address, out var address, out var problem))
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        _services.WordAudio.Set(address);
        var tried = await _services.WordAudio.FindAsync(TryTerm, TryReading, cancellationToken).ConfigureAwait(false);
        return new WordAudioSaved
        {
            WordAudio = View(_services.WordAudio, caller),
            Tried = tried.Outcome switch
            {
                WordAudioOutcome.Found => "found",
                WordAudioOutcome.NotFound => "notFound",
                _ => "failed",
            },
        };
    }

    /// <summary>Where words' sounds come from, as a user may see it.</summary>
    /// <param name="source">The source.</param>
    /// <param name="caller">Who asks: only administrators see its host.</param>
    /// <returns>The view.</returns>
    internal static WordAudioView View(WordAudioSource source, Caller caller) => new()
    {
        Set = source.IsSet,
        Host = caller.IsAdministrator ? source.Host : null,
    };

    /// <summary>The name a word's sound goes into Anki under: the same word gets the same name.</summary>
    /// <param name="term">The word.</param>
    /// <param name="reading">Its reading.</param>
    /// <returns>The name, without its extension.</returns>
    internal static string MediaName(string term, string? reading)
    {
        reading = string.IsNullOrWhiteSpace(reading) ? term.Trim() : reading.Trim();
        var mark = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(term.Trim() + "|" + reading)), 0, 4);
        return "subtitle-miner_word_" + mark;
    }

    private async Task<(WordAudio? Sound, ActionResult? Refusal)> FindAsync(string term, string? reading, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length > MaxWord || (reading?.Length ?? 0) > MaxWord)
        {
            return (null, Problem("Name a word of at most 64 characters.", statusCode: StatusCodes.Status400BadRequest));
        }

        var sound = await _services.WordAudio.FindAsync(term, reading ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return sound.Outcome switch
        {
            WordAudioOutcome.Found => (sound, null),
            WordAudioOutcome.NoSource => (null, Problem(NoSource, statusCode: StatusCodes.Status404NotFound)),
            WordAudioOutcome.NotFound => (null, Problem(NoSound, statusCode: StatusCodes.Status404NotFound)),
            _ => (null, Problem(SourceFailed, statusCode: StatusCodes.Status502BadGateway)),
        };
    }
}
