using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleMiner.Api;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;
using Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;
using Jellyfin.Plugin.SubtitleMiner.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class DictionariesControllerTests : IDisposable
{
    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private static YomitanZip Words(string title = "Words", string meaning = "to eat") => new()
    {
        Title = title,
        Terms = { YomitanZip.Term("食べる", "たべる", "v1", "v1", 1, [meaning]) },
        Styles = ".gloss { color: teal; }",
        Files = { ["img/bowl.svg"] = "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray() },
    };

    private DictionariesController Controller(Stream? body = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider(),
        };
        if (body is not null)
        {
            context.Request.Body = body;
            context.Request.ContentType = "application/zip";
        }

        return new DictionariesController(_test.Build()) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static ProblemDetails Problem(IActionResult? result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private async Task<DictionariesView> Upload(YomitanZip zip, string name = "words.zip")
    {
        _test.SignInAs(administrator: true);
        await using var body = File.OpenRead(zip.Write(_test.Scratch, Guid.NewGuid().ToString("N") + ".zip"));
        var result = await Controller(body).PostDictionary(name, TestContext.Current.CancellationToken);
        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        await _test.Dictionaries.Job;
        return Assert.IsType<DictionariesView>(accepted.Value);
    }

    /// <summary>Sends a dictionary without waiting for the import.</summary>
    private async Task<DictionariesView> Post(YomitanZip zip, string name)
    {
        _test.SignInAs(administrator: true);
        await using var body = File.OpenRead(zip.Write(_test.Scratch, Guid.NewGuid().ToString("N") + ".zip"));
        var result = await Controller(body).PostDictionary(name, TestContext.Current.CancellationToken);
        return Assert.IsType<DictionariesView>(Assert.IsType<AcceptedResult>(result.Result).Value);
    }

    private async Task<DictionariesView> List() => (await Controller().GetDictionaries()).Value!;

    private async Task<JsonElement> Lookup(string text)
    {
        var content = Assert.IsType<ContentResult>(await Controller().GetLookup(text));
        Assert.Equal("application/json", content.ContentType);
        return JsonDocument.Parse(content.Content!).RootElement;
    }

    [Fact]
    public async Task A_user_sees_the_dictionaries_and_only_an_administrator_may_change_them()
    {
        var view = await List();

        Assert.Empty(view.Dictionaries);
        Assert.Equal("idle", view.Import.State);
        Assert.False(view.MayManage);

        _test.SignInAs(administrator: true);
        Assert.True((await List()).MayManage);
    }

    [Fact]
    public async Task Someone_who_is_not_an_administrator_may_not_change_the_dictionaries()
    {
        _test.SignInAs(administrator: false);
        await using var body = new MemoryStream([1, 2, 3]);

        foreach (var result in new IActionResult?[]
        {
            (await Controller(body).PostDictionary("x.zip", TestContext.Current.CancellationToken)).Result,
            (await Controller().PostDownload(new DictionaryDownload { Url = "https://dictionaries.example/x.zip" })).Result,
            (await Controller().PutDictionaries(new DictionaryOrderChange { Order = [] })).Result,
            (await Controller().DeleteDictionary("x")).Result,
            (await Controller().DeleteImport()).Result,
        })
        {
            Assert.Equal(DictionariesController.OnlyAdministrators, Problem(result, StatusCodes.Status403Forbidden).Detail);
        }

        Assert.Empty(_test.DownloadNetwork.Asked);
    }

    [Fact]
    public async Task An_uploaded_dictionary_is_imported_and_its_words_are_looked_up_in_the_popups_shape()
    {
        await Upload(Words());

        var view = await List();
        var words = Assert.Single(view.Dictionaries);
        Assert.Equal(("Words", true, 1L, ".gloss { color: teal; }"), (words.Title, words.Enabled, words.Terms, words.Styles));
        Assert.True(words.Bytes > 0);
        Assert.Equal(("done", "words.zip", "Words"), (view.Import.State, view.Import.Source, view.Import.Title));

        _test.SignInAs(administrator: false);
        var first = (await Lookup("食べたい")).GetProperty("results")[0];
        Assert.Equal("食べる", first.GetProperty("term").GetProperty("expression").GetString());
        Assert.Equal("食べたい", first.GetProperty("matched").GetString());
        Assert.Equal("-たい", first.GetProperty("trace")[0].GetProperty("name").GetString());
        Assert.Equal("Words", first.GetProperty("term").GetProperty("glossaries")[0].GetProperty("dictionary").GetString());

        // The upload waited in the scratch folder and is gone.
        Assert.Empty(Directory.GetFiles(Path.Combine(_test.Scratch, "dictionary-uploads")));
    }

    [Fact]
    public async Task Without_dictionaries_a_lookup_finds_nothing_and_a_long_text_is_cut()
    {
        Assert.Equal(0, (await Lookup("食べる")).GetProperty("results").GetArrayLength());
        Assert.Equal(0, (await Lookup(new string('あ', 10_000))).GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task A_dictionary_is_downloaded_from_its_address()
    {
        var zip = Words("Downloaded").Write(_test.Scratch, "downloaded.zip");
        _test.DownloadNetwork.Files["https://dictionaries.example/words.zip"] = File.ReadAllBytes(zip);
        _test.SignInAs(administrator: true);

        var result = await Controller().PostDownload(new DictionaryDownload { Url = " https://dictionaries.example/words.zip " });
        Assert.IsType<AcceptedResult>(result.Result);
        await _test.Dictionaries.Job;

        Assert.Equal("Downloaded", Assert.Single((await List()).Dictionaries).Title);
        Assert.Equal(["https://dictionaries.example/words.zip"], _test.DownloadNetwork.Asked.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task A_download_that_fails_says_why()
    {
        _test.SignInAs(administrator: true);

        await Controller().PostDownload(new DictionaryDownload { Url = "https://dictionaries.example/gone.zip" });
        await _test.Dictionaries.Job;

        var import = (await List()).Import;
        Assert.Equal(("failed", "The address answered 404 Not Found."), (import.State, import.Error));
        Assert.Empty((await List()).Dictionaries);

        _test.DownloadNetwork.Unreachable.Add("https://dictionaries.example/words.zip");
        await Controller().PostDownload(new DictionaryDownload { Url = "https://dictionaries.example/words.zip" });
        await _test.Dictionaries.Job;
        Assert.Equal(
            "The address could not be reached: Connection refused (dictionaries.example:443).",
            (await List()).Import.Error);
    }

    [Theory]
    [InlineData("ftp://dictionaries.example/words.zip")]
    [InlineData("https://name:password@dictionaries.example/words.zip")]
    [InlineData("words.zip")]
    [InlineData("")]
    public async Task A_dictionary_is_only_downloaded_from_a_web_address_without_a_password(string url)
    {
        _test.SignInAs(administrator: true);

        var result = await Controller().PostDownload(new DictionaryDownload { Url = url });

        Problem(result.Result, StatusCodes.Status400BadRequest);
        Assert.Empty(_test.DownloadNetwork.Asked);
    }

    [Fact]
    public async Task A_zip_that_is_not_a_dictionary_fails_with_the_reason()
    {
        await Upload(new YomitanZip { Index = string.Empty, Terms = { YomitanZip.Term("猫", "ねこ", string.Empty, ["cat"]) } }, "other.zip");

        var import = (await List()).Import;
        Assert.Equal(("failed", "That zip has no index.json at its top, so it is not a Yomitan dictionary."), (import.State, import.Error));
    }

    [Fact]
    public async Task Dictionaries_asked_for_while_one_is_imported_wait_their_turn()
    {
        var release = new TaskCompletionSource<string>();
        Assert.True(_test.Dictionaries.TryStartImport("slow.zip", (_, _) => release.Task));

        var second = await Post(Words("Second"), "second.zip");
        Assert.Equal(("downloading", "slow.zip", 1), (second.Import.State, second.Import.Source, second.Import.Waiting));
        _test.DownloadNetwork.Files["https://dictionaries.example/third.zip"] = File.ReadAllBytes(Words("Third").Write(_test.Scratch, "third.zip"));
        var third = Assert.IsType<DictionariesView>(Assert.IsType<AcceptedResult>(
            (await Controller().PostDownload(new DictionaryDownload { Url = "https://dictionaries.example/third.zip" })).Result).Value);
        Assert.Equal(("downloading", "slow.zip", 2), (third.Import.State, third.Import.Source, third.Import.Waiting));
        Assert.Empty(third.Import.Finished);
        Assert.Empty(third.Dictionaries);

        release.SetResult(Words("First").Write(_test.Scratch, "slow.zip"));
        await _test.Dictionaries.Job;

        var view = await List();
        Assert.Equal(["First", "Second", "Third"], view.Dictionaries.Select(item => item.Title));
        Assert.Equal(("done", "https://dictionaries.example/third.zip", "Third", 0), (view.Import.State, view.Import.Source, view.Import.Title, view.Import.Waiting));
        Assert.Equal(
            [(1L, "slow.zip", "First"), (2L, "second.zip", "Second"), (3L, "https://dictionaries.example/third.zip", "Third")],
            view.Import.Finished.Select(outcome => (outcome.Number, outcome.Source, outcome.Title)));
        Assert.All(view.Import.Finished, outcome => Assert.Null(outcome.Error));
        Assert.Empty(Directory.GetFiles(Path.Combine(_test.Scratch, "dictionary-uploads")));

        // The next one is counted on from there.
        await Upload(Words("Fourth"), "fourth.zip");
        Assert.Equal([1L, 2L, 3L, 4L], (await List()).Import.Finished.Select(outcome => outcome.Number));
    }

    [Fact]
    public async Task One_that_fails_does_not_hold_up_those_that_wait()
    {
        var release = new TaskCompletionSource<string>();
        Assert.True(_test.Dictionaries.TryStartImport("slow.zip", (_, _) => release.Task));
        await Post(new YomitanZip { Index = string.Empty }, "other.zip");
        await Post(Words("Good"), "good.zip");

        release.SetException(new DictionaryFetchException("The address answered 404 Not Found."));
        await _test.Dictionaries.Job;

        var view = await List();
        Assert.Equal(["Good"], view.Dictionaries.Select(item => item.Title));
        Assert.Equal(("done", "good.zip"), (view.Import.State, view.Import.Source));
        Assert.Equal(
            [
                ("slow.zip", null, "The address answered 404 Not Found."),
                ("other.zip", null, "That zip has no index.json at its top, so it is not a Yomitan dictionary."),
                ("good.zip", "Good", null),
            ],
            view.Import.Finished.Select(outcome => (outcome.Source, outcome.Title, outcome.Error)));
    }

    [Fact]
    public async Task Stopping_the_import_lets_go_of_those_that_wait()
    {
        var release = new TaskCompletionSource<string>();
        Assert.True(_test.Dictionaries.TryStartImport("slow.zip", (_, _) => release.Task));
        await Post(Words("Second"), "second.zip");
        await Post(Words("Third"), "third.zip");
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_test.Scratch, "dictionary-uploads")).Length);

        var stopped = (await Controller().DeleteImport()).Value!;
        Assert.Equal(("downloading", 0), (stopped.Import.State, stopped.Import.Waiting));
        Assert.Empty(Directory.GetFiles(Path.Combine(_test.Scratch, "dictionary-uploads")));
        release.SetCanceled(TestContext.Current.CancellationToken);
        await _test.Dictionaries.Job;

        var view = await List();
        Assert.Empty(view.Dictionaries);
        Assert.Equal(("failed", "slow.zip", "The import was stopped."), (view.Import.State, view.Import.Source, view.Import.Error));
        Assert.Equal(
            [
                ("second.zip", DictionaryLibrary.Stopped),
                ("third.zip", DictionaryLibrary.Stopped),
                ("slow.zip", "The import was stopped."),
            ],
            view.Import.Finished.Select(outcome => (outcome.Source, outcome.Error)));

        // Stopping when nothing runs does nothing, and the next import starts as ever.
        await Controller().DeleteImport();
        await Upload(Words("Later"), "later.zip");
        Assert.Equal(["Later"], (await List()).Dictionaries.Select(item => item.Title));
    }

    [Fact]
    public async Task One_asked_for_after_the_stop_is_imported_once_the_stopped_one_has_ended()
    {
        var release = new TaskCompletionSource<string>();
        Assert.True(_test.Dictionaries.TryStartImport("slow.zip", (_, _) => release.Task));
        await Controller().DeleteImport();

        var waiting = await Post(Words("After"), "after.zip");
        Assert.Equal(("downloading", "slow.zip", 1), (waiting.Import.State, waiting.Import.Source, waiting.Import.Waiting));
        release.SetCanceled(TestContext.Current.CancellationToken);
        await _test.Dictionaries.Job;

        Assert.Equal(["After"], (await List()).Dictionaries.Select(item => item.Title));
    }

    [Fact]
    public async Task Only_so_many_dictionaries_wait()
    {
        var release = new TaskCompletionSource<string>();
        Assert.True(_test.Dictionaries.TryStartImport("slow.zip", (_, _) => release.Task));
        for (var i = 0; i < DictionaryLibrary.MaxWaiting; i++)
        {
            Assert.False(_test.Dictionaries.ImportsFull);
            Assert.True(_test.Dictionaries.TryStartImport("waits-" + i, (_, _) => throw new DictionaryFetchException("No such file.")));
        }

        Assert.True(_test.Dictionaries.ImportsFull);
        Assert.False(_test.Dictionaries.TryStartImport("one too many", (_, _) => throw new DictionaryFetchException("No such file.")));
        _test.SignInAs(administrator: true);
        await using var body = new MemoryStream([1, 2, 3]);
        Assert.Equal(DictionariesController.ImportsFull, Problem((await Controller(body).PostDictionary("x.zip", TestContext.Current.CancellationToken)).Result, StatusCodes.Status409Conflict).Detail);
        Assert.Equal(DictionariesController.ImportsFull, Problem((await Controller().PostDownload(new DictionaryDownload { Url = "https://dictionaries.example/words.zip" })).Result, StatusCodes.Status409Conflict).Detail);
        Assert.False(Directory.Exists(Path.Combine(_test.Scratch, "dictionary-uploads")));
        Assert.Equal(DictionaryLibrary.MaxWaiting, (await List()).Import.Waiting);

        release.SetException(new DictionaryFetchException("No such file."));
        await _test.Dictionaries.Job;

        // What the last imports came to is kept, the oldest forgotten first.
        for (var round = 0; round < 2; round++)
        {
            var held = new TaskCompletionSource<string>();
            Assert.True(_test.Dictionaries.TryStartImport("slow.zip", (_, _) => held.Task));
            for (var i = 0; i < DictionaryLibrary.MaxWaiting; i++)
            {
                Assert.True(_test.Dictionaries.TryStartImport("waits-" + i, (_, _) => throw new DictionaryFetchException("No such file.")));
            }

            held.SetException(new DictionaryFetchException("No such file."));
            await _test.Dictionaries.Job;
        }

        var finished = (await List()).Import.Finished;
        var all = 3 * (DictionaryLibrary.MaxWaiting + 1);
        Assert.Equal(DictionaryLibrary.OutcomesKept, finished.Count);
        Assert.Equal(Enumerable.Range(all - DictionaryLibrary.OutcomesKept + 1, DictionaryLibrary.OutcomesKept).Select(number => (long)number), finished.Select(outcome => outcome.Number));
    }

    [Fact]
    public async Task A_server_that_stops_lets_go_of_what_waits()
    {
        var folder = Path.Combine(_test.Scratch, "stopping");
        var library = new DictionaryLibrary(folder, NullLogger.Instance);
        var release = new TaskCompletionSource<string>();
        Assert.True(library.TryStartImport("slow.zip", (_, token) => release.Task.WaitAsync(token)));
        var uploaded = Words().Write(_test.Scratch, "waits.zip");
        Assert.True(library.TryStartImport("waits.zip", (_, _) => Task.FromResult(uploaded), uploaded));

        library.Dispose();
        await library.Job;

        Assert.False(File.Exists(uploaded));
        Assert.Empty(library.Installed);
        Assert.Equal(
            [("waits.zip", DictionaryLibrary.Stopped), ("slow.zip", "The import was stopped.")],
            library.Import.Finished.Select(outcome => (outcome.Source, outcome.Error)));
        Assert.Equal(("failed", 0), (library.Import.State, library.Import.Waiting));
        Assert.False(library.TryStartImport("late.zip", (_, _) => Task.FromResult(uploaded)));
    }

    [Fact]
    public async Task Dictionaries_are_put_in_order_turned_off_and_removed()
    {
        await Upload(Words("First", "first meaning"));
        await Upload(Words("Second", "second meaning"));
        var ids = (await List()).Dictionaries.Select(item => item.Id).ToList();

        var view = (await Controller().PutDictionaries(new DictionaryOrderChange { Order = [ids[1], ids[0]], Disabled = [ids[0]] })).Value!;
        Assert.Equal([("Second", true), ("First", false)], view.Dictionaries.Select(item => (item.Title, item.Enabled)));
        var glossaries = (await Lookup("食べる")).GetProperty("results")[0].GetProperty("term").GetProperty("glossaries");
        Assert.Equal("Second", Assert.Single(glossaries.EnumerateArray()).GetProperty("dictionary").GetString());

        Problem((await Controller().PutDictionaries(new DictionaryOrderChange { Order = [ids[0]] })).Result, StatusCodes.Status400BadRequest);
        Problem((await Controller().PutDictionaries(new DictionaryOrderChange { Order = [ids[0], ids[0]] })).Result, StatusCodes.Status400BadRequest);

        view = (await Controller().DeleteDictionary(ids[1])).Value!;
        Assert.Equal(["First"], view.Dictionaries.Select(item => item.Title));
        Assert.False(File.Exists(Path.Combine(_test.Scratch, "dictionaries", ids[1] + ".db")));
        Problem((await Controller().DeleteDictionary(ids[1])).Result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task A_dictionary_imported_again_takes_the_place_of_the_old_one_on_or_off_as_it_was()
    {
        await Upload(Words("Other"));
        await Upload(Words("Words", "old meaning"));
        var ids = (await List()).Dictionaries.Select(item => item.Id).ToList();
        await Controller().PutDictionaries(new DictionaryOrderChange { Order = [ids[1], ids[0]], Disabled = [ids[1]] });

        await Upload(Words("Words", "new meaning"));

        var view = await List();
        Assert.Equal([("Words", false), ("Other", true)], view.Dictionaries.Select(item => (item.Title, item.Enabled)));
        Assert.NotEqual(ids[1], view.Dictionaries[0].Id);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_test.Scratch, "dictionaries"), "*.db").Length);
    }

    [Fact]
    public async Task A_dictionary_removed_or_replaced_during_a_lookup_keeps_its_file_until_the_lookup_ends()
    {
        await Upload(Words("Words", "old meaning"));
        await Upload(Words("Other"));
        var ids = (await List()).Dictionaries.Select(item => item.Id).ToList();
        var folder = Path.Combine(_test.Scratch, "dictionaries");
        string FileOf(string id) => Path.Combine(folder, id + ".db");

        var first = _test.Dictionaries.Read(_ => true);
        var second = _test.Dictionaries.Read(item => item.Reader.Info.Id == ids[1]);
        await Upload(Words("Words", "new meaning"));
        await Controller().DeleteDictionary(ids[1]);

        Assert.True(File.Exists(FileOf(ids[0])));
        Assert.True(File.Exists(FileOf(ids[1])));
        var titles = DictionaryLookup.Find("食べる", first.Readers).Results
            .SelectMany(result => result.Term.Glossaries)
            .Select(glossary => glossary.Dictionary);
        Assert.Equal(["Words", "Other"], titles);
        Assert.Equal(["Words"], (await List()).Dictionaries.Select(item => item.Title));

        first.Dispose();
        first.Dispose();
        Assert.False(File.Exists(FileOf(ids[0])));
        Assert.True(File.Exists(FileOf(ids[1])));

        second.Dispose();
        Assert.False(File.Exists(FileOf(ids[1])));
        Assert.Single(Directory.GetFiles(folder, "*.db"));
    }

    [Fact]
    public async Task The_dictionaries_their_order_and_what_is_off_are_there_after_a_restart()
    {
        await Upload(Words("First"));
        await Upload(Words("Second"));
        var ids = (await List()).Dictionaries.Select(item => item.Id).ToList();
        await Controller().PutDictionaries(new DictionaryOrderChange { Order = [ids[1], ids[0]], Disabled = [ids[1]] });
        var folder = Path.Combine(_test.Scratch, "dictionaries");
        File.WriteAllText(Path.Combine(folder, "cut-short.importing"), "an import the stop cut short");

        using var restarted = new DictionaryLibrary(folder, NullLogger.Instance);

        Assert.Equal([("Second", false), ("First", true)], restarted.Installed.Select(item => (item.Reader.Info.Title, item.Enabled)));
        Assert.False(File.Exists(Path.Combine(folder, "cut-short.importing")));
    }

    [Fact]
    public async Task A_removed_dictionary_whose_file_stayed_is_not_back_after_a_restart_unless_the_list_is_lost()
    {
        await Upload(Words("Kept"));
        var folder = Path.Combine(_test.Scratch, "dictionaries");
        var kept = Directory.GetFiles(folder, "*.db").Single();
        var stayed = Path.Combine(folder, "0123456789abcdef0123456789abcdef.db");
        File.Copy(kept, stayed);

        using (var restarted = new DictionaryLibrary(folder, NullLogger.Instance))
        {
            Assert.Equal(["Kept"], restarted.Installed.Select(item => item.Reader.Info.Title));
        }

        Assert.False(File.Exists(stayed));

        // A list that cannot be read loses nothing: every file is listed again, on.
        File.WriteAllText(Path.Combine(folder, DictionaryLibrary.ListFileName), "{ not json");
        using var relisted = new DictionaryLibrary(folder, NullLogger.Instance);
        Assert.Equal([("Kept", true)], relisted.Installed.Select(item => (item.Reader.Info.Title, item.Enabled)));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task A_picture_of_a_dictionary_is_served_so_that_it_cannot_run_anything()
    {
        await Upload(Words());
        var id = Assert.Single((await List()).Dictionaries).Id;
        var controller = Controller();

        var file = Assert.IsType<FileContentResult>(await controller.GetMedia(id, "img/bowl.svg"));

        Assert.Equal("image/svg+xml", file.ContentType);
        Assert.Contains("sandbox", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
        Problem(await Controller().GetMedia(id, "img/none.png"), StatusCodes.Status404NotFound);
        Problem(await Controller().GetMedia("other", "img/bowl.svg"), StatusCodes.Status404NotFound);
        Problem(await Controller().GetMedia(id, "index.json"), StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Without_a_user_nothing_is_answered()
    {
        _test.Callers.Caller = null;

        Problem((await Controller().GetDictionaries()).Result, StatusCodes.Status403Forbidden);
        Problem(await Controller().GetLookup("食べる"), StatusCodes.Status403Forbidden);
        Problem(await Controller().GetMedia("x", "y.png"), StatusCodes.Status403Forbidden);
    }
}
