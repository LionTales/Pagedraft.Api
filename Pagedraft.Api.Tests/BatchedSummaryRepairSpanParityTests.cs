using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Moq;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// s3 assertion (c) - BUFFER-TO-REPAIR FIDELITY, pinned against absolute span values.
///
/// The plan asserts that "the deterministic detect+classify stage finds the SAME foreign runs at the SAME
/// offsets as the baseline ... this part is PURE, so it must match EXACTLY". s1 proved that assertion is
/// UNACHIEVABLE against two LIVE runs: the stage is pure only <em>given the same input string</em>, and its
/// input is a freshly generated model summary - two live /summarize runs on byte-identical chapter text
/// return different prose of different lengths, so cross-run offsets cannot match and asserting it live
/// would be theatre.
///
/// So the intent is satisfied HERE instead, where the input CAN be held fixed: the same fixed pre-repair
/// string is driven through <see cref="UnifiedAnalysisService.RunRawAsync"/> (the interleaved, un-deferred
/// public seam - summarize, then repair, then persist, one chapter at a time) and through
/// <see cref="BookIntelligenceService.SummarizeChaptersCoreAsync"/> (the batched path - summarize every
/// chapter first, buffer the pre-repair value, then run ONE repair pass), and the marked-span requests the
/// repair engine issues must be BYTE-IDENTICAL.
///
/// NOTE on what this test does and does not prove about the two call paths: post-split, <c>RunRawAsync</c>
/// IS the composition <c>RunRawDeferredRepairAsync</c> + <c>CompleteDeferredRepairAsync</c> (its whole body in
/// <c>UnifiedAnalysisService</c>), and the batched pass calls those exact same two methods (phase 1 and phase 2
/// of <c>BookIntelligenceService.SummarizeOneWindowAsync</c>, the per-window body of
/// <c>SummarizeChaptersCoreAsync</c>). So both sides of the
/// <c>Assert.Equal(interleaved, batched)</c> below run identical detect+classify code over an identical
/// input string - that equivalence is now guaranteed BY CONSTRUCTION (the batched path is not a
/// reimplementation that could drift out of sync), which is a stronger guarantee than a test could give, and
/// makes the interleaved-vs-batched comparison itself near-tautological rather than a true old-vs-new check.
///
/// What this test actually earns its keep on is what construction alone does NOT guarantee: (1) that
/// buffering the summary across the whole-book batch (see <c>PreRepairSummary</c> below) does not mutate the
/// string handed to detect+classify, i.e. the buffer is a faithful copy of what the model produced; and
/// (2) the LITERAL offsets and marked runs the shared detect+classify code produces on this fixed input,
/// asserted as absolute values below, not merely "the two paths agree on whatever they both got wrong".
///
/// The marked value (<c>«run»</c>) is a faithful witness of the detect+classify output: the engine builds it
/// from the ORIGINAL value at the detected offset, so the marker position IS the offset and the marked text
/// IS the classified run.
/// </summary>
public class BatchedSummaryRepairSpanParityTests
{
    private const char MarkOpen = '«';
    private const char MarkClose = '»';

    /// <summary>
    /// TWO repairable foreign runs in ONE value, at different offsets — the same shape BASELINE-B produced
    /// live (start=459 len=11 cold + start=161 len=11 warm in a single summary). Both Latin names are
    /// SENTENCE-INITIAL Title-Case: ForeignRunClassifier spares a Title-Case run only MID-sentence, so
    /// sentence-initial is what makes them classify REPAIR with an empty book-entity set.
    /// </summary>
    private const string FixedLeakingSummary =
        "Marlow נכנס אל החדר האפל ומצא את המכתב הישן. Rowan הלך אחריו בשקט אל המרתף. סוף הסיכום.";

    private const string FixedCleanSummary = "אין כאן מילים זרות כלל. סיכום נקי לגמרי.";

    private static string ChapterText(int order) => $"תוכן הפרק מספר {order} של הספר לבדיקה.";

    private static AnalysisRepairOptions ShippedRepairOptions() => new()
    {
        Enabled = true,
        GuardOnly = true,
        Mode = AnalysisRepairMode.GlossaryThenDynamic
    };

    /// <summary>Router that answers Summarization with a canned per-chapter summary and TermRepair with a
    /// Hebrew replacement, recording every marked value it is asked to repair, in call order.</summary>
    private static Mock<IAiRouter> BuildRouter(
        Func<string, string> summaryFor,
        List<string> markedRequests)
    {
        var replacements = new Dictionary<string, string>
        {
            ["Marlow"] = "מרלו",
            ["Rowan"] = "רואן"
        };

        var mock = new Mock<IAiRouter>();
        mock.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                if (req.TaskType == AiTaskType.TermRepair)
                {
                    markedRequests.Add(req.InputText);
                    var open = req.InputText.IndexOf(MarkOpen);
                    var close = req.InputText.IndexOf(MarkClose);
                    var run = req.InputText.Substring(open + 1, close - open - 1);
                    var repl = replacements.TryGetValue(run, out var r) ? r : "מוחלף";
                    return new AiResponse
                    {
                        Content = $"{{\"replacement\":\"{repl}\"}}",
                        Provider = "test",
                        Model = "gemma4:12b"
                    };
                }

                return new AiResponse
                {
                    Content = summaryFor(req.InputText),
                    Provider = "test",
                    Model = "qwen3.5:9b"
                };
            });
        return mock;
    }

    private static ServiceProvider BuildProvider(Mock<IAiRouter> router)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(router.Object);
        services.Configure<AiOptions>(o =>
        {
            o.BookContextTokenBudget = 1_000_000;
            o.AnalysisRepair = ShippedRepairOptions();
        });
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
        services.AddScoped<DynamicTermRepairService>();
        services.AddSingleton<IBookEntityProvider>(new StubBookEntityProvider());
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();
        return services.BuildServiceProvider();
    }

    private static async Task<(Guid BookId, List<Chapter> Chapters)> SeedBookAsync(AppDbContext db, int chapterCount)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "ספר בדיקה", Language = "he" });
        var chapters = new List<Chapter>();
        for (var i = 0; i < chapterCount; i++)
        {
            var ch = new Chapter
            {
                Id = Guid.NewGuid(), BookId = bookId, Order = i,
                Title = $"פרק {i}", ContentText = ChapterText(i)
            };
            chapters.Add(ch);
            db.Chapters.Add(ch);
        }
        await db.SaveChangesAsync();
        return (bookId, chapters);
    }

    /// <summary>Decode the (start, length) the engine detected, from the marked value it built.</summary>
    private static (int Start, int Length, string Run) DecodeSpan(string marked)
    {
        var open = marked.IndexOf(MarkOpen);
        var close = marked.IndexOf(MarkClose);
        Assert.True(open >= 0 && close > open, "the repair request must carry exactly one marked span");
        return (open, close - open - 1, marked.Substring(open + 1, close - open - 1));
    }

    // ── (c) SAME SPANS, SAME OFFSETS: interleaved vs batched, on a FIXED input ────────────────────────────

    /// <summary>
    /// The gate. One fixed pre-repair summary, two repairable runs, driven through both paths. The marked
    /// requests must match byte for byte and in order - same runs, same offsets, same right-to-left
    /// sequencing. Because both paths call the identical shared detect+classify code (see the class
    /// <c>NOTE</c> above), this equality is expected by construction; what this test pins is that the
    /// buffered pre-repair value fed to that shared code is byte-identical to what the model produced
    /// (line below), and that the offsets/runs it produces on this fixed input are the literal ones
    /// asserted further down - not that "path A" and "path B" independently reached the same answer.
    /// </summary>
    [Fact]
    public async Task DetectClassify_SameSpansAtSameOffsets_InterleavedPathVsBatchedPath()
    {
        // Seed the book FIRST so the SAME real bookId can be threaded through BOTH sides below.
        // IBookEntityProvider is keyed by bookId; StubBookEntityProvider ignores it today, but a future
        // book-sensitive entity provider would feed the two sides DIFFERENT input unless the id matches on
        // both sides, which would silently invalidate this comparison.
        var batched = new List<string>();
        using var batchedProvider = BuildProvider(BuildRouter(_ => FixedLeakingSummary, batched));
        var batchedDb = batchedProvider.GetRequiredService<AppDbContext>();
        var (bookId, _) = await SeedBookAsync(batchedDb, 1);

        // BATCHED (s2): summarize every chapter first, then ONE repair pass.
        var outcome = await batchedProvider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        // The batched path's input to detect+classify is the un-repaired summary, byte-identical to the
        // string the interleaved path fed it.
        Assert.Equal(FixedLeakingSummary, Assert.Single(outcome.Summarized).PreRepairSummary);

        // INTERLEAVED (the pre-s2 per-chapter call): RunRawAsync = summarize, then repair immediately.
        // Uses the SAME bookId seeded above (not a throwaway Guid.NewGuid()).
        var interleaved = new List<string>();
        using (var provider = BuildProvider(BuildRouter(_ => FixedLeakingSummary, interleaved)))
        {
            var result = await provider.GetRequiredService<UnifiedAnalysisService>().RunRawAsync(
                ChapterText(0), AnalysisType.Summarization, instruction: null, language: "he",
                bookId: bookId, ct: CancellationToken.None);
            Assert.DoesNotContain("Marlow", result);
            Assert.DoesNotContain("Rowan", result);
        }

        // EXACT match: same count, same order, same marked bytes → same runs at the same offsets.
        Assert.Equal(2, interleaved.Count);
        Assert.Equal(interleaved, batched);

        // ...and the offsets are the literal ones in the fixed string (so this pins real values, not just
        // "two paths agree on whatever they both got wrong").
        var spans = batched.Select(DecodeSpan).ToList();
        Assert.Equal(
            new[]
            {
                (FixedLeakingSummary.IndexOf("Rowan", StringComparison.Ordinal), 5, "Rowan"),
                (FixedLeakingSummary.IndexOf("Marlow", StringComparison.Ordinal), 6, "Marlow")
            },
            spans);
    }

    /// <summary>
    /// The batching-specific risk the single-chapter case cannot reach: with SEVERAL chapters buffered and
    /// repaired in one pass, each chapter's spans must still be detected against ITS OWN buffered value at
    /// ITS OWN offsets - no bleed from the neighbours held in the same buffer. Chapter 0 and chapter 2 leak
    /// at deliberately DIFFERENT offsets (chapter 2's value is prefixed), so a value mix-up would show up
    /// as a wrong offset rather than merely a wrong string. As with the single-chapter test above, the
    /// interleaved-vs-batched equality is expected by construction (both call the same detect+classify
    /// code); what this test proves is buffer fidelity per chapter plus the absolute, prefix-shifted
    /// offsets asserted at the bottom.
    /// </summary>
    [Fact]
    public async Task DetectClassify_MultipleBufferedChapters_EachRepairedAgainstItsOwnOffsets()
    {
        const string prefix = "פתיחה ארוכה במיוחד לפרק הזה בלבד. ";
        var summaries = new Dictionary<string, string>
        {
            [ChapterText(0)] = FixedLeakingSummary,
            [ChapterText(1)] = FixedCleanSummary,
            [ChapterText(2)] = prefix + FixedLeakingSummary
        };

        // Seed the book FIRST so the SAME real bookId is threaded through BOTH sides below (see the class
        // NOTE and the single-chapter test above for why a book-sensitive entity provider needs the ids to
        // match for this comparison to stay valid).
        var batched = new List<string>();
        using var batchedProvider = BuildProvider(BuildRouter(
            input => summaries.First(kv => input.Contains(kv.Key, StringComparison.Ordinal)).Value,
            batched));
        var batchedDb = batchedProvider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(batchedDb, 3);

        var outcome = await batchedProvider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        Assert.Equal(3, outcome.Summarized.Count);
        // Each buffered pre-repair value is its OWN chapter's summary (correlation by identity).
        Assert.Equal(FixedLeakingSummary,
            outcome.Summarized.Single(o => o.ChapterId == chapters[0].Id).PreRepairSummary);
        Assert.Equal(prefix + FixedLeakingSummary,
            outcome.Summarized.Single(o => o.ChapterId == chapters[2].Id).PreRepairSummary);

        // Interleaved reference: repair each chapter's summary on its own, in chapter order. Uses the SAME
        // bookId seeded above (not a throwaway Guid.NewGuid()).
        var interleaved = new List<string>();
        using (var provider = BuildProvider(BuildRouter(
            input => summaries.First(kv => input.Contains(kv.Key, StringComparison.Ordinal)).Value,
            interleaved)))
        {
            var svc = provider.GetRequiredService<UnifiedAnalysisService>();
            foreach (var order in new[] { 0, 1, 2 })
            {
                await svc.RunRawAsync(ChapterText(order), AnalysisType.Summarization,
                    instruction: null, language: "he", bookId: bookId, ct: CancellationToken.None);
            }
        }

        Assert.Equal(4, interleaved.Count);          // 2 runs in chapter 0, 0 in chapter 1, 2 in chapter 2
        Assert.Equal(interleaved, batched);

        // Chapter 2's offsets are shifted by exactly the prefix length — proof each value was measured
        // against itself, not against a neighbour's.
        var spans = batched.Select(DecodeSpan).ToList();
        Assert.Equal(spans[0].Start + prefix.Length, spans[2].Start);
        Assert.Equal(spans[1].Start + prefix.Length, spans[3].Start);
    }
}
