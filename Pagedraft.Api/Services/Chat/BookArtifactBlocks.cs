using System.Text;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE rendering of retrieved book artifacts into the delimited, TYPED blocks the prompt carries
/// (chatbot phase B, c1).
///
/// <para>TYPED AND DELIMITED IS THE CITATION CONTRACT, NOT DECORATION. Every block opens with a header
/// naming its citation reference verbatim (<c>chapter-brief:7</c>, <c>finding:&lt;guid&gt;</c>,
/// <c>status:review</c>), so "cite the artifact you used" is a thing the model can do by copying a
/// string it can see, and <c>ProductChatCitations</c> can round-trip it back to the block it names.
/// A block whose header the model cannot see is a block it can only cite by guessing.</para>
///
/// <para>THE CHAPTER-TEXT LABEL IS LOAD-BEARING (d1 sections (1) and (3)). <c>[CHAPTER 7, whole
/// chapter]</c> licenses a chapter-scoped assertion; <c>[CHAPTER 7 EXCERPT, not the whole chapter]</c>
/// keeps the partial-coverage shape mandatory. Getting that label wrong reopens exactly the fabrication
/// class phase B's gate exists to catch, so the two forms are constants here rather than interpolated
/// prose at a call site.</para>
///
/// <para>NO MODEL IDS CROSS THE WIRE. The status classes carry <c>BuiltWithModel</c>/<c>ActiveModel</c>;
/// the shipped status DTOs deliberately drop them ("only the VERDICT crosses the wire", the controller
/// mappers' rule) and so does every renderer here. The chat prompt reaches a model and its answer
/// reaches the user, so this is the same boundary, not a looser one.</para>
///
/// <para>AND NO BUILD-TIME-ONLY REVIEW FIELDS. <c>BookReviewStatus.WindowCount</c>,
/// <c>RanSynthesis</c>, <c>RanContinuityReduce</c> and <c>FailedWindows</c> are 0/false on the STATUS
/// probe by construction (they are known only during a build). Rendering them would hand the model a
/// factual-looking "0 windows / synthesis did not run" that is simply an artifact of which probe ran -
/// a WRONG status assertion, which phase B's gate counts as fabrication. They are omitted on purpose.</para>
/// </summary>
public static class BookArtifactBlocks
{
    // ─── Labels. ASCII and language-independent so a test can assert on them ─────────────────────
    //
    // final-r01: a second `BookMarker = "[BOOK]"` was declared here and had ZERO callers - the marker is
    // owned by ProductChatPrompt, which is what emits the section, and every test reads it from there.
    // Two constants for one literal is how the emitter and the parser of a delimiter drift apart, so the
    // dead copy is gone rather than left as a convenience nobody used.

    /// <summary>
    /// {0} IS THE RAW 0-BASED <c>Chapter.Order</c>, THE SAME NUMBER THE REF IN THE HEADER ABOVE IT
    /// CARRIES, AND THAT IS DELIBERATE (be-c02). Every place this prompt names a chapter as DATA - label,
    /// ref, brief heading, history line - carries that one number. Rendering the author's number HERE while
    /// the ref beside it kept the wire's would put two UNLABELLED numbers for one chapter inside the prompt,
    /// which is the defect this fix exists to close, moved one layer in.
    ///
    /// <para>THE 0-TO-1 TRANSLATION IS NO LONGER A PROMPT RULE (final-r02). It used to happen once in the
    /// grounding clause, which taught the offset with this very label as its worked example;
    /// <c>g4</c> then measured the model reproducing that one example and failing every order above it
    /// (0 of 9). The translation now happens in <see cref="AuthorFacingChapterName"/>, which renders the
    /// finished author-facing name on every chapter-scoped block, and the grounding clause points at that
    /// line instead of teaching arithmetic. So a chapter-scoped block really does carry two numbers - this
    /// internal one and the author's - and that is intended: each is LABELLED as what it is, which is
    /// exactly what be-c02's rejected option 2 could not have provided.</para>
    ///
    /// <para>A SECOND EXCEPTION IS NAMED HERE RATHER THAN LEFT TO BE DISCOVERED (final-r01). This used to
    /// read "one chapter has exactly ONE number everywhere in this prompt", which the SAME todo's other
    /// edit falsified: <see cref="ChapterNumberNote"/> speaks the AUTHOR's numbering, deliberately, because
    /// it is the one line of the BOOK section the grounding rule instructs the model to carry into its
    /// answer (see that method's own docstring for the argument). So on an ambiguous-number turn the
    /// section really does carry both conventions - <c>[CHAPTER 4]</c>/<c>[CHAPTER 5]</c> beside "the
    /// author's chapter 5 or their chapter 6" - and that is intended, not a leak. It is worth knowing
    /// while class (a) is open: <c>g4</c>'s only explicit-number question, the one shape that fires this
    /// note, disagreed with its own chips 2 of 2.</para>
    /// </summary>
    internal const string WholeChapterLabelFormat = "[CHAPTER {0}, whole chapter]";

    /// <inheritdoc cref="WholeChapterLabelFormat"/>
    internal const string ExcerptLabelFormat = "[CHAPTER {0} EXCERPT, not the whole chapter]";

    // ─── The author-facing name of a chapter, PRE-COMPUTED (final-r02) ──────────────────────────

    /// <summary>
    /// THE AUTHOR'S NUMBER FOR A WIRE ORDER. The one place on the server that adds the one, mirroring the
    /// client's single <c>chapterDisplayNumber</c> (<c>core/utils/chapter-number.ts</c>). Pinned against it
    /// by <c>ProductChatChapterNumberingTests.TheWireRefAndTheAuthorsNumber_AgreeAcrossTheStack</c>.
    /// </summary>
    internal static int AuthorsChapterNumber(int order) => order + 1;

    /// <summary>
    /// THE FINISHED, AUTHOR-FACING NAME OF ONE CHAPTER, RENDERED SO THE MODEL CAN COPY IT (final-r02, the
    /// phase-B P0). Carried by every chapter-scoped block: <see cref="ChapterText"/>,
    /// <see cref="ChapterBrief"/> and the standalone <see cref="AuthorSummary"/>.
    ///
    /// <para>WHY A RENDERED LINE AND NOT A PROMPT RULE. <c>be-c02</c> put the 0-vs-1 offset into the
    /// grounding clause as a rule with one worked example, and <c>g4</c> then measured what the model does
    /// with it: at order 0 - the order the worked example uses - 4 pass / 3 fail; at every order above it,
    /// <b>0 pass / 9 fail</b>. The model reproduces the example it was shown and does not apply <c>+1</c> as
    /// an operation. So the arithmetic is done HERE, once, and what reaches the model is a finished string
    /// whose cheapest correct use is to copy it. The owner's decision, recorded in the plan's
    /// "final-r02 owner decision" section together with the three options it rejected.</para>
    ///
    /// <para>IT ADDS A HUMAN-FACING LINE; IT RENUMBERS NOTHING. The refs
    /// (<c>chapter-text:0</c>) are wire keys the client parses and the whole-vs-excerpt labels are a
    /// measured safety property (<c>g4</c> bucket (f), which licenses Wave 3's <c>w7</c>), so both keep the
    /// raw order. This is the SECOND number in the section for one chapter, and unlike be-c02's rejected
    /// option 2 that is safe precisely because this one is LABELLED as the author's while the other is
    /// labelled as internal - the collision option 2 would have created was two UNLABELLED numbers.</para>
    ///
    /// <para>THE TITLE RIDES BECAUSE THE TITLE IS WHAT THE AUTHOR RECOGNISES, and <c>g4</c> measured that
    /// from the other side: <c>a4</c>, its one question that asked BY TITLE, named the chapter correctly
    /// 2 of 2 and is one of only four passes in the whole bank.</para>
    ///
    /// <para>AND IT ANSWERS <c>A16</c>, WHICH final-r01 LEFT OWED. An untitled chapter renders the number
    /// alone ("the author calls this chapter: chapter 1", he: "המחבר קורא לפרק הזה: פרק 1"); a chapter
    /// whose own title IS a number - which is the commonest real shape in this corpus, <c>פרק 28</c>
    /// sitting at order 0 - renders both ("the author calls this chapter: פרק 28 (chapter 1)", he:
    /// "המחבר קורא לפרק הזה: פרק 28 (פרק 1)"). Nothing is lost either way and the author keeps a number to
    /// refer back to, which is why this was preferred to naming chapters by title only. The Hebrew
    /// numeric-title case really does read "פרק 28 (פרק 1)" and that disagreement is the POINT: the author
    /// wrote "chapter 28" on the chapter the product counts as their chapter 1, and hiding either half is
    /// a worse answer than showing both.</para>
    ///
    /// <para>THIS IS THE ONE LINE OF THE BOOK SECTION THAT IS WRITTEN IN THE READER'S LANGUAGE, AND THAT
    /// IS DELIBERATE (final-r05). DO NOT "TIDY" IT BACK TO ENGLISH FOR CONSISTENCY WITH ITS NEIGHBOURS.
    /// Every other string in that section is machine-facing - the labels, the refs, the headings, the
    /// notes - and its own docstring says "English, like every other line of the BOOK section: none of it
    /// is user-facing". This line is the exception because it is the one string in the section DESIGNED to
    /// reach the author verbatim: the grounding clause tells the model to name a chapter by COPYING it.
    ///
    /// <para>IT IS NOT A STYLE PREFERENCE, IT IS A MEASUREMENT. final-r02 shipped this line in English and
    /// flagged the risk that the Hebrew path would have to TRANSLATE the frame. <c>g5</c> then measured
    /// exactly that: the model does copy the line (19 of 25 answers reproduce the title and the number
    /// together), and copying is what makes an English frame a defect - Latin-script "chapter N" survived
    /// untranslated inside Hebrew prose in <b>7 of 45 Hebrew book-scoped runs (16%)</b>, e.g.
    /// <c>הפרק שנקרא על ידי המחבר "השביל הנסתר" (chapter 39)</c>. Six of the seven carried the CORRECT
    /// number, so the arithmetic was working and the FRAME was the thing failing. An LTR fragment inside RTL
    /// prose also drags its punctuation to the wrong end, which is the same malformation shape review
    /// finding #3 recorded for <c>chapter-text:0</c>. Rendering the line in the answer's language leaves
    /// nothing to translate.</para>
    ///
    /// <para>WHICH LANGUAGE, AND WHY IT IS THE ANSWER'S AND NOT THE BOOK'S. The two are allowed to differ -
    /// that divergence is the whole of the <c>g1</c> F-2 fix, and <c>BookChatContextReader</c> logs them
    /// separately for that reason. The BOOK's language
    /// (<c>BaselineLanguageResolver.Normalize(book.Language)</c>) is a RETRIEVAL KEY: it decides which
    /// artifact rows exist, not what the reader reads, and it resolves a blank to Hebrew on purpose, so
    /// keying this line on it would put a Hebrew frame into an English answer on precisely the
    /// cross-language turn F-2 exists to serve. The ANSWER's language
    /// (<c>ChatLanguage.Detect(question, request.Language)</c>) is the language of the sentence this line
    /// is copied into, AND it is the same value that picks the grounding clause pointing at this line - so
    /// keying on it is also what keeps the instruction and its referent in one language. The TITLE inside
    /// the line is untouched either way: the book's own language is still represented where it belongs, in
    /// the data.</para>
    ///
    /// <para>THE LANGUAGE IS A REQUIRED PARAMETER, NOT A DEFAULT. A defaulted <c>"en"</c> would let the
    /// next chapter-scoped renderer ship the untranslated frame silently, which is this defect exactly.</para>
    /// </summary>
    internal static string AuthorFacingChapterName(string language, int order, string? title)
    {
        var number = AuthorsChapterNumber(order);
        var hebrew = ChatLanguage.IsHebrew(language);

        var frame = hebrew ? "המחבר קורא לפרק הזה: " : "the author calls this chapter: ";
        var authorsNumber = hebrew ? $"פרק {number}" : $"chapter {number}";

        return string.IsNullOrWhiteSpace(title)
            ? frame + authorsNumber
            : $"{frame}{title.Trim()} ({authorsNumber})";
    }

    /// <summary>
    /// The BookBrief size cap, reused from <see cref="AiOptions.BookReviewWindowBriefMaxTokens"/> rather
    /// than invented (d1's decision: one BookBrief-size budget, not two that can drift). The DEFAULT is
    /// named here only for the case where no options instance is available; live callers pass the
    /// configured value.
    /// </summary>
    public const int DefaultBookBriefMaxTokens = 800;

    // ─── Block headers ──────────────────────────────────────────────────────────────────────────

    private static string Header(string reference, string? label = null)
        => label == null
            ? $"=== ARTIFACT ref={reference} ===\n"
            : $"=== ARTIFACT ref={reference} {label} ===\n";

    // ─── The chapter-numbering note (phase B, f2, c1 watch-list item 2) ─────────────────────────

    /// <summary>
    /// The one thing retrieval KNEW and the prompt used to discard: a bare "chapter 5" grounds BOTH order
    /// 4 and order 5, because <c>Chapter.Order</c> is 0-based here and authors count from 1, and the
    /// selector keeps both rather than guessing (<see cref="BookArtifactSelector"/>). g1 confirmed the
    /// model does NOT merge the two into one false claim: it answers one of them and never says it chose.
    /// The honesty was in the data and was thrown away at the prompt boundary, so this puts it back into
    /// the BOOK section where the grounding rule can act on it.
    ///
    /// <para>EMITTED ONLY WHEN BOTH CANDIDATES ACTUALLY RODE. The note is computed from the blocks that
    /// SURVIVED the trim, not from the selector's intent, for the same reason the acceptable citation set
    /// is: telling the model "both were retrieved" about a chapter the trimmer dropped would be a
    /// statement about the prompt that the prompt contradicts. A number whose second candidate carries no
    /// artifact is not ambiguous in anything the model can see, so it produces nothing and the ordinary
    /// chapter answer never acquires a hedge.</para>
    ///
    /// <para>English, like every other line of the BOOK section. The rule that governs what the model DOES
    /// with the note is in both languages, in <c>ProductChatPrompt.BookGroundingEn</c>/<c>He</c>.</para>
    ///
    /// <para>THIS IS THE ONE LINE OF THE BOOK SECTION WHOSE NUMBERS ARE THE AUTHOR'S, AND THE REASON IS
    /// THAT IT IS THE ONE LINE WRITTEN TO BE SPOKEN (be-c02, review finding #1). Everything else in the
    /// section is machine-facing and keeps the wire's 0-based orders, with the prompt carrying a single
    /// translation rule; this note is different because the grounding rule explicitly instructs the model
    /// to CARRY IT INTO THE ANSWER ("a note in the BOOK section about what the question could have meant
    /// belongs in the answer"), so it is author-facing by contract. It used to read "both chapter 4 and
    /// chapter 5 were retrieved" in raw orders, with an inline re-teaching of the 0-vs-1 offset. Handing
    /// the model raw orders in the one string it is told to repeat, while the grounding clause tells it to
    /// give the author's number, is two emphatic instructions that disagree - the collision shape this
    /// prompt has already been burned by twice (g3's fourth prohibition, F-1's two rules). So the note now
    /// states the ambiguity in the author's own numbering (their chapter N or their chapter N+1, since
    /// orders N-1 and N are what rode) and drops the offset explanation, which the widened clause in
    /// <c>ProductChatPrompt</c> now owns alone. It is also SHORTER, which the Hebrew budget needed.</para>
    /// </summary>
    /// <param name="ambiguousChapterNumbers">Numbers the question wrote that resolved to two real
    /// chapters, from <c>BookQuestionKeys.AmbiguousChapterNumbers</c>.</param>
    /// <param name="blocks">The blocks that survived composition.</param>
    public static string? ChapterNumberNote(
        IReadOnlyList<int>? ambiguousChapterNumbers, IReadOnlyList<BookArtifactBlock>? blocks)
    {
        if (ambiguousChapterNumbers == null || ambiguousChapterNumbers.Count == 0) return null;
        if (blocks == null || blocks.Count == 0) return null;

        var carried = new HashSet<int>();
        foreach (var reference in blocks.SelectMany(b => b.References))
        {
            if (TryChapterOrderOf(reference, out var order)) carried.Add(order);
        }

        var parts = ambiguousChapterNumbers
            .Where(n => carried.Contains(n - 1) && carried.Contains(n))
            .OrderBy(n => n)
            .Select(n =>
                $"the question says chapter {n}, which could be the author's chapter {n} or their " +
                $"chapter {n + 1}; both were retrieved and which one was meant is not known")
            .ToList();

        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// The note the BOOK section carries when the question was about a chapter and NO chapter resolved
    /// (chatbot phase B, d2 section (5)). English, like every other line of that section: none of it is
    /// user-facing, and the RULE governing what the model does with a note lives in both languages in
    /// <c>ProductChatPrompt</c>'s grounding string.
    /// </summary>
    internal const string NoChapterIdentifiedNote =
        "the question is about a chapter and no chapter was identified: none was named and none is open.";

    /// <summary>
    /// The ONE note line the BOOK section carries, whichever of the two notes applies.
    ///
    /// <para>THEY SHARE A CHANNEL BECAUSE THEY ARE PROVABLY DISJOINT, not because a collision was judged
    /// unlikely. The ambiguity note fires only when <c>AmbiguousChapterNumbers</c> is non-empty, and both
    /// candidates it names are already resolved into <c>ChapterOrders</c> (see that field's own doc), so
    /// firing means <c>ChapterOrders.Count</c> is never 0. <c>NeedsChapterClarification</c> instead
    /// requires <c>ChapterOrders.Count == 0</c>. The two fields' own preconditions cannot both hold, so
    /// there is no ordering to decide and no "both fired" state to render - and the model is never handed
    /// two notes to arbitrate between, which is the shape of collision this prompt has already been burned
    /// by twice.</para>
    /// </summary>
    public static string? BookSectionNote(
        IReadOnlyList<int>? ambiguousChapterNumbers,
        IReadOnlyList<BookArtifactBlock>? blocks,
        bool needsChapterClarification)
        => needsChapterClarification
            ? NoChapterIdentifiedNote
            : ChapterNumberNote(ambiguousChapterNumbers, blocks);

    /// <summary>The chapter order a chapter-scoped ref names, for the three chapter-keyed vocabularies.
    /// False for <c>register</c>, <c>status:review</c>, a finding guid and anything else.</summary>
    private static bool TryChapterOrderOf(string reference, out int order)
    {
        order = 0;
        if (string.IsNullOrEmpty(reference)) return false;

        var colon = reference.IndexOf(':');
        if (colon <= 0) return false;

        var prefix = reference[..colon];
        var isChapterRef =
            string.Equals(prefix, BookArtifactRefs.ChapterBriefPrefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(prefix, BookArtifactRefs.ChapterSummaryPrefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(prefix, BookArtifactRefs.ChapterTextPrefix, StringComparison.OrdinalIgnoreCase);

        return isChapterRef && int.TryParse(reference.AsSpan(colon + 1), out order);
    }

    // ─── Book-level brief ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The book-level brief, trimmed to <paramref name="maxTokens"/> under the SHARED estimator.
    ///
    /// <para>d1 named <c>BookContextAssembler.FormatBookBriefTrimmed</c> as the thing to reuse. Its
    /// RENDER (<see cref="BookContextAssembler.FormatBookBrief"/>) is reused verbatim; its TRIM is not,
    /// because that method sizes itself with the assembler's single-rate-per-blob estimator and d1's own
    /// section (2) requires the whole composed chat prompt to be measured by ONE rule. Trimming here with
    /// a second estimator would be the dual-surface trap moved into the budget layer. The trim POLICY is
    /// the same one that method states: the cheap metadata lines are never touched, and the synopsis is
    /// the first thing sacrificed.</para>
    /// </summary>
    public static BookArtifactBlock? BookBrief(BookBrief? brief, string? bookTitle, int maxTokens)
    {
        if (brief == null) return null;

        var full = BookContextAssembler.FormatBookBrief(brief).Trim();

        // "Book title", not "Title" (be-c03, review finding #7). This is the SECOND place the book's own
        // title reaches the model, and a bare "Title:" in a section that also carries chapter titles is
        // the same ambiguity the BOOK section's head line was fixed for. Chat-only: the shared
        // FormatBookBrief above renders no title line of its own.
        var title = string.IsNullOrWhiteSpace(bookTitle) ? string.Empty : $"Book title: {bookTitle}\n";
        var body = title + full;

        if (body.Length == 0) return null;

        if (ProductChatBudget.EstimateTokens(body) > maxTokens)
            body = TrimBookBrief(brief, title, maxTokens);

        // Kind Status is NOT used here: the book brief has its own tier, one rung below the statuses.
        return new BookArtifactBlock(
            BookArtifactKind.BookBrief,
            new[] { BookArtifactRefs.BookBrief },
            Header(BookArtifactRefs.BookBrief) + body,
            Rank: 0);
    }

    /// <summary>Metadata head always; then themes; then as much synopsis as fits. Same sacrifice order
    /// as the windowed review's trim, measured with the chat's estimator.</summary>
    private static string TrimBookBrief(BookBrief b, string title, int maxTokens)
    {
        var head = new StringBuilder(title);
        if (b.Genre != null) head.AppendLine($"Genre: {b.Genre}{(b.SubGenre != null ? $" / {b.SubGenre}" : "")}");
        if (b.TargetAudience != null) head.AppendLine($"Audience: {b.TargetAudience}");
        if (b.LiteratureLevel.HasValue) head.AppendLine($"Literature level: {b.LiteratureLevel}/10");

        var used = ProductChatBudget.EstimateTokens(head.ToString());

        if (b.Themes.Count > 0)
        {
            var kept = new List<string>();
            foreach (var theme in b.Themes)
            {
                var candidate = $"Themes: {string.Join(", ", kept.Append(theme))}\n";
                if (kept.Count > 0 && used + ProductChatBudget.EstimateTokens(candidate) > maxTokens) break;
                kept.Add(theme);
            }

            if (kept.Count > 0)
            {
                var line = $"Themes: {string.Join(", ", kept)}\n";
                head.Append(line);
                used += ProductChatBudget.EstimateTokens(line);
            }
        }

        if (!string.IsNullOrWhiteSpace(b.Synopsis))
        {
            var remaining = maxTokens - used - ProductChatBudget.EstimateTokens("Synopsis: \n");
            if (remaining > 0)
            {
                // The chat estimator is script-aware, so invert it conservatively at the HEBREW rate:
                // assuming the denser script cannot under-cut the cap on Latin text.
                var maxChars = Math.Max(0, (int)(remaining * ProductChatBudget.HebrewCharsPerToken) - 1);
                var synopsis = b.Synopsis!.Length > maxChars
                    ? b.Synopsis[..maxChars].TrimEnd() + "…"
                    : b.Synopsis;
                head.Append($"Synopsis: {synopsis}\n");
            }
        }

        return head.ToString().TrimEnd();
    }

    // ─── Per-chapter brief (+ the author's own flat summary) ────────────────────────────────────

    /// <summary>
    /// One chapter's STRUCTURED brief, rendered through the SAME
    /// <see cref="BookContextAssembler.FormatChapterBrief"/> the whole-book review reads (d1 section (1)),
    /// so an answer grounded in a brief can never describe it differently than the finding the author is
    /// also looking at.
    ///
    /// <para>WHEN THE AUTHOR EDITED THE FLAT SUMMARY it rides along in the SAME block under its own
    /// <c>chapter-summary:&lt;order&gt;</c> reference, labeled as the author's own words. Two citable
    /// artifacts, not a silent override: the author wrote one of them, and answering from the
    /// machine-extracted surface while their own sentences sit in the same row is a worse answer, not a
    /// safer one. They ride in ONE block so a trim can never keep one and drop the other, which would
    /// leave the model comparing a surface against something it can no longer see.</para>
    /// </summary>
    /// <param name="language">The ANSWER's language, for the author-facing name line only (final-r05).
    /// Language comes FIRST on all three chapter-scoped producers, matching
    /// <c>ProductChatPrompt.SystemMessage</c> / <c>ComposeInstruction</c> / <c>ProductChatBudget.Compose</c>,
    /// and it is required rather than defaulted so a new caller cannot ship the untranslated frame.</param>
    /// <param name="authorSummary">The flat <c>ChunkSummary.SummaryText</c>, passed ONLY when
    /// <c>SummaryUserEdited</c> is true. Null otherwise.</param>
    public static BookArtifactBlock ChapterBrief(
        string language, ChapterBrief brief, string? authorSummary, double rank)
    {
        var refs = new List<string> { BookArtifactRefs.ChapterBrief(brief.Order) };
        var sb = new StringBuilder();

        sb.Append(Header(BookArtifactRefs.ChapterBrief(brief.Order)));

        // THE AUTHOR-FACING NAME FIRST, then the shared "## Chapter {Order}:" heading (final-r02). It
        // leads the block because it is the string the model is meant to reach for when it names this
        // chapter to the author; the heading under it is the cross-feature contract with the whole-book
        // review and stays raw. See AuthorFacingChapterName for why this is rendered rather than taught.
        sb.Append(AuthorFacingChapterName(language, brief.Order, brief.Title)).Append('\n');

        sb.Append(BookContextAssembler.FormatChapterBrief(brief));

        if (!string.IsNullOrWhiteSpace(authorSummary))
        {
            refs.Add(BookArtifactRefs.ChapterSummary(brief.Order));
            sb.Append('\n')
              .Append(Header(BookArtifactRefs.ChapterSummary(brief.Order), "the author's own summary"))
              .Append(authorSummary.Trim());
        }

        return new BookArtifactBlock(BookArtifactKind.ChapterBrief, refs, sb.ToString(), rank);
    }

    /// <summary>
    /// The author's own edited flat summary STANDING ALONE, for a chapter whose structured brief is not
    /// being carried because its raw text is (g1 F-7).
    ///
    /// <para>Identical rendering to the sub-block <see cref="ChapterBrief"/> emits, deliberately: the
    /// model must not be able to tell from the prose whether the author's summary arrived beside its
    /// structured twin or on its own, because the claim it licenses ("this is what the author wrote about
    /// this chapter") is the same claim either way. Only the citation ref rides, so an answer built from
    /// it cites <c>chapter-summary:&lt;order&gt;</c> and never implies a brief it was not given.</para>
    ///
    /// <para>Returns null for a blank summary, so a chapter the author never edited produces no block
    /// rather than an empty one the model could cite as having been read.</para>
    ///
    /// <para>IT TAKES THE CHAPTER'S TITLE ONLY TO NAME THE CHAPTER (final-r02). This block is one of the
    /// three the model can answer a chapter question from, so it carries the same
    /// <see cref="AuthorFacingChapterName"/> line as the other two; without the title it could render the
    /// author's number but not the name they recognise, and the "identical rendering" rule above is about
    /// the SUMMARY body, which is untouched.</para>
    /// </summary>
    /// <param name="language">The ANSWER's language, for the author-facing name line only (final-r05); see
    /// <see cref="AuthorFacingChapterName"/> for why that and not the book's.</param>
    public static BookArtifactBlock? AuthorSummary(
        string language, int order, string? title, string? authorSummary, double rank)
    {
        if (string.IsNullOrWhiteSpace(authorSummary)) return null;

        return new BookArtifactBlock(
            BookArtifactKind.AuthorSummary,
            new[] { BookArtifactRefs.ChapterSummary(order) },
            Header(BookArtifactRefs.ChapterSummary(order), "the author's own summary")
                + AuthorFacingChapterName(language, order, title) + "\n"
                + authorSummary.Trim(),
            rank);
    }

    // ─── Raw chapter text (escalated) ───────────────────────────────────────────────────────────

    /// <summary>
    /// An escalated chapter's raw text, carrying the whole-vs-excerpt label the grounding rule branches
    /// on. Returns null for an empty excerpt, so a chapter that yielded nothing never produces a block
    /// the model could cite as having been read.
    /// </summary>
    /// <param name="language">The ANSWER's language, for the author-facing name line only (final-r05). The
    /// whole-vs-excerpt LABEL above it stays English and unchanged - it is a measured safety property
    /// (<c>g4</c>/<c>g5</c> bucket (f)) and the grounding clause quotes it verbatim in both languages.</param>
    public static BookArtifactBlock? ChapterText(
        string language, int order, string? title, BookChatExcerpts.Excerpt excerpt, double rank)
    {
        if (!excerpt.HasText) return null;

        var label = string.Format(
            excerpt.IsWholeChapter ? WholeChapterLabelFormat : ExcerptLabelFormat, order);

        // "title:" NAMES WHAT FOLLOWS IT (be-c03, review finding #7). The chapter's own title used to be
        // juxtaposed to the label with nothing saying it was a title at all, while the book's title sat at
        // the head of the same section; the answer that came out named the CHAPTER by the BOOK's title.
        // The grounding clause tells the model to name a chapter to the author "by its title", so this is
        // the line that has to make "its title" findable.
        var heading = string.IsNullOrWhiteSpace(title) ? label : $"{label} title: {title}";

        return new BookArtifactBlock(
            BookArtifactKind.ChapterText,
            new[] { BookArtifactRefs.ChapterText(order) },
            Header(BookArtifactRefs.ChapterText(order))
                + heading + "\n"
                // The author-facing name goes directly under the internal label, so the two numbers for
                // this chapter sit adjacent and each is read with its own description (final-r02).
                + AuthorFacingChapterName(language, order, title) + "\n"
                + excerpt.Text,
            rank);
    }

    // ─── Findings ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One review finding: dimension, verdict, severity, status and rationale. NOT the
    /// <c>EvidenceJson</c> blob - d1 measured it at an average of 412 characters of nested anchors
    /// against a 79-character rationale that already carries the claim, so spending budget on it buys
    /// density, not grounding.
    /// </summary>
    public static BookArtifactBlock Finding(BookFinding finding, double rank)
    {
        var sb = new StringBuilder();

        // THE HEADER CARRIES A NAME AS WELL AS THE REF (g1 F-10). The ref key is a raw guid because the
        // client's findings ledger routes on it, and g1 measured that guid printed into Hebrew prose,
        // which is the only handle the model had for saying WHICH finding it meant. The prompt now says a
        // finding is named by its dimension in a sentence and by its ref on the sources line; this is what
        // makes the first half of that sentence something it can do by copying rather than by inventing.
        sb.Append(Header(
            BookArtifactRefs.Finding(finding.Id),
            $"the {finding.Dimension} finding"));
        sb.Append($"Dimension: {finding.Dimension}; verdict: {finding.Verdict}; severity: {finding.Severity}/3; ")
          .Append($"status: {finding.Status}\n");
        sb.Append(finding.Rationale.Trim());

        if (!string.IsNullOrWhiteSpace(finding.SuggestedAction))
            sb.Append("\nSuggested action: ").Append(finding.SuggestedAction!.Trim());

        return new BookArtifactBlock(
            BookArtifactKind.Finding,
            new[] { BookArtifactRefs.Finding(finding.Id) },
            sb.ToString(),
            rank);
    }

    // ─── Character register ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The character register, rendered through
    /// <see cref="BookContextAssembler.FormatCharacterRegisterBlock"/> - reused, not re-derived, so chat
    /// and the whole-book review name and alias characters identically.
    ///
    /// <para>PROVENANCE IS RENDERED SEPARATELY, because the shared renderer deliberately does not carry
    /// it (it decides what the register HOLDS, not what a model is told) and chat is the one surface
    /// where it must be SAID: "confirmed by the author" and "recorded, not yet confirmed" are different
    /// claims, and asserting the second as the first is a small fabrication about the author's own
    /// decisions. Only the confirmed entries are listed, so this costs one short line and never grows
    /// with the cast.</para>
    ///
    /// <para>The register handed in must ALREADY be suppression-filtered
    /// (<c>CharacterRegisterMerge.ForAnalysis</c>): a suppressed entry must never reach the prompt at
    /// all.</para>
    /// </summary>
    public static BookArtifactBlock? Register(CharacterRegister? register)
    {
        var body = BookContextAssembler.FormatCharacterRegisterBlock(register);
        if (string.IsNullOrWhiteSpace(body)) return null;

        var sb = new StringBuilder();
        sb.Append(Header(BookArtifactRefs.Register));
        sb.Append(body.Trim());

        var confirmed = register!.Characters
            .Where(c => c.IsCharacter && (c.GenderConfirmed || c.AliasesConfirmed || c.IsCharacterConfirmed))
            .Select(c => c.Name)
            .ToList();

        sb.Append("\nAuthor-confirmed entries: ")
          .Append(confirmed.Count == 0
              ? "none (every value here was extracted, not confirmed by the author)"
              : string.Join(", ", confirmed));

        return new BookArtifactBlock(
            BookArtifactKind.Register, new[] { BookArtifactRefs.Register }, sb.ToString(), Rank: 0);
    }

    // ─── Analysis history metadata (B-scope fence) ──────────────────────────────────────────────

    /// <summary>What ran, when, on which chapter. METADATA ONLY (d1's deliberate B-scope fence): result
    /// bodies are unbounded, and quoting an old proofread's suggestions back is low-value.</summary>
    /// <param name="entries">Already ordered newest-first and already capped by the reader.</param>
    public static BookArtifactBlock? History(IReadOnlyList<(string Type, int? ChapterOrder, DateTimeOffset At)> entries)
    {
        if (entries.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append(Header(BookArtifactRefs.History, "editing history: what ran, when, where"));

        foreach (var (type, chapterOrder, at) in entries)
        {
            sb.Append("- ").Append(type);
            sb.Append(chapterOrder.HasValue ? $" on chapter {chapterOrder}" : " on the whole book");
            sb.Append(" at ").Append(at.UtcDateTime.ToString("yyyy-MM-dd HH:mm")).Append(" UTC\n");
        }

        return new BookArtifactBlock(
            BookArtifactKind.History, new[] { BookArtifactRefs.History }, sb.ToString().TrimEnd(), Rank: 0);
    }

    // ─── Statuses: the tutoring backbone ────────────────────────────────────────────────────────

    /// <summary>
    /// The three build/staleness statuses as ONE never-dropped block, rendered as compact label:value
    /// lines rather than JSON.
    ///
    /// <para>ONE BLOCK, NOT THREE, for a reason the trimmer makes plain: they are jointly never
    /// droppable, so three blocks would only add two more headers' worth of tokens and three more
    /// chances for a partial drop. The three citation references still travel separately, so an answer
    /// can cite <c>status:review</c> alone.</para>
    ///
    /// <para>EVERY LINE IS A FACT THE PRODUCT ALREADY DERIVES, never a re-derivation. "Behind" is
    /// <c>hasX &amp;&amp; !ready</c>, the review's blocked state is <c>!hasBriefs</c>, and the finding
    /// counts are rendered as the THREE buckets the ledger actually has (open / acknowledged / resolved),
    /// because <c>FindingCount - Resolved - Open</c> is the acknowledged bucket and NOT zero - a two-bucket
    /// sentence would be a wrong status assertion, which this phase's gate counts as fabrication.</para>
    /// </summary>
    public static BookArtifactBlock Statuses(
        BookSummaryStatus? summary,
        BookReviewStatus? review,
        BookStyleBaselineStatus? styleBaseline)
    {
        // HEADER SHAPE, DECIDED DELIBERATELY (review finding #2). The block stays ONE block (the
        // reasoning above still holds: three would only add two more headers' worth of tokens and
        // three more chances for a partial drop), but the header must still show refs the parser
        // accepts. A bare "status" is not one - BookArtifactRefs.LooksLikeArtifactRef requires either
        // a colon or membership in the keyless allowlist (register/history/book-brief), and "status"
        // is neither - so it silently contradicted the citation instruction ("name a book artifact by
        // the ref in its own header"), and g2 measured the model writing the placeholder verbatim
        // ("Sources: ref=status"). The fix names all THREE real refs in the one header, comma-joined,
        // so the model can copy any single token off it exactly as the prompt's own worked example
        // shows ("Sources: chapter-text:7, status:review").
        var sb = new StringBuilder();
        sb.Append(Header(
            string.Join(", ", BookArtifactRefs.StatusSummary, BookArtifactRefs.StatusReview, BookArtifactRefs.StatusStyleBaseline),
            "build state of this book, always current"));

        sb.Append("status:summary (Book briefs) - ");
        if (summary == null) sb.Append("could not be read.\n");
        else
        {
            sb.Append($"chapters: {summary.TotalChapters}; with a current brief: {summary.BuiltChapters}; ")
              .Append($"missing or out of date: {summary.StaleCount}; ")
              .Append($"book-level brief built: {Yes(summary.HasSummary)}; ")
              .Append($"state: {SummaryState(summary)}");
            AppendBuild(sb, summary.ActiveBuildJobId, summary.LastUpdatedAt);
        }

        sb.Append("status:review (Developmental review) - ");
        if (review == null) sb.Append("could not be read.\n");
        else
        {
            sb.Append($"state: {ReviewState(review)}; ")
              .Append($"findings: {review.FindingCount} total, {review.OpenFindingCount} untouched, ")
              .Append($"{Acknowledged(review)} acknowledged, {review.ResolvedFindingCount} resolved; ")
              .Append($"chapters covered: {review.ChaptersReviewed} of {review.ChaptersTotal}");
            AppendBuild(sb, review.ActiveBuildJobId, review.LastUpdatedAt);
        }

        sb.Append("status:style-baseline (Style baseline) - ");
        if (styleBaseline == null) sb.Append("could not be read.\n");
        else
        {
            sb.Append($"chapters with a current profile: {styleBaseline.BuiltChapters} of {styleBaseline.TotalChapters}; ")
              .Append($"missing or out of date: {styleBaseline.StaleCount}; ")
              .Append($"state: {BaselineState(styleBaseline)}");
            AppendBuild(sb, styleBaseline.ActiveBuildJobId, styleBaseline.LastUpdatedAt);
        }

        return new BookArtifactBlock(
            BookArtifactKind.Status,
            new[] { BookArtifactRefs.StatusSummary, BookArtifactRefs.StatusReview, BookArtifactRefs.StatusStyleBaseline },
            sb.ToString().TrimEnd(),
            Rank: 0);
    }

    private static void AppendBuild(StringBuilder sb, Guid? activeJob, DateTimeOffset? lastUpdated)
    {
        if (activeJob.HasValue) sb.Append("; a build is running right now");
        if (lastUpdated.HasValue) sb.Append($"; last built {lastUpdated.Value.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
        sb.Append(".\n");
    }

    /// <summary>The stage-spine vocabulary, stated once. The states are the SAME five the Wave 3 spine
    /// renders, derived from the same fields, so chat and the dashboard can never disagree about whether
    /// a stage is behind.
    ///
    /// <para>THE REASON IS A NAMED FIELD, NOT A PARENTHETICAL (g1 F-6). "Why is my review out of date"
    /// was 3/3 correct in English and 0/6 in Hebrew on the same book, reciting the guides' generic list of
    /// possible causes instead of the one this book's status states. The reason was already in the block,
    /// inside brackets at the end of a compound line, which reads as an aside; <c>reason:</c> makes it the
    /// same shape as every other fact here and gives the prompt's "where it names a reason, that reason is
    /// this book's reason" a field to point at. This is a legibility change, not a content change: the
    /// same strings, derived the same way, on the same conditions.</para>
    /// </summary>
    private static string SummaryState(BookSummaryStatus s)
    {
        if (s.ActiveBuildJobId.HasValue) return "running";
        if (!s.HasSummary) return "not built yet";
        if (s.IsReady) return "up to date";
        return $"BEHIND; reason: {BehindReason(s)}";
    }

    private static string BehindReason(BookSummaryStatus s)
    {
        var reasons = new List<string>();
        if (s.StaleCount > 0) reasons.Add($"{s.StaleCount} chapter brief(s) missing or out of date");
        if (s.BuiltWithDifferentModel) reasons.Add("built under a different model than the one now configured");
        if (!s.SummaryCoversBuiltChapters) reasons.Add("the book-level brief does not yet cover every built chapter");
        return reasons.Count == 0 ? "a rebuild is required" : string.Join("; ", reasons);
    }

    private static string ReviewState(BookReviewStatus r)
    {
        if (!r.HasBriefs) return "BLOCKED: the book briefs are not built, and the review reads them";
        if (r.ActiveBuildJobId.HasValue) return "running";
        if (!r.HasReview) return "not built yet";
        if (r.IsReady) return "up to date";

        var reasons = new List<string>();
        if (r.StaleVsBriefs) reasons.Add("the briefs were rebuilt after this review");
        if (r.BuiltWithDifferentModel) reasons.Add("built under a different model than the one now configured");
        return $"BEHIND; reason: {(reasons.Count == 0 ? "a rebuild is required" : string.Join("; ", reasons))}";
    }

    private static string BaselineState(BookStyleBaselineStatus s)
    {
        if (s.ActiveBuildJobId.HasValue) return "running";
        if (!s.HasBaseline) return "not built yet";
        if (s.IsReady) return "up to date";

        var reasons = new List<string>();
        if (s.StaleCount > 0) reasons.Add($"{s.StaleCount} chapter profile(s) missing or out of date");
        if (s.BuiltWithDifferentModel) reasons.Add("built under a different model than the one now configured");
        return $"BEHIND; reason: {(reasons.Count == 0 ? "a rebuild is required" : string.Join("; ", reasons))}";
    }

    /// <summary>The third finding bucket, which is not derivable from the other two and is therefore
    /// computed rather than implied. Clamped at zero so an inconsistent snapshot cannot render a
    /// negative count as if it were a fact.</summary>
    private static int Acknowledged(BookReviewStatus r)
        => Math.Max(0, r.FindingCount - r.OpenFindingCount - r.ResolvedFindingCount);

    private static string Yes(bool value) => value ? "yes" : "no";
}
