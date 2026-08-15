using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Phase B's KEYED SELECTION over book artifacts, and the raw-text escalation it decides
/// (chatbot phase B, c1; d1 sections (1) and (2)).
///
/// <para>WHY THIS FILE IS THE CHEAP INSURANCE. The selector is pure and deterministic, and it is the
/// component whose regressions are invisible: a ranking change never fails, it just produces a
/// slightly worse answer about someone's manuscript. It is also the component that decides whether the
/// prompt carries raw chapter text at all, which is the difference between an answer grounded in the
/// author's actual sentences and one grounded in a summary of them.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE: every input here is a literal.</para>
///
/// <para>EVERY "no offenders" ASSERTION BELOW IS PAIRED WITH A VACUITY GUARD. A test that asserts an
/// empty result set must first prove the set it swept was non-empty, because this codebase has already
/// shipped a loader that silently returned an empty array and greened every gold test that read it.</para>
/// </summary>
public class ProductChatBookSelectionTests
{
    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

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

    private static CharacterRegister Register() => new()
    {
        Characters = new[]
        {
            new CharacterRegisterEntry
            {
                Name = "Miriam", Role = "protagonist", Gender = "female",
                Aliases = new[] { "Mimi", "the lighthouse keeper" }, GenderConfirmed = true
            },
            new CharacterRegisterEntry { Name = "Dov", Role = "antagonist" },
            new CharacterRegisterEntry { Name = "אליהו", Aliases = new[] { "אליהו הזקן" } },
            new CharacterRegisterEntry { Name = "שרה" },
            // SUPPRESSED: the author said this is not a character. It must not ground anything.
            new CharacterRegisterEntry
            {
                Name = "Jerusalem", IsCharacter = false, IsCharacterConfirmed = true
            }
        }
    };

    private static BookArtifactSelector.BookQuestionKeys Select(string question)
        => BookArtifactSelector.Select(question, Chapters(), Register());

    // ─── Chapter references ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A bare chapter number selects ONE chapter (w9). It used to select both readings - order 2 and
    /// order 3 for "chapter 3" - on the grounds that <c>Chapter.Order</c> is 0-based, authors count from
    /// 1, and guessing would be a silent 50% error rate. The premise was wrong: the author is not
    /// guessable-between, they count from 1 on every surface the product shows them
    /// (<c>chapterDisplayNumber</c>), so the pair was a 100% rate of retrieving one chapter they did not
    /// ask about - which then halved the raw-text budget and withheld both chapters' briefs.
    ///
    /// <para>No title in this fixture names chapter 3, so this is the COUNTING half of the rule. The title
    /// half is pinned below.</para>
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 3?")]
    [InlineData("מה קורה בפרק 3?")]
    [InlineData("Summarise ch. 3 for me")]
    public void AnExplicitChapterNumber_SelectsTheOneChapterTheAuthorCounted(string question)
    {
        var keys = Select(question);

        Assert.Equal(new[] { 2 }, keys.ChapterOrders);
        Assert.True(keys.HasLocationCue);
    }

    /// <summary>
    /// AND A TITLE THAT NAMES THE NUMBER OUTRANKS THE COUNTING (w9). This fixture is built so the two
    /// rules disagree: the chapter titled "Chapter 5" sits at order 4 and the one titled "פרק 2" sits at
    /// order 1, so counting would answer "chapter 5" with order 4 by luck and "chapter 2" with order 1 by
    /// luck - here they agree. What cannot agree by luck is that the title is CONSULTED FIRST, which the
    /// prologue-offset case in <c>ProductChatBookScopingTests</c> pins where the two really diverge.
    /// </summary>
    [Theory]
    [InlineData("What happens in Chapter 5?", 4)]
    [InlineData("מה קורה בפרק 2?", 1)]
    public void ANumberedTitle_IsWhatTheNumberResolvesTo(string question, int expectedOrder)
    {
        var keys = Select(question);

        Assert.Equal(new[] { expectedOrder }, keys.ChapterOrders);
        Assert.Empty(keys.AmbiguousChapterNumbers);
    }

    /// <summary>
    /// A number the book does not have selects nothing, rather than inventing a chapter. The vacuity
    /// guard is the second assertion: the SAME question shape with an in-range number does select, so
    /// the empty result is the range check and not a broken parse.
    /// </summary>
    [Fact]
    public void AChapterNumberTheBookDoesNotHave_SelectsNoChapter()
    {
        Assert.Empty(Select("What happens in chapter 40?").ChapterOrders);

        // VACUITY GUARD: the parse itself works on this book.
        Assert.NotEmpty(Select("What happens in chapter 4?").ChapterOrders);
    }

    /// <summary>
    /// A number that is not NEAR a chapter word is not a chapter reference. Without this, "I wrote 2000
    /// words about the chapter" would select chapter 2000 (and, once range-checked away, nothing at
    /// all) or worse, some real chapter.
    /// </summary>
    [Fact]
    public void ANumberNotAttachedToAChapterWord_IsNotAChapterReference()
    {
        var keys = Select("The chapter about Miriam has 3 scenes I am unsure about");

        Assert.Empty(keys.ChapterOrders);

        // VACUITY GUARD: the same book, the same selector, an attached number DOES resolve.
        Assert.NotEmpty(Select("The chapter 3 opening").ChapterOrders);
    }

    /// <summary>A distinctive title reaches its chapter without a number, in either script.</summary>
    [Theory]
    [InlineData("What does Salt and Rope establish?", 2)]
    [InlineData("ספר לי על המנעול השבור", 3)]
    public void ADistinctiveTitle_SelectsItsChapter(string question, int expectedOrder)
    {
        Assert.Contains(expectedOrder, Select(question).ChapterOrders);
    }

    /// <summary>
    /// A GENERIC title cannot select its chapter on the strength of its generic word alone. Chapter 4 of
    /// this fixture is literally titled "Chapter 5" and chapter 1 is "פרק 2"; without the distinctiveness
    /// guard, any question containing the word "chapter" would title-match them and a question mentioning
    /// "פרק" would match every Hebrew-titled chapter, selecting a large slice of the manuscript as
    /// grounding.
    /// </summary>
    [Fact]
    public void AGenericTitle_DoesNotSelectItsChapterByItsGenericWordAlone()
    {
        var keys = BookArtifactSelector.Select(
            "How do chapters work in this product?", Chapters(), Register());

        Assert.Empty(keys.ChapterOrders);

        // VACUITY GUARD: those two chapters ARE reachable, by the number their own title names, so the
        // emptiness above is the distinctiveness rule and not an unreachable fixture. (w9 made this guard
        // stronger than it was: it used to reach them through the 0-based/1-based pair, which touched them
        // only as the second candidate of a number that meant a different chapter.)
        Assert.Contains(4, Select("what is in chapter 5").ChapterOrders);
        Assert.Contains(1, Select("what is in פרק 2").ChapterOrders);
    }

    /// <summary>
    /// A scene reference resolves to its PARENT chapter, not to the scene. d1 assumed a scene could be
    /// escalated to its own text; <c>Scene</c> carries <c>ContentSfdt</c> and NO plain-text column, so
    /// reading one as text would mean a second SFDT-to-text path, which this phase forbids.
    /// </summary>
    [Fact]
    public void ASceneTitle_SelectsItsParentChapter()
    {
        var chapters = new[]
        {
            new BookArtifactSelector.ChapterRef(0, "The Arrival", Array.Empty<string>()),
            new BookArtifactSelector.ChapterRef(1, "Salt and Rope", new[] { "The Ferry Deck", "Nightfall" })
        };

        var keys = BookArtifactSelector.Select("what happens on the ferry deck?", chapters, Register());

        Assert.Equal(new[] { 1 }, keys.ChapterOrders);
    }

    // ─── Character names, through the register ──────────────────────────────────────────────────

    [Fact]
    public void ARegisteredName_ResolvesToItsCanonicalName()
    {
        Assert.Equal(new[] { "Miriam" }, Select("Who is Miriam?").CharacterNames);
    }

    /// <summary>An ALIAS resolves to the canonical name, single-word and multi-word alike, so the answer
    /// and its citations name the character the register knows rather than the nickname asked about.</summary>
    [Theory]
    [InlineData("What does Mimi want?")]
    [InlineData("What does the lighthouse keeper want?")]
    public void AnAlias_ResolvesToTheCanonicalName(string question)
    {
        Assert.Equal(new[] { "Miriam" }, Select(question).CharacterNames);
    }

    /// <summary>
    /// A Hebrew name carrying a single-letter clitic resolves through the SAME inflection keys the guide
    /// selector uses ("לאליהו" reaches "אליהו"), rather than through a second tolerance of this class's
    /// own. One implementation, so the be-c02 fix cannot go stale in one of two places.
    /// </summary>
    [Fact]
    public void AHebrewNameWithAClitic_ResolvesThroughTheSharedInflectionKeys()
    {
        Assert.Contains("אליהו", Select("מה קורה לאליהו בסוף?").CharacterNames);
    }

    /// <summary>
    /// AND THE SHARED TOLERANCE'S BOUND IS INHERITED, NOT WORKED AROUND. Its four-letter stem floor means
    /// a THREE-letter Hebrew name does not survive clitic-stripping, so "לשרה" does not reach "שרה". That
    /// is the documented, deliberate bound of <c>GuideSelector.InflectionKeys</c> (it is what stops
    /// <c>הספר</c> reaching <c>ספר</c>), and inheriting it is the correct trade: a second, looser
    /// tolerance living only in the book selector is exactly the drift the reuse exists to prevent.
    /// Pinned so the limitation is a decision on the record rather than a surprise in a live transcript.
    /// </summary>
    [Fact]
    public void AShortHebrewNameWithAClitic_DoesNotResolve_TheSharedFourLetterFloor()
    {
        Assert.DoesNotContain("שרה", Select("מה קורה לשרה בסוף?").CharacterNames);

        // VACUITY GUARD: the same name, written bare, DOES resolve - so this is the stem floor and not
        // a register that was never read.
        Assert.Contains("שרה", Select("מה קורה עם שרה בסוף?").CharacterNames);
    }

    /// <summary>
    /// A SUPPRESSED entry never grounds anything. The author marked "Jerusalem" as not-a-character with
    /// <c>IsCharacterConfirmed</c>, which is permanent suppression, so a question naming it must not
    /// even partially select on that name.
    /// </summary>
    [Fact]
    public void ASuppressedEntry_NeverResolves()
    {
        var keys = Select("What role does Jerusalem play?");

        Assert.Empty(keys.CharacterNames);

        // VACUITY GUARD: the same register, the same selector, a VISIBLE entry does resolve - so the
        // emptiness is the suppression rule and not an unread register.
        Assert.NotEmpty(Select("What role does Dov play?").CharacterNames);
    }

    // ─── Review dimensions ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dimension words resolve to the SAME six canonical slugs the review declares, in the review's own
    /// declaration order, in both languages. A seventh dimension is unreachable by construction: the
    /// vocabulary is keyed to <c>BookReviewService.Dimensions</c>.
    /// </summary>
    [Theory]
    [InlineData("What did the review say about pacing?", "pacing")]
    [InlineData("מה הסקירה אמרה על הקצב?", "pacing")]
    [InlineData("Are there continuity problems?", "continuity")]
    [InlineData("יש סתירות בעלילה?", "plot")]
    public void ADimensionWord_ResolvesToItsCanonicalSlug(string question, string expected)
    {
        Assert.Contains(expected, Select(question).Dimensions);
    }

    [Fact]
    public void Dimensions_ComeBackInTheReviewsOwnDeclarationOrder()
    {
        // Asked in the reverse of the declared order; the result must not depend on word order.
        var keys = Select("Is the continuity, the tone or the plot the problem?");

        Assert.Equal(new[] { "plot", "tone", "continuity" }, keys.Dimensions);
    }

    // ─── Escalation: the seam that licenses raw-text answers ────────────────────────────────────

    /// <summary>
    /// A question NAMING a chapter earns THAT chapter's raw text - one chapter, so the whole escalation
    /// slice goes to it (w9). It used to earn two, and the shared 3,500-token slice was split between
    /// them, which is how a named chapter arrived as a thin excerpt with its brief withheld.
    /// </summary>
    [Fact]
    public void AChapterNamingQuestion_Escalates()
    {
        Assert.Equal(new[] { 2 }, Select("What exactly does Miriam say in chapter 3?").EscalationChapterOrders);
    }

    /// <summary>
    /// A LOCATION-FREE question does NOT escalate, even when it names a character. This is d1's explicit
    /// rule and the reason escalation is affordable: the briefs stay the default and the budget is spent
    /// only where the question earns it.
    /// </summary>
    [Fact]
    public void ALocationFreeQuestion_DoesNotEscalate()
    {
        var keys = Select("Who is Miriam?");

        Assert.Empty(keys.EscalationChapterOrders);
        Assert.False(keys.HasLocationCue);

        // VACUITY GUARD: the question DID resolve a character, so the escalation set is empty because
        // of the pairing rule and not because the whole selection failed.
        Assert.Equal(new[] { "Miriam" }, keys.CharacterNames);
    }

    /// <summary>
    /// A character name PAIRED with a positional cue escalates to the chapter that cue resolves to:
    /// "first" to the lowest order, "last" to the highest.
    /// </summary>
    [Theory]
    [InlineData("Does Miriam appear in the first chapter?", 0)]
    [InlineData("How does Miriam's story end?", 7)]
    public void ACharacterPairedWithAPositionalCue_EscalatesToThatEnd(string question, int expectedOrder)
    {
        Assert.Equal(new[] { expectedOrder }, Select(question).EscalationChapterOrders);
    }

    /// <summary>A positional cue with NO character does not escalate on its own: a book-shaped question
    /// ("how does it end") is a briefs question until it names who or what it is about.</summary>
    [Fact]
    public void APositionalCueAlone_DoesNotEscalate()
    {
        var keys = BookArtifactSelector.Select("How does it end?", Chapters(), Register());

        Assert.Empty(keys.EscalationChapterOrders);
        Assert.True(keys.HasLocationCue, "the positional word is still a location cue; it just does not pair");
    }

    // ─── Excerpting ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A chapter that FITS rides whole, and says so - which is what licenses a chapter-scoped
    /// assertion in the answer.</summary>
    [Fact]
    public void AChapterThatFits_RidesWhole()
    {
        var text = "Miriam climbed the stair. The lamp was cold. She lit it anyway.";

        var excerpt = BookChatExcerpts.Build(text, "what does Miriam do with the lamp?", budgetTokens: 5_000);

        Assert.True(excerpt.IsWholeChapter);
        Assert.Equal(text, excerpt.Text);
    }

    /// <summary>
    /// An OVER-BUDGET chapter degrades to excerpts that CONTAIN the question's terms, and reports itself
    /// as not-whole. Both halves matter: an excerpt that misses the answer is useless, and an excerpt
    /// that claims to be the whole chapter is the fabrication this labeling exists to prevent.
    /// </summary>
    [Fact]
    public void AnOverBudgetChapter_DegradesToExcerptsCarryingTheQuestionsTerms()
    {
        var filler = string.Join(" ", Enumerable.Repeat("The tide moved against the pilings all night.", 400));
        var needle = "Miriam found the brass key beneath the third stair.";
        var text = filler + " " + needle + " " + filler;

        var excerpt = BookChatExcerpts.Build(text, "where did Miriam find the brass key?", budgetTokens: 200);

        Assert.False(excerpt.IsWholeChapter);
        Assert.Contains("brass key", excerpt.Text);
        Assert.Contains(BookChatExcerpts.Elision, excerpt.Text);
        Assert.True(excerpt.EstimatedTokens <= 200,
            $"the excerpt must fit its slice; it estimated {excerpt.EstimatedTokens} against 200");

        // VACUITY GUARD: the same text under a generous budget IS the whole chapter, so the excerpting
        // above was forced by the budget rather than by an empty or unparseable input.
        Assert.True(BookChatExcerpts.Build(text, "brass key", budgetTokens: 50_000).IsWholeChapter);
    }

    /// <summary>
    /// When NOTHING matches lexically, the chapter's opening rides along rather than nothing at all: an
    /// answer of the shape "the parts of chapter 7 I could read do not mention X" is only truthful if
    /// some part was actually read.
    /// </summary>
    [Fact]
    public void AnOverBudgetChapterWithNoLexicalMatch_StillSendsALabeledOpening()
    {
        var text = string.Join(" ", Enumerable.Repeat("The tide moved against the pilings.", 400));

        var excerpt = BookChatExcerpts.Build(text, "what colour was the helicopter?", budgetTokens: 60);

        Assert.True(excerpt.HasText);
        Assert.False(excerpt.IsWholeChapter);
        Assert.True(excerpt.EstimatedTokens <= 60);
    }

    /// <summary>An empty chapter yields nothing, so no block can claim it was read.</summary>
    [Fact]
    public void AnEmptyChapter_YieldsNoExcerpt()
    {
        Assert.False(BookChatExcerpts.Build("   ", "anything", 5_000).HasText);
        Assert.False(BookChatExcerpts.Build(null, "anything", 5_000).HasText);

        // VACUITY GUARD: the same call with real text does yield one.
        Assert.True(BookChatExcerpts.Build("A sentence.", "anything", 5_000).HasText);
    }

    /// <summary>
    /// Hebrew scores through the SHARED inflection keys, not through a second implementation: a question
    /// asking about "המנעול" reaches a sentence writing "מנעול".
    /// </summary>
    [Fact]
    public void HebrewExcerptScoring_UsesTheSharedInflectionTolerance()
    {
        var filler = string.Join(" ", Enumerable.Repeat("הגלים היכו בסלעים כל הלילה.", 400));
        var needle = "מנעול הפליז נמצא מתחת למדרגה השלישית.";
        var text = filler + " " + needle + " " + filler;

        var excerpt = BookChatExcerpts.Build(text, "איפה המנעול?", budgetTokens: 200);

        Assert.False(excerpt.IsWholeChapter);
        Assert.Contains("מנעול הפליז", excerpt.Text);
    }
}
