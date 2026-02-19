using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine.Rewrite;

/// <summary>DictaLM 2.0 Instruct model rewrite engine (Hebrew-specialized, Apache 2.0).</summary>
public class DictaLmRewriteEngine : IRewriteEngine
{
    private readonly IAiRouter _aiRouter;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<DictaLmRewriteEngine>? _logger;
    private readonly ILanguageEngineMetrics? _metrics;

    public DictaLmRewriteEngine(
        IAiRouter aiRouter,
        IOptions<AiOptions> aiOptions,
        ILogger<DictaLmRewriteEngine>? logger = null,
        ILanguageEngineMetrics? metrics = null)
    {
        _aiRouter = aiRouter;
        _aiOptions = aiOptions;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<string> RewriteAsync(string normalizedText, List<LanguageIssue> issues, string language, CancellationToken cancellationToken = default)
    {
        try
        {
            // DictaLM 2.0 is configured via FeatureModels in appsettings.json
            // Override the model selection to use DictaLM
            var prompt = BuildDictaLmPrompt(normalizedText, issues, language);
            var request = new AiRequest
            {
                InputText = normalizedText,
                Instruction = prompt,
                TaskType = AiTaskType.Proofread,
                Language = language,
                Metadata = new Dictionary<string, string> { { "preferredModel", "dictalm2.0-instruct" } }
            };

            var response = await _aiRouter.CompleteAsync(request, cancellationToken);
            var durationMs = response.Duration?.TotalMilliseconds ?? 0;
            _metrics?.RecordLlmCall("rewrite", response.Model, response.InputTokens, response.OutputTokens, durationMs, response.ApproxCostUsd);
            _logger?.LogInformation("DictaLM rewrite completed using model {Model}. Original: {OriginalLength}, Rewritten: {RewrittenLength}",
                response.Model, normalizedText.Length, response.Content.Length);

            return response.Content;
        }
        catch (Exception ex)
        {
            _metrics?.RecordError("rewrite", ex);
            _logger?.LogError(ex, "Error in DictaLM rewrite");
            throw;
        }
    }

    private static string BuildDictaLmPrompt(string text, List<LanguageIssue> issues, string language)
    {
        // DictaLM 2.0 Instruct is optimized for Hebrew proofreading
        // Provide context about issues but let the model use its Hebrew expertise
        var issuesSummary = issues.Count > 0
            ? $"זוהו {issues.Count} שגיאות בטקסט."
            : "בדוק ותקן שגיאות בטקסט.";

        return $@"{issuesSummary}

טקסט לתיקון:
{text}

החזר את הטקסט המתוקן בלבד:";
    }
}
