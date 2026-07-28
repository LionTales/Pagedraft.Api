using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Regression tests for the analysis-output repair wiring on <see cref="UnifiedAnalysisService.RunRawAsync"/>.
///
/// RunRawAsync is the NON-persisting path BookIntelligenceService.SummarizeChaptersAsync uses to produce the
/// flat chapter summary it stores in ChunkSummary.SummaryText. It previously returned the sanitized model
/// text WITHOUT running ApplyAnalysisRepairAsync — so a Hebrew Summarization routed through it skipped the
/// shipped glossary repair that RunAsync / RunWithInputAsync / streaming / chunked LineEdit all apply, and
/// persisted chapter summaries leaked English the rest of the pipeline had cleaned. These tests pin that
/// RunRawAsync now runs the SAME repair layer, still type-scoped and Hebrew-gated (no over-application).
/// </summary>
public class RunRawAnalysisRepairTests
{
    private static IAiRouter RouterReturning(string content)
    {
        var mock = new Mock<IAiRouter>();
        mock.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = content, Provider = "test", Model = "test" });
        return mock.Object;
    }

    private static UnifiedAnalysisService NewService(
        AppDbContext db,
        IAiRouter router,
        AnalysisRepairOptions? repair,
        IAiRouter? termRepairRouter = null,
        IBookEntityProvider? entityProvider = null,
        ILogger<UnifiedAnalysisService>? logger = null)
        => new(
            db,
            router,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(new AiOptions { AnalysisRepair = repair }),
            logger ?? NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            new Mock<IAnalysisContextService>().Object,
            new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions()),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(
                termRepairRouter ?? new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            entityProvider ?? new StubBookEntityProvider());

    /// <summary>Minimal in-memory <see cref="ILogger{T}"/> that records every entry's level + rendered
    /// message (h1-observable-gate-skip idiom, mirrors OllamaProviderFallbackLoggingTests.CapturingLogger).</summary>
    private sealed class CapturingLogger : ILogger<UnifiedAnalysisService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static AppDbContext NewDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>The core regression: a Hebrew Summarization with an English leak is glossary-repaired on the
    /// raw path exactly as it would be on the persisted seams (shipped default { Enabled:true, GuardOnly:true }).</summary>
    [Fact]
    public async Task RunRawAsync_HebrewSummarization_AppliesGlossaryRepair()
    {
        await using var db = NewDb();
        var router = RouterReturning("סיכום הפרק כולל תיאור פעולה (Action) מהיר.");
        var svc = NewService(db, router, new AnalysisRepairOptions { Enabled = true, GuardOnly = true });

        var result = await svc.RunRawAsync(
            "טקסט הפרק.", AnalysisType.Summarization, instruction: null, language: "he", ct: CancellationToken.None);

        Assert.Contains("(פעולה)", result);
        Assert.DoesNotContain("Action", result);
    }

    /// <summary>Guard: the repair stays TYPE-SCOPED. A non-target type (BookOverview) is a strict no-op even on
    /// a Hebrew book, so RunRawAsync must not translate its Latin — proving the fix reused the type-aware layer
    /// rather than blanket-translating every raw run.</summary>
    [Fact]
    public async Task RunRawAsync_NonTargetType_LeavesLatinUntouched()
    {
        await using var db = NewDb();
        var router = RouterReturning("סקירה כוללת (Action) כאן.");
        var svc = NewService(db, router, new AnalysisRepairOptions { Enabled = true, GuardOnly = true });

        var result = await svc.RunRawAsync(
            "טקסט.", AnalysisType.BookOverview, instruction: null, language: "he", ct: CancellationToken.None);

        Assert.Contains("Action", result); // BookOverview is not a repair target → unchanged
    }

    /// <summary>Guard: the repair respects config. With the layer disabled (no Ai:AnalysisRepair block) the raw
    /// path returns the sanitized text unchanged, same as before the fix.</summary>
    [Fact]
    public async Task RunRawAsync_RepairDisabled_ReturnsSanitizedTextUnchanged()
    {
        await using var db = NewDb();
        var router = RouterReturning("סיכום עם (Action) בפנים.");
        var svc = NewService(db, router, repair: null);

        var result = await svc.RunRawAsync(
            "טקסט.", AnalysisType.Summarization, instruction: null, language: "he", ct: CancellationToken.None);

        Assert.Contains("Action", result); // layer off → no glossary applied
    }

    // ── be-c02: the bookId SEAM. RunRawAsync used to hard-code Guid.Empty into ApplyAnalysisRepairAsync, so the
    // per-book proper-noun LEAVE set (IBookEntityProvider) was ALWAYS empty on this seam while the four persisted
    // seams passed the real id. Summarization is the one type this seam repairs and its output is persisted to
    // ChunkSummary.SummaryText — chapter summaries name characters constantly, and a SENTENCE-INITIAL Latin name
    // is not spared by the classifier's Title-Case-MID-sentence proper-noun rule (rule 7), so with an empty entity
    // set there is nothing left to stop the dynamic stage rewriting the book's own character name. These tests
    // drive the real classifier: an entity-set hit means ZERO term-repair model calls (LEAVE), a miss means one
    // call and a spliced replacement (REPAIR) — so the model-call count is the proof the set reached the classifier.

    /// <summary>A Hebrew summary opening with the book's own character name. "Daniel" is Title-Case but
    /// SENTENCE-INITIAL, so ForeignRunClassifier rule 7 does NOT spare it — only the book-entity set (rule 2) can.</summary>
    private const string HebrewSummaryWithSentenceInitialName =
        "Daniel נכנס אל החדר האפל ומצא את המכתב.";

    /// <summary>The term-repair model's reply for the marked «Daniel» span: a Hebrew replacement that PASSES
    /// DynamicTermRepairService's validation (native script, one word, well under the length cap) — so if the
    /// classifier says REPAIR, the name really is rewritten.</summary>
    private static Mock<IAiRouter> TermRepairRouterReturningHebrewName()
    {
        var mock = new Mock<IAiRouter>();
        mock.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = "{\"replacement\":\"דניאל\"}", Provider = "test", Model = "test" });
        return mock;
    }

    /// <summary>The core seam fix: RunRawAsync WITH a bookId asks the provider for THAT EXACT book, and the
    /// entity set it returns reaches the classifier — the book's own character name is LEFT byte-identical and
    /// the term-repair model is never called. The provider is seeded FOR the real bookId only, so the pre-fix
    /// hard-coded Guid.Empty would have returned the empty set and rewritten the name (revert-verify).</summary>
    [Fact]
    public async Task RunRawAsync_WithBookId_ThreadsBookEntitiesToClassifier_SparingTheBooksOwnName()
    {
        await using var db = NewDb();
        var bookId = Guid.NewGuid();
        var entities = StubBookEntityProvider.For(bookId, "Daniel");
        var termRouter = TermRepairRouterReturningHebrewName();
        var svc = NewService(
            db,
            RouterReturning(HebrewSummaryWithSentenceInitialName),
            new AnalysisRepairOptions
            {
                Enabled = true,
                GuardOnly = true,
                Mode = AnalysisRepairMode.GlossaryThenDynamic // the shipped default: the dynamic stage runs
            },
            termRouter.Object,
            entities);

        var result = await svc.RunRawAsync(
            "טקסט הפרק.", AnalysisType.Summarization, instruction: null, language: "he",
            bookId: bookId, ct: CancellationToken.None);

        // The provider was asked for the REAL book (not Guid.Empty) — the seam threads the id through.
        Assert.Equal(new[] { bookId }, entities.RequestedBookIds);

        // ...and the set it returned reached the classifier: the name is spared, so NO term-repair call was made.
        Assert.Contains("Daniel", result);
        Assert.DoesNotContain("דניאל", result);
        termRouter.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Fail-safe parity: a caller with NO book context (bookId omitted) behaves exactly as this seam did
    /// before — Guid.Empty is passed, the provider returns the EMPTY set, and the dynamic stage repairs the run.
    /// This is the control for the test above: same text, same routers, only the bookId differs, and the verdict
    /// flips — which is what proves the entity set (not something else) drives the LEAVE.</summary>
    [Fact]
    public async Task RunRawAsync_WithoutBookId_UsesEmptyEntitySet_AndRepairsAsBefore()
    {
        await using var db = NewDb();
        var otherBookId = Guid.NewGuid();
        var entities = StubBookEntityProvider.For(otherBookId, "Daniel"); // seeded, but NOT for the id this call passes
        var termRouter = TermRepairRouterReturningHebrewName();
        var svc = NewService(
            db,
            RouterReturning(HebrewSummaryWithSentenceInitialName),
            new AnalysisRepairOptions
            {
                Enabled = true,
                GuardOnly = true,
                Mode = AnalysisRepairMode.GlossaryThenDynamic
            },
            termRouter.Object,
            entities);

        var result = await svc.RunRawAsync(
            "טקסט הפרק.", AnalysisType.Summarization, instruction: null, language: "he",
            ct: CancellationToken.None); // no bookId → the fail-safe

        Assert.Equal(new[] { Guid.Empty }, entities.RequestedBookIds); // null degrades to Guid.Empty (empty set)

        // Empty set → the sentence-initial name is classified REPAIR → one span-scoped call → spliced back.
        Assert.Contains("דניאל", result);
        Assert.DoesNotContain("Daniel", result);
        termRouter.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── h1-observable-gate-skip: UnifiedAnalysisService.ApplyAnalysisRepairAsync's first gate previously
    // returned silently for a null config block, Enabled=false, or a non-empty PerType map excluding the
    // type — leaving no trace of WHICH of the three reasons closed it (each has a different fix). These
    // tests drive the gate through RunRawAsync (the same public seam RunRawAnalysisRepairTests uses above)
    // and assert the Debug-level "gate closed" line names the type + reason, or (for an allowed type) that
    // NO such line is emitted at all. ──

    private static IReadOnlyList<(LogLevel Level, string Message)> SkipLogEntries(CapturingLogger logger) =>
        logger.Entries.Where(e => e.Message.Contains("gate closed", StringComparison.Ordinal)).ToList();

    private static IReadOnlyList<string> SkipLogMessages(CapturingLogger logger) =>
        SkipLogEntries(logger).Select(e => e.Message).ToList();

    [Fact]
    public async Task ApplyAnalysisRepairAsync_PerTypeExcludesType_LogsDebugWithTypeAndReason()
    {
        await using var db = NewDb();
        var logger = new CapturingLogger();
        var svc = NewService(
            db,
            RouterReturning("סיכום עם (Action) בפנים."),
            new AnalysisRepairOptions
            {
                Enabled = true,
                // Non-empty allowlist that does NOT include Summarization → PerTypeExcluded.
                PerType = new Dictionary<string, bool> { ["LiteraryAnalysis"] = true }
            },
            logger: logger);

        await svc.RunRawAsync(
            "טקסט.", AnalysisType.Summarization, instruction: null, language: "he", ct: CancellationToken.None);

        var entry = Assert.Single(SkipLogEntries(logger));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("Summarization", entry.Message, StringComparison.Ordinal);
        Assert.Contains("PerTypeExcluded", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAnalysisRepairAsync_Disabled_LogsDebugWithDisabledReason()
    {
        await using var db = NewDb();
        var logger = new CapturingLogger();
        var svc = NewService(
            db,
            RouterReturning("סיכום עם (Action) בפנים."),
            new AnalysisRepairOptions { Enabled = false },
            logger: logger);

        await svc.RunRawAsync(
            "טקסט.", AnalysisType.Summarization, instruction: null, language: "he", ct: CancellationToken.None);

        var line = Assert.Single(SkipLogMessages(logger));
        Assert.Contains("Summarization", line, StringComparison.Ordinal);
        Assert.Contains("Disabled", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAnalysisRepairAsync_NullConfig_LogsDebugWithNullConfigReason()
    {
        await using var db = NewDb();
        var logger = new CapturingLogger();
        var svc = NewService(
            db,
            RouterReturning("סיכום עם (Action) בפנים."),
            repair: null,
            logger: logger);

        await svc.RunRawAsync(
            "טקסט.", AnalysisType.Summarization, instruction: null, language: "he", ct: CancellationToken.None);

        var line = Assert.Single(SkipLogMessages(logger));
        Assert.Contains("Summarization", line, StringComparison.Ordinal);
        Assert.Contains("NullConfig", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAnalysisRepairAsync_AllowedType_LogsNoSkipLine()
    {
        await using var db = NewDb();
        var logger = new CapturingLogger();
        var svc = NewService(
            db,
            RouterReturning("סיכום עם (Action) בפנים."),
            new AnalysisRepairOptions { Enabled = true, GuardOnly = true }, // no PerType restriction → allowed
            logger: logger);

        await svc.RunRawAsync(
            "טקסט.", AnalysisType.Summarization, instruction: null, language: "he", ct: CancellationToken.None);

        Assert.Empty(SkipLogMessages(logger));
    }
}
