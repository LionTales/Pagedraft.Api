using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE CHAPTER-NUMBERING SEAM (be-c02, review finding #1, the phase-B P0).
///
/// <para>THE DEFECT THESE PIN. A deictic question on a chapter at order 0 produced an answer opening
/// "בפרק שנקרא ... (שהוא למעשה פרק 0)" while the citation chip rendered directly beneath it in the same
/// answer card read "הטקסט של פרק 1". Same chapter, two numbers, one card. The server is 0-based
/// everywhere by construction and the client's <c>chapterDisplayNumber</c> is 1-based everywhere by
/// construction, and NOTHING compared them: that missing comparison is why three GPU gates and two green
/// suites did not see it.</para>
///
/// <para>THE INVARIANT: no surface the author can read shows two different numbers for one chapter. The
/// half of it that is decidable without a model is what this file pins:
/// <list type="number">
///   <item>inside one composed prompt, one chapter has exactly ONE number AS DATA, and it is the wire's;</item>
///   <item>every chapter-scoped block carries the FINISHED author-facing name of its chapter, with the
///     author's number already computed into it;</item>
///   <item>the grounding clause points at that line and no longer teaches the offset as a worked case;</item>
///   <item>the wire ref for order 0 is <c>chapter-text:0</c>, and the author's number for it is 1.</item>
/// </list>
/// (4) is one half of a CROSS-STACK PAIR - see <see cref="TheWireRefAndTheAuthorsNumber_AgreeAcrossTheStack"/>.</para>
///
/// <para>WHAT CHANGED IN final-r02, AND WHY (2) AND (3) EXIST. <c>be-c02</c> put the 0-vs-1 offset into the
/// grounding clause as a RULE with one worked example. <c>g4</c> measured it: at order 0, the order the
/// worked example used, 4 pass / 3 fail; at every order above it, 0 pass / 9 fail. The model reproduces the
/// example and does not apply <c>+1</c> as an operation. The owner's decision was to fix the SEAM: the
/// author-facing name is pre-computed by <c>BookArtifactBlocks.AuthorFacingChapterName</c> and rendered on
/// every chapter-scoped block, and the clause narrows to point at it (which also removes the
/// <c>[CHAPTER 0]</c> exemplar <c>final-r03</c> believes taught the label leak).</para>
///
/// <para>WHAT THESE CANNOT PIN. The RENDERED LINE's shape is verifiable by reading and is pinned here;
/// whether the model COPIES it is a gate measurement and not a unit test. These assert only that it has
/// something to copy.</para>
///
/// <para>Pure: no model, no GPU, no network, no database.</para>
/// </summary>
public class ProductChatChapterNumberingTests
{
    // ─── The literal both halves of the cross-stack pin are written against ──────────────────────

    /// <summary>The chapter the AUTHOR calls chapter 1 is the chapter at wire order 0. This constant is
    /// duplicated verbatim in the client spec named by the cross-stack test below; it is the whole
    /// content of the contract, so it is stated rather than derived on either side.</summary>
    private const int WireOrderOfTheAuthorsFirstChapter = 0;

    private const int AuthorsNumberForWireOrderZero = 1;

    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    private static GuideDocument Guide()
        => new("export", "stage", "author", "2026-01-01", "en", "50-export.en.md", 50,
               new[] { "# Export" }, "Body of export.");

    private static BookArtifactBlock EscalatedWholeChapter(string language, int order, string title)
        => BookArtifactBlocks.ChapterText(
            language, order, title,
            new BookChatExcerpts.Excerpt("The harbour was dark.", IsWholeChapter: true, EstimatedTokens: 6),
            rank: 100 - order)!;

    // ─── The two rendered forms of the author-facing name, one per answer language (final-r05) ───
    //
    // WHY THESE ARE WRITTEN OUT RATHER THAN CALLED FOR. Deriving the expectation from
    // AuthorFacingChapterName would make every assertion below survive the renderer emitting the wrong
    // language, which is the whole defect - g5 measured Latin "chapter N" surviving inside Hebrew prose in
    // 7 of 45 runs BECAUSE the model copies this line. So the literals are stated here once, and the tests
    // compose them with the numbers rather than asking the code under test what it thinks it renders.

    private const string EnFrame = "the author calls this chapter: ";
    private const string HeFrame = "המחבר קורא לפרק הזה: ";

    /// <summary>The frame the answer's language must carry.</summary>
    private static string Frame(string language) => language == "he" ? HeFrame : EnFrame;

    /// <summary>The author's number as it is written INSIDE the line, in the answer's language.</summary>
    private static string AuthorsNumberWord(string language, int authorsNumber)
        => language == "he" ? $"פרק {authorsNumber}" : $"chapter {authorsNumber}";

    /// <summary>The whole expected line for a titled chapter.</summary>
    private static string ExpectedNameLine(string language, int authorsNumber, string title)
        => $"{Frame(language)}{title} ({AuthorsNumberWord(language, authorsNumber)})";

    /// <summary>
    /// Every chapter number the composed prompt PRINTS FOR A REAL CHAPTER, derived from the rendered text
    /// rather than hand-listed, so a renderer that grows a fourth way of naming a chapter is covered by
    /// construction.
    ///
    /// <para>SCOPED TO THE BOOK SECTION ON PURPOSE. The rule strings above it legitimately print chapter
    /// numbers that name no chapter: the citation clause's worked example ("Sources: chapter-text:7,
    /// status:review"). Counting those would make this assert on the prompt's prose instead of on its
    /// data, which is the opposite of what it is for.</para>
    ///
    /// <para>AND IT IS SCOPED TO THE THREE DATA RENDERERS, WHICH IS NOW A REAL DISTINCTION AND NOT A
    /// TECHNICALITY (final-r02). The BOOK section deliberately carries a SECOND number for a chapter: the
    /// author-facing name line, whose number is <c>order + 1</c> by construction. None of the three
    /// patterns below matches it, and that is correct rather than lucky - this derivation is the "one
    /// chapter, one number AS DATA" probe, and the author line is not data, it is the finished sentence
    /// fragment the model is meant to copy. It has its own assertions below
    /// (<see cref="EveryChapterScopedBlock_CarriesTheAuthorFacingName_WithTheNumberAlreadyComputed"/>),
    /// so it is covered, not skipped.</para>
    /// </summary>
    private static IReadOnlyList<int> ChapterNumbersPrintedInTheBookSection(string prompt)
    {
        var start = prompt.IndexOf(ProductChatPrompt.BookMarker, StringComparison.Ordinal);
        var section = start < 0 ? string.Empty : prompt[start..];

        var found = new List<int>();

        // The bracketed labels: [CHAPTER 0, whole chapter] / [CHAPTER 0 EXCERPT, ...].
        foreach (Match m in Regex.Matches(section, @"\[CHAPTER (\d+)[ ,]"))
            found.Add(int.Parse(m.Groups[1].Value));

        // The three chapter-keyed refs, in the block headers that license them.
        foreach (Match m in Regex.Matches(section, @"chapter-(?:brief|summary|text):(\d+)"))
            found.Add(int.Parse(m.Groups[1].Value));

        // The shared brief heading, which the whole-book review also parses.
        foreach (Match m in Regex.Matches(section, @"## Chapter (\d+):"))
            found.Add(int.Parse(m.Groups[1].Value));

        return found;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 1. One chapter, one number, inside the composed prompt ─────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A book-scoped prompt for an ESCALATED chapter at order 0 - the exact shape the live defect was
    /// observed on - prints the number 0 for that chapter and never the number 1.
    ///
    /// <para>This is the property the chosen seam guarantees, and it is the one a half-done option 2
    /// would break: rendering "[CHAPTER 1, whole chapter]" above "ref=chapter-text:0" would put two
    /// numbers for one chapter inside the model's own input. The numbers are DERIVED from the rendered
    /// prompt rather than hand-listed, so a fourth renderer is covered by construction.</para>
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void AnEscalatedChapterAtOrderZero_IsPrintedWithExactlyOneNumber_AndItIsTheWiresZero(string language)
    {
        var blocks = new[]
        {
            EscalatedWholeChapter(language, WireOrderOfTheAuthorsFirstChapter, "The Dark Harbour")
        };

        var prompt = ProductChatPrompt.ComposeInstruction(
            language, new[] { Guide() }, Array.Empty<ProductChatTurn>(), blocks, "Shadow of the Moon");

        var printed = ChapterNumbersPrintedInTheBookSection(prompt);

        // VACUITY GUARD: the derivation really found the chapter this composition carries. Without it a
        // regex that stopped matching would green this test by finding nothing at all.
        Assert.NotEmpty(printed);

        Assert.All(printed, n => Assert.Equal(WireOrderOfTheAuthorsFirstChapter, n));
        Assert.DoesNotContain(AuthorsNumberForWireOrderZero, printed);
    }

    /// <summary>
    /// The same property under PRESSURE, through the real budget loop rather than the composer: the
    /// numbering must hold on the string a provider is actually handed, since composing and delivering
    /// are two different questions and F-1 was the second one.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void ThroughTheRealBudgetLoop_TheEscalatedChaptersNumber_IsStillOnlyTheWires(string language)
    {
        var blocks = new[]
        {
            BookArtifactBlocks.Statuses(null, null, null),
            EscalatedWholeChapter(language, WireOrderOfTheAuthorsFirstChapter, "The Dark Harbour")
        };

        var composed = ProductChatBudget.Compose(
            language, new[] { Guide() }, Array.Empty<ProductChatTurn>(),
            question: "What happens in this chapter?", budgetTokens: 14080, bookBlocks: blocks,
            bookTitle: "Shadow of the Moon");

        var printed = ChapterNumbersPrintedInTheBookSection(composed.Instruction);

        Assert.NotEmpty(printed);
        Assert.All(printed, n => Assert.Equal(WireOrderOfTheAuthorsFirstChapter, n));

        // And the clause that POINTS AT the author-facing line rode all the way to what the provider is
        // handed, in the system slot AND restated at the head of the user message (Ollama truncates from
        // the START). This used to assert the offset sentence; final-r02 replaced the offset with the
        // rendered line, so what has to survive the budget loop is the pointer to it.
        var pointer = language == "he"
            ? "בבלוק של כל פרק יש שורה עם השם שהמחבר משתמש בו"
            : "Each chapter's block carries a line with the name the author has for it";
        Assert.Contains(pointer, composed.SystemMessage, StringComparison.Ordinal);
        Assert.Contains(pointer, composed.Instruction, StringComparison.Ordinal);

        // And the line it points at is actually in the composed BOOK section, for the chapter carried,
        // WRITTEN IN THE LANGUAGE THIS COMPOSITION WILL BE ANSWERED IN (final-r05). The expectation is a
        // LITERAL, not a call to AuthorFacingChapterName: deriving it would make this survive both a
        // broken conversion (the tautology final-r01 warned about) and a frame emitted in the wrong
        // language, which is the defect g5 measured at 7 of 45 on the Hebrew path.
        Assert.Contains(
            ExpectedNameLine(language, AuthorsNumberForWireOrderZero, "The Dark Harbour"),
            composed.Instruction, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 2. The grounding clause POINTS AT the rendered line, in both languages ─────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The book-aware grounding string tells the model that the numbers in the labels and refs are
    /// internal, and that the author's name for a chapter is on a line it can COPY. It does not ask for
    /// arithmetic, because <c>g4</c> measured the model failing that arithmetic 9 times out of 9 above
    /// order 0.
    ///
    /// <para>It is asserted on the clause that already owned the bracketed labels, because that is where
    /// it was widened into. A fifth free-standing rule is the move this prompt has recorded failing
    /// twice, so the test is written against the scoped clause and would go red if the sentence were
    /// lifted out into one.</para>
    /// </summary>
    [Fact]
    public void TheBookAwareGrounding_PointsAtTheAuthorFacingLine_InEnglish()
    {
        var message = ProductChatPrompt.SystemMessage("en", bookAware: true);

        Assert.Contains(
            "Those bracketed labels are for you and the author never sees them, so only your own " +
            "sentence carries that difference to them. The refs are internal too and the author never " +
            "sees them either, and so are their numbers. Each chapter's block carries a line with the " +
            "name the author has for it; name a chapter by copying that line.",
            message, StringComparison.Ordinal);

        // VACUITY GUARD: the BOOK-LESS message must NOT carry it. Phase A never retrieves a chapter, so a
        // chapter-naming rule there would be a rule about nothing, and its presence would mean the sentence
        // had drifted out of the book-aware middle into the shared head.
        Assert.DoesNotContain(
            "the name the author has for it", ProductChatPrompt.SystemMessage("en", bookAware: false),
            StringComparison.Ordinal);
    }

    /// <summary>The Hebrew twin. DRAFT Hebrew, like every other string in this file's subject: it needs a
    /// native speaker, and this test pins its PRESENCE, never its quality.</summary>
    [Fact]
    public void TheBookAwareGrounding_PointsAtTheAuthorFacingLine_InHebrew()
    {
        var message = ProductChatPrompt.SystemMessage("he", bookAware: true);

        Assert.Contains(
            "גם המזהים פנימיים והמחבר אינו רואה גם אותם, וגם לא את המספרים שבהם. בבלוק של כל פרק יש " +
            "שורה עם השם שהמחבר משתמש בו; ציין פרק בהעתקת השורה הזו.",
            message, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "בבלוק של כל פרק יש שורה", ProductChatPrompt.SystemMessage("he", bookAware: false),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ASSERTION final-r01 SAID WAS OWED AND DELIBERATELY DID NOT WRITE. It withheld it because it
    /// would have been written against a sentence that was about to change; the sentence has now changed,
    /// so here it is.
    ///
    /// <para>WHAT IT PINS: the grounding clause no longer teaches the 0-vs-1 offset AS A WORKED CASE.
    /// <c>g4</c>'s reading of its own data is that the clause taught exactly one instance
    /// (<c>[CHAPTER 0]</c> is the chapter they call chapter 1) and that the model reproduced that instance
    /// as a lookup instead of applying <c>+1</c> - order 0 was the ONLY order that ever answered correctly.
    /// So a worked case reappearing in this clause is not a wording preference, it is the defect returning,
    /// and the offset now lives in a renderer where it is applied rather than taught.</para>
    ///
    /// <para>IT IS ALSO final-r03's HALF OF THE SAME EDIT. The worked case reproduced a literal bracketed
    /// label in order to keep bracketed labels internal, and <c>g4</c> measured that exact token reaching
    /// the author's prose in 3 of 38 runs. No literal <c>[CHAPTER</c> may return to either grounding
    /// string, and asserting the ABSENCE of the token is not the same as adding a prohibition to the
    /// prompt - the prompt says nothing about it at all, which is the point.</para>
    ///
    /// <para>Written as three properties over BOTH grounding strings rather than as an exact-text pin,
    /// because an exact-text pin is what the two tests above already are; this one has to stay true across
    /// the next rewording as well.</para>
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void TheBookAwareGrounding_NoLongerTeachesTheOffsetAsAWorkedCase(string language)
    {
        var message = ProductChatPrompt.SystemMessage(language, bookAware: true);

        // VACUITY GUARD: this really is the book-aware clause and not an empty string, so the three
        // absences below are absences FROM something.
        var anchor = language == "he" ? "התוויות בסוגריים" : "Those bracketed labels are for you";
        Assert.Contains(anchor, message, StringComparison.Ordinal);

        // (1) NO LITERAL BRACKETED LABEL. The exemplar the model was measured copying into prose.
        Assert.DoesNotContain("[CHAPTER", message, StringComparison.OrdinalIgnoreCase);

        // (2) NO WORKED CASE. A worked case needs a concrete pair of numbers, and the only concrete pair
        //     this clause could state is the offset one. The citation clause's own example
        //     ("chapter-text:7") is a REF and survives this, which is why the pattern is anchored on the
        //     word 'chapter' followed by a bare number rather than on digits generally.
        var workedCase = language == "he" ? @"פרק\s+\d" : @"\bchapter\s+\d";
        Assert.False(
            Regex.IsMatch(message, workedCase, RegexOptions.IgnoreCase),
            $"the {language} book-aware grounding names a specific chapter number: " +
            $"'{Regex.Match(message, workedCase, RegexOptions.IgnoreCase).Value}'. g4 measured the model " +
            "reproducing the clause's single worked example (order 0) and failing every order above it " +
            "(0 of 9); the author's number is pre-computed by AuthorFacingChapterName and must not be " +
            "taught as an operation or as an instance here.");

        // (3) AND NO ARITHMETIC ASKED FOR. The two counting bases stated together IS the operation, with
        //     or without an example attached to it.
        var countsFrom = language == "he" ? "סופרים פרקים מ-0" : "counts chapters from 0";
        Assert.DoesNotContain(countsFrom, message, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 3. EVERY chapter-scoped block carries the finished author-facing name (final-r02) ──────
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //
    // WHICH OF THE TWO PROPERTIES IS PARAMETERISED OVER ORDERS, AND WHY. final-r01 section 2(b) ruled that
    // parameterising the PROMPT property over more orders would be false comfort: "one chapter has exactly
    // ONE number as data, and it is the wire's" is ORDER-INDEPENDENT, so orders 1, 7 and 12 render
    // [CHAPTER 12] beside chapter-text:12 and pass identically while holding no information about the
    // region where the model never gets it right. That ruling stands and sections 1 and 2 are still pinned
    // at one order.
    //
    // THE RENDERING PROPERTY IS THE OPPOSITE SHAPE, AND THAT IS THE WHOLE REASON IT EXISTS. The line for
    // order 12 must say "chapter 13", so an implementation that hard-coded the one worked example - which
    // is precisely what the MODEL was measured doing - would pass at order 0 and fail at order 12. The
    // orders below are chosen to be the ones g4 actually failed on (3, 5, 7, 12) plus 0 (the only order
    // that ever passed) and 27 (the real-corpus shape whose title is itself a number).

    /// <summary>
    /// The one place the server adds the one, checked against the one place the CLIENT adds it. See
    /// <see cref="TheWireRefAndTheAuthorsNumber_AgreeAcrossTheStack"/> for why this is a pair.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    [InlineData(5, 6)]
    [InlineData(7, 8)]
    [InlineData(12, 13)]
    [InlineData(27, 28)]
    public void TheAuthorsNumber_IsTheWireOrderPlusOne(int order, int authorsNumber)
        => Assert.Equal(authorsNumber, BookArtifactBlocks.AuthorsChapterNumber(order));

    /// <summary>
    /// EVERY chapter-scoped block producer renders the author-facing name, and the number in it is already
    /// computed. These three are the blocks a chapter question can be answered FROM - the escalated raw
    /// text, the structured brief, and the author's own summary standing alone - enumerated from the three
    /// chapter-keyed ref vocabularies (<c>chapter-text</c>, <c>chapter-brief</c>, <c>chapter-summary</c>)
    /// rather than from a hand-kept list, so a fourth chapter-scoped block cannot quietly ship without one.
    ///
    /// <para>It asserts the WHOLE rendered line, not just the number: the model's cheapest correct action
    /// has to be copying a finished string, so a block that printed the right number in a shape the model
    /// would have to reassemble is not the fix.</para>
    ///
    /// <para>AND IT IS PARAMETERISED OVER THE ANSWER'S LANGUAGE AS WELL AS THE ORDER (final-r05). The
    /// order-dependence is final-r02's property and it is kept; the language-dependence is this todo's,
    /// and it has the same shape - an implementation that hard-codes the English frame passes every
    /// English row and fails every Hebrew one, which is exactly the state <c>g5</c> measured in the
    /// model's output (Latin "chapter N" inside Hebrew prose, 7 of 45 Hebrew book-scoped runs).</para>
    /// </summary>
    [Theory]
    [InlineData("en", 0, 1)]
    [InlineData("en", 3, 4)]
    [InlineData("en", 5, 6)]
    [InlineData("en", 7, 8)]
    [InlineData("en", 12, 13)]
    [InlineData("he", 0, 1)]
    [InlineData("he", 3, 4)]
    [InlineData("he", 5, 6)]
    [InlineData("he", 7, 8)]
    [InlineData("he", 12, 13)]
    public void EveryChapterScopedBlock_CarriesTheAuthorFacingName_WithTheNumberAlreadyComputed(
        string language, int order, int authorsNumber)
    {
        const string title = "The Dark Harbour";
        var expected = ExpectedNameLine(language, authorsNumber, title);

        var blocks = ChapterScopedBlocks(language, order, title);

        // VACUITY GUARD: all three producers really returned a block. Two of them are nullable and would
        // otherwise green this by contributing nothing.
        Assert.Equal(3, blocks.Count);

        foreach (var (name, block) in blocks)
        {
            Assert.True(
                block.Text.Contains(expected, StringComparison.Ordinal),
                $"the {name} block composed for a {language} answer at wire order {order} does not carry " +
                $"the author-facing name line '{expected}'; the author calls this chapter {authorsNumber}, " +
                $"g4 measured the model unable to derive that from the wire order itself (0 of 9 correct " +
                $"above order 0), and g5 measured it COPYING the line - so the line has to already be in " +
                $"the language the answer is written in. Block text was:\n{block.Text}");

            // And the line stands ALONE, so copying it cannot drag the chapter's body along.
            Assert.Contains("\n" + expected + "\n", "\n" + block.Text + "\n", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE ASSERTION THIS DEFECT IS ABOUT, AND IT IS CHECKABLE BY READING (final-r05). A Hebrew-composed
    /// chapter-scoped block carries NO Latin-script <c>chapter</c> frame on its author-facing name line.
    ///
    /// <para><c>g5</c> measured the model copying that line into its answer (19 of 25), which is what the
    /// fix was for - and therefore measured the ENGLISH frame arriving inside Hebrew prose in 7 of 45
    /// Hebrew book-scoped runs, six of them carrying the CORRECT number. The frame was the failure, not
    /// the arithmetic. An LTR fragment inside RTL prose also drags its punctuation to the wrong end.</para>
    ///
    /// <para>SCOPED TO THE NAME LINE, NOT TO THE BLOCK. The block legitimately keeps English elsewhere and
    /// must: the whole-vs-excerpt label <c>[CHAPTER 7, whole chapter]</c> and the ref
    /// <c>chapter-text:7</c> are a measured safety property and a wire key, and the grounding clause
    /// quotes the label verbatim in BOTH languages. Asserting "no English anywhere in the block" would
    /// therefore demand a change this todo explicitly forbids.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 8)]
    [InlineData(38, 39)]
    public void NoHebrewComposedBlock_CarriesTheLatinChapterFrame_OnItsAuthorFacingNameLine(
        int order, int authorsNumber)
    {
        const string title = "השביל הנסתר";
        var blocks = ChapterScopedBlocks("he", order, title);

        Assert.Equal(3, blocks.Count);

        foreach (var (name, block) in blocks)
        {
            var nameLine = block.Text
                .Split('\n')
                .SingleOrDefault(l => l.Contains(HeFrame, StringComparison.Ordinal));

            // VACUITY GUARD: there really is a Hebrew name line to inspect, so the absences below are
            // absences FROM something rather than from a line that stopped being rendered.
            Assert.True(nameLine != null,
                $"the {name} block composed for a Hebrew answer carries no author-facing name line at all " +
                $"(looked for '{HeFrame}'). Block text was:\n{block.Text}");

            Assert.False(
                nameLine!.Contains("chapter", StringComparison.OrdinalIgnoreCase),
                $"the {name} block's author-facing name line, composed for a HEBREW answer, still carries " +
                $"the Latin-script 'chapter' frame: '{nameLine}'. g5 measured the model copying this line " +
                $"verbatim, so an English frame here is an English fragment inside the author's Hebrew " +
                $"prose (7 of 45 runs), dragging its punctuation to the wrong end of an RTL sentence.");

            // And what it says instead is the author's number, in Hebrew, on that same line.
            Assert.Contains(
                AuthorsNumberWord("he", authorsNumber), nameLine, StringComparison.Ordinal);
        }
    }

    /// <summary>The three chapter-scoped producers, one order, one answer language, named so a failure
    /// says which one broke.</summary>
    private static IReadOnlyList<(string Name, BookArtifactBlock Block)> ChapterScopedBlocks(
        string language, int order, string? title)
        => new (string, BookArtifactBlock)[]
        {
            ("chapter-text", BookArtifactBlocks.ChapterText(
                language, order, title,
                new BookChatExcerpts.Excerpt("The harbour was dark.", IsWholeChapter: true, EstimatedTokens: 6),
                rank: 1)!),
            ("chapter-brief", BookArtifactBlocks.ChapterBrief(
                language,
                new ChapterBrief { Order = order, Title = title ?? string.Empty, Summary = "They reach the harbour." },
                authorSummary: null, rank: 1)),
            ("chapter-summary", BookArtifactBlocks.AuthorSummary(
                language, order, title, "What I meant to do in this chapter.", rank: 1)!)
        };

    /// <summary>
    /// THE <c>A16</c> CASE, ANSWERED RATHER THAN DEFERRED (final-r01 said a decision was owed on it before
    /// the title-only option could be written; the owner chose this shape instead, and it is why).
    ///
    /// <list type="bullet">
    ///   <item>An UNTITLED chapter renders the author's number alone, so there is always something safe to
    ///     say. Title-only naming had nothing here, which is what made it cost a decision.</item>
    ///   <item>A chapter whose own TITLE IS A NUMBER - <c>פרק 28</c> sitting at wire order 0, the commonest
    ///     real shape in this corpus - renders BOTH, and the two disagree on purpose: the author wrote
    ///     "chapter 28" on a chapter the product counts as their chapter 1, and hiding either half is a
    ///     worse answer than showing both.</item>
    /// </list>
    ///
    /// <para>BOTH CASES ARE PINNED IN BOTH LANGUAGES (final-r05). They are the two shapes where the frame
    /// carries the most weight: on an untitled chapter the frame plus the number is the ENTIRE line, so an
    /// English frame there leaves a Hebrew answer nothing at all to copy in its own language; and the
    /// numeric-title case is the commonest real shape in this corpus, where Hebrew renders
    /// <c>פרק 28 (פרק 1)</c> - the disagreement is the point, and it is now stated once in each
    /// language instead of half in each.</para>
    /// </summary>
    [Theory]
    [InlineData("en", null, 0, "the author calls this chapter: chapter 1")]
    [InlineData("en", "", 12, "the author calls this chapter: chapter 13")]
    [InlineData("en", "   ", 3, "the author calls this chapter: chapter 4")]
    [InlineData("en", "פרק 28", 0, "the author calls this chapter: פרק 28 (chapter 1)")]
    [InlineData("en", "Chapter 16", 6, "the author calls this chapter: Chapter 16 (chapter 7)")]
    [InlineData("he", null, 0, "המחבר קורא לפרק הזה: פרק 1")]
    [InlineData("he", "", 12, "המחבר קורא לפרק הזה: פרק 13")]
    [InlineData("he", "   ", 3, "המחבר קורא לפרק הזה: פרק 4")]
    [InlineData("he", "פרק 28", 0, "המחבר קורא לפרק הזה: פרק 28 (פרק 1)")]
    [InlineData("he", "Chapter 16", 6, "המחבר קורא לפרק הזה: Chapter 16 (פרק 7)")]
    public void TheAuthorFacingName_HandlesAnUntitledChapter_AndOneWhoseTitleIsItselfANumber(
        string language, string? title, int order, string expected)
        => Assert.Equal(expected, BookArtifactBlocks.AuthorFacingChapterName(language, order, title));

    /// <summary>And an untitled chapter still gets the line on all three blocks, not just from the helper,
    /// in either answer language.</summary>
    [Theory]
    [InlineData("en", "the author calls this chapter: chapter 13")]
    [InlineData("he", "המחבר קורא לפרק הזה: פרק 13")]
    public void AnUntitledChapter_StillCarriesTheAuthorFacingName_OnEveryChapterScopedBlock(
        string language, string expected)
    {
        var blocks = ChapterScopedBlocks(language, order: 12, title: null);

        Assert.Equal(3, blocks.Count);
        foreach (var (name, block) in blocks)
        {
            Assert.True(
                block.Text.Contains(expected, StringComparison.Ordinal),
                $"the {name} block for an UNTITLED chapter at wire order 12, composed for a {language} " +
                $"answer, does not name it as the author's chapter 13 as '{expected}'; an untitled chapter " +
                $"is exactly the case where the number is the only thing the author can be given, so the " +
                $"frame around it is the whole line. Block text was:\n{block.Text}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 4. The ambiguity note speaks the AUTHOR's numbering ────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The ONE line of the BOOK section the model is instructed to CARRY INTO ITS ANSWER states its
    /// numbers the way the author counts, not the way the wire does.
    ///
    /// <para>The author writing "chapter 5" grounds orders 4 and 5, which are the author's chapters 5 and
    /// 6. The note used to name the raw orders ("both chapter 4 and chapter 5 were retrieved"), which,
    /// beside a grounding clause that now says to give the author's number, would be two emphatic
    /// instructions that disagree - and the author who wrote "5" would be told their question might have
    /// meant "4", which is a number they never used.</para>
    /// </summary>
    [Fact]
    public void TheAmbiguityNote_NamesTheAuthorsTwoChapters_NotTheWiresTwoOrders()
    {
        var both = new[]
        {
            new BookArtifactBlock(BookArtifactKind.ChapterText, new[] { "chapter-text:4" }, "four", 1),
            new BookArtifactBlock(BookArtifactKind.ChapterText, new[] { "chapter-text:5" }, "five", 1)
        };

        var note = BookArtifactBlocks.ChapterNumberNote(new[] { 5 }, both);

        Assert.NotNull(note);
        Assert.Contains("the author's chapter 5 or their chapter 6", note!, StringComparison.Ordinal);

        // And it no longer hands the model the raw orders it is being asked not to speak.
        Assert.DoesNotContain("chapter 4", note, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 5. THE CROSS-STACK PIN. Half of a pair; the other half is a TypeScript spec ────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SERVER HALF OF A CROSS-STACK PAIR. The other half is
    /// <c>pagedraft-client/src/app/core/models/chat-artifact-ref.spec.ts</c>, the test named
    /// "cross-stack pin (server half: Pagedraft.Api.Tests/ProductChatChapterNumberingTests.cs)".
    ///
    /// <para>WHY IT IS A PAIR AND NOT ONE TEST. Neither repo can run the other's suite, so the agreement
    /// is pinned twice against the SAME literal: <c>chapter-text:0</c> is the wire ref for the chapter the
    /// author calls chapter 1. This half fails if the SERVER drifts (a ref or a label that stops being the
    /// raw order, or a grounding string that stops stating the translation); the client half fails if the
    /// CLIENT drifts (a <c>chapterDisplayNumber</c> that stops adding one, or a chip label that stops
    /// using it). Its absence is precisely what made the P0 invisible to 2,155 green tests and three GPU
    /// gates: two deliberate, opposite, individually-correct conventions with nothing comparing them.</para>
    ///
    /// <para>This mirrors the shape already used for the guide-title map
    /// (<c>ProductChatCorpusTests.EveryShippedGuidesFirstH1_IsWhatTheClientsCitationTitleMapMirrors</c>),
    /// which is this codebase's existing answer to a cross-repo contract.</para>
    /// </summary>
    [Fact]
    public void TheWireRefAndTheAuthorsNumber_AgreeAcrossTheStack()
    {
        const int order = WireOrderOfTheAuthorsFirstChapter;

        // (a) THE WIRE KEY IS THE RAW ORDER, for all three chapter-keyed vocabularies. The client parses
        //     these and matches them against chapter entities without arithmetic, so they may not move.
        Assert.Equal("chapter-text:0", BookArtifactRefs.ChapterText(order));
        Assert.Equal("chapter-brief:0", BookArtifactRefs.ChapterBrief(order));
        Assert.Equal("chapter-summary:0", BookArtifactRefs.ChapterSummary(order));

        // (b) THE MODEL-FACING LABEL CARRIES THE SAME NUMBER as the ref beside it. One chapter, one
        //     number, inside the prompt.
        //     THE WIRE HALF IS ASSERTED ON BOTH ANSWER LANGUAGES (final-r05): the ref and the label are the
        //     two things localization may NOT move, so the language that localized the name line beside
        //     them is exactly the language they have to be checked under.
        foreach (var composedFor in new[] { "en", "he" })
        {
            var wireBlock = EscalatedWholeChapter(composedFor, order, "The Dark Harbour");
            Assert.Contains("ref=chapter-text:0", wireBlock.Text, StringComparison.Ordinal);
            Assert.Contains("[CHAPTER 0, whole chapter]", wireBlock.Text, StringComparison.Ordinal);
        }

        // (c) AND THE BLOCK ITSELF SAYS WHAT THE AUTHOR'S NUMBER FOR IT IS, on a line the model can copy.
        //     Without this (a) and (b) are exactly the shipped defect: an honest wire the model reads out
        //     loud. THIS MOVED IN final-r02: it used to be an assertion on the grounding SENTENCE (which
        //     quoted "[CHAPTER 0]" and "chapter 1"), and g4 measured that sentence not surviving contact
        //     with any order above 0. What the author's number rides on now is the rendering - and since
        //     final-r05 that rendering is in the ANSWER's language, so the number the client's chip must
        //     agree with is pinned under BOTH, against the literal each one writes it as.
        var block = EscalatedWholeChapter("en", order, "The Dark Harbour");
        Assert.Contains(
            $"(chapter {AuthorsNumberForWireOrderZero})", block.Text, StringComparison.Ordinal);
        Assert.Contains(
            $"(פרק {AuthorsNumberForWireOrderZero})",
            EscalatedWholeChapter("he", order, "The Dark Harbour").Text, StringComparison.Ordinal);

        //     And the clause still points at that line, in both languages, so the model is told to use it.
        foreach (var language in new[] { "en", "he" })
        {
            var message = ProductChatPrompt.SystemMessage(language, bookAware: true);
            Assert.Contains(
                language == "he"
                    ? "בבלוק של כל פרק יש שורה עם השם שהמחבר משתמש בו"
                    : "Each chapter's block carries a line with the name the author has for it",
                message, StringComparison.Ordinal);
        }

        // (d) THE CLIENT'S SIDE OF THE SAME LITERAL is not asserted here, and saying so is the point: it
        //     CANNOT be, because this process cannot call `chapterDisplayNumber`. What the pair buys is
        //     that the number this half names ({AuthorsNumberForWireOrderZero}) is the number the client
        //     spec named above asserts its chip renders for `chapter-text:0`. An assertion here that
        //     0 + 1 == 1 would be a tautology dressed as a contract, so it is deliberately absent - and
        //     that is still true of (c): it asserts the RENDERED LINE carries the number, which is a fact
        //     about a renderer, not about addition.
    }
}
