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
    {
        var taskKey = AiTaskType.LinguisticAnalysis.ToString();
        if (opt.FeatureModels != null
            && opt.FeatureModels.TryGetValue(taskKey, out var feature)
            && !string.IsNullOrEmpty(feature.Provider)
            && !string.IsNullOrEmpty(feature.Model))
        {
            return (feature.Provider, feature.Model);
        }
        return (opt.DefaultProvider, opt.DefaultModel);
    }

    /// <summary>Convenience accessor for just the resolved active LinguisticAnalysis model id.</summary>
    public static string? ResolveModel(AiOptions opt) => Resolve(opt).model;
}
