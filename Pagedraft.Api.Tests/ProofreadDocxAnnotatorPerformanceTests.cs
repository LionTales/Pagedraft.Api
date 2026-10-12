using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Export;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The annotator on a chapter the size of the real one that made the owner wait (plan
/// whole-book-proofread-and-annotated-docx-2026-10-11, c12: 'זיכרונות של מכשף להגהה' chapter 1 is 38,209 words
/// with 1,476 pending suggestions, 1,241 of them ONE character long: 999 a no-break space, 242 a space). A
/// one-character original occurs thousands of times in a chapter, so every such suggestion is a many-way tie
/// settled by its contexts; the shipped tie-break sliced both context windows per hit, 10.4 million hits on
/// that chapter, 1.7 s of its 2.0 s resolve. The fixture reproduces that shape with a comma as the frequent
/// character: ~40,000 words, 1,000 pending suggestions, 70% of them a single comma, the rest whole words and
/// pure insertions, each with the 49-character contexts the pipeline stores.
///
/// <para>THE BOUND IS GENEROUS ON PURPOSE: 10 s, on a machine where this measured 0.7 to 0.9 s after the c12
/// fix and 1.3 to 2.1 s before it (the owner's laptop, Intel Core i7-13620H, 2026-10-12), so a busy CI box
/// does not flake, while a quadratic path at this scale (a per-edit body walk, a per-hit window scan) still
/// blows through it. The non-vacuity floor (most suggestions placed) keeps a fixture that silently stops
/// anchoring from passing fast for the wrong reason.</para>
/// </summary>
public class ProofreadDocxAnnotatorPerformanceTests
{
    private const int ParagraphCount = 400;
    private const int WordsPerParagraph = 100;
    private const int SuggestionCount = 1_000;
    private const int ContextLength = 49;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DateTime Stamp = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Vocabulary =
    {
        "הוא", "היא", "הלך", "ראה", "אמר", "בית", "ספר", "דרך", "יום", "לילה", "אור", "חושך", "מכשף", "זיכרון",
        "עיר", "שער", "מים", "אש", "רוח", "אבן", "עץ", "שמים", "ארץ", "ים", "הר", "עמק", "שקט", "קול", "מילה",
        "חלום", "לב", "יד", "עין", "פנים", "דלת", "חלון", "שולחן", "כוס", "לחם", "מלח",
    };

    private readonly ITestOutputHelper _output;

    public ProofreadDocxAnnotatorPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_forty_thousand_word_chapter_with_a_thousand_pending_suggestions_annotates_within_the_bound()
    {
        var paragraphs = BuildParagraphs(new Random(20261012));
        var docx = BuildDocx(paragraphs);
        var suggestions = BuildSuggestions(string.Join(" ", paragraphs), new Random(12));
        var annotator = new ProofreadDocxAnnotator(NullLogger<ProofreadDocxAnnotator>.Instance);

        Assert.Equal(ParagraphCount * WordsPerParagraph, paragraphs.Sum(p => p.Split(' ').Length));
        Assert.Equal(SuggestionCount, suggestions.Count);

        var clock = Stopwatch.StartNew();
        var result = annotator.Annotate(docx, suggestions, "he", Stamp);
        clock.Stop();
        _output.WriteLine($"Annotate: {clock.ElapsedMilliseconds} ms, placed {result.Placed}, unanchored {result.Unanchored}");

        Assert.Equal(SuggestionCount, result.Placed + result.Unanchored);
        Assert.True(result.Placed >= 950, $"only {result.Placed} of {SuggestionCount} placed: the fixture stopped anchoring");
        Assert.True(clock.Elapsed < Bound, $"Annotate took {clock.ElapsedMilliseconds} ms (bound {Bound.TotalMilliseconds} ms)");

        // The output is a real package whose revisions DocIO can count: one del per non-insertion, one ins per
        // non-empty replacement, so the timing above was spent writing what it should have.
        using var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx);
        Assert.True(reloaded.Revisions.Count >= result.Placed);
    }

    /// <summary>
    /// The c12 tie-break no longer slices a window per hit; it locates each context once and scores only the
    /// hits within reach of an occurrence. It must pick exactly what the slice rule picked, so this runs the
    /// shipped rule (kept below verbatim as the reference) against it on random small-alphabet texts, where
    /// ties, overlapping occurrences, contexts longer than the window, empty contexts and hits at both ends of
    /// the text are all common.
    /// </summary>
    [Fact]
    public void PickBestMatch_picks_what_the_slice_rule_picked_on_random_inputs()
    {
        var random = new Random(1012);
        var compared = 0;
        var picked = 0;
        for (var round = 0; round < 6_000; round++)
        {
            var text = RandomText(random, random.Next(20, 400));
            var needleStart = random.Next(text.Length - 3);
            var needle = text.Substring(needleStart, random.Next(1, 4));
            var positions = AllOccurrences(text, needle);
            if (positions.Count < 2) continue;

            var s = new AnalysisSuggestion
            {
                OriginalText = needle,
                ContextBefore = RandomContext(random, text, needleStart, before: true),
                ContextAfter = RandomContext(random, text, needleStart + needle.Length, before: false),
            };
            if (string.IsNullOrEmpty(s.ContextBefore) && string.IsNullOrEmpty(s.ContextAfter)) continue;

            var expected = SliceRule(positions, needle.Length, text, s);
            Assert.Equal(expected, ProofreadDocxAnnotator.PickBestMatch(positions, needle.Length, text, s));
            compared++;
            if (expected != null) picked++;
        }

        // Non-vacuity: enough cases ran, and both answers (a pick and "ambiguous") were exercised many times.
        Assert.True(compared > 3_000, $"only {compared} cases compared");
        Assert.True(picked > 500 && compared - picked > 500, $"picked {picked} of {compared}");
    }

    /// <summary>The tie-break as shipped before c12 (one slice per hit per context), the reference.</summary>
    private static int? SliceRule(IReadOnlyList<int> positions, int needleLength, string text, AnalysisSuggestion s)
    {
        var bestPos = positions[0];
        var bestScore = int.MinValue;
        foreach (var pos in positions)
        {
            var score = 0;
            if (!string.IsNullOrEmpty(s.ContextBefore))
            {
                var windowStart = Math.Max(0, pos - ProofreadDocxAnnotator.ContextWindow);
                if (text[windowStart..pos].Contains(s.ContextBefore, StringComparison.Ordinal)) score += 2;
            }
            if (!string.IsNullOrEmpty(s.ContextAfter))
            {
                var afterStart = pos + needleLength;
                var afterEnd = Math.Min(text.Length, afterStart + ProofreadDocxAnnotator.ContextWindow);
                if (text[afterStart..afterEnd].Contains(s.ContextAfter, StringComparison.Ordinal)) score += 2;
            }
            if (score > bestScore)
            {
                bestScore = score;
                bestPos = pos;
            }
        }
        return bestScore <= 0 ? null : bestPos;
    }

    private static string RandomText(Random random, int length)
    {
        const string alphabet = "אבג ,";
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }

    /// <summary>
    /// Mostly the real neighbourhood of the needle (so it scores), sometimes a slice from elsewhere, a random
    /// string, an over-long context, or nothing at all.
    /// </summary>
    private static string? RandomContext(Random random, string text, int at, bool before)
    {
        switch (random.Next(8))
        {
            case 0: return null;
            case 1: return string.Empty;
            case 2: return RandomText(random, random.Next(1, 12));
            case 3:
                var from = random.Next(text.Length);
                return text.Substring(from, Math.Min(text.Length - from, random.Next(1, 8)));
            case 4:
                var longLength = random.Next(55, 70);
                return before
                    ? text[Math.Max(0, at - longLength)..at]
                    : text[at..Math.Min(text.Length, at + longLength)];
            default:
                var length = random.Next(1, 30);
                return before
                    ? text[Math.Max(0, at - length)..at]
                    : text[at..Math.Min(text.Length, at + length)];
        }
    }

    private static List<int> AllOccurrences(string text, string needle)
    {
        var positions = new List<int>();
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            positions.Add(i);
        return positions;
    }

    /// <summary>Sentences of eight words, a comma after the fourth word and a period at the end.</summary>
    private static List<string> BuildParagraphs(Random random)
    {
        var paragraphs = new List<string>(ParagraphCount);
        for (var p = 0; p < ParagraphCount; p++)
        {
            var text = new StringBuilder();
            for (var w = 0; w < WordsPerParagraph; w++)
            {
                if (w > 0) text.Append(' ');
                text.Append(Vocabulary[random.Next(Vocabulary.Length)]);
                if (w % 8 == 3) text.Append(',');
                else if (w % 8 == 7 || w == WordsPerParagraph - 1) text.Append('.');
            }
            paragraphs.Add(text.ToString());
        }
        return paragraphs;
    }

    /// <summary>
    /// Each paragraph as three runs (plain, bold, plain) so spans can cross run boundaries the way an imported
    /// manuscript's formatting makes them.
    /// </summary>
    private static byte[] BuildDocx(IReadOnlyList<string> paragraphs)
    {
        using var doc = new WordDocument();
        var section = doc.AddSection();
        foreach (var text in paragraphs)
        {
            var paragraph = section.AddParagraph();
            paragraph.ParagraphFormat.Bidi = true;
            var third = text.Length / 3;
            foreach (var (part, bold) in new[]
                     {
                         (text[..third], false), (text[third..(2 * third)], true), (text[(2 * third)..], false),
                     })
            {
                var range = paragraph.AppendText(part);
                range.CharacterFormat.Bidi = true;
                range.CharacterFormat.Bold = bold;
                range.CharacterFormat.BoldBidi = bold;
            }
        }
        using var stream = new MemoryStream();
        doc.Save(stream, FormatType.Docx);
        return stream.ToArray();
    }

    /// <summary>
    /// Suggestions on the NORMALIZED axis of the whole chapter (paragraphs joined by one space, as the snapshot
    /// has them), spaced so none overlaps another: 70% delete a comma, 20% replace a word, 10% insert one.
    /// </summary>
    private static List<AnalysisSuggestion> BuildSuggestions(string text, Random random)
    {
        var commas = Enumerable.Range(0, text.Length).Where(i => text[i] == ',').ToList();
        var spaces = Enumerable.Range(0, text.Length).Where(i => text[i] == ' ').ToList();
        var stride = text.Length / SuggestionCount;
        var suggestions = new List<AnalysisSuggestion>(SuggestionCount);
        for (var i = 0; i < SuggestionCount; i++)
        {
            var from = i * stride + ContextLength + 1;
            var kind = random.Next(10);
            int start;
            string original, suggested;
            if (kind < 7)
            {
                start = commas.First(c => c >= from);
                (original, suggested) = (",", string.Empty);
            }
            else if (kind < 9)
            {
                start = spaces.First(s => s >= from) + 1;
                var end = text.IndexOf(' ', start);
                original = text[start..end].TrimEnd(',', '.');
                suggested = original + "ה";
            }
            else
            {
                start = spaces.First(s => s >= from);
                (original, suggested) = (string.Empty, " גם");
            }

            var end2 = start + original.Length;
            suggestions.Add(new AnalysisSuggestion
            {
                Id = Guid.NewGuid(),
                OriginalText = original,
                SuggestedText = suggested,
                StartOffset = start,
                EndOffset = end2,
                ContextBefore = text[Math.Max(0, start - ContextLength)..start],
                ContextAfter = text[end2..Math.Min(text.Length, end2 + ContextLength)],
                OrderIndex = i,
            });
        }
        return suggestions;
    }
}
