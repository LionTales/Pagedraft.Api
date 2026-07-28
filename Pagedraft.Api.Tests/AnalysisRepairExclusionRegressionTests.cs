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
/// e1-enable-the-approved-types, the DELIBERATE NON-CHANGE.
///
/// e1's instruction was "wire ONLY the types q1 passed". q1 passed NONE, so the enable-set is EMPTY and NO
/// config key, NO dispatch arm and NO seam change was made. These tests are what makes that negative result
/// durable: they PIN the three currently-unrepaired analysis types as unrepaired, at every surface that would
/// have to change to enable one, so a later "it's just two config keys" edit turns RED with the reason.
///
///   • <c>Synopsis</c> — HALTED by q1 (2026-07-28) on the shipped LOCAL tier (Ollama | gemma4:12b):
///     preservation 83% (5/6) against a bar of >= 90%, with 1 false positive — the repair model
///     TRANSLITERATED a legitimate proper noun (Chekhov -> צ'כוב) at a paragraph head. Over-rewrite was 0 and
///     the cleaning side passed 100%, but the shipped bar (docs/ANALYSIS_OUTPUT_REPAIR.md section 18.2) is a
///     CONJUNCTION and the precision half failed. It reproduced IDENTICALLY on cloud gemma-4-31b-it, so it is
///     STRUCTURAL (a synopsis names authors/works/places the manuscript never mentions, so BookEntityProvider
///     cannot spare them, and sentence-initial Title-Case is deliberately not a proper-noun signal), not a
///     small-model artifact — swapping the repair model does not fix it.
///   • <c>Custom</c> — EXCLUDED by d1 (2026-07-28): its instruction is user-authored, so the output is
///     legitimately English / bilingual / quoted / tabular, which falsifies the layer's "foreign script =
///     model leakage" premise; and the layer costs one sequential model call per foreign WORD with no cap, so
///     a legitimately-English answer would be both silently mistranslated and a several-hundred-call GPU wedge.
///   • <c>Proofread</c> — excluded BY DESIGN and documented (docs section 4): its output quotes verbatim
///     manuscript spans, and repairing them would corrupt the suggestion diff.
///
/// Three independent surfaces are pinned, because enabling a type needs a change at each and a test that only
/// covered one would stay green for a half-enable:
///   (1) the ALLOWLIST — the shipped PerType map in BOTH config files, and the gate predicate it drives;
///   (2) the DISPATCH SWITCHES — GlossaryRepairPass.Apply and DynamicTermRepairService.ApplyAsync;
///   (3) the REAL PRODUCER SEAM — UnifiedAnalysisService.RunAsync for Custom (see
///       BookIntelligenceProfileRepairTests.BuildBookProfileAsync_UnderTheShippedConfig_LeavesSynopsisByteIdentical
///       for Synopsis's, which needs that file's full profile-build harness).
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

    /// <summary>Hebrew prose opening with a Latin proper noun. Title-Case but SENTENCE-INITIAL, so
    /// ForeignRunClassifier rule 7 does not spare it and an empty entity set leaves the dynamic stage to
    /// classify it REPAIR — i.e. exactly one term-repair model call. Same shape as q1's false positive
    /// (a proper noun at a paragraph head), reusing RunRawAnalysisRepairTests' proven Daniel/דניאל pair so
    /// the replacement is one the service's validation definitely accepts.</summary>
    private const string HebrewProseWithSentenceInitialLatinName = "Daniel נכנס אל החדר האפל ומצא את המכתב.";

    /// <summary>WHY each type stays unrepaired. This string is what a failing assertion prints, so it must
    /// carry the measurement/decision rather than "expected true".</summary>
    private static readonly IReadOnlyDictionary<string, string> ExclusionReasons = new Dictionary<string, string>
    {
        [nameof(AnalysisType.Synopsis)] =
            "q1 HALTED Synopsis on 2026-07-28: preservation 83% (5/6) on the shipped LOCAL tier " +
            "(Ollama | gemma4:12b) against a bar of >= 90% AND over-rewrite exactly 0, with 1 false positive " +
            "(the repair model TRANSLITERATED the legitimate proper noun \"Chekhov\" at a paragraph head). It " +
            "reproduced identically on cloud gemma-4-31b-it, so it is STRUCTURAL, not a small-model artifact - " +
            "swapping the repair model does not fix it. Do not enable Synopsis without re-running the d6 " +
            "preservation gate and clearing it. See the plan's `## q1 quality-gate results`.",
        [nameof(AnalysisType.Custom)] =
            "d1 EXCLUDED Custom on 2026-07-28: its instruction is user-authored, so its output is legitimately " +
            "English / bilingual / quoted / tabular - which falsifies the repair layer's \"foreign script = " +
            "model leakage\" premise - and the layer makes one sequential model call per foreign WORD with no " +
            "cap, so an English answer would be both silently mistranslated and a several-hundred-call GPU " +
            "wedge on a single-GPU host. See the plan's `## d1 decision`.",
        [nameof(AnalysisType.Proofread)] =
            "Proofread is NEVER repaired, by design and documented (docs/ANALYSIS_OUTPUT_REPAIR.md section 4): " +
            "its output quotes verbatim manuscript spans, so repairing them would corrupt the suggestion diff."
    };

    /// <summary>The three types this plan leaves unrepaired: (typeName, reason).</summary>
    public static IEnumerable<object[]> UnrepairedTypes()
    {
        foreach (var pair in ExclusionReasons) yield return new object[] { pair.Key, pair.Value };
    }

    /// <summary>The same three, once per shipped config file: (fileName, typeName, reason).</summary>
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
    /// fixture. Both switches matter: they are documented mirrors of each other
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
