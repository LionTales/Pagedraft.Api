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
/// Character-register PROVENANCE + MERGE (character-register-editing plan, c1 / d1 §1 and §3).
///
/// Two things are pinned here and nothing else may be allowed to drift from them:
///   1. BACKWARD COMPATIBILITY. Every register already persisted has none of the provenance
///      properties. Absent provenance must deserialize as EXTRACTED. Defaulting the other way would
///      freeze every currently-guessed gender as if a human had blessed it, silently and for every
///      book at once.
///   2. THE MERGE RULE, case for case. It exists so a re-extraction cannot erase an author's work;
///      it lives in exactly one place (CharacterRegisterMerge) and every row of d1's table has a test.
/// </summary>
public class CharacterRegisterProvenanceTests
{
    /// <summary>
    /// A register EXACTLY as it is persisted today, before this feature: no provenance properties, no
    /// isCharacter, no updatedAt. Seeded as a literal string on purpose — building it from the C#
    /// record would silently acquire whatever new defaults the record grows, which is the one thing
    /// this test is here to catch.
    /// </summary>
    private const string PreChangeRegisterJson = """
        {
          "characters": [
            { "name": "רונית", "gender": "female", "role": "protagonist", "description": "הגיבורה", "aliases": ["רוני"] },
            { "name": "אלון", "gender": "male", "role": "supporting" }
          ]
        }
        """;

    /// <summary>
    /// The SAME legacy register in the casing the column actually holds. The pre-change writer was
    /// <c>JsonSerializer.Serialize(extracted, JsonOpts)</c> with no naming policy, so every register
    /// already on disk is PascalCase; it reads back only because the deserializer is
    /// case-insensitive. If that option is ever dropped, every persisted register silently
    /// deserializes to all-default values — which for provenance would mean every entry reading as a
    /// blank, unnamed non-character. Pinned here so the option cannot be removed quietly.
    /// </summary>
    private const string PreChangeRegisterJsonPascalCase = """
        {
          "Characters": [
            { "Name": "רונית", "Gender": "female", "Role": "protagonist", "Description": "הגיבורה", "Aliases": ["רוני"] },
            { "Name": "אלון", "Gender": "male", "Role": "supporting" }
          ]
        }
        """;

    private static CharacterRegister Deserialize(string json)
    {
        Assert.True(CharacterRegisterService.TryDeserialize(json, out var register, out var fault));
        Assert.Null(fault);
        Assert.NotNull(register);
        return register!;
    }

    // ── 1. Backward compatibility ───────────────────────────────────────────────────────────────

    [Fact]
    public void PreChangeRegister_DeserializesAsExtracted_NeverAsAuthorConfirmed()
    {
        var register = Deserialize(PreChangeRegisterJson);

        Assert.Equal(2, register.Characters.Count);

        // The stamp is absent -> null. A null stamp means "NO staleness signal"; if this ever became
        // DateTimeOffset.MinValue or UtcNow, every pre-existing AnalysisResult on every book would
        // light up as stale purely because the feature shipped.
        Assert.Null(register.UpdatedAt);

        foreach (var entry in register.Characters)
        {
            Assert.False(entry.GenderConfirmed, $"{entry.Name}: absent provenance must read as EXTRACTED, not confirmed.");
            Assert.False(entry.AliasesConfirmed, $"{entry.Name}: absent provenance must read as EXTRACTED, not confirmed.");
            Assert.False(entry.IsCharacterConfirmed, $"{entry.Name}: absent provenance must read as EXTRACTED, not confirmed.");
            Assert.False(entry.IsAuthorAdded, $"{entry.Name}: a legacy entry came from extraction, not from the author.");

            // The one field that defaults TRUE: including the entry at all was the extractor asserting
            // it is a character, so a legacy row must not read as suppressed.
            Assert.True(entry.IsCharacter, $"{entry.Name}: a legacy entry must read as a character, not as suppressed.");
        }

        var ronit = register.Characters.First();
        Assert.Equal("רונית", ronit.Name);
        Assert.Equal("female", ronit.Gender);
        Assert.Equal("protagonist", ronit.Role);
        Assert.Equal("הגיבורה", ronit.Description);
        Assert.Equal(new[] { "רוני" }, ronit.Aliases);

        // Absent `aliases` is empty, not null.
        Assert.Empty(register.Characters[1].Aliases);
    }

    [Fact]
    public void PreChangeRegister_InTheCasingActuallyOnDisk_AlsoDeserializesAsExtracted()
    {
        var register = Deserialize(PreChangeRegisterJsonPascalCase);

        Assert.Equal(2, register.Characters.Count);
        Assert.Null(register.UpdatedAt);
        Assert.Equal("רונית", register.Characters[0].Name);
        Assert.Equal("female", register.Characters[0].Gender);
        Assert.Equal(new[] { "רוני" }, register.Characters[0].Aliases);
        Assert.All(register.Characters, e =>
        {
            Assert.False(e.GenderConfirmed);
            Assert.False(e.AliasesConfirmed);
            Assert.False(e.IsCharacterConfirmed);
            Assert.False(e.IsAuthorAdded);
            Assert.True(e.IsCharacter);
        });
    }

    [Fact]
    public void ExplicitJsonNullCollections_CoerceToEmpty_NotNull()
    {
        // System.Text.Json NULL TRAP: a `= Array.Empty<T>()` initializer does NOT protect against an
        // EXPLICIT null in the payload — the null is written straight through and the collection
        // becomes null despite the initializer. PromptFactory.FormatCharacters reads `c.Aliases.Count`
        // unguarded, so an explicit `"aliases": null` used to be one NRE away from taking down the
        // whole analysis.
        var register = Deserialize("""
            { "characters": [ { "name": "רונית", "aliases": null } ] }
            """);

        Assert.NotNull(register.Characters[0].Aliases);
        Assert.Empty(register.Characters[0].Aliases);
    }

    [Fact]
    public void ExplicitNullCharactersArray_CoercesToEmpty_NotNull()
    {
        var register = Deserialize("""{ "characters": null }""");

        Assert.NotNull(register.Characters);
        Assert.Empty(register.Characters);
    }

    [Fact]
    public void ExplicitNullCollections_SurviveTheMerge_WithoutThrowing()
    {
        // The guard has to hold on the path that actually walks the entries, not just at the record.
        var local = Deserialize("""{ "characters": [ { "name": "Danny", "aliases": null } ] }""");
        var incoming = Deserialize("""{ "characters": null }""");

        var merged = CharacterRegisterMerge.Merge(local, incoming, DateTimeOffset.UtcNow);

        Assert.Single(merged.Characters);
        Assert.Empty(merged.Characters[0].Aliases);
    }

    [Fact]
    public void UnreadableRegisterJson_ReportsAFault_AndDoesNotThrow()
    {
        // The fail-safe must hand the fault BACK rather than swallow it: a catch that stays silent to
        // stay non-throwing blinds every outer logger, and an always-on layer then ships failures
        // invisibly.
        Assert.False(CharacterRegisterService.TryDeserialize("{ not json at all", out var register, out var fault));
        Assert.Null(register);
        Assert.NotNull(fault);
    }

    [Fact]
    public void RoundTrip_PreservesEveryProvenanceFlag()
    {
        var original = new CharacterRegister
        {
            UpdatedAt = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            Characters = new[]
            {
                new CharacterRegisterEntry
                {
                    Name = "Danny",
                    Gender = "male",
                    Aliases = new[] { "Daniel" },
                    IsCharacter = false,
                    IsAuthorAdded = true,
                    GenderConfirmed = true,
                    AliasesConfirmed = true,
                    IsCharacterConfirmed = true
                }
            }
        };

        var round = Deserialize(CharacterRegisterService.Serialize(original));
        var entry = round.Characters.Single();

        Assert.Equal(original.UpdatedAt, round.UpdatedAt);
        Assert.True(entry.GenderConfirmed);
        Assert.True(entry.AliasesConfirmed);
        Assert.True(entry.IsCharacterConfirmed);
        Assert.True(entry.IsAuthorAdded);
        Assert.False(entry.IsCharacter);
    }

    // ── 2. The merge rule, one test per row of d1 §3's table ────────────────────────────────────

    private static readonly DateTimeOffset MergeNow = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    private static CharacterRegister Reg(params CharacterRegisterEntry[] entries)
        => new() { Characters = entries };

    [Fact]
    public void Merge_ConfirmedGender_WinsOverTheNewExtraction()
    {
        var local = Reg(new CharacterRegisterEntry { Name = "רונית", Gender = "female", GenderConfirmed = true });
        var incoming = Reg(new CharacterRegisterEntry { Name = "רונית", Gender = "male" });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        var entry = merged.Characters.Single();
        Assert.Equal("female", entry.Gender);
        Assert.True(entry.GenderConfirmed);
    }

    [Fact]
    public void Merge_ConfirmedAliases_WinOverTheNewExtraction()
    {
        var local = Reg(new CharacterRegisterEntry
        {
            Name = "Danny",
            Aliases = new[] { "Daniel", "Dan" },
            AliasesConfirmed = true
        });
        var incoming = Reg(new CharacterRegisterEntry { Name = "Danny", Aliases = new[] { "Danno" } });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        Assert.Equal(new[] { "Daniel", "Dan" }, merged.Characters.Single().Aliases);
        Assert.True(merged.Characters.Single().AliasesConfirmed);
    }

    [Fact]
    public void Merge_ExtractedOnlyFields_AreReplacedByTheNewExtraction()
    {
        var local = Reg(new CharacterRegisterEntry
        {
            Name = "רונית",
            Gender = "male",              // a guess, NOT confirmed
            Role = "minor",
            Description = "stale",
            Aliases = new[] { "old" }
        });
        var incoming = Reg(new CharacterRegisterEntry
        {
            Name = "רונית",
            Gender = "female",
            Role = "protagonist",
            Description = "fresh",
            Aliases = new[] { "רוני" }
        });

        var entry = CharacterRegisterMerge.Merge(local, incoming, MergeNow).Characters.Single();

        Assert.Equal("female", entry.Gender);
        Assert.Equal("protagonist", entry.Role);
        Assert.Equal("fresh", entry.Description);
        Assert.Equal(new[] { "רוני" }, entry.Aliases);
        Assert.False(entry.GenderConfirmed);
    }

    [Fact]
    public void Merge_AuthorAddedEntry_IsLeftEntirelyUntouched()
    {
        // Row 3: none of an author-added entry's fields originated from extraction, so the replace
        // step must not reach ANY of them — not just the three provenance-flagged ones.
        var local = Reg(new CharacterRegisterEntry
        {
            Name = "Mira",
            Gender = "female",
            Role = "supporting",
            Description = "the author's own note",
            Aliases = new[] { "Mimi" },
            IsAuthorAdded = true,
            IsCharacterConfirmed = true
        });
        var incoming = Reg(new CharacterRegisterEntry
        {
            Name = "Mira",
            Gender = "male",
            Role = "minor",
            Description = "the model's guess",
            Aliases = new[] { "M" }
        });

        var entry = CharacterRegisterMerge.Merge(local, incoming, MergeNow).Characters.Single();

        Assert.Equal("female", entry.Gender);
        Assert.Equal("supporting", entry.Role);
        Assert.Equal("the author's own note", entry.Description);
        Assert.Equal(new[] { "Mimi" }, entry.Aliases);
        Assert.True(entry.IsAuthorAdded);
    }

    [Fact]
    public void Merge_NewlyDiscoveredCharacter_IsAdded_WithNoConfirmedFlags()
    {
        var local = Reg(new CharacterRegisterEntry { Name = "רונית" });
        var incoming = Reg(
            new CharacterRegisterEntry { Name = "רונית" },
            new CharacterRegisterEntry { Name = "אלון", Gender = "male" });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        Assert.Equal(2, merged.Characters.Count);
        var added = merged.Characters.Single(c => c.Name == "אלון");
        Assert.Equal("male", added.Gender);
        Assert.True(added.IsCharacter);
        Assert.False(added.IsAuthorAdded);
        Assert.False(added.GenderConfirmed);
        Assert.False(added.AliasesConfirmed);
        Assert.False(added.IsCharacterConfirmed);
    }

    [Fact]
    public void Merge_ExtractionCannotConfirmAnything_EvenIfItClaimsTo()
    {
        // The extraction JSON is deserialized into the SAME record, so a model that echoes the schema
        // back could emit `"genderConfirmed": true`. Only a human may set a confirmation flag.
        var incoming = Reg(new CharacterRegisterEntry
        {
            Name = "אלון",
            GenderConfirmed = true,
            AliasesConfirmed = true,
            IsCharacterConfirmed = true,
            IsAuthorAdded = true
        });

        var added = CharacterRegisterMerge.Merge(null, incoming, MergeNow).Characters.Single();

        Assert.False(added.GenderConfirmed);
        Assert.False(added.AliasesConfirmed);
        Assert.False(added.IsCharacterConfirmed);
        Assert.False(added.IsAuthorAdded);
    }

    [Fact]
    public void Merge_LocalEntryTheExtractionDidNotMention_IsKept_NotDeleted()
    {
        // The pre-pass reads only the first ~2000 words, so "the extractor didn't mention them" says
        // NOTHING about whether the character exists. Deleting here would drop real characters purely
        // for first appearing on page 50.
        var local = Reg(
            new CharacterRegisterEntry { Name = "רונית" },
            new CharacterRegisterEntry { Name = "יעל", Gender = "female", Role = "supporting" });
        var incoming = Reg(new CharacterRegisterEntry { Name = "רונית" });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        Assert.Equal(2, merged.Characters.Count);
        var kept = merged.Characters.Single(c => c.Name == "יעל");
        Assert.Equal("female", kept.Gender);
        Assert.Equal("supporting", kept.Role);
        // KEPT is not the same as CONFIRMED: nothing about surviving a merge makes a guess authored.
        Assert.False(kept.GenderConfirmed);
    }

    [Fact]
    public void Merge_SuppressedEntry_IsNeverResurrected()
    {
        var local = Reg(new CharacterRegisterEntry
        {
            Name = "הרוח",
            IsCharacter = false,
            IsCharacterConfirmed = true
        });
        var incoming = Reg(new CharacterRegisterEntry { Name = "הרוח", Gender = "female", Role = "minor" });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        var entry = merged.Characters.Single();
        Assert.False(entry.IsCharacter);
        Assert.True(entry.IsCharacterConfirmed);
        // The extraction's fields must not leak in through the back door either.
        Assert.Null(entry.Gender);
        Assert.Null(entry.Role);
    }

    [Fact]
    public void Merge_SuppressedEntry_IsNotResurrectedViaAnAliasMatchEither()
    {
        var local = Reg(new CharacterRegisterEntry
        {
            Name = "הרוח",
            Aliases = new[] { "הרוח הקרה" },
            IsCharacter = false,
            IsCharacterConfirmed = true
        });
        var incoming = Reg(new CharacterRegisterEntry { Name = "הרוח הקרה" });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        Assert.Single(merged.Characters);
        Assert.False(merged.Characters[0].IsCharacter);
    }

    [Fact]
    public void Merge_IsCharacterFalseWithoutConfirmation_IsNotTreatedAsSuppression()
    {
        // Only an AUTHOR decision suppresses. A bare isCharacter=false with no confirmation is not
        // one, so a fresh extraction is free to correct it.
        var local = Reg(new CharacterRegisterEntry { Name = "הרוח", IsCharacter = false });
        var incoming = Reg(new CharacterRegisterEntry { Name = "הרוח", Role = "minor" });

        var entry = CharacterRegisterMerge.Merge(local, incoming, MergeNow).Characters.Single();

        Assert.True(entry.IsCharacter);
        Assert.Equal("minor", entry.Role);
    }

    [Theory]
    // The extraction surfaces the character under a name the local entry lists as an alias.
    [InlineData("Danny", "Daniel", "Daniel", "")]
    // ...and the reverse: the local name appears in the extraction's aliases.
    [InlineData("Danny", "", "Daniel", "Danny")]
    // ...and a plain name match that only differs by case and surrounding whitespace.
    [InlineData("Danny", "", "  danny  ", "")]
    public void Merge_AliasFallbackMatching_TreatsTheEntriesAsTheSameCharacter(
        string localName,
        string localAlias,
        string incomingName,
        string incomingAlias)
    {
        var local = Reg(new CharacterRegisterEntry
        {
            Name = localName,
            Gender = "male",
            GenderConfirmed = true,
            Aliases = localAlias.Length == 0 ? Array.Empty<string>() : new[] { localAlias }
        });
        var incoming = Reg(new CharacterRegisterEntry
        {
            Name = incomingName,
            Gender = "female",
            Aliases = incomingAlias.Length == 0 ? Array.Empty<string>() : new[] { incomingAlias }
        });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        // ONE character, not two, and the confirmed gender survived.
        Assert.Single(merged.Characters);
        Assert.Equal("male", merged.Characters[0].Gender);
        // Name is identity: it stays local, so the entry cannot be orphaned from future matches.
        Assert.Equal(localName, merged.Characters[0].Name);
    }

    [Fact]
    public void Merge_TheSameCharacterTwiceInOneExtraction_DoesNotDuplicateIt()
    {
        var incoming = Reg(
            new CharacterRegisterEntry { Name = "Danny", Aliases = new[] { "Daniel" } },
            new CharacterRegisterEntry { Name = "Daniel" });

        var merged = CharacterRegisterMerge.Merge(null, incoming, MergeNow);

        Assert.Single(merged.Characters);
    }

    [Fact]
    public void Merge_NormalizesBlankAndDuplicateEntries()
    {
        var local = Reg(
            new CharacterRegisterEntry { Name = "  רונית  ", Aliases = new[] { "רוני", "  רוני ", "", "רונית" } },
            new CharacterRegisterEntry { Name = "   " });

        var merged = CharacterRegisterMerge.Merge(local, null, MergeNow);

        var entry = Assert.Single(merged.Characters);
        Assert.Equal("רונית", entry.Name);
        // Duplicates collapse; an alias equal to the entry's own name is dropped.
        Assert.Equal(new[] { "רוני" }, entry.Aliases);
    }

    // ── 2b. Duplicate ENTRY collapse (fix-plan c02) ─────────────────────────────────────────────
    //
    // Normalize used to de-duplicate only ALIASES, so one character could sit in the register twice.
    // That broke the surface at both ends: NG0955 on the client (rows tracked by name) and, server
    // side, ApplyOne's FindIndex resolving every edit to the FIRST occurrence forever. The collapse
    // must lose NO author state, so each of these pins one field of the collapse rule.

    [Fact]
    public void Normalize_CollapsesTwoEntriesForOneCharacter_UnioningEveryAuthorFlag()
    {
        var register = Reg(
            new CharacterRegisterEntry { Name = "דנה", Gender = "female", GenderConfirmed = true },
            new CharacterRegisterEntry { Name = " דנה ", Role = "protagonist", Aliases = new[] { "דנצ'י" }, AliasesConfirmed = true, IsAuthorAdded = true });

        var entry = Assert.Single(CharacterRegisterMerge.Normalize(register));

        Assert.Equal("דנה", entry.Name);
        // A flag set on EITHER duplicate survives: dropping one would let a re-extraction overwrite
        // something a human blessed.
        Assert.True(entry.GenderConfirmed);
        Assert.True(entry.AliasesConfirmed);
        Assert.True(entry.IsAuthorAdded);
        Assert.Equal("female", entry.Gender);
        // Role has no provenance flag, so the survivor's null yields to the duplicate's value.
        Assert.Equal("protagonist", entry.Role);
        Assert.Equal(new[] { "דנצ'י" }, entry.Aliases);
    }

    [Fact]
    public void Normalize_ConflictingConfirmedGenders_TheFirstOccurrenceWins_AndStaysConfirmed()
    {
        // THE DELIBERATE DECISION (argued at CollapseDuplicate): there is no per-field timestamp, so
        // "most recent wins" is not available; first-in-order is what ApplyOne's FindIndex already
        // resolved to, so it changes the fewest observable answers and keeps Normalize idempotent.
        var register = Reg(
            new CharacterRegisterEntry { Name = "דנה", Gender = "female", GenderConfirmed = true },
            new CharacterRegisterEntry { Name = "דנה", Gender = "male", GenderConfirmed = true });

        var entry = Assert.Single(CharacterRegisterMerge.Normalize(register));

        Assert.Equal("female", entry.Gender);
        // The losing VALUE is dropped; the CONFIRMATION is not. The author can now re-edit the one
        // surviving row, which is exactly what the duplicate made impossible.
        Assert.True(entry.GenderConfirmed);
    }

    [Fact]
    public void Normalize_ASuppressedDuplicate_SuppressesTheSurvivor()
    {
        // Suppression wins in BOTH orders. Resurrecting a banished name is the irreversible error;
        // hiding a visible one is undone with a single Restore click.
        var suppressedSecond = Reg(
            new CharacterRegisterEntry { Name = "הרוח" },
            new CharacterRegisterEntry { Name = "הרוח", IsCharacter = false, IsCharacterConfirmed = true });
        var suppressedFirst = Reg(
            new CharacterRegisterEntry { Name = "הרוח", IsCharacter = false, IsCharacterConfirmed = true },
            new CharacterRegisterEntry { Name = "הרוח" });

        foreach (var register in new[] { suppressedSecond, suppressedFirst })
        {
            var entry = Assert.Single(CharacterRegisterMerge.Normalize(register));
            Assert.False(entry.IsCharacter);
            Assert.True(entry.IsCharacterConfirmed);
            Assert.True(CharacterRegisterMerge.IsSuppressed(entry));
        }
    }

    [Fact]
    public void Normalize_CollapsingByAlias_KeepsTheDroppedNameAsAnAlias()
    {
        // The dropped entry's NAME is a surface form the register demonstrably held for this
        // character. Losing it would NARROW the matching key and let the next extraction re-split the
        // character into a fresh duplicate.
        var register = Reg(
            new CharacterRegisterEntry { Name = "Dana" },
            new CharacterRegisterEntry { Name = "Danny", Aliases = new[] { "Dana" } });

        var entry = Assert.Single(CharacterRegisterMerge.Normalize(register));

        Assert.Equal("Dana", entry.Name);
        Assert.Equal(new[] { "Danny" }, entry.Aliases);
    }

    [Fact]
    public void Normalize_IsIdempotent_OverItsOwnCollapsedOutput()
    {
        // The collapse matches against the ACCUMULATED survivor, so a second pass must find nothing
        // left to collapse. Without that, repeated reads would keep rewriting the register.
        var register = Reg(
            new CharacterRegisterEntry { Name = "דנה", Gender = "female", GenderConfirmed = true },
            new CharacterRegisterEntry { Name = "דנה", Aliases = new[] { "דנצ'י" } },
            new CharacterRegisterEntry { Name = "אלון" });

        var once = CharacterRegisterMerge.Normalize(register);
        var twice = CharacterRegisterMerge.Normalize(new CharacterRegister { Characters = once });

        Assert.Equal(2, once.Count);
        Assert.Equal(once.Select(e => e.Name), twice.Select(e => e.Name));
        Assert.Equal(once[0].Aliases, twice[0].Aliases);
        Assert.Equal(once[0].Gender, twice[0].Gender);
        Assert.Equal(once[0].GenderConfirmed, twice[0].GenderConfirmed);
    }

    [Fact]
    public void Merge_TwoIncomingEntriesThatMatchOneLocalButNotEachOther_StillProduceOneEntry()
    {
        // The collapse in Normalize did NOT make Merge's own already-appended guard redundant, because
        // Matches is not TRANSITIVE. "Dana" and "Dani" do not match each other (different names,
        // neither carrying the other as an alias), so Normalize leaves both; the local entry matches
        // BOTH through its alias. The first consumes the local, and the second reaches the guard with
        // no unconsumed local left. This is the case that keeps that guard alive, and the existing
        // Merge_TheSameCharacterTwiceInOneExtraction test no longer reaches it (the collapse now
        // answers that one upstream).
        var local = Reg(new CharacterRegisterEntry { Name = "Dana", Aliases = new[] { "Dani" }, AliasesConfirmed = true });
        var incoming = Reg(
            new CharacterRegisterEntry { Name = "Dana" },
            new CharacterRegisterEntry { Name = "Dani" });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        var entry = Assert.Single(merged.Characters);
        Assert.Equal("Dana", entry.Name);
    }

    [Fact]
    public void Merge_CollapsingADuplicateLocalEntry_DoesNotBumpTheStamp()
    {
        // Normalization is REPAIR, not new content. Bumping here would mark every prior AnalysisResult
        // on every legacy book stale the first time anything touched its register.
        var stamped = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var local = new CharacterRegister
        {
            UpdatedAt = stamped,
            Characters = new[]
            {
                new CharacterRegisterEntry { Name = "דנה" },
                new CharacterRegisterEntry { Name = "דנה" }
            }
        };

        var merged = CharacterRegisterMerge.Merge(local, null, MergeNow);

        Assert.Single(merged.Characters);
        Assert.Equal(stamped, merged.UpdatedAt);
    }

    // ── 3. The invalidation stamp (d1 §4) ───────────────────────────────────────────────────────

    [Fact]
    public void Merge_StampsUpdatedAt_WhenTheMergeChangedSomething()
    {
        var local = Reg(new CharacterRegisterEntry { Name = "רונית" });
        var incoming = Reg(new CharacterRegisterEntry { Name = "אלון" });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        Assert.Equal(MergeNow, merged.UpdatedAt);
    }

    [Fact]
    public void Merge_PreservesTheOldStamp_WhenNothingChanged()
    {
        // A no-op re-extraction must NOT bump the stamp: doing so would make every already-computed
        // AnalysisResult on the book read as stale for no reason at all.
        var previous = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var local = new CharacterRegister
        {
            UpdatedAt = previous,
            Characters = new[] { new CharacterRegisterEntry { Name = "רונית", Gender = "female", Aliases = new[] { "רוני" } } }
        };
        var incoming = Reg(new CharacterRegisterEntry { Name = "רונית", Gender = "female", Aliases = new[] { "רוני" } });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        Assert.Equal(previous, merged.UpdatedAt);
    }

    [Fact]
    public void Merge_OverALegacyRegister_LeavesTheStampNull_WhenNothingChanged()
    {
        // A register persisted before provenance shipped has no stamp. A no-op merge must not invent
        // one — "never stale" is the correct default, not "stale as of now".
        var local = Deserialize(PreChangeRegisterJson);
        var incoming = Reg(
            new CharacterRegisterEntry { Name = "רונית", Gender = "female", Role = "protagonist", Description = "הגיבורה", Aliases = new[] { "רוני" } },
            new CharacterRegisterEntry { Name = "אלון", Gender = "male", Role = "supporting" });

        var merged = CharacterRegisterMerge.Merge(local, incoming, MergeNow);

        Assert.Null(merged.UpdatedAt);
    }

    // ── 4. The FAIL-SAFE contract of LoadCharacterRegisterAsync (unchanged) ──────────────────────

    [Fact]
    public async Task LoadCharacterRegister_UnreadableJson_DegradesToNull_AndDoesNotFailTheAnalysis()
    {
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookAsync(db);

        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = "{ this is not json" });
        await db.SaveChangesAsync();

        var context = await provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
            AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);

        // The analysis ITSELF succeeded (we have a context with the target text); only the character
        // info degraded. That is the fail-safe contract.
        Assert.False(string.IsNullOrWhiteSpace(context.TargetText));
        Assert.Null(context.Characters);

        // ...and the unreadable value was NOT overwritten by a re-extraction: clobbering what we could
        // not read is exactly how author edits disappear silently.
        var bible = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        Assert.Equal("{ this is not json", bible.CharacterRegisterJson);
    }

    [Fact]
    public async Task LoadCharacterRegister_StillPropagatesCancellation()
    {
        using var provider = BuildProvider(slowRouter: true);
        var db = provider.GetRequiredService<AppDbContext>();
        var (_, chapterId) = await SeedBookAsync(db);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
                AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", cts.Token));
    }

    [Fact]
    public async Task LoadCharacterRegister_SuppressedEntries_AreNotHandedToTheAnalysis()
    {
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookAsync(db);

        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = CharacterRegisterService.Serialize(Reg(
                new CharacterRegisterEntry { Name = "רונית" },
                new CharacterRegisterEntry { Name = "הרוח", IsCharacter = false, IsCharacterConfirmed = true }))
        });
        await db.SaveChangesAsync();

        var context = await provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
            AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);

        var visible = Assert.Single(context.Characters!.Characters);
        Assert.Equal("רונית", visible.Name);

        // Filtering is a VIEW, not a write: the suppressed entry is still persisted, or the next merge
        // would have nothing to suppress against. Asserted by re-reading the register (not by a
        // substring probe on the column, which would also pass on a differently-shaped payload).
        var bible = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        var persisted = Deserialize(bible.CharacterRegisterJson!);
        Assert.Contains(persisted.Characters, c => c.Name == "הרוח" && CharacterRegisterMerge.IsSuppressed(c));
    }

    [Fact]
    public async Task LoadCharacterRegister_LegacyMalformedRegister_ReachesTheAnalysisNormalized()
    {
        // The analysis read used to be the ONE reader of a stored register that skipped Normalize, so a
        // legacy register described the same character to the model twice while GET showed one collapsed
        // row. Every seeded defect here is one Normalize repairs: a duplicate pair, a name matched only
        // through the other side's aliases, an untrimmed name, and a blank-named entry.
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookAsync(db);

        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = CharacterRegisterService.Serialize(Reg(
                new CharacterRegisterEntry { Name = "דניאל", Gender = "male" },
                new CharacterRegisterEntry { Name = "  דניאל  " },
                new CharacterRegisterEntry { Name = "דני", Aliases = new[] { "דניאל" } },
                new CharacterRegisterEntry { Name = "   " },
                new CharacterRegisterEntry { Name = "רונית" }))
        });
        await db.SaveChangesAsync();

        var context = await provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
            AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);

        var names = context.Characters!.Characters.Select(c => c.Name).ToList();
        Assert.Equal(new[] { "דניאל", "רונית" }, names);

        // Non-vacuity: the seeded column really did hold the malformed shape this asserts was repaired,
        // so the test cannot pass by the register having been empty or unreadable all along.
        var bible = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        Assert.Equal(5, Deserialize(bible.CharacterRegisterJson!).Characters.Count);
    }

    [Fact]
    public async Task LoadCharacterRegister_DuplicateWhereOneCopyIsSuppressed_ReachesTheAnalysisNotAtAll()
    {
        // The reason normalization and suppression-filtering are ONE funnel in a fixed ORDER rather than
        // two steps: collapsing the duplicate lets suppression win onto the survivor. Filter first and the
        // unsuppressed copy survives, handing the model a character the author struck out - the opposite
        // of what the register API reports for the same column.
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookAsync(db);

        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = CharacterRegisterService.Serialize(Reg(
                new CharacterRegisterEntry { Name = "הרוח" },
                new CharacterRegisterEntry { Name = "הרוח", IsCharacter = false, IsCharacterConfirmed = true },
                new CharacterRegisterEntry { Name = "רונית" }))
        });
        await db.SaveChangesAsync();

        var context = await provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
            AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);

        var visible = Assert.Single(context.Characters!.Characters);
        Assert.Equal("רונית", visible.Name);
    }

    [Fact]
    public async Task LoadCharacterRegister_AllSuppressedRegister_DoesNotReExtractAtAll()
    {
        // REPLACES a test that asserted the opposite and passed VACUOUSLY. It believed an all-suppressed
        // register falls through to the pre-pass and is rescued by the merge; in fact the gate counts
        // stored entries whether or not they are suppressed, so the method returns early and NOTHING
        // runs. Its assertions held only because the seeded row was never rewritten - they would have
        // held for any implementation, including one that erased the suppression.
        //
        // The non-vacuity guard here is the router Verify: it fails if the pre-pass ever fires, which is
        // the only thing that could put the suppression at risk. Asserting on the persisted JSON alone
        // CANNOT distinguish these cases, because a merge over this register legitimately reproduces
        // byte-identical output (the suppressed entry wins, and the stamp is preserved when nothing
        // changed) - that identity is exactly what made the old test vacuous.
        using var provider = BuildProvider(out var router, extractedCharacterName: "רונית");
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookAsync(db);

        var seededJson = CharacterRegisterService.Serialize(new CharacterRegister
        {
            Characters = new[]
            {
                new CharacterRegisterEntry { Name = "רונית", IsCharacter = false, IsCharacterConfirmed = true }
            }
        });
        db.BookBibles.Add(new BookBible { BookId = bookId, CharacterRegisterJson = seededJson });
        await db.SaveChangesAsync();

        var context = await provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
            AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);

        var extractionPrompt = provider.GetRequiredService<PromptFactory>().GetCharacterExtractionPrompt("he");
        router.Verify(
            r => r.CompleteAsync(It.Is<AiRequest>(q => q.Instruction == extractionPrompt), It.IsAny<CancellationToken>()),
            Times.Never);

        // The column was not rewritten, and the suppressed entry is still hidden from the analysis.
        var bible = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        Assert.Equal(seededJson, bible.CharacterRegisterJson);
        Assert.Empty(context.Characters!.Characters);
    }

    [Fact]
    public async Task LoadCharacterRegister_ReExtractionOverAnEmptyRegister_WritesThroughTheMerge()
    {
        // The one production state that DOES reach the merge: a stored register with zero entries.
        // Non-vacuity: a plain Serialize(extracted) overwrite would satisfy the character assertion just
        // as well, so the discriminator is the STAMP - only CharacterRegisterMerge sets UpdatedAt, and
        // it sets it only when the entry set actually changed.
        using var provider = BuildProvider(out var router, extractedCharacterName: "רונית");
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookAsync(db);

        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = CharacterRegisterService.Serialize(new CharacterRegister())
        });
        await db.SaveChangesAsync();

        await provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
            AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);

        var extractionPrompt = provider.GetRequiredService<PromptFactory>().GetCharacterExtractionPrompt("he");
        router.Verify(
            r => r.CompleteAsync(It.Is<AiRequest>(q => q.Instruction == extractionPrompt), It.IsAny<CancellationToken>()),
            Times.Once);

        var bible = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        var persisted = Deserialize(bible.CharacterRegisterJson!);
        var entry = Assert.Single(persisted.Characters);
        Assert.Equal("רונית", entry.Name);
        Assert.False(entry.GenderConfirmed); // a fresh extraction confirms nothing
        Assert.NotNull(persisted.UpdatedAt);
    }

    // ── 5. The re-extraction write must not clobber an author edit made DURING the pre-pass (c01) ─

    [Fact]
    public async Task LoadCharacterRegister_AuthorEditDuringThePrePass_SurvivesTheMergeWrite()
    {
        // The register read at the top of LoadCharacterRegisterAsync is a snapshot taken BEFORE a
        // multi-second local-model call. An author PATCH landing inside that window used to be read by
        // nobody and overwritten wholesale by the merge write, with no error and no log line.
        //
        // DETERMINISTIC WITHOUT THREADS: the router mock's own callback performs the author edit, so
        // the write is guaranteed to land strictly between the pre-call read and the merge. Same
        // side-effecting-fake technique the tracker TOCTOU test used.
        //
        // SEEDING: an EMPTY (zero-entry) register is the one state that actually reaches the merge -
        // the gate returns early on any non-empty stored register, so seeding characters here would
        // have the test pass against the un-fixed code for the wrong reason.
        using var provider = BuildProvider(out var router);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookAsync(db);

        db.BookBibles.Add(new BookBible
        {
            BookId = bookId,
            CharacterRegisterJson = CharacterRegisterService.Serialize(new CharacterRegister())
        });
        await db.SaveChangesAsync();

        var extractionPrompt = provider.GetRequiredService<PromptFactory>().GetCharacterExtractionPrompt("he");
        var authorEditFired = false;
        string? authorEditError = null;

        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns<AiRequest, CancellationToken>(async (request, unusedCt) =>
            {
                if (request.Instruction == extractionPrompt)
                {
                    // The author edits the register on ANOTHER request while the model runs. Its own
                    // DI scope means its own AppDbContext, exactly as in production.
                    using var scope = provider.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<CharacterRegisterService>();
                    (_, authorEditError) = await service.ApplyEditsAsync(
                        bookId,
                        new UpdateCharacterRegisterRequest(new[]
                        {
                            // A confirmed gender that CONTRADICTS what the extraction is about to say,
                            // and a character the extraction never mentions at all.
                            new CharacterRegisterEditDto("רונית", "upsert", Gender: "male"),
                            new CharacterRegisterEditDto("אלון", "upsert")
                        }),
                        CancellationToken.None);
                    authorEditFired = true;
                }

                // The extraction's own answer: רונית, gendered female.
                return new AiResponse
                {
                    Content = JsonSerializer.Serialize(new[] { new { name = "רונית", gender = "female", role = "protagonist" } }),
                    Model = "test",
                    Provider = "test"
                };
            });

        await provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
            AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);

        // NON-VACUITY: if the pre-pass never ran, or the author write silently failed, everything
        // below would hold for reasons that have nothing to do with the race.
        Assert.True(authorEditFired, "The author edit never ran, so this test proved nothing about the race.");
        Assert.Null(authorEditError);
        router.Verify(
            r => r.CompleteAsync(It.Is<AiRequest>(q => q.Instruction == extractionPrompt), It.IsAny<CancellationToken>()),
            Times.Once);

        var bible = await db.BookBibles.AsNoTracking().FirstAsync(b => b.BookId == bookId);
        var persisted = Deserialize(bible.CharacterRegisterJson!);

        // The author's confirmed gender survived. The extraction said "female"; merging the PRE-CALL
        // snapshot instead of the current value is what silently replaced it with the guess.
        var ronit = persisted.Characters.SingleOrDefault(c => c.Name == "רונית");
        Assert.NotNull(ronit);
        Assert.Equal("male", ronit!.Gender);
        Assert.True(ronit.GenderConfirmed);

        // ...and so did the character the author added by hand, which the extraction never saw.
        var alon = persisted.Characters.SingleOrDefault(c => c.Name == "אלון");
        Assert.NotNull(alon);
        Assert.True(alon!.IsAuthorAdded);
    }

    [Fact]
    public async Task LoadCharacterRegister_BibleRowCreatedDuringThePrePass_IsAdoptedNotDuplicated()
    {
        // Same race, but with NO BookBible row at the first read: the author's edit CREATES it. The
        // re-read has nothing to reload, so it must query and ADOPT the new row. Adding a second one
        // hits the unique index on BookId (AppDbContext.cs) with an unhandled DbUpdateException, i.e.
        // a 500 on the analysis. The in-memory provider does not enforce that index, so this asserts
        // the row COUNT, which is the condition the index would have rejected.
        using var provider = BuildProvider(out var router);
        var db = provider.GetRequiredService<AppDbContext>();
        var (bookId, chapterId) = await SeedBookAsync(db);

        var extractionPrompt = provider.GetRequiredService<PromptFactory>().GetCharacterExtractionPrompt("he");
        var authorEditFired = false;
        string? authorEditError = null;

        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Returns<AiRequest, CancellationToken>(async (request, unusedCt) =>
            {
                if (request.Instruction == extractionPrompt)
                {
                    using var scope = provider.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<CharacterRegisterService>();
                    (_, authorEditError) = await service.ApplyEditsAsync(
                        bookId,
                        new UpdateCharacterRegisterRequest(new[] { new CharacterRegisterEditDto("אלון", "upsert") }),
                        CancellationToken.None);
                    authorEditFired = true;
                }

                return new AiResponse
                {
                    Content = JsonSerializer.Serialize(new[] { new { name = "רונית", gender = "female", role = "protagonist" } }),
                    Model = "test",
                    Provider = "test"
                };
            });

        await provider.GetRequiredService<IAnalysisContextService>().BuildContextAsync(
            AnalysisScope.Chapter, chapterId, AnalysisType.Proofread, "he", CancellationToken.None);

        Assert.True(authorEditFired, "The author edit never ran, so this test proved nothing about the race.");
        Assert.Null(authorEditError);

        var bibles = await db.BookBibles.AsNoTracking().Where(b => b.BookId == bookId).ToListAsync();
        Assert.Single(bibles);

        var persisted = Deserialize(bibles[0].CharacterRegisterJson!);
        Assert.Contains(persisted.Characters, c => c.Name == "אלון" && c.IsAuthorAdded);
        Assert.Contains(persisted.Characters, c => c.Name == "רונית");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<(Guid BookId, Guid ChapterId)> SeedBookAsync(AppDbContext db)
    {
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Register Book", Language = "he" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "פרק",
            ContentText = "רונית דיברה עם אלון."
        });
        await db.SaveChangesAsync();
        return (bookId, chapterId);
    }

    private static ServiceProvider BuildProvider(bool slowRouter = false, string? extractedCharacterName = null)
        => BuildProvider(out _, slowRouter, extractedCharacterName);

    /// <summary>
    /// Same provider, but hands back the router mock so a test can assert whether the character
    /// extraction pre-pass actually FIRED. Some claims about this path cannot be settled from the
    /// persisted register alone.
    /// </summary>
    private static ServiceProvider BuildProvider(
        out Mock<IAiRouter> routerMock,
        bool slowRouter = false,
        string? extractedCharacterName = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The database name is resolved ONCE, outside the options lambda, on purpose. AddDbContext
        // registers DbContextOptions with a SCOPED lifetime by default, so a Guid generated inside the
        // lambda gives every DI scope its OWN store - and a test that opens a second scope to stand in
        // for a concurrent request would then write to a different database entirely and prove nothing.
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(databaseName));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var router = new Mock<IAiRouter>();
        if (slowRouter)
        {
            router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .Returns<AiRequest, CancellationToken>(async (_, ct) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return new AiResponse { Content = "[]", Model = "test", Provider = "test" };
                });
        }
        else
        {
            var payload = extractedCharacterName == null
                ? "[]"
                : JsonSerializer.Serialize(new[] { new { name = extractedCharacterName, gender = "female", role = "protagonist" } });
            router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AiResponse { Content = payload, Model = "test", Provider = "test" });
        }

        routerMock = router;
        services.AddSingleton(router.Object);
        services.Configure<AiOptions>(_ => { });
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<CharacterRegisterService>();

        return services.BuildServiceProvider();
    }
}
