using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// AnalysisRepairService — the value-scoped LLM stage of the analysis-output
// repair layer (the deterministic first stage is GlossaryRepairPass).
//
// Plan: src/.cursor/plans/_todo/analysis-output-repair-2026-07-03.plan.md
//       (todo p3-repair-service). Promotes the value-only Hebrew repair pass
//       proven in OutputQualityDiagnostic.RepairAsync (diagnostic Part 2:
//       gemma4:12b removed 100% of the seeded English leaks value-only while
//       preserving meaning/structure; Dicta over-rewrote) into a production
//       service.
//
// FAIL-SAFE by construction (the load-bearing property):
//   • A null/empty/whitespace field is returned unchanged (no model call).
//   • The model output is VALIDATED before it is accepted; on ANY failure the
//     ORIGINAL value is returned. The worst case is "left the field as-is",
//     never "made it worse".
//   • A router error / timeout / any exception is caught and logged, and the
//     ORIGINAL value is returned. RepairFieldAsync NEVER throws to the caller.
//
// SCOPE: this service repairs ONE prose field's VALUE at a time — structure is
// held by the caller (RepairableFields whitelist + re-serialization), never by
// this pass. It is stateless aside from the injected router + logger.
//
// ROUTING: the AiRequest is tagged TaskType=AiTaskType.AnalysisRepair, which the
// router resolves to Ai:FeatureModels:AnalysisRepair (= Ollama/gemma4:12b) and
// sends the repair instruction VERBATIM (AiRouter.ShouldUseUnifiedInstructionVerbatim
// includes AnalysisRepair). NOT wired into RunAsync yet — that is p3-wire.
// Observability (p6-observability): per-field Debug logging + a one-line aggregate are wired here and
// surfaced through AnalysisRepairResult; NO field value / model output is ever logged (counts/booleans/
// latency/type/index only).
// ---------------------------------------------------------------------------

/// <summary>
/// Result of <see cref="AnalysisRepairService.RepairAnalysisAsync"/>: the (possibly repaired)
/// <see cref="StructuredJson"/> / <see cref="CleanContent"/> to assign back, plus the LLM-pass
/// observability counters the caller uses to emit the per-run aggregate log line (p6-observability):
///   • <see cref="LlmFlagged"/> (N) — repairable fields the guard flagged and sent to the model;
///   • <see cref="LlmRepaired"/> (M) — of those, the ones whose repair was ACCEPTED and changed the value;
///   • <see cref="LlmFailSafe"/> (K) — of those, the ones the validator rejected / the call errored (original kept).
/// All three counters are 0 on every no-op path (non-Hebrew book, non-target type, unparsable payload,
/// clean output) and whenever the LLM stage is skipped (GuardOnly). A 2-arg <see cref="Deconstruct"/> keeps
/// the historical <c>(structuredJson, cleanContent)</c> destructuring working unchanged at every call site.
/// </summary>
public readonly record struct AnalysisRepairResult
{
    public string? StructuredJson { get; init; }
    public string CleanContent { get; init; }
    public int LlmFlagged { get; init; }
    public int LlmRepaired { get; init; }
    public int LlmFailSafe { get; init; }

    /// <summary>Back-compat projection to the original <c>(structuredJson, cleanContent)</c> shape; the
    /// counters are observability-only, so callers that just want the values are unaffected.</summary>
    public void Deconstruct(out string? structuredJson, out string cleanContent)
    {
        structuredJson = StructuredJson;
        cleanContent = CleanContent;
    }
}

/// <summary>
/// Value-scoped, guard-gated, FAIL-SAFE Hebrew cleanup of a single analysis prose field via the
/// repair model (gemma4:12b). See the header comment for the fail-safe contract. The caller supplies
/// only the field VALUE (structure is held by <see cref="RepairableFields"/> + re-serialization) and
/// gets back either the accepted repair or — on any validation failure or error — the original value.
/// </summary>
public class AnalysisRepairService
{
    private readonly IAiRouter _router;
    private readonly ILogger<AnalysisRepairService> _logger;

    /// <summary>Lower/upper bound on repaired.Length / original.Length. A repair that shrinks or grows
    /// the field beyond this band is rejected as fail-safe (the model likely dropped or padded content).
    /// [0.6, 1.6] matches the RepairQuality gold scorer's length-ratio bound (p0-repair-gold).</summary>
    private const double MinLengthRatio = 0.6;
    private const double MaxLengthRatio = 1.6;

    /// <summary>
    /// The value-only Hebrew repair instruction, promoted VERBATIM from
    /// OutputQualityDiagnostic.RepairAsync (the proven prototype). It asks the model to (1) replace any
    /// non-Hebrew term with its accepted Hebrew literary equivalent (inline glossary examples), (2) fix
    /// spelling/grammar/fluency, (3) preserve meaning, insights and structure exactly, and return ONLY the
    /// corrected Hebrew text. Sent verbatim under the Hebrew analysis system frame (see the routing note).
    /// </summary>
    private const string RepairInstruction =
        "אתה עורך לשוני מקצועי. לפניך טקסט ניתוח ספרותי בעברית שהופק על ידי מודל שפה ועלול להכיל " +
        "מילים או מונחים באנגלית, שגיאות כתיב או ניסוח לא תקין. משימתך: " +
        "1) החלף כל מילה או מונח שאינם בעברית במונח העברי הנכון והמקובל בשדה הספרות " +
        "(לדוגמה: narrator->מספר, tone->טון, foreshadowing->רמיזה מקדימה, imagery->דימויים, " +
        "mood->מצב רוח, tension->מתח, climax->שיא). " +
        "2) תקן שגיאות כתיב, דקדוק ותחביר ושפר את זרימת העברית. " +
        "3) שמור בדיוק על המשמעות, על התובנות ועל המבנה של הטקסט. אל תוסיף ואל תסיר תוכן או תובנות. " +
        "החזר אך ורק את הטקסט המתוקן בעברית, בלי הקדמה ובלי הסברים.";

    public AnalysisRepairService(IAiRouter router, ILogger<AnalysisRepairService> logger)
    {
        _router = router;
        _logger = logger;
    }

    /// <summary>
    /// Attempts a fail-safe Hebrew repair of a single analysis prose field's VALUE. Returns the accepted
    /// repaired value, or — on a null/empty input, a validation failure, or any error — the ORIGINAL
    /// <paramref name="value"/> unchanged. NEVER throws.
    /// </summary>
    /// <param name="value">The prose field value to repair. Null/empty/whitespace is returned unchanged.</param>
    /// <param name="language">The BOOK language (e.g. "he-IL"), passed through to the router.</param>
    /// <param name="ct">Cancellation token forwarded to the router.</param>
    public async Task<string> RepairFieldAsync(string value, string language, CancellationToken ct = default)
    {
        var outcome = await RepairFieldCoreAsync(value, language, ct).ConfigureAwait(false);
        return outcome.Value;
    }

    /// <summary>Per-field outcome of one LLM repair attempt — carries only the value to write back plus
    /// observability flags (never any content beyond the kept value). <see cref="LlmRan"/> is false only on
    /// the empty-input short-circuit; <see cref="Accepted"/> is true only when the validator accepted the
    /// model output; <see cref="LatencyMs"/> is the Stopwatch around the model call.</summary>
    private readonly record struct FieldRepairOutcome(string Value, bool LlmRan, bool Accepted, long LatencyMs);

    /// <summary>
    /// Core of a single field's fail-safe repair: the model call + validation, timed with a Stopwatch, returning
    /// the value to KEEP plus observability flags so the caller can emit a per-field Debug line and the aggregate
    /// counters WITHOUT logging any content. Same fail-safe contract as <see cref="RepairFieldAsync"/>: NEVER
    /// throws; keeps the original value on validation failure or any error.
    /// </summary>
    private async Task<FieldRepairOutcome> RepairFieldCoreAsync(string value, string language, CancellationToken ct)
    {
        // 1) Nothing to repair — no model call (LlmRan=false, zero latency).
        if (string.IsNullOrWhiteSpace(value))
        {
            return new FieldRepairOutcome(value, LlmRan: false, Accepted: false, LatencyMs: 0);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            // 2) Call the repair model via the router (routes to gemma4:12b, instruction sent verbatim).
            var request = new AiRequest
            {
                InputText = value,
                Instruction = RepairInstruction,
                TaskType = AiTaskType.AnalysisRepair,
                Language = language,
                SourceId = "repair",
                JsonMode = false
            };

            var response = await _router.CompleteAsync(request, ct).ConfigureAwait(false);
            var repaired = UnifiedAnalysisService.SanitizeResponse(response.Content ?? string.Empty);

            // 3) Validate before accepting; on ANY failure keep the original (fail-safe).
            if (IsAcceptableRepair(value, repaired))
            {
                sw.Stop();
                return new FieldRepairOutcome(repaired.Trim(), LlmRan: true, Accepted: true, sw.ElapsedMilliseconds);
            }

            sw.Stop();
            _logger.LogDebug(
                "AnalysisRepair: model output rejected by validation; keeping original value (fail-safe).");
            return new FieldRepairOutcome(value, LlmRan: true, Accepted: false, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // 4) Router error / timeout / cancellation / anything else: never propagate — keep original.
            sw.Stop();
            _logger.LogWarning(ex, "AnalysisRepair: repair call failed; keeping original value (fail-safe).");
            return new FieldRepairOutcome(value, LlmRan: true, Accepted: false, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Emits the per-field Debug line for the LLM repair path. Logs ONLY the analysis type, the accessor INDEX
    /// (a stable field identifier — never the field name value), the Latin-run counts before/after, and the
    /// run/accept/fail-safe/latency observability flags. NO field value or model output is ever logged (no PII/
    /// content in logs). Debug because per-field is high-volume.
    /// </summary>
    private void LogRepairedField(AnalysisType type, int fieldIndex, int latinBefore, int latinAfter, FieldRepairOutcome outcome)
    {
        _logger.LogDebug(
            "AnalysisRepair.field type={Type} field={FieldIndex} latinBefore={LatinBefore} latinAfter={LatinAfter} llmRan={LlmRan} accepted={Accepted} failSafe={FailSafe} latencyMs={LatencyMs}",
            type, fieldIndex, latinBefore, latinAfter, outcome.LlmRan, outcome.Accepted, !outcome.Accepted, outcome.LatencyMs);
    }

    /// <summary>
    /// Per-object orchestration of the value-scoped repair over ONE analysis result (p3-wire). Mirrors
    /// <see cref="GlossaryRepairPass.Apply"/>'s type dispatch and fail-safe contract but replaces the
    /// deterministic glossary substitution with the LLM <see cref="RepairFieldAsync"/> call — and ONLY for a
    /// field the guard still flags, so a clean output makes ZERO model calls. Structure is held by code:
    /// each repairable prose VALUE is read/written through the <see cref="RepairableFields"/> whitelist and
    /// the object is re-serialised with the pipeline's camelCase <paramref name="jsonOptions"/> (never a raw
    /// key rewrite). Kept HERE (not in UnifiedAnalysisService) so it is unit-testable with a fake IAiRouter.
    ///
    /// Handles ONLY the repair-target types reachable at the analysis seams: Summarization (whole
    /// <paramref name="cleanContent"/>), LiteraryAnalysis, LinguisticAnalysis, LineEdit, BookOverview,
    /// CharacterAnalysis, StoryAnalysis, QA. That is EIGHT arms, one fewer than the nine each deterministic switch
    /// carries. <b>Synopsis is allowlisted and wired in both deterministic switches but is DELIBERATELY NOT an
    /// arm here</b> (be-c01): this stage has no ForeignRunClassifier rule-(7b) gate, which is the property q2
    /// measured and the reason Synopsis shipped. The full argument and the reversal condition are stated at the
    /// <c>default:</c> arm. Proofread, Synopsis and every other type — plus a non-Hebrew book, an
    /// unparsable payload, or any exception — return the inputs UNCHANGED (fail-safe; never throws). For
    /// LineEdit the caller (UnifiedAnalysisService) refreshes ResultText from the repaired overallFeedback;
    /// this method only repairs the structured value.
    /// </summary>
    /// <param name="type">Analysis type; only Summarization / LiteraryAnalysis / LinguisticAnalysis / LineEdit / BookOverview / CharacterAnalysis / StoryAnalysis / QA are repaired (Synopsis is a NAMED exclusion here; see the <c>default:</c> arm).</param>
    /// <param name="structuredJson">Parsed-and-reserialised StructuredResult (null for Summarization / non-structured types).</param>
    /// <param name="cleanContent">Prose ResultText (the whole repairable text for Summarization).</param>
    /// <param name="language">BOOK language; the pass fires only when this starts with "he".</param>
    /// <param name="jsonOptions">The pipeline's camelCase JsonOpts, so re-serialisation matches persistence exactly.</param>
    /// <param name="ct">Cancellation token forwarded to the router.</param>
    public async Task<AnalysisRepairResult> RepairAnalysisAsync(
        AnalysisType type,
        string? structuredJson,
        string cleanContent,
        string language,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct = default)
    {
        // Hebrew-gate: the repair model targets Hebrew literary prose (same convention as GlossaryRepairPass).
        // On a non-Hebrew book it is a strict no-op (all counters 0).
        if (string.IsNullOrWhiteSpace(language) ||
            !language.StartsWith("he", StringComparison.OrdinalIgnoreCase))
        {
            return new AnalysisRepairResult { StructuredJson = structuredJson, CleanContent = cleanContent };
        }

        try
        {
            switch (type)
            {
                // Summarization: the ENTIRE cleanContent is the repairable prose (via ForPlainText).
                case AnalysisType.Summarization:
                {
                    var (text, flagged, repaired, failSafe) =
                        await RepairPlainTextAsync(type, cleanContent, language, ct).ConfigureAwait(false);
                    return new AnalysisRepairResult
                    {
                        StructuredJson = structuredJson,
                        CleanContent = text,
                        LlmFlagged = flagged,
                        LlmRepaired = repaired,
                        LlmFailSafe = failSafe
                    };
                }

                case AnalysisType.LiteraryAnalysis:
                {
                    var (json, flagged, repaired, failSafe) =
                        await RepairStructuredAsync<LiteraryAnalysisResult>(
                            type, structuredJson, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false);
                    return new AnalysisRepairResult
                    {
                        StructuredJson = json,
                        CleanContent = cleanContent,
                        LlmFlagged = flagged,
                        LlmRepaired = repaired,
                        LlmFailSafe = failSafe
                    };
                }

                case AnalysisType.LinguisticAnalysis:
                {
                    var (json, flagged, repaired, failSafe) =
                        await RepairStructuredAsync<LinguisticAnalysisResult>(
                            type, structuredJson, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false);
                    return new AnalysisRepairResult
                    {
                        StructuredJson = json,
                        CleanContent = cleanContent,
                        LlmFlagged = flagged,
                        LlmRepaired = repaired,
                        LlmFailSafe = failSafe
                    };
                }

                case AnalysisType.LineEdit:
                {
                    var (json, flagged, repaired, failSafe) =
                        await RepairStructuredAsync<LineEditResult>(
                            type, structuredJson, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false);
                    return new AnalysisRepairResult
                    {
                        StructuredJson = json,
                        CleanContent = cleanContent,
                        LlmFlagged = flagged,
                        LlmRepaired = repaired,
                        LlmFailSafe = failSafe
                    };
                }

                // Book-level structured-Hebrew-prose analyses (f5-wire): same seam + fail-safe contract as
                // LiteraryAnalysis. GuardOnly is the shipped default, so this Stage-2 path is off by default;
                // it is wired for symmetry with the Stage-1 glossary pass. QA reaches here with a parsed QAResult.
                case AnalysisType.BookOverview:
                {
                    var (json, flagged, repaired, failSafe) =
                        await RepairStructuredAsync<BookOverviewResult>(
                            type, structuredJson, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false);
                    return new AnalysisRepairResult
                    {
                        StructuredJson = json,
                        CleanContent = cleanContent,
                        LlmFlagged = flagged,
                        LlmRepaired = repaired,
                        LlmFailSafe = failSafe
                    };
                }

                case AnalysisType.CharacterAnalysis:
                {
                    var (json, flagged, repaired, failSafe) =
                        await RepairStructuredAsync<CharacterAnalysisResult>(
                            type, structuredJson, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false);
                    return new AnalysisRepairResult
                    {
                        StructuredJson = json,
                        CleanContent = cleanContent,
                        LlmFlagged = flagged,
                        LlmRepaired = repaired,
                        LlmFailSafe = failSafe
                    };
                }

                case AnalysisType.StoryAnalysis:
                {
                    var (json, flagged, repaired, failSafe) =
                        await RepairStructuredAsync<StoryAnalysisResult>(
                            type, structuredJson, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false);
                    return new AnalysisRepairResult
                    {
                        StructuredJson = json,
                        CleanContent = cleanContent,
                        LlmFlagged = flagged,
                        LlmRepaired = repaired,
                        LlmFailSafe = failSafe
                    };
                }

                case AnalysisType.QA:
                {
                    var (json, flagged, repaired, failSafe) =
                        await RepairStructuredAsync<QAResult>(
                            type, structuredJson, language, jsonOptions, RepairableFields.For, ct).ConfigureAwait(false);
                    return new AnalysisRepairResult
                    {
                        StructuredJson = json,
                        CleanContent = cleanContent,
                        LlmFlagged = flagged,
                        LlmRepaired = repaired,
                        LlmFailSafe = failSafe
                    };
                }

                // ── SYNOPSIS: allowlisted, wired in the other two switches, DELIBERATELY NOT here ──────────
                // (be-c01, 2026-07-28.) This is a NAMED exclusion, not a gap. `f2` enabled Synopsis
                // (PerType=true in both files) and added a PLAIN-TEXT arm to BOTH DETERMINISTIC dispatch
                // switches - GlossaryRepairPass.Apply and DynamicTermRepairService.ApplyAsync - so under the
                // shipped Enabled=true / GuardOnly=true posture Synopsis is fully repaired. It reaches THIS
                // switch only in the opt-in state (3) (GuardOnly=false), and there it stays out, because:
                //
                //   (1) THIS STAGE CANNOT HONOUR RULE (7b), AND RULE (7b) IS WHY SYNOPSIS SHIPPED. q1 HALTED
                //       Synopsis at 83% preservation (5/6) with ONE false positive: the repair model
                //       TRANSLITERATED "Chekhov" -> "צ'כוב" at a paragraph head. What cleared the §18.2 bar in
                //       q2 (preservation 100% (6/6), 0 FP, over-rewrite 0) was NOT a better model - it was
                //       ForeignRunClassifier rule (7b) making that position a deterministic LEAVE, so 0 of the
                //       6 legitimate values reach a model at all. Rule (7b) lives in ForeignRunClassifier and
                //       is consulted ONLY by DynamicTermRepairService. This service never touches the
                //       classifier: its sole gate is LatinInHebrewContentDetector.HasNonAllowlistedLatin, and
                //       past it the WHOLE value goes to the model under RepairInstruction, which explicitly
                //       orders "replace every non-Hebrew term with the accepted Hebrew equivalent".
                //   (2) IsAcceptableRepair WOULD NOT CATCH IT. It rejects a NEW Latin run, non-Hebrew output,
                //       and a length ratio outside [0.6, 1.6]. Transliterating a proper noun REMOVES a Latin
                //       run and barely moves the length, so q1's exact false positive passes every guard.
                //   (3) THE ONLY MEASUREMENT OF A REPAIR MODEL REWRITING SYNOPSIS PROSE IS q1's HALT. Adding an
                //       arm here would put the failing configuration back one config flip away, with no
                //       measurement covering it. (The p3-gate GUARD-ONLY decision's over-rewrite finding on
                //       mixed leak+prose fields supports this, but is not the reason - it does not distinguish
                //       Synopsis from Summarization. The missing rule-(7b) gate does.)
                //   (4) PRECEDENT: "Repaired, but deliberately outside the value-scoped stage" already exists -
                //       BookReview's engine hook ignores GuardOnly entirely by design
                //       (docs/ANALYSIS_OUTPUT_REPAIR.md §4.2's `GuardOnly` asymmetry note). Synopsis is the
                //       second such case, for a different and stated reason.
                //
                // TO REVERSE: re-run the §18.2 gate (preservation >= 90% AND over-rewrite exactly 0) for
                // Synopsis THROUGH THIS SERVICE on q1's own fixtures, or give this stage a rule-(7b)
                // equivalent, then add `case AnalysisType.Synopsis:` mirroring the Summarization arm above.
                // Pinned by AnalysisRepairExclusionRegressionTests.
                // ShippedSynopsis_IsDeliberatelyOutsideTheValueScopedLlmStage and recorded in
                // AnalysisRepairConfigParityTests.DispatchCoverageFor. See docs §4.1 / §4.2.
                //
                // Proofread and everything else: NEVER repaired. Return the inputs unchanged (counters 0).
                default:
                    return new AnalysisRepairResult { StructuredJson = structuredJson, CleanContent = cleanContent };
            }
        }
        catch (Exception ex)
        {
            // Belt-and-braces: RepairFieldAsync is already fail-safe, but a deserialize/serialize edge case
            // must never bubble out of the analysis pipeline. Keep the original values (counters 0).
            _logger.LogWarning(ex, "AnalysisRepair: RepairAnalysisAsync failed; keeping original values (fail-safe).");
            return new AnalysisRepairResult { StructuredJson = structuredJson, CleanContent = cleanContent };
        }
    }

    /// <summary>
    /// Whole-text repair for Summarization: the entire <paramref name="cleanContent"/> is the prose, routed
    /// through <see cref="RepairableFields.ForPlainText"/> so the write-back uses the same whitelist seam as
    /// the structured types. Guard-gated: a clean field makes ZERO model calls and returns byte-identical.
    /// </summary>
    private async Task<(string Value, int Flagged, int Repaired, int FailSafe)> RepairPlainTextAsync(
        AnalysisType type, string cleanContent, string language, CancellationToken ct)
    {
        // GUARD: no residual Latin => no model call, byte-identical output (nothing flagged).
        if (!LatinInHebrewContentDetector.HasNonAllowlistedLatin(cleanContent))
        {
            return (cleanContent, 0, 0, 0);
        }

        var written = cleanContent;
        var field = RepairableFields.ForPlainText(cleanContent, v => written = v)[0];
        var value = field.Get();

        var latinBefore = LatinInHebrewContentDetector.DetectLatinRuns(value).Count;
        var outcome = await RepairFieldCoreAsync(value, language, ct).ConfigureAwait(false);
        var latinAfter = LatinInHebrewContentDetector.DetectLatinRuns(outcome.Value).Count;

        var repaired = 0;
        var failSafe = 0;
        if (outcome.Accepted && !string.Equals(outcome.Value, cleanContent, StringComparison.Ordinal))
        {
            field.Set(outcome.Value); // forwards to the caller-supplied delegate -> `written`
            repaired = 1;
        }
        else if (!outcome.Accepted)
        {
            failSafe = 1;
        }

        LogRepairedField(type, fieldIndex: 0, latinBefore, latinAfter, outcome);

        return (written, 1, repaired, failSafe);
    }

    /// <summary>
    /// Structured-result repair: deserialize, walk the whitelisted prose accessors, and — for ONLY the
    /// fields the guard still flags — attempt a fail-safe <see cref="RepairFieldAsync"/>. Re-serialise with
    /// the runtime-type overload (so an object-typed reference cannot serialise to <c>{}</c>) ONLY when at
    /// least one value changed; otherwise the ORIGINAL JSON is returned byte-identical. An unparsable /
    /// null payload leaves the input untouched (fail-safe).
    /// </summary>
    private async Task<(string? Json, int Flagged, int Repaired, int FailSafe)> RepairStructuredAsync<T>(
        AnalysisType type,
        string? structuredJson,
        string language,
        JsonSerializerOptions jsonOptions,
        Func<T, IReadOnlyList<RepairableField>> accessorsOf,
        CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(structuredJson))
        {
            return (structuredJson, 0, 0, 0);
        }

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(structuredJson, jsonOptions);
        }
        catch (JsonException)
        {
            return (structuredJson, 0, 0, 0); // fail-safe: unparsable => leave untouched
        }

        if (parsed is null)
        {
            return (structuredJson, 0, 0, 0);
        }

        var changed = false;
        var flagged = 0;
        var repairedCount = 0;
        var failSafeCount = 0;

        // Walk by index so the per-field Debug log can identify the field by its STABLE accessor position
        // (RepairableFields ordering) without ever logging its content.
        var accessors = accessorsOf(parsed);
        for (var index = 0; index < accessors.Count; index++)
        {
            var field = accessors[index];
            var value = field.Get();

            // GUARD: a field with no non-allowlisted Latin never reaches the model (ZERO model calls on
            // clean output). This is what guarantees a clean result is a strict no-op.
            if (!LatinInHebrewContentDetector.HasNonAllowlistedLatin(value))
            {
                continue;
            }

            flagged++;
            var latinBefore = LatinInHebrewContentDetector.DetectLatinRuns(value).Count;
            var outcome = await RepairFieldCoreAsync(value, language, ct).ConfigureAwait(false);
            var latinAfter = LatinInHebrewContentDetector.DetectLatinRuns(outcome.Value).Count;

            if (outcome.Accepted && !string.Equals(outcome.Value, value, StringComparison.Ordinal))
            {
                field.Set(outcome.Value);
                changed = true;
                repairedCount++;
            }
            else if (!outcome.Accepted)
            {
                failSafeCount++;
            }

            LogRepairedField(type, index, latinBefore, latinAfter, outcome);
        }

        if (!changed)
        {
            // Nothing rewritten: return the input JSON byte-identical (never re-serialize an unchanged
            // object, so a clean / all-fail-safe result cannot drift).
            return (structuredJson, flagged, repairedCount, failSafeCount);
        }

        // Re-serialize with the SAME options the pipeline persists with. The runtime-type overload avoids
        // the object->{} gotcha if T is ever referenced through a less-derived static type.
        var newJson = JsonSerializer.Serialize(parsed, parsed.GetType(), jsonOptions);
        return (newJson, flagged, repairedCount, failSafeCount);
    }

    /// <summary>
    /// Accept the repaired value ONLY if EVERY guard holds (else the caller keeps the original):
    ///   (a) non-empty after trim;
    ///   (b) still Hebrew — contains Hebrew letters AND is predominantly Hebrew (Hebrew letters &gt;= Latin);
    ///   (c) introduces NO new Latin run — every non-allowlisted Latin run in the repair must already be
    ///       present in the INPUT's Latin-run set (removing Latin is good; adding a NEW run is a reject);
    ///   (d) length ratio (repaired / original) within [<see cref="MinLengthRatio"/>, <see cref="MaxLengthRatio"/>].
    /// </summary>
    private static bool IsAcceptableRepair(string original, string? repaired)
    {
        // (a) non-empty after trim
        if (string.IsNullOrWhiteSpace(repaired))
        {
            return false;
        }

        var trimmed = repaired.Trim();

        // (b) still Hebrew (predominantly)
        if (!IsPredominantlyHebrew(trimmed))
        {
            return false;
        }

        // (c) no NEW Latin run — compare against the input's Latin-run set (case-insensitive). Uses the
        // SAME detector as the guard/gold scorer, so "Latin" means the same thing everywhere and an
        // allowlisted proper noun (Google/Facebook) is not counted as a run on either side.
        var originalRuns = new HashSet<string>(
            LatinInHebrewContentDetector.DetectLatinRuns(original), StringComparer.OrdinalIgnoreCase);
        foreach (var run in LatinInHebrewContentDetector.DetectLatinRuns(trimmed))
        {
            if (!originalRuns.Contains(run))
            {
                return false;
            }
        }

        // (d) length ratio in bounds (original is non-empty here, so no divide-by-zero)
        if (original.Length == 0)
        {
            return false;
        }
        var ratio = (double)trimmed.Length / original.Length;
        if (ratio < MinLengthRatio || ratio > MaxLengthRatio)
        {
            return false;
        }

        return true;
    }

    /// <summary>Matches a single Hebrew letter (block U+0590–U+05FF). Compiled once.</summary>
    private static readonly Regex HebrewLetterRegex = new("[֐-׿]", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="text"/> contains at least one Hebrew letter AND has at least as many
    /// Hebrew letters as Latin letters. Robust against a repair that silently returned an English
    /// translation (Latin would dominate) while allowing a small number of legitimately-Latin tokens
    /// (which are separately bounded by the no-new-Latin-run guard).
    /// </summary>
    private static bool IsPredominantlyHebrew(string text)
    {
        var hebrew = HebrewLetterRegex.Matches(text).Count;
        if (hebrew == 0)
        {
            return false;
        }

        var latin = 0;
        foreach (var ch in text)
        {
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
            {
                latin++;
            }
        }

        return hebrew >= latin;
    }
}
