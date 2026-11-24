using System.Linq;
using System.Text;
using Jellyfin.Plugin.SubtitleMiner.Subtitles;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Subtitles;

public class TextDecoderTests
{
    private const string Japanese = "1\n00:00:01,000 --> 00:00:02,000\n昨日の話、まだ覚えてる？ カタカナも。\n";

    static TextDecoderTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("shift_jis")]
    [InlineData("euc-jp")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    public void Japanese_text_is_read_in_the_encodings_subtitle_files_come_in(string encoding)
    {
        var bytes = Encoding.GetEncoding(encoding).GetBytes(Japanese);

        Assert.Equal(Japanese, TextDecoder.Decode(bytes));
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    public void A_byte_order_mark_is_obeyed_and_left_out(string encoding)
    {
        var chosen = Encoding.GetEncoding(encoding);
        byte[] bytes = [.. chosen.GetPreamble(), .. chosen.GetBytes(Japanese)];

        Assert.NotEmpty(chosen.GetPreamble());
        Assert.Equal(Japanese, TextDecoder.Decode(bytes));
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    public void Utf16_without_a_byte_order_mark_is_read_when_it_holds_only_latin_letters(string encoding)
    {
        const string English = "1\n00:00:01,000 --> 00:00:02,000\nDo you remember yesterday?\n";
        var bytes = Encoding.GetEncoding(encoding).GetBytes(English);

        Assert.Equal(English, TextDecoder.Decode(bytes));
        Assert.Equal("Do you remember yesterday?", Assert.Single(SubtitleReader.Read(bytes).Lines).Text);
    }

    [Fact]
    public void A_shift_jis_subtitle_file_gives_readable_lines()
    {
        var bytes = Encoding.GetEncoding(932).GetBytes("[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,電車が止まって、間に合わなかったんだ。");

        var line = Assert.Single(SubtitleReader.Read(bytes).Lines);

        Assert.Equal("電車が止まって、間に合わなかったんだ。", line.Text);
    }

    [Fact]
    public void Western_text_that_is_not_utf8_is_read_as_windows_1252()
    {
        var text = "1\n00:00:01,000 --> 00:00:02,000\nCafé déjà vu – naïve\n";

        Assert.Equal(text, TextDecoder.Decode(Encoding.GetEncoding(1252).GetBytes(text)));
    }

    [Fact]
    public void Chinese_text_in_gb18030_is_read()
    {
        var text = "1\n00:00:01,000 --> 00:00:02,000\n你还记得昨天的事吗\n";

        Assert.Equal(text, TextDecoder.Decode(Encoding.GetEncoding(54936).GetBytes(text)));
    }

    [Fact]
    public void Nothing_gives_nothing()
    {
        Assert.Equal(string.Empty, TextDecoder.Decode([]));
        Assert.Equal("ab", TextDecoder.Decode(Enumerable.Repeat((byte)'a', 1).Append((byte)'b').ToArray()));
    }
}
