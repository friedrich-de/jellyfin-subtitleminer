using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// Reads the subtitles of a video and cuts pictures and sound from it, for the page or straight into the user's
/// Anki on a server. A user gets this for every video they may watch in Jellyfin, and for no other.
/// </summary>
[ApiController]
[Route("SubtitleMiner/Api/Items/{itemId}")]
[Authorize]
[CallerGone]
public class ItemsController : ControllerBase
{
    private const string NoVideo = "This video is not in your library.";

    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemsController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public ItemsController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Reads the lines of a subtitle track.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="track">The key of the subtitle track.</param>
    /// <param name="source">The version of the video. Leave it out for the first.</param>
    /// <param name="cancellationToken">Ends with the request.</param>
    /// <returns>The lines.</returns>
    [HttpGet("Subtitles")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SubtitlesView>> GetSubtitles(
        [FromRoute] Guid itemId,
        [FromQuery, Required] string track,
        [FromQuery] string? source,
        CancellationToken cancellationToken)
    {
        var (_, media, refusal) = await FindAsync(itemId, source, anki: false).ConfigureAwait(false);
        if (media is null)
        {
            return refusal!;
        }

        var stream = media.Stream(track, StreamKind.Subtitle);
        if (stream is null)
        {
            return Problem("This video has no such subtitle track.", statusCode: StatusCodes.Status404NotFound);
        }

        if (!stream.IsReadable)
        {
            return Problem("This subtitle track is made of pictures, not of text, so its lines cannot be read.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var document = await _services.Subtitles.GetAsync(media, stream, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return Problem("The subtitle track could not be read. The Jellyfin log says why.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        Response.Headers.CacheControl = "no-store";
        return new SubtitlesView
        {
            Key = stream.Key,
            Format = document.Format,
            Lines = document.Lines
                .Select(line => new LineView
                {
                    Id = line.Id,
                    StartMs = line.StartMs,
                    EndMs = line.EndMs,
                    Text = line.Text,
                    Style = line.Style,
                    Name = line.Name,
                    Kind = line.Kind.ToString().ToLowerInvariant(),
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Cuts a picture from the video: a still at one moment, or a short moving picture between two.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="at">The moment of the still, or where the moving picture starts, in milliseconds.</param>
    /// <param name="end">Where a moving picture ends. Leave it out for a still.</param>
    /// <param name="format"><c>jpeg</c>, <c>webp</c> or <c>avif</c>.</param>
    /// <param name="width">The widest the picture may be, in pixels.</param>
    /// <param name="quality">From 1, smallest file, to 100, best picture.</param>
    /// <param name="source">The version of the video. Leave it out for the first.</param>
    /// <param name="cancellationToken">Ends with the request, and stops the cut.</param>
    /// <returns>The picture.</returns>
    [HttpGet("Picture")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> GetPicture(
        [FromRoute] Guid itemId,
        [FromQuery, Required] long at,
        [FromQuery] long? end,
        [FromQuery] string format = "jpeg",
        [FromQuery] int width = 960,
        [FromQuery] int quality = 85,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var (cut, refusal) = await CutPictureAsync(itemId, source, new PictureRequest(at, end, format, width, quality), anki: false, cancellationToken).ConfigureAwait(false);
        return cut is null ? refusal! : Answer(cut);
    }

    /// <summary>
    /// Cuts a picture as <see cref="GetPicture"/> does and puts it into the user's Anki on a server, for a card.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="at">The moment of the still, or where the moving picture starts, in milliseconds.</param>
    /// <param name="end">Where a moving picture ends. Leave it out for a still.</param>
    /// <param name="format"><c>jpeg</c>, <c>webp</c> or <c>avif</c>.</param>
    /// <param name="width">The widest the picture may be, in pixels.</param>
    /// <param name="quality">From 1, smallest file, to 100, best picture.</param>
    /// <param name="source">The version of the video. Leave it out for the first.</param>
    /// <param name="cancellationToken">Ends with the request, and stops the cut.</param>
    /// <returns>The name Anki stored the picture under.</returns>
    [HttpPost("Picture/Anki")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StoredMediaView>> PostPictureToAnki(
        [FromRoute] Guid itemId,
        [FromQuery, Required] long at,
        [FromQuery] long? end,
        [FromQuery] string format = "jpeg",
        [FromQuery] int width = 960,
        [FromQuery] int quality = 85,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var request = new PictureRequest(at, end, format, width, quality);
        var (cut, refusal) = await CutPictureAsync(itemId, source, request, anki: true, cancellationToken).ConfigureAwait(false);
        return cut is null
            ? refusal!
            : await StoreAsync(cut, MediaName(itemId, source, at, $"picture|{at}|{end}|{format}|{width}|{quality}"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cuts a piece of sound from the video.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="start">Where the sound starts, in milliseconds.</param>
    /// <param name="end">Where it ends. At most a minute is cut.</param>
    /// <param name="track">The key of the audio track. Leave it out for the video's first.</param>
    /// <param name="format"><c>mp3</c> or <c>opus</c>.</param>
    /// <param name="bitrate">Kilobits per second.</param>
    /// <param name="normalize">Whether to bring the loudness to a common level.</param>
    /// <param name="source">The version of the video. Leave it out for the first.</param>
    /// <param name="cancellationToken">Ends with the request, and stops the cut.</param>
    /// <returns>The sound.</returns>
    [HttpGet("Audio")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> GetAudio(
        [FromRoute] Guid itemId,
        [FromQuery, Required] long start,
        [FromQuery, Required] long end,
        [FromQuery] string? track = null,
        [FromQuery] string format = "mp3",
        [FromQuery] int bitrate = 128,
        [FromQuery] bool normalize = false,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var (cut, refusal) = await CutAudioAsync(itemId, source, track, new AudioRequest(start, end, format, bitrate, normalize, null), anki: false, cancellationToken).ConfigureAwait(false);
        return cut is null ? refusal! : Answer(cut);
    }

    /// <summary>
    /// Cuts a piece of sound as <see cref="GetAudio"/> does and puts it into the user's Anki on a server, for a
    /// card.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="start">Where the sound starts, in milliseconds.</param>
    /// <param name="end">Where it ends. At most a minute is cut.</param>
    /// <param name="track">The key of the audio track. Leave it out for the video's first.</param>
    /// <param name="format"><c>mp3</c> or <c>opus</c>.</param>
    /// <param name="bitrate">Kilobits per second.</param>
    /// <param name="normalize">Whether to bring the loudness to a common level.</param>
    /// <param name="source">The version of the video. Leave it out for the first.</param>
    /// <param name="cancellationToken">Ends with the request, and stops the cut.</param>
    /// <returns>The name Anki stored the sound under.</returns>
    [HttpPost("Audio/Anki")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StoredMediaView>> PostAudioToAnki(
        [FromRoute] Guid itemId,
        [FromQuery, Required] long start,
        [FromQuery, Required] long end,
        [FromQuery] string? track = null,
        [FromQuery] string format = "mp3",
        [FromQuery] int bitrate = 128,
        [FromQuery] bool normalize = false,
        [FromQuery] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var request = new AudioRequest(start, end, format, bitrate, normalize, null);
        var (cut, refusal) = await CutAudioAsync(itemId, source, track, request, anki: true, cancellationToken).ConfigureAwait(false);
        return cut is null
            ? refusal!
            : await StoreAsync(cut, MediaName(itemId, source, start, $"audio|{start}|{end}|{track}|{format}|{bitrate}|{normalize}"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The name a file goes into Anki under: what it is from and when, and a mark of how it was cut, so the same
    /// cut gets the same name and another one another.
    /// </summary>
    internal static string MediaName(Guid itemId, string? source, long atMs, string how)
    {
        var mark = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(itemId.ToString("N") + "|" + source + "|" + how)), 0, 4);
        return string.Create(CultureInfo.InvariantCulture, $"subtitle-miner_{itemId.ToString("N")[..8]}_{atMs}_{mark}");
    }

    private async Task<(CutResult? Cut, ActionResult? Refusal)> CutPictureAsync(Guid itemId, string? source, PictureRequest request, bool anki, CancellationToken cancellationToken)
    {
        var (caller, media, refusal) = await FindAsync(itemId, source, anki).ConfigureAwait(false);
        if (media is null)
        {
            return (null, refusal);
        }

        if (media.RuntimeMs is { } runtime && request.AtMs >= runtime)
        {
            return (null, Problem("The picture's time is not within the video.", statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        return Refused(await _services.Cutter.PictureAsync(media, request, caller!.UserId, cancellationToken).ConfigureAwait(false));
    }

    private async Task<(CutResult? Cut, ActionResult? Refusal)> CutAudioAsync(Guid itemId, string? source, string? track, AudioRequest request, bool anki, CancellationToken cancellationToken)
    {
        var (caller, media, refusal) = await FindAsync(itemId, source, anki).ConfigureAwait(false);
        if (media is null)
        {
            return (null, refusal);
        }

        MediaStreamInfo? stream = null;
        if (!string.IsNullOrEmpty(track))
        {
            stream = media.Stream(track, StreamKind.Audio);
            if (stream is null)
            {
                return (null, Problem("This video has no such audio track.", statusCode: StatusCodes.Status404NotFound));
            }
        }

        if (media.RuntimeMs is { } runtime && request.StartMs >= runtime)
        {
            return (null, Problem("The sound's time is not within the video.", statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        return Refused(await _services.Cutter.AudioAsync(media, request with { Track = stream }, caller!.UserId, cancellationToken).ConfigureAwait(false));
    }

    private async Task<(Caller? Caller, MediaInfo? Media, ActionResult? Refusal)> FindAsync(Guid itemId, string? source, bool anki)
    {
        var caller = await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false);
        if (caller is null)
        {
            return (null, null, Problem(SubtitleMinerController.NoUser, statusCode: StatusCodes.Status403Forbidden));
        }

        // A video that is not there and one the user may not see get the same answer.
        var media = _services.Library.Find(caller.UserId, itemId, string.IsNullOrEmpty(source) ? null : source);
        if (media is null)
        {
            return (caller, null, Problem(NoVideo, statusCode: StatusCodes.Status404NotFound));
        }

        // Whether the file can go anywhere is settled before anything is cut.
        if (anki && !_services.Anki.TryFind(caller, out _, out var status, out var message))
        {
            return (caller, null, Problem(message, statusCode: status));
        }

        return (caller, media, null);
    }

    /// <summary>
    /// Keeps a cut that is done, and turns any other into its answer.
    /// </summary>
    private (CutResult? Cut, ActionResult? Refusal) Refused(CutResult result) =>
        result.Status == CutStatus.Done ? (result, null) : (null, Answer(result));

    private async Task<ActionResult<StoredMediaView>> StoreAsync(CutResult cut, string name, CancellationToken cancellationToken)
    {
        var caller = (await _services.Callers.ResolveAsync(HttpContext).ConfigureAwait(false))!;
        byte[] content;
        try
        {
            content = await System.IO.File.ReadAllBytesAsync(cut.Path!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            System.IO.File.Delete(cut.Path!);
        }

        // Read again: an administrator may have changed the settings while the cut ran.
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
        var fileName = name + "." + cut.Extension;
        try
        {
            answer = await _services.AnkiClient.StoreMediaAsync(target!, fileName, content, cancellationToken).ConfigureAwait(false);
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

        if (answer.Error is not null)
        {
            return Problem(AnkiGate.Explain(answer.Error), statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // AnkiConnect answers with the name it stored the file under, which may differ from the one asked for.
        var stored = answer.Result.ValueKind == JsonValueKind.String ? answer.Result.GetString() : null;
        Response.Headers.CacheControl = "no-store";
        return new StoredMediaView { FileName = string.IsNullOrEmpty(stored) ? fileName : stored };
    }

    private ActionResult Answer(CutResult result)
    {
        switch (result.Status)
        {
            case CutStatus.Done:
                Response.Headers.CacheControl = "no-store";
                Response.Headers.XContentTypeOptions = "nosniff";

                // The file is removed as soon as it is sent.
                var stream = new FileStream(result.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                return File(stream, result.ContentType!);
            case CutStatus.Busy:
                Response.Headers.RetryAfter = "2";
                return Problem(result.Message, statusCode: StatusCodes.Status429TooManyRequests);
            case CutStatus.Refused:
                return Problem(result.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            default:
                return Problem(result.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
