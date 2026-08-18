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

        // VACUITY GUARD: the very same answer text on a NON-general route does cite, so the emptiness
        // above is this route's licensing and not an answer shape that never cites anything.
        var productCaptured = new List<AiRequest>();
        var product = ProductChatBudgetTests.Service(
            AnsweringRouter(productCaptured), out _, guidesDirectory: null, aiOptions: null,
            routingEnabled: true);
        var productResult = await product.AnswerAsync(
            new ProductChatRequest("How do I export my book to DOCX?"), CancellationToken.None);
        Assert.NotEmpty(productResult.GuideIds);
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
        var guideHeaders = request.Instruction!.Split("=== GUIDE id=", StringSplitOptions.None).Length - 1;

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
            unionRequest.Instruction!.Split("=== GUIDE id=", StringSplitOptions.None).Length - 1);
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
}
