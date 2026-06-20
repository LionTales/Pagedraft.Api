using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// Deterministic tests for <see cref="UnifiedAnalysisService.MergeKtivMaleIntoProofread"/>, the
/// same-word conflict resolver that merges the deterministic ktiv-male (full-spelling) suggestions
/// into the LLM proofread suggestion list. No LLM/Ollama/GPU dependency: the helper is a pure function
/// over hand-built <see cref="AnalysisSuggestion"/> lists, so every case here runs offline and fast.
///
/// The governing rule is that the DETERMINISTIC ktiv normative spelling WINS a same-word conflict, so
/// a (possibly wrong) LLM rewrite touching the same token can never silently suppress the correct
/// spelling - the live regression these tests guard against.
/// </summary>
public class KtivMaleMergeTests
{
    private static AnalysisSuggestion Sug(string original, string suggested, int start, int end) =>
        new AnalysisSuggestion
        {
            OriginalText = original,
            SuggestedText = suggested,
            StartOffset = start,
            EndOffset = end,
            Category = "test",
        };

    private static AnalysisSuggestion Ktiv(string original, string suggested, int start, int end) =>
        new AnalysisSuggestion
        {
            OriginalText = original,
            SuggestedText = suggested,
            StartOffset = start,
            EndOffset = end,
            Category = "ktiv-male",
        };

    // (c) THE LIVE CASE: the proofread LLM rewrote עצמה→עוצמת at the same [78,82] span where the
    // deterministic ktiv check flags עצמה→עוצמה. The ktiv normative spelling must win the word: after
    // the merge there is exactly one suggestion at that span, its SuggestedText is עוצמה, and the
    // wrong LLM rewrite עוצמת is gone.
    [Fact]
    public void Merge_SameSpanCompetingRewrite_KtivWinsTheWord()
    {
        var proofread = new List<AnalysisSuggestion> { Sug("עצמה", "עוצמת", 78, 82) };
        var ktiv = new List<AnalysisSuggestion> { Ktiv("עצמה", "עוצמה", 78, 82) };

        var merged = UnifiedAnalysisService.MergeKtivMaleIntoProofread(proofread, ktiv);

        var atSpan = merged.Where(s => s.StartOffset == 78 && s.EndOffset == 82).ToList();
        var only = Assert.Single(atSpan);
        Assert.Equal("עוצמה", only.SuggestedText);
        Assert.Equal("ktiv-male", only.Category);
        Assert.DoesNotContain(merged, s => s.SuggestedText == "עוצמת");
    }

    // (b) AGREEMENT: the proofread LLM and the deterministic check already agree on the male form
    // (both suggest עוצמה at the same span). Keep exactly one - no duplicate.
    [Fact]
    public void Merge_SameSpanSameSuggestion_KeepsExactlyOne()
    {
        var proofread = new List<AnalysisSuggestion> { Sug("עצמה", "עוצמה", 78, 82) };
        var ktiv = new List<AnalysisSuggestion> { Ktiv("עצמה", "עוצמה", 78, 82) };

        var merged = UnifiedAnalysisService.MergeKtivMaleIntoProofread(proofread, ktiv);

        var atSpan = merged.Where(s => s.StartOffset == 78 && s.EndOffset == 82).ToList();
        var only = Assert.Single(atSpan);
        Assert.Equal("עוצמה", only.SuggestedText);
    }

    // (a) NON-OVERLAPPING: a proofread suggestion at [10,14] and a ktiv suggestion at [78,82] touch
    // different words. Both survive.
    [Fact]
    public void Merge_NonOverlappingSpans_KeepsBoth()
    {
        var proofread = new List<AnalysisSuggestion> { Sug("מילה", "תיקון", 10, 14) };
        var ktiv = new List<AnalysisSuggestion> { Ktiv("עצמה", "עוצמה", 78, 82) };

        var merged = UnifiedAnalysisService.MergeKtivMaleIntoProofread(proofread, ktiv);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, s => s.StartOffset == 10 && s.EndOffset == 14 && s.SuggestedText == "תיקון");
        Assert.Contains(merged, s => s.StartOffset == 78 && s.EndOffset == 82 && s.SuggestedText == "עוצמה");
    }

    // (d) MULTI-WORD REWRITE: a broader LLM rewrite spans [78,90] (wider than the ktiv token [78,82]),
    // i.e. it extends beyond the word. Keep the broader proofread fix and DROP the ktiv suggestion so a
    // wider fix is never fragmented.
    [Fact]
    public void Merge_BroaderMultiWordRewrite_KeepsProofreadDropsKtiv()
    {
        var proofread = new List<AnalysisSuggestion> { Sug("עצמה רבה", "בעוצמה גדולה", 78, 90) };
        var ktiv = new List<AnalysisSuggestion> { Ktiv("עצמה", "עוצמה", 78, 82) };

        var merged = UnifiedAnalysisService.MergeKtivMaleIntoProofread(proofread, ktiv);

        var only = Assert.Single(merged);
        Assert.Equal(78, only.StartOffset);
        Assert.Equal(90, only.EndOffset);
        Assert.Equal("בעוצמה גדולה", only.SuggestedText);
        Assert.DoesNotContain(merged, s => s.SuggestedText == "עוצמה" && s.EndOffset == 82);
    }
}
