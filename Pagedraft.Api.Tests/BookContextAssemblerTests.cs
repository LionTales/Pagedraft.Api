using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
/// Tests for <see cref="BookContextAssembler"/> (wb1-c03): the SINGLE budget-aware assembler that replaces
/// the previously unguarded whole-book concat (AnalysisContextService.ResolveBookAsync appended every
/// chapter's full text; BookIntelligenceService.GetConcatenatedSummaries appended every flat summary) so a
/// large book can no longer silently overflow the model context window.
///
/// Covers: (a) budget cap respected — assembled context stays within the configured token budget; (b)
/// dropped-units reported — overflow is recorded in DroppedUnits, not silently cut; (c) degradation path —
/// briefs absent → flat-summary fallback still works AND is budget-guarded; (d) big-book no-overflow — a
/// many-chapter book whose full content would overflow the unguarded path assembles within budget AND keeps
/// the BookBrief present. Mirrors the BookSummaryServiceTests / ChapterBriefServiceTests fixed-name
/// in-memory-DB conventions so the assembler's child reads (via BookSummaryService) share one store.
/// </summary>
public class BookContextAssemblerTests
{
    // A structured L0 brief the assembler's L1 projection reads (camelCase, matches StructuredChunkSummaryData).
    // Each chapter gets a distinct, moderately sized brief so the union is observable and tokens accumulate.
    private static string BriefJson(int n) => $$"""
        {
          "plotEvents": ["Chapter {{n}} event alpha that advances the plot", "Chapter {{n}} event beta with consequences"],
          "characterStates": [ { "name": "Dana", "state": "in motion during chapter {{n}}", "emotionalArc": "fear to resolve" } ],
          "thematicMarkers": ["isolation-{{n}}", "rebirth"],
          "toneNotes": "tense and foreboding throughout chapter {{n}}",
          "openThreads": ["who sent the letter in chapter {{n}}?"]
        }
        """;

    // ─── (a) Budget cap respected: assembled context stays within the configured budget ──────────────

    [Fact]
    public async Task AssembleAsync_StructuredBriefs_StaysWithinConfiguredBudget()
    {
        var dbName = Guid.NewGuid().ToString();
        // Small explicit budget so only a few briefs fit.
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 400);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Budget Book", Language = "he" });
        for (var i = 0; i < 12; i++)
            SeedChapterWithBrief(db, bookId, order: i, title: $"Ch{i}", briefJson: BriefJson(i));
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        Assert.True(result.UsedStructuredBriefs);
        Assert.Equal(400, result.BudgetTokens);
        // The whole point: assembled tokens never exceed the budget.
        Assert.True(result.EstimatedTokens <= result.BudgetTokens,
            $"assembled {result.EstimatedTokens}t must be <= budget {result.BudgetTokens}t");
        // And an independent re-estimate of the actual Text agrees.
        Assert.True(BookContextAssembler.EstimateTokens(result.Text) <= result.BudgetTokens);
        // BookBrief is always present in the structured path.
        Assert.NotNull(result.BookBrief);
        Assert.Contains("[BOOK_CONTEXT]", result.Text);
        // Not everything fit at this tight budget, so some chapters were necessarily dropped.
        Assert.True(result.IncludedChapterBriefs.Count < 12);
        Assert.True(result.DroppedCount > 0);
    }

    // ─── (b) Dropped-units reported: overflow is recorded, not silently cut ──────────────────────────

    [Fact]
    public async Task AssembleAsync_OverBudget_ReportsDroppedUnits_PreservingNarrativeOrder()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 350);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Drop Book", Language = "he" });
        for (var i = 0; i < 10; i++)
            SeedChapterWithBrief(db, bookId, order: i, title: $"Ch{i}", briefJson: BriefJson(i));
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        // Some included, some dropped; together they account for ALL chapters (nothing vanishes silently).
        Assert.True(result.DroppedCount > 0);
        Assert.Equal(10, result.IncludedChapterBriefs.Count + result.DroppedCount);

        // Narrative order preserved: the included prefix is the lowest Orders, the dropped tail the highest.
        var includedOrders = result.IncludedChapterBriefs.Select(b => b.Order).ToList();
        var droppedOrders = result.DroppedUnits.Select(d => d.Order).ToList();
        Assert.Equal(includedOrders.OrderBy(o => o), includedOrders); // included are ascending
        // Every dropped order is greater than every included order (contiguous prefix kept).
        if (includedOrders.Count > 0)
            Assert.True(droppedOrders.All(d => d > includedOrders.Max()));
        // Dropped units carry their title + an estimated cost for diagnostics.
        Assert.All(result.DroppedUnits, d => Assert.False(string.IsNullOrWhiteSpace(d.Title)));
        Assert.All(result.DroppedUnits, d => Assert.True(d.EstimatedTokens > 0));
    }

    // ─── (c) Degradation: briefs absent → flat-summary fallback works AND is budget-guarded ──────────

    [Fact]
    public async Task AssembleAsync_NoStructuredBriefs_FlatSummaryFallback_IsBudgetGuarded()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 300);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Flat Book", Language = "he" });
        // Flat summaries only (no StructuredJson) → the structured path finds nothing → flat fallback.
        for (var i = 0; i < 10; i++)
        {
            var chId = Guid.NewGuid();
            db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = i, Title = $"Ch{i}", ContentText = $"raw {i}" });
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chId, Language = "he",
                // Substantial flat summaries so a 300-token budget meaningfully bites (only a few fit).
                SummaryText = $"Flat summary number {i}: " +
                    string.Concat(Enumerable.Repeat($"the chapter recounts events, characters, and turns {i}. ", 10)),
                StructuredJson = null
            });
        }
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        // Degraded path: no structured briefs used, no BookBrief, but STILL budget-guarded + reports drops.
        Assert.False(result.UsedStructuredBriefs);
        Assert.Null(result.BookBrief);
        Assert.True(result.EstimatedTokens <= result.BudgetTokens);
        Assert.True(BookContextAssembler.EstimateTokens(result.Text) <= result.BudgetTokens);
        Assert.True(result.DroppedCount > 0, "tight budget over 10 flat summaries should drop some");
        Assert.True(result.DroppedCount < 10, "some flat summaries should have fit under the budget");
        // The flat framing is used (and at least one unit made it into the text).
        Assert.Contains("פרק / Chapter:", result.Text);
    }

    [Fact]
    public async Task AssembleAsync_NoSummariesAtAll_FallsBackToRawTextStillBudgetGuarded()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 300);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Raw Book", Language = "he" });
        // No ChunkSummaries at all → last-resort raw chapter text, still trimmed to budget.
        for (var i = 0; i < 8; i++)
            db.Chapters.Add(new Chapter
            {
                Id = Guid.NewGuid(), BookId = bookId, Order = i, Title = $"Ch{i}",
                ContentText = string.Concat(Enumerable.Repeat($"raw chapter {i} body text. ", 40))
            });
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        Assert.False(result.UsedStructuredBriefs);
        Assert.True(result.EstimatedTokens <= result.BudgetTokens);
        Assert.True(BookContextAssembler.EstimateTokens(result.Text) <= result.BudgetTokens);
        Assert.True(result.DroppedCount > 0);
    }

    // ─── (d) Big-book no-overflow: a book whose FULL content would overflow stays within budget ──────

    [Fact]
    public async Task AssembleAsync_BigBook_FullContentWouldOverflow_StaysWithinBudgetAndKeepsBookBrief()
    {
        var dbName = Guid.NewGuid().ToString();
        // Derive the budget from NumCtx (do NOT set an explicit budget) so this exercises the real
        // production derivation. Summarization NumCtx default below is 8192; fraction default 0.5 → 4096t.
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 0,
            ollamaSummarizationNumCtx: 8192, budgetFraction: 0.5);
        var db = provider.GetRequiredService<AppDbContext>();
        var asm = provider.GetRequiredService<BookContextAssembler>();
        var expectedBudget = asm.ResolveBudgetTokens();
        Assert.Equal(4096, expectedBudget); // 8192 * 0.5

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Big Book", Language = "he" });

        // 200 chapters, each with a SUBSTANTIAL brief. Build the equivalent raw concat to PROVE the
        // unguarded path would have blown the budget many times over.
        const int chapterCount = 200;
        var rawConcatTokens = 0;
        for (var i = 0; i < chapterCount; i++)
        {
            var brief = BigBriefJson(i);
            SeedChapterWithBrief(db, bookId, order: i, title: $"Chapter {i}", briefJson: brief,
                // a big raw body too, so the OLD ResolveBookAsync concat would be enormous
                rawBody: string.Concat(Enumerable.Repeat($"Long narrative body for chapter {i}. ", 100)));
            rawConcatTokens += BookContextAssembler.EstimateTokens(brief);
        }
        await db.SaveChangesAsync();

        // Sanity: the sum of all briefs alone DWARFS the budget — i.e. the unguarded path overflows.
        Assert.True(rawConcatTokens > expectedBudget * 3,
            $"test must be genuinely large: all-briefs {rawConcatTokens}t should far exceed budget {expectedBudget}t");

        var result = await asm.AssembleAsync(bookId, "he");

        // The assembled context stays within budget DESPITE the overflowing input.
        Assert.True(result.EstimatedTokens <= expectedBudget,
            $"assembled {result.EstimatedTokens}t must be <= budget {expectedBudget}t");
        Assert.True(BookContextAssembler.EstimateTokens(result.Text) <= expectedBudget);
        // BookBrief is ALWAYS present.
        Assert.NotNull(result.BookBrief);
        Assert.Contains("[BOOK_CONTEXT]", result.Text);
        // Most of the 200 chapters were necessarily dropped, and that is reported (no silent truncation).
        Assert.True(result.DroppedCount > 0);
        Assert.Equal(chapterCount, result.IncludedChapterBriefs.Count + result.DroppedCount);
        // The included briefs are the narrative-leading prefix.
        var includedOrders = result.IncludedChapterBriefs.Select(b => b.Order).ToList();
        Assert.Equal(includedOrders.OrderBy(o => o), includedOrders);
    }

    // ─── Budget derivation: explicit override wins; otherwise derived from NumCtx ────────────────────

    [Fact]
    public void ResolveBudgetTokens_ExplicitOverride_WinsOverNumCtxDerivation()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 1234,
            ollamaSummarizationNumCtx: 16384, budgetFraction: 0.5);
        var asm = provider.GetRequiredService<BookContextAssembler>();
        Assert.Equal(1234, asm.ResolveBudgetTokens());
    }

    [Fact]
    public void ResolveBudgetTokens_NoOverride_DerivesFromActiveModelNumCtx()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 0,
            ollamaSummarizationNumCtx: 16384, budgetFraction: 0.25);
        var asm = provider.GetRequiredService<BookContextAssembler>();
        Assert.Equal(4096, asm.ResolveBudgetTokens()); // 16384 * 0.25
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static string BigBriefJson(int n)
    {
        // A deliberately chunky brief so a few hundred chapters overflow any sane budget.
        var events = string.Join(", ",
            Enumerable.Range(0, 6).Select(k => $"\"Chapter {n} significant plot development number {k} with elaborate detail\""));
        return $$"""
            {
              "plotEvents": [{{events}}],
              "characterStates": [ { "name": "Dana", "state": "navigating chapter {{n}} complications at length", "emotionalArc": "an extended arc" } ],
              "thematicMarkers": ["isolation-{{n}}", "rebirth", "longing-{{n}}"],
              "toneNotes": "an extended tonal description for chapter {{n}} spanning several descriptive clauses",
              "openThreads": ["thread one for chapter {{n}}", "thread two for chapter {{n}}"]
            }
            """;
    }

    private static void SeedChapterWithBrief(
        AppDbContext db, Guid bookId, int order, string title, string briefJson, string? rawBody = null)
    {
        var chId = Guid.NewGuid();
        db.Chapters.Add(new Chapter
        {
            Id = chId, BookId = bookId, Order = order, Title = title,
            ContentText = rawBody ?? $"raw body for {title}"
        });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chId, Language = "he",
            StructuredJson = briefJson, BuiltWithModel = "qwen2.5:14b"
        });
    }

    /// <summary>
    /// DI provider mirroring BookSummaryServiceTests' fixed-DB-name pattern. The IAiRouter is mocked but
    /// never invoked here: the assembler reads PERSISTED L0/summary rows through BookSummaryService's
    /// compose methods (pure projections, no LLM). Lets the test control budget + NumCtx via AiOptions.
    /// </summary>
    private static ServiceProvider BuildProvider(
        string dbName,
        int bookContextTokenBudget,
        int ollamaSummarizationNumCtx = 8192,
        double budgetFraction = 0.5)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = "{}", Model = "qwen2.5:14b", Provider = "test" });
        services.AddSingleton(routerMock.Object);

        services.Configure<AiOptions>(o =>
        {
            o.BookContextTokenBudget = bookContextTokenBudget;
            o.BookContextBudgetFraction = budgetFraction;
            // The Summarization task model's NumCtx drives the derived budget (mirrors OllamaProvider's
            // "{provider}_{task}" tuning precedence). DefaultProvider is "Ollama"; FeatureModels unset, so
            // the Summarization task resolves to provider "Ollama" → key "Ollama_Summarization".
            o.ProviderSettings = new Dictionary<string, ProviderTuningOptions>
            {
                ["Ollama"] = new ProviderTuningOptions { NumCtx = 4096 },
                ["Ollama_Summarization"] = new ProviderTuningOptions { NumCtx = ollamaSummarizationNumCtx }
            };
        });

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();

        return services.BuildServiceProvider();
    }
}
