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
            });

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var key in new[]
                 {
                     "\"bookId\"", "\"hasRegister\"", "\"updatedAt\"", "\"characters\"",
                     "\"name\"", "\"gender\"", "\"role\"", "\"description\"", "\"aliases\"",
                     "\"isCharacter\"", "\"isAuthorAdded\"", "\"genderConfirmed\"",
                     "\"aliasesConfirmed\"", "\"isCharacterConfirmed\""
                 })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }
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
