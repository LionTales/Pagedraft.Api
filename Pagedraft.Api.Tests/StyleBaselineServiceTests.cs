using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for the heavy "fill coverage" book-wide style baseline builder (a3): idempotency, status math,
/// graceful per-chapter failure, and the persisted round-trip of the cached average.
/// </summary>
public class StyleBaselineServiceTests
{
    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string Lang = "he";

    // A valid LinguisticAnalysisResult-shaped JSON the mock returns for a chapter build, parameterised
    // on averageSentenceLength so different chapters can carry distinct metrics.
    private static string MetricsJson(double avgSentenceLength) => $$"""
        {
          "syntaxMetrics": { "sentenceCount": 6, "averageSentenceLength": {{avgSentenceLength}} },
          "morphologyMetrics": { "wordCount": 100, "uniqueWords": 70, "averageWordLength": 4.3, "lexicalDensity": 0.6 },
          "styleMetrics": { "formality": "literary", "readability": 0.75, "voiceBalance": "active" },
          "grammaticalityScore": 0.9,
          "summary": "ok",
          "deviations": [],
          "consistencyIssues": []
        }
        """;

    // ─── 1. Idempotency: second build with everything fresh is a no-op, no LLM call ──────────────

    [Fact]
    public async Task BuildBookStyleBaselineAsync_AllFresh_SecondBuildIsNoOp_NoLlmCall()
    {
        using var provider = BuildServiceProvider(out var routerMock, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Idempotency Book", Language = Lang });
        for (var i = 0; i < 3; i++)
            db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = i, Title = $"Ch{i}", ContentText = $"תוכן פרק {i} לניתוח לשוני." });
        await db.SaveChangesAsync();

        // First build: each chapter is missing → 3 LLM builds, baseline persisted.
        var first = await svc.BuildBookStyleBaselineAsync(bookId, Lang);
        Assert.True(first.Ready);
        Assert.False(first.NoOp);
        Assert.Equal(3, first.BuiltChapters);
        Assert.Equal(0, first.FailedChapters);

        var callsAfterFirst = routerMock.Invocations.Count(i => i.Method.Name == nameof(IAiRouter.CompleteAsync));
        Assert.Equal(3, callsAfterFirst);

        // Cached average persisted.
        Assert.Equal(1, await db.BookStyleBaselines.CountAsync());

        // Second build with everything fresh → idempotent no-op, NO further LLM calls.
        var second = await svc.BuildBookStyleBaselineAsync(bookId, Lang);
        Assert.True(second.Ready);
        Assert.True(second.NoOp);

        var callsAfterSecond = routerMock.Invocations.Count(i => i.Method.Name == nameof(IAiRouter.CompleteAsync));
        Assert.Equal(callsAfterFirst, callsAfterSecond); // unchanged
        Assert.Equal(1, await db.BookStyleBaselines.CountAsync()); // no duplicate
    }

    // ─── 2. Status math: mixed fresh / stale / missing chapters ─────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_MixedFreshStaleMissing_ComputesCorrectCounts()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        var freshChapterId = Guid.NewGuid();
        var staleChapterId = Guid.NewGuid();
        var missingChapterId = Guid.NewGuid();

        // Establish deterministic freshness via SAVE ORDERING (the SaveChanges override stamps both
        // Added and Modified rows with UtcNow; tiny delays guarantee distinct, monotonically increasing
        // timestamps so the profile.UpdatedAt >= chapter.UpdatedAt predicate is exercised precisely).

        // 1. Stale + missing chapters first, plus the STALE chapter's profile - so the profile is OLDER
        //    than the chapter will be after we touch it below.
        db.Books.Add(new Book { Id = bookId, Title = "Status Math Book", Language = Lang });
        db.Chapters.Add(new Chapter { Id = staleChapterId, BookId = bookId, Order = 1, Title = "Stale", ContentText = "תוכן." });
        db.Chapters.Add(new Chapter { Id = missingChapterId, BookId = bookId, Order = 2, Title = "Missing", ContentText = "תוכן." });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = staleChapterId, Language = Lang, MetricsJson = MetricsJson(12.0) });
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 2. The FRESH chapter (newer than the stale profile, but its own profile will be newer still).
        db.Chapters.Add(new Chapter { Id = freshChapterId, BookId = bookId, Order = 0, Title = "Fresh", ContentText = "תוכן." });
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 3. Touch the STALE chapter so its UpdatedAt jumps AFTER its (step-1) profile → stale.
        var staleChapter = await db.Chapters.SingleAsync(c => c.Id == staleChapterId);
        staleChapter.Title = "Stale (edited)";
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 4. The FRESH chapter's profile last, so it is NEWER than the fresh chapter → fresh. Stamped with
        //    the active model ("test-model" per the configured FeatureModel) so it is ALSO model-fresh; a
        //    null here would now count as model-stale under the DEF-1 freshness gate.
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = freshChapterId, Language = Lang, MetricsJson = MetricsJson(12.0), BuiltWithModel = "test-model" });
        await db.SaveChangesAsync();

        var status = await svc.GetStatusAsync(bookId, Lang);

        Assert.Equal(3, status.TotalChapters);
        Assert.Equal(1, status.BuiltChapters);          // only the fresh chapter
        Assert.Equal(2, status.StaleCount);             // stale + missing
        Assert.False(status.HasBaseline);               // no cached average yet
        Assert.False(status.IsReady);                   // staleCount > 0
        Assert.Null(status.LastUpdatedAt);
    }

    // ─── 3. Graceful per-chapter failure: one bad chapter does not abort the job ─────────────────

    [Fact]
    public async Task BuildBookStyleBaselineAsync_OneChapterFails_OthersStillBuild_StatusReflectsFailure()
    {
        // The mock fails only for the chapter whose text contains "BAD_CHAPTER"; all others succeed.
        using var provider = BuildServiceProvider(
            out var routerMock,
            defaultMetrics: MetricsJson(12.0),
            failWhenInputContains: "BAD_CHAPTER");

        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        var badChapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Partial Failure Book", Language = Lang });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "Good A", ContentText = "תוכן תקין של פרק א." });
        db.Chapters.Add(new Chapter { Id = badChapterId, BookId = bookId, Order = 1, Title = "Bad", ContentText = "BAD_CHAPTER content that makes the model throw." });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 2, Title = "Good C", ContentText = "תוכן תקין של פרק ג." });
        await db.SaveChangesAsync();

        var result = await svc.BuildBookStyleBaselineAsync(bookId, Lang);

        // Job did NOT abort: two good chapters built, the bad one failed and was skipped.
        Assert.Equal(3, result.TotalChapters);
        Assert.Equal(2, result.BuiltChapters);
        Assert.Equal(1, result.FailedChapters);

        // Two good profiles persisted, none for the bad chapter.
        Assert.Equal(2, await db.ChapterStyleProfiles.CountAsync());
        Assert.False(await db.ChapterStyleProfiles.AnyAsync(p => p.ChapterId == badChapterId));

        // Average over the two good chapters was built and persisted.
        Assert.True(result.Ready);
        Assert.Equal(1, await db.BookStyleBaselines.CountAsync());

        // Final status: one chapter remains stale (the failed one).
        var status = await svc.GetStatusAsync(bookId, Lang);
        Assert.Equal(2, status.BuiltChapters);
        Assert.Equal(1, status.StaleCount);
        Assert.True(status.HasBaseline);
    }

    // ─── 4. Persisted round-trip of the cached average (write then read) ─────────────────────────

    [Fact]
    public async Task BuildBookStyleBaselineAsync_PersistsAverage_RoundTripsThroughDb()
    {
        // Two chapters with distinct averageSentenceLength (10 and 20) → expected mean = 15.
        using var provider = BuildServiceProvider(
            out _,
            metricsByInputMarker: new Dictionary<string, string>
            {
                ["CH_TEN"] = MetricsJson(10.0),
                ["CH_TWENTY"] = MetricsJson(20.0)
            });

        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Round Trip Book", Language = Lang });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "Ten", ContentText = "CH_TEN chapter content." });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "Twenty", ContentText = "CH_TWENTY chapter content." });
        await db.SaveChangesAsync();

        var result = await svc.BuildBookStyleBaselineAsync(bookId, Lang);
        Assert.True(result.Ready);
        Assert.Equal(2, result.BuiltChapters);

        // Read the persisted row back via a clean query (round-trip through the DB).
        var persisted = await db.BookStyleBaselines.AsNoTracking()
            .SingleAsync(b => b.BookId == bookId && b.Language == Lang);

        Assert.Equal(2, persisted.BuiltChapterCount);
        Assert.False(string.IsNullOrWhiteSpace(persisted.MetricsJson));
        // BuiltWithModel records the LinguisticAnalysis model id (DefaultModel here, no FeatureModels set).
        Assert.False(string.IsNullOrWhiteSpace(persisted.BuiltWithModel));
        Assert.True(persisted.UpdatedAt > DateTimeOffset.MinValue);

        // The cached average is the per-metric mean: (10 + 20) / 2 = 15.
        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(persisted.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);
        Assert.Equal(15.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5);

        // Status reflects the persisted baseline.
        var status = await svc.GetStatusAsync(bookId, Lang);
        Assert.True(status.HasBaseline);
        Assert.Equal(persisted.BuiltWithModel, status.BuiltWithModel);
        Assert.NotNull(status.LastUpdatedAt);
        Assert.True(status.IsReady);
    }

    // ─── 4b. Fewer than two usable chapters → not ready, no baseline persisted ───────────────────

    [Fact]
    public async Task BuildBookStyleBaselineAsync_SingleChapter_NoBaselinePersisted_NotReady()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Single Chapter Book", Language = Lang });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "Only", ContentText = "תוכן יחיד." });
        await db.SaveChangesAsync();

        var result = await svc.BuildBookStyleBaselineAsync(bookId, Lang);

        // The single chapter profile builds, but a one-chapter "average" is not meaningful → no baseline.
        Assert.False(result.Ready);
        Assert.Equal(0, await db.BookStyleBaselines.CountAsync());
        Assert.Equal(1, await db.ChapterStyleProfiles.CountAsync());
    }

    // ─── DEF-1: GetStatusAsync counts a model-mismatched chapter as stale + surfaces cross-model signals ──
    // The active LinguisticAnalysis model in these tests is "test-model" (configured FeatureModel). A
    // chapter profile built under a DIFFERENT model must be counted stale even when timestamp-fresh, and
    // a cached baseline built under a different model must set builtWithDifferentModel=true with activeModel
    // populated.

    [Fact]
    public async Task GetStatusAsync_ModelMismatchedChapter_CountedStale_AndCrossModelSignalsSet()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        var matchChapterId = Guid.NewGuid();
        var mismatchChapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Cross-Model Status Book", Language = Lang });
        db.Chapters.Add(new Chapter { Id = matchChapterId, BookId = bookId, Order = 0, Title = "Match", ContentText = "תוכן." });
        db.Chapters.Add(new Chapter { Id = mismatchChapterId, BookId = bookId, Order = 1, Title = "Mismatch", ContentText = "תוכן." });
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // Both profiles built AFTER their chapters → timestamp-fresh. One under the active model, one under
        // a different model, so only the model gate separates them.
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = matchChapterId, Language = Lang, MetricsJson = MetricsJson(12.0), BuiltWithModel = "test-model" });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = mismatchChapterId, Language = Lang, MetricsJson = MetricsJson(12.0), BuiltWithModel = "different-model" });
        // A cached book baseline built under a different model → builtWithDifferentModel must be true.
        db.BookStyleBaselines.Add(new BookStyleBaseline { Id = Guid.NewGuid(), BookId = bookId, Language = Lang, MetricsJson = MetricsJson(12.0), BuiltChapterCount = 2, BuiltWithModel = "different-model" });
        await db.SaveChangesAsync();

        var status = await svc.GetStatusAsync(bookId, Lang);

        // The model-mismatched chapter is counted stale; only the model-matching chapter is built/fresh.
        Assert.Equal(2, status.TotalChapters);
        Assert.Equal(1, status.BuiltChapters);
        Assert.Equal(1, status.StaleCount);
        Assert.Equal(1, status.ChaptersToBuild);

        // Cross-model signals: activeModel populated, baseline flagged as built with a different model.
        Assert.Equal("test-model", status.ActiveModel);
        Assert.True(status.HasBaseline);
        Assert.True(status.BuiltWithDifferentModel);
    }

    [Fact]
    public async Task GetStatusAsync_BaselineBuiltWithActiveModel_BuiltWithDifferentModelFalse()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Same-Model Baseline Book", Language = Lang });
        db.BookStyleBaselines.Add(new BookStyleBaseline { Id = Guid.NewGuid(), BookId = bookId, Language = Lang, MetricsJson = MetricsJson(12.0), BuiltChapterCount = 2, BuiltWithModel = "test-model" });
        await db.SaveChangesAsync();

        var status = await svc.GetStatusAsync(bookId, Lang);

        Assert.True(status.HasBaseline);
        Assert.Equal("test-model", status.ActiveModel);
        Assert.False(status.BuiltWithDifferentModel);
    }

    // ─── Bug 1: a cross-model cached baseline is NOT "up to date", even when every chapter is fresh ───
    // StaleCount measures only chapter-profile freshness; the persisted BookStyleBaseline carries its OWN
    // model. When every chapter profile matches the active model but the cached average was built under a
    // different model (BuiltWithDifferentModel), a build must NOT no-op - it must recompute + restamp the
    // average under the active model, else the cross-model average persists forever.

    [Fact]
    public async Task BuildBookStyleBaselineAsync_ChaptersFresh_BaselineCrossModel_RebuildsAndRestamps()
    {
        using var provider = BuildServiceProvider(out var routerMock, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Cross-Model Rebuild Book", Language = Lang });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן א." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "B", ContentText = "תוכן ב." });
        await db.SaveChangesAsync();

        await Task.Delay(10); // profiles saved AFTER chapters → timestamp-fresh

        // Both chapter profiles are timestamp-fresh AND built under the ACTIVE model ("test-model").
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chA, Language = Lang, MetricsJson = MetricsJson(10.0), BuiltWithModel = "test-model" });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chB, Language = Lang, MetricsJson = MetricsJson(20.0), BuiltWithModel = "test-model" });
        // The cached average was built under a DIFFERENT model → out of date despite the fresh chapters.
        db.BookStyleBaselines.Add(new BookStyleBaseline { Id = Guid.NewGuid(), BookId = bookId, Language = Lang, MetricsJson = MetricsJson(99.0), BuiltChapterCount = 2, BuiltWithModel = "different-model" });
        await db.SaveChangesAsync();

        // Pre-condition: chapters all fresh (StaleCount 0) but the baseline is flagged cross-model.
        var pre = await svc.GetStatusAsync(bookId, Lang);
        Assert.Equal(0, pre.StaleCount);
        Assert.True(pre.HasBaseline);
        Assert.True(pre.BuiltWithDifferentModel);

        var callsBefore = routerMock.Invocations.Count(i => i.Method.Name == nameof(IAiRouter.CompleteAsync));

        var result = await svc.BuildBookStyleBaselineAsync(bookId, Lang);

        // NOT a no-op: the cross-model baseline is recomputed even though no chapter needed a rebuild.
        Assert.False(result.NoOp);
        Assert.True(result.Ready);

        // The fresh same-model profiles were aggregated WITHOUT any LLM call (idempotent chapter step).
        var callsAfter = routerMock.Invocations.Count(i => i.Method.Name == nameof(IAiRouter.CompleteAsync));
        Assert.Equal(callsBefore, callsAfter);

        // The persisted average is recomputed (mean of 10 & 20 = 15, NOT the stale 99) and restamped
        // under the active model, clearing the cross-model signal.
        var baseline = await db.BookStyleBaselines.AsNoTracking().SingleAsync(b => b.BookId == bookId && b.Language == Lang);
        Assert.Equal("test-model", baseline.BuiltWithModel);
        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(baseline.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);
        Assert.Equal(15.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5);

        var post = await svc.GetStatusAsync(bookId, Lang);
        Assert.False(post.BuiltWithDifferentModel);
    }

    // Bug 1 (controller surface): the POST build fast path must also honour the cross-model signal, or it
    // returns NoOp:true with no jobId and the stale cross-model average is never recomputed.

    [Fact]
    public async Task BuildStyleBaseline_ChaptersFresh_BaselineCrossModel_DoesNotNoOp()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();
        var registry = provider.GetRequiredService<StyleBaselineBuildRegistry>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Controller Cross-Model Book", Language = Lang });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן א." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "B", ContentText = "תוכן ב." });
        await db.SaveChangesAsync();
        await Task.Delay(10);
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chA, Language = Lang, MetricsJson = MetricsJson(10.0), BuiltWithModel = "test-model" });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chB, Language = Lang, MetricsJson = MetricsJson(20.0), BuiltWithModel = "test-model" });
        db.BookStyleBaselines.Add(new BookStyleBaseline { Id = Guid.NewGuid(), BookId = bookId, Language = Lang, MetricsJson = MetricsJson(99.0), BuiltChapterCount = 2, BuiltWithModel = "different-model" });
        await db.SaveChangesAsync();

        // Cross-model + every chapter fresh: the BUGGY gate (StaleCount==0 && HasBaseline) would no-op here.
        var pre = await svc.GetStatusAsync(bookId, Lang);
        Assert.Equal(0, pre.StaleCount);
        Assert.True(pre.BuiltWithDifferentModel);

        // Register an already-running build so the controller, once PAST the no-op gate, takes the
        // DETERMINISTIC dedup branch (returns the existing jobId, no background Task.Run) instead of
        // spawning a real build. With the bug the no-op gate fires FIRST and returns NoOp:true / null jobId.
        var existingJobId = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, Lang, existingJobId));
        progress.StartJob(existingJobId, AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null);
        progress.SetStatus(existingJobId, AnalysisProgressStatus.Running, "running");

        var controller = new BooksController(
            db: db,
            bookIntelligence: null!,
            styleBaseline: svc,
            bookSummary: null!,
            bookReview: null!,
            chapterBrief: null!,
            progress: progress,
            aiTierStatus: null!,
            scopeFactory: scopeFactory,
            appLifetime: new TestApplicationLifetime(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<BooksController>.Instance);

        var action = await controller.BuildStyleBaseline(bookId, new BuildStyleBaselineRequest(Lang), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<StartStyleBaselineBuildResponse>(ok.Value);

        // The no-op fast path did NOT fire: the request proceeded to the build path (here, dedup).
        Assert.False(response.NoOp);
        Assert.Equal(existingJobId, response.JobId);
    }

    // ─── Bug 2: an explicit locale request language is normalized to the SAME cache key as the book ───
    // Profiles/baselines are persisted under the normalized key ("en"). A request that passes an explicit
    // locale ("en-US") must resolve to "en" too, or it queries an empty "en-US" slot and understates
    // coverage (and a build would target the wrong slot).

    [Fact]
    public async Task GetStyleBaselineStatus_ExplicitLocaleLanguage_NormalizedToCacheKey()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        // Book language is the locale form; chapter profiles are keyed under the normalized "en".
        db.Books.Add(new Book { Id = bookId, Title = "Locale Book", Language = "en-US" });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "English chapter content one." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "B", ContentText = "English chapter content two." });
        await db.SaveChangesAsync();
        await Task.Delay(10);
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chA, Language = "en", MetricsJson = MetricsJson(10.0), BuiltWithModel = "test-model" });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chB, Language = "en", MetricsJson = MetricsJson(20.0), BuiltWithModel = "test-model" });
        await db.SaveChangesAsync();

        var controller = new BooksController(
            db: db,
            bookIntelligence: null!,
            styleBaseline: svc,
            bookSummary: null!,
            bookReview: null!,
            chapterBrief: null!,
            progress: progress,
            aiTierStatus: null!,
            scopeFactory: scopeFactory,
            appLifetime: new TestApplicationLifetime(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<BooksController>.Instance);

        // Explicit locale query language: must normalize to "en" so coverage reflects the "en" profiles.
        var action = await controller.GetStyleBaselineStatus(bookId, language: "en-US", ct: CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<BookStyleBaselineStatusDto>(ok.Value);

        Assert.Equal("en", dto.Language);
        Assert.Equal(2, dto.TotalChapters);
        Assert.Equal(2, dto.BuiltChapters); // would be 0 if "en-US" missed the "en"-keyed profiles
        Assert.Equal(0, dto.StaleCount);
    }

    // ─── Bug 1: inline LinguisticAnalysis and the builder/status endpoints key the baseline cache under
    // the SAME normalized language. A book/request language of "en-US" must resolve to the "en" slot, or
    // an inline-built profile is invisible to status and coverage looks missing.

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_LocaleLanguage_PersistsUnderNormalizedKey_VisibleToStatus()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var contextService = provider.GetRequiredService<IAnalysisContextService>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Locale Book", Language = "en-US" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Order = 0, Title = "A", ContentText = "English chapter content for analysis." });
        await db.SaveChangesAsync();

        // The inline LinguisticAnalysis path builds the profile from the RAW locale "en-US"...
        var profile = await contextService.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, "en-US");
        Assert.NotNull(profile);
        // ...but it must persist under the canonical "en" cache key.
        Assert.Equal("en", profile!.Language);

        // And the status endpoint (which the controller calls with the normalized "en") must see that exact
        // profile as built coverage, not a missing "en-US" slot.
        var status = await svc.GetStatusAsync(bookId, "en");
        Assert.Equal(1, status.TotalChapters);
        Assert.Equal(1, status.BuiltChapters);
        Assert.Equal(0, status.StaleCount);
    }

    [Fact]
    public async Task BuildBookStyleAverageProfileAsync_LocaleLanguage_AggregatesNormalizedKeyProfiles()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var contextService = provider.GetRequiredService<IAnalysisContextService>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Locale Average Book", Language = "en-US" });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "English content A." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "B", ContentText = "English content B." });
        await db.SaveChangesAsync();
        await Task.Delay(10);
        // Profiles persisted under the canonical "en" key (as the builder would write them).
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chA, Language = "en", MetricsJson = MetricsJson(10.0), BuiltWithModel = "test-model" });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chB, Language = "en", MetricsJson = MetricsJson(20.0), BuiltWithModel = "test-model" });
        await db.SaveChangesAsync();

        // The inline Chapter-scope path asks for the average with the RAW locale "en-US"; it must aggregate
        // the "en"-keyed profiles (mean of 10 & 20 = 15), not return null for an empty "en-US" slot.
        var avg = await contextService.BuildBookStyleAverageProfileAsync(bookId, "en-US");
        Assert.NotNull(avg);
        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(avg!.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);
        Assert.Equal(15.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5);
    }

    // ─── Bug 2: IsReady requires a usable cached average built under the active model, not just
    // StaleCount == 0. A fresh-chapters book whose cached average is cross-model must report NOT ready.

    [Fact]
    public async Task GetStatusAsync_ChaptersFresh_BaselineCrossModel_IsReadyFalse()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Cross-Model Ready Book", Language = Lang });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן א." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "B", ContentText = "תוכן ב." });
        await db.SaveChangesAsync();
        await Task.Delay(10);
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chA, Language = Lang, MetricsJson = MetricsJson(10.0), BuiltWithModel = "test-model" });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = chB, Language = Lang, MetricsJson = MetricsJson(20.0), BuiltWithModel = "test-model" });
        // Cached average built under a DIFFERENT model than the active "test-model".
        db.BookStyleBaselines.Add(new BookStyleBaseline { Id = Guid.NewGuid(), BookId = bookId, Language = Lang, MetricsJson = MetricsJson(99.0), BuiltChapterCount = 2, BuiltWithModel = "different-model" });
        await db.SaveChangesAsync();

        var status = await svc.GetStatusAsync(bookId, Lang);

        // Every chapter profile is fresh (StaleCount 0) and a baseline exists, but it is cross-model, so a
        // rebuild is still required → IsReady must be FALSE (it was wrongly true when keyed only off StaleCount).
        Assert.Equal(0, status.StaleCount);
        Assert.True(status.HasBaseline);
        Assert.True(status.BuiltWithDifferentModel);
        Assert.False(status.IsReady);
    }

    // ─── DEF-2: GetStatusAsync surfaces the active build jobId while one is registered ──────────────

    [Fact]
    public async Task GetStatusAsync_ReturnsActiveBuildJobId_WhileRegistered_NullAfterComplete()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();
        var registry = provider.GetRequiredService<StyleBaselineBuildRegistry>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Active Build Book", Language = Lang });
        await db.SaveChangesAsync();

        // No build registered → null.
        var before = await svc.GetStatusAsync(bookId, Lang);
        Assert.Null(before.ActiveBuildJobId);

        // Register an in-progress build (as the background job would) AND start its progress entry as
        // Running (DEF-2 now cross-checks the tracker before advertising the jobId) → status surfaces it.
        var jobId = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, Lang, jobId));
        progress.StartJob(jobId, AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null);

        var during = await svc.GetStatusAsync(bookId, Lang);
        Assert.Equal(jobId, during.ActiveBuildJobId);

        // Complete → null again.
        registry.Complete(bookId, Lang);
        var after = await svc.GetStatusAsync(bookId, Lang);
        Assert.Null(after.ActiveBuildJobId);
    }

    // ─── DEF-2 hardening: a lingering registry entry for a TERMINAL job is not advertised + self-heals ──

    [Fact]
    public async Task GetStatusAsync_RegistryHoldsTerminalJob_ReturnsNullJobId_AndClearsRegistry()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();
        var registry = provider.GetRequiredService<StyleBaselineBuildRegistry>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Terminal Build Book", Language = Lang });
        await db.SaveChangesAsync();

        // A build that already SUCCEEDED in the tracker, but whose registry entry lingered (e.g. a crash
        // before the finally-block cleared it). The status must NOT advertise it as in-progress.
        var jobId = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, Lang, jobId));
        progress.StartJob(jobId, AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null);
        progress.SetStatus(jobId, AnalysisProgressStatus.Succeeded, "done");

        var status = await svc.GetStatusAsync(bookId, Lang);
        Assert.Null(status.ActiveBuildJobId);

        // Self-heal: the lingering registry entry was cleared so it never resurfaces.
        Assert.Null(registry.TryGetActive(bookId, Lang));
    }

    [Fact]
    public async Task GetStatusAsync_RegistryHoldsRunningJob_ReturnsJobId()
    {
        using var provider = BuildServiceProvider(out _, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();
        var registry = provider.GetRequiredService<StyleBaselineBuildRegistry>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Running Build Book", Language = Lang });
        await db.SaveChangesAsync();

        // A genuinely in-progress build: registry entry + a RUNNING tracker entry → advertised.
        var jobId = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, Lang, jobId));
        progress.StartJob(jobId, AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null);
        progress.SetStatus(jobId, AnalysisProgressStatus.Running, "running");

        var status = await svc.GetStatusAsync(bookId, Lang);
        Assert.Equal(jobId, status.ActiveBuildJobId);

        // Registry entry is untouched while the build runs.
        Assert.Equal(jobId, registry.TryGetActive(bookId, Lang));
    }

    // ─── be-c02: controller dedup — a build while ActiveBuildJobId is set returns the SAME jobId ──────
    // The documented "one active build per (bookId, language)" invariant: when GetStatusAsync surfaces an
    // already-running build (registry + a non-terminal tracker entry), BuildStyleBaseline must hand back
    // THAT jobId instead of minting a new one / starting a second background build.

    [Fact]
    public async Task BuildStyleBaseline_ActiveBuildInProgress_ReturnsExistingJobId_NoSecondBuild()
    {
        using var provider = BuildServiceProvider(out var routerMock, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();
        var registry = provider.GetRequiredService<StyleBaselineBuildRegistry>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Seed a book with chapters but NO profiles → StaleCount > 0, so this is NOT the no-op fast path.
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Dedup Book", Language = Lang });
        for (var i = 0; i < 3; i++)
            db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = i, Title = $"Ch{i}", ContentText = $"תוכן פרק {i}." });
        await db.SaveChangesAsync();

        // Sanity: status reports stale chapters and no active build yet.
        var pre = await svc.GetStatusAsync(bookId, Lang);
        Assert.Equal(3, pre.StaleCount);
        Assert.Null(pre.ActiveBuildJobId);

        // Register an in-progress build (registry + a RUNNING tracker entry) so GetStatusAsync surfaces it.
        var existingJobId = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, Lang, existingJobId));
        progress.StartJob(existingJobId, AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null);
        progress.SetStatus(existingJobId, AnalysisProgressStatus.Running, "running");

        var during = await svc.GetStatusAsync(bookId, Lang);
        Assert.Equal(existingJobId, during.ActiveBuildJobId);

        var controller = new BooksController(
            db: db,
            bookIntelligence: null!,
            styleBaseline: svc,
            bookSummary: null!,
            bookReview: null!,
            chapterBrief: null!,
            progress: progress,
            aiTierStatus: null!,
            scopeFactory: scopeFactory,
            appLifetime: new TestApplicationLifetime(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<BooksController>.Instance);

        var callsBefore = routerMock.Invocations.Count(i => i.Method.Name == nameof(IAiRouter.CompleteAsync));

        var action = await controller.BuildStyleBaseline(bookId, new BuildStyleBaselineRequest(Lang), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<StartStyleBaselineBuildResponse>(ok.Value);

        // The dedup guard hands back the EXISTING jobId — no new build minted.
        Assert.Equal(existingJobId, response.JobId);
        Assert.False(response.NoOp);
        Assert.False(response.Ready);

        // No second background build started: the active registry slot is unchanged (still the original
        // jobId) and no further LLM calls were issued by this BuildStyleBaseline invocation.
        Assert.Equal(existingJobId, registry.TryGetActive(bookId, Lang));
        var callsAfter = routerMock.Invocations.Count(i => i.Method.Name == nameof(IAiRouter.CompleteAsync));
        Assert.Equal(callsBefore, callsAfter);
        // Still exactly one progress job for this book (no new jobId registered in the tracker).
        Assert.False(progress.TryGet(response.JobId!.Value, out var snap) && snap == null);
        Assert.True(progress.TryGet(existingJobId, out _));
    }

    // ─── be-c03: the bail branch drives the LOSING jobId to a TERMINAL (Canceled) status ─────────────
    // Regression guard for be-c02: when the async build loses the TryStart race to a concurrent build
    // already holding the (bookId, language) slot, BuildBookStyleBaselineAsync bails WITHOUT running
    // RunBuildAsync (no duplicate paid LLM calls) — but it must first drive the bailed jobId to a
    // terminal status, otherwise the losing tab polls a jobId stuck in Pending/Running forever. The
    // FE's pollStyleBaselineBuild treats Canceled like succeeded/failed (stops polling, reattaches to
    // the winner), so Canceled cleanly hands the losing tab over to the winning build.

    [Fact]
    public async Task BuildBookStyleBaselineAsync_LosesRegistryRace_BailsAndCancelsLoserJobId_WinnerUntouched()
    {
        using var provider = BuildServiceProvider(out var routerMock, defaultMetrics: MetricsJson(12.0));
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();
        var registry = provider.GetRequiredService<StyleBaselineBuildRegistry>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();

        // Seed a book with chapters but NO profiles → StaleCount > 0, so this is NOT the no-op fast path
        // (the bail branch is only reachable on the real-build path).
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Race Loser Book", Language = Lang });
        for (var i = 0; i < 3; i++)
            db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = i, Title = $"Ch{i}", ContentText = $"תוכן פרק {i}." });
        await db.SaveChangesAsync();

        // The WINNER already holds the (bookId, language) slot in the registry AND a RUNNING tracker entry.
        // The RUNNING tracker entry matters: GetStatusAsync (called inside BuildBookStyleBaselineAsync)
        // self-heals away any registry slot whose tracker job is terminal/absent, which would otherwise
        // free the slot and let the loser win the TryStart race — so the winner must look genuinely live.
        var winnerJobId = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, Lang, winnerJobId));
        progress.StartJob(winnerJobId, AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null);
        progress.SetStatus(winnerJobId, AnalysisProgressStatus.Running, "running");

        // The LOSER's jobId is StartJob'd (as the controller + service would) and left non-terminal.
        var loserJobId = Guid.NewGuid();
        progress.StartJob(loserJobId, AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null,
            "Starting style baseline build…");
        progress.SetStatus(loserJobId, AnalysisProgressStatus.Running, "running");

        var callsBefore = routerMock.Invocations.Count(i => i.Method.Name == nameof(IAiRouter.CompleteAsync));

        // The loser calls the build; TryStart loses the race → bail branch.
        var result = await svc.BuildBookStyleBaselineAsync(bookId, Lang, loserJobId, CancellationToken.None);

        // (a) Bail result: NoOp true and RunBuildAsync did NOT run (no LLM calls, no profiles built).
        Assert.True(result.NoOp);
        var callsAfter = routerMock.Invocations.Count(i => i.Method.Name == nameof(IAiRouter.CompleteAsync));
        Assert.Equal(callsBefore, callsAfter);
        Assert.Equal(0, await db.ChapterStyleProfiles.CountAsync());
        Assert.Equal(0, await db.BookStyleBaselines.CountAsync());

        // (b) The loser jobId is now terminal (Canceled) so the FE progress poll resolves.
        Assert.True(progress.TryGet(loserJobId, out var snap));
        Assert.NotNull(snap);
        Assert.Equal(AnalysisProgressStatus.Canceled, snap!.Status);

        // The winner's registry slot is untouched by the bail (the active build still owns cleanup).
        Assert.Equal(winnerJobId, registry.TryGetActive(bookId, Lang));
    }

    /// <summary>Minimal IHostApplicationLifetime stub: ApplicationStopping never fires (build allowed).</summary>
    private sealed class TestApplicationLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    // ─── Helper: DI provider with a SHARED-name in-memory DB so per-chapter DI scopes see seed data ──

    private static ServiceProvider BuildServiceProvider(
        out Mock<IAiRouter> routerMock,
        string? defaultMetrics = null,
        string? failWhenInputContains = null,
        Dictionary<string, string>? metricsByInputMarker = null)
        => BuildServiceProvider(out routerMock, out _, defaultMetrics, failWhenInputContains, metricsByInputMarker);

    private static ServiceProvider BuildServiceProvider(
        out Mock<IAiRouter> routerMock,
        out string dbName,
        string? defaultMetrics = null,
        string? failWhenInputContains = null,
        Dictionary<string, string>? metricsByInputMarker = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Fixed DB name so the root context AND every per-chapter scope created by IServiceScopeFactory
        // resolve the SAME in-memory store (StyleBaselineService creates a fresh scope per chapter).
        dbName = "StyleBaselineTests_" + Guid.NewGuid();
        var name = dbName;
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(name));

        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest req, CancellationToken _) =>
            {
                var input = req.InputText ?? "";

                if (!string.IsNullOrEmpty(failWhenInputContains) && input.Contains(failWhenInputContains, StringComparison.Ordinal))
                    throw new InvalidOperationException("Simulated per-chapter LLM failure");

                if (metricsByInputMarker != null)
                {
                    foreach (var kvp in metricsByInputMarker)
                    {
                        if (input.Contains(kvp.Key, StringComparison.Ordinal))
                            return Task.FromResult(new AiResponse { Content = kvp.Value, Model = "test-model", Provider = "test-provider" });
                    }
                }

                return Task.FromResult(new AiResponse
                {
                    Content = defaultMetrics ?? MetricsJson(12.0),
                    Model = "test-model",
                    Provider = "test-provider"
                });
            });

        services.AddSingleton(routerMock.Object);
        // Configure the LinguisticAnalysis FeatureModel so the config-resolved active model equals the
        // model the mock router reports ("test-model"). In production the provider sets AiResponse.Model
        // from the SAME resolved selection AiRouter computes, so config-resolved == router-reported; this
        // mirrors that invariant so freshly-built profiles (stamped with the router-reported model) pass
        // the DEF-1 model-freshness gate in GetStatusAsync (which compares against the config-resolved
        // active model). Without this the mock's "test-model" would mismatch the default "qwen2.5:14b".
        services.Configure<AiOptions>(o =>
        {
            o.FeatureModels = new Dictionary<string, FeatureModelOptions>
            {
                ["LinguisticAnalysis"] = new FeatureModelOptions { Provider = "test-provider", Model = "test-model" }
            };
        });
        // AnalysisContextService now depends on the whole-book context assembler graph (wb1-c03); register
        // it so IAnalysisContextService resolves. Not exercised by the StyleBaseline tests.
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<StyleBaselineService>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<StyleBaselineBuildRegistry>();

        return services.BuildServiceProvider();
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// a4: Unit tests for the build estimate pure math (StyleBaselineService.ComputeEstimate)
// These call the internal static method directly — no DB, no LLM mock needed.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

public class StyleBaselineEstimateTests
{
    // ─── 1. 0 chapters → 0 seconds, null USD (local) ──────────────────────────────────────────

    [Fact]
    public void ComputeEstimate_ZeroChapters_ZeroSecondsNullUsd()
    {
        var (seconds, usd) = StyleBaselineService.ComputeEstimate(
            chaptersToBuild: 0,
            providerName: "Ollama",
            maxParallel: 2);

        Assert.Equal(0, seconds);
        Assert.Null(usd);
    }

    // ─── 2. N chapters, local provider → expected seconds, null USD ───────────────────────────
    // With maxParallel=2, N=4 chapters: ceil(4/2)=2 waves * ApproxSecondsPerChapter=45 = 90s.

    [Fact]
    public void ComputeEstimate_FourChapters_LocalProvider_CorrectSecondsNullUsd()
    {
        const int n = 4;
        const int maxParallel = 2;
        int expectedSeconds = (int)Math.Ceiling((double)n / maxParallel) * StyleBaselineService.ApproxSecondsPerChapter;
        // 2 waves * 45 = 90

        var (seconds, usd) = StyleBaselineService.ComputeEstimate(
            chaptersToBuild: n,
            providerName: "Ollama",
            maxParallel: maxParallel);

        Assert.Equal(expectedSeconds, seconds);
        Assert.Equal(90, seconds); // explicit sanity check for constant value
        Assert.Null(usd);
    }

    // ─── 3. Odd number of chapters, local provider → ceil rounding ────────────────────────────
    // With maxParallel=2, N=5 chapters: ceil(5/2)=3 waves * 45 = 135s.

    [Fact]
    public void ComputeEstimate_FiveChapters_MaxParallelTwo_CeilRoundingApplied()
    {
        var (seconds, usd) = StyleBaselineService.ComputeEstimate(
            chaptersToBuild: 5,
            providerName: "Ollama",
            maxParallel: 2);

        Assert.Equal(135, seconds); // ceil(5/2)=3 waves * 45
        Assert.Null(usd);
    }

    // ─── 4. Paid provider (OpenRouter) → estimatedUsd != null, correct figure ─────────────────
    // N=2 chapters: 2 * 3000 / 1000 * 0.0015 = 2 * 3 * 0.0015 = 0.009.

    [Fact]
    public void ComputeEstimate_PaidProvider_OpenRouter_ReturnsExpectedUsd()
    {
        const int n = 2;
        // expected = n * ApproxTokensPerChapter / 1000 * ratePerKToken
        // = 2 * 3000 / 1000 * 0.0015 = 0.009
        const decimal expectedUsd = 2 * StyleBaselineService.ApproxTokensPerChapter / 1000m * 0.0015m;

        var (seconds, usd) = StyleBaselineService.ComputeEstimate(
            chaptersToBuild: n,
            providerName: "OpenRouter",
            maxParallel: 2);

        Assert.NotNull(usd);
        Assert.Equal(expectedUsd, usd!.Value);
        Assert.Equal(0.009m, usd.Value); // explicit sanity check
        // seconds: ceil(2/2)=1 wave * 45 = 45
        Assert.Equal(45, seconds);
    }

    // ─── 5. Unknown provider → null USD (safe default: treat as free) ─────────────────────────

    [Fact]
    public void ComputeEstimate_UnknownProvider_NullUsd()
    {
        var (seconds, usd) = StyleBaselineService.ComputeEstimate(
            chaptersToBuild: 3,
            providerName: "SomeUnknownProvider",
            maxParallel: 1);

        Assert.Null(usd);
        // seconds: ceil(3/1)=3 waves * 45 = 135
        Assert.Equal(135, seconds);
    }

    // ─── 6. maxParallel=1 → serial estimate ───────────────────────────────────────────────────
    // N=3 chapters, serial: ceil(3/1)=3 waves * 45 = 135s.

    [Fact]
    public void ComputeEstimate_SerialParallelism_ThreeChapters_CorrectSeconds()
    {
        var (seconds, usd) = StyleBaselineService.ComputeEstimate(
            chaptersToBuild: 3,
            providerName: "Ollama",
            maxParallel: 1);

        Assert.Equal(135, seconds);
        Assert.Null(usd);
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// ResolveLinguisticProviderAndModel — half-configured FeatureModels guard (NIT fix)
// Verifies the method applies the SAME both-non-empty predicate as AiRouter.ResolveSelection
// (line 104-105): a half-configured entry (only Provider set, or only Model set) must fall back to
// DefaultProvider/DefaultModel, not produce a mixed/half override.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

public class StyleBaselineResolveLinguisticTests
{
    private const string TaskKey = "LinguisticAnalysis"; // AiTaskType.LinguisticAnalysis.ToString()
    private const string DefaultProvider = "Ollama";
    private const string DefaultModel = "default-model";
    private const string FeatureProvider = "OpenRouter";
    private const string FeatureModel = "feature-model";

    // Builds a minimal StyleBaselineService wired to the given AiOptions (no DB, no LLM needed).
    private static StyleBaselineService BuildService(AiOptions aiOpt)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase("ResolveTests_" + Guid.NewGuid()));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<Mock<IAiRouter>>().AddSingleton(sp => sp.GetRequiredService<Mock<IAiRouter>>().Object);
        services.Configure<AiOptions>(o =>
        {
            o.DefaultProvider = aiOpt.DefaultProvider;
            o.DefaultModel = aiOpt.DefaultModel;
            o.FeatureModels = aiOpt.FeatureModels;
        });
        // AnalysisContextService now depends on the whole-book context assembler graph (wb1-c03).
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<StyleBaselineService>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<StyleBaselineBuildRegistry>();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<StyleBaselineService>();
    }

    // ─── 1. Fully-configured entry → override applied ─────────────────────────────────────────

    [Fact]
    public void ResolveLinguistic_BothSet_UsesFeatureOverride()
    {
        var opt = new AiOptions
        {
            DefaultProvider = DefaultProvider,
            DefaultModel = DefaultModel,
            FeatureModels = new Dictionary<string, FeatureModelOptions>
            {
                [TaskKey] = new FeatureModelOptions { Provider = FeatureProvider, Model = FeatureModel }
            }
        };

        var svc = BuildService(opt);
        var (provider, model) = svc.ResolveLinguisticProviderAndModel();

        Assert.Equal(FeatureProvider, provider);
        Assert.Equal(FeatureModel, model);
    }

    // ─── 2. Only Provider set (Model empty) → fall back to defaults ───────────────────────────

    [Fact]
    public void ResolveLinguistic_OnlyProviderSet_ModelEmpty_FallsBackToDefaults()
    {
        var opt = new AiOptions
        {
            DefaultProvider = DefaultProvider,
            DefaultModel = DefaultModel,
            FeatureModels = new Dictionary<string, FeatureModelOptions>
            {
                [TaskKey] = new FeatureModelOptions { Provider = FeatureProvider, Model = "" }
            }
        };

        var svc = BuildService(opt);
        var (provider, model) = svc.ResolveLinguisticProviderAndModel();

        Assert.Equal(DefaultProvider, provider);
        Assert.Equal(DefaultModel, model);
    }

    // ─── 3. Only Model set (Provider empty) → fall back to defaults ───────────────────────────

    [Fact]
    public void ResolveLinguistic_OnlyModelSet_ProviderEmpty_FallsBackToDefaults()
    {
        var opt = new AiOptions
        {
            DefaultProvider = DefaultProvider,
            DefaultModel = DefaultModel,
            FeatureModels = new Dictionary<string, FeatureModelOptions>
            {
                [TaskKey] = new FeatureModelOptions { Provider = "", Model = FeatureModel }
            }
        };

        var svc = BuildService(opt);
        var (provider, model) = svc.ResolveLinguisticProviderAndModel();

        Assert.Equal(DefaultProvider, provider);
        Assert.Equal(DefaultModel, model);
    }

    // ─── 4. No FeatureModels configured at all → defaults ─────────────────────────────────────

    [Fact]
    public void ResolveLinguistic_NoFeatureModels_UsesDefaults()
    {
        var opt = new AiOptions
        {
            DefaultProvider = DefaultProvider,
            DefaultModel = DefaultModel,
            FeatureModels = null
        };

        var svc = BuildService(opt);
        var (provider, model) = svc.ResolveLinguisticProviderAndModel();

        Assert.Equal(DefaultProvider, provider);
        Assert.Equal(DefaultModel, model);
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// DEF-2: StyleBaselineBuildRegistry — TryStart / TryGetActive / Complete behaviour (unit, no DI).
// ─────────────────────────────────────────────────────────────────────────────────────────────────

public class StyleBaselineBuildRegistryTests
{
    private const string Lang = "he";

    [Fact]
    public void TryGetActive_NoBuild_ReturnsNull()
    {
        var registry = new StyleBaselineBuildRegistry();
        Assert.Null(registry.TryGetActive(Guid.NewGuid(), Lang));
    }

    [Fact]
    public void TryStart_ThenTryGetActive_ReturnsJobId()
    {
        var registry = new StyleBaselineBuildRegistry();
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        Assert.True(registry.TryStart(bookId, Lang, jobId));
        Assert.Equal(jobId, registry.TryGetActive(bookId, Lang));
    }

    [Fact]
    public void TryStart_SecondStartForSameKey_FailsWhileFirstActive()
    {
        var registry = new StyleBaselineBuildRegistry();
        var bookId = Guid.NewGuid();
        var firstJob = Guid.NewGuid();
        var secondJob = Guid.NewGuid();

        Assert.True(registry.TryStart(bookId, Lang, firstJob));
        // A second build for the same (bookId, language) is rejected while the first is active.
        Assert.False(registry.TryStart(bookId, Lang, secondJob));
        // The original jobId is still the one surfaced.
        Assert.Equal(firstJob, registry.TryGetActive(bookId, Lang));
    }

    [Fact]
    public void Complete_ClearsActive_AllowsRestart()
    {
        var registry = new StyleBaselineBuildRegistry();
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        Assert.True(registry.TryStart(bookId, Lang, jobId));
        registry.Complete(bookId, Lang);
        Assert.Null(registry.TryGetActive(bookId, Lang));

        // After completion a new build can register.
        var newJob = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, Lang, newJob));
        Assert.Equal(newJob, registry.TryGetActive(bookId, Lang));
    }

    [Fact]
    public void Complete_WhenNoneActive_IsNoOp()
    {
        var registry = new StyleBaselineBuildRegistry();
        // Should not throw.
        registry.Complete(Guid.NewGuid(), Lang);
    }

    [Fact]
    public void DifferentLanguages_AreIndependentKeys()
    {
        var registry = new StyleBaselineBuildRegistry();
        var bookId = Guid.NewGuid();
        var heJob = Guid.NewGuid();
        var enJob = Guid.NewGuid();

        Assert.True(registry.TryStart(bookId, "he", heJob));
        Assert.True(registry.TryStart(bookId, "en", enJob));

        Assert.Equal(heJob, registry.TryGetActive(bookId, "he"));
        Assert.Equal(enJob, registry.TryGetActive(bookId, "en"));

        registry.Complete(bookId, "he");
        Assert.Null(registry.TryGetActive(bookId, "he"));
        Assert.Equal(enJob, registry.TryGetActive(bookId, "en")); // unaffected
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// be-f03: BooksController.GetStyleBaselineProgress — job-type guard
// Exercises the guard added in be-f03: a jobId whose snapshot is not Book/LinguisticAnalysis must
// return 404, while a Book-scope LinguisticAnalysis job for the correct book returns 200.
// Construction note: GetStyleBaselineProgress only calls _progress; all other constructor params
// are passed as null (they are never dereferenced by this action path).
// ─────────────────────────────────────────────────────────────────────────────────────────────────

public class StyleBaselineProgressJobTypeGuardTests
{
    private static BooksController BuildController(AnalysisProgressTracker tracker)
    {
        return new BooksController(
            db: null!,
            bookIntelligence: null!,
            styleBaseline: null!,
            bookSummary: null!,
            bookReview: null!,
            chapterBrief: null!,
            progress: tracker,
            aiTierStatus: null!,
            scopeFactory: null!,
            appLifetime: null!,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<BooksController>.Instance);
    }

    // A book-scope LinguisticAnalysis job for the matching bookId → 200 Ok.
    [Fact]
    public void GetStyleBaselineProgress_BookScopeLinguisticJob_SameBook_ReturnsOk()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        tracker.StartJob(jobId, AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null);

        var controller = BuildController(tracker);
        var result = controller.GetStyleBaselineProgress(bookId, jobId);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // A chapter-scope Proofread job (same bookId) → 404.
    [Fact]
    public void GetStyleBaselineProgress_ChapterScopeProofreadJob_SameBook_Returns404()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, chapterId, null);

        var controller = BuildController(tracker);
        var result = controller.GetStyleBaselineProgress(bookId, jobId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // A book-scope job that is NOT LinguisticAnalysis (e.g. Proofread at book scope) → 404.
    [Fact]
    public void GetStyleBaselineProgress_BookScopeNonLinguisticJob_Returns404()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        tracker.StartJob(jobId, AnalysisScope.Book, AnalysisType.Proofread, bookId, null, null);

        var controller = BuildController(tracker);
        var result = controller.GetStyleBaselineProgress(bookId, jobId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // A LinguisticAnalysis job at non-Book scope → 404.
    [Fact]
    public void GetStyleBaselineProgress_ChapterScopeLinguisticJob_Returns404()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.LinguisticAnalysis, bookId, chapterId, null);

        var controller = BuildController(tracker);
        var result = controller.GetStyleBaselineProgress(bookId, jobId);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
