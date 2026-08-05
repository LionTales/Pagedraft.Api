using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and the whole point of this file's location is to be outside it.
// Same rule (and same reason) as ChunkedAgreementFixtureTests.
using ProofreadCorrection = Pagedraft.Api.Tests.LanguageEngine.ProofreadCorrection;
using ProofreadQualityTests = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// HebrewOrthographyShapeGuardTests - the shipped orthographic-impossibility safety net.
//
// THE TWO THINGS THESE TESTS EXIST TO PROVE, in order of importance:
//   1. THE GUARD CANNOT DROP A LEGAL CORRECTION. It is enabled by default and it REMOVES model
//      output, so the calibration is not a formality: every expected correction in both gold corpora,
//      every gold input, every gold ideal output, and 4,000-plus tokens of real twice-proofread
//      manuscript Hebrew must survive it untouched. A precision guard that eats legal repairs is the
//      exact failure the arm this file's sibling class records already made.
//   2. ITS REACH IS ONE SHAPE, NOT THE NON-WORD CLASS. The reach is COMPUTED against the recorded
//      corpus rather than asserted in a comment, so a future reader cannot inherit a claim the data
//      does not support.
//
// NON-VACUITY IS ASSERTED, NOT ASSUMED. Every "no offenders" test here first asserts the size of the
// population it swept. That is not ceremony: LoadProofreadGold returns an EMPTY array rather than
// throwing when the JSON is missing from the output directory, so a copy-to-output regression would
// otherwise green every calibration test in this file at once.
// ---------------------------------------------------------------------------------------------
public class HebrewOrthographyShapeGuardTests
{
    // ── the rule itself ──────────────────────────────────────────────────────────────────────────

    [Theory]
    // The one real instance in the recorded corpus: a final tsadi placed mid-word.
    [InlineData("צמץם")]
    // One probe per final form, so a future edit that drops a letter from the set turns this red.
    [InlineData("אךב")]
    [InlineData("אםב")]
    [InlineData("אןב")]
    [InlineData("אףב")]
    [InlineData("אץב")]
    // The offending letter need not be first.
    [InlineData("שלוםים")]
    public void ImpossibleWords_FlagsAFinalFormInANonFinalPosition(string text)
    {
        var offenders = HebrewOrthographyShapeGuard.ImpossibleWords(text);
        Assert.Equal(new[] { text }, offenders);
        Assert.True(HebrewOrthographyShapeGuard.IsImpossible(text));
    }

    [Theory]
    // A final form at the END of a word is the NORMAL case and must never be flagged. נכריץ is the
    // recorded instance that makes the point: it is a non-word, and its SHAPE is perfectly legal.
    [InlineData("נכריץ")]
    [InlineData("שלום")]
    [InlineData("הזמן")]
    [InlineData("צריך")]
    [InlineData("כסף")]
    // A maqaf compound: the mem is final in ITS word, and the maqaf ends that word.
    [InlineData("אם־כן")]
    [InlineData("בית־ספר")]
    // Acronyms: the gershayim/geresh ends the run, so the letter before it is word-final.
    [InlineData("תנ\"ך")]
    [InlineData("בע\"מ")]
    [InlineData("צה\"ל")]
    [InlineData("ר׳ם")]
    // An ASCII hyphen and an en-dash are boundaries too.
    [InlineData("אם-כן")]
    [InlineData("אם–כן")]
    // Vocalised text: every niqqud mark splits the run, which can only remove violations.
    [InlineData("שָׁלוֹם")]
    // Nothing Hebrew at all.
    [InlineData("hello world")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    public void ImpossibleWords_LeavesWellFormedOrNonHebrewTextAlone(string text)
    {
        Assert.Empty(HebrewOrthographyShapeGuard.ImpossibleWords(text));
        Assert.False(HebrewOrthographyShapeGuard.IsImpossible(text));
    }

    [Fact]
    public void ImpossibleWords_NullText_IsEmptyRatherThanThrowing()
    {
        Assert.Empty(HebrewOrthographyShapeGuard.ImpossibleWords(null));
        Assert.False(HebrewOrthographyShapeGuard.IsImpossible(null));
    }

    [Fact]
    public void ImpossibleWords_ReportsEveryOffendingWordOnce_InFirstAppearanceOrder()
    {
        var offenders = HebrewOrthographyShapeGuard.ImpossibleWords("אםב שלום אץב אםב תקין");

        Assert.Equal(new[] { "אםב", "אץב" }, offenders);
    }

    // ── the DROP predicate and its bound ─────────────────────────────────────────────────────────

    [Fact]
    public void WouldDrop_ReplacementIntroducingAnImpossibleWordIntoACleanOriginal_IsDropped()
    {
        Assert.True(HebrewOrthographyShapeGuard.WouldDrop("צמצם", "צמץם", out var offender));
        Assert.Equal("צמץם", offender);
    }

    [Fact]
    public void WouldDrop_AnOriginalThatIsALREADYImpossible_KeepsItsSuggestion()
    {
        // THE BOUND THAT MAKES THE GUARD SAFE. If the manuscript already writes an impossible shape
        // (a Masoretic anomaly, a stylised transliteration, an OCR artifact), a proofread suggestion
        // touching that word is the very repair the user needs. Half (a) of the predicate is what
        // keeps such a word REPAIRABLE rather than frozen - and the repair here is a real one: the
        // replacement fixes the mid-word final mem.
        Assert.False(HebrewOrthographyShapeGuard.WouldDrop("לםרבה", "למרבה", out var offender));
        Assert.Equal(string.Empty, offender);

        // ... and it holds even when the replacement is ALSO impossible, because at that point the
        // guard has no way to tell a botched repair from a preserved oddity, and the conservative
        // reading is the one that never suppresses a repair.
        Assert.False(HebrewOrthographyShapeGuard.WouldDrop("לםרבה", "לםרבהו", out _));
    }

    [Theory]
    [InlineData("נכריח", "נכריץ")]   // a non-word whose SHAPE is legal - out of reach, by design
    [InlineData("הזאת", "הזעת")]     // a legal token, wrong only in context - out of reach
    [InlineData("שלום", "")]          // a pure deletion has no replacement to inspect
    [InlineData("colour", "color")]   // English cannot trip a Hebrew-letter rule
    [InlineData("השעונ", "השעון")]   // a gold seed's repair: medial->final AT the word end, legal
    public void WouldDrop_LeavesEverythingOutsideTheOneShapeAlone(string original, string suggested)
    {
        Assert.False(HebrewOrthographyShapeGuard.WouldDrop(original, suggested, out var offender));
        Assert.Equal(string.Empty, offender);
    }

    [Fact]
    public void WouldDrop_NullsAreTreatedAsEmpty_RatherThanThrowing()
    {
        Assert.False(HebrewOrthographyShapeGuard.WouldDrop(null, null, out _));
        Assert.False(HebrewOrthographyShapeGuard.WouldDrop("שלום", null, out _));
        Assert.True(HebrewOrthographyShapeGuard.WouldDrop(null, "צמץם", out _));
    }

    // ── calibration: it must not regress the gold corpora ────────────────────────────────────────

    /// <summary>
    /// ZERO GOLD REGRESSIONS, PROVED THROUGH THE PRODUCTION DIFF rather than through the predicate
    /// alone. For every gold case that declares the corrected text a perfect proofread run would
    /// produce, the guarded suggestion list is IDENTICAL to the unguarded one - same count, same
    /// spans, same replacements, same order.
    ///
    /// NON-VACUITY, in three layers, because this test would pass trivially in at least three ways:
    ///  1. the corpora must actually load (LoadProofreadGold returns Array.Empty on a missing file);
    ///  2. the cases that declare a corrected text must be a substantial population; and
    ///  3. the diff must actually PRODUCE suggestions - comparing two empty lists proves nothing.
    /// </summary>
    [Fact]
    public void TheGuard_DropsNothingOnEitherGoldCorpusIdealOutput()
    {
        var hebrew = ProofreadQualityTests.LoadProofreadGold("proofread-gold.json");
        var english = ProofreadQualityTests.LoadProofreadGold("proofread-gold-en.json");
        Assert.Equal(116, hebrew.Length);
        Assert.Equal(30, english.Length);

        var guarded = new SuggestionDiffService(
            new HebrewStyleOptions { DropOrthographicallyImpossibleSuggestions = true });
        var unguarded = new SuggestionDiffService(
            new HebrewStyleOptions { DropOrthographicallyImpossibleSuggestions = false });

        var casesSwept = 0;
        var suggestionsSwept = 0;
        var divergences = new List<string>();

        foreach (var c in hebrew.Concat(english))
        {
            if (string.IsNullOrWhiteSpace(c.Input) || string.IsNullOrWhiteSpace(c.ExpectedCorrectedText))
                continue;

            casesSwept++;

            var withGuard = guarded.ComputeProofreadSuggestions(
                c.Input, c.ExpectedCorrectedText!, out var outcome);
            var withoutGuard = unguarded.ComputeProofreadSuggestions(c.Input, c.ExpectedCorrectedText!);

            suggestionsSwept += withoutGuard.Count;

            if (outcome.DroppedCount != 0 || !SameSuggestions(withGuard, withoutGuard))
                divergences.Add(
                    $"{c.Id}: guarded {withGuard.Count} vs unguarded {withoutGuard.Count}, dropped " +
                    string.Join(" | ", outcome.Dropped.Select(d =>
                        $"[{d.OriginalText}] -> [{d.SuggestedText}]")));
        }

        Assert.Equal(42, casesSwept);
        Assert.True(suggestionsSwept >= 40,
            $"the sweep produced only {suggestionsSwept} suggestions, so 'identical with and without " +
            "the guard' would be a comparison of near-empty lists rather than a calibration");
        Assert.True(divergences.Count == 0,
            "the shape guard changed a gold case's suggestion set: " + string.Join("; ", divergences));
    }

    /// <summary>
    /// The same calibration one level lower: every correction the gold corpora DECLARE - the exact
    /// (original -> suggested) pairs a perfect model would emit - survives the drop predicate.
    /// Cheaper and more direct than the diff sweep above, and it covers the declared pairs that no
    /// ExpectedCorrectedText carries.
    /// </summary>
    [Fact]
    public void TheGuard_DropsNoDeclaredGoldCorrection()
    {
        var declared =
            ProofreadQualityTests.LoadProofreadGold("proofread-gold.json")
                .Concat(ProofreadQualityTests.LoadProofreadGold("proofread-gold-en.json"))
                .SelectMany(c => (c.ExpectedCorrections ?? Array.Empty<ProofreadCorrection>())
                    .Select(e => (c.Id, e.Original, e.Suggested)))
                .ToArray();

        Assert.Equal(50, declared.Length);

        var dropped = declared
            .Where(d => HebrewOrthographyShapeGuard.WouldDrop(d.Original, d.Suggested, out _))
            .Select(d => $"{d.Id}: [{d.Original}] -> [{d.Suggested}]")
            .ToArray();

        Assert.True(dropped.Length == 0,
            "the shape guard would suppress a declared gold correction: " + string.Join("; ", dropped));
    }

    /// <summary>
    /// The strongest false-positive calibration available offline: the twelve real manuscript passages
    /// are verbatim Hebrew prose that a human proofread TWICE. If the rule fired anywhere in there it
    /// would be firing on correct Hebrew, and no bound could rescue it.
    ///
    /// NON-VACUITY: the token count is asserted, so a fixture file that stopped compiling into the
    /// passages (or an empty corpus) cannot pass this as "no offenders".
    /// </summary>
    [Fact]
    public void TheRule_FiresNowhereInFourThousandTokensOfRealTwiceProofreadHebrew()
    {
        var tokens = RealProsePrecisionFixtures.All
            .SelectMany(p => RealProseNonWordResidue.HebrewTokens(p.CleanText))
            .ToArray();

        Assert.True(tokens.Length >= 4_000,
            $"only {tokens.Length} Hebrew tokens were swept, which is not the real-prose corpus");

        var offenders = tokens.Where(HebrewOrthographyShapeGuard.IsImpossible).Distinct().ToArray();

        Assert.True(offenders.Length == 0,
            "the rule fired on human-proofread manuscript Hebrew: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The seeded defects the recall half of the real-prose surface depends on. Their repairs are
    /// exactly the "medial form written where a final form belongs" class, i.e. the MIRROR of the rule
    /// - so this checks the guard does not confuse the two directions and eat the repair.
    /// </summary>
    [Fact]
    public void TheGuard_DropsNoSeededGoldRepairOnTheRealProseSurface()
    {
        var seeds = RealProsePrecisionFixtures.All.SelectMany(p => p.Seeds).ToArray();

        Assert.Equal(8, seeds.Length);

        var dropped = seeds
            .Where(s => HebrewOrthographyShapeGuard.WouldDrop(s.SeededSpan, s.CleanSpan, out _))
            .Select(s => $"{s.GoldCaseId}: [{s.SeededSpan}] -> [{s.CleanSpan}]")
            .ToArray();

        Assert.True(dropped.Length == 0,
            "the shape guard would suppress a transplanted gold repair: " + string.Join("; ", dropped));
    }

    // ── the bound, as a property of the service ──────────────────────────────────────────────────

    /// <summary>
    /// THE STRUCTURAL BOUND: the guard can only ever REMOVE entries. The guarded output is always a
    /// SUBSEQUENCE of the unguarded output - never reordered, never rewritten, never extended - and
    /// every missing entry is accounted for in the reported outcome. That is what makes "it cannot
    /// corrupt a suggestion" a property rather than a hope.
    ///
    /// The battery deliberately includes inputs that DO trip the guard, so the subsequence relation is
    /// exercised on a non-empty difference rather than only on the identity case; the assertion below
    /// requires that.
    /// </summary>
    [Fact]
    public void GuardedOutput_IsAlwaysASubsequenceOfUnguardedOutput()
    {
        var pairs = new (string Original, string Result)[]
        {
            // trips the guard
            ("הוא צמצם את הפער בין השניים.", "הוא צמץם את הפער בין השניים."),
            ("היא אמרה שלום לכולם.", "היא אמרה שלוםים לכולם."),
            // trips it AND carries a legitimate edit in the same run
            ("הוא צמצם את הפער בין השניים.", "הוא צמץם את הפער בין השתיים."),
            // does not trip it
            ("שלום עולם. זהו טקסט לבדיקה.", "שלום עולם! זהו טקסט לבדיקה מתוקן."),
            ("היא נכריח אותו ללכת.", "היא נכריץ אותו ללכת."),
            ("the colour of the sky", "the color of the sky"),
        };

        var guarded = new SuggestionDiffService(
            new HebrewStyleOptions { DropOrthographicallyImpossibleSuggestions = true });
        var unguarded = new SuggestionDiffService(
            new HebrewStyleOptions { DropOrthographicallyImpossibleSuggestions = false });

        var totalDropped = 0;

        foreach (var (original, result) in pairs)
        {
            var withGuard = guarded.ComputeProofreadSuggestions(original, result, out var outcome);
            var withoutGuard = unguarded.ComputeProofreadSuggestions(original, result);

            Assert.True(outcome.Ran);
            totalDropped += outcome.DroppedCount;

            Assert.Equal(withoutGuard.Count - outcome.DroppedCount, withGuard.Count);
            Assert.True(IsSubsequence(withGuard, withoutGuard),
                $"the guarded list is not a subsequence of the unguarded one for [{original}]");

            // Every entry the guard removed is REPORTED, with the word that caused it.
            foreach (var drop in outcome.Dropped)
            {
                Assert.Contains(withoutGuard, s =>
                    string.Equals(s.SuggestedText, drop.SuggestedText, StringComparison.Ordinal));
                Assert.DoesNotContain(withGuard, s =>
                    string.Equals(s.SuggestedText, drop.SuggestedText, StringComparison.Ordinal));
                Assert.Contains(drop.OffendingWord, drop.SuggestedText, StringComparison.Ordinal);
            }
        }

        Assert.True(totalDropped >= 3,
            $"only {totalDropped} suggestions were dropped across the battery, so the subsequence " +
            "relation was exercised on an (almost) empty difference and proves nothing");
    }

    [Fact]
    public void DefaultOutcome_HasAnEmptyEnumerableDroppedList_NotANull()
    {
        // A public record struct is constructible via default(...) by any caller, bypassing both
        // constructors below. If Dropped were a bare positional property, this would be null and
        // DroppedCount's null-conditional would be the only thing standing between that and a
        // NullReferenceException in every bare `foreach (var drop in shapeGuard.Dropped)`.
        var outcome = default(ProofreadShapeGuardOutcome);

        Assert.False(outcome.Ran);
        Assert.Equal(0, outcome.DroppedCount);
        Assert.NotNull(outcome.Dropped);
        Assert.Empty(outcome.Dropped);

        foreach (var drop in outcome.Dropped)
            Assert.Fail($"unexpected drop on a default outcome: {drop.OriginalText}");
    }

    [Fact]
    public void KillSwitchOff_RestoresTheUnguardedSuggestionListExactly()
    {
        const string original = "הוא צמצם את הפער.";
        const string result = "הוא צמץם את הפער.";

        var off = new SuggestionDiffService(
            new HebrewStyleOptions { DropOrthographicallyImpossibleSuggestions = false });

        var suggestions = off.ComputeProofreadSuggestions(original, result, out var outcome);

        Assert.False(outcome.Ran);
        Assert.Equal(0, outcome.DroppedCount);
        Assert.Contains(suggestions, s => s.SuggestedText.Contains("צמץם", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGuardShipsON_AndTheParameterlessServiceTakesThatSamePosture()
    {
        // The class default is what a hosted deployment gets: appsettings.Production.json carries no
        // Ai:HebrewStyle block, so a flip here silently changes production while base appsettings.json
        // still reads true.
        Assert.True(new HebrewStyleOptions().DropOrthographicallyImpossibleSuggestions);

        var suggestions = new SuggestionDiffService()
            .ComputeProofreadSuggestions("הוא צמצם את הפער.", "הוא צמץם את הפער.", out var outcome);

        Assert.True(outcome.Ran);
        Assert.Equal(1, outcome.DroppedCount);
        Assert.DoesNotContain(suggestions, s => s.SuggestedText.Contains("צמץם", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE SUGGESTION CAP AND THE DROP COUNT MUST AGREE. A proofread whose suggestion list exceeds
    /// MaxSuggestionCountForProofread (2,000) is discarded WHOLE, so the guard withheld nothing from
    /// the caller on that branch and the reported drop count must be zero. Without the reset it stays
    /// at whatever the guard removed from the list the cap then threw away, and the run persists
    /// SuppressedSuggestionCount > 0 beside SuggestionCount == 0 - the exact pair an operator is told
    /// to read together - plus a Warning naming a suggestion no user could ever have seen.
    ///
    /// NON-VACUITY, in the same test and mandatory: "zero drops" is only a fact about the CAP branch if
    /// the very same construction UNDER the cap does return suggestions and does report the drop. The
    /// control below is that proof; without it this test would pass just as well on an input that never
    /// tripped the guard at all.
    /// </summary>
    [Fact]
    public void OverTheSuggestionCap_TheResultIsDiscardedWhole_AndNoDropIsReported()
    {
        // 2,101 candidate suggestions (2,100 generated + the impossible-shape one), so the guarded
        // list is still 2,100 and clears the 2,000 cap.
        var (cappedOriginal, cappedResult) = BuildPairWithOneImpossibleShape(2_100);
        var (controlOriginal, controlResult) = BuildPairWithOneImpossibleShape(20);

        var service = new SuggestionDiffService(
            new HebrewStyleOptions { DropOrthographicallyImpossibleSuggestions = true });

        var capped = service.ComputeProofreadSuggestions(cappedOriginal, cappedResult, out var cappedOutcome);

        Assert.Empty(capped);
        Assert.Equal(0, cappedOutcome.DroppedCount);
        Assert.Empty(cappedOutcome.Dropped);
        // The guard is still ENABLED on this run: the cap says nothing about the kill switch.
        Assert.True(cappedOutcome.Ran);

        // NON-VACUITY CONTROL: the same construction, trimmed under the cap.
        var control = service.ComputeProofreadSuggestions(
            controlOriginal, controlResult, out var controlOutcome);

        Assert.True(control.Count >= 20,
            $"the under-cap control produced only {control.Count} suggestions, so the capped input " +
            "was never anywhere near the cap and the assertions above prove nothing");
        Assert.Equal(1, controlOutcome.DroppedCount);
        Assert.Equal("צמץם", controlOutcome.Dropped[0].OffendingWord);
        Assert.DoesNotContain(control, s => s.SuggestedText.Contains("צמץם", StringComparison.Ordinal));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A proofread (original, result) pair carrying <paramref name="differingWords"/> one-word changes
    /// plus the corpus's single recorded impossible-shape replacement (צמצם -> צמץם), so the count of
    /// suggestions is set by the caller and exactly one of them trips the guard. Generating the words
    /// is the only practical way to exceed a 2,000 cap in a test.
    ///
    /// The unchanged "zz" between every changed word is load-bearing, not padding: BuildMergedWordRanges
    /// merges word ranges that sit within one character of each other, so consecutive changed words
    /// would fuse into a single range and the pair would yield far fewer than one suggestion per word.
    /// </summary>
    private static (string Original, string Result) BuildPairWithOneImpossibleShape(int differingWords)
    {
        var original = new StringBuilder("הוא צמצם את הפער. ");
        var result = new StringBuilder("הוא צמץם את הפער. ");

        for (var i = 0; i < differingWords; i++)
        {
            original.Append('p').Append(i).Append(" zz ");
            result.Append('q').Append(i).Append(" zz ");
        }

        return (original.ToString(), result.ToString());
    }


    private static bool SameSuggestions(
        IReadOnlyList<AnalysisSuggestion> a, IReadOnlyList<AnalysisSuggestion> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].OriginalText, b[i].OriginalText, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].SuggestedText, b[i].SuggestedText, StringComparison.Ordinal)) return false;
            if (a[i].StartOffset != b[i].StartOffset || a[i].EndOffset != b[i].EndOffset) return false;
        }

        return true;
    }

    private static bool IsSubsequence(
        IReadOnlyList<AnalysisSuggestion> candidate, IReadOnlyList<AnalysisSuggestion> full)
    {
        var j = 0;
        foreach (var c in candidate)
        {
            while (j < full.Count &&
                   !(string.Equals(full[j].OriginalText, c.OriginalText, StringComparison.Ordinal) &&
                     string.Equals(full[j].SuggestedText, c.SuggestedText, StringComparison.Ordinal) &&
                     full[j].StartOffset == c.StartOffset))
                j++;

            if (j == full.Count) return false;
            j++;
        }

        return true;
    }
}
