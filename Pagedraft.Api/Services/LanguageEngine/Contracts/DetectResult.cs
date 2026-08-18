namespace Pagedraft.Api.Services.LanguageEngine.Contracts;

/// <summary>Result of the detect stage; may indicate the detection service was unavailable.</summary>
public class DetectResult
{
    public List<LanguageIssue> Issues { get; set; } = new();
    /// <summary>True when the detection service (e.g. LanguageTool) could not be reached.</summary>
    public bool ServiceUnavailable { get; set; }
    /// <summary>Friendly message to show when ServiceUnavailable is true. Kept for one release; prefer ServiceUnavailableCode.</summary>
    public string? ServiceUnavailableMessage { get; set; }
    /// <summary>
    /// Stable machine-readable reason for ServiceUnavailable (e.g. "hebrew-unsupported", "disabled",
    /// "unavailable", "timeout"), so callers can localize instead of rendering the English message.
    /// </summary>
    public string? ServiceUnavailableCode { get; set; }
}
