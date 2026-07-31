namespace Pagedraft.Api.Models.Dtos;

/// <summary>
/// <paramref name="AiTier"/> is the book's model tier (model-tier-fast-thinking plan, p3-4), NORMALIZED on
/// the way out: the column is a nullable free string so a legacy/hand-edited row degrades instead of
/// throwing, but the wire contract is always exactly "fast" or "thinking". A client must never have to
/// repeat the defensive parse (<c>AiTierPolicy.Parse</c>) that the server already owns, or the two will
/// eventually disagree about what an unrecognised value means.
/// </summary>
public record BookDto(Guid Id, string Title, string? Author, string Language, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string AiTier);

public record BookDetailDto(Guid Id, string Title, string? Author, string Language, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string AiTier, List<ChapterSummaryDto> Chapters);

public record ChapterSummaryDto(Guid Id, string Title, string? PartName, int Order, int WordCount, DateTimeOffset UpdatedAt);

public record ChapterDto(Guid Id, string Title, string? PartName, int Order, int WordCount, DateTimeOffset UpdatedAt, string ContentSfdt);

public record CreateBookRequest(string Title, string? Author, string? Language);

// ─── Model tier (model-tier-fast-thinking plan, p3-4) ────────────────────

/// <summary>
/// One allowlisted task's ACTUAL route for this book: what the UI is allowed to promise will run.
/// Mirrors <c>Services.Ai.AiTierRouteInfo</c>.
/// </summary>
/// <param name="UsesTier">
/// False is not an error. For an ENGLISH book the <c>Proofread_en</c> key outranks the tier rung, so English
/// proofreading stays local on both tiers by design (enforcement layer E3) and the surface says so.
/// </param>
public record BookAiTierRouteDto(string Task, string Provider, string Model, bool UsesTier);

/// <summary>
/// GET/PUT <c>/api/books/{bookId}/ai-tier</c>. Everything the tier control needs in order to describe the
/// book's tier WITHOUT overstating it.
/// </summary>
/// <param name="Tier">The stored tier, normalized to "fast" or "thinking".</param>
/// <param name="ThinkingReadiness">
/// "ready" | "routeNotConfigured" | "providerNotRegistered" | "providerCredentialsMissing" - camelCase of
/// <c>Services.Ai.AiTierReadiness</c>. Anything other than "ready" means the option must not be offered as
/// if it worked.
/// </param>
/// <param name="FallbackActive">
/// The book stores "thinking" but NO route actually uses the tier, so it is running on the local models.
/// The surface MUST render this; a stored tier that quietly resolves to something else is precisely the
/// silent-fallback failure this endpoint exists to make visible.
/// </param>
public record BookAiTierDto(
    Guid BookId,
    string Tier,
    string ThinkingReadiness,
    bool FallbackActive,
    List<BookAiTierRouteDto> Routes);

/// <summary>PUT <c>/api/books/{bookId}/ai-tier</c> body. "fast" | "thinking"; anything else is rejected.</summary>
public record UpdateBookAiTierRequest(string? Tier);

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
