using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
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
/// Status values:    declared by Services.Analysis.FindingStatusPartition (see Status below)
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

    /// <summary>Workflow status. The vocabulary, its casing policy and the open/resolved split are declared
    /// ONCE by <see cref="Services.Analysis.FindingStatusPartition"/>; read every status question from there
    /// rather than comparing this string to a literal.</summary>
    public string Status { get; set; } = Services.Analysis.FindingStatusPartition.Open;

    /// <summary>
    /// Deterministic dedup key used to preserve user-set Status across rebuild runs.
    /// Computed as a stable SHA-256 over the composite:
    ///   dimension (lowercased, trimmed)
    ///   + "|" + primaryChapterOrder (order of the first RESOLVED ChapterAnchor, or the literal
    ///           <see cref="NoAnchorKeyToken"/> when the finding anchors NO chapter -- see
    ///           <see cref="ComputeDedupKey"/> on why that is NOT the number 0)
    ///   + "|" + rationale (lowercased, trimmed, collapsed whitespace)
    /// Hex-encoded (64 chars). The (BookId, Language, DedupKey) triple is UNIQUE in the table.
    /// </summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>
    /// TRANSIENT (never persisted -- <see cref="NotMappedAttribute"/>): the key this finding WOULD have had under
    /// the pre-2026-07-12 derivation (<see cref="ComputeLegacyDedupKeyV1"/>). Set only on FRESHLY BUILT findings,
    /// and read only by the persist step, which -- when a fresh finding's current key matches no existing row --
    /// falls back to matching on this legacy key so a row persisted under the OLD derivation is RE-MATCHED, keeps
    /// the user's Status (acknowledged / dismissed / done), and has its stored key UPGRADED in place. That lazy,
    /// self-healing backfill is what stops the sentinel de-collision from orphaning every user-acted row.
    /// Null on rows loaded from the DB (nothing to migrate from -- their stored key IS the key).
    /// </summary>
    [NotMapped]
    public string? LegacyDedupKeyV1 { get; set; }

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
    /// The token hashed in place of the primary chapter order when a finding anchors NO chapter (a book-wide
    /// finding). Deliberately NOT a number.
    ///
    /// Chapter orders are 0-BASED (every book runs 0..N-1), so chapter 0 is a REAL chapter in EVERY book. The
    /// pre-2026-07-12 derivation used the number 0 as the "no anchor" sentinel, which COLLIDED with it: a
    /// book-wide finding and a first-chapter finding with the same dimension + rationale hashed to the SAME key,
    /// so one of them was silently discarded by the union's first-occurrence-wins rule (and, had both reached the
    /// DB, the UNIQUE (BookId, Language, DedupKey) index would have rejected the second). A non-numeric token
    /// cannot collide with any chapter order, present or future.
    /// </summary>
    private const string NoAnchorKeyToken = "none";

    /// <summary>
    /// Computes a stable SHA-256 dedup key from the three canonical inputs.
    ///
    /// WHAT IT ABSORBS — and NOTHING else. The inputs are trimmed + lower-cased, and every internal whitespace
    /// RUN in <paramref name="rationale"/> is collapsed to a single space. So the key is invariant under case,
    /// leading/trailing space, and re-spacing (a newline vs a space, a double space vs a single). It is EXACT
    /// TEXT otherwise: change ONE character of the prose and you get a completely different key.
    ///
    /// WHAT IT DOES NOT ABSORB: RE-WORDING. This is the point most easily got wrong — until 2026-07-12 this very
    /// comment claimed the opposite ("minor re-wording does NOT produce a new key"), a property the code has
    /// never implemented, and that false claim is part of why the duplicate-findings bug shipped: it asserted the
    /// protection existed, so nobody looked. A hash cannot be tolerant to rewording BY CONSTRUCTION. The model
    /// re-emits the same finding with one word added ("קשת דמויות ברורה" vs "קשת דמויות ברורה ומרשימה") and the
    /// two rationales hash to two unrelated keys, so both rows survive this dedup.
    ///
    /// WHERE REWORDING IS ACTUALLY HANDLED: NOT here. Near-duplicate collapse is a SEPARATE, BUILD-TIME pass —
    /// <c>NearDuplicateCollapser</c>, run by <c>BookReviewService.UnionAndDedup</c> AFTER this exact-key dedup —
    /// which buckets by (dimension, resolved chapter order) and merges rationales whose normalized content-token
    /// sets are highly similar. It deliberately does NOT touch this derivation: the STORED key must stay stable
    /// across rebuilds or the persist step loses the user's Status (see <see cref="ComputeLegacyDedupKeyV1"/>),
    /// so collapse happens on the freshly built set and the survivor keeps its own exact-text key.
    /// </summary>
    /// <param name="dimension">Finding dimension (e.g. "plot").</param>
    /// <param name="primaryChapterOrder">Order index of the primary chapter anchor AFTER it has been resolved
    /// against the book's real chapters, or NULL when the finding anchors no chapter at all. Null hashes as
    /// <see cref="NoAnchorKeyToken"/>, never as 0 -- 0 is a real chapter order (see that constant).</param>
    /// <param name="rationale">Free-text rationale from the model.</param>
    /// <returns>64-character lowercase hex SHA-256 digest.</returns>
    public static string ComputeDedupKey(string dimension, int? primaryChapterOrder, string rationale)
    {
        var normalized = string.Join("|",
            (dimension ?? string.Empty).Trim().ToLowerInvariant(),
            primaryChapterOrder?.ToString(CultureInfo.InvariantCulture) ?? NoAnchorKeyToken,
            CollapseWhitespace((rationale ?? string.Empty).Trim().ToLowerInvariant()));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// The PRE-2026-07-12 dedup-key derivation (V1), preserved BYTE-FOR-BYTE. It is a MIGRATION SHIM, not a key
    /// source: never stamp a new row with it.
    ///
    /// WHY IT EXISTS. The persist step matches an incoming finding to its cached row on (BookId, Language,
    /// DedupKey) in order to carry the user's Status (acknowledged / dismissed / done) across a rebuild. When
    /// <see cref="ComputeDedupKey"/>'s derivation changed (the order-0 sentinel de-collision), EVERY cached row's
    /// key went stale, which would have ORPHANED every user-acted finding and silently lost its Status. So the
    /// build re-derives the V1 key for each fresh finding too (from the SAME raw model item V1 hashed) and the
    /// persist step falls back to it when the new key matches nothing, then UPGRADES the matched row's stored key.
    /// The migration is therefore lazy, idempotent and self-healing, and it needs no data migration.
    ///
    /// WHY NOT A BACKFILL MIGRATION (recompute every row's key from the row itself)? Because the key is NOT a
    /// function of the persisted row: the glossary / dynamic term-repair layers rewrite a finding's Rationale IN
    /// PLACE after the key is stamped (deliberately -- the key hashes the RAW model prose so it stays stable while
    /// the repair is display-only). The raw pre-repair rationale is never persisted, so no recompute-from-row can
    /// reproduce the stored key for a repaired finding, and a backfill would have CORRUPTED exactly those rows.
    /// </summary>
    /// <param name="primaryChapterOrder">V1's primary-order input: the RAW (unresolved) first model anchor's Order,
    /// or 0 when the model emitted no anchors -- V1's colliding sentinel, reproduced on purpose.</param>
    internal static string ComputeLegacyDedupKeyV1(string dimension, int primaryChapterOrder, string rationale)
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
