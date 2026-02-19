using Microsoft.Extensions.Logging;

namespace Pagedraft.Api.Services.LanguageEngine.Metrics;

/// <summary>Logging-only implementation of language engine metrics. Writes structured properties for Stage, DurationMs, Tokens, Cost; can later add App Insights or Prometheus.</summary>
public class LoggingLanguageEngineMetrics : Contracts.ILanguageEngineMetrics
{
    private readonly ILogger<LoggingLanguageEngineMetrics> _logger;

    public LoggingLanguageEngineMetrics(ILogger<LoggingLanguageEngineMetrics> logger)
    {
        _logger = logger;
    }

    public void RecordStageDuration(string stageName, double durationMs, string? model = null)
    {
        _logger.LogInformation(
            "LanguageEngine.Stage.Duration Stage={Stage} DurationMs={DurationMs} Model={Model}",
            stageName, durationMs, model ?? "");
    }

    public void RecordLlmCall(string stageName, string model, int? inputTokens, int? outputTokens, double durationMs, decimal? costUsd)
    {
        _logger.LogInformation(
            "LanguageEngine.LlmCall Stage={Stage} Model={Model} InputTokens={InputTokens} OutputTokens={OutputTokens} DurationMs={DurationMs} CostUsd={CostUsd}",
            stageName, model, inputTokens ?? 0, outputTokens ?? 0, durationMs, costUsd);
    }

    public void RecordError(string stageName, Exception exception)
    {
        _logger.LogError(exception,
            "LanguageEngine.Error Stage={Stage} Message={Message}",
            stageName, exception.Message);
    }
}
