using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
using Pagedraft.Api.Services.Ai.Contracts;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The proofread-annotated exports (plan whole-book-proofread-and-annotated-docx-2026-10-11, c4), through the
/// controller so the wire answer (file name, the two annotation headers, the skipped-chapter headers, the 409s)
/// is what is asserted. The produced file is RELOADED with DocIO and its revisions and comments counted, the
/// claim being "the author's Word shows these suggestions", which neither a status code nor a header can carry.
/// </summary>
public class BookExportProofreadTests
{
    private const string ParagraphOne = "רוני הלכה לבית הספר היום בבוקר.";
    private const string ParagraphTwo = "היא פגשה את דני ליד השער הגדול.";
    private static readonly string ChapterText = ParagraphOne + "\n" + ParagraphTwo;

    // ─── chapter path ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportChapterProofread_PendingSuggestions_AreWrittenAsCommentsAndTrackedChanges()
    {
        var (controller, db, _) = Build();
        var (book, chapter) = await SeedChapterAsync(db, "he", "פרק ראשון", ChapterText);
        SeedResult(db, chapter, sceneId: null,
            Sug("לבית הספר", "לביה\"ס"),
            Sug("הגדול", "הגדולה"),
            Sug("מילה שאיננה בטקסט", "x")); // pending but unplaceable
        await db.SaveChangesAsync();

        var result = await controller.ExportChapterProofread(book.Id, chapter.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("פרק ראשון - הגהה.docx", file.FileDownloadName);
        Assert.Equal("2", Header(controller, BookExportService.AnnotationCountHeader));
        Assert.Equal("1", Header(controller, BookExportService.UnanchoredCountHeader));

        using var reloaded = Reload(file);
        Assert.Equal(2, reloaded.Comments.Count);
        Assert.Equal(4, reloaded.Revisions.Count);
        Assert.Equal(2, reloaded.Revisions.Cast<Revision>().Count(r => r.RevisionType == RevisionType.Deletions));
        Assert.Equal(2, reloaded.Revisions.Cast<Revision>().Count(r => r.RevisionType == RevisionType.Insertions));
    }

    [Fact]
    public async Task ExportChapterProofread_OnlyPendingSuggestionsOfTheActiveResult_AreWritten()
    {
        var (controller, db, _) = Build();
        var (book, chapter) = await SeedChapterAsync(db, "he", "פרק", ChapterText);
        // Accepted is already in the text, Dismissed is not a proposal any more: neither is written or counted.
        SeedResult(db, chapter, sceneId: null,
            Sug("לבית הספר", "לביה\"ס"),
            Sug("היום", "הערב", SuggestionOutcome.Accepted),
            Sug("השער", "הדלת", SuggestionOutcome.Dismissed));
        // An ARCHIVED result's pending-looking suggestion is history, not a proposal.
        SeedResult(db, chapter, sceneId: null, status: AnalysisStatus.Archived, suggestions: Sug("פגשה", "ראתה"));
        await db.SaveChangesAsync();

        var result = await controller.ExportChapterProofread(book.Id, chapter.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("1", Header(controller, BookExportService.AnnotationCountHeader));
        Assert.Equal("0", Header(controller, BookExportService.UnanchoredCountHeader));
        using var reloaded = Reload(file);
        Assert.Equal(1, reloaded.Comments.Count);
    }

    [Fact]
    public async Task ExportChapterProofread_NoProofreadResult_ShipsThePlainDocumentWithZeroCounts()
    {
        var (controller, db, _) = Build();
        var (book, chapter) = await SeedChapterAsync(db, "he", "פרק", ChapterText);

        var plain = Assert.IsType<FileContentResult>(await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None));
        // A plain export never carries the annotation headers: their presence means "this was annotated".
        Assert.Null(Header(controller, BookExportService.AnnotationCountHeader));
        Assert.Null(Header(controller, BookExportService.UnanchoredCountHeader));

        var (annotatedController, _, _) = Build(db);
        var annotated = Assert.IsType<FileContentResult>(
            await annotatedController.ExportChapterProofread(book.Id, chapter.Id, CancellationToken.None));

        Assert.Equal("0", Header(annotatedController, BookExportService.AnnotationCountHeader));
        Assert.Equal("0", Header(annotatedController, BookExportService.UnanchoredCountHeader));
        Assert.Null(Entry(annotated.FileContents, "word/comments.xml"));
        Assert.Equal(TextOf(plain), TextOf(annotated));
        using var reloaded = Reload(annotated);
        Assert.Equal(0, reloaded.Revisions.Count);
    }

    [Fact]
    public async Task ExportChapterProofread_EnglishBook_GetsTheEnglishSuffixAndCommentAuthor()
    {
        var (controller, db, _) = Build();
        var (book, chapter) = await SeedChapterAsync(db, "en", "Chapter One", "The cat sat on teh mat.\nIt was a sunny day.");
        SeedResult(db, chapter, sceneId: null, Sug("teh", "the"));
        await db.SaveChangesAsync();

        var file = Assert.IsType<FileContentResult>(
            await controller.ExportChapterProofread(book.Id, chapter.Id, CancellationToken.None));

        Assert.Equal("Chapter One - proofread.docx", file.FileDownloadName);
        using var reloaded = Reload(file);
        Assert.Equal("PageDraft Proofreader", reloaded.Comments[0].Format.User);
    }

    [Fact]
    public async Task ExportChapterProofread_UnwrittenChapter_Is409NothingWritten_And404ForAnUnknownChapter()
    {
        var (controller, db, _) = Build();
        var (book, _) = await SeedChapterAsync(db, "he", "פרק", ChapterText);
        var empty = new Chapter { Id = Guid.NewGuid(), BookId = book.Id, Order = 1, Title = "ריק", ContentSfdt = "{}" };
        db.Chapters.Add(empty);
        await db.SaveChangesAsync();

        var conflict = Assert.IsType<ConflictObjectResult>(
            await controller.ExportChapterProofread(book.Id, empty.Id, CancellationToken.None));
        Assert.Equal(ExportUnavailableDto.NothingWritten, Assert.IsType<ExportUnavailableDto>(conflict.Value).Reason);

        Assert.IsType<NotFoundResult>(await controller.ExportChapterProofread(book.Id, Guid.NewGuid(), CancellationToken.None));
    }

    // ─── scenes ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportChapterProofread_SceneSplitChapter_AnnotatesEachSceneFromItsOwnSceneScopeResult()
    {
        var (controller, db, scenes) = Build();
        var book = new Book { Id = Guid.NewGuid(), Title = "ספר", Language = "he" };
        db.Books.Add(book);
        const string sceneOne = "SCENEONE הגיבור יוצא מן הבית לפני עלות השחר והולך לאורך הדרך השקטה.";
        const string sceneTwo = "SCENETWO הסערה מגיעה אל החוף בערב והסירות נמשכות אל החול הרטוב.";
        var chapter = NewChapter(book.Id, 0, "פרק", sceneOne + "\n\n***\n\n" + sceneTwo);
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();
        var created = await scenes.SplitScenesFromChapterAsync(book.Id, chapter.Id);
        Assert.Equal(2, created.Count);

        // The author writes into scene 0, so the scenes now hold the chapter's text.
        var first = created.OrderBy(s => s.Order).First();
        var second = created.OrderBy(s => s.Order).Last();
        var written = await scenes.UpdateAsync(book.Id, chapter.Id, first.Id, title: null, order: null,
            contentSfdt: SfdtConversionService.CreateMinimalSfdtFromText(sceneOne + " ואז עצר."));
        Assert.True(written!.UpdatedAt > written.CreatedAt);

        SeedResult(db, chapter, first.Id, Sug("השקטה", "השוקטת"));
        SeedResult(db, chapter, second.Id, Sug("הרטוב", "הלח"), Sug("בערב", "בלילה"));
        // A Chapter-scope result on the chapter row: the row is a frozen pre-split copy, so it is not the unit.
        SeedResult(db, chapter, sceneId: null, Sug("הגיבור", "הגיבורה"));
        await db.SaveChangesAsync();

        var file = Assert.IsType<FileContentResult>(
            await controller.ExportChapterProofread(book.Id, chapter.Id, CancellationToken.None));

        Assert.Equal("3", Header(controller, BookExportService.AnnotationCountHeader));
        Assert.Equal("0", Header(controller, BookExportService.UnanchoredCountHeader));
        using var reloaded = Reload(file);
        Assert.Equal(3, reloaded.Comments.Count);
        Assert.Equal(6, reloaded.Revisions.Count);
        // The chapter-row suggestion was not applied: its original word is still plain, un-deleted text.
        foreach (var revision in reloaded.Revisions.Cast<Revision>().ToList()) revision.Accept();
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(reloaded.GetText());
        Assert.Contains("הגיבור יוצא", text, StringComparison.Ordinal);
        Assert.Contains("השוקטת", text, StringComparison.Ordinal);
        Assert.Contains("בלילה", text, StringComparison.Ordinal);
        Assert.Contains("הלח", text, StringComparison.Ordinal);
    }

    // ─── book path ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportBookProofread_SumsEveryChapter_AndStillReportsTheSkippedChapter()
    {
        var (controller, db, _) = Build();
        var (book, one) = await SeedChapterAsync(db, "he", "אחד", ChapterText, bookTitle: "הספר שלי");
        var two = NewChapter(book.Id, 1, "שניים", "הוא הלך הביתה ואמר שלום לכולם.\nבערב ירד גשם חזק על העיר.");
        var unwritten = new Chapter { Id = Guid.NewGuid(), BookId = book.Id, Order = 2, Title = "ריק", ContentSfdt = "{}" };
        db.Chapters.AddRange(two, unwritten);
        SeedResult(db, one, sceneId: null, Sug("לבית הספר", "לביה\"ס"), Sug("לא קיים כאן", "y"));
        SeedResult(db, two, sceneId: null, Sug("שלום", "שלומות"), Sug("חזק ", ""));
        await db.SaveChangesAsync();

        var file = Assert.IsType<FileContentResult>(await controller.ExportBookProofread(book.Id, CancellationToken.None));

        Assert.Equal("הספר שלי - הגהה.docx", file.FileDownloadName);
        Assert.Equal("3", Header(controller, BookExportService.AnnotationCountHeader));
        Assert.Equal("1", Header(controller, BookExportService.UnanchoredCountHeader));
        Assert.Equal("1", Header(controller, BookExportService.SkippedCountHeader));
        Assert.NotNull(Header(controller, BookExportService.SkippedChaptersHeader));

        using var reloaded = Reload(file);
        Assert.Equal(3, reloaded.Comments.Count);
        Assert.Equal(5, reloaded.Revisions.Count); // (del + ins) x2 + del
    }

    [Fact]
    public async Task ExportBookProofread_KeepsThePlainPathsEmptyBookAnswers()
    {
        var (controller, db, _) = Build();
        Assert.IsType<NotFoundResult>(await controller.ExportBookProofread(Guid.NewGuid(), CancellationToken.None));

        var book = new Book { Id = Guid.NewGuid(), Title = "ריק", Language = "he" };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        var conflict = Assert.IsType<ConflictObjectResult>(await controller.ExportBookProofread(book.Id, CancellationToken.None));
        Assert.Equal(ExportUnavailableDto.NoChapters, Assert.IsType<ExportUnavailableDto>(conflict.Value).Reason);
    }

    [Fact]
    public void BuildFileName_PutsTheSuffixAfterTheCappedStem()
    {
        var longTitle = new string('א', 120);
        var name = BookExportService.BuildFileName(longTitle, "book", " - הגהה");
        Assert.Equal(new string('א', 80) + " - הגהה.docx", name);
        Assert.Equal("book - proofread.docx", BookExportService.BuildFileName("   ", "book", " - proofread"));
        Assert.Equal("x.docx", BookExportService.BuildFileName("x", "book"));
    }

    // ─── fixture ──────────────────────────────────────────────────────────────────────────────────

    private static AnalysisSuggestion Sug(string original, string suggested, SuggestionOutcome? outcome = null) => new()
    {
        Id = Guid.NewGuid(),
        OriginalText = original,
        SuggestedText = suggested,
        StartOffset = 0,
        EndOffset = original.Length,
        Outcome = outcome,
    };

    private static void SeedResult(AppDbContext db, Chapter chapter, Guid? sceneId, params AnalysisSuggestion[] suggestions)
        => SeedResult(db, chapter, sceneId, AnalysisStatus.Active, suggestions);

    private static void SeedResult(
        AppDbContext db, Chapter chapter, Guid? sceneId, AnalysisStatus status, params AnalysisSuggestion[] suggestions)
    {
        var result = new AnalysisResult
        {
            Id = Guid.NewGuid(),
            BookId = chapter.BookId,
            ChapterId = chapter.Id,
            SceneId = sceneId,
            Scope = sceneId == null ? AnalysisScope.Chapter : AnalysisScope.Scene,
            AnalysisType = AnalysisType.Proofread,
            Type = nameof(AnalysisType.Proofread),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var order = 0;
        foreach (var s in suggestions)
        {
            s.AnalysisResultId = result.Id;
            s.OrderIndex = order++;
            result.Suggestions.Add(s);
        }
        db.AnalysisResults.Add(result);
    }

    private static async Task<(Book Book, Chapter Chapter)> SeedChapterAsync(
        AppDbContext db, string language, string title, string text, string? bookTitle = null)
    {
        var book = new Book { Id = Guid.NewGuid(), Title = bookTitle ?? "ספר", Language = language };
        db.Books.Add(book);
        var chapter = NewChapter(book.Id, 0, title, text);
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();
        return (book, chapter);
    }

    private static Chapter NewChapter(Guid bookId, int order, string title, string text) => new()
    {
        Id = Guid.NewGuid(),
        BookId = bookId,
        Order = order,
        Title = title,
        ContentText = text,
        WordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
        ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText(text),
    };

    private static string? Header(ControllerBase controller, string name) =>
        controller.Response.Headers[name].ToString() is { Length: > 0 } v ? v : null;

    private static WordDocument Reload(FileContentResult file) =>
        new(new MemoryStream(file.FileContents), FormatType.Docx);

    private static string TextOf(FileContentResult file)
    {
        using var document = Reload(file);
        return SyncfusionWatermarkStripper.StripSyncfusionWatermark(document.GetText());
    }

    private static ZipArchiveEntry? Entry(byte[] docx, string name)
    {
        var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        return zip.GetEntry(name);
    }

    private static (DocumentController Controller, AppDbContext Db, SceneService Scenes) Build(AppDbContext? existing = null)
    {
        var db = existing ?? new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var hub = BuildHubContext();
        var sfdt = new SfdtConversionService();
        var chapters = new ChapterService(db, hub, sfdt);
        var assembly = new BookAssemblyService();
        var export = new BookExportService(db, chapters, sfdt, assembly);

        var controller = new DocumentController(new DocxParserService(), sfdt, chapters, assembly, export)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, db, new SceneService(db, hub));
    }

    private static IHubContext<BookSyncHub> BuildHubContext()
    {
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hubContext = new Mock<IHubContext<BookSyncHub>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }
}
