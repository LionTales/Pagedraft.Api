using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;

namespace Pagedraft.Api.Services.Analysis.Hebrew;

/// <summary>
/// Deterministic Hebrew ktiv-male (כתיב מלא, full-spelling) copyedit check.
///
/// DATA-SOURCE DECISION (recorded here per the implementation plan): this is a DETERMINISTIC
/// dictionary/rule lookup, NOT an LLM call. No licensable machine-readable Academy/Hspell/Dicta
/// ktiv-male resource was found embeddable in this repo, and prior bake-off evidence showed the
/// Hebrew LLM (Dicta-LM 3.0) performs poorly on schema'd Hebrew sub-tasks (composite 0.029 vs
/// gemma4:12b 0.900), so an LLM ktiv-male sub-check is the wrong tool. Instead we embed a curated,
/// extensible seed list of the Academy's closed-list haser→male pairs (see
/// <see cref="KtivMaleWordList"/>) and apply the simple "add vav for /o/, add yod for /i,e/"
/// transformation only where it is decidable via that vetted lookup. Rule source: the Academy of
/// the Hebrew Language's 2017 ktiv-male rules (hebrew-academy.org.il).
///
/// CONTRACT:
///  - SUGGESTIONS ONLY. PageDraft is human-in-the-loop; this never auto-fixes. It returns
///    <see cref="AnalysisSuggestion"/> items shaped exactly like proofread suggestions, anchored
///    by offset in the NORMALIZED text (same convention as SuggestionDiffService), so the existing
///    apply/highlight path works unchanged.
///  - CONSERVATIVE. Only whole-word matches against the closed seed list (optionally behind a
///    single common Hebrew prefix letter) are flagged. Anything not on the list is left alone, so
///    intentional/colloquial spellings the proofread "preserve intentional dialogue" rule protects
///    are never touched.
///  - GATED. Runs only for Hebrew text AND only when the house-style toggle
///    (Ai:HebrewStyle:EnforceKtivMale) is on. English is never affected.
/// </summary>
public class KtivMaleChecker
{
    private readonly HebrewStyleOptions _options;

    /// <summary>Common Hebrew single-letter prefixes that may precede a base word (ו, ה, ב, כ, ל, מ, ש).</summary>
    private static readonly char[] PrefixLetters = { 'ו', 'ה', 'ב', 'כ', 'ל', 'מ', 'ש' };

    public KtivMaleChecker(IOptions<HebrewStyleOptions> options)
    {
        _options = options?.Value ?? new HebrewStyleOptions();
    }

    /// <summary>Test-friendly constructor: pass options directly without IOptions wrapping.</summary>
    public KtivMaleChecker(HebrewStyleOptions options)
    {
        _options = options ?? new HebrewStyleOptions();
    }

    /// <summary>
    /// Scan <paramref name="originalText"/> for defective (haser) spellings on the closed ktiv-male
    /// seed list and return one suggestion per occurrence with the normative full (male) form.
    ///
    /// Returns an empty list when the toggle is off, the language is not Hebrew, or nothing matches.
    /// Offsets are positions in the NORMALIZED original text (TextNormalization.NormalizeTextForAnalysis),
    /// matching SuggestionDiffService so the apply/highlight path is consistent.
    /// </summary>
    public List<AnalysisSuggestion> FindSuggestions(string originalText, string language)
    {
        var result = new List<AnalysisSuggestion>();

        if (!_options.EnforceKtivMale)
            return result;
        if (string.IsNullOrWhiteSpace(originalText))
            return result;
        if (language == null || !language.StartsWith("he", StringComparison.OrdinalIgnoreCase))
            return result;

        var text = TextNormalization.NormalizeTextForAnalysis(originalText);

        var i = 0;
        while (i < text.Length)
        {
            // Skip to the start of the next word.
            if (!IsHebrewLetter(text[i]))
            {
                i++;
                continue;
            }

            // Find the word boundary [wordStart, wordEnd).
            var wordStart = i;
            while (i < text.Length && IsHebrewLetter(text[i]))
                i++;
            var wordEnd = i;

            var word = text[wordStart..wordEnd];
            if (TryGetMale(word, out var male))
            {
                result.Add(new AnalysisSuggestion
                {
                    StartOffset = wordStart,
                    EndOffset = wordEnd,
                    OriginalText = word,
                    SuggestedText = male,
                    Reason = "Ktiv male (כתיב מלא): normative full spelling per the Academy of the Hebrew Language.",
                    Category = "ktiv-male",
                    ContextBefore = text[Math.Max(0, wordStart - 50)..wordStart],
                    ContextAfter = text[wordEnd..Math.Min(text.Length, wordEnd + 50)]
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Resolve the normative male spelling for <paramref name="word"/> if it is a defective form on
    /// the seed list, either as a bare word or behind a single common Hebrew prefix letter. Returns
    /// false when the word is not on the list or is already in its male form (so already-correct and
    /// off-list words are never flagged).
    /// </summary>
    private static bool TryGetMale(string word, out string male)
    {
        male = string.Empty;

        // 1. Exact whole-word match.
        if (KtivMaleWordList.HaserToMale.TryGetValue(word, out var directMale))
        {
            if (string.Equals(word, directMale, StringComparison.Ordinal))
                return false; // already-male sentinel; do not flag
            male = directMale;
            return true;
        }

        // 2. Single common prefix letter + base word (e.g. ל + עתים → לעיתים, ה + תכנית → התוכנית).
        if (word.Length >= 2 && Array.IndexOf(PrefixLetters, word[0]) >= 0)
        {
            var baseWord = word[1..];
            if (KtivMaleWordList.HaserToMale.TryGetValue(baseWord, out var baseMale)
                && !string.Equals(baseWord, baseMale, StringComparison.Ordinal))
            {
                male = word[0] + baseMale;
                return true;
            }
        }

        return false;
    }

    /// <summary>True for Hebrew block letters (U+05D0..U+05EA). Niqqud/punctuation are not letters here.</summary>
    private static bool IsHebrewLetter(char c) => c >= 'א' && c <= 'ת';
}
