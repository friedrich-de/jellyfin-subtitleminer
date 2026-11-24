using System;
using System.Text;

namespace Jellyfin.Plugin.SubtitleMiner.Subtitles;

/// <summary>
/// Turns the bytes of a subtitle file into text. Most files are UTF-8, but Japanese subtitle files are
/// often Shift-JIS, and some editors on Windows save UTF-16.
/// </summary>
internal static class TextDecoder
{
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    static TextDecoder()
    {
        // Shift-JIS and the other legacy code pages are not available until this is registered.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Decodes a file.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The text, without a byte order mark.</returns>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith<byte>([0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith<byte>([0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        // UTF-16 first: where it holds only Latin letters, every byte of it is valid UTF-8 as well, zeros
        // included. Text in UTF-8 has no zeros in it.
        if (LooksLikeUtf16(bytes, out var bigEndian))
        {
            return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes);
        }

        if (TryDecode(_strictUtf8, bytes, out var utf8))
        {
            return utf8;
        }

        // Not UTF-8. The Japanese code pages come first, because that is what this plugin is used with:
        // the one that reads the file without an error and finds the most kana wins.
        string? best = null;
        var bestScore = 0;
        foreach (var codePage in new[] { 932, 51932 })
        {
            if (TryDecode(Strict(codePage), bytes, out var text))
            {
                var score = CountKana(text);
                if (score > bestScore)
                {
                    best = text;
                    bestScore = score;
                }
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (var codePage in new[] { 54936, 950 })
        {
            if (TryDecode(Strict(codePage), bytes, out var text) && HasIdeographs(text))
            {
                return text;
            }
        }

        return Encoding.GetEncoding(1252).GetString(bytes);
    }

    private static Encoding Strict(int codePage) =>
        Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static bool TryDecode(Encoding encoding, ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// UTF-16 without a byte order mark shows as a zero in every other byte wherever the text is Latin,
    /// and a subtitle file always has Latin in it: its time codes.
    /// </summary>
    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, out bool bigEndian)
    {
        bigEndian = false;
        var sample = bytes[..Math.Min(bytes.Length & ~1, 4096)];
        if (sample.Length < 16)
        {
            return false;
        }

        var evenZeros = 0;
        var oddZeros = 0;
        for (var i = 0; i < sample.Length; i += 2)
        {
            evenZeros += sample[i] == 0 ? 1 : 0;
            oddZeros += sample[i + 1] == 0 ? 1 : 0;
        }

        var pairs = sample.Length / 2;
        bigEndian = evenZeros > oddZeros;
        return Math.Max(evenZeros, oddZeros) * 5 >= pairs && Math.Min(evenZeros, oddZeros) * 20 <= pairs;
    }

    private static int CountKana(string text)
    {
        var count = 0;
        foreach (var character in text)
        {
            count += character is >= '぀' and <= 'ヿ' ? 1 : 0;
        }

        return count;
    }

    private static bool HasIdeographs(string text)
    {
        foreach (var character in text)
        {
            if (character is >= '一' and <= '鿿')
            {
                return true;
            }
        }

        return false;
    }
}
