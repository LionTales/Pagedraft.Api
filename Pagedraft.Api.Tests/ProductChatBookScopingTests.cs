using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// WHAT THE BOOK ARTIFACTS LICENSE, AND WHAT SURVIVES OF THAT TO THE PROMPT (chatbot phase B, f2;
/// g1 findings F-5, F-6, F-8, F-10 and c1's watch-list item 2).
///
/// <para>THE COMMON SHAPE OF ALL FOUR FINDINGS. Each is a place where the artifact carried MORE
/// information than the answer used, or the answer asserted more than the artifact carried. The status
/// block states a COUNT of chapters that are behind and never says which; the model named a range, flat
/// and unhedged, including a chapter the book does not have. It states a reason for a stale review; the
/// Hebrew answer recited the guides' generic causes instead, 0 of 6, on a question English got 3 of 3.
/// The chapter-text block carries a whole-vs-EXCERPT label that must reach the model and must not reach
/// the author; it opened 5 of 6 and 6 of 6 Hebrew answers verbatim. The finding block's ref is a raw
/// guid because the client's ledger routes on it, and the guid was printed into Hebrew prose. And the
/// selector deliberately grounds BOTH candidates for a bare "chapter N" - honest, because Order is
/// 0-based here and authors count from 1 - and the prompt threw that honesty away.</para>
///
/// <para>NONE OF THIS IS PROVABLE BY A UNIT TEST. What a model does with a rule is a gate measurement.
/// What IS pinnable is the half that decides whether it has anything to obey: the artifact says the
/// thing, the rule scopes the thing, and the retrieval's own uncertainty survives to the prompt instead
/// of being discarded on the way. That is what this file pins, in both languages.</para>
///
/// <para>Pure: no model, no GPU, no network, no database.</para>
/// </summary>
public class ProductChatBookScopingTests
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 1. F-5: a count is not a list ──────────────────────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PREMISE THE RULE RESTS ON, ASSERTED RATHER THAN ASSUMED: the status block really does state a
    /// count and really does not state WHICH chapters. If it ever grew a list, the scoping sentence below
    /// would be actively wrong, and nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void TheStatusBlock_StatesACount_AndNamesNoChapter()
    {
        var block = BookArtifactBlocks.Statuses(
            Summary(total: 6, built: 3, stale: 3), Review(stale: true), Baseline(stale: 6));

        Assert.Contains("missing or out of date: 3", block.Text, StringComparison.Ordinal);

        // No per-chapter identity of any kind: the block speaks in totals and states.
        Assert.DoesNotContain("chapter 3", block.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chapters 3", block.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AND THE RULE SCOPES IT, in the shape g1 recorded the model reaching on its own ("the status
    /// artifact indicates that three chapters are missing or out of date; it does not specify which ones
    /// they are"). Scoped, not prohibited: it says what a count IS, which leaves the count sayable, rather
    /// than forbidding a list and leaving the model to work out what remains.
    /// </summary>
    [Theory]
    [InlineData("en", "counts and states, not lists", "which chapters they are is something it does not say")]
    [InlineData("he", "מספרים ומצבים ולא רשימות", "אילו פרקים אלה הוא אינו אומר")]
    public void TheRule_ScopesACountAsACount(string language, string countsNotLists, string notWhich)
    {
        var message = ProductChatPrompt.SystemMessage(language, bookAware: true);

        Assert.Contains(countsNotLists, message, StringComparison.Ordinal);
        Assert.Contains(notWhich, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The invented-count half of F-5 (a scalar the block states literally, restated as "four" in 2 of 3
    /// runs) gets the one instruction that can address it: give the numbers as they are written. Pinned in
    /// both languages because the failure was measured in Hebrew and the block is rendered in English, so
    /// the model is transcribing across scripts.
    /// </summary>
    [Theory]
    [InlineData("en", "Give its numbers exactly as written")]
    [InlineData("he", "מסור את המספרים שלו בדיוק כפי שהם כתובים")]
    public void TheRule_AsksForTheStatusNumbersAsWritten(string language, string asWritten)
        => Assert.Contains(asWritten, ProductChatPrompt.SystemMessage(language, bookAware: true),
                           StringComparison.Ordinal);

    /// <summary>
    /// The invented-PRECONDITION half (telling an author to save chapter text that is already saved,
    /// inventing a cause for staleness) is scoped by tying the next step to the state that calls for it,
    /// rather than by forbidding the inventions one at a time.
    /// </summary>
    [Theory]
    [InlineData("en", "that state plus the next step it calls for")]
    [InlineData("he", "המצב הזה יחד עם הצעד הבא שהוא מחייב")]
    public void TheRule_TiesTheNextStepToTheState(string language, string tied)
        => Assert.Contains(tied, ProductChatPrompt.SystemMessage(language, bookAware: true),
                           StringComparison.Ordinal);

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 2. F-6: this book's reason, as a field and as a rule ───────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// "Why is my review out of date" has ONE answer for a given book and it is written in the status
    /// block. It used to be a parenthetical at the end of a compound line; it is now a named field, which
    /// is what the rule below can point at. English read it 3/3 and Hebrew 0/6 on the same book, so the
    /// legibility of this one field is the narrowest thing f2 could change about that gap.
    /// </summary>
    [Fact]
    public void AStaleReview_NamesItsReason_AsANamedField()
    {
        var block = BookArtifactBlocks.Statuses(Summary(), Review(stale: true), Baseline());

        Assert.Contains(
            "state: BEHIND; reason: the briefs were rebuilt after this review",
            block.Text, StringComparison.Ordinal);

        // VACUITY GUARD: a review that is NOT stale states no reason, so the field above is the state and
        // not a label the renderer emits unconditionally.
        var ready = BookArtifactBlocks.Statuses(Summary(), Review(stale: false), Baseline());
        Assert.DoesNotContain("reason:", ready.Text, StringComparison.Ordinal);
        Assert.Contains("state: up to date", ready.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "where it names a reason, that reason is this book's reason")]
    [InlineData("he", "וכאשר הוא נוקב בסיבה, הסיבה הזו היא הסיבה של הספר הזה")]
    public void TheRule_PointsAWhyQuestionAtTheStatusReason(string language, string clause)
        => Assert.Contains(clause, ProductChatPrompt.SystemMessage(language, bookAware: true),
                           StringComparison.Ordinal);

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 3. F-8: the label keeps its job, and stops being read aloud ────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE LABEL IS NOT THE FIX AND MUST NOT MOVE. It is a d1 section (3) safety property - it is what
    /// decides which grounding shape applies - and it passed 12/12 in g1, including 6/6 on the excerpt
    /// case using the mandated partial-coverage phrasing. It is asserted here EXACTLY as the model sees
    /// it, so a future attempt to solve the leak by softening the label fails a test instead of a gate.
    /// </summary>
    [Fact]
    public void BothChapterTextLabels_StillReachTheModelVerbatim()
    {
        // ASSERTED ON THE HEBREW PATH ON PURPOSE (final-r05). The label is language-INDEPENDENT, and the
        // author-facing name line that sits directly under it is not, so composing this in Hebrew is what
        // makes a future localization that swept the label along with the line fail HERE.
        var whole = BookArtifactBlocks.ChapterText(
            "he", 7, "The Lighthouse", new BookChatExcerpts.Excerpt("Full text.", true, 3), 1);
        var partial = BookArtifactBlocks.ChapterText(
            "he", 7, "The Lighthouse", new BookChatExcerpts.Excerpt("A slice.", false, 3), 1);

        Assert.NotNull(whole);
        Assert.NotNull(partial);
        Assert.Contains("[CHAPTER 7, whole chapter]", whole!.Text, StringComparison.Ordinal);
        Assert.Contains("[CHAPTER 7 EXCERPT, not the whole chapter]", partial!.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// What changed instead is that the prompt says who the labels are FOR. The leak (5 of 6 and 6 of 6
    /// Hebrew runs opening with "בפרק 'המגדלור' (חלקו הנתון כ'EXCERPT')") is the model explaining its own
    /// scaffolding to the author, so the rule states the reader's side of it and hands the distinction
    /// back to the sentence, which the mandated phrasing beside it already supplies.
    /// </summary>
    [Theory]
    [InlineData("en", "the author never sees them", "only your own sentence carries that difference")]
    [InlineData("he", "והמחבר אינו רואה אותן", "רק המשפט שלך מעביר אליו את ההבחנה הזו")]
    public void TheRule_SaysTheLabelsAreNotForTheAuthor(string language, string neverSees, string yourSentence)
    {
        var message = ProductChatPrompt.SystemMessage(language, bookAware: true);

        Assert.Contains(neverSees, message, StringComparison.Ordinal);
        Assert.Contains(yourSentence, message, StringComparison.Ordinal);

        // AND THE MANDATED PARTIAL-COVERAGE PHRASING IS UNTOUCHED beside it: the replacement wording has
        // to exist, or "say it in your sentence" points at nothing.
        Assert.Contains(
            language == "en" ? "the parts you could read do and do not mention" : "מה החלקים שהצלחת",
            message, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 4. F-10: a finding has a name, and the reader has a name ───────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The finding ref is a raw guid because the client's findings ledger routes on it, and that guid was
    /// the only handle the model had for saying which finding it meant. The header now carries the
    /// dimension beside the ref, so "name a finding by its dimension" is something it can do by copying.
    /// </summary>
    [Fact]
    public void AFindingBlock_NamesItsDimensionInTheHeader_BesideTheRoutableGuid()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var block = BookArtifactBlocks.Finding(
            new BookFinding
            {
                Id = id, Dimension = "pacing", Verdict = "needs-work", Severity = 2,
                Status = "open", Rationale = "The middle third slows."
            },
            rank: 1);

        Assert.Contains($"ref=finding:{id:D} the pacing finding", block.Text, StringComparison.Ordinal);
        Assert.Equal($"finding:{id:D}", block.References.Single());
    }

    /// <summary>
    /// g1's `m1` run3 opened by addressing the AUTHOR as "מירב," - a character out of their own book.
    /// Nothing in the prompt had ever said who is being written to, so the artifacts' cast was the only
    /// roster of names in scope. One scoping sentence, in the clause that already establishes what the
    /// artifacts are.
    /// </summary>
    [Theory]
    [InlineData("en", "You are writing to the AUTHOR of this book; the names in these artifacts are the people in it.")]
    [InlineData("he", "אתה כותב אל המחבר של הספר הזה; השמות שבפריטים האלה הם הדמויות שבו.")]
    public void TheRule_SaysWhoIsBeingWrittenTo(string language, string sentence)
    {
        Assert.Contains(sentence, ProductChatPrompt.SystemMessage(language, bookAware: true),
                        StringComparison.Ordinal);

        // And it is book-aware only, so phase A's measured message is untouched.
        Assert.DoesNotContain(sentence, ProductChatPrompt.SystemMessage(language, bookAware: false),
                              StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 5. THE AMBIGUITY THE PROMPT USED TO DISCARD (c1 watch-list item 2) ─────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SELECTOR RECORDS WHAT IT COULD NOT DECIDE. Both candidates were already in
    /// <c>ChapterOrders</c>, and two orders in a list are indistinguishable from a question that named two
    /// chapters, so nothing downstream could tell honest uncertainty from a two-chapter question.
    /// </summary>
    [Fact]
    public void ABareChapterNumber_RecordsTheAmbiguity_WhenBothCandidatesExist()
    {
        var keys = BookArtifactSelector.Select("מה קורה בפרק 5?", Chapters(8), register: null);

        Assert.Equal(new[] { 4, 5 }, keys.ChapterOrders);
        Assert.Equal(new[] { 5 }, keys.AmbiguousChapterNumbers);
    }

    /// <summary>
    /// AND ONLY THEN. At the ends of the book exactly one candidate is real, so there is nothing to
    /// surface and no answer acquires a hedge it has no reason for. "chapter 8" on an 8-chapter book
    /// (orders 0 to 7) can only mean order 7; "chapter 0" can only mean order 0.
    /// </summary>
    [Theory]
    [InlineData("what happens in chapter 8?", new[] { 7 })]
    [InlineData("what happens in chapter 0?", new[] { 0 })]
    public void AChapterNumberWithOneRealCandidate_RecordsNoAmbiguity(string question, int[] expectedOrders)
    {
        var keys = BookArtifactSelector.Select(question, Chapters(8), register: null);

        Assert.Equal(expectedOrders, keys.ChapterOrders);
        Assert.Empty(keys.AmbiguousChapterNumbers);
    }

    /// <summary>
    /// THE NOTE IS COMPUTED FROM THE SURVIVORS, NOT FROM THE SELECTOR'S INTENT. Telling the model "both
    /// were retrieved" about a chapter the trimmer dropped would be a statement about the prompt that the
    /// prompt itself contradicts - the same discipline that makes the acceptable citation set a function
    /// of the surviving blocks.
    /// </summary>
    [Fact]
    public void TheNote_IsEmittedOnlyWhenBothCandidatesActuallyRode()
    {
        var both = new[]
        {
            Block(BookArtifactKind.ChapterText, "chapter-text:4"),
            Block(BookArtifactKind.ChapterText, "chapter-text:5")
        };
        var one = new[] { Block(BookArtifactKind.ChapterText, "chapter-text:5") };

        var withBoth = BookArtifactBlocks.ChapterNumberNote(new[] { 5 }, both);
        Assert.NotNull(withBoth);
        // be-c02: stated in the AUTHOR's numbering (orders 4 and 5 are their chapters 5 and 6), because
        // this is the one line of the BOOK section the model is told to carry into its answer.
        Assert.Contains("the author's chapter 5 or their chapter 6", withBoth!, StringComparison.Ordinal);
        Assert.Contains("which one was meant is not known", withBoth, StringComparison.Ordinal);

        Assert.Null(BookArtifactBlocks.ChapterNumberNote(new[] { 5 }, one));
        Assert.Null(BookArtifactBlocks.ChapterNumberNote(Array.Empty<int>(), both));
        Assert.Null(BookArtifactBlocks.ChapterNumberNote(new[] { 5 }, Array.Empty<BookArtifactBlock>()));
    }

    /// <summary>
    /// A brief and an author summary count as the chapter having ridden, exactly as the raw text does:
    /// the ambiguity is about which CHAPTER the number meant, not about which artifact answered.
    /// A non-chapter ref never satisfies it, which is the trap a prefix-only test would fall into.
    /// </summary>
    [Fact]
    public void EveryChapterKeyedRefCounts_AndNonChapterRefsDoNot()
    {
        var briefs = new[]
        {
            Block(BookArtifactKind.ChapterBrief, "chapter-brief:4"),
            Block(BookArtifactKind.AuthorSummary, "chapter-summary:5")
        };
        Assert.NotNull(BookArtifactBlocks.ChapterNumberNote(new[] { 5 }, briefs));

        var notChapters = new[]
        {
            Block(BookArtifactKind.Status, "status:review"),
            Block(BookArtifactKind.Register, "register"),
            Block(BookArtifactKind.Finding, "finding:" + Guid.Empty.ToString("D"))
        };
        Assert.Null(BookArtifactBlocks.ChapterNumberNote(new[] { 5 }, notChapters));
    }

    /// <summary>
    /// AND IT REACHES THE PROMPT, in the BOOK section where the rule can act on it. Composed through the
    /// real budget loop, because that is where the note is derived and where a note computed against the
    /// pre-trim block list would have gone stale.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void TheNote_ReachesTheComposedInstruction_UnderTheBookMarker(string language)
    {
        var blocks = new[]
        {
            Block(BookArtifactKind.Status, "status:review"),
            Block(BookArtifactKind.ChapterText, "chapter-text:4"),
            Block(BookArtifactKind.ChapterText, "chapter-text:5")
        };

        var withNote = ProductChatBudget.Compose(
            language, new[] { Guide() }, Array.Empty<ProductChatTurn>(), "q",
            budgetTokens: 100_000, blocks, "Salt and Rope", new[] { 5 });

        var withoutNote = ProductChatBudget.Compose(
            language, new[] { Guide() }, Array.Empty<ProductChatTurn>(), "q",
            budgetTokens: 100_000, blocks, "Salt and Rope");

        Assert.Contains("Note: the question says chapter 5", withNote.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Note: ", withoutNote.Instruction, StringComparison.Ordinal);

        // It sits inside the BOOK section, where the rule that governs it says it does.
        var bookAt = withNote.Instruction.IndexOf(ProductChatPrompt.BookMarker, StringComparison.Ordinal);
        var noteAt = withNote.Instruction.IndexOf("Note: ", StringComparison.Ordinal);
        Assert.True(bookAt >= 0 && noteAt > bookAt, "the note must sit inside the BOOK section");
    }

    /// <summary>
    /// And the rule that makes it sayable is in both languages. It is conditional on the note existing, so
    /// an unambiguous chapter question carries no hedge and reads exactly as it did before.
    /// </summary>
    [Theory]
    [InlineData("en", "A note in the BOOK section about what the question could have meant belongs in the answer")]
    [InlineData("he", "הערה במקטע הספר על מה שהשאלה יכלה להתכוון אליו שייכת לתשובה")]
    public void TheRule_MakesTheAmbiguitySayable(string language, string clause)
        => Assert.Contains(clause, ProductChatPrompt.SystemMessage(language, bookAware: true),
                           StringComparison.Ordinal);

    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<BookArtifactSelector.ChapterRef> Chapters(int count)
        => Enumerable.Range(0, count)
            .Select(i => new BookArtifactSelector.ChapterRef(i, $"Chapter title {i}"))
            .ToList();

    private static GuideDocument Guide(string id = "export", string lang = "en")
        => new(id, "stage", "author", "2026-01-01", lang, $"50-{id}.{lang}.md", 50,
               new[] { "# Export" }, $"Body of {id}.");

    private static BookArtifactBlock Block(
        BookArtifactKind kind, string reference, string text = "artifact text", double rank = 0)
        => new(kind, new[] { reference }, $"=== ARTIFACT ref={reference} ===\n{text}", rank);

    private static BookSummaryStatus Summary(
        int total = 10, int built = 10, int stale = 0, bool hasSummary = true, bool covers = true)
        => new()
        {
            TotalChapters = total, BuiltChapters = built, StaleCount = stale,
            HasSummary = hasSummary, SummaryCoversBuiltChapters = covers, Language = "he"
        };

    private static BookReviewStatus Review(
        bool hasBriefs = true, bool hasReview = true, bool stale = false,
        int findings = 9, int open = 4, int resolved = 3)
        => new()
        {
            HasBriefs = hasBriefs, HasReview = hasReview, StaleVsBriefs = stale,
            FindingCount = findings, OpenFindingCount = open, ResolvedFindingCount = resolved,
            ChaptersReviewed = 10, ChaptersTotal = 10, Language = "he"
        };

    private static BookStyleBaselineStatus Baseline(bool has = true, int stale = 0)
        => new() { TotalChapters = 10, BuiltChapters = 10 - stale, StaleCount = stale, HasBaseline = has };
}
