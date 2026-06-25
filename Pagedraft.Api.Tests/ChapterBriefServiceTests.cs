using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for <see cref="ChapterBriefService"/> (wb1-c01): the per-chapter STRUCTURED brief builder that
/// populates <see cref="ChunkSummary.StructuredJson"/> from <see cref="StructuredChunkSummaryData"/>.
/// Covers structured parse, cache hit/miss, incremental (only stale chapters rebuild), cross-model
/// staleness, graceful degradation (empty/unparseable → null, job continues), and a build→persist→read
/// round-trip. Mirrors the <see cref="ChapterStyleProfileAndLinguisticTests"/> conventions.
/// </summary>
public class ChapterBriefServiceTests
{
    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Active Summarization model the freshness gate resolves to under empty AiOptions: FeatureModels is
    // unset, so it falls back to AiOptions.DefaultModel. Profiles seeded with THIS model are model-fresh;
    // any other (or null) is model-stale (cross-model self-heal). Kept in sync with AiOptions.DefaultModel.
    private const string ActiveModel = "qwen2.5:14b";

    // A complete structured-brief JSON the mock LLM returns (camelCase, matches StructuredChunkSummaryData).
    private const string ValidBriefJson = """
        {
          "plotEvents": ["The hero leaves home", "A storm hits the village"],
          "characterStates": [
            { "name": "Dana", "state": "fleeing the city", "emotionalArc": "fear turns to resolve" }
          ],
          "thematicMarkers": ["isolation", "rebirth"],
          "toneNotes": "tense and foreboding",
          "openThreads": ["who sent the letter?"]
        }
        """;

    // ─── 1. Structured parse: valid JSON → populated fields ───────────────────────────────────

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_ValidJson_PopulatesAllStructuredFields()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Brief Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "פרק עם תוכן לניתוח." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.NotNull(brief);
        Assert.Equal(new[] { "The hero leaves home", "A storm hits the village" }, brief!.PlotEvents);
        Assert.Single(brief.CharacterStates);
        Assert.Equal("Dana", brief.CharacterStates[0].Name);
        Assert.Equal("fleeing the city", brief.CharacterStates[0].State);
        Assert.Equal("fear turns to resolve", brief.CharacterStates[0].EmotionalArc);
        Assert.Equal(new[] { "isolation", "rebirth" }, brief.ThematicMarkers);
        Assert.Equal("tense and foreboding", brief.ToneNotes);
        Assert.Equal(new[] { "who sent the letter?" }, brief.OpenThreads);

        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── 1b. build → persist → read → deserialize round-trips through StructuredJson ────────────

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_PersistsStructuredJson_ThatRoundTripsFromDb()
    {
        using var provider = BuildServiceProvider(out _, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "RoundTrip Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        // Read the persisted row straight from the DB and deserialize the StructuredJson column back.
        var row = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.False(string.IsNullOrWhiteSpace(row.StructuredJson));
        Assert.Equal("test-model", row.BuiltWithModel); // router-reported model is stamped

        var fromDb = JsonSerializer.Deserialize<StructuredChunkSummaryData>(row.StructuredJson!, DeserializeOpts);
        Assert.NotNull(fromDb);
        Assert.Equal("tense and foreboding", fromDb!.ToneNotes);
        Assert.Equal(2, fromDb.PlotEvents.Count);
        Assert.Single(fromDb.CharacterStates);
        Assert.Equal("Dana", fromDb.CharacterStates[0].Name);

        // The flat SummaryText is left empty (back-compat: BookIntelligenceService owns it).
        Assert.True(string.IsNullOrEmpty(row.SummaryText));
    }

    // ─── 2. Cache MISS → builds + persists exactly one row, LLM called once ────────────────────

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_CacheMiss_BuildsAndPersistsOneRow()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Miss Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן מספיק." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.NotNull(brief);
        Assert.Equal(1, await db.ChunkSummaries.CountAsync());
        var row = await db.ChunkSummaries.SingleAsync();
        Assert.Equal("test-model", row.BuiltWithModel);
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── 2b. Cache HIT: fresh structured brief returned, LLM never called ──────────────────────

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_FreshCachedBrief_ReturnsCachedWithoutLlm()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Hit Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן." });
        var hitRowId = Guid.NewGuid();
        db.ChunkSummaries.Add(new ChunkSummary
        {
            Id = hitRowId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            StructuredJson = ValidBriefJson,
            BuiltWithModel = ActiveModel // model-fresh so only the timestamp path is exercised
        });
        await db.SaveChangesAsync();

        // Force timestamp-fresh: chapter edited BEFORE the cached brief was built.
        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        // wb1-r02: the structured freshness gate reads StructuredBuiltAt (not the shared CreatedAt). The row
        // is read TRACKED by the service, so a post-save mutation is visible. Stamp it AFTER the chapter's
        // UpdatedAt so the brief is timestamp-fresh on the new column.
        var rowEntry = db.Entry(db.ChunkSummaries.Local.Single(cs => cs.Id == hitRowId));
        rowEntry.Entity.StructuredBuiltAt = DateTimeOffset.UtcNow;
        rowEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.NotNull(brief);
        Assert.Equal("tense and foreboding", brief!.ToneNotes);
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, await db.ChunkSummaries.CountAsync());
    }

    // ─── 2c. Flat-only legacy row (no StructuredJson) is treated as a MISS and built ───────────

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_FlatOnlyRow_BuildsStructuredJsonInPlace()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Flat Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן." });
        // Existing row carries only the flat SummaryText (BookIntelligenceService output); no StructuredJson.
        var rowId = Guid.NewGuid();
        db.ChunkSummaries.Add(new ChunkSummary
        {
            Id = rowId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "A flat natural-language summary.",
            StructuredJson = null
        });
        await db.SaveChangesAsync();

        // Even timestamp-fresh: the missing StructuredJson alone forces a build.
        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.NotNull(brief);
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        // The SAME row was updated in place (structured filled, flat summary preserved).
        var row = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal(rowId, row.Id);
        Assert.False(string.IsNullOrWhiteSpace(row.StructuredJson));
        Assert.Equal("A flat natural-language summary.", row.SummaryText); // back-compat preserved
        Assert.Equal(1, await db.ChunkSummaries.CountAsync());
    }

    // ─── 2d. Cross-language refresh clears the stale flat summary from the other locale ────────

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_CrossLanguageRefresh_ClearsStaleFlatSummary()
    {
        // A row built flat in 'en' (Language="en", English SummaryText) that the user MANUALLY edited
        // (SummaryUserEdited=true). A structured (re)build for 'he' flips the row's Language to "he"; the
        // English SummaryText must NOT be left behind, or BookContextAssembler — which selects flat fallbacks
        // by Language only — would assemble English prose into the Hebrew book context. AND the user-edit
        // clobber guard must be reset alongside the cleared prose: leaving SummaryUserEdited=true with an empty
        // SummaryText makes the automatic re-summary skip this row to "preserve" a now-empty edit (so the
        // Hebrew flat prose never regenerates) and makes the re-derive endpoint 409 on the empty text.
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Cross-Lang Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן עברי." });
        var rowId = Guid.NewGuid();
        db.ChunkSummaries.Add(new ChunkSummary
        {
            Id = rowId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = "en",
            SummaryText = "English prose summary that must not leak into the Hebrew context.",
            SummaryUserEdited = true,
            SummaryUserEditedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            StructuredJson = null
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.NotNull(brief);
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        var row = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal(rowId, row.Id); // same row refreshed in place
        Assert.Equal("he", row.Language);
        Assert.False(string.IsNullOrWhiteSpace(row.StructuredJson));
        // The stale English flat summary is cleared so it cannot be served as Hebrew prose by the assembler.
        Assert.True(string.IsNullOrEmpty(row.SummaryText),
            $"expected the cross-language flat summary to be cleared, got: '{row.SummaryText}'");
        // The clobber guard is reset alongside the cleared prose — otherwise the now-empty SummaryText would
        // be treated as a protected manual edit (automatic re-summary skipped; re-derive 409s).
        Assert.False(row.SummaryUserEdited,
            "expected the user-edit clobber guard to be reset when the flat summary it protected was cleared.");
        Assert.Null(row.SummaryUserEditedAt);
    }

    // ─── 3. Timestamp-stale brief is rebuilt in place ─────────────────────────────────────────

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_TimestampStale_RebuildsInPlace()
    {
        const string newBrief = """
            { "plotEvents": ["rebuilt event"], "characterStates": [], "thematicMarkers": [], "toneNotes": "new", "openThreads": [] }
            """;

        using var provider = BuildServiceProvider(out var routerMock, llmResponse: newBrief);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Stale Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן ששונה." });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            Id = rowId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            StructuredJson = ValidBriefJson, // old brief
            BuiltWithModel = ActiveModel,
            StructuredBuiltAt = DateTimeOffset.UtcNow // pushed to the past below to force timestamp-stale
        });
        await db.SaveChangesAsync();

        // Force timestamp-stale: the cached structured brief was built BEFORE the chapter's last edit.
        // wb1-r02: the structured gate reads StructuredBuiltAt, so push THAT (not the shared CreatedAt) back.
        var rowEntry = db.Entry(db.ChunkSummaries.Local.Single(cs => cs.Id == rowId));
        rowEntry.Entity.StructuredBuiltAt = DateTimeOffset.UtcNow.AddHours(-2);
        rowEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(brief);
        Assert.Equal("new", brief!.ToneNotes);

        var row = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal(rowId, row.Id); // updated in place
        Assert.Contains("rebuilt event", row.StructuredJson);
        Assert.Equal(1, await db.ChunkSummaries.CountAsync());
    }

    // ─── 4. Cross-model staleness: a brief built under a different model is rebuilt + restamped ─

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_ModelMismatch_RebuildsAndRestamps()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Cross-Model Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן." });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            Id = rowId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            StructuredJson = ValidBriefJson,
            BuiltWithModel = "some-old-model", // different from the active model
            // wb1-r02: keep StructuredBuiltAt fresh so ONLY the model mismatch can make it stale (the chapter
            // is forced to UpdatedAt = now-2h below), isolating the cross-model gate from the null-stamp path.
            StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Timestamp-fresh so ONLY the model mismatch can make it stale (isolates the cross-model gate).
        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "A model-mismatched brief must be rebuilt even when timestamp-fresh");
        Assert.NotNull(brief);

        var row = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal("test-model", row.BuiltWithModel); // restamped with the model actually used
        Assert.Equal(1, await db.ChunkSummaries.CountAsync());
    }

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_LegacyNullModel_TreatedAsStaleAndRebuilt()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Legacy Null Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן." });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            StructuredJson = ValidBriefJson,
            BuiltWithModel = null // legacy structured row before the column existed
        });
        await db.SaveChangesAsync();

        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<ChapterBriefService>();
        await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "A legacy null-BuiltWithModel structured row must self-heal (rebuild) on next access");
        var row = await db.ChunkSummaries.SingleAsync();
        Assert.Equal("test-model", row.BuiltWithModel);
    }

    // ─── 5. Graceful degradation: empty / unparseable model output → null, no row, no throw ────

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_EmptyLlmOutput_ReturnsNullNoRow()
    {
        using var provider = BuildServiceProvider(out _, llmResponse: "");
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Empty Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.Null(brief);
        Assert.Equal(0, await db.ChunkSummaries.CountAsync());
    }

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_UnparseableLlmOutput_ReturnsNullNoRow()
    {
        // No JSON object at all → ExtractJson yields null → graceful null.
        using var provider = BuildServiceProvider(out _, llmResponse: "Sorry, I cannot help with that.");
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Unparseable Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.Null(brief);
        Assert.Equal(0, await db.ChunkSummaries.CountAsync());
    }

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_LlmThrows_ReturnsNullWithoutThrowing()
    {
        using var provider = BuildServiceProvider(out _, llmThrows: true);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Throws Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "תוכן." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.Null(brief);
        Assert.Equal(0, await db.ChunkSummaries.CountAsync());
    }

    [Fact]
    public async Task LoadOrBuildChapterBriefAsync_EmptyChapterContent_ReturnsNullNoLlm()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Empty Chapter Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "" });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");

        Assert.Null(brief);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Empty chapter content must not trigger a brief build");
    }

    // ─── 6. Incremental book build: only stale/missing chapters rebuild; fresh ones are NOT ────

    [Fact]
    public async Task BuildBookChapterBriefsAsync_OnlyStaleChaptersRebuilt_FreshNotRebuilt()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: ValidBriefJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var freshChapterId = Guid.NewGuid();   // already has a fresh structured brief → skipped
        var staleChapterId = Guid.NewGuid();   // has a stale brief → rebuilt
        var missingChapterId = Guid.NewGuid(); // no brief at all → built

        // Use real SAVE ORDERING + tiny delays so the PERSISTED timestamps reflect freshness — the
        // book build's child DI scopes read from the shared store, which only holds committed values
        // (a post-save Unchanged mutation is not flushed and would not be visible to a child scope).
        db.Books.Add(new Book { Id = bookId, Title = "Incremental Book" });

        // 1. Stale chapter + its brief FIRST so the brief is the OLDEST row.
        db.Chapters.Add(new Chapter { Id = staleChapterId, BookId = bookId, Order = 1, Title = "Stale", ContentText = "תוכן פרק ישן." });
        var staleRowId = Guid.NewGuid();
        db.ChunkSummaries.Add(new ChunkSummary
        {
            Id = staleRowId, BookId = bookId, ChapterId = staleChapterId, Language = "he",
            // wb1-r02: structured freshness reads StructuredBuiltAt. Stamp it = now at this Add (committed
            // value, visible to the book build's child DI scopes) so the time-ordering — oldest brief first,
            // then the stale chapter touched later — makes this brief timestamp-stale, exactly as before.
            StructuredJson = ValidBriefJson, BuiltWithModel = ActiveModel, StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 2. The fresh + missing chapters next (created AFTER the stale brief).
        db.Chapters.Add(new Chapter { Id = freshChapterId, BookId = bookId, Order = 0, Title = "Fresh", ContentText = "תוכן פרק טרי." });
        db.Chapters.Add(new Chapter { Id = missingChapterId, BookId = bookId, Order = 2, Title = "Missing", ContentText = "תוכן פרק חסר." });
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 3. Touch the stale chapter so its UpdatedAt jumps AFTER its (step-1) brief → timestamp-stale.
        var staleChapter = await db.Chapters.SingleAsync(c => c.Id == staleChapterId);
        staleChapter.Title = "Stale (edited)";
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 4. The fresh chapter's brief LAST so it is newer than its chapter → timestamp-fresh.
        var freshRowId = Guid.NewGuid();
        db.ChunkSummaries.Add(new ChunkSummary
        {
            Id = freshRowId, BookId = bookId, ChapterId = freshChapterId, Language = "he",
            // wb1-r02: stamp StructuredBuiltAt = now at this LAST Add so this brief is newer than its chapter
            // (committed value visible to child scopes) → timestamp-fresh and skipped, exactly as before.
            StructuredJson = ValidBriefJson, BuiltWithModel = ActiveModel, StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var result = await svc.BuildBookChapterBriefsAsync(bookId, "he");

        // Exactly TWO LLM calls: the stale rebuild + the missing build. The fresh chapter was NOT rebuilt.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "Only stale + missing chapters should be (re)built; the fresh one is skipped");

        Assert.Equal(3, result.TotalChapters);
        Assert.Equal(3, result.BuiltChapters); // all three now have a usable brief
        Assert.Equal(0, result.FailedChapters);

        // All three chapters now carry a structured brief, and there are exactly three rows.
        Assert.Equal(3, await db.ChunkSummaries.CountAsync());
        Assert.True(await db.ChunkSummaries.AllAsync(cs => cs.StructuredJson != null));
    }

    // ─── 7. Book build continues past a failing chapter (graceful degradation, no abort) ───────

    [Fact]
    public async Task BuildBookChapterBriefsAsync_OneChapterFails_JobContinuesAndCountsIt()
    {
        // The mock returns valid JSON for chapters whose text differs from the "BAD" sentinel, and empty
        // for the bad one — so exactly one chapter fails to build but the job still processes the rest.
        const string badContent = "BAD_CHAPTER_TEXT";

        var services = new ServiceCollection();
        services.AddLogging();
        // Fixed DB name so the per-chapter child scopes share the same in-memory store (see helper note).
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) => new AiResponse
            {
                Content = req.InputText.Contains(badContent) ? "" : ValidBriefJson,
                Model = "test-model",
                Provider = "test-provider"
            });
        services.AddSingleton(routerMock.Object);
        services.Configure<AiOptions>(_ => { });
        services.AddScoped<ChapterBriefService>();

        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Resilient Book" });
        var goodA = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var goodB = Guid.NewGuid();
        db.Chapters.Add(new Chapter { Id = goodA, BookId = bookId, Order = 0, Title = "A", ContentText = "פרק תקין א." });
        db.Chapters.Add(new Chapter { Id = bad, BookId = bookId, Order = 1, Title = "Bad", ContentText = badContent });
        db.Chapters.Add(new Chapter { Id = goodB, BookId = bookId, Order = 2, Title = "B", ContentText = "פרק תקין ב." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var result = await svc.BuildBookChapterBriefsAsync(bookId, "he");

        // The job did NOT abort: both good chapters built, the bad one counted as failed.
        Assert.Equal(3, result.TotalChapters);
        Assert.Equal(2, result.BuiltChapters);
        Assert.Equal(1, result.FailedChapters);

        // Only the two good chapters got persisted rows; the failed one wrote nothing.
        Assert.Equal(2, await db.ChunkSummaries.CountAsync());
        Assert.False(await db.ChunkSummaries.AnyAsync(cs => cs.ChapterId == bad));
    }

    [Fact]
    public async Task BuildBookChapterBriefsAsync_NoChapters_ReturnsZeroResult()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Empty Book" });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<ChapterBriefService>();
        var result = await svc.BuildBookChapterBriefsAsync(bookId, "he");

        Assert.Equal(0, result.TotalChapters);
        Assert.Equal(0, result.BuiltChapters);
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── wb1-r02: the flat re-summary path must NOT mask a stale structured brief ───────────────

    /// <summary>
    /// REGRESSION (wb1-r02): the structured brief and the legacy flat summary share ONE ChunkSummary row.
    /// The flat re-summary path (BookIntelligenceService.SummarizeChaptersAsync) bumps the SHARED CreatedAt.
    /// Before the fix the structured freshness gate keyed on CreatedAt, so a flat re-summary AFTER a chapter
    /// edit pushed CreatedAt past the edit and FALSELY made the (old-content) structured brief read fresh —
    /// serving a stale brief into the book-analysis context. The fix gives the structured brief its OWN
    /// build stamp (StructuredBuiltAt) that the flat path does not touch.
    ///
    /// Flow: build a structured brief → edit the chapter (brief now correctly stale) → run the REAL flat
    /// SummarizeChaptersAsync → assert the brief is reported STALE (status) AND is rebuilt on next
    /// LoadOrBuildChapterBriefAsync access, NOT served as fresh. Fails before the fix (gate read CreatedAt,
    /// which the flat path bumped), passes after (gate reads StructuredBuiltAt, which the flat path leaves
    /// alone).
    /// </summary>
    [Fact]
    public async Task FlatSummarizeChapters_DoesNotMaskStaleStructuredBrief()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildFlatAndStructuredProvider(dbName, out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Shared-Row Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Order = 0, Title = "Ch1", ContentText = "תוכן מקורי לניתוח." });
        await db.SaveChangesAsync();

        // 1. Build the structured brief from the ORIGINAL content. Stamps StructuredBuiltAt = now.
        var briefSvc = provider.GetRequiredService<ChapterBriefService>();
        var firstBrief = await briefSvc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");
        Assert.NotNull(firstBrief);
        Assert.Equal("tense and foreboding", firstBrief!.ToneNotes); // the ORIGINAL (now soon-stale) brief

        // Sanity: status reports it fresh right after building (1 built, 0 stale).
        var summarySvc = provider.GetRequiredService<BookSummaryService>();
        var statusFresh = await summarySvc.GetStatusAsync(bookId, "he");
        Assert.Equal(1, statusFresh.BuiltChapters);
        Assert.Equal(0, statusFresh.StaleCount);

        await Task.Delay(15);

        // 2. Edit the chapter so its UpdatedAt jumps AFTER the brief build → brief is now CORRECTLY stale.
        var chapter = await db.Chapters.SingleAsync(c => c.Id == chapterId);
        chapter.ContentText = "תוכן ערוך וחדש לגמרי.";
        chapter.Title = "Ch1 (edited)";
        await db.SaveChangesAsync();

        await Task.Delay(15);

        // 3. Run the REAL legacy flat path. It re-summarizes the chapter text and bumps the SHARED CreatedAt
        //    to NOW (past the chapter edit), leaving StructuredJson + StructuredBuiltAt untouched. Before the
        //    fix this masked the staleness; after the fix it cannot.
        var intelligence = provider.GetRequiredService<BookIntelligenceService>();
        await intelligence.SummarizeChaptersAsync(bookId, "he");

        // The flat write really did bump CreatedAt past the chapter edit (this is the masking lever).
        var rowAfterFlat = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.True(rowAfterFlat.CreatedAt > chapter.UpdatedAt,
            "Flat re-summary must bump the shared CreatedAt past the chapter edit (the masking condition).");
        Assert.False(string.IsNullOrWhiteSpace(rowAfterFlat.SummaryText)); // flat surface was written
        Assert.NotNull(rowAfterFlat.StructuredBuiltAt);
        Assert.True(rowAfterFlat.StructuredBuiltAt < chapter.UpdatedAt,
            "Structured build stamp must remain BEFORE the chapter edit (the flat path must not touch it).");

        // 4a. Status must STILL report the structured brief STALE despite the bumped CreatedAt.
        var statusAfterFlat = await summarySvc.GetStatusAsync(bookId, "he");
        Assert.Equal(0, statusAfterFlat.BuiltChapters);
        Assert.Equal(1, statusAfterFlat.StaleCount);

        // 4b. And the next structured access must REBUILD (not serve the stale brief). Reset the LLM-call
        //     counter and verify exactly one rebuild call happens, returning the NEW-content brief.
        routerMock.Invocations.Clear();
        var rebuilt = await briefSvc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he");
        Assert.NotNull(rebuilt);
        Assert.Equal("rebuilt tone", rebuilt!.ToneNotes); // the rebuilt brief, not the stale "tense and foreboding"
        Assert.Contains(
            routerMock.Invocations,
            i => i.Method.Name == nameof(IAiRouter.CompleteAsync) &&
                 ((AiRequest)i.Arguments[0]).TaskType == AiTaskType.Summarization &&
                 ((AiRequest)i.Arguments[0]).JsonMode);

        // Post-rebuild the structured brief is fresh again (status flips back to built).
        var statusRebuilt = await summarySvc.GetStatusAsync(bookId, "he");
        Assert.Equal(1, statusRebuilt.BuiltChapters);
        Assert.Equal(0, statusRebuilt.StaleCount);
    }

    /// <summary>
    /// Regression: the legacy flat path persisted the RAW request locale ("en-US"), which the assembler and
    /// the structured path — both keyed on the NORMALIZED locale ("en") — then skipped. SummarizeChaptersAsync
    /// must store the flat summary under the normalized locale so it shares ONE language key with the rest of
    /// the system.
    /// </summary>
    [Fact]
    public async Task SummarizeChaptersAsync_StoresNormalizedLanguage_NotRawLocale()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildFlatAndStructuredProvider(dbName, out _);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Locale Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Order = 0, Title = "Ch1", ContentText = "English chapter body." });
        await db.SaveChangesAsync();

        var intelligence = provider.GetRequiredService<BookIntelligenceService>();
        await intelligence.SummarizeChaptersAsync(bookId, "en-US"); // RAW locale request

        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.False(string.IsNullOrWhiteSpace(row.SummaryText)); // the flat summary was written
        Assert.Equal("en", row.Language); // normalized, NOT the raw "en-US"
    }

    /// <summary>
    /// DI provider wiring BOTH the structured builder (<see cref="ChapterBriefService"/> /
    /// <see cref="BookSummaryService"/>) AND the real flat path (<see cref="BookIntelligenceService"/> and
    /// its full analysis graph), so the wb1-r02 regression exercises the genuine flat writer rather than a
    /// simulation. The router mock returns a STRUCTURED brief (JsonMode Summarization) and a short flat
    /// summary otherwise; the structured reply differs by call so a rebuild is observable.
    /// </summary>
    private static ServiceProvider BuildFlatAndStructuredProvider(string dbName, out Mock<IAiRouter> routerMock)
    {
        // First structured reply (original content) vs subsequent rebuild reply — distinguishable by ToneNotes
        // so the test can prove a rebuild served NEW content, not the cached stale brief.
        const string originalBriefJson = """
            { "plotEvents": ["original event"], "characterStates": [], "thematicMarkers": ["a"], "toneNotes": "tense and foreboding", "openThreads": [] }
            """;
        const string rebuiltBriefJson = """
            { "plotEvents": ["edited event"], "characterStates": [], "thematicMarkers": ["b"], "toneNotes": "rebuilt tone", "openThreads": [] }
            """;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));

        routerMock = new Mock<IAiRouter>();
        var structuredCalls = 0;
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                // The structured-brief request is the JsonMode Summarization call (ChapterBriefService); the
                // flat re-summary is a non-JsonMode Summarization call (UnifiedAnalysisService.RunRawAsync).
                var isStructured = req.TaskType == AiTaskType.Summarization && req.JsonMode;
                string content;
                if (isStructured)
                {
                    var n = Interlocked.Increment(ref structuredCalls);
                    content = n == 1 ? originalBriefJson : rebuiltBriefJson;
                }
                else
                {
                    content = "A short flat natural-language summary.";
                }
                return new AiResponse { Content = content, Model = ActiveModel, Provider = "test-provider" };
            });

        services.AddSingleton(routerMock.Object);
        services.Configure<AiOptions>(_ => { });
        services.Configure<Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions>(_ => { });

        // Shared infra.
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<SuggestionDiffService>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker>();

        // Structured + flat analysis graph (mirrors Program.cs registrations).
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();

        return services.BuildServiceProvider();
    }

    // ─── Helper: DI ServiceProvider mirroring ChapterStyleProfileAndLinguisticTests ────────────

    private static ServiceProvider BuildServiceProvider(
        out Mock<IAiRouter> routerMock,
        string? llmResponse = null,
        bool llmThrows = false)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        // A FIXED database name (not a fresh Guid per context) so child DI scopes spawned by
        // BuildBookChapterBriefsAsync share the SAME in-memory store — mirroring SQL Server, where every
        // scope's DbContext points at one physical database. A per-context Guid would give each child scope
        // an empty DB and the book build would see no chapters.
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));

        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        routerMock = new Mock<IAiRouter>();
        if (llmThrows)
        {
            routerMock
                .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Simulated LLM failure"));
        }
        else
        {
            var content = llmResponse ?? "{}";
            routerMock
                .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AiResponse
                {
                    Content = content,
                    Model = "test-model",
                    Provider = "test-provider"
                });
        }

        services.AddSingleton(routerMock.Object);
        services.Configure<AiOptions>(_ => { });
        services.AddScoped<ChapterBriefService>();

        return services.BuildServiceProvider();
    }
}
