using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Hubs.Events;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services;

public class SceneService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<BookSyncHub> _hubContext;

    public SceneService(AppDbContext db, IHubContext<BookSyncHub> hubContext)
    {
        _db = db;
        _hubContext = hubContext;
    }

    public async Task<List<Scene>> GetAllByChapterAsync(Guid bookId, Guid chapterId, CancellationToken ct = default)
    {
        await EnsureChapterBelongsToBook(bookId, chapterId, ct);
        return await _db.Scenes
            .Where(s => s.ChapterId == chapterId)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);
    }

    public async Task<Scene?> GetByIdAsync(Guid bookId, Guid chapterId, Guid sceneId, CancellationToken ct = default)
    {
        await EnsureChapterBelongsToBook(bookId, chapterId, ct);
        return await _db.Scenes
            .FirstOrDefaultAsync(s => s.ChapterId == chapterId && s.Id == sceneId, ct);
    }

    public async Task<Scene?> CreateAsync(Guid bookId, Guid chapterId, string title, int? order, string? contentSfdt, CancellationToken ct = default)
    {
        var chapter = await _db.Chapters
            .Include(c => c.Scenes)
            .FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return null;

        var existingOrders = chapter.Scenes.Select(s => s.Order).ToList();
        var nextOrder = order ?? (existingOrders.Count == 0 ? 0 : existingOrders.Max() + 1);
        if (existingOrders.Count > 0)
        {
            var used = new HashSet<int>(existingOrders);
            while (used.Contains(nextOrder)) nextOrder++;
        }

        var sfdt = !string.IsNullOrWhiteSpace(contentSfdt) ? contentSfdt : "{\"sections\":[{\"blocks\":[]}]}";
        var scene = new Scene
        {
            ChapterId = chapterId,
            Title = title,
            Order = nextOrder,
            ContentSfdt = sfdt
        };
        _db.Scenes.Add(scene);
        await _db.SaveChangesAsync(ct);

        await _hubContext.Clients.Group($"book:{bookId}")
            .SendAsync("SceneCreated", new SceneCreatedEvent(bookId, chapterId, scene.Id, scene.Title, scene.Order), ct);
        return scene;
    }

    public async Task<Scene?> UpdateAsync(Guid bookId, Guid chapterId, Guid sceneId, string? title, int? order, string? contentSfdt, CancellationToken ct = default)
    {
        var scene = await _db.Scenes
            .FirstOrDefaultAsync(s => s.ChapterId == chapterId && s.Id == sceneId, ct);
        if (scene == null) return null;

        if (title != null) scene.Title = title;
        if (order.HasValue) scene.Order = order.Value;
        if (contentSfdt != null) scene.ContentSfdt = contentSfdt;
        await _db.SaveChangesAsync(ct);

        await _hubContext.Clients.Group($"book:{bookId}")
            .SendAsync("SceneUpdated", new SceneUpdatedEvent(bookId, chapterId, sceneId, scene.UpdatedAt), ct);
        return scene;
    }

    public async Task<bool> DeleteAsync(Guid bookId, Guid chapterId, Guid sceneId, CancellationToken ct = default)
    {
        var scene = await _db.Scenes.FirstOrDefaultAsync(s => s.ChapterId == chapterId && s.Id == sceneId, ct);
        if (scene == null) return false;

        _db.Scenes.Remove(scene);
        await _db.SaveChangesAsync(ct);

        await _hubContext.Clients.Group($"book:{bookId}")
            .SendAsync("SceneDeleted", new SceneDeletedEvent(bookId, chapterId, sceneId), ct);
        return true;
    }

    public async Task<List<Scene>?> ReorderAsync(Guid bookId, Guid chapterId, List<(Guid SceneId, int Order)> newOrder, CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.Include(c => c.Scenes).FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return null;

        var map = newOrder.ToDictionary(x => x.SceneId, x => x.Order);
        const int TempOffset = 10_000;

        foreach (var s in chapter.Scenes)
        {
            if (map.TryGetValue(s.Id, out var targetOrder))
                s.Order = TempOffset + targetOrder;
        }
        await _db.SaveChangesAsync(ct);

        foreach (var s in chapter.Scenes)
        {
            if (map.TryGetValue(s.Id, out var targetOrder))
                s.Order = targetOrder;
        }
        await _db.SaveChangesAsync(ct);

        var list = newOrder.Select(x => new SceneOrderItem(x.SceneId, x.Order)).ToList();
        await _hubContext.Clients.Group($"book:{bookId}")
            .SendAsync("ScenesReordered", new ScenesReorderedEvent(bookId, chapterId, list), ct);

        return await GetAllByChapterAsync(bookId, chapterId, ct);
    }

    /// <summary>
    /// Auto-split chapter content into scenes using SceneAutoSplitRules, replace existing scenes.
    /// </summary>
    public async Task<List<Scene>> SplitScenesFromChapterAsync(Guid bookId, Guid chapterId, CancellationToken ct = default)
    {
        var chapter = await _db.Chapters
            .Include(c => c.Scenes)
            .FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null)
            throw new InvalidOperationException("Chapter not found.");

        var text = chapter.ContentText ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return await GetAllByChapterAsync(bookId, chapterId, ct);

        var scenes = SceneAutoSplitRules.Split(text, chapter.Title);

        _db.Scenes.RemoveRange(chapter.Scenes);
        await _db.SaveChangesAsync(ct);

        var created = new List<Scene>();
        for (var i = 0; i < scenes.Count; i++)
        {
            var (title, content) = scenes[i];
            var sfdt = SfdtConversionService.CreateMinimalSfdtFromText(content);
            var scene = new Scene
            {
                ChapterId = chapterId,
                Title = title,
                Order = i,
                ContentSfdt = sfdt
            };
            _db.Scenes.Add(scene);
            created.Add(scene);
        }
        await _db.SaveChangesAsync(ct);

        foreach (var scene in created)
        {
            await _hubContext.Clients.Group($"book:{bookId}")
                .SendAsync("SceneCreated", new SceneCreatedEvent(bookId, chapterId, scene.Id, scene.Title, scene.Order), ct);
        }

        return created;
    }

    private async Task EnsureChapterBelongsToBook(Guid bookId, Guid chapterId, CancellationToken ct)
    {
        var exists = await _db.Chapters.AnyAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (!exists)
            throw new InvalidOperationException("Chapter not found for this book.");
    }
}
