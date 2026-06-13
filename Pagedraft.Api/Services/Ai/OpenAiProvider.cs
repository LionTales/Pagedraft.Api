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

        var payload = BuildPayload(model, request, tuning);

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

    /// <summary>
    /// Builds the OpenAI /v1/chat/completions request body. Extracted from <see cref="CompleteAsync"/> so the
    /// per-model-family param logic can be unit-tested without a network call. Behavior MUST stay identical to
    /// inline construction: same property names (`max_completion_tokens` vs `max_tokens`), same temperature-omit
    /// conditional.
    /// MAINTENANCE: when a NEW model family that requires `max_completion_tokens` / drops `temperature` (like
    /// gpt-5 does) ships, extend the prefix check below AND extend ProviderPayloadTests, or it will silently
    /// send an unsupported param and 400. See AnthropicProvider for the equivalent omit-list.
    /// </summary>
    internal static Dictionary<string, object?> BuildPayload(string model, ResolvedAiRequest request, ProviderTuningOptions tuning)
    {
        var userContent = request.Instruction + "\n\n" + request.InputText;

        // The GPT-5 family rejects `max_tokens` (must use `max_completion_tokens`) and rejects a
        // non-default `temperature` (only the default is allowed). Older models (gpt-4o, etc.) use
        // `max_tokens` + `temperature` exactly as before.
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new { role = "system", content = request.SystemMessage },
                new { role = "user", content = userContent }
            }
        };
        if (model.StartsWith("gpt-5"))
        {
            payload["max_completion_tokens"] = tuning.MaxTokens;
        }
        else
        {
            payload["temperature"] = tuning.Temperature;
            payload["max_tokens"] = tuning.MaxTokens;
        }
        return payload;
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
