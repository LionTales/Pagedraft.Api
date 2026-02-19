namespace Pagedraft.Api.Models;

public class Scene
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChapterId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Order { get; set; }
    public string? ContentSfdt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Chapter Chapter { get; set; } = null!;
}
