namespace Pagedraft.Api.Models;

/// <summary>
/// WHAT PART OF THE PRODUCT a <see cref="FeedbackItem"/> is about. An OPEN vocabulary enforced at the
/// application layer against this allowlist - not a CLR enum, not a DB <c>CHECK</c> constraint - which is
/// the entire reason <c>FeedbackItem.Area</c> is a plain string column (Show C2, d1 section (1)).
///
/// <para>MOUNT #2 EXTENDS THIS LIST BY ONE LINE. Adding <c>"suggestion-card"</c> here plus one constant in
/// <see cref="FeedbackTargetTypes"/> and one arm in the target-existence check is the whole schema story
/// for the next mount; it is a code change reviewed like any other, never a migration.</para>
/// </summary>
public static class FeedbackAreas
{
    /// <summary>Show's assistant answers - mount #1, and the only value C2 writes.</summary>
    public const string ChatAnswer = "chat-answer";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ChatAnswer
    };

    public static bool IsKnown(string? area) => area != null && All.Contains(area);
}

/// <summary>
/// WHAT KIND OF THING <c>FeedbackItem.TargetId</c> points at. Same open-vocabulary rule as
/// <see cref="FeedbackAreas"/>, and additionally the DISCRIMINATOR two behaviours switch on: the
/// target-existence check that produces <c>400 targetNotFound</c>, and the evidence join that composes the
/// triage detail. A value added here without an arm in both is a target nobody can validate and nobody
/// can read evidence for.
/// </summary>
public static class FeedbackTargetTypes
{
    /// <summary>A persisted Show turn (<c>ConversationMessage.Id</c>) - mount #1's only target.</summary>
    public const string ConversationMessage = "conversation-message";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ConversationMessage
    };

    public static bool IsKnown(string? targetType) => targetType != null && All.Contains(targetType);
}

/// <summary>The two verdicts. A thumbs pair is the whole vocabulary; there is no neutral value.</summary>
public static class FeedbackVerdicts
{
    public const string Up = "up";
    public const string Down = "down";

    /// <summary>
    /// C3'S HALF OF ITS CONSUMPTION PREDICATE (the other half is <see cref="FeedbackStatuses.New"/>).
    /// Named rather than spelled inline so a rename cannot silently un-wire the pipeline.
    /// </summary>
    public const string C3Consumes = Down;

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Up, Down };

    public static bool IsKnown(string? verdict) => verdict != null && All.Contains(verdict);
}

/// <summary>
/// THE TRIAGE LIFECYCLE, and C3's plug (Show C2, d1 sections (1), (4) and (5)). The vocabulary is d1's and
/// frozen; the TRANSITION GRAPH below is this implementation's, because d1 fixed the words and the rule
/// that <c>PATCH /api/feedback/{id}/status</c> is their only writer, not which moves are legal.
/// </summary>
public static class FeedbackStatuses
{
    /// <summary>Nobody has read it. Half of C3's consumption predicate.</summary>
    public const string New = "New";

    /// <summary>Read, not yet judged.</summary>
    public const string Triaged = "Triaged";

    /// <summary>Judged a real defect.</summary>
    public const string ConfirmedBug = "ConfirmedBug";

    /// <summary>Judged not a defect (or not actionable).</summary>
    public const string Dismissed = "Dismissed";

    /// <summary>The confirmed defect is gone. Reachable only from <see cref="ConfirmedBug"/>.</summary>
    public const string Fixed = "Fixed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        New, Triaged, ConfirmedBug, Dismissed, Fixed
    };

    public static bool IsKnown(string? status) => status != null && All.Contains(status);

    /// <summary>
    /// THE LEGAL MOVES, stated as a map so the illegal ones are a property of data rather than of a
    /// forgotten <c>if</c>. Three decisions are load-bearing and are here rather than in a comment on the
    /// endpoint, because they are what make the vocabulary mean anything:
    ///
    /// <para>(1) <b>NOTHING RETURNS TO <see cref="New"/>.</b> "New" is not "untouched", it is C3's INBOX:
    /// C3 consumes <c>Status = New AND Verdict = down</c>. A hand transition back into it would put an
    /// already-judged row back in the queue and C3 would re-check it forever. Re-opening a wrong
    /// judgement goes to <see cref="Triaged"/> or <see cref="ConfirmedBug"/> instead, which says the same
    /// thing without re-arming the pipeline.</para>
    ///
    /// <para>(2) <b><see cref="Fixed"/> is reachable only from <see cref="ConfirmedBug"/>.</b> "Fixed"
    /// asserts that a defect existed and no longer does; reaching it from <see cref="New"/> or
    /// <see cref="Triaged"/> would let a row claim a fix for something nobody ever confirmed was broken,
    /// which is exactly the state that makes a status column decorative.</para>
    ///
    /// <para>(3) <b>A regression re-opens.</b> <see cref="Fixed"/> -&gt; <see cref="ConfirmedBug"/> is
    /// legal, because a fix that did not hold is the same defect and not a new report.</para>
    ///
    /// <para>A transition to the status a row ALREADY holds is not in this map and is not an error either:
    /// it is an idempotent no-op that leaves <c>StatusChangedAt</c> untouched (a double-clicked button is
    /// not an event), handled at the one call site rather than by seeding every row of this map with
    /// itself.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> LegalTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [New] = new HashSet<string>(StringComparer.Ordinal) { Triaged, ConfirmedBug, Dismissed },
            [Triaged] = new HashSet<string>(StringComparer.Ordinal) { ConfirmedBug, Dismissed },
            [ConfirmedBug] = new HashSet<string>(StringComparer.Ordinal) { Fixed, Dismissed },
            [Dismissed] = new HashSet<string>(StringComparer.Ordinal) { Triaged, ConfirmedBug },
            [Fixed] = new HashSet<string>(StringComparer.Ordinal) { ConfirmedBug }
        };

    /// <summary>
    /// True when <paramref name="to"/> may be reached from <paramref name="from"/>. A move to the SAME
    /// status returns false here and is handled as a no-op by the caller - this method answers "is this a
    /// legal CHANGE", which is a different question from "may this request proceed".
    /// </summary>
    public static bool IsLegalTransition(string from, string to)
        => LegalTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
}

/// <summary>
/// The write-side length caps, declared once so the column widths, the validation and the wire contract
/// the client's character counter renders cannot disagree.
///
/// <para>OVER-LENGTH IS A 400, NOT A SILENT TRUNCATION, and the asymmetry with
/// <c>ConversationsController.Rename</c> (which truncates a long title) is deliberate:
/// <see cref="TextChars"/> holds the READER'S OWN WORDS, and quietly cutting the last sentence off a bug
/// report is worse than refusing it - the client knows the cap and counts against it.</para>
/// </summary>
public static class FeedbackCaps
{
    /// <summary>
    /// 2000 characters - generous headroom for a paragraph of reader commentary while keeping the field
    /// honest about what it is for. d1 section (1); frozen.
    /// </summary>
    public const int TextChars = 2000;

    /// <summary>Mirrors <c>FeedbackItem.InstallationId</c>'s column width.</summary>
    public const int InstallationIdChars = 100;

    /// <summary>
    /// The per-field bound on the strings inside <c>ContextJson</c> (route / uiLanguage / appBuild).
    /// The column is <c>nvarchar(max)</c>, so this is not a storage limit: it stops an unbounded client
    /// string from riding into a blob nothing else bounds.
    /// </summary>
    public const int ContextFieldChars = 500;
}
