using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleMiner.Api;

/// <summary>
/// The dictionaries of the server, what an import does, and whether the user may change them.
/// </summary>
public sealed record DictionariesView
{
    /// <summary>Gets the dictionaries in the order lookups read them.</summary>
    public required IReadOnlyList<DictionaryView> Dictionaries { get; init; }

    /// <summary>Gets what the import that runs or ran last is doing.</summary>
    public required DictionaryImportView Import { get; init; }

    /// <summary>Gets a value indicating whether the user may add, arrange and remove dictionaries: administrators may.</summary>
    public required bool MayManage { get; init; }
}

/// <summary>
/// A dictionary of the server.
/// </summary>
public sealed record DictionaryView
{
    /// <summary>Gets its name in the plugin's folder, which the calls that change it use.</summary>
    public required string Id { get; init; }

    /// <summary>Gets its title, as lookups name it.</summary>
    public required string Title { get; init; }

    /// <summary>Gets its revision.</summary>
    public required string Revision { get; init; }

    /// <summary>Gets a value indicating whether lookups use it.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Gets the number of words in it.</summary>
    public required long Terms { get; init; }

    /// <summary>Gets the number of frequencies in it.</summary>
    public required long Frequencies { get; init; }

    /// <summary>Gets the number of pitch accents and pronunciations in it.</summary>
    public required long Pitches { get; init; }

    /// <summary>Gets whether its frequencies are ranks ("rank-based") or counts ("occurrence-based").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FrequencyMode { get; init; }

    /// <summary>Gets who made it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Author { get; init; }

    /// <summary>Gets its web site.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    /// <summary>Gets its licence and sources.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Attribution { get; init; }

    /// <summary>Gets where it can be downloaded again, for a newer revision.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadUrl { get; init; }

    /// <summary>Gets its stylesheet, which the popup keeps to its definitions; "" when it has none.</summary>
    public required string Styles { get; init; }

    /// <summary>Gets the size of its file on the server, in bytes.</summary>
    public required long Bytes { get; init; }

    /// <summary>Gets when it was imported.</summary>
    public required DateTimeOffset Imported { get; init; }
}

/// <summary>
/// What an import does.
/// </summary>
public sealed record DictionaryImportView
{
    /// <summary>Gets <c>idle</c>, <c>downloading</c>, <c>importing</c>, <c>done</c> or <c>failed</c>.</summary>
    public required string State { get; init; }

    /// <summary>Gets what is imported: a file's name or an address.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>Gets how far it is, from 0 to 1.</summary>
    public required double Progress { get; init; }

    /// <summary>Gets the title of the dictionary imported.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>Gets why it failed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>
/// A new order of the dictionaries, and which are off.
/// </summary>
public sealed record DictionaryOrderChange
{
    /// <summary>Gets every dictionary's <see cref="DictionaryView.Id"/>, once, the first first.</summary>
    public required IReadOnlyList<string> Order { get; init; }

    /// <summary>Gets the dictionaries lookups do not use.</summary>
    public IReadOnlyList<string> Disabled { get; init; } = [];
}

/// <summary>
/// An address to download a dictionary from.
/// </summary>
public sealed record DictionaryDownload
{
    /// <summary>Gets the address of the dictionary's zip.</summary>
    public required string Url { get; init; }
}
