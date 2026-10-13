using System;
using Microsoft.Extensions.DependencyInjection;
using Pagedraft.Api.Services.Ai;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The ONE place the live LanguageEngine harnesses register their HTTP clients (be-f02).
///
/// WHY. After c19 the four cloud providers resolve <c>CreateClient("AiProvider:&lt;name&gt;")</c> (see
/// <see cref="AiProviderHttpClients"/>), not the unnamed client. A harness that only registered the unnamed
/// client with a long timeout therefore handed every cloud call .NET's 100 s default, the very timeout c19
/// removed from production. Routing every harness through this helper reuses the PRODUCTION registration
/// (<see cref="AiProviderHttpClients.AddCloudAiProviderHttpClients"/>) and then widens each cloud client to the
/// harness timeout. The unnamed client and the "Ollama" client are kept exactly as the harnesses had them.
///
/// The production registration reads <c>IConfiguration</c> from the provider, so the caller must have
/// registered one (every harness does).
/// </summary>
public static class HarnessHttpClients
{
    public static IServiceCollection AddHarnessHttpClients(this IServiceCollection services, TimeSpan timeout)
    {
        services.AddHttpClient("Ollama", c => c.Timeout = timeout);
        services.AddHttpClient(string.Empty, c => c.Timeout = timeout);

        services.AddCloudAiProviderHttpClients();
        foreach (var providerName in AiProviderHttpClients.CloudProviderNames)
        {
            // Registered after the production configure action, so it wins.
            services.AddHttpClient(AiProviderHttpClients.ClientName(providerName))
                .ConfigureHttpClient(c => c.Timeout = timeout);
        }

        return services;
    }
}
