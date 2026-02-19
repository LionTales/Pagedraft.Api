namespace Pagedraft.Api.Models;

public class Chapter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public string? PartName { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Order { get; set; }
    public string ContentSfdt { get; set; } = "{}";
    public string ContentText { get; set; } = string.Empty;
    public int WordCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
    public ICollection<Scene> Scenes { get; set; } = new List<Scene>();
    public ICollection<AnalysisResult> AnalysisResults { get; set; } = new List<AnalysisResult>();
}
