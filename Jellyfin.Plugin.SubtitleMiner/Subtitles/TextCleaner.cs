using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleMiner.Subtitles;

/// <summary>
/// Takes the formatting out of subtitle text, so what is left is what a viewer reads.
/// </summary>
internal static partial class TextCleaner
{
    /// <summary>
    /// Removes the tags SRT and WebVTT files carry: styling, classes, voices, karaoke time stamps and
    /// ruby. The small reading above a word (<c>rt</c>) goes with its tag, the word stays.
    /// </summary>
    /// <param name="text">The text of one cue.</param>
    /// <param name="voice">The speaker a WebVTT voice tag names, if there is one.</param>
    /// <returns>The text without tags, with entities turned into their characters.</returns>
    public static string StripMarkup(string text, out string? voice)
    {
        voice = null;
        var match = VoiceTag().Match(text);
        if (match.Success)
        {
            var name = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            voice = name.Length == 0 ? null : name;
        }

        text = RubyReading().Replace(text, string.Empty);
        text = AssOverride().Replace(text, string.Empty);
        text = Tag().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text).Replace(' ', ' ');
    }

    /// <summary>
    /// Removes a reading written in brackets behind a word in kanji, as Japanese captions made from
    /// ruby text have it: 約束（やくそく） becomes 約束. A bracket that does not follow kanji stays, so the
    /// speaker names and sound descriptions of captions are kept.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without such readings.</returns>
    public static string RemoveReadings(string text) => BracketReading().Replace(text, string.Empty);

    /// <summary>
    /// Trims every row, drops empty rows and folds runs of spaces into one.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The tidy text, rows separated by a line feed.</returns>
    public static string Tidy(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var row in text.Split('\n'))
        {
            var tidy = Spaces().Replace(row, " ").Trim(' ', '\t', '\r', '​', '‎', '‏', '﻿');
            if (tidy.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(tidy);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether a caption marks itself as sung, with a note at its start or its end.
    /// </summary>
    /// <param name="text">The tidy text.</param>
    /// <returns><c>true</c> for a sung line.</returns>
    public static bool IsMarkedAsSung(string text) =>
        text.Length > 1 && (IsNote(text[0]) || IsNote(text[^1]));

    private static bool IsNote(char character) => character is '♪' or '♫' or '♬' or '♩';

    [GeneratedRegex(@"<v(?:\.[^\s>]*)?\s+([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VoiceTag();

    [GeneratedRegex(@"<r[tp]\b[^>]*>.*?</r[tp]\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RubyReading();

    [GeneratedRegex(@"\{\\[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex AssOverride();

    [GeneratedRegex(@"</?[A-Za-z0-9][^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<=[㐀-䶿一-鿿豈-﫿々〆ヶ])[（(][぀-ゟ゠-ヿ]+[）)]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketReading();

    [GeneratedRegex(@"[ \t　]{2,}|\t", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
