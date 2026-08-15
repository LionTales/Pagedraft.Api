using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// Reads the book artifacts one chat turn is allowed to see (chatbot phase B, c1). The ONLY impure part
/// of phase B's retrieval: selection (<see cref="BookArtifactSelector"/>), excerpting
/// (<see cref="BookChatExcerpts"/>), rendering (<see cref="BookArtifactBlocks"/>) and budgeting
/// (<see cref="ProductChatBudget"/>) are all pure and pinned separately.
/// </summary>
public interface IBookChatContextReader
{
    /// <param name="ambient">The chapter the author has open on screen, or
    /// <see cref="AmbientChapterContext.None"/>. REQUIRED rather than optional: "no chapter is open" is a
    /// statement, and a defaulted parameter would let a call site make it by accident.</param>
    Task<BookChatContext> ReadAsync(
        Guid bookId, string question, string language, AmbientChapterContext ambient, CancellationToken ct);
}

/// <summary>
/// The database-facing half of phase B's retrieval.
///
/// <para>A THIRD CONSUMER OF READERS THAT ALREADY EXIST, not a new query layer. Chapter briefs come from
/// <c>BookSummaryService.ComposeChapterBriefsAsync</c> (which applies the SAME freshness gate the status
/// count applies, so chat can never quote a brief the dashboard calls stale), the book brief from
/// <c>ComposeBookBriefAsync</c>, findings from <c>BookReviewService.GetFindingsAsync</c>, the statuses
/// from the three services the stage spine reads, and the register through
/// <c>CharacterRegisterService.TryDeserialize</c> + <c>CharacterRegisterMerge.ForAnalysis</c> - the
/// zero-LLM read path the whole-book review uses, deliberately NOT
/// <c>AnalysisContextService.LoadCharacterRegisterAsync</c>, which can spend an extraction call and can
/// WRITE. A chat question must never mutate the book.</para>
///
/// <para>THE RAW-TEXT ESCALATION READS <c>Chapter.ContentText</c>, the plain-text column the analysis
/// paths already read. No second SFDT-to-text extraction is introduced: one already exists and this uses
/// its output.</para>
///
/// <para>EVERY SOURCE IS READ INSIDE ITS OWN TRY, AND A FAILURE RECORDS A TYPED FAULT (d1 section (6)).
/// A nested catch that swallows to stay non-throwing blinds the outer logger - a recorded lesson in this
/// codebase, not a hypothesis - so a source that throws leaves a <see cref="BookChatFaults"/> code
/// behind instead of silently contributing nothing. Cancellation is the one thing that is NOT caught:
/// a cancelled request has no user left to answer.</para>
/// </summary>
public sealed class BookChatContextReader : IBookChatContextReader
{
    /// <summary>
    /// How many chapter briefs a keyed question may pull. A cap on SELECTION, above the budget's cap on
    /// SIZE: at d1's measured ~700-800 tokens per formatted brief, six briefs already exceed the whole
    /// input budget, so a question matching thirty chapters must not compose thirty blocks and hand the
    /// trimmer a list it will spend its whole loop discarding.
    /// </summary>
    public const int MaxChapterBriefs = 6;

    /// <summary>How many findings a keyed question may pull, for the same reason.</summary>
    public const int MaxFindings = 8;

    /// <summary>How many analysis-history rows the metadata block may name.</summary>
    public const int MaxHistoryEntries = 8;

    /// <summary>
    /// How many chapters may escalate to raw text in one turn. The <see cref="BookChatExcerpts.EscalationBudgetTokens"/>
    /// slice is shared, so a question naming eight chapters would otherwise produce eight thin excerpts -
    /// each too thin to answer from, and all of them labeled as if they were readable. Two is the shape the
    /// escalation is for ("what happens between chapters 3 and 4").
    /// </summary>
    public const int MaxEscalatedChapters = 2;

    private readonly AppDbContext _db;
    private readonly BookSummaryService _summaries;
    private readonly BookReviewService _review;
    private readonly StyleBaselineService _styleBaseline;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<BookChatContextReader> _logger;

    public BookChatContextReader(
        AppDbContext db,
        BookSummaryService summaries,
        BookReviewService review,
        StyleBaselineService styleBaseline,
        IOptions<AiOptions> aiOptions,
        ILogger<BookChatContextReader> logger)
    {
        _db = db;
        _summaries = summaries;
        _review = review;
        _styleBaseline = styleBaseline;
        _aiOptions = aiOptions;
        _logger = logger;
    }

    public async Task<BookChatContext> ReadAsync(
        Guid bookId, string question, string language, AmbientChapterContext ambient, CancellationToken ct)
    {
        var faults = new List<string>();

        // The book itself, and the chapter shape the selector matches keys against. If this fails there
        // is nothing to select over, so it is the one read that short-circuits.
        Book? book;
        List<ChapterRow> chapterRows;
        try
        {
            book = await _db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookId, ct);
            if (book == null)
            {
                _logger.LogWarning(
                    "Book chat context REFUSED to retrieve ({Fault}): book {BookId} does not exist. The chat " +
                    "half answers from artifacts or says it cannot see the book; it never answers from priors.",
                    BookChatFaults.BookUnavailable, bookId);
                return BookChatContext.None with { Faults = new[] { BookChatFaults.BookUnavailable } };
            }

            chapterRows = await _db.Chapters
                .AsNoTracking()
                .Where(c => c.BookId == bookId)
                .OrderBy(c => c.Order)
                .Select(c => new ChapterRow(
                    c.Id, c.Order, c.Title, c.Scenes.OrderBy(s => s.Order).Select(s => s.Title).ToList()))
                .ToListAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Book chat context could not read book {BookId} or its chapters ({Fault}); the book half of " +
                "this turn degrades to the honest fail-safe while the guides half is unaffected.",
                bookId, BookChatFaults.BookUnavailable);
            return BookChatContext.None with { Faults = new[] { BookChatFaults.BookUnavailable } };
        }

        // ─── RETRIEVAL LANGUAGE (g1 F-2). The ANSWER language is untouched; only retrieval moves ─────
        //
        // Every book artifact - chapter briefs, findings and all three statuses - is keyed by the BOOK's
        // language, never by the language the ANSWER will be written in. Keying on the detected answer
        // language made an English question about a Hebrew book retrieve an EMPTY corpus AND a status
        // reading "0 of 8 briefs built / review BLOCKED", which the model then asserted to the author as a
        // fact about their own manuscript.
        //
        // AND THE BLANK-BOOK-LANGUAGE CASE FALLS BACK TO NOTHING, WHICH IS THE POINT. g1's diagnostic
        // patch fell back to the passed answer language when Book.Language was empty. That was examined
        // and REJECTED, because it re-opens the same defect through a second door: a book row with no
        // language, asked about in a language it has no rows in, retrieves nothing and reports "not
        // built" about a book that is built. There is no need to invent a fallback, because the product
        // already has ONE canonical answer to "which language slot are this book's artifacts in":
        // BaselineLanguageResolver.Normalize(book.Language), which is what BooksController's three status
        // endpoints AND their build POSTs key on (BooksController.ResolveBaselineLanguageAsync), and it
        // resolves a blank to "he" rather than to the caller's language. Using anything else would make
        // chat the only surface in the product that reads these rows under a different key than the one
        // every writer wrote them under - and "chat and the dashboard can never disagree about whether a
        // stage is behind" is the property the status block is built on.
        //
        // A consequence worth stating: with this key there is no longer a "found nothing because we asked
        // the wrong language" state to distinguish from "found nothing because nothing is built". They
        // collapse into one, and the surviving one is the honest one.
        var lang = BaselineLanguageResolver.Normalize(book.Language);

        // ─── The register, first: the selector resolves character names THROUGH it ──────────────
        var register = await ReadRegisterAsync(bookId, faults, ct);

        var ambientOrder = ResolveAmbientOrder(bookId, ambient, chapterRows);

        var keys = BookArtifactSelector.Select(
            question,
            chapterRows.Select(c => new BookArtifactSelector.ChapterRef(c.Order, c.Title, c.SceneTitles)).ToList(),
            register,
            ambientOrder);

        var blocks = new List<BookArtifactBlock>();

        // ─── Statuses: ALWAYS, and never dropped. The tutoring backbone ─────────────────────────
        var (summaryStatus, reviewStatus, baselineStatus) = await ReadStatusesAsync(bookId, lang, faults, ct);
        blocks.Add(BookArtifactBlocks.Statuses(summaryStatus, reviewStatus, baselineStatus));

        // ─── Raw-text escalation runs BEFORE the brief selection, and the order is load-bearing ──
        //
        // The brief selection has to exclude a chapter whose raw text is riding along, and "is riding
        // along" is something only the escalation READ can answer. Ranking first meant excluding on the
        // INTENT to escalate, so a chapter whose text was unreadable or empty lost its brief and gained
        // no text: the question named a chapter and the prompt then carried nothing about it at all,
        // while the citation vocabulary still advertised a chapter-brief ref that was never carried.
        // `language`, NOT `lang`, on every chapter-scoped producer below. THE TWO ARE DIFFERENT ANSWERS TO
        // DIFFERENT QUESTIONS (final-r05): `lang` is the retrieval key, and every read in this method uses
        // it; `language` is what the reader will be reading, and the ONE string these blocks carry that is
        // meant to reach them verbatim - the author-facing chapter name - is written in it. Keying that
        // line on `lang` would put a Hebrew frame in an English answer on exactly the cross-language turn
        // the F-2 fix above exists to serve. See BookArtifactBlocks.AuthorFacingChapterName.
        var (textBlocks, whole, excerpted) =
            await EscalateAsync(bookId, chapterRows, keys, question, language, faults, ct);
        blocks.AddRange(textBlocks);

        var carriedRawText = new HashSet<int>(whole.Concat(excerpted));

        // ─── Briefs: the book-level backbone plus the keyed chapter selection ───────────────────
        var chapterBriefs = Array.Empty<ChapterBrief>() as IReadOnlyList<ChapterBrief>;
        try
        {
            chapterBriefs = await _summaries.ComposeChapterBriefsAsync(bookId, lang, ct);
            var bookBrief = await _summaries.ComposeBookBriefAsync(bookId, chapterBriefs, ct);

            var briefBlock = BookArtifactBlocks.BookBrief(
                bookBrief, book.Title, _aiOptions.Value.BookReviewWindowBriefMaxTokens);
            if (briefBlock != null) blocks.Add(briefBlock);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            faults.Add(BookChatFaults.BriefsUnreadable);
            _logger.LogError(ex,
                "Book chat context could not compose the briefs for book {BookId} ({Fault}). The answer will " +
                "be built from whatever else survived, and the statuses still say what state the briefs are in.",
                bookId, BookChatFaults.BriefsUnreadable);
        }

        var authorSummaries = await ReadAuthorEditedSummariesAsync(bookId, lang, faults, ct);

        foreach (var (brief, rank) in RankChapterBriefs(chapterBriefs, keys, carriedRawText))
        {
            authorSummaries.TryGetValue(brief.Order, out var authorSummary);
            blocks.Add(BookArtifactBlocks.ChapterBrief(language, brief, authorSummary, rank));
        }

        // ─── The author's own summary for a chapter whose TEXT rode along (g1 F-7) ──────────────
        //
        // Its structured brief was withheld above, and d1 section (1) had the author's own edited summary
        // riding INSIDE that brief - so naming a chapter escalated it, which dropped its brief, which
        // dropped the author's own words, and the question that literally asks "what did I write in my
        // summary" was the one question that could never be answered. The escalated raw text and the
        // author's summary are different artifacts answering different questions, so the summary rides on
        // its own here rather than the exclusion being deleted; the structured brief of an escalated
        // chapter is still withheld, which is what that exclusion was actually protecting.
        foreach (var order in carriedRawText.OrderBy(o => o))
        {
            if (!authorSummaries.TryGetValue(order, out var authorSummary)) continue;

            // The chapter's own title, so this block can carry the same author-facing name line as the
            // other two chapter-scoped blocks (final-r02). Null for an order with no row is impossible
            // here - carriedRawText comes from the escalation over these very rows - but the renderer
            // handles a missing title by naming the chapter with the author's number alone.
            var title = chapterRows.FirstOrDefault(c => c.Order == order)?.Title;

            // The same rank the escalated text carries, so the pair sorts together in the prompt.
            var summaryBlock = BookArtifactBlocks.AuthorSummary(
                language, order, title, authorSummary, rank: 100 - order);
            if (summaryBlock != null) blocks.Add(summaryBlock);
        }

        // ─── Register block ─────────────────────────────────────────────────────────────────────
        var registerBlock = BookArtifactBlocks.Register(register);
        if (registerBlock != null) blocks.Add(registerBlock);

        // ─── Findings ───────────────────────────────────────────────────────────────────────────
        foreach (var (finding, rank) in await RankFindingsAsync(bookId, lang, keys, faults, ct))
        {
            blocks.Add(BookArtifactBlocks.Finding(finding, rank));
        }

        // ─── Editing history (metadata only) ────────────────────────────────────────────────────
        var historyBlock = BookArtifactBlocks.History(await ReadAnalysisHistoryAsync(bookId, chapterRows, faults, ct));
        if (historyBlock != null) blocks.Add(historyBlock);

        // RETRIEVAL LANGUAGE AND ANSWER LANGUAGE ARE LOGGED SEPARATELY, because they are allowed to
        // differ (that is the whole of the F-2 fix) and a single "language" field would make the one
        // state this defect lives in - a cross-language turn - indistinguishable from the ordinary one.
        // THE AMBIENT CHAPTER AND THE TIER THAT RESOLVED IT ARE LOGGED BESIDE THE SELECTION, because "the
        // chapter was open and the question still selected nothing" and "no chapter was open" are the two
        // states this feature exists to tell apart, and a log that only prints the selected orders makes
        // them look identical - which is exactly how the defect that prompted this work read in the log.
        _logger.LogInformation(
            "Book chat context for book {BookId} retrieved in {RetrievalLanguage} (the BOOK's language; the " +
            "answer will be written in {AnswerLanguage}) selected chapters [{ChapterOrders}], characters " +
            "[{CharacterCount}], dimensions [{Dimensions}]; ambient chapter {AmbientOrder} resolved as " +
            "{AmbientMatch} (used: {AmbientUsed}), clarify: {NeedsClarification}; escalated whole " +
            "[{WholeChapters}] and excerpted [{ExcerptChapters}]; {BlockCount} block(s), fault(s): [{Faults}].",
            bookId, lang, language, string.Join(", ", keys.ChapterOrders), keys.CharacterNames.Count,
            string.Join(", ", keys.Dimensions),
            ambientOrder?.ToString() ?? "none", keys.AmbientMatch, keys.AmbientChapterOrder?.ToString() ?? "none",
            keys.NeedsChapterClarification,
            string.Join(", ", whole), string.Join(", ", excerpted),
            blocks.Count, string.Join(", ", faults));

        return new BookChatContext(book.Title, blocks, keys, faults, whole, excerpted);
    }

    /// <summary>
    /// Turns the request's ambient chapter into the ONE value the selector consumes: a current
    /// <c>Chapter.Order</c> of THIS book, or null (d2 section (1b)).
    ///
    /// <para>THE ID IS LOOKED UP AGAINST THE ROWS THAT WERE JUST READ, AND ITS ROW'S ORDER WINS. The
    /// client's order number is a snapshot taken when the editor loaded the book; a reorder since then
    /// makes it point at a different chapter, and answering confidently about the wrong chapter of the
    /// author's own manuscript is the failure this whole feature has to avoid. The id is durable, so it
    /// decides WHICH chapter and the freshly-read row decides its CURRENT order.</para>
    ///
    /// <para>THE SENT ORDER IS A FALLBACK ONLY, for a client too old to send an id or a chapter deleted
    /// since the editor loaded it, and it is range-checked against the same rows. Both the reorder case
    /// and the unresolved-id case are LOGGED rather than silently absorbed: they are the two states in
    /// which the ambient key was almost, but not quite, right, and a silent almost is what a wrong-chapter
    /// answer looks like from the outside.</para>
    /// </summary>
    private int? ResolveAmbientOrder(
        Guid bookId, AmbientChapterContext ambient, IReadOnlyList<ChapterRow> chapterRows)
    {
        if (!ambient.IsPresent) return null;

        if (ambient.ChapterId is Guid id)
        {
            var row = chapterRows.FirstOrDefault(c => c.Id == id);
            if (row != null)
            {
                if (ambient.ChapterOrder is int sent && sent != row.Order)
                {
                    _logger.LogInformation(
                        "Book chat ambient chapter {ChapterId} of book {BookId} is at order {CurrentOrder}, " +
                        "not the order {SentOrder} the client sent: the chapters were reordered since the " +
                        "editor loaded them. The freshly-read order wins, because everything downstream is " +
                        "order-keyed and answering about the chapter that now holds the old number would be " +
                        "an answer about the wrong chapter.",
                        id, bookId, row.Order, sent);
                }

                return row.Order;
            }

            _logger.LogWarning(
                "Book chat ambient chapter {ChapterId} is not a chapter of book {BookId} (deleted since the " +
                "editor loaded it, or a stale client). Falling back to the sent order {SentOrder}, which is " +
                "range-checked against this book's chapters; a null there simply means nothing is ambient " +
                "for this turn.",
                id, bookId, ambient.ChapterOrder);
        }

        if (ambient.ChapterOrder is int fallback && chapterRows.Any(c => c.Order == fallback))
            return fallback;

        return null;
    }

    // ─── Sources ────────────────────────────────────────────────────────────────────────────────

    private async Task<CharacterRegister?> ReadRegisterAsync(
        Guid bookId, List<string> faults, CancellationToken ct)
    {
        try
        {
            var json = await _db.BookBibles
                .AsNoTracking()
                .Where(b => b.BookId == bookId)
                .Select(b => b.CharacterRegisterJson)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(json)) return null;   // no register built yet: not a fault

            if (!CharacterRegisterService.TryDeserialize(json, out var stored, out var fault) || stored is null)
            {
                faults.Add(BookChatFaults.RegisterUnreadable);
                _logger.LogWarning(fault,
                    "Book chat context could not deserialize the character register for book {BookId} " +
                    "({Fault}); character names in this turn resolve from the chapter briefs alone, and no " +
                    "register citation is offered.",
                    bookId, BookChatFaults.RegisterUnreadable);
                return null;
            }

            // ForAnalysis, never the raw register: a suppressed entry (the author said "not a character")
            // must not reach the prompt at all, let alone ground an answer.
            return CharacterRegisterMerge.ForAnalysis(stored);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            faults.Add(BookChatFaults.RegisterUnreadable);
            _logger.LogError(ex,
                "Book chat context could not read the character register for book {BookId} ({Fault}).",
                bookId, BookChatFaults.RegisterUnreadable);
            return null;
        }
    }

    private async Task<(BookSummaryStatus?, BookReviewStatus?, BookStyleBaselineStatus?)> ReadStatusesAsync(
        Guid bookId, string language, List<string> faults, CancellationToken ct)
    {
        // Each status is read on its own so ONE failing lookup does not blank the other two. A partially
        // readable status block is still the tutoring backbone; a wholly missing one is not.
        var summary = await TryStatusAsync(() => _summaries.GetStatusAsync(bookId, language, ct), "summary", bookId, faults);
        var review = await TryStatusAsync(() => _review.GetStatusAsync(bookId, language, ct), "review", bookId, faults);
        var baseline = await TryStatusAsync(() => _styleBaseline.GetStatusAsync(bookId, language, ct), "style-baseline", bookId, faults);
        return (summary, review, baseline);
    }

    private async Task<T?> TryStatusAsync<T>(
        Func<Task<T>> read, string which, Guid bookId, List<string> faults) where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!faults.Contains(BookChatFaults.StatusUnavailable)) faults.Add(BookChatFaults.StatusUnavailable);
            _logger.LogError(ex,
                "Book chat context could not read the {Which} status for book {BookId} ({Fault}). That status " +
                "line renders as unreadable rather than as a state, because a status the assistant states " +
                "WRONGLY is counted as fabrication, not as a degraded answer.",
                which, bookId, BookChatFaults.StatusUnavailable);
            return null;
        }
    }

    /// <summary>
    /// The flat <c>SummaryText</c> of chapters the AUTHOR edited, keyed by chapter order. Only
    /// user-edited rows: the machine-written flat summary is a THIRD surface with its own freshness stamp
    /// and no author behind it, so quoting it would add tokens and a drift risk without adding a claim
    /// anyone stands behind.
    /// </summary>
    private async Task<Dictionary<int, string>> ReadAuthorEditedSummariesAsync(
        Guid bookId, string language, List<string> faults, CancellationToken ct)
    {
        try
        {
            var rows = await _db.ChunkSummaries
                .AsNoTracking()
                .Where(cs => cs.BookId == bookId && cs.SummaryUserEdited && cs.Language == language)
                .Join(_db.Chapters.AsNoTracking(), cs => cs.ChapterId, c => c.Id,
                      (cs, c) => new { c.Order, cs.SummaryText })
                .ToListAsync(ct);

            return rows
                .Where(r => !string.IsNullOrWhiteSpace(r.SummaryText))
                .GroupBy(r => r.Order)
                .ToDictionary(g => g.Key, g => g.First().SummaryText);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            faults.Add(BookChatFaults.BriefsUnreadable);
            _logger.LogError(ex,
                "Book chat context could not read the author-edited chapter summaries for book {BookId} " +
                "({Fault}); the structured briefs still ground the answer.",
                bookId, BookChatFaults.BriefsUnreadable);
            return new Dictionary<int, string>();
        }
    }

    private async Task<IReadOnlyList<(BookFinding Finding, double Rank)>> RankFindingsAsync(
        Guid bookId, string language, BookArtifactSelector.BookQuestionKeys keys,
        List<string> faults, CancellationToken ct)
    {
        try
        {
            var findings = (await _review.GetFindingsAsync(bookId, language, ct)).Findings;

            return findings
                .Select(f => (Finding: f, Rank: FindingRank(f, keys)))
                .OrderByDescending(x => x.Rank)
                .ThenByDescending(x => x.Finding.Severity)
                .ThenBy(x => x.Finding.Dimension, StringComparer.Ordinal)
                .ThenBy(x => x.Finding.Id)
                .Take(MaxFindings)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            faults.Add(BookChatFaults.FindingsUnreadable);
            _logger.LogError(ex,
                "Book chat context could not read the review findings for book {BookId} ({Fault}); the " +
                "review STATUS still says whether a review exists, so the answer can still be honest about it.",
                bookId, BookChatFaults.FindingsUnreadable);
            return Array.Empty<(BookFinding, double)>();
        }
    }

    private async Task<IReadOnlyList<(string Type, int? ChapterOrder, DateTimeOffset At)>> ReadAnalysisHistoryAsync(
        Guid bookId, IReadOnlyList<ChapterRow> chapters, List<string> faults, CancellationToken ct)
    {
        try
        {
            // METADATA ONLY (d1's B-scope fence): no ResultText, no StructuredResult, no Suggestions.
            var rows = await _db.AnalysisResults
                .AsNoTracking()
                .Where(r => r.BookId == bookId && r.Status == AnalysisStatus.Active)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new { r.AnalysisType, r.ChapterId, r.CreatedAt })
                .Take(MaxHistoryEntries)
                .ToListAsync(ct);

            var orderById = chapters.ToDictionary(c => c.Id, c => c.Order);

            return rows
                .Select(r => (
                    Type: r.AnalysisType.ToString(),
                    ChapterOrder: r.ChapterId.HasValue && orderById.TryGetValue(r.ChapterId.Value, out var order)
                        ? order
                        : (int?)null,
                    At: r.CreatedAt))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            faults.Add(BookChatFaults.HistoryUnreadable);
            _logger.LogError(ex,
                "Book chat context could not read the analysis history for book {BookId} ({Fault}).",
                bookId, BookChatFaults.HistoryUnreadable);
            return Array.Empty<(string, int?, DateTimeOffset)>();
        }
    }

    /// <summary>
    /// Reads the raw text of every chapter the selector escalated, sharing the ONE
    /// <see cref="BookChatExcerpts.EscalationBudgetTokens"/> slice between them in chapter order.
    /// </summary>
    /// <param name="language">The ANSWER's language, threaded through for the author-facing name line the
    /// chapter-text block carries (final-r05). NOT the retrieval language: nothing this method READS is
    /// keyed by it. See <c>BookArtifactBlocks.AuthorFacingChapterName</c> for the argument.</param>
    private async Task<(List<BookArtifactBlock> Blocks, List<int> Whole, List<int> Excerpted)> EscalateAsync(
        Guid bookId, IReadOnlyList<ChapterRow> chapters, BookArtifactSelector.BookQuestionKeys keys,
        string question, string language, List<string> faults, CancellationToken ct)
    {
        var blocks = new List<BookArtifactBlock>();
        var whole = new List<int>();
        var excerpted = new List<int>();

        if (keys.EscalationChapterOrders.Count == 0) return (blocks, whole, excerpted);

        var targets = keys.EscalationChapterOrders.Take(MaxEscalatedChapters).ToList();
        var byOrder = chapters.ToDictionary(c => c.Order);
        var remaining = BookChatExcerpts.EscalationBudgetTokens;

        foreach (var order in targets)
        {
            if (!byOrder.TryGetValue(order, out var chapter)) continue;
            if (remaining <= 0) break;

            string? text;
            try
            {
                text = await _db.Chapters
                    .AsNoTracking()
                    .Where(c => c.Id == chapter.Id && c.BookId == bookId)
                    .Select(c => c.ContentText)
                    .FirstOrDefaultAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!faults.Contains(BookChatFaults.EscalationUnreadable))
                    faults.Add(BookChatFaults.EscalationUnreadable);
                _logger.LogError(ex,
                    "Book chat context decided to escalate to chapter {ChapterOrder} of book {BookId} and " +
                    "could not read its text ({Fault}). The question named that chapter, so the answer must " +
                    "not be built as if the briefs were all that was ever available.",
                    order, bookId, BookChatFaults.EscalationUnreadable);
                continue;
            }

            // An EMPTY chapter is not a fault: a hand-added or title-only chapter genuinely has no text,
            // and saying "chapter 7 has no text yet" is a true answer the statuses already support.
            var excerpt = BookChatExcerpts.Build(text, question, remaining);
            if (!excerpt.HasText) continue;

            // Escalated text outranks every other chapter's brief, so it carries a high rank inside its
            // own tier; the tier ordering is what actually protects it (d1: escalation never evicts the
            // artifact that triggered it, enforced by ordering rather than by a special case).
            var block = BookArtifactBlocks.ChapterText(
                language, order, chapter.Title, excerpt, rank: 100 - order);
            if (block == null) continue;

            blocks.Add(block);
            remaining -= excerpt.EstimatedTokens;
            (excerpt.IsWholeChapter ? whole : excerpted).Add(order);
        }

        return (blocks, whole, excerpted);
    }

    // ─── Ranking ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ranks chapter briefs against the question's keys. HIGHER survives the trim longer.
    ///
    /// <para>A brief for a chapter the question NAMED outranks one that merely mentions a matched
    /// character, which outranks one that carries a named dimension's marker. A question with NO chapter
    /// or character key at all falls back to book order, so "what happens in my book" grounds in the
    /// opening chapters rather than in an arbitrary slice.</para>
    ///
    /// <para>A chapter whose RAW TEXT ACTUALLY RODE ALONG is excluded: paying for the summary of a
    /// chapter whose full text is already in the prompt is the one clearly wasted block, and at d1's
    /// measured ~700-800 tokens a brief the waste is counted in whole other chapters that did not fit.
    /// That is what the exclusion protects, and it still holds.</para>
    ///
    /// <para>WHAT IT DOES NOT PROTECT, AND USED TO BREAK (g1 F-7). The exclusion keyed on
    /// <c>keys.EscalationChapterOrders</c>, the INTENT to escalate, not on the text having been read. A
    /// chapter whose text was unreadable, empty, or beyond
    /// <see cref="MaxEscalatedChapters"/> therefore lost its brief and gained no text, so the prompt
    /// carried nothing at all about the chapter the question named. <paramref name="carriedRawText"/> is
    /// the escalation's RESULT and is required rather than defaulted, so no caller can reintroduce the
    /// intent-keyed version by omitting it.</para>
    /// </summary>
    /// <param name="carriedRawText">Chapter orders whose raw text really is in this turn's prompt, whole
    /// or excerpted. Empty when nothing escalated.</param>
    internal static IReadOnlyList<(ChapterBrief Brief, double Rank)> RankChapterBriefs(
        IReadOnlyList<ChapterBrief> briefs,
        BookArtifactSelector.BookQuestionKeys keys,
        IReadOnlyCollection<int> carriedRawText)
    {
        var carried = new HashSet<int>(carriedRawText);
        var named = new HashSet<int>(keys.ChapterOrders);

        var ranked = new List<(ChapterBrief Brief, double Rank)>();

        foreach (var brief in briefs)
        {
            if (carried.Contains(brief.Order)) continue;

            var rank = 0.0;
            if (named.Contains(brief.Order)) rank += 100.0;

            foreach (var name in keys.CharacterNames)
            {
                if (brief.CharacterStates.Any(cs =>
                        string.Equals(cs.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    rank += 10.0;
                }
            }

            // AGAINST THE DIMENSION'S SURFACE FORMS, NOT ITS CANONICAL SLUG. `keys.Dimensions` holds
            // canonical slugs (`pacing`), while `ThematicMarkers` is model-written prose in the BOOK's
            // language, so comparing the two directly could only ever match on an English book:
            // `"קצב".Contains("pacing")` is false, and a Hebrew book therefore ranked every chapter brief
            // as though the question had named no dimension at all. Note the sibling comparison in
            // FindingRank stays slug-to-slug and is correct: a finding's Dimension is a KEY the review
            // stamped, not content. Content wants surfaces; a key wants the key.
            foreach (var dimension in keys.Dimensions)
            {
                if (brief.ThematicMarkers.Any(m => BookArtifactSelector.MarkerNamesDimension(m, dimension)))
                    rank += 1.0;
            }

            ranked.Add((brief, rank));
        }

        // ─── THE BOOK-ORDER FALLBACK, AND THE STATE IT USED TO FIRE IN BY ACCIDENT (w9) ─────────
        //
        // The fallback exists for a question that resolved NOTHING chapter-specific ("what happens in my
        // book"), so it grounds in the opening chapters rather than an arbitrary slice. But it was keyed
        // on "no SURVIVING brief scored", and those two are not the same predicate in the one case that
        // matters: a question naming ONE chapter whose raw text rode. That chapter's brief is excluded
        // above (its full text is already here), so nothing left could score, `anyKeyed` went false, and
        // the fallback fired - putting the briefs of chapters 1-6 into a prompt about chapter 8.
        //
        // MEASURED, NOT REASONED: "בפרק 8 איך הם תקשרו את הבעיה?" on the owner's 32-chapter book carried
        // chapter-text:7 plus chapter-brief:0,1,2,3,4,5 and reached ~12,478 of 14,080 input tokens. Six
        // briefs of unrelated chapters at d1's measured ~700-800 tokens each is ~4,500 tokens - more than
        // the entire escalation slice - spent on chapters the author did not ask about, while the chapter
        // they DID ask about was cut down to an excerpt to fit. It is the same defect class as the
        // 0-based/1-based pair: budget spent on a chapter the question never named.
        //
        // So the fallback is now suppressed whenever the question reached a chapter AT ALL - resolved or
        // carried. A character- or dimension-only question is untouched, which is why the guard names
        // chapters specifically rather than testing `keys.IsEmpty`.
        var reachedAChapter = keys.ChapterOrders.Count > 0 || carried.Count > 0;
        var anyKeyed = ranked.Any(r => r.Rank > 0);

        return ranked
            .Where(r => (!anyKeyed && !reachedAChapter) || r.Rank > 0)
            .OrderByDescending(r => r.Rank)
            .ThenBy(r => r.Brief.Order)
            .Take(MaxChapterBriefs)
            .ToList();
    }

    /// <summary>
    /// Ranks a finding against the question's keys: a named dimension is the strongest signal, a named
    /// chapter next (findings anchor chapters), severity is the tie-break rather than a rank of its own.
    /// </summary>
    internal static double FindingRank(BookFinding finding, BookArtifactSelector.BookQuestionKeys keys)
    {
        var rank = 0.0;

        if (keys.Dimensions.Any(d => string.Equals(d, finding.Dimension, StringComparison.OrdinalIgnoreCase)))
            rank += 10.0;

        // The anchors are a JSON array of { chapterId, order, title }, compared here as NUMBERS through the
        // same tri-state parse the reconciler already uses. This replaces a raw-substring probe for
        // "order":N (plus a second probe for the whitespace variant), which was wrong at a digit boundary:
        // "order":1 is a substring of "order":10, "order":12 and "order":19, so a question about chapter 1
        // scored every finding anchored anywhere in chapters 10-19 as a chapter match and MaxFindings then
        // picked the prompt's findings on that inflated rank - invisible under ten chapters, live at forty.
        // Parsed ints are boundary-exact by construction (no separator to get right, no second form to
        // remember), and single-source "what chapters does this finding anchor" instead of keeping a
        // looser second notion of it here.
        //
        // WHAT IT COSTS, STATED HONESTLY (final-r01). The comment this replaced defended the substring
        // probe as costing "no deserialization on a hot path", and that property IS given up here: this
        // is a full System.Text.Json deserialize of the anchors column per finding, not the "one small
        // array" an earlier draft of this paragraph claimed. It is still the right trade, and the reason
        // is the SCALE rather than the price: the rows are already materialized in memory (see
        // RankFindingsAsync, which ranks an awaited List), the count is one book's findings (20 on the
        // 40-chapter fixture, capped downstream at MaxFindings = 8), and every one of these turns then
        // makes a multi-second model call. So do NOT trade this back for a substring scan to buy back a
        // deserialize that no user can perceive - the substring form was WRONG, and wrong at the size the
        // gates never ran. A payload that does not parse yields no anchors and so no chapter rank: an
        // unreadable scope is not a match.
        var anchoredOrders = BookFindingReconciler.ChapterOrdersOf(finding);
        if (anchoredOrders is { Count: > 0 })
        {
            foreach (var order in keys.ChapterOrders)
            {
                if (anchoredOrders.Contains(order))
                    rank += 5.0;
            }
        }

        return rank;
    }

    /// <summary>One chapter as the reader needs it. Scene titles come along because the selector's
    /// scene-reference branch matches against them.</summary>
    private sealed record ChapterRow(Guid Id, int Order, string Title, List<string> SceneTitles);
}
