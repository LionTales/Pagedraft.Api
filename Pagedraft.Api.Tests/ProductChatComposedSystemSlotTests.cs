using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE COMPOSED SYSTEM SLOT: the string a provider is actually handed, measured through the REAL
/// <see cref="AiRouter"/> and the REAL <see cref="PromptFactory"/>, driven by the REAL
/// <see cref="ProductChatService"/>.
///
/// <para>WHY THIS FILE EXISTS AT ALL. The 2,155-test suite was green before AND after g1's F-1, a defect
/// that shipped phase A's "answering questions about a specific book is not available yet" in the SYSTEM
/// slot while phase B's "answer it from the BOOK section below" sat in the user message, and returned
/// that refusal verbatim to the author 6 of 6 runs with a full 16-block book context in the prompt. It
/// was green because NO TEST ASSERTED ON THE COMPOSED SYSTEM SLOT FOR A BOOK-SCOPED TURN. Every prompt
/// test in this suite asserted on <c>ProductChatPrompt.SystemMessage(...)</c>, which was correct all
/// along; what was wrong was which of its two outputs reached the model, and that is decided two layers
/// away, in <c>PromptFactory</c> - which sees only the task type and therefore cannot know that this
/// turn carries a book. Nothing between the prompt class and the provider was under test. That gap, not
/// a weak assertion, is what this file closes: it is a SEED-SPACE fixture (a book-scoped turn observed at
/// the provider boundary), and it fails on the pre-fix code.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK, NO DATABASE. The provider is a capturing double and the book
/// reader is a stub; everything from <c>ProductChatService.AnswerAsync</c> down to
/// <see cref="ResolvedAiRequest"/> is the shipped code path.</para>
///
/// <para>A NOTE FOR WHOEVER EDITS PHASE B'S WORDING NEXT: <see cref="BookAwareEn"/> /
/// <see cref="BookAwareHe"/> below are LITERAL copies of the composed book-aware system message. A
/// deliberate change to the B grounding rule is SUPPOSED to fail these two tests, the same way phase A's
/// byte-identity literal is supposed to fail when someone edits a sentence a gate verdict rests on.
/// Update them in the same commit as the wording, and never by pasting
/// <c>ProductChatPrompt.SystemMessage(...)</c>'s output in place of the literal, which would turn the
/// assertion into a tautology.</para>
/// </summary>
public class ProductChatComposedSystemSlotTests
{
    // ─── The composed BOOK-AWARE system message, literally ──────────────────────────────────────
    //
    // head (phase A, verbatim) + B's grounding rule + tail (phase A, verbatim). Written out rather than
    // assembled from the production constants on purpose: an assertion built from the same pieces the
    // code under test uses cannot notice a piece going missing.

    private const string BookAwareEn =
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

    private const string BookAwareHe =
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

    // ─── Harness ────────────────────────────────────────────────────────────────────────────────

    private sealed class CapturingProvider : IAiAnalysisProvider
    {
        public ResolvedAiRequest? Captured { get; private set; }

        public Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken ct = default)
        {
            Captured = request;
            return Task.FromResult(new AiResponse
            {
                Content = "An answer.", Provider = "Fake", Model = request.Selection.Model
            });
        }
    }

    private static (ProductChatService Service, CapturingProvider Provider) ChatOverTheRealRouter(
        IBookChatContextReader? bookContext = null)
    {
        var provider = new CapturingProvider();
        var options = ProductChatBudgetTests.AiConfig();
        options.DefaultProvider = "Fake";
        Assert.NotNull(options.FeatureModels);
        options.FeatureModels["ProductChat"] = new FeatureModelOptions { Provider = "Fake", Model = "fake-model" };

        // THE TUNING KEY IS PROVIDER-SCOPED, so routing this harness at "Fake" silently orphaned
        // AiConfig's Ollama_ProductChat window and every book-scoped turn here composed against a
        // 1,792-token budget instead of the feature's 14,080 (f2). The system-message assertions did not
        // notice, because the statuses are undroppable and one surviving block is all "is this turn
        // book-aware" needs - but every book artifact BELOW the statuses was being trimmed away before
        // the provider ever saw it, which makes the harness blind to anything that depends on one
        // surviving. Mirrored here so the payload under test is the payload the feature composes.
        Assert.NotNull(options.ProviderSettings);
        options.ProviderSettings["Fake_ProductChat"] = options.ProviderSettings["Ollama_ProductChat"];

        // THE REAL ROUTER AND THE REAL FACTORY. Substituting either would put the defect's own home
        // outside the test: F-1 lived in PromptFactory's inability to see the book, resolved by AiRouter.
        var router = new AiRouter(
            Options.Create(options),
            new PromptFactory(),
            new Dictionary<string, IAiAnalysisProvider> { ["Fake"] = provider });

        var guides = new GuidesCorpusReader(
            ProductChatCorpusTests.RealGuidesDirectory(),
            ProductChatCorpusTests.NullLoggerFor<GuidesCorpusReader>());

        var service = new ProductChatService(
            guides, router, Options.Create(options),
            bookContext ?? new ProductChatBudgetTests.ThrowingBookChatContextReader(),
            ProductChatCorpusTests.NullLoggerFor<ProductChatService>());

        return (service, provider);
    }

    private static IBookChatContextReader BookReader(params BookArtifactBlock[] blocks)
        => BookReader(BookArtifactSelector.BookQuestionKeys.Empty, blocks);

    private static IBookChatContextReader BookReader(
        BookArtifactSelector.BookQuestionKeys keys, params BookArtifactBlock[] blocks)
        => new ProductChatBudgetTests.StubBookChatContextReader(
            new BookChatContext("Salt and Rope", blocks, keys,
                                Array.Empty<string>(), Array.Empty<int>(), Array.Empty<int>()));

    // RENDERED THROUGH THE REAL PRODUCTION RENDERER (review finding #13), not hand-written: a
    // hand-written fixture is exactly what let #2's header defect ship invisibly - this file wrote
    // "ref=status" while ProductChatAmbientWireTests wrote "ref=status:summary", both fixtures, so
    // neither could ever go red on the real header shape.
    private static BookArtifactBlock Status()
        => BookArtifactBlocks.Statuses(null, null, null);

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 1. THE PHASE-A FENCE, at the provider boundary ─────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// WITH NO BOOK IN SCOPE, THE SLOT A PROVIDER RECEIVES IS PHASE A'S, CHARACTER FOR CHARACTER, in both
    /// languages. Bucket (d) of g1 was 36/36 clean, and that is the proof phase A's closed fabrication
    /// class stayed closed; it is a measurement of exactly these sentences and of nothing else. Every
    /// other test of this property asserts on <c>ProductChatPrompt.SystemMessage</c>, one layer above the
    /// place where B's change actually lands.
    /// </summary>
    [Theory]
    [InlineData("How do I export my book?", ProductChatBookPromptTests.ShippedGroundingEn)]
    [InlineData("איך מייצאים את הספר שלי?", ProductChatBookPromptTests.ShippedGroundingHe)]
    public async Task WithNoBookId_TheProviderReceives_PhaseAsSystemMessage_ByteForByte(
        string question, string expected)
    {
        var (service, provider) = ChatOverTheRealRouter();

        await service.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        Assert.NotNull(provider.Captured);
        Assert.Equal(expected, provider.Captured!.SystemMessage);
    }

    /// <summary>
    /// And it is byte-identical to what <c>PromptFactory</c> itself returns, so the caller-supplied
    /// override is a NO-OP on the book-less path rather than a second, parallel copy of phase A's wording
    /// that could drift from the factory's. This is the "by construction" half of the fence: the identity
    /// holds because the two strings come from the same method, not because someone remembered to skip
    /// the override when no book was present.
    /// </summary>
    [Theory]
    [InlineData("How do I export my book?", "en")]
    [InlineData("איך מייצאים את הספר שלי?", "he")]
    public async Task WithNoBookId_TheOverride_IsAnIdentityOnTheFactorysOutput(string question, string language)
    {
        var (service, provider) = ChatOverTheRealRouter();

        await service.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);

        var (factorySystemMessage, _) = new PromptFactory().GetPrompt(AiTaskType.ProductChat, language);
        Assert.Equal(factorySystemMessage, provider.Captured!.SystemMessage);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 2. THE DEFECT: a book-scoped turn's system slot ────────────────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE FIXTURE THAT WOULD HAVE CAUGHT F-1. A book-scoped turn's SYSTEM slot carries the book-aware
    /// rule, literally. Before the fix this assertion returned phase A's book REFUSAL, because
    /// <c>PromptFactory</c> derives the message from the task type alone and a task type cannot say "this
    /// turn carries a book".
    /// </summary>
    [Theory]
    [InlineData("What happens to Miriam in chapter 4?", BookAwareEn)]
    [InlineData("מה קורה למרים בפרק 4?", BookAwareHe)]
    public async Task WithABookId_TheProviderReceives_TheBookAwareSystemMessage_Literally(
        string question, string expected)
    {
        var (service, provider) = ChatOverTheRealRouter(BookReader(Status()));

        await service.AnswerAsync(
            new ProductChatRequest(question, BookId: Guid.NewGuid()), CancellationToken.None);

        Assert.NotNull(provider.Captured);
        Assert.Equal(expected, provider.Captured!.SystemMessage);
    }

    /// <summary>
    /// AND THE TWO RULES ARE NEVER BOTH IN THE PROMPT. This is the finding stated as a property rather
    /// than as a string comparison: the refusal and the grounding rule are contradictory, and a prompt
    /// carrying both is resolved by the model instead of by the author - <c>ProductChatPrompt</c>'s own
    /// recorded g3 lesson. Asserted across the WHOLE composed payload (system slot + instruction), not
    /// just the slot, because g1 observed the refusal winning from the slot while the grounding rule sat
    /// in the user message.
    /// </summary>
    [Theory]
    [InlineData("What happens to Miriam in chapter 4?", "is not available yet and is coming",
                "answer it from the BOOK section below")]
    [InlineData("מה קורה למרים בפרק 4?", "מענה על שאלות לגבי ספר מסוים עדיין אינו",
                "ענה עליה מתוך מקטע הספר שמופיע למטה")]
    public async Task WithABookId_TheRefusalAndTheGroundingRule_AreNeverBothInThePayload(
        string question, string refusalFragment, string groundingFragment)
    {
        var (service, provider) = ChatOverTheRealRouter(BookReader(Status()));

        await service.AnswerAsync(
            new ProductChatRequest(question, BookId: Guid.NewGuid()), CancellationToken.None);

        var payload = provider.Captured!.SystemMessage + "\n" + provider.Captured.Instruction;

        Assert.DoesNotContain(refusalFragment, payload, StringComparison.Ordinal);
        Assert.Contains(groundingFragment, payload, StringComparison.Ordinal);

        // VACUITY GUARD: the refusal fragment IS reachable on this exact code path, so its absence above
        // is the swap and not a fragment that never appears in any composed payload.
        var (bookless, booklessProvider) = ChatOverTheRealRouter();
        await bookless.AnswerAsync(new ProductChatRequest(question), CancellationToken.None);
        Assert.Contains(
            refusalFragment,
            booklessProvider.Captured!.SystemMessage + "\n" + booklessProvider.Captured.Instruction,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// EXACTLY ONE RULE REACHES THE MODEL, and the strongest available statement of that is that exactly
    /// one STRING exists: the composed instruction OPENS with the very system message the provider was
    /// handed. Phase A restates the rule at the head of the user message on purpose (Ollama truncates
    /// from the START), so the two copies are not redundancy to remove - they are two copies that must
    /// never be two different rules. Checked in both modes and both languages.
    /// </summary>
    [Theory]
    [InlineData("How do I export my book?", false)]
    [InlineData("איך מייצאים את הספר שלי?", false)]
    [InlineData("What happens to Miriam in chapter 4?", true)]
    [InlineData("מה קורה למרים בפרק 4?", true)]
    public async Task TheInstruction_OpensWithTheExactSystemMessage_TheProviderWasHanded(
        string question, bool withBook)
    {
        var (service, provider) = ChatOverTheRealRouter(withBook ? BookReader(Status()) : null);

        await service.AnswerAsync(
            new ProductChatRequest(question, BookId: withBook ? Guid.NewGuid() : (Guid?)null),
            CancellationToken.None);

        Assert.StartsWith(provider.Captured!.SystemMessage, provider.Captured.Instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RETRIEVAL'S OWN UNCERTAINTY REACHES THE PROVIDER (f2; c1 watch-list item 2). When one number
    /// names two real chapters of the book, the selector grounds both and records that it could not
    /// decide; g1 confirmed the model does not merge them into a false claim - it answers one and never
    /// says it chose. Everything needed to say so existed in the data and stopped at the prompt boundary,
    /// so this asserts on the string a provider is handed rather than on the composer that builds it, for
    /// the same reason the tests above do: what the composer produces and what the provider receives are
    /// two different questions, and F-1 was the second one.
    /// </summary>
    /// <remarks>THE EXPECTED NOTE IS PER-QUESTION, NOT PER-FIXTURE (be-c04). The note is written in the
    /// ANSWER's language, which this test reaches the honest way: the Hebrew row's question is Hebrew, so
    /// <c>ChatLanguage.Detect</c> inside the service picks Hebrew and the provider is handed a Hebrew note.
    /// A single English expectation across both rows would have passed only while the note was English.
    /// </remarks>
    [Theory]
    [InlineData("What happens in chapter 5?",
        "Note: 2 chapters of this book are named chapter 5, and the briefs below cover 2 of them " +
        "(chapters 5 and 6); answer from those, say which chapters you are describing and that other " +
        "chapters share the name, and offer to narrow to one.")]
    [InlineData("מה קורה בפרק 5?",
        "Note: 2 פרקים בספר הזה נקראים פרק 5, והתקצירים שלמטה מכסים 2 מהם (פרקים 5 ו-6); ענה מתוכם, " +
        "אמור על אילו פרקים אתה מדבר ושיש בספר פרקים נוספים באותו שם, והצע לצמצם לפרק אחד.")]
    public async Task WithAnAmbiguousChapterNumber_TheNote_ReachesTheProvidersInstruction(
        string question, string expectedNote)
    {
        var keys = new BookArtifactSelector.BookQuestionKeys(
            new[] { 4, 5 }, Array.Empty<string>(), Array.Empty<string>(), true, new[] { 4, 5 })
        {
            AmbiguousChapterNumbers = new[]
            {
                new BookArtifactSelector.ChapterReferenceAmbiguity("chapter 5", new[] { 4, 5 }, 5)
            }
        };

        var blocks = new[]
        {
            Status(),
            new BookArtifactBlock(BookArtifactKind.ChapterText, new[] { "chapter-text:4" },
                                  "=== ARTIFACT ref=chapter-text:4 ===\nfour", 1),
            new BookArtifactBlock(BookArtifactKind.ChapterText, new[] { "chapter-text:5" },
                                  "=== ARTIFACT ref=chapter-text:5 ===\nfive", 1)
        };

        var (service, provider) = ChatOverTheRealRouter(BookReader(keys, blocks));
        await service.AnswerAsync(
            new ProductChatRequest(question, BookId: Guid.NewGuid()), CancellationToken.None);

        Assert.Contains(expectedNote, provider.Captured!.Instruction, StringComparison.Ordinal);

        // VACUITY GUARD: the same turn with NO recorded ambiguity carries no note, so an unambiguous
        // chapter question is unchanged and does not acquire a hedge.
        var (plain, plainProvider) = ChatOverTheRealRouter(BookReader(blocks));
        await plain.AnswerAsync(
            new ProductChatRequest(question, BookId: Guid.NewGuid()), CancellationToken.None);

        Assert.DoesNotContain("Note: ", plainProvider.Captured!.Instruction, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // ─── 3. THE SEAM ITSELF: a nullable only one caller sets is a seam that can rot ─────────────
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// FOR EVERY OTHER TASK TYPE THE OVERRIDE IS INERT. <c>AiRequest.SystemMessageOverride</c> is set by
    /// exactly one caller, and the property that makes that safe - null means the factory decides,
    /// unchanged - is asserted here over EVERY value of <see cref="AiTaskType"/> rather than over the
    /// handful this feature touches, so a task added later inherits the guarantee instead of needing a
    /// new test.
    /// </summary>
    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public async Task ANullOverride_LeavesEveryTaskTypesSystemMessage_ExactlyAsTheFactoryBuiltIt(string language)
    {
        var factory = new PromptFactory();
        var provider = new CapturingProvider();
        var router = new AiRouter(
            Options.Create(new AiOptions { DefaultProvider = "Fake", DefaultModel = "m" }),
            factory,
            new Dictionary<string, IAiAnalysisProvider> { ["Fake"] = provider });

        var taskTypes = Enum.GetValues<AiTaskType>();
        Assert.NotEmpty(taskTypes);   // VACUITY GUARD: the loop below has something to iterate

        foreach (var taskType in taskTypes)
        {
            await router.CompleteAsync(new AiRequest
            {
                InputText = "text", Instruction = "instruction", TaskType = taskType, Language = language
            });

            var (expected, _) = factory.GetPrompt(taskType, language);
            Assert.Equal(expected, provider.Captured!.SystemMessage);
        }
    }

    /// <summary>
    /// And a SET override wins, for every task type. Stated so the seam's one behaviour is pinned in both
    /// directions: the assertion above would also pass against a router that ignored the property
    /// entirely, which is the shape of a seam that has quietly rotted.
    /// </summary>
    [Fact]
    public async Task ASetOverride_ReplacesTheFactorysSystemMessage_ForEveryTaskType()
    {
        const string sentinel = "SYSTEM MESSAGE SUPPLIED BY THE CALLER";

        var factory = new PromptFactory();
        var provider = new CapturingProvider();
        var router = new AiRouter(
            Options.Create(new AiOptions { DefaultProvider = "Fake", DefaultModel = "m" }),
            factory,
            new Dictionary<string, IAiAnalysisProvider> { ["Fake"] = provider });

        foreach (var taskType in Enum.GetValues<AiTaskType>())
        {
            await router.CompleteAsync(new AiRequest
            {
                InputText = "text", Instruction = "instruction", TaskType = taskType, Language = "he",
                SystemMessageOverride = sentinel
            });

            Assert.Equal(sentinel, provider.Captured!.SystemMessage);
        }
    }

    /// <summary>
    /// THE ONLY PRODUCTION CALLER IS THE ONE THAT HAS THE FACT. A nullable that exactly one caller sets
    /// rots when a second caller starts setting it from a hand-composed string, at which point "the
    /// grounding wording has one home" quietly stops being true. This scans the shipped source for
    /// assignments and pins the roster, so adding one is a deliberate act with a test to update rather
    /// than an unnoticed one.
    /// </summary>
    [Fact]
    public void OnlyProductChatService_SetsTheSystemMessageOverride()
    {
        var servicesRoot = System.IO.Path.Combine(ApiProjectDirectory(), "Services");
        Assert.True(System.IO.Directory.Exists(servicesRoot), $"expected the API's Services directory at {servicesRoot}");

        var setters = System.IO.Directory
            .EnumerateFiles(servicesRoot, "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(path => System.IO.File.ReadAllText(path).Contains("SystemMessageOverride =", StringComparison.Ordinal))
            .Select(path => System.IO.Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "ProductChatService.cs" }, setters);
    }

    /// <summary>
    /// The API project directory, anchored on the SHIPPED guides directory
    /// (<c>&lt;api&gt;/Content/guides</c>) that <c>ProductChatCorpusTests.RealGuidesDirectory</c> already
    /// locates. Anchoring on a known file rather than on a directory NAME matters: the build output also
    /// contains a folder called <c>Pagedraft.Api</c>, so a name walk finds the copy in <c>bin</c>.
    /// </summary>
    private static string ApiProjectDirectory()
        => System.IO.Directory.GetParent(ProductChatCorpusTests.RealGuidesDirectory())!.Parent!.FullName;
}
