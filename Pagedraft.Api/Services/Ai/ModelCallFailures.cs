using System.Net;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// The LOCAL runner (Ollama) did not answer within <c>Ai:Providers:Ollama:TimeoutMinutes</c> (be-c03).
///
/// <para>Thrown by <see cref="OllamaProvider.CompleteAsync"/> in place of the bare <see cref="TaskCanceledException"/>
/// HttpClient raises on its own <see cref="HttpClient.Timeout"/>, and ONLY when the caller's token is live (a caller
/// cancellation is rethrown untouched). It exists so <c>ModelCallRetry</c> can tell a local timeout from a cloud one
/// by the exception's SHAPE, the same way it already tells Ollama's message-only 500 from a cloud 5xx: the retry seam
/// sees only <see cref="IAiRouter"/> and an <c>AiRequest</c>, which carries no provider.</para>
///
/// <para>It DERIVES from <see cref="TaskCanceledException"/> on purpose, so every existing
/// <c>catch (OperationCanceledException)</c> / token-discriminating filter in the codebase (BookIntelligenceService,
/// ChapterBriefService, the chunk loops' <c>IsCallerCancellation</c>) classifies it exactly as before.</para>
/// </summary>
public sealed class LocalModelTimeoutException : TaskCanceledException
{
    public LocalModelTimeoutException(OperationCanceledException inner)
        : base($"Local model (Ollama) timed out: {inner.Message}", inner)
    {
    }
}

/// <summary>
/// A cloud provider answered HTTP 429 (be-c03). Carries the response's <c>Retry-After</c> as a span (null when the
/// header was absent or unreadable), which <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> discarded, so the
/// one-retry policy can wait what the provider asked for instead of re-sending into the same limit. Still an
/// <see cref="HttpRequestException"/> with <see cref="HttpRequestException.StatusCode"/> 429, so any code that reads
/// the status keeps working.
/// </summary>
public sealed class ModelRateLimitedException : HttpRequestException
{
    public ModelRateLimitedException(string message, TimeSpan? retryAfter)
        : base(message, null, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>The provider's requested wait, clamped at zero; null when the response carried no usable header.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// The ONE non-2xx check the cloud providers (OpenAI, Azure, Anthropic, OpenRouter / OpenAI-compatible) run on a
/// completion response. Identical to <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> except for 429, which
/// becomes a <see cref="ModelRateLimitedException"/> carrying <c>Retry-After</c>. Ollama keeps its own message-only
/// exception (see <c>ModelCallRetry.IsTransient</c> for why that shape matters).
/// </summary>
public static class ProviderResponse
{
    public static void EnsureSuccess(HttpResponseMessage response, string provider)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = RetryAfterOf(response, TimeProvider.System);
            response.Dispose();
            var detail = wait is { } w ? $"Retry-After {w.TotalSeconds:0.###} s" : "no Retry-After header";
            throw new ModelRateLimitedException($"{provider} rate-limited the request (HTTP 429, {detail}).", wait);
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Reads <c>Retry-After</c> in either form RFC 9110 allows: delta-seconds, or an HTTP-date measured against the
    /// response's own <c>Date</c> header when present (so a skewed local clock cannot stretch the wait), else against
    /// <paramref name="clock"/>. A date in the past reads as zero. Null when the header is absent.
    /// </summary>
    internal static TimeSpan? RetryAfterOf(HttpResponseMessage response, TimeProvider clock)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;
        if (header.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date)
        {
            var now = response.Headers.Date ?? clock.GetUtcNow();
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }
}
