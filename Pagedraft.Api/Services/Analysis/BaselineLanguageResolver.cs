namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Single source of truth for normalizing a raw language/locale string to the canonical style-baseline
/// cache key. Hebrew/English (including locale forms like "he-IL"/"en-US") collapse to "he"/"en", a blank
/// value defaults to "he", and anything else is trimmed (and, when longer than five chars, shortened as below).
///
/// Extracted so EVERY path that reads or writes a ChapterStyleProfile / BookStyleBaseline keys the cache
/// identically and cannot drift:
///   • the style-baseline API (BooksController.ResolveBaselineLanguageAsync → GET status / POST build), and
///   • the inline LinguisticAnalysis path (AnalysisContextService.LoadOrBuildChapterStyleProfileAsync and
///     BuildBookStyleAverageProfileAsync), plus the background StyleBaselineService.
/// Without one shared rule the inline analysis path keyed profiles under the raw "en-US" while the builder
/// and status endpoints used "en", so coverage looked missing and chapter analyses omitted a baseline that
/// had in fact been built.
///
/// The Hebrew and English rungs are <see cref="LanguageTags"/> subtag matches (be-c09): <c>he</c>/<c>he-*</c>
/// and <c>en</c>/<c>en-*</c> only, so a value that merely begins with the letters (<c>help</c>, <c>hen</c>) is
/// not keyed as Hebrew. A value longer than five characters becomes its primary subtag when that is a 2- or
/// 3-letter code (<c>zh-Hant</c> -&gt; <c>zh</c>) and its first five characters otherwise; it used to become its
/// first two letters, which keyed <c>hebridean</c> or <c>hebrew</c> as Hebrew and <c>english</c> as English, the
/// prefix reading this rule removes. Either way the key stays inside the 10-character Language columns. Every
/// stored language in the dev DB is exactly <c>he</c> or <c>en</c> (2026-10-13), so no existing key moves.
/// Normalize is idempotent and <c>IsHebrew(Normalize(x))</c> equals <c>IsHebrew(x)</c> for every non-blank x, so a
/// caller that classifies the normalized value (the export) agrees with every caller that classifies the raw one.
/// </summary>
public static class BaselineLanguageResolver
{
    public static string Normalize(string? language)
    {
        var lang = language?.Trim();
        if (string.IsNullOrWhiteSpace(lang)) return "he";
        if (LanguageTags.IsHebrew(lang)) return LanguageTags.Hebrew;
        if (LanguageTags.IsTagFor(lang, "en")) return "en";
        if (lang.Length <= 5) return lang;
        var dash = lang.IndexOf('-');
        return dash is 2 or 3 ? lang[..dash] : lang[..5];
    }
}
