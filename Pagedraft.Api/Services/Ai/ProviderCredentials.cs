namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// THE one implementation of "does this provider have a usable API key, and what is it?"
/// (model-tier-fast-thinking plan, p3-4).
///
/// WHY IT IS SHARED RATHER THAN INLINE. The model tier's UI has to tell the user, BEFORE a run, whether
/// choosing the thinking tier will actually reach the cloud provider or silently do something else. That
/// answer is only trustworthy if the pre-flight check reads the key exactly the way the provider that makes
/// the call reads it - a second, slightly different copy would let the UI say "ready" while the run throws
/// (or the reverse, which is worse: the UI blocks a tier that would have worked). So
/// <see cref="OpenAiCompatibleProvider"/> and <see cref="AiTierStatusService"/> call THIS.
///
/// THE PLACEHOLDER RUNG IS THE POINT. <c>appsettings.json</c> commits <c>"__AI_OPENROUTER_APIKEY__"</c> as a
/// deployment-substitution marker. It is NON-EMPTY, so a naive null/empty check treats it as a configured
/// key and sends it verbatim as the Bearer token, which the provider rejects with a 401 that reads like a
/// bad key rather than a missing one. Anything of the shape <c>__NAME__</c> is therefore treated as absent.
/// </summary>
public static class ProviderCredentials
{
    /// <summary>
    /// Resolves a provider's API key, or null when none is configured.
    ///
    /// Rungs: <c>Ai:Providers:{name}:ApiKey</c>, then (only when
    /// <paramref name="includeLegacySection"/>) the older <c>Ai:{name}:ApiKey</c> spelling that
    /// <see cref="OpenAiProvider"/> / <see cref="AzureOpenAiProvider"/> / <see cref="AnthropicProvider"/>
    /// still accept, then the environment variable <c>AI_{NAME}_APIKEY</c>. A committed
    /// <c>__PLACEHOLDER__</c> value counts as absent at every CONFIG rung.
    ///
    /// <paramref name="includeLegacySection"/> defaults to FALSE so that
    /// <see cref="OpenAiCompatibleProvider"/>'s behaviour is byte-identical to its pre-extraction inline
    /// copy (it never consulted the legacy section). The tier's pre-flight check passes true, because it
    /// must not report a provider as unusable on the strength of a rung that provider would have accepted.
    ///
    /// THE RESOLVED VALUE IS TRIMMED AT EVERY RUNG - config, legacy config, and environment variable alike.
    /// A key pasted with a trailing newline or space is a common user-secrets / .env artifact; sending it
    /// untrimmed as a Bearer token fails as a malformed HTTP header, which is a worse diagnostic than the
    /// "bad key" 401 a trimmed-but-wrong key produces.
    /// </summary>
    public static string? ResolveApiKey(IConfiguration config, string providerName, bool includeLegacySection = false)
    {
        if (config == null || string.IsNullOrWhiteSpace(providerName)) return null;

        var fromProviders = Clean(config[$"Ai:Providers:{providerName}:ApiKey"]);
        if (fromProviders != null) return fromProviders;

        if (includeLegacySection)
        {
            var fromLegacy = Clean(config[$"Ai:{providerName}:ApiKey"]);
            if (fromLegacy != null) return fromLegacy;
        }

        var fromEnv = Environment.GetEnvironmentVariable($"AI_{providerName.ToUpperInvariant()}_APIKEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
    }

    /// <summary>True when <see cref="ResolveApiKey"/> finds a key. Convenience for pre-flight checks.</summary>
    public static bool HasApiKey(IConfiguration config, string providerName, bool includeLegacySection = false)
        => !string.IsNullOrWhiteSpace(ResolveApiKey(config, providerName, includeLegacySection));

    /// <summary>
    /// Null for blank values AND for an uninterpolated <c>__PLACEHOLDER__</c>; the TRIMMED value otherwise.
    /// Every rung of <see cref="ResolveApiKey"/> normalizes (trims) the value it returns, so a key pasted
    /// with a trailing newline or space (a common user-secrets / .env artifact) never reaches
    /// <c>OpenAiCompatibleProvider</c> untrimmed - an untrimmed key fails as a malformed HTTP header, which
    /// is a worse diagnostic than the "bad key" 401 a trimmed-but-wrong key produces.
    /// </summary>
    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length >= 4 && trimmed.StartsWith("__", StringComparison.Ordinal) && trimmed.EndsWith("__", StringComparison.Ordinal))
            return null;
        return trimmed;
    }
}
