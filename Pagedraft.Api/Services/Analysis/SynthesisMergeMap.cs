using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// b8 — THE SYNTHESIS MERGE MAP: the DELETE channel the reduce pass never had.
///
/// THE BUG THIS CLOSES. The whole-book review is a map-reduce. The SYNTHESIS pass is the reduce: it is a real
/// model call, it is shown EVERY accumulated window finding side by side in one digest, and its prompt has always
/// told it — in both languages — to "merge duplicate or near-duplicate findings into a single finding that unions
/// the chapters involved". But its output schema was a flat findings array which the build APPENDS to the
/// accumulated set. So the only way the model could express "these two are one finding" was to emit a THIRD
/// finding, which was then unioned with the two originals. A REDUCE THAT CAN ONLY ADD IS NOT A REDUCE. The
/// instruction was live, the model complied with it, and the compliance made the duplicate list LONGER.
///
/// WHY A MODEL SIGNAL, WHEN EVERY OTHER PASS IS DETERMINISTIC. Because on the real corpus the token metric is
/// EXHAUSTED. Scoring the live duplicate/distinct pairs of book A63A6E02 with the SHIPPED
/// <see cref="NearDuplicateCollapser.Similarity"/> gives:
///     0.875 DUP  &gt;  0.462 DISTINCT  &gt;  0.455 DUP  &gt;  0.444 DUP  &gt;  0.375 DUP  &gt;  0.364 DISTINCT
/// The two classes are INTERLEAVED, so NO threshold on max(Jaccard, containment) separates them — not 0.45, not
/// 0.40, not 0.30. And the 0.462 DISTINCT pair is not a marginal loss if we get it wrong: it is a SEVERITY-3
/// FACTUAL CONTRADICTION, the single most valuable finding in that book, sitting 0.007 away from a true duplicate.
/// Separating them needs something that reads MEANING rather than tokens. The synthesis model already reads all of
/// them; it just had nowhere to put the answer.
///
/// THE CONTRACT (additive on the OUTPUT side — a response with NO "merges" key is handled exactly as it was
/// pre-b8. The INPUT side is not additive and not optional: see KILL-SWITCH below):
///   • Every accumulated finding printed in the [WINDOW_FINDINGS] digest gets a stable BUILD-LOCAL id, W1..Wn, as
///     the first column of its line. The ids exist only for the duration of one build.
///   • The synthesis may return, beside its findings, <c>"merges": [ { "ids": ["W3","W7"], "keep": "W7" } ]</c>.
///   • Each group means: these accumulated findings are ONE finding; keep that one.
///
/// WHAT IT IS NOT — AND THIS IS THE LOAD-BEARING NON-DECISION. The synthesis output does NOT replace the window
/// findings. The obvious "make the reduce authoritative" design is CATASTROPHIC here: the synthesis prompt caps its
/// output at 12 findings, against ~23 accumulated on a real 17-chapter book, so a pure replace would SILENTLY
/// DESTROY 11+ findings on every single build. The merge map buys the reduce's authority WITHOUT the replace's
/// blast radius: it can only remove findings it EXPLICITLY NAMES, and a finding it does not name is never touched.
///
/// FAIL-CLOSED BY CONSTRUCTION (every one of these is a REJECT, not a partial honour):
///   • an id that was never printed in the digest (invented / hallucinated)   → reject the GROUP
///   • the same id twice in one group, or a group of fewer than 2 ids         → reject the GROUP
///   • "keep" absent, or naming a finding outside the group's own ids         → reject the GROUP
///   • a finding named by an EARLIER accepted group (one group per finding)   → reject the GROUP
///   • "keep" naming a finding the exact-key dedup already dropped            → reject the GROUP
///   • members anchored to TWO DIFFERENT REAL CHAPTERS (be-c07)               → reject the GROUP
///   • a member whose stored anchor payload does not PARSE (be-c08 / P3-7)    → reject the GROUP
///   • a "keep" that anchors NO chapter while a member it absorbs anchors one → reject the GROUP
///     (be-c08 / P3-8 — the union would MOVE the survivor's anchors[0], see below)
/// A malformed group is never partially applied: half-trusting a model that just proved it is confused about which
/// findings exist is how a merge deletes the wrong row.
///
/// NOTE WHAT EVERY RULE ABOVE EXCEPT THE LAST ONE HAS IN COMMON: it validates the model against ITSELF (are the ids
/// real, is the survivor inside the group, is a finding claimed twice). Not one of them asks whether the findings it
/// wants to merge are even ABOUT THE SAME PART OF THE BOOK. The last rule — <see cref="AnchorsMayMerge"/>, added by
/// be-c07 — is the only DETERMINISTIC FLOOR under the model's judgement, and it exists because that judgement has
/// already been MEASURED destroying a whole dimension on a real book. Read its remarks before touching it: the
/// obvious relaxation (compare the full anchor SETS and allow any overlap) is UNSAFE on the real corpus.
///
/// WHAT A MERGE DOES TO THE SURVIVOR (and, just as importantly, what it does NOT):
///   • The survivor is one of the ORIGINALS, kept VERBATIM. No merged prose is ever fabricated — the model picks a
///     survivor from findings it was shown; it does not write a new one. (It may ALSO emit new HOLISTIC synthesis
///     findings, as it always could, and the build still APPENDS those — that code path is unchanged. What the b8
///     prompt now forbids is a new finding written to DESCRIBE a merge, which is the ADD that made the duplicate
///     list longer.)
///   • ANCHORS are UNIONED, by APPEND only, so the survivor's OWN first anchor stays at index 0. This is the
///     sharpest trap in the whole change: <c>anchors[0].Order</c> is a DEDUP-KEY input (BookReviewService's
///     ProjectToEntity derives primaryOrder from it, and the key is stamped there, BEFORE this pass). If the union
///     reordered index 0, the key would move, b3's LegacyDedupKeyV1 fallback would stop matching, and every
///     acknowledged / dismissed finding would be re-orphaned and lose the user's Status. Appending cannot move it.
///     The key is NEVER recomputed here — b3 owns the derivation and changed it exactly once.
///     be-c08 (P3-8) — THE ONE CASE WHERE APPENDING *DOES* MOVE INDEX 0: a survivor with ZERO anchors. Appending to
///     an EMPTY list CREATES anchors[0] out of an absorbed copy's chapter, so the survivor's primary order moves
///     from "none" to that chapter while its key is still hashed at "none" — and its
///     <see cref="NearDuplicateCollapser.Candidate.PrimaryChapterOrder"/> is still null, which would let the very
///     next pass (b4b's FoldNoAnchorIntoAnchored) fold away the survivor the model chose to KEEP, discarding the
///     anchors just unioned onto it. That group is now REJECTED outright. Cost: a model that keeps the BOOK-WIDE
///     copy of a duplicate whose other copy is anchored gets no merge — which is the right answer anyway, because
///     b4b's own rule is that the ANCHORED copy must survive (a navigable finding is never traded for a book-wide
///     one), and the deterministic pass will fold the pair itself when they really are one finding.
///   • SEVERITY is the MAX of the group (b4b's rule). Severity is a scalar the user triages on, not prose, so
///     lifting it fabricates nothing; taking the survivor's verbatim would let the model's choice of survivor
///     silently DOWNGRADE a major finding.
///   • RATIONALE, SUGGESTEDACTION and EVIDENCE are NOT merged. Pairing one copy's quotes with another's prose would
///     fabricate a finding neither of them states (b4's rule, unchanged).
///
/// KILL-SWITCH: <c>Ai:BookReview:SynthesisMergeMap</c>, DEFAULT OFF. It gates the APPLY step, and NOTHING ELSE.
///
/// WHAT "OFF" ACTUALLY MEANS (be-c06 / P1-4 — the claim that used to stand here, "the build is byte-identical to
/// pre-b8", was FALSE, and this replaces it):
///   • NO MERGE IS APPLIED. No finding is deleted, no anchor unioned, no severity lifted. The switch gates the only
///     MUTATION, which is the only irreversible harm this feature can do.
///   • The map is still PARSED, VALIDATED against the digest ids, RESOLVED to concrete findings, PLANNED and LOGGED
///     — the coverage line NAMES the exact groups a flipped build would have merged. The channel is read; it just
///     does not write. That is measure mode, and it is the entire reason this ships rather than waits.
///   • BUT THE MODEL'S INPUT IS NOT REVERTED BY THE SWITCH. b8 changed the synthesis prompt and digest
///     UNCONDITIONALLY: the digest gained the <c>W#</c> id column, the rationale cap went 140 → 260, and BOTH
///     synthesis prompts (He + En) gained the merge contract — including "do NOT write a new finding to describe a
///     merge; a merge is expressed ONLY through <c>merges</c>". So with the switch OFF the model reads a DIFFERENT
///     prompt from the pre-b8 one, and its OWN findings, anchors and severities may differ from what a pre-b8 build
///     would have produced. OFF is a THIRD behavior: it is not b8, and it is not pre-b8 either. Anything that needs
///     the pre-b8 build must REVERT b8 — flipping this bit will not get you there.
///
/// WHY THE PROMPT IS DELIBERATELY *NOT* GATED (be-c06 considered gating it, and rejected it):
///   • GATING IT WOULD DESTROY THE MEASUREMENT. With no ids in the digest and no merge contract in the prompt there
///     is no map to validate and nothing to log; every OFF build would report "0 proposed over 0 ids" — precisely
///     the guard-that-never-ran signature this subsystem keeps re-learning the hard way (b4c: 136 green tests, and
///     its fold fired ZERO times on real data). The operator would have to FLIP the switch to get the first datum,
///     which is not a staged rollout, it is a dead branch with a flag on it.
///   • WORSE, IT WOULD INVALIDATE IT. The OFF log is an honest forecast of what flipping the switch would do ONLY
///     BECAUSE THE ON PROMPT AND THE OFF PROMPT ARE THE SAME PROMPT: the response being measured is the response the
///     flipped build would get. Fork the prompt on the switch and every OFF measurement is taken against an input
///     the ON build will never see.
///   • AND "PRE-B8" IS NOT A STATE WORTH RESTORING. Its 140-char cap truncated exactly ONE rationale on the real
///     corpus — the SEV-3 FACTUAL CONTRADICTION, at 163 chars — i.e. it hid the specificity that PREVENTS a false
///     merge, in the input to the pass that decides merges. And its reconcile instruction was LIVE and unmuzzled
///     ("merge duplicate or near-duplicate findings into a single finding that unions the chapters"): the model
///     COMPLIED — by emitting a THIRD finding, which the build APPENDED beside the two originals. That is not a
///     hypothesis, it is the measured mechanism of the duplicate bug this whole change set exists to kill. Gating
///     the prompt back would re-ship the duplicate amplifier in the state that actually ships.
///
/// THE RECONCILIATION HOLE, ACCEPTED WITH OPEN EYES. In the OFF state the model is told not to reconcile inside
/// <c>findings</c>, and the <c>merges</c> it returns instead is not applied. It is NOT left with nowhere to SPEAK —
/// its answer is validated and written to the coverage log, which is the artefact this rollout exists to produce —
/// but it IS left with nowhere to ACT. That is the trade, made deliberately: the only thing pre-b8 let it DO
/// instead was lengthen the duplicate list. Silence beats that. If this switch is ever ABANDONED rather than
/// flipped, strip the merge contract out of the prompts as well; do not soften it back into "reconcile inside
/// findings", which is the bug.
/// </summary>
internal static class SynthesisMergeMap
{
    /// <summary>The prefix of a build-local digest id (W1, W2, ... Wn).</summary>
    internal const string IdPrefix = "W";

    /// <summary>Formats the build-local id of the <paramref name="oneBasedIndex"/>-th accumulated finding. The
    /// SINGLE source of the id format: the digest prints it and the resolver parses it, so they cannot drift.</summary>
    internal static string IdFor(int oneBasedIndex) => IdPrefix + oneBasedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One VALIDATED merge group: the accumulated finding that survives, and the ones it absorbs. Holds
    /// the model items by REFERENCE (not by id) so the apply step cannot re-interpret an id a second time. The ids
    /// are carried along for the COVERAGE LOG only — during a staged rollout, "1 group applied" is far less useful
    /// than "W3+W7 -> W7", which is what lets a human check the model's judgement against the digest it was shown.</summary>
    internal sealed record MergeGroup(
        string KeepId, IReadOnlyList<string> Ids, BookFindingItem Survivor, IReadOnlyList<BookFindingItem> Absorbed)
    {
        /// <summary>"W3+W7 -&gt;W7" — the group as the model expressed it.</summary>
        public string Describe() => string.Join("+", Ids) + "->" + KeepId;
    }

    /// <summary>
    /// What the synthesis pass proposed and what survived validation — carried from the synthesis call site into
    /// <c>UnionAndDedup</c> so ONE coverage line can report the whole funnel (proposed → applied → rejected).
    ///
    /// NULL (rather than an empty instance) is the meaningful "the synthesis pass did not run at all" state — a
    /// book with no BookBrief, or the legacy per-dimension path. Nothing is logged then, because there is nothing
    /// to say. An instance with zero groups means the pass RAN and proposed nothing, which is a fact worth logging.
    /// </summary>
    internal sealed class Resolution
    {
        /// <summary>The kill-switch state (<c>Ai:BookReview:SynthesisMergeMap</c>). FALSE = measure only.</summary>
        public required bool Enabled { get; init; }

        /// <summary>How many groups the model emitted, BEFORE validation.</summary>
        public required int ProposedGroups { get; init; }

        /// <summary>How many findings the digest actually printed an id for (the id space the model could name).</summary>
        public required int DigestIdCount { get; init; }

        /// <summary>The groups that survived validation, in the order the model emitted them.</summary>
        public required IReadOnlyList<MergeGroup> Groups { get; init; }

        /// <summary>Rejection reason → count. Surfaced in the coverage log so a model that is fighting the contract
        /// (inventing ids, naming one finding in two groups) is VISIBLE rather than silently ignored.</summary>
        public required IReadOnlyDictionary<string, int> Rejections { get; init; }

        /// <summary>"W1=character/13, W2=continuity/-, …" — the id space the model was SHOWN, logged at Debug. The
        /// question a staged rollout actually has to answer is not "how many merges did it make" but "of the
        /// duplicates that SURVIVED, which ones were even in front of it": a duplicate whose copies never shared a
        /// digest is a STRUCTURAL miss (nothing to tune), while one it saw and did not name is a MODEL-RECALL miss.
        /// Those have completely different fixes, and without this line they are indistinguishable.</summary>
        public required string DigestSummary { get; init; }

        public static Resolution Empty(bool enabled) => new()
        {
            Enabled = enabled,
            ProposedGroups = 0,
            DigestIdCount = 0,
            Groups = Array.Empty<MergeGroup>(),
            Rejections = new Dictionary<string, int>(StringComparer.Ordinal),
            DigestSummary = string.Empty,
        };
    }

    /// <summary>
    /// Validates the model's raw merge map against the ids the digest ACTUALLY printed, and resolves each surviving
    /// group to the BookFindingItem objects it names. Non-throwing: any fault resolves to ZERO GROUPS, i.e. no merge
    /// is applied, which is the fail-closed direction — a merge we do not make is a visible duplicate, a merge we
    /// make wrongly is a deleted finding.
    /// </summary>
    /// <param name="enabled">The kill-switch. Validation runs either way (the OFF state is MEASURED, not blind);
    /// only <see cref="Apply"/> honours it. It does NOT gate the prompt: the model is asked for <c>merges</c> in
    /// both states, by design (see the KILL-SWITCH note on the class).</param>
    /// <param name="proposed">The model's raw <c>merges</c> array (may be null — the key is optional).</param>
    /// <param name="idMap">Build-local id → the accumulated finding whose digest line carried it. Contains ONLY
    /// the lines the digest EMITTED: a finding the budget cap dropped was never shown to the model, so an id for it
    /// would be an invention and must not resolve.</param>
    internal static Resolution Resolve(
        bool enabled,
        IReadOnlyList<SynthesisMergeItem>? proposed,
        IReadOnlyDictionary<string, BookFindingItem> idMap,
        ILogger? logger = null)
    {
        if (idMap is null || idMap.Count == 0)
            return Resolution.Empty(enabled);

        var groups = new List<MergeGroup>();
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var proposedCount = proposed?.Count ?? 0;

        // One finding may take part in at most ONE group. Tracked by REFERENCE: two different ids can never map to
        // the same item (the digest prints one line per accumulated finding), but a model naming the same finding in
        // two groups is exactly the confusion this guard exists for.
        var claimed = new HashSet<BookFindingItem>(ReferenceItemComparer.Instance);

        try
        {
            foreach (var group in proposed ?? Enumerable.Empty<SynthesisMergeItem>())
            {
                if (group?.Ids is not { Count: > 0 })
                {
                    Reject(rejections, "no-ids");
                    continue;
                }

                var ids = group.Ids
                    .Select(NormalizeId)
                    .ToList();

                if (ids.Any(string.IsNullOrEmpty))
                {
                    Reject(rejections, "blank-id");
                    continue;
                }

                // A group that names the same finding twice ("ids": ["W3","W3"]) is not a merge, it is a model that
                // has lost track of what it is looking at. Reject rather than silently de-duplicate the id list.
                if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                {
                    Reject(rejections, "duplicate-id");
                    continue;
                }

                if (ids.Count < 2)
                {
                    Reject(rejections, "too-few-ids");
                    continue;
                }

                // An id the digest never printed is an INVENTION. It is the same failure mode as the phantom chapter
                // anchors b1 exists for, and it gets the same answer: do not guess what the model meant.
                if (ids.Any(id => !idMap.ContainsKey(id)))
                {
                    Reject(rejections, "unknown-id");
                    continue;
                }

                var keep = NormalizeId(group.Keep);
                if (string.IsNullOrEmpty(keep) || !ids.Contains(keep, StringComparer.Ordinal))
                {
                    // No survivor, or a survivor from OUTSIDE the group. Picking one for the model would be us
                    // deciding which finding the user loses, on no evidence.
                    Reject(rejections, "bad-keep");
                    continue;
                }

                var items = ids.Select(id => idMap[id]).ToList();
                if (items.Any(claimed.Contains))
                {
                    Reject(rejections, "finding-already-in-another-group");
                    continue;
                }

                var survivor = idMap[keep];
                var absorbed = items.Where(i => !ReferenceEquals(i, survivor)).ToList();
                if (absorbed.Count == 0)
                {
                    Reject(rejections, "nothing-to-absorb"); // every id resolved to the survivor itself
                    continue;
                }

                foreach (var item in items)
                    claimed.Add(item);

                groups.Add(new MergeGroup(keep, ids, survivor, absorbed));
            }
        }
        catch (Exception ex)
        {
            // FAIL-CLOSED, and NOT silent. Zero groups = NO merge applied (nothing deleted), never a partial honour.
            logger?.LogWarning(ex,
                "Book review (synthesis merge map): validation faulted; NO merge is applied this build (fail-closed).");
            return new Resolution
            {
                Enabled = enabled,
                ProposedGroups = proposedCount,
                DigestIdCount = idMap.Count,
                Groups = Array.Empty<MergeGroup>(),
                Rejections = new Dictionary<string, int>(StringComparer.Ordinal) { ["validation-faulted"] = 1 },
                DigestSummary = DescribeDigest(idMap),
            };
        }

        return new Resolution
        {
            Enabled = enabled,
            ProposedGroups = proposedCount,
            DigestIdCount = idMap.Count,
            Groups = groups,
            Rejections = rejections,
            DigestSummary = DescribeDigest(idMap),
        };
    }

    /// <summary>The id space the model was shown, as "W1=character/13, W2=continuity/-" (raw model anchor order, or
    /// "-" for a book-wide finding). Diagnostic only.</summary>
    private static string DescribeDigest(IReadOnlyDictionary<string, BookFindingItem> idMap) =>
        string.Join(", ", idMap
            .OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var order = kv.Value.ChapterAnchors is { Count: > 0 } a
                    ? a[0].Order.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "-";
                return $"{kv.Key}={kv.Value.Dimension}/{order}";
            }));

    /// <summary>
    /// PASS 0 of the collapse pipeline. Applies the validated merge map to the freshly PROJECTED candidates, BEFORE
    /// the near-duplicate collapser's passes 1-3 (b4 within-bucket / b4b cross-bucket / b4c cross-dimension) — so
    /// those passes see the ALREADY-MERGED set and never re-litigate a decision the model made with the whole book's
    /// findings in front of it.
    ///
    /// It runs on the ENTITY side, deliberately, and that is what makes the anchor union safe: every anchor here has
    /// ALREADY been resolved by <see cref="ChapterAnchorResolver"/> under the shown-set of the pass that WROTE it
    /// (b7's visibility gate). So a chapter link a merge carries over was seen by the window that claimed it —
    /// unioning at the raw-model level would instead have re-judged the absorbed copy's anchors against the
    /// SURVIVOR's shown-set, which is a different window's, and either dropped real anchors or laundered unseen ones.
    ///
    /// THE DEDUP KEY IS NOT TOUCHED. It was stamped in ProjectToEntity from the survivor's own first anchor and is
    /// left exactly as it is; <see cref="NearDuplicateCollapser.Candidate.PrimaryChapterOrder"/> is likewise left at
    /// the survivor's own value, so the collapser's bucket key and the persisted dedup key still agree — the
    /// invariant that whole file depends on.
    ///
    /// COVERAGE LOG. Emits ONE line per build, ALWAYS (even for zero groups, even with the switch OFF). A guard that
    /// reports only its positive count is indistinguishable from a guard that never ran: b4c shipped with 136 green
    /// tests and its fold fired ZERO times on real data, and only a coverage line would have caught it.
    ///
    /// PLAN → STAGE → COMMIT, and the fail-safe below is only HONEST because of it (be-c08 / P3-5). Every group is
    /// validated AND its survivor's new anchor payload and severity are COMPUTED into the plan before ANY entity is
    /// touched; the commit loop then does nothing but assign. The pre-fix code mutated survivors AS IT WENT, so a
    /// throw halfway through left the earlier groups' anchors unioned and severities lifted while the catch returned
    /// "the UN-merged set" and the comment claimed the pre-pass state was restored. It was not: it was a THIRD state
    /// nobody had reasoned about. Nothing below the commit line can throw, and nothing above it can mutate.
    /// </summary>
    /// <param name="candidates">The projected candidates, in build order (what UnionAndDedup is about to collapse).</param>
    /// <param name="candidateItems">The RAW model item each candidate was projected from, index-aligned with
    /// <paramref name="candidates"/>. This is the bridge from the model's ids (which name items) to the entities.</param>
    /// <param name="resolution">The validated map, or NULL when the synthesis pass did not run (nothing is logged).</param>
    /// <returns>The surviving candidates, in input order. With the switch OFF, or with no applicable group, this is
    /// the input list unchanged.</returns>
    internal static List<NearDuplicateCollapser.Candidate> Apply(
        IReadOnlyList<NearDuplicateCollapser.Candidate> candidates,
        IReadOnlyList<BookFindingItem> candidateItems,
        Resolution? resolution,
        ILogger? logger = null)
    {
        var all = candidates?.ToList() ?? new List<NearDuplicateCollapser.Candidate>();
        if (resolution is null)
            return all; // the synthesis pass never ran: no channel, no log, no change.

        try
        {
            // item → candidate index. An item is MISSING here when the exact-key dedup dropped it as a byte-identical
            // duplicate of an earlier one, or when it had no rationale. Both mean "this finding is already gone".
            var indexByItem = new Dictionary<BookFindingItem, int>(ReferenceItemComparer.Instance);
            for (var i = 0; i < all.Count && i < (candidateItems?.Count ?? 0); i++)
            {
                if (candidateItems![i] is { } item)
                    indexByItem[item] = i;
            }

            // PLAN AND STAGE first, COMMIT second — for two reasons, and both are load-bearing.
            //
            // (1) THE OFF (measure) PATH AND THE ON (apply) PATH COMPUTE THE SAME NUMBERS, so the coverage log with
            // the switch OFF is an honest forecast of what flipping it would do. That forecast is only SOUND because
            // the PROMPT is not gated by the switch either (class doc, KILL-SWITCH): the response being measured is
            // the response a flipped build would get from the very same input. Gate the prompt and this line becomes
            // a measurement of a state that will never ship. EVERY reject below therefore runs in BOTH states.
            //
            // (2) THE FAIL-SAFE CATCH BELOW BECOMES TRUE (be-c08 / P3-5). The staged plan carries the survivor's NEW
            // anchor JSON and NEW severity, computed here; the commit loop only assigns them. Nothing in this loop
            // touches an entity, so a fault anywhere in it leaves the findings EXACTLY as they arrived — which is
            // what the catch has always claimed to return, and did not.
            //
            // STAGING ALONE WAS NOT ENOUGH, AND final-r01 HAD TO FINISH THE JOB: the commit loop also has to contain
            // NOTHING BUT ASSIGNMENTS. It used to emit the per-group audit LOG between the writes, and a logger can
            // throw, so a fault part-way through still left earlier survivors merged inside a set reported as
            // un-merged. Every log now runs BEFORE the first write. See the COMMIT block.
            //
            // The plan carries the GROUP, not just the indexes, so both logs below can name the groups they are
            // ACTUALLY talking about. Resolve's valid set is not that set: a group whose survivor the exact-key dedup
            // already removed is rejected HERE, and reporting it as "applied" would be one more claim the code does
            // not implement.
            var plan = new List<PlannedMerge>();
            var rejections = new Dictionary<string, int>(resolution.Rejections, StringComparer.Ordinal);

            foreach (var group in resolution.Groups)
            {
                if (!indexByItem.TryGetValue(group.Survivor, out var survivorIndex))
                {
                    // The chosen survivor is not in the candidate set (the exact-key dedup already collapsed it away).
                    // Promoting a different member would be US choosing the survivor, on the model's behalf, for a
                    // group whose premise has already shifted. Reject.
                    Reject(rejections, "keep-not-a-candidate");
                    continue;
                }

                var absorbed = new List<int>();
                foreach (var item in group.Absorbed)
                {
                    if (indexByItem.TryGetValue(item, out var index) && index != survivorIndex)
                        absorbed.Add(index);
                }

                if (absorbed.Count == 0)
                {
                    Reject(rejections, "members-already-deduped"); // the exact-key dedup got there first: a no-op
                    continue;
                }

                // THE ANCHOR-COMPATIBILITY FENCE (be-c07 / P2-4) — the deterministic FLOOR under the model's
                // judgement. Everything above this line validates the model against ITSELF (are the ids real, is the
                // survivor in the group, is a finding named twice); nothing checked whether the findings it wants to
                // merge are even ABOUT THE SAME CHAPTER. See AnchorsMayMerge.
                var members = new List<int>(absorbed.Count + 1) { survivorIndex };
                members.AddRange(absorbed);
                if (!AnchorsMayMerge(all, members))
                {
                    Reject(rejections, "anchors-span-different-chapters");
                    continue;
                }

                // STAGE the survivor's new state (be-c08). This is where the anchor union and the severity MAX are
                // computed — and where the two remaining anchor faults reject the group instead of silently guessing.
                if (!TryStageMerge(all, survivorIndex, absorbed, out var staged, out var rejection))
                {
                    Reject(rejections, rejection!);
                    continue;
                }

                plan.Add(new PlannedMerge(group, survivorIndex, absorbed, staged.AnchorsJson, staged.Severity));
            }

            var wouldAbsorb = plan.Sum(p => p.Absorbed.Count);
            var rejected = rejections.Values.Sum();
            var planned = string.Join("; ", plan.Select(p => p.Group.Describe()));

            if (!resolution.Enabled)
            {
                // MEASURE MODE — and THE LINE THE OPERATOR READS TO DECIDE WHETHER TO FLIP THE SWITCH. Everything
                // above ran; nothing below does.
                //
                // be-c06 (P1-4): this line used to end "the findings are exactly what they would be without the merge
                // channel." THAT WAS FALSE, and it was false in the worst possible place — the evidence a human reads
                // before turning a model-driven DELETE on. The switch gates the APPLY step ONLY. The prompt and the
                // digest carry the merge contract in BOTH states (deliberately: it is what makes the forecast below
                // valid), so the model's OWN findings in an OFF build are not necessarily the findings a pre-b8 build
                // would have produced. Say so, here, every build.
                logger?.LogInformation(
                    "Book review (synthesis merge map): switch OFF (Ai:BookReview:SynthesisMergeMap=false) — synthesis " +
                    "proposed {Proposed} merge group(s) over {DigestIds} digest id(s), {Valid} valid (would have merged " +
                    "{WouldAbsorb} finding(s) away: [{Groups}]), rejected {Rejected} ({Reasons}). NOTHING was applied: " +
                    "no finding was deleted, no anchor unioned, no severity lifted. This is NOT the pre-b8 build, " +
                    "though: the synthesis prompt and digest still carry the merge contract (the W# id column, the " +
                    "260-char rationale cap, and the instruction to express a merge ONLY through `merges`), so the " +
                    "model's OWN findings, anchors and severities may differ from a pre-b8 build's. The switch gates " +
                    "the APPLY step, not the prompt.",
                    resolution.ProposedGroups, resolution.DigestIdCount, plan.Count, wouldAbsorb,
                    planned.Length == 0 ? "none" : planned, rejected, DescribeRejections(rejections));

                // P2-5 / be-f01: DigestSummary exists to separate a STRUCTURAL miss (the duplicate's copies never
                // shared a digest — nothing to tune) from a MODEL-RECALL miss (it saw both and did not name them) —
                // see the property's own xmldoc. That question belongs to THIS state: OFF is measure mode, and OFF
                // is what ships. Logging the id space only on the ON path (as this used to) meant the rollout's key
                // diagnostic was never emitted in the state anyone actually runs.
                logger?.LogDebug(
                    "Book review (synthesis merge map): the digest the model was shown = [{Digest}].",
                    resolution.DigestSummary);
                return all;
            }

            // ── SELECT the survivors. Pure bookkeeping over the plan's indexes: no entity is touched.
            var absorbedFlags = new bool[all.Count];
            var mergedFindings = 0;
            foreach (var (_, _, absorbedIndexes, _, _) in plan)
            {
                foreach (var index in absorbedIndexes)
                {
                    if (absorbedFlags[index])
                        continue; // defensive: Resolve already guarantees one group per finding
                    absorbedFlags[index] = true;
                    mergedFindings++;
                }
            }

            var kept = new List<NearDuplicateCollapser.Candidate>(all.Count);
            for (var i = 0; i < all.Count; i++)
            {
                if (!absorbedFlags[i])
                    kept.Add(all[i]);
            }

            // ── LOG, THEN WRITE — and that ORDER is the fail-safe (final-r01, completing be-c08 / P3-5).
            //
            // be-c08 STAGED the mutations so the catch below could honestly say "the findings exactly as they
            // arrived", and it made that true in NearDuplicateCollapser.Collapse by committing the staged severities
            // as the LAST statement in the try, AFTER its coverage log. THIS method did not follow through: the
            // per-group audit line sat INSIDE the write loop, one statement AFTER
            // `survivor.ChapterAnchorsJson = …; survivor.Severity = …`. A LOGGER CAN THROW — be-c08's own P3-5 test
            // for the collapser injects exactly that fault, on the grounds that it is "the strongest form of the
            // property" — so a fault while logging left the survivors it had already written half-merged (unioned
            // anchors, a lifted severity) inside the set the catch then handed back as UN-merged. Same todo, same
            // lesson, applied to only one of the two passes. STAGING ALONE IS NOT ENOUGH: the commit block must also
            // contain nothing that can fault, or the staging buys nothing.
            //
            // Everything that CAN throw (every log; the Snippet/LINQ that builds their arguments) now runs while the
            // findings are still untouched. The audit line reads only Dimension, Rationale and the group's counts —
            // NONE of which a merge mutates — so it says exactly the same thing before the write as after it.
            foreach (var (_, survivorIndex, absorbedIndexes, _, _) in plan)
            {
                var survivor = all[survivorIndex].Finding!; // staged: TryStageMerge rejected a null-entity survivor

                // THE AUDIT TRAIL FOR A DESTRUCTIVE OPERATION. This is the only place in the whole review where a
                // MODEL JUDGEMENT deletes a finding the user paid for, so the build says exactly WHAT it deleted and
                // what it kept. A count ("applied 1") cannot tell a correct merge from one that folded an unrelated
                // theme finding into a character one — and that difference is the entire risk of this switch.
                logger?.LogInformation(
                    "Book review (synthesis merge map): MERGED — KEPT [{KeepDim}] \"{Keep}\"; DELETED {Count}: {Deleted}.",
                    survivor.Dimension,
                    Snippet(survivor.Rationale),
                    absorbedIndexes.Count,
                    string.Join(" | ", absorbedIndexes
                        .Select(x => all[x].Finding)
                        .Where(f => f is not null)
                        .Select(f => $"[{f!.Dimension}] \"{Snippet(f.Rationale)}\"")));
            }

            // "Applied:" names the groups the PLAN actually applied, not Resolve's valid set — those differ whenever a
            // group's survivor was already deduped away (rejected above), and a log that names a group it did not
            // apply is the same false-claim class this whole fix pass exists to stamp out.
            logger?.LogInformation(
                "Book review (synthesis merge map): switch ON — synthesis proposed {Proposed} merge group(s) over " +
                "{DigestIds} digest id(s), applied {Applied} (merged {Absorbed} finding(s) away, {Kept} remain), " +
                "rejected {Rejected} ({Reasons}). Applied: [{Groups}].",
                resolution.ProposedGroups, resolution.DigestIdCount, plan.Count, mergedFindings, kept.Count,
                rejected, DescribeRejections(rejections),
                planned.Length == 0 ? "none" : planned);

            // The id space the model was SHOWN. A duplicate that survives is only a MODEL-RECALL miss if both copies
            // were in here; if they were not, it is a STRUCTURAL miss (the synthesis digest is built from the
            // findings accumulated BEFORE the pass runs, so the synthesis's OWN findings and the continuity reduce's
            // — both appended AFTER it — carry no id and can never be merged). Those two failures have completely
            // different fixes, and only this line can tell them apart.
            logger?.LogDebug(
                "Book review (synthesis merge map): the digest the model was shown = [{Digest}].",
                resolution.DigestSummary);

            // ── COMMIT. THE FIRST AND ONLY WRITES TO AN ENTITY IN THIS METHOD, and deliberately the LAST statements
            //    in the try (mirroring NearDuplicateCollapser.Collapse's staged-severity commit). Assignments ONLY —
            //    every value was computed by TryStageMerge before anything was touched — so this block cannot throw,
            //    and therefore every statement above it can fault with the findings EXACTLY as they arrived. That is
            //    what the catch below promises; now the code implements it.
            foreach (var (_, survivorIndex, _, anchorsJson, severity) in plan)
            {
                var survivor = all[survivorIndex].Finding!;

                // ANCHOR UNION, APPEND-ONLY (staged above). anchors[0] — the survivor's OWN first anchor, the one that
                // fed its dedup key — cannot move: nothing is ever inserted before it or removed, and the one case
                // where an append COULD create it (a survivor with no anchors at all) was rejected at staging time.
                // NULL here means the union added nothing, so the payload is left BYTE-IDENTICAL rather than
                // re-serialized for no reason.
                if (anchorsJson is not null)
                    survivor.ChapterAnchorsJson = anchorsJson;
                survivor.Severity = severity;
                // DedupKey / LegacyDedupKeyV1 / Rationale / EvidenceJson / SuggestedAction: deliberately UNTOUCHED.
            }

            return kept;
        }
        catch (Exception ex)
        {
            // FAIL-SAFE, and now TRUE (be-c08 / P3-5, completed by final-r01). The un-merged set really is what the
            // pipeline had BEFORE this pass — not merely the same LIST, but the same FINDINGS. Two things make that
            // literally true, and BOTH are needed: every mutation is STAGED in `plan` (nothing is computed against a
            // live entity), and the commit loop that applies it contains ONLY ASSIGNMENTS and is the LAST thing the
            // try does — so no statement that can fault (including every log line, which CAN throw) runs after the
            // first write. A fault therefore leaves no survivor half-merged (unioned anchors, a lifted severity)
            // behind it. Visible duplicates, never a lost finding, and never a state nobody reasoned about.
            // Surfaced, never swallowed (a fail-safe that logs nothing ships its failures invisibly).
            logger?.LogWarning(ex,
                "Book review (synthesis merge map): apply faulted; persisting the {Count} UN-merged finding(s) " +
                "(fail-safe: a visible duplicate beats a deleted finding).",
                all.Count);
            return all;
        }
    }

    /// <summary>One VALIDATED, FULLY STAGED merge: the group, the survivor's candidate index, the indexes it absorbs,
    /// and the survivor's NEW state. <paramref name="AnchorsJson"/> is NULL when the union added nothing (the payload
    /// is then left untouched rather than re-serialized). Computed before any entity is written, so the commit loop
    /// cannot fault (be-c08 / P3-5) and the OFF forecast counts exactly the groups an ON build would apply.</summary>
    private readonly record struct PlannedMerge(
        MergeGroup Group, int SurvivorIndex, List<int> Absorbed, string? AnchorsJson, int Severity);

    /// <summary>The survivor's post-merge state, computed without touching it.</summary>
    private readonly record struct StagedSurvivor(string? AnchorsJson, int Severity);

    /// <summary>
    /// Computes what a merge WOULD do to its survivor — the APPEND-ONLY anchor union and the group's MAX severity —
    /// without writing anything, and rejects the group on the two anchor faults the pre-fix code answered by GUESSING
    /// (be-c08). Returns false + a rejection reason instead.
    ///
    /// P3-7 — AN UNREADABLE ANCHOR PAYLOAD REJECTS THE GROUP, AND THE OLD FAIL-SAFE POINTED THE WRONG WAY. The pre-fix
    /// helper answered a JsonException with an EMPTY anchor list, on either side of the merge. On the SURVIVOR's side
    /// that is not a safe default, it is a silent identity theft: the union then appends the ABSORBED copy's anchors
    /// to nothing, and the survivor is rewritten carrying ONLY THE OTHER FINDING'S CHAPTERS — its anchors[0] MOVES, so
    /// it stops agreeing with the dedup key already stamped on it (b3 hashes the primary order), and the card now
    /// points the user at a chapter that finding never claimed. A payload we cannot READ is unknown scope, and unknown
    /// scope is exactly what the rest of this subsystem refuses to act on (BookFindingReconciler.ChapterOrdersOf → null →
    /// never deleted, never fuzzy-matched). So: reject the group, keep both findings, lose nothing. In practice this
    /// JSON was written by THIS build a moment ago (ProjectToEntity) and always parses — but "it cannot happen" is the
    /// premise under which the wrong-direction default was written in the first place.
    ///
    /// P3-8 — A BOOK-WIDE "keep" MAY NOT ABSORB AN ANCHORED MEMBER. See the class remarks (WHAT A MERGE DOES TO THE
    /// SURVIVOR): appending to an EMPTY anchor list CREATES anchors[0], which is the one way the append can move the
    /// dedup key's primary-order input out from under it.
    /// </summary>
    private static bool TryStageMerge(
        IReadOnlyList<NearDuplicateCollapser.Candidate> all,
        int survivorIndex,
        IReadOnlyList<int> absorbedIndexes,
        out StagedSurvivor staged,
        out string? rejection)
    {
        staged = default;
        rejection = null;

        var survivor = all[survivorIndex].Finding;
        if (survivor is null)
        {
            rejection = "keep-has-no-entity";
            return false;
        }

        if (!TryDeserializeAnchors(survivor.ChapterAnchorsJson, out var anchors))
        {
            rejection = "anchors-unreadable";
            return false;
        }

        // A survivor that anchors NOTHING has no anchors[0] to protect — an append would CREATE one (P3-8).
        var survivorIsBookWide = anchors.Count == 0;
        var originalCount = anchors.Count;
        var seen = new HashSet<(Guid ChapterId, int Order)>(anchors.Select(a => (a.ChapterId, a.Order)));
        var severity = survivor.Severity;

        foreach (var index in absorbedIndexes)
        {
            var absorbedFinding = all[index].Finding;
            if (absorbedFinding is null)
                continue;

            if (!TryDeserializeAnchors(absorbedFinding.ChapterAnchorsJson, out var absorbedAnchors))
            {
                rejection = "anchors-unreadable";
                return false;
            }

            if (survivorIsBookWide && absorbedAnchors.Count > 0)
            {
                rejection = "keep-is-book-wide-but-a-member-is-anchored";
                return false;
            }

            foreach (var anchor in absorbedAnchors)
            {
                if (seen.Add((anchor.ChapterId, anchor.Order)))
                    anchors.Add(anchor);
            }

            severity = Math.Max(severity, absorbedFinding.Severity);
        }

        staged = new StagedSurvivor(
            anchors.Count > originalCount ? JsonSerializer.Serialize(anchors, BookReviewService.SerializeOpts) : null,
            severity);
        return true;
    }

    /// <summary>
    /// THE ANCHOR-COMPATIBILITY FENCE (be-c07 / P2-4). May the model merge THESE findings, given the chapter each is
    /// anchored to? It is <see cref="NearDuplicateCollapser.MayFold"/> — the SAME predicate, the SAME single source —
    /// applied to EVERY PAIR of the group's members: at most ONE distinct real chapter may appear across the group,
    /// and a book-wide member (null order) is a wildcard that may join any group.
    ///
    /// WHY THERE HAS TO BE A FLOOR AT ALL. Every other collapse path in this subsystem has a deterministic fence.
    /// This one had ONLY the model — and the model has ALREADY BEEN MEASURED DESTROYING A DIMENSION with it. In the
    /// b8 live gate (gemma4:12b, book A63A6E02, switch ON) it kept a TONE finding about chapter 14 and deleted a
    /// CHARACTER finding about chapter 12 — two entirely different criticisms, merged because both mention דניאל.
    /// Those were the book's ONLY TWO character findings, so the whole דמויות dimension vanished from the score
    /// panel. The switch ships OFF because of that, but a kill-switch is a config value with an inviting name; it is
    /// not a floor. This is the floor, and it REJECTS THAT EXACT GROUP: the survivor's own anchors show the union
    /// carried chapter 12 onto a chapter-14 finding, so its members spanned two different real chapters.
    ///
    /// WHY THE *PRIMARY* ORDER AND NOT THE FULL ANCHOR SET — and this is the whole ballgame, measured. The obvious
    /// reading of "unless every member shares an anchor" is a set INTERSECTION, and on the real corpus it is UNSAFE:
    /// A63A6E02's SEVERITY-3 FACTUAL CONTRADICTION is anchored [12,13,14,15] and the severity-1 praise it scores
    /// 0.462 against is anchored [14,15]. THEY SHARE TWO ANCHORS. An intersection fence would happily let the model
    /// merge them and DELETE the single most valuable finding in the book — the exact finding b4b's MayFold is
    /// measured to be protecting. Comparing the PRIMARY (first resolved) order — 12 vs 14 — refuses. So the fence
    /// mirrors MayFold exactly, on the same value MayFold uses (<see cref="NearDuplicateCollapser.Candidate.PrimaryChapterOrder"/>,
    /// which is also the dedup key's primary-order input). Any "improvement" that widens this to an overlap test
    /// re-opens the deletion of that contradiction; the test suite pins it.
    ///
    /// PAIRWISE, NOT "SHARES WITH THE SURVIVOR" — the TRANSITIVITY TRAP, already learned once by the collapser. A
    /// book-wide member is a wildcard, so if the fence only compared each absorbed copy against the SURVIVOR, a
    /// book-wide survivor could BRIDGE two copies anchored to two different real chapters and merge them after all.
    /// Checking every pair closes it: the bridge itself is fine, but the two anchored members still face each other.
    ///
    /// THE NO-ANCHOR CALL — EQUALLY PERMISSIVE AS MayFold, deliberately, and it is a DIFFERENT call from the one
    /// be-c07 made for the persisted tier (<see cref="NearDuplicateCollapser.UserActedAnchorMismatchThreshold"/>,
    /// which is STRICTER than MayFold). The two paths look alike and are not:
    ///   • A book-wide finding NAMES NO CHAPTER, so it cannot "span two different real chapters" with anything. The
    ///     fence's predicate has nothing to bite on, and inventing a constraint the evidence does not support would
    ///     cost real recall for nothing.
    ///   • It is where the real duplicates ARE. At the b5 acceptance gate, ALL SEVEN surviving duplicate pairs on
    ///     A63A6E02 were a book-wide copy beside an anchored copy. Banning book-wide members would reject the group
    ///     shape that is most often CORRECT.
    ///   • The MEASURED false merge was NOT of this shape — it spanned two REAL chapters (14 and 12). The evidence
    ///     points the fence at anchored-vs-anchored, and that is where it is aimed.
    ///   • And the HARM ASYMMETRY that forced the strict call on the persisted tier is ABSENT here. There, a wrong
    ///     claim on a dismissed row SUPPRESSES the fresh finding — the user never sees it. Here, a wrong merge still
    ///     leaves the survivor's card VISIBLE and the anchors are UNIONED, so no chapter link is lost. Same-looking
    ///     wildcard, different blast radius, different bar. (This is not a licence: an absorbed copy IS deleted, and
    ///     that is precisely why the switch is OFF.)
    ///
    /// A BOOK-WIDE MEMBER MAY JOIN A GROUP; IT MAY NOT BE THE GROUP'S *keep* WHEN THE GROUP HAS AN ANCHORED MEMBER —
    /// this fence says nothing about that, and it is NOT this fence's job. It is enforced one step later, at staging
    /// (<see cref="TryStageMerge"/>, be-c08 / P3-8), because the reason is different: not "these are two different
    /// findings" but "unioning anchors ONTO a survivor that has none would MOVE its anchors[0] and break the dedup
    /// key it was already stamped with". Two rules, two failure modes, deliberately not merged into one.
    ///
    /// WHAT THE FENCE COSTS, STATED HONESTLY — it is not free, and b8's own acceptance cases pay it. The model can no
    /// longer merge the 0.875 science-and-poetics clause filed on ch1 AND ch15, nor fold the ch14 Daniel copy into
    /// the ch13 one. Those are REAL duplicates. They now stay as visible duplicate cards — which is exactly the
    /// KNOWN, ACCEPTED residual this feature already carries ("one observation emitted by two windows, each anchoring
    /// correctly to its OWN chapter"), whose agreed fix is DISPLAY-side grouping in the client, not a delete here.
    /// The mechanism is NOT neutered: a merge group whose members share a chapter scope still APPLIES, and that still
    /// buys what no threshold can — on the real corpus it closes the memory triple's third copy (0.375) and the
    /// same-chapter Daniel pair (0.444), BOTH below b4's 0.45 cut-off and BOTH unreachable by lowering it (0.44
    /// would also catch the 0.462 contradiction — the interleaving is pinned as a test). What the fence removes is
    /// precisely the class where the model is MEASURED to be dangerous, and it keeps the class where the
    /// deterministic passes are the ones falling short.
    /// </summary>
    private static bool AnchorsMayMerge(IReadOnlyList<NearDuplicateCollapser.Candidate> all, IReadOnlyList<int> members)
    {
        for (var i = 0; i < members.Count; i++)
        {
            for (var j = i + 1; j < members.Count; j++)
            {
                if (!NearDuplicateCollapser.MayFold(
                        all[members[i]].PrimaryChapterOrder, all[members[j]].PrimaryChapterOrder))
                    return false;
            }
        }

        return true;
    }

    // NIT-5: single-sourced. This class used to re-declare its own AnchorSerializeOpts / AnchorDeserializeOpts,
    // byte-identical to BookReviewService.SerializeOpts / DeserializeOpts — the same wire shape
    // (List&lt;FindingChapterAnchor&gt;, the same BookFinding.ChapterAnchorsJson field), maintained twice for no
    // reason. BookReviewService's are now `internal`; reuse them directly instead of drifting a third copy.

    /// <summary>
    /// Reads an entity's serialized anchor list, distinguishing the two things an empty result can mean — which the
    /// pre-fix helper conflated, and that conflation was the bug (be-c08 / P3-7, see <see cref="TryStageMerge"/>):
    ///   • absent / "[]" / "null" → TRUE with an EMPTY list. The finding legitimately anchors NO chapter (book-wide).
    ///   • a payload that does NOT parse → FALSE. The scope is UNKNOWN, and the caller REJECTS the whole group.
    /// </summary>
    private static bool TryDeserializeAnchors(string? json, out List<FindingChapterAnchor> anchors)
    {
        anchors = new List<FindingChapterAnchor>();
        if (string.IsNullOrWhiteSpace(json))
            return true; // a book-wide finding: no anchors were ever written. Not a fault.
        try
        {
            anchors = JsonSerializer.Deserialize<List<FindingChapterAnchor>>(json, BookReviewService.DeserializeOpts)
                      ?? new List<FindingChapterAnchor>();
            return true;
        }
        catch (JsonException)
        {
            anchors = new List<FindingChapterAnchor>();
            return false; // UNKNOWN scope → never guessed at, never acted on.
        }
    }

    /// <summary>Ids are compared case-insensitively and trimmed, because the model writes them back by hand and
    /// "w3" / " W3 " mean W3. Nothing else is normalized: an id that is not one we printed must NOT resolve.</summary>
    private static string NormalizeId(string? id) => (id ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>A rationale, trimmed to a loggable length. Enough to recognise the finding, not enough to flood.
    /// Internal (not private): be-f01 / P2-2 reuses this from <c>BookReviewService</c>'s fuzzy-fold audit log rather
    /// than declaring a third copy.</summary>
    internal static string Snippet(string? rationale)
    {
        var text = (rationale ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= 120 ? text : text.Substring(0, 120) + "…";
    }

    private static void Reject(IDictionary<string, int> rejections, string reason) =>
        rejections[reason] = rejections.TryGetValue(reason, out var n) ? n + 1 : 1;

    private static string DescribeRejections(IReadOnlyDictionary<string, int> rejections) =>
        rejections.Count == 0
            ? "none"
            : string.Join(", ", rejections.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>Reference identity for BookFindingItem keys: two DIFFERENT findings can carry identical prose (that
    /// is the whole bug this subsystem exists for), so the merge map must never conflate them by value.</summary>
    private sealed class ReferenceItemComparer : IEqualityComparer<BookFindingItem>
    {
        internal static readonly ReferenceItemComparer Instance = new();
        public bool Equals(BookFindingItem? x, BookFindingItem? y) => ReferenceEquals(x, y);
        public int GetHashCode(BookFindingItem obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
