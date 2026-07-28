using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// OllamaProvider silently retries with Ai:DefaultModel when the requested model returns 404.
/// That is deliberate behavior, but silently swallowing it meant a typo in Ai:FeatureModels ran a whole
/// task on the WRONG model with no signal anywhere (a fault-injection attempt with a bogus model name
/// returned a plausible result from the default model instead of failing). These tests pin that BOTH
/// fallback sites (CompleteAsync and StreamCompleteAsync) emit a WARNING naming the requested model and
/// the substituted default, and that the fallback behavior itself is unchanged.
/// </summary>
public class OllamaProviderFallbackLoggingTests
{
    private const string RequestedModel = "typo-model:12b";
    private const string DefaultModel = "default-model:9b";

    /// <summary>Returns 404 for the first request and a canned success for every subsequent one.</summary>
    private sealed class NotFoundThenOkHandler : HttpMessageHandler
    {
        private int _calls;
        private readonly string _body;

        public NotFoundThenOkHandler(string body) => _body = body;

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var n = Interlocked.Increment(ref _calls);
            return n == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        // A fresh HttpClient per call: OllamaProvider assigns BaseAddress after CreateClient, which throws
        // once a client has already sent a request.
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class CapturingLogger : ILogger<OllamaProvider>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (OllamaProvider Provider, CapturingLogger Logger, NotFoundThenOkHandler Handler) Build(string responseBody)
    {
        var handler = new NotFoundThenOkHandler(responseBody);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Providers:Ollama:BaseUrl"] = "http://localhost:11434",
                ["Ai:Providers:Ollama:DefaultModel"] = DefaultModel
            })
            .Build();
        var logger = new CapturingLogger();
        var provider = new OllamaProvider(new StubHttpClientFactory(handler), config, Options.Create(new AiOptions()), logger);
        return (provider, logger, handler);
    }

    private static ResolvedAiRequest Request() => new()
    {
        SystemMessage = "sys",
        Instruction = "INSTRUCTION_SENTINEL",
        InputText = "BOOK_CONTENT_SENTINEL",
        Selection = new AiModelSelection { Provider = "Ollama", Model = RequestedModel },
        TaskType = AiTaskType.Proofread
    };

    private static void AssertFallbackWarning(CapturingLogger logger)
    {
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        var fallback = Assert.Single(warnings, m => m.Contains(RequestedModel, StringComparison.Ordinal));
        // Both model names must appear, so the operator can see WHAT was asked for and WHAT actually ran.
        Assert.Contains(DefaultModel, fallback, StringComparison.Ordinal);
        // And the consequence must be explicit, pointing at the likely cause.
        Assert.Contains("Ai:FeatureModels", fallback, StringComparison.Ordinal);
        // Never log prompt/input/output content.
        Assert.DoesNotContain("INSTRUCTION_SENTINEL", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("BOOK_CONTENT_SENTINEL", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_WhenRequestedModelIs404_LogsWarningNamingBothModels()
    {
        var (provider, logger, handler) = Build("{\"response\":\"ok\"}");

        var result = await provider.CompleteAsync(Request());

        AssertFallbackWarning(logger);

        // Behavior unchanged: the retry still happens and still reports the substituted model.
        Assert.Equal("ok", result.Content);
        Assert.Equal(DefaultModel, result.Model);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains(RequestedModel, handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains(DefaultModel, handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompleteAsync_WhenRequestedModelIs404_LogsWarningNamingBothModels()
    {
        var (provider, logger, handler) = Build("{\"response\":\"ok\"}\n");

        var tokens = new List<string>();
        await foreach (var token in provider.StreamCompleteAsync(Request()))
            tokens.Add(token);

        AssertFallbackWarning(logger);

        // Behavior unchanged: the retried stream is still consumed and yielded.
        Assert.Equal(new[] { "ok" }, tokens);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains(RequestedModel, handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains(DefaultModel, handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_WhenModelResolves_LogsNoFallbackWarning()
    {
        // No 404 on the first call: the happy path must stay silent.
        var okHandler = new AlwaysOkHandler("{\"response\":\"ok\"}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Providers:Ollama:DefaultModel"] = DefaultModel
            })
            .Build();
        var logger = new CapturingLogger();
        var provider = new OllamaProvider(new StubHttpClientFactory(okHandler), config, Options.Create(new AiOptions()), logger);

        await provider.CompleteAsync(Request());

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class AlwaysOkHandler : HttpMessageHandler
    {
        private readonly string _body;
        public AlwaysOkHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    }
}
