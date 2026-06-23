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

