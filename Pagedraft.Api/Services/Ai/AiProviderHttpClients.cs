namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// The NAMED <see cref="HttpClient"/> each cloud provider sends through, and the timeout each one carries.
///
/// WHY THIS EXISTS (c19, measured 2026-10-12). Before it, <see cref="OpenAiProvider"/>,
/// <see cref="AzureOpenAiProvider"/>, <see cref="AnthropicProvider"/> and <see cref="OpenAiCompatibleProvider"/>
/// called <c>IHttpClientFactory.CreateClient()</c> UNNAMED and so inherited .NET's default 100-second
/// <see cref="HttpClient.Timeout"/>, while only Ollama had a named client with a configurable ceiling. A
/// thinking-tier model on OpenRouter can spend more than 100 s on one proofread chunk; the owner's forced
/// whole-book run lost 10 of 55 chapters to exactly that timeout.
///
/// The idiom mirrors the Ollama client in Program.cs: a generous-but-bounded CEILING, not a target, read from
/// <c>Ai:Providers:{provider}:TimeoutSeconds</c> (default <see cref="DefaultCloudTimeoutSeconds"/>; zero,
/// negative or absent falls back to the default, so "no timeout" is not expressible by accident). The value is
/// read once per client creation by the factory, so an edit to appsettings takes effect on the next request.
///
/// A NEW cloud provider must be added to <see cref="CloudProviderNames"/> or it silently gets the 100 s default
/// again; <c>AiProviderHttpClientsTests</c> enumerates <see cref="AiProviderRegistry"/> and fails if one is missing.
/// </summary>
public static class AiProviderHttpClients
{
    /// <summary>The per-request ceiling a cloud provider gets when its <c>TimeoutSeconds</c> is unset or not positive.</summary>
    public const int DefaultCloudTimeoutSeconds = 300;

    /// <summary>
    /// Every registered provider that sends through a cloud client (all of <see cref="AiProviderRegistry"/> except
    /// Ollama, which keeps its own <c>"Ollama"</c> client and <c>TimeoutMinutes</c> key).
    /// </summary>
    public static readonly IReadOnlyList<string> CloudProviderNames = new[] { "OpenAI", "Azure", "Anthropic", "OpenRouter" };

    /// <summary>The named-client key a provider passes to <c>IHttpClientFactory.CreateClient</c>.</summary>
    public static string ClientName(string providerName) => "AiProvider:" + providerName;

    /// <summary>
    /// The configured ceiling for <paramref name="providerName"/>: <c>Ai:Providers:{provider}:TimeoutSeconds</c>,
    /// or <see cref="DefaultCloudTimeoutSeconds"/> when it is unparsable or not positive (see <see cref="ProviderTimeoutResolver"/> for the shared policy and clamp).
    /// </summary>
    public static TimeSpan ResolveTimeout(IConfiguration config, string providerName, ILogger? logger = null)
        => ProviderTimeoutResolver.ResolveAndLog(
            config, $"Ai:Providers:{providerName}:TimeoutSeconds", TimeSpan.FromSeconds(1), DefaultCloudTimeoutSeconds, logger);

    /// <summary>Registers one named client per <see cref="CloudProviderNames"/> entry with its configured timeout.</summary>
    public static IServiceCollection AddCloudAiProviderHttpClients(this IServiceCollection services)
    {
        foreach (var providerName in CloudProviderNames)
        {
            var name = providerName; // captured per iteration
            services.AddHttpClient(ClientName(name), (sp, client) =>
                client.Timeout = ResolveTimeout(sp.GetRequiredService<IConfiguration>(), name,
                sp.GetService<ILoggerFactory>()?.CreateLogger("ProviderTimeout")));
        }

        return services;
    }
}
