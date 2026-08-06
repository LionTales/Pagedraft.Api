using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The author-write side of the register race (fix-plan c05), the sibling of the re-extraction race
/// pinned in <see cref="CharacterRegisterProvenanceTests"/>.
///
/// <para><see cref="CharacterRegisterService.ApplyEditsAsync"/> is a read-modify-write over one JSON
/// column. Two overlapping batches used to leave the later SaveChangesAsync silently overwriting the
/// earlier one's entries, and two overlapping batches on a book with NO bible row both took the create
/// branch, the second one violating the unique index on BookId with an unhandled DbUpdateException.</para>
///
/// <para>DETERMINISTIC, NO THREADS. Each test positions the concurrent write at an exact point in the
/// other request's lifetime rather than racing two tasks: the lost-update test interleaves between the
/// read and the write by construction, and the duplicate-row test uses a one-shot side-effecting fake
/// inside SaveChangesAsync. The same technique the tracker TOCTOU test used.</para>
///
/// <para>SHARED STORE. Both contexts in every test are built on ONE in-memory database name, resolved
/// once and passed in. A per-context name is the trap that made this class of test falsely green
/// before (see the note on CharacterRegisterProvenanceTests.BuildProvider): a "concurrent" write to a
/// different store proves nothing. Each test asserts the other context's write is VISIBLE before
/// relying on it, so a store split cannot pass silently.</para>
/// </summary>
public class CharacterRegisterWriteConcurrencyTests
{
    private static DbContextOptions<AppDbContext> OptionsFor(string storeName) =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(storeName).Options;

    private static AppDbContext NewDb(string storeName) => new(OptionsFor(storeName));

    private static CharacterRegisterService ServiceFor(AppDbContext db) =>
        new(db, NullLogger<CharacterRegisterService>.Instance);

    private static UpdateCharacterRegisterRequest Batch(params CharacterRegisterEditDto[] edits) => new(edits);

    private static async Task<Guid> SeedBookAsync(string storeName, string? registerJson)
    {
        using var db = NewDb(storeName);
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Register Book", Language = "he" });
        if (registerJson != null)
            db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = registerJson });
        await db.SaveChangesAsync();
        return bookId;
    }

    private static async Task<CharacterRegister> ReadPersistedAsync(string storeName, Guid bookId)
    {
        using var db = NewDb(storeName);
        var row = await db.BookBibles.AsNoTracking().SingleAsync(b => b.BookId == bookId);
        Assert.True(
            CharacterRegisterService.TryDeserialize(row.CharacterRegisterJson, out var register, out _)
            && register is not null,
            "The persisted register column did not deserialize; the assertions below would be vacuous.");
        return register!;
    }

    // ── (a) Two overlapping batches must not lose the first batch's entry ────────────────────────

    [Fact]
    public async Task TwoOverlappingBatches_TheSecondWriter_DoesNotLoseTheFirstBatchEntry()
    {
        // THE INTERLEAVE, positioned by construction rather than by timing: request A performs its
        // read, request B's ENTIRE batch lands, and only then does request A apply and write. That is
        // exactly the window ApplyEditsAsync's read-modify-write leaves open, and it is the window the
        // re-read closes. A's context resolves its own read to the instance it already tracks, whose
        // property values EF deliberately does NOT refresh from the store - which is why ReloadAsync
        // exists and why the fix uses it.
        //
        // A write landing LATER than this - between the re-read and SaveChangesAsync - is still lost,
        // and the fix says so in its own comment. This test pins what the fix covers, not more.
        var store = Guid.NewGuid().ToString();
        var bookId = await SeedBookAsync(store, CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[] { new CharacterRegisterEntry { Name = "רונית", Gender = "female" } }
        }));

        using var ctxA = NewDb(store);
        using var ctxB = NewDb(store);

        // Request A reads. Nothing written yet.
        await ctxA.BookBibles.FirstAsync(b => b.BookId == bookId);

        // Request B's whole batch lands inside A's window, on its own DbContext exactly as a second
        // HTTP request would.
        var (bResult, bError) = await ServiceFor(ctxB).ApplyEditsAsync(
            bookId,
            Batch(new CharacterRegisterEditDto("אלון", "upsert", Gender: "male")),
            CancellationToken.None);

        // NON-VACUITY + SHARED-STORE GUARD: B really wrote, and its write is visible to a third
        // context. Without this the test could pass because B silently did nothing.
        Assert.Null(bError);
        Assert.NotNull(bResult);
        var afterB = await ReadPersistedAsync(store, bookId);
        Assert.Contains(afterB.Characters, c => c.Name == "אלון");

        // Request A now applies its own batch against a register it read BEFORE B wrote.
        var (aResult, aError) = await ServiceFor(ctxA).ApplyEditsAsync(
            bookId,
            Batch(new CharacterRegisterEditDto("רונית", "upsert", Gender: "male")),
            CancellationToken.None);

        Assert.Null(aError);
        Assert.NotNull(aResult);

        var persisted = await ReadPersistedAsync(store, bookId);

        // THE DEFECT: B's entry is the one the lost update erases.
        Assert.Contains(persisted.Characters, c => c.Name == "אלון");
        Assert.True(persisted.Characters.Single(c => c.Name == "אלון").GenderConfirmed);

        // A's own edit still lands - the fix re-applies the batch, it does not abandon it.
        var ronit = persisted.Characters.Single(c => c.Name == "רונית");
        Assert.Equal("male", ronit.Gender);
        Assert.True(ronit.GenderConfirmed);

        // The DTO A hands back is what the client reconciles its optimistic state against, so it must
        // describe the same register that was persisted, not the pre-merge one.
        Assert.Contains(aResult!.Characters, c => c.Name == "אלון");
        Assert.Contains(aResult.Characters, c => c.Name == "רונית" && c.Gender == "male");

        // UpdatedAt is stamped on every accepted batch (d1 §4), including a re-applied one.
        Assert.NotNull(persisted.UpdatedAt);
    }

    // ── (b) Two overlapping FIRST writes must not 500 or duplicate the row ───────────────────────

    [Fact]
    public async Task TwoOverlappingFirstWrites_AdoptTheExistingRow_InsteadOfThrowingOrDuplicating()
    {
        // Both requests find NO bible row, so both take the create branch. In production the second
        // INSERT hits IX_BookBibles_BookId and EF surfaces a DbUpdateException that nothing catches:
        // a 500 on an author's edit. The in-memory provider does NOT enforce that unique index, so
        // this test's context enforces it (see InterleavingDbContext) - otherwise the production
        // failure is unreachable here and the test would prove nothing.
        var store = Guid.NewGuid().ToString();
        var bookId = await SeedBookAsync(store, registerJson: null);

        using var ctxB = NewDb(store);
        using var ctxA = new InterleavingDbContext(OptionsFor(store));

        var concurrentWriteFired = false;
        string? concurrentError = null;

        // ONE-SHOT SIDE-EFFECTING FAKE: request B's whole batch runs inside request A's save, so B is
        // guaranteed to have committed a row before A's insert reaches the store. The retry's second
        // SaveChangesAsync does not re-fire it.
        ctxA.OnFirstSaveChanges = async () =>
        {
            (_, concurrentError) = await ServiceFor(ctxB).ApplyEditsAsync(
                bookId,
                Batch(new CharacterRegisterEditDto("אלון", "upsert")),
                CancellationToken.None);
            concurrentWriteFired = true;
        };

        // THE DEFECT: un-fixed, this call throws DbUpdateException out of ApplyEditsAsync.
        var (aResult, aError) = await ServiceFor(ctxA).ApplyEditsAsync(
            bookId,
            Batch(new CharacterRegisterEditDto("רונית", "upsert", Gender: "female")),
            CancellationToken.None);

        // NON-VACUITY: the concurrent write actually happened and actually succeeded, so the row A
        // collided with is real.
        Assert.True(concurrentWriteFired, "The concurrent batch never ran; there was no race to survive.");
        Assert.Null(concurrentError);

        Assert.Null(aError);
        Assert.NotNull(aResult);

        // Exactly ONE row: the condition the unique index would have rejected.
        using (var verify = NewDb(store))
        {
            Assert.Equal(1, await verify.BookBibles.CountAsync(b => b.BookId == bookId));
        }

        // And the adopted row carries BOTH requests' entries - adopting must not mean discarding the
        // row that won, nor abandoning the batch that lost.
        var persisted = await ReadPersistedAsync(store, bookId);
        Assert.Contains(persisted.Characters, c => c.Name == "אלון");
        Assert.Contains(persisted.Characters, c => c.Name == "רונית" && c.Gender == "female");
        Assert.NotNull(persisted.UpdatedAt);

        Assert.Contains(aResult!.Characters, c => c.Name == "אלון");
        Assert.Contains(aResult.Characters, c => c.Name == "רונית");
    }

    // NOT TESTED, and deliberately so: a batch REJECTED only on the re-applied value. The single
    // rejection ApplyOne can produce is an unmatched `restore` (f03), and a restore that MATCHED the
    // first read still matches the re-read, so there is no reachable input that passes the first pass
    // and fails the second. (One that did not match is rejected before any write is attempted.)
    //
    // The reason is MATCHABILITY, not list length - do not restate it as "the entry list only ever
    // grows". No EditOp removes an entry, but Normalize COLLAPSES duplicates (c02, shipped alongside
    // this), so the re-read list can legitimately be SHORTER than the first. A collapse cannot cost a
    // match: the survivor keeps its own Name and absorbs the dropped entry's Name and aliases into its
    // alias set, so every probe that resolved to either copy still resolves to the survivor.
    //
    // The re-apply paths still return the error rather than writing, because the ONE place that decides
    // what an edit means must keep deciding it; that guard is defensive, not dead-by-accident.

    /// <summary>
    /// An <see cref="AppDbContext"/> that (1) runs a ONE-SHOT callback at the top of the first
    /// SaveChangesAsync, which is how a concurrent request is placed at an exact point in this
    /// request's lifetime without threads, and (2) enforces the unique index on
    /// <c>BookBible.BookId</c> that the real model declares (AppDbContext.OnModelCreating) but the
    /// in-memory provider does not implement.
    ///
    /// <para>The emulation exists so the PRODUCTION failure is reachable in a test at all: without it
    /// a duplicate insert silently succeeds in memory and the 500 this fix closes could only be
    /// approximated by counting rows. The row count is asserted too, so weakening the emulation cannot
    /// quietly empty these tests.</para>
    /// </summary>
    private sealed class InterleavingDbContext : AppDbContext
    {
        private bool _fired;

        public InterleavingDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public Func<Task>? OnFirstSaveChanges { get; set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_fired && OnFirstSaveChanges is not null)
            {
                _fired = true;
                await OnFirstSaveChanges();
            }

            foreach (var candidate in ChangeTracker.Entries<BookBible>()
                         .Where(e => e.State == EntityState.Added)
                         .Select(e => e.Entity)
                         .ToList())
            {
                var clash = await BookBibles.AsNoTracking()
                    .AnyAsync(b => b.BookId == candidate.BookId && b.Id != candidate.Id, cancellationToken);
                if (clash)
                {
                    throw new DbUpdateException(
                        $"Emulated IX_BookBibles_BookId violation: a BookBible row for book {candidate.BookId} already exists.");
                }
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
