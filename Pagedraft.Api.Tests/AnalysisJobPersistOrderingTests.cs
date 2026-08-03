using System;
using System.Linq;
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
/// PERSIST, THEN SIGNAL (be-c01). The FE polls <c>analysis-progress</c> and, the instant it observes
/// <see cref="AnalysisProgressStatus.Succeeded"/>, GETs <c>analysis-jobs/{jobId}</c> —
/// <c>AnalysisController.GetAnalysisByJobId</c>, which looks the row up by (ChapterId, BookId, JobId) on its
/// OWN request-scoped DbContext. Both chunked paths used to flip the status BEFORE saving, with
/// <c>ArchivePreviousActiveAsync</c> and the LLM-backed <c>ApplyAnalysisRepairAsync</c> in between — a window
/// seconds wide, in which that GET 404s and a run that actually succeeded is reported to the user as failed.
///
/// These are ORDERING assertions, not timing ones: a tracker subclass runs AT the Succeeded transition and
/// asks a SECOND DbContext (the observer's, like the controller's) whether the row is already there. Nothing
/// sleeps, nothing races; reverting the ordering fails the assertion deterministically.
/// </summary>
public class AnalysisJobPersistOrderingTests
{
    /// <summary>
    /// An observer that runs at the exact moment a job is marked Succeeded — the FE's position. Everything
    /// else delegates to the real tracker, so the run behaves normally.
    /// </summary>
    private sealed class SucceededObserverTracker : AnalysisProgressTracker
    {
        private readonly Action<Guid> _onSucceeded;

        public SucceededObserverTracker(Action<Guid> onSucceeded) => _onSucceeded = onSucceeded;

        public override void SetStatus(Guid jobId, AnalysisProgressStatus status, string? message = null)
        {
            base.SetStatus(jobId, status, message);
            if (status == AnalysisProgressStatus.Succeeded)
                _onSucceeded(jobId);
        }
    }

    private sealed record ProbeOutcome(
        bool? RowVisibleAtSucceeded,
        int ChunkRequestCount,
        Guid? StampedJobId,
        AnalysisProgressStatus FinalStatus);

    /// <summary>600 Hebrew words: comfortably over the Hebrew chunk target, so RunAsync routes to the CHUNKED path.</summary>
    private static string LongHebrewText() =>
        string.Join(" ", Enumerable.Range(0, 600).Select(i => $"מילה{i % 40}"));

    private static async Task<ProbeOutcome> RunAndProbeAsync(
        AnalysisType type, string? llmContent, string? inputTextOverride = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var inputText = inputTextOverride ?? LongHebrewText();

        db.Books.Add(new Book { Id = bookId, Title = "T", Language = "he" });
        await db.SaveChangesAsync();

        bool? rowVisibleAtSucceeded = null;
        var tracker = new SucceededObserverTracker(_ =>
        {
            // A SECOND DbContext over the same store — exactly the position GetAnalysisByJobId is in (its own
            // request scope). An added-but-unsaved row is invisible here, just as an uncommitted row is
            // invisible to the controller's query.
            using var observer = new AppDbContext(options);
            rowVisibleAtSucceeded = observer.AnalysisResults
                .Any(a => a.ChapterId == chapterId && a.BookId == bookId && a.JobId == jobId);
        });

        var captured = 0;
        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AiRequest, CancellationToken>((_, _) => Interlocked.Increment(ref captured))
            .ReturnsAsync((AiRequest req, CancellationToken _) => new AiResponse
            {
                // Proofread merges the model output back into the chapter text, so echo the chunk; the JSON
                // tasks get their structured payload.
                Content = llmContent ?? req.InputText,
                Provider = "test",
                Model = "test"
            });

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

        var svc = new UnifiedAnalysisService(
            db, routerMock.Object, new PromptFactory(), new SfdtConversionService(),
            Options.Create(new AiOptions()), NullLogger<UnifiedAnalysisService>.Instance,
            tracker, contextMock.Object, new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        // Mirror the controller: it StartJob's before dispatching the background RunAsync.
        tracker.StartJob(jobId, AnalysisScope.Chapter, type, bookId, chapterId, null, "queued");

        var result = await svc.RunAsync(
            AnalysisScope.Chapter, type, chapterId,
            customPrompt: null, language: "he", jobId: jobId, ct: CancellationToken.None);

        tracker.TryGet(jobId, out var snap);
        return new ProbeOutcome(
            rowVisibleAtSucceeded,
            captured,
            result.JobId,
            snap?.Status ?? AnalysisProgressStatus.Pending);
    }

    private static void AssertPersistedBeforeSucceeded(ProbeOutcome outcome, string pathName)
    {
        Assert.True(outcome.RowVisibleAtSucceeded.HasValue,
            $"the {pathName} run never marked the job Succeeded, so the ordering was never observed. " +
            "The FE's progress poll would hang rather than 404.");

        Assert.True(outcome.RowVisibleAtSucceeded!.Value,
            $"ORDERING VIOLATION on the {pathName} path: the job was marked Succeeded while the " +
            "AnalysisResult row was still uncommitted, so GetAnalysisByJobId (which queries on its own " +
            "DbContext) returns 404 to the FE poller and a successful run is reported as failed. " +
            "Persist BEFORE signalling - call PersistThenMarkJobSucceededAsync.");

        Assert.Equal(AnalysisProgressStatus.Succeeded, outcome.FinalStatus);
    }

    private static void AssertReallyChunked(ProbeOutcome outcome, string pathName) =>
        Assert.True(outcome.ChunkRequestCount > 1,
            $"expected the CHUNKED {pathName} path (>1 model request) but only {outcome.ChunkRequestCount} " +
            "request(s) were made - this test is no longer exercising the chunked path at all.");

    [Fact]
    public async Task ChunkedProofread_PersistsTheRowBeforeMarkingTheJobSucceeded()
    {
        var outcome = await RunAndProbeAsync(AnalysisType.Proofread, llmContent: null);

        AssertReallyChunked(outcome, "Proofread");
        AssertPersistedBeforeSucceeded(outcome, "chunked Proofread");
        Assert.NotNull(outcome.StampedJobId);
    }

    [Fact]
    public async Task ChunkedLineEdit_PersistsTheRowBeforeMarkingTheJobSucceeded()
    {
        var outcome = await RunAndProbeAsync(AnalysisType.LineEdit, "{\"suggestions\":[]}");

        AssertReallyChunked(outcome, "LineEdit");
        AssertPersistedBeforeSucceeded(outcome, "chunked LineEdit");
        Assert.NotNull(outcome.StampedJobId);
    }

    /// <summary>
    /// The GENERIC (single-shot, non-chunked) async seam. It always had the ordering right, but it now shares
    /// the same helper as the two chunked paths, so pin it here too: the fold is what keeps a fourth path from
    /// re-splitting persist and signal, and a regression on any one caller is a regression on the invariant.
    /// </summary>
    [Fact]
    public async Task SingleShotAsyncJob_PersistsTheRowBeforeMarkingTheJobSucceeded()
    {
        var outcome = await RunAndProbeAsync(
            AnalysisType.LinguisticAnalysis,
            "{\"syntaxMetrics\":{\"sentenceCount\":2},\"morphologyMetrics\":{\"wordCount\":9}," +
            "\"styleMetrics\":{\"formality\":\"literary\"},\"grammaticalityScore\":0.9,\"summary\":\"סיכום\"," +
            "\"deviations\":[],\"consistencyIssues\":[]}",
            inputTextOverride: "שלום עולם. זהו טקסט קצר לבדיקה.");

        Assert.Equal(1, outcome.ChunkRequestCount); // single-shot: exactly one model call, no chunking
        AssertPersistedBeforeSucceeded(outcome, "single-shot");
        Assert.NotNull(outcome.StampedJobId);
    }
}
