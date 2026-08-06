using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// The REDUCE-PASS DIGESTS: how the two reduce passes (synthesis, continuity-final) are SHOWN the findings the
/// window MAP accumulated. Extracted verbatim from <see cref="BookReviewService"/> (be-c09 / P2-7 — a pure MOVE,
/// no behavior change).
///
/// WHY THESE THREE BELONG TOGETHER, AND WHY THEY ARE THE HIGHEST-STAKES RENDERING IN THE SUBSYSTEM. A reduce pass
/// reads NO chapter text — its whole view of the accumulated findings is the digest block built here. That makes
/// what these methods PRINT a load-bearing security boundary rather than a formatting concern, in three separate
/// ways, each of which was a shipped bug:
///   • the printed chapter column becomes the pass's anchor ALLOWLIST *and* the resolver's shown-set, so printing
///     an order the emitting pass never saw LAUNDERS a window's hallucination into the next pass's licence to
///     anchor there (be-c02 / P1-1 — fixed by rendering through <see cref="DigestAnchorGate"/>);
///   • a finding whose line the budget CAP drops is invisible to the reduce: no id, so it can never be merged,
///     and its chapter leaves the shown-set. Both digests therefore derive their shown-set from the EMITTED lines,
///     never from the accumulated set;
///   • the no-anchor token must not be "0" — orders are 0-BASED, so 0 is a real chapter (b3).
/// The two digests are also required to AGREE (be-f03 / P2-9): they print the same finding's anchors the same way,
/// or a chapter link the one shows is dropped as UNSEEN by the other. Keeping them in one file is what makes that
/// agreement checkable by reading, instead of by hoping.
///
/// Static by construction: the two budget-aware builders take the <see cref="BookContextAssembler"/> and the
/// <see cref="ILogger"/> they need as parameters (the service passes its own <c>_contextAssembler</c> and
/// <c>_logger</c>, so the log CATEGORY is unchanged).
/// </summary>
internal static class BookReviewDigests
{
    /// <summary>
    /// Max chars of a finding's rationale kept in a digest line (evidence/suggestedAction are dropped entirely).
    ///
    /// b8 RAISED THIS FROM 140 TO 260, and the reason is not cosmetic. The synthesis reduce is now asked to decide
    /// which accumulated findings are THE SAME FINDING (see <see cref="SynthesisMergeMap"/>), and it can only decide
    /// that from what the digest shows it. On the real 18-row corpus of book A63A6E02, EXACTLY ONE rationale exceeded
    /// 140 chars — the SEVERITY-3 FACTUAL CONTRADICTION, at 163 — so the old cap truncated precisely the one finding
    /// whose specificity is what STOPS it being merged into the sev-1 praise it shares a chapter range and half its
    /// vocabulary with (they score 0.462 on the collapser's metric, ABOVE a true duplicate at 0.455). Truncating the
    /// distinguishing tail of the most valuable finding in the book, in the input to the pass that decides whether to
    /// delete it, is as close to a designed-in false merge as this code gets.
    ///
    /// 260 covers that 163 with ~60% headroom and comfortably fits the "one or two sentences" the finding prompts
    /// ask for, so on real data NOTHING is truncated any more. It is not free: at ~2.2 Hebrew chars/token it roughly
    /// doubles the digest's worst-case token cost, which on a book with many findings could push the digest over its
    /// budget and start DROPPING lines. That is why <see cref="SynthesisRationaleDigestCharsCompact"/> exists — the
    /// digest falls back to the old 140 BEFORE it drops any line, so no book loses a digest line it had before.
    ///
    /// NOT GATED BY THE b8 KILL-SWITCH, deliberately (be-c06): <c>Ai:BookReview:SynthesisMergeMap</c> gates the APPLY
    /// step, not the model's input. Reverting this cap in the OFF state would re-truncate the most valuable finding in
    /// the book in the state that actually ships — the switch exists to withhold a DELETE, not to withhold context.
    /// </summary>
    internal const int SynthesisRationaleDigestChars = 260;

    /// <summary>The pre-b8 rationale cap, kept as the COMPACT fallback: when the full digest at
    /// <see cref="SynthesisRationaleDigestChars"/> does not fit the reduce budget, every line is re-rendered at this
    /// shorter cap before ANY line is dropped. Shortening every line degrades the merge decision a little; dropping a
    /// line removes a finding from the reduce's view entirely (it gets no id, so it can never be merged, and its
    /// chapter leaves the synthesis shown-set). Truncation is the cheaper loss, so it is tried first.</summary>
    internal const int SynthesisRationaleDigestCharsCompact = 140;

    /// <summary>What a digest line prints in the chapterOrder column for a finding that anchors NO chapter (a
    /// book-wide finding). b3: chapter orders are 0-BASED, so "0" means the FIRST chapter and can never double as
    /// "no chapter" — printing 0 here told the model a book-wide finding belonged to chapter one.</summary>
    internal const string NoAnchorDigestToken = "-";

    /// <summary>Max chars of a group finding's rationale kept in the FINAL-reduce digest line (evidence +
    /// suggestedAction dropped), mirroring <see cref="SynthesisRationaleDigestChars"/>. Terse so the union of
    /// every group's continuity findings fits the final reduce's model window.</summary>
    internal const int ContinuityRationaleDigestChars = 140;

    /// <summary>
    /// Builds the COMPACT [WINDOW_FINDINGS] digest the synthesis prompt reads: one terse line per accumulated
    /// finding — <c>dimension | chapterOrder | rationale[..140]</c> — with evidence and suggestedAction
    /// STRIPPED so the block stays small even for ~100 findings. chapterOrder is a comma-joined list of ALL of
    /// the finding's surviving chapter anchor orders (be-f03; previously only the first — see RenderDigestLines),
    /// or <see cref="NoAnchorDigestToken"/> when the finding anchors NO chapter — b3: it must NOT be
    /// printed as 0, which is a REAL chapter order (orders are 0-based) and would tell the model a book-wide
    /// finding belongs to the FIRST chapter. If the full digest's estimated tokens exceed the
    /// resolved book-context budget (minus the brief block already charged), the digest is CAPPED by dropping
    /// the LOWEST-severity findings first (highest severity retained) and the drop is LOGGED (no silent
    /// truncation). The lines keep their original accumulation order; only the over-budget tail is removed.
    ///
    /// b7: also RETURNS the set of chapter orders the EMITTED lines actually print — the synthesis pass's
    /// shown-set. It is derived from the emitted lines, not from the accumulated findings, because a finding whose
    /// line was dropped by the cap above shows the model NOTHING: its order is not in front of the model and an
    /// anchor to it would be a guess. Returning it from HERE (rather than recomputing it at the call site) is what
    /// keeps the printed digest and the enforced allowlist from drifting apart.
    ///
    /// b8: each EMITTED line now carries a stable BUILD-LOCAL ID as its FIRST column (<c>W1 | dimension | order |
    /// rationale</c>), and the method returns the id → finding map. That id is the only way the model can NAME a
    /// finding: without one, a reduce asked to "merge duplicates" can only describe a merge in prose, which is why
    /// it could never do anything but ADD. The map is built from the EMITTED lines for the same reason the shown-set
    /// is — an id the model was never given must not resolve — and the ids number the ACCUMULATION positions, so a
    /// capped-away line simply leaves a gap in the sequence rather than renumbering the ones that survived it.
    /// </summary>
    /// <param name="registerBlock">
    /// be-c02: the rendered <c>[BOOK_CHARACTERS]</c> block the synthesis prompt places between the brief and
    /// this digest (empty when the book has no register). It is RESERVED here alongside the brief block, for
    /// exactly the same reason the brief block is: all three share ONE model window, so a digest sized against
    /// only the brief would grow into the room the register now occupies and push the whole prompt past
    /// num_ctx - where Ollama truncates it silently and the synthesis comes back unparseable. The caller passes
    /// the block it will actually emit rather than re-rendering it here, so what is charged is provably what is
    /// sent.
    /// </param>
    internal static (string Digest, IReadOnlyCollection<int> ShownOrders, IReadOnlyDictionary<string, BookFindingItem> IdMap)
        BuildSynthesisDigest(
            IReadOnlyList<BookFindingItem> accumulatedFindings,
            string lang,
            string briefBlock,
            string registerBlock,
            DigestAnchorGate anchorGate,
            BookContextAssembler contextAssembler,
            ILogger logger)
    {
        var charsPerToken = BookContextAssembler.CharsPerTokenForLanguage(lang);
        var budget = contextAssembler.ResolveBudgetTokens(new[] { AiTaskType.BookReview });

        // Reserve the room the FULL brief block and the [BOOK_CHARACTERS] block already occupy in the prompt;
        // the digest must fit in what remains. Estimated SEPARATELY and summed, which rounds UP per block - the
        // conservative direction, since under-counting is what silently truncates.
        // Guard a pathological non-positive remainder to a small floor so at least a few lines survive.
        //
        // P3-10: `registerTokens` charges `registerBlock` alone, while the caller (RunSynthesisAsync) actually
        // EMITS `registerBlock + "\n\n"` when the block is non-empty - two characters this line does not charge
        // for. Left unequal deliberately rather than reshaped to match byte-for-byte: at the densest supported
        // script (CharsPerTokenDense = 2.0 chars/token) two uncharged characters can undercount by AT MOST one
        // whole token, against a `digestBudget` denominated in the thousands and already floored at 256 above -
        // not "absorbed to zero" by Math.Ceiling, but small enough that a one-token miss cannot push the digest
        // past num_ctx. (`briefBlock` has the identical two-character gap against its own "\n\n" separator and
        // is pre-existing, out of this fix's scope.)
        var briefTokens = BookContextAssembler.EstimateTokens(briefBlock, charsPerToken);
        var registerTokens = BookContextAssembler.EstimateTokens(registerBlock, charsPerToken);
        var digestBudget = Math.Max(256, budget - briefTokens - registerTokens);

        const string openMarker = "[WINDOW_FINDINGS]";
        const string closeMarker = "[/WINDOW_FINDINGS]";
        var markerTokens = BookContextAssembler.EstimateTokens(openMarker + "\n" + closeMarker, charsPerToken);

        // Render at the PREFERRED rationale cap; if the whole digest does not fit, re-render every line at the
        // COMPACT cap and try again. Only if THAT still does not fit does any line get dropped. Truncating a line
        // costs detail; dropping one removes the finding from the reduce's view entirely (no id → unmergeable).
        var lines = RenderDigestLines(accumulatedFindings, SynthesisRationaleDigestChars, anchorGate);
        var fullTokens = markerTokens + lines.Sum(l => BookContextAssembler.EstimateTokens(l.Line + "\n", charsPerToken));
        var compacted = false;
        if (fullTokens > digestBudget)
        {
            var compactLines = RenderDigestLines(accumulatedFindings, SynthesisRationaleDigestCharsCompact, anchorGate);
            var compactTokens = markerTokens + compactLines.Sum(l => BookContextAssembler.EstimateTokens(l.Line + "\n", charsPerToken));
            // P3-14: this line fires BEFORE it is known whether the compact render actually fits (that check runs
            // below, at `fullTokens <= digestBudget`) — it must not claim the outcome, only the attempt. If the
            // compact render still does not fit, the ELSE branch below drops lines by severity and logs THAT.
            logger.LogInformation(
                "Book review (synthesis): the findings digest at {Preferred} rationale chars (~{FullTokens} tokens) " +
                "exceeded the reduce budget ({DigestBudget} tokens after the {BriefTokens}-token brief); re-rendering " +
                "at the COMPACT {Compact} chars (~{CompactTokens} tokens) to try to fit without dropping any line " +
                "(a lower-severity-first drop follows only if this still does not fit).",
                SynthesisRationaleDigestChars, fullTokens, digestBudget, briefTokens,
                SynthesisRationaleDigestCharsCompact, compactTokens);
            lines = compactLines;
            fullTokens = compactTokens;
            compacted = true;
        }

        List<DigestLine> emitted;
        if (fullTokens <= digestBudget)
        {
            emitted = lines; // everything fits (at the preferred cap, or at the compact one)
        }
        else
        {
            // Still over budget even compacted: drop lowest-severity first. Rank by severity DESC (stable), keep as
            // many as fit the digest budget, then restore original order for the kept subset.
            var ranked = lines
                .Select((l, idx) => (l.Severity, l.Line, idx))
                .OrderByDescending(x => x.Severity)
                .ThenBy(x => x.idx)
                .ToList();

            var keepIndices = new HashSet<int>();
            var tokens = markerTokens;
            foreach (var r in ranked)
            {
                var lineTokens = BookContextAssembler.EstimateTokens(r.Line + "\n", charsPerToken);
                if (tokens + lineTokens > digestBudget)
                    continue; // this line does not fit; a later lower-severity line will not either, but keep scanning
                tokens += lineTokens;
                keepIndices.Add(r.idx);
            }

            emitted = lines.Where((_, idx) => keepIndices.Contains(idx)).ToList();

            var dropped = lines.Count - emitted.Count;
            logger.LogWarning(
                "Book review (synthesis): the accumulated-findings digest ({Total} findings, ~{FullTokens} tokens at " +
                "the {Cap}-char rationale cap) exceeded the reduce budget ({DigestBudget} tokens after the " +
                "{BriefTokens}-token brief); capped to {Kept} findings (dropped {Dropped}, lowest-severity first) so " +
                "the synthesis input fits the model window. A dropped finding is invisible to the reduce: it gets no " +
                "merge id and its chapter leaves the synthesis shown-set.",
                lines.Count,
                fullTokens,
                compacted ? SynthesisRationaleDigestCharsCompact : SynthesisRationaleDigestChars,
                digestBudget,
                briefTokens,
                emitted.Count,
                dropped);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(openMarker);
        foreach (var line in emitted)
            sb.AppendLine(line.Line);
        sb.Append(closeMarker);

        // b7: the shown-set = the orders the EMITTED lines print. A capped-away line shows the model nothing, so
        // its order is deliberately NOT in here.
        // be-c02: and, because RenderDigestLines now prints only GATED orders, neither is an order a window
        // hallucinated. This set feeds BOTH the synthesis allowlist and the synthesis shown-set, so anything in it
        // is something the model is TOLD it may anchor to AND that the resolver will then ACCEPT — the two must be
        // the same set, and it must contain only orders some pass genuinely read.
        var shown = emitted.SelectMany(e => e.Orders).Distinct().OrderBy(o => o).ToArray();

        // b8: the id map covers exactly the EMITTED lines — the ids the model was actually shown. Anything else it
        // names is an invention and SynthesisMergeMap.Resolve rejects the group that names it.
        var idMap = emitted.ToDictionary(e => e.Id, e => e.Finding, StringComparer.Ordinal);

        return (sb.ToString(), shown, idMap);
    }

    /// <summary>One rendered [WINDOW_FINDINGS] line: its build-local id, the finding it names (b8), its severity
    /// (the cap ranks on it), the printed text, and the chapter orders the line actually shows (b7's shown-set).</summary>
    private readonly record struct DigestLine(
        string Id, BookFindingItem Finding, int Severity, string Line, IReadOnlyList<int> Orders);

    /// <summary>
    /// Renders one digest line per accumulated finding, at the given rationale cap:
    /// <c>W3 | continuity | 14 | rationale…</c>. Ids are 1-based ACCUMULATION positions, assigned here and nowhere
    /// else, so they are stable across a re-render at a different cap (the compact fallback must not renumber the
    /// findings the model is about to be asked about).
    ///
    /// be-c02 (P1-1): the chapter column is rendered from the finding's GATED anchors (<see cref="DigestAnchorGate"/>),
    /// NOT from its raw model-supplied ones. The order(s) printed here become the synthesis pass's ALLOWLIST and its
    /// shown-set, so printing an order the emitting window never saw would launder that window's hallucination into
    /// the next pass's licence to anchor there — and the resolver, seeing a real order inside the synthesis shown-set,
    /// would then ACCEPT it. Every printed order is one the persist step will also accept; a finding whose anchors are
    /// all gated away prints <see cref="NoAnchorDigestToken"/> (it is about to become a book-wide finding), and its
    /// LINE is still emitted — a bad anchor must not cost the finding its place in the reduce.
    ///
    /// be-f03 (P2-9, digest parity): prints EVERY surviving order (comma-joined), not just the first — matching
    /// <see cref="BuildContinuityFindingsDigest"/> exactly. Before this fix a finding anchored to chapters [5, 12]
    /// printed only "5" here, so 12 never entered the synthesis allowlist/shown-set even though the SAME finding's
    /// continuity-final digest printed both; if the synthesis then re-emitted the finding with both anchors, 12 was
    /// dropped at persist time as an UNSEEN order — a real chapter link lost to a digest that under-reported its own
    /// finding. <see cref="DigestLine.Orders"/> now carries every printed order, so the shown-set union computed from
    /// it (see the caller) is automatically correct; nothing at the call site needed to change.
    /// </summary>
    private static List<DigestLine> RenderDigestLines(
        IReadOnlyList<BookFindingItem> accumulatedFindings, int rationaleChars, DigestAnchorGate anchorGate)
    {
        var lines = new List<DigestLine>(accumulatedFindings.Count);
        for (var i = 0; i < accumulatedFindings.Count; i++)
        {
            var f = accumulatedFindings[i];

            // be-c02: REAL chapter ∩ the finding's OWN shown-set. b3: a finding left with no anchor (either because
            // the model emitted none, or because every one it emitted is about to be dropped) prints the NO-ANCHOR
            // token, never "0" — 0 is chapter one. be-f03: ALL surviving orders are printed (comma-joined), not just
            // the first — see BuildContinuityFindingsDigest, which already did this.
            var visibleOrders = anchorGate.VisibleAnchorOrders(f);
            var order = visibleOrders.Count > 0
                ? string.Join(",", visibleOrders)
                : NoAnchorDigestToken;

            var rationale = (f.Rationale ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (rationale.Length > rationaleChars)
                rationale = rationale.Substring(0, rationaleChars);

            var dimension = BookReviewService.NormalizeDimension(f.Dimension);
            var id = SynthesisMergeMap.IdFor(i + 1);
            lines.Add(new DigestLine(id, f, f.Severity, $"{id} | {dimension} | {order} | {rationale}", visibleOrders));
        }

        return lines;
    }

    /// <summary>
    /// Builds the COMPACT digest of group-level continuity findings for the FINAL reduce, rendered as
    /// skeleton-shaped lines the continuity prompt reads: one line per finding —
    /// <c>continuity | &lt;chapterOrders&gt; | rationale[..140]</c> — wrapped in the
    /// <c>[CONTINUITY_SKELETON]…[/CONTINUITY_SKELETON]</c> markers so the same prompt/parse/mock switch applies.
    /// chapterOrders lists ALL of a finding's anchor orders (a continuity break spans chapters). Capped to the
    /// budget (minus the brief block) by dropping the LOWEST-severity findings first, LOGGED (no silent
    /// truncation), exactly like <see cref="BuildSynthesisDigest"/>.
    ///
    /// b7: RETURNS the orders the EMITTED lines print (the final reduce's shown-set — it sees no chapter content,
    /// only this digest), and FIXES a surviving instance of the b3 order-0 sentinel: a book-wide (no-anchor) group
    /// finding used to print its chapter column as literal <c>"0"</c>, which tells the model the finding belongs to
    /// the FIRST chapter — orders are 0-based, so 0 is a real chapter and can never double as "none". It now prints
    /// <see cref="NoAnchorDigestToken"/>, exactly as <see cref="BuildSynthesisDigest"/> already did. Left as "0",
    /// this hands the model a fabricated chapter-0 anchor to copy, which is the very failure b7 exists to stop.
    ///
    /// be-c02 (P1-1): the chapter column is rendered from each group finding's GATED anchors
    /// (<see cref="DigestAnchorGate"/>), not its raw ones. As with the synthesis digest, what this prints becomes
    /// the FINAL reduce's anchor allowlist AND its shown-set, so a group's mis-anchor (a real chapter that group's
    /// skeleton slice never listed) would otherwise be handed to the final reduce as a licence to anchor there — and
    /// the resolver would then have no grounds to object. A finding all of whose anchors are gated away still gets
    /// its LINE (only the orders are dropped); it prints the no-anchor token, which is exactly what it is about to
    /// become: a book-wide finding.
    /// </summary>
    internal static (string Digest, IReadOnlyCollection<int> ShownOrders) BuildContinuityFindingsDigest(
        IReadOnlyList<BookFindingItem> groupFindings,
        string lang,
        string briefBlock,
        DigestAnchorGate anchorGate,
        BookContextAssembler contextAssembler,
        ILogger logger)
    {
        var charsPerToken = BookContextAssembler.CharsPerTokenForLanguage(lang);
        var budget = contextAssembler.ResolveBudgetTokens(new[] { AiTaskType.BookReview });
        var briefTokens = BookContextAssembler.EstimateTokens(briefBlock, charsPerToken);
        var digestBudget = Math.Max(256, budget - briefTokens);

        var lines = new List<(int Severity, string Line, IReadOnlyList<int> Orders)>(groupFindings.Count);
        foreach (var f in groupFindings)
        {
            // be-c02: REAL chapter ∩ the emitting group's own shown-set. A continuity finding legitimately spans
            // several chapters, so ALL surviving orders are printed — and since be-f03 the SYNTHESIS digest prints
            // all of them too (RenderDigestLines used to narrow to the first; this comment still said so, which was
            // the very parity claim be-f03 landed, asserted backwards). The two digests render a finding's anchors
            // IDENTICALLY. They have to: a chapter one prints and the other does not is a chapter link dropped as
            // UNSEEN at persist time (P2-9). Keep them in step.
            var anchorOrders = anchorGate.VisibleAnchorOrders(f);
            var orders = anchorOrders.Count > 0
                ? string.Join(",", anchorOrders)
                : NoAnchorDigestToken; // b3/b7: NOT "0" — 0 is the first chapter, not "no chapter"
            var rationale = (f.Rationale ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (rationale.Length > ContinuityRationaleDigestChars)
                rationale = rationale.Substring(0, ContinuityRationaleDigestChars);
            lines.Add((f.Severity, $"continuity | {orders} | {rationale}", anchorOrders));
        }

        var openMarker = BookContextAssembler.ContinuitySkeletonOpen;
        var closeMarker = BookContextAssembler.ContinuitySkeletonClose;
        var markerTokens = BookContextAssembler.EstimateTokens(openMarker + "\n" + closeMarker, charsPerToken);

        var keptCount = lines.Count;
        var runningTokens = markerTokens;
        for (var i = 0; i < lines.Count; i++)
        {
            var lineTokens = BookContextAssembler.EstimateTokens(lines[i].Line + "\n", charsPerToken);
            if (runningTokens + lineTokens > digestBudget)
            {
                keptCount = i;
                break;
            }
            runningTokens += lineTokens;
        }

        List<(int Severity, string Line, IReadOnlyList<int> Orders)> emitted;
        if (keptCount >= lines.Count)
        {
            emitted = lines; // everything fits
        }
        else
        {
            var ranked = lines
                .Select((l, idx) => (l.Severity, l.Line, idx))
                .OrderByDescending(x => x.Severity)
                .ThenBy(x => x.idx)
                .ToList();

            var keepIndices = new HashSet<int>();
            var tokens = markerTokens;
            foreach (var r in ranked)
            {
                var lineTokens = BookContextAssembler.EstimateTokens(r.Line + "\n", charsPerToken);
                if (tokens + lineTokens > digestBudget)
                    continue;
                tokens += lineTokens;
                keepIndices.Add(r.idx);
            }

            emitted = lines.Where((_, idx) => keepIndices.Contains(idx)).ToList();
            logger.LogWarning(
                "Book review (continuity): the group-findings union digest ({Total} findings, ~{FullTokens} tokens) " +
                "exceeded the reduce budget ({DigestBudget} tokens after the {BriefTokens}-token brief); capped to " +
                "{Kept} findings (dropped {Dropped}, lowest-severity first) so the final reduce input fits the model window.",
                lines.Count, runningTokens, digestBudget, briefTokens, emitted.Count, lines.Count - emitted.Count);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(openMarker);
        foreach (var (_, line, _) in emitted)
            sb.AppendLine(line);
        sb.Append(closeMarker);

        // b7: the final reduce's shown-set = the orders the EMITTED lines print (a capped-away line shows nothing).
        var shown = emitted.SelectMany(e => e.Orders).Distinct().OrderBy(o => o).ToArray();
        return (sb.ToString(), shown);
    }
}
