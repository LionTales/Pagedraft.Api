using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services;

/// <summary>
/// THE rule for "which Active Proofread result is this exported unit's, and what does it say" (be-c05). The
/// whole-book proofread status (<see cref="BookProofreadService"/>: coverage, freshness, ChaptersEdited,
/// ChaptersUnreliable, UnreliableChapters, LastRunAt, the retry's ReliableThrough) and the annotated export
/// (<see cref="BookExportService"/>: which result a unit is annotated from, and whether <c>?unreliable=skip</c>
/// skips it) both call it, so the dialog that promises "skip these chapters" lists exactly the chapters the
/// export then skips.
///
/// <para>THE UNIT. A unit is what the export renders (<see cref="BookExportService.RenderableUnitSourcesOf"/>,
/// whose scene-or-row decision is <see cref="ChapterTextSource.StampsHoldTheChaptersCurrentText"/>): the chapter
/// row, whose result is the Chapter-scope row keyed by <c>ChapterId</c> with <c>SceneId</c> null, or one scene,
/// whose result is that scene's Scene-scope row. A result keyed to anything else (a Chapter-scope row on a
/// chapter that exports through its scenes, which stays Active beside the scene rows because archiving is keyed
/// by scope + scene; a Scene-scope row of a scene that is not a unit) belongs to NO exported unit: it is not in
/// the file, so it can neither make the chapter "unreliable" nor "covered".</para>
///
/// <para>THE LANGUAGE. Only results in the given language count, compared through
/// <see cref="BaselineLanguageResolver.Normalize"/> on both sides. The status is keyed by the request language
/// (the client sends the book's); the export has no language parameter and passes the book's language, so for
/// the client the two read the same results. Archiving is language-blind, so a unit's only Active result can be
/// in another language (the book's language changed after the run): that unit counts as NOT proofread in this
/// language, the plan re-runs it, and the export leaves it unannotated rather than writing a proofread made
/// under another language's rules into the file. The chapter view (<c>AnalysisController.ActiveAnalysesQuery</c>)
/// is language-blind and still shows it.</para>
///
/// <para>THE CURRENT RESULT. Per unit, the NEWEST such result (ties: the later row in the caller's order, which
/// both callers keep in id order). Normally there is exactly one; more than one Active row per unit only
/// survives a non-transactional archive, and the newest is what the chapter view puts first.</para>
/// </summary>
public static class ProofreadUnitResults
{
    /// <summary>One exported unit: the chapter row (<paramref name="SceneId"/> null) or one scene.</summary>
    public readonly record struct UnitKey(Guid ChapterId, Guid? SceneId);

    /// <summary>Everything this rule reads of one Active Proofread result.</summary>
    public readonly record struct ResultFacts(
        Guid ChapterId, Guid? SceneId, AnalysisScope Scope, DateTimeOffset CreatedAt, string? Language, bool Unreliable);

    /// <summary>What a unit's current result says.</summary>
    public enum Verdict
    {
        /// <summary>No Active result of this unit in the language: not proofread.</summary>
        None,
        /// <summary>The current result is a usable proofread (fresh or not is the caller's timestamp question).</summary>
        Reliable,
        /// <summary>The current result is flagged <see cref="AnalysisResult.ProofreadResultUnreliable"/>.</summary>
        Unreliable,
    }

    /// <summary>The unit a result is keyed to, or null when it is keyed to neither unit shape.</summary>
    public static UnitKey? UnitOf(ResultFacts result) => result.Scope switch
    {
        AnalysisScope.Chapter when result.SceneId == null => new UnitKey(result.ChapterId, null),
        AnalysisScope.Scene when result.SceneId != null => new UnitKey(result.ChapterId, result.SceneId),
        _ => null,
    };

    /// <summary>Whether a result's stored language is <paramref name="normalizedLanguage"/> (already normalized).</summary>
    public static bool IsInLanguage(string? resultLanguage, string normalizedLanguage)
        => BaselineLanguageResolver.Normalize(resultLanguage) == normalizedLanguage;

    /// <summary>
    /// Each unit's current result: the newest row keyed to a unit, in <paramref name="language"/> (normalized
    /// here). Callers look their units up by <see cref="UnitKey"/>; a key no unit asks for is simply never read.
    /// </summary>
    public static Dictionary<UnitKey, T> CurrentByUnit<T>(
        IEnumerable<T> rows, string? language, Func<T, ResultFacts> facts)
    {
        var lang = BaselineLanguageResolver.Normalize(language);
        var byUnit = new Dictionary<UnitKey, T>();
        // OrderBy is stable: equal timestamps keep the caller's order, so the later row wins the tie.
        foreach (var row in rows.OrderBy(r => facts(r).CreatedAt))
        {
            var f = facts(row);
            if (!IsInLanguage(f.Language, lang)) continue;
            if (UnitOf(f) is not { } key) continue;
            byUnit[key] = row;
        }
        return byUnit;
    }

    /// <summary>The verdict of a unit whose current result is <paramref name="current"/> (null: none).</summary>
    public static Verdict VerdictOf(ResultFacts? current) => current switch
    {
        null => Verdict.None,
        { Unreliable: true } => Verdict.Unreliable,
        _ => Verdict.Reliable,
    };
}
