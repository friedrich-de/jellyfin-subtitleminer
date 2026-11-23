using System;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Media;
using Jellyfin.Plugin.SubtitleMiner.Playback;
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
        TimeProvider time)
    {
        Page = page;
        Callers = callers;
        Server = server;
        Tracker = tracker;
        Library = library;
        Subtitles = subtitles;
        Cutter = cutter;
        Time = time;
    }

    internal PageFiles Page { get; }

    internal ICallerResolver Callers { get; }

    internal ServerInfo Server { get; }

    internal PlaybackTracker Tracker { get; }

    internal IMediaLibrary Library { get; }

    internal SubtitleStore Subtitles { get; }

    internal MediaCutter Cutter { get; }

    internal TimeProvider Time { get; }
}
