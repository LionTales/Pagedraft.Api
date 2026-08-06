using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The author's edit surface: GET /api/books/{bookId}/character-register and PATCH on the same route
/// (character-register-editing plan, c1), plus the d1 §4 invalidation stamp as the analysis-result
/// DTO reports it.
///
/// The endpoints are the contract the client (c2) builds against, so the WIRE shape — including the
/// System.Text.Json default camelCase — is asserted here, not just the C# objects.
/// </summary>
public class CharacterRegisterEndpointTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"charreg-endpoint-{Guid.NewGuid()}").Options);

    private static CharacterRegisterController NewController(AppDbContext db) =>
        new(new CharacterRegisterService(db, NullLogger<CharacterRegisterService>.Instance));

    private static async Task<Guid> SeedBookAsync(AppDbContext db, string? registerJson = null)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Register Book", Language = "he" });
        if (registerJson != null)
            db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = registerJson });
        await db.SaveChangesAsync();
        return bookId;
    }

    private static CharacterRegisterDto Ok(ActionResult<CharacterRegisterDto> result)
        => Assert.IsType<CharacterRegisterDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static Task<ActionResult<CharacterRegisterDto>> PatchAsync(
        CharacterRegisterController controller, Guid bookId, params CharacterRegisterEditDto[] edits)
        => controller.Patch(bookId, new UpdateCharacterRegisterRequest(edits), CancellationToken.None);

    // ── GET ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_UnknownBook_Is404()
    {
        using var db = NewDb();
        var result = await NewController(db).Get(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_BookWithNoRegisterYet_Is200_WithHasRegisterFalse()
    {
        // The EMPTY STATE. A book whose register has never been extracted is not a 404 and is not the
        // same as "this book has no characters" — the client has to be able to tell them apart to
        // explain that the register is built on the first analysis run.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var dto = Ok(await NewController(db).Get(bookId, CancellationToken.None));

        Assert.False(dto.HasRegister);
        Assert.Empty(dto.Characters);
        Assert.Null(dto.UpdatedAt);
    }

    [Fact]
    public async Task Get_ReturnsProvenance_SoTheUiCanTellConfirmedFromGuessed()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            UpdatedAt = new DateTimeOffset(2026, 8, 5, 8, 0, 0, TimeSpan.Zero),
            Characters = new[]
            {
                new CharacterRegisterEntry { Name = "רונית", Gender = "female", GenderConfirmed = true },
                new CharacterRegisterEntry { Name = "אלון", Gender = "male" }
            }
        }));

        var dto = Ok(await NewController(db).Get(bookId, CancellationToken.None));

        Assert.True(dto.HasRegister);
        Assert.NotNull(dto.UpdatedAt);
        Assert.True(dto.Characters.Single(c => c.Name == "רונית").GenderConfirmed);
        Assert.False(dto.Characters.Single(c => c.Name == "אלון").GenderConfirmed);
    }

    [Fact]
    public async Task Get_IncludesSuppressedEntries_SoTheyCanBeRestored()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[]
            {
                new CharacterRegisterEntry { Name = "הרוח", IsCharacter = false, IsCharacterConfirmed = true }
            }
        }));

        var entry = Ok(await NewController(db).Get(bookId, CancellationToken.None)).Characters.Single();

        Assert.False(entry.IsCharacter);
        Assert.True(entry.IsCharacterConfirmed);
    }

    // ── Duplicate entries in a legacy register (fix-plan c02) ───────────────────────────────────
    //
    // Every register written before provenance came straight from JsonSerializer.Serialize(extracted)
    // with no de-duplication of ENTRIES, so one character appearing twice is reachable on real books.
    // Seeded as a LITERAL string in the PascalCase the column actually holds, not built from the
    // record: a legacy payload is what this pair is about, and constructing it in C# would quietly
    // acquire whatever defaults the record grows.

    private const string LegacyDuplicateRegisterJson = """
        {
          "Characters": [
            { "Name": "דנה", "Gender": "female", "GenderConfirmed": true, "Aliases": ["דני"] },
            { "Name": " דנה ", "Role": "protagonist", "Aliases": ["דנצ'י"], "AliasesConfirmed": true, "IsAuthorAdded": true }
          ]
        }
        """;

    [Fact]
    public async Task Get_LegacyRegisterHoldingOneCharacterTwice_ReturnsOneRow_CarryingBothCopiesAuthorState()
    {
        // Two rows for one character broke the surface at both ends at once: the client tracked rows
        // by name (Angular NG0955 on every change-detection pass, and one Edit click opening the form
        // on both rows), and server-side every edit resolved to the FIRST occurrence forever.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, LegacyDuplicateRegisterJson);

        var dto = Ok(await NewController(db).Get(bookId, CancellationToken.None));

        var entry = Assert.Single(dto.Characters);
        Assert.Equal("דנה", entry.Name);
        // The UNION of the two copies' author state. A confirmation or an author-added marker on
        // EITHER duplicate has to survive onto the kept entry.
        Assert.True(entry.GenderConfirmed);
        Assert.True(entry.AliasesConfirmed);
        Assert.True(entry.IsAuthorAdded);
        Assert.Equal("female", entry.Gender);
        Assert.Equal("protagonist", entry.Role);
        Assert.Equal(new[] { "דני", "דנצ'י" }, entry.Aliases);
    }

    [Fact]
    public async Task Patch_AgainstALegacyDuplicate_ReachesTheSurvivingEntry_AndPersistsTheCollapse()
    {
        // The defect this closes: ApplyOne locates its target with FindIndex, so on a duplicate every
        // edit landed on the first occurrence and the second row could never be corrected while the
        // UI reported the save succeeded.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, LegacyDuplicateRegisterJson);
        var controller = NewController(db);

        var patched = Ok(await PatchAsync(controller, bookId,
            new CharacterRegisterEditDto("דנה", "upsert", "male", null)));

        var entry = Assert.Single(patched.Characters);
        Assert.Equal("male", entry.Gender);
        Assert.True(entry.GenderConfirmed);
        // Still the union: the edit must not cost the OTHER copy's author state.
        Assert.True(entry.AliasesConfirmed);
        Assert.True(entry.IsAuthorAdded);

        // And the collapse is PERSISTED, not just projected: a fresh read of the column holds one entry.
        var stored = await db.BookBibles.AsNoTracking()
            .Where(b => b.BookId == bookId).Select(b => b.CharacterRegisterJson).FirstAsync();
        Assert.True(CharacterRegisterService.TryDeserialize(stored, out var register, out _));
        var persisted = Assert.Single(register!.Characters);
        Assert.Equal("male", persisted.Gender);
    }

    [Fact]
    public async Task Get_TwoConfirmedGendersOnOneCharacter_KeepsTheFirstOccurrencesValue()
    {
        // The deliberate conflict decision (argued in full at CharacterRegisterMerge.CollapseDuplicate):
        // provenance is per FIELD but the only timestamp is per REGISTER, so nothing says which
        // confirmation came later and "most recent wins" is not available. First-in-order is what
        // ApplyOne's FindIndex already resolved to, so it changes the fewest observable answers.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, """
            {
              "Characters": [
                { "Name": "דנה", "Gender": "female", "GenderConfirmed": true },
                { "Name": "דנה", "Gender": "male", "GenderConfirmed": true }
              ]
            }
            """);

        var entry = Assert.Single(Ok(await NewController(db).Get(bookId, CancellationToken.None)).Characters);

        Assert.Equal("female", entry.Gender);
        // The losing value goes; the confirmation stays, and the single surviving row is now editable.
        Assert.True(entry.GenderConfirmed);
    }

    [Fact]
    public async Task Get_UnreadableRegister_DegradesToTheEmptyState_WithoutThrowing()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "{ not json");

        var dto = Ok(await NewController(db).Get(bookId, CancellationToken.None));

        Assert.False(dto.HasRegister);
        Assert.Empty(dto.Characters);
    }

    [Fact]
    public void Get_WireShape_IsCamelCase()
    {
        // The API calls AddControllers() with no naming-policy override, so the wire contract c2 codes
        // against is System.Text.Json's web default. Pin the exact property names.
        var dto = new CharacterRegisterDto(
            Guid.Empty,
            HasRegister: true,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            Characters: new[]
            {
                new CharacterRegisterEntryDto("רונית", "female", "protagonist", "d", new[] { "רוני" }, true, false, true, false, false)
            },
            Coverage: new CharacterRegisterCoverageDto(40, 3, 35, 1, 1, false, DateTimeOffset.UnixEpoch));

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var key in new[]
                 {
                     "\"bookId\"", "\"hasRegister\"", "\"updatedAt\"", "\"characters\"",
                     "\"name\"", "\"gender\"", "\"role\"", "\"description\"", "\"aliases\"",
                     "\"isCharacter\"", "\"isAuthorAdded\"", "\"genderConfirmed\"",
                     "\"aliasesConfirmed\"", "\"isCharacterConfirmed\"",
                     // be-c03's coverage summary, the contract c01's quiet line of fact reads.
                     "\"coverage\"", "\"totalChapters\"", "\"coveredChapters\"", "\"pendingChapters\"",
                     "\"staleChapters\"", "\"unscannableChapters\"", "\"isComplete\"", "\"lastScannedAt\""
                 })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }
    }

    // ── Coverage (automatic-coverage plan, be-c03) ──────────────────────────────────────────────
    //
    // Coverage is automatic and invisible - each chapter contributes the first time an analysis that
    // reads the register (PromptFactory.RendersCharacterRegister: Proofread, LiteraryAnalysis, QA,
    // Synopsis) runs against it, one chapter per such analysis - so the only thing standing between
    // the author and a register silently built from 3 of 40 chapters is this report. Every count below
    // has to come from the SAME
    // persisted ledger the scan path writes (CharacterRegister.ScannedChapters) through the SAME
    // predicate it re-scans on (CharacterRegisterCoverage.IsCoveredAndFresh); the end-to-end proof
    // that the reporter and the writer agree lives in CharacterRegisterCoverageTests, where a real
    // scan is run and then reported.

    private static readonly DateTimeOffset ScanStamp = new(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A seeded chapter and the <c>UpdatedAt</c> the DATABASE actually gave it.</summary>
    private sealed record SeededChapter(Guid Id, DateTimeOffset UpdatedAt);

    private static async Task<SeededChapter> AddChapterAsync(AppDbContext db, Guid bookId, int order, string content)
    {
        var chapter = new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Title = $"פרק {order}",
            Order = order,
            ContentText = content
        };
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        // AppDbContext's SaveChanges override stamps Chapter.UpdatedAt itself, so a value assigned here
        // would be silently replaced. The ledger fixtures below are therefore built from the PERSISTED
        // stamp, which is also what the scan path does - it reads the chapter's UpdatedAt back rather
        // than assuming one. A test that made a stamp up would classify every chapter as stale and the
        // "covered" assertions would be unreachable.
        return new SeededChapter(chapter.Id, chapter.UpdatedAt);
    }

    /// <summary>A ledger line that is COVERED-AND-FRESH: its stamp matches the chapter's current text.</summary>
    private static ScannedChapterEntry Scanned(SeededChapter chapter) => new()
    {
        ChapterId = chapter.Id,
        ScannedAt = ScanStamp,
        SourceStamp = chapter.UpdatedAt
    };

    /// <summary>A ledger line left behind by a scan that happened BEFORE the chapter's current text.</summary>
    private static ScannedChapterEntry ScannedBeforeAnEdit(SeededChapter chapter) => new()
    {
        ChapterId = chapter.Id,
        ScannedAt = ScanStamp,
        SourceStamp = chapter.UpdatedAt.AddDays(-1)
    };

    /// <summary>A ledger line for a chapter that no longer exists.</summary>
    private static ScannedChapterEntry ScannedOrphan() => new()
    {
        ChapterId = Guid.NewGuid(),
        ScannedAt = ScanStamp,
        SourceStamp = ScanStamp
    };

    private static string RegisterWithLedger(params ScannedChapterEntry[] ledger)
        => CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "רונית" } },
            ScannedChapters = ledger
        });

    private static async Task<CharacterRegisterCoverageDto> CoverageAsync(AppDbContext db, Guid bookId)
        => Ok(await NewController(db).Get(bookId, CancellationToken.None)).Coverage;

    private const string WatermarkOnlyChapter =
        "Created with a trial version of Syncfusion Word library. Visit the site to obtain the valid key.";

    [Fact]
    public async Task Get_Coverage_CountsTheChaptersTheLedgerRecorded_AndReportsTheRestAsPending()
    {
        // The honest headline: 1 of 3, not "the register exists" (which is all the surface could say
        // before) and not 3 of 3 (which is what a count derived from the character list would imply).
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var chapter1 = await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");
        await AddChapterAsync(db, bookId, 2, "מרים ישבה על הספסל.");
        await AddChapterAsync(db, bookId, 3, "יעל הגיעה מאוחר.");

        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = RegisterWithLedger(Scanned(chapter1)) });
        await db.SaveChangesAsync();

        var coverage = await CoverageAsync(db, bookId);

        Assert.Equal(3, coverage.TotalChapters);
        Assert.Equal(1, coverage.CoveredChapters);
        Assert.Equal(2, coverage.PendingChapters);
        Assert.Equal(0, coverage.StaleChapters);
        Assert.Equal(0, coverage.UnscannableChapters);
        Assert.False(coverage.IsComplete);
        Assert.Equal(ScanStamp, coverage.LastScannedAt);
    }

    [Fact]
    public async Task Get_Coverage_AChapterEditedSinceItsScan_IsStale_NotCovered()
    {
        // The re-scan key is the chapter's text version, so an edited chapter has to stop counting as
        // covered the moment it is saved - otherwise the report claims the register reflects prose no
        // extraction has ever read.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var edited = await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");

        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = RegisterWithLedger(ScannedBeforeAnEdit(edited)) });
        await db.SaveChangesAsync();

        var coverage = await CoverageAsync(db, bookId);

        Assert.Equal(1, coverage.TotalChapters);
        Assert.Equal(0, coverage.CoveredChapters);
        Assert.Equal(1, coverage.StaleChapters);
        Assert.Equal(0, coverage.PendingChapters);
        Assert.False(coverage.IsComplete);
        // It DID contribute once, and saying so is the difference between "re-contributes on its next
        // analysis" and "has never been read".
        Assert.Equal(ScanStamp, coverage.LastScannedAt);
    }

    [Fact]
    public async Task Get_Coverage_AnEmptyChapterIsReportedUnscannable_SoCompleteIsReachable()
    {
        // THE PREDICATE-SATISFIABILITY POINT. An empty chapter is refused by the analysis pipeline
        // outright ("No chapter text to analyze"), so it can never enter the ledger. Counted as
        // outstanding it would hold the book at 1 of 2 forever and the author would be told coverage
        // is still growing when nothing can grow it.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var written = await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");
        await AddChapterAsync(db, bookId, 2, "   ");

        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = RegisterWithLedger(Scanned(written)) });
        await db.SaveChangesAsync();

        var coverage = await CoverageAsync(db, bookId);

        Assert.Equal(2, coverage.TotalChapters);
        Assert.Equal(1, coverage.CoveredChapters);
        Assert.Equal(1, coverage.UnscannableChapters);
        // Named, not silently folded into covered: nothing was read, so nothing may be claimed.
        Assert.Equal(0, coverage.PendingChapters);
        Assert.Equal(0, coverage.StaleChapters);
        Assert.True(coverage.IsComplete);
    }

    [Fact]
    public async Task Get_Coverage_AWatermarkOnlyChapterIsUnscannable_NotPending()
    {
        // "Blank" has to mean what the analysis path means by it: the Syncfusion trial watermark is
        // stripped before any text is read, so a chapter holding only a watermark is non-blank in the
        // column and empty to every analysis. A cheaper IsNullOrWhiteSpace(ContentText) test would file
        // it under pending and never let this book reach complete.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        await AddChapterAsync(db, bookId, 1, WatermarkOnlyChapter);

        var coverage = await CoverageAsync(db, bookId);

        Assert.Equal(1, coverage.UnscannableChapters);
        Assert.Equal(0, coverage.PendingChapters);
        Assert.True(coverage.IsComplete);
    }

    [Fact]
    public async Task Get_Coverage_AChapterEmptiedAfterItContributed_IsUnscannable_NotStaleForever()
    {
        // The precedence that matters: emptying a chapter moves its UpdatedAt, so its ledger line goes
        // stale AND its content becomes unreadable. Left in STALE it would be permanent outstanding
        // work that no analysis can ever clear.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var emptied = await AddChapterAsync(db, bookId, 1, "");

        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = RegisterWithLedger(ScannedBeforeAnEdit(emptied)) });
        await db.SaveChangesAsync();

        var coverage = await CoverageAsync(db, bookId);

        Assert.Equal(1, coverage.UnscannableChapters);
        Assert.Equal(0, coverage.StaleChapters);
        Assert.Equal(0, coverage.CoveredChapters);
        Assert.True(coverage.IsComplete);
    }

    [Fact]
    public async Task Get_Coverage_BucketsSumToTotal_AcrossAllFourStates()
    {
        // The exhaustiveness invariant, asserted on a book that hits every bucket at once. A chapter
        // that fell through the classification would be invisible in the sum, and the author would be
        // told the book has fewer chapters than it does.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var covered = await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");
        var stale = await AddChapterAsync(db, bookId, 2, "מרים ישבה על הספסל.");
        await AddChapterAsync(db, bookId, 3, "יעל הגיעה מאוחר.");
        await AddChapterAsync(db, bookId, 4, "");

        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = RegisterWithLedger(Scanned(covered), ScannedBeforeAnEdit(stale))
        });
        await db.SaveChangesAsync();

        var c = await CoverageAsync(db, bookId);

        Assert.Equal(4, c.TotalChapters);
        Assert.Equal(1, c.CoveredChapters);
        Assert.Equal(1, c.StaleChapters);
        Assert.Equal(1, c.PendingChapters);
        Assert.Equal(1, c.UnscannableChapters);
        Assert.Equal(
            c.TotalChapters,
            c.CoveredChapters + c.PendingChapters + c.StaleChapters + c.UnscannableChapters);
        Assert.False(c.IsComplete);
    }

    // ── The two-phase coverage read (fix-plan be-c01) ────────────────────────────────────────────
    //
    // BuildCoverageAsync used to project ContentText for EVERY chapter of the book, on every register
    // GET and on every PATCH response - on an 80-chapter book, the whole manuscript pulled into memory
    // to render one dashboard card, twice per panel open. It now projects {Id, UpdatedAt} for every
    // chapter and fetches ContentText only for the chapters whose bucket that text can still change.
    //
    // WHY THE FIRST TWO TESTS DRIVE CharacterRegisterCoverage.SummarizeAsync DIRECTLY, INSTEAD OF THE
    // ENDPOINT. The optimisation is exact BY CONSTRUCTION: a covered-and-fresh chapter is classified
    // before IsScannable is ever reached, so no reported number can differ between reading that
    // chapter's text and not reading it. That is the whole point of the change - and it is also why
    // "the totals are the same" is a vacuous assertion here, since it passes identically against the
    // old code and the new. The only observer that can tell the two apart is the read itself, so these
    // two assert on the loader: whether it was called at all, and which chapter ids it was given.
    // Against the old code neither assertion could even be written; there was no loader, and every
    // chapter's prose was fetched unconditionally.

    /// <summary>
    /// A phase-2 fetch that answers from a fixture and REMEMBERS what it was asked for. The Calls list
    /// is the instrument: empty means no chapter text was read at all.
    /// </summary>
    private sealed class RecordingContentTextLoader
    {
        private readonly IReadOnlyDictionary<Guid, string?> _text;

        public RecordingContentTextLoader(IReadOnlyDictionary<Guid, string?> text) => _text = text;

        /// <summary>Every phase-2 fetch, in call order, with the exact id set each was given.</summary>
        public List<IReadOnlyCollection<Guid>> Calls { get; } = new();

        public Task<IReadOnlyDictionary<Guid, string?>> LoadAsync(
            IReadOnlyCollection<Guid> chapterIds, CancellationToken ct)
        {
            Calls.Add(chapterIds.ToList());
            return Task.FromResult<IReadOnlyDictionary<Guid, string?>>(
                chapterIds.ToDictionary(id => id, id => _text.TryGetValue(id, out var t) ? t : null));
        }
    }

    private static CharacterRegisterCoverage.ChapterVersion NewChapterVersion(int daysOld = 0)
        => new(Guid.NewGuid(), ScanStamp.AddDays(-daysOld));

    private static CharacterRegister RegisterWithEntries(params ScannedChapterEntry[] ledger)
        => new()
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "רונית" } },
            ScannedChapters = ledger
        };

    private static ScannedChapterEntry Fresh(CharacterRegisterCoverage.ChapterVersion chapter)
        => new() { ChapterId = chapter.ChapterId, ScannedAt = ScanStamp, SourceStamp = chapter.UpdatedAt };

    private static ScannedChapterEntry Outdated(CharacterRegisterCoverage.ChapterVersion chapter)
        => new() { ChapterId = chapter.ChapterId, ScannedAt = ScanStamp, SourceStamp = chapter.UpdatedAt.AddDays(-1) };

    [Fact]
    public async Task Coverage_OnAFullyCoveredBook_ReadsNoChapterTextAtAll()
    {
        // THE COST FIX ITSELF. Every chapter has a fresh ledger line, so nothing the prose could say
        // can move a single number - and the read must therefore not go and get it.
        var chapters = new[] { NewChapterVersion(), NewChapterVersion(), NewChapterVersion() };
        var loader = new RecordingContentTextLoader(new Dictionary<Guid, string?>());

        var coverage = await CharacterRegisterCoverage.SummarizeAsync(
            RegisterWithEntries(chapters.Select(Fresh).ToArray()),
            chapters,
            loader.LoadAsync,
            CancellationToken.None);

        // THE DISCRIMINATING ASSERTION: not one chapter's prose was fetched. Not "a smaller query" -
        // no query at all, because the id set phase 1 produced was empty and the fetch is skipped
        // rather than issued with an empty filter.
        Assert.Empty(loader.Calls);

        // NON-VACUITY: the answer is still the full, correct one, so the assertion above is not passing
        // because the fixture classified nothing or the register failed to bind.
        Assert.Equal(3, coverage.TotalChapters);
        Assert.Equal(3, coverage.CoveredChapters);
        Assert.Equal(0, coverage.PendingChapters);
        Assert.Equal(0, coverage.StaleChapters);
        Assert.Equal(0, coverage.UnscannableChapters);
        Assert.True(coverage.IsComplete);
    }

    [Fact]
    public async Task Coverage_FetchesChapterTextForExactlyTheChaptersTheLedgerMissed()
    {
        // The other half of exactness. Deferring too much would be a wrong ANSWER (a pending chapter
        // with no text loaded reads as unscannable); deferring too little would be the cost this fix
        // exists to remove. So the assertion is the exact id set, in ONE round trip.
        var covered = NewChapterVersion();
        var stale = NewChapterVersion();
        var pending = NewChapterVersion();

        var loader = new RecordingContentTextLoader(new Dictionary<Guid, string?>
        {
            [covered.ChapterId] = "רונית פתחה את הדלת.",
            [stale.ChapterId] = "מרים ישבה על הספסל.",
            [pending.ChapterId] = "יעל הגיעה מאוחר."
        });

        var coverage = await CharacterRegisterCoverage.SummarizeAsync(
            RegisterWithEntries(Fresh(covered), Outdated(stale)),
            new[] { covered, stale, pending },
            loader.LoadAsync,
            CancellationToken.None);

        var fetched = Assert.Single(loader.Calls);
        Assert.Equal(
            new[] { stale.ChapterId, pending.ChapterId }.OrderBy(id => id),
            fetched.OrderBy(id => id));
        Assert.DoesNotContain(covered.ChapterId, fetched);

        // NON-VACUITY, and the reason the id set has to be exactly this one: the two chapters whose
        // text WAS fetched land in buckets that are only reachable when real text arrived under the
        // right key. A missing or mis-keyed fetch would file both under unscannable.
        Assert.Equal(1, coverage.CoveredChapters);
        Assert.Equal(1, coverage.StaleChapters);
        Assert.Equal(1, coverage.PendingChapters);
        Assert.Equal(0, coverage.UnscannableChapters);
    }

    [Fact]
    public async Task Get_Coverage_ACoveredChapterWhoseTextIsUnreadable_StaysCovered()
    {
        // THE INVARIANT THE DEFERRED LOAD RESTS ON, pinned deliberately. Summarize answers COVERED
        // before it ever asks IsScannable, which is the only reason a covered chapter's text can be
        // left unread. Swap those two precedence rules and this book silently reports 0 covered / 1
        // unscannable - and worse, it would be reclassifying against a null that was never fetched.
        //
        // The state is synthetic: through the app, replacing a chapter's text also moves its UpdatedAt,
        // which sends the line stale (see AChapterEmptiedAfterItContributed above). This test is not
        // claiming the state is reachable - it is holding the branch ORDER still, because the two-phase
        // read now depends on it and nothing else would fail if it changed.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var covered = await AddChapterAsync(db, bookId, 1, WatermarkOnlyChapter);

        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = RegisterWithLedger(Scanned(covered)) });
        await db.SaveChangesAsync();

        var coverage = await CoverageAsync(db, bookId);

        Assert.Equal(1, coverage.CoveredChapters);
        Assert.Equal(0, coverage.UnscannableChapters);
        Assert.Equal(0, coverage.PendingChapters);
        Assert.Equal(0, coverage.StaleChapters);
        Assert.True(coverage.IsComplete);
    }

    [Fact]
    public async Task Get_Coverage_ThroughTheRealService_StillSeparatesAllFourBuckets_IncludingUnscannableAfterARealScan()
    {
        // THE WIRING PROOF for the reshaped query, end to end through the controller rather than
        // against a hand-supplied loader. It is the PENDING and STALE counts that carry it: a chapter
        // whose ContentText did not arrive from phase 2 - never fetched, or fetched and returned under
        // the wrong key - reads as having no text and is filed UNSCANNABLE. So a book that still
        // reports one pending and one stale is a book whose second query ran, was scoped to the right
        // chapters, and was joined back correctly.
        //
        // It also carries the unscannable-after-a-real-scan case (chapter 5) inside the same mixed
        // book, which the existing four-state test does not: that chapter has a ledger line AND
        // unreadable text, so it exercises the precedence rule in the direction where UNSCANNABLE must
        // beat STALE, alongside the direction where COVERED must beat UNSCANNABLE.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var covered = await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");
        var stale = await AddChapterAsync(db, bookId, 2, "מרים ישבה על הספסל.");
        await AddChapterAsync(db, bookId, 3, "יעל הגיעה מאוחר.");
        await AddChapterAsync(db, bookId, 4, WatermarkOnlyChapter);
        var emptiedAfterAScan = await AddChapterAsync(db, bookId, 5, "");

        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = RegisterWithLedger(
                Scanned(covered), ScannedBeforeAnEdit(stale), ScannedBeforeAnEdit(emptiedAfterAScan))
        });
        await db.SaveChangesAsync();

        var c = await CoverageAsync(db, bookId);

        Assert.Equal(5, c.TotalChapters);
        Assert.Equal(1, c.CoveredChapters);
        Assert.Equal(1, c.StaleChapters);
        Assert.Equal(1, c.PendingChapters);
        Assert.Equal(2, c.UnscannableChapters);
        Assert.Equal(
            c.TotalChapters,
            c.CoveredChapters + c.PendingChapters + c.StaleChapters + c.UnscannableChapters);
        Assert.False(c.IsComplete);

        // The watermark-only chapter is the one that would regress most quietly: it is non-blank in the
        // column, so a phase-2 fetch that silently returned nothing for it, and an IsNullOrWhiteSpace
        // shortcut, both land it in the same bucket for the wrong reason. Its neighbours above are what
        // make the count mean something.
        Assert.Equal(ScanStamp, c.LastScannedAt);
    }

    [Fact]
    public async Task Get_Coverage_OnTheNeverBuiltEmptyState_ReportsEveryChapterOutstanding()
    {
        // The empty state gains real content: not just "no characters" but "none of your 2 chapters
        // has been analysed yet", which is the sentence that explains what to do about it.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");
        await AddChapterAsync(db, bookId, 2, "מרים ישבה על הספסל.");

        var dto = Ok(await NewController(db).Get(bookId, CancellationToken.None));

        Assert.False(dto.HasRegister);
        Assert.Equal(2, dto.Coverage.TotalChapters);
        Assert.Equal(0, dto.Coverage.CoveredChapters);
        Assert.Equal(2, dto.Coverage.PendingChapters);
        Assert.False(dto.Coverage.IsComplete);
        Assert.Null(dto.Coverage.LastScannedAt);
    }

    [Fact]
    public async Task Get_Coverage_OnAnUnreadableRegister_ReportsEverythingOutstanding_RatherThanNothing()
    {
        // The degraded path still answers the question. The ledger is inside the value that would not
        // parse, so nothing is known to have contributed - and the field is still there, so the client
        // never has to handle a missing coverage object.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "{ not json");
        await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");

        var dto = Ok(await NewController(db).Get(bookId, CancellationToken.None));

        Assert.False(dto.HasRegister);
        Assert.Equal(1, dto.Coverage.TotalChapters);
        Assert.Equal(1, dto.Coverage.PendingChapters);
    }

    [Fact]
    public async Task Get_Coverage_IgnoresLedgerLinesForChaptersThatNoLongerExist()
    {
        // The walk goes chapters -> ledger, never ledger -> count. A deleted chapter leaves its line
        // behind (the ledger is never pruned, by design), and counting lines instead of chapters would
        // let those orphans report coverage the book does not have - the classic shape of a status
        // count that disagrees with what built it.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");

        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = RegisterWithLedger(ScannedOrphan(), ScannedOrphan())
        });
        await db.SaveChangesAsync();

        var coverage = await CoverageAsync(db, bookId);

        Assert.Equal(1, coverage.TotalChapters);
        Assert.Equal(0, coverage.CoveredChapters);
        Assert.Equal(1, coverage.PendingChapters);
        // ...and no stamp either: an orphan line is never visited at all.
        Assert.Null(coverage.LastScannedAt);
    }

    [Fact]
    public async Task Get_Coverage_ABookWithNoChaptersYet_IsNotComplete()
    {
        // Zero of zero is not "the register reflects the whole book". There is nothing to reflect yet,
        // and telling the author it is complete would be the one reading that is certainly wrong.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var coverage = await CoverageAsync(db, bookId);

        Assert.Equal(0, coverage.TotalChapters);
        Assert.False(coverage.IsComplete);
    }

    [Fact]
    public async Task Get_Coverage_IsScopedToThisBook()
    {
        // A neighbouring book's chapters must not inflate or dilute this book's denominator.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");
        var otherBookId = await SeedBookAsync(db);
        await AddChapterAsync(db, otherBookId, 1, "מרים ישבה על הספסל.");
        await AddChapterAsync(db, otherBookId, 2, "יעל הגיעה מאוחר.");

        Assert.Equal(1, (await CoverageAsync(db, bookId)).TotalChapters);
        Assert.Equal(2, (await CoverageAsync(db, otherBookId)).TotalChapters);
    }

    [Fact]
    public async Task Patch_ResponseCarriesTheCoverage_ItDidNotChange()
    {
        // The client replaces its whole register state from the PATCH response, so coverage has to be
        // on it and has to be the real thing: an author edit neither advances nor retreats coverage
        // (this writer carries the ledger through untouched), and a zeroed or omitted value here would
        // collapse the line to "0 of 2" on every save and silently recover on the next GET.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var scanned = await AddChapterAsync(db, bookId, 1, "רונית פתחה את הדלת.");
        await AddChapterAsync(db, bookId, 2, "מרים ישבה על הספסל.");

        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = RegisterWithLedger(Scanned(scanned)) });
        await db.SaveChangesAsync();

        var patched = Ok(await PatchAsync(NewController(db), bookId,
            new CharacterRegisterEditDto("רונית", "upsert", Gender: "female")));

        // NON-VACUITY: the edit really landed, so the column really was rewritten by the other writer.
        Assert.True(patched.Characters.Single(c => c.Name == "רונית").GenderConfirmed);

        Assert.Equal(2, patched.Coverage.TotalChapters);
        Assert.Equal(1, patched.Coverage.CoveredChapters);
        Assert.Equal(1, patched.Coverage.PendingChapters);
        Assert.Equal(ScanStamp, patched.Coverage.LastScannedAt);
    }

    // ── PATCH ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_UnknownBook_Is404()
    {
        using var db = NewDb();
        var result = await PatchAsync(NewController(db), Guid.NewGuid(), new CharacterRegisterEditDto("רונית"));
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Patch_EditWithoutAName_Is400(string? name)
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);
        var result = await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto(name));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Patch_UnknownOp_Is400_AndIsNotSilentlyTreatedAsUpsert()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var result = await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("רונית", Op: "delete"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        // ...and nothing was written: a rejected batch must not half-apply.
        Assert.Null(await db.BookBibles.FirstOrDefaultAsync(b => b.BookId == bookId));
    }

    [Fact]
    public async Task Patch_EmptyBatch_Is400()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var result = await NewController(db).Patch(bookId, new UpdateCharacterRegisterRequest(Array.Empty<CharacterRegisterEditDto>()), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Patch_SetGender_ConfirmsIt_AndStampsTheRegister()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "רונית", Gender = "male" } }
        }));

        var dto = Ok(await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("רונית", Gender: "female")));

        var entry = dto.Characters.Single();
        Assert.Equal("female", entry.Gender);
        Assert.True(entry.GenderConfirmed);
        Assert.NotNull(dto.UpdatedAt);

        // Persisted, not just returned — and asserted by re-reading the register rather than by a
        // substring probe on the column, which would pass on any payload merely containing the word.
        var stored = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        Assert.True(CharacterRegisterService.TryDeserialize(stored.CharacterRegisterJson, out var persisted, out _));
        Assert.Equal("female", persisted!.Characters.Single().Gender);
        Assert.True(persisted.Characters.Single().GenderConfirmed);
        Assert.NotNull(persisted.UpdatedAt);
    }

    [Fact]
    public async Task Patch_EmptyGenderString_ClearsTheValueButStillConfirmsIt()
    {
        // "the extractor's guess is wrong and there is no gender" is a DIFFERENT statement from "I have
        // not looked", and only the confirmation flag can carry the difference.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "רונית", Gender = "male" } }
        }));

        var entry = Ok(await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("רונית", Gender: ""))).Characters.Single();

        Assert.Null(entry.Gender);
        Assert.True(entry.GenderConfirmed);
    }

    [Fact]
    public async Task Patch_OmittedFields_LeaveTheirValuesUntouched()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[]
            {
                new CharacterRegisterEntry { Name = "רונית", Gender = "female", Aliases = new[] { "רוני" }, AliasesConfirmed = true }
            }
        }));

        var entry = Ok(await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("רונית", Gender: "female"))).Characters.Single();

        Assert.Equal(new[] { "רוני" }, entry.Aliases);
        Assert.True(entry.AliasesConfirmed);
    }

    [Fact]
    public async Task Patch_EditAliases_ReplacesAndConfirmsThem_AndNormalizes()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "Danny", Aliases = new[] { "old" } } }
        }));

        var entry = Ok(await PatchAsync(NewController(db), bookId,
            new CharacterRegisterEditDto("Danny", Aliases: new[] { "Daniel", " daniel ", "", "Danny", "Dan" }))).Characters.Single();

        Assert.Equal(new[] { "Daniel", "Dan" }, entry.Aliases);
        Assert.True(entry.AliasesConfirmed);
    }

    [Fact]
    public async Task Patch_EmptyAliasesArray_IsAConfirmedEmptyList()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "Danny", Aliases = new[] { "Daniel" } } }
        }));

        var entry = Ok(await PatchAsync(NewController(db), bookId,
            new CharacterRegisterEditDto("Danny", Aliases: Array.Empty<string>()))).Characters.Single();

        Assert.Empty(entry.Aliases);
        Assert.True(entry.AliasesConfirmed);
    }

    [Fact]
    public async Task Patch_AddCharacter_MarksItAuthorAdded_AndCreatesTheBibleRow()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var dto = Ok(await PatchAsync(NewController(db), bookId,
            new CharacterRegisterEditDto("מירה", Gender: "female", Aliases: new[] { "מימי" })));

        var entry = dto.Characters.Single();
        Assert.True(entry.IsAuthorAdded);
        Assert.True(entry.IsCharacter);
        Assert.True(entry.IsCharacterConfirmed);
        Assert.Equal("female", entry.Gender);
        Assert.True(dto.HasRegister);
        Assert.NotNull(await db.BookBibles.FirstOrDefaultAsync(b => b.BookId == bookId));
    }

    [Fact]
    public async Task Patch_Suppress_MarksNotACharacter_AndSurvivesAReExtractionMerge()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "הרוח", Role = "minor" } }
        }));

        var entry = Ok(await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("הרוח", Op: "suppress"))).Characters.Single();
        Assert.False(entry.IsCharacter);
        Assert.True(entry.IsCharacterConfirmed);

        // The durability claim, pinned deterministically against the one merge implementation.
        var stored = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        Assert.True(CharacterRegisterService.TryDeserialize(stored.CharacterRegisterJson, out var local, out _));
        var merged = CharacterRegisterMerge.Merge(
            local,
            new CharacterRegister { Characters = new[] { new CharacterRegisterEntry { Name = "הרוח", Role = "minor" } } },
            DateTimeOffset.UtcNow);

        Assert.False(merged.Characters.Single().IsCharacter);
    }

    [Fact]
    public async Task Patch_Restore_UnSuppresses()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "הרוח", IsCharacter = false, IsCharacterConfirmed = true } }
        }));

        var entry = Ok(await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("הרוח", Op: "restore"))).Characters.Single();

        Assert.True(entry.IsCharacter);
        Assert.True(entry.IsCharacterConfirmed);
    }

    [Fact]
    public async Task Patch_Restore_UnknownName_Is400_AndDoesNotFabricateACharacter()
    {
        // Restore's whole meaning is "un-suppress an entry that exists". Unlike suppress (which
        // legitimately pre-empts a not-yet-extracted name), a restore with nothing to un-suppress must
        // be rejected rather than silently invent an author-added character.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "הרוח", Role = "minor" } }
        }));

        var result = await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("לא קיים", Op: "restore"));

        Assert.IsType<BadRequestObjectResult>(result.Result);

        // Nothing was written: a rejected batch must not half-apply, and the existing entry must be
        // untouched.
        var stored = await db.BookBibles.AsNoTracking().SingleAsync(b => b.BookId == bookId);
        Assert.True(CharacterRegisterService.TryDeserialize(stored.CharacterRegisterJson, out var persisted, out _));
        var entry = persisted!.Characters.Single();
        Assert.Equal("הרוח", entry.Name);
        Assert.Null(persisted.UpdatedAt);
    }

    [Fact]
    public async Task Patch_Restore_UnknownName_OnBookWithNoBibleRow_Is400_AndCreatesNoRow()
    {
        // The bible == null branch is a separate code path from the update branch above; pin it
        // separately so a rejected batch cannot create the row either.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var result = await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("לא קיים", Op: "restore"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(await db.BookBibles.FirstOrDefaultAsync(b => b.BookId == bookId));
    }

    [Fact]
    public async Task Patch_TargetsTheCharacterByAlias_NotJustByName()
    {
        // The surface should never have to know which surface form the extractor happened to pick.
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "Danny", Aliases = new[] { "Daniel" } } }
        }));

        var dto = Ok(await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("daniel", Gender: "male")));

        var entry = Assert.Single(dto.Characters);
        Assert.Equal("Danny", entry.Name);
        Assert.Equal("male", entry.Gender);
        Assert.True(entry.GenderConfirmed);
    }

    [Fact]
    public async Task Patch_AppliesABatchInOrder()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db);

        var dto = Ok(await PatchAsync(NewController(db), bookId,
            new CharacterRegisterEditDto("מירה", Gender: "female"),
            new CharacterRegisterEditDto("הרוח", Op: "suppress"),
            new CharacterRegisterEditDto("מירה", Aliases: new[] { "מימי" })));

        Assert.Equal(2, dto.Characters.Count);
        var mira = dto.Characters.Single(c => c.Name == "מירה");
        Assert.Equal("female", mira.Gender);
        Assert.Equal(new[] { "מימי" }, mira.Aliases);
        Assert.False(dto.Characters.Single(c => c.Name == "הרוח").IsCharacter);
    }

    [Fact]
    public async Task Patch_DoesNotBumpSiblingBibleBlobs()
    {
        // The register's stamp lives INSIDE the JSON precisely so it cannot be confused with a write to
        // a sibling blob. Confirm the write leaves the siblings alone.
        using var db = NewDb();
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "B", Language = "he" });
        db.BookBibles.Add(new BookBible { BookId = bookId, StyleProfileJson = "{\"pov\":\"third-limited\"}" });
        await db.SaveChangesAsync();

        await PatchAsync(NewController(db), bookId, new CharacterRegisterEditDto("מירה"));

        var bible = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        Assert.Equal("{\"pov\":\"third-limited\"}", bible.StyleProfileJson);
    }

    // ── d1 §4: the invalidation stamp as the analysis DTO reports it ─────────────────────────────

    private static AnalysisResult Result(AnalysisType type, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        Type = type.ToString(),
        AnalysisType = type,
        ResultText = "x",
        CreatedAt = createdAt,
        Suggestions = new List<AnalysisSuggestion>()
    };

    private static readonly DateTimeOffset Stamp = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnalysisDto_NoRegisterStamp_IsNeverStale()
    {
        // Every register persisted before provenance shipped has a null stamp. It must read as "no
        // staleness signal", not "everything is stale".
        var dto = AnalysisController.ToDto(Result(AnalysisType.Proofread, Stamp.AddDays(-1)), null);
        Assert.False(dto.CharacterRegisterStale);
    }

    [Fact]
    public void AnalysisDto_ResultOlderThanTheStamp_IsStale()
    {
        var dto = AnalysisController.ToDto(Result(AnalysisType.Proofread, Stamp.AddMinutes(-1)), Stamp);
        Assert.True(dto.CharacterRegisterStale);
    }

    [Fact]
    public void AnalysisDto_ResultNewerThanTheStamp_IsNotStale()
    {
        var dto = AnalysisController.ToDto(Result(AnalysisType.Proofread, Stamp.AddMinutes(1)), Stamp);
        Assert.False(dto.CharacterRegisterStale);
    }

    /// <summary>
    /// A READABLE SMOKE CHECK of the flag's type gate, and explicitly NOT the guard on it (c04). Both
    /// sides here are hand-authored and only 7 of AnalysisType's 12 members are named, so a newly added
    /// register-reading type moves nothing red in this table - it is simply absent from it. The real
    /// oracle is
    /// <c>CharacterRegisterReadingTypeSetTests.EveryAnalysisType_LoadGate_RenderGate_AndStaleFlag_Agree</c>,
    /// which enumerates <c>Enum.GetValues&lt;AnalysisType&gt;()</c> and probes the rendered prompt, the
    /// real context build and this flag for EVERY member. Do not grow this table instead of that one.
    /// </summary>
    [Theory]
    [InlineData(AnalysisType.Proofread, true)]
    [InlineData(AnalysisType.LiteraryAnalysis, true)]
    [InlineData(AnalysisType.QA, true)]
    [InlineData(AnalysisType.Synopsis, true)]
    // These never pull the register into context, so flagging them would be a FALSE signal.
    [InlineData(AnalysisType.LineEdit, false)]
    [InlineData(AnalysisType.LinguisticAnalysis, false)]
    [InlineData(AnalysisType.Summarization, false)]
    public void AnalysisDto_StaleFlag_IsGatedToTypesThatActuallyReadTheRegister(AnalysisType type, bool expected)
    {
        var dto = AnalysisController.ToDto(Result(type, Stamp.AddDays(-1)), Stamp);
        Assert.Equal(expected, dto.CharacterRegisterStale);
    }

    [Fact]
    public void AnalysisDto_StaleFlag_IsInformationalOnly_AndDefaultsFalse()
    {
        // The single-argument call (a freshly produced result) must not report staleness.
        Assert.False(AnalysisController.ToDto(Result(AnalysisType.Proofread, Stamp)).CharacterRegisterStale);
    }
}
