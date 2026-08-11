using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
/// What the wave-3 fix pass then changed, and why an EXPORT is where honesty is cheapest to lose: a chapter
/// with nothing renderable in it is left out of the assembled document, and w1 left that out SILENTLY. So a
/// 10-chapter book with 3 unwritten chapters downloaded as a plausible manuscript with 3 chapters missing,
/// and an all-unwritten book downloaded as a valid, correctly-named, EMPTY file with HTTP 200 - the exact
/// thing BookExportOutcome.NothingToExport's own doc comment forbids, pinned as desired by a test in this
/// class. Both are now answered: the skipped chapters are named on the response, and a document that would
/// contain nothing is a 409 instead. Both export paths, one rule.
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
        // Exporting is not the same as exporting everything: the blank chapter is left out, and said so.
        Assert.Equal("1", SkippedCount(controller));
        Assert.Equal(new[] { "Blank" }, SkippedTitles(controller));
    }

    [Fact]
    public async Task ExportBook_ManySkippedChapters_KeepsTheHeaderBoundedAndTheCountTruthful()
    {
        // The header shares a few kilobytes of response-header budget with Content-Disposition and the rest,
        // and a Hebrew title costs about six characters per letter once percent-encoded. So the LIST is
        // bounded and the COUNT is not: a client that renders the count is always right, and one that renders
        // the names knows it may be seeing fewer of them.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "ארוך", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(NewChapter(book.Id, order: 0, title: "כתוב", text: "הגיבור יוצא מביתו בשקט."));
        for (var i = 1; i <= 60; i++)
        {
            db.Chapters.Add(new Chapter { BookId = book.Id, Order = i, Title = new string('פ', 60), ContentSfdt = "{}" });
        }
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal("60", SkippedCount(controller));

        var raw = controller.Response.Headers[BookExportService.SkippedChaptersHeader].ToString();
        Assert.True(raw.Length <= 3000, $"The skipped-chapters header must stay inside its budget, got {raw.Length}.");
        var named = SkippedChapters(controller);
        Assert.NotEmpty(named);
        Assert.True(named.Count < 60, "This case is only meaningful if the list was actually bounded below the count.");
    }

    [Theory]
    // The neighbours of the "{}" entity default. The SceneService writes the second of these as its own empty
    // document, so it is not a hypothetical shape - it is one this product's own code produces.
    [InlineData("{\"sections\":[]}")]
    [InlineData("{\"sections\":[{\"blocks\":[]}]}")]
    public async Task ExportBook_ChapterCarryingAnEmptyDocumentShape_DoesNotFailTheWholeBook(string emptyShape)
    {
        // These reach Syncfusion and throw KeyNotFoundException("sfdt"), i.e. the WHOLE book's export answers
        // 500 because of one chapter nobody has written in - the identical failure the "{}" guard was written
        // to close, one shape over. Verified against the un-patched guard: both cases threw here.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Mixed", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(NewChapter(book.Id, order: 0, title: "Written", text: "This chapter has real text in it."));
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "Blank", ContentSfdt = emptyShape });
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.True(IsZipArchive(file.FileContents));
        // And it is reported, not silently dropped.
        Assert.Equal("1", SkippedCount(controller));
        Assert.Equal(new[] { "Blank" }, SkippedTitles(controller));
    }

    [Fact]
    public async Task ExportBook_EveryChapterUntouched_IsAConflict_NotAnEmptyDocument()
    {
        // REWRITTEN. This used to assert the empty document as desired behaviour, under the name
        // ExportBook_EveryChapterUntouched_StillProducesAValidDocument. The book HAS chapters, so the reason
        // is not "noChapters" - it is that nothing in it has been written yet - but handing the author a
        // valid, correctly-named .docx with nothing in it in answer to "export my book" is exactly what
        // BookExportOutcome.NothingToExport's own doc comment forbids, and it is worse than the 500 that
        // preceded it: a 500 is visible, a plausible empty file is not.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Outline only", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 0, Title = "פרק א", ContentSfdt = "{}" });
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "פרק ב", ContentSfdt = "{}" });
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = Assert.IsType<ExportUnavailableDto>(conflict.Value);
        // A DIFFERENT token from the no-chapters case: the author's next action is "write", not "import".
        Assert.Equal("nothingWritten", body.Reason);
        Assert.NotEqual(ExportUnavailableDto.NoChapters, body.Reason);
    }

    [Fact]
    public async Task ExportBook_SomeChaptersUnwritten_NamesThemOnTheSuccessfulResponse()
    {
        // The partial case, which is the worse one: the file DOES arrive, it looks like the manuscript, and
        // two chapters are missing from it. Before this, nothing on any surface said so.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "חלקי", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(NewChapter(book.Id, order: 0, title: "פרק א", text: "הגיבור יוצא מביתו בשקט."));
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "פרק ב", ContentSfdt = "{}" });
        db.Chapters.Add(NewChapter(book.Id, order: 2, title: "פרק ג", text: "הסופה מגיעה אל החוף."));
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 3, Title = "פרק ד", ContentSfdt = "   " });
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.True(IsZipArchive(file.FileContents));
        Assert.Equal("2", SkippedCount(controller));

        var skipped = SkippedChapters(controller);
        Assert.Equal(2, skipped.Count);
        // Order and title, in Order, so the client can render a sentence naming chapters the author knows.
        Assert.Equal(1, skipped[0].Order);
        Assert.Equal("פרק ב", skipped[0].Title);
        Assert.Equal(3, skipped[1].Order);
        Assert.Equal("פרק ד", skipped[1].Title);
    }

    [Fact]
    public async Task ExportBook_NothingSkipped_StillSaysSoRatherThanSayingNothing()
    {
        // The zero is written deliberately. If the header were emitted only when something was skipped, a
        // client could not tell "nothing was skipped" from "this server, or a proxy, did not tell me".
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Complete", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(NewChapter(book.Id, order: 0, title: "One", text: "This chapter has real text in it."));
        await db.SaveChangesAsync();

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal("0", SkippedCount(controller));
        Assert.False(controller.Response.Headers.ContainsKey(BookExportService.SkippedChaptersHeader));
    }

    [Fact]
    public async Task ExportChapter_NeverWrittenTo_IsAConflict_NotAnEmptyDocument()
    {
        // REWRITTEN, and it is the BOOK path's all-unwritten case seen through a one-chapter window - the two
        // document paths are the standing drift trap in this codebase, so they answer with the same token.
        // This used to be ExportChapter_NeverWrittenTo_ExportsAnEmptyDocument and asserted "Blank.docx".
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Mixed", Language = "he" };
        db.Books.Add(book);
        var chapter = new Chapter { Id = Guid.NewGuid(), BookId = book.Id, Order = 0, Title = "Blank", ContentSfdt = "{}" };
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        var result = await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = Assert.IsType<ExportUnavailableDto>(conflict.Value);
        Assert.Equal("nothingWritten", body.Reason);
    }

    [Fact]
    public async Task ExportChapter_CarryingAnEmptyDocumentShape_IsTheSameConflict()
    {
        // The other half of the drift check: the empty-document family is recognized on BOTH paths, and on
        // this one it is a 409 rather than a skip, because the unit the caller asked for is the empty one.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Mixed", Language = "he" };
        db.Books.Add(book);
        var chapter = new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Order = 0,
            Title = "Blank",
            ContentSfdt = "{\"sections\":[{\"blocks\":[]}]}"
        };
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        var result = await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("nothingWritten", Assert.IsType<ExportUnavailableDto>(conflict.Value).Reason);
    }

    [Fact]
    public async Task ExportChapter_SuccessfulExport_ReportsNothingSkipped()
    {
        // Nothing can be "skipped" on the chapter path by construction - the unit either exports or it does
        // not - but the header is written anyway, so a client reads ONE contract on both paths.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "A book", Language = "he" };
        db.Books.Add(book);
        var chapter = NewChapter(book.Id, order: 0, title: "One", text: "This chapter has real text in it.");
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        var result = await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal("0", SkippedCount(controller));
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

    [Theory]
    [InlineData("{\"sections\":[]}")]                                      // no sections at all
    [InlineData("  {\"sections\":[]}\n")]                                  // and it survives surrounding whitespace
    [InlineData("{\"sections\":[{\"blocks\":[]}]}")]                       // SceneService's own empty default
    [InlineData("{\"sections\":[{}]}")]                                    // a section with no blocks key
    [InlineData("{\"sections\":[{\"blocks\":[]},{\"blocks\":[]}]}")]       // several, all blockless
    public void HasRenderableContent_RejectsTheWholeEmptyDocumentFamily_NotJustTheOneLiteral(string stored)
    {
        // Matching only "{}" closed one member of a family and left its neighbours open, and the neighbours
        // are not hypothetical: SceneService writes {"sections":[{"blocks":[]}]} as its empty document. Each
        // of these reaches Syncfusion and throws, taking the whole book's export down with it.
        Assert.False(BookExportService.HasRenderableContent(stored));
    }

    [Theory]
    [InlineData("{\"unexpected\":1}")]                                     // a JSON object that is not an SFDT document
    [InlineData("not json at all")]
    [InlineData("[]")]                                                     // valid JSON, wrong root kind
    [InlineData("\"sections\"")]
    [InlineData("{\"sections\":\"nope\"}")]                                // sections present but not an array
    [InlineData("{\"sections\":[{\"blocks\":\"nope\"}]}")]                 // blocks present but not an array
    [InlineData("{\"sections\":[42]}")]                                    // a section that is not an object
    public void HasRenderableContent_KeepsCorruptSfdtLoud_RatherThanDroppingTheChapter(string stored)
    {
        // The failure modes are NOT symmetrical. Widening the guard until it swallows anything odd would make
        // a corrupt chapter disappear from the author's exported manuscript with no error anywhere - which is
        // the very defect this todo exists to remove, arriving from the opposite direction. Anything that is
        // not RECOGNIZABLY an empty document goes to the converter and is allowed to throw.
        Assert.True(BookExportService.HasRenderableContent(stored));
    }

    [Fact]
    public void HasRenderableContent_AcceptsARealChapter()
    {
        Assert.True(BookExportService.HasRenderableContent(SfdtConversionService.CreateMinimalSfdtFromText("Some real chapter text.")));
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

    /// <summary>The raw skipped-count header, as a client would read it - no default, no coercion.</summary>
    private static string? SkippedCount(DocumentController controller) =>
        controller.Response.Headers[BookExportService.SkippedCountHeader].ToString() is { Length: > 0 } v ? v : null;

    /// <summary>
    /// Decodes the skipped-chapters header EXACTLY the way the client is told to:
    /// <c>JSON.parse(decodeURIComponent(value))</c>. If this helper and the client's read ever disagree, the
    /// contract stated in <c>BookExportService.SkippedChaptersHeader</c> is what both are wrong about.
    /// </summary>
    private static IReadOnlyList<SkippedChapterOnTheWire> SkippedChapters(DocumentController controller)
    {
        var raw = controller.Response.Headers[BookExportService.SkippedChaptersHeader].ToString();
        Assert.False(string.IsNullOrEmpty(raw), "The skipped-chapters header is missing.");
        var json = Uri.UnescapeDataString(raw);
        return JsonSerializer.Deserialize<List<SkippedChapterOnTheWire>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static IReadOnlyList<string> SkippedTitles(DocumentController controller) =>
        SkippedChapters(controller).Select(c => c.Title).ToList();

    /// <summary>Declared here rather than reused from the API so the wire shape is asserted, not assumed.</summary>
    private sealed record SkippedChapterOnTheWire(int Order, string Title);

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

        var controller = new DocumentController(new DocxParserService(), sfdt, chapters, assembly, export)
        {
            // A real HttpContext, because the export answer is not only a body: the skipped-chapter signal
            // rides on RESPONSE HEADERS, and a controller with no context cannot write one.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return Task.FromResult((controller, db));
    }
}
