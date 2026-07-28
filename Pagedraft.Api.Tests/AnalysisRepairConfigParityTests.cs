using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Guards the documented parity contract between <c>Pagedraft.Api/appsettings.json</c> and
/// <c>Pagedraft.Api/appsettings.Production.json</c> for <c>Ai:AnalysisRepair:PerType</c>
/// (docs/ANALYSIS_OUTPUT_REPAIR.md §4.1; the Production block comment says this block "MIRRORS base
/// appsettings.json"). Nothing at runtime enforces that mirror - Production.json fully overrides
/// (not merges with) the base file's Ai:AnalysisRepair block, so an edit to one PerType map that
/// forgets the other silently drifts the per-analysis-type repair gate between environments. This
/// test loads both files independently (mirroring the FindUpward + AddJsonFile pattern used by
/// LanguageEngine/AnalysisRepairSmokeTests.cs) and asserts the two PerType maps are equal - same
/// keys, same bool values.
/// </summary>
public class AnalysisRepairConfigParityTests
{
    [Fact]
    public void PerType_BaseAndProduction_AreEqual()
    {
        var basePath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        var prodPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.Production.json"));

        var basePerType = LoadPerType(basePath);
        var prodPerType = LoadPerType(prodPath);

        Assert.True(basePerType is { Count: > 0 },
            $"appsettings.json Ai:AnalysisRepair:PerType was null or empty ({basePath}).");
        Assert.True(prodPerType is { Count: > 0 },
            $"appsettings.Production.json Ai:AnalysisRepair:PerType was null or empty ({prodPath}).");

        var baseKeys = basePerType!.Keys.ToHashSet(StringComparer.Ordinal);
        var prodKeys = prodPerType!.Keys.ToHashSet(StringComparer.Ordinal);

        var onlyInBase = baseKeys.Except(prodKeys).ToList();
        var onlyInProd = prodKeys.Except(baseKeys).ToList();
        Assert.True(onlyInBase.Count == 0 && onlyInProd.Count == 0,
            "Ai:AnalysisRepair:PerType key sets differ between appsettings.json and appsettings.Production.json. " +
            $"Only in appsettings.json: [{string.Join(", ", onlyInBase)}]. " +
            $"Only in appsettings.Production.json: [{string.Join(", ", onlyInProd)}].");

        var mismatched = baseKeys
            .Where(key => basePerType![key] != prodPerType![key])
            .Select(key => $"{key}: base={basePerType![key]} prod={prodPerType![key]}")
            .ToList();
        Assert.True(mismatched.Count == 0,
            "Ai:AnalysisRepair:PerType values differ between appsettings.json and appsettings.Production.json " +
            "for the following key(s), breaking the documented mirror (docs/ANALYSIS_OUTPUT_REPAIR.md §4.1): " +
            string.Join("; ", mismatched));
    }

    // ---------------------------------------------------------------------------------------------
    // h2-enum-coverage-guard-test - the ENUM-COMPLETENESS ORACLE.
    //
    // PerType_BaseAndProduction_AreEqual above only compares the two config FILES to each other, so both
    // could omit the same AnalysisType forever and stay green - which is exactly what happened to
    // Synopsis and Custom (analysis-repair-pertype-coverage-holes plan, i1's 12-row table). This closes
    // that class of hole: it walks EVERY AnalysisType and requires an explicit in-test decision for it,
    // so a NEWLY ADDED enum member with no decision fails loudly instead of silently inheriting "off".
    //
    // Per-type PIN assertions for the three DeliberatelyExcluded types already live in
    // AnalysisRepairExclusionRegressionTests.cs (the shipped allowlist, both dispatch switches, and
    // Custom's/Synopsis's real producer seams) - this file does not restate those; it is the different
    // guard that a newly added enum value cannot slip through undecided. See that file's class doc for
    // the per-type assertions, and the plan's `## i1`/`## d1 decision`/`## q1 quality-gate results`
    // sections for the evidence behind every verdict below.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The verdict for one <see cref="AnalysisType"/> in the hand-authored coverage table.</summary>
    private readonly record struct RepairCoverageEntry(bool Repaired, string? ExclusionReason);

    private static RepairCoverageEntry Repaired() => new(true, null);
    private static RepairCoverageEntry Excluded(string reason) => new(false, reason);

    /// <summary>
    /// The hand-authored decision table, one entry per <see cref="AnalysisType"/> member. THIS is the
    /// oracle - deliberately NOT derived from the shipped PerType map (that would be a tautology that
    /// passes for any map the code happens to ship). Sourced from the
    /// analysis-repair-pertype-coverage-holes plan's i1 investigation, d1 decision and q1 quality-gate
    /// results (2026-07-28).
    ///
    /// Mirrors the <see cref="ExpectedStagesFor"/> oracle pattern below: a switch expression over every
    /// defined enum member, with a <c>_ =&gt;</c> arm that THROWS naming the missing member and what to do,
    /// rather than silently defaulting, so adding a case to <see cref="AnalysisType"/> without adding a
    /// case here fails the test instead of passing vacuously.
    /// </summary>
    private static RepairCoverageEntry DecisionFor(AnalysisType type) => type switch
    {
        AnalysisType.Proofread => Excluded(
            "Excluded BY DESIGN, documented (docs/ANALYSIS_OUTPUT_REPAIR.md section 4): its output quotes " +
            "verbatim manuscript spans (original/suggested/span), and repairing them would corrupt the " +
            "suggestion diff."),

        AnalysisType.LineEdit => Repaired(),
        AnalysisType.LinguisticAnalysis => Repaired(),
        AnalysisType.LiteraryAnalysis => Repaired(),

        // BookOverview: this Repaired() verdict is CORRECT, not an oversight - the key IS
        // present-and-true in both config files and IS a dispatch arm in both switches (see
        // DispatchCoverageFor below), which is what this verdict asserts. It is however a NO-OP on
        // its only real producer path: BuildBookProfileAsync -> RunRawAsync(structuredJson: null)
        // blank-guards both repair stages to a no-op, and its one repairable field (Summary) is
        // discarded before persistence anyway - so "Repaired" is dead config on the profile path,
        // not wrong (an unadvertised direct POST /analyze with analysisType=BookOverview DOES get
        // it repaired). docs/ANALYSIS_OUTPUT_REPAIR.md section 4.1's BookOverview row documents this
        // exact asymmetry. Whether to KEEP or REMOVE the key given the dead profile path is the
        // adjudication owned by the child plan
        // analysis-repair-coverage-followups-2026-07-28.plan.md's `f1` todo - do not reclassify this
        // Excluded here; that would require the key to be absent-or-false, which would silently
        // disable the still-reachable direct-API path and would pre-empt f1's decision.
        AnalysisType.BookOverview => Repaired(),

        AnalysisType.Synopsis => Excluded(
            "DeliberatelyExcluded - MEASURED HALT (q1, 2026-07-28): preservation 83% (5/6) on the shipped " +
            "LOCAL tier (Ollama | gemma4:12b) against a bar of >= 90% AND over-rewrite exactly 0. Over-rewrite " +
            "was 0 and cleaning passed 100% (3/3), but the bar is a conjunction and the precision half " +
            "failed: the repair model TRANSLITERATED a legitimate proper noun (\"Chekhov\" -> \"צ'כוב\") " +
            "at a paragraph head, reproduced identically on cloud gemma-4-31b-it, so it is structural, not a " +
            "small-model artifact. See the plan's `## q1 quality-gate results`."),

        AnalysisType.CharacterAnalysis => Repaired(),
        AnalysisType.StoryAnalysis => Repaired(),
        AnalysisType.BookReview => Repaired(),
        AnalysisType.Summarization => Repaired(),
        AnalysisType.QA => Repaired(),

        AnalysisType.Custom => Excluded(
            "DeliberatelyExcluded (d1, 2026-07-28): its instruction is user-authored (req.CustomPrompt), so " +
            "its output is legitimately English / bilingual / quoted / tabular - which falsifies the repair " +
            "layer's \"foreign script = model leakage\" premise - and the layer makes ONE sequential model " +
            "call per foreign WORD with no cap, so a legitimately-English answer would be both silently " +
            "mistranslated and a several-hundred-call GPU wedge on a single-GPU host. See the plan's " +
            "`## d1 decision`."),

        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            $"AnalysisType.{type} was added with NO repair-coverage decision. Add a case to DecisionFor in " +
            "AnalysisRepairConfigParityTests.cs classifying it Repaired (must then be present AND true in " +
            "Ai:AnalysisRepair:PerType in BOTH appsettings.json and appsettings.Production.json - and wire a " +
            "dispatch arm in GlossaryRepairPass.Apply and DynamicTermRepairService.ApplyAsync, or it is dead " +
            "config) or DeliberatelyExcluded (must stay absent, or present-and-false, WITH a one-line reason " +
            "string). See docs/ANALYSIS_OUTPUT_REPAIR.md section 4 and the analysis-repair-pertype-coverage-" +
            "holes plan.")
    };

    /// <summary>
    /// THE ORACLE. Every <see cref="AnalysisType"/> member must resolve through <see cref="DecisionFor"/> -
    /// a new member with no case throws there, failing this test with a message that says what to do
    /// (proved by the h2 revert-verify drill: a temporary dummy enum member turns this test red with that
    /// exact message, then is fully reverted).
    ///
    /// A Repaired verdict must be present-and-true in BOTH shipped config files. A DeliberatelyExcluded
    /// verdict must be absent OR present-and-false in BOTH - that tolerance is load-bearing: todo h3 is
    /// expected to add an explicit "Custom": false (and possibly "Synopsis": false) key to both files as
    /// the visible-at-the-config-surface form of the exclusion, and this assertion must accept that shape
    /// without going red.
    /// </summary>
    [Fact]
    public void EveryAnalysisType_HasAnExplicitRepairCoverageDecision()
    {
        var basePath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        var prodPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.Production.json"));
        var basePerType = LoadPerType(basePath) ?? new Dictionary<string, bool>();
        var prodPerType = LoadPerType(prodPath) ?? new Dictionary<string, bool>();

        foreach (var type in Enum.GetValues<AnalysisType>())
        {
            var entry = DecisionFor(type); // throws for an undecided new member
            var typeName = type.ToString();

            if (entry.Repaired)
            {
                var enabledInBase = basePerType.TryGetValue(typeName, out var baseVal) && baseVal;
                var enabledInProd = prodPerType.TryGetValue(typeName, out var prodVal) && prodVal;

                Assert.True(enabledInBase,
                    $"{typeName} is classified Repaired in AnalysisRepairConfigParityTests.DecisionFor, but " +
                    "appsettings.json's Ai:AnalysisRepair:PerType does not have it present-and-true. Either " +
                    "wire it (PerType key in both files + a dispatch arm in GlossaryRepairPass.Apply and " +
                    "DynamicTermRepairService.ApplyAsync) or reclassify it DeliberatelyExcluded here with a " +
                    "reason.");
                Assert.True(enabledInProd,
                    $"{typeName} is classified Repaired in AnalysisRepairConfigParityTests.DecisionFor, but " +
                    "appsettings.Production.json's Ai:AnalysisRepair:PerType does not have it present-and-true " +
                    "(Production fully OVERRIDES the base block, so it does not inherit the base file's key). " +
                    "Either wire it there too or reclassify it DeliberatelyExcluded here.");
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.ExclusionReason),
                    $"{typeName} is classified DeliberatelyExcluded in DecisionFor but carries no reason " +
                    "string. Every exclusion needs a one-line reason.");

                var enabledInBase = basePerType.TryGetValue(typeName, out var baseVal) && baseVal;
                var enabledInProd = prodPerType.TryGetValue(typeName, out var prodVal) && prodVal;

                Assert.False(enabledInBase,
                    $"appsettings.json now ships Ai:AnalysisRepair:PerType:{typeName} = true, but this test's " +
                    $"decision table classifies it DeliberatelyExcluded. {entry.ExclusionReason}");
                Assert.False(enabledInProd,
                    $"appsettings.Production.json now ships Ai:AnalysisRepair:PerType:{typeName} = true, but " +
                    $"this test's decision table classifies it DeliberatelyExcluded. {entry.ExclusionReason}");
            }
        }
    }

    /// <summary>
    /// The SECOND half of this defect class: an allowlist key can drift apart from the two per-type
    /// dispatch switches (<c>GlossaryRepairPass.Apply</c>, <c>DynamicTermRepairService.ApplyAsync</c>)
    /// that actually do the repairing - this shipped once already for <c>BookOverview</c> on its
    /// profile-build path (i1's investigation; there it stayed a "dead key" rather than a hole only
    /// because BookOverview's one repairable field is discarded before persistence on that path).
    /// Reflection over a switch expression's case labels is not practical, so this is a SECOND
    /// hand-maintained table, mirroring the same fail-loudly idiom as <see cref="DecisionFor"/>: every
    /// type classified Repaired must resolve through it, and a Repaired type with no entry throws rather
    /// than passing silently.
    ///
    /// Read directly against both switches on 2026-07-28: they carry the identical eight arms
    /// (Summarization, LiteraryAnalysis, LinguisticAnalysis, LineEdit, BookOverview, CharacterAnalysis,
    /// StoryAnalysis, QA). <c>BookReview</c> is the one Repaired type that is NOT an arm in either switch
    /// - both switches' own `_ =&gt;` comments say so ("BookReview is handled on its own path, never
    /// here") - it is repaired instead through BookReviewService's own glossary + dynamic ENGINE HOOKS
    /// (BookReviewService.cs:1139 / :1199), so it is recorded here as <see cref="DispatchCoverage.OwnEngineHook"/>
    /// rather than asserted as a dispatch-switch arm.
    /// </summary>
    private enum DispatchCoverage { BothSwitches, OwnEngineHook }

    private static DispatchCoverage DispatchCoverageFor(AnalysisType type) => type switch
    {
        AnalysisType.LineEdit => DispatchCoverage.BothSwitches,
        AnalysisType.LinguisticAnalysis => DispatchCoverage.BothSwitches,
        AnalysisType.LiteraryAnalysis => DispatchCoverage.BothSwitches,

        // BookOverview: BothSwitches is CORRECT - it is a genuine dispatch arm in both
        // GlossaryRepairPass.Apply and DynamicTermRepairService.ApplyAsync, matching the config key
        // being present-and-true in both PerType files. That arm is simply never reached on the
        // profile-build path (BuildBookProfileAsync -> RunRawAsync(structuredJson: null) blank-guards
        // both stages to a no-op before Summary, its only repairable field, is discarded pre-
        // persistence), which is why it is a NO-OP there rather than a hole - see
        // docs/ANALYSIS_OUTPUT_REPAIR.md section 4.1's BookOverview row for the documented asymmetry
        // (the key IS live on a direct POST /analyze with analysisType=BookOverview). KEEP-vs-REMOVE
        // of the key is the child plan analysis-repair-coverage-followups-2026-07-28.plan.md's `f1`
        // todo's call, not this test's - do not reclassify this arm as OwnEngineHook or drop it to
        // force that decision here.
        AnalysisType.BookOverview => DispatchCoverage.BothSwitches,
        AnalysisType.CharacterAnalysis => DispatchCoverage.BothSwitches,
        AnalysisType.StoryAnalysis => DispatchCoverage.BothSwitches,
        AnalysisType.Summarization => DispatchCoverage.BothSwitches,
        AnalysisType.QA => DispatchCoverage.BothSwitches,
        AnalysisType.BookReview => DispatchCoverage.OwnEngineHook,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            $"AnalysisType.{type} is classified Repaired in DecisionFor but has no entry in " +
            "DispatchCoverageFor. Decide whether it is a dispatch arm in BOTH GlossaryRepairPass.Apply and " +
            "DynamicTermRepairService.ApplyAsync (BothSwitches), or reaches repair through its own engine " +
            "hook like BookReview (OwnEngineHook), and record which - an allowlist key whose dispatch arm " +
            "was never added is dead config (docs/ANALYSIS_OUTPUT_REPAIR.md section 4).")
    };

    /// <summary>
    /// Forces the same explicit decision as <see cref="EveryAnalysisType_HasAnExplicitRepairCoverageDecision"/>,
    /// one layer down: every Repaired type must ALSO have a stated dispatch-coverage answer, so a type
    /// that gets a PerType key but never gets a matching dispatch arm cannot slip through this file
    /// undecided - the allowlist and the dispatch switches drifting apart is the second half of this
    /// defect class. This does not re-invoke the switches (GlossaryRepairPassTests.cs and
    /// AnalysisRepairExclusionRegressionTests.cs already drive them per-type with fixtures); it only
    /// forces the decision to be recorded and kept in sync by hand, which is strictly better than the
    /// silence that let BookOverview ship as a dead key.
    /// </summary>
    [Fact]
    public void EveryRepairedType_HasAnExplicitDispatchCoverageDecision()
    {
        foreach (var type in Enum.GetValues<AnalysisType>())
        {
            if (!DecisionFor(type).Repaired) continue;
            _ = DispatchCoverageFor(type); // throws for a Repaired type with no dispatch-coverage decision
        }
    }

    // Ai:AnalysisRepair:Mode is the other value that must mirror across the two files (both carry
    // GlossaryThenDynamic after the dynamic-term-repair precision follow-up shipped the dynamic stage on
    // the LOCAL tier; docs §13, §15). Like PerType, Production.json fully overrides (not merges with) the
    // base Ai:AnalysisRepair block, so a Mode flip in one file that forgets the other silently drifts the
    // repair-stage selection between environments - this guards that they stay identical.
    [Fact]
    public void Mode_BaseAndProduction_AreEqual()
    {
        var basePath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        var prodPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.Production.json"));

        var baseMode = LoadMode(basePath);
        var prodMode = LoadMode(prodPath);

        Assert.False(string.IsNullOrWhiteSpace(baseMode),
            $"appsettings.json Ai:AnalysisRepair:Mode was null or empty ({basePath}).");
        Assert.False(string.IsNullOrWhiteSpace(prodMode),
            $"appsettings.Production.json Ai:AnalysisRepair:Mode was null or empty ({prodPath}).");

        Assert.True(string.Equals(baseMode, prodMode, StringComparison.Ordinal),
            "Ai:AnalysisRepair:Mode differs between appsettings.json and appsettings.Production.json " +
            $"(base={baseMode}, prod={prodMode}), breaking the documented mirror (docs/ANALYSIS_OUTPUT_REPAIR.md §13/§15).");
    }

    /// <summary>
    /// be-f05. Pins the null-check-comes-FIRST ordering at its source, mechanically rather than only in
    /// prose: <see cref="AnalysisRepairGate.Evaluate"/>'s xmldoc now states that an <see
    /// cref="AnalysisRepairGateReason.Allowed"/> return implies <c>cfg is not null</c>, a guarantee
    /// <see cref="Analysis.UnifiedAnalysisService.ApplyAnalysisRepairAsync"/> relies on to dereference
    /// <c>cfg!</c> without its own null check. This test binds that guarantee to the actual method so a
    /// future edit to <c>Evaluate</c> (reordering checks, or adding a fifth reason ahead of the null check)
    /// fails here instead of only risking an NRE at the distant call site.
    /// </summary>
    [Fact]
    public void Evaluate_NullConfig_ReturnsNullConfigReason()
    {
        Assert.Equal(AnalysisRepairGateReason.NullConfig, AnalysisRepairGate.Evaluate(null, "Summarization"));
    }

    /// <summary>
    /// be-c03. <c>Ai:AnalysisRepair:SummaryBatchWindowChapters</c> is the third value that must mirror across
    /// the two files. It bounds how many chapters <c>SummarizeChaptersCoreAsync</c> summarizes and repairs
    /// before it COMMITS, so a divergence is not cosmetic: a prod value silently larger than the base one
    /// re-widens the all-or-nothing persist window that this key exists to bound, and on the real 80-chapter
    /// corpus that is the difference between losing 10 chapters' work to an abort and losing all 80. Same
    /// override hazard as PerType and Mode - Production.json fully replaces the base block.
    /// </summary>
    [Fact]
    public void SummaryBatchWindowChapters_BaseAndProduction_AreEqual()
    {
        var basePath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        var prodPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.Production.json"));

        var baseWindow = LoadSummaryBatchWindow(basePath);
        var prodWindow = LoadSummaryBatchWindow(prodPath);

        Assert.True(baseWindow.HasValue,
            $"appsettings.json has no Ai:AnalysisRepair:SummaryBatchWindowChapters value ({basePath}).");
        Assert.True(prodWindow.HasValue,
            $"appsettings.Production.json has no Ai:AnalysisRepair:SummaryBatchWindowChapters value ({prodPath}). " +
            "Production fully OVERRIDES the base Ai:AnalysisRepair block, so an absent key here does not " +
            "inherit the base value - it falls back to the AnalysisRepairOptions class default.");

        Assert.True(baseWindow == prodWindow,
            "Ai:AnalysisRepair:SummaryBatchWindowChapters differs between appsettings.json and " +
            $"appsettings.Production.json (base={baseWindow}, prod={prodWindow}), so the chapter-summary " +
            "checkpoint window - the bound on how much work one abort can discard - is not the same in the " +
            "two environments.");

        // The shipped value must be POSITIVE. A non-positive value is clamped to the default at runtime
        // (BookIntelligenceService.ResolveSummaryBatchWindow), so a 0 or -1 here would not restore the
        // all-or-nothing persist, but it WOULD mean the file no longer says what actually runs.
        Assert.True(baseWindow!.Value > 0,
            $"appsettings.json ships Ai:AnalysisRepair:SummaryBatchWindowChapters={baseWindow}, which is " +
            "clamped to the class default at runtime - the config no longer states the window in effect.");
    }

    /// <summary>
    /// The companion to the parity check: the shipped value must actually REACH
    /// <see cref="AnalysisRepairOptions.SummaryBatchWindowChapters"/>. Both files currently ship the same
    /// number as the class default, so an equality check against the default would prove nothing - this
    /// compares the bound value to the RAW string in the JSON, which catches a misspelled or moved key.
    /// </summary>
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public void ShippedSummaryBatchWindow_BindsIntoAiOptions(string fileName)
    {
        var path = FindUpward(Path.Combine("Pagedraft.Api", fileName));
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();

        var ai = config.GetSection("Ai").Get<AiOptions>();
        Assert.NotNull(ai);
        Assert.NotNull(ai!.AnalysisRepair);

        var raw = config.GetSection("Ai:AnalysisRepair:SummaryBatchWindowChapters").Value;
        Assert.False(string.IsNullOrWhiteSpace(raw),
            $"{fileName} has no Ai:AnalysisRepair:SummaryBatchWindowChapters value ({path}).");
        Assert.True(int.TryParse(raw, out var expected),
            $"{fileName} ships a non-integer Ai:AnalysisRepair:SummaryBatchWindowChapters=\"{raw}\".");
        Assert.True(expected == ai.AnalysisRepair!.SummaryBatchWindowChapters,
            $"{fileName} ships Ai:AnalysisRepair:SummaryBatchWindowChapters=\"{raw}\" but it bound to " +
            $"{ai.AnalysisRepair.SummaryBatchWindowChapters} - the config value is not reaching " +
            "AiOptions.AnalysisRepair.SummaryBatchWindowChapters.");
    }

    // ---------------------------------------------------------------------------------------------
    // be-c06 - the SHIPPED Mode vs the CODE PATH.
    //
    // AiOptions.cs keeps the CLASS default at AnalysisRepairMode.Glossary (a deliberate "safe posture" for
    // programmatic/test construction) while the appsettings files ship their own value. The consequence is
    // that every unit test and every `new AnalysisRepairOptions()` silently exercises glossary-only, so the
    // mode that ACTUALLY SHIPS had no deterministic test asserting which repair stages it selects.
    // Mode_BaseAndProduction_AreEqual above compares the two JSON FILES to each other; these tests compare
    // the FILE to the CODE PATH - they bind the real appsettings into AiOptions and assert the bound Mode
    // drives the stage selection the repair layer would actually take.
    //
    // The expectation is DERIVED FROM the bound value, never hard-coded: be-f01 moved this value once and
    // be-c09 may move it again, so a test that pins "Mode is X" would break spuriously or, worse, silently
    // pin the wrong thing. The contract under test is "WHATEVER Mode ships, the stage predicates agree with
    // it" - not "Mode is X".
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The canonical stage table, restated here INDEPENDENTLY of the production predicate pair
    /// (<see cref="AnalysisRepairModeExtensions"/>) so the test is a real oracle rather than a tautology.
    /// Throws on an unknown mode, so ADDING A FIFTH <see cref="AnalysisRepairMode"/> without deciding which
    /// stages it selects fails <see cref="EveryDefinedMode_IsCoveredByTheStageTable"/> instead of silently
    /// defaulting somewhere.
    /// </summary>
    private static (bool Glossary, bool Dynamic) ExpectedStagesFor(AnalysisRepairMode mode) => mode switch
    {
        AnalysisRepairMode.Off => (false, false),
        AnalysisRepairMode.Glossary => (true, false),
        AnalysisRepairMode.Dynamic => (false, true),
        AnalysisRepairMode.GlossaryThenDynamic => (true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode,
            "A new AnalysisRepairMode was added without deciding which repair stages it selects. Add it to " +
            "ExpectedStagesFor AND to AnalysisRepairModeExtensions.RunsGlossary/RunsDynamic (Services/Ai/AiOptions.cs), " +
            "then re-check every stage gate that consumes them.")
    };

    /// <summary>
    /// Pins the predicate pair over ALL FOUR modes. This is the shared gate that UnifiedAnalysisService's two
    /// repair stages AND BookReviewService's two engine hooks all consume, so pinning it here pins every seam.
    /// </summary>
    [Theory]
    [InlineData(AnalysisRepairMode.Off, false, false)]
    [InlineData(AnalysisRepairMode.Glossary, true, false)]
    [InlineData(AnalysisRepairMode.Dynamic, false, true)]
    [InlineData(AnalysisRepairMode.GlossaryThenDynamic, true, true)]
    public void StagePredicates_PinnedForEveryMode(AnalysisRepairMode mode, bool runsGlossary, bool runsDynamic)
    {
        Assert.Equal(runsGlossary, mode.RunsGlossary());
        Assert.Equal(runsDynamic, mode.RunsDynamic());
    }

    /// <summary>
    /// Guards the replicated-gate hazard at its source: every DEFINED enum value must have an explicit
    /// stage decision. A fifth mode added to the enum without updating the predicate pair trips this.
    /// </summary>
    [Fact]
    public void EveryDefinedMode_IsCoveredByTheStageTable()
    {
        foreach (var mode in Enum.GetValues<AnalysisRepairMode>())
        {
            var expected = ExpectedStagesFor(mode); // throws if a new mode was added without a decision
            Assert.Equal(expected.Glossary, mode.RunsGlossary());
            Assert.Equal(expected.Dynamic, mode.RunsDynamic());
        }
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public void ShippedMode_BindsIntoAiOptions_AndDrivesTheStageSelection(string fileName)
    {
        var path = FindUpward(Path.Combine("Pagedraft.Api", fileName));
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();

        var ai = config.GetSection("Ai").Get<AiOptions>();
        Assert.NotNull(ai);
        Assert.NotNull(ai!.AnalysisRepair);

        var mode = ai.AnalysisRepair!.Mode;

        // (1) The bound value came from the FILE, not from the class default. Both happen to be Glossary at
        //     the moment, so an equality check against the class default would prove nothing - compare the
        //     bound enum to the RAW STRING in the JSON instead. If the key were ever misspelled/removed, the
        //     binder would silently leave the class default here and this assertion catches it.
        var raw = config.GetSection("Ai:AnalysisRepair:Mode").Value;
        Assert.False(string.IsNullOrWhiteSpace(raw), $"{fileName} has no Ai:AnalysisRepair:Mode value ({path}).");
        Assert.True(string.Equals(raw, mode.ToString(), StringComparison.OrdinalIgnoreCase),
            $"{fileName} ships Ai:AnalysisRepair:Mode=\"{raw}\" but it bound to AnalysisRepairMode.{mode} - " +
            "the config value is not reaching AiOptions.AnalysisRepair.Mode.");

        // (2) The bound value is a DEFINED enum member. Enum binding accepts NUMERIC strings, so a stray
        //     "9" would bind to an undefined (AnalysisRepairMode)9 that satisfies neither stage predicate and
        //     would silently behave like Off. (An unrecognised NAME throws at bind time - pinned by
        //     InvalidModeName_ThrowsAtBindTime below - but the numeric hole does not, so guard it here.)
        Assert.True(Enum.IsDefined(mode),
            $"{fileName} ships Ai:AnalysisRepair:Mode=\"{raw}\", which bound to the UNDEFINED enum value " +
            $"({(int)mode}). It would select NO repair stage - a silent Off.");

        // (3) The shipped Mode drives the stage selection the repair layer actually takes. Both
        //     UnifiedAnalysisService.ApplyAnalysisRepairAsync (glossary stage + dynamic stage) and the
        //     BookReviewService engine hooks (glossary hook + dynamic hook) gate on exactly these two
        //     predicates, so asserting them here asserts all four seams. DERIVED from the bound value.
        var expected = ExpectedStagesFor(mode);
        Assert.Equal(expected.Glossary, mode.RunsGlossary());
        Assert.Equal(expected.Dynamic, mode.RunsDynamic());

        // (4) Sanity: the Mode knob is layered UNDER Enabled, so a shipped Enabled=false would make the whole
        //     stage question moot. Both files ship Enabled=true; assert it so the Mode assertions above stay
        //     load-bearing rather than vacuous.
        Assert.True(ai.AnalysisRepair.Enabled,
            $"{fileName} ships Ai:AnalysisRepair:Enabled=false, so the Mode above selects nothing at runtime.");
    }

    /// <summary>
    /// Pins what the configuration binder does with an UNRECOGNISED Mode name: it THROWS
    /// (InvalidOperationException wrapping the EnumConverter's parse failure) rather than silently falling
    /// back to the class default or to Off. That fail-fast is the behaviour we want from a typo in the
    /// shipped config - a silent fallback would disable a repair stage in production with no signal.
    /// </summary>
    [Fact]
    public void InvalidModeName_ThrowsAtBindTime()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:AnalysisRepair:Enabled"] = "true",
                ["Ai:AnalysisRepair:Mode"] = "Glossry" // typo
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => config.GetSection("Ai").Get<AiOptions>());
        Assert.Contains("Ai:AnalysisRepair:Mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The companion hazard: a NUMERIC Mode string does NOT throw - the binder happily produces an undefined
    /// enum value, which selects neither stage (a silent Off). Nothing in the binder catches this, which is
    /// why ShippedMode_BindsIntoAiOptions_AndDrivesTheStageSelection asserts Enum.IsDefined on the real files.
    /// </summary>
    [Fact]
    public void OutOfRangeNumericMode_BindsToAnUndefinedValue_ThatSelectsNoStage()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:AnalysisRepair:Enabled"] = "true",
                ["Ai:AnalysisRepair:Mode"] = "99"
            })
            .Build();

        var ai = config.GetSection("Ai").Get<AiOptions>();
        var mode = ai!.AnalysisRepair!.Mode;

        Assert.False(Enum.IsDefined(mode));
        Assert.False(mode.RunsGlossary());
        Assert.False(mode.RunsDynamic());
    }

    private static Dictionary<string, bool>? LoadPerType(string path)
    {
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        return config.GetSection("Ai:AnalysisRepair:PerType").Get<Dictionary<string, bool>>();
    }

    private static int? LoadSummaryBatchWindow(string path)
    {
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var raw = config.GetSection("Ai:AnalysisRepair:SummaryBatchWindowChapters").Value;
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static string? LoadMode(string path)
    {
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        return config.GetSection("Ai:AnalysisRepair:Mode").Value;
    }

    // Mirrors LanguageEngine/AnalysisRepairSmokeTests.FindUpward: walks up from the test assembly's
    // output directory to locate the API project's appsettings files.
    private static string FindUpward(string relativeSubPath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeSubPath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate " + relativeSubPath + " above " + AppContext.BaseDirectory);
    }
}
