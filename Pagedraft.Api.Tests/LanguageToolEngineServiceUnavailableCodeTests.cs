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
    /// <summary>
    /// The wire literals, pinned INDEPENDENTLY of <see cref="LanguageToolEngine.Codes"/>. A C# const is
    /// inlined at every call site, so `Assert.Equal(LanguageToolEngine.Codes.X, result.ServiceUnavailableCode)`
    /// moves in lockstep with a rename of the const and stays green even though the wire string changed.
    /// The OTHER end of this contract is the client's literal-keyed map
    /// (pagedraft-client/src/app/features/language-engine/issue-panel.component.ts, the UNAVAILABLE_COPY
    /// map - cited by NAME, never by line, because both files move) which matches on these exact strings
    /// and silently falls back to generic copy for any code it doesn't recognize. A rename of any Codes
    /// value must move BOTH repos in the same change.
    ///
    /// BE PRECISE ABOUT WHAT THESE CATCH, because the split is not the obvious one (final-r01):
    ///  - a changed VALUE with the const NAME kept is caught HERE and nowhere else (verified by mutating
    ///    Codes.HebrewUnsupported's value: this file's assertion is the one that goes red);
    ///  - a pure RENAME of a const is caught by the COMPILER, since this file still references
    ///    LanguageToolEngine.Codes.* on the line above each literal;
    ///  - what NOTHING in this repo catches is the three hops between DetectResult and the JSON the
    ///    client parses: the metadata key "languageToolCode" written in LanguageEngine.cs, the read of
    ///    that same string in LanguageEngineController.cs, and the IssuesResponse property name. These
    ///    assertions stop at the ENGINE seam. Renaming the metadata key leaves the whole suite green and
    ///    drops the field off the wire.
    /// </summary>
    private static class WireLiterals
    {
        public const string HebrewUnsupported = "hebrew-unsupported";
        public const string Disabled = "disabled";
        public const string Unavailable = "unavailable";
        public const string Timeout = "timeout";
    }

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
        // Pinned against the raw wire literal too - see WireLiterals doc comment.
        Assert.Equal(WireLiterals.Disabled, result.ServiceUnavailableCode);
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
        Assert.Equal(WireLiterals.HebrewUnsupported, result.ServiceUnavailableCode);
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
        Assert.Equal(WireLiterals.Unavailable, result.ServiceUnavailableCode);
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
        Assert.Equal(WireLiterals.Timeout, result.ServiceUnavailableCode);
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

    [Fact]
    public async Task HebrewAutoRetrySucceeds_ServiceUnavailableTrueButCodeIsNullOrEmpty()
    {
        // KNOWN GAP, pinned deliberately - do NOT assign this branch a code here; that is a contract
        // change tracked separately (be-c02). This is the FIFTH unavailability branch by the doc's
        // enumeration (the four CODED reasons plus this one); by file position it is the SECOND
        // ServiceUnavailable assignment in LanguageToolEngine.cs, and it is not a literal `= true` - it
        // reads `ServiceUnavailable = langCode == "he"` in the auto-retry-SUCCESS arm. Cited by branch,
        // not by line: the `400` retry with `auto` SUCCEEDS and the originally requested language was
        // Hebrew, so the result is ServiceUnavailable=true with a non-empty issue list and NO code -
        // unlike the other four branches, which all carry one. Documented in the "KNOWN GAP" paragraph
        // of PAGEDRAFT_DESIGN.md's language-engine issues section ("There is a FIFTH unavailability
        // branch that sets a message and no code"), which sits just BELOW the four-code vocabulary
        // table. If this test goes red, the gap was closed (or moved) without updating
        // that doc and without going through be-c02 - fix the doc/route the change through be-c02, don't
        // just re-pin the new behavior here.
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("'he' is not a language code known to LanguageTool") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"matches\":[{\"offset\":0,\"length\":3,\"message\":\"issue\"}]}")
                };
        });
        var engine = Build(handler);

        var result = await engine.DetectAsync("טקסט לבדיקה", "he");

        Assert.True(result.ServiceUnavailable);
        Assert.NotEmpty(result.Issues);
        Assert.True(
            string.IsNullOrEmpty(result.ServiceUnavailableCode),
            $"the auto-retry-SUCCESS branch now carries the code '{result.ServiceUnavailableCode}'. That is a "
            + "WIRE CONTRACT CHANGE, not a bug fix: the client's UNAVAILABLE_COPY map has no entry for it, so "
            + "it renders the generic fallback, and PAGEDRAFT_DESIGN.md's KNOWN GAP paragraph still says this "
            + "branch sends a message and no code. Route the change through be-c02 (assign the code, add the "
            + "client copy, update the doc) rather than re-pinning the new value here.");
        Assert.Equal(2, calls);
    }
}
