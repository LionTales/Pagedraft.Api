using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Export;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The chunked proofread merge on REAL prose (be-c10). Every other merge test runs on authored text
/// (<see cref="ProofreadChunkMergeTests"/>) and the proofread gold harness is single-shot, so before this file no
/// committed test chunked a real manuscript and merged it back. Source: the twelve verbatim, contiguous excerpts in
/// <see cref="RealProsePassages"/> (<see cref="RealProsePrecisionFixtures.SourceManuscript"/>, cleared for use as
/// proofread test data and already committed), 4,000+ words of narration and dialogue with the author's own
/// punctuation, joined into one chapter in each of the paragraph-separator shapes a stored chapter carries.
///
/// <para>Chunked at the PRODUCTION Hebrew target (<see cref="UnifiedAnalysisService.ProofreadChunkTargetWordsFor"/>
/// on default options), so the dialogue-aware grouping, the sentence split and the chunk boundaries are the ones a
/// real run makes. An identity model (each chunk returned unchanged) and the same model behind the production
/// response sanitizer must both merge back to the source byte for byte, with no suggestion at all (so none that is
/// whitespace-only). The non-vacuity floor is the chunk count (at least 3, so several chunk boundaries and the text
/// between chunks are exercised), plus, per separator shape, proof that a naive separator join or the sanitizer
/// really does change the text the merge has to restore.</para>
///
/// Deterministic: no model, no GPU, in-memory database for the end-to-end case only.
/// </summary>
public class ProofreadChunkMergeRealProseTests
{
    private static readonly SuggestionDiffService Diff = new();

    /// <summary>The production Hebrew chunk target on default options (~250 words).</summary>
    private static int HebrewTarget => UnifiedAnalysisService.ProofreadChunkTargetWordsFor(new AiOptions(), "he");

    private static string Chapter(string separator) =>
        string.Join(separator, RealProsePassages.All.SelectMany(p => p));

    private static List<(string Text, string SeparatorAfter, string? OverlapPrefix)> ChunkAtTheHebrewTarget(string source)
    {
        var chunks = UnifiedAnalysisService.ChunkForProofreadForTest(source, HebrewTarget);
        Assert.True(chunks.Count >= 3, $"real-prose fixture must cut into at least 3 chunks at the Hebrew target {HebrewTarget}; got {chunks.Count}");
        return chunks;
    }

    private static (ProofreadChunkMerge.Result Merged, List<AnalysisSuggestion> Suggestions, bool OutputsDiffer, string OldJoin)
        MergeThrough(string source, Func<string, string> model)
    {
        var chunks = ChunkAtTheHebrewTarget(source);
        var texts = chunks.Select(c => c.Text).ToList();
        var seps = chunks.Select(c => c.SeparatorAfter).ToList();
        var outputs = texts.Select(model).ToList();
        var merged = ProofreadChunkMerge.Merge(source, texts, seps, outputs);
        return (merged, Diff.ComputeProofreadSuggestions(source, merged.Text),
            !texts.SequenceEqual(outputs, StringComparer.Ordinal), ProofreadChunkMerge.SeparatorJoin(texts, seps, outputs));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\n\n")]
    [InlineData("\r\n")]
    public void RealProse_IdentityModel_MergesBackToTheSource(string separator)
    {
        var source = Chapter(separator);
        Assert.True(RealProsePrecisionFixtures.WordCount(source) > 4000);

        var identity = MergeThrough(source, t => t);
        Assert.True(identity.Merged.AlignedToSource);
        Assert.Equal(source, identity.Merged.Text);
        Assert.Empty(identity.Suggestions);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\n\n")]
    [InlineData("\r\n")]
    public void RealProse_IdentityModelBehindTheProductionSanitizer_MergesBackToTheSource(string separator)
    {
        var source = Chapter(separator);
        var sanitized = MergeThrough(source, UnifiedAnalysisService.SanitizeResponse);

        Assert.True(sanitized.Merged.AlignedToSource);
        Assert.Equal(source, sanitized.Merged.Text);
        Assert.Empty(sanitized.Suggestions);
        Assert.DoesNotContain(sanitized.Suggestions, s => ProofreadWhitespace.IsWhitespaceOnlyChange(s.OriginalText, s.SuggestedText));
    }

    /// <summary>
    /// The floor that makes the two tests above measure the MERGE: for the separator shapes a naive pipeline gets
    /// wrong, either the sanitizer changes the chunk text (a blank line collapses to one break) or the pre-c17
    /// separator join loses source whitespace (the CR of a CRLF), so "merged == source" is not free. The single-LF
    /// shape is the control: real prose in that shape survives even the naive join, recorded so a change that makes
    /// it lossy is noticed.
    /// </summary>
    [Fact]
    public void RealProse_TheSeparatorShapesReallyChallengeTheMerge()
    {
        var blankLines = Chapter("\n\n");
        var sanitized = MergeThrough(blankLines, UnifiedAnalysisService.SanitizeResponse);
        Assert.True(sanitized.OutputsDiffer, "the sanitizer must change at least one chunk of the blank-line chapter");
        Assert.NotEqual(blankLines, sanitized.OldJoin);

        var crlf = Chapter("\r\n");
        Assert.NotEqual(crlf, MergeThrough(crlf, t => t).OldJoin);

        var lf = Chapter("\n");
        Assert.Equal(lf, MergeThrough(lf, t => t).OldJoin);
    }

    /// <summary>
    /// The same real chapter through the PRODUCTION chunked run (router stand-in returns each chunk unchanged; the
    /// run itself sanitizes, strips the wrapper markers, applies the fallback heuristics, merges and diffs).
    /// </summary>
    [Theory]
    [InlineData("\n\n")]
    [InlineData("\r\n")]
    public async Task RealProse_ProductionChunkedRun_EchoModel_StoresTheSourceAndNoSuggestions(string separator)
    {
        var source = Chapter(separator);
        ChunkAtTheHebrewTarget(source); // floor: the run below really fans out over several chunks

        var result = await ProofreadChunkMergeTests.RunChunkedProofreadAsync(source, t => t, HebrewTarget);

        Assert.Equal("chunked", result.ModelName);
        Assert.False(result.ProofreadResultUnreliable);
        Assert.Equal(source, result.ResultText);
        Assert.Empty(result.Suggestions);
    }
}
