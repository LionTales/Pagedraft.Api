namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>
/// The user-selectable model tier (model-tier-fast-thinking plan, phase 3). Scope is PER BOOK
/// (<c>Book.AiTier</c>) - decided in the plan's "## p3 scope decision" section, because the tier's unit and
/// the cache-invalidation unit must coincide: a per-REQUEST tier makes <c>ActiveModel</c> /
/// <c>BuiltWithDifferentModel</c> uncomputable on the status GETs, which have no request to key on.
///
/// DELIBERATELY A SINGLE TOKEN, not a task-&gt;model map and not a provider/model pair. That is enforcement
/// layer E1: there is no value a book can hold that says "route BookReview at OpenRouter". Which tasks the
/// token may move is decided in exactly one place, <see cref="AiTierPolicy.TieredTasks"/>.
///
/// <see cref="Fast"/> is the DEFAULT and means "resolve exactly as this code resolved before the tier
/// existed" - a null/absent/unrecognised stored value parses to Fast
/// (<see cref="AiTierPolicy.Parse"/>), so the tier can never fail open into paid cloud routing.
/// </summary>
public enum AiTier
{
    /// <summary>Local, free, private, offline. The default for every book, and the fallback for any
    /// unrecognised stored value. Resolution is byte-identical to the pre-tier behaviour.</summary>
    Fast = 0,

    /// <summary>
    /// The measured-better cloud model for the ALLOWLISTED tasks only. Opt-in: choosing it means an
    /// unpublished manuscript leaves the machine (the privacy posture recorded in the plan's "## p2 decision"
    /// residual-risk section and in appsettings <c>_comment_TermRepair</c>).
    /// </summary>
    Thinking = 1
}
