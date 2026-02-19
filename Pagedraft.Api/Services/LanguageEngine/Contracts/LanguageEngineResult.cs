namespace Pagedraft.Api.Services.LanguageEngine.Contracts;

/// <summary>Result from language engine processing.</summary>
public class LanguageEngineResult
{
    public string NormalizedText { get; set; } = string.Empty;
    public List<LanguageIssue> Issues { get; set; } = new();
    public string? RewrittenText { get; set; }
    public LanguageAnalysis? Analysis { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>Detected language issue with location, message, and suggestions.</summary>
public class LanguageIssue
{
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public required string Message { get; set; }
    public string Category { get; set; } = "grammar"; // "grammar", "spelling", "punctuation", "style"
    public string Severity { get; set; } = "warning"; // "error", "warning", "info"
    public double Confidence { get; set; } = 0.5;
    public List<string> Suggestions { get; set; } = new();
    public string? RuleId { get; set; } // For LanguageTool compatibility
}

/// <summary>Linguistic and literary analysis result.</summary>
public class LanguageAnalysis
{
    public LinguisticAnalysis? Linguistic { get; set; }
    public LiteraryAnalysis? Literary { get; set; }
}

/// <summary>Linguistic analysis details.</summary>
public class LinguisticAnalysis
{
    public Dictionary<string, object> SyntaxMetrics { get; set; } = new();
    public Dictionary<string, object> MorphologyMetrics { get; set; } = new();
    public Dictionary<string, object> StyleMetrics { get; set; } = new();
    public double? GrammaticalityScore { get; set; }
}

/// <summary>Literary analysis details.</summary>
public class LiteraryAnalysis
{
    public List<string> Themes { get; set; } = new();
    public string? Tone { get; set; }
    public string? NarrativeVoice { get; set; }
    public List<string> RhetoricalDevices { get; set; } = new();
}
