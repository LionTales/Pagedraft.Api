using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic tests for the whole-book review ENGINE-path glossary safety net (f5-wire JOB 2):
/// <see cref="BookReviewService.ApplyGlossaryToFindings"/> plus the <see cref="RepairableFields.For(BookFinding)"/>
/// entity overload and the shared <see cref="GlossaryRepairPass.RepairFields"/> entry point.
///
/// BookReview runs on the SAME gemma4:12b as LiteraryAnalysis and emits the SAME structured Hebrew prose
/// (findings[].rationale / suggestedAction) that leaks English terms stochastically, but it never flows through
/// UnifiedAnalysisService.ApplyAnalysisRepairAsync — so the glossary is hooked directly on the finalised
/// BookFinding entities before persist. These tests prove: (a) the "(Action)"/"(Tension)" leak family is
/// Hebraised on Rationale + SuggestedAction while EVERY other field (dimension/verdict/severity/evidence/
/// chapterAnchors/dedupKey/status/builtWithModel) stays byte-identical; (b) the pass is fail-safe (a faulting
/// enumerator is swallowed, nothing thrown); (c) null SuggestedAction / null list elements are safe and never
/// synthesised; and the gate (Enabled / PerType "BookReview" / Hebrew-only) is honoured.
///
/// NO Ollama, NO DB, NO windowed engine — the repair step is a pure static method, so these run in CI always.
/// </summary>
public class BookReviewGlossaryRepairTests
{
    // Shipped-default config: Enabled + GuardOnly + an explicit PerType allowlist that includes BookReview
    // (mirrors appsettings.json Ai:AnalysisRepair after f5-wire JOB 1).
    private static AnalysisRepairOptions EnabledCfg() => new()
    {
        Enabled = true,
        GuardOnly = true,
        PerType = new Dictionary<string, bool>
        {
            ["Summarization"] = true,
            ["LiteraryAnalysis"] = true,
            ["LinguisticAnalysis"] = true,
            ["LineEdit"] = true,
            ["BookOverview"] = true,
            ["CharacterAnalysis"] = true,
            ["StoryAnalysis"] = true,
            ["QA"] = true,
            ["BookReview"] = true,
        }
    };

    private static BookFinding SampleFinding(string rationale, string? suggestedAction) => new()
    {
        BookId = Guid.NewGuid(),
        Language = "he",
        Dimension = "plot",
        Verdict = "improve",
        Severity = 2,
        Rationale = rationale,
        EvidenceJson = "[{\"chapterOrder\":3,\"excerpt\":\"ציטוט מהטקסט\"}]",
        ChapterAnchorsJson = "[{\"order\":3,\"title\":\"הפרק השלישי\"}]",
        SuggestedAction = suggestedAction,
        Status = "acknowledged",
        DedupKey = "abc123def456",
        BuiltWithModel = "gemma4:12b",
    };

    // ─── (a) scoping + byte-identity: Rationale leak Hebraised, everything else untouched ───

    [Fact]
    public void RationaleWithGlossaryTerm_IsHebraized_AllOtherFieldsByteIdentical()
    {
        var finding = SampleFinding("הממצא מצביע על תיאור פעולה (Action) עז בפרק.", suggestedAction: null);

        // Snapshot every must-not-touch field BEFORE.
        var dimension = finding.Dimension;
        var verdict = finding.Verdict;
        var severity = finding.Severity;
        var evidence = finding.EvidenceJson;
        var anchors = finding.ChapterAnchorsJson;
        var dedupKey = finding.DedupKey;
        var status = finding.Status;
        var model = finding.BuiltWithModel;

        var changed = BookReviewService.ApplyGlossaryToFindings(
            new[] { finding }, "he", EnabledCfg());

        Assert.Equal(1, changed);
        Assert.Contains("(פעולה)", finding.Rationale);
        Assert.DoesNotContain("Action", finding.Rationale);

        // BYTE-IDENTICAL on everything else.
        Assert.Equal(dimension, finding.Dimension);
        Assert.Equal(verdict, finding.Verdict);
        Assert.Equal(severity, finding.Severity);
        Assert.Equal(evidence, finding.EvidenceJson);
        Assert.Equal(anchors, finding.ChapterAnchorsJson);
        Assert.Equal(dedupKey, finding.DedupKey); // dedup key derived from RAW rationale — never recomputed here
        Assert.Equal(status, finding.Status);
        Assert.Equal(model, finding.BuiltWithModel);
    }

    [Fact]
    public void SuggestedActionWithGlossaryTerm_IsHebraized_RationaleAndActionBothRepaired()
    {
        var finding = SampleFinding(
            "הממצא: (Tension) גוברת לאורך הפרק.",
            "הדק את פתיחת הפרק כדי להגביר (Tension).");

        var changed = BookReviewService.ApplyGlossaryToFindings(
            new[] { finding }, "he", EnabledCfg());

        Assert.Equal(1, changed); // one finding changed (both its fields were repaired within it)
        Assert.Contains("(מתח)", finding.Rationale);
        Assert.DoesNotContain("Tension", finding.Rationale);
        Assert.NotNull(finding.SuggestedAction);
        Assert.Contains("(מתח)", finding.SuggestedAction!);
        Assert.DoesNotContain("Tension", finding.SuggestedAction!);
    }

    [Fact]
    public void CleanHebrewFinding_IsByteIdentical_ReturnsZero()
    {
        var finding = SampleFinding("הפרק בונה מתח הדרגתי ומגיע לשיא רגשי.", "הדק את הפתיחה.");
        var rationale = finding.Rationale;
        var action = finding.SuggestedAction;

        var changed = BookReviewService.ApplyGlossaryToFindings(
            new[] { finding }, "he", EnabledCfg());

        Assert.Equal(0, changed);
        Assert.Equal(rationale, finding.Rationale);
        Assert.Equal(action, finding.SuggestedAction);
    }

    // ─── (c) null SuggestedAction / null element / null+empty list are safe ───

    [Fact]
    public void NullSuggestedAction_StaysNull_RationaleStillRepaired_NoThrow()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);

        var changed = BookReviewService.ApplyGlossaryToFindings(
            new[] { finding }, "he", EnabledCfg());

        Assert.Equal(1, changed);
        Assert.Contains("(פעולה)", finding.Rationale);
        Assert.Null(finding.SuggestedAction); // never synthesised from null
    }

    [Fact]
    public void NullElementInList_IsSkipped_ValidFindingRepaired_NoThrow()
    {
        var valid = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var list = new List<BookFinding> { null!, valid };

        var changed = BookReviewService.ApplyGlossaryToFindings(list, "he", EnabledCfg());

        Assert.Equal(1, changed);
        Assert.Contains("(פעולה)", valid.Rationale);
    }

    [Fact]
    public void NullOrEmptyList_IsNoOp_NoThrow()
    {
        Assert.Equal(0, BookReviewService.ApplyGlossaryToFindings(null!, "he", EnabledCfg()));
        Assert.Equal(0, BookReviewService.ApplyGlossaryToFindings(Array.Empty<BookFinding>(), "he", EnabledCfg()));
    }

    // ─── (b) fail-safe: a faulting enumerator is swallowed — the pass can NEVER throw ───

    [Fact]
    public void FaultingEnumerator_IsSwallowed_DoesNotThrow_ReturnsZero()
    {
        // A list whose Count is positive (so the gate is passed) but whose enumeration THROWS. The outer
        // belt-and-braces catch must swallow it: the pass returns 0 and never throws into the review build.
        var throwing = new ThrowingFindingList();

        var ex = Record.Exception(() =>
        {
            var changed = BookReviewService.ApplyGlossaryToFindings(throwing, "he", EnabledCfg());
            Assert.Equal(0, changed);
        });

        Assert.Null(ex); // no throw escaped
    }

    // ─── Gate: Enabled / null cfg / PerType allowlist / Hebrew-only ───

    [Fact]
    public void LayerDisabled_IsNoOp_ByteIdentical()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var cfg = EnabledCfg();
        cfg.Enabled = false;

        var changed = BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", cfg);

        Assert.Equal(0, changed);
        Assert.Contains("Action", finding.Rationale); // untouched — layer off
    }

    [Fact]
    public void NullConfig_IsNoOp_ByteIdentical()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);

        var changed = BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", cfg: null);

        Assert.Equal(0, changed);
        Assert.Contains("Action", finding.Rationale);
    }

    [Fact]
    public void PerTypeExcludesBookReview_IsNoOp_ByteIdentical()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var cfg = new AnalysisRepairOptions
        {
            Enabled = true,
            // A non-empty allowlist WITHOUT BookReview => BookReview is skipped (mirrors PerTypeAllows).
            PerType = new Dictionary<string, bool> { ["LiteraryAnalysis"] = true }
        };

        var changed = BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", cfg);

        Assert.Equal(0, changed);
        Assert.Contains("Action", finding.Rationale);
    }

    [Fact]
    public void PerTypeBookReviewFalse_IsNoOp()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var cfg = new AnalysisRepairOptions
        {
            Enabled = true,
            PerType = new Dictionary<string, bool> { ["BookReview"] = false }
        };

        Assert.Equal(0, BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", cfg));
        Assert.Contains("Action", finding.Rationale);
    }

    [Fact]
    public void EmptyPerType_AllowsRepair_NoRestriction()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var cfg = new AnalysisRepairOptions { Enabled = true, PerType = new Dictionary<string, bool>() };

        var changed = BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", cfg);

        Assert.Equal(1, changed);
        Assert.Contains("(פעולה)", finding.Rationale);
    }

    [Fact]
    public void NonHebrewBook_IsNeverTranslated_NoOp()
    {
        // The glossary is English -> Hebrew; on an English book the Hebrew gate inside RepairFields must skip.
        var finding = SampleFinding("The scene drives the Action forward.", "Tighten the Action.");

        var changed = BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "en", EnabledCfg());

        Assert.Equal(0, changed);
        Assert.Contains("Action", finding.Rationale);
        Assert.Contains("Action", finding.SuggestedAction!);
    }

    // ─── h1-observable-gate-skip: the Enabled/PerType gate above previously returned 0 with NO logging at
    //     all, so a caller staring at a skipped book review could not tell WHICH of the three reasons (null
    //     block / Enabled=false / PerType exclusion) closed it. It now evaluates the shared
    //     AnalysisRepairGate predicate and logs a Debug line naming "BookReview" + the reason before
    //     returning. Debug-only: BookReview is routinely gated out on PerType allowlists that don't include
    //     it, so this must never rise to INFO/WARN.
    //
    //     be-c02 NOTE: BuildBookReviewAsync now evaluates the SAME Enabled/PerType gate ONCE for the whole
    //     repair layer and short-circuits ahead of this method, so on the ENGINE path this internal gate is
    //     belt-and-braces that never fires. It is deliberately KEPT (defence-in-depth for any other caller)
    //     and these four tests, which drive ApplyGlossaryToFindings DIRECTLY, are what still cover it — the
    //     engine-path equivalent is BookReviewServiceTests' RepairLayerGate_* group. ───

    /// <summary>Minimal in-memory <see cref="ILogger"/> (ApplyGlossaryToFindings takes a plain, non-generic
    /// <see cref="ILogger"/>?) that records every entry's level + rendered message.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IReadOnlyList<(LogLevel Level, string Message)> SkipLogEntries(CapturingLogger logger) =>
        logger.Entries.Where(e => e.Message.Contains("gate closed", StringComparison.Ordinal)).ToList();

    [Fact]
    public void PerTypeExcludesBookReview_LogsDebugWithTypeAndReason()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var cfg = new AnalysisRepairOptions
        {
            Enabled = true,
            PerType = new Dictionary<string, bool> { ["LiteraryAnalysis"] = true } // excludes BookReview
        };
        var logger = new CapturingLogger();

        BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", cfg, logger);

        var entry = Assert.Single(SkipLogEntries(logger));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("BookReview", entry.Message, StringComparison.Ordinal);
        Assert.Contains("PerTypeExcluded", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LayerDisabled_LogsDebugWithDisabledReason()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var cfg = EnabledCfg();
        cfg.Enabled = false;
        var logger = new CapturingLogger();

        BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", cfg, logger);

        var entry = Assert.Single(SkipLogEntries(logger));
        Assert.Contains("BookReview", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Disabled", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConfig_LogsDebugWithNullConfigReason()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var logger = new CapturingLogger();

        BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", cfg: null, logger);

        var entry = Assert.Single(SkipLogEntries(logger));
        Assert.Contains("BookReview", entry.Message, StringComparison.Ordinal);
        Assert.Contains("NullConfig", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedType_LogsNoSkipLine()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var logger = new CapturingLogger();

        BookReviewService.ApplyGlossaryToFindings(new[] { finding }, "he", EnabledCfg(), logger);

        Assert.Empty(SkipLogEntries(logger));
    }

    // ─── RepairableFields.For(BookFinding) overload: scope of exposed accessors ───

    [Fact]
    public void RepairableFields_ForBookFinding_ExposesRationaleOnly_WhenSuggestedActionNull()
    {
        var finding = SampleFinding("rationale", suggestedAction: null);
        var fields = RepairableFields.For(finding);
        Assert.Single(fields); // only Rationale
    }

    [Fact]
    public void RepairableFields_ForBookFinding_ExposesRationaleAndSuggestedAction_WhenPresent()
    {
        var finding = SampleFinding("rationale", "action");
        var fields = RepairableFields.For(finding);
        Assert.Equal(2, fields.Count); // Rationale + SuggestedAction
    }

    // ─── GlossaryRepairPass.RepairFields: shared entry point Hebrew gate + change count ───

    [Fact]
    public void RepairFields_NonHebrewLanguage_ReturnsZero_NoChange()
    {
        var finding = SampleFinding("תיאור פעולה (Action) עז.", suggestedAction: null);
        var changed = GlossaryRepairPass.RepairFields(RepairableFields.For(finding), "en");
        Assert.Equal(0, changed);
        Assert.Contains("Action", finding.Rationale);
    }

    /// <summary>An <see cref="IReadOnlyList{T}"/> that reports a positive Count (so the gate is reached) but
    /// THROWS on enumeration — the deterministic fault used to prove the outer catch never lets an exception
    /// escape <see cref="BookReviewService.ApplyGlossaryToFindings"/>.</summary>
    private sealed class ThrowingFindingList : IReadOnlyList<BookFinding>
    {
        public int Count => 1;
        public BookFinding this[int index] => throw new InvalidOperationException("boom (indexer)");
        public IEnumerator<BookFinding> GetEnumerator() => throw new InvalidOperationException("boom (enumerator)");
        IEnumerator IEnumerable.GetEnumerator() => throw new InvalidOperationException("boom (enumerator)");
    }
}
