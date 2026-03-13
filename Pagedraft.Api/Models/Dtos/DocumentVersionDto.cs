namespace Pagedraft.Api.Models.Dtos;

public record DocumentVersionDto(
    Guid Id,
    Guid BookId,
    Guid ChapterId,
    Guid? SceneId,
    DateTimeOffset CreatedAt,
    string? Label,
    Guid? AnalysisResultId = null,
    Guid? SuggestionId = null,
    string? OriginalText = null,
    string? SuggestedText = null,
    /// <summary>Status of the linked analysis result, when present (Active/Archived).</summary>
    string? AnalysisStatus = null);

public record DocumentVersionDetailDto(
    Guid Id,
    Guid BookId,
    Guid ChapterId,
    Guid? SceneId,
    DateTimeOffset CreatedAt,
    string? Label,
    string ContentSfdt,
    Guid? AnalysisResultId = null,
    Guid? SuggestionId = null,
    string? OriginalText = null,
    string? SuggestedText = null,
    /// <summary>Status of the linked analysis result, when present (Active/Archived).</summary>
    string? AnalysisStatus = null);

public record CreateDocumentVersionRequest(
    string ContentSfdt,
    string? Label = null,
    Guid? AnalysisId = null,
    Guid? SuggestionId = null,
    string? OriginalText = null,
    string? SuggestedText = null);
