using System;
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
/// Real-path coverage for the f5-wire analysis-output repair on the BOOK PROFILE path
/// (<see cref="BookIntelligenceService.BuildBookProfileAsync"/>).
///
/// BuildBookProfileAsync is the ONLY producer of CharacterAnalysis / StoryAnalysis, and it generates them via
/// <see cref="UnifiedAnalysisService.RunRawAsync"/> with <c>structuredJson: null</c>. For those types the shipped
/// repair seam (<c>ApplyAnalysisRepairAsync</c> -> <see cref="GlossaryRepairPass.Apply"/>) is a STRICT no-op, so
/// the deterministic glossary that Hebraises leaked English never reached the persisted <c>CharactersJson</c> /
/// <c>StoryStructureJson</c> — the 92 unit-level repair tests passed only because they call the glossary directly
/// with a synthetic non-null structuredJson. BuildBookProfileAsync now applies the SAME glossary as an ENGINE
/// HOOK (mirroring BookReviewService.ApplyGlossaryToFindings) before persist. These tests drive the GENUINE build
/// end to end (real UnifiedAnalysisService + BookContextAssembler + in-memory DB, fake IAiRouter) and assert on
/// the STORED profile JSON.
/// </summary>
public class BookIntelligenceProfileRepairTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // A leak-containing, MARKDOWN-FENCED CharacterAnalysis payload: the leak "(Action)" lives in the repairable
    // `description` prose; `name` (proper noun) and `role` (enum) must survive byte-identical.
    private const string CharacterAnalysisLeakJson = """
        ```json
        {
          "characters": [
            { "name": "דנה", "role": "protagonist", "description": "גיבורה שמניעה את ה-(Action) המרכזי של הסיפור", "arc": "מפחד אל עבר תקווה", "firstAppearanceChapter": 1 }
          ],
          "relationships": [],
          "summary": "סיכום מערך הדמויות."
        }
        ```
        """;

    // A leak-containing, MARKDOWN-FENCED StoryAnalysis payload: the leak "(Action)" lives in the repairable
    // `plotStructure.risingAction` prose; the conflict `type`/`status` enums must survive byte-identical.
    private const string StoryAnalysisLeakJson = """
        ```json
        {
          "plotStructure": {
            "setup": "הצגת המצב ההתחלתי",
            "risingAction": "העלייה בעימות עם (Action) מהיר וטעון",
            "climax": "שיא העלילה",
            "fallingAction": "אירועים לאחר השיא",
            "resolution": "הסיום"
          },
          "pacing": "קצב מהיר",
          "conflicts": [ { "type": "external", "description": "עימות חיצוני מרכזי", "status": "ongoing" } ],
          "summary": "סיכום מבנה הסיפור."
        }
        ```
        """;

    private const string BookOverviewJson = """
        { "genre": "פנטזיה", "subGenre": "אפי", "targetAudience": "מבוגרים", "literatureLevel": 3, "estimatedReadingTimeMinutes": 120, "languageRegister": "רשמי", "summary": "סקירה כללית." }
        """;

    /// <summary>
    /// The Synopsis the router returns on the profile build. Deliberately SYNOPSIS-SHAPED (multi-paragraph,
    /// third person, per PromptFactory.SynopsisHe) and deliberately REPAIRABLE: it carries "(Action)", a
    /// closed-glossary term (LiteraryTermGlossary.Terms["action"] = "פעולה") that the glossary stage rewrites
    /// deterministically with zero model calls, plus "Daniel" at a paragraph head — q1's false-positive shape,
    /// a Title-Case Latin proper noun that is sentence-initial and therefore NOT spared by the classifier's
    /// mid-sentence rule. Both are there so
    /// <see cref="BuildBookProfileAsync_UnderTheShippedConfig_LeavesSynopsisByteIdentical"/> is a real
    /// assertion: if a Synopsis dispatch arm ever existed, this value could not come back unchanged.
    /// Chosen so UnifiedAnalysisService.SanitizeResponse is the IDENTITY on it, which is what makes
    /// byte-identity against the model's own string mean "the repair layer did not touch it": single spaces,
    /// no leading/trailing whitespace, and SINGLE newlines between paragraphs — SanitizeResponse ->
    /// SyncfusionWatermarkStripper collapses every run of [\r\n]+ to one "\n", so a blank line here would be
    /// eaten by sanitization and the assertion would fail for a reason that has nothing to do with repair.
    /// </summary>
    private const string SynopsisText =
        "תקציר קצר של הספר בגוף שלישי, ובו (Action) מרכזית שמניעה את העלילה קדימה.\n" +
        "Daniel הוא הדמות שסביבה נבנה הסיפור, והמסע שלו נמשך עד הפרק האחרון.";

    // A QA {answer, citations, confidence} envelope (the shape QAHe requests). The `answer` prose is what the
    // ask card must render; `confidence` is an English enum that must survive. "confidence" is the distinctive
    // token the router keys QA on (no profile prompt contains it).
    private const string QaAnswerJson = """
        {
          "answer": "הדמות המרכזית עוברת מסע של גדילה לאורך הפרק.",
          "citations": [ { "chapterNumber": 1, "chapterTitle": "פרק 1", "relevantExcerpt": "ציטוט רלוונטי" } ],
          "confidence": "high"
        }
        """;

    /// <summary>
    /// Router keyed on the analysis prompt (the ONLY thing that distinguishes BookOverview / CharacterAnalysis /
    /// StoryAnalysis here — they all map to the same AiTaskType). The Hebrew prompts carry distinctive JSON keys:
    /// StoryAnalysis has "plotStructure", CharacterAnalysis has "characters", BookOverview has "genre"; anything
    /// else is the plain-prose Synopsis prompt.
    /// </summary>
    private static Mock<IAiRouter> BuildRouter()
    {
        var mock = new Mock<IAiRouter>();
        mock.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                var instr = req.Instruction ?? string.Empty;
                string content =
                    instr.Contains("plotStructure") ? StoryAnalysisLeakJson :
                    instr.Contains("\"characters\"") ? CharacterAnalysisLeakJson :
                    instr.Contains("\"genre\"") ? BookOverviewJson :
                    instr.Contains("confidence") ? QaAnswerJson :
                    SynopsisText;
                return new AiResponse { Content = content, Model = "gemma4:12b", Provider = "test" };
            });
        return mock;
    }

    private static ServiceProvider BuildProvider(
        Mock<IAiRouter> router,
        AnalysisRepairOptions? repair,
        IBookEntityProvider? entityProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services.AddSingleton(router.Object);
        services.Configure<AiOptions>(o =>
        {
            // Large budget so the whole flat-summary context fits (no chapters dropped).
            o.BookContextTokenBudget = 1_000_000;
            o.AnalysisRepair = repair;
        });
        services.Configure<Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions>(_ => { });

        // Shared infra + the full flat/structured analysis graph (mirrors Program.cs + ChapterSummaryEditTests).
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
        if (entityProvider is null)
        {
            services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
        }
        else
        {
            services.AddSingleton(entityProvider); // be-c02: spy on WHICH bookId the raw seam asks for
        }
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();

        return services.BuildServiceProvider();
    }

    /// <summary>Seeds a Hebrew book with two chapters, each carrying a flat ChunkSummary so the assembler produces
    /// a non-empty context (flat-fallback path) and BuildBookProfileAsync has summaries to run against.</summary>
    private static async Task<Guid> SeedHebrewBookAsync(AppDbContext db)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "ספר בדיקה", Language = "he" });
        for (var i = 0; i < 2; i++)
        {
            var chId = Guid.NewGuid();
            db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = i, Title = $"פרק {i}", ContentText = $"תוכן פרק {i}." });
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chId, Language = "he",
                SummaryText = $"סיכום פרק {i}: הגיבורה יוצאת למסע ומתמודדת עם עימות מרכזי.",
                StructuredJson = null
            });
        }
        await db.SaveChangesAsync();
        return bookId;
    }

    /// <summary>The core coverage: with the shipped repair layer ({ Enabled:true, GuardOnly:true }) on a Hebrew
    /// book, the STORED CharactersJson + StoryStructureJson have the English leak Hebraised, the model's markdown
    /// fence stripped (clean JSON), and every proper noun / enum / structural field left intact.</summary>
    [Fact]
    public async Task BuildBookProfileAsync_HebrewBook_RepairsLeakInStoredCharacterAndStoryJson()
    {
        using var provider = BuildProvider(
            BuildRouter(), new AnalysisRepairOptions { Enabled = true, GuardOnly = true });
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var svc = provider.GetRequiredService<BookIntelligenceService>();
        var profile = await svc.BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        // ── CharacterAnalysis: leak Hebraised, fence stripped, prose repaired, structure preserved ──
        // NOTE: the app's JsonOpts (no custom encoder, matching UnifiedAnalysisService's re-serialize) escapes
        // non-ASCII as \uXXXX, so the Hebraisation is asserted on the DESERIALIZED field (which decodes the
        // escapes) rather than as a literal substring of the raw JSON — JSON.parse on the FE decodes it identically.
        var charactersJson = profile.CharactersJson!;
        Assert.DoesNotContain("```", charactersJson);        // reserialized to CLEAN JSON (FE JSON.parse-safe)
        Assert.DoesNotContain("(Action)", charactersJson);   // the leak is gone

        var chars = JsonSerializer.Deserialize<CharacterAnalysisResult>(charactersJson, JsonOpts)!;
        var hero = Assert.Single(chars.Characters);
        Assert.Equal("דנה", hero.Name);                      // proper noun untouched
        Assert.Equal("protagonist", hero.Role);              // enum untouched
        Assert.Contains("(פעולה)", hero.Description);        // the repaired prose field
        Assert.DoesNotContain("Action", hero.Description);

        // ── StoryAnalysis: leak Hebraised in plotStructure prose, enums + structure preserved ──
        var storyJson = profile.StoryStructureJson!;
        Assert.DoesNotContain("```", storyJson);
        Assert.DoesNotContain("(Action)", storyJson);

        var story = JsonSerializer.Deserialize<StoryAnalysisResult>(storyJson, JsonOpts)!;
        Assert.Contains("(פעולה)", story.PlotStructure.RisingAction);
        Assert.DoesNotContain("Action", story.PlotStructure.RisingAction);
        var conflict = Assert.Single(story.Conflicts);
        Assert.Equal("external", conflict.Type);             // enum untouched
        Assert.Equal("ongoing", conflict.Status);            // enum untouched
    }

    /// <summary>Guard: the hook is GATED. With the repair layer disabled the profile stores the raw model output
    /// verbatim (fence + English leak intact) — proving the fix is additive and the gate is honoured (no
    /// over-application), and that the failure-safe "store raw" fallback keeps the pre-fix behaviour.</summary>
    [Fact]
    public async Task BuildBookProfileAsync_RepairDisabled_StoresRawUnrepairedJson()
    {
        using var provider = BuildProvider(
            BuildRouter(), new AnalysisRepairOptions { Enabled = false });
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var svc = provider.GetRequiredService<BookIntelligenceService>();
        var profile = await svc.BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        // Layer off → raw model output stored unchanged (fence + leak present, no Hebraisation).
        Assert.Contains("```json", profile.CharactersJson);
        Assert.Contains("(Action)", profile.CharactersJson);
        Assert.DoesNotContain("(פעולה)", profile.CharactersJson);
        Assert.Contains("(Action)", profile.StoryStructureJson);
    }

    /// <summary>
    /// Book-scoped QA (`/ask`) must persist with a NULL ChapterId, not Guid.Empty. The old
    /// `ChapterId = chapterId ?? Guid.Empty` wrote Guid.Empty for a book-scoped result, and on SQL Server that
    /// violated FK_AnalysisResults_Chapters_ChapterId (no chapter with that id) — a 500 on every ask. The
    /// in-memory provider does not enforce FKs, so this asserts the CODE now writes NULL (revert-verify: restore
    /// `?? Guid.Empty` and ChapterId becomes Guid.Empty, failing the Assert.Null below).
    /// </summary>
    [Fact]
    public async Task AskAsync_BookScopedQA_PersistsWithNullChapterId()
    {
        using var provider = BuildProvider(
            BuildRouter(), new AnalysisRepairOptions { Enabled = true, GuardOnly = true });
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var svc = provider.GetRequiredService<BookIntelligenceService>();
        var result = await svc.AskAsync(bookId, "מיהי הדמות המרכזית בספר?", "he", CancellationToken.None);

        Assert.Null(result.ChapterId);                 // book-scoped => no owning chapter (was Guid.Empty => FK 500)
        Assert.Equal(AnalysisScope.Book, result.Scope);
        Assert.Equal(bookId, result.BookId);
        Assert.Equal(AnalysisType.QA, result.AnalysisType);

        // ResultText surfaces the parsed `answer` prose (what the ask card renders), NOT the raw JSON envelope;
        // StructuredResult keeps the full {answer, citations, confidence} shape for the citations line.
        Assert.Equal("הדמות המרכזית עוברת מסע של גדילה לאורך הפרק.", result.ResultText);
        Assert.DoesNotContain("\"citations\"", result.ResultText);   // envelope not leaked into the display text
        Assert.Contains("\"confidence\"", result.StructuredResult!);  // ...but preserved in the structured result

        // It actually persisted, with a null ChapterId (the row SQL Server previously rejected).
        var persisted = await db.AnalysisResults.FirstAsync(a => a.Id == result.Id);
        Assert.Null(persisted.ChapterId);
        Assert.Equal(AnalysisScope.Book, persisted.Scope);
    }

    // ── f2: SYNOPSIS IS NOW REPAIRED, pinned at its REAL producer path ───────────────────────────────────
    //
    // e1's enable-set came out EMPTY (Custom excluded by d1, Synopsis HALTED by q1) and this test used to pin
    // BookProfile.Synopsis as BYTE-IDENTICAL to the model's output. f2 (2026-07-28) enabled Synopsis after q2
    // re-measured it at 100% (6/6) preservation / 0 FP / 0 over-rewrite on q1's own fixtures, so the pin is
    // INVERTED rather than deleted: the same seam is asserted to REPAIR now, and it additionally pins the
    // property that made the enable safe (a paragraph-head proper noun is left alone, deterministically).
    //
    // The seam that changed is the real one: BuildBookProfileAsync runs
    // RunRawAsync(concatenated, AnalysisType.Synopsis, ...) and assigns the result to profile.Synopsis, and
    // RunRawAsync's second half (CompleteDeferredRepairAsync) calls ApplyAnalysisRepairAsync with
    // structuredJson: null - which is exactly the shape the new PLAIN-TEXT dispatch arm handles (Synopsis has
    // no structured payload, so the whole prose value is the repairable surface, mirroring Summarization).
    // That is why enabling Synopsis covers the PROFILE path as well as the direct POST /analyze path, unlike
    // BookOverview, whose only repairable field is structured AND is discarded before persistence (f1).
    // See AnalysisRepairExclusionRegressionTests for the allowlist-level and dispatch-level halves.

    /// <summary>
    /// Under the SHIPPED <c>Ai:AnalysisRepair</c> block (loaded from appsettings.json, not hand-authored) the
    /// string persisted to <c>BookProfile.Synopsis</c> now has its English leak Hebraised — Synopsis is a
    /// repaired type as of f2.
    ///
    /// TWO assertions, and the second is the load-bearing one:
    /// (a) the closed-glossary leak <c>(Action)</c> IS rewritten to <c>(פעולה)</c>, proving the plain-text
    ///     dispatch arm is reached on the PROFILE path (not just at the RunAsync seam);
    /// (b) <c>Daniel</c>, a Title-Case Latin proper noun at a PARAGRAPH HEAD, survives BYTE-IDENTICAL. That is
    ///     q1's measured false-positive shape, and ForeignRunClassifier rule (7b) (c3) is what spares it
    ///     deterministically — 0 model calls. q2 measured that property at 100% (6/6) preservation with 0 of 6
    ///     values reaching the model, which is what cleared q1's HALT. READ IT AS A GATE PROPERTY: the model is
    ///     not better at proper nouns, it is simply no longer asked.
    ///
    /// Non-vacuity: the CONTROL in the same build (persisted CharactersJson, also Hebraised) proves the layer
    /// was live, and assertion (a) proves Synopsis specifically was not skipped — so (b) cannot pass because
    /// "the layer was off".
    /// </summary>
    [Fact]
    public async Task BuildBookProfileAsync_UnderTheShippedConfig_RepairsSynopsis()
    {
        // Shipped config = Mode:GlossaryThenDynamic, so the dynamic stage is live; the stub entity provider
        // keeps it deterministic (empty LEAVE set = the harshest case, nothing spared by the entity lever, so
        // the paragraph-head assertion below can ONLY be satisfied by the classifier rule).
        var router = BuildRouter();
        using var provider = BuildProvider(
            router, ShippedAnalysisRepairConfig.Load(), new StubBookEntityProvider());
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var profile = await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        // CONTROL first: the repair layer really was live in THIS build (a repaired type on the same path).
        Assert.DoesNotContain("(Action)", profile.CharactersJson);
        Assert.Contains("(פעולה)", JsonSerializer
            .Deserialize<CharacterAnalysisResult>(profile.CharactersJson!, JsonOpts)!.Characters[0].Description);

        // (a) Synopsis IS repaired on the profile path.
        Assert.True(profile.Synopsis is not null && profile.Synopsis.Contains("(פעולה)"),
            "BookProfile.Synopsis still carries the raw English leak, i.e. the repair layer no longer touches " +
            "Synopsis on the profile path.\n" +
            $"  expected to contain: (פעולה)\n  actual:   {profile.Synopsis}\n" +
            "f2 ENABLED Synopsis on 2026-07-28 (PerType key true in both appsettings files + a plain-text " +
            "dispatch arm in GlossaryRepairPass.Apply and DynamicTermRepairService.ApplyAsync) after q2 " +
            "cleared the shipped bar: preservation 100% (6/6), 0 false positives, over-rewrite 0, cleaning " +
            "100% (3/3). If Synopsis is being rolled back, flip the key in BOTH files and update this test " +
            "plus AnalysisRepairConfigParityTests.DecisionFor. See `## q2 quality-gate results` / `## f2 outcome`.");
        Assert.DoesNotContain("(Action)", profile.Synopsis!);

        // (b) ...and the PARAGRAPH-HEAD proper noun is spared deterministically - the property that made (a)
        //     safe to ship. With an empty entity set the ONLY thing that can spare it is classifier rule (7b).
        Assert.Contains("Daniel", profile.Synopsis!);
        Assert.DoesNotContain("דניאל", profile.Synopsis!);

        // ...and it never even reached the model. NOTHING in this build makes a TermRepair call: every planted
        // leak is in-glossary, so the deterministic stage clears them all and the only foreign run the dynamic
        // stage could have been handed is `Daniel`, which rule (7b) LEAVES. A single TermRepair call here means
        // the paragraph-head run was sent to the repair model - q1's exact false-positive path.
        // (The NON-VACUITY control for rule (7b) - a value-INITIAL Latin name on the same dispatch arm that DOES
        // reach the model - lives in AnalysisRepairExclusionRegressionTests
        // .ShippedSynopsis_ParagraphHeadProperNoun_NeverReachesTheRepairModel, and the pure-classifier verdicts
        // are pinned in ForeignRunClassifierTests.)
        router.Verify(
            r => r.CompleteAsync(It.Is<AiRequest>(q => q.TaskType == AiTaskType.TermRepair), It.IsAny<CancellationToken>()),
            Times.Never,
            "A TermRepair model call was made during this profile build. The only foreign run in the fixture is " +
            "the paragraph-head proper noun `Daniel`, so this means ForeignRunClassifier rule (7b) stopped " +
            "sparing it - which is exactly the false positive q1 measured (Chekhov -> צ'כוב) and c3/q2 closed.");
    }

    // ── be-c02: the RunRawAsync bookId seam, asserted at the CALL SITES ──────────────────────────────────
    // BookIntelligenceService is the only caller of UnifiedAnalysisService.RunRawAsync, and it HAS the bookId in
    // scope at every one of those calls. RunRawAsync used to hard-code Guid.Empty into the repair layer, so the
    // per-book proper-noun LEAVE set was always empty on this seam. These tests pin that both call sites now pass
    // the REAL id: the spy provider records every bookId the repair layer asks it for, and Guid.Empty (the
    // signature of a dropped id) must never appear.

    /// <summary>The dynamic stage is what consults IBookEntityProvider, so the seam is only observable under a
    /// Mode that runs it (the shipped default). Mode=Glossary/Off never asks the provider at all.</summary>
    private static AnalysisRepairOptions DynamicRepairOptions() => new()
    {
        Enabled = true,
        GuardOnly = true,
        Mode = AnalysisRepairMode.GlossaryThenDynamic
    };

    /// <summary>Seeds a Hebrew book with one chapter and NO ChunkSummary, so SummarizeChaptersAsync actually runs
    /// the chapter (the freshness / user-edited guards both skip a chapter that already has one).</summary>
    private static async Task<Guid> SeedUnsummarizedHebrewBookAsync(AppDbContext db)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "ספר בדיקה", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "פרק 1",
            ContentText = "הגיבורה יוצאת למסע ומתמודדת עם עימות מרכזי."
        });
        await db.SaveChangesAsync();
        return bookId;
    }

    /// <summary>SummarizeChaptersAsync — the seam whose output is PERSISTED to ChunkSummary.SummaryText — passes
    /// the real bookId, so the summary's character names are matched against THIS book's entity set.</summary>
    [Fact]
    public async Task SummarizeChaptersAsync_PassesRealBookId_ToBookEntityProvider()
    {
        var entities = new StubBookEntityProvider();
        using var provider = BuildProvider(BuildRouter(), DynamicRepairOptions(), entities);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedUnsummarizedHebrewBookAsync(db);

        await provider.GetRequiredService<BookIntelligenceService>()
            .SummarizeChaptersAsync(bookId, "he", CancellationToken.None);

        Assert.NotEmpty(entities.RequestedBookIds);                 // the repair layer reached the provider
        Assert.All(entities.RequestedBookIds, id => Assert.Equal(bookId, id));
        Assert.DoesNotContain(Guid.Empty, entities.RequestedBookIds); // ...never with the dropped-id sentinel
    }

    /// <summary>BuildBookProfileAsync makes four RunRawAsync calls (BookOverview / Synopsis / CharacterAnalysis /
    /// StoryAnalysis); every one of them must carry the real bookId.</summary>
    [Fact]
    public async Task BuildBookProfileAsync_PassesRealBookId_ToBookEntityProvider()
    {
        var entities = new StubBookEntityProvider();
        using var provider = BuildProvider(BuildRouter(), DynamicRepairOptions(), entities);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        Assert.NotEmpty(entities.RequestedBookIds);
        Assert.All(entities.RequestedBookIds, id => Assert.Equal(bookId, id));
        Assert.DoesNotContain(Guid.Empty, entities.RequestedBookIds);
    }

    // ── be-c03: the profile build is a PRODUCER of a harvest source, so it must refresh the entity cache ──

    /// <summary>
    /// BuildBookProfileAsync persists BookProfile.CharactersJson — a serialized CharacterAnalysisResult, and one
    /// of the two sources BookEntityProvider harvests its per-book proper-noun LEAVE set from. The provider caches
    /// that set per book, so without an invalidation here the ordinary sequence (analyse a chapter on a fresh book
    /// -> the set is built with no character names in it -> the profile build produces them -> nothing refreshes)
    /// means this book's character names NEVER reach the classifier, and the repair model rewrites them.
    /// Asserted at the CALL SITE with the spy: the real bookId is invalidated after the profile is persisted.
    /// </summary>
    [Fact]
    public async Task BuildBookProfileAsync_InvalidatesTheBookEntityCache()
    {
        var entities = new StubBookEntityProvider();
        using var provider = BuildProvider(BuildRouter(), DynamicRepairOptions(), entities);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        Assert.Empty(entities.InvalidatedBookIds);

        await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        Assert.Contains(bookId, entities.InvalidatedBookIds);
        Assert.DoesNotContain(Guid.Empty, entities.InvalidatedBookIds);
    }
}
