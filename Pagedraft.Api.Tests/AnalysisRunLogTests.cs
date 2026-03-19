using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    public async Task RunAsync_Proofread_NearlyIdentical_Output_RecordedAsFallbackNoChanges_AndOutputCharCountReflectsLLMOutput()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var inputText = "שלום עולם. זהו טקסט לבדיקה.";
        var llmOutput = inputText; // force ProofreadNoChangesHint=true

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

        Assert.True(runLog.NoChangesHint);
        Assert.Equal(1, runLog.TotalChunks);
        Assert.Equal(0, runLog.SucceededChunks);
        Assert.Equal(1, runLog.FallbackChunks);
        Assert.Equal("Proofread", runLog.AnalysisType);
        Assert.Equal(llmOutput.Length, runLog.OutputCharCount);

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var outcome = Assert.Single(outcomes!);
        Assert.Equal(0, outcome.ChunkIndex);
        Assert.Equal("FallbackNoChanges", outcome.Outcome);
        Assert.Null(outcome.Note);
        Assert.Null(outcome.WordSimilarity);
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

    [Fact]
    public async Task RunAsync_Proofread_Unrelated_Output_RecordedAsFallbackUnrelated_AndOutputCharCountReflectsLLMOutput()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var inputText =
            "זהו טקסט לבדיקה שמטרתו למצוא שגיאות ולהציע תיקונים באיכות גבוהה ומקיפה לאותיות ודקדוק " +
            "במהלך הקריאה כדי לוודא שהמודל אכן מתקן.";

        // Include "Chapter 12" so IsProofreadResultUnrelated can detect continuation marker.
        var llmOutput =
            "Chapter 12: The story continues with completely different content and new characters, far away from the original text.";

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

        Assert.True(runLog.NoChangesHint); // AttachSuggestions treated it as "no changes"
        Assert.Equal(1, runLog.TotalChunks);
        Assert.Equal(0, runLog.SucceededChunks);
        Assert.Equal(1, runLog.FallbackChunks);
        Assert.Equal(llmOutput.Length, runLog.OutputCharCount); // should reflect real LLM output length

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var outcome = Assert.Single(outcomes!);
        Assert.Equal("FallbackUnrelated", outcome.Outcome);
        Assert.NotNull(outcome.Note);
        Assert.StartsWith("similarity=", outcome.Note);
        Assert.NotNull(outcome.WordSimilarity);
    }

    [Fact]
    public async Task RunWithInputAsync_Proofread_Unrelated_Output_RecordedAsFallbackUnrelated_AndOutputCharCountReflectsLLMOutput()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var inputText =
            "זהו טקסט לבדיקה שמטרתו למצוא שגיאות ולהציע תיקונים באיכות גבוהה ומקיפה לאותיות ודקדוק " +
            "במהלך הקריאה כדי לוודא שהמודל אכן מתקן.";

        var llmOutput =
            "Chapter 12: The story continues with completely different content and new characters, far away from the original text.";

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

        var result = await svc.RunWithInputAsync(
            AnalysisScope.Book,
            AnalysisType.Proofread,
            bookId: Guid.NewGuid(),
            chapterId: null,
            sceneId: null,
            inputText,
            language: "he",
            ct: CancellationToken.None);

        var runLog = await db.AnalysisRunLogs
            .SingleAsync(r => r.AnalysisResultId == result.Id);

        Assert.True(runLog.NoChangesHint);
        Assert.Equal(1, runLog.TotalChunks);
        Assert.Equal(0, runLog.SucceededChunks);
        Assert.Equal(1, runLog.FallbackChunks);
        Assert.Equal(llmOutput.Length, runLog.OutputCharCount);

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var outcome = Assert.Single(outcomes!);
        Assert.Equal("FallbackUnrelated", outcome.Outcome);
    }

    [Fact]
    public async Task RunProofreadChunkedAsync_WhitespaceOnlyChunk_PersistsOutcomePerChunk()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var routerMock = new Mock<IAiRouter>(MockBehavior.Loose);
        var contextMock = new Mock<IAnalysisContextService>();

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

        var method = typeof(UnifiedAnalysisService).GetMethod(
            "RunProofreadChunkedAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var inputText = "   ";
        var analysisContext = new AnalysisContext
        {
            TargetText = inputText,
            Scope = AnalysisScope.Chapter,
            AnalysisType = AnalysisType.Proofread,
            BookId = null,
            ChapterId = null,
            SceneId = null,
            Characters = null
        };

        var jobId = Guid.NewGuid();
        var result = (AnalysisResult)await (Task<AnalysisResult>)method.Invoke(
            svc,
            new object?[]
            {
                inputText,
                null, // bookId
                null, // chapterId
                null, // sceneId
                AnalysisScope.Chapter,
                Guid.NewGuid(), // targetId
                null, // customPrompt
                "he", // language
                5, // chunkTargetWords
                1, // maxParallel
                jobId,
                analysisContext,
                CancellationToken.None
            })!;

        var runLog = await db.AnalysisRunLogs
            .SingleAsync(r => r.AnalysisResultId == result.Id);

        Assert.Equal(1, runLog.TotalChunks);
        Assert.Equal(1, runLog.SucceededChunks);
        Assert.Equal(0, runLog.FallbackChunks);

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(1, outcomes!.Count);
        Assert.Equal("Succeeded", outcomes[0].Outcome);
    }

    [Fact]
    public async Task RunProofreadChunkedAsync_RepetitionLoopChunk_RecordedAsFallbackRepetition()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken ct) =>
            {
                const string start = "[TEXT_TO_CORRECT]";
                const string end = "[/TEXT_TO_CORRECT]";

                var input = req.InputText ?? "";
                var startIdx = input.IndexOf(start, StringComparison.Ordinal);
                if (startIdx >= 0)
                {
                    startIdx += start.Length;
                    var endIdx = input.IndexOf(end, startIdx, StringComparison.Ordinal);
                    if (endIdx > startIdx)
                        input = input.Substring(startIdx, endIdx - startIdx);
                }

                // Make the output significantly longer than the input chunk.
                var repeated = $"{input} {input} {input}";

                return new AiResponse
                {
                    Content = repeated,
                    Provider = "test-provider",
                    Model = "test-model"
                };
            });

        var contextMock = new Mock<IAnalysisContextService>();

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

        var method = typeof(UnifiedAnalysisService).GetMethod(
            "RunProofreadChunkedAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var inputText = string.Join(" ", Enumerable.Repeat("שלום", 200));
        var analysisContext = new AnalysisContext
        {
            TargetText = inputText,
            Scope = AnalysisScope.Chapter,
            AnalysisType = AnalysisType.Proofread,
            BookId = null,
            ChapterId = null,
            SceneId = null,
            Characters = null
        };

        var jobId = Guid.NewGuid();
        var result = (AnalysisResult)await (Task<AnalysisResult>)method.Invoke(
            svc,
            new object?[]
            {
                inputText,
                null, // bookId
                null, // chapterId
                null, // sceneId
                AnalysisScope.Chapter,
                Guid.NewGuid(), // targetId
                null, // customPrompt
                "he", // language
                1000, // chunkTargetWords (should produce a single chunk)
                1, // maxParallel
                jobId,
                analysisContext,
                CancellationToken.None
            })!;

        var runLog = await db.AnalysisRunLogs
            .SingleAsync(r => r.AnalysisResultId == result.Id);

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var outcome = Assert.Single(outcomes!);
        Assert.Equal("FallbackRepetition", outcome.Outcome);
        Assert.True(outcome.OutputCharCount > outcome.InputCharCount);
    }

    [Fact]
    public async Task RunLineEditChunkedAsync_WhitespaceOnlyChunk_PersistsOutcomePerChunk()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var routerMock = new Mock<IAiRouter>(MockBehavior.Loose);
        var contextMock = new Mock<IAnalysisContextService>();

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

        var method = typeof(UnifiedAnalysisService).GetMethod(
            "RunLineEditChunkedAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var inputText = "   ";
        var analysisContext = new AnalysisContext
        {
            TargetText = inputText,
            Scope = AnalysisScope.Chapter,
            AnalysisType = AnalysisType.LineEdit,
            BookId = null,
            ChapterId = null,
            SceneId = null,
            Characters = null,
            StyleProfile = null
        };

        var jobId = Guid.NewGuid();
        var result = (AnalysisResult)await (Task<AnalysisResult>)method.Invoke(
            svc,
            new object?[]
            {
                inputText,
                null, // bookId
                null, // chapterId
                null, // sceneId
                AnalysisScope.Chapter,
                Guid.NewGuid(), // targetId
                null, // customPrompt
                "he", // language
                5, // chunkTargetWords
                1, // maxParallel
                jobId,
                analysisContext,
                CancellationToken.None
            })!;

        var runLog = await db.AnalysisRunLogs
            .SingleAsync(r => r.AnalysisResultId == result.Id);

        Assert.Equal(1, runLog.TotalChunks);
        Assert.Equal(1, runLog.SucceededChunks);
        Assert.Equal(0, runLog.FallbackChunks);

        var outcomes = JsonSerializer.Deserialize<List<AnalysisChunkOutcome>>(
            runLog.ChunkDetailsJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(1, outcomes!.Count);
        Assert.Equal("Succeeded", outcomes[0].Outcome);
    }
}

