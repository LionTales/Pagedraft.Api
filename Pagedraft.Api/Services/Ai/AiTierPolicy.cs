using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// THE ONE PLACE that decides which tasks a model tier may move, and how a tier is spelled as an
/// <c>Ai:FeatureModels</c> key (model-tier-fast-thinking plan, p3-1 enforcement layer E2).
///
/// WHY IT IS A NAMED CONSTANT AND NOT CONFIG. Phase 2 measured the cloud tier per TASK and the verdict is
/// per task, not global (plan "## p2 decision"): GO on LinguisticAnalysis and on HEBREW Proofread; NO-GO on
/// BookReview and on Proofread_en because they are UNMEASURED, not because they lost; LineEdit /
/// Summarization / AnalysisRepair / GenericChat unmeasured; TermRepair additionally routing-only by a
/// STANDING cost/privacy decision that is independent of quality (appsettings <c>_comment_TermRepair</c>).
/// Adding a task to the tier is therefore a CODE change here - reviewable, and it turns
/// <c>AiTierConfigParityTests</c> red until the reviewer also decides what the new task's cloud entry is -
/// rather than a config edit somebody makes without reading any of that.
///
/// READ WITH <see cref="LinguisticModelResolver.ResolveForTask(AiOptions, AiTaskType, string?, AiTier)"/>:
/// that method is the single implementation of the precedence, and <see cref="AiRouter"/> delegates to it,
/// so this allowlist is consulted by BOTH surfaces by construction rather than by convention.
/// </summary>
public static class AiTierPolicy
{
    /// <summary>
    /// The tier's key suffix, lower-case to match the existing <c>_en</c> language-suffix idiom
    /// (<c>Proofread_en</c>). Composed as <c>{task}_{tier}</c>, e.g. <c>Proofread_thinking</c>.
    /// </summary>
    public const string ThinkingKeySuffix = "thinking";

    /// <summary>
    /// The stored-string form of <see cref="AiTier.Thinking"/> (<c>Book.AiTier</c>). Matched
    /// case-insensitively on read; written in this exact casing.
    /// </summary>
    public const string ThinkingStoredValue = "thinking";

    /// <summary>The stored-string form of <see cref="AiTier.Fast"/>. Null/absent means the same thing.</summary>
    public const string FastStoredValue = "fast";

    /// <summary>
    /// TASKS THE TIER MAY MOVE. Exactly the two tasks p2-4 gave a GO, and nothing else. Every other
    /// <see cref="AiTaskType"/> ignores the tier argument entirely, so its resolution is byte-identical to
    /// the pre-tier behaviour on both tiers - pinned per task by
    /// <c>LinguisticModelResolverTierAgreementTests</c>.
    ///
    /// "Hebrew Proofread only" is NOT expressible here, because <c>Proofread_en</c> is not an
    /// <see cref="AiTaskType"/> - it is a FeatureModels key SUFFIX. English exclusion is a PRECEDENCE
    /// property instead: <c>{task}_{lang}</c> outranks <c>{task}_{tier}</c>, so an English book on the
    /// thinking tier resolves <c>Proofread_en</c> and never reaches the tier rung (layer E3).
    /// </summary>
    public static readonly IReadOnlySet<AiTaskType> TieredTasks = new HashSet<AiTaskType>
    {
        AiTaskType.LinguisticAnalysis,
        AiTaskType.Proofread
    };

    /// <summary>True when the tier is allowed to change this task's routing at all.</summary>
    public static bool IsTiered(AiTaskType task) => TieredTasks.Contains(task);

    /// <summary>
    /// The <c>Ai:FeatureModels</c> key a tier rung would consult for this (task, tier), or NULL when there is
    /// no tier rung to consult at all - which is the case for EITHER of the two independent reasons the
    /// restriction rests on:
    ///   • the tier is <see cref="AiTier.Fast"/> (Fast IS the untiered baseline; a <c>{task}_fast</c> key is
    ///     dead config and <c>AiTierConfigParityTests</c> fails if one is ever written), or
    ///   • the task is outside <see cref="TieredTasks"/>.
    /// Returning null rather than a key nobody configured is what makes "the tier is ignored for this task"
    /// a structural fact instead of a lookup that happens to miss today.
    /// </summary>
    public static string? TierKeyFor(AiTaskType task, AiTier tier) =>
        tier == AiTier.Thinking && IsTiered(task)
            ? $"{task}_{ThinkingKeySuffix}"
            : null;

    /// <summary>
    /// DEFENSIVE parse of the stored <c>Book.AiTier</c> string. Null, empty, whitespace, an unknown token, a
    /// value written by a newer build, or a hand-edited row all resolve to <see cref="AiTier.Fast"/> and
    /// NEVER throw. The tier gates paid, privacy-relevant cloud routing, so the only safe failure direction
    /// is back to local.
    /// </summary>
    public static AiTier Parse(string? stored) =>
        string.Equals(stored?.Trim(), ThinkingStoredValue, StringComparison.OrdinalIgnoreCase)
            ? AiTier.Thinking
            : AiTier.Fast;

    /// <summary>The canonical stored form of a tier, for writing <c>Book.AiTier</c>.</summary>
    public static string ToStoredValue(AiTier tier) =>
        tier == AiTier.Thinking ? ThinkingStoredValue : FastStoredValue;
}
