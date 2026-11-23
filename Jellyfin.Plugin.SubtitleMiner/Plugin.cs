using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SubtitleMiner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SubtitleMiner;

/// <summary>
/// The Subtitle Miner plugin: a page that follows the subtitles of what is playing on the server and makes
/// Anki cards from them.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The name Jellyfin shows for the plugin.
    /// </summary>
    public const string PluginName = "Subtitle Miner";

    /// <summary>
    /// The name of the dashboard page, as used in <c>/web/ConfigurationPage?name=</c>.
    /// </summary>
    public const string ConfigPageName = "SubtitleMiner";

    /// <summary>
    /// The embedded resource that holds the dashboard page.
    /// </summary>
    public const string ConfigPageResource = "Jellyfin.Plugin.SubtitleMiner.Configuration.configPage.html";

    /// <summary>
    /// The plugin's fixed id. It is the id of the versions before 2.0, so an installed plugin updates in place.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("8e947a62-b5e1-4d6a-a70c-486996db901f");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the pages Jellyfin's dashboard serves for this plugin. Only administrators can open them.
    /// </summary>
    public static IReadOnlyList<PluginPageInfo> Pages { get; } =
    [
        new PluginPageInfo
        {
            Name = ConfigPageName,
            DisplayName = PluginName,
            EmbeddedResourcePath = ConfigPageResource,
            EnableInMainMenu = true,
            MenuIcon = "translate"
        }
    ];

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Follows the subtitles of what is playing and makes Anki cards from them, with a picture and the audio.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => Pages;
}
