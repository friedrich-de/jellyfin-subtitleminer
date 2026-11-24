using System;
using System.IO;
using Jellyfin.Plugin.SubtitleMiner.Settings;

namespace Jellyfin.Plugin.SubtitleMiner.Tests;

public sealed class KeyProtectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "subtitle-miner-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string KeyFile(string name = "a") => Path.Combine(_root, name, KeyProtector.FileName);

    [Fact]
    public void A_key_is_stored_encrypted_and_read_back_as_typed()
    {
        var keys = new KeyProtector(KeyFile());

        var stored = keys.Protect("anki-0123456789abcdef");

        Assert.StartsWith(KeyProtector.Prefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", stored, StringComparison.Ordinal);
        Assert.Equal("anki-0123456789abcdef", keys.Reveal(stored));

        // The same key twice looks different each time, so equal keys cannot be spotted in the files.
        Assert.NotEqual(stored, keys.Protect("anki-0123456789abcdef"));

        // Encrypting what is already encrypted changes nothing, and an empty value stays empty.
        Assert.Equal(stored, keys.Protect(stored));
        Assert.Equal(string.Empty, keys.Protect(string.Empty));
        Assert.Equal(string.Empty, keys.Reveal(string.Empty));
        Assert.Equal("ключ & <ü>", keys.Reveal(keys.Protect("ключ & <ü>")));
    }

    [Fact]
    public void The_secret_is_a_file_only_Jellyfins_user_may_read_and_survives_a_restart()
    {
        var stored = new KeyProtector(KeyFile()).Protect("anki-secret");

        Assert.Equal(32, new FileInfo(KeyFile()).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile()));
        }

        Assert.Equal("anki-secret", new KeyProtector(KeyFile()).Reveal(stored));
    }

    [Fact]
    public void A_key_encrypted_elsewhere_or_changed_is_not_read()
    {
        var stored = new KeyProtector(KeyFile("a")).Protect("anki-secret");
        var other = new KeyProtector(KeyFile("b"));
        other.Protect("make the second secret");

        Assert.Equal(string.Empty, other.Reveal(stored));
        Assert.False(other.CanReveal(stored));

        var keys = new KeyProtector(KeyFile("a"));
        var bytes = Convert.FromBase64String(stored[KeyProtector.Prefix.Length..]);
        bytes[20] ^= 1;
        Assert.Equal(string.Empty, keys.Reveal(KeyProtector.Prefix + Convert.ToBase64String(bytes)));
        Assert.Equal(string.Empty, keys.Reveal(KeyProtector.Prefix + "not base64!"));
        Assert.Equal(string.Empty, keys.Reveal(KeyProtector.Prefix + Convert.ToBase64String(new byte[10])));
    }

    [Fact]
    public void Without_its_file_an_encrypted_key_is_not_read_and_no_new_secret_is_made()
    {
        var stored = new KeyProtector(KeyFile("a")).Protect("anki-secret");
        File.Delete(KeyFile("a"));

        var keys = new KeyProtector(KeyFile("a"));

        Assert.Equal(string.Empty, keys.Reveal(stored));
        Assert.False(File.Exists(KeyFile("a")));
    }

    [Fact]
    public void A_key_put_into_the_file_in_plain_by_hand_is_read_as_it_is()
    {
        var keys = new KeyProtector(KeyFile());

        Assert.Equal("typed-by-hand", keys.Reveal("typed-by-hand"));
        Assert.True(keys.CanReveal("typed-by-hand"));
        Assert.False(File.Exists(KeyFile()));
    }
}
