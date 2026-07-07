using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic tests for <see cref="GlossaryRepairPass"/> — the always-on, fail-safe glossary
/// stage of the analysis-output repair layer (plan analysis-output-repair-2026-07-03, todo
/// p2-glossary-apply).
///
/// Proves: the real "(Action)"/"(Tension)"/"(High Stakes)" leak family is fixed for free;
/// structural fields (keys, enums, anchors) stay byte-identical (leaf-path diff, mirroring
/// <see cref="RepairableFieldsTests"/>); clean fields are a no-op; unknown terms are left and
/// surfaced as residual for the p3 LLM hand-off; the Hebrew-book gate keeps English books
/// untouched; and Proofread is never repaired.
///
/// NO Ollama, NO skip-gate — runs in CI always.
/// </summary>
public class GlossaryRepairPassTests
{
    /// <summary>Mirror of the pipeline's camelCase JsonOpts (UnifiedAnalysisService.JsonOpts) so
    /// the (de)serialize round-trip matches what production persists.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ─── LiteraryAnalysis: (Action) in summary ──────────────────────────────

    [Fact]
    public void Literary_SummaryWithParentheticalAction_BecomesHebrew_StructureByteIdentical()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "הסצנה מציגה תיאורי פעולה (Action) עזים.",
            Tone = "דרמטי",
            ToneDescription = "טון מתוח לאורך כל הפרק",
            NarrativeVoice = "גוף שלישי",
            NarrativeVoiceDescription = "מספר יודע-כול",
            MoodProgression = "עולה בהדרגה",
            Themes =
            {
                new ThemeEntry { Name = "כוח והקרבה", Description = "מוטיב חוזר", Significance = "major" },
            },
            RhetoricalDevices =
            {
                new RhetoricalDevice { Name = "מטאפורה", Example = "האור שבר את החושך", Effect = "מדגיש תקווה" },
            },
        };

        var before = Serialize(literary);
        var result = GlossaryRepairPass.Apply(AnalysisType.LiteraryAnalysis, before, "", "he", JsonOpts);

        Assert.NotNull(result.StructuredJson);
        var after = JsonSerializer.Deserialize<LiteraryAnalysisResult>(result.StructuredJson!, JsonOpts)!;

        Assert.Contains("(פעולה)", after.Summary);
        Assert.DoesNotContain("Action", after.Summary);
        Assert.Equal(1, result.FieldsChanged);

        // Only the summary leaf changed; every enum / key / other prose leaf is byte-identical.
        AssertOnlyLeavesChanged(before, result.StructuredJson!, "summary");
    }

    // ─── LiteraryAnalysis: multi-word "(High Stakes)" beats a single-word match ─

    [Fact]
    public void Literary_ToneDescriptionWithHighStakes_UsesMultiWordEquivalent()
    {
        var literary = new LiteraryAnalysisResult
        {
            ToneDescription = "טון עם (High Stakes) לאורך הסצנה.",
        };

        var before = Serialize(literary);
        var result = GlossaryRepairPass.Apply(AnalysisType.LiteraryAnalysis, before, "", "he", JsonOpts);

        var after = JsonSerializer.Deserialize<LiteraryAnalysisResult>(result.StructuredJson!, JsonOpts)!;

        Assert.Contains("(סיכונים גבוהים)", after.ToneDescription);
        Assert.DoesNotContain("High", after.ToneDescription);
        Assert.DoesNotContain("Stakes", after.ToneDescription);
        AssertOnlyLeavesChanged(before, result.StructuredJson!, "toneDescription");
    }

    // ─── LiteraryAnalysis: partial hit + residual hand-off to p3 ─────────────

    [Fact]
    public void Literary_ThemeName_NatureTranslated_MagicAndVsSurfacedAsResidual()
    {
        var literary = new LiteraryAnalysisResult
        {
            Themes =
            {
                new ThemeEntry { Name = "כוח מול (Magic vs. Nature)", Description = "מוטיב מרכזי", Significance = "major" },
            },
        };

        var before = Serialize(literary);
        var result = GlossaryRepairPass.Apply(AnalysisType.LiteraryAnalysis, before, "", "he", JsonOpts);

        var after = JsonSerializer.Deserialize<LiteraryAnalysisResult>(result.StructuredJson!, JsonOpts)!;

        // "nature" -> "טבע" in place; "Magic"/"vs" are not in the closed glossary, so they are
        // LEFT verbatim AND reported as residual — the contract the p3 LLM pass consumes.
        Assert.Contains("(Magic vs. טבע)", after.Themes[0].Name);
        Assert.DoesNotContain("Nature", after.Themes[0].Name);
        Assert.Contains("Magic", result.ResidualLatinRuns);
        Assert.Contains("vs", result.ResidualLatinRuns);
        Assert.DoesNotContain("Nature", result.ResidualLatinRuns);

        // Only the theme name changed; significance enum + description are byte-identical.
        AssertOnlyLeavesChanged(before, result.StructuredJson!, "themes[0].name");
    }

    // ─── Clean Hebrew field → guard skips → byte-identical no-op ─────────────

    [Fact]
    public void Literary_CleanHebrewFields_AreByteIdentical_NoResidual()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "הפרק בונה מתח הדרגתי ומגיע לשיא רגשי מרשים.",
            Tone = "נוגה",
            Themes =
            {
                new ThemeEntry { Name = "אובדן", Description = "נוכח בכל הפרק", Significance = "minor" },
            },
        };

        var before = Serialize(literary);
        var result = GlossaryRepairPass.Apply(AnalysisType.LiteraryAnalysis, before, "", "he", JsonOpts);

        // changed==0 returns the ORIGINAL json byte-identical (never re-serialized).
        Assert.Equal(before, result.StructuredJson);
        Assert.Equal(0, result.FieldsChanged);
        Assert.Empty(result.ResidualLatinRuns);
    }

    // ─── Unknown Latin term → left untouched, reported as residual ──────────

    [Fact]
    public void Literary_UnknownLatinTerm_IsLeftUntouched_AndReportedAsResidual()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "הניתוח מזכיר את Zorptext בהקשר לא ברור.",
        };

        var before = Serialize(literary);
        var result = GlossaryRepairPass.Apply(AnalysisType.LiteraryAnalysis, before, "", "he", JsonOpts);

        // Glossary miss => no field rewritten => original json returned byte-identical.
        Assert.Equal(before, result.StructuredJson);
        Assert.Equal(0, result.FieldsChanged);
        Assert.Contains("Zorptext", result.ResidualLatinRuns);
    }

    // ─── Summarization: whole-text prose path ───────────────────────────────

    [Fact]
    public void Summarization_WholeText_ParentheticalActionBecomesHebrew()
    {
        const string cleanContent = "סיכום הפרק כולל תיאור פעולה (Action) מהיר.";

        var result = GlossaryRepairPass.Apply(AnalysisType.Summarization, structuredJson: null, cleanContent, "he", JsonOpts);

        Assert.Null(result.StructuredJson); // Summarization has no structured result
        Assert.Contains("(פעולה)", result.CleanContent);
        Assert.DoesNotContain("Action", result.CleanContent);
        Assert.Equal(1, result.FieldsChanged);
    }

    [Fact]
    public void Summarization_CleanWholeText_IsByteIdentical()
    {
        const string cleanContent = "סיכום הפרק בונה מתח ומגיע לשיא.";

        var result = GlossaryRepairPass.Apply(AnalysisType.Summarization, structuredJson: null, cleanContent, "he", JsonOpts);

        Assert.Equal(cleanContent, result.CleanContent);
        Assert.Equal(0, result.FieldsChanged);
        Assert.Empty(result.ResidualLatinRuns);
    }

    // ─── LineEdit: overallFeedback repaired, anchors untouched ──────────────

    [Fact]
    public void LineEdit_OverallFeedbackRepaired_AnchorsAndCategoryByteIdentical()
    {
        var lineEdit = new LineEditResult
        {
            OverallFeedback = "משוב כללי עם (Tension) גבוה.",
            Suggestions =
            {
                new LineEditSuggestion
                {
                    Original = "המשפט המקורי",
                    Suggested = "המשפט המוצע",
                    Reason = "לשיפור הבהירות",
                    Category = "clarity",
                },
            },
        };

        var before = Serialize(lineEdit);
        var result = GlossaryRepairPass.Apply(AnalysisType.LineEdit, before, "", "he", JsonOpts);

        var after = JsonSerializer.Deserialize<LineEditResult>(result.StructuredJson!, JsonOpts)!;

        // overallFeedback is the prose-primary field the wiring refreshes ResultText from (via
        // MaybeReplaceLineEditResultText): it now carries the repaired value.
        Assert.Contains("(מתח)", after.OverallFeedback);
        Assert.DoesNotContain("Tension", after.OverallFeedback);

        // Verbatim anchors + enum category are untouched.
        Assert.Equal("המשפט המקורי", after.Suggestions[0].Original);
        Assert.Equal("המשפט המוצע", after.Suggestions[0].Suggested);
        Assert.Equal("clarity", after.Suggestions[0].Category);
        Assert.Equal("לשיפור הבהירות", after.Suggestions[0].Reason);

        AssertOnlyLeavesChanged(before, result.StructuredJson!, "overallFeedback");
    }

    // ─── EN-book gate: English input is a strict no-op ──────────────────────

    [Fact]
    public void EnglishBook_IsNeverTranslated_NoOp()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "The scene builds tension and drives the Action forward.",
            ToneDescription = "High Stakes throughout.",
        };

        var before = Serialize(literary);
        var result = GlossaryRepairPass.Apply(AnalysisType.LiteraryAnalysis, before, "", "en", JsonOpts);

        // The glossary is English->Hebrew; on an English book it MUST NOT fire.
        Assert.Equal(before, result.StructuredJson);
        Assert.Equal(0, result.FieldsChanged);
        Assert.Empty(result.ResidualLatinRuns);
    }

    // ─── Proofread: never repaired ──────────────────────────────────────────

    [Fact]
    public void Proofread_IsNeverRepaired_EvenWithLatinAndHebrewLanguage()
    {
        const string cleanContent = "טקסט מתוקן עם (Action) שנשאר כפי שהוא.";

        var result = GlossaryRepairPass.Apply(AnalysisType.Proofread, structuredJson: null, cleanContent, "he", JsonOpts);

        Assert.Null(result.StructuredJson);
        Assert.Equal(cleanContent, result.CleanContent);
        Assert.Equal(0, result.FieldsChanged);
        Assert.Empty(result.ResidualLatinRuns);
    }

    // ─── LinguisticAnalysis: summary leak repaired ──────────────────────────

    [Fact]
    public void Linguistic_SummaryWithActionLeak_Repaired_MetricAndSpanByteIdentical()
    {
        var linguistic = new LinguisticAnalysisResult
        {
            Summary = "הניתוח מצביע על תבנית פעולה (Action) חוזרת.",
            GrammaticalityScore = 0.9,
            Deviations =
            {
                new StyleDeviation { Metric = "avgSentenceLength", SceneValue = 18.0, ChapterBaseline = 12.0, Note = "משפטים ארוכים" },
            },
            ConsistencyIssues =
            {
                new ConsistencyIssue { Type = "tense", Span = "הלך אל הבית", Description = "מעבר זמנים לא עקבי" },
            },
        };

        var before = Serialize(linguistic);
        var result = GlossaryRepairPass.Apply(AnalysisType.LinguisticAnalysis, before, "", "he", JsonOpts);

        var after = JsonSerializer.Deserialize<LinguisticAnalysisResult>(result.StructuredJson!, JsonOpts)!;

        Assert.Contains("(פעולה)", after.Summary);
        Assert.DoesNotContain("Action", after.Summary);

        // The metric label key and the manuscript-quote span anchor are untouched.
        Assert.Equal("avgSentenceLength", after.Deviations[0].Metric);
        Assert.Equal("הלך אל הבית", after.ConsistencyIssues[0].Span);
        Assert.Equal("tense", after.ConsistencyIssues[0].Type);

        AssertOnlyLeavesChanged(before, result.StructuredJson!, "summary");
    }

    // ─── FIX final-r01: a model-emitted null collection must not throw ───────

    /// <summary>
    /// P0 regression guard (closing-review final-r01): a model emits <c>"rhetoricalDevices": null</c> /
    /// <c>"themes": null</c> ("none"). System.Text.Json overwrites the <c>= new()</c> list with null; before
    /// the fix the ALWAYS-ON Stage-1 pass walked the null list and threw an uncaught NRE that crashed the
    /// WHOLE analysis at the shipped guard-only default. <see cref="GlossaryRepairPass.Apply"/> must now be a
    /// no-throw no-op that returns the input byte-identical. (Without the fix this test throws.)
    /// </summary>
    [Fact]
    public void Literary_NullThemesAndDevices_CleanScalars_DoesNotThrow_ReturnsInputByteIdentical()
    {
        const string json =
            "{\"themes\":null,\"tone\":\"דרמטי\",\"toneDescription\":\"טון מתוח לאורך כל הפרק\"," +
            "\"narrativeVoice\":\"גוף שלישי\",\"narrativeVoiceDescription\":\"מספר יודע-כול\"," +
            "\"rhetoricalDevices\":null,\"moodProgression\":\"עולה בהדרגה\"," +
            "\"summary\":\"הפרק בונה מתח ומגיע לשיא רגשי.\"}";

        var result = GlossaryRepairPass.Apply(AnalysisType.LiteraryAnalysis, json, "", "he", JsonOpts);

        // No throw; clean Hebrew scalars => nothing flagged => input returned byte-identical.
        Assert.Equal(json, result.StructuredJson);
        Assert.Equal(0, result.FieldsChanged);
        Assert.Empty(result.ResidualLatinRuns);
    }

    /// <summary>Same null collections, but a scalar carries a real leak so a repair FIRES and the object is
    /// re-serialized — proving the null lists survive the whole walk + re-serialize with no throw and are not
    /// materialized into empty lists nor dropped.</summary>
    [Fact]
    public void Literary_NullCollections_WithSummaryLeak_RepairsSummary_NoThrow_NullPreserved()
    {
        const string json =
            "{\"themes\":null,\"rhetoricalDevices\":null,\"summary\":\"תיאור פעולה (Action) עז.\"}";

        var result = GlossaryRepairPass.Apply(AnalysisType.LiteraryAnalysis, json, "", "he", JsonOpts);

        Assert.NotNull(result.StructuredJson);
        Assert.Equal(1, result.FieldsChanged);

        var after = JsonSerializer.Deserialize<LiteraryAnalysisResult>(result.StructuredJson!, JsonOpts)!;
        Assert.Contains("(פעולה)", after.Summary);
        Assert.DoesNotContain("Action", after.Summary);
        // The null collections were neither dereferenced nor synthesized into lists.
        Assert.Null(after.Themes);
        Assert.Null(after.RhetoricalDevices);
    }

    [Fact]
    public void Linguistic_NullDeviationsAndConsistencyIssues_DoesNotThrow_ReturnsInputByteIdentical()
    {
        const string json =
            "{\"grammaticalityScore\":0.9,\"summary\":\"טקסט תקין לחלוטין.\"," +
            "\"deviations\":null,\"consistencyIssues\":null}";

        var result = GlossaryRepairPass.Apply(AnalysisType.LinguisticAnalysis, json, "", "he", JsonOpts);

        Assert.Equal(json, result.StructuredJson);
        Assert.Equal(0, result.FieldsChanged);
        Assert.Empty(result.ResidualLatinRuns);
    }

    // ─── Fault channel: null on success/no-op paths (fires only on swallowed exception) ──

    /// <summary>
    /// <see cref="GlossaryRepairResult.Fault"/> is the ONLY signal a caller has that the pass caught
    /// and swallowed an internal fault (it never throws) — <c>ApplyAnalysisRepairAsync</c> logs on it,
    /// otherwise a re-serialize / accessor-walk fault would leave leaked English with no warning. This
    /// guards the other half of that contract: on the normal success AND clean-no-op paths Fault stays
    /// null so it can never false-fire a warning. (The swallowed-exception path itself is the
    /// belt-and-braces catch over a now-fully-null-guarded walk; it has no deterministic public trigger,
    /// so it is exercised by inspection of the catch, not a brittle fault-injection input.)
    /// </summary>
    [Fact]
    public void SuccessAndNoOp_HaveNullFault()
    {
        var repaired = GlossaryRepairPass.Apply(
            AnalysisType.LiteraryAnalysis,
            Serialize(new LiteraryAnalysisResult { Summary = "תיאור פעולה (Action) עז." }),
            "", "he", JsonOpts);
        Assert.Equal(1, repaired.FieldsChanged); // a repair fired...
        Assert.Null(repaired.Fault);             // ...with no fault.

        var clean = GlossaryRepairPass.Apply(
            AnalysisType.LiteraryAnalysis,
            Serialize(new LiteraryAnalysisResult { Summary = "הפרק בונה מתח ומגיע לשיא." }),
            "", "he", JsonOpts);
        Assert.Equal(0, clean.FieldsChanged);    // clean no-op...
        Assert.Null(clean.Fault);                // ...also faultless.
    }

    // ─── Serialization + leaf-diff helpers (mirror RepairableFieldsTests) ────

    private static string Serialize(object instance)
        => JsonSerializer.Serialize(instance, instance.GetType(), JsonOpts);

    /// <summary>Asserts the BEFORE/AFTER JSON have the identical set of leaf paths (no key
    /// renamed/added/dropped) and that EXACTLY <paramref name="expectedChangedPaths"/> changed
    /// value — every other leaf (enums, metric keys, spans, anchors, numerics) is byte-identical.</summary>
    private static void AssertOnlyLeavesChanged(string beforeJson, string afterJson, params string[] expectedChangedPaths)
    {
        var before = FlattenLeaves(beforeJson);
        var after = FlattenLeaves(afterJson);

        Assert.Equal(
            before.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            after.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var changed = before.Keys
            .Where(k => !string.Equals(before[k], after[k], StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedChangedPaths.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            changed);
    }

    private static Dictionary<string, string> FlattenLeaves(string json)
    {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(JsonNode.Parse(json), path: string.Empty, leaves);
        return leaves;
    }

    private static void Walk(JsonNode? node, string path, IDictionary<string, string> leaves)
    {
        switch (node)
        {
            case null:
                leaves[path] = "null";
                break;
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    var childPath = path.Length == 0 ? kv.Key : $"{path}.{kv.Key}";
                    Walk(kv.Value, childPath, leaves);
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    Walk(arr[i], $"{path}[{i}]", leaves);
                }
                break;
            default:
                leaves[path] = node.ToJsonString();
                break;
        }
    }
}
