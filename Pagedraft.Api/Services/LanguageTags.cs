namespace Pagedraft.Api.Services;

/// <summary>
/// THE ONE place a language string is classified as Hebrew (be-c09). Before it, the API held a dozen copies of
/// <c>language.StartsWith("he", ...)</c> plus local variants that disagreed on null, blank and case, and one
/// export decided Hebrew-ness twice with opposite null rules (the DOCX conversion read a null language as not
/// Hebrew while the proofread comments defaulted it to Hebrew).
///
/// <para><b>The predicate.</b> <see cref="IsHebrew"/> is true when the trimmed value IS <c>he</c> or is a BCP-47
/// tag whose primary subtag is <c>he</c> (<c>he-IL</c>), case-insensitively. A tag that merely begins with the
/// two letters (<c>help</c>, <c>hen</c>, <c>hebrew</c>, <c>he_IL</c>) is NOT Hebrew: a bare <c>StartsWith</c>
/// read a request value such as <c>?language=help</c> as Hebrew (the GuidesCatalog lesson). Null, empty and
/// whitespace are not Hebrew: the predicate never guesses.</para>
///
/// <para><b>Stored versus request values.</b> The same predicate serves both, because the strict form costs a
/// trusted value nothing: every stored <c>Book.Language</c> and <c>AnalysisResult.Language</c> in the dev DB is
/// exactly <c>he</c> or <c>en</c> (measured 2026-10-13: Books 24 he / 3 en, AnalysisResults 289 he / 6 en,
/// PromptTemplates 4 he), and <c>Book.Language</c> is itself written from a request (create and update store
/// the client's value unvalidated), so "stored" does not mean "well-formed" here anyway.</para>
///
/// <para><b>The null rule.</b> A MISSING book language is resolved ONCE, by the caller that owns the default,
/// never inside the predicate: <see cref="Analysis.BaselineLanguageResolver.Normalize"/> maps null/blank to
/// <c>he</c>, the product's default book language (also the entity default, the column default and
/// <c>BooksController.Create</c>'s fallback). An export normalizes the book's language with it before anything
/// reads it, so the conversion, the result filter and the comment strings of one export see the same value;
/// <c>Normalize</c> is idempotent and its Hebrew rung is this predicate, so classifying the normalized value
/// agrees with the key the results were filtered by.</para>
/// </summary>
public static class LanguageTags
{
    /// <summary>The product's default (and Hebrew) language code.</summary>
    public const string Hebrew = "he";

    /// <summary>True when <paramref name="tag"/> is Hebrew: <c>he</c> or <c>he-*</c>, trimmed, any case.</summary>
    public static bool IsHebrew(string? tag) => IsTagFor(tag, Hebrew);

    /// <summary>
    /// True when the trimmed <paramref name="tag"/> is exactly <paramref name="code"/> or a BCP-47 tag whose
    /// primary subtag is <paramref name="code"/> (it continues with the <c>-</c> separator), case-insensitively.
    /// </summary>
    public static bool IsTagFor(string? tag, string code)
    {
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrEmpty(code)) return false;
        var value = tag.AsSpan().Trim();
        if (value.Length < code.Length || !value[..code.Length].Equals(code, StringComparison.OrdinalIgnoreCase))
            return false;
        return value.Length == code.Length || value[code.Length] == '-';
    }
}
