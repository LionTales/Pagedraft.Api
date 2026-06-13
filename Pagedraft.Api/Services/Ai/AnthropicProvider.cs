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

        var payload = BuildPayload(model, request, tuning);

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

    /// <summary>
    /// Builds the Anthropic /v1/messages request body. Extracted from <see cref="CompleteAsync"/> so the
    /// per-model-family sampling-param logic can be unit-tested without a network call. Behavior MUST stay
    /// identical to inline construction: same property names, same temperature-omit conditional.
    /// </summary>
    internal static Dictionary<string, object?> BuildPayload(string model, ResolvedAiRequest request, ProviderTuningOptions tuning)
    {
        var userContent = request.Instruction + "\n\n" + request.InputText;

        // Some Anthropic models (Opus 4.7/4.8, Fable 5, Mythos 5) reject the `temperature` sampling
        // parameter (HTTP 400). Build the payload so `temperature` is simply absent for those models;
        // Sonnet 4.6 / Haiku 4.5 / older Claude still accept it. We intentionally do NOT add a
        // `thinking` field: adaptive thinking is off by default on Opus 4.7/4.8 when unset, and
        // Fable 5 has thinking always-on automatically.
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = tuning.MaxTokens,
            ["system"] = request.SystemMessage,
            ["messages"] = new[] { new { role = "user", content = userContent } }
        };
        if (AnthropicSupportsTemperature(model))
            payload["temperature"] = tuning.Temperature;
        return payload;
    }

    /// <summary>
    /// Whether the given Anthropic model accepts a custom `temperature`. Opus 4.7/4.8, Fable 5 and
    /// Mythos 5 reject it (HTTP 400); Sonnet 4.6, Haiku 4.5 and older Claude still accept it.
    /// MAINTENANCE: when a NEW Claude family that drops/changes `temperature` (or any sampling param)
    /// ships, add its model-id prefix here AND extend ProviderPayloadTests, or it will silently send an
    /// unsupported param and 400. See OpenAiProvider for the equivalent gpt-5 list.
    /// </summary>
    private static bool AnthropicSupportsTemperature(string model)
        => !(model.StartsWith("claude-opus-4-7") || model.StartsWith("claude-opus-4-8")
            || model.StartsWith("claude-fable") || model.StartsWith("claude-mythos"));

    private ProviderTuningOptions GetTuning(string providerName)
    {
        if (_options.ProviderSettings != null && _options.ProviderSettings.TryGetValue(providerName, out var t))
            return t;
        return new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 2048 };
    }
}
