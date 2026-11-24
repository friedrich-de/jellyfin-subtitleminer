using System.Linq;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

public class JapaneseTextTests
{
    [Theory]
    [InlineData("タベル", "たべる")]
    [InlineData("スーパー", "すうぱあ")]
    [InlineData("コーヒー", "こうひい")]
    [InlineData("ケーキ", "けえき")]
    [InlineData("ヴァイオリン", "ゔぁいおりん")]
    [InlineData("ヵ月ヶ所", "ヵ月ヶ所")]
    [InlineData("ーカ", "ーか")]
    public void Katakana_become_hiragana_and_a_long_mark_the_vowel_before_it(string text, string expected)
    {
        Assert.Equal(expected, JapaneseText.KatakanaToHiragana(text));
    }

    [Fact]
    public void Hiragana_become_katakana()
    {
        Assert.Equal("タベル漢字ー", JapaneseText.HiraganaToKatakana("たべる漢字ー"));
    }

    [Theory]
    [InlineData("すっっごい", false, "すっごい")]
    [InlineData("すっっごい", true, "すごい")]
    [InlineData("すげーーー", false, "すげーーー")]
    [InlineData("すげーーーよ", false, "すげーよ")]
    [InlineData("すげーーーよ", true, "すげよ")]
    [InlineData("っっあっっ", true, "っっあっっ")]
    [InlineData("っー", true, "っー")]
    public void Drawn_out_sounds_are_shortened_but_not_at_either_end(string text, bool full, string expected)
    {
        Assert.Equal(expected, JapaneseText.CollapseEmphatic(text, full));
    }

    [Fact]
    public void Repeat_marks_are_written_out_and_middle_dots_left_out()
    {
        Assert.Equal("時時", JapaneseText.ExpandIterationMarks("時々"));
        Assert.Equal("々", JapaneseText.ExpandIterationMarks("々"));
        Assert.Equal("ジョンスミス", JapaneseText.StripMiddleDots("ジョン・スミス"));
        Assert.Equal("ＣＤ２", JapaneseText.FullWidthAlphanumeric("CD2"));
    }

    [Fact]
    public void The_text_as_written_comes_first_and_each_spelling_counts_its_changes()
    {
        var variants = JapaneseText.Variants("ﾀﾍﾞﾙ");

        Assert.Equal(("ﾀﾍﾞﾙ", 0), variants[0]);
        Assert.Contains(("タベル", 1), variants);
        Assert.Contains(("たべる", 2), variants);
        Assert.Equal(variants.Count, variants.Select(variant => variant.Text).Distinct().Count());
        Assert.Equal(variants.OrderBy(variant => variant.Changes).Select(variant => variant.Changes), variants.Select(variant => variant.Changes));
    }

    [Fact]
    public void A_plain_kanji_text_has_few_spellings()
    {
        Assert.Equal([("日本語", 0)], JapaneseText.Variants("日本語"));
    }
}
