using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>Anthropic Claude provider.</summary>
public class AnthropicProvider : IAiAnalysisProvider
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly AiOptions _options;

    public AnthropicProvider(IHttpClientFactory httpFactory, IConfiguration config, IOptions<AiOptions> options)
    {
        _httpFactory = httpFactory;
        _config = config;
        _options = options.Value;
    }

    public async Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var apiKey = _config["Ai:Providers:Anthropic:ApiKey"] ?? _config["Ai:Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("AI_ANTHROPIC_APIKEY");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Anthropic ApiKey not configured. Set Ai:Providers:Anthropic:ApiKey or AI_ANTHROPIC_APIKEY.");

        var model = request.Selection.Model;
        var tuning = GetTuning("Anthropic");
        var userContent = request.Instruction + "\n\n" + request.InputText;

        var payload = new
        {
            model,
            max_tokens = tuning.MaxTokens,
            system = request.SystemMessage,
            messages = new[] { new { role = "user", content = userContent } },
            temperature = tuning.Temperature
        };

        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var response = await client.PostAsJsonAsync("https://api.anthropic.com/v1/messages", payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var contentEl = json.GetProperty("content");
        var content = contentEl.GetArrayLength() > 0
            ? contentEl[0].GetProperty("text").GetString() ?? ""
            : "";
        var usage = json.TryGetProperty("usage", out var usageEl) ? usageEl : (JsonElement?)null;
        var inputTokens = usage?.TryGetProperty("input_tokens", out var pt) == true ? pt.GetInt32() : (int?)null;
        var outputTokens = usage?.TryGetProperty("output_tokens", out var ct) == true ? ct.GetInt32() : (int?)null;

        sw.Stop();

        return new AiResponse
        {
            Content = content,
            Provider = request.Selection.Provider,
            Model = model,
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
