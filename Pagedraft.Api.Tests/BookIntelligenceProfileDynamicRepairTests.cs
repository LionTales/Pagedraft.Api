using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// c2 coverage: the BOOK PROFILE engine hook (<c>BookIntelligenceService.RepairStructuredProfileJsonAsync</c>,
/// called from <see cref="BookIntelligenceService.BuildBookProfileAsync"/>) is now TWO-stage — the deterministic
/// glossary AND the span-scoped dynamic stage — mirroring <c>BookReviewService</c>'s hook (glossary 5b /
/// dynamic 5c), each selected by the SHARED <c>RunsGlossary()</c> / <c>RunsDynamic()</c> predicates under the
/// same Enabled/PerType layer gate.
///
/// WHY THIS MATTERS (the defect c2 closed): the hook used to call <see cref="GlossaryRepairPass.RepairFields"/>
/// and nothing else, so the persisted <c>CharactersJson</c> / <c>StoryStructureJson</c> got the CLOSED 23-term
/// <see cref="LiteraryTermGlossary.Terms"/> map and 0% of the dynamic cleaning — on the d5 out-of-glossary
/// corpus that is 0/10 versus the dynamic stage's 10/10. Every fixture below therefore carries BOTH kinds of
/// leak in the SAME prose value:
///   • "(Action)" — IN the glossary (Terms["action"] = "פעולה"), cleaned deterministically with zero model calls;
///   • "subtext" / "catharsis" — NOT in the glossary, so ONLY the dynamic stage can clean them.
/// The pair is what makes each Mode assertion non-vacuous: it distinguishes "the layer was off" from "the
/// layer ran and this stage was not selected".
///
/// DETERMINISTIC, NO GPU: the router is a fake. Profile prompts are keyed on their distinctive JSON keys (the
/// idiom in <see cref="BookIntelligenceProfileRepairTests"/>); TermRepair calls are keyed on
/// <see cref="AiTaskType.TermRepair"/> and answered from a fixed token -> replacement map, so the call COUNT is
/// an exact, model-free measure of whether the dynamic stage ran. q2 measures the real model separately.
/// </summary>
public class BookIntelligenceProfileDynamicRepairTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Fixtures: one in-glossary leak + one OUT-of-glossary leak per type, in the SAME repairable value ──

    /// <summary>"(Action)" is in the glossary; "subtext" is not. Both sit in `characters[0].description`, a
    /// whitelisted prose field (RepairableFields.For(CharacterAnalysisResult)). `name` / `role` are
    /// must-not-touch.</summary>
    private const string CharacterAnalysisLeakJson = """
        ```json
        {
          "characters": [
            { "name": "דנה", "role": "protagonist", "description": "גיבורה שמניעה את ה-(Action) המרכזי, ויש בה subtext שנחשף לאט", "arc": "מפחד אל עבר תקווה", "firstAppearanceChapter": 1 }
          ],
          "relationships": [],
          "summary": "סיכום מערך הדמויות."
        }
        ```
        """;

    /// <summary>"(Action)" is in the glossary; "catharsis" is not. Both sit in
    /// `plotStructure.risingAction`.</summary>
    private const string StoryAnalysisLeakJson = """
        ```json
        {
          "plotStructure": {
            "setup": "הצגת המצב ההתחלתי",
            "risingAction": "העלייה בעימות עם (Action) מהיר, עד לרגע של catharsis",
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

    private const string SynopsisText = "תקציר קצר של הספר בגוף שלישי, ובו עלילה מרכזית שמניעה את הסיפור קדימה.";

    /// <summary>What the fake TermRepair model answers for each marked run. An unmapped token is ECHOED, which
    /// <c>IsAcceptableReplacement</c> rejects (still foreign script) so the original span survives — the same
    /// fail-safe the real pass relies on.</summary>
    private static readonly Dictionary<string, string> ReplacementMap = new(StringComparer.Ordinal)
    {
        ["subtext"] = "תת-טקסט",
        ["catharsis"] = "קתרזיס",
    };

    // ── Fake router: counts TermRepair calls (the model-free "did the dynamic stage run?" probe) ──────────

    private sealed class ProfileRouter : IAiRouter
    {
        private readonly object _lock = new();
        private readonly bool _throwOnTermRepair;

        public ProfileRouter(bool throwOnTermRepair = false) => _throwOnTermRepair = throwOnTermRepair;

        /// <summary>Every marked run the dynamic stage sent to the model, in call order. Count == 0 proves the
        /// dynamic stage never ran; the RunRawAsync seam cannot contribute here (it calls ApplyAsync with
        /// structuredJson: null for these types, which returns before any model call).</summary>
        public List<string> TermRepairTokens { get; } = new();

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            if (request.TaskType == AiTaskType.TermRepair)
            {
                var token = MarkedToken(request.InputText);
                lock (_lock) { TermRepairTokens.Add(token); }
                if (_throwOnTermRepair)
                    throw new InvalidOperationException("simulated TermRepair router failure");

                var replacement = ReplacementMap.TryGetValue(token, out var r) ? r : token;
                return Task.FromResult(new AiResponse
                {
                    Content = $"{{\"replacement\":\"{replacement}\"}}",
                    Model = "gemma4:12b",
                    Provider = "test"
                });
            }

            var instr = request.Instruction ?? string.Empty;
            string content =
                instr.Contains("plotStructure") ? StoryAnalysisLeakJson :
                instr.Contains("\"characters\"") ? CharacterAnalysisLeakJson :
                instr.Contains("\"genre\"") ? BookOverviewJson :
                SynopsisText;
            return Task.FromResult(new AiResponse { Content = content, Model = "gemma4:12b", Provider = "test" });
        }

        public IAsyncEnumerable<string> StreamCompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The profile build never streams.");

        private static string MarkedToken(string? marked)
        {
            if (string.IsNullOrEmpty(marked)) return string.Empty;
            var open = marked.IndexOf('«');
            var close = marked.IndexOf('»');
            return open >= 0 && close > open ? marked.Substring(open + 1, close - open - 1) : string.Empty;
        }
    }

    /// <summary><see cref="IBookEntityProvider"/> that THROWS on the fetch — the one genuinely throwing step
    /// inside the dynamic block, used to prove the outer fail-safe still stores the RAW string.</summary>
    private sealed class ThrowingBookEntityProvider : IBookEntityProvider
    {
        public Task<IReadOnlySet<string>> GetEntitiesAsync(Guid bookId, string? language, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated entity-provider failure");
        public void Invalidate(Guid bookId) { }
    }

    // ── Log capture (the fault must be SURFACED, not swallowed) ───────────────────────────────────────────

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly object _lock = new();
        public List<(string Category, LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Cap(this, categoryName);
        public void Dispose() { }

        private sealed class Cap : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            private readonly string _category;
            public Cap(CapturingLoggerProvider owner, string category) { _owner = owner; _category = category; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                lock (_owner._lock)
                {
                    _owner.Entries.Add((_category, level, formatter(state, ex), ex));
                }
            }
        }
    }

    // ── DI graph (mirrors BookIntelligenceProfileRepairTests.BuildProvider) ───────────────────────────────

    private static ServiceProvider BuildProvider(
        IAiRouter router,
        AnalysisRepairOptions? repair,
        CapturingLoggerProvider logs,
        IBookEntityProvider? entityProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services.AddSingleton(router);
        services.Configure<AiOptions>(o =>
        {
            o.BookContextTokenBudget = 1_000_000;
            o.AnalysisRepair = repair;
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
        services.AddSingleton(entityProvider ?? new StubBookEntityProvider());
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();

        return services.BuildServiceProvider();
    }

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
                BookId = bookId,
                ChapterId = chId,
                Language = "he",
                SummaryText = $"סיכום פרק {i}: הגיבורה יוצאת למסע ומתמודדת עם עימות מרכזי.",
                StructuredJson = null
            });
        }
        await db.SaveChangesAsync();
        return bookId;
    }

    private static AnalysisRepairOptions Repair(AnalysisRepairMode mode, bool enabled = true) =>
        new() { Enabled = enabled, GuardOnly = true, Mode = mode };

    private static string DescriptionOf(string charactersJson) =>
        JsonSerializer.Deserialize<CharacterAnalysisResult>(charactersJson, JsonOpts)!.Characters[0].Description;

    private static string RisingActionOf(string storyJson) =>
        JsonSerializer.Deserialize<StoryAnalysisResult>(storyJson, JsonOpts)!.PlotStructure.RisingAction;

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CORE c2 ASSERTION. Under the shipped <c>Mode=GlossaryThenDynamic</c> the persisted profile JSON now
    /// receives BOTH stages: the in-glossary "(Action)" is Hebraised deterministically AND the out-of-glossary
    /// "subtext" / "catharsis" — which the closed 23-term map cannot touch by construction — are cleaned by the
    /// span-scoped dynamic stage. Before c2 the dynamic half never ran on this path, so the out-of-glossary
    /// leaks survived into the column the FE reads.
    /// </summary>
    [Fact]
    public async Task BuildBookProfileAsync_GlossaryThenDynamic_RunsBothStagesOnPersistedProfileJson()
    {
        var router = new ProfileRouter();
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(router, Repair(AnalysisRepairMode.GlossaryThenDynamic), logs);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var profile = await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        // Stage 1 (glossary) — the in-glossary leak is gone in BOTH types.
        var description = DescriptionOf(profile.CharactersJson!);
        var risingAction = RisingActionOf(profile.StoryStructureJson!);
        Assert.Contains("(פעולה)", description);
        Assert.DoesNotContain("Action", description);
        Assert.Contains("(פעולה)", risingAction);
        Assert.DoesNotContain("Action", risingAction);

        // Stage 2 (dynamic) — THE c2 DELTA: the OUT-of-glossary leaks are cleaned too.
        Assert.DoesNotContain("subtext", description, StringComparison.Ordinal);
        Assert.Contains("תת-טקסט", description);
        Assert.DoesNotContain("catharsis", risingAction, StringComparison.Ordinal);
        Assert.Contains("קתרזיס", risingAction);

        // ...and it really was the dynamic stage: exactly the two out-of-glossary runs reached the model.
        Assert.Equal(new[] { "catharsis", "subtext" }, router.TermRepairTokens.OrderBy(t => t, StringComparer.Ordinal).ToArray());

        // Must-not-touch fields survive the second stage as well.
        var chars = JsonSerializer.Deserialize<CharacterAnalysisResult>(profile.CharactersJson!, JsonOpts)!;
        Assert.Equal("דנה", chars.Characters[0].Name);
        Assert.Equal("protagonist", chars.Characters[0].Role);
        var story = JsonSerializer.Deserialize<StoryAnalysisResult>(profile.StoryStructureJson!, JsonOpts)!;
        Assert.Equal("external", story.Conflicts[0].Type);
        Assert.Equal("ongoing", story.Conflicts[0].Status);
        Assert.DoesNotContain("```", profile.CharactersJson);   // still reserialized to FE-parseable JSON
    }

    /// <summary>
    /// THE ROLLBACK. <c>Mode=Glossary</c> is the documented kill-switch, and it must stay BYTE-IDENTICAL to the
    /// pre-c2 hook: the glossary runs, the dynamic stage does not, and the out-of-glossary leak survives
    /// verbatim. Zero TermRepair calls is the model-free proof that the second stage was not merely
    /// ineffective but never selected.
    /// </summary>
    [Fact]
    public async Task BuildBookProfileAsync_ModeGlossary_RunsGlossaryOnly_AndLeavesOutOfGlossaryLeaks()
    {
        var router = new ProfileRouter();
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(router, Repair(AnalysisRepairMode.Glossary), logs);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var profile = await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        var description = DescriptionOf(profile.CharactersJson!);
        var risingAction = RisingActionOf(profile.StoryStructureJson!);

        Assert.Contains("(פעולה)", description);              // glossary ran (non-vacuity)
        Assert.Contains("(פעולה)", risingAction);
        Assert.Contains("subtext", description);              // ...and ONLY the glossary ran
        Assert.Contains("catharsis", risingAction);
        Assert.Empty(router.TermRepairTokens);
    }

    /// <summary>
    /// <c>Mode=Off</c> applies NO repair at all — neither stage is selected, so both leaks survive. (Before c2
    /// this hook had no Mode gate whatsoever and ran the glossary even under Off, the same contract violation
    /// be-c06 fixed in BookReviewService.) The reserialize is deliberately NOT conditional on a stage having
    /// run: the fence strip is what keeps the FE's bare JSON.parse working, so Mode=Off stays a repair no-op
    /// without becoming an FE-breaking one.
    /// </summary>
    [Fact]
    public async Task BuildBookProfileAsync_ModeOff_AppliesNeitherStage()
    {
        var router = new ProfileRouter();
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(router, Repair(AnalysisRepairMode.Off), logs);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var profile = await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        var description = DescriptionOf(profile.CharactersJson!);
        Assert.Contains("(Action)", description);             // glossary skipped
        Assert.Contains("subtext", description);              // dynamic skipped
        Assert.Contains("(Action)", RisingActionOf(profile.StoryStructureJson!));
        Assert.Empty(router.TermRepairTokens);
        Assert.DoesNotContain("```", profile.CharactersJson); // ...but still FE-parseable
    }

    /// <summary>
    /// A CLOSED LAYER GATE (a non-empty PerType allowlist that excludes these types) is a strict no-op even
    /// under the shipped Mode: the RAW model string — markdown fence and both leaks intact — is what is stored,
    /// and the ONE Debug line names BOTH stages so a closed gate never reads as "only the dynamic stage was
    /// skipped" (the be-c02 observability idiom).
    /// </summary>
    [Fact]
    public async Task BuildBookProfileAsync_ClosedLayerGate_StoresRawJson_AndLogsOneLineNamingBothStages()
    {
        var router = new ProfileRouter();
        var logs = new CapturingLoggerProvider();
        var cfg = Repair(AnalysisRepairMode.GlossaryThenDynamic);
        cfg.PerType = new Dictionary<string, bool> { ["Summarization"] = true }; // excludes both profile types
        using var provider = BuildProvider(router, cfg, logs);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var profile = await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        Assert.Contains("```json", profile.CharactersJson);    // raw model output, verbatim
        Assert.Contains("(Action)", profile.CharactersJson);
        Assert.Contains("subtext", profile.CharactersJson);
        Assert.Contains("catharsis", profile.StoryStructureJson);
        Assert.Empty(router.TermRepairTokens);

        var gateLines = logs.Entries
            .Where(e => e.Category.Contains(nameof(BookIntelligenceService), StringComparison.Ordinal)
                        && e.Message.Contains("gate closed", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, gateLines.Count);                      // one per repaired type, not one per stage
        Assert.All(gateLines, l =>
        {
            Assert.Equal(LogLevel.Debug, l.Level);
            Assert.Contains("PerTypeExcluded", l.Message, StringComparison.Ordinal);
            Assert.Contains("glossary", l.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dynamic", l.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// FAULT SURFACING, the fail-safe-swallow lesson. <see cref="DynamicTermRepairService"/> never throws: it
    /// catches a per-span router failure, keeps the ORIGINAL span and reports what it swallowed through
    /// <c>TermRepairResult.Fault</c>. If the profile hook discarded that, an always-on layer would ship
    /// failures silently. So: the leak survives (fail-safe), the profile still builds, the glossary half still
    /// landed — AND a Warning naming the type carries the inner exception.
    /// </summary>
    [Fact]
    public async Task BuildBookProfileAsync_DynamicStageFaults_KeepsOriginalSpans_AndLogsTheSurfacedFault()
    {
        var router = new ProfileRouter(throwOnTermRepair: true);
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(router, Repair(AnalysisRepairMode.GlossaryThenDynamic), logs);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var profile = await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        // Fail-safe: the dynamic stage's spans keep their ORIGINAL text; the glossary half still landed.
        var description = DescriptionOf(profile.CharactersJson!);
        Assert.Contains("subtext", description);
        Assert.Contains("(פעולה)", description);
        Assert.NotEmpty(router.TermRepairTokens);             // it really did attempt the model

        // ...and the swallowed fault is SURFACED by this layer rather than discarded.
        var faultLines = logs.Entries
            .Where(e => e.Category.Contains(nameof(BookIntelligenceService), StringComparison.Ordinal)
                        && e.Level == LogLevel.Warning
                        && e.Message.Contains("dynamic repair reported a fault", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, faultLines.Count);                     // CharacterAnalysis + StoryAnalysis
        Assert.All(faultLines, l => Assert.NotNull(l.Exception));
        Assert.Contains(faultLines, l => l.Message.Contains("CharacterAnalysis", StringComparison.Ordinal));
        Assert.Contains(faultLines, l => l.Message.Contains("StoryAnalysis", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE OUTER FAIL-SAFE, exercised through the one genuinely throwing step inside the dynamic block: the
    /// entity fetch. A throw there must NEVER break the profile build — the RAW string is stored (the pre-c2
    /// behaviour on every non-repaired path) and the fault is logged, not swallowed.
    /// </summary>
    [Fact]
    public async Task BuildBookProfileAsync_DynamicStageThrows_StoresRawJson_AndLogs()
    {
        var router = new ProfileRouter();
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(
            router, Repair(AnalysisRepairMode.GlossaryThenDynamic), logs, new ThrowingBookEntityProvider());
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedHebrewBookAsync(db);

        var profile = await provider.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        // Raw string stored — fence and BOTH leaks intact; the build did not throw.
        Assert.Contains("```json", profile.CharactersJson);
        Assert.Contains("(Action)", profile.CharactersJson);
        Assert.Contains("subtext", profile.CharactersJson);
        Assert.Contains("```json", profile.StoryStructureJson);

        var faultLines = logs.Entries
            .Where(e => e.Category.Contains(nameof(BookIntelligenceService), StringComparison.Ordinal)
                        && e.Level == LogLevel.Warning
                        && e.Message.Contains("storing un-repaired raw JSON (fail-safe)", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, faultLines.Count);
        Assert.All(faultLines, l => Assert.NotNull(l.Exception));
    }
}
