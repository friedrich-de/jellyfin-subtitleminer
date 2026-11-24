using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubtitleMiner.Dictionary;

namespace Jellyfin.Plugin.SubtitleMiner.Tests.Dictionary;

public class DeinflectorTests
{
    private static readonly Deinflector Rules = Deinflector.Japanese;

    /// <summary>
    /// The rows of Yomitan's Japanese conjugation tests and of the Hoshidicts pack's own: a text, the dictionary form
    /// it must (or must not) reach, what kind of word that is, and the inflections in the order they were made.
    /// </summary>
    public static TheoryData<string, string, bool, string, string, string> Cases()
    {
        var data = new TheoryData<string, string, bool, string, string, string>();
        foreach (var file in new[] { "yomitan.tsv", "cases.tsv" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Dictionary", "Japanese", file);
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith('#') || line.Length == 0)
                {
                    continue;
                }

                var cells = line.Split('\t');
                var category = cells[0];

                // Cat speech is a pass of its own in Hoshidicts, not a rule; the dictionary does not read it.
                if (category == "cat speech")
                {
                    continue;
                }

                data.Add(file + ": " + category, cells[2], cells[1] == "1", cells[3], cells[4], cells.Length > 5 ? cells[5] : string.Empty);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Inflected_words_reach_their_dictionary_form_by_the_inflections_they_were_made_with(string category, string source, bool valid, string term, string rule, string reasons)
    {
        var found = Rules.Deinflect(source).Any(result => Fits(result, term, rule, reasons));

        Assert.True(found == valid, $"{category}: {source} {(valid ? "should" : "should not")} give {term} ({rule}) by {reasons}");
    }

    [Fact]
    public void The_tests_cover_all_of_yomitans_rows()
    {
        // 1,446 rows in Yomitan's corpus and the pack's 46, less its 13 rows of cat speech.
        Assert.Equal(1_446 + 46 - 13, Cases().Count);
    }

    [Fact]
    public void A_text_comes_first_as_it_is_with_no_conditions()
    {
        var first = Rules.Deinflect("食べられなかった")[0];

        Assert.Equal("食べられなかった", first.Text);
        Assert.Equal(0UL, first.Conditions);
        Assert.Empty(first.Trace);
    }

    [Fact]
    public void The_trace_lists_the_inflections_in_the_order_they_were_undone()
    {
        var result = Rules.Deinflect("食べられなかった").First(result => result.Text == "食べる" && Deinflector.ConditionsMatch(result.Conditions, Rules.ConditionsOfEntry("v1")));

        Assert.Equal(["-た", "negative", "potential or passive"], result.Trace.Select(step => step.Name));
        Assert.All(result.Trace, step => Assert.False(string.IsNullOrEmpty(step.Description)));
        Assert.False(result.WholeWord);
    }

    [Fact]
    public void A_word_read_whole_by_a_rule_says_so()
    {
        var result = Rules.Deinflect("ございます").First(result => result.Text == "ござる");

        Assert.True(result.WholeWord);
        Assert.DoesNotContain(Rules.Deinflect("偽ございます"), result => result.Text == "偽ござる");
    }

    [Fact]
    public void An_entry_counts_only_the_tags_of_dictionary_forms()
    {
        Assert.Equal(0UL, Rules.ConditionsOfEntry(null));
        Assert.Equal(0UL, Rules.ConditionsOfEntry("n vt"));
        Assert.NotEqual(0UL, Rules.ConditionsOfEntry("v5 vt"));
        Assert.Equal(Rules.ConditionsOfEntry("v1"), Rules.ConditionsNamed("v1"));
        Assert.True(Deinflector.ConditionsMatch(0, Rules.ConditionsOfEntry("n")));
        Assert.False(Deinflector.ConditionsMatch(Rules.ConditionsNamed("v1"), Rules.ConditionsOfEntry("adj-i")));
    }

    [Fact]
    public void A_long_text_stays_within_bounds()
    {
        var text = string.Concat(Enumerable.Repeat("させられませんでした", 30));

        var results = Rules.Deinflect(text);

        Assert.InRange(results.Count, 1, Deinflector.MaxResults);
        Assert.All(results, result => Assert.InRange(result.Text.Length, 1, text.Length + Deinflector.MaxGrowth));
    }

    private static bool Fits(Deinflection result, string term, string rule, string reasons)
    {
        if (result.Text != term)
        {
            return false;
        }

        if (rule != "*" && !Deinflector.ConditionsMatch(result.Conditions, Rules.ConditionsNamed(rule)))
        {
            return false;
        }

        if (reasons == "*")
        {
            return true;
        }

        // The tests list inflections in the order they were made, the reverse of the order they are undone.
        IEnumerable<string> made = reasons.Length == 0 ? [] : reasons.Split('|');
        return result.Trace.Select(step => step.Name).Reverse().SequenceEqual(made);
    }
}
