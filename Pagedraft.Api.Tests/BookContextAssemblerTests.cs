using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task AssembleAsync_StructuredBriefs_ChargesInterBlockSeparatorsAgainstBudget()
    {
        // Regression: the running budget total must charge the "\n\n" separators inserted between the
        // BookBrief and every ChapterBrief. Those separators are part of the emitted Text; if they are not
        // counted, EstimatedTokens reads <= budget while the real Text is larger (EstimateTokens(Text) >
        // EstimatedTokens) and can exceed the configured window. Use a generous budget so ALL briefs are
        // included (every inter-block separator present) and many, varied briefs so the separators accumulate
        // beyond what per-block rounding could incidentally absorb.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 1_000_000);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Separator Book", Language = "he" });
        const int chapterCount = 100;
        for (var i = 0; i < chapterCount; i++)
            SeedChapterWithBrief(db, bookId, order: i, title: $"Ch{i}", briefJson: BriefJson(i));
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        // All briefs fit at this budget, so every inter-block separator is present in Text.
        Assert.True(result.UsedStructuredBriefs);
        Assert.Equal(chapterCount, result.IncludedChapterBriefs.Count);
        Assert.Equal(0, result.DroppedCount);

        // The reported EstimatedTokens must be a valid UPPER BOUND on the actual assembled Text's token
        // estimate. Pre-fix this fails: the uncounted separators push EstimateTokens(Text) above EstimatedTokens.
        Assert.True(
            BookContextAssembler.EstimateTokens(result.Text) <= result.EstimatedTokens,
            $"EstimateTokens(Text)={BookContextAssembler.EstimateTokens(result.Text)} must be <= " +
            $"EstimatedTokens={result.EstimatedTokens}: the inter-block separators must be charged.");
        // And the budget contract still holds against the real Text.
        Assert.True(BookContextAssembler.EstimateTokens(result.Text) <= result.BudgetTokens);
    }

    // ─── (a2) Partial structured coverage: uncovered chapters back-filled, never silently omitted ────

    [Fact]
    public async Task AssembleAsync_PartialStructuredCoverage_FillsUncoveredChaptersFromFlatFallback()
    {
        // Regression: when SOME chapters have a fresh structured brief the assembler takes the structured
        // path; chapters WITHOUT one must still reach the context (filled from their flat summary), not be
        // silently omitted. Generous budget so everything fits.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Partial Coverage", Language = "he" });

        // Chapter 0: FRESH structured brief → dense structured block.
        SeedChapterWithBrief(db, bookId, order: 0, title: "Covered", briefJson: BriefJson(0));

        // Chapter 1: NO structured brief, only a flat summary → must be back-filled, not omitted.
        var uncoveredId = Guid.NewGuid();
        db.Chapters.Add(new Chapter { Id = uncoveredId, BookId = bookId, Order = 1, Title = "Uncovered", ContentText = "raw body" });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = uncoveredId, Language = "he",
            SummaryText = "Flat summary for the uncovered chapter that must still reach the book context.",
            StructuredJson = null
        });
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        Assert.True(result.UsedStructuredBriefs);
        // The covered chapter contributes its structured brief...
        Assert.Single(result.IncludedChapterBriefs);
        Assert.Equal(0, result.IncludedChapterBriefs[0].Order);
        // ...and the uncovered chapter is NOT silently omitted: its flat summary is in the assembled context.
        Assert.Contains("Flat summary for the uncovered chapter", result.Text);
        // It fit the budget, so it is not recorded as dropped.
        Assert.Empty(result.DroppedUnits);
    }

    [Fact]
    public async Task AssembleAsync_PartialStructuredCoverage_NoFlatSummary_BackFillsFromRawText()
    {
        // An uncovered chapter with no flat summary falls back to its raw chapter text (last resort), so it
        // is still represented rather than dropped from the context.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Partial Raw", Language = "he" });
        SeedChapterWithBrief(db, bookId, order: 0, title: "Covered", briefJson: BriefJson(0));
        // Uncovered chapter: no ChunkSummary row at all → only raw text exists.
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "RawOnly",
            ContentText = "Distinctive raw chapter body that should back-fill the uncovered chapter."
        });
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        Assert.True(result.UsedStructuredBriefs);
        Assert.Single(result.IncludedChapterBriefs);
        Assert.Contains("Distinctive raw chapter body", result.Text);
        Assert.Empty(result.DroppedUnits);
    }

    [Fact]
    public async Task AssembleAsync_PartialCoverage_UncoveredChapterOverBudget_RecordedInDroppedUnits()
    {
        // The uncovered chapter's flat block is too large for the remaining budget: it must be RECORDED in
        // DroppedUnits (no silent omission), exactly like a chapter dropped for token budget.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 600);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Partial Tight", Language = "he" });

        // Covered chapter first (small structured brief that fits the budget).
        SeedChapterWithBrief(db, bookId, order: 0, title: "Covered", briefJson: BriefJson(0));

        // Uncovered chapter with a LARGE flat summary that cannot fit the remaining budget.
        var uncoveredId = Guid.NewGuid();
        db.Chapters.Add(new Chapter { Id = uncoveredId, BookId = bookId, Order = 1, Title = "Uncovered", ContentText = "raw" });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = uncoveredId, Language = "he",
            SummaryText = string.Concat(Enumerable.Repeat("flat overflow body. ", 400)), // ~8000 chars ≫ budget
            StructuredJson = null
        });
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        Assert.True(result.UsedStructuredBriefs);
        Assert.Single(result.IncludedChapterBriefs); // the covered chapter fit
        // The uncovered chapter did not fit → recorded as dropped (no silent omission), like a budget drop.
        Assert.Contains(result.DroppedUnits, d => d.Order == 1 && d.Title == "Uncovered");
        Assert.True(BookContextAssembler.EstimateTokens(result.Text) <= result.BudgetTokens);
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

    [Fact]
    public async Task AssembleAsync_FlatFallback_PartialCoverage_BackFillsUncoveredChaptersFromRawText()
    {
        // Regression: with NO fresh structured briefs the assembler takes the flat fallback. When only SOME
        // chapters have a flat summary, the uncovered ones (no summary row, or a blank one) must still reach
        // the context from their raw text — the prior code built its unit list only from non-blank summaries
        // and walked raw text for the whole book ONLY when there were zero summaries, so under partial flat
        // coverage the uncovered chapters were silently omitted (absent from Text AND DroppedUnits).
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Partial Flat", Language = "he" });

        // Chapter 0: has a flat summary (and a raw body); the flat summary wins.
        var ch0 = Guid.NewGuid();
        db.Chapters.Add(new Chapter { Id = ch0, BookId = bookId, Order = 0, Title = "HasSummary", ContentText = "raw zero body" });
        db.ChunkSummaries.Add(new ChunkSummary { BookId = bookId, ChapterId = ch0, Language = "he",
            SummaryText = "Flat summary for chapter zero.", StructuredJson = null });

        // Chapter 1: NO ChunkSummary row at all → only raw text.
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 1, Title = "NoRow",
            ContentText = "Distinctive raw body for chapter one." });

        // Chapter 2: ChunkSummary with a BLANK SummaryText → falls back to raw text.
        var ch2 = Guid.NewGuid();
        db.Chapters.Add(new Chapter { Id = ch2, BookId = bookId, Order = 2, Title = "BlankSummary",
            ContentText = "Distinctive raw body for chapter two." });
        db.ChunkSummaries.Add(new ChunkSummary { BookId = bookId, ChapterId = ch2, Language = "he",
            SummaryText = "   ", StructuredJson = null });

        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "he");

        Assert.False(result.UsedStructuredBriefs);
        // The chapter with a flat summary contributes it (flat wins over its own raw body)...
        Assert.Contains("Flat summary for chapter zero.", result.Text);
        Assert.DoesNotContain("raw zero body", result.Text);
        // ...and the uncovered chapters are NOT silently omitted: their raw text is included.
        Assert.Contains("Distinctive raw body for chapter one.", result.Text);
        Assert.Contains("Distinctive raw body for chapter two.", result.Text);
        Assert.Empty(result.DroppedUnits);
    }

    [Fact]
    public async Task AssembleAsync_FlatFallback_LegacyUnnormalizedLanguage_UsesSummaryNotRawText()
    {
        // Regression: a legacy flat summary persisted by SummarizeChaptersAsync under the RAW request locale
        // ("en-US"), while the assembler keys on the normalized locale ("en"). The prior exact-match selection
        // skipped the row and degraded to raw chapter text; the summary must instead be matched by NORMALIZED
        // locale and used.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Locale Book", Language = "en-US" });
        var chId = Guid.NewGuid();
        db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = 0, Title = "Ch0",
            ContentText = "Raw chapter body that should NOT be used when a summary exists." });
        // Stored under the RAW locale "en-US" (as the legacy flat path did), not the normalized "en".
        db.ChunkSummaries.Add(new ChunkSummary { BookId = bookId, ChapterId = chId, Language = "en-US",
            SummaryText = "Flat summary stored under the raw locale.", StructuredJson = null });
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();
        var result = await asm.AssembleAsync(bookId, "en"); // request the normalized locale

        Assert.False(result.UsedStructuredBriefs);
        // The legacy summary (stored "en-US") is matched for the normalized "en" locale and used...
        Assert.Contains("Flat summary stored under the raw locale.", result.Text);
        // ...instead of degrading to the raw chapter text.
        Assert.DoesNotContain("Raw chapter body", result.Text);
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

    // ─── (e) Flat fallback honours the requested language (no cross-language leak) ───────────────────

    [Fact]
    public async Task AssembleAsync_FlatFallback_DoesNotLeakOtherLanguageSummaries()
    {
        // Bug: the flat-summary fallback loaded ChunkSummaries by BookId ONLY, ignoring the requested
        // language. A book whose flat summaries exist solely in another language would have that foreign
        // prose concatenated into THIS locale's book context. The structured path already filters by
        // language, so the fallback must too.
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 4000);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Lang Book", Language = "he" });
        // Flat summaries ONLY in English; chapters carry NO raw body so the raw-text fallback yields nothing
        // either — isolating the language filter as the only thing that can surface text.
        for (var i = 0; i < 3; i++)
        {
            var chId = Guid.NewGuid();
            db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = i, Title = $"Ch{i}", ContentText = "" });
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chId, Language = "en",
                SummaryText = $"English summary for chapter {i} that must not leak into the Hebrew context."
            });
        }
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();

        // Requesting Hebrew: no he summaries, no raw bodies → the en summaries must NOT be concatenated.
        var he = await asm.AssembleAsync(bookId, "he");
        Assert.False(he.UsedStructuredBriefs);
        Assert.DoesNotContain("English summary", he.Text);
        Assert.True(string.IsNullOrWhiteSpace(he.Text), $"expected empty he context, got: '{he.Text}'");

        // Control: requesting English DOES surface them — proving the rows are present and only the
        // language filter (not their absence) kept them out of the Hebrew context.
        var en = await asm.AssembleAsync(bookId, "en");
        Assert.Contains("English summary", en.Text);
    }

    // ─── (f) Budget follows the CONSUMING task's context window, not always Summarization ────────────

    [Fact]
    public void ResolveBudgetTokens_DerivesFromConsumingTaskWindow_NotAlwaysSummarization()
    {
        // Bug: the budget was always derived from Summarization's NumCtx, but the assembled text is fed to
        // LinguisticAnalysis / GenericChat consumers whose per-task window can be SMALLER. Budgeting against
        // Summarization alone overflows the consumer's window (Ollama then silently truncates).
        var dbName = Guid.NewGuid().ToString();
        // Summarization window large (16384); the generic Ollama window — which GenericChat/QA falls back to
        // when there is no Ollama_GenericChat key — is small (4096). Fraction 0.5.
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 0,
            ollamaSummarizationNumCtx: 16384, budgetFraction: 0.5);
        var asm = provider.GetRequiredService<BookContextAssembler>();

        // Default (no task) and explicit Summarization both use the Summarization window: 16384 * 0.5.
        Assert.Equal(8192, asm.ResolveBudgetTokens());
        Assert.Equal(8192, asm.ResolveBudgetTokens(new[] { AiTaskType.Summarization }));

        // GenericChat (the QA route) has no Ollama_GenericChat key → falls back to the Ollama window (4096).
        // The derived budget now RESERVES output (2048) + prompt (1536) + margin (512); 4096 minus that
        // reservation is <= 0, so it floors to the 256 minimum (the tight fallback window leaves no room for
        // book context once output is reserved — QA in prod uses a 16384 window, unaffected).
        Assert.Equal(256, asm.ResolveBudgetTokens(new[] { AiTaskType.GenericChat }));

        // When several tasks share ONE assembly, budget to the SMALLEST window so it fits the tightest one.
        Assert.Equal(256, asm.ResolveBudgetTokens(new[] { AiTaskType.Summarization, AiTaskType.GenericChat }));
    }

    [Fact]
    public async Task AssembleAsync_BudgetsToConsumingTaskWindow()
    {
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 0,
            ollamaSummarizationNumCtx: 16384, budgetFraction: 0.5);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Consumer Budget", Language = "he" });
        for (var i = 0; i < 4; i++)
            SeedChapterWithBrief(db, bookId, order: i, title: $"Ch{i}", briefJson: BriefJson(i));
        await db.SaveChangesAsync();

        var asm = provider.GetRequiredService<BookContextAssembler>();

        // The consuming-task window flows through AssembleAsync to the assembly's BudgetTokens.
        var summarization = await asm.AssembleAsync(bookId, "he", new[] { AiTaskType.Summarization });
        Assert.Equal(8192, summarization.BudgetTokens); // 16384 * 0.5

        var genericChat = await asm.AssembleAsync(bookId, "he", new[] { AiTaskType.GenericChat });
        Assert.Equal(256, genericChat.BudgetTokens); // 4096 window minus output/prompt/margin reservation, floored
        Assert.True(genericChat.EstimatedTokens <= genericChat.BudgetTokens);
    }

    // ─── (g) Output-reserving budget + Hebrew-dense estimate (whole-book review truncation fix) ───────

    [Fact]
    public void CharsPerTokenForLanguage_HebrewIsDenser_ThanLatin()
    {
        Assert.Equal(4.0, BookContextAssembler.CharsPerTokenForLanguage("en"));
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage("he"));
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage("he-IL"));

        // The Latin default UNDER-counts Hebrew ~2x; the language-aware estimate roughly doubles it so the
        // budget corresponds to real tokens (this under-count over-filled the window and truncated the review).
        var hebrew = string.Concat(Enumerable.Repeat("שלום עולם זהו טקסט בעברית לבדיקה. ", 40));
        var latin = BookContextAssembler.EstimateTokens(hebrew);
        var dense = BookContextAssembler.EstimateTokens(hebrew, BookContextAssembler.CharsPerTokenForLanguage("he"));
        Assert.True(dense >= latin * 1.9, $"Hebrew dense estimate {dense} should be ~2x the Latin {latin}");
    }

    [Fact]
    public void CharsPerTokenForLanguage_UnknownOrEmptyLanguage_ReturnsDenseConservativeDefault()
    {
        // Null, empty, whitespace, and unrecognised codes must all return the DENSE (conservative) estimate
        // so a Hebrew book whose Language field is unset does not silently revert to the lenient Latin
        // estimate and recreate the whole-book review truncation. Over-counting is safe; under-counting
        // overflows num_ctx and silently truncates model output.
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage(null));
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage(""));
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage("   "));
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage("zz")); // unrecognised code
    }

    [Fact]
    public void CharsPerTokenForLanguage_LatinAllowlist_ReturnsLatinEstimate()
    {
        // Recognised Latin-script language codes (bare and with region suffix) return the Latin estimate.
        Assert.Equal(4.0, BookContextAssembler.CharsPerTokenForLanguage("en"));
        Assert.Equal(4.0, BookContextAssembler.CharsPerTokenForLanguage("en-US"));
    }

    [Fact]
    public void CharsPerTokenForLanguage_DenseScripts_ReturnDenseEstimate()
    {
        // All Hebrew/Arabic variants (including the legacy "iw" code) return the dense estimate.
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage("he"));
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage("he-IL"));
        Assert.Equal(2.0, BookContextAssembler.CharsPerTokenForLanguage("ar"));
    }

    [Fact]
    public void EffectiveBookContextTokenBudget_ReservesOutputPromptAndMargin()
    {
        var opt = new AiOptions
        {
            BookContextBudgetFraction = 0.5,
            BookContextPromptReserveTokens = 1536,
            BookContextSafetyMarginTokens = 512
        };

        // BookReview-shaped window (16384 ctx, 6144 output): input budget + output + prompt + margin must fit.
        var budget = opt.EffectiveBookContextTokenBudget(16384, 6144);
        Assert.True(budget + 6144 + 1536 + 512 <= 16384,
            $"budget {budget} + output 6144 + prompt 1536 + margin 512 must fit 16384");

        // Raising the output reservation MUST shrink the input budget (principled, not a fixed fraction).
        var tighter = opt.EffectiveBookContextTokenBudget(16384, 10000);
        Assert.True(tighter < budget, $"larger NumPredict must shrink the input budget ({tighter} < {budget})");

        // Explicit override still wins verbatim, ignoring the reservation.
        var overridden = new AiOptions { BookContextTokenBudget = 1234 };
        Assert.Equal(1234, overridden.EffectiveBookContextTokenBudget(16384, 6144));
    }

    [Fact]
    public async Task AssembleAsync_BigHebrewBook_ForBookReview_LeavesRoomForOutput()
    {
        // RULE 0 for the truncation bug: a large HEBREW book assembled for the BookReview consumer must leave
        // the model room to GENERATE. Assert the assembled context, measured with the Hebrew-DENSE estimate,
        // plus the review's output (NumPredict) + prompt overhead + margin, fits the BookReview num_ctx — i.e.
        // it can no longer fill the window and truncate the findings ("no dimension yielded findings").
        const int numCtx = 16384;
        const int numPredict = 6144;
        var dbName = Guid.NewGuid().ToString();
        using var provider = BuildProvider(dbName, bookContextTokenBudget: 0, budgetFraction: 0.5,
            bookReviewNumCtx: numCtx, bookReviewNumPredict: numPredict);
        var db = provider.GetRequiredService<AppDbContext>();
        var asm = provider.GetRequiredService<BookContextAssembler>();
        var opt = provider.GetRequiredService<IOptions<AiOptions>>().Value;

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "ספר גדול", Language = "he" });
        const int chapterCount = 80;
        for (var i = 0; i < chapterCount; i++)
            SeedChapterWithBrief(db, bookId, order: i, title: $"פרק {i}", briefJson: HebrewBriefJson(i));
        await db.SaveChangesAsync();

        var expectedBudget = asm.ResolveBudgetTokens(new[] { AiTaskType.BookReview });
        var result = await asm.AssembleAsync(bookId, "he", new[] { AiTaskType.BookReview });

        // The assembled context in REAL (Hebrew-dense) tokens stays within budget...
        var contextTokens = BookContextAssembler.EstimateTokens(
            result.Text, BookContextAssembler.CharsPerTokenForLanguage("he"));
        Assert.True(contextTokens <= expectedBudget,
            $"assembled {contextTokens} dense-tokens must be <= budget {expectedBudget}");

        // ...and the anti-truncation invariant holds: context + output + prompt + margin fits num_ctx.
        Assert.True(
            contextTokens + numPredict + opt.BookContextPromptReserveTokens + opt.BookContextSafetyMarginTokens <= numCtx,
            $"context {contextTokens} + output {numPredict} + prompt {opt.BookContextPromptReserveTokens} + " +
            $"margin {opt.BookContextSafetyMarginTokens} must fit num_ctx {numCtx}");

        // A large book → most chapters dropped, REPORTED (no silent truncation), BookBrief always present.
        Assert.True(result.DroppedCount > 0);
        Assert.Equal(chapterCount, result.IncludedChapterBriefs.Count + result.DroppedCount);
        Assert.NotNull(result.BookBrief);
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

    // Hebrew-content brief so the language-aware (dense) token estimate applies — used by the big-Hebrew-book
    // review-fit test that pins that the whole-book review can no longer overfill num_ctx and truncate.
    private static string HebrewBriefJson(int n) => $$"""
        {
          "plotEvents": ["בפרק {{n}} הגיבור יוצא למסע ומגלה סוד ישן ששובר את שגרת חייו לחלוטין", "עימות דרמטי וטעון רגשית מתרחש לקראת סוף הפרק {{n}}"],
          "characterStates": [ { "name": "דנה", "state": "מתמודדת עם ספקות וחששות כבדים בפרק {{n}}", "emotionalArc": "מפחד אל עבר תקווה זהירה" } ],
          "thematicMarkers": ["בדידות-{{n}}", "גאולה", "זהות-{{n}}"],
          "toneNotes": "אווירה מתוחה, אפלה ומאיימת שנמשכת לאורך כל הפרק {{n}} ומעצימה את תחושת חוסר הוודאות",
          "openThreads": ["מי שלח את המכתב המסתורי בפרק {{n}}?", "האם דנה תבטח שוב במנהיג?"]
        }
        """;

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
            // The structured path only consumes FRESH briefs (ComposeChapterBriefsAsync now gates on the same
            // predicate as GetStatusAsync): BuiltWithModel must equal the active Summarization model (= the
            // AiOptions.DefaultModel "qwen2.5:14b" here, since FeatureModels is unset) AND StructuredBuiltAt
            // must be at/after the chapter's UpdatedAt. UpdatedAt is stamped to "now" at SaveChanges, so stamp
            // the structured build a minute ahead to guarantee timestamp-freshness regardless of save timing.
            StructuredJson = briefJson, BuiltWithModel = "qwen2.5:14b",
            StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1)
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
        double budgetFraction = 0.5,
        int? bookReviewNumCtx = null,
        int? bookReviewNumPredict = null)
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
            if (bookReviewNumCtx.HasValue)
                o.ProviderSettings["Ollama_BookReview"] = new ProviderTuningOptions
                {
                    NumCtx = bookReviewNumCtx.Value,
                    NumPredict = bookReviewNumPredict ?? 2048
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
