using System;
using System.Collections.Generic;
using System.Linq;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// THE FLOOR'S THIRD CHARACTERIZATION SURFACE: interventions ALREADY MEASURED AGAINST IT AND REJECTED.
//
// WHY THIS EXISTS AT ALL. A FloorOutcome.KnownDefect entry says "this fails today". It does not say
// what has already been TRIED against it, so nothing stops the next plan from paying for a GPU session
// that has already been paid for - and the cheapest, most attractive candidates are exactly the ones
// most likely to be re-derived from the same reasoning that produced them the first time. A measured
// dead end is a deliverable; it stays one only if it is recorded next to the bar it failed to move.
//
// WHAT A ROW IS. One prompt-side intervention, built behind a default-off switch, measured at the
// corpus's own n>=15 single-case rule, and REJECTED against a decision rule stated BEFORE the run.
// The row carries the intervention's rendered text VERBATIM, so a future planner can see exactly what
// the model was shown - the intervention itself no longer exists in the tree, and a paraphrase of a
// prompt is not a prompt.
//
// WHAT A ROW IS NOT. It is not a bar and nothing is gated against it: no run re-measures a retired
// arm. What IS gated (ProofreadStandingFloorRetiredInterventionTests) is that the record stays
// internally coherent, stays tied to the fixtures and floor entries it cites, and stays HONEST about
// the arms not having moved anything - a retired arm whose recorded hits disagreed with the floor's
// own pinned hits would be a re-pin nobody performed.
//
// WHERE THE PRODUCTION CODE IS NOW, AND WHY IT DIFFERS PER ARM (updated 2026-08-05).
//   ARM B is GONE. It was refuted on both axes, bought nothing anywhere, and keeping a dead path behind
//   a config flag is the shape the plan warned against. Its verbatim rendered text survives only here.
//   ARM A EXISTS, DEFAULT OFF (Ai:ProofreadPrompt:OverlapReferentLicence). Its recall verdict is the
//   same rejection, but its measured PRECISION side effect needed a real-prose re-measurement, which
//   required the arm to be composable in-process; the follow-up plan re-landed it VERBATIM for that
//   purpose and the re-measurement then closed the lead negative (see MeasuredSideEffect below). It was
//   kept rather than re-reverted because ProofreadPromptArmTests pins the OFF path byte-identical to
//   the pre-arm prompt, so production BEHAVIOUR is unchanged either way, while a second revert would
//   make a third re-implementation - not a third measurement - the price of ever asking again.
// What the arms bought is preserved HERE regardless - their verbatim rendered text and their numbers -
// which is what a re-implementation actually needs.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// What one retired intervention did on one fixture. Aligned to a <c>ChunkedAgreementFixtures</c> id so
/// the row can be compared against that fixture's standing floor entry rather than read on its own.
/// </summary>
/// <param name="FixtureId">A <c>ChunkedAgreementFixtures</c> id.</param>
/// <param name="Hits">Runs in which the agreement error was corrected in the persisted result.</param>
/// <param name="Runs">Repetitions measured (the corpus's n &gt;= 15 single-case rule).</param>
/// <param name="Applied">
/// Whether the intervention's text actually RENDERED in this fixture's prompts. FALSE is a real and
/// important value, not a defect: the single-shot control never rides the per-chunk builder, and an
/// arm that resolves from PRECEDING text renders nothing on a first chunk. A fixture the intervention
/// could not reach is a control for it, and a verdict that read such a fixture as evidence would be
/// reading the wiring rather than the model.
/// </param>
/// <param name="OverCorrectionsPerRunMean">
/// CHARACTERIZATION, and NULLABLE on purpose: null means g1 did not publish a per-fixture mean for this
/// arm, and an invented one would be indistinguishable from a measured one at the next reading.
/// </param>
public sealed record RetiredInterventionOutcome(
    string FixtureId,
    int Hits,
    int Runs,
    bool Applied,
    double? OverCorrectionsPerRunMean);

/// <summary>
/// One prompt-side intervention that was built, measured against the standing floor, and REJECTED.
/// </summary>
/// <param name="Id">Stable key, <c>plan.ARM</c> shaped.</param>
/// <param name="Plan">The plan file that scoped and measured it.</param>
/// <param name="MeasuredOn">Date of the measuring session.</param>
/// <param name="Hypothesis">What it was built on the belief that it would fix.</param>
/// <param name="RenderedChange">
/// VERBATIM what the intervention added to the composed per-chunk prompt. The whole point of keeping
/// it: the code is reverted, so this string is the only remaining statement of what the model was
/// actually shown, and a re-implementation that paraphrases it is not a re-run.
/// </param>
/// <param name="Applicability">Where the change rendered, and where it structurally could not.</param>
/// <param name="PerFixture">One row per fixture the arm was measured on.</param>
/// <param name="Verdict">Which condition of the pre-stated decision rule it failed, and on what number.</param>
/// <param name="WhyItIsNotAWiringFailure">
/// The evidence that the negative is the MODEL's and not the harness's. Required on every row: a
/// negative result with no such evidence is indistinguishable from an arm that never rendered, and
/// re-running it is then the correct thing to do - which defeats the purpose of recording it.
/// </param>
/// <param name="MeasuredSideEffect">
/// A real, measured effect on an axis the decision rule did NOT govern, or empty. This is a LEAD, never
/// a justification: shipping an arm that failed its own acceptance condition because it did something
/// else well is how a decision rule stops being one. A non-empty value must say which axis, with what
/// numbers, and what a future plan would have to do to act on it.
/// </param>
public sealed record RetiredIntervention(
    string Id,
    string Plan,
    string MeasuredOn,
    string Hypothesis,
    string RenderedChange,
    string Applicability,
    IReadOnlyList<RetiredInterventionOutcome> PerFixture,
    string Verdict,
    string WhyItIsNotAWiringFailure,
    string MeasuredSideEffect);

/// <summary>
/// A claim a plan (or a doc) asserted as established, which a later run checked and found NOT to be.
/// Recorded as data rather than as prose in a plan file because plan files are archived and this is
/// the kind of premise that gets re-derived from the same source text every time somebody reads it.
/// </summary>
/// <param name="Id">Stable key.</param>
/// <param name="Claim">The claim as it was made.</param>
/// <param name="Correction">What is actually the case.</param>
/// <param name="Evidence">How it was checked, and against what.</param>
/// <param name="Consequence">What downstream reasoning has to change because of it.</param>
public sealed record PremiseCorrection(
    string Id,
    string Claim,
    string Correction,
    string Evidence,
    string Consequence);

public static partial class ProofreadStandingFloor
{
    /// <summary>Date of the referent-carry-forward measuring session (g1). One Ollama session.</summary>
    public const string RetiredInterventionsMeasuredOn = "2026-08-04";

    /// <summary>
    /// Spurious (unexpected) edits the OFF arm produced across all 60 chunked fixture-runs of that
    /// session. The comparison baseline for every <see cref="RetiredIntervention.MeasuredSideEffect"/>
    /// figure below, recorded so those numbers are readable without the artifacts.
    /// </summary>
    public const int RetiredInterventionsOffArmSpuriousEdits = 333;

    /// <summary>
    /// INTERVENTIONS ALREADY MEASURED AGAINST THIS FLOOR AND REJECTED. See the file header.
    ///
    /// Both rows below come from ONE session on <c>Ollama / gemma4:12b</c> - the model
    /// <see cref="MeasuredOnModel"/> names - with n=15 per fixture per arm and ZERO spread in all twelve
    /// cells. The OFF arm of that session reproduced <see cref="ChunkedAgreement"/> cell for cell, which
    /// is what makes the cross-arm comparison valid rather than merely internally consistent.
    ///
    /// THE HEADLINE, because it is what a future plan is most likely to re-derive: handing the model an
    /// explicit, correct, deterministically resolved binding IMMEDIATELY ADJACENT to the text under
    /// correction changes nothing. The model does not choose a wrong referent over a right one - the
    /// error span survives byte-intact and nothing in that chunk is treated as correctable at all. So
    /// the missing thing is not the binding, and candidate lines that share that assumption (pronoun
    /// bindings carried inside the register itself, wider or better-labelled context) inherit a REFUTED
    /// premise rather than an open one.
    /// </summary>
    public static readonly IReadOnlyList<RetiredIntervention> RetiredInterventions = new[]
    {
        new RetiredIntervention(
            Id: "referent-carry-forward.ARM_A.OverlapLicence",
            Plan: "referent-carry-forward-2026-08-04",
            MeasuredOn: RetiredInterventionsMeasuredOn,
            Hypothesis:
                "The overlap already carries the antecedent; the prompt simply never licenses the model " +
                "to RESOLVE a pronoun against it. Restating the [CONTEXT_BEFORE] instruction to permit " +
                "referent resolution should therefore be enough, and it adds permission rather than " +
                "information - the cheapest candidate available.",
            RenderedChange:
                "Appended verbatim to the [CONTEXT_BEFORE] line of the base proofread prompt " +
                "(PromptFactory.ProofreadHe / ProofreadEn), leaving the rest of the prompt untouched.\n" +
                "  he: \" השתמש בו גם כדי לזהות אל מי מתייחסים כינויי הגוף שבטקסט שיש לתקן, והתאם את מין " +
                "הפועל, התואר והכינוי אל אותה דמות.\"\n" +
                "  en: \" Also use it to resolve which character the pronouns in the text to correct refer " +
                "to, and make verb, adjective and pronoun agreement follow that character.\"",
            Applicability:
                "Rendered on EVERY chunked per-chunk prompt (120/120 across the three chunked fixtures), " +
                "including chunks carrying no overlap - the line it extends is itself conditional prose. " +
                "It did NOT reach the single-shot control, which composes its instruction through " +
                "GetAnalysisPrompt rather than the per-chunk builder, so that fixture is a control for " +
                "this arm and not evidence about it.",
            PerFixture: new[]
            {
                new RetiredInterventionOutcome(
                    ChunkedAgreementFixtures.SeparatedAndDilutedId, Hits: 0, Runs: 15,
                    Applied: true, OverCorrectionsPerRunMean: 4.47),
                new RetiredInterventionOutcome(
                    ChunkedAgreementFixtures.AntecedentInOverlapId, Hits: 0, Runs: 15,
                    Applied: true, OverCorrectionsPerRunMean: 2.33),
                new RetiredInterventionOutcome(
                    ChunkedAgreementFixtures.DilutionOnlyId, Hits: 15, Runs: 15,
                    Applied: true, OverCorrectionsPerRunMean: 3.07),
                new RetiredInterventionOutcome(
                    ChunkedAgreementFixtures.SingleChunkControlId, Hits: 15, Runs: 15,
                    Applied: false, OverCorrectionsPerRunMean: null),
            },
            Verdict:
                "REJECTED on condition 1 of the pre-stated decision rule: chunked-agree-02, the " +
                "acceptance case, stayed at 0/15 - the arm's own isolating fixture, whose antecedent sits " +
                "in the immediately preceding sentence of the overlap the arm licenses. chunked-agree-01 " +
                "also stayed at 0/15. Both controls held at 15/15 and all tripwires were zero, so the " +
                "verdict rests on the acceptance case rather than on a voided run.",
            WhyItIsNotAWiringFailure:
                "The added line was present in 120/120 chunked prompts, verified in the published " +
                "per-chunk artifacts rather than assumed from the config. Across the two defect fixtures " +
                "the error span survived BYTE-INTACT in 90/90 runs (all three arms) with zero " +
                "agreement-bearing edits, so the model was not resolving to the wrong character - it was " +
                "not treating the span as correctable at all.",
            MeasuredSideEffect:
                "PRECISION, NOT RECALL - and the lead is CLOSED. DO NOT RE-OPEN IT; it has now been " +
                "opened twice.\n" +
                "WHAT WAS OBSERVED (2026-08-04, SYNTHETIC): chunked over-correction fell ~38% (" +
                RetiredInterventionsOffArmSpuriousEdits + " -> 205 spurious edits over 60 runs; " +
                "per-fixture means 7.67->4.47, 5.00->2.33, 6.13->3.07) while the arm-INVARIANT single-shot " +
                "control stayed flat. It was recorded as an open lead on an axis this arm's decision rule " +
                "did not govern, and explicitly NOT grounds to have shipped it.\n" +
                "WHAT CLOSED IT (2026-08-05, REAL PROSE): plan " + RealProseArmMeasurement.Plan + " built " +
                "the surface the lead needed - 12 verbatim passages of a twice-proofread Hebrew " +
                "manuscript, driven through the REAL chunker and the production entry point, with a " +
                "transplanted-defect recall guard on 4 of them (RealProsePrecisionFixtures) - and " +
                "measured both arms on it in one bounded session on the model MeasuredOnModel names. " +
                "The effect DID NOT REPRODUCE. Edits fell 47 -> 39 across the twelve clean passages " +
                "(mean 3.92 -> 3.25, -17.0%), but ARM A was individually better on only 5 of 12 " +
                "passages, tied on 4 and WORSE on 3, for a one-sided exact sign-test p of 0.363 against " +
                "the pre-stated bar of >=10 of 12 (p=0.019). Two passages carried -7 of the -8 net; the " +
                "other ten sum to -1. Of 57 distinct edits only 29 were shared - ARM A dropped 18 and " +
                "ADDED 10 - so arm-unique churn was 3.5x the net effect. Recall was identical, the same " +
                "seed ids repaired and missed under both arms (6/8), so this is NOT the 'precision up, " +
                "recall down' trade; the arm simply has no real-prose effect to trade with. Tripwires " +
                "were zero on all 32 rows and the arm rendered on 32/32 ARM A prompts and 0/32 OFF, so " +
                "the null is the model's.\n" +
                "WHY IT DID NOT GENERALISE, and this is the part worth carrying forward: the synthetic " +
                "effect had ONE carrier. Four instances of the construction '" +
                RealProsePrecisionFixtures.SyntheticDominantConstruction + "' produced 61.6% of the OFF " +
                "arm's over-corrections there and 93% of the gross drop, and that construction occurs " +
                "ZERO times as a standalone word sequence in real manuscript prose. The lead was " +
                "fixture-bound. Numbers, the per-passage matrix and the re-derived carrier-absence proof: " +
                nameof(RealProseArmMeasurement) + ".\n" +
                "WHAT IS STILL TRUE. The prerequisite this row used to name - that the gold surface " +
                "cannot observe a per-chunk intervention at all (see " +
                nameof(GoldSurfaceCannotReachAPerChunkIntervention) + ") - is a STRUCTURAL fact and still " +
                "holds; RealProsePrecisionFixtures is a second surface beside it, not a repair of it. The " +
                "recall floor a precision trade may not be bought with (legacy93.recall) also still " +
                "stands, and was never reached: nothing was traded. What is NOT still true is the 38% " +
                "figure as a statement about production prose."),

        new RetiredIntervention(
            Id: "referent-carry-forward.ARM_B.ResolvedReferent",
            Plan: "referent-carry-forward-2026-08-04",
            MeasuredOn: RetiredInterventionsMeasuredOn,
            Hypothesis:
                "The register maps NAME -> gender, and a chunk whose prose never names the character " +
                "gives that map nothing to apply to. Resolving the referent DETERMINISTICALLY (a " +
                "last-mention scan of all preceding text against the register's names and aliases, no " +
                "model call) and rendering it as an explicit binding should hand the model the one thing " +
                "neither the register nor the overlap states: who THIS passage is about.",
            RenderedChange:
                "A [RESOLVED_REFERENT] section composed ahead of the base prompt, after " +
                "[CHARACTER_REGISTER] and [CONTEXT_BEFORE]. Hebrew form, as it rendered on the corpus:\n" +
                "  \"נושא הקטע שיש לתקן: רוני (נקבה).\\n" +
                "כל כינוי גוף בגוף שלישי יחיד בטקסט שיש לתקן מתייחס אל רוני, אלא אם הטקסט מציין במפורש " +
                "דמות אחרת. התאם את מין הפועל, התואר והכינוי לנקבה.\"\n" +
                "English form (built, never measured - the corpus is Hebrew): \"Subject of the text to " +
                "correct: {name} ({gender}).\\nEvery third-person singular pronoun in the text to correct " +
                "refers to {name} unless the text explicitly names another character. Make verb, adjective " +
                "and pronoun agreement follow {gender}.\"\n" +
                "Gender was rendered only for register values the composer recognised, never inferred.",
            Applicability:
                "Rendered in 75/75 ELIGIBLE per-chunk prompts, including 15/15 on the exact chunk carrying " +
                "the error in BOTH defect fixtures, with the correct binding. Structurally inert on a " +
                "FIRST chunk (nothing precedes it) and on the single-shot control, so chunked-agree-03 " +
                "(error in chunk 0) and chunked-agree-04 are controls for this arm rather than tests of " +
                "it - which bounds what their holding at 15/15 may be read as.",
            PerFixture: new[]
            {
                new RetiredInterventionOutcome(
                    ChunkedAgreementFixtures.SeparatedAndDilutedId, Hits: 0, Runs: 15,
                    Applied: true, OverCorrectionsPerRunMean: null),
                new RetiredInterventionOutcome(
                    ChunkedAgreementFixtures.AntecedentInOverlapId, Hits: 0, Runs: 15,
                    Applied: true, OverCorrectionsPerRunMean: null),
                new RetiredInterventionOutcome(
                    ChunkedAgreementFixtures.DilutionOnlyId, Hits: 15, Runs: 15,
                    Applied: false, OverCorrectionsPerRunMean: null),
                new RetiredInterventionOutcome(
                    ChunkedAgreementFixtures.SingleChunkControlId, Hits: 15, Runs: 15,
                    Applied: false, OverCorrectionsPerRunMean: null),
            },
            Verdict:
                "REJECTED on condition 1: chunked-agree-02 stayed at 0/15, and so did chunked-agree-01. " +
                "This was the plan's LEADING candidate and it is the more consequential half of the " +
                "negative - a correct, explicit, adjacent binding is the strongest form of the " +
                "'the pronoun is not bound' hypothesis, and it moved nothing.",
            WhyItIsNotAWiringFailure:
                "The [RESOLVED_REFERENT] section rendered with the CORRECT binding (רוני, נקבה) on the " +
                "very chunk carrying the error, 15/15 on both defect fixtures, verified in the published " +
                "prompt artifacts. The error span still survived byte-intact in every run.",
            MeasuredSideEffect:
                "None. Flat on the over-correction axis (316 spurious edits over 60 runs against the OFF " +
                "arm's " + RetiredInterventionsOffArmSpuriousEdits + "), so this arm bought nothing on " +
                "either axis."),
    };

    /// <summary>
    /// WHY NO ARM WAS EVER EVALUATED AGAINST DECISION-RULE CONDITION 4 (no over-correction regression on
    /// the <c>agree-preserve.*</c> bars). Not an oversight in the run - a STRUCTURAL fact about the two
    /// surfaces, recorded because "condition 4 held" would otherwise read as a measurement.
    ///
    /// Those bars sit on <c>GoldPromptSurface.ProductionLongPlusShort</c> and are fed by
    /// <c>ProofreadQualityTests.BuildGoldRequest</c>, which composes its instruction through the
    /// three-argument <c>BuildProofreadChunkPrompt(language, characters, overlapPrefix: null)</c> - the
    /// production per-chunk builder, called with no overlap and nothing else. A per-chunk intervention
    /// therefore cannot render into a gold request AT ALL, whatever it is switched to. Condition 4 was
    /// vacuously satisfied, and the arms' verdicts rest entirely on condition 1.
    ///
    /// CONSEQUENCE FOR THE PRECISION LEAD above: acting on it needs a way to measure a per-chunk
    /// intervention on a surface where precision is floored, and today the two do not intersect. That is
    /// a prerequisite of any follow-up, not a detail of it.
    ///
    /// Pinned deterministically (composed prompt strings, no model) by
    /// <c>ProofreadStandingFloorRetiredInterventionTests</c>.
    /// </summary>
    public const string GoldSurfaceCannotReachAPerChunkIntervention =
        "BuildGoldRequest composes through the 3-argument BuildProofreadChunkPrompt, so no per-chunk " +
        "prompt intervention can reach the agree-preserve.* bars; decision-rule condition 4 was " +
        "structurally vacuous rather than measured.";

    /// <summary>
    /// CLAIMS A PLAN ASSERTED AS ESTABLISHED AND A LATER RUN FOUND NOT TO BE. Kept as data next to the
    /// floor because a premise re-derived from the same source text will be re-derived the same way; the
    /// correction has to live where the next reader is already looking, and be gated so a prompt edit
    /// that invalidates it fails loudly instead of leaving a stale correction behind.
    /// </summary>
    public static readonly IReadOnlyList<PremiseCorrection> PremiseCorrections = new[]
    {
        new PremiseCorrection(
            Id: "proofread-he.character-register-pronoun-clause",
            Claim:
                "The Hebrew proofread prompt's [CHARACTER_REGISTER] line ALREADY licenses pronoun " +
                "RESOLUTION (it names זיהוי כינויי גוף), so a referent-resolution licence is present and " +
                "already insufficient - which demotes 'restate the licence' from a likely fix to a cheap " +
                "control arm.",
            Correction:
                "NOT ESTABLISHED. The clause reads " +
                "'אם מופיע [CHARACTER_REGISTER] — השתמש בו לאימות התאמת מין (נטיית פועל, תואר, כינוי), " +
                "עקביות כתיב שמות, וזיהוי כינויי גוף.' The whole list is governed by לאימות (FOR " +
                "VERIFICATION), and זיהוי כינויי גוף is identifying that a word IS a personal pronoun, " +
                "not resolving what it refers to. The coreference wording (פענוח הפניות) is absent. The " +
                "prompt licenses agreement VERIFICATION against the register; it does not license " +
                "referent resolution.",
            Evidence:
                "Read off the RENDERED prompt (not the source literal) during the 2026-08-04 session, " +
                "after the same reading was flagged for confirmation rather than settled on one " +
                "translator's judgement - the standing Hebrew rule for this corpus.",
            Consequence:
                "The candidate ORDERING that rested on it is void, but the outcome is not: the licensing " +
                "arm was measured as a genuine candidate and lost on its own numbers (0/15 on the " +
                "acceptance case), not by assumption. What must not survive is the inference that " +
                "'a licence exists and is insufficient' - it was never shown to exist, so nothing has " +
                "been learned about licensing from its absence."),
    };

    /// <summary>The retired intervention with this id. Throws on an unknown id rather than returning null.</summary>
    public static RetiredIntervention RetiredInterventionById(string id) =>
        RetiredInterventions.SingleOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No retired intervention with this id.");
}
