using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-c03: the retry policy's two defects, driven through the REAL providers over a stub HTTP handler (a real
/// HttpClient.Timeout fires; a real 429 response carries a real Retry-After header), so the exception SHAPE each
/// provider emits is what the policy sees. No live model is called.
/// (a) an Ollama timeout was retried, so a wedged local runner cost two full 30-minute timeouts with no tracker
///     update, past the tracker's 30-minute expiry;
/// (b) a 429 was retried immediately, ignoring Retry-After (EnsureSuccessStatusCode discarded the header).
/// </summary>
public class ModelCallRetryPolicyTests
{
    // ─── stubs ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Answers each request with the next scripted step; counts the requests it received.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps;
        private int _calls;

        public ScriptedHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] steps)
            => _steps = new ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>>(steps);

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _steps.TryDequeue(out var step)
                ? step(cancellationToken)
                : throw new InvalidOperationException("No scripted response left: the policy sent more requests than expected.");
        }
    }

    /// <summary>Never answers: only the HttpClient's own Timeout (or the caller's token) ends it.</summary>
    private static Func<CancellationToken, Task<HttpResponseMessage>> Hang =>
        async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };

    private static Func<CancellationToken, Task<HttpResponseMessage>> Ok =>
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ProviderTuningOutputKnobTests.AnyProviderResponseBody)
        });

    private static Func<CancellationToken, Task<HttpResponseMessage>> TooMany(Action<HttpResponseMessage>? headers = null) =>
        _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            headers?.Invoke(response);
            return Task.FromResult(response);
        };

    /// <summary>A client factory whose clients time out after <paramref name="timeout"/>, like the named clients do.</summary>
    private sealed class TimingOutFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly TimeSpan _timeout;
        public TimingOutFactory(HttpMessageHandler handler, TimeSpan timeout) { _handler = handler; _timeout = timeout; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false) { Timeout = _timeout };
    }

    /// <summary>
    /// Like <see cref="Hang"/>, but completes <paramref name="entered"/> when the request reaches the handler, so a
    /// test can cancel the caller at a point it chose instead of racing a timer against the client timeout.
    /// </summary>
    private static Func<CancellationToken, Task<HttpResponseMessage>> HangAfterSignalling(TaskCompletionSource entered) =>
        async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };

    /// <summary>A client timeout no test outlives: a caller-cancel test must never race it (be-c11).</summary>
    private static readonly TimeSpan NeverDuringATest = TimeSpan.FromMinutes(10);

    private static IReadOnlyDictionary<string, IAiAnalysisProvider> Providers(HttpMessageHandler handler, TimeSpan? clientTimeout = null) =>
        AiProviderRegistry.Create(
            new TimingOutFactory(handler, clientTimeout ?? TimeSpan.FromMilliseconds(200)),
            ProviderTuningOutputKnobTests.ProviderConfig(),
            Options.Create(new AiOptions()));

    /// <summary>Routes straight to one named provider (the AiRouter's own selection is not under test here).</summary>
    private sealed class DirectRouter : IAiRouter
    {
        private readonly IAiAnalysisProvider _provider;
        private readonly string _name;
        public DirectRouter(IReadOnlyDictionary<string, IAiAnalysisProvider> providers, string name)
        {
            _provider = providers[name];
            _name = name;
        }

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
            => _provider.CompleteAsync(ProviderTuningWirePayloadTests.Request(_name, request.TaskType), cancellationToken);

        public IAsyncEnumerable<string> StreamCompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// The delay seam: records every wait it is asked for. <paramref name="fire"/> = false never completes the wait,
    /// so a test can cancel the caller during it.
    /// </summary>
    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly bool _fire;
        public RecordingTimeProvider(bool fire = true) => _fire = fire;
        public ConcurrentQueue<TimeSpan> Requested { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Requested.Enqueue(dueTime);
            if (_fire)
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static AiRequest Req() => new() { InputText = "text", TaskType = AiTaskType.Proofread, Language = "he" };

    private static Task<AiResponse> Retry(IAiRouter router, CancellationToken ct, TimeProvider? clock = null, Action<Exception>? onRetry = null)
        => ModelCallRetry.CompleteWithOneRetryAsync(router, Req(), NullLogger.Instance, "Proofread", 1, 1, onRetry, ct, clock);

    // ─── (a) local vs cloud timeout ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OllamaTimeout_ThroughTheRealRouterAndProvider_IsNotRetried()
    {
        var handler = new ScriptedHandler(Hang, Hang);
        // The REAL router: an unconfigured AiOptions routes Proofread to Ollama, as production does by default.
        var router = new AiRouter(Options.Create(new AiOptions()), new PromptFactory(), Providers(handler));
        var clock = new RecordingTimeProvider();

        var ex = await Assert.ThrowsAsync<LocalModelTimeoutException>(() => Retry(router, CancellationToken.None, clock));

        Assert.Equal(1, handler.Calls);
        Assert.Empty(clock.Requested);
        Assert.IsAssignableFrom<OperationCanceledException>(ex); // every OCE-shaped filter elsewhere still sees a timeout
        Assert.False(ModelCallRetry.IsCallerCancellation(ex, CancellationToken.None));
    }

    [Fact]
    public async Task OllamaCallerCancel_IsNotDressedAsALocalTimeout()
    {
        // be-c11: deterministic. The request is in the handler before the caller cancels, and the client timeout is
        // far beyond the test (was: a 50 ms caller token raced the 200 ms client timeout and lost once under load).
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(HangAfterSignalling(entered));
        var provider = Providers(handler, NeverDuringATest)["Ollama"];
        using var cts = new CancellationTokenSource();

        var call = provider.CompleteAsync(ProviderTuningWirePayloadTests.Request("Ollama", AiTaskType.Proofread), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.Equal(1, handler.Calls);
        Assert.IsNotType<LocalModelTimeoutException>(ex);
        Assert.True(ModelCallRetry.IsCallerCancellation(ex, cts.Token));
    }

    [Fact]
    public async Task CloudTimeout_ThroughTheRealProvider_IsRetriedOnceImmediately()
    {
        var handler = new ScriptedHandler(Hang, Ok);
        var clock = new RecordingTimeProvider();

        var response = await Retry(new DirectRouter(Providers(handler), "OpenRouter"), CancellationToken.None, clock);

        Assert.Equal("ok", response.Content);
        Assert.Equal(2, handler.Calls);
        Assert.Empty(clock.Requested); // immediate: no wait was asked for
    }

    [Fact]
    public async Task CloudTimeoutTwice_ThrowsTheTimeoutAfterExactlyTwoCalls()
    {
        var handler = new ScriptedHandler(Hang, Hang);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Retry(new DirectRouter(Providers(handler), "OpenRouter"), CancellationToken.None, new RecordingTimeProvider()));

        Assert.IsNotType<LocalModelTimeoutException>(ex);
        Assert.Equal(2, handler.Calls);
    }

    // ─── (b) 429 and Retry-After ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimited_WithRetryAfterTwoSeconds_WaitsTwoSecondsOnTheSeam_ThenRetries()
    {
        var handler = new ScriptedHandler(TooMany(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2))), Ok);
        var clock = new RecordingTimeProvider();
        Exception? reported = null;

        var response = await Retry(new DirectRouter(Providers(handler), "OpenRouter"), CancellationToken.None, clock, ex => reported = ex);

        Assert.Equal("ok", response.Content);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(clock.Requested));
        Assert.Equal("HTTP 429, Retry-After 2 s", ModelCallRetry.Describe(reported!));
    }

    [Fact]
    public async Task RateLimited_WithoutRetryAfter_IsNotRetried()
    {
        var handler = new ScriptedHandler(TooMany(), Ok);
        var clock = new RecordingTimeProvider();

        var ex = await Assert.ThrowsAsync<ModelRateLimitedException>(() =>
            Retry(new DirectRouter(Providers(handler), "OpenRouter"), CancellationToken.None, clock));

        Assert.Null(ex.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(clock.Requested);
    }

    [Fact]
    public async Task RateLimited_WithRetryAfterAboveTheCap_IsNotRetried()
    {
        var beyond = ModelCallRetry.MaxRateLimitWait + TimeSpan.FromSeconds(1);
        var handler = new ScriptedHandler(TooMany(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(beyond)), Ok);
        var clock = new RecordingTimeProvider();

        var ex = await Assert.ThrowsAsync<ModelRateLimitedException>(() =>
            Retry(new DirectRouter(Providers(handler), "OpenRouter"), CancellationToken.None, clock));

        Assert.Equal(beyond, ex.RetryAfter);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(clock.Requested);
    }

    [Fact]
    public async Task RateLimited_RetryAfterAsAnHttpDate_IsMeasuredAgainstTheResponseDate()
    {
        var serverNow = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var handler = new ScriptedHandler(
            TooMany(r =>
            {
                r.Headers.Date = serverNow;
                r.Headers.RetryAfter = new RetryConditionHeaderValue(serverNow.AddSeconds(5));
            }),
            Ok);
        var clock = new RecordingTimeProvider();

        await Retry(new DirectRouter(Providers(handler), "OpenRouter"), CancellationToken.None, clock);

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(clock.Requested));
    }

    [Fact]
    public async Task EveryCloudProvider_TurnsA429IntoTheTypedExceptionCarryingRetryAfter()
    {
        foreach (var name in AiProviderHttpClients.CloudProviderNames)
        {
            var handler = new ScriptedHandler(TooMany(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2))));

            var ex = await Assert.ThrowsAsync<ModelRateLimitedException>(() =>
                Providers(handler)[name].CompleteAsync(ProviderTuningWirePayloadTests.Request(name, AiTaskType.Proofread)));

            Assert.True(TimeSpan.FromSeconds(2) == ex.RetryAfter, $"{name}: RetryAfter was {ex.RetryAfter}");
        }
    }

    [Fact]
    public async Task CallerCancel_DuringTheRetryAfterWait_PropagatesWithoutTheSecondCall()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(TooMany(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2))), Ok);
        var clock = new RecordingTimeProvider(fire: false); // the wait never ends on its own

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Retry(new DirectRouter(Providers(handler), "OpenRouter"), cts.Token, clock, _ => cts.CancelAfter(20)));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancel_IsNeverRetried()
    {
        // be-c11: the same race as OllamaCallerCancel_IsNotDressedAsALocalTimeout (a 50 ms caller token against the
        // 200 ms client timeout; losing it made a CLOUD timeout, retried into Ok), made deterministic the same way.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(HangAfterSignalling(entered), Ok);

        var call = Retry(new DirectRouter(Providers(handler, NeverDuringATest), "OpenRouter"), cts.Token, new RecordingTimeProvider());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.Equal(1, handler.Calls);
    }

    // ─── the single-shot RunAsync call shares the policy (short chapters behave like one chunk) ──────────────

    private static (UnifiedAnalysisService Svc, Guid ChapterId) SingleShot(
        AppDbContext db, IAiRouter router, AnalysisType type = AnalysisType.Proofread)
    {
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var context = new Mock<IAnalysisContextService>();
        context.Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, type, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = "שלום עולם. זהו טקסט קצר לבדיקה.",
                Scope = AnalysisScope.Chapter,
                AnalysisType = type,
                BookId = bookId,
                ChapterId = chapterId
            });
        var svc = new UnifiedAnalysisService(
            db, router, new PromptFactory(), new SfdtConversionService(),
            Options.Create(new AiOptions()), NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(), context.Object, new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());
        return (svc, chapterId);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task SingleShotProofread_CloudTimeoutOnce_IsRetried_AndThePersistedResultIsTheRetry()
    {
        await using var db = NewDb();
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1
                ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.")
                : Task.FromResult(new AiResponse { Content = "שלום עולם. זהו טקסט קצר לבדיקה.", Provider = "t", Model = "t" }));
        var (svc, chapterId) = SingleShot(db, router.Object);

        var result = await svc.RunAsync(AnalysisScope.Chapter, AnalysisType.Proofread, chapterId, null, "he");

        Assert.Equal(2, calls);
        Assert.NotNull(await db.AnalysisResults.SingleOrDefaultAsync(r => r.Id == result.Id));
    }

    [Fact]
    public async Task SingleShotProofread_LocalTimeout_IsNotRetried_AndFailsTheCall()
    {
        await using var db = NewDb();
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                throw new LocalModelTimeoutException(new TaskCanceledException("HttpClient.Timeout of 1800 seconds elapsing."));
            });
        var (svc, chapterId) = SingleShot(db, router.Object);

        await Assert.ThrowsAsync<LocalModelTimeoutException>(() =>
            svc.RunAsync(AnalysisScope.Chapter, AnalysisType.Proofread, chapterId, null, "he"));

        Assert.Equal(1, calls);
        Assert.Empty(await db.AnalysisResults.ToListAsync());
    }

    // final-r02 (CR-4): the single-shot wrap is SCOPED to Proofread / LineEdit (the types that also chunk). Every
    // other type reaches the router once, as before be-c03: on sync /analyze an author is waiting, and one retry
    // would double a 300 s cloud timeout.
    [Theory]
    [InlineData(AnalysisType.LinguisticAnalysis)]
    [InlineData(AnalysisType.LiteraryAnalysis)]
    [InlineData(AnalysisType.Summarization)]
    [InlineData(AnalysisType.Custom)]
    public async Task SingleShotNonProofreadType_CloudTimeout_IsCalledExactlyOnce_AndFailsTheCall(AnalysisType type)
    {
        await using var db = NewDb();
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.");
            });
        var (svc, chapterId) = SingleShot(db, router.Object, type);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.RunAsync(AnalysisScope.Chapter, type, chapterId, type == AnalysisType.Custom ? "Say something." : null, "he"));

        Assert.Equal(1, calls);
        Assert.Empty(await db.AnalysisResults.ToListAsync());
    }

    [Fact]
    public async Task SingleShotLineEdit_CloudTimeoutOnce_IsRetried()
    {
        await using var db = NewDb();
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.");
            });
        var (svc, chapterId) = SingleShot(db, router.Object, AnalysisType.LineEdit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.RunAsync(AnalysisScope.Chapter, AnalysisType.LineEdit, chapterId, null, "he"));

        Assert.Equal(2, calls);
    }
}
