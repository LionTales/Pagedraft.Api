using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Export;

/// <summary>
/// Author-facing text the proofread DOCX annotator writes into Word comments and revision metadata.
/// Hebrew for a Hebrew book, English otherwise (the book's language, not the UI's: the file goes back to
/// the author). No em-dash in any string (house rule for user-facing text). Hebrew-ness is
/// <see cref="LanguageTags.IsHebrew"/>, so a null language reads English here exactly as it reads not-Hebrew in
/// the DOCX conversion; the export passes the book's language already resolved by
/// <see cref="Analysis.BaselineLanguageResolver.Normalize"/>, so neither ever sees a null from it.
///
/// <para>A suggestion is a tracked change ONLY; a comment is written only when it carries something the
/// change does not show (owner decision 2026-10-12: a comment that repeated the change cost the author a
/// third click per suggestion, and Word's Accept never clears a comment). <see cref="CommentLines"/> is
/// that rule.</para>
/// </summary>
internal static class ProofreadAnnotationStrings
{
    /// <summary>The <c>w:initials</c> of every comment the annotator writes.</summary>
    public const string Initials = "PD";

    /// <summary>The category <c>KtivMaleChecker</c> stamps on its deterministic suggestions.</summary>
    public const string KtivMaleCategory = "ktiv-male";

    /// <summary>Longest explanation written into a comment, before the ellipsis.</summary>
    public const int MaxExplanationLength = 500;

    /// <summary>The <c>w:author</c> of every comment and revision.</summary>
    public static string Author(string? language) =>
        LanguageTags.IsHebrew(language)
            ? "PageDraft הגהה" // DRAFT he - needs native review
            : "PageDraft Proofreader";

    /// <summary>
    /// Appended to the plain export's file-name stem (before <c>.docx</c>) on an annotated export, so the
    /// annotated file never lands on top of the clean one in the author's downloads folder.
    /// </summary>
    public static string FileNameSuffix(string? language) =>
        LanguageTags.IsHebrew(language)
            ? " - הגהה" // DRAFT he - needs native review
            : " - proofread";

    /// <summary>The label line for a category with an author-facing name; null for every other category.</summary>
    public static string? CategoryLine(string? language, string? category)
    {
        if (!string.Equals(category, KtivMaleCategory, StringComparison.Ordinal)) return null;
        return LanguageTags.IsHebrew(language)
            ? "כתיב מלא לפי האקדמיה" // DRAFT he - needs native review
            : "Academy full spelling";
    }

    /// <summary>
    /// The body of the comment a placed suggestion gets: the category label when it has one, then the
    /// explanation when it says anything. EMPTY means no comment at all: the tracked change already shows
    /// what goes and what comes, so a comment restating it is not written.
    /// </summary>
    public static IReadOnlyList<string> CommentLines(string? language, string? category, string? explanation)
    {
        var lines = new List<string>(2);
        var label = CategoryLine(language, category);
        if (label != null) lines.Add(label);
        var text = SanitizeExplanation(explanation);
        if (text != null) lines.Add(text);
        return lines;
    }

    /// <summary>
    /// An explanation is model output, so it is cleaned before it reaches the author's Word file:
    /// every character an XML part cannot carry is removed first (<see cref="XmlSafeText.Clean"/>: U+FFFE/U+FFFF
    /// and unpaired surrogates as well as C0 controls; be-c06, where one such character 500ed the export);
    /// every whitespace run (line breaks and tabs included) becomes one space and the ends are trimmed;
    /// other control characters (C1 and DEL, legal in XML but invisible noise in a comment) are dropped; the
    /// result is capped at <see cref="MaxExplanationLength"/> characters plus an ellipsis, never splitting a
    /// surrogate pair (after the clean every surrogate left is half of a valid pair).
    /// It is ABSENT (null) when it holds no letter or digit (whitespace-only and punctuation-only both
    /// say nothing) or when it is <see cref="UnifiedAnalysisService.NoExplanationFallback"/>, the
    /// placeholder stored when the model returned nothing. Everything else, bidi marks included, is kept
    /// as written: this is a floor, not a filter on what the model may say.
    /// </summary>
    public static string? SanitizeExplanation(string? explanation)
    {
        var legal = XmlSafeText.Clean(explanation);
        if (string.IsNullOrEmpty(legal)) return null;

        var builder = new StringBuilder(legal.Length);
        var pendingSpace = false;
        foreach (var c in legal)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (char.IsControl(c)) continue;
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
        }

        var text = builder.ToString();
        if (!text.Any(char.IsLetterOrDigit)) return null;
        if (string.Equals(text, UnifiedAnalysisService.NoExplanationFallback, StringComparison.Ordinal)) return null;

        if (text.Length > MaxExplanationLength)
        {
            var cut = MaxExplanationLength;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut].TrimEnd() + "…";
        }
        return text;
    }
}
