namespace Pagedraft.Api.Services.LanguageEngine.Contracts;

/// <summary>Request for language engine processing.</summary>
public class LanguageEngineRequest
{
    public required string InputText { get; set; }
    public string Language { get; set; } = "he-IL";
    public LanguageEngineOptions Options { get; set; } = new();
}

/// <summary>Options controlling which pipeline stages are enabled.</summary>
public class LanguageEngineOptions
{
    public bool EnableNormalize { get; set; } = true;
    public bool EnableDetect { get; set; } = true;
    public bool EnableRewrite { get; set; } = false;
    public bool EnableAnalyze { get; set; } = false;
    public string? PreferredModel { get; set; } // Override default model selection
}
