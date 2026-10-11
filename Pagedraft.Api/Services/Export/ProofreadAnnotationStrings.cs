using System;

namespace Pagedraft.Api.Services.Export;

/// <summary>
/// Author-facing text the proofread DOCX annotator writes into Word comments and revision metadata.
/// Hebrew for a Hebrew book, English otherwise (the book's language, not the UI's: the file goes back to
/// the author). No em-dash in any string (house rule for user-facing text).
/// </summary>
internal static class ProofreadAnnotationStrings
{
    /// <summary>The <c>w:initials</c> of every comment the annotator writes.</summary>
    public const string Initials = "PD";

    /// <summary>The category <c>KtivMaleChecker</c> stamps on its deterministic suggestions.</summary>
    public const string KtivMaleCategory = "ktiv-male";

    public static bool IsHebrew(string? language) =>
        !string.IsNullOrEmpty(language) && language.StartsWith("he", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>w:author</c> of every comment and revision.</summary>
    public static string Author(string? language) =>
        IsHebrew(language)
            ? "PageDraft הגהה" // DRAFT he - needs native review
            : "PageDraft Proofreader";

    /// <summary>
    /// Comment line 1. A replacement names both sides, a pure deletion names what goes, a pure insertion
    /// names what comes.
    /// </summary>
    public static string SuggestionLine(string? language, string original, string suggested)
    {
        var he = IsHebrew(language);
        if (original.Length == 0)
            return he
                ? $"הצעה: להוסיף {suggested}" // DRAFT he - needs native review
                : $"Suggested: add {suggested}";
        if (suggested.Length == 0)
            return he
                ? $"הצעה: למחוק {original}" // DRAFT he - needs native review
                : $"Suggested: delete {original}";
        return he
            ? $"הצעה: {suggested} במקום {original}" // DRAFT he - needs native review
            : $"Suggested: {suggested} instead of {original}";
    }

    /// <summary>
    /// Appended to the plain export's file-name stem (before <c>.docx</c>) on an annotated export, so the
    /// annotated file never lands on top of the clean one in the author's downloads folder.
    /// </summary>
    public static string FileNameSuffix(string? language) =>
        IsHebrew(language)
            ? " - הגהה" // DRAFT he - needs native review
            : " - proofread";

    /// <summary>Comment line 2: present only for a category with an author-facing name.</summary>
    public static string? CategoryLine(string? language, string? category)
    {
        if (!string.Equals(category, KtivMaleCategory, StringComparison.Ordinal)) return null;
        return IsHebrew(language)
            ? "כתיב מלא לפי האקדמיה" // DRAFT he - needs native review
            : "Academy full spelling";
    }
}
