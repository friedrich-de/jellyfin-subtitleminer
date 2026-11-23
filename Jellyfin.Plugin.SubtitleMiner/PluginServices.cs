using Jellyfin.Plugin.SubtitleMiner.Api;

namespace Jellyfin.Plugin.SubtitleMiner;

/// <summary>
/// The plugin's own services in one object, so the controllers Jellyfin creates can ask for them.
/// </summary>
public sealed class PluginServices
{
    internal PluginServices(PageFiles page, ICallerResolver callers, ServerInfo server)
    {
        Page = page;
        Callers = callers;
        Server = server;
    }

    internal PageFiles Page { get; }

    internal ICallerResolver Callers { get; }

    internal ServerInfo Server { get; }
}
