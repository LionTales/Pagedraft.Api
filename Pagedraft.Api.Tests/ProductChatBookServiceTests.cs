using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The endpoint's server half with a book in scope (chatbot phase B, c1): what reaches the router, what
/// comes back to the client, and what happens when the book cannot be read.
///
/// <para>THE BOOK READER IS STUBBED HERE ON PURPOSE. Every property this file pins is a property of the
/// COMPOSITION, and driving it through a real database would measure Entity Framework instead. The
/// reader's own decisions - which briefs, which findings, which chapter escalates - are pure static
/// helpers and are pinned directly at the bottom of this file.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK.</para>
/// </summary>
public class ProductChatBookServiceTests
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

    private static BookChatContext Context(params BookArtifactBlock[] blocks)
        => new("Salt and Rope", blocks, BookArtifactSelector.BookQuestionKeys.Empty,
               Array.Empty<string>(), Array.Empty<int>(), Array.Empty<int>());

    // ─── Absent bookId: phase A, untouched ──────────────────────────────────────────────────────

    /// <summary>
    /// A REQUEST WITH NO bookId NEVER READS A BOOK CONTEXT, and the assertion is structural: the reader
    /// handed to the service THROWS if it is called. A test that merely checked "no book text appeared
    /// in the prompt" would pass for a reader that ran, hit the database and happened to return nothing.
    /// </summary>
    [Fact]
    public async Task WithNoBookId_TheBookReader_IsNeverCalled()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            bookContext: new ProductChatBudgetTests.ThrowingBookChatContextReader());

        var result = await svc.AnswerAsync(new ProductChatRequest("How do I export my book?"), CancellationToken.None);

        Assert.True(result.IsGrounded);
        Assert.Empty(result.ArtifactRefs ?? Array.Empty<string>());
        Assert.Null(result.BookFaultReason);

        var request = Assert.Single(captured);
        Assert.DoesNotContain(ProductChatPrompt.BookMarker, request.Instruction);
        Assert.Contains("is not available yet and is coming", request.Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// A book-less turn still selects <see cref="GuideSelector.DefaultCount"/> guides. Phase B halves the
    /// count only when a book is in scope, so the product-only traffic A measured is unchanged.
    /// </summary>
    [Fact]
    public async Task WithNoBookId_TheGuideCount_IsPhaseAs()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null);

        await svc.AnswerAsync(new ProductChatRequest("How do I export my book?"), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.NotNull(request.Instruction);
        var guideHeaders = request.Instruction
            .Split("=== GUIDE id=", StringSplitOptions.None).Length - 1;

        Assert.Equal(GuideSelector.DefaultCount, guideHeaders);
    }

    // ─── With a bookId ──────────────────────────────────────────────────────────────────────────

    /// <summary>The bookId and the question reach the reader verbatim, and the retrieved blocks reach
    /// the prompt under the book-aware grounding rule.</summary>
    [Fact]
    public async Task WithABookId_TheArtifacts_ReachThePromptUnderTheBookAwareRule()
    {
        var bookId = Guid.NewGuid();
        var captured = new List<AiRequest>();
        var reader = new ProductChatBudgetTests.StubBookChatContextReader(Context(
            Block(BookArtifactKind.Status, "status:summary"),
            Block(BookArtifactKind.ChapterBrief, "chapter-brief:3", "Miriam climbs the stair.")));

        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null, bookContext: reader);

        await svc.AnswerAsync(
            new ProductChatRequest("What happens in chapter 4?", BookId: bookId), CancellationToken.None);

        Assert.Equal(bookId, reader.LastBookId);
        Assert.Equal("What happens in chapter 4?", reader.LastQuestion);

        var instruction = Assert.Single(captured).Instruction;
        Assert.Contains(ProductChatPrompt.BookMarker, instruction, StringComparison.Ordinal);
        Assert.Contains(
            ProductChatPrompt.BookTitleLabel + "Salt and Rope", instruction, StringComparison.Ordinal);
        Assert.Contains("ref=chapter-brief:3", instruction, StringComparison.Ordinal);
        Assert.Contains("Miriam climbs the stair.", instruction, StringComparison.Ordinal);

        // The book-aware rule replaced the phase-A refusal, and A's product half is still there.
        Assert.Contains("answer it from the BOOK section below", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("is not available yet and is coming", instruction, StringComparison.Ordinal);
        Assert.Contains("a bare refusal is the whole answer", instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// A book-scoped turn halves the guide count. This is arithmetic, not taste: phase A's own measured
    /// Hebrew worst case leaves ~274 tokens of headroom and ONE formatted chapter brief costs 700-800.
    /// </summary>
    [Fact]
    public async Task WithABookId_TheGuideCount_DropsToTwo()
    {
        var captured = new List<AiRequest>();
        var reader = new ProductChatBudgetTests.StubBookChatContextReader(
            Context(Block(BookArtifactKind.Status, "status:summary")));

        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null, bookContext: reader);

        await svc.AnswerAsync(
            new ProductChatRequest("How do I export my book?", BookId: Guid.NewGuid()), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.NotNull(request.Instruction);
        var guideHeaders = request.Instruction
            .Split("=== GUIDE id=", StringSplitOptions.None).Length - 1;

        Assert.Equal(ProductChatService.BookAwareGuideCount, guideHeaders);
        Assert.True(guideHeaders >= ProductChatBudget.MinGuides,
            "a mixed question must never lose ALL product grounding");
    }

    /// <summary>
    /// The response separates guide ids from book-artifact refs, so the client can render two kinds of
    /// chip without re-parsing prose. Both lists are narrowed to what the prompt actually carried.
    /// </summary>
    [Fact]
    public async Task TheResponse_SeparatesGuideIdsFromArtifactRefs()
    {
        const string question = "What does Miriam do in chapter 4, and how do I export it?";

        // The guide this turn will really be given, computed the same way the service computes it, so
        // the fixture cites a guide that was actually selected rather than one that happens to be in the
        // shipped corpus. Citing an unselected id would be REFUSED by the parser, and the test would then
        // be measuring the refusal path while looking like it measured the citation path.
        var selectedGuideId = GuideSelector.Select(
                question, ProductChatCorpusTests.LoadRealCorpus().Documents, "en",
                ProductChatService.BookAwareGuideCount)[0].Id;

        var captured = new List<AiRequest>();
        var reader = new ProductChatBudgetTests.StubBookChatContextReader(Context(
            Block(BookArtifactKind.Status, "status:summary"),
            Block(BookArtifactKind.ChapterText, "chapter-text:3", "Miriam climbs the stair.")));

        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured, $"She climbs the stair.\nGuides: chapter-text:3, {selectedGuideId}"),
            out _, guidesDirectory: null, aiOptions: null, bookContext: reader);

        var result = await svc.AnswerAsync(
            new ProductChatRequest(question, BookId: Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(new[] { "chapter-text:3" }, result.ArtifactRefs);
        Assert.Equal(new[] { selectedGuideId }, result.GuideIds);
        Assert.DoesNotContain("Guides:", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IsGrounded);
        Assert.Null(result.BookFaultReason);
    }

    /// <summary>
    /// A PARTIAL book fault does NOT refuse the turn: the two halves fail independently, and an answer
    /// built from the artifacts that DID read is better than a refusal. The fault still travels to the
    /// client so the drawer can say the answer is thinner than usual.
    /// </summary>
    [Fact]
    public async Task APartialBookFault_StillAnswers_AndReportsTheFault()
    {
        var captured = new List<AiRequest>();
        var partial = new BookChatContext(
            "Salt and Rope",
            new[] { Block(BookArtifactKind.Status, "status:summary") },
            BookArtifactSelector.BookQuestionKeys.Empty,
            new[] { BookChatFaults.RegisterUnreadable },
            Array.Empty<int>(), Array.Empty<int>());

        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            bookContext: new ProductChatBudgetTests.StubBookChatContextReader(partial));

        var result = await svc.AnswerAsync(
            new ProductChatRequest("Who is Miriam?", BookId: Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsGrounded);
        Assert.Null(result.FaultReason);                                  // phase A's contract, untouched
        Assert.Equal(BookChatFaults.RegisterUnreadable, result.BookFaultReason);
        Assert.Single(captured);
    }

    /// <summary>
    /// A TOTAL book failure produces the honest fail-safe, never an answer. This mirrors phase A's
    /// posture exactly: the sentence is DETERMINISTIC rather than something the model is asked to
    /// produce, because "never from priors" has to be a property of the code path.
    /// </summary>
    [Theory]
    [InlineData("Who is Miriam in my book?", "I cannot see your book right now")]
    [InlineData("מי זו מרים בספר שלי?", "אינני מצליח לראות כרגע את הספר שלכם")]
    public async Task ATotalBookFailure_ReturnsTheHonestFailSafe_AndLogsWhy(string question, string expected)
    {
        var captured = new List<AiRequest>();
        var blind = BookChatContext.None with { Faults = new[] { BookChatFaults.BookUnavailable } };

        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out var logger, guidesDirectory: null, aiOptions: null,
            bookContext: new ProductChatBudgetTests.StubBookChatContextReader(blind));

        var result = await svc.AnswerAsync(
            new ProductChatRequest(question, BookId: Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsGrounded);
        Assert.Equal(BookChatFaults.BookUnavailable, result.FaultReason);
        Assert.Equal(BookChatFaults.BookUnavailable, result.BookFaultReason);
        Assert.Contains(expected, result.Answer, StringComparison.Ordinal);
        Assert.Empty(captured);   // the model was never asked

        // OBSERVABILITY: a catch that keeps the endpoint non-throwing and says nothing ships its
        // failures invisibly. The refusal must LOG its cause.
        Assert.Contains(logger.AtLeast(LogLevel.Warning),
            m => m.Contains("REFUSED to answer about book", StringComparison.Ordinal)
              && m.Contains(BookChatFaults.BookUnavailable, StringComparison.Ordinal));
    }

    /// <summary>
    /// A reader that BREAKS ITS OWN CONTRACT and throws still cannot take the endpoint down, and still
    /// cannot produce an answer about a manuscript nothing was read from.
    /// </summary>
    [Fact]
    public async Task AThrowingBookReader_DegradesToTheFailSafe_AndLogsTheException()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out var logger, guidesDirectory: null, aiOptions: null,
            bookContext: new ProductChatBudgetTests.ThrowingBookChatContextReader());

        var result = await svc.AnswerAsync(
            new ProductChatRequest("Who is Miriam?", BookId: Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsGrounded);
        Assert.Equal(BookChatFaults.BookUnavailable, result.BookFaultReason);
        Assert.Contains(logger.AtLeast(LogLevel.Error), m => m.Contains("THREW for book", StringComparison.Ordinal));
    }

    // ─── The reader's pure decisions ────────────────────────────────────────────────────────────

    private static Pagedraft.Api.Models.ChapterBrief Brief(int order, params string[] characters)
        => new()
        {
            Title = $"Chapter {order}",
            Order = order,
            CharacterStates = characters
                .Select(c => new ChapterCharacterState { Name = c })
                .ToList()
        };

    /// <summary>
    /// A CHAPTER WHOSE RAW TEXT RODE ALONG DOES NOT ALSO PAY FOR ITS BRIEF. Its full text is in the
    /// prompt, so the summary of it is the one clearly wasted block in the assembly - and at ~700-800
    /// tokens a brief, waste here is measured in whole other chapters that did not fit. That is what the
    /// exclusion protects, and it is unchanged by the F-7 fix.
    /// </summary>
    [Fact]
    public void AChapterWhoseTextRodeAlong_DoesNotAlsoPayForItsBrief()
    {
        var keys = new BookArtifactSelector.BookQuestionKeys(
            ChapterOrders: new[] { 3 }, CharacterNames: Array.Empty<string>(),
            Dimensions: Array.Empty<string>(), HasLocationCue: true,
            EscalationChapterOrders: new[] { 3 });

        var ranked = BookChatContextReader.RankChapterBriefs(
            new[] { Brief(2), Brief(3), Brief(4) }, keys, new[] { 3 });

        Assert.DoesNotContain(3, ranked.Select(r => r.Brief.Order));

        // VACUITY GUARD: chapter 3's brief IS selectable when nothing escalated, so the exclusion is the
        // rule firing and not an empty input.
        var withoutEscalation = keys with { EscalationChapterOrders = Array.Empty<int>() };
        Assert.Contains(3, BookChatContextReader.RankChapterBriefs(
            new[] { Brief(2), Brief(3), Brief(4) }, withoutEscalation, Array.Empty<int>())
            .Select(r => r.Brief.Order));
    }

    /// <summary>
    /// THE FIXTURE THAT WOULD HAVE CAUGHT THE OTHER HALF OF F-7: a chapter the selector INTENDED to
    /// escalate but whose text never rode along (unreadable, empty, or past the escalation cap). The old
    /// exclusion keyed on the intent, so that chapter lost its brief AND gained no text and the prompt
    /// carried nothing at all about the chapter the question named. No fixture held that state: every
    /// escalation fixture in this suite had text to escalate, so intent and outcome always agreed.
    /// </summary>
    [Fact]
    public void AChapterThatMeantToEscalateButCarriedNoText_KeepsItsBrief()
    {
        var keys = new BookArtifactSelector.BookQuestionKeys(
            ChapterOrders: new[] { 3 }, CharacterNames: Array.Empty<string>(),
            Dimensions: Array.Empty<string>(), HasLocationCue: true,
            EscalationChapterOrders: new[] { 3 });

        var ranked = BookChatContextReader.RankChapterBriefs(
            new[] { Brief(2), Brief(3), Brief(4) }, keys, carriedRawText: Array.Empty<int>());

        Assert.Contains(3, ranked.Select(r => r.Brief.Order));

        // And it is the NAMED chapter, so it ranks first rather than merely surviving.
        Assert.Equal(3, ranked[0].Brief.Order);
    }

    /// <summary>A brief mentioning a matched character outranks one that does not, and a NAMED chapter
    /// outranks both.</summary>
    [Fact]
    public void ChapterBriefs_RankByNamedChapterThenByCharacterMention()
    {
        var keys = new BookArtifactSelector.BookQuestionKeys(
            ChapterOrders: new[] { 5 }, CharacterNames: new[] { "Miriam" },
            Dimensions: Array.Empty<string>(), HasLocationCue: true,
            EscalationChapterOrders: Array.Empty<int>());

        var ranked = BookChatContextReader.RankChapterBriefs(
            new[] { Brief(1, "Dov"), Brief(2, "Miriam"), Brief(5, "Dov") }, keys, Array.Empty<int>());

        // Chapter 1 mentions neither key, so a KEYED question drops it entirely rather than spending a
        // brief's worth of budget on an unrelated chapter.
        Assert.Equal(new[] { 5, 2 }, ranked.Select(r => r.Brief.Order));
    }

    /// <summary>
    /// A DIMENSION QUESTION RANKS A HEBREW BOOK'S CHAPTER BRIEFS, which it could not do at all before.
    ///
    /// <para>A resolved dimension is a CANONICAL slug (<c>pacing</c>), while a brief's
    /// <c>ThematicMarkers</c> are model-written prose in the BOOK's language. Matching the two directly
    /// could only ever succeed on an English book - <c>"קצב".Contains("pacing")</c> is false - so every
    /// Hebrew book ranked its briefs as though the question had named no dimension, and fell back to book
    /// order. Found by a CR bot on the PR, which reported it as a fixture that "does not exercise the
    /// brief-ranking behavior its comments describe"; the fixture was the symptom and this was the
    /// cause.</para>
    ///
    /// <para>Both languages are asserted in one test on purpose: the English half is what made the defect
    /// invisible, so a Hebrew-only assertion would pin the fix without pinning what hid it.</para>
    /// </summary>
    [Theory]
    [InlineData("קצב הסצנות איטי")]   // Hebrew marker, the case that was broken
    [InlineData("the pacing drags")]  // English marker, the case that always worked
    public void ADimensionQuestion_RanksBriefsWhoseMarkersNameItInEitherLanguage(string marker)
    {
        var keys = new BookArtifactSelector.BookQuestionKeys(
            ChapterOrders: Array.Empty<int>(), CharacterNames: Array.Empty<string>(),
            Dimensions: new[] { "pacing" }, HasLocationCue: false,
            EscalationChapterOrders: Array.Empty<int>());

        var marked = Brief(7);
        marked = marked with { ThematicMarkers = new[] { marker } };
        var unmarked = Brief(1) with { ThematicMarkers = new[] { "אהבה ואובדן" } };

        var ranked = BookChatContextReader.RankChapterBriefs(
            new[] { unmarked, marked }, keys, Array.Empty<int>());

        // Not merely "it survived": an unkeyed brief is dropped entirely once ANY brief is keyed, so the
        // marked chapter being the ONLY survivor is what says the marker actually scored.
        Assert.Equal(new[] { 7 }, ranked.Select(r => r.Brief.Order));
        Assert.True(ranked[0].Rank > 0, "a named dimension must raise the rank of a brief that carries it");
    }

    /// <summary>
    /// With NO keys at all, the selection falls back to book order rather than to nothing: "what happens
    /// in my book" is a real question and it grounds in the opening chapters.
    /// </summary>
    [Fact]
    public void WithNoKeys_ChapterBriefsFallBackToBookOrder()
    {
        var ranked = BookChatContextReader.RankChapterBriefs(
            new[] { Brief(0), Brief(1), Brief(2) }, BookArtifactSelector.BookQuestionKeys.Empty,
            Array.Empty<int>());

        Assert.Equal(new[] { 0, 1, 2 }, ranked.Select(r => r.Brief.Order));
    }

    /// <summary>The selection is CAPPED, because at d1's measured ~700-800 tokens per brief, six briefs
    /// already exceed the entire input budget and a thirty-brief list would be a loop the trimmer spends
    /// itself discarding.</summary>
    [Fact]
    public void TheChapterBriefSelection_IsCapped()
    {
        var briefs = Enumerable.Range(0, 30).Select(i => Brief(i)).ToList();

        var ranked = BookChatContextReader.RankChapterBriefs(
            briefs, BookArtifactSelector.BookQuestionKeys.Empty, Array.Empty<int>());

        Assert.Equal(BookChatContextReader.MaxChapterBriefs, ranked.Count);
    }

    /// <summary>A finding matching a named DIMENSION outranks one matching only a named chapter, which
    /// outranks one matching neither.</summary>
    [Fact]
    public void Findings_RankByDimensionThenByChapterAnchor()
    {
        var keys = new BookArtifactSelector.BookQuestionKeys(
            ChapterOrders: new[] { 4 }, CharacterNames: Array.Empty<string>(),
            Dimensions: new[] { "pacing" }, HasLocationCue: true,
            EscalationChapterOrders: Array.Empty<int>());

        var onDimension = new BookFinding { Dimension = "pacing", ChapterAnchorsJson = "[]" };
        var onChapter = new BookFinding { Dimension = "tone", ChapterAnchorsJson = "[{\"order\":4}]" };
        var onNeither = new BookFinding { Dimension = "theme", ChapterAnchorsJson = "[{\"order\":9}]" };

        Assert.True(BookChatContextReader.FindingRank(onDimension, keys)
                  > BookChatContextReader.FindingRank(onChapter, keys));
        Assert.True(BookChatContextReader.FindingRank(onChapter, keys)
                  > BookChatContextReader.FindingRank(onNeither, keys));

        // VACUITY GUARD: the weakest is genuinely zero, so the ordering above is not three equal ranks
        // compared by a stable sort.
        Assert.Equal(0.0, BookChatContextReader.FindingRank(onNeither, keys));
    }
}
