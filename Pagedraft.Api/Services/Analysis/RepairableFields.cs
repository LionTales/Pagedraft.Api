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
//             DimensionScore.Score, CharacterEntry.Role, ConflictEntry.Type,
//             ConflictEntry.Status, QAResult.Confidence.
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
//   • BookOverviewResult.Genre / SubGenre / TargetAudience / LanguageRegister —
//     short label/register fields, not free prose; LiteratureLevel /
//     EstimatedReadingTimeMinutes — numeric.
//   • CharacterEntry.Name, CharacterRelationship.Character1 / Character2 —
//     proper-noun character references; CharacterEntry.FirstAppearanceChapter —
//     numeric.
//   • ChapterCitation.ChapterTitle / RelevantExcerpt — a chapter-title reference
//     and a manuscript-quote excerpt (leave verbatim, like ConsistencyIssue.Span);
//     ChapterCitation.ChapterNumber — numeric.
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
    /// BookOverview (gemma4:12b): summary ONLY. NOT: genre / subGenre / targetAudience /
    /// languageRegister (short label/register fields, not free prose) or literatureLevel /
    /// estimatedReadingTimeMinutes (numeric). No collections here, so no null-guard is needed —
    /// the single prose scalar is always safe to read/write.
    /// </summary>
    public static IReadOnlyList<RepairableField> For(BookOverviewResult result)
        => new[]
        {
            new RepairableField(() => result.Summary, v => result.Summary = v),
        };

    /// <summary>
    /// CharacterAnalysis (gemma4:12b): top-level summary; per character description + arc;
    /// per relationship the relationship-prose field. NOT: character name (proper noun),
    /// role (enum), or firstAppearanceChapter (numeric); relationship character1 / character2
    /// (proper-noun references).
    /// </summary>
    public static IReadOnlyList<RepairableField> For(CharacterAnalysisResult result)
    {
        var fields = new List<RepairableField>
        {
            new(() => result.Summary, v => result.Summary = v),
        };

        // NULL-GUARD (see LiteraryAnalysisResult.For): a model-emitted `"characters": null` /
        // `"relationships": null` deserializes to null and must not throw. `?? Empty` + a per-element
        // null skip keep the walk safe without exposing any new field.
        foreach (var character in result.Characters ?? Enumerable.Empty<CharacterEntry>())
        {
            if (character is null) continue;
            var c = character; // capture the element so the setter writes back to it
            fields.Add(new RepairableField(() => c.Description, v => c.Description = v));
            fields.Add(new RepairableField(() => c.Arc, v => c.Arc = v));
            // c.Name is a proper noun, c.Role an enum label, c.FirstAppearanceChapter numeric — must-not-touch.
        }

        foreach (var relationship in result.Relationships ?? Enumerable.Empty<CharacterRelationship>())
        {
            if (relationship is null) continue;
            var r = relationship;
            fields.Add(new RepairableField(() => r.Relationship, v => r.Relationship = v));
            // r.Character1 / r.Character2 are proper-noun character references — must-not-touch.
        }

        return fields;
    }

    /// <summary>
    /// StoryAnalysis (gemma4:12b): plotStructure prose subfields (setup / risingAction / climax /
    /// fallingAction / resolution), pacing, per conflict description, top-level summary. NOT:
    /// conflict type / status (enums). plotStructure is a single nested object, null-guarded exactly
    /// like a collection because a model can emit `"plotStructure": null`.
    /// </summary>
    public static IReadOnlyList<RepairableField> For(StoryAnalysisResult result)
    {
        var fields = new List<RepairableField>();

        // NULL-GUARD: plotStructure is initialised `= new()`, but System.Text.Json OVERWRITES it with
        // null when the model emits `"plotStructure": null`. Walking its subfields unguarded would NRE
        // out of the always-on Stage-1 pass. Expose the 5 prose subfields ONLY when the object is present
        // (capture the local so each setter writes back to it).
        var plot = result.PlotStructure;
        if (plot is not null)
        {
            fields.Add(new RepairableField(() => plot.Setup, v => plot.Setup = v));
            fields.Add(new RepairableField(() => plot.RisingAction, v => plot.RisingAction = v));
            fields.Add(new RepairableField(() => plot.Climax, v => plot.Climax = v));
            fields.Add(new RepairableField(() => plot.FallingAction, v => plot.FallingAction = v));
            fields.Add(new RepairableField(() => plot.Resolution, v => plot.Resolution = v));
        }

        fields.Add(new RepairableField(() => result.Pacing, v => result.Pacing = v));
        fields.Add(new RepairableField(() => result.Summary, v => result.Summary = v));

        // NULL-GUARD (see LiteraryAnalysisResult.For): a model-emitted `"conflicts": null` deserializes
        // to null and must not throw. `?? Empty` + a per-element null skip keep the walk safe.
        foreach (var conflict in result.Conflicts ?? Enumerable.Empty<ConflictEntry>())
        {
            if (conflict is null) continue;
            var cf = conflict;
            fields.Add(new RepairableField(() => cf.Description, v => cf.Description = v));
            // cf.Type / cf.Status are enum labels — must-not-touch.
        }

        return fields;
    }

    /// <summary>
    /// QA (via GenericChat): answer ONLY. NOT: citations[].chapterNumber (numeric) / chapterTitle
    /// (chapter-title reference) / relevantExcerpt (a manuscript-quote excerpt, left verbatim like
    /// ConsistencyIssue.Span) or confidence (enum label). QA DOES reach the repair seam with a parsed
    /// structuredJson: RunWithInputAsync calls TryParseStructured, which routes QA through
    /// TryExtractAndReserialize&lt;QAResult&gt; (the QA prompt requests the answer/citations/confidence
    /// JSON shape), so the Answer prose gets the same deterministic glossary safety net as the other
    /// structured-Hebrew-prose analyses. Citations is a collection but exposes NO prose here, so there is
    /// no walk and no null-guard is needed.
    /// </summary>
    public static IReadOnlyList<RepairableField> For(QAResult result)
        => new[]
        {
            new RepairableField(() => result.Answer, v => result.Answer = v),
        };

    /// <summary>
    /// BookReview (gemma4:12b): findings[].rationale and findings[].suggestedAction.
    /// suggestedAction is nullable — exposed ONLY when non-null; the setter assigns
    /// through the string? property. NOT: dimension/verdict (enums), severity,
    /// evidence, chapterAnchors, or the rollup scores.
    ///
    /// DTO OVERLOAD — test-only. BookReview flows through the whole-book review engine
    /// (BookReviewService), never through the RunAsync/RunWithInputAsync/streaming seams that call
    /// ApplyAnalysisRepairAsync, and both GlossaryRepairPass.Apply and
    /// AnalysisRepairService.RepairAnalysisAsync intentionally exclude AnalysisType.BookReview. The
    /// engine-path glossary hook (f5-wire JOB 2) repairs the persisted <see cref="BookFinding"/> ENTITIES
    /// via the sibling <see cref="For(BookFinding)"/> overload (BookReviewService.ApplyGlossaryToFindings),
    /// NOT this parsed-DTO overload — the model's raw JSON is projected straight to entities before repair.
    /// This <see cref="BookReviewResult"/> overload therefore stays consumed only by tests (kept for
    /// symmetry / a future parsed-DTO wiring).
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
    /// BookReview ENTITY path (f5-wire JOB 2): the PERSISTED <see cref="BookFinding"/> row exposes ONLY
    /// Rationale + (non-null) SuggestedAction. This is the ENTITY sibling of <see cref="For(BookReviewResult)"/>
    /// (which targets the parsed <c>BookFindingItem</c> DTO): the whole-book review ENGINE
    /// (<c>BookReviewService</c>) finalises findings as <see cref="BookFinding"/> ENTITIES via UnionAndDedup and
    /// never passes through the RunAsync/streaming repair seam, so the deterministic glossary safety net is
    /// applied to the entity fields directly, IN PLACE, before persist.
    ///
    /// NOT: Dimension / Verdict (enum-like labels), Severity (numeric), EvidenceJson / ChapterAnchorsJson
    /// (manuscript anchors + structural JSON), DedupKey / Status / BuiltWithModel / CreatedAt / UpdatedAt —
    /// none is ever exposed to the glossary, so a repair can change ONLY the two prose fields. SuggestedAction
    /// is <c>string?</c>: exposed ONLY when non-null (the setter writes through the nullable property); never
    /// synthesised from null. Repairing Rationale here does NOT invalidate the row's DedupKey — the key is
    /// computed from the RAW model rationale in UnionAndDedup BEFORE this pass runs and is deliberately left
    /// untouched, so a rebuild re-derives the same key from the model's (re-leaked) output and user Status is
    /// preserved (the repair is a display-time cleanup, never a dedup input).
    /// </summary>
    public static IReadOnlyList<RepairableField> For(BookFinding finding)
    {
        var fields = new List<RepairableField>
        {
            new(() => finding.Rationale, v => finding.Rationale = v),
        };

        // SuggestedAction is string?; only repair an existing value, never synthesise one from null. The
        // getter is only reached while non-null (mirrors For(BookReviewResult)'s BookFindingItem handling).
        if (finding.SuggestedAction != null)
        {
            fields.Add(new RepairableField(() => finding.SuggestedAction!, v => finding.SuggestedAction = v));
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
