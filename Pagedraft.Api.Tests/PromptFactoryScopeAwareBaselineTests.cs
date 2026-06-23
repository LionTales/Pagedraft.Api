using System;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Verifies that the [CHAPTER_STYLE_BASELINE] section emitted by PromptFactory.GetAnalysisPrompt is
/// scope-aware (be-c04). At Scene scope the reference is the CHAPTER and the analyzed unit is the scene;
/// at Chapter (and Book) scope the analyzed unit is the WHOLE CHAPTER and the injected baseline is the
/// BOOK AVERAGE, so the model must frame its free-text note about the BOOK, never scene-vs-chapter. This
/// mirrors the FE reference logic (linguistic-result.component.ts): Scene -> chapter, Chapter/Book -> book.
/// </summary>
public class PromptFactoryScopeAwareBaselineTests
{
    // A minimal, well-formed LinguisticAnalysisResult-shaped MetricsJson so FormatChapterStyleBaseline
    // parses it and renders the metric lines under a scope-aware header (it returns empty otherwise).
    private const string MetricsJson = """
        {
          "syntaxMetrics": { "sentenceCount": 8, "averageSentenceLength": 15.0, "complexSentences": 2, "shortestSentence": 4, "longestSentence": 30 },
          "morphologyMetrics": { "wordCount": 120, "uniqueWords": 90, "averageWordLength": 4.5, "lexicalDensity": 0.75 },
          "styleMetrics": { "formality": "literary", "readability": 0.8, "voiceBalance": "active" },
          "grammaticalityScore": 0.95,
          "summary": "Baseline.",
          "deviations": [],
          "consistencyIssues": []
        }
        """;

    private static AnalysisContext BuildContext(AnalysisScope scope) => new()
    {
        TargetText = "The analyzed text under linguistic analysis.",
        AnalysisType = AnalysisType.LinguisticAnalysis,
        Scope = scope,
        BookId = Guid.NewGuid(),
        ChapterId = Guid.NewGuid(),
        ChapterStyleBaseline = new ChapterStyleProfile { MetricsJson = MetricsJson }
    };

    /// <summary>
    /// Returns just the text BETWEEN the [CHAPTER_STYLE_BASELINE] markers so assertions target the
    /// scope-aware header and not the (now scope-neutral) static base-prompt framing.
    /// </summary>
    private static string ExtractBaselineSection(string prompt)
    {
        const string open = "[CHAPTER_STYLE_BASELINE]";
        const string close = "[/CHAPTER_STYLE_BASELINE]";
        var start = prompt.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "Prompt must contain an injected [CHAPTER_STYLE_BASELINE] section.");
        start += open.Length;
        var end = prompt.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end > start, "Prompt must contain a closing [/CHAPTER_STYLE_BASELINE] marker.");
        return prompt.Substring(start, end - start);
    }

    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public void ChapterScope_BaselineSection_FramedAsBook_NotSceneVsChapter(string language)
    {
        var factory = new PromptFactory();
        var ctx = BuildContext(AnalysisScope.Chapter);

        var prompt = factory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, language, ctx);
        var section = ExtractBaselineSection(prompt);

        // Chapter scope: the reference is the BOOK AVERAGE and the analyzed unit is the WHOLE CHAPTER.
        Assert.Contains("Book-wide AVERAGE style metrics", section, StringComparison.Ordinal);
        Assert.Contains("BOOK-average", section, StringComparison.Ordinal);
        Assert.Contains("WHOLE CHAPTER", section, StringComparison.Ordinal);

        // It must NOT carry the scene-vs-chapter framing that produced the contradictory note.
        Assert.DoesNotContain("Compare the current SCENE against these CHAPTER numbers", section, StringComparison.Ordinal);
        Assert.DoesNotContain("Chapter-wide baseline metrics.", section, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public void SceneScope_BaselineSection_FramedAsSceneVsChapter(string language)
    {
        var factory = new PromptFactory();
        var ctx = BuildContext(AnalysisScope.Scene);

        var prompt = factory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, language, ctx);
        var section = ExtractBaselineSection(prompt);

        // Scene scope: the reference is the CHAPTER and the analyzed unit is the scene.
        Assert.Contains("Chapter-wide baseline metrics.", section, StringComparison.Ordinal);
        Assert.Contains("Compare the current SCENE against these CHAPTER numbers", section, StringComparison.Ordinal);

        // It must NOT carry the book framing reserved for chapter/book scope.
        Assert.DoesNotContain("Book-wide AVERAGE style metrics", section, StringComparison.Ordinal);
        Assert.DoesNotContain("WHOLE CHAPTER", section, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public void StaticBasePromptFraming_IsScopeNeutral(string language)
    {
        // The static base-prompt explanation must no longer hardcode a scene-vs-chapter framing that
        // could contradict the scope-aware section; it must defer to the [CHAPTER_STYLE_BASELINE] section.
        var factory = new PromptFactory();
        var basePrompt = factory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, language);

        if (language == "en")
        {
            Assert.Contains("reference baseline style metrics", basePrompt, StringComparison.Ordinal);
            Assert.Contains("divergence from the reference baseline", basePrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("the chapter's own reference line", basePrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("divergence from the chapter baseline", basePrompt, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("מדדי סגנון של קו ייחוס", basePrompt, StringComparison.Ordinal);
            Assert.Contains("מקו הייחוס", basePrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("קו ייחוס של הפרק עצמו", basePrompt, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public void BasePrompt_DeviationsExplanation_PrefersNormalizedMetrics(string language)
    {
        // be-c05: the deviations field explanation must soft-prefer NORMALIZED/rate metrics
        // (averageSentenceLength, lexicalDensity, averageWordLength) over length-confounded raw counts
        // (sentenceCount, wordCount, uniqueWords), which are reported only when stylistically meaningful.
        // Scope-neutral: it lives in the base prompt, so the 2-arg overload is sufficient.
        var factory = new PromptFactory();
        var basePrompt = factory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, language);

        // The preferred normalized/rate metric identifiers are named as preferred deviations.
        Assert.Contains("averageSentenceLength", basePrompt, StringComparison.Ordinal);
        Assert.Contains("lexicalDensity", basePrompt, StringComparison.Ordinal);
        Assert.Contains("averageWordLength", basePrompt, StringComparison.Ordinal);

        // The raw absolute counts are named as the ones to report only when stylistically meaningful.
        Assert.Contains("sentenceCount", basePrompt, StringComparison.Ordinal);
        Assert.Contains("wordCount", basePrompt, StringComparison.Ordinal);
        Assert.Contains("uniqueWords", basePrompt, StringComparison.Ordinal);

        if (language == "en")
        {
            // Soft-preference wording for the normalized metrics.
            Assert.Contains("Prefer reporting NORMALIZED", basePrompt, StringComparison.Ordinal);
            Assert.Contains("reflect style independent of length", basePrompt, StringComparison.Ordinal);
            // Raw counts gated on a stylistic signal beyond the text's size.
            Assert.Contains("ONLY when it carries a stylistic signal beyond the size", basePrompt, StringComparison.Ordinal);
        }
        else
        {
            // Hebrew soft-preference wording (DRAFT, pending native-speaker validation).
            Assert.Contains("העדף לדווח על מדדים מנורמלים", basePrompt, StringComparison.Ordinal);
            Assert.Contains("ללא תלות באורך", basePrompt, StringComparison.Ordinal);
            // Raw counts gated on a stylistic signal beyond the text's size.
            Assert.Contains("רק כאשר הוא נושא משמעות סגנונית מעבר לגודל", basePrompt, StringComparison.Ordinal);
        }
    }
}
