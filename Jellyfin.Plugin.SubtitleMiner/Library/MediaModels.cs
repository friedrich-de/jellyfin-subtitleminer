using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleMiner.Library;

/// <summary>
/// What a stream of a video holds.
/// </summary>
internal enum StreamKind
{
    /// <summary>The picture.</summary>
    Video,

    /// <summary>A sound track.</summary>
    Audio,

    /// <summary>A subtitle track.</summary>
    Subtitle,
}

/// <summary>
/// One stream of a video: inside its file, or a subtitle file that lies next to it.
/// </summary>
/// <param name="Key">
/// A name for the stream that does not move. Jellyfin's own numbers do: it counts the subtitle files next
/// to a video before the streams inside it, so every number shifts when such a file is added.
/// </param>
/// <param name="Kind">What the stream holds.</param>
/// <param name="Index">Jellyfin's number of the stream, as players report it.</param>
/// <param name="Ordinal">The stream's place among the streams of its kind inside the file, which is how ffmpeg counts. -1 for a file of its own.</param>
/// <param name="Codec">The codec's name in lower case.</param>
/// <param name="Language">The language code the file gives.</param>
/// <param name="Title">The title the file gives.</param>
/// <param name="IsExternal">Whether the stream is a file of its own.</param>
/// <param name="IsDefault">Whether the file marks the stream as the default.</param>
/// <param name="IsForced">Whether the file marks a subtitle track as forced.</param>
/// <param name="Path">The file of an external stream.</param>
internal sealed record MediaStreamInfo(
    string Key,
    StreamKind Kind,
    int Index,
    int Ordinal,
    string Codec,
    string? Language,
    string? Title,
    bool IsExternal,
    bool IsDefault,
    bool IsForced,
    string? Path)
{
    private static readonly string[] _textInside = ["ass", "ssa", "srt", "subrip", "vtt", "webvtt", "mov_text", "text"];
    private static readonly string[] _textFiles = ["ass", "ssa", "srt", "subrip", "vtt", "webvtt"];

    /// <summary>
    /// Gets a value indicating whether the plugin can read the lines of this subtitle track. Tracks made
    /// of pictures, as on a Blu-ray, cannot be read.
    /// </summary>
    public bool IsReadable => Kind == StreamKind.Subtitle && (IsExternal ? _textFiles : _textInside).Contains(Codec, StringComparer.Ordinal);
}

/// <summary>
/// How a video is called.
/// </summary>
/// <param name="Name">The film's or the episode's name.</param>
/// <param name="SeriesName">The show an episode belongs to.</param>
/// <param name="Season">The season's number.</param>
/// <param name="Episode">The episode's number.</param>
/// <param name="Year">The year of a film.</param>
internal sealed record MediaTitle(string Name, string? SeriesName, int? Season, int? Episode, int? Year);

/// <summary>
/// A video as the plugin needs it: where its file is and what streams it has.
/// </summary>
/// <param name="ItemId">Jellyfin's id of the video.</param>
/// <param name="MediaSourceId">The version of the video.</param>
/// <param name="Path">The video file, when the server can read it as a file. Otherwise <c>null</c>.</param>
/// <param name="Stamp">Changes when the video file does, so what was read from it can be told to be stale.</param>
/// <param name="Title">How the video is called.</param>
/// <param name="RuntimeMs">Its length.</param>
/// <param name="Streams">Its streams.</param>
internal sealed record MediaInfo(
    Guid ItemId,
    string MediaSourceId,
    string? Path,
    string Stamp,
    MediaTitle Title,
    long? RuntimeMs,
    IReadOnlyList<MediaStreamInfo> Streams)
{
    /// <summary>
    /// Finds a stream by its key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="kind">The kind the stream must be of.</param>
    /// <returns>The stream, or <c>null</c>.</returns>
    public MediaStreamInfo? Stream(string? key, StreamKind kind) =>
        key is null ? null : Streams.FirstOrDefault(stream => stream.Kind == kind && string.Equals(stream.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Finds a stream by Jellyfin's number, as a player reports it.
    /// </summary>
    /// <param name="index">The number.</param>
    /// <param name="kind">The kind the stream must be of.</param>
    /// <returns>The stream, or <c>null</c>.</returns>
    public MediaStreamInfo? StreamAt(int? index, StreamKind kind) =>
        index is null ? null : Streams.FirstOrDefault(stream => stream.Kind == kind && stream.Index == index);
}

/// <summary>
/// Reads videos from the Jellyfin library.
/// </summary>
internal interface IMediaLibrary
{
    /// <summary>
    /// Finds a video a user may watch.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The video.</param>
    /// <param name="mediaSourceId">The version of the video, or <c>null</c> for the first.</param>
    /// <returns>The video, or <c>null</c> when there is none or the user may not see it.</returns>
    MediaInfo? Find(Guid userId, Guid itemId, string? mediaSourceId);
}
