using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;
using Harness = Pagedraft.Api.Tests.BookProofreadServiceTests.Harness;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-c05: the whole-book proofread STATUS and the annotated EXPORT decide "which Active proofread result is this
/// unit's, and is it unreliable" through ONE rule (<see cref="ProofreadUnitResults"/>), so the dialog that lists
/// the status's UnreliableChapters and promises to skip them is kept by the export's skip. Every cell runs both
/// sides over the same seeded database and asserts the agreement itself (listed chapters == skipped chapters),
/// plus the status split (coverage, ChaptersEdited, ChaptersUnreliable) the client words.
/// </summary>
public class BookProofreadExportAgreementTests
{
    private static readonly DateTimeOffset Edited = BookProofreadServiceTests.Edited;

    // ── (a) a scene-split chapter's frozen chapter row ─────────────────────────────────────────────────

    [Fact]
    public async Task SceneSplitChapter_AnUnreliableChapterRowResult_IsNeitherListedNorSkipped()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "פרק");
        var s1 = h.AddScene(chapter, 0, "אחת", written: true); // written: the scenes hold the text
        var s2 = h.AddScene(chapter, 1, "שתיים", written: false);
        AddResult(h, chapter, s1, Edited.AddMinutes(10));
        AddResult(h, chapter, s2, Edited.AddMinutes(10));
        // Archiving is keyed by scope + scene, so the row's own Chapter-scope result stays Active beside them.
        AddResult(h, chapter, null, Edited.AddMinutes(10), unreliable: true);

        var status = await Status(h);
        var counts = await ExportSkip(h);

        Assert.Empty(status.UnreliableChapters);
        Assert.Equal((1, 0, 0), (status.ChaptersWithActiveProofread, status.ChaptersEdited, status.ChaptersUnreliable));
        Assert.Equal(0, counts.UnreliableSkippedChapters);
        Assert.Equal(2, counts.Placed); // the two scenes annotated; the frozen row's suggestion is not in the file
    }

    [Fact]
    public async Task SceneSplitChapter_AnUnreliableSceneResult_IsListedAndSkipped()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "פרק");
        var s1 = h.AddScene(chapter, 0, "אחת", written: true);
        var s2 = h.AddScene(chapter, 1, "שתיים", written: false);
        AddResult(h, chapter, s1, Edited.AddMinutes(10));
        AddResult(h, chapter, s2, Edited.AddMinutes(10), unreliable: true);

        var status = await Status(h);
        var counts = await ExportSkip(h);

        Assert.Equal(chapter, Assert.Single(status.UnreliableChapters).ChapterId);
        Assert.Equal((0, 0, 1), (status.ChaptersWithActiveProofread, status.ChaptersEdited, status.ChaptersUnreliable));
        Assert.Equal(1, counts.UnreliableSkippedChapters);
        Assert.Equal(1, counts.Placed); // the reliable scene only
    }

    // ── (b) a result in another language ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnreliableResultInAnotherLanguage_IsNeitherListedNorSkipped_NorAnnotated()
    {
        using var h = new Harness(); // a Hebrew book
        var chapter = AddRenderableChapter(h, 0, "ראשון");
        AddResult(h, chapter, null, Edited.AddMinutes(1), unreliable: true, language: "en");

        var status = await Status(h);
        var skip = await ExportSkip(h);
        var include = await Export(h, UnreliableProofreadMode.Include);

        Assert.Empty(status.UnreliableChapters);
        Assert.Equal((0, 0, 0), (status.ChaptersWithActiveProofread, status.ChaptersEdited, status.ChaptersUnreliable));
        Assert.Equal(1, status.PlannedChapters); // not proofread in Hebrew, so the run plans it
        Assert.Equal(0, skip.UnreliableSkippedChapters);
        Assert.Equal(0, include.Placed); // the file holds the proofread the status describes: none
    }

    // ── (c) one unreliable chapter after a run ────────────────────────────────────────────────────────

    [Fact]
    public async Task OneUnreliableChapterAfterARun_IsUnreliableNotEdited_AndCoverageExcludesIt()
    {
        using var h = new Harness();
        var fine = AddRenderableChapter(h, 0, "ראשון");
        var broken = AddRenderableChapter(h, 1, "שני");
        // The model answers the second chapter with an unrelated continuation, so its result is flagged.
        h.Respond = input => input.Contains("שני")
            ? "Chapter 12: The story continues with completely different content and new characters, far away from the original text."
            : null;
        await BookProofreadServiceTests.StartAndFinish(h, h.Service());
        Assert.Equal(new[] { false, true }, // precondition: one reliable, one unreliable result
            h.ActiveProofreads().OrderBy(r => r.ChapterId == broken).Select(r => r.ProofreadResultUnreliable));

        var status = await Status(h);
        var counts = await ExportSkip(h);

        Assert.Equal(broken, Assert.Single(status.UnreliableChapters).ChapterId);
        Assert.Equal(1, status.ChaptersWithActiveProofread);
        Assert.Equal(0, status.ChaptersEdited);
        Assert.Equal(1, status.ChaptersUnreliable);
        Assert.Equal(1, status.ChaptersStale);
        Assert.Equal(h.ActiveProofreads().Single(r => r.ChapterId == fine).CreatedAt, status.LastRunAt);
        Assert.Equal(status.UnreliableChapters.Count, counts.UnreliableSkippedChapters);
    }

    // ── (d) an edited chapter ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AChapterEditedAfterItsReliableResult_IsEditedNotUnreliable()
    {
        using var h = new Harness();
        var chapter = AddRenderableChapter(h, 0, "ראשון");
        AddResult(h, chapter, null, Edited.AddMinutes(-1)); // older than the last edit

        var status = await Status(h);
        var counts = await ExportSkip(h);

        Assert.Equal((1, 1, 0, 1), (status.ChaptersWithActiveProofread, status.ChaptersEdited,
            status.ChaptersUnreliable, status.ChaptersStale));
        Assert.Empty(status.UnreliableChapters);
        Assert.Equal(0, counts.UnreliableSkippedChapters);
        Assert.Equal(1, counts.Placed); // edited is not unreliable: still annotated under skip
    }

    // ── fixture ───────────────────────────────────────────────────────────────────────────────────────

    private static Task<BookProofreadStatusDto> Status(Harness h) =>
        h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);

    private static Task<ExportAnnotationCounts> ExportSkip(Harness h) => Export(h, UnreliableProofreadMode.Skip);

    /// <summary>The whole book's annotated export over the harness's database, as the controller calls it.</summary>
    private static async Task<ExportAnnotationCounts> Export(Harness h, UnreliableProofreadMode mode)
    {
        using var db = h.Db();
        var sfdt = new SfdtConversionService();
        var export = new BookExportService(db, new ChapterService(db, HubContext(), sfdt), sfdt, new BookAssemblyService());
        var result = await export.ExportBookProofreadAsync(h.BookId, mode, CancellationToken.None);
        Assert.Equal(BookExportOutcome.Ok, result.Outcome);
        return result.Annotation!;
    }

    /// <summary><see cref="Harness.AddChapter"/> plus the SFDT the export renders (the harness seeds text only).</summary>
    private static Guid AddRenderableChapter(Harness h, int order, string marker)
    {
        var id = h.AddChapter(order, marker);
        using var db = h.Db();
        var chapter = db.Chapters.Single(c => c.Id == id);
        chapter.ContentSfdt = SfdtConversionService.CreateMinimalSfdtFromText(chapter.ContentText!);
        db.SaveChanges(); // sync: the timestamp stamping lives in SaveChangesAsync, so UpdatedAt stays Edited
        return id;
    }

    /// <summary>An Active Proofread result with one pending suggestion ("הבית", in every harness text).</summary>
    private static void AddResult(
        Harness h, Guid chapterId, Guid? sceneId, DateTimeOffset createdAt, bool unreliable = false, string language = "he")
    {
        using var db = h.Db();
        var result = new AnalysisResult
        {
            Id = Guid.NewGuid(), BookId = h.BookId, ChapterId = chapterId, SceneId = sceneId,
            Scope = sceneId.HasValue ? AnalysisScope.Scene : AnalysisScope.Chapter,
            AnalysisType = AnalysisType.Proofread, Status = AnalysisStatus.Active, Type = "Proofread",
            Language = language, CreatedAt = createdAt, ProofreadResultUnreliable = unreliable,
        };
        result.Suggestions.Add(new AnalysisSuggestion
        {
            Id = Guid.NewGuid(), AnalysisResultId = result.Id, OriginalText = "הבית", SuggestedText = "הביתה",
            StartOffset = 0, EndOffset = 4,
        });
        db.AnalysisResults.Add(result);
        db.SaveChanges();
    }

    private static IHubContext<BookSyncHub> HubContext()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<BookSyncHub>>();
        hub.SetupGet(x => x.Clients).Returns(clients.Object);
        return hub.Object;
    }
}
