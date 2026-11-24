using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly FakeCallers _callers;

    public ControllerTests()
    {
        _callers = _test.Callers;
        _callers.Caller = null;
    }

    public void Dispose() => _test.Dispose();

    private PluginServices Services(params string[] pageFiles) => _test.Build(pageFiles);

    private static ControllerContext Context(string path, string query = "") => new()
    {
        HttpContext = new DefaultHttpContext
        {
            Request = { Path = path, QueryString = new QueryString(query) },
            RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
        }
    };

    private PageController Page(string path, string query = "", params string[] pageFiles) =>
        new(Services(pageFiles.Length == 0 ? ["index.html", "assets/app-1a2b3c.js", "sw.js"] : pageFiles)) { ControllerContext = Context(path, query) };

    private static string Read(ActionResult result, string expectedType)
    {
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal(expectedType, file.ContentType);
        using var reader = new StreamReader(file.FileStream);
        return reader.ReadToEnd();
    }

    private static ProblemDetails Problem(ActionResult result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public void The_page_is_served_with_a_policy_that_allows_only_this_server()
    {
        var controller = Page("/SubtitleMiner/");

        Assert.Equal("content of index.html", Read(controller.GetPage(), "text/html; charset=utf-8"));

        var headers = controller.Response.Headers;
        Assert.Equal(PageController.ContentSecurityPolicy, headers.ContentSecurityPolicy);
        Assert.Equal("no-cache", headers.CacheControl);
        Assert.Equal("nosniff", headers.XContentTypeOptions);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
    }

    [Fact]
    public void The_policy_names_no_other_site_and_allows_no_inline_or_generated_code()
    {
        var policy = PageController.ContentSecurityPolicy;

        Assert.StartsWith("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self' http://127.0.0.1:* http://localhost:* https://127.0.0.1:* https://localhost:*;", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", policy, StringComparison.Ordinal);

        // Chromium refuses an IPv6 address as a source and says so in the console of the page.
        Assert.DoesNotContain("[", policy, StringComparison.Ordinal);

        // Besides the user's own device, for an AnkiConnect there, no address and no wildcard.
        var rest = policy.Replace(PageController.DeviceAddresses, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("http", rest, StringComparison.Ordinal);
        Assert.DoesNotContain("*", rest, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/SubtitleMiner", "", "SubtitleMiner/")]
    [InlineData("/SubtitleMiner", "?tab=1", "SubtitleMiner/?tab=1")]
    public void The_address_without_a_slash_leads_to_the_one_with_it(string path, string query, string expected)
    {
        var redirect = Assert.IsType<RedirectResult>(Page(path, query).GetPage());

        Assert.Equal(expected, redirect.Url);
        Assert.False(redirect.Permanent);
    }

    [Fact]
    public void A_build_without_the_page_answers_404_and_says_why()
    {
        var problem = Problem(Page("/SubtitleMiner/", pageFiles: ["assets/app-1a2b3c.js"]).GetPage(), StatusCodes.Status404NotFound);

        Assert.Contains("dev/package.sh", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_a_hash_in_its_name_may_be_kept_for_good_and_the_others_are_asked_for_again()
    {
        var hashed = Page("/SubtitleMiner/assets/app-1a2b3c.js");
        Assert.Equal("content of assets/app-1a2b3c.js", Read(hashed.GetFile("assets/app-1a2b3c.js"), "text/javascript; charset=utf-8"));
        Assert.Equal("public, max-age=31536000, immutable", hashed.Response.Headers.CacheControl);
        Assert.Equal(PageController.ContentSecurityPolicy, hashed.Response.Headers.ContentSecurityPolicy);

        var worker = Page("/SubtitleMiner/sw.js");
        Read(worker.GetFile("sw.js"), "text/javascript; charset=utf-8");
        Assert.Equal("no-cache", worker.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("manifest.webmanifest", "application/manifest+json; charset=utf-8")]
    [InlineData("icons/icon-512.png", "image/png")]
    [InlineData("icons/apple-touch-icon.png", "image/png")]
    public void The_app_manifest_and_icons_are_served_as_what_they_are_and_asked_for_again(string path, string type)
    {
        var page = Page("/SubtitleMiner/" + path, string.Empty, "index.html", "manifest.webmanifest", "icons/icon-512.png", "icons/apple-touch-icon.png");
        Assert.Equal("content of " + path, Read(page.GetFile(path), type));
        Assert.Equal("no-cache", page.Response.Headers.CacheControl);
        Assert.Equal("nosniff", page.Response.Headers.XContentTypeOptions);
    }

    [Theory]
    [InlineData("nothing.js")]
    [InlineData("../index.html")]
    [InlineData("Api/Unknown")]
    public void Anything_that_is_not_a_page_file_answers_404(string path)
    {
        Assert.IsType<NotFoundResult>(Page("/SubtitleMiner/" + path).GetFile(path));
    }

    [Theory]
    [InlineData("/Plugins/JellyfinMiner", null, "../SubtitleMiner/")]
    [InlineData("/Plugins/JellyfinMiner/", null, "../../SubtitleMiner/")]
    [InlineData("/Plugins/JellyfinMiner/index.html", "index.html", "../../SubtitleMiner/")]
    [InlineData("/Plugins/JellyfinMiner/assets/index-abc.js", "assets/index-abc.js", "../../../SubtitleMiner/")]
    public void The_address_of_the_versions_before_2_leads_to_the_page(string requested, string? path, string expected)
    {
        var redirect = Assert.IsType<RedirectResult>(Page(requested).GetOldAddress(path));

        Assert.Equal(expected, redirect.Url);
        Assert.False(redirect.Permanent);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("api/v1/sessions")]
    [InlineData("API/v1/sessions/abc/state")]
    public void The_endpoints_of_the_versions_before_2_are_gone(string path)
    {
        Assert.IsType<NotFoundResult>(Page("/Plugins/JellyfinMiner/" + path).GetOldAddress(path));
    }

    [Fact]
    public void The_page_files_need_no_login_and_every_endpoint_does()
    {
        Assert.NotNull(typeof(PageController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.DoesNotContain(typeof(PageController).GetMethods(), method => method.GetCustomAttribute<AuthorizeAttribute>() is not null);

        Assert.NotNull(typeof(SubtitleMinerController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(typeof(SubtitleMinerController).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.DoesNotContain(
            typeof(SubtitleMinerController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
    }

    [Fact]
    public async Task The_status_names_the_user_the_plugin_and_the_server()
    {
        var userId = Guid.NewGuid();
        _callers.Caller = new Caller(userId, "mika", IsAdministrator: true);
        var controller = new SubtitleMinerController(Services()) { ControllerContext = Context("/SubtitleMiner/Api/Status") };

        var status = Assert.IsType<StatusView>(Assert.IsType<OkObjectResult>(await controller.GetStatus()).Value);

        Assert.Equal(userId, status.UserId);
        Assert.Equal("mika", status.UserName);
        Assert.True(status.IsAdministrator);
        Assert.Equal("Test server", status.ServerName);
        Assert.Equal("12.4.0", status.ServerVersion);
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version!.ToString(), status.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", status.Version);
    }

    [Fact]
    public async Task A_request_without_a_user_such_as_one_with_an_api_key_gets_no_status()
    {
        _callers.Caller = null;
        var controller = new SubtitleMinerController(Services()) { ControllerContext = Context("/SubtitleMiner/Api/Status") };

        var problem = Problem(await controller.GetStatus(), StatusCodes.Status403Forbidden);

        Assert.Contains("Sign in", problem.Detail, StringComparison.Ordinal);
    }
}
