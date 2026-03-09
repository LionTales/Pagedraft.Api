namespace Pagedraft.Api.Models;

/// <summary>
/// Records whether a specific suggestion (from a Proofread or Line Edit analysis run) was accepted or dismissed.
/// Enables restoring Accepted/Dismissed state when reopening the History tab.
/// </summary>
public class SuggestionOutcomeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnalysisResultId { get; set; }
    /// <summary>Original text of the suggestion (e.g. from proofread diff or line-edit suggestion).</summary>
    public string OriginalText { get; set; } = string.Empty;
    /// <summary>Suggested replacement text.</summary>
    public string SuggestedText { get; set; } = string.Empty;
    /// <summary>Accepted or Dismissed.</summary>
    public SuggestionOutcome Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public AnalysisResult AnalysisResult { get; set; } = null!;
}

/// <summary>User outcome for a single suggestion.</summary>
public enum SuggestionOutcome
{
    Accepted = 0,
    Dismissed = 1,
    Reverted = 2
}
