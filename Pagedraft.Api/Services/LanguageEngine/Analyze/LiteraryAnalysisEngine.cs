using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine.Analyze;

/// <summary>Literary analysis engine extending linguistic analysis with literary features.</summary>
public class LiteraryAnalysisEngine : IAnalyzeEngine
{
    private readonly IAiRouter _aiRouter;
    private readonly ILogger<LiteraryAnalysisEngine>? _logger;
    private readonly ILanguageEngineMetrics? _metrics;

    public LiteraryAnalysisEngine(IAiRouter aiRouter, ILogger<LiteraryAnalysisEngine>? logger = null, ILanguageEngineMetrics? metrics = null)
    {
        _aiRouter = aiRouter;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<LanguageAnalysis> AnalyzeAsync(string text, string language, CancellationToken cancellationToken = default)
    {
        try
        {
            var prompt = BuildLiteraryPrompt(text, language);
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
            var analysis = ParseLiteraryAnalysis(response.Content, language);

            _logger?.LogInformation("Literary analysis completed");
            return analysis;
        }
        catch (Exception ex)
        {
            _metrics?.RecordError("analyze", ex);
            _logger?.LogError(ex, "Error in literary analysis");
            throw;
        }
    }

    private static string BuildLiteraryPrompt(string text, string language)
    {
        return language.StartsWith("he", StringComparison.OrdinalIgnoreCase)
            ? $@"אתה מומחה לניתוח ספרותי. נתח את הטקסט הבא מבחינה ספרותית וחזור בתשובה בפורמט JSON.

פורמט התשובה:
{{
  ""linguistic"": {{
    ""syntaxMetrics"": {{}},
    ""morphologyMetrics"": {{}},
    ""styleMetrics"": {{}},
    ""grammaticalityScore"": 0.9
  }},
  ""literary"": {{
    ""themes"": [""נושא 1"", ""נושא 2""],
    ""tone"": ""טון הטקסט"",
    ""narrativeVoice"": ""קול מספר"",
    ""rhetoricalDevices"": [""אמצעי 1"", ""אמצעי 2""]
  }}
}}

טקסט לניתוח:
{text}"
            : $@"You are a literary analysis expert. Analyze the following text literarily and return the result in JSON format.

Response format:
{{
  ""linguistic"": {{
    ""syntaxMetrics"": {{}},
    ""morphologyMetrics"": {{}},
    ""styleMetrics"": {{}},
    ""grammaticalityScore"": 0.9
  }},
  ""literary"": {{
    ""themes"": [""theme 1"", ""theme 2""],
    ""tone"": ""text tone"",
    ""narrativeVoice"": ""narrative voice"",
    ""rhetoricalDevices"": [""device 1"", ""device 2""]
  }}
}}

Text to analyze:
{text}";
    }

    private static LanguageAnalysis ParseLiteraryAnalysis(string responseContent, string language)
    {
        try
        {
            var jsonStart = responseContent.IndexOf('{');
            var jsonEnd = responseContent.LastIndexOf('}');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = responseContent.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<LanguageAnalysis>(json, options);
                if (parsed != null)
                {
                    return parsed;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to default
        }

        return new LanguageAnalysis
        {
            Linguistic = new LinguisticAnalysis(),
            Literary = new LiteraryAnalysis()
        };
    }
}
