using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pagedraft.Api.Services.Ai;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-f02: the live harnesses must give the four NAMED cloud clients (what the providers resolve since c19) a
/// generous timeout, not .NET's 100 s default. Deterministic: no network, no model. Lives OUTSIDE the
/// LanguageEngine namespace so the standing filter runs it.
/// </summary>
public class HarnessHttpClientTests
{
    private static readonly TimeSpan Default100s = TimeSpan.FromSeconds(100);

    private static ServiceProvider Build(TimeSpan timeout)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddHarnessHttpClients(timeout);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Azure")]
    [InlineData("Anthropic")]
    [InlineData("OpenRouter")]
    public void Harness_named_cloud_client_timeout_exceeds_the_100s_default(string providerName)
    {
        using var sp = Build(TimeSpan.FromMinutes(10));
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(AiProviderHttpClients.ClientName(providerName));
        Assert.True(client.Timeout > Default100s, $"{providerName} client timeout was {client.Timeout}");
        Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
    }

    [Fact]
    public void Harness_covers_every_cloud_provider_and_keeps_ollama_and_unnamed_clients()
    {
        using var sp = Build(TimeSpan.FromMinutes(10));
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        foreach (var name in AiProviderHttpClients.CloudProviderNames)
            Assert.True(factory.CreateClient(AiProviderHttpClients.ClientName(name)).Timeout > Default100s, name);
        Assert.Equal(TimeSpan.FromMinutes(10), factory.CreateClient("Ollama").Timeout);
        Assert.Equal(TimeSpan.FromMinutes(10), factory.CreateClient().Timeout);
    }

    [Fact]
    public void Every_LanguageEngine_harness_registers_its_clients_through_the_helper()
    {
        var dir = FindLanguageEngineDir();
        var offenders = new System.Collections.Generic.List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            var buildsProviders = text.Contains("Configure<AiOptions>") || text.Contains("IAiAnalysisProvider>>");
            // HebrewRegressionTests wires only Ollama (2 min) + LanguageTool, no cloud provider.
            if (buildsProviders && name != "HebrewRegressionTests.cs" && !text.Contains("AddHarnessHttpClients("))
                offenders.Add(name + ": builds providers without AddHarnessHttpClients");
            if (text.Contains("AddHttpClient(string.Empty"))
                offenders.Add(name + ": registers the unnamed client directly");
        }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static string FindLanguageEngineDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var candidate = Path.Combine(d.FullName, "LanguageEngine");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.cs").Any())
                return candidate;
            var nested = Path.Combine(d.FullName, "Pagedraft.Api.Tests", "LanguageEngine");
            if (Directory.Exists(nested))
                return nested;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("LanguageEngine source folder not found above " + AppContext.BaseDirectory);
    }
}
