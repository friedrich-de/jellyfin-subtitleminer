using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public class PluginTests
{
    [Fact]
    public void The_plugin_keeps_the_id_of_the_versions_before_2_so_an_installed_plugin_updates_in_place()
    {
        Assert.Equal(Guid.Parse("8e947a62-b5e1-4d6a-a70c-486996db901f"), Plugin.PluginId);
        Assert.Equal("Subtitle Miner", Plugin.PluginName);
    }

    [Fact]
    public void The_dashboard_page_is_registered_and_embedded_and_leads_to_the_page()
    {
        var page = Assert.Single(Plugin.Pages);

        Assert.Equal(Plugin.ConfigPageName, page.Name);
        Assert.Equal(Plugin.ConfigPageResource, page.EmbeddedResourcePath);
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        Assert.Contains("data-role=\"page\"", html, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl('SubtitleMiner/')", html, StringComparison.Ordinal);

        // The dashboard page holds no field, so nothing typed there can end up in Jellyfin's settings file.
        Assert.DoesNotContain("<input", html, StringComparison.Ordinal);
    }
}
