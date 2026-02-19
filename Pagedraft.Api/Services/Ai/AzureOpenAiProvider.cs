using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>Azure OpenAI provider (same API shape as OpenAI).</summary>
public class AzureOpenAiProvider : IAiAnalysisProvider
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly AiOptions _options;

    public AzureOpenAiProvider(IHttpClientFactory httpFactory, IConfiguration config, IOptions<AiOptions> options)
    {
        _httpFactory = httpFactory;
        _config = config;
        _options = options.Value;
    }

    public async Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var endpoint = _config["Ai:Providers:Azure:Endpoint"] ?? _config["Ai:Azure:Endpoint"];
        var deployment = _config["Ai:Providers:Azure:DeploymentName"] ?? _config["Ai:Azure:DeploymentName"];
        var apiKey = _config["Ai:Providers:Azure:ApiKey"] ?? _config["Ai:Azure:ApiKey"] ?? Environment.GetEnvironmentVariable("AI_AZURE_APIKEY");

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(deployment) || string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Azure OpenAI Endpoint, DeploymentName, and ApiKey must be configured.");

        var baseUrl = endpoint.TrimEnd('/') + "/openai/deployments/" + deployment;
        var tuning = GetTuning("Azure");
        var userContent = request.Instruction + "\n\n" + request.InputText;

        var payload = new
        {
            messages = new[]
            {
                new { role = "system", content = request.SystemMessage },
                new { role = "user", content = userContent }
            },
            temperature = tuning.Temperature,
            max_tokens = tuning.MaxTokens
        };

        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Add("api-key", apiKey);

        var url = baseUrl + "/chat/completions?api-version=2024-02-15-preview";
        var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var content = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var usage = json.TryGetProperty("usage", out var usageEl) ? usageEl : (JsonElement?)null;
        var inputTokens = usage?.TryGetProperty("input_tokens", out var pt) == true ? pt.GetInt32() : (int?)null;
        var outputTokens = usage?.TryGetProperty("output_tokens", out var ct) == true ? ct.GetInt32() : (int?)null;

        sw.Stop();

        return new AiResponse
        {
            Content = content,
            Provider = request.Selection.Provider,
            Model = request.Selection.Model,
            Duration = sw.Elapsed,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ApproxCostUsd = null,
            Extra = null
        };
    }

    private ProviderTuningOptions GetTuning(string providerName)
    {
        if (_options.ProviderSettings != null && _options.ProviderSettings.TryGetValue(providerName, out var t))
            return t;
        return new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 2048 };
    }
}
