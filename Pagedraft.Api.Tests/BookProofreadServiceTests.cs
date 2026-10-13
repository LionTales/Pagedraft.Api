using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The whole-book proofread run (plan todo c5): planning (fresh skip, force, unreliable = stale, scenes),
/// the parent + child jobs in the tracker, failure and cancellation between chapters, status counts, the ETA
/// formula pinned to the accessor RunAsync chunks at, and the controller's route guards. The model is a
/// mocked <see cref="IAiRouter"/> behind the REAL <see cref="UnifiedAnalysisService"/>, so every chapter's
/// result is the same Active row a per-chapter run persists.
/// </summary>
public class BookProofreadServiceTests
{
    internal static readonly DateTimeOffset Edited = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>One book's fixture: the DI graph the service runs in, plus the texts the mocked context serves.</summary>
    internal sealed class Harness : IDisposable
    {
        public readonly Guid BookId = Guid.NewGuid();
        public readonly ServiceProvider Provider;
        public readonly AnalysisProgressTracker Tracker;
        public readonly BookProofreadBuildRegistry Registry = new();
        public readonly ConcurrentQueue<string> RouterInputs = new();
        public readonly ConcurrentDictionary<Guid, (string Text, Guid ChapterId, Guid? SceneId)> Targets = new();

        /// <summary>Called inside the model call with the request's input text; may throw to fail that unit.</summary>
        public Action<string>? OnModelCall;

        /// <summary>When set, the model's answer for the request's input text (c20: an unreliable output).</summary>
        public Func<string, string?>? Respond;

        /// <summary>
        /// Awaited inside the model call after <see cref="OnModelCall"/> (be-c11): a test holds a unit in flight on a
        /// gate (a TaskCompletionSource it releases), no sleeps; it may throw to fail that unit.
        /// </summary>
        public Func<string, Task>? ModelGate;

        /// <param name="clock">Registered for the service's ETA clock when given (c10); the system clock otherwise.</param>
        /// <param name="tracker">The singleton tracker (be-c04: an observing subclass); a plain one otherwise.</param>
        /// <param name="configure">Runs after the defaults (be-c11), e.g. to set or bind <see cref="AiOptions"/>.</param>
        public Harness(TimeProvider? clock = null, AnalysisProgressTracker? tracker = null, Action<IServiceCollection>? configure = null)
        {
            Tracker = tracker ?? new AnalysisProgressTracker();
            var dbName = Guid.NewGuid().ToString();
            var router = new Mock<IAiRouter>();
            router
                .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .Returns<AiRequest, CancellationToken>(async (req, _) =>
                {
                    RouterInputs.Enqueue(req.InputText);
                    OnModelCall?.Invoke(req.InputText);
                    if (ModelGate != null) await ModelGate(req.InputText);
                    // A one-word correction, so the diff yields a pending suggestion and the run is not "unrelated".
                    return new AiResponse
                    {
                        Content = Respond?.Invoke(req.InputText) ?? req.InputText.Replace("הבית", "הביתה"),
                        Provider = "test-provider",
                        Model = "test-model"
                    };
                });

            var context = new Mock<IAnalysisContextService>();
            context
                .Setup(c => c.BuildContextAsync(
                    It.IsAny<AnalysisScope>(), It.IsAny<Guid>(), It.IsAny<AnalysisType>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((AnalysisScope scope, Guid targetId, AnalysisType type, string _, CancellationToken _) =>
                {
                    var t = Targets[targetId];
                    return new AnalysisContext
                    {
                        TargetText = t.Text,
                        Scope = scope,
                        AnalysisType = type,
                        BookId = BookId,
                        ChapterId = t.ChapterId,
                        SceneId = t.SceneId
                    };
                });

            var lifetime = new Mock<IHostApplicationLifetime>();
            lifetime.SetupGet(l => l.ApplicationStopping).Returns(CancellationToken.None);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.Configure<AiOptions>(_ => { });
            services.AddSingleton(Tracker);
            services.AddSingleton(Registry);
            services.AddSingleton(lifetime.Object);
            services.AddScoped(sp => new UnifiedAnalysisService(
                sp.GetRequiredService<AppDbContext>(), router.Object, new PromptFactory(), new SfdtConversionService(),
                sp.GetRequiredService<IOptions<AiOptions>>(), NullLogger<UnifiedAnalysisService>.Instance,
                Tracker, context.Object, new SuggestionDiffService(),
                new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
                new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
                new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
                new StubBookEntityProvider()));
            if (clock != null) services.AddSingleton(clock);
            services.AddScoped<BookProofreadService>();
            configure?.Invoke(services);
            Provider = services.BuildServiceProvider();

            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Books.Add(new Book { Id = BookId, Title = "ספר", Language = "he" });
            db.SaveChanges();
        }

        public BookProofreadService Service() => Provider.CreateScope().ServiceProvider.GetRequiredService<BookProofreadService>();

        public AppDbContext Db() => Provider.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        /// <summary>A chapter whose text names <paramref name="marker"/>. Sync SaveChanges keeps the explicit timestamps.</summary>
        public Guid AddChapter(int order, string marker, int? wordCount = null)
        {
            var id = Guid.NewGuid();
            var text = $"הילד {marker} הלך אל הבית שלו בבוקר והיה שמח מאוד לראות את אמא.";
            using var db = Db();
            db.Chapters.Add(new Chapter
            {
                Id = id, BookId = BookId, Order = order, Title = marker, ContentText = text,
                WordCount = wordCount ?? text.Split(' ').Length, CreatedAt = Edited, UpdatedAt = Edited
            });
            db.SaveChanges();
            Targets[id] = (text, id, null);
            return id;
        }

        public Guid AddScene(Guid chapterId, int order, string marker, bool written, string? sfdt = null)
        {
            var id = Guid.NewGuid();
            var text = $"הסצנה {marker} נגמרה ליד הבית הישן של סבתא.";
            using var db = Db();
            db.Scenes.Add(new Scene
            {
                Id = id, ChapterId = chapterId, Order = order, Title = marker,
                ContentSfdt = sfdt ?? SfdtConversionService.CreateMinimalSfdtFromText(text),
                CreatedAt = Edited, UpdatedAt = written ? Edited.AddMinutes(5) : Edited
            });
            db.SaveChanges();
            Targets[id] = (text, chapterId, id);
            return id;
        }

        public void AddActiveResult(Guid chapterId, Guid? sceneId, DateTimeOffset createdAt, bool unreliable = false, string language = "he")
        {
            using var db = Db();
            db.AnalysisResults.Add(new AnalysisResult
            {
                BookId = BookId, ChapterId = chapterId, SceneId = sceneId,
                Scope = sceneId.HasValue ? AnalysisScope.Scene : AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread, Status = AnalysisStatus.Active,
                Language = language, CreatedAt = createdAt, ProofreadResultUnreliable = unreliable,
                Type = "Proofread"
            });
            db.SaveChanges();
        }

        public List<AnalysisResult> ActiveProofreads()
        {
            using var db = Db();
            return db.AnalysisResults.AsNoTracking().Include(a => a.Suggestions)
                .Where(a => a.AnalysisType == AnalysisType.Proofread && a.Status == AnalysisStatus.Active)
                .ToList();
        }

        public AnalysisProgressSnapshot Snapshot(Guid jobId)
        {
            Assert.True(Tracker.TryGet(jobId, out var snap));
            return snap!;
        }

        public void Dispose() => Provider.Dispose();
    }

    internal static async Task<StartBookProofreadResponse> StartAndFinish(Harness h, BookProofreadService svc, bool force = false)
    {
        var response = await svc.StartAsync(h.BookId, "he", force, CancellationToken.None);
        if (response.Started && svc.RunTask != null) await svc.RunTask;
        return response;
    }

    // ── Planning ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FreshChaptersAreSkipped_AndForceRunsThemAnyway()
    {
        using var h = new Harness();
        var fresh = h.AddChapter(0, "ראשון");
        var stale = h.AddChapter(1, "שני");
        h.AddActiveResult(fresh, null, Edited.AddMinutes(1));   // newer than the last edit
        h.AddActiveResult(stale, null, Edited.AddMinutes(-1));  // older than the last edit

        var first = await StartAndFinish(h, h.Service());

        Assert.True(first.Started);
        Assert.Equal(1, first.SkippedFresh);
        Assert.Equal(1, first.PlannedChapters);
        Assert.Single(h.RouterInputs);
        Assert.Contains("שני", h.RouterInputs.Single());

        var forced = await StartAndFinish(h, h.Service(), force: true);

        Assert.True(forced.Started);
        Assert.Equal(0, forced.SkippedFresh);
        Assert.Equal(2, forced.PlannedChapters);
        Assert.Equal(3, h.RouterInputs.Count);
    }

    [Fact]
    public async Task HebrewChapterTextAndTitle_FlowThroughIntact()
    {
        using var h = new Harness();
        var id = h.AddChapter(0, "ראשון");

        await StartAndFinish(h, h.Service());

        // The router received the real Hebrew-script chapter text (not mojibake) ...
        var input = Assert.Single(h.RouterInputs);
        Assert.Contains("הילד ראשון הלך אל הבית שלו", input);
        Assert.All(input, c => Assert.True(c < 0x80 || (c >= 0x05D0 && c <= 0x05EA) || c == '־', $"unexpected U+{(int)c:X4}"));
        // ... and the stored title round-trips as Hebrew letters.
        using var db = h.Db();
        Assert.Equal("ראשון", db.Chapters.Single(c => c.Id == id).Title);
    }

    [Fact]
    public async Task AnUnreliableResultIsStale_SoANonForceRunReplacesIt()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון");
        // Newer than the edit, so the time rule alone would call it fresh: only the flag makes it stale.
        h.AddActiveResult(chapter, null, Edited.AddMinutes(1), unreliable: true);

        // be-c05: an unreliable result is not coverage, and it is not "edited since": it is its own count.
        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        Assert.Equal(0, status.ChaptersWithActiveProofread);
        Assert.Equal(0, status.ChaptersEdited);
        Assert.Equal(1, status.ChaptersUnreliable);
        Assert.Equal(1, status.ChaptersStale); // kept for older clients: edited + unreliable
        Assert.Null(status.LastRunAt);         // the only result is the unreliable one

        var run = await StartAndFinish(h, h.Service());

        Assert.True(run.Started);
        Assert.Equal(0, run.SkippedFresh);
        Assert.Equal(1, run.PlannedChapters);
        var active = Assert.Single(h.ActiveProofreads());
        Assert.False(active.ProofreadResultUnreliable);
        Assert.NotEqual(Edited.AddMinutes(1), active.CreatedAt);
        var after = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        Assert.Equal((1, 0, 0, 0), (after.ChaptersWithActiveProofread, after.ChaptersEdited, after.ChaptersUnreliable, after.ChaptersStale));
    }

    [Fact]
    public async Task AResultInAnotherLanguageIsNotFresh()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון");
        h.AddActiveResult(chapter, null, Edited.AddMinutes(1), language: "en");

        var run = await StartAndFinish(h, h.Service());

        Assert.Equal(1, run.PlannedChapters);
        Assert.Equal(0, run.SkippedFresh);
    }

    [Fact]
    public async Task NothingToRun_ReturnsNotStarted_WithNoJob()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון");
        h.AddActiveResult(chapter, null, Edited.AddMinutes(1));
        var svc = h.Service();

        var response = await svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None);

        Assert.False(response.Started);
        Assert.Equal(Guid.Empty, response.JobId);
        Assert.Equal(1, response.SkippedFresh);
        Assert.Equal(0, response.PlannedChapters);
        Assert.Null(svc.RunTask);
        Assert.Null(h.Registry.TryGetActive(h.BookId, "he"));
        Assert.Empty(h.RouterInputs);
    }

    [Fact]
    public async Task ChaptersWithoutTextAreNeitherPlannedNorCounted()
    {
        using var h = new Harness();
        h.AddChapter(0, "ראשון");
        h.AddChapter(1, "ריק", wordCount: 0);

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        var run = await StartAndFinish(h, h.Service());

        Assert.Equal(1, status.ChaptersWithText);
        Assert.Equal(1, run.PlannedChapters);
        Assert.Single(h.RouterInputs);
    }

    [Fact]
    public async Task ASecondStartWhileRunning_ReturnsTheActiveJob()
    {
        using var h = new Harness();
        h.AddChapter(0, "ראשון");
        var active = Guid.NewGuid();
        Assert.True(h.Registry.TryStart(h.BookId, "he", active, out _));
        var svc = h.Service();

        var response = await svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None);

        Assert.True(response.Started);
        Assert.Equal(active, response.JobId);
        Assert.Null(svc.RunTask);
        Assert.Empty(h.RouterInputs);
    }

    // ── Parent and child jobs ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParentCountsChapters_AndEachChapterRunsAsAPlainChildProofreadJob()
    {
        using var h = new Harness();
        var a = h.AddChapter(0, "ראשון");
        var b = h.AddChapter(1, "שני");
        var seenDuringRun = new ConcurrentBag<AnalysisProgressSnapshot>();
        h.OnModelCall = _ => { foreach (var j in h.Tracker.GetActiveJobsByBook(h.BookId)) seenDuringRun.Add(j); };

        var response = await StartAndFinish(h, h.Service());

        var parent = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisScope.Book, parent.Scope);
        Assert.Equal(AnalysisType.Proofread, parent.AnalysisType);
        Assert.Equal(AnalysisProgressStatus.Succeeded, parent.Status);
        Assert.Equal(2, parent.TotalChunks);
        Assert.Equal(2, parent.CompletedChunks);

        // The active-analysis-jobs read saw ONE child per chapter, Chapter scope, never the Book-scope parent.
        Assert.DoesNotContain(seenDuringRun, j => j.JobId == response.JobId);
        Assert.Equal(new[] { a, b }.OrderBy(x => x), seenDuringRun.Select(j => j.ChapterId!.Value).Distinct().OrderBy(x => x));
        Assert.All(seenDuringRun, j =>
        {
            Assert.Equal(AnalysisScope.Chapter, j.Scope);
            Assert.Equal(AnalysisType.Proofread, j.AnalysisType);
        });

        // Each child reached Succeeded and its job id is stamped on that chapter's new Active result.
        var results = h.ActiveProofreads();
        Assert.Equal(2, results.Count);
        foreach (var r in results)
        {
            Assert.NotNull(r.JobId);
            Assert.Equal(AnalysisProgressStatus.Succeeded, h.Snapshot(r.JobId!.Value).Status);
            Assert.Contains(r.Suggestions, s => s.Outcome == null);
        }
        Assert.Null(h.Registry.TryGetActive(h.BookId, "he"));
    }

    [Fact]
    public async Task ChaptersRunInReadingOrder()
    {
        using var h = new Harness();
        h.AddChapter(2, "שלישי");
        h.AddChapter(0, "ראשון");
        h.AddChapter(1, "שני");

        await StartAndFinish(h, h.Service());

        var order = h.RouterInputs.ToList();
        Assert.Contains("ראשון", order[0]);
        Assert.Contains("שני", order[1]);
        Assert.Contains("שלישי", order[2]);
    }

    [Fact]
    public async Task AFailedChapterDoesNotAbortTheRun_AndTheParentReportsPartial()
    {
        using var h = new Harness();
        var bad = h.AddChapter(0, "שבור");
        var good = h.AddChapter(1, "תקין");
        h.OnModelCall = input => { if (input.Contains("שבור")) throw new InvalidOperationException("model down"); };

        var response = await StartAndFinish(h, h.Service());

        var parent = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Succeeded, parent.Status);
        Assert.Equal(2, parent.TotalChunks);
        Assert.Equal(1, parent.CompletedChunks); // partial: completed < total
        Assert.Contains("partially", parent.Message);

        var result = Assert.Single(h.ActiveProofreads());
        Assert.Equal(good, result.ChapterId);
        Assert.DoesNotContain(h.ActiveProofreads(), r => r.ChapterId == bad);
    }

    [Fact]
    public async Task EveryChapterFailing_FailsTheParent_AndMarksEachChildFailed()
    {
        using var h = new Harness();
        h.AddChapter(0, "ראשון");
        h.AddChapter(1, "שני");
        var children = new ConcurrentBag<Guid>();
        h.OnModelCall = _ =>
        {
            foreach (var j in h.Tracker.GetActiveJobsByBook(h.BookId)) children.Add(j.JobId);
            throw new InvalidOperationException("model down");
        };

        var response = await StartAndFinish(h, h.Service());

        Assert.Equal(AnalysisProgressStatus.Failed, h.Snapshot(response.JobId).Status);
        Assert.Equal(2, children.Distinct().Count());
        Assert.All(children.Distinct(), c => Assert.Equal(AnalysisProgressStatus.Failed, h.Snapshot(c).Status));
        Assert.Empty(h.ActiveProofreads());
    }

    [Fact]
    public async Task CancelStopsBeforeTheNextChapter_AndKeepsTheFinishedOne()
    {
        using var h = new Harness();
        var first = h.AddChapter(0, "ראשון");
        h.AddChapter(1, "שני");
        h.AddChapter(2, "שלישי");
        BookProofreadService? svc = null;
        h.OnModelCall = input =>
        {
            // The author presses stop while chapter 1 is in the model.
            if (input.Contains("ראשון"))
                Assert.True(svc!.Cancel(h.BookId, h.Registry.TryGetActive(h.BookId, "he")!.Value));
        };
        svc = h.Service();

        var response = await StartAndFinish(h, svc);

        var parent = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Canceled, parent.Status);
        Assert.Equal(3, parent.TotalChunks);
        Assert.Equal(1, parent.CompletedChunks);
        Assert.Single(h.RouterInputs);
        var kept = Assert.Single(h.ActiveProofreads());
        Assert.Equal(first, kept.ChapterId);
        Assert.Null(h.Registry.TryGetActive(h.BookId, "he"));
        Assert.False(svc.Cancel(h.BookId, response.JobId)); // nothing left to cancel
    }

    // ── Scenes ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WrittenScenesAreProofreadOneByOne_AndTheChapterCountsOnce()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "פרק");
        var s1 = h.AddScene(chapter, 0, "אחת", written: true);
        var s2 = h.AddScene(chapter, 1, "שתיים", written: false);
        h.AddScene(chapter, 2, "ריקה", written: false, sfdt: "{\"sections\":[{\"blocks\":[]}]}");

        var response = await StartAndFinish(h, h.Service());

        Assert.Equal(1, response.PlannedChapters);
        var parent = h.Snapshot(response.JobId);
        Assert.Equal(1, parent.TotalChunks);
        Assert.Equal(1, parent.CompletedChunks);

        var results = h.ActiveProofreads();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(AnalysisScope.Scene, r.Scope));
        Assert.Equal(new[] { s1, s2 }.OrderBy(x => x), results.Select(r => r.SceneId!.Value).OrderBy(x => x));
        Assert.DoesNotContain(h.RouterInputs, i => i.Contains("הילד")); // the frozen chapter row was not run

        // Both scenes now hold fresh results, so the chapter is fresh.
        var again = await h.Service().StartAsync(h.BookId, "he", force: false, CancellationToken.None);
        Assert.False(again.Started);
        Assert.Equal(1, again.SkippedFresh);
    }

    [Fact]
    public async Task UnwrittenScenes_LeaveTheChapterRowAsTheText()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "פרק");
        h.AddScene(chapter, 0, "אחת", written: false);

        await StartAndFinish(h, h.Service());

        var result = Assert.Single(h.ActiveProofreads());
        Assert.Equal(AnalysisScope.Chapter, result.Scope);
        Assert.Null(result.SceneId);
    }

    // ── Status ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StatusCountsTextCoverageStalenessAndTheNewestRun()
    {
        using var h = new Harness();
        var fresh = h.AddChapter(0, "ראשון");
        var stale = h.AddChapter(1, "שני");
        h.AddChapter(2, "שלישי");          // never proofread: not stale, just missing
        h.AddChapter(3, "ריק", wordCount: 0);
        h.AddActiveResult(fresh, null, Edited.AddMinutes(3));
        h.AddActiveResult(stale, null, Edited.AddMinutes(-3));

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);

        Assert.Null(status.ActiveJobId);
        Assert.Equal(3, status.ChaptersWithText);
        Assert.Equal(2, status.ChaptersWithActiveProofread);
        Assert.Equal(1, status.ChaptersStale);
        Assert.Equal(1, status.ChaptersEdited);     // be-c05: the stale one is EDITED since its proofread ...
        Assert.Equal(0, status.ChaptersUnreliable); // ... not unreliable
        Assert.Equal(Edited.AddMinutes(3), status.LastRunAt);
    }

    [Fact]
    public async Task StatusCarriesTheActiveRunId()
    {
        using var h = new Harness();
        h.AddChapter(0, "ראשון");
        var active = Guid.NewGuid();
        Assert.True(h.Registry.TryStart(h.BookId, "he", active, out _));

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);

        Assert.Equal(active, status.ActiveJobId);
        Assert.Null(status.LastRunAt);
    }

    // ── Estimate ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheEstimateIsCeilWordsOverTheRealChunkTarget_TimesSecondsPerChunk_PlusTheOverheadPerUnit()
    {
        using var h = new Harness();
        h.AddChapter(0, "ראשון", wordCount: 600);
        h.AddChapter(1, "שני", wordCount: 100);
        var opts = new AiOptions();
        // THE accessor RunAsync chunks at; if either side changes sizing the two cannot drift silently.
        var target = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, "he", AiTier.Fast);
        var expectedChunks = (int)Math.Ceiling(600 / (double)target) + (int)Math.Ceiling(100 / (double)target);
        Assert.True(target < 600, "fixture must span several chunks to pin the formula");

        var response = await StartAndFinish(h, h.Service());

        Assert.Equal(expectedChunks, response.EstimatedChunks);
        // be-c07: two chapter-row units, each its own run with its own time outside the chunk stage.
        Assert.Equal(2 * opts.ProofreadSecondsPerUnitOverhead + expectedChunks * opts.ProofreadSecondsPerChunkEstimate,
            response.EstimatedSeconds);
    }

    [Fact]
    public void AShortBooksEstimate_PaysTheOverheadOncePerUnit()
    {
        // The review's live case (be-c07): an English book of five short chapters, one chunk each, was promised
        // 5 chunks x the per-chunk constant ("less than a minute") for a run that takes several times that,
        // because every chapter is its own run with its own time outside the chunk stage.
        var opts = new AiOptions();
        var target = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, "en", AiTier.Fast);
        var words = Math.Max(1, target / 3);
        var planned = Enumerable.Range(0, 5)
            .Select(i => new BookProofreadService.ChapterProofreadState(
                Guid.NewGuid(), i, WordCount: words,
                new[] { new BookProofreadService.ProofreadUnit(null, Edited) },
                IsCovered: false, IsFresh: false, NewestReliableAt: null))
            .ToList();

        var (chunks, seconds) = BookProofreadService.Estimate(planned, opts, "en", AiTier.Fast);

        Assert.Equal(5, chunks);
        Assert.Equal(5 * opts.ProofreadSecondsPerUnitOverhead + 5 * opts.ProofreadSecondsPerChunkEstimate, seconds);
        Assert.True(opts.ProofreadSecondsPerUnitOverhead > 0, "the shipped default must carry the per-unit overhead");
    }

    [Fact]
    public void ASceneSplitChapterIsAtLeastOneChunkPerScene()
    {
        var opts = new AiOptions();
        var units = new[]
        {
            new BookProofreadService.ProofreadUnit(Guid.NewGuid(), Edited),
            new BookProofreadService.ProofreadUnit(Guid.NewGuid(), Edited),
            new BookProofreadService.ProofreadUnit(Guid.NewGuid(), Edited)
        };
        var chapter = new BookProofreadService.ChapterProofreadState(
            Guid.NewGuid(), 0, WordCount: 30, units, IsCovered: false, IsFresh: false, NewestReliableAt: null);

        var (chunks, seconds) = BookProofreadService.Estimate(new[] { chapter }, opts, "he", AiTier.Fast);

        Assert.Equal(3, chunks);
        // Three scene units: three runs, three overheads (be-c07).
        Assert.Equal(3 * opts.ProofreadSecondsPerUnitOverhead + 3 * opts.ProofreadSecondsPerChunkEstimate, seconds);
    }

    // ── Controller ────────────────────────────────────────────────────────────────────────────────────

    private static BookProofreadController Controller(Harness h)
    {
        var scope = h.Provider.CreateScope().ServiceProvider;
        return new BookProofreadController(
            scope.GetRequiredService<AppDbContext>(), scope.GetRequiredService<BookProofreadService>(),
            h.Tracker, scope.GetRequiredService<IHostApplicationLifetime>());
    }

    [Fact]
    public async Task Controller_StartAndStatus_404ForAnUnknownBook()
    {
        using var h = new Harness();
        var controller = Controller(h);

        Assert.IsType<NotFoundResult>((await controller.Start(Guid.NewGuid(), "he", false, onlyFailed: false, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.GetStatus(Guid.NewGuid(), null, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Controller_StatusDefaultsToTheBookLanguage()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון");
        h.AddActiveResult(chapter, null, Edited.AddMinutes(1));

        var ok = Assert.IsType<OkObjectResult>((await Controller(h).GetStatus(h.BookId, null, CancellationToken.None)).Result);
        var status = Assert.IsType<BookProofreadStatusDto>(ok.Value);

        Assert.Equal(1, status.ChaptersWithActiveProofread);
        Assert.Equal(0, status.ChaptersStale);
    }

    [Fact]
    public void Controller_ProgressServesOnlyTheBookScopeProofreadParentOfThisBook()
    {
        using var h = new Harness();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var review = Guid.NewGuid();
        h.Tracker.StartJob(parent, AnalysisScope.Book, AnalysisType.Proofread, h.BookId, null, null);
        h.Tracker.SetTotalChunks(parent, 4);
        h.Tracker.StartJob(child, AnalysisScope.Chapter, AnalysisType.Proofread, h.BookId, Guid.NewGuid(), null);
        h.Tracker.StartJob(review, AnalysisScope.Book, AnalysisType.BookReview, h.BookId, null, null);
        var controller = Controller(h);

        var ok = Assert.IsType<OkObjectResult>(controller.GetProgress(h.BookId, parent).Result);
        var dto = Assert.IsType<AnalysisProgressDto>(ok.Value);
        Assert.Equal("Book", dto.Scope);
        Assert.Equal("Proofread", dto.AnalysisType);
        Assert.Equal(4, dto.TotalChunks);

        Assert.IsType<NotFoundResult>(controller.GetProgress(h.BookId, child).Result);
        Assert.IsType<NotFoundResult>(controller.GetProgress(h.BookId, review).Result);
        Assert.IsType<NotFoundResult>(controller.GetProgress(Guid.NewGuid(), parent).Result);
        Assert.IsType<NotFoundResult>(controller.GetProgress(h.BookId, Guid.NewGuid()).Result);
    }

    // The cancel route's bodies are pinned in BookProofreadCancelRaceTests (be-c11 moved the route test there).
}
