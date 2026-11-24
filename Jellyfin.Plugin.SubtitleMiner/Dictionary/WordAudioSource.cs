using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Settings;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// What a search for how a word sounds came to.
/// </summary>
internal enum WordAudioOutcome
{
    /// <summary>The word's sound is there.</summary>
    Found,

    /// <summary>No audio source is set.</summary>
    NoSource,

    /// <summary>The audio source has no sound for the word.</summary>
    NotFound,

    /// <summary>The audio source could not be reached, or answered with something else.</summary>
    Failed,
}

/// <summary>
/// How a word sounds, or why there is no sound.
/// </summary>
/// <param name="Outcome">What the search came to.</param>
/// <param name="Content">The sound's file.</param>
/// <param name="ContentType">Its media type.</param>
/// <param name="Extension">The file extension that goes with it.</param>
internal sealed record WordAudio(WordAudioOutcome Outcome, byte[]? Content = null, string? ContentType = null, string? Extension = null);

/// <summary>
/// Fetches how a word sounds from the audio source an administrator set, as Yomitan does with a custom audio source:
/// an address with <c>{term}</c> and <c>{reading}</c> in it that answers with a list of sounds in JSON (Yomitan's
/// "Custom URL (JSON)") or with the sound itself ("Custom URL"). Such an address often holds a key: it is kept
/// encrypted, never sent to the page, and never written to the log, which names its host only.
/// </summary>
internal sealed class WordAudioSource : IDisposable
{
    /// <summary>A list of sounds larger than this is not read.</summary>
    internal const int MaxListBytes = 1024 * 1024;

    /// <summary>A sound larger than this is not fetched.</summary>
    internal const int MaxAudioBytes = 8 * 1024 * 1024;

    /// <summary>How many sounds of a list are tried before the word is taken to have none.</summary>
    internal const int MaxTries = 3;

    /// <summary>How long a search may take in all.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static readonly Dictionary<string, (string Type, string Extension)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ("audio/mpeg", "mp3"),
        ["audio/mp3"] = ("audio/mpeg", "mp3"),
        ["audio/ogg"] = ("audio/ogg", "ogg"),
        ["audio/opus"] = ("audio/ogg", "opus"),
        ["audio/webm"] = ("audio/webm", "webm"),
        ["audio/mp4"] = ("audio/mp4", "m4a"),
        ["audio/x-m4a"] = ("audio/mp4", "m4a"),
        ["audio/aac"] = ("audio/aac", "aac"),
        ["audio/wav"] = ("audio/wav", "wav"),
        ["audio/x-wav"] = ("audio/wav", "wav"),
        ["audio/wave"] = ("audio/wav", "wav"),
        ["audio/flac"] = ("audio/flac", "flac"),
    };

    private static readonly Dictionary<string, string> TypeOfExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".opus"] = "audio/opus",
        [".webm"] = "audio/webm",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
    };

    private readonly HttpClient _client;
    private readonly ISharedSettings _shared;
    private readonly KeyProtector _keys;
    private readonly ILogger<WordAudioSource> _logger;
    private readonly SemaphoreSlim _running = new(4, 4);

    /// <summary>Initializes a new instance of the <see cref="WordAudioSource"/> class.</summary>
    /// <param name="handler">What sends the requests.</param>
    /// <param name="shared">Where the address is kept.</param>
    /// <param name="keys">What encrypts it.</param>
    /// <param name="logger">Where problems are written, without the address.</param>
    public WordAudioSource(HttpMessageHandler handler, ISharedSettings shared, KeyProtector keys, ILogger<WordAudioSource> logger)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Limit };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("SubtitleMiner/2.0 (Jellyfin plugin)");
        _shared = shared;
        _keys = keys;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether an audio source is set.</summary>
    public bool IsSet => Address.Length > 0;

    /// <summary>Gets the host of the audio source, which may be shown to an administrator; null without one.</summary>
    public string? Host => Fill(Address, "x", "x") is { } address ? address.Host : null;

    /// <summary>Gets the address as set, decrypted; empty without one.</summary>
    private string Address => _keys.Reveal(_shared.WordAudioAddress);

    /// <summary>The handler the plugin fetches sounds with: a few redirects followed, a proxy the environment names used.</summary>
    /// <returns>The handler.</returns>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };

    /// <summary>Reads an address an administrator typed: http or https, without a name and password, with <c>{term}</c> in it.</summary>
    /// <param name="typed">What was typed.</param>
    /// <param name="address">The address, trimmed.</param>
    /// <param name="problem">What is wrong with it, for the administrator.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryRead(string? typed, out string address, out string problem)
    {
        address = typed?.Trim() ?? string.Empty;
        problem = string.Empty;
        if (!address.Contains("{term}", StringComparison.Ordinal))
        {
            problem = "The address needs {term} where the word goes, as in Yomitan's custom audio sources.";
            return false;
        }

        if (Fill(address, "x", "x") is not { } filled || filled.UserInfo.Length > 0)
        {
            problem = "That is not an http or https address.";
            return false;
        }

        return true;
    }

    /// <summary>The address for one word, with <c>{term}</c>, <c>{reading}</c> and <c>{language}</c> filled in.</summary>
    /// <param name="template">The address as set.</param>
    /// <param name="term">The word.</param>
    /// <param name="reading">Its reading.</param>
    /// <returns>The address, or null when it is not an http or https one.</returns>
    internal static Uri? Fill(string template, string term, string reading)
    {
        var text = template
            .Replace("{term}", Uri.EscapeDataString(term), StringComparison.Ordinal)
            .Replace("{reading}", Uri.EscapeDataString(reading), StringComparison.Ordinal)
            .Replace("{language}", "ja", StringComparison.Ordinal);
        return Uri.TryCreate(text, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
            ? address
            : null;
    }

    /// <summary>Sets the audio source, or takes it away.</summary>
    /// <param name="address">An address <see cref="TryRead"/> took, or empty for none.</param>
    public void Set(string address) => _shared.SetWordAudioAddress(address.Length == 0 ? string.Empty : _keys.Protect(address));

    /// <summary>Finds how a word sounds: the first sound of the source's list that can be fetched.</summary>
    /// <param name="term">The word as the dictionary writes it.</param>
    /// <param name="reading">Its reading; the word itself where it is empty.</param>
    /// <param name="cancellationToken">Ends the search.</param>
    /// <returns>The sound, or why there is none.</returns>
    public async Task<WordAudio> FindAsync(string term, string reading, CancellationToken cancellationToken)
    {
        var template = Address;
        if (template.Length == 0)
        {
            return new WordAudio(WordAudioOutcome.NoSource);
        }

        term = term.Trim();
        reading = string.IsNullOrWhiteSpace(reading) ? term : reading.Trim();
        if (term.Length == 0 || Fill(template, term, reading) is not { } address)
        {
            return new WordAudio(WordAudioOutcome.NotFound);
        }

        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Limit);
            var first = await GetAsync(address, limit.Token).ConfigureAwait(false);
            if (first is null)
            {
                return new WordAudio(WordAudioOutcome.Failed);
            }

            // A "Custom URL" source answers with the sound itself.
            if (AudioOf(first) is { } direct)
            {
                return direct;
            }

            if (SoundsOf(first) is not { } sounds)
            {
                _logger.LogWarning("The word audio source at {Host} answered with neither a sound nor Yomitan's list of sounds.", address.Host);
                return new WordAudio(WordAudioOutcome.Failed);
            }

            var tried = 0;
            foreach (var sound in sounds)
            {
                if (tried++ == MaxTries)
                {
                    break;
                }

                if (await GetAsync(sound, limit.Token).ConfigureAwait(false) is { } fetched && AudioOf(fetched) is { } found)
                {
                    return found;
                }
            }

            return new WordAudio(WordAudioOutcome.NotFound);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The word audio source at {Host} did not answer in time.", address.Host);
            return new WordAudio(WordAudioOutcome.Failed);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
        _running.Dispose();
    }

    /// <summary>The sound a fetch brought, when it brought one.</summary>
    private static WordAudio? AudioOf(Fetched fetched)
    {
        if (fetched.Content.Length == 0)
        {
            return null;
        }

        var type = fetched.ContentType;
        if (string.IsNullOrEmpty(type) || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            // A file server that names no type: the address's extension says what it is.
            if (!TypeOfExtension.TryGetValue(Path.GetExtension(fetched.Address.AbsolutePath), out type))
            {
                return null;
            }
        }

        if (Types.TryGetValue(type, out var known))
        {
            return new WordAudio(WordAudioOutcome.Found, fetched.Content, known.Type, known.Extension);
        }

        return type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? new WordAudio(WordAudioOutcome.Found, fetched.Content, type, "audio")
            : null;
    }

    /// <summary>The sounds of Yomitan's list, in its order, or null when the answer is not such a list.</summary>
    private static List<Uri>? SoundsOf(Fetched fetched)
    {
        if (fetched.Content.Length > MaxListBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(fetched.Content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "audioSourceList"
                || !root.TryGetProperty("audioSources", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sounds = new List<Uri>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(fetched.Address, url.GetString(), out var address)
                    && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
                {
                    sounds.Add(address);
                }
            }

            return sounds;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Fetched?> GetAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("The word audio source at {Host} answered {Status}.", address.Host, (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxAudioBytes)
            {
                return null;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxAudioBytes)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                return new Fetched(response.RequestMessage?.RequestUri ?? address, response.Content.Headers.ContentType?.MediaType, buffer.ToArray());
            }
        }
        catch (HttpRequestException error)
        {
            // The exception's text may name the whole address, key and all.
            _logger.LogInformation("The word audio source at {Host} could not be reached ({Error}).", address.Host, error.HttpRequestError);
            return null;
        }
    }

    private sealed record Fetched(Uri Address, string? ContentType, byte[] Content);
}
