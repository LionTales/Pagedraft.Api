using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Regression tests for the analysis-output repair wiring on <see cref="UnifiedAnalysisService.RunRawAsync"/>.
///
/// RunRawAsync is the NON-persisting path BookIntelligenceService.SummarizeChaptersAsync uses to produce the
/// flat chapter summary it stores in ChunkSummary.SummaryText. It previously returned the sanitized model
/// text WITHOUT running ApplyAnalysisRepairAsync — so a Hebrew Summarization routed through it skipped the
/// shipped glossary repair that RunAsync / RunWithInputAsync / streaming / chunked LineEdit all apply, and
/// persisted chapter summaries leaked English the rest of the pipeline had cleaned. These tests pin that
/// RunRawAsync now runs the SAME repair layer, still type-scoped and Hebrew-gated (no over-application).
/// </summary>
public class RunRawAnalysisRepairTests
{
    private static IAiRouter RouterReturning(string content)
    {
        var mock = new Mock<IAiRouter>();
        mock.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = content, Provider = "test", Model = "test" });
        return mock.Object;
    }

    private static UnifiedAnalysisService NewService(AppDbContext db, IAiRouter router, AnalysisRepairOptions? repair)
        => new(
            db,
            router,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(new AiOptions { AnalysisRepair = repair }),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            new Mock<IAnalysisContextService>().Object,
            new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions()),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance));

    private static AppDbContext NewDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>The core regression: a Hebrew Summarization with an English leak is glossary-repaired on the
    /// raw path exactly as it would be on the persisted seams (shipped default { Enabled:true, GuardOnly:true }).</summary>
    [Fact]
    public async Task RunRawAsync_HebrewSummarization_AppliesGlossaryRepair()
    {
        await using var db = NewDb();
        var router = RouterReturning("סיכום הפרק כולל תיאור פעולה (Action) מהיר.");
        var svc = NewService(db, router, new AnalysisRepairOptions { Enabled = true, GuardOnly = true });

        var result = await svc.RunRawAsync(
            "טקסט הפרק.", AnalysisType.Summarization, instruction: null, language: "he", CancellationToken.None);

        Assert.Contains("(פעולה)", result);
        Assert.DoesNotContain("Action", result);
    }

    /// <summary>Guard: the repair stays TYPE-SCOPED. A non-target type (BookOverview) is a strict no-op even on
    /// a Hebrew book, so RunRawAsync must not translate its Latin — proving the fix reused the type-aware layer
    /// rather than blanket-translating every raw run.</summary>
    [Fact]
    public async Task RunRawAsync_NonTargetType_LeavesLatinUntouched()
    {
        await using var db = NewDb();
        var router = RouterReturning("סקירה כוללת (Action) כאן.");
        var svc = NewService(db, router, new AnalysisRepairOptions { Enabled = true, GuardOnly = true });

        var result = await svc.RunRawAsync(
            "טקסט.", AnalysisType.BookOverview, instruction: null, language: "he", CancellationToken.None);

        Assert.Contains("Action", result); // BookOverview is not a repair target → unchanged
    }

    /// <summary>Guard: the repair respects config. With the layer disabled (no Ai:AnalysisRepair block) the raw
    /// path returns the sanitized text unchanged, same as before the fix.</summary>
    [Fact]
    public async Task RunRawAsync_RepairDisabled_ReturnsSanitizedTextUnchanged()
    {
        await using var db = NewDb();
        var router = RouterReturning("סיכום עם (Action) בפנים.");
        var svc = NewService(db, router, repair: null);

        var result = await svc.RunRawAsync(
            "טקסט.", AnalysisType.Summarization, instruction: null, language: "he", CancellationToken.None);

        Assert.Contains("Action", result); // layer off → no glossary applied
    }
}
