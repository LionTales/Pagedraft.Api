using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes. Same rule (and same reason) as ProofreadAgreementGoldTests.
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// ProofreadStandingFloor — THE NO-REGRESSION BAR Wave 2 changes are measured against (c3).
//
// READ "WHO EVALUATES WHAT" BELOW BEFORE QUOTING THAT HEADLINE. Most of this file is gated by a real
// consumer; a named minority is a RECORDED FIGURE with no automated evaluator at all, and quoting the
// headline over those rows would be false.
//
// WHAT IT IS. g1 measured the Hebrew proofread corpus on 2026-08-04 in one Ollama session. Those
// numbers are written down HERE, as data, so that "the chunked agreement failure" and "the
// punctuation tax" stop being one-off run reports and become a standing bar. Every entry carries the
// PROMPT SURFACE it was measured on, because the corpus spans THREE non-comparable surfaces and a
// figure quoted without its surface is not a figure (see GoldPromptSurface).
//
// IT IS MODEL-CONDITIONAL, AND SAYS SO. Everything below was measured on Ollama / gemma4:12b, the
// shipped Proofread(he) model. A model swap does not "regress" this floor - it VOIDS it, and the
// floor has to be re-measured. TheFloorNamesTheShippedModel (in ProofreadStandingFloorTests) fails
// deterministically the day the shipped model constant moves, so nobody compares a new model's
// numbers against an old model's bar.
//
// -- THE TWO-SIDED SEMANTICS, WHICH ARE THE WHOLE POINT --------------------------------------------
// Two of the four chunked fixtures currently FAIL. So the floor encodes the CURRENT TRUTH, not an
// aspiration, and it has to be loud in BOTH directions:
//
//   ExpectedPass (03, 04)  15/15 today. Dropping below that is a REGRESSION -> FloorVerdict.Regressed.
//   KnownDefect  (01, 02)  0/15 today. Still 0 is the defect reproducing, which is the floor HOLDING
//                          (FloorVerdict.KnownDefectReproduced) - NOT a silent pass. Anything above 0
//                          is FloorVerdict.KnownDefectMoved: the referent-carry-forward defect has
//                          started to move and the FLOOR MUST BE UPDATED. That is deliberately a
//                          failure too. An expected-fail that flips to pass must never be
//                          indistinguishable from an expected-fail that still fails - if it were,
//                          a Wave 2 fix landing would look exactly like nothing happening.
//
// -- WHAT IS A FLOOR AND WHAT IS ONLY CHARACTERIZATION ---------------------------------------------
// Hits/runs, the metric bars and the two tripwires (whitespace-only = 0, transport failures = 0) are
// FLOORS: they are flat across every repetition g1 ran (sd 0) and a movement is a finding.
// OverCorrectionsPerRunMean and KnownCorruptions are CHARACTERIZATION: they are recorded so movement
// is visible, and they are deliberately NOT gated, because they are means with real spread
// (fixture 03 ranged 2-8) and gating them would buy flake instead of signal.
//
// -- WHY THE EVALUATOR LIVES HERE AND NOT IN THE LIVE TEST -----------------------------------------
// The live consumers are skip-by-default and sit in the filter-EXCLUDED namespace, so nothing they
// compute is exercised by a standing gate. The DECISION - "did the floor hold, regress, or move?" -
// is therefore a pure function here, unit-tested deterministically with no model
// (ProofreadStandingFloorTests). The live tests only supply observations.
//
// -- WHO EVALUATES WHAT (c3/be-c03), because "measured against" was NOT true of all of it -----------
// A bar is only a bar if something can OBSERVE it. Every row below is assigned to exactly one of three
// buckets, and MetricEvaluators is that assignment as data rather than as prose, so a bar added
// without an owner fails the deterministic suite instead of quietly joining the third bucket:
//
//   GoldHarnessEvaluatedMetricIds (7)  ProofreadQualityTests.ProofreadQuality_RunGoldCases_* reports
//                                      every one and ASSERTS them when the run is on the measured
//                                      provider/model and the default Hebrew gold.
//   ChunkedHarnessEvaluatedMetricIds   ChunkedAgreementLiveTests.ReportAndGateTheStandingFloor routes
//                                (2)   both tripwires through EvaluateMetric - ONE source of truth, not
//                                      a hand-rolled copy of the same comparison.
//   UnevaluatedMetricIds         (1)   NO AUTOMATED EVALUATOR. A recorded figure, honestly labelled.
//                                      See each row's Meaning for what a future run must build.
//
// PunctuationPhenomena is in the same position as that third bucket and says so on the table itself:
// its per-phenomenon split needs a classifier that exists only for the CHUNKED fixture corpus
// (ChunkedAgreementLiveTests.Classify, which reads a fixture's ErrorSpan/ExpectedFix), and no gold row
// carries those fields. Its subset TOTALS are reported against a live gold run; its rows are not.
//
// Both live consumers are compile-verified only, so what actually holds the line in CI is the
// deterministic half: the pure decision functions, the corpus ties, and the ownership partition.
// ---------------------------------------------------------------------------------------------

/// <summary>Which side of the bar a standing case sits on TODAY (not where it ought to sit).</summary>
public enum FloorOutcome
{
    /// <summary>The case passes today and must keep passing.</summary>
    ExpectedPass,

    /// <summary>
    /// The case FAILS today. This is a pinned defect, not an acceptance: the floor records the failure
    /// so a fix can be measured against it, and flags any improvement as "update the floor".
    /// </summary>
    KnownDefect
}

/// <summary>The four things a measured (hits, runs) pair can mean against a floor entry.</summary>
public enum FloorVerdict
{
    /// <summary>An <see cref="FloorOutcome.ExpectedPass"/> entry still passes every run. Nothing to do.</summary>
    Held,

    /// <summary>An <see cref="FloorOutcome.ExpectedPass"/> entry stopped passing. FAILURE.</summary>
    Regressed,

    /// <summary>
    /// A <see cref="FloorOutcome.KnownDefect"/> entry still fails every run - the defect reproduced, so
    /// the floor holds. Reported distinctly from <see cref="Held"/> so a run cannot be read as
    /// "everything passed" when half the corpus is a pinned failure.
    /// </summary>
    KnownDefectReproduced,

    /// <summary>
    /// A <see cref="FloorOutcome.KnownDefect"/> entry produced at least one hit. The defect is moving.
    /// Treated as a FAILURE on purpose: it must not green silently, because a silent green is
    /// indistinguishable from the defect still being there.
    /// </summary>
    KnownDefectMoved
}

/// <summary>Which direction a metric floor bounds.</summary>
public enum FloorBound
{
    /// <summary>A tax/cost: the observed value may not EXCEED the floor.</summary>
    AtMost,

    /// <summary>A quality figure: the observed value may not FALL BELOW the floor.</summary>
    AtLeast,

    /// <summary>A tripwire: the observed value must equal the floor exactly (used for the zeros).</summary>
    Exactly
}

/// <summary>
/// Which way a metric is GOOD - the one fact <see cref="FloorBound"/> cannot supply.
///
/// A bound is the ENCODING of a judgement ("may not exceed" / "may not fall below"); it cannot also be
/// the evidence that the judgement was made correctly. Stating the direction separately is what makes
/// "an AtLeast on a tax" a contradiction a deterministic test can see, because the two columns are then
/// two claims rather than one restated twice.
///
/// AUTHOR THIS FROM THE METRIC, NEVER FROM THE BOUND COLUMN. "Recall: more of it is better." "Spurious
/// edits: fewer of them are better." If it is ever filled in by reading <see cref="FloorBound"/>, the
/// two columns collapse back into one and the cross-check becomes the tautology it was added to remove.
/// </summary>
public enum MetricDirection
{
    /// <summary>A quality figure (recall, precision). A LARGER observation is better news.</summary>
    HigherIsBetter,

    /// <summary>
    /// A tax, cost or tripwire (spurious edits, over-correction rate, transport failures). A SMALLER
    /// observation is better news; several of these are already at 0, where nothing better exists.
    /// </summary>
    LowerIsBetter
}

/// <summary>What a measured metric did against its floor.</summary>
public enum MetricVerdict
{
    /// <summary>Within the bar.</summary>
    Held,

    /// <summary>Outside the bar in the bad direction. FAILURE.</summary>
    Regressed,

    /// <summary>
    /// Outside the bar in the GOOD direction. Not a failure, but the floor is now stale and saying so
    /// is the only way a real improvement gets recorded instead of being absorbed as slack.
    /// </summary>
    ImprovedUpdateTheFloor
}

/// <summary>
/// One chunked-corpus fixture's floor: what it did, over how many runs, on which surface.
/// </summary>
/// <param name="FixtureId">A <c>ChunkedAgreementFixtures</c> id.</param>
/// <param name="Surface">The prompt surface it was measured on.</param>
/// <param name="Outcome">Where it sits today.</param>
/// <param name="MeasuredHits">Runs in which the agreement error was corrected in the persisted result.</param>
/// <param name="MeasuredRuns">Repetitions g1 ran (the baseline's n &gt;= 15 single-case rule).</param>
/// <param name="OverCorrectionsPerRunMean">CHARACTERIZATION ONLY - not gated, see the file header.</param>
/// <param name="Meaning">What a movement on this entry would mean, in words.</param>
public sealed record ChunkedAgreementFloorEntry(
    string FixtureId,
    GoldPromptSurface Surface,
    FloorOutcome Outcome,
    int MeasuredHits,
    int MeasuredRuns,
    double OverCorrectionsPerRunMean,
    string Meaning);

/// <summary>One scalar bar, with its surface and its subset. Never quote one without both.</summary>
/// <param name="Id">Stable key, used by a report and by the tests.</param>
/// <param name="Surface">The prompt surface the number was measured on.</param>
/// <param name="Subset">The corpus subset (a gold id prefix, or the whole legacy set).</param>
/// <param name="Metric">What is measured.</param>
/// <param name="Direction">
/// Which way the metric is GOOD, stated independently of <paramref name="Bound"/> and authored from the
/// metric itself - see <see cref="MetricDirection"/>. This is the only thing that makes a wrong bound
/// direction detectable without a model.
/// </param>
/// <param name="Bound">Which direction the bar binds.</param>
/// <param name="Value">The TOLERATED bar - the value an observation is allowed to reach.</param>
/// <param name="Unit">The unit <paramref name="Value"/> is expressed in.</param>
/// <param name="Meaning">Why this bar exists and what crossing it would mean.</param>
public sealed record ProofreadMetricFloor(
    string Id,
    GoldPromptSurface Surface,
    string Subset,
    string Metric,
    MetricDirection Direction,
    FloorBound Bound,
    double Value,
    string Unit,
    string Meaning)
{
    private readonly double? _measuredValue;

    /// <summary>
    /// WHAT WAS ACTUALLY MEASURED, which is not always what is TOLERATED (be-c04).
    ///
    /// Almost every bar is its own measurement, and for those this equals <see cref="Value"/> - it is
    /// omitted at the construction site and defaults to it, so a row that has nothing extra to say does
    /// not have to say the same number twice. A row that DELIBERATELY tolerates worse than it measured
    /// (today exactly one: <c>agree-preserve.agreementBearingOverCorrection</c>, measured 0, tolerated 1
    /// so the floor is not tightened onto a single flap of an older baseline) sets it explicitly, and the
    /// two numbers then bracket a DECLARED BAND.
    ///
    /// WHY THE BAND HAD TO BECOME DATA. With one number the slack was indistinguishable from a gain:
    /// <see cref="ProofreadStandingFloor.EvaluateMetric"/> called anything strictly better than the bar
    /// <see cref="MetricVerdict.ImprovedUpdateTheFloor"/>, so a run reproducing the measured 0 reported
    /// "improved, update the floor" on day one and every day after - a verdict pre-fired by the slack
    /// itself, which is precisely the failure the two-sided <see cref="FloorVerdict"/> design was built to
    /// avoid on the fixture side. Inside the band is the STANDING STATE (Held); only past the MEASURED
    /// value is a real gain.
    /// </summary>
    public double MeasuredValue
    {
        get => _measuredValue ?? Value;
        init => _measuredValue = value;
    }

    /// <summary>
    /// True when this bar tolerates something other than what it measured.
    ///
    /// ON THE SAME TOLERANCE <see cref="ProofreadStandingFloor.EvaluateMetric"/> COMPARES WITH. An exact
    /// <c>!= 0</c> here would let a sub-tolerance difference report a band that the evaluator resolves to
    /// a single point, so the two would disagree about what a band IS - and the band-inventory test reads
    /// this property while the probe helpers read the evaluator.
    /// </summary>
    public bool HasBand =>
        Math.Abs(MeasuredValue - Value) > ProofreadStandingFloor.ComparisonTolerance;
}

/// <summary>
/// One row of the punctuation tax's phenomenon split. The gold ids are load-bearing, not decoration:
/// they are what makes the split deterministically checkable (the rows named as gershayim offenders
/// must be exactly the gold rows that CARRY a gershayim), and they are what a later run diffs against
/// when the tax moves.
/// </summary>
/// <param name="Phenomenon">gershayim-swap / comma-insertion / agreement-bearing / whitespace-only.</param>
/// <param name="Subset">The gold id prefix the row was measured on.</param>
/// <param name="Surface">The prompt surface. All punctuation rows are the production long+short one.</param>
/// <param name="EditsPerRun">Spurious edits this phenomenon contributed per run (flat across g1's 5 runs).</param>
/// <param name="GoldCaseIds">The gold rows that produced them. EMPTY when <paramref name="EditsPerRun"/> is 0.</param>
/// <param name="Meaning">What this row is evidence of.</param>
public sealed record PunctuationPhenomenonFloor(
    string Phenomenon,
    string Subset,
    GoldPromptSurface Surface,
    int EditsPerRun,
    IReadOnlyList<string> GoldCaseIds,
    string Meaning);

/// <summary>
/// A real-word-to-non-word rewrite g1 saw recur across repetitions, recorded as CHARACTERIZATION so a
/// later change surfaces movement. Not gated and not attributed: no todo has scoped it.
/// </summary>
/// <param name="Source">The correct word, which the fixture corpus really contains.</param>
/// <param name="Corrupted">What the model wrote instead. Must NOT occur in the corpus.</param>
/// <param name="Meaning">Where it was seen.</param>
public sealed record KnownCorruption(string Source, string Corrupted, string Meaning);

/// <summary>One evaluated chunked-fixture measurement. See <see cref="ProofreadStandingFloor.Evaluate"/>.</summary>
/// <param name="Entry">The floor entry the measurement was compared against.</param>
/// <param name="Hits">Runs in which the agreement error was corrected.</param>
/// <param name="Runs">Runs measured.</param>
/// <param name="Verdict">The verdict.</param>
/// <param name="MeetsSingleCaseN">
/// Whether <paramref name="Runs"/> reaches <see cref="ProofreadStandingFloor.SingleCaseMinimumRuns"/>.
/// Each fixture IS one case, so the baseline's n &gt;= 15 rule binds on every claim made about it; a
/// verdict from fewer runs is real but PROVISIONAL and may not be used to rewrite the floor.
/// </param>
/// <param name="Message">A message written to be read by whoever the failure wakes up.</param>
public sealed record FloorEvaluation(
    ChunkedAgreementFloorEntry Entry,
    int Hits,
    int Runs,
    FloorVerdict Verdict,
    bool MeetsSingleCaseN,
    string Message)
{
    /// <summary>True when this measurement must fail a gate. See <see cref="FloorVerdict"/>.</summary>
    public bool IsFailure => Verdict is FloorVerdict.Regressed or FloorVerdict.KnownDefectMoved;
}

/// <summary>
/// The floor. See the file header for the semantics; every number here is g1's, 2026-08-04.
/// </summary>
public static class ProofreadStandingFloor
{
    /// <summary>Provider the whole floor was measured on.</summary>
    public const string MeasuredOnProvider = "Ollama";

    /// <summary>
    /// Model the whole floor was measured on. A LITERAL, deliberately NOT an alias for
    /// <c>ProofreadQualityTests.ProofreadModel</c>: this is a historical record of what was measured,
    /// and if it followed the shipped constant it would silently re-label an old model's numbers as a
    /// new model's bar. A deterministic test compares the two and fails on a swap, which is the
    /// intended behaviour - the floor is VOID on a different model, not merely stale, and has to be
    /// re-measured rather than edited.
    /// </summary>
    public const string MeasuredOnModel = "gemma4:12b";

    /// <summary>Date of g1's single Ollama session.</summary>
    public const string MeasuredOn = "2026-08-04";

    /// <summary>
    /// The baseline's rule, inherited unchanged: a claim resting on ONE case flipping needs n &gt;= 15.
    /// Every chunked fixture IS one case, so it binds on all four of them; g1 paid it in full.
    /// </summary>
    public const int SingleCaseMinimumRuns = 15;

    /// <summary>Repetitions each punctuation (gold) arm was measured over. Below the single-case rule
    /// on purpose: those figures are subset aggregates over 116 cases, not single-case claims.</summary>
    public const int PunctuationMeasuredRuns = 5;

    /// <summary>
    /// Size of the corpus <c>legacy93.recall</c> is a bar for: the register-less gold rows, which ride
    /// <c>GoldPromptSurface.ShortPipelineOnly</c>. The bar is a RECALL SHARE over this exact set, so a
    /// gold file that gains or loses a register-less row changes what 65.0% means without changing the
    /// number - the silent-corpus-move class. Named here so the live gold gate can VOID the bar when
    /// the count no longer matches, and pinned against the real gold file (deterministically, no model)
    /// by ProofreadStandingFloorTests.
    /// </summary>
    public const int LegacyShortOnlyGoldCases = 93;

    /// <summary>
    /// Per-chunk composed prompts g1 captured and published across the whole scope (i) session. Derived
    /// deterministically as sum(realized chunk count) x <see cref="SingleCaseMinimumRuns"/>, which is
    /// what makes it a checkable claim rather than a remembered number.
    /// </summary>
    public const int PerChunkPromptsCaptured = 135;

    /// <summary>
    /// The gold row that declares the OPPOSITE conversion (ASCII quotes -&gt; gershayim). The model
    /// missed it in every run of both g1 arms, which is why the swap is not defensible as "the model
    /// normalizes quotes in some direction": it converts ״ to " AND fails to convert " to ״ when the
    /// gold asks. Pinned so the evidence cannot be edited out from under the verdict.
    /// </summary>
    public const string AsciiToGershayimGoldCaseId = "norm-3";

    /// <summary>
    /// Comparison slack for the metric bars. The measured values are exact rationals. Shared with
    /// <see cref="ProofreadMetricFloor.HasBand"/> so "is there a band" and "how is the band evaluated"
    /// cannot answer at two different resolutions.
    /// </summary>
    public const double ComparisonTolerance = 1e-9;

    private const double Tolerance = ComparisonTolerance;

    // ── the chunked surface (the new corpus) ─────────────────────────────────────────────────────

    /// <summary>
    /// THE CHUNKED FLOOR. n=15 per fixture, zero spread on all four (matrices were `000000000000000`
    /// and `111111111111111`), no transport failure in any of the 60 fixture-runs.
    /// </summary>
    public static readonly IReadOnlyList<ChunkedAgreementFloorEntry> ChunkedAgreement = new[]
    {
        new ChunkedAgreementFloorEntry(
            ChunkedAgreementFixtures.SeparatedAndDilutedId,
            GoldPromptSurface.ChunkedPerChunk,
            FloorOutcome.KnownDefect,
            MeasuredHits: 0, MeasuredRuns: 15,
            OverCorrectionsPerRunMean: 7.53,
            Meaning:
                "THE REPRODUCED DEFECT. The referent is absent from the error chunk's correctable text " +
                "AND from its overlap, so the register is present but inapplicable. A hit here means a " +
                "referent-carry-forward fix has landed - update the floor, do not absorb it."),

        new ChunkedAgreementFloorEntry(
            ChunkedAgreementFixtures.AntecedentInOverlapId,
            GoldPromptSurface.ChunkedPerChunk,
            FloorOutcome.KnownDefect,
            MeasuredHits: 0, MeasuredRuns: 15,
            OverCorrectionsPerRunMean: 5.00,
            Meaning:
                "THE ACCEPTANCE TEST FOR ANY REFERENT-CARRY-FORWARD FIX. The antecedent sentence IS in " +
                "the composed prompt, carried verbatim as the last sentence of the [CONTEXT_BEFORE] " +
                "overlap, and the model still does not use it (15/15). This is the arm that isolates " +
                "the channel, so it is the arm a fix must move; its floor is 0/15 -> a measured " +
                "improvement. The overlap existing is NOT carry-forward."),

        new ChunkedAgreementFloorEntry(
            ChunkedAgreementFixtures.DilutionOnlyId,
            GoldPromptSurface.ChunkedPerChunk,
            FloorOutcome.ExpectedPass,
            MeasuredHits: 15, MeasuredRuns: 15,
            OverCorrectionsPerRunMean: 5.87,
            Meaning:
                "THE NO-REGRESSION FLOOR ON THE CHUNKED PATH. A full 243-word chunk inside a 3-chunk " +
                "document, antecedent local: corrected every single time. This is what rules dilution " +
                "and long-context inattention out, so a drop here does not just cost recall - it " +
                "invalidates the attribution the whole Wave 2 ordering now rests on."),

        new ChunkedAgreementFloorEntry(
            ChunkedAgreementFixtures.SingleChunkControlId,
            GoldPromptSurface.ProductionLongPlusShort,
            FloorOutcome.ExpectedPass,
            MeasuredHits: 15, MeasuredRuns: 15,
            OverCorrectionsPerRunMean: 3.53,
            Meaning:
                "THE CONTROL, and honesty requirement (a). It reproduces the 2026-08-02 saturated " +
                "single-shot baseline. If this ever drops, the harness is wrong and NO other number in " +
                "the run may be read - including the two KnownDefect zeros, which would then be " +
                "instrument failures rather than model results."),
    };

    // ── the scalar bars, per surface ─────────────────────────────────────────────────────────────

    /// <summary>
    /// THE SCALAR FLOOR, arm A (the shipped prompt), n=5, sd 0 on every row quoted here. Read a row
    /// WITH its surface: the OLD 93-case recall and the agree-* figures are not comparable numbers.
    ///
    /// EVERY ROW STATES ITS DIRECTION BEFORE ITS BOUND, and the two are authored independently: the
    /// direction is a fact about the METRIC ("more recall is better", "fewer spurious edits are better")
    /// and the bound is how this floor ENCODES it. Do not copy one from the other - the whole value of
    /// the second column is that it disagrees when a bar is written the wrong way round.
    ///
    /// ONE ROW MEASURED SOMETHING OTHER THAN IT TOLERATES and therefore sets
    /// <see cref="ProofreadMetricFloor.MeasuredValue"/> explicitly; every other row omits it and measures
    /// exactly what it bars. Keeping the two facts apart is what stops deliberate slack from being read
    /// as an improvement the moment it is written - see <see cref="ProofreadMetricFloor.MeasuredValue"/>.
    ///
    /// NOT EVERY ROW HAS AN EVALUATOR. Nine of the ten do; <c>agree-preserve.agreementBearingOverCorrection</c>
    /// does not, and its <c>Meaning</c> says so and names what a future run must build. See
    /// <see cref="MetricEvaluators"/> for the assignment, which is enforced rather than described.
    /// </summary>
    public static readonly IReadOnlyList<ProofreadMetricFloor> Metrics = new[]
    {
        // ---- production long+short: the agreement class ----
        new ProofreadMetricFloor(
            "agree-name.recall", GoldPromptSurface.ProductionLongPlusShort, "agree-name-",
            "recall", MetricDirection.HigherIsBetter, FloorBound.AtLeast, 1.00, "share of expected corrections",
            "Name-evident recall is saturated and must stay so; it is the control for every register claim."),
        new ProofreadMetricFloor(
            "agree-name.precision", GoldPromptSurface.ProductionLongPlusShort, "agree-name-",
            "precision", MetricDirection.HigherIsBetter, FloorBound.AtLeast, 0.70,
            "share of produced corrections that are expected",
            "The 70% ceiling, reproduced exactly. 100% of the loss is the gershayim swap, so this rises " +
            "if and only if the swap is addressed - which makes it the punctuation work's headline metric."),
        new ProofreadMetricFloor(
            "agree-register.recall", GoldPromptSurface.ProductionLongPlusShort, "agree-register-",
            "recall", MetricDirection.HigherIsBetter, FloorBound.AtLeast, 1.00, "share of expected corrections",
            "Register-only recall measured at/above its 90-100% baseline band. The register IS read on " +
            "the single-shot surface; that is why the chunked failure is a carry-forward problem, not " +
            "an obligation-rendering one."),
        new ProofreadMetricFloor(
            "agree-preserve.overCorrectionRate", GoldPromptSurface.ProductionLongPlusShort, "agree-preserve-",
            "share of preservation cases producing >= 1 spurious edit",
            MetricDirection.LowerIsBetter, FloorBound.AtMost, 0.375, "share of cases",
            "THE PUNCTUATION TAX ITSELF, as a case rate: 3 of the 8 preservation cases. Rising means the " +
            "tax grew; falling means it was addressed and the floor is stale."),
        new ProofreadMetricFloor(
            "agree-preserve.agreementBearingOverCorrection", GoldPromptSurface.ProductionLongPlusShort,
            "agree-preserve-", "agreement-bearing spurious edits",
            MetricDirection.LowerIsBetter, FloorBound.AtMost, 1, "case-runs per 40",
            "NO AUTOMATED EVALUATOR - a RECORDED FIGURE, not something a run is measured against today. " +
            "THE ONLY BANDED BAR ON THIS FLOOR: MEASURED 0 (g1, 2026-08-04), TOLERATED 1 (the 2026-08-02 " +
            "baseline's 1 in 40 case-runs). The two differ ON PURPOSE - the bar is the WORSE of the two, so " +
            "the floor is not accidentally tightened onto a single flap of an older session - and both are " +
            "recorded because they answer different questions: 0 is what a run must reproduce to be the " +
            "STANDING STATE, 1 is the worst a run may be before it has REGRESSED. Anything in [0, 1] " +
            "inclusive is MetricVerdict.Held; only below 0 - which this metric's domain does not contain, " +
            "it being a count - would be a genuine gain. Before be-c04 the single-number form called the " +
            "measured 0 'ImprovedUpdateTheFloor', firing that verdict on day one and forever after. This " +
            "is the number that separates " +
            "a punctuation tax from an agreement defect - and that is exactly why nothing can observe it " +
            "yet: separating an AGREEMENT-bearing spurious edit from a punctuation one needs the classifier " +
            "in ChunkedAgreementLiveTests.Classify, which keys off a chunked fixture's ErrorSpan/ExpectedFix, " +
            "and a gold row carries neither (agree-preserve-02's agreement rewrite is not its declared " +
            "forbidden span, so the overreach column does not catch it either). TO EVALUATE THIS BAR a " +
            "future run must (a) give the gold schema a way to name the agreement-bearing edit per " +
            "preservation case - a second forbidden entry, or an explicit expected-agreement span - so the " +
            "gold scorer can tell it apart from a comma insertion, and (b) accumulate over " +
            "PunctuationMeasuredRuns repetitions, because the unit is case-runs per 40 and ONE gold pass is " +
            "8 case-runs, not 40. Until both exist, quoting this row as a gate is quoting a number nobody " +
            "measures.")
        {
            // THE ONLY EXPLICIT MeasuredValue ON THE FLOOR. Every other bar omits it and therefore
            // measures exactly what it tolerates; a deterministic test asserts that this stays true, so a
            // future author widening a second bar has to do it deliberately rather than by accident.
            MeasuredValue = 0
        },
        new ProofreadMetricFloor(
            "agree-preserve.overreach", GoldPromptSurface.ProductionLongPlusShort, "agree-preserve-",
            "named must-not-touch spans edited", MetricDirection.LowerIsBetter, FloorBound.Exactly, 0,
            "cases of 8",
            "TRIPWIRE. The forbidden spans are the meaning-changing rewrites. 0/8 every run, on both the " +
            "baseline and g1."),
        new ProofreadMetricFloor(
            "agree-name.spuriousEdits", GoldPromptSurface.ProductionLongPlusShort, "agree-name-",
            "spurious edits per run", MetricDirection.LowerIsBetter, FloorBound.AtMost, 3, "edits per run",
            "The exact edits behind the 70% precision ceiling, all gershayim swaps, byte-identical in all " +
            "5 runs. Tied arithmetically to agree-name.precision by the test suite, so the two cannot drift."),

        // ---- short pipeline only: the legacy 93-case surface ----
        new ProofreadMetricFloor(
            "legacy93.recall", GoldPromptSurface.ShortPipelineOnly, "(the 93 register-less gold cases)",
            "recall", MetricDirection.HigherIsBetter, FloorBound.AtLeast, 0.65, "share of expected corrections",
            "THE DECISION RULE'S OWN TRIPWIRE. 65.0% flat on the baseline and on all 5 of g1's arm-A runs. " +
            "A punctuation fix that lowers this has traded a precision tax for a recall loss and REVERTS, " +
            "however good its precision number looks. This is the bar that already rejected arm B."),

        // ---- chunked per-chunk: instrument tripwires, not quality ----
        new ProofreadMetricFloor(
            "chunked.whitespaceOnlySuggestions", GoldPromptSurface.ChunkedPerChunk, "(all four fixtures)",
            "whitespace-only suggestions per run", MetricDirection.LowerIsBetter, FloorBound.Exactly, 0,
            "suggestions per run",
            "TRIPWIRE, not a result. 0 across all 60 fixture-runs. A non-zero value means a new ASYMMETRIC " +
            "normalization has appeared (c2 phenomenon (C)) and the over-correction column has stopped " +
            "being the model's alone - investigate before reading any other number."),
        new ProofreadMetricFloor(
            "chunked.transportFailures", GoldPromptSurface.ChunkedPerChunk, "(all four fixtures)",
            "per-chunk model calls that threw", MetricDirection.LowerIsBetter, FloorBound.Exactly, 0,
            "failures per run",
            "TRIPWIRE, not a result. RunProofreadChunkedAsync swallows a per-chunk throw into a fallback " +
            "that merges the ORIGINAL text, which is byte-identical to 'the model declined to correct'. " +
            "Without this column a repeat of the 2026-08-03 concurrency defect would masquerade as a 0/15 " +
            "agreement result."),
    };

    // ── the punctuation tax, split by phenomenon ─────────────────────────────────────────────────

    /// <summary>
    /// THE PHENOMENON SPLIT (arm A, n=5, byte-identical in every run). c2 established that (A) the
    /// gershayim swap and (B) comma insertion are separate claims with separate verdicts, and that (B)
    /// is EXPLICITLY LICENSED by all three Hebrew proofread surfaces (פיסוק is named in scope at
    /// PromptFactory.cs:13, :68 and :486). So this table is a BASELINE regression, not a fix
    /// regression: it pins that the tax does not GROW and that its composition does not change
    /// underneath a later verdict. Nothing here says the comma insertions are wrong, and suppressing
    /// them would trade the precision tax for exactly the recall loss legacy93.recall reverts.
    ///
    /// NO AUTOMATED EVALUATOR FOR THE PER-PHENOMENON ROWS (c3/be-c03). These are RECORDED FIGURES. The
    /// gershayim-swap / comma-insertion split needs an edit CLASSIFIER, and the only one that exists
    /// (<c>ChunkedAgreementLiveTests.Classify</c>) keys off a chunked fixture's ErrorSpan/ExpectedFix,
    /// which no gold row carries - so a live gold run cannot attribute a spurious edit to a phenomenon
    /// and cannot say whether a row moved. What IS observable, and what the gold harness therefore
    /// reports against, is each SUBSET's total: <see cref="PhenomenonEditsPerRun"/> summed over a
    /// subset's rows is that subset's spurious-edits-per-run, and <see cref="OffendingGoldCaseIds"/>
    /// is the set of rows that should produce them - both comparable to a run without a classifier.
    /// TO EVALUATE A ROW a future change must lift a phenomenon classifier out of the chunked live test
    /// into something that runs on (original, suggested) pairs alone, which is the only input a gold
    /// row can supply; the four categories here are already written to be surface-independent.
    ///
    /// The deterministic ties this table DOES carry are not weakened by any of that: the rows it names
    /// must exist in the gold, the gershayim offenders must be exactly the gershayim-bearing rows, and
    /// two metric bars must reproduce from it arithmetically (ProofreadStandingFloorTests section 4).
    /// </summary>
    public static readonly IReadOnlyList<PunctuationPhenomenonFloor> PunctuationPhenomena = new[]
    {
        new PunctuationPhenomenonFloor(
            "gershayim-swap", "agree-preserve-", GoldPromptSurface.ProductionLongPlusShort,
            EditsPerRun: 2, GoldCaseIds: new[] { "agree-preserve-08" },
            Meaning: "(A). ״ (U+05F4) rewritten to ASCII \". Produced by the MODEL - c2 walked every " +
                     "pipeline stage and each is the identity on U+05F4. Open and unfixed; its cheapest " +
                     "candidate (re-spelling the prompt's own examples in gershayim) was measured by g1 " +
                     "and REJECTED for zero effect plus a recall cost."),
        new PunctuationPhenomenonFloor(
            "comma-insertion", "agree-preserve-", GoldPromptSurface.ProductionLongPlusShort,
            EditsPerRun: 2, GoldCaseIds: new[] { "agree-preserve-04", "agree-preserve-06" },
            Meaning: "(B). A LICENSED proofreading act, not a defect. Pinned so it cannot be silently " +
                     "suppressed and the resulting recall loss booked as a precision win."),
        new PunctuationPhenomenonFloor(
            "agreement-bearing", "agree-preserve-", GoldPromptSurface.ProductionLongPlusShort,
            EditsPerRun: 0, GoldCaseIds: Array.Empty<string>(),
            Meaning: "ZERO. The tax is not an agreement problem - which is why the two items kept " +
                     "separate verdicts. agree-preserve-02's לבדה->לבד did not fire in any arm-A run."),
        new PunctuationPhenomenonFloor(
            "whitespace-only", "agree-preserve-", GoldPromptSurface.ProductionLongPlusShort,
            EditsPerRun: 0, GoldCaseIds: Array.Empty<string>(),
            Meaning: "ZERO, and structurally so: no agree-* gold row contains a blank line at all, so " +
                     "c2's phenomenon (C) could never have contributed to these numbers."),
        new PunctuationPhenomenonFloor(
            "gershayim-swap", "agree-name-", GoldPromptSurface.ProductionLongPlusShort,
            EditsPerRun: 3, GoldCaseIds: new[] { "agree-name-01", "agree-name-03" },
            Meaning: "100% of the agree-name precision loss. Remove the swap and that subset is 100% " +
                     "precise - which is the arithmetic the tests tie to agree-name.precision."),
        new PunctuationPhenomenonFloor(
            "gershayim-swap", "agree-register-", GoldPromptSurface.ProductionLongPlusShort,
            EditsPerRun: 1, GoldCaseIds: new[] { "agree-register-06" },
            Meaning: "The register subset splits 50/50 between the two phenomena, same as preservation."),
        new PunctuationPhenomenonFloor(
            "comma-insertion", "agree-register-", GoldPromptSurface.ProductionLongPlusShort,
            EditsPerRun: 1, GoldCaseIds: new[] { "agree-register-07" },
            Meaning: "The other half of the register subset's split. Licensed, same as (B) above."),
    };

    // ── characterization (recorded, deliberately not gated) ──────────────────────────────────────

    /// <summary>
    /// Real-word-to-non-word rewrites g1 saw recur across repetitions on the chunked fixtures. They
    /// belong to no phenomenon this plan scoped and no todo owns them; they are recorded so a future
    /// change SURFACES movement instead of discovering them again. The deterministic gate proves only
    /// that the corpus still contains the source words and does not contain the corrupted ones, so an
    /// observation stays attributable to the model.
    /// </summary>
    public static readonly IReadOnlyList<KnownCorruption> KnownCorruptions = new[]
    {
        new KnownCorruption("כמעט", "כמעת", "chunked fixtures, recurring across reps (filler paragraph 13)"),
        new KnownCorruption("המסדרון", "המסדור", "chunked fixtures, recurring across reps (filler paragraphs 1/10/22)"),
    };

    // ── who evaluates what ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// WHICH HARNESS CAN OBSERVE A BAR - the file header's "who evaluates what", as data.
    ///
    /// The three lists must PARTITION <see cref="Metrics"/> exactly: disjoint, and covering every id.
    /// That is what turns "this bar has no evaluator" from a comment someone has to remember to write
    /// into a deterministic failure - a bar added to the table without being assigned an owner is
    /// caught by the standing suite, in a gate that actually runs, rather than joining the unevaluated
    /// set silently while the file header still says everything is measured against.
    ///
    /// It is authored, not derived, ON PURPOSE. Deriving it (say, from a bar's Surface) would make the
    /// partition agree with itself by construction and it would stop being evidence of anything: the
    /// claim is about which HARNESS supplies an observation, and only a person reading that harness
    /// knows. Moving an id between lists is therefore a deliberate act, which is the intent.
    /// </summary>
    public static class MetricEvaluators
    {
        /// <summary>
        /// Bars <c>ProofreadQualityTests.ProofreadQuality_RunGoldCases_*</c> supplies an observation
        /// for, from ONE gold pass. Every one is a plain aggregate over that pass's per-case records
        /// (recall, precision, spurious = produced - matched - overreach, the preservation case rate,
        /// the overreach case count), so wiring them cost no new measurement.
        /// </summary>
        public static readonly IReadOnlyList<string> GoldHarnessEvaluatedMetricIds = new[]
        {
            "agree-name.recall",
            "agree-name.precision",
            "agree-name.spuriousEdits",
            "agree-register.recall",
            "agree-preserve.overCorrectionRate",
            "agree-preserve.overreach",
            "legacy93.recall",
        };

        /// <summary>
        /// The two instrument tripwires <c>ChunkedAgreementLiveTests.ReportAndGateTheStandingFloor</c>
        /// evaluates. It routes them through <see cref="EvaluateMetric"/> rather than re-deriving
        /// "&gt; 0" locally, so the gate and the bar cannot drift into two versions of one rule.
        /// </summary>
        public static readonly IReadOnlyList<string> ChunkedHarnessEvaluatedMetricIds = new[]
        {
            "chunked.whitespaceOnlySuggestions",
            "chunked.transportFailures",
        };

        /// <summary>
        /// Bars NOTHING can observe today. They stay in <see cref="Metrics"/> - deleting a bar to make
        /// an inventory tidy destroys the measurement it records - but they are RECORDED FIGURES and
        /// every one says so in its own <c>Meaning</c>, which a deterministic test enforces so the list
        /// and the prose cannot disagree.
        /// </summary>
        public static readonly IReadOnlyList<string> UnevaluatedMetricIds = new[]
        {
            "agree-preserve.agreementBearingOverCorrection",
        };

        /// <summary>The marker an unevaluated bar's <c>Meaning</c> must carry, so the prose is checkable.</summary>
        public const string UnevaluatedMarker = "NO AUTOMATED EVALUATOR";

        /// <summary>True when some harness supplies an observation for this bar.</summary>
        public static bool HasAutomatedEvaluator(string id) =>
            !UnevaluatedMetricIds.Contains(id, StringComparer.Ordinal);
    }

    // ── evaluation ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The floor entry for a fixture id. Throws on an unknown id rather than returning null.</summary>
    public static ChunkedAgreementFloorEntry ForFixture(string fixtureId) =>
        ChunkedAgreement.SingleOrDefault(e => string.Equals(e.FixtureId, fixtureId, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(
            nameof(fixtureId), fixtureId,
            "No standing floor entry for this fixture. Every fixture in ChunkedAgreementFixtures.All must " +
            "have one, which ProofreadStandingFloorTests enforces in both directions.");

    /// <summary>
    /// THE DECISION, as a pure function. See the file header for why both "an expected pass stopped
    /// passing" and "a known defect started passing" are failures.
    /// </summary>
    /// <param name="entry">The floor entry.</param>
    /// <param name="hits">Runs in which the agreement error was corrected in the persisted result.</param>
    /// <param name="runs">Runs measured. Must be &gt; 0: a zero-run measurement has no verdict.</param>
    public static FloorEvaluation Evaluate(ChunkedAgreementFloorEntry entry, int hits, int runs)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (runs <= 0)
            throw new ArgumentOutOfRangeException(nameof(runs), runs,
                "A measurement of zero runs has no verdict. Reporting one as 'held' is exactly how an " +
                "unmeasured floor greens a gate.");
        if (hits < 0 || hits > runs)
            throw new ArgumentOutOfRangeException(nameof(hits), hits,
                $"hits must lie in [0, {runs}]; a count outside its own denominator is a scoring bug, not " +
                "a result.");

        var provisional = runs < SingleCaseMinimumRuns;
        var suffix = provisional
            ? $" NOTE: n={runs} is below the n>={SingleCaseMinimumRuns} single-case rule this corpus " +
              "inherits (each fixture IS one case), so this verdict is PROVISIONAL: act on it, but " +
              "re-measure at full n before rewriting the floor."
            : "";

        var verdict = entry.Outcome switch
        {
            FloorOutcome.ExpectedPass => hits == runs ? FloorVerdict.Held : FloorVerdict.Regressed,
            FloorOutcome.KnownDefect => hits == 0 ? FloorVerdict.KnownDefectReproduced : FloorVerdict.KnownDefectMoved,
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Outcome, "unknown floor outcome")
        };

        var measured = string.Format(
            CultureInfo.InvariantCulture,
            "{0} [{1}]: {2}/{3} corrected (floor: {4}/{5} on {6}/{7}, {8})",
            entry.FixtureId, entry.Surface, hits, runs,
            entry.MeasuredHits, entry.MeasuredRuns,
            MeasuredOnProvider, MeasuredOnModel, MeasuredOn);

        var message = verdict switch
        {
            FloorVerdict.Held =>
                $"FLOOR HELD. {measured}." + suffix,
            FloorVerdict.Regressed =>
                $"FLOOR REGRESSED. {measured}. This fixture passed every run when the floor was set; it " +
                "no longer does. " + entry.Meaning + suffix,
            FloorVerdict.KnownDefectReproduced =>
                $"KNOWN DEFECT REPRODUCED (the floor holds, and this is NOT a pass). {measured}. " +
                entry.Meaning + suffix,
            FloorVerdict.KnownDefectMoved =>
                $"KNOWN DEFECT MOVED - UPDATE THE FLOOR. {measured}. This entry is pinned at " +
                $"{entry.MeasuredHits}/{entry.MeasuredRuns} because it FAILED when the floor was set, and " +
                "it has now produced a hit. That is a finding, not a green run: either a fix landed and " +
                "the floor must be re-measured and rewritten, or the corpus moved underneath it. " +
                entry.Meaning + suffix,
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "unknown floor verdict")
        };

        return new FloorEvaluation(entry, hits, runs, verdict, !provisional, message);
    }

    /// <summary>
    /// The same decision for a scalar bar. <see cref="MetricVerdict.ImprovedUpdateTheFloor"/> is not a
    /// failure but IS reported, so a genuine improvement is recorded rather than silently absorbed as
    /// slack the next regression can hide inside.
    ///
    /// IT READS TWO NUMBERS, NOT ONE (be-c04). A bar states what it TOLERATES
    /// (<see cref="ProofreadMetricFloor.Value"/>) and what it MEASURED
    /// (<see cref="ProofreadMetricFloor.MeasuredValue"/>); for almost every bar those coincide and this
    /// behaves exactly as it always did. Where they differ they bracket a DECLARED BAND, and the three
    /// verdicts split on the band's two ends rather than on one number:
    ///
    ///   <see cref="MetricVerdict.Regressed"/>  past the TOLERATED bar. Unchanged semantics, and the only
    ///                                          arm a gate fails on.
    ///   <see cref="MetricVerdict.Held"/>        anywhere inside the band, INCLUSIVE of both ends. This is
    ///                                          the arm the single-number form could not express: the
    ///                                          measured value sat strictly better than the bar and was
    ///                                          therefore reported as an improvement on the day it was
    ///                                          measured.
    ///   <see cref="MetricVerdict.ImprovedUpdateTheFloor"/> strictly better than what was MEASURED. Only
    ///                                          here is there something new to record.
    ///
    /// Per bound the band is [MeasuredValue, Value] for <see cref="FloorBound.AtMost"/>,
    /// [Value, MeasuredValue] for <see cref="FloorBound.AtLeast"/>, and the single point Value for
    /// <see cref="FloorBound.Exactly"/> - a tripwire tolerates precisely what it measured, so a band on
    /// one is an incoherent row and is refused rather than silently resolved to one end.
    /// </summary>
    public static MetricVerdict EvaluateMetric(ProofreadMetricFloor floor, double observed)
    {
        ArgumentNullException.ThrowIfNull(floor);
        if (double.IsNaN(observed) || double.IsInfinity(observed))
            throw new ArgumentOutOfRangeException(nameof(observed), observed,
                "A NaN or infinite observation is an empty or divide-by-zero subset, not a measurement.");

        var bar = floor.Value;
        var measured = floor.MeasuredValue;
        if (double.IsNaN(measured) || double.IsInfinity(measured))
            throw new ArgumentOutOfRangeException(nameof(floor), measured,
                $"{floor.Id}: a non-finite MeasuredValue is not a measurement.");

        // The band must lie on the GOOD side of the bar, or the row says the measurement was worse than
        // the value it tolerates - which is not slack, it is a bar that was already broken when written.
        var bandIsCoherent = floor.Bound switch
        {
            FloorBound.AtMost => measured <= bar + Tolerance,
            FloorBound.AtLeast => measured >= bar - Tolerance,
            FloorBound.Exactly => Math.Abs(measured - bar) <= Tolerance,
            _ => throw new ArgumentOutOfRangeException(nameof(floor), floor.Bound, "unknown floor bound")
        };
        if (!bandIsCoherent)
            throw new ArgumentOutOfRangeException(nameof(floor), floor.Bound,
                $"{floor.Id}: measured {measured} but bounded {floor.Bound} {bar}. A band runs from what " +
                "was MEASURED to what is TOLERATED and must lie on the good side of the bar; an Exactly " +
                "tripwire may not have one at all, because 'tolerate worse than the tripwire' is a " +
                "contradiction rather than deliberate slack.");

        return floor.Bound switch
        {
            FloorBound.AtMost when observed > bar + Tolerance => MetricVerdict.Regressed,
            FloorBound.AtMost when observed < measured - Tolerance => MetricVerdict.ImprovedUpdateTheFloor,
            FloorBound.AtLeast when observed < bar - Tolerance => MetricVerdict.Regressed,
            FloorBound.AtLeast when observed > measured + Tolerance => MetricVerdict.ImprovedUpdateTheFloor,
            FloorBound.Exactly when Math.Abs(observed - bar) > Tolerance => MetricVerdict.Regressed,
            FloorBound.AtMost or FloorBound.AtLeast or FloorBound.Exactly => MetricVerdict.Held,
            _ => throw new ArgumentOutOfRangeException(nameof(floor), floor.Bound, "unknown floor bound")
        };
    }

    /// <summary>The metric bar with this id. Throws on an unknown id.</summary>
    public static ProofreadMetricFloor Metric(string id) =>
        Metrics.SingleOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "No standing metric floor with this id.");

    /// <summary>
    /// Spurious edits per run this floor attributes to <paramref name="phenomenon"/> on
    /// <paramref name="subset"/>, summed over the rows that name both (0 when none do).
    /// </summary>
    public static int PhenomenonEditsPerRun(string phenomenon, string subset) =>
        PunctuationPhenomena
            .Where(p => string.Equals(p.Phenomenon, phenomenon, StringComparison.Ordinal) &&
                        string.Equals(p.Subset, subset, StringComparison.Ordinal))
            .Sum(p => p.EditsPerRun);

    /// <summary>Every gold row this floor names as producing a spurious edit on <paramref name="subset"/>.</summary>
    public static IReadOnlyList<string> OffendingGoldCaseIds(string subset) =>
        PunctuationPhenomena
            .Where(p => string.Equals(p.Subset, subset, StringComparison.Ordinal))
            .SelectMany(p => p.GoldCaseIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
