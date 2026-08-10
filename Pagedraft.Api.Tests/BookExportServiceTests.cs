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
        // colon, a quote. An unsanitized name breaks the download on Windows. This assertion is host-
        // independent only because the sanitizer no longer asks the host what a filename is - see
        // BuildFileName_StripsEveryCharacterTheREADERSPlatformForbids_NotJustThisHosts, which is the
        // character-by-character oracle for that; when this ran off Path.GetInvalidFileNameChars() it passed
        // here and failed on the Linux CI runner.
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

    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    [InlineData('\0')]
    public void BuildFileName_StripsEveryCharacterTheREADERSPlatformForbids_NotJustThisHosts(char forbidden)
    {
        // THE POINT OF THIS TEST: the filename is written to disk by the reader's BROWSER, not by the API
        // host, so the rule cannot be Path.GetInvalidFileNameChars() - that answers "what can THIS machine
        // save", and it returns 40+ characters on Windows and only {'\0','/'} on Unix. Under that call this
        // theory passes on a developer's Windows box and fails on the ubuntu-latest CI runner for the eight
        // characters Unix omits, and, worse than red CI, a Linux-hosted API hands a Windows reader a name
        // their machine cannot save. Every case below must hold on EVERY host.
        var name = BookExportService.BuildFileName($"Act 1{forbidden}2 Departure", fallbackStem: "book");

        Assert.Equal("Act 12 Departure.docx", name);
        Assert.DoesNotContain(forbidden, name);
    }

    [Theory]
    [InlineData("CON")]         // the classic devices, all four
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("nul")]         // case does not exempt it
    [InlineData("Com1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("NUL.v2")]      // nor does an extension: the reservation is on the segment before the dot
    [InlineData("con.txt")]
    [InlineData("  CON  ")]     // nor trailing space, which Windows drops
    public void BuildFileName_RefusesAWindowsDeviceName(string title)
    {
        // "NUL.docx" is not a file on Windows, it is the null device: the download would silently go nowhere.
        Assert.Equal("book.docx", BookExportService.BuildFileName(title, fallbackStem: "book"));
    }

    [Theory]
    [InlineData("COM0")]        // COM0/LPT0 are not reserved
    [InlineData("LPT0")]
    [InlineData("CONSOLE")]     // a longer word that merely starts with one
    [InlineData("NULL")]
    [InlineData("My CON game")] // and one that merely contains one
    public void BuildFileName_KeepsATitleThatOnlyLooksLikeADeviceName(string title)
    {
        Assert.Equal(title + ".docx", BookExportService.BuildFileName(title, fallbackStem: "book"));
    }

    [Fact]
    public void BuildFileName_CapsAVeryLongTitle()
    {
        var name = BookExportService.BuildFileName(new string('א', 500), fallbackStem: "book");

        Assert.EndsWith(".docx", name);
        Assert.True(name.Length <= 90, $"Filename should stay short enough for a Content-Disposition header, got {name.Length}.");
    }

    [Fact]
    public void BuildFileName_CapsALongTitleWithoutSplittingASurrogatePair()
    {
        // An emoji is one character to the author and two UTF-16 code units to the cap. Cutting between them
        // leaves an unpaired surrogate, which is not text: encoded into the header's filename* parameter it
        // reaches the reader as a replacement character in the middle of their filename.
        //
        // The 79 ASCII characters are load-bearing: they put the FIRST emoji's two code units astride the
        // 80-unit boundary. A title of nothing but emoji would land the cap on an even offset and pass under
        // the broken code too, which is the shape of a test that cannot fail.
        var name = BookExportService.BuildFileName(new string('a', 79) + string.Concat(Enumerable.Repeat("😀", 10)), fallbackStem: "book");

        var stem = name[..^".docx".Length];
        Assert.Equal(new string('a', 79), stem);
        Assert.True(stem.Length > 0, "The cap should keep a usable stem, not empty it.");
        Assert.All(
            Enumerable.Range(0, stem.Length),
            i => Assert.True(
                !char.IsSurrogate(stem[i]) ||
                (char.IsHighSurrogate(stem[i]) && i + 1 < stem.Length && char.IsLowSurrogate(stem[i + 1])) ||
                (char.IsLowSurrogate(stem[i]) && i > 0 && char.IsHighSurrogate(stem[i - 1])),
                $"Unpaired surrogate at index {i} of the filename stem."));
    }

    [Fact]
    public void BuildFileName_DropsAnUnpairedSurrogateRatherThanEmittingOne()
    {
        var name = BookExportService.BuildFileName("Act \ud83d 1", fallbackStem: "book");

        Assert.Equal("Act 1.docx", name);
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
