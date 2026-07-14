using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Resolves MODEL-SUPPLIED chapter references (a finding's <see cref="FindingChapterAnchor"/> and
/// <see cref="FindingEvidence"/> chapterOrder) against the book's REAL chapters.
///
/// THE ORDER CONTRACT. A chapter's <c>Order</c> is <b>0-based</b> in the DB (every book runs 0..N-1) and the
/// assembled [BOOK_CONTEXT] shows the model that exact number in each chapter heading
/// (<c>## Chapter {order}: {title}</c>). The model is nonetheless UNTRUSTED on this field: it has been observed
/// INVENTING an order (a 1-based guess, or a number read out of the chapter TITLE, e.g. anchoring to "16"
/// because the single chapter is called "פרק 16"). Before this resolver existed, an invented order simply
/// missed the chapters-by-order lookup and the anchor was persisted with <c>ChapterId = Guid.Empty</c> — a
/// silent phantom: unusable for navigation, and (because its order is not a real chapter order of the book) it
/// could never be scoped out by the delete-vanished-open pass, so it accumulated on every rebuild. In a
/// MULTI-chapter book the same invented order lands on SOME real chapter instead, so the finding is silently
/// MIS-anchored. Both failures are unacceptable, so:
///
///   1. resolve by ORDER (exact match against a real chapter order);
///   2. on a miss, fall back to a TITLE match (normalized: trim, collapse whitespace, drop invisible
///      formatting marks, case-insensitive) — this recovers the "order guessed from the title" case and
///      CORRECTS the order to the chapter's real one;
///   3. still unresolvable → the reference is DROPPED. It is never persisted with an empty chapter id and
///      never guessed at.
///
/// THE VISIBILITY GATE (b7). Steps 1-2 answer "is this a real chapter of the BOOK?". That is NOT enough, because
/// no single review pass sees the whole book: the review is a MAP-REDUCE (per-window map, synthesis reduce over a
/// findings digest, continuity reduce over a skeleton slice). A model that is shown chapters 11-16 will still
/// happily anchor a finding to chapter 2 — and chapter 2 EXISTS, so steps 1-2 resolve it and the finding is
/// silently MIS-ANCHORED to a chapter the model never read. This was observed live on a 17-chapter book (a
/// finding whose prose names chapter 16 came back anchored to chapters 2 and 5). So a fourth question is asked
/// FIRST-CLASS, after resolution:
///
///   4. was the resolved chapter actually SHOWN to the pass that produced this finding? If the caller supplied a
///      shown-set and the resolved order is not in it, the reference is DROPPED as UNSEEN — an anchor to a
///      chapter the model never saw is a guess, not evidence. A null shown-set means "unconstrained" (the caller
///      did not declare visibility) and skips this gate; an EMPTY shown-set means the pass saw no chapter orders
///      at all, so every anchor is a guess. (Null and empty are deliberately DIFFERENT — see
///      <see cref="BookFindingItem.VisibleChapterOrders"/>.)
///
/// Every drop is COUNTED and recorded — separately for the two REASONS (see <see cref="UnresolvedAnchors"/> /
/// <see cref="UnseenAnchors"/> / <see cref="UnresolvedEvidenceOrders"/>) — so the caller can log a WARNING per
/// build: an unresolvable model reference that is silently swallowed into a default value ships failures
/// invisibly, which is exactly how the phantom-anchor bug survived undetected. The two reasons are kept apart on
/// purpose: "no such chapter" is a model that cannot count, "real but unseen" is a model that is HALLUCINATING
/// about content it was never given, which is a different (and more serious) quality signal.
///
/// A finding whose anchors are ALL dropped is still KEPT: its rationale can be perfectly valid book-wide
/// criticism. It simply becomes a NO-ANCHOR finding (an empty anchors list), which is an existing, supported
/// state in this pipeline — and, since b4b, the exact shape whose cross-bucket fold merges it back onto its
/// ANCHORED twin when another pass anchored the same finding correctly.
/// </summary>
internal sealed class ChapterAnchorResolver
{
    private readonly IReadOnlyDictionary<int, (Guid Id, string Title)> _byOrder;

    /// <summary>Normalized chapter title → the chapter's real Order. Contains ONLY titles that are UNIQUE across
    /// the book: an ambiguous title cannot identify a chapter, so it does not resolve (and the anchor is dropped
    /// rather than pinned to an arbitrary one of the candidates).</summary>
    private readonly Dictionary<string, int> _orderByUniqueTitle;

    private readonly List<(int Order, string Title)> _unresolvedAnchors = new();
    private readonly List<int> _unresolvedEvidenceOrders = new();

    // b7 — the VISIBILITY drops, kept SEPARATE from the "no such chapter" drops above. These anchors named a REAL
    // chapter of the book; what makes them bogus is that the pass that emitted them was never SHOWN that chapter.
    private readonly List<(int Order, string Title)> _unseenAnchors = new();
    private readonly List<int> _unseenEvidenceOrders = new();

    public ChapterAnchorResolver(IReadOnlyDictionary<int, (Guid Id, string Title)> chaptersByOrder)
    {
        _byOrder = chaptersByOrder ?? throw new ArgumentNullException(nameof(chaptersByOrder));
        // Materialized (not a cast of .Keys): IReadOnlyDictionary.Keys is only IEnumerable, so casting it to a
        // collection would depend on the concrete map type.
        RealChapterOrders = chaptersByOrder.Keys.ToArray();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var orders = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (order, ch) in chaptersByOrder)
        {
            var key = NormalizeTitle(ch.Title);
            if (key.Length == 0)
                continue; // an untitled chapter cannot be matched by title
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            // Lowest order wins the slot; ambiguous keys are pruned below anyway.
            if (!orders.TryGetValue(key, out var existing) || order < existing)
                orders[key] = order;
        }

        _orderByUniqueTitle = orders
            .Where(kv => counts[kv.Key] == 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>The book's REAL chapter orders (0-based).</summary>
    public IReadOnlyCollection<int> RealChapterOrders { get; }

    /// <summary>Anchors whose order matched a real chapter directly. Diagnostic only.</summary>
    public int ResolvedByOrderCount { get; private set; }

    /// <summary>Anchors whose ORDER was wrong but whose TITLE identified a real chapter (the order was
    /// CORRECTED). A non-zero count means the model is guessing orders — worth surfacing.</summary>
    public int ResolvedByTitleCount { get; private set; }

    /// <summary>Model anchors that matched NO real chapter by order or title and were DROPPED.</summary>
    public int DroppedAnchorCount => _unresolvedAnchors.Count;

    /// <summary>Model evidence items whose chapterOrder matched no real chapter and were DROPPED.</summary>
    public int DroppedEvidenceCount => _unresolvedEvidenceOrders.Count;

    /// <summary>b7: anchors that named a REAL chapter which the emitting pass was never SHOWN, and were DROPPED as
    /// UNSEEN. A non-zero count means the model is reasoning about chapters outside its context — the mis-anchoring
    /// that b1's real-chapter check cannot see (the order IS real).</summary>
    public int DroppedUnseenAnchorCount => _unseenAnchors.Count;

    /// <summary>b7: evidence items whose chapterOrder named a REAL but UNSEEN chapter, and were DROPPED. The model
    /// cannot have excerpted a chapter it was not shown.</summary>
    public int DroppedUnseenEvidenceCount => _unseenEvidenceOrders.Count;

    /// <summary>The (order, title) pairs that could not be resolved, in encounter order (duplicates included:
    /// the count is the number of DROPPED references, not of distinct phantoms).</summary>
    public IReadOnlyList<(int Order, string Title)> UnresolvedAnchors => _unresolvedAnchors;

    /// <summary>The evidence chapterOrders that could not be resolved, in encounter order.</summary>
    public IReadOnlyList<int> UnresolvedEvidenceOrders => _unresolvedEvidenceOrders;

    /// <summary>b7: the (order, title) pairs dropped because the chapter, though REAL, was not shown to the pass
    /// that emitted them, in encounter order.</summary>
    public IReadOnlyList<(int Order, string Title)> UnseenAnchors => _unseenAnchors;

    /// <summary>b7: the evidence chapterOrders dropped as real-but-unseen, in encounter order.</summary>
    public IReadOnlyList<int> UnseenEvidenceOrders => _unseenEvidenceOrders;

    /// <summary>True when this build dropped at least one model-supplied chapter reference (the caller logs a
    /// warning; see the type remarks on why a silent swallow is not acceptable here).</summary>
    public bool HasDrops => _unresolvedAnchors.Count > 0 || _unresolvedEvidenceOrders.Count > 0;

    /// <summary>b7: true when this build dropped at least one anchor/evidence reference to a REAL chapter that the
    /// emitting pass never saw. Tracked and logged SEPARATELY from <see cref="HasDrops"/> because it is a different
    /// failure: not "the model cannot count chapters" but "the model is writing about a chapter it never read".</summary>
    public bool HasUnseenDrops => _unseenAnchors.Count > 0 || _unseenEvidenceOrders.Count > 0;

    /// <summary>
    /// Resolves ONE model anchor. Returns false (and records the drop) when neither the order nor the title
    /// identifies a real chapter of this book — the caller must then DROP the anchor rather than persist a
    /// phantom order with <c>Guid.Empty</c>. On success <paramref name="resolved"/> always carries the REAL
    /// chapter's Id, Order and Title (so a title-resolved anchor has its invented order corrected).
    ///
    /// b7: also returns false when the anchor resolves to a real chapter that <paramref name="shownOrders"/> says
    /// the emitting pass was never SHOWN (a null shown-set skips that gate; see the type remarks). The visibility
    /// check runs AFTER resolution, deliberately: the model's raw order may be a mis-numbering of a chapter it DID
    /// see, and the title fallback corrects that — it is the RESOLVED chapter, the one the anchor would actually
    /// navigate to, whose visibility decides.
    /// </summary>
    public bool TryResolveAnchor(
        FindingChapterAnchor modelAnchor,
        out FindingChapterAnchor resolved,
        IReadOnlyCollection<int>? shownOrders = null) =>
        TryResolveCore(modelAnchor, shownOrders, record: true, out resolved);

    /// <summary>
    /// be-c02 (P1-1): a NON-RECORDING dry run of <see cref="TryResolveAnchor"/> — same answer, no counters, no drop
    /// lists, no effect on what the build LOGS. It exists so a caller that must know, BEFORE the persist step runs,
    /// which of a finding's anchors will actually SURVIVE resolution can ask THIS class instead of re-deriving the
    /// rule (see <see cref="DigestAnchorGate"/>: the reduce digests must not print — and thereby ALLOWLIST — an
    /// order the resolver is about to drop, or the prompt and the parser disagree).
    ///
    /// It shares <see cref="TryResolveCore"/> with the real path ON PURPOSE. A second, parallel copy of "is this
    /// anchor keepable?" is exactly the drift this subsystem has already paid for: the digest would say yes while
    /// the resolver said no, which is the incoherence b7's allowlist was written to remove.
    /// </summary>
    public bool TryPreviewAnchor(
        FindingChapterAnchor modelAnchor,
        out FindingChapterAnchor resolved,
        IReadOnlyCollection<int>? shownOrders = null) =>
        TryResolveCore(modelAnchor, shownOrders, record: false, out resolved);

    /// <summary>
    /// The ONE implementation of the anchor rule (order → title → shown? → drop), shared by the RECORDING resolve
    /// (<see cref="TryResolveAnchor"/>, which counts the drops the build warns about) and the NON-RECORDING preview
    /// (<see cref="TryPreviewAnchor"/>). <paramref name="record"/> gates ONLY the bookkeeping — never the decision —
    /// so a preview can never answer differently from the resolution it previews.
    /// </summary>
    private bool TryResolveCore(
        FindingChapterAnchor modelAnchor,
        IReadOnlyCollection<int>? shownOrders,
        bool record,
        out FindingChapterAnchor resolved)
    {
        var order = modelAnchor.Order;
        var title = modelAnchor.Title ?? string.Empty;

        // 1. By ORDER — the contract the prompt states.
        if (_byOrder.TryGetValue(order, out var byOrder))
        {
            if (!WasShown(order, shownOrders))
            {
                // b7: a REAL chapter the pass never saw. The order resolves, so nothing before b7 could object —
                // and that is precisely how a finding ended up pointing at the wrong chapter.
                if (record)
                    _unseenAnchors.Add((order, title));
                resolved = default!;
                return false;
            }

            if (record)
                ResolvedByOrderCount++;
            resolved = new FindingChapterAnchor
            {
                ChapterId = byOrder.Id,
                Order = order,
                Title = string.IsNullOrWhiteSpace(byOrder.Title) ? title : byOrder.Title
            };
            return true;
        }

        // 2. By TITLE — the model guessed the order (often FROM the title). Trust the title, correct the order.
        var key = NormalizeTitle(title);
        if (key.Length > 0 && _orderByUniqueTitle.TryGetValue(key, out var titleOrder)
            && _byOrder.TryGetValue(titleOrder, out var byTitle))
        {
            if (!WasShown(titleOrder, shownOrders))
            {
                // The title identifies a real chapter, but not one this pass was shown: still a guess.
                if (record)
                    _unseenAnchors.Add((titleOrder, title));
                resolved = default!;
                return false;
            }

            if (record)
                ResolvedByTitleCount++;
            resolved = new FindingChapterAnchor
            {
                ChapterId = byTitle.Id,
                Order = titleOrder, // CORRECTED to the chapter's real (0-based) order
                Title = string.IsNullOrWhiteSpace(byTitle.Title) ? title : byTitle.Title
            };
            return true;
        }

        // 3. Unresolvable → drop it, and remember it so the build logs a warning.
        if (record)
            _unresolvedAnchors.Add((order, title));
        resolved = default!;
        return false;
    }

    /// <summary>
    /// Resolves ONE model evidence item's chapterOrder. Evidence carries no title, so ORDER is the only handle:
    /// an order that is not a real chapter order of this book is a phantom nav target (in a multi-chapter book it
    /// would point the reader at a chapter the excerpt is not in), so the evidence item is DROPPED and counted.
    /// The finding's rationale — the substance the user reads — is unaffected.
    ///
    /// b7: an order naming a REAL chapter the emitting pass was never SHOWN is dropped too. Evidence is a quotation
    /// or paraphrase; the model cannot have taken one from a chapter it was not given, so such an item is fabricated
    /// by construction.
    /// </summary>
    public bool TryResolveEvidence(
        FindingEvidence modelEvidence,
        out FindingEvidence resolved,
        IReadOnlyCollection<int>? shownOrders = null)
    {
        if (_byOrder.TryGetValue(modelEvidence.ChapterOrder, out var ch))
        {
            if (!WasShown(modelEvidence.ChapterOrder, shownOrders))
            {
                _unseenEvidenceOrders.Add(modelEvidence.ChapterOrder);
                resolved = default!;
                return false;
            }

            resolved = new FindingEvidence
            {
                ChapterId = ch.Id,
                ChapterOrder = modelEvidence.ChapterOrder,
                Excerpt = modelEvidence.Excerpt
            };
            return true;
        }

        _unresolvedEvidenceOrders.Add(modelEvidence.ChapterOrder);
        resolved = default!;
        return false;
    }

    /// <summary>b7 visibility predicate. A NULL shown-set is UNCONSTRAINED (the producer declared no visibility, so
    /// every real chapter passes) — an EMPTY one is not: it says the pass saw no chapter orders at all, so nothing
    /// passes. Null and empty must never collapse into one another (the b3 sentinel lesson).</summary>
    private static bool WasShown(int order, IReadOnlyCollection<int>? shownOrders) =>
        shownOrders == null || shownOrders.Contains(order);

    /// <summary>
    /// Surfaces what the anchor resolution actually did this build. A dropped anchor means the model referenced a
    /// chapter that does not exist in this book, and a TITLE-resolved anchor means it supplied the WRONG order —
    /// both are model-quality signals that must never be swallowed. The pre-fix code silently defaulted an
    /// unresolvable anchor to <c>Guid.Empty</c> and logged nothing, which is precisely why the phantom anchors sat
    /// undetected in production until a user noticed duplicate findings. Warning (not error): the build is still
    /// correct and useful, the finding survives without the bogus anchor.
    ///
    /// b7 adds a SECOND, separate warning for the UNSEEN-chapter drops. It is deliberately its own line, and its own
    /// counter, because the two failures are not the same thing and the fix for each is different: a phantom order
    /// is a model that cannot copy a number, while an anchor onto a REAL chapter the pass was never shown is a model
    /// writing about content it never read. The latter has NO visible symptom without this log — the order resolves,
    /// the chapterId is a real Guid, the card navigates — it just navigates to the WRONG CHAPTER, which is how it
    /// stayed invisible while every other guard in this plan reported green.
    ///
    /// be-c09 (P2-7): moved here VERBATIM from BookReviewService (a pure file-size extraction). This is the
    /// observability for THIS class's own counters — every value it prints is a property of the resolver — so it is
    /// an instance method on the resolver rather than a free function that reaches into it. The caller passes its own
    /// <c>ILogger&lt;BookReviewService&gt;</c>, so the log CATEGORY is unchanged.
    /// </summary>
    public void LogResolution(
        ILogger logger,
        Guid bookId,
        string lang,
        int gatedFindings,
        int totalFindings)
    {
        // b7 GATE COVERAGE (Information, every build). "Zero unseen anchors were dropped" is only good news if the
        // gate was RUNNING; a gate that is silently unwired reports the identical zero. So the build states, every
        // time, how many findings actually carried a shown-set — a layer that cannot distinguish "clean" from "off"
        // is a layer that ships failures invisibly (the fail-safe-swallow lesson, and the exact reason the original
        // mis-anchoring survived three green rebuilds).
        logger.LogInformation(
            "Book review (anchors): book {BookId} ({Lang}) — visibility gate covered {Gated}/{Total} finding(s) " +
            "(each carrying the chapter orders its own pass was SHOWN). Resolved by order: {ByOrder}; corrected by " +
            "title: {ByTitle}; dropped as REAL-but-UNSEEN: {Unseen}; dropped as no-such-chapter: {Phantom}.",
            bookId, lang, gatedFindings, totalFindings,
            ResolvedByOrderCount, ResolvedByTitleCount,
            DroppedUnseenAnchorCount, DroppedAnchorCount);

        if (gatedFindings < totalFindings)
        {
            // Every production pass stamps a shown-set. If one did not, the gate silently degrades to pre-b7
            // behaviour for those findings (unconstrained), so say so rather than letting it pass unnoticed.
            logger.LogWarning(
                "Book review (anchors): book {BookId} ({Lang}) — {Ungated} of {Total} finding(s) carried NO shown-set, " +
                "so their anchors were NOT visibility-checked (a producing pass did not declare which chapters it " +
                "displayed). Those findings can still be mis-anchored to a real chapter the model never read.",
                bookId, lang, totalFindings - gatedFindings, totalFindings);
        }

        if (ResolvedByTitleCount > 0)
        {
            logger.LogWarning(
                "Book review (anchors): book {BookId} ({Lang}) — {Count} chapter anchor(s) carried an order that is " +
                "not a real chapter order but a TITLE that is; the order was CORRECTED from the title. The model is " +
                "guessing chapter orders (the prompt states they are 0-based and copied from [BOOK_CONTEXT]).",
                bookId, lang, ResolvedByTitleCount);
        }

        if (HasUnseenDrops)
        {
            logger.LogWarning(
                "Book review (anchors): book {BookId} ({Lang}) — DROPPED {UnseenAnchors} chapter anchor(s) and " +
                "{UnseenEvidence} evidence reference(s) pointing at chapters that ARE REAL but were NOT SHOWN to the " +
                "pass that produced the finding (the review is a map-reduce: a window sees only its own chapters, a " +
                "reduce pass only a digest). An anchor to a chapter the model never read is a guess, not evidence, so " +
                "it is not persisted and the finding becomes book-wide. This is MIS-ANCHORING, not a phantom: the " +
                "order resolves to a real chapter, so it would have navigated the user to the WRONG chapter. Unseen " +
                "anchors: {Unseen}. Unseen evidence orders: [{UnseenEvidenceOrders}].",
                bookId, lang,
                DroppedUnseenAnchorCount,
                DroppedUnseenEvidenceCount,
                DescribeUnseen(),
                string.Join(", ", UnseenEvidenceOrders.Distinct().OrderBy(o => o)));
        }

        if (!HasDrops)
            return;

        logger.LogWarning(
            "Book review (anchors): book {BookId} ({Lang}) — DROPPED {DroppedAnchors} chapter anchor(s) and " +
            "{DroppedEvidence} evidence reference(s) that match NO real chapter of this book (neither by order nor " +
            "by title); they are NOT persisted (an unresolvable anchor used to be written with an empty chapterId, " +
            "which was un-navigable and un-deletable). Real chapter orders: [{RealOrders}]. Unresolved anchors: " +
            "{Unresolved}. Unresolved evidence orders: [{UnresolvedEvidence}].",
            bookId, lang,
            DroppedAnchorCount,
            DroppedEvidenceCount,
            string.Join(", ", RealChapterOrders.OrderBy(o => o)),
            DescribeUnresolved(),
            string.Join(", ", UnresolvedEvidenceOrders.Distinct().OrderBy(o => o)));
    }

    /// <summary>A compact, log-safe rendering of the dropped references, capped so a pathological build cannot
    /// flood the log. Shape: <c>(order=16, title='פרק 16')</c>, comma separated.</summary>
    public string DescribeUnresolved(int max = 10) => Describe(_unresolvedAnchors, max);

    /// <summary>b7: the same compact rendering for the REAL-but-UNSEEN anchor drops.</summary>
    public string DescribeUnseen(int max = 10) => Describe(_unseenAnchors, max);

    private static string Describe(IReadOnlyList<(int Order, string Title)> drops, int max)
    {
        var parts = drops
            .Take(max)
            .Select(u => $"(order={u.Order}, title='{u.Title}')")
            .ToList();
        if (drops.Count > max)
            parts.Add($"... +{drops.Count - max} more");
        return parts.Count > 0 ? string.Join(", ", parts) : "(none)";
    }

    /// <summary>
    /// Title normalization for matching: Unicode NFC, invisible formatting marks (LRM/RLM/ZWJ and friends, which
    /// Hebrew titles pick up from Word/Syncfusion round-trips) removed, every whitespace run (including NBSP)
    /// collapsed to a single space, trimmed, and lower-cased invariantly. Deliberately conservative: it absorbs
    /// only cosmetic differences, so two genuinely different chapter titles never collide.
    /// </summary>
    internal static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var normalized = title.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                continue; // LRM / RLM / ZWJ / ZWNJ …: invisible, never part of the title's identity
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }

        return sb.ToString().ToLowerInvariant();
    }
}
