using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Round-trip coverage for the SFDT the auto-split path emits per scene.
///
/// Regression: <see cref="SfdtConversionService.CreateMinimalSfdtFromText"/> used to emit a
/// hand-rolled minimal SFDT that <see cref="SfdtConversionService.GetTextFromSfdt"/>
/// (WordDocument.Save -> GetText) parsed back to EMPTY text. That empty text made the analyzer
/// reject auto-split scenes with "Scene has no content to analyze." These tests assert the
/// WRITE path (CreateMinimalSfdtFromText) and READ path (GetTextFromSfdt) round-trip to the
/// original text, for single-paragraph, multi-paragraph, and Hebrew/RTL input.
///
/// Note on the watermark: under a valid Syncfusion license (production, Program.cs registers one)
/// GetText() returns clean text. In the unlicensed test host, Syncfusion prepends a "Created with a
/// trial version..." banner. The analyzer ALWAYS pipes scene/chapter text through
/// SyncfusionWatermarkStripper before use, so these tests assert the same READ path the analyzer
/// uses: StripSyncfusionWatermark(GetTextFromSfdt(...)). Comparing both sides through the stripper
/// makes the assertion license-independent (it also collapses runs of whitespace/newlines).
/// </summary>
public class SfdtRoundTripAndSceneSplitTests
{
    private readonly SfdtConversionService _sfdt = new();

    /// <summary>The text the analyzer actually sees: GetTextFromSfdt output with the trial banner stripped.</summary>
    private string AnalyzerReadText(string sfdt)
        => SyncfusionWatermarkStripper.StripSyncfusionWatermark(_sfdt.GetTextFromSfdt(sfdt).PlainText);

    private static string Expected(string input)
        => SyncfusionWatermarkStripper.StripSyncfusionWatermark(TextNormalization.NormalizeTextForStorage(input));

    [Fact]
    public void CreateMinimalSfdtFromText_SingleParagraph_RoundTripsToNormalizedText()
    {
        var input = "The protagonist walked into the room and looked around carefully.";

        var sfdt = SfdtConversionService.CreateMinimalSfdtFromText(input);
        var (_, wordCount) = _sfdt.GetTextFromSfdt(sfdt);

        Assert.Equal(Expected(input), AnalyzerReadText(sfdt));
        Assert.True(wordCount > 0, $"Expected WordCount > 0, got {wordCount}");
    }

    [Fact]
    public void CreateMinimalSfdtFromText_MultiParagraph_RoundTripsToNormalizedText()
    {
        var input = "First paragraph of the scene body.\nSecond paragraph continues the action.\nThird and final paragraph closes it.";

        var sfdt = SfdtConversionService.CreateMinimalSfdtFromText(input);
        var (rawPlainText, wordCount) = _sfdt.GetTextFromSfdt(sfdt);
        var readText = AnalyzerReadText(sfdt);

        Assert.Equal(Expected(input), readText);
        Assert.True(wordCount > 0, $"Expected WordCount > 0, got {wordCount}");

        // All three paragraphs survived the round trip.
        Assert.Contains("First paragraph", readText);
        Assert.Contains("Second paragraph", readText);
        Assert.Contains("Third and final", readText);

        // Boundary assertion: paragraph structure must survive. Assert on the raw (pre-stripper)
        // PlainText to catch a regression where paragraphs are joined with no separator — a
        // collapse that would still pass the Contains checks above but loses the boundary.
        // "body.Second" can only appear if paragraphs 1 and 2 were concatenated with no whitespace.
        // Syncfusion emits '\r' or '\n' between paragraphs, so the raw text will always have at
        // least one of those between "body." and "Second" when structure is intact.
        Assert.DoesNotContain("body.Second", rawPlainText,
            StringComparison.Ordinal);
        // Similarly, paragraphs 2 and 3 must have a separator between them.
        Assert.DoesNotContain("action.Third", rawPlainText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateMinimalSfdtFromText_HebrewText_RoundTripsToNormalizedText()
    {
        var input = "רונית נכנסה לחדר והביטה סביבה.\nאלון עקב אחריה בשתיקה.";

        var sfdt = SfdtConversionService.CreateMinimalSfdtFromText(input);
        var (_, wordCount) = _sfdt.GetTextFromSfdt(sfdt);
        var readText = AnalyzerReadText(sfdt);

        Assert.Equal(Expected(input), readText);
        Assert.True(wordCount > 0, $"Expected WordCount > 0, got {wordCount}");
        Assert.Contains("רונית נכנסה לחדר", readText);
        Assert.Contains("אלון עקב אחריה", readText);
    }

    [Fact]
    public async Task SplitScenesFromChapterAsync_ProducesScenesWithNonEmptySfdtText()
    {
        await using var db = NewDb();
        var svc = new SceneService(db, BuildHubContext());

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        // Two scene bodies (each >= MinSceneContentLength) separated by an explicit *** marker
        // so SceneAutoSplitRules produces at least two scenes.
        var scene1 = "The first scene opens on a quiet street where nothing yet seems out of place.";
        var scene2 = "In the second scene the storm finally breaks and the whole town is forced to react.";
        var chapterText = $"{scene1}\n\n***\n\n{scene2}";

        db.Books.Add(new Book { Id = bookId, Title = "Split Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter One",
            ContentText = chapterText
        });
        await db.SaveChangesAsync();

        var created = await svc.SplitScenesFromChapterAsync(bookId, chapterId, CancellationToken.None);

        Assert.True(created.Count >= 2, $"Expected the *** marker to produce >= 2 scenes, got {created.Count}");

        foreach (var scene in created)
        {
            // GetTextFromSfdt must yield non-empty text, AND the analyzer's read path (after
            // stripping the trial banner) must still contain real content - not just the watermark.
            Assert.NotNull(scene.ContentSfdt);
            var (rawText, wordCount) = _sfdt.GetTextFromSfdt(scene.ContentSfdt!);
            Assert.False(string.IsNullOrWhiteSpace(rawText),
                $"Scene '{scene.Title}' produced empty text from GetTextFromSfdt - the analyzer would reject it.");
            Assert.True(wordCount > 0, $"Scene '{scene.Title}' produced WordCount {wordCount}");

            var analyzerText = SyncfusionWatermarkStripper.StripSyncfusionWatermark(rawText);
            Assert.False(string.IsNullOrWhiteSpace(analyzerText),
                $"Scene '{scene.Title}' had only the trial watermark and no real content for the analyzer.");
        }

        // The two source bodies landed in the produced scenes (read via the analyzer path).
        var allText = string.Join("\n",
            created.Select(s => SyncfusionWatermarkStripper.StripSyncfusionWatermark(_sfdt.GetTextFromSfdt(s.ContentSfdt!).PlainText)));
        Assert.Contains("first scene opens on a quiet street", allText);
        Assert.Contains("second scene the storm finally breaks", allText);
    }

    // ── Robustness / guard tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void CreateMinimalSfdtFromText_ControlCharInInput_DoesNotThrow_AndReturnsNonEmptySfdt()
    {
        // A raw STX (0x02) control character is invalid in XML 1.0 and will cause OpenXml /
        // Syncfusion to throw if not stripped.  The method must sanitize and return valid SFDT.
        var input = "Normal text\x02with a control char.";

        var sfdt = SfdtConversionService.CreateMinimalSfdtFromText(input);

        Assert.NotNull(sfdt);
        Assert.False(string.IsNullOrWhiteSpace(sfdt), "Expected a non-empty SFDT string.");
        // Must be parseable JSON (either the full round-trip or the empty-blocks fallback).
        Assert.True(sfdt.StartsWith("{") && sfdt.EndsWith("}"), $"SFDT does not look like JSON: {sfdt}");
    }

    [Fact]
    public void CreateMinimalSfdtFromText_MultipleControlChars_DoesNotThrow_AndReturnsNonEmptySfdt()
    {
        // Mix of several illegal XML control characters alongside normal (and Hebrew) text.
        var input = "\x01Hello\x0Bworld\x0C – \x1Fשלום\x00.";

        var sfdt = SfdtConversionService.CreateMinimalSfdtFromText(input);

        Assert.NotNull(sfdt);
        Assert.False(string.IsNullOrWhiteSpace(sfdt));
        Assert.True(sfdt.StartsWith("{") && sfdt.EndsWith("}"), $"SFDT does not look like JSON: {sfdt}");
    }

    [Fact]
    public async Task SplitScenesFromChapterAsync_ChapterWithControlChar_DoesNotThrow_AndReturnsBothScenes()
    {
        await using var db = NewDb();
        var svc = new SceneService(db, BuildHubContext());

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        // Embed a raw STX control char inside the chapter text to simulate corrupted source data.
        var scene1 = "The first scene opens on a quiet street\x02where nothing seems out of place.";
        var scene2 = "In the second scene the storm finally breaks\x02and the town reacts.";
        var chapterText = $"{scene1}\n\n***\n\n{scene2}";

        db.Books.Add(new Book { Id = bookId, Title = "Control Char Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter With Bad Chars",
            ContentText = chapterText
        });
        await db.SaveChangesAsync();

        // Must not throw.
        var created = await svc.SplitScenesFromChapterAsync(bookId, chapterId, CancellationToken.None);

        Assert.True(created.Count >= 2, $"Expected >= 2 scenes, got {created.Count}");
        foreach (var scene in created)
        {
            Assert.NotNull(scene.ContentSfdt);
            // SFDT must be valid JSON-shaped output (not a raw exception message).
            Assert.True(scene.ContentSfdt!.StartsWith("{") && scene.ContentSfdt.EndsWith("}"),
                $"Scene '{scene.Title}' ContentSfdt does not look like JSON: {scene.ContentSfdt}");
        }
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static IHubContext<BookSyncHub> BuildHubContext()
    {
        var clientProxy = new Mock<IClientProxy>();
        // SignalR's SendAsync(...) is an extension method over SendCoreAsync; mock the core
        // method to return a completed task so the hub broadcast inside the service does not NPE.
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
