using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// DynamicTermRepairService — the SPAN-SCOPED LLM stage of the dynamic
// detect-and-repair layer (dynamic-term-repair-design plan, todo d3).
//
//   [d1] LatinInHebrewContentDetector.DetectForeignRuns  -> foreign-script runs
//         |                                                  (text + Start + Length)
//   [d2] ForeignRunClassifier.RunsToRepair               -> the REPAIR subset
//         |                                                  (proper nouns / acronyms / urls -> LEAVE)
//   [d3] DynamicTermRepairService                        -> one span-scoped IAiRouter
//                                                            call per REPAIR run (THIS file)
//
// WHY SPAN-SCOPE (the whole point): the value/whole-JSON-scoped AnalysisRepair LLM Stage-2 was turned OFF
// because it could Hebraize JSON keys and restructure the payload. Here every model call is handed the
// value with exactly ONE foreign run MARKED («…») and asked to return only a replacement token. The call
// is structurally unable to touch keys/enums/anchors — the tiny blast radius is what makes an LLM repair
// safe (the Stage-2 lesson was "wrong granularity", not "no LLM").
//
// FAIL-SAFE by construction (the load-bearing property):
//   • No REPAIR runs => ZERO model calls, the value is returned byte-identical.
//   • Each replacement is VALIDATED before it is spliced in: it must be non-empty and contain NO run of the
//     FOREIGN script (relative to the book's expected script). That single check rejects BOTH the
//     proper-noun "return it unchanged" case (the model echoes the foreign token) AND any junk still in the
//     foreign script — on rejection the ORIGINAL span is kept.
//   • A malformed / missing / null model payload yields NO replacement => the original span is kept.
//   • A whole-value backstop: the candidate is never allowed to have MORE foreign runs than the input; if it
//     somehow does, the WHOLE value reverts to the original.
//   • A router error / timeout / any exception is caught and logged; the affected span (or, at the outer
//     level, the whole value) keeps the ORIGINAL text. No method here EVER throws to the caller.
//
// SCOPE: this service repairs ONE prose value's foreign runs at a time. Structure is held by the caller
// (the RepairableFields whitelist + re-serialization), never by this pass — the RepairFieldsAsync
// convenience only ever reads/writes through RepairableField.Get/Set, so every non-prose (key / enum /
// numeric / anchor) field stays byte-identical.
//
// ROUTING: the AiRequest is tagged TaskType=AiTaskType.TermRepair, which the router resolves to
// Ai:FeatureModels:TermRepair and sends the marked-span instruction VERBATIM
// (AiRouter.ShouldUseUnifiedInstructionVerbatim includes TermRepair).
//
// [d4] ApplyAsync / RepairFindingsAsync (below) are the per-type / per-entity dispatch that wires this
//      service into UnifiedAnalysisService.ApplyAnalysisRepairAsync and the BookReview engine hook
//      (BookReviewService), gated by the NEW Ai:AnalysisRepair.Mode knob (AnalysisRepairMode). The SHIPPED
//      default (Mode=Glossary) never reaches this service — Dynamic/GlossaryThenDynamic are opt-in.
//
// OBSERVABILITY (fail-safe-swallow lesson): a non-deterministic repairer needs auditing. Every span logs
// (Debug) its offset/length, accepted/reverted, model+provider, and latency; every fault is LOGGED (never
// silently swallowed) AND surfaced through the returned result's Fault. Following the AnalysisRepairService
// convention, NO field value / run text / model output is ever logged (offsets + booleans + latency only).
// ---------------------------------------------------------------------------

/// <summary>
/// Outcome of repairing ONE prose value via <see cref="DynamicTermRepairService"/>. <see cref="Value"/> is
/// the value to assign back (the repaired value, or — on any doubt — the original, byte-identical). The
/// counters are observability-only: <see cref="Flagged"/> = REPAIR runs handed to the model,
/// <see cref="Repaired"/> = spans whose replacement was accepted and applied, <see cref="Reverted"/> = spans
/// whose replacement was rejected / the call errored (original kept). <see cref="LatencyMs"/> is the summed
/// model latency. <see cref="Fault"/> is the LAST exception the pass caught and swallowed (null on every
/// clean/no-op path); because the pass never throws, it is the signal a caller inspects to log a fault.
/// </summary>
public readonly record struct TermRepairValueResult
{
    public string Value { get; init; }
    public int Flagged { get; init; }
    public int Repaired { get; init; }
    public int Reverted { get; init; }
    public long LatencyMs { get; init; }
    public Exception? Fault { get; init; }
}

/// <summary>
/// Aggregate outcome of <see cref="DynamicTermRepairService.RepairFieldsAsync"/> over a
/// <see cref="RepairableField"/> list: how many prose fields were scanned / changed and the summed per-run
/// counters, plus the last swallowed <see cref="Fault"/>. Structural fields are never counted — only
/// whitelisted prose values are read/written.
/// </summary>
public readonly record struct TermRepairResult
{
    public int FieldsScanned { get; init; }
    public int FieldsChanged { get; init; }
    public int RunsFlagged { get; init; }
    public int RunsRepaired { get; init; }
    public int RunsReverted { get; init; }
    public long LatencyMs { get; init; }
    public Exception? Fault { get; init; }
}

/// <summary>
/// Span-scoped, detector-gated, FAIL-SAFE dynamic repair of leaked foreign terms in analysis prose via the
/// <see cref="AiTaskType.TermRepair"/> model. See the header comment for the fail-safe contract. Given a
/// prose value + its REPAIR-classified runs (from <see cref="ForeignRunClassifier.RunsToRepair"/>) + the
/// book's <see cref="ExpectedScript"/>, it makes one marked-span model call per run, validates the returned
/// replacement, and substitutes it back by offset. Clean prose (no REPAIR runs) makes ZERO model calls.
/// </summary>
public class DynamicTermRepairService
{
    private readonly IAiRouter _router;
    private readonly ILogger<DynamicTermRepairService> _logger;

    /// <summary>Delimiters that MARK the single foreign run inside the value sent to the model. Guillemets
    /// are near-absent from analysis prose, so the model can locate the marked token unambiguously; the
    /// instruction references them explicitly.</summary>
    private const char MarkOpen = '«';
    private const char MarkClose = '»';

    /// <summary>Whole-sentence-echo guard bounds for <see cref="IsAcceptableReplacement"/>. A genuine
    /// single-term equivalent is at most a few words (e.g. claustrophobia -> "פחד ממקומות סגורים", 3 words /
    /// 18 chars for a 14-char run) and carries no interior sentence punctuation. Anything beyond these bounds is
    /// treated as a misbehaving model echoing a whole sentence / long paraphrase in the NATIVE script (which the
    /// foreign-run check cannot catch) and is REVERTED, so it is never spliced into the single run's offset and
    /// cannot duplicate prose. Length cap is REPLACEMENT-length &gt; runLength * factor + constant.</summary>
    private const int MaxReplacementWords = 5;
    private const int ReplacementLengthFactor = 3;
    private const int ReplacementLengthConstant = 12;

    /// <summary>
    /// Marked-span instruction for the Hebrew-native direction (repair a Latin run -> Hebrew). Sent VERBATIM
    /// under the Hebrew analysis frame. Asks the model to replace ONLY the «marked» token with its idiomatic
    /// Hebrew equivalent in context, to return a proper noun / brand / acronym / no-equivalent token
    /// UNCHANGED, and to emit ONLY the tiny {"replacement":"…"} JSON.
    /// </summary>
    private const string HebrewTargetInstruction =
        "לפניך משפט או קטע בעברית שבתוכו סומנה בדיוק מילה זרה אחת בין הסימנים «...». " +
        "החלף אך ורק את המילה המסומנת במונח העברי המקובל והאידיומטי המתאים להקשר. " +
        "אם המילה המסומנת היא שם פרטי, שם מותג, ראשי תיבות או מונח שאין לו מקבילה בעברית — החזר אותה ללא שינוי. " +
        "אל תשנה דבר מלבד המילה המסומנת ואל תוסיף הסבר. " +
        "החזר אך ורק JSON קצר בתבנית {\"replacement\":\"<המילה בעברית>\"}.";

    /// <summary>
    /// Marked-span instruction for the Latin-native direction (repair a Hebrew run -> the surrounding
    /// language). Generic over the specific Latin-script language (d4/d5 may specialise per-language). Same
    /// contract as <see cref="HebrewTargetInstruction"/>: replace ONLY the «marked» token, leave proper
    /// nouns / brands / no-equivalent tokens unchanged, and return ONLY the tiny JSON.
    /// </summary>
    private const string LatinTargetInstruction =
        "The text below contains exactly one foreign word marked between «...» guillemets. " +
        "Replace ONLY the marked word with its idiomatic equivalent in the surrounding language " +
        "(the language the rest of the sentence is written in), and change nothing else. " +
        "If the marked word is a proper noun, a brand, an acronym, or has no equivalent, return it UNCHANGED. " +
        "Do not add any explanation. Return ONLY a tiny JSON object of the form {\"replacement\":\"<word>\"}.";

    public DynamicTermRepairService(IAiRouter router, ILogger<DynamicTermRepairService> logger)
    {
        _router = router;
        _logger = logger;
    }

    /// <summary>
    /// Maps a book language to the script the prose is EXPECTED to be in: a "he*" language expects Hebrew
    /// (foreign = Latin); everything else expects Latin (foreign = Hebrew). This mirrors the Hebrew&lt;-&gt;Latin
    /// axis of the d1 detector; other scripts are out of scope.
    /// </summary>
    public static ExpectedScript ExpectedScriptForLanguage(string? language)
        => !string.IsNullOrWhiteSpace(language) && language.StartsWith("he", StringComparison.OrdinalIgnoreCase)
            ? ExpectedScript.Hebrew
            : ExpectedScript.Latin;

    /// <summary>
    /// Detect (d1) -> classify (d2) -> repair (d3) one prose value end to end. Runs the bidirectional
    /// detector for <paramref name="expected"/>, keeps only the REPAIR runs (proper nouns / acronyms / urls
    /// are left), and hands them to <see cref="RepairRunsAsync"/>. Clean prose (no REPAIR runs) makes ZERO
    /// model calls and returns the value byte-identical. NEVER throws.
    /// </summary>
    /// <param name="value">The prose value to repair. Null/empty/whitespace is returned unchanged (no call).</param>
    /// <param name="expected">The book's native script (Hebrew -> repair Latin; Latin -> repair Hebrew).</param>
    /// <param name="language">The BOOK language, forwarded to the router (forces target-language output).</param>
    /// <param name="bookEntities">Optional known character/place names the classifier always LEAVEs.</param>
    /// <param name="ct">Cancellation token forwarded to the router.</param>
    public async Task<TermRepairValueResult> RepairValueAsync(
        string? value,
        ExpectedScript expected,
        string language,
        IReadOnlySet<string>? bookEntities = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new TermRepairValueResult { Value = value ?? string.Empty };
        }

        try
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, expected);
            if (runs.Count == 0)
            {
                return new TermRepairValueResult { Value = value };
            }

            var repairRuns = ForeignRunClassifier.RunsToRepair(runs, value, expected, bookEntities);
            if (repairRuns.Count == 0)
            {
                // Every foreign run was legitimately foreign (proper noun / acronym / url) — no model call.
                return new TermRepairValueResult { Value = value };
            }

            return await RepairRunsAsync(value, repairRuns, expected, language, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Belt-and-braces: detection/classification is deterministic + pure, but the repair layer's
            // load-bearing invariant is "never throw to the caller". Keep the original value.
            _logger.LogWarning(ex, "TermRepair: RepairValueAsync failed; keeping original value (fail-safe).");
            return new TermRepairValueResult { Value = value, Fault = ex };
        }
    }

    /// <summary>
    /// Span-scoped repair of a value whose REPAIR runs are ALREADY classified (the direct d4 hook when the
    /// caller has run d1/d2 itself). Makes one marked-span model call per run, validates each returned
    /// replacement, and substitutes accepted replacements back by offset. NEVER throws: on any doubt the
    /// affected span (or, via the whole-value backstop, the entire value) keeps the ORIGINAL text.
    /// </summary>
    /// <param name="value">The prose value the runs were detected in.</param>
    /// <param name="repairRuns">The REPAIR-classified foreign runs (offsets into <paramref name="value"/>).</param>
    /// <param name="expected">The book's native script; validation rejects a replacement still in the foreign script.</param>
    /// <param name="language">The BOOK language, forwarded to the router.</param>
    /// <param name="ct">Cancellation token forwarded to the router.</param>
    public async Task<TermRepairValueResult> RepairRunsAsync(
        string value,
        IReadOnlyList<ForeignRun> repairRuns,
        ExpectedScript expected,
        string language,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(value) || repairRuns is null || repairRuns.Count == 0)
        {
            return new TermRepairValueResult { Value = value ?? string.Empty };
        }

        try
        {
            return await RepairRunsCoreAsync(value, repairRuns, expected, language, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail-safe outer wrap: any unforeseen fault keeps the ORIGINAL value entirely.
            _logger.LogWarning(ex, "TermRepair: RepairRunsAsync failed; keeping original value (fail-safe).");
            return new TermRepairValueResult
            {
                Value = value,
                Flagged = repairRuns.Count,
                Reverted = repairRuns.Count,
                Fault = ex
            };
        }
    }

    private async Task<TermRepairValueResult> RepairRunsCoreAsync(
        string value,
        IReadOnlyList<ForeignRun> repairRuns,
        ExpectedScript expected,
        string language,
        CancellationToken ct)
    {
        // Apply substitutions RIGHT-TO-LEFT (descending Start) so every splice leaves the offsets of the
        // not-yet-processed (leftward) runs valid. The marked prompt is always built from the ORIGINAL value
        // (its offsets never shift), so the two stay consistent.
        var ordered = new List<ForeignRun>(repairRuns);
        ordered.Sort((a, b) => b.Start.CompareTo(a.Start));

        var working = value;
        var repaired = 0;
        var reverted = 0;
        long latencyMs = 0;
        Exception? lastFault = null;

        foreach (var run in ordered)
        {
            // Defensive: a run whose offsets fall outside the value (stale) is skipped, original kept.
            if (run.Start < 0 || run.Length <= 0 || run.Start + run.Length > value.Length)
            {
                reverted++;
                continue;
            }

            var runText = value.Substring(run.Start, run.Length);
            var marked = value.Substring(0, run.Start) + MarkOpen + runText + MarkClose + value.Substring(run.Start + run.Length);

            var sw = Stopwatch.StartNew();
            string? replacement = null;
            string model = "?";
            string provider = "?";
            try
            {
                var request = new AiRequest
                {
                    InputText = marked,
                    Instruction = expected == ExpectedScript.Hebrew ? HebrewTargetInstruction : LatinTargetInstruction,
                    TaskType = AiTaskType.TermRepair,
                    Language = language,
                    SourceId = "term-repair",
                    JsonMode = true
                };

                var response = await _router.CompleteAsync(request, ct).ConfigureAwait(false);
                model = response.Model;
                provider = response.Provider;
                replacement = ExtractReplacement(response.Content);
            }
            catch (Exception ex)
            {
                // A per-span router error / timeout must NOT abort the other spans — keep this span's
                // original and carry on. Surface + log the fault (never swallow silently).
                sw.Stop();
                latencyMs += sw.ElapsedMilliseconds;
                reverted++;
                lastFault = ex;
                _logger.LogWarning(ex,
                    "TermRepair.span provider={Provider} model={Model} start={Start} length={Length} failed; keeping original span (fail-safe).",
                    provider, model, run.Start, run.Length);
                continue;
            }

            sw.Stop();
            latencyMs += sw.ElapsedMilliseconds;

            // VALIDATE: accept only a non-empty replacement that carries NO foreign-script run AND reads as a
            // single term (not a whole-sentence echo, bounded by the ORIGINAL run length). That rejects the
            // proper-noun echo (foreign token unchanged), junk still in the foreign script, AND a native-script
            // sentence echo/paraphrase — on rejection the ORIGINAL span is kept.
            if (replacement is null || !IsAcceptableReplacement(replacement, expected, run.Length))
            {
                reverted++;
                LogSpan(provider, model, run, accepted: false, sw.ElapsedMilliseconds);
                continue;
            }

            var repl = replacement.Trim();
            working = working.Substring(0, run.Start) + repl + working.Substring(run.Start + run.Length);
            repaired++;
            LogSpan(provider, model, run, accepted: true, sw.ElapsedMilliseconds);
        }

        // WHOLE-VALUE BACKSTOP: never return a value with MORE foreign runs than the input. Per-span
        // validation already guarantees each accepted replacement is native-script, so this can only fire on
        // an unforeseen edge — on which we revert the ENTIRE value to the original (fail-safe).
        var beforeCount = LatinInHebrewContentDetector.DetectForeignRuns(value, expected).Count;
        var afterCount = LatinInHebrewContentDetector.DetectForeignRuns(working, expected).Count;
        if (afterCount > beforeCount)
        {
            _logger.LogWarning(
                "TermRepair: candidate had more foreign runs ({After}) than the input ({Before}); reverting whole value (fail-safe).",
                afterCount, beforeCount);
            return new TermRepairValueResult
            {
                Value = value,
                Flagged = repairRuns.Count,
                Reverted = repairRuns.Count,
                LatencyMs = latencyMs,
                Fault = lastFault
            };
        }

        return new TermRepairValueResult
        {
            Value = working,
            Flagged = repairRuns.Count,
            Repaired = repaired,
            Reverted = reverted,
            LatencyMs = latencyMs,
            Fault = lastFault
        };
    }

    /// <summary>
    /// Detect -> classify -> repair over an ALREADY-BUILT list of repairable prose fields — the d4 hook for
    /// ApplyAnalysisRepairAsync (alongside GlossaryRepairPass) and the BookReview finalize->persist hook. For
    /// each field it reads the value, repairs it via <see cref="RepairValueAsync"/>, and writes the result
    /// back through <see cref="RepairableField.Set"/> ONLY when it actually changed — so every structural
    /// (non-prose) field stays byte-identical. Clean fields make ZERO model calls. NEVER throws.
    /// </summary>
    public async Task<TermRepairResult> RepairFieldsAsync(
        IReadOnlyList<RepairableField> fields,
        ExpectedScript expected,
        string language,
        IReadOnlySet<string>? bookEntities = null,
        CancellationToken ct = default)
    {
        if (fields is null || fields.Count == 0)
        {
            return new TermRepairResult();
        }

        var scanned = 0;
        var changed = 0;
        var flagged = 0;
        var repaired = 0;
        var reverted = 0;
        long latencyMs = 0;
        Exception? lastFault = null;

        foreach (var field in fields)
        {
            scanned++;
            string value;
            try
            {
                value = field.Get();
            }
            catch (Exception ex)
            {
                // A faulting getter must not abort the whole walk (fail-safe).
                lastFault = ex;
                _logger.LogWarning(ex, "TermRepair: a repairable-field getter threw; skipping the field (fail-safe).");
                continue;
            }

            var outcome = await RepairValueAsync(value, expected, language, bookEntities, ct).ConfigureAwait(false);
            flagged += outcome.Flagged;
            repaired += outcome.Repaired;
            reverted += outcome.Reverted;
            latencyMs += outcome.LatencyMs;
            if (outcome.Fault is not null) lastFault = outcome.Fault;

            if (!string.Equals(outcome.Value, value, StringComparison.Ordinal))
            {
                try
                {
                    field.Set(outcome.Value);
                    changed++;
                }
                catch (Exception ex)
                {
                    // A faulting setter leaves the field as-is; never throw out of the repair layer.
                    lastFault = ex;
                    _logger.LogWarning(ex, "TermRepair: a repairable-field setter threw; leaving the field unchanged (fail-safe).");
                }
            }
        }

        return new TermRepairResult
        {
            FieldsScanned = scanned,
            FieldsChanged = changed,
            RunsFlagged = flagged,
            RunsRepaired = repaired,
            RunsReverted = reverted,
            LatencyMs = latencyMs,
            Fault = lastFault
        };
    }

    // ─── d4: per-type dispatch (ApplyAnalysisRepairAsync + BookReview engine hook) ─────────────────────────
    //
    // MIRRORS GlossaryRepairPass.Apply's switch (same repair-target types, same Proofread/BookReview
    // exclusions) so the dynamic stage is a drop-in alternative/companion to the glossary at the SAME seam.
    // Unlike the glossary (Hebrew-only, English->Hebrew), the dynamic pass is BIDIRECTIONAL —
    // ExpectedScriptForLanguage picks the repair direction from the book language, so this dispatch has no
    // Hebrew-only gate of its own (the caller's Mode gating is what decides whether this ever runs at all).

    /// <summary>
    /// Detect -&gt; classify -&gt; span-repair dispatch for ONE analysis result, mirroring
    /// <see cref="GlossaryRepairPass.Apply"/>'s per-type switch exactly (same repairable types; Proofread and
    /// BookReview are left untouched here too — BookReview flows through <see cref="RepairFindingsAsync"/>
    /// instead). For Summarization the entire <paramref name="cleanContent"/> is the repairable prose
    /// (<see cref="RepairableFields.ForPlainText"/>); for the structured types <paramref name="structuredJson"/>
    /// is deserialized, walked via the type's <c>RepairableFields.For</c> overload, and — ONLY when at least
    /// one field changed — re-serialized with the SAME <paramref name="jsonOptions"/> the pipeline persists
    /// with. A null/blank structuredJson, an unparsable/null parse, or zero changed fields all return the
    /// inputs byte-identical (never re-serialize a clean/unchanged object).
    ///
    /// FAIL-SAFE: the whole dispatch is wrapped in one try/catch; on ANY exception (deserialize, accessor
    /// walk, reserialize) the ORIGINAL inputs are returned unchanged and the exception is surfaced as
    /// <c>fault</c> — this method NEVER throws.
    /// </summary>
    public async Task<(string? structuredJson, string cleanContent, int fieldsChanged, Exception? fault)> ApplyAsync(
        AnalysisType type,
        string? structuredJson,
        string cleanContent,
        string language,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        try
        {
            return type switch
            {
                // Summarization: the ENTIRE cleanContent is the repairable prose (mirrors GlossaryRepairPass).
                AnalysisType.Summarization =>
                    await ApplyPlainTextAsync(structuredJson, cleanContent, language, ct).ConfigureAwait(false),

                AnalysisType.LiteraryAnalysis =>
                    await ApplyStructuredAsync<LiteraryAnalysisResult>(structuredJson, cleanContent, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false),
                AnalysisType.LinguisticAnalysis =>
                    await ApplyStructuredAsync<LinguisticAnalysisResult>(structuredJson, cleanContent, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false),
                AnalysisType.LineEdit =>
                    await ApplyStructuredAsync<LineEditResult>(structuredJson, cleanContent, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false),

                AnalysisType.BookOverview =>
                    await ApplyStructuredAsync<BookOverviewResult>(structuredJson, cleanContent, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false),
                AnalysisType.CharacterAnalysis =>
                    await ApplyStructuredAsync<CharacterAnalysisResult>(structuredJson, cleanContent, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false),
                AnalysisType.StoryAnalysis =>
                    await ApplyStructuredAsync<StoryAnalysisResult>(structuredJson, cleanContent, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false),
                AnalysisType.QA =>
                    await ApplyStructuredAsync<QAResult>(structuredJson, cleanContent, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false),

                // Proofread and everything else (incl. BookReview, handled on its own entity path via
                // RepairFindingsAsync): not a dispatch target here. Return the inputs unchanged.
                _ => (structuredJson, cleanContent, 0, null),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TermRepair: ApplyAsync dispatch failed for type={Type}; keeping un-repaired inputs (fail-safe).",
                type);
            return (structuredJson, cleanContent, 0, ex);
        }
    }

    /// <summary>Summarization dispatch target: the whole ResultText is the repairable prose. Mirrors
    /// <see cref="GlossaryRepairPass"/>'s RepairPlainText via the SAME <see cref="RepairableFields.ForPlainText"/>
    /// write-back seam, but through <see cref="RepairFieldsAsync"/> (span-scoped model calls) instead of the
    /// closed glossary.</summary>
    private async Task<(string? structuredJson, string cleanContent, int fieldsChanged, Exception? fault)> ApplyPlainTextAsync(
        string? structuredJson,
        string cleanContent,
        string language,
        CancellationToken ct)
    {
        string? written = null;
        var field = RepairableFields.ForPlainText(cleanContent, v => written = v)[0];

        var result = await RepairFieldsAsync(
            new[] { field }, ExpectedScriptForLanguage(language), language, ct: ct).ConfigureAwait(false);

        if (result.FieldsChanged == 0)
        {
            return (structuredJson, cleanContent, 0, result.Fault);
        }

        // field.Set forwards to the local delegate above (`written`), mirroring GlossaryRepairPass.RepairPlainText.
        return (structuredJson, written ?? cleanContent, result.FieldsChanged, result.Fault);
    }

    /// <summary>
    /// Structured-result dispatch target: deserialize, walk the type's whitelisted <see cref="RepairableField"/>
    /// accessors via <see cref="RepairFieldsAsync"/>, and re-serialize with <paramref name="jsonOptions"/> ONLY
    /// when at least one field changed (mirrors <see cref="GlossaryRepairPass"/>'s RepairStructured&lt;T&gt;
    /// exactly, substituting the span-scoped dynamic repair for the closed glossary). A null/blank
    /// structuredJson, a JSON parse failure, or a null parsed object are byte-identical no-ops.
    /// </summary>
    private async Task<(string? structuredJson, string cleanContent, int fieldsChanged, Exception? fault)> ApplyStructuredAsync<T>(
        string? structuredJson,
        string cleanContent,
        string language,
        JsonSerializerOptions jsonOptions,
        Func<T, IReadOnlyList<RepairableField>> accessorsOf,
        CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(structuredJson))
        {
            return (structuredJson, cleanContent, 0, null);
        }

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(structuredJson, jsonOptions);
        }
        catch (JsonException)
        {
            return (structuredJson, cleanContent, 0, null); // fail-safe: unparsable => leave untouched
        }

        if (parsed is null)
        {
            return (structuredJson, cleanContent, 0, null);
        }

        var fields = accessorsOf(parsed);
        var result = await RepairFieldsAsync(
            fields, ExpectedScriptForLanguage(language), language, ct: ct).ConfigureAwait(false);

        if (result.FieldsChanged == 0)
        {
            // Nothing rewritten: return the input JSON byte-identical (never re-serialize an unchanged object).
            return (structuredJson, cleanContent, 0, result.Fault);
        }

        var newJson = JsonSerializer.Serialize(parsed, jsonOptions);
        return (newJson, cleanContent, result.FieldsChanged, result.Fault);
    }

    /// <summary>
    /// BookReview ENTITY path (d4 hook, mirrors <see cref="GlossaryRepairPass.RepairFields"/> /
    /// <c>BookReviewService.ApplyGlossaryToFindings</c>): repairs each finalised <see cref="BookFinding"/>'s
    /// Rationale + (non-null) SuggestedAction IN PLACE via <see cref="RepairableFields.For(BookFinding)"/> +
    /// <see cref="RepairFieldsAsync"/> — the SAME span-scoped dynamic repair the RunAsync/streaming dispatch
    /// above uses, applied to entity fields instead of a parsed DTO. Everything else on the entity (Dimension /
    /// Verdict / Severity / EvidenceJson / ChapterAnchorsJson / DedupKey / Status / BuiltWithModel) is never
    /// exposed, so it stays byte-identical.
    ///
    /// FAIL-SAFE exactly like <c>ApplyGlossaryToFindings</c>: a per-finding try/catch means a fault on ONE
    /// finding leaves THAT finding un-repaired and continues; an outer try/catch means the whole pass can never
    /// throw into the review build. Returns the count of findings whose prose changed.
    /// </summary>
    public async Task<int> RepairFindingsAsync(
        IReadOnlyList<BookFinding> findings,
        string language,
        CancellationToken ct = default)
    {
        if (findings is null || findings.Count == 0)
        {
            return 0;
        }

        var expected = ExpectedScriptForLanguage(language);
        var changedFindings = 0;

        try
        {
            foreach (var finding in findings)
            {
                if (finding is null) continue; // NULL-GUARD: never walk a null element.
                try
                {
                    var result = await RepairFieldsAsync(
                        RepairableFields.For(finding), expected, language, ct: ct).ConfigureAwait(false);
                    if (result.FieldsChanged > 0)
                        changedFindings++;

                    if (result.Fault is not null)
                    {
                        _logger.LogWarning(result.Fault,
                            "TermRepair: BookReview finding repair swallowed a fault (dimension={Dimension}); continuing (fail-safe).",
                            finding.Dimension);
                    }
                }
                catch (Exception ex)
                {
                    // FAIL-SAFE per finding: a fault on ONE finding must not abort the others.
                    _logger.LogWarning(ex,
                        "TermRepair: BookReview finding repair threw (dimension={Dimension}); keeping it un-repaired (fail-safe).",
                        finding.Dimension);
                }
            }
        }
        catch (Exception ex)
        {
            // Belt-and-braces: any fault OUTSIDE a single finding's body (e.g. a throwing enumerator) is
            // swallowed too, so the review build proceeds with whatever was repaired before the fault.
            _logger.LogWarning(ex,
                "TermRepair: BookReview findings repair pass faulted; returning {Count} finding(s) repaired so far (fail-safe).",
                changedFindings);
        }

        return changedFindings;
    }

    /// <summary>
    /// Accept a replacement token ONLY if ALL of the following hold; otherwise the caller keeps the ORIGINAL
    /// span (fail-safe). NEVER throws.
    /// <list type="number">
    /// <item>Non-empty after trim.</item>
    /// <item>Carries NO run of the FOREIGN script (relative to <paramref name="expected"/>). This linchpin
    /// rejects the proper-noun "return unchanged" echo (the model returns the still-foreign token) and any junk
    /// the model left in the foreign script. Uses the SAME detector as the guard so "foreign" means the same
    /// thing everywhere (an allowlisted brand is not counted as a run).</item>
    /// <item>Reads as a single TERM, not a whole-sentence echo. A misbehaving model may echo the marked
    /// sentence (or a long paraphrase) in the NATIVE script: it carries no foreign run, so it passes check (2),
    /// but spliced into the single run's offset it DUPLICATES the surrounding prose. It is rejected when it
    /// EITHER (a) carries a strong sentence terminator (<c>. ! ? … </c>/ newline; a single TRAILING period is
    /// tolerated because a model may append one to a lone term); OR (b) is implausibly long for one term —
    /// its word count exceeds <see cref="MaxReplacementWords"/>, or its length exceeds
    /// <paramref name="runLength"/> * <see cref="ReplacementLengthFactor"/> + <see cref="ReplacementLengthConstant"/>.
    /// These bounds clear every legitimate multi-word equivalent (e.g. a 14-char term ->
    /// "פחד ממקומות סגורים", 3 words / 18 chars) with margin, while a native-script sentence echo (6+ words,
    /// spanning the run's whole surrounding prose) trips at least one bound.</item>
    /// </list>
    /// </summary>
    private static bool IsAcceptableReplacement(string replacement, ExpectedScript expected, int runLength)
    {
        var trimmed = replacement.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        // (2) A replacement still containing the foreign script (echoed proper noun, or junk) is rejected.
        if (LatinInHebrewContentDetector.HasForeignRuns(trimmed, expected))
        {
            return false;
        }

        // (3a) Sentence punctuation. A single TRAILING period is tolerated (a model may append one to a lone
        //      term); any interior period, or any '!' '?' '…' / newline anywhere, signals a full/multi sentence.
        var probe = trimmed.Length > 1 && trimmed[trimmed.Length - 1] == '.'
            ? trimmed.Substring(0, trimmed.Length - 1)
            : trimmed;
        foreach (var ch in probe)
        {
            if (ch is '.' or '!' or '?' or '…' or '\n' or '\r')
            {
                return false;
            }
        }

        // (3b) Word count. A genuine term equivalent is at most a few words (claustrophobia ->
        //      "פחד ממקומות סגורים", 3 words); a sentence echo has many more.
        var wordCount = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > MaxReplacementWords)
        {
            return false;
        }

        // (3b) Length relative to the ORIGINAL run. Generous headroom for a legit multi-word equivalent yet
        //      well below a whole-sentence echo (which spans the run's entire surrounding prose).
        if (trimmed.Length > (runLength * ReplacementLengthFactor) + ReplacementLengthConstant)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// DEFENSIVE parse of a TermRepair model payload: sanitizes (strips any &lt;think&gt; block / watermark /
    /// control noise), extracts the first {...} object, and reads a string "replacement" property. Returns
    /// null (=> keep original span) for a null/blank payload, no JSON object, malformed JSON, a missing /
    /// non-string / null "replacement", or a whitespace value. NEVER throws.
    /// </summary>
    private static string? ExtractReplacement(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var sanitized = UnifiedAnalysisService.SanitizeResponse(raw);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return null;
        }

        // Locate the JSON object with the shared extractor (balanced-brace matching + fence /
        // BOM / bidi stripping), same as BookReviewService / ChapterBriefService / BookIntelligenceService.
        var slice = UnifiedAnalysisService.ExtractJson(sanitized);
        if (string.IsNullOrWhiteSpace(slice))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(slice);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("replacement", out var repl) ||
                repl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = repl.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null; // malformed JSON => no replacement => keep original span
        }
    }

    /// <summary>
    /// Per-span Debug audit line for the dynamic repair. Logs ONLY the provider/model, the run's OFFSET and
    /// LENGTH, the accept/revert outcome, and latency — NO run text / replacement / value is ever logged
    /// (no content/PII in logs; mirrors AnalysisRepairService.LogRepairedField). Debug because per-span is
    /// high-volume; the d4 wiring emits a per-run aggregate at INFO.
    /// </summary>
    private void LogSpan(string provider, string model, ForeignRun run, bool accepted, long latencyMs)
    {
        _logger.LogDebug(
            "TermRepair.span provider={Provider} model={Model} start={Start} length={Length} accepted={Accepted} reverted={Reverted} latencyMs={LatencyMs}",
            provider, model, run.Start, run.Length, accepted, !accepted, latencyMs);
    }
}
