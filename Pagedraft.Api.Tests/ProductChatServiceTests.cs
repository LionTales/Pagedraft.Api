using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The chat endpoint's server half (chatbot phase A, c1): what reaches the router, and what happens
/// when something is missing.
///
/// <para>NO MODEL, NO GPU, NO NETWORK. <see cref="IAiRouter"/> is stubbed, which is the whole reason
/// the grounding contract is worth pinning here: the property "the prompt carries the grounding rule
/// and the selected guide text" is a property of the COMPOSITION, and asserting it against a real
/// model would measure the model instead. The model's behaviour under this prompt is g1's job.</para>
///
/// <para>The FAIL-SAFE tests are the centre of this file. A retrieval failure that produced a
/// confident answer from the model's own knowledge of the product is the exact failure this plan
/// exists to prevent, so each refusal path is asserted on three things: the honest answer, the
/// <c>isGrounded=false</c> flag the client branches on, and the machine-readable fault reason. Plus a
/// fourth that is easy to forget: that it LOGGED why, because a catch that keeps the endpoint
/// non-throwing and says nothing ships its failures invisibly.</para>
/// </summary>
public class ProductChatServiceTests : IDisposable
{
    // ─── Fixtures ───────────────────────────────────────────────────────────────────────────────

    private const string Answer = "Export produces a DOCX file from what is saved in your chapters.";

    /// <summary>
    /// The in-memory <see cref="AppDbContext"/> behind <see cref="ConversationStore"/>, tracked so
    /// <see cref="Dispose"/> can release it. xUnit gives each test method its own instance of this
    /// class, so at most one context is ever created per instance, and the null check covers every
    /// test that never calls <see cref="ConversationStore"/> at all.
    /// </summary>
    private AppDbContext? _conversationDbContext;

    public void Dispose() => _conversationDbContext?.Dispose();

    private static ProductChatService Service(
        Mock<IAiRouter> router, out CapturingLogger<ProductChatService> logger, string? guidesDirectory)
        => ProductChatBudgetTests.Service(router, out logger, guidesDirectory, aiOptions: null);

    /// <summary>A router that always answers, and records the request it was handed.</summary>
    private static Mock<IAiRouter> AnsweringRouter(List<AiRequest> captured, string content = Answer)
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .Callback<AiRequest, CancellationToken>((req, _) => captured.Add(req))
              .ReturnsAsync(new AiResponse { Content = content, Provider = "test", Model = "test-model" });
        return router;
    }

    private static ProductChatRequest Ask(string question, IReadOnlyList<ProductChatTurnDto>? history = null)
        => new(question, history);

    // ─── Routing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The request goes through <see cref="IAiRouter"/> tagged <see cref="AiTaskType.ProductChat"/> and
    /// NOT <see cref="AiTaskType.GenericChat"/> - d1 item 4's whole point, since GenericChat is a live
    /// route for chapter QA/Custom/Translation whose tuning must not be able to move this feature.
    /// The tier is left unstamped (null = Fast): phase A chat is app-level, there is no book id, and
    /// ProductChat is deliberately outside the tier allowlist.
    /// </summary>
    [Fact]
    public async Task TheRequest_IsRoutedAsProductChat_OnTheFastRung()
    {
        var captured = new List<AiRequest>();
        var svc = Service(AnsweringRouter(captured), out _);

        await svc.AnswerAsync(Ask("How do I export my book?"), CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(AiTaskType.ProductChat, request.TaskType);
        Assert.Null(request.Tier);
        Assert.Equal("en", request.Language);
        Assert.Equal("How do I export my book?", request.InputText);
        Assert.False(AiTierPolicy.IsTiered(AiTaskType.ProductChat),
            "ProductChat must stay outside AiTierPolicy.TieredTasks: the tier is per BOOK and this feature is " +
            "app-level, so there is no book id to resolve one from.");
        Assert.DoesNotContain(AiTaskType.ProductChat, AiTierPolicy.UserFacingTasks);
    }

    /// <summary>
    /// THROUGH THE REAL ROUTER, to the provider seam. The composed instruction must arrive VERBATIM:
    /// <c>AiRouter.ShouldUseUnifiedInstructionVerbatim</c> is private, so the property is asserted where
    /// it is observable, on what a provider actually receives. Without the allowlist entry the legacy
    /// heading/numbered-list pipeline instruction is appended after the grounding block and contradicts
    /// it, which is invisible from the endpoint.
    /// </summary>
    [Fact]
    public async Task TheComposedInstruction_ReachesTheProviderVerbatim_WithTheGroundingSystemMessage()
    {
        var opt = new AiOptions
        {
            DefaultProvider = "Ollama",
            DefaultModel = "fallback-model",
            FeatureModels = new Dictionary<string, FeatureModelOptions>
            {
                ["ProductChat"] = new FeatureModelOptions { Provider = "Ollama", Model = "product-chat-model" }
            }
        };

        ResolvedAiRequest? resolved = null;
        var provider = new Mock<IAiAnalysisProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ResolvedAiRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ResolvedAiRequest, CancellationToken>((r, _) => resolved = r)
                .ReturnsAsync(new AiResponse { Content = "ok", Provider = "Ollama", Model = "product-chat-model" });

        var router = new AiRouter(
            Microsoft.Extensions.Options.Options.Create(opt),
            new PromptFactory(),
            new Dictionary<string, IAiAnalysisProvider> { ["Ollama"] = provider.Object });

        await router.CompleteAsync(new AiRequest
        {
            InputText = "How do I export?",
            Instruction = "COMPOSED-GROUNDING-BLOCK",
            TaskType = AiTaskType.ProductChat,
            Language = "en"
        });

        Assert.NotNull(resolved);
        Assert.Equal("COMPOSED-GROUNDING-BLOCK", resolved!.Instruction);   // verbatim, nothing appended
        Assert.Equal(ProductChatPrompt.SystemMessage("en"), resolved.SystemMessage);
        Assert.Equal("product-chat-model", resolved.Selection.Model);      // its OWN key, not the default rung
        Assert.False(resolved.JsonMode);
    }

    // ─── Prompt composition ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE GROUNDING RULE AND THE SELECTED CONTENT BOTH REACH THE PROMPT. Asserted on the real corpus,
    /// so "the selected content" means real guide prose and not a fixture string that would still be
    /// there if retrieval broke.
    /// </summary>
    [Fact]
    public async Task ThePrompt_CarriesTheGroundingRule_TheRefusalRule_AndTheSelectedGuideText()
    {
        var captured = new List<AiRequest>();
        var svc = Service(AnsweringRouter(captured), out _);

        await svc.AnswerAsync(Ask("How do I export my book to Word?"), CancellationToken.None);

        var instruction = Assert.Single(captured).Instruction!;

        // (1) The grounding rule, verbatim from its single owner.
        Assert.Contains(ProductChatPrompt.SystemMessage("en"), instruction, StringComparison.Ordinal);
        Assert.Contains("Answer ONLY from the guide content provided below", instruction, StringComparison.Ordinal);
        // (2) The coverage refusal and (3) the book-specific refusal, worded distinctly (d1 item 5).
        Assert.Contains("If the guides do not address the question", instruction, StringComparison.Ordinal);
        Assert.Contains(
            "say that you can only see a book while it is open", instruction, StringComparison.Ordinal);
        // (2b) THE g2/g3 HALT RULES, by their own words rather than by "some refusal rule is present":
        // a refusal may name another topic the guides DO cover WHEN one is relevant, may fall back to
        // a bare refusal when none is, and may never characterize what they say about what they do not
        // cover. All three halves are asserted, because forbidding only the last would let a fix that
        // also killed the pivot pass.
        Assert.Contains("If another topic they DO cover is genuinely relevant, name it and its guide id",
            instruction, StringComparison.Ordinal);
        Assert.Contains("if none is, a bare refusal is the whole answer", instruction, StringComparison.Ordinal);
        Assert.Contains("do not describe what the guides say about a topic they do not address",
            instruction, StringComparison.Ordinal);
        Assert.Contains("not as a fact about the product", instruction, StringComparison.Ordinal);
        // (4) The selected guide's real text, and its citable id.
        Assert.Contains("=== GUIDE id=export lang=en ===", instruction, StringComparison.Ordinal);
        Assert.Contains("Export produces a DOCX file", instruction, StringComparison.Ordinal);
        // A section heading from deep inside the guide, so this asserts the WHOLE file travels rather
        // than only its opening. be-c06 renamed it (from "Save before you export", which blamed the
        // author for the scene-split staleness defect) and kept the token "export" in it deliberately:
        // GuideSelector scores headings only, and this is the one heading that carries that token.
        Assert.Contains("Which version of your text the export writes", instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE g2 HALT GUARD, IN BOTH LANGUAGES. g2's `b7` run1 refused correctly and then asserted "the
    /// only shortcuts mentioned in the text are related to saving chapters or dismissing cards", about
    /// a corpus with zero occurrences of shortcut/keyboard/ctrl/hotkey and their Hebrew equivalents.
    /// It broke no rule that existed: it invented no setting, button or screen, and it did name what
    /// the guides cover. It made a claim about the CORPUS instead. Both strings must forbid that, and
    /// must forbid the same family's Hebrew variant (g2 F7: asserting the PRODUCT does not support
    /// EPUB, where the guides only fail to mention it).
    ///
    /// <para>The pivot that measured CLEAN is asserted alongside, because a guard that also silenced
    /// "here is what the guides DO cover" would be a regression on 33 of 33 adjacent refusals, and a
    /// one-sided assertion would not notice. The pivot is now CONDITIONAL (see
    /// <see cref="BothGroundingStrings_ConditionThePivotOnACoveredTopic_AndAllowABareRefusal"/>, the
    /// g3 fix), so what is asserted here is only that it survives, not that it is unconditional.</para>
    /// </summary>
    [Fact]
    public void BothGroundingStrings_ForbidCharacterizingWhatTheGuidesSayAboutAnAbsentTopic()
    {
        var en = ProductChatPrompt.SystemMessage("en");
        var he = ProductChatPrompt.SystemMessage("he");

        Assert.True(
            en.Contains("do not describe what the guides say about a topic they do not address", StringComparison.Ordinal),
            "GroundingEn no longer forbids characterizing what the guides say about a topic they do NOT address. " +
            "That is the exact shape that produced g2's HALT (b7 run1 invented a class of keyboard shortcut while " +
            "refusing); the older rules against naming an absent setting/button/screen and against a bare refusal " +
            "do not reach it, because the sentence was a claim about the corpus, not about the product.");
        Assert.True(
            he.Contains("ואל תתאר מה המדריכים אומרים על נושא שאינם עוסקים בו", StringComparison.Ordinal),
            "GroundingHe no longer forbids characterizing what the guides say about a topic they do NOT address. " +
            "The HALT was observed in English, but the rule has to hold in the app's DEFAULT language too, and " +
            "Hebrew was the language that already produced the same family of over-claim (g2 F7).");

        // Non-COVERAGE, not non-SUPPORT. English got this right unprompted (b1); Hebrew did not (d4).
        Assert.True(
            en.Contains("not as a fact about the product", StringComparison.Ordinal),
            "GroundingEn no longer requires a gap to be stated as a gap in the GUIDES rather than as a fact about " +
            "the product. Without it an answer can claim PageDraft lacks a feature the guides merely never mention.");
        Assert.True(
            he.Contains("ולא כעובדה על המוצר", StringComparison.Ordinal),
            "GroundingHe no longer requires a gap to be stated as a gap in the GUIDES rather than as a fact about " +
            "the product. This is the clause that targets g2 F7, where 2 of 3 Hebrew runs answered that PageDraft " +
            "does not SUPPORT EPUB export, which no guide says.");

        // The pivot that works, and must keep working: name ANOTHER topic that IS covered.
        Assert.True(
            en.Contains("name it and its guide id", StringComparison.Ordinal),
            "GroundingEn no longer tells the model to name a covered topic on a refusal. g2 measured 33 of 33 and " +
            "g3 39 of 39 adjacent questions refused WITH that pivot; suppressing it would trade the HALT for a " +
            "worse answer.");
        Assert.True(
            he.Contains("ציין אותו לפי המזהה שלו", StringComparison.Ordinal),
            "GroundingHe no longer tells the model to name a covered topic on a refusal.");

        // d1 item 5's two refusal reasons stay DISTINCTLY worded. Both runs confirmed this held. g3
        // rewrote WHAT the book refusal says (its old sentence had gone false and was reaching users);
        // that the two reasons are still worded distinctly is the property, and it is unchanged.
        Assert.Contains("say that you can only see a book while it is open", en, StringComparison.Ordinal);
        Assert.Contains("אני יכול לראות ספר רק כשהוא פתוח", he, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE g3 HALT GUARD: THE PIVOT IS CONDITIONAL, AND A BARE REFUSAL IS A COMPLETE ANSWER. Adding
    /// the prohibition guarded above did not close the class. g3 still saw 2 of 39 adjacent runs
    /// fabricate, one of them now quoting "Cmd/Ctrl+S" as something the guides describe. The cause was
    /// a COLLISION between two consecutive sentences, not a missing rule: the refusal sentence demanded
    /// UNCONDITIONALLY that a refusal name what the guides DO cover, and on the one question shape
    /// "which X does the product have?" against a corpus with no X at all, every honest referent is
    /// absent, so the only way to obey was to report what the guides supposedly say about X, which the
    /// prohibition forbids. The model resolved the conflict toward the older, more emphatic clause.
    ///
    /// <para>So the demand is SCOPED, not supplemented: the pivot is conditioned on the guides covering
    /// another relevant topic, and a bare refusal is stated to be the whole answer when they do not.
    /// Both halves are asserted here, and the unconditional phrasing is separately pinned OUT, because
    /// re-introducing it anywhere in the string would restore the collision even with the conditional
    /// still present.</para>
    /// </summary>
    [Fact]
    public void BothGroundingStrings_ConditionThePivotOnACoveredTopic_AndAllowABareRefusal()
    {
        var en = ProductChatPrompt.SystemMessage("en");
        var he = ProductChatPrompt.SystemMessage("he");

        // (1) The pivot is CONDITIONAL on there being another covered topic that is relevant.
        Assert.True(
            en.Contains("If another topic they DO cover is genuinely relevant, name it and its guide id",
                StringComparison.Ordinal),
            "GroundingEn no longer CONDITIONS the 'name what the guides DO cover' pivot on there actually being a " +
            "relevant covered topic. Unconditional, it collides with the ban on characterizing an absent topic: on " +
            "'which X does the product have?' against a corpus with no X, the only way to obey it is to invent what " +
            "the guides say about X, which is exactly the g3 HALT (b7 run5 quoted 'Cmd/Ctrl+S').");
        Assert.True(
            he.Contains("אם יש נושא אחר שהם כן מכסים ורלוונטי לשאלה, ציין אותו לפי המזהה שלו", StringComparison.Ordinal),
            "GroundingHe no longer CONDITIONS the pivot on there actually being a relevant covered topic. The HALT " +
            "was observed in English, but the collision is in the wording, not the language, and Hebrew is the " +
            "app's default.");

        // (2) And a bare refusal is explicitly a COMPLETE answer when no covered topic is relevant.
        Assert.True(
            en.Contains("if none is, a bare refusal is the whole answer", StringComparison.Ordinal),
            "GroundingEn no longer states that a bare refusal is a complete answer. Without that, conditioning the " +
            "pivot is not enough: the model is left with a demand it cannot satisfy honestly and no sanctioned way " +
            "to stop, which is how both g3 fabrications were produced.");
        Assert.True(
            he.Contains("אם אין, די בסירוב בלבד", StringComparison.Ordinal),
            "GroundingHe no longer states that a bare refusal is a complete answer when no covered topic is relevant.");

        // (3) The unconditional demand is pinned OUT. A conditional clause added ALONGSIDE the old
        // sentence would satisfy (1) and (2) and still carry the collision that caused the HALT.
        Assert.False(
            en.Contains("say so plainly and then name what the provided guides DO cover", StringComparison.Ordinal),
            "GroundingEn has gone back to demanding a covered-topic inventory on EVERY refusal. That is the g3 " +
            "collision, whether or not a scoped clause also appears.");
        Assert.False(
            he.Contains("ואז ציין מה כן מכוסה במדריכים שניתנו", StringComparison.Ordinal),
            "GroundingHe has gone back to demanding a covered-topic inventory on EVERY refusal.");

        // NOTE ON WHAT IS *NOT* ASSERTED HERE. That the bare refusal stays a PERMISSION rather than a
        // mandate is carried by (1): the pivot instruction has to be present and reachable whenever a
        // relevant covered topic exists. g3 measured that pivot working (b1 refuses EPUB and then
        // correctly quotes what export DOES produce), and a bare-refusal regression is a real cost even
        // though it is not a HALT, but it is a MODEL BEHAVIOR and only g4's live run can measure it.
    }

    /// <summary>
    /// THE HEBREW BOOK-SPECIFIC REFUSAL IS THE SENTENCE TO SAY, NOT THE ORDER TO FOLLOW (g1 F4, g2 F4).
    /// Given as an imperative, the model read the instruction back verbatim, imperative verb included:
    /// 2 of 18 Hebrew answers in g1, 6 of 6 runs of that question shape in g2. Both measurements
    /// recommended a wording pass. The refusal is still correct either way, so nothing but the wording
    /// catches this, and nothing but a test keeps it caught.
    ///
    /// <para>g3 REWROTE WHAT THE QUOTED SENTENCE SAYS AND DELIBERATELY KEPT THE CONSTRUCTION. The old
    /// sentence claimed the book feature "is not available yet and is coming", which had been false since
    /// phase B and which g3 measured reaching a real user on 5 of 102 turns. What this test pins is
    /// therefore the SHAPE - a finished first-person sentence handed over inside quotes - plus the fact
    /// that the sentence inside them is the one the deterministic code path already gives, so an echo
    /// costs the author nothing.</para>
    /// </summary>
    [Fact]
    public void TheHebrewBookSpecificRefusal_IsPhrasedAsASentenceToSay_NotAsAnImperative()
    {
        var he = ProductChatPrompt.SystemMessage("he");

        Assert.True(
            he.Contains("ענה בגוף ראשון במשמעות הזו: '", StringComparison.Ordinal),
            "GroundingHe no longer hands the book-specific refusal over as a finished first-person sentence. When " +
            "it was phrased as an order to the model ('and offer help with general questions'), the model echoed " +
            "the order into the user-visible answer in 6 of 6 g2 runs of that question shape.");
        Assert.True(
            he.Contains("אני יכול לראות ספר רק כשהוא פתוח.", StringComparison.Ordinal),
            "GroundingHe's quoted sentence is no longer the one ProductChatService.OpenTheBookHe returns from the " +
            "deterministic path. Two paths answer this question shape and they must not tell the author two " +
            "different stories about the product.");
        Assert.False(
            he.Contains("והצע עזרה", StringComparison.Ordinal),
            "GroundingHe has gone back to the imperative 'והצע עזרה'. That is the exact string g1 and g2 both " +
            "observed leaking verbatim into Hebrew answers.");
        Assert.False(
            he.Contains("אמור שמענה", StringComparison.Ordinal),
            "GroundingHe has gone back to instructing the model to SAY that answering is unavailable, rather than " +
            "giving it the sentence. The echo followed that framing, not the specific verb.");
    }

    /// <summary>
    /// SHOW'S VOICE IS REGISTER, AND IT SITS AHEAD OF THE RULES (phase A.2, c2). The owner asked for a
    /// less robotic assistant; the gate history says the way that request breaks the feature is by
    /// buying warmth with a rule change. So what this pins is that the voice arrived as a PERSONA
    /// sentence and nothing else, and three properties of where it sits:
    ///
    /// <para>(1) It is FIRST. The local provider concatenates system + instruction + input and Ollama
    /// truncates from the START, so whatever opens the string is what is lost first when a Hebrew
    /// prompt runs at 98% of the window (g4's measured peak). Persona is the right thing to lose;
    /// "answer only from the guides" is not. Asserted as an index comparison rather than left to the
    /// order the constant happens to be written in.</para>
    ///
    /// <para>(2) The Hebrew half is DESCRIPTIVE, not imperative. A Hebrew imperative in this string has
    /// leaked verbatim into user-visible answers twice at two different clauses (g1/g2 F4 at the
    /// book-specific refusal, and g4's new <c>e1</c> locus at the gap-framing clause), so the imperative
    /// forms of the same instruction are pinned OUT.</para>
    ///
    /// <para>(3) It carries the assistant's NAME, which the client already renders on every turn
    /// (f1's scope fence deferred the server-side half to this todo).</para>
    ///
    /// <para>What is NOT re-asserted here: that the grounding contract, the two refusal rules and
    /// final-r02's scoped instruction 1 survived. Three tests above already own those by their own
    /// words, and duplicating them here would make a future edit look guarded twice while actually
    /// being guarded once.</para>
    /// </summary>
    [Fact]
    public void BothGroundingStrings_OpenWithShowsVoice_AheadOfTheGroundingRule()
    {
        var en = ProductChatPrompt.SystemMessage("en");
        var he = ProductChatPrompt.SystemMessage("he");

        const string enPersona = "You are Show, the PageDraft product assistant. You write in the first person, " +
                                 "warmly and briefly, and you open each reply from what was actually asked.";
        const string hePersona = "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת.";

        Assert.Contains(enPersona, en, StringComparison.Ordinal);
        Assert.Contains(hePersona, he, StringComparison.Ordinal);

        // (1) Persona first, grounding second, so truncation from the start spends the persona.
        Assert.True(
            en.IndexOf(enPersona, StringComparison.Ordinal)
                < en.IndexOf("Answer ONLY from the guide content provided below", StringComparison.Ordinal),
            "GroundingEn's persona sentence no longer precedes the grounding rule. Ollama truncates from the " +
            "START of the concatenated prompt, so the rule must never be the thing that opens the string.");
        Assert.True(
            he.IndexOf(hePersona, StringComparison.Ordinal)
                < he.IndexOf("ענה אך ורק מתוך תוכן המדריכים שמופיע למטה", StringComparison.Ordinal),
            "GroundingHe's persona sentence no longer precedes the grounding rule, and Hebrew is the half that " +
            "actually runs near the window ceiling (g4 peak 13,799 of 14,080).");

        // (2) The Hebrew persona stays a self-description. The imperative forms are the F4 shape.
        Assert.False(
            he.Contains("דבר בגוף ראשון", StringComparison.Ordinal) || he.Contains("כתוב בגוף ראשון", StringComparison.Ordinal),
            "GroundingHe now ORDERS the model to write in the first person instead of describing that it does. " +
            "Every Hebrew instruction echo this feature has measured (g1/g2 F4, g4's e1) was an imperative read " +
            "back to the user as prose.");

        // (3) The name the client already puts on every assistant turn.
        Assert.Contains("Show", en, StringComparison.Ordinal);
        Assert.Contains("שואו", he, StringComparison.Ordinal);
    }

    /// <summary>
    /// NO TERMINOLOGY MAPPING (d1 item 6). The guides still say "book summary" where Wave 3's
    /// reconciled vocabulary says "book briefs"; phase A ships against the guides exactly as they
    /// read, because an answer that says one while citing the other is the citation/text mismatch the
    /// grounding contract exists to prevent. Pinned so a well-meaning future edit cannot quietly add
    /// the substitution instruction the copy-edit is supposed to make unnecessary.
    /// </summary>
    [Fact]
    public void TheSystemPrompt_CarriesNoVocabularySubstitutionInstruction()
    {
        foreach (var language in new[] { "he", "en" })
        {
            var prompt = ProductChatPrompt.SystemMessage(language);
            Assert.DoesNotContain("book brief", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("תקצירי הספר", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("instead of", prompt, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>No em-dash in anything a user reads: a workspace-wide rule, and the model echoes the
    /// punctuation in its own frame back into the answer.</summary>
    [Fact]
    public async Task NoUserFacingStringContainsAnEmDash()
    {
        var strings = new List<string> { ProductChatPrompt.SystemMessage("he"), ProductChatPrompt.SystemMessage("en") };

        foreach (var fault in new[] { ProductChatFaults.GuidesUnavailable, ProductChatFaults.ModelUnavailable })
        {
            var svc = Service(ThrowingRouter(), out _,
                guidesDirectory: fault == ProductChatFaults.GuidesUnavailable ? MissingDirectory() : null);
            strings.Add((await svc.AnswerAsync(Ask("שאלה"), CancellationToken.None)).Answer);
        }

        Assert.Equal(4, strings.Count);   // the population, before the sweep
        Assert.All(strings, s => Assert.DoesNotContain("—", s, StringComparison.Ordinal));
    }

    /// <summary>
    /// A HEBREW QUESTION IS ANSWERED IN HEBREW FROM HEBREW GUIDES (d1 item 3), and the language is
    /// reported on the wire so the client can set <c>dir</c> without re-detecting.
    /// </summary>
    [Fact]
    public async Task AHebrewQuestion_SelectsHebrewGuides_AndReportsHebrew()
    {
        var captured = new List<AiRequest>();
        var svc = Service(AnsweringRouter(captured, "הייצוא מפיק קובץ DOCX."), out _);

        var result = await svc.AnswerAsync(Ask("איך מייצאים את הספר?"), CancellationToken.None);

        Assert.Equal("he", result.Language);
        var instruction = Assert.Single(captured).Instruction!;
        Assert.Contains("=== GUIDE id=export lang=he ===", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("lang=en", instruction, StringComparison.Ordinal);
        Assert.Contains("ענה אך ורק מתוך תוכן המדריכים", instruction, StringComparison.Ordinal);
    }

    /// <summary>The client's language hint cannot override the question's own script.</summary>
    [Theory]
    [InlineData("How do I export?", "he", "en")]
    [InlineData("איך מייצאים?", "en", "he")]
    [InlineData("2026?", "en", "en")]
    [InlineData("2026?", null, "he")]   // no script signal and no hint: Hebrew is the app default
    public void TheAnswerLanguage_IsTheQuestionsOwnScript_NotTheClientsClaim(
        string question, string? hint, string expected)
        => Assert.Equal(expected, ChatLanguage.Detect(question, hint));

    /// <summary>
    /// A MIXED-SCRIPT QUESTION RESOLVES HEBREW, AND THAT IS THE DECISION, NOT AN OVERSIGHT (review
    /// finding 18). The guides name every editing pass in Hebrew, so an English question quoting one
    /// (ספרותי, עריכת שורה, הגהה) is answered entirely in Hebrew. Single-letter decisiveness was
    /// reviewed and KEPT: a majority-of-letters rule would flip the far more common input, a short
    /// Hebrew question carrying an English product noun ("PageDraft", "DOCX"), into English, and a
    /// native question answered in the wrong language does not announce itself the way one quoted
    /// term does. Both directions are asserted, because a change that "fixed" the first row would
    /// break the second and a one-sided test would not notice.
    /// </summary>
    [Theory]
    [InlineData("what does the ספרותי pass do?", "he")]        // English question, one quoted Hebrew term
    [InlineData("Does הגהה run on the whole chapter?", "he")]  // ... and again, with more Latin around it
    [InlineData("איך מייצאים ל-DOCX ב-PageDraft?", "he")]      // Hebrew question, Latin product nouns
    [InlineData("How do I export to DOCX?", "en")]             // pure Latin still resolves English
    public void AMixedScriptQuestion_ResolvesHebrew_BecauseOneHebrewLetterIsDeliberatelyDecisive(
        string question, string expected)
        => Assert.Equal(expected, ChatLanguage.Detect(question, clientHint: null));

    // ─── History cap ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CLIENT IS NOT TRUSTED TO BOUND ITS HISTORY (d1's finding for c1). The retrieval budget math
    /// assumes a bounded history; an unbounded client list would overrun the 16k window silently
    /// rather than fail, and the answer would come back ungrounded while still looking grounded.
    /// </summary>
    [Fact]
    public async Task OnlyTheLastNTurns_AreForwarded_NoMatterHowManyTheClientSends()
    {
        var history = Enumerable.Range(1, 40)
            .Select(i => new ProductChatTurnDto(i % 2 == 0 ? "assistant" : "user", $"turn-{i:00}"))
            .ToList();

        var captured = new List<AiRequest>();
        var svc = Service(AnsweringRouter(captured), out _);

        await svc.AnswerAsync(Ask("How do I export?", history), CancellationToken.None);
        var instruction = Assert.Single(captured).Instruction!;

        Assert.Equal(8, ProductChatService.MaxHistoryTurns);
        // The NEWEST 8 survive ...
        for (var i = 33; i <= 40; i++)
            Assert.Contains($"turn-{i:00}", instruction, StringComparison.Ordinal);
        // ... and nothing older does. Asserted over the whole dropped population, which is non-empty
        // by construction (40 sent, 8 kept).
        var dropped = Enumerable.Range(1, 32).Select(i => $"turn-{i:00}").ToList();
        Assert.Equal(32, dropped.Count);
        Assert.All(dropped, t => Assert.DoesNotContain(t, instruction, StringComparison.Ordinal));
    }

    /// <summary>
    /// The turn COUNT alone does not bound a history: one pasted turn can be larger than the whole
    /// guides corpus, so each turn is truncated too.
    /// </summary>
    [Fact]
    public void AnEnormousSingleTurn_IsTruncated()
    {
        var capped = ProductChatService.CapHistory(new[]
        {
            new ProductChatTurnDto("user", new string('x', 50_000))
        });

        Assert.Equal(ProductChatService.MaxHistoryTurnChars, Assert.Single(capped).Content.Length);
    }

    /// <summary>Blank turns are dropped BEFORE the cap, so padding cannot push real context out.</summary>
    [Fact]
    public void BlankTurns_AreDroppedBeforeTheCapIsApplied()
    {
        var turns = Enumerable.Repeat(new ProductChatTurnDto("user", "   "), 20)
            .Concat(new[] { new ProductChatTurnDto("user", "the real question context") })
            .ToList();

        var capped = ProductChatService.CapHistory(turns);

        Assert.Single(capped);
        Assert.Equal("the real question context", capped[0].Content);
    }

    /// <summary>An unrecognised role degrades to "user" rather than rejecting the whole question.</summary>
    [Fact]
    public void RolesAreMappedLeniently_AssistantIsTheOnlyNonUserValue()
    {
        var capped = ProductChatService.CapHistory(new[]
        {
            new ProductChatTurnDto("ASSISTANT", "a"),
            new ProductChatTurnDto("user", "b"),
            new ProductChatTurnDto("banana", "c"),
            new ProductChatTurnDto(null, "d")
        });

        Assert.Equal(new[] { false, true, true, true }, capped.Select(t => t.IsUser).ToArray());
    }

    [Fact]
    public void ANullOrEmptyHistory_IsNotAnError()
    {
        Assert.Empty(ProductChatService.CapHistory(null));
        Assert.Empty(ProductChatService.CapHistory(Array.Empty<ProductChatTurnDto>()));
    }

    // ─── Citations ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The answer carries the guide ids it USED (d1 item 2), stripped out of the prose the user reads.
    /// </summary>
    [Fact]
    public async Task TheCitedGuideIds_AreReturned_AndRemovedFromTheProse()
    {
        var svc = Service(AnsweringRouter(new List<AiRequest>(), "Export writes a DOCX.\n\nGuides: export"), out _);

        var result = await svc.AnswerAsync(Ask("How do I export my book to Word?"), CancellationToken.None);

        Assert.True(result.IsGrounded);
        Assert.Equal(new[] { "export" }, result.GuideIds);
        Assert.Equal("Export writes a DOCX.", result.Answer);
        Assert.DoesNotContain("Guides:", result.Answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// A citation can only NARROW what the answer was given. A model naming a guide it never saw must
    /// not be able to widen the citation into a false provenance claim.
    /// </summary>
    [Fact]
    public void AHallucinatedGuideId_IsRejected_AndTheSelectionIsReturnedInstead()
    {
        var selected = new[] { Guide("export"), Guide("faq") };

        var (answer, ids) = ProductChatCitations.Extract("Some prose.\n\nGuides: nonexistent-guide", selected);

        Assert.Equal(new[] { "export", "faq" }, ids);
        Assert.Contains("Guides: nonexistent-guide", answer, StringComparison.Ordinal); // prose untouched on a miss
    }

    [Fact]
    public void ACitationNamingSomeOfTheSelection_NarrowsToThatSubset_InSelectionOrder()
    {
        var selected = new[] { Guide("export"), Guide("faq"), Guide("import") };

        var (_, ids) = ProductChatCitations.Extract("Prose.\n**Guides:** import, export", selected);

        Assert.Equal(new[] { "export", "import" }, ids);   // selection order, not the model's
    }

    [Fact]
    public void AnAnswerWithNoCitationLine_FallsBackToEveryGuideItWasGrounded_In()
    {
        var selected = new[] { Guide("export"), Guide("faq") };

        var (answer, ids) = ProductChatCitations.Extract("Just prose, no citation.", selected);

        Assert.Equal("Just prose, no citation.", answer);
        Assert.Equal(new[] { "export", "faq" }, ids);
    }

    /// <summary>An en/he twin pair shares one id, so a mixed-language selection must not list it twice.</summary>
    [Fact]
    public void TheReturnedIds_AreDistinct_AcrossAnEnHeTwinPair()
    {
        var (_, ids) = ProductChatCitations.Extract("Prose.", new[] { Guide("export", "en"), Guide("export", "he") });

        Assert.Equal(new[] { "export" }, ids);
    }

    [Fact]
    public void TheHebrewCitationLabel_IsUnderstoodToo()
    {
        var (answer, ids) = ProductChatCitations.Extract("הייצוא מפיק DOCX.\n\nמדריכים: export", new[] { Guide("export") });

        Assert.Equal(new[] { "export" }, ids);
        Assert.Equal("הייצוא מפיק DOCX.", answer);
    }

    // ─── The fail-safe paths ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A MISSING CORPUS PRODUCES AN HONEST REFUSAL, NOT AN ANSWER. The router must not be called at
    /// all: an ungrounded completion is not something to generate and then discard, because the next
    /// person to touch this code would find it there and be tempted to return it.
    /// </summary>
    [Fact]
    public async Task AMissingCorpus_RefusesHonestly_WithoutEverCallingTheModel()
    {
        var captured = new List<AiRequest>();
        var router = AnsweringRouter(captured);
        var svc = Service(router, out var logger, guidesDirectory: MissingDirectory());

        var result = await svc.AnswerAsync(Ask("How do I export my book?"), CancellationToken.None);

        Assert.False(result.IsGrounded);
        Assert.Equal(ProductChatFaults.GuidesUnavailable, result.FaultReason);
        Assert.Empty(result.GuideIds);
        Assert.Contains("cannot reach the PageDraft guides", result.Answer, StringComparison.Ordinal);
        Assert.Empty(captured);
        router.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // ... and it said WHY, naming the fault (observability, not just non-throwing).
        var warning = Assert.Single(logger.AtLeast(LogLevel.Warning));
        Assert.Contains(ProductChatFaults.GuidesUnavailable, warning, StringComparison.Ordinal);
    }

    /// <summary>The same refusal for a directory that exists and parses to nothing - a different fault
    /// reason, because "the corpus never shipped" and "the corpus is corrupt" need different fixes.</summary>
    [Fact]
    public async Task AnEmptyCorpus_RefusesHonestly_WithItsOwnFaultReason()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pagedraft-guides-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var captured = new List<AiRequest>();
            var svc = Service(AnsweringRouter(captured), out var logger, guidesDirectory: dir);

            var result = await svc.AnswerAsync(Ask("איך מייצאים?"), CancellationToken.None);

            Assert.False(result.IsGrounded);
            Assert.Equal(ProductChatFaults.GuidesEmpty, result.FaultReason);
            Assert.Empty(result.GuideIds);
            Assert.Contains("אינני מצליח להגיע", result.Answer, StringComparison.Ordinal);
            Assert.Empty(captured);
            Assert.Contains(logger.AtLeast(LogLevel.Warning),
                m => m.Contains(ProductChatFaults.GuidesEmpty, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A router that throws is a fail-safe too, with its OWN reason: the guides are fine, the model is
    /// not, and telling the author "I cannot reach the guides" would send them to fix the wrong thing.
    /// Logged at Error, because unlike a missing corpus this is an outage.
    /// </summary>
    [Fact]
    public async Task AnUnreachableModel_FailsSafe_WithItsOwnFaultReason_LoggedAtError()
    {
        var svc = Service(ThrowingRouter(), out var logger);

        var result = await svc.AnswerAsync(Ask("How do I export my book?"), CancellationToken.None);

        Assert.False(result.IsGrounded);
        Assert.Equal(ProductChatFaults.ModelUnavailable, result.FaultReason);
        Assert.Empty(result.GuideIds);
        Assert.Contains("The guides are available", result.Answer, StringComparison.Ordinal);
        Assert.Contains(logger.At(LogLevel.Error),
            m => m.Contains(ProductChatFaults.ModelUnavailable, StringComparison.Ordinal));
    }

    /// <summary>An empty completion is not an answer. It used to be possible for a truncated prompt to
    /// return a one-token fragment on this stack, and a blank one must not be rendered as a reply.</summary>
    [Fact]
    public async Task AnEmptyCompletion_FailsSafe_RatherThanReturningABlankAnswer()
    {
        var svc = Service(AnsweringRouter(new List<AiRequest>(), "   "), out var logger);

        var result = await svc.AnswerAsync(Ask("How do I export my book?"), CancellationToken.None);

        Assert.False(result.IsGrounded);
        Assert.Equal(ProductChatFaults.EmptyAnswer, result.FaultReason);
        Assert.NotEmpty(result.Answer);
        Assert.Contains(logger.AtLeast(LogLevel.Warning),
            m => m.Contains(ProductChatFaults.EmptyAnswer, StringComparison.Ordinal));
    }

    /// <summary>
    /// THE STRIP MAY NOT EMPTY THE ANSWER (A3). The emptiness check above runs on <c>response.Content</c>,
    /// which is UPSTREAM of every rewrite, so a rewrite that deletes the last word of a one-line answer used
    /// to ship <c>Answer: ""</c> with <c>IsGrounded: true</c> and no fault - an empty card claiming to be
    /// grounded, which is worse than any leaked token. <c>"(EXCERPT)"</c> is exactly that answer: the whole
    /// of it is a gloss the strip removes whole.
    ///
    /// <para>The contract asserted here is the SIBLING's (<c>ProductChatCitations</c> holds it at three
    /// sites): the words come back untouched. Leaving jargon in beats returning nothing.</para>
    /// </summary>
    [Fact]
    public async Task AnAnswerThatIsNOTHINGButAGloss_ShipsItsWords_RatherThanAnEmptyGroundedCard()
    {
        var svc = Service(AnsweringRouter(new List<AiRequest>(), "(EXCERPT)"), out var logger);

        var result = await svc.AnswerAsync(Ask("How do I export my book?"), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.Answer),
            "the answer the strip returned was EMPTY, and it shipped as a grounded card: " +
            $"Answer='{result.Answer}', IsGrounded={result.IsGrounded}, FaultReason={result.FaultReason ?? "null"}. " +
            "A rewrite layer must never delete the whole answer - leaving an internal token in the prose is " +
            "cosmetic, and an empty card that claims to be grounded is not.");
        Assert.Equal("(EXCERPT)", result.Answer);
        Assert.True(result.IsGrounded);
        Assert.Null(result.FaultReason);

        Assert.Contains(logger.AtLeast(LogLevel.Warning),
            m => m.Contains("KEPT", StringComparison.Ordinal) && m.Contains("emptied", StringComparison.Ordinal));
    }

    /// <summary>
    /// A17, the removal half. The count the strip returns is TOKENS, and one token can carry a whole
    /// parenthetical away with it, so "REMOVED 1" alone cannot tell a gloss from a clause. The characters
    /// are logged beside it. Lengths, never the text - the same rule that keeps the question out of the log.
    /// </summary>
    [Fact]
    public async Task TheStripLog_SaysHowManyCHARACTERSWent_NotOnlyHowManyTokens()
    {
        const string leaked = "Chapter 8 slows the pace (EXCERPT).";
        var svc = Service(AnsweringRouter(new List<AiRequest>(), leaked), out var logger);

        var result = await svc.AnswerAsync(Ask("What happens in chapter 8?"), CancellationToken.None);

        Assert.Equal("Chapter 8 slows the pace.", result.Answer);

        var line = Assert.Single(logger.AtLeast(LogLevel.Warning),
            m => m.Contains("REMOVED", StringComparison.Ordinal));
        Assert.Contains("1 internal token(s)", line, StringComparison.Ordinal);
        Assert.Contains("10 of 35 chars", line, StringComparison.Ordinal);
        Assert.DoesNotContain("EXCERPT).", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A17, the denominator. The strip used to log only when it removed something, so "ran and found
    /// nothing" and "never ran" were one silence - and a leak rate whose denominator is "answers that
    /// logged a removal" is the numerator twice. Debug, because it fires on nearly every answer.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithNothingToStrip_SaysSo_SoTheDENOMINATORIsObservable()
    {
        var svc = Service(AnsweringRouter(new List<AiRequest>()), out var logger);

        await svc.AnswerAsync(Ask("How do I export my book?"), CancellationToken.None);

        var debug = logger.At(LogLevel.Debug);
        Assert.True(debug.Any(m => m.Contains("NO internal tokens", StringComparison.Ordinal)),
            "the strip ran over a clean answer and said nothing, so 'ran and found nothing' is " +
            "indistinguishable from 'never ran' and any leak rate read off this log has no denominator. " +
            $"Debug lines logged: {(debug.Count == 0 ? "(none)" : string.Join(" | ", debug))}");
        Assert.DoesNotContain(logger.AtLeast(LogLevel.Warning),
            m => m.Contains("REMOVED", StringComparison.Ordinal)
                 && m.Contains("internal token", StringComparison.Ordinal));
    }

    /// <summary>
    /// final-r01. THE BRACES IS LIVE COVERAGE, not only a structural fence. The comment at that branch used
    /// to say no test could reach it without first breaking the strip's never-empty belt. That is true of
    /// the STRIP and false of the chain: only <c>ProductChatInternalLabels</c> carries a never-empty guard,
    /// and <c>ProductChatPunctuation</c> DROPS an em-dash that both opens and ends its line. A model answer
    /// of one em-dash therefore passes the upstream <c>IsNullOrWhiteSpace</c> (it is not whitespace), passes
    /// the strip untouched (no internal token), and is emptied by the punctuation layer. Without this
    /// re-check the DTO ships <c>Answer: ""</c> with <c>IsGrounded: true</c>.
    /// </summary>
    [Fact]
    public async Task AnAnswerThePUNCTUATIONLayerEmpties_StillReachesTheFailSafe()
    {
        var svc = Service(AnsweringRouter(new List<AiRequest>(), "—"), out var logger);

        var result = await svc.AnswerAsync(Ask("How do I export my book?"), CancellationToken.None);

        Assert.False(result.IsGrounded,
            "a rewrite layer OTHER than the strip emptied the answer and it shipped as a grounded card: " +
            $"Answer='{result.Answer}', IsGrounded={result.IsGrounded}, FaultReason={result.FaultReason ?? "null"}. " +
            "The strip's never-empty guard does not cover this route, which is the whole reason the " +
            "re-check sits after all three rewrites rather than inside one of them.");
        Assert.Equal(ProductChatFaults.EmptyAnswer, result.FaultReason);
        Assert.Contains(logger.AtLeast(LogLevel.Error),
            m => m.Contains("REWROTE THE ANSWER AWAY", StringComparison.Ordinal));
    }

    /// <summary>The refusal answers in the QUESTION's language: an author who asked in Hebrew and got
    /// an English apology has been failed twice.</summary>
    [Fact]
    public async Task TheFailSafeAnswer_IsInTheQuestionsLanguage()
    {
        var he = await Service(ThrowingRouter(), out _).AnswerAsync(Ask("איך מייצאים?"), CancellationToken.None);
        var en = await Service(ThrowingRouter(), out _).AnswerAsync(Ask("How do I export?"), CancellationToken.None);

        Assert.Equal("he", he.Language);
        Assert.Equal("en", en.Language);
        Assert.NotEqual(he.Answer, en.Answer);
    }

    /// <summary>
    /// NOTHING IS LOGGED THAT WOULD LEAK THE QUESTION. Counts, ids and reasons are; the user's own
    /// words are not.
    /// </summary>
    [Fact]
    public async Task TheQuestionText_IsNeverLogged()
    {
        const string secret = "zxqvfluffernutter";
        var svc = Service(AnsweringRouter(new List<AiRequest>()), out var logger);

        await svc.AnswerAsync(Ask($"How do I export my {secret}?"), CancellationToken.None);

        var entries = logger.AtLeast(LogLevel.Trace);
        Assert.NotEmpty(entries);   // the population: a silent success would make this vacuous
        Assert.All(entries, e => Assert.DoesNotContain(secret, e, StringComparison.OrdinalIgnoreCase));
    }

    // ─── The endpoint ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ABlankQuestion_IsA400_AndNeverReachesTheModel()
    {
        var captured = new List<AiRequest>();
        var controller = new ProductChatController(Service(AnsweringRouter(captured), out _), ConversationStore());

        var action = await controller.Ask(new ProductChatRequest("   "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Empty(captured);
    }

    /// <summary>
    /// A FAIL-SAFE IS A 200, not a 5xx. The endpoint did its job, which in that situation is to
    /// refuse; the client tells the two apart on <c>isGrounded</c>, which is why that flag is on the
    /// wire rather than being inferred from an empty citation list.
    /// </summary>
    [Fact]
    public async Task AFailSafe_IsReturnedAs200_WithIsGroundedFalse()
    {
        var controller = new ProductChatController(Service(ThrowingRouter(), out _), ConversationStore());

        var action = await controller.Ask(new ProductChatRequest("How do I export?"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ProductChatResponseDto>(ok.Value);
        Assert.False(dto.IsGrounded);
        Assert.Equal(ProductChatFaults.ModelUnavailable, dto.FaultReason);
    }

    /// <summary>
    /// The window BETWEEN the dual-write's halves: the user turn is committed, the author cancels
    /// mid-GPU-call, and <c>AnswerAsync</c> rethrows the cancellation. The endpoint must flag the
    /// committed question failed - otherwise it sits in storage indistinguishable from one still being
    /// answered - and must still let the cancellation out rather than swallowing it into a 200.
    /// </summary>
    [Fact]
    public async Task ACancelledAnswer_FlagsTheCommittedQuestion_AndStillCancels()
    {
        using var cts = new CancellationTokenSource();
        var router = new Mock<IAiRouter>();
        // The cancellation arrives DURING the model call, after BeginExchangeAsync committed the user
        // turn - which is what makes this the between-the-writes window rather than either write's own.
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .Returns<AiRequest, CancellationToken>((_, _) =>
              {
                  cts.Cancel();
                  return Task.FromException<AiResponse>(new OperationCanceledException(cts.Token));
              });
        var controller = new ProductChatController(Service(router, out _), ConversationStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.Ask(new ProductChatRequest("How do I export my book to Word?"), cts.Token));

        var stored = _conversationDbContext!.ConversationMessages.ToList();
        var question = Assert.Single(stored);
        Assert.Equal("How do I export my book to Word?", question.Text);
        Assert.True(question.Failed);
    }

    [Fact]
    public async Task AGroundedAnswer_Is200_WithIsGroundedTrue_AndANonEmptyCitation()
    {
        var controller = new ProductChatController(
            Service(AnsweringRouter(new List<AiRequest>()), out _), ConversationStore());

        var action = await controller.Ask(new ProductChatRequest("How do I export my book to Word?"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ProductChatResponseDto>(ok.Value);
        Assert.True(dto.IsGrounded);
        Assert.Null(dto.FaultReason);
        Assert.NotEmpty(dto.GuideIds);
        Assert.Contains("export", dto.GuideIds);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static Mock<IAiRouter> ThrowingRouter()
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new HttpRequestException("Ollama request failed: ServiceUnavailable"));
        return router;
    }

    private static ProductChatService Service(Mock<IAiRouter> router, out CapturingLogger<ProductChatService> logger)
        => Service(router, out logger, guidesDirectory: null);

    /// <summary>
    /// The Show C1 dual-write the endpoint now wraps its single <c>AnswerAsync</c> call in, over a throwaway
    /// in-memory database. It is a COLLABORATOR here, not the subject: these cases pin the endpoint's status
    /// codes and grounding contract, which persistence must leave exactly as they were. What persistence
    /// itself does is pinned in <c>ChatConversationStoreTests</c>.
    ///
    /// <para>The backing <see cref="AppDbContext"/> is tracked in <see cref="_conversationDbContext"/> and
    /// released by <see cref="Dispose"/>; instance (not static) so each test's context is scoped to that
    /// test's own instance of this class.</para>
    /// </summary>
    private ChatConversationStore ConversationStore()
    {
        _conversationDbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        return new ChatConversationStore(
            _conversationDbContext,
            new ProductChatGroundingCapture(),
            NullLogger<ChatConversationStore>.Instance);
    }

    private static string MissingDirectory()
        => Path.Combine(Path.GetTempPath(), "pagedraft-guides-absent-" + Guid.NewGuid().ToString("N"));

    private static GuideDocument Guide(string id, string lang = "en")
        => new(id, id, "author", "2026-08-02", lang, $"{id}.md", 10, Array.Empty<string>(), "body");
}
