using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE FENCE AROUND g1's REFACTOR. g1 moved every authored prompt block out of
/// <c>ProductChatPrompt</c> into <c>ProductChatPromptBlocks</c>, re-partitioned the grounding head into
/// a persona block plus a product-grounding block, and generalized the <c>bookAware</c> bool into a
/// <see cref="ChatRoute"/>. All three are supposed to change NO CHARACTER of any composed message, and
/// "supposed to" is not a property. This file makes it one.
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
    /// THE CENTRAL FACT OF g1. <see cref="ChatRoute.Union"/> composes exactly what shipped, so a
    /// misroute in g2 can only ever return the status quo. All four cells are pinned, because the
    /// bookAware predicate is what Union still branches on and half a pin is not a fence.
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

    // ─── The other three routes are inert in g1 ─────────────────────────────────────────────────

    /// <summary>
    /// g1 BUILT THE SEAM AND CHANGED NO WORDING, so every route composes one of the two messages that
    /// already shipped. This is what makes an accidental flag flip in g1 unable to send the model a
    /// sentence nobody has measured; g2 is where these two arms stop being placeholders.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void ProductAndGeneral_StillComposeTodaysBookLessMessage(string language)
    {
        var shipped = ProductChatPrompt.SystemMessage(language, ChatRoute.Union, bookAware: false);

        Assert.Equal(shipped, ProductChatPrompt.SystemMessage(language, ChatRoute.Product, bookAware: false));
        Assert.Equal(shipped, ProductChatPrompt.SystemMessage(language, ChatRoute.General, bookAware: false));

        // And they stay book-LESS even when a BOOK section survived: the route, not the block count,
        // decides for a non-Union route.
        Assert.Equal(shipped, ProductChatPrompt.SystemMessage(language, ChatRoute.Product, bookAware: true));
        Assert.Equal(shipped, ProductChatPrompt.SystemMessage(language, ChatRoute.General, bookAware: true));
    }

    /// <summary>
    /// The Book route composes today's book-aware message - and, when nothing survived the trim, today's
    /// book-LESS one. A book grounding rule with no BOOK section beneath it is a rule about nothing, and
    /// shipping one is the g1 F-1 collision in a new costume.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void Book_ComposesTodaysBookAwareMessage_AndDefersWhenNoBookSectionSurvived(string language)
    {
        Assert.Equal(
            ProductChatPrompt.SystemMessage(language, ChatRoute.Union, bookAware: true),
            ProductChatPrompt.SystemMessage(language, ChatRoute.Book, bookAware: true));

        Assert.Equal(
            ProductChatPrompt.SystemMessage(language, ChatRoute.Union, bookAware: false),
            ProductChatPrompt.SystemMessage(language, ChatRoute.Book, bookAware: false));
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
    /// THE DEFAULT IS OFF, at the class level as well as in the shipped config. A test that constructs
    /// the options object without thinking about the flag must get the inert posture, because that is the
    /// posture every pin test in this suite is written against.
    /// </summary>
    [Fact]
    public void RoutingIsOffByDefault()
        => Assert.False(new ProductChatOptions().RoutingEnabled);
}
