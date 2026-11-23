using Jellyfin.Plugin.SubtitleMiner.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleMiner;

/// <summary>
/// Registers the plugin's services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ICallerResolver, CallerResolver>();
        serviceCollection.AddSingleton(provider => new PluginServices(
            new PageFiles(new EmbeddedPageFiles(typeof(Plugin).Assembly)),
            provider.GetRequiredService<ICallerResolver>(),
            new ServerInfo(applicationHost.FriendlyName, applicationHost.ApplicationVersionString)));
    }
}
