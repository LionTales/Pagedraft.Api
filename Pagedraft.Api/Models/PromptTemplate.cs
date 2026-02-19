namespace Pagedraft.Api.Models;

public class PromptTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Proofreading, Literary, Linguistic, Custom
    public string TemplateText { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public string Language { get; set; } = "he";

    public ICollection<AnalysisResult> Results { get; set; } = new List<AnalysisResult>();
}
