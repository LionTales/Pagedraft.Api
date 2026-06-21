using System;
using System.Collections.Generic;
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
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for the "clear all scenes for a chapter" capability:
///   - Service: ClearScenesForChapterAsync
///   - Controller: DELETE api/books/{bookId}/chapters/{chapterId}/scenes (no sceneId segment)
/// </summary>
public class SceneClearServiceTests
{
    // ── helpers ──────────────────────────────────────────────────────────

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
        clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<BookSyncHub>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }

    private static async Task<(AppDbContext db, Guid bookId, Guid chapterId)> SeedChapterWithScenes(
        AppDbContext db, int sceneCount)
    {
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Test Book" });
        db.Chapters.Add(new Chapter
        {
            Id = chapterId,
            BookId = bookId,
            Title = "Chapter One",
            ContentText = "Some chapter text."
        });

        for (var i = 0; i < sceneCount; i++)
        {
            db.Scenes.Add(new Scene
            {
                Id = Guid.NewGuid(),
                ChapterId = chapterId,
                Title = $"Scene {i + 1}",
                Order = i,
                ContentSfdt = "{\"sections\":[{\"blocks\":[]}]}"
            });
        }

        await db.SaveChangesAsync();
        return (db, bookId, chapterId);
    }

    // ── service tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task ClearScenesForChapterAsync_RemovesAllScenesAndReturnsTrue()
    {
        await using var db = NewDb();
        var (_, bookId, chapterId) = await SeedChapterWithScenes(db, 3);
        var svc = new SceneService(db, BuildHubContext());

        var result = await svc.ClearScenesForChapterAsync(bookId, chapterId, CancellationToken.None);

        Assert.True(result);
        var remaining = await db.Scenes.Where(s => s.ChapterId == chapterId).CountAsync();
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task ClearScenesForChapterAsync_ChapterFromDifferentBook_ReturnsFalse()
    {
        await using var db = NewDb();
        var (_, _, chapterId) = await SeedChapterWithScenes(db, 2);
        var wrongBookId = Guid.NewGuid(); // chapter does NOT belong to this book
        var svc = new SceneService(db, BuildHubContext());

        var result = await svc.ClearScenesForChapterAsync(wrongBookId, chapterId, CancellationToken.None);

        Assert.False(result);
        // Scenes untouched — still 2 in the DB.
        var remaining = await db.Scenes.CountAsync();
        Assert.Equal(2, remaining);
    }

    [Fact]
    public async Task ClearScenesForChapterAsync_NonExistentChapter_ReturnsFalse()
    {
        await using var db = NewDb();
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Empty Book" });
        await db.SaveChangesAsync();
        var svc = new SceneService(db, BuildHubContext());

        var result = await svc.ClearScenesForChapterAsync(bookId, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ClearScenesForChapterAsync_EmitsScenesClearedSignalR()
    {
        await using var db = NewDb();
        var (_, bookId, chapterId) = await SeedChapterWithScenes(db, 2);

        var clientProxy = new Mock<IClientProxy>();
        string? emittedEventName = null;
        clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((name, _, _) => emittedEventName = name)
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hubContext = new Mock<IHubContext<BookSyncHub>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);

        var svc = new SceneService(db, hubContext.Object);

        await svc.ClearScenesForChapterAsync(bookId, chapterId, CancellationToken.None);

        Assert.Equal("ScenesCleared", emittedEventName);
    }

    [Fact]
    public async Task ClearScenesForChapterAsync_ChapterWithZeroScenes_ReturnsTrueAndDoesNothing()
    {
        await using var db = NewDb();
        var (_, bookId, chapterId) = await SeedChapterWithScenes(db, 0);
        var svc = new SceneService(db, BuildHubContext());

        var result = await svc.ClearScenesForChapterAsync(bookId, chapterId, CancellationToken.None);

        Assert.True(result);
        var remaining = await db.Scenes.Where(s => s.ChapterId == chapterId).CountAsync();
        Assert.Equal(0, remaining);
    }
}

public class SceneClearControllerTests
{
    // ── helpers ──────────────────────────────────────────────────────────

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
        clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<BookSyncHub>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }

    private static ScenesController BuildController(SceneService svc) => new(svc);

    // ── controller tests ──────────────────────────────────────────────────

    [Fact]
    public async Task ClearAll_WhenChapterExists_Returns204NoContent()
    {
        await using var db = NewDb();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Test Book" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch1", ContentText = "" });
        db.Scenes.Add(new Scene
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Title = "S1",
            Order = 0,
            ContentSfdt = "{\"sections\":[{\"blocks\":[]}]}"
        });
        await db.SaveChangesAsync();

        var svc = new SceneService(db, BuildHubContext());
        var controller = BuildController(svc);

        var result = await controller.ClearAll(bookId, chapterId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        // Scene was actually removed.
        Assert.Equal(0, await db.Scenes.CountAsync());
    }

    [Fact]
    public async Task ClearAll_WhenChapterDoesNotBelongToBook_Returns404()
    {
        await using var db = NewDb();
        var bookId = Guid.NewGuid();
        var anotherBookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Book A" });
        db.Books.Add(new Book { Id = anotherBookId, Title = "Book B" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = anotherBookId, Title = "Ch1", ContentText = "" });
        await db.SaveChangesAsync();

        var svc = new SceneService(db, BuildHubContext());
        var controller = BuildController(svc);

        // Call with bookId, but chapter belongs to anotherBookId.
        var result = await controller.ClearAll(bookId, chapterId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ClearAll_CallsServiceClearScenesForChapter()
    {
        // Verify the controller delegates to the service by checking the DB state
        // (integration-style, mirrors how existing controller tests work in this repo).
        await using var db = NewDb();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        db.Books.Add(new Book { Id = bookId, Title = "Test" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Title = "Ch", ContentText = "" });
        for (var i = 0; i < 3; i++)
        {
            db.Scenes.Add(new Scene
            {
                Id = Guid.NewGuid(),
                ChapterId = chapterId,
                Title = $"Scene {i}",
                Order = i,
                ContentSfdt = "{\"sections\":[{\"blocks\":[]}]}"
            });
        }
        await db.SaveChangesAsync();

        var svc = new SceneService(db, BuildHubContext());
        var controller = BuildController(svc);

        await controller.ClearAll(bookId, chapterId, CancellationToken.None);

        // All 3 scenes removed — confirms the controller delegated to the service correctly.
        Assert.Equal(0, await db.Scenes.CountAsync());
    }
}
