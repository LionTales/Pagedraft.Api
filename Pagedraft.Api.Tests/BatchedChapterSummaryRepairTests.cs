using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
/// s2 — the BATCHED chapter-summary repair pass
/// (<see cref="BookIntelligenceService.SummarizeChaptersCoreAsync"/>).
///
/// The defect: the dynamic term repair routes to <c>Ai:FeatureModels:TermRepair</c> (gemma4:12b) while
/// Summarization routes to qwen3.5:9b. On a single-GPU host with <c>OLLAMA_MAX_LOADED_MODELS=1</c> the old
/// per-chapter <c>summarize -> repair -> persist</c> loop EVICTED the summarization model and cold-loaded the
/// ~7 GB repair model on every leaking chapter (measured 21-23 s each), then evicted it back. K leaking
/// chapters cost K swaps.
///
/// The fix DEFERS the repair calls, it does not MERGE them: every foreign run still gets its own isolated
/// span-scoped model call with the identical prompt and the identical validation-by-re-detect, so repair
/// quality is unchanged BY CONSTRUCTION. What reordering CAN break is what these tests pin:
/// correlation-by-identity, the persist boundary, the two skip guards, the dual-surface contract, and the
/// per-chapter fail-safe.
///
/// Everything here is DETERMINISTIC: a mocked <see cref="IAiRouter"/> supplies both the chapter summaries and
/// the term-repair replacements, and the REAL detect/classify/splice engine runs. No GPU, no Ollama.
/// </summary>
public class BatchedChapterSummaryRepairTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Hebrew summary whose FIRST token is a Title-Case Latin name. Sentence-initial is deliberate:
    /// ForeignRunClassifier rule 7 spares a Title-Case Latin run only MID-sentence, so with an empty book
    /// entity set this run is classified REPAIR and reaches the term-repair model — which is what makes a
    /// "leaking chapter" reproducible without a real model. The Hebrew tail carries a per-chapter MARKER
    /// (e.g. "פרק בית") that the span-scoped repair must leave byte-identical, so a mis-correlated summary
    /// is detectable by the marker alone.
    /// </summary>
    private static string LeakingSummary(string latinName, string hebrewMarker)
        => $"{latinName} נכנס אל החדר האפל ומצא את המכתב. סיכום {hebrewMarker}.";

    private static string CleanSummary(string hebrewMarker)
        => $"אין כאן מילים זרות כלל. סיכום {hebrewMarker}.";

    /// <summary>Chapter body text, unique per chapter so the router can key its reply on the chapter.</summary>
    private static string ChapterText(int order) => $"תוכן הפרק מספר {ToHebrewOrdinal(order)} של הספר.";

    private static string ToHebrewOrdinal(int order) => order switch
    {
        0 => "אלף",
        1 => "בית",
        2 => "גימל",
        3 => "דלת",
        _ => "הא"
    };

    private static string Marker(int order) => $"פרק {ToHebrewOrdinal(order)}";

    /// <summary>The shipped repair configuration: the dynamic (span-scoped) stage runs. Mode=Glossary/Off is
    /// the rollback that never reaches the term-repair model at all, so the batching is unobservable there.</summary>
    private static AnalysisRepairOptions ShippedRepairOptions() => new()
    {
        Enabled = true,
        GuardOnly = true,
        Mode = AnalysisRepairMode.GlossaryThenDynamic
    };

    /// <summary>
    /// One router serving BOTH roles, distinguished by <see cref="AiRequest.TaskType"/>:
    /// <c>Summarization</c> returns the per-chapter canned summary (keyed on the chapter body text);
    /// <c>TermRepair</c> returns a Hebrew replacement for the marked span (keyed on the Latin name inside the
    /// marked value). Every call is appended to <paramref name="callLog"/> in order, which is how the
    /// "all summaries before any repair" assertion is made.
    /// </summary>
    /// <param name="onSummarize">
    /// Side effect run WHILE a Summarization request is being answered, before its reply is produced. This is
    /// the same side-effecting-fake technique <see cref="ThrowOnNthAccessAiOptions"/> uses: it lets a test
    /// mutate state "concurrently" with the pass, deterministically and without threads.
    /// </param>
    private static Mock<IAiRouter> BuildRouter(
        IReadOnlyDictionary<string, string> summaryByChapterText,
        IReadOnlyDictionary<string, string> replacementByLatinName,
        List<string> callLog,
        Func<AiRequest, bool>? throwOnTermRepair = null,
        Action<AiRequest>? onSummarize = null)
    {
        var mock = new Mock<IAiRouter>();
        mock.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                if (req.TaskType == AiTaskType.TermRepair)
                {
                    callLog.Add("repair");
                    if (throwOnTermRepair is not null && throwOnTermRepair(req))
                        throw new InvalidOperationException("injected term-repair fault");

                    var hit = replacementByLatinName.FirstOrDefault(
                        kv => req.InputText.Contains(kv.Key, StringComparison.Ordinal));
                    var replacement = hit.Value ?? "מוחלף";
                    return new AiResponse
                    {
                        Content = $"{{\"replacement\":\"{replacement}\"}}",
                        Provider = "test",
                        Model = "gemma4:12b"
                    };
                }

                callLog.Add("summarize");
                onSummarize?.Invoke(req);
                var summary = summaryByChapterText.FirstOrDefault(
                    kv => req.InputText.Contains(kv.Key, StringComparison.Ordinal)).Value
                    ?? "סיכום כללי.";
                return new AiResponse { Content = summary, Provider = "test", Model = "qwen3.5:9b" };
            });
        return mock;
    }

    private static ServiceProvider BuildProvider(
        Mock<IAiRouter> router,
        AnalysisRepairOptions? repair,
        IBookEntityProvider? entityProvider = null,
        IOptions<AiOptions>? aiOptionsOverride = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services.AddSingleton(router.Object);
        if (aiOptionsOverride is not null)
        {
            services.AddSingleton(aiOptionsOverride);
        }
        else
        {
            services.Configure<AiOptions>(o =>
            {
                o.BookContextTokenBudget = 1_000_000;
                o.AnalysisRepair = repair;
            });
        }
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
        services.AddSingleton(entityProvider ?? new StubBookEntityProvider());
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();

        return services.BuildServiceProvider();
    }

    /// <summary>Seeds a Hebrew book with <paramref name="chapterCount"/> chapters and NO ChunkSummary rows, so
    /// every chapter is eligible (both skip guards pass).</summary>
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
                Title = Marker(i), ContentText = ChapterText(i)
            };
            chapters.Add(ch);
            db.Chapters.Add(ch);
        }
        await db.SaveChangesAsync();
        return (bookId, chapters);
    }

    // ── (1) THE BATCHING PIN: every summarization, then every repair, then ONE persist ────────────────────

    /// <summary>
    /// THE core regression. Three chapters, ALL leaking. The router call log must be
    /// <c>summarize x3</c> then <c>repair x3</c> — never interleaved — and the FIRST SaveChanges must land
    /// AFTER the last repair.
    ///
    /// This is both halves of the fix in one assertion: the ordering is the GPU win (the repair model loads
    /// once instead of three times), and the save position is non-negotiable (ii) — un-repaired prose is
    /// never persisted, not even transiently.
    ///
    /// REVERT-VERIFY: against the old per-chapter <c>summarize -> repair -> persist</c> loop the log is
    /// <c>summarize, repair, save, summarize, repair, save, ...</c> and both assertions fail.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_SummarizesEveryChapterBeforeAnyRepair_AndPersistsOnlyAtTheEnd()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = LeakingSummary("Marlow", Marker(0)),
                [ChapterText(1)] = LeakingSummary("Rowan", Marker(1)),
                [ChapterText(2)] = LeakingSummary("Sedgwick", Marker(2))
            },
            replacementByLatinName: new Dictionary<string, string>
            {
                ["Marlow"] = "מרלו", ["Rowan"] = "רואן", ["Sedgwick"] = "סדג׳וויק"
            },
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, _) = await SeedBookAsync(db, 3);

        // The persist boundary, observed on the REAL DbContext the service uses.
        db.SavingChanges += (_, _) => callLog.Add("save");

        await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersAsync(bookId, "he", CancellationToken.None);

        Assert.Equal(
            new[] { "summarize", "summarize", "summarize", "repair", "repair", "repair", "save" },
            callLog);
    }

    // ── (2) CORRELATION: a leak in only SOME chapters lands on the RIGHT chapters ─────────────────────────

    /// <summary>
    /// Four chapters, leaking in chapters 1 and 3 ONLY, and — critically — chapter 0 is SKIPPED (its summary
    /// is fresh). That makes the buffered position and the chapter position DIVERGE: buffered item 0 belongs
    /// to chapter 1, item 1 to chapter 2, item 2 to chapter 3. Any correlation that used the buffered INDEX
    /// against the chapter list would land every summary one chapter early, so this test would fail — which
    /// is what makes it a real pin of non-negotiable (i) rather than a tautology.
    ///
    /// Each summary also carries a Hebrew marker naming its own chapter, so a mis-landed summary is visible
    /// even though the repair rewrote part of the text (a content match could not be used for correlation —
    /// the repair changes the content — which is exactly why identity is the only sound key).
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_LeakInSomeChapters_RepairsLandOnTheirOwnChapters()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = CleanSummary(Marker(0)),   // never requested: chapter 0 is skipped
                [ChapterText(1)] = LeakingSummary("Marlow", Marker(1)),
                [ChapterText(2)] = CleanSummary(Marker(2)),
                [ChapterText(3)] = LeakingSummary("Rowan", Marker(3))
            },
            replacementByLatinName: new Dictionary<string, string> { ["Marlow"] = "מרלו", ["Rowan"] = "רואן" },
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 4);

        // Chapter 0 already has a FRESH summary → skipped, so buffered index and chapter index diverge.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chapters[0].Id, Language = "he", SummaryText = "סיכום טרי קיים."
        });
        await db.SaveChangesAsync();
        var freshRow = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapters[0].Id);
        freshRow.CreatedAt = DateTimeOffset.UtcNow.AddHours(2);
        await db.SaveChangesAsync();

        await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersAsync(bookId, "he", CancellationToken.None);

        var rows = await db.ChunkSummaries.AsNoTracking()
            .Where(cs => cs.BookId == bookId)
            .ToDictionaryAsync(cs => cs.ChapterId);

        Assert.Equal(4, rows.Count);

        // The skipped chapter keeps its existing summary untouched.
        Assert.Equal("סיכום טרי קיים.", rows[chapters[0].Id].SummaryText);

        // The clean chapter: persisted byte-identical to what the model produced.
        Assert.Equal(CleanSummary(Marker(2)), rows[chapters[2].Id].SummaryText);

        // Leaking chapters: repaired, and each carries ITS OWN marker + ITS OWN replacement.
        var row1 = rows[chapters[1].Id].SummaryText;
        Assert.Contains("מרלו", row1);
        Assert.Contains(Marker(1), row1);
        Assert.DoesNotContain("Marlow", row1);
        Assert.DoesNotContain("רואן", row1);          // chapter 3's replacement must NOT be here
        Assert.DoesNotContain(Marker(3), row1);

        var row3 = rows[chapters[3].Id].SummaryText;
        Assert.Contains("רואן", row3);
        Assert.Contains(Marker(3), row3);
        Assert.DoesNotContain("Rowan", row3);
        Assert.DoesNotContain("מרלו", row3);
        Assert.DoesNotContain(Marker(1), row3);

        // Exactly three summaries (chapter 0 skipped) and two repair calls — one per leaking chapter,
        // deferred but never merged.
        Assert.Equal(3, callLog.Count(c => c == "summarize"));
        Assert.Equal(2, callLog.Count(c => c == "repair"));
    }

    // ── (3) THE PRE-REPAIR TEST SEAM: over-rewrite 0, asserted deterministically ──────────────────────────

    /// <summary>
    /// The pre-repair summary is observable NOWHERE in production (DynamicTermRepairService.LogSpan logs
    /// offsets/latency only — "NO run text / replacement / value is ever logged"), which made the plan's
    /// over-rewrite-0 assertion unverifiable. The batch buffers the un-repaired text in memory, so
    /// <see cref="BookIntelligenceService.ChapterSummaryOutcome"/> can hand the (pre, post) PAIR to a test.
    /// It is a return value, never a log line — no book prose is logged at any level.
    ///
    /// The assertion is the span-scope invariant: everything OUTSIDE the repaired run is byte-identical
    /// between the pre-repair and post-repair values, and the persisted row is exactly the post-repair value.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_ExposesPreAndPostRepairPair_AndTheRepairIsSpanScoped()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = CleanSummary(Marker(0)),
                [ChapterText(1)] = LeakingSummary("Marlow", Marker(1))
            },
            replacementByLatinName: new Dictionary<string, string> { ["Marlow"] = "מרלו" },
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 2);

        var outcome = await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        Assert.Empty(outcome.Skipped);
        Assert.Equal(2, outcome.Summarized.Count);
        Assert.All(outcome.Summarized, o => Assert.False(o.RepairFaulted));

        // The clean chapter round-trips byte-identical: pre == post (the repair layer is a strict no-op).
        var clean = outcome.Summarized.Single(o => o.ChapterId == chapters[0].Id);
        Assert.Equal(clean.PreRepairSummary, clean.PersistedSummary);

        // The leaking chapter: exactly the Latin run changed. Substituting the replacement back out of the
        // repaired text must reproduce the pre-repair text EXACTLY — i.e. over-rewrite 0 outside the span.
        var leaked = outcome.Summarized.Single(o => o.ChapterId == chapters[1].Id);
        Assert.Equal(LeakingSummary("Marlow", Marker(1)), leaked.PreRepairSummary);
        Assert.NotEqual(leaked.PreRepairSummary, leaked.PersistedSummary);
        Assert.Equal(
            leaked.PreRepairSummary,
            leaked.PersistedSummary.Replace("מרלו", "Marlow", StringComparison.Ordinal));

        // ...and the persisted row is exactly the post-repair value (no third transformation on the way out).
        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapters[1].Id);
        Assert.Equal(leaked.PersistedSummary, row.SummaryText);
    }

    // ── (4) PER-CHAPTER FAIL-SAFE ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Non-negotiable (v), through the REAL fail-safe chain: the term-repair model throws for ONE chapter's
    /// span. DynamicTermRepairService swallows it (span-scoped fail-safe), so that chapter persists
    /// UN-repaired — and, crucially for batching, the other two chapters still persist REPAIRED. Before
    /// batching this was free (earlier chapters were already saved); now every chapter is in flight when the
    /// fault happens, so "the others survive" is a real new invariant.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_TermRepairFaultOnOneChapter_LeavesThatChapterUnrepaired_AndKeepsTheOthers()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = LeakingSummary("Marlow", Marker(0)),
                [ChapterText(1)] = LeakingSummary("Rowan", Marker(1)),
                [ChapterText(2)] = LeakingSummary("Sedgwick", Marker(2))
            },
            replacementByLatinName: new Dictionary<string, string>
            {
                ["Marlow"] = "מרלו", ["Rowan"] = "רואן", ["Sedgwick"] = "סדג׳וויק"
            },
            callLog,
            throwOnTermRepair: req => req.InputText.Contains("Rowan", StringComparison.Ordinal));

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 3);

        await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersAsync(bookId, "he", CancellationToken.None);

        var rows = await db.ChunkSummaries.AsNoTracking()
            .Where(cs => cs.BookId == bookId)
            .ToDictionaryAsync(cs => cs.ChapterId);

        Assert.Equal(3, rows.Count);                                    // nothing lost
        Assert.Contains("מרלו", rows[chapters[0].Id].SummaryText);       // repaired
        Assert.Contains("סדג׳וויק", rows[chapters[2].Id].SummaryText);   // repaired
        Assert.Contains("Rowan", rows[chapters[1].Id].SummaryText);      // fail-safe: left un-repaired
        Assert.Contains(Marker(1), rows[chapters[1].Id].SummaryText);    // ...and still its OWN summary
    }

    /// <summary>
    /// The belt-and-braces half of (v): a fault that ESCAPES the repair layer entirely (here injected at the
    /// options read at the very top of ApplyAnalysisRepairAsync, the one seam on that path outside its
    /// internal try/catch) must still leave only THAT chapter un-repaired. Asserted through the
    /// <c>RepairFaulted</c> flag as well as the persisted rows, so the fail-safe is observable and not merely
    /// invisible.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_RepairLayerThrows_SwallowedPerChapter_OthersStillRepaired()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = LeakingSummary("Marlow", Marker(0)),
                [ChapterText(1)] = LeakingSummary("Rowan", Marker(1))
            },
            replacementByLatinName: new Dictionary<string, string> { ["Marlow"] = "מרלו", ["Rowan"] = "רואן" },
            callLog);

        // Throws on the SECOND read of AiOptions.Value. Read 1 is be-c03's ResolveSummaryBatchWindow, once per
        // PASS before the window loop (it was read 0 until be-c03 added the checkpoint window, and the
        // AccessCount assertion below is precisely what caught that shift instead of letting the injected
        // fault silently relocate). Nothing else on the summarize path reads it, so read 2 is chapter 0's
        // ApplyAnalysisRepairAsync and chapter 1's read (the third) succeeds.
        var options = new ThrowOnNthAccessAiOptions(
            new AiOptions { BookContextTokenBudget = 1_000_000, AnalysisRepair = ShippedRepairOptions() },
            throwOnAccess: 2);

        using var provider = BuildProvider(router, repair: null, entityProvider: null, aiOptionsOverride: options);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 2);

        var outcome = await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        // The injected fault landed where the test intends (a diagnostic, so a future extra Options read
        // fails loudly here rather than silently relocating the fault).
        Assert.Equal(3, options.AccessCount);

        var faulted = outcome.Summarized.Single(o => o.ChapterId == chapters[0].Id);
        var healthy = outcome.Summarized.Single(o => o.ChapterId == chapters[1].Id);
        Assert.True(faulted.RepairFaulted);
        Assert.False(healthy.RepairFaulted);

        var rows = await db.ChunkSummaries.AsNoTracking()
            .Where(cs => cs.BookId == bookId).ToDictionaryAsync(cs => cs.ChapterId);
        Assert.Equal(2, rows.Count);                                    // the batch was NOT lost
        Assert.Contains("Marlow", rows[chapters[0].Id].SummaryText);     // un-repaired (fail-safe)
        Assert.Contains("רואן", rows[chapters[1].Id].SummaryText);       // still repaired
    }

    /// <summary>An <see cref="IOptions{TOptions}"/> that throws on the Nth read of <c>Value</c>. Used to
    /// inject a fault that ESCAPES the repair layer's own catch-all stages. <paramref name="exceptionFactory"/>
    /// chooses WHICH exception (be-c04 needs an <see cref="OperationCanceledException"/>-shaped one there);
    /// it defaults to the plain non-cancellation fault the earlier tests inject.</summary>
    private sealed class ThrowOnNthAccessAiOptions : IOptions<AiOptions>
    {
        private readonly AiOptions _value;
        private readonly int _throwOnAccess;
        private readonly Func<Exception> _exceptionFactory;

        public int AccessCount { get; private set; }

        public ThrowOnNthAccessAiOptions(
            AiOptions value, int throwOnAccess, Func<Exception>? exceptionFactory = null)
        {
            _value = value;
            _throwOnAccess = throwOnAccess;
            _exceptionFactory = exceptionFactory
                ?? (() => new InvalidOperationException("injected repair-layer fault"));
        }

        public AiOptions Value
        {
            get
            {
                AccessCount++;
                if (AccessCount == _throwOnAccess)
                    throw _exceptionFactory();
                return _value;
            }
        }
    }

    // ── (4b) be-c04: CANCELLATION SEMANTICS OF THE PER-CHAPTER FAIL-SAFE ─────────────────────────────────

    /// <summary>
    /// be-c04, THE TIMEOUT MIS-CLASSIFICATION. <c>HttpClient</c> surfaces its OWN timeout as
    /// <see cref="TaskCanceledException"/>, which IS an <see cref="OperationCanceledException"/>. The phase-2
    /// fail-safe used to filter on the TYPE alone (<c>ex is not OperationCanceledException</c>), so a
    /// repair-side timeout - a cold ~21 s TermRepair model load overrunning the Ollama client timeout is the
    /// measured shape of it - was treated as a USER CANCELLATION and aborted the whole window, costing every
    /// other chapter in it the summary the fail-safe exists to protect. The token is the discriminator: our
    /// <c>ct</c> is <see cref="CancellationToken.None"/> here, so nobody asked this pass to stop.
    ///
    /// WHY THE FAULT IS INJECTED AT THE OPTIONS READ AND NOT AT THE ROUTER. A <see cref="TaskCanceledException"/>
    /// thrown by the router on the TermRepair call never reaches this filter at all: it is absorbed by the
    /// per-span <c>catch (Exception)</c> in <c>DynamicTermRepairService</c> ("keeping original span
    /// (fail-safe)"), and would be absorbed again by <c>ApplyAnalysisRepairAsync</c>'s stage catches - all of
    /// which are catch-ALL and therefore swallow OperationCanceledException too. Injecting there would make
    /// this test pass against BOTH filters, i.e. a vacuous green. The options read at the top of
    /// <c>ApplyAnalysisRepairAsync</c> is the seam that genuinely ESCAPES the repair layer (the same seam the
    /// test above uses), so it is the only deterministic way to put an OperationCanceledException in front of
    /// this filter.
    ///
    /// REVERT-VERIFY: restore <c>catch (Exception ex) when (ex is not OperationCanceledException)</c> and the
    /// pass throws instead of degrading, failing on the named diagnostic below.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_RepairThrowsTaskCanceled_TokenNotCancelled_DegradesOnlyThatChapter()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = LeakingSummary("Marlow", Marker(0)),
                [ChapterText(1)] = LeakingSummary("Rowan", Marker(1))
            },
            replacementByLatinName: new Dictionary<string, string> { ["Marlow"] = "מרלו", ["Rowan"] = "רואן" },
            callLog);

        // The SAME ordinal as the InvalidOperationException test above (read 1 = be-c03's
        // ResolveSummaryBatchWindow, once per pass; read 2 = chapter 0's ApplyAnalysisRepairAsync; read 3 =
        // chapter 1's). Only the exception TYPE differs.
        var options = new ThrowOnNthAccessAiOptions(
            new AiOptions { BookContextTokenBudget = 1_000_000, AnalysisRepair = ShippedRepairOptions() },
            throwOnAccess: 2,
            exceptionFactory: () => new TaskCanceledException("simulated HttpClient timeout on the repair call"));

        using var provider = BuildProvider(router, repair: null, entityProvider: null, aiOptionsOverride: options);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 2);

        var service = provider.GetRequiredService<BookIntelligenceService>();
        BookIntelligenceService.ChapterSummaryBatchOutcome outcome;
        try
        {
            outcome = await service.SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);
        }
        catch (OperationCanceledException ex)
        {
            Assert.Fail(
                $"TIMEOUT MIS-CLASSIFIED AS CANCELLATION: the phase-2 per-chapter fail-safe did not swallow a " +
                $"{ex.GetType().Name} even though the pass's own CancellationToken was NEVER cancelled, so ONE " +
                "chapter's repair timeout aborted the entire window and cost every other chapter its summary. " +
                "An HttpClient timeout surfaces as TaskCanceledException, which IS an " +
                "OperationCanceledException, so filtering on the TYPE alone cannot tell a repair fault from a " +
                "caller cancel. The filter must also require ct.IsCancellationRequested.");
            throw;   // unreachable: Assert.Fail always throws (here only for definite assignment)
        }

        // The injected fault landed where the test intends (loud if a future Options read shifts the ordinal).
        Assert.Equal(3, options.AccessCount);

        var faulted = outcome.Summarized.Single(o => o.ChapterId == chapters[0].Id);
        var healthy = outcome.Summarized.Single(o => o.ChapterId == chapters[1].Id);
        Assert.True(faulted.RepairFaulted);
        Assert.False(healthy.RepairFaulted);

        var rows = await db.ChunkSummaries.AsNoTracking()
            .Where(cs => cs.BookId == bookId).ToDictionaryAsync(cs => cs.ChapterId);
        Assert.Equal(2, rows.Count);                                    // the batch was NOT lost
        Assert.Contains("Marlow", rows[chapters[0].Id].SummaryText);     // degraded to un-repaired
        Assert.Contains(Marker(0), rows[chapters[0].Id].SummaryText);    // ...and still its OWN summary
        Assert.Contains("רואן", rows[chapters[1].Id].SummaryText);       // the rest of the window survived
    }

    /// <summary>
    /// The other half of be-c04: a GENUINE caller cancellation must still propagate, and the window IN FLIGHT
    /// must persist nothing. Four chapters, checkpoint window of two. The caller's token is cancelled while
    /// window 1 is still summarizing, and window 1's first repair then raises an
    /// <see cref="OperationCanceledException"/> at the one seam that escapes the repair layer. Because
    /// <c>ct.IsCancellationRequested</c> is now true, the filter does NOT match and the pass aborts.
    ///
    /// The "persists NOTHING" claim is scoped to the window IN FLIGHT on purpose: window 0 was committed
    /// BEFORE the cancel, after its own complete repair pass, and it staying committed is exactly be-c03's
    /// checkpoint guarantee - not a leak of un-repaired prose. Both halves are asserted here so a future
    /// change cannot satisfy one by breaking the other.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_RepairThrowsWhileTokenIsCancelled_Propagates_AndTheInFlightWindowPersistsNothing()
    {
        var callLog = new List<string>();
        using var cts = new CancellationTokenSource();
        var cancelled = false;

        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = LeakingSummary("Marlow", Marker(0)),
                [ChapterText(1)] = LeakingSummary("Rowan", Marker(1)),
                [ChapterText(2)] = LeakingSummary("Sedgwick", Marker(2)),
                [ChapterText(3)] = LeakingSummary("Thorne", Marker(3))
            },
            replacementByLatinName: new Dictionary<string, string>
            {
                ["Marlow"] = "מרלו", ["Rowan"] = "רואן", ["Sedgwick"] = "סדגוויק", ["Thorne"] = "תורן"
            },
            callLog,
            onSummarize: req =>
            {
                // One-shot, on the LAST chapter of the SECOND window: window 0 is already committed, window 1
                // has buffered its summaries and has not persisted anything. The caller walks away HERE.
                if (cancelled || !req.InputText.Contains(ChapterText(3), StringComparison.Ordinal)) return;
                cancelled = true;
                cts.Cancel();
            });

        // Read 1 = ResolveSummaryBatchWindow; reads 2-3 = window 0's two chapters; read 4 = window 1's FIRST
        // chapter, which is the first repair to run after the cancel above.
        var options = new ThrowOnNthAccessAiOptions(
            new AiOptions
            {
                BookContextTokenBudget = 1_000_000,
                AnalysisRepair = WindowedRepairOptions(windowChapters: 2)
            },
            throwOnAccess: 4,
            exceptionFactory: () => new OperationCanceledException(cts.Token));

        using var provider = BuildProvider(router, repair: null, entityProvider: null, aiOptionsOverride: options);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 4);
        db.SavingChanges += (_, _) => callLog.Add("save");

        var service = provider.GetRequiredService<BookIntelligenceService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SummarizeChaptersCoreAsync(bookId, "he", cts.Token));

        Assert.True(cancelled, "the cancel hook never ran; everything below would be a vacuous green");
        Assert.Equal(4, options.AccessCount);   // threw on read 4 and never reached a fifth

        var rows = await db.ChunkSummaries.AsNoTracking()
            .Where(cs => cs.BookId == bookId).ToListAsync();

        // (i) The window IN FLIGHT persisted NOTHING - not a half-repaired row, not an un-repaired one.
        Assert.DoesNotContain(rows, cs => cs.ChapterId == chapters[2].Id);
        Assert.DoesNotContain(rows, cs => cs.ChapterId == chapters[3].Id);

        // (ii) ...and the window committed BEFORE the cancel is untouched and fully repaired (be-c03).
        Assert.Equal(2, rows.Count);
        foreach (var (chapterIndex, latin, hebrew) in new[] { (0, "Marlow", "מרלו"), (1, "Rowan", "רואן") })
        {
            var row = Assert.Single(rows, cs => cs.ChapterId == chapters[chapterIndex].Id);
            Assert.Contains(hebrew, row.SummaryText);
            Assert.DoesNotContain(latin, row.SummaryText);
        }

        // (iii) Exactly ONE save, window 0's. Window 1 summarized both chapters, then aborted at its FIRST
        //       repair - before any term-repair model call and before any save of its own.
        Assert.Equal(
            new[] { "summarize", "summarize", "repair", "repair", "save", "summarize", "summarize" },
            callLog);
    }

    // ── (5) BOTH SKIP GUARDS STILL FIRE, BEFORE THE CHAPTER IS SUMMARIZED ────────────────────────────────

    /// <summary>
    /// Non-negotiable (iii). A FRESH row (CreatedAt >= Chapter.UpdatedAt) and a USER-EDITED row are both
    /// skipped, and skipped BEFORE any model call — so a guarded chapter still costs zero summarization
    /// calls and zero repair calls. Only the one eligible chapter is summarized and persisted.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_KeepsBothSkipGuards_AheadOfTheModelCall()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = CleanSummary(Marker(0)),
                [ChapterText(1)] = CleanSummary(Marker(1)),
                [ChapterText(2)] = CleanSummary(Marker(2))
            },
            replacementByLatinName: new Dictionary<string, string>(),
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 3);

        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chapters[0].Id, Language = "he",
            SummaryText = "סיכום טרי קיים."
        });
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chapters[1].Id, Language = "he",
            SummaryText = "עריכה של המשתמש — אין לדרוס.",
            SummaryUserEdited = true, SummaryUserEditedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Chapter 0's row is explicitly FRESH; chapter 1's is explicitly STALE, so only the user-edit guard
        // can save it (proving the two guards are independent and both still evaluated).
        var fresh = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapters[0].Id);
        fresh.CreatedAt = DateTimeOffset.UtcNow.AddHours(2);
        var edited = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapters[1].Id);
        edited.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();

        var outcome = await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        Assert.Equal(
            new[]
            {
                new BookIntelligenceService.SkippedChapter(
                    chapters[0].Id, BookIntelligenceService.ChapterSkipReason.Fresh),
                new BookIntelligenceService.SkippedChapter(
                    chapters[1].Id, BookIntelligenceService.ChapterSkipReason.UserEdited)
            },
            outcome.Skipped);
        Assert.Equal(chapters[2].Id, Assert.Single(outcome.Summarized).ChapterId);

        // Exactly ONE model call in the whole pass — the guards ran before it, not after.
        Assert.Equal(new[] { "summarize" }, callLog);

        var rows = await db.ChunkSummaries.AsNoTracking()
            .Where(cs => cs.BookId == bookId).ToDictionaryAsync(cs => cs.ChapterId);
        Assert.Equal("סיכום טרי קיים.", rows[chapters[0].Id].SummaryText);
        Assert.Equal("עריכה של המשתמש — אין לדרוס.", rows[chapters[1].Id].SummaryText);
        Assert.True(rows[chapters[1].Id].SummaryUserEdited);
        Assert.Equal(CleanSummary(Marker(2)), rows[chapters[2].Id].SummaryText);
    }

    // ── (5b) THE THIRD SKIP GUARD: EMPTY/WHITESPACE ContentText ─────────────────────────────────────────

    /// <summary>
    /// be-f05: the whitespace-only <c>ContentText</c> guard (evaluated before the other two, and before any
    /// model call) had no direct test. One chapter's content is whitespace-only, the other is normal, so the
    /// blank chapter must land in <c>outcome.Skipped</c> as <see cref="BookIntelligenceService.ChapterSkipReason.NoContent"/>,
    /// cost zero router calls, and leave no <see cref="ChunkSummary"/> row behind.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_WhitespaceOnlyContentText_SkippedAsNoContent_ZeroCalls_NoRowCreated()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string> { [ChapterText(1)] = CleanSummary(Marker(1)) },
            replacementByLatinName: new Dictionary<string, string>(),
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 2);

        var blank = await db.Chapters.SingleAsync(c => c.Id == chapters[0].Id);
        blank.ContentText = "   ";
        await db.SaveChangesAsync();

        var outcome = await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        var skipped = Assert.Single(outcome.Skipped);
        Assert.Equal(chapters[0].Id, skipped.ChapterId);
        Assert.Equal(BookIntelligenceService.ChapterSkipReason.NoContent, skipped.Reason);

        Assert.Equal(chapters[1].Id, Assert.Single(outcome.Summarized).ChapterId);

        // Zero router calls attributable to the blank chapter — the ONLY call in the whole pass is the
        // normal chapter's summarize (no repair needed for the clean summary).
        Assert.Equal(new[] { "summarize" }, callLog);

        var rowExists = await db.ChunkSummaries.AnyAsync(cs => cs.ChapterId == chapters[0].Id);
        Assert.False(rowExists);
    }

    // ── (5c) THE ALL-SKIPPED PASS: NO SaveChanges AT ALL ─────────────────────────────────────────────────

    /// <summary>
    /// be-f05: <c>if (persisted.Count > 0) await _db.SaveChangesAsync(ct)</c> (the gate be-c02 narrowed from
    /// "anything was summarized" to "anything was actually WRITTEN") means a pass in which EVERY chapter is
    /// skipped must issue NO <c>SaveChanges</c> call at all — not an empty no-op save, none.
    /// Both chapters are seeded with a fresher-than-the-chapter <see cref="ChunkSummary"/> row (the existing
    /// freshness idiom), so every chapter takes the <see cref="BookIntelligenceService.ChapterSkipReason.Fresh"/>
    /// guard and phase 2/3 never run.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_AllChaptersSkipped_IssuesNoSaveChanges_AndSummarizedIsEmpty()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>(),
            replacementByLatinName: new Dictionary<string, string>(),
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 2);

        foreach (var chapter in chapters)
        {
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chapter.Id, Language = "he",
                SummaryText = "סיכום טרי קיים."
            });
        }
        await db.SaveChangesAsync();
        foreach (var row in await db.ChunkSummaries.Where(cs => cs.BookId == bookId).ToListAsync())
        {
            row.CreatedAt = DateTimeOffset.UtcNow.AddHours(2);
        }
        await db.SaveChangesAsync();

        // The persist boundary, attached AFTER all seeding is done so it only captures SaveChanges calls
        // made by the method under test (same probe used at the top of this file).
        db.SavingChanges += (_, _) => callLog.Add("save");

        var outcome = await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        Assert.Empty(outcome.Summarized);
        Assert.Equal(2, outcome.Skipped.Count);
        Assert.All(outcome.Skipped,
            s => Assert.Equal(BookIntelligenceService.ChapterSkipReason.Fresh, s.Reason));

        Assert.DoesNotContain("save", callLog);
        Assert.DoesNotContain("summarize", callLog);
        Assert.DoesNotContain("repair", callLog);
    }

    // ── (6) THE ChunkSummary DUAL-SURFACE CONTRACT ───────────────────────────────────────────────────────

    /// <summary>
    /// Non-negotiable (iv). The row is shared with the STRUCTURED writer (ChapterBriefService owns
    /// StructuredJson / StructuredBuiltAt / BuiltWithModel) and the USER-EDIT writer (SummaryUserEdited /
    /// SummaryUserEditedAt). The batched flat path must write exactly the three flat columns — SummaryText,
    /// Language (NORMALIZED, "en-US" -> "en"), CreatedAt — and orphan neither companion surface.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_WritesOnlyTheFlatSurface_AndNormalizesTheLocale()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string> { [ChapterText(0)] = CleanSummary(Marker(0)) },
            replacementByLatinName: new Dictionary<string, string>(),
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 1);

        var structuredBuiltAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chapters[0].Id, Language = "he",
            SummaryText = "סיכום ישן.",
            StructuredJson = "{\"beats\":[\"פתיחה\"]}",
            StructuredBuiltAt = structuredBuiltAt,
            BuiltWithModel = "qwen3.5:9b"
        });
        await db.SaveChangesAsync();
        var stale = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapters[0].Id);
        stale.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();

        var before = DateTimeOffset.UtcNow;
        await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersAsync(bookId, "en-US", CancellationToken.None);   // RAW locale in

        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapters[0].Id);

        // Flat surface: rewritten, under the NORMALIZED locale, with a bumped freshness stamp.
        Assert.Equal(CleanSummary(Marker(0)), row.SummaryText);
        Assert.Equal("en", row.Language);
        Assert.True(row.CreatedAt >= before);

        // Structured surface: untouched, and its own freshness stamp NOT bumped.
        Assert.Equal("{\"beats\":[\"פתיחה\"]}", row.StructuredJson);
        Assert.Equal("qwen3.5:9b", row.BuiltWithModel);
        Assert.Equal(structuredBuiltAt, row.StructuredBuiltAt);

        // User-edit surface: untouched.
        Assert.False(row.SummaryUserEdited);
        Assert.Null(row.SummaryUserEditedAt);
    }

    // ── (6b) THE FRESHNESS STAMP IS ANCHORED TO SUMMARIZE TIME, NOT PERSIST TIME ─────────────────────────

    /// <summary>
    /// be-c01. Batching moved the CreatedAt write out of "immediately after this chapter's own model call"
    /// and into phase 3, which runs only after EVERY chapter has been summarized and repaired - minutes later
    /// on a real book. Because the freshness guard is <c>CreatedAt &gt;= Chapter.UpdatedAt</c>, a chapter the
    /// user edits DURING the pass (after its own summary was produced, before the batch persists) would get
    /// <c>CreatedAt &gt; UpdatedAt</c> and be classified fresh PERMANENTLY, pinned forever to a summary of the
    /// pre-edit text that no automatic pass would ever rebuild.
    ///
    /// The concurrent edit is reproduced WITHOUT threads by the side-effecting-fake technique this file
    /// already uses for <see cref="ThrowOnNthAccessAiOptions"/>: while the router answers the LAST chapter's
    /// Summarization request, it edits two EARLIER chapters. The <see cref="Thread.Sleep"/> pair is what makes
    /// the ordering real rather than sub-tick: the edit must land strictly AFTER those chapters' summarize
    /// stamps and strictly BEFORE the batch persist, and 25 ms clears even a coarse (15.6 ms) system clock.
    ///
    /// BOTH persist branches are covered, because CreatedAt is written in two places: chapter 0 already has a
    /// (stale) ChunkSummary row so it takes the UPDATE branch, chapter 1 has none so it takes the INSERT
    /// branch - where "leave it to the default" means AppDbContext.SaveChangesAsync stamps PERSIST time, the
    /// same defect by another route.
    ///
    /// REVERT-VERIFY: restore <c>existing.CreatedAt = DateTimeOffset.UtcNow</c> (update branch) or drop the
    /// explicit <c>CreatedAt</c> from the insert, and the corresponding STALE-SKIP assertion below fails - the
    /// re-run skips the edited chapter as Fresh.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_ChapterEditedMidPass_StampsSummarizeTime_SoTheNextPassStillReSummarizes()
    {
        var callLog = new List<string>();
        Action<AiRequest>? editEarlierChaptersMidPass = null;
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = CleanSummary(Marker(0)),
                [ChapterText(1)] = CleanSummary(Marker(1)),
                [ChapterText(2)] = CleanSummary(Marker(2)),
                [ChapterText(3)] = CleanSummary(Marker(3))
            },
            replacementByLatinName: new Dictionary<string, string>(),
            callLog,
            onSummarize: req => editEarlierChaptersMidPass?.Invoke(req));

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 4);

        // Chapter 0 already has a STALE row (so the pass takes the UPDATE branch for it); chapter 1 has none
        // (INSERT branch). The two-step seed is this file's existing freshness idiom.
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chapters[0].Id, Language = "he", SummaryText = "סיכום ישן."
        });
        await db.SaveChangesAsync();
        var staleRow = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapters[0].Id);
        staleRow.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();

        var edited = false;
        var editedAt = default(DateTimeOffset);
        editEarlierChaptersMidPass = req =>
        {
            // One-shot, and only while the LAST chapter is being summarized: chapters 0 and 1 have already
            // been summarized and buffered, and the batch has not persisted anything yet.
            if (edited || !req.InputText.Contains(ChapterText(3), StringComparison.Ordinal))
                return;
            edited = true;

            Thread.Sleep(25);
            editedAt = DateTimeOffset.UtcNow;
            foreach (var target in new[] { chapters[0], chapters[1] })
            {
                target.ContentText += " תוספת של המשתמש.";
                target.UpdatedAt = editedAt;
            }
            // Sync SaveChanges deliberately: only SaveChangesAsync carries the UpdatedAt auto-stamp override,
            // so the edit lands with EXACTLY the timestamp above, and the entities leave the tracker Unchanged
            // so the batch's own SaveChangesAsync cannot re-stamp them later.
            db.SaveChanges();
            Thread.Sleep(25);
        };

        var service = provider.GetRequiredService<BookIntelligenceService>();
        var first = await service.SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        // The side effect really fired (otherwise everything below is a vacuous green).
        Assert.True(edited, "the mid-pass edit never ran; the router hook did not fire");
        Assert.Equal(4, first.Summarized.Count);
        Assert.Empty(first.Skipped);

        // (i) Each edited chapter's row carries its SUMMARIZE time, which is EARLIER than the edit - i.e. the
        //     row is honestly stale, not falsely fresh.
        foreach (var editedId in new[] { chapters[0].Id, chapters[1].Id })
        {
            var chapter = await db.Chapters.AsNoTracking().SingleAsync(c => c.Id == editedId);
            var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == editedId);
            Assert.Equal(editedAt, chapter.UpdatedAt);
            Assert.True(
                row.CreatedAt < chapter.UpdatedAt,
                $"CreatedAt {row.CreatedAt:O} must predate the mid-pass edit at {chapter.UpdatedAt:O}");
        }

        // (ii) THE USER-VISIBLE DEFECT: a second pass must RE-SUMMARIZE both edited chapters rather than skip
        //      them as fresh. The two untouched chapters are correctly skipped, so the re-run stays targeted.
        var second = await service.SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);
        var reSummarized = second.Summarized.Select(o => o.ChapterId).ToList();

        Assert.True(
            reSummarized.Contains(chapters[0].Id),
            "STALE-SKIP (existing-row UPDATE branch): the chapter edited mid-pass was classified fresh and " +
            "skipped, so its pre-edit summary is now permanent. Its CreatedAt must be its own summarize " +
            "time, not the batch persist time.");
        Assert.True(
            reSummarized.Contains(chapters[1].Id),
            "STALE-SKIP (new-row INSERT branch): the chapter edited mid-pass was classified fresh and " +
            "skipped, so its pre-edit summary is now permanent. Its CreatedAt must be set explicitly to its " +
            "own summarize time, not left to the SaveChanges override's persist-time stamp.");
        Assert.Equal(2, reSummarized.Count);
        Assert.Equal(
            new[]
            {
                new BookIntelligenceService.SkippedChapter(
                    chapters[2].Id, BookIntelligenceService.ChapterSkipReason.Fresh),
                new BookIntelligenceService.SkippedChapter(
                    chapters[3].Id, BookIntelligenceService.ChapterSkipReason.Fresh)
            },
            second.Skipped);
    }

    // ── (6c) THE wb3-c04 CLOBBER GUARD IS RE-CHECKED AGAINST DATABASE TRUTH AT PERSIST TIME ──────────────

    /// <summary>
    /// be-c02, the SILENT USER-CONTENT LOSS. The clobber guard is READ in phase 1 and the write it guards
    /// happens in phase 3, so batching stretched its check-to-act window from one chapter's model call to the
    /// WHOLE PASS. <see cref="ChunkSummary"/> carries no concurrency token, so the tracked phase-1 entity is
    /// written back over anything the PUT-summary path committed in between - losing the user's manual text
    /// AND leaving <c>SummaryUserEdited</c> true (this path never writes that column), so the row then CLAIMS
    /// to hold a manual edit while holding machine text and the guard protects the MACHINE text forever.
    ///
    /// UPDATE BRANCH: chapter 0 already has a stale, NOT-user-edited row, so it passes both phase-1 guards and
    /// is summarized; while the LAST chapter is being summarized the user "PUTs" an edit onto chapter 0's row
    /// (the same side-effecting-router technique the be-c01 test uses, with the same sync-<c>SaveChanges</c>
    /// so the entity leaves the tracker Unchanged and no auto-stamp fires).
    ///
    /// REVERT-VERIFY: drop the phase-3 re-check and the SummaryText assertion below fails with the machine
    /// summary in place of the user sentinel.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_RowUserEditedMidPass_DoesNotOverwriteTheUserEdit_UpdateBranch()
    {
        const string userSentinel = "USER-EDIT-MID-PASS-SENTINEL - אין לדרוס.";

        var callLog = new List<string>();
        Action<AiRequest>? editMidPass = null;
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = CleanSummary(Marker(0)),
                [ChapterText(1)] = CleanSummary(Marker(1))
            },
            replacementByLatinName: new Dictionary<string, string>(),
            callLog,
            onSummarize: req => editMidPass?.Invoke(req));

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 2);

        // Chapter 0: an existing, STALE, NOT-user-edited row → passes both phase-1 guards (update branch).
        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId, ChapterId = chapters[0].Id, Language = "he", SummaryText = "סיכום ישן."
        });
        await db.SaveChangesAsync();
        var staleRow = await db.ChunkSummaries.SingleAsync(cs => cs.ChapterId == chapters[0].Id);
        staleRow.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();

        var edited = false;
        editMidPass = req =>
        {
            // One-shot, and only while the LAST chapter is being summarized: chapter 0 has already cleared the
            // phase-1 guard and been buffered, and the batch has not persisted anything yet.
            if (edited || !req.InputText.Contains(ChapterText(1), StringComparison.Ordinal))
                return;
            edited = true;

            // Exactly what BooksController.UpdateChapterSummary writes on the existing-row branch.
            staleRow.SummaryText = userSentinel;
            staleRow.SummaryUserEdited = true;
            staleRow.SummaryUserEditedAt = DateTimeOffset.UtcNow;
            db.SaveChanges();
        };

        var outcome = await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        Assert.True(edited, "the mid-pass user edit never ran; the router hook did not fire");

        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapters[0].Id);
        Assert.Equal(userSentinel, row.SummaryText);
        Assert.True(row.SummaryUserEdited,
            "the row must still be flagged user-edited, and it must be flagged over the USER's text");

        // The chapter is reported as WITHHELD, not as summarized: it was summarized and repaired, but nothing
        // was persisted for it, so it must not appear in Summarized (whose members promise a PersistedSummary).
        Assert.Equal(
            new[]
            {
                new BookIntelligenceService.SkippedChapter(
                    chapters[0].Id, BookIntelligenceService.ChapterSkipReason.UserEditedDuringPass)
            },
            outcome.Skipped);
        Assert.Equal(chapters[1].Id, Assert.Single(outcome.Summarized).ChapterId);

        // The other chapter still persisted normally - one poisoned row does not cost the batch.
        var other = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapters[1].Id);
        Assert.Equal(CleanSummary(Marker(1)), other.SummaryText);
    }

    /// <summary>
    /// The INSERT-BRANCH MIRROR of the case above. A chapter with NO row in phase 1 can have one by phase 3:
    /// <c>BooksController.UpdateChapterSummary</c> INSERTS a row (its <c>row == null</c> branch) for a chapter
    /// that was never summarized. ChunkSummary has a UNIQUE index on (BookId, ChapterId) - AppDbContext,
    /// <c>modelBuilder.Entity&lt;ChunkSummary&gt;</c> - so a blind <c>Add</c> here fails the whole
    /// <c>SaveChanges</c> on SQL Server and loses the ENTIRE batch, and on the in-memory provider (which does
    /// not enforce unique indexes) silently duplicates the row instead. Both are asserted below by requiring
    /// exactly ONE row carrying the user's text.
    ///
    /// REVERT-VERIFY: drop the phase-3 re-check and this fails on the row count (two rows for one chapter).
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_RowCreatedByUserMidPass_DoesNotDuplicateOrClobberIt_InsertBranch()
    {
        const string userSentinel = "USER-CREATED-MID-PASS-SENTINEL - אין לדרוס.";

        var callLog = new List<string>();
        Action<AiRequest>? createMidPass = null;
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = CleanSummary(Marker(0)),
                [ChapterText(1)] = CleanSummary(Marker(1))
            },
            replacementByLatinName: new Dictionary<string, string>(),
            callLog,
            onSummarize: req => createMidPass?.Invoke(req));

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 2);   // NO ChunkSummary rows at all

        var created = false;
        createMidPass = req =>
        {
            // One-shot, while the LAST chapter is summarized: chapter 0 was buffered with NO existing row.
            if (created || !req.InputText.Contains(ChapterText(1), StringComparison.Ordinal))
                return;
            created = true;

            // Exactly what BooksController.UpdateChapterSummary writes on the new-row branch.
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chapters[0].Id, Language = "he",
                SummaryText = userSentinel,
                SummaryUserEdited = true,
                SummaryUserEditedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        };

        var outcome = await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        Assert.True(created, "the mid-pass user creation never ran; the router hook did not fire");

        var rows = await db.ChunkSummaries.AsNoTracking()
            .Where(cs => cs.ChapterId == chapters[0].Id).ToListAsync();
        var row = Assert.Single(rows);   // never a duplicate of the (BookId, ChapterId) unique key
        Assert.Equal(userSentinel, row.SummaryText);
        Assert.True(row.SummaryUserEdited);

        Assert.Equal(
            new[]
            {
                new BookIntelligenceService.SkippedChapter(
                    chapters[0].Id, BookIntelligenceService.ChapterSkipReason.UserEditedDuringPass)
            },
            outcome.Skipped);
        Assert.Equal(chapters[1].Id, Assert.Single(outcome.Summarized).ChapterId);
    }

    // ── (7) THE OPT-OUT SEAM IS NOT REACHABLE BY ACCIDENT, AND EVERY OTHER CALLER STILL REPAIRS ───────────

    /// <summary>
    /// Non-negotiable (vi). The deferred (repair-less) seam must NOT be a general, publicly reachable
    /// "raw without repair" path — that is the exact bug class this feature has already shipped twice (the
    /// glossary skipped RunRawAsync; then the entity lever skipped it). Pins the three properties that make
    /// it hard to reach by accident: both members are <c>internal</c>, the carrier type is <c>internal</c>,
    /// and the producer does not return a bare <c>string</c> (so an un-repaired value cannot be persisted
    /// where a finished summary is expected without a compile error).
    /// </summary>
    [Fact]
    public void DeferredRepairSeam_IsInternalOnly_AndCannotBeMistakenForAFinishedResult()
    {
        var type = typeof(UnifiedAnalysisService);

        Assert.Null(type.GetMethod("RunRawDeferredRepairAsync", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(type.GetMethod("CompleteDeferredRepairAsync", BindingFlags.Public | BindingFlags.Instance));

        var producer = type.GetMethod("RunRawDeferredRepairAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var consumer = type.GetMethod("CompleteDeferredRepairAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(producer);
        Assert.NotNull(consumer);
        Assert.True(producer!.IsAssembly, "the repair-less producer must be internal, never protected/public");
        Assert.True(consumer!.IsAssembly, "the deferred repair consumer must be internal");

        // The producer hands back a CARRIER, not a string: "I forgot to repair" is a compile error.
        var carrier = producer.ReturnType.GetGenericArguments().Single();
        Assert.Equal("DeferredRepairRawRun", carrier.Name);
        Assert.NotEqual(typeof(string), carrier);
        Assert.True(carrier.IsNestedAssembly, "the un-repaired carrier type must not be public");

        // No public member anywhere on the service exposes the carrier (no accidental escape hatch).
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            m => m.ReturnType == carrier
                 || (m.ReturnType.IsGenericType && m.ReturnType.GetGenericArguments().Contains(carrier)));
    }

    /// <summary>
    /// The other half of (vi): splitting RunRawAsync into producer + consumer must leave the PUBLIC raw seam
    /// repairing exactly as before — it is now literally the composition of the two halves, so any caller
    /// that did not opt into deferral still gets the repair.
    /// </summary>
    [Fact]
    public async Task RunRawAsync_StillRepairs_AfterTheDeferredSplit()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string> { ["טקסט הפרק"] = LeakingSummary("Marlow", "פרק אלף") },
            replacementByLatinName: new Dictionary<string, string> { ["Marlow"] = "מרלו" },
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var result = await provider.GetRequiredService<UnifiedAnalysisService>().RunRawAsync(
            "טקסט הפרק לניתוח.", AnalysisType.Summarization, instruction: null, language: "he",
            bookId: Guid.NewGuid(), ct: CancellationToken.None);

        Assert.Contains("מרלו", result);
        Assert.DoesNotContain("Marlow", result);
    }

    /// <summary>
    /// ...and so does the PERSISTED seam (<see cref="UnifiedAnalysisService.RunAsync"/>), which shares
    /// ApplyAnalysisRepairAsync with the raw path but not the deferral. A Hebrew chapter Summarization run
    /// through it still lands repaired in the persisted AnalysisResult.
    /// </summary>
    [Fact]
    public async Task RunAsync_PersistedSeam_StillRepairs_AfterTheDeferredSplit()
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string> { [ChapterText(0)] = LeakingSummary("Marlow", Marker(0)) },
            replacementByLatinName: new Dictionary<string, string> { ["Marlow"] = "מרלו" },
            callLog);

        using var provider = BuildProvider(router, ShippedRepairOptions());
        var db = provider.GetRequiredService<AppDbContext>();
        var (_, chapters) = await SeedBookAsync(db, 1);

        var result = await provider.GetRequiredService<UnifiedAnalysisService>().RunAsync(
            AnalysisScope.Chapter, AnalysisType.Summarization, chapters[0].Id,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        Assert.Contains("מרלו", result.ResultText);
        Assert.DoesNotContain("Marlow", result.ResultText);
    }

    // ── (9) be-c03: MONOTONIC PROGRESS across checkpoint windows ──────────────────────────────────────────

    /// <summary>The shipped repair configuration with an explicit checkpoint window, so a test can put a
    /// window BOUNDARY inside a book small enough to reason about. Everything else matches
    /// <see cref="ShippedRepairOptions"/>; the default window (10) is above every chapter count in this file,
    /// which is why every OTHER test here still runs as a single window.</summary>
    private static AnalysisRepairOptions WindowedRepairOptions(int windowChapters)
    {
        var options = ShippedRepairOptions();
        options.SummaryBatchWindowChapters = windowChapters;
        return options;
    }

    /// <summary>
    /// be-c03, the LOST-PROGRESS regression. Four chapters, checkpoint window of two, aborted while the
    /// SECOND window is summarizing. The first window must already be durably persisted, and the retry must
    /// re-summarize ONLY the remainder.
    ///
    /// Why this matters and why "a re-run is idempotent" was not a sufficient answer: this pass is awaited
    /// inline on the request thread with the REQUEST token (BooksController.Summarize / RefreshProfile), it
    /// costs a measured ~18-27 s per chapter (docs/ANALYSIS_OUTPUT_REPAIR.md section 19), and the project's
    /// corpus contains an 80-chapter book, so a first pass over it runs 24-37 minutes. Under a single commit
    /// at the very end, a reload, a gateway idle ceiling, or the OOM-wedged Ollama runner this host has
    /// actually produced discarded ALL of it - and since such a failure recurs at roughly the SAME point, the
    /// "idempotent re-run" never converged. Idempotence is a CORRECTNESS property; this test pins PROGRESS.
    ///
    /// The abort is modelled as the router throwing <c>OperationCanceledException</c> for a token that really
    /// is cancelled, which is what a cancelled HttpClient call does. It is one-shot, so the SAME router serves
    /// the retry - the side-effecting-fake idiom this file already uses, deterministic and thread-free.
    ///
    /// Every chapter LEAKS, so the assertion that survives the checkpoint is not merely "a row exists" but
    /// "a REPAIRED row exists": the non-negotiable invariant is that a window persists only after its OWN
    /// repair pass, and un-repaired prose is never written even transiently.
    ///
    /// REVERT-VERIFY: against the un-windowed code (one SaveChanges after every chapter is repaired) the
    /// first pass persists NOTHING, so the "window 0 must survive" assertions fail by name.
    /// </summary>
    [Fact]
    public async Task SummarizeChapters_AbortedMidPass_KeepsCompletedWindows_AndReSummarizesOnlyTheRemainder()
    {
        var callLog = new List<string>();
        using var cts = new CancellationTokenSource();
        var aborted = false;

        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = LeakingSummary("Marlow", Marker(0)),
                [ChapterText(1)] = LeakingSummary("Rowan", Marker(1)),
                [ChapterText(2)] = LeakingSummary("Sedgwick", Marker(2)),
                [ChapterText(3)] = LeakingSummary("Thorne", Marker(3))
            },
            replacementByLatinName: new Dictionary<string, string>
            {
                ["Marlow"] = "מרלו", ["Rowan"] = "רואן", ["Sedgwick"] = "סדגוויק", ["Thorne"] = "תורן"
            },
            callLog,
            onSummarize: req =>
            {
                // One-shot, on the FIRST chapter of the SECOND window: window 0 (chapters 0-1) has been
                // summarized, repaired and committed; window 1 has produced nothing yet.
                if (aborted || !req.InputText.Contains(ChapterText(2), StringComparison.Ordinal)) return;
                aborted = true;
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        using var provider = BuildProvider(router, WindowedRepairOptions(windowChapters: 2));
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapters) = await SeedBookAsync(db, 4);
        db.SavingChanges += (_, _) => callLog.Add("save");

        var service = provider.GetRequiredService<BookIntelligenceService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SummarizeChaptersCoreAsync(bookId, "he", cts.Token));

        Assert.True(aborted, "the abort hook never ran; everything below would be a vacuous green");

        // (i) LOST PROGRESS - the defect itself, asserted on the user-visible outcome (what is in the
        //     database) BEFORE any call-ordering proxy, so a revert reds out on the assertion that NAMES the
        //     bug rather than on a symptom of it. Window 0's chapters must have survived the abort, holding
        //     their OWN repaired summaries (correlation by identity, and repaired BEFORE the commit).
        var afterAbort = await db.ChunkSummaries.AsNoTracking()
            .Where(cs => cs.BookId == bookId).ToListAsync();

        Assert.True(
            afterAbort.Count == 2,
            "LOST PROGRESS: an abort during a LATER window discarded the windows already completed. " +
            $"Expected the 2 chapters of the completed window to be persisted, found {afterAbort.Count}. " +
            "The chapter-summary pass must checkpoint per window, not persist once at the very end - a book " +
            "whose full pass outlives a client/gateway timeout would otherwise never persist a single summary.");

        foreach (var (chapterIndex, latin, hebrew) in new[] { (0, "Marlow", "מרלו"), (1, "Rowan", "רואן") })
        {
            var row = Assert.Single(afterAbort, cs => cs.ChapterId == chapters[chapterIndex].Id);
            Assert.Contains(hebrew, row.SummaryText);
            Assert.DoesNotContain(latin, row.SummaryText);
            Assert.Contains(Marker(chapterIndex), row.SummaryText);
        }

        // (ii) ...and the un-run window wrote nothing at all.
        Assert.DoesNotContain(afterAbort, cs => cs.ChapterId == chapters[2].Id);
        Assert.DoesNotContain(afterAbort, cs => cs.ChapterId == chapters[3].Id);

        // (iii) THE CHECKPOINT FIRED BEFORE THE ABORT. Window 0's two summaries, then its two repairs, then
        //       its save - and only THEN the summarize call that aborts. The save's POSITION is the whole
        //       point: it is after window 0's repairs (nothing un-repaired is persisted) and before window
        //       1's work (progress is not held hostage to the rest of the book).
        Assert.Equal(
            new[] { "summarize", "summarize", "repair", "repair", "save", "summarize" },
            callLog);

        // (iv) THE RETRY MAKES PROGRESS INSTEAD OF REDOING THE BOOK. The completed window is now Fresh and
        //      costs zero model calls; only chapters 2 and 3 are summarized.
        callLog.Clear();
        var retry = await service.SummarizeChaptersCoreAsync(bookId, "he", CancellationToken.None);

        Assert.Equal(
            new[] { chapters[2].Id, chapters[3].Id },
            retry.Summarized.Select(o => o.ChapterId).ToArray());
        Assert.Equal(
            new[]
            {
                new BookIntelligenceService.SkippedChapter(
                    chapters[0].Id, BookIntelligenceService.ChapterSkipReason.Fresh),
                new BookIntelligenceService.SkippedChapter(
                    chapters[1].Id, BookIntelligenceService.ChapterSkipReason.Fresh)
            },
            retry.Skipped);
        Assert.Equal(
            new[] { "summarize", "summarize", "repair", "repair", "save" },
            callLog);

        // (v) The book is whole, and every chapter carries its OWN repaired summary.
        var final = await db.ChunkSummaries.AsNoTracking().Where(cs => cs.BookId == bookId).ToListAsync();
        Assert.Equal(4, final.Count);
        foreach (var (chapterIndex, latin, hebrew) in
                 new[] { (0, "Marlow", "מרלו"), (1, "Rowan", "רואן"), (2, "Sedgwick", "סדגוויק"), (3, "Thorne", "תורן") })
        {
            var row = Assert.Single(final, cs => cs.ChapterId == chapters[chapterIndex].Id);
            Assert.Contains(hebrew, row.SummaryText);
            Assert.DoesNotContain(latin, row.SummaryText);
            Assert.Contains(Marker(chapterIndex), row.SummaryText);
        }
    }

    /// <summary>
    /// The other half of the window contract, and the reason every OTHER test in this file was unaffected:
    /// a window at or above the chapter count reproduces the original single-commit behaviour EXACTLY - one
    /// summarize burst, one repair burst, one save. A non-positive configured value is CLAMPED to the class
    /// default rather than read as "no windowing", so a stray 0 cannot silently restore the all-or-nothing
    /// persist; with 4 chapters and a default of 10 that is observationally the same single window.
    /// </summary>
    [Theory]
    [InlineData(4)]      // exactly the chapter count
    [InlineData(99)]     // far above it
    [InlineData(0)]      // clamped to the default (10), which is still above the chapter count
    [InlineData(-1)]     // ditto
    public async Task SummarizeChapters_WindowAtOrAboveChapterCount_ReproducesTheSingleCommitBehaviour(int window)
    {
        var callLog = new List<string>();
        var router = BuildRouter(
            summaryByChapterText: new Dictionary<string, string>
            {
                [ChapterText(0)] = LeakingSummary("Marlow", Marker(0)),
                [ChapterText(1)] = LeakingSummary("Rowan", Marker(1)),
                [ChapterText(2)] = LeakingSummary("Sedgwick", Marker(2)),
                [ChapterText(3)] = LeakingSummary("Thorne", Marker(3))
            },
            replacementByLatinName: new Dictionary<string, string>
            {
                ["Marlow"] = "מרלו", ["Rowan"] = "רואן", ["Sedgwick"] = "סדגוויק", ["Thorne"] = "תורן"
            },
            callLog);

        using var provider = BuildProvider(router, WindowedRepairOptions(window));
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, _) = await SeedBookAsync(db, 4);
        db.SavingChanges += (_, _) => callLog.Add("save");

        await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersAsync(bookId, "he", CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "summarize", "summarize", "summarize", "summarize",
                "repair", "repair", "repair", "repair",
                "save"
            },
            callLog);
    }
}
