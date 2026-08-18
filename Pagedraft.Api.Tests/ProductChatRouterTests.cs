using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// <see cref="ProductChatRouter"/>, TABLE-DRIVEN IN BOTH LANGUAGES.
///
/// <para>THE TABLE IS THE TEST. One row per question shape, each carrying its own name, and the same
/// table is driven twice - once as Hebrew, once as English - because the router is contracted to be
/// language-INDEPENDENT and that contract is worth more as a pinned property than as a paragraph.</para>
///
/// <para>THE NAMED CASES ARE THE OWNER'S OWN FAILURES, read out of conversation
/// 6802c061-baef-4afe-acd5-622163454f32 rather than invented. Five turns, all Hebrew, all with a book
/// open, and every one of them answered by narrating which source had failed. Their routes are pinned
/// below with their transcript position, so g2 and g3 inherit regression cases instead of anecdotes -
/// including the one the router gets WRONG, which is turn 0. See
/// <see cref="OwnerTurn0_NamesOnlyCharacters_FallsToUnion_TheKnownBlindSpot"/>.</para>
///
/// <para>PURE. No model, no GPU, no network, no database.</para>
/// </summary>
public class ProductChatRouterTests
{
    /// <param name="Name">What this row is, for the failure message and for g3's question set.</param>
    /// <param name="Hebrew">The question as the owner would type it.</param>
    /// <param name="English">The same question shape in English. Not a translation exercise: it is the
    /// second half of the language-independence property, and it must hit the same lexicon FAMILY.</param>
    /// <param name="HasBookId">Whether a book is open.</param>
    /// <param name="Expected">The route.</param>
    public sealed record Row(
        string Name, string Hebrew, string English, bool HasBookId, ChatRoute Expected);

    public static readonly IReadOnlyList<Row> Table = new[]
    {
        // ─── The owner's real turns (conversation 6802c061), in transcript order ─────────────────

        new Row(
            "owner-t0-romance-between-two-characters",
            "אם אני רוצה לחזק את הקשר הרומנטי בין רובי לפלואו. איפה כדאי לחזק את היחסים ואיך לדעתך?",
            "If I want to strengthen the romance between Ruby and Flo, where should I strengthen it and how?",
            HasBookId: true,
            // THE KNOWN BLIND SPOT, pinned deliberately. See the dedicated fact below.
            Expected: ChatRoute.Union),

        new Row(
            "owner-t2-what-about-chapter-15",
            "מה לגבי chapter 15, כשפלואו מצילה אותו והם נופלים מחובקים לתוך מערה?",
            "What about chapter 15, where Flo saves him and they fall into a cave holding each other?",
            HasBookId: true,
            Expected: ChatRoute.Book),

        new Row(
            "owner-t4-use-the-chapter-text-not-the-guides",
            "כן, תשתמש בטקסט של פרק 15 ותראה אם יש תשובה טובה. לא במדריכים",
            "Yes, use the text of chapter 15 and see if there is a good answer. Not the guides",
            HasBookId: true,
            // Naming the guides in order to RULE THEM OUT must not route Product. That is why "guide" is
            // absent from the product lexicon; see ProductChatRouter.ProductSurfaceWords.
            Expected: ChatRoute.Book),

        new Row(
            "owner-t6-bare-followup-so-how-do-i-improve",
            "אז איך לשפר?",
            "So how do I improve it?",
            HasBookId: true,
            // Carries no signal of any family. Union, which is what the deictic-free bare follow-up
            // deserves: the router cannot tell it from a product question without the transcript.
            Expected: ChatRoute.Union),

        new Row(
            "owner-t8-give-me-a-sentence-from-chapter-15",
            "מתוך הטקסט בלבד של פרק 15, וכהצעת שיפור, תן לי משפט או שניים טובים לכתיבת הסצינה",
            "From the text of chapter 15 only, as an improvement, give me a good sentence or two for the scene",
            HasBookId: true,
            // Craft words AND a chapter word. The chapter word wins because the book is open and the
            // craft family alone never beats a location.
            Expected: ChatRoute.Book),

        // ─── Product ────────────────────────────────────────────────────────────────────────────

        new Row(
            "product-export-epub-no-book",
            "איך אפשר לייצא את הספר ל-EPUB?",
            "How do I export the book to EPUB?",
            HasBookId: false,
            Expected: ChatRoute.Product),

        new Row(
            "product-export-epub-with-a-book-open",
            "איך אפשר לייצא ל-EPUB?",
            "How do I export to EPUB?",
            HasBookId: true,
            // A book being open is a client fact, not a decision about the question. A product question
            // stays a product question.
            Expected: ChatRoute.Product),

        new Row(
            "product-where-is-the-settings-screen",
            "איפה מסך ההגדרות?",
            "Where is the settings screen?",
            HasBookId: false,
            Expected: ChatRoute.Product),

        // ─── Book ───────────────────────────────────────────────────────────────────────────────

        new Row(
            "book-what-happens-in-chapter-three",
            "מה קורה בפרק 3?",
            "What happens in chapter 3?",
            HasBookId: true,
            Expected: ChatRoute.Book),

        new Row(
            "book-deictic-this-chapter",
            "האם הפרק הזה מוכן?",
            "Is this chapter ready?",
            HasBookId: true,
            Expected: ChatRoute.Book),

        new Row(
            "book-review-dimension-pacing",
            "מה מצב הקצב בספר?",
            "How is the pacing in the book?",
            HasBookId: true,
            Expected: ChatRoute.Book),

        new Row(
            "book-shaped-question-with-no-book-open",
            "מה קורה בפרק 3?",
            "What happens in chapter 3?",
            HasBookId: false,
            // NOT Book: the Book route means "answer from the BOOK section", and without a bookId there
            // is no BOOK section. g2 owes this shape a deterministic "open the book first" and reads
            // ProductChatRouter.Analyze for it.
            Expected: ChatRoute.Union),

        // ─── General ────────────────────────────────────────────────────────────────────────────

        new Row(
            "general-how-do-i-write-better-dialogue",
            "איך כותבים דיאלוג טוב יותר?",
            "How do I write better dialogue?",
            HasBookId: false,
            Expected: ChatRoute.General),

        new Row(
            "general-what-is-a-metaphor-for",
            "למה משמשת מטאפורה בפרוזה?",
            "What is a metaphor for in prose?",
            HasBookId: false,
            Expected: ChatRoute.General),

        new Row(
            "general-craft-question-with-a-book-open-is-mixed",
            "איך כותבים דיאלוג טוב יותר?",
            "How do I write better dialogue?",
            HasBookId: true,
            // A craft question asked over an open manuscript is MIXED, and mixed is Union. Routing it
            // General would drop the briefs, the register and the findings; the router cannot tell a
            // question about writing from a question about THIS writing, because that difference is
            // carried by proper nouns it has no register to resolve. See owner turn 0.
            Expected: ChatRoute.Union),

        // ─── Mixed and empty ────────────────────────────────────────────────────────────────────

        new Row(
            "mixed-product-and-book-in-one-question",
            "איך אני מייצא את פרק 3 ל-EPUB?",
            "How do I export chapter 3 to EPUB?",
            HasBookId: true,
            Expected: ChatRoute.Union),

        new Row(
            "unmatched-question",
            "שלום, מה שלומך?",
            "Hello, how are you?",
            HasBookId: false,
            Expected: ChatRoute.Union),
    };

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in Table)
        {
            data.Add(row.Name, "he");
            data.Add(row.Name, "en");
        }

        return data;
    }

    private static Row Find(string name) => Table.Single(r => r.Name == name);

    /// <summary>THE TABLE, driven once per language. The question TEXT changes with the language; the
    /// expected route does not.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void TheTableRoutesAsWritten(string name, string language)
    {
        var row = Find(name);
        var question = language == "he" ? row.Hebrew : row.English;

        var actual = ProductChatRouter.Resolve(question, row.HasBookId, language);

        Assert.True(
            row.Expected == actual,
            $"[{row.Name}/{language}] expected {row.Expected} but got {actual} for: {question}");
    }

    /// <summary>
    /// LANGUAGE-INDEPENDENCE AS A PROPERTY, not as a paragraph. The same question resolves the same way
    /// whichever language it was DETECTED as, because detection is a heuristic and a mixed-script
    /// question must not route differently depending on which way it fell.
    /// </summary>
    [Fact]
    public void TheRouteDoesNotDependOnTheDetectedLanguage()
    {
        foreach (var row in Table)
        {
            foreach (var question in new[] { row.Hebrew, row.English })
            {
                Assert.Equal(
                    ProductChatRouter.Resolve(question, row.HasBookId, "he"),
                    ProductChatRouter.Resolve(question, row.HasBookId, "en"));
            }
        }
    }

    // ─── The owner's turn 0, and why it is Union ────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THE ROUTER CANNOT SEE, WRITTEN DOWN SO IT IS NOT REDISCOVERED. The owner's first turn is
    /// unambiguously about their manuscript: it names two characters out of it and asks where to
    /// strengthen their relationship. Show answered it by narrating that the guides do not cover romance,
    /// which is the whole defect 8d exists to remove.
    ///
    /// <para>The router still routes it <see cref="ChatRoute.Union"/>, and that is the correct outcome
    /// FOR THIS ROUTER: the only book signal in the sentence is two proper nouns, and resolving those
    /// needs the character register, which needs a database read that would make this class impure and
    /// put a query in front of every turn. Union is the status quo, so the turn is no worse than it is
    /// today; what it is not, is fixed. Anyone reading a green suite as "8d turn 0 is handled" is reading
    /// it wrong.</para>
    ///
    /// <para>WHAT WOULD CLOSE IT, for whoever picks it up: <c>BookArtifactSelector.ResolveCharacters</c>
    /// already matches question tokens against a <c>CharacterRegister</c>, and the retrieval has that
    /// register in hand by the time it selects blocks. A route resolved AFTER retrieval, from the keys
    /// the selector produced, would see the names. That is a different seam from this one and it belongs
    /// to whoever measures whether it is worth the coupling.</para>
    /// </summary>
    [Fact]
    public void OwnerTurn0_NamesOnlyCharacters_FallsToUnion_TheKnownBlindSpot()
    {
        var row = Find("owner-t0-romance-between-two-characters");

        var signals = ProductChatRouter.Analyze(row.Hebrew);
        Assert.False(signals.BookContent);      // no chapter, scene or dimension word: only two names
        Assert.False(signals.ProductSurface);

        Assert.Equal(ChatRoute.Union, ProductChatRouter.Resolve(row.Hebrew, hasBookId: true, "he"));
        Assert.Equal(ChatRoute.Union, ProductChatRouter.Resolve(row.English, hasBookId: true, "en"));
    }

    // ─── The lexicon is SHARED, not copied ──────────────────────────────────────────────────────

    /// <summary>
    /// THE BOOK LEXICON HAS ONE OWNER. It is derived from the vocabularies that already drive book
    /// retrieval, so a word added for retrieval reaches routing automatically. Two copies would drift
    /// invisibly: the router would route Book for a question the selector retrieves nothing for, or the
    /// reverse, and neither shows up as a failure.
    /// </summary>
    [Fact]
    public void TheBookContentLexicon_IsDerivedFromTheRetrievalVocabulary()
    {
        var lexicon = BookArtifactSelector.BookContentWords;

        // The three families it unions.
        Assert.Contains("chapter", lexicon);
        Assert.Contains("פרק", lexicon);
        Assert.Contains("scene", lexicon);
        Assert.Contains("סצנה", lexicon);
        Assert.Contains("pacing", lexicon);
        Assert.Contains("עלילה", lexicon);

        // And the two it deliberately does NOT: positional words read as a place inside anything, and
        // deictic markers only point at a book when one is open, so they are exposed separately.
        Assert.DoesNotContain("first", lexicon);
        Assert.DoesNotContain("סוף", lexicon);
        Assert.DoesNotContain("this", lexicon);
        Assert.Contains("this", BookArtifactSelector.BookDeicticWords);

        // No duplicates: it is a union of overlapping lists and the dedupe is load-bearing for anyone
        // reading it as a vocabulary.
        Assert.Equal(lexicon.Count, lexicon.Distinct().Count());
    }

    /// <summary>
    /// WHOLE-WORD, because the router borrows the selector's matcher rather than writing one. "פרק" must
    /// not match inside "פרקליט"; a router with its own substring scan would route a lawyer question to
    /// the manuscript. The English row is the SAME matcher's trailing-letter check, which is what keeps
    /// "chapter" from matching inside "chapters" - the plural is deliberately outside the location
    /// vocabulary (see <c>BookArtifactSelector.PluralLocationWords</c>), because "how many chapters are
    /// there" is a question about the whole book rather than about one chapter's content.
    /// </summary>
    [Theory]
    [InlineData("מי הפרקליט בסיפור?")]
    [InlineData("How many chapters are there?")]
    public void TheLexiconMatchesWholeWordsOnly(string question)
        => Assert.False(ProductChatRouter.Analyze(question).BookContent);

    // ─── The guide top score ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The strong-guide signal is a POSITIVE one only. A score at or above the threshold adds Product; a
    /// score below it removes nothing, because <c>GuideSelector</c> is contracted never to decide "no
    /// coverage" and a router reading a weak score as a refusal would take a decision that contract
    /// reserves for the model.
    /// </summary>
    [Fact]
    public void AStrongGuideScore_RoutesProduct_AndAWeakOneDecidesNothing()
    {
        const string neutral = "מה כדאי לעשות עכשיו?";

        Assert.Equal(
            ChatRoute.Union,
            ProductChatRouter.Resolve(neutral, hasBookId: false, "he", guideTopScore: 0.0));

        Assert.Equal(
            ChatRoute.Product,
            ProductChatRouter.Resolve(
                neutral, hasBookId: false, "he",
                guideTopScore: ProductChatRouter.StrongGuideTopScore));

        // Just under the threshold decides nothing at all.
        Assert.Equal(
            ChatRoute.Union,
            ProductChatRouter.Resolve(
                neutral, hasBookId: false, "he",
                guideTopScore: ProductChatRouter.StrongGuideTopScore - 0.5));
    }

    /// <summary>
    /// And a strong guide score does NOT overrule a book signal: a question naming a chapter over an open
    /// book is mixed at worst, never Product. A guides-only answer to "what happens in chapter 3" is the
    /// exact failure the owner reported.
    /// </summary>
    [Fact]
    public void AStrongGuideScore_NeverOverrulesABookSignal()
        => Assert.Equal(
            ChatRoute.Union,
            ProductChatRouter.Resolve(
                "מה קורה בפרק 3?", hasBookId: true, "he",
                guideTopScore: ProductChatRouter.StrongGuideTopScore * 2));

    // ─── Degenerate input ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankQuestionsRouteUnion(string? question)
    {
        Assert.Equal(ChatRoute.Union, ProductChatRouter.Resolve(question, hasBookId: true, "he"));
        Assert.Equal(ChatRoute.Union, ProductChatRouter.Resolve(question, hasBookId: false, "en"));

        var signals = ProductChatRouter.Analyze(question);
        Assert.False(signals.BookContent);
        Assert.False(signals.BookDeictic);
        Assert.False(signals.ProductSurface);
        Assert.False(signals.WritingCraft);
        Assert.False(signals.StrongGuideMatch);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── g2: THE ONE SHAPE THAT IS ANSWERED IN CODE ─────────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A PLACE INSIDE A MANUSCRIPT, WITH NO MANUSCRIPT OPEN. Nothing that could answer this is in scope,
    /// so <c>ProductChatService</c> answers it from a fixed string and never calls the model. Both
    /// languages, and both a numbered chapter and a scene, because the location lexicon carries both
    /// families.
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 3?")]
    [InlineData("מה קורה בפרק 3?")]
    [InlineData("Which scene does he leave in?")]
    [InlineData("באיזו סצנה הוא עוזב?")]
    public void ALocationQuestionWithNoBookOpen_IsAnsweredInCode(string question)
        => Assert.True(ProductChatRouter.AsksAboutABookThatIsNotOpen(question, hasBookId: false));

    /// <summary>
    /// WITH A BOOK OPEN IT IS NOT, which is the whole condition: the path exists for the state where
    /// nothing can be read, and with a bookId there is something to read.
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 3?")]
    [InlineData("מה קורה בפרק 3?")]
    public void TheSameQuestionWithABookOpen_IsNotAnsweredInCode(string question)
        => Assert.False(ProductChatRouter.AsksAboutABookThatIsNotOpen(question, hasBookId: true));

    /// <summary>
    /// THE NARROWING THAT KEEPS A CRAFT QUESTION OUT, and it is the reason this predicate reads
    /// <c>BookLocationWords</c> rather than the full book lexicon. The six review DIMENSIONS are in
    /// <c>BookContentWords</c> because they drive retrieval, and they are craft words at the same time -
    /// "how do I improve the pacing?" is a book-content hit and a general question about writing at once.
    /// Answered in code it would meet a real question with an instruction to open a book.
    /// </summary>
    [Theory]
    [InlineData("How do I improve the pacing of a novel?")]
    [InlineData("איך משפרים את הקצב של רומן?")]
    [InlineData("How much plot does a short story need?")]
    [InlineData("כמה עלילה צריך סיפור קצר?")]
    public void ADimensionQuestionWithNoLocation_IsNotAnsweredInCode(string question)
        => Assert.False(ProductChatRouter.AsksAboutABookThatIsNotOpen(question, hasBookId: false));

    /// <summary>
    /// EVERY OTHER FAMILY VETOES, on the same conjunction discipline the routes use: a product word, a
    /// strong guide top score, or a craft word means the question is at least partly about something the
    /// model CAN answer, and mixed is never answered deterministically.
    /// </summary>
    [Fact]
    public void AnyOtherSignal_VetoesTheDeterministicAnswer()
    {
        // A product word beside the location.
        Assert.False(ProductChatRouter.AsksAboutABookThatIsNotOpen(
            "How do I export a single chapter?", hasBookId: false));
        Assert.False(ProductChatRouter.AsksAboutABookThatIsNotOpen(
            "איך מייצאים פרק בודד?", hasBookId: false));

        // A craft word beside it.
        Assert.False(ProductChatRouter.AsksAboutABookThatIsNotOpen(
            "How long should a chapter of a novel be?", hasBookId: false));

        // A strong guide top score alone, on a question the lexicons would otherwise have caught.
        Assert.False(ProductChatRouter.AsksAboutABookThatIsNotOpen(
            "What happens in chapter 3?", hasBookId: false,
            guideTopScore: ProductChatRouter.StrongGuideTopScore));

        // VACUITY GUARD: the same question just below the threshold IS caught, so the vetoes above are
        // the signals and not a predicate that never fires.
        Assert.True(ProductChatRouter.AsksAboutABookThatIsNotOpen(
            "What happens in chapter 3?", hasBookId: false,
            guideTopScore: ProductChatRouter.StrongGuideTopScore - 0.1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankQuestion_IsNeverAnsweredInCode(string? question)
        => Assert.False(ProductChatRouter.AsksAboutABookThatIsNotOpen(question, hasBookId: false));

    /// <summary>
    /// AND IT IS LANGUAGE-INDEPENDENT LIKE THE ROUTER, swept over the whole table rather than over the
    /// handful of rows above: whatever the predicate answers for a row's Hebrew, it answers for its
    /// English. The lexicons are bilingual by construction and the predicate reads the raw question, so
    /// this is a property and not a coincidence of the rows chosen.
    /// </summary>
    [Fact]
    public void TheDeterministicShape_IsReadTheSameInBothLanguages()
    {
        foreach (var row in Table)
        {
            Assert.Equal(
                ProductChatRouter.AsksAboutABookThatIsNotOpen(row.Hebrew, hasBookId: false),
                ProductChatRouter.AsksAboutABookThatIsNotOpen(row.English, hasBookId: false));
        }
    }

    /// <summary>Same inputs, same route, every time. The property that makes the table above worth
    /// anything.</summary>
    [Fact]
    public void ResolveIsDeterministic()
    {
        foreach (var row in Table)
        {
            var first = ProductChatRouter.Resolve(row.Hebrew, row.HasBookId, "he", 4.0);
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(first, ProductChatRouter.Resolve(row.Hebrew, row.HasBookId, "he", 4.0));
            }
        }
    }
}
