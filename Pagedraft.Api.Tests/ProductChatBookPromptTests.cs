using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The phase-B PROMPT: that phase A's is untouched when no book is in scope, that both scoped
/// instructions travel when one is, and that the artifacts arrive typed and delimited enough to cite.
///
/// <para>THE CENTRAL TEST IN THIS FILE IS THE BYTE-IDENTITY ONE, and it is written against a LITERAL
/// rather than against a helper. Phase A's gate verdict (g4: 0 fabricated product behaviors in 48
/// adjacent runs, 48 of 48 pivots intact; g5's voice pass on top of it) is a measurement of exactly
/// those sentences and of nothing else. Phase B splits the string in three to swap the middle, which
/// is a refactor that CANNOT be trusted to be lossless just because it looks lossless - so the
/// reassembly is compared with a copy of the shipped text that was taken before the split.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE.</para>
/// </summary>
public class ProductChatBookPromptTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ProductChatBookPromptTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    // ─── The shipped phase-A strings, copied verbatim before the phase-B split ───────────────────

    internal const string ShippedGroundingEn =
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
        "plot, what a specific chapter says, what a review found), say that answering questions about " +
        "a specific book is not available yet and is coming, and offer help with general product and " +
        "workflow questions instead. Do not attempt an answer from the guides in that case. " +
        "End your reply with a line of the form 'Guides: <id>, <id>' naming the guide ids you used, " +
        "and nothing else on that line. " +
        "Answer in English, because the question is in English, even where a guide you used is in " +
        "another language.";

    internal const string ShippedGroundingHe =
        "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת. " +
        "ענה אך ורק מתוך תוכן המדריכים שמופיע למטה. " +
        "אל תשתמש בידע חיצוני על PageDraft, ולעולם אל תציין הגדרה, כפתור, מסך או התנהגות שאינם כתובים " +
        "במדריכים שניתנו. " +
        "אם המדריכים אינם עונים על השאלה, אמור זאת במפורש. אם יש נושא אחר שהם כן מכסים ורלוונטי " +
        "לשאלה, ציין אותו לפי המזהה שלו; אם אין, די בסירוב בלבד. אל תרכיב ניחוש מתוך חומר שרק חלקית " +
        "רלוונטי. " +
        "נסח זאת כפער במדריכים ולא כעובדה על המוצר: אל תאמר ש-PageDraft אינו תומך בכך. ואל תתאר מה " +
        "המדריכים אומרים על נושא שאינם עוסקים בו, גם לא כדי לציין מה מוזכר בהם לגביו. " +
        "אם השאלה נוגעת לתוכן או למצב של הספר הספציפי של המשתמש (הדמויות שבו, העלילה, מה כתוב בפרק " +
        "מסוים, מה סקירה מצאה), ענה בגוף ראשון במשמעות הזו: 'מענה על שאלות לגבי ספר מסוים עדיין אינו " +
        "זמין, והיכולת בדרך. אשמח לעזור בשאלות כלליות על המוצר ועל תהליך העריכה.' אל תנסה לענות מתוך " +
        "המדריכים במקרה כזה. " +
        "סיים את התשובה בשורה בצורה 'מדריכים: <מזהה>, <מזהה>' שמציינת את מזהי המדריכים שהשתמשת בהם, " +
        "ובלי דבר נוסף באותה שורה. " +
        "השב בעברית, כי השאלה נשאלה בעברית, גם אם מדריך שהשתמשת בו כתוב בשפה אחרת.";

    /// <summary>final-r02's scoped instruction, the sentence that closed phase A's g3 HALT. It must
    /// survive VERBATIM in BOTH modes: it is the one clause whose exact wording a gate verdict rests
    /// on, and B's addition is a PARALLEL scope beside it rather than a rewrite of it.</summary>
    private const string FinalR02ScopedInstructionEn =
        "If the guides do not address the question, say so plainly. If another topic they DO cover is " +
        "genuinely relevant, name it and its guide id; if none is, a bare refusal is the whole " +
        "answer. Do not assemble a guess out of partially relevant material.";

    private const string FinalR02ScopedInstructionHe =
        "אם המדריכים אינם עונים על השאלה, אמור זאת במפורש. אם יש נושא אחר שהם כן מכסים ורלוונטי " +
        "לשאלה, ציין אותו לפי המזהה שלו; אם אין, די בסירוב בלבד. אל תרכיב ניחוש מתוך חומר שרק חלקית " +
        "רלוונטי.";

    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    private static GuideDocument Guide(string id = "export", string lang = "en")
        => new(id, "stage", "author", "2026-01-01", lang, $"50-{id}.{lang}.md", 50,
               new[] { "# Export" }, $"Body of {id}.");

    private static BookArtifactBlock Block(
        BookArtifactKind kind, string reference, string text = "text", double rank = 0)
        => new(kind, new[] { reference }, $"=== ARTIFACT ref={reference} ===\n{text}", rank);

    // ─── Absent bookId: phase A, byte for byte ──────────────────────────────────────────────────

    /// <summary>
    /// THE REGRESSION THAT MATTERS MOST. With no book in scope the system message is character-for-
    /// character what phase A shipped, in both languages. A's gate verdict is a measurement of these
    /// exact sentences, so B is only licensed to change the prompt in the situation A never measured.
    /// </summary>
    [Fact]
    public void WithNoBook_TheSystemMessage_IsPhaseAsByteForByte()
    {
        Assert.Equal(ShippedGroundingEn, ProductChatPrompt.SystemMessage("en"));
        Assert.Equal(ShippedGroundingHe, ProductChatPrompt.SystemMessage("he"));

        // And explicitly, not just by default-argument accident.
        Assert.Equal(ShippedGroundingEn, ProductChatPrompt.SystemMessage("en", bookAware: false));
        Assert.Equal(ShippedGroundingHe, ProductChatPrompt.SystemMessage("he", bookAware: false));
    }

    /// <summary>
    /// And the composed instruction with no book carries NO book section at all: no marker, no book
    /// line, nothing between the guides and the conversation. An empty list and a null must behave
    /// identically, because the service passes whichever the request produced.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void WithNoBook_TheComposedInstruction_CarriesNoBookSection(string language)
    {
        var guides = new[] { Guide() };
        var history = new[] { new ProductChatTurn(IsUser: true, "earlier") };

        var phaseAShaped = ProductChatPrompt.ComposeInstruction(language, guides, history);
        var withNullBook = ProductChatPrompt.ComposeInstruction(language, guides, history, null, null);
        var withEmptyBook = ProductChatPrompt.ComposeInstruction(
            language, guides, history, Array.Empty<BookArtifactBlock>(), "A Title");

        Assert.DoesNotContain(ProductChatPrompt.BookMarker, phaseAShaped);
        Assert.Equal(phaseAShaped, withNullBook);
        Assert.Equal(phaseAShaped, withEmptyBook);

        // VACUITY GUARD: the same call WITH a block does produce the marker, so the absence above is
        // the no-book path and not a marker constant that is never emitted at all.
        var withBook = ProductChatPrompt.ComposeInstruction(
            language, guides, history, new[] { Block(BookArtifactKind.Status, "status:review") }, "A Title");
        Assert.Contains(ProductChatPrompt.BookMarker, withBook);
    }

    // ─── With a book: BOTH scoped instructions ──────────────────────────────────────────────────

    /// <summary>
    /// The book-aware prompt carries BOTH scopes: phase A's product-grounding half survives verbatim
    /// (final-r02's scoped instruction included), and B's book-content instruction sits beside it in the
    /// same shape - scoping what may be asserted and from what, rather than stacking another
    /// prohibition on top.
    /// </summary>
    [Fact]
    public void WithABook_TheSystemMessage_CarriesBothScopedInstructions_InEnglish()
    {
        var message = ProductChatPrompt.SystemMessage("en", bookAware: true);

        // A's half, verbatim.
        Assert.Contains(FinalR02ScopedInstructionEn, message, StringComparison.Ordinal);
        Assert.Contains("Answer ONLY from the guide content provided below.", message, StringComparison.Ordinal);
        Assert.Contains(
            "And do not describe what the guides say about a topic they do not address",
            message, StringComparison.Ordinal);

        // B's half: the parallel scope, and the partial-coverage phrasing the plan names as the whole risk.
        Assert.Contains(
            "answer it from the BOOK section below and from nothing else; the rule above about the guides " +
            "governs questions about PageDraft itself.",
            message, StringComparison.Ordinal);
        Assert.Contains("say that the briefs do not mention it", message, StringComparison.Ordinal);
        Assert.Contains("whole chapter", message, StringComparison.Ordinal);
        Assert.Contains("EXCERPT", message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithABook_TheSystemMessage_CarriesBothScopedInstructions_InHebrew()
    {
        var message = ProductChatPrompt.SystemMessage("he", bookAware: true);

        Assert.Contains(FinalR02ScopedInstructionHe, message, StringComparison.Ordinal);
        Assert.Contains("ענה אך ורק מתוך תוכן המדריכים שמופיע למטה.", message, StringComparison.Ordinal);
        Assert.Contains("ענה עליה מתוך מקטע הספר שמופיע למטה ומשום מקור אחר", message, StringComparison.Ordinal);
        Assert.Contains("אמור שהתקצירים אינם מזכירים", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE PHASE-A BOOK REFUSAL IS GONE when a book IS in scope. Leaving it in would be the g3
    /// collision all over again: two emphatic rules that contradict each other, resolved by the model
    /// rather than by the author. Here it would resolve toward refusing to answer about a book the
    /// prompt is simultaneously carrying.
    /// </summary>
    [Theory]
    [InlineData("en", "is not available yet and is coming")]
    [InlineData("he", "מענה על שאלות לגבי ספר מסוים עדיין אינו")]
    public void WithABook_ThePhaseARefusal_IsNotAlsoPresent(string language, string refusalFragment)
    {
        Assert.DoesNotContain(refusalFragment, ProductChatPrompt.SystemMessage(language, bookAware: true),
            StringComparison.Ordinal);

        // VACUITY GUARD: that fragment IS in the no-book message, so its absence is the swap and not a
        // fragment that never appears anywhere.
        Assert.Contains(refusalFragment, ProductChatPrompt.SystemMessage(language, bookAware: false),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// WHAT THE SYSTEM MESSAGE COSTS, REPORTED SO THE NEXT EDIT STARTS FROM A NUMBER RATHER THAN FROM AN
    /// INSTINCT. Every sentence in these strings is paid for TWICE - the provider's system slot and the
    /// head of the user message, which phase A restates on purpose because Ollama truncates from the
    /// START - and the Hebrew rate is 1.8 chars/token against Latin's 3.5, so the same clause costs
    /// roughly double in Hebrew. f2's first draft of the B rule measured 40 tokens over what the
    /// 40-chapter worst case had left and went through a concision pass before landing.
    ///
    /// <para>It ASSERTS only a ceiling, deliberately loose: the exact size is a fact to read off the
    /// output, not a number to freeze, and a test that failed on every wording change would be deleted
    /// the first time someone was in a hurry. The ceiling is there to catch a doubling, which is the
    /// failure mode that would quietly eat the grounding on a long book.</para>
    /// </summary>
    [Fact]
    public void TheSystemMessageSize_IsReported_AndStaysUnderTheCeiling()
    {
        const int ceiling = 1400;   // ~10% of the 14,080-token input budget, paid twice

        foreach (var language in new[] { "en", "he" })
        {
            foreach (var bookAware in new[] { false, true })
            {
                var message = ProductChatPrompt.SystemMessage(language, bookAware);
                var tokens = ProductChatBudget.EstimateTokens(message);

                _output.WriteLine(
                    $"{language} {(bookAware ? "book-aware" : "book-less  ")}: {message.Length,5} chars, " +
                    $"{tokens,4} tokens (x2 in the composed payload = {tokens * 2})");

                Assert.True(tokens > 0, "vacuity guard: the message must be non-empty");
                Assert.True(tokens < ceiling,
                    $"the {language} {(bookAware ? "book-aware" : "book-less")} system message is " +
                    $"{tokens} tokens, over the {ceiling}-token ceiling; it is paid for twice");
            }
        }
    }

    /// <summary>
    /// No em-dash anywhere in either mode of either language. These strings frame the model's output,
    /// and two live phase-A runs measured the model echoing punctuation from its frame into user-facing
    /// text that the workspace's no-em-dash rule covers.
    /// </summary>
    [Fact]
    public void NoPromptString_CarriesAnEmDash()
    {
        foreach (var language in new[] { "en", "he" })
        {
            foreach (var bookAware in new[] { false, true })
            {
                Assert.DoesNotContain('—', ProductChatPrompt.SystemMessage(language, bookAware));
            }
        }
    }

    // ─── Artifact blocks arrive typed and delimited ─────────────────────────────────────────────

    /// <summary>
    /// Every carried block's citation ref appears in the prompt EXACTLY as the model is asked to write
    /// it back. "Cite the artifact you used" has to be a thing the model can do by copying a visible
    /// string, or it can only be done by guessing.
    /// </summary>
    [Fact]
    public void EveryCarriedBlock_ShowsItsCitationRefInThePrompt()
    {
        var blocks = new[]
        {
            Block(BookArtifactKind.Status, "status:review"),
            Block(BookArtifactKind.BookBrief, "book-brief"),
            Block(BookArtifactKind.ChapterText, "chapter-text:7"),
            Block(BookArtifactKind.ChapterBrief, "chapter-brief:3"),
            Block(BookArtifactKind.Register, "register"),
            Block(BookArtifactKind.Finding, "finding:" + Guid.Empty.ToString("D"))
        };

        var instruction = ProductChatPrompt.ComposeInstruction(
            "en", new[] { Guide() }, Array.Empty<ProductChatTurn>(), blocks, "Salt and Rope");

        Assert.Contains(
            ProductChatPrompt.BookTitleLabel + "Salt and Rope", instruction, StringComparison.Ordinal);
        foreach (var block in blocks)
        {
            Assert.Contains($"ref={block.References[0]}", instruction, StringComparison.Ordinal);
        }
    }

    // ─── Two titles in one section, each said to be whose it is (be-c03, review finding #7) ──────
    //
    // OBSERVED live, 1 of 1: an answer opened 'בפרק שנקרא "צל הירח"' - naming the open CHAPTER by the
    // BOOK's title. The book's title rendered as a bare "Book: <title>" at the head of the BOOK section
    // and the chapter's title was juxtaposed to its bracketed label with nothing calling it a title, so
    // two titles sat in one section with nothing marking which was which. The fix is a RENDERING one, and
    // these are what makes it checkable by reading: the finding's PROSE half is g4's to measure.

    private const string ABookTitle = "Shadow of the Moon";
    private const string AChapterTitle = "The Dark Harbour";

    /// <summary>A composition carrying BOTH titles, through the real block renderers: the book's title in
    /// the section head AND inside the book-brief block, the chapter's in the chapter-text heading AND in
    /// the shared brief heading. Four renderers, two titles.</summary>
    private static string InstructionWithBothTitles(string language = "en")
    {
        var blocks = new[]
        {
            BookArtifactBlocks.BookBrief(
                new BookBrief { Genre = "Fantasy", Synopsis = "A quest across the salt flats." },
                ABookTitle, maxTokens: 800)!,
            // The composition's OWN language is threaded into the block renderers, not hard-coded
            // (final-r05): the author-facing name line is written in the answer's language, so a helper
            // that composed the instruction in one language and its blocks in another would be measuring
            // a prompt this product never builds.
            BookArtifactBlocks.ChapterBrief(
                language,
                new ChapterBrief { Title = AChapterTitle, Order = 0, Summary = "They reach the harbour." },
                authorSummary: null, rank: 10),
            BookArtifactBlocks.ChapterText(
                language, order: 0, title: AChapterTitle,
                excerpt: new BookChatExcerpts.Excerpt("The harbour was dark.", IsWholeChapter: true, EstimatedTokens: 6),
                rank: 9)!
        };

        return ProductChatPrompt.ComposeInstruction(
            language, new[] { Guide() }, Array.Empty<ProductChatTurn>(), blocks, ABookTitle);
    }

    private static IReadOnlyList<string> BookSectionLines(string instruction)
    {
        var start = instruction.IndexOf(ProductChatPrompt.BookMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the composition under test carries no BOOK section at all");
        return instruction[start..].Split('\n');
    }

    /// <summary>
    /// EVERY line of the BOOK section that carries the book's title says it is the BOOK's title. Written
    /// as a property over the rendered lines rather than against the two call sites, so a third renderer
    /// that printed the title bare fails HERE instead of shipping: printing it bare is the whole of the
    /// defect, and it shipped from a call site nobody thought to look at.
    /// </summary>
    [Fact]
    public void InTheBookSection_EveryLineCarryingTheBooksTitle_SaysItIsTheBooksTitle()
    {
        var lines = BookSectionLines(InstructionWithBothTitles())
            .Where(l => l.Contains(ABookTitle, StringComparison.Ordinal))
            .ToList();

        // VACUITY GUARD: the title really is rendered, in BOTH of the places that render it (the section
        // head line and the book-brief block's own title line). Without this the property below would be
        // green over an empty set.
        Assert.Equal(2, lines.Count);

        Assert.All(lines, line =>
            Assert.True(
                line.Contains("Book title", StringComparison.Ordinal),
                $"the book's title is rendered on a line that does not say it is the book's title: '{line}'"));
    }

    /// <summary>
    /// And the head line goes further than a label: it CONTRASTS the two, which is the thing a label alone
    /// does not do. "Book title:" sitting above "[CHAPTER 0, whole chapter] title: ..." is still two
    /// titles in one section, and the model that named a chapter with the book's title had a labelled
    /// chapter heading available to it already.
    /// </summary>
    [Fact]
    public void TheBookTitleLine_SaysThatItIsNotAChaptersTitle()
    {
        // The head line is found BY POSITION - the line immediately under the marker - so a revert that
        // strips the contrast fails on the contrast rather than on a search that no longer matches.
        var lines = BookSectionLines(InstructionWithBothTitles());
        Assert.Equal(ProductChatPrompt.BookMarker, lines[0]);

        var head = lines[1];
        Assert.EndsWith(ABookTitle, head, StringComparison.Ordinal);
        Assert.Contains("not a chapter title", head, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other side of the same contrast: every line carrying the CHAPTER's title names the chapter it
    /// belongs to, and the escalated chapter's heading says the word "title" of it. Both renderers are
    /// covered.
    ///
    /// <para>THE NUMBER SUCH A LINE NAMES IS NOW ONE OF TWO, AND THE TEST SAYS WHICH (final-r02). The two
    /// DATA renderers keep the wire's 0 (be-c02's seam, unchanged); the two author-facing name lines carry
    /// the author's 1, because the whole of final-r02 is that the offset is applied by a renderer instead
    /// of taught to the model. Both satisfy this test's actual property - a title is never rendered on a
    /// line that does not say which chapter it belongs to - so the property is asserted against the two
    /// permitted forms rather than loosened to "contains a digit".</para>
    /// </summary>
    [Fact]
    public void InTheBookSection_EveryLineCarryingTheChaptersTitle_NamesTheChapterItBelongsTo()
    {
        var lines = BookSectionLines(InstructionWithBothTitles())
            .Where(l => l.Contains(AChapterTitle, StringComparison.Ordinal))
            .ToList();

        // VACUITY GUARD: the chapter-text heading, the shared "## Chapter 0:" brief heading, and the
        // author-facing name line each of those two blocks now carries.
        Assert.Equal(4, lines.Count);

        Assert.All(lines, line =>
            Assert.True(
                line.Contains("CHAPTER 0", StringComparison.Ordinal)
                || line.Contains("Chapter 0", StringComparison.Ordinal)
                || line.Contains("(chapter 1)", StringComparison.Ordinal),
                $"a chapter's title is rendered on a line that does not name its chapter: '{line}'"));

        // AND THE TWO FORMS ARE BOTH PRESENT, in the counts they are expected in - so this cannot go green
        // by every line having drifted to one convention, which is precisely the defect class.
        Assert.Equal(2, lines.Count(l => l.Contains("(chapter 1)", StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(l => !l.Contains("(chapter 1)", StringComparison.Ordinal)));

        // And the escalated chapter's heading says the word of it, so "name a chapter by its title" has
        // something findable to point at. Located by its label first, so a revert fails on the missing
        // word rather than on a search that stopped matching.
        var chapterTextHeading = lines.Single(l => l.Contains("[CHAPTER 0,", StringComparison.Ordinal));
        Assert.Contains("title: " + AChapterTitle, chapterTextHeading, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROMPT ORDER IS THE REVERSE OF DROP ORDER, which is what puts the never-droppable statuses
    /// FIRST. Ollama truncates from the START, so the artifact that must never be lost is placed where a
    /// runtime truncation reaches last among the book blocks.
    /// </summary>
    [Fact]
    public void TheStatusBlock_IsOrderedFirstAmongTheBookArtifacts()
    {
        var blocks = new[]
        {
            Block(BookArtifactKind.Finding, "finding:a"),
            Block(BookArtifactKind.ChapterBrief, "chapter-brief:3"),
            Block(BookArtifactKind.Status, "status:review"),
            Block(BookArtifactKind.BookBrief, "book-brief")
        };

        var ordered = ProductChatBudget.OrderForPrompt(blocks);

        Assert.Equal(
            new[] { BookArtifactKind.Status, BookArtifactKind.BookBrief, BookArtifactKind.ChapterBrief, BookArtifactKind.Finding },
            ordered.Select(b => b.Kind));
    }
}
