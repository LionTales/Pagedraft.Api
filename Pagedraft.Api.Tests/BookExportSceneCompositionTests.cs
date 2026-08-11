using System;
using System.Collections.Generic;
using System.IO;
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
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// WHICH COPY OF A CHAPTER AN EXPORT SHIPS, on both document paths.
///
/// A chapter that has been split into scenes has TWO independently writable stores for one chapter's prose,
/// and nothing in this product copies between them: <c>SplitScenesFromChapterAsync</c> reads
/// <c>Chapter.ContentText</c> and writes scene rows, and from that moment <c>Chapter.ContentSfdt</c> is a
/// frozen pre-split copy while the editor saves into whichever unit is selected
/// (<c>editor-page.component.ts</c> saves to the scene when one is open, to the chapter otherwise). Both
/// export paths read the chapter row only, so every scene edit made after a split was invisible to export and
/// the author downloaded the pre-split draft.
///
/// The rule these tests pin is deliberately CONDITIONAL, and the second test is the reason: the split is
/// lossy (it slices plain text, deletes the break markers, and discards any segment under
/// <c>SceneAutoSplitRules.MinSceneContentLength</c>), so preferring scenes unconditionally would strip
/// formatting and drop text out of the manuscript of an author who never touched a scene. Scenes win only
/// once the author has written into one.
///
/// THE ARTIFACT IS INSPECTED, NOT PROXIED. Every export assertion here reads the TEXT back out of the
/// produced .docx through DocIO, because "the file contains the new paragraph and not the old one" is the
/// claim, and a byte count or a status code cannot carry it. The unlicensed test host prepends a Syncfusion
/// trial banner to extracted text, so the reads go through the same
/// <see cref="SyncfusionWatermarkStripper"/> the analyzer uses.
/// </summary>
public class BookExportSceneCompositionTests
{
    // ─── The reported defect, both paths ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportBook_SceneWrittenAfterTheSplit_ShipsTheSceneText_NotThePreSplitDraft()
    {
        var (controller, db, scenes) = await BuildAsync();
        var (book, chapter) = await SeedSplitChapterAsync(db, scenes);

        await WriteSceneAsync(db, scenes, book.Id, chapter.Id, sceneOrder: 0, text: RewrittenFirstScene);

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var text = TextOf(Assert.IsType<FileContentResult>(result));
        Assert.Contains(Marker(RewrittenFirstScene), text, StringComparison.Ordinal);
        // The pre-split draft is what the chapter row still holds, and it is a whole editing session behind.
        Assert.DoesNotContain(Marker(OriginalFirstScene), text, StringComparison.Ordinal);
        // The scenes the author did NOT rewrite are still in the file: composing means all of them, in order.
        Assert.Contains(Marker(OriginalSecondScene), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportChapter_SceneWrittenAfterTheSplit_ShipsTheSceneText_NotThePreSplitDraft()
    {
        // THE OTHER DOCUMENT PATH. Book-level and single-chapter export are the standing drift trap in this
        // codebase and the export screen exposes both, so the identical situation gets the identical answer.
        var (controller, db, scenes) = await BuildAsync();
        var (book, chapter) = await SeedSplitChapterAsync(db, scenes);

        await WriteSceneAsync(db, scenes, book.Id, chapter.Id, sceneOrder: 0, text: RewrittenFirstScene);

        var result = await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("פרק ראשון.docx", file.FileDownloadName);
        var text = TextOf(file);
        Assert.Contains(Marker(RewrittenFirstScene), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker(OriginalFirstScene), text, StringComparison.Ordinal);
        Assert.Contains(Marker(OriginalSecondScene), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportBook_ComposesTheScenesInSceneOrder_NotInWhateverOrderTheyWereWritten()
    {
        // The composition has to reproduce the author's reading order, which is Scene.Order - the order the
        // split assigns and the editor's tree renders - and NOT the order the rows happen to come back in or
        // the order they were last edited. So the LATER scene is rewritten first here.
        var (controller, db, scenes) = await BuildAsync();
        var (book, chapter) = await SeedSplitChapterAsync(db, scenes);

        await WriteSceneAsync(db, scenes, book.Id, chapter.Id, sceneOrder: 1, text: RewrittenSecondScene);
        await WriteSceneAsync(db, scenes, book.Id, chapter.Id, sceneOrder: 0, text: RewrittenFirstScene);

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var text = TextOf(Assert.IsType<FileContentResult>(result));
        var first = text.IndexOf(Marker(RewrittenFirstScene), StringComparison.Ordinal);
        var second = text.IndexOf(Marker(RewrittenSecondScene), StringComparison.Ordinal);
        Assert.True(first >= 0, "The first scene is missing from the exported document.");
        Assert.True(second >= 0, "The second scene is missing from the exported document.");
        Assert.True(first < second, $"Scenes came out in the wrong order (scene 0 at {first}, scene 1 at {second}).");
    }

    // ─── The conditional half: an untouched scene layer must not replace the chapter ──────────────

    [Fact]
    public async Task ExportBook_SplitButNoSceneWritten_StillShipsTheChaptersOwnDocument_LosslesslY()
    {
        // THE ANTI-REGRESSION SIDE, and the reason the rule is not "scenes whenever there are scenes".
        // SceneAutoSplitRules discards any segment shorter than MinSceneContentLength and deletes the break
        // markers, so right after a split the scenes are a strictly poorer copy of the same prose. An
        // unconditional preference would drop this chapter's short closing line out of the manuscript of an
        // author who never opened a scene.
        var (controller, db, scenes) = await BuildAsync();
        var (book, _) = await SeedSplitChapterAsync(db, scenes);

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        var text = TextOf(Assert.IsType<FileContentResult>(result));
        Assert.Contains(Marker(OriginalFirstScene), text, StringComparison.Ordinal);
        Assert.Contains(Marker(OriginalSecondScene), text, StringComparison.Ordinal);
        Assert.Contains(ShortTailTheSplitDrops, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSplitReallyDoesDropThatLine_SoTheTestAboveIsNotVacuous()
    {
        // Non-vacuity for the assertion above: if the split ever became lossless, the "still ships the
        // chapter's own document" test would pass for the wrong reason. This pins the premise at its source.
        var (_, db, scenes) = await BuildAsync();
        var (book, chapter) = await SeedSplitChapterAsync(db, scenes);

        var stored = await db.Scenes.Where(s => s.ChapterId == chapter.Id).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, s => Assert.DoesNotContain(
            ShortTailTheSplitDrops,
            SyncfusionWatermarkStripper.StripSyncfusionWatermark(new SfdtConversionService().GetTextFromSfdt(s.ContentSfdt!).PlainText),
            StringComparison.Ordinal));
        Assert.NotEqual(Guid.Empty, book.Id);
    }

    // ─── The empty cases, which must stay honest rather than fall back ────────────────────────────

    [Fact]
    public async Task ExportBook_ScenesHoldTheWritingButAreAllBlank_IsReportedAsASkippedChapter()
    {
        // The author emptied the scenes. Falling back to the pre-split chapter row here would resurrect text
        // they deleted, which is the same lie as shipping a stale draft, pointed the other way. The chapter
        // contributes nothing and is NAMED through be-c02's skipped-chapter headers.
        var (controller, db, scenes) = await BuildAsync();
        var (book, chapter) = await SeedSplitChapterAsync(db, scenes);
        db.Chapters.Add(NewChapter(book.Id, order: 1, title: "פרק שני", text: "This chapter was never split and still has its text."));
        await db.SaveChangesAsync();

        await WriteSceneAsync(db, scenes, book.Id, chapter.Id, sceneOrder: 0, sfdt: EmptySceneSfdt);
        await WriteSceneAsync(db, scenes, book.Id, chapter.Id, sceneOrder: 1, sfdt: EmptySceneSfdt);

        var result = await controller.ExportBook(book.Id, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal("1", controller.Response.Headers[BookExportService.SkippedCountHeader].ToString());
        // Decoded exactly as the client is told to read it: JSON.parse(decodeURIComponent(value)).
        var named = System.Text.Json.JsonSerializer.Deserialize<List<SkippedChapterOnTheWire>>(
            Uri.UnescapeDataString(controller.Response.Headers[BookExportService.SkippedChaptersHeader].ToString()),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("פרק ראשון", Assert.Single(named).Title);

        // ... and the chapter that was never split is still in the file, so the skip is about this chapter and
        // not about the export having given up.
        Assert.Contains("never split", TextOf((FileContentResult)result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportChapter_ScenesHoldTheWritingButAreAllBlank_IsTheSameConflictAsAnUnwrittenChapter()
    {
        var (controller, db, scenes) = await BuildAsync();
        var (book, chapter) = await SeedSplitChapterAsync(db, scenes);

        await WriteSceneAsync(db, scenes, book.Id, chapter.Id, sceneOrder: 0, sfdt: EmptySceneSfdt);
        await WriteSceneAsync(db, scenes, book.Id, chapter.Id, sceneOrder: 1, sfdt: EmptySceneSfdt);

        var result = await controller.ExportChapter(book.Id, chapter.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("nothingWritten", Assert.IsType<ExportUnavailableDto>(conflict.Value).Reason);
    }

    // ─── The decision itself ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenesHoldTheChaptersCurrentText_IsFalseForASceneLayerNobodyHasWrittenIn()
    {
        var born = DateTimeOffset.UtcNow;

        Assert.False(BookExportService.ScenesHoldTheChaptersCurrentText(null));
        Assert.False(BookExportService.ScenesHoldTheChaptersCurrentText(Array.Empty<Scene>()));
        // Exactly what SplitScenesFromChapterAsync leaves behind: added in one SaveChanges, so the override
        // (AppDbContext.cs) stamps CreatedAt and UpdatedAt equal.
        Assert.False(BookExportService.ScenesHoldTheChaptersCurrentText(new[]
        {
            new Scene { CreatedAt = born, UpdatedAt = born },
            new Scene { CreatedAt = born, UpdatedAt = born }
        }));
    }

    [Fact]
    public void ScenesHoldTheChaptersCurrentText_IsTrueAsSoonAsONESceneHasBeenWritten()
    {
        var born = DateTimeOffset.UtcNow;

        Assert.True(BookExportService.ScenesHoldTheChaptersCurrentText(new[]
        {
            new Scene { CreatedAt = born, UpdatedAt = born },
            new Scene { CreatedAt = born, UpdatedAt = born.AddSeconds(1) }
        }));
    }

    [Fact]
    public void RenderableUnitsOf_ReturnsTheChapterWhenTheSceneLayerIsUntouched_AndTheScenesWhenItIsNot()
    {
        var born = DateTimeOffset.UtcNow;
        var chapterSfdt = SfdtConversionService.CreateMinimalSfdtFromText("The chapter's own stored document.");
        var chapter = new Chapter { Id = Guid.NewGuid(), Title = "פרק", ContentSfdt = chapterSfdt };

        var untouched = new[]
        {
            new Scene { Order = 0, CreatedAt = born, UpdatedAt = born, ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText("scene one") }
        };
        Assert.Equal(new[] { chapterSfdt }, BookExportService.RenderableUnitsOf(chapter, untouched));

        var written = new[]
        {
            new Scene { Order = 0, CreatedAt = born, UpdatedAt = born.AddSeconds(1), ContentSfdt = "SCENE-ONE" },
            // A blank scene inside a written layer contributes nothing rather than making the chapter fall
            // back: the guard that drops it is the same one the chapter row goes through.
            new Scene { Order = 1, CreatedAt = born, UpdatedAt = born, ContentSfdt = EmptySceneSfdt }
        };
        Assert.Equal(new[] { "SCENE-ONE" }, BookExportService.RenderableUnitsOf(chapter, written));
    }

    // ─── fixture ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Declared here rather than reused from the API so the wire shape is asserted, not assumed.</summary>
    private sealed record SkippedChapterOnTheWire(int Order, string Title);

    /// <summary>SceneService's own empty document, i.e. what a scene with nothing in it stores.</summary>
    private const string EmptySceneSfdt = "{\"sections\":[{\"blocks\":[]}]}";

    /// <summary>
    /// A short closing line the auto-split DISCARDS (it is under MinSceneContentLength = 50 characters and
    /// follows the last break), which is what makes "the scenes are a poorer copy" a fact rather than a claim.
    /// </summary>
    private const string ShortTailTheSplitDrops = "Curtain.";

    private const string OriginalFirstScene =
        "SCENEONEORIGINAL The hero leaves the house before dawn and walks the length of the quiet road.";
    private const string OriginalSecondScene =
        "SCENETWOORIGINAL The storm arrives at the shore that evening and the boats are pulled up the sand.";
    private const string RewrittenFirstScene =
        "SCENEONEREWRITTEN The hero waits until the sun is up, and takes the long road on purpose.";
    private const string RewrittenSecondScene =
        "SCENETWOREWRITTEN The storm turns north before it lands, and nothing on the shore moves at all.";

    /// <summary>The first word of a passage: one token that is present or absent, with no whitespace or
    /// line-wrap variation to make the assertion fragile.</summary>
    private static string Marker(string passage) => passage.Split(' ')[0];

    /// <summary>
    /// A chapter written, then split into scenes by the REAL auto-split, with nothing written since. This is
    /// the shape every scene-split chapter in the product starts from.
    /// </summary>
    private static async Task<(Book Book, Chapter Chapter)> SeedSplitChapterAsync(AppDbContext db, SceneService scenes)
    {
        var book = new Book { Id = Guid.NewGuid(), Title = "מסע הגיבור", Language = "he" };
        db.Books.Add(book);

        var text = OriginalFirstScene + "\n\n***\n\n" + OriginalSecondScene + "\n\n***\n\n" + ShortTailTheSplitDrops;
        var chapter = NewChapter(book.Id, order: 0, title: "פרק ראשון", text: text);
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();

        var created = await scenes.SplitScenesFromChapterAsync(book.Id, chapter.Id);
        Assert.Equal(2, created.Count);

        // The split Adds every scene in one SaveChanges, so the override stamps CreatedAt and UpdatedAt equal
        // and the layer reads as "nobody has written here". Asserted rather than assumed: if that ever stopped
        // being true, every test in this file would be exercising the wrong starting state and passing.
        Assert.All(created, s => Assert.False(
            s.UpdatedAt > s.CreatedAt,
            $"The fixture must start from an unwritten scene layer, but scene {s.Order} has UpdatedAt {s.UpdatedAt:o} > CreatedAt {s.CreatedAt:o}."));

        return (book, chapter);
    }

    /// <summary>An editor save into one scene, through the real service, so the timestamp bump is the
    /// production one and not a value the test chose.</summary>
    private static async Task WriteSceneAsync(
        AppDbContext db, SceneService scenes, Guid bookId, Guid chapterId, int sceneOrder, string? text = null, string? sfdt = null)
    {
        var scene = await db.Scenes.FirstAsync(s => s.ChapterId == chapterId && s.Order == sceneOrder);
        var content = sfdt ?? SfdtConversionService.CreateMinimalSfdtFromText(text!);

        var updated = await scenes.UpdateAsync(bookId, chapterId, scene.Id, title: null, order: null, contentSfdt: content);

        Assert.NotNull(updated);
        Assert.True(updated!.UpdatedAt > updated.CreatedAt,
            "The save did not move UpdatedAt past CreatedAt, so the fixture cannot distinguish a written scene from a split one.");
    }

    /// <summary>The text a reader would see in the downloaded file, read back out of the real .docx.</summary>
    private static string TextOf(FileContentResult file)
    {
        using var stream = new MemoryStream(file.FileContents);
        using var document = new Syncfusion.DocIO.DLS.WordDocument(stream, Syncfusion.DocIO.FormatType.Docx);
        return SyncfusionWatermarkStripper.StripSyncfusionWatermark(document.GetText());
    }

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

    private static Task<(DocumentController Controller, AppDbContext Db, SceneService Scenes)> BuildAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);

        var hub = BuildHubContext();
        var sfdt = new SfdtConversionService();
        var chapters = new ChapterService(db, hub, sfdt);
        var assembly = new BookAssemblyService();
        var export = new BookExportService(db, chapters, sfdt, assembly);
        var scenes = new SceneService(db, hub);

        var controller = new DocumentController(new DocxParserService(), sfdt, chapters, assembly, export)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return Task.FromResult((controller, db, scenes));
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
