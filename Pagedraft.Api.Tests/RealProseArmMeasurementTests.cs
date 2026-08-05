using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC, NO-MODEL, NO-GPU gate on the SHIP-NOTHING outcome of the real-prose arm measurement.
///
/// WHAT HAPPENED. ARM A of <c>referent-carry-forward-2026-08-04</c> was re-landed behind a default-off
/// switch and measured on a corpus of real, twice-proofread Hebrew manuscript prose. Its synthetic
/// 38-53% over-correction cut did NOT reproduce: 5 of 12 passages individually, one-sided p = 0.363,
/// three passages WORSE. The switch stays OFF and the lead is closed.
///
/// WHAT THIS FILE HAS TO PROVE, and why none of it is ceremony:
///
///  1. THE RECORDED MATRIX IS WHAT PRODUCES THE VERDICT. The totals, the win/tie/loss split and the
///     sign-test p are all DERIVED from <see cref="RealProseArmMeasurement.Precision"/> here rather
///     than quoted, so a row edited to make the arm look better fails instead of rewriting history.
///  2. THE RECORD IS TIED TO THE SURFACE. Every passage id and every seed id it cites must still exist
///     in <see cref="RealProsePrecisionFixtures"/>. A passage renamed or dropped would otherwise leave
///     a measurement filed against prose nobody can find.
///  3. THE REASON IT DID NOT GENERALISE IS RE-DERIVED, NOT REMEMBERED. The synthetic effect had one
///     carrier construction; the claim that it is absent from real prose is recomputed from the
///     embedded passages through the REAL chunker on every run, with a non-vacuous control. This is
///     the assertion that makes the closure durable: a future passage set carrying the carrier fails
///     here rather than silently inheriting a verdict that would not apply to it.
///  4. THE PRODUCTION POSTURE MATCHES THE VERDICT. A recorded DO-NOT-SHIP beside a shipped arm that is
///     ON is the failure this whole record exists to prevent.
///
/// Every "no offenders" assertion proves its population non-empty first. On this surface that is not a
/// habit: a counter whose needle never matched anything, over a corpus that shrank to nothing, would
/// report EXACTLY the carrier-absence result claim 3 depends on.
/// </summary>
public class RealProseArmMeasurementTests
{
    /// <summary>Hebrew letters (aleph through tav, including finals). A match is "standalone" when the character before it is not one of these.</summary>
    private static bool IsHebrewLetter(char c) => c is >= 'א' and <= 'ת';

    // ── 1. the recorded matrix is what produces the verdict ──────────────────────────────────────

    /// <summary>
    /// THE PRECISION MATRIX IS ARITHMETICALLY COHERENT AND STILL FAILS ITS OWN PRE-STATED BAR.
    ///
    /// Everything here is recomputed from the rows: 47 -> 39, five wins, four ties, three losses, and a
    /// one-sided exact sign-test p of 0.363 against c1's bar of >=10 of 12 (p = 0.019). The point is
    /// that the CONCLUSION is a function of the DATA - edit a row to flatter the arm and this fails,
    /// which is the only version of "we measured it and it lost" that cannot rot.
    /// </summary>
    [Fact]
    public void ThePrecisionMatrix_IsCoherent_AndTheRecordedVerdictIsWhatItImplies()
    {
        var rows = RealProseArmMeasurement.Precision;
        Assert.Equal(12, rows.Count);
        Assert.Equal(12, rows.Select(r => r.PassageId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows, r => Assert.True(r.Off >= 0 && r.ArmA >= 0, $"{r.PassageId}: negative edit count"));

        // The published totals, derived rather than restated.
        Assert.Equal(47, RealProseArmMeasurement.OffTotal);
        Assert.Equal(39, RealProseArmMeasurement.ArmATotal);
        Assert.Equal(-8, RealProseArmMeasurement.ArmATotal - RealProseArmMeasurement.OffTotal);
        Assert.Equal(-8, rows.Sum(r => r.Delta));

        // Per-passage, which is the axis condition 1 was actually stated on.
        Assert.Equal(5, RealProseArmMeasurement.PassagesArmAWon);
        Assert.Equal(4, RealProseArmMeasurement.PassagesTied);
        Assert.Equal(3, RealProseArmMeasurement.PassagesArmALost);
        Assert.Equal(12, RealProseArmMeasurement.PassagesArmAWon +
                         RealProseArmMeasurement.PassagesTied +
                         RealProseArmMeasurement.PassagesArmALost);

        // THE BAR. Recorded, and missed by half.
        Assert.True(RealProseArmMeasurement.PassagesArmAWon < RealProseArmMeasurement.PreStatedPassageBar,
            "the recorded matrix now MEETS the pre-stated per-passage bar, so the DO-NOT-SHIP verdict " +
            "filed beside it is no longer what the data says. Re-measure or re-write the verdict; do " +
            "not leave the two disagreeing.");

        // The sign test, computed. The helper is proved live on both extremes first, because a stub
        // that returned a constant above 0.05 would satisfy the assertion that follows for free.
        Assert.Equal(1.0, RealProseArmMeasurement.OneSidedSignTestP(0, 8), 10);
        Assert.Equal(1.0 / 256.0, RealProseArmMeasurement.OneSidedSignTestP(8, 8), 10);
        Assert.True(RealProseArmMeasurement.OneSidedSignTestP(10, 12) < 0.02,
            "the bar c1 set (10 of 12) must itself be significant, or the bar was never a bar");

        var p = RealProseArmMeasurement.ObservedSignTestP;
        Assert.Equal(0.363, p, 3);
        Assert.True(p > 0.05,
            $"the recorded per-passage split is now significant (p = {p:F3}), which contradicts the " +
            "recorded verdict of a coin flip");

        // The churn that makes the net counts overstate the effect: 29 shared + 18 off-only = OFF's
        // total, 29 shared + 10 arm-only = ARM A's, and the two together are the 57 distinct edits.
        Assert.Equal(
            RealProseArmMeasurement.SharedDistinctEdits + RealProseArmMeasurement.OffOnlyDistinctEdits,
            RealProseArmMeasurement.OffTotal);
        Assert.Equal(
            RealProseArmMeasurement.SharedDistinctEdits + RealProseArmMeasurement.ArmAOnlyDistinctEdits,
            RealProseArmMeasurement.ArmATotal);
        Assert.True(RealProseArmMeasurement.ArmAOnlyDistinctEdits > 0,
            "ARM A is recorded as introducing no edits of its own, which would make it a pure " +
            "suppressor - the shape the synthetic run suggested and this one refuted");
    }

    /// <summary>
    /// THE SEMANTIC FAMILY BREAKDOWN RECONCILES TO THE PRECISION TOTALS, in both columns.
    ///
    /// A hand classification that does not add up to the machine count is a classification of something
    /// else. This is the only check that ties c1's families - the layer condition 3 was decided on - to
    /// the numbers condition 1 was decided on.
    /// </summary>
    [Fact]
    public void TheSemanticFamilies_ReconcileToThePrecisionTotals_AndTheNamedHalfIsTheHalfThatFailed()
    {
        var families = RealProseArmMeasurement.SemanticFamilies;
        Assert.NotEmpty(families);
        Assert.Equal(families.Count, families.Select(f => f.Family).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(RealProseArmMeasurement.OffTotal, families.Sum(f => f.Off));
        Assert.Equal(RealProseArmMeasurement.ArmATotal, families.Sum(f => f.ArmA));

        // The two halves of condition 3, as data: the corruption family moved at c1's magnitude, the
        // REGISTER family - the one the named mechanism is about - did not.
        var corruption = families.Single(f => f.Family == "CORRUPTION");
        var register = families.Single(f => f.Family == "REGISTER");
        Assert.Equal(10, corruption.Off);
        Assert.Equal(5, corruption.ArmA);
        Assert.Equal(12, register.Off);
        Assert.Equal(10, register.ArmA);

        var corruptionCut = 1.0 - (double)corruption.ArmA / corruption.Off;
        var registerCut = 1.0 - (double)register.ArmA / register.Off;
        Assert.True(corruptionCut > 2 * registerCut,
            "the corruption family no longer moves far more than the register family, which is the " +
            "whole shape of the condition-3 failure: c1's mechanism predicts the REGISTER cut, and " +
            "that is the one that did not reproduce");

        // The orthographic layer is arm-invariant, exactly as c1 predicted. Recorded because it is the
        // half of the mechanism that DID reproduce, and a closure that only records failures is a
        // caricature of the run.
        var orthographic = families.Single(f => f.Family == "ORTHOGRAPHIC");
        Assert.Equal(orthographic.Off, orthographic.ArmA);
        Assert.True(orthographic.Off > 0, "the orthographic family is empty, so its flatness says nothing");

        Assert.All(families, f => Assert.True(f.Note.Trim().Length >= 40,
            $"{f.Family}: a family row with no note is a number nobody can interpret"));
    }

    // ── 2. the record is tied to the surface it was measured on ──────────────────────────────────

    /// <summary>
    /// EVERY PASSAGE AND EVERY SEED THE RECORD CITES STILL EXISTS, and the precision matrix covers the
    /// WHOLE clean corpus rather than a flattering subset - which is the specific thing condition 1
    /// forbade ("report the per-passage matrix, never a subset mean alone").
    /// </summary>
    [Fact]
    public void TheRecord_CoversTheWholeCorpus_AndEveryPassageAndSeedItCitesStillExists()
    {
        var corpusIds = RealProsePrecisionFixtures.All.Select(p => p.Id).ToArray();
        Assert.Equal(12, corpusIds.Length);

        // Set equality both ways: no passage measured that does not exist, none omitted that does.
        var measuredIds = RealProseArmMeasurement.Precision.Select(r => r.PassageId).ToArray();
        Assert.Empty(measuredIds.Except(corpusIds, StringComparer.Ordinal));
        Assert.Empty(corpusIds.Except(measuredIds, StringComparer.Ordinal));

        // ...and each cited passage resolves through the corpus's own accessor, which throws on unknown.
        foreach (var id in measuredIds) Assert.Equal(id, RealProsePrecisionFixtures.ById(id).Id);

        // The recall rows are exactly the seeded passages, and each cites exactly that passage's seeds.
        var seededIds = RealProsePrecisionFixtures.Seeded.Select(p => p.Id).ToArray();
        Assert.Equal(4, seededIds.Length);
        Assert.Empty(RealProseArmMeasurement.Recall.Select(r => r.PassageId)
            .Except(seededIds, StringComparer.Ordinal));
        Assert.Empty(seededIds.Except(RealProseArmMeasurement.Recall.Select(r => r.PassageId),
            StringComparer.Ordinal));

        var offenders = new List<string>();
        var seedsChecked = 0;
        foreach (var row in RealProseArmMeasurement.Recall)
        {
            var declared = RealProsePrecisionFixtures.ById(row.PassageId).Seeds
                .Select(s => s.GoldCaseId).ToArray();
            var cited = row.RepairedGoldCaseIds.Concat(row.MissedGoldCaseIds).ToArray();
            seedsChecked += cited.Length;

            if (cited.Distinct(StringComparer.Ordinal).Count() != cited.Length)
                offenders.Add($"{row.PassageId}: a seed is recorded as both repaired and missed");
            foreach (var phantom in cited.Except(declared, StringComparer.Ordinal))
                offenders.Add($"{row.PassageId}: cites seed '{phantom}', which the passage does not carry");
            foreach (var missing in declared.Except(cited, StringComparer.Ordinal))
                offenders.Add($"{row.PassageId}: seed '{missing}' has no recorded outcome at all");
        }

        Assert.True(seedsChecked == 8, $"expected 8 seed outcomes, found {seedsChecked}");
        Assert.True(offenders.Count == 0,
            "The recall record does not line up with the seeds actually transplanted into the " +
            "corpus:\n  " + string.Join("\n  ", offenders));

        // THE RECALL READING ITSELF: 6 of 8, and it is the SAME 6 under both arms, which is why this is
        // not the "precision up, recall down" case the plan said must not ship.
        Assert.Equal(6, RealProseArmMeasurement.SeedsRepaired);
        Assert.Equal(2, RealProseArmMeasurement.SeedsMissed);
        Assert.Equal(8, RealProseArmMeasurement.SeedsRepaired + RealProseArmMeasurement.SeedsMissed);

        // ...and it is honestly labelled as under-powered rather than as an established recall claim.
        Assert.True(RealProseArmMeasurement.SeedsRepaired + RealProseArmMeasurement.SeedsMissed <
                    ProofreadStandingFloor.SingleCaseMinimumRuns,
            "the recall guard now reaches the corpus's n>=15 single-case rule, so the record's " +
            "'recall did not drop, not recall is established' framing understates what was measured");
    }

    // ── 3. the reason it did not generalise, re-derived from the corpus ──────────────────────────

    /// <summary>
    /// THE CARRIER IS ABSENT FROM REAL PROSE, RECOMPUTED ON EVERY RUN.
    ///
    /// This is the load-bearing assertion of the whole closure. The synthetic measurement was carried by
    /// four instances of ONE construction; if that construction were present here, the negative would be
    /// a real refutation of the arm, and if it is absent the negative is a statement about the corpora.
    /// It is the second, and this test is what keeps that true: it counts standalone occurrences of the
    /// carrier across the 24 clean chunk units the real chunker produces, through the same seam the
    /// measurement used.
    ///
    /// NON-VACUITY, THREE WAYS, because "found zero" is exactly what a broken counter reports:
    ///  - the population is asserted to be 24 units of real length;
    ///  - the SUBSTRING form of the same needle IS found (the manuscript's זמן ה..., i.e. the word
    ///    "time" plus a definite article), so needle and population are both real and it is the word
    ///    boundary alone that excludes them;
    ///  - a common Hebrew standalone token is counted in the same population with the same
    ///    boundary-aware counter and found in the hundreds, so the boundary rule does not reject
    ///    everything.
    /// </summary>
    [Fact]
    public void TheCarrierConstruction_IsAbsentFromEveryRealProseChunk_AndTheCounterIsProvedLive()
    {
        var carrier = RealProsePrecisionFixtures.SyntheticDominantConstruction;
        Assert.False(string.IsNullOrWhiteSpace(carrier));

        // The population: every clean chunk as the model saw it, overlap included.
        var units = new List<string>();
        foreach (var passage in RealProsePrecisionFixtures.All)
            foreach (var chunk in RealProseHarness.Chunk(passage, RealProseVariant.Clean))
                units.Add((chunk.OverlapPrefix ?? "") + "\n" + chunk.Text);

        Assert.Equal(24, units.Count);
        Assert.All(units, u => Assert.True(u.Length > 200, "a chunk unit is too short to be real prose"));

        // NON-VACUITY (a): the needle is findable as a SUBSTRING in this very population.
        var substringHits = units.Sum(u => RealProsePrecisionFixtures.Occurrences(u, carrier));
        Assert.True(substringHits > 0,
            $"'{carrier}' does not occur even as a substring in the corpus, so the standalone count " +
            "below is zero for a reason that has nothing to do with the construction");

        // NON-VACUITY (b): the boundary-aware counter finds a common standalone Hebrew token in bulk.
        var control = units.Sum(u => StandaloneOccurrences(u, "את "));
        Assert.True(control >= 100,
            $"the boundary-aware counter found only {control} occurrences of a very common standalone " +
            "Hebrew token, so it is rejecting matches it should accept and its zero below proves nothing");

        // THE CLAIM.
        var offenders = new List<string>();
        for (var i = 0; i < units.Count; i++)
        {
            var hits = StandaloneOccurrences(units[i], carrier);
            if (hits > 0) offenders.Add($"chunk unit {i}: {hits} standalone occurrence(s)");
        }

        Assert.True(offenders.Count == 0,
            $"The construction '{carrier}' now occurs as a standalone word sequence in the real-prose " +
            "corpus. It carried 61.6% of the OFF arm's over-corrections on the SYNTHETIC fixtures and " +
            "93% of the gross drop there, and its ABSENCE here is the recorded reason ARM A's cut did " +
            "not generalise. A corpus that contains it is no longer the corpus that verdict was " +
            "measured on, so the closure has to be re-opened rather than inherited:\n  " +
            string.Join("\n  ", offenders));

        // ...and the recorded prose says what this test just recomputed.
        Assert.Contains(carrier, RealProseArmMeasurement.WhyItDidNotGeneralise, StringComparison.Ordinal);
        Assert.Contains("ZERO times", RealProseArmMeasurement.WhyItDidNotGeneralise, StringComparison.Ordinal);
    }

    // ── 4. the production posture matches the verdict ────────────────────────────────────────────

    /// <summary>
    /// THE ARM IS RECORDED AS REJECTED AND SHIPPED AS OFF, and the composed real-prose prompt carries
    /// neither arm's text in either language.
    ///
    /// The byte-identity gate in <c>ProofreadPromptArmTests</c> proves the OFF path equals the legacy
    /// path on the composer; this proves the same thing where the measurement happened - on the real
    /// chunked surface, through the harness's own arm resolver, in both language branches. A leak that
    /// somehow reached only the real-prose route would otherwise be invisible to both files.
    /// </summary>
    [Fact]
    public void TheDefaultArm_IsOff_AndNoArmsTextReachesTheRealProsePrompt()
    {
        // The verdict says do not ship, in those words, before anything else is read.
        Assert.StartsWith("DO NOT SHIP", RealProseArmMeasurement.Verdict, StringComparison.Ordinal);
        Assert.Equal(1, RealProseArmMeasurement.RepsPerCell);
        Assert.Equal(64, RealProseArmMeasurement.ChunkCalls);

        // The class default and the harness's OFF arm agree, so "Off" in a run record is the shipped
        // posture and not a third thing.
        Assert.False(new ProofreadPromptOptions().OverlapReferentLicence);
        Assert.False(RealProseHarness.OptionsFor(ProofreadPromptArm.Off).OverlapReferentLicence);
        Assert.True(RealProseHarness.OptionsFor(ProofreadPromptArm.OverlapReferentLicence)
            .OverlapReferentLicence);

        var off = RealProseHarness.PromptFactoryFor(ProofreadPromptArm.Off);
        var on = new PromptFactory(
            Options.Create(new ProofreadPromptOptions { OverlapReferentLicence = true }));

        var overlap = RealProseHarness
            .Chunk(RealProsePrecisionFixtures.ById(RealProsePrecisionFixtures.DialogueMidId))[1]
            .OverlapPrefix;
        Assert.False(string.IsNullOrWhiteSpace(overlap));

        var checkedLanguages = 0;
        foreach (var (language, licence) in new[]
                 {
                     (RealProsePrecisionFixtures.Language, PromptFactory.OverlapReferentLicenceHe),
                     ("en-US", PromptFactory.OverlapReferentLicenceEn),
                 })
        {
            checkedLanguages++;
            var offPrompt = off.BuildProofreadChunkPrompt(language, characters: null, overlap);
            var onPrompt = on.BuildProofreadChunkPrompt(language, characters: null, overlap);

            // NON-VACUITY: the ON prompt DOES carry it, so the absence below is a live search.
            Assert.Contains(licence, onPrompt, StringComparison.Ordinal);

            Assert.DoesNotContain(PromptFactory.OverlapReferentLicenceHe, offPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(PromptFactory.OverlapReferentLicenceEn, offPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("[RESOLVED_REFERENT]", offPrompt, StringComparison.Ordinal);
        }

        Assert.Equal(2, checkedLanguages);
    }

    /// <summary>
    /// THE CLOSURE IS FILED AGAINST THE ARM IT CLOSES, and it did not quietly re-pin anything.
    ///
    /// The retired-intervention row's <c>MeasuredSideEffect</c> was an OPEN lead (the synthetic 38%
    /// cut). It must now carry the real-prose numbers, say the lead is CLOSED, and still refuse to read
    /// as an acceptance. And the two chunked-agreement defects this plan never touched must still be
    /// pinned exactly where the previous session left them - the plan that closed this lead had no
    /// mandate to move an agreement bar, and a closure that moved one would be the re-pin nobody
    /// performed.
    /// </summary>
    [Fact]
    public void TheClosedLead_CitesThisMeasurement_AndTheAgreementDefectsAreStillPinnedWhereTheyWere()
    {
        var lead = ProofreadStandingFloor.RetiredInterventionById(
            "referent-carry-forward.ARM_A.OverlapLicence");

        // CLOSED, with the real-prose numbers and the reason, not merely re-worded.
        Assert.Contains("CLOSED", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains(RealProseArmMeasurement.Plan, lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains(RealProsePrecisionFixtures.SyntheticDominantConstruction,
            lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains("47 -> 39", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains("5 of 12", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains(nameof(RealProseArmMeasurement), lead.MeasuredSideEffect, StringComparison.Ordinal);

        // ...and it still cannot be read as a reason the arm should have shipped.
        Assert.Contains("NOT grounds", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains("REJECTED", lead.Verdict, StringComparison.Ordinal);

        // THE AGREEMENT DEFECTS, untouched. This plan measured PRECISION; it may not move a recall bar.
        foreach (var fixtureId in new[]
                 {
                     ChunkedAgreementFixtures.SeparatedAndDilutedId,
                     ChunkedAgreementFixtures.AntecedentInOverlapId
                 })
        {
            var entry = ProofreadStandingFloor.ForFixture(fixtureId);
            Assert.Equal(FloorOutcome.KnownDefect, entry.Outcome);
            Assert.Equal(0, entry.MeasuredHits);
            Assert.Equal(ProofreadStandingFloor.SingleCaseMinimumRuns, entry.MeasuredRuns);
        }

        // The residue is recorded, and is recorded as NOT a verdict - the single most likely thing to
        // be misread out of this session, because it is the one family that DID move.
        Assert.Contains("NOT THIS PLAN'S RESULT", RealProseArmMeasurement.UnownedResidue,
            StringComparison.Ordinal);
        Assert.Contains("NOT ACCEPTED", RealProseArmMeasurement.UnownedResidue, StringComparison.Ordinal);
        Assert.True(RealProseArmMeasurement.UnownedResidue.Length > 200,
            "the residue is too short to be findable as a lead rather than as an aside");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Occurrences of <paramref name="needle"/> that START a word: the preceding character is not a
    /// Hebrew letter. Without this, the manuscript's זמן ה... ("the time ...") counts as the carrier
    /// construction מן ה... and the whole carrier-absence finding inverts.
    /// </summary>
    private static int StandaloneOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var from = 0;
        while (true)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0) return count;
            if (at == 0 || !IsHebrewLetter(haystack[at - 1])) count++;
            from = at + 1;
        }
    }
}
