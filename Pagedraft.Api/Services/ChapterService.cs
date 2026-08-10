using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Hubs.Events;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services;

public class ChapterService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<BookSyncHub> _hubContext;
    private readonly SfdtConversionService _sfdtConversion;

    /// <summary>
    /// be-c03: the per-book proper-noun LEAVE set is harvested partly FROM <c>Chapter.ContentText</c>, so every
    /// write below changes a harvest source and must drop the cached set (a stale set that MISSES a name lets the
    /// repair model rewrite that name — not cosmetic). OPTIONAL only so the direct-construction test fakes that
    /// pass <c>null!</c> for the DbContext keep compiling; DI always supplies the real singleton, and
    /// <see cref="IBookEntityProvider.Invalidate"/> is non-throwing by contract, so the invalidation can never
    /// break a chapter save.
    /// </summary>
    private readonly IBookEntityProvider? _bookEntities;

    public ChapterService(
        AppDbContext db,
        IHubContext<BookSyncHub> hubContext,
        SfdtConversionService sfdtConversion,
        IBookEntityProvider? bookEntities = null)
    {
        _db = db;
        _hubContext = hubContext;
        _sfdtConversion = sfdtConversion;
        _bookEntities = bookEntities;
    }

    /// <summary>
    /// All of a book's chapters, in <c>Order</c>. Untracked: every caller - the chapter list and reorder
    /// responses in <see cref="Controllers.ChaptersController"/>, and the export read in
    /// <see cref="BookExportService"/> - only reads or projects these rows and never mutates the returned
    /// instances, so nothing needs them tracked. If a future caller needs to mutate a chapter fetched this
    /// way, load it through a tracked query instead of removing this for everyone.
    /// </summary>
    public async Task<List<Chapter>> GetAllByBookAsync(Guid bookId, CancellationToken ct = default)
    {
        return await _db.Chapters.AsNoTracking().Where(c => c.BookId == bookId).OrderBy(c => c.Order).ToListAsync(ct);
    }

    public async Task<Chapter?> GetByIdAsync(Guid bookId, Guid chapterId, CancellationToken ct = default)
    {
        return await _db.Chapters.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
    }

    public async Task<Chapter> CreateAsync(Guid bookId, string title, string? partName, int? order, string contentSfdt, CancellationToken ct = default)
    {
        // Compute a unique Order value for this book, even if there are gaps or
        // the client passes an explicit order that already exists.
        var existingOrders = await _db.Chapters
            .Where(c => c.BookId == bookId)
            .Select(c => c.Order)
            .ToListAsync(ct);

        var nextOrder = order ?? (existingOrders.Count == 0 ? 0 : existingOrders.Max() + 1);
        if (existingOrders.Count > 0)
        {
            var used = new HashSet<int>(existingOrders);
            while (used.Contains(nextOrder))
            {
                nextOrder++;
            }
        }
        var (contentText, wordCount) = ExtractTextAndCount(contentSfdt);

        var chapter = new Chapter
        {
            BookId = bookId,
            Title = title,
            PartName = partName,
            Order = nextOrder,
            ContentSfdt = contentSfdt,
            ContentText = contentText,
            WordCount = wordCount
        };
        _db.Chapters.Add(chapter);
        await _db.SaveChangesAsync(ct);
        _bookEntities?.Invalidate(bookId); // be-c03: Chapter.ContentText is a harvest source

        await _hubContext.Clients.Group($"book:{bookId}").SendAsync("ChapterCreated", new ChapterCreatedEvent(bookId, chapter.Id, chapter.Title, chapter.Order), ct);
        return chapter;
    }

    public async Task CreateBatchAsync(Guid bookId, IReadOnlyList<(string Title, string? PartName, string ContentSfdt)> chapters, CancellationToken ct = default)
    {
        for (var i = 0; i < chapters.Count; i++)
        {
            var (title, partName, contentSfdt) = chapters[i];
            var (contentText, wordCount) = ExtractTextAndCount(contentSfdt);
            var ch = new Chapter
            {
                BookId = bookId,
                Title = title,
                PartName = partName,
                Order = i,
                ContentSfdt = contentSfdt,
                ContentText = contentText,
                WordCount = wordCount
            };
            _db.Chapters.Add(ch);
        }
        await _db.SaveChangesAsync(ct);
        _bookEntities?.Invalidate(bookId); // be-c03: Chapter.ContentText is a harvest source
        foreach (var ch in await _db.Chapters.Where(c => c.BookId == bookId).OrderBy(c => c.Order).ToListAsync(ct))
            await _hubContext.Clients.Group($"book:{bookId}").SendAsync("ChapterCreated", new ChapterCreatedEvent(bookId, ch.Id, ch.Title, ch.Order), ct);
    }

    public async Task<Chapter?> SaveAsync(Guid bookId, Guid chapterId, string? contentSfdt, string? title, string? partName, int? order, CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return null;

        var contentChanged = contentSfdt != null;
        if (contentSfdt != null)
        {
            chapter.ContentSfdt = contentSfdt;
            var (contentText, wordCount) = ExtractTextAndCount(contentSfdt);
            chapter.ContentText = contentText;
            chapter.WordCount = wordCount;
        }
        if (title != null) chapter.Title = title;
        if (partName != null) chapter.PartName = partName;
        if (order.HasValue) chapter.Order = order.Value;
        await _db.SaveChangesAsync(ct);

        // be-c03: only a CONTENT write changes the manuscript harvest source (a title/order-only save does not
        // change any token or the chapter's presence, so it cannot change the harvested set).
        if (contentChanged)
        {
            _bookEntities?.Invalidate(bookId);
        }

        await _hubContext.Clients.Group($"book:{bookId}").SendAsync("ChapterUpdated", new ChapterUpdatedEvent(bookId, chapter.Id, chapter.WordCount, chapter.UpdatedAt), ct);
        return chapter;
    }

    public async Task<bool> DeleteAsync(Guid bookId, Guid chapterId, CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return false;
        _db.Chapters.Remove(chapter);
        await _db.SaveChangesAsync(ct);
        _bookEntities?.Invalidate(bookId); // be-c03: removing a chapter removes its prose from the harvest
        await _hubContext.Clients.Group($"book:{bookId}").SendAsync("ChapterDeleted", new ChapterDeletedEvent(bookId, chapterId), ct);
        return true;
    }

    public async Task ReorderAsync(Guid bookId, List<(Guid ChapterId, int Order)> newOrder, CancellationToken ct = default)
    {
        // To satisfy the unique index on (BookId, Order) we need to avoid
        // transient duplicate Order values while reassigning. We do this in
        // two phases with a large offset for temporary values.

        var chapters = await _db.Chapters
            .Where(c => c.BookId == bookId)
            .ToListAsync(ct);

        var map = newOrder.ToDictionary(x => x.ChapterId, x => x.Order);

        const int TEMP_OFFSET = 10_000;

        // Phase 1: move targeted chapters to temporary, non-conflicting order range
        foreach (var ch in chapters)
        {
            if (map.TryGetValue(ch.Id, out var targetOrder))
            {
                ch.Order = TEMP_OFFSET + targetOrder;
            }
        }
        await _db.SaveChangesAsync(ct);

        // Phase 2: move them into their final order values
        foreach (var ch in chapters)
        {
            if (map.TryGetValue(ch.Id, out var targetOrder))
            {
                ch.Order = targetOrder;
            }
        }
        await _db.SaveChangesAsync(ct);

        var list = newOrder.Select(x => new ChapterOrderItem(x.ChapterId, x.Order)).ToList();
        await _hubContext.Clients.Group($"book:{bookId}").SendAsync("ChapterReordered", new ChapterReorderedEvent(bookId, list), ct);
    }

        public async Task<List<Chapter>> ImportFromPreviewAsync(
            Guid bookId,
            string mode,
            IReadOnlyList<(string Title, string? PartName, int Order, string ContentSfdt)> chapters,
            CancellationToken ct = default)
        {
            var isOverwrite = string.Equals(mode, "overwrite", StringComparison.OrdinalIgnoreCase);

            if (isOverwrite)
            {
                var existing = await _db.Chapters.Where(c => c.BookId == bookId).ToListAsync(ct);
                if (existing.Count > 0)
                {
                    _db.Chapters.RemoveRange(existing);
                    await _db.SaveChangesAsync(ct);
                }
            }

            var existingMaxOrder = await _db.Chapters
                .Where(c => c.BookId == bookId)
                .MaxAsync(c => (int?)c.Order, ct) ?? -1;

            var ordered = chapters.OrderBy(c => c.Order).ToList();
            var created = new List<Chapter>();

            for (var i = 0; i < ordered.Count; i++)
            {
                var (title, partName, order, contentSfdt) = ordered[i];
                var (contentText, wordCount) = ExtractTextAndCount(contentSfdt);

                var chapter = new Chapter
                {
                    BookId = bookId,
                    Title = title,
                    PartName = partName,
                    Order = isOverwrite ? i : existingMaxOrder + 1 + i,
                    ContentSfdt = contentSfdt,
                    ContentText = contentText,
                    WordCount = wordCount
                };

                _db.Chapters.Add(chapter);
                created.Add(chapter);
            }

            await _db.SaveChangesAsync(ct);
            _bookEntities?.Invalidate(bookId); // be-c03: an import rewrites the manuscript harvest source wholesale

            foreach (var ch in created.OrderBy(c => c.Order))
            {
                await _hubContext.Clients.Group($"book:{bookId}")
                    .SendAsync("ChapterCreated", new ChapterCreatedEvent(bookId, ch.Id, ch.Title, ch.Order), ct);
            }

            return created;
        }

        private (string ContentText, int WordCount) ExtractTextAndCount(string contentSfdt) => _sfdtConversion.GetTextFromSfdt(contentSfdt);
}
