using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public class PageFilesTests
{
    private static PageFiles Files(params string[] names) => new(new FakePageFileSource(names));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("index.html")]
    public void An_empty_path_names_the_page(string? path)
    {
        var file = Files("index.html", "assets/app-1a2b3c.js").Find(path);

        Assert.NotNull(file);
        Assert.Equal("index.html", file.Name);
        Assert.Equal("text/html; charset=utf-8", file.ContentType);
        Assert.False(file.Immutable);
    }

    [Theory]
    [InlineData("assets/app-1a2b3c.js", "text/javascript; charset=utf-8", true)]
    [InlineData("assets/app-4d5e6f.css", "text/css; charset=utf-8", true)]
    [InlineData("assets/engine-778899.wasm", "application/wasm", true)]
    [InlineData("manifest.webmanifest", "application/manifest+json; charset=utf-8", false)]
    [InlineData("sw.js", "text/javascript; charset=utf-8", false)]
    [InlineData("icons/icon-192.png", "image/png", false)]
    [InlineData("icons/favicon.svg", "image/svg+xml", false)]
    public void A_file_is_served_as_its_kind_and_only_hashed_files_may_be_kept_for_good(string path, string contentType, bool immutable)
    {
        var file = Files("index.html", path).Find(path);

        Assert.NotNull(file);
        Assert.Equal(contentType, file.ContentType);
        Assert.Equal(immutable, file.Immutable);
    }

    [Theory]
    [InlineData("../index.html")]
    [InlineData("assets/../index.html")]
    [InlineData("./index.html")]
    [InlineData("/index.html")]
    [InlineData("assets\\app-1a2b3c.js")]
    [InlineData("%2e%2e/index.html")]
    [InlineData("INDEX.HTML")]
    [InlineData("assets/")]
    [InlineData("assets/app-1a2b3c.js/")]
    [InlineData("Api/Status")]
    [InlineData("meta.json")]
    public void Nothing_but_the_exact_name_of_a_page_file_is_found(string path)
    {
        Assert.Null(Files("index.html", "assets/app-1a2b3c.js").Find(path));
    }

    [Theory]
    [InlineData("assets/app-1a2b3c.js.map")]
    [InlineData("notes.txt")]
    [InlineData("tool.exe")]
    [InlineData("LICENSE")]
    public void A_file_of_a_kind_the_page_does_not_use_is_not_served_even_when_the_build_holds_it(string name)
    {
        Assert.Null(Files("index.html", name).Find(name));
    }

    [Fact]
    public void A_build_without_the_page_says_so()
    {
        Assert.False(Files().HasPage);
        Assert.False(Files("assets/app-1a2b3c.js").HasPage);
        Assert.True(Files("index.html").HasPage);
    }

    [Fact]
    public void A_found_file_opens_with_its_content()
    {
        var files = Files("index.html");

        using var stream = files.Open(files.Find("index.html")!);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Equal("content of index.html", reader.ReadToEnd());
    }

    [Fact]
    public void Embedded_files_are_found_by_their_path_whichever_slash_the_build_used()
    {
        var embedded = new EmbeddedPageFiles(typeof(PageFilesTests).Assembly);

        Assert.Equal(["assets/app-1a2b3c.js", "index.html"], embedded.Names.Order());
        using var script = embedded.Open("assets/app-1a2b3c.js");
        Assert.NotNull(script);
        using var reader = new StreamReader(script);
        Assert.Contains("embedded test script", reader.ReadToEnd(), System.StringComparison.Ordinal);
        Assert.Null(embedded.Open("page/index.html"));
        Assert.Null(embedded.Open("Jellyfin.Plugin.SubtitleMiner.Tests.dll"));
    }

    [Fact]
    public void The_plugin_embeds_nothing_under_the_page_prefix_except_what_the_page_build_left()
    {
        // The plugin's own resources, such as the dashboard page, must not be reachable as page files.
        var embedded = new EmbeddedPageFiles(typeof(Plugin).Assembly);

        Assert.DoesNotContain(embedded.Names, name => name.Contains("configPage", System.StringComparison.OrdinalIgnoreCase));
    }
}
