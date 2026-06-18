using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Generic OpenAI-compatible provider (e.g. OpenRouter, Together AI, Groq).
/// Reads base URL + API key from <c>Ai:Providers:{name}:BaseUrl</c> /
/// <c>Ai:Providers:{name}:ApiKey</c> (fallback env <c>AI_{NAME}_APIKEY</c>).
/// Supports optional per-provider extra request headers from
/// <c>Ai:Providers:{name}:Headers</c> (string-&gt;string map, e.g. OpenRouter's
/// <c>HTTP-Referer</c> / <c>X-Title</c>).
/// </summary>
public class OpenAiCompatibleProvider : IAiAnalysisProvider
{
    private readonly string _providerName;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly AiOptions _options;

    public OpenAiCompatibleProvider(
        string providerName,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IOptions<AiOptions> options)
    {
        _providerName = providerName;
        _httpFactory = httpFactory;
        _config = config;
        _options = options.Value;
    }

    public async Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var baseUrl = _config[$"Ai:Providers:{_providerName}:BaseUrl"];
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException($"{_providerName} BaseUrl not configured. Set Ai:Providers:{_providerName}:BaseUrl.");

        var nameUpper = _providerName.ToUpperInvariant();
        var apiKey = _config[$"Ai:Providers:{_providerName}:ApiKey"];
        // Treat an unset value OR an uninterpolated committed placeholder (e.g. "__AI_OPENROUTER_APIKEY__")
        // as "no key in config" and fall back to the environment variable. The placeholder is non-empty, so
        // without this guard it would be sent verbatim as the Bearer token and rejected (401).
        if (string.IsNullOrWhiteSpace(apiKey) || (apiKey.StartsWith("__") && apiKey.EndsWith("__")))
            apiKey = Environment.GetEnvironmentVariable($"AI_{nameUpper}_APIKEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"{_providerName} ApiKey not configured. Set Ai:Providers:{_providerName}:ApiKey or AI_{nameUpper}_APIKEY.");

        var model = request.Selection.Model;
        var tuning = GetTuning(_providerName);

        var payload = OpenAiProvider.BuildPayload(model, request, tuning);

        if (request.JsonMode)
            payload["response_format"] = new { type = "json_object" };

        var endpointUrl = baseUrl.TrimEnd('/') + "/chat/completions";

        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        // Apply optional extra headers from config (e.g. HTTP-Referer, X-Title for OpenRouter)
        var headersSection = _config.GetSection($"Ai:Providers:{_providerName}:Headers");
        foreach (var header in headersSection.GetChildren())
        {
            if (!string.IsNullOrEmpty(header.Key) && !string.IsNullOrEmpty(header.Value))
                client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        var response = await client.PostAsJsonAsync(endpointUrl, payload, cancellationToken).ConfigureAwait(false);
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
            ApproxCostUsd = null, // Cost varies by model/pricing tier on compatible providers
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
