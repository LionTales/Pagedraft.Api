using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE AMBIENT OPEN CHAPTER (chatbot phase B, a1, implementing d2): the state no fixture in this suite
/// has ever held.
///
/// <para>WHY THE FIXTURE IS THE DELIVERABLE AND NOT THE ASSERTION. Three gates and 2,243 deterministic
/// tests were green when the owner asked the first real question against the live API - in Hebrew, about
/// the chapter open in their editor: "זה פרק שעבר עריכה..." - and Show answered out of the faq guide
/// while that chapter's own brief, the author's own summary and five findings sat in the prompt. Nothing
/// failed. All 42 questions across g1 and g2 used EXPLICIT references ("chapter 5", "the lighthouse
/// chapter"); not one said "this chapter", so the corpus never held the way an author actually talks
/// about the chapter in front of them. Every row below is a state that could not be expressed before
/// this change, which is why they are new fixtures rather than new assertions on old ones.</para>
///
/// <para>EACH BEHAVIOUR ROW CARRIES ITS OWN PRE-CHANGE HALF. The ambient key is an argument, so "what
/// this question did before anyone was open on a chapter" is expressible in the same test: the paired
/// call with no ambient chapter IS the shipped behaviour, and it resolves nothing. A row that only
/// showed the new behaviour would prove the feature runs, not that it changed anything.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE: the selector is pure and every input here is a
/// literal. The other two seams - the ambient key reaching the READER, and the clarify flag reaching the
/// CLIENT and the prompt - are pinned in <c>ProductChatAmbientWireTests</c>, and the database-backed
/// half (id-to-order reconciliation, and an ambient chapter that excerpts) in
/// <c>ProductChatBookRetrievalTests</c>.</para>
///
/// <para>EVERY "no offenders" ASSERTION IS PAIRED WITH A VACUITY GUARD, for the reason this codebase
/// records: a loader that silently returned an empty array once greened every gold test that read it.</para>
/// </summary>
public class ProductChatAmbientChapterTests
{
    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The owner's real question, verbatim from the live session that found this defect. It
    /// names no chapter number and no title; the only thing that says WHICH chapter it is about is the
    /// chapter that was open on screen.</summary>
    private const string OwnersQuestion =
        "זה פרק שעבר עריכה אחרי שאמרו לי שהסתרה בו סכנה וחסרים קונפליקטים. האם עכשיו מרגישים אותם?";

    private static IReadOnlyList<BookArtifactSelector.ChapterRef> Chapters() => new[]
    {
        new BookArtifactSelector.ChapterRef(0, "The Arrival"),
        new BookArtifactSelector.ChapterRef(1, "פרק 2"),
        new BookArtifactSelector.ChapterRef(2, "Salt and Rope"),
        new BookArtifactSelector.ChapterRef(3, "המנעול השבור"),
        new BookArtifactSelector.ChapterRef(4, "Chapter 5"),
        new BookArtifactSelector.ChapterRef(5, "הפרידה"),
        new BookArtifactSelector.ChapterRef(6, "Low Tide"),
        new BookArtifactSelector.ChapterRef(7, "Last Light")
    };

    /// <summary>The owner's real book shape: ONE chapter, titled "פרק 28".</summary>
    private static IReadOnlyList<BookArtifactSelector.ChapterRef> SingleChapterBook() => new[]
    {
        new BookArtifactSelector.ChapterRef(0, "פרק 28")
    };

    private static CharacterRegister Register() => new()
    {
        Characters = new[]
        {
            new CharacterRegisterEntry
            {
                Name = "Miriam", Role = "protagonist", Gender = "female",
                Aliases = new[] { "Mimi" }, GenderConfirmed = true
            },
            new CharacterRegisterEntry { Name = "Dov", Role = "antagonist" },
            new CharacterRegisterEntry { Name = "אליהו", Aliases = new[] { "אליהו הזקן" } }
        }
    };

    /// <summary>The gate corpus's Hebrew register: the name g3's positional questions actually used.</summary>
    private static CharacterRegister HebrewRegister() => new()
    {
        Characters = new[] { new CharacterRegisterEntry { Name = "מירב", Role = "protagonist" } }
    };

    /// <summary>Selection with a chapter OPEN on screen.</summary>
    private static BookArtifactSelector.BookQuestionKeys WithAmbient(string question, int ambientOrder)
        => BookArtifactSelector.Select(question, Chapters(), Register(), ambientOrder);

    /// <summary>Selection with NOTHING open: the shipped, pre-ambient behaviour of the same question.</summary>
    private static BookArtifactSelector.BookQuestionKeys WithoutAmbient(string question)
        => BookArtifactSelector.Select(question, Chapters(), Register(), ambientChapterOrder: null);

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 1. A DEICTIC QUESTION WITH A CHAPTER OPEN: resolves, and earns its raw text ────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TIER 1, THE ROW THIS PLAN EXISTS FOR. A deictic marker beside a location word resolves to the open
    /// chapter and escalates it on the same terms as an explicit reference (d2 sections (2) and (4)), so
    /// the question the owner asked can reach the chapter's PROSE. Their question needs the prose:
    /// whether conflicts land after an edit is not answerable from a summary, and the escalation built
    /// for exactly that could not fire because nothing said which chapter.
    ///
    /// <para>THE SECOND HALF IS THE DEFECT. The identical question with no chapter open resolves nothing
    /// and escalates nothing, which is what shipped and what the live log recorded as "selected chapters
    /// []".</para>
    /// </summary>
    [Theory]
    [InlineData(OwnersQuestion, 0)]
    [InlineData("בפרק הזה יש מספיק מתח?", 3)]
    [InlineData("הפרק הזה מרגיש איטי לי", 3)]
    [InlineData("הפרק שאני עורך מרגיש איטי", 5)]
    [InlineData("Does the conflict land in this chapter?", 3)]
    [InlineData("Is the current chapter too slow?", 6)]
    public void ADeicticQuestion_ResolvesTheOpenChapter_AndEscalatesIt(string question, int open)
    {
        var keys = WithAmbient(question, open);

        Assert.Equal(new[] { open }, keys.ChapterOrders);
        Assert.Equal(new[] { open }, keys.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.Deictic, keys.AmbientMatch);
        Assert.Equal(open, keys.AmbientChapterOrder);
        Assert.True(keys.HasLocationCue);

        // THE ANTI-RULE: a resolved chapter never asks the author which chapter they meant.
        Assert.False(keys.NeedsChapterClarification);

        // PRE-CHANGE: the same question, with nothing open, is the shipped behaviour - and it is the
        // defect. No chapter, no escalation, so the prose could never reach the prompt.
        var shipped = WithoutAmbient(question);
        Assert.Empty(shipped.ChapterOrders);
        Assert.Empty(shipped.EscalationChapterOrders);
    }

    /// <summary>
    /// AND THE MARKER IS SEEN THROUGH THE RAW TEXT, WHICH IS THE ONE IMPLEMENTATION DETAIL d2 HAD TO
    /// CORRECT. <c>GuideSelector</c>'s stop list drops English "this" and Hebrew "זה"/"זו", so
    /// <c>Tokenize("this chapter")</c> and <c>Tokenize("the chapter")</c> are the SAME set and a
    /// token-based marker match is impossible by construction. This pins the consequence rather than the
    /// mechanism: the two questions differ ONLY in the marker, and they resolve to different tiers.
    /// </summary>
    [Fact]
    public void TheMarkerWord_IsWhatSeparatesTier1FromTier3_ThoughTheTokenizerCannotSeeIt()
    {
        // "this chapter" and "the chapter" produce the SAME token set, so no token-based rule could tell
        // them apart...
        Assert.Equal(
            GuideSelector.Tokenize("what happens in the chapter").OrderBy(t => t, StringComparer.Ordinal),
            GuideSelector.Tokenize("what happens in this chapter").OrderBy(t => t, StringComparer.Ordinal));

        // ...and the Hebrew marker in the owner's own question is dropped by the same stop list.
        Assert.DoesNotContain("זה", GuideSelector.Tokenize(OwnersQuestion));

        // ...yet the marker decides the tier, because it is matched against the RAW question.
        Assert.Equal(
            BookArtifactSelector.AmbientChapterMatch.Deictic,
            WithAmbient("what happens in this chapter", 3).AmbientMatch);
        Assert.Equal(
            BookArtifactSelector.AmbientChapterMatch.BareNounAlone,
            WithAmbient("what happens in the chapter", 3).AmbientMatch);
        Assert.Equal(
            BookArtifactSelector.AmbientChapterMatch.Deictic,
            WithAmbient(OwnersQuestion, 0).AmbientMatch);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 2. A DEICTIC QUESTION WITH NOTHING OPEN: the clarifying question, as a FALLBACK ────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The clarifying question is what happens when nothing resolves, and only then (d2 section (5)). It
    /// is computed from the selection with NO model call, so the client can offer chapter chips instead
    /// of making the author retype the question.
    /// </summary>
    [Theory]
    [InlineData(OwnersQuestion)]
    [InlineData("בפרק הזה יש מספיק מתח?")]
    [InlineData("Does the conflict land in this chapter?")]
    [InlineData("Is the current chapter too slow?")]
    public void ADeicticQuestionWithNoChapterOpen_AsksWhichChapter(string question)
    {
        var keys = WithoutAmbient(question);

        Assert.Empty(keys.ChapterOrders);
        Assert.Empty(keys.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, keys.AmbientMatch);
        Assert.True(keys.NeedsChapterClarification);

        // VACUITY GUARD: the SAME question with a chapter open does not ask - so the flag is the ambient
        // key's absence and not a question shape that always asks.
        Assert.False(WithAmbient(question, 3).NeedsChapterClarification);
    }

    /// <summary>
    /// AN AMBIENT ORDER THIS BOOK DOES NOT HAVE IS NOT A CHAPTER. A stale client, or a chapter deleted
    /// since the editor loaded the book, must degrade to "nothing is open" and not to a selection of
    /// whatever order that number now names.
    /// </summary>
    [Fact]
    public void AnAmbientOrderOutsideTheBook_ResolvesNothing_AndFallsBackToAsking()
    {
        var keys = WithAmbient("Does the conflict land in this chapter?", 99);

        Assert.Empty(keys.ChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, keys.AmbientMatch);
        Assert.True(keys.NeedsChapterClarification);

        // VACUITY GUARD: a real order of this book DOES resolve the same question.
        Assert.Equal(new[] { 3 }, WithAmbient("Does the conflict land in this chapter?", 3).ChapterOrders);
    }

    /// <summary>
    /// A BOOK WITH ONE CHAPTER NEVER ASKS WHICH CHAPTER. The owner's real book is exactly this shape (one
    /// chapter, "פרק 28"), and a clarifying question there would be absurd: there is nothing to
    /// disambiguate, and the book-order fallback already grounds in the only chapter there is. Enforced
    /// in the selector so it is impossible on the API and the client alike, rather than hidden on one.
    /// </summary>
    [Theory]
    [InlineData(OwnersQuestion)]
    [InlineData("מה קורה בפרק?")]
    [InlineData("Does the conflict land in this chapter?")]
    public void AOneChapterBook_NeverAsksWhichChapter(string question)
    {
        var keys = BookArtifactSelector.Select(
            question, SingleChapterBook(), Register(), ambientChapterOrder: null);

        Assert.False(keys.NeedsChapterClarification);

        // VACUITY GUARD: the same question, the same absent ambient key, on a book with more than one
        // chapter DOES ask - so the silence above is the one-chapter rule and not a dead flag.
        Assert.True(WithoutAmbient(question).NeedsChapterClarification);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 3. EXPLICIT BEATS AMBIENT: the regression that would answer about the wrong chapter ────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NAMING CHAPTER 5 WHILE CHAPTER 3 IS OPEN MUST ANSWER ABOUT 5. This is the failure that costs the
    /// most: a confident, cited answer about the wrong chapter of the author's own manuscript is
    /// fabrication with a citation attached.
    ///
    /// <para>It holds STRUCTURALLY, not by score: the ambient branch runs under a guard that an explicit
    /// resolution has already falsified, so it is unreachable rather than out-ranked. The assertion is
    /// therefore that the open chapter is ABSENT from the selection entirely, not merely ranked below.</para>
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 5?", 2)]
    [InlineData("מה קורה בפרק 5?", 2)]
    [InlineData("What does Salt and Rope establish?", 7)]
    public void AnExplicitReference_WinsOverTheOpenChapter(string question, int open)
    {
        var keys = WithAmbient(question, open);

        Assert.DoesNotContain(open, keys.ChapterOrders);
        Assert.DoesNotContain(open, keys.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, keys.AmbientMatch);
        Assert.Null(keys.AmbientChapterOrder);
        Assert.False(keys.NeedsChapterClarification);

        // VACUITY GUARD ONE: the explicit reference really did resolve, so the absence above is the
        // precedence rule and not a question that selected nothing at all.
        Assert.NotEmpty(keys.ChapterOrders);

        // VACUITY GUARD TWO: that same open chapter IS live on this call - a deictic question resolves
        // it - so the ambient key was supplied and ignored, not silently dropped by the harness.
        Assert.Equal(new[] { open }, WithAmbient("what happens in this chapter?", open).ChapterOrders);
    }

    /// <summary>
    /// AND AN EXPLICIT REFERENCE THAT FAILED TO RESOLVE STILL WINS (d2 section (5)'s flagged widening,
    /// TAKEN). "Chapter 40" on an 8-chapter book used to be absorbed in silence: nothing entered the
    /// selection and no signal survived anywhere. With an ambient chapter in play that silence would let
    /// the open chapter answer in place of the one the author named, which is the same wrong-chapter
    /// failure through a quieter door. It blocks the substitution AND asks.
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 40?")]
    [InlineData("מה קורה בפרק 40?")]
    public void AnExplicitNumberTheBookDoesNotHave_BlocksTheOpenChapter_AndAsks(string question)
    {
        var keys = WithAmbient(question, 3);

        Assert.Empty(keys.ChapterOrders);
        Assert.Empty(keys.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, keys.AmbientMatch);
        Assert.Equal(new[] { 40 }, keys.UnresolvedChapterNumbers);
        Assert.True(keys.NeedsChapterClarification);

        // VACUITY GUARD: the same shape with an IN-RANGE number resolves explicitly and never asks.
        var inRange = WithAmbient(question.Replace("40", "4"), 3);
        Assert.NotEmpty(inRange.ChapterOrders);
        Assert.False(inRange.NeedsChapterClarification);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 4. A LOCATION-FREE QUESTION SPENDS NOTHING, even with a chapter open ───────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// d1's "the briefs stay the default and the budget is spent only where the question earns it",
    /// surviving intact. A question that names no location and carries no deictic marker never reaches
    /// any ambient tier, so a chapter merely BEING open changes nothing about what is selected or spent.
    /// This is the rule d2 called the most likely to be eroded by a careless implementation.
    /// </summary>
    [Theory]
    [InlineData("Who is Miriam?")]
    [InlineData("What did the review say about pacing?")]
    [InlineData("מי זו מרים?")]
    [InlineData("How do I export my book?")]
    public void ALocationFreeQuestion_SelectsAndEscalatesNothing_EvenWithAChapterOpen(string question)
    {
        var open = WithAmbient(question, 3);
        var closed = WithoutAmbient(question);

        Assert.Empty(open.ChapterOrders);
        Assert.Empty(open.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, open.AmbientMatch);
        Assert.False(open.NeedsChapterClarification);

        // AND EVERY OTHER KEY IS IDENTICAL to the same question with nothing open, not just the two
        // asserted above, so a future tier cannot start leaking into a question that earned nothing.
        // Compared field by field on purpose: the record's members are lists, and a record's generated
        // equality compares those by REFERENCE, so an == between two selections is always false and would
        // make this the strongest-looking assertion in the file while proving nothing.
        Assert.Equal(closed.ChapterOrders, open.ChapterOrders);
        Assert.Equal(closed.CharacterNames, open.CharacterNames);
        Assert.Equal(closed.Dimensions, open.Dimensions);
        Assert.Equal(closed.EscalationChapterOrders, open.EscalationChapterOrders);
        Assert.Equal(closed.AmbiguousChapterNumbers, open.AmbiguousChapterNumbers);
        Assert.Equal(closed.UnresolvedChapterNumbers, open.UnresolvedChapterNumbers);
        Assert.Equal(closed.HasLocationCue, open.HasLocationCue);
        Assert.Equal(closed.AmbientMatch, open.AmbientMatch);
        Assert.Equal(closed.AmbientChapterOrder, open.AmbientChapterOrder);
        Assert.Equal(closed.NeedsChapterClarification, open.NeedsChapterClarification);
    }

    /// <summary>
    /// THE ONE ESCALATION PATH THAT ALREADY EXISTED IS UNTOUCHED BY THE OPEN CHAPTER (d2 section (3.3)).
    /// A character name paired with a positional cue escalates to that END of the book, and it must keep
    /// doing exactly that with a chapter open: bucket (f) measured this shape identical across two gates,
    /// and (f) licenses Wave 3's w7. Positional words are deliberately NOT wired to the ambient key.
    /// </summary>
    [Theory]
    [InlineData("Does Miriam appear in the first chapter?", 0)]
    [InlineData("How does Miriam's story end?", 7)]
    public void APositionalEscalation_IsUnchangedByTheOpenChapter(string question, int expected)
    {
        Assert.Equal(new[] { expected }, WithoutAmbient(question).EscalationChapterOrders);
        Assert.Equal(new[] { expected }, WithAmbient(question, 3).EscalationChapterOrders);
    }

    /// <summary>A bare positional question does not acquire the open chapter either: "how does it end"
    /// with chapter 3 open must not silently pull chapter 3.</summary>
    [Fact]
    public void ABarePositionalQuestion_DoesNotPullTheOpenChapter()
    {
        var keys = WithAmbient("How does it end?", 3);

        Assert.Empty(keys.ChapterOrders);
        Assert.Empty(keys.EscalationChapterOrders);

        // VACUITY GUARD: the same open chapter resolves for a question that earns it.
        Assert.Equal(new[] { 3 }, WithAmbient("How does this chapter end?", 3).ChapterOrders);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 5. THE BARE-NOUN TIERS: recall at tier 2 and 3, spend at tier 2 only ───────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TIER 2: a bare location word plus another book-content signal - a resolved character or a review
    /// dimension - is escalation-eligible, because the co-occurring signal is itself evidence that this is
    /// a question about the book's content.
    ///
    /// <para>A POSITIONAL WORD IS NO LONGER ONE OF THOSE SIGNALS (x2, closing g3's (g-1)). d2 section (2)
    /// listed it and section (3.3) forbids it; the gate measured the section (2) reading escalating the
    /// OPEN chapter for a question about the first/last one. "How does the chapter end?" therefore moved
    /// from this row to the tier-3 row below, where it grounds without spending raw text.</para>
    /// </summary>
    [Theory]
    [InlineData("What does Miriam do in the chapter?")]
    [InlineData("Is the pacing of the chapter working?")]
    [InlineData("מה קורה עם הקצב בפרק?")]
    [InlineData("מה אליהו עושה בפרק?")]
    public void TheBareNounWithAnotherSignal_ResolvesAndEscalates(string question)
    {
        var keys = WithAmbient(question, 3);

        Assert.Equal(new[] { 3 }, keys.ChapterOrders);
        Assert.Equal(new[] { 3 }, keys.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.BareNounWithSignal, keys.AmbientMatch);
        Assert.False(keys.NeedsChapterClarification);
    }

    /// <summary>
    /// TIER 3: the bare location word ALONE grounds but does not spend. It stays in the selection, so a
    /// bare "what happens in the chapter" prefers the open chapter's brief over the book-order fallback
    /// the ranker otherwise uses - the owner's whole complaint is that this is how people talk - while
    /// the raw-text budget is NOT committed to the single most generic and most product-question-prone
    /// phrasing.
    ///
    /// <para>The last row is the shape d2 explicitly flagged for g3: a PRODUCT question that happens to
    /// use the singular word "chapter" while a chapter is open. Tier 3 is what bounds its cost to a
    /// ranking preference.</para>
    /// </summary>
    [Theory]
    [InlineData("What happens in the chapter?")]
    [InlineData("מה קורה בפרק?")]
    [InlineData("How do I split a chapter?")]
    [InlineData("איך מפצלים פרק?")]
    // A POSITION INSIDE THE CHAPTER IS NOT A CHAPTER ORDINAL, and these two rows are where that
    // distinction is spent (x2). "The end OF the chapter" is a question about the chapter in front of the
    // author; "the LAST chapter" is a question about a different one. Only the second blocks the ambient
    // key, so these still ground - they just no longer buy raw text off a positional word.
    [InlineData("How does the chapter end?")]
    [InlineData("מה קורה בסוף הפרק?")]
    public void TheBareNounAlone_Grounds_ButDoesNotSpendTheRawTextBudget(string question)
    {
        var keys = WithAmbient(question, 3);

        Assert.Equal(new[] { 3 }, keys.ChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.BareNounAlone, keys.AmbientMatch);
        Assert.False(keys.NeedsChapterClarification);

        // THE SPEND IS THE POINT OF THE TIER: no raw text.
        Assert.Empty(keys.EscalationChapterOrders);

        // VACUITY GUARD: the same open chapter, the same book, a question carrying a marker word DOES
        // escalate - so the empty set above is the tier rule and not an ambient key that never escalates.
        Assert.Equal(new[] { 3 }, WithAmbient("what happens in this chapter?", 3).EscalationChapterOrders);
    }

    /// <summary>
    /// THE PLURAL IS EXCLUDED FROM EVERY AMBIENT RULE. "כמה פרקים יש בספר" is a question about the whole
    /// book, so narrowing it to the one chapter that happens to be open would answer a different question
    /// AND spend an escalation on it. English needs no twin: "chapters" is not in the location vocabulary
    /// and the whole-word check already refuses to match "chapter" inside it.
    /// </summary>
    [Theory]
    [InlineData("כמה פרקים יש בספר?")]
    [InlineData("How many chapters are in my book?")]
    public void ThePluralLocationWord_NeverResolvesTheOpenChapter(string question)
    {
        var keys = WithAmbient(question, 3);

        Assert.Empty(keys.ChapterOrders);
        Assert.Empty(keys.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, keys.AmbientMatch);

        // AND IT DOES NOT ASK EITHER: a book-wide question is not a question whose chapter is unclear.
        Assert.False(keys.NeedsChapterClarification);

        // VACUITY GUARD: the SINGULAR of the same word, same book, same open chapter, does resolve.
        Assert.Equal(new[] { 3 }, WithAmbient("מה קורה בפרק?", 3).ChapterOrders);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 6. A CHAPTER NAMED BY POSITION, WITH A CHAPTER OPEN (x2, closing g3's (g-1)) ───────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE STATE NO FIXTURE HELD: a positional question asked WHILE A CHAPTER IS OPEN. a1's rows covered
    /// the positional PAIR (a character name plus a position) and the bare positional with no location
    /// word; neither expresses "מה קורה בפרק האחרון?" with chapter 3 open, which is exactly where d2
    /// section (3.3) and d2 section (2)'s tier-2 list disagree. g3 measured the disagreement live:
    /// <c>sel[3] BareNounWithSignal(used: 3) clarify: False; escalated whole [3]</c> on 18 runs of 18, an
    /// escalation slice spent on the wrong chapter and the clarifying question suppressed. Only the model
    /// declining kept a wrong-chapter answer off the screen, which is not a guarantee.
    ///
    /// <para>NAMING A CHAPTER BY POSITION IS NAMING A CHAPTER, so it blocks the ambient substitution on
    /// the same terms as an out-of-range explicit number - and then the clarifying question, which is the
    /// thing that would actually have found the right chapter, is free to fire.</para>
    /// </summary>
    [Theory]
    [InlineData("מה קורה בפרק האחרון?", 3)]        // g3's gp2, verbatim
    [InlineData("What happens in the first chapter?", 4)]   // g3's gp3, verbatim
    [InlineData("What happens in the last chapter?", 3)]    // g3's gp4
    [InlineData("מה קורה בפרק הראשון?", 5)]        // g3's gp5
    [InlineData("איך נגמר הפרק האחרון?", 0)]
    public void AChapterNamedByPosition_BlocksTheOpenChapter_AndAsks(string question, int open)
    {
        var keys = WithAmbient(question, open);

        Assert.Empty(keys.ChapterOrders);
        Assert.Empty(keys.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, keys.AmbientMatch);
        Assert.Null(keys.AmbientChapterOrder);
        Assert.True(keys.NeedsChapterClarification);

        // VACUITY GUARD: that open chapter is live on this call - a deictic question resolves it - so the
        // empty selection above is the positional rule and not an ambient key the harness never supplied.
        Assert.Equal(new[] { open }, WithAmbient("what happens in this chapter?", open).ChapterOrders);
    }

    /// <summary>
    /// AND THE OPEN CHAPTER CHANGES NOTHING AT ALL ABOUT SUCH A QUESTION - every key, not just the two
    /// asserted above. This is d2 section (3.3) stated as the property it actually is ("positional words
    /// are NOT wired to the ambient key"), and it is the shape of the control g3 ran alongside the defect:
    /// the same questions with nothing open escalated nothing and produced substantively the same answers.
    /// Compared field by field because the record's lists compare by reference.
    /// </summary>
    [Theory]
    [InlineData("מה קורה בפרק האחרון?")]
    [InlineData("What happens in the first chapter?")]
    public void AChapterNamedByPosition_SelectsIdentically_OpenOrNot(string question)
    {
        var open = WithAmbient(question, 3);
        var closed = WithoutAmbient(question);

        Assert.Equal(closed.ChapterOrders, open.ChapterOrders);
        Assert.Equal(closed.CharacterNames, open.CharacterNames);
        Assert.Equal(closed.Dimensions, open.Dimensions);
        Assert.Equal(closed.EscalationChapterOrders, open.EscalationChapterOrders);
        Assert.Equal(closed.AmbiguousChapterNumbers, open.AmbiguousChapterNumbers);
        Assert.Equal(closed.UnresolvedChapterNumbers, open.UnresolvedChapterNumbers);
        Assert.Equal(closed.HasLocationCue, open.HasLocationCue);
        Assert.Equal(closed.AmbientMatch, open.AmbientMatch);
        Assert.Equal(closed.AmbientChapterOrder, open.AmbientChapterOrder);
        Assert.Equal(closed.NeedsChapterClarification, open.NeedsChapterClarification);

        // VACUITY GUARD: the clarify flag really is TRUE for this question, so the equality above is two
        // matching real answers and not two matching defaults.
        Assert.True(open.NeedsChapterClarification);
    }

    /// <summary>
    /// A DEICTIC MARKER STILL OUTRANKS A CHAPTER ORDINAL, because tier 1 sits above the block. "Is this
    /// the last chapter?" carries both, and the marker is the word that says which chapter is meant: the
    /// one on screen. Without this row the fix for (g-1) would have quietly taken the owner's own tier -
    /// the tier their driving question uses - away from any question that happens to contain "last".
    /// </summary>
    [Theory]
    [InlineData("Is this the last chapter?")]
    [InlineData("האם זה הפרק האחרון?")]
    [InlineData("האם הפרק הזה הוא הראשון?")]
    public void ADeicticMarker_OutranksAChapterOrdinal(string question)
    {
        var keys = WithAmbient(question, 3);

        Assert.Equal(new[] { 3 }, keys.ChapterOrders);
        Assert.Equal(new[] { 3 }, keys.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.Deictic, keys.AmbientMatch);
        Assert.False(keys.NeedsChapterClarification);
    }

    /// <summary>
    /// THE POSITIONAL PAIR IS UNTOUCHED BY THE BLOCK, asserted on the ESCALATION SET rather than on the
    /// tier, because that set is what bucket (f) measured identical across three gates and what licenses
    /// Wave 3's w7. A character name plus a position still resolves to that END of the book, with a
    /// chapter open and without, and the position that resolves it is read from the FULL positional
    /// vocabulary - both the chapter ordinals ("first") and the within-unit words ("end").
    /// </summary>
    [Theory]
    [InlineData("Does Miriam appear in the first chapter?", 0)]
    [InlineData("How does Miriam's story end?", 7)]
    [InlineData("Does Miriam appear in the last chapter?", 7)]
    public void ThePositionalPair_IsUnchangedByTheBlock(string question, int expected)
    {
        Assert.Equal(new[] { expected }, WithoutAmbient(question).EscalationChapterOrders);
        Assert.Equal(new[] { expected }, WithAmbient(question, 3).EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, WithAmbient(question, 3).AmbientMatch);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 7. RAW TEXT THAT WAS CARRIED SILENCES THE QUESTION TOO (x2, closing g3's (g-2)) ────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SECOND STATE NO FIXTURE HELD: the clarify predicate evaluated against a NON-EMPTY escalation
    /// set. The positional pair is the one selection where a chapter's raw text is carried without that
    /// chapter ever entering <c>ChapterOrders</c>, so the predicate's old "nothing resolved" test read
    /// TRUE while the answer was being built from that chapter's own prose. g3 measured the result on the
    /// live API, 3 runs of 3: a complete, correct, <c>chapter-text:0</c>-grounded answer about Miriam on
    /// the Haifa pier, with "which chapter did you mean?" chips rendered underneath it.
    ///
    /// <para>A question answered from a chapter's own prose is not a question that needs asking.</para>
    /// </summary>
    [Theory]
    [InlineData("Does Miriam appear in the first chapter?", 0)]
    [InlineData("How does Miriam's story end?", 7)]
    public void AQuestionWhoseRawTextWasCarried_NeverAsksWhichChapter(string question, int escalated)
    {
        var keys = WithAmbient(question, 5);

        // The state itself: nothing RESOLVED, and yet a chapter's prose is on its way into the prompt.
        Assert.Empty(keys.ChapterOrders);
        Assert.Equal(new[] { escalated }, keys.EscalationChapterOrders);

        Assert.False(keys.NeedsChapterClarification);
        Assert.False(WithoutAmbient(question).NeedsChapterClarification);

        // VACUITY GUARD: the same shape MINUS the register-resolved name escalates NOTHING and DOES ask -
        // so the silence above is the escalation set and not a question shape that can never ask.
        var withoutTheName = WithAmbient("Does anyone appear in the first chapter?", 5);
        Assert.Empty(withoutTheName.EscalationChapterOrders);
        Assert.True(withoutTheName.NeedsChapterClarification);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 8. THE GATE SIGNATURES, pinned so a later change cannot move them quietly (x2) ─────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THREE SIGNATURES THREE GATES MEASURED, restated here as literals off the live logs, because the
    /// (g-1) fix touches the mechanism that produces two of them and bucket (f) is pass/fail on its own -
    /// it is what licenses Wave 3's removal of the Custom pass. g1, g2 and g3 all recorded these
    /// character-by-character; a change that moves one is worse than the bug this file just fixed, and
    /// until now nothing but a GPU gate would have noticed.
    ///
    /// <para>The book shapes are the gate's own: book A (<c>צל הירח</c>) has 8 chapters and book C
    /// (<c>Draft Zero</c>) has 4.</para>
    ///
    /// <para>TWO OF THE THREE SIGNATURES MOVED IN w9, DELIBERATELY, AND THIS IS THE RECORD OF IT. gf6 and
    /// gf7 were pairs only because of the 0-based/1-based rule that w9 removed - the very rule whose
    /// downstream cost (a split raw-text slice, both briefs withheld, an answer hedging about a chapter
    /// the author never named) was the defect w9 exists to fix. So the pinning is doing its job here: it
    /// forced the change to be stated rather than absorbed. The gate signatures these lines quote are
    /// therefore HISTORICAL for gf6/gf7 - a re-run would now log <c>sel[2] whole[2]</c> and
    /// <c>sel[4] whole[4]</c> - and the assertions below are updated to the intended behaviour, not to
    /// whatever the code happens to do. gp1 is untouched: it carries no number, so no rule of w9's
    /// reaches it, and bucket (f) - the pass/fail signature that licenses Wave 3's removal of the Custom
    /// pass - still resolves through the positional pair exactly as three gates measured it.</para>
    /// </summary>
    [Fact]
    public void TheBucketFAndPositionalPairSignatures_AreExactlyWhatThreeGatesMeasured()
    {
        var bookA = Chapters();                                    // 8 chapters, orders 0-7
        var bookC = Chapters().Take(4).ToList();                   // 4 chapters, orders 0-3
        var register = HebrewRegister();

        // gf6, book C, "What happens in chapter 3 of my book?" with chapter 0 open. No title in book C
        // names chapter 3, so the author is COUNTING and their third chapter is order 2 - alone.
        var gf6 = BookArtifactSelector.Select(
            "What happens in chapter 3 of my book?", bookC, register, ambientChapterOrder: 0);
        Assert.Equal(new[] { 2 }, gf6.ChapterOrders);
        Assert.Equal(new[] { 2 }, gf6.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, gf6.AmbientMatch);
        Assert.False(gf6.NeedsChapterClarification);

        // gf7, book A, "מה קורה בפרק 5?" with chapter 0 open. Book A's order 4 is TITLED "Chapter 5", so
        // the title decides and the whole escalation slice goes to that one chapter - no pair for the
        // reader to split into whole[4] exc[5] any more.
        var gf7 = BookArtifactSelector.Select(
            "מה קורה בפרק 5?", bookA, register, ambientChapterOrder: 0);
        Assert.Equal(new[] { 4 }, gf7.ChapterOrders);
        Assert.Equal(new[] { 4 }, gf7.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, gf7.AmbientMatch);
        Assert.False(gf7.NeedsChapterClarification);

        // gp1, book A, "האם מירב מופיעה בפרק הראשון?" with chapter 5 open: sel[] whole[0]. The escalation
        // is unchanged; the flag is the (g-2) fix - g3 logged clarify TRUE here, under a complete answer
        // built from chapter 0's own prose, and the client rendered chapter chips over it 3 runs of 3.
        var gp1 = BookArtifactSelector.Select(
            "האם מירב מופיעה בפרק הראשון?", bookA, register, ambientChapterOrder: 5);
        Assert.Empty(gp1.ChapterOrders);
        Assert.Equal(new[] { 0 }, gp1.EscalationChapterOrders);
        Assert.Equal(BookArtifactSelector.AmbientChapterMatch.None, gp1.AmbientMatch);
        Assert.Null(gp1.AmbientChapterOrder);
        Assert.False(gp1.NeedsChapterClarification);

        // VACUITY GUARD: the Hebrew register really did resolve the name, so gp1's escalation is the
        // positional PAIR and not a bare positional word that happened to land on chapter 0.
        Assert.Equal(new[] { "מירב" }, gp1.CharacterNames);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 9. THE ANTI-RULE, swept ────────────────────────────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A RESOLVED CHAPTER NEVER PRODUCES A CLARIFYING QUESTION - swept over every chapter-shaped question
    /// in this file rather than asserted per row, because the owner's rule is a property of the feature
    /// and not of six examples. Show asking "which chapter?" while the chapter is open on screen is a
    /// failure of this plan, not a safe default.
    ///
    /// <para>It holds because ONE boolean carries both halves: the flag's condition opens with "no
    /// chapter resolved", and the selection is populated by an explicit reference OR the ambient key. The
    /// two states cannot both be true.</para>
    /// </summary>
    [Fact]
    public void NoQuestionThatResolvedAChapter_EverAsksWhichChapter()
    {
        var questions = new[]
        {
            OwnersQuestion, "בפרק הזה יש מספיק מתח?", "הפרק שאני עורך מרגיש איטי",
            "Does the conflict land in this chapter?", "Is the current chapter too slow?",
            "What happens in the chapter?", "מה קורה בפרק?", "How do I split a chapter?",
            "What does Miriam do in the chapter?", "What happens in chapter 5?", "מה קורה בפרק 5?",
            "What does Salt and Rope establish?", "Who is Miriam?", "How does it end?",
            // x2: the two shapes that carry a chapter WITHOUT resolving one, which is where the sweep's
            // old "resolved a chapter" filter could not see the violation g3 measured.
            "Does Miriam appear in the first chapter?", "How does Miriam's story end?",
            // ...and the positional shapes that must now resolve nothing at all.
            "מה קורה בפרק האחרון?", "What happens in the first chapter?"
        };

        Assert.NotEmpty(questions);   // VACUITY GUARD: the sweep has something to sweep

        // GROUNDED AT ALL, not merely RESOLVED: a chapter's raw text riding into the prompt grounds the
        // answer just as a resolved chapter does, and g3 found the flag blind to exactly that half.
        var offenders = questions
            .Select(q => (Question: q, Keys: WithAmbient(q, 3)))
            .Where(x => x.Keys.NeedsChapterClarification
                        && (x.Keys.ChapterOrders.Count > 0 || x.Keys.EscalationChapterOrders.Count > 0))
            .Select(x => x.Question)
            .ToList();

        Assert.Empty(offenders);

        // VACUITY GUARD: this corpus really does resolve chapters AND really does escalate without
        // resolving (so neither half of the filter above was empty for want of an instance) AND the flag
        // really can be true for some question in it (so the sweep is not measuring a constant false).
        Assert.Contains(questions, q => WithAmbient(q, 3).ChapterOrders.Count > 0);
        Assert.Contains(
            questions,
            q => WithAmbient(q, 3).ChapterOrders.Count == 0
                 && WithAmbient(q, 3).EscalationChapterOrders.Count > 0);
        Assert.Contains(questions, q => WithoutAmbient(q).NeedsChapterClarification);
        Assert.Contains(questions, q => WithAmbient(q, 3).NeedsChapterClarification);
    }

    /// <summary>
    /// The clarify predicate, pinned directly on its own inputs. Its truth table is small enough to state
    /// exhaustively, and stating it here means the rule has one definition that a reader can check without
    /// reconstructing it from question fixtures.
    ///
    /// <para>x2 EXTENDED IT RATHER THAN LOOSENING IT: the escalated count is a new input that can only
    /// make the answer FALSE, so every pre-existing row below is unchanged with a 0 in that column and the
    /// new rows are the states that column adds. The predicate still says "asks" only where NOTHING was
    /// carried.</para>
    /// </summary>
    [Theory]
    // resolved, escalated, locationWord, unresolvedNumbers, chapterCount => asks?
    [InlineData(1, 0, true, 0, 8, false)]     // a chapter resolved: never asks, whatever else is true
    [InlineData(1, 0, true, 1, 8, false)]
    [InlineData(0, 0, true, 0, 8, true)]      // chapter-shaped by a location word, nothing carried
    [InlineData(0, 0, false, 1, 8, true)]     // chapter-shaped by an out-of-range number
    [InlineData(0, 0, false, 0, 8, false)]    // not chapter-shaped at all
    [InlineData(0, 0, true, 1, 1, false)]     // a one-chapter book has nothing to disambiguate
    [InlineData(0, 1, true, 0, 8, false)]     // (g-2): raw text was carried, so the chapter is not unknown
    [InlineData(0, 1, false, 1, 8, false)]    // ...including when the question also named a missing number
    [InlineData(1, 1, true, 0, 8, false)]     // both halves grounded: still silent
    public void TheClarifyPredicate_IsExactlyThis(
        int resolved, int escalated, bool locationWord, int unresolved, int chapters, bool expected)
        => Assert.Equal(
            expected,
            BookArtifactSelector.NeedsClarification(resolved, escalated, locationWord, unresolved, chapters));
}
