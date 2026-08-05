using System;
using System.Collections.Generic;
using System.Linq;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// RealProseArmMeasurement - WHAT THE REAL-PROSE SESSION ACTUALLY MEASURED, AS DATA.
//
// WHY THIS FILE EXISTS. RealProsePrecisionFixtures is the INSTRUMENT and RealProseHarness is the
// DRIVER; both are gated, and both would keep passing forever while the one thing they were built to
// produce - a verdict on ARM A's over-correction cut - decayed into a sentence in an archived plan
// file. That is exactly how this lead got re-opened twice: the synthetic 38% survived in prose while
// the reason it could not be trusted did not. So the outcome is recorded HERE, next to the surface it
// was measured on, in a shape a test can contradict.
//
// THE OUTCOME, IN ONE LINE: DO NOT SHIP. The effect does not reproduce on real manuscript prose.
// Two of the three pre-stated conditions failed. This is the plan's pre-authorised clean negative and
// NOT the "precision up, recall down" case - recall was identical, seed for seed, in both arms.
//
// WHAT MAKES IT A CLOSURE RATHER THAN ANOTHER LEAD, and it is the only part worth re-reading: the
// synthetic effect had a NAMED CARRIER. Four instances of one Hebrew construction (מן ה...) produced
// 61.6% of the OFF arm's over-corrections there and 93% of the gross drop. That construction does not
// occur in this manuscript's prose at all. CarrierAbsence below is not a remembered fact - it is
// re-derived from the embedded passages through the real chunker on every run of the deterministic
// suite, against a non-vacuous control, so a future passage set that DID contain the carrier would
// fail rather than quietly inherit this verdict.
//
// WHAT THIS RECORD IS NOT. It is not a floor and nothing is gated against it in the FloorOutcome
// sense: n=1 per cell cannot support a bar. What IS gated (RealProseArmMeasurementTests) is that the
// record stays arithmetically coherent, stays tied to the passages and seeds it cites, stays
// consistent with the shipped default-OFF posture, and keeps carrying the reason the synthetic effect
// did not generalise.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One clean passage's edit counts under both arms. On twice-proofread prose every substantive edit is
/// presumed spurious, so these are precision readings and LOWER is better.
/// </summary>
/// <param name="PassageId">A <see cref="RealProsePrecisionFixtures"/> passage id.</param>
/// <param name="Off">Substantive edits under the shipped prompt.</param>
/// <param name="ArmA">Substantive edits under <c>ProofreadPromptArm.OverlapReferentLicence</c>.</param>
public sealed record RealProsePassageOutcome(string PassageId, int Off, int ArmA)
{
    /// <summary>ARM A minus OFF. Negative means ARM A proposed fewer edits on this passage.</summary>
    public int Delta => ArmA - Off;

    /// <summary>ARM A proposed strictly fewer edits here.</summary>
    public bool ArmAWon => ArmA < Off;

    /// <summary>ARM A proposed strictly MORE edits here. Three passages did, which never happened synthetically.</summary>
    public bool ArmALost => ArmA > Off;
}

/// <summary>
/// One seeded passage's recall reading. Recorded as the SEED IDS repaired and missed rather than as a
/// ratio: "6/8 under both arms" is compatible with the two arms repairing different defects, which
/// would be a real finding, and the ids are what rule that out.
/// </summary>
/// <param name="PassageId">A seeded <see cref="RealProsePrecisionFixtures"/> passage id.</param>
/// <param name="RepairedGoldCaseIds">Seeds repaired, identically under both arms.</param>
/// <param name="MissedGoldCaseIds">Seeds missed, identically under both arms.</param>
public sealed record RealProseRecallOutcome(
    string PassageId,
    IReadOnlyList<string> RepairedGoldCaseIds,
    IReadOnlyList<string> MissedGoldCaseIds);

/// <summary>
/// The 2026-08-05 real-prose arm measurement. See the file header. Every number here was re-derived
/// from the published JSONL rather than taken from the driver's own aggregate.
/// </summary>
public static class RealProseArmMeasurement
{
    /// <summary>The plan that scoped and spent this session.</summary>
    public const string Plan = "proofread-overcorrection-arm-a-2026-08-05";

    /// <summary>Date of the measuring session.</summary>
    public const string MeasuredOn = "2026-08-05";

    /// <summary>Model. The same one every other number in this corpus was measured on.</summary>
    public const string MeasuredOnModel = "gemma4:12b";

    /// <summary>
    /// Repetitions per cell. ONE, and stated as a constant so no reader has to infer it: the complete
    /// balanced 16-unit x 2-arm matrix cost 64 chunk calls and 42.2 minutes of a ~70 minute budget,
    /// and n=3 would have been 192 calls. The parent took a WHOLE matrix at n=1 over a truncated one
    /// at n=3. What that buys and what it does not is spelled out in <see cref="WhatNEqualsOneSupports"/>.
    /// </summary>
    public const int RepsPerCell = 1;

    /// <summary>Chunk-level model calls the session made. 32 rows x 2 chunks.</summary>
    public const int ChunkCalls = 64;

    /// <summary>THE VERDICT. Prefixed so no reader reaches the numbers before the conclusion.</summary>
    public const string Verdict =
        "DO NOT SHIP. The effect does not reproduce on real manuscript prose. Condition 1 (precision " +
        "visible PER PASSAGE) FAILED: ARM A was better on 5 of 12 passages, tied on 4 and WORSE on 3, " +
        "for a one-sided exact sign-test p of 0.363 on the 8 non-tied passages against a pre-stated " +
        "bar of >=10 of 12 (p=0.019). Condition 3 (the named mechanism reproduces) FAILED: the " +
        "corruption half reproduced at c1's magnitude (10 -> 5) but the REGISTER half, which is the " +
        "half c1's category-scoping story actually names, barely moved (12 -> 10), and ARM A both " +
        "INTRODUCED the corpus's clearest register edit and SUPPRESSED two edits inside its own named " +
        "grammatical categories. Condition 2 (recall holds) PASSED and cannot carry a ship on its own. " +
        "This is the plan's pre-authorised clean negative, NOT the 'precision up, recall down' case.";

    /// <summary>
    /// WHY IT COLLAPSED, and the claim <see cref="RealProseArmMeasurementTests"/> re-derives rather than
    /// trusts. Referenced by the retired intervention's closed side effect.
    /// </summary>
    public const string WhyItDidNotGeneralise =
        "The synthetic effect had a single carrier. Four instances of the construction " +
        "'" + RealProsePrecisionFixtures.SyntheticDominantConstruction + "' produced 61.6% of the OFF " +
        "arm's over-corrections on the authored fixtures and 93% of the gross drop. That construction " +
        "occurs ZERO times as a standalone word sequence anywhere in the real-prose corpus. The carrier " +
        "of the synthetic effect is simply absent from real manuscript prose, which is what " +
        "'fixture-bound' means.";

    /// <summary>
    /// WHAT n=1 SUPPORTS AND WHAT IT DOES NOT, stated on the record because the next reader will want
    /// to quote a number from it.
    /// </summary>
    public const string WhatNEqualsOneSupports =
        "SUPPORTS: the tripwire verdict (deterministic, all 32 rows), the direction and rough size of " +
        "the mean shift, the absence of a recall drop, the family composition of the delta, and the " +
        "verified absence of the carrier construction. DOES NOT SUPPORT: any per-passage significance " +
        "claim, any statement that a specific edit was ELIMINATED rather than simply not drawn once, " +
        "and any estimate of the real-prose noise floor - there is no OFF-vs-OFF replicate on this " +
        "surface. If this surface is ever funded again, THAT replicate is the informative spend, not " +
        "another arm comparison.";

    // ── condition 1: precision, the full matrix ──────────────────────────────────────────────────

    /// <summary>
    /// EVERY clean passage, in corpus order. The full matrix, never a subset mean: two passages
    /// (<c>-02</c> and <c>-08</c>) carry -7 of the -8 net, and stripping them leaves -1 across the
    /// remaining ten. A mean alone would have read as a 17% cut.
    /// </summary>
    public static readonly IReadOnlyList<RealProsePassageOutcome> Precision = new[]
    {
        new RealProsePassageOutcome(RealProsePrecisionFixtures.NarrationNoQuotesId,    Off: 4, ArmA: 2),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.DialogueMidId,          Off: 5, ArmA: 2),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.DialogueLowId,          Off: 3, ArmA: 3),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.DialogueHighId,         Off: 7, ArmA: 8),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.ArgumentMidId,          Off: 3, ArmA: 2),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.NarrationNoQuotesTwoId, Off: 1, ArmA: 1),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.DialogueMidTwoId,       Off: 4, ArmA: 4),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.DialogueVeryHighId,     Off: 9, ArmA: 5),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.InteriorLowId,          Off: 3, ArmA: 2),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.BanterVeryHighId,       Off: 5, ArmA: 6),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.SceneHighId,            Off: 2, ArmA: 3),
        new RealProsePassageOutcome(RealProsePrecisionFixtures.ActionMidId,            Off: 1, ArmA: 1),
    };

    /// <summary>c1's pre-stated bar: ARM A individually better on at least this many of the twelve.</summary>
    public const int PreStatedPassageBar = 10;

    /// <summary>
    /// DISTINCT EDITS ACROSS BOTH ARMS, and the reason the net counts overstate the effect. Only 29 of
    /// 57 distinct edits are shared: ARM A dropped 18 that OFF made and ADDED 10 that OFF did not, so
    /// arm-unique churn is 3.5x the net difference of 8. The synthetic effect had disjoint per-rep
    /// ranges and Cohen d of 3.0 to 4.6; nothing of that shape survives here.
    /// </summary>
    public const int SharedDistinctEdits = 29;

    /// <summary>Distinct edits OFF made and ARM A did not. See <see cref="SharedDistinctEdits"/>.</summary>
    public const int OffOnlyDistinctEdits = 18;

    /// <summary>Distinct edits ARM A made and OFF did not. An arm that only removed edits would be 0 here.</summary>
    public const int ArmAOnlyDistinctEdits = 10;

    // ── condition 2: recall ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE RECALL READING, identical under both arms - not merely the same RATE but the same seed ids
    /// repaired and the same ones missed. That is what rules out the "lazier model" shape. It is still
    /// only 8 seeded defects at n=1, far below this corpus's n&gt;=15 single-case rule, so it is
    /// "recall did not drop", never "recall is established".
    /// </summary>
    public static readonly IReadOnlyList<RealProseRecallOutcome> Recall = new[]
    {
        new RealProseRecallOutcome(RealProsePrecisionFixtures.NarrationNoQuotesId,
            RepairedGoldCaseIds: new[] { "inj-ms-06" }, MissedGoldCaseIds: new[] { "inj-ms-03" }),
        new RealProseRecallOutcome(RealProsePrecisionFixtures.ArgumentMidId,
            RepairedGoldCaseIds: new[] { "inj-ms-11", "inj-ms-12" }, MissedGoldCaseIds: Array.Empty<string>()),
        new RealProseRecallOutcome(RealProsePrecisionFixtures.DialogueVeryHighId,
            RepairedGoldCaseIds: new[] { "inj-ms-04" }, MissedGoldCaseIds: new[] { "inj-ms-02" }),
        new RealProseRecallOutcome(RealProsePrecisionFixtures.InteriorLowId,
            RepairedGoldCaseIds: new[] { "inj-ms-09", "inj-ms-10" }, MissedGoldCaseIds: Array.Empty<string>()),
    };

    // ── condition 3: the mechanism, by hand-classified semantic family ───────────────────────────

    /// <summary>
    /// The delta by SEMANTIC family, hand-classified into c1's own families from the raw responses.
    /// The driver's own shape buckets put the whole net drop in SingleWordSubstitution (25 -> 16) with
    /// the orthographic bucket exactly flat, which LOOKS like c1's story; opening that bucket is what
    /// shows it is only half of it. Both columns reconcile to the precision totals, which is asserted.
    /// </summary>
    public static readonly IReadOnlyList<(string Family, int Off, int ArmA, string Note)> SemanticFamilies = new[]
    {
        ("CORRUPTION", 10, 5,
            "The model emits a Hebrew NON-WORD. REPRODUCES at almost exactly c1's synthetic magnitude " +
            "(-50% vs -49%). ARM A stopped nine of OFF's non-words and manufactured four of its own. " +
            "GUARD POSTURE (recorded 2026-08-05, not re-measured): one of ARM A's five - צמצם -> צמץם - " +
            "is the single suggestion the shipped HebrewOrthographyShapeGuard reaches, so a harness " +
            "running that guard ON would report this column as 4. RealProseHarness therefore " +
            "constructs its diff with the guard OFF (MeasurementDiffService), which is the posture " +
            "these numbers were produced under and the one a later re-run has to keep to be comparable."),
        ("REGISTER", 12, 10,
            "Lexical word-for-word swap flattening the author's register. c1's synthetic cut here was " +
            "-58%; on real prose it is -17%. This is the half the named mechanism is ABOUT, and it is " +
            "the half that does not reproduce."),
        ("INFLECTION", 4, 4,
            "Number, gender, person, possessive. Flat. ARM A's unique edits do skew inflectional, but " +
            "at 3 or 4 tokens rather than c1's 30 occurrences."),
        ("ORTHOGRAPHIC", 19, 19,
            "Maqaf, ellipsis, quote-and-period ordering. Flat, exactly as predicted - but flat by " +
            "CANCELLATION, not identity: each arm has 3 unique punctuation edits of the same two " +
            "subtypes, so the token-level differences here are pure churn."),
        ("KTIV", 1, 0, "Ktiv-male regression. One token; too small to read."),
        ("DELETION", 1, 1,
            "The model drops a word outright. One token in each arm, flat. Listed rather than folded " +
            "into another family so the six columns reconcile to the precision totals exactly."),
    };

    /// <summary>
    /// THE TWO TOKEN-LEVEL TESTS OF c1's MECHANISM, BOTH OF WHICH WENT THE WRONG WAY. c1's story was
    /// scope narrowing by NAMED grammatical category (ARM A names פועל / תואר / כינוי), so edits
    /// outside those categories should collapse and edits inside them should be preserved.
    /// </summary>
    public const string MechanismCounterEvidence =
        "ARM A INTRODUCED the corpus's clearest register edit (להגיד את זה -> לומר זאת, colloquial " +
        "first-person interior narration flattened up into formal register), which is precisely the " +
        "class c1 said the arm suppresses and which OFF did not make. And ARM A SUPPRESSED two edits " +
        "squarely INSIDE its own named categories, which the mechanism says are preserved: גרם -> גרמה " +
        "(a פועל) and ומבטי -> ומבטו (a כינוי). The category-scoping signature does not hold at token " +
        "level.";

    // ── the residue this measurement surfaced and does not own ───────────────────────────────────

    /// <summary>
    /// A LEAD THIS SESSION SURFACED AND DID NOT MEASURE, followed up separately on 2026-08-05 and now
    /// CLOSED. Deliberately kept apart from <see cref="Verdict"/> so it is findable without being
    /// mistaken for one: it is about the MODEL, not about the arm, and no decision rule of this
    /// session governed it. The follow-up record lives in
    /// <c>Pagedraft.Api.Tests.RealProseNonWordResidue</c>; this note points at it rather than
    /// duplicating it, and carries the two corrections the original note needed.
    /// </summary>
    public const string UnownedResidue =
        "NOT THIS PLAN'S RESULT AND NOT ACCEPTED as a verdict about the arm, and now CLOSED by a " +
        "separate follow-up (plan hebrew-non-word-suggestions-2026-08-05). WHAT WAS SEEN: gemma4:12b " +
        "emits character-level Hebrew NON-WORDS on clean, twice-proofread manuscript prose in BOTH " +
        "arms - 10 of the 47 edits the OFF arm proposed across twelve short passages, for instance " +
        "הקטנה -> הקטענה and עכשיו -> עכשור, the single biggest family in the OFF column after " +
        "orthographic churn, plus five more under ARM A. " +
        "ARM ATTRIBUTION, CORRECTED: this note used to cite צמצם -> צמץם as one of the OFF arm's ten. " +
        "It is not an OFF edit. It occurs exactly once in the whole 128-suggestion corpus, under ARM " +
        "A on real-prose-10-banter-very-high, and it is the corpus's ONLY mechanically illegal " +
        "suggestion. " +
        "WHAT THE CLASS HAS NOW, replacing this note's original claim that it had no decision rule, no " +
        "gold class and no n>=15 measurement: (1) an ORIGIN VERDICT of RAW MODEL, settled " +
        "deterministically and for free off the already-recorded IAiRouter-seam artifacts, with every " +
        "post-model pass cleared individually rather than assumed inert - see " +
        "RealProseNonWordResidue.Origin; (2) a DETERMINISTIC REGRESSION SURFACE, all fifteen instances " +
        "recorded in RealProseNonWordResidue on the real-prose surface, deliberately not in " +
        "proofread-gold.json, which structurally cannot reach a per-chunk intervention; (3) a shipped " +
        "narrow GUARD, HebrewOrthographyShapeGuard behind Ai:HebrewStyle:" +
        "DropOrthographicallyImpossibleSuggestions (default true), whose measured reach on this corpus " +
        "is 1 suggestion in 128 and 0 of the ten. " +
        "WHAT IT STILL DOES NOT HAVE, stated so nobody claims otherwise: NO n>=15 measurement. The GPU " +
        "todo that would have produced one was CANCELLED with reason, because a guard proved incapable " +
        "of firing on any of the ten cannot be measured against them. " +
        "WHY IT IS CLOSED RATHER THAN OPEN: the origin is the model, the pipeline is acquitted by " +
        "evidence, the guard is a 1-in-128 safety net and not a remedy, and the residue is " +
        "model-quality character-level corruption on clean prose - two instances of which (הזעת, " +
        "שהמרור) are legal Hebrew tokens wrong only in context and so are unreachable by any shape or " +
        "lexicon rule even in principle. Re-opening this needs a NEW lead with its own scope.";

    // ── derived readings ─────────────────────────────────────────────────────────────────────────

    /// <summary>Total substantive edits under the shipped prompt across the twelve clean passages.</summary>
    public static int OffTotal => Precision.Sum(p => p.Off);

    /// <summary>Total substantive edits under ARM A across the same twelve.</summary>
    public static int ArmATotal => Precision.Sum(p => p.ArmA);

    /// <summary>Passages where ARM A proposed strictly fewer edits. Five of twelve, against a bar of ten.</summary>
    public static int PassagesArmAWon => Precision.Count(p => p.ArmAWon);

    /// <summary>Passages where ARM A proposed strictly MORE edits. Three; the synthetic run never did this.</summary>
    public static int PassagesArmALost => Precision.Count(p => p.ArmALost);

    /// <summary>Passages where the two arms tied.</summary>
    public static int PassagesTied => Precision.Count(p => p.Delta == 0);

    /// <summary>Seeds repaired, summed over the seeded passages. Identical under both arms.</summary>
    public static int SeedsRepaired => Recall.Sum(r => r.RepairedGoldCaseIds.Count);

    /// <summary>Seeds missed, summed over the seeded passages. Identical under both arms.</summary>
    public static int SeedsMissed => Recall.Sum(r => r.MissedGoldCaseIds.Count);

    /// <summary>
    /// One-sided exact sign test on the non-tied passages: P(at least <paramref name="successes"/> of
    /// <paramref name="trials"/> under a fair coin). Implemented here rather than quoted so the recorded
    /// matrix is what decides the verdict, not a number somebody typed next to it.
    /// </summary>
    public static double OneSidedSignTestP(int successes, int trials)
    {
        if (trials <= 0) throw new ArgumentOutOfRangeException(nameof(trials), trials, "no trials");
        if (successes < 0 || successes > trials)
            throw new ArgumentOutOfRangeException(nameof(successes), successes, "outside its denominator");

        double tail = 0;
        for (var k = successes; k <= trials; k++) tail += Choose(trials, k);
        return tail / Math.Pow(2, trials);
    }

    /// <summary>The one-sided p the RECORDED precision matrix produces. See <see cref="OneSidedSignTestP"/>.</summary>
    public static double ObservedSignTestP =>
        OneSidedSignTestP(PassagesArmAWon, PassagesArmAWon + PassagesArmALost);

    private static double Choose(int n, int k)
    {
        double c = 1;
        for (var i = 1; i <= k; i++) c = c * (n - k + i) / i;
        return c;
    }
}
