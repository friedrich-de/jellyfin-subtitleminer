using System;
using System.IO;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Jellyfin.Plugin.SubtitleMiner.Playback;
using Jellyfin.Plugin.SubtitleMiner.Subtitles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleMiner;

/// <summary>
/// Registers the plugin's services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// The folder inside Jellyfin's folder for temporary files where the plugin puts what it cuts.
    /// </summary>
    internal const string ScratchFolderName = "subtitle-miner";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        static string Scratch(IServiceProvider provider) =>
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().TempDirectory, ScratchFolderName);

        serviceCollection.AddSingleton<ICallerResolver, CallerResolver>();
        serviceCollection.AddSingleton(_ => new PlaybackTracker(TimeProvider.System));
        serviceCollection.AddHostedService<PlaybackEvents>();
        serviceCollection.AddSingleton<IMediaLibrary, JellyfinMediaLibrary>();
        serviceCollection.AddSingleton<IFfmpeg, JellyfinFfmpeg>();
        serviceCollection.AddSingleton(provider => new SubtitleStore(
            provider.GetRequiredService<IFfmpeg>(),
            Scratch(provider),
            provider.GetRequiredService<ILogger<SubtitleStore>>()));
        serviceCollection.AddSingleton(provider => new MediaCutter(
            provider.GetRequiredService<IFfmpeg>(),
            Scratch(provider),
            provider.GetRequiredService<ILogger<MediaCutter>>()));
        serviceCollection.AddSingleton(provider => new PluginServices(
            new PageFiles(new EmbeddedPageFiles(typeof(Plugin).Assembly)),
            provider.GetRequiredService<ICallerResolver>(),
            new ServerInfo(applicationHost.FriendlyName, applicationHost.ApplicationVersionString),
            provider.GetRequiredService<PlaybackTracker>(),
            provider.GetRequiredService<IMediaLibrary>(),
            provider.GetRequiredService<SubtitleStore>(),
            provider.GetRequiredService<MediaCutter>(),
            TimeProvider.System));
    }
}
