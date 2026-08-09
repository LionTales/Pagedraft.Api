using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// CONTRACT tests for stage 5 (Export) of the Wave 3 stage model - the endpoints the export screen is built
/// on. They pin the three things a client cannot guess and must not have to discover by trial: the HTTP
/// answer for each outcome, the download filename, and the fact that the two export paths (whole book,
/// single chapter) behave the same way. That last one is the standing trap in this codebase: book-level
/// export and single-chapter export are separate document paths that historically drift, and the export
/// screen exposes BOTH.
///
/// What w1 changed, and why each was a real gap on a screen that is about to exist:
///   • an unknown bookId and a book with no chapters BOTH answered 500 - they fell through to the assembler's
///     zero-buffer path, whose "empty document" fallback threw ("There are no sections present in the
///     document"). Two different user situations, one opaque failure, neither attributable by the client;
///   • ONE chapter created but never written to (ContentSfdt is the entity default "{}") made the WHOLE
///     book's export throw the same way;
///   • every book on the installation downloaded as the literal filename "book.docx".
///
/// The happy-path tests run the REAL Syncfusion SFDT -> DOCX conversion, because that is the thing the user
/// receives; asserting a byte count off a fake would prove nothing about the artifact.
/// </summary>
public class BookExportServiceTests
{
    // ─── Book-level export ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportBook_UnknownBook_IsNotFound_NotAnEmptyDocument()
    {
        var (controller, _) = await BuildAsync();

        var result = await controller.ExportBook(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ExportBook_BookWithNoChapters_IsAConflictCarryingAReasonToken()
    {
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "טרם יובא", Language = "he" };
        db.Books.Add(book);
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = Assert.IsType<ExportUnavailableDto>(conflict.Value);
        // A TOKEN, not a sentence: the client owns the Hebrew and English copy.
        Assert.Equal("noChapters", body.Reason);
    }

    [Fact]
    public async Task ExportBook_AssemblesEveryChapterAndNamesTheFileAfterTheBook()
    {
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "מסע הגיבור", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(NewChapter(book.Id, order: 0, title: "פרק א", text: "הגיבור יוצא מביתו בשקט."));
        db.Chapters.Add(NewChapter(book.Id, order: 1, title: "פרק ב", text: "הסופה מגיעה אל החוף."));
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(BookExportService.DocxContentType, file.ContentType);
        Assert.Equal("מסע הגיבור.docx", file.FileDownloadName);
        Assert.True(file.FileContents.Length > 0);
        Assert.True(IsZipArchive(file.FileContents), "A DOCX is a ZIP container; the exported bytes are not one.");
    }

    [Fact]
    public async Task ExportBook_ChapterCreatedByHand_WithDefaultEmptySfdt_StillExports()
    {
        // POST /chapters creates a chapter whose ContentSfdt is the literal "{}". A whole-book export that
        // threw on it would fail on any book the author added a chapter to by hand - which the export screen
        // will hit on its first test book.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Mixed", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(NewChapter(book.Id, order: 0, title: "Written", text: "This chapter has real text in it."));
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "Blank", ContentSfdt = "{}" });
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.True(file.FileContents.Length > 0);
        Assert.True(IsZipArchive(file.FileContents));
    }

    [Fact]
    public async Task ExportBook_EveryChapterUntouched_StillProducesAValidDocument()
    {
        // The book HAS chapters, so this is not "nothing to export"; it is a manuscript with nothing written
        // in it yet. The assembler's zero-buffer fallback has to produce a real DOCX for that, which it did
        // not before w1 - it threw, so this was another 500.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Outline only", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 0, Title = "פרק א", ContentSfdt = "{}" });
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "פרק ב", ContentSfdt = "{}" });
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("Outline only.docx", file.FileDownloadName);
        Assert.True(IsZipArchive(file.FileContents));
    }

    [Fact]
    public async Task ExportChapter_NeverWrittenTo_ExportsAnEmptyDocument()
    {
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Mixed", Language = "he" };
        db.Books.Add(book);
        var chapter = new Chapter { Id = Guid.NewGuid(), BookId = book.Id, Order = 0, Title = "Blank", ContentSfdt = "{}" };
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        var result = await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("Blank.docx", file.FileDownloadName);
        Assert.True(IsZipArchive(file.FileContents));
    }

    [Theory]
    [InlineData("{}")]      // the Chapter entity default: a chapter created but never written to
    [InlineData("  {} ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void HasRenderableContent_RejectsAnUntouchedChapter(string? stored)
    {
        Assert.False(BookExportService.HasRenderableContent(stored));
    }

    [Fact]
    public void HasRenderableContent_AcceptsAnythingElse()
    {
        // The guard must not become a general "if anything looks odd, skip the chapter" rule: a corrupt
        // chapter is a fault to surface, not one to quietly drop from the author's exported manuscript.
        Assert.True(BookExportService.HasRenderableContent(SfdtConversionService.CreateMinimalSfdtFromText("Some real chapter text.")));
        Assert.True(BookExportService.HasRenderableContent("{\"unexpected\":1}"));
    }

    // ─── Chapter-level export: the same rules, the other path ─────────────────────────────────────

    [Fact]
    public async Task ExportChapter_UnknownChapter_IsNotFound()
    {
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "A book", Language = "he" };
        db.Books.Add(book);
        await db.SaveChangesAsync();

        var result = await controller.ExportChapter(book.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ExportChapter_FromAnotherBook_IsNotFound()
    {
        // The lookup is book-scoped, and the export screen deep-links (bookId, chapterId) pairs.
        var (controller, db) = await BuildAsync();
        var mine = new Book { Id = Guid.NewGuid(), Title = "Mine", Language = "he" };
        var other = new Book { Id = Guid.NewGuid(), Title = "Other", Language = "he" };
        db.Books.AddRange(mine, other);
        var chapter = NewChapter(other.Id, order: 0, title: "Elsewhere", text: "Text belonging to the other book.");
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        var result = await controller.ExportChapter(mine.Id, chapter.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ExportChapter_NamesTheFileAfterTheChapter()
    {
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "מסע הגיבור", Language = "he" };
        db.Books.Add(book);
        var chapter = NewChapter(book.Id, order: 0, title: "פרק ראשון", text: "הגיבור יוצא מביתו בשקט.");
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        var result = await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(BookExportService.DocxContentType, file.ContentType);
        Assert.Equal("פרק ראשון.docx", file.FileDownloadName);
        Assert.True(IsZipArchive(file.FileContents));
    }

    [Fact]
    public async Task ExportChapter_TitleWithPathCharacters_ProducesAUsableFileName()
    {
        // Chapter titles come from the manuscript and from the author, so they carry anything: a slash, a
        // colon, a quote. An unsanitized name breaks the download on Windows.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "A book", Language = "he" };
        db.Books.Add(book);
        var chapter = NewChapter(book.Id, order: 0, title: "Act 1/2: \"Departure\"", text: "Some real chapter text.");
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        var result = await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("Act 12 Departure.docx", file.FileDownloadName);
    }

    // ─── The filename rule itself ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("מסע הגיבור", "מסע הגיבור.docx")]                 // Hebrew survives; ASCII-stripping would lose every title
    [InlineData("  padded  title  ", "padded title.docx")]        // trimmed, runs of whitespace collapsed
    [InlineData("Chapter 1.", "Chapter 1.docx")]                  // trailing dot: Windows drops it silently
    [InlineData("", "book.docx")]                                 // blank title falls back
    [InlineData("   ", "book.docx")]
    [InlineData(null, "book.docx")]
    [InlineData("///", "book.docx")]                              // nothing usable survives sanitizing
    public void BuildFileName_ProducesADownloadableName(string? title, string expected)
    {
        Assert.Equal(expected, BookExportService.BuildFileName(title, fallbackStem: "book"));
    }

    [Fact]
    public void BuildFileName_CapsAVeryLongTitle()
    {
        var name = BookExportService.BuildFileName(new string('א', 500), fallbackStem: "book");

        Assert.EndsWith(".docx", name);
        Assert.True(name.Length <= 90, $"Filename should stay short enough for a Content-Disposition header, got {name.Length}.");
    }

    // ─── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>DOCX is an OOXML package, i.e. a ZIP: "PK\x03\x04".</summary>
    private static bool IsZipArchive(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static Chapter NewChapter(Guid bookId, int order, string title, string text) => new()
    {
        Id = Guid.NewGuid(),
        BookId = bookId,
        Order = order,
        Title = title,
        ContentText = text,
        WordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
        ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText(text)
    };

    private static Task<(DocumentController Controller, AppDbContext Db)> BuildAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);

        var sfdt = new SfdtConversionService();
        var chapters = new ChapterService(db, new Mock<IHubContext<BookSyncHub>>().Object, sfdt);
        var assembly = new BookAssemblyService();
        var export = new BookExportService(db, chapters, sfdt, assembly);

        var controller = new DocumentController(new DocxParserService(), sfdt, chapters, assembly, export);
        return Task.FromResult((controller, db));
    }
}
