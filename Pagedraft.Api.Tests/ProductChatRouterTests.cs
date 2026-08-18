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

        // ─── g3: THE PRODUCT HOW-TO, WHICH USED TO BE ANSWERED "OPEN A BOOK FIRST" ──────────────

        new Row(
            "product-howto-add-a-chapter-no-book",
            "איך מוסיפים פרק חדש?",
            "How do I add a chapter?",
            HasBookId: false,
            // g1 named this residual; g3 measured it failing 4 of 6. A structure verb applied to a
            // LOCATION means the app's chapter list, not a chapter's contents, so the location the verb
            // named is withdrawn and what is left is a product question.
            Expected: ChatRoute.Product),

        new Row(
            "product-howto-delete-a-chapter-with-a-book-open",
            "איך מוחקים פרק?",
            "How do I delete a chapter?",
            HasBookId: true,
            // A book being open does not turn a product how-to into a question about the manuscript, for
            // the same reason product-export-epub-with-a-book-open stays Product.
            Expected: ChatRoute.Product),

        new Row(
            "structure-verb-with-no-location-is-not-a-how-to",
            "איך יוצרים דמות משכנעת?",
            "How do I create a convincing character?",
            HasBookId: false,
            // "create"/"יוצרים" alone is NOT a product signal. Without the conjunction this row would
            // route Product and the answer would be a refusal, which is the g3 defect this whole round
            // exists to remove. (Hebrew "דמות" is a review DIMENSION surface, so the Hebrew half also
            // carries a book-content hit with no book open, which is Union; the English half carries
            // "character" for the same reason. Either way it is not Product, which is the point.)
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

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── g3: THE PRODUCT HOW-TO VETO, AND THE CALIBRATED THRESHOLD ──────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A PRODUCT HOW-TO IS NEVER MET WITH "OPEN A BOOK FIRST". g3's residual cell asked six of these and
    /// four were answered "open the book you are asking about" - a non-answer, because the question is
    /// not about any particular manuscript. Both languages, and all four measured shapes.
    /// </summary>
    [Theory]
    [InlineData("How do I add a chapter?")]
    [InlineData("How do I delete a chapter?")]
    [InlineData("איך מוסיפים פרק חדש?")]
    [InlineData("איך מוחקים פרק?")]
    public void AProductHowTo_IsNeverAnsweredInCode(string question)
    {
        Assert.True(ProductChatRouter.Analyze(question).ProductHowTo);
        Assert.False(ProductChatRouter.AsksAboutABookThatIsNotOpen(question, hasBookId: false));
    }

    /// <summary>
    /// THE CONJUNCTION IS BOTH HALVES, AND EACH HALF ALONE MUST NOT FIRE. A structure verb with no
    /// location is an ordinary craft question ("how do I create a convincing villain?"), and a location
    /// with no structure verb is the very shape the deterministic path exists for. Getting either wrong
    /// trades one non-answer for another.
    /// </summary>
    [Theory]
    [InlineData("How do I create a convincing villain?", false)]
    [InlineData("איך יוצרים נבל משכנע?", false)]
    [InlineData("What happens in chapter 3?", false)]
    [InlineData("מה קורה בפרק 3?", false)]
    [InlineData("How do I split a chapter?", true)]
    [InlineData("איך מפצלים פרק?", true)]
    public void TheHowToSignal_NeedsAStructureVerbAndALocation(string question, bool expected)
        => Assert.Equal(expected, ProductChatRouter.Analyze(question).ProductHowTo);

    /// <summary>
    /// AND IT WITHDRAWS THE LOCATION IT NAMED, which is what puts those turns on
    /// <see cref="ChatRoute.Product"/> instead of on Union. Without this the veto alone would leave a
    /// product how-to mixed (a book word AND a product signal), and mixed is Union, where it is answered
    /// from the guides only by luck.
    /// </summary>
    [Theory]
    [InlineData("How do I add a chapter?")]
    [InlineData("איך מוסיפים פרק חדש?")]
    public void AProductHowTo_RoutesProduct_WithOrWithoutABookOpen(string question)
    {
        Assert.Equal(ChatRoute.Product, ProductChatRouter.Resolve(question, hasBookId: false, "en"));
        Assert.Equal(ChatRoute.Product, ProductChatRouter.Resolve(question, hasBookId: true, "en"));

        // VACUITY GUARD: the same location word WITHOUT the structure verb still reads as the book,
        // so the withdrawal above is the verb's doing and not a book signal that never fires.
        Assert.Equal(
            ChatRoute.Book,
            ProductChatRouter.Resolve(
                question.Contains("chapter") ? "What happens in chapter 3?" : "מה קורה בפרק 3?",
                hasBookId: true, "en"));
    }

    /// <summary>
    /// THE THRESHOLD IS 7.0 AND THAT NUMBER IS A MEASUREMENT. It shipped at 6.0, which was a description
    /// of the scoring (two exact heading matches at weight 3.0) and of no question at all. g3 ran 102 real
    /// turns: four of the eight English craft questions scored EXACTLY 6.0 off the guides' incidental
    /// vocabulary, routed Product, and were all four refused as product questions the corpus does not
    /// cover. Nothing in the run scored between 6.0 and 7.0, so this is the smallest move that fixes all
    /// four, and it is pinned as a number because the next person to read
    /// <see cref="ProductChatRouter.StrongGuideTopScore"/> as "roughly two heading matches" would round it
    /// back down.
    /// </summary>
    [Fact]
    public void TheStrongGuideThreshold_IsWhatG3Calibrated()
    {
        Assert.Equal(7.0, ProductChatRouter.StrongGuideTopScore);

        const string craft = "When is a metaphor doing too much work?";
        Assert.Equal(ChatRoute.General, ProductChatRouter.Resolve(craft, hasBookId: false, "en", 6.0));
        Assert.Equal(ChatRoute.Product, ProductChatRouter.Resolve(craft, hasBookId: false, "en", 7.0));
    }

    /// <summary>
    /// THE HEBREW PRODUCT LEXICON CARRIES THE CLITIC-PREFIXED SURFACES. Whole-word matching is exact, so
    /// <c>מסך</c> does not match inside <c>במסך</c>; g3 measured two plain product questions falling to
    /// Union for exactly that reason and being answered with a refusal. Both of the measured questions are
    /// here verbatim, plus the four other clitics, so a later narrowing of the generator fails here rather
    /// than in a live run.
    /// </summary>
    [Theory]
    [InlineData("איפה במסך רואים את סטטוס העריכה?")]
    [InlineData("איך מתחילים ספר חדש במערכת?")]
    [InlineData("מה יש בהגדרות?")]
    [InlineData("איך מגיעים למסך הראשי?")]
    [InlineData("מה נעלם מהתפריט?")]
    [InlineData("איזו לשונית זו שמסך העריכה נפתח בה?")]
    public void ThePrefixedHebrewProductSurfaces_AreInTheLexicon(string question)
    {
        Assert.True(
            ProductChatRouter.Analyze(question).ProductSurface,
            $"no product-surface word matched: {question}");

        Assert.Equal(ChatRoute.Product, ProductChatRouter.Resolve(question, hasBookId: false, "he"));
    }

    /// <summary>
    /// AND THE TWO CELLS OF THAT CROSS PRODUCT THAT ARE ORDINARY HEBREW ARE EXCLUDED BY HAND.
    /// <c>בחשבון</c> is the idiom "לקחת בחשבון" (to take into account), which turns up in ordinary craft
    /// prose, and <c>מחשבון</c> is the noun "calculator". A generated lexicon that swept either would
    /// route a craft question Product, which is the class this round is closing.
    ///
    /// <para>VACUITY GUARD: the unprefixed <c>חשבון</c> and the surviving <c>לחשבון</c> DO match, so the
    /// two absences are the exclusion list and not a noun that was dropped altogether.</para>
    /// </summary>
    [Fact]
    public void TheCollidingHebrewSurfaces_AreExcluded()
    {
        Assert.False(ProductChatRouter.Analyze("כדאי לקחת בחשבון את קצב הסצנה").ProductSurface);
        Assert.False(ProductChatRouter.Analyze("מחשבון פשוט").ProductSurface);

        Assert.True(ProductChatRouter.Analyze("איך מוחקים חשבון?").ProductSurface);
        Assert.True(ProductChatRouter.Analyze("איך נכנסים לחשבון שלי?").ProductSurface);
    }

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

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── g3d / gate 4: THE ENGLISH PRODUCT DOCUMENTS FLOOR ──────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FLOOR IS 4.0 AND THE BOUNDARY IS PINNED AT 3 AND AT 4, the way
    /// <see cref="ProductChatRouter.StrongGuideTopScore"/> is, because the whole lever rests on that one
    /// digit. It is fitted to g3d's own scores and to nothing else: re-derived from that run's 102 records,
    /// the English product route's UNCOVERED cell scores {0,0,0,0,3,3,3,3} and its COVERED cell scores
    /// {3,3,4,6,7,7,7,9}. 4.0 is therefore the ONLY cut that takes the whole uncovered cell while leaving
    /// every covered turn that answered; 3.0 would take none of the uncovered cell at all, and 5.0 would
    /// start eating covered turns that answer well (<c>B|en|3</c> scored exactly 4).
    ///
    /// <para>UNLIKE THE THRESHOLD ABOVE IT, THIS NUMBER HAS NOT BEEN MEASURED IN PLACE. It is fitted to one
    /// question set at n=8 per cell and the run that judges it has not been taken; see
    /// <see cref="ProductChatRouter.EnglishProductDocumentsFloor"/> for the two numbers that would say it is
    /// wrong. This test pins WHAT WAS SHIPPED so a later change is deliberate, not that the value is right.</para>
    /// </summary>
    [Fact]
    public void TheEnglishDocumentsFloor_IsTheCutGate4TookFromItsOwnScores()
    {
        Assert.Equal(4.0, ProductChatRouter.EnglishProductDocumentsFloor);

        // THE BOUNDARY, BOTH SIDES. 3 is the top of the uncovered cell and 4 is the bottom of the covered
        // one, so this is the exact pair the cut was chosen to separate.
        Assert.True(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 3.0));
        Assert.False(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 4.0));

        // The ends of the measured range, so a future off-by-one at either edge fails here.
        Assert.True(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 0.0));
        Assert.False(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 9.0));
    }

    /// <summary>
    /// AND THE EXACT RECORDS THE CUT MOVES, BY THEIR REAL QUESTIONS AND THEIR REAL g3d SCORES. The whole
    /// uncovered cell goes, and it takes exactly two covered turns with it - <c>B|en|4</c> and
    /// <c>B|en|7</c> - both of which had already answered with a refusal in all four runs, so the cut loses
    /// no answer that was working. <c>B|en|3</c> sits one point above the cut and stays, which is the
    /// tightest thing this table says: the floor is one point away from taking a covered turn that answers.
    /// </summary>
    [Theory]
    // C|en, the cell this round exists for: all eight move.
    [InlineData("How do I change my account password?", 3.0, true)]
    [InlineData("Is there a mobile app for PageDraft?", 3.0, true)]
    [InlineData("How much does the monthly subscription cost?", 0.0, true)]
    [InlineData("How do I invite a co-editor to my account so they can leave comments?", 3.0, true)]
    [InlineData("What is the keyboard shortcut for inserting a comment?", 0.0, true)]
    [InlineData("Can I share my screen with a publisher through the app?", 3.0, true)]
    [InlineData("Is there a dark mode in the settings?", 0.0, true)]
    [InlineData("How do I permanently delete my account?", 0.0, true)]
    // B|en, the covered cell: only the two that already refused move.
    [InlineData("What settings can I change in the app?", 3.0, true)]
    [InlineData("How do I open the editor and start working?", 3.0, true)]
    [InlineData("Where on the screen do I see the review status?", 4.0, false)]
    [InlineData("How do I import my manuscript into PageDraft?", 7.0, false)]
    [InlineData("What does the proofread pass do?", 6.0, false)]
    [InlineData("What is the difference between the editing passes the app offers?", 9.0, false)]
    // R|en, the residual how-tos: both scored 4 and neither moves.
    [InlineData("How do I add a chapter?", 4.0, false)]
    [InlineData("How do I delete a chapter?", 4.0, false)]
    public void TheDocumentsFloor_MovesExactlyTheRecordsGate4Named(
        string question, double guideTopScore, bool withheld)
    {
        // VACUITY GUARD: every row really is on the product route at the score it was measured at, so a
        // "withheld: false" row is the floor's doing and not a question that routes somewhere else.
        Assert.Equal(
            ChatRoute.Product,
            ProductChatRouter.Resolve(question, hasBookId: false, "en", guideTopScore));

        Assert.Equal(
            withheld,
            ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", guideTopScore));
    }

    /// <summary>
    /// HEBREW IS EXCLUDED, AND THAT ASYMMETRY IS DELIBERATE AND MEASURED. Hebrew's product-uncovered cell
    /// is the win of the four gate runs (8/8 to 3/8), and its COVERED cell answers well at exactly the
    /// scores this cut would take away. <c>B|he|0</c> and <c>B|he|2</c> are the two records that make the
    /// case: both scored 3, and both returned a correct, substantive answer in all four runs -
    /// <c>B|he|0</c> the drag-or-browse import flow with the add-or-replace choice, <c>B|he|2</c> the
    /// proofread pass producing suggestions the author approves one at a time. Applying the English floor
    /// to Hebrew would replace both with "I do not have that information".
    ///
    /// <para>SO A READER WHO NOTICES THE ASYMMETRY AND FIXES IT FAILS HERE, which is the point of pinning
    /// the two questions verbatim rather than asserting on a language flag. The route is identical for the
    /// pair; only the grounding decision differs.</para>
    /// </summary>
    [Theory]
    [InlineData("איך מייבאים כתב יד לתוכנה?")]      // B|he|0, score 3, answered well in all four runs
    [InlineData("מה עושה כפתור ההגהה?")]             // B|he|2, score 3, answered well in all four runs
    public void TheDocumentsFloor_NeverFiresOnHebrew(string question)
    {
        Assert.Equal(ChatRoute.Product, ProductChatRouter.Resolve(question, hasBookId: false, "he", 3.0));

        Assert.False(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "he", 3.0));
        Assert.False(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "he", 0.0));

        // VACUITY GUARD: the same score on the same route DOES withhold in English, so the two falses above
        // are the language condition and not a predicate that never fires.
        Assert.True(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 3.0));
    }

    /// <summary>
    /// A LANGUAGE THIS LAYER DOES NOT SERVE KEEPS ITS DOCUMENTS. The condition asks for
    /// <see cref="ChatLanguage.English"/> rather than for "not Hebrew", so an unrecognised tag lands on the
    /// status quo instead of inheriting an English-only lever by not being Hebrew. Null is included because
    /// the predicate is public and a caller outside this service can reach it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("en-GB")]   // NOT normalized here: the service resolves the tag before this is ever called
    public void TheDocumentsFloor_NeedsEnglishExactly(string? language)
        => Assert.False(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, language, 0.0));

    /// <summary>
    /// IT IS A PRODUCT-ROUTE LEVER AND NOTHING ELSE. The other three routes at the lowest possible score
    /// keep what they had: Union is the fallback every misroute lands on and must return the status quo,
    /// Book's answer comes out of the BOOK section, and General already carries no documents by its own
    /// decision. Gate 4's floors include General/A at 0/16 and the Book route at 0/28, and this is what
    /// keeps this change from being able to move either.
    /// </summary>
    [Theory]
    [InlineData(ChatRoute.Union)]
    [InlineData(ChatRoute.Book)]
    [InlineData(ChatRoute.General)]
    public void TheDocumentsFloor_FiresOnTheProductRouteAlone(ChatRoute route)
    {
        Assert.False(ProductChatRouter.WithholdsProductDocuments(route, "en", 0.0));

        // VACUITY GUARD: same language, same score, Product route.
        Assert.True(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 0.0));
    }

    /// <summary>
    /// A FLOOR OF ZERO IS THE KILL SWITCH, and it is tested for explicitly rather than left to arithmetic:
    /// <c>score &lt; 0</c> would already be false for every real score, but a reader setting the config to 0
    /// is saying "turn this off", and a later change that made the comparison inclusive would silently turn
    /// the off switch into "withhold from everything". Negative values mean the same thing.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void TheDocumentsFloor_IsOffAtZeroOrBelow(double floor)
    {
        Assert.False(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 0.0, floor));
        Assert.False(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 3.0, floor));

        // VACUITY GUARD: the shipped floor withholds both of those.
        Assert.True(ProductChatRouter.WithholdsProductDocuments(ChatRoute.Product, "en", 3.0));
    }

    /// <summary>
    /// AND IT DID NOT LEAK INTO THE ROUTE. <see cref="ProductChatRouter.Resolve"/>'s language parameter is
    /// documented as accepted and never consulted, and the whole table is resolved twice above to pin it.
    /// This lever IS language-dependent, so it is kept out of <c>Resolve</c> deliberately; this asserts the
    /// pin still holds at the scores the floor cares about, which is the one place a careless fold-in would
    /// show up.
    /// </summary>
    [Fact]
    public void TheRoute_IsStillLanguageIndependentAtTheFloorScores()
    {
        foreach (var score in new[] { 0.0, 3.0, 3.9, 4.0 })
        {
            foreach (var row in Table)
            {
                Assert.Equal(
                    ProductChatRouter.Resolve(row.English, row.HasBookId, "he", score),
                    ProductChatRouter.Resolve(row.English, row.HasBookId, "en", score));
            }
        }
    }
}
