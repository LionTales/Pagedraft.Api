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
/// selector used to deliberately ground BOTH candidates for a bare "chapter N" - honest, because Order
/// is 0-based here and authors count from 1 - and the prompt threw that honesty away. w9 replaced that
/// manufactured pair with deterministic resolution; what still has to survive to the prompt is real
/// ambiguity the book actually has (the same number or title naming two chapters), pinned in section 5
/// below.</para>
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
    /// A BARE CHAPTER NUMBER RESOLVES TO EXACTLY ONE CHAPTER (w9). It used to ground BOTH order 4 and
    /// order 5 for "chapter 5" - the 0-based and 1-based readings kept side by side - which split the one
    /// raw-text slice between two chapters, withheld both of their briefs, and produced an answer that
    /// hedged about a chapter the author had not asked about. The author never had that ambiguity: every
    /// surface in the product shows them a 1-based number, so the Nth chapter is order N-1 and nothing
    /// else.
    /// </summary>
    [Fact]
    public void ABareChapterNumber_ResolvesToOneChapter_ByTheNumberTheAuthorCounts()
    {
        var keys = BookArtifactSelector.Select("מה קורה בפרק 5?", Chapters(8), register: null);

        Assert.Equal(new[] { 4 }, keys.ChapterOrders);
        Assert.Empty(keys.AmbiguousChapterNumbers);

        // And the raw-text slice goes to that one chapter, which is the half of the defect the author felt.
        Assert.Equal(new[] { 4 }, keys.EscalationChapterOrders);
    }

    /// <summary>
    /// AND THE TITLE THE AUTHOR READS OUTRANKS THE COUNTING, which is what makes the resolution right on a
    /// book whose numbering does not line up with its positions. The owner's real manuscript is exactly
    /// that shape: chapters titled "פרק 1".."פרק 31" with a "פרולוג" sitting AFTER them, so position and
    /// title agree only by luck. Here the prologue is first, so they disagree everywhere - and the chapter
    /// the author calls "פרק 5" is the one titled that, never the fifth row.
    /// </summary>
    [Fact]
    public void ANumberedTitle_DecidesTheChapter_EvenWhenItsPositionDisagrees()
    {
        var withPrologue = new List<BookArtifactSelector.ChapterRef>
        {
            new(0, "פרולוג")
        };
        for (var i = 1; i <= 8; i++) withPrologue.Add(new BookArtifactSelector.ChapterRef(i, $"פרק {i}"));

        var keys = BookArtifactSelector.Select("מה קורה בפרק 5?", withPrologue, register: null);

        // Order 5 is the chapter TITLED "פרק 5"; the counting fallback would have said order 4.
        Assert.Equal(new[] { 5 }, keys.ChapterOrders);
        Assert.Empty(keys.AmbiguousChapterNumbers);
        Assert.Empty(keys.UnresolvedChapterNumbers);
        Assert.False(keys.NeedsChapterClarification);
    }

    /// <summary>
    /// A SINGLE-CHAPTER IMPORT TITLED "פרק 24" IS ANSWERABLE, and before w9 it was the one book shape that
    /// could not be. Neither order 23 nor order 24 exists on a one-chapter book, so the number resolved to
    /// nothing and Show asked the author which chapter they meant - about a book with exactly one. This
    /// corpus really contains these (a chapter exported and re-imported on its own).
    /// </summary>
    [Fact]
    public void ASingleChapterImport_ResolvesByItsOwnTitle()
    {
        var oneChapter = new[] { new BookArtifactSelector.ChapterRef(0, "איחוד היסודות פרק 24") };

        var keys = BookArtifactSelector.Select("על מה פרק 24?", oneChapter, register: null);

        Assert.Equal(new[] { 0 }, keys.ChapterOrders);
        Assert.Empty(keys.UnresolvedChapterNumbers);
        Assert.False(keys.NeedsChapterClarification);
    }

    /// <summary>
    /// WHAT REMAINS AMBIGUOUS IS AMBIGUOUS IN THE BOOK ITSELF: two chapters really titled "פרק 2", which
    /// is what a manuscript that restarts its numbering inside each part produces. Then both ride and the
    /// selector says so, because this is the one case where asking is the honest move rather than a
    /// substitute for resolving.
    /// </summary>
    [Fact]
    public void TwoChaptersNamedTheSameNumber_AreRecordedAsAmbiguous()
    {
        var restarted = new[]
        {
            new BookArtifactSelector.ChapterRef(0, "פרק 1"),
            new BookArtifactSelector.ChapterRef(1, "פרק 2"),
            new BookArtifactSelector.ChapterRef(2, "פרק 1"),
            new BookArtifactSelector.ChapterRef(3, "פרק 2")
        };

        var keys = BookArtifactSelector.Select("מה קורה בפרק 2?", restarted, register: null);

        Assert.Equal(new[] { 1, 3 }, keys.ChapterOrders);
        var ambiguity = Assert.Single(keys.AmbiguousChapterNumbers);
        Assert.Equal("chapter 2", ambiguity.Reference);
        Assert.Equal(new[] { 1, 3 }, ambiguity.CandidateOrders);
    }

    /// <summary>
    /// A TITLE MANY CHAPTERS SHARE IDENTIFIES NONE OF THEM, and must not spend the raw-text budget on
    /// whichever two sort first (w9). Measured live before the fix on the owner's 64-chapter book whose
    /// chapters are named for their POV character: "מה קורה בפרק של רוני?" selected 19 chapters, escalated
    /// orders 1 and 3, and reported clarify FALSE - a confident answer about two chapters chosen by sort
    /// order. The briefs still ride (they are the best available answer to a question about רוני) and the
    /// note gives the answer its honest frame: how many chapters bear the name in total, how many of them
    /// the briefs cover, and an offer to narrow to one.
    /// </summary>
    [Fact]
    public void ATitleManyChaptersShare_GroundsButDoesNotSpendRawText()
    {
        var povNamed = new[]
        {
            new BookArtifactSelector.ChapterRef(0, "אילון"),
            new BookArtifactSelector.ChapterRef(1, "רוני"),
            new BookArtifactSelector.ChapterRef(2, "אילון"),
            new BookArtifactSelector.ChapterRef(3, "רוני")
        };

        var keys = BookArtifactSelector.Select("מה קורה בפרק של רוני?", povNamed, register: null);

        Assert.Equal(new[] { 1, 3 }, keys.ChapterOrders);   // still grounds and ranks
        Assert.Empty(keys.EscalationChapterOrders);         // but spends nothing

        var ambiguity = Assert.Single(keys.AmbiguousChapterNumbers);
        Assert.Equal("\"רוני\"", ambiguity.Reference);
        Assert.Equal(new[] { 1, 3 }, ambiguity.CandidateOrders);
    }

    /// <summary>
    /// AND THE CLARIFY CHIPS CANNOT ANSWER THE QUESTION THE NOTE ASKS - the ambient key is IGNORED in
    /// exactly this state (be-c05). The note ends "offer to narrow to one", and the
    /// client's one-click chapter chips answer by re-asking the same sentence with the chosen chapter as
    /// the AMBIENT key. But the ambient branch is guarded on <c>orders.Count == 0</c>, and an ambiguity
    /// exists only because its candidates are already in <c>orders</c>, so supplying one changes nothing:
    /// same selection, same note, same question back. A chip wired to the existing channel here would be
    /// a visible no-op, which is why be-c05 deferred the affordance rather than half-building it - giving
    /// the chips a real action needs a REQUEST-side "the author chose this one" key and a new precedence
    /// tier above the textual reference, not a wire field and a render path.
    ///
    /// <para>WHAT DOES WORK, pinned beside it because the deferral rests on it: the note names the
    /// candidates' POSITIONS, and typing one resolves a single chapter deterministically (w9) and clears
    /// the ambiguity. That is the affordance the author has today.</para>
    ///
    /// <para>THIS PINS CURRENT BEHAVIOUR, NOT A DESIRED END STATE. If the owner takes the deferred
    /// feature, the first assertion is the one that has to change, deliberately.</para>
    /// </summary>
    [Fact]
    public void AnAmbientChapterCannotResolveAnAmbiguity_ButTypingItsPositionCan()
    {
        var povNamed = new[]
        {
            new BookArtifactSelector.ChapterRef(0, "אילון"),
            new BookArtifactSelector.ChapterRef(1, "רוני"),
            new BookArtifactSelector.ChapterRef(2, "אילון"),
            new BookArtifactSelector.ChapterRef(3, "רוני")
        };

        // What a chip tap sends: the same sentence, with one candidate as the ambient key.
        var tapped = BookArtifactSelector.Select(
            "מה קורה בפרק של רוני?", povNamed, register: null, ambientChapterOrder: 3);

        Assert.Equal(new[] { 1, 3 }, tapped.ChapterOrders);          // both candidates, unchanged
        // The key was never consulted.
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, tapped.AmbientMatch);
        Assert.Null(tapped.AmbientChapterOrder);
        Assert.Single(tapped.AmbiguousChapterNumbers);               // so the note fires again

        // What the note tells the author to do instead: name the position. Order 1 is the note's
        // "chapter 2", and it resolves alone.
        var typed = BookArtifactSelector.Select("מה קורה בפרק 2?", povNamed, register: null);

        Assert.Equal(new[] { 1 }, typed.ChapterOrders);
        Assert.Equal(new[] { 1 }, typed.EscalationChapterOrders);
        Assert.Empty(typed.AmbiguousChapterNumbers);
    }

    /// <summary>
    /// AND TWO DIFFERENT TITLES MATCHING ONE QUESTION IS A TWO-CHAPTER QUESTION, not an ambiguity. This is
    /// the distinction the rule above is keyed on - the title STRING, not the match count - and getting it
    /// wrong would silently stop "compare X and Y" from carrying either chapter's prose.
    /// </summary>
    [Fact]
    public void TwoDifferentTitles_ResolveToTwoChapters_AndStillEscalate()
    {
        var distinct = new[]
        {
            new BookArtifactSelector.ChapterRef(0, "האי הנעלם"),
            new BookArtifactSelector.ChapterRef(1, "המגדלור האבוד"),
            new BookArtifactSelector.ChapterRef(2, "הסערה השקטה")
        };

        var keys = BookArtifactSelector.Select(
            "השווה בין האי הנעלם לבין המגדלור האבוד", distinct, register: null);

        Assert.Equal(new[] { 0, 1 }, keys.ChapterOrders);
        Assert.Equal(new[] { 0, 1 }, keys.EscalationChapterOrders);
        Assert.Empty(keys.AmbiguousChapterNumbers);
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

        var withBoth = BookArtifactBlocks.ChapterNumberNote("en", Ambiguity(5, 4, 5), both);
        Assert.NotNull(withBoth);
        // be-c02: stated in the AUTHOR's numbering (orders 4 and 5 are their chapters 5 and 6), because
        // this is the one line of the BOOK section the model is told to carry into its answer.
        Assert.Contains("2 chapters of this book are named chapter 5", withBoth!, StringComparison.Ordinal);
        Assert.Contains("the briefs below cover 2 of them (chapters 5 and 6)", withBoth, StringComparison.Ordinal);
        Assert.Contains(
            "answer from those, say which chapters you are describing and that other chapters share the " +
            "name, and offer to narrow to one",
            withBoth, StringComparison.Ordinal);

        Assert.Null(BookArtifactBlocks.ChapterNumberNote("en", Ambiguity(5, 4, 5), one));
        Assert.Null(BookArtifactBlocks.ChapterNumberNote(
            "en", Array.Empty<BookArtifactSelector.ChapterReferenceAmbiguity>(), both));
        Assert.Null(BookArtifactBlocks.ChapterNumberNote(
            "en", Ambiguity(5, 4, 5), Array.Empty<BookArtifactBlock>()));
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
        Assert.NotNull(BookArtifactBlocks.ChapterNumberNote("en", Ambiguity(5, 4, 5), briefs));

        var notChapters = new[]
        {
            Block(BookArtifactKind.Status, "status:review"),
            Block(BookArtifactKind.Register, "register"),
            Block(BookArtifactKind.Finding, "finding:" + Guid.Empty.ToString("D"))
        };
        Assert.Null(BookArtifactBlocks.ChapterNumberNote("en", Ambiguity(5, 4, 5), notChapters));
    }

    /// <summary>
    /// THE CAP, EXERCISED. A book whose chapters are named for their POV character can share one title 32
    /// ways; this pins the 8-way case the cap exists for (f04, review finding: an unbounded " and "-chain
    /// read as a broken list and "5 and 3 more" invited being parsed as the pair "5 and 3", in the one line
    /// the grounding rule tells the model to carry into the author's answer). All 8 candidates ride, so
    /// <c>MaxNamedAmbiguityCandidates</c> (5) caps <c>shown</c> at the author's chapters 1-5 and the tail
    /// states the remaining <c>rode.Count - 5 == 3</c> as a separate clause the comma keeps from merging
    /// into the list.
    /// </summary>
    /// <remarks>IN BOTH LANGUAGES (be-c04), because the cap, the list join and the overflow tail are three
    /// separate pieces of prose and each of them had to be written twice. The Hebrew row also pins the two
    /// things a half-localized note would get wrong: the reference reads "פרק 1" and not the selector's
    /// English literal "chapter 1", and the list joins with "ו-" rather than " and ".</remarks>
    [Theory]
    [InlineData("en",
        "8 chapters of this book are named chapter 1, and the briefs below cover 8 of them " +
        "(chapters 1, 2, 3, 4 and 5, and 3 more); answer from those, say which chapters you are " +
        "describing and that other chapters share the name, and offer to narrow to one.")]
    [InlineData("he",
        "8 פרקים בספר הזה נקראים פרק 1, והתקצירים שלמטה מכסים 8 מהם (פרקים 1, 2, 3, 4 ו-5, ועוד 3 פרקים); " +
        "ענה מתוכם, אמור על אילו פרקים אתה מדבר ושיש בספר פרקים נוספים באותו שם, והצע לצמצם לפרק אחד.")]
    public void TheNote_CapsAnEightWayCollision_AndStatesTheOverflowSeparately(
        string language, string expected)
    {
        var blocks = Enumerable.Range(0, 8)
            .Select(order => Block(BookArtifactKind.ChapterBrief, $"chapter-brief:{order}"))
            .ToArray();

        var note = BookArtifactBlocks.ChapterNumberNote(language, Ambiguity(1, 0, 1, 2, 3, 4, 5, 6, 7), blocks);

        Assert.NotNull(note);
        Assert.Equal(expected, note);
    }

    /// <summary>
    /// OVERFLOW == 1, WHICH IS THE VALUE A REAL BOOK PRODUCES AND THE ONE NO TEST HELD (final-r04). The cap
    /// is 5, so a collision has to reach SIX candidates before a tail appears at all, and six is what the
    /// owner's manuscript actually has under one title - the gate read "and 1 others" / "ועוד 1 אחרים" off
    /// a live answer. Every fixture in this class jumped from no tail (2 candidates) straight to 3, so the
    /// first value the tail can take was the one cell the suite never rendered.
    ///
    /// <para>Pinned byte-for-byte in both languages rather than by a substring, because the defect was a
    /// disagreement BETWEEN two adjacent words: a Contains on the numeral alone would have passed on the
    /// defective string.</para>
    /// </summary>
    [Theory]
    [InlineData("en",
        "6 chapters of this book are named chapter 1, and the briefs below cover 6 of them " +
        "(chapters 1, 2, 3, 4 and 5, and 1 more); answer from those, say which chapters you are " +
        "describing and that other chapters share the name, and offer to narrow to one.")]
    [InlineData("he",
        "6 פרקים בספר הזה נקראים פרק 1, והתקצירים שלמטה מכסים 6 מהם (פרקים 1, 2, 3, 4 ו-5, ועוד פרק אחד); " +
        "ענה מתוכם, אמור על אילו פרקים אתה מדבר ושיש בספר פרקים נוספים באותו שם, והצע לצמצם לפרק אחד.")]
    public void TheOverflowTail_AgreesWithItsOwnNumber_AtOne(string language, string expected)
    {
        var blocks = Enumerable.Range(0, 6)
            .Select(order => Block(BookArtifactKind.ChapterBrief, $"chapter-brief:{order}"))
            .ToArray();

        var note = BookArtifactBlocks.ChapterNumberNote(language, Ambiguity(1, 0, 1, 2, 3, 4, 5), blocks);

        // VACUITY GUARD: six candidates against a cap of five is what makes the overflow exactly 1, so the
        // row below is the singular cell and not a re-run of the plural one.
        Assert.NotNull(note);
        Assert.Equal(expected, note);
    }

    /// <summary>
    /// AND THE TAIL AT 1, 2 AND 13, WITH ITS CLOSING PARENTHESIS (final-r04). The tail is the last thing
    /// inside the parenthetical, so asserting the bare fragment would pass on a tail that had drifted into
    /// the LIST; including the <c>);</c> pins the position too. 13 is here because Hebrew's plural noun must
    /// not be re-derived per magnitude, and 2 because it is the first plural - the two values either side of
    /// the branch the singular case opened.
    /// </summary>
    [Theory]
    [InlineData("en", 6, ", and 1 more);")]
    [InlineData("en", 7, ", and 2 more);")]
    [InlineData("en", 18, ", and 13 more);")]
    [InlineData("he", 6, ", ועוד פרק אחד);")]
    [InlineData("he", 7, ", ועוד 2 פרקים);")]
    [InlineData("he", 18, ", ועוד 13 פרקים);")]
    public void TheOverflowTail_RendersItsNoun_AtEveryMagnitude(
        string language, int candidates, string expectedTail)
    {
        var blocks = Enumerable.Range(0, candidates)
            .Select(order => Block(BookArtifactKind.ChapterBrief, $"chapter-brief:{order}"))
            .ToArray();

        var note = BookArtifactBlocks.ChapterNumberNote(
            language, Ambiguity(1, Enumerable.Range(0, candidates).ToArray()), blocks);

        Assert.NotNull(note);
        Assert.Contains(expectedTail, note!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SHARED TITLE IS NOT RE-RENDERED, IN EITHER LANGUAGE (be-c04). The number half of the note is a
    /// FRAME the renderer owns, so it is said in the answer's language; the title half is the book's own
    /// DATA, so it rides exactly as the selector resolved it and an English answer about a Hebrew book
    /// still quotes the Hebrew title. This is the line that separates "translate the frame" from "translate
    /// the author's manuscript", and only the first is ever right.
    /// </summary>
    [Theory]
    [InlineData("en",
        "2 chapters of this book are named \"רוני\", and the briefs below cover 2 of them " +
        "(chapters 3 and 8); answer from those, say which chapters you are describing and that other " +
        "chapters share the name, and offer to narrow to one.")]
    [InlineData("he",
        "2 פרקים בספר הזה נקראים \"רוני\", והתקצירים שלמטה מכסים 2 מהם (פרקים 3 ו-8); ענה מתוכם, אמור " +
        "על אילו פרקים אתה מדבר ושיש בספר פרקים נוספים באותו שם, והצע לצמצם לפרק אחד.")]
    public void TheNote_KeepsASharedTitleVerbatim_AndSaysTheRestInTheAnswersLanguage(
        string language, string expected)
    {
        var blocks = new[]
        {
            Block(BookArtifactKind.ChapterText, "chapter-text:2"),
            Block(BookArtifactKind.ChapterText, "chapter-text:7")
        };

        var note = BookArtifactBlocks.ChapterNumberNote(
            language,
            new[] { new BookArtifactSelector.ChapterReferenceAmbiguity("\"רוני\"", new[] { 2, 7 }) },
            blocks);

        Assert.Equal(expected, note);
    }

    /// <summary>
    /// THE TOTAL IS EVERY CHAPTER THAT BEARS THE NAME, NOT THE NUMBER THAT RODE (w9 gate cell A). This is
    /// the one fact the note has and the prompt does not: the chapters that did not survive composition are
    /// precisely what is missing from the BOOK section, so the model cannot count them and an answer built
    /// from the briefs alone reads as complete when it is not. The old note asked instead of answering and
    /// measured 0 of 5 on the owner's manuscript; what closes the defect the author actually felt is
    /// stating how many chapters share the name at all.
    ///
    /// <para>THE TWO NUMBERS MUST DIFFER IN THE FIXTURE OR THE ASSERTION PINS NOTHING. Every other cell in
    /// this class rides every candidate it declares, so <c>total</c> and <c>rode.Count</c> are equal there
    /// and a note that printed either one would pass. This is the owner's real shape: nineteen chapters
    /// titled "רוני", six briefs surviving the budget. The third row parameter is the note the wrong source
    /// would have produced, asserted absent, so a regression to <c>rode.Count</c> cannot hide behind a
    /// coincidence of two equal counts.</para>
    /// </summary>
    [Theory]
    [InlineData("en",
        "19 chapters of this book are named \"רוני\", and the briefs below cover 6 of them " +
        "(chapters 1, 2, 3, 4 and 5, and 1 more); answer from those, say which chapters you are " +
        "describing and that other chapters share the name, and offer to narrow to one.",
        "6 chapters of this book are named")]
    [InlineData("he",
        "19 פרקים בספר הזה נקראים \"רוני\", והתקצירים שלמטה מכסים 6 מהם (פרקים 1, 2, 3, 4 ו-5, ועוד פרק " +
        "אחד); ענה מתוכם, אמור על אילו פרקים אתה מדבר ושיש בספר פרקים נוספים באותו שם, והצע לצמצם לפרק אחד.",
        "6 פרקים בספר הזה נקראים")]
    public void TheNote_StatesTheTotalThatBearTheName_NotTheNumberThatRode(
        string language, string expected, string theRodeCountMisusedAsTheTotal)
    {
        // Six briefs survived; nineteen chapters claim the title.
        var blocks = Enumerable.Range(0, 6)
            .Select(order => Block(BookArtifactKind.ChapterBrief, $"chapter-brief:{order}"))
            .ToArray();
        var ambiguity = new[]
        {
            new BookArtifactSelector.ChapterReferenceAmbiguity(
                "\"רוני\"", Enumerable.Range(0, 19).ToArray())
        };

        var note = BookArtifactBlocks.ChapterNumberNote(language, ambiguity, blocks);

        // VACUITY GUARD: the two counts really are different on this fixture, so the row below can tell
        // "all the candidates" from "the ones that rode" and is not passing on a coincidence.
        Assert.Equal(19, ambiguity[0].CandidateOrders.Count);
        Assert.Equal(6, blocks.Length);

        Assert.NotNull(note);
        Assert.Equal(expected, note);
        Assert.DoesNotContain(theRodeCountMisusedAsTheTotal, note!, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE OTHER TWO NOTES ARE LOCALIZED TOO (be-c04), on the same argument: the grounding clause puts
    /// a note's content in the author's answer, so a note is user-facing by contract and an English one
    /// leaves the model a translation to do. The out-of-range note carries the author's own number and the
    /// book's range; the flat note carries no book data at all and is localized because it shares the
    /// channel, which must not hold two language policies at once.
    /// </summary>
    [Theory]
    [InlineData("en",
        "the question names chapter 40, which this book does not have (this book's chapters are numbered " +
        "1 to 8); say so and ask which chapter was meant.",
        "the question is about a chapter and no chapter was identified: none was named and none is open.")]
    [InlineData("he",
        "השאלה מציינת את פרק 40, שאינו קיים בספר הזה (פרקי הספר הזה ממוספרים מ-1 עד 8); אמור זאת ושאל " +
        "לאיזה פרק התכוונו.",
        "השאלה נוגעת לפרק ולא זוהה אף פרק: לא צוין פרק ואין פרק פתוח.")]
    public void TheTwoClarifyNotes_AreWrittenInTheAnswersLanguage(
        string language, string expectedOutOfRange, string expectedFlat)
    {
        var blocks = new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") };

        var outOfRange = BookArtifactBlocks.BookSectionNote(
            language,
            BookArtifactSelector.BookQuestionKeys.Empty with
            {
                NeedsChapterClarification = true,
                UnresolvedChapterNumbers = new[] { 40 },
                ChapterCount = 8
            },
            blocks);

        var flat = BookArtifactBlocks.BookSectionNote(
            language,
            BookArtifactSelector.BookQuestionKeys.Empty with
            {
                NeedsChapterClarification = true,
                UnresolvedChapterNumbers = Array.Empty<int>(),
                ChapterCount = 8
            },
            blocks);

        Assert.Equal(expectedOutOfRange, outOfRange);
        Assert.Equal(expectedFlat, flat);
    }

    /// <summary>
    /// THE LANGUAGE IS THE ANSWER'S AND NOT THE BOOK'S, pinned on the turn where the two differ (be-c04).
    /// A Hebrew book answered in English is the cross-language turn the <c>g1</c> F-2 fix exists to serve,
    /// and it is reachable through <c>BookChatContextReader</c>, which reads artifacts with
    /// <c>BaselineLanguageResolver.Normalize(book.Language)</c> and renders author-facing lines with
    /// <c>ChatLanguage.Detect</c>. Keying the note on the book would put a Hebrew sentence in that English
    /// answer; this asserts that the note follows the argument the composer was given and nothing else,
    /// with the Hebrew TITLE proving the book itself is Hebrew.
    /// </summary>
    [Fact]
    public void TheNoteFollowsTheAnswersLanguage_EvenWhenTheBooksOwnDataIsHebrew()
    {
        var blocks = new[]
        {
            Block(BookArtifactKind.ChapterText, "chapter-text:2"),
            Block(BookArtifactKind.ChapterText, "chapter-text:7")
        };
        var ambiguity = new[]
        {
            new BookArtifactSelector.ChapterReferenceAmbiguity("\"רוני\"", new[] { 2, 7 })
        };

        var english = BookArtifactBlocks.ChapterNumberNote("en", ambiguity, blocks);
        var hebrew = BookArtifactBlocks.ChapterNumberNote("he", ambiguity, blocks);

        Assert.Contains(
            "2 chapters of this book are named", english!, StringComparison.Ordinal);
        Assert.Contains(
            "2 פרקים בספר הזה נקראים", hebrew!, StringComparison.Ordinal);

        // The whole frame follows the answer, not just its opening: the closing instruction is localized
        // too, and an English arm that had leaked into the Hebrew note would show up here first.
        Assert.Contains("offer to narrow to one", english, StringComparison.Ordinal);
        Assert.Contains("והצע לצמצם לפרק אחד", hebrew, StringComparison.Ordinal);

        // The book's own title is in the note either way: it is data, not frame.
        Assert.Contains("\"רוני\"", english, StringComparison.Ordinal);
        Assert.Contains("\"רוני\"", hebrew, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND IT REACHES THE PROMPT, in the BOOK section where the rule can act on it. Composed through the
    /// real budget loop, because that is where the note is derived and where a note computed against the
    /// pre-trim block list would have gone stale.
    /// </summary>
    [Theory]
    [InlineData("en", "Note: 2 chapters of this book are named chapter 5, and the briefs below cover " +
                      "2 of them (chapters 5 and 6)")]
    [InlineData("he", "Note: 2 פרקים בספר הזה נקראים פרק 5, והתקצירים שלמטה מכסים 2 מהם (פרקים 5 ו-6)")]
    public void TheNote_ReachesTheComposedInstruction_UnderTheBookMarker(string language, string expected)
    {
        var blocks = new[]
        {
            Block(BookArtifactKind.Status, "status:review"),
            Block(BookArtifactKind.ChapterText, "chapter-text:4"),
            Block(BookArtifactKind.ChapterText, "chapter-text:5")
        };

        var withNote = ProductChatBudget.Compose(
            language, new[] { Guide() }, Array.Empty<ProductChatTurn>(), "q",
            budgetTokens: 100_000, blocks, "Salt and Rope",
            BookArtifactSelector.BookQuestionKeys.Empty with
            {
                ChapterOrders = new[] { 4, 5 },
                AmbiguousChapterNumbers = Ambiguity(5, 4, 5)
            });

        var withoutNote = ProductChatBudget.Compose(
            language, new[] { Guide() }, Array.Empty<ProductChatTurn>(), "q",
            budgetTokens: 100_000, blocks, "Salt and Rope");

        // The note reaches the instruction in the ANSWER's language (be-c04), so the Hebrew row is what
        // proves the localization survives composition and not just the renderer's own unit test.
        Assert.Contains(expected, withNote.Instruction, StringComparison.Ordinal);
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

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 6. be-f03: the out-of-range clarify note ("chapter 40" on an 8-chapter book) ───────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE END-TO-END PATH: a real question naming a chapter this book does not have resolves to
    /// <c>UnresolvedChapterNumbers</c> through <see cref="BookArtifactSelector.Select"/>, and
    /// <see cref="BookArtifactBlocks.BookSectionNote"/> turns that into a note naming BOTH the missed
    /// number and the book's actual range. This is the exact shape review measured: 8 chapters titled
    /// "פרק 1".."פרק 8", asked about "פרק 40".
    /// </summary>
    [Fact]
    public void AnOutOfRangeChapterNumber_NamesTheMissAndTheRange_EndToEnd()
    {
        var titled = new List<BookArtifactSelector.ChapterRef>();
        for (var i = 1; i <= 8; i++) titled.Add(new BookArtifactSelector.ChapterRef(i - 1, $"פרק {i}"));

        var keys = BookArtifactSelector.Select("מה קורה בפרק 40?", titled, register: null);

        Assert.Equal(new[] { 40 }, keys.UnresolvedChapterNumbers);
        Assert.True(keys.NeedsChapterClarification);
        Assert.Equal(8, keys.ChapterCount);

        // The question is Hebrew, so the ANSWER is Hebrew and so is the note (be-c04). Composed here
        // through the same detector production uses rather than a hard-coded "he", because the point of
        // the end-to-end row is that nothing between the question and the note picks the language twice.
        var language = ChatLanguage.Detect("מה קורה בפרק 40?");
        Assert.Equal("he", language);

        var note = BookArtifactBlocks.BookSectionNote(
            language, keys, new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") });

        Assert.Contains("השאלה מציינת את פרק 40, שאינו קיים בספר הזה", note, StringComparison.Ordinal);
        Assert.Contains("פרקי הספר הזה ממוספרים מ-1 עד 8", note, StringComparison.Ordinal);

        // And the same selection answered in English says the same two things in English.
        var englishNote = BookArtifactBlocks.BookSectionNote(
            "en", keys, new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") });

        Assert.Contains(
            "the question names chapter 40, which this book does not have",
            englishNote, StringComparison.Ordinal);
        Assert.Contains("this book's chapters are numbered 1 to 8", englishNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE THREE-WAY CHOICE <see cref="BookArtifactBlocks.BookSectionNote"/> MAKES. An unresolved chapter
    /// number picks the out-of-range note over the flat "no chapter identified" one, even though both are
    /// gated on the SAME <c>NeedsChapterClarification</c> flag - the tie-break is
    /// <c>UnresolvedChapterNumbers.Count</c>, not which fired first.
    /// </summary>
    [Fact]
    public void BookSectionNote_PrefersTheOutOfRangeNote_OverTheFlatOne_WhenANumberMissed()
    {
        var blocks = new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") };

        var namedButMissing = BookArtifactSelector.BookQuestionKeys.Empty with
        {
            NeedsChapterClarification = true,
            UnresolvedChapterNumbers = new[] { 40 },
            ChapterCount = 8
        };
        var outOfRangeNote = BookArtifactBlocks.BookSectionNote("en", namedButMissing, blocks);
        Assert.Equal(BookArtifactBlocks.OutOfRangeChapterNote("en", new[] { 40 }, 8), outOfRangeNote);

        var noneNamed = BookArtifactSelector.BookQuestionKeys.Empty with
        {
            NeedsChapterClarification = true,
            UnresolvedChapterNumbers = Array.Empty<int>(),
            ChapterCount = 8
        };
        var flatNote = BookArtifactBlocks.BookSectionNote("en", noneNamed, blocks);
        Assert.Equal(BookArtifactBlocks.NoChapterIdentifiedNote("en"), flatNote);
    }

    /// <summary>
    /// THE MULTI-NUMBER JOIN: a question that names two out-of-range chapters gets one note naming both,
    /// ascending regardless of the order the question (or the selection) produced them in - "chapters 40
    /// and 41", never "41 and 40". The NOUN is plural here and singular on the one-number row above, which
    /// is the branch final-r04 added: the joined list used to sit under a fixed singular ("את פרק 40 ו-41").
    /// </summary>
    [Fact]
    public void BookSectionNote_JoinsMultipleMissedChapterNumbers_Ascending()
    {
        var keys = BookArtifactSelector.BookQuestionKeys.Empty with
        {
            NeedsChapterClarification = true,
            UnresolvedChapterNumbers = new[] { 41, 40 },
            ChapterCount = 8
        };

        var note = BookArtifactBlocks.BookSectionNote(
            "en", keys, new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") });

        Assert.Contains(
            "the question names chapters 40 and 41, which this book does not have",
            note, StringComparison.Ordinal);

        // The join is one of the pieces that had to be written twice (be-c04): Hebrew prefixes its
        // conjunction to the numeral.
        var hebrew = BookArtifactBlocks.BookSectionNote(
            "he", keys, new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") });

        Assert.Contains("השאלה מציינת את פרקים 40 ו-41, שאינם קיימים", hebrew, StringComparison.Ordinal);
    }

    /// <summary>
    /// THREE MISSED NUMBERS GET THE LIST JOIN, NOT THE FLAT AND-CHAIN (final-r01). be-f04 removed
    /// "1 and 2 and 3" from <see cref="BookArtifactBlocks.ChapterNumberNote"/> for reading as a broken
    /// list; this note kept it, and it is reachable - three chapter WORDS in one question produce three
    /// numbers, and on a book that has none of them all three are unresolved and the clarify flag fires.
    /// The two-number rendering above is byte-identical under either join, which is why nothing caught it.
    /// </summary>
    [Fact]
    public void BookSectionNote_JoinsThreeMissedChapterNumbers_AsAList()
    {
        var chapters = Chapters(8);
        var keys = BookArtifactSelector.Select(
            "what happens in chapter 40, in chapter 41 and in chapter 42?", chapters, register: null);

        // VACUITY GUARD: the question really did produce three missed numbers through the real selector,
        // so the rendering below is the shape a reachable turn produces and not a hand-built record.
        Assert.Equal(new[] { 40, 41, 42 }, keys.UnresolvedChapterNumbers);
        Assert.True(keys.NeedsChapterClarification);

        var blocks = new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") };

        Assert.Contains(
            "the question names chapters 40, 41 and 42, which this book does not have",
            BookArtifactBlocks.BookSectionNote("en", keys, blocks), StringComparison.Ordinal);

        Assert.Contains(
            "השאלה מציינת את פרקים 40, 41 ו-42",
            BookArtifactBlocks.BookSectionNote("he", keys, blocks), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NOUN AGREES WITH THE LIST, PINNED ON BOTH SIDES OF THE BRANCH (final-r04). The note used to open
    /// with a fixed singular over a joined list, so two missed numbers shipped "את פרק 40 ו-41" /
    /// "chapter 40 and 41" - one noun for two chapters. The join tests above assert the SEPARATOR and would
    /// have passed on either noun, so the count branch itself had no byte-for-byte cell in any language.
    ///
    /// <para>Hebrew carries the disagreement one word further ("שאינו קיים" for a list), which is why the
    /// plural row asserts the relative clause too; English's "which this book does not have" is
    /// number-neutral and is identical on both rows by design, not by omission.</para>
    /// </summary>
    [Theory]
    [InlineData("en", new[] { 40 },
        "the question names chapter 40, which this book does not have (this book's chapters are " +
        "numbered 1 to 8); say so and ask which chapter was meant.")]
    [InlineData("en", new[] { 41, 40 },
        "the question names chapters 40 and 41, which this book does not have (this book's chapters are " +
        "numbered 1 to 8); say so and ask which chapter was meant.")]
    [InlineData("he", new[] { 40 },
        "השאלה מציינת את פרק 40, שאינו קיים בספר הזה (פרקי הספר הזה ממוספרים מ-1 עד 8); אמור זאת ושאל " +
        "לאיזה פרק התכוונו.")]
    [InlineData("he", new[] { 41, 40 },
        "השאלה מציינת את פרקים 40 ו-41, שאינם קיימים בספר הזה (פרקי הספר הזה ממוספרים מ-1 עד 8); אמור " +
        "זאת ושאל לאיזה פרק התכוונו.")]
    public void TheOutOfRangeNote_AgreesItsNounWithTheNumberOfMissedChapters(
        string language, int[] missed, string expected)
        => Assert.Equal(expected, BookArtifactBlocks.OutOfRangeChapterNote(language, missed, 8));

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 7. be-c01: the note clause defers to the note, for every shape the note has ────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //
    // THE CLAUSE AND THE NOTES USED TO CONTRADICT EACH OTHER IN THE SHIPPED PROMPT. The clause hard-coded
    // two branches ("where it says a number could have meant two chapters, say which one you answered for
    // ...; where it says no chapter was identified, ask ..."), and w9 rewrote what the notes say: the
    // ambiguity note now states the TOTAL that share the name and tells the model to answer from the
    // briefs it has and disclose the scope, it names up to five candidates rather than two, and two of the
    // three note shapes matched neither branch. The rewrite defers to the note instead of enumerating
    // notes, which is exactly why the note's own wording could be rewritten again (w9 gate cell A turned
    // the bare "ask" into "answer and disclose") without the clause needing a line changed.
    //
    // WHAT THESE TESTS DO AND DO NOT PROVE. They pin the ASSEMBLY: both languages carry the new clause,
    // neither carries the answer-for-one wording, and all three note shapes still reach the composed
    // instruction verbatim. They prove NOTHING about what the model does with the new clause; that half
    // is a GPU gate and it has NOT been run. See the clause's own comment in ProductChatPrompt.

    /// <summary>The three shapes <see cref="BookArtifactBlocks.BookSectionNote"/> can put under the BOOK
    /// marker, each with the blocks it needs to survive to the composed instruction. Keyed by name because
    /// <c>InlineData</c> cannot carry a keys record.
    ///
    /// <para>The expected note is spelled out per language rather than obtained from the renderer
    /// (be-c04). A fixture that called <c>BookSectionNote</c> to compute what it then asserts about
    /// <c>BookSectionNote</c> would pass whatever the renderer happened to emit, which is the shape of
    /// vacuity these clause tests exist to avoid.</para></summary>
    private static (BookArtifactSelector.BookQuestionKeys Keys, BookArtifactBlock[] Blocks, string Note)
        NoteShape(string shape, string language) => (shape, hebrew: language == "he") switch
    {
        // (1a) A NUMBER borne by two chapters. Orders 4 and 5 are the author's chapters 5 and 6.
        ("number-ambiguity", var he) => (
            BookArtifactSelector.BookQuestionKeys.Empty with
            {
                ChapterOrders = new[] { 4, 5 },
                AmbiguousChapterNumbers = Ambiguity(5, 4, 5)
            },
            new[]
            {
                Block(BookArtifactKind.ChapterText, "chapter-text:4"),
                Block(BookArtifactKind.ChapterText, "chapter-text:5")
            },
            he
                ? "2 פרקים בספר הזה נקראים פרק 5, והתקצירים שלמטה מכסים 2 מהם (פרקים 5 ו-6); ענה מתוכם, " +
                  "אמור על אילו פרקים אתה מדבר ושיש בספר פרקים נוספים באותו שם, והצע לצמצם לפרק אחד."
                : "2 chapters of this book are named chapter 5, and the briefs below cover 2 of them " +
                  "(chapters 5 and 6); answer from those, say which chapters you are describing and that " +
                  "other chapters share the name, and offer to narrow to one."),

        // (1b) A TITLE borne by two chapters - the shape the old clause's "a number" trigger missed
        // entirely, so it reached the model with no rule at all.
        ("title-ambiguity", var he) => (
            BookArtifactSelector.BookQuestionKeys.Empty with
            {
                ChapterOrders = new[] { 2, 7 },
                AmbiguousChapterNumbers = new[]
                {
                    new BookArtifactSelector.ChapterReferenceAmbiguity("\"רוני\"", new[] { 2, 7 })
                }
            },
            new[]
            {
                Block(BookArtifactKind.ChapterText, "chapter-text:2"),
                Block(BookArtifactKind.ChapterText, "chapter-text:7")
            },
            he
                ? "2 פרקים בספר הזה נקראים \"רוני\", והתקצירים שלמטה מכסים 2 מהם (פרקים 3 ו-8); ענה " +
                  "מתוכם, אמור על אילו פרקים אתה מדבר ושיש בספר פרקים נוספים באותו שם, והצע לצמצם לפרק אחד."
                : "2 chapters of this book are named \"רוני\", and the briefs below cover 2 of them " +
                  "(chapters 3 and 8); answer from those, say which chapters you are describing and that " +
                  "other chapters share the name, and offer to narrow to one."),

        // (2) THE BARE NOTE: it states what is unclear and asks for nothing, so it is the one shape the
        // clause must still supply an instruction for. This is why the second half of the clause exists.
        ("no-chapter-identified", var he) => (
            BookArtifactSelector.BookQuestionKeys.Empty with
            {
                NeedsChapterClarification = true,
                UnresolvedChapterNumbers = Array.Empty<int>(),
                ChapterCount = 8
            },
            new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") },
            he
                ? "השאלה נוגעת לפרק ולא זוהה אף פרק: לא צוין פרק ואין פרק פתוח."
                : "the question is about a chapter and no chapter was identified: none was named and " +
                  "none is open."),

        // (3) A number the book does not have - the other shape the old "a number ... two chapters"
        // branch did not match, and it carries its own instruction.
        ("out-of-range", var he) => (
            BookArtifactSelector.BookQuestionKeys.Empty with
            {
                NeedsChapterClarification = true,
                UnresolvedChapterNumbers = new[] { 40 },
                ChapterCount = 8
            },
            new[] { Block(BookArtifactKind.ChapterText, "chapter-text:0") },
            he
                ? "השאלה מציינת את פרק 40, שאינו קיים בספר הזה (פרקי הספר הזה ממוספרים מ-1 עד 8); אמור " +
                  "זאת ושאל לאיזה פרק התכוונו."
                : "the question names chapter 40, which this book does not have (this book's chapters " +
                  "are numbered 1 to 8); say so and ask which chapter was meant."),

        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown note shape")
    };

    /// <summary>The clause as it must read now, per language: defer to the note, and ask only where the
    /// note itself says no step.</summary>
    private const string DeferringClauseEn =
        "A note in the BOOK section about what the question could have meant belongs in the answer: do " +
        "what the note says, and where it does not say what to do, ask about what remains unclear " +
        "before answering about a particular chapter.";

    private const string DeferringClauseHe =
        "הערה במקטע הספר על מה שהשאלה יכלה להתכוון אליו שייכת לתשובה: עשה מה שההערה אומרת, וכאשר היא " +
        "אינה אומרת מה לעשות, שאל על מה שנותר לא ברור לפני שתענה על פרק מסוים.";

    /// <summary>The wording w9 falsified. Its presence anywhere in the composed instruction is the defect:
    /// it tells the model to answer for one of the candidates while the note it is about tells the model
    /// to ask, and answering for a candidate picked by sort order is what w9 exists to remove.</summary>
    private const string AnswerForOneEn = "say which one you answered for";
    private const string AnswerForOneHe = "אמור על איזה פרק ענית";

    /// <summary>
    /// THE COMPOSED INSTRUCTION NO LONGER TELLS THE MODEL TO ANSWER FOR ONE CANDIDATE, in either language,
    /// on a turn carrying ANY of the three note shapes. Asserted on the composed instruction rather than
    /// on the prompt constant because that is where the clause and the note meet: the collision was
    /// OBSERVED by composing this exact string, not by reading either half alone.
    /// </summary>
    [Theory]
    [InlineData("en", "number-ambiguity")]
    [InlineData("en", "title-ambiguity")]
    [InlineData("en", "no-chapter-identified")]
    [InlineData("en", "out-of-range")]
    [InlineData("he", "number-ambiguity")]
    [InlineData("he", "title-ambiguity")]
    [InlineData("he", "no-chapter-identified")]
    [InlineData("he", "out-of-range")]
    public void TheNoteClause_DefersToTheNote_AndNeverAnswersForOneCandidate(string language, string shape)
    {
        var (keys, blocks, note) = NoteShape(shape, language);

        var composed = ProductChatBudget.Compose(
            language, new[] { Guide() }, Array.Empty<ProductChatTurn>(), "q",
            budgetTokens: 100_000, blocks, "Salt and Rope", keys);

        var clause = language == "he" ? DeferringClauseHe : DeferringClauseEn;
        var answerForOne = language == "he" ? AnswerForOneHe : AnswerForOneEn;

        // VACUITY GUARD: the note really did reach this instruction, so the two assertions below are
        // about a turn where the clause has something to govern and not about an empty BOOK section.
        Assert.Contains("Note: " + note, composed.Instruction, StringComparison.Ordinal);

        Assert.DoesNotContain(answerForOne, composed.Instruction, StringComparison.Ordinal);
        Assert.Contains(clause, composed.Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE SAME TWO FACTS AT THE PROMPT CONSTANT, in both languages, so the pin does not depend on a
    /// book-scoped turn being composable. <see cref="ProductChatComposedSystemSlotTests"/> holds the
    /// byte-for-byte copy of the whole message; this pins the one clause the notes are contracted to.
    /// </summary>
    [Theory]
    [InlineData("en", DeferringClauseEn, AnswerForOneEn)]
    [InlineData("he", DeferringClauseHe, AnswerForOneHe)]
    public void TheNoteClause_IsInBothLanguages_AndTheOldWordingIsGone(
        string language, string clause, string answerForOne)
    {
        var message = ProductChatPrompt.SystemMessage(language, bookAware: true);

        Assert.DoesNotContain(answerForOne, message, StringComparison.Ordinal);
        Assert.Contains(clause, message, StringComparison.Ordinal);
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<BookArtifactSelector.ChapterRef> Chapters(int count)
        => Enumerable.Range(0, count)
            .Select(i => new BookArtifactSelector.ChapterRef(i, $"Chapter title {i}"))
            .ToList();

    /// <summary>One recorded ambiguity: the number the author wrote, and the orders that claim it. The
    /// number rides in the third slot as well as inside the English literal, exactly as the selector
    /// records it, so the note can render it in the answer's language (be-c04).</summary>
    private static IReadOnlyList<BookArtifactSelector.ChapterReferenceAmbiguity> Ambiguity(
        int number, params int[] candidateOrders)
        => new[]
        {
            new BookArtifactSelector.ChapterReferenceAmbiguity($"chapter {number}", candidateOrders, number)
        };

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
