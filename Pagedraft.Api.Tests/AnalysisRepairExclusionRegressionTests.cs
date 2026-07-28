using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Loads the SHIPPED <c>Ai:AnalysisRepair</c> block straight out of the API project's appsettings files, so a
/// test can assert against the config that actually ships rather than against a hand-authored options object.
/// Mirrors <c>AnalysisRepairConfigParityTests.FindUpward</c> / <c>LanguageEngine/AnalysisRepairSmokeTests</c>
/// (the established config-file-truth idiom in this suite).
/// </summary>
internal static class ShippedAnalysisRepairConfig
{
    public const string BaseFile = "appsettings.json";
    public const string ProductionFile = "appsettings.Production.json";

    /// <summary>The bound <c>Ai:AnalysisRepair</c> block from the named appsettings file.</summary>
    public static AnalysisRepairOptions Load(string fileName = BaseFile)
    {
        var path = FindUpward(Path.Combine("Pagedraft.Api", fileName));
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var ai = config.GetSection("Ai").Get<AiOptions>()
            ?? throw new InvalidOperationException($"Could not bind the Ai section of {path}.");
        return ai.AnalysisRepair
            ?? throw new InvalidOperationException($"{fileName} has no Ai:AnalysisRepair block ({path}).");
    }

    /// <summary>The raw <c>PerType</c> map from the named appsettings file (null when the key is absent).</summary>
    public static Dictionary<string, bool>? LoadPerType(string fileName)
    {
        var path = FindUpward(Path.Combine("Pagedraft.Api", fileName));
        return new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection("Ai:AnalysisRepair:PerType").Get<Dictionary<string, bool>>();
    }

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

/// <summary>
/// e1-enable-the-approved-types, the DELIBERATE NON-CHANGE — as narrowed by f2.
///
/// e1's instruction was "wire ONLY the types q1 passed". q1 passed NONE, so e1's enable-set was EMPTY and no
/// config key, dispatch arm or seam change was made. These tests are what makes that negative result durable:
/// they PIN the still-unrepaired analysis types as unrepaired, at every surface that would have to change to
/// enable one, so a later "it's just two config keys" edit turns RED with the reason.
///
///   • <c>Custom</c> — EXCLUDED by d1 (2026-07-28), reasoning NARROWED by c1 (same date): its instruction is
///     user-authored, so the output is legitimately English / bilingual / quoted / tabular, which falsifies
///     the layer's "foreign script = model leakage" premise; and the layer costs one sequential model call per
///     foreign WORD with no cap, so a legitimately-English answer would be both silently mistranslated and a
///     several-hundred-call GPU wedge. c1 then MEASURED the real corpus and found the BENEFIT side empty too
///     (n=16 rows, script fraction 1.0000 on every row, max REPAIR-run count 0).
///   • <c>Proofread</c> — excluded BY DESIGN and documented (docs section 4): its output quotes verbatim
///     manuscript spans, and repairing them would corrupt the suggestion diff.
///
/// <b><c>Synopsis</c> IS NO LONGER PINNED HERE — it is now REPAIRED.</b> q1 HALTED it on 83% preservation
/// (5/6) with 1 false positive (the repair model TRANSLITERATED "Chekhov" -> "צ'כוב" at a paragraph head).
/// c3 then landed ForeignRunClassifier rule (7b) — a Title-Case Latin run at a LINE HEAD is LEAVE, additive
/// by construction; be-c03 restated it as a LINE head (ONE hard line break, a blank line is NOT required,
/// which is what the persisted production prose actually uses) — and q2 (2026-07-28) re-measured on q1's OWN
/// fixtures: preservation
/// <b>100% (6/6)</b>, false positives <b>0</b>, over-rewrite <b>0</b>, cleaning 100% (3/3), LOCAL
/// Ollama|gemma4:12b and cloud gemma-4-31b-it identical, with ZERO recall cost on the shipped eight (d5 10/10,
/// model calls held at exactly 10; d6 21/21). f2 therefore wired it: PerType key `true` in both files plus a
/// plain-text dispatch arm in both DETERMINISTIC switches. The pins that used to assert Synopsis was untouched have been
/// FLIPPED, not deleted — see <see cref="ShippedSynopsisCoverage_IsWiredAtEveryDeterministicEnablingSurface"/> below and
/// BookIntelligenceProfileRepairTests.BuildBookProfileAsync_UnderTheShippedConfig_RepairsSynopsis. READ THAT
/// 100% AS A GATE PROPERTY: 0 of the 6 values reach the model, so rule (7b) does not make gemma4:12b better at
/// proper nouns, it stops asking it.
///
/// f2 wired Synopsis into the two DETERMINISTIC dispatch switches only; the THIRD switch (the value-scoped
/// LLM stage behind GuardOnly=false) excludes it DELIBERATELY, pinned by
/// <see cref="ShippedSynopsis_IsDeliberatelyOutsideTheValueScopedLlmStage"/> (be-c01).
///
/// Three independent surfaces are pinned, because enabling a type needs a change at each and a test that only
/// covered one would stay green for a half-enable:
///   (1) the ALLOWLIST — the shipped PerType map in BOTH config files, and the gate predicate it drives;
///   (2) the DISPATCH SWITCHES — there are THREE: GlossaryRepairPass.Apply (deterministic glossary stage),
///       DynamicTermRepairService.ApplyAsync (span-scoped dynamic stage) and
///       AnalysisRepairService.RepairAnalysisAsync (value-scoped LLM stage, reached only when
///       GuardOnly=false). The two deterministic ones run under the shipped config; the third is state (3);
///   (3) the REAL PRODUCER SEAM — UnifiedAnalysisService.RunAsync for Custom (Synopsis's real producer is the
///       profile build, covered by BookIntelligenceProfileRepairTests' full profile-build harness).
/// Every byte-identity assertion is paired with a SUMMARIZATION CONTROL on the same fixture and the same call,
/// so "unchanged" can never pass vacuously: the control proves the fixture really is repairable and the layer
/// really was live.
/// </summary>
public class AnalysisRepairExclusionRegressionTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Hebrew prose carrying "(Action)", a CLOSED-GLOSSARY term (LiteraryTermGlossary.Terms["action"]
    /// = "פעולה"). Deterministic: the glossary stage rewrites it with zero model calls, so a byte-identity
    /// assertion over this text is falsifiable the moment a dispatch arm exists.</summary>
    private const string HebrewProseWithGlossaryLeak = "התקציר מתאר (Action) מרכזית בעלילת הספר.";

    /// <summary>Hebrew prose opening with a Latin proper noun. Title-Case but VALUE-INITIAL, so
    /// ForeignRunClassifier rule 7 does not spare it (sentence-initial capitalization is orthography, not a
    /// name signal) and neither does c3's rule (7b), which deliberately does NOT claim the value start — a
    /// short value can plausibly OPEN with a capitalized leak, and there is no paragraph boundary to argue
    /// from. With an empty entity set the dynamic stage therefore classifies it REPAIR, i.e. exactly one
    /// term-repair model call, which makes it the non-vacuity CONTROL for the paragraph-head pin below.
    /// Reuses RunRawAnalysisRepairTests' proven Daniel/דניאל pair so the replacement is one the service's
    /// validation definitely accepts.</summary>
    private const string HebrewProseWithSentenceInitialLatinName = "Daniel נכנס אל החדר האפל ומצא את המכתב.";

    /// <summary>WHY each type stays unrepaired. This string is what a failing assertion prints, so it must
    /// carry the measurement/decision rather than "expected true".</summary>
    private static readonly IReadOnlyDictionary<string, string> ExclusionReasons = new Dictionary<string, string>
    {
        [nameof(AnalysisType.Custom)] =
            "d1 EXCLUDED Custom on 2026-07-28 (reasoning NARROWED by c1, same date): its instruction is " +
            "user-authored, so its output is legitimately English / bilingual / quoted / tabular - which " +
            "falsifies the repair layer's \"foreign script = model leakage\" premise - and the layer makes one " +
            "sequential model call per foreign WORD with no cap, so an English answer would be both silently " +
            "mistranslated and a several-hundred-call GPU wedge on a single-GPU host. c1 then MEASURED the " +
            "real Custom corpus and the BENEFIT side is empty too: n=16 rows / 6 distinct instructions / 1 " +
            "user / 2 books, Hebrew script fraction 1.0000 on EVERY row, max offline REPAIR-classified run " +
            "count 0, rows with at least one repair run 0/16 (Wilson 95% CI 0.0-19.4%) - an enabled Custom " +
            "repair would clean NOTHING on that data. See the plan's `## d1 decision` and `## c1 findings`.",
        [nameof(AnalysisType.Proofread)] =
            "Proofread is NEVER repaired, by design and documented (docs/ANALYSIS_OUTPUT_REPAIR.md section 4): " +
            "its output quotes verbatim manuscript spans, so repairing them would corrupt the suggestion diff."
    };

    /// <summary>The types this plan leaves unrepaired: (typeName, reason). Synopsis was here until f2 and is
    /// now REPAIRED - see <see cref="ShippedSynopsisCoverage_IsWiredAtEveryDeterministicEnablingSurface"/>.</summary>
    public static IEnumerable<object[]> UnrepairedTypes()
    {
        foreach (var pair in ExclusionReasons) yield return new object[] { pair.Key, pair.Value };
    }

    /// <summary>The same set, once per shipped config file: (fileName, typeName, reason).</summary>
    public static IEnumerable<object[]> UnrepairedTypesInBothFiles()
    {
        foreach (var file in new[] { ShippedAnalysisRepairConfig.BaseFile, ShippedAnalysisRepairConfig.ProductionFile })
        {
            foreach (var pair in ExclusionReasons) yield return new object[] { file, pair.Key, pair.Value };
        }
    }

    // ── Surface (1): the ALLOWLIST, as it ships ────────────────────────────────────────────────────────────

    /// <summary>
    /// The shipped <c>PerType</c> map must not enable any of the three. Asserted against BOTH files because
    /// appsettings.Production.json fully OVERRIDES (never merges with) the base Ai:AnalysisRepair block, so a
    /// key added to one file alone drifts the gate between environments. "Not enabled" accepts ABSENT or
    /// PRESENT-AND-FALSE: h3 may make Custom's exclusion explicit as <c>"Custom": false</c> (d1's deliverable),
    /// which AnalysisRepairGate.Evaluate treats identically to absence.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnrepairedTypesInBothFiles))]
    public void ShippedPerTypeMap_DoesNotEnable(string fileName, string typeName, string reason)
    {
        var perType = ShippedAnalysisRepairConfig.LoadPerType(fileName);
        Assert.True(perType is { Count: > 0 }, $"{fileName} has no Ai:AnalysisRepair:PerType map.");

        var enabled = perType!.TryGetValue(typeName, out var value) && value;
        Assert.False(enabled,
            $"{fileName} now ships Ai:AnalysisRepair:PerType:{typeName} = true, but {typeName} is a " +
            $"DELIBERATE exclusion. {reason}");
    }

    /// <summary>
    /// The allowlist assertion, restated at the PREDICATE the four gate call sites actually consult
    /// (<see cref="AnalysisRepairGate.Evaluate"/>, the single source of truth h1 extracted). This is the
    /// assertion that goes red on a CONFIG-ONLY enable — adding the key without a dispatch arm changes no
    /// behaviour, so the seam tests below would stay green while the config claimed coverage it cannot deliver
    /// (the "dead config" failure docs/ANALYSIS_OUTPUT_REPAIR.md:231-236 warns about).
    /// </summary>
    [Theory]
    [MemberData(nameof(UnrepairedTypes))]
    public void ShippedGate_IsClosedFor(string typeName, string reason)
    {
        var shipped = ShippedAnalysisRepairConfig.Load();

        Assert.True(shipped.Enabled, "The shipped repair layer is disabled outright; this test is vacuous.");
        Assert.Equal(AnalysisRepairGateReason.Allowed,
            AnalysisRepairGate.Evaluate(shipped, nameof(AnalysisType.Summarization))); // control: the gate opens for a repaired type

        Assert.True(AnalysisRepairGate.Evaluate(shipped, typeName) == AnalysisRepairGateReason.PerTypeExcluded,
            $"The shipped repair gate is now OPEN for {typeName}. {reason}");
    }

    // ── Surface (2): the two per-type DISPATCH SWITCHES ────────────────────────────────────────────────────

    /// <summary>
    /// The glossary stage has no dispatch arm for any of the three: <c>GlossaryRepairPass.Apply</c> falls to
    /// its <c>_ =&gt;</c> NoOp and returns the inputs byte-identical, even on a Hebrew book with a leak the
    /// closed glossary would otherwise rewrite deterministically. The Summarization control on the SAME text
    /// proves the fixture is genuinely repairable, so the byte-identity above is a real assertion.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnrepairedTypes))]
    public void GlossaryDispatch_HasNoArmFor(string typeName, string reason)
    {
        var type = Enum.Parse<AnalysisType>(typeName);

        var control = GlossaryRepairPass.Apply(
            AnalysisType.Summarization, null, HebrewProseWithGlossaryLeak, "he", JsonOpts);
        Assert.True(control.FieldsChanged > 0, // the fixture IS repairable at this seam
            "Summarization control did not repair the fixture, so the byte-identity assertions below would " +
            "pass vacuously. Fix the fixture before trusting this test.");
        Assert.Contains("(פעולה)", control.CleanContent);

        var actual = GlossaryRepairPass.Apply(type, null, HebrewProseWithGlossaryLeak, "he", JsonOpts);

        Assert.True(actual.FieldsChanged == 0 &&
            string.Equals(HebrewProseWithGlossaryLeak, actual.CleanContent, StringComparison.Ordinal),
            $"GlossaryRepairPass.Apply now has a dispatch arm for {type} (it rewrote the value). {reason}");
    }

    /// <summary>
    /// The dynamic span-scoped stage has no dispatch arm for any of the three either:
    /// <c>DynamicTermRepairService.ApplyAsync</c> falls to its <c>_ =&gt;</c> arm, returns the inputs
    /// byte-identical, and — the sharper assertion — makes ZERO term-repair model calls. The Summarization
    /// control makes exactly one on the same text, so the zero is a property of the missing arm, not of the
    /// fixture. Both DETERMINISTIC switches matter: they are documented mirrors of each other
    /// (DynamicTermRepairService.cs:451-457) and the shipped Mode=GlossaryThenDynamic runs BOTH stages, so
    /// adding one arm alone would ship half the layer.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnrepairedTypes))]
    public async Task DynamicDispatch_HasNoArmFor(string typeName, string reason)
    {
        var type = Enum.Parse<AnalysisType>(typeName);

        var controlRouter = TermRepairRouter();
        var control = await NewDynamicTermRepair(controlRouter).ApplyAsync(
            AnalysisType.Summarization, null, HebrewProseWithSentenceInitialLatinName, "he", JsonOpts,
            bookEntities: null, CancellationToken.None);
        Assert.True(control.fieldsChanged > 0, // the fixture IS repairable at this seam
            "Summarization control did not repair the fixture, so the byte-identity assertions below would " +
            "pass vacuously. Fix the fixture before trusting this test.");
        controlRouter.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        var router = TermRepairRouter();
        var actual = await NewDynamicTermRepair(router).ApplyAsync(
            type, null, HebrewProseWithSentenceInitialLatinName, "he", JsonOpts,
            bookEntities: null, CancellationToken.None);

        Assert.True(actual.fieldsChanged == 0 &&
            string.Equals(HebrewProseWithSentenceInitialLatinName, actual.cleanContent, StringComparison.Ordinal),
            $"DynamicTermRepairService.ApplyAsync now has a dispatch arm for {type} (it rewrote the value). {reason}");
        router.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── f2: SYNOPSIS, the type that MOVED OUT of the exclusion set (2026-07-28) ────────────────────────────
    //
    // These are the flipped counterparts of the three pins above. q1 HALTED Synopsis on 83% preservation with
    // one false positive; c3 landed ForeignRunClassifier rule (7b) and q2 re-measured 100% (6/6) / 0 FP /
    // 0 over-rewrite / 0 values reaching the model, with ZERO recall cost on the shipped eight. f2 wired it.
    // A stale "Synopsis is untouched" pin would now be actively wrong, so the assertions are INVERTED rather
    // than deleted: the same three surfaces are asserted OPEN, and a silent un-wiring turns them red.

    /// <summary>Synopsis-shaped Hebrew prose whose SECOND PARAGRAPH opens with a Latin proper noun - the exact
    /// shape of q1's measured false positive (`Chekhov` at a paragraph head, transliterated by the repair
    /// model). Rule (7b) now classifies that position LEAVE deterministically, so this value must come back
    /// byte-identical with ZERO model calls even though Synopsis is fully wired. This is the property that
    /// let Synopsis ship, so it is pinned at the dispatch seam rather than only in the classifier's own
    /// unit tests.</summary>
    private const string SynopsisProseWithParagraphHeadName =
        "התקציר פותח בתיאור הרקע ההיסטורי של היצירה ובמצבה של הגיבורה.\n\n" +
        "Chekhov הוא ההשוואה המתבקשת לסגנון הסיפור ולקצב שלו.";

    /// <summary>
    /// Surface (1) + (2) for Synopsis, asserted OPEN. The shipped <c>PerType</c> map must enable it in BOTH
    /// files (Production fully OVERRIDES the base block, so a one-file enable drifts the gate between
    /// environments), <see cref="AnalysisRepairGate.Evaluate"/> must return <c>Allowed</c> for it, and both
    /// DETERMINISTIC dispatch switches must carry the plain-text arm - the shipped
    /// <c>Mode=GlossaryThenDynamic</c> runs both stages, so one arm alone would ship half the layer.
    ///
    /// SCOPE, be-c01: "every enabling surface" means every surface that is ENABLED for Synopsis, which is the
    /// two DETERMINISTIC switches and the allowlist that opens them. The THIRD dispatch switch
    /// (<c>AnalysisRepairService.RepairAnalysisAsync</c>, the value-scoped LLM stage behind
    /// <c>GuardOnly=false</c>) is a NAMED EXCLUSION for Synopsis, pinned in the opposite direction by
    /// <see cref="ShippedSynopsis_IsDeliberatelyOutsideTheValueScopedLlmStage"/>. This test asserted two
    /// surfaces under the name "every enabling surface" before that distinction was recorded; the name now
    /// says which two.
    ///
    /// Every assertion is paired with its own falsifier: the glossary half asserts the leak was REWRITTEN (not
    /// merely "did not throw"), and the dynamic half asserts the router was called EXACTLY ONCE, so a missing
    /// arm cannot look like a clean value.
    /// </summary>
    [Fact]
    public async Task ShippedSynopsisCoverage_IsWiredAtEveryDeterministicEnablingSurface()
    {
        const string why =
            "Synopsis was ENABLED by f2 on 2026-07-28 after q2 cleared the shipped bar " +
            "(docs/ANALYSIS_OUTPUT_REPAIR.md section 18.2: preservation >= 90% AND over-rewrite exactly 0) on " +
            "q1's OWN fixtures: preservation 100% (6/6), false positives 0, over-rewrite 0, cleaning 100% " +
            "(3/3), LOCAL Ollama | gemma4:12b and cloud gemma-4-31b-it identical, with ZERO recall cost on the " +
            "shipped eight (d5 10/10 with model calls held at exactly 10, d6 21/21). That SUPERSEDES q1's 83% " +
            "/ 1-FP HALT. If you are intentionally rolling Synopsis back, flip the PerType key to false in " +
            "BOTH appsettings files and update this test plus " +
            "AnalysisRepairConfigParityTests.DecisionFor - do not leave a half-wired type. See the plan's " +
            "`## q2 quality-gate results` and `## f2 outcome`.";

        // (1a) the shipped allowlist, in BOTH files.
        foreach (var file in new[] { ShippedAnalysisRepairConfig.BaseFile, ShippedAnalysisRepairConfig.ProductionFile })
        {
            var perType = ShippedAnalysisRepairConfig.LoadPerType(file);
            Assert.True(perType is { Count: > 0 }, $"{file} has no Ai:AnalysisRepair:PerType map.");
            Assert.True(perType!.TryGetValue(nameof(AnalysisType.Synopsis), out var value) && value,
                $"{file} no longer ships Ai:AnalysisRepair:PerType:Synopsis = true. {why}");
        }

        // (1b) the predicate the four gate call sites actually consult.
        var shipped = ShippedAnalysisRepairConfig.Load();
        Assert.True(shipped.Enabled, "The shipped repair layer is disabled outright; this test is vacuous.");
        Assert.True(
            AnalysisRepairGate.Evaluate(shipped, nameof(AnalysisType.Synopsis)) == AnalysisRepairGateReason.Allowed,
            $"The shipped repair gate is CLOSED for Synopsis. {why}");

        // (2a) the GLOSSARY dispatch arm: the closed-glossary leak is deterministically rewritten.
        var glossary = GlossaryRepairPass.Apply(
            AnalysisType.Synopsis, null, HebrewProseWithGlossaryLeak, "he", JsonOpts);
        Assert.True(glossary.FieldsChanged > 0 && glossary.CleanContent.Contains("(פעולה)"),
            $"GlossaryRepairPass.Apply no longer has a dispatch arm for Synopsis. {why}");

        // (2b) the DYNAMIC dispatch arm: a VALUE-INITIAL Latin name is a REPAIR run (rule (7b) deliberately
        //      does NOT claim the value start), so the arm must produce exactly one term-repair model call.
        var router = TermRepairRouter();
        var dynamic = await NewDynamicTermRepair(router).ApplyAsync(
            AnalysisType.Synopsis, null, HebrewProseWithSentenceInitialLatinName, "he", JsonOpts,
            bookEntities: null, CancellationToken.None);
        Assert.True(dynamic.fieldsChanged > 0 &&
            !string.Equals(HebrewProseWithSentenceInitialLatinName, dynamic.cleanContent, StringComparison.Ordinal),
            $"DynamicTermRepairService.ApplyAsync no longer has a dispatch arm for Synopsis. {why}");
        router.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// WHY Synopsis was allowed to ship, pinned at the dispatch seam: rule (7b) gates the paragraph-head
    /// proper noun DETERMINISTICALLY, so the value q1 lost never reaches the repair model at all. The
    /// value-initial control in the SAME test proves the pin is not vacuous - the arm is live and does call the
    /// model when the classifier says REPAIR.
    ///
    /// This is also the honest reading of q2's 100%: it is a property of the GATE, not of gemma4:12b. If a
    /// future change reopens rule (7b), THIS test goes red before any Synopsis false positive reaches a user.
    /// </summary>
    [Fact]
    public async Task ShippedSynopsis_ParagraphHeadProperNoun_NeverReachesTheRepairModel()
    {
        var router = TermRepairRouter();
        var result = await NewDynamicTermRepair(router).ApplyAsync(
            AnalysisType.Synopsis, null, SynopsisProseWithParagraphHeadName, "he", JsonOpts,
            bookEntities: null, CancellationToken.None);

        Assert.True(result.fieldsChanged == 0 &&
            string.Equals(SynopsisProseWithParagraphHeadName, result.cleanContent, StringComparison.Ordinal),
            "A Latin proper noun at a PARAGRAPH HEAD inside a Synopsis value was rewritten. That is q1's " +
            "measured false positive (\"Chekhov\" -> \"צ'כוב\") returning: ForeignRunClassifier rule (7b) is " +
            "what makes this position a deterministic LEAVE, and it is the entire reason Synopsis cleared the " +
            "preservation bar in q2 (100% (6/6) with 0 of 6 values reaching the model). If rule (7b) is being " +
            "removed or narrowed, re-run the d6 preservation gate for Synopsis before shipping it.\n" +
            $"  expected: {SynopsisProseWithParagraphHeadName}\n  actual:   {result.cleanContent}");
        router.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // NON-VACUITY: the same arm, the same router, a position rule (7b) does NOT claim (value-initial) -
        // there the model IS called. So the Times.Never above is a property of (7b), not of a dead arm.
        var control = TermRepairRouter();
        var reached = await NewDynamicTermRepair(control).ApplyAsync(
            AnalysisType.Synopsis, null, HebrewProseWithSentenceInitialLatinName, "he", JsonOpts,
            bookEntities: null, CancellationToken.None);
        Assert.True(reached.fieldsChanged > 0,
            "The value-initial control did not repair, so the paragraph-head assertion above would pass " +
            "vacuously (a dead dispatch arm looks identical to a deterministic LEAVE).");
        control.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Hebrew prose carrying a NON-glossary, non-proper-noun English leak ("confusion" - the real
    /// leak `OutputQualityDiagnostic` caught in the wild, docs section 11). Deliberately outside
    /// `LiteraryTermGlossary.Terms`, so it isolates the VALUE-SCOPED LLM stage: the deterministic glossary
    /// cannot touch it, and `LatinInHebrewContentDetector.HasNonAllowlistedLatin` flags it, which is exactly
    /// the guard `AnalysisRepairService.RepairPlainTextAsync` consults before calling the model.</summary>
    private const string HebrewProseWithNonGlossaryLeak = "התקציר מתאר confusion מרכזי בעלילת הספר.";

    /// <summary>The repaired form the scripted router returns. Passes every `IsAcceptableRepair` guard
    /// (non-empty, predominantly Hebrew, introduces no NEW Latin run, length ratio ~0.90 inside [0.6, 1.6]),
    /// so an arm that DOES exist really does change the value rather than failing validation and looking
    /// like a no-op.</summary>
    private const string HebrewProseRepaired = "התקציר מתאר בלבול מרכזי בעלילת הספר.";

    /// <summary>
    /// be-c01, THE NAMED EXCLUSION PIN. There are THREE per-type dispatch switches, not two, and `f2` wired
    /// Synopsis into only the two DETERMINISTIC ones. The third,
    /// <c>AnalysisRepairService.RepairAnalysisAsync</c>, is the VALUE-SCOPED LLM stage that
    /// <c>UnifiedAnalysisService.ApplyAnalysisRepairAsync</c> invokes when <c>GuardOnly=false</c> (documented
    /// state (3), one config line away). Synopsis stays OUT of it DELIBERATELY, and this pins that decision.
    ///
    /// WHY: q1 HALTED Synopsis at 83% preservation with one false positive - the repair model TRANSLITERATED
    /// "Chekhov" at a paragraph head. What cleared the section-18.2 bar in q2 was ForeignRunClassifier rule
    /// (7b) turning that position into a deterministic LEAVE, so 0 of 6 legitimate values reach a model at
    /// all. Rule (7b) is consulted ONLY by DynamicTermRepairService; this stage never touches the classifier
    /// and hands the WHOLE value to the model, and its validator (no NEW Latin run, length ratio 0.6-1.6)
    /// does not catch a transliteration - it REMOVES a Latin run and barely moves the length. An arm here
    /// would therefore ship precisely the configuration q1 measured as a HALT. Full argument and reversal
    /// condition at that switch's <c>default:</c> arm.
    ///
    /// NON-VACUITY: the Summarization control runs the SAME stage over the SAME fixture with the SAME
    /// scripted router and IS repaired, with the model called exactly once - so the Synopsis zero is a
    /// property of the missing arm, not of a clean fixture or a dead stage.
    ///
    /// This test drives <c>AnalysisRepairService</c> directly rather than <c>RunAsync</c> because that IS the
    /// value-scoped stage; the wiring premise (the gate is OPEN for Synopsis, so <c>GuardOnly=false</c> is
    /// the only thing between the shipped config and this call) is asserted separately below.
    /// </summary>
    [Fact]
    public async Task ShippedSynopsis_IsDeliberatelyOutsideTheValueScopedLlmStage()
    {
        const string why =
            "Synopsis is DELIBERATELY not a dispatch arm in AnalysisRepairService.RepairAnalysisAsync " +
            "(be-c01, 2026-07-28) - the value-scoped LLM stage that runs under GuardOnly=false. That stage " +
            "has no ForeignRunClassifier rule-(7b) gate, and rule (7b) is the entire reason Synopsis cleared " +
            "the section-18.2 bar in q2 (0 of 6 legitimate values reach a model). The only measurement of a " +
            "repair model rewriting Synopsis prose is q1's 83% preservation / 1 false positive HALT. If you " +
            "are intentionally adding the arm, re-run the section-18.2 gate for Synopsis THROUGH THIS " +
            "SERVICE first, and update AnalysisRepairConfigParityTests.DispatchCoverageFor. See that " +
            "switch's `default:` arm and docs/ANALYSIS_OUTPUT_REPAIR.md sections 4.1 / 4.2.";

        // (0) THE WIRING PREMISE: the shipped allowlist opens the gate for Synopsis, so ApplyAnalysisRepairAsync
        //     reaches `if (!cfg.GuardOnly) -> _analysisRepair.RepairAnalysisAsync(...)` for it. The exclusion
        //     below is therefore a real decision about a REACHABLE path, not a note about dead code.
        var shipped = ShippedAnalysisRepairConfig.Load();
        Assert.True(shipped.Enabled, "The shipped repair layer is disabled outright; this test is vacuous.");
        Assert.True(
            AnalysisRepairGate.Evaluate(shipped, nameof(AnalysisType.Synopsis)) == AnalysisRepairGateReason.Allowed,
            "The shipped repair gate is CLOSED for Synopsis, so this exclusion pin no longer covers a " +
            "reachable path. Re-check f2 before trusting it.");

        // (1) CONTROL FIRST, so a failure says whether the stage was even live: Summarization, same fixture,
        //     same scripted router - the value IS repaired and the model IS called exactly once.
        var controlRouter = ValueScopedRepairRouter();
        var control = await NewAnalysisRepair(controlRouter).RepairAnalysisAsync(
            AnalysisType.Summarization, structuredJson: null, HebrewProseWithNonGlossaryLeak, "he", JsonOpts,
            CancellationToken.None);
        Assert.True(control.LlmRepaired == 1 && string.Equals(HebrewProseRepaired, control.CleanContent, StringComparison.Ordinal),
            "The Summarization control did not repair the fixture, so the byte-identity assertion below would " +
            "pass vacuously. Fix the fixture (or the scripted reply) before trusting this test.\n" +
            $"  expected: {HebrewProseRepaired}\n  actual:   {control.CleanContent}");
        controlRouter.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        // (2) THE PIN: Synopsis falls to the `default:` arm - byte-identical value, all counters 0, and the
        //     sharper assertion, ZERO model calls.
        var router = ValueScopedRepairRouter();
        var actual = await NewAnalysisRepair(router).RepairAnalysisAsync(
            AnalysisType.Synopsis, structuredJson: null, HebrewProseWithNonGlossaryLeak, "he", JsonOpts,
            CancellationToken.None);

        Assert.True(string.Equals(HebrewProseWithNonGlossaryLeak, actual.CleanContent, StringComparison.Ordinal),
            $"AnalysisRepairService.RepairAnalysisAsync now REPAIRS a Synopsis value. {why}\n" +
            $"  expected: {HebrewProseWithNonGlossaryLeak}\n  actual:   {actual.CleanContent}");
        Assert.True(actual.LlmFlagged == 0 && actual.LlmRepaired == 0 && actual.LlmFailSafe == 0,
            $"AnalysisRepairService.RepairAnalysisAsync now has a dispatch arm for Synopsis (it flagged " +
            $"{actual.LlmFlagged} / repaired {actual.LlmRepaired} / fail-safed {actual.LlmFailSafe} " +
            $"field(s)). {why}");
        router.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A router scripted for the VALUE-SCOPED stage: it returns a whole repaired Hebrew value (not a
    /// span-repair JSON envelope), which is what <see cref="AnalysisRepairService"/> expects.</summary>
    private static Mock<IAiRouter> ValueScopedRepairRouter()
    {
        var mock = new Mock<IAiRouter>();
        mock.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = HebrewProseRepaired, Provider = "test", Model = "test" });
        return mock;
    }

    private static AnalysisRepairService NewAnalysisRepair(Mock<IAiRouter> router)
        => new(router.Object, NullLogger<AnalysisRepairService>.Instance);

    /// <summary>A term-repair router whose reply passes DynamicTermRepairService's replacement validation
    /// (native script, one word, well inside the length cap), so a dispatch arm that DOES exist really does
    /// change the value rather than failing validation and looking like a no-op.</summary>
    private static Mock<IAiRouter> TermRepairRouter()
    {
        var mock = new Mock<IAiRouter>();
        mock.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = "{\"replacement\":\"דניאל\"}", Provider = "test", Model = "test" });
        return mock;
    }

    private static DynamicTermRepairService NewDynamicTermRepair(Mock<IAiRouter> router)
        => new(router.Object, NullLogger<DynamicTermRepairService>.Instance);

    // ── Surface (3): Custom's REAL PRODUCER SEAM (UnifiedAnalysisService.RunAsync) ─────────────────────────

    /// <summary>
    /// Custom's real path: the editor type picker and the async-job path both land on
    /// <c>UnifiedAnalysisService.RunAsync</c> with the user's prompt as the instruction. Under the SHIPPED
    /// config (Enabled=true, GuardOnly=true, Mode=GlossaryThenDynamic — loaded from appsettings.json, not
    /// hand-authored) the persisted <c>ResultText</c> must be byte-identical to what the model returned.
    ///
    /// Non-vacuity is proved inside the same test by the Summarization control: the SAME service, the SAME
    /// model output, the SAME Hebrew book — and there the leak IS rewritten. So a green here means "the layer
    /// ran and skipped Custom", never "the layer was off".
    ///
    /// Also pinned: <c>StructuredResult</c> is null for Custom (TryParseStructured's <c>_ =&gt; null</c> arm),
    /// which is WHY enabling it would make the entire ResultText repairable prose with no field whitelist —
    /// the widest seam in the layer, and half of d1's reason for excluding it.
    /// </summary>
    [Fact]
    public async Task RunAsync_Custom_UnderTheShippedConfig_LeavesResultTextByteIdentical()
    {
        const string modelOutput = "התשובה מתארת (Action) מרכזית בעלילה.";

        await using var db = NewDb();
        var svc = NewUnifiedAnalysisService(db, modelOutput, out var chapterId);

        var custom = await svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.Custom, chapterId,
            customPrompt: "סכם את הפרק במשפט אחד.", language: "he", jobId: null, ct: CancellationToken.None);

        // Control FIRST, so a failure tells you whether the layer was even live.
        var control = await svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.Summarization, chapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);
        Assert.Contains("(פעולה)", control.ResultText);
        Assert.DoesNotContain("Action", control.ResultText);

        Assert.True(string.Equals(modelOutput, custom.ResultText, StringComparison.Ordinal),
            "A Custom analysis result is now being REPAIRED on the RunAsync seam. Expected the model's text " +
            $"back byte-identical.\n  expected: {modelOutput}\n  actual:   {custom.ResultText}\n" +
            "d1 EXCLUDED Custom on 2026-07-28: its instruction is user-authored, so its output is legitimately " +
            "English / bilingual / quoted / tabular — which falsifies the repair layer's 'foreign script = " +
            "model leakage' premise — and the layer makes one sequential model call per foreign WORD with no " +
            "cap, so an English answer would be both mistranslated and a several-hundred-call GPU wedge.");

        // The reason the blast radius would be the WHOLE text: Custom has no structured payload to whitelist.
        Assert.Null(custom.StructuredResult);
    }

    private static AppDbContext NewDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>A directly-constructed <see cref="UnifiedAnalysisService"/> wired to the SHIPPED
    /// Ai:AnalysisRepair block, mirroring AnalysisRunLogTests' construction of the same service.</summary>
    private static UnifiedAnalysisService NewUnifiedAnalysisService(
        AppDbContext db, string modelOutput, out Guid chapterId)
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = modelOutput, Provider = "test", Model = "gemma4:12b" });

        var bookId = Guid.NewGuid();
        chapterId = Guid.NewGuid();
        var chapter = chapterId;
        var context = new Mock<IAnalysisContextService>();
        context.Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), It.IsAny<Guid>(), It.IsAny<AnalysisType>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AnalysisScope scope, Guid _, AnalysisType type, string _, CancellationToken _) =>
                new AnalysisContext
                {
                    TargetText = "טקסט הפרק לבדיקה.",
                    Scope = scope,
                    AnalysisType = type,
                    BookId = bookId,
                    ChapterId = chapter,
                    SceneId = null
                });

        return new UnifiedAnalysisService(
            db,
            router.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(new AiOptions { AnalysisRepair = ShippedAnalysisRepairConfig.Load() }),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            context.Object,
            new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions()),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());
    }
}
