using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// c19 (owner report 2026-10-12): a forced whole-book proofread on a thinking-tier book lost 10 of 55 chapters,
/// each to "HttpClient.Timeout of 100 seconds elapsing" on ONE chunk. Two defects, pinned here:
/// (1) the cloud providers sent through the UNNAMED HttpClient and so got .NET's 100 s default, unlike Ollama's
/// configurable named client; (2) the chunk loops' <c>catch (OperationCanceledException) { throw; }</c> read that
/// timeout (a TaskCanceledException with the caller's token live) as the caller cancelling, so one slow chunk
/// killed the chapter instead of falling back to its original text.
/// No live provider is called: the fake router / stub handler IS the proof (no cloud gate was run).
/// </summary>
public class CloudTimeoutIsNotCancelTests
{
    // ─── (1) named clients with a configurable timeout ──────────────────────────────────────────────────────

    private static IHttpClientFactory FactoryWith(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddCloudAiProviderHttpClients();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    [Fact]
    public void NamedCloudClient_TakesItsTimeoutFromConfig()
    {
        var factory = FactoryWith(new Dictionary<string, string?>
        {
            ["Ai:Providers:OpenRouter:TimeoutSeconds"] = "450",
            ["Ai:Providers:Anthropic:TimeoutSeconds"] = "0",
            ["Ai:Providers:Azure:TimeoutSeconds"] = "-5"
        });

        Assert.Equal(TimeSpan.FromSeconds(450), factory.CreateClient(AiProviderHttpClients.ClientName("OpenRouter")).Timeout);
        // Unset, zero and negative all fall back to the default ceiling, never to "no timeout" or to 100 s.
        Assert.Equal(TimeSpan.FromSeconds(AiProviderHttpClients.DefaultCloudTimeoutSeconds),
            factory.CreateClient(AiProviderHttpClients.ClientName("OpenAI")).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(AiProviderHttpClients.DefaultCloudTimeoutSeconds),
            factory.CreateClient(AiProviderHttpClients.ClientName("Anthropic")).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(AiProviderHttpClients.DefaultCloudTimeoutSeconds),
            factory.CreateClient(AiProviderHttpClients.ClientName("Azure")).Timeout);
        // The defect: the unnamed client is still .NET's 100 s default, which is why nothing may send through it.
        Assert.Equal(TimeSpan.FromSeconds(100), factory.CreateClient().Timeout);
    }

    [Fact]
    public void NamedCloudClient_DefaultIsAboveTheOldHundredSeconds()
        => Assert.True(AiProviderHttpClients.DefaultCloudTimeoutSeconds > 100);

    [Fact]
    public void ShippedAppsettings_GivesEveryCloudProviderAPositiveTimeout()
    {
        var config = ProviderTuningConfigParityTests.LoadShippedConfiguration();

        foreach (var name in AiProviderHttpClients.CloudProviderNames)
        {
            var raw = config[$"Ai:Providers:{name}:TimeoutSeconds"];
            Assert.True(int.TryParse(raw, out var seconds) && seconds > 100,
                $"appsettings.json Ai:Providers:{name}:TimeoutSeconds is '{raw}'; expected a value above the old 100 s default.");
            Assert.Equal(TimeSpan.FromSeconds(seconds), AiProviderHttpClients.ResolveTimeout(config, name));
        }
    }

    [Fact]
    public void EveryRegisteredNonOllamaProvider_HasANamedCloudClient()
    {
        var registeredCloud = ProviderTuningOutputKnobTests.RegisteredProviderNames()
            .Where(n => !n.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var named = AiProviderHttpClients.CloudProviderNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(named, registeredCloud, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RecordingFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public RecordingFactory(HttpMessageHandler handler) => _handler = handler;
        public ConcurrentQueue<string> Names { get; } = new();

        public HttpClient CreateClient(string name)
        {
            Names.Enqueue(name);
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    [Fact]
    public async Task EveryCloudProvider_SendsThroughItsOwnNamedClient()
    {
        foreach (var name in AiProviderHttpClients.CloudProviderNames)
        {
            var factory = new RecordingFactory(
                new ProviderTuningWirePayloadTests.CapturingHandler(ProviderTuningOutputKnobTests.AnyProviderResponseBody));
            var providers = AiProviderRegistry.Create(
                factory, ProviderTuningOutputKnobTests.ProviderConfig(), Options.Create(new AiOptions()));

            await providers[name].CompleteAsync(ProviderTuningWirePayloadTests.Request(name, AiTaskType.Proofread));

            var requested = Assert.Single(factory.Names);
            Assert.Equal(AiProviderHttpClients.ClientName(name), requested);
        }
    }

    // ─── (2) the discriminator and the one-retry policy ─────────────────────────────────────────────────────

    public static IEnumerable<object[]> TransientCases() => new[]
    {
        new object[] { new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."), true },
        new object[] { new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway), true },
        new object[] { new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests), true },
        new object[] { new HttpRequestException("timeout", null, HttpStatusCode.RequestTimeout), true },
        new object[] { new HttpRequestException(HttpRequestError.ConnectionError, "refused"), true },
        new object[] { new IOException("connection reset"), true },
        new object[] { new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized), false },
        new object[] { new HttpRequestException("Ollama request failed: InternalServerError"), false },
        new object[] { new JsonException("bad json"), false },
        new object[] { new InvalidOperationException("OpenRouter ApiKey not configured."), false },
    };

    [Theory]
    [MemberData(nameof(TransientCases))]
    public void IsTransient_WithTheCallerTokenLive(Exception ex, bool expected)
    {
        Assert.Equal(expected, ModelCallRetry.IsTransient(ex, CancellationToken.None));
        Assert.False(ModelCallRetry.IsCallerCancellation(ex, CancellationToken.None));
    }

    [Fact]
    public void ACancelledCallerToken_IsNeverTransient_AndIsTheOnlyCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new TaskCanceledException();

        Assert.False(ModelCallRetry.IsTransient(ex, cts.Token));
        Assert.True(ModelCallRetry.IsCallerCancellation(ex, cts.Token));
        Assert.False(ModelCallRetry.IsTransient(new HttpRequestException("x", null, HttpStatusCode.BadGateway), cts.Token));
    }

    private static AiResponse Ok(string content) => new() { Content = content, Provider = "test", Model = "test" };

    private static AiRequest Req() => new() { InputText = "text", TaskType = AiTaskType.Proofread };

    [Fact]
    public async Task CompleteWithOneRetry_TimeoutThenSuccess_RetriesOnceAndReportsIt()
    {
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? throw new TaskCanceledException("timeout") : Task.FromResult(Ok("ok")));
        Exception? reported = null;

        var response = await ModelCallRetry.CompleteWithOneRetryAsync(
            router.Object, Req(), NullLogger.Instance, "Proofread", 1, 1, ex => reported = ex, CancellationToken.None);

        Assert.Equal("ok", response.Content);
        Assert.Equal(2, calls);
        Assert.IsType<TaskCanceledException>(reported);
    }

    [Fact]
    public async Task CompleteWithOneRetry_TwoTimeouts_ThrowsTheSecondAfterExactlyTwoCalls()
    {
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => { calls++; throw new TaskCanceledException("timeout"); });

        await Assert.ThrowsAsync<TaskCanceledException>(() => ModelCallRetry.CompleteWithOneRetryAsync(
            router.Object, Req(), NullLogger.Instance, "Proofread", 1, 1, null, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CompleteWithOneRetry_NonTransient_IsNotRetried()
    {
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => { calls++; throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized); });

        await Assert.ThrowsAsync<HttpRequestException>(() => ModelCallRetry.CompleteWithOneRetryAsync(
            router.Object, Req(), NullLogger.Instance, "Proofread", 1, 1, null, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompleteWithOneRetry_CallerCancel_PropagatesWithoutARetry()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest _, CancellationToken token) =>
            {
                calls++;
                cts.Cancel();
                throw new TaskCanceledException("canceled", null, token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelCallRetry.CompleteWithOneRetryAsync(
            router.Object, Req(), NullLogger.Instance, "Proofread", 1, 1, null, cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FailureNote_PrefixesTheRetryAndKeepsTheTwoHundredCharCap()
    {
        Assert.Equal("boom", ModelCallRetry.FailureNote(new Exception("boom"), null));
        Assert.Equal("retried once after timeout; then: boom", ModelCallRetry.FailureNote(new Exception("boom"), "retried once after timeout"));
        Assert.Equal(200, ModelCallRetry.FailureNote(new Exception(new string('x', 500)), "retried once after timeout").Length);
    }

    // ─── (2) through the real chunked Proofread / LineEdit loops ────────────────────────────────────────────

    private const string SlowMarker = "SLOWCHUNK";

    /// <summary>Three paragraphs of 40 words; the middle one carries the marker the fake router times out on.</summary>
    private static string ThreeParagraphs()
    {
        var plain = string.Join(" ", Enumerable.Repeat("שלום", 40));
        var slow = SlowMarker + " " + string.Join(" ", Enumerable.Repeat("עולם", 39));
        return plain + "\n\n" + slow + "\n\n" + plain;
    }

    private static string Unwrap(string input, string start, string end)
    {
        var s = input.IndexOf(start, StringComparison.Ordinal);
        if (s < 0) return input;
        s += start.Length;
        var e = input.IndexOf(end, s, StringComparison.Ordinal);
        return e > s ? input[s..e] : input;
    }

    private static UnifiedAnalysisService Service(AppDbContext db, IAiRouter router) => new(
        db,
        router,
        new PromptFactory(),
        new SfdtConversionService(),
        Options.Create(new AiOptions()),
        NullLogger<UnifiedAnalysisService>.Instance,
        new AnalysisProgressTracker(),
        new Mock<IAnalysisContextService>().Object,
        new SuggestionDiffService(),
        new KtivMaleChecker(new HebrewStyleOptions()),
        new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
        new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
        new StubBookEntityProvider());

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// Invokes the private chunked runner by reflection, by POSITION, exactly as AnalysisRunLogTests does (see the
    /// "BEFORE CHANGING THIS SIGNATURE" note on the method).
    /// </summary>
    private static Task<AnalysisResult> InvokeChunked(
        UnifiedAnalysisService svc, string method, AnalysisType type, string inputText, CancellationToken ct)
    {
        var m = typeof(UnifiedAnalysisService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(m);
        var context = new AnalysisContext
        {
            TargetText = inputText,
            Scope = AnalysisScope.Chapter,
            AnalysisType = type
        };
        return (Task<AnalysisResult>)m!.Invoke(svc, new object?[]
        {
            inputText, null, null, null, AnalysisScope.Chapter, Guid.NewGuid(), null, "he",
            40, // chunkTargetWords: one chunk per paragraph
            1,  // maxParallel
            AiTier.Thinking,
            Guid.NewGuid(),
            context,
            ct
        })!;
    }

    private static async Task<(AnalysisRunLog Log, List<AnalysisChunkOutcome> Outcomes)> RunLogOf(AppDbContext db, AnalysisResult result)
    {
        var log = await db.AnalysisRunLogs.SingleAsync(r => r.AnalysisResultId == result.Id);
        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            log.ChunkDetailsJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return (log, outcomes.OrderBy(o => o.ChunkIndex).ToList());
    }

    [Fact]
    public async Task ProofreadChunk_TimingOutTwice_FallsBackToItsOriginalText_AndTheChapterIsPersisted()
    {
        await using var db = NewDb();
        var slowCalls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest req, CancellationToken _) =>
            {
                var text = Unwrap(req.InputText ?? "", "[TEXT_TO_CORRECT]", "[/TEXT_TO_CORRECT]");
                if (text.Contains(SlowMarker))
                {
                    Interlocked.Increment(ref slowCalls);
                    // Exactly what HttpClient throws on its own Timeout: the caller's token is NOT cancelled.
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
                }
                return Task.FromResult(Ok(text));
            });

        var input = ThreeParagraphs();
        var result = await InvokeChunked(Service(db, router.Object), "RunProofreadChunkedAsync", AnalysisType.Proofread, input, CancellationToken.None);

        Assert.Equal(2, slowCalls); // first attempt + exactly one retry
        Assert.NotNull(await db.AnalysisResults.SingleOrDefaultAsync(r => r.Id == result.Id));
        var (log, outcomes) = await RunLogOf(db, result);
        Assert.True(log.TotalChunks >= 2, $"precondition: the input must chunk (got {log.TotalChunks})");
        Assert.Equal(1, log.FallbackChunks);
        var failed = Assert.Single(outcomes, o => o.Outcome == "FallbackError");
        Assert.StartsWith("retried once after timeout; then: The request was canceled", failed.Note);
        Assert.All(outcomes.Where(o => o != failed), o => Assert.Equal("Succeeded", o.Outcome));
        // The slow chunk kept its source text, so the merged result is the source (the fake echoes the rest).
        Assert.Contains(SlowMarker, result.ResultText);
    }

    [Fact]
    public async Task ProofreadChunk_TimingOutOnce_SucceedsOnTheRetry_AndRecordsTheRetry()
    {
        await using var db = NewDb();
        var slowCalls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest req, CancellationToken _) =>
            {
                var text = Unwrap(req.InputText ?? "", "[TEXT_TO_CORRECT]", "[/TEXT_TO_CORRECT]");
                if (text.Contains(SlowMarker) && Interlocked.Increment(ref slowCalls) == 1)
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
                return Task.FromResult(Ok(text));
            });

        var result = await InvokeChunked(Service(db, router.Object), "RunProofreadChunkedAsync", AnalysisType.Proofread, ThreeParagraphs(), CancellationToken.None);

        Assert.Equal(2, slowCalls);
        var (log, outcomes) = await RunLogOf(db, result);
        Assert.Equal(0, log.FallbackChunks);
        Assert.Single(outcomes, o => o.Note == "retried once after timeout");
        Assert.All(outcomes, o => Assert.Equal("Succeeded", o.Outcome));
    }

    [Fact]
    public async Task ProofreadChunk_CallerCancel_PropagatesPromptly_WithNoRetryAndNothingPersisted()
    {
        await using var db = NewDb();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest _, CancellationToken token) =>
            {
                Interlocked.Increment(ref calls);
                cts.Cancel(); // the user / ApplicationStopping cancels while the first call is in flight
                throw new TaskCanceledException("canceled", null, token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeChunked(Service(db, router.Object), "RunProofreadChunkedAsync", AnalysisType.Proofread, ThreeParagraphs(), cts.Token));

        Assert.Equal(1, calls); // no retry, and maxParallel 1 means no further chunk was even sent
        Assert.Empty(await db.AnalysisResults.ToListAsync());
        Assert.Empty(await db.AnalysisRunLogs.ToListAsync());
    }

    [Fact]
    public async Task LineEditChunk_TimingOutTwice_FallsBack_AndTheChapterIsPersisted()
    {
        await using var db = NewDb();
        var slowCalls = 0;
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest req, CancellationToken _) =>
            {
                if ((req.InputText ?? "").Contains(SlowMarker))
                {
                    Interlocked.Increment(ref slowCalls);
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
                }
                return Task.FromResult(Ok("{\"suggestions\":[],\"overallFeedback\":\"\"}"));
            });

        var result = await InvokeChunked(Service(db, router.Object), "RunLineEditChunkedAsync", AnalysisType.LineEdit, ThreeParagraphs(), CancellationToken.None);

        Assert.Equal(2, slowCalls);
        var (log, outcomes) = await RunLogOf(db, result);
        Assert.True(log.TotalChunks >= 2, $"precondition: the input must chunk (got {log.TotalChunks})");
        Assert.Equal(1, log.FallbackChunks);
        var failed = Assert.Single(outcomes, o => o.Outcome == "FallbackError");
        Assert.StartsWith("retried once after timeout", failed.Note);
    }

    [Fact]
    public async Task LineEditChunk_CallerCancel_Propagates()
    {
        await using var db = NewDb();
        using var cts = new CancellationTokenSource();
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest _, CancellationToken token) =>
            {
                cts.Cancel();
                throw new TaskCanceledException("canceled", null, token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeChunked(Service(db, router.Object), "RunLineEditChunkedAsync", AnalysisType.LineEdit, ThreeParagraphs(), cts.Token));
        Assert.Empty(await db.AnalysisResults.ToListAsync());
    }

    // ─── (2) a degrade-gracefully site outside the chunk loops ──────────────────────────────────────────────

    private static async Task<(ChapterBriefService Svc, Guid BookId, Guid ChapterId)> BriefService(AppDbContext db, IAiRouter router)
    {
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "c19", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Order = 0, Title = "Ch1", ContentText = "תוכן הפרק הראשון." });
        await db.SaveChangesAsync();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var svc = new ChapterBriefService(db, router, new PromptFactory(), Options.Create(new AiOptions()), scopeFactory,
            NullLogger<ChapterBriefService>.Instance);
        return (svc, bookId, chapterId);
    }

    [Fact]
    public async Task ChapterBrief_ModelTimeout_DegradesToNull_InsteadOfAbortingTheCaller()
    {
        await using var db = NewDb();
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
        var (svc, bookId, chapterId) = await BriefService(db, router.Object);

        var brief = await svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he", CancellationToken.None);

        Assert.Null(brief);
    }

    [Fact]
    public async Task ChapterBrief_CallerCancel_StillPropagates()
    {
        await using var db = NewDb();
        using var cts = new CancellationTokenSource();
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest _, CancellationToken token) =>
            {
                cts.Cancel();
                throw new TaskCanceledException("canceled", null, token);
            });
        var (svc, bookId, chapterId) = await BriefService(db, router.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.LoadOrBuildChapterBriefAsync(bookId, chapterId, "he", cts.Token));
    }
}
