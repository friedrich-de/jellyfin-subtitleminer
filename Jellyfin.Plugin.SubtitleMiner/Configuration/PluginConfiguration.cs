using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubtitleMiner.Configuration;

/// <summary>
/// The plugin's server-wide settings. Jellyfin stores them as XML on the host, readable by its administrator.
/// Each user's own settings are files of their own, see <c>Settings.UserSettingsStore</c>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether users who are not administrators may have the server send their
    /// cards to an AnkiConnect address of their choosing. Off by default: the server would call any address
    /// such a user types, also ones inside its own network.
    /// </summary>
    public bool AnkiThroughServerForEveryone { get; set; }

    /// <summary>
    /// Gets or sets where the server fetches how a word sounds, as Yomitan's custom audio sources take it: an address
    /// with <c>{term}</c> and <c>{reading}</c> in it. Encrypted, since such an address often holds a key. Empty for none.
    /// </summary>
    public string WordAudioAddress { get; set; } = string.Empty;
}
