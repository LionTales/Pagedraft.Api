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
    ProviderCredentialsMissing = 3
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
public sealed record AiTierRouteInfo(string Task, string Provider, string Model, bool UsesTier);

/// <summary>Everything the surface needs to describe a book's tier without lying about it.</summary>
/// <param name="Tier">The book's STORED tier, defensively parsed.</param>
/// <param name="ThinkingReadiness">Whether the thinking tier is usable on this deployment at all.</param>
/// <param name="FallbackActive">
/// True when the book is stored as Thinking but NO route uses the tier, i.e. the book is silently running on
/// the local models. This is the "fall back visibly, never silently" flag.
/// </param>
/// <param name="Routes">Per allowlisted task, the route that will actually run, at the STORED tier.</param>
public sealed record AiTierStatus(
    AiTier Tier,
    AiTierReadiness ThinkingReadiness,
    bool FallbackActive,
    IReadOnlyList<AiTierRouteInfo> Routes);

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
    /// Describes a book's tier. <paramref name="language"/> is the BOOK's language and it matters: it selects
    /// the <c>{task}_{lang}</c> rung, which outranks the tier rung, so an English book's Proofread route is
    /// local on both tiers.
    /// </summary>
    public AiTierStatus Describe(AiTier storedTier, string? language)
    {
        var opt = _options.Value;
        var tasks = AiTierPolicy.TieredTasks.OrderBy(t => t.ToString(), StringComparer.Ordinal).ToList();

        var routes = new List<AiTierRouteInfo>(tasks.Count);
        foreach (var task in tasks)
        {
            var (provider, model) = LinguisticModelResolver.ResolveForTask(opt, task, language, storedTier);
            var (fastProvider, fastModel) = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Fast);
            // Provider names compare OrdinalIgnoreCase because the provider registry itself is
            // case-insensitive (AiProviderRegistry) - "ollama" and "Ollama" are the SAME provider and route
            // identically, so a mere casing difference must not be reported as the tier having moved. The
            // model half stays Ordinal: an Ollama model tag is case-sensitive on the wire, so two different
            // spellings really are two different models.
            var usesTier = storedTier == AiTier.Thinking
                && (!string.Equals(provider, fastProvider, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(model, fastModel, StringComparison.Ordinal));
            routes.Add(new AiTierRouteInfo(task.ToString(), provider ?? "", model ?? "", usesTier));
        }

        var readiness = EvaluateThinkingReadiness(opt, language, tasks);
        var fallbackActive = storedTier == AiTier.Thinking && routes.All(r => !r.UsesTier);

        return new AiTierStatus(storedTier, readiness, fallbackActive, routes);
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
