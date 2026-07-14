using System;
using System.Collections.Generic;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// be-c02 (P1-1) — THE DIGEST ANCHOR GATE. Decides which of an accumulated finding's chapter anchors a REDUCE
/// pass's digest is allowed to PRINT, which is the same thing as deciding what goes into that pass's anchor
/// ALLOWLIST and its shown-set.
///
/// THE BUG THIS CLOSES. The two reduce passes (synthesis, continuity-final) do not read chapter text: the only
/// chapter numbers in front of them are the ones their digest prints. b7 therefore derives BOTH the prompt's
/// allowlist AND the resolver's shown-set from the digest's orders. But the digest was rendered from the RAW,
/// UNRESOLVED <see cref="BookFindingItem.ChapterAnchors"/> the windows emitted — and the whole reason b7 exists is
/// that a window's anchors are NOT trustworthy. So b7's gate was validating the model against its own hallucination:
///
///   • window 2 (shown chapters 11-16) anchors a finding to chapter 2 — REAL, never read. Its OWN copy of that
///     anchor is correctly dropped as UNSEEN. But the digest printed "2", so the synthesis prompt said "you may
///     anchor ONLY to these orders: … 2 …", the synthesis anchored to 2, and the resolver ACCEPTED it: 2 is real AND
///     2 is in the SYNTHESIS shown-set. The finding was persisted mis-anchored to a chapter no pass ever read — and
///     the correctly-anchored book-wide copy then folded INTO that wrong-chapter synthesis copy.
///   • a phantom order (99 in a 17-chapter book) landed in the allowlist while the resolver dropped it, so the
///     PROMPT and the PARSER disagreed — the exact incoherence b7's allowlist was written to remove.
///
/// THE RULE. An order may reach a reduce's digest / allowlist / shown-set only if it SURVIVES the very resolution
/// the persist step will later apply to the finding that carries it: it must name a REAL chapter of the book AND it
/// must be in the EMITTING finding's own <see cref="BookFindingItem.VisibleChapterOrders"/> (the shown-set of the
/// pass that wrote it). An order its own author never saw is a guess, and a guess must not be LAUNDERED into the
/// next pass's allowlist merely by appearing in a digest line. b7's stated property — "unseen-chapter anchors are
/// structurally unproducible" — is only true once this holds for the reduce passes too.
///
/// SINGLE-SOURCED, NOT REIMPLEMENTED. The decision is delegated to <see cref="ChapterAnchorResolver.TryPreviewAnchor"/>
/// — the same order→title→shown?→drop rule <see cref="ChapterAnchorResolver.TryResolveAnchor"/> runs at persist time,
/// with the bookkeeping switched off. So the digest prints exactly the anchors the finding WILL END UP WITH, and the
/// allowlist the model reads is, by construction, a set the parser will accept. (It also inherits the TITLE fallback
/// for free: a finding whose order is a mis-numbering of a chapter it DID see is printed at its CORRECTED order,
/// rather than being needlessly demoted to book-wide.)
///
/// WHAT A FILTERED-OUT ANCHOR PRINTS. The digest LINE is always kept; only the bad ORDER is dropped. A finding whose
/// anchors are ALL filtered out prints the no-anchor token ("-"), the established "book-wide finding" column value.
/// That is not a fudge, it is the TRUTH ahead of time: the resolver is about to drop those anchors, so the finding
/// really will be persisted as a book-wide finding, and the digest now shows the reduce the state the finding is
/// actually in. The alternative — dropping the line — would be strictly worse: a finding with no digest line gets no
/// merge id, cannot be reconciled, and its (perfectly valid) criticism vanishes from the reduce's view because of a
/// bad anchor. It also needs no new prompt legend: the allowlist rule already tells the model that a finding it
/// cannot place returns an empty "chapterAnchors" (a book-wide finding), which is precisely what "-" denotes.
///
/// FAIL-CLOSED. When every accumulated anchor is filtered out the shown-set is EMPTY, not null — the reduce really
/// was shown no chapter order — and the allowlist says so ("this pass shows you NO chapter order at all"). Empty and
/// null must never collapse (see <see cref="BookFindingItem.VisibleChapterOrders"/>).
/// </summary>
internal sealed class DigestAnchorGate
{
    /// <summary>A resolver used ONLY through its non-recording preview. Its drop counters are deliberately never
    /// read: the build's REAL resolver (the one in <c>UnionAndDedup</c>) owns the drop counts and the warnings, and
    /// a second instance counting the same anchors again would double-report them.</summary>
    private readonly ChapterAnchorResolver _resolver;

    public DigestAnchorGate(IReadOnlyDictionary<int, (Guid Id, string Title)> chaptersByOrder) =>
        _resolver = new ChapterAnchorResolver(chaptersByOrder);

    /// <summary>
    /// The chapter orders of <paramref name="finding"/> that a reduce digest may print: its anchors, resolved
    /// against the book's REAL chapters and gated by the finding's OWN emitting pass's shown-set, in the finding's
    /// anchor order, deduped (a title-corrected anchor can collide with another). EMPTY when the finding anchors
    /// nothing, or when every anchor it carries is a phantom or a chapter its author never saw — in which case the
    /// caller prints the no-anchor token.
    /// </summary>
    public IReadOnlyList<int> VisibleAnchorOrders(BookFindingItem finding)
    {
        if (finding.ChapterAnchors is not { Count: > 0 } anchors)
            return Array.Empty<int>();

        var kept = new List<int>(anchors.Count);
        foreach (var anchor in anchors)
        {
            // The finding's OWN shown-set — null means its producer declared no visibility (unconstrained), which
            // the resolver honours identically here and at persist time.
            if (!_resolver.TryPreviewAnchor(anchor, out var resolved, finding.VisibleChapterOrders))
                continue; // phantom order, or a REAL chapter this finding's author was never shown → not printable
            if (!kept.Contains(resolved.Order))
                kept.Add(resolved.Order);
        }

        return kept;
    }
}
