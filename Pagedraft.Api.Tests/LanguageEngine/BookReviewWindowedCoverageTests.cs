using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// WINDOWED-COVERAGE live measurement (api-whole-book-editing-p4, part 7). Where
/// <see cref="BookReviewQualityTests"/> mirrors <c>RunDimensionAsync</c> DIRECTLY (it bypasses the DB +
/// assembler + the new windowed MAP), THIS harness drives the FULL production
/// <see cref="BookReviewService.BuildBookReviewAsync"/> end-to-end over the LARGE 48-chapter gold book so it
/// can PROVE the Phase-4 windowing actually restored coverage:
///
///   (i)  the LATE-chapter developmental defect (planted at chapter 40) is SURFACED — a persisted finding
///        anchored at/near the planted late chapter in the right dimension. The pre-Phase-4 single-pass engine
///        assembled ONE 8192-token context and DROPPED every chapter past the budget (it reviewed only ch 0-5
///        on a big book), so it could NEVER reach chapter 40. Surfacing it proves coverage is restored.
///   (ii) the DISTANT CONTINUITY BREAK (ch 5 kills mentor Rourke; ch 45 has him alive) is FLAGGED by the
///        continuity-reduce pass — a dimension='continuity' finding whose anchors SPAN the two distant
///        chapters. The two chapters are ~40 apart and never co-occur in one 8192-token window, so only the
///        hierarchical continuity reduce (which reads a dense skeleton of every chapter's states + threads)
///        can catch it. The single-pass engine, seeing only ch 0-5, could not.
///   (iii) coverage reports N/N: <see cref="BookReviewBuildResult.ChaptersReviewed"/> ==
///        <see cref="BookReviewBuildResult.ChaptersTotal"/> == 48 (the honest-coverage claim).
///
/// HOW IT SEEDS THE DB — the same pattern as <see cref="BookReviewServiceTests"/> (in-memory
/// <see cref="AppDbContext"/>, a book + chapters + a cached <see cref="BookSummaryBaseline"/> + one fresh
/// structured <see cref="ChunkSummary"/> per chapter), except each chapter's structured brief is loaded from
/// the large gold fixture so the summed briefs exceed the ~8192-token production budget SEVERAL times over and
/// the REAL <see cref="BookContextAssembler.AssembleWindowsAsync"/> genuinely partitions the book into
/// MULTIPLE windows. The briefs are stamped with the resolved Summarization model so they read FRESH with NO
/// summarization LLM call — the only live model calls are the review's window + reduce calls. NO
/// <c>BookBibles</c> row is seeded for this book, so <c>BookReviewService.LoadCharacterRegisterForReviewAsync</c>
/// resolves a null character register for this run too, same as the model-free window-partition guard below —
/// the production-faithful budget claim on that guard's <c>AssembleWindowsAsync</c> call holds here as well, by
/// the same accident of an unregistered fixture rather than by construction.
///
/// HOW IT IS WIRED — the REAL Ollama-backed <see cref="IAiRouter"/> (the SAME DI shape
/// <see cref="BookReviewQualityTests.CreateRouter"/> uses to reach the model), plus the production BookReview
/// tuning (Ollama_BookReview NumCtx=16384 / NumPredict=6144, mirrored from appsettings.json) so
/// <see cref="BookContextAssembler.ResolveBudgetTokens"/> derives the SAME ~8192-token budget production uses.
/// <see cref="AiOptions.BookContextTokenBudget"/> is left at 0 (derive) so windowing is production-faithful,
/// NOT the tiny forced budget the unit tests use.
///
/// GATING — SKIP-BY-DEFAULT via the SAME Ollama probe <see cref="BookReviewQualityTests"/> uses: if the
/// endpoint is unreachable it writes a message and RETURNS (passes) BEFORE seeding the DB or touching the
/// model, so with no model this class SKIPS cleanly in seconds and never runs the DB-heavy live path. Reachable
/// via the class-name filter (<c>~BookReviewWindowedCoverageTests</c>) or the shared
/// <c>~BookReviewQuality</c>-adjacent live filter, but NOT via the safe unit filters (it has no light [Fact]
/// that a unit run would sweep).
///
/// ENV MODEL KNOB: <c>BOOK_REVIEW_MODEL</c> overrides the review model WITHOUT recompiling (same knob the
/// sibling harness uses); default = <see cref="BookReviewQualityTests.DefaultBookReviewModel"/> (gemma4:12b,
/// kept in sync with appsettings via the wb2-f01 config-pin guard in BookReviewServiceTests).
/// </summary>
public class BookReviewWindowedCoverageTests
{
    private readonly ITestOutputHelper _output;

    public BookReviewWindowedCoverageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string OllamaBaseUrl = "http://localhost:11434";
    private const string ModelEnvVar = "BOOK_REVIEW_MODEL";

    // The Summarization stamp on the seeded briefs. Left EQUAL to AiOptions.DefaultModel below (no
    // Summarization feature model is configured, so ActiveSummarizationModel == DefaultModel) so
    // ComposeChapterBriefsAsync's IsFresh check passes and the assembler takes the dense structured path with
    // NO summarization model call.
    private const string SummarizationStamp = "seeded-summarization-model";

    private static readonly JsonSerializerOptions GoldOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions StructuredOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // ─── The live windowed-coverage eval (skip-by-default) ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "LiveModel")]
    public async Task WindowedReview_RestoresLateCoverage_AndFlagsDistantContinuityBreak()
    {
        // SKIP-GATE FIRST — before seeding the DB or building any provider — so with no model this returns
        // (passes) in seconds and never touches the DB-heavy live path.
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl}. " +
                              "This windowed-coverage measurement needs a live model; skipping so CI stays green.");
            return;
        }

        var largeBook = LoadLargeBook("bigbook-windowed-coverage-en");
        Assert.NotNull(largeBook);
        Assert.True(largeBook!.Chapters.Count >= 40,
            $"the large windowed-coverage book must have >= 40 chapters (had {largeBook.Chapters.Count}).");

        var model = ResolveModel();
        _output.WriteLine($"=== Windowed-coverage eval: book={largeBook.Id}, {largeBook.Chapters.Count} chapters, model={model} ===");

        using var provider = BuildProvider(model);
        var db = provider.GetRequiredService<AppDbContext>();

        // Seed the book + 48 chapters + one FRESH structured ChunkSummary per chapter + a cached
        // BookSummaryBaseline (the same seeding SHAPE as BookReviewServiceTests.SeedReviewableBookAsync, but the
        // per-chapter briefs come from the large fixture so the summed context forces MULTIPLE windows).
        var bookId = await SeedLargeBookAsync(db, largeBook);

        var svc = provider.GetRequiredService<BookReviewService>();

        // Drive the FULL production windowed build against the REAL model.
        var result = await svc.BuildBookReviewAsync(bookId, largeBook.Language);

        _output.WriteLine(
            $"build: ready={result.Ready} noOp={result.NoOp} briefsMissing={result.BriefsMissing} " +
            $"windows={result.WindowCount} chaptersReviewed={result.ChaptersReviewed}/{result.ChaptersTotal} " +
            $"ranSynthesis={result.RanSynthesis} ranContinuity={result.RanContinuityReduce} " +
            $"failedWindows={result.FailedWindows} findings={result.FindingCount}");
        _output.WriteLine($"message: {result.Message}");

        // The build must have PRODUCED a review (a total failure would mean the model choked on the big book).
        Assert.True(result.Ready, $"the windowed build over the large book must be Ready. Message: {result.Message}");
        Assert.False(result.BriefsMissing);

        // ── (iii) COVERAGE RESTORED: N/N chapters, and windowing actually happened (> 1 window). ──
        // The pre-Phase-4 engine would have reported far fewer reviewed chapters (it dropped everything past the
        // 8192-token budget). N/N across MULTIPLE windows is the honest end-to-end coverage claim.
        Assert.Equal(largeBook.Chapters.Count, result.ChaptersTotal);
        Assert.Equal(result.ChaptersTotal, result.ChaptersReviewed);
        Assert.True(result.WindowCount > 1,
            $"the large book must partition into MULTIPLE windows on the ~8192-token budget (was {result.WindowCount}); " +
            "a single window would mean the budget did not force windowing and the coverage claim is not exercised.");

        // Read back the persisted findings (what the FE would show).
        var persisted = await db.BookFindings.AsNoTracking()
            .Where(f => f.BookId == bookId).ToListAsync();
        _output.WriteLine($"persisted findings: {persisted.Count}");
        foreach (var f in persisted.OrderByDescending(f => f.Severity))
            _output.WriteLine($"  [{f.Dimension}/{f.Verdict}] sev={f.Severity} anchors={AnchorOrders(f)} :: {Truncate(f.Rationale, 90)}");

        var lateDefect = largeBook.PlantedDefects.Single(d =>
            string.Equals(d.Dimension, "character", StringComparison.OrdinalIgnoreCase));
        var continuityDefect = largeBook.PlantedDefects.Single(d =>
            string.Equals(d.Dimension, "continuity", StringComparison.OrdinalIgnoreCase));

        // ── (i) LATE-CHAPTER FINDING SURFACED: a finding in the accepted dimension anchored at/near the planted
        //        late chapter (order 39, chapter 40). Anchor leniency +/-1 (the same the sibling harness uses)
        //        covers a finding anchored on the neighbouring chapter. This is the coverage-restored proof: the
        //        old single-pass drop could not reach chapter 40 at all. ──
        var allowedLateDims = AllowedDimensions(lateDefect);
        var lateHit = persisted.FirstOrDefault(f =>
            allowedLateDims.Contains(Norm(f.Dimension)) && AnchorsNear(f, lateDefect.ChapterOrder, 1));
        Assert.True(lateHit != null,
            $"COVERAGE NOT RESTORED: no {string.Join("/", allowedLateDims)} finding anchored near chapter " +
            $"order {lateDefect.ChapterOrder} (chapter {lateDefect.ChapterOrder + 1}) — the late-chapter planted " +
            "defect was not surfaced. On the pre-Phase-4 engine this ALWAYS failed (the chapter was dropped); a " +
            "failure here means windowing did not restore late-chapter coverage.");
        _output.WriteLine($"LATE-CHAPTER HIT: [{lateHit!.Dimension}/{lateHit.Verdict}] anchors={AnchorOrders(lateHit)}");

        // ── (ii) DISTANT CONTINUITY BREAK FLAGGED: a dimension='continuity' finding whose anchors SPAN the two
        //         distant chapters (order 4 AND order 44). Only the continuity-reduce pass can see both across
        //         ~40 chapters. Anchor leniency +/-1 on each end. ──
        var spans = continuityDefect.SpansChapterOrders ?? new[] { continuityDefect.ChapterOrder };
        Assert.True(spans.Length == 2, "the continuity defect must record the TWO distant chapter orders it spans.");
        var (deathOrder, aliveOrder) = (spans.Min(), spans.Max());

        var continuityHit = persisted.FirstOrDefault(f =>
            Norm(f.Dimension) == "continuity"
            && AnchorsNear(f, deathOrder, 1)
            && AnchorsNear(f, aliveOrder, 1));
        Assert.True(continuityHit != null,
            $"CONTINUITY BREAK NOT FLAGGED: no dimension='continuity' finding spans BOTH chapter order {deathOrder} " +
            $"(the death, chapter {deathOrder + 1}) AND order {aliveOrder} (the impossible reappearance, chapter " +
            $"{aliveOrder + 1}). Only the continuity-reduce pass can catch a break spanning two chapters ~40 apart; " +
            "a failure here means the continuity pass did not connect the distant chapters.");
        _output.WriteLine($"CONTINUITY BREAK FLAGGED: anchors={AnchorOrders(continuityHit!)} :: {Truncate(continuityHit.Rationale, 120)}");
        Assert.True(result.RanContinuityReduce, "the continuity reduce pass must have run for a full-brief multi-chapter book.");

        _output.WriteLine("");
        _output.WriteLine("HEBREW DRAFT FLAG: the he parity book (bigbook-windowed-coverage-he) in book-review-gold-large.json " +
                          "is AI-authored and REQUIRES NATIVE SPEAKER VALIDATION before its Hebrew numbers are trusted. Run the " +
                          "he variant by loading 'bigbook-windowed-coverage-he' (same asserts, language 'he').");
    }

    // ─── MODEL-FREE window-partition guard (NO Ollama, runs in ms) ─────────────────────────────────────

    /// <summary>
    /// DETERMINISTIC guard (no model, no GPU, NOT gated on Ollama) that PROVES the live
    /// <see cref="WindowedReview_RestoresLateCoverage_AndFlagsDistantContinuityBreak"/> precondition — that the
    /// large gold book genuinely partitions into MULTIPLE windows at the PRODUCTION budget — without spending a
    /// single model call. It seeds the SAME DB shape the live harness uses (one fresh structured
    /// <see cref="ChunkSummary"/> per gold chapter + a cached <see cref="BookSummaryBaseline"/>), then calls the
    /// REAL <see cref="BookContextAssembler.AssembleWindowsAsync"/> with the SAME consuming task the production
    /// build passes (<see cref="AiTaskType.BookReview"/>, NumCtx=16384 → derived ~8192-token budget) and asserts:
    ///
    ///   (a) the book partitions into <c>Count &gt;= 4</c> windows (the live test only needs &gt; 1, so this is a
    ///       STRICTER guard: a fixture whose briefs go terse and collapse back to one window fails HERE, in ms,
    ///       instead of silently defeating the live coverage claim);
    ///   (b) EVERY chapter order 0..47 appears as a PRIMARY (IncludedChapterOrders \ OverlapChapterOrders) in
    ///       EXACTLY ONE window — no chapter dropped, none duplicated across primaries;
    ///   (c) the two planted chapters (order 4, the Rourke death; order 44, the impossible reappearance) land in
    ///       DIFFERENT primary windows, so the continuity break truly SPANS windows and only the continuity-reduce
    ///       pass (not a single window) can catch it.
    ///
    /// AssembleWindowsAsync is pure DB-read + string composition (no <see cref="IAiRouter"/> call), so this uses
    /// the SAME <see cref="BuildProvider"/> DI as the live test but NEVER reaches the model. The window count is
    /// emitted via <see cref="ITestOutputHelper"/>.
    /// </summary>
    [Fact]
    public async Task LargeBook_ForcesMultipleWindows_NoChapterDropped_PlantedChaptersSeparated()
    {
        var largeBook = LoadLargeBook("bigbook-windowed-coverage-en");
        Assert.NotNull(largeBook);
        Assert.Equal(48, largeBook!.Chapters.Count);

        // Same DI + seeding the live test uses, but we stop at the DETERMINISTIC assembler — no model call.
        using var provider = BuildProvider(BookReviewQualityTests.DefaultBookReviewModel);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedLargeBookAsync(db, largeBook);

        var assembler = provider.GetRequiredService<BookContextAssembler>();

        // Production-faithful budget: the SAME consuming task the production build passes so ResolveBudgetTokens
        // derives the SAME ~8192-token window budget (Ollama_BookReview NumCtx=16384 in BuildProvider).
        //
        // characterRegister IS EXPLICIT, not merely absent: production also charges a [BOOK_CHARACTERS] block
        // into this SAME per-window budget when a register exists (BookContextAssembler.AssembleWindowsAsync's
        // characterRegister param doc; the registerBlock/registerBlockTokens pair and the
        // `used = briefBlockTokens + registerBlockTokens + overlapTokens` line), which would make every window
        // NARROWER and could change the window count this test asserts. This fixture's SeedLargeBookAsync never
        // writes a db.BookBibles row for the seeded book, so LoadCharacterRegisterForReviewAsync (the production
        // caller) would resolve null here too - the SAME null this test passes. So this call's fidelity to
        // production holds ONLY for a book with no character register, which is what this fixture is; passing
        // `characterRegister: null` by name makes that a deliberate, visible choice instead of a default a
        // future signature change could move silently.
        var windows = await assembler.AssembleWindowsAsync(
            bookId, largeBook.Language, consumingTasks: new[] { AiTaskType.BookReview }, characterRegister: null);

        _output.WriteLine($"AssembleWindowsAsync partitioned the {largeBook.Chapters.Count}-chapter large gold " +
                          $"book into {windows.Count} window(s) at the production BookReview budget " +
                          $"({(windows.Count > 0 ? windows[0].BudgetTokens : 0)} tokens/window).");
        for (var w = 0; w < windows.Count; w++)
        {
            var primaries = windows[w].IncludedChapterOrders.Except(windows[w].OverlapChapterOrders).OrderBy(o => o).ToList();
            _output.WriteLine(
                $"  window {w}: primaries {(primaries.Count > 0 ? $"[{primaries.First()}..{primaries.Last()}]" : "[]")} " +
                $"(n={primaries.Count}) overlap=[{string.Join(",", windows[w].OverlapChapterOrders)}] " +
                $"est={windows[w].EstimatedTokens}t");
        }

        // (a) MULTIPLE windows — stricter than the live test's > 1. A terse fixture that collapses to one window
        //     fails here, in milliseconds, rather than silently voiding the live coverage claim.
        Assert.True(windows.Count >= 4,
            $"the large gold book must partition into >= 4 windows at the production ~8192-token budget " +
            $"(was {windows.Count}); if this drops the fixture's structured briefs went too terse and the live " +
            "windowed-coverage precondition (WindowCount > 1) is no longer exercised. Re-densify the briefs.");

        // (b) Every chapter is a PRIMARY in EXACTLY ONE window — no drop, no dup across primaries.
        var primaryOrders = windows
            .SelectMany(w => w.IncludedChapterOrders.Except(w.OverlapChapterOrders))
            .ToList();
        var expected = Enumerable.Range(0, largeBook.Chapters.Count).ToList();
        Assert.Equal(expected, primaryOrders.OrderBy(o => o).ToList());          // every order present, once sorted
        Assert.Equal(expected.Count, primaryOrders.Distinct().Count());          // no duplicate primary
        Assert.Equal(expected.Count, primaryOrders.Count);                       // no extra / no drop

        // (c) The two planted chapters land in DIFFERENT primary windows (so the continuity break spans windows).
        int PrimaryWindowOf(int order)
        {
            for (var w = 0; w < windows.Count; w++)
                if (windows[w].IncludedChapterOrders.Except(windows[w].OverlapChapterOrders).Contains(order))
                    return w;
            return -1;
        }
        var deathWindow = PrimaryWindowOf(4);   // ch 5, Rourke killed
        var aliveWindow = PrimaryWindowOf(44);  // ch 45, Rourke impossibly alive
        Assert.True(deathWindow >= 0 && aliveWindow >= 0,
            $"both planted continuity chapters must be primaries (death@4→win {deathWindow}, alive@44→win {aliveWindow}).");
        Assert.NotEqual(deathWindow, aliveWindow);
        _output.WriteLine(
            $"planted continuity chapters separated: order 4 (Rourke death) → window {deathWindow}, " +
            $"order 44 (Rourke alive) → window {aliveWindow}. No live model was invoked (pure assembler path).");
    }

    // ─── DB seeding (mirrors BookReviewServiceTests.SeedReviewableBookAsync, per-chapter briefs from gold) ──

    /// <summary>
    /// Seeds the large book: a <see cref="Book"/>, one <see cref="Chapter"/> per gold chapter, one FRESH
    /// structured <see cref="ChunkSummary"/> per chapter (StructuredJson serialised from the gold chapter's
    /// structured fields — the exact <see cref="StructuredChunkSummaryData"/> shape), and a cached
    /// <see cref="BookSummaryBaseline"/> carrying the gold BookBrief. Briefs are stamped with
    /// <see cref="SummarizationStamp"/> (== the resolved active Summarization model) and StructuredBuiltAt AFTER
    /// the chapter UpdatedAt so ComposeChapterBriefsAsync reads them as FRESH — no summarization model call.
    /// </summary>
    private static async Task<Guid> SeedLargeBookAsync(AppDbContext db, GoldBook book)
    {
        var lang = book.Language;
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = book.Id, Language = lang });

        foreach (var ch in book.Chapters.OrderBy(c => c.Order))
        {
            var chId = Guid.NewGuid();
            db.Chapters.Add(new Chapter
            {
                Id = chId,
                BookId = bookId,
                Order = ch.Order,
                Title = ch.Title,
                ContentText = ch.Summary ?? $"content {ch.Order}"
            });

            // Serialise the gold chapter's structured fields into the StructuredChunkSummaryData shape the
            // composer parses. characterStates carry the planted death/alive states; openThreads carry the
            // planted continuity threads — both the continuity skeleton and the window prompt read them.
            var structured = new
            {
                plotEvents = ch.PlotEvents ?? new List<string>(),
                characterStates = (ch.CharacterStates ?? new List<GoldCharacterState>())
                    .Select(s => new { name = s.Name, state = s.State, emotionalArc = s.EmotionalArc }).ToArray(),
                thematicMarkers = ch.ThematicMarkers ?? new List<string>(),
                toneNotes = ch.ToneNotes,
                openThreads = ch.OpenThreads ?? new List<string>()
            };

            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId,
                ChapterId = chId,
                Language = lang,
                StructuredJson = JsonSerializer.Serialize(structured, StructuredOpts),
                BuiltWithModel = SummarizationStamp,
                StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1) // fresh: after the chapter UpdatedAt
            });
        }

        // A cached BookSummaryBaseline so the assembler has an L2 BookBrief (genre/themes/synopsis for the
        // synthesis + continuity [BOOK_CONTEXT]) and staleness can be computed.
        var brief = book.BookBrief ?? new GoldBookBrief();
        var bookBriefJson = JsonSerializer.Serialize(new
        {
            genre = brief.Genre,
            subGenre = brief.SubGenre,
            targetAudience = brief.TargetAudience,
            literatureLevel = brief.LiteratureLevel,
            themes = brief.Themes ?? new List<string>(),
            synopsis = brief.Synopsis
        }, StructuredOpts);

        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId,
            Language = lang,
            BookBriefJson = bookBriefJson,
            BuiltChapterCount = book.Chapters.Count,
            BuiltWithModel = SummarizationStamp
        });

        await db.SaveChangesAsync();
        return bookId;
    }

    // ─── DI: real Ollama router + full assembler chain + BookReviewService (production budget) ─────────────

    /// <summary>
    /// Builds the DI provider: an in-memory <see cref="AppDbContext"/>, the full assembler chain
    /// (ChapterBriefService → BookSummaryService → BookContextAssembler), <see cref="BookReviewService"/>, its
    /// registries + progress tracker, and the REAL Ollama-backed <see cref="IAiRouter"/> (same shape as
    /// <see cref="BookReviewQualityTests.CreateRouter"/>). Sets the production Ollama_BookReview tuning
    /// (NumCtx=16384 / NumPredict=6144) so the derived book-context budget matches production (~8192 tokens) and
    /// the large book genuinely windows. <see cref="AiOptions.BookContextTokenBudget"/> stays 0 (derive) —
    /// production-faithful windowing, NOT the tiny forced budget the unit tests use.
    /// </summary>
    private static ServiceProvider BuildProvider(string reviewModel)
    {
        const string provider = "Ollama";

        // Mirror the review routing AND the production Ollama tuning into the SAME in-memory IConfiguration the
        // OllamaProvider resolves through (so the live call sends NumCtx=16384) as WELL as into AiOptions (so the
        // assembler's ResolveBudgetTokens derives the ~8192 budget). DefaultModel doubles as the Summarization
        // stamp target (no Summarization feature model → ActiveSummarizationModel == DefaultModel).
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = provider,
                ["Ai:DefaultModel"] = SummarizationStamp,
                ["Ai:Providers:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["Ai:FeatureModels:BookReview:Provider"] = provider,
                ["Ai:FeatureModels:BookReview:Model"] = reviewModel,
                // Production BookReview tuning (mirrors appsettings.json Ollama_BookReview) so the review call
                // gets the full 16k window and the derived book budget is ~8192.
                ["Ai:ProviderSettings:Ollama:NumCtx"] = "8192",
                ["Ai:ProviderSettings:Ollama:NumPredict"] = "2048",
                ["Ai:ProviderSettings:Ollama_BookReview:NumCtx"] = "16384",
                ["Ai:ProviderSettings:Ollama_BookReview:NumPredict"] = "6144"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        // Long timeout: a windowed review over a 48-chapter book issues several window + reduce calls, each of
        // which can CPU-spill on the 8 GB dev GPU. 30 minutes total headroom for the harness ONLY.
        services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromMinutes(30));
        services.AddHttpClient(string.Empty, client => client.Timeout = TimeSpan.FromMinutes(30));

        services.Configure<AiOptions>(opts =>
        {
            opts.DefaultProvider = provider;
            opts.DefaultModel = SummarizationStamp;
            opts.BookReviewSingleCombined = true; // the DEFAULT windowed MAP path (NOT per-dimension)
            opts.BookContextTokenBudget = 0;      // DERIVE from NumCtx (production-faithful ~8192), NOT a forced budget
            opts.FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["BookReview"] = new FeatureModelOptions { Provider = provider, Model = reviewModel }
            };
            opts.ProviderSettings = new Dictionary<string, ProviderTuningOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama"] = new ProviderTuningOptions { NumCtx = 8192, NumPredict = 2048 },
                ["Ollama_BookReview"] = new ProviderTuningOptions { NumCtx = 16384, NumPredict = 6144 }
            };
        });

        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        // The REAL provider set + router (same wiring shape as BookReviewQualityTests.CreateRouter).
        services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
        {
            var c = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama"] = new OllamaProvider(factory, c, opts),
                ["Anthropic"] = new AnthropicProvider(factory, c, opts),
                ["OpenAI"] = new OpenAiProvider(factory, c, opts)
            };
        });
        services.AddSingleton<IAiRouter, AiRouter>();

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<BookReviewService>();
        services.AddScoped<DynamicTermRepairService>();
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<BookReviewBuildRegistry>();

        return services.BuildServiceProvider();
    }

    // ─── Gold loading + matching helpers ─────────────────────────────────────────────────────────────

    private static GoldBook? LoadLargeBook(string id)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "book-review-gold-large.json");
        if (!File.Exists(path)) return null;
        var raw = JsonSerializer.Deserialize<GoldBook[]>(File.ReadAllText(path), GoldOpts);
        return raw?.FirstOrDefault(b =>
            b.Chapters is { Count: > 0 } && string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveModel()
    {
        var raw = Environment.GetEnvironmentVariable(ModelEnvVar);
        return string.IsNullOrWhiteSpace(raw) ? BookReviewQualityTests.DefaultBookReviewModel : raw.Trim();
    }

    private static string Norm(string? dimension) => (dimension ?? string.Empty).Trim().ToLowerInvariant();

    private static HashSet<string> AllowedDimensions(GoldPlantedDefect d)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(d.Dimension)) set.Add(Norm(d.Dimension));
        if (d.AcceptDimensions != null)
            foreach (var a in d.AcceptDimensions)
                if (!string.IsNullOrWhiteSpace(a)) set.Add(Norm(a));
        return set;
    }

    /// <summary>True when the persisted finding anchors a chapter within <paramref name="tolerance"/> of the
    /// expected order, reading BOTH chapterAnchors[].order and evidence[].chapterOrder from the persisted JSON
    /// (the same two anchor sources the sibling harness's AnchorsChapter reads).</summary>
    private static bool AnchorsNear(BookFinding f, int expectedOrder, int tolerance)
    {
        foreach (var o in AnchorOrderSet(f))
            if (Math.Abs(o - expectedOrder) <= tolerance) return true;
        return false;
    }

    private static IEnumerable<int> AnchorOrderSet(BookFinding f)
    {
        var orders = new List<int>();
        try
        {
            var anchors = JsonSerializer.Deserialize<List<FindingChapterAnchor>>(
                string.IsNullOrWhiteSpace(f.ChapterAnchorsJson) ? "[]" : f.ChapterAnchorsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (anchors != null) orders.AddRange(anchors.Select(a => a.Order));
        }
        catch { /* tolerate malformed anchor JSON */ }
        try
        {
            var evidence = JsonSerializer.Deserialize<List<FindingEvidence>>(
                string.IsNullOrWhiteSpace(f.EvidenceJson) ? "[]" : f.EvidenceJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (evidence != null) orders.AddRange(evidence.Select(e => e.ChapterOrder));
        }
        catch { /* tolerate malformed evidence JSON */ }
        return orders;
    }

    private static string AnchorOrders(BookFinding f) =>
        "[" + string.Join(",", AnchorOrderSet(f).Distinct().OrderBy(o => o)) + "]";

    private static string Truncate(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    // ─── Ollama reachability probe (skip-gate) — identical to BookReviewQualityTests ──────────────────

    private static async Task<bool> IsOllamaReachableAsync()
    {
        if (await ProbeAsync(OllamaBaseUrl)) return true;
        if (!OllamaBaseUrl.Contains("127.0.0.1") &&
            await ProbeAsync(OllamaBaseUrl.Replace("localhost", "127.0.0.1")))
            return true;
        return false;
    }

    private static async Task<bool> ProbeAsync(string baseUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resp = await client.GetAsync($"{baseUrl}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ─── Gold shape (a small local copy — the large fixture carries structured chapter fields the sibling
    //     harness's GoldBook does not need, so this class parses its own shape). ─────────────────────────

    private sealed class GoldBook
    {
        public string Id { get; set; } = "";
        public string Language { get; set; } = "en";
        public bool ExpectClean { get; set; }
        public GoldBookBrief? BookBrief { get; set; }
        public List<GoldChapter> Chapters { get; set; } = new();
        public List<GoldPlantedDefect> PlantedDefects { get; set; } = new();
    }

    private sealed class GoldBookBrief
    {
        public string? Genre { get; set; }
        public string? SubGenre { get; set; }
        public string? TargetAudience { get; set; }
        public int? LiteratureLevel { get; set; }
        public List<string>? Themes { get; set; }
        public string? Synopsis { get; set; }
    }

    private sealed class GoldChapter
    {
        public int Order { get; set; }
        public string Title { get; set; } = "";
        public string? Summary { get; set; }
        public List<string>? PlotEvents { get; set; }
        public List<GoldCharacterState>? CharacterStates { get; set; }
        public List<string>? ThematicMarkers { get; set; }
        public string? ToneNotes { get; set; }
        public List<string>? OpenThreads { get; set; }
    }

    private sealed class GoldCharacterState
    {
        public string Name { get; set; } = "";
        public string? State { get; set; }
        public string? EmotionalArc { get; set; }
    }

    private sealed class GoldPlantedDefect
    {
        public string Dimension { get; set; } = "";
        public string[]? AcceptDimensions { get; set; }
        public string Verdict { get; set; } = "";
        public string[]? AcceptVerdicts { get; set; }
        public int ChapterOrder { get; set; }
        public int[]? SpansChapterOrders { get; set; }
        public string? Note { get; set; }
    }
}
