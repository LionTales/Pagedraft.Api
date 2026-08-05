using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Xunit;

// Bound through using ALIASES for the same reason as ProofreadStandingFloorTests: this file must NOT
// pull Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and being outside it is the whole point of this file's location.
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;
using GoldPromptSurfaces = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurfaces;
using ProofreadQualityTests = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC, NO-MODEL, NO-GPU gate on the SHIP-NOTHING outcome of the referent-carry-forward plan.
///
/// WHAT HAPPENED. Two prompt-side arms were built behind a default-off switch and measured in one
/// Ollama session on the model the floor names, n=15 per fixture per arm, zero spread in all twelve
/// cells. BOTH failed the decision rule's first condition: the acceptance fixture
/// (<c>chunked-agree-02</c>) stayed at 0/15 under each. Nothing shipped. ARM B was REVERTED outright;
/// ARM A was later re-landed VERBATIM behind a default-off switch so its measured precision side effect
/// could be re-measured on real prose in-process (that re-measurement closed the lead negative on
/// 2026-08-05 - see <c>RealProseArmMeasurement</c>). Either way the DEFAULT proofread path is
/// byte-identical to the pre-arm one, which is what every assertion below is about.
///
/// WHAT THIS FILE THEREFORE HAS TO PROVE, and why each half is not optional:
///
///  1. THE DEFAULT PATH IS THE ONLY PATH. Every per-chunk instruction a real chunked run composes is
///     exactly what the three-argument production builder composes for that chunk's own overlap, and
///     neither retired arm's text occurs anywhere in it. ARM A's switch now EXISTS (default off), so
///     this is no longer close to true by construction: it is the assertion that catches a default
///     flipped on, a stray revival of ARM B, or a re-implementation landed without a plan. It is the
///     end-to-end complement of the composer-level kill-switch test in <c>ProofreadPromptArmTests</c>.
///  2. THE RECORD OF THE NEGATIVE IS COHERENT AND TIED TO THE FLOOR. A retired arm's per-fixture hits
///     must AGREE with the floor's own pinned hits - a disagreement would mean either a re-pin nobody
///     performed or a recorded number nobody measured - and both defect entries must still be
///     <see cref="FloorOutcome.KnownDefect"/>. Recording "we tried and failed" while quietly moving the
///     bar is precisely how the next planner inherits a false premise.
///  3. THE PREMISE CORRECTION SURVIVES A PROMPT EDIT. It is a claim ABOUT a specific prompt line, so it
///     is worth nothing if that line changes underneath it. The line is pinned verbatim here.
///  4. THE STRUCTURAL LIMIT ON THE RUN IS STATED, NOT ASSUMED. Decision-rule condition 4 (no
///     over-correction regression on the gold <c>agree-preserve.*</c> bars) could not have been
///     violated by either arm, because the gold surface is composed through the three-argument builder
///     and no per-chunk intervention can reach it. That is proved here rather than believed.
///
/// Every "no offenders" assertion proves its population non-empty first - the vacuity class this corpus
/// has now been bitten by repeatedly, and the one that would make each of the four claims above read
/// exactly as it reads today while checking nothing.
/// </summary>
public class ProofreadStandingFloorRetiredInterventionTests
{
    /// <summary>
    /// The [CONTEXT_BEFORE] instruction ARM A extended, and the [CHARACTER_REGISTER] instruction the
    /// premise correction is ABOUT, pinned verbatim per language. See
    /// <see cref="TheAnchorLines_TheRetiredWorkDependsOn_AreStillVerbatimInTheShippedPrompt"/>.
    /// </summary>
    private static readonly (string Language, string ContextBefore, string CharacterRegister)[] AnchorLines =
    {
        ("he-IL",
            "אם מופיע [CONTEXT_BEFORE]...[/CONTEXT_BEFORE] — זהו הקשר בלבד לצורך המשכיות. אל תתקן אותו ואל תכלול אותו בפלט.",
            "אם מופיע [CHARACTER_REGISTER] — השתמש בו לאימות התאמת מין (נטיית פועל, תואר, כינוי), עקביות כתיב שמות, וזיהוי כינויי גוף."),
        ("en-US",
            "If [CONTEXT_BEFORE]...[/CONTEXT_BEFORE] is present — it is read-only context for continuity. Do not correct it or include it in your output.",
            "If [CHARACTER_REGISTER] is present — use it to verify name spelling consistency, pronoun agreement, and gender-specific language."),
    };

    /// <summary>
    /// Distinctive fragments of what each retired arm RENDERED, taken from the record's own
    /// <c>RenderedChange</c> text rather than restated, so a row rewritten to describe a different
    /// intervention cannot leave this search pointed at the old one.
    /// </summary>
    private static readonly string[] RetiredMarkers =
    {
        "[RESOLVED_REFERENT]",                                   // ARM B's section name
        "נושא הקטע שיש לתקן",                                     // ARM B's Hebrew subject line
        "Subject of the text to correct",                        // ARM B's English subject line
        "השתמש בו גם כדי לזהות אל מי מתייחסים כינויי הגוף",        // ARM A's Hebrew added clause
        "Also use it to resolve which character the pronouns",   // ARM A's English added clause
    };

    // ── 1. the default path is the only path ─────────────────────────────────────────────────────

    /// <summary>
    /// THE BYTE-IDENTITY GATE, on the REAL production path rather than at the composer.
    ///
    /// Every per-chunk instruction of a default run over EVERY standing fixture equals, byte for byte,
    /// what <c>PromptFactory.BuildProofreadChunkPrompt(language, characters, overlapPrefix)</c> composes
    /// for that chunk's own overlap - the three-argument call, which is the only call that exists again.
    /// The single-shot control is included deliberately: it composes through <c>GetAnalysisPrompt</c>,
    /// and with a context carrying only Characters the two coincide, so it is covered by the same
    /// equality instead of being excused from it.
    ///
    /// THIS IS WHAT THE SHIP-NOTHING OUTCOME IS WORTH. The floor's twelve measured cells - including the
    /// OFF arm that reproduced it - describe THIS prompt. If the composed instruction moves, the numbers
    /// in <see cref="ProofreadStandingFloor.ChunkedAgreement"/> and in
    /// <see cref="ProofreadStandingFloor.RetiredInterventions"/> stop being about the shipped path, and
    /// nothing else in the deterministic suite would say so.
    /// </summary>
    [Fact]
    public async Task ADefaultRun_ComposesTheThreeArgumentInstruction_ForEveryChunkOfEveryFixture()
    {
        var factory = new PromptFactory();
        var checkedCalls = 0;
        var overlapCarryingCalls = 0;

        foreach (var fixture in ChunkedAgreementFixtures.All)
        {
            var run = await ChunkedAgreementHarness.RunAsync(fixture);
            Assert.NotEmpty(run.Calls);
            Assert.Empty(run.Failures);

            foreach (var call in run.Calls)
            {
                Assert.NotEqual(RecordingChunkRouter.UnknownChunkIndex, call.ChunkIndex);

                var overlap = run.Chunks[call.ChunkIndex].OverlapPrefix;
                var expected = factory.BuildProofreadChunkPrompt(
                    fixture.Language,
                    new CharacterRegister { Characters = fixture.Register },
                    overlap);

                Assert.Equal(expected, call.Instruction);
                checkedCalls++;
                if (overlap is not null) overlapCarryingCalls++;
            }
        }

        // NON-VACUITY, twice over. A fixture set that produced no call, or only first chunks, would
        // satisfy the loop above while leaving the overlap-carrying shape - the one an arm targeted -
        // entirely unexercised.
        Assert.True(checkedCalls >= ChunkedAgreementFixtures.All.Count,
            $"only {checkedCalls} per-chunk instructions were compared against the three-argument builder");
        Assert.True(overlapCarryingCalls > 0,
            "no chunk carried a [CONTEXT_BEFORE] overlap, so the equality was never checked on the shape " +
            "ARM A was built to change");
    }

    /// <summary>
    /// NEITHER RETIRED ARM RENDERS ANYWHERE. The complement of the equality above, and not redundant
    /// with it: equality says "the instruction is the legacy one", this says "the specific text we
    /// removed is gone", and only the second one names what to look for if it ever comes back - a
    /// revived switch, a copied prompt fragment, a re-implementation landed without a plan.
    ///
    /// THE VACUITY GUARD IS THE INTERESTING PART. A search for absent strings passes trivially against
    /// an empty corpus, a truncated prompt, or a typo'd needle. So the same prompts are first proved to
    /// CONTAIN the anchor line ARM A extended - if that is present and the extension is not, the search
    /// is demonstrably live and looking in the right place.
    /// </summary>
    [Fact]
    public async Task NoRetiredArmsText_OccursInAnyComposedPromptOnTheDefaultPath()
    {
        Assert.NotEmpty(ProofreadStandingFloor.RetiredInterventions);
        Assert.NotEmpty(RetiredMarkers);

        // The markers really are quoted by the record, so this list cannot drift into describing an
        // intervention that was never recorded (or miss one that was rewritten).
        var recorded = string.Join("\n", ProofreadStandingFloor.RetiredInterventions.Select(r => r.RenderedChange));
        foreach (var marker in RetiredMarkers)
            Assert.Contains(marker, recorded, StringComparison.Ordinal);

        var offenders = new List<string>();
        var promptsSearched = 0;
        var anchorsFound = 0;

        foreach (var fixture in ChunkedAgreementFixtures.All)
        {
            var run = await ChunkedAgreementHarness.RunAsync(fixture);
            foreach (var call in run.Calls)
            {
                promptsSearched++;

                // THE VACUITY GUARD: the line ARM A extended is present, so a search of this same string
                // for ARM A's extension is a search that could have found something.
                if (call.Instruction.Contains(AnchorLines[0].ContextBefore, StringComparison.Ordinal))
                    anchorsFound++;

                foreach (var marker in RetiredMarkers)
                    if (call.Instruction.Contains(marker, StringComparison.Ordinal))
                        offenders.Add($"{fixture.Id} chunk {call.ChunkIndex}: '{marker}'");
            }
        }

        Assert.True(promptsSearched > 0, "no composed prompt was searched at all");
        Assert.True(anchorsFound == promptsSearched,
            $"only {anchorsFound} of {promptsSearched} composed prompts carried the [CONTEXT_BEFORE] anchor " +
            "line, so the absence assertion below is searching prompts that may not be the ones the " +
            "retired arms would have modified");
        Assert.True(offenders.Count == 0,
            "A retired referent-carry-forward arm's text is rendering on the DEFAULT proofread path. Both " +
            "arms were measured and REJECTED (see ProofreadStandingFloor.RetiredInterventions), and the " +
            "standing floor's twelve cells were measured WITHOUT them, so anything reintroducing one " +
            "invalidates the bar rather than improving it:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// THE ANCHOR LINES BOTH RETIRED ARMS AND THE PREMISE CORRECTION DEPEND ON, pinned verbatim.
    ///
    /// The <see cref="PremiseCorrection"/> recorded next to the floor is a claim about what ONE Hebrew
    /// line does and does not license. Its whole value is that a future plan does not re-derive the
    /// wrong reading from the source text - so if that line is ever reworded, the correction is stale
    /// and must be re-read against the new wording rather than left standing. Failing HERE, at the
    /// moment of the edit, is the only way that happens.
    ///
    /// The [CONTEXT_BEFORE] line is pinned for the same reason from the other direction: ARM A's
    /// recorded RenderedChange is an extension OF it, so a reader reconstructing that arm needs the
    /// thing it was appended to.
    /// </summary>
    [Fact]
    public void TheAnchorLines_TheRetiredWorkDependsOn_AreStillVerbatimInTheShippedPrompt()
    {
        var factory = new PromptFactory();
        Assert.NotEmpty(AnchorLines);

        foreach (var (language, contextBefore, characterRegister) in AnchorLines)
        {
            var prompt = factory.BuildProofreadChunkPrompt(language, characters: null, overlapPrefix: null);

            Assert.Equal(1, Occurrences(prompt, contextBefore));
            Assert.Equal(1, Occurrences(prompt, characterRegister));
        }

        // The premise correction really is about the line pinned above - not about some other clause -
        // so the pin and the correction cannot drift apart.
        var premise = ProofreadStandingFloor.PremiseCorrections
            .Single(p => p.Id == "proofread-he.character-register-pronoun-clause");
        Assert.Contains(AnchorLines[0].CharacterRegister, premise.Correction, StringComparison.Ordinal);
        Assert.Contains("זיהוי כינויי גוף", premise.Claim, StringComparison.Ordinal);
    }

    // ── 2. the record of the negative is coherent, and tied to the floor ─────────────────────────

    /// <summary>
    /// EVERY RETIRED INTERVENTION IS WELL-FORMED, and every field that would let a reader mistake a
    /// recorded number for a measured one is checked.
    /// </summary>
    [Fact]
    public void EveryRetiredIntervention_IsWellFormed_AndCoversTheWholeFixtureCorpus()
    {
        var retired = ProofreadStandingFloor.RetiredInterventions;
        Assert.NotEmpty(retired);

        var ids = retired.Select(r => r.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        var fixtureIds = ChunkedAgreementFixtures.All.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(fixtureIds);

        var defects = new List<string>();
        var outcomesChecked = 0;

        foreach (var r in retired)
        {
            if (string.IsNullOrWhiteSpace(r.Plan)) defects.Add($"{r.Id}: names no plan");
            if (r.MeasuredOn != ProofreadStandingFloor.RetiredInterventionsMeasuredOn)
                defects.Add($"{r.Id}: measured {r.MeasuredOn}, but the session date is " +
                            ProofreadStandingFloor.RetiredInterventionsMeasuredOn);
            if (r.Hypothesis.Trim().Length < 60)
                defects.Add($"{r.Id}: the Hypothesis is too short to say what was believed");
            if (r.RenderedChange.Trim().Length < 60)
                defects.Add($"{r.Id}: RenderedChange must carry the intervention's text VERBATIM - the " +
                            "code is gone, so a paraphrase leaves nothing to re-implement from");
            if (r.Verdict.Trim().Length < 60)
                defects.Add($"{r.Id}: the Verdict must name the condition it failed and on what number");
            if (r.WhyItIsNotAWiringFailure.Trim().Length < 60)
                defects.Add($"{r.Id}: a negative with no evidence that the arm RENDERED is " +
                            "indistinguishable from an arm that never ran, and re-running it is then " +
                            "correct - which defeats the point of recording it");

            // The corpus, in both directions: every fixture measured, no fixture invented.
            var covered = r.PerFixture.Select(o => o.FixtureId).ToArray();
            Assert.Equal(covered.Length, covered.Distinct(StringComparer.Ordinal).Count());
            foreach (var missing in fixtureIds.Except(covered, StringComparer.Ordinal))
                defects.Add($"{r.Id}: no outcome recorded for fixture '{missing}'");
            foreach (var phantom in covered.Except(fixtureIds, StringComparer.Ordinal))
                defects.Add($"{r.Id}: records an outcome for '{phantom}', which is not a fixture");

            foreach (var o in r.PerFixture)
            {
                outcomesChecked++;
                if (o.Runs < ProofreadStandingFloor.SingleCaseMinimumRuns)
                    defects.Add($"{r.Id}/{o.FixtureId}: {o.Runs} run(s), below the " +
                                $"n>={ProofreadStandingFloor.SingleCaseMinimumRuns} single-case rule, so " +
                                "this row may not be quoted as a measured outcome");
                if (o.Hits < 0 || o.Hits > o.Runs)
                    defects.Add($"{r.Id}/{o.FixtureId}: {o.Hits} hits over {o.Runs} runs is outside its " +
                                "own denominator");
                if (o.OverCorrectionsPerRunMean is < 0)
                    defects.Add($"{r.Id}/{o.FixtureId}: negative over-correction mean");
            }
        }

        Assert.True(outcomesChecked > 0, "no per-fixture outcome was checked at all");
        Assert.True(defects.Count == 0, string.Join("\n  ", defects));

        // NON-VACUITY for the Applied flag: BOTH values really occur, so "an arm that could not reach a
        // fixture is a control for it" is a distinction the data actually draws rather than a comment.
        var allOutcomes = retired.SelectMany(r => r.PerFixture).ToArray();
        Assert.Contains(allOutcomes, o => o.Applied);
        Assert.Contains(allOutcomes, o => !o.Applied);
    }

    /// <summary>
    /// NO RETIRED ARM MOVED ANY FIXTURE - asserted against the FLOOR rather than restated, which is the
    /// only version of this claim that can fail.
    ///
    /// THE FAILURE THIS EXISTS TO CATCH is the plan's own named hazard: a half-fix recorded as a whole
    /// one, or a whole one recorded as nothing. Feeding each retired arm's measured (hits, runs) through
    /// the real <c>Evaluate</c> must reproduce the floor's standing verdicts exactly - the two pinned
    /// defects <see cref="FloorVerdict.KnownDefectReproduced"/>, the two controls
    /// <see cref="FloorVerdict.Held"/> - and NOT a single
    /// <see cref="FloorVerdict.KnownDefectMoved"/>, because a KnownDefectMoved anywhere in this record
    /// would mean an arm DID move a pinned defect and the floor was owed a re-pin nobody made.
    /// </summary>
    [Fact]
    public void EveryRetiredArmsMeasurement_ReproducesTheFloorsStandingVerdict_SoNoRePinWasOwed()
    {
        var retired = ProofreadStandingFloor.RetiredInterventions;
        Assert.NotEmpty(retired);

        var evaluated = 0;
        var reproduced = 0;
        var held = 0;
        var defects = new List<string>();

        foreach (var r in retired)
        foreach (var o in r.PerFixture)
        {
            var entry = ProofreadStandingFloor.ForFixture(o.FixtureId);
            var ev = ProofreadStandingFloor.Evaluate(entry, o.Hits, o.Runs);
            evaluated++;

            if (ev.IsFailure)
            {
                defects.Add($"{r.Id}/{o.FixtureId}: {ev.Message}");
                continue;
            }
            if (ev.Verdict == FloorVerdict.KnownDefectReproduced) reproduced++;
            if (ev.Verdict == FloorVerdict.Held) held++;

            // ...and the recorded hits are the floor's own, so the record cannot quietly restate a
            // different number than the bar it is filed against.
            if (o.Hits != entry.MeasuredHits)
                defects.Add($"{r.Id}/{o.FixtureId}: recorded {o.Hits}/{o.Runs} against a floor pinned at " +
                            $"{entry.MeasuredHits}/{entry.MeasuredRuns}. Either an arm moved this fixture " +
                            "and the floor is owed a re-pin, or a number nobody measured is recorded here.");
        }

        Assert.True(defects.Count == 0,
            "A retired arm's recorded measurement does NOT reproduce the standing floor:\n  " +
            string.Join("\n  ", defects));

        // NON-VACUITY: both verdict classes really occurred, so "nothing moved" was not satisfied by
        // there being nothing to evaluate or by every fixture being a control.
        Assert.True(evaluated >= ChunkedAgreementFixtures.All.Count * retired.Count);
        Assert.True(reproduced > 0, "no retired-arm measurement reproduced a pinned defect");
        Assert.True(held > 0, "no retired-arm measurement held a control");
    }

    /// <summary>
    /// THE TWO PINNED DEFECTS ARE STILL PINNED, named individually rather than counted.
    ///
    /// This is the assertion the plan's re-flooring branch turns on. Nothing shipped, so nothing was
    /// re-pinned, so <c>chunked-agree-01</c> and <c>chunked-agree-02</c> must both still be
    /// <see cref="FloorOutcome.KnownDefect"/> at 0/15 - and the record of the retired arms must SAY they
    /// were the arms measured against them, or "we tried" is an unattributed claim.
    /// </summary>
    [Fact]
    public void BothChunkedAgreementDefects_RemainPinned_AfterTheRefutedArms()
    {
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
            Assert.Equal(GoldPromptSurface.ChunkedPerChunk, entry.Surface);

            // EVERY retired arm was measured on this fixture and produced 0 - so the pin is not merely
            // unchanged, it is unchanged HAVING BEEN ATTACKED, which is what makes it worth recording.
            var attempts = ProofreadStandingFloor.RetiredInterventions
                .SelectMany(r => r.PerFixture.Where(o => o.FixtureId == fixtureId).Select(o => (r.Id, o)))
                .ToArray();
            Assert.Equal(ProofreadStandingFloor.RetiredInterventions.Count, attempts.Length);
            Assert.All(attempts, a => Assert.Equal(0, a.o.Hits));
            Assert.All(attempts, a => Assert.True(a.o.Applied,
                $"{a.Id} is recorded as inapplicable on {fixtureId}, the fixture it was built for - a " +
                "0/15 there would then be a statement about the wiring, not about the model"));
        }
    }

    /// <summary>
    /// A MEASURED SIDE EFFECT IS A LEAD, IS LABELLED AS ONE, AND IS NOW LABELLED CLOSED.
    ///
    /// ARM A cut chunked over-correction by ~38% while failing the recall condition it was built for.
    /// That was real, it was the single most likely thing to be mistaken later for a reason the arm
    /// should have shipped, and it was re-opened twice before it was measured. On 2026-08-05 it WAS
    /// measured, on real twice-proofread manuscript prose, and it did not reproduce (5 of 12 passages,
    /// p = 0.363; see <c>RealProseArmMeasurement</c>). So the row must still say which axis it is on and
    /// must still not be readable as an acceptance - and it must now also carry the CLOSURE, because an
    /// open-sounding 38% is exactly what got re-derived the last two times.
    ///
    /// Pinned by NAME: exactly one row declares a side effect today, and a second one appearing is a
    /// deliberate act somebody has to come here and make.
    /// </summary>
    [Fact]
    public void ExactlyOneRetiredArm_DeclaresASideEffect_AndItReadsAsALeadNotAnAcceptance()
    {
        var withSideEffect = ProofreadStandingFloor.RetiredInterventions
            .Where(r => !string.IsNullOrWhiteSpace(r.MeasuredSideEffect) &&
                        !r.MeasuredSideEffect.StartsWith("None", StringComparison.Ordinal))
            .ToArray();

        var lead = Assert.Single(withSideEffect);
        Assert.Equal("referent-carry-forward.ARM_A.OverlapLicence", lead.Id);
        Assert.Same(lead, ProofreadStandingFloor.RetiredInterventionById(lead.Id));

        // It states the axis, refuses to be read as an acceptance, and names the prerequisite.
        Assert.Contains("PRECISION, NOT RECALL", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains("NOT grounds", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains(nameof(ProofreadStandingFloor.GoldSurfaceCannotReachAPerChunkIntervention),
            lead.MeasuredSideEffect, StringComparison.Ordinal);

        // ...and it is CLOSED, by a named measurement, on a named surface. A row that goes back to
        // reading as open is how this lead got re-derived twice; it does not get a third turn.
        Assert.Contains("CLOSED", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains("DID NOT REPRODUCE", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Contains(nameof(RealProseArmMeasurement), lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.StartsWith("DO NOT SHIP", RealProseArmMeasurement.Verdict, StringComparison.Ordinal);

        // The recall floor it names as the trade it may not make is a REAL bar, resolvable through the
        // accessor a future run would call, not a remembered name.
        Assert.Contains("legacy93.recall", lead.MeasuredSideEffect, StringComparison.Ordinal);
        Assert.Equal("legacy93.recall", ProofreadStandingFloor.Metric("legacy93.recall").Id);

        // ...and the arm it is filed against really did fail its own acceptance condition, so the lead
        // cannot be quoted without the rejection that sits beside it.
        Assert.All(
            lead.PerFixture.Where(o => o.FixtureId == ChunkedAgreementFixtures.AntecedentInOverlapId),
            o => Assert.Equal(0, o.Hits));
        Assert.Contains("REJECTED", lead.Verdict, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProofreadStandingFloor.RetiredInterventionById("no.such.intervention"));
    }

    /// <summary>
    /// EVERY PREMISE CORRECTION IS WELL-FORMED. Short rows are the failure mode here: a correction that
    /// states what is wrong without stating what follows from it gets read as a quibble and the original
    /// premise survives anyway.
    /// </summary>
    [Fact]
    public void EveryPremiseCorrection_SaysWhatWasClaimed_WhatIsTrue_AndWhatFollows()
    {
        var corrections = ProofreadStandingFloor.PremiseCorrections;
        Assert.NotEmpty(corrections);

        var ids = corrections.Select(c => c.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        var defects = new List<string>();
        foreach (var c in corrections)
        {
            if (c.Claim.Trim().Length < 40) defects.Add($"{c.Id}: the Claim is too short to be the claim");
            if (c.Correction.Trim().Length < 40) defects.Add($"{c.Id}: the Correction is too short");
            if (c.Evidence.Trim().Length < 40)
                defects.Add($"{c.Id}: no evidence, so this is an opinion replacing another opinion");
            if (c.Consequence.Trim().Length < 40)
                defects.Add($"{c.Id}: no Consequence - a correction that does not say what changes " +
                            "downstream leaves the original premise standing in every place it is used");
        }

        Assert.True(defects.Count == 0, string.Join("\n  ", defects));
    }

    // ── 3. the structural limit on what the session could measure ────────────────────────────────

    /// <summary>
    /// DECISION-RULE CONDITION 4 WAS STRUCTURALLY VACUOUS, proved rather than asserted.
    ///
    /// The condition was "no over-correction regression on the <c>agree-preserve.*</c> bars". Those bars
    /// are measured on gold rows built by <c>ProofreadQualityTests.BuildGoldRequest</c>, which composes
    /// its instruction through the THREE-ARGUMENT <c>BuildProofreadChunkPrompt(language, characters,
    /// overlapPrefix: null)</c>. A per-chunk prompt intervention has no way into that call, whatever it
    /// is switched to - so condition 4 could not have been violated and its holding is not evidence
    /// about either arm.
    ///
    /// WHY IT MATTERS BEYOND THE POST-MORTEM: the precision lead ARM A left behind lives on exactly the
    /// axis those bars measure, so any follow-up has to close this gap FIRST. Stating it as a gated
    /// fact, tied to the real request builder, is what keeps that prerequisite from being rediscovered.
    /// </summary>
    [Fact]
    public void TheGoldSurface_IsComposedByTheThreeArgumentBuilder_SoNoPerChunkInterventionCanReachIt()
    {
        var factory = new PromptFactory();
        var gold = ProofreadQualityTests.LoadProofreadGold();
        Assert.True(gold.Length > 0,
            "proofread-gold.json loaded as an EMPTY array (LoadProofreadGold returns Array.Empty rather " +
            "than throwing when the file is missing from the output directory), so every assertion below " +
            "would pass by iterating nothing.");

        var registerCarrying = gold
            .Where(c => GoldPromptSurfaces.SurfaceOf(c) == GoldPromptSurface.ProductionLongPlusShort)
            .ToArray();
        Assert.NotEmpty(registerCarrying);

        // The bars condition 4 named really do sit on this surface and on this subset.
        var bar = ProofreadStandingFloor.Metric("agree-preserve.overCorrectionRate");
        Assert.Equal(GoldPromptSurface.ProductionLongPlusShort, bar.Surface);
        Assert.Contains(registerCarrying, c => c.Id.StartsWith(bar.Subset, StringComparison.Ordinal));

        var checkedCases = 0;
        foreach (var c in registerCarrying)
        {
            var request = ProofreadQualityTests.BuildGoldRequest(c);
            Assert.NotNull(request.Instruction);

            // BYTE-IDENTICAL to the three-argument builder with NO overlap: there is no seam a per-chunk
            // intervention could have rendered through.
            var threeArg = factory.BuildProofreadChunkPrompt(
                c.Language,
                new CharacterRegister { Characters = c.CharacterRegister! },
                overlapPrefix: null);
            Assert.Equal(threeArg, request.Instruction);

            foreach (var marker in RetiredMarkers)
                Assert.DoesNotContain(marker, request.Instruction!, StringComparison.Ordinal);

            checkedCases++;
        }

        Assert.True(checkedCases > 0, "no register-carrying gold case was checked");

        // The recorded statement of this fact says what the test just proved.
        Assert.Contains("3-argument", ProofreadStandingFloor.GoldSurfaceCannotReachAPerChunkIntervention,
            StringComparison.Ordinal);
        Assert.Contains("agree-preserve", ProofreadStandingFloor.GoldSurfaceCannotReachAPerChunkIntervention,
            StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
