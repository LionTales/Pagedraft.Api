using Microsoft.AspNetCore.Mvc;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Controllers;

/// <summary>
/// The author's surface over the book's character register
/// (character-register-editing plan, c1).
///
/// <para>Deliberately its own controller rather than more methods on <c>BooksController</c>, which is
/// already ~1240 lines — well past this workspace's ~700-line soft ceiling — and would only get
/// worse. The route stays book-scoped so the client sees one consistent
/// <c>/api/books/{bookId}/...</c> family.</para>
///
/// <para>Thin by design: every rule (matching key, provenance defaults, suppression, stamping) lives
/// in <see cref="CharacterRegisterService"/> and <see cref="CharacterRegisterMerge"/>, so the
/// endpoints and the re-extraction path can never drift apart.</para>
/// </summary>
[ApiController]
[Route("api/books/{bookId:guid}/character-register")]
public class CharacterRegisterController : ControllerBase
{
    private readonly CharacterRegisterService _registers;

    public CharacterRegisterController(CharacterRegisterService registers) => _registers = registers;

    /// <summary>
    /// GET the book's register WITH provenance, so the surface can show what the author confirmed and
    /// what the extractor guessed. Suppressed entries are included (flagged), because suppression is
    /// permanent and has to stay visible/restorable somewhere.
    ///
    /// 200 with <c>hasRegister=false</c> and an empty list when the book has no register yet — that is
    /// the empty state, not a 404; the book exists, its register just has not been built (it is
    /// extracted on the first analysis run that needs it).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<CharacterRegisterDto>> Get(Guid bookId, CancellationToken ct)
    {
        var dto = await _registers.GetAsync(bookId, ct);
        if (dto == null) return NotFound();
        return Ok(dto);
    }

    /// <summary>
    /// PATCH the author's edits: add a character, suppress/restore one, set a gender, replace aliases.
    /// The body is a BATCH applied in order, and the response is the SERVER's resulting register —
    /// the client reconciles against this rather than assuming its optimistic patch landed.
    ///
    /// <para>Partial semantics are the point of PATCH here: an omitted <c>gender</c>/<c>aliases</c>
    /// means "untouched", never "clear". A whole-register PUT is deliberately NOT offered — it would
    /// let a stale client silently drop characters (including author-confirmed ones) it never knew
    /// about.</para>
    ///
    /// 400 on a malformed edit (missing name, unrecognised op) AND on a semantically impossible one
    /// (a <c>restore</c> naming a character the register does not hold, see
    /// <see cref="CharacterRegisterEditDto"/>), 404 when the book does not exist. A 400 writes NOTHING:
    /// the batch is all-or-nothing.
    /// </summary>
    [HttpPatch]
    public async Task<ActionResult<CharacterRegisterDto>> Patch(
        Guid bookId,
        [FromBody] UpdateCharacterRegisterRequest req,
        CancellationToken ct)
    {
        var (result, error) = await _registers.ApplyEditsAsync(bookId, req, ct);
        if (error != null) return BadRequest(new { error });
        if (result == null) return NotFound();
        return Ok(result);
    }
}
