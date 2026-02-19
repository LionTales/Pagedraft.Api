namespace Pagedraft.Api.Services.LanguageEngine.Contracts;

/// <summary>Result of the detect stage; may indicate the detection service was unavailable.</summary>
public class DetectResult
{
    public List<LanguageIssue> Issues { get; set; } = new();
    /// <summary>True when the detection service (e.g. LanguageTool) could not be reached.</summary>
    public bool ServiceUnavailable { get; set; }
    /// <summary>Friendly message to show when ServiceUnavailable is true.</summary>
    public string? ServiceUnavailableMessage { get; set; }
}
