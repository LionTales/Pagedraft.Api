using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Loads the suggestions of already-loaded analysis results in a SECOND query, instead of
/// <c>Include(a =&gt; a.Suggestions)</c> on the result query (plan whole-book-proofread-and-annotated-docx-2026-10-11,
/// c14). A single-query include joins every suggestion row to its result row, so the result's chapter-sized columns
/// (<c>ResultText</c>, <c>SourceTextSnapshot</c>, plus <c>PromptUsed</c>/<c>StructuredResult</c>) are repeated once
/// PER SUGGESTION: on the owner's 38,209-word chapter (1,476 suggestions, ~207,000 characters in each of the two
/// text columns) that is ~1.2 GB through SqlClient's async reader, and every history read took 120 to 150 s. Here
/// the result columns are read once (by the caller's own query) and this query selects only suggestion columns.
/// Same root cause as c12's <c>BookExportService.ActiveProofreadUnitsQuery</c>; both are pinned by
/// <c>ToQueryString</c> tests (<c>AnalysisSuggestionLoadQueryShapeTests</c>).
/// </summary>
internal static class AnalysisSuggestionLoad
{
    /// <summary>
    /// The suggestions of the given results, as a query (so its SQL can be pinned). Selects only
    /// <c>[AnalysisSuggestions]</c> columns: no join back to <c>[AnalysisResults]</c>.
    /// </summary>
    internal static IQueryable<AnalysisSuggestion> OfResults(AppDbContext db, IReadOnlyCollection<Guid> resultIds)
    {
        var ids = resultIds.ToList();
        return db.AnalysisSuggestions.Where(s => ids.Contains(s.AnalysisResultId));
    }

    /// <summary>
    /// Fills <see cref="AnalysisResult.Suggestions"/> of each NO-TRACKING result with its suggestions, read in one
    /// query. Equivalent to the include it replaces: every suggestion of every result, in no guaranteed order
    /// (<c>AnalysisController.ToDto</c> orders them by <c>OrderIndex</c>, as it always did).
    /// Not for tracked results: the change tracker would fix the navigations up on its own and this would add
    /// each suggestion a second time.
    /// </summary>
    internal static async Task AttachToUntrackedAsync(
        AppDbContext db, IReadOnlyList<AnalysisResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return;

        var suggestions = await OfResults(db, results.Select(r => r.Id).ToList())
            .AsNoTracking()
            .ToListAsync(ct);

        var byResult = suggestions.ToLookup(s => s.AnalysisResultId);
        foreach (var result in results)
            result.Suggestions = byResult[result.Id].ToList();
    }
}
