using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for the LANGUAGE-AWARE proofread / LineEdit chunk sizing (pf-b01). The chunker still splits by
/// words, but the per-chunk WORD target is now DERIVED from an estimated-TOKEN budget via
/// <see cref="UnifiedAnalysisService.EffectiveChunkTargetWords"/>, reusing
/// <see cref="BookContextAssembler.CharsPerTokenForLanguage"/> +
/// <see cref="AiOptions.EffectiveBookContextTokenBudget"/>. A dense-script (Hebrew/Arabic) chunk therefore
/// gets FEWER words than an English chunk with the same token footprint, so a long Hebrew chapter produces
/// MORE, smaller chunks whose estimated tokens stay inside the model window.
///
/// All pure unit tests: they call the internal sizing helper and the internal chunker test seams directly —
/// NO database, NO LLM, NO live GPU.
/// </summary>
public class ProofreadChunkSizingTests
{
    // Production-shaped AiOptions mirroring appsettings.json ProviderSettings so derived targets match
    // production behaviour.
    //
    // Effective Proofread NumCtx is 4096, NOT 8192 — confirmed by tracing ResolveNumCtxForTask:
    //   "Ollama_Proofread" has no NumCtx in config, so the deserialized entry gets the C# property default
    //   (ProviderTuningOptions.NumCtx = 4096). ResolveNumCtxForTask checks taskTuning.NumCtx > 0 first;
    //   4096 > 0 is true, so it returns 4096 and never falls through to the base "Ollama" entry (8192).
    //   This matches the live pf-b02 observation of context_length 4096.
    //
    // Hebrew sizing is unchanged by this: at NumCtx=4096 the window-fit (bound B) is 341 words, which is
    // still above the language ceiling (bound A) of 250, so the tighter bound A wins and all assertions
    // remain correct with either value (250 either way).
    //
    // ProdShapedOptions omits NumCtx on Ollama_Proofread (matching prod), so the test already exercises the
    // real 4096 path via the C# default — no functional change needed, only this comment corrected.
    private static AiOptions ProdShapedOptions() => new()
    {
        DefaultProvider = "Ollama",
        DefaultModel = "test-model",
        ProofreadChunkTargetWords = 500,
        LineEditChunkTargetWords = 500,
        ProviderSettings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama"] = new ProviderTuningOptions { NumCtx = 8192, NumPredict = 2048 },
            ["Ollama_Proofread"] = new ProviderTuningOptions { NumPredict = 4096 },
            ["Ollama_LineEdit"] = new ProviderTuningOptions { NumPredict = 5120 }
        }
    };

    // ─── (1) Sizing math: Latin keeps the ceiling; Hebrew shrinks (derived ~half) ─────────────────────────

    [Fact]
    public void EffectiveChunkTargetWords_Latin_KeepsConfiguredCeiling()
    {
        var opts = ProdShapedOptions();

        var latin = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "en", opts.EffectiveProofreadChunkTargetWords);

        // English must NOT exceed today's behaviour (500). At the production window it lands exactly at 500.
        Assert.Equal(500, latin);
    }

    [Fact]
    public void EffectiveChunkTargetWords_Hebrew_ShrinksBelowLatin_DerivedFromDensityRatio()
    {
        var opts = ProdShapedOptions();

        var latin = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "en", opts.EffectiveProofreadChunkTargetWords);
        var hebrew = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "he", opts.EffectiveProofreadChunkTargetWords);

        Assert.True(hebrew < latin, $"Hebrew target ({hebrew}) must be smaller than Latin ({latin}).");

        // The shrink is the char/token density ratio (Hebrew 2.0 / Latin 4.0 = 1/2), NOT a hardcoded half:
        // languageCeiling = floor(ceiling * charsPerToken(he) / charsPerToken(en)).
        var expected = (int)Math.Floor(
            500 * BookContextAssembler.CharsPerTokenForLanguage("he")
                / BookContextAssembler.CharsPerTokenForLanguage("en"));
        Assert.Equal(expected, hebrew);
        Assert.Equal(250, hebrew); // concrete: half of 500 at these densities
    }

    [Fact]
    public void EffectiveChunkTargetWords_Arabic_AlsoShrinks_LikeHebrew()
    {
        var opts = ProdShapedOptions();
        var arabic = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "ar", opts.EffectiveProofreadChunkTargetWords);
        Assert.Equal(250, arabic); // Arabic shares the dense (2.0) density
    }

    [Fact]
    public void EffectiveChunkTargetWords_UnknownLanguage_TreatedAsDense_Conservative()
    {
        var opts = ProdShapedOptions();
        // Unknown/blank language → CharsPerTokenForLanguage returns the DENSE (conservative) 2.0, so it sizes
        // like Hebrew rather than silently reverting to the lenient Latin ceiling.
        var unknown = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "zz-unknown", opts.EffectiveProofreadChunkTargetWords);
        Assert.Equal(250, unknown);
    }

    [Fact]
    public void EffectiveChunkTargetWords_TightWindow_ShrinksBelowLanguageCeiling()
    {
        // A tight window: numCtx 3072. After reserving prompt (1536) + safety (512) the generation window is
        // 1024 tokens, split in half → 512 input tokens, so window-fit (bound B) bites and drops the Hebrew
        // target BELOW the 250 language ceiling.
        var opts = new AiOptions
        {
            DefaultProvider = "Ollama",
            DefaultModel = "test-model",
            ProofreadChunkTargetWords = 500,
            ProviderSettings = new Dictionary<string, ProviderTuningOptions>
            {
                ["Ollama"] = new ProviderTuningOptions { NumCtx = 3072, NumPredict = 1024 }
            }
        };

        var numCtx = BookContextAssembler.ResolveNumCtxForTask(opts, AiTaskType.Proofread); // 3072
        // Bound (B): input = (numCtx - promptReserve - safetyMargin) / 2, then words = input * cpt / 6.0.
        var generationWindow = numCtx - opts.BookContextPromptReserveTokens - opts.BookContextSafetyMarginTokens;
        var availableInputTokens = Math.Max(64, generationWindow / 2);
        var expectedWindowWords = (int)Math.Floor(
            availableInputTokens * BookContextAssembler.CharsPerTokenForLanguage("he") / 6.0);

        var hebrew = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "he", opts.EffectiveProofreadChunkTargetWords);

        Assert.Equal(expectedWindowWords, hebrew);
        Assert.True(hebrew < 250, $"tight window must shrink below the language ceiling 250 (got {hebrew}).");
        Assert.True(hebrew >= 1, "always at least one word per chunk.");
    }

    // ─── (2) Chunker: Hebrew yields MORE, smaller chunks than the same-word-count English text ────────────

    [Fact]
    public void ChunkForProofread_Hebrew_ProducesMoreChunksThanEnglish_ForSameWordCount()
    {
        var opts = ProdShapedOptions();
        const int wordCount = 1200;

        var hebrewTarget = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "he", opts.EffectiveProofreadChunkTargetWords);
        var englishTarget = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "en", opts.EffectiveProofreadChunkTargetWords);

        var hebrewChunks = UnifiedAnalysisService.ChunkForProofreadForTest(
            BuildText(wordCount, hebrew: true), hebrewTarget);
        var englishChunks = UnifiedAnalysisService.ChunkForProofreadForTest(
            BuildText(wordCount, hebrew: false), englishTarget);

        Assert.True(hebrewChunks.Count > englishChunks.Count,
            $"Hebrew ({hebrewChunks.Count} chunks @ {hebrewTarget} words) must split into MORE chunks than " +
            $"English ({englishChunks.Count} chunks @ {englishTarget} words) for the same {wordCount}-word input.");
    }

    // ─── (3) A long Hebrew chapter's chunks fit the configured window (estimated tokens) ──────────────────

    [Fact]
    public void ChunkForProofread_Hebrew_EveryChunkFitsWindow_ByEstimatedTokens()
    {
        var opts = ProdShapedOptions();

        var numCtx = BookContextAssembler.ResolveNumCtxForTask(opts, AiTaskType.Proofread);
        // The per-chunk INPUT budget: half the generation window (output ≈ input for a proofread rewrite),
        // after reserving prompt overhead + safety margin — the same accounting the sizer uses.
        var generationWindow = numCtx - opts.BookContextPromptReserveTokens - opts.BookContextSafetyMarginTokens;
        var inputBudget = Math.Max(64, generationWindow / 2);

        var target = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, "he", opts.EffectiveProofreadChunkTargetWords);

        // A long Hebrew chapter (well beyond a single chunk).
        var chunks = UnifiedAnalysisService.ChunkForProofreadForTest(
            BuildText(3000, hebrew: true), target);

        Assert.True(chunks.Count > 1, "long Hebrew chapter must produce multiple chunks.");
        var charsPerToken = BookContextAssembler.CharsPerTokenForLanguage("he");
        foreach (var chunk in chunks)
        {
            var estTokens = BookContextAssembler.EstimateTokens(chunk.Text, charsPerToken);
            // The estimated input tokens (chunk text) fit the input half of the window, so input + its
            // ~equal-sized output + prompt + margin all fit num_ctx (Ollama never truncates).
            Assert.True(estTokens <= inputBudget,
                $"Hebrew chunk estimated {estTokens} tokens must fit the per-chunk input budget {inputBudget} " +
                $"(half of numCtx {numCtx} minus reserved prompt/margin).");
            // And input + output (≈input) + reserves fit the full window.
            Assert.True(2 * estTokens + opts.BookContextPromptReserveTokens + opts.BookContextSafetyMarginTokens <= numCtx,
                "input + equal-sized output + reserves must fit num_ctx.");
        }
    }

    // ─── (4) Boundaries + overlap preserved (ProofreadChunk.OverlapPrefix) ────────────────────────────────

    [Fact]
    public void ChunkForProofread_PreservesOverlapPrefix_OnAllButFirstChunk()
    {
        // Sentence-delimited English so the trailing-sentence overlap extractor has real boundaries to carry.
        var text = BuildSentences(60, hebrew: false);
        var chunks = UnifiedAnalysisService.ChunkForProofreadForTest(text, targetWordsPerChunk: 40);

        Assert.True(chunks.Count > 1, "input must split into multiple chunks for the overlap assertion.");
        Assert.Null(chunks[0].OverlapPrefix); // first chunk has no preceding context
        for (var i = 1; i < chunks.Count; i++)
            Assert.False(string.IsNullOrWhiteSpace(chunks[i].OverlapPrefix),
                $"chunk {i} must carry an OverlapPrefix (trailing sentences of chunk {i - 1}).");

        // Boundary integrity: the concatenated chunk texts contain every word of the input in order (no word
        // dropped or duplicated at a boundary — overlap is a SEPARATE read-only field, not merged into Text).
        var joinedWords = string.Join(" ", chunks.Select(c => c.Text)).Split(
            new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var originalWords = text.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(originalWords.Length, joinedWords.Length);
    }

    // ─── (5) LineEdit chunker got the SAME language-aware fix ─────────────────────────────────────────────

    [Fact]
    public void EffectiveChunkTargetWords_LineEdit_Hebrew_ShrinksLikeProofread()
    {
        var opts = ProdShapedOptions();

        var latin = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.LineEdit, "en", opts.EffectiveLineEditChunkTargetWords);
        var hebrew = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.LineEdit, "he", opts.EffectiveLineEditChunkTargetWords);

        Assert.Equal(500, latin);   // English keeps the LineEdit ceiling
        Assert.Equal(250, hebrew);  // Hebrew halves from the density ratio
    }

    [Fact]
    public void ChunkForLineEdit_Hebrew_ProducesMoreChunksThanEnglish_AndKeepsOverlaps()
    {
        var opts = ProdShapedOptions();

        var hebrewTarget = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.LineEdit, "he", opts.EffectiveLineEditChunkTargetWords);
        var englishTarget = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.LineEdit, "en", opts.EffectiveLineEditChunkTargetWords);

        var hebrewChunks = UnifiedAnalysisService.ChunkForLineEditForTest(
            BuildSentences(120, hebrew: true), hebrewTarget);
        var englishChunks = UnifiedAnalysisService.ChunkForLineEditForTest(
            BuildSentences(120, hebrew: false), englishTarget);

        Assert.True(hebrewChunks.Count > englishChunks.Count,
            $"Hebrew LineEdit ({hebrewChunks.Count}) must split into more chunks than English ({englishChunks.Count}).");

        // LineEdit carries BOTH a preceding-context prefix and a following-context suffix; interior chunks must
        // have both, so the overlap seam survives the smaller Hebrew target.
        if (hebrewChunks.Count >= 3)
        {
            Assert.False(string.IsNullOrWhiteSpace(hebrewChunks[1].OverlapPrefix),
                "interior LineEdit chunk must carry a preceding-context prefix.");
            Assert.False(string.IsNullOrWhiteSpace(hebrewChunks[1].OverlapSuffix),
                "interior LineEdit chunk must carry a following-context suffix.");
        }
    }

    // ─── (6) Fallback default is TASK-APPROPRIATE when configuredCeiling <= 0 ────────────────────────────

    [Fact]
    public void EffectiveChunkTargetWords_LineEdit_ZeroCeiling_FallsBackToLineEditDefault()
    {
        // This branch is unreachable from production (callers always pass EffectiveLineEditChunkTargetWords
        // which is already > 0), but the fallback should reference the task-appropriate constant so the
        // intent stays clear. Assert the result equals what the function would compute with the real
        // DefaultLineEditChunkTargetWords as ceiling — for "en" at the production window that is exactly
        // AiOptions.DefaultLineEditChunkTargetWords (the Latin path is the identity case).
        var opts = ProdShapedOptions();

        var result = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.LineEdit, "en", 0 /* force the fallback branch */);

        // Compute the expected value the same way the helper does, using the LineEdit default as ceiling.
        var expectedCeiling = AiOptions.DefaultLineEditChunkTargetWords;
        var charsPerTokenLang = BookContextAssembler.CharsPerTokenForLanguage("en");
        var charsPerTokenLatin = BookContextAssembler.CharsPerTokenForLanguage("en");
        var languageCeiling = (int)Math.Floor(expectedCeiling * charsPerTokenLang / charsPerTokenLatin);

        var numCtx = BookContextAssembler.ResolveNumCtxForTask(opts, AiTaskType.LineEdit);
        var generationWindow = numCtx
            - Math.Max(0, opts.BookContextPromptReserveTokens)
            - Math.Max(0, opts.BookContextSafetyMarginTokens);
        var availableInputTokens = Math.Max(64, generationWindow / 2);
        const double avgCharsPerWord = 6.0; // mirrors the private constant in UnifiedAnalysisService
        var wordsThatFitWindow = (int)Math.Floor(availableInputTokens * charsPerTokenLang / avgCharsPerWord);

        var expected = Math.Max(1, Math.Min(languageCeiling, wordsThatFitWindow));
        Assert.Equal(expected, result);

        // Concrete sanity: at production opts for "en" the result must equal the Latin ceiling (500).
        Assert.Equal(AiOptions.DefaultLineEditChunkTargetWords, result);
    }

    // ─── Text builders ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>N space-separated ~5-char words (Latin) or ~5-char Hebrew words, no sentence boundaries.</summary>
    private static string BuildText(int words, bool hebrew)
    {
        var word = hebrew ? "מִלָּה" : "wordy"; // both ~5 visible chars
        var sb = new StringBuilder(words * 6);
        for (var i = 0; i < words; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(word);
        }
        return sb.ToString();
    }

    /// <summary>N sentences of 10 words each, period-terminated, so the sentence/overlap logic has boundaries.</summary>
    private static string BuildSentences(int sentences, bool hebrew)
    {
        var word = hebrew ? "מִלָּה" : "wordy";
        var sb = new StringBuilder();
        for (var s = 0; s < sentences; s++)
        {
            for (var w = 0; w < 10; w++)
            {
                if (w > 0) sb.Append(' ');
                sb.Append(word);
            }
            sb.Append(". ");
        }
        return sb.ToString().TrimEnd();
    }
}
