using System;
using System.Collections.Generic;
using static Pagedraft.Api.Services.Analysis.ScriptTokenPredicates;

namespace Pagedraft.Api.Services.Analysis.Hebrew;

/// <summary>
/// A NARROW ORTHOGRAPHIC-IMPOSSIBILITY SAFETY NET for proofread suggestions. Zero lexicon, zero model
/// calls, one linear scan of the suggestion text.
///
/// WHAT IT IS, STATED BEFORE WHAT IT DOES, because the honest scope is the easiest thing to lose:
/// this rejects ONE mechanically impossible SHAPE - a Hebrew final-form letter (ך ם ן ף ץ) sitting in a
/// non-final position of an orthographic word. It is NOT a Hebrew spell checker, NOT a non-word
/// detector, and specifically NOT a fix for the "the model proposes character-level non-words" class
/// that this file was written alongside.
///
/// ITS MEASURED REACH, so no future reader has to guess. On the 2026-08-05 real-prose corpus
/// (128 suggestions across two prompt arms and twelve twice-proofread manuscript passages) this rule
/// fires on exactly ONE suggestion: <c>צמצם -> צמץם</c>, which belongs to the ARM A column, not the
/// shipped-default column. It reaches ZERO of the ten non-words the shipped default produced. Those ten
/// are recorded, with that number computed rather than asserted, in
/// <c>Pagedraft.Api.Tests.RealProseNonWordResidue</c>. A justification of this guard that says "it
/// catches most of the non-words" would be resting on a premise that data refutes.
///
/// WHY IT SHIPS ANYWAY: it costs nothing, and the one thing it does catch is an edit no human editor
/// could ever want. A suggestion of this shape is wrong in every register, every dialect and every
/// house style, so offering it to a human editor is pure noise.
///
/// WHAT IT MAY DROP, AND THE BOUND THAT MAKES THAT SAFE. The drop predicate is
/// <see cref="WouldDrop"/>, and it requires BOTH halves:
///  (a) the ORIGINAL span is orthographically clean (no impossible word anywhere in it), and
///  (b) the SUGGESTED span is not.
/// So the guard only ever removes a suggestion that INTRODUCES an impossible word into clean text. It
/// cannot drop a legal correction, because a replacement containing a mechanically impossible word is
/// not a legal correction; and it cannot suppress a repair OF an already-impossible word, because half
/// (a) fails the moment the original carries one - which is what keeps a genuinely odd manuscript
/// spelling (a Masoretic anomaly, a stylised transliteration) repairable rather than frozen.
/// Deliberately NOT a set difference between the two sides: half (a) is strictly more conservative and
/// is one sentence to state.
///
/// WHY IT IS NOT VALIDATED BY A COUNT. The standing lesson from the glossary-repair work is that a
/// fail-safe validating by a foreign-run COUNT misses a same-script whole-value echo. This guard
/// therefore bounds the SHAPE of what it may drop (one impossible word, introduced into a clean span)
/// and never compares counts of anything.
///
/// SELF-GATING BY SCRIPT, so it takes no language parameter. A word here is a maximal run of Hebrew
/// base letters, so a Latin or mixed-script suggestion has no words at all as far as this guard is
/// concerned and can never trip it. That removes a whole class of "the caller forgot to pass the
/// language" bug - notably on the measurement harnesses, which call the diff service directly.
/// </summary>
internal static class HebrewOrthographyShapeGuard
{
    /// <summary>
    /// The five Hebrew final (sofit) forms. Each is legal ONLY as the last letter of an orthographic
    /// word; anywhere else it is mechanically impossible with no lexicon at all.
    /// </summary>
    internal const string FinalForms = "ךםןףץ";

    /// <summary>
    /// Every distinct orthographic word of <paramref name="text"/> that carries a final form in a
    /// non-final position, in first-appearance order. Empty for null/empty text, for text with no
    /// Hebrew letters, and for well-formed Hebrew.
    ///
    /// WORD = A MAXIMAL RUN OF HEBREW BASE LETTERS (U+05D0..U+05EA, the same predicate the foreign-run
    /// pipeline uses). EVERYTHING else terminates a word: whitespace, ASCII and Hebrew punctuation, the
    /// maqaf ־, geresh ׳ and gershayim ״, niqqud and cantillation marks, digits and Latin letters. That
    /// choice is what keeps the rule free of false positives on the shapes Hebrew actually writes:
    ///  - a maqaf compound (אם־כן) is two words, so its first word's final mem is word-final and legal;
    ///  - an acronym (תנ"ך, בע"מ) splits at the gershayim, so its final letter is word-final and legal;
    ///  - vocalised text splits at every niqqud mark, which can only SHRINK a run.
    /// Splitting a run can only ever move a letter TOWARDS the end of its word, i.e. it can only remove
    /// violations, never create one - so every boundary rule here is conservative by construction.
    /// </summary>
    internal static IReadOnlyList<string> ImpossibleWords(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        List<string>? offenders = null;

        var i = 0;
        while (i < text.Length)
        {
            if (!IsHebrewLetter(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && IsHebrewLetter(text[i]))
                i++;

            // The LAST letter is allowed to be a final form; every earlier one is not. A one-letter
            // word therefore never offends, which is what makes the acronym/maqaf splits above safe.
            for (var j = start; j < i - 1; j++)
            {
                if (FinalForms.IndexOf(text[j]) < 0)
                    continue;

                var word = text[start..i];
                offenders ??= new List<string>();
                if (!offenders.Contains(word))
                    offenders.Add(word);
                break;
            }
        }

        return (IReadOnlyList<string>?)offenders ?? Array.Empty<string>();
    }

    /// <summary>True when <paramref name="text"/> contains at least one mechanically impossible word.</summary>
    internal static bool IsImpossible(string? text) => ImpossibleWords(text).Count > 0;

    /// <summary>
    /// THE DROP PREDICATE. True when a proofread suggestion replacing <paramref name="originalText"/>
    /// with <paramref name="suggestedText"/> must be withheld: the original span is orthographically
    /// clean and the replacement introduces a mechanically impossible word. See the type remarks for
    /// why both halves are required.
    /// </summary>
    /// <param name="offendingWord">
    /// The first impossible word the replacement introduces, for the log line and the diagnostics
    /// count. Empty when the predicate is false.
    /// </param>
    internal static bool WouldDrop(string? originalText, string? suggestedText, out string offendingWord)
    {
        offendingWord = string.Empty;

        var introduced = ImpossibleWords(suggestedText);
        if (introduced.Count == 0)
            return false;

        // Half (a): an original that is ALREADY impossible keeps its suggestion. Repairing an odd
        // spelling is exactly the case this half exists to protect.
        if (IsImpossible(originalText))
            return false;

        offendingWord = introduced[0];
        return true;
    }
}
