using System;

namespace Jellyfin.Plugin.SubtitleMiner.Playback;

/// <summary>
/// What a player told Jellyfin about its playback at one moment.
/// </summary>
/// <param name="SessionId">Jellyfin's id of the player's session.</param>
/// <param name="UserId">The user who is watching.</param>
/// <param name="Player">The device's name, such as "Living room TV".</param>
/// <param name="Client">The app's name, such as "Jellyfin Web".</param>
/// <param name="ItemId">The video.</param>
/// <param name="MediaSourceId">The version of the video that plays.</param>
/// <param name="PositionMs">Where playback is, in milliseconds.</param>
/// <param name="Paused">Whether it is paused.</param>
/// <param name="AudioStreamIndex">Jellyfin's number of the audio track that plays.</param>
/// <param name="SubtitleStreamIndex">Jellyfin's number of the subtitle track that shows.</param>
internal sealed record PlaybackReport(
    string SessionId,
    Guid UserId,
    string Player,
    string Client,
    Guid ItemId,
    string MediaSourceId,
    long PositionMs,
    bool Paused,
    int? AudioStreamIndex,
    int? SubtitleStreamIndex);

/// <summary>
/// One video on one player: playing now, or watched a while ago.
/// </summary>
/// <param name="Id">A name for this watch that stays the same while it is listed.</param>
/// <param name="Report">The latest the player said.</param>
/// <param name="At">When it said so.</param>
/// <param name="Playing">Whether the player still has the video open.</param>
/// <param name="StoppedAt">When playback ended, for a watch that is over.</param>
internal sealed record Watch(string Id, PlaybackReport Report, DateTimeOffset At, bool Playing, DateTimeOffset? StoppedAt)
{
    /// <summary>
    /// Where playback is at a later moment, going by the latest report.
    /// </summary>
    /// <param name="now">The moment.</param>
    /// <returns>The position in milliseconds.</returns>
    public long PositionAt(DateTimeOffset now) =>
        Playing && !Report.Paused ? Report.PositionMs + Math.Max(0, (long)(now - At).TotalMilliseconds) : Report.PositionMs;
}
