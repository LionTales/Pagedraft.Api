using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Controllers;

/// <summary>Exposes client-relevant configuration (e.g. chunk thresholds for analysis) so the UI can match server behavior.</summary>
[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly IOptions<AiOptions> _aiOptions;

    public ConfigController(IOptions<AiOptions> aiOptions)
    {
        _aiOptions = aiOptions;
    }

    /// <summary>
    /// Returns the per-chunk word target Proofread and LineEdit will ACTUALLY use for the given
    /// <paramref name="language"/>. The client uses these to pick analysis-jobs (async) vs sync /analyze, so
    /// they must equal the language-aware sizing <see cref="UnifiedAnalysisService.RunAsync"/> applies — the
    /// raw configured ceiling is a LATIN word count (500), but a dense script (Hebrew/Arabic) chunks at ~250,
    /// so returning the ceiling would make the client pick sync while the server chunks. Language is optional;
    /// when absent (or unknown) the CONSERVATIVE dense sizing is returned, matching
    /// <see cref="UnifiedAnalysisService.EffectiveChunkTargetWords"/>'s unknown-language default — never the
    /// lenient Latin ceiling.
    /// </summary>
    [HttpGet("analysis-chunk-thresholds")]
    public ActionResult<AnalysisChunkThresholdsDto> GetAnalysisChunkThresholds([FromQuery] string? language = null)
    {
        var opts = _aiOptions.Value;
        var proofread = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.Proofread, language, opts.EffectiveProofreadChunkTargetWords);
        var lineEdit = UnifiedAnalysisService.EffectiveChunkTargetWords(
            opts, AiTaskType.LineEdit, language, opts.EffectiveLineEditChunkTargetWords);
        return Ok(new AnalysisChunkThresholdsDto(proofread, lineEdit));
    }
}

public record AnalysisChunkThresholdsDto(int ProofreadChunkTargetWords, int LineEditChunkTargetWords);
