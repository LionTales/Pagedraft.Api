using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// PINS THE LIVE CALLER SET OF THE <see cref="AiTaskType.GenericChat"/> RUNG (c2-genericchat-adjudication).
///
/// Wave 3's <c>w7</c> removed the UI of BOTH surfaces the rung's own breadcrumbs name — the free-form Custom
/// pass and the dashboard ask card — while leaving both server routes intact. That left every written record
/// of this rung describing a caller set with no user-visible surface, which is exactly the shape that gets a
/// rung retired by a later cleanup pass. It would take the <b>Why?</b> button with it: the suggestion-explain
/// path (<c>UnifiedAnalysisService.ExplainSuggestionAsync</c>) builds its <see cref="AiRequest"/> with
/// <see cref="AiTaskType.GenericChat"/> directly, has a live client caller
/// (<c>analysis.service.ts</c> -> <c>POST .../suggestions/{id}/explain</c>), was never named in any of those
/// breadcrumbs, and was untouched by <c>w7</c>.
///
/// The adjudication's verdict was KEEP, so these tests make the reachability that justifies it structural
/// rather than a claim in a comment. Three surfaces, because a retirement pass that only checked one would
/// still find the rung "unused":
///   (1) the DIRECT caller with live UI — <c>ExplainSuggestionAsync</c>;
///   (2) the MAPPED callers, API-reachable only since <c>w7</c> — <see cref="AnalysisType.QA"/> and
///       <see cref="AnalysisType.Custom"/> via <see cref="AnalysisTaskMapping"/>;
///   (3) the SHIPPED tuning entry, whose absence is the regression that created this rung's config block in
///       the first place (commit 339d45c: without <c>Ollama_GenericChat</c> the task falls back to the base
///       Ollama window, a large chapter overflows it, Ollama truncates from the START, and the question is
///       what gets dropped — leaving the model chapter text and no task).
///
/// (3) PINS the shipped window; it does not endorse changing it. The standing caveat from chatbot phase B
/// binds this rung too: do NOT widen <c>NumCtx</c> without a measurement plan.
/// </summary>
public class GenericChatCallerSetTests
{
    // ─── (1) the direct caller that still has a user-visible surface ───

    /// <summary>
    /// The <b>Why?</b> button's server path tags its request <see cref="AiTaskType.GenericChat"/>. This is the
    /// ONLY GenericChat consumer with a live UI after <c>w7</c>, and nothing else in the suite asserts it —
    /// <c>PromptFactoryByteIdentityPinTests</c> pins the explain PROMPT's bytes but not the task it routes to,
    /// so the rung could be retired with that pin still green.
    /// </summary>
    [Fact]
    public async Task ExplainSuggestionAsync_TagsItsRequest_GenericChat()
    {
        using var db = NewDb();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var result = new AnalysisResult
        {
            BookId = bookId,
            ChapterId = chapterId,
            AnalysisType = AnalysisType.Proofread,
            Language = "he"
        };
        var suggestion = new AnalysisSuggestion
        {
            AnalysisResultId = result.Id,
            AnalysisResult = result,
            OriginalText = "הוא הלך לבית",
            SuggestedText = "הוא הלך הביתה",
            Reason = "ניסוח"
        };
        db.AnalysisResults.Add(result);
        db.AnalysisSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        var captured = new List<AiRequest>();
        var svc = NewUnifiedAnalysisService(db, "כי כך נכון יותר.", captured);

        var explanation = await svc.ExplainSuggestionAsync(bookId, chapterId, suggestion.Id, CancellationToken.None);

        Assert.Equal("כי כך נכון יותר.", explanation);
        var request = Assert.Single(captured);
        Assert.Equal(AiTaskType.GenericChat, request.TaskType);
    }

    // ─── (2) the mapped callers, API-reachable only since w7 ───

    /// <summary>
    /// <see cref="AnalysisType.QA"/> (<c>POST /api/books/{bookId}/ask</c>) and
    /// <see cref="AnalysisType.Custom"/> (the analysis-run endpoints' <c>customPrompt</c> / <c>"Custom"</c>
    /// type) both still resolve to this rung. <c>w7</c> deleted their CLIENT callers only — the client's own
    /// <c>book.service.spec.ts</c> pins that there is no <c>ask()</c> any more, and this is the server-side
    /// half of the same fact: the routes did not move, so the rung is still reachable.
    /// </summary>
    [Theory]
    [InlineData(AnalysisType.QA)]
    [InlineData(AnalysisType.Custom)]
    public void MappedAnalysisTypes_StillRouteToGenericChat(AnalysisType analysisType)
    {
        Assert.Equal(AiTaskType.GenericChat, AnalysisTaskMapping.ToAiTaskType(analysisType));
    }

    // ─── (3) the shipped tuning entry ───

    /// <summary>
    /// The shipped <c>Ai:ProviderSettings:Ollama_GenericChat</c> block exists and carries a window LARGER than
    /// the base <c>Ollama</c> one. Deleting the key is silent: routing keeps working, the task just quietly
    /// inherits the base window and long inputs get truncated from the start. Asserted as "> base", not as a
    /// literal, so this stays a floor and never reads as a licence to move the value.
    /// </summary>
    [Fact]
    public void ShippedConfig_GivesGenericChat_ItsOwnLargerWindowThanTheBaseOllamaRung()
    {
        var path = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        var settings = new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection("Ai").Get<AiOptions>()?.ProviderSettings;

        Assert.NotNull(settings);
        Assert.True(settings!.ContainsKey("Ollama_GenericChat"),
            "appsettings.json has no Ai:ProviderSettings:Ollama_GenericChat block. Without it GenericChat " +
            "inherits the base Ollama window and a long chapter is truncated from the START, dropping the " +
            "question and leaving the model text with no task (the regression commit 339d45c fixed).");
        Assert.True(settings.ContainsKey("Ollama"), "appsettings.json has no base Ai:ProviderSettings:Ollama block.");

        var generic = settings["Ollama_GenericChat"].NumCtx;
        var baseline = settings["Ollama"].NumCtx;

        Assert.True(generic > baseline,
            $"Ollama_GenericChat NumCtx ({generic}) must stay above the base Ollama window ({baseline}); " +
            "the whole point of the key is that this task's input does not fit the base rung.");
    }

    // ─── helpers ───

    private static AppDbContext NewDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// A directly-constructed <see cref="UnifiedAnalysisService"/> whose router RECORDS every request, so a
    /// test can assert on the task type the service tagged. Mirrors
    /// <c>AnalysisRepairExclusionRegressionTests.NewUnifiedAnalysisService</c>'s construction.
    /// </summary>
    private static UnifiedAnalysisService NewUnifiedAnalysisService(
        AppDbContext db, string modelOutput, List<AiRequest> captured)
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Callback((AiRequest req, CancellationToken _) => captured.Add(req))
            .ReturnsAsync(new AiResponse { Content = modelOutput, Provider = "test", Model = "qwen3.5:9b" });

        return new UnifiedAnalysisService(
            db,
            router.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(new AiOptions()),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            new Mock<IAnalysisContextService>().Object,
            new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions()),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());
    }

    private static string FindUpward(string relativeSubPath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeSubPath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate " + relativeSubPath + " above " + AppContext.BaseDirectory);
    }
}
