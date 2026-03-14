namespace Pagedraft.Api.Models;

/// <summary>
/// Snapshot of document content (chapter or scene) for version history and revert.
/// </summary>
public class DocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Guid ChapterId { get; set; }
    public Guid? SceneId { get; set; }
    /// <summary>When set, this version was created by accepting a proofread suggestion; used to record Reverted outcome on revert.</summary>
    public Guid? AnalysisResultId { get; set; }
    public string ContentSfdt { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Optional label, e.g. "After proofread accept", "Before edit", or "Original: X → Suggested: Y".</summary>
    public string? Label { get; set; }
    /// <summary>When set with AnalysisResultId, exact original text for the suggestion (so revert can record Reverted outcome).</summary>
    public string? OriginalText { get; set; }
    /// <summary>When set with AnalysisResultId, exact suggested text (so revert can record Reverted outcome).</summary>
    public string? SuggestedText { get; set; }
    /// <summary>
    /// Optional link to the specific AnalysisSuggestion row that produced this version.
    /// When present, Versions/History operations should prefer this stable identifier
    /// over text-based matching of OriginalText/SuggestedText.
    /// </summary>
    public Guid? SuggestionId { get; set; }
}
