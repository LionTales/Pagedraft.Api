using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// THE provider registry: the name → <see cref="IAiAnalysisProvider"/> map that <see cref="AiRouter"/> routes
/// through. Extracted from Program.cs's DI lambda (p1-2) so the set of registered provider NAMES is reachable
/// from tests — the name is what every <c>Ai:ProviderSettings</c> / <c>Ai:FeatureModels</c> key is written
/// against, and it is also what <see cref="ProviderTuningResolver.KnownOutputKnobs"/> classifies. Keeping the
/// list inside a DI lambda meant a newly registered provider could silently miss that classification; now
/// <c>ProviderTuningOutputKnobTests</c> enumerates THIS map and fails if one does.
///
/// Program.cs must stay a one-line delegate to <see cref="Create"/>. The keys are compared
/// case-insensitively, matching how a provider name may be spelled in config.
/// </summary>
public static class AiProviderRegistry
{
    /// <summary>
    /// Builds the registered providers. The <paramref name="ollamaLogger"/> is optional ONLY for tests; the
    /// production registration MUST pass a real one — see the note on <see cref="OllamaProvider"/>'s constructor,
    /// where a NullLogger silently swallows the 404-model-fallback deployment warning.
    /// </summary>
    public static IReadOnlyDictionary<string, IAiAnalysisProvider> Create(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IOptions<AiOptions> options,
        ILogger<OllamaProvider>? ollamaLogger = null)
        => new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ollama"] = new OllamaProvider(httpFactory, config, options, ollamaLogger),
            ["OpenAI"] = new OpenAiProvider(httpFactory, config, options),
            ["Azure"] = new AzureOpenAiProvider(httpFactory, config, options),
            ["Anthropic"] = new AnthropicProvider(httpFactory, config, options),
            ["OpenRouter"] = new OpenAiCompatibleProvider("OpenRouter", httpFactory, config, options)
        };
}
