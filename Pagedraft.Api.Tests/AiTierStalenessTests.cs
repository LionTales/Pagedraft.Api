using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Shared harness for the p3-3 staleness/cache-coherence suite (model-tier-fast-thinking plan).
///
/// THE ROUTER MOCK IS TIER-AWARE ON PURPOSE, and that is what makes these tests able to fail. In production
/// the provider sets <see cref="AiResponse.Model"/> from the SAME selection <see cref="AiRouter"/> resolved,
/// so "the model that came back" is a function of the tier that was STAMPED on the request. The mock
/// reproduces exactly that: it reads <see cref="AiRequest.Tier"/> and answers with the tier's model. A build
/// whose request was NOT stamped therefore comes back with the LOCAL model id and gets stamped into
/// <c>BuiltWithModel</c>, which the tier-aware freshness gate then rejects - i.e. the "permanently stale"
/// failure the atomic pairing exists to prevent shows up as a red test rather than as an extra LLM call per
/// chapter in production.
/// </summary>
internal static class AiTierTestHarness
{
    internal const string Lang = "he";
    internal const string LocalProvider = "Ollama";
    internal const string CloudProvider = "OpenRouter";
    internal const string LocalLinguisticModel = "local-linguistic-model";
    internal const string CloudLinguisticModel = "cloud-linguistic-model";
    internal const string SummarizationModel = "local-summarization-model";

    /// <summary>A valid LinguisticAnalysisResult-shaped payload for the chapter style profile build.</summary>
    internal const string MetricsJson = """
        {
          "syntaxMetrics": { "sentenceCount": 6, "averageSentenceLength": 12.0, "complexSentences": 2, "shortestSentence": 4, "longestSentence": 20 },
          "morphologyMetrics": { "wordCount": 100, "uniqueWords": 70, "averageWordLength": 4.3, "lexicalDensity": 0.6 },
          "styleMetrics": { "formality": "literary", "readability": 0.75, "voiceBalance": "active" },
          "grammaticalityScore": 0.9,
          "summary": "ok",
          "deviations": [],
          "consistencyIssues": []
        }
        """;

    /// <summary>A parseable structured chapter brief, so a seeded ChunkSummary reads as USABLE.</summary>
    internal const string BriefJson = """
        { "plotEvents": ["seeded event"], "characterStates": [], "thematicMarkers": [], "toneNotes": "seeded tone", "openThreads": [] }
        """;

    /// <summary>
    /// DI graph with a shared-name in-memory DB (StyleBaselineService creates a per-chapter DI scope, so the
    /// name must be fixed for those scopes to see the seed data), and a FeatureModels block wired exactly the
    /// way the shipped one is: a bare <c>LinguisticAnalysis</c> key, a <c>LinguisticAnalysis_thinking</c>
    /// counterpart on a DIFFERENT provider, and a <c>Summarization</c> key with NO tier counterpart (which is
    /// what <c>AiTierConfigParityTests</c> enforces for every non-allowlisted task).
    /// </summary>
    /// <param name="onRequest">
    /// be-c02: a SIDE-EFFECTING SEAM invoked inside the router, before it answers, with the request and the
    /// built root provider. It is how a mid-build tier flip is made deterministic without threads: the
    /// side effect fires at a known point in the build (during chapter N's model call), exactly the way
    /// <c>OneShotSideEffectTimeProvider</c> makes the tracker's check-then-act race deterministic. Null for
    /// every pre-existing caller, which leaves the router's behaviour byte-identical.
    /// </param>
    /// <param name="maxParallelChapters">
    /// Overrides <see cref="AiOptions.MaxParallelStyleBaselineChapters"/> (default 2). A test that needs the
    /// chapters built in a KNOWN order - so "flip between chapter 1 and chapter 2" means what it says -
    /// passes 1; anything else would let both chapters resolve the tier before the flip and pass vacuously.
    /// </param>
    internal static ServiceProvider Build(
        out List<AiRequest> captured,
        out Mock<IAiRouter> routerMock,
        Action<AiRequest, IServiceProvider>? onRequest = null,
        int? maxParallelChapters = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var dbName = "AiTierStaleness_" + Guid.NewGuid();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));

        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var requests = new List<AiRequest>();
        captured = requests;

        // Assigned to the finished provider just before Build returns, so the onRequest side effect (which
        // only ever runs later, from inside a build) can open its own DI scope.
        ServiceProvider? built = null;

        var mock = new Mock<IAiRouter>();
        mock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AiRequest req, CancellationToken _) =>
            {
                lock (requests) requests.Add(req);

                onRequest?.Invoke(req, built!);

                // Mirror production: the answer names the model the STAMPED tier resolves to.
                var (provider, model) = LinguisticModelResolver.ResolveForTask(
                    BuildOptions(), req.TaskType, req.Language, req.Tier ?? AiTier.Fast);

                var content = req.TaskType == AiTaskType.Summarization ? BriefJson : MetricsJson;
                return Task.FromResult(new AiResponse
                {
                    Content = content,
                    Model = model ?? "",
                    Provider = provider
                });
            });
        routerMock = mock;

        services.AddSingleton(mock.Object);
        services.Configure<AiOptions>(o =>
        {
            var source = BuildOptions();
            o.FeatureModels = source.FeatureModels;
            o.DefaultProvider = source.DefaultProvider;
            o.DefaultModel = source.DefaultModel;
            if (maxParallelChapters.HasValue)
                o.MaxParallelStyleBaselineChapters = maxParallelChapters.Value;
        });

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<StyleBaselineService>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<StyleBaselineBuildRegistry>();

        built = services.BuildServiceProvider();
        return built;
    }

    internal static AiOptions BuildOptions() => new()
    {
        DefaultProvider = LocalProvider,
        DefaultModel = "default-model",
        FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.Ordinal)
        {
            ["LinguisticAnalysis"] =
                new FeatureModelOptions { Provider = LocalProvider, Model = LocalLinguisticModel },
            ["LinguisticAnalysis_thinking"] =
                new FeatureModelOptions { Provider = CloudProvider, Model = CloudLinguisticModel },
            // Summarization is NOT allowlisted, so it deliberately has no _thinking counterpart. That
            // absence is the whole reason the ChunkSummary dual-surface trap cannot fire.
            ["Summarization"] =
                new FeatureModelOptions { Provider = LocalProvider, Model = SummarizationModel }
        }
    };

    /// <summary>Seeds a book with <paramref name="chapterCount"/> analysable chapters.</summary>
    internal static async Task<(Guid BookId, List<Guid> ChapterIds)> SeedBookAsync(
        AppDbContext db, string title, string? storedTier, int chapterCount)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = title, Language = Lang, AiTier = storedTier });

        var chapterIds = new List<Guid>();
        for (var i = 0; i < chapterCount; i++)
        {
            var chapterId = Guid.NewGuid();
            chapterIds.Add(chapterId);
            db.Chapters.Add(new Chapter
            {
                Id = chapterId,
                BookId = bookId,
                Order = i,
                Title = $"Ch{i}",
                ContentText = $"תוכן פרק {i} לניתוח לשוני."
            });
        }

        await db.SaveChangesAsync();
        return (bookId, chapterIds);
    }

    /// <summary>Flips a book's stored tier through the same column the production read path uses.</summary>
    internal static async Task SetTierAsync(AppDbContext db, Guid bookId, string? storedTier)
    {
        var book = await db.Books.FirstAsync(b => b.Id == bookId);
        book.AiTier = storedTier;
        await db.SaveChangesAsync();
    }

    internal static int LlmCalls(List<AiRequest> captured)
    {
        lock (captured) return captured.Count;
    }
}

/// <summary>
/// POSITIVE STALENESS (model-tier-fast-thinking plan, p3-3). Flipping a book's tier must make THAT BOOK's
/// LinguisticAnalysis provenance read stale, restamp it on rebuild, and leave every other book alone.
///
/// THERE IS NO INVALIDATION ENGINE HERE, and that is the design. p3-1 established that the cross-model gate
/// (<see cref="ChapterStyleProfileFreshness.IsFresh"/>, one definition, five entities) is already a per-row
/// <c>BuiltWithModel == activeModel</c> comparison with a rendered Refresh affordance, so the ONLY change
/// p3-3 makes is that <c>activeModel</c> becomes a function of the BOOK's tier - in exactly two places,
/// <c>AnalysisContextService.ActiveLinguisticModelFor</c> and
/// <c>StyleBaselineService.ResolveLinguisticProviderAndModel</c>. These tests assert the consequence rather
/// than the mechanism, so they stay honest if the mechanism is refactored.
///
/// Class named *AiTier* so the standing deterministic filter picks it up.
/// </summary>
public class AiTierStalenessTests
{
    /// <summary>
    /// THE PRIMARY ASSERTION. A book built on the fast tier and then moved to thinking reads STALE on every
    /// surface the FE renders: the per-chapter count, the persisted book baseline's cross-model flag, and the
    /// <c>ActiveModel</c> the status DTO advertises. A SECOND book on the same database is untouched, which is
    /// the property per-book scope was chosen for (p3-1 option B: the tier's unit and the invalidation unit
    /// coincide).
    /// </summary>
    [Fact]
    public async Task FlippingOneBooksTier_MakesThatBookStale_AndLeavesEveryOtherBookFresh()
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var (bookX, _) = await AiTierTestHarness.SeedBookAsync(db, "X", storedTier: null, chapterCount: 3);
        var (bookY, _) = await AiTierTestHarness.SeedBookAsync(db, "Y", storedTier: null, chapterCount: 3);

        Assert.True((await svc.BuildBookStyleBaselineAsync(bookX, AiTierTestHarness.Lang)).Ready);
        Assert.True((await svc.BuildBookStyleBaselineAsync(bookY, AiTierTestHarness.Lang)).Ready);

        // Both books are fully fresh on the local tier before the flip.
        var beforeX = await svc.GetStatusAsync(bookX, AiTierTestHarness.Lang);
        Assert.Equal(0, beforeX.StaleCount);
        Assert.True(beforeX.IsReady);
        Assert.Equal(AiTierTestHarness.LocalLinguisticModel, beforeX.ActiveModel);

        await AiTierTestHarness.SetTierAsync(db, bookX, AiTierPolicy.ThinkingStoredValue);

        var afterX = await svc.GetStatusAsync(bookX, AiTierTestHarness.Lang);
        Assert.Equal(AiTierTestHarness.CloudLinguisticModel, afterX.ActiveModel);
        Assert.Equal(3, afterX.StaleCount);
        Assert.Equal(0, afterX.BuiltChapters);
        Assert.True(afterX.BuiltWithDifferentModel);
        Assert.Equal(AiTierTestHarness.LocalLinguisticModel, afterX.BuiltWithModel);
        Assert.False(afterX.IsReady);

        // Book Y never opted in, so nothing about it moved.
        var afterY = await svc.GetStatusAsync(bookY, AiTierTestHarness.Lang);
        Assert.Equal(AiTierTestHarness.LocalLinguisticModel, afterY.ActiveModel);
        Assert.Equal(0, afterY.StaleCount);
        Assert.False(afterY.BuiltWithDifferentModel);
        Assert.True(afterY.IsReady);
    }

    /// <summary>
    /// THE ATOMIC PAIRING, asserted as one behaviour. p3-2 deliberately did NOT stamp
    /// <c>AnalysisContextService.ComputeChapterLinguisticMetricsAsync</c>, because stamping the request while
    /// the freshness gate still resolved the LOCAL active model would leave every thinking-tier profile
    /// PERMANENTLY stale - one extra LLM call per chapter per analysis, forever. This test is the proof that
    /// both halves landed together: after a rebuild on the thinking tier the profiles carry the CLOUD model,
    /// the status reads fully fresh, and a second build is a no-op that makes ZERO further model calls.
    ///
    /// It fails in BOTH directions of the pairing. Remove the request stamp and the mock answers with the
    /// local model, so <c>BuiltWithModel</c> never matches the cloud active model and StaleCount stays 3.
    /// Revert the gate to the untiered active model and the cloud stamp never matches the local active model,
    /// with the same visible result. Only the pair passing produces a fresh, idempotent baseline.
    /// </summary>
    [Fact]
    public async Task ARebuildOnTheThinkingTier_RestampsTheProvenance_AndIsNotPermanentlyStale()
    {
        using var provider = AiTierTestHarness.Build(out var captured, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var (bookId, _) = await AiTierTestHarness.SeedBookAsync(
            db, "Thinking Book", AiTierPolicy.ThinkingStoredValue, chapterCount: 3);

        var build = await svc.BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang);
        Assert.True(build.Ready);
        Assert.Equal(3, build.BuiltChapters);

        // Every rebuild request carried the book's tier (the p3-2 stamp this todo added to the baseline path).
        Assert.Equal(3, AiTierTestHarness.LlmCalls(captured));
        Assert.All(captured, r => Assert.Equal(AiTier.Thinking, r.Tier));
        Assert.All(captured, r => Assert.Equal(AiTaskType.LinguisticAnalysis, r.TaskType));

        // ... and the provenance stamped on both entities is the TIER-RESOLVED model, not the local default.
        var profiles = await db.ChapterStyleProfiles.AsNoTracking()
            .Where(p => p.BookId == bookId).ToListAsync();
        Assert.Equal(3, profiles.Count);
        Assert.All(profiles, p => Assert.Equal(AiTierTestHarness.CloudLinguisticModel, p.BuiltWithModel));

        var baseline = await db.BookStyleBaselines.AsNoTracking().SingleAsync(b => b.BookId == bookId);
        Assert.Equal(AiTierTestHarness.CloudLinguisticModel, baseline.BuiltWithModel);

        // THE PERMANENT-STALENESS CHECK: the gate agrees with what was just built.
        var status = await svc.GetStatusAsync(bookId, AiTierTestHarness.Lang);
        Assert.Equal(0, status.StaleCount);
        Assert.False(status.BuiltWithDifferentModel);
        Assert.True(status.IsReady);

        // ... and therefore a second build is an idempotent no-op rather than a per-chapter LLM storm.
        var second = await svc.BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang);
        Assert.True(second.NoOp);
        Assert.Equal(3, AiTierTestHarness.LlmCalls(captured));
    }

    /// <summary>
    /// The INLINE read path (<c>AnalysisContextService.LoadOrBuildChapterStyleProfileAsync</c>), which is the
    /// other of the two activeModel sites and the one an ordinary scene analysis hits. A profile built on the
    /// fast tier is rebuilt exactly once after the flip, restamped with the cloud model, and then served from
    /// cache without further model calls. Without the tier-aware gate the first assertion fails (no rebuild);
    /// without the request stamp the last one fails (it rebuilds forever).
    /// </summary>
    [Fact]
    public async Task TheInlineProfilePath_RebuildsOnceAfterATierFlip_ThenServesFromCache()
    {
        using var provider = AiTierTestHarness.Build(out var captured, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var context = provider.GetRequiredService<IAnalysisContextService>();

        var (bookId, chapterIds) = await AiTierTestHarness.SeedBookAsync(
            db, "Inline Book", storedTier: null, chapterCount: 1);
        var chapterId = chapterIds[0];

        var onFast = await context.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, AiTierTestHarness.Lang);
        Assert.NotNull(onFast);
        Assert.Equal(AiTierTestHarness.LocalLinguisticModel, onFast!.BuiltWithModel);
        Assert.Equal(1, AiTierTestHarness.LlmCalls(captured));

        // Cache hit on the same tier: no second call.
        await context.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, AiTierTestHarness.Lang);
        Assert.Equal(1, AiTierTestHarness.LlmCalls(captured));

        await AiTierTestHarness.SetTierAsync(db, bookId, AiTierPolicy.ThinkingStoredValue);

        var onThinking = await context.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, AiTierTestHarness.Lang);
        Assert.NotNull(onThinking);
        Assert.Equal(AiTierTestHarness.CloudLinguisticModel, onThinking!.BuiltWithModel);
        Assert.Equal(2, AiTierTestHarness.LlmCalls(captured));

        // The rebuild SETTLED. A third read on the thinking tier is a cache hit, not another rebuild.
        await context.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, AiTierTestHarness.Lang);
        Assert.Equal(2, AiTierTestHarness.LlmCalls(captured));
    }

    /// <summary>
    /// The read-only book-average aggregator (<c>BuildBookStyleAverageProfileAsync</c>), the THIRD consumer of
    /// the active model inside <c>AnalysisContextService</c>. It never rebuilds - it EXCLUDES cross-model rows
    /// - so if its active model were left untiered it would silently drop every profile a thinking-tier book
    /// just built and return null, removing [CHAPTER_STYLE_BASELINE] from the prompt with no error anywhere.
    /// </summary>
    [Fact]
    public async Task TheBookAverage_AggregatesProfilesBuiltOnTheBooksOwnTier()
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();
        var context = provider.GetRequiredService<IAnalysisContextService>();

        var (bookId, _) = await AiTierTestHarness.SeedBookAsync(
            db, "Average Book", AiTierPolicy.ThinkingStoredValue, chapterCount: 3);
        Assert.True((await svc.BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang)).Ready);

        var average = await context.BuildBookStyleAverageProfileAsync(bookId, AiTierTestHarness.Lang);
        Assert.NotNull(average);
        Assert.False(string.IsNullOrWhiteSpace(average!.MetricsJson));

        // Flipping back to fast makes those same cloud-built rows cross-model, so the average degrades to
        // null rather than serving an apples-to-oranges reference. Same gate, opposite direction.
        await AiTierTestHarness.SetTierAsync(db, bookId, AiTierPolicy.FastStoredValue);
        Assert.Null(await context.BuildBookStyleAverageProfileAsync(bookId, AiTierTestHarness.Lang));
    }

    /// <summary>
    /// be-c02 (pre-PR review P1-2): THE MID-BUILD TIER FLIP, which is the failure the "resolve the tier ONCE
    /// per build" comment in <c>StyleBaselineService.BuildBookStyleBaselineAsync</c> claimed to prevent while
    /// two of the sites it named still re-read <c>Book.AiTier</c> for themselves.
    ///
    /// A baseline build takes minutes (the feature's own consent screen quotes ~3 for 5 chapters), so a user
    /// flipping the tier while it runs is ordinary, not exotic. Unthreaded, the first chapter is built and
    /// stamped under the OLD tier's model and the second under the NEW one; the average - which never
    /// rebuilds, only EXCLUDES cross-model rows - is then computed at the new tier, drops the first chapter,
    /// falls below the two-profile minimum and returns null. That null short-circuits the persist, so the
    /// build produces NOTHING while the post-status (correctly computed at the old tier) blames the chapters
    /// it just built and reports one failed. No exception, no log, no user-visible signal.
    ///
    /// The flip is driven by a ONE-SHOT SIDE EFFECT inside the router rather than by a thread, so it lands at
    /// a known point in the build (the same idiom as the tracker suite's side-effecting clock), and the
    /// chapter cap is set to 1 so "between chapter 1 and chapter 2" is enforced rather than hoped for.
    ///
    /// It fails at THREE independent assertions against the unthreaded code, in escalating order of how
    /// visible the damage is: two models on the profiles, no baseline row persisted, and FailedChapters == 1.
    /// </summary>
    [Fact]
    public async Task ATierFlipMidBuild_StampsEveryChapterUnderOneModel_AndStillPersistsTheBaseline()
    {
        var flipped = 0;
        var bookId = Guid.Empty;

        using var provider = AiTierTestHarness.Build(
            out var captured,
            out _,
            onRequest: (_, sp) =>
            {
                // ONE-SHOT: only the FIRST chapter's model call flips the stored tier, so the flip is
                // strictly between chapter 1 and chapter 2. Its own DI scope -> its own DbContext, which is
                // how the production flip (an HTTP PUT on another connection) reaches this build too.
                if (Interlocked.Exchange(ref flipped, 1) != 0)
                    return;

                using var scope = sp.CreateScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                AiTierTestHarness
                    .SetTierAsync(scopedDb, bookId, AiTierPolicy.ThinkingStoredValue)
                    .GetAwaiter().GetResult();
            },
            maxParallelChapters: 1);

        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        (bookId, _) = await AiTierTestHarness.SeedBookAsync(
            db, "Mid-build Flip Book", AiTierPolicy.FastStoredValue, chapterCount: 2);

        var result = await svc.BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang);

        // GUARD THE GUARD: the flip really happened, really landed mid-build, and really stuck. Without
        // this the three assertions below could pass because nothing ever changed.
        Assert.Equal(1, flipped);
        Assert.Equal(2, AiTierTestHarness.LlmCalls(captured));
        Assert.Equal(
            AiTierPolicy.ThinkingStoredValue,
            (await db.Books.AsNoTracking().SingleAsync(b => b.Id == bookId)).AiTier);

        // 1. ONE MODEL. Every chapter of one build is routed to, and stamped with, the tier the build
        // started under - the flip applies to the NEXT build, not to the middle of this one.
        var profiles = await db.ChapterStyleProfiles.AsNoTracking()
            .Where(p => p.BookId == bookId).ToListAsync();
        Assert.Equal(2, profiles.Count);
        var distinctModels = profiles.Select(p => p.BuiltWithModel).Distinct().ToList();
        Assert.True(
            distinctModels.Count == 1,
            "A tier flip mid-build stamped this build's chapters under MORE THAN ONE model: " +
            string.Join(", ", distinctModels) +
            ". The tier must be resolved once per build and threaded to every chapter's profile build.");
        // ... and the cause, one layer down: every request was ROUTED at the build's tier.
        Assert.All(captured, r => Assert.Equal(AiTier.Fast, r.Tier));

        // 2. THE SILENT NULL. The average is computed at the tier the profiles were built under, so it
        // still sees both of them and the baseline is actually persisted.
        var baseline = await db.BookStyleBaselines.AsNoTracking()
            .SingleOrDefaultAsync(b => b.BookId == bookId && b.Language == AiTierTestHarness.Lang);
        Assert.True(
            baseline != null && !string.IsNullOrWhiteSpace(baseline.MetricsJson),
            "The book average excluded the chapters this build just wrote, so NO BookStyleBaseline was " +
            "persisted. The build spent its full cost and stored nothing, with no error and no log.");
        Assert.True(result.Ready, "The build reported not-ready after successfully building every chapter.");

        // 3. ... and it does not blame the chapters for the flip.
        Assert.Equal(0, result.FailedChapters);
        Assert.Equal(2, result.BuiltChapters);
    }

    /// <summary>
    /// The build's COST ESTIMATE moves with the tier too. It is derived from the resolved PROVIDER, so a
    /// thinking-tier book must quote a paid figure on the consent screen. Quoting the local (free, null)
    /// estimate for a run that will actually bill OpenRouter is a user-facing lie, not a rounding error.
    /// </summary>
    [Fact]
    public async Task TheBuildEstimate_IsPricedForTheTierTheBuildWouldActuallyRunOn()
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var (bookId, _) = await AiTierTestHarness.SeedBookAsync(db, "Estimate Book", storedTier: null, chapterCount: 2);

        var onFast = await svc.GetStatusAsync(bookId, AiTierTestHarness.Lang);
        Assert.Null(onFast.EstimatedUsd); // Ollama is a local/free provider

        await AiTierTestHarness.SetTierAsync(db, bookId, AiTierPolicy.ThinkingStoredValue);

        var onThinking = await svc.GetStatusAsync(bookId, AiTierTestHarness.Lang);
        Assert.NotNull(onThinking.EstimatedUsd);
        Assert.True(onThinking.EstimatedUsd > 0m);
    }

    /// <summary>
    /// FAIL-SAFE DIRECTION on the staleness surface, mirroring the stamping-side guarantee p3-2 pinned. An
    /// unrecognised stored token resolves to Fast, so a hand-edited or newer-build value can never silently
    /// invalidate a book's whole baseline (and can never quote a paid estimate for a local run).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("Thinking-2")]
    public async Task AnUnrecognisedStoredTier_LeavesTheBaselineFresh(string? stored)
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var (bookId, _) = await AiTierTestHarness.SeedBookAsync(db, "Fail-safe Book", storedTier: null, chapterCount: 2);
        Assert.True((await svc.BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang)).Ready);

        await AiTierTestHarness.SetTierAsync(db, bookId, stored);

        var status = await svc.GetStatusAsync(bookId, AiTierTestHarness.Lang);
        Assert.Equal(AiTierTestHarness.LocalLinguisticModel, status.ActiveModel);
        Assert.Equal(0, status.StaleCount);
        Assert.False(status.BuiltWithDifferentModel);
        Assert.Null(status.EstimatedUsd);
    }

    /// <summary>
    /// The three services that resolve a book's tier must all get the SAME answer, or the freshness gate
    /// compares a stamp made under one tier against an active model resolved under another and the profiles
    /// read stale forever. p3-3 made that structural by routing all three through
    /// <see cref="BookAiTierResolver"/>; this asserts the shared lookup's contract directly, including the
    /// fail-safe cases that must never reach the cloud.
    /// </summary>
    [Theory]
    [InlineData("thinking", AiTier.Thinking)]
    [InlineData("THINKING", AiTier.Thinking)]
    [InlineData("  thinking  ", AiTier.Thinking)]
    [InlineData("fast", AiTier.Fast)]
    [InlineData(null, AiTier.Fast)]
    [InlineData("banana", AiTier.Fast)]
    public async Task TheSharedBookTierLookup_ParsesTheStoredColumnDefensively(string? stored, AiTier expected)
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, _) = await AiTierTestHarness.SeedBookAsync(db, "Lookup Book", stored, chapterCount: 0);

        // tier-ux-rework c1: the lookup is per (book, TASK) and the task is required. LinguisticAnalysis is
        // the task the two freshness consumers ask about, so it is the one whose answer this file is about.
        Assert.Equal(expected, await BookAiTierResolver.ResolveAsync(
            db, bookId, AiTaskType.LinguisticAnalysis,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None));
    }

    /// <summary>A book id that resolves to nothing must mean LOCAL, never paid cloud.</summary>
    [Fact]
    public async Task TheSharedBookTierLookup_FailsSafeToLocal_ForAMissingOrEmptyBookId()
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        // Asserted for EVERY task, not just LinguisticAnalysis: per-task storage multiplied the number of
        // ways this lookup can be asked, and the fail-safe direction has to hold on all of them.
        foreach (var task in Enum.GetValues<AiTaskType>())
        {
            Assert.Equal(AiTier.Fast, await BookAiTierResolver.ResolveAsync(db, null, task, log, CancellationToken.None));
            Assert.Equal(AiTier.Fast, await BookAiTierResolver.ResolveAsync(db, Guid.Empty, task, log, CancellationToken.None));
            Assert.Equal(AiTier.Fast, await BookAiTierResolver.ResolveAsync(db, Guid.NewGuid(), task, log, CancellationToken.None));
        }
    }
}

/// <summary>
/// NEGATIVE / NON-TRIGGERING (model-tier-fast-thinking plan, p3-3). The tier's blast radius must stop exactly
/// where p2-4's allowlist stops, and the NON-triggering is itself the deliverable - "it cannot happen" is a
/// claim, and an unasserted claim is how it stops being true.
///
/// WHAT THESE PROTECT: the <c>ChunkSummary</c> DUAL-SURFACE TRAP (memory
/// <c>pagedraft-chunksummary-dual-surface</c>). ONE row carries a flat <c>SummaryText</c> AND a structured
/// <c>StructuredJson</c>, sharing a single <c>Language</c> column but keeping SEPARATE freshness stamps
/// (<c>CreatedAt</c> vs <c>StructuredBuiltAt</c>), written by TWO services. Any invalidation that clears one
/// surface can orphan the other's locale or freshness, and clearing the flat text without also resetting its
/// companion guard (<c>SummaryUserEdited</c>/<c>At</c>) makes the automatic re-summary SKIP the row forever
/// to "preserve" an edit that is now an empty string.
///
/// WHY IT CANNOT FIRE TODAY: that row is keyed on the SUMMARIZATION model, and <c>Summarization</c> is not in
/// <see cref="AiTierPolicy.TieredTasks"/>, so a tier flip does not move the active model the structured gate
/// compares against, so nothing is ever declared stale, so neither writer runs.
/// </summary>
public class AiTierNonTriggeringTests
{
    /// <summary>Every column of the row, captured so a comparison is byte-for-byte rather than field-by-eye.</summary>
    private sealed record ChunkSummarySnapshot(
        string SummaryText,
        string? StructuredJson,
        string Language,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StructuredBuiltAt,
        string? BuiltWithModel,
        bool SummaryUserEdited,
        DateTimeOffset? SummaryUserEditedAt);

    private static async Task<ChunkSummarySnapshot> SnapshotAsync(AppDbContext db, Guid chapterId)
    {
        var row = await db.ChunkSummaries.AsNoTracking().SingleAsync(cs => cs.ChapterId == chapterId);
        return new ChunkSummarySnapshot(
            row.SummaryText, row.StructuredJson, row.Language, row.CreatedAt,
            row.StructuredBuiltAt, row.BuiltWithModel, row.SummaryUserEdited, row.SummaryUserEditedAt);
    }

    /// <summary>
    /// Seeds a book on the THINKING tier whose single chapter already has a fully-populated ChunkSummary:
    /// a USER-EDITED flat summary with its guard set, plus a fresh structured brief stamped with the active
    /// Summarization model. Both surfaces are therefore live and mutually entangled, which is the state the
    /// dual-surface trap is dangerous in.
    /// </summary>
    private static async Task<(Guid BookId, Guid ChapterId)> SeedDualSurfaceAsync(AppDbContext db)
    {
        var (bookId, chapterIds) = await AiTierTestHarness.SeedBookAsync(
            db, "Dual Surface Book", AiTierPolicy.ThinkingStoredValue, chapterCount: 2);
        var chapterId = chapterIds[0];

        var chapterUpdatedAt = await db.Chapters.AsNoTracking()
            .Where(c => c.Id == chapterId).Select(c => c.UpdatedAt).SingleAsync();

        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = AiTierTestHarness.Lang,
            SummaryText = "סיכום שהמשתמש ערך בעצמו.",
            SummaryUserEdited = true,
            SummaryUserEditedAt = chapterUpdatedAt.AddMinutes(1),
            StructuredJson = AiTierTestHarness.BriefJson,
            BuiltWithModel = AiTierTestHarness.SummarizationModel,
            StructuredBuiltAt = chapterUpdatedAt.AddMinutes(2)
        });
        await db.SaveChangesAsync();

        return (bookId, chapterId);
    }

    /// <summary>
    /// THE DUAL-SURFACE NON-TRIGGERING PIN. All EIGHT content columns are byte-identical across a full round
    /// of allowlisted work on a thinking-tier book - the heavy style-baseline build AND a direct structured
    /// chapter-brief read, which is the exact call that would rebuild the row (and could hit the language-flip
    /// clear branch) if Summarization were ever tiered.
    ///
    /// REVERSAL CONDITION - READ THIS BEFORE ADDING Summarization TO <see cref="AiTierPolicy.TieredTasks"/>.
    /// The moment Summarization becomes tiered, this test SHOULD start failing, and the failure is real work,
    /// not a test to relax:
    ///   1. <c>ChapterBriefService.ActiveSummarizationModel</c> must become book-tier-aware in the same change
    ///      that stamps <c>AiRequest.Tier</c> on <c>ComputeChapterBriefAsync</c>'s request - the same ATOMIC
    ///      PAIRING p3-3 performed for LinguisticAnalysis. Half of it alone leaves every thinking-tier book's
    ///      briefs permanently stale, at one Summarization call per chapter per access.
    ///   2. The dual-surface reconciliation in <c>ChapterBriefService.LoadOrBuildChapterBriefAsync</c>
    ///      (the language-flip branch that clears the flat <c>SummaryText</c>) and the mirrored clear in
    ///      <c>BooksController</c>'s PUT-summary path become reachable on a tier flip. EVERY site that clears
    ///      the flat text must ALSO reset <c>SummaryUserEdited</c> and <c>SummaryUserEditedAt</c> together, or
    ///      the automatic re-summary skips the row forever to preserve an edit that is now an empty string and
    ///      the re-derive endpoint answers 409.
    ///   3. <c>BookSummaryBaseline.BuiltWithModel</c> (the L2 rollup) is keyed on the SAME Summarization model,
    ///      so it becomes a fourth tier-invalidated entity and needs its own positive staleness test.
    /// </summary>
    [Fact]
    public async Task ATierFlip_LeavesEveryChunkSummaryColumnByteIdentical()
    {
        using var provider = AiTierTestHarness.Build(out var captured, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedDualSurfaceAsync(db);

        var before = await SnapshotAsync(db, chapterId);

        // A full round of ALLOWLISTED work on this thinking-tier book.
        Assert.True((await provider.GetRequiredService<StyleBaselineService>()
            .BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang)).Ready);

        // ... plus the read that would rebuild the row if the Summarization gate had moved.
        var brief = await provider.GetRequiredService<ChapterBriefService>()
            .LoadOrBuildChapterBriefAsync(bookId, chapterId, AiTierTestHarness.Lang);
        Assert.NotNull(brief);
        Assert.Equal("seeded tone", brief!.ToneNotes); // the SEEDED brief, not a rebuilt one

        Assert.Equal(before, await SnapshotAsync(db, chapterId));

        // And no Summarization request was made at all - the row was not merely rewritten with equal values.
        Assert.DoesNotContain(captured, r => r.TaskType == AiTaskType.Summarization);
    }

    /// <summary>
    /// THE USER-EDIT CLOBBER GUARD, asserted on its own because it is the half of the dual-surface trap with
    /// the worst failure mode: the user's own prose is the authoritative flat summary, and a tier-driven clear
    /// that reset the text without resetting the guard (or reset the guard without the text) leaves the row in
    /// a state no later path can repair. A tier flip must not touch any of the three.
    /// </summary>
    [Fact]
    public async Task ATierFlip_CannotClobberAUserEditedSummary_NorItsCompanionGuard()
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedDualSurfaceAsync(db);

        var before = await SnapshotAsync(db, chapterId);
        Assert.True(before.SummaryUserEdited);
        Assert.NotNull(before.SummaryUserEditedAt);
        Assert.False(string.IsNullOrWhiteSpace(before.SummaryText));

        // Flip the tier BACK, so the book crosses the boundary in both directions during its lifetime.
        await AiTierTestHarness.SetTierAsync(db, bookId, AiTierPolicy.FastStoredValue);
        Assert.True((await provider.GetRequiredService<StyleBaselineService>()
            .BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang)).Ready);
        await AiTierTestHarness.SetTierAsync(db, bookId, AiTierPolicy.ThinkingStoredValue);
        Assert.True((await provider.GetRequiredService<StyleBaselineService>()
            .BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang)).Ready);

        var after = await SnapshotAsync(db, chapterId);
        Assert.Equal(before.SummaryText, after.SummaryText);
        Assert.True(after.SummaryUserEdited);
        Assert.Equal(before.SummaryUserEditedAt, after.SummaryUserEditedAt);
        // The structured surface's locale and freshness were not orphaned either.
        Assert.Equal(before.Language, after.Language);
        Assert.Equal(before.StructuredJson, after.StructuredJson);
        Assert.Equal(before.StructuredBuiltAt, after.StructuredBuiltAt);
        Assert.Equal(before.BuiltWithModel, after.BuiltWithModel);
    }

    /// <summary>
    /// <c>BookSummaryBaseline</c> (keyed on Summarization) and <c>BookFinding</c> (keyed on BookReview) are the
    /// two provenance entities p3-1 put explicitly OUT of the GO'd scope. Assert they are untouched rather
    /// than modifying them - a tier flip that quietly restamped either would mean the allowlist leaked.
    /// </summary>
    [Fact]
    public async Task ATierFlip_LeavesBookSummaryBaselineAndBookFindingUntouched()
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, _) = await AiTierTestHarness.SeedBookAsync(db, "Out Of Scope Book", storedTier: null, chapterCount: 2);

        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId,
            Language = AiTierTestHarness.Lang,
            BookBriefJson = "{}",
            BuiltChapterCount = 2,
            BuiltWithModel = AiTierTestHarness.SummarizationModel
        });
        db.BookFindings.Add(new BookFinding
        {
            BookId = bookId,
            Language = AiTierTestHarness.Lang,
            Dimension = "pacing",
            Verdict = "needs-work",
            Severity = 2,
            Rationale = "נימוק",
            EvidenceJson = "[]",
            ChapterAnchorsJson = "[1]",
            DedupKey = "k1",
            BuiltWithModel = "review-model"
        });
        await db.SaveChangesAsync();

        await AiTierTestHarness.SetTierAsync(db, bookId, AiTierPolicy.ThinkingStoredValue);
        Assert.True((await provider.GetRequiredService<StyleBaselineService>()
            .BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang)).Ready);

        Assert.Equal(
            AiTierTestHarness.SummarizationModel,
            (await db.BookSummaryBaselines.AsNoTracking().SingleAsync(b => b.BookId == bookId)).BuiltWithModel);
        Assert.Equal(
            "review-model",
            (await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId)).BuiltWithModel);
    }

    /// <summary>
    /// THE REASON the tests above pass, stated as a property of the SHIPPED config rather than of the test
    /// harness. The Summarization and BookReview active models - the comparison targets behind
    /// <c>ChunkSummary</c>, <c>BookSummaryBaseline</c> and <c>BookFinding</c> - are identical on both tiers,
    /// because neither task is allowlisted. If this ever goes red, the byte-identity tests above are no longer
    /// guaranteed by construction and their reversal condition has come due.
    /// </summary>
    [Fact]
    public void TheProvenanceKeysOutsideTheAllowlist_AreTierInvariant_InTheShippedConfig()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();

        foreach (var task in new[] { AiTaskType.Summarization, AiTaskType.BookReview })
        {
            Assert.False(AiTierPolicy.IsTiered(task),
                $"{task} entered AiTierPolicy.TieredTasks. Read the REVERSAL CONDITION on " +
                "ATierFlip_LeavesEveryChunkSummaryColumnByteIdentical before changing that.");

            var fast = LinguisticModelResolver.ResolveForTask(opt, task, language: null, AiTier.Fast);
            var thinking = LinguisticModelResolver.ResolveForTask(opt, task, language: null, AiTier.Thinking);
            Assert.Equal(fast, thinking);
        }
    }

    /// <summary>
    /// The whole-book CONTEXT BUDGET is a second, non-staleness blast radius p3-1 flagged (§6 correction 1):
    /// <c>BookContextAssembler</c> builds the <c>{Provider}_{TaskType}</c> tuning key from the RESOLVED
    /// PROVIDER, so a tier that moves the provider can move num_ctx and the output reservation with it.
    /// <c>AssembleAsync</c> still sizes at <see cref="AiTier.Fast"/>, which is currently INERT because the
    /// shipped Ollama and OpenRouter LinguisticAnalysis entries declare the SAME window and the SAME output
    /// cap. That is a fact about the config, not a guarantee, so it is pinned: if it goes red, the book-scope
    /// assembly path must start threading the book's tier before the tier can ship for that scope.
    /// </summary>
    [Fact]
    public void TheWholeBookBudget_IsUnmovedByTheTier_AtTheShippedValues()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        const AiTaskType task = AiTaskType.LinguisticAnalysis;

        Assert.Equal(
            BookContextAssembler.ResolveNumCtxForTask(opt, task, language: null, AiTier.Fast),
            BookContextAssembler.ResolveNumCtxForTask(opt, task, language: null, AiTier.Thinking));
        Assert.Equal(
            BookContextAssembler.ResolveOutputReserveForTask(opt, task, language: null, AiTier.Fast),
            BookContextAssembler.ResolveOutputReserveForTask(opt, task, language: null, AiTier.Thinking));

        // Not vacuous: the tier really does move the ROUTE for this task in the shipped config.
        Assert.NotEqual(
            LinguisticModelResolver.ResolveForTask(opt, task, language: null, AiTier.Fast),
            LinguisticModelResolver.ResolveForTask(opt, task, language: null, AiTier.Thinking));
    }
}

/// <summary>
/// PROOFREAD'S ZERO STALENESS IMPACT (model-tier-fast-thinking plan, p3-3 item 4).
///
/// Half the GO'd scope is invalidation-free, and that is a load-bearing part of why per-book scope was safe
/// to choose (p3-1 §1e): Proofread has NO provenance cache. Its only model stamp is
/// <c>AnalysisResult.ModelName</c> / <c>AnalysisRunLog.ModelName</c>, which is written for display and
/// compared by NOTHING - there is no Proofread freshness gate, no Proofread rebuild, and no Proofread
/// staleness DTO.
///
/// This test exists so that stops being an observation somebody made once. Adding a freshness gate keyed on
/// <c>ModelName</c> - the natural thing to reach for the next time someone wants Proofread results
/// invalidated - turns it RED, which forces the author to notice that <c>ModelName</c> is a per-RUN string
/// (it is literally the constant <c>"chunked"</c> on both chunked paths, and <c>"stream"</c> on the streaming
/// one) and is therefore unusable as a cross-model provenance key without first giving Proofread a real one.
/// </summary>
public class AiTierProofreadProvenanceTests
{
    /// <summary>Files allowed to mention <c>ModelName</c> at all, with why. Anything else is a new consumer.</summary>
    private static readonly Dictionary<string, string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Models/AnalysisResult.cs"] = "the declaration",
        ["Models/AnalysisRunLog.cs"] = "the declaration",
        ["Models/SceneEmbedding.cs"] = "the declaration (no writer service exists)",
        ["Models/Dtos/AnalysisDto.cs"] = "the DTO member",
        ["Data/AppDbContext.cs"] = "the EF column configuration",
        ["Services/Ai/AiAnalysisService.cs"] = "a writer",
        ["Services/Analysis/UnifiedAnalysisService.cs"] = "the writers",
        ["Controllers/AnalysisController.cs"] = "THE single read - a DTO projection"
    };

    /// <summary>Comparison shapes that would turn a display string into a freshness key.</summary>
    private static readonly Regex ComparisonShape = new(
        @"ModelName[^;]*?(==|!=|\.Equals\(|IsFresh)|(==|!=|Equals\(|IsFresh\()[^;]*?ModelName",
        RegexOptions.Compiled);

    [Fact]
    public void AnalysisResultModelName_IsWrittenAndProjectedOnly_NeverComparedToAnActiveModel()
    {
        var offenders = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (relativePath, lineNumber, line) in ApiSourceLines())
        {
            if (!line.Contains("ModelName", StringComparison.Ordinal))
                continue;

            seenFiles.Add(relativePath);

            if (!AllowedFiles.ContainsKey(relativePath))
                offenders.Add(
                    $"{relativePath}:{lineNumber} is a NEW consumer of ModelName. It is a per-RUN display " +
                    "string (the chunked paths write the literal \"chunked\"), not a provenance key - if you " +
                    "need Proofread invalidation, give Proofread a real BuiltWithModel-style stamp first, " +
                    "then add the file here.");

            // Strip the comment tail before shape-matching: the writers carry explanatory comments that
            // legitimately spell "ModelName" next to an unrelated `==` (e.g. the jobId note beside the
            // chunked write), and flagging prose would make this guard noise rather than signal.
            var code = StripCommentTail(line);
            if (!code.Contains("ModelName", StringComparison.Ordinal))
                continue;

            if (ComparisonShape.IsMatch(code))
                offenders.Add(
                    $"{relativePath}:{lineNumber} COMPARES ModelName. Nothing compared it before, which is " +
                    "why a Proofread tier flip invalidates nothing (plan p3-1 section 1e). A gate here " +
                    $"changes that: {line.Trim()}");
        }

        static string StripCommentTail(string line)
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));

        // Guard the guard: if the scan found nothing it would pass vacuously.
        Assert.Contains("Controllers/AnalysisController.cs", seenFiles);
        Assert.Contains("Models/AnalysisResult.cs", seenFiles);
    }

    /// <summary>
    /// Every non-generated C# line of the API project, as (repo-relative path with forward slashes, 1-based
    /// line number, text). Migrations are excluded: they are EF-generated snapshots of every entity, so they
    /// mention every column and say nothing about who consumes it.
    /// </summary>
    private static IEnumerable<(string RelativePath, int LineNumber, string Line)> ApiSourceLines()
    {
        var root = FindApiProjectRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("Migrations/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                yield return (relative, i + 1, lines[i]);
        }
    }

    private static string FindApiProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Pagedraft.Api");
            if (File.Exists(Path.Combine(candidate, "Pagedraft.Api.csproj")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the Pagedraft.Api project above " + AppContext.BaseDirectory);
    }
}
