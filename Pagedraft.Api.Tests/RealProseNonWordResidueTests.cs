using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

// Bound through a using ALIAS, not a namespace import - see RealProseNonWordResidue's own header.
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// RealProseNonWordResidueTests - the DETERMINISTIC, NO-MODEL, NO-GPU gate on the non-word gold class.
//
// WHAT IT GATES, and it is deliberately not the Hebrew. The readings recorded beside each instance
// are UNCONFIRMED and are never asserted here; correcting one later is a data edit. What IS asserted
// is everything a recorded instance can be silently wrong about:
//   - that it ANCHORS in the prose it names (the original span really is in that passage);
//   - that its replacement is NOT prose the manuscript already contained, checked at whole-token
//     level because להירד is a substring of the manuscript's own להירדם and a substring test would
//     invert the conclusion;
//   - that the class reconciles with the arm measurement it was extracted from;
//   - and that the shipped shape guard's reach over it is what the record says, COMPUTED from the
//     guard rather than copied from a comment.
// ---------------------------------------------------------------------------------------------
public class RealProseNonWordResidueTests
{
    [Fact]
    public void TheTen_AreTenAndArmAsAreFive_AndBothPopulationsAreNonEmpty()
    {
        Assert.Equal(10, RealProseNonWordResidue.Ten.Count);
        Assert.Equal(5, RealProseNonWordResidue.ArmAFive.Count);
        Assert.Equal(15, RealProseNonWordResidue.All.Count);
    }

    /// <summary>
    /// RECONCILIATION WITH THE MEASUREMENT THE INSTANCES CAME OUT OF. The arm measurement records the
    /// CORRUPTION family as 10 under the shipped default and 5 under ARM A; this class enumerates
    /// exactly those. If a future edit adds an eleventh instance without revisiting the measurement,
    /// or the measurement's family count moves without the enumeration, this turns red rather than
    /// leaving two records quietly disagreeing.
    /// </summary>
    [Fact]
    public void TheClassSize_ReconcilesWithTheArmMeasurementsCorruptionFamily()
    {
        var corruption = RealProseArmMeasurement.SemanticFamilies
            .Single(f => string.Equals(f.Family, "CORRUPTION", StringComparison.Ordinal));

        Assert.Equal(corruption.Off, RealProseNonWordResidue.Ten.Count);
        Assert.Equal(corruption.ArmA, RealProseNonWordResidue.ArmAFive.Count);
    }

    /// <summary>
    /// ANCHORING. Every instance's original span occurs in the CLEAN text of the passage it names, so
    /// no instance can drift into being a remembered string with no carrier. The passage lookup itself
    /// throws on an unknown id, which is the other half of the anchor.
    ///
    /// NON-VACUITY: the swept population is asserted, and the per-instance assertion is a containment
    /// in a specific passage's text rather than in the corpus at large - the weaker form would pass for
    /// a common word attributed to the wrong passage.
    /// </summary>
    [Fact]
    public void EveryInstance_AnchorsInTheCleanTextOfThePassageItNames()
    {
        var swept = 0;
        var missing = new List<string>();

        foreach (var i in RealProseNonWordResidue.All)
        {
            swept++;
            var clean = i.Passage.CleanText;

            if (!clean.Contains(i.Original, StringComparison.Ordinal))
                missing.Add($"{i.Arm}/{i.Ordinal}: [{i.Original}] is not in {i.PassageId}");
        }

        Assert.Equal(15, swept);
        Assert.True(missing.Count == 0, string.Join("; ", missing));
    }

    /// <summary>
    /// THE MODEL INTRODUCED IT. Every replacement contains at least one Hebrew token the passage's own
    /// prose does not contain, so no instance is really the manuscript's own wording mistaken for a
    /// model edit.
    ///
    /// WHOLE TOKENS, NOT SUBSTRINGS, and that distinction is load-bearing: instance 1's replacement
    /// להירד IS a substring of the manuscript's own להירדם, so a substring test would conclude the
    /// model changed nothing. Checked here the way c1 checked containment against the raw responses.
    /// </summary>
    [Fact]
    public void EveryInstance_IntroducesAtLeastOneTokenThePassageDidNotContain()
    {
        var swept = 0;
        var notIntroduced = new List<string>();

        foreach (var i in RealProseNonWordResidue.All)
        {
            swept++;

            var prose = RealProseNonWordResidue.HebrewTokens(i.Passage.CleanText)
                .ToHashSet(StringComparer.Ordinal);
            var replacement = RealProseNonWordResidue.HebrewTokens(i.Suggested);

            Assert.NotEmpty(replacement);

            if (replacement.All(prose.Contains))
                notIntroduced.Add(
                    $"{i.Arm}/{i.Ordinal}: every token of [{i.Suggested}] is already in {i.PassageId}");
        }

        Assert.Equal(15, swept);
        Assert.True(notIntroduced.Count == 0, string.Join("; ", notIntroduced));
    }

    /// <summary>
    /// SURFACE. Every instance was produced on the per-chunk production surface, and every passage it
    /// names really is measured on that surface - so the class cannot silently claim a regime its
    /// carriers do not ride. This is also the assertion that records WHY these are not
    /// proofread-gold.json cases: that corpus structurally cannot reach this surface.
    /// </summary>
    [Fact]
    public void EveryInstance_RidesTheChunkedPerChunkSurface()
    {
        Assert.Equal(GoldPromptSurface.ChunkedPerChunk, RealProseNonWordResidue.Surface);

        var swept = 0;
        foreach (var i in RealProseNonWordResidue.All)
        {
            swept++;
            Assert.Equal(RealProseNonWordResidue.Surface, i.Passage.Surface);
            // Each passage is exactly two chunks, so a recorded chunk index outside {0, 1} would mean
            // the instance was attributed to a chunk that does not exist.
            Assert.Equal(2, i.Passage.ExpectedChunkCount);
            Assert.InRange(i.RawChunkIndex, 0, i.Passage.ExpectedChunkCount - 1);
        }

        Assert.Equal(15, swept);
    }

    /// <summary>
    /// THE HONEST SIZING, COMPUTED. The shipped shape guard reaches ZERO of the ten the default prompt
    /// produced and exactly ONE of ARM A's five - and that one is צמצם -> צמץם, which the arm
    /// measurement's residue note ONCE MISATTRIBUTED to the OFF column and no longer does. That
    /// correction is itself pinned, by
    /// <see cref="TheOnlyMechanicallyIllegalInstance_IsRecordedUnderArmA_NotUnderTheShippedDefault"/>.
    ///
    /// This is the test that stops the guard from acquiring a reputation it has not earned: widen the
    /// guard and these numbers move on their own, so a future claim about its reach has to be made
    /// against the data rather than against a comment.
    /// </summary>
    [Fact]
    public void TheShapeGuard_Reaches0OfTheTen_And1OfArmAsFive()
    {
        var reachedInTen = RealProseNonWordResidue.Ten.Where(i => i.ShapeGuardWouldDrop).ToArray();
        Assert.True(reachedInTen.Length == 0,
            "the shape guard now reaches an instance the record says it cannot; update " +
            "RealProseNonWordResidue.WhatADeterministicGuardCanReach and this test together: " +
            string.Join(", ", reachedInTen.Select(i => $"{i.Original} -> {i.Suggested}")));

        var reachedInArmA = RealProseNonWordResidue.ArmAFive.Where(i => i.ShapeGuardWouldDrop).ToArray();
        var only = Assert.Single(reachedInArmA);
        Assert.Equal("צמצם", only.Original);
        Assert.Equal("צמץם", only.Suggested);
        Assert.Equal(ProofreadPromptArm.OverlapReferentLicence, only.Arm);

        Assert.Single(RealProseNonWordResidue.ShapeGuardReaches);
    }

    /// <summary>
    /// NON-VACUITY FOR THE TEST ABOVE. "The guard reaches 0 of the ten" would also be true of a guard
    /// that never fires at all, which is exactly the shape of a broken predicate. So the same predicate
    /// is shown to FIRE on this very corpus - on ARM A's instance - and the two assertions together
    /// distinguish "a narrow guard" from "a dead guard".
    /// </summary>
    [Fact]
    public void TheShapeGuardPredicate_IsNotDead_ItFiresOnThisCorpus()
    {
        Assert.NotEmpty(RealProseNonWordResidue.ShapeGuardReaches);
        Assert.True(HebrewOrthographyShapeGuard.WouldDrop("צמצם", "צמץם", out _));
    }

    /// <summary>
    /// TOKENHOOD IS CLASSIFIED, NOT USED AS AN ENTRY CRITERION. Two of the ten produce legal Hebrew
    /// tokens and are wrong only in context; they stay in the class because the assertion the class
    /// makes is "the model should not have proposed this edit on clean, twice-proofread text", which
    /// holds for them too. Their COUNT is pinned so a later reading correction is visible as a data
    /// change rather than silently rebalancing the class.
    /// </summary>
    [Fact]
    public void TheTwoLegalTokenInstances_AreClassifiedRatherThanDropped()
    {
        var legal = RealProseNonWordResidue.Ten
            .Where(i => i.Kind == NonWordKind.LegalTokenWrongInContext)
            .ToArray();

        Assert.Equal(2, legal.Length);
        Assert.Equal(new[] { "הזעת", "שהמרור" }, legal.Select(i => i.Suggested).ToArray());

        // No shape rule and no lexicon can reach these, which is the reason they are called out.
        Assert.All(legal, i => Assert.False(i.ShapeGuardWouldDrop));
        Assert.All(legal, i => Assert.False(HebrewOrthographyShapeGuard.IsImpossible(i.Suggested)));
    }

    /// <summary>
    /// THE SHARED INSTANCE. ARM A stopped nine of the ten and manufactured four of its own, which is
    /// what makes the class arm-INVARIANT in kind. That arithmetic is derived here rather than
    /// restated, so an edit to either list has to keep it true.
    /// </summary>
    [Fact]
    public void ArmAKeepsExactlyOneOfTheTen_AndAddsExactlyFour()
    {
        bool Same(RealProseNonWordInstance a, RealProseNonWordInstance b) =>
            string.Equals(a.PassageId, b.PassageId, StringComparison.Ordinal) &&
            string.Equals(a.Original, b.Original, StringComparison.Ordinal) &&
            string.Equals(a.Suggested, b.Suggested, StringComparison.Ordinal);

        var shared = RealProseNonWordResidue.ArmAFive
            .Where(a => RealProseNonWordResidue.Ten.Any(t => Same(a, t)))
            .ToArray();

        var kept = Assert.Single(shared);
        Assert.Equal("נכריח", kept.Original);
        Assert.Equal("נכריץ", kept.Suggested);
        Assert.Equal(4, RealProseNonWordResidue.ArmAFive.Count - shared.Length);
    }

    /// <summary>
    /// THE REGRESSION QUERY a future run scores itself with. Positive AND negative control, because a
    /// matcher that returns true for everything reads exactly like a working one.
    /// </summary>
    [Fact]
    public void IsRecordedInstance_MatchesARecordedEditAndNothingElse()
    {
        Assert.True(RealProseNonWordResidue.IsRecordedInstance(
            RealProsePrecisionFixtures.NarrationNoQuotesId, "להירדם", "להירד"));

        // right passage, wrong edit
        Assert.False(RealProseNonWordResidue.IsRecordedInstance(
            RealProsePrecisionFixtures.NarrationNoQuotesId, "להירדם", "לרדת"));
        // right edit, wrong passage
        Assert.False(RealProseNonWordResidue.IsRecordedInstance(
            RealProsePrecisionFixtures.ActionMidId, "להירדם", "להירד"));
    }

    /// <summary>
    /// THE MISATTRIBUTION THIS CLASS MUST NOT INHERIT, checked in two places. First, this class's own
    /// lists (<see cref="RealProseNonWordResidue.Ten"/> and <see cref="RealProseNonWordResidue.ArmAFive"/>)
    /// are pinned as the arm-correct record: צמצם -> צמץם is ARM A's, not OFF's. Second, the arm
    /// measurement's own prose - <see cref="RealProseArmMeasurement.UnownedResidue"/> - is asserted
    /// directly, not just described: the note used to list צמץם as the trailing item of an enumeration
    /// of "the OFF arm's 47 edits" ("...and צמצם -> צמץם"); that exact enumeration shape must not
    /// reappear, and the note must affirmatively say the suggestion is not an OFF edit rather than
    /// merely stop mentioning it. Both checks are non-vacuous: they first require the note to still
    /// name צמץם at all, so a note that silently dropped the suggestion could not pass by omission.
    /// </summary>
    [Fact]
    public void TheOnlyMechanicallyIllegalInstance_IsRecordedUnderArmA_NotUnderTheShippedDefault()
    {
        Assert.DoesNotContain(RealProseNonWordResidue.Ten,
            i => string.Equals(i.Suggested, "צמץם", StringComparison.Ordinal));
        Assert.Contains(RealProseNonWordResidue.ArmAFive,
            i => string.Equals(i.Suggested, "צמץם", StringComparison.Ordinal));

        var residueNote = RealProseArmMeasurement.UnownedResidue;

        // Non-vacuity floor: the checks below only mean something if the note still talks about this
        // suggestion at all. A note that stopped mentioning צמץם entirely would otherwise satisfy
        // "does not misattribute" for free.
        Assert.False(string.IsNullOrWhiteSpace(residueNote));
        Assert.Contains("צמץם", residueNote, StringComparison.Ordinal);

        // THE MISATTRIBUTION ITSELF, as a shape rather than the whole paragraph: the pre-correction
        // note enumerated צמץם as the last item of "the OFF arm's 47 edits" ("...and צמצם -> צמץם").
        // That construction must never come back, whatever else the wording around it changes to.
        Assert.DoesNotContain("and צמצם -> צמץם", residueNote, StringComparison.Ordinal);

        // And the corrected note must affirmatively deny OFF ownership, not merely omit the old claim.
        Assert.Contains("not an OFF edit", residueNote, StringComparison.Ordinal);

        // Not an assertion about which state the note is in - c3 owns that. An assertion that whatever
        // state it is in, THIS class stays the arm-correct record of the instance.
        Assert.True(RealProseNonWordResidue.WhatADeterministicGuardCanReach
                .Contains("1 of the five ARM A produced", StringComparison.Ordinal),
            "the sizing constant must keep naming ARM A as the owner of the one reachable instance");
    }

    /// <summary>
    /// The origin verdict is recorded as data so the next planner does not re-run c1's attribution.
    /// Deliberately a containment check on the two load-bearing words rather than a full-string
    /// comparison, which would only pin the prose.
    /// </summary>
    [Fact]
    public void TheOriginVerdict_IsRecordedAsRawModel()
    {
        Assert.StartsWith("RAW MODEL", RealProseNonWordResidue.Origin, StringComparison.Ordinal);
        Assert.Contains("IAiRouter", RealProseNonWordResidue.Origin, StringComparison.Ordinal);
        Assert.Equal(RealProseArmMeasurement.MeasuredOn, RealProseNonWordResidue.MeasuredOn);
        Assert.Equal(RealProseArmMeasurement.MeasuredOnModel, RealProseNonWordResidue.MeasuredOnModel);
    }

    /// <summary>
    /// The tokenizer the anchoring tests rest on. Its own non-vacuity: it must SPLIT on the boundaries
    /// the passages actually contain, or "the replacement introduces a new token" would be measured
    /// against one giant token and could never fail.
    /// </summary>
    [Fact]
    public void HebrewTokens_SplitsOnEveryNonLetter_AndIsNotVacuous()
    {
        Assert.Equal(new[] { "שלום", "עולם" }, RealProseNonWordResidue.HebrewTokens("שלום, עולם!"));
        Assert.Equal(new[] { "אם", "כן" }, RealProseNonWordResidue.HebrewTokens("אם־כן"));
        Assert.Equal(new[] { "תנ", "ך" }, RealProseNonWordResidue.HebrewTokens("תנ\"ך"));
        Assert.Empty(RealProseNonWordResidue.HebrewTokens("hello 123"));
        Assert.Empty(RealProseNonWordResidue.HebrewTokens(""));

        var corpusTokens = RealProsePrecisionFixtures.All
            .Sum(p => RealProseNonWordResidue.HebrewTokens(p.CleanText).Count);
        Assert.True(corpusTokens >= 4_000,
            $"the tokenizer produced only {corpusTokens} tokens over the whole real-prose corpus");
    }
}
