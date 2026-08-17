using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Feedback;
using Xunit;
using static Pagedraft.Api.Tests.FeedbackTestData;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE VOTE HALF of Show C2: the one-vote upsert rule, target validation, the caps, and retract.
///
/// <para>The one-vote rule is the decision the whole feature rests on - C3 consumes the CURRENT opinion
/// about a target, not a history of opinions - so every case here asserts the ROW COUNT as well as the
/// row's contents. A test that only checked the returned DTO would pass identically against an
/// append-only table, which is the exact design this rejected.</para>
/// </summary>
public class FeedbackVoteTests
{
    [Fact]
    public async Task Vote_StoresTheRow_AtStatusNew_WithBothStampsEqual()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var dto = await VoteOkAsync(NewController(db), VoteFor(answer, FeedbackVerdicts.Down, "The steps are wrong."));

        Assert.Equal(FeedbackAreas.ChatAnswer, dto.Area);
        Assert.Equal(FeedbackTargetTypes.ConversationMessage, dto.TargetType);
        Assert.Equal(answer, dto.TargetId);
        Assert.Equal(FeedbackVerdicts.Down, dto.Verdict);
        Assert.Equal("The steps are wrong.", dto.Text);
        // Status is New on arrival - the other half of C3's consumption predicate - and StatusChangedAt
        // equals CreatedAt at insert, so a brand-new row can never look already-triaged.
        Assert.Equal(FeedbackStatuses.New, dto.Status);
        Assert.Equal(dto.CreatedAt, dto.StatusChangedAt);
        Assert.Null(dto.TargetDeletedAt);

        var stored = Assert.Single(await db.FeedbackItems.ToListAsync());
        Assert.Equal(dto.Id, stored.Id);
        Assert.Equal(Installation, stored.InstallationId);
        // Always null until the login lands - the column exists so that is an addition, not a migration.
        Assert.Null(stored.UserId);
    }

    /// <summary>
    /// THE UPSERT, PROVED BY THE ROW COUNT. A second vote on the same target from the same voter rewrites
    /// the row it already has; an append-only table would leave two behind and every reader would then
    /// have to decide which one is live.
    /// </summary>
    [Fact]
    public async Task Vote_Twice_RewritesTheOneRow_RatherThanAppendingASecond()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var first = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Up));
        var second = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(FeedbackVerdicts.Down, second.Verdict);
        Assert.Single(await db.FeedbackItems.ToListAsync());
    }

    /// <summary>
    /// A VERDICT FLIP KEEPS THE NOTE (d1 section (1)). The client prompts the reader to revise it, which
    /// is a UI concern - the server must not silently discard what they already wrote.
    /// </summary>
    [Fact]
    public async Task Vote_FlippingTheVerdict_KeepsTheExistingNote()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down, "It invented a menu item."));

        // No text on the flip at all - which is "leave the note alone", not "clear it".
        var flipped = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Up, text: null));

        Assert.Equal(FeedbackVerdicts.Up, flipped.Verdict);
        Assert.Equal("It invented a menu item.", flipped.Text);
    }

    [Fact]
    public async Task Vote_WithANewNote_ReplacesTheOldOne_AndAnEmptyNoteClearsIt()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down, "First note."));

        var revised = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down, "Second note."));
        Assert.Equal("Second note.", revised.Text);

        // A SUPPLIED-BUT-EMPTY note is the reader deliberately revising down to nothing, which is a
        // different request from omitting the field. Both are exercised in this suite so the asymmetry
        // cannot be flattened by accident.
        var cleared = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down, "   "));
        Assert.Null(cleared.Text);
        Assert.Single(await db.FeedbackItems.ToListAsync());
    }

    /// <summary>
    /// The key is per VOTER, so a second reader's opinion about the same answer is a second row. Without
    /// this, one reader's vote would overwrite another's and the whole table would hold one opinion per
    /// target.
    /// </summary>
    [Fact]
    public async Task Vote_FromASecondInstallation_IsASecondRow()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var mine = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Up, installationId: Installation));
        var theirs = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down, installationId: OtherInstallation));

        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Equal(2, await db.FeedbackItems.CountAsync());
    }

    /// <summary>
    /// <c>UserId ?? InstallationId</c>, PROVED ON THE UPPER HALF. When a user id exists it is the WHOLE
    /// key: the same person voting from a second browser is still one vote, so the second request updates
    /// rather than appends even though the installation id changed. Nothing produces an authenticated
    /// principal today, which is exactly why this rule needs a test rather than a deployment to be true.
    /// </summary>
    [Fact]
    public async Task Vote_WhenAUserIsAuthenticated_KeysOnTheUser_AcrossInstallations()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var asUser = NewControllerAsUser(db, "user-1");
        var first = await VoteOkAsync(asUser, VoteFor(answer, FeedbackVerdicts.Up, installationId: Installation));
        var second = await VoteOkAsync(asUser, VoteFor(answer, FeedbackVerdicts.Down, installationId: OtherInstallation));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(FeedbackVerdicts.Down, second.Verdict);
        var stored = Assert.Single(await db.FeedbackItems.ToListAsync());
        Assert.Equal("user-1", stored.UserId);
        // The freshest device stamp is kept: it is the one a later retract arrives with.
        Assert.Equal(OtherInstallation, stored.InstallationId);
    }

    /// <summary>
    /// An ANONYMOUS vote must never hijack a signed-in reader's row, even when the installation matches -
    /// the anonymous branch of the lookup additionally requires <c>UserId == null</c>.
    /// </summary>
    [Fact]
    public async Task Vote_Anonymously_DoesNotOverwriteASignedInReadersRow()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var signedIn = await VoteOkAsync(
            NewControllerAsUser(db, "user-1"), VoteFor(answer, FeedbackVerdicts.Up, installationId: Installation));
        var anonymous = await VoteOkAsync(
            NewController(db), VoteFor(answer, FeedbackVerdicts.Down, installationId: Installation));

        Assert.NotEqual(signedIn.Id, anonymous.Id);
        Assert.Equal(2, await db.FeedbackItems.CountAsync());
        Assert.Equal(
            FeedbackVerdicts.Up,
            (await db.FeedbackItems.SingleAsync(f => f.Id == signedIn.Id)).Verdict);
    }

    /// <summary>
    /// A RE-VOTE NEVER REGRESSES STATUS (d1 section (1)), and this is the guard that keeps C3 possible: a
    /// reader flipping their vote after the owner already confirmed a bug must not quietly put the row
    /// back in C3's inbox, which is what <c>Status = New</c> means.
    /// </summary>
    [Fact]
    public async Task Vote_AfterTriageHasMovedTheRow_LeavesStatusAndItsStampAlone()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var voted = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down, "Wrong."));

        var triaged = await controller.ChangeStatus(
            voted.Id, new FeedbackStatusRequest(FeedbackStatuses.ConfirmedBug), CancellationToken.None);
        var confirmed = Assert.IsType<FeedbackDto>(Assert.IsType<OkObjectResult>(triaged.Result).Value);
        Assert.Equal(FeedbackStatuses.ConfirmedBug, confirmed.Status);

        var flipped = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Up, "Actually fine."));

        Assert.Equal(FeedbackVerdicts.Up, flipped.Verdict);
        Assert.Equal("Actually fine.", flipped.Text);
        Assert.Equal(FeedbackStatuses.ConfirmedBug, flipped.Status);
        Assert.Equal(confirmed.StatusChangedAt, flipped.StatusChangedAt);
    }

    // ─── Target validation ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A feedback row pointing at nothing is unactionable, so it is refused at the door. NON-VACUOUS: the
    /// same database holds a message that DOES validate, so the rejection is the check acting rather than
    /// an empty target table refusing everything.
    /// </summary>
    [Fact]
    public async Task Vote_OnATargetThatDoesNotExist_Is400TargetNotFound_WhileARealTargetSucceeds()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);

        Assert.Equal(FeedbackErrors.TargetNotFound, await VoteRejectedAsync(controller, VoteFor(Guid.NewGuid())));
        Assert.Empty(await db.FeedbackItems.ToListAsync());

        await VoteOkAsync(controller, VoteFor(answer));
        Assert.Single(await db.FeedbackItems.ToListAsync());
    }

    /// <summary>
    /// A vote nobody can key cannot be deduped, so it defeats the one-vote rule entirely - it is refused
    /// rather than stored as an orphan (d1 section (1)).
    /// </summary>
    [Fact]
    public async Task Vote_WithNoVoterIdentityAtAll_Is400()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var error = await VoteRejectedAsync(
            NewController(db), VoteFor(answer, installationId: "   "));

        Assert.Equal(FeedbackErrors.VoterIdentityRequired, error);
        Assert.Empty(await db.FeedbackItems.ToListAsync());
    }

    [Theory]
    [InlineData(null, FeedbackTargetTypes.ConversationMessage, FeedbackVerdicts.Down, FeedbackErrors.AreaRequired)]
    [InlineData("suggestion-card", FeedbackTargetTypes.ConversationMessage, FeedbackVerdicts.Down, FeedbackErrors.AreaNotRecognized)]
    [InlineData(FeedbackAreas.ChatAnswer, null, FeedbackVerdicts.Down, FeedbackErrors.TargetTypeRequired)]
    [InlineData(FeedbackAreas.ChatAnswer, "suggestion", FeedbackVerdicts.Down, FeedbackErrors.TargetTypeNotRecognized)]
    [InlineData(FeedbackAreas.ChatAnswer, FeedbackTargetTypes.ConversationMessage, null, FeedbackErrors.VerdictRequired)]
    [InlineData(FeedbackAreas.ChatAnswer, FeedbackTargetTypes.ConversationMessage, "meh", FeedbackErrors.VerdictNotRecognized)]
    public async Task Vote_WithAValueOutsideTheVocabulary_Is400WithThatCode(
        string? area, string? targetType, string? verdict, string expected)
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var request = new FeedbackVoteRequest(area, targetType, answer, verdict, null, Installation, null);

        Assert.Equal(expected, await VoteRejectedAsync(controller, request));
        Assert.Empty(await db.FeedbackItems.ToListAsync());

        // NON-VACUITY: the very same controller and database accept the vocabulary-correct request, so the
        // rejections above are the allowlist acting rather than a write path that refuses everything.
        await VoteOkAsync(controller, VoteFor(answer));
        Assert.Single(await db.FeedbackItems.ToListAsync());
    }

    /// <summary>
    /// The cap is a REFUSAL and not a silent truncation - the note is the reader's own words. Asserted on
    /// both sides of the boundary so it is a cap rather than a coincidence.
    /// </summary>
    [Fact]
    public async Task Vote_WithANoteAtTheCap_Succeeds_AndOneCharacterOverIsRefused()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);

        var atTheCap = new string('x', FeedbackCaps.TextChars);
        var stored = await VoteOkAsync(controller, VoteFor(answer, text: atTheCap));
        Assert.Equal(FeedbackCaps.TextChars, stored.Text!.Length);

        var overTheCap = new string('x', FeedbackCaps.TextChars + 1);
        Assert.Equal(
            FeedbackErrors.TextTooLong,
            await VoteRejectedAsync(controller, VoteFor(answer, text: overTheCap)));

        // The refused note did not partially land: the row still holds the accepted one.
        Assert.Equal(atTheCap, (await db.FeedbackItems.SingleAsync()).Text);
    }

    // ─── Vote-time context ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Vote_StoresTheVoteTimeContext_AndAReVoteWithoutOneLeavesItAlone()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var book = Guid.NewGuid();
        var controller = NewController(db);
        var context = new FeedbackContextDto("/books/1/edit", book, null, "he", null);

        var voted = await VoteOkAsync(controller, VoteFor(answer, context: context));
        Assert.NotNull(voted.Context);
        Assert.Equal("/books/1/edit", voted.Context!.Route);
        Assert.Equal(book, voted.Context.BookId);
        Assert.Equal("he", voted.Context.UiLanguage);
        // RESERVED, not populated in v1 - no build stamp exists in this client or API today.
        Assert.Null(voted.Context.AppBuild);

        var reVoted = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Up, context: null));
        Assert.NotNull(reVoted.Context);
        Assert.Equal("/books/1/edit", reVoted.Context!.Route);
    }

    /// <summary>
    /// NULL-GUARD PARITY. <c>System.Text.Json</c> nulls out a property on an explicit JSON <c>null</c>, so
    /// a body carrying <c>"context": null</c> and a body omitting it entirely must land in the same place.
    /// This binds the actual serializer rather than constructing the record by hand, which is the only way
    /// the assertion is about the wire.
    /// </summary>
    [Fact]
    public async Task Vote_WithAnExplicitJsonNullContext_BehavesExactlyLikeAnAbsentOne()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var explicitNull = JsonSerializer.Deserialize<FeedbackVoteRequest>(
            """{"area":"chat-answer","targetType":"conversation-message","verdict":"down","context":null}""",
            options)!;
        var absent = JsonSerializer.Deserialize<FeedbackVoteRequest>(
            """{"area":"chat-answer","targetType":"conversation-message","verdict":"down"}""",
            options)!;

        Assert.Null(explicitNull.Context);
        Assert.Null(absent.Context);
        Assert.Null(explicitNull.Text);

        // And the write path treats that null as "no context", not as a crash or an empty blob.
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var dto = await VoteOkAsync(
            NewController(db), explicitNull with { TargetId = answer, InstallationId = Installation });

        Assert.Null(dto.Context);
        Assert.Null((await db.FeedbackItems.SingleAsync()).ContextJson);
    }

    [Fact]
    public async Task Vote_WithAnAbsentBody_Is400_RatherThanA500()
    {
        await using var db = NewDb();
        var result = await NewController(db).Vote(null, CancellationToken.None);
        Assert.Equal(FeedbackErrors.AreaRequired, ErrorCodeOf(Assert.IsType<BadRequestObjectResult>(result.Result).Value));
    }

    // ─── Retract ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retract_HardDeletesTheRow_LeavesOtherVotesAlone_And404sTheSecondTime()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var mine = await VoteOkAsync(controller, VoteFor(answer, installationId: Installation));
        var theirs = await VoteOkAsync(controller, VoteFor(answer, installationId: OtherInstallation));

        // NON-VACUITY: there really are two rows, so "leaves the other alone" is a claim.
        Assert.Equal(2, await db.FeedbackItems.CountAsync());

        Assert.IsType<NoContentResult>(await controller.Retract(mine.Id, CancellationToken.None));

        Assert.Equal(theirs.Id, (await db.FeedbackItems.SingleAsync()).Id);
        Assert.IsType<NotFoundObjectResult>(await controller.Retract(mine.Id, CancellationToken.None));
    }

    /// <summary>
    /// A retract genuinely clears the vote rather than parking it: voting again after a retract creates a
    /// NEW row, which is what "hard delete" has to mean for the widget's unvoted state to be honest.
    /// </summary>
    [Fact]
    public async Task Retract_ThenVoteAgain_StartsAFreshRow()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var first = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down, "A note."));
        await controller.Retract(first.Id, CancellationToken.None);

        var second = await VoteOkAsync(controller, VoteFor(answer, FeedbackVerdicts.Down));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(second.Text);
        Assert.Single(await db.FeedbackItems.ToListAsync());
    }
}
