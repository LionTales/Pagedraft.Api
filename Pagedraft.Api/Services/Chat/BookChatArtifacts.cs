namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// The artifact TYPES phase B retrieves, in DROP ORDER (d1 section (2)'s priority order, listed
/// drop-first to drop-last). The enum's numeric order IS the trim order, so the trimmer sorts by it
/// instead of carrying a parallel table that could disagree with this list.
///
/// <para>WHERE THE GUIDES SIT. Guides are not book artifacts and have no member here; the trimmer
/// drops guides beyond <c>MinGuides</c> BETWEEN <see cref="Register"/> and <see cref="ChapterText"/>,
/// which is d1's placement: a mixed question's product half still needs SOME grounding, while a
/// finding's claim can be pointed at ("see your findings ledger") without its full text riding along.</para>
///
/// <para>Three placements d1 left silent, decided here and stated rather than buried: the CHARACTER
/// REGISTER survives the chapter briefs (it is what resolves a name to a person, so an answer about a
/// character that lost it is grounded in nothing); the analysis-HISTORY metadata drops second (it is
/// the only block grounding nothing about the manuscript's content); and an author-edited FLAT chapter
/// summary rides INSIDE its chapter-brief block rather than as a tier of its own, so it can never
/// survive the structured brief it is being compared against.</para>
///
/// <para>THE ONE EXCEPTION TO THAT LAST PLACEMENT is <see cref="AuthorSummary"/>, and it exists because
/// the pairing above has no meaning for a chapter whose structured brief is deliberately not carried at
/// all. See that member.</para>
/// </summary>
public enum BookArtifactKind
{
    /// <summary>Review findings. First to go: a finding's claim can be pointed at ("see your findings
    /// ledger") without its full text riding along.</summary>
    Finding = 1,

    /// <summary>Metadata of what analysis passes ran, when, on which chapter (d1's B-scope fence: never
    /// result bodies). Dropped early because it answers a narrow question ("what did I run?") that the
    /// statuses already answer approximately, and it is the only block that grounds nothing about the
    /// manuscript's content.</summary>
    History = 2,

    /// <summary>A selected chapter's structured brief (plus the author's own flat summary when they
    /// edited it). Never a chapter that triggered raw-text escalation - that one is protected.</summary>
    ChapterBrief = 3,

    /// <summary>The character register, suppression-filtered. Survives the briefs because it is what
    /// turns a NAME into a person: an answer about a character that lost it is grounded in nothing, and
    /// it costs a fraction of one brief.</summary>
    Register = 4,

    /// <summary>
    /// The author's OWN edited flat chapter summary, standing ALONE - emitted only for a chapter whose
    /// raw text rode along, which is exactly the case where its structured brief did not (g1 F-7).
    ///
    /// <para>WHY IT IS A TIER AND NOT A PARAGRAPH INSIDE <see cref="ChapterBrief"/>. d1 section (1) put
    /// the two surfaces in one block so a trim could never keep one and drop the other, leaving the model
    /// comparing a surface against something it can no longer see. That reason is about a PAIR. When a
    /// chapter escalates there is no pair: the structured brief is deliberately withheld because the full
    /// text is riding along instead, and the author's own words then had nothing to ride inside, so they
    /// silently vanished from the one question shape that asks for them - naming a chapter escalated it,
    /// which dropped its brief, which dropped the author's summary. Standing alone it is not a split
    /// pair, it is the only surviving member of one, and the artifact it sits beside is the raw text,
    /// which is a third thing answering a third question.</para>
    ///
    /// <para>It sits ABOVE the chapter briefs and the register (a few hundred characters of the author's
    /// own prose is worth more per token than either) and BELOW <see cref="ChapterText"/>, so under
    /// pressure the escalated text - the larger, more general grounding, and the artifact whose label
    /// licenses a chapter-scoped assertion - is the last of the pair to go. Both survive the guide trim,
    /// so the shape "which chapter did I write about, and what did I say" keeps both under any pressure
    /// the 40-chapter fixture measures.</para>
    /// </summary>
    AuthorSummary = 5,

    /// <summary>Escalated raw chapter text, whole or excerpted. PROTECTED once selected: escalation never
    /// evicts the artifact that triggered it, and that is enforced by this ordering rather than by a
    /// special case.</summary>
    ChapterText = 6,

    /// <summary>The book-level brief, the default backbone. Dropped only in the pathological last-resort
    /// case phase A already names <c>StillOverBudget</c>.</summary>
    BookBrief = 7,

    /// <summary>The build/staleness statuses. NEVER dropped while a bookId is present: they are the
    /// tutoring floor, and "the answer is the status plus the next action" is only possible while they
    /// are in the prompt.</summary>
    Status = 8
}

/// <summary>
/// One retrieved book artifact, already rendered and already labeled, with the citation reference(s) it
/// licenses.
///
/// <para>THE REFERENCES AND THE TEXT TRAVEL TOGETHER ON PURPOSE. Phase A's latent-budget finding warned
/// about the exact failure of trimming the grounding while keeping the citation; here the acceptable
/// citation set is DERIVED from the blocks that survived composition (see
/// <c>ProductChatBudget.Compose</c>), so a dropped block takes its references with it and the model
/// cannot cite an artifact it was never given.</para>
/// </summary>
/// <param name="Kind">Which tier this block trims in.</param>
/// <param name="References">The citation refs this block licenses, e.g. <c>chapter-brief:7</c> plus
/// <c>chapter-summary:7</c> when the author edited the flat summary too.</param>
/// <param name="Text">The rendered block, delimited and typed so citation-by-artifact is parseable.</param>
/// <param name="Rank">Selection strength within the kind. HIGHER survives longer; the trimmer drops the
/// lowest-ranked block of a tier first, exactly as phase A drops the lowest-ranked guide.</param>
public sealed record BookArtifactBlock(
    BookArtifactKind Kind,
    IReadOnlyList<string> References,
    string Text,
    double Rank);

/// <summary>
/// Machine-readable fault codes for the BOOK half of a chat turn, mirroring
/// <see cref="ProductChatFaults"/>'s shape (d1 section (6)).
///
/// <para>WHY TYPED FAULTS AND NOT JUST AN EMPTY RESULT. A nested catch that swallows to stay
/// non-throwing blinds the outer logger, which is a recorded lesson in this codebase rather than a
/// hypothetical. Each source is read inside its own try, and a failure records a code HERE instead of
/// vanishing, so a book half that came back thin is distinguishable from a book that genuinely has
/// nothing built yet - and those two want completely different answers ("I cannot see your book right
/// now" vs "your briefs are not built; build them and I can answer").</para>
///
/// <para>THE TWO HALVES FAIL INDEPENDENTLY. A fault here never suppresses the guides half of a mixed
/// question: one broken status lookup must not silence an otherwise-fine guide-grounded answer.</para>
/// </summary>
public static class BookChatFaults
{
    /// <summary>The book id did not resolve to a book this request may read.</summary>
    public const string BookUnavailable = "book-unavailable";

    /// <summary>The character register could not be read or deserialized.</summary>
    public const string RegisterUnreadable = "register-unreadable";

    /// <summary>The chapter/book briefs could not be composed.</summary>
    public const string BriefsUnreadable = "briefs-unreadable";

    /// <summary>One or more of the summary/review/style-baseline status lookups threw.</summary>
    public const string StatusUnavailable = "status-unavailable";

    /// <summary>The review findings could not be read.</summary>
    public const string FindingsUnreadable = "findings-unreadable";

    /// <summary>The selector decided to escalate to raw chapter text and the read failed. Distinct from
    /// "the chapter is empty", which is not a fault.</summary>
    public const string EscalationUnreadable = "escalation-unreadable";

    /// <summary>The analysis-history metadata read failed.</summary>
    public const string HistoryUnreadable = "history-unreadable";
}

/// <summary>
/// The chapter the author has OPEN on screen, as the request stated it (chatbot phase B, d2 section (1)).
///
/// <para>A TYPE RATHER THAN TWO LOOSE NULLABLES ON A SIGNATURE, and a REQUIRED parameter rather than an
/// optional one, for the reason d2 gives about the wire: "no chapter is open" and "nobody said" must stay
/// distinguishable, and an optional parameter defaulting to null quietly collapses them at every call
/// site that forgets. <see cref="None"/> is how a caller says the first one on purpose.</para>
/// </summary>
/// <param name="ChapterId">Authoritative for IDENTITY. Reconciled against freshly-read chapter rows to
/// find the chapter's CURRENT order, because the client's order is a snapshot a reorder invalidates.</param>
/// <param name="ChapterOrder">Authoritative for RESOLUTION once reconciled, and a fallback only when
/// <paramref name="ChapterId"/> does not resolve to a row of this book.</param>
public sealed record AmbientChapterContext(Guid? ChapterId, int? ChapterOrder)
{
    /// <summary>No chapter is open. Stated, not omitted.</summary>
    public static AmbientChapterContext None { get; } = new(null, null);

    public bool IsPresent => ChapterId.HasValue || ChapterOrder.HasValue;
}

/// <summary>
/// Everything the book half of one chat turn retrieved: the rendered blocks, what the question resolved
/// to, and what failed on the way.
/// </summary>
/// <param name="BookTitle">For the log line and for the prompt's context header. Never a fabrication
/// source - the model is told the title, not asked to infer anything from it.</param>
/// <param name="Blocks">Every retrieved block, in no particular order; <c>ProductChatBudget</c> orders
/// them. Empty is a legitimate result (a book with nothing built yet) and is NOT a fault.</param>
/// <param name="Keys">What the question resolved to, kept for the trim/selection log line.</param>
/// <param name="Faults">Typed fault codes, empty when every source read cleanly.</param>
/// <param name="EscalatedWholeChapters">Chapter orders whose text rode along WHOLE. The prompt's
/// grounding rule differs for these (a scoped "chapter 7 does not mention X" becomes sayable), which is
/// why the distinction is carried in the data and not only in the block label.</param>
/// <param name="EscalatedExcerptChapters">Chapter orders that degraded to labeled excerpts.</param>
public sealed record BookChatContext(
    string? BookTitle,
    IReadOnlyList<BookArtifactBlock> Blocks,
    BookArtifactSelector.BookQuestionKeys Keys,
    IReadOnlyList<string> Faults,
    IReadOnlyList<int> EscalatedWholeChapters,
    IReadOnlyList<int> EscalatedExcerptChapters)
{
    public static BookChatContext None { get; } = new(
        null, Array.Empty<BookArtifactBlock>(), BookArtifactSelector.BookQuestionKeys.Empty,
        Array.Empty<string>(), Array.Empty<int>(), Array.Empty<int>());

    /// <summary>
    /// True when NOTHING about the book could be read - not even the statuses, which are the cheapest
    /// and most robust source. This is the only shape that earns the honest "I cannot see your book right
    /// now" fail-safe; a book with faults but surviving statuses still gets a real, tutoring-shaped
    /// answer, because that is a better answer than a refusal.
    /// </summary>
    public bool IsBlind => Blocks.Count == 0 && Faults.Count > 0;

    /// <summary>Every citation reference the blocks in this context license, deduped.</summary>
    public IReadOnlyList<string> References => Blocks
        .SelectMany(b => b.References)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// The citation reference vocabulary phase B adds to phase A's guide ids (d1 section (3)). Built HERE
/// rather than spelled inline at each renderer, so the parser and the renderers cannot disagree about a
/// separator or a case.
///
/// <para>The client renders both families as chips, so the shape is deliberately flat and slug-like:
/// <c>&lt;type&gt;</c> or <c>&lt;type&gt;:&lt;key&gt;</c>, no spaces, no brackets.</para>
/// </summary>
public static class BookArtifactRefs
{
    public const string ChapterBriefPrefix = "chapter-brief";
    public const string ChapterSummaryPrefix = "chapter-summary";
    public const string ChapterTextPrefix = "chapter-text";
    public const string FindingPrefix = "finding";
    public const string StatusPrefix = "status";

    /// <summary>The register is a single artifact, so it has no key.</summary>
    public const string Register = "register";

    /// <summary>The analysis-history metadata block, likewise a single artifact.</summary>
    public const string History = "history";

    /// <summary>The book-level brief, likewise a single artifact.</summary>
    public const string BookBrief = "book-brief";

    public const string StatusSummary = StatusPrefix + ":summary";
    public const string StatusReview = StatusPrefix + ":review";
    public const string StatusStyleBaseline = StatusPrefix + ":style-baseline";

    public static string ChapterBrief(int order) => $"{ChapterBriefPrefix}:{order}";
    public static string ChapterSummary(int order) => $"{ChapterSummaryPrefix}:{order}";
    public static string ChapterText(int order) => $"{ChapterTextPrefix}:{order}";

    /// <summary>
    /// A finding reference. The key is the finding's Guid in the SAME "D" format the client's findings
    /// ledger routes on, so a chip can navigate without a lookup table.
    /// </summary>
    public static string Finding(Guid id) => $"{FindingPrefix}:{id:D}";

    /// <summary>
    /// True when <paramref name="token"/> has the SHAPE of a book-artifact reference. Shape only: whether
    /// the reference was actually licensed by this turn is decided by intersecting with the surviving
    /// blocks, exactly as phase A intersects a cited guide id with the surviving selection. A shape test
    /// that also tried to validate the key would be a second place for the vocabulary to drift.
    /// </summary>
    public static bool LooksLikeArtifactRef(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;

        if (Equals(token, Register) || Equals(token, History) || Equals(token, BookBrief)) return true;

        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1) return false;

        var prefix = token[..colon];
        return Equals(prefix, ChapterBriefPrefix)
            || Equals(prefix, ChapterSummaryPrefix)
            || Equals(prefix, ChapterTextPrefix)
            || Equals(prefix, FindingPrefix)
            || Equals(prefix, StatusPrefix);
    }

    private static bool Equals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
