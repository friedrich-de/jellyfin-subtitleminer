using System;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleMiner.Settings;

/// <summary>
/// Where the page sends cards.
/// </summary>
internal static class AnkiWhere
{
    /// <summary>No Anki: the page only shows the lines.</summary>
    public const string Off = "off";

    /// <summary>An AnkiConnect the browser calls itself, on the user's own device.</summary>
    public const string Device = "device";

    /// <summary>An AnkiConnect the Jellyfin server calls for the page, with a key the page never sees.</summary>
    public const string Server = "server";

    public static bool IsKnown(string? value) => value is Off or Device or Server;
}

/// <summary>
/// One user's settings as the plugin keeps them. The keys are stored encrypted.
/// </summary>
internal sealed class UserSettings
{
    /// <summary>The address desktop Anki's AnkiConnect listens on unless told otherwise.</summary>
    public const string DefaultDeviceAddress = "http://127.0.0.1:8765";

    /// <summary>Gets or sets where cards go: one of <see cref="AnkiWhere"/>.</summary>
    public string Where { get; set; } = AnkiWhere.Off;

    /// <summary>Gets or sets the address of the AnkiConnect on the user's device.</summary>
    public string DeviceAddress { get; set; } = DefaultDeviceAddress;

    /// <summary>Gets or sets its key, encrypted. The page gets it back, because the browser sends it.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the address of the AnkiConnect the server calls.</summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets its key, encrypted. It never leaves the server.</summary>
    public string ServerKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what only the page reads: the deck, the note type and its fields, the tags, how pictures and
    /// sound are cut. The plugin keeps it as the page sent it.
    /// </summary>
    public JsonElement? Page { get; set; }
}

/// <summary>
/// The rules for the addresses a user may set.
/// </summary>
internal static class AnkiAddress
{
    /// <summary>The longest address taken.</summary>
    public const int MaxLength = 2048;

    /// <summary>The longest key taken.</summary>
    public const int MaxKeyLength = 512;

    /// <summary>
    /// Reads an address: http or https, with a host, without a name and password in it.
    /// </summary>
    /// <param name="text">The address as typed.</param>
    /// <param name="address">The address, when it is one.</param>
    /// <returns><c>true</c> for an address the plugin can call.</returns>
    public static bool TryRead(string? text, out Uri? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return false;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        address = uri;
        return true;
    }
}
