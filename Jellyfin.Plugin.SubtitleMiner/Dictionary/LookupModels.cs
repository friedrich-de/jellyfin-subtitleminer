using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// What a lookup found, in the shape the page's dictionary popup draws: Hoshidicts' results, which Hachidori's
/// popup reads (<c>dictionary-popup</c>'s <c>LookupResult</c>).
/// </summary>
internal sealed class LookupView
{
    /// <summary>Gets the words found, the best first.</summary>
    public List<LookupEntryView> Results { get; init; } = [];
}

/// <summary>One word found.</summary>
internal sealed class LookupEntryView
{
    /// <summary>Gets the start of the text it was found as.</summary>
    public required string Matched { get; init; }

    /// <summary>Gets the form it was looked up as, after inflections were undone.</summary>
    public required string Deinflected { get; init; }

    /// <summary>Gets the inflections undone, the first undone first.</summary>
    public List<TraceView> Trace { get; init; } = [];

    /// <summary>Gets the word.</summary>
    public required TermView Term { get; init; }
}

/// <summary>An inflection undone.</summary>
internal sealed class TraceView
{
    /// <summary>Gets its short name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets what it means.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}

/// <summary>A word, with what each dictionary says of it.</summary>
internal sealed class TermView
{
    /// <summary>Gets how it is written.</summary>
    public required string Expression { get; init; }

    /// <summary>Gets how it is read.</summary>
    public required string Reading { get; init; }

    /// <summary>Gets its parts of speech, space-separated.</summary>
    public string Rules { get; init; } = string.Empty;

    /// <summary>Gets its definitions, by dictionary.</summary>
    public List<GlossaryView> Glossaries { get; init; } = [];

    /// <summary>Gets its frequencies, by dictionary.</summary>
    public List<FrequencyGroupView> Frequencies { get; init; } = [];

    /// <summary>Gets its pitch accents and pronunciations, by dictionary.</summary>
    public List<PitchGroupView> Pitches { get; init; } = [];
}

/// <summary>What one dictionary says a word means.</summary>
internal sealed class GlossaryView
{
    /// <summary>Gets the dictionary's title.</summary>
    public required string Dictionary { get; init; }

    /// <summary>Gets the definitions as the dictionary stores them: a JSON array of strings or structured content.</summary>
    public required string Glossary { get; init; }

    /// <summary>Gets the tags of the definitions, space-separated.</summary>
    public string DefinitionTags { get; init; } = string.Empty;

    /// <summary>Gets the tags of the word, space-separated.</summary>
    public string TermTags { get; init; } = string.Empty;

    /// <summary>Gets those tags as the dictionary's tag bank explains them.</summary>
    public List<TagView> Tags { get; init; } = [];
}

/// <summary>A tag.</summary>
internal sealed class TagView
{
    /// <summary>Gets its name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets its kind.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Gets where it sorts.</summary>
    public long Order { get; init; }

    /// <summary>Gets what it stands for.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>Gets its score.</summary>
    public long Score { get; init; }
}

/// <summary>The frequencies one dictionary gives a word.</summary>
internal sealed class FrequencyGroupView
{
    /// <summary>Gets the dictionary's title.</summary>
    public required string Dictionary { get; init; }

    /// <summary>Gets the frequencies.</summary>
    public List<FrequencyView> Frequencies { get; init; } = [];
}

/// <summary>A frequency.</summary>
internal sealed class FrequencyView
{
    /// <summary>Gets the number: a rank or a count, as the dictionary's frequency mode says.</summary>
    public double Value { get; init; }

    /// <summary>Gets how the dictionary writes it, when it writes it another way.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayValue { get; init; }
}

/// <summary>The pitch accents and pronunciations one dictionary gives a word.</summary>
internal sealed class PitchGroupView
{
    /// <summary>Gets the dictionary's title.</summary>
    public required string Dictionary { get; init; }

    /// <summary>Gets the pitch accents.</summary>
    public List<PitchView> Pitches { get; init; } = [];

    /// <summary>Gets the word in IPA, when the dictionary gives that.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Transcriptions { get; init; }
}

/// <summary>A pitch accent.</summary>
internal sealed class PitchView
{
    /// <summary>Gets the mora the pitch falls after; 0 when it does not fall.</summary>
    public int Position { get; init; }

    /// <summary>Gets the pattern ("LHL"), when the dictionary writes it that way.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pattern { get; init; }

    /// <summary>Gets the morae said through the nose.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Nasal { get; init; }

    /// <summary>Gets the morae whose vowel is not voiced.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Devoice { get; init; }
}

/// <summary>Writes lookups as the popup reads them, and the dictionary files' own information.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LookupView))]
[JsonSerializable(typeof(DictionaryInfo))]
[JsonSerializable(typeof(DictionaryList))]
internal sealed partial class DictionaryJsonContext : JsonSerializerContext
{
}
