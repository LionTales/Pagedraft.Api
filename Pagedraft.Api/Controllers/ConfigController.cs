using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;

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

    /// <summary>Returns chunk target words for Proofread and LineEdit. Client uses these to decide when to use analysis-jobs (async) vs sync /analyze.</summary>
    [HttpGet("analysis-chunk-thresholds")]
    public ActionResult<AnalysisChunkThresholdsDto> GetAnalysisChunkThresholds()
    {
        var opts = _aiOptions.Value;
        return Ok(new AnalysisChunkThresholdsDto(opts.EffectiveProofreadChunkTargetWords, opts.EffectiveLineEditChunkTargetWords));
    }
}

public record AnalysisChunkThresholdsDto(int ProofreadChunkTargetWords, int LineEditChunkTargetWords);
