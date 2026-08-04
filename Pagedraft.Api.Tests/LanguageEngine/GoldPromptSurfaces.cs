using System;
using System.Collections.Generic;
using System.Linq;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// Which of the THREE proofread prompt surfaces a standing case is measured on. The proofread corpus
/// spans all three, their numbers are not comparable to each other, and every aggregate reported
/// against the corpus has to say which surface it measured.
///
/// PUBLIC, unlike the rest of this file, for exactly one reason: <c>ChunkedAgreementFixture</c> is a
/// public record at the assembly root that carries this enum as a property, and C# forbids a public
/// member typed by an internal enum. c1 tagged its fixtures with a SEPARATE
/// <c>ChunkedAgreementSurface</c> enum precisely because this one had only two buckets and
/// <see cref="GoldPromptSurfaces.Split"/> would have dropped a third-surface record out of both
/// per-surface aggregates; c3 widened this enum to three and RETIRED that duplicate, so there is one
/// surface vocabulary for the whole corpus rather than two that can drift apart.
/// </summary>
public enum GoldPromptSurface
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
    /// the router appends after them. ONE model call over the whole input, which is NOT wrapped.
    /// </summary>
    ProductionLongPlusShort,

    /// <summary>
    /// The PER-CHUNK production surface (added by c3 when it promoted c1's chunked corpus): ONE model
    /// call per chunk, each instruction built by
    /// <c>PromptFactory.BuildProofreadChunkPrompt(language, characters, overlapPrefix)</c> - so it also
    /// carries a <c>[CONTEXT_BEFORE]</c> overlap for every chunk after the first - and each input
    /// wrapped in <c>[TEXT_TO_CORRECT]...[/TEXT_TO_CORRECT]</c>. Reached only when the input's word
    /// count EXCEEDS the language-keyed chunk target, so a case rides it as a consequence of its
    /// LENGTH rather than of anything the case declares.
    ///
    /// NOT comparable to <see cref="ProductionLongPlusShort"/> even though the instruction bodies
    /// coincide for a first chunk: the model sees a wrapped fragment of a document plus an overlap it
    /// is told not to correct, not a whole short passage. That is the entire regime difference the
    /// chunked corpus exists to measure.
    /// </summary>
    ChunkedPerChunk
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
/// The four aggregates one scoring pass yields: each of the THREE surfaces on its own, plus the MIXED
/// total over everything. <see cref="All"/> is deliberately kept, because it is what the harness
/// historically printed, but it blends non-comparable prompt surfaces and is therefore not a figure
/// any model comparison may be read off. Callers must label it as such.
/// </summary>
internal readonly record struct SurfaceSplitScores(
    ProofreadQualityTests.ModelScore ShortOnly, int ShortOnlyCases,
    ProofreadQualityTests.ModelScore Production, int ProductionCases,
    ProofreadQualityTests.ModelScore Chunked, int ChunkedCases,
    ProofreadQualityTests.ModelScore All, int AllCases)
{
    /// <summary>How many of the three surfaces contributed at least one scored case.</summary>
    public int PopulatedSurfaces =>
        (ShortOnlyCases > 0 ? 1 : 0) + (ProductionCases > 0 ? 1 : 0) + (ChunkedCases > 0 ? 1 : 0);

    /// <summary>
    /// True when the scored set rides ONE surface only (an id-subsetted bake-off, or the English gold,
    /// where no case carries a character register). In that case <see cref="All"/> is identical to the
    /// populated subset and printing it as a separate "mixed" block would be noise, not information.
    ///
    /// An EMPTY record set also reports true (no surface is populated), which is vacuously right but
    /// is NOT a shape the two consumers can reach: both return early when the gold loads or subsets to
    /// zero cases, and the scorer emits exactly one record per case including the error path, so an
    /// empty record set implies an empty case set. A caller that does not have that guarantee must
    /// check the case count itself rather than read this flag as "one surface is populated".
    /// </summary>
    public bool IsSingleSurface => PopulatedSurfaces <= 1;

    /// <summary>The aggregate and case count for one surface, so a report can loop rather than branch.</summary>
    public (ProofreadQualityTests.ModelScore Score, int Cases) On(GoldPromptSurface surface) => surface switch
    {
        GoldPromptSurface.ShortPipelineOnly => (ShortOnly, ShortOnlyCases),
        GoldPromptSurface.ProductionLongPlusShort => (Production, ProductionCases),
        GoldPromptSurface.ChunkedPerChunk => (Chunked, ChunkedCases),
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "unknown prompt surface")
    };
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

    /// <summary>
    /// The prompt surface a CHUNKED-AGREEMENT fixture is sent on, DERIVED from the routing rule rather
    /// than read off the fixture's own <c>Surface</c> tag.
    ///
    /// <c>UnifiedAnalysisService.RunAsync</c> branches on
    /// <c>WordCount(context.TargetText) &gt; ProofreadChunkTargetWordsFor(opts, language)</c>
    /// (UnifiedAnalysisService.cs:401): above the language-keyed target the input is chunked and every
    /// chunk rides <see cref="GoldPromptSurface.ChunkedPerChunk"/>; at or below it the whole input goes
    /// out in ONE call on <see cref="GoldPromptSurface.ProductionLongPlusShort"/>. Nothing the fixture
    /// DECLARES enters into it - which is exactly why the declared tag has to be checked against this,
    /// the same way <c>ProofreadAgreementGoldTests</c> checks the gold partition against the real
    /// <c>BuildGoldRequest</c>. A fixture that grows or shrinks past the threshold changes surface
    /// silently otherwise, and its floor stops meaning what it says.
    /// </summary>
    internal static GoldPromptSurface DerivedSurfaceOf(ChunkedAgreementFixture fixture) =>
        ChunkedAgreementHarness.WordCount(ChunkedAgreementHarness.ProductionTargetText(fixture)) >
        ChunkedAgreementHarness.ChunkTargetWordsFor(fixture)
            ? GoldPromptSurface.ChunkedPerChunk
            : GoldPromptSurface.ProductionLongPlusShort;

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
    /// Every surface, in report order. Derived from the enum itself, so a fourth surface added later
    /// reaches every caller that loops over this instead of hand-listing the buckets.
    ///
    /// IMMUTABLE, AND THAT IS LOAD-BEARING. This briefly carried a setter so one test could widen the
    /// list to simulate a "declared but unbucketed" surface. xUnit runs test CLASSES in parallel (this
    /// assembly disables it for exactly one named collection and nowhere else), and
    /// <c>ProofreadStandingFloorTests</c> and <c>ChunkedAgreementFixtureTests</c> both READ this while
    /// that window was open - MEASURED: widening the window made
    /// <c>TheStandingCorpus_SpansAllThreeSurfaces_*</c> throw from <see cref="Describe"/> and
    /// <c>EveryMetricFloor_StatesItsSurfaceAndSubset_*</c> fail on "NO standing metric bar sits on 999".
    /// A <c>finally</c> does not close that; the scenario is exercised through
    /// <see cref="OrphanLabels"/> instead, which takes its bucket set as a parameter and needs no
    /// mutable global at all.
    /// </summary>
    internal static IReadOnlyList<GoldPromptSurface> AllSurfaces { get; } =
        Enum.GetValues<GoldPromptSurface>();

    /// <summary>
    /// The surfaces <see cref="Split"/> actually buckets - kept as an EXPLICIT set rather than derived
    /// from <see cref="AllSurfaces"/>, on purpose. <c>AllSurfaces</c> is every DECLARED enum member;
    /// this is every member with a bucket below. The two are meant to drift apart for exactly one
    /// version: the day a fourth surface is added to <c>GoldPromptSurface</c> without a matching
    /// bucket here. If this set were <c>AllSurfaces</c> instead, the orphan-detection below would
    /// never name that new member - it IS declared, so it would pass a <c>!AllSurfaces.Contains(...)</c>
    /// filter, and the throw that fires from the count mismatch would report an empty offender list.
    /// Do not "simplify" this back to <c>AllSurfaces</c>; that is the bug this set exists to fix.
    /// </summary>
    private static readonly HashSet<GoldPromptSurface> BucketedSurfaces = new()
    {
        GoldPromptSurface.ShortPipelineOnly,
        GoldPromptSurface.ProductionLongPlusShort,
        GoldPromptSurface.ChunkedPerChunk
    };

    /// <summary>
    /// The offenders <see cref="Split"/> names when its bucket count and its record count disagree:
    /// every record whose surface is in NONE of <paramref name="bucketed"/>, labelled
    /// <c>{id} [{numeric surface}]</c> and de-duplicated.
    ///
    /// TAKES THE BUCKET SET AS A PARAMETER so the derivation can be exercised against a set that
    /// deliberately omits a REAL declared surface - the "a fourth member was added to the enum but not
    /// to Split" scenario, which is the one the old <c>!AllSurfaces.Contains(...)</c> filter got wrong
    /// (a declared member passes that filter, so the throw fired with an EMPTY offender list). Testing
    /// it by mutating <see cref="AllSurfaces"/> would need a shared mutable global and races with the
    /// other classes that read it; see that property's remarks.
    /// </summary>
    internal static string[] OrphanLabels(
        IEnumerable<GoldCaseScore> records, IReadOnlySet<GoldPromptSurface> bucketed) =>
        records
            .Where(r => !bucketed.Contains(r.Surface))
            .Select(r => $"{r.Id} [{(int)r.Surface}]")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Partition ONE scored pass into its per-surface aggregates plus the mixed total. No second model
    /// pass: the records were produced by a single run and are only grouped here.
    ///
    /// THROWS rather than silently dropping a record whose surface matches no bucket. That is the whole
    /// hazard this split exists to prevent, and c1 named it explicitly when it declined to fold the
    /// chunked corpus into a TWO-bucket split: a record that falls out of every per-surface aggregate
    /// still lands in <see cref="SurfaceSplitScores.All"/>, so the mixed block silently grows while no
    /// per-surface block moves - a corpus change that looks like a model change. Making it an exception
    /// (not just a test) means no caller can reach that state at all.
    /// </summary>
    internal static SurfaceSplitScores Split(IEnumerable<GoldCaseScore> records)
    {
        var all = records as IReadOnlyList<GoldCaseScore> ?? records.ToArray();
        var shortOnly = all.Where(r => r.Surface == GoldPromptSurface.ShortPipelineOnly).ToArray();
        var production = all.Where(r => r.Surface == GoldPromptSurface.ProductionLongPlusShort).ToArray();
        var chunked = all.Where(r => r.Surface == GoldPromptSurface.ChunkedPerChunk).ToArray();

        var bucketed = shortOnly.Length + production.Length + chunked.Length;
        if (bucketed != all.Count)
        {
            var orphans = OrphanLabels(all, BucketedSurfaces);
            throw new InvalidOperationException(
                $"{all.Count - bucketed} scored record(s) belong to NO per-surface bucket, so they would " +
                "be counted only in the mixed ALL aggregate - the silent surface-mixing this split exists " +
                "to prevent. Add the surface to GoldPromptSurface AND to Split's buckets, or stop tagging " +
                "records with it. Offenders: " + string.Join(", ", orphans));
        }

        return new SurfaceSplitScores(
            Aggregate(shortOnly), shortOnly.Length,
            Aggregate(production), production.Length,
            Aggregate(chunked), chunked.Length,
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
        GoldPromptSurface.ChunkedPerChunk =>
            "PRODUCTION per-chunk ([CHARACTER_REGISTER] + [CONTEXT_BEFORE] overlap + ProofreadHe/En, " +
            "one call per chunk, input wrapped in [TEXT_TO_CORRECT])",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "unknown prompt surface")
    };
}
