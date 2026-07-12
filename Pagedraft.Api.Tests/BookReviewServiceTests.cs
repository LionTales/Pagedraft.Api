using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    // ─── 3b. GLOSSARY REPAIR e2e: a leak-containing rationale is CLEANED on persist, yet its DedupKey (derived
    //         from the RAW rationale in UnionAndDedup) stays STABLE across a rebuild — so the user's Status is
    //         PRESERVED and the row is not duplicated. Drives the full build→UnionAndDedup→ApplyGlossaryToFindings
    //         →persist→rebuild path (f5-wire JOB 2) that BookReviewGlossaryRepairTests only exercises in isolated
    //         pieces (DedupKey byte-identity after ONE repair pass; rebuild status-preservation with CLEAN findings
    //         where the repair is a no-op). This is the single test where a LEAK flows through the whole path. ──
    [Fact]
    public async Task BuildBookReviewAsync_LeakInRationale_CleanedOnPersist_DedupKeyStable_StatusSurvivesRebuild()
    {
        // The model emits, for a Hebrew book, a plot finding whose RAW rationale carries a closed-glossary English
        // leak "(Action)". The build must (1) Hebraise the PERSISTED rationale to "(פעולה)" while (2) keeping its
        // stored DedupKey derived from the RAW "(Action)" rationale — UnionAndDedup stamps the key from the raw
        // model prose BEFORE ApplyGlossaryToFindings cleans it. That raw-derived key is what makes a REBUILD stable:
        // the model re-emits the same leak, the same raw key recomputes, the persist matches the cached row on it,
        // so the user's Status is PRESERVED and no duplicate row is inserted.
        const int anchorOrder = 1; // chapter order 1 exists (chapterCount: 2) → the finding's primary anchor order
        const string rawRationale = "הממצא מצביע על תיאור פעולה (Action) עז בפרק."; // closed-glossary "(Action)" leak
        var byDim = FindingsPerDimension(perDimensionCount: 0);
        byDim["plot"] = JsonFindings(new FindingSpec("improve", 2, rawRationale, anchorOrder));

        using var provider = BuildProvider(out _, byDim);

        // BuildProvider leaves AiOptions.AnalysisRepair null → the engine repair hook is a strict no-op. Turn it ON
        // exactly as shipped (Enabled + PerType allowing "BookReview") by mutating the resolved singleton options
        // before the build reads _aiOptions.Value.AnalysisRepair. Without this the "(Action)" leak is never cleaned.
        var aiOptions = provider.GetRequiredService<IOptions<AiOptions>>().Value;
        aiOptions.AnalysisRepair = new AnalysisRepairOptions
        {
            Enabled = true,
            GuardOnly = true, // BookReview ignores GuardOnly (glossary-only, no LLM) — set it to mirror the ship default
            PerType = new Dictionary<string, bool> { ["BookReview"] = true },
        };

        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        // The DedupKey UnionAndDedup stamped: dimension stamped 'plot', primary anchor order = 1, and the RAW
        // (un-cleaned) rationale — computed BEFORE the glossary Hebraised the prose. The cleaned "(פעולה)" rationale
        // would hash to a DIFFERENT key, so this equality proves the key came from the raw text, not the clean text.
        var rawDerivedKey = BookFinding.ComputeDedupKey("plot", anchorOrder, rawRationale);

        var afterBuild1 = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var finding = Assert.Single(afterBuild1);
        // (1) PERSISTED rationale is Hebraised: the leak is gone.
        Assert.Contains("(פעולה)", finding.Rationale);
        Assert.DoesNotContain("Action", finding.Rationale);
        // (2) ...yet the stored DedupKey is the RAW-derived one, NOT recomputed from the cleaned rationale. The
        //     repair must leave the key untouched so it stays stable across rebuilds (the crux of this test).
        Assert.Equal(rawDerivedKey, finding.DedupKey);
        Assert.Equal("plot", finding.Dimension);

        // The user acts on the finding (mirrors the SetStatus + SaveChanges pattern the rebuild tests above use).
        var tracked = await db.BookFindings.SingleAsync(f => f.BookId == bookId);
        tracked.Status = "acknowledged";
        await db.SaveChangesAsync();
        var persistedId = tracked.Id;

        // REBUILD with the SAME fake output (the model re-emits the same "(Action)" leak) + a stale-vs-briefs bump so
        // the rebuild is not a no-op. The raw-derived key recomputes identically → the persist matches the cached row.
        await TouchSummaryBaselineAsync(db, bookId);
        await svc.BuildBookReviewAsync(bookId, "he");

        var afterBuild2 = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var preserved = Assert.Single(afterBuild2); // NOT duplicated — dedup matched on the stable raw-derived key
        Assert.Equal(persistedId, preserved.Id);          // the SAME row, refreshed in place
        Assert.Equal("acknowledged", preserved.Status);   // the user's Status SURVIVED the rebuild
        Assert.Equal(rawDerivedKey, preserved.DedupKey);  // the key is still the raw-derived one
        // Still Hebraised after the rebuild (the repair re-ran on the re-emitted leak).
        Assert.Contains("(פעולה)", preserved.Rationale);
        Assert.DoesNotContain("Action", preserved.Rationale);
    }

    // ─── 3c. GLOSSARY MODE GATE (P1): the step-5b deterministic glossary must obey Ai:AnalysisRepair.Mode,
    //         mirroring UnifiedAnalysisService.ApplyAnalysisRepairAsync — Mode is a stage-selection knob layered
    //         UNDER the Enabled/PerType gate. With Mode=Off (Enabled=true, PerType allowing "BookReview") the
    //         glossary is SKIPPED even though Enabled/PerType would open the gate, so a closed-glossary English
    //         leak ("(Action)") stays un-Hebraised. With Mode=Glossary (the SHIPPED default) the SAME leak IS
    //         Hebraised, so the shipped behaviour is unchanged. Regression guard for the P1 where step 5b ignored
    //         Mode and ran the glossary regardless (Off did not disable it; Dynamic ran glossary+dynamic). ──
    [Fact]
    public async Task BuildBookReviewAsync_Step5bGlossary_ObeysAnalysisRepairMode()
    {
        const int anchorOrder = 1; // chapter order 1 exists (chapterCount: 2) → the finding's primary anchor order
        const string rawRationale = "הממצא מצביע על תיאור פעולה (Action) עז בפרק."; // closed-glossary "(Action)" leak

        // (A) Mode=Off: Enabled + PerType would open the gate, but Mode=Off is an ADDITIONAL strict no-op scoped
        //     to stage selection, so the glossary must NOT run — the "(Action)" leak survives un-Hebraised.
        {
            var byDim = FindingsPerDimension(perDimensionCount: 0);
            byDim["plot"] = JsonFindings(new FindingSpec("improve", 2, rawRationale, anchorOrder));

            using var provider = BuildProvider(out _, byDim);
            var aiOptions = provider.GetRequiredService<IOptions<AiOptions>>().Value;
            aiOptions.AnalysisRepair = new AnalysisRepairOptions
            {
                Enabled = true, // layer ON
                Mode = AnalysisRepairMode.Off, // ...but Mode=Off must skip every stage
                PerType = new Dictionary<string, bool> { ["BookReview"] = true }, // PerType allows BookReview
            };

            var db = provider.GetRequiredService<AppDbContext>();
            var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
            var svc = provider.GetRequiredService<BookReviewService>();
            await svc.BuildBookReviewAsync(bookId, "he");

            var finding = Assert.Single(
                await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
            // Mode=Off ⇒ step 5b skipped ⇒ the English leak is NOT Hebraised (glossary never ran).
            Assert.Contains("(Action)", finding.Rationale);
            Assert.DoesNotContain("(פעולה)", finding.Rationale);
        }

        // (B) Mode=Glossary (the SHIPPED default): the SAME gate is open AND the glossary stage runs, so the
        //     leak IS Hebraised. This pins the shipped-default behaviour unchanged by the Mode gate.
        {
            var byDim = FindingsPerDimension(perDimensionCount: 0);
            byDim["plot"] = JsonFindings(new FindingSpec("improve", 2, rawRationale, anchorOrder));

            using var provider = BuildProvider(out _, byDim);
            var aiOptions = provider.GetRequiredService<IOptions<AiOptions>>().Value;
            aiOptions.AnalysisRepair = new AnalysisRepairOptions
            {
                Enabled = true,
                Mode = AnalysisRepairMode.Glossary, // shipped default → glossary stage runs
                PerType = new Dictionary<string, bool> { ["BookReview"] = true },
            };

            var db = provider.GetRequiredService<AppDbContext>();
            var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
            var svc = provider.GetRequiredService<BookReviewService>();
            await svc.BuildBookReviewAsync(bookId, "he");

            var finding = Assert.Single(
                await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
            // Mode=Glossary ⇒ step 5b runs ⇒ the leak is Hebraised to "(פעולה)".
            Assert.Contains("(פעולה)", finding.Rationale);
            Assert.DoesNotContain("Action", finding.Rationale);
        }
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

    // ─── 6d. IsReady requires HasBriefs: a fresh review with the briefs gone is NOT ready ───────────

    [Fact]
    public async Task IsReady_RequiresHasBriefs_BriefsGoneAfterBuild_NotReady_AndBuildReportsBriefsMissing()
    {
        // A fresh review exists (HasReview, active model, not stale), but the structured briefs it reads are
        // GONE. IsReady must be FALSE — a build would NOT be a no-op, it would hit the briefs-absent guard and
        // return BriefsMissing — so a caller trusting IsReady (or the DTO's `ready`) cannot treat the cached
        // review as current. BuildBookReviewAsync must agree: it must NOT take the no-op fast path.
        using var provider = BuildProvider(out var routerMock, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var svc = provider.GetRequiredService<BookReviewService>();

        // Build once → a fresh, ready review (briefs present).
        var first = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(first.Ready);
        var before = await svc.GetStatusAsync(bookId, "he");
        Assert.True(before.IsReady);
        Assert.True(before.HasBriefs);

        // Remove the structured briefs AFTER the build: the cached findings remain, the summary baseline is NOT
        // rebuilt (so not stale-vs-briefs), and the model is unchanged — only HasBriefs flips to false.
        db.ChunkSummaries.RemoveRange(await db.ChunkSummaries.Where(cs => cs.BookId == bookId).ToListAsync());
        await db.SaveChangesAsync();

        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.True(status.HasReview);
        Assert.False(status.HasBriefs);
        Assert.False(status.StaleVsBriefs);
        Assert.False(status.BuiltWithDifferentModel);
        // The crux: ready must NOT be true while the briefs are gone.
        Assert.False(status.IsReady, "IsReady must require HasBriefs so it never reports ready while briefs are gone");

        // BuildBookReviewAsync agrees: NOT a no-op; it returns BriefsMissing (no fresh review produced).
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.False(result.NoOp, "with briefs gone the build must not take the IsReady no-op fast path");
        Assert.True(result.BriefsMissing);
        Assert.False(result.Ready);

        // Only the first build spent model calls (6 per-dimension); the briefs-absent path spends none.
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(6));
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

        // ONE combined window call + ONE synthesis reduce call (wb4-c04) + ONE continuity reduce call (wb4-c05,
        // the 3-chapter skeleton fits the generous budget in one window → auto-collapse to a single call) =
        // THREE calls — NOT six. The point of the single-combined default is no per-dimension fan-out; the two
        // reduce passes add exactly one call each.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        // Exactly ONE combined window call (no reduce marker), distinguished from the synthesis reduce
        // ([WINDOW_FINDINGS]) and the continuity reduce ([CONTINUITY_SKELETON]).
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.TaskType == AiTaskType.BookReview
                    && (req.Instruction == null
                        || (!req.Instruction.Contains("[WINDOW_FINDINGS]")
                            && !req.Instruction.Contains("[CONTINUITY_SKELETON]")))),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // And exactly ONE synthesis reduce call (the [WINDOW_FINDINGS] one), also BookReview-tagged.
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.TaskType == AiTaskType.BookReview
                    && req.Instruction != null && req.Instruction.Contains("[WINDOW_FINDINGS]")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // And exactly ONE continuity reduce call (the [CONTINUITY_SKELETON] one — auto-collapsed single call).
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.TaskType == AiTaskType.BookReview
                    && req.Instruction != null && req.Instruction.Contains("[CONTINUITY_SKELETON]")),
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

        // One combined window call + one synthesis reduce call + one continuity reduce call (a combined failure
        // does not retry as a fan-out). Both reduces run but add nothing here (the briefs are present so both
        // are attempted), so the total-failure outcome below is unchanged.
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));

        Assert.False(result.Ready, "a combined build whose single call failed must not be Ready");
        Assert.False(result.NoOp);
        Assert.False(result.BriefsMissing);
        // Windowed MAP: a one-window book whose only window failed reports one failed unit (window). The
        // load-bearing signal is total failure (Ready=false, FAILED job, cache preserved), asserted below.
        Assert.Equal(1, result.FailedDimensions);
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

        // Each build issues one combined window call + one synthesis reduce (wb4-c04) + one continuity reduce
        // (wb4-c05, 2-chapter skeleton fits the budget → single call): 2 builds × 3 = 6.
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(6));

        Assert.False(result.Ready, "a combined build that produced no findings must not be Ready");
        Assert.False(result.NoOp);
        // Windowed MAP: an all-EMPTY (parseable but zero findings) window is NOT counted as a failed window —
        // it parsed cleanly, it simply produced nothing. With no findings across the whole set the build is
        // still a TOTAL failure (deduped.Count == 0), asserted via Ready=false + FAILED status + cache below.
        Assert.Equal(0, result.FailedDimensions);
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

    // ═══ WINDOWED MAP (wb4-c07): AssembleWindowsAsync → sequential per-window combined calls → accumulate →
    //     union/dedup → persist ONCE. Every test forces one window PER CHAPTER via a tiny budget. ═══

    // ─── W1. N windows produce N combined calls, and findings from ALL windows persist ──────────────────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_ThreeWindows_IssuesThreeCalls_AllWindowsPersist()
    {
        // A 3-chapter book at the tiny budget splits into THREE windows (one chapter each). The service must
        // issue exactly THREE combined calls (one per window, SEQUENTIALLY) and persist the findings from ALL
        // three windows in ONE persist — nothing overwrites or deletes an earlier window's findings.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "Window 1 finding", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Window 2 finding", 1)),
                [3] = JsonCombinedFindings(new CombinedFindingSpec("pacing", "cut", 3, "Window 3 finding", 2)),
            }
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.False(result.NoOp);
        Assert.False(result.BriefsMissing);
        Assert.Equal(0, result.FailedDimensions); // no window failed

        // wb4-c06 HONEST COVERAGE: three windows, one primary chapter each, so N/N coverage across 3 windows,
        // zero failed windows, and both reduce passes ran (a full BookBrief + ordered chapter briefs existed).
        Assert.Equal(3, result.ChaptersTotal);
        Assert.Equal(3, result.ChaptersReviewed);
        Assert.Equal(result.ChaptersTotal, result.ChaptersReviewed); // the N/N honest-coverage equality
        Assert.Equal(3, result.WindowCount);
        Assert.Equal(0, result.FailedWindows);
        Assert.True(result.RanSynthesis);
        Assert.True(result.RanContinuityReduce);
        // The build message is the honest coverage claim (chapters/windows), no em-dash.
        Assert.Contains("Reviewed 3/3 chapters across 3 window(s)", result.Message);
        Assert.DoesNotContain('—', result.Message);

        // THREE window calls (the sequential MAP) + ONE synthesis reduce call (wb4-c04) + THREE continuity
        // GROUP calls (wb4-c05: the tiny budget forces one skeleton group per chapter; the final reduce is
        // skipped because every empty group produced nothing to merge) = SEVEN — not one whole-book call and
        // not six per-dimension calls.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(7));
        // Every call was BookReview-tagged.
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.TaskType == AiTaskType.BookReview),
                It.IsAny<CancellationToken>()),
            Times.Exactly(7));
        // Exactly THREE WINDOW prompts (neither reduce marker present).
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.Instruction != null
                    && !req.Instruction.Contains("[WINDOW_FINDINGS]")
                    && !req.Instruction.Contains("[CONTINUITY_SKELETON]")),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        // Exactly THREE continuity GROUP calls (the [CONTINUITY_SKELETON] ones); no final reduce (empty groups).
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.Instruction != null && req.Instruction.Contains("[CONTINUITY_SKELETON]")),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));

        // ALL THREE windows' findings survive the ONE persist (the cross-window-deletion regression guard for
        // three windows): a per-window persist would have deleted windows 1 and 2 when window 3 persisted.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(3, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == "Window 1 finding");
        Assert.Single(persisted, f => f.Rationale == "Window 2 finding");
        Assert.Single(persisted, f => f.Rationale == "Window 3 finding");
        // Dimensions taken from each window's self-labelled finding.
        Assert.Single(persisted, f => f.Dimension == "plot");
        Assert.Single(persisted, f => f.Dimension == "character");
        Assert.Single(persisted, f => f.Dimension == "pacing");
    }

    // ─── W2. THE cross-window-deletion regression guard: a 2-window build keeps BOTH windows' findings ───

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_TwoWindows_PersistsBothWindowsFindings_NoCrossWindowDeletion()
    {
        // The core regression this todo guards: persisting per window would run the delete-vanished-open loop
        // for each window, so window 2's persist (whose incoming set lacks window 1's DedupKeys) would DELETE
        // window 1's just-persisted open findings. Because we accumulate in memory and persist the whole set
        // ONCE, BOTH windows' findings must be present after the build.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(
                    new CombinedFindingSpec("plot", "keep", 1, "Chapter 0 strength", 0),
                    new CombinedFindingSpec("theme", "improve", 2, "Chapter 0 theme note", 0)),
                [2] = JsonCombinedFindings(
                    new CombinedFindingSpec("character", "improve", 2, "Chapter 1 flat cast", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5)); // two windows + one synthesis reduce (wb4-c04) + two continuity group calls
                               // (wb4-c05: tiny budget → one group per chapter; empty groups skip the final reduce)

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // Window 1's TWO findings AND window 2's one finding all coexist — window 1 was NOT wiped by window 2.
        Assert.Equal(3, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == "Chapter 0 strength");
        Assert.Single(persisted, f => f.Rationale == "Chapter 0 theme note");
        Assert.Single(persisted, f => f.Rationale == "Chapter 1 flat cast");
        Assert.Equal(3, result.FindingCount);
    }

    // ─── W3. One bad window does not abort the build; the other windows persist (partial → warning) ──────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_OneWindowFails_OthersPersist_SucceedsWithWarning()
    {
        // Window 2 returns unparseable output (a window-level failure). It must NOT abort the build: windows 1
        // and 3 still persist, and the build reports Succeeded-with-warning carrying the failed-window count.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "Good window 1", 0)),
                [2] = "{ truncated mid-stream, no closing brace", // unparseable → this window fails
                [3] = JsonCombinedFindings(new CombinedFindingSpec("pacing", "improve", 2, "Good window 3", 2)),
            }
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();
        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        // THREE window calls (the bad window is attempted, it just yields nothing) + ONE synthesis reduce call
        // + THREE continuity group calls (wb4-c05, one skeleton group per chapter; empty groups skip the final
        // reduce) = SEVEN.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(7));

        // NOT a total failure: the two good windows produced findings, so it is Ready with a warning.
        Assert.True(result.Ready);
        Assert.False(result.NoOp);
        Assert.Equal(1, result.FailedDimensions); // one failed window (back-compat field)
        Assert.Contains("warning", result.Message, StringComparison.OrdinalIgnoreCase);

        // wb4-c06 PARTIAL coverage (be-c01 HONEST-COVERAGE FIX): the failed window's chapter (order 1) is still
        // IN SCOPE for the DENOMINATOR (the build was responsible for it), so ChaptersTotal stays 3. But it is
        // NOT counted as REVIEWED — only windows whose call succeeded (1 and 3, orders 0 and 2) count toward the
        // numerator, so ChaptersReviewed is 2, and reviewed < total. The failure is surfaced explicitly via
        // FailedWindows and the warning message; the coverage claim no longer over-reports a failed window.
        Assert.Equal(3, result.ChaptersTotal);
        Assert.Equal(2, result.ChaptersReviewed);
        Assert.True(result.ChaptersReviewed < result.ChaptersTotal); // failed window lowers reviewed below total
        Assert.Equal(3, result.WindowCount);
        Assert.Equal(1, result.FailedWindows);
        Assert.Contains("Reviewed 2/3 chapters", result.Message);
        Assert.Contains("1 window(s) failed", result.Message);
        Assert.DoesNotContain('—', result.Message);

        // The two good windows persisted; the failed window contributed nothing (but did not abort the build).
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == "Good window 1");
        Assert.Single(persisted, f => f.Rationale == "Good window 3");
        Assert.DoesNotContain(persisted, f => f.Dimension == "character");

        // A partial build still finishes Succeeded (green), not Failed — the good windows produced a review.
        Assert.True(progress.TryGet(jobId, out var snap));
        Assert.Equal(AnalysisProgressStatus.Succeeded, snap!.Status);
    }

    // ─── W4. TOTAL failure (every window fails) → Failed + no persist → the prior cache survives ─────────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_AllWindowsFail_SurfacesFailed_PreservesPriorCache()
    {
        // Build 1: two windows each produce a finding the user leaves open. Build 2: EVERY window fails
        // (unparseable), so the whole accumulated set deduped to zero → a TOTAL failure. The destructive
        // persist is SKIPPED so both prior open cached findings survive; the job is FAILED, not a green finish.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Prior open window 1", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Prior open window 2", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var first = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(first.Ready);
        Assert.Equal(2, await db.BookFindings.CountAsync(f => f.BookId == bookId));

        // Build 2: force a non-no-op (bump the baseline) then make BOTH windows fail.
        await TouchSummaryBaselineAsync(db, bookId);
        holder.ByWindowIndex = new Dictionary<int, string?>
        {
            [1] = "{ broken", // unparseable → window 1 fails
            [2] = "also broken, no json", // unparseable → window 2 fails
        };

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        Assert.False(result.Ready, "every window failed → total failure → not Ready");
        Assert.False(result.NoOp);
        Assert.Contains("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AnalysisProgressStatus.Failed, progress.TryGet(jobId, out var snap) ? snap!.Status : AnalysisProgressStatus.Succeeded);

        // wb4-c06: even on a TOTAL failure the coverage shape is reported (both windows attempted → both failed),
        // so the FE can show which windows failed without treating the build as successful.
        Assert.Equal(2, result.WindowCount);
        Assert.Equal(2, result.FailedWindows);
        Assert.Equal(2, result.ChaptersTotal);

        // The PRIOR cache is intact — a bad build never wipes a good review.
        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, f => f.Rationale == "Prior open window 1");
        Assert.Single(rows, f => f.Rationale == "Prior open window 2");
        Assert.Equal(2, result.FindingCount);
    }

    // ─── W5. be-c02 SCOPED DELETE: a rebuild where a window's call FAILS (its chapter not re-reviewed) must
    //         PRESERVE that chapter's prior open findings, while a window that SUCCEEDED still refreshes its own
    //         (deletes vanished-open). Stops the partial-empty destructive wipe. ─────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_FailedWindow_PreservesThatChaptersPriorFinding_RefreshesReviewedWindow()
    {
        // Six chapters at the tiny budget → six windows (one primary each, orders 0..5). Build 1: window 1 and
        // window 6 each emit an open finding (on ch 0 and ch 5); the other windows are parsed-empty. The user
        // leaves both open. Build 2: window 1 emits a DIFFERENT ch-0 finding (the old one vanishes) and window 6
        // FAILS (null output → a window-level failure, so ch 5 is NOT in the reviewed set). be-c02: ch 0 IS
        // reviewed so its vanished-open finding is deleted/replaced (unchanged behavior); ch 5 is NOT reviewed
        // (its window failed) so its PRIOR open finding SURVIVES — the fix stops the partial wipe.
        // Every intervening window (2..5) returns a PARSED-EMPTY result so it is a clean SUCCESS (an ABSENT key
        // serves "" = empty OUTPUT, which is a window FAILURE, not a clean review — so populate them explicitly).
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Old ch0 finding", 0)),
                [2] = EmptyFindings, [3] = EmptyFindings, [4] = EmptyFindings, [5] = EmptyFindings,
                [6] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Ch5 finding must survive", 5)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 6);

        var svc = provider.GetRequiredService<BookReviewService>();
        var first = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(first.Ready);
        var afterBuild1 = await db.BookFindings.Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, afterBuild1.Count);
        Assert.Single(afterBuild1, f => f.Rationale == "Old ch0 finding");
        Assert.Single(afterBuild1, f => f.Rationale == "Ch5 finding must survive");
        // Both left OPEN by the user (no status change).

        // Build 2: force a non-no-op, then window 1 emits a NEW ch-0 finding (old ch0 vanishes), windows 2..5 stay
        // parsed-empty (clean), and window 6 FAILS (unparseable output → ch 5 NOT re-reviewed).
        await TouchSummaryBaselineAsync(db, bookId);
        holder.ByWindowIndex = new Dictionary<int, string?>
        {
            [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
            [2] = EmptyFindings, [3] = EmptyFindings, [4] = EmptyFindings, [5] = EmptyFindings,
            [6] = "{ truncated, no closing brace", // unparseable → window 6 FAILS → ch 5 NOT reviewed this build
        };

        var second = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(second.Ready);
        Assert.Equal(1, second.FailedWindows); // ONLY window 6 failed → ch 5 not in the reviewed set

        var afterBuild2 = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // ch 0 (a REVIEWED window): the old vanished-open finding was deleted, the fresh one inserted.
        Assert.DoesNotContain(afterBuild2, f => f.Rationale == "Old ch0 finding");
        Assert.Single(afterBuild2, f => f.Rationale == "Fresh ch0 finding");
        // ch 5 (a FAILED window, NOT re-reviewed): its prior open finding SURVIVES — the be-c02 fix.
        Assert.Single(afterBuild2, f => f.Rationale == "Ch5 finding must survive");
        Assert.Equal(2, afterBuild2.Count);
    }

    // ─── W6. be-c02 BOUNDARY: a PARSED-EMPTY window is a SUCCESS (be-c01) — its clean chapters ARE re-reviewed,
    //         so a vanished-open finding on them IS deleted (the window looked and no longer surfaces it). This
    //         pins the semantic boundary vs W5 (a FAILED window preserves). ────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_ParsedEmptyWindow_IsReviewed_DeletesItsVanishedOpenFinding()
    {
        // Same shape as W5 but build 2's window 6 returns a PARSED-EMPTY result ({"findings":[]}, NOT null). Per
        // be-c01 a parsed-empty window is a SUCCESS (its chapter was reviewed and is legitimately clean), so ch 5
        // IS in the reviewed set → its prior vanished-open finding IS deleted (regenerated noise the window no
        // longer surfaces). This is the opposite outcome to W5's FAILED window, proving the scope keys on
        // reviewed-vs-not, not on incoming-emptiness alone.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Old ch0 finding", 0)),
                [2] = EmptyFindings, [3] = EmptyFindings, [4] = EmptyFindings, [5] = EmptyFindings,
                [6] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Ch5 finding cleaned up", 5)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 6);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);
        Assert.Equal(2, await db.BookFindings.CountAsync(f => f.BookId == bookId));

        await TouchSummaryBaselineAsync(db, bookId);
        holder.ByWindowIndex = new Dictionary<int, string?>
        {
            [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
            [2] = EmptyFindings, [3] = EmptyFindings, [4] = EmptyFindings, [5] = EmptyFindings,
            [6] = EmptyFindings, // PARSED-EMPTY (not null) → window 6 SUCCEEDS clean → ch 5 IS reviewed
        };

        var second = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(second.Ready);
        Assert.Equal(0, second.FailedWindows); // parsed-empty is NOT a failed window

        var afterBuild2 = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.DoesNotContain(afterBuild2, f => f.Rationale == "Old ch0 finding");
        Assert.Single(afterBuild2, f => f.Rationale == "Fresh ch0 finding");
        // ch 5's window reviewed cleanly and no longer surfaces the finding → it is DELETED (open noise), NOT kept.
        Assert.DoesNotContain(afterBuild2, f => f.Rationale == "Ch5 finding cleaned up");
        Assert.Single(afterBuild2);
    }

    // ─── W7. be-c02 NO REGRESSION: a fully-successful multi-window rebuild still deletes vanished-open findings
    //         for the reviewed chapters (every reviewed chapter is in the set, so the delete behaves as before).
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_FullySuccessfulRebuild_StillDeletesVanishedOpen()
    {
        // Two windows, both succeed on both builds. Build 1: ch 0 finding + ch 1 finding, both left open. Build 2:
        // ch 0 emits a DIFFERENT finding (old vanishes) and ch 1 is parsed-empty. Both chapters ARE reviewed, so
        // both vanished-open findings are deleted — the scoping never preserves a reviewed chapter's stale open.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Old ch0", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Old ch1", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);
        Assert.Equal(2, await db.BookFindings.CountAsync(f => f.BookId == bookId));

        await TouchSummaryBaselineAsync(db, bookId);
        holder.ByWindowIndex = new Dictionary<int, string?>
        {
            [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "New ch0", 0)),
            [2] = "{ \"findings\": [] }", // ch 1 reviewed clean → its old open finding must be deleted
        };

        var second = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(second.Ready);
        Assert.Equal(0, second.FailedWindows);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Single(rows, f => f.Rationale == "New ch0");
        Assert.DoesNotContain(rows, f => f.Rationale == "Old ch0"); // reviewed ch 0 → vanished-open deleted
        Assert.DoesNotContain(rows, f => f.Rationale == "Old ch1"); // reviewed (clean) ch 1 → vanished-open deleted
        Assert.Single(rows);
    }

    // ─── W8. be-c02 MULTI-ANCHOR SCOPE (bug fix): a vanished-open finding anchored to SEVERAL chapters must be
    //         PRESERVED when ANY of those chapters was NOT re-reviewed — even if its FIRST anchor was. The old
    //         first-anchor-only scope (PrimaryChapterOrderOf) wrongly deleted such multi-chapter continuity
    //         findings whenever the first anchor happened to be reviewed. ───────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_MultiChapterFinding_LaterAnchorWindowFails_IsPreserved()
    {
        // A continuity finding anchored to BOTH ch 0 and ch 1 is open. On rebuild window 1 (ch 0) SUCCEEDS but
        // window 2 (ch 1) FAILS, so ch 1 is NOT re-reviewed. The finding vanishes from `incoming`. Because it
        // anchors an UN-reviewed chapter (ch 1), it must be PRESERVED — its absence is a failure artifact, not a
        // retraction. Pre-fix, the scope keyed on the FIRST anchor (ch 0, which WAS reviewed) and deleted it.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = "{ truncated, no closing brace", // unparseable → window 2 FAILS → ch 1 NOT re-reviewed
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // Seed the prior OPEN multi-chapter continuity finding (anchors ch 0 AND ch 1), then force a real rebuild
        // (stale vs briefs) so the persist step's scoped delete actually runs (not an idempotent no-op).
        await SeedOpenFindingAsync(db, bookId, "continuity-0-1", "Continuity break spanning ch 0 and ch 1", 0, 1);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(1, result.FailedWindows); // only window 2 failed → ch 1 not in the reviewed set

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // The multi-chapter finding SURVIVES: ch 1 (a SECOND anchor) was NOT re-reviewed this build.
        Assert.Single(rows, f => f.Rationale == "Continuity break spanning ch 0 and ch 1");
        // The reviewed chapter's fresh finding was inserted alongside it.
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Equal(2, rows.Count);
    }

    // ─── W9. be-c02 MULTI-ANCHOR BOUNDARY: the SAME multi-chapter finding IS deleted when EVERY anchored chapter
    //         was re-reviewed (both windows succeed). Proves the fix requires ALL anchors reviewed to delete — it
    //         does not blanket-preserve every multi-chapter finding. ────────────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_MultiChapterFinding_AllAnchorsReviewed_IsDeleted()
    {
        // Window 1 emits a fresh ch-0 finding (ch 0 reviewed) and window 2 is PARSED-EMPTY (a clean SUCCESS → ch 1
        // reviewed). Every anchored chapter of the seeded [0,1] finding was re-reviewed and the model no longer
        // surfaces it, so it IS deleted (regenerated noise) — the opposite outcome to W8's failed window.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = EmptyFindings, // parsed-empty → window 2 SUCCEEDS clean → ch 1 IS re-reviewed
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        await SeedOpenFindingAsync(db, bookId, "continuity-0-1", "Continuity break spanning ch 0 and ch 1", 0, 1);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(0, result.FailedWindows); // both windows succeeded (parsed-empty is not a failure)

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // Both anchored chapters were re-reviewed → the vanished-open finding is deleted.
        Assert.DoesNotContain(rows, f => f.Rationale == "Continuity break spanning ch 0 and ch 1");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Single(rows);
    }

    // ═══ COVERAGE PROVENANCE (wb4-c06): the build result + status DTO carry HONEST chapters/windows/pass
    //     counts so the FE can show "N/N chapters across W windows" instead of trusting a truncation-prone
    //     single call. ═══

    // ─── C1. A multi-window build over MANY chapters reports N/N honest coverage (the wb4-c07 "64/64" claim in
    //         miniature): every chapter is a primary in exactly one window, so ChaptersReviewed == ChaptersTotal.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_ManyChapters_ReportsHonestFullCoverage()
    {
        // Eight chapters at the tiny budget → EIGHT windows (one primary each). Every window succeeds, so the
        // coverage claim is 8/8 across 8 windows with zero failed windows — the honest end-to-end claim.
        var byWindow = new Dictionary<int, string?>();
        for (var w = 1; w <= 8; w++)
            byWindow[w] = JsonCombinedFindings(
                new CombinedFindingSpec("plot", "improve", 2, $"Window {w} note", w - 1));
        var holder = new WindowedResponseHolder { ByWindowIndex = byWindow };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 8);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.Equal(8, result.ChaptersTotal);
        Assert.Equal(8, result.ChaptersReviewed);
        Assert.Equal(result.ChaptersTotal, result.ChaptersReviewed); // N/N honest-coverage equality
        Assert.Equal(8, result.WindowCount);
        Assert.Equal(0, result.FailedWindows);
        Assert.True(result.RanSynthesis);
        Assert.True(result.RanContinuityReduce);
        Assert.Contains("Reviewed 8/8 chapters across 8 window(s)", result.Message);
        Assert.Contains("+ continuity pass", result.Message);
        Assert.DoesNotContain('—', result.Message); // no em-dash in the user-facing build message
    }

    // ─── C1a (be-c01 HEADLINE FIX). PARTIALLY-BUILT book: a chapter with content but NO fresh structured brief
    //         windows via the flat/raw fallback. The DENOMINATOR must count it (it is a reviewable primary), so
    //         ChaptersReviewed can NEVER exceed ChaptersTotal. This is the >100% ("Reviewed 64/40") regression:
    //         the old denominator (orderedChapterBriefs.Count) counted ONLY fresh-brief chapters, while the
    //         numerator counted the back-filled chapter too, so reviewed > total.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_PartialBriefBook_ReviewedNeverExceedsTotal()
    {
        // THREE chapters, each its own window on the tiny budget. Chapters 0 and 2 have a FRESH structured brief;
        // chapter 1 has content but NO ChunkSummary at all, so ComposeChapterBriefsAsync SKIPS it and it windows
        // via the raw-text back-fill — it is a reviewable PRIMARY with no fresh brief. Every window succeeds.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Ch0 note", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("pacing", "improve", 2, "Ch1 note (no brief)", 1)),
                [3] = JsonCombinedFindings(new CombinedFindingSpec("theme", "improve", 2, "Ch2 note", 2)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedPartialBriefBookAsync(db, chapterCount: 3, chapterWithoutBriefOrder: 1);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        // THE INVARIANT: reviewed is a SUBSET of total, so it can never exceed it (the >100% regression). All
        // three chapters are reviewable primaries (the brief-less one windows via raw text), and every window
        // succeeded, so this is honest 3/3 — NOT 3/2 (old denominator counted only the two fresh-brief chapters).
        Assert.Equal(3, result.ChaptersTotal);
        Assert.Equal(3, result.ChaptersReviewed);
        Assert.False(result.ChaptersReviewed > result.ChaptersTotal,
            "ChaptersReviewed must NEVER exceed ChaptersTotal — a brief-less chapter must be in the denominator too.");
        Assert.True(result.ChaptersReviewed <= result.ChaptersTotal);
        Assert.Equal(3, result.WindowCount);
        Assert.Equal(0, result.FailedWindows);
        Assert.Contains("Reviewed 3/3 chapters", result.Message);
        Assert.DoesNotContain('—', result.Message);
    }

    // ─── C1b (be-c01 HEADLINE FIX). A failed window's chapters are NOT counted as reviewed. 2-window build where
    //         window 2's call returns null (failed): ChaptersReviewed == window-1 primaries only, ChaptersTotal ==
    //         ALL primaries (both windows), reviewed < total, and the failed-window count is still reported.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_FailedWindow_NotCountedAsReviewed()
    {
        // Two chapters → two windows. Window 1 succeeds; window 2 returns unparseable output → its call FAILS, so
        // its chapter (order 1) is a reviewable primary (in the denominator) but NOT reviewed (not in numerator).
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Ch0 good", 0)),
                [2] = "{ truncated, no closing brace", // unparseable → window 2 fails
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready); // window 1 produced findings → not a total failure
        // DENOMINATOR = all primaries across BOTH windows (the failed window's chapter is still in scope).
        Assert.Equal(2, result.ChaptersTotal);
        // NUMERATOR = window-1 primaries ONLY (the failed window's chapter is NOT counted as reviewed).
        Assert.Equal(1, result.ChaptersReviewed);
        Assert.True(result.ChaptersReviewed < result.ChaptersTotal,
            "a failed window must lower ChaptersReviewed below ChaptersTotal, never inflate it.");
        Assert.Equal(1, result.FailedWindows); // the failure is still reported
        Assert.Equal(2, result.WindowCount);
        Assert.Contains("Reviewed 1/2 chapters", result.Message);
        Assert.Contains("1 window(s) failed", result.Message);
        Assert.DoesNotContain('—', result.Message);
    }

    // ─── C2. The STATUS probe reports persisted-derivable coverage (N/N once a review exists); the build-time
    //         shape (windowCount / ranSynthesis / ranContinuityReduce / failedWindows) is 0/false on the probe.
    [Fact]
    public async Task GetStatusAsync_AfterBuild_ReportsPersistedCoverage_BuildTimeShapeZero()
    {
        var byWindow = new Dictionary<int, string?>
        {
            [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "W1", 0)),
            [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "W2", 1)),
        };
        var holder = new WindowedResponseHolder { ByWindowIndex = byWindow };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();

        // BEFORE any build: no review yet → ChaptersReviewed 0, ChaptersTotal is the book's chapter count.
        var before = await svc.GetStatusAsync(bookId, "he");
        Assert.False(before.HasReview);
        Assert.Equal(0, before.ChaptersReviewed);
        Assert.Equal(2, before.ChaptersTotal);

        await svc.BuildBookReviewAsync(bookId, "he");

        // AFTER the build: the probe reports N/N persisted coverage; the build-time-only shape stays 0/false
        // (those precise counts live on BookReviewBuildResult, not the cached status probe).
        var after = await svc.GetStatusAsync(bookId, "he");
        Assert.True(after.HasReview);
        Assert.Equal(2, after.ChaptersTotal);
        Assert.Equal(2, after.ChaptersReviewed);
        Assert.Equal(0, after.WindowCount);
        Assert.False(after.RanSynthesis);
        Assert.False(after.RanContinuityReduce);
        Assert.Equal(0, after.FailedWindows);
    }

    // ─── C3 (data-c01 HEADLINE FIX). After a PARTIAL build (one window FAILED so the build response honestly
    //         reports reviewed < total), the STATUS PROBE reports the SAME persisted reviewed < total — NOT the
    //         old dishonest N/N. This is the reload-stays-honest fix. ─────────────────────────────────────────
    [Fact]
    public async Task GetStatusAsync_AfterPartialBuild_ReportsPersistedReviewedBelowTotal_NotNofN()
    {
        // Two chapters → two windows. Window 1 succeeds; window 2 returns unparseable output → its call FAILS, so
        // its chapter (order 1) is a reviewable primary (in the denominator) but NOT reviewed (not in numerator).
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Ch0 good", 0)),
                [2] = "{ truncated, no closing brace", // unparseable → window 2 fails
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var build = await svc.BuildBookReviewAsync(bookId, "he");

        // The build response is honest: reviewed (1) < total (2) — a failed window is not counted as reviewed.
        Assert.True(build.Ready);
        Assert.Equal(2, build.ChaptersTotal);
        Assert.Equal(1, build.ChaptersReviewed);
        Assert.True(build.ChaptersReviewed < build.ChaptersTotal);

        // The STATUS probe (what a page reload reads) reports the SAME persisted reviewed < total — the fix.
        // Before data-c01 this returned N/N (2/2) regardless of the failed window.
        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.True(status.HasReview);
        Assert.Equal(2, status.ChaptersTotal);
        Assert.Equal(1, status.ChaptersReviewed);
        Assert.True(status.ChaptersReviewed < status.ChaptersTotal,
            "a partial build's coverage must survive a reload as reviewed < total, never inflate to N/N.");
        // The build-time-only shape stays 0/false on the probe (unchanged).
        Assert.Equal(0, status.WindowCount);
        Assert.False(status.RanSynthesis);
        Assert.False(status.RanContinuityReduce);
        Assert.Equal(0, status.FailedWindows);
    }

    // ─── C4 (data-c01 FALLBACK). A book whose findings exist WITHOUT a persisted coverage row (an old review
    //         built before data-c01) falls back to (0, chapter count) on the probe — it does not crash and does
    //         not claim N/N. ────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetStatusAsync_ReviewWithoutCoverageRow_FallsBackToZeroOfChapterCount()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "W1", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "W2", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        // Simulate an OLD review (findings persisted, but no coverage row — built before data-c01) by deleting
        // the coverage row while leaving the findings intact.
        var coverageRows = await db.BookReviewCoverages.Where(c => c.BookId == bookId).ToListAsync();
        db.BookReviewCoverages.RemoveRange(coverageRows);
        await db.SaveChangesAsync();

        var status = await svc.GetStatusAsync(bookId, "he");
        // The review still exists (findings are present)…
        Assert.True(status.HasReview);
        // …but with no coverage row the probe falls back to (0, reviewable-chapter count) — no crash, no dishonest
        // N/N. Both seeded chapters are reviewable, so the reviewable count equals the raw chapter count here (2).
        Assert.Equal(0, status.ChaptersReviewed);
        Assert.Equal(2, status.ChaptersTotal);
    }

    // ─── C5 (Bug 1). LEGACY per-dimension path reports HONEST coverage on the RESULT (reviewed == total ==
    //         chapter count), not 0/0. Only the windowed block assigned the coverage counters, so the legacy
    //         BookReviewBuildResult under-reported 0/0 even though PersistPreservingStatusAsync wrote honest
    //         coverage. This asserts the returned result AND the persisted row AND the status probe all agree. ──
    [Fact]
    public async Task BuildBookReviewAsync_PerDimension_ReportsHonestFullCoverage_OnResultAndStatus()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.False(result.BriefsMissing);
        // The legacy path reviews the WHOLE book in one concatenated context, so every chapter is reviewed:
        // reviewed == total == chapter count. Before the fix BOTH were 0 (only the windowed block set them).
        Assert.Equal(3, result.ChaptersTotal);
        Assert.Equal(3, result.ChaptersReviewed);
        Assert.Equal(result.ChaptersTotal, result.ChaptersReviewed);

        // The persisted coverage row was always honest (the bug was only the RETURNED result); assert they agree.
        var coverage = await db.BookReviewCoverages.AsNoTracking().SingleAsync(c => c.BookId == bookId);
        Assert.Equal(3, coverage.ChaptersTotal);
        Assert.Equal(3, coverage.ChaptersReviewed);

        // And the status probe (reading that row) reports 3/3, consistent with the build result.
        var status = await svc.GetStatusAsync(bookId, "he");
        Assert.Equal(3, status.ChaptersTotal);
        Assert.Equal(3, status.ChaptersReviewed);
    }

    // ─── C6 (Bug 2). STATUS DENOMINATOR STABLE across the first build for a book with a genuinely-empty chapter.
    //         The windowed build persists ChaptersTotal as the REVIEWABLE primaries only (an empty chapter never
    //         enters a window); the pre-build fallback must report the SAME reviewable denominator, not the raw
    //         chapter count — otherwise the denominator JUMPS from 3 to 2 after the first build. ─────────────────
    [Fact]
    public async Task GetStatusAsync_BookWithEmptyChapter_FallbackDenominatorMatchesPersistedReviewableTotal()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Ch0 note", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Ch1 note", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        // Two REVIEWABLE chapters (orders 0,1: content + a fresh brief each)…
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        // …plus a THIRD, GENUINELY-EMPTY chapter (order 2): no ChunkSummary (so no brief, no flat summary) and a
        // blank ContentText. BuildChapterBlock returns a null block for it, so it never enters a window and is not
        // reviewable — the raw chapter count is 3 but the reviewable count is 2.
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 2, Title = "Empty", ContentText = "" });
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookReviewService>();

        // BEFORE any build (no coverage row → the fallback): the denominator is the REVIEWABLE count (2), NOT the
        // raw chapter count (3). Before the fix this reported 3 and then dropped to 2 after the build.
        var before = await svc.GetStatusAsync(bookId, "he");
        Assert.False(before.HasReview);
        Assert.Equal(0, before.ChaptersReviewed);
        Assert.Equal(2, before.ChaptersTotal);

        var build = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(build.Ready);
        // The build is responsible for the two reviewable chapters only; the empty chapter is never windowed.
        Assert.Equal(2, build.ChaptersTotal);
        Assert.Equal(2, build.ChaptersReviewed);

        // AFTER the build: the denominator is UNCHANGED (2/2) — it did NOT jump from 3 to 2.
        var after = await svc.GetStatusAsync(bookId, "he");
        Assert.True(after.HasReview);
        Assert.Equal(before.ChaptersTotal, after.ChaptersTotal);
        Assert.Equal(2, after.ChaptersTotal);
        Assert.Equal(2, after.ChaptersReviewed);
    }

    // ─── C7 (wb4-c06 FE delivery). A windowed build STAMPS the transient build-shape (windowCount /
    //         ranContinuityReduce / failedWindows) onto the PROGRESS job at its terminal, so the FE's LIVE
    //         progress poll can render the window detail + partial warning the persisted status probe omits
    //         (it reports 0/false). The build result and the stamped shape must agree. ─────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_StampsBuildShapeOnProgressJob_ForTheLiveTerminalPoll()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "W1", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "W2", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();

        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);
        Assert.True(result.Ready);

        // The terminal progress snapshot carries the SAME shape the build result reports — this is the channel
        // the FE reads (the status probe leaves these 0/false). Two windows, both succeeded, continuity ran.
        Assert.True(progress.TryGet(jobId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(AnalysisProgressStatus.Succeeded, snapshot!.Status);
        Assert.Equal(2, snapshot.BookReviewWindowCount);
        Assert.Equal(result.WindowCount, snapshot.BookReviewWindowCount);
        Assert.True(snapshot.BookReviewRanContinuityReduce);
        Assert.Equal(result.RanContinuityReduce, snapshot.BookReviewRanContinuityReduce);
        Assert.Equal(0, snapshot.BookReviewFailedWindows);
    }

    // ─── C8 (wb4-c06 FE delivery). A PARTIAL build stamps failedWindows > 0 on the progress job, so the FE's
    //         partial-window warning renders from the live terminal poll. ───────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_PartialFailure_StampsFailedWindowsOnProgressJob()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Ch0 good", 0)),
                [2] = "{ truncated, no closing brace", // window 2 fails → failedWindows = 1
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();

        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);
        Assert.True(result.Ready);
        Assert.Equal(1, result.FailedWindows);

        Assert.True(progress.TryGet(jobId, out var snapshot));
        Assert.Equal(2, snapshot!.BookReviewWindowCount);
        Assert.Equal(1, snapshot.BookReviewFailedWindows);
        Assert.Equal(result.FailedWindows, snapshot.BookReviewFailedWindows);
    }

    // ═══ SYNTHESIS reduce pass (wb4-c04): after the window MAP, ONE reduce call receives a COMPACT digest of
    //     every accumulated finding + the FULL BookBrief, and its findings JOIN the persisted set. ═══

    // ─── S1. The synthesis call receives the COMPACT digest (a digest line, NOT full evidence) + the full brief.
    //         Its findings join the persisted set. ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_Synthesis_ReceivesCompactDigest_AndFindingsPersist()
    {
        // Two windows each produce a finding; the synthesis pass then ADDS a book-level (arc) finding. The
        // synthesis request must carry the COMPACT digest of the window findings (dimension | order | rationale,
        // NOT their evidence excerpts) plus the FULL BookBrief in [BOOK_CONTEXT], and its finding must persist
        // alongside the window findings.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "Window one plot spine", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Window two flat cast", 1)),
            },
            // The synthesis pass adds a holistic arc-level finding the windows could not see.
            SynthesisResponse = JsonCombinedFindings(
                new CombinedFindingSpec("pacing", "improve", 2, "Global arc sags in the middle", 1)),
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        // Two window calls + one synthesis call + two continuity group calls (wb4-c05, one per chapter under the
        // tiny budget; empty groups skip the final reduce) = FIVE router calls total.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5));

        // The synthesis call was issued and captured.
        Assert.NotNull(holder.SynthesisInstruction);
        var synthInstruction = holder.SynthesisInstruction!;

        // (a) DIGEST-NOT-EVIDENCE: the compact digest carries a `dimension | order | rationale` line for each
        //     window finding, and does NOT carry the window findings' evidence excerpt text.
        Assert.Contains("[WINDOW_FINDINGS]", synthInstruction);
        Assert.Contains("plot | 0 | Window one plot spine", synthInstruction);
        Assert.Contains("character | 1 | Window two flat cast", synthInstruction);
        Assert.DoesNotContain("an excerpt", synthInstruction); // evidence excerpt was stripped from the digest

        // (b) FULL BRIEF: the full (untrimmed) BookBrief is prepended in a [BOOK_CONTEXT] block. The composed
        //     brief's themes (the union of the seeded chapter thematic markers) appear, so the synthesis has
        //     whole-book context. (Genre comes from a BookProfile, which the reviewable-book seed omits, so the
        //     themes line is the load-bearing brief signal here.)
        Assert.Contains("[BOOK_CONTEXT]", synthInstruction);
        Assert.Contains("Themes: isolation, rebirth", synthInstruction);

        // (c) SYNTHESIS FINDINGS PERSIST: the two window findings AND the synthesis arc finding all persist.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(3, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == "Window one plot spine");
        Assert.Single(persisted, f => f.Rationale == "Window two flat cast");
        Assert.Single(persisted, f => f.Rationale == "Global arc sags in the middle");
    }

    // ─── S2. A synthesis finding that DUPLICATES a window finding dedups away by key (append-before-dedup) ────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_Synthesis_DuplicateOfWindowFinding_DedupsAway()
    {
        // The synthesis pass re-emits a finding identical (dimension + primary order + rationale) to a window
        // finding. Because synthesis findings are appended to `accumulated` BEFORE UnionAndDedup, the duplicate
        // collapses to one row (first occurrence — the window finding — wins). A NEW synthesis finding survives.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "Duplicated finding", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "A window finding", 1)),
            },
            SynthesisResponse = JsonCombinedFindings(
                new CombinedFindingSpec("plot", "cut", 3, "Duplicated finding", 0),   // dup of window 1 → dedups away
                new CombinedFindingSpec("theme", "improve", 2, "New synthesis theme note", 1)), // survives
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // window1 + window2 + new-synthesis = 3 (the duplicated synthesis finding collapsed into window 1).
        Assert.Equal(3, persisted.Count);
        var dup = persisted.Single(f => f.Rationale == "Duplicated finding");
        Assert.Equal("keep", dup.Verdict);      // the WINDOW occurrence won (first), not the synthesis "cut"
        Assert.Single(persisted, f => f.Rationale == "New synthesis theme note");
    }

    // ─── S3. A synthesis FAILURE (unparseable) does NOT fail an otherwise-good build; windows still persist ──

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_SynthesisFails_DoesNotFailBuild_WindowsPersist()
    {
        // The synthesis reduce call returns unparseable output (a synthesis-level failure). It must contribute
        // ZERO findings but must NOT sink the build: the windows already produced coverage, so the build is
        // Ready and the window findings persist. FailedDimensions counts only failed WINDOWS (zero here).
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "Good window 1", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Good window 2", 1)),
            },
            SynthesisResponse = "{ truncated synthesis, no closing brace", // unparseable → synthesis fails
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();

        var progress = provider.GetRequiredService<AnalysisProgressTracker>();
        var jobId = Guid.NewGuid();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he", jobId);

        // Windows + the (failed) synthesis + the two continuity group calls (wb4-c05, one per chapter; empty
        // groups skip the final reduce) were all called = FIVE.
        routerMock.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5));
        Assert.NotNull(holder.SynthesisInstruction); // synthesis WAS attempted

        // NOT a total failure: the good windows carried the build. Ready, no failed windows, and a green finish.
        Assert.True(result.Ready, "a synthesis failure must not fail a build the windows already covered");
        Assert.False(result.NoOp);
        Assert.Equal(0, result.FailedDimensions); // synthesis failure is NOT counted as a failed window

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, persisted.Count); // only the two window findings; synthesis added nothing
        Assert.Single(persisted, f => f.Rationale == "Good window 1");
        Assert.Single(persisted, f => f.Rationale == "Good window 2");

        Assert.True(progress.TryGet(jobId, out var snap));
        Assert.Equal(AnalysisProgressStatus.Succeeded, snap!.Status);
    }

    // ─── S4. The digest is CAPPED (lowest-severity dropped) and the cap is LOGGED when over budget ────────────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_Synthesis_DigestOverBudget_CapsAndLogs()
    {
        // Window 1 emits MANY findings with long rationales so the compact digest exceeds the reduce budget.
        // The digest must be CAPPED (dropping the lowest-severity findings first) and the cap LOGGED — no silent
        // truncation. We assert on the warning entry (naming the drop) rather than on prompt bytes.
        var longRationale = new string('ל', 140); // 140 Hebrew chars → a full-length digest line
        var manySpecs = Enumerable.Range(0, 12)
            // Ascending severity so the LOW-severity ones are the drop candidates; distinct rationale per line.
            .Select(i => new CombinedFindingSpec("plot", "improve", (i % 3) + 1, $"{longRationale} #{i}", i))
            .ToArray();

        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(manySpecs), // one window, many long findings → oversized digest
            },
            SynthesisResponse = JsonCombinedFindings(
                new CombinedFindingSpec("theme", "improve", 2, "Synthesis note", 0)),
        };

        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(out _, holder, logCapture);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 1); // one chapter → one window carrying all findings

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        // The synthesis call still fired and its digest was capped: a WARNING names the cap + the drop count.
        Assert.NotNull(holder.SynthesisInstruction);
        var capWarning = logCapture.Entries.FirstOrDefault(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("digest", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("capped", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(capWarning.Message),
            "an over-budget synthesis digest must LOG a cap warning (no silent truncation)");
        Assert.Contains("dropped", capWarning.Message, StringComparison.OrdinalIgnoreCase);

        // The digest was capped to fewer than all 12 window lines: at least one full-length line was dropped.
        var digestLineCount = holder.SynthesisInstruction!
            .Split('\n')
            .Count(l => l.Contains(longRationale, StringComparison.Ordinal));
        Assert.True(digestLineCount < 12, $"expected the digest to drop some lines, kept {digestLineCount}/12");
    }

    // ═══ HIERARCHICAL CONTINUITY reduce (wb4-c05): a deterministic skeleton grouping runs the continuity prompt
    //     once when the whole skeleton fits one budget window (auto-collapse) or per-group + a final reduce when
    //     it overflows; its findings anchor to chapters and JOIN the persisted set; a group failure is non-fatal.

    // ─── C1. AUTO-COLLAPSE: the whole skeleton fits one budget window → EXACTLY ONE continuity call. ──────────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_Continuity_SkeletonFitsOneWindow_IssuesExactlyOneContinuityCall()
    {
        // A GENEROUS budget keeps the 3-chapter book ONE window AND its whole continuity skeleton ONE group, so
        // the continuity pass AUTO-COLLAPSES to a single call (no final reduce) — exactly what a bigger window or
        // cloud model gets for free. This is the load-bearing "scales to a bigger window unchanged" proof.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "Whole-book window finding", 0)),
            },
            // One continuity finding proves the single call's output persists (dimension forced to continuity).
            ContinuityResponse = JsonContinuityFindings(
                new ContinuityFindingSpec("improve", 2, "Dropped thread across chapters", new[] { 0, 2 })),
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        // EXACTLY ONE continuity call (the auto-collapse): a single [CONTINUITY_SKELETON] request, no final reduce.
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.Instruction != null && req.Instruction.Contains("[CONTINUITY_SKELETON]")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Single(holder.ContinuityInstructions);

        // That single skeleton carried ALL three chapters (dense per-chapter lines, threads + states), proving the
        // whole book was reviewed in one continuity pass rather than fanned out.
        var skeleton = holder.ContinuityInstructions[0];
        Assert.Contains("[CONTINUITY_SKELETON]", skeleton);
        Assert.Contains("#0 Chapter 0 | threads: who sent the letter? | states: Dana:fleeing", skeleton);
        Assert.Contains("#1 Chapter 1 | threads:", skeleton);
        Assert.Contains("#2 Chapter 2 | threads:", skeleton);

        // The continuity finding persisted with dimension='continuity' and its anchors on the involved chapters.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var cont = Assert.Single(persisted, f => f.Rationale == "Dropped thread across chapters");
        Assert.Equal("continuity", cont.Dimension);
        Assert.Single(persisted, f => f.Rationale == "Whole-book window finding");
    }

    // ─── C2. OVERFLOW HIERARCHY: the skeleton does not fit one window → per-GROUP calls + ONE final reduce. ────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_Continuity_SkeletonOverflows_GroupCallsThenOneFinalReduce()
    {
        // The tiny default budget forces one skeleton GROUP per chapter (3 chapters → 3 groups), so the pass runs
        // three group continuity calls and THEN one final reduce that merges their findings — 4 continuity calls.
        // Each group returns a distinct finding; the final reduce (the 4th continuity call) returns the merged
        // cross-group finding that becomes the persisted continuity result.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "W1", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "W2", 1)),
                [3] = JsonCombinedFindings(new CombinedFindingSpec("pacing", "improve", 2, "W3", 2)),
            },
            // Continuity call ordinals: 1..3 = the three group calls, 4 = the final reduce over their union.
            ContinuityByCallIndex = new Dictionary<int, string?>
            {
                [1] = JsonContinuityFindings(new ContinuityFindingSpec("improve", 2, "Group 1 thread", new[] { 0 })),
                [2] = JsonContinuityFindings(new ContinuityFindingSpec("improve", 2, "Group 2 thread", new[] { 1 })),
                [3] = JsonContinuityFindings(new ContinuityFindingSpec("improve", 2, "Group 3 thread", new[] { 2 })),
                [4] = JsonContinuityFindings(
                    new ContinuityFindingSpec("improve", 3, "Merged cross-group break", new[] { 0, 2 })),
            },
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        // FOUR continuity calls total: three group calls + one final reduce (the deterministic group+1 topology).
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.Instruction != null && req.Instruction.Contains("[CONTINUITY_SKELETON]")),
                It.IsAny<CancellationToken>()),
            Times.Exactly(4));
        Assert.Equal(4, holder.ContinuityInstructions.Count);

        // The first three continuity calls are GROUP calls whose skeletons carry chapter lines (#0/#1/#2). The
        // 4th is the FINAL reduce whose input is the DIGEST of the group findings (continuity | orders | rationale
        // lines), NOT raw chapter skeleton lines — so it merges cross-group continuity issues.
        Assert.Contains("#0 Chapter 0 | threads:", holder.ContinuityInstructions[0]);
        var finalReduceInput = holder.ContinuityInstructions[3];
        Assert.Contains("[CONTINUITY_SKELETON]", finalReduceInput);
        Assert.Contains("continuity | 0 | Group 1 thread", finalReduceInput);
        Assert.Contains("continuity | 1 | Group 2 thread", finalReduceInput);
        Assert.Contains("continuity | 2 | Group 3 thread", finalReduceInput);

        // The FINAL reduce's merged finding is the authoritative continuity result that persists (its output
        // supersedes the raw group findings). It is dimension='continuity' and anchors the involved chapters.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var merged = Assert.Single(persisted, f => f.Rationale == "Merged cross-group break");
        Assert.Equal("continuity", merged.Dimension);
        // The three window findings coexist with the merged continuity finding.
        Assert.Single(persisted, f => f.Rationale == "W1");
        Assert.Single(persisted, f => f.Rationale == "W2");
        Assert.Single(persisted, f => f.Rationale == "W3");
    }

    // ─── C3. ANCHORING + PERSIST: a continuity finding's chapterAnchors backfill to the right chapter rows. ────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_Continuity_FindingAnchorsToChapters_AndPersists()
    {
        // A single-window book (generous budget) whose continuity pass emits a break anchored to chapters 0 AND
        // 2. The persisted continuity row must carry BOTH anchors, with each anchor's chapterId backfilled from
        // the chapter Order (Phase-3 navigation), so the FE can jump to the exact chapters involved in the break.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "A window finding", 0)),
            },
            ContinuityResponse = JsonContinuityFindings(
                new ContinuityFindingSpec("improve", 3, "Timeline break between ch0 and ch2", new[] { 0, 2 })),
        };
        using var provider = BuildWindowedProvider(out _, holder, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        // Map chapter Order → Id so we can assert the persisted anchors backfilled the right chapter ids.
        var chaptersByOrder = await db.Chapters.AsNoTracking()
            .Where(c => c.BookId == bookId).ToDictionaryAsync(c => c.Order, c => c.Id);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var cont = Assert.Single(persisted, f => f.Rationale == "Timeline break between ch0 and ch2");
        Assert.Equal("continuity", cont.Dimension);

        // The anchors deserialize to the two involved chapters (orders 0 and 2), each with its chapterId
        // backfilled from the Order → Id map (Phase-3 navigation).
        var anchors = JsonSerializer.Deserialize<List<FindingChapterAnchor>>(cont.ChapterAnchorsJson)!;
        Assert.Equal(2, anchors.Count);
        Assert.Contains(anchors, a => a.Order == 0 && a.ChapterId == chaptersByOrder[0]);
        Assert.Contains(anchors, a => a.Order == 2 && a.ChapterId == chaptersByOrder[2]);
    }

    // ─── C4. GROUP FAILURE is NON-FATAL: a failed group contributes nothing; the build still succeeds. ────────

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_Continuity_GroupFailure_IsNonFatal_OtherFindingsPersist()
    {
        // The tiny budget forces 3 continuity groups. Group 2's continuity call returns unparseable output (a
        // group-level failure): it must contribute NOTHING but NOT abort the pass or the build. Groups 1 and 3
        // still produce findings, and the final reduce merges what survived — the build is Ready.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "Win 1", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Win 2", 1)),
                [3] = JsonCombinedFindings(new CombinedFindingSpec("pacing", "improve", 2, "Win 3", 2)),
            },
            ContinuityByCallIndex = new Dictionary<int, string?>
            {
                [1] = JsonContinuityFindings(new ContinuityFindingSpec("improve", 2, "Surviving group finding", new[] { 0 })),
                [2] = "{ truncated group continuity, no closing brace", // group 2 FAILS (non-fatal)
                [3] = JsonContinuityFindings(new ContinuityFindingSpec("improve", 2, "Another surviving finding", new[] { 2 })),
                // Final reduce (call 4) returns EMPTY → the pass falls back to the raw surviving group findings.
                [4] = """{ "findings": [] }""",
            },
        };
        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        // The build is Ready (a group failure is NON-FATAL) and NOT counted as a failed window/dimension.
        Assert.True(result.Ready, "a continuity group failure must not fail an otherwise-good build");
        Assert.Equal(0, result.FailedDimensions);

        // All four continuity calls were still made (the bad group was attempted, it just yielded nothing).
        Assert.Equal(4, holder.ContinuityInstructions.Count);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // The two SURVIVING group findings persist (final reduce empty → fall back to the raw group union); the
        // failed group contributed nothing but did not sink the pass. Window findings coexist.
        Assert.Single(persisted, f => f.Rationale == "Surviving group finding");
        Assert.Single(persisted, f => f.Rationale == "Another surviving finding");
        Assert.Single(persisted, f => f.Rationale == "Win 1");
        Assert.Single(persisted, f => f.Rationale == "Win 3");
        // Every surviving continuity finding is dimension='continuity'.
        Assert.All(persisted.Where(f => f.Rationale!.Contains("finding")), f => Assert.Equal("continuity", f.Dimension));
    }

    // ─── Test helpers ─────────────────────────────────────────────────────────────────────────────
    // FindingSpec, FindingsPerDimension, JsonFindings, SeedReviewableBookAsync live in BookReviewTestHelpers
    // (shared with BooksReviewControllerTests). The class-level helpers below are specific to this suite.

    /// <summary>Seeds a PARTIALLY-BUILT book (be-c01 coverage regression fixture): <paramref name="chapterCount"/>
    /// chapters each with content, but the chapter at <paramref name="chapterWithoutBriefOrder"/> gets NO
    /// ChunkSummary at all, so ComposeChapterBriefsAsync SKIPS it (no fresh structured brief) while it still
    /// windows via the raw-text back-fill — a reviewable primary with no fresh brief. The other chapters get a
    /// FRESH structured brief (so bookBrief != null and the structured windowed path is taken, not the whole-book
    /// flat fallback). This is the shape where the OLD denominator (fresh-brief count) undercounts the reviewable
    /// set and lets reviewed exceed total.</summary>
    private static async Task<Guid> SeedPartialBriefBookAsync(AppDbContext db, int chapterCount, int chapterWithoutBriefOrder)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Partial Brief Book", Language = "he" });
        for (var i = 0; i < chapterCount; i++)
        {
            var chId = Guid.NewGuid();
            db.Chapters.Add(new Chapter { Id = chId, BookId = bookId, Order = i, Title = $"Chapter {i}", ContentText = $"תוכן {i}." });
            if (i == chapterWithoutBriefOrder)
                continue; // NO ChunkSummary → no fresh structured brief; windows via raw-text back-fill
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chId, Language = "he",
                StructuredJson = StructuredBriefJson, BuiltWithModel = ActiveModel,
                StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1) // fresh: after the chapter UpdatedAt
            });
        }
        // A cached BookSummaryBaseline so the assembler has an L2 BookBrief (the structured windowed path needs it).
        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId, Language = "he",
            BookBriefJson = """{ "genre": "Fantasy", "themes": ["isolation"] }""",
            BuiltChapterCount = chapterCount, BuiltWithModel = ActiveModel
        });
        await db.SaveChangesAsync();
        return bookId;
    }

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

    /// <summary>Seeds one OPEN BookFinding (dimension='continuity', since multi-chapter findings are continuity
    /// ones) anchored to <paramref name="anchorOrders"/>, stamped with <see cref="ActiveModel"/> so it reads
    /// model-fresh. ChapterAnchorsJson is written in the SAME camelCase shape ProjectToEntity emits, so the
    /// service's ChapterOrdersOf reads every anchor order back. Used by the be-c02 multi-anchor scoped-delete
    /// tests to plant a prior finding spanning several chapters.</summary>
    private static async Task SeedOpenFindingAsync(
        AppDbContext db, Guid bookId, string dedupKey, string rationale, params int[] anchorOrders)
    {
        // Omit chapterId: FindingChapterAnchor.ChapterId is a non-nullable Guid, so a serialized "chapterId":null
        // would throw on deserialize. The service only reads each anchor's Order for the scoped delete anyway.
        var anchors = anchorOrders
            .Select(o => new { order = o, title = $"Chapter {o}" })
            .ToArray();
        var anchorsJson = JsonSerializer.Serialize(anchors,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        db.BookFindings.Add(new BookFinding
        {
            BookId = bookId,
            Language = "he",
            Dimension = "continuity",
            Verdict = "improve",
            Severity = 2,
            Rationale = rationale,
            EvidenceJson = "[]",
            ChapterAnchorsJson = anchorsJson,
            Status = "open",
            DedupKey = dedupKey,
            BuiltWithModel = ActiveModel
        });
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
        services.AddScoped<DynamicTermRepairService>();
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
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
            {
                // wb4-c04/c05: the single-combined path runs ONE synthesis reduce pass ([WINDOW_FINDINGS]) AND
                // ONE continuity reduce pass ([CONTINUITY_SKELETON], since the small seeded book's skeleton fits
                // the generous budget in one window) after its lone window. These combined-path tests assert the
                // COMBINED call's parse/dedup/persist, not the reduces, so BOTH reduce calls return an EMPTY
                // findings set — clean no-ops that keep every combined test's PERSISTED expectations intact (they
                // add two router calls, which those tests' call-count asserts now account for).
                var instruction = req.Instruction ?? string.Empty;
                var isReduce = instruction.Contains("[WINDOW_FINDINGS]", StringComparison.Ordinal)
                    || instruction.Contains("[CONTINUITY_SKELETON]", StringComparison.Ordinal);
                var content = isReduce ? "{ \"findings\": [] }" : holder.Response;
                return new AiResponse { Content = content, Model = ActiveModel, Provider = "test-provider" };
            });
        services.AddSingleton(routerMock.Object);
        // Explicit ON, even though it is the default, so the intent is visible at the call site.
        // A GENEROUS fixed budget so the small seeded books in these single-combined tests stay ONE window
        // (the windowed MAP degenerates to a single combined call). The dedicated multi-window tests below use
        // BuildWindowedProvider, which forces a tiny budget to split the book into several windows.
        services.Configure<AiOptions>(o =>
        {
            o.BookReviewSingleCombined = true;
            o.BookContextTokenBudget = 100_000; // one window for any small test book
        });

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<BookReviewService>();
        services.AddScoped<DynamicTermRepairService>();
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<BookReviewBuildRegistry>();

        return services.BuildServiceProvider();
    }

    /// <summary>Mutable per-window response map keyed on the 1-based window index (wb4-c07 windowed MAP).
    /// The wb4-c04 SYNTHESIS reduce pass is served from <see cref="SynthesisResponse"/> (detected by the
    /// [WINDOW_FINDINGS] marker the synthesis prompt carries, which the windowed prompts never do), and the
    /// synthesis request's Instruction is captured into <see cref="SynthesisInstruction"/> for assertions.</summary>
    private sealed class WindowedResponseHolder
    {
        public Dictionary<int, string?> ByWindowIndex { get; set; } = new();

        /// <summary>Canned JSON the SYNTHESIS reduce call returns. Null (default) → empty output → the
        /// synthesis pass contributes zero findings (models the "no synthesis" / failure case).</summary>
        public string? SynthesisResponse { get; set; }

        /// <summary>The Instruction of the LAST synthesis call the mock saw (the compact digest + full brief +
        /// synthesis prompt body). Null until a synthesis call is issued.</summary>
        public string? SynthesisInstruction { get; set; }

        /// <summary>Canned JSON EVERY wb4-c05 CONTINUITY reduce call returns (group calls AND the final reduce,
        /// all carrying [CONTINUITY_SKELETON]). Null (default) → empty output → the continuity pass contributes
        /// zero findings, so the existing window/synthesis tests are unaffected by its added calls. A dedicated
        /// continuity test overrides this to prove the pass's findings persist. When
        /// <see cref="ContinuityByCallIndex"/> is set it takes precedence per call.</summary>
        public string? ContinuityResponse { get; set; }

        /// <summary>Optional per-call continuity responses keyed on the 1-based CONTINUITY call ordinal (1 =
        /// first group call, …, last = the final reduce), so a multi-group test can hand each group a distinct
        /// findings set and assert the final reduce merged them. Overrides <see cref="ContinuityResponse"/> for
        /// any index present.</summary>
        public Dictionary<int, string?>? ContinuityByCallIndex { get; set; }

        /// <summary>Every CONTINUITY reduce call's Instruction, in call order (group calls then final reduce),
        /// captured so a test can assert the skeleton shape, the group→final-reduce topology, and anchoring.</summary>
        public List<string> ContinuityInstructions { get; } = new();
    }

    /// <summary>
    /// Builds a DI provider for the WINDOWED MAP path (single-combined ON). A tiny fixed BookContextTokenBudget
    /// forces the BookContextAssembler to split the seeded book into ONE WINDOW PER CHAPTER (each chapter is a
    /// window's sole primary), so an N-chapter book yields N windows. The mock IAiRouter reads the 1-based
    /// window index out of the wb4-c03 window frame ("(זהו חלון {n})") baked into each windowed prompt and
    /// returns that window's configured canned JSON — so a test can hand each window a DIFFERENT findings set
    /// (or a failure) and assert every window's findings survive the ONE persist.
    /// </summary>
    private static ServiceProvider BuildWindowedProvider(
        out Mock<IAiRouter> routerMock,
        WindowedResponseHolder holder,
        CapturingLoggerProvider? logCapture = null,
        int? bookContextTokenBudget = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (logCapture != null)
            {
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddProvider(logCapture);
            }
        });
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();
        services.AddSingleton(holder);

        routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                var instruction = req.Instruction ?? string.Empty;
                // The wb4-c04 SYNTHESIS reduce call is the only one carrying the [WINDOW_FINDINGS] marker (the
                // windowed prompts never do). Detect it, capture its instruction, and serve the synthesis
                // response so a test can assert the digest shape and that synthesis findings persist.
                if (instruction.Contains("[WINDOW_FINDINGS]", StringComparison.Ordinal))
                {
                    holder.SynthesisInstruction = instruction;
                    return new AiResponse { Content = holder.SynthesisResponse ?? string.Empty, Model = ActiveModel, Provider = "test-provider" };
                }
                // The wb4-c05 CONTINUITY reduce calls (group calls + the final reduce) are the only ones carrying
                // the [CONTINUITY_SKELETON] marker. Capture each in call order and serve either a per-call
                // response (ContinuityByCallIndex) or the shared ContinuityResponse. Default (null) = empty, so
                // the continuity pass adds no findings and the window/synthesis tests are unaffected by its calls.
                if (instruction.Contains("[CONTINUITY_SKELETON]", StringComparison.Ordinal))
                {
                    holder.ContinuityInstructions.Add(instruction);
                    var callIndex = holder.ContinuityInstructions.Count; // 1-based ordinal of this continuity call
                    string? json = null;
                    if (holder.ContinuityByCallIndex != null && holder.ContinuityByCallIndex.TryGetValue(callIndex, out var perCall))
                        json = perCall;
                    else
                        json = holder.ContinuityResponse;
                    return new AiResponse { Content = json ?? string.Empty, Model = ActiveModel, Provider = "test-provider" };
                }
                // The wb4-c03 Hebrew window frame ends the window index with "(זהו חלון {n})". Pull {n} out so
                // the mock serves the right window's response. Fall back to window 1 if the token is absent.
                var windowIndex = ExtractWindowIndex(instruction);
                holder.ByWindowIndex.TryGetValue(windowIndex, out var wjson);
                return new AiResponse { Content = wjson ?? string.Empty, Model = ActiveModel, Provider = "test-provider" };
            });
        services.AddSingleton(routerMock.Object);

        services.Configure<AiOptions>(o =>
        {
            o.BookReviewSingleCombined = true;
            // A tiny positive budget wins verbatim (EffectiveBookContextTokenBudget), so every chapter becomes
            // its own window (the loop always keeps at least one primary chapter, then the next exceeds). Tests
            // that need the SYNTHESIS digest to fit (so windows + synthesis both run) pass a larger budget.
            o.BookContextTokenBudget = bookContextTokenBudget ?? 1;
            o.BookReviewWindowOverlapChapters = 0; // no overlap: keep the per-window primary set clean for asserts
        });

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<BookReviewService>();
        services.AddScoped<DynamicTermRepairService>();
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<BookReviewBuildRegistry>();

        return services.BuildServiceProvider();
    }

    /// <summary>Minimal in-memory <see cref="ILoggerProvider"/> that records every log entry's level +
    /// rendered message, so a test can assert the wb4-c04 digest-cap warning fired (no silent truncation).</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        private readonly object _gate = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        internal void Add(LogLevel level, string message)
        {
            lock (_gate) Entries.Add((level, message));
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _owner.Add(logLevel, formatter(state, exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }

    /// <summary>Extracts the 1-based window index from the wb4-c03 Hebrew window frame token "(זהו חלון {n})".
    /// Returns 1 when the token is absent (defensive; every windowed prompt carries it).</summary>
    private static int ExtractWindowIndex(string instruction)
    {
        const string marker = "זהו חלון ";
        var at = instruction.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return 1;
        var i = at + marker.Length;
        var digits = new System.Text.StringBuilder();
        while (i < instruction.Length && char.IsDigit(instruction[i])) { digits.Append(instruction[i]); i++; }
        return digits.Length > 0 && int.TryParse(digits.ToString(), out var n) ? n : 1;
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

    /// <summary>A PARSED-EMPTY window response ({"findings":[]}): a clean SUCCESS (the window reviewed its chapter
    /// and surfaced nothing), distinct from an ABSENT map key (empty OUTPUT "" = a window FAILURE). be-c02 tests
    /// use this to make intervening windows reviewed-clean so the reviewed set is exact.</summary>
    private const string EmptyFindings = "{ \"findings\": [] }";

    /// <summary>Serialises a BookReviewResult-shaped JSON for the CONTINUITY reduce mock to return: each finding
    /// is dimension='continuity' and can anchor MULTIPLE chapter orders (a continuity break spans chapters).</summary>
    private static string JsonContinuityFindings(params ContinuityFindingSpec[] specs)
    {
        var findings = specs.Select(s => new
        {
            dimension = "continuity",
            verdict = s.Verdict,
            severity = s.Severity,
            rationale = s.Rationale,
            chapterAnchors = s.Orders.Select(o => new { order = o, title = $"Chapter {o}" }).ToArray(),
            evidence = s.Orders.Select(o => new { chapterOrder = o, excerpt = "a skeleton excerpt" }).ToArray(),
            suggestedAction = (string?)null
        }).ToArray();
        return JsonSerializer.Serialize(new { findings });
    }

    /// <summary>Spec for a single continuity-pass model finding: a verdict/severity/rationale plus the chapter
    /// orders the break involves (anchored on all of them).</summary>
    private sealed record ContinuityFindingSpec(string Verdict, int Severity, string Rationale, int[] Orders);
}
