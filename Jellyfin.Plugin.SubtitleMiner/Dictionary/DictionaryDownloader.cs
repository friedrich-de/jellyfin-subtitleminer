using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// Downloads a dictionary's zip for an administrator, from the address they gave, into the plugin's folder for
/// temporary files. Nothing about the server or its users is sent along.
/// </summary>
internal sealed class DictionaryDownloader : IDisposable
{
    /// <summary>A download larger than this is stopped.</summary>
    internal const long MaxBytes = 2L * 1024 * 1024 * 1024;

    private readonly HttpClient _client;

    /// <summary>Initializes a new instance of the <see cref="DictionaryDownloader"/> class.</summary>
    /// <param name="handler">What sends the requests.</param>
    public DictionaryDownloader(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("SubtitleMiner/2.0 (Jellyfin plugin)");
    }

    /// <summary>The handler the plugin downloads with: redirects followed (release downloads redirect), a proxy the
    /// environment names used.</summary>
    /// <returns>The handler.</returns>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };

    /// <summary>Whether an address is one a dictionary may be downloaded from: http or https, without a name and password.</summary>
    /// <param name="text">The address.</param>
    /// <param name="address">The address read.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryRead(string? text, out Uri? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var read)
            || (read.Scheme != Uri.UriSchemeHttps && read.Scheme != Uri.UriSchemeHttp) || read.UserInfo.Length > 0)
        {
            return false;
        }

        address = read;
        return true;
    }

    /// <summary>Downloads <paramref name="address"/> into <paramref name="folder"/>.</summary>
    /// <param name="address">The address.</param>
    /// <param name="folder">Where the zip goes.</param>
    /// <param name="progress">Told how far the download is, when the server says how large it is.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    /// <returns>The zip's path.</returns>
    public async Task<string> DownloadAsync(Uri address, string folder, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DictionaryFetchException($"The address answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var length = response.Content.Headers.ContentLength;
        if (length > MaxBytes)
        {
            throw new DictionaryFetchException("That download is larger than a dictionary can be.");
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var target = File.Create(path);
                await using (target.ConfigureAwait(false))
                {
                    await CopyAsync(source, target, length, progress, cancellationToken).ConfigureAwait(false);
                }
            }

            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>Copies a download or an upload, stopping when it grows larger than a dictionary can be.</summary>
    /// <param name="source">What is copied.</param>
    /// <param name="target">Where it goes.</param>
    /// <param name="length">Its length, when known.</param>
    /// <param name="progress">Told how far it is, when its length is known.</param>
    /// <param name="cancellationToken">Stops the copy.</param>
    /// <returns>The copy.</returns>
    internal static async Task CopyAsync(Stream source, Stream target, long? length, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > MaxBytes)
            {
                throw new DictionaryFetchException("That file is larger than a dictionary can be.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (length > 0)
            {
                progress?.Report((double)copied / length.Value);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
