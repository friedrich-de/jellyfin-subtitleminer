using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.SubtitleMiner.Subtitles;

/// <summary>
/// Reads Advanced SubStation Alpha (.ass) and SubStation Alpha (.ssa) files. It keeps each line's style
/// and speaker and notes whether the line is placed on the picture or carries karaoke timing, which is
/// how signs and songs are told from dialogue. Drawings are left out.
/// </summary>
internal static class AssReader
{
    private static readonly string[] _defaultFormat = ["layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text"];

    /// <summary>
    /// Reads the dialogue lines of a file.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The lines in the order of the file.</returns>
    public static List<RawLine> Read(string text)
    {
        var lines = new List<RawLine>();
        var format = _defaultFormat;
        var inEvents = false;
        foreach (var rawRow in text.Split('\n'))
        {
            var row = rawRow.Trim('\r', ' ', '\t', '﻿');
            if (row.StartsWith('['))
            {
                inEvents = row.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (row.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var names = row[7..].Split(',');
                format = new string[names.Length];
                for (var i = 0; i < names.Length; i++)
                {
                    format[i] = names[i].Trim().ToLowerInvariant();
                }

                continue;
            }

            if (!row.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The text is the last field and may hold commas itself.
            var fields = row[9..].Split(',', format.Length);
            if (fields.Length < format.Length)
            {
                continue;
            }

            string? Field(string name)
            {
                var at = Array.IndexOf(format, name);
                return at < 0 ? null : fields[at].Trim();
            }

            if (!TryParseTime(Field("start"), out var start) || !TryParseTime(Field("end"), out var end))
            {
                continue;
            }

            var clean = TextCleaner.Tidy(TextCleaner.RemoveReadings(Clean(fields[^1], out var positioned, out var sung)));
            if (clean.Length == 0)
            {
                continue;
            }

            // A star before the name marks a style changed in the editor; it is the same style.
            var style = Field("style")?.TrimStart('*');
            var name = Field("name") ?? Field("actor");
            lines.Add(new RawLine(start, end, clean, string.IsNullOrEmpty(style) ? null : style, string.IsNullOrEmpty(name) ? null : name, positioned, sung));
        }

        return lines;
    }

    /// <summary>
    /// Takes the override blocks out of a line's text and reports what they said about it.
    /// </summary>
    internal static string Clean(string text, out bool positioned, out bool sung)
    {
        positioned = false;
        sung = false;
        var drawing = false;
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '{')
            {
                var close = text.IndexOf('}', index + 1);
                if (close < 0)
                {
                    // A brace that never closes is text.
                    if (!drawing)
                    {
                        builder.Append(text, index, text.Length - index);
                    }

                    break;
                }

                ReadTags(text.AsSpan(index + 1, close - index - 1), ref positioned, ref sung, ref drawing);
                index = close + 1;
                continue;
            }

            if (drawing)
            {
                index++;
                continue;
            }

            if (character == '\\' && index + 1 < text.Length)
            {
                var next = text[index + 1];
                if (next is 'N' or 'n')
                {
                    builder.Append('\n');
                    index += 2;
                    continue;
                }

                if (next == 'h')
                {
                    builder.Append(' ');
                    index += 2;
                    continue;
                }
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    private static void ReadTags(ReadOnlySpan<char> block, ref bool positioned, ref bool sung, ref bool drawing)
    {
        var index = block.IndexOf('\\');
        while (index >= 0 && index < block.Length - 1)
        {
            var tag = block[(index + 1)..];
            if (tag.StartsWith("pos(", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("move(", StringComparison.OrdinalIgnoreCase))
            {
                positioned = true;
            }
            else if (tag[0] is 'k' or 'K')
            {
                // \k, \K, \kf and \ko, each followed by a duration.
                var rest = tag[1..];
                if (rest.Length > 0 && rest[0] is 'f' or 'o')
                {
                    rest = rest[1..];
                }

                sung |= rest.Length > 0 && char.IsAsciiDigit(rest[0]);
            }
            else if (tag[0] == 'p' && tag.Length > 1 && char.IsAsciiDigit(tag[1]))
            {
                // \p1 and higher switch drawing mode on, \p0 switches it off. \pos and \pbo are other tags.
                drawing = tag[1] != '0' || (tag.Length > 2 && char.IsAsciiDigit(tag[2]));
            }

            var next = tag.IndexOf('\\');
            index = next < 0 ? -1 : index + 1 + next;
        }
    }

    private static bool TryParseTime(string? value, out long milliseconds)
    {
        milliseconds = 0;
        if (value is null)
        {
            return false;
        }

        // H:MM:SS.cc
        var parts = value.Split(':');
        if (parts.Length != 3
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !decimal.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        milliseconds = (((hours * 60) + minutes) * 60 * 1000) + (long)Math.Round(seconds * 1000m);
        return true;
    }
}
