using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
/// Tests for <see cref="BookSummaryService"/> (wb1-c02): aggregates the per-chapter L0 structured briefs
/// (<see cref="ChunkSummary.StructuredJson"/>, built by <see cref="ChapterBriefService"/>) into L1
/// <see cref="ChapterBrief"/>s and a single L2 <see cref="BookBrief"/>, caches the rollup in
/// <see cref="BookSummaryBaseline"/>, and exposes a consented async build job + status.
///
/// Covers: aggregate rollup (L0 → L1 → L2 composition incl. BookProfile/BookBible reuse), idempotency
/// (a second build with nothing changed does no rebuild work), status math (coverage + staleCount), and
/// per-chapter failure isolation (one bad chapter does not abort the job; Failed count reflects it).
/// Mirrors the <see cref="ChapterBriefServiceTests"/> conventions, including the fixed-name in-memory DB
/// trick so the book build's per-chapter child DI scopes share one store.
/// </summary>
public class BookSummaryServiceTests
{
    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Active Summarization model the freshness gate resolves to under empty AiOptions: FeatureModels is
    // unset so it falls back to AiOptions.DefaultModel. L0 briefs seeded/built with THIS model are
    // model-fresh; any other (or null) is model-stale (cross-model self-heal). Kept in sync with
    // AiOptions.DefaultModel (same constant the ChapterBriefServiceTests use).
    private const string ActiveModel = "qwen2.5:14b";

    // A complete L0 structured-brief JSON the mock LLM returns (camelCase, matches StructuredChunkSummaryData).
    private const string BriefAJson = """
        {
          "plotEvents": ["The hero leaves home"],
          "characterStates": [ { "name": "Dana", "state": "fleeing", "emotionalArc": "fear to resolve" } ],
          "thematicMarkers": ["isolation", "rebirth"],
          "toneNotes": "tense",
          "openThreads": ["who sent the letter?"]
        }
        """;

    private const string BriefBJson = """
        {
          "plotEvents": ["A storm hits"],
          "characterStates": [ { "name": "Eli", "state": "stranded", "emotionalArc": "denial to acceptance" } ],
          "thematicMarkers": ["nature", "rebirth"],
          "toneNotes": "ominous",
          "openThreads": ["will the bridge hold?"]
        }
        """;

    // ─── 1. Aggregate rollup: L0 → L1 → L2 produces the expected composition ─────────────────────

    [Fact]
    public async Task BuildBookSummaryAsync_AggregatesL0IntoL1AndL2_WithExpectedComposition()
    {
        // Two chapters; the mock returns DIFFERENT L0 briefs per chapter so the rollup is observably the
        // union (not a single chapter echoed). BookProfile + BookBible supply the L2 genre/synopsis/themes.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new()
        {
            ["CH_A"] = BriefAJson,
            ["CH_B"] = BriefBJson
        });
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Rollup Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "Departure", ContentText = "CH_A תוכן." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "The Storm", ContentText = "CH_B תוכן." });
        // BookProfile provides the L2 genre/synopsis (reused, not invented).
        db.BookProfiles.Add(new BookProfile
        {
            BookId = bookId, Genre = "Fantasy", SubGenre = "Epic", TargetAudience = "Adult",
            LiteratureLevel = 7, Synopsis = "A hero flees a doomed city.", Language = "he"
        });
        // BookBible provides curated themes (reused; the rollup augments these with L1 markers).
        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            ThemesJson = """[ { "name": "exile", "description": "leaving home", "significance": "major" } ]"""
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();
        var result = await svc.BuildBookSummaryAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.False(result.NoOp);
        Assert.Equal(2, result.TotalChapters);
        Assert.Equal(2, result.BuiltChapters);
        Assert.Equal(0, result.FailedChapters);

        // L1: composing the per-chapter briefs maps L0 fields + chapter Title/Order straight through.
        var l1 = await svc.ComposeChapterBriefsAsync(bookId, "he");
        Assert.Equal(2, l1.Count);
        Assert.Equal("Departure", l1[0].Title);
        Assert.Equal(0, l1[0].Order);
        Assert.Equal(new[] { "The hero leaves home" }, l1[0].PlotEvents);
        Assert.Equal("Dana", l1[0].CharacterStates[0].Name);
        Assert.Equal("The Storm", l1[1].Title);
        Assert.Equal(new[] { "A storm hits" }, l1[1].PlotEvents);

        // L2: the cached BookBrief reuses BookProfile genre/synopsis and rolls up themes (curated bible
        // theme FIRST, then the union of L1 markers, deduped — "rebirth" appears in both chapters once).
        var row = await db.BookSummaryBaselines.SingleAsync(b => b.BookId == bookId);
        Assert.Equal(ActiveModel, row.BuiltWithModel);
        Assert.Equal(2, row.BuiltChapterCount);

        var l2 = JsonSerializer.Deserialize<BookBrief>(row.BookBriefJson, DeserializeOpts);
        Assert.NotNull(l2);
        Assert.Equal("Fantasy", l2!.Genre);
        Assert.Equal("Epic", l2.SubGenre);
        Assert.Equal("Adult", l2.TargetAudience);
        Assert.Equal(7, l2.LiteratureLevel);
        Assert.Equal("A hero flees a doomed city.", l2.Synopsis);
        // Curated theme first, then deduped union of L1 markers.
        Assert.Equal(new[] { "exile", "isolation", "rebirth", "nature" }, l2.Themes);
    }

    [Fact]
    public async Task ComposeBookBriefAsync_NoBookProfileOrBible_RollsUpThemesFromChapterMarkersOnly()
    {
        // No BookProfile/BookBible at all → genre/synopsis null, themes come purely from L1 markers (deduped).
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new()
        {
            ["CH_A"] = BriefAJson,
            ["CH_B"] = BriefBJson
        });
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Bare Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "CH_A תוכן." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "B", ContentText = "CH_B תוכן." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();
        await svc.BuildBookSummaryAsync(bookId, "he");

        var row = await db.BookSummaryBaselines.SingleAsync(b => b.BookId == bookId);
        var l2 = JsonSerializer.Deserialize<BookBrief>(row.BookBriefJson, DeserializeOpts);
        Assert.NotNull(l2);
        Assert.Null(l2!.Genre);
        Assert.Null(l2.Synopsis);
        // Union of both chapters' markers, deduped, first-seen order.
        Assert.Equal(new[] { "isolation", "rebirth", "nature" }, l2.Themes);
    }

    // ─── 2. Idempotency: second build with nothing changed does no rebuild work ───────────────────

    [Fact]
    public async Task BuildBookSummaryAsync_SecondBuildUnchanged_IsNoOp_NoLlmCalls()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out var routerMock, briefByContent: new()
        {
            ["CH_A"] = BriefAJson,
            ["CH_B"] = BriefBJson
        });
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Idempotent Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "CH_A תוכן." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "B", ContentText = "CH_B תוכן." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();

        // First build: both L0 briefs are built (2 LLM calls), the rollup is cached.
        var first = await svc.BuildBookSummaryAsync(bookId, "he");
        Assert.True(first.Ready);
        Assert.False(first.NoOp);
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        var rowAfterFirst = await db.BookSummaryBaselines.SingleAsync(b => b.BookId == bookId);
        var updatedAtAfterFirst = rowAfterFirst.UpdatedAt;

        // Second build, nothing changed: no-op, NO further LLM calls, cached row untouched.
        var second = await svc.BuildBookSummaryAsync(bookId, "he");
        Assert.True(second.Ready);
        Assert.True(second.NoOp);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "A second unchanged build must not re-issue any L0 LLM calls");

        var rowAfterSecond = await db.BookSummaryBaselines.SingleAsync(b => b.BookId == bookId);
        Assert.Equal(updatedAtAfterFirst, rowAfterSecond.UpdatedAt); // no rewrite
        Assert.Equal(1, await db.BookSummaryBaselines.CountAsync());
    }

    // ─── 3. Status math: coverage (built/total) + staleCount are correct ──────────────────────────

    [Fact]
    public async Task GetStatusAsync_CoverageAndStaleCount_AreCorrect()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new());
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var freshCh = Guid.NewGuid();   // has a fresh L0 brief → built
        var staleCh = Guid.NewGuid();   // has a stale L0 brief → stale
        var missingCh = Guid.NewGuid(); // no L0 brief → stale

        db.Books.Add(new Book { Id = bookId, Title = "Status Book", Language = "he" });

        // Stale chapter + its brief FIRST so the brief is the OLDEST row.
        db.Chapters.Add(new Chapter { Id = staleCh, BookId = bookId, Order = 1, Title = "Stale", ContentText = "תוכן ישן." });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = staleCh, Language = "he",
            // wb1-r02: structured freshness reads StructuredBuiltAt (not CreatedAt). Stamp it = now at this
            // Add so the time-ordering this test builds (oldest brief first) is preserved on the new column.
            StructuredJson = BriefAJson, BuiltWithModel = ActiveModel, StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await Task.Delay(10);

        // Fresh + missing chapters next.
        db.Chapters.Add(new Chapter { Id = freshCh, BookId = bookId, Order = 0, Title = "Fresh", ContentText = "תוכן טרי." });
        db.Chapters.Add(new Chapter { Id = missingCh, BookId = bookId, Order = 2, Title = "Missing", ContentText = "תוכן חסר." });
        await db.SaveChangesAsync();
        await Task.Delay(10);

        // Touch the stale chapter so its UpdatedAt jumps AFTER its brief → timestamp-stale.
        var stale = await db.Chapters.SingleAsync(c => c.Id == staleCh);
        stale.Title = "Stale (edited)";
        await db.SaveChangesAsync();
        await Task.Delay(10);

        // Fresh chapter's brief LAST so it is newer than its chapter → timestamp-fresh.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = freshCh, Language = "he",
            // wb1-r02: stamp StructuredBuiltAt = now (after the chapter edits above) so this brief reads fresh.
            StructuredJson = BriefBJson, BuiltWithModel = ActiveModel, StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();
        var status = await svc.GetStatusAsync(bookId, "he");

        Assert.Equal(3, status.TotalChapters);
        Assert.Equal(1, status.BuiltChapters);  // only the fresh chapter
        Assert.Equal(2, status.StaleCount);      // stale + missing
        Assert.Equal(2, status.ChaptersToBuild); // mirrors StaleCount
        Assert.False(status.HasSummary);         // no rollup cached yet
        Assert.False(status.IsReady);            // stale > 0
        Assert.Equal(ActiveModel, status.ActiveModel);
        Assert.Null(status.ActiveBuildJobId);    // no build running
    }

    [Fact]
    public async Task GetStatusAsync_CrossModelCachedRollup_ReportsNotReadyAndWarns()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new());
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Cross-Model Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן." });
        await db.SaveChangesAsync();
        await Task.Delay(10);

        // Fresh L0 brief (model + timestamp fresh) → chapter counts as built.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chA, Language = "he",
            // wb1-r02: stamp StructuredBuiltAt = now (after the chapter Add above) so the brief is fresh.
            StructuredJson = BriefAJson, BuiltWithModel = ActiveModel, StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        // Cached rollup exists but was built under a DIFFERENT model → not ready, warns.
        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId, Language = "he",
            BookBriefJson = """{ "genre": "Old" }""",
            BuiltChapterCount = 1, BuiltWithModel = "old-model"
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();
        var status = await svc.GetStatusAsync(bookId, "he");

        Assert.Equal(0, status.StaleCount);
        Assert.True(status.HasSummary);
        Assert.True(status.BuiltWithDifferentModel);
        Assert.False(status.IsReady); // cross-model rollup is out of date
    }

    // ─── 3b. ComposeChapterBriefsAsync applies the SAME freshness gate as GetStatusAsync ──────────

    [Fact]
    public async Task ComposeChapterBriefsAsync_ExcludesStaleAndCrossModelBriefs_MatchingStatus()
    {
        // Bug: ComposeChapterBriefsAsync included ANY row that had StructuredJson for the language, skipping
        // the timestamp/model freshness gate GetStatusAsync uses. Once any chapter had structured data the
        // assembler took the dense structured path and could inject briefs that no longer matched the edited
        // chapter text. The compose must omit stale (built before the chapter's last edit) and cross-model
        // briefs, leaving exactly the briefs GetStatusAsync counts as "built".
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new());
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var freshCh = Guid.NewGuid();       // fresh by both timestamp and model → composed
        var staleCh = Guid.NewGuid();       // brief built BEFORE the chapter's last edit → timestamp-stale
        var crossModelCh = Guid.NewGuid();  // brief built under a DIFFERENT model → model-stale

        db.Books.Add(new Book { Id = bookId, Title = "Compose Freshness", Language = "he" });

        // staleCh first: brief stamped now, then the chapter is edited AFTER so its UpdatedAt jumps past
        // the brief's StructuredBuiltAt → timestamp-stale.
        db.Chapters.Add(new Chapter { Id = staleCh, BookId = bookId, Order = 0, Title = "Stale", ContentText = "ישן." });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = staleCh, Language = "he",
            StructuredJson = BriefAJson, BuiltWithModel = ActiveModel, StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await Task.Delay(10);

        var staleChapter = await db.Chapters.SingleAsync(c => c.Id == staleCh);
        staleChapter.Title = "Stale (edited)";
        await db.SaveChangesAsync();

        // crossModelCh: fresh by timestamp but built under a different Summarization model → model-stale.
        db.Chapters.Add(new Chapter { Id = crossModelCh, BookId = bookId, Order = 1, Title = "CrossModel", ContentText = "תוכן." });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = crossModelCh, Language = "he",
            StructuredJson = BriefBJson, BuiltWithModel = "some-other-model",
            StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        // freshCh: fresh by both timestamp and model → the only composed brief.
        db.Chapters.Add(new Chapter { Id = freshCh, BookId = bookId, Order = 2, Title = "Fresh", ContentText = "תוכן טרי." });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = freshCh, Language = "he",
            StructuredJson = BriefAJson, BuiltWithModel = ActiveModel,
            StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();

        var l1 = await svc.ComposeChapterBriefsAsync(bookId, "he");
        Assert.Single(l1);
        Assert.Equal("Fresh", l1[0].Title);
        Assert.Equal(2, l1[0].Order);

        // Coverage and the composed L1 set agree (the drift the freshness gate exists to prevent).
        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.Equal(1, status.BuiltChapters);
        Assert.Equal(2, status.StaleCount);
    }

    // ─── 3c. Rollup coverage: a partial cached rollup is not "ready" and forces a rebuild ─────────

    [Fact]
    public async Task BuildBookSummaryAsync_RollupMissingNewlyBuiltChapter_IsNotReadyAndRebuilds()
    {
        // Bug: the no-op gate (and IsReady) checked only StaleCount/HasSummary/model, NOT whether the cached
        // rollup still covers every built chapter. After a chapter gains a fresh brief outside a full book
        // build, every chapter is individually fresh yet the persisted BookBriefJson is an older partial
        // rollup — status must report NOT ready and a build must NOT be a no-op.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new()
        {
            ["CH_A"] = BriefAJson,
            ["CH_B"] = BriefBJson,
            ["CH_C"] = BriefAJson
        });
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chA = Guid.NewGuid();
        var chB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Coverage Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chA, BookId = bookId, Order = 0, Title = "A", ContentText = "CH_A תוכן." });
        db.Chapters.Add(new Chapter { Id = chB, BookId = bookId, Order = 1, Title = "B", ContentText = "CH_B תוכן." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();

        // Full build over 2 chapters → cached rollup covers 2.
        var first = await svc.BuildBookSummaryAsync(bookId, "he");
        Assert.True(first.Ready);
        var row = await db.BookSummaryBaselines.SingleAsync(b => b.BookId == bookId);
        Assert.Equal(2, row.BuiltChapterCount);

        // A 3rd chapter is added and gains a FRESH L0 brief OUTSIDE a full book build (single-chapter/L0-only
        // path): all 3 chapters are now individually fresh, but the cached rollup still covers only 2.
        var chC = Guid.NewGuid();
        db.Chapters.Add(new Chapter { Id = chC, BookId = bookId, Order = 2, Title = "C", ContentText = "CH_C תוכן." });
        await db.SaveChangesAsync();
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chC, Language = "he",
            StructuredJson = BriefAJson, BuiltWithModel = ActiveModel,
            StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        await db.SaveChangesAsync();

        // Status must NOT report ready: every chapter is fresh, but the rollup is a partial (2/3) snapshot.
        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.Equal(0, status.StaleCount);
        Assert.True(status.HasSummary);
        Assert.Equal(3, status.BuiltChapters);
        Assert.False(status.SummaryCoversBuiltChapters);
        Assert.False(status.IsReady);

        // A build is therefore NOT a no-op: it recomposes the rollup to cover all 3 chapters.
        var second = await svc.BuildBookSummaryAsync(bookId, "he");
        Assert.False(second.NoOp);
        var rebuilt = await db.BookSummaryBaselines.SingleAsync(b => b.BookId == bookId);
        Assert.Equal(3, rebuilt.BuiltChapterCount);

        // And once rebuilt to full coverage, a subsequent build IS a no-op again.
        var third = await svc.BuildBookSummaryAsync(bookId, "he");
        Assert.True(third.NoOp);
    }

    // ─── 3d. Controller surface: POST summary/build fast path must honour rollup coverage ─────────

    [Fact]
    public async Task BuildBookSummary_ControllerFastPath_PartialRollup_DoesNotNoOp()
    {
        // Controller surface of the coverage bug: POST summary/build re-derived the no-op gate WITHOUT
        // SummaryCoversBuiltChapters, so a partial cached rollup (a chapter gained a brief outside a full
        // build) returned NoOp/Ready with no jobId and never refreshed BookBriefJson — even though GET summary
        // already reported not-ready via IsReady. The fast path must use status.IsReady (single source of truth).
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new());
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<BookSummaryService>();
        var registry = provider.GetRequiredService<BookSummaryBuildRegistry>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Controller Coverage Book", Language = "he" });
        // Three chapters, each with a FRESH L0 brief (StructuredBuiltAt after the chapter's UpdatedAt, active model).
        for (var i = 0; i < 3; i++)
        {
            var chId = Guid.NewGuid();
            db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = i, Title = $"Ch{i}", ContentText = $"תוכן {i}." });
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chId, Language = "he",
                StructuredJson = BriefAJson, BuiltWithModel = ActiveModel,
                StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1)
            });
        }
        // Cached rollup covers only 2 of the 3 fresh chapters (partial), under the active model.
        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId, Language = "he",
            BookBriefJson = """{ "genre": "Old partial" }""",
            BuiltChapterCount = 2, BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        // Pre-status: everything fresh + same model, but the rollup does NOT cover all built chapters.
        var pre = await svc.GetStatusAsync(bookId, "he");
        Assert.Equal(0, pre.StaleCount);
        Assert.True(pre.HasSummary);
        Assert.False(pre.BuiltWithDifferentModel);
        Assert.False(pre.SummaryCoversBuiltChapters);
        Assert.False(pre.IsReady);

        // Register an already-running build so that, once PAST the no-op gate, the controller takes the
        // DETERMINISTIC dedup branch (returns the existing jobId, NoOp:false) instead of spawning a real
        // build. With the bug the no-op gate fires FIRST and returns NoOp:true / null jobId.
        var existingJobId = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, "he", existingJobId));
        progress.StartJob(existingJobId, AnalysisScope.Book, AnalysisType.Summarization, bookId, null, null);
        progress.SetStatus(existingJobId, AnalysisProgressStatus.Running, "running");

        var controller = new BooksController(
            db: db,
            bookIntelligence: null!,
            styleBaseline: null!,
            bookSummary: svc,
            bookReview: null!,
            reviewRegistry: null!,
            progress: progress,
            scopeFactory: scopeFactory,
            appLifetime: new TestApplicationLifetime(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<BooksController>.Instance);

        var action = await controller.BuildBookSummary(bookId, new BuildBookSummaryRequest("he"), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<StartBookSummaryBuildResponse>(ok.Value);

        // The no-op fast path did NOT fire: the request proceeded to the build path (here, dedup).
        Assert.False(response.NoOp);
        Assert.Equal(existingJobId, response.JobId);
    }

    private sealed class TestApplicationLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    // ─── 3e. Unparseable StructuredJson: status agrees with composition, and a build recovers ────

    [Fact]
    public async Task GetStatusAsync_UnparseableStructuredJson_CountsChapterStaleNotBuilt()
    {
        // Bug: a non-empty but UNPARSEABLE StructuredJson passed the freshness/status "has a brief" test
        // (non-empty only) while ComposeChapterBriefsAsync skipped it (it parses). Status counted it built
        // yet composition omitted it, so BuiltChapterCount < built and the summary could never read ready.
        // Status must now PARSE (StructuredChunkSummaryParser.IsUsable) and count it stale, agreeing with
        // composition.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new());
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Corrupt Brief Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = 0, Title = "Ch0", ContentText = "תוכן." });
        // Fresh by timestamp + model + language, but the StructuredJson is non-empty and UNPARSEABLE.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chId, Language = "he",
            StructuredJson = "{ this is not valid json",
            BuiltWithModel = ActiveModel, StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();
        var status = await svc.GetStatusAsync(bookId, "he");

        // The unparseable brief is NOT usable → counted stale, matching ComposeChapterBriefsAsync.
        Assert.Equal(0, status.BuiltChapters);
        Assert.Equal(1, status.StaleCount);

        var l1 = await svc.ComposeChapterBriefsAsync(bookId, "he");
        Assert.Empty(l1); // composition also skips it → status and composition agree
    }

    [Fact]
    public async Task BuildBookSummaryAsync_UnparseableBrief_RebuildsToReady()
    {
        // End-to-end recovery: a fresh-but-unparseable cached brief must be treated as stale so the build
        // REBUILDS it (the freshness gate no longer serves it as fresh-null without rebuilding), the rollup
        // composes, and the summary reaches ready. Pre-fix the gate returned the corrupt brief as fresh →
        // no rebuild, zero composed briefs, never ready.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out var routerMock, briefByContent: new()
        {
            ["CH_A"] = BriefAJson
        });
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Recovery Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = 0, Title = "A", ContentText = "CH_A תוכן." });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chId, Language = "he",
            StructuredJson = "{ corrupt", // non-empty but unparseable
            BuiltWithModel = ActiveModel, StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();
        var result = await svc.BuildBookSummaryAsync(bookId, "he");

        // The corrupt brief was rebuilt (one LLM call), composed, and the rollup is ready.
        Assert.True(result.Ready);
        Assert.Equal(1, result.BuiltChapters);
        Assert.Equal(0, result.FailedChapters);
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.True(status.IsReady);
        Assert.Equal(1, status.BuiltChapters);

        // The persisted StructuredJson is now a usable (parseable) brief, not the corrupt blob.
        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chId);
        Assert.True(StructuredChunkSummaryParser.IsUsable(row.StructuredJson));
    }

    // ─── 4. Per-chapter failure isolation: one bad chapter does not abort the job ─────────────────

    [Fact]
    public async Task BuildBookSummaryAsync_OneChapterFails_JobContinuesAndCountsIt()
    {
        // The bad chapter's content makes the mock return empty → its L0 build degrades to null (no row),
        // but the two good chapters still build and the rollup is composed from them.
        const string badContent = "BAD_CHAPTER";

        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out _, briefByContent: new()
        {
            ["CH_A"] = BriefAJson,
            ["CH_B"] = BriefBJson
        });
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var goodA = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var goodB = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Resilient Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = goodA, BookId = bookId, Order = 0, Title = "A", ContentText = "CH_A תקין." });
        db.Chapters.Add(new Chapter { Id = bad, BookId = bookId, Order = 1, Title = "Bad", ContentText = badContent });
        db.Chapters.Add(new Chapter { Id = goodB, BookId = bookId, Order = 2, Title = "B", ContentText = "CH_B תקין." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();
        var result = await svc.BuildBookSummaryAsync(bookId, "he");

        // The job did NOT abort: both good chapters built, the bad one counted as failed.
        Assert.True(result.Ready);
        Assert.Equal(3, result.TotalChapters);
        Assert.Equal(2, result.BuiltChapters);
        Assert.Equal(1, result.FailedChapters);

        // Only the two good chapters got persisted L0 rows; the rollup is composed from them.
        Assert.Equal(2, await db.ChunkSummaries.CountAsync());
        Assert.False(await db.ChunkSummaries.AnyAsync(cs => cs.ChapterId == bad));

        var row = await db.BookSummaryBaselines.SingleAsync(b => b.BookId == bookId);
        Assert.Equal(2, row.BuiltChapterCount);
        var l2 = JsonSerializer.Deserialize<BookBrief>(row.BookBriefJson, DeserializeOpts);
        // Themes rolled up from the two GOOD chapters only (union, deduped).
        Assert.Equal(new[] { "isolation", "rebirth", "nature" }, l2!.Themes);
    }

    [Fact]
    public async Task BuildBookSummaryAsync_NoChapters_ReturnsNotReadyZeroResult()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildPerChapterProvider(dbName, out var routerMock, briefByContent: new());
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Empty Book", Language = "he" });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookSummaryService>();
        var result = await svc.BuildBookSummaryAsync(bookId, "he");

        Assert.Equal(0, result.TotalChapters);
        Assert.Equal(0, result.BuiltChapters);
        Assert.False(result.Ready);
        Assert.Equal(0, await db.BookSummaryBaselines.CountAsync());
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Helper: DI ServiceProvider mirroring ChapterBriefServiceTests' fixed-DB-name trick ───────

    /// <summary>
    /// Builds a provider whose mock IAiRouter returns the L0 JSON keyed by a substring of the chapter
    /// content (so different chapters yield different briefs and a "bad" content yields empty → failure).
    /// Crucially the router reports <see cref="ActiveModel"/> as the model, so freshly-built L0 briefs are
    /// model-fresh and the post-build status counts them as built (matching production, where the resolved
    /// Summarization model IS the active model). Uses a FIXED in-memory DB name so the book build's
    /// per-chapter child DI scopes share one store.
    /// </summary>
    private static ServiceProvider BuildPerChapterProvider(
        string dbName,
        out Mock<IAiRouter> routerMock,
        Dictionary<string, string> briefByContent)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                var content = "";
                foreach (var kvp in briefByContent)
                {
                    if (req.InputText.Contains(kvp.Key))
                    {
                        content = kvp.Value;
                        break;
                    }
                }
                return new AiResponse
                {
                    Content = content,
                    Model = ActiveModel, // report the active model so built L0 briefs are model-fresh
                    Provider = "test-provider"
                };
            });
        services.AddSingleton(routerMock.Object);
        services.Configure<AiOptions>(_ => { });

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();

        return services.BuildServiceProvider();
    }
}
