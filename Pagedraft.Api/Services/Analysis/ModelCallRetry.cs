using System.Net;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// The ONE rule for telling a caller's cancellation from a model call that merely failed, and the one-retry
/// policy the chunked Proofread / LineEdit loops apply to a transient failure (c19).
///
/// THE DISCRIMINATOR IS THE CALLER'S TOKEN, NEVER THE EXCEPTION TYPE. <see cref="HttpClient"/> surfaces its own
/// <see cref="HttpClient.Timeout"/> as a <see cref="TaskCanceledException"/>, which IS an
/// <see cref="OperationCanceledException"/>. A bare <c>catch (OperationCanceledException) { throw; }</c> therefore
/// reads a slow model as the user pressing cancel: on 2026-10-12 one 100-second cloud timeout on one proofread
/// chunk killed the whole chapter, ten times in one whole-book run. If OUR token is not cancelled nobody asked
/// us to stop, so the exception is a failed call and belongs to the fail-safe path. Same reasoning as be-c04's
/// filter in BookIntelligenceService.
/// </summary>
public static class ModelCallRetry
{
    /// <summary>
    /// True only when the CALLER asked to stop: the exception is an <see cref="OperationCanceledException"/> AND
    /// <paramref name="callerToken"/> is cancelled. Anything else is a failure of the call itself.
    /// </summary>
    public static bool IsCallerCancellation(Exception ex, CancellationToken callerToken)
        => ex is OperationCanceledException && callerToken.IsCancellationRequested;

    /// <summary>
    /// True when one more attempt is worth making: never after the caller cancelled; otherwise a timeout
    /// (<see cref="OperationCanceledException"/> with the caller's token live), a transport failure (an
    /// <see cref="HttpRequestException"/> with no status that HttpClient classified as a connection / protocol
    /// error, or an <see cref="IOException"/> while reading the body), or a server-side HTTP status (408, 429, 5xx).
    ///
    /// NOT retried: a 4xx such as 401 (a bad key fails the same way twice), a parse fault, and Ollama's own
    /// message-only <c>"Ollama request failed: ..."</c> exception (status unset, <see cref="HttpRequestError.Unknown"/>),
    /// whose 500s on this machine are the VRAM/concurrency ceiling and repeat under the same load.
    /// </summary>
    public static bool IsTransient(Exception ex, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
            return false;

        return ex switch
        {
            OperationCanceledException => true,
            HttpRequestException http when http.StatusCode is { } status =>
                status == HttpStatusCode.RequestTimeout
                || status == HttpStatusCode.TooManyRequests
                || (int)status >= 500,
            HttpRequestException http => http.HttpRequestError != HttpRequestError.Unknown,
            IOException => true,
            _ => false
        };
    }

    /// <summary>A short human-readable label for a failure, used in the retry log line and the chunk note.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "timeout",
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
    /// with a <see cref="IsTransient">transient</see> error. A caller cancellation propagates immediately and is never
    /// retried; a second failure (or a non-transient first one) is thrown for the caller's own fail-safe to handle.
    /// <paramref name="onRetry"/> receives the first failure so the caller can record that the retry happened.
    /// </summary>
    public static async Task<AiResponse> CompleteWithOneRetryAsync(
        IAiRouter router,
        AiRequest request,
        ILogger logger,
        string operation,
        int chunkNumber,
        int totalChunks,
        Action<Exception>? onRetry,
        CancellationToken ct)
    {
        try
        {
            return await router.CompleteAsync(request, ct);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            logger.LogWarning(
                ex,
                "{Operation} chunk {Index}/{Total}: model call failed ({Failure}); retrying once.",
                operation, chunkNumber, totalChunks, Describe(ex));
            onRetry?.Invoke(ex);
        }

        return await router.CompleteAsync(request, ct);
    }
}
