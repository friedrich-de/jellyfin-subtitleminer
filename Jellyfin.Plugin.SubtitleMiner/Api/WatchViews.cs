using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubtitleMiner.Library;
using Jellyfin.Plugin.SubtitleMiner.Playback;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// A subtitle or audio track of a video, as the page lists it.
/// </summary>
public sealed record TrackView
{
    /// <summary>Gets the name the page asks for the track by.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the language code the file gives, if it gives one.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the title the file gives, if it gives one.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the codec's name.</summary>
    public required string Codec { get; init; }

    /// <summary>Gets a value indicating whether the track is a file next to the video.</summary>
    public required bool External { get; init; }

    /// <summary>Gets a value indicating whether the file marks the track as the default.</summary>
    public required bool Default { get; init; }

    /// <summary>Gets a value indicating whether the file marks a subtitle track as forced.</summary>
    public required bool Forced { get; init; }

    /// <summary>Gets a value indicating whether the plugin can read the track's lines. Tracks made of pictures cannot be read.</summary>
    public required bool Readable { get; init; }
}

/// <summary>
/// A video on a player, playing now or watched a while ago, as the page shows it.
/// </summary>
public sealed record WatchView
{
    /// <summary>Gets a name for this watch that stays the same while it is listed.</summary>
    public required string Id { get; init; }

    /// <summary>Gets Jellyfin's id of the video.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the version of the video.</summary>
    public required string MediaSourceId { get; init; }

    /// <summary>Gets a value indicating whether the player still has the video open.</summary>
    public required bool Playing { get; init; }

    /// <summary>Gets a value indicating whether playback is paused.</summary>
    public required bool Paused { get; init; }

    /// <summary>Gets where playback is at the moment this was sent, in milliseconds.</summary>
    public required long PositionMs { get; init; }

    /// <summary>Gets the video's length in milliseconds.</summary>
    public long? RuntimeMs { get; init; }

    /// <summary>Gets how long ago playback ended, for a watch that is over.</summary>
    public long? StoppedAgoMs { get; init; }

    /// <summary>Gets the device's name.</summary>
    public required string Player { get; init; }

    /// <summary>Gets the app's name.</summary>
    public required string Client { get; init; }

    /// <summary>Gets the film's or the episode's name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the show an episode belongs to.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the season's number.</summary>
    public int? Season { get; init; }

    /// <summary>Gets the episode's number.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets the year of a film.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the key of the subtitle track the player shows.</summary>
    public string? SubtitleKey { get; init; }

    /// <summary>Gets the key of the audio track the player plays.</summary>
    public string? AudioKey { get; init; }

    /// <summary>Gets a value indicating whether pictures and sound can be cut from the video.</summary>
    public required bool CanCut { get; init; }

    /// <summary>Gets the video's subtitle tracks.</summary>
    public required IReadOnlyList<TrackView> Subtitles { get; init; }

    /// <summary>Gets the video's audio tracks.</summary>
    public required IReadOnlyList<TrackView> Audio { get; init; }
}

/// <summary>
/// The answer that lists a user's watches.
/// </summary>
public sealed record WatchListView
{
    /// <summary>Gets what plays now, then what was watched lately.</summary>
    public required IReadOnlyList<WatchView> Watches { get; init; }
}

/// <summary>
/// Turns what the tracker remembers into what the page shows, with the video's name and tracks read
/// from the library at that moment.
/// </summary>
internal static class WatchViews
{
    /// <summary>
    /// Builds the list for one user. A video the user may no longer see is left out.
    /// </summary>
    public static WatchListView Build(IReadOnlyList<Watch> watches, IMediaLibrary library, Guid userId, DateTimeOffset now)
    {
        var views = new List<WatchView>(watches.Count);
        foreach (var watch in watches)
        {
            var report = watch.Report;
            var media = library.Find(userId, report.ItemId, report.MediaSourceId) ?? library.Find(userId, report.ItemId, null);
            if (media is null)
            {
                continue;
            }

            views.Add(new WatchView
            {
                Id = watch.Id,
                ItemId = media.ItemId.ToString("N"),
                MediaSourceId = media.MediaSourceId,
                Playing = watch.Playing,
                Paused = report.Paused,
                PositionMs = watch.PositionAt(now),
                RuntimeMs = media.RuntimeMs,
                StoppedAgoMs = watch.StoppedAt is null ? null : Math.Max(0, (long)(now - watch.StoppedAt.Value).TotalMilliseconds),
                Player = report.Player,
                Client = report.Client,
                Name = media.Title.Name,
                SeriesName = media.Title.SeriesName,
                Season = media.Title.Season,
                Episode = media.Title.Episode,
                Year = media.Title.Year,
                SubtitleKey = media.StreamAt(report.SubtitleStreamIndex, StreamKind.Subtitle)?.Key,
                AudioKey = media.StreamAt(report.AudioStreamIndex, StreamKind.Audio)?.Key,
                CanCut = media.Path is not null,
                Subtitles = Tracks(media, StreamKind.Subtitle),
                Audio = Tracks(media, StreamKind.Audio),
            });
        }

        return new WatchListView { Watches = views };
    }

    private static List<TrackView> Tracks(MediaInfo media, StreamKind kind) => media.Streams
        .Where(stream => stream.Kind == kind && (kind == StreamKind.Subtitle || !stream.IsExternal))
        .Select(stream => new TrackView
        {
            Key = stream.Key,
            Language = stream.Language,
            Title = stream.Title,
            Codec = stream.Codec,
            External = stream.IsExternal,
            Default = stream.IsDefault,
            Forced = stream.IsForced,
            Readable = kind == StreamKind.Audio || (stream.IsReadable && (stream.IsExternal || media.Path is not null)),
        })
        .ToList();
}
