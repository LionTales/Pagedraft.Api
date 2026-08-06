using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Reads and writes <see cref="BookBible.CharacterRegisterJson"/> for the author-facing surface
/// (character-register-editing plan, c1).
///
/// <para>Serialization lives here so the register has exactly ONE (de)serializer configuration:
/// <see cref="AnalysisContextService"/> reads through <see cref="TryDeserialize"/> too. A second
/// options instance that disagreed about, say, case sensitivity would produce a register that reads
/// back with every provenance flag silently false — which is precisely the failure mode this whole
/// todo exists to prevent.</para>
///
/// <para>Re-extraction never comes through here; it goes through
/// <see cref="CharacterRegisterMerge"/> from <see cref="AnalysisContextService"/>. This service owns
/// the AUTHOR's writes only.</para>
/// </summary>
public class CharacterRegisterService
{
    /// <summary>
    /// The ONE serializer configuration for the register column. Case-insensitive on read so a
    /// hand-written or older-cased payload still binds its provenance flags rather than defaulting
    /// them to false.
    /// </summary>
    internal static readonly JsonSerializerOptions RegisterJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _db;
    private readonly ILogger<CharacterRegisterService> _logger;

    public CharacterRegisterService(AppDbContext db, ILogger<CharacterRegisterService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Parse a persisted register. Returns false (with <paramref name="register"/> null) when the
    /// column is blank or the JSON is unusable, and never throws for bad JSON — a corrupt register
    /// must degrade, not take an analysis down with it.
    ///
    /// <para><paramref name="fault"/> carries WHY it failed when it failed. A fail-safe that swallows
    /// to stay non-throwing blinds its caller's logger unless the fault is handed back explicitly;
    /// this codebase has already shipped silent failures for exactly that reason.</para>
    /// </summary>
    public static bool TryDeserialize(string? json, out CharacterRegister? register, out Exception? fault)
    {
        register = null;
        fault = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            register = JsonSerializer.Deserialize<CharacterRegister>(json, RegisterJsonOptions);
            return register is not null;
        }
        catch (JsonException ex)
        {
            fault = ex;
            return false;
        }
    }

    /// <summary>Serialize a register for the column, using the one shared configuration.</summary>
    public static string Serialize(CharacterRegister register)
        => JsonSerializer.Serialize(register, RegisterJsonOptions);

    /// <summary>
    /// The author-facing view of a book's register. Returns null when the book does not exist;
    /// returns a DTO with <c>HasRegister=false</c> and no characters when the book exists but the
    /// register has never been built (the empty state the surface must explain, rather than render a
    /// blank list that reads as "no characters").
    /// </summary>
    public async Task<CharacterRegisterDto?> GetAsync(Guid bookId, CancellationToken ct)
    {
        if (!await _db.Books.AsNoTracking().AnyAsync(b => b.Id == bookId, ct)) return null;

        var json = await _db.BookBibles.AsNoTracking()
            .Where(b => b.BookId == bookId)
            .Select(b => b.CharacterRegisterJson)
            .FirstOrDefaultAsync(ct);

        if (!TryDeserialize(json, out var register, out var fault) || register is null)
        {
            if (fault != null)
            {
                // OBSERVABILITY: an unreadable register silently degrades to "never built", which the
                // surface renders as an empty state. Without this line the author sees a plausible
                // empty list and nobody ever learns the column is corrupt. No register CONTENT is
                // logged — character names are user manuscript data.
                _logger.LogError(
                    fault,
                    "Character register for book {BookId} could not be deserialized ({JsonLength} chars); serving an empty register.",
                    bookId,
                    json?.Length ?? 0);
            }

            // COVERAGE IS REPORTED ON THIS PATH TOO, from a null register: the ledger lives inside the
            // value that is missing or would not parse, so every chapter reads as outstanding. That is
            // the honest answer ("nothing is known to have contributed"), it keeps the field non-null on
            // every response, and it is the empty state's real content - the author is told how much of
            // the book the register will cover once analyses start, not just that it is empty.
            return new CharacterRegisterDto(
                bookId,
                HasRegister: false,
                UpdatedAt: null,
                Characters: Array.Empty<CharacterRegisterEntryDto>(),
                Coverage: await BuildCoverageAsync(bookId, register: null, ct));
        }

        // Normalize COLLAPSES duplicate entries as well as cleaning them (fix-plan c02), so the author
        // is served ONE row per character even from a legacy register that holds a name twice. The
        // column is not rewritten here (this is a read); the repair persists on the next author write
        // or re-extraction merge, both of which normalize too.
        var entries = CharacterRegisterMerge.Normalize(register);
        return new CharacterRegisterDto(
            bookId,
            HasRegister: true,
            register.UpdatedAt,
            entries.Select(ToDto).ToList(),
            // Built from `register` - the value just deserialized out of the column - so the reported
            // coverage describes the SAME persisted ledger this response's characters came from.
            await BuildCoverageAsync(bookId, register, ct));
    }

    /// <summary>
    /// The coverage summary for a book, computed from the persisted scan ledger carried inside
    /// <paramref name="register"/> (be-c03).
    ///
    /// <para>THE GUARD THIS SHAPE IS: there is no stored coverage count anywhere and no second walk of
    /// the register. The number the author is shown and the decision the scan path takes about whether
    /// a chapter still needs scanning are the same predicate over the same list
    /// (<see cref="CharacterRegisterCoverage.IsCoveredAndFresh"/>), so they cannot drift apart. This
    /// workspace has shipped a status count and its builder disagreeing more than once.</para>
    ///
    /// <para>COST, stated rather than hidden (be-c01). This is TWO reads. The first projects
    /// <c>{Id, UpdatedAt}</c> for every chapter - two scalar columns, no prose. <c>SummarizeAsync</c>
    /// then works out which chapters' text the classification can actually consult, which is exactly
    /// those that are not covered-and-fresh, and only those chapters' <c>ContentText</c> is fetched.
    /// No reported number moves either way: the answer is identical to loading the whole manuscript,
    /// because a covered chapter's text is never looked at.</para>
    ///
    /// <para>WHAT THAT IS AND IS NOT WORTH, honestly. The second read shrinks with COVERAGE, not with
    /// book size: a fully covered book reads no chapter text at all, a half-covered one reads half,
    /// and a book with nothing covered still reads every chapter - now behind one extra round trip
    /// and an id list. Every register that predates the scan ledger is in exactly that last state, so
    /// on the day this shipped it saved nothing on any book that existed; it starts paying as chapters
    /// contribute. Do not restate it as "the card stopped loading the manuscript".</para>
    ///
    /// <para>WHY THE TEXT IS STILL READ FOR THE REST rather than replaced with something cheaper:
    /// "can an analysis read this chapter" is answered by the expression the analysis path itself
    /// uses (the Syncfusion watermark strip, then a blank test), not by a proxy such as WordCount that
    /// is maintained by a different writer and could disagree. A mis-answer there is not cosmetic - it
    /// is what makes 'complete' unreachable on a book with an empty chapter. The exactness is kept;
    /// only the chapters that cannot change the answer were dropped from the read.</para>
    /// </summary>
    private async Task<CharacterRegisterCoverageDto> BuildCoverageAsync(
        Guid bookId,
        CharacterRegister? register,
        CancellationToken ct)
    {
        var chapters = await _db.Chapters.AsNoTracking()
            .Where(c => c.BookId == bookId)
            .Select(c => new CharacterRegisterCoverage.ChapterVersion(c.Id, c.UpdatedAt))
            .ToListAsync(ct);

        return await CharacterRegisterCoverage.SummarizeAsync(
            register,
            chapters,
            LoadChapterContentTextAsync,
            ct);
    }

    /// <summary>
    /// Phase 2 of the coverage read: the <c>ContentText</c> of the named chapters only, in one round
    /// trip. Never called with an empty set - <c>SummarizeAsync</c> skips the fetch entirely then.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string?>> LoadChapterContentTextAsync(
        IReadOnlyCollection<Guid> chapterIds,
        CancellationToken ct)
    {
        var rows = await _db.Chapters.AsNoTracking()
            .Where(c => chapterIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ContentText })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id, r => (string?)r.ContentText);
    }

    /// <summary>
    /// Apply a batch of author edits and persist. Returns the SERVER's resulting register, which is
    /// what the client reconciles its optimistic state against.
    ///
    /// <para>Every accepted batch stamps <see cref="CharacterRegister.UpdatedAt"/> = now (d1 §4): an
    /// author edit is by definition a content change, so unlike the merge path there is no "nothing
    /// changed" case to preserve a stamp for.</para>
    ///
    /// <para>CONCURRENCY: this is a read-modify-write over one JSON column. It uses the SAME mechanism
    /// as the re-extraction writer (re-read immediately before the write, re-apply against the fresh
    /// value, adopt an existing row rather than inserting a second one). The choice, its limits and
    /// why there is no concurrency token are argued at the re-read below; read that before adding a
    /// third mechanism here.</para>
    /// </summary>
    /// <returns>
    /// <c>(null, error)</c> when the request is invalid (the caller turns it into a 400);
    /// <c>(null, null)</c> when the book does not exist (404); otherwise the updated register.
    /// </returns>
    public async Task<(CharacterRegisterDto? Result, string? Error)> ApplyEditsAsync(
        Guid bookId,
        UpdateCharacterRegisterRequest? request,
        CancellationToken ct)
    {
        var edits = request?.Edits;
        if (edits is null || edits.Count == 0)
            return (null, "At least one edit is required.");

        foreach (var edit in edits)
        {
            if (edit is null)
                return (null, "An edit entry was null.");
            if (string.IsNullOrWhiteSpace(edit.Name))
                return (null, "Every edit requires a non-blank 'name'.");
            if (!TryParseOp(edit.Op, out _))
                return (null, $"Unsupported 'op' value '{edit.Op}'. Expected one of: upsert, suppress, restore.");
        }

        if (!await _db.Books.AsNoTracking().AnyAsync(b => b.Id == bookId, ct)) return (null, null);

        var now = DateTimeOffset.UtcNow;

        // Applying the batch is a PURE function of the stored JSON, so it lives in one local that can
        // be run more than once against different starting values. That is what makes the re-read
        // below a re-APPLY rather than a merge: the same ops, replayed on top of whatever is actually
        // in the column, with no second implementation of what an edit means.
        //
        // Returns the error string (batch rejected) or null. On rejection NOTHING is written: this
        // runs before any assignment to the tracked entity and before any SaveChangesAsync, which is
        // the all-or-nothing contract the caller relies on.
        // `ledger` is passed straight through UNTOUCHED (automatic-coverage plan, be-c01). It is the
        // per-chapter scan ledger the OTHER writer of this column
        // (AnalysisContextService.LoadCharacterRegisterAsync) maintains, and this method has no opinion
        // about it - but it must carry it across, because this writer replaces the WHOLE column. Losing
        // it does not corrupt anything visible: it silently marks every chapter unscanned, so the next
        // analysis of each one pays for a fresh LLM extraction it did not need, forever after any
        // author edit. It comes out of the same read the entries were built from, so a re-apply against
        // a re-read value carries THAT value's ledger, not the stale one.
        string? TryBuildEntries(
            string? storedJson,
            out List<CharacterRegisterEntry> built,
            out IReadOnlyList<ScannedChapterEntry> ledger)
        {
            if (!TryDeserialize(storedJson, out var existing, out var fault) && fault != null)
            {
                // A corrupt register is NOT a reason to refuse the author's edit, but starting from an
                // empty list silently discards whatever the column held. Say so loudly, with the book
                // id and no content.
                _logger.LogError(
                    fault,
                    "Character register for book {BookId} was unreadable while applying {EditCount} author edit(s); the edits are being applied over an EMPTY register and the unreadable value will be replaced.",
                    bookId,
                    edits.Count);
            }

            // Collapse duplicates BEFORE applying the batch (fix-plan c02): ApplyOne locates its
            // target with FindIndex, so against a duplicate-carrying register every edit landed on the
            // first occurrence and the second row was permanently uncorrectable. Post-collapse there
            // is one entry to hit, it carries the union of both copies' author state, and serializing
            // the result persists the repair.
            built = CharacterRegisterMerge.Normalize(existing).ToList();
            ledger = existing?.ScannedChapters ?? Array.Empty<ScannedChapterEntry>();

            foreach (var edit in edits)
            {
                TryParseOp(edit.Op, out var op);
                var (next, err) = ApplyOne(built, edit, op);
                if (err != null) return err;
                built = next;
            }

            return null;
        }

        var bible = await _db.BookBibles.FirstOrDefaultAsync(b => b.BookId == bookId, ct);

        // Captured as a STRING, not as the tracked property, so the comparison below is against what
        // this request actually applied its batch to and cannot be updated underneath us.
        var jsonAtFirstRead = bible?.CharacterRegisterJson;

        var error = TryBuildEntries(jsonAtFirstRead, out var entries, out var ledger);
        if (error != null) return (null, error);

        // ── RE-READ BEFORE THE WRITE (fix-plan c05) ─────────────────────────────────────────────
        // This method is a read-modify-write over ONE JSON column holding every character. Two author
        // batches that overlap would otherwise both apply to the value read at the top and the later
        // SaveChangesAsync would silently overwrite the earlier one's entries; and two batches on a
        // book with no bible row would both take the create branch, the second hitting the unique
        // index on BookId (AppDbContext) with an unhandled DbUpdateException, i.e. a 500 rather than a
        // resolved write.
        //
        // WHY THIS SHAPE AND NOT A CONCURRENCY TOKEN (decided fix-plan c05, argued not assumed):
        //  1. AnalysisContextService.LoadCharacterRegisterAsync already closed the same hazard on the
        //     re-extraction side with re-read-before-write plus adopt-the-existing-row. This column has
        //     exactly TWO writers in the whole API (that one and this one); giving them two different
        //     concurrency mechanisms is how the next reader ends up maintaining both badly.
        //  2. A RowVersion would DETECT a conflict, not RESOLVE one. Both writers would still need the
        //     re-read-and-re-apply written here to decide what to do with the detection, and the
        //     re-extraction writer would additionally need it without breaking its fail-safe contract
        //     (a conflict there must never fail the analysis). The token is work ON TOP of this, not
        //     instead of it.
        //  3. It would ship untested. MEASURED 2026-08-05 against this repo's harness: with
        //     IsRowVersion() configured, UseInMemoryDatabase leaves the token NULL after both insert
        //     and update and accepts a stale-read overwrite with no DbUpdateConcurrencyException. The
        //     whole deterministic suite runs in-memory, so every line of a token's conflict handling
        //     would be unreachable by every test here.
        //  4. It is a schema migration on a live database for a hazard whose sibling columns
        //     (StyleProfileJson, ThemesJson, TimelineJson, WorldBuildingJson) have no production writer
        //     at all today - they are read-only outside test seeds - so the token would buy nothing for
        //     them either.
        //
        // WHAT THIS DOES NOT COVER - say it plainly, because the fix above is narrower than the title
        // "concurrency" suggests:
        //  - It NARROWS the lost-update window, it does not close it. A write landing between the
        //    re-read on the next lines and SaveChangesAsync is still lost silently. Nothing in-process
        //    can close that; only a store-level compare-and-swap (the token) could, and that is
        //    deliberately deferred for the four reasons above.
        //  - Where a conflict IS detected, this batch wins for the names it names: the ops are
        //    re-applied on top of the fresh value. That is the intended semantic (an author edit is an
        //    explicit instruction, and the concurrent batch's OTHER entries survive), but it is not a
        //    field-level merge of two authors editing the same character's gender at once.
        //  - The create retry below is ONE attempt. A third concurrent creator rethrows.
        //  - Sibling columns are safe by EF's own UPDATE shape, not by anything here: EF writes only
        //    modified properties, so this write does not clobber a concurrent StyleProfileJson write.
        //    BookBible.UpdatedAt is the exception - it is one column shared by every blob on the row
        //    (see AnalysisContext.CharacterRegister.UpdatedAt, which exists for exactly that reason).
        if (bible != null)
        {
            // ReloadAsync, not a re-query: a TRACKING re-query resolves to the instance already in the
            // change tracker and leaves its stale property values in place, so it would hand back the
            // very snapshot being replaced. Reload refreshes the tracked instance from the store in
            // place, so the re-apply input and the write target are the same current row.
            await _db.Entry(bible).ReloadAsync(ct);

            // A concurrent DELETE leaves the entity Detached with no row behind it. Treat that as "no
            // row" so the create branch runs instead of writing through a dead entity.
            if (_db.Entry(bible).State == EntityState.Detached) bible = null;
        }
        else
        {
            // Nothing tracked, so nothing to reload: query. If a concurrent request CREATED the row we
            // ADOPT it here, which is what stops the create branch from inserting a SECOND row.
            bible = await _db.BookBibles.FirstOrDefaultAsync(b => b.BookId == bookId, ct);
        }

        var jsonAtReRead = bible?.CharacterRegisterJson;
        if (!string.Equals(jsonAtReRead, jsonAtFirstRead, StringComparison.Ordinal))
        {
            // A clobber that was just prevented. A silently-rescued race nobody can see is the same
            // blindness one layer up. Book id and payload LENGTHS only: register content is the user's
            // manuscript and is never logged.
            _logger.LogWarning(
                "Character register for book {BookId} CHANGED between this edit batch's read and its write ({BeforeLength} -> {AfterLength} chars). Re-applying the {EditCount} edit(s) against the CURRENT stored value; the first read would have overwritten a concurrent write.",
                bookId,
                jsonAtFirstRead?.Length ?? 0,
                jsonAtReRead?.Length ?? 0,
                edits.Count);

            error = TryBuildEntries(jsonAtReRead, out entries, out ledger);
            if (error != null) return (null, error);
        }

        var updated = new CharacterRegister { Characters = entries, UpdatedAt = now, ScannedChapters = ledger };

        if (bible == null)
        {
            bible = new BookBible
            {
                BookId = bookId,
                CreatedAt = now,
                UpdatedAt = now,
                CharacterRegisterJson = Serialize(updated)
            };
            _db.BookBibles.Add(bible);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // The row was created between the re-read above and this insert, so the unique index
                // on BookId rejected the second one. Adopt the winner and re-apply ONCE. Guarded on a
                // row now existing rather than on a provider-specific error number: if there is no
                // row, this was not the race and the fault is rethrown rather than swallowed.
                _db.Entry(bible).State = EntityState.Detached;
                var winner = await _db.BookBibles.FirstOrDefaultAsync(b => b.BookId == bookId, ct);
                if (winner == null) throw;

                _logger.LogWarning(
                    ex,
                    "Character register row for book {BookId} was created concurrently while this edit batch was inserting one. Adopting the existing row and re-applying the {EditCount} edit(s) against it.",
                    bookId,
                    edits.Count);

                error = TryBuildEntries(winner.CharacterRegisterJson, out entries, out ledger);
                if (error != null) return (null, error);

                updated = new CharacterRegister { Characters = entries, UpdatedAt = now, ScannedChapters = ledger };
                winner.CharacterRegisterJson = Serialize(updated);
                winner.UpdatedAt = now;

                // ONE retry only: a second failure propagates rather than looping.
                await _db.SaveChangesAsync(ct);
            }
        }
        else
        {
            bible.CharacterRegisterJson = Serialize(updated);
            bible.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Character register updated by author for book {BookId}: {EditCount} edit(s) applied, {CharacterCount} entries ({SuppressedCount} suppressed).",
            bookId,
            edits.Count,
            entries.Count,
            entries.Count(CharacterRegisterMerge.IsSuppressed));

        // Coverage comes off `updated` - the record that was just SERIALIZED into the column - so this
        // response reports the ledger this write actually persisted. Author edits never advance or
        // retreat coverage (this writer carries the ledger through untouched), but the client replaces
        // its whole register state from this response, so an omitted or zeroed coverage here would make
        // the coverage line collapse to "0 of 40" on every save and silently recover on the next GET.
        return (new CharacterRegisterDto(
            bookId,
            HasRegister: true,
            updated.UpdatedAt,
            entries.Select(ToDto).ToList(),
            await BuildCoverageAsync(bookId, updated, ct)), null);
    }

    // ── internals ───────────────────────────────────────────────────────────────────────────────

    internal enum EditOp
    {
        Upsert,
        Suppress,
        Restore
    }

    internal static bool TryParseOp(string? op, out EditOp parsed)
    {
        parsed = EditOp.Upsert;
        if (string.IsNullOrWhiteSpace(op)) return true;

        switch (op.Trim().ToLowerInvariant())
        {
            case "upsert": parsed = EditOp.Upsert; return true;
            case "suppress": parsed = EditOp.Suppress; return true;
            case "restore": parsed = EditOp.Restore; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Apply one edit to a working list, returning the new list, or an error and the list unchanged
    /// when the edit is invalid for its target. The target is located with the SAME matching key the
    /// merge uses (<see cref="CharacterRegisterMerge.Matches"/>), so an author can address a character
    /// by an alias and the surface never has to know which surface form the extractor happened to pick.
    ///
    /// <para>EditOp verdicts for the no-match branch below (all three members considered, not just the
    /// one that changed):</para>
    /// <list type="bullet">
    /// <item>Upsert - creates a new author-added entry. Correct and unchanged: naming a character the
    /// extractor has not found yet is exactly what upsert is for.</item>
    /// <item>Suppress - creates a new author-added, suppressed entry. Correct and unchanged, already
    /// documented at the branch: it pre-empts a future extraction surfacing a name the author already
    /// knows to hide.</item>
    /// <item>Restore - REJECTED, not created (this is the fix). Restore's entire meaning is "un-suppress
    /// an entry that exists"; there is nothing to un-suppress here, so creating one would fabricate a
    /// character the author never asked for rather than honor what the op means. The client never
    /// issues this shape (Restore renders only on rows the register already holds as suppressed), so
    /// the 400 is unreachable from the shipped UI and exists to keep the API contract honest.</item>
    /// </list>
    /// </summary>
    private static (List<CharacterRegisterEntry> Entries, string? Error) ApplyOne(
        List<CharacterRegisterEntry> entries,
        CharacterRegisterEditDto edit,
        EditOp op)
    {
        var name = edit.Name!.Trim();
        var probe = new CharacterRegisterEntry { Name = name };
        var index = entries.FindIndex(e => CharacterRegisterMerge.Matches(e, probe));

        if (index < 0)
        {
            if (op == EditOp.Restore)
            {
                return (entries, $"Cannot restore '{name}': no character register entry with that name exists.");
            }

            // A brand-new entry. IsAuthorAdded is set for EVERY remaining op (Upsert, Suppress),
            // including suppress: an author who pre-emptively suppresses a name the extractor has not
            // surfaced yet still authored that entry, and the merge must keep it out of the
            // extracted-only replace step.
            var created = new CharacterRegisterEntry
            {
                Name = name,
                IsAuthorAdded = true,
                IsCharacter = op != EditOp.Suppress,
                IsCharacterConfirmed = true
            };
            created = ApplyFieldEdits(created, edit);
            entries.Add(created);
            return (entries, null);
        }

        var target = entries[index];
        target = op switch
        {
            EditOp.Suppress => target with { IsCharacter = false, IsCharacterConfirmed = true },
            EditOp.Restore => target with { IsCharacter = true, IsCharacterConfirmed = true },
            _ => target
        };

        entries[index] = ApplyFieldEdits(target, edit);
        return (entries, null);
    }

    /// <summary>
    /// Gender/aliases: ABSENT (null) means untouched; PRESENT means set AND confirm. An empty gender
    /// string clears the value while still confirming it ("the guess is wrong and there is none"),
    /// and an empty aliases array is a confirmed empty list.
    /// </summary>
    private static CharacterRegisterEntry ApplyFieldEdits(CharacterRegisterEntry entry, CharacterRegisterEditDto edit)
    {
        if (edit.Gender is not null)
        {
            var gender = edit.Gender.Trim();
            entry = entry with
            {
                Gender = gender.Length == 0 ? null : gender,
                GenderConfirmed = true
            };
        }

        if (edit.Aliases is not null)
        {
            entry = entry with
            {
                Aliases = CharacterRegisterMerge.NormalizeAliases(edit.Aliases, entry.Name),
                AliasesConfirmed = true
            };
        }

        return entry;
    }

    private static CharacterRegisterEntryDto ToDto(CharacterRegisterEntry e) => new(
        e.Name,
        e.Gender,
        e.Role,
        e.Description,
        e.Aliases,
        e.IsCharacter,
        e.IsAuthorAdded,
        e.GenderConfirmed,
        e.AliasesConfirmed,
        e.IsCharacterConfirmed);
}
