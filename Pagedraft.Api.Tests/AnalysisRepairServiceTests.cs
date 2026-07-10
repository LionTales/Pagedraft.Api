using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic (fake <see cref="IAiRouter"/>, NO Ollama / NO skip-gate) proof that the wired LLM repair
/// stage (<see cref="AnalysisRepairService"/>) is SAFE — plan analysis-output-repair-2026-07-03, todo
/// p3-tests, "Non-regression strategy". Four groups:
///
///   (1) SCOPING INVARIANT — after a repair that changes EVERY flagged prose field, the set of JSON leaf
///       paths whose value changed equals EXACTLY the prose paths; every structural leaf (keys, enums,
///       metric, span, original/suggested, offsets, severity, numerics) is byte-identical. Reuses the
///       airtight leaf-diff form from <see cref="RepairableFieldsTests"/> / <see cref="GlossaryRepairPassTests"/>,
///       but drives the REAL service through a fake router.
///   (2) FAIL-SAFE — a fake router returning empty / English-still-present / wildly-different-length, and a
///       router that THROWS, are each discarded and the ORIGINAL value kept (structured JSON byte-identical).
///   (3) RE-SERIALIZATION FIDELITY — an accepted repair still deserializes to the same typed shape with an
///       identical key set and preserved list lengths.
///   (4) GUARD-GATING — clean Hebrew input, Proofread, and a non-Hebrew language each make ZERO model calls
///       via the fake router and return byte-identical output.
///
/// The fake router is a hand-written stub (<see cref="FakeAiRouter"/>) so the call count and the per-call
/// response (including a throw) are fully under test control.
/// </summary>
public class AnalysisRepairServiceTests
{
    /// <summary>Mirror of the pipeline's camelCase JsonOpts (UnifiedAnalysisService.JsonOpts) so the
    /// (de)serialize round-trip and the leaf-diff see the exact wire shape production persists.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ─── Fake IAiRouter ─────────────────────────────────────────────────────

    /// <summary>
    /// Hand-written <see cref="IAiRouter"/> stub: records how many times <see cref="CompleteAsync"/> was
    /// invoked and returns a caller-supplied string as <see cref="AiResponse.Content"/>. The responder may
    /// THROW to simulate a router error/timeout (the call is still counted — it WAS made — before the throw
    /// propagates, exactly as a real faulted call would surface to <see cref="AnalysisRepairService"/>).
    /// </summary>
    private sealed class FakeAiRouter : IAiRouter
    {
        private readonly Func<AiRequest, string> _respond;

        public int CallCount { get; private set; }
        public List<AiRequest> Requests { get; } = new();

        public FakeAiRouter(Func<AiRequest, string> respond) => _respond = respond;

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            var content = _respond(request); // may throw -> propagates into the service's fail-safe catch
            return Task.FromResult(new AiResponse
            {
                Content = content,
                Provider = "fake",
                Model = "fake-model"
            });
        }

        public IAsyncEnumerable<string> StreamCompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("AnalysisRepairService never streams.");
    }

    /// <summary>Router that returns, for each field, a valid Hebrew replacement of the SAME length as the
    /// input — pure Hebrew (predominantly Hebrew), no Latin runs (⊆ input's set), length ratio 1.0 — so
    /// every flagged field's repair is ACCEPTED.</summary>
    private static FakeAiRouter ValidHebrewRouter() => new(req => HebrewOfLength(req.InputText.Length));

    /// <summary>Router that must never be invoked; throws loudly if it is (belt-and-braces alongside the
    /// CallCount==0 assertion in the guard-gating tests).</summary>
    private static FakeAiRouter NeverCalledRouter() => new(_ => throw new InvalidOperationException("router must not be called"));

    private static AnalysisRepairService NewService(IAiRouter router)
        => new(router, NullLogger<AnalysisRepairService>.Instance);

    // ─── (1) Scoping invariant: byte-identity of every structural leaf ──────

    [Fact]
    public async Task Scoping_Literary_RepairChangesEveryProseLeaf_AndNoStructuralLeaf()
    {
        // Every PROSE field carries a Latin leak (so the guard fires); enums (significance) are distinctive
        // structural sentinels that must survive byte-identical.
        var literary = new LiteraryAnalysisResult
        {
            Summary = "תקציר עם leak Action",
            Tone = "טון Suspense",
            ToneDescription = "תיאור tone Foreshadowing",
            NarrativeVoice = "קול Narrator",
            NarrativeVoiceDescription = "תיאור voice Omniscient",
            MoodProgression = "מצב Mood",
            Themes =
            {
                new ThemeEntry { Name = "נושא Power theme", Description = "תיאור theme Alpha", Significance = "major" },
                new ThemeEntry { Name = "נושא Loss theme", Description = "תיאור theme Beta", Significance = "minor" },
            },
            RhetoricalDevices =
            {
                new RhetoricalDevice { Name = "אמצעי Metaphor", Example = "דוגמה Example one", Effect = "אפקט Effect one" },
                new RhetoricalDevice { Name = "אמצעי Simile", Example = "דוגמה Example two", Effect = "אפקט Effect two" },
            },
        };

        var beforeJson = Serialize(literary);
        var router = ValidHebrewRouter();

        var (structuredJson, cleanContent) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.LiteraryAnalysis, beforeJson, cleanContent: "", "he-IL", JsonOpts);

        Assert.Equal("", cleanContent); // structured type: cleanContent passes through untouched
        Assert.NotNull(structuredJson);

        var expectedProsePaths = new[]
        {
            "summary", "tone", "toneDescription", "narrativeVoice", "narrativeVoiceDescription", "moodProgression",
            "themes[0].name", "themes[0].description", "themes[1].name", "themes[1].description",
            "rhetoricalDevices[0].name", "rhetoricalDevices[0].example", "rhetoricalDevices[0].effect",
            "rhetoricalDevices[1].name", "rhetoricalDevices[1].example", "rhetoricalDevices[1].effect",
        };

        // Exactly one model call per flagged prose field — nothing structural was ever sent.
        Assert.Equal(expectedProsePaths.Length, router.CallCount);

        // Linchpin: EXACTLY the prose leaves changed; every structural leaf (enums + keys) is byte-identical.
        AssertOnlyLeavesChanged(beforeJson, structuredJson!, expectedProsePaths);
    }

    [Fact]
    public async Task Scoping_Linguistic_RepairChangesEveryProseLeaf_AndNoStructuralLeaf()
    {
        // consistencyIssues[0].span deliberately CONTAINS Latin, yet it is a structural anchor (not in the
        // whitelist): it must never be sent to the model nor changed — proving the WHITELIST (not merely the
        // Latin guard) protects anchors.
        var linguistic = new LinguisticAnalysisResult
        {
            SyntaxMetrics = new SyntaxMetrics { SentenceCount = 11, AverageSentenceLength = 12.5, ComplexSentences = 3, ShortestSentence = 4, LongestSentence = 29 },
            MorphologyMetrics = new MorphologyMetrics { WordCount = 210, UniqueWords = 175, AverageWordLength = 4.7, LexicalDensity = 0.66 },
            StyleMetrics = new StyleMetrics { Formality = "literary", Readability = 55.5, VoiceBalance = "passive" },
            GrammaticalityScore = 0.91,
            Summary = "תקציר summary Action",
            Deviations =
            {
                new StyleDeviation { Metric = "avgSentenceLength", SceneValue = 18.2, ChapterBaseline = 12.0, Note = "הערה note Alpha" },
                new StyleDeviation { Metric = "lexicalDensity", SceneValue = 0.71, ChapterBaseline = 0.63, Note = "הערה note Beta" },
            },
            ConsistencyIssues =
            {
                new ConsistencyIssue { Type = "tense", Span = "SENT span Anchor Latin", Description = "תיאור description Gamma" },
                new ConsistencyIssue { Type = "pov", Span = "עוגן בעברית", Description = "תיאור description Delta" },
            },
        };

        var beforeJson = Serialize(linguistic);
        var router = ValidHebrewRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.LinguisticAnalysis, beforeJson, cleanContent: "", "he-IL", JsonOpts);

        Assert.NotNull(structuredJson);

        var expectedProsePaths = new[]
        {
            "summary",
            "deviations[0].note", "deviations[1].note",
            "consistencyIssues[0].description", "consistencyIssues[1].description",
        };

        // 5 flagged prose fields — the Latin-bearing span is NOT one of them (structural anchor).
        Assert.Equal(expectedProsePaths.Length, router.CallCount);

        AssertOnlyLeavesChanged(beforeJson, structuredJson!, expectedProsePaths);
    }

    // ─── (2) Fail-safe: bad / throwing model output is discarded ────────────

    [Fact]
    public Task FailSafe_EmptyModelOutput_KeepsOriginal()
        => AssertSingleFlaggedFieldKeptOriginal(_ => "");

    [Fact]
    public Task FailSafe_ModelOutputWithNewLatinRun_KeepsOriginal()
        // Predominantly Hebrew and length-in-band, but introduces a NEW Latin run ("Zorptext") absent from
        // the input's run set {leak, Action} -> rejected specifically by the no-new-Latin guard.
        => AssertSingleFlaggedFieldKeptOriginal(_ => "תקציר עם Zorptext כאן");

    [Fact]
    public Task FailSafe_WildlyDifferentLength_KeepsOriginal()
        // Pure Hebrew, no new Latin, but 5x the input length -> rejected by the length-ratio guard.
        => AssertSingleFlaggedFieldKeptOriginal(req => HebrewOfLength(req.InputText.Length * 5));

    [Fact]
    public Task FailSafe_RouterThrows_KeepsOriginal_NeverThrowsOut()
        // A router error/timeout must be caught internally — the service NEVER throws out.
        => AssertSingleFlaggedFieldKeptOriginal(_ => throw new InvalidOperationException("router boom"));

    /// <summary>Drives a single flagged prose field through a "bad" router and proves the original is kept:
    /// exactly one model call is made, its output is rejected, and (because nothing changed) the whole
    /// structured JSON — flagged field and all — is byte-identical to the input.</summary>
    private static async Task AssertSingleFlaggedFieldKeptOriginal(Func<AiRequest, string> badResponder)
    {
        const string flaggedSummary = "תקציר עם leak Action";
        var literary = new LiteraryAnalysisResult
        {
            Summary = flaggedSummary,          // the ONLY field with Latin -> the ONLY flagged field
            Tone = "טון נוגה",                  // the rest are clean Hebrew -> guard skips (no model call)
            ToneDescription = "תיאור טון עברי",
            NarrativeVoice = "גוף שלישי",
            NarrativeVoiceDescription = "מספר יודע כול",
            MoodProgression = "עולה בהדרגה",
            Themes = { new ThemeEntry { Name = "כוח", Description = "מוטיב חוזר", Significance = "major" } },
            RhetoricalDevices = { new RhetoricalDevice { Name = "מטאפורה", Example = "האור שבר את החושך", Effect = "מדגיש תקווה" } },
        };

        var beforeJson = Serialize(literary);
        var router = new FakeAiRouter(badResponder);

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.LiteraryAnalysis, beforeJson, cleanContent: "", "he-IL", JsonOpts);

        // Exactly one flagged field => exactly one model call...
        Assert.Equal(1, router.CallCount);
        // ...whose output was rejected => the ENTIRE structured JSON is byte-identical (changed==false path
        // returns the original json; the flagged field AND every other field are intact).
        Assert.Equal(beforeJson, structuredJson);

        var after = JsonSerializer.Deserialize<LiteraryAnalysisResult>(structuredJson!, JsonOpts)!;
        Assert.Equal(flaggedSummary, after.Summary); // original kept verbatim
    }

    // ─── (3) Re-serialization fidelity ──────────────────────────────────────

    [Fact]
    public async Task ReSerialization_AcceptedRepair_StillDeserializesToSameTypedShape_WithIdenticalKeys()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "תקציר עם leak Action",
            Tone = "טון Suspense",
            ToneDescription = "תיאור tone Foreshadowing",
            NarrativeVoice = "קול Narrator",
            NarrativeVoiceDescription = "תיאור voice Omniscient",
            MoodProgression = "מצב Mood",
            Themes =
            {
                new ThemeEntry { Name = "נושא Power theme", Description = "תיאור theme Alpha", Significance = "major" },
                new ThemeEntry { Name = "נושא Loss theme", Description = "תיאור theme Beta", Significance = "minor" },
            },
            RhetoricalDevices =
            {
                new RhetoricalDevice { Name = "אמצעי Metaphor", Example = "דוגמה Example one", Effect = "אפקט Effect one" },
                new RhetoricalDevice { Name = "אמצעי Simile", Example = "דוגמה Example two", Effect = "אפקט Effect two" },
            },
        };

        var beforeJson = Serialize(literary);
        var router = ValidHebrewRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.LiteraryAnalysis, beforeJson, cleanContent: "", "he-IL", JsonOpts);

        Assert.NotNull(structuredJson);

        // Deserializes cleanly back to the SAME typed shape.
        var after = JsonSerializer.Deserialize<LiteraryAnalysisResult>(structuredJson!, JsonOpts);
        Assert.NotNull(after);

        // List lengths preserved (no element dropped or added).
        Assert.Equal(literary.Themes.Count, after!.Themes.Count);
        Assert.Equal(literary.RhetoricalDevices.Count, after.RhetoricalDevices.Count);

        // Structural enum values survive the round-trip untouched.
        Assert.Equal("major", after.Themes[0].Significance);
        Assert.Equal("minor", after.Themes[1].Significance);

        // The repair DID land (English gone, now Hebrew) — proving the shape held despite a real change.
        Assert.DoesNotContain("Action", after.Summary);
        Assert.NotEqual(literary.Summary, after.Summary);

        // Identical KEY SET: re-serialize the deserialized shape and compare leaf keys to the input's
        // (no dropped/renamed/added key).
        var beforeKeys = FlattenLeaves(beforeJson).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var afterKeys = FlattenLeaves(Serialize(after)).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(beforeKeys, afterKeys);
    }

    // ─── (4) Guard-gating: ZERO model calls on clean / non-target input ─────

    [Fact]
    public async Task GuardGating_CleanHebrewStructured_MakesZeroModelCalls_AndIsByteIdentical()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "הפרק בונה מתח הדרגתי ומגיע לשיא רגשי מרשים.",
            Tone = "נוגה",
            ToneDescription = "טון מתוח לאורך הפרק",
            NarrativeVoice = "גוף שלישי",
            NarrativeVoiceDescription = "מספר יודע כול",
            MoodProgression = "עולה בהדרגה",
            Themes = { new ThemeEntry { Name = "אובדן", Description = "נוכח בכל הפרק", Significance = "minor" } },
            RhetoricalDevices = { new RhetoricalDevice { Name = "מטאפורה", Example = "האור שבר את החושך", Effect = "מדגיש תקווה" } },
        };

        var beforeJson = Serialize(literary);
        var router = NeverCalledRouter();

        var (structuredJson, cleanContent) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.LiteraryAnalysis, beforeJson, cleanContent: "PASSTHROUGH", "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount);          // clean Hebrew -> guard skips every field
        Assert.Equal(beforeJson, structuredJson);   // byte-identical (never re-serialized)
        Assert.Equal("PASSTHROUGH", cleanContent);
    }

    [Fact]
    public async Task GuardGating_Proofread_WithHebrewLanguageAndLatin_MakesZeroModelCalls_Unchanged()
    {
        const string content = "טקסט מתוקן עם leak Action שנשאר כפי שהוא.";
        var router = NeverCalledRouter();

        var (structuredJson, cleanContent) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.Proofread, structuredJson: null, content, "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount); // Proofread is never repaired, even with Latin + Hebrew language
        Assert.Null(structuredJson);
        Assert.Equal(content, cleanContent);
    }

    [Fact]
    public async Task GuardGating_NonHebrewLanguage_WithLatinProse_MakesZeroModelCalls_ByteIdentical()
    {
        var literary = new LiteraryAnalysisResult
        {
            Summary = "The scene builds tension and drives the Action forward.",
            ToneDescription = "High Stakes throughout.",
        };

        var beforeJson = Serialize(literary);
        var router = NeverCalledRouter();

        var (structuredJson, cleanContent) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.LiteraryAnalysis, beforeJson, cleanContent: "clean-content", "en", JsonOpts);

        Assert.Equal(0, router.CallCount);        // non-Hebrew book -> strict no-op before any field walk
        Assert.Equal(beforeJson, structuredJson);
        Assert.Equal("clean-content", cleanContent);
    }

    [Fact]
    public async Task GuardGating_SummarizationCleanHebrew_MakesZeroModelCalls_ByteIdenticalContent()
    {
        const string content = "סיכום הפרק בונה מתח ומגיע לשיא רגשי מרשים.";
        var router = NeverCalledRouter();

        var (structuredJson, cleanContent) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.Summarization, structuredJson: null, content, "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount); // clean whole-text -> the ForPlainText guard skips the model
        Assert.Null(structuredJson);
        Assert.Equal(content, cleanContent);
    }

    // ─── FIX final-r01: null model-emitted collections make zero calls, no throw ─

    /// <summary>
    /// P0 regression guard (closing-review final-r01): a model emits <c>"themes": null</c> /
    /// <c>"rhetoricalDevices": null</c>. The wired repair (walking <see cref="RepairableFields"/>) must not
    /// throw and — with clean Hebrew scalars — make ZERO model calls, returning the input byte-identical.
    /// (End-to-end fail-safe: even before the RepairableFields null-guard, the service's own catch-all keeps
    /// this from throwing; this test locks the observable contract — no calls, unchanged output.)
    /// </summary>
    [Fact]
    public async Task NullCollections_Literary_CleanScalars_DoesNotThrow_MakesZeroModelCalls()
    {
        const string json =
            "{\"themes\":null,\"tone\":\"נוגה\",\"toneDescription\":\"טון מתוח\"," +
            "\"narrativeVoice\":\"גוף שלישי\",\"narrativeVoiceDescription\":\"מספר יודע כול\"," +
            "\"rhetoricalDevices\":null,\"moodProgression\":\"עולה\"," +
            "\"summary\":\"הפרק בונה מתח ומגיע לשיא.\"}";
        var router = NeverCalledRouter();

        var (structuredJson, cleanContent) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.LiteraryAnalysis, json, cleanContent: "", "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount);   // null lists + clean Hebrew => nothing flagged
        Assert.Equal(json, structuredJson);  // byte-identical (never re-serialized)
        Assert.Equal("", cleanContent);
    }

    [Fact]
    public async Task NullCollections_Linguistic_CleanScalars_DoesNotThrow_MakesZeroModelCalls()
    {
        const string json =
            "{\"grammaticalityScore\":0.9,\"summary\":\"טקסט תקין לחלוטין.\"," +
            "\"deviations\":null,\"consistencyIssues\":null}";
        var router = NeverCalledRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.LinguisticAnalysis, json, cleanContent: "", "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount);
        Assert.Equal(json, structuredJson);
    }

    // ─── (f5-wire) Scoping invariant for the newly-wired book-level types ────

    [Fact]
    public async Task Scoping_Character_RepairChangesEveryProseLeaf_AndNoStructuralLeaf()
    {
        // Every PROSE field carries a Latin leak (guard fires). Name/role/character refs also contain Latin
        // but are NOT whitelisted, so they are never sent to the model nor changed — proving the whitelist
        // (not merely the Latin guard) protects proper-noun references + the role enum.
        var characters = new CharacterAnalysisResult
        {
            Summary = "תקציר summary Action",
            Characters =
            {
                new CharacterEntry { Name = "שם Alpha", Role = "protagonist", Description = "תיאור description One", Arc = "קשת arc One", FirstAppearanceChapter = 1 },
                new CharacterEntry { Name = "שם Beta", Role = "antagonist", Description = "תיאור description Two", Arc = "קשת arc Two", FirstAppearanceChapter = 2 },
            },
            Relationships =
            {
                new CharacterRelationship { Character1 = "שם Gamma", Character2 = "שם Delta", Relationship = "יחס relationship One" },
            },
        };

        var beforeJson = Serialize(characters);
        var router = ValidHebrewRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.CharacterAnalysis, beforeJson, cleanContent: "", "he-IL", JsonOpts);

        Assert.NotNull(structuredJson);

        var expectedProsePaths = new[]
        {
            "summary",
            "characters[0].description", "characters[0].arc",
            "characters[1].description", "characters[1].arc",
            "relationships[0].relationship",
        };

        Assert.Equal(expectedProsePaths.Length, router.CallCount); // one call per flagged prose field only
        AssertOnlyLeavesChanged(beforeJson, structuredJson!, expectedProsePaths);
    }

    [Fact]
    public async Task Scoping_Story_RepairChangesEveryProseLeaf_AndNoStructuralLeaf()
    {
        // Conflict type/status carry Latin but are structural (not whitelisted): never sent nor changed.
        var story = new StoryAnalysisResult
        {
            PlotStructure = new PlotStructure
            {
                Setup = "פתיחה setup Alpha",
                RisingAction = "עלייה rising Beta",
                Climax = "שיא climax Gamma",
                FallingAction = "ירידה falling Delta",
                Resolution = "סיום resolution Epsilon",
            },
            Pacing = "קצב pacing Zeta",
            Conflicts =
            {
                new ConflictEntry { Type = "internal", Description = "קונפליקט description One", Status = "unresolved" },
                new ConflictEntry { Type = "external", Description = "קונפליקט description Two", Status = "ongoing" },
            },
            Summary = "תקציר summary Eta",
        };

        var beforeJson = Serialize(story);
        var router = ValidHebrewRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.StoryAnalysis, beforeJson, cleanContent: "", "he-IL", JsonOpts);

        Assert.NotNull(structuredJson);

        var expectedProsePaths = new[]
        {
            "plotStructure.setup", "plotStructure.risingAction", "plotStructure.climax",
            "plotStructure.fallingAction", "plotStructure.resolution",
            "pacing", "summary",
            "conflicts[0].description", "conflicts[1].description",
        };

        Assert.Equal(expectedProsePaths.Length, router.CallCount);
        AssertOnlyLeavesChanged(beforeJson, structuredJson!, expectedProsePaths);
    }

    [Fact]
    public async Task GuardGating_CleanHebrew_BookOverview_MakesZeroModelCalls_ByteIdentical()
    {
        var overview = new BookOverviewResult
        {
            Genre = "פנטזיה",
            SubGenre = "פנטזיה אפית",
            TargetAudience = "מבוגרים",
            LiteratureLevel = 5,
            EstimatedReadingTimeMinutes = 120,
            LanguageRegister = "ספרותי",
            Summary = "הספר בונה עולם עשיר ומגיע לשיא מרשים.",
        };

        var beforeJson = Serialize(overview);
        var router = NeverCalledRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.BookOverview, beforeJson, cleanContent: "", "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount);        // clean Hebrew summary => guard skips (label/numeric never walked)
        Assert.Equal(beforeJson, structuredJson); // byte-identical (never re-serialized)
    }

    [Fact]
    public async Task GuardGating_CleanHebrew_QA_MakesZeroModelCalls_ByteIdentical()
    {
        var qa = new QAResult
        {
            Answer = "התשובה נמצאת בפרק השלישי ומסבירה את המניע של הדמות.",
            Citations = { new ChapterCitation { ChapterNumber = 3, ChapterTitle = "הפרק השלישי", RelevantExcerpt = "ציטוט" } },
            Confidence = "high",
        };

        var beforeJson = Serialize(qa);
        var router = NeverCalledRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.QA, beforeJson, cleanContent: "", "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount);        // clean Hebrew answer => guard skips (citations never walked)
        Assert.Equal(beforeJson, structuredJson);
    }

    [Fact]
    public async Task NullCollections_Character_CleanScalars_DoesNotThrow_MakesZeroModelCalls()
    {
        const string json =
            "{\"characters\":null,\"relationships\":null,\"summary\":\"הפרק בונה מתח ומגיע לשיא.\"}";
        var router = NeverCalledRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.CharacterAnalysis, json, cleanContent: "", "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount);  // null lists + clean Hebrew => nothing flagged
        Assert.Equal(json, structuredJson); // byte-identical (never re-serialized)
    }

    [Fact]
    public async Task NullPlotStructure_Story_CleanScalars_DoesNotThrow_MakesZeroModelCalls()
    {
        const string json =
            "{\"plotStructure\":null,\"pacing\":\"קצב מתון\",\"conflicts\":null,\"summary\":\"סיכום תקין.\"}";
        var router = NeverCalledRouter();

        var (structuredJson, _) = await NewService(router).RepairAnalysisAsync(
            AnalysisType.StoryAnalysis, json, cleanContent: "", "he-IL", JsonOpts);

        Assert.Equal(0, router.CallCount);  // null nested plotStructure + null conflicts + clean scalars => nothing flagged
        Assert.Equal(json, structuredJson);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>The Hebrew letters (U+05D0–U+05EA); used to synthesize a pure-Hebrew replacement of an
    /// exact length. Every char is matched by the service's Hebrew-letter guard and none is Latin.</summary>
    private const string HebrewAlphabet = "אבגדהוזחטיכלמנסעפצקרשת";

    /// <summary>A contiguous, pure-Hebrew string of exactly <paramref name="n"/> characters (deterministic,
    /// no whitespace / Latin / CJK, so SanitizeResponse leaves it length-preserved).</summary>
    private static string HebrewOfLength(int n)
    {
        if (n <= 0) return "א";
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append(HebrewAlphabet[i % HebrewAlphabet.Length]);
        return sb.ToString();
    }

    private static string Serialize(object instance)
        => JsonSerializer.Serialize(instance, instance.GetType(), JsonOpts);

    /// <summary>Asserts the BEFORE/AFTER JSON have the identical set of leaf paths (no key
    /// renamed/added/dropped) and that EXACTLY <paramref name="expectedChangedPaths"/> changed value —
    /// every other leaf (enums, metric keys, spans, numerics) is byte-identical. Mirrors the
    /// <see cref="GlossaryRepairPassTests"/> / <see cref="RepairableFieldsTests"/> linchpin.</summary>
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
            default: // JsonValue — a scalar leaf. ToJsonString() is byte-exact.
                leaves[path] = node.ToJsonString();
                break;
        }
    }
}
