using System;
using Jellyfin.Plugin.SubtitleMiner.Anki;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Jellyfin.Plugin.SubtitleMiner.Playback;
using Jellyfin.Plugin.SubtitleMiner.Settings;
using Jellyfin.Plugin.SubtitleMiner.Subtitles;

namespace Jellyfin.Plugin.SubtitleMiner;

/// <summary>
/// The plugin's own services in one object, so the controllers Jellyfin creates can ask for them.
/// </summary>
public sealed class PluginServices
{
    internal PluginServices(
        PageFiles page,
        ICallerResolver callers,
        ServerInfo server,
        PlaybackTracker tracker,
        IMediaLibrary library,
        SubtitleStore subtitles,
        MediaCutter cutter,
        UserSettingsStore settings,
        ISharedSettings shared,
        AnkiClient ankiClient,
        AnkiGate anki,
        TimeProvider time)
    {
        Page = page;
        Callers = callers;
        Server = server;
        Tracker = tracker;
        Library = library;
        Subtitles = subtitles;
        Cutter = cutter;
        Settings = settings;
        Shared = shared;
        AnkiClient = ankiClient;
        Anki = anki;
        Time = time;
    }

    internal PageFiles Page { get; }

    internal ICallerResolver Callers { get; }

    internal ServerInfo Server { get; }

    internal PlaybackTracker Tracker { get; }

    internal IMediaLibrary Library { get; }

    internal SubtitleStore Subtitles { get; }

    internal MediaCutter Cutter { get; }

    internal UserSettingsStore Settings { get; }

    internal ISharedSettings Shared { get; }

    internal AnkiClient AnkiClient { get; }

    internal AnkiGate Anki { get; }

    internal TimeProvider Time { get; }
}
