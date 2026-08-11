using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Collapses NEAR-duplicate book-review findings — the same criticism emitted two to four times by the model,
/// each time slightly RE-WORDED — down to one survivor per real finding. Runs at BUILD TIME only, inside
/// <c>BookReviewService.UnionAndDedup</c>, immediately AFTER the exact-key dedup.
///
/// WHY IT EXISTS. <see cref="BookFinding.ComputeDedupKey"/> is a SHA-256 over the exact rationale text (case,
/// trim and whitespace-run normalized, and nothing more). A hash cannot be tolerant to rewording by
/// construction, so one added word yields an unrelated key and BOTH rows persist. Observed on the real book
/// 2cf6fcf2 (2026-07-12): 20 stored findings, 20 distinct keys, ~10 real findings. Example pairs, same build,
/// same dimension:
///   character: "מורגן מציג קשת דמויות ברורה של..."       vs "...קשת דמויות ברורה ומרשימה של..."
///   theme:     "הטקסט מצליח להמחיש את המרחק שבין..."     vs "...את המרחק הניכר בין..."
///   tone:      FOUR variants of one excitement-to-painful-drama finding.
///
/// WHAT IT DOES NOT DO: it never changes the dedup key. The STORED key must stay stable across rebuilds or the
/// persist step cannot re-match a cached row and the user's Status (acknowledged / dismissed / done) is lost.
/// This pass only decides WHICH freshly built findings reach the persist step; each survivor keeps its own
/// exact-text key. So there is no migration, and no existing row's key moves because of this pass.
///
/// BUCKETING — (dimension, RESOLVED primary chapter order). Two findings only ever collapse when they are in
/// the SAME dimension AND anchored to the SAME chapter, because the same sentence about two different chapters
/// is two different findings. The order is the RESOLVED one (post-<see cref="ChapterAnchorResolver"/>), and a
/// NO-ANCHOR (book-wide) finding sits in its own bucket keyed by <c>null</c> — deliberately NOT the same bucket
/// as chapter 0. Chapter orders are 0-based, so chapter 0 is a real chapter in every book; conflating the two
/// is exactly the sentinel collision the dedup key was fixed for, and it must not be reintroduced here.
///
/// b4b — THE CROSS-BUCKET FOLD, and WHY IT IS ASYMMETRIC. Strict bucketing left half the duplicates alive. On the
/// WINDOWED path different windows re-emit ONE finding with DIFFERENT anchors, so the copies land in DIFFERENT
/// buckets and never meet. Measured on the live 17-chapter book A63A6E02 (2026-07-12, post-b1..b4 rebuild): SEVEN
/// surviving duplicate pairs, every one of them a NO-ANCHOR copy beside an ANCHORED copy of the same finding, all
/// scoring 0.750 to 1.000 on this metric. See <see cref="MayFold"/> for the rule that closes them and for the
/// fence that keeps two DIFFERENT real chapters apart.
///
/// b4c — THE CROSS-DIMENSION NEAR-IDENTITY FOLD, and why it needs a SECOND, MUCH HIGHER threshold. b4 and b4b both
/// compare only WITHIN a dimension, so the model filing ONE sentence under TWO dimensions produced two cards that
/// never met. Live on A63A6E02 (2026-07-13, post-b4b rebuild): "המעבר לדמות הסופר בפרק Ktiv יוצר שינוי חד בטון…"
/// under character/chapter 2 and the SAME sentence minus two words under continuity/no-anchor — they score 1.000
/// here. The obvious fix (drop the dimension from the bucket key) is WRONG and would destroy real findings: the
/// same book contains a GENUINELY DISTINCT continuity/plot pair — "המעבר בין עולם נווה-חול לבין המרחב של הנמל…"
/// vs "המעבר בין עולם נווה-חול לעולם הנמל בפרקים המאוחרים…" — that scores 0.818, i.e. far above
/// <see cref="DefaultThreshold"/>. So the cross-dimension pass runs at NEAR-IDENTITY only, at
/// <see cref="CrossDimensionThreshold"/> = 0.90, and it changes NO within-dimension outcome (it only ever compares
/// candidates whose dimensions DIFFER). See <see cref="FoldCrossDimensionNearIdentity"/>.
///
/// SIMILARITY — max(Jaccard, containment) over Hebrew-aware normalized CONTENT tokens (see
/// <see cref="ContentTokens"/>). Containment is load-bearing, not decoration: the commonest model rewording is
/// an inserted adjective, which makes one rationale a strict SUPERSET of the other. On the real gold set,
/// dropping containment and scoring on Jaccard alone shrinks the safe threshold window from (0.273, 0.600] to
/// (0.150, 0.313] — from a 0.33-wide margin to 0.16, with the operating point pushed down to ~0.23 where a
/// genuinely distinct pair is uncomfortably close.
///
/// TUNING (gold set = the 20 real rows deleted from book 2cf6fcf2 on 2026-07-12, hand-labelled into 10 real
/// findings). With <see cref="DefaultThreshold"/> = 0.45:
///   • recall: the closest TRUE near-dupe (the 4-variant tone cluster's most-reworded member) scores 0.600 —
///     0.15 of headroom above the threshold. All 10 clusters collapse; the 4 tone variants become 1.
///   • precision: the closest DISTINCT same-dimension pair (two different theme findings that both discuss fear
///     as a force) scores 0.273 — 0.177 BELOW the threshold. No distinct finding is merged away.
/// The threshold therefore sits near the middle of a (0.273, 0.600] safe window. That is a real but not
/// enormous margin, and it is measured on ONE book: treat a false merge (a real finding silently lost) as the
/// expensive failure and a miss (a visible duplicate — the status quo) as the cheap one, which is why the
/// operating point is biased toward the precision side and why the guards below all fail CLOSED (no collapse).
///
/// KNOWN LIMIT: a bag-of-words metric cannot see an antonym swap ("the pacing is too slow" vs "too fast"), so a
/// pair that differs in exactly one polarity word and is otherwise identical could in principle merge. The
/// <see cref="MinContentTokens"/> floor keeps that out of the short-rationale regime where it is most likely;
/// on longer prose the model's rewordings differ in far more than one token.
///
/// P3-2, STATED PLAINLY: NONE OF THIS TUNING COVERS ENGLISH. <see cref="Prefixes"/> and <see cref="Suffixes"/> are
/// Hebrew-only, so an English rationale gets NO stemming at all — "argues" and "argued" are two different content
/// tokens, and every threshold above (<see cref="DefaultThreshold"/>, <see cref="CrossDimensionThreshold"/>,
/// <see cref="UserActedAnchorMismatchThreshold"/>) was tuned exclusively on the Hebrew gold sets named above. The
/// review language is he | en (see <see cref="Stopwords"/>'s English half) and English BookReview prompts exist
/// and ship, so this pass DOES run on English rationales — it just runs UNMEASURED there. The failure direction is
/// the safe one: with no stemming, English rewordings ("argues" vs "argued", "the character's" vs "the character")
/// share FEWER tokens than their Hebrew equivalents would after stemming, so Similarity systematically UNDER-scores
/// English near-duplicates relative to Hebrew ones. That means a miss (a visible duplicate), never a false merge —
/// precision-safe by construction, not by measurement — but the RECALL this pass buys on English rationales is an
/// open question, not a verified number. Revisit with an English gold set before trusting a recall claim here.
///
/// FILE-SIZE WAIVER (CLAUDE.md's ~700-line soft ceiling). This file is over it on PROSE, not on code: ~380 lines of
/// code carrying ~450 of rationale. Every threshold here is an OPERATING POINT tuned against real rows, and each one
/// is only safe because of the specific distinct pair that bounds it; the three passes further share one metric, one
/// stemmer, one MinContentTokens floor and one MayFold fence. Splitting them into separate files would separate each
/// number from the evidence that pins it — which is exactly how the FALSE xmldoc on ComputeDedupKey ("minor
/// re-wording does NOT produce a new key") went unchallenged for months and let this whole bug class ship. Waived
/// deliberately: keep the numbers and their evidence in one place.
/// </summary>
internal static class NearDuplicateCollapser
{
    /// <summary>Similarity at or above which two rationales in the same bucket are the SAME finding. Tuned on
    /// the real gold set: safe window (0.273, 0.600]; see the type remarks for the two binding pairs.</summary>
    internal const double DefaultThreshold = 0.45;

    /// <summary>
    /// b4c. Similarity at or above which two rationales in DIFFERENT dimensions are the same finding, filed twice.
    /// It is a SEPARATE, far stricter cut-off from <see cref="DefaultThreshold"/> and it MUST stay that way.
    ///
    /// WHY NOT 0.45. Two dimensions are two different QUESTIONS asked of the same prose, so two findings can share
    /// most of their vocabulary and still be two real criticisms. The binding evidence is a REAL pair from book
    /// A63A6E02 that the model filed under CONTINUITY and under PLOT — both about the world shifting from Neve-Hol
    /// to the harbour, but one says the transition needs a better bridge and the other places it in the late
    /// chapters. It scores 0.818 on this metric. Re-using the 0.45 within-dimension threshold across dimensions
    /// would merge that pair and SILENTLY DELETE a finding the user paid for, which is the one failure this whole
    /// type is biased against.
    ///
    /// THE OPERATING POINT AND ITS MEASURED MARGINS (production metric, all 89 live BookFinding rows across the 6
    /// books that have any, 1045 cross-dimension pairs scored, 2026-07-13):
    ///   • RECALL: the duplicate this exists to kill (the character/continuity pair above) scores 1.000 — the
    ///     shorter copy's tokens are a strict SUBSET of the longer one's, which is what "the same sentence, filed
    ///     twice" looks like to this metric. Headroom above the threshold: +0.100.
    ///   • PRECISION: the 0.818 distinct pair sits 0.082 BELOW it. That is a MODEST margin — thinner than b4's
    ///     0.177 — and it is stated here rather than smoothed over. What makes it liveable is that 0.818 is a lone
    ///     outlier: of the 1045 cross-dimension pairs in the live corpus, exactly ONE scores at or above 0.45 (the
    ///     true duplicate, at 1.000) and the next-highest DISTINCT pair anywhere scores 0.400 — half the threshold.
    /// The safe window is therefore (0.818, 1.000] and 0.90 sits near its middle (midpoint 0.909).
    ///
    /// THE RECALL LIMIT THIS BUYS, STATED. At 0.90 the pass catches NEAR-IDENTITY only. A cross-dimension duplicate
    /// that is genuinely RE-WORDED (not a strict superset) will score by Jaccard, land somewhere around 0.6-0.85,
    /// and be MISSED. That is deliberate: it is the same trade b4 made, at the point where a distinct pair is known
    /// to reach 0.818. A miss leaves a visible duplicate (annoying, and the status quo); a false merge destroys a
    /// finding. Closing the reworded-across-dimensions class needs a metric that can tell two CLAIMS apart, not a
    /// lower number here.
    /// </summary>
    internal const double CrossDimensionThreshold = 0.90;

    /// <summary>
    /// be-c07 (P2-1). The bar a FRESH finding must clear to fuzzy-claim a PERSISTED row THE USER HAS ACTED ON
    /// (dismissed / done / acknowledged) ACROSS AN ANCHOR MISMATCH — i.e. when exactly one of the two names a
    /// chapter and the other is book-wide. Every other fuzzy claim keeps <see cref="DefaultThreshold"/>.
    ///
    /// WHY THIS ONE PATH NEEDS ITS OWN NUMBER: IT IS WHERE THE SUBSYSTEM'S FAIL-SAFE BIAS INVERTS. Everywhere else,
    /// a wrong merge still leaves the user A CARD — the survivor is visible, and the worst case is a duplicate or a
    /// mis-phrased row. But a claim on a user-acted row is a SUPPRESSION: the fresh finding is NOT inserted, the row
    /// keeps the status the user gave it, and if that status is `dismissed` the row is not even rendered. So a
    /// genuinely DISTINCT fresh finding that wrongly claims a dismissed row NEVER REACHES THE USER AT ALL. That is
    /// the one failure this whole type is biased against, and it is the ONLY place the code can produce it.
    ///
    /// AND THE ANCHOR MISMATCH IS WHERE THE FENCE IS MISSING. <see cref="MayFold"/> keeps a fresh finding away from a
    /// row anchored to a DIFFERENT REAL chapter — but a book-wide row has <c>ComparisonOrder == null</c>, which is a
    /// WILDCARD: it may fold against anything. So a book-wide dismissed row could be claimed by ANY same-dimension
    /// fresh finding scoring 0.45, with no anchor evidence in play at all. Since b7 the windowed engine turns a
    /// finding it cannot place into a book-wide one AS A MATTER OF COURSE, so such rows are common, they are never
    /// deleted, and they accumulate — an unbounded, permanent absorption surface.
    ///
    /// THE OPERATING POINT AND ITS MEASURED MARGINS (scored with the SHIPPED <see cref="Similarity"/> /
    /// <see cref="ContentTokens"/> over BOTH captured corpora — book 2cf6fcf2's 20 rows and book A63A6E02's 18 rows,
    /// 2026-07-13):
    ///   • PRECISION — the binding pair is REAL and it is the WORST THING IN THE CORPUS TO LOSE. A63A6E02's
    ///     SEVERITY-3 FACTUAL CONTRADICTION (continuity, "דניאל is not mentioned anywhere else in the book") scores
    ///     0.462 against a SEVERITY-1 piece of praise in the same dimension (the sleeplessness continuity note).
    ///     They are two genuinely different findings — of OPPOSITE polarity — that a bag-of-words cannot separate.
    ///     At 0.45, if either had been dismissed AND book-wide, the other would CLAIM it and vanish. 0.60 sits
    ///     0.138 above that pair. (The highest DISTINCT same-dimension pair in 2cf6fcf2 is far lower: 0.273.)
    ///   • RECALL — the b4b promise this must NOT regress ("a dismissed finding must not be resurrected by a
    ///     rephrase"). The real anchor-mismatched re-wordings are A63A6E02's SEVEN cross-bucket pairs, every one a
    ///     book-wide copy beside an anchored copy of the SAME finding: 0.750 / 0.857 / 0.889 / 0.889 / 0.889 /
    ///     0.917 / 1.000. The LOWEST is 0.750, which clears 0.60 by 0.150.
    /// The safe window is therefore (0.462, 0.750] and 0.60 sits essentially at its midpoint (0.606).
    ///
    /// WHAT IS DELIBERATELY *NOT* CHANGED, and why the ordinary path keeps 0.45:
    ///   • An OPEN row, in either anchor regime. Claiming an open row is not a suppression — the row is refreshed
    ///     and the user still sees exactly one card. Refusing the claim would just delete the row as vanished and
    ///     insert the fresh copy: one card either way. There is no asymmetry to protect against, so there is no
    ///     reason to pay recall for it.
    ///   • A user-acted row whose anchors AGREE with the fresh finding (the same real chapter, or BOTH book-wide).
    ///     That is b4's own bucket — one dimension, one chapter scope — and the anchor evidence AGREES rather than
    ///     being absent. Its measured margins are the ones 0.45 was tuned on and they still hold on both corpora:
    ///     the closest true re-wording of a user-acted row is A63A6E02's real ACKNOWLEDGED theme row beside its
    ///     reworded re-emission (both chapter 9) at 0.600, and the closest DISTINCT same-dimension SAME-chapter pair
    ///     is A63A6E02's real tone pair at 0.364. Raising this path would start resurrecting dismissed findings for
    ///     no measured precision gain.
    ///
    /// COST OF THE FENCE, STATED: a genuine re-wording that crosses the anchor mismatch and scores between 0.45 and
    /// 0.60 no longer claims its user-acted row, so the fresh copy is INSERTED BESIDE IT and the user sees a
    /// duplicate (and, for a dismissed row, a finding they had dismissed comes back once). That is the CHEAP failure
    /// this subsystem always chooses: annoying, visible, and the user can dismiss it again. The expensive failure —
    /// a real finding silently deleted from their view — is the one it now cannot make below 0.60. No pair in either
    /// corpus actually falls in that band, so the measured cost today is ZERO.
    /// </summary>
    internal const double UserActedAnchorMismatchThreshold = 0.60;

    /// <summary>
    /// DEGENERATE-COLLAPSE GUARD. A rationale with fewer than this many content tokens is never FUZZY-matched
    /// (in either direction); it can still be dropped by the exact-key dedup, which needs no guard because it
    /// demands identical text.
    ///
    /// Short rationales are spuriously similar: "הקצב איטי מדי" and "הקצב מהיר מדי" ("the pacing is too slow" /
    /// "too fast") share 2 of 3 content tokens — Jaccard 0.5, containment 0.67 — so ANY useful threshold would
    /// merge two findings that say OPPOSITE things. The floor makes the pass inert exactly where the metric is
    /// unreliable. It costs nothing on real data: the shortest rationale in the gold set has 8 content tokens.
    /// </summary>
    internal const int MinContentTokens = 5;

    /// <summary>
    /// One freshly built finding plus the RESOLVED primary chapter order that <c>ProjectToEntity</c> derived for
    /// it (the same value that fed its dedup key): the order of its first resolved anchor, or <c>null</c> when it
    /// anchors NO chapter. Passed explicitly rather than re-parsed out of <c>ChapterAnchorsJson</c> so the
    /// bucket key and the dedup key can never drift apart.
    /// </summary>
    internal readonly record struct Candidate(BookFinding Finding, int? PrimaryChapterOrder);

    /// <summary>
    /// Returns the surviving findings, in the order they were supplied. Non-throwing: on ANY fault the input set
    /// is returned UNCOLLAPSED and a warning is logged — degrading to "the user sees a duplicate" is always
    /// preferable to failing a whole book-review build, and the fault is surfaced rather than swallowed. The
    /// un-collapsed set really is the set that arrived, field for field (be-c08 / P3-5): the ONE field these passes
    /// write — the survivor's Severity — is STAGED in a local array and committed to the entities only after all
    /// three passes have finished. The pre-fix code bumped severities as it went, so a mid-pass throw returned a
    /// "pre-pass" set that had already been partially rewritten, exactly the false-invariant the comment asserted.
    ///
    /// THREE passes, and THE ORDER IS LOAD-BEARING:
    ///   1. WITHIN-BUCKET (b4, at <paramref name="threshold"/>): collapse re-wordings that share a
    ///      (dimension, resolved order) bucket.
    ///   2. CROSS-BUCKET FOLD (b4b, same threshold): fold each surviving NO-ANCHOR copy into the ANCHORED copy of
    ///      the same finding in the SAME dimension, when there is one (<see cref="MayFold"/>). Pass 1 first, so the
    ///      no-anchor bucket has already been reduced to its own survivors before any of them is offered to an
    ///      anchored one.
    ///   3. CROSS-DIMENSION NEAR-IDENTITY (b4c, at the much stricter <paramref name="crossDimensionThreshold"/>):
    ///      merge what is left only when two SURVIVORS in DIFFERENT dimensions are the same sentence filed twice.
    /// Passes 1 and 2 run FIRST so that pass 3 compares each dimension's chosen REPRESENTATIVE — the highest
    /// severity, most specific, anchored copy — instead of whichever re-worded variant a window happened to emit.
    /// Run in the other order, a book-wide variant in dimension A could absorb dimension B's copy before B's own
    /// anchored copy had a chance to claim it, and the outcome would depend on the model's phrasing luck.
    /// </summary>
    /// <param name="candidates">Findings that survived the exact-key dedup, each with its resolved primary order.</param>
    /// <param name="logger">Optional; logs one INFORMATION line per build, UNCONDITIONALLY — including when nothing
    /// was collapsed (P1-6 / be-f01: a guard that reports only its positive count is indistinguishable from one that
    /// never ran).</param>
    /// <param name="threshold">WITHIN-dimension similarity cut-off (passes 1-2); defaults to
    /// <see cref="DefaultThreshold"/> (tests probe the boundary).</param>
    /// <param name="crossDimensionThreshold">CROSS-dimension cut-off (pass 3); defaults to
    /// <see cref="CrossDimensionThreshold"/>. Deliberately a separate knob: see that constant for why it may never
    /// be lowered to the within-dimension one.</param>
    internal static List<BookFinding> Collapse(
        IReadOnlyList<Candidate> candidates,
        ILogger? logger = null,
        double threshold = DefaultThreshold,
        double crossDimensionThreshold = CrossDimensionThreshold)
    {
        if (candidates is null || candidates.Count == 0)
            return new List<BookFinding>();

        try
        {
            var tokens = new HashSet<string>[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
                tokens[i] = ContentTokens(candidates[i].Finding?.Rationale);

            // THE STAGED SEVERITIES (be-c08 / P3-5). Passes 2 and 3 lift a survivor's severity to the MAX of what it
            // absorbs; that write is the ONLY entity mutation in this whole method. It is staged here and COMMITTED
            // after the last pass, so the catch below can honestly return "the findings exactly as they arrived".
            //   • `baseSeverities` — the severities as they ARRIVED. Frozen. Read by pass 2's tie-break (P3-9).
            //   • `severities`     — the working set, accumulating each pass's MAX. Committed at the end.
            var baseSeverities = new int[candidates.Count];
            var severities = new int[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
                baseSeverities[i] = severities[i] = candidates[i].Finding?.Severity ?? 0;

            // Bucket by (dimension, RESOLVED order). int? in the key means the no-anchor bucket (null) and the
            // chapter-0 bucket (0) are distinct BY CONSTRUCTION — the default tuple comparer never equates them.
            var buckets = new Dictionary<(string Dimension, int? Order), List<int>>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var dim = BucketDimension(candidates[i].Finding?.Dimension);
                var key = (dim, candidates[i].PrimaryChapterOrder);
                if (!buckets.TryGetValue(key, out var list))
                    buckets[key] = list = new List<int>();
                list.Add(i);
            }

            var absorbed = new bool[candidates.Count];
            var collapsedByDimension = new Dictionary<string, int>(StringComparer.Ordinal);

            // ── PASS 1: WITHIN-BUCKET (b4) ───────────────────────────────────────────────────────────────
            foreach (var (key, members) in buckets)
            {
                if (members.Count < 2)
                    continue;

                // SURVIVOR RULE — a TOTAL order, so the outcome does not depend on the order the model happened to
                // emit the variants in (the windowed engine unions several passes; input order is not meaningful):
                //   1. HIGHEST severity  — the variants of one finding routinely disagree on severity (the gold set
                //      has a theme finding emitted once at severity 1 and once at severity 3). Keeping the lower one
                //      would silently DOWNGRADE a real problem, and severity is what the user triages on.
                //   2. then MOST content tokens — the most specific phrasing. The commonest rewording is an added
                //      qualifier, so the longer variant is a superset of the shorter and loses nothing.
                //   3. then the ordinal-smallest DedupKey — an arbitrary but STABLE final tie-break, so an identical
                //      build always produces the identical survivor.
                // (The pre-existing exact-key dedup is first-occurrence-wins; this pass deliberately does NOT
                //  inherit that rule, because first-occurrence is meaningless across unioned windows.)
                var ordered = members
                    .OrderByDescending(i => baseSeverities[i]) // pass 1 runs before any bump, so base == working here
                    .ThenByDescending(i => tokens[i].Count)
                    .ThenBy(i => candidates[i].Finding?.DedupKey ?? string.Empty, StringComparer.Ordinal)
                    .ToList();

                var survivors = new List<int>();
                foreach (var i in ordered)
                {
                    var mergedInto = -1;
                    if (tokens[i].Count >= MinContentTokens)
                    {
                        foreach (var s in survivors)
                        {
                            if (tokens[s].Count < MinContentTokens)
                                continue;
                            if (Similarity(tokens[i], tokens[s]) >= threshold)
                            {
                                mergedInto = s;
                                break;
                            }
                        }
                    }

                    if (mergedInto < 0)
                    {
                        survivors.Add(i);
                        continue;
                    }

                    // ABSORBED. The survivor row is kept WHOLE — no field is merged in from the variant. Merging
                    // prose (or grafting a variant's suggestedAction onto a different variant's rationale) would
                    // fabricate text the model never wrote; the survivor is already the highest-severity, most
                    // specific statement of the same finding.
                    absorbed[i] = true;
                    collapsedByDimension[key.Dimension] =
                        collapsedByDimension.TryGetValue(key.Dimension, out var n) ? n + 1 : 1;
                }
            }

            // ── PASS 2: CROSS-BUCKET FOLD (b4b) ──────────────────────────────────────────────────────────
            var foldedTotal = FoldNoAnchorIntoAnchored(
                candidates, tokens, absorbed, collapsedByDimension, threshold, baseSeverities, severities);

            // ── PASS 3: CROSS-DIMENSION NEAR-IDENTITY (b4c) ──────────────────────────────────────────────
            var crossDimensionTotal = FoldCrossDimensionNearIdentity(
                candidates, tokens, absorbed, collapsedByDimension, crossDimensionThreshold, severities);

            var kept = new List<BookFinding>(candidates.Count);
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!absorbed[i] && candidates[i].Finding is not null)
                    kept.Add(candidates[i].Finding);
            }

            var collapsedTotal = candidates.Count - kept.Count;
            var withinBucketTotal = collapsedTotal - foldedTotal - crossDimensionTotal;

            // COVERAGE — UNCONDITIONAL (P1-6 / be-f01). This is the pass that GENERATED the "a guard that reports
            // only its positive count is indistinguishable from a guard that never ran" lesson: b4c shipped with 136
            // green tests while its own fold fired ZERO times on real data, and only a live rebuild caught it because
            // this line did not exist for a zero count. So it fires every build, including when nothing collapsed —
            // "0 collapsed" is only meaningful evidence once you can see the gate ran at all.
            logger?.LogInformation(
                "Book review near-duplicate collapse: {CandidatesIn} candidate(s) in, {SurvivorsOut} survivor(s) out " +
                "(collapsed {Collapsed}: {WithinBucket} within-dimension/chapter [b4], {Folded} book-wide-into-anchored " +
                "cross-bucket fold(s) [b4b], {CrossDimension} cross-dimension near-identity fold(s) [b4c]; " +
                "within-dimension threshold {Threshold}, cross-dimension threshold {CrossThreshold}). " +
                "Per dimension (the absorbed copy's): {PerDimension}.",
                candidates.Count, kept.Count, collapsedTotal, withinBucketTotal, foldedTotal, crossDimensionTotal,
                threshold.ToString("0.00", CultureInfo.InvariantCulture),
                crossDimensionThreshold.ToString("0.00", CultureInfo.InvariantCulture),
                collapsedByDimension.Count == 0
                    ? "none"
                    : string.Join(", ", collapsedByDimension.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => $"{kv.Key}={kv.Value}")));

            // ── COMMIT the staged severities (be-c08 / P3-5). THE FIRST AND ONLY WRITE TO AN ENTITY IN THIS METHOD,
            //    and deliberately the LAST statement in the try: every pass, every projection and even the coverage
            //    log above it can fault WITHOUT having touched a single finding, so the catch's promise ("the set
            //    that arrived, un-collapsed") is now literally true rather than approximately true. Only SURVIVORS
            //    are written — an absorbed copy is discarded, so lifting its severity on the way out would be a
            //    mutation nobody can observe and everybody has to reason about.
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!absorbed[i] && candidates[i].Finding is { } finding)
                    finding.Severity = severities[i];
            }

            return kept;
        }
        catch (Exception ex)
        {
            // FAIL-SAFE, but NOT silent (see memory: a swallowed fault ships failures invisibly). Returning the
            // un-collapsed set restores the pre-2026-07-12 behavior for this build — duplicates are visible and
            // annoying, a failed build is not recoverable by the user. be-c08 (P3-5): and it now returns the
            // findings THEMSELVES unchanged, not merely all of them — see the commit block above.
            logger?.LogWarning(ex,
                "Book review near-duplicate collapse faulted; persisting the {Count} un-collapsed finding(s) (fail-safe).",
                candidates.Count);
            return candidates.Where(c => c.Finding is not null).Select(c => c.Finding).ToList();
        }
    }

    // ── b4b: the CROSS-BUCKET fold, and the incoming-vs-PERSISTED tier ───────────────────────────────

    /// <summary>
    /// THE ANCHOR-COMPATIBILITY RULE (b4b). Whether two copies of what the metric says is one finding may be
    /// merged, given the chapter each is anchored to (<c>null</c> = anchored to NO chapter, i.e. book-wide).
    ///
    ///   • SAME order (including both <c>null</c>) → YES. This is b4's bucket: one finding, one chapter.
    ///   • one ANCHORED, the other NOT → YES, and this is the b4b addition. A no-anchor copy is the same finding
    ///     that merely FAILED to get an anchor: the windowed engine reviews the book in overlapping windows and a
    ///     window that sees the material without being able to place it emits the criticism with no chapter (or
    ///     with an anchor b1 then had to drop). Folding it into the anchored copy loses NOTHING — the anchored
    ///     copy says the same thing AND says where.
    ///   • two copies anchored to DIFFERENT chapters → NEVER. This is the precision fence and it is the whole
    ///     reason the rule is asymmetric rather than "merge anything similar in a dimension". "The pacing drags"
    ///     about chapter 3 and about chapter 9 may be TWO genuine findings; merging them silently DELETES one the
    ///     user paid for, which is strictly worse than leaving a visible duplicate. A miss is cheap, a false merge
    ///     is not, so the fence holds even at similarity 1.000 (an identical sentence about two chapters is two
    ///     findings, not one).
    /// </summary>
    internal static bool MayFold(int? a, int? b) => a is null || b is null || a.Value == b.Value;

    /// <summary>
    /// PASS 2 of <see cref="Collapse"/>: folds every surviving NO-ANCHOR candidate into the ANCHORED candidate of
    /// the same dimension that it near-duplicates, if there is one. Returns how many were folded.
    ///
    /// SURVIVOR — the ANCHORED copy ALWAYS survives, and this deliberately OVERRIDES b4's severity-first survivor
    /// rule. A no-anchor copy that happened to be emitted at a higher severity must not win, because winning would
    /// cost the user the chapter LINK: an anchored finding is navigable and a book-wide one is not, and the b1/b2
    /// work exists precisely to make anchors trustworthy. Anchoredness is not a tie-break here, it is a constraint.
    ///
    /// SEVERITY — the survivor takes the MAX severity of the folded pair. It is the one field merged in, for the
    /// same reason b4 sorts survivors by severity first: the model routinely re-emits one finding at two different
    /// severities (the b4 gold set has a theme finding emitted at both 1 and 3), and since the anchored copy is
    /// forced to win here regardless of its severity, taking its severity verbatim would let an arbitrary anchor
    /// coin-flip silently DOWNGRADE a major finding to a minor one. Severity is a scalar the user triages on, not
    /// prose, so lifting it fabricates nothing — no rationale, suggestedAction or evidence is ever merged across
    /// copies (that would graft one copy's text onto another's, which b4 refuses to do). Max is commutative, so
    /// the outcome does not depend on the order the windows happened to emit the copies in. The severity is NOT a
    /// dedup-key input, so nothing about the key moves. It is written to
    /// <paramref name="severities"/> (staged), never to the entity — see <see cref="Collapse"/>.
    ///
    /// ORDER-INDEPENDENCE, AND HOW IT WAS QUIETLY FALSE (be-c08 / P3-9). The claim is that this pass's outcome does
    /// not depend on the order the no-anchor copies are visited in: anchored candidates are never absorbed here, so
    /// the target set does not shrink as we go. TRUE of the target SET — but the pass also LIFTS a target's severity,
    /// and <see cref="IsBetterTarget"/> READ that severity as its tie-break. So when a book-wide copy near-duplicated
    /// two anchored copies EQUALLY well, which one it folded into could depend on whether an EARLIER fold had already
    /// bumped one of them: a genuine order dependence, hiding behind an invariant stated as a fact. (It was still
    /// deterministic — the copies are visited in DedupKey order — so it could never flap between builds; it was the
    /// stated reason that was wrong, not the output.) FIXED IN THE CODE, not in the comment: the tie-break now reads
    /// <paramref name="baseSeverities"/>, the severities as they ARRIVED, which no fold can move. Nothing this pass
    /// writes is read back by anything this pass decides, so the outcome is now genuinely independent of visit order.
    /// </summary>
    /// <param name="baseSeverities">be-c08 / P3-9: the severities as they arrived, frozen. The TIE-BREAK reads these,
    /// so an earlier fold cannot change which target a later, equally-similar copy picks.</param>
    /// <param name="severities">be-c08 / P3-5: the STAGED working severities. The MAX is accumulated here and
    /// committed to the entities by <see cref="Collapse"/> only after every pass has finished.</param>
    private static int FoldNoAnchorIntoAnchored(
        IReadOnlyList<Candidate> candidates,
        HashSet<string>[] tokens,
        bool[] absorbed,
        Dictionary<string, int> collapsedByDimension,
        double threshold,
        int[] baseSeverities,
        int[] severities)
    {
        // Survivors of pass 1, split per dimension into the anchored and the book-wide ones.
        var byDimension = new Dictionary<string, (List<int> Anchored, List<int> NoAnchor)>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (absorbed[i] || candidates[i].Finding is null)
                continue;
            var dim = BucketDimension(candidates[i].Finding!.Dimension);
            if (!byDimension.TryGetValue(dim, out var lists))
                byDimension[dim] = lists = (new List<int>(), new List<int>());
            if (candidates[i].PrimaryChapterOrder.HasValue)
                lists.Anchored.Add(i);
            else
                lists.NoAnchor.Add(i);
        }

        var folded = 0;
        foreach (var (dim, lists) in byDimension)
        {
            if (lists.Anchored.Count == 0 || lists.NoAnchor.Count == 0)
                continue;

            // Anchored candidates are NEVER absorbed by this pass, so the set of possible targets does not shrink as
            // we go; and (be-c08 / P3-9) the tie-break below reads the FROZEN base severities rather than the ones
            // this pass lifts, so no earlier fold can change a later one's choice either. Both halves are needed for
            // the outcome to be independent of the order the no-anchor copies are visited in. Visiting them by
            // DedupKey keeps the log deterministic regardless.
            foreach (var n in lists.NoAnchor.OrderBy(i => candidates[i].Finding!.DedupKey, StringComparer.Ordinal))
            {
                if (tokens[n].Count < MinContentTokens)
                    continue; // the degenerate-collapse guard applies across buckets exactly as within one.

                var target = -1;
                var bestSim = 0d;
                foreach (var a in lists.Anchored)
                {
                    if (tokens[a].Count < MinContentTokens)
                        continue;
                    var sim = Similarity(tokens[n], tokens[a]);
                    if (sim < threshold)
                        continue;

                    // A book-wide copy can near-duplicate the SAME finding anchored in two different chapters (it
                    // names neither). Fold it into the single best target — highest similarity, then the b4
                    // survivor ordering (severity, specificity, key) as a stable tie-break. The other anchored
                    // copies are untouched: MayFold forbids merging them with each other, and this pass never
                    // deletes an anchored row.
                    if (target < 0 || sim > bestSim
                        || (sim == bestSim && IsBetterTarget(candidates, tokens, baseSeverities, a, target)))
                    {
                        target = a;
                        bestSim = sim;
                    }
                }

                if (target < 0)
                    continue;

                absorbed[n] = true;
                severities[target] = Math.Max(severities[target], severities[n]); // STAGED, not written to the entity
                collapsedByDimension[dim] = collapsedByDimension.TryGetValue(dim, out var c) ? c + 1 : 1;
                folded++;
            }
        }

        return folded;
    }

    /// <summary>b4's survivor ordering (severity, then specificity, then the ordinal-smallest key) reused as a
    /// STABLE tie-break when a book-wide copy near-duplicates two anchored copies equally well. The severity it reads
    /// is the FROZEN arrival severity (be-c08 / P3-9), never the one <see cref="FoldNoAnchorIntoAnchored"/> lifts as
    /// it goes — a tie-break that reads a value the pass itself is mutating is a hidden order dependence.</summary>
    private static bool IsBetterTarget(
        IReadOnlyList<Candidate> candidates, HashSet<string>[] tokens, int[] baseSeverities, int a, int b)
    {
        var sa = baseSeverities[a];
        var sb = baseSeverities[b];
        if (sa != sb) return sa > sb;
        if (tokens[a].Count != tokens[b].Count) return tokens[a].Count > tokens[b].Count;
        return string.CompareOrdinal(candidates[a].Finding?.DedupKey ?? string.Empty,
                                     candidates[b].Finding?.DedupKey ?? string.Empty) < 0;
    }

    // ── b4c: the CROSS-DIMENSION near-identity fold ───────────────────────────────────────────────────

    /// <summary>
    /// PASS 3 of <see cref="Collapse"/>. Merges two SURVIVORS of passes 1-2 that sit in DIFFERENT dimensions but
    /// are the same sentence filed twice. Returns how many were absorbed.
    ///
    /// THE CLASS IT CLOSES (live, book A63A6E02, 2026-07-13, and the literal "the same item appears twice" the user
    /// reported): the model emitted one criticism under CHARACTER anchored to chapter 2, and the SAME sentence minus
    /// two words under CONTINUITY with no anchor. b4 buckets by dimension and b4b folds only within one, so neither
    /// ever compared them; both cards reached the user.
    ///
    /// NEAR-IDENTITY ONLY — see <see cref="CrossDimensionThreshold"/>. Two dimensions are two QUESTIONS asked of the
    /// same prose, so a shared vocabulary across dimensions is WEAK evidence of a shared finding: the same book holds
    /// a genuinely DISTINCT continuity/plot pair at 0.818 on this metric. Anything short of near-identity therefore
    /// stays split, and this pass is inert on every other cross-dimension pair in the live corpus.
    ///
    /// EVERY EXISTING GUARD STILL HOLDS, and two of them are what make this safe:
    ///   • <see cref="MayFold"/> — two copies anchored to DIFFERENT REAL chapters NEVER merge, even at similarity
    ///     1.000, and crossing a dimension does not buy an exemption. Identical prose about chapter 3 and chapter 9
    ///     is two findings whatever the model filed them under.
    ///   • <see cref="MinContentTokens"/> — short rationales are never fuzzy-matched, in either direction.
    ///   • The pass ONLY ever compares candidates whose dimensions DIFFER, so it CANNOT change a within-dimension
    ///     outcome. b4's 0.45 threshold and b4b's fold keep their exact meaning, by construction rather than by
    ///     inspection.
    ///
    /// SURVIVOR — a TOTAL order, so the outcome never depends on the order the windows emitted the copies in:
    ///   1. ANCHORED beats NO-ANCHOR. b4b's constraint, and it decides the live pair: the character/chapter-2 copy
    ///      survives and the book-wide continuity copy is absorbed. A navigable finding beats a marginally better
    ///      sentence, and the anchored copy is the one whose window actually LOCATED the criticism in the book.
    ///   2. then HIGHEST SEVERITY, 3. then MOST content tokens, 4. then the ordinal-smallest DedupKey — b4's rule,
    ///      unchanged, reused for the case where both copies are equally (un)anchored.
    ///
    /// THE DIMENSION LABEL THE USER SEES is the SURVIVOR'S OWN, verbatim. Nothing is relabelled and no merged label
    /// is invented: the survivor row is kept WHOLE (b4's rule — no prose, action or evidence is ever grafted across
    /// copies), and Dimension is a DEDUP-KEY input, so writing the absorbed copy's dimension onto the survivor would
    /// move its key — which b3 owns and which must not move (it is how a rebuild re-matches the row and keeps the
    /// user's Status). Concretely, the live pair renders once, under דמויות, with its chapter-2 link. Dropping the
    /// רציפות label costs the user nothing the surviving sentence does not already say; dropping the chapter link
    /// would have cost them navigation.
    ///
    /// SEVERITY — the survivor takes the MAX of the merged pair, for exactly b4b's reason: the survivor is chosen by
    /// anchoredness FIRST, so taking its severity verbatim would let an arbitrary anchor coin-flip silently DOWNGRADE
    /// a major finding. Severity is a scalar the user triages on, not prose, so lifting it fabricates nothing, and it
    /// is not a key input. Written to <paramref name="severities"/> (staged), never to the entity — see
    /// <see cref="Collapse"/>.
    ///
    /// The SURVIVOR ORDER is computed ONCE, up front, from the severities as they stand AFTER pass 2 (this pass runs
    /// on pass 2's chosen representatives, which is the whole point of the pass order). It is therefore not affected
    /// by this pass's OWN severity lifts: the sort happens before the first of them, and nothing below re-reads a
    /// severity to make a decision (the target contest is decided on similarity alone, with the pre-sorted survivor
    /// list breaking ties by position). be-c08 checked this explicitly — the P3-9 order dependence it fixed in pass 2
    /// does not exist here.
    /// </summary>
    /// <param name="severities">be-c08 / P3-5: the STAGED working severities (already carrying pass 2's lifts).</param>
    private static int FoldCrossDimensionNearIdentity(
        IReadOnlyList<Candidate> candidates,
        HashSet<string>[] tokens,
        bool[] absorbed,
        Dictionary<string, int> collapsedByDimension,
        double threshold,
        int[] severities)
    {
        // Everything still standing after passes 1-2, walked in the SURVIVOR order above. Because the walk follows a
        // total order derived from the DATA (never from the input sequence), the first candidate of any merged pair
        // is always the preferred one, so "the survivor" needs no second decision at merge time — and reversing the
        // input cannot change who wins.
        var order = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!absorbed[i] && candidates[i].Finding is not null)
                order.Add(i);
        }
        if (order.Count < 2)
            return 0;

        order.Sort((x, y) => CompareSurvivorPreference(candidates, tokens, severities, x, y));

        var folded = 0;
        var survivors = new List<int>(order.Count);
        foreach (var i in order)
        {
            var dimension = BucketDimension(candidates[i].Finding!.Dimension);

            var target = -1;
            var bestSim = 0d;
            if (tokens[i].Count >= MinContentTokens)
            {
                foreach (var s in survivors)
                {
                    if (tokens[s].Count < MinContentTokens)
                        continue;

                    // SAME dimension → not this pass's business. b4 (bucket) and b4b (fold) already decided it at
                    // the 0.45 threshold; re-judging it here at 0.90 could only ever UN-collapse, never collapse.
                    if (string.Equals(BucketDimension(candidates[s].Finding!.Dimension), dimension, StringComparison.Ordinal))
                        continue;

                    if (!MayFold(candidates[i].PrimaryChapterOrder, candidates[s].PrimaryChapterOrder))
                        continue; // the precision fence, unchanged across dimensions.

                    var sim = Similarity(tokens[i], tokens[s]);
                    if (sim < threshold)
                        continue;

                    // Highest similarity wins. `survivors` is already in preference order, so a TIE resolves to the
                    // MORE PREFERRED (earlier) survivor — the strict `>` is what keeps it there.
                    if (target < 0 || sim > bestSim)
                    {
                        target = s;
                        bestSim = sim;
                    }
                }
            }

            if (target < 0)
            {
                survivors.Add(i);
                continue;
            }

            absorbed[i] = true;
            severities[target] = Math.Max(severities[target], severities[i]); // STAGED, not written to the entity
            collapsedByDimension[dimension] =
                collapsedByDimension.TryGetValue(dimension, out var n) ? n + 1 : 1;
            folded++;
        }

        return folded;
    }

    /// <summary>
    /// The b4c survivor order as an <see cref="IComparer{T}"/>-shaped comparison (lower = preferred): ANCHORED
    /// first (b4b's constraint), then b4's rule — severity, specificity, ordinal-smallest key. Total on real data:
    /// the final tie-break is the dedup key, which is unique per (dimension, order, rationale), and two candidates
    /// sharing all three would already have been dropped by the exact-key dedup upstream.
    /// </summary>
    private static int CompareSurvivorPreference(
        IReadOnlyList<Candidate> candidates, HashSet<string>[] tokens, int[] severities, int x, int y)
    {
        var anchoredX = candidates[x].PrimaryChapterOrder.HasValue;
        var anchoredY = candidates[y].PrimaryChapterOrder.HasValue;
        if (anchoredX != anchoredY)
            return anchoredX ? -1 : 1;

        // The STAGED severities (be-c08 / P3-5) — i.e. pass 2's lifts, exactly as this comparison saw them when it
        // read Finding.Severity directly. Staging moved WHERE the value lives, not WHAT it is.
        var severityX = severities[x];
        var severityY = severities[y];
        if (severityX != severityY)
            return severityY.CompareTo(severityX);

        if (tokens[x].Count != tokens[y].Count)
            return tokens[y].Count.CompareTo(tokens[x].Count);

        return string.CompareOrdinal(candidates[x].Finding?.DedupKey ?? string.Empty,
                                     candidates[y].Finding?.DedupKey ?? string.Empty);
    }

    /// <summary>
    /// An ALREADY-PERSISTED finding offered to <see cref="FindPersistedNearDuplicate"/> as a possible original of
    /// a freshly built one. Build with <see cref="Prepare"/> so the row is tokenized ONCE per build.
    /// </summary>
    /// <param name="ComparisonOrder">The chapter this row is compared on: its first anchor order that is a REAL
    /// chapter of the book, or <c>null</c> when it anchors none. NULL is a book-wide WILDCARD under
    /// <see cref="MayFold"/>, which is also the right reading for a legacy row whose only anchors are PHANTOM
    /// orders the book does not have: such an anchor names no chapter, so (exactly as b2 rules for the scoped
    /// delete) it carries no weight and cannot be evidence that the row is about some OTHER real chapter.</param>
    internal readonly record struct PersistedCandidate(
        BookFinding Row, int? ComparisonOrder, HashSet<string> Tokens, string Dimension);

    /// <summary>Tokenizes a persisted row once for the whole build.</summary>
    internal static PersistedCandidate Prepare(BookFinding row, int? comparisonOrder) =>
        new(row, comparisonOrder, ContentTokens(row.Rationale), BucketDimension(row.Dimension));

    /// <summary>
    /// THE INCOMING-vs-PERSISTED TIER (b4b). Finds the persisted row that a FRESHLY BUILT finding is a RE-WORDING
    /// of, so the persist step can treat it as a REGENERATION of that row instead of inserting a second card.
    ///
    /// WHY THE BUILD-TIME COLLAPSE IS NOT ENOUGH. <see cref="Collapse"/> only ever sees ONE build's incoming set.
    /// A row the user ACTED on (acknowledged / dismissed / done) is deliberately never deleted, so it is still
    /// there on the next build; when the model re-emits that same finding with a word changed, its exact key no
    /// longer matches, it is inserted as a NEW open row, and the user sees the SAME criticism twice. That is the
    /// literal complaint on book 2cf6fcf2: an acknowledged theme card and a fresh theme card, side by side.
    ///
    /// THE SEMANTICS (identical for OPEN / ACKNOWLEDGED / DISMISSED / DONE, on purpose). A hit here means "this
    /// fresh finding IS that row, reworded" — precisely what an exact-key hit means — so the caller handles it the
    /// SAME way it already handles an exact-key hit: the ROW survives with its Status untouched, its content is
    /// refreshed from the fresh finding, and the fresh finding is NOT inserted. Nothing is deleted that was not
    /// already, and no new drop rule is introduced. Per status:
    ///   • OPEN → refreshed in place. Note the alternative (drop the fresh copy, keep the row) would be a BUG: the
    ///     row would then look VANISHED to the delete pass, be deleted as regenerated noise, and the finding would
    ///     disappear ENTIRELY. Claiming the row is what keeps it alive.
    ///   • ACKNOWLEDGED / DONE → the decision is PRESERVED and the card stays acknowledged. The user acted on this
    ///     criticism; a re-worded restatement of it is not a new criticism, and re-raising it as a fresh open card
    ///     would silently undo their triage.
    ///   • DISMISSED → stays DISMISSED, and this is the case that matters most: without it, a dismissed finding is
    ///     RESURRECTED as an open card every time the model rephrases it, i.e. the user cannot make it go away.
    ///     The exact-key path has always suppressed a verbatim re-emission of a dismissed finding; this makes the
    ///     suppression survive re-wording, which is the same promise.
    /// A user-acted row is therefore never dropped, never re-opened, and never duplicated — PROVIDED some tier
    /// (the current key, the legacy key, or this fuzzy match) actually FINDS it.
    ///
    /// THE RESIDUAL, NAMED (P3-11). None of the three tiers is guaranteed to find it. <c>BookFinding.
    /// ComputeLegacyDedupKeyV1</c> (tier 2) hashes the model's RAW, unresolved anchor order — whichever anchor the
    /// model happened to list FIRST, not the RESOLVED primary order this fuzzy tier compares on — so if the model's
    /// raw ordering merely shifts between builds (the same finding, but a different anchor comes first this time),
    /// tier 2's key changes and misses the row. If the fresh finding's RESOLVED order has ALSO moved enough to trip
    /// MayFold's anchor-mismatch fence (a different REAL chapter, not the null/anchored wildcard), this tier refuses
    /// it too. When both miss, the fresh copy is inserted as a NEW OPEN CARD beside the untouched, still user-acted
    /// row: a visible duplicate, not a silent loss — nothing is deleted and no Status is reset, so it fails OPEN,
    /// exactly this type's bias — but a duplicate nonetheless, so the sentence above must not be read as ruling
    /// duplication out entirely. Needs the model's raw anchor list to reorder AND the resolved order to disagree in
    /// the same build; not observed on either captured corpus, but real.
    ///
    /// SCOPE — same dimension, and <see cref="MayFold"/> on the two chapter anchors, so a fresh finding can NEVER
    /// claim a row anchored to a DIFFERENT REAL chapter. Same <see cref="MinContentTokens"/> guard.
    /// Rows whose persisted anchor payload does not parse are not offered here at all (the caller drops them):
    /// unknown scope must not be fuzzy-matched, exactly as it must not be deleted.
    ///
    /// THE BAR IS NOT ONE NUMBER (be-c07 / P2-1). Read the paragraph above again and notice what it does NOT say: a
    /// BOOK-WIDE row has <c>ComparisonOrder == null</c>, and under MayFold that is a WILDCARD, not a fence. So on the
    /// one path where a wrong claim SUPPRESSES a distinct finding rather than duplicating it — a row the user has
    /// ACTED on, matched across an anchor MISMATCH — the claim must clear
    /// <see cref="UserActedAnchorMismatchThreshold"/> (0.60) instead of <see cref="DefaultThreshold"/> (0.45). See
    /// <see cref="RequiredPersistedThreshold"/> for the rule and the constant for the corpus it was tuned on. The
    /// ORDINARY path — an open row, or a user-acted row whose anchors agree — is untouched at 0.45.
    ///
    /// b4c — THIS TIER IS DELIBERATELY STILL DIMENSION-SCOPED, and the residual is stated rather than hidden. b4c's
    /// cross-dimension pass runs at BUILD time, so a finding the model files under two dimensions in the SAME build
    /// never reaches the persist step twice, which is the class the user reported. What is NOT covered: if the model
    /// files a finding under dimension A on one build, the user DISMISSES it, and a later build re-files the same
    /// criticism under dimension B, the fresh B copy matches nothing here and is inserted as a new OPEN card — the
    /// dismissal does not follow the finding across a re-labelling. Matching across dimensions here would fix that,
    /// but it would also mean a fresh finding CLAIMS a user-acted row and rewrites its Dimension (a key input, and
    /// the label the user filed it under), so the row's identity would follow the model's whim between builds. That
    /// is a larger semantic step than the reported bug needs, and the fail-closed bias says take the visible
    /// duplicate over the surprising rewrite. Revisit only with evidence that the model actually re-labels.
    ///
    /// FAIL-CLOSED, like the rest of this type: on any fault it returns NULL, which means "no match", which means
    /// the fresh finding is inserted as its own row — the pre-b4b behavior. A visible duplicate, never a lost row.
    /// </summary>
    /// <param name="freshOrder">The fresh finding's resolved primary chapter order (<c>null</c> = book-wide).</param>
    /// <param name="claimedRowIds">Rows already claimed by an earlier incoming finding this build; one persisted
    /// row backs at most one fresh finding, so two fresh findings can never be folded onto the same row.</param>
    /// <param name="threshold">The ordinary bar (an OPEN row, or a user-acted row whose anchors AGREE).</param>
    /// <param name="userActedAnchorMismatchThreshold">be-c07: the STRICTER bar for the one destructive path — a
    /// user-acted row claimed across an anchor mismatch. See <see cref="UserActedAnchorMismatchThreshold"/>.</param>
    /// <param name="similarity">be-f01 / P2-2: the winning candidate's similarity score (0 when no match), so the
    /// caller can put a NUMBER — not just a count — into the audit trail for a fold onto a NON-OPEN row.</param>
    /// <param name="requiredThreshold">be-f01 / P2-2: the bar the winning candidate actually had to clear (0.45 or
    /// the stricter be-c07 0.60), so the audit trail states the threshold that applied, not just the default.</param>
    internal static PersistedCandidate? FindPersistedNearDuplicate(
        BookFinding fresh,
        int? freshOrder,
        IReadOnlyList<PersistedCandidate> persisted,
        IReadOnlySet<Guid> claimedRowIds,
        out double similarity,
        out double requiredThreshold,
        ILogger? logger = null,
        double threshold = DefaultThreshold,
        double userActedAnchorMismatchThreshold = UserActedAnchorMismatchThreshold)
    {
        similarity = 0d;
        requiredThreshold = threshold;

        if (fresh is null || persisted is null || persisted.Count == 0)
            return null;

        try
        {
            var freshTokens = ContentTokens(fresh.Rationale);
            if (freshTokens.Count < MinContentTokens)
                return null;

            var freshDimension = BucketDimension(fresh.Dimension);

            PersistedCandidate? best = null;
            var bestSim = 0d;
            var bestRequired = threshold;
            foreach (var candidate in persisted)
            {
                if (candidate.Row is null || claimedRowIds.Contains(candidate.Row.Id))
                    continue;
                if (!string.Equals(candidate.Dimension, freshDimension, StringComparison.Ordinal))
                    continue;
                if (!MayFold(freshOrder, candidate.ComparisonOrder))
                    continue; // the precision fence: a DIFFERENT real chapter is a DIFFERENT finding.
                if (candidate.Tokens.Count < MinContentTokens)
                    continue;

                // be-c07: the bar is PER ROW, because the COST of being wrong is per row. A claim on a row the user
                // ACTED on, across an anchor MISMATCH, is the only fuzzy decision in the subsystem that can make a
                // distinct finding INVISIBLE — so it alone must clear the stricter bar. Everything else keeps 0.45.
                // Evaluated BEFORE the best-match comparison, so an ineligible user-acted row does not merely lose
                // the contest — it never enters it, and the claim can still go to an OPEN row where a mistake is
                // cheap and visible.
                var required = RequiredPersistedThreshold(
                    freshOrder, candidate.ComparisonOrder, candidate.Row.Status,
                    threshold, userActedAnchorMismatchThreshold);

                var sim = Similarity(freshTokens, candidate.Tokens);
                if (sim < required)
                    continue;

                // Highest similarity wins (the truest answer to "which row IS this?"), then the most specific row,
                // then the ordinal-smallest key — a stable tie-break so an identical build claims identical rows.
                if (best is null
                    || sim > bestSim
                    || (sim == bestSim
                        && (candidate.Tokens.Count > best.Value.Tokens.Count
                            || (candidate.Tokens.Count == best.Value.Tokens.Count
                                && string.CompareOrdinal(candidate.Row.DedupKey, best.Value.Row.DedupKey) < 0))))
                {
                    best = candidate;
                    bestSim = sim;
                    bestRequired = required;
                }
            }

            similarity = bestSim;
            requiredThreshold = bestRequired;
            return best;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Book review near-duplicate match against persisted findings faulted; the fresh finding is persisted " +
                "as a NEW row (fail-safe: a visible duplicate, never a lost or silently re-opened row).");
            return null;
        }
    }

    /// <summary>
    /// be-c07. The similarity bar ONE persisted row demands of a fresh finding that wants to claim it. Two bars, and
    /// which one applies is decided by the COST OF BEING WRONG about THIS row, not by how similar the text is:
    ///
    ///   • <see cref="UserActedAnchorMismatchThreshold"/> (0.60) when the row carries a USER STATUS
    ///     (dismissed / done / acknowledged) AND the anchors MISMATCH — exactly one side names a chapter. This is the
    ///     one fuzzy decision that can SUPPRESS a distinct finding instead of merely duplicating it, and the one
    ///     where <see cref="MayFold"/> contributes NOTHING (a null order is a wildcard). See the constant.
    ///   • <see cref="DefaultThreshold"/> (0.45) everywhere else — an OPEN row (a wrong claim there is visible and
    ///     costs the user nothing), or a user-acted row whose anchors AGREE (same real chapter, or both book-wide),
    ///     which is b4's own bucket and keeps b4's measured margins.
    ///
    /// DONE AND ACKNOWLEDGED GET THE SAME BAR AS DISMISSED, deliberately. All three are rows the user has ACTED on,
    /// and all three share the two properties that make the harm possible: the row is NEVER deleted (so it is a
    /// permanent absorption surface that only accumulates), and a fresh finding that claims it is NOT INSERTED and
    /// inherits a status that says the user is finished with it. `dismissed` is the extreme (the card is not even
    /// rendered, so the distinct finding is invisible); `done` is barely better (a brand-new criticism arrives
    /// pre-resolved, and the user has already moved on); `acknowledged` is the mildest but still means a fresh
    /// criticism is presented as one they have already triaged. The distinction between them is a matter of DEGREE,
    /// not of kind — and keying the rule on a single "the user has acted on this row" predicate avoids a
    /// status-classification cliff where a one-word FE or API change silently moves a row out of the fence.
    /// The cost of including them is bounded and cheap: a visible duplicate, only in the 0.45-0.60 band, only across
    /// an anchor mismatch.
    /// </summary>
    internal static double RequiredPersistedThreshold(
        int? freshOrder,
        int? rowComparisonOrder,
        string? rowStatus,
        double threshold = DefaultThreshold,
        double userActedAnchorMismatchThreshold = UserActedAnchorMismatchThreshold)
    {
        // "open" is the ONLY non-user-acted status the persist step writes; anything else (including an unknown
        // future one) is treated as a user decision - fail-CLOSED, so a new status cannot quietly opt out of the
        // fence. That rule, its trimming and its casing policy live in FindingStatusPartition; do not re-spell
        // the status strings here.
        var userActed = FindingStatusPartition.IsUserActed(rowStatus);

        // The MayFold WILDCARD case, and ONLY it: exactly one side is book-wide, so the anchors carry no evidence
        // either way. Both anchored on the same chapter, or both book-wide, is agreement — not a mismatch.
        var anchorMismatch = freshOrder.HasValue != rowComparisonOrder.HasValue;

        return userActed && anchorMismatch ? userActedAnchorMismatchThreshold : threshold;
    }

    /// <summary>
    /// The BUCKET key's dimension: case-folded and trimmed, and NOTHING ELSE. An unknown or blank value is returned
    /// AS-IS, in its own bucket.
    ///
    /// final-r01 — IT DELIBERATELY DOES *NOT* CANONICALISE, AND THAT IS WHY IT IS NOT
    /// <see cref="BookReviewService.NormalizeDimension"/>. The two look like the same function and are not: the
    /// service's version falls back to <c>"plot"</c> for anything it does not recognise. This one must not, and the
    /// difference is a SAFETY property, not drift left lying around (it was flagged as drift by be-c09; the review
    /// resolved it the other way).
    ///
    /// WHY. This value is a GATE on a SUPPRESSING operation. <see cref="FindPersistedNearDuplicate"/> only lets a
    /// fresh finding claim a persisted row when the two share this key — and a claim on a row the user DISMISSED
    /// means the fresh finding is never inserted and NEVER REACHES THE USER (that is the whole harm P2-1/be-c07
    /// exists to fence, and why a user-acted row across an anchor mismatch had its bar raised to 0.60). Adopting the
    /// <c>"plot"</c> fallback here would file every unlabelled row INTO THE PLOT BUCKET, where any plot finding could
    /// then claim it. Canonicalising is a WIDENING of what may be suppressed — the one direction this subsystem may
    /// not move in — dressed up as a tidy-up.
    ///
    /// Refusing to guess is the fail-OPEN answer: an unlabelled row sits alone, no fresh finding matches it, and the
    /// fresh copy is INSERTED as its own visible card. A duplicate, never a silence.
    ///
    /// WHAT IT COSTS: nothing measurable. Every finding this engine writes is canonical before it is projected
    /// (<c>BookReviewService.BucketByDimension</c> normalises IN PLACE, and the legacy per-dimension path stamps a
    /// canonical dimension on every item), so <c>ProjectToEntity</c> can only ever store one of the six — which means
    /// this method is the IDENTITY on every row and every fresh finding the code itself produced, and the two
    /// implementations AGREE on all of them. It diverges only on a row some other writer left non-canonical, and
    /// there fail-open is exactly the answer we want.
    /// </summary>
    private static string BucketDimension(string? dimension) =>
        (dimension ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// max(Jaccard, containment) over two content-token sets. Containment (|A∩B| / |smaller|) is what catches the
    /// strict-superset rewording — an inserted adjective — which Jaccard alone under-scores; Jaccard is what keeps
    /// two long, differently-worded statements of the same finding together when neither contains the other.
    /// Empty sets score 0 (never similar), so a rationale with no content tokens can never absorb anything.
    /// </summary>
    internal static double Similarity(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a is null || b is null || a.Count == 0 || b.Count == 0)
            return 0d;

        var smaller = a.Count <= b.Count ? a : b;
        var larger = ReferenceEquals(smaller, a) ? b : a;
        var largerSet = larger as HashSet<string> ?? new HashSet<string>(larger, StringComparer.Ordinal);

        var intersection = smaller.Count(t => largerSet.Contains(t));
        var union = a.Count + b.Count - intersection;

        var jaccard = union == 0 ? 0d : (double)intersection / union;
        var containment = (double)intersection / smaller.Count;
        return Math.Max(jaccard, containment);
    }

    // ── Hebrew-aware normalization ───────────────────────────────────────────────────────────────────

    /// <summary>Hebrew one-letter proclitics (the definite article, and the "and / that / in / to / as / from"
    /// particles). They attach to the following word, so "המחיר" and "והמחיר" are the same content word.</summary>
    private static readonly HashSet<char> Prefixes = new() { 'ה', 'ו', 'ש', 'ב', 'ל', 'כ', 'מ' };

    /// <summary>Inflectional / possessive tails, LONGEST FIRST (first match wins). Strips "דמותו" → "דמות" and
    /// "אווירה"/"אווירת" → "אוויר", which is what makes two rewordings of one finding share tokens at all.</summary>
    private static readonly string[] Suffixes =
        { "יהם", "ותיו", "ותיה", "ים", "ות", "תו", "תה", "ת", "ו", "י", "ה", "ם", "ן" };

    /// <summary>
    /// Function words carrying no content. Hebrew first (these dominate the rationales), then the English set —
    /// review language is he | en, and an English rationale must normalize just as well.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "של", "את", "על", "אל", "עם", "אך", "או", "גם", "כי", "לא", "יש", "אין", "זה", "זו", "זאת", "אשר",
        "כדי", "אף", "רק", "כל", "בין", "לבין", "מן", "אם", "הוא", "היא", "הם", "הן", "אני", "כמו", "לפי",
        "כך", "ללא", "אינו", "אינה", "שאינו", "שאינה", "היטב", "מאוד", "יותר", "עד", "אבל", "לו", "לה",
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "at", "as", "by", "for", "with", "from",
        "is", "are", "was", "were", "be", "been", "it", "its", "this", "that", "these", "those", "but",
        "not", "has", "have", "had", "do", "does", "did", "which", "who", "their", "his", "her", "they",
    };

    /// <summary>
    /// The rationale reduced to a SET of normalized content tokens: Unicode NFC → lower-cased → combining marks
    /// (Hebrew niqqud / cantillation) AND Unicode FORMAT characters (LRM/RLM/ZWJ/ZWNJ) removed → every remaining
    /// non-letter/digit treated as a separator → stopwords dropped → each token stemmed (leading proclitics to a
    /// fixed point, then one inflectional tail, never below a 3-letter stem — see <see cref="Stem"/>) →
    /// 1-character tokens dropped.
    ///
    /// A SET, not a bag: a word repeated in one rationale must not inflate the overlap with a rationale that uses
    /// it once. The stemming is deliberately crude (Hebrew has no lightweight morphological analyzer here) and is
    /// applied SYMMETRICALLY, so its errors — "מורגן" → "מורג", "שתיקה" → "תיק" — cost nothing as long as both
    /// sides mangle the same word the same way. What it must not do is make DIFFERENT words collide, which the
    /// 3-letter stem floor and the measured precision margin (closest distinct pair 0.273, well under the 0.45
    /// threshold) both guard.
    ///
    /// NIT-3: FORMAT characters are removed (not treated as a separator), matching
    /// <see cref="ChapterAnchorResolver.NormalizeTitle"/> exactly — both call it "invisible, never part of the
    /// word's identity". Before this fix the two normalizers DISAGREED on the same character class: the resolver
    /// stripped an embedded LRM/RLM (Hebrew titles pick these up from Word/Syncfusion round-trips) while this
    /// method split on it, so a rationale carrying a stray RLM in the middle of a word produced two token halves
    /// instead of one — a silent recall loss on exactly the kind of invisible mark this codebase already knows to
    /// expect in Hebrew text.
    /// </summary>
    internal static HashSet<string> ContentTokens(string? rationale)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(rationale))
            return result;

        var normalized = rationale.Normalize(NormalizationForm.FormC).ToLowerInvariant();

        var current = new StringBuilder();
        foreach (var c in normalized)
        {
            // Combining marks (Hebrew niqqud / cantillation) AND Format characters (LRM/RLM/ZWJ/ZWNJ …, the same
            // marks ChapterAnchorResolver.NormalizeTitle strips — NIT-3): both are invisible to word identity, and
            // dropping them must not split the word. Deliberately NOT a "U+0591..U+05C7" range test — that range
            // also holds the MAQAF (U+05BE), a hyphen: skipping it would GLUE "בית-ספר" into one bogus token
            // instead of two. Maqaf is punctuation, so it falls through to the separator branch below, which is
            // correct.
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.Format)
                continue;

            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
                continue;
            }

            // Anything else (whitespace, punctuation, geresh/gershayim) ends the token.
            AddToken(result, current);
        }
        AddToken(result, current);

        return result;
    }

    private static void AddToken(HashSet<string> sink, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        var raw = current.ToString();
        current.Clear();

        if (Stopwords.Contains(raw))
            return;

        var stemmed = Stem(raw);
        if (stemmed.Length < 2 || Stopwords.Contains(stemmed))
            return;

        sink.Add(stemmed);
    }

    /// <summary>
    /// Strips leading Hebrew proclitics TO A FIXED POINT (while the head is a proclitic and the stem would stay
    /// at least 3 letters), then ONE inflectional tail under the same 3-letter floor.
    ///
    /// The fixed point is the whole point, and a CAPPED loop is a bug: cap it at two strips and "והמחיר" (two
    /// proclitics) stems to "מחיר" while "המחיר" (one) stems to "חיר" — the SAME content word, two different
    /// tokens, so the rewording it exists to absorb slips straight through. Stripping to exhaustion makes the
    /// stem a function of the word alone FOR A ROOT OF 3 LETTERS OR MORE, so however many particles the model
    /// glued on, both sides land in the same place: והמחיר / המחיר / מחיר → "חיר"; הדמות / דמותו / דמות → "דמו".
    ///
    /// It over-strips words whose stem happens to BEGIN with a proclitic letter ("מורגן" → "ורגן"), which is
    /// harmless precisely because it is symmetric — both sides mangle it identically — and is contained by the
    /// 3-letter floor plus the measured precision margin.
    ///
    /// P3-1, THE LIMIT OF "A FUNCTION OF THE WORD ALONE": the 3-letter FLOOR that makes the paragraph above true
    /// is exactly what breaks it for a 2-LETTER root. The prefix loop refuses to run at all once the token is
    /// already 3 characters long (its own guard is <c>t.Length &gt;= 4</c>), so a bare 2-letter root ("בן", son)
    /// and the SAME root with one proclitic attached ("הבן", the son — 3 characters) come out as two DIFFERENT
    /// stems: "בן" and "הבן". Confluence holds for a 3+-letter root; it does not for a 2-letter one. This is a
    /// RECALL loss only, never a false merge: two DIFFERENT 2-letter roots still land on different stems (the
    /// floor still separates real words), it just fails to unify a short root's bare and prefixed forms with
    /// each other. Not worth special-casing — 2-letter Hebrew content roots are rare in editorial rationale
    /// prose, and the measured precision margin was never at risk from this.
    /// </summary>
    private static string Stem(string token)
    {
        var t = token;

        while (t.Length >= 4 && Prefixes.Contains(t[0]))
            t = t.Substring(1);

        foreach (var suffix in Suffixes)
        {
            if (t.Length - suffix.Length >= 3 && t.EndsWith(suffix, StringComparison.Ordinal))
            {
                t = t.Substring(0, t.Length - suffix.Length);
                break;
            }
        }

        return t;
    }
}
