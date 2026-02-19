namespace Pagedraft.Api.Models.Dtos;

public record BookDto(Guid Id, string Title, string? Author, string Language, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record BookDetailDto(Guid Id, string Title, string? Author, string Language, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<ChapterSummaryDto> Chapters);

public record ChapterSummaryDto(Guid Id, string Title, string? PartName, int Order, int WordCount, DateTimeOffset UpdatedAt);

public record ChapterDto(Guid Id, string Title, string? PartName, int Order, int WordCount, DateTimeOffset UpdatedAt, string ContentSfdt);

public record CreateBookRequest(string Title, string? Author, string? Language);

public record CreateChapterRequest(string Title, string? PartName, int? Order);

public record UpdateChapterRequest(string? ContentSfdt, string? Title, string? PartName, int? Order);

public record ReorderChaptersRequest(List<ChapterOrderRequest> Chapters);

public record ChapterOrderRequest(Guid ChapterId, int Order);

// ─── Book intelligence (Phase 4 / 5) ─────────────────────────────────────

/// <summary>GET /api/books/{bookId}/profile response.</summary>
public record BookProfileDto(
    Guid Id,
    Guid BookId,
    string? Genre,
    string? SubGenre,
    string? Synopsis,
    string? TargetAudience,
    int? LiteratureLevel,
    string? LanguageRegister,
    string? CharactersJson,
    string? StoryStructureJson,
    string Language,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>POST /api/books/{bookId}/summarize — summarize chapters (stale only).</summary>
public record SummarizeBookRequest(string? Language = "he");

/// <summary>POST /api/books/{bookId}/profile/refresh — re-summarize stale chapters and rebuild profile.</summary>
public record RefreshProfileRequest(string? Language = "he");

/// <summary>POST /api/books/{bookId}/ask — one-shot Q&A about the book.</summary>
public record AskBookRequest(string Question, string? Language = "he");

// ─── Scenes (Phase 3) ─────────────────────────────────────────────────

/// <summary>GET scene by id — full scene for editor.</summary>
public record SceneDto(Guid Id, Guid ChapterId, string Title, int Order, string? ContentSfdt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Scene list item (tree node, no content).</summary>
public record SceneSummaryDto(Guid Id, Guid ChapterId, string Title, int Order, DateTimeOffset UpdatedAt);

/// <summary>POST create scene.</summary>
public record CreateSceneDto(string Title, int? Order, string? ContentSfdt);

/// <summary>PATCH update scene.</summary>
public record UpdateSceneDto(string? Title, int? Order, string? ContentSfdt);

/// <summary>PUT reorder scenes.</summary>
public record ReorderScenesRequest(List<SceneOrderRequest> Scenes);

public record SceneOrderRequest(Guid SceneId, int Order);
