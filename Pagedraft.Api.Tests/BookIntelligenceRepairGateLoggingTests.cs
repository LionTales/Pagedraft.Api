using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// h1-observable-gate-skip coverage for <c>BookIntelligenceService.RepairStructuredProfileJson</c>'s
/// Enabled/PerType gate. That gate previously returned the raw model JSON with NO logging at all for a
/// null config block, Enabled=false, or a non-empty PerType map excluding the type — leaving no trace of
/// WHICH of the three reasons closed it. It now evaluates the shared
/// <see cref="AnalysisRepairGate.Evaluate"/> predicate and logs a Debug line naming the type + reason
/// before returning, mirroring the sibling gates in UnifiedAnalysisService.ApplyAnalysisRepairAsync and
/// BookReviewService's glossary/dynamic hooks.
///
/// RepairStructuredProfileJson is `private static` (only reachable in production via
/// BuildBookProfileAsync's full router/DB pipeline), so — mirroring the reflection idiom already used in
/// this project (see LineEditChunkingTests.ChunkForLineEditMethod) — these tests invoke it directly via
/// reflection with a minimal deserializable payload, avoiding a heavyweight DI/router setup for a
/// gate-only concern.
/// </summary>
public class BookIntelligenceRepairGateLoggingTests
{
    private static readonly MethodInfo RepairStructuredProfileJsonMethod = typeof(BookIntelligenceService)
        .GetMethod("RepairStructuredProfileJson", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find RepairStructuredProfileJson via reflection.");

    /// <summary>Minimal in-memory <see cref="ILogger"/> that records every entry's level + rendered
    /// message (h1-observable-gate-skip idiom, mirrors OllamaProviderFallbackLoggingTests.CapturingLogger).
    /// RepairStructuredProfileJson takes a plain (non-generic) <see cref="ILogger"/> parameter.</summary>
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

    private static string Invoke(string rawResult, AnalysisRepairOptions? cfg, ILogger logger)
    {
        Func<CharacterAnalysisResult, IReadOnlyList<RepairableField>> accessor = RepairableFields.For;
        return (string)RepairStructuredProfileJsonMethod
            .MakeGenericMethod(typeof(CharacterAnalysisResult))
            .Invoke(null, new object?[]
            {
                rawResult, "he", AnalysisType.CharacterAnalysis, cfg, accessor, logger
            })!;
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
