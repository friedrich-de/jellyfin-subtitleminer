using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// The other spellings a Japanese text is looked up as, as Yomitan and Hoshidicts make them: half-width and
/// compatibility characters made plain, katakana as hiragana and back, drawn-out ー and っ shortened, 々 written out
/// and ・ left out. Each spelling counts the changes that made it, so a word found as written comes first.
/// </summary>
internal static class JapaneseText
{
    /// <summary>A text gets no more spellings than this.</summary>
    internal const int MaxVariants = 4096;

    private const char SmallTsuHiragana = 'っ';
    private const char SmallTsuKatakana = 'ッ';
    private const char ProlongedSoundMark = 'ー';

    // The processors, in Hoshidicts' order; each gives one or more spellings beside the text itself.
    private static readonly Func<string, string>[][] Processors =
    [
        [Nfkc],
        [KatakanaToHiragana, HiraganaToKatakana],
        [text => CollapseEmphatic(text, false), text => CollapseEmphatic(text, true)],
        [FullWidthAlphanumeric],
        [ExpandIterationMarks],
        [StripMiddleDots],
    ];

    /// <summary>The vowel each hiragana ends in, for a ー after it.</summary>
    private static readonly Dictionary<char, char> Vowels = new[]
    {
        ("ぁあかがさざただなはばぱまゃやらゎわ", 'あ'),
        ("ぃいきぎしじちぢにひびぴみり", 'い'),
        ("ぅうくぐすずつづぬふぶぷむゅゆるゔ", 'う'),
        ("ぇえけげせぜてでねへべぺめれ", 'え'),
        ("ぉおこごそぞとどのほぼぽもょよろを", 'う'),
    }.SelectMany(row => row.Item1.Select(kana => (kana, row.Item2))).ToDictionary(pair => pair.kana, pair => pair.Item2);

    /// <summary>
    /// The spellings of a text: the text itself with 0 changes, then the others with the number of processors that
    /// changed them. A spelling two processors reach keeps the smaller number.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The spellings, each once, those with fewer changes first.</returns>
    public static IReadOnlyList<(string Text, int Changes)> Variants(string text)
    {
        var variants = new Dictionary<string, int>(StringComparer.Ordinal) { [text] = 0 };
        foreach (var processor in Processors)
        {
            foreach (var (variant, changes) in variants.ToArray())
            {
                foreach (var spell in processor)
                {
                    if (variants.Count >= MaxVariants)
                    {
                        break;
                    }

                    var changed = spell(variant);
                    if (changed != variant && (!variants.TryGetValue(changed, out var known) || known > changes + 1))
                    {
                        variants[changed] = changes + 1;
                    }
                }
            }
        }

        return variants
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value))
            .ToList();
    }

    /// <summary>NFKC: half-width katakana and full-width letters made plain, ㍿ written out.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text normalized.</returns>
    public static string Nfkc(string text) => text.IsNormalized(NormalizationForm.FormKC) ? text : text.Normalize(NormalizationForm.FormKC);

    /// <summary>
    /// Katakana as hiragana, as Yomitan converts them: ヵ and ヶ stay, and a ー after a kana becomes that kana's vowel,
    /// with う for an o (スーパー → すうぱあ, コーヒー → こうひい).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text in hiragana.</returns>
    public static string KatakanaToHiragana(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == ProlongedSoundMark && result.Length > 0 && Vowels.TryGetValue(result[^1], out var vowel))
            {
                result.Append(vowel);
            }
            else if (c >= 'ァ' && c <= 'ヴ')
            {
                result.Append((char)(c - 0x60));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>Hiragana as katakana.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text in katakana.</returns>
    public static string HiraganaToKatakana(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            result.Append(c >= 'ぁ' && c <= 'ゖ' ? (char)(c + 0x60) : c);
        }

        return result.ToString();
    }

    /// <summary>
    /// Drawn-out っ, ッ and ー shortened, as Yomitan's collapseEmphaticSequences: a run of one of them becomes one
    /// (すっっごい → すっごい), or with <paramref name="full"/> goes (すっごい → すごい). At the start and the end of
    /// the text they stay.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="full">Whether to leave them out rather than keep one.</param>
    /// <returns>The text shortened.</returns>
    public static string CollapseEmphatic(string text, bool full)
    {
        static bool Emphatic(char c) => c is SmallTsuHiragana or SmallTsuKatakana or ProlongedSoundMark;

        var left = 0;
        while (left < text.Length && Emphatic(text[left]))
        {
            left++;
        }

        var right = text.Length - 1;
        while (right >= 0 && Emphatic(text[right]))
        {
            right--;
        }

        if (left > right)
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        result.Append(text, 0, left);
        var last = '\0';
        for (var i = left; i <= right; i++)
        {
            var c = text[i];
            if (Emphatic(c))
            {
                if (c != last)
                {
                    last = c;
                    if (!full)
                    {
                        result.Append(c);
                    }
                }
            }
            else
            {
                last = '\0';
                result.Append(c);
            }
        }

        result.Append(text, right + 1, text.Length - right - 1);
        return result.ToString();
    }

    /// <summary>Latin letters and digits in full width, as some dictionaries write them (ＣＤ).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text in full width.</returns>
    public static string FullWidthAlphanumeric(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            result.Append(c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') ? (char)(c + 0xFEE0) : c);
        }

        return result.ToString();
    }

    /// <summary>々 written out as the character before it (時々 → 時時).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text written out.</returns>
    public static string ExpandIterationMarks(string text)
    {
        if (!text.Contains('々', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            result.Append(c == '々' && result.Length > 0 && !char.IsSurrogate(result[^1]) ? result[^1] : c);
        }

        return result.ToString();
    }

    /// <summary>The middle dots between the words of a name left out (ジョン・スミス → ジョンスミス).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without them.</returns>
    public static string StripMiddleDots(string text) => text.Replace("・", string.Empty, StringComparison.Ordinal).Replace("･", string.Empty, StringComparison.Ordinal);
}
