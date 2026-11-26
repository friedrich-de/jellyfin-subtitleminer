using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

/// <summary>
/// Dictionaries as packers in the wild write them, which Yomitan takes and so must the plugin: file names in
/// Shift-JIS with the name in UTF-8 beside them, and checksums that do not fit.
/// </summary>
public sealed class ZipsInTheWildTests : IDisposable
{
    private static readonly byte[] Index = "{\"title\":\"In the wild\",\"revision\":\"1\",\"format\":3}"u8.ToArray();

    private static readonly byte[] Bank = Encoding.UTF8.GetBytes(
        "[[\"漢字\",\"かんじ\",\"\",\"\",0,[{\"type\":\"structured-content\",\"content\":[{\"tag\":\"img\",\"path\":\"漢-default.svg\"},\"Chinese characters\"]}],1,\"\"]]");

    private static readonly byte[] Pitches = Encoding.UTF8.GetBytes("[[\"漢字\",\"pitch\",{\"reading\":\"かんじ\",\"pitches\":[{\"position\":0}]}]]");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "subtitle-miner-zips-" + Guid.NewGuid().ToString("N"));

    public ZipsInTheWildTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private static byte[] Picture(string mark) => Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\"><title>{mark}</title></svg>");

    private (DictionaryInfo Info, DictionaryReader Reader) Import(RawZip zip)
    {
        var path = Path.Combine(_folder, "wild.db");
        var info = YomitanArchive.Import(zip.Write(_folder), path, "wild", null, TestContext.Current.CancellationToken);
        return (info, DictionaryReader.TryOpen(path)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pictures_named_in_Shift_JIS_are_kept_under_the_names_their_definitions_ask_for(bool large)
    {
        // As a packer on a Japanese Windows writes Daijirin's label pictures. .NET alone reads the first two as
        // the same two unreadable characters, so that one would even take the other's place.
        var zip = new RawZip { Large = large }
            .Add("index.json", Index)
            .Add("term_bank_1.json", Bank)
            .AddShiftJis(RawZip.Kan, "漢-default.svg", Picture("漢"))
            .AddShiftJis(RawZip.Bun, "文-default.svg", Picture("文"))
            .AddShiftJis(RawZip.Hyouki, "表記-default.svg", Picture("表記"))
            .AddShiftJis([.. "img/"u8, 0x94, 0x5C, .. ".svg"u8], "img/能.svg", Picture("能"))
            .Add("img/可能.svg", Picture("可能"))
            .Add("plain.svg", Picture("plain"));

        var (info, reader) = Import(zip);

        Assert.Equal((1L, 6L), (info.Terms, info.Media));
        Assert.Equal(Picture("漢"), reader.Media("漢-default.svg"));
        Assert.Equal(Picture("文"), reader.Media("文-default.svg"));
        Assert.Equal(Picture("表記"), reader.Media("表記-default.svg"));
        Assert.Equal(Picture("能"), reader.Media("img/能.svg"));
        Assert.Equal(Picture("可能"), reader.Media("img/可能.svg"));
        Assert.Equal(Picture("plain"), reader.Media("plain.svg"));
        Assert.Null(reader.Media("��-default.svg"));
    }

    [Fact]
    public void A_name_beside_a_file_is_only_taken_when_it_is_for_that_file_and_the_file_needs_one()
    {
        var renamed = "renamed.svg"u8.ToArray();
        var notUtf8 = RawZip.UnicodePath(RawZip.Kan, "ab.svg");
        notUtf8[^6] = 0xFF;
        var cutShort = RawZip.UnicodePath(RawZip.Kan, "cut-short.svg")[..^3];
        var zip = new RawZip()
            .Add("index.json", Index)
            .Add("term_bank_1.json", Bank)

            // A tool renamed the file and left the field of its old name: its checksum is of another name.
            .Add(renamed, nameInUtf8: false, Picture("renamed"), RawZip.UnicodePath(RawZip.Kan, "漢-default.svg"))

            // The zip says the name itself is UTF-8, so the name stands.
            .Add("文-default.svg", Picture("文"), RawZip.UnicodePath(Encoding.UTF8.GetBytes("文-default.svg"), "other.svg"))

            // A field of a version nobody wrote yet, one that is not UTF-8 and one that ends before its length.
            .Add("v2.svg"u8.ToArray(), nameInUtf8: false, Picture("v2"), RawZip.UnicodePath("v2.svg"u8.ToArray(), "version-two.svg", version: 2))
            .Add(RawZip.Kan, nameInUtf8: false, Picture("not UTF-8"), notUtf8)
            .Add("short.svg"u8.ToArray(), nameInUtf8: false, Picture("short"), cutShort)

            // Other fields before the name's, as packers write a file's times.
            .Add(RawZip.Hyouki, nameInUtf8: false, Picture("表記"), [0x55, 0x54, 5, 0, 1, 0, 0, 0, 0, .. RawZip.UnicodePath(RawZip.Hyouki, "表記-default.svg")]);

        var (info, reader) = Import(zip);

        Assert.Equal(6, info.Media);
        Assert.Equal(Picture("renamed"), reader.Media("renamed.svg"));
        Assert.Null(reader.Media("漢-default.svg"));
        Assert.Equal(Picture("文"), reader.Media("文-default.svg"));
        Assert.Null(reader.Media("other.svg"));
        Assert.Equal(Picture("v2"), reader.Media("v2.svg"));
        Assert.Equal(Picture("not UTF-8"), reader.Media("��-default.svg"));
        Assert.Equal(Picture("short"), reader.Media("short.svg"));
        Assert.Equal(Picture("表記"), reader.Media("表記-default.svg"));
    }

    [Fact]
    public void The_names_are_those_of_NETs_reader_when_the_zip_has_no_others()
    {
        var path = new YomitanZip { Terms = { YomitanZip.Term("猫", "ねこ", string.Empty, ["cat"]) }, Files = { ["絵/猫.png"] = [1, 2, 3] } }.Write(_folder);
        using var zip = ZipFile.OpenRead(path);

        Assert.Equal(zip.Entries.Select(entry => entry.FullName), ZipNames.Read(path, zip));
        Assert.Contains("絵/猫.png", ZipNames.Read(path, zip));
    }

    [Fact]
    public void The_checksum_of_a_name_is_the_one_zips_use()
    {
        Assert.Equal(0xCBF43926u, ZipNames.Checksum("123456789"u8));
        Assert.Equal(0u, ZipNames.Checksum([]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_checksum_that_does_not_fit_refuses_nothing_when_the_file_unpacks_whole(bool stored)
    {
        // Some pitch accent dictionaries state a wrong checksum for every bank beside banks that are whole.
        // Yomitan does not look at it, and neither does .NET 10; this says so if a later .NET starts to.
        var zip = new RawZip()
            .Add("index.json", Index)
            .Add("term_bank_1.json", Bank, wrongChecksum: true, stored: stored)
            .Add("term_meta_bank_1.json", Pitches, wrongChecksum: true, stored: stored)
            .AddShiftJis(RawZip.Kan, "漢-default.svg", Picture("漢"));

        var (info, reader) = Import(zip);

        Assert.Equal((1L, 1L, 1L, 0L), (info.Terms, info.Pitches, info.Media, info.Skipped));
        Assert.Single(reader.FindMetas(["漢字"]));
        Assert.Equal(Picture("漢"), reader.Media("漢-default.svg"));
    }

    [Fact]
    public void A_picture_that_cannot_be_unpacked_is_named_as_its_definitions_name_it()
    {
        var path = new RawZip()
            .Add("index.json", Index)
            .Add("term_bank_1.json", Bank)
            .AddShiftJis(RawZip.Kan, "漢-default.svg", Picture("漢"))
            .Write(_folder);
        var bytes = File.ReadAllBytes(path);

        // The picture's own bytes, after its local header, name and field, become no stream a zip unpacks.
        var at = bytes.AsSpan().IndexOf(RawZip.Kan) + RawZip.Kan.Length + RawZip.UnicodePath(RawZip.Kan, "漢-default.svg").Length;
        bytes.AsSpan(at, 8).Fill(0xFF);
        File.WriteAllBytes(path, bytes);
        var file = Path.Combine(_folder, "damaged.db");

        var error = Assert.Throws<InvalidDataException>(() => YomitanArchive.Import(path, file, "damaged", null, TestContext.Current.CancellationToken));

        Assert.Equal("漢-default.svg in that dictionary cannot be unpacked: the zip is damaged.", error.Message);
        Assert.False(File.Exists(file));
    }
}
