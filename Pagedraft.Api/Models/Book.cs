namespace Pagedraft.Api.Models;

public class Book
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string Language { get; set; } = "he";

    /// <summary>
    /// The book's model tier (model-tier-fast-thinking plan, p3-2). Stored as a STRING so an unknown value
    /// written by a newer build, a hand-edited row, or a legacy null degrades instead of throwing:
    /// <see cref="Services.Ai.AiTierPolicy.Parse"/> maps anything unrecognised (including null) to
    /// <see cref="Services.Ai.Contracts.AiTier.Fast"/>, the local tier.
    ///
    /// NULL = fast, and that is the shipped default for every book - the p2-4 GO is a quality finding, not a
    /// default flip, and the thinking tier sends an unpublished manuscript to a third-party provider, so it
    /// must be opt-in. Scope is per BOOK (plan "## p3 scope decision"): the tier's unit and the
    /// cache-invalidation unit have to coincide, and per-request makes the status DTOs' ActiveModel /
    /// BuiltWithDifferentModel uncomputable because a GET has no request tier to key on.
    /// </summary>
    public string? AiTier { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<Chapter> Chapters { get; set; } = new List<Chapter>();
}
