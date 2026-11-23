using System.Collections.Generic;

namespace Jellyfin.Plugin.SubtitleMiner.Subtitles;

/// <summary>
/// What a subtitle line is, as far as the plugin can tell.
/// </summary>
internal enum LineKind
{
    /// <summary>Something a character says.</summary>
    Dialogue,

    /// <summary>Text placed on the picture: a sign, a title, a translator's note.</summary>
    Sign,

    /// <summary>A line of a song, usually the opening or the ending.</summary>
    Song,
}

/// <summary>
/// One subtitle line, cleaned of formatting.
/// </summary>
/// <param name="Id">The line's number in the track, counted from 0 in the order of time.</param>
/// <param name="StartMs">When the line appears, in milliseconds from the start of the video.</param>
/// <param name="EndMs">When it disappears.</param>
/// <param name="Text">The text, with a line feed between its rows.</param>
/// <param name="Style">The line's style in an ASS track.</param>
/// <param name="Name">The speaker the track names for the line.</param>
/// <param name="Kind">What the line is.</param>
internal sealed record SubtitleLine(int Id, long StartMs, long EndMs, string Text, string? Style, string? Name, LineKind Kind);

/// <summary>
/// The lines of one subtitle track.
/// </summary>
/// <param name="Format">The format the track was read as: <c>ass</c>, <c>srt</c> or <c>vtt</c>.</param>
/// <param name="Lines">The lines in the order of time.</param>
internal sealed record SubtitleDocument(string Format, IReadOnlyList<SubtitleLine> Lines);

/// <summary>
/// A line as a reader found it, before the track as a whole is cleaned up.
/// </summary>
/// <param name="StartMs">When the line appears.</param>
/// <param name="EndMs">When it disappears.</param>
/// <param name="Text">The text without formatting.</param>
/// <param name="Style">The style's name, if the format has styles.</param>
/// <param name="Name">The speaker, if the format names one.</param>
/// <param name="Positioned">Whether the line is placed at a point of the picture.</param>
/// <param name="Sung">Whether the line carries karaoke timing.</param>
internal sealed record RawLine(long StartMs, long EndMs, string Text, string? Style, string? Name, bool Positioned, bool Sung);
