using System;

namespace Pagedraft.Api.Models;

public class AnalysisSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnalysisResultId { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public string SuggestedText { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string? Reason { get; set; }
    public string? Category { get; set; }
    public string? Explanation { get; set; }
    public SuggestionOutcome? Outcome { get; set; }
    public int OrderIndex { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ContextBefore { get; set; }
    public string? ContextAfter { get; set; }

    public AnalysisResult AnalysisResult { get; set; } = null!;
}

