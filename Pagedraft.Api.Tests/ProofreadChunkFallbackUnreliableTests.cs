using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using Harness = Pagedraft.Api.Tests.BookProofreadServiceTests.Harness;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-c01 (review 2026-10-13, P1): on the chunked Proofread path a chunk that FALLS BACK keeps its source text, and
/// the flag used to come from dropped content only, so a chapter whose chunks timed out (c19 made every twice-timed-
/// out chunk a FallbackError) persisted Succeeded, ResultText == source, 0 suggestions, NOT flagged: the whole-book
/// run counted it proofread, the retry-failed record never listed it, and the export did not skip it. The rule is
/// now <see cref="UnifiedAnalysisService.ChunkedProofreadIsUnreliable"/>: any fallback chunk flags the result.
/// Also pins the merge fail-safe's run-log note. No live model: the fake router IS the proof.
/// </summary>
public class ProofreadChunkFallbackUnreliableTests
{
    private const string SlowMarker = "SLOWCHUNK";
    private const string HttpClientTimeout =
        "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.";

    /// <summary>Three paragraphs of 40 words; the middle one carries the marker the fake router fails on.</summary>
    private static string ThreeParagraphs()
    {
        var plain = string.Join(" ", Enumerable.Repeat("שלום", 40));
        var slow = SlowMarker + " " + string.Join(" ", Enumerable.Repeat("עולם", 39));
        return plain + "\n\n" + slow + "\n\n" + plain;
    }

    private static string Unwrap(string input)
    {
        const string start = "[TEXT_TO_CORRECT]";
        const string end = "[/TEXT_TO_CORRECT]";
        var s = input.IndexOf(start, StringComparison.Ordinal);
        if (s < 0) return input;
        s += start.Length;
        var e = input.IndexOf(end, s, StringComparison.Ordinal);
        return e > s ? input[s..e] : input;
    }

    private static AiResponse Ok(string content) => new() { Content = content, Provider = "test", Model = "test" };

    /// <summary>A router that echoes every chunk, except the marked one, which gets <paramref name="slow"/>.</summary>
    private static IAiRouter Router(Func<string, Task<AiResponse>> slow)
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest req, CancellationToken _) =>
            {
                var text = Unwrap(req.InputText ?? "");
                return text.Contains(SlowMarker) ? slow(text) : Task.FromResult(Ok(text));
            });
        return router.Object;
    }

    private static UnifiedAnalysisService Service(AppDbContext db, IAiRouter router) => new(
        db,
        router,
        new PromptFactory(),
        new SfdtConversionService(),
        Options.Create(new AiOptions()),
        NullLogger<UnifiedAnalysisService>.Instance,
        new AnalysisProgressTracker(),
        new Mock<IAnalysisContextService>().Object,
        new SuggestionDiffService(),
        new KtivMaleChecker(new HebrewStyleOptions()),
        new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
        new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
        new StubBookEntityProvider());

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>The private chunked runner, by reflection and by POSITION (see the note on the method).</summary>
    private static Task<AnalysisResult> RunChunked(UnifiedAnalysisService svc, string inputText)
    {
        var m = typeof(UnifiedAnalysisService).GetMethod("RunProofreadChunkedAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(m);
        var context = new AnalysisContext { TargetText = inputText, Scope = AnalysisScope.Chapter, AnalysisType = AnalysisType.Proofread };
        return (Task<AnalysisResult>)m!.Invoke(svc, new object?[]
        {
            inputText, null, null, null, AnalysisScope.Chapter, Guid.NewGuid(), null, "he",
            40, // chunkTargetWords: one chunk per paragraph
            1,  // maxParallel
            AiTier.Thinking,
            Guid.NewGuid(),
            context,
            CancellationToken.None
        })!;
    }

    private static async Task<(AnalysisRunLog Log, List<AnalysisChunkOutcome> Details)> RunLogOf(AppDbContext db, AnalysisResult result)
    {
        var log = await db.AnalysisRunLogs.SingleAsync(r => r.AnalysisResultId == result.Id);
        var details = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            log.ChunkDetailsJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return (log, details);
    }

    // ── The rule, through the real chunked runner ───────────────────────────────────────────────────────

    [Fact]
    public async Task AChunkTimingOutTwice_FlagsTheChunkedResultUnreliable()
    {
        await using var db = NewDb();
        var slowCalls = 0;
        var router = Router(_ =>
        {
            Interlocked.Increment(ref slowCalls);
            // Exactly what HttpClient throws on its own Timeout: the caller's token is NOT cancelled.
            throw new TaskCanceledException(HttpClientTimeout);
        });

        var result = await RunChunked(Service(db, router), ThreeParagraphs());

        Assert.Equal(2, slowCalls); // first attempt + exactly one retry
        var (log, details) = await RunLogOf(db, result);
        Assert.Equal(3, log.TotalChunks); // precondition: one chunk per paragraph
        Assert.Equal(1, log.FallbackChunks);
        Assert.Equal("FallbackError", Assert.Single(details, d => d.Outcome != "Succeeded").Outcome);
        // The defect: the slow chunk kept its source, so the merge equals the source and nothing was dropped.
        Assert.Contains(SlowMarker, result.ResultText);
        Assert.True(result.ProofreadResultUnreliable,
            "a chunked Proofread whose chunk fell back (FallbackError after a timeout and its retry) must be flagged unreliable");
        Assert.True((await db.AnalysisResults.SingleAsync(r => r.Id == result.Id)).ProofreadResultUnreliable);
    }

    [Fact]
    public async Task AnEmptyChunk_FlagsTheChunkedResultUnreliable()
    {
        await using var db = NewDb();
        var router = Router(_ => Task.FromResult(Ok("")));

        var result = await RunChunked(Service(db, router), ThreeParagraphs());

        var (log, details) = await RunLogOf(db, result);
        Assert.Equal(1, log.FallbackChunks);
        Assert.Equal("FallbackEmpty", Assert.Single(details, d => d.Outcome != "Succeeded").Outcome);
        Assert.True(result.ProofreadResultUnreliable);
    }

    [Fact]
    public async Task ACleanChunkedRun_IsNotFlagged_AndWritesNoRunNote()
    {
        await using var db = NewDb();
        var router = Router(text => Task.FromResult(Ok(text)));

        var result = await RunChunked(Service(db, router), ThreeParagraphs());

        var (log, details) = await RunLogOf(db, result);
        Assert.Equal(3, log.TotalChunks);
        Assert.Equal(0, log.FallbackChunks);
        Assert.False(result.ProofreadResultUnreliable);
        Assert.DoesNotContain(details, d => d.ChunkIndex == UnifiedAnalysisService.RunNoteChunkIndex);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    [InlineData(3, false, true)]
    [InlineData(0, true, true)]
    public void TheRule_AnyFallbackChunkOrDroppedContent(int fallbackChunks, bool dropped, bool expected)
        => Assert.Equal(expected, UnifiedAnalysisService.ChunkedProofreadIsUnreliable(fallbackChunks, dropped));

    [Fact]
    public void CountFallbackChunks_CountsEveryNonSucceededChunk_ButNotARunNote()
    {
        var outcomes = new[]
        {
            new AnalysisChunkOutcome { ChunkIndex = 0, Outcome = "Succeeded" },
            new AnalysisChunkOutcome { ChunkIndex = 1, Outcome = "FallbackError" },
            new AnalysisChunkOutcome { ChunkIndex = 2, Outcome = "FallbackEmpty" },
            new AnalysisChunkOutcome { ChunkIndex = 3, Outcome = "FallbackUnrelated" },
            new AnalysisChunkOutcome { ChunkIndex = 4, Outcome = "FallbackRepetition" },
            UnifiedAnalysisService.MergeFailSafeNote(alignedToSource: false, chunkCount: 5)!,
        };

        Assert.Equal(4, UnifiedAnalysisService.CountFallbackChunks(outcomes));
    }

    // ── The merge fail-safe's run-log note ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheMergeFailSafe_WritesAStructuredRunLogNote_ThatIsNotAChunk()
    {
        await using var db = NewDb();
        var svc = Service(db, Router(text => Task.FromResult(Ok(text))));
        // The fail-safe is unreachable from the real chunker, so the seam forces the result it would return.
        svc.ProofreadMergeResultForTest = m => new ProofreadChunkMerge.Result(m.Text, AlignedToSource: false);

        var result = await RunChunked(svc, ThreeParagraphs());

        var (log, details) = await RunLogOf(db, result);
        var note = Assert.Single(details, d => d.ChunkIndex == UnifiedAnalysisService.RunNoteChunkIndex);
        Assert.Equal(UnifiedAnalysisService.MergeFallbackOutcome, note.Outcome);
        Assert.StartsWith("merge fail-safe: the 3 chunks do not align with the source text", note.Note);
        Assert.Same(note, details[0]); // first, before the chunks
        // Not a chunk: the counts are over the three real chunks only, and the note does not flag the result.
        Assert.Equal(3, log.TotalChunks);
        Assert.Equal(3, log.SucceededChunks);
        Assert.Equal(0, log.FallbackChunks);
        Assert.Equal(4, details.Count);
        Assert.False(result.ProofreadResultUnreliable);
    }

    [Fact]
    public void MergeFailSafeNote_IsNullWhenTheMergeAligned()
        => Assert.Null(UnifiedAnalysisService.MergeFailSafeNote(alignedToSource: true, chunkCount: 3));

    // ── The whole-book run: the flagged chapter is a FAILED chapter ─────────────────────────────────────

    /// <summary>A chapter of three paragraphs, each exactly one chunk at the shipped Hebrew target; the middle one marked.</summary>
    private static Guid AddChunkedChapter(Harness h, int order, string title)
    {
        var target = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(new AiOptions(), "he", AiTier.Fast);
        var plain = string.Join(" ", Enumerable.Repeat("שלום", target));
        var slow = SlowMarker + " " + string.Join(" ", Enumerable.Repeat("עולם", target - 1));
        var text = plain + "\n\n" + slow + "\n\n" + plain;
        var id = Guid.NewGuid();
        using (var db = h.Db())
        {
            db.Chapters.Add(new Chapter
            {
                Id = id, BookId = h.BookId, Order = order, Title = title, ContentText = text,
                WordCount = target * 3,
                CreatedAt = BookProofreadServiceTests.Edited, UpdatedAt = BookProofreadServiceTests.Edited
            });
            db.SaveChanges();
        }
        h.Targets[id] = (text, id, null);
        return id;
    }

    [Fact]
    public async Task WholeBook_AChapterWhoseChunkTimedOut_IsAFailedChapter_AndOnlyFailedPlansIt()
    {
        using var h = new Harness();
        var chapter = AddChunkedChapter(h, 0, "ארוך");
        h.OnModelCall = input => { if (input.Contains(SlowMarker)) throw new TaskCanceledException(HttpClientTimeout); };
        var svc = h.Service();

        var run = await svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None);
        Assert.True(run.Started);
        await svc.RunTask!;

        var persisted = Assert.Single(h.ActiveProofreads());
        Assert.Equal("chunked", persisted.ModelName); // precondition: the chapter went through the chunked path
        Assert.True(persisted.ProofreadResultUnreliable);
        Assert.Equal(0, h.Snapshot(run.JobId).CompletedChunks); // the run did not count it proofread

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        Assert.Equal(chapter, Assert.Single(status.FailedChapters).ChapterId);
        Assert.Equal(chapter, Assert.Single(status.UnreliableChapters).ChapterId);

        // onlyFailed plans exactly that chapter (a registered run makes the start answer with its plan only)...
        Assert.True(h.Registry.TryStart(h.BookId, "he", Guid.NewGuid(), out _));
        var plan = await h.Service().StartFailedAsync(h.BookId, "he", CancellationToken.None);
        Assert.Equal(1, plan.PlannedChapters);
        Assert.Equal(status.FailedEstimatedChunks, plan.EstimatedChunks);
    }

    [Fact]
    public async Task WholeBook_TheRetryOfTheTimedOutChapter_ClearsIt_OnceTheProviderAnswers()
    {
        using var h = new Harness();
        var chapter = AddChunkedChapter(h, 0, "ארוך");
        h.OnModelCall = input => { if (input.Contains(SlowMarker)) throw new TaskCanceledException(HttpClientTimeout); };
        var svc = h.Service();
        await svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None);
        await svc.RunTask!;
        Assert.Equal(chapter, Assert.Single((await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None)).FailedChapters).ChapterId);

        h.OnModelCall = null;
        var retrySvc = h.Service();
        var retry = await retrySvc.StartFailedAsync(h.BookId, "he", CancellationToken.None);
        Assert.True(retry.Started);
        await retrySvc.RunTask!;

        Assert.Equal(1, h.Snapshot(retry.JobId).CompletedChunks);
        Assert.False(Assert.Single(h.ActiveProofreads()).ProofreadResultUnreliable);
        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        Assert.Empty(status.FailedChapters);
        Assert.Empty(status.UnreliableChapters);
    }
}
