using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Pagedraft.Api.Services.Export;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The chunked proofread merge (plan whole-book-proofread-and-annotated-docx-2026-10-11, c17). The chunker trims every
/// paragraph / sentence / chunk edge and re-joins with canonical separators; the OLD merge glued the model outputs back
/// with those separators only, so every trimmed space, NO-BREAK SPACE, CR or blank line came back as a stored
/// whitespace-only deletion (4,007 of 4,676 rows on one 55-chapter book). <see cref="ProofreadChunkMerge"/> replays each
/// chunk's edits onto the SOURCE instead. Every fixture below is multi-paragraph and asserts a non-vacuity floor: the old
/// separator join DOES manufacture whitespace on it, so a green "0 suggestions" here measures the merge, not a fixture
/// with nothing to lose. Pure: no database (except the one end-to-end wiring test, in-memory), no model.
/// </summary>
public class ProofreadChunkMergeTests
{
    private static readonly SuggestionDiffService Diff = new();

    private sealed record Run(string Source, IReadOnlyList<string> Texts, IReadOnlyList<string> Seps, IReadOnlyList<string> Outputs)
    {
        public ProofreadChunkMerge.Result Merged => ProofreadChunkMerge.Merge(Source, Texts, Seps, Outputs);
        public string OldJoin => ProofreadChunkMerge.SeparatorJoin(Texts, Seps, Outputs);
        public List<AnalysisSuggestion> NewSuggestions => Diff.ComputeProofreadSuggestions(Source, Merged.Text);
        public List<AnalysisSuggestion> OldSuggestions => Diff.ComputeProofreadSuggestions(Source, OldJoin);
    }

    /// <summary>Chunk <paramref name="source"/> with the production chunker and run every chunk through <paramref name="model"/>.</summary>
    private static Run Chunked(string source, int targetWords, Func<string, string>? model = null)
    {
        var chunks = UnifiedAnalysisService.ChunkForProofreadForTest(source, targetWords);
        var texts = chunks.Select(c => c.Text).ToList();
        var outputs = texts.Select(t => string.IsNullOrWhiteSpace(t) || model is null ? t : model(t)).ToList();
        return new Run(source, texts, chunks.Select(c => c.SeparatorAfter).ToList(), outputs);
    }

    private static bool IsWhitespaceOnly(AnalysisSuggestion s) => ProofreadWhitespace.IsWhitespaceOnlyChange(s.OriginalText, s.SuggestedText);

    /// <summary>Whitespace-only INCLUDING inner whitespace (an NBSP between two words re-spaced), a superset of the client's rule.</summary>
    private static bool ChangesOnlyWhitespace(AnalysisSuggestion s) => NoWhitespace(s.OriginalText) == NoWhitespace(s.SuggestedText);

    private static string NoWhitespace(string? s) => new((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static void AssertOldJoinManufacturesWhitespace(Run run)
    {
        // Non-vacuity floor: the fixture really carries whitespace the chunker drops.
        Assert.NotEqual(run.Source, run.OldJoin);
        Assert.Contains(run.OldSuggestions, ChangesOnlyWhitespace);
    }

    private static void AssertIdentityRoundTrip(Run run)
    {
        var merged = run.Merged;
        Assert.True(merged.AlignedToSource);
        Assert.Equal(run.Source, merged.Text);
        Assert.Empty(run.NewSuggestions);
    }

    // ── identity model: the merged text IS the source ────────────────────────────────────────────────────────────

    [Fact]
    public void IdentityModel_ParagraphTrailingSpaceAndNbsp_MergesToTheSource()
    {
        var source = "הוא הלך הביתה. \nהיא נשארה בחוץ. \nהם נפגשו שוב.";
        var run = Chunked(source, 3);
        Assert.True(run.Texts.Count >= 3);
        AssertOldJoinManufacturesWhitespace(run);
        AssertIdentityRoundTrip(run);
    }

    [Fact]
    public void IdentityModel_LeadingIndentAndChapterEdgeWhitespace_MergesToTheSource()
    {
        var source = "   פתיחה של הפרק כאן\n\tפסקה שנייה עם הזחה\n   פסקה שלישית בסוף \n  ";
        var run = Chunked(source, 4);
        AssertOldJoinManufacturesWhitespace(run);
        AssertIdentityRoundTrip(run);
    }

    [Fact]
    public void IdentityModel_EmptyParagraphsAndMultipleBreaks_MergesToTheSource()
    {
        // Whitespace-only paragraphs are dropped by the chunker and a "\n \n \n" gap collapses to one "\n".
        var source = "אחת שתיים שלוש\n\n\nארבע חמש שש\n \n \nשבע שמונה תשע\n\nעשר אחת עשרה";
        foreach (var target in new[] { 3, 100 })
        {
            var run = Chunked(source, target);
            AssertOldJoinManufacturesWhitespace(run);
            AssertIdentityRoundTrip(run);
        }
    }

    [Fact]
    public void IdentityModel_CrLfInput_MergesToTheSource()
    {
        // The chunker splits on \n only, so each paragraph's CR is trimmed away; normalized, CRLF is two spaces.
        var source = "שורה ראשונה כאן\r\nשורה שנייה כאן\r\n\r\nשורה שלישית כאן";
        foreach (var target in new[] { 3, 100 })
        {
            var run = Chunked(source, target);
            AssertOldJoinManufacturesWhitespace(run);
            AssertIdentityRoundTrip(run);
        }
    }

    [Fact]
    public void IdentityModel_SentenceAndWordCountSplits_KeepTheSourceSpacing()
    {
        // A paragraph over the target splits on sentence ends (\s+ becomes one space) and an over-long sentence splits
        // by word count (words re-joined with one space), so a double space and a NO-BREAK SPACE inside are re-spaced.
        var source = "משפט אחד כאן.  משפט שני כאן. משפט שלישי כאן.\nמילה אחת  שתיים שלוש ארבע חמש שש שבע שמונה";
        var run = Chunked(source, 3);
        Assert.Contains(run.Texts, t => t == "משפט שני כאן.");
        AssertOldJoinManufacturesWhitespace(run);
        AssertIdentityRoundTrip(run);
    }

    [Fact]
    public void IdentityModel_ThroughTheProductionSanitizer_MergesToTheSource()
    {
        // SanitizeResponse collapses every horizontal run (NBSP included) to one space and 3+ line breaks to 2. That is
        // the pipeline re-spacing a run, not a proofreading edit, so the source's own run is kept.
        var source = "אחת שתיים  שלוש ארבע.\nחמש שש  שבע.\n\n\n\nשמונה תשע עשר.";
        var run = Chunked(source, 100, UnifiedAnalysisService.SanitizeResponse);
        Assert.Single(run.Texts);
        Assert.NotEqual(run.Texts[0], run.Outputs[0]); // the sanitizer did change the whitespace
        AssertOldJoinManufacturesWhitespace(run);
        AssertIdentityRoundTrip(run);
    }

    // ── a model that DOES edit, near an edge ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EditAtParagraphStart_AfterATrailingSpaceParagraph_IsOneCleanSuggestion()
    {
        // c15's fused shape: the old merge reported " כול" -> "כל", a span starting on the paragraph mark.
        var source = "הוא הלך הביתה.  \nכול הדרך שתק.";
        var run = Chunked(source, 3, t => t.Replace("כול", "כל"));

        var old = Assert.Single(run.OldSuggestions, s => s.OriginalText!.Contains("כול"));
        Assert.NotEqual("כול", old.OriginalText); // fused with the manufactured whitespace before c17

        Assert.Equal(source.Replace("כול", "כל"), run.Merged.Text);
        var s = Assert.Single(run.NewSuggestions);
        Assert.Equal("כול", s.OriginalText);
        Assert.Equal("כל", s.SuggestedText);
        Assert.Equal(source.IndexOf("כול", StringComparison.Ordinal), s.StartOffset);
    }

    [Fact]
    public void EditOfTheParagraphFinalWord_KeepsTheTrailingNbspAndBreak()
    {
        var source = "הוא הלך הבייתה \nואז נח קצת.";
        var run = Chunked(source, 3, t => t.Replace("הבייתה", "הביתה"));
        // Floor: before c17 the edit came back FUSED with the trimmed NBSP ("הבייתה " -> "הביתה").
        Assert.Contains(run.OldSuggestions, s => s.OriginalText != ProofreadWhitespace.Trim(s.OriginalText));

        Assert.Equal("הוא הלך הביתה \nואז נח קצת.", run.Merged.Text);
        var s = Assert.Single(run.NewSuggestions);
        Assert.Equal("הבייתה", s.OriginalText);
        Assert.Equal("הביתה", s.SuggestedText);
    }

    [Fact]
    public void PunctuationAddedAtTheParagraphEnd_LandsBeforeTheSourceWhitespace()
    {
        var source = "הוא הלך הביתה \nואז נח קצת";
        var run = Chunked(source, 3, t => t == "הוא הלך הביתה" ? "הוא הלך הביתה." : t);

        Assert.Equal("הוא הלך הביתה. \nואז נח קצת", run.Merged.Text);
        var s = Assert.Single(run.NewSuggestions);
        Assert.False(IsWhitespaceOnly(s));
    }

    [Fact]
    public void LastWordDeletedBeforeATrailingNbsp_KeepsTheNbsp()
    {
        var source = "הוא הלך הביתה הביתה \nואז נח קצת.";
        var run = Chunked(source, 4, t => t.Replace("הביתה הביתה", "הביתה"));

        Assert.Equal("הוא הלך הביתה \nואז נח קצת.", run.Merged.Text);
        var s = Assert.Single(run.NewSuggestions);
        Assert.False(IsWhitespaceOnly(s));
    }

    [Fact]
    public void WordsJoinedByTheModel_AreKeptAsAnEdit()
    {
        // Deleting a whole whitespace run (the words no longer stay apart) is a real correction, not re-spacing.
        var source = "הוא רצה ל הראות לה \nואז נח קצת.";
        var run = Chunked(source, 4, t => t.Replace("ל הראות", "להראות"));

        Assert.Equal("הוא רצה להראות לה \nואז נח קצת.", run.Merged.Text);
        var s = Assert.Single(run.NewSuggestions);
        Assert.Equal("ל הראות", s.OriginalText);
        Assert.Equal("להראות", s.SuggestedText);
    }

    [Fact]
    public void SpaceInsertedInsideAWord_IsKeptAsAnEdit()
    {
        var source = "הוא אמר שלוםלכולם בקול\nואז נח קצת.";
        var run = Chunked(source, 4, t => t.Replace("שלוםלכולם", "שלום לכולם"));

        Assert.Equal("הוא אמר שלום לכולם בקול\nואז נח קצת.", run.Merged.Text);
        var s = Assert.Single(run.NewSuggestions);
        Assert.False(IsWhitespaceOnly(s));
    }

    [Fact]
    public void ModelRespacingARun_KeepsTheSourceRun()
    {
        // NBSP -> space and paragraph break -> space inside one chunk: whitespace for whitespace, words still apart.
        var source = "אחת שתיים שלוש.\nארבע חמש שש.";
        var run = Chunked(source, 100, t => t.Replace(' ', ' ').Replace('\n', ' '));
        Assert.True(run.Merged.AlignedToSource);
        Assert.Equal(source, run.Merged.Text);
        Assert.Empty(run.NewSuggestions);
    }

    [Fact]
    public void EditReachingIntoABlankLineRun_ReplacesTheWholeSourceRun()
    {
        // The chunk keeps the "\n\n" separator but not the space before it. The model's edit ends INSIDE that run
        // ("ת\n" -> "ה"), so the run is the model's now: the whole source gap " \n\n" gives way to the model's "\n".
        var source = "אחת \n\nשתיים שלוש";
        var run = Chunked(source, 100, t => t.Replace("אחת\n\n", "אחה\n"));
        Assert.Equal("אחת\n\nשתיים שלוש", run.Texts.Single());

        var merged = run.Merged;
        Assert.True(merged.AlignedToSource);
        Assert.Equal("אחה\nשתיים שלוש", merged.Text);
    }

    [Fact]
    public void ChunksThatAreNotTheSourcesText_FallBackToTheSeparatorJoin()
    {
        var texts = new[] { "טקסט אחר לגמרי", "וגם זה" };
        var seps = new[] { "\n", "" };
        var outputs = new[] { "טקסט אחר לגמרי!", "וגם זה" };
        var merged = ProofreadChunkMerge.Merge("המקור שונה מהחתיכות\nבאמת", texts, seps, outputs);
        Assert.False(merged.AlignedToSource);
        Assert.Equal("טקסט אחר לגמרי!\nוגם זה", merged.Text);
    }

    // ── the identity-replay invariant on a synthetic multi-paragraph chapter ─────────────────────────────────────

    private static string SyntheticChapter()
    {
        // ~1,300 words, every whitespace shape c15 met on real chapters and the chunker drops: paragraph-final space
        // and NBSP, leading indentation, double space and NBSP after a full stop, blank and whitespace-only lines,
        // CRLF paragraphs, one over-long sentence with no full stop (word-count split), dialogue lines.
        var words = "הוא הלך אל הבית הישן ולא ראה את השמש שזרחה מעל ההרים הרחוקים בבוקר קר במיוחד של חורף".Split(' ');
        var sb = new StringBuilder("   ");
        var w = 0;
        string Next() => words[w++ % words.Length];
        string Sentence(int n) => string.Join(" ", Enumerable.Range(0, n).Select(_ => Next())) + ".";
        for (var p = 0; p < 60; p++)
        {
            if (p % 7 == 3) sb.Append("\t");
            sb.Append(Sentence(9 + p % 5));
            sb.Append(p % 3 == 0 ? "  " : p % 3 == 1 ? " " : " ");
            sb.Append(Sentence(8 + p % 4));
            sb.Append(p % 4 == 0 ? " " : p % 4 == 1 ? " " : p % 4 == 2 ? "  " : "");
            sb.Append(p % 5 == 0 ? "\r\n" : p % 5 == 1 ? "\n\n" : p % 5 == 2 ? "\n \n" : p % 5 == 3 ? "\n \n\n" : "\n");
            if (p % 9 == 4) sb.Append("\"כן,\" היא אמרה. \n\"לא,\" הוא ענה. \n");
        }
        sb.Append(string.Join(" ", Enumerable.Range(0, 70).Select(_ => Next()))).Append("  \n ");
        return sb.ToString();
    }

    [Theory]
    [InlineData(250)]
    [InlineData(40)]
    [InlineData(7)]
    public void SyntheticChapter_IdentityAndSanitizerReplays_ProduceNoSuggestions(int target)
    {
        var source = SyntheticChapter();
        Assert.True(source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 1000);

        var identity = Chunked(source, target);
        Assert.True(identity.Texts.Count > 1);
        AssertOldJoinManufacturesWhitespace(identity);
        AssertIdentityRoundTrip(identity);

        var sanitized = Chunked(source, target, UnifiedAnalysisService.SanitizeResponse);
        AssertOldJoinManufacturesWhitespace(sanitized);
        AssertIdentityRoundTrip(sanitized);
    }

    [Theory]
    [InlineData(250)]
    [InlineData(7)]
    public void SyntheticChapter_WordEditsEverywhere_YieldOnlyTheEditsAndNoWhitespaceRows(int target)
    {
        // A model that rewrites one word wherever it occurs, including at paragraph and chunk edges, then sanitized.
        var source = SyntheticChapter();
        var run = Chunked(source, target, t => UnifiedAnalysisService.SanitizeResponse(t.Replace("הבית", "הבתים").Replace("קר", "קרה")));

        var merged = run.Merged;
        Assert.True(merged.AlignedToSource);
        Assert.Equal(source.Replace("הבית", "הבתים").Replace("קר", "קרה"), merged.Text);
        var suggestions = run.NewSuggestions;
        Assert.NotEmpty(suggestions);
        Assert.DoesNotContain(suggestions, IsWhitespaceOnly);
        Assert.DoesNotContain(suggestions, ChangesOnlyWhitespace);
        Assert.Contains(run.OldSuggestions, ChangesOnlyWhitespace); // floor
    }

    // ── wiring: the production chunked path uses the merge ───────────────────────────────────────────────────────

    private static async Task<AnalysisResult> RunChunkedProofreadAsync(string inputText, Func<string, string> model)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                var text = req.InputText.Replace("[TEXT_TO_CORRECT]", "").Replace("[/TEXT_TO_CORRECT]", "");
                return new AiResponse { Content = model(text), Provider = "test", Model = "test" };
            });
        var svc = new UnifiedAnalysisService(
            db, router.Object, new PromptFactory(), new SfdtConversionService(), Options.Create(new AiOptions()),
            NullLogger<UnifiedAnalysisService>.Instance, new AnalysisProgressTracker(), new Mock<IAnalysisContextService>().Object,
            new SuggestionDiffService(), new KtivMaleChecker(new HebrewStyleOptions()),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());
        var method = typeof(UnifiedAnalysisService).GetMethod("RunProofreadChunkedAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var context = new AnalysisContext { TargetText = inputText, Scope = AnalysisScope.Chapter, AnalysisType = AnalysisType.Proofread };
        return await (Task<AnalysisResult>)method!.Invoke(svc, new object?[]
        {
            inputText, null, null, null, AnalysisScope.Chapter, Guid.NewGuid(), null, "he",
            3, 1, AiTier.Fast, Guid.NewGuid(), context, CancellationToken.None,
        })!;
    }

    [Fact]
    public async Task ChunkedRun_EchoModel_StoresTheSourceAndNoSuggestions()
    {
        var input = "הוא הלך הביתה.  \nהיא נשארה בחוץ. \n\nהם נפגשו שוב \r\nבסוף היום.";
        var result = await RunChunkedProofreadAsync(input, t => t);

        Assert.Equal("chunked", result.ModelName);
        Assert.Equal(input, result.ResultText);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public async Task ChunkedRun_EditAtAParagraphStart_StoresOneCleanSuggestion()
    {
        var input = "הוא הלך הביתה.  \nכול הדרך שתק. \nהם נפגשו שוב.";
        var result = await RunChunkedProofreadAsync(input, t => t.Replace("כול", "כל"));

        var s = Assert.Single(result.Suggestions);
        Assert.Equal("כול", s.OriginalText);
        Assert.Equal("כל", s.SuggestedText);
        Assert.Equal(input.IndexOf("כול", StringComparison.Ordinal), s.StartOffset);
    }
}
