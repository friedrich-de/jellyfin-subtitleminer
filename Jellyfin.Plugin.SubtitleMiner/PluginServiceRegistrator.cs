using System;
using System.IO;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Jellyfin.Plugin.SubtitleMiner.Playback;
using Jellyfin.Plugin.SubtitleMiner.Settings;
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

    /// <summary>
    /// The plugin's folder inside Jellyfin's data folder: the users' settings and the secret that encrypts their keys.
    /// </summary>
    internal const string DataFolderName = "subtitle-miner";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        static string Scratch(IServiceProvider provider) =>
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().TempDirectory, ScratchFolderName);
        static string Data(IServiceProvider provider) =>
            Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, DataFolderName);

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
        serviceCollection.AddSingleton(provider => new UserSettingsStore(
            Path.Combine(Data(provider), "users"),
            new KeyProtector(Path.Combine(Data(provider), KeyProtector.FileName)),
            provider.GetRequiredService<ILogger<UserSettingsStore>>()));
        serviceCollection.AddSingleton<ISharedSettings, PluginSharedSettings>();
        serviceCollection.AddSingleton(provider => new AnkiClient(AnkiClient.CreateHandler(), provider.GetRequiredService<ILogger<AnkiClient>>()));
        serviceCollection.AddSingleton(provider => new AnkiGate(provider.GetRequiredService<UserSettingsStore>(), provider.GetRequiredService<ISharedSettings>()));
        serviceCollection.AddSingleton(provider => new DictionaryServices(
            new DictionaryLibrary(Path.Combine(Data(provider), "dictionaries"), provider.GetRequiredService<ILogger<DictionaryLibrary>>()),
            new DictionaryDownloader(DictionaryDownloader.CreateHandler()),
            Path.Combine(Scratch(provider), "dictionaries")));
        serviceCollection.AddSingleton(provider => new PluginServices(
            new PageFiles(new EmbeddedPageFiles(typeof(Plugin).Assembly)),
            provider.GetRequiredService<ICallerResolver>(),
            new ServerInfo(applicationHost.FriendlyName, applicationHost.ApplicationVersionString),
            provider.GetRequiredService<PlaybackTracker>(),
            provider.GetRequiredService<IMediaLibrary>(),
            provider.GetRequiredService<SubtitleStore>(),
            provider.GetRequiredService<MediaCutter>(),
            provider.GetRequiredService<UserSettingsStore>(),
            provider.GetRequiredService<ISharedSettings>(),
            provider.GetRequiredService<AnkiClient>(),
            provider.GetRequiredService<AnkiGate>(),
            provider.GetRequiredService<DictionaryServices>(),
            TimeProvider.System));
    }
}
