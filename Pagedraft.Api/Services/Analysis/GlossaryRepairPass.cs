using System.Text.Json;
using System.Text.RegularExpressions;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// GlossaryRepairPass — the DETERMINISTIC, fail-safe first stage of the
// analysis-output repair layer.
//
// Plan: src/.cursor/plans/_todo/analysis-output-repair-2026-07-03.plan.md
//       (todo p2-glossary-apply). Wires together the three Phase-1/2 building
//       blocks:
//         • RepairableFields          — WHAT prose may be touched (whitelist).
//         • LatinInHebrewContentDetector — the guard: a field with no residual
//           Latin is left byte-identical (no cost, no risk).
//         • LiteraryTermGlossary      — the closed English -> Hebrew map.
//
// For each repairable field that STILL contains non-allowlisted Latin, it
// applies the glossary at ASCII word boundaries (multi-word keys first, so
// "High Stakes" beats a single-word match), re-serialises the parsed object
// with the pipeline's camelCase JsonOpts, and reports the Latin runs that
// SURVIVED the glossary — that residual is the hand-off contract for the p3
// value-scoped LLM repair pass, which extends this exact seam.
//
// FAIL-SAFE by construction:
//   • Gated on the BOOK being Hebrew — the glossary is English -> Hebrew, so
//     firing it on an English (or other) book would corrupt clean prose. A
//     non-Hebrew language is a no-op.
//   • Only the repair-target types reachable at these seams are handled
//     (Summarization, LiteraryAnalysis, LinguisticAnalysis, LineEdit, BookOverview,
//     CharacterAnalysis, StoryAnalysis, QA). Every other type — Proofread included —
//     is returned UNCHANGED (byte-identical).
//     BookReview flows through a DIFFERENT path (the whole-book review engine,
//     not RunAsync/RunWithInput/streaming), so it is intentionally NOT handled by
//     Apply. Its engine hook (BookReviewService.ApplyGlossaryToFindings, f5-wire)
//     repairs the finalized BookFinding entities via the reusable RepairFields
//     entry point below — the SAME glossary/detector, applied to entity fields.
//   • A parse failure, a clean field, or a term not in the closed glossary all
//     leave the value exactly as-is. The worst case is "left a leak for p3",
//     never "made a field worse".
//
// Always-on for now (deterministic + fail-safe).
// p6-config: gate this behind Ai:AnalysisRepair.GuardOnly/Enabled.
// ---------------------------------------------------------------------------

/// <summary>
/// Outcome of a <see cref="GlossaryRepairPass.Apply"/> call. <see cref="StructuredJson"/>
/// and <see cref="CleanContent"/> are the (possibly repaired) values to assign back at the
/// call site; when nothing changed they are the byte-identical inputs.
/// <see cref="ResidualLatinRuns"/> are the Latin runs that remained AFTER the glossary ran —
/// the hand-off list for the p3 LLM repair pass.
/// <see cref="Fault"/> is the exception the pass CAUGHT AND SWALLOWED on its fail-safe path
/// (returning the inputs unchanged); it is null on every success/no-op path. Because the pass
/// never throws, this is the ONLY signal that a fault occurred — the caller inspects it to log a
/// swallowed accessor-walk / re-serialize fault that would otherwise leave leaked English silently.
/// </summary>
public readonly record struct GlossaryRepairResult(
    string? StructuredJson,
    string CleanContent,
    int FieldsScanned,
    int FieldsChanged,
    IReadOnlyList<string> ResidualLatinRuns,
    Exception? Fault = null);

/// <summary>
/// Deterministic English -> Hebrew glossary replacement over the repairable prose fields of
/// an analysis result. See the header comment for the fail-safe contract and scope. Pure and
/// static: no state, no I/O, no model call.
/// </summary>
public static class GlossaryRepairPass
{
    /// <summary>
    /// Compiled replacement rules built once from <see cref="LiteraryTermGlossary.Terms"/>,
    /// ordered LONGEST-KEY-FIRST so a multi-word phrase ("high stakes") is applied before any
    /// single-word key, and each phrase's internal whitespace/hyphen is matched flexibly. Each
    /// pattern is anchored with ASCII-letter lookarounds: the keys are ASCII, so the boundary
    /// never matches inside a Hebrew word AND never matches inside a longer Latin word
    /// ("action" will not fire inside "reaction"/"actions").
    /// </summary>
    private static readonly IReadOnlyList<(Regex Pattern, string Hebrew)> Replacements = BuildReplacements();

    private static IReadOnlyList<(Regex, string)> BuildReplacements()
    {
        var list = new List<(Regex, string)>();
        foreach (var kvp in LiteraryTermGlossary.Terms.OrderByDescending(k => k.Key.Length))
        {
            // Split the English key on whitespace/hyphen runs, escape each token, then rejoin
            // with [\s-]+ so "high stakes" also matches "High  Stakes" / "high-stakes".
            var tokens = Regex.Split(kvp.Key.Trim(), @"[\s-]+")
                .Where(t => t.Length > 0)
                .Select(Regex.Escape);
            var body = string.Join(@"[\s-]+", tokens);
            if (body.Length == 0) continue;

            // (?<![A-Za-z]) … (?![A-Za-z]) = ASCII word boundary. Safe against Hebrew (no Latin
            // letters) and against longer Latin words. Do NOT use \b naively here.
            var pattern = $@"(?<![A-Za-z]){body}(?![A-Za-z])";
            list.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), kvp.Value));
        }

        return list;
    }

    /// <summary>
    /// Applies the deterministic glossary pass for the repair-target types reachable at the
    /// non-Proofread analysis seams. Returns the (possibly repaired) structuredJson/cleanContent
    /// plus the residual Latin runs for the p3 hand-off. Any non-target type, a non-Hebrew book,
    /// a parse failure, or a clean field is a no-op that returns the inputs byte-identical.
    /// </summary>
    /// <param name="type">Analysis type; only Summarization / LiteraryAnalysis / LinguisticAnalysis / LineEdit / BookOverview / CharacterAnalysis / StoryAnalysis / QA are repaired here.</param>
    /// <param name="structuredJson">The parsed-and-reserialised StructuredResult (null for Summarization / non-structured types).</param>
    /// <param name="cleanContent">The prose ResultText (the whole repairable text for Summarization).</param>
    /// <param name="language">BOOK language; the pass fires only when this starts with "he".</param>
    /// <param name="jsonOptions">The pipeline's camelCase JsonOpts, so re-serialisation matches persistence exactly.</param>
    public static GlossaryRepairResult Apply(
        AnalysisType type,
        string? structuredJson,
        string cleanContent,
        string language,
        JsonSerializerOptions jsonOptions)
    {
        // Hebrew-book gate: the glossary is English -> Hebrew. On a non-Hebrew book it would
        // translate legitimate English prose, so it must be a strict no-op there.
        if (string.IsNullOrWhiteSpace(language) ||
            !language.StartsWith("he", StringComparison.OrdinalIgnoreCase))
        {
            return NoOp(structuredJson, cleanContent);
        }

        return type switch
        {
            // Summarization: TryParseStructured returns null, so the ENTIRE cleanContent is the
            // repairable prose (applied via RepairableFields.ForPlainText).
            AnalysisType.Summarization => RepairPlainText(structuredJson, cleanContent),

            AnalysisType.LiteraryAnalysis =>
                RepairStructured<LiteraryAnalysisResult>(structuredJson, cleanContent, jsonOptions, RepairableFields.For),
            AnalysisType.LinguisticAnalysis =>
                RepairStructured<LinguisticAnalysisResult>(structuredJson, cleanContent, jsonOptions, RepairableFields.For),
            AnalysisType.LineEdit =>
                RepairStructured<LineEditResult>(structuredJson, cleanContent, jsonOptions, RepairableFields.For),

            // Book-level structured-Hebrew-prose analyses on the SAME gemma4:12b as LiteraryAnalysis —
            // susceptible to the same stochastic English-term leak, so wired through the identical seam
            // (f5-wire). QA reaches this seam with a parsed QAResult (RunWithInputAsync -> TryParseStructured),
            // so its answer prose is repaired too.
            AnalysisType.BookOverview =>
                RepairStructured<BookOverviewResult>(structuredJson, cleanContent, jsonOptions, RepairableFields.For),
            AnalysisType.CharacterAnalysis =>
                RepairStructured<CharacterAnalysisResult>(structuredJson, cleanContent, jsonOptions, RepairableFields.For),
            AnalysisType.StoryAnalysis =>
                RepairStructured<StoryAnalysisResult>(structuredJson, cleanContent, jsonOptions, RepairableFields.For),
            AnalysisType.QA =>
                RepairStructured<QAResult>(structuredJson, cleanContent, jsonOptions, RepairableFields.For),

            // Proofread and everything else: not a repair target at these seams. BookReview is
            // handled on its own path, never here. Return the inputs unchanged.
            _ => NoOp(structuredJson, cleanContent),
        };
    }

    /// <summary>
    /// Deterministic glossary pass over an ALREADY-BUILT list of repairable prose fields, for callers that
    /// own their own field list and write-back rather than a re-serialised structured result — specifically
    /// the whole-book review ENGINE path (BookReviewService, f5-wire JOB 2), which repairs
    /// <c>BookFinding</c> ENTITY fields directly IN PLACE and never flows through <see cref="Apply"/>'s
    /// RunAsync/streaming seam. It runs the SAME substitution machinery <see cref="RepairStructured{T}"/>
    /// uses — the guard (<see cref="LatinInHebrewContentDetector.HasNonAllowlistedLatin"/>) then the closed
    /// glossary (<see cref="ApplyGlossary"/>) per field — so there is ONE glossary, never a second copy.
    ///
    /// Hebrew-gated exactly like <see cref="Apply"/>: the glossary is English -> Hebrew, so a blank or
    /// non-Hebrew language is a strict no-op (returns 0, touches nothing). A clean field (no non-allowlisted
    /// Latin) is skipped byte-identical at zero cost; only a field the glossary actually changes is written
    /// back via its <see cref="RepairableField.Set"/>. Returns the number of fields whose value changed.
    ///
    /// This method does NOT catch: the caller owns the fail-safe try/catch (mirroring how
    /// <c>ApplyAnalysisRepairAsync</c> wraps the always-on <see cref="Apply"/> seam). Byte-identity of
    /// everything else is the CALLER's responsibility — pass only fields whose getters/setters touch
    /// repairable prose (RepairableFields.For enforces that whitelist).
    /// </summary>
    internal static int RepairFields(IReadOnlyList<RepairableField> fields, string language)
    {
        // Hebrew-book gate (same contract as Apply): English -> Hebrew glossary must never fire on a
        // non-Hebrew book, or it would translate legitimate English prose.
        if (fields is null || fields.Count == 0 ||
            string.IsNullOrWhiteSpace(language) ||
            !language.StartsWith("he", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var changed = 0;
        foreach (var field in fields)
        {
            var value = field.Get();

            // GUARD: a field with no non-allowlisted Latin is left byte-identical (zero cost, zero risk).
            if (!LatinInHebrewContentDetector.HasNonAllowlistedLatin(value))
            {
                continue;
            }

            var repaired = ApplyGlossary(value);
            if (!string.Equals(repaired, value, StringComparison.Ordinal))
            {
                field.Set(repaired);
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// Structured-result repair: deserialize, walk the whitelisted prose accessors, glossary-
    /// repair only the fields the guard flags, and re-serialize when at least one field changed.
    /// If nothing changed the ORIGINAL structuredJson is returned (byte-identical) — residual
    /// Latin may still be reported so the p3 pass can pick it up.
    /// </summary>
    private static GlossaryRepairResult RepairStructured<T>(
        string? structuredJson,
        string cleanContent,
        JsonSerializerOptions jsonOptions,
        Func<T, IReadOnlyList<RepairableField>> accessorsOf) where T : class
    {
        if (string.IsNullOrWhiteSpace(structuredJson))
        {
            return NoOp(structuredJson, cleanContent);
        }

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(structuredJson, jsonOptions);
        }
        catch (JsonException)
        {
            return NoOp(structuredJson, cleanContent); // fail-safe: unparsable => leave untouched
        }

        if (parsed is null)
        {
            return NoOp(structuredJson, cleanContent);
        }

        // FAIL-SAFE catch-all over the ENTIRE accessor walk + re-serialize — not just the
        // Deserialize above. This stage is ALWAYS-ON at the shipped guard-only default, and the
        // seam that invokes it (UnifiedAnalysisService.ApplyAnalysisRepairAsync) feeds RunAsync,
        // so ANY escape here would crash the whole analysis. A model can emit `"themes": null`
        // etc.; RepairableFields is now null-guarded, but this belt-and-braces guarantees that no
        // unforeseen accessor/serialization fault (e.g. a null element, a serializer edge case)
        // can ever throw out of the repair layer. On ANY exception the INPUT is returned unchanged
        // AND the caught exception is surfaced via GlossaryRepairResult.Fault so the caller logs it —
        // this stage swallowing a fault silently is what previously left leaked English with no warning.
        try
        {
            var accessors = accessorsOf(parsed);
            var scanned = 0;
            var changed = 0;
            var residual = new List<string>();

            foreach (var field in accessors)
            {
                scanned++;
                var value = field.Get();

                // GUARD: a field with no non-allowlisted Latin is left byte-identical. This is what
                // keeps clean fields (and thus every structural field) untouched by construction.
                if (!LatinInHebrewContentDetector.HasNonAllowlistedLatin(value))
                {
                    continue;
                }

                var repaired = ApplyGlossary(value);
                if (!string.Equals(repaired, value, StringComparison.Ordinal))
                {
                    field.Set(repaired);
                    changed++;
                }

                // Latin still present AFTER the glossary = the p3 LLM pass's job.
                residual.AddRange(LatinInHebrewContentDetector.DetectLatinRuns(repaired));
            }

            if (changed == 0)
            {
                // Nothing rewritten: return the input JSON byte-identical (never re-serialize an
                // unchanged object, so a clean/glossary-miss result cannot drift).
                return new GlossaryRepairResult(structuredJson, cleanContent, scanned, 0, residual);
            }

            // Re-serialize with the SAME options TryExtractAndReserialize used, so the wire shape
            // matches what the pipeline would otherwise persist.
            var newJson = JsonSerializer.Serialize(parsed, jsonOptions);
            return new GlossaryRepairResult(newJson, cleanContent, scanned, changed, residual);
        }
        catch (Exception ex)
        {
            // fail-safe: never throw into RunAsync. But surface the swallowed fault via the result so
            // the caller (ApplyAnalysisRepairAsync) can log it — otherwise an accessor-walk / re-serialize
            // fault would silently return the inputs unchanged and leave leaked English with NO warning.
            return new GlossaryRepairResult(structuredJson, cleanContent, 0, 0, Array.Empty<string>(), ex);
        }
    }

    /// <summary>
    /// Whole-text repair for Summarization: the entire cleanContent is the prose. Applied via
    /// RepairableFields.ForPlainText so the write-back goes through the same whitelist seam as
    /// the structured types.
    /// </summary>
    private static GlossaryRepairResult RepairPlainText(string? structuredJson, string cleanContent)
    {
        if (!LatinInHebrewContentDetector.HasNonAllowlistedLatin(cleanContent))
        {
            return NoOp(structuredJson, cleanContent);
        }

        string? written = null;
        var field = RepairableFields.ForPlainText(cleanContent, v => written = v)[0];

        var repaired = ApplyGlossary(field.Get());
        var residual = LatinInHebrewContentDetector.DetectLatinRuns(repaired);

        if (string.Equals(repaired, cleanContent, StringComparison.Ordinal))
        {
            return new GlossaryRepairResult(structuredJson, cleanContent, 1, 0, residual);
        }

        field.Set(repaired); // forwards to the caller-supplied delegate -> `written`
        return new GlossaryRepairResult(structuredJson, written ?? repaired, 1, 1, residual);
    }

    /// <summary>
    /// Replaces every glossary term in <paramref name="value"/> at ASCII word boundaries,
    /// case-insensitively, preserving all surrounding punctuation/parens (the boundaries are
    /// zero-width lookarounds, so nothing around the term is consumed). Multi-word keys are
    /// applied first. A term not in the glossary is left as-is (that residual is the p3 pass's
    /// job). Because every Hebrew replacement is Latin-free, no replacement can cascade into a
    /// later key match.
    /// </summary>
    private static string ApplyGlossary(string value)
    {
        var result = value;
        foreach (var (pattern, hebrew) in Replacements)
        {
            result = pattern.Replace(result, hebrew);
        }

        return result;
    }

    private static GlossaryRepairResult NoOp(string? structuredJson, string cleanContent)
        => new(structuredJson, cleanContent, 0, 0, Array.Empty<string>());
}
