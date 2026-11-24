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
}
