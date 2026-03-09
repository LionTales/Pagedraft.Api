namespace Pagedraft.Api.Models.Dtos;

/// <summary>One suggestion outcome for API responses (when loading outcomes for a chapter).</summary>
public record SuggestionOutcomeDto(
    Guid AnalysisResultId,
    string OriginalText,
    string SuggestedText,
    string Outcome);

/// <summary>Request body for POST .../analyses/{id}/suggestion-outcomes.</summary>
public record CreateSuggestionOutcomeRequest(
    string OriginalText,
    string SuggestedText,
    string Outcome);
