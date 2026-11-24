using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleMiner.Subtitles;

/// <summary>
/// Reads SubRip (.srt) and WebVTT (.vtt) files. The two differ in little: WebVTT has a header, blocks
/// that are no cues, a dot before the milliseconds and settings behind the time codes.
/// </summary>
internal static partial class SrtReader
{
    /// <summary>
    /// Reads the cues of a file.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The cues in the order of the file.</returns>
    public static List<RawLine> Read(string text)
    {
        var lines = new List<RawLine>();
        var rows = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < rows.Length)
        {
            var match = Timing().Match(rows[index]);
            if (!match.Success)
            {
                // A WebVTT block that is no cue runs to the next empty row and holds no time code.
                index++;
                continue;
            }

            index++;
            var body = new StringBuilder();
            while (index < rows.Length && rows[index].Trim().Length > 0)
            {
                // Some files leave out the empty row between two cues.
                if (Timing().IsMatch(rows[index]))
                {
                    RemoveCueNumber(body);
                    break;
                }

                body.Append(rows[index]).Append('\n');
                index++;
            }

            var start = ToMilliseconds(match, 1);
            var end = ToMilliseconds(match, 5);
            var clean = TextCleaner.Tidy(TextCleaner.RemoveReadings(TextCleaner.StripMarkup(body.ToString(), out var voice)));
            if (clean.Length > 0)
            {
                lines.Add(new RawLine(start, end, clean, null, voice, false, TextCleaner.IsMarkedAsSung(clean)));
            }
        }

        return lines;
    }

    /// <summary>
    /// The number of the next cue, when it was read as the last row of this one.
    /// </summary>
    private static void RemoveCueNumber(StringBuilder body)
    {
        var text = body.ToString().TrimEnd('\n');
        var lastBreak = text.LastIndexOf('\n');
        var lastRow = text[(lastBreak + 1)..].Trim();
        if (lastRow.Length > 0 && long.TryParse(lastRow, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            body.Clear();
            body.Append(lastBreak < 0 ? string.Empty : text[..lastBreak]);
        }
    }

    private static long ToMilliseconds(Match match, int firstGroup)
    {
        var hours = match.Groups[firstGroup].Success ? long.Parse(match.Groups[firstGroup].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = long.Parse(match.Groups[firstGroup + 1].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(match.Groups[firstGroup + 2].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[firstGroup + 3].Value.PadRight(3, '0')[..3];
        return (((hours * 60) + minutes) * 60 * 1000) + (seconds * 1000) + long.Parse(fraction, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\s*(?:(\d+):)?(\d{1,2}):(\d{1,2})[.,](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{1,2})[.,](\d{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex Timing();
}
