namespace Pagedraft.Api.Models.Dtos;

/// <summary>Response for async analysis job start. The jobId can be used with analysis-progress and analysis-jobs endpoints.</summary>
public record StartAnalysisJobResponse(
    Guid JobId,
    string AnalysisType,
    string Scope);

/// <summary>
/// Response for POST .../style-baseline/build. The jobId is pollable via the existing
/// analysis-progress endpoint so the FE reuses analysis-progress.service. NoOp is true when the
/// baseline was already up to date (nothing rebuilt).
/// </summary>
public record StartStyleBaselineBuildResponse(
    Guid? JobId,
    string Language,
    bool NoOp,
    bool Ready,
    int BuiltChapters,
    int TotalChapters,
    int StaleCount);

/// <summary>
/// Response for GET .../style-baseline — coverage + freshness of the cached book style baseline.
/// JSON casing follows the System.Text.Json default (camelCase) used across the analysis DTOs:
/// totalChapters, builtChapters, staleCount, hasBaseline, ready, lastUpdatedAt, builtWithModel,
/// activeModel, builtWithDifferentModel, activeBuildJobId, chaptersToBuild, estimatedSeconds, estimatedUsd.
/// </summary>
public record BookStyleBaselineStatusDto(
    Guid BookId,
    string Language,
    int TotalChapters,
    int BuiltChapters,
    int StaleCount,
    bool HasBaseline,
    bool Ready,
    DateTimeOffset? LastUpdatedAt,
    string? BuiltWithModel,
    // DEF-1: cross-model cache-safety signals for the FE warning.
    // builtWithModel (above) = the model the cached baseline was built with; activeModel = the model now
    // configured for LinguisticAnalysis; builtWithDifferentModel = true when a baseline exists and the two
    // differ (rebuild advisable).
    string? ActiveModel,
    bool BuiltWithDifferentModel,
    // DEF-2: jobId of an in-progress build for (bookId, language), so a reload / second tab can reattach
    // to its progress; null when no build is running.
    Guid? ActiveBuildJobId,
    // a4: build estimate fields consumed by the FE consent prompt (a5).
    int ChaptersToBuild,
    int EstimatedSeconds,
    decimal? EstimatedUsd);

/// <summary>POST .../style-baseline/build request body.</summary>
public record BuildStyleBaselineRequest(string? Language = "he");

/// <summary>
/// Response for POST .../summary/build. Mirrors <see cref="StartStyleBaselineBuildResponse"/> so the FE
/// reuses the same async-job handling. The jobId is pollable via GET summary/progress/{jobId}; NoOp is
/// true when the summary was already up to date (nothing rebuilt).
/// </summary>
public record StartBookSummaryBuildResponse(
    Guid? JobId,
    string Language,
    bool NoOp,
    bool Ready,
    int BuiltChapters,
    int TotalChapters,
    int StaleCount);

/// <summary>
/// Response for GET .../summary — coverage + freshness of the cached L2 book summary (BookBrief) rollup.
/// JSON casing follows the System.Text.Json default (camelCase), mirroring
/// <see cref="BookStyleBaselineStatusDto"/>: totalChapters, builtChapters, staleCount, hasSummary, ready,
/// lastUpdatedAt, builtWithModel, activeModel, builtWithDifferentModel, activeBuildJobId, chaptersToBuild,
/// estimatedSeconds, estimatedUsd.
/// </summary>
public record BookSummaryStatusDto(
    Guid BookId,
    string Language,
    int TotalChapters,
    int BuiltChapters,
    int StaleCount,
    bool HasSummary,
    bool Ready,
    DateTimeOffset? LastUpdatedAt,
    string? BuiltWithModel,
    string? ActiveModel,
    bool BuiltWithDifferentModel,
    Guid? ActiveBuildJobId,
    int ChaptersToBuild,
    int EstimatedSeconds,
    decimal? EstimatedUsd);

/// <summary>POST .../summary/build request body.</summary>
public record BuildBookSummaryRequest(string? Language = "he");

// ─── Whole-book review (wb2-c03) ────────────────────────────────────────────────────────────────

/// <summary>POST .../review request body.</summary>
public record BuildBookReviewRequest(string? Language = "he");

/// <summary>
/// Response for POST .../review. Mirrors <see cref="StartBookSummaryBuildResponse"/> so the FE reuses the
/// same async-job handling. The jobId is pollable via GET review/progress/{jobId}; NoOp is true when the
/// review was already up to date (nothing rebuilt). BriefsMissing is true when the book has no usable
/// structured briefs yet — the FE should prompt to build the book summary first (no model calls were
/// spent). FindingCount is the current persisted finding count for (bookId, language). JSON casing is the
/// System.Text.Json default (camelCase): jobId, language, noOp, ready, briefsMissing, findingCount, message.
/// </summary>
public record StartBookReviewBuildResponse(
    Guid? JobId,
    string Language,
    bool NoOp,
    bool Ready,
    bool BriefsMissing,
    int FindingCount,
    string Message);

/// <summary>
/// Response for GET .../review/status — coverage + freshness of the cached whole-book review (BookFinding
/// rows). JSON casing follows the System.Text.Json default (camelCase): bookId, language, hasReview,
/// findingCount, lastUpdatedAt, builtWithModel, activeModel, builtWithDifferentModel, staleVsBriefs,
/// hasBriefs, activeBuildJobId, ready.
/// </summary>
public record BookReviewStatusDto(
    Guid BookId,
    string Language,
    bool HasReview,
    int FindingCount,
    DateTimeOffset? LastUpdatedAt,
    string? BuiltWithModel,
    string? ActiveModel,
    bool BuiltWithDifferentModel,
    bool StaleVsBriefs,
    bool HasBriefs,
    Guid? ActiveBuildJobId,
    bool Ready);

/// <summary>
/// One chapter anchor a finding touches (DTO mirror of <see cref="Pagedraft.Api.Models.FindingChapterAnchor"/>).
/// JSON (camelCase): chapterId, order, title.
/// </summary>
public record FindingChapterAnchorDto(
    Guid ChapterId,
    int Order,
    string Title);

/// <summary>
/// One piece of textual evidence supporting a finding (DTO mirror of
/// <see cref="Pagedraft.Api.Models.FindingEvidence"/>). JSON (camelCase): chapterId, chapterOrder, excerpt.
/// </summary>
public record FindingEvidenceDto(
    Guid? ChapterId,
    int ChapterOrder,
    string Excerpt);

/// <summary>
/// A single persisted whole-book finding for the FE. JSON casing is the System.Text.Json default
/// (camelCase): id, dimension, verdict, severity, rationale, evidence, chapterAnchors, suggestedAction,
/// status, builtWithModel, createdAt, updatedAt.
///   dimension: plot | character | pacing | tone | theme | continuity
///   verdict:   keep | improve | cut
///   severity:  1 (minor) | 2 (moderate) | 3 (major)
///   status:    open | acknowledged | dismissed | done
/// </summary>
public record BookFindingDto(
    Guid Id,
    string Dimension,
    string Verdict,
    int Severity,
    string Rationale,
    IReadOnlyList<FindingEvidenceDto> Evidence,
    IReadOnlyList<FindingChapterAnchorDto> ChapterAnchors,
    string? SuggestedAction,
    string Status,
    string? BuiltWithModel,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Per-dimension rollup score for the FE. JSON (camelCase): dimension, score, keepCount, improveCount,
/// cutCount. score is a label: weak | mixed | strong.
/// </summary>
public record BookReviewDimensionScoreDto(
    string Dimension,
    string Score,
    int KeepCount,
    int ImproveCount,
    int CutCount);

/// <summary>
/// Response for GET .../review/findings — the persisted findings plus per-dimension rollup scores. JSON
/// (camelCase): bookId, language, findings, scores.
/// </summary>
public record BookReviewFindingsDto(
    Guid BookId,
    string Language,
    IReadOnlyList<BookFindingDto> Findings,
    IReadOnlyList<BookReviewDimensionScoreDto> Scores);

/// <summary>
/// PATCH .../review/findings/{id}/status request body. status: acknowledge | dismiss | done | open
/// (the imperative verbs map to the BookFinding.Status set acknowledged | dismissed | done | open).
/// </summary>
public record UpdateFindingStatusRequest(string? Status);

