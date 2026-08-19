using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE ROUTING LAYER WITH THE FLAG ON (g2), driven end to end through the real
/// <see cref="ProductChatService"/>.
///
/// <para>WHY THIS FILE EXISTS SEPARATELY FROM THE OTHER SERVICE TESTS. Every one of those constructs the
/// service without configuring <c>ProductChat:RoutingEnabled</c>, which means they all measure
/// <see cref="ChatRoute.Union"/> - deliberately, because they are the byte-identity fences and a fence
/// that moved with the flag would not be one. The shipped <c>appsettings.json</c> ships the flag ON, so
/// without this file the suite would be green over a code path production never takes. That gap, not a
/// weak assertion, is what this closes.</para>
///
/// <para>THE ROUTER IS A MOCK AND ITS INVOCATION COUNT IS PART OF THE ASSERTION, not scaffolding: the
/// central claim of the deterministic path is that NO MODEL IS CALLED, and a test that only read the
/// returned sentence would pass just as well against a service that asked the model and threw its answer
/// away.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE.</para>
/// </summary>
public class ProductChatRoutedAnswerTests
{
    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    private static Mock<IAiRouter> AnsweringRouter(List<AiRequest> captured, string content = "An answer.")
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .Callback<AiRequest, CancellationToken>((req, _) => captured.Add(req))
              .ReturnsAsync(new AiResponse { Content = content, Provider = "test", Model = "test-model" });
        return router;
    }

    private static BookArtifactBlock Block(BookArtifactKind kind, string reference, string body = "body")
        => new(kind, new[] { reference }, $"=== ARTIFACT ref={reference} ===\n{body}", 0);

    private static IBookChatContextReader BookReader(params BookArtifactBlock[] blocks)
        => new ProductChatBudgetTests.StubBookChatContextReader(
            new BookChatContext("Salt and Rope", blocks, BookArtifactSelector.BookQuestionKeys.Empty,
                                Array.Empty<string>(), Array.Empty<int>(), Array.Empty<int>()));

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 1. A BOOK QUESTION WITH NO BOOK OPEN, ANSWERED IN CODE ─────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SENTENCE THE FALSE REFUSAL WAS DELETED IN FAVOUR OF. Asked about a chapter with no book open,
    /// Show says he can only see a book while it is open - in the language the question was asked in, from
    /// a fixed string, with the model never reached.
    ///
    /// <para>THE MODEL-CALL COUNT IS THE LOAD-BEARING ASSERTION. The wording it replaces ("answering
    /// questions about a specific book is not available yet and is coming") was a PROMPT sentence, and it
    /// was measured being read back verbatim including its imperative 6 of 6 runs of this question shape;
    /// the reason g2 moved it into code is that a fixed answer cannot be renegotiated by the model.</para>
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 3?", "I can only see a book while it is open.")]
    [InlineData("מה קורה בפרק 3?", "אני יכול לראות ספר רק כשהוא פתוח.")]
    public async Task ABookQuestionWithNoBookOpen_IsAnsweredWithoutAModelCall(
        string question, string expectedOpening)
    {
        var captured = new List<AiRequest>();
        var router = AnsweringRouter(captured);
        var svc = ProductChatBudgetTests.Service(
            router, out _, guidesDirectory: null, aiOptions: null, routingEnabled: true);

        var result = await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        Assert.Empty(captured);
        router.Verify(
            r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.StartsWith(expectedOpening, result.Answer, StringComparison.Ordinal);

        // IT IS AN ANSWER, NOT A FAULT, and the distinction is not cosmetic: the client renders a
        // non-grounded response with its OWN per-reason copy and discards the server's prose entirely
        // (product-chat.component.ts, acceptResponse), so shipping this as a fault would replace the
        // sentence with "I cannot reach the guides right now".
        Assert.True(result.IsGrounded);
        Assert.Null(result.FaultReason);
        Assert.Empty(result.GuideIds);
        Assert.Empty(result.ArtifactRefs ?? Array.Empty<string>());
        Assert.Null(result.BookFaultReason);
    }

    /// <summary>
    /// AND IT CARRIES NO CLAIM ABOUT WHAT IS COMING. The sentence this replaced is now false - Show has
    /// read the book since phase B - and the whole point of answering in code was that it can never be
    /// said again on this path.
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 3?", "is not available yet and is coming")]
    [InlineData("מה קורה בפרק 3?", "עדיין אינו")]
    public async Task TheDeterministicAnswer_NeverSaysTheFeatureIsComing(string question, string fragment)
    {
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(new List<AiRequest>()), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);

        var result = await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        Assert.DoesNotContain(fragment, result.Answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// WITH THE FLAG OFF, THE SAME QUESTION GOES TO THE MODEL. This is the rollback stated as a test: the
    /// documented way to undo every wording change in g2 is to set <c>ProductChat:RoutingEnabled</c> back
    /// to false, and that has to restore the deterministic path's traffic too, not only the prompt's. It
    /// is also the vacuity guard for the two tests above - without it they would pass against a service
    /// that never called the model at all.
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 3?")]
    [InlineData("מה קורה בפרק 3?")]
    public async Task WithRoutingOff_TheSameQuestion_StillReachesTheModel(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: false);

        await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Union, bookAware: false),
            request.SystemMessageOverride);
    }

    /// <summary>
    /// A CRAFT QUESTION IS NOT A BOOK QUESTION, and this is the misfire the deterministic path was
    /// narrowed to avoid. The book-content lexicon the router reads includes the six review DIMENSIONS
    /// ("pacing", "קצב"), so keying the code path on it would have met a general question about writing
    /// with an instruction to open a book. It is keyed on manuscript LOCATIONS instead, and a question
    /// that names none of them goes to the model like any other.
    /// </summary>
    [Theory]
    [InlineData("How do I improve the pacing of a novel?")]
    [InlineData("איך משפרים את הקצב של רומן?")]
    public async Task ACraftQuestionNamingNoLocation_IsNotAnsweredDeterministically(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);

        await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        Assert.Single(captured);
    }

    /// <summary>
    /// AND A BOOK QUESTION WITH A BOOK OPEN IS NEVER INTERCEPTED. The path exists for the state where
    /// nothing can be read; with a bookId there IS something to read, and short-circuiting there would
    /// refuse to answer a question the prompt is simultaneously carrying the answer to.
    /// </summary>
    [Fact]
    public async Task ABookQuestionWithABookOpen_IsNeverAnsweredDeterministically()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            bookContext: BookReader(Block(BookArtifactKind.Status, "status:summary")),
            routingEnabled: true);

        await svc.AnswerAsync(
            new ProductChatRequest("What happens in chapter 3?", BookId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.Single(captured);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 2. THE ROUTE THE SERVICE ACTUALLY APPLIES ──────────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A PRODUCT QUESTION COMPOSES THE PRODUCT MESSAGE, at the provider boundary and at the head of the
    /// user message. Both are checked, because they are two copies of one rule that Ollama's
    /// truncate-from-the-START behaviour is the reason for, and a route applied to one and not the other
    /// is the F-1 defect class.
    /// </summary>
    [Theory]
    [InlineData("How do I export my book to DOCX?")]
    [InlineData("איך מייצאים את הספר שלי?")]
    public async Task AProductQuestion_ComposesTheProductMessage(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);

        await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        var expected = ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Product, bookAware: false);

        Assert.Equal(expected, request.SystemMessageOverride);
        Assert.StartsWith(expected, request.Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CRAFT QUESTION WITH NO BOOK OPEN COMPOSES THE GENERAL MESSAGE, and the answer cites NOTHING. The
    /// second half is the one that could quietly fail: the citation parser falls back to the FULL
    /// acceptable set when it finds no citation line, and this route asks for no citation line, so a
    /// service that handed it the surviving guides would put chips for four guides under an answer written
    /// out of Show's own knowledge.
    ///
    /// <para>THE ROUTE STILL LICENSES NOTHING AT THE SERVICE, AND THAT IS NOT MADE REDUNDANT BY g3c's MISS
    /// POLICY. The policy turns the fallback off for <see cref="ChatRoute.Product"/> only; this route's empty
    /// ACCEPTABLE SET is upstream of the parser and stops a general answer from citing even when it does
    /// name a guide id, which the policy would not.</para>
    /// </summary>
    [Theory]
    [InlineData("How do I write better dialogue?")]
    [InlineData("איך כותבים דיאלוג טוב יותר?")]
    public async Task AGeneralCraftQuestion_ComposesTheGeneralMessage_AndCitesNothing(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);

        var result = await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.General, bookAware: false),
            request.SystemMessageOverride);

        Assert.True(result.IsGrounded);
        Assert.Empty(result.GuideIds);

        // VACUITY GUARD: a CITING answer on a NON-general route does cite, so the emptiness above is this
        // route's licensing and not a harness that can never return a reference.
        //
        // THE CONTROL NAMES A GUIDE SINCE g3c, AND THE OLD ONE WOULD NOW PASS FOR THE WRONG REASON. It used
        // to send the same bare "An answer." to the product route and lean on the parser's no-line fallback
        // to produce chips. That fallback is exactly what g3c turned off on this route
        // (ProductChatCitations.MissPolicy.CiteNothingWhenNothingIsNamed), so a no-line control now returns
        // empty on BOTH routes and the guard would be asserting nothing. The control therefore cites
        // explicitly, which is the stronger control anyway: it separates "this route licenses no citation"
        // from "this route's answer named none".
        var productCaptured = new List<AiRequest>();
        var product = ProductChatBudgetTests.Service(
            AnsweringRouter(productCaptured, "An answer.\nGuides: export"), out _,
            guidesDirectory: null, aiOptions: null, routingEnabled: true);
        var productResult = await product.AnswerAsync(
            new ProductChatRequest("How do I export my book to DOCX?"), CancellationToken.None);
        Assert.NotEmpty(productResult.GuideIds);
    }

    /// <summary>
    /// THE GENERAL ROUTE CARRIES NO GUIDES AT ALL (g3), and the assertion is on the composed instruction
    /// rather than on the count constant, because the constant is only a promise until composition keeps
    /// it. g2 shipped a prompt saying to mention PageDraft "only where the guides below say it" while
    /// still sending phase A's four guides, and g3 measured 3 of 8 Hebrew craft turns inventing a
    /// PageDraft behaviour out of that material - Chapter recap detecting repeated dialogue, the
    /// Linguistic pass warning about emotional depth, PageDraft warning you when you change narrative
    /// person, none of which any guide states.
    ///
    /// <para>THE SECTION MARKER GOES WITH THEM. An empty <c>[GUIDES]</c> header is a labelled place where
    /// the grounding is supposed to be, in front of a model this whole round is stopping from talking
    /// about where it looked.</para>
    /// </summary>
    [Theory]
    [InlineData("How do I write better dialogue?")]
    [InlineData("איך כותבים דיאלוג טוב יותר?")]
    public async Task AGeneralCraftQuestion_IsSentNoGuidesAtAll(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);

        await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(
            ProductChatService.GeneralRouteGuideCount,
            request.Instruction!.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(ProductChatPrompt.GuidesMarker, request.Instruction, StringComparison.Ordinal);

        // VACUITY GUARD: the same harness DOES send guides under a marker on a product question, so the
        // emptiness above is this route's decision and not a fixture with no corpus behind it.
        var productCaptured = new List<AiRequest>();
        var product = ProductChatBudgetTests.Service(
            AnsweringRouter(productCaptured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);
        await product.AnswerAsync(
            new ProductChatRequest("How do I export my book to DOCX?"), CancellationToken.None);

        var productRequest = Assert.Single(productCaptured);
        Assert.Contains(
            ProductChatPrompt.GuidesMarker, productRequest.Instruction, StringComparison.Ordinal);
        Assert.True(
            productRequest.Instruction!.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1 > 0);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── g3c: WHAT A PRODUCT REPLY THAT NAMED NOTHING CITES ─────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A PRODUCT ANSWER THAT NAMES NO GUIDE CITES NO GUIDE (g3c), which is a decision and no longer the
    /// parser's fallback. g3c measured the fallback publishing the whole selection under bare refusals: the
    /// product route's grounding block ends by telling the model to say it does not have the answer and
    /// stop, most of those refusals carry no citation line, and the chips came out as four documents
    /// rendered underneath "I do not have that information". Narrowed citations on that route fell 32/36 to
    /// 20/36 and the 4-id full selection went 4 to 16.
    ///
    /// <para>THE ANSWER TEXT HERE IS A REFUSAL AND THE ASSERTION DOES NOT DEPEND ON THAT, which is the
    /// point: the rule is keyed on the ROUTE and on whether a reference was named, never on classifying the
    /// prose as a refusal. Any product answer with no citation line lands here.</para>
    /// </summary>
    [Theory]
    [InlineData("How do I export my book to DOCX?", "I do not have that information.")]
    [InlineData("איך מייצאים את הספר ל-DOCX?", "אין לי את המידע הזה.")]
    public async Task AProductAnswerNamingNoGuide_CitesNothing(string question, string answer)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured, answer), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);

        var result = await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Product, bookAware: false),
            request.SystemMessageOverride);

        Assert.True(result.IsGrounded);
        Assert.Empty(result.GuideIds);

        // VACUITY GUARD, and it is the one this test needs most: the SAME question on the SAME harness
        // does cite when the model names a guide, so the emptiness above is the miss policy and not a
        // selection that was empty to begin with.
        var citing = new List<AiRequest>();
        var svc2 = ProductChatBudgetTests.Service(
            AnsweringRouter(citing, answer + "\nGuides: export"), out _, guidesDirectory: null,
            aiOptions: null, routingEnabled: true);
        var cited = await svc2.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);
        Assert.NotEmpty(cited.GuideIds);
    }

    /// <summary>
    /// THE BOOK ROUTE KEEPS THE FALLBACK, and this is the half of g3c's decision that says the rule is
    /// scoped rather than global. On a book-scoped turn the chips are how an author checks an answer against
    /// their own manuscript, so an answer that used the artifacts and merely forgot its line still shows
    /// where it came from. Same harness, same missing citation line, opposite result.
    /// </summary>
    [Fact]
    public async Task ABookAnswerNamingNoRef_StillFallsBackToWhatItWasGiven()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured, "Tamar leaves at dawn."), out _, guidesDirectory: null,
            aiOptions: null,
            bookContext: BookReader(
                Block(BookArtifactKind.Status, "status:summary"),
                Block(BookArtifactKind.ChapterBrief, "chapter-brief:7", "Tamar leaves at dawn.")),
            routingEnabled: true);

        var result = await svc.AnswerAsync(
            new ProductChatRequest("What happens in chapter 8?", BookId: Guid.NewGuid()),
            CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Book, bookAware: true),
            request.SystemMessageOverride);

        Assert.Contains("chapter-brief:7", result.ArtifactRefs ?? Array.Empty<string>());
    }

    /// <summary>
    /// A PRODUCT HOW-TO NAMING A CHAPTER IS ANSWERED, NOT DEFLECTED (g3). g1 named this residual and g3
    /// measured it failing 4 of 6: "How do I add a chapter?" was met with "open the book you are asking
    /// about", which is a non-answer to a question about the app's chapter list. The model IS called now,
    /// and the message it gets is the product one.
    /// </summary>
    [Theory]
    [InlineData("How do I add a chapter?")]
    [InlineData("How do I delete a chapter?")]
    [InlineData("איך מוסיפים פרק חדש?")]
    [InlineData("איך מוחקים פרק?")]
    public async Task AProductHowTo_ReachesTheModel_OnTheProductMessage(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);

        var result = await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Product, bookAware: false),
            request.SystemMessageOverride);
        Assert.DoesNotContain("only see a book while it is open", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("לראות ספר רק כשהוא פתוח", result.Answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// A BOOK QUESTION WITH A BOOK OPEN COMPOSES THE HEDGED BOOK MESSAGE, and the book artifacts are in
    /// the prompt under it.
    /// </summary>
    [Theory]
    [InlineData("What happens in chapter 3?")]
    [InlineData("מה קורה בפרק 3?")]
    public async Task ABookQuestion_ComposesTheHedgedBookMessage(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            bookContext: BookReader(
                Block(BookArtifactKind.Status, "status:summary"),
                Block(BookArtifactKind.ChapterBrief, "chapter-brief:3", "Miriam climbs the stair.")),
            routingEnabled: true);

        await svc.AnswerAsync(
            new ProductChatRequest(question, BookId: Guid.NewGuid()), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Book, bookAware: true),
            request.SystemMessageOverride);
        Assert.Contains(ProductChatPrompt.BookMarker, request.Instruction, StringComparison.Ordinal);
        Assert.Contains("ref=chapter-brief:3", request.Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOOK ROUTE PAYS THE GUIDES FOR THE ARTIFACTS: one guide, not phase B's two and not phase A's
    /// four. The tokens are not decorative - NumCtx is pinned at 16384 and one formatted chapter brief
    /// costs 700-800 of them.
    /// </summary>
    [Fact]
    public async Task TheBookRoute_DropsTheGuideCountToOne()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            bookContext: BookReader(Block(BookArtifactKind.Status, "status:summary")),
            routingEnabled: true);

        await svc.AnswerAsync(
            new ProductChatRequest("What happens in chapter 3?", BookId: Guid.NewGuid()),
            CancellationToken.None);

        var request = Assert.Single(captured);
        var guideHeaders = request.Instruction!.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1;

        Assert.Equal(ProductChatService.BookRouteGuideCount, guideHeaders);

        // VACUITY GUARD: the count really is a route decision. The same request with routing OFF composes
        // Union and keeps phase B's two, so "1" is not what this harness produces for every turn.
        var unionCaptured = new List<AiRequest>();
        var union = ProductChatBudgetTests.Service(
            AnsweringRouter(unionCaptured), out _, guidesDirectory: null, aiOptions: null,
            bookContext: BookReader(Block(BookArtifactKind.Status, "status:summary")),
            routingEnabled: false);
        await union.AnswerAsync(
            new ProductChatRequest("What happens in chapter 3?", BookId: Guid.NewGuid()),
            CancellationToken.None);

        var unionRequest = Assert.Single(unionCaptured);
        Assert.Equal(
            ProductChatService.BookAwareGuideCount,
            unionRequest.Instruction!.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// A ROUTE THAT COMPOSES A BOOK-LESS MESSAGE NEVER READS THE BOOK, and the assertion is STRUCTURAL:
    /// the reader handed to the service throws if it is called at all. A test that merely checked "no BOOK
    /// section appeared" would pass for a reader that ran, hit the database and had its result discarded -
    /// and the defect this prevents is worse than a wasted query, because rendering artifacts under a
    /// message with no book rule above them is grounding with no contract, the shape of every collision
    /// this prompt has recorded.
    /// </summary>
    [Fact]
    public async Task AProductQuestionWithABookOpen_NeverReadsTheBook()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            bookContext: new ProductChatBudgetTests.ThrowingBookChatContextReader(),
            routingEnabled: true);

        var result = await svc.AnswerAsync(
            new ProductChatRequest("How do I export my book to DOCX?", BookId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsGrounded);

        var request = Assert.Single(captured);
        Assert.DoesNotContain(ProductChatPrompt.BookMarker, request.Instruction, StringComparison.Ordinal);
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Product, bookAware: false),
            request.SystemMessageOverride);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── g3d / gate 4: AN ENGLISH PRODUCT TURN UNDER THE FLOOR IS HANDED NOTHING ────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    //
    // THESE RUN AGAINST THE REAL GUIDES CORPUS (guidesDirectory: null), which is what makes them a test of
    // the lever rather than of a fixture: the scores the floor compares against are the corpus's own, the
    // same ones the four gate runs recorded, so a corpus edit that lifted an uncovered question above the
    // floor fails here instead of in a live run.
    //
    // AND THE LEVER IS OFF AS SHIPPED, so every test in this section passes the rolled-back floor EXPLICITLY
    // rather than inheriting it. Gate run 5 measured the floor at 4.0 and rolled it back to 0 - it fabricated
    // PageDraft behaviour on 4 of 102 records against 0 of 408 before, without moving the source-narration
    // cell it exists for (ProductChatRouter.EnglishProductDocumentsFloor carries the whole record). What
    // these tests defend is that the MECHANISM still behaves as measured; what the shipped configuration
    // does is TheShippedConfiguration_SendsDocumentsOnEveryEnglishProductTurn, at the end of this section.

    /// <summary>
    /// AN ENGLISH PRODUCT QUESTION THE CORPUS DOES NOT COVER IS SENT NO DOCUMENTS AT ALL (g3d/gate 4), and
    /// the assertion is on the composed prompt rather than on the constant, for the reason the general
    /// route's twin above records: a count is a promise until composition keeps it.
    ///
    /// <para>THE THREE QUESTIONS ARE REAL RECORDS. They are <c>C|en|2</c>, <c>C|en|6</c> and <c>C|en|7</c>
    /// from gate 4's run, which scored 0 against the corpus and narrated their source anyway. Pinning the
    /// questions verbatim rather than a synthetic low scorer is what ties this test to the measurement.</para>
    /// </summary>
    [Theory]
    [InlineData("How much does the monthly subscription cost?")]
    [InlineData("Is there a dark mode in the settings?")]
    [InlineData("How do I permanently delete my account?")]
    public async Task AnEnglishProductQuestionUnderTheFloor_IsSentNoDocumentsAtAll(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true,
            englishProductDocumentsFloor: ProductChatRouter.RolledBackEnglishProductDocumentsFloor);

        await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(ChatLanguage.English, request.Language);

        // No documents, and no labelled place where documents would have been. The marker matters on its
        // own: an empty section is a named hole in front of a model this whole round is stopping from
        // talking about where it looked.
        Assert.DoesNotContain(ProductChatPrompt.GuidesMarker, request.Instruction, StringComparison.Ordinal);
        Assert.Equal(
            0,
            request.Instruction!.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1);

        // VACUITY GUARD, AND IT IS THE ONE THIS TEST NEEDS MOST: the same harness and the same corpus DO
        // send documents for an English product question the guides cover, so the emptiness above is the
        // floor and not a corpus that could not be read.
        var coveredCaptured = new List<AiRequest>();
        var covered = ProductChatBudgetTests.Service(
            AnsweringRouter(coveredCaptured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true,
            englishProductDocumentsFloor: ProductChatRouter.RolledBackEnglishProductDocumentsFloor);
        await covered.AnswerAsync(
            new ProductChatRequest("How do I import my manuscript into PageDraft?"),   // B|en|0, score 7
            CancellationToken.None);

        var coveredRequest = Assert.Single(coveredCaptured);
        Assert.Contains(
            ProductChatPrompt.GuidesMarker, coveredRequest.Instruction, StringComparison.Ordinal);
        Assert.True(
            coveredRequest.Instruction!.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1 > 0);
    }

    /// <summary>
    /// AND THE MESSAGE IT GETS INSTEAD IS STILL THE ANTI-FABRICATION ONE. This is the assertion the whole
    /// lever hangs on: the model has nothing in front of it, which is exactly the configuration where an
    /// invented product behaviour would appear, so the rules that hold the C cell at 16/16 appropriate
    /// refusals with 0 fabrications must all still reach it. Gate 4's lever is the General route's treatment
    /// of the DOCUMENTS and never of the CONTRACT - <c>GeneralGrounding</c> licenses an answer from Show's
    /// own knowledge, which on a product question is the definition of the failure being guarded against.
    ///
    /// <para>THE LITERALS ARE TYPED BY HAND, NEVER PASTED FROM THE COMPOSER, which is this suite's standing
    /// rule for a pin: a literal lifted from the code under test asserts that the code equals itself.</para>
    ///
    /// <para>AND THE RULES REACHING THE MODEL TURNED OUT NOT TO BE ENOUGH, WHICH IS WHY THE LEVER IS OFF.
    /// Gate run 5 ran this exact configuration live: every sentence asserted below was in front of the model,
    /// and four withheld English turns invented a settings menu, a security settings screen, official
    /// documentation and "your specific instructions" anyway. This test still says what it always said - the
    /// contract survives the withholding - and that is now on the record as necessary and NOT sufficient. Do
    /// not read a green here as licence to raise the floor.</para>
    /// </summary>
    [Fact]
    public async Task AWithheldProductTurn_StillCarriesEveryAntiFabricationRule()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true,
            englishProductDocumentsFloor: ProductChatRouter.RolledBackEnglishProductDocumentsFloor);

        await svc.AnswerAsync(
            new ProductChatRequest("How much does the monthly subscription cost?"), CancellationToken.None);

        var request = Assert.Single(captured);
        var system = request.SystemMessageOverride!;

        // The three rules g4's PASS is a measurement of, and the finished refusal to say instead.
        Assert.Contains(
            "do not state a setting, button, screen or behavior that is not written there",
            system, StringComparison.Ordinal);
        Assert.Contains(
            "do not assemble one out of parts that are only partly relevant",
            system, StringComparison.Ordinal);
        Assert.Contains(
            "never say that PageDraft lacks a thing or does not support it",
            system, StringComparison.Ordinal);
        Assert.Contains("'I do not have that information.'", system, StringComparison.Ordinal);

        // AND NOT THE GENERAL ROUTE'S LICENCE. If this ever appears here, a product question has been given
        // permission to be answered out of the model's own head.
        Assert.DoesNotContain("from your own knowledge of the craft", system, StringComparison.Ordinal);

        // THE CITATION SENTENCE IS GONE, because there are no ids to name and asking for one of none is an
        // invitation to invent one. This is the second half of the General route's treatment.
        Assert.DoesNotContain("End your reply with a line", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Guides: <id>", system, StringComparison.Ordinal);

        // The language rule still closes the message, so dropping the citation sentence did not truncate
        // the tail with it.
        Assert.Contains("Answer in English, because the question is in English", system, StringComparison.Ordinal);

        // THE HEAD OF THE USER MESSAGE IS THE SAME STRING, which is the F-1 property: exactly one rule
        // reaches the model because exactly one string exists.
        Assert.StartsWith(system, request.Instruction, StringComparison.Ordinal);

        // VACUITY GUARD: a COVERED English product turn on the same harness still gets the citation
        // sentence, so its absence above is the withholding and not a block that stopped composing.
        var coveredCaptured = new List<AiRequest>();
        var covered = ProductChatBudgetTests.Service(
            AnsweringRouter(coveredCaptured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true,
            englishProductDocumentsFloor: ProductChatRouter.RolledBackEnglishProductDocumentsFloor);
        await covered.AnswerAsync(
            new ProductChatRequest("How do I import my manuscript into PageDraft?"), CancellationToken.None);

        Assert.Contains(
            "End your reply with a line",
            Assert.Single(coveredCaptured).SystemMessageOverride, StringComparison.Ordinal);
    }

    /// <summary>
    /// HEBREW KEEPS ITS DOCUMENTS AT THE SAME SCORE, driven end to end rather than through the predicate,
    /// because this is the half a later "fix" of the asymmetry would break. <c>B|he|2</c> scored 3 against
    /// this corpus and answered correctly in all four gate runs; under an English-style floor it would have
    /// been handed nothing and refused.
    /// </summary>
    [Theory]
    [InlineData("איך מייבאים כתב יד לתוכנה?")]      // B|he|0
    [InlineData("מה עושה כפתור ההגהה?")]             // B|he|2
    public async Task AHebrewProductQuestion_KeepsItsDocuments(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);

        await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(ChatLanguage.Hebrew, request.Language);
        Assert.Contains(ProductChatPrompt.GuidesMarker, request.Instruction, StringComparison.Ordinal);
        Assert.True(
            request.Instruction!.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1 > 0);

        // And the Hebrew citation sentence still rides, since there are ids to name.
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Product, bookAware: false),
            request.SystemMessageOverride);
    }

    /// <summary>
    /// A WITHHELD TURN CITES NOTHING, EVEN IF THE MODEL WRITES A CITATION LINE ANYWAY. It licenses the same
    /// empty acceptable set the General route has passed since g2 - the same decision, not a third policy -
    /// so the parser's no-line fallback cannot decorate a refusal with chips for documents the turn never
    /// carried.
    ///
    /// <para>THE MODEL HERE NAMES A REAL GUIDE ID, WHICH IS THE HARD CASE: <c>export</c> exists in the
    /// corpus and would have been perfectly citable on a covered turn. It is a fabrication on THIS turn
    /// because this turn was handed no documents, and the empty acceptable set is what makes that a
    /// property of the composition rather than of the model's restraint.</para>
    ///
    /// <para>THE LINE ITSELF IS STILL LEFT IN THE PROSE, and that is a decision on the record rather than an
    /// oversight - see <c>ProductChatCitationContractTests</c>'s known-residual pin and the reasoning beside
    /// the strip in <c>ProductChatCitations</c>. The chips are the half that had to be closed here.</para>
    /// </summary>
    [Fact]
    public async Task AWithheldProductTurn_CitesNothingEvenWhenTheModelNamesAGuide()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured, "I do not have that information.\nGuides: export"), out _,
            guidesDirectory: null, aiOptions: null, routingEnabled: true,
            englishProductDocumentsFloor: ProductChatRouter.RolledBackEnglishProductDocumentsFloor);

        var result = await svc.AnswerAsync(
            new ProductChatRequest("Is there a dark mode in the settings?"), CancellationToken.None);

        Assert.True(result.IsGrounded);
        Assert.Empty(result.GuideIds);
        Assert.Empty(result.ArtifactRefs ?? Array.Empty<string>());

        // VACUITY GUARD: the same answer, the same id, on a COVERED English product question does cite, so
        // the emptiness above is the withheld turn's licence and not a harness that can never return a chip.
        var citing = new List<AiRequest>();
        var covered = ProductChatBudgetTests.Service(
            AnsweringRouter(citing, "You can export it.\nGuides: export"), out _,
            guidesDirectory: null, aiOptions: null, routingEnabled: true,
            englishProductDocumentsFloor: ProductChatRouter.RolledBackEnglishProductDocumentsFloor);
        var cited = await covered.AnswerAsync(
            new ProductChatRequest("How do I export my book to a file?"), CancellationToken.None);
        Assert.NotEmpty(cited.GuideIds);
    }

    /// <summary>
    /// THE CONFIG VALUE IS THE KILL SWITCH AND IT REACHES THE COMPOSITION. Setting the floor to 0 restores
    /// the pre-lever behaviour for the very question the lever was built for, without a code change - the
    /// same rollback posture <c>ProductChat:RoutingEnabled</c> carries for the routing layer as a whole. A
    /// threshold nobody can turn off is a threshold the next gate cannot argue with.
    ///
    /// <para>THE NEXT GATE ARGUED WITH IT AND WON, AND THIS IS THE PATH IT LEFT BY: gate run 5 set exactly
    /// this key to 0. What ships is now the left-hand side of this test, so the assertions here are also the
    /// shipped behaviour - <see cref="TheShippedConfiguration_SendsDocumentsOnEveryEnglishProductTurn"/>
    /// states that separately, against the real default rather than against a hand-passed 0.</para>
    /// </summary>
    [Fact]
    public async Task TheDocumentsFloor_CanBeTurnedOffInConfig()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true, englishProductDocumentsFloor: 0.0);

        await svc.AnswerAsync(
            new ProductChatRequest("Is there a dark mode in the settings?"), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Contains(ProductChatPrompt.GuidesMarker, request.Instruction, StringComparison.Ordinal);
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Product, bookAware: false),
            request.SystemMessageOverride);

        // VACUITY GUARD: the SAME question on the SAME harness at the ROLLED-BACK floor is withheld, so the
        // documents above are the config value and not a question that never qualified. It is passed by name
        // rather than left to the default, which is 0 now and would have made this guard assert nothing.
        var rolledBack = new List<AiRequest>();
        var withheld = ProductChatBudgetTests.Service(
            AnsweringRouter(rolledBack), out _, guidesDirectory: null, aiOptions: null, routingEnabled: true,
            englishProductDocumentsFloor: ProductChatRouter.RolledBackEnglishProductDocumentsFloor);
        await withheld.AnswerAsync(
            new ProductChatRequest("Is there a dark mode in the settings?"), CancellationToken.None);
        Assert.DoesNotContain(
            ProductChatPrompt.GuidesMarker,
            Assert.Single(rolledBack).Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// WHAT SHIPS WITHHOLDS FROM NOBODY, DRIVEN END TO END THROUGH THE REAL DEFAULT. Gate run 5 measured the
    /// floor at 4.0 on the ten records it was built for and the result was worse, not better: the English
    /// product-uncovered cell did not move (7/8 to 6/8 source-narrating under the blindness-corrected
    /// detector; the apparent 7/8 to 2/8 was the old detector failing to recognise vocabulary the withheld
    /// turns invented), and 4 of the 102 records asserted PageDraft behaviour that does not exist - a
    /// settings menu with appearance options, a security settings screen, "the official documentation",
    /// "your specific instructions" - against 0 in the 408 records of the four runs before it. Every one of
    /// the four was a withheld turn. <c>ProductChatRouter.EnglishProductDocumentsFloor</c> holds the record.
    ///
    /// <para>THE FIXTURE IS THE TEN RECORDS THE LEVER ACTUALLY MOVED, verbatim: the whole <c>C|en</c> cell
    /// plus <c>B|en|4</c> and <c>B|en|7</c>. Asserting on the questions it fired on is what makes this a pin
    /// on the rollback rather than a pin on an arbitrary question, and the four that fabricated are named in
    /// the table below so a reader of a failure here meets them.</para>
    ///
    /// <para>IT PASSES NO FLOOR, WHICH IS THE POINT. The helper's null default is the shipped value, so
    /// raising the const, the class default or the JSON key turns this red - re-enabling the lever has to be
    /// a deliberate act that changes a test, not a config typo nobody notices.</para>
    /// </summary>
    [Theory]
    [InlineData("How do I change my account password?")]                            // C|en|0, fabricated
    [InlineData("Is there a mobile app for PageDraft?")]                             // C|en|1, gap as fact
    [InlineData("How much does the monthly subscription cost?")]                     // C|en|2
    [InlineData("How do I invite a co-editor to my account so they can leave comments?")]  // C|en|3, fabricated
    [InlineData("What is the keyboard shortcut for inserting a comment?")]           // C|en|4
    [InlineData("Can I share my screen with a publisher through the app?")]          // C|en|5, gap as fact
    [InlineData("Is there a dark mode in the settings?")]                            // C|en|6, fabricated
    [InlineData("How do I permanently delete my account?")]                          // C|en|7
    [InlineData("What settings can I change in the app?")]                           // B|en|4
    [InlineData("How do I open the editor and start working?")]                      // B|en|7, fabricated
    public async Task TheShippedConfiguration_SendsDocumentsOnEveryEnglishProductTurn(string question)
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);   // NO floor: the shipped default, which is the kill switch.

        await svc.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(ChatLanguage.English, request.Language);

        // The documents are there, and so is the labelled place they sit in.
        Assert.Contains(ProductChatPrompt.GuidesMarker, request.Instruction, StringComparison.Ordinal);
        Assert.True(
            request.Instruction!.Split(ProductChatPrompt.GuideHeaderPrefix, StringSplitOptions.None).Length - 1 > 0,
            "the shipped configuration handed this English product turn no guide documents, which is the "
            + "lever gate run 5 rolled back");

        // AND THE CITATION SENTENCE RIDES AGAIN, which is the other half of the withholding: the turn gets
        // the ordinary Product system message, byte for byte, not the guides-carried:false variant.
        Assert.Equal(
            ProductChatPrompt.SystemMessage(request.Language, ChatRoute.Product, bookAware: false),
            request.SystemMessageOverride);
    }
}
