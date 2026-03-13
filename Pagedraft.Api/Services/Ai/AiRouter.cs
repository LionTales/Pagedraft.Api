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

        var (systemMessage, pipelineInstruction) = _promptFactory.GetPrompt(request.TaskType, request.Language);
        var resolvedInstruction = string.IsNullOrEmpty(request.Instruction)
            ? pipelineInstruction
            : ShouldUseUnifiedInstructionVerbatim(request, pipelineInstruction)
                ? request.Instruction
                : request.Instruction + "\n\n" + pipelineInstruction;

        var resolved = new ResolvedAiRequest
        {
            SystemMessage = systemMessage,
            Instruction = resolvedInstruction,
            InputText = request.InputText,
            Language = request.Language,
            Selection = selection,
            TaskType = request.TaskType,
            JsonMode = request.JsonMode
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

        var (systemMessage, pipelineInstruction) = _promptFactory.GetPrompt(request.TaskType, request.Language);
        var resolvedInstruction = string.IsNullOrEmpty(request.Instruction)
            ? pipelineInstruction
            : ShouldUseUnifiedInstructionVerbatim(request, pipelineInstruction)
                ? request.Instruction
                : request.Instruction + "\n\n" + pipelineInstruction;

        var resolved = new ResolvedAiRequest
        {
            SystemMessage = systemMessage,
            Instruction = resolvedInstruction,
            InputText = request.InputText,
            Language = request.Language,
            Selection = selection,
            TaskType = request.TaskType,
            JsonMode = request.JsonMode
        };

        await foreach (var token in streaming.StreamCompleteAsync(resolved, cancellationToken).WithCancellation(cancellationToken))
            yield return token;
    }

    private static AiModelSelection ResolveSelection(AiRequest request, AiOptions opt)
    {
        // Future: experiment/feature-flag override by (UserId, SourceId, TaskType) here

        var taskKey = request.TaskType.ToString();
        var language = request.Language?.Trim() ?? "";

        var isEnglish = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (opt.FeatureModels != null &&
            (request.TaskType == AiTaskType.Proofread || request.TaskType == AiTaskType.LineEdit) &&
            isEnglish)
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

    /// <summary>
    /// For unified analysis flows (e.g. LineEdit), avoid appending the legacy pipeline
    /// instruction when the caller already provided a complete, task-specific instruction.
    /// LineEdit has its own dedicated AiTaskType; also kept: heuristic detection for
    /// any prompt containing the sentence-level line edit marker text.
    /// </summary>
    private static bool ShouldUseUnifiedInstructionVerbatim(AiRequest request, string pipelineInstruction)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Instruction))
            return false;

        if (request.TaskType == AiTaskType.LineEdit)
            return true;

        var instruction = request.Instruction;
        if (instruction.IndexOf("Perform a sentence-level line edit", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (instruction.Contains("בצע עריכה ברמת המשפט", StringComparison.Ordinal))
            return true;

        return false;
    }
}
