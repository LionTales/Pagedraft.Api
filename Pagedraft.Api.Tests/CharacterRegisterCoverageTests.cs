using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// AUTOMATIC WHOLE-BOOK COVERAGE (automatic-coverage plan, be-c01 / d1 §1-§2).
///
/// <para>What changed and why these tests exist. <c>LoadCharacterRegisterAsync</c> used to return
/// early on ANY non-empty register, so the character-extraction pre-pass fired exactly ONCE in a
/// book's lifetime, over whichever unit happened to be analysed first, truncated to 2000 words. A
/// character introduced in chapter 33 could therefore never enter the register, and what the register
/// held depended on which chapter the author opened first. The gate is replaced by a per-chapter scan
/// ledger (<see cref="CharacterRegister.ScannedChapters"/>) keyed on <c>Chapter.UpdatedAt</c>.</para>
///
/// <para>THE VACUITY TRAP THIS CLASS IS WRITTEN AGAINST, stated once because every test below is
/// shaped by it: a no-op merge produces a BYTE-IDENTICAL register to a merge that never ran. So "the
/// persisted value looks right" proves nothing about whether a scan happened or was correctly
/// skipped. Every claim about scanning is asserted through the router mock's extraction CALL COUNT;
/// the persisted value is only ever used for claims about CONTENT. This subsystem has already shipped
/// one test that passed because nothing ran at all.</para>
/// </summary>
public class CharacterRegisterCoverageTests
{
    /// <summary>Chapter 1's prose and the character the extractor finds in it.</summary>
    private const string Chapter1Text = "רונית פתחה את הדלת ויצאה אל הרחוב.";

    /// <summary>Chapter 33's prose. Its character appears NOWHERE in chapter 1.</summary>
    private const string Chapter33Text = "מרים ישבה על הספסל וחיכתה עד הערב.";

    // ── 1. A chapter-33 character enters the register on THAT chapter's first analysis ───────────

    [Fact]
    public async Task ChapterAnalysedLater_ContributesItsOwnCharacters_ToTheRegister()
    {
        // THE DEFECT THIS PINS: with the one-shot gate, chapter 1's analysis froze the register and
        // chapter 33's analysis read it back unchanged, so מרים never existed as far as any edit type
        // was concerned - and which characters the book "had" depended on click order.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, chapter33) = await harness.SeedTwoChapterBookAsync();

        // Chapter 1 first, exactly as the author would: it bootstraps the register.
        var first = await harness.AnalyseAsync(chapter1);
        Assert.Equal(new[] { "רונית" }, first.Characters!.Characters.Select(c => c.Name));

        // Now chapter 33.
        var later = await harness.AnalyseAsync(chapter33);

        // The context handed to chapter 33's OWN analysis already knows מרים - the register is built
        // before the prompt is assembled, so a late character is usable on the very analysis that
        // discovers it, not only on the next one.
        Assert.Contains(later.Characters!.Characters, c => c.Name == "מרים");
        Assert.Contains(later.Characters.Characters, c => c.Name == "רונית");

        var persisted = await harness.ReadRegisterAsync(bookId);
        Assert.Equal(new[] { "רונית", "מרים" }, persisted.Characters.Select(c => c.Name));

        // NON-VACUITY + THE TRUNCATION POINT: exactly two extractions, and the SECOND one read chapter
        // 33's own prose. Before this change the 2000-word cap was applied to whichever unit happened
        // to be analysed first; now each chapter is read on its own terms.
        Assert.Equal(2, harness.ExtractionInputs.Count);
        Assert.Contains("רונית", harness.ExtractionInputs[0]);
        Assert.Contains("מרים", harness.ExtractionInputs[1]);
        Assert.DoesNotContain("רונית", harness.ExtractionInputs[1]);

        // Both chapters are in the ledger, each stamped against its own chapter's UpdatedAt.
        Assert.Equal(
            new[] { chapter1, chapter33 }.OrderBy(id => id),
            persisted.ScannedChapters.Select(e => e.ChapterId).OrderBy(id => id));
    }

    // ── 2. An UNCHANGED chapter does not re-extract ──────────────────────────────────────────────

    [Fact]
    public async Task AnalysingTheSameUnchangedChapterAgain_FiresNoSecondExtraction()
    {
        // THE ASSERTION HAS TO BE THE CALL COUNT. Re-scanning an unchanged chapter would merge the
        // same extraction over the same register and persist a byte-identical column, so nothing about
        // the stored value can distinguish "skipped" from "re-ran pointlessly" - only the router can.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, _) = await harness.SeedTwoChapterBookAsync();

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(1, harness.ExtractionCount);

        await harness.AnalyseAsync(chapter1);
        await harness.AnalyseAsync(chapter1);

        Assert.Equal(1, harness.ExtractionCount);

        // NON-VACUITY: the skip is the ledger doing its job, not the register having been empty or
        // unreadable all along.
        var persisted = await harness.ReadRegisterAsync(bookId);
        Assert.Contains(persisted.Characters, c => c.Name == "רונית");
        Assert.Single(persisted.ScannedChapters);
    }

    // ── 3. A chapter EDIT re-scans, and the re-scan does not clobber author state ────────────────

    [Fact]
    public async Task EditingAScannedChapter_ReScansIt_WithoutClobberingAuthorState()
    {
        // The merge is a LIVE path from be-c01 onwards, so every guarantee it carries has to hold
        // against a real re-scan and not only against a unit test of Merge().
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, _) = await harness.SeedTwoChapterBookAsync();

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(1, harness.ExtractionCount);

        // The author now works on the register: contradicts the extracted gender, hand-adds a
        // character the extractor never proposed, and strikes one out permanently.
        await harness.ApplyAuthorEditsAsync(
            bookId,
            new CharacterRegisterEditDto("רונית", "upsert", Gender: "male"),
            new CharacterRegisterEditDto("אלון", "upsert"),
            new CharacterRegisterEditDto("הרוח", "upsert"),
            new CharacterRegisterEditDto("הרוח", "suppress"));

        var beforeEdit = await harness.ReadRegisterAsync(bookId);
        Assert.True(beforeEdit.Characters.Single(c => c.Name == "רונית").GenderConfirmed);
        Assert.True(beforeEdit.Characters.Single(c => c.Name == "אלון").IsAuthorAdded);
        Assert.True(CharacterRegisterMerge.IsSuppressed(beforeEdit.Characters.Single(c => c.Name == "הרוח")));

        // The author edits the CHAPTER: new prose, and Chapter.UpdatedAt moves. That is the re-scan key.
        var rewritten = "רונית וגם הרוח פגשו את יעל בשוק.";
        await harness.RewriteChapterAsync(chapter1, rewritten);

        var context = await harness.AnalyseAsync(chapter1);

        // NON-VACUITY: the edit really did trigger a second scan. Everything below is about what that
        // scan was and was not allowed to do.
        Assert.Equal(2, harness.ExtractionCount);
        Assert.Contains("יעל", harness.ExtractionInputs[1]);

        var persisted = await harness.ReadRegisterAsync(bookId);

        // (a) The author-confirmed gender WINS over the fresh extraction, which said "female".
        var ronit = persisted.Characters.Single(c => c.Name == "רונית");
        Assert.Equal("male", ronit.Gender);
        Assert.True(ronit.GenderConfirmed);

        // (b) The hand-added character is untouched, even though the extraction never mentioned him.
        Assert.True(persisted.Characters.Single(c => c.Name == "אלון").IsAuthorAdded);

        // (c) The suppressed entry is NOT resurrected, even though the rewritten chapter names it and
        // the extraction proposed it again - and it is not described to the analysis either.
        Assert.True(CharacterRegisterMerge.IsSuppressed(persisted.Characters.Single(c => c.Name == "הרוח")));
        Assert.DoesNotContain(context.Characters!.Characters, c => c.Name == "הרוח");

        // (d) The genuinely new character the edit introduced DID enter.
        Assert.Contains(persisted.Characters, c => c.Name == "יעל");
        Assert.Contains(context.Characters.Characters, c => c.Name == "יעל");

        // (e) The ledger re-stamped the SAME chapter rather than appending a second line for it, and
        // the new stamp matches the chapter's current UpdatedAt so the next analysis skips.
        var entry = Assert.Single(persisted.ScannedChapters);
        Assert.Equal(chapter1, entry.ChapterId);
        Assert.Equal(await harness.ChapterUpdatedAtAsync(chapter1), entry.SourceStamp);

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(2, harness.ExtractionCount);
    }

    [Fact]
    public async Task AReScanThatChangesNothing_DoesNotMoveTheRegisterStamp()
    {
        // UpdatedAt is the invalidation signal every prior AnalysisResult on the book is measured
        // against. Coverage advancing is NOT a character fact changing, so grafting a ledger entry on
        // must leave the stamp exactly where it was - otherwise every result on the book reads stale
        // the moment a second chapter is analysed.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, chapter33) = await harness.SeedTwoChapterBookAsync();

        await harness.AnalyseAsync(chapter1);
        var afterFirst = await harness.ReadRegisterAsync(bookId);
        var stamp = afterFirst.UpdatedAt;
        Assert.NotNull(stamp); // the bootstrap DID change the entry set, so it legitimately stamped.

        // Chapter 33, but its extraction proposes only a character the register already holds, so the
        // merge changes nothing while the LEDGER still gains a line.
        harness.ExtractionOverride = _ => Payload(("רונית", "female"));
        await harness.AnalyseAsync(chapter33);

        var afterSecond = await harness.ReadRegisterAsync(bookId);

        // NON-VACUITY: the scan really ran (so the stamp really was at risk) and coverage really grew.
        Assert.Equal(2, harness.ExtractionCount);
        Assert.Equal(2, afterSecond.ScannedChapters.Count);

        Assert.Equal(stamp, afterSecond.UpdatedAt);
    }

    // ── 3b. A FAILED extraction must not be recorded as a scan (final-r01) ───────────────────────

    [Fact]
    public async Task AnExtractionThatFailed_DoesNotMarkTheChapterScanned()
    {
        // THE DEFECT THIS PINS. A chapter-keyed scan records its ledger entry on ANY answer, including
        // an empty one, so that a foreword with nobody in it is not re-extracted forever. Collapsing a
        // FAILED call into that same "no characters" answer would make one wedged Ollama run mark every
        // chapter analysed during the outage permanently covered while having read none of them -
        // invisible in the register (it looks like a chapter with no characters) and invisible in the
        // coverage report (it says covered), with no way back short of an author edit of that chapter.
        //
        // ONLY THE CALL COUNT CAN SEE THIS, again: the persisted register after a failed scan and after
        // a genuinely empty one are byte-identical.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, _) = await harness.SeedTwoChapterBookAsync();

        harness.ExtractionOverride = _ => throw new InvalidOperationException("the model is down");
        var duringOutage = await harness.AnalyseAsync(chapter1);

        // NON-VACUITY: the call really was attempted, and the analysis really did degrade rather than
        // fail - the fail-safe contract is unchanged by this.
        Assert.Equal(1, harness.ExtractionCount);
        Assert.True(duringOutage.Characters is null || duringOutage.Characters.Characters.Count == 0);

        // NOTHING was written, so nothing claims this chapter was read.
        Assert.Equal(0, await harness.CountBibleRowsAsync(bookId));
        var duringCoverage = await harness.CoverageAsync(bookId);
        Assert.Equal(0, duringCoverage.CoveredChapters);
        Assert.Equal(2, duringCoverage.PendingChapters);

        // And the very next analysis retries it, rather than believing a scan that never happened.
        harness.ExtractionOverride = null;
        await harness.AnalyseAsync(chapter1);
        Assert.Equal(2, harness.ExtractionCount);
        Assert.Contains((await harness.ReadRegisterAsync(bookId)).Characters, c => c.Name == "רונית");
        Assert.Equal(1, (await harness.CoverageAsync(bookId)).CoveredChapters);
    }

    [Fact]
    public async Task AChapterWithNobodyInIt_IsRecordedAsScanned_AndNotReExtracted()
    {
        // The other side of the same coin, and the reason the guard above has to be about FAILURE
        // rather than about emptiness: a chapter the model read and found nobody in (a foreword, a
        // title page) IS scanned and must never pay for the call again. An empty ANSWER and a failed
        // CALL look identical in the persisted register, so if the two tests do not both exist one of
        // the two behaviours can regress silently into the other.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, _) = await harness.SeedTwoChapterBookAsync();

        harness.ExtractionOverride = _ => "[]"; // the model answered, and named nobody
        await harness.AnalyseAsync(chapter1);

        Assert.Equal(1, harness.ExtractionCount);
        var persisted = await harness.ReadRegisterAsync(bookId);
        Assert.Empty(persisted.Characters);
        var entry = Assert.Single(persisted.ScannedChapters);
        Assert.Equal(chapter1, entry.ChapterId);
        Assert.Null(persisted.UpdatedAt); // nothing changed, so the stamp did not move

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(1, harness.ExtractionCount);
        Assert.Equal(1, (await harness.CoverageAsync(bookId)).CoveredChapters);
    }

    // ── 3c. A SCENE-scoped analysis scans the CHAPTER, not the scene (d1 §2) ─────────────────────

    [Fact]
    public async Task SceneScopedAnalysis_ScansTheWholeChapter_NotOnlyTheTriggeringScene()
    {
        // THE INVARIANT THE LEDGER'S KEY FORCES, and the one behaviour change d1 §2 named explicitly.
        // The ledger is keyed by ChapterId, so "scanned" has to mean the CHAPTER's own content was
        // read. If a scene-scoped analysis extracted from the scene's text alone, a five-scene chapter
        // would be marked covered after only the first scene ever contributed, and a character who
        // appears only in scene four would be permanently missing while the coverage report said the
        // chapter was done - the exact silent-hole shape this whole plan exists to close.
        //
        // The discriminator is the extraction INPUT: the scene's prose names only מרים, the chapter's
        // ContentText names only רונית, and the extractor reports whoever it is actually shown.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, _) = await harness.SeedTwoChapterBookAsync();
        var sceneId = await harness.SeedSceneAsync(chapter1, Chapter33Text);

        var context = await harness.AnalyseSceneAsync(sceneId);

        Assert.Equal(1, harness.ExtractionCount);
        // The CHAPTER's text was sent, not the scene's.
        Assert.Contains("רונית", harness.ExtractionInputs[0]);
        Assert.DoesNotContain("מרים", harness.ExtractionInputs[0]);
        Assert.Contains(context.Characters!.Characters, c => c.Name == "רונית");

        // ...and the ledger line it wrote is the CHAPTER's, stamped against the chapter's own version,
        // so a later chapter-scoped analysis of the same chapter correctly skips.
        var persisted = await harness.ReadRegisterAsync(bookId);
        var entry = Assert.Single(persisted.ScannedChapters);
        Assert.Equal(chapter1, entry.ChapterId);
        Assert.Equal(await harness.ChapterUpdatedAtAsync(chapter1), entry.SourceStamp);

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(1, harness.ExtractionCount);
    }

    // ── 4. The ledger survives a round trip ──────────────────────────────────────────────────────

    [Fact]
    public void ScannedChapterLedger_SurvivesTheColumnRoundTrip()
    {
        // The ledger rides inside the ONE register JSON column through the ONE serializer
        // configuration. A property that silently failed to round-trip would make every chapter look
        // unscanned forever, which reads as "coverage works" (it keeps scanning) while burning one LLM
        // call per analysis for the life of the book.
        var chapterId = Guid.NewGuid();
        var original = new CharacterRegister
        {
            UpdatedAt = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero),
            Characters = new[] { new CharacterRegisterEntry { Name = "רונית" } },
            ScannedChapters = new[]
            {
                new ScannedChapterEntry
                {
                    ChapterId = chapterId,
                    ScannedAt = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero),
                    SourceStamp = new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.Zero)
                }
            }
        };

        Assert.True(CharacterRegisterService.TryDeserialize(
            CharacterRegisterService.Serialize(original), out var round, out var fault));
        Assert.Null(fault);

        var entry = Assert.Single(round!.ScannedChapters);
        Assert.Equal(chapterId, entry.ChapterId);
        Assert.Equal(original.ScannedChapters[0].ScannedAt, entry.ScannedAt);
        Assert.Equal(original.ScannedChapters[0].SourceStamp, entry.SourceStamp);
    }

    [Fact]
    public void ALegacyRegisterWithNoLedger_ReadsAsEmpty_NotNull()
    {
        // Every register already persisted has no `scannedChapters` at all, and an explicit null is
        // reachable too (this codebase's canonical System.Text.Json trap: a `= Array.Empty<T>()`
        // initializer does NOT survive an explicit null). Both must read as "nothing scanned yet", and
        // neither may NRE the ledger lookup on the analysis path.
        foreach (var json in new[]
                 {
                     """{ "characters": [ { "name": "רונית" } ] }""",
                     """{ "characters": [ { "name": "רונית" } ], "scannedChapters": null }"""
                 })
        {
            Assert.True(CharacterRegisterService.TryDeserialize(json, out var register, out _));
            Assert.NotNull(register!.ScannedChapters);
            Assert.Empty(register.ScannedChapters);
        }
    }

    [Fact]
    public async Task TheLedgerWrittenByAScan_IsReadBackByTheNextAnalysis()
    {
        // The round trip that actually matters: not a serializer unit test, but the persisted column
        // being re-read and BELIEVED. The call-count assertion is what makes this a round-trip test
        // rather than a shape test.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, _) = await harness.SeedTwoChapterBookAsync();

        await harness.AnalyseAsync(chapter1);

        var persisted = await harness.ReadRegisterAsync(bookId);
        var entry = Assert.Single(persisted.ScannedChapters);
        Assert.Equal(chapter1, entry.ChapterId);
        Assert.Equal(await harness.ChapterUpdatedAtAsync(chapter1), entry.SourceStamp);
        Assert.NotEqual(default, entry.ScannedAt);

        // A brand-new context service, reading the column cold, honours what that column says.
        await harness.AnalyseAsync(chapter1);
        Assert.Equal(1, harness.ExtractionCount);
    }

    [Fact]
    public async Task AnAuthorEdit_DoesNotEraseTheScanLedger()
    {
        // THE TWO-WRITER TRAP, and it was live: the register is ONE JSON column with TWO writers, and
        // the author's writer (CharacterRegisterService.ApplyEditsAsync) rebuilds the whole record from
        // its entry list. Anything it does not explicitly carry across is silently dropped. Losing the
        // ledger corrupts nothing an author can see - it just marks every chapter unscanned again, so
        // every chapter pays for a fresh LLM extraction after any edit, forever.
        //
        // Only the CALL COUNT can catch this. The characters and the stamp are identical either way.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, _) = await harness.SeedTwoChapterBookAsync();

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(1, harness.ExtractionCount);
        Assert.Single((await harness.ReadRegisterAsync(bookId)).ScannedChapters);

        await harness.ApplyAuthorEditsAsync(bookId, new CharacterRegisterEditDto("רונית", "upsert", Gender: "male"));

        // NON-VACUITY: the edit really landed, so the column really was rewritten by the other writer.
        var afterEdit = await harness.ReadRegisterAsync(bookId);
        Assert.True(afterEdit.Characters.Single(c => c.Name == "רונית").GenderConfirmed);

        var entry = Assert.Single(afterEdit.ScannedChapters);
        Assert.Equal(chapter1, entry.ChapterId);

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(1, harness.ExtractionCount);
    }

    // ── 5. Two chapters scanning CONCURRENTLY ────────────────────────────────────────────────────

    [Fact]
    public async Task TwoChaptersScanningConcurrently_NeitherLosesItsCharactersNorItsLedgerEntry()
    {
        // WHY THIS TEST EXISTS NOW AND NOT BEFORE: the register used to be written roughly ONCE PER
        // BOOK, so the re-read-before-merge was insurance against an author PATCH landing mid-pre-pass.
        // It is now written once per chapter SCANNED, and two analyses of two different chapters
        // overlap as a matter of course. The pre-call snapshot would overwrite the whole column - both
        // the sibling's characters and its ledger line - with no error and no log entry, and the lost
        // ledger line means that chapter silently pays for a second LLM call later.
        //
        // DETERMINISTIC, NO THREADS: request B runs to completion INSIDE request A's extraction call,
        // on its own DI scope and its own DbContext exactly as a second HTTP request would. Same
        // side-effecting-fake technique as the existing author-edit race test.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, chapter33) = await harness.SeedTwoChapterBookAsync();

        AnalysisContext? bContext = null;
        harness.DuringExtraction = async input =>
        {
            // Only chapter 1's extraction opens the window; chapter 33's own extraction (fired from
            // inside it) must not recurse.
            if (!input.Contains("רונית")) return;
            bContext = await harness.AnalyseAsync(chapter33);
        };

        var aContext = await harness.AnalyseAsync(chapter1);

        // NON-VACUITY: the concurrent request really ran, really extracted, and really wrote. Without
        // this the assertions below would hold for a run in which nothing overlapped at all.
        Assert.NotNull(bContext);
        Assert.Contains(bContext!.Characters!.Characters, c => c.Name == "מרים");
        Assert.Equal(2, harness.ExtractionCount);

        // ONE row: the second writer adopted the row the first created rather than inserting a
        // duplicate that the unique index on BookId would reject with an unhandled DbUpdateException.
        Assert.Equal(1, await harness.CountBibleRowsAsync(bookId));

        var persisted = await harness.ReadRegisterAsync(bookId);

        // Neither request's characters were overwritten by the other's pre-call snapshot.
        Assert.Contains(persisted.Characters, c => c.Name == "רונית");
        Assert.Contains(persisted.Characters, c => c.Name == "מרים");

        // ...and neither request's LEDGER LINE was, which is the new half. A lost line is invisible in
        // the character list and only shows up later as a re-scan that should never have happened.
        Assert.Equal(
            new[] { chapter1, chapter33 }.OrderBy(id => id),
            persisted.ScannedChapters.Select(e => e.ChapterId).OrderBy(id => id));

        // The proof that the surviving ledger is BELIEVED: neither chapter re-extracts now.
        await harness.AnalyseAsync(chapter1);
        await harness.AnalyseAsync(chapter33);
        Assert.Equal(2, harness.ExtractionCount);

        // The later writer's own context saw the union too, so the analysis it fed was not short a
        // character the other request had just discovered.
        Assert.Contains(aContext.Characters!.Characters, c => c.Name == "רונית");
    }

    // ── 6. BOOK scope stays OUT of the ledger (d1 §2's explicit exclusion) ───────────────────────

    [Fact]
    public async Task BookScopeAnalysis_KeepsTheOneShotBehaviour_AndDoesNotTouchTheLedger()
    {
        // A Book-scoped analysis is handed the ASSEMBLED, budget-capped multi-chapter text, which no
        // per-chapter ledger line could honestly describe as "chapter N scanned". So Book scope keeps
        // exactly today's behaviour: a non-empty register is returned as-is with no extraction, and no
        // ledger entry is invented. It neither advances coverage nor regresses it.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, _) = await harness.SeedTwoChapterBookAsync();

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(1, harness.ExtractionCount);
        var afterChapter = await harness.ReadRegisterAsync(bookId);

        var bookContext = await harness.AnalyseBookAsync(bookId);

        // No second extraction, and the ledger is untouched: still exactly chapter 1's line, not a
        // fabricated one and not an erased one.
        Assert.Equal(1, harness.ExtractionCount);
        Assert.Contains(bookContext.Characters!.Characters, c => c.Name == "רונית");

        var afterBook = await harness.ReadRegisterAsync(bookId);
        Assert.Equal(
            afterChapter.ScannedChapters.Select(e => e.ChapterId),
            afterBook.ScannedChapters.Select(e => e.ChapterId));
        Assert.Equal(afterChapter.UpdatedAt, afterBook.UpdatedAt);
    }

    // ── 7. The REPORT and the SCAN PATH read the same ledger (be-c03) ────────────────────────────

    [Fact]
    public async Task TheCoverageReport_TracksWhatTheScanPathActuallyDid_ThroughAFullCycle()
    {
        // THE POINT OF THIS TEST is that neither side is simulated. A real analysis writes the ledger
        // and the real GET endpoint reports it, so a reported count that disagreed with what the
        // scanner believes would fail here - which is the whole guard, because this workspace has
        // shipped a status count and the builder it described drifting apart before. The counts and the
        // scanner's own skip decision come from ONE predicate over ONE list
        // (CharacterRegisterCoverage.IsCoveredAndFresh over CharacterRegister.ScannedChapters).
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, chapter33) = await harness.SeedTwoChapterBookAsync();

        // Nothing analysed yet: the honest answer is 0 of 2, and the book is not complete.
        var before = await harness.CoverageAsync(bookId);
        Assert.Equal(2, before.TotalChapters);
        Assert.Equal(0, before.CoveredChapters);
        Assert.Equal(2, before.PendingChapters);
        Assert.False(before.IsComplete);
        Assert.Null(before.LastScannedAt);

        await harness.AnalyseAsync(chapter1);

        // One analysis, one chapter covered. The partial state be-c03 exists to stop being invisible.
        var afterFirst = await harness.CoverageAsync(bookId);
        Assert.Equal(1, afterFirst.CoveredChapters);
        Assert.Equal(1, afterFirst.PendingChapters);
        Assert.False(afterFirst.IsComplete);
        Assert.NotNull(afterFirst.LastScannedAt);

        await harness.AnalyseAsync(chapter33);

        var afterSecond = await harness.CoverageAsync(bookId);
        Assert.Equal(2, afterSecond.CoveredChapters);
        Assert.Equal(0, afterSecond.PendingChapters);
        Assert.True(afterSecond.IsComplete);

        // NON-VACUITY: two chapters were really scanned, not reported as scanned.
        Assert.Equal(2, harness.ExtractionCount);

        // Now the agreement that matters. Editing chapter 1 must make the REPORT say stale and make the
        // SCANNER re-extract - one predicate, both answers.
        await harness.RewriteChapterAsync(chapter1, "רונית ויעל פגשו זו את זו בשוק.");

        var afterEdit = await harness.CoverageAsync(bookId);
        Assert.Equal(1, afterEdit.StaleChapters);
        Assert.Equal(1, afterEdit.CoveredChapters);
        Assert.False(afterEdit.IsComplete);

        await harness.AnalyseAsync(chapter1);
        Assert.Equal(3, harness.ExtractionCount); // the scanner agreed it was stale

        var afterReScan = await harness.CoverageAsync(bookId);
        Assert.Equal(2, afterReScan.CoveredChapters);
        Assert.Equal(0, afterReScan.StaleChapters);
        Assert.True(afterReScan.IsComplete);

        // ...and the scanner agrees the book is done too: no further extraction on either chapter.
        await harness.AnalyseAsync(chapter1);
        await harness.AnalyseAsync(chapter33);
        Assert.Equal(3, harness.ExtractionCount);
    }

    [Fact]
    public async Task ABookWithAnEmptyChapter_ReachesCompleteCoverage_BecauseTheEmptyOneIsReportedUnscannable()
    {
        // The satisfiability claim, end to end rather than on a hand-built ledger: the analysis path
        // REFUSES an empty chapter outright, so it can never enter the ledger, and a coverage predicate
        // that waited for it would never be satisfied on this book. Reporting it as unscannable is what
        // keeps 'complete' reachable.
        using var harness = new Harness(ExtractionByChapterText);
        var (bookId, chapter1, chapter33) = await harness.SeedTwoChapterBookAsync();
        await harness.RewriteChapterAsync(chapter33, "   ");

        // NON-VACUITY: the pipeline really does refuse it, which is why it can never be covered.
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AnalyseAsync(chapter33));

        await harness.AnalyseAsync(chapter1);

        var coverage = await harness.CoverageAsync(bookId);
        Assert.Equal(2, coverage.TotalChapters);
        Assert.Equal(1, coverage.CoveredChapters);
        Assert.Equal(1, coverage.UnscannableChapters);
        Assert.Equal(0, coverage.PendingChapters);
        Assert.Equal(0, coverage.StaleChapters);
        Assert.True(coverage.IsComplete);
    }

    // ── extraction payloads ─────────────────────────────────────────────────────────────────────

    private static string Payload(params (string Name, string Gender)[] characters)
        => JsonSerializer.Serialize(
            characters.Select(c => new { name = c.Name, gender = c.Gender, role = "supporting" }).ToArray());

    /// <summary>
    /// The extractor as it would behave on real prose: it reports the characters that are actually in
    /// the text it was given. That is the whole point of the coverage change, so the fake must not
    /// short-circuit it by answering the same thing for every chapter.
    /// </summary>
    private static string ExtractionByChapterText(string input)
    {
        var found = new List<(string, string)>();
        if (input.Contains("רונית")) found.Add(("רונית", "female"));
        if (input.Contains("מרים")) found.Add(("מרים", "female"));
        if (input.Contains("יעל")) found.Add(("יעל", "female"));
        if (input.Contains("הרוח")) found.Add(("הרוח", "unknown"));
        return found.Count == 0 ? "[]" : Payload(found.ToArray());
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One in-memory database, one router mock, and the ability to run an analysis on its OWN DI scope
    /// so a "concurrent request" really gets its own <see cref="AppDbContext"/>.
    ///
    /// <para>The database name is resolved ONCE, outside the options lambda, deliberately:
    /// <c>AddDbContext</c> registers its options SCOPED, so a Guid generated inside the lambda would
    /// give every scope its own store and a concurrency test would write to a different database
    /// entirely and prove nothing. Same trap already documented on
    /// <c>CharacterRegisterProvenanceTests.BuildProvider</c>.</para>
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _extractionPrompt;
        private readonly List<string> _extractionInputs = new();
        private readonly object _sync = new();

        public Harness(Func<string, string> extraction)
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var databaseName = Guid.NewGuid().ToString();
            services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(databaseName));
            services.AddSingleton<SfdtConversionService>();
            services.AddSingleton<PromptFactory>();

            var router = new Mock<IAiRouter>();
            services.AddSingleton(router.Object);
            services.Configure<AiOptions>(_ => { });
            services.AddScoped<ChapterBriefService>();
            services.AddScoped<BookSummaryService>();
            services.AddScoped<BookContextAssembler>();
            services.AddSingleton<AnalysisProgressTracker>();
            services.AddSingleton<BookSummaryBuildRegistry>();
            services.AddScoped<IAnalysisContextService, AnalysisContextService>();
            services.AddScoped<CharacterRegisterService>();

            _provider = services.BuildServiceProvider();
            _extractionPrompt = _provider.GetRequiredService<PromptFactory>().GetCharacterExtractionPrompt("he");

            router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .Returns<AiRequest, CancellationToken>(async (request, _) =>
                {
                    if (request.Instruction != _extractionPrompt)
                        return new AiResponse { Content = "", Model = "test", Provider = "test" };

                    var input = request.InputText ?? "";
                    lock (_sync) _extractionInputs.Add(input);

                    var during = DuringExtraction;
                    if (during != null) await during(input);

                    var handler = ExtractionOverride ?? extraction;
                    return new AiResponse { Content = handler(input), Model = "test", Provider = "test" };
                });
        }

        /// <summary>Replaces the extractor's answer for the rest of the test.</summary>
        public Func<string, string>? ExtractionOverride { get; set; }

        /// <summary>Runs INSIDE the extraction call, which is how a concurrent request is placed at an
        /// exact point in this request's lifetime without threads.</summary>
        public Func<string, Task>? DuringExtraction { get; set; }

        /// <summary>Every character-extraction input, in call order. The non-vacuity instrument.</summary>
        public IReadOnlyList<string> ExtractionInputs
        {
            get { lock (_sync) return _extractionInputs.ToList(); }
        }

        public int ExtractionCount
        {
            get { lock (_sync) return _extractionInputs.Count; }
        }

        public async Task<(Guid BookId, Guid Chapter1, Guid Chapter33)> SeedTwoChapterBookAsync()
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var bookId = Guid.NewGuid();
            var chapter1 = Guid.NewGuid();
            var chapter33 = Guid.NewGuid();

            db.Books.Add(new Book { Id = bookId, Title = "ספר", Language = "he" });
            db.Chapters.Add(new Chapter
            {
                Id = chapter1,
                BookId = bookId,
                Title = "פרק 1",
                Order = 1,
                ContentText = Chapter1Text,
                UpdatedAt = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero)
            });
            db.Chapters.Add(new Chapter
            {
                Id = chapter33,
                BookId = bookId,
                Title = "פרק 33",
                Order = 33,
                ContentText = Chapter33Text,
                UpdatedAt = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero)
            });
            await db.SaveChangesAsync();

            return (bookId, chapter1, chapter33);
        }

        /// <summary>
        /// A scene inside a chapter, whose OWN prose is <paramref name="text"/>. Deliberately different
        /// from the chapter's ContentText, so "which text did the extractor see" is answerable.
        /// </summary>
        public async Task<Guid> SeedSceneAsync(Guid chapterId, string text)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var sceneId = Guid.NewGuid();
            db.Scenes.Add(new Scene
            {
                Id = sceneId,
                ChapterId = chapterId,
                Title = "סצנה",
                Order = 0,
                ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText(text)
            });
            await db.SaveChangesAsync();
            return sceneId;
        }

        public async Task<AnalysisContext> AnalyseSceneAsync(Guid sceneId)
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
                AnalysisScope.Scene, sceneId, AnalysisType.Proofread, "he", CancellationToken.None);
        }

        public async Task<AnalysisContext> AnalyseAsync(Guid chapterId)
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
                AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);
        }

        public async Task<AnalysisContext> AnalyseBookAsync(Guid bookId)
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
                AnalysisScope.Book, bookId, AnalysisType.Proofread, "he", CancellationToken.None);
        }

        public async Task ApplyAuthorEditsAsync(Guid bookId, params CharacterRegisterEditDto[] edits)
        {
            using var scope = _provider.CreateScope();
            var (result, error) = await scope.ServiceProvider.GetRequiredService<CharacterRegisterService>()
                .ApplyEditsAsync(bookId, new UpdateCharacterRegisterRequest(edits), CancellationToken.None);
            Assert.Null(error);
            Assert.NotNull(result);
        }

        /// <summary>Rewrites a chapter's prose and moves its UpdatedAt, exactly as a save does.</summary>
        public async Task RewriteChapterAsync(Guid chapterId, string text)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var chapter = await db.Chapters.FirstAsync(c => c.Id == chapterId);
            chapter.ContentText = text;
            chapter.UpdatedAt = chapter.UpdatedAt.AddDays(1);
            await db.SaveChangesAsync();
        }

        public async Task<DateTimeOffset> ChapterUpdatedAtAsync(Guid chapterId)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (await db.Chapters.AsNoTracking().FirstAsync(c => c.Id == chapterId)).UpdatedAt;
        }

        /// <summary>
        /// The AUTHOR-FACING coverage report, read through the real service the GET endpoint uses, so
        /// nothing about the numbers is re-implemented in this file.
        /// </summary>
        public async Task<CharacterRegisterCoverageDto> CoverageAsync(Guid bookId)
        {
            using var scope = _provider.CreateScope();
            var dto = await scope.ServiceProvider.GetRequiredService<CharacterRegisterService>()
                .GetAsync(bookId, CancellationToken.None);
            Assert.NotNull(dto);
            return dto!.Coverage;
        }

        public async Task<CharacterRegister> ReadRegisterAsync(Guid bookId)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.BookBibles.AsNoTracking().SingleAsync(b => b.BookId == bookId);
            Assert.True(
                CharacterRegisterService.TryDeserialize(row.CharacterRegisterJson, out var register, out _)
                && register is not null,
                "The persisted register column did not deserialize; every assertion against it would be vacuous.");
            return register!;
        }

        public async Task<int> CountBibleRowsAsync(Guid bookId)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.BookBibles.AsNoTracking().CountAsync(b => b.BookId == bookId);
        }

        public void Dispose() => _provider.Dispose();
    }
}
