using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Services.Ai;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for <see cref="ConfigController.GetAnalysisChunkThresholds"/> — the endpoint the client uses to pick
/// analysis-jobs (async) vs sync /analyze. It MUST return the language-aware per-chunk word target the server
/// actually chunks at (via <see cref="UnifiedAnalysisService.EffectiveChunkTargetWords"/>), not the raw Latin
/// ceiling: a dense script (Hebrew/Arabic ~250) chunks at half the Latin ceiling (500), so returning the
/// ceiling would make the client pick sync while the server chunks. Pure unit tests: no DB, no LLM.
/// </summary>
public class ConfigControllerTests
{
    // Production-shaped options mirroring ProofreadChunkSizingTests so the derived targets match production
    // (Latin ceiling 500; dense-script halves to 250 at the production window).
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

    private static AnalysisChunkThresholdsDto Thresholds(string? language)
    {
        var controller = new ConfigController(Options.Create(ProdShapedOptions()));
        var action = controller.GetAnalysisChunkThresholds(language);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<AnalysisChunkThresholdsDto>(ok.Value);
    }

    [Fact]
    public void English_ReturnsLatinCeiling()
    {
        var dto = Thresholds("en");
        Assert.Equal(500, dto.ProofreadChunkTargetWords);
        Assert.Equal(500, dto.LineEditChunkTargetWords);
    }

    [Fact]
    public void Hebrew_ReturnsHalvedDenseTarget_MatchingWhatRunAsyncChunksAt()
    {
        var dto = Thresholds("he");
        // Dense-script density ratio (2.0 / 4.0) halves the Latin ceiling; this is the SAME value RunAsync
        // sizes Hebrew chunks at, so the client's async-vs-sync decision now matches the server.
        Assert.Equal(250, dto.ProofreadChunkTargetWords);
        Assert.Equal(250, dto.LineEditChunkTargetWords);
    }

    [Fact]
    public void Arabic_AlsoReturnsHalvedDenseTarget()
    {
        var dto = Thresholds("ar");
        Assert.Equal(250, dto.ProofreadChunkTargetWords);
        Assert.Equal(250, dto.LineEditChunkTargetWords);
    }

    [Fact]
    public void MissingLanguage_ReturnsConservativeDenseTarget_NotLenientLatinCeiling()
    {
        // No language → the conservative dense sizing (matches EffectiveChunkTargetWords' unknown-language
        // default), never the lenient Latin ceiling that would under-trigger async.
        var dto = Thresholds(null);
        Assert.Equal(250, dto.ProofreadChunkTargetWords);
        Assert.Equal(250, dto.LineEditChunkTargetWords);
    }
}
