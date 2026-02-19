using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine.Analyze;

/// <summary>Linguistic analysis engine using AI router.</summary>
public class LinguisticAnalysisEngine : IAnalyzeEngine
{
    private readonly IAiRouter _aiRouter;
    private readonly ILogger<LinguisticAnalysisEngine>? _logger;
    private readonly ILanguageEngineMetrics? _metrics;

    public LinguisticAnalysisEngine(IAiRouter aiRouter, ILogger<LinguisticAnalysisEngine>? logger = null, ILanguageEngineMetrics? metrics = null)
    {
        _aiRouter = aiRouter;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<LanguageAnalysis> AnalyzeAsync(string text, string language, CancellationToken cancellationToken = default)
    {
        try
        {
            var prompt = BuildLinguisticPrompt(text, language);
            var request = new AiRequest
            {
                InputText = text,
                Instruction = prompt,
                TaskType = AiTaskType.LinguisticAnalysis,
                Language = language
            };

            var response = await _aiRouter.CompleteAsync(request, cancellationToken);
            var durationMs = response.Duration?.TotalMilliseconds ?? 0;
            _metrics?.RecordLlmCall("analyze", response.Model, response.InputTokens, response.OutputTokens, durationMs, response.ApproxCostUsd);
            var analysis = ParseLinguisticAnalysis(response.Content, language);

            _logger?.LogInformation("Linguistic analysis completed");
            return new LanguageAnalysis { Linguistic = analysis };
        }
        catch (Exception ex)
        {
            _metrics?.RecordError("analyze", ex);
            _logger?.LogError(ex, "Error in linguistic analysis");
            throw;
        }
    }

    private static string BuildLinguisticPrompt(string text, string language)
    {
        return language.StartsWith("he", StringComparison.OrdinalIgnoreCase)
            ? $@"אתה מומחה לניתוח לשוני. נתח את הטקסט הבא מבחינה לשונית וחזור בתשובה בפורמט JSON.

פורמט התשובה:
{{
  ""syntaxMetrics"": {{
    ""sentenceCount"": 0,
    ""averageSentenceLength"": 0,
    ""complexSentences"": 0
  }},
  ""morphologyMetrics"": {{
    ""wordCount"": 0,
    ""uniqueWords"": 0,
    ""averageWordLength"": 0
  }},
  ""styleMetrics"": {{
    ""formality"": ""formal|informal|mixed"",
    ""readability"": 0.0
  }},
  ""grammaticalityScore"": 0.9
}}

טקסט לניתוח:
{text}"
            : $@"You are a linguistic analysis expert. Analyze the following text linguistically and return the result in JSON format.

Response format:
{{
  ""syntaxMetrics"": {{
    ""sentenceCount"": 0,
    ""averageSentenceLength"": 0,
    ""complexSentences"": 0
  }},
  ""morphologyMetrics"": {{
    ""wordCount"": 0,
    ""uniqueWords"": 0,
    ""averageWordLength"": 0
  }},
  ""styleMetrics"": {{
    ""formality"": ""formal|informal|mixed"",
    ""readability"": 0.0
  }},
  ""grammaticalityScore"": 0.9
}}

Text to analyze:
{text}";
    }

    private static LinguisticAnalysis ParseLinguisticAnalysis(string responseContent, string language)
    {
        try
        {
            // Try to extract JSON from response
            var jsonStart = responseContent.IndexOf('{');
            var jsonEnd = responseContent.LastIndexOf('}');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = responseContent.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<LinguisticAnalysis>(json, options);
                if (parsed != null)
                {
                    return parsed;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to default analysis
        }

        // Return default analysis if parsing fails
        return new LinguisticAnalysis
        {
            SyntaxMetrics = new Dictionary<string, object>(),
            MorphologyMetrics = new Dictionary<string, object>(),
            StyleMetrics = new Dictionary<string, object>()
        };
    }
}
