namespace Pagedraft.Api.Services.LanguageEngine.Contracts;

/// <summary>Abstraction for language engine observability: latency, tokens, costs, errors. Can log only or push to App Insights / Prometheus.</summary>
public interface ILanguageEngineMetrics
{
    /// <summary>Record duration of a pipeline stage (normalize, detect, rewrite, analyze).</summary>
    void RecordStageDuration(string stageName, double durationMs, string? model = null);

    /// <summary>Record an LLM call with token counts and optional cost. Call after IAiRouter.CompleteAsync when provider returns tokens/cost.</summary>
    void RecordLlmCall(string stageName, string model, int? inputTokens, int? outputTokens, double durationMs, decimal? costUsd);

    /// <summary>Record an error in a stage for alerting and diagnostics.</summary>
    void RecordError(string stageName, Exception exception);
}
