using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleMiner.Settings;

/// <summary>
/// Keeps each user's settings in a file of its own in the plugin's data folder, readable only by the user
/// Jellyfin runs as. The AnkiConnect keys in it are encrypted.
/// </summary>
internal sealed class UserSettingsStore
{
    /// <summary>The most the page's own part may hold, written out.</summary>
    internal const int MaxPageBytes = 64 * 1024;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly ILogger<UserSettingsStore> _logger;
    private readonly System.Threading.Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSettingsStore"/> class.
    /// </summary>
    /// <param name="folder">The folder for the files. It is made on the first save.</param>
    /// <param name="keys">What encrypts the keys.</param>
    /// <param name="logger">The logger.</param>
    public UserSettingsStore(string folder, KeyProtector keys, ILogger<UserSettingsStore> logger)
    {
        _folder = folder;
        Keys = keys;
        _logger = logger;
    }

    /// <summary>
    /// Gets what encrypts the keys and turns them back.
    /// </summary>
    public KeyProtector Keys { get; }

    /// <summary>
    /// Reads a user's settings.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The settings, or the defaults for a user who has saved none.</returns>
    public UserSettings Load(Guid userId)
    {
        var path = PathOf(userId);
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                return new UserSettings();
            }

            try
            {
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(path), _json) ?? new UserSettings();
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "The Subtitle Miner settings in {Path} could not be read. The user gets the defaults.", path);
                return new UserSettings();
            }
        }
    }

    /// <summary>
    /// Writes a user's settings. The file is replaced whole, so a crash leaves the old one or the new one.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="settings">The settings, with the keys encrypted already.</param>
    public void Save(Guid userId, UserSettings settings)
    {
        var path = PathOf(userId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, _json);
        lock (_lock)
        {
            Directory.CreateDirectory(_folder);
            var temporary = path + ".new";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
    }

    private string PathOf(Guid userId) => Path.Combine(_folder, userId.ToString("N") + ".json");
}
