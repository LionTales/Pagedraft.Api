namespace Pagedraft.Api.Models;

/// <summary>
/// ONE BOOK'S TIER OVERRIDE FOR ONE TASK (tier-ux-rework plan, c1). <see cref="Book.AiTier"/> remains the
/// book-level DEFAULT seed; a row here says "this one task departs from that default".
///
/// WHY A TABLE AND NOT A JSON COLUMN ON <see cref="Book"/>. Three reasons, in order of weight:
///   • WRITES ARE PER TASK AND INDEPENDENT. The plan's decided semantics are that setting the book default
///     must NOT clobber explicit per-task overrides, and clearing one task must not disturb another. A JSON
///     blob makes every write a whole-document read-modify-write, so two concurrent PUTs for DIFFERENT tasks
///     are last-writer-wins on each other. A row per (book, task) makes them touch disjoint rows.
///   • THE TEST SUITE RUNS ON THE IN-MEMORY PROVIDER. EF's SQL Server JSON-column mapping and the in-memory
///     provider do not behave identically for owned collections, so a JSON column would be exercised
///     differently in tests than in production - exactly the synthetic-vs-production split this codebase has
///     been bitten by before. A plain table is the same shape on both providers.
///   • THE LOOKUP STILL COSTS ONE QUERY. <c>BookAiTierResolver</c> projects the book default and this row's
///     token in a single query keyed on the composite PK, so per-task storage adds no round trip.
///
/// <see cref="TaskKey"/> is the <c>AiTaskType</c> NAME, not an <c>AnalysisType</c> name, and that is
/// load-bearing rather than incidental - see <c>Services.Ai.AiTierPolicy.TryParseTaskKey</c> for the
/// many-to-one mapping that forces it.
///
/// <see cref="Tier"/> is a free STRING for the same reason <see cref="Book.AiTier"/> is: a value written by a
/// newer build or a hand-edited row must degrade to the local tier rather than throw.
/// </summary>
public class BookAiTaskTier
{
    public Guid BookId { get; set; }

    /// <summary>The <c>AiTaskType</c> name this override applies to, e.g. "Proofread".</summary>
    public string TaskKey { get; set; } = string.Empty;

    /// <summary>"fast" | "thinking". Anything else is doubt, and doubt resolves to fast.</summary>
    public string? Tier { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Book? Book { get; set; }
}
