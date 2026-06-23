using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Single source of truth for resolving the (provider, model) pair that LinguisticAnalysis requests are
/// routed to from configuration. Applies the SAME both-non-empty predicate as
/// <see cref="AiRouter.ResolveSelection"/> (line 104-105): the FeatureModels["LinguisticAnalysis"] entry
/// is only honoured when BOTH Provider AND Model are non-empty; a half-configured entry falls back to
/// DefaultProvider / DefaultModel.
///
/// Extracted so the two consumers that need the resolved model id cannot diverge from each other or from
/// the router:
///   • StyleBaselineService.ResolveLinguisticProviderAndModel (estimate + BookStyleBaseline.BuiltWithModel)
///   • AnalysisContextService (the "active model" used for the cross-model staleness comparison and the
///     status DTO's activeModel).
/// </summary>
public static class LinguisticModelResolver
{
    public static (string provider, string? model) Resolve(AiOptions opt)
        => ResolveForTask(opt, AiTaskType.LinguisticAnalysis);

    /// <summary>Convenience accessor for just the resolved active LinguisticAnalysis model id.</summary>
    public static string? ResolveModel(AiOptions opt) => Resolve(opt).model;

    /// <summary>
    /// Generalised resolution for ANY <see cref="AiTaskType"/> using the SAME both-non-empty predicate as
    /// <see cref="AiRouter.ResolveSelection"/>: the FeatureModels[task] entry is only honoured when BOTH
    /// Provider AND Model are non-empty; a half-configured entry falls back to DefaultProvider /
    /// DefaultModel. Lets per-task cache-freshness gates (e.g. the structured chapter-brief builder keyed
    /// on <see cref="AiTaskType.Summarization"/>) resolve their active model exactly as the router routes
    /// that task, without each duplicating the predicate.
    /// </summary>
    public static (string provider, string? model) ResolveForTask(AiOptions opt, AiTaskType task)
    {
        var taskKey = task.ToString();
        if (opt.FeatureModels != null
            && opt.FeatureModels.TryGetValue(taskKey, out var feature)
            && !string.IsNullOrEmpty(feature.Provider)
            && !string.IsNullOrEmpty(feature.Model))
        {
            return (feature.Provider, feature.Model);
        }
        return (opt.DefaultProvider, opt.DefaultModel);
    }

    /// <summary>Convenience accessor for just the resolved active model id for a given task.</summary>
    public static string? ResolveModelForTask(AiOptions opt, AiTaskType task) => ResolveForTask(opt, task).model;
}
