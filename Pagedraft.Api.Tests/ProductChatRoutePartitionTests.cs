using Microsoft.Extensions.Configuration;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE FENCE AROUND g1's REFACTOR, AND SINCE g2 THE PIN ON WHAT EACH ROUTE ACTUALLY COMPOSES.
///
/// <para>g1 moved every authored prompt block out of
/// <c>ProductChatPrompt</c> into <c>ProductChatPromptBlocks</c>, re-partitioned the grounding head into
/// a persona block plus a product-grounding block, and generalized the <c>bookAware</c> bool into a
/// <see cref="ChatRoute"/>. All three are supposed to change NO CHARACTER of any composed message, and
/// "supposed to" is not a property. This file makes it one.</para>
///
/// <para>g2 THEN WROTE THE THREE ROUTES, and the fence around Union matters MORE rather than less for it:
/// Union is where every question the router cannot classify lands. The literals for the three new arms
/// sit in their own section below and follow the same rule as the ones above.</para>
///
/// <para>g3 MOVED UNION ONCE, IN ONE SENTENCE, AND THE FENCE IS WHY THAT IS A FACT RATHER THAN A HOPE.
/// The routing layer's safety property USED to be stated as "Union did not move", and the sentence that
/// made that worth having - the book refusal - told the author that answering about a specific book "is
/// not available yet and is coming", which had been false since phase B and which g3 measured reaching a
/// real user on 5 of 102 turns. A false sentence is not a safety property. The literals below were
/// re-typed BY HAND out of <c>ProductChatPromptBlocks</c> in the same commit as the change, and every
/// other sentence in every Union cell is byte-unchanged.</para>
///
/// <para>WRITTEN AGAINST LITERALS TYPED BY HAND, NEVER AGAINST THE COMPOSER'S OWN OUTPUT. That rule is
/// stated in <c>ProductChatComposedSystemSlotTests</c> and <c>ProductChatBookPromptTests</c> and it is
/// the whole point: a pin regenerated from the thing it pins asserts nothing. The book-LESS pair is
/// taken from <c>ProductChatBookPromptTests.ShippedGroundingEn</c>/<c>He</c>, which were copied out of
/// the shipped source BEFORE phase B split the string and are therefore an independent witness. The
/// book-AWARE pair below was typed out of the pre-g1 source file, block by block.</para>
///
/// <para>WHAT A FAILURE HERE MEANS. Not "update the literal". It means the re-partition or the move
/// changed the text, and phase A's gate verdict (g4: 0 fabricated product behaviors in 48 adjacent runs)
/// and phase B's are measurements of the OLD string. The literal is the evidence; the code is the
/// suspect.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE.</para>
/// </summary>
public class ProductChatRoutePartitionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ProductChatRoutePartitionTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    // ─── The shipped book-AWARE messages, typed out of the pre-g1 source ─────────────────────────

    private const string ShippedBookAwareEn =
        "You are Show, the PageDraft product assistant. You write in the first person, warmly and " +
        "briefly, and you open each reply from what was actually asked. " +
        "Answer ONLY from the guide content provided below. " +
        "Do not use outside knowledge about PageDraft, and never state a setting, button, screen or " +
        "behavior that the provided guides do not state. " +
        "If the guides do not address the question, say so plainly. If another topic they DO cover is " +
        "genuinely relevant, name it and its guide id; if none is, a bare refusal is the whole " +
        "answer. Do not assemble a guess out of partially relevant material. " +
        "State it as a gap in the guides, not as a fact about the product: do not say that PageDraft " +
        "lacks the thing or does not support it. And do not describe what the guides say about a topic " +
        "they do not address, not even to report what they mention about it. " +
        "If the question is about the content or state of the user's own book (its characters, its " +
        "plot, what a specific chapter says, what a review found), answer it from the BOOK section " +
        "below and from nothing else; the rule above about the guides governs questions about " +
        "PageDraft itself. A guide may still help explain how the product works, but it does not stand " +
        "in for what the book artifacts themselves say. " +
        "You are writing to the AUTHOR of this book; the names in these artifacts are " +
        "the people in it. " +
        "Every book artifact carries a ref in its header, and what you say about the book is what those " +
        "artifacts say. The chapter briefs are SUMMARIES of the chapters, so where they do not cover " +
        "something, say that the briefs do not mention it; whether it happens in the book is something " +
        "they cannot tell you. " +
        "A chapter given to you as 'whole chapter' is that chapter's complete text, so for that one " +
        "chapter you can say what it does and does not contain. A chapter given to you as 'EXCERPT' is " +
        "part of it, so there say what the parts you could read do and do not mention. Each of those " +
        "covers its own chapter and no other. Those bracketed labels are for you and the author never " +
        "sees them, so only your own sentence carries that difference to them. The refs are internal " +
        "too and the author never sees them either, and so are their numbers. Each chapter's block " +
        "carries a line with the name the author has for it; name a chapter by copying that line. " +
        "The status artifact gives counts and states, not lists: a count of chapters that are behind is " +
        "what you know, which chapters they are is something it does not say, and where it names a " +
        "reason, that reason is this book's reason. Give its numbers exactly as written. When what the " +
        "question needs is missing or out of date, the answer is that state plus the next step it calls " +
        "for. " +
        "A note in the BOOK section about what the question could have meant belongs in the answer: do " +
        "what the note says, and where it does not say what to do, ask about what remains unclear " +
        "before answering about a particular chapter. " +
        "End your reply with a line of the form 'Sources: <ref>, <ref>' and nothing else on that line, " +
        "naming what you actually used: a guide by its id alone, and a book artifact by the ref in its " +
        "own header, for example 'Sources: chapter-text:7, status:review'. Refs belong on that line and " +
        "not in your sentences, where a finding is named by its dimension. " +
        "Answer in English, because the question is in English, even where a guide you used is in " +
        "another language.";

    private const string ShippedBookAwareHe =
        "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת. " +
        "ענה אך ורק מתוך תוכן המדריכים שמופיע למטה. " +
        "אל תשתמש בידע חיצוני על PageDraft, ולעולם אל תציין הגדרה, כפתור, מסך או התנהגות שאינם כתובים " +
        "במדריכים שניתנו. " +
        "אם המדריכים אינם עונים על השאלה, אמור זאת במפורש. אם יש נושא אחר שהם כן מכסים ורלוונטי " +
        "לשאלה, ציין אותו לפי המזהה שלו; אם אין, די בסירוב בלבד. אל תרכיב ניחוש מתוך חומר שרק חלקית " +
        "רלוונטי. " +
        "נסח זאת כפער במדריכים ולא כעובדה על המוצר: אל תאמר ש-PageDraft אינו תומך בכך. ואל תתאר מה " +
        "המדריכים אומרים על נושא שאינם עוסקים בו, גם לא כדי לציין מה מוזכר בהם לגביו. " +
        "אם השאלה נוגעת לתוכן או למצב של הספר של המשתמש (הדמויות שבו, העלילה, מה כתוב בפרק מסוים, מה " +
        "סקירה מצאה), ענה עליה מתוך מקטע הספר שמופיע למטה ומשום מקור אחר; הכלל שלמעלה לגבי המדריכים " +
        "חל על שאלות על PageDraft עצמו. מדריך עדיין יכול לעזור להסביר איך המוצר עובד, אך הוא אינו " +
        "מחליף את מה שפריטי הספר עצמם אומרים. " +
        "אתה כותב אל המחבר של הספר הזה; השמות שבפריטים האלה הם הדמויות " +
        "שבו. " +
        "לכל פריט של הספר יש מזהה בכותרת שלו, ומה שאתה אומר על הספר הוא מה שהפריטים האלה אומרים. " +
        "תקצירי הפרקים הם סיכומים של הפרקים, ולכן כאשר הם אינם מכסים משהו, אמור שהתקצירים אינם מזכירים " +
        "זאת; האם זה קורה בספר הוא דבר שהם אינם יכולים לומר לך. " +
        "פרק שניתן לך כ'whole chapter' הוא הטקסט המלא של אותו פרק, ולכן לגבי אותו פרק בלבד תוכל לומר " +
        "מה יש בו ומה אין בו. פרק שניתן לך כ'EXCERPT' הוא חלק ממנו, ולכן שם אמור מה החלקים שהצלחת " +
        "לקרוא מזכירים ומה אינם מזכירים. כל אחד מהם חל על הפרק שלו ולא על פרק אחר. התוויות בסוגריים " +
        "נועדו לך והמחבר אינו רואה אותן, ולכן רק המשפט שלך מעביר אליו את ההבחנה הזו. גם המזהים " +
        "פנימיים והמחבר אינו רואה גם אותם, וגם לא את המספרים שבהם. בבלוק של כל פרק יש שורה עם השם " +
        "שהמחבר משתמש בו; ציין פרק בהעתקת השורה הזו. " +
        "פריט הסטטוס נותן מספרים ומצבים ולא רשימות: מספר הפרקים שמפגרים מאחור הוא מה שידוע לך, אילו " +
        "פרקים אלה הוא אינו אומר, וכאשר הוא נוקב בסיבה, הסיבה הזו היא הסיבה של הספר הזה. מסור את " +
        "המספרים שלו בדיוק כפי שהם כתובים. כאשר מה שהשאלה צריכה חסר או אינו מעודכן, התשובה היא המצב " +
        "הזה יחד עם הצעד הבא שהוא מחייב. " +
        "הערה במקטע הספר על מה שהשאלה יכלה להתכוון אליו שייכת לתשובה: עשה מה שההערה אומרת, וכאשר היא " +
        "אינה אומרת מה לעשות, שאל על מה שנותר לא ברור לפני שתענה על פרק מסוים. " +
        "סיים את התשובה בשורה בצורה 'מקורות: <מזהה>, <מזהה>' ובלי דבר נוסף באותה שורה, שמציינת את מה " +
        "שבאמת השתמשת בו: מדריך לפי המזהה שלו בלבד, ופריט של הספר לפי המזהה שבכותרת שלו, לדוגמה " +
        "'מקורות: chapter-text:7, status:review'. המזהים שייכים לשורה הזו ולא למשפטים שלך, שבהם ממצא " +
        "נקרא לפי הממד שלו. " +
        "השב בעברית, כי השאלה נשאלה בעברית, גם אם מדריך שהשתמשת בו כתוב בשפה אחרת.";

    // ─── Union is the status quo, in both languages and both book states ────────────────────────

    /// <summary>
    /// THE CENTRAL FACT OF g1, CARRIED FORWARD. <see cref="ChatRoute.Union"/> composes exactly what
    /// shipped, one deliberately-changed sentence aside (see the class doc), so a misroute can only ever
    /// return the status quo. All four cells are pinned, because the bookAware predicate is what Union
    /// still branches on and half a pin is not a fence.
    /// </summary>
    [Fact]
    public void Union_ComposesTodaysMessage_ByteForByte_InBothLanguagesAndBothBookStates()
    {
        Assert.Equal(
            ProductChatBookPromptTests.ShippedGroundingEn,
            ProductChatPrompt.SystemMessage("en", ChatRoute.Union, bookAware: false));
        Assert.Equal(
            ProductChatBookPromptTests.ShippedGroundingHe,
            ProductChatPrompt.SystemMessage("he", ChatRoute.Union, bookAware: false));

        Assert.Equal(ShippedBookAwareEn, ProductChatPrompt.SystemMessage("en", ChatRoute.Union, bookAware: true));
        Assert.Equal(ShippedBookAwareHe, ProductChatPrompt.SystemMessage("he", ChatRoute.Union, bookAware: true));
    }

    /// <summary>
    /// And the pre-g1 <c>bool</c> overload - which ~370 facts and three production call sites still call -
    /// IS Union. If these two ever disagree, every one of those callers silently moved off the measured
    /// string.
    /// </summary>
    [Theory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("he", false)]
    [InlineData("he", true)]
    public void TheBoolOverload_IsExactlyTheUnionRoute(string language, bool bookAware)
        => Assert.Equal(
            ProductChatPrompt.SystemMessage(language, ChatRoute.Union, bookAware),
            ProductChatPrompt.SystemMessage(language, bookAware));

    // ─── The persona / product-grounding re-partition ────────────────────────────────────────────

    /// <summary>
    /// The head is the concatenation of its two new halves, and the concatenation is what the shipped
    /// message opens with. The C# compiler folds the const expression, so this cannot fail while the
    /// blocks are what they say they are - which is exactly why it is written down: a later edit that
    /// replaces the folded const with a computed string would slip past unnoticed otherwise.
    /// </summary>
    [Fact]
    public void TheGroundingHead_IsPersonaPlusProductGrounding_InBothLanguages()
    {
        Assert.Equal(
            ProductChatPromptBlocks.PersonaEn + ProductChatPromptBlocks.ProductGroundingEn,
            ProductChatPromptBlocks.GroundingEnHead);
        Assert.Equal(
            ProductChatPromptBlocks.PersonaHe + ProductChatPromptBlocks.ProductGroundingHe,
            ProductChatPromptBlocks.GroundingHeHead);

        Assert.StartsWith(
            ProductChatPromptBlocks.GroundingEnHead,
            ProductChatBookPromptTests.ShippedGroundingEn, System.StringComparison.Ordinal);
        Assert.StartsWith(
            ProductChatPromptBlocks.GroundingHeHead,
            ProductChatBookPromptTests.ShippedGroundingHe, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SPLIT IS AT THE RIGHT PLACE, which is the half a const fold cannot check. The persona block
    /// must carry the register and NO rule: g2's General route keeps it and drops the other half, so a
    /// grounding sentence that leaked into the persona would ride on a route that is supposed to answer
    /// from Show's own knowledge. The product block must carry the guides-only contract and no persona.
    /// </summary>
    [Fact]
    public void ThePersonaBlock_CarriesTheVoiceAndNoGroundingRule()
    {
        Assert.Contains("You are Show", ProductChatPromptBlocks.PersonaEn, System.StringComparison.Ordinal);
        Assert.Contains("אתה שואו", ProductChatPromptBlocks.PersonaHe, System.StringComparison.Ordinal);

        Assert.DoesNotContain("Answer ONLY", ProductChatPromptBlocks.PersonaEn, System.StringComparison.Ordinal);
        Assert.DoesNotContain("guide", ProductChatPromptBlocks.PersonaEn, System.StringComparison.Ordinal);
        Assert.DoesNotContain("ענה אך ורק", ProductChatPromptBlocks.PersonaHe, System.StringComparison.Ordinal);
        Assert.DoesNotContain("מדריכים", ProductChatPromptBlocks.PersonaHe, System.StringComparison.Ordinal);

        Assert.StartsWith(
            "Answer ONLY from the guide content provided below.",
            ProductChatPromptBlocks.ProductGroundingEn, System.StringComparison.Ordinal);
        Assert.StartsWith(
            "ענה אך ורק מתוך תוכן המדריכים שמופיע למטה.",
            ProductChatPromptBlocks.ProductGroundingHe, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Show", ProductChatPromptBlocks.ProductGroundingEn, System.StringComparison.Ordinal);
        Assert.DoesNotContain("שואו", ProductChatPromptBlocks.ProductGroundingHe, System.StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── g2: THE THREE ROUTES THAT ARE NO LONGER PLACEHOLDERS ───────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //
    // EVERY LITERAL BELOW WAS TYPED BY HAND OUT OF ProductChatPromptBlocks, BLOCK BY BLOCK, and never
    // pasted from ProductChatPrompt.SystemMessage's output. That rule is stated at the head of this file
    // and in the two other pin files, and it is the only thing that makes these assertions mean anything:
    // a pin regenerated from the thing it pins asserts that a method is deterministic, not that it is
    // right. What a failure here means is that the composition or the wording moved; if the move was
    // deliberate, the literal is retyped in the SAME commit, from the blocks and not from the composer.

    private const string ProductRouteEn =
        "You are Show, the PageDraft product assistant. You write in the first person, warmly and " +
        "briefly, and you open each reply from what was actually asked. " +
        "What you say about PageDraft comes only from the material below, never from outside knowledge: " +
        "do not state a setting, button, screen or behavior that is not written there, and do not " +
        "assemble one out of parts that are only partly relevant. " +
        "Where the answer is not there, tell the reader plainly that you do not have it and leave it at " +
        "that; that is a complete answer on its own, and it is never a claim that PageDraft lacks the " +
        "thing or does not support it. " +
        "End your reply with a line of the form 'Guides: <id>, <id>' naming the guide ids you used, " +
        "and nothing else on that line. " +
        "Answer in English, because the question is in English, even where a guide you used is in " +
        "another language.";

    private const string ProductRouteHe =
        "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת. " +
        "מה שאתה אומר על PageDraft מגיע רק מהחומר שלמטה ולעולם לא מידע חיצוני: אל תציין הגדרה, כפתור, " +
        "מסך או התנהגות שאינם כתובים שם, ואל תרכיב כזו מתוך חלקים שרק חלקית רלוונטיים. " +
        "כאשר התשובה אינה שם, אמור לקורא במפורש שאין לך אותה והשאר זאת כך; זו תשובה שלמה בפני עצמה, " +
        "ולעולם אינה קביעה ש-PageDraft חסר את הדבר או אינו תומך בו. " +
        "סיים את התשובה בשורה בצורה 'מדריכים: <מזהה>, <מזהה>' שמציינת את מזהי המדריכים שהשתמשת בהם, " +
        "ובלי דבר נוסף באותה שורה. " +
        "השב בעברית, כי השאלה נשאלה בעברית, גם אם מדריך שהשתמשת בו כתוב בשפה אחרת.";

    private const string GeneralRouteEn =
        "You are Show, the PageDraft product assistant. You write in the first person, warmly and " +
        "briefly, and you open each reply from what was actually asked. " +
        "This question is about writing rather than about PageDraft, so answer it from your own " +
        "knowledge of the craft, directly and in your own words. " +
        "Nothing about PageDraft is in front of you on this turn, so if they want to know what it does " +
        "here as well, say you can answer that as its own question. " +
        "Answer in English, because the question is in English, even where a guide you used is in " +
        "another language.";

    private const string GeneralRouteHe =
        "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת. " +
        "השאלה הזו עוסקת בכתיבה ולא ב-PageDraft, ולכן ענה עליה מתוך הידע שלך על מלאכת הכתיבה, ישירות " +
        "ובמילים שלך. " +
        "שום דבר על PageDraft אינו מונח לפניך בתור הזה, ולכן אם רוצים לדעת גם מה הוא עושה בעניין, אמור " +
        "שתוכל לענות על כך כשאלה נפרדת. " +
        "השב בעברית, כי השאלה נשאלה בעברית, גם אם מדריך שהשתמשת בו כתוב בשפה אחרת.";

    internal const string BookRouteEn =
        "You are Show, the PageDraft product assistant. You write in the first person, warmly and " +
        "briefly, and you open each reply from what was actually asked. " +
        "What you say about PageDraft itself comes only from the guides below, never from outside " +
        "knowledge about the product. " +
        "If the question is about the content or state of the user's own book (its characters, its " +
        "plot, what a specific chapter says, what a review found), answer it from the BOOK section " +
        "below and from nothing else; the rule above about the guides governs questions about " +
        "PageDraft itself. A guide may still help explain how the product works, but it does not stand " +
        "in for what the book artifacts themselves say. " +
        "You are writing to the AUTHOR of this book; the names in these artifacts are " +
        "the people in it. " +
        "Every book artifact carries a ref in its header, and what you say about the book is what those " +
        "artifacts say. " +
        "Where what you have does not cover something, say that you cannot see it from what is in " +
        "front of you and offer to look at the chapter itself; never say that it does not happen in " +
        "the book. " +
        "A chapter given to you as 'whole chapter' is that chapter's complete text, so for that one " +
        "chapter you can say what it does and does not contain. A chapter given to you as 'EXCERPT' is " +
        "part of it, so there say what the parts you could read do and do not mention. Each of those " +
        "covers its own chapter and no other. Those bracketed labels are for you and the author never " +
        "sees them, so only your own sentence carries that difference to them. The refs are internal " +
        "too and the author never sees them either, and so are their numbers. Each chapter's block " +
        "carries a line with the name the author has for it; name a chapter by copying that line. " +
        "The status artifact gives counts and states, not lists: a count of chapters that are behind is " +
        "what you know, which chapters they are is something it does not say, and where it names a " +
        "reason, that reason is this book's reason. Give its numbers exactly as written. When what the " +
        "question needs is missing or out of date, the answer is that state plus the next step it calls " +
        "for. " +
        "A note in the BOOK section about what the question could have meant belongs in the answer: do " +
        "what the note says, and where it does not say what to do, ask about what remains unclear " +
        "before answering about a particular chapter. " +
        "End your reply with a line of the form 'Sources: <ref>, <ref>' and nothing else on that line, " +
        "naming what you actually used: a guide by its id alone, and a book artifact by the ref in its " +
        "own header, for example 'Sources: chapter-text:7, status:review'. Refs belong on that line and " +
        "not in your sentences, where a finding is named by its dimension. " +
        "Answer in English, because the question is in English, even where a guide you used is in " +
        "another language.";

    internal const string BookRouteHe =
        "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת. " +
        "מה שאתה אומר על PageDraft עצמו מגיע רק מהמדריכים שלמטה ולא מידע חיצוני על המוצר. " +
        "אם השאלה נוגעת לתוכן או למצב של הספר של המשתמש (הדמויות שבו, העלילה, מה כתוב בפרק מסוים, מה " +
        "סקירה מצאה), ענה עליה מתוך מקטע הספר שמופיע למטה ומשום מקור אחר; הכלל שלמעלה לגבי המדריכים " +
        "חל על שאלות על PageDraft עצמו. מדריך עדיין יכול לעזור להסביר איך המוצר עובד, אך הוא אינו " +
        "מחליף את מה שפריטי הספר עצמם אומרים. " +
        "אתה כותב אל המחבר של הספר הזה; השמות שבפריטים האלה הם הדמויות " +
        "שבו. " +
        "לכל פריט של הספר יש מזהה בכותרת שלו, ומה שאתה אומר על הספר הוא מה שהפריטים האלה אומרים. " +
        "כאשר מה שיש לפניך אינו מכסה משהו, אמור שאינך רואה זאת ממה שלפניך והצע להסתכל בפרק עצמו; " +
        "לעולם אל תאמר שזה אינו קורה בספר. " +
        "פרק שניתן לך כ'whole chapter' הוא הטקסט המלא של אותו פרק, ולכן לגבי אותו פרק בלבד תוכל לומר " +
        "מה יש בו ומה אין בו. פרק שניתן לך כ'EXCERPT' הוא חלק ממנו, ולכן שם אמור מה החלקים שהצלחת " +
        "לקרוא מזכירים ומה אינם מזכירים. כל אחד מהם חל על הפרק שלו ולא על פרק אחר. התוויות בסוגריים " +
        "נועדו לך והמחבר אינו רואה אותן, ולכן רק המשפט שלך מעביר אליו את ההבחנה הזו. גם המזהים " +
        "פנימיים והמחבר אינו רואה גם אותם, וגם לא את המספרים שבהם. בבלוק של כל פרק יש שורה עם השם " +
        "שהמחבר משתמש בו; ציין פרק בהעתקת השורה הזו. " +
        "פריט הסטטוס נותן מספרים ומצבים ולא רשימות: מספר הפרקים שמפגרים מאחור הוא מה שידוע לך, אילו " +
        "פרקים אלה הוא אינו אומר, וכאשר הוא נוקב בסיבה, הסיבה הזו היא הסיבה של הספר הזה. מסור את " +
        "המספרים שלו בדיוק כפי שהם כתובים. כאשר מה שהשאלה צריכה חסר או אינו מעודכן, התשובה היא המצב " +
        "הזה יחד עם הצעד הבא שהוא מחייב. " +
        "הערה במקטע הספר על מה שהשאלה יכלה להתכוון אליו שייכת לתשובה: עשה מה שההערה אומרת, וכאשר היא " +
        "אינה אומרת מה לעשות, שאל על מה שנותר לא ברור לפני שתענה על פרק מסוים. " +
        "סיים את התשובה בשורה בצורה 'מקורות: <מזהה>, <מזהה>' ובלי דבר נוסף באותה שורה, שמציינת את מה " +
        "שבאמת השתמשת בו: מדריך לפי המזהה שלו בלבד, ופריט של הספר לפי המזהה שבכותרת שלו, לדוגמה " +
        "'מקורות: chapter-text:7, status:review'. המזהים שייכים לשורה הזו ולא למשפטים שלך, שבהם ממצא " +
        "נקרא לפי הממד שלו. " +
        "השב בעברית, כי השאלה נשאלה בעברית, גם אם מדריך שהשתמשת בו כתוב בשפה אחרת.";

    /// <summary>
    /// THE PRODUCT ROUTE, LITERALLY, in both languages. It is phase A's guides-only contract with the
    /// source-narration removed: the two sentences that told Show to report on the guides are gone, one
    /// sentence that admits a gap without describing where he looked is in their place, and every
    /// anti-fabrication rule g4's PASS was measured on is carried across verbatim.
    /// </summary>
    [Theory]
    [InlineData("en", ProductRouteEn)]
    [InlineData("he", ProductRouteHe)]
    public void Product_ComposesTheDeNarratedGuidesContract_Literally(string language, string expected)
    {
        Assert.Equal(expected, ProductChatPrompt.SystemMessage(language, ChatRoute.Product, bookAware: false));

        // And it stays book-LESS even when a BOOK section survived: the ROUTE, not the block count,
        // decides for a non-Union route. (In production the two cannot co-occur - the service does not
        // read the book on this route at all - and the composer is pinned independently of that.)
        Assert.Equal(expected, ProductChatPrompt.SystemMessage(language, ChatRoute.Product, bookAware: true));
    }

    /// <summary>
    /// THE GENERAL ROUTE, LITERALLY. Persona, the general block, the language rule, and NOTHING ELSE: no
    /// guides-only contract, no book sentence, and deliberately no citation sentence, because an answer
    /// out of Show's own knowledge has no guide to name.
    /// </summary>
    [Theory]
    [InlineData("en", GeneralRouteEn)]
    [InlineData("he", GeneralRouteHe)]
    public void General_ComposesTheOwnKnowledgeBlock_Literally(string language, string expected)
    {
        Assert.Equal(expected, ProductChatPrompt.SystemMessage(language, ChatRoute.General, bookAware: false));
        Assert.Equal(expected, ProductChatPrompt.SystemMessage(language, ChatRoute.General, bookAware: true));
    }

    /// <summary>
    /// THE GENERAL ROUTE ASKS FOR NO CITATION LINE, stated as its own fact rather than left implicit in
    /// the literal above. It is the one property <c>ProductChatService</c> pairs with an empty acceptable
    /// reference set, and a citation sentence creeping back in would make that pairing contradictory
    /// instead of redundant.
    /// </summary>
    [Theory]
    [InlineData("en", "End your reply with a line", "Sources:")]
    [InlineData("he", "סיים את התשובה בשורה", "מקורות:")]
    public void General_AsksForNoCitationLine(string language, string guidesForm, string sourcesForm)
    {
        var message = ProductChatPrompt.SystemMessage(language, ChatRoute.General, bookAware: false);

        Assert.DoesNotContain(guidesForm, message, System.StringComparison.Ordinal);
        Assert.DoesNotContain(sourcesForm, message, System.StringComparison.Ordinal);

        // VACUITY GUARD: both forms ARE reachable on other routes, so their absence is this route's shape
        // and not a fragment that appears in no composed message at all.
        Assert.Contains(
            guidesForm, ProductChatPrompt.SystemMessage(language, ChatRoute.Product, bookAware: false),
            System.StringComparison.Ordinal);
        Assert.Contains(
            sourcesForm, ProductChatPrompt.SystemMessage(language, ChatRoute.Book, bookAware: true),
            System.StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOOK ROUTE, LITERALLY: a ONE-SENTENCE product rule where the five-sentence guides contract used
    /// to be, and the book rule with its briefs fence hedged. Everything else in phase B's rule is
    /// carried across character for character.
    /// </summary>
    [Theory]
    [InlineData("en", BookRouteEn)]
    [InlineData("he", BookRouteHe)]
    public void Book_ComposesTheHedgedBookRule_Literally(string language, string expected)
        => Assert.Equal(expected, ProductChatPrompt.SystemMessage(language, ChatRoute.Book, bookAware: true));

    /// <summary>
    /// WITH NO BOOK SECTION LEFT, THE BOOK ROUTE FALLS BACK TO THE PRODUCT MESSAGE AND NOT TO UNION'S.
    /// A book grounding rule with no BOOK section beneath it is a rule about nothing (g1's reason for
    /// deferring at all), but Union's book-LESS arm carries a refusal built for a turn with NO book, and
    /// this state is reached only on a turn that DID carry a bookId. Falling back there would tell an
    /// author with the book open to open the book, which is g1's own F-1 collision in a new costume.
    /// (Until g3 the sentence it would have told them was the FALSE "not available yet and is coming";
    /// the fallback is the same fallback either way, which is why this fact did not need re-deciding.)
    /// </summary>
    [Theory]
    [InlineData("en", "say that you can only see a book while it is open")]
    [InlineData("he", "ענה בגוף ראשון במשמעות הזו: 'אני יכול לראות ספר רק כשהוא פתוח.")]
    public void Book_WithNothingSurviving_FallsBackToProduct_AndNotToTheFalseRefusal(
        string language, string refusalFragment)
    {
        var fallback = ProductChatPrompt.SystemMessage(language, ChatRoute.Book, bookAware: false);

        Assert.Equal(ProductChatPrompt.SystemMessage(language, ChatRoute.Product, bookAware: false), fallback);
        Assert.DoesNotContain(refusalFragment, fallback, System.StringComparison.Ordinal);

        // VACUITY GUARD: the fragment is still reachable - on Union, which is where it is deliberately
        // kept - so its absence above is the fallback's shape and not a dead string.
        Assert.Contains(
            refusalFragment, ProductChatPrompt.SystemMessage(language, ChatRoute.Union, bookAware: false),
            System.StringComparison.Ordinal);
    }

    /// <summary>
    /// THE FALSE SENTENCE IS GONE FROM EVERY ROUTE, IN EVERY LANGUAGE, IN EITHER BOOK STATE (g3, item 6).
    /// "Answering questions about a specific book is not available yet and is coming" stopped being true
    /// when phase B taught Show to read the book, and g3 measured Union shipping it to a real user on 5 of
    /// 102 turns, two of them plain product questions that had merely missed the product lexicon. It was
    /// DELETED rather than gated behind a flag, so that there are not two versions of the truth in the
    /// file, and this is the fact that says so.
    ///
    /// <para>THERE IS NO VACUITY GUARD ON THIS ONE AND THERE MUST NOT BE. Every other absence in this file
    /// is paired with a reachability check, because an absent fragment that appears nowhere proves
    /// nothing. Here "appears nowhere" IS the property: the whole point is that no composed message and no
    /// code path can say it any more, so a guard proving it is still reachable would be proving the
    /// defect.</para>
    /// </summary>
    [Theory]
    [InlineData("en", "not available yet")]
    [InlineData("en", "is coming")]
    [InlineData("he", "עדיין אינו זמין")]
    [InlineData("he", "והיכולת בדרך")]
    public void NoRouteAtAll_ClaimsTheBookFeatureIsComing(string language, string fragment)
    {
        foreach (var route in System.Enum.GetValues<ChatRoute>())
        {
            foreach (var bookAware in new[] { false, true })
            {
                Assert.DoesNotContain(
                    fragment, ProductChatPrompt.SystemMessage(language, route, bookAware),
                    System.StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// THE PRODUCT ROUTE'S GROUNDING NAMES NO SOURCE, and this is the g3 fix that decided the gate. g2's
    /// block deleted the two narrating sentences and then said "guide" or "they" FIVE times over five
    /// sentences while one clause forbade describing where you looked; g3 measured the answers narrating
    /// 16 of 16 on the product-uncovered cell in both languages. A prohibition stacked on a frame that
    /// keeps teaching the noun is the shape this prompt has recorded failing four times, so the noun is
    /// gone from the instruction rather than banned in it: the grounding is "the material below", and
    /// then "there".
    ///
    /// <para>VACUITY GUARD: Union's grounding block DOES name the guides, repeatedly, so the absence here
    /// is this block's shape and not a word that appears in no composed message.</para>
    /// </summary>
    [Theory]
    [InlineData("en", "guide", "Answer ONLY from the guide content provided below")]
    [InlineData("he", "מדריכים", "ענה אך ורק מתוך תוכן המדריכים")]
    public void TheProductRoutesGrounding_NamesNoSource(
        string language, string sourceNoun, string unionSourceSentence)
    {
        var grounding = language == "he"
            ? ProductChatPromptBlocks.ProductGroundingScopedHe
            : ProductChatPromptBlocks.ProductGroundingScopedEn;

        Assert.DoesNotContain(sourceNoun, grounding, System.StringComparison.OrdinalIgnoreCase);

        var union = language == "he"
            ? ProductChatPromptBlocks.ProductGroundingHe
            : ProductChatPromptBlocks.ProductGroundingEn;
        Assert.Contains(unionSourceSentence, union, System.StringComparison.Ordinal);

        // AND THE ANTI-FABRICATION HALF SURVIVED THE RE-FRAME. g3's 0-fabrication result on the
        // product-uncovered cell (16 of 16 refused, none inventing a behaviour) rests on these two, and
        // "the narration is gone" is not worth buying with the class they protect.
        Assert.Contains(
            language == "he" ? "אל תציין הגדרה, כפתור, מסך או התנהגות שאינם כתובים שם" :
                               "do not state a setting, button, screen or behavior that is not written there",
            grounding, System.StringComparison.Ordinal);
        Assert.Contains(
            language == "he" ? "ולעולם אינה קביעה ש-PageDraft חסר את הדבר או אינו תומך בו" :
                               "never a claim that PageDraft lacks the thing or does not support it",
            grounding, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE GENERAL ROUTE POINTS AT NO GUIDES EITHER, because since g3 there are none under it.
    /// <c>ProductChatService.GeneralRouteGuideCount</c> is 0, so g2's "say something about PageDraft only
    /// where the guides below say it" would have pointed at an empty place; g3 measured 3 of 8 Hebrew
    /// craft turns inventing a PageDraft behaviour while those guides were still being sent.
    /// </summary>
    [Theory]
    [InlineData("en", "guides below")]
    [InlineData("he", "שהמדריכים שלמטה")]
    public void TheGeneralRoutesGrounding_PointsAtNoGuides(string language, string fragment)
    {
        var general = language == "he"
            ? ProductChatPromptBlocks.GeneralGroundingHe
            : ProductChatPromptBlocks.GeneralGroundingEn;

        Assert.DoesNotContain(fragment, general, System.StringComparison.Ordinal);
        Assert.Equal(0, ProductChatService.GeneralRouteGuideCount);
    }

    /// <summary>
    /// THE NARRATION IS GONE FROM EVERY ROUTE g2 COMPOSES, and present on Union. Stated as a property over
    /// the routes rather than left to the literals, because the literals answer "is this the string I
    /// wrote" and this answers "did the change do the thing it was for". The two mandates are the ones the
    /// plan quotes: the instruction to report that the guides do not address the question, and the
    /// instruction to frame a gap as a gap in the guides.
    /// </summary>
    [Theory]
    [InlineData("en", "If the guides do not address the question", "State it as a gap in the guides",
                "say that the briefs do not mention it")]
    [InlineData("he", "אם המדריכים אינם עונים על השאלה", "נסח זאת כפער במדריכים",
                "אמור שהתקצירים אינם מזכירים")]
    public void NoRouteButUnion_CarriesTheSourceNarration(
        string language, string guidesGapMandate, string gapFraming, string briefsMandate)
    {
        foreach (var route in new[] { ChatRoute.Product, ChatRoute.Book, ChatRoute.General })
        {
            foreach (var bookAware in new[] { false, true })
            {
                var message = ProductChatPrompt.SystemMessage(language, route, bookAware);

                Assert.DoesNotContain(guidesGapMandate, message, System.StringComparison.Ordinal);
                Assert.DoesNotContain(gapFraming, message, System.StringComparison.Ordinal);
                Assert.DoesNotContain(briefsMandate, message, System.StringComparison.Ordinal);
            }
        }

        // VACUITY GUARD: all three ARE still composed, by Union, which is the point of Union.
        var union = ProductChatPrompt.SystemMessage(language, ChatRoute.Union, bookAware: false);
        var unionBookAware = ProductChatPrompt.SystemMessage(language, ChatRoute.Union, bookAware: true);

        Assert.Contains(guidesGapMandate, union, System.StringComparison.Ordinal);
        Assert.Contains(gapFraming, union, System.StringComparison.Ordinal);
        Assert.Contains(briefsMandate, unionBookAware, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE FENCE THE HEDGE REPLACED IS STILL A FENCE. Deleting "say that the briefs do not mention it"
    /// removes the narration AND, if nothing takes its place, the only thing standing between a six-brief
    /// sample and "X does not happen in your book" - a false claim about a manuscript the author would act
    /// on. The Book route keeps the ban explicitly and adds the step that actually resolves the question.
    /// </summary>
    [Theory]
    [InlineData("en", "never say that it does not happen in the book", "offer to look at the chapter itself")]
    [InlineData("he", "לעולם אל תאמר שזה אינו קורה בספר", "והצע להסתכל בפרק עצמו")]
    public void Book_KeepsTheBanOnAssertingAbsence_AndOffersTheChapter(
        string language, string absenceBan, string offer)
    {
        var message = ProductChatPrompt.SystemMessage(language, ChatRoute.Book, bookAware: true);

        Assert.Contains(absenceBan, message, System.StringComparison.Ordinal);
        Assert.Contains(offer, message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// THE HEDGE SWAP MOVED EXACTLY ONE SENTENCE, which a hand-typed literal alone cannot show: the two
    /// book rules are the SAME head and the SAME tail with one block between them. Everything phase B
    /// measured - the reader clause, the whole/EXCERPT labels, the status clause, the note clause - is
    /// carried by construction rather than by a careful re-typing.
    /// </summary>
    [Fact]
    public void TheBookRules_DifferInExactlyTheFenceSentence()
    {
        Assert.Equal(
            ProductChatPromptBlocks.BookGroundingHeadEn
                + ProductChatPromptBlocks.BookBriefsFenceEn
                + ProductChatPromptBlocks.BookGroundingTailEn,
            ProductChatPromptBlocks.BookGroundingEn);
        Assert.Equal(
            ProductChatPromptBlocks.BookGroundingHeadEn
                + ProductChatPromptBlocks.BookBriefsHedgeEn
                + ProductChatPromptBlocks.BookGroundingTailEn,
            ProductChatPromptBlocks.BookGroundingRoutedEn);

        Assert.Equal(
            ProductChatPromptBlocks.BookGroundingHeadHe
                + ProductChatPromptBlocks.BookBriefsFenceHe
                + ProductChatPromptBlocks.BookGroundingTailHe,
            ProductChatPromptBlocks.BookGroundingHe);
        Assert.Equal(
            ProductChatPromptBlocks.BookGroundingHeadHe
                + ProductChatPromptBlocks.BookBriefsHedgeHe
                + ProductChatPromptBlocks.BookGroundingTailHe,
            ProductChatPromptBlocks.BookGroundingRoutedHe);

        // The two fences really are different text, so the four equalities above are not four ways of
        // saying one string equals itself.
        Assert.NotEqual(ProductChatPromptBlocks.BookBriefsFenceEn, ProductChatPromptBlocks.BookBriefsHedgeEn);
        Assert.NotEqual(ProductChatPromptBlocks.BookBriefsFenceHe, ProductChatPromptBlocks.BookBriefsHedgeHe);
    }

    /// <summary>
    /// NO EM-DASH ON ANY ROUTE. <c>ProductChatBookPromptTests</c> already sweeps the bool overload, which
    /// is Union alone, so g2's three new arms were outside every existing sweep. These strings FRAME the
    /// model's output and two live phase-A runs measured it echoing punctuation from its frame into
    /// user-facing text the workspace's no-em-dash rule covers.
    /// </summary>
    [Fact]
    public void NoRoute_CarriesAnEmDash()
    {
        foreach (var language in new[] { "en", "he" })
        {
            foreach (var route in System.Enum.GetValues<ChatRoute>())
            {
                foreach (var bookAware in new[] { false, true })
                {
                    Assert.DoesNotContain('—', ProductChatPrompt.SystemMessage(language, route, bookAware));
                }
            }
        }
    }

    // ─── THE BUDGET, AS A PROPERTY RATHER THAN AS A HOPE ────────────────────────────────────────

    /// <summary>
    /// NO ROUTE COSTS MORE THAN UNION DID, in either language or either book state. NumCtx stays pinned at
    /// 16384 and phase A's own measured Hebrew worst case leaves ~274 tokens of headroom, so g2 was
    /// required to pay for every added sentence with a deleted one. That is easy to intend and easy to
    /// lose, so it is asserted: each route is compared against the Union message for the SAME book state,
    /// which is the message that turn would have composed before routing was applied.
    ///
    /// <para>BOTH UNITS ARE CHECKED. Characters are what a reader can verify; tokens are what the context
    /// window actually spends, and the Hebrew rate is 1.8 chars/token against Latin's 3.5, so a change
    /// that trades English characters for Hebrew ones can shrink one measure while growing the other.
    /// Every size is also PRINTED, so the next edit here starts from a number.</para>
    /// </summary>
    [Fact]
    public void NoRoute_ComposesALongerMessageThanUnion_InEitherLanguage()
    {
        foreach (var language in new[] { "en", "he" })
        {
            foreach (var bookAware in new[] { false, true })
            {
                var union = ProductChatPrompt.SystemMessage(language, ChatRoute.Union, bookAware);
                var unionTokens = ProductChatBudget.EstimateTokens(union);

                _output.WriteLine(
                    $"{language} {(bookAware ? "book-aware" : "book-less ")} union  : " +
                    $"{union.Length,5} chars, {unionTokens,4} tokens");

                foreach (var route in new[] { ChatRoute.Product, ChatRoute.Book, ChatRoute.General })
                {
                    var message = ProductChatPrompt.SystemMessage(language, route, bookAware);
                    var tokens = ProductChatBudget.EstimateTokens(message);

                    _output.WriteLine(
                        $"{language} {(bookAware ? "book-aware" : "book-less ")} {route,-7}: " +
                        $"{message.Length,5} chars, {tokens,4} tokens " +
                        $"({message.Length - union.Length:+#;-#;0} chars, {tokens - unionTokens:+#;-#;0} tokens)");

                    Assert.True(tokens > 0, "vacuity guard: every composed message must be non-empty");
                    Assert.True(
                        message.Length <= union.Length,
                        $"the {language} {route} message is {message.Length} chars against Union's " +
                        $"{union.Length}; every route must be paid for out of Union's budget");
                    Assert.True(
                        tokens <= unionTokens,
                        $"the {language} {route} message is {tokens} estimated tokens against Union's " +
                        $"{unionTokens}; NumCtx is pinned at 16384 and there is no headroom to spend");
                }
            }
        }
    }

    /// <summary>
    /// The composed INSTRUCTION defaults to Union too, so every caller written before g1 - including
    /// <c>ProductChatBudget</c>'s own two call sites and every pin test - composes what it always did.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void ComposeInstruction_DefaultsToUnion(string language)
    {
        var guides = new[]
        {
            new GuideDocument(
                "export", "stage", "author", "2026-01-01", language, $"50-export.{language}.md", 50,
                new[] { "# Export" }, "Body."),
        };
        var history = System.Array.Empty<ProductChatTurn>();

        Assert.Equal(
            ProductChatPrompt.ComposeInstruction(language, guides, history, null, null, null, ChatRoute.Union),
            ProductChatPrompt.ComposeInstruction(language, guides, history));
    }

    // ─── The flag ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CLASS DEFAULT IS STILL OFF, AND g2 DELIBERATELY DID NOT MOVE IT. A test that constructs the
    /// options object without thinking about the flag must get the INERT posture, because that is the
    /// posture every byte-identity pin in this suite is written against; a test that wants the routed
    /// behaviour has to ask for it, so which prompt a test measures is visible at its call site.
    /// </summary>
    [Fact]
    public void RoutingIsOffByDefault_AtTheClassLevel()
        => Assert.False(new ProductChatOptions().RoutingEnabled);

    /// <summary>
    /// AND THE SHIPPED CONFIG TURNS IT ON (g2). The two halves of the previous test were one assertion
    /// about two different facts, and they now disagree on purpose, so they are asserted separately and
    /// against the REAL file rather than against a copy. This is also the rollback's own test: setting the
    /// key back to false is the documented way to put every turn on Union again with no code deploy, and
    /// a key that had quietly gone missing would leave the class default in charge and read exactly like
    /// a successful rollback.
    /// </summary>
    [Fact]
    public void TheShippedConfig_TurnsRoutingOn()
    {
        var config = ProviderTuningConfigParityTests.LoadShippedConfiguration();

        var raw = config[ProductChatOptions.SectionName + ":RoutingEnabled"];
        Assert.False(
            string.IsNullOrWhiteSpace(raw),
            "ProductChat:RoutingEnabled is absent from the shipped appsettings.json. It must be written "
            + "out: the class default is false, so an absent key silently reverts every turn to Union and "
            + "is indistinguishable from a deliberate rollback.");

        var options = new ProductChatOptions();
        config.GetSection(ProductChatOptions.SectionName).Bind(options);
        Assert.True(options.RoutingEnabled);
    }
}
