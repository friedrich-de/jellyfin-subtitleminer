namespace Jellyfin.Plugin.SubtitleMiner.Settings;

/// <summary>
/// The settings an administrator makes for everyone on the server.
/// </summary>
internal interface ISharedSettings
{
    /// <summary>
    /// Gets a value indicating whether users who are not administrators may send cards through the server.
    /// </summary>
    bool AnkiThroughServerForEveryone { get; }

    /// <summary>
    /// Changes whether users who are not administrators may send cards through the server, and saves it.
    /// </summary>
    /// <param name="allowed">The new value.</param>
    void SetAnkiThroughServerForEveryone(bool allowed);

    /// <summary>
    /// Gets the address the server fetches how a word sounds from, as stored: encrypted, or empty for none.
    /// </summary>
    string WordAudioAddress { get; }

    /// <summary>
    /// Changes the address the server fetches how a word sounds from, and saves it.
    /// </summary>
    /// <param name="stored">The address, already encrypted, or empty for none.</param>
    void SetWordAudioAddress(string stored);
}

/// <summary>
/// The shared settings in the plugin's configuration, which Jellyfin keeps.
/// </summary>
internal sealed class PluginSharedSettings : ISharedSettings
{
    public bool AnkiThroughServerForEveryone => Plugin.Instance?.Configuration.AnkiThroughServerForEveryone ?? false;

    public void SetAnkiThroughServerForEveryone(bool allowed)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        plugin.Configuration.AnkiThroughServerForEveryone = allowed;
        plugin.SaveConfiguration();
    }

    public string WordAudioAddress => Plugin.Instance?.Configuration.WordAudioAddress ?? string.Empty;

    public void SetWordAudioAddress(string stored)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        plugin.Configuration.WordAudioAddress = stored;
        plugin.SaveConfiguration();
    }
}
