using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// RECONCILES a freshly-built finding set against the rows already cached for (BookId, Language) — the pure
/// DECISION half of <c>BookReviewService.PersistPreservingStatusAsync</c>, extracted verbatim (be-c09 / P2-7: a
/// pure MOVE, no behavior change). The service keeps the EF work (load, apply, upsert, save); everything here is
/// static, side-effect-free, and independently testable.
///
/// Reconciliation asks exactly two questions, and they are the two members this class exists for:
///   • WHICH CACHED ROW IS THIS FRESH FINDING? — <see cref="MatchIncomingToExistingRows"/>, the three match tiers
///     (current dedup key → b3's legacy-V1 key → b4b's fuzzy re-wording match), run TIER-MAJOR (be-c04). A match
///     means the row's user Status is carried forward; a miss means a new open card.
///   • MAY THIS LEFTOVER ROW BE DELETED? — <see cref="IsVanishedOpenDeletable"/>, the be-c02 scoped-delete
///     predicate with b2's phantom-anchor rule and b3's book-wide rule (be-c03: measured against the REVIEWABLE
///     set).
/// Both answers are governed by a finding's ANCHOR SCOPE, so the tri-state that derives it
/// (<see cref="ChapterOrdersOf"/> — anchors / EMPTY / null-UNKNOWN) and the single order the fuzzy tier compares on
/// (<see cref="ComparisonOrderOf"/>) live here with them rather than a call away. That adjacency is load-bearing:
/// the two questions must agree on what "unknown scope" means (never delete, never fuzzy-match — be-c08 / P3-10),
/// and they only agree because they read the SAME derivation.
///
/// THE CONTRACT NOTHING HERE MAY BREAK: a row the user has ACTED on (acknowledged / dismissed / done) is never
/// DELETED and its Status is never reset.
/// </summary>
internal static class BookFindingReconciler
{
    /// <summary>One incoming finding's claim on a cached row, and how it was matched (be-c04).</summary>
    /// <param name="Row">The cached row this fresh finding regenerates; its user Status is carried forward.</param>
    /// <param name="ViaReword">TRUE when the claim came from the FUZZY tier (b4b's re-wording match) rather than
    /// from one of the two KEY tiers. Only the fuzzy tier is counted/logged, because it is the one that SUPPRESSES a
    /// row the hashes could not match — a silent suppressor is indistinguishable from a bug.</param>
    /// <param name="KeepPriorAnchors">TRUE when the fresh copy anchors NO chapter while the row it claims anchors a
    /// REAL one: the anchored side keeps the chapter link — and (be-c08 / P3-6) its own DEDUP KEY with it, since the
    /// primary order is an input to that key and the two must not disagree. A FUZZY-tier rule only: on a KEY match the
    /// primary order is an input to the key, so an anchored row and an anchor-less fresh finding cannot share one —
    /// which is precisely WHY the key must be kept, or the next build's tier 1 WOULD match and blank the anchors.</param>
    /// <param name="Score">be-f01 / P2-2: the fuzzy tier's similarity score (0 for a key match — there is nothing to
    /// score, the match is exact). Carried so the caller can put a NUMBER in the audit trail for a fold onto a
    /// NON-OPEN row, not just a count.</param>
    /// <param name="RequiredThreshold">be-f01 / P2-2: the bar the fuzzy match actually had to clear (0.45, or the
    /// stricter be-c07 0.60 for a user-acted row across an anchor mismatch); 0 for a key match.</param>
    internal readonly record struct PriorMatch(
        BookFinding Row, bool ViaReword, bool KeepPriorAnchors, double Score = 0d, double RequiredThreshold = 0d);

    /// <summary>
    /// Matches every fresh finding to the cached row it regenerates (so the row's user Status can be carried
    /// forward), returning one nullable claim per incoming finding, POSITIONALLY. Three tiers, run TIER-MAJOR:
    ///   1. the CURRENT dedup key, over EVERY incoming finding;
    ///   2. then b3's LEGACY-V1 key (<see cref="BookFinding.ComputeLegacyDedupKeyV1"/>), over the LEFTOVERS — the
    ///      caller upgrades a legacy-matched row's stored key in place, which is what makes the migration
    ///      self-healing;
    ///   3. then b4b's FUZZY re-wording match (<see cref="NearDuplicateCollapser.FindPersistedNearDuplicate"/>),
    ///      over what is still unmatched.
    /// A row is claimed BY ID at most once (<paramref name="matchedExistingIds"/>, stamped the moment a tier claims
    /// it), so one cached row backs at most one fresh finding and every tier sees the claims of the tiers above it.
    ///
    /// be-c04 — WHY TIER-MAJOR, AND WHY THIS IS A CORRECTNESS FIX RATHER THAN A TIDY-UP. The pre-fix loop was
    /// FINDING-major: each fresh finding ran key → key → fuzzy before the next was looked at. Because a row can only
    /// be claimed once, an EARLIER finding's FUZZY claim (a 0.45-similarity GUESS) could take the very row a LATER
    /// finding matched EXACTLY BY KEY. That later finding then found its row already claimed, fell through all three
    /// tiers, and was INSERTED AS A NEW OPEN ROW. When the hijacked row was DISMISSED, the exact re-emission of the
    /// dismissed finding came back as an OPEN CARD — precisely the harm the persisted tier exists to prevent (see
    /// <see cref="NearDuplicateCollapser.FindPersistedNearDuplicate"/>: "without it, a dismissed finding is
    /// RESURRECTED as an open card every time the model rephrases it, i.e. the user cannot make it go away").
    ///
    /// It was not reachable, but BY LUCK, NOT BY CONSTRUCTION — and the luck was other code's: an exact key match
    /// implies identical RAW prose, so the build-time collapser would USUALLY have merged the two incoming copies
    /// before the persist ever saw them. That invariant lives in another class, was written down nowhere, and does
    /// not actually hold: the persisted comparison scores REPAIRED prose (DynamicTermRepairService rewrites
    /// Rationale in place AFTER the dedup key is stamped, and the raw pre-repair prose is never persisted — hence
    /// the stored key and the stored Rationale legitimately DISAGREE on any repaired row), and it is itself an LLM,
    /// so sim(fresh, fresh) and sim(fresh, persisted row) are simply not the same number. Ordering the tiers
    /// GLOBALLY removes the dependence on that accident: a STRONGER tier can never lose a row to a WEAKER one,
    /// whatever order the findings arrive in.
    ///
    /// UNIQUE INDEX (BookId, Language, DedupKey) — strictly SAFER than before, and now by construction. Incoming
    /// findings carry DISTINCT current keys (UnionAndDedup deduped them), so tier 1 is CONTENTION-FREE: each cached
    /// row is looked up by at most one incoming key. Therefore, once tier 1 has run, NO UNCLAIMED ROW CARRIES A KEY
    /// THAT ANY INCOMING FINDING WILL WRITE — a claimed row is overwritten with its single claimant's key, and every
    /// other row keeps a key no claimant carries. Under the old finding-major order that separation held only
    /// because a fuzzy/legacy claimant also rewrote its row's key; here it needs no such argument.
    /// </summary>
    /// <param name="legacyMatches">be-f01 / P2-3: OUT — how many incoming findings were claimed at TIER 2 (the b3
    /// legacy-key shim) this build. The retirement criterion for the shim: once this is 0 across enough rebuilds, no
    /// row still carries the pre-b3 key.</param>
    internal static PriorMatch?[] MatchIncomingToExistingRows(
        IReadOnlyList<BookFinding> incoming,
        IReadOnlyDictionary<string, BookFinding> existingByKey,
        IReadOnlyList<NearDuplicateCollapser.PersistedCandidate> persistedCandidates,
        IReadOnlySet<int> realChapterOrders,
        HashSet<Guid> matchedExistingIds,
        out int legacyMatches,
        ILogger? logger)
    {
        var matches = new PriorMatch?[incoming.Count];
        legacyMatches = 0;

        // TIER 1 — the CURRENT dedup key, over EVERY incoming finding. Runs to completion before any weaker tier
        // gets to claim anything, so an exact re-emission always recovers its own row (and its user Status).
        for (var i = 0; i < incoming.Count; i++)
        {
            var prior = MatchExisting(incoming[i].DedupKey, existingByKey, matchedExistingIds);
            if (prior is null)
                continue;
            matches[i] = new PriorMatch(prior, ViaReword: false, KeepPriorAnchors: false);
            matchedExistingIds.Add(prior.Id);
        }

        // TIER 2 — b3's LEGACY-V1 key, over the leftovers. A row still carrying the pre-b3 derivation is claimed
        // here and its key UPGRADED by the caller. Below tier 1 on purpose: a row already carrying the CURRENT key
        // must never be passed over in favour of a different, stale-keyed row.
        for (var i = 0; i < incoming.Count; i++)
        {
            if (matches[i] is not null)
                continue;
            var prior = MatchExisting(incoming[i].LegacyDedupKeyV1, existingByKey, matchedExistingIds);
            if (prior is null)
                continue;
            matches[i] = new PriorMatch(prior, ViaReword: false, KeepPriorAnchors: false);
            matchedExistingIds.Add(prior.Id);
            legacyMatches++;
        }

        // TIER 3 — b4b's FUZZY re-wording match, over what NEITHER key tier could place. Last on purpose: it is the
        // only tier that GUESSES (0.45 similarity + MayFold), so it may only ever claim a row no key tier wanted.
        for (var i = 0; i < incoming.Count; i++)
        {
            if (matches[i] is not null)
                continue;
            var fresh = incoming[i];

            // be-c08 (P3-10) — UNKNOWN SCOPE IS NEVER FUZZY-MATCHED, ON EITHER SIDE. The caller already refuses to
            // OFFER a persisted row whose anchor payload does not parse ("unknown scope must not be fuzzy-matched,
            // exactly as it must not be deleted"), but the fresh side used to degrade an unparseable payload to the
            // EMPTY set — which ComparisonOrderOf maps to null, and null is the MOST PERMISSIVE value there is: a
            // MayFold WILDCARD that may claim a row anchored to ANY chapter. The lenient default sat on the exact
            // side where the guard's whole purpose is caution, and it pointed the wrong way. Same rule, both sides:
            // a scope we cannot read is a scope we do not act on, so the finding is simply inserted as its own row
            // (fail-open — a visible card, never a claim on somebody else's).
            var freshOrders = ChapterOrdersOf(fresh);
            if (freshOrders is null)
                continue;
            var freshOrder = ComparisonOrderOf(freshOrders, realChapterOrders);
            var nearDuplicate = NearDuplicateCollapser.FindPersistedNearDuplicate(
                fresh, freshOrder, persistedCandidates, matchedExistingIds, out var score, out var required, logger);
            if (nearDuplicate is not { } match)
                continue;
            matches[i] = new PriorMatch(
                match.Row,
                ViaReword: true,
                KeepPriorAnchors: freshOrder is null && match.ComparisonOrder is not null,
                Score: score,
                RequiredThreshold: required);
            matchedExistingIds.Add(match.Row.Id);
        }

        return matches;
    }

    /// <summary>
    /// One KEY tier's lookup: the cached row carrying <paramref name="key"/>, or null when there is none, when the
    /// key is absent (a fresh finding whose LegacyDedupKeyV1 was not derived), or when an earlier incoming finding
    /// has ALREADY CLAIMED that row — one cached row backs at most one fresh finding.
    /// </summary>
    private static BookFinding? MatchExisting(
        string? key,
        IReadOnlyDictionary<string, BookFinding> existingByKey,
        IReadOnlySet<Guid> matchedExistingIds)
    {
        if (key is not { Length: > 0 })
            return null;
        return existingByKey.TryGetValue(key, out var row) && !matchedExistingIds.Contains(row.Id)
            ? row
            : null;
    }

    /// <summary>
    /// The be-c02 scoped-delete predicate, with the b2 IMMORTAL-ORPHAN fix. Decides whether a VANISHED (not in
    /// this build's incoming set) still-OPEN finding may be deleted, given the chapter orders it anchors
    /// (<see cref="ChapterOrdersOf"/>), the orders REVIEWED this build, and the book's REAL chapter orders.
    ///
    /// Two anchor cases that used to be INDISTINGUISHABLE:
    ///   • an anchor naming a REAL chapter of this book that was NOT reviewed this build (its window failed or was
    ///     uncovered) → PRESERVE. This is the be-c02 rule and it must not regress: a MULTI-chapter continuity
    ///     finding whose FIRST anchor was re-reviewed but whose LATER anchor's window FAILED must survive, because
    ///     its absence from `incoming` is a truncation/failure artifact, not the model retracting it.
    ///   • an anchor naming an order this book DOES NOT HAVE (a phantom the model invented — the live 2cf6fcf2
    ///     reproducer: a 1-chapter book whose findings claimed orders 1 and 16) → INVALID. It carries NO
    ///     preservation weight. reviewedChapterOrders is a SUBSET of the real orders, so a phantom order can never
    ///     be "reviewed"; requiring it to be made the row un-deletable on EVERY rebuild, forever — the accumulation
    ///     users saw. Such an anchor is simply SKIPPED here.
    ///
    /// Consequences worth being explicit about:
    ///   • ALL-ANCHORS-INVALID (the orphan): every anchor is skipped, no REAL anchor is left to protect it, so the
    ///     row IS deletable. Note this does NOT go through the no-anchor path: <see cref="ChapterOrdersOf"/> maps a
    ///     row with NO anchors at all to an EMPTY order set, whereas a row WITH phantom anchors yields their
    ///     phantom orders ({16}) — the two are different things and get different rules (below).
    ///   • MIXED (one real-but-unreviewed anchor + one phantom): PRESERVED. Ignoring an invalid anchor must never
    ///     REDUCE the protection a VALID anchor earns — the finding genuinely concerns a chapter we did not
    ///     re-review this build, and the phantom adds no information either way.
    ///   • NO ANCHORS AT ALL (b3 — an EXPLICIT rule, deliberately NOT a fall-through of the invalid-anchor filter
    ///     below): a finding with no chapter anchors is BOOK-WIDE. It is real, valid criticism (after b1 a finding
    ///     whose every anchor was unresolvable KEEPS its rationale and becomes exactly this), it just names no
    ///     chapter. Applying the be-c02 question honestly — "were all the chapters this finding concerns actually
    ///     re-reviewed?" — its scope is the WHOLE BOOK, so it is deletable ONLY on a build that reviewed every
    ///     chapter it COULD review (<paramref name="reviewableChapterOrders"/>); on a partial build it is PRESERVED,
    ///     because its absence from `incoming` may be a failed-window artifact. Its deletability no longer depends on
    ///     chapter 0 (the old sentinel: the empty anchor set used to be mapped to {0}, so a book-wide finding was
    ///     deletable exactly when the FIRST chapter happened to be reviewed — arbitrary, and both too eager and too
    ///     lax by turns).
    ///     be-c03 (P1-2) — WHY REVIEWABLE, NOT REAL. This test used to read <c>reviewed ⊇ real</c>, and on a book
    ///     with ONE genuinely empty chapter (a title-only "Part I" divider, a DOCX artefact) it could NEVER be true:
    ///     BookContextAssembler skips such a chapter, so it is never any window's primary and can never enter
    ///     <paramref name="reviewedChapterOrders"/> — not on a failed build, not on a partial build, not on a
    ///     perfect one. Every vanished-open BOOK-WIDE finding was therefore preserved on EVERY rebuild: immortal,
    ///     unbounded accumulation, which is precisely the orphan class b2 exists to kill (and b7 made it common by
    ///     converting mis-anchored findings into no-anchor ones as a matter of course). An UNREVIEWABLE chapter is a
    ///     PERMANENT gap, not a transient one, so it must not be allowed to veto the retraction forever. Measuring
    ///     against the REVIEWABLE set asks the only question the build can actually answer: "did this build review
    ///     everything it could?" — which is still FALSE whenever a window fails or comes back empty (be-c01), so the
    ///     partial-build protection is fully intact.
    ///     Contrast with the orphan above, which is deletable on ANY build: an all-phantom row names chapters this
    ///     book does not have, so it is KNOWN-bogus scope, not book-wide scope.
    ///   • UNKNOWN scope (<paramref name="anchorOrders"/> null — the persisted anchors payload did not parse):
    ///     never delete. A parse blip must not wipe review content (pre-b3 this was the {0} default, which deleted
    ///     the row whenever chapter 0 was reviewed; refusing outright is strictly safer).
    ///   • A book with NO chapters at all: nothing is deletable (pre-b2 behavior preserved). With no real orders
    ///     there is no scope to reason in, and a review-content wipe must never be triggered by a degenerate
    ///     chapter map. (Unreachable in practice — a chapter-less book cannot produce findings to persist.)
    ///   • A book with real chapters but NOTHING REVIEWABLE (every chapter is empty, so no window was ever built):
    ///     the book-wide rule is fail-closed and refuses, rather than deleting VACUOUSLY (every element of an empty
    ///     set is trivially reviewed). Same reasoning as the no-chapters guard. (Also unreachable in practice — such
    ///     a book has no context to review and produces no findings to persist.)
    ///   • A finding anchored ONLY to a real-but-UNREVIEWABLE (empty) chapter is PRESERVED, not deleted: the
    ///     anchored branch below is unchanged and still asks for REVIEWED, which an empty chapter never is. That is
    ///     the fail-safe direction (this subsystem loses a duplicate before it loses a finding), the row can only
    ///     exist at all if it was written BEFORE b7's visibility gate (which makes an anchor onto a never-shown
    ///     chapter structurally unproducible), and repairing such legacy rows is b6's charter, not the delete pass's.
    ///     Deliberate residual, stated rather than silently fixed.
    /// </summary>
    /// <param name="anchorOrders">The orders the row anchors (<see cref="ChapterOrdersOf"/>): EMPTY = a no-anchor,
    /// book-wide finding; NULL = the anchors payload was unreadable, so the scope is UNKNOWN.</param>
    /// <param name="reviewedChapterOrders">What this build actually REVIEWED: the primaries of the windows that
    /// PRODUCED findings (be-c01 — a failed OR an empty/suspected-truncated window is excluded). Transient: a
    /// chapter missing here may well be reviewed on the next build.</param>
    /// <param name="reviewableChapterOrders">be-c03: what this build COULD review — the orders it put in front of
    /// the model (window primaries / the whole-book context). A genuinely empty chapter is real but NOT reviewable,
    /// and never will be, on any build. Used ONLY by the no-anchor (book-wide) rule.</param>
    /// <param name="realChapterOrders">b2: every chapter order the book actually has. Used ONLY to classify an
    /// ANCHOR as real (preservation weight) or phantom (none). NOT interchangeable with
    /// <paramref name="reviewableChapterOrders"/> — see the two bullets above.</param>
    internal static bool IsVanishedOpenDeletable(
        IReadOnlyCollection<int>? anchorOrders,
        IReadOnlySet<int> reviewedChapterOrders,
        IReadOnlySet<int> reviewableChapterOrders,
        IReadOnlySet<int> realChapterOrders)
    {
        if (realChapterOrders.Count == 0)
            return false; // degenerate: no real chapters → no scope → never delete.

        if (anchorOrders is null)
            return false; // UNKNOWN scope (unparseable anchors) → never delete.

        if (anchorOrders.Count == 0)
        {
            // b3 NO-ANCHOR RULE (be-c03: measured against the REVIEWABLE set): a book-wide finding's scope is the
            // whole book, so only a build that re-reviewed everything it COULD review may retract it. On a fully
            // successful build reviewed == reviewable, so it behaves as the regenerated-noise delete intends — even
            // on a book whose empty chapters mean reviewable ⊊ real, where the old `reviewed ⊇ real` test could never
            // be satisfied and the row was immortal. On a partial build (a failed window, or an empty one under
            // be-c01) reviewed ⊊ reviewable and it is PRESERVED, exactly like an anchored finding whose chapter was
            // not re-reviewed. This must stay an EXPLICIT short-circuit ABOVE the loop: let the empty set fall
            // through instead and the loop returns true VACUOUSLY, making every book-wide finding unconditionally
            // deletable by side effect rather than by decision.
            if (reviewableChapterOrders.Count == 0)
                return false; // nothing was reviewable → the superset test would be VACUOUSLY true → fail closed.
            return reviewedChapterOrders.IsSupersetOf(reviewableChapterOrders);
        }

        foreach (var order in anchorOrders)
        {
            if (!realChapterOrders.Contains(order))
                continue; // INVALID/phantom anchor → no preservation weight (b2).
            if (!reviewedChapterOrders.Contains(order))
                return false; // a REAL anchored chapter was not re-reviewed this build → PRESERVE (be-c02).
        }

        return true;
    }

    /// <summary>The order set an EXISTING finding with NO chapter anchors maps to: EMPTY. b3 — it used to be {0},
    /// which COLLIDED with a genuine first-chapter anchor (orders are 0-based, so chapter 0 is real in every book).
    /// The empty set is the explicit "this finding anchors no chapter" representation, and
    /// <see cref="IsVanishedOpenDeletable"/> gives it its own stated rule rather than letting it fall through the
    /// per-order filter. Shared instance to avoid re-allocating.</summary>
    private static readonly IReadOnlyCollection<int> NoAnchorOrders = Array.Empty<int>();

    /// <summary>
    /// b4b: the chapter a finding is COMPARED ON by the near-duplicate tier — its FIRST anchor order that is a REAL
    /// chapter of this book, or <c>null</c> when it anchors none. Mirrors the primary-order input of the dedup key
    /// (<c>BookReviewService.ProjectToEntity</c>: the first RESOLVED anchor, else null), so the two agree by
    /// construction.
    ///
    /// "BY CONSTRUCTION" IS A CLAIM, AND be-c08 HAD TO REPAIR IT TWICE BEFORE IT WAS TRUE. The key is stamped ONCE,
    /// from the anchors a finding is written with; every later pass that touches EITHER of the two must keep them in
    /// step, and two did not:
    ///   • P3-8 — b8's merge map unions anchors onto its survivor by APPEND, which cannot move anchors[0] … unless
    ///     the survivor had NO anchors, where the append CREATES it. Such a group is now REJECTED
    ///     (<c>SynthesisMergeMap.TryStageMerge</c>), so a merge can never move a survivor's primary order.
    ///   • P3-6 — the persisted fuzzy tier lets an ANCHORED row keep its anchors when an anchor-less copy claims it.
    ///     It used to overwrite that row's KEY with the anchor-less one anyway, so key and anchors disagreed. The key
    ///     now travels with the anchors (see <see cref="PriorMatch"/>).
    /// The one DELIBERATE divergence, unchanged and stated: this method skips PHANTOM orders (below), so a legacy row
    /// whose first anchor names no chapter of the book compares on its first REAL anchor — which the key, hashed on
    /// the raw order, does not know about. That is b2's judgement (a phantom names nothing and carries no weight),
    /// not a drift.
    ///
    /// Two rows are only ever merged when this value is EQUAL or one of them is null (see
    /// <see cref="NearDuplicateCollapser.MayFold"/>), so what this returns decides the precision fence. be-c04 calls
    /// this ONLY for the tier-3 (fuzzy) leftovers — any incoming finding tier 1 (current key) or tier 2 (legacy key)
    /// already claimed never reaches this comparison — and be-c07's stricter user-acted similarity threshold changes
    /// how HIGH a fuzzy score must climb to fold, never which order two rows are compared on; neither changes what
    /// this method returns. It skips PHANTOM anchor orders — an order the book does not have — and a row whose
    /// anchors are ALL phantom therefore compares as book-wide (null). That is the same judgement b2 makes for the
    /// scoped delete: an anchor naming no chapter of this book carries no weight, and in particular it is NOT
    /// evidence that the row is about some OTHER real chapter, so it must not block the row from being re-matched to
    /// the finding it is a rewording of. Fresh findings never carry phantoms (b1 resolves or drops every anchor), so
    /// this only bites on legacy rows. Returns <c>null</c> for the empty set, NEVER 0 — chapter 0 is a real chapter
    /// in every 0-based book (b3).
    /// </summary>
    internal static int? ComparisonOrderOf(IReadOnlyCollection<int> anchorOrders, IReadOnlySet<int> realChapterOrders)
    {
        foreach (var order in anchorOrders)
        {
            if (realChapterOrders.Contains(order))
                return order;
        }
        return null;
    }

    /// <summary>
    /// Derives the FULL set of chapter orders an EXISTING persisted <see cref="BookFinding"/> anchors, from its
    /// <see cref="BookFinding.ChapterAnchorsJson"/> (a serialized <c>List&lt;FindingChapterAnchor&gt;</c>). Three
    /// distinct outcomes, matching the three things the payload can actually say — b3's tri-state contract (be-f03 /
    /// P2-10: this doc was found detached and sitting above <see cref="ComparisonOrderOf"/> instead, leaving THIS
    /// method with no summary at all; moved back here, where the tri-state it describes actually lives):
    ///   • one or more anchors → EVERY anchor's <c>Order</c> (deduped);
    ///   • an empty/absent anchor list ("[]", "null", or an empty column) → <see cref="NoAnchorOrders"/> (EMPTY):
    ///     a NO-ANCHOR, book-wide finding — the same explicit representation <c>BookReviewService.UnionAndDedup</c>
    ///     keys on for INCOMING findings (a null primary order), never the number 0;
    ///   • a payload that does NOT parse → NULL: the scope is UNKNOWN, and <see cref="IsVanishedOpenDeletable"/>
    ///     refuses to delete on it (a review-content wipe must never be triggered by a parse blip).
    /// Feeds <see cref="IsVanishedOpenDeletable"/>, which requires that all of a finding's REAL anchored chapters
    /// were reviewed this build before a vanished-open row is deleted (so a MULTI-chapter continuity finding is not
    /// wiped when only its first anchor was re-reviewed) while ignoring anchor orders that are no chapter of the
    /// book. Note the orders returned here are the RAW persisted ones: a pre-b1 row can carry a phantom order the
    /// book never had. The JSON is not SQL-queryable, so this runs in memory on the already-loaded rows. Deserialized
    /// with <see cref="BookReviewService.DeserializeOpts"/> (case-insensitive CamelCase), matching the CamelCase
    /// writer in <c>BookReviewService.ProjectToEntity</c>.
    /// </summary>
    internal static IReadOnlyCollection<int>? ChapterOrdersOf(BookFinding finding)
    {
        if (string.IsNullOrWhiteSpace(finding.ChapterAnchorsJson))
            return NoAnchorOrders; // nothing was ever written → the finding anchors no chapter
        try
        {
            var anchors = JsonSerializer.Deserialize<List<FindingChapterAnchor>>(
                finding.ChapterAnchorsJson, BookReviewService.DeserializeOpts);
            if (anchors is { Count: > 0 })
                return anchors.Select(a => a.Order).Distinct().ToList();
            return NoAnchorOrders; // "[]" / "null" → a genuine no-anchor (book-wide) finding
        }
        catch (JsonException)
        {
            return null; // UNKNOWN scope → IsVanishedOpenDeletable refuses to delete
        }
    }
}
