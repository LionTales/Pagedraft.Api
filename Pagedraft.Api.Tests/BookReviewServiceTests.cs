using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    // ─── 2a. b4: near-duplicate (RE-WORDED) findings collapse on the REAL build path ───────────────

    [Fact]
    public async Task BuildBookReviewAsync_CollapsesRewordedDuplicateFindings_KeepingOneRowPerRealFinding()
    {
        // WIRING PROOF. The collapse logic is unit-tested in BookFindingNearDuplicateCollapseTests, but a
        // collapser that is never CALLED would leave every one of those tests green while the user still sees
        // the duplicate. This drives the whole build (router → per-dimension parse → UnionAndDedup → persist)
        // and asserts on the PERSISTED rows.
        //
        // The two rationales are REAL: they are the character-dimension pair from book 2cf6fcf2, where the model
        // emitted the same finding twice with one adjective added ("ומרשימה"). Both anchor chapter 1, so they
        // land in the same (dimension, resolved order) bucket. Their dedup keys DIFFER — the exact-key dedup
        // (tested above) cannot touch them — so if the collapse pass is not wired in, BOTH rows persist.
        const string morganShort = "מורגן מציג קשת דמויות ברורה של התמודדות עם פחד וניסיון להתגבר עליו באופן עצמאי.";
        const string morganLong = "מורגן מציג קשת דמויות ברורה ומרשימה של התמודדות עם פחד וניסיון להתגבר עליו באופן עצמאי.";
        Assert.NotEqual(
            BookFinding.ComputeDedupKey("plot", 1, morganShort),
            BookFinding.ComputeDedupKey("plot", 1, morganLong));

        var byDim = FindingsPerDimension(perDimensionCount: 1);
        byDim["plot"] = JsonFindings(
            new FindingSpec("keep", 1, morganShort, 1),
            new FindingSpec("keep", 1, morganLong, 1));

        using var provider = BuildProvider(out _, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();

        // plot collapsed 2 → 1 despite the two DISTINCT dedup keys; the other five dimensions are untouched.
        Assert.Equal(6, persisted.Count);
        var plot = Assert.Single(persisted, f => f.Dimension == "plot");

        // The survivor rule (equal severity → the most specific variant), asserted on the row the user reads.
        Assert.Equal(morganLong, plot.Rationale);
    }

    // ─── 2a-bis. b4c: the SAME finding filed under TWO DIMENSIONS collapses on the REAL build path ──

    [Fact]
    public async Task BuildBookReviewAsync_SameFindingFiledUnderTwoDimensions_PersistsOnceUnderTheAnchoredOne()
    {
        // WIRING PROOF for b4c. Both rationales are REAL rows of book A63A6E02, read from the DB on 2026-07-13 —
        // the pair the user saw on screen as cards 3 and 5. The model emitted ONE criticism twice: once under
        // CHARACTER anchored to chapter 2, once under CONTINUITY with no anchor at all (the second is the first
        // minus the words "בפרק Ktiv"). Different dimension AND different anchor, so the two dedup keys differ and
        // b4's (dimension, order) bucketing never compared them: pre-b4c BOTH rows persisted and BOTH rendered.
        const string characterAnchored =
            "המעבר לדמות הסופר בפרק Ktiv יוצר שינוי חד בטון ובמצב הנפשי של הגיבור, מה שעלול ליצור תחושת ניתוק.";
        const string continuityNoAnchor =
            "המעבר לדמות הסופר יוצר שינוי חד בטון ובמצב הנפשי של הגיבור, מה שעלול ליצור תחושת ניתוק.";
        Assert.NotEqual(
            BookFinding.ComputeDedupKey("character", 2, characterAnchored),
            BookFinding.ComputeDedupKey("continuity", null, continuityNoAnchor));

        var byDim = FindingsPerDimension(perDimensionCount: 0);
        byDim["character"] = JsonFindings(new FindingSpec("improve", 2, characterAnchored, 2));
        byDim["continuity"] = JsonSerializer.Serialize(new
        {
            findings = new[]
            {
                new
                {
                    dimension = "ignored", // the service stamps the called dimension
                    verdict = "improve",
                    severity = 3,          // the book-wide copy is the HIGHER severity: the survivor must take it
                    rationale = continuityNoAnchor,
                    chapterAnchors = Array.Empty<object>(),
                    evidence = Array.Empty<object>(),
                },
            },
        });

        using var provider = BuildProvider(out _, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3); // chapters 0..2, so anchor 2 is REAL

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();

        // ONE row, not two. The ANCHORED copy survives (b4b's constraint, carried across dimensions), so the user
        // reads the finding under דמויות with its chapter-2 link instead of twice under two headings.
        var survivor = Assert.Single(persisted);
        Assert.Equal("character", survivor.Dimension);
        Assert.Contains("שינוי חד בטון", survivor.Rationale);
        Assert.Contains("\"order\":2", survivor.ChapterAnchorsJson.Replace(" ", string.Empty));

        // ...and it took the MAX severity of the pair: the anchored copy is forced to win regardless of severity,
        // so keeping its own 2 would let an arbitrary anchor coin-flip DOWNGRADE the finding the user triages on.
        Assert.Equal(3, survivor.Severity);
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

    // ─── 3d. h1-observable-gate-skip / be-c02: the Enabled+PerType gate over the BookReview repair layer must
    //     leave an observable trace naming WHICH of the three reasons closed it (null block / Enabled=false /
    //     PerType exclusion — each has a different fix), Debug-only (a routine PerType exclusion must never
    //     rise to INFO/WARN).
    //
    //     be-c02 REWROTE these from a DYNAMIC-stage contract to a LAYER contract. h1 left the two sibling hooks
    //     reporting the SAME closed gate asymmetrically: the glossary skip was logged only from INSIDE
    //     ApplyGlossaryToFindings, which Mode=Off/Dynamic never CALL, while the dynamic skip was logged
    //     unconditionally. So a Mode=Off build emitted exactly ONE line, naming only the dynamic stage — an
    //     operator read "the dynamic stage was gated out" when the ENTIRE layer was, and the glossary half of
    //     the path was invisible under 2 of the 4 Modes; a Mode=GlossaryThenDynamic build logged the same
    //     reason TWICE. The gate is now evaluated + logged ONCE for the whole layer (mirroring
    //     UnifiedAnalysisService.ApplyAnalysisRepairAsync's single per-call gate log), naming BOTH stages.
    //
    //     These tests therefore assert the line is UNIQUE across the whole build (pinning the de-duplication)
    //     and that it names BOTH stages (pinning the symmetry) — not merely that "a line mentioning dynamic"
    //     exists, which is what they checked before. ───

    /// <summary>Every "gate closed" line the build emitted, across ALL categories — deliberately NOT filtered
    /// to one stage, so a per-stage duplicate would show up as an extra entry.</summary>
    private static IReadOnlyList<(LogLevel Level, string Message)> RepairGateSkipLines(
        CapturingLoggerProvider logCapture) =>
        logCapture.Entries
            .Where(e => e.Message.Contains("gate closed", StringComparison.Ordinal))
            .ToList();

    /// <summary>Asserts the ONE layer-gate line: Debug, names the type + reason, and names BOTH gated stages
    /// (glossary AND dynamic) so neither half of the layer can go silent again.</summary>
    private static void AssertSingleLayerGateSkipLine(CapturingLoggerProvider logCapture, string expectedReason)
    {
        var entry = Assert.Single(RepairGateSkipLines(logCapture)); // exactly ONE line for the whole layer
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("BookReview", entry.Message, StringComparison.Ordinal);
        Assert.Contains(expectedReason, entry.Message, StringComparison.Ordinal);
        Assert.Contains("glossary", entry.Message, StringComparison.Ordinal);
        Assert.Contains("dynamic", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildBookReviewAsync_RepairLayerGate_PerTypeExcludesBookReview_LogsOneDebugLineNamingBothStages()
    {
        var byDim = FindingsPerDimension(perDimensionCount: 1);
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildProvider(out _, byDim, logCapture);
        var aiOptions = provider.GetRequiredService<IOptions<AiOptions>>().Value;
        aiOptions.AnalysisRepair = new AnalysisRepairOptions
        {
            Enabled = true,
            // Mode WOULD select BOTH stages — so pre-be-c02 this build logged the same reason twice (once from
            // inside ApplyGlossaryToFindings, once from the dynamic block). Assert.Single now pins that down.
            Mode = AnalysisRepairMode.GlossaryThenDynamic,
            PerType = new Dictionary<string, bool> { ["LiteraryAnalysis"] = true }, // ...but PerType excludes BookReview
        };

        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        AssertSingleLayerGateSkipLine(logCapture, "PerTypeExcluded");
    }

    [Fact]
    public async Task BuildBookReviewAsync_RepairLayerGate_Disabled_LogsOneDebugLineNamingBothStages()
    {
        var byDim = FindingsPerDimension(perDimensionCount: 1);
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildProvider(out _, byDim, logCapture);
        var aiOptions = provider.GetRequiredService<IOptions<AiOptions>>().Value;
        aiOptions.AnalysisRepair = new AnalysisRepairOptions
        {
            Enabled = false,
            Mode = AnalysisRepairMode.GlossaryThenDynamic,
        };

        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        AssertSingleLayerGateSkipLine(logCapture, "Disabled");
    }

    [Fact]
    public async Task BuildBookReviewAsync_RepairLayerGate_NullConfig_LogsOneDebugLineNamingBothStages()
    {
        var byDim = FindingsPerDimension(perDimensionCount: 1);
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildProvider(out _, byDim, logCapture);
        // aiOptions.AnalysisRepair stays null (BuildProvider's default) → NullConfig.

        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        AssertSingleLayerGateSkipLine(logCapture, "NullConfig");
    }

    [Fact]
    public async Task BuildBookReviewAsync_RepairLayerGate_Allowed_LogsNoSkipLine()
    {
        var byDim = FindingsPerDimension(perDimensionCount: 1);
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildProvider(out _, byDim, logCapture);
        var aiOptions = provider.GetRequiredService<IOptions<AiOptions>>().Value;
        aiOptions.AnalysisRepair = new AnalysisRepairOptions
        {
            Enabled = true,
            Mode = AnalysisRepairMode.GlossaryThenDynamic,
            PerType = new Dictionary<string, bool> { ["BookReview"] = true }, // no exclusion → allowed
        };

        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        Assert.Empty(RepairGateSkipLines(logCapture));
    }

    // ─── be-c02 REGRESSION (the case with NO test before this todo): under a Mode that does NOT select the
    //     glossary stage (Off / Dynamic), the glossary hook is never CALLED, so its INTERNAL gate line can
    //     never fire. Before the hoist that meant a closed Enabled/PerType gate produced exactly ONE line,
    //     about the DYNAMIC stage only — the glossary half of the BookReview repair path was completely
    //     unobservable, the very failure h1 exists to eliminate. The layer line must name the glossary stage
    //     under these Modes too. Mode is reported (never consulted) so the operator can also see that the
    //     stage selection was irrelevant: Enabled/PerType closed everything. ───
    [Theory]
    [InlineData(AnalysisRepairMode.Off)]
    [InlineData(AnalysisRepairMode.Dynamic)]
    public async Task BuildBookReviewAsync_RepairLayerGate_ClosedUnderModeThatSkipsGlossary_StillReportsGlossaryStage(
        AnalysisRepairMode mode)
    {
        var byDim = FindingsPerDimension(perDimensionCount: 1);
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildProvider(out _, byDim, logCapture);
        var aiOptions = provider.GetRequiredService<IOptions<AiOptions>>().Value;
        aiOptions.AnalysisRepair = new AnalysisRepairOptions
        {
            Enabled = false, // the LAYER gate is closed...
            Mode = mode,     // ...under a Mode that would not have selected the glossary stage anyway
            PerType = new Dictionary<string, bool> { ["BookReview"] = true },
        };

        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var entry = Assert.Single(RepairGateSkipLines(logCapture));
        Assert.Equal(LogLevel.Debug, entry.Level); // a gated-out type is a normal steady state
        Assert.Contains("BookReview", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Disabled", entry.Message, StringComparison.Ordinal);
        // THE POINT: the GLOSSARY stage is named even though ApplyGlossaryToFindings was never invoked.
        Assert.Contains("glossary", entry.Message, StringComparison.Ordinal);
        Assert.Contains("dynamic", entry.Message, StringComparison.Ordinal);
        // ...and the Mode that did not select it is reported, so "the layer was off" is unambiguous.
        Assert.Contains($"Mode={mode}", entry.Message, StringComparison.Ordinal);
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
        // Windowed MAP: an EMPTY (parseable but zero findings) window is not counted as a FAILED window — we did
        // not observe a failure, only a suspected truncation (be-c01), so FailedDimensions stays 0. It is not
        // counted as REVIEWED either. With no findings across the whole set the build is a TOTAL failure
        // (deduped.Count == 0) regardless, asserted via Ready=false + FAILED status + the preserved cache below.
        Assert.Equal(0, result.FailedDimensions);
        Assert.Equal(0, result.ChaptersReviewed); // the empty window reviewed nothing (be-c01)
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
        // Windows 2..5 are PARSED-EMPTY filler. They anchor nothing in this test, so their outcome does not affect
        // it; note that under be-c01 they are NOT reviewed either (an empty window is a suspected truncation), they
        // are simply not a hard FAILURE, which is what the FailedWindows assertion below pins.
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

    // ─── W6 (be-c01 P0, INVERTED 2026-07-13 — this test used to PIN THE BUG). A PARSED-EMPTY window is NOT a
    //         "clean success": it is a SUSPECTED TRUNCATION, so its chapters are NOT re-reviewed and a still-open
    //         finding on them is PRESERVED. Previously window 6's empty result marked ch 5 REVIEWED and DELETED the
    //         open finding there — a build that produced NOTHING for ch 5 destroyed ch 5's finding.
    //         The model cannot express "these chapters are fine" distinctly from "my output was cut off" (see
    //         WindowOutcome), so the pessimistic reading is the only safe one. ─────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_ParsedEmptyWindow_IsNotReviewed_PreservesItsStillOpenFinding()
    {
        // Same shape as W5, but build 2's window 6 returns a PARSED-EMPTY result ({"findings":[]}, NOT null).
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Old ch0 finding", 0)),
                [2] = EmptyFindings, [3] = EmptyFindings, [4] = EmptyFindings, [5] = EmptyFindings,
                [6] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Ch5 finding must survive", 5)),
            }
        };
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(out _, holder, logCapture);
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
            [6] = EmptyFindings, // PARSED-EMPTY → SUSPECTED TRUNCATION → ch 5 is NOT reviewed
        };
        logCapture.Entries.Clear();

        var second = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(second.Ready);
        // NOT counted as a hard failure (we did not observe one) — the honest surface is the COVERAGE gap: only
        // ch 0 produced findings, so 1 of 6 chapters was reviewed, and the message names the empty windows.
        Assert.Equal(0, second.FailedWindows);
        Assert.Equal(1, second.ChaptersReviewed);
        Assert.Equal(6, second.ChaptersTotal);
        Assert.Contains("5 window(s) returned no findings", second.Message);

        // ...and the suspected truncation is LOUD, not silent: one WARNING per empty window.
        var warnings = logCapture.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("returned ZERO findings"))
            .ToList();
        Assert.Equal(5, warnings.Count);

        var afterBuild2 = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // ch 0's window PRODUCED findings → it really was re-reviewed → its vanished-open finding is still deleted.
        Assert.DoesNotContain(afterBuild2, f => f.Rationale == "Old ch0 finding");
        Assert.Single(afterBuild2, f => f.Rationale == "Fresh ch0 finding");
        // ch 5's window returned NOTHING → we cannot tell "clean" from "truncated" → the open finding SURVIVES.
        Assert.Single(afterBuild2, f => f.Rationale == "Ch5 finding must survive");
        Assert.Equal(2, afterBuild2.Count);
    }

    // ─── W6a (be-c01 P0, THE HEADLINE DATA-LOSS CASE). A 2-window build where window 2's model call silently
    //         truncates to a bare `{}` must NOT delete the still-open BOOK-WIDE finding. `{}` deserializes to
    //         Findings = new() (EMPTY but NOT null), which the old code read as a clean success: window 2's chapter
    //         joined reviewedPrimaryOrders, `reviewed ⊇ real` became TRUE, and b3's book-wide rule deleted the row.
    //         The build persists (window 1 produced findings), so the total-failure guard does not save it. ───────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_TruncatedWindow_DoesNotDeleteAStillOpenBookWideFinding()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = TruncatedEmptyObject, // the silent truncation: parses, carries nothing
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // A prior BOOK-WIDE (no-anchor) finding, still open, which this build does not re-emit.
        await SeedOpenFindingAsync(db, bookId, "book-wide-key", "Book-wide finding, no anchors");
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        // The build SUCCEEDS and PERSISTS (window 1 produced a finding), so nothing but the reviewed-set guard
        // stands between the truncated window and the delete pass.
        Assert.True(result.Ready);
        Assert.Equal(1, result.ChaptersReviewed);
        Assert.Equal(2, result.ChaptersTotal);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // THE P0: the book-wide finding is PRESERVED. Its scope is the whole book, and ch 1 was never reviewed.
        Assert.Single(rows, f => f.Rationale == "Book-wide finding, no anchors");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Equal(2, rows.Count);
    }

    // ─── W6b (be-c01 P0). The SAME build must not delete a still-open finding ANCHORED INSIDE the truncated
    //         window's chapters — while the finding anchored in the window that DID produce findings is still
    //         deleted (proving the guard is SCOPED to the truncated window, not a blanket "preserve everything"). ─
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_TruncatedWindow_DoesNotDeleteAStillOpenFindingAnchoredInIt()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = TruncatedEmptyObject, // silent truncation → ch 1 NOT reviewed
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        await SeedOpenFindingAsync(db, bookId, "ch1-key", "Anchored in the truncated window (ch 1)", 1);
        await SeedOpenFindingAsync(db, bookId, "ch0-key", "Anchored in the window that produced findings (ch 0)", 0);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // PRESERVED: ch 1's window produced nothing, so its absence from `incoming` proves nothing.
        Assert.Single(rows, f => f.Rationale == "Anchored in the truncated window (ch 1)");
        // DELETED: ch 0's window DID produce findings and no longer surfaces this one → regenerated noise. The
        // guard must not over-correct into "never delete anything when any window is empty".
        Assert.DoesNotContain(rows, f => f.Rationale == "Anchored in the window that produced findings (ch 0)");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Equal(2, rows.Count);
    }

    // ─── W6c (be-c01). An ALL-EMPTY windowed build produces zero findings, so it is a TOTAL FAILURE and the
    //         destructive persist is SKIPPED ENTIRELY (pre-existing guard — must not regress). Every prior open
    //         row survives, including ones anchored in chapters this build "looked at". ──────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_AllWindowsTruncateToEmpty_SkipsThePersist_PreservesEveryOpenRow()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Prior ch0 finding", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Prior ch1 finding", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);
        Assert.Equal(2, await db.BookFindings.CountAsync(f => f.BookId == bookId));

        // Build 2: BOTH windows truncate to `{}`.
        await TouchSummaryBaselineAsync(db, bookId);
        holder.ByWindowIndex = new Dictionary<int, string?>
        {
            [1] = TruncatedEmptyObject,
            [2] = TruncatedEmptyObject,
        };

        var second = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.False(second.Ready, "zero findings across the whole build is a TOTAL failure, not a clean review");
        Assert.Contains("failed", second.Message, StringComparison.OrdinalIgnoreCase);

        // The persist never ran → the cached review is intact, both rows still open.
        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("open", r.Status));
        Assert.Single(rows, f => f.Rationale == "Prior ch0 finding");
        Assert.Single(rows, f => f.Rationale == "Prior ch1 finding");
    }

    // ─── W6d (be-c01). The CLASSIFIER itself, in isolation: the three shapes a window call can come back as, and
    //         the ONE that licenses the destructive delete. `{}` and `{"findings":[]}` are the same thing to the
    //         parser, which is exactly why "empty" cannot mean "clean". ─────────────────────────────────────────
    [Fact]
    public void WindowOutcomes_Classify_OnlyAWindowThatProducedFindingsCountsAsReviewed()
    {
        // A failed call (null): the model errored or its output did not parse.
        Assert.Equal(WindowOutcome.Failed, WindowOutcomes.Classify(null));
        Assert.False(WindowOutcomes.Classify(null).CountsAsReviewed());

        // A parsed-but-empty call — and BOTH JSON shapes reach it identically, which is the whole argument: `{}`
        // (the truncation) is byte-for-byte as "clean" as an explicit empty array.
        var fromEmptyArray = JsonSerializer.Deserialize<BookReviewResult>("""{"findings": []}""")!.Findings;
        var fromBareObject = JsonSerializer.Deserialize<BookReviewResult>("{}")!.Findings;
        Assert.Empty(fromEmptyArray);
        Assert.Empty(fromBareObject); // NOT null: the `= new()` initialiser survives an absent key
        Assert.Equal(WindowOutcome.EmptySuspectedTruncation, WindowOutcomes.Classify(fromEmptyArray));
        Assert.Equal(WindowOutcome.EmptySuspectedTruncation, WindowOutcomes.Classify(fromBareObject));
        Assert.False(WindowOutcomes.Classify(fromBareObject).CountsAsReviewed());

        // The only outcome that proves the model actually reviewed the chapters.
        var produced = new List<BookFindingItem> { new() { Dimension = "plot", Rationale = "something" } };
        Assert.Equal(WindowOutcome.Reviewed, WindowOutcomes.Classify(produced));
        Assert.True(WindowOutcomes.Classify(produced).CountsAsReviewed());
    }

    [Fact]
    public void WindowOutcomes_CountsAsReviewed_IsFALSEForEveryOutcomeExceptReviewed_SoANewMemberIsFailClosed()
    {
        // final-r01 — THE PREDICATE IS NOW WIRED, AND THIS PINS THE PROPERTY THAT MAKES WIRING IT WORTH ANYTHING.
        //
        // CountsAsReviewed shipped with ZERO production callers: the window loop switched on the enum directly and
        // added the primaries from its catch-all `default:` arm. So this helper — and the test above it — stated the
        // DESTRUCTIVE-path contract ("only a window that produced findings may license a delete") while nothing
        // enforced it. A helper that reads as a guarantee and is not one is worse than no helper. Worse still, that
        // `default:` arm was FAIL-OPEN: a WindowOutcome member added tomorrow would land in it, have its chapters
        // marked REVIEWED, and license the delete-vanished-open pass — the exact P0 be-c01 exists to close — while
        // this predicate went on returning false about it.
        //
        // The loop now asks THIS method for the licence and its `default:` arm is fail-closed. The property that
        // makes that safe is the one asserted here, over EVERY member the enum has or ever gains: anything that is
        // not explicitly `Reviewed` is NOT a review. A new member must be added to CountsAsReviewed DELIBERATELY.
        foreach (var outcome in Enum.GetValues<WindowOutcome>())
        {
            if (outcome == WindowOutcome.Reviewed)
                Assert.True(outcome.CountsAsReviewed(), $"{outcome} is the one outcome that licenses the delete.");
            else
                Assert.False(
                    outcome.CountsAsReviewed(),
                    $"WindowOutcome.{outcome} is not proof the model reviewed the window's chapters, so it must NOT " +
                    "license the destructive delete-vanished-open pass. If a new outcome genuinely IS a review, say " +
                    "so in CountsAsReviewed on purpose - do not let it default into being one.");
        }

        // And the classifier can only ever produce the three the loop handles by name.
        Assert.Equal(3, Enum.GetValues<WindowOutcome>().Length);
    }

    // ─── W7. be-c02 NO REGRESSION + be-c01 NO OVER-CORRECTION: a GENUINELY successful multi-window rebuild (every
    //         window PRODUCED findings) still deletes vanished-open findings for the reviewed chapters. The be-c01
    //         guard must not turn every rebuild into a preserve-everything build — that would recreate the
    //         unbounded accumulation b2 was written to kill. ───────────────────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_FullySuccessfulRebuild_StillDeletesVanishedOpen()
    {
        // Two windows, both PRODUCE findings on both builds. Build 1: ch 0 finding + ch 1 finding, both left open.
        // Build 2: each window emits a DIFFERENT finding (both old ones vanish). Both chapters really were
        // re-reviewed, so BOTH vanished-open findings are deleted — the scoping never preserves a reviewed
        // chapter's stale open row. (be-c01: "reviewed" now means the window produced findings, which it did.)
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
            [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "New ch1", 1)),
        };

        var second = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(second.Ready);
        Assert.Equal(0, second.FailedWindows);
        // A genuinely complete build: every reviewable chapter was reviewed (nothing was silently skipped).
        Assert.Equal(second.ChaptersTotal, second.ChaptersReviewed);
        Assert.DoesNotContain("returned no findings", second.Message);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Single(rows, f => f.Rationale == "New ch0");
        Assert.Single(rows, f => f.Rationale == "New ch1");
        Assert.DoesNotContain(rows, f => f.Rationale == "Old ch0"); // reviewed ch 0 → vanished-open deleted
        Assert.DoesNotContain(rows, f => f.Rationale == "Old ch1"); // reviewed ch 1 → vanished-open deleted
        Assert.Equal(2, rows.Count);
    }

    // ─── W7a (be-c03 P1-2, THE HEADLINE FIX). A book with ONE GENUINELY EMPTY chapter (order 1: no brief, no
    //         summary, blank ContentText — a title-only "Part I" divider or a DOCX artefact). BookContextAssembler
    //         SKIPS it, so it is never a window primary and can NEVER be in reviewedPrimaryOrders — on this build or
    //         any future one. Pre-be-c03 the book-wide rule asked `reviewed ⊇ real`, which was therefore PERMANENTLY
    //         false, so a vanished-open BOOK-WIDE finding was preserved on EVERY rebuild = immortal. On a FULLY
    //         successful build it must now be DELETED. ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_EmptyChapter_FullySuccessfulBuild_DeletesTheVanishedOpenBookWideFinding()
    {
        // Chapters 0 and 2 have content (one window each); chapter 1 is GENUINELY EMPTY and is never windowed.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Fresh ch2 finding", 2)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedBookWithEmptyChapterAsync(db, chapterCount: 3, emptyChapterOrder: 1);

        // A prior BOOK-WIDE (no-anchor) finding, still open, which this build does not re-emit. Pre-be-c03 it was
        // IMMORTAL on this book. Plus a phantom-anchored orphan, to prove b2's fix still holds here.
        await SeedOpenFindingAsync(db, bookId, "book-wide-key", "Book-wide finding, no anchors");
        await SeedOpenFindingAsync(db, bookId, "phantom-16", "Anchored to phantom chapter 16", 16);
        // ...and one anchored to the EMPTY chapter itself: a STATED RESIDUAL, preserved (see below).
        await SeedOpenFindingAsync(db, bookId, "empty-ch1", "Anchored to the empty chapter (order 1)", 1);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.Equal(0, result.FailedWindows);
        // The empty chapter is REAL but NOT REVIEWABLE: the denominator is 2, not 3, and this build reviewed both.
        // This is the most complete build this book can EVER have.
        Assert.Equal(2, result.ChaptersTotal);
        Assert.Equal(2, result.ChaptersReviewed);
        Assert.Equal(3, await db.Chapters.CountAsync(c => c.BookId == bookId)); // ...while the book really has 3.

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // THE P1-2 FIX: the book-wide finding is RETRACTED. Pre-fix it survived here, and on every rebuild forever.
        Assert.DoesNotContain(rows, f => f.Rationale == "Book-wide finding, no anchors");
        // b2 NO REGRESSION: a phantom anchor still carries no preservation weight.
        Assert.DoesNotContain(rows, f => f.Rationale == "Anchored to phantom chapter 16");
        // STATED RESIDUAL (fail-safe): a row anchored to the real-but-unreviewable chapter is PRESERVED. The
        // anchored branch still asks for REVIEWED, which an empty chapter never is. Only a pre-b7 row can carry such
        // an anchor (the visibility gate makes it unproducible now); repairing those is b6's charter, not this pass's.
        Assert.Single(rows, f => f.Rationale == "Anchored to the empty chapter (order 1)");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Single(rows, f => f.Rationale == "Fresh ch2 finding");
        Assert.Equal(3, rows.Count);
    }

    // ─── W7b (be-c03 + be-c02, NO REGRESSION). The SAME book with the SAME empty chapter, but window 2's call
    //         FAILS. The build did NOT review everything it COULD, so the book-wide finding is PRESERVED — and so is
    //         the finding anchored in the failed window's chapter (the b2/be-c02 rule, which must not regress). The
    //         reviewable set makes an UNREVIEWABLE chapter stop vetoing forever; it must not make an UNREVIEWED one
    //         stop vetoing at all. ────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_EmptyChapter_FailedWindow_StillPreservesBookWideAndUnreviewedAnchors()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = "{ truncated, no closing brace", // unparseable → window 2 (chapter 2) FAILS
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedBookWithEmptyChapterAsync(db, chapterCount: 3, emptyChapterOrder: 1);

        await SeedOpenFindingAsync(db, bookId, "book-wide-key", "Book-wide finding, no anchors");
        await SeedOpenFindingAsync(db, bookId, "ch2-anchored", "Anchored to real chapter 2 (window failed)", 2);
        await SeedOpenFindingAsync(db, bookId, "ch0-key", "Anchored in the window that produced findings (ch 0)", 0);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);          // window 1 produced findings → not a total failure → the persist RUNS
        Assert.Equal(1, result.FailedWindows);
        Assert.Equal(1, result.ChaptersReviewed);
        Assert.Equal(2, result.ChaptersTotal);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // PRESERVED: the build reviewed 1 of the 2 chapters it COULD review → its book-wide scope was not covered.
        Assert.Single(rows, f => f.Rationale == "Book-wide finding, no anchors");
        // PRESERVED (be-c02): chapter 2's window failed, so its absence from `incoming` proves nothing.
        Assert.Single(rows, f => f.Rationale == "Anchored to real chapter 2 (window failed)");
        // DELETED: chapter 0's window DID produce findings and no longer surfaces this one → regenerated noise.
        Assert.DoesNotContain(rows, f => f.Rationale == "Anchored in the window that produced findings (ch 0)");
    }

    // ─── W7c (be-c03 x be-c01 COMPOSITION, belt and braces). The two fixes push in OPPOSITE directions on the SAME
    //         superset test: be-c01 makes FEWER windows count as reviewed (harder to satisfy), be-c03 shrinks the set
    //         that must be covered (easier). They must compose so that a book WITH an empty chapter still PRESERVES a
    //         book-wide finding when a window silently TRUNCATES to `{}` — the empty chapter must not make a
    //         truncated build look complete. Same book as W7a, whose successful build DID delete it. ──────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_EmptyChapter_TruncatedWindow_StillPreservesTheBookWideFinding()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = TruncatedEmptyObject, // PARSES, carries nothing → be-c01: chapter 2 is NOT reviewed
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedBookWithEmptyChapterAsync(db, chapterCount: 3, emptyChapterOrder: 1);

        await SeedOpenFindingAsync(db, bookId, "book-wide-key", "Book-wide finding, no anchors");
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.Equal(0, result.FailedWindows);  // an EMPTY window is not an OBSERVED failure (be-c01)...
        Assert.Equal(1, result.ChaptersReviewed); // ...it surfaces as a COVERAGE gap: 1 of the 2 reviewable chapters.
        Assert.Equal(2, result.ChaptersTotal);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // THE COMPOSITION: reviewable = {0, 2} (the empty chapter is out — be-c03), reviewed = {0} (the truncated
        // window is out — be-c01). reviewed ⊉ reviewable → the book-wide finding SURVIVES. Neither fix cancels the
        // other: be-c03 alone would have deleted it here if it had also swallowed be-c01's truncation guard.
        Assert.Single(rows, f => f.Rationale == "Book-wide finding, no anchors");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Equal(2, rows.Count);
    }

    // ─── W7d (be-c03, the LEGACY per-dimension path). That path reviews the whole book in ONE concatenated context,
    //         so it seeds `reviewed` AND `reviewable` from the same source (every chapter order) — reviewed ⊇
    //         reviewable holds by construction and a book-wide finding is retractable even on a book with an empty
    //         chapter. This test is GREEN before be-c03 too (the P1-2 bug is windowed-only, because only that path
    //         derives `reviewed` from window primaries); its job is to keep the two sets WELDED on this path, so
    //         un-welding them (e.g. seeding `reviewed` from the rendered chapters alone) turns it RED. ─────────────
    [Fact]
    public async Task BuildBookReviewAsync_PerDimension_EmptyChapter_StillDeletesTheVanishedOpenBookWideFinding()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedBookWithEmptyChapterAsync(db, chapterCount: 3, emptyChapterOrder: 1);

        await SeedOpenFindingAsync(db, bookId, "legacy-book-wide-key", "Book-wide pacing drift across the manuscript");
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // The whole book was re-reviewed in one context → the vanished-open book-wide finding is regenerated noise.
        Assert.DoesNotContain(rows, f => f.Rationale == "Book-wide pacing drift across the manuscript");
        Assert.NotEmpty(rows); // the six per-dimension findings persisted
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
        // Window 1 emits a fresh ch-0 finding and window 2 emits a fresh ch-1 finding, so BOTH chapters really were
        // re-reviewed (be-c01: a window is reviewed only if it PRODUCED findings). Every anchored chapter of the
        // seeded [0,1] finding was re-reviewed and the model no longer surfaces it, so it IS deleted (regenerated
        // noise) — the opposite outcome to W8's failed window.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("pacing", "improve", 2, "Fresh ch1 finding", 1)),
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
        Assert.Equal(0, result.FailedWindows);
        Assert.Equal(result.ChaptersTotal, result.ChaptersReviewed); // every chapter genuinely re-reviewed

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // Both anchored chapters were re-reviewed → the vanished-open finding is deleted.
        Assert.DoesNotContain(rows, f => f.Rationale == "Continuity break spanning ch 0 and ch 1");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Single(rows, f => f.Rationale == "Fresh ch1 finding");
        Assert.Equal(2, rows.Count);
    }

    // ═══ b2. IMMORTAL ORPHANS: an anchor order the book does not have must NOT block the scoped delete ═══
    //
    // The be-c02 scope preserved a vanished-open finding when ANY anchored chapter was not reviewed this build.
    // reviewedChapterOrders is built from the book's REAL chapters, so a PHANTOM order (the model invented it —
    // live book 2cf6fcf2: a 1-chapter book at Order=0 whose findings claimed orders 1 and 16) could never be in
    // it, and the row was preserved on EVERY rebuild, forever — the across-builds accumulation the user saw.
    // b1 stops NEW phantom anchors being written; these tests pin that EXISTING (pre-b1) phantom rows are now
    // deletable, WITHOUT weakening the be-c02 preservation rule for REAL un-reviewed chapters.

    // ─── B1. ORPHAN: a vanished-open finding anchored ONLY to a non-existent chapter order IS deleted, even
    //         though that order is (necessarily) not in the reviewed set. ────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_OrphanAnchoredToNonExistentChapter_IsDeleted()
    {
        // Two chapters (orders 0, 1) → two windows, BOTH succeed. The seeded orphan anchors order 16, which is no
        // chapter of this book. Pre-b2 the scope demanded 16 ∈ reviewedChapterOrders ({0,1}) — impossible — so it
        // survived every rebuild. Now an invalid anchor carries no preservation weight and the row is deleted.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = EmptyFindings, // filler: the orphan's ONLY anchor (16) is no chapter of this book, so it is
                                     // deletable on ANY build — whether ch 1 was reviewed is irrelevant here
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        await SeedOpenFindingAsync(db, bookId, "orphan-16", "Immortal orphan anchored to phantom chapter 16", 16);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(0, result.FailedWindows);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // The orphan is GONE: its only anchor names no chapter of this book, so nothing real protects it.
        Assert.DoesNotContain(rows, f => f.Rationale == "Immortal orphan anchored to phantom chapter 16");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Single(rows);
    }

    // ─── B2. THE DISCRIMINATOR: in ONE build, a phantom-anchored orphan is DELETED while a finding anchored to a
    //         REAL-but-unreviewed chapter is PRESERVED. Both are vanished-open and both anchor an order that is
    //         NOT in the reviewed set — the ONLY difference is whether that order is a real chapter. ──────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_PhantomAnchorDeleted_RealUnreviewedAnchorPreserved()
    {
        // Two chapters. Window 1 (ch 0) SUCCEEDS; window 2 (ch 1) FAILS → ch 1 is NOT re-reviewed this build.
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

        await SeedOpenFindingAsync(db, bookId, "real-ch1", "Anchored to real chapter 1 (not reviewed)", 1);
        await SeedOpenFindingAsync(db, bookId, "phantom-16", "Anchored to phantom chapter 16", 16);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(1, result.FailedWindows); // ch 1 not in the reviewed set

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // REAL chapter 1 was not re-reviewed → its finding's absence is a failure artifact → PRESERVED (be-c02).
        Assert.Single(rows, f => f.Rationale == "Anchored to real chapter 1 (not reviewed)");
        // Chapter 16 does not exist → the anchor is INVALID → it cannot pin the row alive → DELETED (b2).
        Assert.DoesNotContain(rows, f => f.Rationale == "Anchored to phantom chapter 16");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Equal(2, rows.Count);
    }

    // ─── B3. be-c02 REGRESSION (must never come back): a genuine MULTI-chapter continuity finding whose LATER
    //         anchor's window FAILED is STILL preserved once the invalid-anchor filter is in the path. ───────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_MultiChapterFinding_LaterAnchorWindowFails_StillPreservedUnderInvalidAnchorFilter()
    {
        // Six chapters → six windows. The continuity finding anchors ch 0 (reviewed) AND ch 5 (its window FAILS).
        // Every anchor is a REAL chapter, so the b2 filter removes NOTHING and the be-c02 rule applies in full:
        // ch 5 was not re-reviewed, so the vanished finding is PRESERVED. This is the exact regression the
        // PrimaryChapterOrderOf → ChapterOrdersOf fix closed (memory pagedraft-bookreview-multianchor-delete);
        // b2 must not re-open it by loosening the scope.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = EmptyFindings, [3] = EmptyFindings, [4] = EmptyFindings, [5] = EmptyFindings,
                [6] = "{ truncated, no closing brace", // window 6 FAILS → ch 5 NOT re-reviewed
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 6);

        await SeedOpenFindingAsync(db, bookId, "continuity-0-5", "Continuity break spanning ch 0 and ch 5", 0, 5);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(1, result.FailedWindows);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Single(rows, f => f.Rationale == "Continuity break spanning ch 0 and ch 5"); // PRESERVED
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Equal(2, rows.Count);
    }

    // ─── B4. MIXED anchors (one REAL + one PHANTOM). SEMANTICS: ignoring an invalid anchor must never REDUCE the
    //         protection a VALID anchor earns. So a mix of {real-UNREVIEWED, phantom} is PRESERVED (the real
    //         chapter genuinely was not re-reviewed), while a mix of {real-REVIEWED, phantom} is DELETED (every
    //         real chapter it names WAS re-reviewed; the phantom adds no information either way). ─────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_MixedRealAndPhantomAnchors_PhantomNeitherPreservesNorDeletes()
    {
        // Two chapters. Window 1 (ch 0) SUCCEEDS; window 2 (ch 1) FAILS → ch 1 NOT re-reviewed.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = "{ truncated, no closing brace", // window 2 FAILS → ch 1 NOT re-reviewed
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // {ch 1 (real, NOT reviewed) + ch 16 (phantom)} → the real anchor still earns preservation.
        await SeedOpenFindingAsync(db, bookId, "mixed-1-16", "Mixed anchors: real ch 1 (unreviewed) + phantom 16", 1, 16);
        // {ch 0 (real, REVIEWED) + ch 16 (phantom)} → the phantom must not keep it alive: every real chapter it
        // names was re-reviewed and the model no longer surfaces it, so it is regenerated noise.
        await SeedOpenFindingAsync(db, bookId, "mixed-0-16", "Mixed anchors: real ch 0 (reviewed) + phantom 16", 0, 16);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(1, result.FailedWindows);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Single(rows, f => f.Rationale == "Mixed anchors: real ch 1 (unreviewed) + phantom 16");      // PRESERVED
        Assert.DoesNotContain(rows, f => f.Rationale == "Mixed anchors: real ch 0 (reviewed) + phantom 16"); // DELETED
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Equal(2, rows.Count);
    }

    // ─── B5. IDEMPOTENCE: re-persisting the SAME incoming set does not grow the row count (and does not re-insert
    //         rows — the same Ids are refreshed in place). This is the accumulation regression in miniature. ──
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_RebuildingTheSameFindings_DoesNotGrowTheRowCount()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Stable ch0 finding", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("character", "improve", 2, "Stable ch1 finding", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // A pre-b1 phantom row is present too: the rebuild must CLEAR it, not stack on top of it. Touch the
        // baseline so the seeded row does not make the review read FRESH (which would make build 1 a no-op).
        await SeedOpenFindingAsync(db, bookId, "phantom-16", "Legacy phantom row", 16);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var afterFirst = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, afterFirst.Count); // the two fresh findings; the legacy phantom is gone
        Assert.DoesNotContain(afterFirst, f => f.Rationale == "Legacy phantom row");

        // Rebuild with the IDENTICAL model output (force a real rebuild, not a freshness no-op).
        await TouchSummaryBaselineAsync(db, bookId);
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var afterSecond = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, afterSecond.Count); // NOT 4 — the same keys matched and were refreshed in place
        Assert.Equal(
            afterFirst.Select(f => f.Id).OrderBy(id => id),
            afterSecond.Select(f => f.Id).OrderBy(id => id));
    }

    // ─── B6. The scoped-delete predicate itself, exhaustively (it is the whole of the b2 fix). Includes the
    //         degenerate no-chapters case, which the end-to-end build cannot reach (a chapter-less book produces
    //         no findings to persist) but which must never wipe content. ────────────────────────────────────
    [Fact]
    public void IsVanishedOpenDeletable_DistinguishesInvalidAnchorsFromRealUnreviewedOnes()
    {
        var real = new HashSet<int> { 0, 1, 2, 3, 4, 5 };
        // be-c03: this book has NO genuinely empty chapters, so every real chapter is also REVIEWABLE (the windower
        // renders all six). The reviewable set only diverges from `real` on a book with an empty chapter — that is
        // the subject of IsVanishedOpenDeletable_NoAnchorFinding_MeasuresCoverageAgainstTheReviewableSet below.
        // The ANCHORED cases in this test do not read it at all: the anchored branch asks REVIEWED vs REAL.
        var reviewable = new HashSet<int>(real);
        var reviewed = new HashSet<int> { 0, 1, 2 }; // windows for 3, 4, 5 failed this build
        var nothing = new HashSet<int>();

        // Every anchored chapter is real AND reviewed → deletable (regenerated noise).
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 0, 2 }, reviewed, reviewable, real));
        // A REAL chapter that was not reviewed → PRESERVE (be-c02), whether it is the only or a later anchor.
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 4 }, reviewed, reviewable, real));
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 0, 5 }, reviewed, reviewable, real));
        // ALL anchors invalid (the orphan) → nothing real protects it → deletable. It must NOT be laundered into
        // the no-anchor ({0}) convention, which would preserve it whenever chapter 0 happened to be unreviewed.
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 16 }, reviewed, reviewable, real));
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 16, 42 }, reviewed, reviewable, real));
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { -1 }, reviewed, reviewable, real));
        // An orphan stays deletable even when chapter 0 is UNREVIEWED — proof it is not being treated as {0}.
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 16 }, nothing, reviewable, real));
        // MIXED: the phantom neither preserves (0 is reviewed → deletable) nor deletes (4 is real+unreviewed →
        // preserved). An invalid anchor never changes the answer a valid one gives.
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 0, 16 }, reviewed, reviewable, real));
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 4, 16 }, reviewed, reviewable, real));
        // An anchor on chapter 0 is now ONLY ever a real first-chapter anchor (b3 removed the {0} no-anchor
        // sentinel), so it follows the ordinary be-c02 rule: deletable exactly when chapter 0 was reviewed.
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 0 }, reviewed, reviewable, real));
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 0 }, new HashSet<int> { 1, 2 }, reviewable, real));
        // DEGENERATE: a book with no real chapters has no scope to reason in → never delete (pre-b2 behavior).
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 0 }, nothing, nothing, nothing));
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 16 }, nothing, nothing, nothing));
    }

    // ─── B7 (b3). The NO-ANCHOR case of the same predicate, RE-PINNED to its new, deliberate semantics. b2 mapped
    //         an anchor-less row to {0} and noted that a `-1`-style sentinel would silently fall through the
    //         invalid-order filter and become unconditionally deletable "by side effect". b3 does NOT emit any
    //         numeric sentinel: no-anchor is the EMPTY order set, and it gets its OWN short-circuit above the
    //         per-order loop. STATED RULE: a no-anchor finding is BOOK-WIDE, so it is deletable ONLY on a build
    //         that re-reviewed EVERY real chapter. Its deletability no longer depends on chapter 0. ────────────
    [Fact]
    public void IsVanishedOpenDeletable_NoAnchorFinding_IsBookWide_DeletableOnlyOnACompleteBuild()
    {
        var real = new HashSet<int> { 0, 1, 2, 3, 4, 5 };
        // be-c03: a book with NO empty chapters — every real chapter is reviewable, so `reviewed ⊇ reviewable` and
        // the pre-be-c03 `reviewed ⊇ real` agree on every row below and this test's semantics are unchanged.
        var reviewable = new HashSet<int>(real);
        var noAnchor = Array.Empty<int>(); // ChapterOrdersOf's representation of "this finding anchors no chapter"

        // COMPLETE build (every reviewable chapter re-reviewed) → the model genuinely no longer surfaces it → DELETE.
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(
            noAnchor, new HashSet<int> { 0, 1, 2, 3, 4, 5 }, reviewable, real));

        // PARTIAL build → its absence may be a failed-window artifact → PRESERVE, exactly like an anchored finding
        // whose chapter was not re-reviewed.
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(noAnchor, new HashSet<int> { 0, 1, 2 }, reviewable, real));
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(noAnchor, new HashSet<int>(), reviewable, real));

        // THE COLLISION, PINNED. Chapter 0 reviewed, chapters 1-5 not: pre-b3 the no-anchor row WAS {0}, so this
        // deleted it. Now the two cases give OPPOSITE answers — a no-anchor finding is preserved (the book was not
        // fully re-reviewed) while a genuine chapter-0 finding is deleted (its one chapter WAS re-reviewed).
        var onlyChapterZeroReviewed = new HashSet<int> { 0 };
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(noAnchor, onlyChapterZeroReviewed, reviewable, real));
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 0 }, onlyChapterZeroReviewed, reviewable, real));

        // ...and symmetrically: chapter 0 NOT reviewed but every other chapter is. The no-anchor row is preserved
        // (incomplete), the chapter-0 row is preserved (its chapter was skipped) — no-anchor tracks the BOOK, not
        // chapter 0, so neither answer is derived from the other.
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(
            noAnchor, new HashSet<int> { 1, 2, 3, 4, 5 }, reviewable, real));

        // UNKNOWN scope (unparseable ChapterAnchorsJson → ChapterOrdersOf returns null): never delete, even on a
        // complete build. A parse blip must not wipe review content.
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(
            null, new HashSet<int> { 0, 1, 2, 3, 4, 5 }, reviewable, real));

        // DEGENERATE (no chapters): the no-chapters guard still wins over the no-anchor rule. Note that without
        // that guard the superset test would be vacuously TRUE here (every element of an empty set is reviewed),
        // which is precisely the "deletable by side effect" trap b2 warned about.
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(
            noAnchor, new HashSet<int>(), new HashSet<int>(), new HashSet<int>()));
    }

    // ─── B7a (be-c03, P1-2). THE REVIEWABLE-vs-REAL SET. A genuinely EMPTY chapter (a title-only "Part I" divider,
    //         a DOCX artefact) is skipped by BookContextAssembler, so it is never any window's primary and can NEVER
    //         enter the REVIEWED set — on any build, forever. Measuring the book-wide (no-anchor) rule against the
    //         RAW chapter set therefore made `reviewed ⊇ real` PERMANENTLY unsatisfiable on such a book, and every
    //         vanished-open book-wide finding became IMMORTAL (the b2 orphan class, resurrected). The rule now
    //         measures against what the build COULD review. Note this test's `real` and `reviewable` DIFFER, so an
    //         accidental transposition of the two arguments turns it red. ────────────────────────────────────────
    [Fact]
    public void IsVanishedOpenDeletable_NoAnchorFinding_MeasuresCoverageAgainstTheReviewableSet()
    {
        // Chapter 2 is a genuinely EMPTY chapter: REAL (a Chapters row) but NOT REVIEWABLE (no block, never windowed).
        var real = new HashSet<int> { 0, 1, 2, 3 };
        var reviewable = new HashSet<int> { 0, 1, 3 };
        var noAnchor = Array.Empty<int>();

        // THE FIX. A FULLY successful build reviews every REVIEWABLE chapter — which is the most any build can ever
        // do on this book — so the vanished-open book-wide finding IS retracted. Pre-be-c03 this was FALSE (reviewed
        // could never contain 2), on this build and on every future one: unbounded accumulation.
        var fullySuccessful = new HashSet<int> { 0, 1, 3 };
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(noAnchor, fullySuccessful, reviewable, real));
        // ...and the old test would have said preserve. Pinned so the regression is named, not implied.
        Assert.False(fullySuccessful.IsSupersetOf(real), "premise: the reviewed set can never cover the empty chapter");

        // PARTIAL builds still PRESERVE — the be-c02/be-c01 protection is fully intact on this book. Chapter 3's
        // window failed (or came back empty, which be-c01 treats the same way), so the build did NOT review
        // everything it could.
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(noAnchor, new HashSet<int> { 0, 1 }, reviewable, real));
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(noAnchor, new HashSet<int>(), reviewable, real));

        // The ANCHORED branch is untouched and still reads REVIEWED vs REAL (be-c02): a real, non-empty chapter that
        // was not reviewed still PRESERVES its finding...
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 3 }, new HashSet<int> { 0, 1 }, reviewable, real));
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 0, 3 }, new HashSet<int> { 0, 1 }, reviewable, real));
        // ...while a phantom anchor (no such chapter) is still deletable (b2's fix, unchanged).
        Assert.True(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 16 }, fullySuccessful, reviewable, real));

        // STATED RESIDUAL: a finding anchored ONLY to the real-but-UNREVIEWABLE chapter 2 is PRESERVED, not deleted.
        // The anchored branch asks for REVIEWED, which an empty chapter never is. Fail-safe by design (this
        // subsystem loses a duplicate before it loses a finding), and structurally unproducible after b7's
        // visibility gate — only a pre-b7 legacy row can carry such an anchor, and repairing those is b6's charter.
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(new[] { 2 }, fullySuccessful, reviewable, real));

        // DEGENERATE: real chapters exist but NOTHING is reviewable (every chapter empty → no window was ever built).
        // The superset test would be VACUOUSLY true, so the rule fails CLOSED instead of deleting the book's review.
        Assert.False(BookFindingReconciler.IsVanishedOpenDeletable(noAnchor, new HashSet<int>(), new HashSet<int>(), real));
    }

    // ─── B8 (b3). THE SENTINEL COLLISION, at the key. A NO-ANCHOR finding and a CHAPTER-0 finding with the SAME
    //         dimension + rationale used to hash to the SAME DedupKey (order 0 doubled as "no anchor", but 0 is a
    //         real chapter in every 0-based book), so the union's first-occurrence-wins rule SILENTLY DISCARDED one
    //         of them - and had both reached the DB the UNIQUE (BookId, Language, DedupKey) index would have
    //         rejected the second. They are different findings and must key differently. ────────────────────────
    [Fact]
    public void ComputeDedupKey_NoAnchorAndChapterZero_ProduceDifferentKeys()
    {
        const string dim = "theme";
        const string rationale = "הטקסט מצליח להמחיש את המרחק שבין הדמויות.";

        var noAnchorKey = BookFinding.ComputeDedupKey(dim, null, rationale);
        var chapterZeroKey = BookFinding.ComputeDedupKey(dim, 0, rationale);

        Assert.NotEqual(chapterZeroKey, noAnchorKey);

        // The no-anchor token collides with NO chapter order, not just with 0 (a book could start at any order).
        foreach (var order in new[] { 0, 1, 2, 16, 79 })
            Assert.NotEqual(BookFinding.ComputeDedupKey(dim, order, rationale), noAnchorKey);

        // Determinism + the ordinary axes still hold: same inputs → same key; a different order or dimension → a
        // different key.
        Assert.Equal(noAnchorKey, BookFinding.ComputeDedupKey(dim, null, rationale));
        Assert.NotEqual(BookFinding.ComputeDedupKey(dim, 1, rationale), chapterZeroKey);
        Assert.NotEqual(BookFinding.ComputeDedupKey("plot", 0, rationale), chapterZeroKey);

        // A finding anchored to a REAL chapter keeps the V1 key byte-for-byte (V1 only differed on the no-anchor
        // sentinel and on unresolved orders), so the vast majority of cached rows need no migration at all — the
        // legacy fallback below exists for the rows whose key genuinely MOVED.
        Assert.Equal(BookFinding.ComputeLegacyDedupKeyV1(dim, 0, rationale), chapterZeroKey);
        Assert.NotEqual(BookFinding.ComputeLegacyDedupKeyV1(dim, 0, rationale), noAnchorKey);
    }

    // ─── B9 (b3, RE-PINNED BY b4b). The same collision END-TO-END: one build emits a book-wide (no-anchor) finding
    //         AND a chapter-0 finding with identical dimension + rationale.
    //
    //         b3 asserted BOTH rows persist (pre-b3 they COLLIDED on one dedup key and one was silently dropped).
    //         b4b changes the OUTCOME on purpose: chapter 0 is a REAL, anchored chapter (0-based, not a sentinel),
    //         so a book-wide copy of the same finding FOLDS into it, and ONE row persists. What b3 actually fixed
    //         is untouched and is a DIFFERENT property, asserted below: the two still derive DIFFERENT keys. That
    //         distinction is load-bearing — a hash collision drops an ARBITRARY row (it could just as easily lose
    //         the navigable chapter-0 copy, which is what pre-b3 risked), whereas the fold is a stated rule that
    //         ALWAYS keeps the anchored copy. Same row count, opposite guarantees. ─────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_NoAnchorAndChapterZeroFinding_FoldIntoTheAnchoredRow()
    {
        const string rationale = "Pacing sags whenever the cast splits up";
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                // Same dimension, same rationale; one anchors chapter 0, the other anchors NOTHING (book-wide).
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("pacing", rationale, new[] { (0, "Chapter 0") }, new[] { 0 }),
                    new AnchorSpec("pacing", rationale, Array.Empty<(int, string)>(), Array.Empty<int>())),
                [2] = EmptyFindings,
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // b3's de-collision, still intact at the KEY: the two findings do NOT hash to the same value.
        Assert.NotEqual(
            BookFinding.ComputeDedupKey("pacing", null, rationale),
            BookFinding.ComputeDedupKey("pacing", 0, rationale));

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var row = Assert.Single(rows); // b4b: the book-wide copy folded into the anchored one.

        // ...and the survivor is the CHAPTER-0 copy, with its anchor intact. This is the guarantee a key collision
        // could never make: the user keeps the navigable finding, never the anchor-less one.
        Assert.Equal(0, Assert.Single(DeserializeAnchors(row.ChapterAnchorsJson)).Order);
        Assert.Equal(BookFinding.ComputeDedupKey("pacing", 0, rationale), row.DedupKey);
    }

    // ═══ b4b INCOMING-vs-PERSISTED. The collapse above only ever sees ONE build's incoming set. A row the user
    //     ACTED on is deliberately never deleted, so it is still there next build; when the model re-emits that
    //     same finding RE-WORDED, its exact key no longer matches, it is inserted as a NEW open row, and the user
    //     reads the same criticism twice. (Live: book 2cf6fcf2 renders an acknowledged נושא card and a fresh נושא
    //     card side by side.) A hit on the re-wording tier means exactly what an exact-key hit means — "this fresh
    //     finding IS that row" — so it is handled the SAME way: the ROW survives, its Status is untouched, its
    //     content is refreshed, and the fresh copy is not inserted. Every Hebrew rationale below is REAL (book
    //     A63A6E02, 2026-07-12); the pair scores 0.889. ═══

    // The character/מררה pair: the model emitted this finding once anchored and once book-wide, re-worded.
    private const string MararaShort = "דמותה של מררה מוסיפה רובד רגשי חשוב דרך דאגה שאינה מתבטאת.";
    private const string MararaLong = "הדמות של מררה מוסיפה רובד רגשי חשוב; הדאגה שלה שאינה מתבטאת יוצרת עומק ומתח פנימי.";

    // ─── P1. Incoming near-duplicates a persisted OPEN row → ONE row, refreshed IN PLACE (same Id), still open.
    //         The build is PARTIAL so the b3 no-anchor rule PRESERVES the cached row: without the fold it would sit
    //         beside the fresh copy as a visible duplicate pair. Claiming the row is also what keeps the finding
    //         ALIVE — simply dropping the fresh copy instead would leave the row looking VANISHED to the delete
    //         pass, and on a complete build it would be deleted as regenerated noise, losing the finding entirely.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_IncomingRewordsAPersistedOpenRow_RefreshesItInPlace_NoDuplicate()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, new[] { (0, "Chapter 0") }, new[] { 0 })),
                // window 2 (ch 1) is ABSENT → it FAILS → the build is PARTIAL → a vanished no-anchor row is PRESERVED
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // The cached row: the BOOK-WIDE copy of the same finding, still open, keyed on its own exact text.
        var cachedKey = BookFinding.ComputeDedupKey("character", null, MararaShort);
        var freshKey = BookFinding.ComputeDedupKey("character", 0, MararaLong);
        Assert.NotEqual(cachedKey, freshKey); // premise: the exact-key tiers are helpless against a rewording
        var seededId = await SeedFindingAsync(db, bookId, "character", MararaShort, "open", cachedKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(1, result.FailedWindows); // PARTIAL: the cached row is preserved, not deleted

        var row = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal(seededId, row.Id);       // the SAME row — folded onto, not orphaned + duplicated
        Assert.Equal("open", row.Status);
        Assert.Equal(MararaLong, row.Rationale); // content refreshed from the fresh finding
        Assert.Equal(freshKey, row.DedupKey);
        Assert.Equal(0, Assert.Single(DeserializeAnchors(row.ChapterAnchorsJson)).Order); // and it GAINED the anchor
    }

    // ─── P2. THE USER'S LITERAL COMPLAINT. Incoming near-duplicates a persisted ACKNOWLEDGED row on a COMPLETE
    //         build. An acknowledged row is NEVER deleted (by design), so pre-b4b the reworded fresh copy was
    //         inserted next to it and the user read the same finding twice. SEMANTICS: the row survives with
    //         Status STILL "acknowledged" — a re-worded restatement of a criticism the user already triaged is not
    //         a new criticism, and re-raising it as a fresh open card would silently undo their decision.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_IncomingRewordsAPersistedAcknowledgedRow_KeepsOneRow_StatusPreserved()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, new[] { (0, "Chapter 0") }, new[] { 0 })),
                [2] = EmptyFindings, // filler: the seeded row is CLAIMED by the fuzzy tier, so it is never
                                     // "vanished" and the delete scope (be-c01) does not enter into it
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var cachedKey = BookFinding.ComputeDedupKey("character", 0, MararaShort);
        var seededId = await SeedFindingAsync(db, bookId, "character", MararaShort, "acknowledged", cachedKey, 0);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var row = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal(seededId, row.Id);
        Assert.Equal("acknowledged", row.Status); // the user's decision is NOT dropped and NOT re-opened
        Assert.Equal(MararaLong, row.Rationale);  // ...and the card reads the freshest phrasing
    }

    // ─── P3. Incoming near-duplicates a persisted DISMISSED row. This is the case that matters most: without the
    //         re-wording tier, a dismissed finding is RESURRECTED as an open card every time the model rephrases
    //         it, so the user can never make it go away. The exact-key path has always suppressed a VERBATIM
    //         re-emission of a dismissed finding; this makes that promise survive re-wording.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_IncomingRewordsAPersistedDismissedRow_IsNotResurrectedAsOpen()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, new[] { (0, "Chapter 0") }, new[] { 0 })),
                [2] = EmptyFindings,
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var cachedKey = BookFinding.ComputeDedupKey("character", 0, MararaShort);
        var seededId = await SeedFindingAsync(db, bookId, "character", MararaShort, "dismissed", cachedKey, 0);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(seededId, row.Id);
        Assert.Equal("dismissed", row.Status);
        Assert.DoesNotContain(rows, f => f.Status == "open"); // NOT re-raised behind the user's back
    }

    // ─── P4. THE PERSIST-TIER PRECISION FENCE. The cached ACKNOWLEDGED row is anchored to chapter 0; the fresh
    //         finding carries the IDENTICAL rationale but anchors chapter 1 — a DIFFERENT REAL chapter. Similarity
    //         is 1.000, so ONLY the anchor rule can stop the merge, and it must: the same sentence about two
    //         different chapters may be two genuine findings, and folding them would silently delete one AND
    //         mis-anchor the survivor. TWO rows, and the user's acknowledgement stays on the chapter-0 one.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_IncomingOnADifferentRealChapter_NeverClaimsThePersistedRow()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                [2] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaShort, new[] { (1, "Chapter 1") }, new[] { 1 })),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var cachedKey = BookFinding.ComputeDedupKey("character", 0, MararaShort); // SAME text, chapter 0
        var seededId = await SeedFindingAsync(db, bookId, "character", MararaShort, "acknowledged", cachedKey, 0);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, rows.Count); // NOT merged: two real chapters, two findings.
        var acknowledged = Assert.Single(rows, f => f.Id == seededId);
        Assert.Equal("acknowledged", acknowledged.Status);
        Assert.Equal(0, Assert.Single(DeserializeAnchors(acknowledged.ChapterAnchorsJson)).Order);
        var fresh = Assert.Single(rows, f => f.Id != seededId);
        Assert.Equal("open", fresh.Status);
        Assert.Equal(1, Assert.Single(DeserializeAnchors(fresh.ChapterAnchorsJson)).Order);
    }

    // ─── P5. THE ANCHORED SIDE KEEPS THE CHAPTER LINK — at the persist tier too. The cached ACKNOWLEDGED row is
    //         anchored to a real chapter; the fresh re-wording anchors NOTHING (the window saw the material but
    //         could not place it). Folding must not blank the anchor the user already has: a navigable finding
    //         must never be traded for a book-wide one. Only the ANCHOR is preserved, never the evidence —
    //         evidence is excerpts, and pairing one copy's quotes with another copy's prose would fabricate a
    //         finding neither of them states.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_NoAnchorIncomingClaimsAnAnchoredRow_TheChapterLinkSurvives()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                [2] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, Array.Empty<(int, string)>(), Array.Empty<int>())),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var cachedKey = BookFinding.ComputeDedupKey("character", 1, MararaShort);
        var seededId = await SeedFindingAsync(db, bookId, "character", MararaShort, "acknowledged", cachedKey, 1);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var row = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal(seededId, row.Id);
        Assert.Equal("acknowledged", row.Status);
        Assert.Equal(MararaLong, row.Rationale); // refreshed to the fresh (book-wide) prose...
        // ...but the chapter link is NOT blanked by the anchor-less copy.
        Assert.Equal(1, Assert.Single(DeserializeAnchors(row.ChapterAnchorsJson)).Order);
    }

    // ─── B10 (b3). A vanished-open NO-ANCHOR finding is PRESERVED on a PARTIAL build even though chapter 0 WAS
    //         re-reviewed. This is the delete-scope half of the collision: pre-b3 ChapterOrdersOf mapped the
    //         anchor-less row to {0}, so reviewing chapter 0 was enough to delete a BOOK-WIDE finding whose
    //         evidence lived in the chapter whose window just FAILED. ─────────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_NoAnchorFinding_PartialBuild_PreservedEvenThoughChapterZeroReviewed()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                // window 2 (ch 1) is ABSENT → empty output → the window FAILS → ch 1 was NOT re-reviewed
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // A cached book-wide finding with NO chapter anchors ("[]"), still open, which this build does not re-emit.
        await SeedOpenFindingAsync(db, bookId, "book-wide-key", "Book-wide finding, no anchors");
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(1, result.FailedWindows); // ch 1 NOT reviewed → the build is PARTIAL

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        // PRESERVED: the book was not fully re-reviewed, so the absence of a BOOK-WIDE finding is a failed-window
        // artifact, not the model retracting it. Pre-b3 (no-anchor == {0}, and 0 IS in reviewed) this was DELETED.
        Assert.Single(rows, f => f.Rationale == "Book-wide finding, no anchors");
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Equal(2, rows.Count);
    }

    // ─── B11 (b3). ...and the other branch of the STATED RULE: on a COMPLETE build the same vanished-open
    //         no-anchor finding IS deleted (it must not become the new immortal row). be-c01: "complete" now means
    //         EVERY window PRODUCED findings — an empty window no longer completes the coverage (W6a). ─────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_NoAnchorFinding_CompleteBuild_IsDeleted()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
                [2] = JsonCombinedFindings(new CombinedFindingSpec("pacing", "improve", 2, "Fresh ch1 finding", 1)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        await SeedOpenFindingAsync(db, bookId, "book-wide-key", "Book-wide finding, no anchors");
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);
        Assert.Equal(0, result.FailedWindows);
        Assert.Equal(result.ChaptersTotal, result.ChaptersReviewed); // every chapter really was reviewed

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.DoesNotContain(rows, f => f.Rationale == "Book-wide finding, no anchors"); // regenerated noise → gone
        Assert.Single(rows, f => f.Rationale == "Fresh ch0 finding");
        Assert.Single(rows, f => f.Rationale == "Fresh ch1 finding");
        Assert.Equal(2, rows.Count);
    }

    // ═══ b3 KEY MIGRATION. Changing the dedup-key derivation moves the key of every row whose primary order was a
    //     NO-ANCHOR sentinel (0 → "none") or an UNRESOLVED/mis-guessed order. The persist step matches a cached row
    //     to a fresh finding BY KEY to carry the user's Status (acknowledged / dismissed / done), so those rows
    //     would be ORPHANED and their Status silently LOST. The build therefore re-derives each fresh finding's
    //     LEGACY-V1 key and falls back to it, then UPGRADES the matched row's stored key in place. ═══

    // ─── M1. The live 2cf6fcf2 shape: an ACKNOWLEDGED finding cached under V1 with a PHANTOM anchor (order 16 in a
    //         2-chapter book). The rebuild drops the phantom (b1) → the finding is now no-anchor → its key MOVES
    //         from "plot|16|…" to "plot|none|…". It must be RE-MATCHED, not duplicated: same row, Status intact,
    //         key upgraded, phantom anchor cleaned. ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_AcknowledgedRowUnderLegacyKey_SurvivesRebuild_WithStatusAndKeyUpgraded()
    {
        const string rationale = "מורגן מציג קשת דמויות ברורה של דמויות משנה";
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                // The model re-emits the SAME finding with the SAME invented anchor (order 16, title "Chapter 16"):
                // neither matches a real chapter of this 2-chapter book, so b1 DROPS it → a no-anchor finding.
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, rationale, 16)),
                [2] = EmptyFindings,
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // The cached row EXACTLY as the pre-b3 build wrote it: key hashed over the RAW model order 16.
        var legacyKey = BookFinding.ComputeLegacyDedupKeyV1("plot", 16, rationale);
        var newKey = BookFinding.ComputeDedupKey("plot", null, rationale); // what this build will derive
        Assert.NotEqual(legacyKey, newKey);                                // the derivation really did move
        var seededId = await SeedFindingAsync(db, bookId, "plot", rationale, "acknowledged", legacyKey, 16);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var row = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal(seededId, row.Id);            // the SAME row — not orphaned + re-inserted as a duplicate
        Assert.Equal("acknowledged", row.Status);  // THE HAZARD: the user's decision survived the key change
        Assert.Equal(newKey, row.DedupKey);        // ...and the stored key was UPGRADED in place (self-healing)
        Assert.Empty(DeserializeAnchors(row.ChapterAnchorsJson)); // the phantom anchor is gone (b1)
    }

    // ─── M2. The other moved-key class: a row cached under V1's NO-ANCHOR sentinel ("plot|0|…" — the model emitted
    //         no anchors at all), which now keys as "plot|none|…". A DISMISSED row must survive it. ────────────
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_DismissedNoAnchorRowUnderLegacySentinelKey_SurvivesRebuild()
    {
        const string rationale = "Book-wide: the theme never resolves";
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("theme", rationale, Array.Empty<(int, string)>(), Array.Empty<int>())),
                [2] = EmptyFindings,
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // V1 hashed a no-anchor finding with the COLLIDING 0 sentinel; b3 hashes it as "none".
        var legacyKey = BookFinding.ComputeLegacyDedupKeyV1("theme", 0, rationale);
        var newKey = BookFinding.ComputeDedupKey("theme", null, rationale);
        Assert.NotEqual(legacyKey, newKey);
        var seededId = await SeedFindingAsync(db, bookId, "theme", rationale, "dismissed", legacyKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var row = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal(seededId, row.Id);
        Assert.Equal("dismissed", row.Status);
        Assert.Equal(newKey, row.DedupKey);
    }

    // ═══ be-f01 (P2-3) — THE KEY-MIGRATION SHIM MUST BE OBSERVABLE, AND THE ZERO CASE IS THE ONE THAT MATTERS.
    //     No counter existed for legacy-tier hits or in-place key upgrades before this fix: the shim had NO
    //     RETIREMENT CRITERION (nobody could ever show zero legacy-keyed rows remain) and was the only migration in
    //     this subsystem that could not be observed at all. The line must fire EVERY build, including — especially —
    //     when nothing legacy-matched, or a silently-unwired counter and a genuinely retired shim look identical. ═══

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_NoLegacyOrFuzzyMatches_StillLogsTheKeyMigrationLine_AtZero()
    {
        // A first build on a book with no prior BookFinding rows at all: nothing CAN match at any tier, so both
        // counters must be zero — and the line must still appear (the whole point of this fix).
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, "Fresh ch0 finding", 0)),
            }
        };
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(out _, holder, logCapture);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 1);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var migration = Assert.Single(logCapture.Entries,
            e => e.Message.Contains("dedup-key migration", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, migration.Level);
        Assert.Contains("0 finding(s) matched via the LEGACY", migration.Message, StringComparison.Ordinal);
        Assert.Contains("0 existing row(s) had their stored key rewritten", migration.Message, StringComparison.Ordinal);

        // The SAME zero-coverage principle applies to the reword-fold line (P1-6b): it must fire at 0/0 too, not
        // just when something actually folded.
        var reword = Assert.Single(logCapture.Entries, e => e.Message.StartsWith("Book review (dedup):", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, reword.Level);
        Assert.Contains("0 freshly built finding(s) were RE-WORDINGS", reword.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_ALegacyMatch_IsCountedAsBothALegacyMatchAndAKeyUpgrade()
    {
        // The M1 scenario (an acknowledged row cached under the pre-b3 key), instrumented: exactly ONE legacy match
        // and exactly ONE key upgrade — proving the counters measure the real event, not just "something happened".
        const string rationale = "מורגן מציג קשת דמויות ברורה של דמויות משנה";
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, rationale, 16)),
                [2] = EmptyFindings,
            }
        };
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(out _, holder, logCapture);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var legacyKey = BookFinding.ComputeLegacyDedupKeyV1("plot", 16, rationale);
        await SeedFindingAsync(db, bookId, "plot", rationale, "acknowledged", legacyKey, 16);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var migration = Assert.Single(logCapture.Entries,
            e => e.Message.Contains("dedup-key migration", StringComparison.Ordinal));
        Assert.Contains("1 finding(s) matched via the LEGACY", migration.Message, StringComparison.Ordinal);
        Assert.Contains("1 existing row(s) had their stored key rewritten", migration.Message, StringComparison.Ordinal);
    }

    // ═══ be-c07 (P2-1) — THE ONE PLACE THE FAIL-SAFE BIAS INVERTS ═════════════════════════════════════════════════
    //
    //     Everywhere else in this subsystem, a wrong merge still leaves the user A CARD. Not here. When the fuzzy tier
    //     claims a row the user has ACTED on, the fresh finding is NOT INSERTED and the row keeps the status the user
    //     gave it — so a genuinely DISTINCT fresh finding that wrongly claims a DISMISSED row NEVER REACHES THE USER
    //     AT ALL. It is not a duplicate; it is a deletion, and a silent one.
    //
    //     AND MayFold DOES NOT SAVE YOU. A BOOK-WIDE row has ComparisonOrder == null, which is a MayFold WILDCARD: it
    //     may fold against ANY chapter. So the anchor fence contributes NOTHING on such a row, and the 0.45 threshold
    //     — tuned on ONE book — was the only guard. Since b7, the windowed engine turns a finding it cannot place into
    //     a book-wide one AS A MATTER OF COURSE, such rows are never deleted, and they accumulate: an unbounded,
    //     permanent absorption surface.
    //
    //     THE FIX: a stricter bar (NearDuplicateCollapser.UserActedAnchorMismatchThreshold = 0.60) on exactly that
    //     path — a user-acted row, claimed across an anchor mismatch. Tuned on BOTH captured corpora; see the
    //     constant. The ordinary OPEN-row path keeps 0.45 and is pinned unchanged below.
    //     ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    // ─── D1. THE HARM, WITH THE REAL PAIR THAT CAUSES IT. Book A63A6E02's SEVERITY-3 FACTUAL CONTRADICTION scores
    //         0.462 against a SEVERITY-1 piece of praise in the same dimension — two findings of OPPOSITE polarity
    //         that a bag-of-words simply cannot separate. 0.462 is ABOVE the ordinary 0.45 bar. So if the praise had
    //         ever been DISMISSED and left book-wide, the contradiction — the single most valuable finding in the
    //         book — would have CLAIMED that dismissed row and VANISHED, silently, on every rebuild.
    [Fact]
    public async Task BuildBookReviewAsync_AFreshDISTINCTFinding_CanNoLongerClaimABookWideDISMISSEDRow_AndReachesTheUser()
    {
        // PREMISES, scored through the SHIPPED metric (never a reimplementation): the pair sits in the exact band
        // that used to be lethal — above the ordinary bar, below the user-acted one.
        var sim = NearDuplicateCollapser.Similarity(
            NearDuplicateCollapser.ContentTokens(B8Contradiction),
            NearDuplicateCollapser.ContentTokens(B8Daniel14));
        Assert.Equal(0.462, sim, 3);
        Assert.True(sim >= NearDuplicateCollapser.DefaultThreshold,
            $"premise: at the ordinary bar this claim GOES THROUGH ({sim:0.000} >= {NearDuplicateCollapser.DefaultThreshold})");
        Assert.True(sim < NearDuplicateCollapser.UserActedAnchorMismatchThreshold,
            $"premise: the be-c07 bar is what refuses it ({sim:0.000} < {NearDuplicateCollapser.UserActedAnchorMismatchThreshold})");

        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(new AnchorSpec(
                    "continuity", B8Contradiction, new[] { (12, "Chapter 12") }, Array.Empty<int>(), 3)),
            },
        };
        using var provider = BuildWindowedProvider(out _, holder, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        // THE DISMISSED ROW: a BOOK-WIDE copy of the praise. Book-wide → ComparisonOrder is null → a MayFold
        // WILDCARD, so the anchor fence contributes nothing and the THRESHOLD is the only thing standing here.
        var dismissedKey = BookFinding.ComputeDedupKey("continuity", null, B8Daniel14);
        var dismissedId = await SeedFindingAsync(db, bookId, "continuity", B8Daniel14, "dismissed", dismissedKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, rows.Count);

        // THE HARM, ASSERTED AS THE HARM: the severity-3 factual contradiction REACHES THE USER, as its own OPEN,
        // ANCHORED card. Pre-fix it claimed the dismissed row instead, was never inserted, and the user never saw it.
        var contradiction = Assert.Single(rows, f => f.Rationale == B8Contradiction);
        Assert.Equal("open", contradiction.Status);
        Assert.Equal(3, contradiction.Severity);
        Assert.NotEqual(dismissedId, contradiction.Id);
        Assert.Equal(12, Assert.Single(DeserializeAnchors(contradiction.ChapterAnchorsJson)).Order);

        // ...and the dismissed row is UNTOUCHED — still dismissed, still carrying its OWN prose. (The claim would
        // also have REWRITTEN this row's rationale with a finding that has nothing to do with it, so the user's
        // dismissal would have come to rest on text they never read.)
        var dismissed = Assert.Single(rows, f => f.Id == dismissedId);
        Assert.Equal("dismissed", dismissed.Status);
        Assert.Equal(B8Daniel14, dismissed.Rationale);
    }

    // ─── D2. b4b's ACTUAL PURPOSE MUST NOT REGRESS. A genuine RE-WORDING of a dismissed finding STILL claims its row,
    //         across the very same anchor mismatch — so a dismissed finding is NOT resurrected as an open card just
    //         because the model rephrased it. The pair is REAL (book A63A6E02's מררה character finding, emitted once
    //         book-wide and once anchored) and it scores 0.889: the fence has 0.289 of headroom over it, and the
    //         LOWEST of the seven real cross-bucket pairs (0.750) still clears 0.60 by 0.150.
    [Fact]
    public async Task BuildBookReviewAsync_AGenuineRewordingOfADismissedFinding_StillClaimsIt_AcrossTheAnchorMismatch()
    {
        var sim = NearDuplicateCollapser.Similarity(
            NearDuplicateCollapser.ContentTokens(MararaLong),
            NearDuplicateCollapser.ContentTokens(MararaShort));
        Assert.Equal(0.889, sim, 3);
        Assert.True(sim >= NearDuplicateCollapser.UserActedAnchorMismatchThreshold,
            $"premise: a REAL re-wording must still clear the stricter bar ({sim:0.000} >= {NearDuplicateCollapser.UserActedAnchorMismatchThreshold})");

        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, new[] { (0, "Chapter 0") }, new[] { 0 })),
                [2] = EmptyFindings,
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // The DISMISSED row is BOOK-WIDE and the fresh copy is ANCHORED — the anchor MISMATCH, i.e. the exact regime
        // the new bar governs. It must still be claimed: this is what stops the dismissal being undone by a rephrase.
        var dismissedKey = BookFinding.ComputeDedupKey("character", null, MararaShort);
        var dismissedId = await SeedFindingAsync(db, bookId, "character", MararaShort, "dismissed", dismissedKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(dismissedId, row.Id);          // the SAME row — claimed, not orphaned + duplicated
        Assert.Equal("dismissed", row.Status);      // ...and NOT resurrected as an open card
        Assert.Equal(MararaLong, row.Rationale);    // refreshed to the freshest phrasing, as an exact-key hit would
        Assert.DoesNotContain(rows, f => f.Status == "open");
    }

    // ─── D3. THE ORDINARY OPEN-ROW PATH IS UNCHANGED. The SAME 0.462 pair, the SAME anchor mismatch — but the row is
    //         OPEN, so the claim still goes through at 0.45. It must: claiming an open row is NOT a suppression. The
    //         row is refreshed and the user sees exactly one card; refusing would simply delete the row as vanished
    //         and insert the fresh copy — one card either way. There is no asymmetry to protect, so no recall is paid.
    //         (The row IDENTITY is what distinguishes the two outcomes, which is why this asserts the Id and not a
    //         count: a delete-then-insert would produce a NEW row with the same visible content.)
    [Fact]
    public async Task BuildBookReviewAsync_TheOrdinaryOpenRowFuzzyPath_IsUnchangedByTheUserActedBar()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(new AnchorSpec(
                    "continuity", B8Contradiction, new[] { (12, "Chapter 12") }, Array.Empty<int>(), 3)),
            },
        };
        using var provider = BuildWindowedProvider(out _, holder, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        // The SAME book-wide row as D1, at the SAME 0.462 similarity — but OPEN.
        var openKey = BookFinding.ComputeDedupKey("continuity", null, B8Daniel14);
        var openId = await SeedFindingAsync(db, bookId, "continuity", B8Daniel14, "open", openKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(openId, row.Id);               // CLAIMED at 0.45 and refreshed IN PLACE — the pre-be-c07 outcome
        Assert.Equal("open", row.Status);
        Assert.Equal(B8Contradiction, row.Rationale);
        Assert.Equal(12, Assert.Single(DeserializeAnchors(row.ChapterAnchorsJson)).Order); // it GAINED the anchor
    }

    // ═══ be-f01 (P2-2) — THE AUDIT TRAIL FOR THE ONE PATH THAT REWRITES A USER-ACTED ROW'S PROSE. A bare count
    //     ("1 reword fold onto a user-acted row") cannot tell a CORRECT fold from a WRONG one, and if the fuzzy
    //     0.45-0.60 guess is wrong the OLD text — the text the user actually acknowledged/dismissed — is gone the
    //     moment it is overwritten. Mirrors SynthesisMergeMap's KEPT/DELETED audit log for its own destructive op. ═

    [Fact]
    public async Task BuildBookReviewAsync_FuzzyFoldOntoADismissedRow_LogsOldAndNewSnippets_AndTheScore()
    {
        // Reuses D2's real pair (book A63A6E02's מררה finding, 0.889 similarity, across the anchor mismatch that
        // makes be-c07's 0.60 bar the one that applies).
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, new[] { (0, "Chapter 0") }, new[] { 0 })),
                [2] = EmptyFindings,
            }
        };
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(out _, holder, logCapture);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var dismissedKey = BookFinding.ComputeDedupKey("character", null, MararaShort);
        var dismissedId = await SeedFindingAsync(db, bookId, "character", MararaShort, "dismissed", dismissedKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        // Premise: the fold really happened onto the dismissed row (D2's own assertion).
        var row = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal(dismissedId, row.Id);
        Assert.Equal("dismissed", row.Status);

        var audit = Assert.Single(logCapture.Entries,
            e => e.Message.Contains("fuzzy re-wording fold onto USER-ACTED row", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Contains(dismissedId.ToString(), audit.Message, StringComparison.Ordinal);
        Assert.Contains("'dismissed'", audit.Message, StringComparison.Ordinal);
        Assert.Contains("0.889", audit.Message, StringComparison.Ordinal); // the actual score, not just a count
        Assert.Contains(
            NearDuplicateCollapser.UserActedAnchorMismatchThreshold.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            audit.Message, StringComparison.Ordinal); // the be-c07 bar that ACTUALLY applied (0.60), not the ordinary 0.45
        Assert.Contains(MararaShort[..30], audit.Message, StringComparison.Ordinal); // the OLD text
        Assert.Contains(MararaLong[..30], audit.Message, StringComparison.Ordinal);  // the NEW text
    }

    [Fact]
    public async Task BuildBookReviewAsync_FuzzyFoldOntoAnOpenRow_DoesNotLogTheUserActedAuditTrail()
    {
        // D3's scenario: the SAME kind of fuzzy fold, but onto an OPEN row. This is routine (the row is refreshed
        // either way, no user decision is at risk), so it must NOT get the user-acted audit line — only the ordinary
        // unconditional reword-fold coverage line (P1-6b) fires.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(new AnchorSpec(
                    "continuity", B8Contradiction, new[] { (12, "Chapter 12") }, Array.Empty<int>(), 3)),
            },
        };
        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(out _, holder, logCapture, bookContextTokenBudget: 100_000);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        var openKey = BookFinding.ComputeDedupKey("continuity", null, B8Daniel14);
        await SeedFindingAsync(db, bookId, "continuity", B8Daniel14, "open", openKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        Assert.DoesNotContain(logCapture.Entries, e => e.Message.Contains("USER-ACTED row", StringComparison.Ordinal));
        var reword = Assert.Single(logCapture.Entries, e => e.Message.StartsWith("Book review (dedup):", StringComparison.Ordinal));
        Assert.Contains("1 freshly built finding(s) were RE-WORDINGS", reword.Message, StringComparison.Ordinal);
        Assert.Contains("0 of those", reword.Message, StringComparison.Ordinal); // 0 onto a user-acted row
    }

    // ═══ be-c04 PHASE-SEPARATED MATCH TIERS. The three tiers (current key → legacy key → fuzzy re-wording) used to
    //     run PER FRESH FINDING, in list order: finding 1 went key→key→fuzzy before finding 2 was looked at. A row is
    //     claimed at most once, so an EARLIER finding's FUZZY claim (a 0.45-similarity GUESS) could take the very row
    //     a LATER finding matched EXACTLY BY KEY; that later finding then found its row claimed, fell through every
    //     tier, and was INSERTED AS A NEW OPEN ROW. When the hijacked row was DISMISSED, the exact re-emission of the
    //     dismissed finding came back as an OPEN CARD — the precise harm the persisted tier exists to prevent.
    //
    //     THE ENABLING STATE IS PRODUCTION-REAL, NOT CONTRIVED: a row's stored DedupKey hashes the RAW model prose,
    //     while its stored Rationale is the REPAIRED prose (DynamicTermRepairService rewrites Rationale in place
    //     AFTER the key is stamped, and the raw prose is never persisted — the reason BOOKREVIEW_FINDINGS_ANCHOR_AND_
    //     DEDUP.md states a recompute-from-row backfill is impossible). So the key and the Rationale of a repaired
    //     row legitimately DISAGREE, which is what lets a fresh finding key-match a row whose PROSE a different fresh
    //     finding near-duplicates. The rows below are seeded in exactly that state.
    //
    //     The fix runs the tiers TIER-MAJOR: tier 1 over EVERY incoming finding, then tier 2 over the leftovers, then
    //     the fuzzy tier over what remains. A stronger tier can never lose a row to a weaker one, in any list order.
    //     ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A REAL, DISTINCT character finding (book A63A6E02's דניאל card — the one gemma4:12b falsely merged
    /// during the b8 live gate). Deliberately NOT a near-duplicate of the מררה pair, so the BUILD-time collapser
    /// leaves both incoming copies alone and the persist step really does see two findings.</summary>
    private const string DanielBroken =
        "תיאורו של דניאל כדמות שבורה הנשענת על הרגלי ילדות מעניק לסיפורו עומק ואמינות.";

    /// <summary>A REAL, DISTINCT plot finding (a third dimension, so it cannot fold into either of the two above).</summary>
    private const string PlotRepetition =
        "מבנה העלילה חוזר על עצמו: הפרק פותח ומסתיים באותה סצנת המתנה, והקצב נבלם.";

    // ─── C1. THE P1-3 REPRODUCER, EXACT-KEY TIER. Two fresh findings; ONE cached DISMISSED row.
    //         • fresh A (window 1, anchored ch 0) is a RE-WORDING of the dismissed row's persisted PROSE (0.889).
    //         • fresh B (window 2, book-wide) is the EXACT re-emission the dismissed row was keyed on.
    //         List order puts A first. Pre-fix, A's FUZZY claim took the row, B fell through, and B — a finding the
    //         user DISMISSED — was inserted as a NEW OPEN CARD. The row must go to its EXACT-KEY claimant.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_ExactKeyMatchOnADismissedRow_BeatsAnEarlierFuzzyClaim_NoResurrection()
    {
        // PREMISE 1 — A really can fuzzy-claim the row (scored with the SHIPPED metric, never a reimplementation).
        Assert.True(NearDuplicateCollapser.Similarity(
            NearDuplicateCollapser.ContentTokens(MararaLong),
            NearDuplicateCollapser.ContentTokens(MararaShort)) >= NearDuplicateCollapser.DefaultThreshold);
        // PREMISE 2 — ...but A and B are NOT near-duplicates of each other, so the BUILD-time collapser does not
        // merge them and BOTH reach the persist step. (This is the "saved by another class" invariant the review
        // says does not hold in production, made explicit and false here.)
        Assert.True(NearDuplicateCollapser.Similarity(
            NearDuplicateCollapser.ContentTokens(MararaLong),
            NearDuplicateCollapser.ContentTokens(DanielBroken)) < NearDuplicateCollapser.DefaultThreshold);

        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, new[] { (0, "Chapter 0") }, new[] { 0 })),
                [2] = JsonAnchoredFindings(
                    new AnchorSpec("character", DanielBroken, Array.Empty<(int, string)>(), Array.Empty<int>())),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // The DISMISSED row, in the production state described above: KEYED on B's raw prose, but CARRYING the
        // (repaired) prose that A near-duplicates. Book-wide (no anchors) → MayFold's wildcard → A may claim it.
        var dismissedKey = BookFinding.ComputeDedupKey("character", null, DanielBroken);
        var seededId = await SeedFindingAsync(db, bookId, "character", MararaShort, "dismissed", dismissedKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, rows.Count);

        // THE HARM, ASSERTED FIRST: the DISMISSED finding is not back as an OPEN CARD. (Pre-fix this is exactly what
        // happened — A's fuzzy claim took the row, B fell through every tier, and B was inserted as "open".)
        Assert.DoesNotContain(rows, f => f.Status == "open" && f.Rationale == DanielBroken);

        // THE ROW WENT TO ITS EXACT-KEY CLAIMANT: still dismissed, refreshed with B's own prose, key unchanged.
        var dismissed = Assert.Single(rows, f => f.Id == seededId);
        Assert.Equal("dismissed", dismissed.Status);
        Assert.Equal(DanielBroken, dismissed.Rationale);
        Assert.Equal(dismissedKey, dismissed.DedupKey);

        // ...and A, which matched NO key, is inserted as its own open row (a distinct finding is never lost).
        var inserted = Assert.Single(rows, f => f.Id != seededId);
        Assert.Equal("open", inserted.Status);
        Assert.Equal(MararaLong, inserted.Rationale);
        Assert.Equal(0, Assert.Single(DeserializeAnchors(inserted.ChapterAnchorsJson)).Order);

        // No row claimed twice, and no two rows fighting over one key (the (BookId, Language, DedupKey) index).
        Assert.Equal(2, rows.Select(f => f.Id).Distinct().Count());
        Assert.Equal(2, rows.Select(f => f.DedupKey).Distinct(StringComparer.Ordinal).Count());
    }

    // ─── C2. THE SAME, ONE TIER DOWN: the dismissed row is cached under a LEGACY-V1 key. Phase 1 must cover BOTH key
    //         tiers before ANY fuzzy match runs, or b3's migration shim inherits the identical hijack — and a legacy
    //         row is exactly the one whose Status has been carried the longest.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_LegacyKeyMatchOnADismissedRow_BeatsAnEarlierFuzzyClaim_AndUpgradesTheKey()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, new[] { (0, "Chapter 0") }, new[] { 0 })),
                [2] = JsonAnchoredFindings(
                    new AnchorSpec("character", DanielBroken, Array.Empty<(int, string)>(), Array.Empty<int>())),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // Cached EXACTLY as a pre-b3 build wrote it: V1 hashed a no-anchor finding with the COLLIDING 0 sentinel.
        var legacyKey = BookFinding.ComputeLegacyDedupKeyV1("character", 0, DanielBroken);
        var upgradedKey = BookFinding.ComputeDedupKey("character", null, DanielBroken);
        Assert.NotEqual(legacyKey, upgradedKey); // premise: the derivation really did move
        var seededId = await SeedFindingAsync(db, bookId, "character", MararaShort, "dismissed", legacyKey);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, rows.Count);

        // THE HARM, ASSERTED FIRST: a legacy-keyed DISMISSED finding is not resurrected as an OPEN CARD either.
        Assert.DoesNotContain(rows, f => f.Status == "open" && f.Rationale == DanielBroken);

        var dismissed = Assert.Single(rows, f => f.Id == seededId);
        Assert.Equal("dismissed", dismissed.Status);
        Assert.Equal(DanielBroken, dismissed.Rationale);   // its OWN legacy-key claimant refreshed it...
        Assert.Equal(upgradedKey, dismissed.DedupKey);     // ...and the in-place key UPGRADE still fires (b3)

        var inserted = Assert.Single(rows, f => f.Id != seededId);
        Assert.Equal("open", inserted.Status);
        Assert.Equal(MararaLong, inserted.Rationale);
        Assert.Equal(2, rows.Select(f => f.DedupKey).Distinct(StringComparer.Ordinal).Count());
    }

    // ─── C3. THE FUZZY TIER STILL WORKS ON GENUINE LEFTOVERS (b4b must not regress), IN THE SAME BUILD AS A KEY
    //         MATCH. One fresh finding key-matches a DISMISSED row; the other is a RE-WORDING of an ACKNOWLEDGED row
    //         no key can reach. Phase 2 runs over what phase 1 left behind: BOTH rows are claimed, both Statuses
    //         survive, and NOTHING is inserted.
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_KeyMatchAndFuzzyMatchInOneBuild_BothRowsClaimed_StatusesPreserved()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                // window 1 (ch 0): the RE-WORDING of the acknowledged character row → only the fuzzy tier can place it
                [1] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, new[] { (0, "Chapter 0") }, new[] { 0 })),
                // window 2 (ch 1): a VERBATIM re-emission of the dismissed plot row → an exact-key match
                [2] = JsonCombinedFindings(new CombinedFindingSpec("plot", "improve", 2, PlotRepetition, 1)),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var dismissedKey = BookFinding.ComputeDedupKey("plot", 1, PlotRepetition);
        var dismissedId = await SeedFindingAsync(db, bookId, "plot", PlotRepetition, "dismissed", dismissedKey, 1);
        var acknowledgedKey = BookFinding.ComputeDedupKey("character", 0, MararaShort);
        var acknowledgedId = await SeedFindingAsync(db, bookId, "character", MararaShort, "acknowledged", acknowledgedKey, 0);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var rows = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, rows.Count); // nothing inserted: every fresh finding found its row

        var dismissed = Assert.Single(rows, f => f.Id == dismissedId);
        Assert.Equal("dismissed", dismissed.Status);     // tier 1 (exact key)
        Assert.Equal(PlotRepetition, dismissed.Rationale);

        var acknowledged = Assert.Single(rows, f => f.Id == acknowledgedId);
        Assert.Equal("acknowledged", acknowledged.Status);            // tier 3 (fuzzy) still fires...
        Assert.Equal(MararaLong, acknowledged.Rationale);             // ...refreshing the row with the new phrasing
        Assert.Equal(BookFinding.ComputeDedupKey("character", 0, MararaLong), acknowledged.DedupKey);
        Assert.Equal(0, Assert.Single(DeserializeAnchors(acknowledged.ChapterAnchorsJson)).Order);

        Assert.Equal(2, rows.Select(f => f.DedupKey).Distinct(StringComparer.Ordinal).Count());
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

    /// <summary>
    /// be-c03: seeds a book with <paramref name="chapterCount"/> chapters where ONE of them
    /// (<paramref name="emptyChapterOrder"/>) is GENUINELY EMPTY — no ChunkSummary (so no structured brief AND no
    /// flat summary) and BLANK ContentText. That is exactly the shape
    /// <c>BookContextAssembler.BuildChapterBlock</c> returns a NULL block for, so the windower SKIPS it: it is a
    /// REAL chapter (a Chapters row, in <c>chaptersByOrder</c>) that is NOT REVIEWABLE and can never be reviewed.
    /// The real-world instances are a title-only "Part I" divider and a DOCX import artefact.
    ///
    /// Contrast <see cref="SeedPartialBriefBookAsync"/>, whose brief-less chapter still has CONTENT and therefore
    /// still windows (via the raw-text back-fill) — it IS reviewable. The two helpers pin the two halves of the
    /// denominator: a brief-less chapter counts, an empty one does not.
    /// </summary>
    private static async Task<Guid> SeedBookWithEmptyChapterAsync(AppDbContext db, int chapterCount, int emptyChapterOrder)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Book With An Empty Chapter", Language = "he" });
        var briefCount = 0;
        for (var i = 0; i < chapterCount; i++)
        {
            var chId = Guid.NewGuid();
            var isEmpty = i == emptyChapterOrder;
            db.Chapters.Add(new Chapter
            {
                Id = chId,
                BookId = bookId,
                Order = i,
                Title = isEmpty ? "חלק ראשון" : $"Chapter {i}", // a title-only divider: a heading and nothing else
                ContentText = isEmpty ? string.Empty : $"תוכן {i}."
            });
            if (isEmpty)
                continue; // no ChunkSummary at all → no structured brief AND no flat summary → a NULL block
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId, ChapterId = chId, Language = "he",
                StructuredJson = StructuredBriefJson, BuiltWithModel = ActiveModel,
                StructuredBuiltAt = DateTimeOffset.UtcNow.AddMinutes(1) // fresh: after the chapter UpdatedAt
            });
            briefCount++;
        }
        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId, Language = "he",
            BookBriefJson = """{ "genre": "Fantasy", "themes": ["isolation"] }""",
            BuiltChapterCount = briefCount, BuiltWithModel = ActiveModel // the empty chapter has no brief to build
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

    // ─── b1: MODEL-SUPPLIED CHAPTER ANCHORS ARE VALIDATED AGAINST THE BOOK'S REAL CHAPTERS ──────────────
    //
    // The model invents chapter anchors. On the live reproducer (book 2cf6fcf2, ONE chapter at Order=0 titled
    // "פרק 16") it emitted anchors claiming orders 1 and 16 — the 16 read straight out of the TITLE. Neither
    // exists, so the old chaptersByOrder backfill silently left every anchor at ChapterId = Guid.Empty: an
    // un-navigable phantom whose order is no real chapter order, hence never deletable by the scoped
    // delete-vanished-open pass, hence accumulating on every rebuild. ChapterAnchorResolver now resolves each
    // anchor by ORDER, then by TITLE, else DROPS it, and every drop is logged.

    /// <summary>b1: a hallucinated ORDER whose TITLE matches a real chapter resolves BY TITLE, and the invented
    /// order is CORRECTED to that chapter's real (0-based) order. This is the live 2cf6fcf2 case: the model read
    /// "16" out of the title of the book's only chapter.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_AnchorWithHallucinatedOrderButRealTitle_ResolvesByTitle()
    {
        // Book has chapters 0 and 1 ("Chapter 0", "Chapter 1"). The model anchors to order 16, title "Chapter 1".
        var combined = JsonAnchoredFindings(new AnchorSpec(
            "plot", "Order invented, title real", new[] { (16, "Chapter 1") }, new[] { 1 }));

        using var provider = BuildCombinedProvider(out _, combined);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var realChapter1 = await db.Chapters.AsNoTracking().SingleAsync(c => c.BookId == bookId && c.Order == 1);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var finding = await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId);
        var anchors = DeserializeAnchors(finding.ChapterAnchorsJson);

        var anchor = Assert.Single(anchors);
        Assert.Equal(1, anchor.Order);                    // CORRECTED from the invented 16
        Assert.Equal(realChapter1.Id, anchor.ChapterId);  // and pinned to the real chapter
        Assert.NotEqual(Guid.Empty, anchor.ChapterId);
        Assert.Equal("Chapter 1", anchor.Title);
    }

    /// <summary>b1: an anchor that resolves by NEITHER order nor title is DROPPED (never persisted with an empty
    /// chapterId) and a WARNING naming the book and the unresolved (order, title) is logged. A silently swallowed
    /// unresolvable reference is what hid this bug: the drop must be observable.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_UnresolvableAnchor_IsDroppedAndWarningLogged()
    {
        // Book has chapters 0 and 1. The model anchors to order 16, title "פרק 16" — neither exists.
        var combined = JsonAnchoredFindings(new AnchorSpec(
            "plot", "Anchored to a chapter that does not exist", new[] { (16, "פרק 16") }, new[] { 16 }));

        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildCombinedProvider(out _, combined, logCapture);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var finding = await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId);

        // The phantom anchor is GONE — not persisted with Guid.Empty, and its phantom order 16 is nowhere.
        Assert.Empty(DeserializeAnchors(finding.ChapterAnchorsJson));
        Assert.DoesNotContain("16", finding.ChapterAnchorsJson);
        // The phantom evidence reference (chapterOrder 16) is dropped too: it would point the reader at a
        // chapter the excerpt is not in.
        Assert.Equal("[]", finding.EvidenceJson);

        // And it is LOUD: a warning naming the book and the unresolved (order, title).
        var warnings = logCapture.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        var drop = Assert.Single(warnings, m => m.Contains("DROPPED") && m.Contains(bookId.ToString()));
        Assert.Contains("order=16", drop);
        Assert.Contains("פרק 16", drop);
    }

    /// <summary>b1: a VALID anchor (a real 0-based chapter order) is untouched apart from the chapterId backfill.
    /// The resolver must not "fix" what is already correct, and must log no drop warning.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_ValidAnchor_IsUntouchedAndNothingIsLogged()
    {
        var combined = JsonAnchoredFindings(new AnchorSpec(
            "plot", "Anchored to a real chapter", new[] { (1, "Chapter 1") }, new[] { 1 }));

        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildCombinedProvider(out _, combined, logCapture);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var realChapter1 = await db.Chapters.AsNoTracking().SingleAsync(c => c.BookId == bookId && c.Order == 1);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var finding = await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId);

        var anchor = Assert.Single(DeserializeAnchors(finding.ChapterAnchorsJson));
        Assert.Equal(1, anchor.Order);
        Assert.Equal(realChapter1.Id, anchor.ChapterId);
        Assert.Equal("Chapter 1", anchor.Title);

        // Evidence keeps its real chapterOrder and gains the real chapterId.
        var evidence = JsonSerializer.Deserialize<List<FindingEvidence>>(
            finding.EvidenceJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var e = Assert.Single(evidence);
        Assert.Equal(1, e.ChapterOrder);
        Assert.Equal(realChapter1.Id, e.ChapterId);

        Assert.DoesNotContain(logCapture.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Book review (anchors)"));
    }

    /// <summary>b1 REGRESSION: a finding whose anchors are ALL unresolvable still PERSISTS, with its rationale
    /// intact and NO Guid.Empty anchor row. A bogus anchor does not make the criticism bogus, so the finding is
    /// kept as a NO-ANCHOR finding rather than deleted or persisted with a phantom.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_AllAnchorsDropped_FindingStillPersistsWithNoEmptyGuidAnchor()
    {
        // Two bogus anchors on one finding, plus a second finding with a good anchor to prove the build is normal.
        var combined = JsonAnchoredFindings(
            new AnchorSpec("plot", "Every anchor is bogus but the point stands",
                new[] { (16, "פרק 16"), (7, "מבחן המגדל") }, new[] { 16, 7 }),
            new AnchorSpec("theme", "This one is anchored properly",
                new[] { (0, "Chapter 0") }, new[] { 0 }));

        using var provider = BuildCombinedProvider(out _, combined);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(2, persisted.Count);

        // The all-bogus-anchor finding SURVIVES, carrying its rationale, as a no-anchor finding.
        var orphan = Assert.Single(persisted, f => f.Dimension == "plot");
        Assert.Equal("Every anchor is bogus but the point stands", orphan.Rationale);
        Assert.Empty(DeserializeAnchors(orphan.ChapterAnchorsJson));

        // NOWHERE in this book's findings is an anchor persisted with an empty chapterId — the phantom-anchor
        // class of row (the one the scoped delete could never reach) is now unproducible.
        foreach (var f in persisted)
        {
            Assert.DoesNotContain(Guid.Empty, DeserializeAnchors(f.ChapterAnchorsJson).Select(a => a.ChapterId));
            Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", f.ChapterAnchorsJson);
        }
    }

    /// <summary>b1: a title match is CASE/WHITESPACE tolerant but never collides two different chapters — an
    /// ambiguous (duplicated) title cannot identify a chapter, so an anchor relying on it is DROPPED rather than
    /// pinned to an arbitrary one of the candidates.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_AmbiguousTitle_DoesNotResolve_AnchorIsDropped()
    {
        var combined = JsonAnchoredFindings(new AnchorSpec(
            "plot", "Anchored by an ambiguous title", new[] { (9, "Twin") }, Array.Empty<int>()));

        using var provider = BuildCombinedProvider(out _, combined);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        // Rename BOTH chapters to the same title: the title no longer identifies a chapter.
        foreach (var ch in await db.Chapters.Where(c => c.BookId == bookId).ToListAsync())
            ch.Title = "Twin";
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var finding = await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId);
        Assert.Empty(DeserializeAnchors(finding.ChapterAnchorsJson)); // dropped, not guessed
    }

    /// <summary>b1: the TITLE fallback normalizes cosmetically (case, whitespace runs, leading/trailing space, and
    /// the invisible RTL/LTR marks a Hebrew title picks up from a Word/Syncfusion round-trip) so a real chapter is
    /// still found. It must absorb only cosmetic differences.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_TitleFallback_NormalizesCaseWhitespaceAndBidiMarks()
    {
        const string hebrewTitle = "פרק שני";
        // The model echoes the title with a right-to-left mark, doubled spaces, padding and different casing.
        var modelTitle = "  ‏פרק   שני‎ ";

        var combined = JsonAnchoredFindings(new AnchorSpec(
            "plot", "Title echoed with cosmetic noise", new[] { (42, modelTitle) }, Array.Empty<int>()));

        using var provider = BuildCombinedProvider(out _, combined);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var ch1 = await db.Chapters.SingleAsync(c => c.BookId == bookId && c.Order == 1);
        ch1.Title = hebrewTitle;
        await db.SaveChangesAsync();

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var finding = await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId);
        var anchor = Assert.Single(DeserializeAnchors(finding.ChapterAnchorsJson));
        Assert.Equal(1, anchor.Order);          // resolved by title; the invented 42 is corrected
        Assert.Equal(ch1.Id, anchor.ChapterId);
        Assert.Equal(hebrewTitle, anchor.Title); // the REAL title is persisted, not the model's noisy echo
    }

    // ─── b7: AN ANCHOR TO A REAL CHAPTER THE PASS NEVER SAW IS A GUESS, AND IS DROPPED ──────────────────
    //
    // b1 asks "is this a real chapter of the BOOK?" — and on a multi-chapter book the answer is almost always YES,
    // because a hallucinated order lands on SOME real chapter. That check therefore CANNOT see the failure the live
    // 17-chapter book showed: the review is a MAP-REDUCE, no pass sees the whole book, and a window shown chapters
    // 11-16 anchored a finding to chapters 2 and 5. Real orders, real chapterIds, resolver silent, card navigates —
    // straight to the WRONG CHAPTER. It also silently DEFEATED b4b: two copies of one finding sitting on two
    // DIFFERENT real chapters are exactly what MayFold refuses to merge (that is supposed to mean "two distinct
    // findings"), so the duplicate the collapser exists to kill survived. Both are closed by ONE rule: an anchor to
    // a chapter the emitting pass was not SHOWN is unresolvable.
    //
    // The seeded book is windowed one chapter per window (the tiny test budget), so "what this pass saw" is exact.

    /// <summary>b7 CORE: window 2 (which was shown ONLY chapter 1) anchors a finding to chapter 0 — a REAL chapter,
    /// but one it never read. The anchor is DROPPED (the finding survives book-wide) and a WARNING says so. Under b1
    /// alone this anchor resolved happily and the finding was persisted pointing at the wrong chapter.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_AnchorToRealButUnseenChapter_IsDroppedAndWarningLogged()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                // Window 2 sees chapter 1 ONLY. It anchors to chapter 0 (real, but not shown to it) and cites
                // chapter 0 as evidence — an excerpt it cannot have read.
                [2] = JsonAnchoredFindings(new AnchorSpec(
                    "tone", "A finding about chapter 1 that anchors chapter 0",
                    new[] { (0, "Chapter 0") }, new[] { 0 })),
            }
        };

        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(out _, holder, logCapture);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var finding = await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId);

        // The finding SURVIVES (a bogus anchor does not make the criticism bogus — b1's rule) but it is now
        // BOOK-WIDE: the anchor onto the unseen chapter 0 is gone, and so is its evidence reference.
        Assert.Equal("A finding about chapter 1 that anchors chapter 0", finding.Rationale);
        Assert.Empty(DeserializeAnchors(finding.ChapterAnchorsJson));
        Assert.Equal("[]", finding.EvidenceJson);

        // And it is LOUD, on its OWN warning: this is MIS-ANCHORING (a real chapter), not a phantom order, and the
        // two are logged separately because they are different model failures.
        var warnings = logCapture.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        var unseen = Assert.Single(warnings, m => m.Contains("NOT SHOWN") && m.Contains(bookId.ToString()));
        Assert.Contains("order=0", unseen);
        Assert.Contains("MIS-ANCHORING", unseen);
    }

    /// <summary>b7: the gate must not fire on a CORRECT anchor. Window 2 anchors the chapter it was actually shown,
    /// so the anchor survives with its real chapterId and NOTHING is logged. The fence against over-dropping.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_AnchorToShownChapter_IsUntouched()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                [2] = JsonAnchoredFindings(new AnchorSpec(
                    "tone", "A finding about chapter 1 anchored to chapter 1",
                    new[] { (1, "Chapter 1") }, new[] { 1 })),
            }
        };

        var logCapture = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(out _, holder, logCapture);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        var realChapter1 = await db.Chapters.AsNoTracking().SingleAsync(c => c.BookId == bookId && c.Order == 1);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var finding = await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId);
        var anchor = Assert.Single(DeserializeAnchors(finding.ChapterAnchorsJson));
        Assert.Equal(1, anchor.Order);
        Assert.Equal(realChapter1.Id, anchor.ChapterId);

        // Evidence on the shown chapter is kept too, with its real chapterId backfilled.
        var evidence = JsonSerializer.Deserialize<List<FindingEvidence>>(
            finding.EvidenceJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(1, Assert.Single(evidence).ChapterOrder);

        Assert.DoesNotContain(logCapture.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("NOT SHOWN"));
    }

    /// <summary>b7 CLOSES THE LOOP WITH b4b — the reason the cross-bucket fold fired ZERO times on the real book.
    /// The SAME finding is emitted by two windows: window 2 anchors it CORRECTLY (chapter 1, which it was shown),
    /// window 3 anchors it to chapter 0 — real, but a chapter window 3 never saw. Before b7 both anchors resolved,
    /// so the two copies sat on two DIFFERENT REAL chapters, which is precisely the shape b4b's MayFold REFUSES to
    /// merge (two different real chapters are supposed to mean two distinct findings). Result: a duplicate card, on
    /// a book where every deterministic test was green. With b7 the fabricated anchor is dropped, the copy becomes
    /// BOOK-WIDE, and MayFold(null, 1) folds it onto the anchored twin — ONE row, keeping the chapter link.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_FabricatedAnchorDroppedThenFoldsIntoAnchoredTwin()
    {
        // The real sentence from book A63A6E02 (the finding the live model emitted twice with different anchors).
        const string rationale =
            "המעבר לדמות הסופר יוצר שינוי חד בטון ובמצב הנפשי של הגיבור, מה שעלול ליצור תחושת ניתוק.";

        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                // Window 2 (shown chapter 1) anchors chapter 1: CORRECT.
                [2] = JsonAnchoredFindings(new AnchorSpec(
                    "tone", rationale, new[] { (1, "Chapter 1") }, new[] { 1 })),
                // Window 3 (shown chapter 2) re-emits the SAME finding anchored to chapter 0: REAL, UNSEEN.
                [3] = JsonAnchoredFindings(new AnchorSpec(
                    "tone", rationale, new[] { (0, "Chapter 0") }, new[] { 0 })),
            }
        };

        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);
        var realChapter1 = await db.Chapters.AsNoTracking().SingleAsync(c => c.BookId == bookId && c.Order == 1);

        // PREMISE CHECK, so this test cannot pass for the wrong reason: the two copies have DIFFERENT exact dedup
        // keys (b3 folds the resolved primary order into the key), so nothing in the exact-key dedup can merge them.
        Assert.NotEqual(
            BookFinding.ComputeDedupKey("tone", 1, rationale),
            BookFinding.ComputeDedupKey("tone", 0, rationale));

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        // ONE row, not two. The fold fired.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var row = Assert.Single(persisted);
        Assert.Equal(rationale, row.Rationale);

        // The ANCHORED copy is the survivor (b4b: anchoredness is a constraint, not a tie-break) and it keeps the
        // chapter the model actually READ — chapter 1, never the fabricated chapter 0.
        var anchor = Assert.Single(DeserializeAnchors(row.ChapterAnchorsJson));
        Assert.Equal(1, anchor.Order);
        Assert.Equal(realChapter1.Id, anchor.ChapterId);
    }

    /// <summary>b7: the OVERLAP chapters a window repeats from the previous window ARE shown to it, so an anchor
    /// onto one is grounded in text the model was given and must NOT be dropped. The shown-set is "what the context
    /// PRINTS", which is deliberately WIDER than "what this window is accountable for" (its primaries) — conflating
    /// the two would throw away the correct anchors the overlap exists to produce.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_AnchorToOverlapChapter_IsKept()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                // With overlap K=1, window 2's primary is chapter 1 and it ALSO shows chapter 0 as overlap.
                [2] = JsonAnchoredFindings(new AnchorSpec(
                    "continuity", "A break that straddles the window boundary, seen via the overlap",
                    new[] { (0, "Chapter 0"), (1, "Chapter 1") }, new[] { 0 })),
            }
        };

        using var provider = BuildWindowedProvider(out _, holder, overlapChapters: 1);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var finding = await db.BookFindings.AsNoTracking().SingleAsync(f => f.BookId == bookId);
        var anchors = DeserializeAnchors(finding.ChapterAnchorsJson);

        // BOTH anchors survive: chapter 0 came in as the overlap block, chapter 1 is the primary.
        Assert.Equal(2, anchors.Count);
        Assert.Contains(anchors, a => a.Order == 0);
        Assert.Contains(anchors, a => a.Order == 1);
        Assert.DoesNotContain(anchors, a => a.ChapterId == Guid.Empty);
    }

    /// <summary>b7: the window prompt STATES its allowlist — the model is told which orders it may anchor to, not
    /// merely punished afterwards for guessing. Composes with the resolver (prompt makes it right more often; the
    /// resolver makes a wrong one harmless) and is rendered from the SAME set the resolver gates on.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_WindowPromptCarriesTheAnchorAllowlist()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?> { [1] = EmptyFindings, [2] = EmptyFindings, [3] = EmptyFindings }
        };

        using var provider = BuildWindowedProvider(out var routerMock, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        // Window 2 is shown chapter 1 ONLY (one chapter per window at the test budget, overlap 0), so its prompt
        // must allow exactly "1" — and window 3 exactly "2". A prompt that offered the whole book's orders would
        // be re-inviting the very guess this todo exists to stop.
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.Instruction != null
                    && req.Instruction.Contains("זהו חלון 2")
                    && req.Instruction.Contains("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 1")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        routerMock.Verify(
            r => r.CompleteAsync(
                It.Is<AiRequest>(req => req.Instruction != null
                    && req.Instruction.Contains("זהו חלון 3")
                    && req.Instruction.Contains("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 2")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>b7 SYNTHESIS: the reduce pass sees NO chapter text at all — its [BOOK_CONTEXT] is the BookBrief and
    /// its only chapter numbers are the ones its [WINDOW_FINDINGS] digest prints. So an order that appears in the
    /// digest is anchorable; any other order is a number the model invented, and is dropped. (This also covers a b1
    /// oversight: the generic order rule in the synthesis prompt points the model at "the chapter heading inside
    /// [BOOK_CONTEXT]", a heading this pass is never given.)</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_SynthesisAnchor_OutsideItsDigest_IsDropped()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                // Only chapter 0 ever reaches the digest, so the digest prints exactly one order: 0.
                [1] = JsonAnchoredFindings(new AnchorSpec(
                    "plot", "A window finding on chapter zero", new[] { (0, "Chapter 0") }, new[] { 0 })),
                [2] = EmptyFindings,
                [3] = EmptyFindings,
            },
            SynthesisResponse = JsonAnchoredFindings(
                // Anchored to an order the digest DOES print → kept.
                new AnchorSpec("theme", "Synthesis finding anchored to a digest order",
                    new[] { (0, "Chapter 0") }, Array.Empty<int>()),
                // Anchored to chapter 2: real, but NOWHERE in what synthesis was shown → dropped.
                new AnchorSpec("pacing", "Synthesis finding anchored to an order it never saw",
                    new[] { (2, "Chapter 2") }, new[] { 2 })),
        };

        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();

        // The digest-grounded synthesis anchor survives...
        var grounded = Assert.Single(persisted, f => f.Dimension == "theme");
        Assert.Equal(0, Assert.Single(DeserializeAnchors(grounded.ChapterAnchorsJson)).Order);

        // ...while the invented one is stripped to a book-wide finding (kept, but never pointing at chapter 2).
        var invented = Assert.Single(persisted, f => f.Dimension == "pacing");
        Assert.Empty(DeserializeAnchors(invented.ChapterAnchorsJson));
        Assert.Equal("[]", invented.EvidenceJson);
    }

    /// <summary>b7 SYNTHESIS PROMPT: the synthesis instruction carries the allowlist of the orders its digest prints
    /// — the set it may anchor to — so the model is constrained at the source, not only at the parser.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_SynthesisPromptCarriesTheDigestAllowlist()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonAnchoredFindings(new AnchorSpec(
                    "plot", "Chapter zero finding", new[] { (0, "Chapter 0") }, Array.Empty<int>())),
                [2] = JsonAnchoredFindings(new AnchorSpec(
                    "plot", "Chapter one finding", new[] { (1, "Chapter 1") }, Array.Empty<int>())),
                [3] = EmptyFindings, // chapter 2 produces nothing, so 2 never reaches the digest
            }
        };

        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        Assert.NotNull(holder.SynthesisInstruction);
        // Exactly the orders the digest lines print (0 and 1) — NOT chapter 2, which no digest line mentions even
        // though it is a perfectly real chapter of the book.
        Assert.Contains("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 0, 1", holder.SynthesisInstruction!);
    }

    /// <summary>b7 (b3 residue): the CONTINUITY final-reduce digest printed a book-wide group finding's chapter
    /// column as literal "0" — telling the model a finding with NO anchor belongs to the FIRST chapter, since orders
    /// are 0-based. b3 killed that sentinel in the synthesis digest and missed this one, so the reduce pass was being
    /// handed a fabricated chapter-0 anchor to copy. It now prints the no-anchor token.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_ContinuityFinalReduceDigest_PrintsNoAnchorToken_NotChapterZero()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings, [2] = EmptyFindings, [3] = EmptyFindings,
            },
            // The tiny budget makes one skeleton GROUP per chapter (3 groups) → 3 group calls + 1 final reduce.
            ContinuityByCallIndex = new Dictionary<int, string?>
            {
                // Group 1 returns a BOOK-WIDE continuity finding (no chapterAnchors at all).
                [1] = JsonAnchoredFindings(new AnchorSpec(
                    "continuity", "A book-wide continuity concern with no chapter anchor",
                    Array.Empty<(int, string)>(), Array.Empty<int>())),
                [2] = EmptyFindings,
                [3] = EmptyFindings,
                [4] = EmptyFindings, // the final reduce itself returns nothing; we assert on its INPUT
            }
        };

        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        // 3 group calls + the final reduce.
        Assert.Equal(4, holder.ContinuityInstructions.Count);
        var finalReduceInput = holder.ContinuityInstructions[3];

        // The digest line for the book-wide finding prints the NO-ANCHOR token, never "0" (which is chapter one).
        Assert.Contains("continuity | - | A book-wide continuity concern", finalReduceInput);
        Assert.DoesNotContain("continuity | 0 | A book-wide continuity concern", finalReduceInput);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    // be-c02 (P1-1) — b7's VISIBILITY GATE MUST NOT VALIDATE THE MODEL AGAINST ITS OWN HALLUCINATION
    //
    // b7 made a window's anchors safe: an order the window never saw is dropped. But the two REDUCE passes
    // (synthesis, continuity-final) anchor from a DIGEST of what the windows found, and b7 derived their allowlist
    // AND their shown-set from the RAW, UNRESOLVED orders that digest printed. So the ONE thing b7 established as
    // untrustworthy — a window's anchor — was being LAUNDERED into the next pass's licence to anchor:
    //
    //   window 2 (shown ch 1) anchors ch 0  →  its OWN copy is dropped as UNSEEN (b7 works)
    //                                       →  but the digest printed "0"
    //                                       →  the synthesis prompt says "you may anchor ONLY to … 0 …"
    //                                       →  the synthesis anchors to 0
    //                                       →  the resolver ACCEPTS it (0 is real AND 0 is in the SYNTHESIS
    //                                          shown-set) → PERSISTED, mis-anchored to a chapter NO pass ever read.
    //
    // And a PHANTOM order (99) landed in the allowlist while the resolver dropped it — the prompt and the parser
    // saying different things, which is the incoherence b7's allowlist exists to remove.
    //
    // The fix filters every digest-derived order through the REAL chapter set ∩ the EMITTING finding's own
    // VisibleChapterOrders (DigestAnchorGate, single-sourced through ChapterAnchorResolver.TryPreviewAnchor) before
    // it can become a printed order, an allowlist entry or a shown-set member.
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>be-c02: a window's OUT-OF-WINDOW anchor to a REAL chapter never reaches the synthesis ALLOWLIST. The
    /// digest prints it as a book-wide finding ("-") — which is the truth, because the resolver is about to drop that
    /// anchor and persist the finding book-wide — while the CORRECTLY anchored finding beside it still puts its order
    /// in. Before the fix the allowlist read "0, 1" and told the model it could anchor to a chapter that only a
    /// hallucination had ever mentioned.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_WindowsUnseenRealAnchor_NeverReachesTheSynthesisAllowlist()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                // Window 2 is shown chapter 1 ONLY. It emits one CORRECT finding (anchored to the chapter it read)
                // and one that anchors chapter 0 — real, but a chapter it never saw.
                [2] = JsonAnchoredFindings(
                    new AnchorSpec("tone", "A finding window two anchored inside its own window",
                        new[] { (1, "Chapter 1") }, Array.Empty<int>()),
                    new AnchorSpec("plot", "A finding window two anchored outside its own window",
                        new[] { (0, "Chapter 0") }, Array.Empty<int>())),
                [3] = EmptyFindings,
            }
        };

        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        Assert.NotNull(holder.SynthesisInstruction);
        var instruction = holder.SynthesisInstruction!;

        // THE ALLOWLIST: exactly the one order a pass genuinely READ. Chapter 0 is a perfectly real chapter of this
        // book — and that is the point: it is excluded because the finding that named it was written by a pass that
        // was never shown it.
        Assert.Contains("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 1", instruction);
        Assert.DoesNotContain("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 0", instruction);

        // THE DIGEST: the mis-anchored finding keeps its LINE (dropping it would cost the reduce a real finding, and
        // its merge id) but prints the no-anchor token — the state the resolver is about to put it in.
        Assert.Contains("| - | A finding window two anchored outside its own window", instruction);
        Assert.DoesNotContain("| 0 | A finding window two anchored outside its own window", instruction);
        // ...while the correctly anchored finding still prints its order.
        Assert.Contains("| 1 | A finding window two anchored inside its own window", instruction);
    }

    /// <summary>be-c02 THE HARM ITSELF: the laundered order must not reach the synthesis SHOWN-SET either, or the
    /// resolver validates the synthesis against the window's hallucination and PERSISTS a finding anchored to a
    /// chapter no pass ever read. Here the synthesis anchors one finding to the laundered order 0 (DROPPED → the
    /// finding survives book-wide) and one to the genuinely-shown order 1 (KEPT → the gate has no false positives).
    /// Pre-fix, 0 was in the shown-set, TryResolveAnchor accepted it, and the row was written pointing at chapter
    /// 0.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_SynthesisAnchorToALaunderedOrder_IsDropped_VisibleOneIsKept()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                [2] = JsonAnchoredFindings(
                    new AnchorSpec("tone", "A finding window two anchored inside its own window",
                        new[] { (1, "Chapter 1") }, Array.Empty<int>()),
                    new AnchorSpec("plot", "A finding window two anchored outside its own window",
                        new[] { (0, "Chapter 0") }, Array.Empty<int>())),
                [3] = EmptyFindings,
            },
            SynthesisResponse = JsonAnchoredFindings(
                // The synthesis copies the order the digest laundered into its allowlist. It never read chapter 0
                // either — nobody did — so this anchor is a guess twice over.
                new AnchorSpec("theme", "A synthesis finding that copied the laundered chapter zero",
                    new[] { (0, "Chapter 0") }, new[] { 0 }),
                // ...and one anchored to the order that WAS genuinely read and printed. This must survive.
                new AnchorSpec("pacing", "A synthesis finding anchored to an order a window really read",
                    new[] { (1, "Chapter 1") }, Array.Empty<int>())),
        };

        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);
        var realChapter1 = await db.Chapters.AsNoTracking().SingleAsync(c => c.BookId == bookId && c.Order == 1);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();

        // THE BUG: this row used to be persisted anchored to chapter 0 — a real chapter, resolvable, navigable, and
        // WRONG. It is now book-wide, and its evidence citation of chapter 0 is gone with it.
        var laundered = Assert.Single(persisted, f => f.Dimension == "theme");
        Assert.Empty(DeserializeAnchors(laundered.ChapterAnchorsJson));
        Assert.Equal("[]", laundered.EvidenceJson);

        // THE FENCE: an anchor onto an order a pass really read still resolves, with its real chapter id. The gate
        // narrows the shown-set; it must not empty it.
        var grounded = Assert.Single(persisted, f => f.Dimension == "pacing");
        var anchor = Assert.Single(DeserializeAnchors(grounded.ChapterAnchorsJson));
        Assert.Equal(1, anchor.Order);
        Assert.Equal(realChapter1.Id, anchor.ChapterId);

        // And the window's own two findings are unchanged by b7 + be-c02: the correct one keeps chapter 1, the
        // out-of-window one is book-wide.
        Assert.Equal(1, Assert.Single(DeserializeAnchors(
            Assert.Single(persisted, f => f.Dimension == "tone").ChapterAnchorsJson)).Order);
        Assert.Empty(DeserializeAnchors(Assert.Single(persisted, f => f.Dimension == "plot").ChapterAnchorsJson));
    }

    /// <summary>be-c02 PHANTOM: an order that is not a chapter of this book AT ALL used to be printed in the digest
    /// and named in the allowlist ("you may anchor to … 99") while ChapterAnchorResolver dropped it on the way back
    /// in — the PROMPT and the PARSER disagreeing, which is exactly the incoherence b7's allowlist was written to
    /// eliminate. A phantom now reaches neither.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_PhantomOrder_NeverReachesTheSynthesisAllowlist()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                [2] = JsonAnchoredFindings(
                    new AnchorSpec("tone", "A finding anchored to the chapter window two actually read",
                        new[] { (1, "Chapter 1") }, Array.Empty<int>()),
                    // No such chapter, and no such title: unresolvable by order AND by title.
                    new AnchorSpec("plot", "A finding anchored to a chapter that does not exist",
                        new[] { (99, "A chapter that is not in this book") }, Array.Empty<int>())),
                [3] = EmptyFindings,
            }
        };

        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        Assert.NotNull(holder.SynthesisInstruction);
        var instruction = holder.SynthesisInstruction!;

        // The phantom appears NOWHERE in what the model is shown — not as a digest order, not in the allowlist.
        Assert.DoesNotContain("99", instruction);
        // The allowlist and the resolver now agree on exactly one order: the one a window really read.
        Assert.Contains("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 1", instruction);
        Assert.Contains("| - | A finding anchored to a chapter that does not exist", instruction);
    }

    /// <summary>be-c02, the CONTINUITY reduce: the same laundering ran through the second digest. A continuity GROUP
    /// sees only its slice of the skeleton, so an anchor onto a chapter outside that slice is a guess — and printing
    /// it handed the FINAL reduce a licence to anchor there. The final-reduce digest now prints only the orders the
    /// emitting group actually listed, and its allowlist says the same.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_ContinuityGroupsUnseenAnchor_NeverReachesTheFinalReduce()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings, [2] = EmptyFindings, [3] = EmptyFindings,
            },
            // One skeleton GROUP per chapter at the tiny budget → 3 group calls + 1 final reduce.
            ContinuityByCallIndex = new Dictionary<int, string?>
            {
                // Group 1's skeleton lists chapter 0 ONLY. It anchors 0 (correct) AND 2 (real, never listed to it).
                [1] = JsonAnchoredFindings(new AnchorSpec(
                    "continuity", "A cross chapter break the group could not have seen",
                    new[] { (0, "Chapter 0"), (2, "Chapter 2") }, Array.Empty<int>())),
                [2] = EmptyFindings,
                [3] = EmptyFindings,
                [4] = EmptyFindings, // the final reduce returns nothing; we assert on its INPUT
            }
        };

        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        Assert.Equal(4, holder.ContinuityInstructions.Count);
        var finalReduceInput = holder.ContinuityInstructions[3];

        // The digest keeps the order the group READ and drops the one it invented — it does not drop the LINE.
        Assert.Contains("continuity | 0 | A cross chapter break", finalReduceInput);
        Assert.DoesNotContain("continuity | 0,2 | A cross chapter break", finalReduceInput);

        // ...and the final reduce's allowlist is that same set, so it is never invited to anchor chapter 2.
        Assert.Contains("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 0", finalReduceInput);
        Assert.DoesNotContain("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 0, 2", finalReduceInput);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    // be-f03 (P2-9) — THE TWO REDUCE DIGESTS MUST AGREE ON A MULTI-ANCHOR FINDING
    //
    // Pre-fix, RenderDigestLines (the synthesis [WINDOW_FINDINGS] digest) narrowed a finding's GATED anchors down
    // to ONLY THE FIRST surviving order, while BuildContinuityFindingsDigest (the continuity final-reduce digest)
    // already printed every surviving order. A finding anchored to two chapters it genuinely read — say [0, 1] —
    // therefore put only "0" into the synthesis allowlist/shown-set. If the synthesis then re-emitted that SAME
    // finding with both anchors, chapter 1 was dropped at persist time as UNSEEN: a real, correctly-read chapter
    // link lost purely because the digest under-reported its own finding. be-f03 widens RenderDigestLines to print
    // every surviving order (comma-joined), matching BuildContinuityFindingsDigest exactly.
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>be-f03 (P2-9) THE CORE REGRESSION: a window finding anchored to TWO chapters it genuinely read (via
    /// the overlap mechanism, mirroring <see cref="BuildBookReviewAsync_Windowed_AnchorToOverlapChapter_IsKept"/>)
    /// puts BOTH surviving orders into the synthesis digest/allowlist/shown-set, so a synthesis finding that
    /// re-emits BOTH anchors keeps them both. Pre-fix only order 0 (<c>visibleOrders[0]</c>) reached the shown-set,
    /// so the synthesis's own anchor to order 1 was dropped as UNSEEN and the finding persisted with only ONE of
    /// its two real chapter links.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_MultiAnchorFinding_AllSurvivingOrdersReachTheSynthesisShownSet()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                // With overlap K=1, window 2's shown set is {0 (overlap), 1 (primary)} — both real, both genuinely
                // read. This finding anchors both.
                [2] = JsonAnchoredFindings(new AnchorSpec(
                    "tone", "A window finding anchored to both chapters it actually read",
                    new[] { (0, "Chapter 0"), (1, "Chapter 1") }, Array.Empty<int>())),
            },
            // The synthesis re-emits a DIFFERENT (new) finding anchored to the SAME two orders. If the shown-set
            // dropped order 1 (the pre-fix bug), this anchor would be resolved down to just chapter 0.
            SynthesisResponse = JsonAnchoredFindings(new AnchorSpec(
                "theme", "A synthesis finding that anchors both surviving orders",
                new[] { (0, "Chapter 0"), (1, "Chapter 1") }, Array.Empty<int>())),
        };

        using var provider = BuildWindowedProvider(out _, holder, overlapChapters: 1);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");
        Assert.True(result.Ready);

        Assert.NotNull(holder.SynthesisInstruction);
        var instruction = holder.SynthesisInstruction!;

        // THE DIGEST: the window finding's line prints BOTH surviving orders, comma-joined — not just the first.
        Assert.Contains("| 0,1 | A window finding anchored to both chapters it actually read", instruction);
        // THE ALLOWLIST: derived from the same (now-widened) shown-set, so it names both orders too.
        Assert.Contains("מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 0, 1", instruction);

        // THE HARM, ASSERTED DIRECTLY: the synthesis's own re-emitted finding keeps BOTH anchors, because order 1
        // really was in its shown-set.
        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        var synthesisFinding = Assert.Single(persisted, f => f.Dimension == "theme");
        var anchors = DeserializeAnchors(synthesisFinding.ChapterAnchorsJson);
        Assert.Equal(2, anchors.Count);
        Assert.Contains(anchors, a => a.Order == 0);
        Assert.Contains(anchors, a => a.Order == 1);
        Assert.DoesNotContain(anchors, a => a.ChapterId == Guid.Empty);
    }

    /// <summary>be-f03 (P2-9) PARITY: the SAME multi-anchor shape, fed through BOTH reduce digests in ONE build — a
    /// window finding for the synthesis digest, a continuity-group finding for the continuity final-reduce digest —
    /// prints the IDENTICAL comma-joined order text ("0,1") in both. Before this fix only the continuity digest did
    /// this; the synthesis digest narrowed to "0". Continuity groups respect the SAME overlap config as windows
    /// (<see cref="PlanContinuityReduce"/> reads <c>BookReviewWindowOverlapChapters</c> too), so the identical
    /// chapterCount/overlap setup splits BOTH into a 2-chapter group/window — mirroring
    /// <see cref="BuildBookReviewAsync_Windowed_AnchorToOverlapChapter_IsKept"/> on the window side and
    /// <see cref="BuildBookReviewAsync_Windowed_ContinuityGroupsUnseenAnchor_NeverReachesTheFinalReduce"/> on the
    /// continuity side.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_Windowed_SynthesisAndContinuityDigests_AgreeOnAMultiAnchorFinding()
    {
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                [2] = JsonAnchoredFindings(new AnchorSpec(
                    "tone", "A window finding anchored to both chapters it actually read",
                    new[] { (0, "Chapter 0"), (1, "Chapter 1") }, Array.Empty<int>())),
            },
            // With overlap K=1: group 1 = {chapter 0}, group 2 = {chapter 0 (overlap), chapter 1 (primary)}, plus a
            // final reduce — the SAME split as the window side above.
            ContinuityByCallIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings, // group 1: chapter 0 only
                [2] = JsonAnchoredFindings(new AnchorSpec(
                    "continuity", "A continuity finding anchored to both chapters its group actually read",
                    new[] { (0, "Chapter 0"), (1, "Chapter 1") }, Array.Empty<int>())),
                [3] = EmptyFindings, // the final reduce returns nothing; we assert on its INPUT
            },
        };

        using var provider = BuildWindowedProvider(out _, holder, overlapChapters: 1);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        Assert.NotNull(holder.SynthesisInstruction);
        Assert.Contains(
            "| 0,1 | A window finding anchored to both chapters it actually read", holder.SynthesisInstruction!);
        Assert.Contains(
            "מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 0, 1", holder.SynthesisInstruction!);

        Assert.Equal(3, holder.ContinuityInstructions.Count);
        var finalReduceInput = holder.ContinuityInstructions[2];
        Assert.Contains(
            "continuity | 0,1 | A continuity finding anchored to both chapters its group actually read", finalReduceInput);
        Assert.Contains(
            "מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: 0, 1", finalReduceInput);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    // b8 — THE SYNTHESIS MERGE MAP, END TO END (router → digest → merges → PASS 0 → collapse → persist)
    //
    // SynthesisMergeMapTests covers the pass's SEMANTICS on the real 18-row gold set. These four prove the
    // WIRING: that the digest really hands the model ids it can name, that the map it returns really reaches
    // UnionAndDedup, that the kill-switch really gates it — and, the one that matters most, that a merged
    // survivor keeps its DEDUP KEY, so a user's acknowledged Status survives the rebuild that merges it.
    //
    // The rationales are the REAL Daniel-triple rows of book A63A6E02: three copies of one observation across
    // primaries 14 / 13 / 13 that NO deterministic pass can close (0.455 across two different real chapters,
    // which b4b's MayFold fence refuses; 0.444 inside one bucket, under b4's 0.45 threshold), sitting beside a
    // SEVERITY-3 FACTUAL CONTRADICTION that scores 0.462 — HIGHER than the duplicate — against one of them.
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    private const string B8Contradiction = "קיימת סתירה עובדתית בין מצב הדמויות בפרקי המעקב (12-15) לבין פרקי העלילה המרכזיים; דמות 'דניאל' אינה מוזכרת בשום מקום אחר בספר, ואין קשר ברור בינה לבין תמר או אדם.";
    private const string B8Daniel14 = "המשך מצב חוסר השינה של דניאל מפרק 14 ל-15 יוצר רצף פיזי ורגשי ברור.";
    private const string B8Daniel13 = "המצוקה הפיזית של דניאל (חוסר שינה ועייפות) נשמרת בעקביות ויוצרת רצף ריאליסטי.";
    private const string B8Daniel13b = "המצב הפיזי של דניאל (עייפות קיצונית) נשמר בעקביות לאורך הסצנות.";

    // The 0.875 pair: ONE clause about science and poetics, filed on chapter 1 AND on chapter 15. It rides along in
    // the E2E as the be-c07 ANCHOR FENCE's case: it is a REAL duplicate, it is the highest-scoring one in the book,
    // and the model may NO LONGER merge it, because a group spanning two different real chapters is exactly the shape
    // of the false merge the b8 live gate measured (a ch14 tone finding absorbing a ch12 character finding, erasing
    // the דמויות dimension). It stays a visible duplicate — the cheap failure, chosen over the expensive one.
    private const string B8Science1 = "השילוב בין המדע לבין הפואטיקה יוצר אווירה ייחודית של קדושה ומתח.";
    private const string B8Science15 = "השילוב בין המדע לבין הפואטיקה יוצר אווירה של קדושה ומתח שמתכתבת עם נושאי הריקנות והעצב.";

    // The memory pair (theme, BOTH on chapter 9), score 0.375 — UNDER b4's 0.45 threshold, and no threshold can be
    // lowered to reach it (0.44 would also catch the 0.462 DISTINCT contradiction pair). Same chapter scope, so the
    // anchor fence allows it: this is what the merge map still buys that no deterministic pass can.
    private const string B8Mem9 = "המעבר מהאני האישי לזיכרון קולקטיבי של הקהילה מעניק לספר סיומת של התאוששות ושלום.";
    private const string B8Mem9b = "המעבר מהאני לקהילתי בפרק העשירי מחזק את תחושת השלום והתאוששות.";

    /// <summary>The ONE window's response: the sev3 contradiction (W1), the three Daniel copies (W2/W3/W4), the two
    /// science-and-poetics copies (W5/W6) and the two memory copies (W7/W8), in the accumulation order the digest
    /// numbers them. Nothing here is invented — every row is verbatim from book A63A6E02.</summary>
    private static string B8WindowFindings() => JsonAnchoredFindings(
        new AnchorSpec("continuity", B8Contradiction, new[] { (12, "Chapter 12"), (13, "Chapter 13"), (14, "Chapter 14"), (15, "Chapter 15") }, Array.Empty<int>(), 3),
        new AnchorSpec("continuity", B8Daniel14, new[] { (14, "Chapter 14"), (15, "Chapter 15") }, Array.Empty<int>(), 1),
        new AnchorSpec("continuity", B8Daniel13, new[] { (13, "Chapter 13"), (14, "Chapter 14"), (15, "Chapter 15") }, Array.Empty<int>(), 1),
        new AnchorSpec("continuity", B8Daniel13b, new[] { (13, "Chapter 13") }, Array.Empty<int>(), 1),
        new AnchorSpec("tone", B8Science1, new[] { (1, "Chapter 1") }, Array.Empty<int>(), 1),
        new AnchorSpec("tone", B8Science15, new[] { (15, "Chapter 15") }, Array.Empty<int>(), 1),
        new AnchorSpec("theme", B8Mem9, new[] { (9, "Chapter 9") }, Array.Empty<int>(), 1),
        new AnchorSpec("theme", B8Mem9b, new[] { (9, "Chapter 9") }, Array.Empty<int>(), 1));

    /// <summary>A synthesis response carrying NO new findings and TWO merge groups — the shape the reduce could
    /// never express before b8, because its only output channel was a findings array the build APPENDS.
    ///
    /// Both groups are ANCHOR-COMPATIBLE (be-c07): W3+W4 share primary chapter 13, W7+W8 share chapter 9. The keep of
    /// the first is deliberately W4 (the copy anchored to ONLY chapter 13), so the merge really does UNION chapters 14
    /// and 15 onto it — the shape that proves the union is APPEND-only and that anchors[0] never moves.</summary>
    private static string B8SynthesisMerges() => JsonSerializer.Serialize(new
    {
        findings = Array.Empty<object>(),
        merges = new[]
        {
            new { ids = new[] { "W3", "W4" }, keep = "W4" }, // the Daniel pair that shares primary chapter 13
            new { ids = new[] { "W7", "W8" }, keep = "W7" }, // the memory pair, both on chapter 9
        },
    });

    private static WindowedResponseHolder B8Holder() => new()
    {
        ByWindowIndex = new Dictionary<int, string?> { [1] = B8WindowFindings() },
        SynthesisResponse = B8SynthesisMerges(),
    };

    [Fact]
    public async Task BuildBookReviewAsync_SynthesisMergeMap_CollapsesTheSameChapterDuplicates_AndTheSev3ContradictionSurvives()
    {
        // THE ACCEPTANCE CASE, through the real build. The synthesis names two ANCHOR-COMPATIBLE groups (W3+W4 on
        // chapter 13; W7+W8 on chapter 9) and both apply. BOTH merges are BELOW b4's 0.45 threshold (0.444 and
        // 0.375) and unreachable by lowering it, so this is the merge map earning exactly what it was built for.
        var holder = B8Holder();
        using var provider = BuildWindowedProvider(
            out _, holder, bookContextTokenBudget: 100_000, mergeMapEnabled: true);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();

        // 8 accumulated → 6 persisted. Each survivor is the copy the model named, VERBATIM — no prose was fabricated.
        Assert.Equal(6, persisted.Count);
        Assert.DoesNotContain(persisted, f => f.Rationale == B8Daniel13);  // absorbed into W4
        Assert.DoesNotContain(persisted, f => f.Rationale == B8Mem9b);     // absorbed into W7
        Assert.Single(persisted, f => f.Rationale == B8Mem9);

        // THE UNION THAT ACTUALLY ADDS CHAPTERS — W4 anchored ONLY chapter 13 and absorbed a copy anchored
        // [13,14,15], so the merged row must carry all three. And THE INVARIANT THAT MATTERS MOST: the union is
        // APPEND-only, so the survivor's OWN first anchor (13) is still at index 0. anchors[0].Order is the dedup
        // key's primary-order input AND what ComparisonOrderOf hands b4b's persisted re-wording tier; if a merge
        // could reorder it, a later reworded re-emission would compare against the WRONG chapter, MayFold would
        // refuse to re-match the row, and the fresh copy would insert beside the user's acknowledged one.
        var daniel = Assert.Single(persisted, f => f.Rationale == B8Daniel13b);
        Assert.Equal(new[] { 13, 14, 15 }, AnchorOrdersOf(daniel));
        Assert.Equal(BookFinding.ComputeDedupKey("continuity", 13, B8Daniel13b), daniel.DedupKey); // key did NOT move

        var danielAnchors = JsonSerializer.Deserialize<List<FindingChapterAnchor>>(daniel.ChapterAnchorsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.All(danielAnchors, a => Assert.NotEqual(Guid.Empty, a.ChapterId)); // still navigable after the union

        // THE be-c07 ANCHOR FENCE, THROUGH THE REAL BUILD: the ch14 Daniel copy is NOT swept into the ch13 group, and
        // the 0.875 science/poetics duplicate on ch1 + ch15 is NOT merged either — the model was not even given the
        // chance to ask, and if it had, the fence would refuse. Two visible duplicates; zero deleted findings.
        Assert.Single(persisted, f => f.Rationale == B8Daniel14);
        Assert.Single(persisted, f => f.Rationale == B8Science1);
        Assert.Single(persisted, f => f.Rationale == B8Science15);
        Assert.Equal(new[] { 15 }, AnchorOrdersOf(Assert.Single(persisted, f => f.Rationale == B8Science15)));

        // THE ORIGINAL FENCE, ASSERTED HARD: the severity-3 factual contradiction was never named by the model, so it
        // was NEVER TOUCHED — even though it scores 0.462 against a copy in the merged group, i.e. HIGHER than the
        // 0.444 duplicate the merge closed. This finding is the most valuable one in the book.
        var contradiction = Assert.Single(persisted, f => f.Rationale == B8Contradiction);
        Assert.Equal(3, contradiction.Severity);
        Assert.Equal("continuity", contradiction.Dimension);
        Assert.Equal(new[] { 12, 13, 14, 15 }, AnchorOrdersOf(contradiction));
    }

    [Fact]
    public async Task BuildBookReviewAsync_SynthesisMergeMap_AGroupSpanningTwoRealChapters_IsRefusedByTheAnchorFence()
    {
        // be-c07 / P2-4, END TO END, IN THE STATE THAT CAUSED REAL HARM. The model asks to merge the ch1 and ch15
        // science/poetics copies — a group whose members sit on two different real chapters, which is the SHAPE of
        // the false merge the b8 live gate measured destroying the דמויות dimension. With the switch ON (the worst
        // case), the fence refuses it and BOTH findings reach the user.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?> { [1] = B8WindowFindings() },
            SynthesisResponse = JsonSerializer.Serialize(new
            {
                findings = Array.Empty<object>(),
                merges = new[] { new { ids = new[] { "W5", "W6" }, keep = "W6" } },
            }),
        };
        var log = new CapturingLoggerProvider();
        using var provider = BuildWindowedProvider(
            out _, holder, log, bookContextTokenBudget: 100_000, mergeMapEnabled: true);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();

        // NOTHING was merged: all 8 window findings persist. The named-for-deletion finding is STILL HERE.
        Assert.Equal(8, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == B8Science1);
        Assert.Single(persisted, f => f.Rationale == B8Science15);
        // ...and the survivor the model chose was not half-mutated: no anchor was unioned onto it.
        Assert.Equal(new[] { 15 }, AnchorOrdersOf(Assert.Single(persisted, f => f.Rationale == B8Science15)));

        var coverage = Assert.Single(log.Entries, e => e.Message.Contains(MergeCoverageLine));
        Assert.Contains("applied 0", coverage.Message);
        Assert.Contains("rejected 1 (anchors-span-different-chapters=1)", coverage.Message);
    }

    private static int[] AnchorOrdersOf(BookFinding f) =>
        JsonSerializer.Deserialize<List<FindingChapterAnchor>>(f.ChapterAnchorsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .Select(a => a.Order).ToArray();

    [Fact]
    public async Task BuildBookReviewAsync_SynthesisMergeMap_AnAcknowledgedSurvivor_KeepsItsStatus_AcrossTheMergingRebuild()
    {
        // THE MIGRATION HAZARD, TESTED WHERE IT BITES. PersistPreservingStatusAsync re-matches a cached row by its
        // DEDUP KEY, whose inputs are (dimension, FIRST ANCHOR ORDER, rationale). The merge UNIONS anchors onto the
        // survivor — so if the union had reordered index 0, the key would move, the row would match nothing on the
        // next build, and the user's acknowledgement would be silently lost and the finding duplicated. The union is
        // APPEND-ONLY precisely so it cannot. This test is the proof, end to end.
        var holder = B8Holder();
        using var provider = BuildWindowedProvider(
            out _, holder, bookContextTokenBudget: 100_000, mergeMapEnabled: true);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);
        var svc = provider.GetRequiredService<BookReviewService>();

        // Build 1: the merges fire and the user ACKNOWLEDGES the survivor whose anchors the merge actually CHANGED
        // (the Daniel row, which anchored only chapter 13 and gained 14 and 15 from the copy it absorbed). That is
        // the row where a moved anchors[0] would do its damage, so it is the row the acknowledgement must be pinned to.
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);
        var survivor = await db.BookFindings.SingleAsync(f => f.BookId == bookId && f.Rationale == B8Daniel13b);
        var keyAfterMerge = survivor.DedupKey;
        Assert.Equal(new[] { 13, 14, 15 }, AnchorOrdersOf(survivor)); // the union happened
        survivor.Status = "acknowledged";
        await db.SaveChangesAsync();

        // Build 2: the model emits the same copies and the same merges. Bump the briefs so the build is not a
        // no-op (the same stale-vs-briefs path a real rebuild takes).
        await TouchSummaryBaselineAsync(db, bookId);
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(6, persisted.Count); // no accumulation, no duplicate pair

        var reacquired = Assert.Single(persisted, f => f.Rationale == B8Daniel13b);
        Assert.Equal("acknowledged", reacquired.Status);  // the user's decision SURVIVED the merging rebuild
        Assert.Equal(survivor.Id, reacquired.Id);         // it is the SAME row, re-matched, not a fresh insert
        Assert.Equal(keyAfterMerge, reacquired.DedupKey); // because the key never moved...
        // ...it is still hashed on chapter 13, the survivor's OWN first anchor, not on a chapter the union added.
        Assert.Equal(BookFinding.ComputeDedupKey("continuity", 13, B8Daniel13b), reacquired.DedupKey);
        Assert.Equal(new[] { 13, 14, 15 }, AnchorOrdersOf(reacquired)); // and the union is stable across rebuilds

        // The other merged survivor kept its Status too (open), and nothing was resurrected.
        Assert.Single(persisted, f => f.Rationale == B8Mem9);
        Assert.DoesNotContain(persisted, f => f.Rationale == B8Daniel13 || f.Rationale == B8Mem9b);
    }

    [Fact]
    public async Task BuildBookReviewAsync_SynthesisMergeMap_WithTheKillSwitchOff_AppliesNothing_ButLogsWhatItWouldHaveDone()
    {
        // THE SHIPPED DEFAULT. The switch is OFF, so NOTHING IS APPLIED: all three Daniel copies persist (b4 cannot
        // close them and b4b's fence refuses to), both science copies persist, and the coverage log still states what
        // the model proposed and what flipping the switch would have done. A staged rollout whose OFF state measures
        // nothing is a staged rollout that can never be flipped with evidence.
        //
        // be-c06: this test used to assert "pre-b8 behaviour, byte for byte". THAT WAS FALSE. What is pinned here is
        // the APPLY guarantee — no row deleted, no anchor unioned, no severity lifted — which is exact. The MODEL'S
        // INPUT is a different matter: the OFF build still sends the b8 prompt, which is pinned by
        // ..._WithTheKillSwitchOff_TheSynthesisPromptStillCarriesTheMergeContract below.
        var logCapture = new CapturingLoggerProvider();
        var holder = B8Holder();
        using var provider = BuildWindowedProvider(
            out _, holder, logCapture, bookContextTokenBudget: 100_000, mergeMapEnabled: false);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        Assert.Equal(8, persisted.Count); // every finding the model named for deletion is STILL HERE
        Assert.Single(persisted, f => f.Rationale == B8Daniel14);
        Assert.Single(persisted, f => f.Rationale == B8Daniel13);
        Assert.Single(persisted, f => f.Rationale == B8Daniel13b);
        Assert.Single(persisted, f => f.Rationale == B8Science1);
        Assert.Single(persisted, f => f.Rationale == B8Science15);
        Assert.Single(persisted, f => f.Rationale == B8Mem9);
        Assert.Single(persisted, f => f.Rationale == B8Mem9b);
        Assert.Single(persisted, f => f.Rationale == B8Contradiction);

        // ...and the survivor the model chose was not silently mutated either: the union is part of APPLY, so the
        // Daniel row still carries ONLY its own chapter 13, not the chapters the absorbed copy would have added.
        Assert.Equal(new[] { 13 }, AnchorOrdersOf(Assert.Single(persisted, f => f.Rationale == B8Daniel13b)));

        var coverage = Assert.Single(logCapture.Entries, e => e.Message.Contains(MergeCoverageLine));
        Assert.Contains("switch OFF", coverage.Message);
        Assert.Contains("proposed 2 merge group(s)", coverage.Message);
        Assert.Contains("would have merged 2 finding(s) away", coverage.Message);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    // be-c06 — WHAT "OFF" ACTUALLY IS (P1-4)
    //
    // Three places claimed "switch OFF = byte-identical to pre-b8", including the MEASURE-MODE LOG an operator reads
    // to decide whether to flip a model-driven DELETE on. It was FALSE: b8 changed the model's INPUT unconditionally
    // (the W# id column, the 140 -> 260 rationale cap, the merge contract in both synthesis prompts), and only the
    // APPLY mutation is gated. So OFF is a THIRD behavior — not b8, not pre-b8.
    //
    // The prompt is UNGATED ON PURPOSE (the alternative was rejected): gate it and the OFF state measures nothing,
    // and the little it did measure would have been measured against an input the ON build never sees. So the claim
    // was rewritten to match the code, and these tests PIN the behavior that is now described, in both directions:
    //   (1) the OFF prompt REALLY DOES still carry the merge contract (so no future reader "restores" a claim the
    //       code does not implement), and
    //   (2) the model is NOT muzzled with nowhere to speak: with the switch OFF its merge answer is still read,
    //       validated and NAMED in the log. What is withheld is the ACT, not the voice.
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildBookReviewAsync_WithTheKillSwitchOff_TheSynthesisPromptStillCarriesTheMergeContract()
    {
        // THE FALSE CLAIM, TURNED INTO AN ASSERTION. With the switch OFF the model does NOT see the pre-b8 prompt.
        // Every b8 input change below ships in the DEFAULT state, and each one can change what the model answers.
        var holder = B8Holder();
        using var provider = BuildWindowedProvider(
            out _, holder, bookContextTokenBudget: 100_000, mergeMapEnabled: false);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var instruction = Assert.IsType<string>(holder.SynthesisInstruction);

        // (1) THE ID COLUMN. Pre-b8 the digest line was `dimension | order | rationale` — the model had no way to
        //     NAME an accumulated finding. The OFF build prints the ids anyway. be-f03: the contradiction's chapter
        //     column lists ALL four of its surviving anchors (12,13,14,15), not just the first.
        Assert.Contains($"W1 | continuity | 12,13,14,15 | {B8Contradiction}", instruction);
        Assert.Contains($"W6 | tone | 15 | {B8Science15}", instruction);

        // (2) THE RAISED CAP. The sev-3 contradiction is 163 chars; the pre-b8 cap was 140, so it would have arrived
        //     beheaded. It arrives WHOLE in the OFF build, ending on its own last word.
        Assert.True(B8Contradiction.Length > 140);
        Assert.Contains("ואין קשר ברור בינה לבין תמר או אדם.", instruction);

        // (3) THE MERGE CONTRACT — the schema, the rules, and the id legend. The model is asked for `merges` in a
        //     build that will THROW ITS ANSWER AWAY. That is measure mode, and it is why the OFF coverage log is a
        //     valid forecast of the ON build: same prompt, same response, only the apply step differs.
        Assert.Contains("\"merges\"", instruction);
        Assert.Contains("\"keep\"", instruction);
        Assert.Contains("כללי המיזוג (השדה \"merges\"):", instruction);
        Assert.Contains("המזהה הוא קוד קצר בצורת W1, W2, W3", instruction);

        // (4) THE MUZZLE — the sharpest edge of the OFF state, and the reason it is a THIRD behavior rather than the
        //     old one. Pre-b8 the model was told to reconcile duplicates and its only channel was `findings`, so it
        //     complied by emitting a THIRD finding that the build APPENDED beside the two originals: the measured
        //     mechanism of the duplicate bug. b8 forbids that, unconditionally. It is the half of b8 we want in the
        //     shipped state, so it is NOT gated — see SynthesisMergeMap's KILL-SWITCH note.
        Assert.Contains("אל תכתוב ממצא חדש כדי לתאר מיזוג; המיזוג נעשה אך ורק דרך \"merges\".", instruction);
    }

    [Fact]
    public async Task BuildBookReviewAsync_WithTheKillSwitchOff_TheMergeAnswerIsStillRead_ValidatedAndNamedInTheLog()
    {
        // THE RECONCILIATION CHANNEL IS NOT DEAD IN THE OFF STATE — it is READ-ONLY. The model is told to express a
        // merge only through `merges`, and in the OFF build that map is still parsed, validated against the digest
        // ids, resolved to concrete findings and NAMED in the coverage log. What the switch withholds is the ACT.
        //
        // Without this the operator's evidence would be a bare count, and "2 groups proposed" cannot be checked
        // against the digest by a human — "W3+W4->W4" can. That check is the entire decision procedure for
        // flipping this switch, and the live gate is exactly where it caught gemma4:12b merging two unrelated
        // findings and erasing a whole dimension from the score panel.
        var logCapture = new CapturingLoggerProvider();
        var holder = B8Holder();
        using var provider = BuildWindowedProvider(
            out _, holder, logCapture, bookContextTokenBudget: 100_000, mergeMapEnabled: false);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var coverage = Assert.Single(logCapture.Entries, e => e.Message.Contains(MergeCoverageLine));

        // The model SPOKE, and it was heard: both groups resolved, and the log says which findings each one would
        // have deleted and which it would have kept. (be-c07: the forecast is FENCED — what it reports as "would
        // have merged" is what a flipped build would ACTUALLY merge, not merely what the model asked for.)
        Assert.Contains("W3+W4->W4", coverage.Message);
        Assert.Contains("W7+W8->W7", coverage.Message);
        Assert.Contains("2 valid", coverage.Message);
        Assert.Contains("would have merged 2 finding(s) away", coverage.Message);

        // AND THE LOG NO LONGER LIES TO THE OPERATOR. It used to end "the findings are exactly what they would be
        // without the merge channel" — the load-bearing sentence in the decision to ship b8 at all, and false: the
        // prompt is not reverted by the switch, so an OFF build's own findings need not match a pre-b8 build's.
        Assert.DoesNotContain("exactly what they would be without the merge channel", coverage.Message);
        Assert.Contains("NOT the pre-b8 build", coverage.Message);
        Assert.Contains("gates the APPLY step, not the prompt", coverage.Message);

        // ...while the APPLY guarantee it DOES make is exact, and stated as such: all EIGHT window findings persist,
        // including the two the model asked to have deleted.
        Assert.Contains("no finding was deleted, no anchor unioned, no severity lifted", coverage.Message);
        Assert.Equal(8, await db.BookFindings.CountAsync(f => f.BookId == bookId));
    }

    [Fact]
    public void BookReviewSynthesisPrompt_CarriesTheMergeContract_InBothLanguages_AndTakesNoKillSwitch()
    {
        // THE STRUCTURAL PIN behind the two tests above, and behind every comment that now says "the prompt is not
        // gated": PromptFactory.BuildBookReviewSynthesisPrompt takes a LANGUAGE and nothing else. There is no switch
        // to pass it, so the merge contract CANNOT be conditional on Ai:BookReview:SynthesisMergeMap. If someone ever
        // gates the prompt, this test goes red and the claims in SynthesisMergeMap / AiOptions / BookReviewService
        // must be rewritten in the same commit — which is the whole point of asserting a claim instead of writing it.
        var method = typeof(PromptFactory).GetMethod(nameof(PromptFactory.BuildBookReviewSynthesisPrompt))!;
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(string), parameter.ParameterType);

        var factory = new PromptFactory();

        // i18n parity: the contract, the "keep"-inside-the-group rule and the MUZZLE exist in BOTH prompts, so the
        // OFF state means the same thing for a Hebrew book and an English one.
        var he = factory.BuildBookReviewSynthesisPrompt("he");
        Assert.Contains("\"merges\"", he);
        Assert.Contains("כללי המיזוג (השדה \"merges\"):", he);
        Assert.Contains("אל תכתוב ממצא חדש כדי לתאר מיזוג; המיזוג נעשה אך ורק דרך \"merges\".", he);

        var en = factory.BuildBookReviewSynthesisPrompt("en");
        Assert.Contains("\"merges\"", en);
        Assert.Contains("Merge rules (the \"merges\" field):", en);
        Assert.Contains("Do NOT write a new finding to describe a merge; a merge is expressed ONLY through \"merges\".", en);
        Assert.Contains("\"keep\" MUST be one of that group's own \"ids\".", en);
    }

    [Fact]
    public void RunCombinedCallAsync_WindowParameter_IsRequiredAndNonNullable()
    {
        // NIT-7. `window` used to default to null (WindowFrame?), with a dead ternary branch selecting
        // PromptFactory.BuildBookReviewCombinedPrompt (the whole-book, non-windowed prompt) for the null case.
        // Confirmed unreachable in production: RunCombinedCallAsync has exactly ONE call site, inside the
        // windowed MAP loop, and it always constructs and passes a WindowFrame. Now `window` is REQUIRED and
        // NON-NULLABLE, so the dead branch cannot silently come back via a "helpful" default. Pinned at the
        // signature level (reflection, since the method is private) rather than by behavior, because there is no
        // observable behavior difference to assert against — the whole point is that the removed branch was
        // never reachable to begin with.
        var method = typeof(BookReviewService).GetMethod(
            "RunCombinedCallAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var windowParam = method!.GetParameters().Single(p => p.Name == "window");
        Assert.False(windowParam.HasDefaultValue, "window must be a required parameter, not optional");
        Assert.Null(Nullable.GetUnderlyingType(windowParam.ParameterType));
    }

    [Fact]
    public async Task BuildBookReviewAsync_SynthesisDigest_PrintsABuildLocalIdPerLine_SoTheModelCanNameAFinding()
    {
        // THE CHANNEL'S PRECONDITION. Before b8 the digest printed `dimension | order | rationale` and nothing else,
        // so the model had no way to REFER to an accumulated finding: asked to "merge duplicates", the only thing it
        // could physically do was emit a third finding, which the build then APPENDED to the two it meant to replace.
        // The id column is what makes a merge expressible at all.
        var holder = B8Holder();
        using var provider = BuildWindowedProvider(
            out _, holder, bookContextTokenBudget: 100_000, mergeMapEnabled: true);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var digest = Assert.IsType<string>(holder.SynthesisInstruction);
        Assert.Contains("[WINDOW_FINDINGS]", digest);
        // be-f03 (P2-9): the chapter column lists EVERY surviving anchor, comma-joined — not just the first — so
        // W1/W2/W3 (each anchored to more than one chapter, all within the single window's shown set) print their
        // full order list.
        Assert.Contains($"W1 | continuity | 12,13,14,15 | {B8Contradiction}", digest);
        Assert.Contains($"W2 | continuity | 14,15 | {B8Daniel14}", digest);
        Assert.Contains($"W3 | continuity | 13,14,15 | {B8Daniel13}", digest);
        Assert.Contains($"W4 | continuity | 13 | {B8Daniel13b}", digest);
        Assert.Contains($"W5 | tone | 1 | {B8Science1}", digest);
        Assert.Contains($"W6 | tone | 15 | {B8Science15}", digest);

        // AND THE RAISED CAP. The sev3 contradiction is 163 chars — the ONLY rationale in the real 18-row corpus
        // that the old 140-char cap truncated. It truncated exactly the tail that DISTINGUISHES it from the sev1
        // praise it is 0.462 similar to, in the input to the very pass that now decides whether to merge them. It
        // must now arrive WHOLE, ending on its own last word.
        Assert.True(B8Contradiction.Length > 140);
        Assert.Contains("ואין קשר ברור בינה לבין תמר או אדם.", digest);

        // The merge contract is described to the model in the same instruction it must answer in.
        Assert.Contains("\"merges\"", digest);
        Assert.Contains("\"keep\"", digest);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    // be-c05 — A MALFORMED `merges` MUST NEVER TAKE THE FINDINGS DOWN WITH IT (P1-5 + P2-8)
    //
    // b8 added an OPTIONAL, model-supplied `merges` key to the response schema. Every BookReview pass parsed its
    // response with ONE strict Deserialize<BookReviewResult>, so a wrong SHAPE in that optional side-channel threw
    // JsonException on the WHOLE document and the outer catch discarded the ENTIRE synthesis — including the pass's
    // OWN book-level findings, the holistic observations no window can produce. And the kill-switch could not save
    // them, because THE PROMPT ASKS FOR `merges` WHETHER THE SWITCH IS ON OR OFF.
    //
    // Every test below therefore asserts the SAME floor: the synthesis's own arc finding SURVIVES. What varies is how
    // much of the merge map survives with it — a lexically-repairable shape is recovered and still faces
    // SynthesisMergeMap's full all-or-nothing validation; an uninterpretable one degrades to ZERO merge groups. Never
    // to a lost finding.
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The synthesis's OWN book-level finding — an arc observation no single window could make. This is the
    /// thing be-c05 exists to protect: a formatting slip in `merges` used to kill it.</summary>
    private const string B8Arc = "הקשת הרגשית של הספר נשברת בשליש האחרון, והמעבר בין החלקים אינו מתוּוך.";

    /// <summary>The `findings` VALUE (a bare JSON array) carrying that one arc finding, anchored to chapter 12 — an
    /// order the digest really prints, so b7's visibility gate keeps the anchor and the finding persists.</summary>
    private static string B8ArcFindingsValue() => JsonSerializer.Serialize(new[]
    {
        new
        {
            dimension = "theme",
            verdict = "improve",
            severity = 2,
            rationale = B8Arc,
            chapterAnchors = new[] { new { order = 12, title = "Chapter 12" } },
            evidence = Array.Empty<object>(),
            suggestedAction = (string?)null,
        }
    });

    /// <summary>Assembles a synthesis response from RAW JSON VALUES, so a test can hand the build the exact shapes a
    /// real model emits — an object where the array belongs, ids as one comma-joined string, ids as numbers, an
    /// explicit null. A typed helper cannot express any of them, which is precisely why the bug shipped.</summary>
    private static string SynthesisResponseRaw(string findingsValue, string mergesValue) =>
        "{ \"findings\": " + findingsValue + ", \"merges\": " + mergesValue + " }";

    /// <summary>The two WELL-FORMED merge groups of <see cref="B8SynthesisMerges"/>, as a raw value: the chapter-13
    /// Daniel pair (W3+W4 → W4) and the chapter-9 memory pair (W7+W8 → W7). Both are ANCHOR-COMPATIBLE, so what these
    /// tests measure is the PARSER (be-c05) and not be-c07's fence — a malformed payload must be recovered to exactly
    /// the groups a well-formed one would produce.</summary>
    private const string B8ValidMergesValue =
        """[ { "ids": ["W3","W4"], "keep": "W4" }, { "ids": ["W7","W8"], "keep": "W7" } ]""";

    private const string MergesCoercionWarning = "did not match the contract";

    /// <summary>Identifies the merge map's ONE per-build COVERAGE line (the proposed → applied/would-apply → rejected
    /// funnel), in BOTH switch states. Deliberately NOT "synthesis merge map": that substring also matches the
    /// per-merge KEPT/DELETED audit lines and the Debug digest line, so a test asserting on the funnel would be
    /// matching whichever line happened to come first.</summary>
    private const string MergeCoverageLine = "synthesis proposed";

    /// <summary>Runs one build of the 17-chapter b8 book with a RAW synthesis response, and returns the persisted
    /// findings plus the captured log.</summary>
    private static async Task<(List<BookFinding> Persisted, CapturingLoggerProvider Log)>
        BuildWithRawSynthesisAsync(string synthesisResponse, bool mergeMapEnabled)
    {
        var log = new CapturingLoggerProvider();
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?> { [1] = B8WindowFindings() },
            SynthesisResponse = synthesisResponse,
        };
        using var provider = BuildWindowedProvider(
            out _, holder, log, bookContextTokenBudget: 100_000, mergeMapEnabled: mergeMapEnabled);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 17);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var persisted = await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync();
        return (persisted, log);
    }

    /// <summary>THE CONTROL. The same raw-JSON harness with a WELL-FORMED `merges` reproduces b8's behaviour exactly:
    /// both groups resolve, both apply, three window findings survive — and the synthesis's own arc finding is added
    /// beside them. If this did not hold, every malformed-shape test below would be measuring the harness rather than
    /// the fix.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_SynthesisMergeMap_AWellFormedPayload_StillResolvesAndApplies()
    {
        var (persisted, log) = await BuildWithRawSynthesisAsync(
            SynthesisResponseRaw(B8ArcFindingsValue(), B8ValidMergesValue), mergeMapEnabled: true);

        // 8 accumulated window findings → 6 survivors (both merges applied), + the synthesis's own arc finding.
        Assert.Equal(7, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == B8Arc);
        Assert.Single(persisted, f => f.Rationale == B8Contradiction);
        Assert.Single(persisted, f => f.Rationale == B8Daniel13b);
        Assert.Single(persisted, f => f.Rationale == B8Mem9);
        Assert.DoesNotContain(persisted, f => f.Rationale == B8Daniel13 || f.Rationale == B8Mem9b);

        // A payload that matches the contract must NOT be reported as a coercion — a guard that cries wolf on the
        // happy path is a guard nobody reads.
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(MergesCoercionWarning));
        var coverage = Assert.Single(log.Entries, e => e.Message.Contains(MergeCoverageLine));
        Assert.Contains("applied 2", coverage.Message);
    }

    /// <summary>P1-5, THE NATURAL MISTAKE: `"merges": { … }` — an OBJECT where the array belongs. The prompt's schema
    /// block shows exactly ONE example group, so this is the shape a model reaches for first. It used to throw on the
    /// whole document and destroy the synthesis. Now the object is a lexically unambiguous ONE-group array: the arc
    /// finding lives, the group is recovered, and it still had to pass every one of Resolve's fences to apply.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_MalformedMerges_AnObjectInsteadOfAnArray_KeepsTheFindings_AndRecoversTheGroup()
    {
        var (persisted, log) = await BuildWithRawSynthesisAsync(
            SynthesisResponseRaw(
                B8ArcFindingsValue(),
                """{ "ids": ["W3","W4"], "keep": "W4" }"""),
            mergeMapEnabled: true);

        // THE FLOOR: the synthesis's own book-level finding SURVIVED a malformed merge map.
        Assert.Single(persisted, f => f.Rationale == B8Arc);

        // And the one group the model really did propose was honoured: the chapter-13 Daniel pair collapsed onto W4.
        Assert.Single(persisted, f => f.Rationale == B8Daniel13b);
        Assert.DoesNotContain(persisted, f => f.Rationale == B8Daniel13);
        // The science/poetics pair was in NO group here, so both copies stand (b4b's fence refuses to merge them,
        // and be-c07's would too).
        Assert.Single(persisted, f => f.Rationale == B8Science1);
        Assert.Single(persisted, f => f.Rationale == B8Science15);
        Assert.Single(persisted, f => f.Rationale == B8Contradiction);
        Assert.Equal(8, persisted.Count);

        // THE LOG HAS TEETH: a payload we had to repair is never repaired silently.
        var warning = Assert.Single(log.Entries, e => e.Message.Contains(MergesCoercionWarning));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("merges-was-an-object-not-an-array=1", warning.Message);
    }

    /// <summary>P1-5: `"ids": "W2,W3,W4"` — the ids as one delimited STRING. Splitting it is a purely LEXICAL repair:
    /// every token still has to be an id THIS build's digest printed, and the group still faces Resolve's
    /// all-or-nothing validation, so nothing is trusted that was not trusted before.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_MalformedMerges_IdsAsACommaJoinedString_KeepsTheFindings_AndRecoversTheGroups()
    {
        var (persisted, log) = await BuildWithRawSynthesisAsync(
            SynthesisResponseRaw(
                B8ArcFindingsValue(),
                """[ { "ids": "W3,W4", "keep": "W4" }, { "ids": "W7,W8", "keep": "W7" } ]"""),
            mergeMapEnabled: true);

        Assert.Single(persisted, f => f.Rationale == B8Arc); // the floor
        Assert.Equal(7, persisted.Count);                    // ...and both groups still applied, as if well-formed
        Assert.Single(persisted, f => f.Rationale == B8Daniel13b);
        Assert.Single(persisted, f => f.Rationale == B8Mem9);
        Assert.DoesNotContain(persisted, f => f.Rationale == B8Daniel13 || f.Rationale == B8Mem9b);

        var warning = Assert.Single(log.Entries, e => e.Message.Contains(MergesCoercionWarning));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("ids-was-a-string-not-an-array=2", warning.Message);
    }

    /// <summary>P1-5: `"ids": [2,3,4]` — the ids as NUMBERS. This is the case where tolerance STOPS. A bare 2 is
    /// rendered as the id "2", never as "W2": the digest prints the CHAPTER ORDER in the column right beside the id,
    /// so a model emitting bare integers may well be reading the wrong column, and guessing the prefix would be us
    /// choosing which finding the user loses. So the group resolves to nothing and is REJECTED whole (unknown-id) —
    /// ZERO merges, and every finding, including the synthesis's own, still reaches the user.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_MalformedMerges_IdsAsNumbers_KeepsEveryFinding_AndMergesNothing()
    {
        var (persisted, log) = await BuildWithRawSynthesisAsync(
            SynthesisResponseRaw(
                B8ArcFindingsValue(),
                """[ { "ids": [2,3,4], "keep": 3 } ]"""),
            mergeMapEnabled: true);

        // Nothing was merged, so all eight window findings persist beside the surviving arc finding.
        Assert.Equal(9, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == B8Arc);
        Assert.Single(persisted, f => f.Rationale == B8Daniel14);
        Assert.Single(persisted, f => f.Rationale == B8Daniel13);
        Assert.Single(persisted, f => f.Rationale == B8Daniel13b);

        var warning = Assert.Single(log.Entries, e => e.Message.Contains(MergesCoercionWarning));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("id-was-a-number=4", warning.Message); // three ids + the keep

        // The group was COUNTED as proposed (an honest funnel) and then rejected by the SHIPPED fence, unchanged.
        var coverage = Assert.Single(log.Entries, e => e.Message.Contains(MergeCoverageLine));
        Assert.Contains("proposed 1 merge group(s)", coverage.Message);
        Assert.Contains("rejected 1 (unknown-id=1)", coverage.Message);
    }

    /// <summary>P2-8: `{"findings": null, "merges": [...]}` — the MERGES-ONLY response the b8 prompt now explicitly
    /// invites ("if there are no duplicates, omit `merges`… there is no limit on the number of groups"; the cap of 12
    /// applies to `findings` alone). System.Text.Json writes an explicit null OVER the `= new()` initialiser, so the
    /// old `Findings == null → return null` bailed out and threw the merge map away TOO. A synthesis with nothing new
    /// to ADD but real duplicates to CLOSE lost both. RepairableFields.For(BookReviewResult) has guarded this exact
    /// case for this exact DTO all along; this consumer did not.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_FindingsNull_WithAValidMergeMap_KeepsTheMergeMap()
    {
        var (persisted, log) = await BuildWithRawSynthesisAsync(
            SynthesisResponseRaw("null", B8ValidMergesValue), mergeMapEnabled: true);

        // Zero synthesis findings is a NORMAL outcome — and the merges in the same response were still applied.
        Assert.Equal(6, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == B8Contradiction);
        Assert.Single(persisted, f => f.Rationale == B8Daniel13b);
        Assert.Single(persisted, f => f.Rationale == B8Mem9);
        Assert.DoesNotContain(persisted, f => f.Rationale == B8Daniel13 || f.Rationale == B8Mem9b);
        Assert.DoesNotContain(persisted, f => f.Rationale == B8Arc); // it emitted none, and that is fine

        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("explicit `\"findings\": null`"));

        // An explicit null is a SHAPE fault in `findings`, not in `merges` — the merge map matched its contract, so
        // it must not be reported as coerced.
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(MergesCoercionWarning));
        var coverage = Assert.Single(log.Entries, e => e.Message.Contains(MergeCoverageLine));
        Assert.Contains("applied 2", coverage.Message);
    }

    /// <summary>`"merges": null` — an OPTIONAL key the model chose to null out. Legitimate, not a fault: the findings
    /// stand and nothing is merged. Asserted mainly so the coercion warning does NOT fire on a normal shape.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_MergesNull_KeepsTheFindings_AndProposesNothing()
    {
        var (persisted, log) = await BuildWithRawSynthesisAsync(
            SynthesisResponseRaw(B8ArcFindingsValue(), "null"), mergeMapEnabled: true);

        Assert.Equal(9, persisted.Count); // all eight window findings + the arc finding; nothing merged
        Assert.Single(persisted, f => f.Rationale == B8Arc);

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(MergesCoercionWarning));
        var coverage = Assert.Single(log.Entries, e => e.Message.Contains(MergeCoverageLine));
        Assert.Contains("proposed 0 merge group(s)", coverage.Message);
    }

    /// <summary>`"merges": []` — the empty list the prompt names as the no-duplicates answer. Same floor, same silence
    /// from the coercion guard.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_MergesEmptyArray_KeepsTheFindings_AndProposesNothing()
    {
        var (persisted, log) = await BuildWithRawSynthesisAsync(
            SynthesisResponseRaw(B8ArcFindingsValue(), "[]"), mergeMapEnabled: true);

        Assert.Equal(9, persisted.Count);
        Assert.Single(persisted, f => f.Rationale == B8Arc);

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(MergesCoercionWarning));
        var coverage = Assert.Single(log.Entries, e => e.Message.Contains(MergeCoverageLine));
        Assert.Contains("proposed 0 merge group(s)", coverage.Message);
    }

    /// <summary>THE WORST SHAPE OF P1-5, AND THE REASON IT WAS A P1 RATHER THAN A LATENT NOTE: the kill-switch is OFF
    /// — the merge feature is, by every claim made for it, NOT RUNNING — and yet a malformed `merges` payload still
    /// destroyed the whole synthesis, because the PROMPT ASKS FOR `merges` REGARDLESS OF THE SWITCH. So the feature
    /// that ships disabled could delete real findings. With the switch OFF nothing may be merged, and NOTHING may be
    /// lost: all six window findings AND the synthesis's own arc finding persist.</summary>
    [Fact]
    public async Task BuildBookReviewAsync_MalformedMerges_WithTheKillSwitchOFF_StillKeepsEveryFinding()
    {
        var (persisted, log) = await BuildWithRawSynthesisAsync(
            SynthesisResponseRaw(
                B8ArcFindingsValue(),
                """{ "ids": ["W3","W4"], "keep": "W4" }"""),
            mergeMapEnabled: false);

        Assert.Equal(9, persisted.Count); // NOTHING merged and NOTHING deleted — the OFF state's exact guarantee
        Assert.Single(persisted, f => f.Rationale == B8Arc); // ...including the finding the bug used to destroy
        Assert.Single(persisted, f => f.Rationale == B8Daniel14);
        Assert.Single(persisted, f => f.Rationale == B8Daniel13);
        Assert.Single(persisted, f => f.Rationale == B8Daniel13b);
        Assert.Single(persisted, f => f.Rationale == B8Contradiction);

        var warning = Assert.Single(log.Entries, e => e.Message.Contains(MergesCoercionWarning));
        Assert.Equal(LogLevel.Warning, warning.Level);

        // The OFF state still MEASURES the recovered group (that is what the OFF state is for) without applying it.
        var coverage = Assert.Single(log.Entries, e => e.Message.Contains(MergeCoverageLine));
        Assert.Contains("switch OFF", coverage.Message);
        Assert.Contains("proposed 1 merge group(s)", coverage.Message);
        Assert.Contains("would have merged 1 finding(s) away", coverage.Message);
    }

    // ═══ be-c08 — THE THREE PERSIST-TIER DEFECTS (P3-6, P3-10, P3-15) ════════════════════════════════
    //
    // The persist step is where a wrong answer is PERMANENT: a row's key, its anchors and its user Status all live
    // past the build that wrote them. Each of these three let a defect through the seam in a different way — a rule
    // that was not a fixed point (P3-6), a guard that was strict on one side and permissive on the other (P3-10), and
    // a catch that did not cover the one save failure EF reports as something other than a DbUpdateException (P3-15).
    // ═════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildBookReviewAsync_Windowed_NoAnchorIncomingClaimsAnAnchoredRow_TheChapterLinkSurvivesASECONDBuild()
    {
        // P3-6 — THE KEY UPGRADE WAS NOT A FIXED POINT, AND THE SECOND BUILD IS WHERE IT BILLED. When an anchor-less
        // re-wording fuzzy-claims an ANCHORED row, the row KEEPS its chapter link (test P5 above, and rightly: a
        // navigable finding is never traded for a book-wide one). But it also used to take the fresh finding's key —
        // which is hashed on "no anchor". So the row ended up carrying an ANCHORED payload under a BOOK-WIDE key, and
        // that is not a cosmetic inconsistency, it is a TIME BOMB with a fuse exactly one build long:
        //
        //     build 1 — fuzzy tier claims the row: anchors KEPT, key overwritten with the no-anchor one.
        //     build 2 — the model emits the SAME book-wide copy. Its key now EQUALS the row's stored key, so TIER 1
        //               claims it — and tier 1 is not an anchor-preserving match (KeepPriorAnchors is a fuzzy-tier
        //               rule). The row's ChapterAnchorsJson is overwritten with the anchor-less copy's, and THE
        //               CHAPTER LINK P5 EXISTS TO PROTECT IS GONE.
        //
        // The fix is that the KEY TRAVELS WITH THE ANCHORS: a row that keeps its anchors keeps the key those anchors
        // were hashed into. The book-wide copy then misses BOTH key tiers on every build, the fuzzy tier re-claims it
        // every build, and the anchor survives every build. THIS test is P5 run TWICE — which is all it ever took.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = EmptyFindings,
                [2] = JsonAnchoredFindings(
                    new AnchorSpec("character", MararaLong, Array.Empty<(int, string)>(), Array.Empty<int>())),
            }
        };
        using var provider = BuildWindowedProvider(out _, holder);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var anchoredKey = BookFinding.ComputeDedupKey("character", 1, MararaShort);
        var seededId = await SeedFindingAsync(db, bookId, "character", MararaShort, "acknowledged", anchoredKey, 1);
        await TouchSummaryBaselineAsync(db, bookId);

        var svc = provider.GetRequiredService<BookReviewService>();
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        // BUILD 1 — the P5 outcome, unchanged: same row, Status intact, prose refreshed, chapter link kept.
        var afterOne = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal(seededId, afterOne.Id);
        Assert.Equal(1, Assert.Single(DeserializeAnchors(afterOne.ChapterAnchorsJson)).Order);
        // ...and the row's key still agrees with the anchors it is still carrying. Pre-fix this had become
        // ComputeDedupKey("character", null, MararaLong) — a book-wide key on an anchored row.
        Assert.Equal(anchoredKey, afterOne.DedupKey);

        // BUILD 2 — the same model output, a real (stale-vs-briefs) rebuild.
        await TouchSummaryBaselineAsync(db, bookId);
        Assert.True((await svc.BuildBookReviewAsync(bookId, "he")).Ready);

        var afterTwo = Assert.Single(await db.BookFindings.AsNoTracking().Where(f => f.BookId == bookId).ToListAsync());
        Assert.Equal(seededId, afterTwo.Id);         // still the same row — no orphan, no duplicate card
        Assert.Equal("acknowledged", afterTwo.Status); // the user's decision still stands
        Assert.Equal(MararaLong, afterTwo.Rationale);

        // ★ THE ASSERTION THAT WAS RED: the chapter link is STILL THERE after the second build.
        Assert.Equal(1, Assert.Single(DeserializeAnchors(afterTwo.ChapterAnchorsJson)).Order);
        Assert.Equal(anchoredKey, afterTwo.DedupKey); // and the row is a FIXED POINT: key and anchors, both stable
    }

    [Fact]
    public void MatchIncomingToExistingRows_AFreshFindingWhoseAnchorsDoNotPARSE_IsNeverFuzzyMatched()
    {
        // P3-10 — THE UNKNOWN-SCOPE ASYMMETRY. PersistPreservingStatusAsync refuses to even OFFER a persisted row
        // whose anchor payload will not parse ("unknown scope must not be fuzzy-matched, exactly as it must not be
        // deleted"). The FRESH side of the very same comparison did the opposite: `ChapterOrdersOf(fresh) ?? EMPTY`
        // degraded an unreadable payload to the empty set, which ComparisonOrderOf maps to NULL — and null is not a
        // neutral value here, it is the MOST PERMISSIVE one there is: a MayFold WILDCARD that can claim a row anchored
        // to ANY chapter. The lenient default sat on the one side where the guard's whole point is caution.
        //
        // Same rule, both sides, now: a scope we cannot read is a scope we do not act on.
        var realChapterOrders = new HashSet<int> { 0, 1, 2 };

        // The persisted row: an ACKNOWLEDGED finding anchored to a real chapter (2). Under the fresh finding's
        // wildcard it was claimable; with the fresh scope unknown, it must not be.
        var row = new BookFinding
        {
            Id = Guid.NewGuid(), Language = "he", Dimension = "theme", Verdict = "improve", Severity = 2,
            Rationale = MararaShort, EvidenceJson = "[]", Status = "acknowledged",
            ChapterAnchorsJson = """[{"order":2,"title":"Chapter 2"}]""",
            DedupKey = BookFinding.ComputeDedupKey("theme", 2, MararaShort),
        };
        var persisted = new List<NearDuplicateCollapser.PersistedCandidate>
        {
            NearDuplicateCollapser.Prepare(row, comparisonOrder: 2),
        };

        // The fresh finding: a genuine RE-WORDING of that row (0.889 — comfortably over even the be-c07 bar), whose
        // own anchor payload is CORRUPT. Everything except the payload says "claim the row".
        var fresh = new BookFinding
        {
            Language = "he", Dimension = "theme", Verdict = "improve", Severity = 2,
            Rationale = MararaLong, EvidenceJson = "[]", Status = "open",
            ChapterAnchorsJson = "{ not an anchor array",
            DedupKey = BookFinding.ComputeDedupKey("theme", null, MararaLong),
        };
        Assert.True(
            NearDuplicateCollapser.Similarity(
                NearDuplicateCollapser.ContentTokens(MararaLong), NearDuplicateCollapser.ContentTokens(MararaShort))
            >= NearDuplicateCollapser.UserActedAnchorMismatchThreshold,
            "premise: the similarity is NOT what stops this claim — the unknown scope is");

        var matches = BookFindingReconciler.MatchIncomingToExistingRows(
            new[] { fresh },
            new Dictionary<string, BookFinding>(StringComparer.Ordinal), // no key-tier hit: the fuzzy tier is on trial
            persisted,
            realChapterOrders,
            new HashSet<Guid>(),
            out var legacyMatches,
            logger: null);

        // NO CLAIM. The fresh finding is simply inserted as its own row (fail-open: a visible card, never a silent
        // claim on somebody else's row — and never a rewrite of an ACKNOWLEDGED row's prose on a scope we cannot read).
        Assert.Null(matches[0]);
        Assert.Equal(0, legacyMatches);

        // ...and the control: the SAME fresh finding with a READABLE (book-wide) payload DOES claim the row. Without
        // this, the test above could pass for the wrong reason (e.g. the row was never offered at all).
        fresh.ChapterAnchorsJson = "[]";
        var readable = BookFindingReconciler.MatchIncomingToExistingRows(
            new[] { fresh },
            new Dictionary<string, BookFinding>(StringComparer.Ordinal),
            persisted,
            realChapterOrders,
            new HashSet<Guid>(),
            out _,
            logger: null);
        Assert.NotNull(readable[0]);
        Assert.Equal(row.Id, readable[0]!.Value.Row.Id);
        Assert.True(readable[0]!.Value.ViaReword);
    }

    [Fact]
    public async Task BuildBookReviewAsync_AProblemAtSaveThatIsNotADbUpdateException_StillDetachesTheDirtyBatch()
    {
        // P3-15 — THE CATCH WAS ONE CLAUSE TOO NARROW. The handler exists because a failed batch must not poison the
        // SCOPED DbContext: the caller reads CountAsync off the same context immediately afterwards, and any later
        // SaveChanges on it would RE-ATTEMPT the whole failed batch. But it only caught DbUpdateException — and the
        // one save failure this batch can actually produce that ISN'T one is a CIRCULAR KEY DEPENDENCY: when two rows
        // swap DedupKeys under the unique index (which the fuzzy tier's in-place key rewrites can produce), EF gives
        // up in the batch preparer, BEFORE any SQL, and throws a plain InvalidOperationException. That sailed straight
        // past the handler, leaving every Added/Modified entity tracked and the context dirty.
        //
        // THE FAULT IS INJECTED AT THE SaveChangesAsync SEAM, and the reason is stated rather than hidden: the InMemory
        // provider has no relational batch preparer, so it cannot produce EF's real circular-dependency error. What is
        // under test is not EF — it is the HANDLER'S REACH, which is exactly what the fix changed.
        var holder = new WindowedResponseHolder
        {
            ByWindowIndex = new Dictionary<int, string?>
            {
                [1] = JsonCombinedFindings(new CombinedFindingSpec("plot", "keep", 1, "A finding to persist", 0)),
                [2] = EmptyFindings,
            }
        };
        using var provider = BuildWindowedProvider(out _, holder, saveFails: true);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        await TouchSummaryBaselineAsync(db, bookId);

        var failing = Assert.IsType<SaveFailingDbContext>(db);
        failing.Armed = true; // the seeding above had to succeed; the BUILD's save is the one that fails.

        var svc = provider.GetRequiredService<BookReviewService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BuildBookReviewAsync(bookId, "he"));

        // The handler RAN: the failure is reported as this method's own clean failure, wrapping EF's.
        Assert.Contains("Failed to persist whole-book review findings", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);

        // ...and, the point of the handler: THE CONTEXT IS CLEAN. Pre-fix every projected finding was still sitting in
        // the ChangeTracker as Added, waiting to be re-attempted by whoever saved next on this scoped context.
        Assert.DoesNotContain(
            db.ChangeTracker.Entries<BookFinding>(),
            e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        Assert.DoesNotContain(
            db.ChangeTracker.Entries<BookReviewCoverage>(),
            e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    /// <summary>An <see cref="AppDbContext"/> whose SaveChangesAsync can be ARMED to fail with an
    /// InvalidOperationException — the shape EF Core reports a circular key dependency in (be-c08 / P3-15). Seeding
    /// runs un-armed; only the build's own save fails.</summary>
    private sealed class SaveFailingDbContext : AppDbContext
    {
        public SaveFailingDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public bool Armed { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Armed
                ? throw new InvalidOperationException(
                    "Unable to save changes because a circular dependency was detected in the data to be saved.")
                : base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Reads the persisted <see cref="BookFinding.ChapterAnchorsJson"/> back with the SAME camelCase
    /// options the service writes it with.</summary>
    private static List<FindingChapterAnchor> DeserializeAnchors(string json) =>
        JsonSerializer.Deserialize<List<FindingChapterAnchor>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

    /// <summary>Serialises findings whose anchor ORDER and TITLE are set INDEPENDENTLY (unlike
    /// <see cref="JsonCombinedFindings"/>, which derives the title from the order). The whole point of the b1
    /// tests is a model anchor whose order and title DISAGREE with the book.</summary>
    private static string JsonAnchoredFindings(params AnchorSpec[] specs)
    {
        var findings = specs.Select(s => new
        {
            dimension = s.Dimension,
            verdict = "improve",
            severity = s.Severity,
            rationale = s.Rationale,
            chapterAnchors = s.Anchors.Select(a => new { order = a.Order, title = a.Title }).ToArray(),
            evidence = s.EvidenceOrders.Select(o => new { chapterOrder = o, excerpt = "an excerpt" }).ToArray(),
            suggestedAction = (string?)null
        }).ToArray();
        return JsonSerializer.Serialize(new { findings });
    }

    /// <summary>Spec for a b1 anchor test: the model's anchors as (order, title) pairs that may or may not match
    /// any real chapter, plus the evidence chapterOrders. <paramref name="Severity"/> defaults to the 2 this helper
    /// used to hardcode (so every pre-b8 call site is unchanged); b8's merge tests set it explicitly, because the
    /// finding whose survival they assert is a SEVERITY-3 one.</summary>
    private sealed record AnchorSpec(
        string Dimension, string Rationale, (int Order, string Title)[] Anchors, int[] EvidenceOrders, int Severity = 2);

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

    /// <summary>Seeds ONE BookFinding with an explicit dimension, Status and DedupKey (unlike
    /// <see cref="SeedOpenFindingAsync"/>, which fixes those), anchored to <paramref name="anchorOrders"/> in the
    /// SAME camelCase shape ProjectToEntity emits. Used by the b3 key-migration tests to plant a row EXACTLY as a
    /// pre-b3 build wrote it (a user-acted row under a LEGACY-V1 dedup key). Returns the row's Id so the test can
    /// prove the rebuild re-matched THAT row rather than orphaning it and inserting a duplicate.</summary>
    private static async Task<Guid> SeedFindingAsync(
        AppDbContext db, Guid bookId, string dimension, string rationale, string status, string dedupKey,
        params int[] anchorOrders)
    {
        var anchors = anchorOrders
            .Select(o => new { order = o, title = $"Chapter {o}" })
            .ToArray();
        var anchorsJson = JsonSerializer.Serialize(anchors,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var row = new BookFinding
        {
            BookId = bookId,
            Language = "he",
            Dimension = dimension,
            Verdict = "improve",
            Severity = 2,
            Rationale = rationale,
            EvidenceJson = "[]",
            ChapterAnchorsJson = anchorsJson,
            Status = status,
            DedupKey = dedupKey,
            BuiltWithModel = ActiveModel
        };
        db.BookFindings.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    // ─── be-c02: the whole-book review RECEIVES the character register ───────────────────────────────
    //
    // Before this, GetRelevantFields had no BookReview row (so it fell to ContextField.None),
    // BookReviewService never called BuildContextAsync, and the one analysis whose stated job is judging
    // characters was the one analysis with no character data. These pin that it now arrives, on the SHIPPED
    // path (single-combined windows) as well as the legacy fan-out, and that it arrives for FREE.

    /// <summary>A persisted register: two visible characters and one the author SUPPRESSED.</summary>
    private const string RegisterJsonWithASuppressedEntry = """
        {
          "characters": [
            { "name": "דנה", "role": "protagonist", "gender": "female", "aliases": ["דנצ'ה"] },
            { "name": "יואב", "role": "antagonist", "gender": "male" },
            { "name": "העורב", "role": "minor", "isCharacter": false, "isCharacterConfirmed": true }
          ]
        }
        """;

    private static async Task SeedCharacterRegisterAsync(AppDbContext db, Guid bookId, string? json)
    {
        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = json });
        await db.SaveChangesAsync();
    }

    /// <summary>Every Instruction the mock router was handed, in call order.</summary>
    private static List<string> InstructionsOf(Mock<IAiRouter> routerMock) =>
        routerMock.Invocations
            .Where(i => i.Arguments.Count > 0 && i.Arguments[0] is AiRequest)
            .Select(i => ((AiRequest)i.Arguments[0]).Instruction ?? string.Empty)
            .ToList();

    [Fact]
    public async Task BookReview_WindowPrompt_CarriesTheRegister_WithoutSuppressedCharacters()
    {
        // THE SHIPPED PATH. BookReviewSingleCombined defaults to TRUE, so the review the user actually gets is
        // RunBuildAsync -> AssembleWindowsAsync -> RunCombinedCallAsync -> BuildBookReviewWindowPrompt. The
        // register has to reach THAT prompt; reaching only the legacy per-dimension fan-out would be reaching
        // a path nobody runs.
        using var provider = BuildCombinedProvider(out var routerMock, JsonFindings());
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);
        await SeedCharacterRegisterAsync(db, bookId, RegisterJsonWithASuppressedEntry);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var windowPrompts = InstructionsOf(routerMock)
            .Where(i => !i.Contains("[WINDOW_FINDINGS]") && !i.Contains("[CONTINUITY_SKELETON]"))
            .ToList();
        Assert.NotEmpty(windowPrompts);

        foreach (var prompt in windowPrompts)
        {
            Assert.Contains("[BOOK_CHARACTERS]", prompt);
            Assert.Contains("- דנה (protagonist) [female] (aliases: דנצ'ה)", prompt);
            Assert.Contains("- יואב (antagonist) [male]", prompt);
            // The author struck this one out. A suppression that the register API honours but the review's
            // model never hears about would be inert exactly where it matters most.
            Assert.DoesNotContain("העורב", prompt);
            // The review's block is a DIFFERENT surface from the per-analysis one, and must not borrow its tag.
            Assert.DoesNotContain("[CHARACTER_REGISTER]", prompt);
        }
    }

    [Fact]
    public async Task BookReview_SynthesisPrompt_CarriesTheRegister()
    {
        // The synthesis reduce sees NO chapter text at all - its whole view of the book is the BookBrief plus
        // the findings digest - so without the register it cannot make a holistic character observation about
        // a cast it was never shown.
        using var provider = BuildCombinedProvider(out var routerMock, JsonFindings());
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);
        await SeedCharacterRegisterAsync(db, bookId, RegisterJsonWithASuppressedEntry);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var synthesis = Assert.Single(InstructionsOf(routerMock).Where(i => i.Contains("[WINDOW_FINDINGS]")));
        Assert.Contains("[BOOK_CHARACTERS]", synthesis);
        Assert.Contains("- דנה (protagonist) [female]", synthesis);
        Assert.DoesNotContain("העורב", synthesis);
        // Placed between the book brief and the digest, so the model reads context then evidence.
        Assert.True(
            synthesis.IndexOf("[BOOK_CHARACTERS]", StringComparison.Ordinal)
                < synthesis.IndexOf("[WINDOW_FINDINGS]", StringComparison.Ordinal),
            "the register block belongs before the findings digest");
    }

    [Fact]
    public async Task BookReview_SynthesisDigestBudget_IsReducedByTheRegisterBlock()
    {
        // The synthesis prompt is brief + [BOOK_CHARACTERS] + digest, all in ONE model window. The digest sizes
        // itself against the resolved budget MINUS the blocks that share that window, so adding the register
        // must SHRINK the digest - not ride on top of a budget that no longer describes the prompt. Observed
        // end to end: with a large register in place, the same over-budget digest keeps strictly FEWER lines.
        var longRationale = new string('ל', 140);
        var manySpecs = Enumerable.Range(0, 12)
            .Select(i => new CombinedFindingSpec("plot", "improve", (i % 3) + 1, $"{longRationale} #{i}", i))
            .ToArray();

        static WindowedResponseHolder NewHolder(CombinedFindingSpec[] specs) => new()
        {
            ByWindowIndex = new Dictionary<int, string?> { [1] = JsonCombinedFindings(specs) },
            SynthesisResponse = JsonCombinedFindings(
                new CombinedFindingSpec("theme", "improve", 2, "Synthesis note", 0)),
        };

        // A budget big enough that the digest ALMOST fits: the register's cost is then what decides the cap.
        const int budget = 1500;

        var bareHolder = NewHolder(manySpecs);
        using var bare = BuildWindowedProvider(out _, bareHolder, bookContextTokenBudget: budget);
        var bareDb = bare.GetRequiredService<AppDbContext>();
        var bareBookId = await SeedReviewableBookAsync(bareDb, chapterCount: 1);
        await bare.GetRequiredService<BookReviewService>().BuildBookReviewAsync(bareBookId, "he");

        var withHolder = NewHolder(manySpecs);
        using var with = BuildWindowedProvider(out _, withHolder, bookContextTokenBudget: budget);
        var withDb = with.GetRequiredService<AppDbContext>();
        var withBookId = await SeedReviewableBookAsync(withDb, chapterCount: 1);
        await SeedCharacterRegisterAsync(withDb, withBookId, BigRegisterJson(24));
        await with.GetRequiredService<BookReviewService>().BuildBookReviewAsync(withBookId, "he");

        static int DigestLines(string? instruction, string marker) => (instruction ?? string.Empty)
            .Split('\n').Count(l => l.Contains(marker, StringComparison.Ordinal));

        Assert.NotNull(bareHolder.SynthesisInstruction);
        Assert.NotNull(withHolder.SynthesisInstruction);
        Assert.Contains("[BOOK_CHARACTERS]", withHolder.SynthesisInstruction!);
        Assert.DoesNotContain("[BOOK_CHARACTERS]", bareHolder.SynthesisInstruction!);

        var bareLines = DigestLines(bareHolder.SynthesisInstruction, longRationale);
        var withLines = DigestLines(withHolder.SynthesisInstruction, longRationale);

        // Non-vacuity: the register-less digest must actually be near the cap, or "fewer" proves nothing.
        Assert.True(bareLines > 0 && bareLines <= 12,
            $"fixture precondition: the register-less digest kept {bareLines}/12 lines");
        Assert.True(withLines < bareLines,
            $"the register block must come OUT of the digest's budget: kept {withLines} lines with it vs " +
            $"{bareLines} without, so the digest was sized against a window the register was not charged to");
    }

    /// <summary>A persisted register JSON with <paramref name="count"/> long Hebrew entries (past the render cap).</summary>
    private static string BigRegisterJson(int count)
    {
        var characters = Enumerable.Range(0, count).Select(i => new
        {
            name = $"דמות מרכזית מספר {i}",
            role = "supporting",
            gender = i % 2 == 0 ? "female" : "male",
            aliases = new[] { $"כינוי {i}", $"שם חיבה {i}" }
        });
        return JsonSerializer.Serialize(new { characters });
    }

    [Fact]
    public async Task BookReview_PerDimensionFanOut_CharacterDimensionPrompt_CarriesTheRegister()
    {
        // The legacy fan-out (toggle OFF) is where the character-specific lens text actually lives
        // (BuildBookReviewPrompt's "do characters develop / does one disappear unexplained"). It reaches the
        // register through the same assembled window text, so the dimension that ASKS about characters is now
        // also the dimension that is TOLD who they are.
        using var provider = BuildProvider(out var routerMock, FindingsPerDimension(perDimensionCount: 0));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);
        await SeedCharacterRegisterAsync(db, bookId, RegisterJsonWithASuppressedEntry);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var characterPrompt = Assert.Single(
            InstructionsOf(routerMock).Where(i => i.Contains("\"dimension\": \"character\"")));
        Assert.Contains("[BOOK_CHARACTERS]", characterPrompt);
        Assert.Contains("- דנה (protagonist) [female]", characterPrompt);
        Assert.DoesNotContain("העורב", characterPrompt);
    }

    [Fact]
    public async Task BookReview_ReadingTheRegister_SpendsNoExtractionCall()
    {
        // d1 §2's BOUND: a whole-book review triggers ZERO character-extraction calls. The obvious way to load
        // a register (AnalysisContextService.LoadCharacterRegisterAsync) can EXTRACT, and a 40-chapter review
        // deciding to spend model time on an extraction nobody asked for is exactly the GPU cost this machine
        // cannot absorb. So the review reads the stored row directly - one AsNoTracking query, no model call.
        using var provider = BuildCombinedProvider(out var withRouter, JsonFindings());
        var withDb = provider.GetRequiredService<AppDbContext>();
        var withBookId = await SeedReviewableBookAsync(withDb, chapterCount: 3);
        await SeedCharacterRegisterAsync(withDb, withBookId, RegisterJsonWithASuppressedEntry);
        await provider.GetRequiredService<BookReviewService>().BuildBookReviewAsync(withBookId, "he");

        using var bare = BuildCombinedProvider(out var bareRouter, JsonFindings());
        var bareDb = bare.GetRequiredService<AppDbContext>();
        var bareBookId = await SeedReviewableBookAsync(bareDb, chapterCount: 3);
        await bare.GetRequiredService<BookReviewService>().BuildBookReviewAsync(bareBookId, "he");

        // Identical call counts: supplying a register adds no call of any kind.
        Assert.Equal(InstructionsOf(bareRouter).Count, InstructionsOf(withRouter).Count);
        // And nothing on the review's path routed the character pre-pass's task.
        Assert.All(
            withRouter.Invocations.Select(i => (AiRequest)i.Arguments[0]),
            req => Assert.Equal(AiTaskType.BookReview, req.TaskType));
    }

    [Fact]
    public async Task BookReview_UnreadableCharacterRegister_DegradesToNoCharacterContext()
    {
        // FAIL-SAFE, matching every other reader of this column: a corrupt register must not fail the review.
        // It degrades to "no characters", exactly as a book that has never had one behaves.
        using var provider = BuildCombinedProvider(out var routerMock, JsonFindings(
            new FindingSpec("improve", 2, "a finding", 0)));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);
        await SeedCharacterRegisterAsync(db, bookId, "{ this is not json ");

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        Assert.All(InstructionsOf(routerMock), i => Assert.DoesNotContain("[BOOK_CHARACTERS]", i));
    }

    [Fact]
    public async Task BookReview_BookWithNoRegister_PromptsAreUnchanged()
    {
        // The no-register book must be byte-identical to what it was before the register reached this path:
        // no markers, no bytes. Otherwise every such book pays for an empty section.
        using var provider = BuildCombinedProvider(out var routerMock, JsonFindings(
            new FindingSpec("improve", 2, "a finding", 0)));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);
        // No BookBible row at all - the ordinary state of a book whose register was never built.

        var svc = provider.GetRequiredService<BookReviewService>();
        var result = await svc.BuildBookReviewAsync(bookId, "he");

        Assert.True(result.Ready);
        var prompts = InstructionsOf(routerMock);
        Assert.NotEmpty(prompts);
        Assert.All(prompts, i => Assert.DoesNotContain("[BOOK_CHARACTERS]", i));
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
        Dictionary<string, string> dimensionFindings,
        CapturingLoggerProvider? logCapture = null)
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
        string combinedResponse,
        CapturingLoggerProvider? logCapture = null)
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
    /// <param name="saveFails">be-c08 / P3-15: registers a <see cref="SaveFailingDbContext"/> so a test can ARM a
    /// SaveChangesAsync failure that is NOT a DbUpdateException and assert the persist step still detaches its dirty
    /// batch. Off by default, so every other test gets the ordinary in-memory context.</param>
    private static ServiceProvider BuildWindowedProvider(
        out Mock<IAiRouter> routerMock,
        WindowedResponseHolder holder,
        CapturingLoggerProvider? logCapture = null,
        int? bookContextTokenBudget = null,
        int overlapChapters = 0,
        bool mergeMapEnabled = false,
        bool saveFails = false)
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
        if (saveFails)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            services.AddScoped<AppDbContext>(_ => new SaveFailingDbContext(options));
        }
        else
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
            // Default: NO overlap, so each window's SHOWN set equals its single primary chapter and the b7
            // visibility asserts are exact. The b7 overlap test opts in to K=1 to prove the overlap chapter counts
            // as SHOWN (a window may legitimately anchor onto the chapter it was handed as boundary context).
            o.BookReviewWindowOverlapChapters = overlapChapters;
            // b8: the synthesis merge map SHIPS OFF, so it is off here too unless a test opts in. Every pre-b8 test
            // therefore keeps its exact behaviour, which is the same promise the shipped default makes.
            o.BookReview.SynthesisMergeMap = mergeMapEnabled;
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

    /// <summary>A PARSED-EMPTY window response ({"findings":[]}). It PARSES (unlike an ABSENT map key, which serves
    /// empty OUTPUT "" = a hard window FAILURE) but reports nothing — and per be-c01 that is a SUSPECTED TRUNCATION,
    /// NOT a clean review: the model has no way to say "these chapters are fine" that differs from a cut-off
    /// response, so its chapters are NOT counted as reviewed and nothing is deleted for them. Used as filler for
    /// windows a test does not care about, and as the SUBJECT of the be-c01 tests below.</summary>
    private const string EmptyFindings = "{ \"findings\": [] }";

    /// <summary>The be-c01 P0 shape, verbatim: a model whose structured output was silently cut short emits a bare
    /// object. System.Text.Json leaves BookReviewResult.Findings at its `= new()` initialiser, so this deserializes
    /// to EMPTY-but-NOT-NULL — the exact byte pattern that used to be read as "a clean, successful window".</summary>
    private const string TruncatedEmptyObject = "{}";

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
