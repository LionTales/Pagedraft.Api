using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Pagedraft.Api.Services.Ai;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-f03: the Ollama (minutes) and cloud (seconds) timeout keys share ONE resolver, so a bad value behaves the
/// same for both: default plus a warning, never a throw at client creation.
/// </summary>
public class ProviderTimeoutResolverTests
{
    private const string OllamaKey = "Ai:Providers:Ollama:TimeoutMinutes";
    private const string CloudKey = "Ai:Providers:OpenRouter:TimeoutSeconds";

    private static IConfiguration Cfg(string key, string? value)
    {
        var d = new Dictionary<string, string?>();
        if (value is not null) d[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static TimeSpan Ollama(string? v, out string? w)
        => ProviderTimeoutResolver.Resolve(Cfg(OllamaKey, v), OllamaKey, TimeSpan.FromMinutes(1), 30, out w);

    private static TimeSpan Cloud(string? v, out string? w)
        => ProviderTimeoutResolver.Resolve(Cfg(CloudKey, v), CloudKey, TimeSpan.FromSeconds(1), AiProviderHttpClients.DefaultCloudTimeoutSeconds, out w);

    [Fact]
    public void Valid_value_is_used_as_written_for_both_providers()
    {
        Assert.Equal(TimeSpan.FromMinutes(45), Ollama("45", out var w1));
        Assert.Null(w1);
        Assert.Equal(TimeSpan.FromSeconds(450), Cloud("450", out var w2));
        Assert.Null(w2);
    }

    [Fact]
    public void Absent_value_gives_the_documented_default_without_a_warning()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), Ollama(null, out var w1));
        Assert.Null(w1);
        Assert.Equal(TimeSpan.FromSeconds(300), Cloud(null, out var w2));
        Assert.Null(w2);
        Assert.Equal(TimeSpan.FromSeconds(300), AiProviderHttpClients.ResolveTimeout(Cfg(CloudKey, null), "OpenRouter"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999")]
    public void Empty_or_garbage_falls_back_to_the_default_and_warns_naming_the_key(string raw)
    {
        Assert.Equal(TimeSpan.FromMinutes(30), Ollama(raw, out var w1));
        Assert.Contains(OllamaKey, w1);
        Assert.Equal(TimeSpan.FromSeconds(300), Cloud(raw, out var w2));
        Assert.Contains(CloudKey, w2);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Zero_or_negative_falls_back_to_the_default_and_warns(string raw)
    {
        Assert.Equal(TimeSpan.FromMinutes(30), Ollama(raw, out var w1));
        Assert.Contains(OllamaKey, w1);
        Assert.Equal(TimeSpan.FromSeconds(300), Cloud(raw, out var w2));
        Assert.Contains(CloudKey, w2);
    }

    [Fact]
    public void Too_large_is_clamped_to_24_hours_and_warns_for_both_providers()
    {
        Assert.Equal(TimeSpan.FromHours(24), Ollama("1441", out var w1));
        Assert.Contains(OllamaKey, w1);
        Assert.Equal(TimeSpan.FromHours(24), Cloud("86401", out var w2));
        Assert.Contains(CloudKey, w2);
        Assert.Equal(TimeSpan.FromHours(24), Ollama("1440", out var w3));
        Assert.Null(w3);
        Assert.Equal(TimeSpan.FromHours(24), AiProviderHttpClients.ResolveTimeout(Cfg(CloudKey, "2147483647"), "OpenRouter"));
    }
}
