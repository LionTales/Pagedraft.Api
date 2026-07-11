using System;
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
/// Async-job support for the single-shot (non-chunked) analysis types. Making Linguistic/Literary/
/// Summarization/Custom non-blocking requires RunAsync to (a) stamp the dispatched jobId on the persisted
/// row so GetAnalysisByJobId can find it and (b) drive the progress tracker to Succeeded so the FE poll
/// terminates - exactly what the chunked Proofread/LineEdit paths already do. These tests pin both, plus the
/// sync path (jobId == null) which must NOT stamp a job id or touch progress.
/// </summary>
public class AsyncSingleShotJobTests
{
    private const string LinguisticJson =
        "{\"syntaxMetrics\":{\"sentenceCount\":2},\"morphologyMetrics\":{\"wordCount\":9}," +
        "\"styleMetrics\":{\"formality\":\"literary\"},\"grammaticalityScore\":0.9,\"summary\":\"סיכום\"," +
        "\"deviations\":[],\"consistencyIssues\":[]}";

    private static UnifiedAnalysisService BuildService(
        AppDbContext db, AnalysisProgressTracker tracker, string llmContent, string inputText,
        Guid bookId, Guid chapterId, AnalysisType type)
    {
        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = llmContent, Provider = "test", Model = "test" });

        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, type, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = type,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        return new UnifiedAnalysisService(
            db, routerMock.Object, new PromptFactory(), new SfdtConversionService(),
            Options.Create(new AiOptions()), NullLogger<UnifiedAnalysisService>.Instance,
            tracker, contextMock.Object, new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance));
    }

    [Fact]
    public async Task RunAsync_SingleShotWithJobId_StampsJobId_MarksSucceeded_AndIsFindableByJob()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var inputText = "שלום עולם. זהו טקסט לבדיקה שמכיל כמה מילים בעברית פשוטה.";

        var tracker = new AnalysisProgressTracker();
        // Mirror the controller: it StartJob's before dispatching the background RunAsync. SetStatus no-ops on
        // an untracked job, so without this the transitions would be silently dropped.
        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.LinguisticAnalysis, bookId, chapterId, null, "queued");

        var svc = BuildService(db, tracker, LinguisticJson, inputText, bookId, chapterId, AnalysisType.LinguisticAnalysis);

        var result = await svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.LinguisticAnalysis, chapterId,
            customPrompt: null, language: "he", jobId: jobId, ct: CancellationToken.None);

        // (a) jobId stamped on the persisted row.
        Assert.Equal(jobId, result.JobId);

        // (b) progress driven to Succeeded so the FE poll terminates.
        Assert.True(tracker.TryGet(jobId, out var snap));
        Assert.Equal(AnalysisProgressStatus.Succeeded, snap!.Status);

        // The row is findable by jobId (mirrors GetAnalysisByJobId's query).
        var byJob = await db.AnalysisResults.AsNoTracking()
            .FirstOrDefaultAsync(a => a.JobId == jobId && a.ChapterId == chapterId && a.BookId == bookId);
        Assert.NotNull(byJob);
    }

    [Fact]
    public async Task RunAsync_SingleShotWithoutJobId_LeavesJobIdNull()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var inputText = "שלום עולם. זהו טקסט לבדיקה שמכיל כמה מילים בעברית פשוטה.";

        var tracker = new AnalysisProgressTracker();
        var svc = BuildService(db, tracker, LinguisticJson, inputText, bookId, chapterId, AnalysisType.LinguisticAnalysis);

        // Synchronous /analyze path: jobId == null. The row must not get a spurious job id.
        var result = await svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.LinguisticAnalysis, chapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        Assert.Null(result.JobId);
    }
}
