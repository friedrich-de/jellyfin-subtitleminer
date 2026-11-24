using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// A user's settings as the page gets them. The key of a server AnkiConnect is never in it.
/// </summary>
public sealed record SettingsView
{
    /// <summary>Gets where the user's cards go.</summary>
    public required AnkiView Anki { get; init; }

    /// <summary>Gets what only the page reads, as it saved it. Missing until it saves something.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Page { get; init; }

    /// <summary>Gets the settings for everyone on the server. Only administrators get them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EveryoneView? Everyone { get; init; }
}

/// <summary>
/// Where a user's cards go.
/// </summary>
public sealed record AnkiView
{
    /// <summary>Gets <c>off</c>, <c>device</c> or <c>server</c>.</summary>
    public required string Where { get; init; }

    /// <summary>Gets the address of the AnkiConnect on the user's device.</summary>
    public required string DeviceAddress { get; init; }

    /// <summary>Gets its key. The browser sends it, so the page needs it.</summary>
    public required string DeviceKey { get; init; }

    /// <summary>Gets the address of the AnkiConnect the server calls.</summary>
    public required string ServerAddress { get; init; }

    /// <summary>Gets a value indicating whether a key for it is stored. The key itself stays on the server.</summary>
    public required bool ServerKeySet { get; init; }

    /// <summary>Gets a value indicating whether this user may have the server send their cards.</summary>
    public required bool ServerAllowed { get; init; }
}

/// <summary>
/// The settings an administrator makes for everyone.
/// </summary>
public sealed record EveryoneView
{
    /// <summary>Gets a value indicating whether users who are not administrators may send cards through the server.</summary>
    public required bool AnkiThroughServer { get; init; }
}

/// <summary>
/// What the page sends to change a user's settings. What is left out stays as it is.
/// </summary>
public sealed record SettingsChange
{
    /// <summary>Gets where the cards go.</summary>
    public AnkiChange? Anki { get; init; }

    /// <summary>Gets what only the page reads. It replaces what was there.</summary>
    public JsonElement? Page { get; init; }
}

/// <summary>
/// A change of where the cards go. What is left out stays as it is.
/// </summary>
public sealed record AnkiChange
{
    /// <summary>Gets <c>off</c>, <c>device</c> or <c>server</c>.</summary>
    public string? Where { get; init; }

    /// <summary>Gets the address of the AnkiConnect on the device.</summary>
    public string? DeviceAddress { get; init; }

    /// <summary>Gets its key. Empty takes it away.</summary>
    public string? DeviceKey { get; init; }

    /// <summary>Gets the address of the AnkiConnect the server calls. Empty takes it away.</summary>
    public string? ServerAddress { get; init; }

    /// <summary>Gets its key. Empty takes it away.</summary>
    public string? ServerKey { get; init; }
}

/// <summary>
/// A call the page makes to its AnkiConnect through the server.
/// </summary>
public sealed record AnkiCall
{
    /// <summary>Gets the action, as AnkiConnect names it.</summary>
    public required string Action { get; init; }

    /// <summary>Gets its parameters.</summary>
    public JsonElement? Params { get; init; }
}

/// <summary>
/// A file the server put into the user's Anki.
/// </summary>
public sealed record StoredMediaView
{
    /// <summary>Gets the name Anki stored it under, for the card's field.</summary>
    public required string FileName { get; init; }
}
