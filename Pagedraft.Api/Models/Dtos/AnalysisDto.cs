namespace Pagedraft.Api.Models.Dtos;

/// <summary>Analysis result for API responses. Includes structured JSON and server-side suggestions when applicable.</summary>
public record AnalysisResultDto(
    Guid Id,
    Guid ChapterId,
    Guid? JobId,
    string Type,
    string ResultText,
    string ModelName,
    DateTimeOffset CreatedAt,
    string? StructuredResult = null,
    string? Scope = null,
    string? AnalysisType = null,
    Guid? SceneId = null,
    Guid? BookId = null,
    string? Language = null,
    string? Status = null,
    /// <summary>True when Proofread result was nearly identical to input (possible model length limit or failure).</summary>
    bool ProofreadNoChangesHint = false,
    /// <summary>Server-side suggestions for this analysis run (Proofread and Line Edit).</summary>
    List<AnalysisSuggestionDto>? Suggestions = null);

/// <summary>Request body for POST .../analyze. Send AnalysisType for type picker; TemplateId for legacy.</summary>
public record RunAnalysisRequest(
    Guid? TemplateId,
    string? CustomPrompt,
    bool Stream = false,
    /// <summary>Analysis type when using type picker (Proofread, LineEdit, LinguisticAnalysis, LiteraryAnalysis, Summarization, Custom). Overrides TemplateId when set.</summary>
    string? AnalysisType = null,
    /// <summary>Language code (e.g. "he", "en"). Default "he".</summary>
    string? Language = null);

/// <summary>Server-side suggestion DTO returned from the API.</summary>
public record AnalysisSuggestionDto(
    Guid Id,
    Guid AnalysisResultId,
    string OriginalText,
    string SuggestedText,
    int StartOffset,
    int EndOffset,
    string? Reason,
    string? Category,
    string? Explanation,
    string? Outcome,
    int OrderIndex,
    string? ContextBefore = null,
    string? ContextAfter = null);

/// <summary>Request body for PATCH suggestions/{id}/outcome.</summary>
public record UpdateSuggestionOutcomeRequest(string Outcome);

/// <summary>Response body for POST suggestions/{id}/explain.</summary>
public record ExplainSuggestionResponse(string Explanation);
