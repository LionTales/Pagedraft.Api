using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// (A) Scoping-invariant linchpin for the analysis-output repair layer
/// (plan: analysis-output-repair-2026-07-03.plan.md, "Non-regression strategy").
///
/// For each structured result type we populate an instance with distinctive
/// sentinel values in BOTH prose and structural fields, then push an arbitrary
/// transform through <see cref="RepairableFields"/> that appends a marker to EVERY
/// prose accessor. We then prove, at the JSON level, that the set of leaf paths
/// whose value changed equals EXACTLY the set of prose paths — which catches both
/// a missed prose field (a prose path did not change) AND an accidental structural
/// mutation (a non-prose path changed, or a key was renamed). Because Proofread
/// corrected text and LineEdit / linguistic anchors live in structural fields, this
/// guarantees repair can never touch them by construction.
///
/// Deterministic: NO Ollama, NO skip-gate. Runs in CI always.
/// </summary>
public class RepairableFieldsTests
{
    /// <summary>Marker appended to every prose value by the transform under test
    /// (U+220E END-OF-PROOF followed by "REPAIRED"). Distinctive and non-Hebrew so
    /// it is unambiguous in both the live object and the serialized JSON.</summary>
    private const string Marker = "∎REPAIRED";

    /// <summary>Mirror of the analysis pipeline's camelCase JsonOpts
    /// (UnifiedAnalysisService.JsonOpts) so the structural re-serialize+reparse
    /// check sees the exact same wire shape production persists.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ─── LiteraryAnalysisResult ─────────────────────────────────────────────

    [Fact]
    public void Literary_RepairTransform_ChangesEveryProsePath_AndNothingElse()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "SENT_summary",
            Tone = "SENT_tone",
            ToneDescription = "SENT_toneDescription",
            NarrativeVoice = "SENT_narrativeVoice",
            NarrativeVoiceDescription = "SENT_narrativeVoiceDescription",
            MoodProgression = "SENT_moodProgression",
            Themes =
            {
                new ThemeEntry { Name = "SENT_theme0_name", Description = "SENT_theme0_desc", Significance = "major" },
                new ThemeEntry { Name = "SENT_theme1_name", Description = "SENT_theme1_desc", Significance = "minor" },
            },
            RhetoricalDevices =
            {
                new RhetoricalDevice { Name = "SENT_dev0_name", Example = "SENT_dev0_example", Effect = "SENT_dev0_effect" },
                new RhetoricalDevice { Name = "SENT_dev1_name", Example = "SENT_dev1_example", Effect = "SENT_dev1_effect" },
            },
        };

        var accessors = RepairableFields.For(literary);

        AssertProseOnlyMutation(literary, accessors, new[]
        {
            "summary",
            "tone",
            "toneDescription",
            "narrativeVoice",
            "narrativeVoiceDescription",
            "moodProgression",
            "themes[0].name",
            "themes[0].description",
            "themes[1].name",
            "themes[1].description",
            "rhetoricalDevices[0].name",
            "rhetoricalDevices[0].example",
            "rhetoricalDevices[0].effect",
            "rhetoricalDevices[1].name",
            "rhetoricalDevices[1].example",
            "rhetoricalDevices[1].effect",
        });
    }

    // ─── LinguisticAnalysisResult ───────────────────────────────────────────

    [Fact]
    public void Linguistic_RepairTransform_ChangesEveryProsePath_AndNothingElse()
    {
        var linguistic = new LinguisticAnalysisResult
        {
            SyntaxMetrics = new SyntaxMetrics
            {
                SentenceCount = 11,
                AverageSentenceLength = 12.5,
                ComplexSentences = 3,
                ShortestSentence = 4,
                LongestSentence = 29,
            },
            MorphologyMetrics = new MorphologyMetrics
            {
                WordCount = 210,
                UniqueWords = 175,
                AverageWordLength = 4.7,
                LexicalDensity = 0.66,
            },
            StyleMetrics = new StyleMetrics
            {
                Formality = "literary",
                Readability = 55.5,
                VoiceBalance = "passive",
            },
            GrammaticalityScore = 0.91,
            Summary = "SENT_ling_summary",
            Deviations =
            {
                new StyleDeviation { Metric = "avgSentenceLength", SceneValue = 18.2, ChapterBaseline = 12.0, Note = "SENT_dev0_note" },
                new StyleDeviation { Metric = "lexicalDensity", SceneValue = 0.71, ChapterBaseline = 0.63, Note = "SENT_dev1_note" },
            },
            ConsistencyIssues =
            {
                new ConsistencyIssue { Type = "tense", Span = "SENT_span_anchor_0_עברית", Description = "SENT_ci0_description" },
                new ConsistencyIssue { Type = "pov", Span = "SENT_span_anchor_1", Description = "SENT_ci1_description" },
            },
        };

        var accessors = RepairableFields.For(linguistic);

        AssertProseOnlyMutation(linguistic, accessors, new[]
        {
            "summary",
            "deviations[0].note",
            "deviations[1].note",
            "consistencyIssues[0].description",
            "consistencyIssues[1].description",
        });
    }

    // ─── LineEditResult ─────────────────────────────────────────────────────

    [Fact]
    public void LineEdit_RepairTransform_ChangesEveryProsePath_AndNothingElse()
    {
        var lineEdit = new LineEditResult
        {
            OverallFeedback = "SENT_overallFeedback",
            Suggestions =
            {
                new LineEditSuggestion { Original = "SENT_orig0_anchor", Suggested = "SENT_sugg0_anchor", Reason = "SENT_reason0", Category = "clarity" },
                new LineEditSuggestion { Original = "SENT_orig1_anchor", Suggested = "SENT_sugg1_anchor", Reason = "SENT_reason1", Category = "flow" },
            },
        };

        var accessors = RepairableFields.For(lineEdit);

        AssertProseOnlyMutation(lineEdit, accessors, new[]
        {
            "overallFeedback",
            "suggestions[0].reason",
            "suggestions[1].reason",
        });
    }

    // ─── BookReviewResult ───────────────────────────────────────────────────

    [Fact]
    public void BookReview_RepairTransform_ChangesEveryProsePath_AndNothingElse()
    {
        var bookReview = BuildBookReview();

        var accessors = RepairableFields.For(bookReview);

        // Only findings[0] carries a non-null suggestedAction — findings[1]'s null
        // action produces NO accessor (proven byte-identically below: its serialized
        // "findings[1].suggestedAction": null leaf is absent from the changed set).
        AssertProseOnlyMutation(bookReview, accessors, new[]
        {
            "findings[0].rationale",
            "findings[0].suggestedAction",
            "findings[1].rationale",
        });
    }

    [Fact]
    public void BookReview_NullSuggestedAction_ProducesNoAccessor_ButNonNullDoesGetRepaired()
    {
        var bookReview = BuildBookReview();

        var accessors = RepairableFields.For(bookReview);

        // 2 findings: finding[0] => rationale + (non-null) suggestedAction = 2 accessors;
        // finding[1] => rationale only (null action skipped) = 1 accessor. Total 3.
        Assert.Equal(3, accessors.Count);

        foreach (var acc in accessors)
        {
            acc.Set(acc.Get() + Marker);
        }

        // The non-null action was repaired; the null one was never synthesised.
        Assert.EndsWith(Marker, bookReview.Findings[0].SuggestedAction);
        Assert.Null(bookReview.Findings[1].SuggestedAction);
    }

    private static BookReviewResult BuildBookReview() => new()
    {
        Findings =
        {
            new BookFindingItem
            {
                Dimension = "plot",
                Verdict = "improve",
                Severity = 3,
                Rationale = "SENT_finding0_rationale",
                Evidence =
                {
                    new FindingEvidence
                    {
                        ChapterId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        ChapterOrder = 2,
                        Excerpt = "SENT_finding0_evidence_excerpt",
                    },
                },
                ChapterAnchors =
                {
                    new FindingChapterAnchor
                    {
                        ChapterId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        Order = 2,
                        Title = "SENT_finding0_anchor_title",
                    },
                },
                SuggestedAction = "SENT_finding0_suggestedAction",
            },
            new BookFindingItem
            {
                Dimension = "pacing",
                Verdict = "cut",
                Severity = 1,
                Rationale = "SENT_finding1_rationale",
                Evidence =
                {
                    new FindingEvidence
                    {
                        ChapterId = null,
                        ChapterOrder = 5,
                        Excerpt = "SENT_finding1_evidence_excerpt",
                    },
                },
                ChapterAnchors =
                {
                    new FindingChapterAnchor
                    {
                        ChapterId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                        Order = 5,
                        Title = "SENT_finding1_anchor_title",
                    },
                },
                SuggestedAction = null,
            },
        },
        Scores =
        {
            new DimensionScore { Dimension = "plot", Score = "weak", KeepCount = 1, ImproveCount = 2, CutCount = 3 },
            new DimensionScore { Dimension = "pacing", Score = "strong", KeepCount = 4, ImproveCount = 0, CutCount = 1 },
        },
    };

    // ─── AnalysisSuggestion (Proofread) — leave entirely ────────────────────

    [Fact]
    public void AnalysisSuggestion_For_ReturnsNoAccessors_AndLeavesEverythingByteIdentical()
    {
        var suggestion = new AnalysisSuggestion
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            AnalysisResultId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            OriginalText = "SENT_originalText_anchor",
            SuggestedText = "SENT_suggestedText_anchor",
            StartOffset = 12,
            EndOffset = 34,
            Reason = "SENT_reason",
            Category = "SENT_category",
            Explanation = "SENT_explanation",
            OrderIndex = 7,
        };

        var accessors = RepairableFields.For(suggestion);

        Assert.Empty(accessors);

        // With zero accessors the transform is a no-op: the entire serialized shape
        // (offsets, original/suggested anchors, everything) is byte-identical.
        AssertProseOnlyMutation(suggestion, accessors, Array.Empty<string>());
    }

    // ─── ForPlainText (Summarization / whole-text prose) ────────────────────

    [Fact]
    public void ForPlainText_SingleAccessor_GetsValue_AndSetterForwardsToSuppliedDelegate()
    {
        const string original = "SENT_plainText_prose";
        string? written = null;

        var accessors = RepairableFields.ForPlainText(original, v => written = v);

        Assert.Single(accessors);
        Assert.Equal(original, accessors[0].Get());

        accessors[0].Set(accessors[0].Get() + Marker);

        Assert.Equal(original + Marker, written);
    }

    // ─── FIX final-r01: a model-emitted null collection must not throw ───────

    /// <summary>
    /// P0 regression guard (closing-review final-r01). LLMs routinely emit an explicit <c>null</c> for an
    /// empty array (e.g. <c>"themes": null</c>, <c>"rhetoricalDevices": null</c> for "none"), and
    /// System.Text.Json then OVERWRITES the <c>= new()</c> field initializer with <c>null</c>. Before the
    /// null-guard, <see cref="RepairableFields.For(LiteraryAnalysisResult)"/> walked the null list and
    /// threw an NRE out of the ALWAYS-ON Stage-1 glossary pass, crashing the whole analysis at the shipped
    /// guard-only default. It must now return ONLY the scalar prose accessors, with NO throw and no new
    /// field exposed. (Without the fix this test throws before the first assert.)
    /// </summary>
    [Fact]
    public void Literary_NullThemesAndDevices_For_ReturnsOnlyScalarAccessors_NoThrow()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "SENT_summary",
            Tone = "SENT_tone",
            ToneDescription = "SENT_toneDescription",
            NarrativeVoice = "SENT_narrativeVoice",
            NarrativeVoiceDescription = "SENT_narrativeVoiceDescription",
            MoodProgression = "SENT_moodProgression",
            Themes = null!,             // model emitted "themes": null
            RhetoricalDevices = null!,  // model emitted "rhetoricalDevices": null
        };

        var accessors = RepairableFields.For(literary); // must NOT throw

        // Only the 6 scalar prose fields survive; the null lists contribute no accessor.
        Assert.Equal(6, accessors.Count);
        // Every accessor is usable (read + write-back) without throwing.
        foreach (var acc in accessors)
        {
            Assert.NotNull(acc.Get());
            acc.Set(acc.Get() + Marker);
        }
        Assert.EndsWith(Marker, literary.Summary);
    }

    [Fact]
    public void Linguistic_NullDeviationsAndConsistencyIssues_For_ReturnsOnlySummaryAccessor_NoThrow()
    {
        var linguistic = new LinguisticAnalysisResult
        {
            Summary = "SENT_ling_summary",
            Deviations = null!,        // model emitted "deviations": null
            ConsistencyIssues = null!, // model emitted "consistencyIssues": null
        };

        var accessors = RepairableFields.For(linguistic); // must NOT throw

        Assert.Single(accessors); // only summary
        Assert.Equal("SENT_ling_summary", accessors[0].Get());
    }

    [Fact]
    public void LineEdit_NullSuggestions_For_ReturnsOnlyOverallFeedbackAccessor_NoThrow()
    {
        var lineEdit = new LineEditResult
        {
            OverallFeedback = "SENT_overallFeedback",
            Suggestions = null!, // model emitted "suggestions": null
        };

        var accessors = RepairableFields.For(lineEdit); // must NOT throw

        Assert.Single(accessors); // only overallFeedback
        Assert.Equal("SENT_overallFeedback", accessors[0].Get());
    }

    [Fact]
    public void BookReview_NullFindings_For_ReturnsNoAccessors_NoThrow()
    {
        var bookReview = new BookReviewResult
        {
            Findings = null!, // model emitted "findings": null
        };

        var accessors = RepairableFields.For(bookReview); // must NOT throw

        Assert.Empty(accessors);
    }

    /// <summary>A null ELEMENT inside a collection (<c>[null, {...}]</c>) must be skipped, not
    /// dereferenced — the getter/setter closes over the element, so a null element would otherwise throw
    /// when the walk reads it.</summary>
    [Fact]
    public void Literary_NullThemeElement_IsSkipped_OnlyNonNullElementExposed_NoThrow()
    {
        var literary = new LiteraryAnalysisResult
        {
            Themes =
            {
                null!, // model emitted [null, {...}]
                new ThemeEntry { Name = "SENT_theme_name", Description = "SENT_theme_desc", Significance = "major" },
            },
        };

        var accessors = RepairableFields.For(literary); // must NOT throw

        // 6 scalar + 2 from the single non-null theme = 8; the null element contributes nothing.
        Assert.Equal(8, accessors.Count);
        Assert.All(accessors, acc => Assert.NotNull(acc.Get())); // no throw reading any accessor
    }

    // ─── BookOverviewResult (f5-wire) ───────────────────────────────────────

    [Fact]
    public void BookOverview_RepairTransform_ChangesOnlySummary_AndNothingElse()
    {
        var overview = new BookOverviewResult
        {
            Genre = "SENT_genre",
            SubGenre = "SENT_subGenre",
            TargetAudience = "SENT_targetAudience",
            LiteratureLevel = 7,
            EstimatedReadingTimeMinutes = 123,
            LanguageRegister = "SENT_languageRegister",
            Summary = "SENT_summary",
        };

        var accessors = RepairableFields.For(overview);

        // ONLY summary is prose; genre/subGenre/targetAudience/languageRegister (labels) and
        // literatureLevel/estimatedReadingTimeMinutes (numeric) are proven byte-identical.
        AssertProseOnlyMutation(overview, accessors, new[] { "summary" });
    }

    // ─── CharacterAnalysisResult (f5-wire) ───────────────────────────────────

    [Fact]
    public void Character_RepairTransform_ChangesEveryProsePath_AndNothingElse()
    {
        var characters = new CharacterAnalysisResult
        {
            Summary = "SENT_summary",
            Characters =
            {
                new CharacterEntry { Name = "SENT_char0_name", Role = "protagonist", Description = "SENT_char0_desc", Arc = "SENT_char0_arc", FirstAppearanceChapter = 1 },
                new CharacterEntry { Name = "SENT_char1_name", Role = "antagonist", Description = "SENT_char1_desc", Arc = "SENT_char1_arc", FirstAppearanceChapter = null },
            },
            Relationships =
            {
                new CharacterRelationship { Character1 = "SENT_rel0_c1", Character2 = "SENT_rel0_c2", Relationship = "SENT_rel0_relationship" },
                new CharacterRelationship { Character1 = "SENT_rel1_c1", Character2 = "SENT_rel1_c2", Relationship = "SENT_rel1_relationship" },
            },
        };

        var accessors = RepairableFields.For(characters);

        AssertProseOnlyMutation(characters, accessors, new[]
        {
            "summary",
            "characters[0].description",
            "characters[0].arc",
            "characters[1].description",
            "characters[1].arc",
            "relationships[0].relationship",
            "relationships[1].relationship",
        });
    }

    [Fact]
    public void Character_NullCharactersAndRelationships_For_ReturnsOnlySummaryAccessor_NoThrow()
    {
        var characters = new CharacterAnalysisResult
        {
            Summary = "SENT_summary",
            Characters = null!,      // model emitted "characters": null
            Relationships = null!,   // model emitted "relationships": null
        };

        var accessors = RepairableFields.For(characters); // must NOT throw

        Assert.Single(accessors); // only summary
        Assert.Equal("SENT_summary", accessors[0].Get());
    }

    [Fact]
    public void Character_NullCharacterElement_IsSkipped_OnlyNonNullElementExposed_NoThrow()
    {
        var characters = new CharacterAnalysisResult
        {
            Summary = "SENT_summary",
            Characters =
            {
                null!, // model emitted [null, {...}]
                new CharacterEntry { Name = "שם", Role = "supporting", Description = "SENT_desc", Arc = "SENT_arc" },
            },
        };

        var accessors = RepairableFields.For(characters); // must NOT throw

        // summary + (description, arc) from the single non-null character = 3; the null element contributes nothing.
        Assert.Equal(3, accessors.Count);
        Assert.All(accessors, acc => Assert.NotNull(acc.Get()));
    }

    // ─── StoryAnalysisResult (f5-wire) ───────────────────────────────────────

    [Fact]
    public void Story_RepairTransform_ChangesEveryProsePath_AndNothingElse()
    {
        var story = new StoryAnalysisResult
        {
            PlotStructure = new PlotStructure
            {
                Setup = "SENT_setup",
                RisingAction = "SENT_risingAction",
                Climax = "SENT_climax",
                FallingAction = "SENT_fallingAction",
                Resolution = "SENT_resolution",
            },
            Pacing = "SENT_pacing",
            Conflicts =
            {
                new ConflictEntry { Type = "internal", Description = "SENT_conflict0_desc", Status = "resolved" },
                new ConflictEntry { Type = "external", Description = "SENT_conflict1_desc", Status = "ongoing" },
            },
            Summary = "SENT_summary",
        };

        var accessors = RepairableFields.For(story);

        AssertProseOnlyMutation(story, accessors, new[]
        {
            "plotStructure.setup",
            "plotStructure.risingAction",
            "plotStructure.climax",
            "plotStructure.fallingAction",
            "plotStructure.resolution",
            "pacing",
            "summary",
            "conflicts[0].description",
            "conflicts[1].description",
        });
    }

    [Fact]
    public void Story_NullPlotStructureAndConflicts_For_ReturnsOnlyPacingAndSummary_NoThrow()
    {
        var story = new StoryAnalysisResult
        {
            PlotStructure = null!, // model emitted "plotStructure": null
            Pacing = "SENT_pacing",
            Summary = "SENT_summary",
            Conflicts = null!,     // model emitted "conflicts": null
        };

        var accessors = RepairableFields.For(story); // must NOT throw

        // plotStructure null => no subfield accessors; conflicts null => no walk; only pacing + summary remain.
        Assert.Equal(2, accessors.Count);
        foreach (var acc in accessors)
        {
            Assert.NotNull(acc.Get());
            acc.Set(acc.Get() + Marker);
        }
        Assert.EndsWith(Marker, story.Pacing);
        Assert.EndsWith(Marker, story.Summary);
    }

    // ─── QAResult (f5-wire) ──────────────────────────────────────────────────

    [Fact]
    public void QA_RepairTransform_ChangesOnlyAnswer_AndNothingElse()
    {
        var qa = new QAResult
        {
            Answer = "SENT_answer",
            Citations =
            {
                new ChapterCitation { ChapterNumber = 3, ChapterTitle = "SENT_cit0_title", RelevantExcerpt = "SENT_cit0_excerpt" },
                new ChapterCitation { ChapterNumber = 5, ChapterTitle = "SENT_cit1_title", RelevantExcerpt = "SENT_cit1_excerpt" },
            },
            Confidence = "high",
        };

        var accessors = RepairableFields.For(qa);

        // ONLY answer is prose; citation numbers/titles/excerpts (anchors) and confidence (enum) are
        // proven byte-identical.
        AssertProseOnlyMutation(qa, accessors, new[] { "answer" });
    }

    [Fact]
    public void QA_NullCitations_For_ReturnsOnlyAnswerAccessor_NoThrow()
    {
        var qa = new QAResult
        {
            Answer = "SENT_answer",
            Citations = null!, // model emitted "citations": null — not walked, but must not disturb the answer accessor
        };

        var accessors = RepairableFields.For(qa); // must NOT throw

        Assert.Single(accessors); // only answer
        Assert.Equal("SENT_answer", accessors[0].Get());
    }

    // ─── Invariant harness ──────────────────────────────────────────────────

    /// <summary>
    /// The airtight structural-invariance assertion. Serializes <paramref name="instance"/>
    /// (BEFORE), pushes the marker-append transform through every accessor, serializes
    /// again (AFTER), then asserts at the JSON-leaf level that:
    ///   1. accessor count == expected prose-path count;
    ///   2. every accessor's current value ends with the marker (prose actually changed);
    ///   3. the BEFORE and AFTER JSON have the identical set of leaf paths (no key
    ///      renamed / added / dropped);
    ///   4. the set of leaf paths whose VALUE changed equals EXACTLY <paramref name="expectedProsePaths"/>.
    /// (4) is the linchpin: a missed prose field leaves its path unchanged (missing from
    /// the changed set) and an accidental structural mutation adds a non-prose path — either
    /// fails the exact set-equality. Every non-prose leaf (keys, enums, metric, span,
    /// original/suggested, offsets, severity, evidence, chapterAnchors, numeric metrics)
    /// is therefore proven byte-identical.
    /// </summary>
    private static void AssertProseOnlyMutation(
        object instance,
        IReadOnlyList<RepairableField> accessors,
        string[] expectedProsePaths)
    {
        // Serialize by RUNTIME type — the generic overload would bind TValue to `object`
        // and emit `{}`.
        string beforeJson = JsonSerializer.Serialize(instance, instance.GetType(), JsonOpts);

        Assert.Equal(expectedProsePaths.Length, accessors.Count);

        foreach (var acc in accessors)
        {
            acc.Set(acc.Get() + Marker);
        }

        // (2) every prose accessor's live value now ends with the marker.
        Assert.All(accessors, acc => Assert.EndsWith(Marker, acc.Get()));

        string afterJson = JsonSerializer.Serialize(instance, instance.GetType(), JsonOpts);

        var beforeLeaves = FlattenLeaves(beforeJson);
        var afterLeaves = FlattenLeaves(afterJson);

        // Sanity: the shape must contain structural leaves beyond the prose ones, else
        // the invariant would be vacuous.
        Assert.True(
            beforeLeaves.Count >= expectedProsePaths.Length,
            $"Expected at least {expectedProsePaths.Length} leaves, found {beforeLeaves.Count}.");

        // (3) key sets identical — no key renamed, added, or dropped.
        Assert.Equal(
            beforeLeaves.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            afterLeaves.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // (4) exactly the prose paths changed value; every other leaf is byte-identical.
        var changedPaths = beforeLeaves.Keys
            .Where(k => !string.Equals(beforeLeaves[k], afterLeaves[k], StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedProsePaths.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            changedPaths);

        // Belt-and-braces: the marker really landed in the serialized prose values.
        foreach (var prosePath in expectedProsePaths)
        {
            Assert.True(afterLeaves.ContainsKey(prosePath), $"Missing prose leaf: {prosePath}");
            Assert.Contains("REPAIRED", afterLeaves[prosePath]);
        }
    }

    /// <summary>Flattens a JSON document to a map of leaf-path -&gt; exact JSON token
    /// text (e.g. "themes[0].name" -&gt; "\"…\"", "severity" -&gt; "3",
    /// "suggestedAction" -&gt; "null"). Object/array containers are not leaves.</summary>
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
            default: // JsonValue — a scalar leaf. ToJsonString() is byte-exact.
                leaves[path] = node.ToJsonString();
                break;
        }
    }
}
