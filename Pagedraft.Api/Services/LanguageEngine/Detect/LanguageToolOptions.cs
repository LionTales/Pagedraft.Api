namespace Pagedraft.Api.Services.LanguageEngine.Detect;

/// <summary>Configuration options for LanguageTool integration.</summary>
public class LanguageToolOptions
{
    public const string SectionName = "LanguageEngine:LanguageTool";

    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = "http://localhost:8081";
    public string Language { get; set; } = "he";
}
