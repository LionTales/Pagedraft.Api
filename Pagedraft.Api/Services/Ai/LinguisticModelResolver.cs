using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// THE single implementation of (provider, model) resolution from <see cref="AiOptions"/>.
/// <see cref="AiRouter.ResolveSelection"/> DELEGATES here (p3-2), so the router and every staleness gate
/// cannot resolve differently - they run the same code, not two copies of the same rules.
///
/// Extracted originally so the consumers that need the resolved model id could not diverge from each other
/// or from the router:
///   • StyleBaselineService.ResolveLinguisticProviderAndModel (estimate + BookStyleBaseline.BuiltWithModel)
///   • AnalysisContextService (the "active model" used for the cross-model staleness comparison and the
///     status DTO's activeModel)
///   • BookContextAssembler.ResolveNumCtxForTask / ResolveOutputReserveForTask, which use the resolved
///     PROVIDER NAME to build the <c>{Provider}_{TaskType}</c> tuning key (p1-1..p1-3). A tier that changes
///     the provider therefore also moves NumCtx, the output reservation, and - through
///     UnifiedAnalysisService.EffectiveChunkTargetWords - the /api/config/analysis-chunk-thresholds
///     contract p1-4 pinned.
///
/// THE PRECEDENCE (p3-2; the order is decided in the plan's "## p3 scope decision" section, layer E3, and
/// it deliberately INVERTS the example written into p3-2's todo text - see the note below):
/// <code>
///   1. {task}_{lang}   e.g. Proofread_en      - English Proofread/LineEdit only, UNTIERED
///   2. {task}_{tier}   e.g. Proofread_thinking - allowlisted tasks only, Thinking only
///   3. {task}          e.g. Proofread
///   4. AiOptions.DefaultProvider / DefaultModel
/// </code>
/// There is NO <c>{task}_{lang}_{tier}</c> rung and no such key may exist. That is not a simplification, it
/// is the enforcement: <c>Proofread_en</c> is not an <see cref="AiTaskType"/>, it is a key suffix, so
/// "the GO is for HEBREW Proofread only" can only be expressed as precedence. Because rung 1 outranks
/// rung 2, an English book on the thinking tier resolves <c>Proofread_en</c> (local) and CANNOT reach the
/// tier rung. Under the inverted order in the todo text it would have reached it, violating p2-4's
/// Proofread_en NO-GO.
///
/// Every rung applies the SAME both-non-empty predicate (<see cref="TryFeature"/>): an entry is honoured
/// only when BOTH Provider AND Model are non-empty, so a HALF-configured entry falls through to the next
/// rung rather than half-routing.
/// </summary>
public static class LinguisticModelResolver
{
    public static (string provider, string? model) Resolve(AiOptions opt)
        => ResolveForTask(opt, AiTaskType.LinguisticAnalysis);

    /// <summary>Convenience accessor for just the resolved active LinguisticAnalysis model id.</summary>
    public static string? ResolveModel(AiOptions opt) => Resolve(opt).model;

    /// <summary>
    /// Untiered, language-agnostic resolution - the pre-p3-2 behaviour, preserved EXACTLY. Equivalent to
    /// <see cref="ResolveForTask(AiOptions, AiTaskType, string?, AiTier)"/> with no language and
    /// <see cref="AiTier.Fast"/>: the language rung cannot fire without a language and the tier rung cannot
    /// fire on Fast, so this is rung 3 then rung 4, exactly as before. Kept so the consumers that have no
    /// tier in scope compile and behave untouched.
    /// </summary>
    public static (string provider, string? model) ResolveForTask(AiOptions opt, AiTaskType task)
        => ResolveForTask(opt, task, language: null, AiTier.Fast);

    /// <summary>
    /// Tier-aware, language-agnostic resolution. For a task outside
    /// <see cref="AiTierPolicy.TieredTasks"/> this is IDENTICAL to the 2-arg overload on both tiers.
    /// </summary>
    public static (string provider, string? model) ResolveForTask(AiOptions opt, AiTaskType task, AiTier tier)
        => ResolveForTask(opt, task, language: null, tier);

    /// <summary>
    /// THE implementation of the four-rung precedence documented on this class. Used directly by
    /// <see cref="AiRouter"/> (which has the request's language and tier) and by the budget sizers.
    /// </summary>
    /// <param name="language">
    /// The request language. Only the <c>en*</c> prefix on <see cref="AiTaskType.Proofread"/> /
    /// <see cref="AiTaskType.LineEdit"/> selects a language rung, matching the shipped
    /// <c>Proofread_en</c> / <c>LineEdit_en</c> keys; null or any other tag skips rung 1.
    /// </param>
    /// <param name="tier">The BOOK's tier. Callers stamp it; nothing is looked up from a request id.</param>
    public static (string provider, string? model) ResolveForTask(
        AiOptions opt, AiTaskType task, string? language, AiTier tier)
    {
        // Rung 1 - {task}_{lang}. Outranks the tier on purpose (layer E3).
        var languageKey = LanguageKeyFor(task, language);
        if (languageKey != null && TryFeature(opt, languageKey, out var byLanguage))
            return byLanguage;

        // Rung 2 - {task}_{tier}. Null unless the task is allowlisted AND the tier is Thinking.
        var tierKey = AiTierPolicy.TierKeyFor(task, tier);
        if (tierKey != null && TryFeature(opt, tierKey, out var byTier))
            return byTier;

        // Rung 3 - {task}.
        if (TryFeature(opt, task.ToString(), out var byTask))
            return byTask;

        // Rung 4 - the configured defaults.
        return (opt.DefaultProvider, opt.DefaultModel);
    }

    /// <summary>
    /// TRUE when rung 1 (<c>{task}_{lang}</c>) resolves for this request, i.e. the language rung WINS and
    /// therefore suppresses the tier rung below it (tier-ux-rework c2).
    ///
    /// It exists so the surface can tell the two shapes of "thinking will not move this task" apart. Both
    /// look identical from the outside - the thinking route equals the fast route - but they are different
    /// sentences to a user and have different fixes:
    ///   • this predicate TRUE  = "English proofreading always stays fast", a permanent design property
    ///     (layer E3, the p2-4 <c>Proofread_en</c> NO-GO), nothing to configure;
    ///   • this predicate FALSE = the <c>{task}_thinking</c> key is absent or half-configured, the documented
    ///     kill-switch, which an operator CAN change.
    /// Answering that from the same resolver that owns the precedence is the point: a second copy of "does
    /// Proofread_en exist" living in the status service would drift the moment a rung moved.
    /// </summary>
    public static bool LanguageRungWins(AiOptions opt, AiTaskType task, string? language)
    {
        var key = LanguageKeyFor(task, language);
        return key != null && TryFeature(opt, key, out _);
    }

    /// <summary>
    /// The language-keyed FeatureModels key for a request, or null when there is no language rung. Mirrors
    /// the condition <see cref="AiRouter.ResolveSelection"/> applied inline before p3-2: trim the tag, match
    /// an <c>en</c> PREFIX case-insensitively (so <c>en-US</c> counts), and only for the two tasks that ship
    /// a <c>_en</c> variant.
    /// </summary>
    private static string? LanguageKeyFor(AiTaskType task, string? language)
    {
        if (task != AiTaskType.Proofread && task != AiTaskType.LineEdit)
            return null;

        var tag = language?.Trim() ?? "";
        return tag.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? $"{task}_en" : null;
    }

    /// <summary>
    /// The shared BOTH-NON-EMPTY predicate. A FeatureModels entry that sets only Provider or only Model is
    /// half-configured and must fall through to the next rung, never route with a blank half.
    /// </summary>
    private static bool TryFeature(AiOptions opt, string key, out (string provider, string? model) resolved)
    {
        if (opt.FeatureModels != null
            && opt.FeatureModels.TryGetValue(key, out var feature)
            && !string.IsNullOrEmpty(feature.Provider)
            && !string.IsNullOrEmpty(feature.Model))
        {
            resolved = (feature.Provider, feature.Model);
            return true;
        }

        resolved = default;
        return false;
    }

    /// <summary>Convenience accessor for just the resolved active model id for a given task.</summary>
    public static string? ResolveModelForTask(AiOptions opt, AiTaskType task) => ResolveForTask(opt, task).model;

    /// <summary>Tier-aware counterpart of <see cref="ResolveModelForTask(AiOptions, AiTaskType)"/>.</summary>
    public static string? ResolveModelForTask(AiOptions opt, AiTaskType task, AiTier tier)
        => ResolveForTask(opt, task, tier).model;
}
