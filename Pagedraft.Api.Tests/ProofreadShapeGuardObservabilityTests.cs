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
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// ProofreadShapeGuardObservabilityTests - a DROP MUST BE VISIBLE.
//
// WHY THIS FILE EXISTS SEPARATELY FROM THE GUARD'S OWN UNIT TESTS. The guard removes model output. A
// fail-safe that swallows silently ships failures invisibly, and this workspace has been burned by
// exactly that shape before (a nested catch that swallowed to stay non-throwing blinded the outer
// logger, so an always-on layer shipped its failures without a trace). Correct filtering logic is
// therefore only half of "the guard is acceptable"; the other half is that a run which dropped three
// suggestions is DISTINGUISHABLE from a run that dropped none, without attaching a debugger.
//
// The two channels, both asserted end-to-end through the real UnifiedAnalysisService:
//   1. a WARNING log line per drop, carrying the offending word verbatim (Warning survives production
//      log levels; Debug/Trace would not);
//   2. a COUNT persisted on AnalysisRunLog.SuppressedSuggestionCount, right beside SuggestionCount,
//      written on every Proofread run - which is the half a log line alone cannot give a consumer.
// ---------------------------------------------------------------------------------------------
public class ProofreadShapeGuardObservabilityTests
{
    /// <summary>
    /// The manuscript sentence. Clean Hebrew; nothing in it trips the guard.
    /// </summary>
    private const string CleanInput =
        "הוא צמצם את הפער בין השניים ונשם עמוק. אחר כך פנה אל החלון והביט החוצה בשקט.";

    /// <summary>
    /// The same text as a model might return it, with ONE word corrupted into a mechanically
    /// impossible shape: a final tsadi placed mid-word. This is the corpus's only real instance of the
    /// shape, reproduced here as a fixture rather than invented.
    /// </summary>
    private const string CorruptedOutput =
        "הוא צמץם את הפער בין השניים ונשם עמוק. אחר כך פנה אל החלון והביט החוצה בשקט.";

    [Fact]
    public async Task ADroppedSuggestion_IsLoggedAtWarning_AndCountedOnTheRunLog()
    {
        var (result, runLog, logger) = await RunProofreadAsync(
            CleanInput, CorruptedOutput, new HebrewStyleOptions());

        // 1. The suggestion really was withheld.
        Assert.DoesNotContain(result.Suggestions,
            s => s.SuggestedText.Contains("צמץם", StringComparison.Ordinal));

        // 2. The count reached the persisted diagnostics surface, beside the suggestion count.
        Assert.Equal(1, result.SuppressedImpossibleSuggestionCount);
        Assert.Equal(1, runLog.SuppressedSuggestionCount);
        Assert.Equal(result.Suggestions.Count, runLog.SuggestionCount);

        // 3. The drop is in the log, at a level production keeps, naming the offending word.
        var warnings = logger.MessagesAt(LogLevel.Warning);
        var drop = Assert.Single(warnings, m => m.Contains("WITHHELD", StringComparison.Ordinal));
        Assert.Contains("צמץם", drop, StringComparison.Ordinal);
        Assert.Contains("Ai:HebrewStyle:DropOrthographicallyImpossibleSuggestions", drop,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// NON-VACUITY FOR THE TEST ABOVE, and it is not optional here: "the suggestion is absent" and
    /// "the count is 1" would BOTH be satisfied by a diff that never produced the suggestion in the
    /// first place. So the same run with the guard switched OFF must produce it, and must report zero
    /// drops. The pair is what distinguishes "the guard withheld it" from "nothing was there".
    /// </summary>
    [Fact]
    public async Task WithTheGuardOff_TheSameRunKeepsTheSuggestion_AndCountsNoDrops()
    {
        var (result, runLog, logger) = await RunProofreadAsync(
            CleanInput, CorruptedOutput,
            new HebrewStyleOptions { DropOrthographicallyImpossibleSuggestions = false });

        Assert.Contains(result.Suggestions,
            s => s.SuggestedText.Contains("צמץם", StringComparison.Ordinal));
        Assert.Equal(0, result.SuppressedImpossibleSuggestionCount);
        Assert.Equal(0, runLog.SuppressedSuggestionCount);
        Assert.DoesNotContain(logger.MessagesAt(LogLevel.Warning),
            m => m.Contains("WITHHELD", StringComparison.Ordinal));
    }

    /// <summary>
    /// A CLEAN RUN IS SILENT. The overwhelming majority of runs drop nothing, and they must not emit a
    /// warning or a non-zero count - otherwise the signal that matters is buried under a per-run line.
    /// </summary>
    [Fact]
    public async Task ARunThatDropsNothing_LogsNothingAndCountsZero()
    {
        var edited = CleanInput.Replace("בשקט", "בשתיקה", StringComparison.Ordinal);

        var (result, runLog, logger) = await RunProofreadAsync(
            CleanInput, edited, new HebrewStyleOptions());

        // NON-VACUITY: the run really did produce a suggestion, so "no drop" is a fact about the guard
        // rather than about an empty suggestion list.
        Assert.NotEmpty(result.Suggestions);
        Assert.Equal(0, result.SuppressedImpossibleSuggestionCount);
        Assert.Equal(0, runLog.SuppressedSuggestionCount);
        Assert.DoesNotContain(logger.MessagesAt(LogLevel.Warning),
            m => m.Contains("WITHHELD", StringComparison.Ordinal));
    }

    // ── the CHUNKED half of the same contract ────────────────────────────────────────────────────

    /// <summary>
    /// Clean Hebrew sentences, none of which trips the guard, cycled to build a chapter long enough to
    /// chunk. Deliberately free of the guard word so <see cref="GuardWordSentence"/> can be the ONLY
    /// occurrence of it in the whole fixture - that is what makes "exactly one chunk is corrupted" a
    /// property of the fixture rather than a hope.
    /// </summary>
    private static readonly string[] CleanSentences =
    {
        "אחר כך פנה אל החלון והביט החוצה בשקט מוחלט.",
        "הרוח נשבה בין העצים והעלים רשרשו על המדרכה הרטובה.",
        "הוא נשם עמוק והמשיך בדרכו הארוכה אל הכפר הקטן.",
        "האור דעך לאט מעל הגגות והעיר שקעה בדממה.",
        "היא סגרה את הספר והניחה אותו על השולחן הישן.",
        "הצללים התארכו על הקיר והשעון המשיך לתקתק בסבלנות.",
        "מישהו קרא בשמו מרחוק אך הוא לא הפנה את ראשו.",
        "בבוקר שלמחרת הוא התעורר מוקדם והרגיש קל הרבה יותר."
    };

    /// <summary>The one sentence in the fixture carrying the word the mocked model will corrupt.</summary>
    private const string GuardWordSentence = "הוא צמצם את הפער בין השניים ונשם עמוק.";

    /// <summary>
    /// A Hebrew chapter long enough that <c>RunAsync</c> routes to <c>RunProofreadChunkedAsync</c>, with
    /// the guard word appearing EXACTLY ONCE (see the assertion in the test, which pins that).
    /// </summary>
    private static string LongHebrewInput()
    {
        var sentences = new List<string>();
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var sentence in CleanSentences)
            {
                sentences.Add(sentence);
                if (pass == 0 && sentence == CleanSentences[3])
                    sentences.Add(GuardWordSentence);
            }
        }

        return string.Join(" ", sentences);
    }

    /// <summary>
    /// THE OTHER RUN-LOG WRITER. Every other assertion on <c>SuppressedSuggestionCount</c> in this suite
    /// goes through the ~75-character fixture above, which takes the SINGLE-SHOT path and therefore only
    /// ever exercises <c>PersistSingleChunkRunLog</c>. The chunked writer's copy of the same line was
    /// compile-verified only - and the chunked surface is the one that matters, because
    /// <c>RealProseNonWordResidue</c> records all fifteen instances of the class this guard exists for as
    /// riding <c>GoldPromptSurface.ChunkedPerChunk</c>, and
    /// <c>AnalysisRunLog.SuppressedSuggestionCount</c>'s own docblock claims the field is written on every
    /// Proofread run, chunked or not. This test is what makes that claim true of both halves.
    ///
    /// NON-VACUITY, AND IT IS THE WHOLE POINT: without the <c>TotalChunks &gt; 1</c> assertion this
    /// degrades silently into a second single-shot test the moment the chunk sizing moves, and it would
    /// still pass every other assertion here for entirely the wrong reason.
    /// </summary>
    [Fact]
    public async Task OnTheCHUNKEDPath_TheDropIsCountedOnTheRunLogThatWriterPersists()
    {
        var inputText = LongHebrewInput();

        // The fixture's own precondition: the mocked model's Replace must be able to corrupt exactly one
        // chunk, which it can only do if the guard word occurs exactly once in the whole chapter.
        Assert.Equal(1, CountOccurrences(inputText, "צמצם"));

        var (result, runLog) = await RunChunkedProofreadAsync(inputText);

        // 1. NON-VACUITY FLOOR. This really is the chunked writer's row and not the single-shot one.
        Assert.True(runLog.TotalChunks > 1,
            $"the run took the SINGLE-SHOT path (TotalChunks={runLog.TotalChunks}), so this test proved " +
            "nothing about PersistChunkedRunLog - it is a duplicate of its single-shot twin. The Hebrew " +
            "chunk target moved out from under the fixture; lengthen the input or lower " +
            "AiOptions.ProofreadChunkTargetWords, do not relax this assertion.");

        // 2. The suggestion really was withheld - so "the count is 1" is not satisfied by a diff that
        //    never produced the corrupted suggestion in the first place.
        Assert.DoesNotContain(result.Suggestions,
            s => s.SuggestedText.Contains("צמץם", StringComparison.Ordinal));

        // 3. The count reached the DURABLE surface, written by PersistChunkedRunLog, beside the
        //    suggestion count an operator is told to read it against.
        Assert.Equal(1, result.SuppressedImpossibleSuggestionCount);
        Assert.Equal(1, runLog.SuppressedSuggestionCount);
        Assert.Equal(result.Suggestions.Count, runLog.SuggestionCount);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the CHUNKED proofread path end to end through the real <c>UnifiedAnalysisService</c> with a
    /// mocked router. Construction idiom reused from
    /// <c>AnalysisRunLogTests.RunAsync_Proofread_Chunked_EmptyResponse_PersistsSingleOutcomePerChunk</c>:
    /// there is no flag for "chunk this", so the only lever is to make the word count exceed the target
    /// <c>ProofreadChunkTargetWordsFor</c> resolves from <c>AiOptions</c>. A single parallel slot keeps the
    /// fan-out sequential and the run deterministic.
    ///
    /// THE ROUTER ANSWERS PER CHUNK, which a fixed <c>ReturnsAsync</c> cannot do: a chunked run makes one
    /// model call per chunk, so a constant response would be merged in N times. It echoes each chunk
    /// verbatim (the service strips the [TEXT_TO_CORRECT] markers it wraps the chunk in) and corrupts the
    /// single occurrence of the guard word, so exactly one chunk comes back changed and the merged output
    /// differs from the input by one word. The read-only overlap prefix travels in the INSTRUCTION rather
    /// than in InputText, so the echo cannot pick the guard word up from a neighbouring chunk.
    /// </summary>
    private static async Task<(AnalysisResult Result, AnalysisRunLog RunLog)>
        RunChunkedProofreadAsync(string inputText)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) => new AiResponse
            {
                Content = req.InputText.Replace("צמצם", "צמץם", StringComparison.Ordinal),
                Provider = "test-provider",
                Model = "test-model"
            });

        var chapterId = Guid.NewGuid();
        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, AnalysisType.Proofread,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = Guid.NewGuid(),
                ChapterId = chapterId,
                SceneId = null
            });

        var chunkingOptions = new AiOptions
        {
            ProofreadChunkTargetWords = 20,
            MaxParallelProofreadChunks = 1
        };

        var svc = new UnifiedAnalysisService(
            db,
            routerMock.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(chunkingOptions),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            contextMock.Object,
            // Guard ON: the shipped class default, which is what this test is about.
            new SuggestionDiffService(new HebrewStyleOptions()),
            // Ktiv-male OFF so the counts below are about the shape guard and nothing else.
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        var result = await svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.Proofread, chapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        var runLog = await db.AnalysisRunLogs.SingleAsync(r => r.AnalysisResultId == result.Id);

        return (result, runLog);
    }

    private static async Task<(AnalysisResult Result, AnalysisRunLog RunLog, CapturingLogger<UnifiedAnalysisService> Logger)>
        RunProofreadAsync(string inputText, string modelOutput, HebrewStyleOptions hebrewStyle)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse
            {
                Content = modelOutput,
                Provider = "test-provider",
                Model = "test-model"
            });

        var chapterId = Guid.NewGuid();
        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, AnalysisType.Proofread,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = Guid.NewGuid(),
                ChapterId = chapterId,
                SceneId = null
            });

        var logger = new CapturingLogger<UnifiedAnalysisService>();

        var svc = new UnifiedAnalysisService(
            db,
            routerMock.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(new AiOptions()),
            logger,
            new AnalysisProgressTracker(),
            contextMock.Object,
            new SuggestionDiffService(hebrewStyle),
            // Ktiv-male OFF so the assertions below are about the shape guard and nothing else.
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        var result = await svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.Proofread, chapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        var runLog = await db.AnalysisRunLogs.SingleAsync(r => r.AnalysisResultId == result.Id);

        return (result, runLog, logger);
    }

    /// <summary>Generic capturing logger; the existing AiTier one is non-generic and cannot be injected here.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Add((logLevel, formatter(state, exception)));

        public IReadOnlyList<string> MessagesAt(LogLevel level) =>
            _entries.Where(e => e.Level == level).Select(e => e.Message).ToList();
    }
}
