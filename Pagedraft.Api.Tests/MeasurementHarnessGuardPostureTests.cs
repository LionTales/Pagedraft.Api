using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

// Bound through a using ALIAS, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and this file has to run inside the GPU-safe sweep. Same rule (and
// same reason) as HebrewOrthographyShapeGuardTests and ChunkedAgreementFixtureTests.
using ProofreadQualityTests = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// MeasurementHarnessGuardPostureTests - WHICH SHAPE-GUARD POSTURE EACH MEASURING SURFACE RUNS UNDER.
//
// THE DEFECT THIS EXISTS TO PREVENT. The orthographic-impossibility shape guard ships ON as the
// HebrewStyleOptions CLASS DEFAULT, so every `new SuggestionDiffService()` silently inherits it -
// including the harnesses whose published numbers are counts of what the MODEL proposed. A layer that
// DELETES model output sitting inside a model measurement subtracts from the measurement, and it does
// so without a single test turning red, because the recorded numbers are static constants and nothing
// re-runs the corpus to compare against them.
//
// So each measuring surface now names its posture at ONE construction site, and this file pins the
// posture the way it actually reaches the numbers rather than restating the literal beside it:
//   - the two deterministic harnesses are driven END TO END with a replay that corrupts a real chunk
//     token into the exact shape the guard reaches, and the corrupted edit is required to SURVIVE into
//     the run's suggestion list. That reads the construction site inside the harness, so putting
//     `new SuggestionDiffService()` back turns this red even if the factory below still says OFF;
//   - the gold-scoring surface has no offline end-to-end path (its diff is only ever run against a
//     LIVE model response), so it is pinned one level lower, on the behaviour of the service its one
//     factory returns. That is weaker and is called out here rather than dressed up.
//
// NON-VACUITY IS AN A/B ON IDENTICAL TEXT, not an appeal to the input looking dangerous: every case
// re-diffs the SAME before/after pair through a class-default service and requires the guard to drop
// it there. "It survived" is therefore a fact about the posture, never about an input that never
// tripped the guard.
//
// The ON side needs no pin here: the surfaces that want production parity deliberately construct with
// the parameterless constructor, i.e. they FOLLOW the class default, and the class default is already
// pinned by HebrewOrthographyShapeGuardTests.TheGuardShipsON_AndTheParameterlessServiceTakesThatSamePosture.
// ---------------------------------------------------------------------------------------------
public class MeasurementHarnessGuardPostureTests
{
    /// <summary>
    /// The Hebrew final form the shipped rule is about, taken from the corpus's own recorded instance
    /// rather than typed in again: <see cref="RealProseNonWordResidue.ShapeGuardReaches"/> is COMPUTED
    /// from the guard, so if the rule is ever widened past this letter the seed below moves with it.
    /// </summary>
    private static char TheOffendingFinalForm()
    {
        var recorded = Assert.Single(RealProseNonWordResidue.ShapeGuardReaches);

        // צמצם -> צמץם: the one letter that differs is the final form written mid-word.
        var offending = recorded.Suggested
            .Where((c, i) => i >= recorded.Original.Length || recorded.Original[i] != c)
            .Distinct()
            .ToArray();

        return Assert.Single(offending);
    }

    /// <summary>
    /// Corrupt one real token of <paramref name="chunkText"/> into the guard's one impossible shape.
    /// The LONGEST Hebrew token is chosen on purpose: a longest token cannot be a proper substring of
    /// any other token, so replacing every occurrence can only ever hit whole words.
    /// </summary>
    private static (string Token, string Corrupted) SeedAnImpossibleShape(string chunkText)
    {
        var token = RealProseNonWordResidue.HebrewTokens(chunkText)
            .Where(t => t.Length >= 3)
            .OrderByDescending(t => t.Length)
            .ThenBy(t => t, StringComparer.Ordinal)
            .FirstOrDefault();

        Assert.False(token is null,
            "the chunk carried no Hebrew token long enough to corrupt, so this seed would be vacuous");

        var corrupted = token![0] + TheOffendingFinalForm().ToString() + token[2..];

        // The seed really is the thing the guard reaches, and the token it replaced really was clean.
        Assert.False(HebrewOrthographyShapeGuard.IsImpossible(token));
        Assert.True(HebrewOrthographyShapeGuard.WouldDrop(token, corrupted, out _),
            $"the seeded corruption [{token}] -> [{corrupted}] is not one the guard reaches, so a run " +
            "that kept it would prove nothing about the posture");

        return (token, corrupted);
    }

    /// <summary>
    /// THE A/B THAT MAKES "IT SURVIVED" MEAN SOMETHING. The same before/after pair, re-diffed through a
    /// service on the CLASS DEFAULT, must lose the edit. Without this, a harness whose replay silently
    /// stopped corrupting anything would satisfy every assertion above it.
    /// </summary>
    private static void AssertTheClassDefaultWouldHaveDroppedIt(
        string before, string after, string corrupted, string surface)
    {
        var control = new SuggestionDiffService()
            .ComputeProofreadSuggestions(before, after, out var outcome);

        Assert.True(outcome.Ran, $"{surface}: the class default is no longer guard-ON, so this " +
                                 "control cannot distinguish the two postures");
        Assert.True(outcome.DroppedCount >= 1,
            $"{surface}: the class-default control dropped nothing, so the seeded corruption never " +
            "reached the guard and the surviving-edit assertion is vacuous");
        Assert.DoesNotContain(control, s =>
            (s.SuggestedText ?? "").Contains(corrupted, StringComparison.Ordinal));
    }

    // ── the two deterministic harnesses, pinned END TO END ───────────────────────────────────────

    /// <summary>
    /// REAL-PROSE HARNESS: GUARD OFF, proved through the harness rather than beside it. A model that
    /// corrupts one token into the impossible shape must still be CHARGED for it in
    /// <c>RealProseRun.EditCount</c> / <c>EditComposition</c>, which is what
    /// <see cref="RealProseArmMeasurement"/>'s precision matrix and CORRUPTION family are counts of.
    ///
    /// This is a claim about the harness's CONFIGURATION. No model is run and nothing is re-measured.
    /// </summary>
    [Fact]
    public async Task TheRealProseHarness_ChargesTheModelForAnImpossibleShape_RatherThanGuardingItAway()
    {
        string? before = null, after = null, corrupted = null;

        var run = await RealProseHarness.RunAsync(
            RealProsePrecisionFixtures.ById(RealProsePrecisionFixtures.NarrationNoQuotesId),
            replay: capture =>
            {
                // Corrupt exactly ONE chunk, so the run stays a single, attributable edit.
                if (corrupted is not null) return capture.WrappedInputText;

                var (token, bad) = SeedAnImpossibleShape(capture.ChunkText);
                before = capture.ChunkText;
                after = before.Replace(token, bad, StringComparison.Ordinal);
                corrupted = bad;

                return capture.WrappedInputText.Replace(token, bad, StringComparison.Ordinal);
            });

        Assert.False(corrupted is null, "the replay never fired, so this run measured nothing");
        // VOID ON A PER-CHUNK THROW, which RealProseRun.Failures exists to make impossible to ignore: a
        // chunk whose model call threw is merged as its ORIGINAL text, so a run that lost a LATER chunk
        // would still carry the seeded edit and green this test on numbers the harness declares void.
        Assert.Empty(run.Failures);
        Assert.True(run.RanChunked, "the run took the single-shot route, which is not the surface " +
                                    "the arm measurement was produced on");

        Assert.Contains(run.SubstantiveSuggestions, s =>
            (s.SuggestedText ?? "").Contains(corrupted!, StringComparison.Ordinal));

        AssertTheClassDefaultWouldHaveDroppedIt(before!, after!, corrupted!, "RealProseHarness");
    }

    /// <summary>
    /// CHUNKED-AGREEMENT HARNESS: GUARD OFF, same proof shape. g1's over-correction column is a count
    /// of edits the model should not have made; a mechanically impossible Hebrew word is the clearest
    /// member of that class, and a harness that deleted it before counting would flatter the model at
    /// precisely the metric the harness exists to read.
    /// </summary>
    [Fact]
    public async Task TheChunkedAgreementHarness_ChargesTheModelForAnImpossibleShape_RatherThanGuardingItAway()
    {
        string? before = null, after = null, corrupted = null;

        var run = await ChunkedAgreementHarness.RunAsync(
            ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.SeparatedAndDilutedId),
            replay: capture =>
            {
                if (corrupted is not null) return capture.WrappedInputText;

                var (token, bad) = SeedAnImpossibleShape(capture.ChunkText);
                before = capture.ChunkText;
                after = before.Replace(token, bad, StringComparison.Ordinal);
                corrupted = bad;

                return capture.WrappedInputText.Replace(token, bad, StringComparison.Ordinal);
            });

        Assert.False(corrupted is null, "the replay never fired, so this run measured nothing");
        // Same reason as the real-prose test above: ChunkedAgreementRun.Failures voids a run's readings.
        Assert.Empty(run.Failures);
        Assert.True(run.RanChunked, "the run took the single-shot route, which is not the surface g1 " +
                                    "scores");

        Assert.Contains(run.SubstantiveSuggestions, s =>
            (s.SuggestedText ?? "").Contains(corrupted!, StringComparison.Ordinal));

        AssertTheClassDefaultWouldHaveDroppedIt(before!, after!, corrupted!, "ChunkedAgreementHarness");
    }

    // ── the surfaces with no offline end-to-end path, pinned on the service they construct ───────

    /// <summary>
    /// THE REMAINING MEASURING SURFACES, pinned on the BEHAVIOUR of the service each one's single
    /// construction site returns. Weaker than the two tests above and deliberately labelled as such:
    /// this proves the factory's posture, not that the call site still calls the factory. The gold
    /// scorer and the two live drivers only ever diff a LIVE model response, so there is no offline
    /// run to drive them through; compilation is what ties the call sites to these factories.
    ///
    /// <c>ProofreadQualityTests.GoldScoringDiffService</c> is the one surface where the guard's reach
    /// is NOT already known to be zero: <c>HebrewOrthographyShapeGuardTests.TheGuard_DropsNothingOnEitherGoldCorpusIdealOutput</c>
    /// and <c>.TheGuard_DropsNoDeclaredGoldCorrection</c> prove it is inert on the corpus's inputs and
    /// IDEAL outputs, which is not the text this diff is ever run against.
    /// </summary>
    [Fact]
    public void EveryMeasuringSurfacesDiffService_LetsTheImpossibleShapeThrough()
    {
        var recorded = Assert.Single(RealProseNonWordResidue.ShapeGuardReaches);
        var before = $"הוא {recorded.Original} את הפער בין השניים.";
        var after = $"הוא {recorded.Suggested} את הפער בין השניים.";

        var surfaces = new (string Name, SuggestionDiffService Service)[]
        {
            ("RealProseHarness.MeasurementDiffService", RealProseHarness.MeasurementDiffService()),
            ("ChunkedAgreementHarness.MeasurementDiffService", ChunkedAgreementHarness.MeasurementDiffService()),
            ("ProofreadQualityTests.GoldScoringDiffService", ProofreadQualityTests.GoldScoringDiffService()),
        };

        foreach (var (name, service) in surfaces)
        {
            var suggestions = service.ComputeProofreadSuggestions(before, after, out var outcome);

            Assert.False(outcome.Ran,
                $"{name} now runs the shape guard, so a measurement taken through it no longer counts " +
                "everything the model proposed");
            Assert.Equal(0, outcome.DroppedCount);
            Assert.Contains(suggestions, s =>
                (s.SuggestedText ?? "").Contains(recorded.Suggested, StringComparison.Ordinal));
        }

        // The same A/B as the harness tests: on identical text the class default loses the edit.
        AssertTheClassDefaultWouldHaveDroppedIt(before, after, recorded.Suggested, "the class default");
    }
}
