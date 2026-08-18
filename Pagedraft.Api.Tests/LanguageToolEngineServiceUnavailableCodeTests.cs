using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.LanguageEngine.Detect;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Pins the machine-readable ServiceUnavailableCode LanguageToolEngine attaches alongside its existing
/// English ServiceUnavailableMessage, one case per code, so a client can localize instead of rendering
/// the English sentence verbatim (issue-panel.component.ts previously banner'd the raw message even on
/// a Hebrew UI). Deterministic and network-free: every HTTP call is stubbed, so this class stays in the
/// standing filtered suite (it does NOT touch LanguageTool at localhost:8081, unlike
/// Pagedraft.Api.Tests.LanguageEngine.HebrewRegressionTests).
/// </summary>
public class LanguageToolEngineServiceUnavailableCodeTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _makeException;
        public ThrowingHandler(Func<Exception> makeException) => _makeException = makeException;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _makeException();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost:8081")
        };
    }

    private static LanguageToolEngine Build(HttpMessageHandler handler, bool enabled = true)
    {
        var options = Options.Create(new LanguageToolOptions { Enabled = enabled, ServerUrl = "http://localhost:8081" });
        return new LanguageToolEngine(new StubHttpClientFactory(handler), options, NullLogger<LanguageToolEngine>.Instance);
    }

    [Fact]
    public async Task Disabled_ReturnsDisabledCode()
    {
        // No HTTP call is made when disabled, so a handler that throws proves it was never touched.
        var engine = Build(new ThrowingHandler(() => new InvalidOperationException("should not be called")), enabled: false);

        var result = await engine.DetectAsync("some text", "he");

        Assert.True(result.ServiceUnavailable);
        Assert.Equal(LanguageToolEngine.Codes.Disabled, result.ServiceUnavailableCode);
        Assert.False(string.IsNullOrEmpty(result.ServiceUnavailableMessage));
    }

    [Fact]
    public async Task HebrewUnsupportedByServer_ReturnsHebrewUnsupportedCode()
    {
        // First call (language=he) gets a 400 "not a language code known"; the auto-detect retry also
        // fails, landing on the terminal "doesn't support Hebrew" branch.
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("'he' is not a language code known to LanguageTool") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var engine = Build(handler);

        var result = await engine.DetectAsync("טקסט לבדיקה", "he");

        Assert.True(result.ServiceUnavailable);
        Assert.Equal(LanguageToolEngine.Codes.HebrewUnsupported, result.ServiceUnavailableCode);
        Assert.Contains("Hebrew", result.ServiceUnavailableMessage, StringComparison.Ordinal);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ServerUnreachable_ReturnsUnavailableCode()
    {
        var engine = Build(new ThrowingHandler(() => new HttpRequestException("connection refused")));

        var result = await engine.DetectAsync("some text", "en");

        Assert.True(result.ServiceUnavailable);
        Assert.Equal(LanguageToolEngine.Codes.Unavailable, result.ServiceUnavailableCode);
        Assert.False(string.IsNullOrEmpty(result.ServiceUnavailableMessage));
    }

    [Fact]
    public async Task RequestTimesOut_ReturnsTimeoutCode()
    {
        // TaskCanceledException with no inner TimeoutException still matches the engine's timeout catch
        // guard (ex.InnerException is TimeoutException or null).
        var engine = Build(new ThrowingHandler(() => new TaskCanceledException("request timed out")));

        var result = await engine.DetectAsync("some text", "en");

        Assert.True(result.ServiceUnavailable);
        Assert.Equal(LanguageToolEngine.Codes.Timeout, result.ServiceUnavailableCode);
        Assert.False(string.IsNullOrEmpty(result.ServiceUnavailableMessage));
    }

    [Fact]
    public async Task Success_LeavesServiceUnavailableCodeNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"matches\":[]}")
        });
        var engine = Build(handler);

        var result = await engine.DetectAsync("some text", "en");

        Assert.False(result.ServiceUnavailable);
        Assert.Null(result.ServiceUnavailableCode);
    }
}
