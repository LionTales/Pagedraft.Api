using System.Net;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// The ONE rule for telling a caller's cancellation from a model call that merely failed, and the one-retry
/// policy applied to a transient failure of a Proofread / LineEdit model call (c19; widened and narrowed by be-c03).
///
/// THE DISCRIMINATOR IS THE CALLER'S TOKEN, NEVER THE EXCEPTION TYPE. <see cref="HttpClient"/> surfaces its own
/// <see cref="HttpClient.Timeout"/> as a <see cref="TaskCanceledException"/>, which IS an
/// <see cref="OperationCanceledException"/>. A bare <c>catch (OperationCanceledException) { throw; }</c> therefore
/// reads a slow model as the user pressing cancel: on 2026-10-12 one 100-second cloud timeout on one proofread
/// chunk killed the whole chapter, ten times in one whole-book run. If OUR token is not cancelled nobody asked
/// us to stop, so the exception is a failed call and belongs to the fail-safe path. Same reasoning as be-c04's
/// filter in BookIntelligenceService.
///
/// <para>THE POLICY (be-c03), one attempt plus at most one retry:</para>
/// <list type="bullet">
/// <item>Caller cancelled: never retried, propagates at once (also when it lands during a Retry-After wait).</item>
/// <item>Cloud timeout (a bare <see cref="OperationCanceledException"/>, token live): retried once, immediately.
/// A cloud timeout is one slow request on a shared fleet; the next one usually lands (c19's measured case).</item>
/// <item>LOCAL timeout (<see cref="LocalModelTimeoutException"/>, thrown only by OllamaProvider): NOT retried.
/// The Ollama client's ceiling is <c>Ai:Providers:Ollama:TimeoutMinutes</c> (default 30): a retry made a wedged
/// runner cost 60 minutes with no tracker update, past AnalysisProgressTracker's 30-minute expiry, so the child job
/// vanished and its poll 404'd. A runner that did not answer in 30 minutes is the SAME class as the Ollama 500
/// below: one GPU, same load, it repeats. "Touch the job, then retry" was rejected: it keeps the job alive but
/// still spends another full timeout on a call that will most likely wedge again, and the chunk falls back
/// (flagged unreliable, be-c01) either way. Provider identity is read from the exception SHAPE, the way the 500
/// exclusion already is, because this seam sees only <see cref="IAiRouter"/> and an <see cref="AiRequest"/>.</item>
/// <item>HTTP 429: retried once ONLY when the provider said how long to wait (<see cref="ModelRateLimitedException"/>
/// carrying <c>Retry-After</c>) and that wait is at most <see cref="MaxRateLimitWait"/> (30 s); the retry waits that
/// long first. No header, or a longer wait: NOT retried. An immediate re-send into a rate limit almost surely 429s
/// again (the old behaviour); inventing a back-off is a guess that stalls the chapter, and the chapter is retryable
/// later from the whole-book run's failed list, by which time the window has passed. A 30 s cap keeps one chunk
/// from holding a parallel slot (and the tracker) for minutes.</item>
/// <item>Transport failure (an <see cref="HttpRequestException"/> with no status that HttpClient classified as a
/// connection / protocol error, or an <see cref="IOException"/> while reading the body), 408 and 5xx: retried once,
/// immediately.</item>
/// <item>NOT retried: a 4xx such as 401 (a bad key fails the same way twice), a parse fault, and Ollama's own
/// message-only <c>"Ollama request failed: ..."</c> exception (status unset, <see cref="HttpRequestError.Unknown"/>),
/// whose 500s on this machine are the VRAM/concurrency ceiling and repeat under the same load.</item>
/// </list>
///
/// <para>WHERE IT IS APPLIED: both chunk loops, and the single-shot <c>UnifiedAnalysisService.RunAsync</c> call of a
/// Proofread or LineEdit ONLY, so a short chapter (one call) and a long chapter (one call per chunk) get the same
/// policy. Every other single-shot type calls the router directly: none of them chunks, and on sync <c>/analyze</c>
/// an author is waiting, where a retry doubles a 300 s cloud timeout. Deliberately NOT applied either to
/// <c>RunWithInputAsync</c>, <c>RunRawDeferredRepairAsync</c> (and so <c>RunRawAsync</c>), the streaming path and
/// <c>ExplainSuggestionAsync</c>; each site states why.</para>
/// </summary>
public static class ModelCallRetry
{
    /// <summary>
    /// The longest <c>Retry-After</c> a retry will wait (be-c03). Longer requested waits are not retried at all:
    /// waiting less than the provider asked re-sends into the same limit.
    /// </summary>
    public static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True only when the CALLER asked to stop: the exception is an <see cref="OperationCanceledException"/> AND
    /// <paramref name="callerToken"/> is cancelled. Anything else is a failure of the call itself.
    /// </summary>
    public static bool IsCallerCancellation(Exception ex, CancellationToken callerToken)
        => ex is OperationCanceledException && callerToken.IsCancellationRequested;

    /// <summary>True when one more attempt is worth making; see the class remarks for the policy.</summary>
    public static bool IsTransient(Exception ex, CancellationToken callerToken)
        => RetryDelay(ex, callerToken) is not null;

    /// <summary>
    /// The wait before the one retry, or null when the failure must not be retried (see the class remarks).
    /// <see cref="TimeSpan.Zero"/> means "retry immediately".
    /// </summary>
    public static TimeSpan? RetryDelay(Exception ex, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
            return null;

        return ex switch
        {
            LocalModelTimeoutException => null,
            OperationCanceledException => TimeSpan.Zero,
            ModelRateLimitedException { RetryAfter: { } wait } when wait <= MaxRateLimitWait => wait,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => null,
            HttpRequestException http when http.StatusCode is { } status =>
                status == HttpStatusCode.RequestTimeout || (int)status >= 500 ? TimeSpan.Zero : null,
            HttpRequestException http => http.HttpRequestError != HttpRequestError.Unknown ? TimeSpan.Zero : null,
            IOException => TimeSpan.Zero,
            _ => null
        };
    }

    /// <summary>A short human-readable label for a failure, used in the retry log line and the chunk note.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        LocalModelTimeoutException => "local model timeout",
        OperationCanceledException => "timeout",
        ModelRateLimitedException { RetryAfter: { } wait } => $"HTTP 429, Retry-After {wait.TotalSeconds:0.###} s",
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException http => $"transport error ({http.HttpRequestError})",
        _ => ex.GetType().Name
    };

    /// <summary>
    /// The run-log note for a chunk that fell back after a failure: the exception message, prefixed with the retry
    /// note when a retry happened, capped at 200 chars (the cap the chunk loops always applied).
    /// </summary>
    public static string FailureNote(Exception ex, string? retryNote)
    {
        var note = retryNote is null ? ex.Message : $"{retryNote}; then: {ex.Message}";
        return note.Length > 200 ? note[..200] : note;
    }

    /// <summary>
    /// Sends <paramref name="request"/> through <paramref name="router"/>, retrying ONCE when the first attempt fails
    /// with a failure <see cref="RetryDelay"/> allows, after waiting that delay on <paramref name="timeProvider"/>
    /// (<see cref="TimeProvider.System"/> when null; the seam that keeps tests instant). A caller cancellation
    /// propagates immediately and is never retried; a second failure (or a non-retryable first one) is thrown for the
    /// caller's own fail-safe to handle. <paramref name="onRetry"/> receives the first failure BEFORE the wait so the
    /// caller can record the retry and touch its progress job.
    /// </summary>
    public static async Task<AiResponse> CompleteWithOneRetryAsync(
        IAiRouter router,
        AiRequest request,
        ILogger logger,
        string operation,
        int chunkNumber,
        int totalChunks,
        Action<Exception>? onRetry,
        CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        TimeSpan delay;
        try
        {
            return await router.CompleteAsync(request, ct);
        }
        catch (Exception ex) when (RetryDelay(ex, ct) is { } wait)
        {
            delay = wait;
            logger.LogWarning(
                ex,
                "{Operation} chunk {Index}/{Total}: model call failed ({Failure}); retrying once after {DelaySeconds} s.",
                operation, chunkNumber, totalChunks, Describe(ex), delay.TotalSeconds);
            onRetry?.Invoke(ex);
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, timeProvider ?? TimeProvider.System, ct);

        return await router.CompleteAsync(request, ct);
    }
}
