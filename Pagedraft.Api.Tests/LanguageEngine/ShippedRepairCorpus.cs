using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests.LanguageEngine;

// ---------------------------------------------------------------------------
// ShippedRepairCorpus — WHICH measured values the d5 / d6 quality-gate AGGREGATES and SHIP/HALT VERDICTS
// are computed over, DERIVED FROM THE SHIPPED `Ai:AnalysisRepair:PerType` MAP.
//
// WHY THIS EXISTS (the defect it removes). `be-c01` scoped the d5 and d6 verdicts to a "shipped corpus of
// record" and pushed the SYNOPSIS fixtures into a reported-but-not-gating subset, on the ground that
// `Synopsis` was not a repaired type. It encoded that ground as a LITERAL, twice and differently: d5 used a
// POSITIONAL split (`i < shippedSeedCount`) and d6 used a `BookKey` comparison. `f2` then ENABLED Synopsis
// (PerType `true` in BOTH appsettings files plus a plain-text arm in both deterministic dispatch switches)
// and both literals silently stranded the corpus: the instrument's ship/HALT decision no longer covered a
// type the product repairs, so a Synopsis regression on a later stochastic re-run could not move the verdict.
//
// THE FIX, and the durable half: the fixture still says WHICH SUBSET a value belongs to (that is fixture
// identity: a q1 `SynopsisLeakCase`, or a case routed to the SYNOPSIS book), but WHETHER a subset GATES is
// now read out of the SHIPPED CONFIG through `AnalysisRepairGate.Evaluate` - the same predicate the four
// production gate call sites consult, over the same `appsettings.json` the API ships. Enabling or rolling
// back a type therefore moves the gated corpus BY ITSELF, at both call sites at once.
//
// GRANULARITY, stated honestly. Attribution is per SUBSET, not per value, because that is what the fixture
// can honestly support: the `SynopsisLeakCases` / `SynopsisCases` values were authored for the `SynopsisHe`
// prompt shape and the synopsis book, so they ARE attributable to `AnalysisType.Synopsis`; the original
// `LeakCases` / `Cases` values are generic Hebrew literary-analysis prose, deliberately NOT authored per
// type, so attributing them to one enum member would be a fiction. They stand for the repaired types OTHER
// than Synopsis, and that list is itself read from the shipped map (see <see cref="AnalysisProseTypes"/>).
//
// PINNED: `ShippedRepairCorpusTests` (bottom of this file) asserts this helper agrees with the shipped
// `PerType` map, in BOTH directions, so a future enable/rollback that fails to move the corpus turns RED
// instead of silently stranding it again. Deterministic: config file read plus static fixture lists, NO
// model, NO GPU, NO DB.
// ---------------------------------------------------------------------------

/// <summary>
/// The gated-corpus scope of ONE measured fixture list: which indices form the ANALYSIS-PROSE subset, which
/// form the SYNOPSIS subset, and which of them the SHIPPED config puts inside the GATED corpus (the values
/// the aggregate and the SHIP/HALT verdict are computed over).
/// </summary>
internal sealed class RepairCorpusScope
{
    private readonly HashSet<int> _synopsis;
    private readonly HashSet<int> _gated;

    internal RepairCorpusScope(
        int count,
        Func<int, bool> isSynopsisValue,
        IReadOnlyList<string> repairedTypes,
        bool synopsisRepaired)
    {
        _synopsis = new HashSet<int>(Enumerable.Range(0, count).Where(isSynopsisValue));
        Synopsis = Enumerable.Range(0, count).Where(i => _synopsis.Contains(i)).ToList();
        AnalysisProse = Enumerable.Range(0, count).Where(i => !_synopsis.Contains(i)).ToList();

        RepairedTypes = repairedTypes;
        AnalysisProseTypes = repairedTypes.Where(t => t != nameof(AnalysisType.Synopsis)).ToList();
        SynopsisRepaired = synopsisRepaired;
        AnalysisProseRepaired = AnalysisProseTypes.Count > 0;

        var gated = new List<int>();
        if (AnalysisProseRepaired) gated.AddRange(AnalysisProse);
        if (synopsisRepaired) gated.AddRange(Synopsis);
        gated.Sort();
        Gated = gated;
        _gated = new HashSet<int>(gated);
    }

    /// <summary>The indices the AGGREGATE and the VERDICT cover, ascending.</summary>
    public IReadOnlyList<int> Gated { get; }

    /// <summary>The ANALYSIS-PROSE subset: the pre-f2 corpus of record (the be-c08 figures of record are
    /// quoted off this subset, so it stays separately readable off the report).</summary>
    public IReadOnlyList<int> AnalysisProse { get; }

    /// <summary>The q1 SYNOPSIS subset.</summary>
    public IReadOnlyList<int> Synopsis { get; }

    /// <summary>Every analysis type the SHIPPED PerType map repairs, in <see cref="AnalysisType"/>
    /// DECLARATION order (which is not alphabetical). Includes <c>BookReview</c>, which is repaired through
    /// its own engine hook rather than through a dispatch switch and therefore has no value in either
    /// fixture corpus - see <see cref="AnalysisProseTypes"/>.</summary>
    public IReadOnlyList<string> RepairedTypes { get; }

    /// <summary>The repaired types the ANALYSIS-PROSE subset stands for (every repaired type except
    /// <c>Synopsis</c>), read from the shipped map rather than counted by hand.</summary>
    public IReadOnlyList<string> AnalysisProseTypes { get; }

    /// <summary>Does the SHIPPED config repair <c>Synopsis</c>? (True since f2.)</summary>
    public bool SynopsisRepaired { get; }

    /// <summary>Does the SHIPPED config repair at least one non-Synopsis type?</summary>
    public bool AnalysisProseRepaired { get; }

    /// <summary>Is this index inside the GATED corpus (i.e. may it move the aggregate and the verdict)?</summary>
    public bool IsGated(int i) => _gated.Contains(i);

    /// <summary>Is this index in the SYNOPSIS subset (the fixture's own attribution)?</summary>
    public bool IsSynopsis(int i) => _synopsis.Contains(i);

    /// <summary>The subset label for a report row / list item.</summary>
    public string SubsetLabel(int i) => _synopsis.Contains(i) ? "SYNOPSIS" : "analysis-prose";

    /// <summary>
    /// One sentence naming the GATED CORPUS at the point of a verdict, so a reader of the artifact can tell
    /// what the SHIP/HALT covers without opening the source. States the derivation (the shipped PerType map),
    /// the repaired types by NAME, and the two labelled subsets with their counts.
    /// </summary>
    public string CorpusSentence =>
        $"GATED CORPUS = {Gated.Count} value(s), derived from the SHIPPED `Ai:AnalysisRepair:PerType` map in "
        + $"appsettings.json (NOT from a hardcoded split): that map repairs {RepairedTypes.Count} analysis "
        + $"type(s) - {string.Join(", ", RepairedTypes.Select(t => "`" + t + "`"))}. Two LABELLED SUBSETS of "
        + $"this corpus: {AnalysisProse.Count} ANALYSIS-PROSE value(s) "
        + (AnalysisProseRepaired ? "(GATED; " : "(NOT gated - no non-Synopsis type is repaired; ")
        + $"the pre-f2 corpus of record, so the be-c08 figures of record stay directly comparable off the "
        + $"subset section) + {Synopsis.Count} SYNOPSIS value(s) "
        + (SynopsisRepaired
            ? "(GATED since `f2` enabled `Synopsis` on 2026-07-28; before f2 they were measured but excluded "
              + "from the verdict, which is exactly the stranding this derivation removes)."
            : "(NOT gated: the shipped map does NOT repair `Synopsis`, so they are measured and reported but "
              + "cannot move the verdict).");
}

/// <summary>
/// Reads the SHIPPED <c>Ai:AnalysisRepair</c> block once and answers the ONE question both quality gates
/// need: which fixture subsets are inside the gated corpus. See the file header.
/// </summary>
internal static class ShippedRepairCorpus
{
    private static readonly Lazy<AnalysisRepairOptions> Shipped =
        new(() => ShippedAnalysisRepairConfig.Load(), isThreadSafe: true);

    /// <summary>The SHIPPED gate predicate, over the SHIPPED config: exactly what the four production gate
    /// call sites consult (<see cref="AnalysisRepairGate.Evaluate"/>), so "repaired" here cannot drift from
    /// "repaired" in the product.</summary>
    public static bool Repairs(string typeKey)
        => AnalysisRepairGate.Evaluate(Shipped.Value, typeKey) == AnalysisRepairGateReason.Allowed;

    /// <summary>Every <see cref="AnalysisType"/> the shipped map repairs, in enum-declaration order.</summary>
    public static IReadOnlyList<string> RepairedTypes()
        => Enum.GetNames<AnalysisType>().Where(Repairs).ToList();

    /// <summary>
    /// Scopes a measured fixture list. <paramref name="isSynopsisValue"/> is the FIXTURE's own attribution of
    /// a value to <c>AnalysisType.Synopsis</c> (a q1 synopsis leak case, or a case routed to the synopsis
    /// book) - identity, never position. Whether that subset GATES is decided here, from the shipped config.
    /// </summary>
    public static RepairCorpusScope ScopeOf(int count, Func<int, bool> isSynopsisValue)
        => new(count, isSynopsisValue, RepairedTypes(), Repairs(nameof(AnalysisType.Synopsis)));

    /// <summary>
    /// The d5 CLEANING corpus and its scope, as ONE definition consumed by BOTH the live harness
    /// (<c>OutputQualityDiagnostic.MeasureDynamicTermRepair_LocalVsCloud</c>) and the deterministic pin below.
    /// The shipped <c>LeakCases</c> followed by q1's <c>SynopsisLeakCases</c>, with SYNOPSIS membership
    /// attributed by fixture LABEL identity, never by position.
    /// <para>Living here rather than being re-derived at each call site is deliberate: a pin that rebuilt its
    /// own copy of the split would stay green while the harness's copy drifted, which is a weaker version of
    /// the very stranding this type exists to prevent.</para>
    /// </summary>
    public static (IReadOnlyList<LeakCase> Seeds, RepairCorpusScope Scope) D5CleaningCorpus()
    {
        var seeds = PreservationFixtureBooks.LeakCases
            .Concat(PreservationFixtureBooks.SynopsisLeakCases)
            .ToList();
        var synopsisLeakLabels = new HashSet<string>(
            PreservationFixtureBooks.SynopsisLeakCases.Select(l => l.Label), StringComparer.Ordinal);
        return (seeds, ScopeOf(seeds.Count, i => synopsisLeakLabels.Contains(seeds[i].Label)));
    }

    /// <summary>
    /// The d6 PRESERVATION corpus and its scope, same single-definition rule as
    /// <see cref="D5CleaningCorpus"/>. SYNOPSIS membership is the case's own <c>BookKey</c>.
    /// </summary>
    public static (IReadOnlyList<LegitCase> Cases, RepairCorpusScope Scope) D6PreservationCorpus()
    {
        var cases = PreservationFixtureBooks.Cases
            .Concat(PreservationFixtureBooks.SynopsisCases)
            .ToList();
        return (cases,
            ScopeOf(cases.Count, i => cases[i].BookKey == PreservationFixtureBooks.SynopsisBookKey));
    }
}

/// <summary>
/// The DETERMINISTIC pin on the derivation above (no model, no GPU, no DB): the gated corpus the d5 / d6
/// harnesses compute their verdicts over must agree with the SHIPPED <c>PerType</c> map, in both directions.
/// This is what makes the next enable (or rollback) turn something RED instead of silently stranding a
/// corpus the way <c>f2</c> did.
/// </summary>
public class ShippedRepairCorpusTests
{
    /// <summary>
    /// The SAME scope objects the two live harnesses compute their verdicts over - obtained by calling the
    /// shared builders, NOT by rebuilding an equivalent split here, so this pin binds the real call sites
    /// rather than a look-alike reconstruction of them.
    /// </summary>
    private static RepairCorpusScope D5Scope() => ShippedRepairCorpus.D5CleaningCorpus().Scope;

    private static RepairCorpusScope D6Scope() => ShippedRepairCorpus.D6PreservationCorpus().Scope;

    /// <summary>
    /// The SYNOPSIS subset is inside the gated corpus IFF the shipped map repairs <c>Synopsis</c>. Asserted
    /// against the raw <c>PerType</c> map (not against the helper's own opinion of it), for BOTH harnesses,
    /// so the two call sites can never diverge again the way the positional split and the BookKey split did.
    /// </summary>
    [Fact]
    public void GatedCorpus_TracksTheShippedPerTypeMap_ForSynopsis()
    {
        var perType = ShippedAnalysisRepairConfig.LoadPerType(ShippedAnalysisRepairConfig.BaseFile);
        Assert.True(perType is { Count: > 0 }, "appsettings.json has no Ai:AnalysisRepair:PerType map.");
        var synopsisEnabled = perType!.TryGetValue(nameof(AnalysisType.Synopsis), out var v) && v;

        foreach (var (name, scope) in new[] { ("d5", D5Scope()), ("d6", D6Scope()) })
        {
            Assert.True(scope.Synopsis.Count > 0, $"{name}: the SYNOPSIS fixture subset is empty; this test is vacuous.");
            Assert.True(scope.AnalysisProse.Count > 0, $"{name}: the ANALYSIS-PROSE fixture subset is empty; this test is vacuous.");

            Assert.True(scope.SynopsisRepaired == synopsisEnabled,
                $"{name}: the harness thinks Synopsis is {(scope.SynopsisRepaired ? "" : "NOT ")}repaired, but " +
                $"appsettings.json ships Ai:AnalysisRepair:PerType:Synopsis = {synopsisEnabled}.");

            foreach (var i in scope.Synopsis)
            {
                Assert.True(scope.IsGated(i) == synopsisEnabled,
                    $"{name}: SYNOPSIS value #{i} is {(scope.IsGated(i) ? "INSIDE" : "OUTSIDE")} the gated corpus " +
                    $"while the shipped PerType map says Synopsis repaired = {synopsisEnabled}. The d5/d6 " +
                    "ship/HALT verdict must cover exactly the types the product repairs: a type enabled in " +
                    "config but excluded from the corpus cannot move the verdict (that is the f2 stranding), " +
                    "and a type excluded in config but inside the corpus can HALT the shipped stage on a type " +
                    "the layer never touches.");
            }

            // The ANALYSIS-PROSE subset is gated whenever ANY non-Synopsis type is repaired, which is the
            // shipped state; if it ever is not, the whole d5/d6 gate is vacuous and must say so out loud.
            foreach (var i in scope.AnalysisProse)
            {
                Assert.True(scope.IsGated(i) == scope.AnalysisProseRepaired,
                    $"{name}: ANALYSIS-PROSE value #{i} gating disagrees with the shipped map " +
                    $"({scope.AnalysisProseTypes.Count} non-Synopsis repaired type(s)).");
            }

            Assert.Equal(
                (scope.AnalysisProseRepaired ? scope.AnalysisProse.Count : 0)
                    + (synopsisEnabled ? scope.Synopsis.Count : 0),
                scope.Gated.Count);
        }
    }

    /// <summary>
    /// The repaired-type list the report NAMES at each verdict is the shipped map itself, not a remembered
    /// count: every key mapped <c>true</c> is listed, every excluded type is not. The stale "eight analysis
    /// types" prose that survived f2 is exactly what this prevents.
    /// </summary>
    [Fact]
    public void RepairedTypes_AreExactlyTheShippedPerTypeTrueKeys()
    {
        var perType = ShippedAnalysisRepairConfig.LoadPerType(ShippedAnalysisRepairConfig.BaseFile);
        Assert.True(perType is { Count: > 0 }, "appsettings.json has no Ai:AnalysisRepair:PerType map.");

        var expected = Enum.GetNames<AnalysisType>()
            .Where(n => perType!.TryGetValue(n, out var v) && v)
            .ToList();
        var actual = ShippedRepairCorpus.RepairedTypes();

        Assert.Equal(expected, actual);
        Assert.Contains(nameof(AnalysisType.Summarization), actual); // non-vacuity: the map really is loaded
        Assert.DoesNotContain(nameof(AnalysisType.Proofread), actual); // and the gate really does exclude
    }
}
