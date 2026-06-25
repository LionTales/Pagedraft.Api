using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using static Pagedraft.Api.Tests.BookReviewTestHelpers;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for <see cref="BookReviewService"/> (wb2-c02): the whole-book developmental review orchestrator.
/// Assembles the budgeted book context ONCE via <see cref="BookContextAssembler"/>, fans out the six
/// per-dimension review prompts through a MOCKED <see cref="IAiRouter"/> (no live model), parses each
/// dimension's findings, unions + dedups them across dimensions, rolls up per-dimension scores, and persists
/// <see cref="BookFinding"/> rows preserving any user-set Status across rebuilds.
///
/// Covers BOTH fan-out strategies (wb2-r02): with BookReviewSingleCombined=false the per-dimension fan-out
/// (6 prompts), union/dedup, status-preservation-on-rebuild (acknowledged/dismissed/done survive; open
/// regenerated rows refresh; vanished-open removed; vanished-touched preserved), score rollup counts,
/// idempotency (skip-when-fresh), the briefs-absent guard (returns "build summary first" + spends NO model
/// calls), partial/total-failure reporting, and one-bad-dimension does not abort the build; with
/// BookReviewSingleCombined=true (the DEFAULT) the single combined call (exactly ONE router call, not six),
/// multi-dimension parse + dedup + rollup, defensive dimension normalisation, combined total-failure -> Failed,
/// and combined status-preservation on rebuild. Mirrors the <see cref="BookSummaryServiceTests"/> conventions
/// (fixed in-memory DB name, mock router).
/// </summary>
public class BookReviewServiceTests
{
    // ─── 1. Dimension fan-out: all six per-dimension prompts are issued ───────────────────────────

    [Fact]
    public async Task BuildBookReviewAsync_FansOutSixDimensionPrompts()
    {
        using var provider = BuildProvider(out var routerMock, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.False(result.NoOp);
        Assert.False(result.BriefsMissing);
        Assert.Equal(0, result.FailedDimensions);

        // Exactly six per-dimension review calls (one per dimension), and each dimension's token appeared.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(6));

        foreach (var dim in new[] { "plot", "character", "pacing", "tone", "theme", "continuity" })
        {
            var token = $"\"dimension\": \"{dim}\"";
            routerMock.Verify(
                r => r.CompleteAsync(
                    It.Is<AiRequest>(req => req.Instruction != null && req.Instruction.Contains(token)
                        && req.TaskType == AiTaskType.BookReview),
                    It.IsAny<CancellationToken>()),
                Times.Once,
                $"the '{dim}' dimension prompt must be issued exactly once with TaskType.BookReview");
        }

        // One finding per dimension → six persisted findings, all dimensions represented.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(6, persisted.Count);
        Assert.Equal(
            new[] { "character", "continuity", "pacing", "plot", "theme", "tone" },
            persisted.Select(f => f.Dimension).OrderBy(d => d).ToArray());
    }

    // ─── 2. Union + dedup across dimensions ───────────────────────────────────────────────────────

    [Fact]
    public async Task BuildBookReviewAsync_DedupsIdenticalFindingsAcrossDimensions()
    {
        // Two dimensions emit a finding with the SAME (dimension-after-stamp, primary chapter order,
        // rationale). The service stamps each finding's dimension to its own dimension, so a TRUE duplicate
        // requires the same dimension + order + rationale. We make the 'plot' dimension emit two findings
        // with the SAME order + rationale → they collapse to one. The other five emit one each.
        var byDim = FindingsPerDimension(perDimensionCount: 1);
        byDim["plot"] = JsonFindings(
            new FindingSpec("improve", 2, "Repeated rationale", 1),
            new FindingSpec("cut", 3, "Repeated rationale", 1));

        using var provider = BuildProvider(out _, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // plot collapsed 2 → 1; other five = 5; total 6 (not 7).
        Assert.Equal(6, persisted.Count);
        Assert.Single(persisted, f => f.Dimension == "plot");
        // The surviving plot finding is the FIRST occurrence (verdict "improve", severity 2).
        var plot = persisted.Single(f => f.Dimension == "plot");
        Assert.Equal("improve", plot.Verdict);
        Assert.Equal(2, plot.Severity);
    }

    // ─── 2b. Null anchors/evidence (model omits / emits JSON null) must NOT crash the build ─────────

    [Fact]
    public async Task BuildBookReviewAsync_FindingWithNullAnchorsAndEvidence_PersistsInsteadOfCrashing()
    {
        // A model finding with only rationale + verdict is valid; chapterAnchors/evidence may arrive as JSON
        // null (the deserializer leaves the lists null, NOT empty). UnionAndDedup/ProjectToEntity must treat a
        // null list as empty rather than throwing a NullReferenceException — which, running outside the
        // per-dimension try/catch, would otherwise fail the ENTIRE build over one anchorless finding.
        var byDim = FindingsPerDimension(perDimensionCount: 0); // all dimensions empty...
        byDim["plot"] =
            """{ "findings": [ { "verdict": "improve", "severity": 2, "rationale": "Valid finding, no anchors", "chapterAnchors": null, "evidence": null } ] }""";

        using var provider = BuildProvider(out _, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        // The build SUCCEEDS (the null-anchored finding does not crash it) and persists the finding.
        Assert.True(result.Ready);
        Assert.Equal(0, result.FailedDimensions);
        Assert.Equal(1, result.FindingCount);

        var finding = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal("plot", finding.Dimension);
        Assert.Equal("Valid finding, no anchors", finding.Rationale);
        // Missing anchors/evidence project to empty JSON arrays, not null and not a crash.
        Assert.Equal("[]", finding.ChapterAnchorsJson);
        Assert.Equal("[]", finding.EvidenceJson);
    }

    [Fact]
    public async Task BuildBookReviewAsync_SingleCombined_FindingWithNullAnchorsAndEvidence_PersistsInsteadOfCrashing()
    {
        // Same robustness guarantee on the DEFAULT single-combined path: the combined call's findings flow
        // through the same UnionAndDedup/ProjectToEntity, so a null chapterAnchors/evidence must not crash it.
        var combined =
            """{ "findings": [ { "dimension": "character", "verdict": "improve", "severity": 2, "rationale": "Combined finding, no anchors", "chapterAnchors": null, "evidence": null } ] }""";

        using var provider = BuildCombinedProvider(out _, combined);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.Equal(1, result.FindingCount);

        var finding = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal("character", finding.Dimension);
        Assert.Equal("[]", finding.ChapterAnchorsJson);
        Assert.Equal("[]", finding.EvidenceJson);
    }

    // ─── 3. Status preservation on rebuild ────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildBookReviewAsync_Rebuild_PreservesUserStatusAndHandlesVanished()
    {
        // Build 1 produces findings on plot. The user then acts on some (acknowledged/dismissed/done) and
        // leaves one open. Build 2 (after a brief refresh) regenerates a DIFFERENT plot finding set so we can
        // observe: acted-on rows that VANISH are preserved; an open row that REGENERATES refreshes; an open
        // row that VANISHES is removed; a NEW finding is inserted open.
        var build1 = FindingsPerDimension(perDimensionCount: 0); // start with only plot populated
        build1["plot"] = JsonFindings(
            new FindingSpec("improve", 2, "Ack me", 1),
            new FindingSpec("cut", 3, "Dismiss me", 1),
            new FindingSpec("improve", 1, "Done me", 1),
            new FindingSpec("improve", 2, "Open and vanishes", 1),
            new FindingSpec("keep", 1, "Open and regenerates", 2));

        using var provider = BuildProvider(out _, build1);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var afterBuild1 = await db.BookFindings.Where(f => f.BookId == bookId && f.Dimension == "plot").ToListAsync();
        Assert.Equal(5, afterBuild1.Count);

        // User acts on three; leaves two open.
        SetStatus(db, afterBuild1, "Ack me", "acknowledged");
        SetStatus(db, afterBuild1, "Dismiss me", "dismissed");
        SetStatus(db, afterBuild1, "Done me", "done");
        await db.SaveChangesAsync();

        // Record the dedup keys / ids of the open-regenerates row to assert it is the SAME row after rebuild.
        var regeneratesRow = afterBuild1.Single(f => f.Rationale == "Open and regenerates");
        var regeneratesId = regeneratesRow.Id;

        // Build 2: plot now regenerates only the three acted-on rationales (which VANISH from the new set as
        // model output but are user-acted → preserved), the "Open and regenerates" row (same key → refresh),
        // and a brand-new finding. "Open and vanishes" is NOT re-emitted → it must be DELETED (open noise).
        var build2 = FindingsPerDimension(perDimensionCount: 0);
        build2["plot"] = JsonFindings(
            new FindingSpec("keep", 1, "Open and regenerates", 2), // same dimension+order+rationale → same key
            new FindingSpec("improve", 2, "Brand new finding", 1)); // new → inserted open

        // Refresh the briefs so the review is considered stale vs briefs (forces a non-no-op rebuild).
        await TouchSummaryBaselineAsync(db, bookId);

        // Swap the mock to return build2's plot findings.
        SwapDimensionFindings(provider, build2);

        await svc.BuildBookReviewAsync(bookId, "he");

        var afterBuild2 = await db.BookFindings.AsNoTracking()
            .Where(f => f.BookId == bookId && f.Dimension == "plot").ToListAsync();

        // Acted-on rows that vanished are PRESERVED with their user Status.
        Assert.Equal("acknowledged", afterBuild2.Single(f => f.Rationale == "Ack me").Status);
        Assert.Equal("dismissed", afterBuild2.Single(f => f.Rationale == "Dismiss me").Status);
        Assert.Equal("done", afterBuild2.Single(f => f.Rationale == "Done me").Status);

        // The open row that VANISHED is removed.
        Assert.DoesNotContain(afterBuild2, f => f.Rationale == "Open and vanishes");

        // The open row that REGENERATED is the SAME row (same id), refreshed, still open.
        var regenerated = afterBuild2.Single(f => f.Rationale == "Open and regenerates");
        Assert.Equal(regeneratesId, regenerated.Id);
        Assert.Equal("open", regenerated.Status);

        // The brand-new finding is inserted open.
        Assert.Equal("open", afterBuild2.Single(f => f.Rationale == "Brand new finding").Status);

        // Total plot rows: 3 preserved acted-on + 1 regenerated + 1 new = 5 (the vanished-open one gone).
        Assert.Equal(5, afterBuild2.Count);
    }

    // ─── 4. DimensionScore rollup counts ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFindingsAsync_RollsUpDimensionScoresFromKeepImproveCut()
    {
        var byDim = FindingsPerDimension(perDimensionCount: 0);
        // plot: all keep, no improve/cut → "strong".
        byDim["plot"] = JsonFindings(
            new FindingSpec("keep", 1, "Strength one", 1),
            new FindingSpec("keep", 2, "Strength two", 2));
        // character: more cut than keep → "weak".
        byDim["character"] = JsonFindings(
            new FindingSpec("keep", 1, "One good thing", 1),
            new FindingSpec("cut", 2, "Cut this", 2),
            new FindingSpec("cut", 1, "Cut that", 3));
        // pacing: a major (sev 3) improve → "weak".
        byDim["pacing"] = JsonFindings(
            new FindingSpec("improve", 3, "Major pacing problem", 1));
        // theme: mix of keep + improve, no major → "mixed".
        byDim["theme"] = JsonFindings(
            new FindingSpec("keep", 1, "Theme strength", 1),
            new FindingSpec("improve", 2, "Theme fix", 2));

        using var provider = BuildProvider(out _, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var read = await svc.GetFindingsAsync(bookId, "he");
        var scores = read.Scores.ToDictionary(s => s.Dimension);

        Assert.Equal("strong", scores["plot"].Score);
        Assert.Equal(2, scores["plot"].KeepCount);
        Assert.Equal(0, scores["plot"].ImproveCount);
        Assert.Equal(0, scores["plot"].CutCount);

        Assert.Equal("weak", scores["character"].Score);
        Assert.Equal(1, scores["character"].KeepCount);
        Assert.Equal(2, scores["character"].CutCount);

        Assert.Equal("weak", scores["pacing"].Score); // major improve
        Assert.Equal(1, scores["pacing"].ImproveCount);

        Assert.Equal("mixed", scores["theme"].Score);
        Assert.Equal(1, scores["theme"].KeepCount);
        Assert.Equal(1, scores["theme"].ImproveCount);

        // Dimensions with no findings (tone, continuity) get no score row.
        Assert.False(scores.ContainsKey("tone"));
        Assert.False(scores.ContainsKey("continuity"));
    }

    // ─── 5. Idempotency: skip-when-fresh ──────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildBookReviewAsync_SecondBuildUnchanged_IsNoOp_NoLlmCalls()
    {
        using var provider = BuildProvider(out var routerMock, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();

        var first = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(first.Ready);
        Assert.False(first.NoOp);
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(6));

        // Capture UpdatedAt for every persisted finding after the first build.
        var afterFirst = await db.BookFindings.AsNoTracking()
            .Where(f => f.BookId == bookId).ToListAsync();
        var updatedAtByKey = afterFirst.ToDictionary(f => f.DedupKey, f => f.UpdatedAt);
        Assert.NotEmpty(updatedAtByKey); // sanity: findings were actually persisted

        // Second build, nothing changed: no-op, NO further LLM calls.
        var second = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(second.Ready);
        Assert.True(second.NoOp);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(6),
            "a second unchanged build must not re-issue any per-dimension LLM calls");

        // A no-op must NOT touch the persisted BookFinding rows — UpdatedAt must be bit-identical.
        // If the service re-saved rows, AppDbContext.SaveChangesAsync would stamp a new UpdatedAt.
        var afterSecond = await db.BookFindings.AsNoTracking()
            .Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(updatedAtByKey.Count, afterSecond.Count); // row count unchanged
        foreach (var finding in afterSecond)
        {
            Assert.True(
                updatedAtByKey.TryGetValue(finding.DedupKey, out var capturedAt),
                $"finding with DedupKey '{finding.DedupKey}' was not present after first build");
            Assert.Equal(capturedAt, finding.UpdatedAt);
        }

        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.True(status.IsReady);
        Assert.True(status.HasReview);
        Assert.False(status.StaleVsBriefs);
        Assert.False(status.BuiltWithDifferentModel);
    }

    // ─── 6. Briefs-absent guard: returns "build summary first", spends NO model calls ─────────────

    [Fact]
    public async Task BuildBookReviewAsync_NoStructuredBriefs_ReturnsBriefsMissing_NoLlmCalls()
    {
        using var provider = BuildProvider(out var routerMock, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();

        // A book with chapters but NO structured briefs at all → the assembler degrades to the flat path
        // (UsedStructuredBriefs == false), so the review must NOT spend any model calls.
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "No Briefs", Language = "he" });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן בלי תקציר." });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.BriefsMissing);
        Assert.False(result.Ready);
        Assert.False(result.NoOp);
        Assert.Equal(0, result.FindingCount);
        Assert.Contains("summary", result.Message, StringComparison.OrdinalIgnoreCase);

        // No model calls were spent on the briefs-absent path.
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, await db.BookFindings.CountAsync());

        // Status reflects "no briefs to review".
        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.False(status.HasBriefs);
        Assert.False(status.HasReview);
    }

    // ─── 6b. Briefs-absent guard with a jobId must NOT report a SUCCEEDED job (no review produced) ───

    [Fact]
    public async Task BuildBookReviewAsync_NoBriefs_WithJobId_DoesNotReportSucceededProgress()
    {
        // The async path can reach the briefs-absent guard when the briefs vanish AFTER the controller's
        // request-time guard (a mid-flight race). The build returns Ready=false / BriefsMissing=true and
        // produces NO review, so the job must NOT surface Succeeded — otherwise progress polling shows a green
        // finish for a build that produced nothing.
        using var provider = BuildProvider(out var routerMock, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "No Briefs", Language = "he" });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן בלי תקציר." });
        await db.SaveChangesAsync();

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();
        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        Assert.True(result.BriefsMissing);
        Assert.False(result.Ready);

        // The job is driven to a NON-success terminal (Canceled), never Succeeded.
        Assert.True(progress.TryGet(jobId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.NotEqual(AnalysisProgressStatus.Succeeded, snapshot!.Status);
        Assert.Equal(AnalysisProgressStatus.Canceled, snapshot.Status);

        // No model calls were spent on the briefs-absent path.
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── 6c. Registry-race bail reports Ready via the IsReady gate, NOT HasReview ───────────────────

    [Fact]
    public async Task BuildBookReviewAsync_RegistryRaceLost_StaleReview_ReportsNotReady()
    {
        // When the async build LOSES the registry race (another build already holds the slot), the bail must
        // report Ready using the SAME IsReady gate as the intentional no-op — NOT HasReview. A STALE existing
        // review (HasReview=true but IsReady=false) must NOT surface Ready=true while another build runs, or
        // the caller treats an outdated cache as fresh.
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var svc = provider.GetRequiredService<BookReviewService>();

        // Build once so a review EXISTS (HasReview=true)...
        var first = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(first.Ready);
        // ...then make it STALE vs briefs so IsReady=false while HasReview stays true.
        await TouchSummaryBaselineAsync(db, bookId);
        Assert.False((await svc.GetStatusAsync(bookId, "he")).IsReady);

        // Pre-occupy the registry with a LIVE winner job so the next build loses the race (a winner whose
        // progress is missing/terminal would be self-healed away by ResolveActiveBuildJobId, freeing the slot).
        var registry = provider.GetRequiredService<BookReviewBuildRegistry>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var winnerJobId = Guid.NewGuid();
        Assert.True(registry.TryStart(bookId, "he", winnerJobId));
        progress.StartJob(winnerJobId, AnalysisScope.Book, AnalysisType.BookReview, bookId, null, null, "winner running");

        var loserJobId = Guid.NewGuid();
        var result = await svc.BuildBookReviewAsync(bookId, "he", loserJobId);

        // Race lost → no-op bail, but NOT ready (the existing review is stale, so IsReady=false).
        Assert.True(result.NoOp);
        Assert.False(result.Ready, "a stale review must not surface Ready=true when the build bailed on the race");

        // The losing job is driven to a terminal Canceled status so its tab reattaches to the winner.
        Assert.True(progress.TryGet(loserJobId, out var snap));
        Assert.Equal(AnalysisProgressStatus.Canceled, snap!.Status);
    }

    // ─── 7. One bad dimension does not abort the build; the other five still persist ──────────────

    [Fact]
    public async Task BuildBookReviewAsync_OneDimensionFails_OtherFivePersist()
    {
        var byDim = FindingsPerDimension(perDimensionCount: 1);
        // The 'character' dimension returns garbage that yields no extractable JSON → treated as zero
        // findings (no abort). The other five still produce one finding each.
        byDim["character"] = "this is not json at all, just prose with no braces";

        using var provider = BuildProvider(out var routerMock, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        // The build did NOT abort: it succeeded with one failed dimension and the other five persisted.
        Assert.True(result.Ready);
        Assert.Equal(1, result.FailedDimensions);

        // All six prompts were still issued.
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(6));

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(5, persisted.Count); // character contributed zero
        Assert.DoesNotContain(persisted, f => f.Dimension == "character");
    }

    // ─── 8. chapterId backfill by Order for anchors/evidence (Phase-3 navigation) ─────────────────

    [Fact]
    public async Task BuildBookReviewAsync_BackfillsChapterIdByOrder()
    {
        var byDim = FindingsPerDimension(perDimensionCount: 0);
        // A plot finding anchored to chapter order 1 (the model gives order + title, NOT chapterId).
        byDim["plot"] = JsonFindings(new FindingSpec("improve", 2, "Anchored to ch1", 1));

        using var provider = BuildProvider(out _, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        // Resolve the real id of the chapter at Order 1.
        var ch1Id = await db.Chapters.AsNoTracking()
            .Where(c => c.BookId == bookId && c.Order == 1)
            .Select(c => c.Id).SingleAsync();

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var finding = await db.BookFindings.AsNoTracking()
            .SingleAsync(f => f.BookId == bookId && f.Dimension == "plot");

        var anchors = JsonSerializer.Deserialize<List<FindingChapterAnchor>>(finding.ChapterAnchorsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(anchors);
        Assert.Single(anchors!);
        Assert.Equal(1, anchors![0].Order);
        Assert.Equal(ch1Id, anchors[0].ChapterId); // backfilled from the chapter at Order 1

        var evidence = JsonSerializer.Deserialize<List<FindingEvidence>>(finding.EvidenceJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(evidence);
        Assert.Single(evidence!);
        Assert.Equal(ch1Id, evidence![0].ChapterId);
    }

    // ─── 9. TOTAL failure (all dimensions fail) surfaces FAILED, not a silent green finish (wb2-c05) ─

    [Fact]
    public async Task BuildBookReviewAsync_AllDimensionsFail_SurfacesFailedStatus_NotReady()
    {
        // Every dimension returns unparseable output (the real-world truncation symptom: ExtractJson finds
        // no balanced JSON), so all six contribute zero findings. The build must NOT report Succeeded/ready;
        // it surfaces a FAILED job + a non-ready result so the FE shows a failed state instead of re-enabling
        // Build on a green finish.
        var byDim = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dim in new[] { "plot", "character", "pacing", "tone", "theme", "continuity" })
            byDim[dim] = "{ truncated mid-stream, no closing brace"; // unbalanced → ExtractJson returns null

        using var provider = BuildProvider(out var routerMock, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        // All six prompts were issued (one bad dimension never short-circuits the fan-out).
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(6));

        // TOTAL failure: not ready, all six failed, zero findings persisted.
        Assert.False(result.Ready, "a build where every dimension failed must not be Ready");
        Assert.False(result.NoOp);
        Assert.False(result.BriefsMissing);
        Assert.Equal(6, result.FailedDimensions);
        Assert.Equal(0, result.FindingCount);
        Assert.Contains("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.BookFindings.CountAsync(f => f.BookId == bookId));

        // The job surfaces FAILED (not Succeeded) so the FE shows an error, not a silent green finish.
        Assert.True(progress.TryGet(jobId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(AnalysisProgressStatus.Failed, snapshot!.Status);

        // Status read agrees: no usable review was produced.
        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.False(status.HasReview);
        Assert.False(status.IsReady);
    }

    // ─── 9b. TOTAL failure via ALL-EMPTY (parseable) findings must NOT report success NOR wipe the cache ─

    [Fact]
    public async Task BuildBookReviewAsync_AllDimensionsEmpty_NotReady_AndPreservesCache()
    {
        // Build 1 produces real findings on plot; the user leaves one open and acknowledges another. Build 2
        // returns PARSEABLE but EMPTY findings ({"findings": []}) for every dimension — the degenerate /
        // truncation symptom. The old gate counted only NULL (unparseable) dimensions as failed, so this
        // all-empty build slipped through as a green SUCCESS and the persist step then DELETED every still-open
        // cached finding. The fix: zero fresh findings is a TOTAL failure → not ready, FAILED job, and the
        // destructive persist is SKIPPED so the prior review survives intact.
        var build1 = FindingsPerDimension(perDimensionCount: 0);
        build1["plot"] = JsonFindings(
            new FindingSpec("improve", 2, "Open finding that must survive", 1),
            new FindingSpec("cut", 3, "Acknowledged finding", 1));

        using var provider = BuildProvider(out _, build1);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var afterBuild1 = await db.BookFindings.Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, afterBuild1.Count);
        SetStatus(db, afterBuild1, "Acknowledged finding", "acknowledged");
        await db.SaveChangesAsync();

        // Build 2: every dimension returns an empty findings array; force a non-no-op via a stale-vs-briefs bump.
        await TouchSummaryBaselineAsync(db, bookId);
        SwapDimensionFindings(provider, FindingsPerDimension(perDimensionCount: 0));

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        // Not a successful rebuild: zero fresh findings is a total failure, not a green finish.
        Assert.False(result.Ready, "an all-empty build must not be Ready");
        Assert.False(result.NoOp);
        Assert.Contains("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AnalysisProgressStatus.Failed, progress.TryGet(jobId, out var snap) ? snap!.Status : AnalysisProgressStatus.Succeeded);

        // CRITICAL: the cached review is PRESERVED — neither the open nor the acknowledged row was deleted.
        var afterBuild2 = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, afterBuild2.Count);
        Assert.Contains(afterBuild2, f => f.Rationale == "Open finding that must survive" && f.Status == "open");
        Assert.Contains(afterBuild2, f => f.Rationale == "Acknowledged finding" && f.Status == "acknowledged");
        Assert.Equal(2, result.FindingCount); // reports the preserved cache count, not 0
    }

    // ─── 10. PARTIAL failure carries a warning but still succeeds with the findings that parsed ────────

    [Fact]
    public async Task BuildBookReviewAsync_SomeDimensionsFail_SucceedsWithWarning()
    {
        // Two of six dimensions return unparseable output; the other four produce one finding each. The build
        // SUCCEEDS (ready, findings persisted) but carries a warning message naming the failed-dimension count
        // so the FE can show a degraded banner.
        var byDim = FindingsPerDimension(perDimensionCount: 1);
        byDim["plot"] = "not json, no braces at all";
        byDim["tone"] = "{ unbalanced and truncated";

        using var provider = BuildProvider(out var routerMock, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(6));

        // PARTIAL failure: ready (four dimensions persisted), two failed, warning message present.
        Assert.True(result.Ready, "a partial failure with some findings is still a usable review");
        Assert.False(result.NoOp);
        Assert.Equal(2, result.FailedDimensions);
        Assert.Equal(4, result.FindingCount);
        Assert.Contains("warning", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 failed", result.Message, StringComparison.OrdinalIgnoreCase);

        // The job still SUCCEEDS (degraded, not failed) so the user keeps the partial review; the warning
        // lives in the message for the FE to surface.
        Assert.True(progress.TryGet(jobId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(AnalysisProgressStatus.Succeeded, snapshot!.Status);
        Assert.Contains("warning", snapshot.Message, StringComparison.OrdinalIgnoreCase);

        // Four dimensions persisted; the two that failed contributed nothing.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(4, persisted.Count);
        Assert.DoesNotContain(persisted, f => f.Dimension is "plot" or "tone");
    }

    // ─── 11. Config pin: BookReview task resolves the raised Ollama_BookReview tuning (wb2-c05) ───────

    [Fact]
    public void Appsettings_HasOllamaBookReviewTuning_WithRaisedContextAndPredict()
    {
        // ROOT-CAUSE GUARD (wb2-c05): without an Ollama_BookReview ProviderSettings entry, AiTaskType.BookReview
        // falls back to the implicit ProviderTuningOptions default (NumCtx=4096), which TRUNCATES a 15+ chapter
        // book's input prompt -> the model returns a lone '{' -> ExtractJson null -> all six dimensions fail.
        // Pin the raised tuning so a config regression that drops/lowers it is caught WITHOUT a live model.
        var path = FindAppsettings();
        Assert.True(File.Exists(path), $"appsettings.json not found at {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var providerSettings = doc.RootElement
            .GetProperty("Ai")
            .GetProperty("ProviderSettings");

        Assert.True(providerSettings.TryGetProperty("Ollama_BookReview", out var section),
            "Ai:ProviderSettings:Ollama_BookReview must exist so AiTaskType.BookReview does not fall back to NumCtx=4096.");

        var numCtx = section.GetProperty("NumCtx").GetInt32();
        var numPredict = section.GetProperty("NumPredict").GetInt32();

        // A 15+ chapter book's [BOOK_CONTEXT] (book budget derives as NumCtx*0.5) plus the dimension prompt
        // needs well over the 4096 default; the per-chapter dimensionSignals + findings output needs room too.
        Assert.True(numCtx >= 16384, $"Ollama_BookReview NumCtx must be >= 16384 to fit a large book (was {numCtx}).");
        Assert.True(numPredict >= 4096, $"Ollama_BookReview NumPredict must be >= 4096 for the findings output (was {numPredict}).");

        // ROOT-CAUSE GUARD (wb2-f01): the eval harness constant BookReviewQualityTests.DefaultBookReviewModel
        // must mirror Ai:FeatureModels:BookReview:Model so an appsettings model change cannot silently
        // benchmark a stale model without a compile-time-visible constant update.
        var featureModel = doc.RootElement
            .GetProperty("Ai")
            .GetProperty("FeatureModels")
            .GetProperty("BookReview")
            .GetProperty("Model")
            .GetString();

        Assert.True(
            featureModel == LanguageEngine.BookReviewQualityTests.DefaultBookReviewModel,
            $"Ai:FeatureModels:BookReview:Model in appsettings (\"{featureModel}\") must match the eval-harness " +
            $"constant BookReviewQualityTests.DefaultBookReviewModel (\"{LanguageEngine.BookReviewQualityTests.DefaultBookReviewModel}\"). " +
            "Update the constant (and re-baseline eval gold if needed) whenever the production model changes.");
    }

    /// <summary>Walks up from the test assembly dir to the sibling Pagedraft.Api project's appsettings.json
    /// (it is not copied to the test output, so resolve it from the source tree at test time).</summary>
    private static string FindAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Pagedraft.Api", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // Fallback: the repo-relative path from the test bin (bin/Debug/net8.0 → ../../../../Pagedraft.Api).
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Pagedraft.Api", "appsettings.json"));
    }

    // ─── 12. SINGLE-COMBINED (default): ONE call, multi-dimension parse + dedup + rollup (wb2-r02) ──────

    [Fact]
    public async Task BuildBookReviewAsync_SingleCombined_IssuesOneCall_ParsesMultiDimension()
    {
        // The combined response carries findings across FOUR dimensions in ONE call. The service must issue
        // exactly ONE router call (NOT six), parse the multi-dimension findings, dedup, persist, and roll the
        // per-dimension scores up correctly from the self-labelled dimensions.
        var combined = JsonCombinedFindings(
            new CombinedFindingSpec("plot", "keep", 1, "Strong plot spine", 1),
            new CombinedFindingSpec("plot", "keep", 2, "Satisfying payoff", 2),
            new CombinedFindingSpec("character", "improve", 2, "Flat secondary cast", 1),
            new CombinedFindingSpec("pacing", "cut", 3, "Sagging middle act", 2),
            new CombinedFindingSpec("theme", "improve", 1, "Theme underdeveloped", 1));

        using var provider = BuildCombinedProvider(out var routerMock, combined);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.False(result.NoOp);
        Assert.False(result.BriefsMissing);
        Assert.Equal(0, result.FailedDimensions);

        // EXACTLY ONE combined call — not six. This is the whole point of the single-combined default.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // And the one call was the BookReview-tagged combined prompt (no per-dimension fan-out).
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.TaskType == AiTaskType.BookReview),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // All five findings persisted, dimensions taken from the model's self-labels.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(5, persisted.Count);
        Assert.Equal(2, persisted.Count(f => f.Dimension == "plot"));
        Assert.Single(persisted, f => f.Dimension == "character");
        Assert.Single(persisted, f => f.Dimension == "pacing");
        Assert.Single(persisted, f => f.Dimension == "theme");

        // Score rollup keys on the self-labelled dimensions: plot = strong (2 keep, no improve/cut),
        // pacing = weak (a sev-3 cut), character = mixed, theme = mixed.
        var read = await svc.GetFindingsAsync(bookId, "he");
        var scores = read.Scores.ToDictionary(s => s.Dimension);
        Assert.Equal("strong", scores["plot"].Score);
        Assert.Equal(2, scores["plot"].KeepCount);
        Assert.Equal("weak", scores["pacing"].Score);
        Assert.Equal(1, scores["pacing"].CutCount);
        Assert.False(scores.ContainsKey("tone"));       // no finding → no score row
        Assert.False(scores.ContainsKey("continuity"));
    }

    // ─── 13. SINGLE-COMBINED: a bad/unknown self-labelled dimension is normalised, never poisons dedup ──

    [Fact]
    public async Task BuildBookReviewAsync_SingleCombined_NormalisesUnknownDimension()
    {
        // The model mis-labels one finding's dimension ("plotline" — not one of the six) and leaves another
        // blank. Both must be normalised to a valid dimension ("plot", the fallback) so the dedup key and the
        // score rollup never key on a bad value.
        var combined = JsonCombinedFindings(
            new CombinedFindingSpec("plotline", "improve", 2, "Mislabelled dimension", 1),
            new CombinedFindingSpec("", "improve", 2, "Blank dimension", 2),
            new CombinedFindingSpec("character", "keep", 1, "Valid one", 1));

        using var provider = BuildCombinedProvider(out _, combined);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // Every persisted dimension is one of the six valid values (no "plotline"/blank leaked through).
        var valid = new[] { "plot", "character", "pacing", "tone", "theme", "continuity" };
        Assert.All(persisted, f => Assert.Contains(f.Dimension, valid));
        // The two normalised-to-plot findings + the valid character finding all survive.
        Assert.Equal(2, persisted.Count(f => f.Dimension == "plot"));
        Assert.Single(persisted, f => f.Dimension == "character");
    }

    // ─── 14. SINGLE-COMBINED: the single call fails to parse → TOTAL failure → FAILED, not Succeeded ────

    [Fact]
    public async Task BuildBookReviewAsync_SingleCombined_UnparseableOutput_SurfacesFailed_NotReady()
    {
        // The ONE combined call returns unparseable output (the truncation symptom: ExtractJson finds no
        // balanced JSON). In combined mode the single call is the only producer, so this is a TOTAL failure:
        // not ready, FAILED job, zero findings — mirroring the per-dimension all-failed case (wb2-c05).
        using var provider = BuildCombinedProvider(out var routerMock, "{ truncated mid-stream, no closing brace");
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        // Still exactly ONE call (a combined failure does not retry as a fan-out).
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.False(result.Ready, "a combined build whose single call failed must not be Ready");
        Assert.False(result.NoOp);
        Assert.False(result.BriefsMissing);
        Assert.Equal(6, result.FailedDimensions); // total failure == all six dimensions marked failed
        Assert.Equal(0, result.FindingCount);
        Assert.Contains("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.BookFindings.CountAsync(f => f.BookId == bookId));

        // The job surfaces FAILED (not Succeeded) so the FE shows an error, not a silent green finish.
        Assert.True(progress.TryGet(jobId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(AnalysisProgressStatus.Failed, snapshot!.Status);

        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.False(status.HasReview);
        Assert.False(status.IsReady);
    }

    // ─── 14b. SINGLE-COMBINED: a parseable-but-EMPTY findings[] is a total failure and preserves the cache ─

    [Fact]
    public async Task BuildBookReviewAsync_SingleCombined_EmptyFindings_SurfacesFailed_NotReady_PreservesCache()
    {
        // Build 1 (combined) produces a plot finding the user leaves open. Build 2's single combined call
        // returns a PARSEABLE-but-EMPTY findings array. In combined mode the single call is the only producer,
        // so empty (not just null) is a TOTAL failure — like the unparseable case above: not ready, FAILED job,
        // all six dimensions reported failed, and the destructive persist is SKIPPED so the open cached finding
        // survives. The old gate treated empty as a green success and would have deleted the open row.
        var build1 = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Open finding that must survive", 1));
        using var provider = BuildCombinedProvider(out var routerMock, build1);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");
        Assert.Single(await db.BookFindings.Where(f => f.BookId == bookId).ToListAsync());

        // Build 2: the single combined call returns an empty findings array. Force a non-no-op via a brief bump.
        await TouchSummaryBaselineAsync(db, bookId);
        SwapCombinedResponse(provider, """{ "findings": [] }""");

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        // Exactly one combined call on the second build (plus the one on the first build).
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        Assert.False(result.Ready, "a combined build that produced no findings must not be Ready");
        Assert.False(result.NoOp);
        Assert.Equal(6, result.FailedDimensions); // empty combined output is a total failure, like null
        Assert.Contains("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AnalysisProgressStatus.Failed, progress.TryGet(jobId, out var snap) ? snap!.Status : AnalysisProgressStatus.Succeeded);

        // The open cached finding is PRESERVED (the empty build did not wipe it).
        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Open finding that must survive", rows[0].Rationale);
        Assert.Equal("open", rows[0].Status);
        Assert.Equal(1, result.FindingCount);
    }

    // ─── 15. SINGLE-COMBINED: status preservation on rebuild still works ───────────────────────────────

    [Fact]
    public async Task BuildBookReviewAsync_SingleCombined_Rebuild_PreservesUserStatus()
    {
        // Build 1 (combined) produces plot findings. The user acts on some and leaves one open. Build 2
        // regenerates a different set. The same preserve-touched / refresh-regenerated / delete-vanished-open
        // / insert-new rules must hold in combined mode (the persist step is shared, so this guards the wiring).
        var build1 = JsonCombinedFindings(
            new CombinedFindingSpec("plot", "improve", 2, "Ack me", 1),
            new CombinedFindingSpec("plot", "cut", 3, "Dismiss me", 1),
            new CombinedFindingSpec("plot", "improve", 2, "Open and vanishes", 1),
            new CombinedFindingSpec("plot", "keep", 1, "Open and regenerates", 2));

        using var provider = BuildCombinedProvider(out _, build1);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var afterBuild1 = await db.BookFindings.Where(f => f.BookId == bookId && f.Dimension == "plot").ToListAsync();
        Assert.Equal(4, afterBuild1.Count);

        SetStatus(db, afterBuild1, "Ack me", "acknowledged");
        SetStatus(db, afterBuild1, "Dismiss me", "dismissed");
        await db.SaveChangesAsync();

        var regeneratesId = afterBuild1.Single(f => f.Rationale == "Open and regenerates").Id;

        // Build 2: regenerate the open row (same key → refresh) + a brand-new finding. "Open and vanishes"
        // is NOT re-emitted → deleted (open noise); the two acted-on rows vanish from output but are preserved.
        var build2 = JsonCombinedFindings(
            new CombinedFindingSpec("plot", "keep", 1, "Open and regenerates", 2),
            new CombinedFindingSpec("plot", "improve", 2, "Brand new finding", 1));

        await TouchSummaryBaselineAsync(db, bookId);
        SwapCombinedResponse(provider, build2);

        await svc.BuildBookReviewAsync(bookId, "he");

        var afterBuild2 = await db.BookFindings.AsNoTracking()
            .Where(f => f.BookId == bookId && f.Dimension == "plot").ToListAsync();

        Assert.Equal("acknowledged", afterBuild2.Single(f => f.Rationale == "Ack me").Status);
        Assert.Equal("dismissed", afterBuild2.Single(f => f.Rationale == "Dismiss me").Status);
        Assert.DoesNotContain(afterBuild2, f => f.Rationale == "Open and vanishes");
        var regenerated = afterBuild2.Single(f => f.Rationale == "Open and regenerates");
        Assert.Equal(regeneratesId, regenerated.Id);
        Assert.Equal("open", regenerated.Status);
        Assert.Equal("open", afterBuild2.Single(f => f.Rationale == "Brand new finding").Status);
        // 2 preserved acted-on + 1 regenerated + 1 new = 4 (vanished-open gone).
        Assert.Equal(4, afterBuild2.Count);
    }

    // ─── 16. DATA: a he + en finding with the SAME DedupKey for the SAME book COEXIST (data-c01) ───────

    [Fact]
    public async Task BookFinding_SameDedupKey_DifferentLanguage_BothRowsCoexist()
    {
        // data-c01: ComputeDedupKey hashes (dimension, primaryChapterOrder, rationale) — NOT Language — yet
        // every query scopes BookFinding by (BookId, Language). A he finding and an en finding for the SAME
        // book whose (dimension, order, rationale) are identical therefore share a DedupKey. The unique index
        // must key on (BookId, Language, DedupKey) so BOTH rows coexist; an index that omits Language would
        // throw a unique-constraint DbUpdateException on the second insert (the bug this guards).
        //
        // ENFORCEMENT MECHANISM: this suite uses EF InMemory (see BuildProvider), which does NOT enforce unique
        // indexes, so the coexistence INSERT alone cannot prove RED. The model-metadata assertion below (the
        // configured index includes Language) is the revert-verify surface: reverting AppDbContext to
        // { BookId, DedupKey } drops Language from context.Model and FAILS that assertion. We keep BOTH: the
        // metadata check proves the constraint shape, the behavioral check proves the two rows actually persist
        // and read back as distinct languages under the configured model.
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 0));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 1);

        // CONSTRAINT-SHAPE (RED on revert): the configured unique index on BookFinding includes Language. This
        // reads the EF model, not the DB, so it holds under InMemory AND would catch a revert of the index.
        var entityType = db.Model.FindEntityType(typeof(BookFinding))!;
        var uniqueIndex = entityType.GetIndexes().Single(ix => ix.IsUnique);
        var indexColumns = uniqueIndex.Properties.Select(p => p.Name).ToArray();
        Assert.Contains(nameof(BookFinding.Language), indexColumns);
        Assert.Contains(nameof(BookFinding.BookId), indexColumns);
        Assert.Contains(nameof(BookFinding.DedupKey), indexColumns);

        // Two findings, identical (dimension, primaryChapterOrder, rationale) → identical DedupKey, but he/en.
        const string dimension = "plot";
        const int primaryOrder = 0;
        const string rationale = "Identical rationale across languages";
        var sharedKey = BookFinding.ComputeDedupKey(dimension, primaryOrder, rationale);

        BookFinding MakeFinding(string lang) => new()
        {
            BookId = bookId,
            Language = lang,
            Dimension = dimension,
            Verdict = "improve",
            Severity = 2,
            Rationale = rationale,
            EvidenceJson = "[]",
            ChapterAnchorsJson = "[]",
            Status = "open",
            DedupKey = sharedKey,
            BuiltWithModel = ActiveModel
        };

        db.BookFindings.Add(MakeFinding("he"));
        db.BookFindings.Add(MakeFinding("en"));
        await db.SaveChangesAsync();

        // BEHAVIORAL: both rows coexist for the same book + same DedupKey, distinguished only by Language.
        var rows = await db.BookFindings.AsNoTracking()
            .Where(f => f.BookId == bookId && f.DedupKey == sharedKey)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "en", "he" }, rows.Select(f => f.Language).OrderBy(l => l).ToArray());
        // Both carry the SAME DedupKey (Language is what keeps them apart, not the key).
        Assert.All(rows, f => Assert.Equal(sharedKey, f.DedupKey));
    }

    // ─── Test helpers ─────────────────────────────────────────────────────────────────────────────
    // FindingSpec, FindingsPerDimension, JsonFindings, SeedReviewableBookAsync live in BookReviewTestHelpers
    // (shared with BooksReviewControllerTests). The class-level helpers below are specific to this suite.

    private static void SetStatus(AppDbContext db, List<BookFinding> findings, string rationale, string status)
    {
        var row = findings.Single(f => f.Rationale == rationale);
        row.Status = status;
    }

    /// <summary>Bumps the cached BookSummaryBaseline UpdatedAt past the existing findings so the review reads
    /// STALE vs briefs and a rebuild is not a no-op.</summary>
    private static async Task TouchSummaryBaselineAsync(AppDbContext db, Guid bookId)
    {
        await Task.Delay(10);
        var baseline = await db.BookSummaryBaselines.SingleAsync(b => b.BookId == bookId);
        baseline.BuiltChapterCount += 0; // mark modified
        db.Entry(baseline).State = EntityState.Modified;
        await db.SaveChangesAsync();
    }

    /// <summary>Re-points the shared mutable dimension map the mock router reads, so a second build returns a
    /// different finding set per dimension.</summary>
    private static void SwapDimensionFindings(ServiceProvider provider, Dictionary<string, string> next)
    {
        var holder = provider.GetRequiredService<DimensionFindingsHolder>();
        holder.ByDimension = next;
    }

    /// <summary>Mutable holder so a test can swap the mock's per-dimension responses between builds.</summary>
    private sealed class DimensionFindingsHolder
    {
        public Dictionary<string, string> ByDimension = new(StringComparer.Ordinal);
    }

    /// <summary>Re-points the shared mutable combined-response holder so a second combined build returns a
    /// different finding set.</summary>
    private static void SwapCombinedResponse(ServiceProvider provider, string next)
    {
        provider.GetRequiredService<CombinedResponseHolder>().Response = next;
    }

    /// <summary>Mutable holder so a test can swap the combined mock's single response between builds.</summary>
    private sealed class CombinedResponseHolder
    {
        public string Response = string.Empty;
    }

    /// <summary>
    /// Builds a DI provider whose mock IAiRouter returns the per-dimension findings JSON keyed on the
    /// dimension token (`"dimension": "{dim}"`) baked into the BuildBookReviewPrompt instruction. Reports
    /// <see cref="ActiveModel"/> as the model so freshly-built findings are model-fresh. Registers the full
    /// assembler chain (BookContextAssembler → BookSummaryService → ChapterBriefService) so the seeded
    /// structured briefs flow through to the assembled [BOOK_CONTEXT].
    ///
    /// The PER-DIMENSION fan-out tests force <c>BookReviewSingleCombined = false</c> so they exercise the six
    /// per-dimension calls; the combined-path tests below use the dedicated <see cref="BuildCombinedProvider"/>.
    /// </summary>
    private static ServiceProvider BuildProvider(
        out Mock<IAiRouter> routerMock,
        Dictionary<string, string> dimensionFindings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var holder = new DimensionFindingsHolder { ByDimension = dimensionFindings };
        services.AddSingleton(holder);

        routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                var instruction = req.Instruction ?? string.Empty;
                var content = string.Empty;
                foreach (var dim in new[] { "plot", "character", "pacing", "tone", "theme", "continuity" })
                {
                    // The prompt bakes `"dimension": "{dim}"` into its JSON-shape example exactly once per
                    // dimension; match on that token to serve that dimension's configured response.
                    if (instruction.Contains($"\"dimension\": \"{dim}\""))
                    {
                        holder.ByDimension.TryGetValue(dim, out var json);
                        content = json ?? string.Empty;
                        break;
                    }
                }
                return new AiResponse { Content = content, Model = ActiveModel, Provider = "test-provider" };
            });
        var router = routerMock.Object;
        services.AddSingleton(router);
        // Force the per-dimension fan-out so these tests still issue six calls (wb2-r02: the new default is
        // single-combined; the per-dimension path lives behind the toggle and must stay fully covered).
        services.Configure<AiOptions>(o => o.BookReviewSingleCombined = false);

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<BookReviewService>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<BookReviewBuildRegistry>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds a DI provider for the SINGLE-COMBINED path (BookReviewSingleCombined = true, the default). The
    /// mock IAiRouter returns ONE configured combined-response JSON for the combined call (detected by the
    /// combined prompt's unique dimensions-union token `"dimension": "plot|character|pacing|tone|theme|continuity"`),
    /// reporting <see cref="ActiveModel"/>. Same assembler chain as <see cref="BuildProvider"/>.
    /// </summary>
    private static ServiceProvider BuildCombinedProvider(
        out Mock<IAiRouter> routerMock,
        string combinedResponse)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        // Mutable holder so a test can swap the combined response between builds (mirrors DimensionFindingsHolder).
        var holder = new CombinedResponseHolder { Response = combinedResponse };
        services.AddSingleton(holder);

        routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
                new AiResponse { Content = holder.Response, Model = ActiveModel, Provider = "test-provider" });
        services.AddSingleton(routerMock.Object);
        // Explicit ON, even though it is the default, so the intent is visible at the call site.
        services.Configure<AiOptions>(o => o.BookReviewSingleCombined = true);

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<BookReviewService>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<BookReviewBuildRegistry>();

        return services.BuildServiceProvider();
    }

    /// <summary>Serialises a combined BookReviewResult-shaped JSON (findings[] spanning multiple dimensions)
    /// for the combined mock to return. Unlike <see cref="JsonFindings"/>, each finding carries its OWN
    /// dimension (the combined path trusts the model's self-label, normalised server-side).</summary>
    private static string JsonCombinedFindings(params CombinedFindingSpec[] specs)
    {
        var findings = specs.Select(s => new
        {
            dimension = s.Dimension,
            verdict = s.Verdict,
            severity = s.Severity,
            rationale = s.Rationale,
            chapterAnchors = new[] { new { order = s.Order, title = $"Chapter {s.Order}" } },
            evidence = new[] { new { chapterOrder = s.Order, excerpt = "an excerpt" } },
            suggestedAction = (string?)null
        }).ToArray();
        return JsonSerializer.Serialize(new { findings });
    }

    /// <summary>Spec for a single combined-path model finding (carries its own self-labelled dimension).</summary>
    private sealed record CombinedFindingSpec(string Dimension, string Verdict, int Severity, string Rationale, int Order);
}
