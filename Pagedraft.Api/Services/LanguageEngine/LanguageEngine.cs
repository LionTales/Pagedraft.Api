using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine;

/// <summary>Orchestrates the language engine pipeline: Normalize → Detect → Rewrite. Analysis is handled by UnifiedAnalysisService.</summary>
public class LanguageEngine : Contracts.ILanguageEngine
{
    private readonly INormalizeEngine _normalizeEngine;
    private readonly IDetectEngine _detectEngine;
    private readonly IRewriteEngine? _rewriteEngine;
    private readonly ILogger<LanguageEngine> _logger;
    private readonly ILanguageEngineMetrics? _metrics;

    public LanguageEngine(
        INormalizeEngine normalizeEngine,
        IDetectEngine detectEngine,
        IRewriteEngine? rewriteEngine = null,
        ILogger<LanguageEngine> logger = null!,
        ILanguageEngineMetrics? metrics = null)
    {
        _normalizeEngine = normalizeEngine;
        _detectEngine = detectEngine;
        _rewriteEngine = rewriteEngine;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<LanguageEngineResult> ProcessAsync(LanguageEngineRequest request, CancellationToken cancellationToken = default)
    {
        var result = new LanguageEngineResult();
        var options = request.Options;

        try
        {
            // Stage 1: Normalize
            string normalizedText = request.InputText;
            if (options.EnableNormalize)
            {
                _logger?.LogInformation("Starting normalization stage for language {Language}", request.Language);
                var sw = Stopwatch.StartNew();
                normalizedText = await _normalizeEngine.NormalizeAsync(request.InputText, request.Language, cancellationToken);
                sw.Stop();
                _metrics?.RecordStageDuration("normalize", sw.Elapsed.TotalMilliseconds);
                result.NormalizedText = normalizedText;
                _logger?.LogInformation("Normalization completed. Length: {Length}", normalizedText.Length);
            }
            else
            {
                result.NormalizedText = request.InputText;
            }

            // Stage 2: Detect
            if (options.EnableDetect)
            {
                _logger?.LogInformation("Starting detection stage");
                var sw = Stopwatch.StartNew();
                var detectResult = await _detectEngine.DetectAsync(normalizedText, request.Language, cancellationToken);
                sw.Stop();
                _metrics?.RecordStageDuration("detect", sw.Elapsed.TotalMilliseconds);
                result.Issues = detectResult.Issues;
                if (detectResult.ServiceUnavailable)
                {
                    result.Metadata["languageToolUnavailable"] = true;
                    result.Metadata["languageToolMessage"] = detectResult.ServiceUnavailableMessage ?? "The language checker is not available.";
                }
                _logger?.LogInformation("Detection completed. Found {Count} issues", detectResult.Issues.Count);
            }

            // Stage 3: Rewrite (optional)
            if (options.EnableRewrite && _rewriteEngine != null)
            {
                _logger?.LogInformation("Starting rewrite stage");
                var sw = Stopwatch.StartNew();
                var rewritten = await _rewriteEngine.RewriteAsync(normalizedText, result.Issues, request.Language, cancellationToken);
                sw.Stop();
                _metrics?.RecordStageDuration("rewrite", sw.Elapsed.TotalMilliseconds);
                result.RewrittenText = rewritten;
                _logger?.LogInformation("Rewrite completed. Length: {Length}", rewritten.Length);
            }

            // Analysis is handled by UnifiedAnalysisService (chapter/scene-level API), not in this pipeline.
            result.Metadata["stagesExecuted"] = GetExecutedStages(options);
            return result;
        }
        catch (Exception ex)
        {
            _metrics?.RecordError("pipeline", ex);
            _logger?.LogError(ex, "Error in language engine pipeline");
            result.Metadata["error"] = ex.Message;
            throw;
        }
    }

    private static string GetExecutedStages(LanguageEngineOptions options)
    {
        var stages = new List<string>();
        if (options.EnableNormalize) stages.Add("normalize");
        if (options.EnableDetect) stages.Add("detect");
        if (options.EnableRewrite) stages.Add("rewrite");
        return string.Join(",", stages);
    }
}
