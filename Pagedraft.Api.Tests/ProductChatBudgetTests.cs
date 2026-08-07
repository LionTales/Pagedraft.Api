using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The context-budget guard on the product chat prompt (chatbot phase A, g1 finding F2).
///
/// <para>WHAT THESE TESTS DEFEND. Ollama truncates an over-long prompt from the START, which is where
/// the grounding rule and the guide text sit, so an overrun does not fail - it returns a confident
/// answer from the model's own priors while the response still reports <c>isGrounded=true</c> and
/// still carries guide-id citations. g1 measured the Hebrew worst case at 90.9% of the window and
/// never exercised it live (every measured request had an empty history), so nothing but these tests
/// stands between a long Hebrew conversation and a silently ungrounded answer.</para>
///
/// <para>THE PROPERTY THAT MATTERS MOST IS THE DROP ORDER, not the fact that something was dropped:
/// history goes first and guides only as a last resort. Each "nothing was dropped" assertion below is
/// paired with a case that PROVES the same input would otherwise have been there, because a trim test
/// whose fixture never reached the budget passes for the wrong reason.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK: the router is stubbed and every number is computed from the
/// real shipped corpus.</para>
/// </summary>
public class ProductChatBudgetTests
{
    // ─── Shared fixtures (also used by ProductChatServiceTests) ─────────────────────────────────

    /// <summary>
    /// Mirrors the shipped <c>Ai:ProviderSettings:Ollama_ProductChat</c> and
    /// <c>Ai:FeatureModels:ProductChat</c> so the budget resolves through the SAME rungs production
    /// uses. Parameterised so a test can shrink the window instead of inventing a giant corpus.
    /// </summary>
    internal static AiOptions AiConfig(int numCtx = 16384, int numPredict = 2048) => new()
    {
        DefaultProvider = "Ollama",
        DefaultModel = "qwen3.5:9b",
        FeatureModels = new Dictionary<string, FeatureModelOptions>
        {
            ["ProductChat"] = new FeatureModelOptions { Provider = "Ollama", Model = "qwen3.5:9b" }
        },
        ProviderSettings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama_ProductChat"] = new ProviderTuningOptions { NumCtx = numCtx, NumPredict = numPredict }
        }
    };

    internal static ProductChatService Service(
        Mock<IAiRouter> router,
        out CapturingLogger<ProductChatService> logger,
        string? guidesDirectory,
        AiOptions? aiOptions)
    {
        logger = new CapturingLogger<ProductChatService>();
        var reader = new GuidesCorpusReader(
            guidesDirectory ?? ProductChatCorpusTests.RealGuidesDirectory(),
            ProductChatCorpusTests.NullLoggerFor<GuidesCorpusReader>());
        return new ProductChatService(
            reader, router.Object, Microsoft.Extensions.Options.Options.Create(aiOptions ?? AiConfig()), logger);
    }

    private static Mock<IAiRouter> AnsweringRouter(List<AiRequest> captured, string content = "An answer.")
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .Callback<AiRequest, CancellationToken>((req, _) => captured.Add(req))
              .ReturnsAsync(new AiResponse { Content = content, Provider = "test", Model = "test-model" });
        return router;
    }

    private const string HebrewQuestion = "איך מייצאים את הספר שלי?";

    /// <summary>Eight turns at the per-turn character cap: g1's F2 worst case, in Hebrew.</summary>
    private static List<ProductChatTurnDto> FullHebrewHistory() =>
        Enumerable.Range(1, ProductChatService.MaxHistoryTurns)
            .Select(i => new ProductChatTurnDto(
                i % 2 == 0 ? "assistant" : "user",
                "ת" + i.ToString("00") + new string('ש', ProductChatService.MaxHistoryTurnChars - 3)))
            .ToList();

    private static IReadOnlyList<GuideDocument> HebrewSelection() =>
        GuideSelector.Select(HebrewQuestion, ProductChatCorpusTests.LoadRealCorpus().Documents, "he");

    // ─── The budget itself ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE BOUND IS DERIVED FROM CONFIG, NOT HARD-CODED. A literal 14336 in the service would keep
    /// claiming to protect a window that had been re-tuned underneath it, which is the failure mode
    /// this codebase has hit before with a stale budget constant.
    /// </summary>
    [Fact]
    public void TheInputBudget_IsDerivedFromTheConfiguredWindowAndOutputReserve()
    {
        var shipped = AiConfig();

        var numCtx = BookContextAssembler.ResolveNumCtxForTask(shipped, AiTaskType.ProductChat);
        var reserve = BookContextAssembler.ResolveOutputReserveForTask(shipped, AiTaskType.ProductChat);

        Assert.Equal(16384, numCtx);     // the shipped Ollama_ProductChat entry, through the real rungs
        Assert.Equal(2048, reserve);
        Assert.Equal(16384 - 2048 - ProductChatBudget.PromptOverheadTokens,
            ProductChatBudget.InputTokenBudget(numCtx, reserve));

        // ... and it MOVES when the window does.
        var narrowed = AiConfig(numCtx: 8192);
        Assert.Equal(8192, BookContextAssembler.ResolveNumCtxForTask(narrowed, AiTaskType.ProductChat));
    }

    /// <summary>
    /// THE ESTIMATE IS CALIBRATED AGAINST A REAL TOKENIZER READING, and biased HIGH. g1 measured
    /// 17,919 chars of Hebrew guide text (71% Hebrew letters) at 8,358 real tokens on qwen3.5:9b. An
    /// estimator that came in UNDER that number would authorise a prompt the runtime then truncates,
    /// which is the whole failure this guard exists to stop.
    /// </summary>
    [Fact]
    public void TheTokenEstimate_IsAtOrAboveG1sMeasuredHebrewGuideCost()
    {
        const int measuredChars = 17_919;
        const int measuredTokens = 8_358;
        var hebrewChars = (int)(measuredChars * 0.71);

        var text = new string('ש', hebrewChars) + new string('a', measuredChars - hebrewChars);

        var estimated = ProductChatBudget.EstimateTokens(text);

        Assert.True(estimated >= measuredTokens,
            $"Estimated {estimated} tokens for g1's measured 17,919-char Hebrew worst case, which really cost " +
            $"{measuredTokens}. Under-estimating hands the runtime a prompt it silently truncates from the start.");
        Assert.True(estimated <= measuredTokens * 1.25,
            $"Estimated {estimated} tokens against a measured {measuredTokens}: more than 25% pessimism starts " +
            "trimming history that would genuinely have fit.");
    }

    /// <summary>
    /// Hebrew is counted at its OWN rate, so an English turn whose selection dragged in a Hebrew twin
    /// (g1 F3 measured that in 25% of English selections) is not under-counted by a Latin-only rate.
    /// </summary>
    [Fact]
    public void TheTokenEstimate_CountsHebrewSeparatelyFromLatin()
    {
        var hebrew = ProductChatBudget.EstimateTokens(new string('ש', 1000));
        var latin = ProductChatBudget.EstimateTokens(new string('a', 1000));

        Assert.True(hebrew > latin, $"Hebrew {hebrew} must cost more tokens per character than Latin {latin}.");
        // A mixed string is the sum of its parts, not the cheaper rate applied to everything.
        Assert.Equal(hebrew + latin, ProductChatBudget.EstimateTokens(new string('ש', 1000) + new string('a', 1000)));
    }

    // ─── Trimming: the drop ORDER ───────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CASE g1 COULD NOT OBSERVE LIVE: a Hebrew turn whose guides and full eight-turn history do
    /// not both fit. Every guide survives and the history is what goes.
    ///
    /// <para>The budget is derived from the fixture rather than hard-coded: it is the real cost of
    /// this selection plus a little slack, so the guides fit and the history cannot. The paired
    /// assertion below the trim proves the history was genuinely there to lose.</para>
    /// </summary>
    [Fact]
    public void WhenThePromptIsTooLarge_TheHistoryIsDropped_AndEveryGuideSurvives()
    {
        var guides = HebrewSelection();
        var history = ProductChatService.CapHistory(FullHebrewHistory());

        Assert.Equal(4, guides.Count);                                    // the population, before the sweep
        Assert.Equal(ProductChatService.MaxHistoryTurns, history.Count);

        var guidesOnly = ProductChatBudget.Compose("he", guides, Array.Empty<ProductChatTurn>(), HebrewQuestion, int.MaxValue);
        var withHistory = ProductChatBudget.Compose("he", guides, history, HebrewQuestion, int.MaxValue);

        // The fixture is real: the history costs something, so a budget between the two numbers forces
        // exactly the choice this guard exists to make.
        Assert.True(withHistory.EstimatedTokens > guidesOnly.EstimatedTokens + 1000);

        var budget = guidesOnly.EstimatedTokens + 100;
        var fitted = ProductChatBudget.Compose("he", guides, history, HebrewQuestion, budget);

        Assert.Equal(ProductChatService.MaxHistoryTurns, fitted.DroppedTurns);
        Assert.Empty(fitted.History);
        Assert.Empty(fitted.DroppedGuideIds);                             // GUIDES SURVIVED
        Assert.Equal(guides.Select(g => g.FileName), fitted.Guides.Select(g => g.FileName));
        Assert.True(fitted.EstimatedTokens <= budget);
        Assert.False(fitted.StillOverBudget);
        Assert.True(fitted.Trimmed);

        // And with the same fixture and a real-world budget, nothing is dropped at all - so the trim
        // above was caused by the budget, not by the history cap or a broken composer.
        var roomy = ProductChatBudget.Compose("he", guides, history, HebrewQuestion, withHistory.EstimatedTokens);
        Assert.Equal(0, roomy.DroppedTurns);
        Assert.Equal(ProductChatService.MaxHistoryTurns, roomy.History.Count);
    }

    /// <summary>The OLDEST turn goes first: the newest turn is the one the question most likely
    /// refers to.</summary>
    [Fact]
    public void TheOldestHistoryTurnIsDroppedFirst()
    {
        var guides = HebrewSelection();
        var history = ProductChatService.CapHistory(FullHebrewHistory());
        var full = ProductChatBudget.Compose("he", guides, history, HebrewQuestion, int.MaxValue);

        // Enough to lose some turns but not all of them.
        var guidesOnly = ProductChatBudget.Compose("he", guides, Array.Empty<ProductChatTurn>(), HebrewQuestion, int.MaxValue);
        var budget = (guidesOnly.EstimatedTokens + full.EstimatedTokens) / 2;

        var fitted = ProductChatBudget.Compose("he", guides, history, HebrewQuestion, budget);

        Assert.InRange(fitted.DroppedTurns, 1, ProductChatService.MaxHistoryTurns - 1);
        Assert.Equal(history.Skip(fitted.DroppedTurns).Select(t => t.Content), fitted.History.Select(t => t.Content));
        Assert.Empty(fitted.DroppedGuideIds);
    }

    /// <summary>
    /// GUIDES ARE THE LAST THING GIVEN UP, and the LOWEST-ranked one goes first. Reached only when the
    /// history is already gone, and never below one guide: an answer with no guide is not an answer
    /// this feature is willing to give, and dropping a guide is announced rather than silent.
    /// </summary>
    [Fact]
    public void OnlyAfterTheHistoryIsGone_IsTheLowestRankedGuideDropped_AndNeverTheLastOne()
    {
        var guides = HebrewSelection();
        var history = ProductChatService.CapHistory(FullHebrewHistory());

        Assert.Equal(4, guides.Count);
        Assert.NotEmpty(history);

        var fitted = ProductChatBudget.Compose("he", guides, history, HebrewQuestion, budgetTokens: 1);

        Assert.Equal(history.Count, fitted.DroppedTurns);          // history first, all of it
        Assert.Empty(fitted.History);
        Assert.Equal(3, fitted.DroppedGuideIds.Count);             // then guides, lowest-ranked FIRST
        Assert.Equal(guides.Skip(1).Reverse().Select(g => g.Id), fitted.DroppedGuideIds);
        Assert.Equal(1, ProductChatBudget.MinGuides);
        var survivor = Assert.Single(fitted.Guides);
        Assert.Equal(guides[0].FileName, survivor.FileName);       // the BEST-ranked guide survives
        Assert.True(fitted.StillOverBudget);                       // and it says so rather than pretending
    }

    /// <summary>
    /// THE SHIPPED CORPUS AT THE SHIPPED WINDOW, against the heaviest thing that can be asked of it:
    /// the four LARGEST Hebrew guides plus a full eight-turn history at the per-turn cap. This is g1's
    /// F2 scenario re-run against the corpus as it stands, and it no longer fits - which is the point.
    /// What must hold is that all four guides still reach the model and the prompt ends up inside the
    /// budget; the history absorbs the whole overrun.
    ///
    /// <para>This test is also the corpus's early-warning line. If the guides grow to the point where
    /// they alone cannot fit beside a question, it fails here - at build time, naming the guides - and
    /// not in production as an answer that is quietly no longer grounded.</para>
    /// </summary>
    [Fact]
    public void TheLargestHebrewGuides_AllSurviveAFullHistory_AtTheShippedWindow()
    {
        var heaviest = ProductChatCorpusTests.LoadRealCorpus().Documents
            .Where(d => d.Lang == "he")
            .OrderByDescending(d => d.Body.Length)
            .Take(GuideSelector.DefaultCount)
            .ToList();
        var history = ProductChatService.CapHistory(FullHebrewHistory());

        Assert.Equal(GuideSelector.DefaultCount, heaviest.Count);   // the population, before the sweep
        Assert.Equal(ProductChatService.MaxHistoryTurns, history.Count);

        var shipped = AiConfig();
        var budget = ProductChatBudget.InputTokenBudget(
            BookContextAssembler.ResolveNumCtxForTask(shipped, AiTaskType.ProductChat),
            BookContextAssembler.ResolveOutputReserveForTask(shipped, AiTaskType.ProductChat));

        var fitted = ProductChatBudget.Compose("he", heaviest, history, HebrewQuestion, budget);

        Assert.Empty(fitted.DroppedGuideIds);
        Assert.Equal(heaviest.Select(g => g.FileName), fitted.Guides.Select(g => g.FileName));
        Assert.False(fitted.StillOverBudget,
            $"The four largest Hebrew guides no longer fit the {budget}-token input budget even with no history " +
            "at all. Widen Ai:ProviderSettings:Ollama_ProductChat NumCtx or shorten the guides; until then every " +
            "Hebrew answer is one guide short of what retrieval chose.");
        Assert.True(fitted.EstimatedTokens <= budget);
    }

    // ─── Through the service ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE REAL PATH, not just the pure composer: a Hebrew question with a full history against a
    /// window too small for both. The guides reach the model; the conversation does not.
    /// </summary>
    [Fact]
    public async Task TheService_SendsTheGuidesAndDropsTheHistory_WhenTheWindowIsTooSmallForBoth()
    {
        var guides = HebrewSelection();
        var guidesOnly = ProductChatBudget.Compose(
            "he", guides, Array.Empty<ProductChatTurn>(), HebrewQuestion, int.MaxValue);

        // A window that fits the guides with ~100 tokens to spare and nothing else.
        var numCtx = guidesOnly.EstimatedTokens + 100 + 2048 + ProductChatBudget.PromptOverheadTokens;

        var captured = new List<AiRequest>();
        var svc = Service(AnsweringRouter(captured), out _, null, AiConfig(numCtx: numCtx));

        var result = await svc.AnswerAsync(
            new ProductChatRequest(HebrewQuestion, FullHebrewHistory()), CancellationToken.None);

        var instruction = Assert.Single(captured).Instruction!;

        // Every selected guide is still in the prompt, by id AND by its real text.
        Assert.Equal(4, guides.Count);
        Assert.All(guides, g => Assert.Contains($"=== GUIDE id={g.Id} lang={g.Lang} ===", instruction, StringComparison.Ordinal));
        Assert.Contains(ProductChatPrompt.SystemMessage("he"), instruction, StringComparison.Ordinal);

        // The history is gone, whole. Asserted over the non-empty population that was sent.
        var sentTurns = FullHebrewHistory();
        Assert.Equal(8, sentTurns.Count);
        Assert.All(sentTurns, t => Assert.DoesNotContain(t.Content!, instruction, StringComparison.Ordinal));
        Assert.DoesNotContain(ProductChatPrompt.HistoryMarker, instruction, StringComparison.Ordinal);

        Assert.True(result.IsGrounded);
        Assert.Equal(4, result.GuideIds.Count);
    }

    /// <summary>
    /// The trim is LOGGED, naming what was given up. A prompt that quietly shed its context is
    /// indistinguishable from one that never had any once the answer comes back, and this codebase has
    /// shipped silent fail-safes before.
    /// </summary>
    [Fact]
    public async Task ATrimIsLoggedAtWarning_NamingTheTurnsDroppedAndTheGuidesKept()
    {
        var guides = HebrewSelection();
        var guidesOnly = ProductChatBudget.Compose(
            "he", guides, Array.Empty<ProductChatTurn>(), HebrewQuestion, int.MaxValue);
        var numCtx = guidesOnly.EstimatedTokens + 100 + 2048 + ProductChatBudget.PromptOverheadTokens;

        var svc = Service(AnsweringRouter(new List<AiRequest>()), out var logger, null, AiConfig(numCtx: numCtx));

        await svc.AnswerAsync(new ProductChatRequest(HebrewQuestion, FullHebrewHistory()), CancellationToken.None);

        var warnings = logger.AtLeast(LogLevel.Warning);
        Assert.NotEmpty(warnings);      // the population, before anything is asserted about it
        var trim = Assert.Single(warnings, w => w.Contains("TRIMMED", StringComparison.Ordinal));
        Assert.Contains("Dropped 8 of 8 history turn(s)", trim, StringComparison.Ordinal);
        Assert.Contains("0 guide(s)", trim, StringComparison.Ordinal);         // and no guide was given up
        Assert.Contains(guides[0].Id, trim, StringComparison.Ordinal);         // guides kept, named
    }

    /// <summary>An ordinary request does NOT trim, and does not log a warning about trimming. Paired
    /// with the trim tests above so neither reads as "the guard fires on everything".</summary>
    [Fact]
    public async Task AnOrdinaryHebrewRequestWithAFullHistory_IsNotTrimmed_AtTheShippedWindow()
    {
        var captured = new List<AiRequest>();
        var svc = Service(AnsweringRouter(captured), out var logger, null, AiConfig());

        await svc.AnswerAsync(new ProductChatRequest(HebrewQuestion, FullHebrewHistory()), CancellationToken.None);

        var instruction = Assert.Single(captured).Instruction!;
        Assert.Contains(ProductChatPrompt.HistoryMarker, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.AtLeast(LogLevel.Warning), w => w.Contains("TRIMMED", StringComparison.Ordinal));
    }

    /// <summary>
    /// A DROPPED GUIDE CANNOT BE CITED. The citation is computed against what SURVIVED, so an answer
    /// naming a guide the trim removed falls back to the guides that were actually sent rather than
    /// claiming provenance the model never saw.
    /// </summary>
    [Fact]
    public async Task ACitationNamingADroppedGuide_IsNotAccepted()
    {
        var guides = HebrewSelection();
        var survivor = guides[0];
        var dropped = guides.Last(g => !string.Equals(g.Id, survivor.Id, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(dropped.Id, survivor.Id);

        var svc = Service(
            AnsweringRouter(new List<AiRequest>(), $"תשובה.\n\nמדריכים: {dropped.Id}"),
            out _, null, AiConfig(numCtx: 2048 + ProductChatBudget.PromptOverheadTokens + 1));

        var result = await svc.AnswerAsync(new ProductChatRequest(HebrewQuestion), CancellationToken.None);

        Assert.Equal(new[] { survivor.Id }, result.GuideIds);   // only the guide that survived
        Assert.DoesNotContain(dropped.Id, result.GuideIds);
    }

    /// <summary>The prompt size and the budget it was measured against are on the success log line, so
    /// a live run can report headroom without re-deriving it (g1 had to parse it out of a char count).</summary>
    [Fact]
    public async Task ThePromptSizeAndBudget_AreLoggedOnASuccessfulAnswer()
    {
        var svc = Service(AnsweringRouter(new List<AiRequest>()), out var logger, null, AiConfig());

        await svc.AnswerAsync(new ProductChatRequest(HebrewQuestion), CancellationToken.None);

        var entries = logger.AtLeast(LogLevel.Information);
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Contains("input tokens", StringComparison.Ordinal));
    }
}
