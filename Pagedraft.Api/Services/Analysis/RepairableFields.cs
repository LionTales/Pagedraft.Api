using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// RepairableFields — single source of truth for WHAT prose the analysis-output
// repair layer may touch.
//
// Plan: src/.cursor/plans/_todo/analysis-output-repair-2026-07-03.plan.md
//       (todo p1-repairable-map; "Real-model routing" table + "Must-not-touch").
//
// This is a PURE, DETERMINISTIC map. Each `For(...)` overload returns an ordered
// list of accessors (get/set) over the PROSE-only fields of one parsed structured
// result. A setter writes back to the SAME object / list element it was built
// from (it closes over that reference), so a repaired value lands in the caller's
// parsed object, which the caller then re-serialises. No side effects occur until
// a setter is invoked. Nothing calls this in production yet (no behaviour change).
//
// Ordering is stable and deterministic: fields in the order the routing table
// lists them, then list items in index order.
//
// -------------------------- MUST-NOT-TOUCH (never exposed here) --------------
// The repair layer must NEVER rewrite any of the following; they are anchors,
// structural keys, or numeric/enum metrics. This list is centralised here per
// the plan so the whitelist below and this blacklist stay in one place.
//   • ALL JSON property keys — structure is held by code / re-serialisation,
//     never renamed.
//   • Enums:  ThemeEntry.Significance, ConsistencyIssue.Type,
//             LineEditSuggestion.Category, BookFindingItem.Dimension/Verdict,
//             DimensionScore.Score.
//   • StyleDeviation.Metric      — FE label-lookup key.
//   • StyleDeviation.SceneValue / StyleDeviation.ChapterBaseline — numeric.
//   • ConsistencyIssue.Span      — anchor / manuscript quote (Hebrew by
//     construction; leave verbatim).
//   • LineEditSuggestion.Original / LineEditSuggestion.Suggested — verbatim
//     anchors.
//   • AnalysisSuggestion.OriginalText / SuggestedText / StartOffset / EndOffset
//     — offset anchors (Proofread is left entirely; see For(AnalysisSuggestion)).
//   • BookFindingItem.Severity / Evidence / ChapterAnchors; all numeric metrics
//     everywhere.
// ---------------------------------------------------------------------------

/// <summary>A single repairable prose field: read the current value, or write a
/// repaired value back to the object/list element it was captured from.</summary>
public readonly struct RepairableField
{
    public RepairableField(Func<string> get, Action<string> set)
    {
        Get = get;
        Set = set;
    }

    /// <summary>Reads the current value of the field.</summary>
    public Func<string> Get { get; }

    /// <summary>Writes a repaired value back to the same object/list element.</summary>
    public Action<string> Set { get; }
}

/// <summary>
/// Whitelist of PROSE fields the analysis-output repair layer may rewrite, per
/// structured result type. See the header comment for the must-not-touch list.
/// </summary>
public static class RepairableFields
{
    /// <summary>
    /// LiteraryAnalysis (gemma4:12b): summary, tone, toneDescription,
    /// narrativeVoice(+Description), moodProgression, themes[].name/description,
    /// rhetoricalDevices[].name/example/effect. NOT: themes[].significance (enum).
    /// </summary>
    public static IReadOnlyList<RepairableField> For(LiteraryAnalysisResult result)
    {
        var fields = new List<RepairableField>
        {
            new(() => result.Summary, v => result.Summary = v),
            new(() => result.Tone, v => result.Tone = v),
            new(() => result.ToneDescription, v => result.ToneDescription = v),
            new(() => result.NarrativeVoice, v => result.NarrativeVoice = v),
            new(() => result.NarrativeVoiceDescription, v => result.NarrativeVoiceDescription = v),
            new(() => result.MoodProgression, v => result.MoodProgression = v),
        };

        // NULL-GUARD: a model can emit `"themes": null` (LLMs use null for "none"); System.Text.Json
        // then OVERWRITES the `= new()` initializer with null. Walking it unguarded would throw an
        // NRE out of the always-on Stage-1 glossary pass and crash the whole analysis. `?? Empty`
        // yields no accessors; a null ELEMENT (`[null, {...}]`) is skipped so its getter/setter is
        // never built. This never exposes a new field — it only omits accessors.
        foreach (var theme in result.Themes ?? Enumerable.Empty<ThemeEntry>())
        {
            if (theme is null) continue;
            var t = theme; // capture the element so the setter writes back to it
            fields.Add(new RepairableField(() => t.Name, v => t.Name = v));
            fields.Add(new RepairableField(() => t.Description, v => t.Description = v));
            // t.Significance is an enum label — must-not-touch.
        }

        foreach (var device in result.RhetoricalDevices ?? Enumerable.Empty<RhetoricalDevice>())
        {
            if (device is null) continue;
            var d = device;
            fields.Add(new RepairableField(() => d.Name, v => d.Name = v));
            fields.Add(new RepairableField(() => d.Example, v => d.Example = v));
            fields.Add(new RepairableField(() => d.Effect, v => d.Effect = v));
        }

        return fields;
    }

    /// <summary>
    /// LinguisticAnalysis (gemma4:12b): summary, deviations[].note,
    /// consistencyIssues[].description. NOT: deviations[].metric (FE label key)
    /// or sceneValue/chapterBaseline (numeric); consistencyIssues[].type (enum)
    /// or span (manuscript-quote anchor).
    /// </summary>
    public static IReadOnlyList<RepairableField> For(LinguisticAnalysisResult result)
    {
        var fields = new List<RepairableField>
        {
            new(() => result.Summary, v => result.Summary = v),
        };

        // NULL-GUARD (see LiteraryAnalysisResult.For): a model-emitted `"deviations": null` /
        // `"consistencyIssues": null` deserializes to null and must not throw. `?? Empty` + a
        // per-element null skip keep the walk safe without exposing any new field.
        foreach (var deviation in result.Deviations ?? Enumerable.Empty<StyleDeviation>())
        {
            if (deviation is null) continue;
            var dv = deviation;
            fields.Add(new RepairableField(() => dv.Note, v => dv.Note = v));
        }

        foreach (var issue in result.ConsistencyIssues ?? Enumerable.Empty<ConsistencyIssue>())
        {
            if (issue is null) continue;
            var ci = issue;
            fields.Add(new RepairableField(() => ci.Description, v => ci.Description = v));
            // ci.Type is an enum label and ci.Span is a manuscript anchor — must-not-touch.
        }

        return fields;
    }

    /// <summary>
    /// LineEdit (Dicta-3.0): overallFeedback, suggestions[].reason. NOT:
    /// suggestions[].original / suggested (verbatim anchors) or category (enum).
    /// </summary>
    public static IReadOnlyList<RepairableField> For(LineEditResult result)
    {
        var fields = new List<RepairableField>
        {
            new(() => result.OverallFeedback, v => result.OverallFeedback = v),
        };

        // NULL-GUARD (see LiteraryAnalysisResult.For): a model-emitted `"suggestions": null`
        // deserializes to null and must not throw. `?? Empty` + a per-element null skip keep the
        // walk safe without exposing any new field.
        foreach (var suggestion in result.Suggestions ?? Enumerable.Empty<LineEditSuggestion>())
        {
            if (suggestion is null) continue;
            var s = suggestion;
            fields.Add(new RepairableField(() => s.Reason, v => s.Reason = v));
            // s.Original / s.Suggested are verbatim anchors and s.Category is an
            // enum label — must-not-touch.
        }

        return fields;
    }

    /// <summary>
    /// BookReview (gemma4:12b): findings[].rationale and findings[].suggestedAction.
    /// suggestedAction is nullable — exposed ONLY when non-null; the setter assigns
    /// through the string? property. NOT: dimension/verdict (enums), severity,
    /// evidence, chapterAnchors, or the rollup scores.
    ///
    /// NOT WIRED IN PRODUCTION: BookReview flows through the whole-book review engine
    /// (BookReviewService), never through the RunAsync/RunWithInputAsync/streaming seams
    /// that call ApplyAnalysisRepairAsync, and both GlossaryRepairPass.Apply and
    /// AnalysisRepairService.RepairAnalysisAsync intentionally exclude AnalysisType.BookReview.
    /// This overload is currently consumed only by tests; it is reserved for a future
    /// whole-book-path wiring (see analysis-output-repair-2026-07-03.plan.md Scope/out-of-scope).
    /// </summary>
    public static IReadOnlyList<RepairableField> For(BookReviewResult result)
    {
        var fields = new List<RepairableField>();

        // NULL-GUARD (see LiteraryAnalysisResult.For): a model-emitted `"findings": null`
        // deserializes to null and must not throw. `?? Empty` + a per-element null skip keep the
        // walk safe without exposing any new field.
        foreach (var finding in result.Findings ?? Enumerable.Empty<BookFindingItem>())
        {
            if (finding is null) continue;
            var f = finding;
            fields.Add(new RepairableField(() => f.Rationale, v => f.Rationale = v));

            // suggestedAction is string?; only repair an existing value, never
            // synthesise one from null. The getter is only reached while non-null.
            if (f.SuggestedAction != null)
            {
                fields.Add(new RepairableField(() => f.SuggestedAction!, v => f.SuggestedAction = v));
            }
        }

        return fields;
    }

    /// <summary>
    /// Proofread (Dicta-3.0): NONE. The routing table says "none (leave entirely)":
    /// every field of AnalysisSuggestion is either an anchor
    /// (OriginalText / SuggestedText / StartOffset / EndOffset) or Proofread
    /// metadata that the repair layer does not touch. Returns an empty list so
    /// callers can treat Proofread uniformly without a special case.
    /// </summary>
    public static IReadOnlyList<RepairableField> For(AnalysisSuggestion suggestion)
        => Array.Empty<RepairableField>();

    /// <summary>
    /// Summarization (qwen3.5:9b) and other whole-text prose: the entire ResultText
    /// is the repairable prose. A bare <see cref="string"/> is immutable and cannot
    /// be mutated in place, so — unlike the structured overloads which close over a
    /// mutable object — the CALL SITE must own the write-back. Pass the current
    /// value plus a setter (e.g. `s => result.ResultText = s`); this returns a single
    /// accessor whose setter forwards to that caller-supplied delegate.
    /// </summary>
    public static IReadOnlyList<RepairableField> ForPlainText(string value, Action<string> set)
        => new[] { new RepairableField(() => value, set) };
}
