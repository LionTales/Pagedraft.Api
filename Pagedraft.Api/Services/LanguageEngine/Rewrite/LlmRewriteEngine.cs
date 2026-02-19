using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine.Rewrite;

/// <summary>Generic LLM-based rewrite engine using AI router.</summary>
public class LlmRewriteEngine : IRewriteEngine
{
    private readonly IAiRouter _aiRouter;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<LlmRewriteEngine>? _logger;
    private readonly ILanguageEngineMetrics? _metrics;

    public LlmRewriteEngine(
        IAiRouter aiRouter,
        IOptions<AiOptions> aiOptions,
        ILogger<LlmRewriteEngine>? logger = null,
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
            var prompt = BuildRewritePrompt(normalizedText, issues, language);
            var request = new AiRequest
            {
                InputText = normalizedText,
                Instruction = prompt,
                TaskType = AiTaskType.Proofread,
                Language = language
            };

            var response = await _aiRouter.CompleteAsync(request, cancellationToken);
            var durationMs = response.Duration?.TotalMilliseconds ?? 0;
            _metrics?.RecordLlmCall("rewrite", response.Model, response.InputTokens, response.OutputTokens, durationMs, response.ApproxCostUsd);
            _logger?.LogInformation("LLM rewrite completed. Original length: {OriginalLength}, Rewritten length: {RewrittenLength}",
                normalizedText.Length, response.Content.Length);

            return response.Content;
        }
        catch (Exception ex)
        {
            _metrics?.RecordError("rewrite", ex);
            _logger?.LogError(ex, "Error in LLM rewrite");
            throw;
        }
    }

    private static string BuildRewritePrompt(string text, List<LanguageIssue> issues, string language)
    {
        var issuesDescription = issues.Count > 0
            ? string.Join("\n", issues.Select((issue, idx) => 
                $"{idx + 1}. [{issue.StartOffset}-{issue.EndOffset}] {issue.Message}"))
            : "No specific issues identified";

        return language.StartsWith("he", StringComparison.OrdinalIgnoreCase)
            ? $@"אתה מומחה לעריכה לשונית בעברית. תקן את הטקסט הבא בהתאם לשגיאות שזוהו.

שגיאות שזוהו:
{issuesDescription}

טקסט לתיקון:
{text}

החזר רק את הטקסט המתוקן, ללא הסברים נוספים."
            : $@"You are an expert language editor. Correct the following text according to the identified issues.

Identified issues:
{issuesDescription}

Text to correct:
{text}

Return only the corrected text, without any additional explanations.";
    }
}
