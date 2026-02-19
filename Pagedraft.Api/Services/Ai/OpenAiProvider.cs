using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>OpenAI (GPT-4o, etc.) provider.</summary>
public class OpenAiProvider : IAiAnalysisProvider
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly AiOptions _options;

    public OpenAiProvider(IHttpClientFactory httpFactory, IConfiguration config, IOptions<AiOptions> options)
    {
        _httpFactory = httpFactory;
        _config = config;
        _options = options.Value;
    }

    public async Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var apiKey = _config["Ai:Providers:OpenAI:ApiKey"] ?? _config["Ai:OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("AI_OPENAI_APIKEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("OpenAI ApiKey not configured. Set Ai:Providers:OpenAI:ApiKey or AI_OPENAI_APIKEY.");

        var model = request.Selection.Model;
        var tuning = GetTuning("OpenAI");
        var userContent = request.Instruction + "\n\n" + request.InputText;

        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = request.SystemMessage },
                new { role = "user", content = userContent }
            },
            temperature = tuning.Temperature,
            max_tokens = tuning.MaxTokens
        };

        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var content = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var usage = json.TryGetProperty("usage", out var usageEl) ? usageEl : (JsonElement?)null;
        var inputTokens = usage?.TryGetProperty("prompt_tokens", out var pt) == true ? pt.GetInt32() : (int?)null;
        var outputTokens = usage?.TryGetProperty("completion_tokens", out var ct) == true ? ct.GetInt32() : (int?)null;

        sw.Stop();

        return new AiResponse
        {
            Content = content,
            Provider = request.Selection.Provider,
            Model = model,
            Duration = sw.Elapsed,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ApproxCostUsd = EstimateCost(model, inputTokens, outputTokens),
            Extra = null
        };
    }

    private static decimal? EstimateCost(string model, int? input, int? output)
    {
        if (input == null || output == null) return null;
        // Approximate USD per 1M tokens (GPT-4o)
        decimal inputPerM = 2.50m, outputPerM = 10.00m;
        if (model.Contains("gpt-4o-mini", StringComparison.OrdinalIgnoreCase)) { inputPerM = 0.15m; outputPerM = 0.60m; }
        return (input.Value / 1_000_000m) * inputPerM + (output.Value / 1_000_000m) * outputPerM;
    }

    private ProviderTuningOptions GetTuning(string providerName)
    {
        if (_options.ProviderSettings != null && _options.ProviderSettings.TryGetValue(providerName, out var t))
            return t;
        return new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 2048 };
    }
}
