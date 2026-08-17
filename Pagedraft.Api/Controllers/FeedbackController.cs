using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Feedback;

namespace Pagedraft.Api.Controllers;

/// <summary>
/// FEEDBACK, AS INFRASTRUCTURE (Show C2). Mount #1 is Show's answers, but nothing on this route family
/// knows that: a vote names an <c>area</c> and a <c>targetType</c>/<c>targetId</c>, and mounting the same
/// widget on a proofread suggestion card later is one client change plus two allowlist constants - no new
/// endpoint, no migration.
///
/// <para>TWO HALVES WITH DIFFERENT VISIBILITY, and the split is the design (d1 section (4)). The VOTE
/// half (<c>POST</c>, <c>DELETE</c>) is always open, because the widget must keep working on a deployment
/// where the triage view is hidden - collecting the signal is the point, and reading it is the owner's
/// separate privilege. The TRIAGE half (<c>GET</c> list, <c>GET</c> detail, <c>PATCH</c> status) is gated
/// behind <c>Feedback:TriageEnabled</c> and returns a plain bodiless <c>404</c> when the flag is off, so
/// flag-off is indistinguishable from route-absent.</para>
///
/// <para>WHY A FLAG AND NOT AUTH: PageDraft has no <c>[Authorize]</c>, <c>AddAuthentication</c> or
/// Identity anywhere yet, and the triage detail composes MANUSCRIPT-BEARING evidence. The flag is what
/// keeps that surface off a hosted deployment until the Pagewise-style JWT + Google login lands; on that
/// day these endpoints gain <c>[Authorize]</c> and an ownership check exactly like the five
/// <c>/api/conversations</c> ones, and the flag flips to true in production.</para>
///
/// <para>Thin by construction: the one-vote upsert rule, the target validation, the caps and the
/// transition graph all live in <see cref="FeedbackService"/> where the tests drive them directly, so the
/// endpoint cannot drift from what they pin. This controller decides status codes and reads the flag.</para>
/// </summary>
[ApiController]
[Route("api/feedback")]
public class FeedbackController : ControllerBase
{
    private readonly FeedbackService _feedback;
    private readonly IOptions<FeedbackOptions> _options;

    public FeedbackController(FeedbackService feedback, IOptions<FeedbackOptions> options)
    {
        _feedback = feedback;
        _options = options;
    }

    /// <summary>
    /// POST /api/feedback - cast, flip or revise ONE vote. Create-or-update per the one-vote rule, so a
    /// client that does not know whether it has voted before can always just POST.
    ///
    /// <para>ALWAYS <c>200</c> ON SUCCESS, never <c>201</c>, and that is a contract decision rather than
    /// an oversight: the caller cannot know in advance whether its vote creates or updates, and a status
    /// code the client has to branch on to read the same body twice would buy nothing. The body is the
    /// stored row either way, which is what the widget reconciles its optimistic state against.</para>
    ///
    /// <para><c>400 { "error": "..." }</c> for every refusal, matching this codebase's existing shape
    /// (<c>titleRequired</c>, <c>questionRequired</c>). Notably <c>targetNotFound</c> is a 400 and not a
    /// 404: the caller supplied the id in a body, not as a resource address, and a 404 on a create
    /// endpoint reads as "this URL is wrong", which is not the failure.</para>
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<FeedbackDto>> Vote(
        [FromBody] FeedbackVoteRequest? request, CancellationToken ct)
    {
        var result = await _feedback.VoteAsync(request, ResolveUserId(), ct);

        return result.Outcome switch
        {
            FeedbackOutcome.Ok => Ok(result.Item),
            _ => BadRequest(new { error = result.Error })
        };
    }

    /// <summary>
    /// DELETE /api/feedback/{id} - retract. <c>204</c>, or <c>404 { "error": "feedbackNotFound" }</c>.
    /// NOT gated by the triage flag: this is the voter's own action on their own row, not a triage
    /// operation, and a widget that could vote but not un-vote would be a trap.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Retract(Guid id, CancellationToken ct)
    {
        var result = await _feedback.RetractAsync(id, ct);
        if (result.Outcome == FeedbackOutcome.NotFound)
            return NotFound(new { error = FeedbackErrors.FeedbackNotFound });

        return NoContent();
    }

    /// <summary>
    /// GET /api/feedback/availability - does this deployment serve the triage surface? ALWAYS OPEN, and it
    /// has to be: a gated availability check could never answer the question it exists to answer.
    ///
    /// <para>An addition beyond d1, for the client's route guard. The alternative is a client that learns
    /// the flag by calling a gated endpoint and reading a bodiless 404 - indistinguishable from a transport
    /// failure, and an odd request to make just to decide whether to register a route. It reveals nothing
    /// d1 did not already treat as public: its own argument for 404-over-403 is that Swagger already
    /// exposes the whole route table with nothing in front of it. No feedback data travels here.</para>
    ///
    /// <para>The route cannot collide with <see cref="Detail"/>: that one is constrained to a
    /// <c>{id:guid}</c>, and <c>availability</c> is not one.</para>
    /// </summary>
    [HttpGet("availability")]
    public ActionResult<FeedbackAvailabilityDto> Availability()
        => Ok(new FeedbackAvailabilityDto(TriageEnabled));

    /// <summary>
    /// GET /api/feedback - the triage list (FLAG-GATED). Newest first; every filter optional, and an
    /// omitted filter means EVERYTHING rather than a default subset.
    ///
    /// <para><c>bookId</c> resolves through the evidence join rather than through a column on the feedback
    /// row, consistent with evidence never being copied - so it necessarily excludes rows whose target has
    /// been deleted or whose target type carries no book.</para>
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<FeedbackListDto>> List(
        [FromQuery] string? area,
        [FromQuery] string? status,
        [FromQuery] string? verdict,
        [FromQuery] Guid? bookId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = FeedbackService.DefaultPageSize,
        CancellationToken ct = default)
    {
        if (!TriageEnabled) return TriageHidden();

        return Ok(await _feedback.ListAsync(area, status, verdict, bookId, page, pageSize, ct));
    }

    /// <summary>
    /// GET /api/feedback/{id} - the triage DETAIL (FLAG-GATED): the row plus its evidence, joined live
    /// from the target, in one response.
    ///
    /// <para>A target that no longer resolves does NOT 404 this read. d1 chose to keep the feedback row
    /// when its conversation is deleted, so refusing to show it would defeat that decision; the evidence
    /// comes back with <c>available: false</c> and a reason instead. The only 404 here (flag on) is a
    /// FEEDBACK id that does not exist.</para>
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FeedbackDetailDto>> Detail(Guid id, CancellationToken ct)
    {
        if (!TriageEnabled) return TriageHidden();

        var detail = await _feedback.GetDetailAsync(id, ct);
        if (detail == null) return NotFound(new { error = FeedbackErrors.FeedbackNotFound });

        return Ok(detail);
    }

    /// <summary>
    /// PATCH /api/feedback/{id}/status - the triage transition (FLAG-GATED), and the ONLY write path for
    /// <c>Status</c> anywhere in this codebase, for the owner's buttons and for C3 alike.
    ///
    /// <para><c>400 { "error": "statusTransitionNotAllowed", "from": "...", "to": "..." }</c> names the
    /// refused move rather than only refusing it, so a triage UI can say which button was wrong.</para>
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<FeedbackDto>> ChangeStatus(
        Guid id, [FromBody] FeedbackStatusRequest? request, CancellationToken ct)
    {
        if (!TriageEnabled) return TriageHidden();

        var result = await _feedback.ChangeStatusAsync(id, request?.Status, ct);

        return result.Outcome switch
        {
            FeedbackOutcome.Ok => Ok(result.Item),
            FeedbackOutcome.NotFound => NotFound(new { error = FeedbackErrors.FeedbackNotFound }),
            _ => BadRequest(new { error = result.Error, from = result.From, to = result.To })
        };
    }

    private bool TriageEnabled => _options.Value.TriageEnabled;

    /// <summary>
    /// A BODILESS <c>404</c>, not a <c>403</c> and not a 404 with an error code. This app exposes its
    /// whole route table through Swagger with nothing in front of it, so a 403 would leak exactly as much
    /// as a 200 and buy nothing; an empty 404 is what an unregistered route already returns, so the
    /// flag-off case needs no special-casing on the client to look identical to "this build has no triage".
    /// </summary>
    private NotFoundResult TriageHidden() => NotFound();

    /// <summary>
    /// The UPPER half of the one-vote key, resolved from the request PRINCIPAL and never from the body -
    /// a client-supplied user id would be unauthenticated and therefore meaningless as a dedup key.
    ///
    /// <para>It is null on every deployment today (no <c>AddAuthentication</c> is registered, so the
    /// principal is unauthenticated) and the key falls through to the client's installation id. The claim
    /// read here is <see cref="ClaimTypes.NameIdentifier"/>, which is where the Pagewise-style JWT puts
    /// <c>ApplicationUser.Id</c> - the same value <c>FeedbackItem.UserId</c>'s column width is shaped
    /// for - so the day that login lands this method starts returning it with no other change.</para>
    /// </summary>
    private string? ResolveUserId()
        => User?.Identity?.IsAuthenticated == true
            ? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            : null;
}
