using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

public class DocumentVersionsControllerTests
{
    [Fact]
    public async Task Create_And_List_PersistAndReturnSuggestionId()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var sceneId = (Guid?)null;
        var analysisResultId = Guid.NewGuid();
        var suggestionId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Chapter", ContentText = "text" });
        db.AnalysisResults.Add(new AnalysisResult
        {
            Id = analysisResultId,
            ChapterId = chapterId,
            BookId = bookId,
            AnalysisType = AnalysisType.Proofread,
            Type = "Proofread",
            ModelName = "model",
            ResultText = "result",
            Scope = AnalysisScope.Chapter,
            Status = AnalysisStatus.Active,
            ProofreadNoChangesHint = false,
            PromptUsed = "prompt",
            Language = "he"
        });
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            ChapterId = chapterId,
            SceneId = sceneId,
            ContentSfdt = "{}",
            Label = "legacy",
            AnalysisResultId = null,
            SuggestionId = null,
            OriginalText = "legacy-original",
            SuggestedText = "legacy-suggested"
        });
        await db.SaveChangesAsync();

        var controller = new DocumentVersionsController(db);

        var createRequest = new CreateDocumentVersionRequest(
            ContentSfdt: "{}",
            Label: "version-with-suggestion",
            AnalysisId: analysisResultId,
            SuggestionId: suggestionId,
            OriginalText: "orig",
            SuggestedText: "sugg");

        var createResult = await controller.Create(bookId, chapterId, sceneId, createRequest, CancellationToken.None);

        var createdOk = Assert.IsType<OkObjectResult>(createResult.Result);
        var createdDto = Assert.IsType<DocumentVersionDto>(createdOk.Value);
        Assert.Equal(bookId, createdDto.BookId);
        Assert.Equal(chapterId, createdDto.ChapterId);
        Assert.Equal(analysisResultId, createdDto.AnalysisResultId);
        Assert.Equal(suggestionId, createdDto.SuggestionId);
        Assert.Equal("orig", createdDto.OriginalText);
        Assert.Equal("sugg", createdDto.SuggestedText);

        var listResult = await controller.List(bookId, chapterId, sceneId, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var list = Assert.IsType<System.Collections.Generic.List<DocumentVersionDto>>(listOk.Value);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, v => v.SuggestionId == null && v.Label == "legacy");
        Assert.Contains(list, v => v.SuggestionId == suggestionId && v.Label == "version-with-suggestion");
    }
}

