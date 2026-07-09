using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Controller-level tests for the widened async-job gate in AnalysisController.StartAnalysisJob
/// (see AnalysisController.cs ~L260-275). AsyncSingleShotJobTests already covers the SERVICE
/// (UnifiedAnalysisService.RunAsync) for single-shot types; these tests pin the CONTROLLER'S gate
/// that decides which AnalysisType values are even allowed onto the async job path.
///
/// The controller fires a background Task.Run for the actual analysis work but returns the
/// ActionResult synchronously before that work runs/completes, so these tests never touch a live
/// model: the scope factory is an unconfigured mock (CreateScope() returns null), which makes the
/// background task fail fast and get swallowed by its own internal try/catch - it never affects the
/// synchronously-returned ActionResult under test.
/// </summary>
public class StartAnalysisJobGateTests
{
    private static (AnalysisController controller, Guid bookId, Guid chapterId) BuildController()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);

        var book = new Book { Title = "Test Book", Language = "he" };
        var chapter = new Chapter { BookId = book.Id, Book = book, Title = "Chapter 1", Order = 0, ContentText = "טקסט לבדיקה" };
        db.Books.Add(book);
        db.Chapters.Add(chapter);
        db.SaveChanges();

        var tracker = new AnalysisProgressTracker();

        // Unconfigured: CreateScope() returns null, so the fire-and-forget background task throws
        // internally and is caught by StartAnalysisJob's own try/catch. This never surfaces to the
        // caller because the controller does not await the background Task.Run.
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();

        var appLifetimeMock = new Mock<IHostApplicationLifetime>();
        appLifetimeMock.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        var controller = new AnalysisController(
            db,
            unifiedAnalysis: null!, // not used directly by StartAnalysisJob; the background task resolves its own scoped instance
            tracker,
            scopeFactoryMock.Object,
            NullLogger<AnalysisController>.Instance,
            appLifetimeMock.Object);

        return (controller, book.Id, chapter.Id);
    }

    private static string? GetErrorMessage(object? badRequestValue)
    {
        if (badRequestValue == null) return null;
        var prop = badRequestValue.GetType().GetProperty("error");
        return prop?.GetValue(badRequestValue) as string;
    }

    [Fact]
    public async Task StartAnalysisJob_SingleShotType_LinguisticAnalysis_ReturnsOk_NotBadRequest()
    {
        var (controller, bookId, chapterId) = BuildController();
        var req = new RunAnalysisRequest(TemplateId: null, CustomPrompt: null, Stream: false, AnalysisType: "LinguisticAnalysis", Language: "he");

        var result = await controller.StartAnalysisJob(bookId, chapterId, sceneId: null, req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StartAnalysisJobResponse>(ok.Value);
        Assert.NotEqual(Guid.Empty, response.JobId);
        Assert.Equal("LinguisticAnalysis", response.AnalysisType);
        Assert.Equal("Chapter", response.Scope);
    }

    [Theory]
    [InlineData("Proofread")]
    [InlineData("LineEdit")]
    [InlineData("LiteraryAnalysis")]
    [InlineData("Summarization")]
    [InlineData("Custom")]
    public async Task StartAnalysisJob_EachSupportedType_ReturnsOk_NotBadRequest(string analysisType)
    {
        var (controller, bookId, chapterId) = BuildController();
        var req = new RunAnalysisRequest(TemplateId: null, CustomPrompt: analysisType == "Custom" ? "do the thing" : null, Stream: false, AnalysisType: analysisType, Language: "he");

        var result = await controller.StartAnalysisJob(bookId, chapterId, sceneId: null, req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<StartAnalysisJobResponse>(ok.Value);
        Assert.Equal(analysisType, response.AnalysisType);
    }

    [Fact]
    public async Task StartAnalysisJob_UnsupportedType_QA_ReturnsBadRequest_WithTypeNameInMessage()
    {
        var (controller, bookId, chapterId) = BuildController();
        var req = new RunAnalysisRequest(TemplateId: null, CustomPrompt: null, Stream: false, AnalysisType: "QA", Language: "he");

        var result = await controller.StartAnalysisJob(bookId, chapterId, sceneId: null, req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var error = GetErrorMessage(badRequest.Value);
        Assert.NotNull(error);
        Assert.Contains("not supported for the", error);
        Assert.Contains("QA", error);
    }

    [Fact]
    public async Task StartAnalysisJob_UnsupportedType_BookReview_ReturnsBadRequest_WithTypeNameInMessage()
    {
        var (controller, bookId, chapterId) = BuildController();
        var req = new RunAnalysisRequest(TemplateId: null, CustomPrompt: null, Stream: false, AnalysisType: "BookReview", Language: "he");

        var result = await controller.StartAnalysisJob(bookId, chapterId, sceneId: null, req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var error = GetErrorMessage(badRequest.Value);
        Assert.NotNull(error);
        Assert.Contains("not supported for the", error);
        Assert.Contains("BookReview", error);
    }
}
