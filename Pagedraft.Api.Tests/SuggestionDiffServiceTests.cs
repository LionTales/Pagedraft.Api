using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DiffPlex;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

public class SuggestionDiffServiceTests
{
    private readonly SuggestionDiffService _sut = new();

    [Fact]
    public void ComputeProofreadSuggestions_StraySpaceBeforePeriod_ProducesWhitespaceCorrection()
    {
        // Regression: removing a stray space before sentence-final punctuation is a real correction.
        // The trim-equality "meaningful" filter used to drop it because the only change was trailing
        // whitespace on the word span.
        const string original = "דרך המראה ראיתי שהיא מסמיקה .";
        const string result = "דרך המראה ראיתי שהיא מסמיקה.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var s = Assert.Single(suggestions);
        Assert.EndsWith(" ", s.OriginalText);
        Assert.Equal(s.OriginalText.TrimEnd(), s.SuggestedText);
    }

    [Fact]
    public void ComputeProofreadSuggestions_WordJoin_KeptAsOneSuggestion()
    {
        // Regression: a space deleted between two words ("ל הראות" → "להראות") joins them into one.
        // The oversized-range splitter used to break the merged range into two single words, each of
        // which mapped to an unchanged word, losing the join entirely.
        const string original = "שירה התחילה ל הראות סימני לחץ.";
        const string result = "שירה התחילה להראות סימני לחץ.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var s = Assert.Single(suggestions);
        Assert.Equal("ל הראות", s.OriginalText);
        Assert.Equal("להראות", s.SuggestedText);
    }

    /// <summary>
    /// Apply every suggestion back onto the normalized original (highest offset first so earlier
    /// offsets stay valid). This is the invariant that matters in production: the editor applies
    /// suggestions INDIVIDUALLY by offset, so the suggestion set - not just resultText - must be
    /// able to reproduce the corrected text. A suggestion set that cannot is actively corrupting.
    /// </summary>
    private static string ApplyAllSuggestions(string original, IEnumerable<AnalysisSuggestion> suggestions)
    {
        var text = TextNormalization.NormalizeTextForAnalysis(original);
        foreach (var s in suggestions.OrderByDescending(x => x.StartOffset!.Value))
        {
            var start = s.StartOffset!.Value;
            var end = s.EndOffset!.Value;
            text = text[..start] + s.SuggestedText + text[end..];
        }
        return text;
    }

    [Fact]
    public void ComputeProofreadSuggestions_DuplicatedWordRemoved_SuggestionsReconstructResult()
    {
        // Live leak (proofread fixture): the model removed a duplicated "היא". DiffPlex's minimal
        // character diff does not align on a word boundary for a repeated token, and the forced
        // one-word split then emitted "היא"→"י" plus "ידעה"→"דעה" - applying those individually
        // produced "י דעה", corrupting the sentence. The suggestion set must reconstruct the result.
        const string original = "היא היא ידעה שהיום הזה יהיה ארוך.";
        const string result = "היא ידעה שהיום הזה יהיה ארוך.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        Assert.Equal(
            TextNormalization.NormalizeTextForAnalysis(result),
            ApplyAllSuggestions(original, suggestions));
    }

    [Fact]
    public void ComputeProofreadSuggestions_BracketedNoteRemoved_SuggestionsReconstructResult()
    {
        // Live leak (proofread fixture): removing a leftover editorial note "[לבדוק את התאריך]"
        // fragmented into "לכתוב"→"לכתו", "לבדוק"→"ב", "את"→"", "התאריך"→"", which mangles the
        // adjacent word when applied. A whole-word deletion is not a whitespace-only join, so the
        // splitter's existing join special-case did not protect it.
        const string original = "התיישבה ליד השולחן והתחילה לכתוב [לבדוק את התאריך]. המילים באו לאט.";
        const string result = "התיישבה ליד השולחן והתחילה לכתוב. המילים באו לאט.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        Assert.Equal(
            TextNormalization.NormalizeTextForAnalysis(result),
            ApplyAllSuggestions(original, suggestions));
    }

    /// <summary>
    /// Every emitted suggestion must be APPLICABLE, not merely arithmetically consistent. A zero-width
    /// span (StartOffset == EndOffset, OriginalText "") is inert in production: the client anchors a
    /// suggestion by searching the document for OriginalText, and suggestion-anchor.service.ts marks an
    /// empty needle stale, after which editor-page.component.ts computes idx = -1, warns, and returns -
    /// so the Apply button silently does nothing.
    ///
    /// WHY THE EXISTING TESTS WERE BLIND to this: both of the invariants they pin are SATISFIED by a
    /// degenerate suggestion. ApplyAllSuggestions reconstructs the result correctly, because splicing
    /// the suggested text into a zero-width range (inserting at [4,4)) is arithmetically the right
    /// answer; and the offset-integrity check compares normOrig[4..4] ("") to OriginalText ("") and
    /// passes trivially. Reconstruction is NECESSARY but NOT SUFFICIENT - the set must additionally
    /// consist of spans the editor can actually locate.
    /// </summary>
    private static void AssertNoDegenerateSuggestions(IEnumerable<AnalysisSuggestion> suggestions)
    {
        // Assert.All over an EMPTY collection passes, so a caller whose input stopped producing
        // suggestions would silently turn this whole check into a no-op. Every current caller already
        // asserts non-emptiness itself; this makes the helper carry that requirement so a future one
        // cannot forget it.
        Assert.NotEmpty(suggestions);

        Assert.All(suggestions, s =>
        {
            Assert.False(
                s.StartOffset!.Value == s.EndOffset!.Value,
                $"zero-width span [{s.StartOffset}-{s.EndOffset}] for suggested text '{s.SuggestedText}' " +
                "can never be applied (the client cannot anchor an empty OriginalText)");
            Assert.False(
                string.IsNullOrEmpty(s.OriginalText),
                $"empty OriginalText at [{s.StartOffset}-{s.EndOffset}] for suggested text " +
                $"'{s.SuggestedText}' can never be applied (the client anchors by searching for it)");
        });
    }

    [Theory]
    [InlineData("היא היא ידעה שהיום הזה יהיה ארוך.", "היא ידעה שהיום הזה יהיה ארוך.")]
    [InlineData("התיישבה ליד השולחן והתחילה לכתוב [לבדוק את התאריך]. המילים באו לאט.",
                "התיישבה ליד השולחן והתחילה לכתוב. המילים באו לאט.")]
    [InlineData("שירה התחילה ל הראות סימני לחץ.", "שירה התחילה להראות סימני לחץ.")]
    // Pure INSERTIONS: the added word's span expands to the following word, whose entire content is
    // also the shared trailing run, so the affix trim used to consume the original side completely and
    // emit [4,4) "" => "xyz ". These rows pin the non-degeneracy back-off (be-c01).
    [InlineData("abc def", "abc xyz def")]
    [InlineData("hello world", "hello brave world")]
    [InlineData("שלום עולם", "שלום גדול עולם")]
    public void ComputeProofreadSuggestions_OriginalTextAlwaysMatchesItsOwnOffsets(string original, string result)
    {
        // The editor re-anchors a suggestion by searching for OriginalText (suggestionAnchorService
        // .relocateOne), so OriginalText MUST be exactly the normalized slice its offsets point at.
        // If they disagree, the apply path relocates onto the wrong span.
        var normOrig = TextNormalization.NormalizeTextForAnalysis(original);

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        Assert.NotEmpty(suggestions);

        Assert.All(suggestions, s =>
        {
            Assert.Equal(normOrig[s.StartOffset!.Value..s.EndOffset!.Value], s.OriginalText);
        });

        // Offset integrity alone is trivially true for a zero-width span, so also require applicability.
        AssertNoDegenerateSuggestions(suggestions);

        // And the set must still reproduce the corrected text after the trim backs off.
        Assert.Equal(
            TextNormalization.NormalizeTextForAnalysis(result),
            ApplyAllSuggestions(original, suggestions));
    }

    [Fact]
    public void ComputeProofreadSuggestions_InsertedWord_AnchorsOnTheFollowingWordNotAZeroWidthSpan()
    {
        // be-c01: "abc def" -> "abc xyz def" emitted [4,4) "" => "xyz ", an un-appliable suggestion.
        // The correct output anchors on the whole following word so the client can find it.
        const string original = "abc def";
        const string result = "abc xyz def";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var s = Assert.Single(suggestions);
        Assert.Equal("def", s.OriginalText);
        Assert.Equal("xyz def", s.SuggestedText);
        Assert.Equal(4, s.StartOffset);
        Assert.Equal(7, s.EndOffset);
        AssertNoDegenerateSuggestions(suggestions);
        Assert.Equal(
            TextNormalization.NormalizeTextForAnalysis(result),
            ApplyAllSuggestions(original, suggestions));
    }

    [Fact]
    public void ComputeProofreadSuggestions_LiveHebrewProofreadFixture_SuggestionSetReconstructsResult()
    {
        // The full live case this fix came from: a planted-error Hebrew proofread fixture and the
        // model's actual returned text. Every correction the model made must be individually
        // applicable, because the editor applies suggestions one at a time. Before the fix this set
        // contained "היא"→"י", "ידעה"→"דעה" and a four-way fragmentation of the "[לבדוק את התאריך]"
        // removal, none of which reconstruct the corrected chapter.
        const string original =
            "הבוקר בקיסריה התחיל באפור. נעמי הסתלכה מבעד לחלון וראתה את הים נבלע בערפל. היא היא ידעה שהיום הזה יהיה ארוך\n" +
            "על השולחן חיכה עתון ישן ,ולידו כוס תה שהתקררה. סבסטיאן השאיר פתק: \"אני חוזר בשמונה\". נעמי קרא אותו פעמיים ולא הבינה מדוע דווקא בשמונה..\n" +
            "היא נכנסה החדר הפנימי, פתחה את המגירה ומצאה שם צרור מכתבים. TODO: להוסיף כאן תיאור של המגירה. המכתב העליון היה מ־1998, והדיו כבר דהה. היא הפכה את המעטפות אחת אחת, ומצאה בין השורות שם שלא ראתה שנים רבות. “לא ייתכן שהוא שמר את זה כל השנים,” לחשה לעצמה.\n" +
            "בחוץ נשמעו צעדים. לרגע חשבה שהיא לבדה בבית. נעמי הסתובבה מהר מדי והפילה את הכוס. הכוס נשברה לרסיסים!! היא רכנה לאסוף את השברים, וכשהיאא הרימה את הראש עמד סבסטין בפתח.\n" +
            "\"מה את עושה כאן?\" שאל בשקט.\n" +
            "'חיפשתי את המפתחות,' ענתה.\n" +
            "הם ישבו בבית-הקפה שליד מגדל השעון, אותו בית–הקפה שבו נפגשו לראשונה לפני שלוש שנים. המלצרית הביאה שתי כוסות מים בלי לשאול. נעמי הביטה החוצה, אל הרחוב הרטוב, וחשבה על כל מה שלא נאמר. נעמי הביטה החוצה, אל הרחוב הרטוב, וחשבה על כל מה שלא נאמר.\n" +
            "\"תגידי,\" אמר לבסוף, \"את עדיין כועסת ?\"\n" +
            "היא לא ענתה. הרוח נשבה מן   הים והזיזה את המפית מן השולחן. בקסריה, חשבה, אפילו הרוח יודעת לשתוק.\n" +
            "היא לא הדליקה את האור. החדר היה חשוך ורק פס אור אחד חדר מבעד לתרי\n" +
            "בערב חזרה הביתה לבדה. היא הדליקה את המנורה, התיישבה ליד השולחן והת- חילה לכתוב [לבדוק את התאריך]. המילים באו לאט,, אחת אחת, כאילו כל אחת מהן שקלה מליון טון…. בסוף העמוד כתבה משפט אחד בלבד: \"אני עוד לא יודעת אם אני סולחת לך\" — ואז כיבתה את האור והלכהאל המיטה.";

        const string result =
            "הבוקר בקיסריה התחיל באפור. נעמי הסתלכה מבעד לחלון וראתה את הים נבלע בערפל. היא ידעה שהיום הזה יהיה ארוך.\n" +
            "על השולחן חיכה עיתון ישן, ולידו כוס תה שהתקררה. סבסטיאן השאיר פתק: \"אני חוזר בשמונה\". נעמי קרא אותו פעמיים ולא הבינה מדוע דווקא בשמונה...\n" +
            "היא נכנסה לחדר הפנימי, פתחה את המגירה ומצאה שם צרור מכתבים. TODO: להוסיף כאן תיאור של המגירה. המכתב העליון היה מ-1998, והדיו כבר דהה. היא הפכה את המעטפות אחת אחת, ומצאה בין השורות שם שלא ראתה שנים רבות. \"לא ייתכן שהוא שמר את זה כל השנים,\" לחשה לעצמה.\n" +
            "בחוץ נשמעו צעדים. לרגע חשבה שהיא לבדה בבית. נעמי הסתובבה מהר מדי והפילה את הכוס. הכוס נשברה לרסיסים! היא רכנה לאסוף את השברים, וכשהיא הרימה את הראש עמד סבסטיאן בפתח.\n" +
            "\"מה את עושה כאן?\" שאל בשקט.\n" +
            "\"חיפשתי את המפתחות,\" ענתה.\n" +
            "הם ישבו בבית-הקפה שליד מגדל השעון, אותו בית-הקפה שבו נפגשו לראשונה לפני שלוש שנים. המלצרית הביאה שתי כוסות מים בלי לשאול. נעמי הביטה החוצה, אל הרחוב הרטוב, וחשבה על כל מה שלא נאמר. נעמי הביטה החוצה, אל הרחוב הרטוב, וחשבה על כל מה שלא נאמר.\n" +
            "\"תגידי,\" אמר לבסוף, \"את עדיין כועסת?\"\n" +
            "היא לא ענתה. הרוח נשבה מן הים והזיזה את המפית מן השולחן. בקיסריה, חשבה, אפילו הרוח יודעת לשתוק.\n" +
            "היא לא הדליקה את האור. החדר היה חשוך ורק פס אור אחד חדר מבעד לתרי.\n" +
            "בערב חזרה הביתה לבדה. היא הדליקה את המנורה, התיישבה ליד השולחן והתחילה לכתוב. המילים באו לאט, אחת אחת, כאילו כל אחת מהן שקלה מליון טון... בסוף העמוד כתבה משפט אחד בלבד: \"אני עוד לא יודעת אם אני סולחת לך\" – ואז כיבתה את האור והלכה אל המיטה.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        Assert.NotEmpty(suggestions);

        // Offsets and text agree, so the editor's relocate-by-OriginalText apply path is sound.
        var normOrig = TextNormalization.NormalizeTextForAnalysis(original);
        Assert.All(suggestions, s =>
            Assert.Equal(normOrig[s.StartOffset!.Value..s.EndOffset!.Value], s.OriginalText));

        // Every correction must also be APPLICABLE, not just reconstructible (see the helper's note).
        AssertNoDegenerateSuggestions(suggestions);

        Assert.Equal(
            TextNormalization.NormalizeTextForAnalysis(result),
            ApplyAllSuggestions(original, suggestions));
    }

    /// <summary>
    /// Inputs that stress the merge step from every direction the be-c03 investigation probed:
    /// repeated tokens, independent edits close enough to merge across the 1-character gap, edits
    /// pinned to the very first/last character (including leading/trailing whitespace), a diff block
    /// straddling what would otherwise be a merged boundary, insertions, whitespace-run collapse and
    /// expansion, whole-clause deletion, and punctuation-adjacent insertion.
    /// </summary>
    private static readonly (string Original, string Result)[] MergeBoundaryCorpus =
    {
        // repeated tokens - the shape that made DiffPlex's minimal diff cross word boundaries
        ("היא היא ידעה שהיום הזה יהיה ארוך.", "היא ידעה שהיום הזה יהיה ארוך."),
        ("the the cat sat", "the cat sat"),
        ("aa aa aa bb", "aa aa bb"),
        ("abab abab", "abab"),
        // adjacent independent edits, within MergeGapThreshold of each other
        ("ab cd", "xb yd"),
        ("a b c", "x y z"),
        ("aa bb cc", "az bz cz"),
        ("one two three", "onx twx thrxe"),
        // edits pinned to the string boundaries
        ("abc def", "xbc def"),
        ("abc def", "abc dex"),
        ("abc def", "xabc defy"),
        (" abc", "x abc"),
        ("abc ", "abc x"),
        ("  abc  ", "  xabc  "),
        ("a", "b"),
        ("a b", "b a"),
        // a diff block straddling a would-be merged boundary (the space between two words)
        ("aaa bbb ccc", "aaabbb ccc"),
        ("aaa bbb ccc", "aaa bbbccc"),
        ("ל הראות", "להראות"),
        ("שירה התחילה ל הראות סימני לחץ.", "שירה התחילה להראות סימני לחץ."),
        // insertions
        ("abc def", "abc xyz def"),
        ("hello world", "hello brave world"),
        ("שלום עולם", "שלום גדול עולם"),
        // whitespace runs
        ("מן   הים", "מן הים"),
        ("a   b", "a b"),
        ("a b", "a   b"),
        // whole-clause deletion
        ("התיישבה ליד השולחן והתחילה לכתוב [לבדוק את התאריך]. המילים באו לאט.",
         "התיישבה ליד השולחן והתחילה לכתוב. המילים באו לאט."),
        ("first. second sentence should be removed. third.", "first. third."),
        // punctuation-adjacent insertion
        ("בשמונה..", "בשמונה..."),
        ("hi there", "hi, there"),
        ("ok", "ok."),
    };

    /// <summary>
    /// Independent forward-walk alignment oracle over the diff blocks, deliberately built the OTHER
    /// way round from <c>OrigToResultPos</c> (which accumulates a per-position delta): walk A and B in
    /// lockstep, mapping each unchanged run 1:1 and each block's deleted range onto its inserted range.
    ///
    /// <c>lo[i]</c> is the result position for original position i EXCLUDING a pure insertion sitting
    /// exactly at i; <c>hi[i]</c> INCLUDES it (the two answers <c>includeInsertionAtPos</c> chooses
    /// between). <c>-1</c> marks a position STRICTLY inside a deleted range, where no exact answer
    /// exists and <c>OrigToResultPos</c> has to interpolate.
    /// </summary>
    private static (int[] Lo, int[] Hi) BuildAlignmentOracle(int origLength, IList<DiffPlex.Model.DiffBlock> blocks)
    {
        var lo = new int[origLength + 1];
        var hi = new int[origLength + 1];
        var pinned = new bool[origLength + 1]; // finalized at a block edge - a later run must not clobber it

        var a = 0;
        var b = 0;
        foreach (var blk in blocks)
        {
            if (blk.DeleteCountA == 0 && blk.InsertCountB == 0) continue;

            // Guard the oracle's own assumption about DiffPlex: unchanged runs are equal-length in A and B.
            Assert.Equal(b + (blk.DeleteStartA - a), blk.InsertStartB);

            // Unchanged run [a, DeleteStartA] maps 1:1.
            for (var i = a; i <= blk.DeleteStartA; i++)
            {
                if (pinned[i]) continue;
                lo[i] = b + (i - a);
                hi[i] = lo[i];
            }

            var blockStartB = blk.InsertStartB;

            // At the block's start: lo excludes an insertion attached here, hi includes it. A block that
            // also DELETES owns its insert outright, so both answers coincide there.
            lo[blk.DeleteStartA] = blockStartB;
            hi[blk.DeleteStartA] = blk.DeleteCountA == 0 ? blockStartB + blk.InsertCountB : blockStartB;
            pinned[blk.DeleteStartA] = true;

            if (blk.DeleteCountA > 0)
            {
                // Strictly inside a deleted range there is no exact answer.
                for (var i = blk.DeleteStartA + 1; i < blk.DeleteStartA + blk.DeleteCountA; i++)
                {
                    lo[i] = -1;
                    hi[i] = -1;
                    pinned[i] = true;
                }

                var deleteEnd = blk.DeleteStartA + blk.DeleteCountA;
                lo[deleteEnd] = blockStartB + blk.InsertCountB;
                hi[deleteEnd] = lo[deleteEnd];
                pinned[deleteEnd] = true;
            }

            a = blk.DeleteStartA + blk.DeleteCountA;
            b = blk.InsertStartB + blk.InsertCountB;
        }

        for (var i = a; i <= origLength; i++)
        {
            if (pinned[i]) continue;
            lo[i] = b + (i - a);
            hi[i] = lo[i];
        }

        return (lo, hi);
    }

    /// <summary>
    /// be-c03. `ComputeProofreadSuggestions` exempts WHOLE merged ranges from the two pathological
    /// guards (origLen > 40 && sugLen &lt;= 8, origLen > 25 && sugLen == 0) because a whole range's
    /// mapping into the result text is exact - so a giant-original/tiny-suggestion shape is a real
    /// large deletion there, not a misalignment to be suppressed. Nothing else verifies a whole range
    /// (SubRangesReproduceResult only decides SPLIT vs WHOLE), so if that exemption rested on a comment
    /// the safety net and the verification would not overlap at all. This test IS the verification.
    ///
    /// It asserts, over the corpus plus a seeded randomized sweep:
    ///   (a) no merged boundary falls STRICTLY inside a diff block's deleted range, and
    ///   (b) OrigToResultPos at those boundaries agrees with an independent alignment oracle,
    /// which together mean the whole-range mapping is exact. The investigation behind this (see the
    /// plan's `## Investigation findings`) found no counterexample in ~679k input pairs, including
    /// exhaustive sweeps over 3-4 character alphabets; this pins the property so a future change to
    /// the expansion or the merge cannot quietly invalidate the exemption.
    /// </summary>
    [Fact]
    public void MergedRangeBoundaries_NeverFallInsideADiffBlock()
    {
        // Coverage counters - a sweep that never builds the interesting shapes would pass vacuously.
        var boundariesChecked = 0;
        var boundaryOnBlockEdge = 0;
        var gapMerges = 0;
        var pureInsertionAtRangeStart = 0;
        var pureInsertionAtRangeEnd = 0;
        var blockStrictlyInsideRange = 0;

        void Check(string original, string result)
        {
            if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(result)) return;

            var normOrig = TextNormalization.NormalizeTextForAnalysis(original);
            var normResult = TextNormalization.NormalizeTextForAnalysis(result);

            var diff = new Differ().CreateCharacterDiffs(normOrig, normResult, ignoreCase: false, ignoreWhitespace: false);
            if (diff.DiffBlocks.Count == 0) return;

            var merged = SuggestionDiffService.BuildMergedWordRanges(normOrig, diff.DiffBlocks);
            if (merged.Count == 0) return;

            var (lo, hi) = BuildAlignmentOracle(normOrig.Length, diff.DiffBlocks);

            // Ranges must come out sorted and separated by MORE than the merge gap - anything closer
            // should have been merged, so seeing it would mean the merge step did not run to fixpoint.
            for (var i = 1; i < merged.Count; i++)
            {
                Assert.True(
                    merged[i].Start > merged[i - 1].End + 1,
                    $"merged ranges overlap, touch, or sit within the merge gap: {merged[i - 1]} then " +
                    $"{merged[i]} (orig='{Escape(normOrig)}' result='{Escape(normResult)}')");
            }

            foreach (var (mStart, mEnd) in merged)
            {
                Assert.True(0 <= mStart && mStart < mEnd && mEnd <= normOrig.Length,
                    $"merged range [{mStart},{mEnd}) out of range for '{Escape(normOrig)}'");

                // Did this range actually absorb SEVERAL diff blocks separated by unchanged text? That
                // is the "adjacent independent edits merged across the gap" shape the invariant is
                // riskiest for, so the sweep has to keep producing it.
                var blocksInRange = diff.DiffBlocks
                    .Where(k => (k.DeleteCountA > 0 || k.InsertCountB > 0)
                                && k.DeleteStartA >= mStart && k.DeleteStartA + k.DeleteCountA <= mEnd)
                    .OrderBy(k => k.DeleteStartA)
                    .ToList();
                for (var i = 1; i < blocksInRange.Count; i++)
                {
                    if (blocksInRange[i].DeleteStartA
                        > blocksInRange[i - 1].DeleteStartA + blocksInRange[i - 1].DeleteCountA)
                    {
                        gapMerges++;
                        break;
                    }
                }

                foreach (var blk in diff.DiffBlocks)
                {
                    if (blk.DeleteCountA == 0 && blk.InsertCountB == 0) continue;
                    var x = blk.DeleteStartA;
                    var y = x + blk.DeleteCountA;

                    if (x == mStart || y == mStart || x == mEnd || y == mEnd) boundaryOnBlockEdge++;
                    if (blk.DeleteCountA == 0 && x == mStart) pureInsertionAtRangeStart++;
                    if (blk.DeleteCountA == 0 && x == mEnd) pureInsertionAtRangeEnd++;
                    if (blk.DeleteCountA > 0 && mStart < x && y < mEnd) blockStrictlyInsideRange++;

                    // (a) THE INVARIANT the guard exemption rests on.
                    Assert.False(x < mStart && mStart < y,
                        $"merged range START {mStart} falls INSIDE diff block [{x},{y}) - " +
                        $"OrigToResultPos must interpolate, so the whole-range mapping is NOT exact " +
                        $"and the pathological-guard exemption is unsafe. orig='{Escape(normOrig)}' result='{Escape(normResult)}'");
                    Assert.False(x < mEnd && mEnd < y,
                        $"merged range END {mEnd} falls INSIDE diff block [{x},{y}) - " +
                        $"OrigToResultPos must interpolate, so the whole-range mapping is NOT exact " +
                        $"and the pathological-guard exemption is unsafe. orig='{Escape(normOrig)}' result='{Escape(normResult)}'");
                }

                // (b) and therefore the production mapping matches the independent oracle exactly.
                Assert.True(lo[mStart] >= 0 && hi[mEnd] >= 0,
                    $"oracle has no exact mapping for [{mStart},{mEnd}) - boundary inside a deleted range");
                var mappedStart = SuggestionDiffService.OrigToResultPos(mStart, diff.DiffBlocks);
                var mappedEnd = SuggestionDiffService.OrigToResultPos(mEnd, diff.DiffBlocks, includeInsertionAtPos: true);
                Assert.True(lo[mStart] == mappedStart,
                    $"range START {mStart} of [{mStart},{mEnd}): oracle says {lo[mStart]}, OrigToResultPos says " +
                    $"{mappedStart}. orig='{Escape(normOrig)}' result='{Escape(normResult)}' blocks=[" +
                    string.Join(",", diff.DiffBlocks.Select(k => $"(dA={k.DeleteStartA},dC={k.DeleteCountA},iB={k.InsertStartB},iC={k.InsertCountB})")) + "]");
                Assert.True(hi[mEnd] == mappedEnd,
                    $"range END {mEnd} of [{mStart},{mEnd}): oracle says {hi[mEnd]}, OrigToResultPos says " +
                    $"{mappedEnd}. orig='{Escape(normOrig)}' result='{Escape(normResult)}' blocks=[" +
                    string.Join(",", diff.DiffBlocks.Select(k => $"(dA={k.DeleteStartA},dC={k.DeleteCountA},iB={k.InsertStartB},iC={k.InsertCountB})")) + "]");
                Assert.True(hi[mEnd] <= normResult.Length,
                    $"mapped end {hi[mEnd]} past result length {normResult.Length}");
                boundariesChecked += 2;
            }
        }

        foreach (var (original, result) in MergeBoundaryCorpus)
            Check(original, result);

        // Randomized sweep. Fixed seed so a failure is reproducible; a mix of independently random
        // pairs (adversarial, dense diffs) and small mutations (the realistic proofread shape).
        var rnd = new Random(20260801);
        const string alphabet = "aab c.,\nב";
        for (var i = 0; i < 20_000; i++)
        {
            var original = RandomText(rnd, alphabet, 1 + rnd.Next(14));
            var result = i % 2 == 0
                ? RandomText(rnd, alphabet, 1 + rnd.Next(14))
                : MutateText(rnd, original, alphabet);
            Check(original, result);
        }

        // Non-vacuity: the sweep must actually have produced the adversarial configurations.
        Assert.True(boundariesChecked > 10_000, $"only {boundariesChecked} boundaries checked");
        Assert.True(boundaryOnBlockEdge > 1_000, $"only {boundaryOnBlockEdge} boundary/block-edge coincidences");
        Assert.True(gapMerges > 100, $"only {gapMerges} ranges absorbed several gap-separated diff blocks");
        Assert.True(pureInsertionAtRangeStart > 100, $"only {pureInsertionAtRangeStart} pure insertions at a range start");
        Assert.True(pureInsertionAtRangeEnd > 100, $"only {pureInsertionAtRangeEnd} pure insertions at a range end");
        Assert.True(blockStrictlyInsideRange > 100, $"only {blockStrictlyInsideRange} blocks strictly inside a range");
    }

    private static string Escape(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r");

    private static string RandomText(Random rnd, string alphabet, int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
        return sb.ToString();
    }

    private static string MutateText(Random rnd, string text, string alphabet)
    {
        var sb = new StringBuilder(text);
        var edits = 1 + rnd.Next(3);
        for (var i = 0; i < edits; i++)
        {
            if (sb.Length == 0) break;
            var op = rnd.Next(3);
            var pos = rnd.Next(sb.Length);
            if (op == 0) sb.Insert(pos, alphabet[rnd.Next(alphabet.Length)]);
            else if (op == 1) sb.Remove(pos, 1);
            else sb[pos] = alphabet[rnd.Next(alphabet.Length)];
        }
        return sb.ToString();
    }

    [Fact]
    public void ComputeProofreadSuggestions_NoChanges_ReturnsEmpty()
    {
        var text = "שלום עולם, זהו טקסט לבדיקה.";

        var result = _sut.ComputeProofreadSuggestions(text, text);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeProofreadSuggestions_SingleReplacement_ProducesOneSuggestionWithCorrectSpan()
    {
        const string original = "Hello world, this is a test.";
        const string result = "Hello friend, this is a test.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);

        Assert.True(suggestion.StartOffset >= 0);
        Assert.True(suggestion.EndOffset > suggestion.StartOffset);

        var span = TextNormalization.NormalizeTextForAnalysis(original)[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value];
        Assert.Equal(suggestion.OriginalText, span);

        Assert.Equal("world", suggestion.OriginalText.TrimEnd(',', ' '));
        Assert.Equal("friend", suggestion.SuggestedText.TrimEnd(',', ' '));
    }

    [Fact]
    public void ComputeProofreadSuggestions_PunctuationInclusiveSpan_IncludesPunctuationInOffsets()
    {
        const string original = "הוא אמר, ואז הפסיק.";
        const string result = "הוא אמר. ואז הפסיק.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);

        Assert.EndsWith(",", suggestion.OriginalText);
        Assert.EndsWith(".", suggestion.SuggestedText);
    }

    [Fact]
    public void ComputeProofreadSuggestions_DistantChanges_ProduceSeparateSuggestions()
    {
        const string original = "one two three four five six seven eight nine ten";
        const string result = "one TOO three four five six SEVEN eight nine ten";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        Assert.True(suggestions.Count >= 2);

        Assert.Contains(suggestions, s => s.OriginalText.Contains("two", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(suggestions, s => s.OriginalText.Contains("seven", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComputeProofreadSuggestions_DeletedText_ProducesSuggestionWithEmptySuggestedText()
    {
        const string original = "First sentence. Second sentence should be removed. Third sentence.";
        const string result = "First sentence. Third sentence.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        Assert.Contains(suggestions, s => string.IsNullOrEmpty(s.SuggestedText?.Trim()));

        // The old per-word split satisfied the assertion above while leaving the spaces BETWEEN the
        // deleted words behind ("First sentence.    Third sentence."), so also pin the reconstruction.
        Assert.Equal(
            TextNormalization.NormalizeTextForAnalysis(result),
            ApplyAllSuggestions(original, suggestions));
    }

    [Fact]
    public void ComputeProofreadSuggestions_BidiAndCrLfAreIgnoredForOffsets()
    {
        var originalRaw = "שלום\u200F עולם\r\nשורה שנייה";
        var resultRaw = "שלום\u200F טוב\r\nשורה שנייה.";

        var originalNorm = TextNormalization.NormalizeTextForAnalysis(originalRaw);
        TextNormalization.NormalizeTextForAnalysis(resultRaw);

        var suggestions = _sut.ComputeProofreadSuggestions(originalRaw, resultRaw);

        Assert.NotEmpty(suggestions);

        foreach (var s in suggestions)
        {
            Assert.InRange(s.StartOffset!.Value, 0, originalNorm.Length);
            Assert.InRange(s.EndOffset!.Value, s.StartOffset.Value, originalNorm.Length);
        }
    }

    [Fact]
    public void ComputeLineEditSuggestions_UniqueMatch_MapsToCorrectOffsets()
    {
        const string doc = "זהו משפט אחד. זהו משפט שני.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "משפט שני", Suggested = "משפט שני משופר", Reason = "clarity", Category = "clarity" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        var suggestion = Assert.Single(suggestions);

        Assert.Equal("משפט שני", suggestion.OriginalText);
        Assert.Contains("משפט שני משופר", suggestion.SuggestedText);

        var normDoc = TextNormalization.NormalizeTextForAnalysis(doc);
        var slice = normDoc[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value];
        Assert.Equal("משפט שני", slice);
    }

    [Fact]
    public void ComputeLineEditSuggestions_NotFound_SkipsSuggestion()
    {
        const string doc = "זהו משפט אחד בלבד.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "טקסט שלא קיים", Suggested = "טקסט חדש", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeLineEditSuggestions_MultipleOccurrences_UsesSearchStartOffsetToAdvance()
    {
        const string doc = "משפט חוזר. משפט חוזר. משפט חוזר.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "משפט חוזר", Suggested = "ראשון", Reason = "style", Category = "style" },
                new() { Original = "משפט חוזר", Suggested = "שני", Reason = "style", Category = "style" },
                new() { Original = "משפט חוזר", Suggested = "שלישי", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        Assert.Equal(3, suggestions.Count);

        var starts = suggestions.Select(s => s.StartOffset).ToArray();
        Assert.True(starts[0] < starts[1] && starts[1] < starts[2]);
    }

    [Fact]
    public void ComputeLineEditSuggestions_PreservesCategory_ForConsistencyAndContinuity()
    {
        const string doc = "First sentence. Second sentence. Third sentence.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new()
                {
                    Original = "Second sentence.",
                    Suggested = "Second sentence, improved.",
                    Reason = "consistency",
                    Category = "consistency"
                },
                new()
                {
                    Original = "Third sentence.",
                    Suggested = "Third sentence, adjusted.",
                    Reason = "continuity",
                    Category = "continuity"
                }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        Assert.Equal(2, suggestions.Count);
        Assert.Contains(suggestions, s => s.Category == "consistency");
        Assert.Contains(suggestions, s => s.Category == "continuity");
    }

    [Fact]
    public void ComputeProofreadSuggestions_SuggestionAtStartOfText_ContextBeforeIsEmpty()
    {
        const string original = "First word here and more text.";
        const string result = "Second word here and more text.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("", suggestion.ContextBefore);
        Assert.NotNull(suggestion.ContextAfter);
        Assert.True(suggestion.ContextAfter!.Length > 0);
    }

    [Fact]
    public void ComputeProofreadSuggestions_SuggestionAtEndOfText_ContextAfterIsEmpty()
    {
        // No character after the changed word so ContextAfter is empty
        const string original = "Some text here and final";
        const string result = "Some text here and last";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);
        Assert.NotNull(suggestion.ContextBefore);
        Assert.True(suggestion.ContextBefore!.Length > 0);
        Assert.Equal("", suggestion.ContextAfter);
    }

    [Fact]
    public void ComputeProofreadSuggestions_MidDocumentSuggestion_BothContextsPopulatedMax50Chars()
    {
        // Long enough prefix/suffix so 50 chars exist on both sides of the changed word
        var prefix = new string('x', 60);
        var suffix = new string('y', 60);
        var original = prefix + " wrong " + suffix;
        var result = prefix + " right " + suffix;

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);
        Assert.NotNull(suggestion.ContextBefore);
        Assert.NotNull(suggestion.ContextAfter);
        Assert.True(suggestion.ContextBefore!.Length > 0);
        Assert.True(suggestion.ContextAfter!.Length > 0);
        Assert.True(suggestion.ContextBefore.Length <= 50, "ContextBefore should be at most 50 characters");
        Assert.True(suggestion.ContextAfter.Length <= 50, "ContextAfter should be at most 50 characters");
    }

    [Fact]
    public void ComputeProofreadSuggestions_HallucinatedRepetition_RejectsDisproportionateSuggestions()
    {
        const string original = "אחד שניים שלושה ארבע חמש שש שבע שמונה תשע עשר";
        var repeated = string.Concat(Enumerable.Repeat("שלושה ארבע חמש שש שבע שמונה תשע עשר ", 20));
        var result = "אחד שניים " + repeated;

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        foreach (var s in suggestions)
        {
            Assert.True(
                s.SuggestedText.Length <= s.OriginalText.Length * 5 + 30,
                $"Suggestion for '{s.OriginalText}' has disproportionate suggestedText length {s.SuggestedText.Length}");
        }
    }

    [Fact]
    public void ComputeLineEditSuggestions_MidDocument_CapturesContextBeforeAndAfter()
    {
        var prefix = new string('a', 55);
        var suffix = new string('b', 55);
        const string middle = "replace me";
        var doc = prefix + " " + middle + " " + suffix;

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = middle, Suggested = "replaced", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        var suggestion = Assert.Single(suggestions);
        Assert.NotNull(suggestion.ContextBefore);
        Assert.NotNull(suggestion.ContextAfter);
        Assert.True(suggestion.ContextBefore!.Length > 0);
        Assert.True(suggestion.ContextAfter!.Length > 0);
        Assert.True(suggestion.ContextBefore.Length <= 50, "ContextBefore should be at most 50 characters");
        Assert.True(suggestion.ContextAfter.Length <= 50, "ContextAfter should be at most 50 characters");
    }

    // -----------------------------------------------------------------------
    // ComputeConsistencyIssueSuggestions tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeConsistencyIssueSuggestions_VerbatimSpan_CorrectOffsetsInNormalizedSpace()
    {
        // The span is verbatim text that exists in the document.
        // Expected offsets must match where the span sits in the normalized document.
        const string inputText = "He walked slowly down the street. He ran fast.";
        const string spanRaw = "walked slowly";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var normSpan = TextNormalization.NormalizeTextForAnalysis(spanRaw);
        var expectedStart = normDoc.IndexOf(normSpan, StringComparison.Ordinal);
        var expectedEnd = expectedStart + normSpan.Length;

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = spanRaw, Description = "tense shift" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedEnd, suggestion.EndOffset);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_BidiSpan_MatchesAfterNormalization()
    {
        // Span contains an LRM bidi mark (‎) that the document does not have.
        // After normalization both are stripped, so the span is still found.
        const string inputText = "הוא הלך לאט במסדרון. הוא רץ מהר.";
        // Span has a trailing LRM bidi mark that the document text does not contain.
        var spanRaw = "הלך לאט‎";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = spanRaw, Description = "register shift" }
        };

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var normSpan = TextNormalization.NormalizeTextForAnalysis(spanRaw);
        var expectedStart = normDoc.IndexOf(normSpan, StringComparison.Ordinal);

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.NotNull(suggestion.StartOffset);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + normSpan.Length, suggestion.EndOffset);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_RepeatedPhrase_DistinctNonOverlappingOffsets()
    {
        // The same phrase appears twice; the second issue must map to the second occurrence.
        const string inputText = "She smiled. Then she smiled again.";
        const string spanRaw = "smiled";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "pov", Span = spanRaw, Description = "first occurrence" },
            new() { Type = "pov", Span = spanRaw, Description = "second occurrence" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(2, suggestions.Count);
        var first = suggestions[0];
        var second = suggestions[1];
        // Both must be matched (non-null offsets).
        Assert.NotNull(first.StartOffset);
        Assert.NotNull(second.StartOffset);
        // Second occurrence must start after first occurrence ends.
        Assert.True(second.StartOffset > first.StartOffset, "Second match must be past first match");
        Assert.True(second.StartOffset >= first.EndOffset, "Offsets must not overlap");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_IssuesOutOfDocumentOrder_AllMatchedNoneDropped()
    {
        // Issues arrive in SIGNIFICANCE order, not document order: issues[0]'s span sits LATER in the
        // document than issues[1]'s span. The old monotonic-searchStart code advanced the cursor past
        // issues[0]'s (later) match, so IndexOf for issues[1]'s (earlier) span returned -1 and the
        // issue was silently dropped. The fix locates each issue independently from offset 0, so both
        // are matched and issues[1] maps to its REAL earlier offset.
        const string inputText = "The tense slip is here near the start. Much later the POV shift appears.";
        const string laterSpan = "POV shift appears";   // sits later in the document
        const string earlierSpan = "tense slip is here"; // sits earlier in the document

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        // Sanity-check the fixture: the first-emitted span really is later than the second-emitted span.
        var laterIdx = normDoc.IndexOf(laterSpan, StringComparison.Ordinal);
        var earlierIdx = normDoc.IndexOf(earlierSpan, StringComparison.Ordinal);
        Assert.True(laterIdx > earlierIdx, "fixture: issues[0] span must sit later than issues[1] span");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "pov",   Span = laterSpan,   Description = "emitted first, located later" },
            new() { Type = "tense", Span = earlierSpan, Description = "emitted second, located earlier" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Both must survive (this is what fails against the old monotonic-cursor code).
        Assert.Equal(2, suggestions.Count);

        var firstEmitted = suggestions[0];  // the later span
        var secondEmitted = suggestions[1]; // the earlier span

        Assert.NotNull(firstEmitted.StartOffset);
        Assert.NotNull(firstEmitted.EndOffset);
        Assert.NotNull(secondEmitted.StartOffset);
        Assert.NotNull(secondEmitted.EndOffset);

        // Each suggestion must map to its real span position in normalized space.
        Assert.Equal(laterIdx, firstEmitted.StartOffset);
        Assert.Equal(laterIdx + laterSpan.Length, firstEmitted.EndOffset);
        Assert.Equal(earlierIdx, secondEmitted.StartOffset);
        Assert.Equal(earlierIdx + earlierSpan.Length, secondEmitted.EndOffset);

        // The second-emitted issue's REAL position is earlier than the first-emitted issue's.
        Assert.True(
            secondEmitted.StartOffset < firstEmitted.StartOffset,
            "issues[1] must map to its real earlier offset, before issues[0]'s offset");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_DistinctIssuesShareSingleOccurrenceSpan_BothAnchoredNotDropped()
    {
        // Two DIFFERENT issue types quote the SAME passage, which appears exactly once in the text.
        // The old overlap-dedup dropped the second issue (no other occurrence to claim); but distinct
        // issues legitimately share an anchor, so BOTH must survive and anchor to that one occurrence.
        const string inputText = "She walks to the door and opened it slowly, then she leaves the room.";
        const string sharedSpan = "She walks to the door and opened it slowly";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = sharedSpan, Description = "tense shift: walks vs opened" },
            new() { Type = "pov",   Span = sharedSpan, Description = "pov phrasing in the same passage" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Both issues survive (the bug surfaced only the first in significance order).
        Assert.Equal(2, suggestions.Count);
        Assert.Contains(suggestions, s => s.Category == "consistency-tense");
        Assert.Contains(suggestions, s => s.Category == "consistency-pov");

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var idx = normDoc.IndexOf(sharedSpan, StringComparison.Ordinal);
        Assert.True(idx >= 0, "fixture: shared span must be present once in the text");
        // Span occurs once, so both issues anchor to that single occurrence (overlap is allowed here).
        Assert.All(suggestions, s =>
        {
            Assert.Equal(idx, s.StartOffset);
            Assert.Equal(idx + sharedSpan.Length, s.EndOffset);
        });
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_DistinctIssuesOverlappingWindows_BothAnchored()
    {
        // Tense and POV issues quote OVERLAPPING (not identical) windows over the same sentence. The
        // second window's only occurrence overlaps the first's claimed range; it must still anchor to
        // its real position rather than being dropped.
        const string inputText = "He walks into the kitchen and she opened the window while they watch the rain.";
        const string tenseWindow = "He walks into the kitchen and she opened";       // earlier window
        const string povWindow   = "kitchen and she opened the window while they";   // overlaps tenseWindow

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var tenseIdx = normDoc.IndexOf(tenseWindow, StringComparison.Ordinal);
        var povIdx = normDoc.IndexOf(povWindow, StringComparison.Ordinal);
        Assert.True(tenseIdx >= 0 && povIdx >= 0, "fixture: both windows present");
        // Sanity-check the fixture: the two windows really overlap in text.
        Assert.True(povIdx < tenseIdx + tenseWindow.Length && tenseIdx < povIdx + povWindow.Length,
            "fixture: windows must overlap so the dedup path is exercised");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = tenseWindow, Description = "tense shift" },
            new() { Type = "pov",   Span = povWindow,   Description = "pov shift" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(2, suggestions.Count);
        var tense = Assert.Single(suggestions, s => s.Category == "consistency-tense");
        var pov = Assert.Single(suggestions, s => s.Category == "consistency-pov");
        // Each maps to its own window's real position even though they overlap.
        Assert.Equal(tenseIdx, tense.StartOffset);
        Assert.Equal(povIdx, pov.StartOffset);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_UnmatchedSpan_IsDropped()
    {
        // Span is not present in the analyzed text at all. It is not navigable in this unit, so it
        // must be dropped entirely (no null-offset fallback item).
        const string inputText = "A completely different sentence.";
        const string spanRaw = "text not found anywhere";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = spanRaw, Description = "unmatched issue" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Dropped - nothing surfaced for an all-unmatched input.
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_SpanFromContextNotInTarget_IsDropped()
    {
        // Mimics the cross-chapter bug: the model detected a register/POV shift relative to the
        // surrounding chapters and quoted a sentence from that NEIGHBORING context. The span is a
        // verbatim sentence that exists only in some OTHER text, NOT in the analyzed target chapter.
        // Because it cannot be located in the analyzed unit, it is not navigable and must be dropped.
        const string targetChapter =
            "The morning was quiet. Sarah opened the shutters and let the light spill across the floor.";
        // This sentence exists only in the PRECEDING_CONTEXT (the previous chapter), never in the target.
        const string spanFromOtherChapter =
            "Down in the harbor the old fishermen mended their nets before dawn";

        // Sanity: the span really is absent from the analyzed text.
        Assert.DoesNotContain(spanFromOtherChapter, targetChapter, StringComparison.Ordinal);

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = spanFromOtherChapter, Description = "register shift vs previous chapter" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, targetChapter);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_CategoryMapping_RegisterTensePov()
    {
        // Each issue type must produce the correct "consistency-{type}" category.
        const string inputText = "He runs. She walked. They think.";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = "He runs",   Description = "register" },
            new() { Type = "tense",    Span = "She walked", Description = "tense"    },
            new() { Type = "pov",      Span = "They think", Description = "pov"      }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(3, suggestions.Count);
        Assert.Contains(suggestions, s => s.Category == "consistency-register");
        Assert.Contains(suggestions, s => s.Category == "consistency-tense");
        Assert.Contains(suggestions, s => s.Category == "consistency-pov");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_CategoryMapping_NormalizesTypeCase()
    {
        // Mixed-case and whitespace-padded Type values must be trimmed + lowercased in the category.
        const string inputText = "He runs. She walked. They think.";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = " Register ", Span = "He runs",   Description = "register padded" },
            new() { Type = "Tense",      Span = "She walked", Description = "tense mixed"    },
            new() { Type = "POV",        Span = "They think", Description = "pov upper"      }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(3, suggestions.Count);
        Assert.Contains(suggestions, s => s.Category == "consistency-register");
        Assert.Contains(suggestions, s => s.Category == "consistency-tense");
        Assert.Contains(suggestions, s => s.Category == "consistency-pov");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_SuggestedTextIsAlwaysEmpty()
    {
        // SuggestedText must be "" for every produced suggestion (navigate-only v1).
        // Both spans are present in the analyzed text so both are surfaced (matched spans only).
        const string inputText = "He walked. She ran.";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense",    Span = "walked", Description = "tense shift" },
            new() { Type = "register", Span = "ran",    Description = "register shift" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(2, suggestions.Count);
        foreach (var s in suggestions)
        {
            Assert.Equal(string.Empty, s.SuggestedText);
        }
    }

    // -----------------------------------------------------------------------
    // Near-match anchoring fallback tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeConsistencyIssueSuggestions_ExactSpan_AnchorsAtRightOffset_OriginalTextUnchanged()
    {
        // Regression: an exactly-quoted span still anchors at its real offset and keeps the model's
        // verbatim text - the near-match fallback must not interfere with the exact-match path.
        const string inputText = "Now the highway stretches ahead of us and the fields slide past the window.";
        const string spanRaw = "the highway stretches ahead of us";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var expectedStart = normDoc.IndexOf(spanRaw, StringComparison.Ordinal);
        Assert.True(expectedStart >= 0, "fixture: span must be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = spanRaw, Description = "tense shift" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + spanRaw.Length, suggestion.EndOffset);
        // OriginalText is the model's verbatim span (unchanged) on the exact path.
        Assert.Equal(spanRaw, suggestion.OriginalText);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_HebrewMorphologicalNearMiss_AnchorsToRealDocumentText()
    {
        // The measured leak: the document has the present-tense "ועוצר" but the model mis-quoted it as
        // the past-tense "ועצר" (one character shorter). The exact-substring anchor failed and the tense
        // card was dropped. The near-match fallback must anchor to the REAL document text "ועוצר".
        const string inputText = "הוא רץ אל הדלת ועוצר ליד הסף, מביט החוצה אל הרחוב השקט.";
        const string misquotedSpan = "הוא רץ אל הדלת ועצר ליד הסף"; // "ועצר" (past) vs document "ועוצר" (present)

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var correctSpan = "הוא רץ אל הדלת ועוצר ליד הסף";
        var expectedStart = normDoc.IndexOf(correctSpan, StringComparison.Ordinal);
        Assert.True(expectedStart >= 0, "fixture: real document text must be present");
        Assert.True(normDoc.IndexOf(misquotedSpan, StringComparison.Ordinal) < 0,
            "fixture: the mis-quoted span must NOT be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "present-tense slip" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + correctSpan.Length, suggestion.EndOffset);
        // OriginalText is the REAL document text, not the model's mis-quote.
        Assert.Equal(correctSpan, suggestion.OriginalText);
        // Offsets are in normalized space and select exactly the anchored text.
        Assert.Equal(correctSpan, normDoc[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value]);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_EnglishOneCharNearMiss_AnchorsToRealDocumentText()
    {
        // English analog of the Hebrew morphological slip: the document has "stretches" but the model
        // quoted "streches" (one letter dropped). The near-match fallback anchors to the real text.
        const string inputText = "Now the highway stretches ahead of us and the fields slide past the window.";
        const string misquotedSpan = "the highway streches ahead of us"; // "streches" vs document "stretches"

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        const string correctSpan = "the highway stretches ahead of us";
        var expectedStart = normDoc.IndexOf(correctSpan, StringComparison.Ordinal);
        Assert.True(expectedStart >= 0, "fixture: real document text must be present");
        Assert.True(normDoc.IndexOf(misquotedSpan, StringComparison.Ordinal) < 0,
            "fixture: the mis-quoted span must NOT be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "tense slip" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + correctSpan.Length, suggestion.EndOffset);
        Assert.Equal(correctSpan, suggestion.OriginalText);
        Assert.Equal(correctSpan, normDoc[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value]);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_AmbiguousNearMatch_IsDropped()
    {
        // Two equally-good near-match windows exist (the span is one edit away from BOTH), so the match
        // is ambiguous. The uniqueness guard must DROP the issue rather than guess which window to anchor.
        // Document contains both "the red gate stood open wide" and "the bed gate stood open wide";
        // the span "the led gate stood open wide" is exactly one substitution from each.
        const string inputText =
            "Near the orchard the red gate stood open wide. Far across the yard the bed gate stood open wide.";
        const string ambiguousSpan = "the led gate stood open wide";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(ambiguousSpan, StringComparison.Ordinal) < 0,
            "fixture: span must not be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = ambiguousSpan, Description = "ambiguous near-match" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Ambiguous -> dropped, no false anchor.
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_GenuinelyAbsentSpan_IsStillDropped()
    {
        // A span quoted from OUTSIDE the analyzed unit (e.g. from preceding/following context) is not a
        // near-miss of anything in the body. The tight threshold + uniqueness guard must keep dropping it.
        const string targetChapter =
            "The morning was quiet. Sarah opened the shutters and let the light spill across the floor.";
        const string spanFromElsewhere =
            "Down in the harbor the old fishermen mended their nets before dawn";

        Assert.DoesNotContain(spanFromElsewhere, targetChapter, StringComparison.Ordinal);

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = spanFromElsewhere, Description = "absent span" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, targetChapter);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_DeletionNearMiss_SnapsToWholeWordsNotMidWord()
    {
        // The exact live leak: the model dropped a letter in its span ("ועצר" for the document's
        // "ועוצר", a DELETION). Because the near-match fallback uses a FIXED-LENGTH window and the
        // mis-quote is one char shorter than the real text, the window anchored one char late and shaved
        // the leading "ה" - the card highlighted "וא מגיע ... פעמיים" instead of "הוא מגיע ... פעמיים".
        // The word-boundary snap must restore the full leading word "הוא" and end on a whole word.
        const string inputText =
            "הוא מגיע אל קצה החומה ועוצר. המגדלור מהבהב פעם, פעמיים, והמים שוטפים את הסלעים.";
        // Span has "ועצר" (dropped vav) - a 1-char deletion vs the document's "ועוצר".
        const string misquotedSpan = "הוא מגיע אל קצה החומה ועצר. המגדלור מהבהב פעם, פעמיים";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(misquotedSpan, StringComparison.Ordinal) < 0,
            "fixture: the mis-quoted span must NOT be an exact substring");
        var hePos = normDoc.IndexOf("הוא", StringComparison.Ordinal);
        Assert.True(hePos >= 0, "fixture: 'הוא' must be present in the document");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "tense slip (dropped vav)" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);

        // The highlight starts on the WHOLE word "הוא" (not the shaved "וא").
        Assert.StartsWith("הוא", suggestion.OriginalText);

        // StartOffset points exactly at the "ה" of "הוא" in the normalized text (the leading char was
        // restored, not shaved).
        Assert.Equal(hePos, suggestion.StartOffset);

        var startOffset = suggestion.StartOffset!.Value;
        var endOffset = suggestion.EndOffset!.Value;

        // The span ends on a whole word: end is at string end or sits on a word boundary (the next char
        // is whitespace or punctuation, never another word's letter). Here it ends on "פעמיים" with a
        // following comma.
        Assert.True(endOffset == normDoc.Length
                    || char.IsWhiteSpace(normDoc[endOffset])
                    || char.IsPunctuation(normDoc[endOffset]),
            "anchored span must end on a whole-word boundary");
        Assert.EndsWith("פעמיים", suggestion.OriginalText.TrimEnd());

        // The anchored OriginalText is exactly the normalized document slice it points to.
        Assert.Equal(normDoc[startOffset..endOffset], suggestion.OriginalText);

        // Start is itself a whole-word boundary (start of text or preceded by whitespace/punctuation).
        Assert.True(startOffset == 0
                    || char.IsWhiteSpace(normDoc[startOffset - 1])
                    || char.IsPunctuation(normDoc[startOffset - 1]),
            "anchored span must start on a whole-word boundary");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_EnglishDeletionNearMiss_SnapsToWholeWords()
    {
        // English analog of the deletion leak: the document reads "stops" but the model quoted "stps"
        // (a 1-char deletion). A fixed-length window would clip a neighbouring word; the word-boundary
        // snap must keep both ends on whole words (no mid-word start/end).
        const string inputText = "She walks to the shore and he stops there quietly before the tide turns.";
        const string misquotedSpan = "he stps there quietly"; // "stps" vs document "stops"

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(misquotedSpan, StringComparison.Ordinal) < 0,
            "fixture: the mis-quoted span must NOT be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "tense slip (dropped letter)" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);

        var startOffset = suggestion.StartOffset!.Value;
        var endOffset = suggestion.EndOffset!.Value;

        // Neither end is mid-word: start is at index 0 or preceded by whitespace; end is at the input
        // end or followed by whitespace.
        Assert.True(startOffset == 0 || char.IsWhiteSpace(normDoc[startOffset - 1]),
            "anchored span must start on a whole-word boundary");
        Assert.True(endOffset == normDoc.Length || char.IsWhiteSpace(normDoc[endOffset]),
            "anchored span must end on a whole-word boundary");

        // Anchors to the real document words "stops" / "quietly" (whole, not clipped).
        Assert.Contains("stops", suggestion.OriginalText);
        Assert.StartsWith("he ", suggestion.OriginalText);
        Assert.EndsWith("quietly", suggestion.OriginalText.TrimEnd());
        Assert.Equal(normDoc[startOffset..endOffset], suggestion.OriginalText);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_NearMatchOffsets_AreInNormalizedSpaceAndCorrect()
    {
        // The document carries a bidi mark and a hard line break that normalization strips; the near-match
        // offsets must index into the NORMALIZED document, not the raw input, and select the real text.
        var inputRaw = "‏פתיח קצר.\r\nהוא רץ אל הדלת ועוצר ליד הסף, ואז ממשיך.";
        const string misquotedSpan = "הוא רץ אל הדלת ועצר ליד הסף"; // past-tense mis-quote of "ועוצר"

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputRaw);
        const string correctSpan = "הוא רץ אל הדלת ועוצר ליד הסף";
        var expectedStart = normDoc.IndexOf(correctSpan, StringComparison.Ordinal);
        Assert.True(expectedStart >= 0);

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "tense slip with bidi/crlf" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputRaw);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + correctSpan.Length, suggestion.EndOffset);
        // Offsets index normalized space and select exactly the real document text.
        Assert.Equal(correctSpan, normDoc[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value]);
        Assert.Equal(correctSpan, suggestion.OriginalText);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_ContextBleedSpan_ProseSimilarToBodyWindow_IsDropped()
    {
        // The reopened-risk regression: at Scene scope the model is still handed PRECEDING/FOLLOWING
        // context it can quote from, so a detected issue's span can be a sentence from that neighboring
        // context that is genuinely ABSENT from the analyzed body - yet PROSE-SIMILAR to a body window.
        //
        // This span ("the old harbof walt was bold and the gray wager ruse") differs from the body window
        // "the old harbor wall was cold and the grey water rose" by SIX scattered single-char edits - well
        // inside the old percentage budget (ceil(0.12 * 52) = 7) - while sharing only the short stopword run
        // "the old" (~13% of the span), NOT a multi-word content run. Before the fix the near-match fallback
        // (generous 7-edit budget + full-offset scan) uniquely anchored it onto the body window, re-opening
        // exactly the out-of-unit false-anchor the be-c01 decision closed. After the fix it is dropped: the
        // shared exact word-run is too short (< 25% of the span) AND the real distance (6) exceeds the hard
        // 4-edit cap. The morphological near-misses (one word differs by 1 char) keep a LONG exact run, so
        // they still anchor - this only sheds prose-similar context bleed.
        const string inputText =
            "Near the shore the old harbor wall was cold and the grey water rose at noon.";
        const string contextBleedSpan =
            "the old harbof walt was bold and the gray wager ruse";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(contextBleedSpan, StringComparison.Ordinal) < 0,
            "fixture: span must not be an exact substring of the body");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = contextBleedSpan, Description = "register shift vs preceding context" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Out-of-unit prose-similar span -> dropped, no false body anchor.
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_HebrewContextBleedSpan_ProseSimilarToBodyWindow_IsDropped()
    {
        // Hebrew analog of the context-bleed regression. The span reads like a sentence quoted from the
        // preceding scene/chapter that is absent from the analyzed body but prose-similar to a body window:
        // many content words are perturbed by a single character each (הנהר->הנחר, הקר->הקור, סגריר->סגרירי,
        // רחש->רחשי, המים->המימ, הסלעים->הסלאים), so it lands inside the old percentage budget while sharing
        // only the short connective run "ושמענו את" (~15% of the span). Before the fix it falsely anchored
        // onto the body; after the fix it is dropped (short word-run + over the 4-edit cap). A true
        // morphological slip, where only ONE word differs, keeps a long exact run and still anchors.
        const string inputText =
            "ירדנו אל הנהר הקר ביום סגריר ושמענו את רחש המים בין הסלעים החלקלקים.";
        const string contextBleedSpan =
            "ירדנו אל הנחר הקור ביום סגרירי ושמענו את רחשי המימ בין הסלאים";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(contextBleedSpan, StringComparison.Ordinal) < 0,
            "fixture: span must not be an exact substring of the body");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = contextBleedSpan, Description = "register shift vs preceding scene" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Empty(suggestions);
    }
}

