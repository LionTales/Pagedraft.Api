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
            : ShouldUseUnifiedInstructionVerbatim(request)
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
            : ShouldUseUnifiedInstructionVerbatim(request)
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

    /// <summary>
    /// THE MODEL-TIER SEAM (model-tier-fast-thinking plan, p3-2). This method used to open with
    /// "Future: experiment/feature-flag override by (UserId, SourceId, TaskType) here"; the tier is that
    /// override, and it lands here rather than at a new seam.
    ///
    /// It is keyed on <see cref="AiRequest.Tier"/>, which the CALLER stamps from the book, NOT on
    /// <see cref="AiRequest.SourceId"/> - the comment's suggested key. SourceId is heterogeneous (chapterId
    /// / sceneId / bookId / the literals "repair" and "term-repair" across its assignment sites), so it is
    /// not a book identifier and cannot be used to look one up. Keeping the tier on the request also keeps
    /// the router DB-free.
    ///
    /// The whole four-rung precedence lives in
    /// <see cref="LinguisticModelResolver.ResolveForTask(AiOptions, AiTaskType, string?, AiTier)"/>,
    /// including the language rung this method used to spell out inline. That is deliberate: the resolver
    /// exists precisely because a staleness gate resolving differently from the router is the failure mode,
    /// and one implementation cannot drift from itself. An absent tier means
    /// <see cref="AiTier.Fast"/>, i.e. resolution byte-identical to the pre-tier behaviour.
    /// </summary>
    private static AiModelSelection ResolveSelection(AiRequest request, AiOptions opt)
    {
        var (provider, model) = LinguisticModelResolver.ResolveForTask(
            opt, request.TaskType, request.Language, request.Tier ?? AiTier.Fast);

        // Last-resort literals preserved from the pre-p3-2 router: they only bite when AiOptions binds a
        // NULL default (the class initializers are "Ollama" / "qwen2.5:14b"), and the resolver returns the
        // configured defaults verbatim rather than substituting its own.
        return new AiModelSelection
        {
            Provider = provider ?? "Ollama",
            Model = model ?? "qwen2.5:14b"
        };
    }

    /// <summary>
    /// Test seam over <see cref="ResolveSelection"/> so the router-vs-resolver agreement can be asserted on
    /// the ROUTER'S OWN code path for every (task, language, tier) triple without booting a provider.
    /// </summary>
    internal static AiModelSelection ResolveSelectionForTest(AiRequest request, AiOptions opt)
        => ResolveSelection(request, opt);

    /// <summary>
    /// For unified analysis flows (LineEdit, LinguisticAnalysis, BookReview, AnalysisRepair, TermRepair) and for
    /// grounded product chat (ProductChat, whose caller composes the whole grounding + guides + history block and
    /// whose contract the legacy heading/numbered-list instruction would directly contradict), avoid
    /// appending the legacy pipeline instruction when the caller already provided a complete,
    /// task-specific instruction (for AnalysisRepair, the value-only Hebrew cleanup prompt; for TermRepair,
    /// the marked-span replace-one-word prompt). These
    /// have dedicated AiTaskTypes and run with structured JSON output (format=json), so the legacy
    /// heading/numbered-list pipeline prompt must NOT be appended (it would contradict the JSON schema
    /// and hurt quality/parsing). Also kept: heuristic detection for any prompt containing the
    /// sentence-level line edit marker text.
    /// </summary>
    private static bool ShouldUseUnifiedInstructionVerbatim(AiRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Instruction))
            return false;

        if (request.TaskType is AiTaskType.LineEdit or AiTaskType.LinguisticAnalysis or AiTaskType.BookReview or AiTaskType.AnalysisRepair or AiTaskType.TermRepair or AiTaskType.ProductChat)
            return true;

        var instruction = request.Instruction;
        if (instruction.IndexOf("Perform a sentence-level line edit", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (instruction.Contains("בצע עריכה ברמת המשפט", StringComparison.Ordinal))
            return true;

        return false;
    }
}
