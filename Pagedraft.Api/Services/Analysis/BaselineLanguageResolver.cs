namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Single source of truth for normalizing a raw language/locale string to the canonical style-baseline
/// cache key. Hebrew/English (including locale forms like "he-IL"/"en-US") collapse to "he"/"en", a blank
/// value defaults to "he", and anything else is trimmed (and, when longer than five chars, reduced to its
/// two-letter prefix).
///
/// Extracted so EVERY path that reads or writes a ChapterStyleProfile / BookStyleBaseline keys the cache
/// identically and cannot drift:
///   • the style-baseline API (BooksController.ResolveBaselineLanguageAsync → GET status / POST build), and
///   • the inline LinguisticAnalysis path (AnalysisContextService.LoadOrBuildChapterStyleProfileAsync and
///     BuildBookStyleAverageProfileAsync), plus the background StyleBaselineService.
/// Without one shared rule the inline analysis path keyed profiles under the raw "en-US" while the builder
/// and status endpoints used "en", so coverage looked missing and chapter analyses omitted a baseline that
/// had in fact been built.
/// </summary>
public static class BaselineLanguageResolver
{
    public static string Normalize(string? language)
    {
        var lang = language?.Trim();
        if (string.IsNullOrWhiteSpace(lang)) return "he";
        if (lang.StartsWith("he", StringComparison.OrdinalIgnoreCase)) return "he";
        if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
        return lang.Length <= 5 ? lang : lang[..2];
    }
}
