using System;
using System.Collections.Generic;
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
/// THE AMBIENT OPEN CHAPTER AT THE WIRE AND IN THE PROMPT (chatbot phase B, a1, implementing d2). The
/// selector's own rules are pinned next door in <see cref="ProductChatAmbientChapterTests"/>; this file
/// is the other two seams, which are where a correct selector still ships a broken feature.
///
/// <para>WHY THESE ARE SEPARATE FIXTURES. A field parsed off the request and then dropped on the way to
/// the retrieval is the whole capability failing silently, and it is invisible from the answer - which is
/// the exact shape of g1's F-1, where the right string existed and the wrong one reached the model. So
/// the ambient key is asserted where the READER receives it, and the clarify flag where the CLIENT
/// receives it, rather than only where they are computed.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE: the router is a capturing mock and the book reader is
/// a stub.</para>
/// </summary>
public class ProductChatAmbientWireTests
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 7. THE WIRE: the request carries it, the response reports it ───────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    private static BookChatContext Context(
        BookArtifactSelector.BookQuestionKeys keys, params BookArtifactBlock[] blocks)
        => new("Salt and Rope", blocks, keys, Array.Empty<string>(), Array.Empty<int>(), Array.Empty<int>());

    // RENDERED THROUGH THE REAL PRODUCTION RENDERER (review finding #13), not hand-written: this file
    // used to hand-write "ref=status:summary" while ProductChatComposedSystemSlotTests hand-wrote
    // "ref=status", both fixtures, so neither could ever go red on the real header shape.
    private static BookArtifactBlock Status()
        => BookArtifactBlocks.Statuses(null, null, null);

    private static BookArtifactSelector.BookQuestionKeys Keys(bool asks, params int[] orders)
        => new(orders, Array.Empty<string>(), Array.Empty<string>(), orders.Length > 0, orders)
        {
            NeedsChapterClarification = asks
        };

    /// <summary>
    /// THE AMBIENT KEY REACHES THE READER, both fields, exactly as the request stated them. A field that
    /// is parsed off the wire and then dropped on the way to the retrieval is the whole feature failing
    /// silently, and it is invisible from the answer.
    /// </summary>
    [Fact]
    public async Task TheAmbientChapter_ReachesTheReader_BothFields()
    {
        var chapterId = Guid.NewGuid();
        var reader = new ProductChatBudgetTests.StubBookChatContextReader(
            Context(Keys(asks: false, 3), Status()));

        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(new List<AiRequest>()), out _, guidesDirectory: null, aiOptions: null,
            bookContext: reader);

        await svc.AnswerAsync(
            new ProductChatRequest(
                "Does the conflict land in this chapter?", BookId: Guid.NewGuid(),
                AmbientChapterId: chapterId, AmbientChapterOrder: 3),
            CancellationToken.None);

        Assert.NotNull(reader.LastAmbient);
        Assert.Equal(chapterId, reader.LastAmbient!.ChapterId);
        Assert.Equal(3, reader.LastAmbient.ChapterOrder);
    }

    /// <summary>
    /// AND "NOTHING IS OPEN" REACHES IT AS A STATEMENT, not as a missing argument. The reader is handed
    /// an explicit ambient context with both fields null, which is what lets it tell "the drawer is open
    /// on the dashboard" apart from "this client is too old to say".
    /// </summary>
    [Fact]
    public async Task WithNoChapterOpen_TheReader_IsStillToldSoExplicitly()
    {
        var reader = new ProductChatBudgetTests.StubBookChatContextReader(
            Context(Keys(asks: true), Status()));

        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(new List<AiRequest>()), out _, guidesDirectory: null, aiOptions: null,
            bookContext: reader);

        await svc.AnswerAsync(
            new ProductChatRequest("Does the conflict land in this chapter?", BookId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.NotNull(reader.LastAmbient);
        Assert.Null(reader.LastAmbient!.ChapterId);
        Assert.Null(reader.LastAmbient.ChapterOrder);
        Assert.False(reader.LastAmbient.IsPresent);
    }

    /// <summary>
    /// THE RESPONSE FLAG IS THE SELECTION'S, NEVER THE ANSWER'S. The model's prose here says nothing
    /// about chapters at all, and the flag still reports what the retrieval decided - which is the whole
    /// reason d2 made this a structured field rather than a prompt instruction: this codebase has already
    /// measured the model failing to hold a rule under collision (F-1), so a boolean the model's own
    /// output cannot influence is the only thing that makes the anti-rule true by construction.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheClarifyFlag_IsReported_FromTheSelection(bool asks)
    {
        var reader = new ProductChatBudgetTests.StubBookChatContextReader(
            Context(asks ? Keys(asks: true) : Keys(asks: false, 3), Status()));

        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(new List<AiRequest>(), "A confident answer about nothing in particular."),
            out _, guidesDirectory: null, aiOptions: null, bookContext: reader);

        var result = await svc.AnswerAsync(
            new ProductChatRequest("What happens in the chapter?", BookId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(asks, result.NeedsChapterClarification);
    }

    /// <summary>
    /// THE BOOK-LESS PATH IS UNTOUCHED, INCLUDING BY A REQUEST THAT CARRIES AN AMBIENT CHAPTER. A client
    /// that sends an ambient chapter with no bookId gets phase A byte-for-byte: the reader is never
    /// called (the stub THROWS if it is), the flag is false, and the system slot is phase A's literal.
    /// g2's bucket (d) verdict is a measurement of exactly that string.
    /// </summary>
    [Fact]
    public async Task WithNoBookId_AnAmbientChapter_ChangesNothing()
    {
        var captured = new List<AiRequest>();
        var svc = ProductChatBudgetTests.Service(
            AnsweringRouter(captured), out _, guidesDirectory: null, aiOptions: null,
            bookContext: new ProductChatBudgetTests.ThrowingBookChatContextReader());

        var result = await svc.AnswerAsync(
            new ProductChatRequest(
                "How do I export my book?", AmbientChapterId: Guid.NewGuid(), AmbientChapterOrder: 3),
            CancellationToken.None);

        Assert.False(result.NeedsChapterClarification);
        Assert.True(result.IsGrounded);

        var request = Assert.Single(captured);
        Assert.Equal(ProductChatBookPromptTests.ShippedGroundingEn, request.SystemMessageOverride);
        Assert.DoesNotContain(ProductChatPrompt.BookMarker, request.Instruction);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 8. THE PROMPT'S HALF: one note, and only when nothing resolved ─────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The BOOK section carries the "no chapter identified" note exactly when the flag is true, so a
    /// client that does not render chapter chips still degrades to a readable question in the answer's
    /// own prose. The flag's TRUTH is never derived from whether the model actually asked.
    /// </summary>
    [Fact]
    public void TheClarifyNote_RidesOnlyWhenNothingResolved()
    {
        var blocks = new[] { Status() };
        var guides = GuideSelector.Select(
            "chapter", ProductChatCorpusTests.LoadRealCorpus().Documents, "en", 1);

        var asking = ProductChatBudget.Compose(
            "en", guides, Array.Empty<ProductChatTurn>(), "what happens in the chapter?", int.MaxValue,
            blocks, "Salt and Rope",
            BookArtifactSelector.BookQuestionKeys.Empty with { NeedsChapterClarification = true });

        var resolved = ProductChatBudget.Compose(
            "en", guides, Array.Empty<ProductChatTurn>(), "what happens in the chapter?", int.MaxValue,
            blocks, "Salt and Rope",
            BookArtifactSelector.BookQuestionKeys.Empty with { NeedsChapterClarification = false });

        Assert.Contains(
            BookArtifactBlocks.NoChapterIdentifiedNote("en"), asking.Instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(
            BookArtifactBlocks.NoChapterIdentifiedNote("en"), resolved.Instruction, StringComparison.Ordinal);

        // VACUITY GUARD: both compositions really did emit a BOOK section, so the absence above is the
        // flag and not a turn that carried no book at all.
        Assert.Contains(ProductChatPrompt.BookMarker, asking.Instruction, StringComparison.Ordinal);
        Assert.Contains(ProductChatPrompt.BookMarker, resolved.Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE TWO NOTES SHARE A CHANNEL BECAUSE THEY CANNOT CO-OCCUR, and this pins the disjointness rather
    /// than trusting it: the ambiguity note needs a number that named TWO chapters, the clarify
    /// note needs a selection of NONE, and one count cannot be both 2 and 0. The model is never handed
    /// two notes to arbitrate between, which is the collision shape this prompt has been burned by twice.
    /// </summary>
    [Fact]
    public void TheTwoBookSectionNotes_AreMutuallyExclusive()
    {
        var chapterBlocks = new[]
        {
            new BookArtifactBlock(BookArtifactKind.ChapterText, new[] { "chapter-text:4" }, "four", 1),
            new BookArtifactBlock(BookArtifactKind.ChapterText, new[] { "chapter-text:5" }, "five", 1)
        };

        // w9: an ambiguity is now a number that names two REAL chapters (two chapters both titled
        // "chapter 5"), never the manufactured 0-vs-1-based pair the selector used to produce.
        var ambiguity = BookArtifactBlocks.BookSectionNote(
            "en",
            BookArtifactSelector.BookQuestionKeys.Empty with
            {
                ChapterOrders = new[] { 4, 5 },
                AmbiguousChapterNumbers = new[]
                {
                    new BookArtifactSelector.ChapterReferenceAmbiguity("chapter 5", new[] { 4, 5 }, 5)
                }
            },
            chapterBlocks);

        var clarify = BookArtifactBlocks.BookSectionNote(
            "en",
            BookArtifactSelector.BookQuestionKeys.Empty with { NeedsChapterClarification = true },
            chapterBlocks);

        Assert.NotNull(ambiguity);
        // The note names the candidates by the only thing that separates two identically-named chapters -
        // where they sit - and it counts the way the author does, not the way the wire does.
        Assert.Contains("2 chapters of this book are named chapter 5", ambiguity!, StringComparison.Ordinal);
        Assert.Contains(
            "the briefs below cover 2 of them (chapters 5 and 6)", ambiguity, StringComparison.Ordinal);
        Assert.Contains(
            "answer from those, say which chapters you are describing and that other chapters share the " +
            "name, and offer to narrow to one",
            ambiguity, StringComparison.Ordinal);
        Assert.Equal(BookArtifactBlocks.NoChapterIdentifiedNote("en"), clarify);

        // No selection can produce both flags, so the shared channel has no ordering to decide: a
        // resolved-chapter count cannot be 0 and 2 at once.
        Assert.False(BookArtifactSelector.NeedsClarification(
            resolvedChapterCount: 2, escalatedChapterCount: 2, hasAmbientLocationWord: true,
            unresolvedNumberCount: 0, chapterCount: 8));
    }

    /// <summary>
    /// THE §6 GUIDE-FALLBACK RULE IS IN BOTH LANGUAGES AND IN THE BOOK-AWARE MODE ONLY. It closes cause
    /// (2) of the defect that opened this plan: the owner's turn carried 13 book artifacts and the answer
    /// came out of the faq guide, citing none of them. It is PERMISSIVE about process ("a guide may still
    /// help explain how the product works") and restrictive only about what the artifacts already cover,
    /// which is the scope-do-not-stack-prohibitions shape that closed A's gate.
    /// </summary>
    [Theory]
    [InlineData("en", "A guide may still help explain how the product works, but it does not stand in for")]
    [InlineData("he", "מדריך עדיין יכול לעזור להסביר איך המוצר עובד, אך הוא אינו")]
    public void TheGuideFallbackRule_IsPresentInTheBookAwareRule_AndAbsentFromPhaseAs(
        string language, string fragment)
    {
        Assert.Contains(fragment, ProductChatPrompt.SystemMessage(language, bookAware: true),
                        StringComparison.Ordinal);

        // AND IT NEVER REACHES A BOOK-LESS TURN: phase A's string is a gate verdict and is byte-frozen.
        Assert.DoesNotContain(fragment, ProductChatPrompt.SystemMessage(language, bookAware: false),
                              StringComparison.Ordinal);
    }

    private static Mock<IAiRouter> AnsweringRouter(
        List<AiRequest> captured, string content = "An answer.")
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .Callback<AiRequest, CancellationToken>((req, _) => captured.Add(req))
              .ReturnsAsync(new AiResponse { Content = content, Provider = "test", Model = "test-model" });
        return router;
    }
}
