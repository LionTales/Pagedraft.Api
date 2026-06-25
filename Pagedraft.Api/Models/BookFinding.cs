using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;

namespace Pagedraft.Api.Models;

/// <summary>
/// Cached book-wide editorial finding produced by the whole-book review pass. Each row
/// represents one discrete issue (or strength) across one or more chapters, keyed by
/// (BookId, DedupKey) so a rebuild can preserve prior user acknowledgements/dismissals.
///
/// Dimension values: plot | character | pacing | tone | theme | continuity
/// Verdict values:   keep | improve | cut
/// Status values:    open | acknowledged | dismissed | done
///
/// Severity 1-3:
///   1 = minor (optional polish -- nice to have)
///   2 = moderate (noticeable issue; recommended fix)
///   3 = major (significant structural or narrative problem; strongly recommended fix)
/// </summary>
public class BookFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookId { get; set; }

    /// <summary>Analysis language ("he" / "en"). Part of the build context.</summary>
    public string Language { get; set; } = "he";

    /// <summary>Editorial dimension: plot | character | pacing | tone | theme | continuity</summary>
    public string Dimension { get; set; } = string.Empty;

    /// <summary>Overall verdict for this finding: keep | improve | cut</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>
    /// Severity 1-3:
    ///   1 = minor (optional polish -- nice to have)
    ///   2 = moderate (noticeable issue; recommended fix)
    ///   3 = major (significant structural or narrative problem; strongly recommended fix)
    /// </summary>
    public int Severity { get; set; }

    /// <summary>Human-readable explanation of the finding.</summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>
    /// Serialised list of evidence anchors. Each element is:
    /// { chapterId?: Guid, chapterOrder: int, excerpt: string }
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string EvidenceJson { get; set; } = string.Empty;

    /// <summary>
    /// Serialised list of chapter anchors that this finding touches. Each element is:
    /// { chapterId: Guid, order: int, title: string }
    /// Used for navigation and dedup key derivation.
    /// </summary>
    [Column(TypeName = "nvarchar(max)")]
    public string ChapterAnchorsJson { get; set; } = string.Empty;

    /// <summary>Optional editorial action the model suggests (e.g. "Tighten chapter 3 opening").</summary>
    public string? SuggestedAction { get; set; }

    /// <summary>Workflow status: open | acknowledged | dismissed | done. Defaults to "open".</summary>
    public string Status { get; set; } = "open";

    /// <summary>
    /// Deterministic dedup key used to preserve user-set Status across rebuild runs.
    /// Computed as a stable SHA-256 over the composite:
    ///   dimension (lowercased, trimmed)
    ///   + "|" + primaryChapterOrder (order of the first ChapterAnchor, or "0" if none)
    ///   + "|" + rationale (lowercased, trimmed, collapsed whitespace)
    /// Hex-encoded (64 chars). The (BookId, DedupKey) pair is UNIQUE in the table.
    /// </summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>The resolved model id that produced this finding.</summary>
    public string? BuiltWithModel { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;

    // ---------------------------------------------------------------------------
    // Static helper -- call before inserting a new finding so the dedup key
    // is computed the same way everywhere.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Computes a stable SHA-256 dedup key from the three canonical inputs.
    /// Inputs are lowercased and trimmed; internal whitespace in <paramref name="rationale"/>
    /// is collapsed to a single space so minor re-wording does NOT produce a new key.
    /// </summary>
    /// <param name="dimension">Finding dimension (e.g. "plot").</param>
    /// <param name="primaryChapterOrder">Order index of the primary chapter anchor, or 0 if none.</param>
    /// <param name="rationale">Free-text rationale from the model.</param>
    /// <returns>64-character lowercase hex SHA-256 digest.</returns>
    public static string ComputeDedupKey(string dimension, int primaryChapterOrder, string rationale)
    {
        var normalized = string.Join("|",
            (dimension ?? string.Empty).Trim().ToLowerInvariant(),
            primaryChapterOrder.ToString(),
            CollapseWhitespace((rationale ?? string.Empty).Trim().ToLowerInvariant()));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CollapseWhitespace(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
}
