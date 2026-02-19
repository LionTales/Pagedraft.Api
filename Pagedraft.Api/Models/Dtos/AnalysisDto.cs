namespace Pagedraft.Api.Models.Dtos;

/// <summary>Analysis result for API responses. Includes structured JSON when applicable.</summary>
public record AnalysisResultDto(
    Guid Id,
    Guid ChapterId,
    string Type,
    string ResultText,
    string ModelName,
    DateTimeOffset CreatedAt,
    string? StructuredResult = null,
    string? Scope = null,
    string? AnalysisType = null,
    Guid? SceneId = null,
    Guid? BookId = null,
    string? Language = null);

/// <summary>Request body for POST .../analyze. Send AnalysisType for type picker; TemplateId for legacy.</summary>
public record RunAnalysisRequest(
    Guid? TemplateId,
    string? CustomPrompt,
    bool Stream = false,
    /// <summary>Analysis type when using type picker (Proofread, LineEdit, LinguisticAnalysis, LiteraryAnalysis, Summarization, Custom). Overrides TemplateId when set.</summary>
    string? AnalysisType = null,
    /// <summary>Language code (e.g. "he", "en"). Default "he".</summary>
    string? Language = null);
