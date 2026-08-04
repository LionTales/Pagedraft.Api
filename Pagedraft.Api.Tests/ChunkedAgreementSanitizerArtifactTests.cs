using System;
using System.Linq;
using System.Threading.Tasks;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// PHENOMENON (C) OF THE PUNCTUATION SPLIT: whitespace-only "corrections" nobody made.
///
/// WHAT c1 SAW. Every proofread response - single-shot AND per chunk - goes through
/// <c>UnifiedAnalysisService.SanitizeResponse</c> (UnifiedAnalysisService.cs:3252), which calls
/// <c>SyncfusionWatermarkStripper.StripSyncfusionWatermark</c> (SyncfusionWatermarkStripper.cs:38).
/// That stripper ends with an UNCONDITIONAL
/// <code>result = Regex.Replace(result, @"[\r\n]+", "\n");</code>
/// so it collapses every run of line breaks to a single newline whether or not a watermark was
/// present. Driven through c1's harness, a 22-paragraph Hebrew chapter with a model that changed
/// NOTHING came back with ~19 user-facing corrections, every one of them whitespace-only.
///
/// THE c2 VERDICT: THAT WAS A HARNESS ARTIFACT, NOT A PRODUCT DEFECT - and the production sanitizer
/// is NOT changed. The collapse is one half of a SYMMETRIC pair:
///  - the OUTPUT side runs it inside <c>SanitizeResponse</c>;
///  - the INPUT side runs the SAME function on the chapter/scene plain text before it ever becomes
///    <c>AnalysisContext.TargetText</c> (AnalysisContextService.cs:739 for a chapter, :1082/:1093 for
///    a scene), and <c>RunAsync</c> diffs against exactly that string
///    (UnifiedAnalysisService.cs:380).
/// Because the function is IDEMPOTENT, an echoing model produces a byte-identical result and the diff
/// is empty. c1's mocked <c>IAnalysisContextService</c> set <c>TargetText</c> to the RAW fixture text,
/// which no production path can produce, so only the output side was collapsed and the asymmetry
/// manufactured the suggestions. The fix is at the layer that caused it: the harness now feeds
/// <c>ChunkedAgreementHarness.ProductionTargetText</c>.
///
/// AND THE INPUT-SIDE COLLAPSE MUST NOT BE "FIXED" EITHER. It is load-bearing: Syncfusion's
/// <c>GetText()</c> joins paragraphs with CRLF, which <c>NormalizeTextForAnalysis</c> maps to TWO
/// spaces, while the FE offset walk advances a constant one character per block boundary. The collapse
/// to a single <c>\n</c> is what keeps suggestion offsets aligned past the first paragraph break - see
/// the be-c01 invariant pinned by <c>TextNormalizationAndContextTests</c>. Suppressing whitespace-only
/// suggestions in the diff instead would also have been wrong: it would have hidden a real asymmetry
/// rather than removed one.
///
/// The three tests below are the three claims: the mechanism is real, production is symmetric, and the
/// production regime therefore yields none of them.
/// </summary>
public class ChunkedAgreementSanitizerArtifactTests
{
    /// <summary>
    /// THE MECHANISM, at the smallest possible scale and directly on the shipped sanitizer. Both
    /// halves matter: a blank line is destroyed, and a single line break is left alone (so this is a
    /// paragraph-structure normalization, not a general whitespace collapse). Nothing about a
    /// watermark is involved - the inputs carry none.
    /// </summary>
    [Fact]
    public void TheProofreadResponseSanitizer_CollapsesEveryBlankLine_EvenWithNoWatermarkPresent()
    {
        Assert.Equal("א.\nב.", UnifiedAnalysisService.SanitizeResponse("א.\n\nב."));
        Assert.Equal("א.\nב.", UnifiedAnalysisService.SanitizeResponse("א.\n\n\n\nב."));
        Assert.Equal("א.\nב.", UnifiedAnalysisService.SanitizeResponse("א.\r\n\r\nב."));

        // A single break is untouched, so what it normalizes is specifically PARAGRAPH separation.
        Assert.Equal("א.\nב.", UnifiedAnalysisService.SanitizeResponse("א.\nב."));

        Assert.DoesNotContain("Syncfusion", UnifiedAnalysisService.SanitizeResponse("א.\n\nב."),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE SYMMETRY that makes the mechanism harmless in production, asserted as the two facts it
    /// rests on rather than inferred:
    ///  (1) the stripper is IDEMPOTENT, so running it on both sides cannot produce a difference; and
    ///  (2) applying it to only the OUTPUT side genuinely does produce whitespace-only suggestions -
    ///      i.e. c1's observation reproduces exactly when, and only when, the input side is bypassed.
    ///
    /// (2) is the non-vacuity guard for the production-regime claim in the next test: without it,
    /// "the production regime produces no whitespace-only suggestions" would also pass if the diff had
    /// simply stopped emitting them for some unrelated reason.
    /// </summary>
    [Fact]
    public void OnlyTheASYMMETRICApplication_ProducesWhitespaceOnlySuggestions()
    {
        var raw = string.Join("\n\n", ChunkedAgreementFixtures.Filler.Take(4));
        Assert.Equal(3, CountBlankLines(raw)); // non-vacuity: the input really has paragraph breaks

        // (1) Idempotent, so the symmetric application is a no-op difference.
        var stripped = SyncfusionWatermarkStripper.StripSyncfusionWatermark(raw);
        Assert.Equal(stripped, SyncfusionWatermarkStripper.StripSyncfusionWatermark(stripped));
        Assert.Equal(0, CountBlankLines(stripped));

        var diff = new SuggestionDiffService();

        // The PRODUCTION pairing: both sides stripped, model echoed => nothing to report.
        Assert.Empty(diff.ComputeProofreadSuggestions(stripped, UnifiedAnalysisService.SanitizeResponse(stripped)));

        // c1's pairing: raw original vs sanitized response => corrections the model never made.
        var asymmetric = diff.ComputeProofreadSuggestions(raw, UnifiedAnalysisService.SanitizeResponse(raw));
        Assert.NotEmpty(asymmetric);
        Assert.All(asymmetric, s => Assert.True(
            string.IsNullOrWhiteSpace(s.OriginalText) && string.IsNullOrWhiteSpace(s.SuggestedText),
            $"expected a whitespace-only artifact, got '{s.OriginalText}' -> '{s.SuggestedText}'"));
    }

    /// <summary>
    /// THE END-TO-END CONSEQUENCE on the production regime, through the real entry point with a model
    /// that changed NOTHING: a multi-paragraph, multi-chunk Hebrew chapter round-trips to a
    /// byte-identical result and ZERO suggestions.
    ///
    /// NON-VACUITY: the fixture is first proved to be a multi-paragraph, multi-chunk, chunk-ROUTED
    /// input, because "no suggestions" is also what a run that never reached the chunked path, or an
    /// input with no paragraph breaks to lose, would produce.
    /// </summary>
    [Fact]
    public async Task AnUntouchedMultiParagraphChapter_RoundTripsWithNoCorrectionsAtAll()
    {
        var fixture = ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.SeparatedAndDilutedId);

        // Non-vacuity, before the run: the RAW fixture is the multi-paragraph shape that would have
        // produced the artifact, and the production regime is what neutralizes it.
        Assert.True(CountBlankLines(fixture.Text) > 0);

        var run = await ChunkedAgreementHarness.RunAsync(fixture);

        Assert.True(run.RanChunked, "the run did not take the chunked path, so it measured the wrong regime");
        Assert.True(run.Chunks.Count > 1, "a single-chunk run cannot exercise the merge, so it proves less");

        Assert.Empty(run.Result.Suggestions);
        Assert.Empty(run.WhitespaceOnlySuggestions);
        Assert.Empty(run.SubstantiveSuggestions);

        // ...and the persisted artifact is the input, unchanged. The merge re-appends each chunk's own
        // SeparatorAfter after sanitization, so this also pins that the separators survived.
        Assert.Equal(ChunkedAgreementHarness.ProductionTargetText(fixture), run.Result.ResultText);
    }

    /// <summary>
    /// It is not a chunking artifact either way: the same sanitizer runs on the single-shot path. Kept
    /// as the direct statement of that, since the single-chunk control fixture is one paragraph and so
    /// would show nothing.
    /// </summary>
    [Fact]
    public void TheSameCollapse_AppliesToTheSingleShotPath_SoItIsNotAChunkingArtifact()
    {
        var twoParagraphs = ChunkedAgreementFixtures.Filler[0] + "\n\n" + ChunkedAgreementFixtures.Filler[1];
        Assert.Equal(1, CountBlankLines(twoParagraphs));

        var sanitized = UnifiedAnalysisService.SanitizeResponse(twoParagraphs);
        Assert.Equal(0, CountBlankLines(sanitized));
        Assert.Contains("\n", sanitized, StringComparison.Ordinal);
    }

    /// <summary>Blank-line (paragraph) separators: runs of two or more consecutive line breaks.</summary>
    private static int CountBlankLines(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"(\r?\n){2,}").Count;
}
