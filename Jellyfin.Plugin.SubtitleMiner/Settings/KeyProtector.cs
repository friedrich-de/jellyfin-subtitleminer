using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubtitleMiner.Settings;

/// <summary>
/// Keeps the AnkiConnect keys in the users' settings files encrypted, so a search of the server's files for a
/// key finds none. The secret that encrypts them is a file of its own in the plugin's data folder, readable only
/// by the user Jellyfin runs as. A value is turned back into the key only where a call sends it.
/// </summary>
/// <remarks>
/// Someone who can read both files can still recover the keys. This guards against the easy cases: a search for
/// key patterns, a copy of the settings folder, a look at the settings through Jellyfin's own plugin calls.
/// </remarks>
internal sealed class KeyProtector
{
    /// <summary>The name of the file that holds the secret, in the plugin's data folder.</summary>
    internal const string FileName = "settings.key";

    /// <summary>What every encrypted value starts with, so it is told apart from a key typed in plain.</summary>
    internal const string Prefix = "enc:v1:";

    private const int SecretLength = 32;
    private const int IvLength = 16;
    private const int TagLength = 32;

    private static readonly byte[] _encryptionInfo = Encoding.ASCII.GetBytes("Subtitle Miner settings encryption");
    private static readonly byte[] _signingInfo = Encoding.ASCII.GetBytes("Subtitle Miner settings signature");

    private readonly string _path;
    private readonly System.Threading.Lock _lock = new();
    private byte[]? _encryptionKey;
    private byte[]? _signingKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyProtector"/> class.
    /// </summary>
    /// <param name="path">The file that holds the secret. It is made on first use.</param>
    public KeyProtector(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyProtector"/> class with a secret held in memory only, for tests.
    /// </summary>
    /// <param name="secret">The secret, 32 bytes.</param>
    internal KeyProtector(byte[] secret)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(secret.Length, SecretLength);
        _path = string.Empty;
        _encryptionKey = HKDF.Expand(HashAlgorithmName.SHA256, secret, SecretLength, _encryptionInfo);
        _signingKey = HKDF.Expand(HashAlgorithmName.SHA256, secret, SecretLength, _signingInfo);
    }

    /// <summary>
    /// Gets the file that holds the secret.
    /// </summary>
    public string KeyFilePath => _path;

    /// <summary>
    /// Tells whether a stored value is encrypted.
    /// </summary>
    /// <param name="stored">The value as the settings hold it.</param>
    /// <returns><c>true</c> for a value this class encrypted.</returns>
    public static bool IsProtected(string? stored) => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts a key. An empty value and one already encrypted stay as they are.
    /// </summary>
    /// <param name="value">The key in plain.</param>
    /// <returns>The value to store.</returns>
    public string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value) || IsProtected(value))
        {
            return value ?? string.Empty;
        }

        var (encryptionKey, signingKey) = Keys();
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        byte[] encrypted;
        using (var aes = Aes.Create())
        {
            aes.Key = encryptionKey;
            encrypted = aes.EncryptCbc(Encoding.UTF8.GetBytes(value), iv, PaddingMode.PKCS7);
        }

        var message = new byte[IvLength + encrypted.Length + TagLength];
        iv.CopyTo(message, 0);
        encrypted.CopyTo(message, IvLength);
        HMACSHA256.HashData(signingKey, message.AsSpan(0, IvLength + encrypted.Length), message.AsSpan(IvLength + encrypted.Length));
        return Prefix + Convert.ToBase64String(message);
    }

    /// <summary>
    /// Turns a stored value back into the key, for the one call that sends it. A value stored in plain, put
    /// there by hand, comes back as it is.
    /// </summary>
    /// <param name="stored">The value as the settings hold it.</param>
    /// <returns>The key, or an empty string when none is stored or the stored one cannot be read.</returns>
    public string Reveal(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        if (!IsProtected(stored))
        {
            return stored;
        }

        return TryReveal(stored, out var value) ? value : string.Empty;
    }

    /// <summary>
    /// Tells whether a stored value can be read with this server's secret.
    /// </summary>
    /// <param name="stored">The value as the settings hold it.</param>
    /// <returns><c>false</c> for an encrypted value that is damaged or was made with another secret.</returns>
    public bool CanReveal(string? stored) => !IsProtected(stored) || TryReveal(stored!, out _);

    private bool TryReveal(string stored, out string value)
    {
        value = string.Empty;
        byte[] message;
        try
        {
            message = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        // At least one block of text after the IV, and whole blocks only.
        var encryptedLength = message.Length - IvLength - TagLength;
        if (encryptedLength < 16 || encryptedLength % 16 != 0)
        {
            return false;
        }

        if (!File.Exists(_path) && _encryptionKey is null)
        {
            // Without the secret nothing can be read, and making a new one here would not help.
            return false;
        }

        var (encryptionKey, signingKey) = Keys();
        Span<byte> tag = stackalloc byte[TagLength];
        HMACSHA256.HashData(signingKey, message.AsSpan(0, IvLength + encryptedLength), tag);
        if (!CryptographicOperations.FixedTimeEquals(tag, message.AsSpan(IvLength + encryptedLength)))
        {
            return false;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = encryptionKey;
            var plain = aes.DecryptCbc(message.AsSpan(IvLength, encryptedLength), message.AsSpan(0, IvLength), PaddingMode.PKCS7);
            value = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private (byte[] EncryptionKey, byte[] SigningKey) Keys()
    {
        lock (_lock)
        {
            if (_encryptionKey is null || _signingKey is null)
            {
                var secret = ReadOrCreateSecret();
                _encryptionKey = HKDF.Expand(HashAlgorithmName.SHA256, secret, SecretLength, _encryptionInfo);
                _signingKey = HKDF.Expand(HashAlgorithmName.SHA256, secret, SecretLength, _signingInfo);
                CryptographicOperations.ZeroMemory(secret);
            }

            return (_encryptionKey, _signingKey);
        }
    }

    private byte[] ReadOrCreateSecret()
    {
        if (File.Exists(_path))
        {
            var existing = File.ReadAllBytes(_path);
            if (existing.Length == SecretLength)
            {
                return existing;
            }

            // A damaged file: what was encrypted with it is lost either way, so a new secret takes its place.
            File.Delete(_path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            // Only the user Jellyfin runs as may read it.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(_path, options))
        {
            stream.Write(secret);
            stream.Flush(flushToDisk: true);
        }

        return secret;
    }
}
