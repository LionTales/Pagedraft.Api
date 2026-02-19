namespace Pagedraft.Api.Hubs.Events;

public record ChapterUpdatedEvent(Guid BookId, Guid ChapterId, int WordCount, DateTimeOffset UpdatedAt);
public record ChapterCreatedEvent(Guid BookId, Guid ChapterId, string Title, int Order);
public record ChapterDeletedEvent(Guid BookId, Guid ChapterId);
public record ChapterReorderedEvent(Guid BookId, List<ChapterOrderItem> NewOrder);
public record ChapterOrderItem(Guid ChapterId, int Order);

// ─── Scene events (Phase 3) ───────────────────────────────────────────
public record SceneCreatedEvent(Guid BookId, Guid ChapterId, Guid SceneId, string Title, int Order);
public record SceneUpdatedEvent(Guid BookId, Guid ChapterId, Guid SceneId, DateTimeOffset UpdatedAt);
public record SceneDeletedEvent(Guid BookId, Guid ChapterId, Guid SceneId);
public record ScenesReorderedEvent(Guid BookId, Guid ChapterId, List<SceneOrderItem> NewOrder);
public record SceneOrderItem(Guid SceneId, int Order);
