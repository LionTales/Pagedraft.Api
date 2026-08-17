using System.Text.Json;
using Pagedraft.Api.Models.Dtos;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// THE ONE READER for <c>ConversationMessage.GroundingJson</c>, extracted when Show C2's triage evidence
/// became its second consumer. It reads with <see cref="ChatConversationStore.SnapshotJson"/> - the same
/// options the WRITER uses - so a reader can never disagree with the writer about casing, and a second
/// hand-rolled <c>Deserialize</c> beside this one cannot drift from it.
///
/// <para>IT NEVER THROWS, AND IT NEVER SWALLOWS. A blob that will not parse must not cost the caller the
/// row it was asking for (the author came for the transcript; the triage owner came for the feedback),
/// so a fault returns null - but the exception comes back OUT on <paramref name="error"/> rather than
/// disappearing, because a nested catch that swallows to stay non-throwing blinds every layer above it,
/// which is a recorded failure shape in this codebase and not a hypothesis. Each caller logs it with the
/// id that makes it findable.</para>
///
/// <para>Catches <see cref="Exception"/> and not only <see cref="JsonException"/>: <c>Deserialize</c> can
/// also throw <see cref="NotSupportedException"/> on a blob whose shape it cannot bind.</para>
/// </summary>
public static class ConversationGroundingSnapshot
{
    /// <summary>
    /// Reads a stored snapshot. Returns false with a null <paramref name="snapshot"/> and a non-null
    /// <paramref name="error"/> when the blob exists but did not parse; returns true with a null
    /// <paramref name="snapshot"/> when there was simply nothing stored (a user turn, or a failed answer,
    /// both of which carry no grounding by design).
    /// </summary>
    public static bool TryRead(
        string? json,
        out ConversationGroundingDto? snapshot,
        out Exception? error)
    {
        snapshot = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            snapshot = JsonSerializer.Deserialize<ConversationGroundingDto>(
                json, ChatConversationStore.SnapshotJson);
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }
}
