using System.Collections.Generic;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// One subtitle line, as the page shows it.
/// </summary>
public sealed record LineView
{
    /// <summary>Gets the line's number in the track.</summary>
    public required int Id { get; init; }

    /// <summary>Gets when the line appears, in milliseconds.</summary>
    public required long StartMs { get; init; }

    /// <summary>Gets when the line disappears, in milliseconds.</summary>
    public required long EndMs { get; init; }

    /// <summary>Gets the text, with a line feed between its rows.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the line's style, in a track that has styles.</summary>
    public string? Style { get; init; }

    /// <summary>Gets the speaker the track names.</summary>
    public string? Name { get; init; }

    /// <summary>Gets what the line is: <c>dialogue</c>, <c>sign</c> or <c>song</c>.</summary>
    public required string Kind { get; init; }
}

/// <summary>
/// The lines of one subtitle track.
/// </summary>
public sealed record SubtitlesView
{
    /// <summary>Gets the key of the track.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the format the track was read as.</summary>
    public required string Format { get; init; }

    /// <summary>Gets the lines in the order of time.</summary>
    public required IReadOnlyList<LineView> Lines { get; init; }
}
