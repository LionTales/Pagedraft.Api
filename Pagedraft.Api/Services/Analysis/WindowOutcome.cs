using System.Collections.Generic;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// What ONE window's model call in the whole-book review MAP actually produced. The build turns this into two
/// decisions: whether the window's PRIMARY chapters join <c>reviewedPrimaryOrders</c> (which is what licenses the
/// DESTRUCTIVE delete-vanished-open pass in <see cref="BookReviewService.PersistPreservingStatusAsync"/>), and what
/// the coverage line reports to the user.
///
/// THE CONCLUSION THIS TYPE ENCODES (be-c01, 2026-07-13) — an EMPTY findings array is NOT distinguishable from a
/// silent truncation, so it is NOT a review:
///   • The output schema (<see cref="BookReviewResult"/>) has exactly ONE representation for "I reviewed these
///     chapters and they are clean" and for "my output was cut short / the model gave up": a findings list with no
///     items. <c>{}</c>, <c>{"findings": []}</c> and a truncated-then-repaired object all deserialize to
///     <c>Findings.Count == 0</c> (the <c>= new()</c> initialiser means an ABSENT key is an EMPTY list, not null).
///     There is no "clean" verdict, no per-chapter acknowledgement, no token the model can emit to say "I looked".
///   • So the model CANNOT tell us which of the two happened, and we must not invent a distinction it cannot make.
///     Under this subsystem's governing rule (losing a real finding is strictly worse than showing a duplicate) the
///     only safe reading is the pessimistic one: EMPTY == the window did not produce a usable review of its
///     chapters. It is a SUSPECTED TRUNCATION, not a clean bill of health.
///   • This is not hypothetical: a silently truncated / short-circuited structured response is the characteristic
///     over-budget failure of the 8 GB local card, and treating an all-empty combined result as a SUCCESS already
///     shipped once in this very engine (the whole-book combined path now treats empty as a total failure, see
///     <c>BuildBookReviewAsync</c>'s <c>totalFailure</c>). The windowed path kept the old, optimistic reading:
///     a truncated window was marked REVIEWED, and b3's book-wide deletability rule
///     (<c>reviewed ⊇ real</c>) then DELETED still-open findings for chapters that build never actually reviewed.
///
/// COST OF BEING WRONG, both ways. If a window genuinely was clean and we call it a suspected truncation, the only
/// harm is that a vanished-open finding on those chapters survives one more build (and the coverage line says
/// "reviewed 5/6"); the next build that produces anything for that window deletes it. If a window truncated and we
/// call it clean, we DELETE a still-open finding the user paid for and it never comes back. The asymmetry decides it.
/// </summary>
internal enum WindowOutcome
{
    /// <summary>The call errored, returned nothing, or its output could not be parsed
    /// (<see cref="BookReviewService"/>'s per-window call returns null). Reported to the user as a FAILED window.</summary>
    Failed,

    /// <summary>The call parsed cleanly but carried ZERO findings. Indistinguishable from a silent truncation (see
    /// the type doc), so its chapters are NOT counted as reviewed. Logged at WARNING; surfaced on the coverage line
    /// as a chapter the build did not review, NOT as a hard failed window (we did not OBSERVE a failure).</summary>
    EmptySuspectedTruncation,

    /// <summary>The call parsed and produced at least one finding: the ONLY outcome that proves the model actually
    /// reviewed the window's chapters, and therefore the only one whose primaries join the reviewed set.</summary>
    Reviewed
}

/// <summary>Classifies a window's parsed result. Split out of the already-oversized <c>BookReviewService</c>
/// (CLAUDE.md file-size rule) so the rule is stated, and testable, in one place.</summary>
internal static class WindowOutcomes
{
    /// <param name="windowFindings">The window call's parsed findings: NULL = the call failed (error/unparseable),
    /// an EMPTY list = it parsed but produced nothing, a non-empty list = a real review.</param>
    internal static WindowOutcome Classify(IReadOnlyCollection<BookFindingItem>? windowFindings) =>
        windowFindings is null ? WindowOutcome.Failed
        : windowFindings.Count == 0 ? WindowOutcome.EmptySuspectedTruncation
        : WindowOutcome.Reviewed;

    /// <summary>The single question the DESTRUCTIVE path asks: may this window's primary chapters be added to
    /// <c>reviewedPrimaryOrders</c> (and so license the delete of a vanished-open finding anchored there)? ONLY a
    /// window that produced findings may. Both failure shapes answer NO.
    ///
    /// final-r01 — THIS IS WIRED, AND IT MUST STAY WIRED. It shipped with NO production caller: the window loop
    /// switched on the enum directly and added the primaries from its catch-all <c>default:</c> arm, so this method
    /// (and its green unit test) stated the destructive-path contract while nothing enforced it — a helper that reads
    /// as a guarantee and is not one, which is strictly worse than no helper. It also made the loop FAIL-OPEN: a
    /// WindowOutcome member added later would fall into <c>default:</c> and LICENSE A DELETE. The loop now asks THIS
    /// predicate for the licence, and its <c>default:</c> arm is fail-closed. Anything not explicitly
    /// <see cref="WindowOutcome.Reviewed"/> answers NO — so a new member is not a review until someone deliberately
    /// makes it one, HERE.</summary>
    internal static bool CountsAsReviewed(this WindowOutcome outcome) => outcome == WindowOutcome.Reviewed;
}
