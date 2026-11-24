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
/// The sounds an audio source has for a word, by name, as Yomitan lists them when its audio button is held.
/// </summary>
/// <param name="Outcome">What the search came to: <see cref="WordAudioOutcome.Found"/> with the list, which may be empty.</param>
/// <param name="Names">The names of the sounds, in the source's order, each once.</param>
internal sealed record WordAudioChoices(WordAudioOutcome Outcome, IReadOnlyList<string> Names);

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

    /// <summary>A sound's name longer than this is cut.</summary>
    internal const int MaxName = 60;

    /// <summary>The name of the one sound of a "Custom URL" source, which answers with the sound itself.</summary>
    internal const string DirectName = "Sound";

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
    public Task<WordAudio> FindAsync(string term, string reading, CancellationToken cancellationToken)
        => FindAsync(term, reading, null, cancellationToken);

    /// <summary>
    /// Finds how a word sounds: the sound of the source's list with the name given, or without one the first sound
    /// of the list that can be fetched.
    /// </summary>
    /// <param name="term">The word as the dictionary writes it.</param>
    /// <param name="reading">Its reading; the word itself where it is empty.</param>
    /// <param name="source">The name of the sound wanted, as <see cref="ChoicesAsync"/> gives it; null for the first that works.</param>
    /// <param name="cancellationToken">Ends the search.</param>
    /// <returns>The sound, or why there is none.</returns>
    public Task<WordAudio> FindAsync(string term, string reading, string? source, CancellationToken cancellationToken)
        => AskAsync(term, reading, new WordAudio(WordAudioOutcome.NoSource), new WordAudio(WordAudioOutcome.NotFound), new WordAudio(WordAudioOutcome.Failed), async (address, first, token) =>
        {
            // A "Custom URL" source answers with the sound itself.
            if (AudioOf(first) is { } direct)
            {
                return source is null || source == DirectName ? direct : new WordAudio(WordAudioOutcome.NotFound);
            }

            if (SoundsOf(first) is not { } sounds)
            {
                _logger.LogWarning("The word audio source at {Host} answered with neither a sound nor Yomitan's list of sounds.", address.Host);
                return new WordAudio(WordAudioOutcome.Failed);
            }

            if (source is not null)
            {
                var chosen = sounds.Find(sound => sound.Name == source);
                return chosen is not null && await GetAsync(chosen.Address, token).ConfigureAwait(false) is { } fetched && AudioOf(fetched) is { } sound
                    ? sound
                    : new WordAudio(WordAudioOutcome.NotFound);
            }

            var tried = 0;
            foreach (var sound in sounds)
            {
                if (tried++ == MaxTries)
                {
                    break;
                }

                if (await GetAsync(sound.Address, token).ConfigureAwait(false) is { } fetched && AudioOf(fetched) is { } found)
                {
                    return found;
                }
            }

            return new WordAudio(WordAudioOutcome.NotFound);
        }, cancellationToken);

    /// <summary>
    /// The sounds the source has for a word, by name, for the user to choose one: a "Custom URL (JSON)" source's
    /// list, or the one sound of a "Custom URL" source. Only the names leave the server, never the addresses.
    /// </summary>
    /// <param name="term">The word as the dictionary writes it.</param>
    /// <param name="reading">Its reading; the word itself where it is empty.</param>
    /// <param name="cancellationToken">Ends the search.</param>
    /// <returns>The names, or why there are none.</returns>
    public Task<WordAudioChoices> ChoicesAsync(string term, string reading, CancellationToken cancellationToken)
        => AskAsync(term, reading, new WordAudioChoices(WordAudioOutcome.NoSource, []), new WordAudioChoices(WordAudioOutcome.Found, []), new WordAudioChoices(WordAudioOutcome.Failed, []), (address, first, _) =>
        {
            if (AudioOf(first) is not null)
            {
                return Task.FromResult(new WordAudioChoices(WordAudioOutcome.Found, [DirectName]));
            }

            if (SoundsOf(first) is not { } sounds)
            {
                _logger.LogWarning("The word audio source at {Host} answered with neither a sound nor Yomitan's list of sounds.", address.Host);
                return Task.FromResult(new WordAudioChoices(WordAudioOutcome.Failed, []));
            }

            return Task.FromResult(new WordAudioChoices(WordAudioOutcome.Found, sounds.ConvertAll(sound => sound.Name)));
        }, cancellationToken);

    /// <summary>Asks the source about a word and reads its first answer, at most four words at a time and within <see cref="Limit"/>.</summary>
    private async Task<T> AskAsync<T>(string term, string reading, T noSource, T nothing, T failed, Func<Uri, Fetched, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        var template = Address;
        if (template.Length == 0)
        {
            return noSource;
        }

        term = term.Trim();
        reading = string.IsNullOrWhiteSpace(reading) ? term : reading.Trim();
        if (term.Length == 0 || Fill(template, term, reading) is not { } address)
        {
            return nothing;
        }

        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Limit);
            var first = await GetAsync(address, limit.Token).ConfigureAwait(false);
            return first is null ? failed : await read(address, first, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The word audio source at {Host} did not answer in time.", address.Host);
            return failed;
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

    /// <summary>
    /// The sounds of Yomitan's list, in its order, each with a name of its own: the list's name, cut to
    /// <see cref="MaxName"/>, "Sound 2" where it has none, and "nhk16 (2)" for a name said before. Null when the answer
    /// is not such a list.
    /// </summary>
    private static List<NamedSound>? SoundsOf(Fetched fetched)
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

            var sounds = new List<NamedSound>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(fetched.Address, url.GetString(), out var address)
                    && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp))
                {
                    var given = item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()!.Trim() : string.Empty;
                    if (given.Length > MaxName)
                    {
                        given = given[..MaxName];
                    }

                    var unique = given.Length > 0 ? given : $"Sound {sounds.Count + 1}";
                    for (var count = 2; !names.Add(unique); count++)
                    {
                        unique = $"{(given.Length > 0 ? given : $"Sound {sounds.Count + 1}")} ({count})";
                    }

                    sounds.Add(new NamedSound(unique, address));
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

    private sealed record NamedSound(string Name, Uri Address);
}
