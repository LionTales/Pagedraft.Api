using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine.Detect;

/// <summary>LLM-based issue detection using AI router.</summary>
public class LlmDetectEngine : IDetectEngine
{
    private readonly IAiRouter _aiRouter;
    private readonly ILogger<LlmDetectEngine>? _logger;

    public LlmDetectEngine(IAiRouter aiRouter, ILogger<LlmDetectEngine>? logger = null)
    {
        _aiRouter = aiRouter;
        _logger = logger;
    }

    public async Task<DetectResult> DetectAsync(string normalizedText, string language, CancellationToken cancellationToken = default)
    {
        try
        {
            var prompt = BuildDetectionPrompt(normalizedText, language);
            var request = new AiRequest
            {
                InputText = normalizedText,
                Instruction = prompt,
                TaskType = AiTaskType.Proofread,
                Language = language
            };

            var response = await _aiRouter.CompleteAsync(request, cancellationToken);
            var issues = ParseIssuesFromResponse(response.Content, normalizedText);

            _logger?.LogInformation("LLM detection found {Count} issues", issues.Count);
            return new DetectResult { Issues = issues };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in LLM-based detection");
            throw;
        }
    }

    private static string BuildDetectionPrompt(string text, string language)
    {
        return language.StartsWith("he", StringComparison.OrdinalIgnoreCase)
            ? @"אתה מומחה לזיהוי שגיאות בעברית. זהה את כל השגיאות בטקסט הבא וחזור בתשובה בפורמט JSON.

פורמט התשובה:
[
  {
    ""startOffset"": 0,
    ""endOffset"": 5,
    ""message"": ""תיאור השגיאה"",
    ""category"": ""spelling|grammar|punctuation|style"",
    ""severity"": ""error|warning|info"",
    ""confidence"": 0.9,
    ""suggestions"": [""הצעה 1"", ""הצעה 2""]
  }
]

טקסט לבדיקה:
" + text
            : @"You are an expert at detecting language issues. Identify all issues in the following text and return them in JSON format.

Response format:
[
  {
    ""startOffset"": 0,
    ""endOffset"": 5,
    ""message"": ""Description of the issue"",
    ""category"": ""spelling|grammar|punctuation|style"",
    ""severity"": ""error|warning|info"",
    ""confidence"": 0.9,
    ""suggestions"": [""suggestion 1"", ""suggestion 2""]
  }
]

Text to check:
" + text;
    }

    private static List<LanguageIssue> ParseIssuesFromResponse(string responseContent, string originalText)
    {
        var issues = new List<LanguageIssue>();
        
        try
        {
            // Try to extract JSON array from response
            var jsonStart = responseContent.IndexOf('[');
            var jsonEnd = responseContent.LastIndexOf(']');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = responseContent.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var parsed = JsonSerializer.Deserialize<List<LanguageIssue>>(json);
                if (parsed != null)
                {
                    issues = parsed;
                }
            }
        }
        catch (JsonException)
        {
            // If JSON parsing fails, return empty list
            // In production, might want to log this or try alternative parsing
        }

        // Validate offsets are within text bounds
        return issues.Where(issue => 
            issue.StartOffset >= 0 && 
            issue.EndOffset <= originalText.Length && 
            issue.StartOffset < issue.EndOffset).ToList();
    }
}
