using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>
/// Looks up the words a Japanese text starts with, as Yomitan does: every start of the text from the longest,
/// each in its other spellings, each with its inflections undone, is looked up as written and as read; an entry is
/// kept when its part of speech fits what undoing the inflections asked for. The longest match comes first, then
/// the one with fewer changes of spelling and fewer inflections, then the more frequent and the dictionary's order.
/// </summary>
internal static class DictionaryLookup
{
    /// <summary>Only this many characters of the text are looked at, as Yomitan's default scan length.</summary>
    internal const int ScanLength = 16;

    /// <summary>At most this many words are given.</summary>
    internal const int MaxEntries = 20;

    /// <summary>
    /// Looks <paramref name="text"/> up in <paramref name="dictionaries"/>, which are in the order the
    /// administrator gave them.
    /// </summary>
    /// <param name="text">The text, starting where the word is.</param>
    /// <param name="dictionaries">The dictionaries.</param>
    /// <returns>The words.</returns>
    public static LookupView Find(string text, IReadOnlyList<DictionaryReader> dictionaries)
    {
        var rules = Deinflector.Japanese;
        var forms = Forms(text, rules);
        if (forms.Count == 0)
        {
            return new LookupView();
        }

        // Every row the forms find, each with the best way it was reached.
        var found = new List<Found>();
        for (var index = 0; index < dictionaries.Count; index++)
        {
            var dictionary = dictionaries[index];
            if (dictionary.Info.Terms == 0)
            {
                continue;
            }

            foreach (var row in dictionary.FindTerms(forms.Keys))
            {
                var wanted = rules.ConditionsOfEntry(row.Rules);
                Form? best = null;
                var byExpression = false;
                foreach (var (key, isExpression) in new[] { (row.Expression, true), (row.Reading, false) })
                {
                    if (!forms.TryGetValue(key, out var candidates))
                    {
                        continue;
                    }

                    foreach (var form in candidates)
                    {
                        if (Deinflector.ConditionsMatch(form.Deinflection.Conditions, wanted) && (best is null || Better(form, isExpression, best, byExpression)))
                        {
                            best = form;
                            byExpression = isExpression;
                        }
                    }
                }

                if (best is not null)
                {
                    found.Add(new Found(index, row, best, byExpression));
                }
            }
        }

        // A word is what is written and read alike, whichever dictionaries have it.
        var words = found
            .GroupBy(item => (item.Row.Expression, item.Row.Reading))
            .Select(group => new Word(group.Key.Expression, group.Key.Reading, group.ToList()))
            .ToList();
        if (words.Count == 0)
        {
            return new LookupView();
        }

        // The first dictionary with frequencies sorts words that tie on everything else.
        var metas = Metas(words.Select(word => word.Expression).Distinct().ToList(), dictionaries);
        var sortBy = Enumerable.Range(0, dictionaries.Count).Where(index => dictionaries[index].Info.Frequencies > 0).Select(index => (int?)index).FirstOrDefault();
        var counts = sortBy is { } by && dictionaries[by].Info.FrequencyMode == "occurrence-based";
        var ranked = words
            .Select((word, discovered) => (word, discovered, frequency: sortBy is { } index ? SortFrequency(word, metas[index], counts) : null))
            .OrderBy(item => item, Comparer<(Word Word, int Discovered, double? Frequency)>.Create((a, b) => Compare(a, b, counts)))
            .Take(MaxEntries)
            .Select(item => item.word)
            .ToList();

        var glossaries = new Dictionary<(int, long), string>();
        foreach (var group in ranked.SelectMany(word => word.Rows).GroupBy(item => item.Dictionary))
        {
            foreach (var (id, glossary) in dictionaries[group.Key].Glossaries(group.Select(item => item.Row.Id).ToList()))
            {
                glossaries[(group.Key, id)] = glossary;
            }
        }

        return new LookupView { Results = ranked.Select(word => View(word, dictionaries, glossaries, metas)).ToList() };
    }

    /// <summary>
    /// The forms a text is looked up as: each start of it, in each spelling, with each set of inflections undone,
    /// by the form. A form reached from several starts keeps each.
    /// </summary>
    internal static Dictionary<string, List<Form>> Forms(string text, Deinflector rules)
    {
        var forms = new Dictionary<string, List<Form>>(StringComparer.Ordinal);
        var ends = new List<int>();
        var info = StringInfo.GetTextElementEnumerator(text);
        while (info.MoveNext() && ends.Count < ScanLength)
        {
            var element = info.GetTextElement();
            if (char.IsWhiteSpace(element, 0) || char.IsControl(element, 0))
            {
                break;
            }

            ends.Add(info.ElementIndex + element.Length);
        }

        for (var i = ends.Count - 1; i >= 0; i--)
        {
            var matched = text[..ends[i]];
            foreach (var (spelling, changes) in JapaneseText.Variants(matched))
            {
                foreach (var deinflection in rules.Deinflect(spelling))
                {
                    if (!forms.TryGetValue(deinflection.Text, out var list))
                    {
                        forms[deinflection.Text] = list = [];
                    }

                    list.Add(new Form(matched, i + 1, changes, deinflection));
                }
            }
        }

        return forms;
    }

    private static bool Better(Form a, bool aByExpression, Form b, bool bByExpression)
    {
        var order = Rank(a, aByExpression, b, bByExpression);
        return order < 0;
    }

    /// <summary>Which of two ways to a word is better: negative when <paramref name="a"/> is.</summary>
    private static int Rank(Form a, bool aByExpression, Form b, bool bByExpression)
    {
        var order = b.Length.CompareTo(a.Length);
        if (order == 0)
        {
            order = a.Changes.CompareTo(b.Changes);
        }

        if (order == 0)
        {
            order = a.Deinflection.Trace.Count.CompareTo(b.Deinflection.Trace.Count);
        }

        if (order == 0)
        {
            order = b.Deinflection.WholeWord.CompareTo(a.Deinflection.WholeWord);
        }

        if (order == 0)
        {
            order = bByExpression.CompareTo(aByExpression);
        }

        return order;
    }

    private static int Compare((Word Word, int Discovered, double? Frequency) a, (Word Word, int Discovered, double? Frequency) b, bool counts)
    {
        var order = Rank(a.Word.Best.Form, a.Word.Best.ByExpression, b.Word.Best.Form, b.Word.Best.ByExpression);
        if (order == 0)
        {
            // A word the sorting dictionary knows comes before one it does not; ranks low first, counts high first.
            order = (b.Frequency is not null).CompareTo(a.Frequency is not null);
            if (order == 0 && a.Frequency is { } x && b.Frequency is { } y)
            {
                order = counts ? y.CompareTo(x) : x.CompareTo(y);
            }
        }

        if (order == 0)
        {
            order = a.Word.FirstDictionary.CompareTo(b.Word.FirstDictionary);
        }

        if (order == 0)
        {
            order = b.Word.Score.CompareTo(a.Word.Score);
        }

        return order != 0 ? order : a.Discovered.CompareTo(b.Discovered);
    }

    private static Dictionary<int, List<MetaRow>> Metas(IReadOnlyCollection<string> expressions, IReadOnlyList<DictionaryReader> dictionaries)
    {
        var metas = new Dictionary<int, List<MetaRow>>();
        for (var index = 0; index < dictionaries.Count; index++)
        {
            var info = dictionaries[index].Info;
            if (info.Frequencies > 0 || info.Pitches > 0)
            {
                metas[index] = dictionaries[index].FindMetas(expressions);
            }
        }

        return metas;
    }

    /// <summary>A word's frequency in the sorting dictionary: its best rank, or its highest count.</summary>
    private static double? SortFrequency(Word word, List<MetaRow> metas, bool counts)
    {
        var values = metas
            .Where(meta => meta.Mode == "freq" && meta.Expression == word.Expression)
            .Select(meta => ReadFrequency(meta.Data, word.Reading)?.Value)
            .OfType<double>()
            .ToList();
        if (values.Count == 0)
        {
            return null;
        }

        return counts ? values.Max() : values.Min();
    }

    private static LookupEntryView View(Word word, IReadOnlyList<DictionaryReader> dictionaries, Dictionary<(int, long), string> glossaries, Dictionary<int, List<MetaRow>> metas)
    {
        var best = word.Best.Form;
        var rows = word.Rows
            .OrderBy(item => item.Dictionary)
            .ThenByDescending(item => item.Row.Score)
            .ThenBy(item => item.Row.Id)
            .ToList();
        var term = new TermView
        {
            Expression = word.Expression,
            Reading = word.Reading,
            Rules = string.Join(' ', rows.SelectMany(item => item.Row.Rules.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal)),
            Glossaries = rows.Select(item =>
            {
                var dictionary = dictionaries[item.Dictionary];
                var names = (item.Row.DefinitionTags + " " + item.Row.TermTags).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal);
                return new GlossaryView
                {
                    Dictionary = dictionary.Info.Title,
                    Glossary = glossaries.GetValueOrDefault((item.Dictionary, item.Row.Id), "[]"),
                    DefinitionTags = item.Row.DefinitionTags,
                    TermTags = item.Row.TermTags,
                    Tags = names
                        .Select(name => dictionary.Tags.TryGetValue(name, out var tag)
                            ? new TagView { Name = tag.Name, Category = tag.Category, Order = tag.Order, Notes = tag.Notes, Score = tag.Score }
                            : null)
                        .OfType<TagView>()
                        .ToList(),
                };
            }).ToList(),
        };

        foreach (var (index, rowsOfDictionary) in metas.OrderBy(pair => pair.Key))
        {
            var title = dictionaries[index].Info.Title;
            var frequencies = new List<FrequencyView>();
            var pitches = new List<PitchView>();
            var transcriptions = new List<string>();
            foreach (var meta in rowsOfDictionary.Where(meta => meta.Expression == word.Expression))
            {
                switch (meta.Mode)
                {
                    case "freq":
                        if (ReadFrequency(meta.Data, word.Reading) is { } frequency)
                        {
                            frequencies.Add(frequency);
                        }

                        break;
                    case "pitch":
                        pitches.AddRange(ReadPitches(meta.Data, word.Reading));
                        break;
                    case "ipa":
                        transcriptions.AddRange(ReadTranscriptions(meta.Data, word.Reading));
                        break;
                }
            }

            if (frequencies.Count > 0)
            {
                term.Frequencies.Add(new FrequencyGroupView { Dictionary = title, Frequencies = frequencies });
            }

            if (pitches.Count > 0 || transcriptions.Count > 0)
            {
                term.Pitches.Add(new PitchGroupView { Dictionary = title, Pitches = pitches, Transcriptions = transcriptions.Count > 0 ? transcriptions : null });
            }
        }

        return new LookupEntryView
        {
            Matched = best.Matched,
            Deinflected = best.Deinflection.Text,
            Trace = best.Deinflection.Trace.Select(step => new TraceView { Name = step.Name, Description = step.Description }).ToList(),
            Term = term,
        };
    }

    /// <summary>
    /// A frequency as Yomitan's term meta banks write it: a number, a text, <c>{value, displayValue}</c>, or any of
    /// these under <c>frequency</c> beside the <c>reading</c> it is for. One for another reading gives nothing.
    /// </summary>
    internal static FrequencyView? ReadFrequency(string json, string reading)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reading", out var forReading))
        {
            if (forReading.ValueKind != JsonValueKind.String || forReading.GetString() != reading || !data.TryGetProperty("frequency", out data))
            {
                return null;
            }
        }

        switch (data.ValueKind)
        {
            case JsonValueKind.Number:
                return new FrequencyView { Value = data.GetDouble() };
            case JsonValueKind.String:
                var text = data.GetString()!;
                var digits = new string(text.TakeWhile(char.IsAsciiDigit).ToArray());
                return new FrequencyView { Value = digits.Length > 0 ? double.Parse(digits, CultureInfo.InvariantCulture) : 0, DisplayValue = text };
            case JsonValueKind.Object when data.TryGetProperty("value", out var value) && YomitanArchive.Number(value, out var number):
                return new FrequencyView
                {
                    Value = number,
                    DisplayValue = data.TryGetProperty("displayValue", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() : null,
                };
            default:
                return null;
        }
    }

    /// <summary>Pitch accents as Yomitan writes them: <c>{reading, pitches: [{position, nasal, devoice}]}</c>.</summary>
    internal static IEnumerable<PitchView> ReadPitches(string json, string reading)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("reading", out var forReading) || forReading.GetString() != reading
            || !data.TryGetProperty("pitches", out var pitches) || pitches.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        static List<int>? Morae(JsonElement pitch, string name)
        {
            if (!pitch.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var one) => [one],
                JsonValueKind.Array => value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _)).Select(item => item.GetInt32()).ToList(),
                _ => null,
            };
        }

        var result = new List<PitchView>();
        foreach (var pitch in pitches.EnumerateArray())
        {
            if (pitch.ValueKind != JsonValueKind.Object || !pitch.TryGetProperty("position", out var position))
            {
                continue;
            }

            if (position.ValueKind == JsonValueKind.Number && position.TryGetInt32(out var mora))
            {
                result.Add(new PitchView { Position = mora, Nasal = Morae(pitch, "nasal"), Devoice = Morae(pitch, "devoice") });
            }
            else if (position.ValueKind == JsonValueKind.String)
            {
                // A pattern of highs and lows; the fall is after the last high that a low follows.
                var pattern = position.GetString()!;
                var fall = pattern.IndexOf("HL", StringComparison.Ordinal);
                result.Add(new PitchView { Position = fall < 0 ? 0 : fall + 1, Pattern = pattern, Nasal = Morae(pitch, "nasal"), Devoice = Morae(pitch, "devoice") });
            }
        }

        return result;
    }

    /// <summary>Pronunciations as Yomitan writes them: <c>{reading, transcriptions: [{ipa}]}</c>.</summary>
    internal static IEnumerable<string> ReadTranscriptions(string json, string reading)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("reading", out var forReading) || forReading.GetString() != reading
            || !data.TryGetProperty("transcriptions", out var transcriptions) || transcriptions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return transcriptions.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("ipa", out var ipa) && ipa.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("ipa").GetString()!)
            .ToList();
    }

    /// <summary>A form looked up: the start of the text it came from, its length in characters and how it was made.</summary>
    internal sealed record Form(string Matched, int Length, int Changes, Deinflection Deinflection);

    /// <summary>A dictionary row found, by its dictionary's place in the order.</summary>
    private sealed record Found(int Dictionary, TermRow Row, Form Form, bool ByExpression);

    /// <summary>A word: rows written and read alike.</summary>
    private sealed class Word(string expression, string reading, List<Found> rows)
    {
        public string Expression { get; } = expression;

        public string Reading { get; } = reading;

        public List<Found> Rows { get; } = rows;

        public Found Best { get; } = rows.Aggregate((a, b) => Rank(b.Form, b.ByExpression, a.Form, a.ByExpression) < 0 ? b : a);

        public int FirstDictionary { get; } = rows.Min(row => row.Dictionary);

        public long Score { get; } = rows.Max(row => row.Row.Score);
    }
}
