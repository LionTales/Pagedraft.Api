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
/// ONE USER-FACING TASK'S TIER (tier-ux-rework c1, de-identified by c2). The unit the toggle binds to.
///
/// THERE IS NO PROVIDER, MODEL OR VERSION FIELD HERE, AND THERE MAY NOT BE ONE. Model identity is internal
/// IP, it changes without notice, and the previous shape (a <c>routes</c> list carrying
/// <c>provider</c>/<c>model</c> per task) was rendered verbatim by the client. Stripping it client-side is
/// not enough - it has to be absent from the PAYLOAD, or the next consumer re-leaks it.
/// <c>AiTierDtoDeidentificationTests</c> serializes this DTO against the SHIPPED configuration and fails if
/// any configured provider/model string, or any of the vendor substrings, appears anywhere in the JSON.
/// </summary>
/// <param name="Task">
/// The <c>AiTaskType</c> name - the routing task, which is what the tier is stored per. A client may PUT
/// either this name or a user-facing <c>AnalysisType</c> name; the server normalizes (several analysis types
/// route to one task), and what comes back is always the normalized name.
/// </param>
/// <param name="StoredTier">
/// The task's own override, or null when it INHERITS the book default. Null is not the same as "fast": it is
/// what makes a later change to the book default apply to this task, and what the explicit clear restores.
/// </param>
/// <param name="EffectiveTier">
/// THE TIER THAT WILL ACTUALLY ROUTE for this task on this book, which is the value the toggle highlights.
/// It is deliberately NOT <c>BookAiTierResolver</c>'s answer: the resolver knows the three STORAGE rungs
/// (override, book default, fast) and nothing about task eligibility or the language rung, so
/// <c>AiTierStatusService.DescribeTask</c> clamps it against the route the run will really take. A task whose
/// readiness says it always stays fast - <c>taskNotEligible</c>, or <c>languageAlwaysFast</c> - therefore
/// reads "fast" here whatever the book default is, and so does a task whose <c>{task}_thinking</c> key an
/// operator has removed. That is also how the language-rung override stays visible without naming a model.
///
/// WHAT WAS ASKED FOR IS A DIFFERENT FIELD. <paramref name="StoredTier"/> carries this task's own choice and
/// <paramref name="FallbackActive"/> carries "asked for and not honoured", so clamping here loses nothing;
/// before be-c01 this field answered both questions at once and answered the rendering one wrongly, so a
/// single flip of the book default highlighted "thinking" on tasks whose own write path answers 409.
/// </param>
/// <param name="ThinkingReadiness">
/// Whether "thinking" can route FOR THIS TASK on this deployment (camelCase of <c>AiTierReadiness</c>), and a
/// TOKEN rather than a sentence - the client is he/en bilingual and owns the localized copy. Anything other
/// than "ready" means a PUT of "thinking" for this task is a 409. The three not-ready reasons a user can
/// actually hit are distinct on purpose: "taskNotEligible" (LineEdit / BookReview never move),
/// "languageAlwaysFast" (an English book's Proofread; the language rung outranks the tier rung) and
/// "routeNotConfigured" (the operator kill-switch). They are different sentences with different fixes.
/// </param>
/// <param name="FallbackActive">
/// "THINKING WAS ASKED FOR AND IT IS NOT BEING HONOURED" - the per-task form of the "fall back visibly, never
/// silently" flag, and a claim about the SETTING rather than about the run. Derived from the pre-clamp
/// resolved tier: derived from <paramref name="EffectiveTier"/> it could never be true at all, which would
/// replace be-c01's loud lie with a silent one. The state it exists for is a task stored as "thinking" whose
/// <c>{task}_thinking</c> key an operator then removed; there <paramref name="EffectiveTier"/> reads "fast",
/// <paramref name="StoredTier"/> still reads "thinking", and this flag is what tells the user why.
///
/// It is deliberately FALSE when the book default merely washed over a task that could never honour it
/// (<c>taskNotEligible</c> / <c>languageAlwaysFast</c> with no override of its own): nothing on that control
/// claims thinking, its readiness reason already explains the state, and warning there is what made the
/// pre-be-c01 toggle say three contradictory things at once. An explicit stored "thinking" re-opens it, so an
/// opt-in that a later language change left dormant is still reported.
/// </param>
public record BookAiTierTaskDto(
    string Task,
    string? StoredTier,
    string EffectiveTier,
    string ThinkingReadiness,
    bool FallbackActive);

/// <summary>
/// GET/PUT <c>/api/books/{bookId}/ai-tier</c>. Everything the tier control needs in order to describe the
/// book's tier WITHOUT overstating it.
/// </summary>
/// <param name="Tier">
/// The book-level DEFAULT tier, normalized to "fast" or "thinking". Since tier-ux-rework c1 it is a SEED, not
/// the answer: a task with its own override does not follow it. Per-task answers are in <paramref name="Tasks"/>.
/// </param>
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
/// <param name="ConsentRequired">
/// tier-ux-rework c2. Whether the client must render an explicit consent step before committing a task to
/// "thinking" (<c>Ai:Tier:ConsentRequired</c>; true in dev where fast is local, false in a hosted deployment
/// where both tiers are already off-machine). It is a RENDERING instruction, not an authorization gate: the
/// server's 409 on an unroutable "thinking" request is unchanged and independent of it, so a client that
/// ignores this flag cannot gain anything.
/// </param>
/// <param name="Tasks">
/// Every user-facing task's stored and effective tier (tier-ux-rework c1), so the per-type toggle renders the
/// server's answer rather than deriving one.
/// </param>
/// <remarks>
/// tier-ux-rework c2 REMOVED the <c>routes</c> array, which carried a provider and model string per task.
/// Nothing on this payload may name a provider, a model or a version, and since be-c03 NO routing-derived fact
/// survives at all: the last one was a per-task <c>processingLocation</c> token, kept on the stated grounds
/// that the consent copy could not be written without it, which no client ever read - the copy is a constant
/// and the token described the task's CURRENT tier, never the tier a consent prompt is about. Do not re-add a
/// "which model" field here for debugging - the server logs already carry it, and the payload is what the
/// browser holds. If the consent copy ever does need a routing fact, it needs the location of the THINKING
/// route expressed relative to the USER's machine, which is a different field from the one that was here.
/// </remarks>
public record BookAiTierDto(
    Guid BookId,
    string Tier,
    string ThinkingReadiness,
    bool FallbackActive,
    bool ConsentRequired,
    List<BookAiTierTaskDto> Tasks);

/// <summary>
/// PUT <c>/api/books/{bookId}/ai-tier</c> body. <paramref name="Tier"/> is "fast" | "thinking"; anything else
/// is rejected with a 400 rather than defensively parsed.
/// </summary>
/// <param name="Task">
/// tier-ux-rework c1. The task to set, as an <c>AiTaskType</c> or <c>AnalysisType</c> name. ABSENT (null or
/// blank) means "set the BOOK DEFAULT", which deliberately does NOT clear existing per-task overrides - a
/// default is a seed for tasks that have not been decided, and silently discarding an explicit choice because
/// the user touched an unrelated control is the kind of surprise no undo exists for. Clearing is the separate
/// DELETE.
/// </param>
public record UpdateBookAiTierRequest(string? Tier, string? Task = null);

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
