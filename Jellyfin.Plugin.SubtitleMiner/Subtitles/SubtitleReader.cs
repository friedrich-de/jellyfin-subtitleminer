using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleMiner.Subtitles;

/// <summary>
/// Reads a subtitle file of any format the plugin knows and tidies the track as a whole: it puts the
/// lines in the order of time, drops repeats, joins a text that typesetting cut into many short lines,
/// and decides for each line whether it is dialogue, a sign or a song.
/// </summary>
internal static partial class SubtitleReader
{
    /// <summary>The gap, in milliseconds, up to which two lines with the same text count as one.</summary>
    private const long JoinGapMs = 50;

    /// <summary>
    /// Reads a file.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="format">The format, if it is known: <c>ass</c>, <c>ssa</c>, <c>srt</c> or <c>vtt</c>. Otherwise it is read off the file.</param>
    /// <returns>The track.</returns>
    public static SubtitleDocument Read(ReadOnlySpan<byte> bytes, string? format = null)
    {
        var text = TextDecoder.Decode(bytes);
        var isAss = format?.ToLowerInvariant() switch
        {
            "ass" or "ssa" => true,
            "srt" or "subrip" or "vtt" or "webvtt" => false,
            _ => text.Contains("[Events]", StringComparison.OrdinalIgnoreCase) && text.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase),
        };
        if (isAss)
        {
            return new SubtitleDocument("ass", Tidy(AssReader.Read(text)));
        }

        var isVtt = text.AsSpan().TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal);
        return new SubtitleDocument(isVtt ? "vtt" : "srt", Tidy(SrtReader.Read(text)));
    }

    /// <summary>
    /// Tidies the lines of one track.
    /// </summary>
    internal static List<SubtitleLine> Tidy(List<RawLine> raw)
    {
        var ordered = raw
            .Where(line => line.EndMs > line.StartMs)
            .Select((line, index) => (line, index))
            .OrderBy(pair => pair.line.StartMs)
            .ThenBy(pair => pair.line.EndMs)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.line)
            .ToList();

        // Typesetting repeats a text on several layers at the same time, and cuts a moving sign into one
        // line per frame. Both are one line to a reader.
        var kept = new List<RawLine>(ordered.Count);
        var latest = new Dictionary<(string? Style, string Text), int>();
        var seen = new HashSet<(long Start, long End, string Text)>();
        foreach (var line in ordered)
        {
            var key = (line.Style, line.Text);
            if (latest.TryGetValue(key, out var at) && line.StartMs <= kept[at].EndMs + JoinGapMs)
            {
                var earlier = kept[at];
                kept[at] = earlier with
                {
                    EndMs = Math.Max(earlier.EndMs, line.EndMs),
                    Positioned = earlier.Positioned || line.Positioned,
                    Sung = earlier.Sung || line.Sung,
                };
                continue;
            }

            if (!seen.Add((line.StartMs, line.EndMs, line.Text)))
            {
                continue;
            }

            latest[key] = kept.Count;
            kept.Add(line);
        }

        var styles = kept
            .Where(line => line.Style is not null)
            .GroupBy(line => line.Style!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, KindOfStyle, StringComparer.Ordinal);

        var result = new List<SubtitleLine>(kept.Count);
        foreach (var line in kept)
        {
            var kind = line.Style is not null && styles.TryGetValue(line.Style, out var styleKind) ? styleKind : LineKind.Dialogue;
            if (line.Sung)
            {
                kind = LineKind.Song;
            }
            else if (line.Positioned && kind == LineKind.Dialogue)
            {
                kind = LineKind.Sign;
            }

            result.Add(new SubtitleLine(result.Count, line.StartMs, line.EndMs, line.Text, line.Style, line.Name, kind));
        }

        return result;
    }

    /// <summary>
    /// What the lines of a style are. Its name says it in most files; where it does not, a style whose
    /// lines are mostly placed on the picture holds signs, and one that is mostly karaoke holds songs.
    /// </summary>
    private static LineKind KindOfStyle(IGrouping<string, RawLine> style)
    {
        var count = style.Count();
        if (style.Count(line => line.Sung) * 5 >= count * 3 || SongName().IsMatch(style.Key))
        {
            return LineKind.Song;
        }

        if (style.Count(line => line.Positioned) * 5 >= count * 3 || SignName().IsMatch(style.Key))
        {
            return LineKind.Sign;
        }

        return LineKind.Dialogue;
    }

    [GeneratedRegex(@"(?<![a-z])(op|ed)(?![a-z])|opening|ending|song|karaoke|kara(?![a-z])|lyric|romaji|insert", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SongName();

    [GeneratedRegex(@"sign|title|caption|(?<![a-z])note|typeset|(?<![a-z])ts(?![a-z])|credit|staff|logo|eyecatch|preview|screen|card", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignName();
}
