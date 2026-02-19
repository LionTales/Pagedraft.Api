using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

public class DocumentControllerTests
{
    [Fact]
    public async void Import_ReturnsBadRequest_ForNonDocx()
    {
        var controller = CreateController();
        var file = CreateFormFile("test.txt");

        var result = await controller.Import(Guid.NewGuid(), file, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Only DOCX is supported", badRequest.Value);
    }

    [Fact]
    public async void Import_ReturnsPreview_ForValidDocx()
    {
        var controller = CreateController();
        var file = CreateFormFile("test.docx");

        var result = await controller.Import(Guid.NewGuid(), file, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var preview = Assert.IsType<ImportPreviewResponseDto>(ok.Value);
        Assert.Equal("test.docx", preview.FileName);
        Assert.NotEmpty(preview.Chapters);
    }

    private static DocumentController CreateController()
    {
        var parser = new FakeDocxParserService();
        var sfdt = new FakeSfdtConversionService();
        var chapterService = new FakeChapterService();
        var bookAssembly = new FakeBookAssemblyService();

        return new DocumentController(parser, sfdt, chapterService, bookAssembly);
    }

    private static IFormFile CreateFormFile(string fileName)
    {
        var ms = new MemoryStream(new byte[] { 0x01, 0x02, 0x03 });
        return new FormFile(ms, 0, ms.Length, "file", fileName);
    }

    private sealed class FakeDocxParserService : DocxParserService
    {
        public new System.Collections.Generic.List<RawChapterSegment> SplitIntoChapters(Stream docxStream)
        {
            return new()
            {
                new RawChapterSegment
                {
                    Title = "Chapter 1",
                    PartName = null,
                    Order = 0,
                    BodyElements = new System.Collections.Generic.List<DocumentFormat.OpenXml.OpenXmlElement>()
                }
            };
        }
    }

    private sealed class FakeSfdtConversionService : SfdtConversionService
    {
        public new SfdtConversionResult ConvertToSfdt(System.Collections.Generic.List<DocumentFormat.OpenXml.OpenXmlElement> bodyElements)
        {
            return new SfdtConversionResult
            {
                SfdtJson = "{}",
                PlainText = "Sample text",
                WordCount = 2
            };
        }
    }

    private sealed class FakeChapterService : ChapterService
    {
        public FakeChapterService() : base(null!, null!, new SfdtConversionService())
        {
        }
    }

    private sealed class FakeBookAssemblyService : BookAssemblyService
    {
        public FakeBookAssemblyService() : base()
        {
        }
    }
}

