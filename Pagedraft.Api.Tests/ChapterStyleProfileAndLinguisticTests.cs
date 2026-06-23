using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for ChapterStyleProfile caching behaviour, LinguisticAnalysisResult JSON round-trip,
/// and book-wide comparison wiring added in plan a6.
/// </summary>
public class ChapterStyleProfileAndLinguisticTests
{
    // ─── JSON opts that mirror what AnalysisContextService / UnifiedAnalysisService use ────────

    /// <summary>
    /// Options used by TryExtractAndReserialize / ComputeChapterLinguisticMetricsAsync for
    /// deserialising the LLM response: case-insensitive + camelCase naming policy.
    /// </summary>
    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Options used when serialising the MetricsJson blob stored on ChapterStyleProfile.
    /// </summary>
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // The active LinguisticAnalysis model the freshness gate resolves to under empty AiOptions in these
    // tests: FeatureModels is unset, so it falls back to AiOptions.DefaultModel's initializer. Profiles
    // seeded with THIS model are model-fresh (so cache-hit/fresh tests still exercise the timestamp path);
    // profiles seeded with null/other are model-stale (cross-model self-heal). Kept in sync with
    // AiOptions.DefaultModel so the DEF-1 freshness gate is exercised deterministically.
    private const string ActiveModel = "qwen2.5:14b";

    // ─── 1. Cache HIT: seeded row returned, LLM never called ─────────────────────────────────

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_CacheHit_ReturnsSeedRowWithoutLlmCall()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Cache Hit Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "טקסט לפרק."
        });

        var seeded = new ChapterStyleProfile
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":5}}",
            // Model-fresh so this test isolates the timestamp cache-hit path (DEF-1 freshness now also
            // gates on model; a null here would self-heal and call the LLM).
            BuiltWithModel = ActiveModel
        };
        db.ChapterStyleProfiles.Add(seeded);
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // Returned the seeded row
        Assert.NotNull(result);
        Assert.Equal(seeded.Id, result!.Id);
        Assert.Equal(seeded.MetricsJson, result.MetricsJson);

        // LLM was never invoked
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "IAiRouter should not be called on a cache hit");

        // No second row was inserted
        var count = await db.ChapterStyleProfiles.CountAsync();
        Assert.Equal(1, count);
    }

    // ─── 1. Cache MISS → build: profile persisted, LLM called once ───────────────────────────

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_CacheMiss_BuildsAndPersistsProfile()
    {
        var metricsPayload = new
        {
            syntaxMetrics = new { sentenceCount = 10, averageSentenceLength = 12.5 },
            morphologyMetrics = new { wordCount = 125, uniqueWords = 80, averageWordLength = 4.2, lexicalDensity = 0.64 },
            styleMetrics = new { formality = "literary", readability = 0.75, voiceBalance = "active" },
            grammaticalityScore = 0.92,
            summary = "Solid prose.",
            deviations = Array.Empty<object>(),
            consistencyIssues = Array.Empty<object>()
        };

        var llmResponseJson = JsonSerializer.Serialize(metricsPayload);

        using var provider = BuildServiceProvider(out var routerMock, llmResponse: llmResponseJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Cache Miss Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "זהו פרק עם תוכן מספיק לניתוח לשוני."
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // Profile was returned and persisted
        Assert.NotNull(result);
        Assert.Equal(chapterId, result!.ChapterId);
        Assert.Equal(bookId, result.BookId);
        Assert.Equal(language, result.Language);
        Assert.False(string.IsNullOrWhiteSpace(result.MetricsJson), "MetricsJson should be set");

        // Exactly one row in the table
        var count = await db.ChapterStyleProfiles.CountAsync();
        Assert.Equal(1, count);

        // LLM was called exactly once
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "IAiRouter should be called once on a cache miss");
    }

    // ─── 1b. Cache MISS → build with Hebrew prose-wrapped JSON: baseline still builds ─────────
    // Regression for the divergent-extractor bug: ComputeChapterLinguisticMetricsAsync used to
    // parse with a local first-'{'-to-last-'}' helper, while the user-facing LinguisticAnalysis
    // path uses UnifiedAnalysisService.ExtractJson (bidi stripping + balanced-brace matching +
    // Hebrew-prose-brace rejection). An LLM reply with Hebrew prose containing braces BEFORE the
    // real JSON would parse on the main path but fail the baseline build, so the profile was never
    // built and scene deviations lacked [CHAPTER_STYLE_BASELINE]. With the shared extractor the
    // leading {Hebrew prose} braces are skipped and the real object is extracted, so the build
    // succeeds. The old helper would have spliced first-'{'..last-'}' into invalid JSON → null.

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_HebrewProseWrappedJson_StillBuildsProfile()
    {
        // Hebrew preamble containing its OWN braces, then the real metrics object. The old
        // first-'{'-to-last-'}' parser would extract "{הערה...}\n\n{...real...}" → JsonException.
        const string llmResponse = """
            לפניכם ניתוח לשוני של הפרק {הערה ראשונית: הטקסט תקין}.

            {
              "syntaxMetrics": { "sentenceCount": 8, "averageSentenceLength": 15.0 },
              "morphologyMetrics": { "wordCount": 120, "uniqueWords": 90, "averageWordLength": 4.5, "lexicalDensity": 0.75 },
              "styleMetrics": { "formality": "literary", "readability": 0.8, "voiceBalance": "active" },
              "grammaticalityScore": 0.95,
              "summary": "ניתוח תקין.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        using var provider = BuildServiceProvider(out var routerMock, llmResponse: llmResponse);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Prose-Wrapped Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "זהו פרק עם תוכן מספיק לניתוח לשוני."
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // Profile was built and persisted despite the prose-wrapped, brace-laden Hebrew preamble.
        Assert.NotNull(result);
        Assert.Equal(chapterId, result!.ChapterId);
        Assert.False(string.IsNullOrWhiteSpace(result.MetricsJson), "MetricsJson should be set");

        // The persisted metrics are the REAL object (not the Hebrew preamble): the grammaticality
        // score round-trips, proving the correct '{' was selected.
        var parsed = JsonSerializer.Deserialize<LinguisticAnalysisResult>(result.MetricsJson, DeserializeOpts);
        Assert.NotNull(parsed);
        Assert.Equal(0.95, parsed!.GrammaticalityScore, precision: 5);

        var count = await db.ChapterStyleProfiles.CountAsync();
        Assert.Equal(1, count);
    }

    // ─── 2. Graceful degradation: missing chapter → null, no throw, no row ──────────────────

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_MissingChapter_ReturnsNullWithoutThrowing()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var nonexistentChapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Degradation Book" });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        // Should not throw
        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, nonexistentChapterId, "he");

        Assert.Null(result);

        // No row persisted
        var count = await db.ChapterStyleProfiles.CountAsync();
        Assert.Equal(0, count);
    }

    // ─── 2. Graceful degradation: LLM returns empty → null, no throw, no row ────────────────

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_LlmReturnsEmpty_ReturnsNullWithoutThrowing()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: "");
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Empty LLM Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "פרק עם תוכן."
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, "he");

        Assert.Null(result);

        var count = await db.ChapterStyleProfiles.CountAsync();
        Assert.Equal(0, count);
    }

    // ─── 2. Graceful degradation: LLM throws → null, no throw, no row ────────────────────────

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_LlmThrows_ReturnsNullWithoutThrowing()
    {
        using var provider = BuildServiceProvider(out var routerMock, llmThrows: true);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "LLM Throws Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "תוכן לבדיקה."
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, "he");

        Assert.Null(result);

        var count = await db.ChapterStyleProfiles.CountAsync();
        Assert.Equal(0, count);
    }

    // ─── 3. JSON round-trip: serialize → deserialize preserves all new fields ─────────────────

    [Fact]
    public void LinguisticAnalysisResult_JsonRoundTrip_DeviationsAndConsistencyIssuesSurvive()
    {
        var original = new LinguisticAnalysisResult
        {
            GrammaticalityScore = 0.88,
            Summary = "Well-written chapter.",
            Deviations = new List<StyleDeviation>
            {
                new StyleDeviation
                {
                    Metric = "averageSentenceLength",
                    SceneValue = 22.5,
                    ChapterBaseline = 14.0,
                    Note = "Scene sentences are notably longer than chapter baseline."
                }
            },
            ConsistencyIssues = new List<ConsistencyIssue>
            {
                new ConsistencyIssue { Type = "register", Span = "paragraph 3", Description = "Sudden shift to informal register." },
                new ConsistencyIssue { Type = "tense",    Span = "paragraph 5", Description = "Unexpected switch to present tense." },
                new ConsistencyIssue { Type = "pov",      Span = "last line",   Description = "POV slip to second person." }
            }
        };

        var json = JsonSerializer.Serialize(original, SerializeOpts);
        var roundTripped = JsonSerializer.Deserialize<LinguisticAnalysisResult>(json, DeserializeOpts);

        Assert.NotNull(roundTripped);

        // Deviations
        Assert.Single(roundTripped!.Deviations);
        var dev = roundTripped.Deviations[0];
        Assert.Equal("averageSentenceLength", dev.Metric);
        Assert.Equal(22.5, dev.SceneValue, precision: 5);
        Assert.Equal(14.0, dev.ChapterBaseline, precision: 5);
        Assert.Equal("Scene sentences are notably longer than chapter baseline.", dev.Note);

        // ConsistencyIssues - all three types
        Assert.Equal(3, roundTripped.ConsistencyIssues.Count);
        Assert.Equal("register", roundTripped.ConsistencyIssues[0].Type);
        Assert.Equal("paragraph 3", roundTripped.ConsistencyIssues[0].Span);
        Assert.Equal("Sudden shift to informal register.", roundTripped.ConsistencyIssues[0].Description);

        Assert.Equal("tense", roundTripped.ConsistencyIssues[1].Type);
        Assert.Equal("pov",   roundTripped.ConsistencyIssues[2].Type);
        Assert.Equal("last line", roundTripped.ConsistencyIssues[2].Span);
    }

    // ─── 3. JSON round-trip from hand-written camelCase string (simulates model output) ───────

    [Fact]
    public void LinguisticAnalysisResult_DeserializesFromCamelCaseModelOutput()
    {
        // This is the shape that the LLM emits and TryExtractAndReserialize / ComputeChapterLinguisticMetricsAsync parse.
        const string modelOutput = """
            {
              "syntaxMetrics": { "sentenceCount": 8, "averageSentenceLength": 15.0 },
              "morphologyMetrics": { "wordCount": 120, "uniqueWords": 90, "averageWordLength": 4.5, "lexicalDensity": 0.75 },
              "styleMetrics": { "formality": "literary", "readability": 0.8, "voiceBalance": "active" },
              "grammaticalityScore": 0.95,
              "summary": "Balanced prose.",
              "deviations": [
                {
                  "metric": "lexicalDensity",
                  "sceneValue": 0.9,
                  "chapterBaseline": 0.75,
                  "note": "Higher density than usual."
                }
              ],
              "consistencyIssues": [
                {
                  "type": "tense",
                  "span": "lines 10-12",
                  "description": "Tense inconsistency detected."
                }
              ]
            }
            """;

        var result = JsonSerializer.Deserialize<LinguisticAnalysisResult>(modelOutput, DeserializeOpts);

        Assert.NotNull(result);

        // Top-level fields
        Assert.Equal(0.95, result!.GrammaticalityScore, precision: 5);
        Assert.Equal("Balanced prose.", result.Summary);

        // Deviations
        Assert.Single(result.Deviations);
        Assert.Equal("lexicalDensity", result.Deviations[0].Metric);
        Assert.Equal(0.9, result.Deviations[0].SceneValue, precision: 5);
        Assert.Equal(0.75, result.Deviations[0].ChapterBaseline, precision: 5);
        Assert.Equal("Higher density than usual.", result.Deviations[0].Note);

        // ConsistencyIssues
        Assert.Single(result.ConsistencyIssues);
        Assert.Equal("tense", result.ConsistencyIssues[0].Type);
        Assert.Equal("lines 10-12", result.ConsistencyIssues[0].Span);
        Assert.Equal("Tense inconsistency detected.", result.ConsistencyIssues[0].Description);
    }

    // ─── 3. JSON key contract: serialised JSON contains the expected camelCase property names ─

    [Fact]
    public void LinguisticAnalysisResult_SerializesToExpectedJsonKeys()
    {
        var result = new LinguisticAnalysisResult
        {
            Deviations = new List<StyleDeviation>
            {
                new StyleDeviation { Metric = "m", SceneValue = 1.0, ChapterBaseline = 2.0, Note = "n" }
            },
            ConsistencyIssues = new List<ConsistencyIssue>
            {
                new ConsistencyIssue { Type = "register", Span = "s", Description = "d" }
            }
        };

        // [JsonPropertyName] attributes control the keys regardless of NamingPolicy.
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"deviations\"", json);
        Assert.Contains("\"consistencyIssues\"", json);
        Assert.Contains("\"sceneValue\"", json);
        Assert.Contains("\"chapterBaseline\"", json);
        Assert.Contains("\"type\"", json);
        Assert.Contains("\"span\"", json);
        Assert.Contains("\"description\"", json);
    }

    // ─── 4. BuildContextAsync wiring - LinguisticAnalysis scope with StyleProfile present ─────

    [Fact]
    public async Task BuildContextAsync_LinguisticAnalysis_WithStyleProfile_PopulatesStyleProfile_AndBookStyleAveragesIsNull()
    {
        var metricsPayload = """
            {
              "syntaxMetrics": { "sentenceCount": 3 },
              "morphologyMetrics": { "wordCount": 30, "uniqueWords": 25, "averageWordLength": 4.0, "lexicalDensity": 0.5 },
              "styleMetrics": { "formality": "mixed", "readability": 0.7, "voiceBalance": "mixed" },
              "grammaticalityScore": 0.9,
              "summary": "Brief chapter.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        using var provider = BuildServiceProvider(out _, llmResponse: metricsPayload);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Wiring Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "פרק לבדיקת חיבור."
        });

        var styleProfile = new StyleProfileData
        {
            DominantTone = "neutral",
            Pov = "third-limited"
        };

        db.BookBibles.Add(new BookBible
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            StyleProfileJson = JsonSerializer.Serialize(styleProfile)
        });

        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.LinguisticAnalysis,
            "he",
            CancellationToken.None);

        // The qualitative book style profile is loaded into StyleProfile (rendered as [STYLE_PROFILE]).
        Assert.NotNull(context.StyleProfile);
        Assert.Equal("neutral", context.StyleProfile!.DominantTone);
        Assert.Equal("third-limited", context.StyleProfile.Pov);

        // BookStyleAverages is intentionally null for this PR: it previously duplicated the
        // qualitative StyleProfile under a mislabeled marker. Real numeric book-average metrics
        // and a book-comparison output are deferred to Plan 5.
        Assert.Null(context.BookStyleAverages);
    }

    // ─── 4. BuildContextAsync wiring - LinguisticAnalysis scope, no StyleProfile → null ──────

    [Fact]
    public async Task BuildContextAsync_LinguisticAnalysis_WithoutStyleProfile_BookStyleAveragesIsNull()
    {
        var metricsPayload = """
            {
              "syntaxMetrics": { "sentenceCount": 2 },
              "morphologyMetrics": { "wordCount": 20, "uniqueWords": 15, "averageWordLength": 3.5, "lexicalDensity": 0.45 },
              "styleMetrics": { "formality": "informal", "readability": 0.6, "voiceBalance": "passive" },
              "grammaticalityScore": 0.8,
              "summary": "Simple chapter.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        using var provider = BuildServiceProvider(out _, llmResponse: metricsPayload);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "No Bible Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "פרק ללא ביבל."
        });

        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.LinguisticAnalysis,
            "he",
            CancellationToken.None);

        // BookStyleAverages should be null when no BookBible / StyleProfile exists
        Assert.Null(context.BookStyleAverages);

        // Analysis type and scope must still be set correctly
        Assert.Equal(AnalysisType.LinguisticAnalysis, context.AnalysisType);
        Assert.Equal(AnalysisScope.Chapter, context.Scope);
    }

    // ─── 4. BuildContextAsync wiring - non-LinguisticAnalysis → baseline stays null ──────────

    [Fact]
    public async Task BuildContextAsync_NonLinguisticScope_ChapterStyleBaselineIsNull()
    {
        using var provider = BuildServiceProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Proofread Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "טקסט הגהה."
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        // Proofread is not LinguisticAnalysis - ChapterStyleBaseline must remain null
        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.Proofread,
            "he",
            CancellationToken.None);

        Assert.Null(context.ChapterStyleBaseline);
        Assert.Null(context.BookStyleAverages);
    }

    // ─── 5. Context envelope wiring - LinguisticAnalysis pulls preceding/following neighbours ──
    // Regression test: chapter-scope LinguisticAnalysis no longer pulls the adjacent-chapter context
    // envelope. At Chapter scope the chapter is self-contained, so injecting neighbouring-chapter text
    // would surface cross-chapter consistency issues that are not navigable in this unit. The envelope
    // is reserved for Scene scope (within-chapter boundary detection).

    [Fact]
    public async Task BuildContextAsync_ChapterScopeLinguistic_DoesNotPullAdjacentChapterEnvelope()
    {
        var metricsPayload = """
            {
              "syntaxMetrics": { "sentenceCount": 3 },
              "morphologyMetrics": { "wordCount": 30, "uniqueWords": 25, "averageWordLength": 4.0, "lexicalDensity": 0.5 },
              "styleMetrics": { "formality": "mixed", "readability": 0.7, "voiceBalance": "mixed" },
              "grammaticalityScore": 0.9,
              "summary": "Middle chapter.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        using var provider = BuildServiceProvider(out _, llmResponse: metricsPayload);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var prevId = Guid.NewGuid();
        var middleId = Guid.NewGuid();
        var nextId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Envelope Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = prevId, BookId = bookId, Order = 0, Title = "Prev", ContentText = "פתיחת הפרק הקודם.\n\nסוף הפרק הקודם כאן." });
        db.Chapters.Add(new Chapter { Id = middleId, BookId = bookId, Order = 1, Title = "Middle", ContentText = "תוכן הפרק האמצעי לניתוח לשוני." });
        db.Chapters.Add(new Chapter { Id = nextId, BookId = bookId, Order = 2, Title = "Next", ContentText = "תחילת הפרק הבא כאן.\n\nסוף הפרק הבא." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            middleId,
            AnalysisType.LinguisticAnalysis,
            "he",
            CancellationToken.None);

        // At Chapter scope the adjacent-chapter envelope is intentionally omitted for LinguisticAnalysis,
        // so neighbours stay null (the chapter is treated as self-contained).
        Assert.Null(context.PrecedingContext);
        Assert.Null(context.FollowingContext);
    }

    // ─── 5. Context envelope wiring - Scene-scope LinguisticAnalysis DOES pull the envelope ─────
    // Companion positive test for BuildContextAsync_ChapterScopeLinguistic_DoesNotPullAdjacentChapterEnvelope.
    // At Scene scope the adjacent-scene envelope IS pulled so the prompt can detect cross-paragraph
    // register/tense/POV breaks at scene boundaries within a chapter. A future refactor that
    // accidentally drops the Scene-scope envelope would be caught here.

    [Fact]
    public async Task BuildContextAsync_SceneScopeLinguistic_PullsContextEnvelope()
    {
        // LLM returns valid metrics so LoadOrBuildChapterStyleProfileAsync (called at Scene scope)
        // can build the chapter baseline without failing.
        var metricsPayload = """
            {
              "syntaxMetrics": { "sentenceCount": 3 },
              "morphologyMetrics": { "wordCount": 30, "uniqueWords": 25, "averageWordLength": 4.0, "lexicalDensity": 0.5 },
              "styleMetrics": { "formality": "literary", "readability": 0.75, "voiceBalance": "active" },
              "grammaticalityScore": 0.9,
              "summary": "Chapter baseline for scene envelope test.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        using var provider = BuildServiceProvider(out _, llmResponse: metricsPayload);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId    = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var prevSceneId   = Guid.NewGuid();
        var middleSceneId = Guid.NewGuid();
        var nextSceneId   = Guid.NewGuid();

        // Build real, round-trippable SFDT for each scene so SfdtConversionService.GetTextFromSfdt
        // returns the planted marker text (a minimal JSON SFDT yields empty text in the test host).
        var sfdtSvc = new SfdtConversionService();

        static string MakeSfdt(SfdtConversionService s, string text) =>
            s.ConvertToSfdt(
                new System.Collections.Generic.List<DocumentFormat.OpenXml.OpenXmlElement>
                {
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.Text(text)))
                }).SfdtJson;

        // Distinctive marker text planted in each neighbour – asserted below to appear in the envelope.
        const string prevSceneMarker   = "PREV_SCENE_TAIL_MARKER unique text from previous scene";
        const string middleSceneText   = "Middle scene content under analysis for linguistic check.";
        const string nextSceneMarker   = "NEXT_SCENE_HEAD_MARKER unique text from following scene";

        db.Books.Add(new Book { Id = bookId, Title = "Scene Envelope Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Order = 1,
            Title = "Chapter With Scenes",
            ContentText = "Chapter prose that covers the whole chapter."
        });
        // Three scenes in the same chapter, ordered 0-1-2.
        db.Scenes.Add(new Scene { Id = prevSceneId,   ChapterId = chapterId, Order = 0, Title = "Prev",   ContentSfdt = MakeSfdt(sfdtSvc, prevSceneMarker)  });
        db.Scenes.Add(new Scene { Id = middleSceneId, ChapterId = chapterId, Order = 1, Title = "Middle", ContentSfdt = MakeSfdt(sfdtSvc, middleSceneText)  });
        db.Scenes.Add(new Scene { Id = nextSceneId,   ChapterId = chapterId, Order = 2, Title = "Next",   ContentSfdt = MakeSfdt(sfdtSvc, nextSceneMarker)  });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Scene,
            middleSceneId,
            AnalysisType.LinguisticAnalysis,
            "he",
            CancellationToken.None);

        // At Scene scope the context envelope IS pulled for LinguisticAnalysis.
        // PrecedingContext comes from the tail of the previous scene's SFDT text.
        // FollowingContext comes from the head of the next scene's SFDT text.
        Assert.NotNull(context.PrecedingContext);
        Assert.NotNull(context.FollowingContext);

        Assert.Contains(prevSceneMarker, context.PrecedingContext, StringComparison.Ordinal);
        Assert.Contains(nextSceneMarker, context.FollowingContext, StringComparison.Ordinal);
    }

    // ─── 5. Context envelope wiring - non-envelope analysis type leaves neighbours null ────────

    [Fact]
    public async Task BuildContextAsync_LiteraryAnalysis_LeavesContextEnvelopeNull()
    {
        using var provider = BuildServiceProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var prevId = Guid.NewGuid();
        var middleId = Guid.NewGuid();
        var nextId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "No Envelope Book", Language = "he" });
        db.Chapters.Add(new Chapter { Id = prevId, BookId = bookId, Order = 0, Title = "Prev", ContentText = "פרק קודם." });
        db.Chapters.Add(new Chapter { Id = middleId, BookId = bookId, Order = 1, Title = "Middle", ContentText = "פרק אמצעי." });
        db.Chapters.Add(new Chapter { Id = nextId, BookId = bookId, Order = 2, Title = "Next", ContentText = "פרק הבא." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        // LiteraryAnalysis does not request the context envelope, so neighbours stay null.
        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            middleId,
            AnalysisType.LiteraryAnalysis,
            "he",
            CancellationToken.None);

        Assert.Null(context.PrecedingContext);
        Assert.Null(context.FollowingContext);
    }

    // ─── 6. Cache STALE → rebuild: existing row refreshed, LLM called once ──────────────────────

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_StaleProfile_RebuildsAndUpdatesRow()
    {
        var newMetricsPayload = new
        {
            syntaxMetrics = new { sentenceCount = 20, averageSentenceLength = 11.0 },
            morphologyMetrics = new { wordCount = 200, uniqueWords = 140, averageWordLength = 4.8, lexicalDensity = 0.70 },
            styleMetrics = new { formality = "literary", readability = 0.82, voiceBalance = "active" },
            grammaticalityScore = 0.91,
            summary = "Rebuilt prose.",
            deviations = Array.Empty<object>(),
            consistencyIssues = Array.Empty<object>()
        };

        var llmResponseJson = JsonSerializer.Serialize(newMetricsPayload);

        using var provider = BuildServiceProvider(out var routerMock, llmResponse: llmResponseJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";
        const string oldMetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":3}}";

        db.Books.Add(new Book { Id = bookId, Title = "Stale Profile Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "זהו פרק עם תוכן שהשתנה לאחר בניית הפרופיל."
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = oldMetricsJson
        });

        await db.SaveChangesAsync();

        // Force stale: set the profile's UpdatedAt to a time BEFORE the chapter's UpdatedAt.
        // SaveChanges overrides UpdatedAt on Added/Modified, so we bypass it by setting the
        // entity's property and marking it Unchanged so the override's Modified branch is skipped.
        var profileEntry = db.Entry(db.ChapterStyleProfiles.Local.Single(p => p.Id == profileId));
        profileEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        profileEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // LLM was called to rebuild the stale profile
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "IAiRouter should be called once when the profile is stale");

        // The existing row was updated in place (same Id, new MetricsJson)
        Assert.NotNull(result);
        Assert.Equal(profileId, result!.Id);
        Assert.NotEqual(oldMetricsJson, result.MetricsJson);
        Assert.False(string.IsNullOrWhiteSpace(result.MetricsJson), "MetricsJson must be set after rebuild");

        // No duplicate row was inserted
        var count = await db.ChapterStyleProfiles.CountAsync();
        Assert.Equal(1, count);
    }

    // ─── 6. Cache FRESH: profile newer than chapter, LLM never called ────────────────────────

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_FreshProfile_ReturnsCachedRowWithoutLlmCall()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";
        const string existingMetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":7}}";

        db.Books.Add(new Book { Id = bookId, Title = "Fresh Profile Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "פרק שלא השתנה מאז הפרופיל נבנה."
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = existingMetricsJson,
            // Model-fresh so only the timestamp freshness path is exercised here.
            BuiltWithModel = ActiveModel
        });

        await db.SaveChangesAsync();

        // Force fresh: set the chapter's UpdatedAt to a time BEFORE the profile's UpdatedAt.
        // Both were just stamped by SaveChanges to ~UtcNow; push the chapter back by 2 hours
        // and mark it Unchanged so the override's Modified branch is not triggered.
        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // The seeded row was returned unchanged
        Assert.NotNull(result);
        Assert.Equal(profileId, result!.Id);
        Assert.Equal(existingMetricsJson, result.MetricsJson);

        // LLM was never invoked
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "IAiRouter should not be called when the profile is fresh");

        // No second row was inserted
        var count = await db.ChapterStyleProfiles.CountAsync();
        Assert.Equal(1, count);
    }

    // ─── 6a. Stale profile + failed rebuild: return null, never the outdated cached row ────────────

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_StaleProfile_RebuildFails_ReturnsNullNotStaleRow()
    {
        // The chapter has (new) content so a rebuild is attempted, but the LLM yields nothing. The
        // stale cached row must NOT be returned - that would inject an outdated [CHAPTER_STYLE_BASELINE]
        // and produce spurious deviations.
        using var provider = BuildServiceProvider(out var routerMock, llmResponse: "");
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Stale Rebuild-Fail Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "פרק עם תוכן חדש לאחר עריכה." // has content → rebuild is attempted
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":99}}" // outdated baseline
        });
        await db.SaveChangesAsync();

        // Force the profile STALE: built before the chapter's last edit.
        var profileEntry = db.Entry(db.ChapterStyleProfiles.Local.Single(p => p.Id == profileId));
        profileEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        profileEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // No stale baseline returned, and the rebuild was actually attempted.
        Assert.Null(result);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "A stale profile must trigger a rebuild attempt");
    }

    // ─── 6b. Empty chapter content: do NOT return a stale baseline from the previous full chapter ──

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_EmptyChapterContent_StaleProfile_ReturnsNullWithoutLlm()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Cleared Chapter Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "" // body has been cleared
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            // Baseline cached from the PREVIOUS, non-empty chapter.
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":12}}"
        });
        await db.SaveChangesAsync();

        // Force the profile STALE: built before the chapter's clearing edit.
        var profileEntry = db.Entry(db.ChapterStyleProfiles.Local.Single(p => p.Id == profileId));
        profileEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        profileEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // No stale baseline is returned for a now-empty chapter (would inject an outdated
        // [CHAPTER_STYLE_BASELINE]), and we cannot rebuild from empty text.
        Assert.Null(result);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Empty chapter content must not trigger a baseline rebuild");
    }

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_EmptyChapterContent_FreshProfile_ReturnsCachedRow()
    {
        // Defensive: when the cached profile is NOT older than the chapter's last edit, keep it even on
        // an empty read (guards a spurious empty content read that did not bump the chapter's UpdatedAt).
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Empty Read Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = ""
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":7}}",
            // Model-fresh: the empty-content branch still applies the timestamp+model freshness gate.
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        // Force the profile FRESH: chapter's last edit predates the profile build.
        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        Assert.NotNull(result);
        Assert.Equal(profileId, result!.Id);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── 7. Model-config regression: Chapter FK = Cascade, Book FK = Restrict (P0 fix guard) ──────
    // The InMemory provider does NOT enforce cascade/restrict at runtime, so a delete-based test
    // would pass regardless of the FK config. Instead assert the configured DeleteBehavior on the
    // model directly. This is the reliable guard for the P0 fix: deleting a chapter must cascade to
    // its cached ChapterStyleProfile, while the Book FK stays Restrict to avoid SQL Server's
    // "multiple cascade paths" error (BooksController.Delete removes profiles explicitly).

    [Fact]
    public void ChapterStyleProfile_DeleteBehavior_ChapterIsCascade_BookIsRestrict()
    {
        using var provider = BuildServiceProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();

        var entityType = db.Model.FindEntityType(typeof(ChapterStyleProfile));
        Assert.NotNull(entityType);

        var foreignKeys = entityType!.GetForeignKeys().ToList();

        var chapterFk = foreignKeys.Single(fk => fk.PrincipalEntityType.ClrType == typeof(Chapter));
        var bookFk = foreignKeys.Single(fk => fk.PrincipalEntityType.ClrType == typeof(Book));

        Assert.Equal(DeleteBehavior.Cascade, chapterFk.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, bookFk.DeleteBehavior);
    }

    // ─── 7. Book-delete cleanup: profiles for the book are removed before the book is deleted ─────
    // Mirrors BooksController.Delete's explicit RemoveRange of ChapterStyleProfiles (required because
    // the Book FK is Restrict). InMemory is fine here because the cleanup is explicit C#, not a DB
    // cascade. Replicates the controller's RemoveRange to avoid wiring BookIntelligenceService.

    [Fact]
    public async Task BookDeleteCleanup_RemovesChapterStyleProfilesForBook_AndBookIsGone()
    {
        using var provider = BuildServiceProvider(out _);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        // Seed a book + chapter + cached style profile, plus a second book whose profile must survive.
        var otherBookId = Guid.NewGuid();
        var otherChapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Book To Delete" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Chapter", ContentText = "טקסט." });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":4}}"
        });

        db.Books.Add(new Book { Id = otherBookId, Title = "Survivor Book" });
        db.Chapters.Add(new Chapter { Id = otherChapterId, BookId = otherBookId, Title = "Chapter", ContentText = "טקסט אחר." });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = Guid.NewGuid(),
            BookId = otherBookId,
            ChapterId = otherChapterId,
            Language = "he",
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":9}}"
        });

        await db.SaveChangesAsync();

        // Replicate BooksController.Delete's explicit cleanup (Restrict Book FK requires it).
        var book = await db.Books.FindAsync(bookId);
        Assert.NotNull(book);

        var styleProfiles = await db.ChapterStyleProfiles.Where(p => p.BookId == bookId).ToListAsync();
        if (styleProfiles.Count > 0)
            db.ChapterStyleProfiles.RemoveRange(styleProfiles);

        db.Books.Remove(book!);
        await db.SaveChangesAsync();

        // The deleted book's cached profiles are gone, the book is gone.
        Assert.Empty(db.ChapterStyleProfiles.Where(p => p.BookId == bookId));
        Assert.Null(await db.Books.FindAsync(bookId));

        // The other book's profile is untouched.
        Assert.Single(db.ChapterStyleProfiles.Where(p => p.BookId == otherBookId));
    }

    // ─── 8. Chapter-scope LinguisticAnalysis with NO per-chapter profiles → no baseline, no LLM ─────
    // At Chapter scope the [CHAPTER_STYLE_BASELINE] reference is the BOOK AVERAGE (mean of the per-chapter
    // ChapterStyleProfile rows), never the chapter compared against itself. When no profiles exist yet the
    // book average degrades to null (the section is omitted → deviations []) and, crucially, the book-average
    // build NEVER force-builds the missing chapters, so no LLM call is made.

    [Fact]
    public async Task BuildContextAsync_ChapterScopeLinguistic_NoProfiles_NullBaselineNoLlm()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Chapter Scope Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "תוכן הפרק כולו לניתוח לשוני ברמת הפרק."
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.LinguisticAnalysis,
            "he",
            CancellationToken.None);

        // No book-average baseline when fewer than two chapters have a profile (here: zero).
        Assert.Null(context.ChapterStyleBaseline);

        // The book-average build only AGGREGATES existing profiles; it must never force-build the
        // missing chapter, so no LLM call is made.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Chapter-scope LinguisticAnalysis must not force-build per-chapter profiles");

        // Nothing was cached.
        Assert.Equal(0, await db.ChapterStyleProfiles.CountAsync());
    }

    // ─── 8. Bug 1 + Bug 2: scene-scope LinguisticAnalysis builds the baseline using the REQUEST ────
    // language (override / normalized code), not the raw book language, so the cache key, build prompt
    // and [CHAPTER_STYLE_BASELINE] agree with the analysis language.

    [Fact]
    public async Task BuildContextAsync_SceneScopeLinguistic_BuildsBaselineUsingRequestLanguage()
    {
        var metricsPayload = """
            {
              "syntaxMetrics": { "sentenceCount": 4 },
              "morphologyMetrics": { "wordCount": 40, "uniqueWords": 30, "averageWordLength": 4.2, "lexicalDensity": 0.55 },
              "styleMetrics": { "formality": "mixed", "readability": 0.7, "voiceBalance": "mixed" },
              "grammaticalityScore": 0.9,
              "summary": "Chapter baseline.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        using var provider = BuildServiceProvider(out var routerMock, llmResponse: metricsPayload);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();

        // Book language is Hebrew, but the analysis runs with an English override.
        db.Books.Add(new Book { Id = bookId, Title = "Override Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Order = 1,
            Title = "Chapter",
            ContentText = "The full chapter text used to compute the style baseline."
        });
        // A real, round-trippable SFDT (the ultra-minimal CreateMinimalSfdtFromText payload yields
        // empty text through Syncfusion in the test host, which would make ResolveSceneAsync throw).
        var sceneSfdt = new SfdtConversionService().ConvertToSfdt(
            new System.Collections.Generic.List<DocumentFormat.OpenXml.OpenXmlElement>
            {
                new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text("The scene under analysis with several words here.")))
            }).SfdtJson;
        db.Scenes.Add(new Scene
        {
            Id = sceneId,
            ChapterId = chapterId,
            Order = 1,
            Title = "Scene",
            ContentSfdt = sceneSfdt
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Scene,
            sceneId,
            AnalysisType.LinguisticAnalysis,
            "en",
            CancellationToken.None);

        // Baseline built for scene scope.
        Assert.NotNull(context.ChapterStyleBaseline);

        // Bug 1: the baseline is keyed/built with the REQUEST language ("en"), not the book's "he".
        Assert.Equal("en", context.ChapterStyleBaseline!.Language);
        var persisted = await db.ChapterStyleProfiles.SingleAsync();
        Assert.Equal("en", persisted.Language);

        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Scene-scope LinguisticAnalysis builds the chapter baseline once");
    }

    // ─── 8. Bug 3: a failed stale-refresh save must not leave a tracked Modified entity ────────────
    // If the refresh SaveChanges throws, the profile must be detached so a later SaveChanges on the
    // same scoped DbContext (e.g. from UnifiedAnalysisService) is not poisoned by the pending change.

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_StaleRefreshSaveFails_DoesNotLeaveTrackedModifiedEntity()
    {
        var newMetricsJson = JsonSerializer.Serialize(new
        {
            syntaxMetrics = new { sentenceCount = 9 },
            deviations = Array.Empty<object>(),
            consistencyIssues = Array.Empty<object>()
        });

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ThrowOnSaveDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";
        const string oldMetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":3}}";

        db.Books.Add(new Book { Id = bookId, Title = "Save Fail Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "תוכן הפרק שהשתנה לאחר בניית הפרופיל."
        });
        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = oldMetricsJson
        });
        await db.SaveChangesAsync();

        // Force stale so the refresh path runs.
        var profileEntry = db.Entry(db.ChapterStyleProfiles.Local.Single(p => p.Id == profileId));
        profileEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        profileEntry.State = EntityState.Unchanged;

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = newMetricsJson, Model = "m", Provider = "p" });

        var svc = new AnalysisContextService(
            db,
            new SfdtConversionService(),
            routerMock.Object,
            new PromptFactory(),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<AnalysisContextService>.Instance);

        // Make the stale-refresh SaveChanges throw.
        db.ThrowOnSave = true;
        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // Degrades gracefully: returns the freshly computed baseline rather than null.
        Assert.NotNull(result);

        // The failed refresh must NOT leave a tracked Modified ChapterStyleProfile.
        Assert.DoesNotContain(
            db.ChangeTracker.Entries<ChapterStyleProfile>(),
            e => e.State == EntityState.Modified);

        // A later SaveChanges on the same context succeeds (no poisoned pending change to retry).
        db.ThrowOnSave = false;
        var ex = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Null(ex);
    }

    // ─── 9. BuildBookStyleAverageProfileAsync: mean math over multiple chapter profiles ───────────
    // The book-average baseline is the per-metric MEAN of the numeric syntax + morphology fields across
    // every chapter that ALREADY has a profile. Text fields (formality, summary, deviations) are ignored.

    [Fact]
    public async Task BuildBookStyleAverageProfileAsync_AveragesNumericMetricsAcrossProfiles()
    {
        // No LLM should be needed: all chapters already have a profile, and they are FRESH (chapter
        // UpdatedAt pushed before the profile build), so the read-or-build method never recomputes.
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";
        db.Books.Add(new Book { Id = bookId, Title = "Average Book" });

        // Three chapters with averageSentenceLength 10 / 20 / 30 → mean 20; lexicalDensity 0.4/0.6/0.8 → 0.6;
        // sentenceCount 4/8/12 → 8; grammaticalityScore 0.6/0.8/1.0 → 0.8.
        var chapterIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var sentenceLengths = new[] { 10.0, 20.0, 30.0 };
        var sentenceCounts = new[] { 4, 8, 12 };
        var lexicalDensities = new[] { 0.4, 0.6, 0.8 };
        var grammaticalityScores = new[] { 0.6, 0.8, 1.0 };

        for (var i = 0; i < chapterIds.Length; i++)
        {
            db.Chapters.Add(new Chapter
            {
                Id = chapterIds[i],
                BookId = bookId,
                Title = $"Chapter {i}",
                ContentText = "תוכן הפרק."
            });

            var payload = new
            {
                syntaxMetrics = new { sentenceCount = sentenceCounts[i], averageSentenceLength = sentenceLengths[i] },
                morphologyMetrics = new { wordCount = 100, uniqueWords = 70, averageWordLength = 4.0, lexicalDensity = lexicalDensities[i] },
                styleMetrics = new { formality = "literary", readability = 0.7, voiceBalance = "active" },
                grammaticalityScore = grammaticalityScores[i],
                summary = "S",
                deviations = Array.Empty<object>(),
                consistencyIssues = Array.Empty<object>()
            };

            db.ChapterStyleProfiles.Add(new ChapterStyleProfile
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                ChapterId = chapterIds[i],
                Language = language,
                MetricsJson = JsonSerializer.Serialize(payload),
                // Model-fresh so aggregating these fresh profiles needs no rebuild (DEF-1).
                BuiltWithModel = ActiveModel
            });
        }

        await db.SaveChangesAsync();

        // Make every chapter FRESH relative to its profile so no rebuild fires.
        foreach (var c in db.Chapters.Local.ToList())
        {
            var entry = db.Entry(c);
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
            entry.State = EntityState.Unchanged;
        }

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var avg = await svc.BuildBookStyleAverageProfileAsync(bookId, language, CancellationToken.None);

        Assert.NotNull(avg);
        Assert.Equal(bookId, avg!.BookId);
        Assert.Equal(language, avg.Language);

        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(avg.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);

        // Mean of the numeric metrics.
        Assert.Equal(20.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5);
        Assert.Equal(8, metrics.SyntaxMetrics.SentenceCount);
        Assert.Equal(0.6, metrics.MorphologyMetrics.LexicalDensity, precision: 5);
        Assert.Equal(0.8, metrics.GrammaticalityScore, precision: 5);

        // No LLM call: fresh profiles are only read, never rebuilt.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Aggregating fresh existing profiles must not call the LLM");
    }

    // ─── 9. BuildBookStyleAverageProfileAsync: a profile with null metric sub-objects is SKIPPED ───
    // A fresh same-model profile whose JSON carried an explicit "syntaxMetrics": null deserializes with
    // that sub-object NULL (overriding LinguisticAnalysisResult's non-null default). The Average(...)
    // lambdas dereference SyntaxMetrics/MorphologyMetrics, so before the fix one such profile NRE'd and
    // the outer catch degraded the ENTIRE book average to null. The fix excludes only the bad profile
    // and aggregates the rest, so two good profiles still yield a non-null average.

    [Fact]
    public async Task BuildBookStyleAverageProfileAsync_ProfileWithNullSyntaxMetrics_SkippedNotWholeAverageNulled()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";
        db.Books.Add(new Book { Id = bookId, Title = "Null-Metrics Book" });

        // Two GOOD profiles: averageSentenceLength 10 / 30 → mean 20; sentenceCount 4 / 12 → 8;
        // wordCount 100 / 200 → 150. The THIRD profile is also fresh same-model but serializes with an
        // explicit null syntaxMetrics, so it must be excluded (not crash the whole average).
        var chapterIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        // Good profile 0.
        var good0 = new
        {
            syntaxMetrics = new { sentenceCount = 4, averageSentenceLength = 10.0 },
            morphologyMetrics = new { wordCount = 100, uniqueWords = 70, averageWordLength = 4.0, lexicalDensity = 0.5 },
            grammaticalityScore = 0.8,
            deviations = Array.Empty<object>(),
            consistencyIssues = Array.Empty<object>()
        };
        // Good profile 1.
        var good1 = new
        {
            syntaxMetrics = new { sentenceCount = 12, averageSentenceLength = 30.0 },
            morphologyMetrics = new { wordCount = 200, uniqueWords = 140, averageWordLength = 4.0, lexicalDensity = 0.7 },
            grammaticalityScore = 0.9,
            deviations = Array.Empty<object>(),
            consistencyIssues = Array.Empty<object>()
        };
        // BAD profile 2: explicit null syntaxMetrics, valid morphologyMetrics. Deserializes with
        // SyntaxMetrics == null, which would NRE the Average(...) lambda before the skip fix.
        const string badProfileJson = """
            {
              "syntaxMetrics": null,
              "morphologyMetrics": { "wordCount": 9999, "uniqueWords": 9999, "averageWordLength": 9.9, "lexicalDensity": 0.99 },
              "grammaticalityScore": 0.1,
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        var profileJsons = new[]
        {
            JsonSerializer.Serialize(good0),
            JsonSerializer.Serialize(good1),
            badProfileJson
        };

        for (var i = 0; i < chapterIds.Length; i++)
        {
            db.Chapters.Add(new Chapter
            {
                Id = chapterIds[i],
                BookId = bookId,
                Title = $"Chapter {i}",
                ContentText = "תוכן הפרק."
            });

            db.ChapterStyleProfiles.Add(new ChapterStyleProfile
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                ChapterId = chapterIds[i],
                Language = language,
                MetricsJson = profileJsons[i],
                // All three are model-fresh so the freshness gate admits them; the bad one is then
                // excluded by the null-metrics skip, not by staleness.
                BuiltWithModel = ActiveModel
            });
        }

        await db.SaveChangesAsync();

        // Make every chapter FRESH relative to its profile so no rebuild fires (pure read path).
        foreach (var c in db.Chapters.Local.ToList())
        {
            var entry = db.Entry(c);
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
            entry.State = EntityState.Unchanged;
        }

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        // No NRE, no all-or-nothing null: the average is computed from the two GOOD profiles only.
        var avg = await svc.BuildBookStyleAverageProfileAsync(bookId, language, CancellationToken.None);

        Assert.NotNull(avg);

        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(avg!.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);

        // Mean of the TWO good profiles only (the bad profile's 9999 word count is NOT folded in).
        Assert.Equal(20.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5); // (10 + 30) / 2
        Assert.Equal(8, metrics.SyntaxMetrics.SentenceCount);                            // (4 + 12) / 2
        Assert.Equal(150, metrics.MorphologyMetrics.WordCount);                          // (100 + 200) / 2

        // Pure read of fresh profiles → no LLM rebuild.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Skipping a null-metrics profile must not trigger a rebuild");
    }

    // ─── 9. BuildBookStyleAverageProfileAsync: fewer than 2 profiles → null (degradation) ─────────

    [Fact]
    public async Task BuildBookStyleAverageProfileAsync_FewerThanTwoProfiles_ReturnsNull()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Single Profile Book" });
        // Two chapters exist, but only ONE has a profile → the book average is not meaningful.
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Chapter 0", ContentText = "תוכן." });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Title = "Chapter 1", ContentText = "תוכן." });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":5}}"
        });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var avg = await svc.BuildBookStyleAverageProfileAsync(bookId, language, CancellationToken.None);

        Assert.Null(avg);

        // The single missing chapter is NOT force-built (no LLM call), and the lone existing profile is
        // not even read because the <2 guard short-circuits first.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Fewer than two profiles must short-circuit without force-building missing chapters");
    }

    // ─── 9. BuildBookStyleAverageProfileAsync: never force-builds unprofiled chapters ──────────────
    // The "existing-profile-only, no force-build" subtlety: a book with many unprofiled chapters and only
    // two profiled chapters must aggregate ONLY the two, never firing an LLM build for the rest.

    [Fact]
    public async Task BuildBookStyleAverageProfileAsync_OnlyAggregatesProfiledChapters_NoForceBuild()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Mixed Book" });

        // Two profiled chapters + three unprofiled chapters.
        var profiledIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < profiledIds.Length; i++)
        {
            db.Chapters.Add(new Chapter { Id = profiledIds[i], BookId = bookId, Title = $"Profiled {i}", ContentText = "תוכן." });
            db.ChapterStyleProfiles.Add(new ChapterStyleProfile
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                ChapterId = profiledIds[i],
                Language = language,
                MetricsJson = JsonSerializer.Serialize(new
                {
                    syntaxMetrics = new { sentenceCount = 6 + i, averageSentenceLength = 12.0 + i },
                    morphologyMetrics = new { wordCount = 80, uniqueWords = 60, averageWordLength = 4.0, lexicalDensity = 0.5 },
                    grammaticalityScore = 0.8,
                    deviations = Array.Empty<object>(),
                    consistencyIssues = Array.Empty<object>()
                }),
                // Model-fresh so the read-or-build path only reads them (no rebuild → no LLM).
                BuiltWithModel = ActiveModel
            });
        }

        for (var i = 0; i < 3; i++)
            db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Title = $"Unprofiled {i}", ContentText = "תוכן ללא פרופיל." });

        await db.SaveChangesAsync();

        // Make the profiled chapters FRESH so the read-or-build path only reads them.
        foreach (var id in profiledIds)
        {
            var entry = db.Entry(db.Chapters.Local.Single(c => c.Id == id));
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
            entry.State = EntityState.Unchanged;
        }

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var avg = await svc.BuildBookStyleAverageProfileAsync(bookId, language, CancellationToken.None);

        // Average built from the two profiled chapters only.
        Assert.NotNull(avg);

        // The three unprofiled chapters were NOT built → no LLM call at all.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Unprofiled chapters must never be force-built when aggregating the book average");

        // No new ChapterStyleProfile rows were created for the unprofiled chapters.
        Assert.Equal(2, await db.ChapterStyleProfiles.CountAsync());
    }

    // ─── DEF-1 REGRESSION: book-average aggregation is READ-ONLY (no inline rebuild storm) ─────────
    // The severe perf regression: a single Chapter-scope analysis used to re-run the read-or-build method
    // for EVERY profiled chapter, and after the model column was added every legacy (null-model) row was
    // judged stale → rebuilt inline → N sequential gemma4:12b calls on one analysis. The fix makes the
    // aggregation a pure DB read that EXCLUDES (never rebuilds) stale/cross-model profiles. These tests
    // assert ZERO router calls.

    [Fact]
    public async Task BuildBookStyleAverageProfileAsync_AllLegacyNullModelProfiles_ZeroLlmCalls_ReturnsNull()
    {
        // Three profiled chapters whose BuiltWithModel is null (legacy rows after the migration). Every one
        // is model-stale relative to the active model, so the read-only aggregation EXCLUDES all three and
        // returns null - WITHOUT making a single rebuild/LLM call. This is the proof the storm is gone.
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";
        db.Books.Add(new Book { Id = bookId, Title = "Legacy Profiles Book" });

        var chapterIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < chapterIds.Length; i++)
        {
            db.Chapters.Add(new Chapter { Id = chapterIds[i], BookId = bookId, Title = $"Chapter {i}", ContentText = "תוכן הפרק." });
            db.ChapterStyleProfiles.Add(new ChapterStyleProfile
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                ChapterId = chapterIds[i],
                Language = language,
                MetricsJson = JsonSerializer.Serialize(new
                {
                    syntaxMetrics = new { sentenceCount = 5, averageSentenceLength = 12.0 },
                    morphologyMetrics = new { wordCount = 80, uniqueWords = 60, averageWordLength = 4.0, lexicalDensity = 0.5 },
                    grammaticalityScore = 0.8,
                    deviations = Array.Empty<object>(),
                    consistencyIssues = Array.Empty<object>()
                }),
                BuiltWithModel = null // legacy row: model-stale vs the active model
            });
        }
        await db.SaveChangesAsync();

        // Even make every profile TIMESTAMP-fresh (chapters older than their profiles) so ONLY the
        // null-model staleness is in play - proving the legacy rows are excluded, not rebuilt.
        foreach (var c in db.Chapters.Local.ToList())
        {
            var entry = db.Entry(c);
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
            entry.State = EntityState.Unchanged;
        }

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var avg = await svc.BuildBookStyleAverageProfileAsync(bookId, language, CancellationToken.None);

        // Every legacy profile is excluded as model-stale → fewer than 2 usable → null.
        Assert.Null(avg);

        // THE REGRESSION GUARD: not a single rebuild/LLM call was made.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Legacy/model-stale profiles must be EXCLUDED, never rebuilt inline (no LLM call)");

        // Nothing was rebuilt/restamped: the rows stay exactly as seeded (3, still null-model).
        Assert.Equal(3, await db.ChapterStyleProfiles.CountAsync());
    }

    [Fact]
    public async Task BuildBookStyleAverageProfileAsync_ThreeFreshSameModel_ReturnsAverage_ZeroLlmCalls()
    {
        // Three FRESH, same-(active)-model profiles → the average is produced with ZERO LLM calls (pure read).
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";
        db.Books.Add(new Book { Id = bookId, Title = "Fresh Same-Model Book" });

        var chapterIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var lengths = new[] { 10.0, 20.0, 30.0 }; // mean = 20
        for (var i = 0; i < chapterIds.Length; i++)
        {
            db.Chapters.Add(new Chapter { Id = chapterIds[i], BookId = bookId, Title = $"Chapter {i}", ContentText = "תוכן הפרק." });
            db.ChapterStyleProfiles.Add(new ChapterStyleProfile
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                ChapterId = chapterIds[i],
                Language = language,
                MetricsJson = JsonSerializer.Serialize(new
                {
                    syntaxMetrics = new { sentenceCount = 6, averageSentenceLength = lengths[i] },
                    morphologyMetrics = new { wordCount = 80, uniqueWords = 60, averageWordLength = 4.0, lexicalDensity = 0.5 },
                    grammaticalityScore = 0.8,
                    deviations = Array.Empty<object>(),
                    consistencyIssues = Array.Empty<object>()
                }),
                BuiltWithModel = ActiveModel // same as the active model → model-fresh
            });
        }
        await db.SaveChangesAsync();

        foreach (var c in db.Chapters.Local.ToList())
        {
            var entry = db.Entry(c);
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2); // timestamp-fresh
            entry.State = EntityState.Unchanged;
        }

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var avg = await svc.BuildBookStyleAverageProfileAsync(bookId, language, CancellationToken.None);

        Assert.NotNull(avg);
        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(avg!.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);
        Assert.Equal(20.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5);

        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Aggregating fresh same-model profiles is a pure read (no LLM call)");
    }

    [Fact]
    public async Task BuildBookStyleAverageProfileAsync_MixedFreshnessAndModel_AveragesOnlyFreshSameModel_ZeroLlmCalls()
    {
        // Mix: 2 fresh same-model + 1 cross-model (timestamp-fresh) + 1 timestamp-stale (same model). The
        // average must include ONLY the 2 fresh same-model profiles (lengths 10 & 20 → mean 15). The
        // excluded ones (length 100 each) would skew the mean if wrongly included, AND must NOT be rebuilt.
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";
        db.Books.Add(new Book { Id = bookId, Title = "Mixed Freshness Book" });

        var freshA = Guid.NewGuid();
        var freshB = Guid.NewGuid();
        var crossModel = Guid.NewGuid();
        var timestampStale = Guid.NewGuid();

        string Payload(double len) => JsonSerializer.Serialize(new
        {
            syntaxMetrics = new { sentenceCount = 6, averageSentenceLength = len },
            morphologyMetrics = new { wordCount = 80, uniqueWords = 60, averageWordLength = 4.0, lexicalDensity = 0.5 },
            grammaticalityScore = 0.8,
            deviations = Array.Empty<object>(),
            consistencyIssues = Array.Empty<object>()
        });

        // Use SAVE ORDERING (the SaveChanges override stamps UtcNow on each save) + tiny delays so the
        // persisted timestamps are real and monotonically increasing. This is the reliable way to set up
        // the timestamp-stale case for an AsNoTracking read (post-save Unchanged mutation is not flushed).

        // 1. Seed the timestampStale chapter + its profile FIRST (so the profile is the OLDEST row).
        db.Chapters.Add(new Chapter { Id = timestampStale, BookId = bookId, Title = "C-stale", ContentText = "תוכן הפרק." });
        var staleProfileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = staleProfileId, BookId = bookId, ChapterId = timestampStale, Language = language, MetricsJson = Payload(100.0), BuiltWithModel = ActiveModel });
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 2. The fresh + cross-model chapters next.
        foreach (var id in new[] { freshA, freshB, crossModel })
            db.Chapters.Add(new Chapter { Id = id, BookId = bookId, Title = "C", ContentText = "תוכן הפרק." });
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 3. Touch the stale chapter so its UpdatedAt jumps AFTER its (step-1) profile → timestamp-stale.
        var staleChapter = await db.Chapters.SingleAsync(c => c.Id == timestampStale);
        staleChapter.Title = "C-stale (edited)";
        await db.SaveChangesAsync();

        await Task.Delay(10);

        // 4. The fresh + cross-model profiles LAST so they are newer than their chapters (timestamp-fresh).
        //    freshA/freshB are same-model (included); crossModel is a different model (excluded by model).
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = freshA, Language = language, MetricsJson = Payload(10.0), BuiltWithModel = ActiveModel });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = freshB, Language = language, MetricsJson = Payload(20.0), BuiltWithModel = ActiveModel });
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile { Id = Guid.NewGuid(), BookId = bookId, ChapterId = crossModel, Language = language, MetricsJson = Payload(100.0), BuiltWithModel = "different-model" });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var avg = await svc.BuildBookStyleAverageProfileAsync(bookId, language, CancellationToken.None);

        // Only the two fresh same-model profiles contribute → mean of 10 & 20 = 15 (NOT pulled toward 100).
        Assert.NotNull(avg);
        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(avg!.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);
        Assert.Equal(15.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5);

        // The excluded (cross-model + timestamp-stale) profiles were NOT rebuilt: no LLM call.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Stale/cross-model profiles are excluded from the mean, never rebuilt");
    }

    // ─── 9. Scope selection: Scene → chapter profile, Chapter → book average ──────────────────────

    [Fact]
    public async Task BuildContextAsync_ChapterScope_UsesBookAverageBaseline_WhenProfilesExist()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Chapter-Scope Average Book", Language = language });

        // Two OTHER chapters with profiles, plus the chapter under analysis (also profiled). Their
        // averageSentenceLength values are 14 / 18 / 22 → mean 18.
        var underAnalysisId = Guid.NewGuid();
        var otherIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var allIds = new[] { underAnalysisId, otherIds[0], otherIds[1] };
        var lengths = new[] { 14.0, 18.0, 22.0 };

        for (var i = 0; i < allIds.Length; i++)
        {
            db.Chapters.Add(new Chapter
            {
                Id = allIds[i],
                BookId = bookId,
                Order = i,
                Title = $"Chapter {i}",
                ContentText = "תוכן הפרק לניתוח לשוני ברמת הפרק."
            });
            db.ChapterStyleProfiles.Add(new ChapterStyleProfile
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                ChapterId = allIds[i],
                Language = language,
                MetricsJson = JsonSerializer.Serialize(new
                {
                    syntaxMetrics = new { sentenceCount = 5, averageSentenceLength = lengths[i] },
                    morphologyMetrics = new { wordCount = 90, uniqueWords = 70, averageWordLength = 4.0, lexicalDensity = 0.55 },
                    grammaticalityScore = 0.85,
                    deviations = Array.Empty<object>(),
                    consistencyIssues = Array.Empty<object>()
                }),
                // Model-fresh so the book-average aggregation reads profiles without rebuilding (no LLM).
                BuiltWithModel = ActiveModel
            });
        }

        await db.SaveChangesAsync();

        // Make all chapters FRESH so no rebuild fires.
        foreach (var c in db.Chapters.Local.ToList())
        {
            var entry = db.Entry(c);
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
            entry.State = EntityState.Unchanged;
        }

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            underAnalysisId,
            AnalysisType.LinguisticAnalysis,
            language,
            CancellationToken.None);

        // Chapter scope → ChapterStyleBaseline is the BOOK AVERAGE (mean averageSentenceLength = 18),
        // not the chapter's own profile (which had 14).
        Assert.NotNull(context.ChapterStyleBaseline);
        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(
            context.ChapterStyleBaseline!.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);
        Assert.Equal(18.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5);

        // The synthetic book-average baseline has no ChapterId (it is an aggregate, not a chapter row).
        Assert.Equal(Guid.Empty, context.ChapterStyleBaseline.ChapterId);

        // BookStyleAverages (the StyleProfileData slot) stays null.
        Assert.Null(context.BookStyleAverages);

        // Aggregating fresh profiles needs no LLM call.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BuildContextAsync_SceneScope_UsesChapterProfileBaseline_NotBookAverage()
    {
        // Scene scope must keep using the chapter's OWN profile (averageSentenceLength = 14), even though
        // sibling chapters with different metrics exist (which would pull a book average to 40).
        var metricsPayload = """
            {
              "syntaxMetrics": { "sentenceCount": 5, "averageSentenceLength": 14.0 },
              "morphologyMetrics": { "wordCount": 90, "uniqueWords": 70, "averageWordLength": 4.0, "lexicalDensity": 0.55 },
              "styleMetrics": { "formality": "literary", "readability": 0.75, "voiceBalance": "active" },
              "grammaticalityScore": 0.85,
              "summary": "Own chapter baseline.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        using var provider = BuildServiceProvider(out _, llmResponse: metricsPayload);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        const string language = "he";
        var chapterId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Scene-Scope Book", Language = language });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Order = 0,
            Title = "Chapter Under Analysis",
            ContentText = "The full chapter text used to compute the scene's chapter baseline."
        });

        // Two sibling chapters, each WITH a FRESH, same-model profile of a very different
        // averageSentenceLength (40). For this to be a REAL regression guard the sibling profiles must be
        // eligible to enter a book average, which requires BOTH halves of the freshness gate to pass:
        //   • the profile must link to an ACTUAL chapter row (ChapterId = the sibling's real Id), because
        //     BuildBookStyleAverageProfileAsync excludes any profile whose ChapterId has no matching
        //     chapter ("chapter gone -> cannot judge freshness -> exclude"); an orphaned Guid.NewGuid()
        //     here is silently dropped, AND
        //   • the profile must carry BuiltWithModel = ActiveModel (a null/mismatched model is treated as
        //     stale and excluded too).
        // A profile that fails either check is excluded REGARDLESS of scope, which would make the value-40
        // "trap" inert and this guard a no-op. We also seed TWO of them because the book average needs at
        // least two fresh same-model profiles to be non-null. With both in place, a wrong-scope (book
        // average) implementation would surface the book mean (40), failing the 14.0 assertion below.
        var siblingOrder = 1;
        foreach (var siblingTitle in new[] { "Sibling A", "Sibling B" })
        {
            var siblingId = Guid.NewGuid();
            db.Chapters.Add(new Chapter
            {
                Id = siblingId,
                BookId = bookId,
                Order = siblingOrder++,
                Title = siblingTitle,
                ContentText = "תוכן הפרק האחאי."
            });
            db.ChapterStyleProfiles.Add(new ChapterStyleProfile
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                ChapterId = siblingId,
                Language = language,
                // Same active model so the cross-model half of the freshness gate passes (else excluded).
                BuiltWithModel = ActiveModel,
                MetricsJson = JsonSerializer.Serialize(new
                {
                    syntaxMetrics = new { sentenceCount = 5, averageSentenceLength = 40.0 },
                    morphologyMetrics = new { wordCount = 90, uniqueWords = 70, averageWordLength = 4.0, lexicalDensity = 0.55 },
                    grammaticalityScore = 0.85,
                    deviations = Array.Empty<object>(),
                    consistencyIssues = Array.Empty<object>()
                })
            });
        }

        var sceneSfdt = new SfdtConversionService().ConvertToSfdt(
            new System.Collections.Generic.List<DocumentFormat.OpenXml.OpenXmlElement>
            {
                new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text("The scene under analysis with several words here.")))
            }).SfdtJson;
        db.Scenes.Add(new Scene { Id = sceneId, ChapterId = chapterId, Order = 0, Title = "Scene", ContentSfdt = sceneSfdt });

        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var context = await svc.BuildContextAsync(
            AnalysisScope.Scene,
            sceneId,
            AnalysisType.LinguisticAnalysis,
            language,
            CancellationToken.None);

        // Scene scope → the chapter's OWN baseline (built via LLM here = 14.0), NOT the book average.
        Assert.NotNull(context.ChapterStyleBaseline);
        Assert.Equal(chapterId, context.ChapterStyleBaseline!.ChapterId);
        var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(
            context.ChapterStyleBaseline.MetricsJson, DeserializeOpts);
        Assert.NotNull(metrics);
        Assert.Equal(14.0, metrics!.SyntaxMetrics.AverageSentenceLength, precision: 5);
    }

    // ─── DEF-1: cross-model cache safety ──────────────────────────────────────────────────────────
    // A cached profile built under a DIFFERENT model than the active LinguisticAnalysis model must be
    // treated as STALE and rebuilt (never served as a cross-model baseline). Under the empty AiOptions in
    // this helper the active (config-resolved) model is AiOptions.DefaultModel = "qwen2.5:14b" (= the
    // ActiveModel constant), while the mock router reports "test-model" on a rebuild.

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_ModelMismatch_RebuildsAndRestampsRow()
    {
        var newMetricsJson = JsonSerializer.Serialize(new
        {
            syntaxMetrics = new { sentenceCount = 11, averageSentenceLength = 13.0 },
            morphologyMetrics = new { wordCount = 130, uniqueWords = 95, averageWordLength = 4.4, lexicalDensity = 0.66 },
            grammaticalityScore = 0.93,
            deviations = Array.Empty<object>(),
            consistencyIssues = Array.Empty<object>()
        });

        using var provider = BuildServiceProvider(out var routerMock, llmResponse: newMetricsJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";
        const string oldMetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":3}}";

        db.Books.Add(new Book { Id = bookId, Title = "Model Mismatch Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "פרק עם תוכן לבדיקת אי-התאמת מודל."
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = oldMetricsJson,
            // Built under a model that is NOT the active model → cross-model stale even though timestamps
            // make it timestamp-fresh.
            BuiltWithModel = "some-old-model"
        });
        await db.SaveChangesAsync();

        // Make the profile TIMESTAMP-fresh (newer than the chapter) so ONLY the model mismatch can make it
        // stale - this isolates the DEF-1 model gate from the timestamp gate.
        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // The model mismatch alone forced a rebuild (LLM called once).
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "A model-mismatched profile must be rebuilt even when timestamp-fresh");

        // Same row, restamped with the model actually used (the router-reported "test-model") and new metrics.
        Assert.NotNull(result);
        Assert.Equal(profileId, result!.Id);
        Assert.NotEqual(oldMetricsJson, result.MetricsJson);
        Assert.Equal("test-model", result.BuiltWithModel);
        Assert.Equal(1, await db.ChapterStyleProfiles.CountAsync());
    }

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_NullBuiltWithModel_LegacyRow_TreatedAsStaleAndRebuilt()
    {
        var newMetricsJson = JsonSerializer.Serialize(new
        {
            syntaxMetrics = new { sentenceCount = 7 },
            deviations = Array.Empty<object>(),
            consistencyIssues = Array.Empty<object>()
        });

        using var provider = BuildServiceProvider(out var routerMock, llmResponse: newMetricsJson);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Legacy Row Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "פרק ישן עם פרופיל ללא מודל."
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":99}}",
            BuiltWithModel = null // legacy row created before the column existed
        });
        await db.SaveChangesAsync();

        // Timestamp-fresh so only the null-model can make it stale (the one-time self-heal).
        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        // A null-model legacy row is stale → rebuilt once and stamped with the active/used model.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "A legacy null-BuiltWithModel row must self-heal (rebuild) on next access");
        Assert.NotNull(result);
        Assert.Equal("test-model", result!.BuiltWithModel);
    }

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_MatchingModelAndFreshTimestamp_NoRebuild()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";
        const string existingMetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":7}}";

        db.Books.Add(new Book { Id = bookId, Title = "Matching Model Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "פרק שלא השתנה ובנוי במודל הפעיל."
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = existingMetricsJson,
            // Built under the ACTIVE model AND timestamp-fresh → a true cache hit, no rebuild.
            BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();

        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        Assert.NotNull(result);
        Assert.Equal(profileId, result!.Id);
        Assert.Equal(existingMetricsJson, result.MetricsJson);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "A model-matching, timestamp-fresh profile is a cache hit (no rebuild)");
    }

    [Fact]
    public async Task LoadOrBuildChapterStyleProfileAsync_EmptyContent_ModelMismatch_ReturnsNullNoCrossModelServe()
    {
        using var provider = BuildServiceProvider(out var routerMock);
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        const string language = "he";

        db.Books.Add(new Book { Id = bookId, Title = "Empty + Mismatch Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter",
            ContentText = "" // no analysable text → a rebuild is impossible
        });

        var profileId = Guid.NewGuid();
        db.ChapterStyleProfiles.Add(new ChapterStyleProfile
        {
            Id = profileId,
            BookId = bookId,
            ChapterId = chapterId,
            Language = language,
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":12}}",
            BuiltWithModel = "some-old-model" // cross-model
        });
        await db.SaveChangesAsync();

        // TIMESTAMP-fresh so ONLY the model mismatch is in play; with empty content a rebuild cannot run,
        // so the safe behaviour is to return null rather than serve the cross-model profile.
        var chapterEntry = db.Entry(db.Chapters.Local.Single(c => c.Id == chapterId));
        chapterEntry.Entity.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        chapterEntry.State = EntityState.Unchanged;

        var svc = provider.GetRequiredService<IAnalysisContextService>();

        var result = await svc.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, language);

        Assert.Null(result);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Empty content cannot rebuild; a cross-model profile must not be served");
    }

    /// <summary>AppDbContext whose SaveChangesAsync can be made to throw, to simulate a save failure.</summary>
    private sealed class ThrowOnSaveDbContext : AppDbContext
    {
        public bool ThrowOnSave { get; set; }

        public ThrowOnSaveDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => ThrowOnSave
                ? throw new DbUpdateException("Simulated stale-refresh save failure")
                : base.SaveChangesAsync(cancellationToken);
    }

    // ─── Helper: build a DI ServiceProvider matching TextNormalizationAndContextTests convention ─

    private static ServiceProvider BuildServiceProvider(
        out Mock<IAiRouter> routerMock,
        string? llmResponse = null,
        bool llmThrows = false)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt =>
            opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        routerMock = new Mock<IAiRouter>();

        if (llmThrows)
        {
            routerMock
                .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Simulated LLM failure"));
        }
        else
        {
            var content = llmResponse ?? "[]";
            routerMock
                .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AiResponse
                {
                    Content = content,
                    Model = "test-model",
                    Provider = "test-provider"
                });
        }

        services.AddSingleton(routerMock.Object);
        services.Configure<AiOptions>(_ => { });
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();

        return services.BuildServiceProvider();
    }
}
