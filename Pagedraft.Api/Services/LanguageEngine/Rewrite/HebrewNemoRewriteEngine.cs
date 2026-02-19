using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine.Rewrite;

/// <summary>Hebrew🔥Nemo rewrite engine (12B, 128K context, Apache 2.0) - optimized for long documents.</summary>
public class HebrewNemoRewriteEngine : IRewriteEngine
{
    private readonly IAiRouter _aiRouter;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<HebrewNemoRewriteEngine>? _logger;
    private readonly ILanguageEngineMetrics? _metrics;

    public HebrewNemoRewriteEngine(
        IAiRouter aiRouter,
        IOptions<AiOptions> aiOptions,
        ILogger<HebrewNemoRewriteEngine>? logger = null,
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
            // Hebrew🔥Nemo is particularly good for long documents due to 128K context
            var prompt = BuildHebrewNemoPrompt(normalizedText, issues, language);
            var request = new AiRequest
            {
                InputText = normalizedText,
                Instruction = prompt,
                TaskType = AiTaskType.Proofread,
                Language = language,
                Metadata = new Dictionary<string, string> { { "preferredModel", "hebrew-nemo-12b" } }
            };

            var response = await _aiRouter.CompleteAsync(request, cancellationToken);
            var durationMs = response.Duration?.TotalMilliseconds ?? 0;
            _metrics?.RecordLlmCall("rewrite", response.Model, response.InputTokens, response.OutputTokens, durationMs, response.ApproxCostUsd);
            _logger?.LogInformation("Hebrew🔥Nemo rewrite completed using model {Model}. Original: {OriginalLength}, Rewritten: {RewrittenLength}",
                response.Model, normalizedText.Length, response.Content.Length);

            return response.Content;
        }
        catch (Exception ex)
        {
            _metrics?.RecordError("rewrite", ex);
            _logger?.LogError(ex, "Error in Hebrew🔥Nemo rewrite");
            throw;
        }
    }

    private static string BuildHebrewNemoPrompt(string text, List<LanguageIssue> issues, string language)
    {
        // Hebrew🔥Nemo excels at long-form Hebrew text with context understanding
        // Provide comprehensive context for better rewriting
        var issuesContext = issues.Count > 0
            ? $"זוהו {issues.Count} בעיות לשוניות. תקן אותן תוך שמירה על הסגנון והקונטקסט של הטקסט."
            : "בדוק ותקן שגיאות לשוניות תוך שמירה על הסגנון המקורי.";

        return $@"{issuesContext}

טקסט לעריכה:
{text}

החזר את הטקסט המתוקן והמשופר, תוך שמירה על המשמעות והסגנון המקוריים:";
    }
}
