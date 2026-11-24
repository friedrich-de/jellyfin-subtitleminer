using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.SubtitleMiner.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class UserSettingsStoreTests : IDisposable
{
    private static readonly Guid _user = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "subtitle-miner-settings-" + Guid.NewGuid().ToString("N"));
    private readonly UserSettingsStore _store;

    public UserSettingsStoreTests()
    {
        _store = new UserSettingsStore(_folder, new KeyProtector(RandomNumberGenerator.GetBytes(32)), NullLogger<UserSettingsStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string FileOf(Guid user) => Path.Combine(_folder, user.ToString("N") + ".json");

    [Fact]
    public void A_user_who_saved_nothing_gets_the_defaults_and_no_file()
    {
        var settings = _store.Load(_user);

        Assert.Equal(AnkiWhere.Off, settings.Where);
        Assert.Equal("http://127.0.0.1:8765", settings.DeviceAddress);
        Assert.Equal(string.Empty, settings.ServerAddress);
        Assert.Null(settings.Page);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Settings_come_back_as_saved_from_a_file_only_Jellyfins_user_may_read()
    {
        var saved = new UserSettings
        {
            Where = AnkiWhere.Server,
            ServerAddress = "https://anki.example.org/",
            ServerKey = _store.Keys.Protect("server-key"),
            Page = JsonDocument.Parse("""{"deck":"Mining","fields":{"Sentence":"sentence"}}""").RootElement,
        };

        _store.Save(_user, saved);
        var loaded = _store.Load(_user);

        Assert.Equal(AnkiWhere.Server, loaded.Where);
        Assert.Equal("https://anki.example.org/", loaded.ServerAddress);
        Assert.Equal("server-key", _store.Keys.Reveal(loaded.ServerKey));
        Assert.Equal("Mining", loaded.Page!.Value.GetProperty("deck").GetString());
        Assert.DoesNotContain("server-key", File.ReadAllText(FileOf(_user)), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FileOf(_user)));
        }

        // Each user has a file of their own.
        Assert.Equal(AnkiWhere.Off, _store.Load(Guid.NewGuid()).Where);
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults_and_the_next_save_replaces_it()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FileOf(_user), "{ not json");

        Assert.Equal(AnkiWhere.Off, _store.Load(_user).Where);

        _store.Save(_user, new UserSettings { Where = AnkiWhere.Device });
        Assert.Equal(AnkiWhere.Device, _store.Load(_user).Where);
        Assert.False(File.Exists(FileOf(_user) + ".new"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765", true)]
    [InlineData("https://anki.example.org/connect", true)]
    [InlineData("  http://localhost:8765  ", true)]
    [InlineData("", false)]
    [InlineData("127.0.0.1:8765", false)]
    [InlineData("ftp://anki.example.org", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://name:password@anki.example.org", false)]
    [InlineData("https://anki.example.org/#part", false)]
    public void Only_plain_http_and_https_addresses_are_taken(string text, bool taken)
    {
        Assert.Equal(taken, AnkiAddress.TryRead(text, out _));
    }
}
