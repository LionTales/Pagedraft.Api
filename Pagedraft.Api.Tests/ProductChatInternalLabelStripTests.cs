using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Chat;
using Xunit;
using Xunit.Sdk;

namespace Pagedraft.Api.Tests;

/// <summary>
/// <see cref="ProductChatInternalLabels"/> - the post-hoc strip of the prompt's own internal tokens out
/// of the answer prose (chatbot phase B, review finding final-r03).
///
/// <para>THE FIVE INPUTS BELOW ARE REAL ANSWERS, COPIED BYTE-FOR-BYTE OUT OF final-r03's RUN ARTIFACTS
/// (<c>r03-pre-results.jsonl</c> / <c>r03-cur-results.jsonl</c>, 32 live <c>/api/product-chat</c> calls,
/// <c>a6-deictic-D0-he</c> at n=8 per arm). They are not invented and they are not transliterated: every
/// one is a leak the instrument actually measured, which is the only reason this class exists. The
/// expected text of each is the same string with the gloss deleted, so what these tests pin is the
/// SENTENCE THAT SURVIVES, not the absence of a token.</para>
///
/// <para>WHY THAT DISTINCTION MATTERS HERE. "does not contain EXCERPT" would pass on an empty string and
/// on a deleted paragraph, and this layer's one real risk is deleting prose the author was reading. The
/// full-line equality is the assertion; the not-contains checks are the belt.</para>
///
/// <para>AND WHAT THE FILE PINS IS RESTRAINT, not removal - review finding A8. The first version of this
/// suite carried 41 assertions and a mutation pass over the class survived 13 of 29 mutants, EVERY
/// survivor a guard against OVER-stripping: the blast-radius bound, the label regex's tail bound, the
/// slug shape's lookbehind, the fragment scan's word boundary. A suite that only pins what a rewriter
/// REMOVES cannot see the day it starts removing more, which is exactly how the over-strip shipped as
/// correct. Every "is left alone" test below names the guard it defends and was revert-verified against
/// the mutant that breaks it; the section at the bottom of this file is that inventory.</para>
///
/// <para>ASSERTIONS GO THROUGH <see cref="AssertText"/> RATHER THAN <c>Assert.True(a == b, msg)</c>
/// (A18). xUnit's own string comparison prints the CHARACTER INDEX at which two strings part, and a
/// character index is the only way a wrong SPACE is visible in an RTL answer - two Hebrew lines that
/// differ by one space look identical printed whole, which is this feature's entire failure mode. The
/// wrapper keeps the reason as well, because a bare differ names no defect.</para>
/// </summary>
public class ProductChatInternalLabelStripTests
{
    // ─── Assertion helpers (A18) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Assert.Equal</c> plus a reason. xUnit 2.x has no message overload, and this file needs both
    /// halves: the differ says WHERE two strings part (a character index, which is what makes a stray
    /// space in Hebrew prose visible at all), and <paramref name="because"/> says which guard that
    /// index belongs to.
    /// </summary>
    private static void AssertText(string expected, string actual, string because)
    {
        try
        {
            Assert.Equal(expected, actual);
        }
        catch (XunitException differ)
        {
            throw new XunitException(because + "\n" + differ.Message);
        }
    }

    /// <inheritdoc cref="AssertText"/>
    private static void AssertCount(int expected, int actual, string because)
    {
        try
        {
            Assert.Equal(expected, actual);
        }
        catch (XunitException differ)
        {
            throw new XunitException(because + "\n" + differ.Message);
        }
    }

    /// <summary>Line breaks made legible, so a REASON can show a defect that lives past one.</summary>
    private static string Visible(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

    // ─── The five measured leaks (verbatim) ─────────────────────────────────────────────────────

    /// <summary>pre-arm run 1: Show asks THE AUTHOR to supply an internal key.</summary>
    private const string Leak1 =
        "אם תרצה לדעת על פרק אחר או אם יש לך טקסט ספציפי שאתה מתכוון אליו, אנא ציין את המזהה שלו (למשל `chapter-text:X` או `EXCERPT`) ואשמח לעדכן אותך.";

    private const string Leak1Expected =
        "אם תרצה לדעת על פרק אחר או אם יש לך טקסט ספציפי שאתה מתכוון אליו, אנא ציין את המזהה שלו ואשמח לעדכן אותך.";

    /// <summary>pre-arm run 3: a finding GUID mid-sentence.</summary>
    private const string Leak2 =
        "בנוסף לכך, ב**פרק 8**, הקצב מואט משמעותית בגלל תיאור נוף ארוך שמעכב את ההתקדמות בעלילה (מצא פתיחה: finding:4c8dd0c9-7aca-489e-95f6-2861631e6058).";

    private const string Leak2Expected =
        "בנוסף לכך, ב**פרק 8**, הקצב מואט משמעותית בגלל תיאור נוף ארוך שמעכב את ההתקדמות בעלילה.";

    /// <summary>pre-arm run 4: the excerpt label glossing a Hebrew sentence that already said it.</summary>
    private const string Leak3 =
        "אם התכוונת ל**פרק 7**, הטקסט שמופיע כאן הוא רק חלק ממנו (EXCERPT). במקטע הזה נכתב שממצאי העריכה ההתפתחותית בודדו את הפרק כ\"התפתח עקבי שאינו דורש התערבות מיידית\".";

    private const string Leak3Expected =
        "אם התכוונת ל**פרק 7**, הטקסט שמופיע כאן הוא רק חלק ממנו. במקטע הזה נכתב שממצאי העריכה ההתפתחותית בודדו את הפרק כ\"התפתח עקבי שאינו דורש התערבות מיידית\".";

    /// <summary>pre-arm run 7: both halves of the whole-vs-excerpt vocabulary, glossed in one sentence.</summary>
    private const string Leak4 =
        "השאלה שלך מתייחסת ל\"פרק זה\", אך לא ציינת את מספר הפרק או שמו, ולכן איני יכול לקרוא מה כתוב בו. המדריכים מציינים כי תקצירי פרקים הם סיכומים בלבד ואינם מכסים כל אירוע בספר; רק הטקסט המלא של פרק (whole chapter) מאפשר לי לומר בדיוק מה קורה בתוכו, בעוד שחלק ממנו (EXCERPT) יאמר רק על החלקים שהצלחת לקרוא.";

    private const string Leak4Expected =
        "השאלה שלך מתייחסת ל\"פרק זה\", אך לא ציינת את מספר הפרק או שמו, ולכן איני יכול לקרוא מה כתוב בו. המדריכים מציינים כי תקצירי פרקים הם סיכומים בלבד ואינם מכסים כל אירוע בספר; רק הטקסט המלא של פרק מאפשר לי לומר בדיוק מה קורה בתוכו, בעוד שחלק ממנו יאמר רק על החלקים שהצלחת לקרוא.";

    /// <summary>
    /// cur-arm run 2: the one leak that survived at HEAD. It also carries an ORDINARY parenthetical on the
    /// same line ("(ישנם 40 פרקים בסך הכל)"), which is what makes it the strongest single regression case
    /// in the file: the strip has to reach one bracket pair on the line and not the other.
    /// </summary>
    private const string Leak5 =
        "המדריך לא מכסה את כל הפרקים בספר (ישנם 40 פרקים בסך הכל). התקצירים הזמינים לכיסוי רק **38 מתוך 40 הפרקים**, ונותרו שני פרקים בלבד שלא נכללו בהם. בנוסף, המדריכים אינם מכילים את הטקסט המלא של כל הפרקים אלא רק חלקים מהם (EXCERPT), ולכן הם לא יכולים לומר אם יש דברים בספר שאינם מוזכרים בתקצירים או בחלקים שניתנו.";

    private const string Leak5Expected =
        "המדריך לא מכסה את כל הפרקים בספר (ישנם 40 פרקים בסך הכל). התקצירים הזמינים לכיסוי רק **38 מתוך 40 הפרקים**, ונותרו שני פרקים בלבד שלא נכללו בהם. בנוסף, המדריכים אינם מכילים את הטקסט המלא של כל הפרקים אלא רק חלקים מהם, ולכן הם לא יכולים לומר אם יש דברים בספר שאינם מוזכרים בתקצירים או בחלקים שניתנו.";

    public static TheoryData<string, string, int, string> MeasuredLeaks => new()
    {
        { Leak1, Leak1Expected, 2, "pre run1: the author is asked to supply `chapter-text:X` or `EXCERPT`" },
        { Leak2, Leak2Expected, 1, "pre run3: a finding GUID inside the sentence" },
        { Leak3, Leak3Expected, 1, "pre run4: '(EXCERPT)' glossing a sentence that already said 'only part of it'" },
        { Leak4, Leak4Expected, 2, "pre run7: '(whole chapter)' and '(EXCERPT)' in one sentence" },
        { Leak5, Leak5Expected, 1, "cur run2: '(EXCERPT)' beside an ordinary parenthetical" },
    };

    [Theory]
    [MemberData(nameof(MeasuredLeaks))]
    public void EveryMeasuredLeak_LosesItsGlossAndKeepsItsSentence(
        string leaked, string expected, int expectedRemovals, string what)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            $"a real measured leak ({what}) did not come back as the sentence without its gloss.");

        AssertCount(expectedRemovals, removed,
            $"the strip reported the wrong number of internal tokens removed for {what}. That count is the " +
            "only place the underlying model rate stays visible once this layer is on.");
    }

    /// <summary>
    /// The belt behind the equality theory above, and it is now written so that EVERY datum can fail
    /// (A15). It used to be four tokens crossed with all five leaks - 20 assertions of which 15 could not
    /// fail, because the token was never in the raw answer to begin with, and an assertion that cannot
    /// fail is not coverage, it is a green light with no bulb. Each pair below is a token the raw answer
    /// REALLY carries, and the first assertion pins that premise so a future edit to a leak string turns
    /// this vacuous LOUDLY rather than quietly.
    /// </summary>
    public static TheoryData<string, string, string> TokensReallyPresentInAMeasuredLeak => new()
    {
        { Leak1, "chapter-text:X", "pre run1" },
        { Leak1, "EXCERPT", "pre run1" },
        { Leak2, "finding:4c8dd0c9", "pre run3" },
        { Leak3, "EXCERPT", "pre run4" },
        { Leak4, "whole chapter", "pre run7" },
        { Leak4, "EXCERPT", "pre run7" },
        { Leak5, "EXCERPT", "cur run2" },
    };

    [Theory]
    [MemberData(nameof(TokensReallyPresentInAMeasuredLeak))]
    public void ATokenTheRawAnswerREALLYCarried_IsGoneFromTheStrippedOne(
        string leaked, string token, string run)
    {
        Assert.True(leaked.Contains(token, StringComparison.OrdinalIgnoreCase),
            $"this datum claims {run}'s measured answer carries '{token}', and it does not. The datum is " +
            "vacuous as written: the assertion below would pass on any string, including an empty one.");

        var (text, _) = ProductChatInternalLabels.Strip(leaked);

        Assert.False(text.Contains(token, StringComparison.OrdinalIgnoreCase),
            $"an internal token the author cannot act on ('{token}') survived the strip in {run}'s real " +
            $"measured answer. Text was: {text}");
    }

    // ─── The bracketed label (currently at 0 of 146, and pinned so it stays there) ───────────────

    [Theory]
    // The two forms BookArtifactBlocks actually renders, plus the bare form g4 measured 3 of 38.
    [InlineData("The repetition happens in chapter 8 [CHAPTER 7].", "The repetition happens in chapter 8.")]
    [InlineData("Chapter 1 [CHAPTER 0 EXCERPT] opens on the harbour.", "Chapter 1 opens on the harbour.")]
    [InlineData("Chapter 4 [CHAPTER 3, whole chapter] is complete.", "Chapter 4 is complete.")]
    [InlineData("[CHAPTER 7 EXCERPT, not the whole chapter] is what I have.", "is what I have.")]
    // The Hebrew form a model translating the label would write. Nothing EMITS this one - it is a
    // translation the model performs - so unlike the two English forms it cannot be pinned against a
    // format constant, and this datum is the only thing holding it.
    [InlineData("החזרה מתרחשת בפרק 8 [פרק 7].", "החזרה מתרחשת בפרק 8.")]
    public void ABracketedChapterLabel_IsRemovedWithItsBrackets(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text, "a bracketed internal chapter label was not removed cleanly.");
        Assert.Equal(1, removed);
    }

    /// <summary>
    /// DECISION 2, the mixed case. g4 measured "בפרק 7 (כפי שמצויין בכותרת [CHAPTER 6])" - order 7's label
    /// is [CHAPTER 7], so the parenthetical was FALSE as well as internal. Three residual words is a clause
    /// the author can read, so the clause stays and only the label goes; and because the parenthetical was
    /// subordinate to the sentence, what is left standing is the sentence's own CORRECT claim about chapter
    /// 7. That is DECISION 1: the strip deletes a wrong statement rather than correcting it.
    /// </summary>
    [Fact]
    public void AMixedParenthetical_KeepsItsWordsAndLosesOnlyTheLabel()
    {
        const string leaked = "החזרה מתרחשת בפרק 7 (כפי שמצויין בכותרת [CHAPTER 6]).";
        const string expected = "החזרה מתרחשת בפרק 7 (כפי שמצויין בכותרת).";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a parenthetical carrying a CLAUSE plus a wrongly-quoted internal label should lose the label " +
            "and keep the clause.");
        Assert.Equal(1, removed);
        Assert.DoesNotContain("CHAPTER 6", text, StringComparison.Ordinal);
        Assert.Contains("בפרק 7", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// DECISION 3, and it is the reason this is a rendering fix at all: review finding #3 recorded an LTR
    /// slug inside Hebrew prose dragging its closing parenthesis to the wrong end, RENDERING as
    /// "chapter-text:0 ),". The source of that rendering is a BALANCED "(chapter-text:0),", which is the
    /// shape asserted here, and the whole pair goes.
    ///
    /// <para>This replaced a test that fed the strip an already-UNMATCHED ")" and pinned the strip for
    /// dropping it. That test was written against a mis-reading of its own evidence: nothing measured ever
    /// showed this strip orphaning a bracket, and under the narrowing nothing can, because a group goes
    /// whole, a label is a matched pair, and a slug removal touches no bracket at all. The rule it pinned
    /// (drop every unmatched bracket on any line the strip touched) was line-wide, so the only brackets it
    /// could reach were ones the MODEL wrote - see the two cases in
    /// <see cref="AModelWrittenUnmatchedBracket_IsNotTheStripsToDrop"/>.</para>
    /// </summary>
    [Fact]
    public void ASlugInsideAParenthetical_TakesTheWholeMatchedPairWithIt()
    {
        const string leaked = "הפרק הזה עודכן לאחרונה בשבוע שעבר, כפי שמסומן בקובץ (chapter-text:0).";
        const string expected = "הפרק הזה עודכן לאחרונה בשבוע שעבר, כפי שמסומן בקובץ.";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a parenthesised slug in RTL prose must take BOTH its brackets, or the surviving half renders " +
            "at the wrong end of the phrase.");
        Assert.Equal(1, removed);
        Assert.DoesNotContain(")", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A5 and A6. A bracket the strip did not orphan is not the strip's to drop, and the line is not the
    /// unit of repair. The first case is a pair the model opened on the previous line, so dropping the
    /// closer would MAKE the orphan; the second is a smiley at the far end of the sentence, whose bracket
    /// the old line-wide rule dropped and whose surrounding words the old line-wide tidies then glued
    /// together.
    /// </summary>
    [Theory]
    [InlineData("prose (see\nchapter-text:0) tail", "prose (see\n) tail")]
    [InlineData("chapter-text:0 removed, and a smiley :-( stays?", "removed, and a smiley :-( stays?")]
    public void AModelWrittenUnmatchedBracket_IsNotTheStripsToDrop(string leaked, string expected)
    {
        var (text, _) = ProductChatInternalLabels.Strip(leaked);

        // The reason escapes the newlines, because the defect this catches LIVES on the second line:
        // printed raw, both sides read "prose (see" and the reader never sees which bracket went.
        AssertText(expected, text,
            "the strip reached past its own removal and rewrote punctuation the model wrote elsewhere on " +
            $"the line.\nEXPECTED: {Visible(expected)}\nACTUAL  : {Visible(text)}");
    }

    [Fact]
    public void ABalancedBracketOnATouchedLine_SurvivesTheStrip()
    {
        var (text, _) = ProductChatInternalLabels.Strip(Leak5);

        Assert.Contains("(ישנם 40 פרקים בסך הכל)", text, StringComparison.Ordinal);
    }

    // ─── The slug family, tested through BookArtifactRefs' own shape rule ────────────────────────

    [Theory]
    [InlineData("The brief chapter-brief:5 says so.", "The brief says so.")]
    [InlineData("Per chapter-summary:3 the pacing slows.", "Per the pacing slows.")]
    [InlineData("The review status:review is behind.", "The review is behind.")]
    [InlineData("See chapter-text:12 for the full text.", "See for the full text.")]
    [InlineData("The book-brief has the premise.", "The has the premise.")]
    [InlineData("Reported as finding:4c8dd0c9-7aca-489e-95f6-2861631e6058 here.", "Reported as here.")]
    public void AnArtifactRefInASentence_IsRemoved(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text, "an artifact ref was not removed from the prose.");
        Assert.Equal(1, removed);
    }

    /// <summary>
    /// DECISION 5, and what is LEFT of a theory that used to run three data. Two of them asserted that
    /// "I read it from `register`." and "I read it from **history**." come back as the ungrammatical
    /// "I read it from." - they PINNED THE OVER-STRIP AS CORRECT, on a rule that no measurement ever
    /// supported and that ate the product's own feature names out of running prose. They are deleted, not
    /// weakened; their inputs now live in <see cref="AnEmphasisWrappedWordInRunningProse_IsLeftAlone"/>
    /// asserting the opposite. What survives is the third datum, which is a real slug: it carries a shape
    /// no sentence produces, so it goes, and it takes the wrapper it emptied with it rather than leaving
    /// two backticks behind to flip <c>ProductChatPunctuation</c>'s code-span parity downstream.
    /// </summary>
    [Theory]
    [InlineData("I read it from `chapter-text:0`.", "I read it from.")]
    [InlineData("prose `chapter-text:0` more", "prose more")]
    [InlineData("prose *chapter-text:0* more", "prose more")]
    [InlineData("prose **chapter-text:0** more", "prose more")]
    public void AWrapperEmptiedByASlugRemoval_GoesWithTheSlug(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a slug removal left its own emphasis wrapper behind. An empty '**' opens a run that never " +
            "closes, so the rest of the paragraph renders bold.");
        Assert.Equal(1, removed);
        Assert.DoesNotContain("`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("*", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A4, the half of the same shape where the wrapper still holds WORDS. The slug goes and the wrapper
    /// stays, which means the space the slug was holding open has to go with it: in CommonMark a
    /// space-preceded closer never closes, so "**see **" would render every following paragraph bold in
    /// <c>app-markdown-text</c>.
    /// </summary>
    [Theory]
    [InlineData("prose **see chapter-text:0** more", "prose **see** more")]
    [InlineData("prose **chapter-text:0 see** more", "prose **see** more")]
    public void ASlugInsideAWrapperThatStillHoldsWords_TakesTheSpaceItStranded(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a slug removal stranded a space against an emphasis closer, which in CommonMark does not close.");
        Assert.Equal(1, removed);
    }

    /// <summary>
    /// A19. A markdown link has two halves and only ONE of them is author-facing: the text is what a
    /// reader sees and the target is not rendered as prose at all. The strip used to delete both, because
    /// the text half matched the bracketed-label rule, so "See [chapter 1](chapter-text:0) for the text."
    /// came back as "See for the text." - three of the author's words gone. The link is unlinked instead.
    /// </summary>
    [Theory]
    [InlineData("See [chapter 1](chapter-text:0) for the text.", "See chapter 1 for the text.")]
    [InlineData("ראה [פרק 1](chapter-text:0) בהמשך.", "ראה פרק 1 בהמשך.")]
    public void AMarkdownLinkToAnArtifactRef_LosesItsTargetAndKeepsItsText(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a markdown link lost the half the author reads. The target is internal, the TEXT is not.");
        Assert.Equal(1, removed);
    }

    /// <summary>
    /// final-r01. The same DECISION 5 rule as <see cref="AWrapperEmptiedByASlugRemoval_GoesWithTheSlug"/>,
    /// on the OTHER removal path. A whole-group removal spliced out the brackets and left the wrapper that
    /// held them, so <c>**(EXCERPT)**</c> came back as <c>****</c>: the token path consumed its wrapper and
    /// the group path did not, and the class doc asserted the property for both. Two harms, not one style
    /// miss - see <see cref="AStrayWrapperLeftByAGroupRemoval_DoesNotDisarmThePunctuationRepair"/> for the
    /// measured second one.
    /// </summary>
    [Theory]
    [InlineData("This is text **(EXCERPT)** and more.", "This is text and more.")]
    [InlineData("Text *(EXCERPT)* and more.", "Text and more.")]
    [InlineData("Text `(EXCERPT)` and more.", "Text and more.")]
    [InlineData("Text **(chapter-text:0)** and more.", "Text and more.")]
    [InlineData("טקסט **(EXCERPT)** ועוד.", "טקסט ועוד.")]
    public void AWrapperEmptiedByAWHOLEGROUPRemoval_GoesWithTheGroup(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a WHOLE-GROUP removal left its own emphasis or backtick wrapper behind. '**' opens a " +
            "CommonMark run that never closes; a stray backtick pair silently disarms the em-dash repair.");
        AssertCount(1, removed, "the group removal is one token, whatever the wrapper around it was.");
        Assert.DoesNotContain("`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("*", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// final-r01, and the reason the test above is not cosmetic. <c>ProductChatService</c> runs this strip
    /// and then <see cref="ProductChatPunctuation.Repair"/>, and asserts at the call site that the strip
    /// leaves the code-span parity the repair depends on alone. A stray backtick pair breaks that: every
    /// character after it reads as a code span, so the repair finds nothing on a line that has two
    /// em-dashes. Measured before it was fixed, and pinned against the UNSTRIPPED answer so the datum
    /// cannot pass by the repair being broken for some other reason.
    /// </summary>
    [Fact]
    public void AStrayWrapperLeftByAGroupRemoval_DoesNotDisarmThePunctuationRepair()
    {
        const string leaked = "Text `(EXCERPT)` and a dash — here — ok.";

        var (_, withoutStrip) = ProductChatPunctuation.Repair(leaked);
        AssertCount(2, withoutStrip,
            "PREMISE: this datum only means something if the repair fixes both em-dashes when the strip " +
            "has not run. It does not, so the comparison below is vacuous.");

        var (stripped, _) = ProductChatInternalLabels.Strip(leaked);
        var (_, afterStrip) = ProductChatPunctuation.Repair(stripped);

        AssertCount(withoutStrip, afterStrip,
            "the strip disarmed the punctuation repair downstream of it. It left a wrapper delimiter " +
            "stray, which flips ProductChatPunctuation's code-span parity for the rest of the ANSWER (that " +
            "layer never resets the state at a newline), so an em-dash the workspace rule forbids ships to " +
            "the author. The comment at the strip's call site in ProductChatService asserts this cannot " +
            "happen.");
    }

    /// <summary>
    /// final-r01, and the OTHER half of A19. DECISION 5 exempts a link's text from the bracketed-label
    /// rule, which is not the same as exempting it from shape 2: a link whose text happened to read as a
    /// gloss was still a bracket pair, so <c>[an excerpt](chapter-text:0)</c> lost BOTH halves and the
    /// author-facing words went with the target. The unlink must keep the text whatever the text says.
    /// </summary>
    [Theory]
    [InlineData("See [an excerpt](chapter-text:0) for the text.", "See an excerpt for the text.")]
    [InlineData("See [the whole chapter](chapter-text:0) for the text.", "See the whole chapter for the text.")]
    [InlineData("ראה [חלק מהפרק](chapter-text:0) לפרטים.", "ראה חלק מהפרק לפרטים.")]
    public void AMarkdownLinkWhoseTEXTReadsLikeAGloss_StillKeepsItsText(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a markdown link lost the half the author reads because its TEXT matched the gloss rule. " +
            "DECISION 5 says a link is unlinked and never deleted, whatever its text happens to say.");
        AssertCount(1, removed, "the target is the only internal token on the line.");
    }

    // ─── The boundary: what is deliberately NOT stripped ─────────────────────────────────────────

    /// <summary>
    /// DECISION 4's first bullet, and the objection final-r05 raised against this whole todo: the grounding
    /// clause DELIBERATELY names "whole chapter" and "excerpt" in both languages as the distinction the
    /// model must carry to the author in its own sentence. A bare use of either is the instruction WORKING,
    /// so only a bracketed / parenthesised / backticked gloss is removed. This test is what keeps the strip
    /// from eating the model's reasoning.
    /// </summary>
    [Theory]
    [InlineData("I have the whole chapter, so I can say what it does and does not contain.")]
    [InlineData("I was only given an excerpt of chapter 8, so I can speak for the part I read.")]
    [InlineData("יש לי את הטקסט המלא של הפרק, ולכן אני יכול לומר מה יש בו ומה אין בו.")]
    [InlineData("The register of characters is the place to look, and the history is longer.")]
    [InlineData("She kept a register in the harbour office; its history goes back to 1932.")]
    public void LegitimateProse_IsNotTouched(string prose)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(prose);

        AssertText(prose, text,
            "the strip rewrote ordinary prose. The words 'whole chapter' and 'excerpt' are ones the " +
            "grounding clause tells the model to reason WITH, and 'register'/'history' are ordinary nouns.");
        Assert.Equal(0, removed);
    }

    /// <summary>
    /// A1. A bracket that still holds a readable clause keeps EVERY word it had. The strip used to reach
    /// inside a parenthetical it had declined to remove and delete one word out of the middle of it, so
    /// "(an excerpt of chapter 8)" came back as "(an of chapter 8)" and "(I have the whole chapter in
    /// front of me)" as "(I have the in front of me)". The bracket goes whole or not at all.
    /// </summary>
    [Theory]
    [InlineData("I can only speak to what I read (an excerpt of chapter 8), not the rest.")]
    [InlineData("I can answer that (I have the whole chapter in front of me).")]
    [InlineData("אני יכול לדבר רק על מה שקראתי (חלק מפרק 8, לא whole chapter), לא על השאר.")]
    public void AParentheticalThatStillHoldsProse_IsLeftEntirelyAlone(string prose)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(prose);

        AssertText(prose, text,
            "the strip deleted a word from inside a bracket it had decided to KEEP, which leaves the " +
            "author reading a mangled clause.");
        Assert.Equal(0, removed);
    }

    /// <summary>
    /// A2. The strip used to delete an emphasis- or backtick-wrapped word wrapper-and-all out of running
    /// prose, on the unmeasured theory that a sentence does not put those words in a wrapper. It does:
    /// "This is only an **excerpt**, not the whole thing." came back as "This is only an, not the whole
    /// thing.", and "register" and "history" are the product's OWN feature names. That pass is deleted, so
    /// a wrapper is now no evidence of anything and only the three measured shapes are.
    /// </summary>
    [Theory]
    [InlineData("This is only an **excerpt**, not the whole thing.")]
    [InlineData("I read it from `register`.")]
    [InlineData("I read it from **history**.")]
    [InlineData("The **whole chapter** is here, so I can answer.")]
    [InlineData("קראתי רק `excerpt` מהפרק הזה, ולכן איני יכול לומר יותר.")]
    public void AnEmphasisWrappedWordInRunningProse_IsLeftAlone(string prose)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(prose);

        AssertText(prose, text,
            "the strip deleted an emphasised word out of running prose. 'excerpt' and 'whole chapter' are " +
            "words the grounding clause tells the model to reason WITH, and 'register' and 'history' are " +
            "this product's own feature names.");
        Assert.Equal(0, removed);
    }

    /// <summary>
    /// final-r01. A GLOSS IS A PARENTHETICAL, which is what DECISION 2 and DECISION 4 both say and what
    /// all five measured leaks are: every one puts its token inside a ROUND pair. The fragment scan was
    /// widened to every bracket pair, which made a square aside in the author's own prose a gloss, so
    /// "He gave me [an excerpt] of it." lost its words - and, because a link's text is a square pair too,
    /// so did a link this class has no business touching at all: the last two data carry NO internal token
    /// anywhere on the line and were still rewritten.
    /// </summary>
    [Theory]
    [InlineData("He gave me [an excerpt] of it.")]
    [InlineData("The note said [whole chapter] beside it.")]
    [InlineData("See [an excerpt](https://example.com/x) for the text.")]
    [InlineData("ראה [whole chapter](https://example.com/x) לפרטים.")]
    public void ASquareAsideThatIsNotTheEmittersLabel_IsNotAGloss(string prose)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(prose);

        AssertText(prose, text,
            "the strip treated a SQUARE bracket pair as a gloss. Every measured leak is a parenthetical; " +
            "a square aside is the author's own, and a link's text is the only half a reader sees.");
        AssertCount(0, removed, "there is no internal token on this line at all.");
    }

    /// <summary>
    /// DECISION 4's second bullet. A whole-line citation is the ONE line where a ref belongs, and
    /// <see cref="ProductChatCitations"/> owns it end to end - including its deliberate choice to leave a
    /// line it refused in the prose. A strip that ran there would gut it.
    ///
    /// <para>A16: EVERY datum here now CARRIES A REF, because that is the only thing the skip protects.
    /// The bold-label datum used to read "**Guides:** faq, export", which passes with the skip DELETED -
    /// "Guides:" is not an artifact ref, "faq" and "export" are guide ids, and so there was nothing on the
    /// line for the strip to remove either way. It was pinning its own property vacuously, on the one
    /// datum whose markdown wrapper is the interesting half.</para>
    /// </summary>
    [Theory]
    [InlineData("מקורות: chapter-text:7, status:review")]
    [InlineData("Sources: chapter-brief:5, register, export")]
    [InlineData("**Guides:** faq, export, chapter-brief:5")]
    [InlineData("- מדריכים: chapter-summary:3")]
    public void ACitationLine_IsLeftEntirelyToTheCitationParser(string citationLine)
    {
        var answer = "התשובה שלי על הפרק.\n\n" + citationLine;

        var (text, removed) = ProductChatInternalLabels.Strip(answer);

        AssertText(answer, text,
            "the strip reached into a citation line, which ProductChatCitations owns end to end.\n" +
            $"BEFORE: {Visible(answer)}\nAFTER : {Visible(text)}");
        Assert.Equal(0, removed);
    }

    [Fact]
    public void AnInlineTrailingLabel_KeepsItsWordsAndLosesTheRefTheParserRefused()
    {
        // The inline shape is prose with a label at the end of it, which is why it is NOT skipped: a ref
        // that reached the reader here is exactly what LooksFabricated exists to stop publishing. The label
        // word stays, because deleting words the author is reading is the worse failure.
        const string leaked = "הפרק הזה מכוסה בתקציר. מקורות: chapter-brief:99";
        const string expected = "הפרק הזה מכוסה בתקציר. מקורות:";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "an INLINE trailing label is prose with a ref at the end of it, not a citation line: the words " +
            "stay and only the ref goes.");
        Assert.Equal(1, removed);
    }

    // ─── Shape guards ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAnswerWithNothingToStrip_IsReturnedUnchangedAndCostsNothing()
    {
        const string clean = "פרק 8 מאט את הקצב בגלל תיאור נוף ארוך.\n\nהתקצירים אינם מזכירים את זה.";

        var (text, removed) = ProductChatInternalLabels.Strip(clean);

        Assert.Same(clean, text);
        Assert.Equal(0, removed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoAnswer_IsNotAFailure(string? answer)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(answer);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void LineStructure_AndWindowsLineEndings_SurviveTheStrip()
    {
        const string leaked = "שורה ראשונה (EXCERPT).\r\n\r\n*   פריט ברשימה (EXCERPT), והמשך.";
        const string expected = "שורה ראשונה.\r\n\r\n*   פריט ברשימה, והמשך.";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "the strip changed the answer's line structure.\n" +
            $"EXPECTED: {Visible(expected)}\nACTUAL  : {Visible(text)}");
        Assert.Equal(2, removed);
    }

    // ─── The blast-radius bound, pinned by a LITERAL (A7 / mutant M06) ──────────────────────────

    /// <summary>
    /// THE LITERAL THIS FILE PINS <see cref="ProductChatInternalLabels.MaxGroupChars"/> AT, written out
    /// rather than read from the constant. PROVENANCE, so a later reader can move it on evidence instead
    /// of guessing: <see cref="ProductChatInternalLabels.MaxGroupChars"/>' own docstring records the longest
    /// MEASURED gloss at 57 characters (leak 2's finding-guid parenthetical, countable in
    /// <see cref="Leak2"/> below), this is about three and a half times that, and its sibling bound is
    /// <c>ProductChatCitations.MaxInlineCitationChars</c>. It is NOT recorded in DECISION 2, which this
    /// comment used to cite and which carries no character figure at all.
    ///
    /// <para>WHY A LITERAL AND NOT THE CONSTANT. The test that used to hold this bound built its input
    /// from <c>ProductChatInternalLabels.MaxGroupChars</c> itself, so it asserted f(x) == f(x): mutating
    /// the constant moved the input by exactly as much as it moved the rule, and the file's HEADLINE
    /// SAFETY BOUND passed all 41 assertions with the bound set to 100000 (review mutant M06). A bound
    /// pinned by the thing it bounds is pinned by nothing.</para>
    /// </summary>
    private const int PinnedMaxGroupChars = 200;

    [Fact]
    public void TheBlastRadiusBound_IsTheValueThisFilePins()
    {
        AssertCount(PinnedMaxGroupChars, ProductChatInternalLabels.MaxGroupChars,
            "the most a whole-parenthetical removal may ever delete has MOVED. This is the class's headline " +
            "safety bound: raise it and a mis-classified bracket costs the author a paragraph instead of a " +
            "gloss. If the move is deliberate, move this literal too AND write down the measurement that " +
            "justified it, the way MaxGroupChars' docstring records the 57-character longest measured gloss.");
    }

    /// <summary>
    /// The same bound asserted through BEHAVIOUR, on both sides of it and one character apart, so the
    /// constant and the rule that reads it are pinned separately. At the bound the parenthetical goes
    /// whole; one character over it, the brackets and every word inside them stay and only the token
    /// leaves. Both inputs are built from the LITERAL above, never from the constant.
    /// </summary>
    [Theory]
    [InlineData(PinnedMaxGroupChars, true)]
    [InlineData(PinnedMaxGroupChars + 1, false)]
    public void AParentheticalIsRemovedWholeOnlyUpToTheBound(int groupChars, bool goesWhole)
    {
        const string token = "chapter-text:0";
        // "(" + filler + " " + token + ")" - three characters of frame plus the token.
        var filler = new string('x', groupChars - token.Length - 3);
        var group = $"({filler} {token})";
        Assert.Equal(groupChars, group.Length);

        var leaked = $"Some prose {group} and more.";
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertCount(1, removed, "the strip did not report removing the token inside the parenthetical.");

        if (goesWhole)
            AssertText("Some prose and more.", text,
                $"a parenthetical of exactly {groupChars} characters - the bound - did not go whole. The " +
                "bound is inclusive, and this is the side of it every measured leak sits on.");
        else
            AssertText($"Some prose ({filler}) and more.", text,
                $"a parenthetical of {groupChars} characters - ONE over the bound - lost more than its " +
                "token. Over the bound the brackets and every word inside them must stay, which is what " +
                "stops a mis-classified bracket costing the author a paragraph.");
    }

    // ─── The label vocabulary, DERIVED from the emitter's own formats (A9) ──────────────────────

    /// <summary>
    /// The whole/excerpt vocabulary, DISCOVERED from <see cref="BookArtifactBlocks"/>' own format
    /// constants rather than hand-copied. <c>ProductChatInternalLabels</c> restates it as three string
    /// literals while its own class doc argues, at length, that restating a vocabulary is precisely how
    /// an emitter and its parser drift apart - and nothing anywhere pinned the two together, so renaming
    /// the emitted label would have stopped the strip matching with every assertion in this file green.
    /// One side of this oracle is discovered and the other is the strip's real behaviour; neither is a
    /// second copy of the list.
    /// </summary>
    private static IEnumerable<string> PhrasesIn(string labelFormat)
    {
        foreach (var phrase in labelFormat.Trim('[', ']').Split(','))
        {
            // What is left of a comma-separated phrase once the "[CHAPTER n" frame is taken out of it is
            // the vocabulary the model reads and copies: "whole chapter", "EXCERPT", "not the whole
            // chapter".
            var words = phrase.Replace("{0}", " ").Replace("CHAPTER", " ").Trim();
            if (words.Length > 0) yield return words;
        }
    }

    private static List<string> EmittedLabelVocabulary() =>
        PhrasesIn(BookArtifactBlocks.WholeChapterLabelFormat)
            .Concat(PhrasesIn(BookArtifactBlocks.ExcerptLabelFormat))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static TheoryData<string> EmittedLabelPhrases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var phrase in EmittedLabelVocabulary()) data.Add(phrase);
            return data;
        }
    }

    /// <summary>
    /// The NON-VACUITY FLOOR under the theory below. A <c>MemberData</c> source that returns nothing does
    /// not fail - it makes the theory disappear, silently - so a reworded format that defeated
    /// <see cref="PhrasesIn"/> would take the whole A9 oracle with it and leave a green suite behind. The
    /// count is a floor and not a fourth copy of the vocabulary: it says the derivation still FOUND
    /// something, and it says how much.
    /// </summary>
    [Fact]
    public void TheVocabularyDerivedFromTheEmittersOwnFormats_IsNotEmpty()
    {
        var derived = EmittedLabelVocabulary();

        AssertCount(3, derived.Count,
            "the phrases derived from BookArtifactBlocks' own label formats are no longer the three this " +
            "file expects (" + string.Join(" | ", derived) + "). Either the emitter grew or lost a phrase - " +
            "in which case ProductChatInternalLabels.LabelFragments has to learn it, and this floor moves " +
            "with it - or PhrasesIn no longer parses the format, in which case the theory below is running " +
            "on nothing and is not testing what it claims to.");
    }

    /// <summary>
    /// A9. Every phrase the EMITTER puts inside its own label must be one the STRIP recognises as a gloss.
    /// Rename <c>[CHAPTER {0}, whole chapter]</c> to anything else and this goes red naming the phrase the
    /// strip no longer knows, instead of the strip quietly ceasing to match.
    ///
    /// <para>The frame carries TWO connective words ("that is,") deliberately. With no residue the longest
    /// phrase is not load-bearing - drop "not the whole chapter" from the strip's vocabulary and the
    /// shorter "whole chapter" inside it still qualifies the bracket, so the removal looks identical. At a
    /// residue of two, losing the long phrase pushes the leftover "not the" over
    /// <c>MaxResidueWords</c> and the gloss survives, which is the difference this datum exists to see
    /// (review mutant M20).</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EmittedLabelPhrases))]
    public void EveryPhraseTheEmitterPutsInsideItsOwnLabel_IsAGlossTheStripKnows(string phrase)
    {
        var leaked = $"I read part of it (that is, {phrase}), so I can speak to that part.";
        const string expected = "I read part of it, so I can speak to that part.";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            $"'{phrase}' is a phrase BookArtifactBlocks puts in front of the model inside its own chapter " +
            "label, and the strip did not recognise it as a gloss. The strip hand-copies this vocabulary " +
            "as string literals; if the emitter's wording moved, LabelFragments has to move with it.");
        AssertCount(1, removed, $"the gloss '({{that is, {phrase}}})' was not counted as one removal.");
    }

    public static TheoryData<string> EmittedLabelFormats => new()
    {
        BookArtifactBlocks.WholeChapterLabelFormat,
        BookArtifactBlocks.ExcerptLabelFormat,
    };

    /// <summary>
    /// The other half of A9, and the direction that catches a rename of the FRAME rather than of a phrase.
    /// The label the emitter actually renders, formatted with a real order, has to come out of the prose
    /// whole. Nothing else in this file pins the two files together: the English data in
    /// <see cref="ABracketedChapterLabel_IsRemovedWithItsBrackets"/> are hand-typed copies of these formats
    /// and would keep passing after a rename.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmittedLabelFormats))]
    public void TheLabelTheEmitterActuallyRenders_IsRemovedWithItsBrackets(string labelFormat)
    {
        var label = string.Format(labelFormat, 7);
        var leaked = $"The repetition happens in chapter 8 {label}.";
        const string expected = "The repetition happens in chapter 8.";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            $"the strip did not remove '{label}', which is the literal string BookArtifactBlocks renders " +
            "in front of the model. A rename on the emitter's side stops this layer matching, and every " +
            "hand-typed label datum in this file would go on passing.");
        Assert.Equal(1, removed);
    }

    // ─── What the strip must LEAVE ALONE: the mutation survivors (A8) ────────────────────────────

    /// <summary>
    /// Mutant M18. <c>BracketedLabel</c>'s tail is bounded at 40 characters, and that bound is the only
    /// thing separating an internal label from a bracketed ASIDE the model wrote for the author. Relax it
    /// and any bracket that happens to open with "CHAPTER &lt;n&gt;" is eaten whole, however many of the
    /// author's words are inside it - the same paragraph-costing failure
    /// <see cref="AParentheticalIsRemovedWholeOnlyUpToTheBound"/> guards on the round-bracket side.
    /// </summary>
    [Theory]
    [InlineData("[CHAPTER 3 and the harbour scene that runs for pages and pages beyond any label] stays.")]
    [InlineData("[פרק 3 והתיאור הארוך של הנמל שנמשך עמודים שלמים הרבה מעבר לאורך של תווית] נשאר.")]
    public void ABracketWhoseTailIsLongerThanALabel_IsProseAndIsLeftAlone(string prose)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(prose);

        AssertText(prose, text,
            "the strip ate a bracketed ASIDE as though it were an internal chapter label. The label regex's " +
            "tail bound is what tells the two apart: a label is label-sized, and a bracket holding a clause " +
            "is the author's to read.");
        Assert.Equal(0, removed);
    }

    /// <summary>
    /// Mutant M23. <c>SlugCandidate</c>'s lookbehind is the ONLY thing stopping a wire ref being cut out of
    /// the middle of a URL or a path, and there was no test for it. Removing the token from
    /// <c>/api/chapter-text:0</c> leaves the author reading "/api/", which is a mangled artifact rather
    /// than a tidied sentence - the over-strip direction this class refuses everywhere else. The ref is
    /// left where it is because a path is one token, not prose with a token in it.
    /// </summary>
    [Theory]
    [InlineData("The endpoint is /api/chapter-text:0 in the docs.")]
    [InlineData("See https://example.com/api/chapter-text:0 for the raw text.")]
    [InlineData("Open https://example.com/register to sign up.")]
    public void AnArtifactRefInsideAPathOrAUrl_IsLeftAlone(string prose)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(prose);

        AssertText(prose, text,
            "the strip reached INSIDE a URL or a path and cut a ref out of it, leaving a truncated link. A " +
            "slug preceded by '/', ':' or a word character is part of a longer token, not a ref standing in " +
            "prose.");
        Assert.Equal(0, removed);
    }

    /// <summary>
    /// Mutant M24. <c>Occurrences</c>' word boundary is the only thing stopping "excerpt" matching inside
    /// "excerpts" or "excerpted", and there was no test for it. A match inside a longer word does not just
    /// delete a word - it qualifies the whole bracket for removal, because what is left of the longer word
    /// counts as one residue word rather than a phrase, so "(excerpts)" would vanish entirely.
    /// </summary>
    [Theory]
    [InlineData("I have what you sent (excerpts), so I can speak to those parts.")]
    [InlineData("He excerpted the chapter (excerpted), which is fine.")]
    public void ALabelFragmentInsideALongerWord_IsNotAFragment(string prose)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(prose);

        AssertText(prose, text,
            "the strip matched a label fragment INSIDE a longer word and took the author's own word - and " +
            "with it the whole bracket, because the letters left over read as a short residue rather than " +
            "as a word.");
        Assert.Equal(0, removed);
    }

    /// <summary>
    /// The underscore that is deliberately NOT in <c>WrapperChars</c>. <c>SlugCandidate</c>'s trailing
    /// lookahead does not exclude "_", so a slug can end directly against one; treating the underscore as
    /// an emphasis delimiter would let the removal swallow the space in front of the token and glue the
    /// leftover to the word before it. The token goes, the neighbouring word keeps its own boundary.
    /// </summary>
    [Fact]
    public void ARemovalDoesNotReachAcrossAnUnderscoreIntoTheWordBesideIt()
    {
        const string leaked = "Here is chapter-text:0_suffix in the file.";
        const string expected = "Here is _suffix in the file.";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a removal treated '_' as an emphasis wrapper and reached across it, gluing what was left to " +
            "the word before it. Underscore is a word character in every language this ships in.");
        Assert.Equal(1, removed);
    }

    /// <summary>
    /// <c>BareBookBrief</c>'s own boundaries, which are what make the ONE keyless ref safe to strip
    /// wherever it stands. Without them "book-brief" matches inside any longer hyphenated token and the
    /// strip cuts a hole in a word.
    /// </summary>
    [Theory]
    [InlineData("The abook-briefy token is not a ref.")]
    [InlineData("The book-brief-summary is not the same thing.")]
    public void TheKeylessRefInsideALongerToken_IsNotThatRef(string prose)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(prose);

        AssertText(prose, text,
            "the strip matched 'book-brief' inside a longer token and cut a hole in a word. The keyless ref " +
            "is safe to remove wherever it STANDS, which is not the same as wherever it appears.");
        Assert.Equal(0, removed);
    }

    // ─── The never-empty contract (A3) ──────────────────────────────────────────────────────────

    /// <summary>
    /// DECISION 6, and the failure it prevents is the worst one this layer can cause. Every input below is
    /// an answer whose WHOLE content is internal, so the strip's own rules would delete all of it and hand
    /// the reader an empty card - which the service then ships with <c>IsGrounded: true</c> and no fault,
    /// because its emptiness check is upstream on the model's raw content. <c>ProductChatCitations</c>
    /// carries this same guard at three separate sites for the same reason: a leaked label is cosmetic, a
    /// deleted answer is not.
    /// </summary>
    [Theory]
    [InlineData("(EXCERPT)")]
    [InlineData("(whole chapter)")]
    [InlineData("[CHAPTER 7, whole chapter]")]
    [InlineData("chapter-text:0")]
    [InlineData("book-brief")]
    [InlineData("**chapter-text:0**")]
    [InlineData("(EXCERPT)\n\n(whole chapter)")]
    [InlineData("*   (EXCERPT)")]
    [InlineData("(EXCERPT).")]
    public void AnAnswerThatIsNothingBUTInternalTokens_ComesBackWHOLE_RatherThanEmpty(string answer)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(answer);

        Assert.False(string.IsNullOrWhiteSpace(text),
            "the strip returned an EMPTY answer. Every word of this one was internal, so removing them all " +
            "leaves the author a card that says nothing and still claims to be grounded. Leaving the jargon " +
            $"in is the safe direction and the contract.\nINPUT: {answer}\nGOT  : '{text}'");

        AssertText(answer, text,
            "the strip refused to empty the answer but did not return it INTACT. All or nothing is the " +
            "contract: a partial strip would be choosing which words of the sentence the author gets.");

        AssertCount(0, removed,
            "the strip reported tokens REMOVED from an answer it returned unchanged. Nothing was removed; " +
            "the tokens it declined to remove are reported separately, and a caller logging this count " +
            "would otherwise claim a rewrite that did not happen.");
    }

    /// <summary>
    /// The refusal is REPORTED, not swallowed. Without this the service cannot tell a refusal from a clean
    /// answer, and a gate recovering the model's emission rate from the removal count would silently
    /// under-count exactly the answers that leaked most.
    /// </summary>
    [Fact]
    public void TheTokensARefusalLeavesInPlace_AreCountedSoTheModelsRateStaysHonest()
    {
        var (text, removed) = ProductChatInternalLabels.Strip("(EXCERPT) (whole chapter)", out var kept);

        AssertText("(EXCERPT) (whole chapter)", text,
            "an answer made only of glosses did not come back whole, so there is nothing for the kept " +
            "count to be a count OF.");
        Assert.Equal(0, removed);
        AssertCount(2, kept,
            "the strip refused to empty the answer but reported the wrong number of tokens kept. That " +
            "number is the ONLY record that the model emitted them: the removal count is 0 on this path, " +
            "so a gate reading removals alone would score this answer as clean.");
    }

    /// <summary>
    /// The other side of the same report, and the reason it is a separate number: an ordinary strip must
    /// say it KEPT nothing, or the refusal signal is on for every answer and means nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeasuredLeaks))]
    public void AnOrdinaryStrip_ReportsNoRefusal(
        string leaked, string expected, int expectedRemovals, string what)
    {
        _ = expected;

        var (_, removed) = ProductChatInternalLabels.Strip(leaked, out var kept);

        AssertCount(0, kept,
            $"a real measured leak ({what}) reported tokens kept to avoid emptying the answer, but its " +
            "sentence survived the strip. A refusal signal that is on for ordinary answers cannot be read " +
            "as a refusal.");
        Assert.Equal(expectedRemovals, removed);
    }

    // ─── Edge cases of the narrowed surface (A11-A14) ─────────────────────────────────────────────

    /// <summary>
    /// A11. A bidi control mark sits directly against an LTR slug in RTL prose to fix its rendering
    /// direction, with no space between the mark and the token - real usage, not a transliteration. A bare
    /// removal orphans the marks: they are not whitespace, so <c>DoubledSpace</c>-style collapsing cannot
    /// reach them, and an un-consumed embedding initiator (LRE/RLE) still opens a directional run the rest
    /// of the answer never closes. <see cref="ProductChatInternalLabels.Strip(string?)"/> now consumes them
    /// with the token, the same way it already consumes an emptied markdown wrapper.
    /// </summary>
    [Theory]
    [InlineData("בקובץ \u200Fchapter-text:0\u200F כתוב.", "בקובץ כתוב.")] // RLM both sides
    [InlineData("in the file \u200Echapter-text:0\u200E it says.", "in the file it says.")] // LRM both sides
    [InlineData("בקובץ \u202Bchapter-text:0\u202C כתוב.", "בקובץ כתוב.")] // RLE ... PDF
    public void ABidiMarkOrphanedByASlugRemoval_GoesWithTheSlug(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "a slug removal orphaned the bidi control mark(s) that bounded it. They are not whitespace, so " +
            "no later pass can collapse them, and an embedding mark left open corrupts everything after it" +
            $" on the line.\nEXPECTED: {Visible(expected)}\nACTUAL  : {Visible(text)}");
        Assert.Equal(1, removed);
    }

    /// <summary>
    /// A12. A markdown HARD LINE BREAK is two or more trailing spaces on a line, and it must survive a strip
    /// that fired somewhere else on that same line - otherwise the same document renders two ways depending
    /// on whether a token happened to leak on a hard-broken line, which is not a decision this layer is
    /// entitled to make.
    /// </summary>
    [Fact]
    public void AHardLineBreak_SurvivesAStripThatFiredEarlierOnTheSameLine()
    {
        const string leaked = "See chapter-text:0 for details.  \nNext paragraph.";
        const string expected = "See for details.  \nNext paragraph.";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(expected, text,
            "the strip ate a markdown hard line break (two trailing spaces) on a line it touched elsewhere, " +
            $"merging two lines into one paragraph.\nEXPECTED: {Visible(expected)}\nACTUAL  : {Visible(text)}");
        Assert.Equal(1, removed);
        Assert.EndsWith("details.  ", text.Split('\n')[0]);
    }

    /// <summary>
    /// A13. A list item whose ENTIRE content is an internal token must not collapse to a bare marker mid
    /// list: "- chapter-text:0" becoming "-" reads as an empty bullet to a reader and a broken item to a
    /// markdown renderer. The line is left ENTIRELY ALONE rather than shipped as a marker with nothing
    /// after it - the same under-strip-over-over-strip choice DECISION 6 makes at the whole-answer scope,
    /// applied here at the line.
    ///
    /// <para>final-r01: AND IT IS REPORTED AS A REFUSAL, for DECISION 6's reason at this smaller scope. It
    /// used to be reported by NEITHER number, so an answer leaking a whole-token list item came back with
    /// the leak still in it, <c>Removed</c> 0 and <c>keptToAvoidEmptying</c> 0 - indistinguishable from a
    /// clean answer to the only pre-strip signal this program has, while the class doc claimed no refusal
    /// could silently deflate that rate.</para>
    /// </summary>
    [Theory]
    [InlineData("Intro line.\n- chapter-text:0\n- second item", "Intro line.\n- chapter-text:0\n- second item")]
    [InlineData("Intro line.\n1. chapter-text:0\n2. second item", "Intro line.\n1. chapter-text:0\n2. second item")]
    [InlineData("Intro line.\n*   book-brief\n*   second item", "Intro line.\n*   book-brief\n*   second item")]
    public void AListItemThatWouldCollapseToABareMarker_IsLeftEntirelyAlone(string leaked, string expected)
    {
        var (text, removed) = ProductChatInternalLabels.Strip(leaked, out var kept);

        AssertText(expected, text,
            "a list item whose whole content was internal tokens was reduced to a bare marker, an empty " +
            $"bullet mid-list.\nEXPECTED: {Visible(expected)}\nACTUAL  : {Visible(text)}");
        AssertCount(0, removed, "nothing was removed here: the whole line was put back.");
        AssertCount(1, kept,
            "the line-level refusal reported NOTHING. The token is still in the prose the author reads and " +
            "both counters say the answer was clean, so a gate reading this log under-counts the model's " +
            "own emission rate - the one property the class doc promises these two numbers hold.");
    }

    /// <summary>
    /// A14. A FENCED code block is content, not prose, matching <c>ProductChatPunctuation.Repair</c>'s
    /// policy for the same construct: neither layer edits inside one. (Cited by its words rather than by a
    /// line range - the bullet beginning "Text inside backticks is copied verbatim" - because a line range
    /// into another file goes stale the first time a sibling todo edits it, which is what happened to the
    /// ":38-41" this replaced: that range now lands on the U+2014 bullet above it.) A bare INLINE
    /// span deliberately does NOT get the same protection - see <see cref="AWrapperEmptiedByASlugRemoval_GoesWithTheSlug"/>,
    /// which already pins that a bare backtick-wrapped slug in running prose is removed, wrapper and all.
    /// </summary>
    [Fact]
    public void AFencedCodeBlock_IsLeftEntirelyAlone()
    {
        const string leaked = "See the example below.\n```\nchapter-text:0\n```\nThat is not for you.";

        var (text, removed) = ProductChatInternalLabels.Strip(leaked);

        AssertText(leaked, text,
            "the strip edited inside a fenced code block, which ProductChatPunctuation.Repair deliberately " +
            $"leaves alone.\nBEFORE: {Visible(leaked)}\nAFTER : {Visible(text)}");
        Assert.Equal(0, removed);
    }
}
