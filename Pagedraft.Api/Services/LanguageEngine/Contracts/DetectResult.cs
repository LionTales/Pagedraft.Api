namespace Pagedraft.Api.Services.LanguageEngine.Contracts;

/// <summary>Result of the detect stage; may indicate the detection service was unavailable.</summary>
public class DetectResult
{
    public List<LanguageIssue> Issues { get; set; } = new();
    /// <summary>True when the detection service (e.g. LanguageTool) could not be reached.</summary>
    public bool ServiceUnavailable { get; set; }
    /// <summary>
    /// Friendly message to show when ServiceUnavailable is true. Prefer ServiceUnavailableCode. Kept
    /// UNTIL the fifth ServiceUnavailable path (LanguageToolEngine's `he` auto-retry-success branch,
    /// which carries no code today) is assigned a code - not for a fixed number of releases. See
    /// PAGEDRAFT_DESIGN.md's "KNOWN GAP" paragraph for this endpoint: "a code should be assigned
    /// before the message field is removed."
    /// </summary>
    public string? ServiceUnavailableMessage { get; set; }
    /// <summary>
    /// Stable machine-readable reason for ServiceUnavailable (e.g. "hebrew-unsupported", "disabled",
    /// "unavailable", "timeout"), so callers can localize instead of rendering the English message.
    /// </summary>
    public string? ServiceUnavailableCode { get; set; }
}
