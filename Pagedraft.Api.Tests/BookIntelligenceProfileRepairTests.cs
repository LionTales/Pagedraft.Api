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

    private const string SynopsisText = "תקציר קצר של הספר בגוף שלישי.";

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

    private static ServiceProvider BuildProvider(Mock<IAiRouter> router, AnalysisRepairOptions? repair)
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
}
