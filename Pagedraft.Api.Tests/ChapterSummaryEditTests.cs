using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
/// wb3-c04: view + EDIT chapter summaries (the dual-surface ChunkSummary — flat user-authoritative
/// SummaryText vs AI structured StructuredJson). Covers:
///   - GET returns BOTH surfaces + the user-edited flag + the two freshness stamps;
///   - PUT saves the flat edit, sets SummaryUserEdited + stamps SummaryUserEditedAt, and does NOT orphan the
///     structured surface (StructuredJson / StructuredBuiltAt / BuiltWithModel untouched);
///   - the rebuild-clobber guard: the real automatic flat re-summary (BookIntelligenceService
///     .SummarizeChaptersAsync) SKIPS a user-edited row rather than overwriting it;
///   - the re-derive incorporates the user's edited summary as authoritative input (the model receives the
///     summary in its instruction) and writes ONLY the structured surface, preserving the flat edit + flag.
/// The model is MOCKED (no live Ollama/GPU) — the router mock distinguishes the structured (JsonMode
/// Summarization) call from the flat one and records the instruction so seeding can be asserted.
/// </summary>
public class ChapterSummaryEditTests
{
    private const string ActiveModel = "qwen2.5:14b";

    private const string StructuredBriefJson = """
        { "plotEvents": ["e1"], "characterStates": [], "thematicMarkers": ["t"], "toneNotes": "derived tone", "openThreads": [] }
        """;

    // ─── GET: dual-surface view ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetChapterSummary_ReturnsBothSurfacesAndFlags()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        var editedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var builtAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "סיכום המשתמש.",
            SummaryUserEdited = true,
            SummaryUserEditedAt = editedAt,
            StructuredJson = StructuredBriefJson,
            StructuredBuiltAt = builtAt,
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.GetChapterSummary(bookId, chapterId, "he", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ChapterSummaryViewDto>(ok.Value);

        Assert.Equal("סיכום המשתמש.", dto.SummaryText);
        Assert.True(dto.HasSummary);
        Assert.True(dto.HasStructuredBrief);
        Assert.True(dto.SummaryUserEdited);
        Assert.Equal(editedAt, dto.SummaryUserEditedAt);
        Assert.Equal(builtAt, dto.StructuredBuiltAt);
        Assert.Equal(ActiveModel, dto.BuiltWithModel);
        Assert.Equal("he", dto.Language);
    }

    [Fact]
    public async Task GetChapterSummary_ParsesStructuredBriefFacts_WhenStructuredJsonPresent()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        // Built-but-never-flat-summarized chapter: structured brief exists, flat SummaryText is EMPTY.
        // This is exactly the wb3-c04 fallback case the FE renders a human-readable digest for.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = string.Empty,
            StructuredJson = StructuredBriefJson,
            StructuredBuiltAt = DateTimeOffset.UtcNow,
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.GetChapterSummary(bookId, chapterId, "he", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ChapterSummaryViewDto>(ok.Value);

        Assert.False(dto.HasSummary);            // no flat summary to show
        Assert.True(dto.HasStructuredBrief);     // but a structured brief exists
        Assert.NotNull(dto.StructuredBrief);     // ...and its parsed facts are exposed for the FE digest
        Assert.Equal(new[] { "e1" }, dto.StructuredBrief!.PlotEvents);
        Assert.Equal(new[] { "t" }, dto.StructuredBrief.ThematicMarkers);
        Assert.Equal("derived tone", dto.StructuredBrief.ToneNotes);
    }

    [Fact]
    public async Task GetChapterSummary_StructuredBriefNull_WhenNoStructuredJson()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        // A flat-only row (no structured surface): no facts to expose, FE shows the user's flat summary.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "flat only.",
            StructuredJson = null
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.GetChapterSummary(bookId, chapterId, "he", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ChapterSummaryViewDto>(ok.Value);
        Assert.True(dto.HasSummary);
        Assert.False(dto.HasStructuredBrief);
        Assert.Null(dto.StructuredBrief);
    }

    [Fact]
    public async Task GetChapterSummary_MalformedStructuredJson_DoesNotThrow_OmitsStructuredBrief()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        // Non-empty but UNPARSEABLE StructuredJson: HasStructuredBrief stays true (presence check, unchanged
        // contract) but the defensive parse yields no facts (null) rather than throwing.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = string.Empty,
            StructuredJson = "{ this is not valid json ",
            StructuredBuiltAt = DateTimeOffset.UtcNow,
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.GetChapterSummary(bookId, chapterId, "he", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ChapterSummaryViewDto>(ok.Value);
        Assert.True(dto.HasStructuredBrief);     // presence check unchanged
        Assert.Null(dto.StructuredBrief);        // defensive parse omitted the malformed facts (no throw)
    }

    [Fact]
    public async Task GetChapterSummary_NoRow_ReturnsEmptyEditableState()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        var controller = BuildController(provider);
        var action = await controller.GetChapterSummary(bookId, chapterId, "he", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ChapterSummaryViewDto>(ok.Value);
        Assert.Equal(string.Empty, dto.SummaryText);
        Assert.False(dto.HasSummary);
        Assert.False(dto.HasStructuredBrief);
        Assert.False(dto.SummaryUserEdited);
        Assert.Null(dto.StructuredBrief);
    }

    [Fact]
    public async Task GetChapterSummary_UnknownChapter_ReturnsNotFound()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, _) = await SeedBookWithChapterAsync(db);

        var controller = BuildController(provider);
        var action = await controller.GetChapterSummary(bookId, Guid.NewGuid(), "he", CancellationToken.None);
        Assert.IsType<NotFoundResult>(action.Result);
    }

    // ─── PUT: saves the flat edit + sets the flag + does NOT orphan the structured surface ──────────

    [Fact]
    public async Task UpdateChapterSummary_SetsFlatEdit_AndFlag_AndDoesNotTouchStructuredSurface()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        var originalBuiltAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "AI summary.",
            StructuredJson = StructuredBriefJson,
            StructuredBuiltAt = originalBuiltAt,
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.UpdateChapterSummary(
            bookId, chapterId, new UpdateChapterSummaryRequest("My own understanding.", "he"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ChapterSummaryViewDto>(ok.Value);
        Assert.Equal("My own understanding.", dto.SummaryText);
        Assert.True(dto.SummaryUserEdited);
        Assert.NotNull(dto.SummaryUserEditedAt);

        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal("My own understanding.", row.SummaryText);
        Assert.True(row.SummaryUserEdited);
        Assert.NotNull(row.SummaryUserEditedAt);
        // Dual-surface: the structured surface is NOT orphaned — same JSON, same build stamp, same model.
        Assert.Equal(StructuredBriefJson, row.StructuredJson);
        Assert.Equal(originalBuiltAt, row.StructuredBuiltAt);
        Assert.Equal(ActiveModel, row.BuiltWithModel);
    }

    [Fact]
    public async Task UpdateChapterSummary_NoExistingRow_CreatesUserEditedRow()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        var controller = BuildController(provider);
        var action = await controller.UpdateChapterSummary(
            bookId, chapterId, new UpdateChapterSummaryRequest("First-ever summary.", "he"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);

        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal("First-ever summary.", row.SummaryText);
        Assert.True(row.SummaryUserEdited);
        Assert.NotNull(row.SummaryUserEditedAt);
        // No structured surface yet (a re-derive builds it).
        Assert.True(string.IsNullOrEmpty(row.StructuredJson));
    }

    [Fact]
    public async Task UpdateChapterSummary_MissingBody_ReturnsBadRequest()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        var controller = BuildController(provider);
        var action = await controller.UpdateChapterSummary(
            bookId, chapterId, new UpdateChapterSummaryRequest(null, "he"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    [Fact]
    public async Task UpdateChapterSummary_WhitespaceOnlyBody_ReturnsBadRequest_AndDoesNotSetUserEditedFlag()
    {
        // DECISION: whitespace-only is rejected identically to null — there is no "delete summary" UX,
        // and saving "" with SummaryUserEdited=true would permanently brick auto-resummary for the chapter.
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        var controller = BuildController(provider);
        var action = await controller.UpdateChapterSummary(
            bookId, chapterId, new UpdateChapterSummaryRequest("   ", "he"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);

        // No row should have been created/modified with SummaryUserEdited = true.
        var row = await db.ChunkSummaries.AsNoTracking().FirstOrDefaultAsync(cs => cs.ChapterId == chapterId);
        Assert.True(row == null || !row.SummaryUserEdited,
            "A whitespace-only PUT must not create or flip a user-edited row.");
    }

    // ─── Clobber guard: the REAL automatic flat re-summary SKIPS a user-edited row ──────────────────

    [Fact]
    public async Task SummarizeChaptersAsync_SkipsUserEditedRow_PreservesManualEdit()
    {
        using var provider = BuildProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db, chapterText: "תוכן הפרק.");

        // The user edited this chapter's flat summary. Its CreatedAt is OLD (before the chapter's UpdatedAt)
        // so the AI freshness check would normally re-summarize — but the user-edit guard must win.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "USER EDIT — do not overwrite.",
            SummaryUserEdited = true,
            SummaryUserEditedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Push the row's CreatedAt to the past so the AI freshness gate (CreatedAt >= UpdatedAt) would FAIL
        // and the loop would, absent the guard, re-summarize and overwrite.
        var row = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapterId);
        db.Entry(row).Property(r => r.CreatedAt).CurrentValue = DateTimeOffset.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();

        var intelligence = provider.GetRequiredService<BookIntelligenceService>();
        await intelligence.SummarizeChaptersAsync(bookId, "he");

        var after = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal("USER EDIT — do not overwrite.", after.SummaryText); // preserved
        Assert.True(after.SummaryUserEdited);

        // The flat (non-JsonMode) re-summary LLM call was NEVER issued for the guarded chapter.
        Assert.DoesNotContain(
            routerMock.Invocations,
            i => i.Method.Name == nameof(IAiRouter.CompleteAsync)
                 && ((AiRequest)i.Arguments[0]).TaskType == AiTaskType.Summarization
                 && !((AiRequest)i.Arguments[0]).JsonMode);
    }

    // ─── Re-derive: incorporates the user's edited summary as authoritative input ───────────────────

    [Fact]
    public async Task RederiveChapterSummary_SeedsStructuredBriefWithUserSummary_AndPreservesFlatEdit()
    {
        using var provider = BuildProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db, chapterText: "תוכן הפרק לרקע.");

        const string userSummary = "THE-USER-AUTHORITATIVE-SUMMARY-SENTINEL";
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = userSummary,
            SummaryUserEdited = true,
            SummaryUserEditedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.RederiveChapterSummary(
            bookId, chapterId, new RederiveChapterSummaryRequest("he"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var resp = Assert.IsType<RederiveChapterSummaryResponse>(ok.Value);
        Assert.True(resp.Rederived);
        Assert.True(resp.HasStructuredBrief);
        Assert.NotNull(resp.StructuredBuiltAt);

        // The model's instruction carried the user's authoritative summary (the seeding lever).
        Assert.Contains(
            routerMock.Invocations,
            i => i.Method.Name == nameof(IAiRouter.CompleteAsync)
                 && ((AiRequest)i.Arguments[0]).TaskType == AiTaskType.Summarization
                 && ((AiRequest)i.Arguments[0]).JsonMode
                 && (((AiRequest)i.Arguments[0]).Instruction ?? string.Empty).Contains(userSummary));

        // The structured surface was built; the flat user edit + flag are preserved (dual-surface).
        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.False(string.IsNullOrWhiteSpace(row.StructuredJson));
        Assert.Equal(userSummary, row.SummaryText);
        Assert.True(row.SummaryUserEdited);
        Assert.Equal(ActiveModel, row.BuiltWithModel);
    }

    [Fact]
    public async Task RederiveChapterSummary_NoUserEdit_ReturnsConflict()
    {
        using var provider = BuildProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        // Row exists but is NOT user-edited (AI-only) → re-derive has nothing authoritative to seed from.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "AI-only summary.",
            SummaryUserEdited = false
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.RederiveChapterSummary(
            bookId, chapterId, new RederiveChapterSummaryRequest("he"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(action.Result);
        // No model call on the conflict path.
        Assert.Empty(routerMock.Invocations);
    }

    // ─── (a) Graceful-miss: rederived=false when the model yields no usable brief ─────────────────

    /// <summary>
    /// be-f02-a: when the model returns no usable brief (empty/null response that ChapterBriefService
    /// parses as null), RederiveChapterSummary must return rederived=false with the graceful-miss
    /// message, AND HasStructuredBrief must reflect the unchanged row (prior StructuredJson, if any,
    /// is untouched; the flat edit + SummaryUserEdited flag are also preserved).
    ///
    /// Revert-verified: replaced the empty-response mock with StructuredBriefJson → rederived became
    /// true and the assertion failed, proving the test exercises the real branch.
    /// </summary>
    [Fact]
    public async Task RederiveChapterSummary_ModelYieldsNoUsableBrief_ReturnsFalseAndPreservesRow()
    {
        // Arrange a router that returns empty content for every call (including the JsonMode structured
        // call) so ComputeChapterBriefAsync → ExtractJson returns null → RederiveChapterBriefFromUserSummaryAsync
        // returns null → the controller sets rederived=false.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = "", Model = ActiveModel, Provider = "test-provider" });

        services.AddSingleton(routerMock.Object);
        services.Configure<AiOptions>(_ => { });
        services.Configure<Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions>(_ => { });
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<SuggestionDiffService>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker>();
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<AnalysisRepairService>();
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();

        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db, chapterText: "תוכן הפרק.");

        const string userSummary = "USER-EDITED-SUMMARY";
        const string priorStructuredJson = StructuredBriefJson; // an existing structured brief from a prior build
        var priorBuiltAt = DateTimeOffset.UtcNow.AddHours(-1);
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = userSummary,
            SummaryUserEdited = true,
            SummaryUserEditedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            // Pre-existing structured brief; must survive the graceful miss unchanged.
            StructuredJson = priorStructuredJson,
            StructuredBuiltAt = priorBuiltAt,
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.RederiveChapterSummary(
            bookId, chapterId, new RederiveChapterSummaryRequest("he"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var resp = Assert.IsType<RederiveChapterSummaryResponse>(ok.Value);

        // Core assertion: rederived=false with the graceful-miss message.
        Assert.False(resp.Rederived);
        Assert.Contains("your edit was saved", resp.Message, StringComparison.OrdinalIgnoreCase);

        // The prior StructuredJson must be UNCHANGED (HasStructuredBrief reflects the pre-existing brief).
        Assert.True(resp.HasStructuredBrief,
            "The pre-existing structured brief must still be reflected as present after a graceful miss");

        // DB row: flat edit + SummaryUserEdited preserved; StructuredJson NOT overwritten by the failed attempt.
        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal(userSummary, row.SummaryText);
        Assert.True(row.SummaryUserEdited);
        Assert.Equal(priorStructuredJson, row.StructuredJson);
    }

    // ─── be-c01: a DEGENERATE (empty/zero-content) brief must NOT overwrite a good one ────────────

    /// <summary>
    /// be-c01-degenerate-brief-guard (Phase 4c-16): the model returns a parseable-but-EMPTY structured
    /// payload (the num_ctx-truncation failure on an 8GB GPU). StructuredChunkSummaryParser.Parse returns a
    /// NON-null record for such a payload, so absent the degenerate guard ComputeChapterBriefAsync would treat
    /// it as a success and RederiveChapterBriefFromUserSummaryAsync would OVERWRITE the previously-good
    /// StructuredJson with the empty brief, reporting rederived=true. The guard makes it a FAILURE: re-derive
    /// returns null → the controller reports rederived=false and the prior StructuredJson is preserved.
    ///
    /// Theory covers both empty shapes: "{}" (all collections default to empty) and the explicit
    /// all-empty-arrays object.
    ///
    /// Revert-verified: with the IsDegenerate guard removed (// TEMP-REVERT in ChapterBriefService
    /// .ComputeChapterBriefAsync), both rows reported rederived=true and StructuredJson was overwritten with
    /// the empty payload, so both assertions failed; restoring the guard returns both to green.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"plotEvents":[],"characterStates":[],"thematicMarkers":[],"openThreads":[],"toneNotes":""}""")]
    public async Task RederiveChapterSummary_DegenerateBrief_DoesNotOverwriteGoodBrief_ReportsFalse(
        string degeneratePayload)
    {
        // Router returns the DEGENERATE payload for the JsonMode structured (re-derive) call.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = degeneratePayload, Model = ActiveModel, Provider = "test-provider" });

        services.AddSingleton(routerMock.Object);
        services.Configure<AiOptions>(_ => { });
        services.Configure<Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions>(_ => { });
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<SuggestionDiffService>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker>();
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<AnalysisRepairService>();
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();

        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db, chapterText: "תוכן הפרק.");

        const string userSummary = "USER-EDITED-SUMMARY";
        const string priorStructuredJson = StructuredBriefJson; // a GOOD structured brief from a prior build
        var priorBuiltAt = DateTimeOffset.UtcNow.AddHours(-1);
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = userSummary,
            SummaryUserEdited = true,
            SummaryUserEditedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            StructuredJson = priorStructuredJson,
            StructuredBuiltAt = priorBuiltAt,
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.RederiveChapterSummary(
            bookId, chapterId, new RederiveChapterSummaryRequest("he"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var resp = Assert.IsType<RederiveChapterSummaryResponse>(ok.Value);

        // The degenerate brief is a FAILURE, not a success: graceful-miss contract.
        Assert.False(resp.Rederived);
        Assert.Contains("your edit was saved", resp.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(resp.HasStructuredBrief, "the pre-existing good brief must still be present");

        // The previously-good StructuredJson must be UNCHANGED — never overwritten by the empty payload.
        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal(priorStructuredJson, row.StructuredJson);
        Assert.Equal(priorBuiltAt, row.StructuredBuiltAt);
        // The flat user edit + flag are likewise preserved.
        Assert.Equal(userSummary, row.SummaryText);
        Assert.True(row.SummaryUserEdited);
    }

    // ─── (b) Book-404: GET / PUT / rederive all return NotFound for an unknown bookId ─────────────

    /// <summary>
    /// be-f02-b: GET, PUT, and rederive each guard against a missing BOOK (BooksController.cs:784,
    /// 813, 874) and return 404. The existing tests only cover the unknown-CHAPTER case; this adds
    /// the book-missing case for all three verbs.
    ///
    /// Revert-verified (GET): removing the book-existence check from GetChapterSummary turned the
    /// assertion from NotFound to OkObjectResult and the test failed.
    /// </summary>
    [Fact]
    public async Task GetChapterSummary_UnknownBook_ReturnsNotFound()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        // Seed a chapter under a REAL book so the chapter lookup would not interfere — the book-id
        // we pass in the request is a fresh unknown Guid.
        var (_, chapterId) = await SeedBookWithChapterAsync(db);

        var controller = BuildController(provider);
        var action = await controller.GetChapterSummary(
            Guid.NewGuid(), chapterId, "he", CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public async Task UpdateChapterSummary_UnknownBook_ReturnsNotFound()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (_, chapterId) = await SeedBookWithChapterAsync(db);

        var controller = BuildController(provider);
        var action = await controller.UpdateChapterSummary(
            Guid.NewGuid(), chapterId, new UpdateChapterSummaryRequest("some text", "he"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    [Fact]
    public async Task RederiveChapterSummary_UnknownBook_ReturnsNotFound()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (_, chapterId) = await SeedBookWithChapterAsync(db);

        var controller = BuildController(provider);
        var action = await controller.RederiveChapterSummary(
            Guid.NewGuid(), chapterId, new RederiveChapterSummaryRequest("he"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    // ─── (c) DbUpdateException detach branch in RederiveChapterBriefFromUserSummaryAsync ──────────

    /// <summary>
    /// be-f02-c: when SaveChangesAsync throws DbUpdateException inside
    /// ChapterBriefService.RederiveChapterBriefFromUserSummaryAsync, the method must detach the modified
    /// entity so no tracked-Modified ChunkSummary is left on the scoped DbContext, AND it must return null
    /// (NOT the computed brief): the re-derive's whole purpose is to persist the structured brief, so an
    /// unpersisted brief is a failure and the controller's rederived flag (brief != null) must mirror the
    /// database — reporting success on an unsaved row was the bug.
    ///
    /// Uses the same ThrowOnSaveDbContext pattern established in
    /// ChapterStyleProfileAndLinguisticTests.LoadOrBuildChapterStyleProfileAsync_StaleRefreshSaveFails_*
    /// to simulate a DbUpdateException without needing a real SQL Server constraint.
    ///
    /// Revert-verified: removing the Detach line (_db.Entry(existing).State = EntityState.Detached)
    /// from ChapterBriefService.cs left the entity in Modified state and the DoesNotContain assertion
    /// failed; reverting the `return null` to a fall-through `return brief` failed the Assert.Null.
    /// </summary>
    [Fact]
    public async Task RederiveChapterBriefFromUserSummaryAsync_SaveFails_DetachesEntityAndReturnsNull()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ThrowOnSaveDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Detach Test Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId, BookId = bookId, Order = 0, Title = "Ch1",
            ContentText = "תוכן הפרק."
        });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "USER-SUMMARY-SEED",
            SummaryUserEdited = true,
            SummaryUserEditedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        // Initial seed uses normal saves (ThrowOnSave is still false here).
        await db.SaveChangesAsync();

        // Wire the router to return valid structured JSON so the build path runs to the save.
        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse
            {
                Content = StructuredBriefJson,
                Model = ActiveModel,
                Provider = "test-provider"
            });

        var aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions());
        var scopeFactory = new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var svc = new ChapterBriefService(
            db, routerMock.Object, new PromptFactory(), aiOptions, scopeFactory,
            NullLogger<ChapterBriefService>.Instance);

        // Arm the throwing context AFTER the seed writes succeed.
        db.ThrowOnSave = true;

        var brief = await svc.RederiveChapterBriefFromUserSummaryAsync(bookId, chapterId, "he");

        // The persist failed, so the re-derive did NOT achieve its purpose (updating the DB row the
        // whole-book review reads). The method must return null so the controller reports rederived=false
        // rather than claiming success on a row that was never saved.
        Assert.Null(brief);

        // No tracked-Modified ChunkSummary must remain: the catch detached the entity so a later
        // SaveChanges on the same scoped context is not poisoned by the pending change.
        Assert.DoesNotContain(
            db.ChangeTracker.Entries<ChunkSummary>(),
            e => e.State == EntityState.Modified);

        // Confirm the context is unblocked: a subsequent SaveChanges with ThrowOnSave=false succeeds.
        db.ThrowOnSave = false;
        var saveEx = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Null(saveEx);
    }

    // ─── be-c02: language-flip guard (mirror the load path; reconcile the stale surface) ─────────────

    /// <summary>
    /// be-c02-language-flip-guard (PUT path): when a PUT flips the row's Language (e.g. the book language
    /// changed after the structured brief was built), the EXISTING structured brief was built under the OLD
    /// language and would masquerade as the new locale's brief — BookContextAssembler selects flat/structured
    /// surfaces by Language only. Mirroring LoadOrBuildChapterBriefAsync's flip handling, the PUT must clear the
    /// now-stale structured surface (StructuredJson / StructuredBuiltAt / BuiltWithModel) so it cannot leak the
    /// wrong language, while still saving the user's new flat edit (which IS in the new locale).
    ///
    /// Revert-verified: with the language-flip block removed from UpdateChapterSummary (// TEMP-REVERT), the
    /// old-language StructuredJson survived the flip and the StructuredJson-null assertion failed; restoring
    /// the guard returns it to green.
    /// </summary>
    [Fact]
    public async Task UpdateChapterSummary_LanguageFlipOnPopulatedRow_ClearsStaleStructuredSurface()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        // Existing row built in Hebrew with a structured brief.
        var originalBuiltAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "סיכום עברית.",
            SummaryUserEdited = true,
            SummaryUserEditedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            StructuredJson = StructuredBriefJson,
            StructuredBuiltAt = originalBuiltAt,
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        // PUT an English edit — flips the row's Language he -> en.
        var controller = BuildController(provider);
        var action = await controller.UpdateChapterSummary(
            bookId, chapterId, new UpdateChapterSummaryRequest("My English summary.", "en"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);

        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        // The user's new flat edit is saved in the new locale (never dropped).
        Assert.Equal("My English summary.", row.SummaryText);
        Assert.Equal("en", row.Language);
        Assert.True(row.SummaryUserEdited);
        // The OLD-language structured brief is cleared so it cannot leak as the new locale's brief.
        Assert.True(string.IsNullOrEmpty(row.StructuredJson),
            "A language flip must clear the now-stale structured surface (built under the old language).");
        Assert.Null(row.StructuredBuiltAt);
        Assert.Null(row.BuiltWithModel);
    }

    /// <summary>
    /// be-c02-language-flip-guard (PUT same-language path): a PUT that does NOT flip the Language must leave the
    /// structured surface untouched (the dual-surface contract from
    /// UpdateChapterSummary_SetsFlatEdit_AndFlag_AndDoesNotTouchStructuredSurface is preserved by the guard).
    /// This pins that the new flip block fires ONLY on a flip, not on every PUT.
    /// </summary>
    [Fact]
    public async Task UpdateChapterSummary_SameLanguage_LeavesStructuredSurfaceIntact()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db);

        var originalBuiltAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = "סיכום עברית.",
            StructuredJson = StructuredBriefJson,
            StructuredBuiltAt = originalBuiltAt,
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.UpdateChapterSummary(
            bookId, chapterId, new UpdateChapterSummaryRequest("סיכום מעודכן.", "he"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);

        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal("he", row.Language);
        // Same-language PUT: structured surface is NOT orphaned.
        Assert.Equal(StructuredBriefJson, row.StructuredJson);
        Assert.Equal(originalBuiltAt, row.StructuredBuiltAt);
        Assert.Equal(ActiveModel, row.BuiltWithModel);
    }

    /// <summary>
    /// be-c02-language-flip-guard (re-derive path): when a re-derive flips the row's Language, the flat
    /// SummaryText it seeded from is in the OLD language and would masquerade as the new locale's prose once
    /// Language flips (BookContextAssembler selects flat fallbacks by Language only). Mirroring
    /// LoadOrBuildChapterBriefAsync's flip handling, RederiveChapterBriefFromUserSummaryAsync must clear the
    /// now-stale flat summary (and its user-edit flag, which is meaningless once the prose no longer matches the
    /// locale) while persisting the freshly built structured brief in the new locale.
    ///
    /// Revert-verified: with the language-flip block removed from RederiveChapterBriefFromUserSummaryAsync
    /// (// TEMP-REVERT), the Hebrew flat SummaryText survived under Language="en" and the SummaryText-cleared
    /// assertion failed; restoring the guard returns it to green.
    /// </summary>
    [Fact]
    public async Task RederiveChapterSummary_LanguageFlipOnPopulatedRow_ClearsStaleFlatSummary()
    {
        using var provider = BuildProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookWithChapterAsync(db, chapterText: "תוכן הפרק לרקע.");

        const string oldLocaleSummary = "סיכום עברית של המשתמש.";
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            SummaryText = oldLocaleSummary,
            SummaryUserEdited = true,
            SummaryUserEditedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        // Re-derive requested in English — flips the row's Language he -> en. The router mock returns a usable
        // structured brief for the JsonMode call, so the build succeeds and the row is persisted.
        var controller = BuildController(provider);
        var action = await controller.RederiveChapterSummary(
            bookId, chapterId, new RederiveChapterSummaryRequest("en"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var resp = Assert.IsType<RederiveChapterSummaryResponse>(ok.Value);
        Assert.True(resp.Rederived);

        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        Assert.Equal("en", row.Language);
        // The freshly built structured brief is persisted in the new locale.
        Assert.False(string.IsNullOrWhiteSpace(row.StructuredJson));
        // The OLD-language flat prose is cleared so it cannot leak as the new locale's summary.
        Assert.True(string.IsNullOrEmpty(row.SummaryText),
            "A language flip must clear the now-stale flat summary (in the old language).");
        Assert.False(row.SummaryUserEdited);
        Assert.Null(row.SummaryUserEditedAt);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(Guid bookId, Guid chapterId)> SeedBookWithChapterAsync(
        AppDbContext db, string chapterText = "תוכן.")
    {
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Summary Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Order = 0, Title = "Ch1", ContentText = chapterText });
        await db.SaveChangesAsync();
        return (bookId, chapterId);
    }

    private static BooksController BuildController(ServiceProvider provider) => new(
        db: provider.GetRequiredService<AppDbContext>(),
        bookIntelligence: provider.GetRequiredService<BookIntelligenceService>(),
        styleBaseline: null!,
        bookSummary: provider.GetRequiredService<BookSummaryService>(),
        bookReview: null!,
        chapterBrief: provider.GetRequiredService<ChapterBriefService>(),
        progress: provider.GetRequiredService<AnalysisProgressTracker>(),
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        appLifetime: new TestApplicationLifetime(),
        logger: NullLogger<BooksController>.Instance);

    /// <summary>
    /// DI provider wiring the real flat path (BookIntelligenceService + its analysis graph) AND the
    /// structured builder (ChapterBriefService), so the clobber guard runs the GENUINE re-summary loop and
    /// the re-derive runs the GENUINE structured build. The router mock distinguishes the structured
    /// (JsonMode Summarization) call from the flat one and returns deterministic content for each; its
    /// Invocations are asserted for the seeded instruction. No live model is contacted.
    /// </summary>
    private static ServiceProvider BuildProvider(out Mock<IAiRouter> routerMock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                var isStructured = req.TaskType == AiTaskType.Summarization && req.JsonMode;
                return new AiResponse
                {
                    Content = isStructured ? StructuredBriefJson : "A short flat natural-language summary.",
                    Model = ActiveModel,
                    Provider = "test-provider"
                };
            });

        services.AddSingleton(routerMock.Object);
        services.Configure<AiOptions>(_ => { });
        services.Configure<Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions>(_ => { });

        // Shared infra (mirrors ChapterBriefServiceTests.BuildFlatAndStructuredProvider + Program.cs).
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<SuggestionDiffService>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker>();

        // Structured + flat analysis graph.
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<AnalysisRepairService>();
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();

        return services.BuildServiceProvider();
    }

    private sealed class TestApplicationLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>
    /// AppDbContext whose SaveChangesAsync can be made to throw DbUpdateException, mirroring the
    /// identical helper in ChapterStyleProfileAndLinguisticTests. Used for be-f02-c.
    /// </summary>
    private sealed class ThrowOnSaveDbContext : AppDbContext
    {
        public bool ThrowOnSave { get; set; }

        public ThrowOnSaveDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => ThrowOnSave
                ? throw new DbUpdateException("Simulated re-derive save failure")
                : base.SaveChangesAsync(cancellationToken);
    }
}
