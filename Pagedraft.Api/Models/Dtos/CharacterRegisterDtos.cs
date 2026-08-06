namespace Pagedraft.Api.Models.Dtos;

// ─── Author-editable character register (character-register-editing plan, c1) ────────────────────
//
// JSON casing throughout is the System.Text.Json default the API already uses everywhere
// (camelCase) — the app calls AddControllers() with no naming-policy override.

/// <summary>
/// Response for <c>GET /api/books/{bookId}/character-register</c> and for every write on that route.
/// JSON (camelCase): bookId, hasRegister, updatedAt, characters.
///
/// <para><paramref name="HasRegister"/> distinguishes "this book's register has never been built"
/// from "the register exists and is empty". They look identical in a bare list and mean opposite
/// things to the author: the first is answered by running an analysis (the register is extracted on
/// the first run that needs it), the second means every character was suppressed.</para>
///
/// <para><paramref name="Characters"/> includes SUPPRESSED entries (<c>isCharacter=false</c>), so the
/// surface can show and un-suppress them. Suppression is permanent-by-design (a re-extraction may
/// never resurrect a suppressed entry), which only works if the entry stays visible somewhere.</para>
/// </summary>
public record CharacterRegisterDto(
    Guid BookId,
    bool HasRegister,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<CharacterRegisterEntryDto> Characters);

/// <summary>
/// One character, with its per-field provenance. JSON (camelCase): name, gender, role, description,
/// aliases, isCharacter, isAuthorAdded, genderConfirmed, aliasesConfirmed, isCharacterConfirmed.
///
/// <para>The three <c>*Confirmed</c> booleans are the feature: they tell the surface which values a
/// human blessed and which the extractor guessed, which is where the author's attention is worth
/// spending. <c>role</c> and <c>description</c> carry NO confirmation flag — they are
/// always-extracted, always-replaceable, and no edit path exposes them.</para>
///
/// <para><c>isCharacter=false</c> with <c>isCharacterConfirmed=true</c> is a SUPPRESSED entry: the
/// author said "this is not a character". Render it as suppressed/restorable, not as a character.</para>
/// </summary>
public record CharacterRegisterEntryDto(
    string Name,
    string? Gender,
    string? Role,
    string? Description,
    IReadOnlyList<string> Aliases,
    bool IsCharacter,
    bool IsAuthorAdded,
    bool GenderConfirmed,
    bool AliasesConfirmed,
    bool IsCharacterConfirmed);

/// <summary>
/// Body for <c>PATCH /api/books/{bookId}/character-register</c> — a batch of author edits applied in
/// order. JSON (camelCase): <c>{ "edits": [ ... ] }</c>.
///
/// <para>A batch (rather than one endpoint per operation) keeps the whole surface's save atomic and
/// gives the client back the SERVER's resulting register in one response, which is what an
/// optimistic-update UI has to reconcile against.</para>
/// </summary>
public record UpdateCharacterRegisterRequest(IReadOnlyList<CharacterRegisterEditDto>? Edits);

/// <summary>
/// One author edit. JSON (camelCase): name, op, gender, aliases.
///
/// <para><paramref name="Name"/> identifies the target character using the SAME matching key the
/// merge uses (trim + case-insensitive on name, with alias fallback), so an author can address a
/// character by an alias. Required.</para>
///
/// <para><paramref name="Op"/> is one of:</para>
/// <list type="bullet">
/// <item><c>"upsert"</c> (the default when omitted) — update the matched character, or CREATE it when
/// nothing matches. A created entry is marked <c>isAuthorAdded=true</c> and is exempt from the
/// extracted-only replace step forever.</item>
/// <item><c>"suppress"</c> — mark not-a-character: sets <c>isCharacter=false</c> +
/// <c>isCharacterConfirmed=true</c>. This is the "remove" operation, and it is deliberately a
/// suppression rather than a delete: only a persisted decision can stop a future re-extraction from
/// re-adding the character. Suppressing an unknown name creates the suppressed marker so a future
/// extraction is pre-empted.</item>
/// <item><c>"restore"</c> — the inverse: <c>isCharacter=true</c> + <c>isCharacterConfirmed=true</c>.
/// UNLIKE the two ops above, restore does NOT create on a no-match: a restore naming a character the
/// register does not hold is a <b>400</b>. Its whole meaning is "un-suppress an entry that exists", so
/// creating one would fabricate a character the author never asked for. Read the three no-match
/// verdicts together at <c>CharacterRegisterService.ApplyOne</c>.</item>
/// </list>
/// <para>Any other value is a 400. An unrecognised op is never treated as "upsert" — silently doing
/// something other than what was asked is worse than refusing.</para>
///
/// <para><paramref name="Gender"/>: ABSENT/null leaves the gender untouched. Any present value SETS
/// it and sets <c>genderConfirmed=true</c>. The empty string clears it to null while STILL confirming
/// — that is how an author says "the extractor's guess is wrong and I do not want one", which is a
/// different statement from "I have not looked".</para>
///
/// <para><paramref name="Aliases"/>: ABSENT/null leaves the aliases untouched. Any present array
/// REPLACES them and sets <c>aliasesConfirmed=true</c>; an empty array is a confirmed empty list.
/// Blank entries, duplicates (case-insensitive) and an alias equal to the character's own name are
/// dropped server-side.</para>
/// </summary>
public record CharacterRegisterEditDto(
    string? Name,
    string? Op = null,
    string? Gender = null,
    IReadOnlyList<string>? Aliases = null);
