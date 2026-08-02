using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Why the thinking tier is or is not usable on THIS deployment. One value, four states, and the difference
/// between them is the difference between two very different user-facing sentences.
/// </summary>
public enum AiTierReadiness
{
    /// <summary>Every allowlisted task that the tier can move resolves a route whose provider is registered
    /// and has credentials. Choosing the tier will actually reach the cloud provider.</summary>
    Ready = 0,

    /// <summary>
    /// NO allowlisted task moves on the thinking tier: the <c>{task}_thinking</c> keys are absent, or
    /// half-configured and therefore fell through the shared both-non-empty predicate. This is the
    /// documented KILL-SWITCH state (delete the two keys and every book resolves local again), and it is the
    /// one state where a book stored as "thinking" SILENTLY runs on the local model. The UI must say so -
    /// that is the whole point of surfacing this enum rather than a bool.
    /// </summary>
    RouteNotConfigured = 1,

    /// <summary>A tier route names a provider that is not in the provider registry. The run would throw
    /// "Unknown AI provider" at call time. Loud, but the user should not have to discover it by running.</summary>
    ProviderNotRegistered = 2,

    /// <summary>A tier route's provider is registered but has no API key (config or <c>AI_{NAME}_APIKEY</c>).
    /// The run would throw "ApiKey not configured" at call time. Again loud, again worth pre-flighting.</summary>
    ProviderCredentialsMissing = 3,

    /// <summary>
    /// PER-TASK ONLY (tier-ux-rework c2). This task is outside <see cref="AiTierPolicy.TieredTasks"/> -
    /// LineEdit and BookReview - so there is no tier rung to consult at all and it runs fast whatever the
    /// deployment does. Split out of <see cref="RouteNotConfigured"/> because the two are different sentences:
    /// this one is a permanent product property with nothing to fix, while RouteNotConfigured is an operator's
    /// kill-switch that an operator can undo.
    /// </summary>
    TaskNotEligible = 4,

    /// <summary>
    /// PER-TASK ONLY (tier-ux-rework c2). The <c>{task}_{lang}</c> rung resolves for this book's language and
    /// OUTRANKS the tier rung (layer E3), so the task stays fast for this language by design - the shipped
    /// case is an ENGLISH book's Proofread, which IS the p2-4 <c>Proofread_en</c> NO-GO. Also different from
    /// <see cref="RouteNotConfigured"/>: wiring the missing tier key would not change this task's answer,
    /// changing the BOOK'S LANGUAGE would.
    /// </summary>
    LanguageAlwaysFast = 5
}

/// <summary>
/// One allowlisted task's ACTUAL route for a given book (its language) and a given tier: the (provider,
/// model) that <see cref="LinguisticModelResolver"/> - the same code the router runs - resolves.
/// </summary>
/// <param name="Task">The <see cref="AiTaskType"/> name.</param>
/// <param name="Provider">The resolved provider name, e.g. "Ollama" or "OpenRouter".</param>
/// <param name="Model">The resolved model id.</param>
/// <param name="UsesTier">
/// True when this route differs from the SAME task's route on <see cref="AiTier.Fast"/>, i.e. the tier rung
/// actually fired. False is not a bug: for an ENGLISH book <c>Proofread_en</c> outranks the tier rung (layer
/// E3), so English proofreading stays local on both tiers by design and this flag says so honestly.
/// </param>
/// <param name="ResolvedTier">
/// tier-ux-rework c1: the tier THIS task resolved to FROM STORAGE (per-task override, else book default, else
/// fast), which is no longer the same value for every task on the book. It is what the settings ASK FOR, not
/// necessarily what will run: see <see cref="UsesTier"/> for whether the route actually moved, and
/// <c>BookAiTierTaskDto.EffectiveTier</c> for the wire field that reports what WILL run.
///
/// NAMED "Resolved" AND NOT "Effective" ON PURPOSE (be-c01). Those were one word before, and that is exactly
/// how a book default reached tasks that provably cannot run on it and the toggle highlighted the wrong word.
/// This record is server-internal (c2 removed the routes array from the wire) and the book-level fallback flag
/// below is derived from it, so it must keep the pre-clamp value: derived from a clamped one, "the book asked
/// for thinking and nothing moved" would be unsatisfiable.
/// </param>
public sealed record AiTierRouteInfo(
    string Task, string Provider, string Model, bool UsesTier, AiTier ResolvedTier);

/// <summary>Everything the surface needs to describe a book's tier without lying about it.</summary>
/// <param name="Tier">The book's STORED tier, defensively parsed.</param>
/// <param name="ThinkingReadiness">Whether the thinking tier is usable on this deployment at all.</param>
/// <param name="FallbackActive">
/// True when the BOOK DEFAULT is Thinking but NO route uses the tier, i.e. the book default is silently
/// running on the local models. This is the "fall back visibly, never silently" flag at book level, and it is
/// deliberately scoped to the book default (final-r02): the only surface that renders it is the book-default
/// toggle, so a per-task opt-in that is not being honoured belongs on that TASK's row
/// (<see cref="AiTierTaskStatus.FallbackActive"/>) and not here, where it would warn about a setting the
/// control beside it does not carry.
/// </param>
/// <param name="Routes">Per allowlisted task, the route that will actually run, at the STORED tier.</param>
public sealed record AiTierStatus(
    AiTier Tier,
    AiTierReadiness ThinkingReadiness,
    bool FallbackActive,
    IReadOnlyList<AiTierRouteInfo> Routes);

/// <summary>
/// ONE TASK, DE-IDENTIFIED (tier-ux-rework c2). Everything the per-task toggle needs and NOTHING that names a
/// provider, a model or a version - model identity is internal IP, it changes without notice, and a client
/// that renders it teaches users to reason about a name we may retire next week.
///
/// It is the service, not the controller, that reduces the resolved route to these facts: the (provider,
/// model) pair never leaves this class on a surface path, so re-leaking it takes a deliberate new call rather
/// than one careless <c>Select</c>. Since be-c03 the reduction is total - the route survives only as
/// <see cref="EffectiveTier"/> and <see cref="FallbackActive"/>, which are statements about the SETTING and the
/// RUN rather than about the topology. The previous <c>ProcessingLocation</c> token is gone: no client read it,
/// and it described the task's CURRENT tier, so it could not have grounded the consent copy it was justified by.
/// </summary>
/// <param name="EffectiveTier">
/// THE TIER THAT WILL ACTUALLY ROUTE for this task on this book (be-c01) - CLAMPED, and therefore NOT the same
/// value as the storage resolver's answer. It reads Thinking only when the task's thinking route genuinely
/// differs from its fast route; a task the tier cannot move (outside <see cref="AiTierPolicy.TieredTasks"/>)
/// or whose <c>{task}_{lang}</c> rung outranks the tier rung (an English book's Proofread) reads Fast however
/// the book default is set. It is the value the toggle highlights, so it has to be a statement about the run.
/// </param>
/// <param name="ThinkingReadiness">
/// Whether "thinking" can route FOR THIS TASK, including the two per-task-only verdicts
/// (<see cref="AiTierReadiness.TaskNotEligible"/>, <see cref="AiTierReadiness.LanguageAlwaysFast"/>). Anything
/// other than <see cref="AiTierReadiness.Ready"/> is exactly what the write path answers with a 409.
/// </param>
/// <param name="FallbackActive">
/// "YOU ASKED FOR THINKING AND IT IS NOT MOVING." Derived from the PRE-clamp resolved tier, never from
/// <paramref name="EffectiveTier"/> - derived from the clamped value this flag would be unsatisfiable, which
/// would trade the loud lie be-c01 fixed for a silent one. Deliberately silent in the two states where the
/// book default was never a request this task could honour and the readiness reason says so instead; an
/// explicit stored "thinking" re-opens it, so an opt-in left dormant by a later language change still shows.
/// </param>
public sealed record AiTierTaskStatus(
    AiTaskType Task,
    AiTier EffectiveTier,
    AiTierReadiness ThinkingReadiness,
    bool FallbackActive);

/// <summary>
/// Answers "which model will actually run for this book, and can the thinking tier run at all?" - the
/// pre-flight behind the tier control (model-tier-fast-thinking plan, p3-4).
///
/// IT RESOLVES THROUGH <see cref="LinguisticModelResolver"/>, NOT THROUGH A COPY. That is the whole
/// contract: RULE 0 for the tier is "the model that RAN is the model the UI said would run", and the only
/// way to guarantee that structurally is for the UI's answer and the router's answer to come out of the same
/// function. <see cref="AiRouter.ResolveSelection"/> delegates to that resolver too (p3-2), so the three
/// surfaces - what the UI promises, what the staleness gate assumes, and what the provider is asked for -
/// are one implementation.
///
/// WHAT IT DELIBERATELY DOES NOT DO: it never calls the provider. A GET that describes a setting must not
/// spend money or hang on a third-party network round trip, so "usable" is a CONFIGURATION fact
/// (registered + has credentials), not a liveness probe. The two failure modes it cannot see are stated
/// where the user can act on them:
///   • a cloud key that is present but invalid/out of credit - the run fails loudly with the provider's own
///     error, which is the existing behaviour and is not silent;
///   • the LOCAL <c>_comment_ProofreadFallback</c> hazard, where an unpulled Ollama model 404s and
///     OllamaProvider silently retries DefaultModel. That is a pre-existing hazard on the FAST tier and is
///     out of this todo's scope, but note it CANNOT be inherited by the thinking tier as configured: the
///     tier routes to OpenRouter, whose provider throws rather than substituting a model.
/// </summary>
public class AiTierStatusService
{
    private readonly IOptions<AiOptions> _options;
    private readonly IConfiguration _config;
    private readonly IReadOnlyDictionary<string, IAiAnalysisProvider> _providers;

    public AiTierStatusService(
        IOptions<AiOptions> options,
        IConfiguration config,
        IReadOnlyDictionary<string, IAiAnalysisProvider> providers)
    {
        _options = options;
        _config = config;
        _providers = providers;
    }

    /// <summary>
    /// Whether the UI must render an explicit consent step before a book opts a task into the thinking tier
    /// (<c>Ai:Tier:ConsentRequired</c>, tier-ux-rework c2). Surfaced on the tier DTO so the client never
    /// hardcodes either deployment topology.
    ///
    /// IT IS NOT AN AUTHORIZATION GATE. The 409 on an unroutable "thinking" request is unconditional and
    /// completely independent of this flag - consent is a UI step, and a server that stopped enforcing
    /// because a rendering flag said a dialog would appear would have handed a security decision to the
    /// client. See <see cref="AiTierOptions.ConsentRequired"/> for why the answer is deployment-shaped.
    /// </summary>
    public bool ConsentRequired => _options.Value.Tier?.ConsentRequired ?? true;

    /// <summary>
    /// Describes a book's tier. <paramref name="language"/> is the BOOK's language and it matters: it selects
    /// the <c>{task}_{lang}</c> rung, which outranks the tier rung, so an English book's Proofread route is
    /// local on both tiers.
    /// </summary>
    public AiTierStatus Describe(AiTier storedTier, string? language)
        => Describe(_ => storedTier, storedTier, language);

    /// <summary>
    /// The PER-TASK form (tier-ux-rework c1). <paramref name="tierForTask"/> is the resolved tier for one
    /// task - in production, <c>BookAiTierResolver</c>'s answer, so the surface and the run agree per task
    /// rather than only per book. <paramref name="bookDefaultTier"/> is the book-level seed, reported as
    /// <see cref="AiTierStatus.Tier"/>.
    /// </summary>
    public AiTierStatus Describe(Func<AiTaskType, AiTier> tierForTask, AiTier bookDefaultTier, string? language)
    {
        var opt = _options.Value;
        var tasks = AiTierPolicy.TieredTasks.OrderBy(t => t.ToString(), StringComparer.Ordinal).ToList();

        var routes = new List<AiTierRouteInfo>(tasks.Count);
        foreach (var task in tasks)
        {
            var resolvedTier = tierForTask(task);
            var (provider, model) = LinguisticModelResolver.ResolveForTask(opt, task, language, resolvedTier);
            var (fastProvider, fastModel) = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Fast);
            // Provider names compare OrdinalIgnoreCase because the provider registry itself is
            // case-insensitive (AiProviderRegistry) - "ollama" and "Ollama" are the SAME provider and route
            // identically, so a mere casing difference must not be reported as the tier having moved. The
            // model half stays Ordinal: an Ollama model tag is case-sensitive on the wire, so two different
            // spellings really are two different models.
            var usesTier = resolvedTier == AiTier.Thinking
                && (!string.Equals(provider, fastProvider, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(model, fastModel, StringComparison.Ordinal));
            routes.Add(new AiTierRouteInfo(task.ToString(), provider ?? "", model ?? "", usesTier, resolvedTier));
        }

        var readiness = EvaluateThinkingReadiness(opt, language, tasks);
        // "THE BOOK DEFAULT asked for thinking and NOTHING moved." This is the BOOK-LEVEL flag, and the only
        // thing that renders it is the book-DEFAULT toggle, whose highlighted word is `bookDefaultTier` - so
        // it may not be true about a setting that control does not own (final-r02). Without the first clause a
        // single per-task opt-in whose route an operator later removed made this true on a book whose default
        // is "fast", and the book-default row then rendered "this is set to thinking, but it is running fast"
        // beside a highlighted "Fast" pill: the same one-control-three-statements defect be-c01 fixed per
        // task. The per-task opt-in is NOT lost by the clause - it is reported on its OWN row, where the
        // stored "thinking" that justifies the sentence actually lives.
        //
        // Identical to the pre-per-task definition when every task shares one tier (the single-tier overload
        // above, where bookDefaultTier IS every task's tier), and it makes the wire contract's own wording
        // ("the book stores thinking but no route uses the tier" - BookAiTierDto.FallbackActive) true rather
        // than approximately true.
        //
        // IT READS THE RESOLVED (PRE-CLAMP) TIER AND MUST KEEP DOING SO (be-c01). The per-task DTO field now
        // reports the tier that will actually route, and "asked for thinking" is by definition a question
        // about what was asked, not about what ran - against a clamped value this expression is unsatisfiable.
        var fallbackActive = bookDefaultTier == AiTier.Thinking
            && routes.Any(r => r.ResolvedTier == AiTier.Thinking)
            && routes.All(r => !r.UsesTier);

        return new AiTierStatus(bookDefaultTier, readiness, fallbackActive, routes);
    }

    /// <summary>
    /// Whether the thinking tier is usable at all, evaluated INDEPENDENTLY of what the book currently stores
    /// so the control can enable/disable the option before anybody opts in.
    /// </summary>
    public AiTierReadiness EvaluateThinkingReadiness(string? language)
        => EvaluateThinkingReadiness(
            _options.Value,
            language,
            AiTierPolicy.TieredTasks.OrderBy(t => t.ToString(), StringComparer.Ordinal).ToList());

    /// <summary>
    /// Readiness for ONE task (tier-ux-rework c1), which is what a per-task opt-in has to be judged against:
    /// the deployment-wide verdict says the tier works SOMEWHERE, and a per-task PUT needs to know whether it
    /// works HERE.
    ///
    /// THE THREE "IT WILL NOT MOVE" CASES ARE NOW DISTINCT (c2). c1 collapsed all of them into
    /// <see cref="AiTierReadiness.RouteNotConfigured"/>, which is true at the routing level and useless at the
    /// copy level - the client renders a disabled-with-reason state from this token, and the three have
    /// nothing in common for a reader:
    ///   • <see cref="AiTierReadiness.TaskNotEligible"/> - the task is outside
    ///     <see cref="AiTierPolicy.TieredTasks"/> (LineEdit, BookReview). Permanent, nothing to fix.
    ///   • <see cref="AiTierReadiness.LanguageAlwaysFast"/> - the <c>{task}_{lang}</c> rung wins and outranks
    ///     the tier rung (an English book's Proofread; layer E3, the p2-4 NO-GO). Changing the deployment's
    ///     config does not change this; changing the book's LANGUAGE does.
    ///   • <see cref="AiTierReadiness.RouteNotConfigured"/> - the <c>{task}_thinking</c> key is absent or
    ///     half-configured. This is the operator kill-switch, and an operator can undo it.
    /// ORDER MATTERS and is stated here rather than left to fall out of the code: eligibility is checked
    /// first, then the language rung, then configuration. A task can satisfy more than one at once (an
    /// English Proofread on a kill-switched deployment satisfies the last two), and the earlier verdict is
    /// the more FUNDAMENTAL one - it would still hold after the later cause was removed.
    ///
    /// The 409 semantics are unchanged by the split: anything other than <see cref="AiTierReadiness.Ready"/>
    /// still refuses the write.
    /// </summary>
    public AiTierReadiness EvaluateThinkingReadiness(string? language, AiTaskType task)
    {
        var opt = _options.Value;

        if (!AiTierPolicy.IsTiered(task)) return AiTierReadiness.TaskNotEligible;
        if (LinguisticModelResolver.LanguageRungWins(opt, task, language)) return AiTierReadiness.LanguageAlwaysFast;

        return EvaluateThinkingReadiness(opt, language, new[] { task });
    }

    /// <summary>
    /// THE DE-IDENTIFIED PER-TASK READ MODEL (tier-ux-rework c2) - what the surface is handed instead of a
    /// (provider, model) pair.
    ///
    /// The route IS resolved here, through the same <see cref="LinguisticModelResolver"/> the router uses -
    /// the Rule 0 guarantee is untouched - it is simply REDUCED to a moved/not-moved judgement before it
    /// leaves the method. Losing the model name on the wire does not mean losing the resolution behind it.
    ///
    /// IT CLAMPS (be-c01), and the clamp is the whole point of the method now. What arrives is the STORAGE
    /// answer; what leaves is what will ACTUALLY RUN. Passing the storage answer through untouched is how a
    /// single flip of the book default made BookReview, LineEdit and an English book's Proofread all report
    /// <c>effectiveTier: "thinking"</c> while their own write path answers 409 - the toggle then highlighted
    /// "thinking" next to a reason line saying the task always runs fast.
    /// </summary>
    /// <param name="resolvedTier">
    /// <c>BookAiTierResolver</c>'s answer for this (book, task): the per-task override, else the book default,
    /// else fast. WHAT THE SETTINGS ASK FOR, which is not the same question as what will run.
    /// </param>
    /// <param name="storedTier">
    /// This task's OWN override, or null when it inherits the book default. It is needed for one thing only:
    /// telling "the user opted THIS TASK into thinking and it is not being honoured" apart from "an unrelated
    /// book default washed over a task that could never honour it". Those are the same resolved tier and very
    /// different sentences, and only the first is a fallback worth warning about.
    /// </param>
    public AiTierTaskStatus DescribeTask(AiTaskType task, AiTier resolvedTier, AiTier? storedTier, string? language)
    {
        var opt = _options.Value;
        var (provider, model) = LinguisticModelResolver.ResolveForTask(opt, task, language, resolvedTier);
        var (fastProvider, fastModel) = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Fast);

        // Same OrdinalIgnoreCase-provider / Ordinal-model split as Describe(): the registry treats "ollama"
        // and "Ollama" as one provider, so a casing difference is not the tier having moved.
        var usesTier = resolvedTier == AiTier.Thinking
            && (!string.Equals(provider, fastProvider, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(model, fastModel, StringComparison.Ordinal));

        // THE CLAMP. No eligibility test is needed beside it and none may be added: a task outside
        // TieredTasks has no tier rung at all (AiTierPolicy.TierKeyFor returns null, so both tiers resolve one
        // route) and the {task}_{lang} rung outranks the tier rung, so BOTH per-task "always fast" verdicts
        // already make usesTier false. Asking the route rather than re-deriving the policy is what keeps this
        // from becoming a second copy of the precedence.
        var effectiveTier = usesTier ? AiTier.Thinking : AiTier.Fast;

        var readiness = EvaluateThinkingReadiness(language, task);

        // The two verdicts that mean "this task stays fast whatever anyone stores": the book default is not a
        // request they could ever honour, so inheriting it is not a fallback, it is the default not applying.
        // The readiness reason line already says so, and adding "you set thinking, it runs fast" beside it
        // states something no control on screen claims. An EXPLICIT stored thinking is different - the user
        // really did ask for this task (a Hebrew Proofread opt-in that a later language change left dormant) -
        // so it re-opens the warning.
        var permanentlyFast = readiness is AiTierReadiness.TaskNotEligible or AiTierReadiness.LanguageAlwaysFast;
        var fallbackActive = resolvedTier == AiTier.Thinking
            && !usesTier
            && (storedTier == AiTier.Thinking || !permanentlyFast);

        return new AiTierTaskStatus(
            task,
            effectiveTier,
            readiness,
            FallbackActive: fallbackActive);
    }

    private AiTierReadiness EvaluateThinkingReadiness(AiOptions opt, string? language, IReadOnlyList<AiTaskType> tasks)
    {
        // Which providers would the thinking tier actually send this book's text to? Only the routes that
        // MOVE count: a task whose thinking route equals its fast route (English Proofread, or any task once
        // the kill-switch has removed its key) is not evidence that the tier works.
        var movedProviders = new List<string>();
        foreach (var task in tasks)
        {
            var (provider, model) = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Thinking);
            var (fastProvider, fastModel) = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Fast);
            // Same OrdinalIgnoreCase-provider / Ordinal-model split as Describe() above: the registry treats
            // "ollama" and "Ollama" as one provider, so only a genuine provider change - not a casing
            // difference - counts as evidence the tier moved.
            if (string.Equals(provider, fastProvider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(model, fastModel, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(provider))
                movedProviders.Add(provider);
        }

        if (movedProviders.Count == 0) return AiTierReadiness.RouteNotConfigured;

        foreach (var provider in movedProviders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_providers.ContainsKey(provider)) return AiTierReadiness.ProviderNotRegistered;

            // The local provider needs no credentials. Every other provider in the registry is a remote one
            // that authenticates with a key, and a missing key is the single most likely real-world cause of
            // "I chose thinking and nothing worked".
            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase)) continue;

            if (!ProviderCredentials.HasApiKey(_config, provider, includeLegacySection: true))
                return AiTierReadiness.ProviderCredentialsMissing;
        }

        return AiTierReadiness.Ready;
    }
}
