using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

public class AnalysisRunLogTests
{
    [Fact]
    public async Task RunAsync_Proofread_Normal_PersistsSingleChunkRunLog()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var inputText = "שלום עולם. זהו טקסט לבדיקה.";
        var llmOutput = "שלום עולם! זהו טקסט לבדיקה מתוקן.";

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse
            {
                Content = llmOutput,
                Provider = "test-provider",
                Model = "test-model"
            });

        var contextMock = new Mock<IAnalysisContextService>();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(),
                chapterId,
                AnalysisType.Proofread,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        var svc = new UnifiedAnalysisService(
            db,
            routerMock.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(new AiOptions()),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            contextMock.Object,
            new SuggestionDiffService());

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        var runLog = await db.AnalysisRunLogs
            .SingleAsync(r => r.AnalysisResultId == result.Id);

        Assert.Equal(1, runLog.TotalChunks);
        Assert.Equal(1, runLog.SucceededChunks);
        Assert.Equal(0, runLog.FallbackChunks);
        Assert.False(runLog.NoChangesHint);
        Assert.Equal("Proofread", runLog.AnalysisType);
        Assert.NotNull(runLog.ChunkDetailsJson);

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(outcomes);
        var outcome = Assert.Single(outcomes);
        Assert.Equal(0, outcome.ChunkIndex);
        Assert.Equal("Succeeded", outcome.Outcome);
    }

    [Fact]
    public async Task RunAsync_Proofread_Chunked_EmptyResponse_PersistsSingleOutcomePerChunk()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        // Ensure we go through proofread chunking (wordCount > chunkTargetWords).
        // With chunkTargetWords=5 and 30 words, we should produce multiple chunks.
        var inputText = string.Join(" ", Enumerable.Repeat("שלום", 30));

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse
            {
                Content = "   ", // whitespace -> sanitize/clean should be empty => FallbackEmpty path
                Provider = "test-provider",
                Model = "test-model"
            });

        var contextMock = new Mock<IAnalysisContextService>();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(),
                chapterId,
                AnalysisType.Proofread,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        var chunkingOptions = new AiOptions
        {
            ProofreadChunkTargetWords = 5,
            MaxParallelProofreadChunks = 1
        };

        var svc = new UnifiedAnalysisService(
            db,
            routerMock.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(chunkingOptions),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            contextMock.Object,
            new SuggestionDiffService());

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        var runLog = await db.AnalysisRunLogs
            .SingleAsync(r => r.AnalysisResultId == result.Id);

        Assert.True(runLog.TotalChunks > 1);
        Assert.Equal(0, runLog.SucceededChunks);
        Assert.Equal(runLog.TotalChunks, runLog.FallbackChunks);

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(outcomes);
        Assert.Equal(runLog.TotalChunks, outcomes!.Count);
        Assert.Equal(outcomes.Count, outcomes.Select(o => o.ChunkIndex).Distinct().Count());

        Assert.All(outcomes, o =>
        {
            Assert.Equal("FallbackEmpty", o.Outcome);
            Assert.Equal(0, o.OutputCharCount);
        });
    }

    [Fact]
    public async Task RunAsync_LineEdit_Normal_PersistsSingleChunkRunLog()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var inputText = "זהו משפט אחד. זהו משפט שני כאן.";
        const string original = "משפט שני כאן";
        const string suggested = "משפט שני כאן משופר";

        var structuredJson =
            "{\"suggestions\":[{\"original\":\"" + original + "\",\"suggested\":\"" + suggested +
            "\",\"reason\":\"clarity\",\"category\":\"clarity\"}],\"overallFeedback\":\"משוב כללי\"}";

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse
            {
                Content = structuredJson,
                Provider = "test-provider",
                Model = "test-model"
            });

        var contextMock = new Mock<IAnalysisContextService>();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(),
                chapterId,
                AnalysisType.LineEdit,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.LineEdit,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        var svc = new UnifiedAnalysisService(
            db,
            routerMock.Object,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(new AiOptions()),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            contextMock.Object,
            new SuggestionDiffService());

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.LineEdit,
            chapterId,
            customPrompt: null,
            language: "he",
            jobId: null,
            ct: CancellationToken.None);

        // Ensure AttachSuggestions actually produced at least one suggestion.
        Assert.True(result.Suggestions.Count >= 1);

        var runLog = await db.AnalysisRunLogs
            .SingleAsync(r => r.AnalysisResultId == result.Id);

        Assert.Equal(1, runLog.TotalChunks);
        Assert.Equal(1, runLog.SucceededChunks);
        Assert.Equal(0, runLog.FallbackChunks);
        Assert.False(runLog.NoChangesHint);
        Assert.Equal("LineEdit", runLog.AnalysisType);
        Assert.NotNull(runLog.ChunkDetailsJson);

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(outcomes);
        var outcome = Assert.Single(outcomes);
        Assert.Equal(0, outcome.ChunkIndex);
        Assert.Equal("Succeeded", outcome.Outcome);

        Assert.Equal(result.Suggestions.Count, runLog.SuggestionCount);
    }
}

