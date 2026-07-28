using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// h1-observable-gate-skip coverage for <c>BookIntelligenceService.RepairStructuredProfileJsonAsync</c>'s
/// Enabled/PerType gate. That gate previously returned the raw model JSON with NO logging at all for a
/// null config block, Enabled=false, or a non-empty PerType map excluding the type — leaving no trace of
/// WHICH of the three reasons closed it. It now evaluates the shared
/// <see cref="AnalysisRepairGate.Evaluate"/> predicate and logs a Debug line naming the type + reason
/// before returning, mirroring the sibling gates in UnifiedAnalysisService.ApplyAnalysisRepairAsync and
/// BookReviewService's glossary/dynamic hooks.
///
/// RepairStructuredProfileJsonAsync is `private` (only reachable in production via BuildBookProfileAsync's
/// full router/DB pipeline), so — mirroring the reflection idiom already used in this project (see
/// LineEditChunkingTests.ChunkForLineEditMethod) — these tests invoke it directly via reflection with a
/// minimal deserializable payload, avoiding a heavyweight DI/router setup for a gate-only concern.
///
/// c2 made the hook an INSTANCE + async method (it now needs the injected DynamicTermRepairService +
/// IBookEntityProvider for its second stage), so the reflection target moved from Static to Instance and the
/// tests await the returned Task. The instance is built with `null!` for the three collaborators the gate
/// path provably never reaches (AppDbContext / UnifiedAnalysisService / BookContextAssembler are only used
/// by BuildBookProfileAsync itself); the two the hook DOES use are real, so a stage that unexpectedly ran
/// would fault visibly rather than being masked by a null.
/// </summary>
public class BookIntelligenceRepairGateLoggingTests
{
    private static readonly MethodInfo RepairStructuredProfileJsonMethod = typeof(BookIntelligenceService)
        .GetMethod("RepairStructuredProfileJsonAsync", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not find RepairStructuredProfileJsonAsync via reflection.");

    /// <summary>Minimal in-memory <see cref="ILogger"/> that records every entry's level + rendered
    /// message (h1-observable-gate-skip idiom, mirrors OllamaProviderFallbackLoggingTests.CapturingLogger).
    /// RepairStructuredProfileJsonAsync takes a plain (non-generic) <see cref="ILogger"/> parameter.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // An empty-but-valid CharacterAnalysisResult payload: every property has a `= new()`/`= string.Empty`
    // default, so this deserializes cleanly regardless of whether the gate lets the method past it.
    private const string MinimalCharacterAnalysisJson = "{}";

    /// <summary>The hook's owner. Only the two collaborators its second stage uses are real; the three that
    /// belong to BuildBookProfileAsync (db / analysis / assembler) are never reached from this method and are
    /// deliberately left null so an unexpected code path faults loudly instead of quietly succeeding.</summary>
    private static BookIntelligenceService NewService(IBookEntityProvider entities) =>
        new(
            db: null!,
            analysis: null!,
            bookContextAssembler: null!,
            aiOptions: Options.Create(new AiOptions()),
            bookEntities: entities,
            dynamicTermRepair: new DynamicTermRepairService(
                new ThrowingRouter(), NullLogger<DynamicTermRepairService>.Instance),
            logger: NullLogger<BookIntelligenceService>.Instance);

    /// <summary>A router that must never be called on these gate-only paths (Mode is the class default
    /// <see cref="AnalysisRepairMode.Glossary"/> everywhere here, so the dynamic stage is never selected).
    /// If one ever were, the throw is caught by the hook's fail-safe — but the entity spy below records the
    /// call, which is the assertion that would actually fail.</summary>
    private sealed class ThrowingRouter : IAiRouter
    {
        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The gate tests must never reach the model.");
        public IAsyncEnumerable<string> StreamCompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static string Invoke(string rawResult, AnalysisRepairOptions? cfg, ILogger logger,
        IBookEntityProvider? entities = null)
    {
        Func<CharacterAnalysisResult, IReadOnlyList<RepairableField>> accessor = RepairableFields.For;
        var task = (Task<string>)RepairStructuredProfileJsonMethod
            .MakeGenericMethod(typeof(CharacterAnalysisResult))
            .Invoke(NewService(entities ?? new StubBookEntityProvider()), new object?[]
            {
                rawResult, "he", AnalysisType.CharacterAnalysis, cfg, accessor,
                Guid.NewGuid(), logger, CancellationToken.None
            })!;
        return task.GetAwaiter().GetResult();
    }

    private static IReadOnlyList<(LogLevel Level, string Message)> SkipLogEntries(CapturingLogger logger) =>
        logger.Entries.Where(e => e.Message.Contains("gate closed", StringComparison.Ordinal)).ToList();

    [Fact]
    public void PerTypeExcludesType_LogsDebugWithTypeAndReason()
    {
        var logger = new CapturingLogger();
        var cfg = new AnalysisRepairOptions
        {
            Enabled = true,
            // Non-empty allowlist that does NOT include CharacterAnalysis → PerTypeExcluded.
            PerType = new Dictionary<string, bool> { ["Summarization"] = true }
        };

        var result = Invoke(MinimalCharacterAnalysisJson, cfg, logger);

        Assert.Equal(MinimalCharacterAnalysisJson, result); // gate closed → raw string returned verbatim
        var entry = Assert.Single(SkipLogEntries(logger));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("CharacterAnalysis", entry.Message, StringComparison.Ordinal);
        Assert.Contains("PerTypeExcluded", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_LogsDebugWithDisabledReason()
    {
        var logger = new CapturingLogger();
        var cfg = new AnalysisRepairOptions { Enabled = false };

        var result = Invoke(MinimalCharacterAnalysisJson, cfg, logger);

        Assert.Equal(MinimalCharacterAnalysisJson, result);
        var entry = Assert.Single(SkipLogEntries(logger));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("CharacterAnalysis", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Disabled", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConfig_LogsDebugWithNullConfigReason()
    {
        var logger = new CapturingLogger();

        var result = Invoke(MinimalCharacterAnalysisJson, cfg: null, logger);

        Assert.Equal(MinimalCharacterAnalysisJson, result);
        var entry = Assert.Single(SkipLogEntries(logger));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("CharacterAnalysis", entry.Message, StringComparison.Ordinal);
        Assert.Contains("NullConfig", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedType_LogsNoSkipLine()
    {
        var logger = new CapturingLogger();
        var cfg = new AnalysisRepairOptions { Enabled = true }; // no PerType restriction → allowed

        Invoke(MinimalCharacterAnalysisJson, cfg, logger);

        Assert.Empty(SkipLogEntries(logger));
    }
}
