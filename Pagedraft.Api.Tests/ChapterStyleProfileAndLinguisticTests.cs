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
            MetricsJson = "{\"syntaxMetrics\":{\"sentenceCount\":5}}"
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

        // ConsistencyIssues — all three types
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

    // ─── 4. BuildContextAsync wiring — LinguisticAnalysis scope with StyleProfile present ─────

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

    // ─── 4. BuildContextAsync wiring — LinguisticAnalysis scope, no StyleProfile → null ──────

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

    // ─── 4. BuildContextAsync wiring — non-LinguisticAnalysis → baseline stays null ──────────

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

        // Proofread is not LinguisticAnalysis — ChapterStyleBaseline must remain null
        var context = await svc.BuildContextAsync(
            AnalysisScope.Chapter,
            chapterId,
            AnalysisType.Proofread,
            "he",
            CancellationToken.None);

        Assert.Null(context.ChapterStyleBaseline);
        Assert.Null(context.BookStyleAverages);
    }

    // ─── 5. Context envelope wiring — LinguisticAnalysis pulls preceding/following neighbours ──
    // Regression test for the follow-up that wired the context envelope into the LinguisticAnalysis
    // scope (so the prompt can detect cross-paragraph register/tense/POV breaks at scene boundaries).

    [Fact]
    public async Task BuildContextAsync_LinguisticAnalysis_PopulatesContextEnvelopeFromAdjacentChapters()
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

        // Preceding comes from the previous chapter's tail; following from the next chapter's head.
        Assert.False(string.IsNullOrWhiteSpace(context.PrecedingContext), "PrecedingContext should be populated for LinguisticAnalysis");
        Assert.Contains("הקודם", context.PrecedingContext!);
        Assert.False(string.IsNullOrWhiteSpace(context.FollowingContext), "FollowingContext should be populated for LinguisticAnalysis");
        Assert.Contains("הבא", context.FollowingContext!);
    }

    // ─── 5. Context envelope wiring — non-envelope analysis type leaves neighbours null ────────

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
            MetricsJson = existingMetricsJson
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

    // ─── 8. Bug 2: chapter-scope LinguisticAnalysis must NOT build a baseline (no self-comparison) ─
    // For Chapter scope the analysed text IS the whole chapter, so a chapter-vs-itself baseline would
    // surface stochastic `deviations`. The baseline (and its extra LLM call) is skipped; only Scene
    // scope compares a scene against its chapter.

    [Fact]
    public async Task BuildContextAsync_ChapterScopeLinguistic_SkipsBaselineAndLlm()
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

        // No baseline at chapter scope (would be the chapter compared against itself).
        Assert.Null(context.ChapterStyleBaseline);

        // The baseline build (a full-chapter LLM pass) is skipped entirely.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Chapter-scope LinguisticAnalysis must not trigger the baseline LLM call");

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
