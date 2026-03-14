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
        var proofread = opts.ProofreadChunkTargetWords > 0 ? opts.ProofreadChunkTargetWords : 500;
        var lineEdit = opts.LineEditChunkTargetWords > 0 ? opts.LineEditChunkTargetWords : 1500;
        return Ok(new AnalysisChunkThresholdsDto(proofread, lineEdit));
    }
}

public record AnalysisChunkThresholdsDto(int ProofreadChunkTargetWords, int LineEditChunkTargetWords);
