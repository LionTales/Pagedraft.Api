namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// The bucket a persisted <c>BookFinding.Status</c> value falls into. TOTAL over the vocabulary: every
/// member of <see cref="FindingStatusPartition"/> maps to exactly one non-<see cref="Unknown"/> bucket, and
/// <c>Wave3StageSignalContractTests</c> proves it by REFLECTING over the declared constants rather than
/// restating them, so a fifth status member that nobody classified fails the build instead of silently
/// landing in whichever branch happens to be the fallback.
/// </summary>
public enum FindingStatusBucket
{
    /// <summary>Not a member of the vocabulary: null, blank, or a value this build does not know. Callers
    /// must treat it as fail-CLOSED (see <see cref="FindingStatusPartition.IsUserActed"/>), never as open.</summary>
    Unknown = 0,

    /// <summary>Strictly <c>open</c>: the author has never touched the row.</summary>
    Open,

    /// <summary>The author looked at the row and moved on without finishing with it. Neither open nor
    /// resolved: it is the third bucket the progress counts cannot derive one from the other because of.</summary>
    Acknowledged,

    /// <summary><c>dismissed</c> or <c>done</c>: the author is finished with the row, either way.</summary>
    Resolved,
}

/// <summary>
/// The ONE place the persisted <c>BookFinding.Status</c> vocabulary (open | acknowledged | dismissed | done)
/// is declared, parsed and partitioned. Every production site that needs a status string or a status
/// question routes through here: the entity default (<c>BookFinding.Status</c>), the EF column default
/// (<c>AppDbContext</c>), the fresh-finding projection and the rebuild reconciler
/// (<see cref="BookReviewService"/>), the near-duplicate fence (<see cref="NearDuplicateCollapser"/>), and
/// the PATCH endpoint that writes it (<c>BooksController.UpdateFindingStatus</c>). Do NOT re-spell the
/// strings at a call site; the count on the status payload and the ledger's own grouping drifting apart is
/// exactly what this exists to prevent (the client's findings ledger renders open + acknowledged as its
/// ACTIVE group and dismissed + done as its RESOLVED group, and a spine that counted differently would
/// report progress the ledger contradicts on the next screen).
///
/// CASING POLICY: comparisons here are TRIMMED and CASE-INSENSITIVE, deliberately, and that is the policy
/// for the whole vocabulary. Every writer in the system emits the lowercase form and nothing else - the
/// entity default, the EF column default, the fresh-finding projection, and <see cref="TryParse"/>, which
/// case-folds its input before mapping - so the tolerance is never exercised by data this app wrote. It is
/// here for two other reasons: (1) the SQL Server column is collated CI (Latin1_General_100_CI_AS_SC_UTF8),
/// so a SQL-side predicate on this column ALREADY matches a case variant, and an ordinal in-memory
/// comparison would answer differently from the database for the same question; (2) a hand-edited or
/// imported row reading <c>Open</c> carries no author decision under any reading, so classifying it as
/// user-acted (which an ordinal comparison does) would preserve it forever against the vanished-open
/// delete while the spine's progress line counted it as open. The tolerance is for CASE ONLY - an UNKNOWN
/// member is still fail-closed, see <see cref="IsUserActed"/>.
/// </summary>
public static class FindingStatusPartition
{
    /// <summary>Never looked at. The value every fresh finding is inserted with.</summary>
    public const string Open = "open";

    /// <summary>Seen and left for later. Neither open nor resolved.</summary>
    public const string Acknowledged = "acknowledged";

    /// <summary>The author rejected the finding.</summary>
    public const string Dismissed = "dismissed";

    /// <summary>The author acted on the finding.</summary>
    public const string Done = "done";

    /// <summary>
    /// The whole vocabulary, in workflow order. Bound to the declared constants by reflection in
    /// <c>Wave3StageSignalContractTests</c>, so this cannot fall behind a newly added member.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[] { Open, Acknowledged, Dismissed, Done };

    /// <summary>
    /// What the PATCH endpoint accepts, mapped to what gets persisted. The FE sends the IMPERATIVE verb
    /// (acknowledge / dismiss); the stored adjective form is accepted too so the endpoint is tolerant of
    /// either, and "done" / "open" are identical in both forms. This dictionary is the only authority for
    /// what is accepted - <see cref="AcceptedInputs"/> and the endpoint's error message both read it.
    /// </summary>
    private static readonly Dictionary<string, string> AcceptedByInput = new(StringComparer.OrdinalIgnoreCase)
    {
        [Open] = Open,
        ["acknowledge"] = Acknowledged,
        [Acknowledged] = Acknowledged,
        ["dismiss"] = Dismissed,
        [Dismissed] = Dismissed,
        [Done] = Done,
    };

    /// <summary>Every raw token <see cref="TryParse"/> accepts, ordered so an error message built from it is
    /// deterministic. Derived from the mapping, never hand-listed beside it.</summary>
    public static IReadOnlyList<string> AcceptedInputs =>
        AcceptedByInput.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Classifies a persisted status value. TOTAL over <see cref="All"/>; anything else (null, blank, or a
    /// member this build does not know) is <see cref="FindingStatusBucket.Unknown"/>. Trimmed and
    /// case-insensitive per the casing policy above.
    /// </summary>
    public static FindingStatusBucket BucketOf(string? status)
    {
        var s = status?.Trim();
        if (string.IsNullOrEmpty(s)) return FindingStatusBucket.Unknown;
        if (Eq(s, Open)) return FindingStatusBucket.Open;
        if (Eq(s, Acknowledged)) return FindingStatusBucket.Acknowledged;
        if (Eq(s, Dismissed) || Eq(s, Done)) return FindingStatusBucket.Resolved;
        return FindingStatusBucket.Unknown;
    }

    /// <summary>Strictly <c>open</c>: never looked at. An acknowledged finding is NOT open, and neither is
    /// an unknown value.</summary>
    public static bool IsOpen(string? status) => BucketOf(status) == FindingStatusBucket.Open;

    /// <summary><c>dismissed</c> or <c>done</c>: the author is finished with it, either way. An unknown
    /// value is NOT resolved - it must not be counted as progress the author made.</summary>
    public static bool IsResolved(string? status) => BucketOf(status) == FindingStatusBucket.Resolved;

    /// <summary>
    /// The author has acted on this row, so a rebuild must not delete it or fold a fresh finding onto it
    /// cheaply. This is the exact complement of <see cref="IsOpen"/> and NOT the complement of
    /// <see cref="IsResolved"/>: an UNKNOWN status is user-acted (fail-CLOSED, so a status member added
    /// later cannot quietly opt a row out of the preservation fence) while still not counting as resolved
    /// progress. Acknowledged is user-acted for the same reason.
    /// </summary>
    public static bool IsUserActed(string? status) => !IsOpen(status);

    /// <summary>
    /// Maps a caller-supplied token (verb or adjective, any casing, surrounding whitespace tolerated) to the
    /// value that gets persisted. False when the token is not in <see cref="AcceptedInputs"/>.
    /// </summary>
    public static bool TryParse(string? raw, out string status)
    {
        if (raw != null && AcceptedByInput.TryGetValue(raw.Trim(), out var mapped))
        {
            status = mapped;
            return true;
        }

        status = string.Empty;
        return false;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
