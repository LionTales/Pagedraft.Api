using System;
using System.Collections.Generic;
using System.Linq;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// Which of the two proofread prompt surfaces a gold case is measured on. The proofread gold file
/// holds BOTH, and their numbers are not comparable to each other, so every aggregate reported
/// against that file has to say which surface it measured.
/// </summary>
internal enum GoldPromptSurface
{
    /// <summary>
    /// The caller leaves <c>AiRequest.Instruction</c> null and the router sends the SHORT legacy
    /// pipeline instruction (<c>PromptFactory.GetPrompt(Proofread, lang)</c>) ALONE. This is the
    /// historical harness surface every pre-2026-08-02 gold number was measured on.
    /// </summary>
    ShortPipelineOnly,

    /// <summary>
    /// The production long+short shape: <c>[CHARACTER_REGISTER]</c> block + <c>ProofreadHe/En</c> body
    /// (both built by <c>PromptFactory.BuildProofreadChunkPrompt</c>) + the short pipeline instruction
    /// the router appends after them.
    /// </summary>
    ProductionLongPlusShort
}

/// <summary>
/// One gold case's scoring outcome, tagged with the prompt surface it rode. The scorer emits one of
/// these per case so a single model pass can be aggregated MULTIPLE ways (short-only / production /
/// all) without re-running the model, which would double the cost of every GPU sweep.
/// </summary>
/// <param name="Id">Gold case id (diagnostics only; not read by the aggregation).</param>
/// <param name="Surface">Which prompt surface this case was sent on.</param>
/// <param name="Expected">Expected corrections declared by the case (recall denominator).</param>
/// <param name="Produced">Corrections the production diff extracted from the model output.</param>
/// <param name="Matched">Produced corrections that matched an expected one.</param>
/// <param name="NoChangeCase">The case declares <c>shouldHaveNoChanges</c>.</param>
/// <param name="NoChangeWithCorrection">A <c>shouldHaveNoChanges</c> case that produced a correction.</param>
/// <param name="Errored">The case threw (timeout/OOM/etc.) and produced nothing.</param>
/// <param name="InputTokens">Provider-reported input tokens (cloud providers only).</param>
/// <param name="OutputTokens">Provider-reported output tokens (cloud providers only).</param>
/// <param name="OverreachEdits">Produced corrections that hit one of the case's forbidden edits.</param>
/// <param name="DeclaresForbidden">The case declares at least one forbidden edit.</param>
/// <param name="OverreachHit">The case tripped at least one forbidden edit.</param>
internal readonly record struct GoldCaseScore(
    string Id,
    GoldPromptSurface Surface,
    int Expected,
    int Produced,
    int Matched,
    bool NoChangeCase,
    bool NoChangeWithCorrection,
    bool Errored,
    int InputTokens,
    int OutputTokens,
    int OverreachEdits,
    bool DeclaresForbidden,
    bool OverreachHit);

/// <summary>
/// The three aggregates one scoring pass yields: each surface on its own, plus the MIXED total over
/// everything. <see cref="All"/> is deliberately kept, because it is what the harness historically
/// printed, but it blends two prompt surfaces and is therefore not a figure any model comparison may
/// be read off. Callers must label it as such.
/// </summary>
internal readonly record struct SurfaceSplitScores(
    ProofreadQualityTests.ModelScore ShortOnly, int ShortOnlyCases,
    ProofreadQualityTests.ModelScore Production, int ProductionCases,
    ProofreadQualityTests.ModelScore All, int AllCases)
{
    /// <summary>
    /// True when the scored set rides ONE surface only (an id-subsetted bake-off, or the English gold,
    /// where no case carries a character register). In that case <see cref="All"/> is identical to the
    /// populated subset and printing it as a separate "mixed" block would be noise, not information.
    ///
    /// An EMPTY record set also reports true (neither side is populated), which is vacuously right but
    /// is NOT a shape the two consumers can reach: both return early when the gold loads or subsets to
    /// zero cases, and the scorer emits exactly one record per case including the error path, so an
    /// empty record set implies an empty case set. A caller that does not have that guarantee must
    /// check the case count itself rather than read this flag as "one surface is populated".
    /// </summary>
    public bool IsSingleSurface => ShortOnlyCases == 0 || ProductionCases == 0;
}

/// <summary>
/// The ONE place the proofread gold is partitioned by prompt surface, and the ONE place a scored pass
/// is aggregated per surface.
///
/// WHY THIS EXISTS: <c>proofread-gold.json</c> holds cases on two different prompt surfaces (see
/// <see cref="GoldPromptSurface"/>). Reporting a single blended aggregate over both makes the headline
/// number unreproducible and silently changes the corpus a model-ship decision is read off. Both
/// consumers (the single-model Fact and the bake-off table) therefore report per surface.
///
/// DERIVED, NOT ENCODED: <see cref="SurfaceOf"/> reads the SAME condition
/// <c>ProofreadQualityTests.BuildGoldRequest</c> branches on (<c>CharacterRegister is { Length: &gt; 0 }</c>),
/// so a future register-carrying case lands on the right side no matter what its id looks like. Never
/// replace this with an id-prefix allowlist, a substring match on "agree-", or a positional split:
/// those encode today's population instead of deriving the surface, and would silently drift from the
/// request builder. <c>ProofreadAgreementGoldTests</c> pins the agreement by calling BOTH functions
/// for every gold case.
/// </summary>
internal static class GoldPromptSurfaces
{
    /// <summary>
    /// The prompt surface a gold case is sent on, derived from the same condition
    /// <c>ProofreadQualityTests.BuildGoldRequest</c> uses to decide whether to build an instruction.
    /// </summary>
    internal static GoldPromptSurface SurfaceOf(HebrewRegressionCase c) =>
        c.CharacterRegister is { Length: > 0 }
            ? GoldPromptSurface.ProductionLongPlusShort
            : GoldPromptSurface.ShortPipelineOnly;

    /// <summary>Gold cases riding <paramref name="surface"/>, file order preserved.</summary>
    internal static HebrewRegressionCase[] OnSurface(
        IEnumerable<HebrewRegressionCase> cases, GoldPromptSurface surface) =>
        cases.Where(c => SurfaceOf(c) == surface).ToArray();

    /// <summary>
    /// Sum per-case records into the aggregate the harness reports. Every field is a plain sum or
    /// count of the per-case records, which is exactly what the scorer's running totals used to be,
    /// so the resulting <c>ModelScore</c> over the FULL record set is identical to the pre-split one.
    /// An EMPTY record set aggregates to all-zero, and every derived rate on <c>ModelScore</c> guards
    /// its denominator, so an empty subset yields 0.0 rates and "n/a" precision rather than NaN.
    /// </summary>
    internal static ProofreadQualityTests.ModelScore Aggregate(IEnumerable<GoldCaseScore> records)
    {
        var list = records as IReadOnlyCollection<GoldCaseScore> ?? records.ToArray();
        return new ProofreadQualityTests.ModelScore(
            TotalExpected: list.Sum(r => r.Expected),
            TotalProduced: list.Sum(r => r.Produced),
            TotalMatched: list.Sum(r => r.Matched),
            NoChangeCases: list.Count(r => r.NoChangeCase),
            NoChangeWithCorrection: list.Count(r => r.NoChangeWithCorrection),
            Errors: list.Count(r => r.Errored),
            InputTokens: list.Sum(r => r.InputTokens),
            OutputTokens: list.Sum(r => r.OutputTokens),
            OverreachEdits: list.Sum(r => r.OverreachEdits),
            OverreachCases: list.Count(r => r.DeclaresForbidden),
            OverreachCaseHits: list.Count(r => r.OverreachHit));
    }

    /// <summary>
    /// Partition ONE scored pass into its per-surface aggregates plus the mixed total. No second model
    /// pass: the records were produced by a single run and are only grouped here.
    /// </summary>
    internal static SurfaceSplitScores Split(IEnumerable<GoldCaseScore> records)
    {
        var all = records as IReadOnlyList<GoldCaseScore> ?? records.ToArray();
        var shortOnly = all.Where(r => r.Surface == GoldPromptSurface.ShortPipelineOnly).ToArray();
        var production = all.Where(r => r.Surface == GoldPromptSurface.ProductionLongPlusShort).ToArray();

        return new SurfaceSplitScores(
            Aggregate(shortOnly), shortOnly.Length,
            Aggregate(production), production.Length,
            Aggregate(all), all.Count);
    }

    /// <summary>
    /// Human-readable name of a surface, used in report headings so an operator reading the output
    /// knows which prompt the numbers came from without consulting the source.
    /// </summary>
    internal static string Describe(GoldPromptSurface surface) => surface switch
    {
        GoldPromptSurface.ShortPipelineOnly => "short pipeline instruction ALONE (legacy harness surface)",
        GoldPromptSurface.ProductionLongPlusShort =>
            "PRODUCTION long+short ([CHARACTER_REGISTER] + ProofreadHe/En + short pipeline)",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "unknown prompt surface")
    };
}
