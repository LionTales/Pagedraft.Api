using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>Ollama-backed AI provider (local models: Qwen2.5, Dicta-LM, etc.).</summary>
public class OllamaProvider : IAiAnalysisProvider, IStreamingAiAnalysisProvider
{
    // Stop sequences to avoid model rambling. Exclude instruction echoes (e.g. "בדוק את הטקסט הבא")
    // so proofread output is not truncated to empty when the model repeats the task.
    // Do NOT add the think-close tag here: we need the text after the thinking block; StripThinkBlock in UnifiedAnalysisService handles it.
    private static readonly string[] StopSequences =
    {
        "\n\n\n", "以下是修正后的", "Ensure accuracy and fluency", "但由于", "请提供", "在此文本",
        "所提供的是", "为了提供帮助", "请提供具体", "改成", "改为", "更符合", "Hebrew内容",
        "以下是正确的希伯来语", "被错误地", "希伯来语校对", "אין טקסט לעריכה",
        "Corrections and suggestions"
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly AiOptions _options;

    public OllamaProvider(IHttpClientFactory httpFactory, IConfiguration config, IOptions<AiOptions> options)
    {
        _httpFactory = httpFactory;
        _config = config;
        _options = options.Value;
    }

    public async Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var baseUrl = _config["Ai:Providers:Ollama:BaseUrl"] ?? _config["Ai:Ollama:BaseUrl"] ?? "http://localhost:11434";
        var defaultModel = _config["Ai:Providers:Ollama:DefaultModel"] ?? _config["Ai:DefaultModel"] ?? "qwen2.5:14b";
        var model = request.Selection.Model;
        var prompt = BuildOllamaPrompt(request);

        var client = _httpFactory.CreateClient("Ollama");
        client.BaseAddress = new Uri(baseUrl);

        var tuning = GetTuning("Ollama", request.TaskType);
        var options = new { temperature = tuning.Temperature, num_predict = tuning.NumPredict };

        object payload = request.JsonMode
            ? new { model, prompt, stream = false, think = false, options, stop = StopSequences, format = "json" }
            : new { model, prompt, stream = false, think = false, options, stop = StopSequences };

        var response = await client.PostAsJsonAsync("/api/generate", payload, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && model != defaultModel)
        {
            response.Dispose();
            model = defaultModel;
            payload = request.JsonMode
                ? new { model, prompt, stream = false, think = false, options, stop = StopSequences, format = "json" } as object
                : new { model, prompt, stream = false, think = false, options, stop = StopSequences };
            response = await client.PostAsJsonAsync("/api/generate", payload, cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var msg = response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? $"Ollama model '{model}' not found. Pull it with: ollama pull {model}"
                : $"Ollama request failed: {response.StatusCode}";
            response.Dispose();
            throw new HttpRequestException(msg);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var text = json.GetProperty("response").GetString() ?? "";

        sw.Stop();
        return new AiResponse
        {
            Content = text,
            Provider = request.Selection.Provider,
            Model = model,
            Duration = sw.Elapsed,
            InputTokens = null,
            OutputTokens = null,
            Extra = null
        };
    }

    public async IAsyncEnumerable<string> StreamCompleteAsync(ResolvedAiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = _config["Ai:Providers:Ollama:BaseUrl"] ?? _config["Ai:Ollama:BaseUrl"] ?? "http://localhost:11434";
        var defaultModel = _config["Ai:Providers:Ollama:DefaultModel"] ?? _config["Ai:DefaultModel"] ?? "qwen2.5:14b";
        var model = request.Selection.Model;
        var prompt = BuildOllamaPrompt(request);

        var client = _httpFactory.CreateClient("Ollama");
        client.BaseAddress = new Uri(baseUrl);

        var tuning = GetTuning("Ollama", request.TaskType);
        var options = new { temperature = tuning.Temperature, num_predict = tuning.NumPredict };
        var payload = new { model, prompt, stream = true, think = false, options, stop = StopSequences };

        using var response = await client.PostAsJsonAsync("/api/generate", payload, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && model != defaultModel)
        {
            model = defaultModel;
            payload = new { model, prompt, stream = true, think = false, options, stop = StopSequences };
            using var retryResponse = await client.PostAsJsonAsync("/api/generate", payload, cancellationToken).ConfigureAwait(false);
            if (!retryResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException(retryResponse.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? $"Ollama model '{model}' not found. Pull it with: ollama pull {model}"
                    : $"Ollama request failed: {retryResponse.StatusCode}");
            }
            await using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var retryReader = new StreamReader(retryStream);
            while (await retryReader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var json = JsonSerializer.Deserialize<JsonElement>(line);
                if (json.TryGetProperty("response", out var token))
                    yield return token.GetString() ?? "";
            }
            yield break;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? $"Ollama model '{model}' not found. Pull it with: ollama pull {model}"
                : $"Ollama request failed: {response.StatusCode}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var json = JsonSerializer.Deserialize<JsonElement>(line);
            if (json.TryGetProperty("response", out var token))
                yield return token.GetString() ?? "";
        }
    }

    private static string BuildOllamaPrompt(ResolvedAiRequest request)
    {
        var userContent = request.Instruction + "\n\n" + request.InputText;
        return $"<system>{request.SystemMessage}</system>\n<user>{userContent}</user>";
    }

    private ProviderTuningOptions GetTuning(string providerName, AiTaskType taskType = AiTaskType.GenericChat)
    {
        if (_options.ProviderSettings != null)
        {
            var taskSpecificKey = providerName + "_" + taskType;
            if (_options.ProviderSettings.TryGetValue(taskSpecificKey, out var taskTuning))
                return taskTuning;
        }
        if (_options.ProviderSettings != null && _options.ProviderSettings.TryGetValue(providerName, out var t))
            return t;
        return new ProviderTuningOptions { Temperature = 0.2, NumPredict = 2048 };
    }
}
