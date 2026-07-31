using System;
using System.Linq;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and the whole point of this file's location is to be outside it.
using RepairCorpusScope = Pagedraft.Api.Tests.LanguageEngine.RepairCorpusScope;
using ShippedRepairCorpus = Pagedraft.Api.Tests.LanguageEngine.ShippedRepairCorpus;

namespace Pagedraft.Api.Tests;

// LIVES AT THE TEST PROJECT ROOT ON PURPOSE, split out of LanguageEngine/ShippedRepairCorpus.cs, which
// keeps the RepairCorpusScope / ShippedRepairCorpus helpers next to their live consumer
// (OutputQualityDiagnostic). The Pagedraft.Api.Tests.LanguageEngine namespace is the one the standing
// deterministic filter EXCLUDES (FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine), because that
// folder holds the live-GPU harnesses. While this class sat in that namespace its 2 tests ran in NEITHER
// standing filter, so the anti-stranding pin below could not fire - the same defect it exists to prevent,
// one level up. Do not move it back; see LiveHarnessNamespaceGuardTests.

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
