using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

public class AiRouter : IAiRouter
{
    private readonly IOptions<AiOptions> _options;
    private readonly PromptFactory _promptFactory;
    private readonly IReadOnlyDictionary<string, IAiAnalysisProvider> _providers;

    public AiRouter(
        IOptions<AiOptions> options,
        PromptFactory promptFactory,
        IReadOnlyDictionary<string, IAiAnalysisProvider> providers)
    {
        _options = options;
        _promptFactory = promptFactory;
        _providers = providers;
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var opt = _options.Value;
        var selection = ResolveSelection(request, opt);

        if (!_providers.TryGetValue(selection.Provider, out var provider))
            throw new InvalidOperationException($"Unknown AI provider: {selection.Provider}. Configured: {string.Join(", ", _providers.Keys)}");

        var (systemMessage, instruction) = _promptFactory.GetPrompt(request.TaskType, request.Language);
        var resolvedInstruction = string.IsNullOrEmpty(request.Instruction)
            ? instruction
            : request.Instruction + "\n\n" + instruction;

        var resolved = new ResolvedAiRequest
        {
            SystemMessage = systemMessage,
            Instruction = resolvedInstruction,
            InputText = request.InputText,
            Language = request.Language,
            Selection = selection,
            TaskType = request.TaskType
        };

        return await provider.CompleteAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> StreamCompleteAsync(AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var opt = _options.Value;
        var selection = ResolveSelection(request, opt);

        if (!_providers.TryGetValue(selection.Provider, out var provider))
            throw new InvalidOperationException($"Unknown AI provider: {selection.Provider}.");

        if (provider is not IStreamingAiAnalysisProvider streaming)
            throw new NotSupportedException($"Provider {selection.Provider} does not support streaming.");

        var (systemMessage, instruction) = _promptFactory.GetPrompt(request.TaskType, request.Language);
        var resolvedInstruction = string.IsNullOrEmpty(request.Instruction)
            ? instruction
            : request.Instruction + "\n\n" + instruction;

        var resolved = new ResolvedAiRequest
        {
            SystemMessage = systemMessage,
            Instruction = resolvedInstruction,
            InputText = request.InputText,
            Language = request.Language,
            Selection = selection,
            TaskType = request.TaskType
        };

        await foreach (var token in streaming.StreamCompleteAsync(resolved, cancellationToken).WithCancellation(cancellationToken))
            yield return token;
    }

    private static AiModelSelection ResolveSelection(AiRequest request, AiOptions opt)
    {
        // Future: experiment/feature-flag override by (UserId, SourceId, TaskType) here

        var taskKey = request.TaskType.ToString();
        var language = request.Language?.Trim() ?? "";

        // For Proofread (and LineEdit, which maps to Proofread), prefer a language-specific model so English uses an English-capable model (e.g. Qwen) instead of DictaLM.
        var isEnglish = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (opt.FeatureModels != null && request.TaskType == AiTaskType.Proofread && isEnglish)
        {
            var langKey = taskKey + "_en";
            if (opt.FeatureModels.TryGetValue(langKey, out var featureEn) &&
                !string.IsNullOrEmpty(featureEn.Provider) && !string.IsNullOrEmpty(featureEn.Model))
            {
                return new AiModelSelection { Provider = featureEn.Provider, Model = featureEn.Model };
            }
        }

        if (opt.FeatureModels != null && opt.FeatureModels.TryGetValue(taskKey, out var feature) &&
            !string.IsNullOrEmpty(feature.Provider) && !string.IsNullOrEmpty(feature.Model))
        {
            return new AiModelSelection { Provider = feature.Provider, Model = feature.Model };
        }

        return new AiModelSelection
        {
            Provider = opt.DefaultProvider ?? "Ollama",
            Model = opt.DefaultModel ?? "qwen2.5:14b"
        };
    }
}
