using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// One inflection undone on the way from the text to a dictionary form.
/// </summary>
/// <param name="Name">The inflection's short name, such as "-た" or "negative".</param>
/// <param name="Description">What it means, for the popup's tooltip.</param>
internal sealed record DeinflectionStep(string Name, string? Description);

/// <summary>
/// A text a dictionary may hold, reached from the text looked up by undoing inflections.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="Conditions">What kind of word the text must be, as flags; 0 for the text as it was given.</param>
/// <param name="Trace">The inflections undone, the first undone first.</param>
/// <param name="WholeWord">Whether the first step read the whole text as one known word (ございます).</param>
internal sealed record Deinflection(string Text, ulong Conditions, IReadOnlyList<DeinflectionStep> Trace, bool WholeWord);

/// <summary>
/// Undoes Japanese inflections: 食べられなかった gives 食べる, among other texts. It runs Yomitan's rules, as the
/// Hoshidicts language pack writes them as data (<c>Dictionary/Japanese/transforms.json</c>), the way Yomitan's
/// LanguageTransformer runs them: every rule whose ending fits the text and whose conditions fit the text's gives a
/// new text, which the rules read again, until none fits.
/// </summary>
internal sealed class Deinflector
{
    /// <summary>A text gets no more than this many rules undone after each other.</summary>
    internal const int MaxDepth = 128;

    /// <summary>A text found is never this many characters longer than the text given.</summary>
    internal const int MaxGrowth = 32;

    /// <summary>A lookup never considers more texts than this for one text given.</summary>
    internal const int MaxResults = 4096;

    private static readonly Lazy<Deinflector> JapaneseRules = new(() =>
    {
        using var stream = typeof(Deinflector).Assembly.GetManifestResourceStream("dictionary/ja/transforms.json")
            ?? throw new InvalidOperationException("The plugin was built without its Japanese rules.");
        return Load(stream);
    });

    private readonly Dictionary<string, Rule[]> _bySuffix;
    private readonly Dictionary<string, Rule[]> _byWord;
    private readonly Dictionary<string, ulong> _conditions;
    private readonly Dictionary<string, ulong> _dictionaryForms;
    private readonly int _longestSuffix;

    private Deinflector(Dictionary<string, Rule[]> bySuffix, Dictionary<string, Rule[]> byWord, Dictionary<string, ulong> conditions, Dictionary<string, ulong> dictionaryForms)
    {
        _bySuffix = bySuffix;
        _byWord = byWord;
        _conditions = conditions;
        _dictionaryForms = dictionaryForms;
        _longestSuffix = bySuffix.Keys.Select(key => key.Length).DefaultIfEmpty(0).Max();
    }

    /// <summary>Gets Japanese, the only language the dictionary reads.</summary>
    public static Deinflector Japanese => JapaneseRules.Value;

    /// <summary>
    /// Whether a text with conditions <paramref name="current"/> may be read by something that wants
    /// <paramref name="wanted"/>: always when the text is as it was given (0), else when they share a flag.
    /// </summary>
    /// <param name="current">The text's conditions.</param>
    /// <param name="wanted">What a rule or a dictionary entry wants.</param>
    /// <returns>Whether they fit.</returns>
    public static bool ConditionsMatch(ulong current, ulong wanted) => current == 0 || (current & wanted) != 0;

    /// <summary>
    /// Reads rules in the Hoshidicts pack's <c>transforms.json</c> shape: <c>conditions</c> (flags, some made of
    /// others) and <c>transforms</c> with their <c>suffix</c> and <c>wholeWord</c> rules.
    /// </summary>
    /// <param name="json">The file.</param>
    /// <returns>The deinflector.</returns>
    public static Deinflector Load(Stream json)
    {
        var file = JsonSerializer.Deserialize(json, TransformsJsonContext.Default.TransformsFile)
            ?? throw new InvalidDataException("The rules file is empty.");

        // Each plain condition gets a bit, in the file's order; one made of others stands for all of theirs.
        var conditions = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var bit = 0;
        foreach (var (name, condition) in file.Conditions)
        {
            if (condition.SubConditions is null || condition.SubConditions.Count == 0)
            {
                if (bit == 64)
                {
                    throw new InvalidDataException("The rules have more than 64 conditions.");
                }

                conditions[name] = 1UL << bit++;
            }
        }

        ulong Flags(string name, int depth)
        {
            if (conditions.TryGetValue(name, out var known))
            {
                return known;
            }

            if (depth > file.Conditions.Count || !file.Conditions.TryGetValue(name, out var condition) || condition.SubConditions is null)
            {
                throw new InvalidDataException($"The rules name a condition they do not define: {name}.");
            }

            var flags = 0UL;
            foreach (var sub in condition.SubConditions)
            {
                flags |= Flags(sub, depth + 1);
            }

            return conditions[name] = flags;
        }

        foreach (var name in file.Conditions.Keys)
        {
            Flags(name, 0);
        }

        var dictionaryForms = file.Conditions
            .Where(pair => pair.Value.IsDictionaryForm)
            .ToDictionary(pair => pair.Key, pair => conditions[pair.Key], StringComparer.Ordinal);

        ulong All(IEnumerable<string>? names) => (names ?? []).Aggregate(0UL, (flags, name) => flags | Flags(name, 0));

        var bySuffix = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        var byWord = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        foreach (var transform in file.Transforms)
        {
            var step = new DeinflectionStep(transform.Name ?? transform.Id, transform.Description);
            foreach (var rule in transform.Rules)
            {
                var target = rule.Type switch
                {
                    "suffix" => bySuffix,
                    "wholeWord" => byWord,
                    _ => throw new InvalidDataException($"The dictionary does not know rules of the type {rule.Type}."),
                };
                if (string.IsNullOrEmpty(rule.Inflected))
                {
                    throw new InvalidDataException($"A rule of {transform.Id} has no ending.");
                }

                var read = new Rule(transform.Id, step, rule.Inflected, rule.Deinflected ?? string.Empty, All(rule.ConditionsIn), All(rule.ConditionsOut), rule.Type == "wholeWord");
                if (!target.TryGetValue(rule.Inflected, out var list))
                {
                    target[rule.Inflected] = list = [];
                }

                list.Add(read);
            }
        }

        return new Deinflector(
            bySuffix.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
            byWord.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
            conditions,
            dictionaryForms);
    }

    /// <summary>
    /// The conditions of a dictionary entry, from its space-separated rule tags ("v5 vt"). Only the tags that name
    /// a dictionary form count; an entry with none matches only a text as it was given.
    /// </summary>
    /// <param name="rules">The entry's rules.</param>
    /// <returns>The flags.</returns>
    public ulong ConditionsOfEntry(string? rules)
    {
        if (string.IsNullOrWhiteSpace(rules))
        {
            return 0;
        }

        var flags = 0UL;
        foreach (var tag in rules.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_dictionaryForms.TryGetValue(tag, out var form))
            {
                flags |= form;
            }
        }

        return flags;
    }

    /// <summary>
    /// The flags of one condition by its name, as the rules file names it, or 0 when it has none of that name.
    /// </summary>
    /// <param name="name">The name, such as "v1" or "adj-i".</param>
    /// <returns>The flags.</returns>
    public ulong ConditionsNamed(string name) => _conditions.GetValueOrDefault(name);

    /// <summary>
    /// Every text that undoing inflections reaches from <paramref name="text"/>, the text itself first, each
    /// with the shortest trace that reached it first.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The texts.</returns>
    public IReadOnlyList<Deinflection> Deinflect(string text)
    {
        var results = new List<Node> { new(text, 0, null, null, 0, false) };
        var limit = text.Length + MaxGrowth;
        for (var i = 0; i < results.Count && results.Count < MaxResults; i++)
        {
            var node = results[i];
            if (node.Depth >= MaxDepth)
            {
                continue;
            }

            if (_byWord.TryGetValue(node.Text, out var words))
            {
                Apply(node, words, results, limit);
            }

            for (var length = Math.Min(node.Text.Length, _longestSuffix); length > 0; length--)
            {
                if (_bySuffix.TryGetValue(node.Text[^length..], out var rules))
                {
                    Apply(node, rules, results, limit);
                }
            }
        }

        return results.Select(node => new Deinflection(node.Text, node.Conditions, node.Steps(), node.WholeWord)).ToList();
    }

    private static void Apply(Node node, Rule[] rules, List<Node> results, int limit)
    {
        foreach (var rule in rules)
        {
            if (results.Count >= MaxResults)
            {
                return;
            }

            if (!ConditionsMatch(node.Conditions, rule.ConditionsIn))
            {
                continue;
            }

            var next = rule.WholeWord ? rule.Deinflected : string.Concat(node.Text.AsSpan(0, node.Text.Length - rule.Inflected.Length), rule.Deinflected);
            if (next.Length == 0 || next.Length > limit || node.HasApplied(rule, node.Text))
            {
                continue;
            }

            results.Add(new Node(next, rule.ConditionsOut, rule, node, node.Depth + 1, node.Depth == 0 ? rule.WholeWord : node.WholeWord));
        }
    }

    private sealed record Rule(string Transform, DeinflectionStep Step, string Inflected, string Deinflected, ulong ConditionsIn, ulong ConditionsOut, bool WholeWord);

    /// <summary>A text found, and the rule and text it was found from.</summary>
    private sealed record Node(string Text, ulong Conditions, Rule? Rule, Node? From, int Depth, bool WholeWord)
    {
        /// <summary>Whether the rule was already applied to the same text on the way here, which would go round.</summary>
        public bool HasApplied(Rule rule, string text)
        {
            for (var node = this; node?.Rule is not null; node = node.From)
            {
                if (ReferenceEquals(node.Rule, rule) && node.From!.Text == text)
                {
                    return true;
                }
            }

            return false;
        }

        public DeinflectionStep[] Steps()
        {
            var steps = new DeinflectionStep[Depth];
            var node = this;
            for (var i = Depth - 1; i >= 0; i--)
            {
                steps[i] = node!.Rule!.Step;
                node = node.From;
            }

            return steps;
        }
    }
}

/// <summary>The rules file, as the Hoshidicts pack writes it.</summary>
internal sealed class TransformsFile
{
    [JsonPropertyName("conditions")]
    public Dictionary<string, TransformCondition> Conditions { get; init; } = [];

    [JsonPropertyName("transforms")]
    public List<TransformEntry> Transforms { get; init; } = [];
}

/// <summary>A condition: a kind of word or of inflected form.</summary>
internal sealed class TransformCondition
{
    [JsonPropertyName("isDictionaryForm")]
    public bool IsDictionaryForm { get; init; }

    [JsonPropertyName("subConditions")]
    public List<string>? SubConditions { get; init; }
}

/// <summary>An inflection and the rules that undo it.</summary>
internal sealed class TransformEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("rules")]
    public List<TransformRule> Rules { get; init; } = [];
}

/// <summary>One rule.</summary>
internal sealed class TransformRule
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("inflected")]
    public string Inflected { get; init; } = string.Empty;

    [JsonPropertyName("deinflected")]
    public string? Deinflected { get; init; }

    [JsonPropertyName("conditionsIn")]
    public List<string>? ConditionsIn { get; init; }

    [JsonPropertyName("conditionsOut")]
    public List<string>? ConditionsOut { get; init; }
}

/// <summary>Reads the rules file without reflection.</summary>
[JsonSerializable(typeof(TransformsFile))]
internal sealed partial class TransformsJsonContext : JsonSerializerContext
{
}
