using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
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
    ///
    /// LOCKSTEP (p1-4): this calls the SAME accessors <see cref="UnifiedAnalysisService.RunAsync"/> chunks by
    /// (<see cref="UnifiedAnalysisService.ProofreadChunkTargetWordsFor"/> /
    /// <see cref="UnifiedAnalysisService.LineEditChunkTargetWordsFor"/>) rather than restating their task +
    /// ceiling arguments here, so the two surfaces cannot drift one argument at a time. The sizing also depends
    /// on the num_ctx of the model the task is ROUTED to, so a future model tier that lowers the Proofread /
    /// LineEdit window moves both surfaces together — the client must re-fetch on anything that changes the
    /// route, exactly as it already re-fetches on a language change.
    ///
    /// TIER (p3-2). The route is now a function of (language, TIER), because the tier can change which
    /// provider a task resolves to and therefore which Ai:ProviderSettings window sizes bound (B). The
    /// parameter is OPTIONAL and parsed defensively — absent or unrecognised means the local (fast) tier, so
    /// a client that has not been updated keeps getting exactly the numbers it got before. AT THE SHIPPED
    /// VALUES THE TWO TIERS RETURN THE SAME NUMBERS (OpenRouter_Proofread declares NumCtx 4096, equal to the
    /// local effective 4096, and the crossover below which the window bound starts binding is 3548), which
    /// is pinned rather than assumed by <c>ChunkThresholdBoundDominanceTests</c>; the parameter exists so
    /// that stops being an accident the day a tier entry's window changes.
    ///
    /// Deliberately takes a tier TOKEN and not a bookId: this controller has no database dependency and the
    /// caller already knows its book's tier.
    /// </summary>
    [HttpGet("analysis-chunk-thresholds")]
    public ActionResult<AnalysisChunkThresholdsDto> GetAnalysisChunkThresholds(
        [FromQuery] string? language = null,
        [FromQuery] string? tier = null)
    {
        var opts = _aiOptions.Value;
        var resolvedTier = AiTierPolicy.Parse(tier);
        var proofread = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, language, resolvedTier);
        var lineEdit = UnifiedAnalysisService.LineEditChunkTargetWordsFor(opts, language, resolvedTier);
        return Ok(new AnalysisChunkThresholdsDto(proofread, lineEdit));
    }
}

public record AnalysisChunkThresholdsDto(int ProofreadChunkTargetWords, int LineEditChunkTargetWords);
