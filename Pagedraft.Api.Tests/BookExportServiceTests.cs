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

    // ─── Export readiness, as a surface that speaks for the exporter must read it (w8 / F2) ───────
    //
    // THE STATE THESE TESTS SEED IS THE ONE NO FIXTURE HELD. The stage spine's Export stage read "chapters
    // whose WordCount is greater than zero" while this service decides by asking whether the stored SFDT holds
    // a renderable block, and every book in every fixture satisfied both predicates or neither - so a green
    // suite could not see the gap. The live gate could: a book seeded with word counts and the entity-default
    // "{}" document rendered `Export: Ready` above a card saying the file would be empty, and the endpoint
    // answered 409. Each test below therefore seeds THAT book and asserts the two answers together.

    [Fact]
    public async Task ExportableCount_AndTheEndpoint_AgreeOnABookWithWordCountsButNoRenderableDocument()
    {
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "יובא, לא נערך", Language = "he" };
        db.Books.Add(book);
        // Word counts and plain text over the entity-default document: exactly the shape the seeded gate
        // books carry, and exactly the divergence. SEEDED, not produced - no production write reaches it,
        // because ChapterService derives every WordCount from the SFDT it stores - which is the point: a row
        // can hold it, so the two predicates must be allowed to disagree on it.
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 0, Title = "פרק א", ContentText = "טקסט", WordCount = 120, ContentSfdt = "{}" });
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "פרק ב", ContentText = "טקסט", WordCount = 340, ContentSfdt = "{}" });
        await db.SaveChangesAsync();

        var chapters = await db.Chapters.Where(c => c.BookId == book.Id).ToListAsync();
        // The premise, asserted rather than assumed: the word-count predicate - still live for stage 1, but
        // no longer read by the Export stage - says this book is ready to export.
        Assert.Equal(2, chapters.Count(ChapterTextPredicate.HasText.Compile()));

        var exportable = await BookExportService.CountExportableChaptersAsync(db, chapters, CancellationToken.None);
        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        Assert.Equal(0, exportable);
        Assert.Equal("nothingWritten", Assert.IsType<ExportUnavailableDto>(Assert.IsType<ConflictObjectResult>(result).Value).Reason);
    }

    [Fact]
    public async Task ExportableCount_AndTheEndpoint_AgreeOnABookThatReallyHasDocuments()
    {
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Written", Language = "en" };
        db.Books.Add(book);
        db.Chapters.Add(NewChapter(book.Id, 0, "One", "The first chapter, really written."));
        db.Chapters.Add(NewChapter(book.Id, 1, "Two", "The second chapter, also written."));
        await db.SaveChangesAsync();

        var chapters = await db.Chapters.Where(c => c.BookId == book.Id).ToListAsync();

        var exportable = await BookExportService.CountExportableChaptersAsync(db, chapters, CancellationToken.None);
        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        Assert.Equal(2, exportable);
        Assert.IsType<FileContentResult>(result); // a real file, so `ready` is a claim the exporter backs.
    }

    [Fact]
    public async Task ExportableCount_CountsOnlyTheChaptersTheFileWouldContain()
    {
        // The PARTIAL book, which is the case the spine's old warning was written for and got right. The count
        // is not a boolean: it has to survive being read as "how many chapters would be in the file".
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Half written", Language = "en" };
        db.Books.Add(book);
        db.Chapters.Add(NewChapter(book.Id, 0, "Written", "This one exists."));
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "Blank", WordCount = 90, ContentSfdt = "{}" });
        await db.SaveChangesAsync();

        var chapters = await db.Chapters.Where(c => c.BookId == book.Id).ToListAsync();

        var exportable = await BookExportService.CountExportableChaptersAsync(db, chapters, CancellationToken.None);
        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        Assert.Equal(1, exportable);
        Assert.IsType<FileContentResult>(result);
        Assert.Equal("1", SkippedCount(controller)); // the exporter left out exactly the one this did not count
    }

    [Fact]
    public async Task ExportableCount_ReadsTheSCENES_WhenTheySpeakForTheChapter()
    {
        // WHY THIS IS NOT A PREDICATE OVER Chapter.ContentSfdt. A chapter the author split and then wrote into
        // is exported from its scenes, and its own row is a frozen pre-split copy - here, an empty one. A
        // chapter-only check would report this book as unexportable while the endpoint happily produces a file.
        var (controller, db) = await BuildAsync();
        var book = new Book { Id = Guid.NewGuid(), Title = "Split", Language = "en" };
        db.Books.Add(book);
        var chapter = new Chapter { Id = Guid.NewGuid(), BookId = book.Id, Order = 0, Title = "Split", WordCount = 0, ContentSfdt = "{}" };
        db.Chapters.Add(chapter);
        var scene = new Scene { Id = Guid.NewGuid(), ChapterId = chapter.Id, Order = 0, Title = "Scene one", ContentSfdt = "{}" };
        db.Scenes.Add(scene);
        await db.SaveChangesAsync();

        // A second write is what makes a scene "written" (UpdatedAt > CreatedAt) - the same route the editor
        // takes, rather than hand-stamping the timestamps this rule is defined on.
        scene.ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("The scene the author actually wrote.");
        await db.SaveChangesAsync();

        var chapters = await db.Chapters.Where(c => c.BookId == book.Id).ToListAsync();

        var exportable = await BookExportService.CountExportableChaptersAsync(db, chapters, CancellationToken.None);
        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        Assert.Equal(1, exportable);
        Assert.IsType<FileContentResult>(result);
    }

    /// <summary>
    /// The short-circuit, asserted through a seam that can actually see it.
    ///
    /// <para>This test used to promise "WithoutQueryingForScenes" in its name and assert only the returned
    /// zero, so an implementation with the <c>chapters.Count == 0</c> guard deleted passed it identically -
    /// a name a future reader would trust for a guarantee nothing was checking. A DISPOSED context is the
    /// cheap seam: EF throws <c>ObjectDisposedException</c> the moment a query is composed against it, so
    /// the guard is the only reason this call can return at all.</para>
    /// </summary>
    [Fact]
    public async Task ExportableCount_IsZeroForABookWithNoChapters_WithoutQueryingForScenes()
    {
        var (_, db) = await BuildAsync();
        await db.DisposeAsync();

        var count = -1;
        var fault = await Record.ExceptionAsync(async () =>
            count = await BookExportService.CountExportableChaptersAsync(db, new List<Chapter>(), CancellationToken.None));

        Assert.True(fault == null,
            "CountExportableChaptersAsync went to the database for a book with NO chapters: the " +
            "`chapters.Count == 0` short-circuit this test's name promises is gone, so every books payload " +
            $"for an un-imported book now pays a scenes query for nothing. ({fault?.GetType().Name}: {fault?.Message})");
        Assert.Equal(0, count);
    }

    // ─── The invariant the two export loops must satisfy together ────────────────────────────────
    //
    // WHAT IS ACTUALLY TRUE, AND WHY IT NEEDED PINNING. The readiness signal is only honest if
    //
    //     CountExportableChaptersAsync(db, chapters) == (the chapters ExportBookAsync put in the file)
    //
    // and those two numbers are produced by two INDEPENDENT `foreach` loops in BookExportService
    // (ExportBookAsync and CountExportableChaptersAsync), which share only the pure RenderableUnitsOf
    // predicate. Nothing in the type system makes them agree. Before these tests the invariant was asserted
    // once, on one two-chapter fixture, and the scene test never compared against the skipped header at all,
    // so a `continue` added to the export loop that drops a chapter WITHOUT recording it as skipped passed
    // every test in this file.
    //
    // So the shapes below are chosen to vary the thing the two loops can disagree about rather than to add
    // more books: nothing to iterate, every chapter included, none included, a mixture, a chapter whose
    // SCENES hold its current text (the case the shared predicate exists for), and a chapter whose scenes
    // hold its text but are all blank - the state between "there are scenes" and "the scenes have content",
    // which no fixture in this repo held.
    //
    // WHAT THIS CANNOT SEE, said plainly: the invariant is AGREEMENT, so a change applied to both loops
    // identically stays green by construction. It catches divergence, not a shared wrong answer; the
    // per-shape endpoint assertions below are what pin the answer itself.

    private const string NoChapters = "no chapters";
    private const string EveryChapterExportable = "every chapter exportable";
    private const string NoChapterExportable = "no chapter exportable";
    private const string SomeChaptersExportable = "some chapters exportable";
    private const string ScenesHoldTheText = "a chapter whose scenes hold its text";
    private const string ScenesWrittenThenEmptied = "a chapter whose scenes were written then emptied";
    private const string OnlyAChapterWhoseScenesWereEmptied = "only a chapter whose scenes were emptied";

    [Theory]
    [InlineData(NoChapters)]
    [InlineData(EveryChapterExportable)]
    [InlineData(NoChapterExportable)]
    [InlineData(SomeChaptersExportable)]
    [InlineData(ScenesHoldTheText)]
    [InlineData(ScenesWrittenThenEmptied)]
    [InlineData(OnlyAChapterWhoseScenesWereEmptied)]
    public async Task TheExportableCount_EqualsWhatTheExporterPutInTheFile_AndTheEndpointAgrees(string shape)
    {
        var (controller, db, exporter) = Build();
        var bookId = await SeedExportShapeAsync(db, shape);

        // ONE fixture, read by both sides. Two hand-written numbers would be two opinions about a book;
        // this is the same chapter list handed to the counter and to the exporter.
        var chapters = await db.Chapters.Where(c => c.BookId == bookId).OrderBy(c => c.Order).ToListAsync();

        var counted = await BookExportService.CountExportableChaptersAsync(db, chapters, CancellationToken.None);
        var result = await exporter.ExportBookAsync(bookId, CancellationToken.None);

        var included = result.Outcome switch
        {
            BookExportOutcome.Ok => chapters.Count - result.SkippedChapters.Count,
            // Both terminal outcomes mean the assembled document would hold NOTHING, so the exporter
            // included no chapter at all. Neither populates SkippedChapters (see BookExportResult), so the
            // subtraction above cannot be reused here: zero comes from the outcome's own definition.
            BookExportOutcome.NothingToExport or BookExportOutcome.NothingWritten => 0,
            _ => throw new Xunit.Sdk.XunitException(
                $"[{shape}] ExportBookAsync answered {result.Outcome} for a book that exists, which is " +
                "neither a file nor one of the two 'nothing to download' outcomes.")
        };

        var skippedList = result.SkippedChapters.Count == 0
            ? "none"
            : string.Join("; ", result.SkippedChapters.Select(c => $"order {c.Order} '{c.Title}'"));

        Assert.True(counted == included,
            $"[{shape}] THE EXPORT READINESS INVARIANT IS BROKEN. " +
            $"CountExportableChaptersAsync says {counted} of this book's {chapters.Count} chapters are " +
            $"exportable, but ExportBookAsync ({result.Outcome}) put {included} of them in the file and " +
            $"named these as skipped: {skippedList}. Those two answers come from two INDEPENDENT foreach " +
            "loops over the same chapters in Services/BookExportService.cs (ExportBookAsync and " +
            "CountExportableChaptersAsync). When they diverge the stage spine says Export: Ready over an " +
            "endpoint that answers 409, or a downloaded manuscript is quietly missing a chapter that " +
            "nothing on the response mentions. Whichever loop you changed, change both.");

        // AND the endpoint's own answer, from the same fixture: the count is a claim ABOUT this call, so a
        // test that never makes it is pinning arithmetic rather than the promise the client reads.
        var httpResult = await controller.ExportBook(bookId, CancellationToken.None);

        if (counted == 0)
        {
            var conflict = Assert.IsType<ConflictObjectResult>(httpResult);
            var reason = Assert.IsType<ExportUnavailableDto>(conflict.Value).Reason;
            var expected = chapters.Count == 0 ? ExportUnavailableDto.NoChapters : ExportUnavailableDto.NothingWritten;
            Assert.True(expected == reason,
                $"[{shape}] Nothing is exportable and the book has {chapters.Count} chapters, so the " +
                $"endpoint owes the reason token '{expected}'; it answered '{reason}'. The two tokens lead " +
                "the author to different next actions (import a manuscript, versus write something).");
        }
        else
        {
            Assert.IsType<FileContentResult>(httpResult);
            var header = SkippedCount(controller);
            Assert.False(header == null,
                $"[{shape}] A file was produced but the skipped-count header is absent, so a client " +
                "cannot tell a complete manuscript from one with chapters missing.");
            Assert.True(chapters.Count - counted == int.Parse(header!),
                $"[{shape}] The count says {counted} of {chapters.Count} chapters are exportable, so " +
                $"{chapters.Count - counted} should be reported as skipped on the wire; the header says " +
                $"{header}. The header is what the export screen renders.");
        }
    }

    /// <summary>
    /// Seeds one book per shape and returns its id. Every shape is a real state of this product, reached the
    /// way the product reaches it - in particular a scene becomes "written" by being SAVED a second time
    /// (<c>UpdatedAt &gt; CreatedAt</c>), which is the rule <c>RenderableUnitsOf</c> reads, rather than by
    /// hand-stamping the timestamps that rule is defined on.
    /// </summary>
    private static async Task<Guid> SeedExportShapeAsync(AppDbContext db, string shape)
    {
        var book = new Book { Id = Guid.NewGuid(), Title = "מסע", Language = "he" };
        db.Books.Add(book);

        switch (shape)
        {
            case NoChapters:
                break;

            case EveryChapterExportable:
                db.Chapters.Add(NewChapter(book.Id, 0, "One", "The first chapter, really written."));
                db.Chapters.Add(NewChapter(book.Id, 1, "Two", "The second chapter, also written."));
                db.Chapters.Add(NewChapter(book.Id, 2, "Three", "The third chapter, written as well."));
                break;

            case NoChapterExportable:
                db.Chapters.Add(Imported(book.Id, 0, "פרק א"));
                db.Chapters.Add(Imported(book.Id, 1, "פרק ב"));
                db.Chapters.Add(Imported(book.Id, 2, "פרק ג"));
                break;

            case SomeChaptersExportable:
                db.Chapters.Add(NewChapter(book.Id, 0, "Written", "This one exists."));
                db.Chapters.Add(Imported(book.Id, 1, "Blank"));
                db.Chapters.Add(NewChapter(book.Id, 2, "Written again", "So does this one."));
                db.Chapters.Add(Imported(book.Id, 3, "Blank again"));
                break;

            case ScenesHoldTheText:
            {
                db.Chapters.Add(NewChapter(book.Id, 0, "Unsplit", "A chapter nobody ever split."));
                var split = Imported(book.Id, 1, "Split");
                db.Chapters.Add(split);
                await db.SaveChangesAsync();
                // Its own row is a frozen pre-split copy; the manuscript is in the scenes.
                await WriteScenesAsync(db, split.Id,
                    SfdtConversionService.CreateMinimalSfdtFromText("The first scene the author wrote."),
                    SfdtConversionService.CreateMinimalSfdtFromText("And the second one."));
                break;
            }

            case ScenesWrittenThenEmptied:
            {
                db.Chapters.Add(NewChapter(book.Id, 0, "Unsplit", "A chapter nobody ever split."));
                var emptied = Imported(book.Id, 1, "Split, then emptied");
                db.Chapters.Add(emptied);
                await db.SaveChangesAsync();
                // THE STATE BETWEEN THE TWO PREDICATES. The scenes speak for this chapter (they were saved
                // into) and hold nothing renderable, so it contributes no unit and must be REPORTED as
                // skipped rather than falling back to the pre-split draft the author replaced with nothing.
                await WriteScenesAsync(db, emptied.Id, EditorEmptiedSfdt, EditorEmptiedSfdt);
                break;
            }

            case OnlyAChapterWhoseScenesWereEmptied:
            {
                // The same state as the whole book, so it lands on NothingWritten rather than on a partial
                // skip: the all-or-nothing branch of the export loop, seeded through the scene layer.
                var only = Imported(book.Id, 0, "Split, then emptied");
                db.Chapters.Add(only);
                await db.SaveChangesAsync();
                await WriteScenesAsync(db, only.Id, EditorEmptiedSfdt);
                break;
            }

            default:
                throw new Xunit.Sdk.XunitException($"Unknown export shape '{shape}'.");
        }

        await db.SaveChangesAsync();
        return book.Id;
    }

    /// <summary>
    /// What the editor saves for a document the author has emptied, and <c>SceneService</c>'s own default for
    /// a scene created with no content: a well-formed SFDT whose single section holds no blocks. Not the
    /// entity default <c>"{}"</c> - these are different shapes of the same empty-document family and
    /// <c>HasRenderableContent</c> handles them on different branches.
    /// </summary>
    private const string EditorEmptiedSfdt = "{\"sections\":[{\"blocks\":[]}]}";

    /// <summary>
    /// Plain text and a word count over the entity-default document. This is the shape that made the
    /// word-count predicate and the exporter disagree, and it is SEEDED rather than produced: no production
    /// write reaches it, because ChapterService derives every WordCount from the SFDT it stores. The name
    /// records what the gate's books looked like on the row, not a path that writes them.
    /// </summary>
    private static Chapter Imported(Guid bookId, int order, string title) => new()
    {
        Id = Guid.NewGuid(),
        BookId = bookId,
        Order = order,
        Title = title,
        ContentText = "טקסט שיובא",
        WordCount = 120,
        ContentSfdt = "{}"
    };

    /// <summary>
    /// Adds scenes to a chapter and then SAVES them again with the given documents, which is what makes a
    /// scene count as written (<c>UpdatedAt &gt; CreatedAt</c>). The initial content is deliberately
    /// different from the final content in every case, so the second save is a real EF modification even
    /// when the author's edit emptied the scene.
    /// </summary>
    private static async Task WriteScenesAsync(AppDbContext db, Guid chapterId, params string[] finalSfdt)
    {
        var scenes = finalSfdt
            .Select((_, i) => new Scene
            {
                Id = Guid.NewGuid(),
                ChapterId = chapterId,
                Order = i,
                Title = $"Scene {i + 1}",
                ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("What the split put here.")
            })
            .ToList();
        db.Scenes.AddRange(scenes);
        await db.SaveChangesAsync();

        for (var i = 0; i < scenes.Count; i++) scenes[i].ContentSfdt = finalSfdt[i];
        await db.SaveChangesAsync();
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
        var (controller, db, _) = Build();
        return Task.FromResult((controller, db));
    }

    /// <summary>
    /// <see cref="BuildAsync"/> plus the export SERVICE itself, for the tests that need the
    /// <see cref="BookExportResult"/> rather than the HTTP projection of it - the skipped-chapter list is
    /// bounded on the wire (see the header cap in <c>DocumentController</c>), so an invariant stated over
    /// the header would be stating it about a truncation.
    /// </summary>
    private static (DocumentController Controller, AppDbContext Db, BookExportService Export) Build()
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
        return (controller, db, export);
    }
}
