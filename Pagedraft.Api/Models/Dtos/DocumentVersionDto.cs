namespace Pagedraft.Api.Models.Dtos;

public record DocumentVersionDto(
    Guid Id,
    Guid BookId,
    Guid ChapterId,
    Guid? SceneId,
    DateTimeOffset CreatedAt,
    string? Label,
    Guid? AnalysisResultId = null,
    string? OriginalText = null,
    string? SuggestedText = null);

public record DocumentVersionDetailDto(
    Guid Id,
    Guid BookId,
    Guid ChapterId,
    Guid? SceneId,
    DateTimeOffset CreatedAt,
    string? Label,
    string ContentSfdt,
    Guid? AnalysisResultId = null,
    string? OriginalText = null,
    string? SuggestedText = null);

public record CreateDocumentVersionRequest(string ContentSfdt, string? Label = null, Guid? AnalysisId = null, string? OriginalText = null, string? SuggestedText = null);
