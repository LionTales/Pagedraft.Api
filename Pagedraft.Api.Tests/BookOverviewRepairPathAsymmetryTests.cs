using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
/// f1-adjudicate-bookoverview-dead-key: pins the PATH ASYMMETRY the adjudication rests on.
/// <c>"BookOverview": true</c> in the shipped <c>Ai:AnalysisRepair:PerType</c> map (loaded here via
/// <see cref="ShippedAnalysisRepairConfig"/>, not hand-authored) is a genuine no-op on the BOOK-PROFILE path —
/// <c>BookIntelligenceService.BuildBookProfileAsync</c> (BookIntelligenceService.cs:523) produces BookOverview
/// through <see cref="UnifiedAnalysisService.RunRawAsync"/>, whose second half
/// (<c>CompleteDeferredRepairAsync</c>, UnifiedAnalysisService.cs:985-987) ALWAYS calls
/// <c>ApplyAnalysisRepairAsync(structuredJson: null, ...)</c> regardless of <see cref="AnalysisType"/>, and
/// <c>GlossaryRepairPass.Apply</c>'s <c>BookOverview</c> arm (<c>RepairStructured&lt;BookOverviewResult&gt;</c>,
/// GlossaryRepairPass.cs:148-149) is unconditionally a no-op whenever <c>structuredJson</c> is null/blank
/// (GlossaryRepairPass.cs:227-230) — BUT the same key is genuinely LIVE on the direct-analyze / controller
/// path: <c>AnalysisController.TryParseAnalysisType</c> (AnalysisController.cs:151-156) is a bare
/// <c>Enum.TryParse</c>, so a request carrying <c>analysisType:"BookOverview"</c> resolves at
/// <c>ResolveAnalysisParamsAsync</c> (:131-132) and reaches <see cref="UnifiedAnalysisService.RunAsync"/>
/// (AnalysisController.cs:66) — the exact method under test below — which DOES parse a structured
/// <c>BookOverviewResult</c> (<c>TryParseStructured</c>, UnifiedAnalysisService.cs:2322) and DOES repair its
/// <c>Summary</c> field.
///
/// Both tests use the SAME leak-carrying content and the SAME shipped repair config, so the divergence in
/// outcome is attributable ONLY to which seam produced it, never to a config or fixture difference — that is
/// the asymmetry f1 adjudicated (KEEP + document; see the plan's <c>## f1 decision</c>).
/// </summary>
public class BookOverviewRepairPathAsymmetryTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>A well-formed BookOverviewResult JSON envelope whose repairable field (<c>summary</c>, per
    /// <c>RepairableFields.For(BookOverviewResult)</c>, RepairableFields.cs:185) carries "(Action)" — a
    /// CLOSED-GLOSSARY term (<c>LiteraryTermGlossary.Terms["action"]</c> = "פעולה") the deterministic glossary
    /// stage rewrites with zero model calls, so the assertions below are falsifiable rather than passing by
    /// construction.</summary>
    private const string BookOverviewJsonWithLeak =
        "{\"genre\":\"פנטזיה\",\"subGenre\":\"אפי\",\"targetAudience\":\"מבוגרים\"," +
        "\"literatureLevel\":3,\"estimatedReadingTimeMinutes\":120,\"languageRegister\":\"רשמי\"," +
        "\"summary\":\"סקירה כללית עם (Action) מרכזית בעלילה.\"}";

    private static AppDbContext NewDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Mirrors <c>AnalysisRepairExclusionRegressionTests.NewUnifiedAnalysisService</c> — a
    /// directly-constructed service wired to the SHIPPED <c>Ai:AnalysisRepair</c> block (not a hand-authored
    /// options object), with a working context mock (needed only by <c>RunAsync</c>) and a router that always
    /// returns <paramref name="modelOutput"/> verbatim.</summary>
    private static UnifiedAnalysisService NewShippedService(
        AppDbContext db, string modelOutput, out Guid bookId, out Guid chapterId)
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = modelOutput, Provider = "test", Model = "gemma4:12b" });

        var book = Guid.NewGuid();
        var chapter = Guid.NewGuid();
        bookId = book;
        chapterId = chapter;

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
                    BookId = book,
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

    /// <summary>
    /// THE CONTROLLER / DIRECT-ANALYZE PATH: <c>RunAsync</c> parses BookOverview's structured JSON and the
    /// shipped repair layer genuinely rewrites the glossary leak in <c>Summary</c>. This is byte-for-byte the
    /// method <c>AnalysisController.RunAsync</c> (line 66) calls once <c>TryParseAnalysisType</c> (:151-156)
    /// has resolved a client's <c>analysisType:"BookOverview"</c> — an unadvertised but reachable path.
    /// </summary>
    [Fact]
    public async Task RunAsync_BookOverview_UnderTheShippedConfig_RepairsSummary()
    {
        await using var db = NewDb();
        var svc = NewShippedService(db, BookOverviewJsonWithLeak, out _, out var chapterId);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.BookOverview, chapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        Assert.NotNull(result.StructuredResult);
        var overview = JsonSerializer.Deserialize<BookOverviewResult>(result.StructuredResult!, JsonOpts)!;
        Assert.Contains("(פעולה)", overview.Summary);
        Assert.DoesNotContain("Action", overview.Summary);
    }

    /// <summary>
    /// THE BOOK-PROFILE PATH: the SAME shipped config and the SAME leak-carrying content reach the model
    /// through <c>RunRawAsync</c> instead (what <c>BuildBookProfileAsync</c> actually calls,
    /// BookIntelligenceService.cs:523) — and here the repair is a strict no-op, because <c>RunRawAsync</c>'s
    /// second half (<c>CompleteDeferredRepairAsync</c>) always calls <c>ApplyAnalysisRepairAsync</c> with
    /// <c>structuredJson: null</c>, and <c>GlossaryRepairPass.Apply</c>'s <c>BookOverview</c> arm
    /// (<c>RepairStructured&lt;BookOverviewResult&gt;</c>) returns the inputs byte-identical whenever
    /// <c>structuredJson</c> is null/blank. Non-vacuity is proved by the SIBLING test above: the same shipped
    /// config plus the same leak-carrying content DOES get repaired at the <c>RunAsync</c> seam, so a
    /// byte-identical result here means "this seam skips it", never "the layer is off".
    /// </summary>
    [Fact]
    public async Task RunRawAsync_BookOverview_UnderTheShippedConfig_LeavesLeakUntouched()
    {
        await using var db = NewDb();
        var svc = NewShippedService(db, BookOverviewJsonWithLeak, out var bookId, out _);

        var raw = await svc.RunRawAsync(
            "טקסט.", AnalysisType.BookOverview, instruction: null, language: "he",
            bookId: bookId, ct: CancellationToken.None);

        Assert.True(string.Equals(BookOverviewJsonWithLeak, raw, StringComparison.Ordinal),
            "RunRawAsync now repairs BookOverview (the book-profile-path seam) — the f1 path asymmetry no " +
            "longer holds; docs/ANALYSIS_OUTPUT_REPAIR.md section 4.1's BookOverview row needs " +
            "re-adjudicating, and RunAsync_BookOverview_UnderTheShippedConfig_RepairsSummary shows the layer " +
            "was live for this exact fixture, so this is a real regression, not a disabled layer.\n" +
            $"  expected: {BookOverviewJsonWithLeak}\n  actual:   {raw}");
    }
}
