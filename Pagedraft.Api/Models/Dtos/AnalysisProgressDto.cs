using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Models.Dtos;

public record AnalysisProgressDto(
    Guid JobId,
    string AnalysisType,
    string Scope,
    Guid? BookId,
    Guid? ChapterId,
    Guid? SceneId,
    string Status,
    int CurrentChunk,
    int TotalChunks,
    string Message,
    int EstimatedCompletionPercent);

