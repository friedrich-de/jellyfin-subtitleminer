using System.Linq;
using System.Text;
using Jellyfin.Plugin.SubtitleMiner.Subtitles;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Subtitles;

public class SubtitleReaderTests
{
    private const string Ass = """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 1920

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, Alignment
        Style: Default,Noto Sans JP,60,&H00FFFFFF,2
        Style: Signs,Noto Sans JP,40,&H00FFFFFF,8
        Style: OP-Romaji,Noto Sans JP,40,&H00FFFFFF,8

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:12.50,0:00:14.00,Default,凛,0,0,0,,{\i1}昨日の話、{\i0}まだ覚えてる？
        Comment: 0,0:00:13.00,0:00:14.00,Default,,0,0,0,,a note of the translator
        Dialogue: 0,0:00:05.00,0:00:08.00,Signs,,0,0,0,,{\pos(960,120)\fad(200,200)}東京駅
        Dialogue: 0,0:00:15.00,0:00:17.25,Default,士郎,0,0,0,,もちろん。\N忘れるわけ、ないだろ。
        Dialogue: 1,0:00:20.00,0:00:24.00,OP-Romaji,,0,0,0,,{\k20}ki{\k35}mi {\kf40}no
        Dialogue: 0,0:00:30.00,0:00:32.00,Default,,0,0,0,,{\p1}m 0 0 l 100 0 100 100 0 100{\p0}
        Dialogue: 0,0:00:33.00,0:00:35.00,Default,,0,0,0,,{\an8}上に出る台詞
        Dialogue: 0,0:00:36.00,0:00:38.00,Default,,0,0,0,,{\pos(400,300)}看板
        """;

    private static SubtitleDocument Read(string text, string? format = null) => SubtitleReader.Read(Encoding.UTF8.GetBytes(text), format);

    [Fact]
    public void An_ass_track_is_read_in_the_order_of_time_without_formatting_comments_or_drawings()
    {
        var document = Read(Ass);

        Assert.Equal("ass", document.Format);
        Assert.Equal(
            ["東京駅", "昨日の話、まだ覚えてる？", "もちろん。\n忘れるわけ、ないだろ。", "kimi no", "上に出る台詞", "看板"],
            document.Lines.Select(line => line.Text));
        Assert.Equal(Enumerable.Range(0, 6), document.Lines.Select(line => line.Id));

        var first = document.Lines[1];
        Assert.Equal(12_500, first.StartMs);
        Assert.Equal(14_000, first.EndMs);
        Assert.Equal("Default", first.Style);
        Assert.Equal("凛", first.Name);
    }

    [Fact]
    public void Signs_and_songs_are_told_from_dialogue()
    {
        var kinds = Read(Ass).Lines.ToDictionary(line => line.Text, line => line.Kind);

        Assert.Equal(LineKind.Sign, kinds["東京駅"]);
        Assert.Equal(LineKind.Song, kinds["kimi no"]);
        Assert.Equal(LineKind.Dialogue, kinds["昨日の話、まだ覚えてる？"]);

        // A line at the top of the picture is still dialogue. One pinned to a point is a sign, whatever its style says.
        Assert.Equal(LineKind.Dialogue, kinds["上に出る台詞"]);
        Assert.Equal(LineKind.Sign, kinds["看板"]);
    }

    [Theory]
    [InlineData("Default", "Dialogue")]
    [InlineData("Main - Top", "Dialogue")]
    [InlineData("Flashback", "Dialogue")]
    [InlineData("Sign-Title", "Sign")]
    [InlineData("TS", "Sign")]
    [InlineData("Ep Title", "Sign")]
    [InlineData("OP", "Song")]
    [InlineData("ED2 Kanji", "Song")]
    [InlineData("Insert Song", "Song")]
    [InlineData("Karaoke", "Song")]
    [InlineData("OPJP-01 Special", "Song")]
    [InlineData("EDJP-02", "Song")]
    [InlineData("Hope", "Dialogue")]
    [InlineData("Shed", "Dialogue")]
    [InlineData("Edit", "Dialogue")]
    [InlineData("EDITOR", "Dialogue")]
    public void A_style_name_says_what_its_lines_are(string style, string expected)
    {
        var document = Read($"[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,{style},,0,0,0,,text");

        Assert.Equal(expected, Assert.Single(document.Lines).Kind.ToString());
    }

    [Fact]
    public void Speech_pinned_to_where_speech_stands_is_dialogue_and_a_line_pinned_elsewhere_is_a_sign()
    {
        var document = Read("""
            [Script Info]
            PlayResX: 1920
            PlayResY: 1080

            [Events]
            Dialogue: 0,0:00:01.00,0:00:02.00,JPN,,0,0,0,,ついでに言うなら
            Dialogue: 0,0:00:03.00,0:00:04.00,JPN,,0,0,0,,{\pos(960,1030)}警察組織内で
            Dialogue: 0,0:00:05.00,0:00:06.00,JPN,,0,0,0,,{\an8\pos(950,40)}上で話す
            Dialogue: 0,0:00:07.00,0:00:08.00,JPN,,0,0,0,,{\pos(300,1030)}左下の看板
            Dialogue: 0,0:00:09.00,0:00:10.00,JPN,,0,0,0,,{\pos(960,540)}真ん中の看板
            Dialogue: 0,0:00:11.00,0:00:12.00,JPN,,0,0,0,,{\move(960,1030,960,900)}動く字幕
            Dialogue: 0,0:00:13.00,0:00:14.00,JPN,,0,0,0,,まだ話す
            """);

        var kinds = document.Lines.ToDictionary(line => line.Text, line => line.Kind);
        Assert.Equal(LineKind.Dialogue, kinds["警察組織内で"]);
        Assert.Equal(LineKind.Dialogue, kinds["上で話す"]);
        Assert.Equal(LineKind.Sign, kinds["左下の看板"]);
        Assert.Equal(LineKind.Sign, kinds["真ん中の看板"]);
        Assert.Equal(LineKind.Sign, kinds["動く字幕"]);
        Assert.Equal(LineKind.Dialogue, kinds["まだ話す"]);
    }

    [Theory]
    [InlineData("", "{\\pos(192,270)}", "Dialogue")]
    [InlineData("PlayResX: 1280", "{\\pos(640,1000)}", "Dialogue")]
    [InlineData("PlayResX: 1280", "{\\pos(640,700)}", "Sign")]
    [InlineData("PlayResY: 720", "{\\pos(480,700)}", "Dialogue")]
    [InlineData("PlayResY: 720", "{\\pos(640)}", "Sign")]
    public void A_script_without_its_size_is_read_at_the_size_players_give_it(string info, string tags, string expected)
    {
        var document = Read($"[Script Info]\n{info}\n\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{tags}話す");

        Assert.Equal(expected, Assert.Single(document.Lines).Kind.ToString());
    }

    [Fact]
    public void A_style_with_an_unknown_name_holds_signs_when_most_of_its_lines_are_placed_on_the_picture()
    {
        var document = Read("""
            [Events]
            Dialogue: 0,0:00:01.00,0:00:02.00,Alt,,0,0,0,,{\pos(1,1)}one
            Dialogue: 0,0:00:03.00,0:00:04.00,Alt,,0,0,0,,{\move(1,1,2,2)}two
            Dialogue: 0,0:00:05.00,0:00:06.00,Alt,,0,0,0,,three
            """);

        Assert.All(document.Lines, line => Assert.Equal(LineKind.Sign, line.Kind));
    }

    [Fact]
    public void A_text_repeated_on_several_layers_or_cut_into_frames_is_one_line()
    {
        var document = Read("""
            [Events]
            Dialogue: 0,0:00:01.00,0:00:02.00,Signs,,0,0,0,,{\pos(1,1)}出口
            Dialogue: 1,0:00:01.00,0:00:02.00,Signs,,0,0,0,,{\pos(1,1)\blur2}出口
            Dialogue: 0,0:00:10.00,0:00:10.04,Signs,,0,0,0,,{\pos(10,10)}動く看板
            Dialogue: 0,0:00:10.04,0:00:10.08,Signs,,0,0,0,,{\pos(11,10)}動く看板
            Dialogue: 0,0:00:10.08,0:00:10.12,Signs,,0,0,0,,{\pos(12,10)}動く看板
            Dialogue: 0,0:00:20.00,0:00:21.00,Signs,,0,0,0,,{\pos(10,10)}動く看板
            """);

        Assert.Equal(["出口", "動く看板", "動く看板"], document.Lines.Select(line => line.Text));
        Assert.Equal((10_000, 10_120), (document.Lines[1].StartMs, document.Lines[1].EndMs));
        Assert.Equal(20_000, document.Lines[2].StartMs);
    }

    [Fact]
    public void An_old_ssa_file_with_its_own_field_order_is_read()
    {
        var document = Read("""
            [Events]
            Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: Marked=0,0:00:01.00,0:00:02.50,*Default,NTP,0000,0000,0000,,Hello, there, friend
            """);

        var line = Assert.Single(document.Lines);
        Assert.Equal("Hello, there, friend", line.Text);
        Assert.Equal("Default", line.Style);
        Assert.Equal(2_500, line.EndMs);
    }

    [Fact]
    public void An_srt_file_is_read_without_its_tags_and_position_codes()
    {
        var document = Read("""
            1
            00:00:01,000 --> 00:00:03,500
            {\an8}<i>昨日の話、</i>
            <font color="#ffffff">まだ覚えてる？</font>

            2
            00:01:05,250 --> 00:01:07,000 X1:10 X2:20
            Tom &amp; Jerry

            3
            01:00:00,000 --> 01:00:01,000

            4
            00:00:10.5 --> 00:00:11.75
            dot and short fraction
            """);

        Assert.Equal("srt", document.Format);
        Assert.Equal(["昨日の話、\nまだ覚えてる？", "dot and short fraction", "Tom & Jerry"], document.Lines.Select(line => line.Text));
        Assert.Equal((1_000, 3_500), (document.Lines[0].StartMs, document.Lines[0].EndMs));
        Assert.Equal((10_500, 11_750), (document.Lines[1].StartMs, document.Lines[1].EndMs));
        Assert.All(document.Lines, line => Assert.Equal(LineKind.Dialogue, line.Kind));
    }

    [Fact]
    public void Cues_without_an_empty_row_between_them_stay_apart()
    {
        var document = Read("1\n00:00:01,000 --> 00:00:02,000\none\n2\n00:00:03,000 --> 00:00:04,000\ntwo\n");

        Assert.Equal(["one", "two"], document.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Readings_above_or_behind_a_word_are_left_out_and_other_brackets_stay()
    {
        var document = Read("""
            1
            00:00:01,000 --> 00:00:02,000
            <ruby>約束<rt>やくそく</rt></ruby>の<ruby>時間<rp>(</rp><rt>じかん</rt><rp>)</rp></ruby>

            2
            00:00:03,000 --> 00:00:04,000
            （士郎）電車（でんしゃ）が止まった（ドアの音）

            3
            00:00:05,000 --> 00:00:06,000
            携帯(ケータイ)を見て
            """);

        Assert.Equal(["約束の時間", "（士郎）電車が止まった（ドアの音）", "携帯を見て"], document.Lines.Select(line => line.Text));
    }

    [Fact]
    public void A_caption_between_notes_is_a_song()
    {
        var document = Read("1\n00:00:01,000 --> 00:00:02,000\n♪～\n\n2\n00:00:03,000 --> 00:00:04,000\n♪ 君の名前を ♪\n\n3\n00:00:05,000 --> 00:00:06,000\n音楽♪が好き\n");

        Assert.Equal([LineKind.Song, LineKind.Song, LineKind.Dialogue], document.Lines.Select(line => line.Kind));
    }

    [Fact]
    public void A_webvtt_file_is_read_without_its_header_notes_styles_and_settings()
    {
        var document = Read("""
            WEBVTT - made for a test

            NOTE
            This block is no cue.

            STYLE
            ::cue { color: white }

            intro
            00:01.000 --> 00:02.500 line:10% position:50% align:center
            <v 凛>昨日の<c.yellow>話</c></v>

            00:00:03.000 --> 00:00:04.000
            <00:00:03.200>one <00:00:03.600>two &lt;three&gt;
            """);

        Assert.Equal("vtt", document.Format);
        Assert.Equal(["昨日の話", "one two <three>"], document.Lines.Select(line => line.Text));
        Assert.Equal("凛", document.Lines[0].Name);
        Assert.Equal((1_000, 2_500), (document.Lines[0].StartMs, document.Lines[0].EndMs));
    }

    [Fact]
    public void A_line_that_ends_before_it_starts_is_left_out()
    {
        var document = Read("1\n00:00:05,000 --> 00:00:04,000\nbackwards\n\n2\n00:00:05,000 --> 00:00:05,000\nno time\n\n3\n00:00:06,000 --> 00:00:07,000\nfine\n");

        Assert.Equal(["fine"], document.Lines.Select(line => line.Text));
    }

    [Fact]
    public void The_format_can_be_given_when_the_file_does_not_show_it()
    {
        Assert.Equal("ass", Read("[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,text", "ssa").Format);
        Assert.Empty(Read("nothing of a subtitle in here").Lines);
        Assert.Empty(SubtitleReader.Read([]).Lines);
    }
}
