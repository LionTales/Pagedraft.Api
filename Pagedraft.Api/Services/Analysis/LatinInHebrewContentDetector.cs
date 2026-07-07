using System.Text.RegularExpressions;

namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// LatinInHebrewContentDetector — the DETERMINISTIC guard that gates the
// analysis-output repair layer's LLM pass (guard-before-LLM).
//
// Plan: src/.cursor/plans/_todo/analysis-output-repair-2026-07-03.plan.md
//       (todo p2-detector). Seeded against the "Phase 0 baseline" real CONTENT-
//       value leaks: "(Action)", "Tension", "Magic vs. Nature", "High Stakes".
//
// Its only question is "does this repairable prose string still contain residual
// Latin that is NOT legitimate?" — NOT "what should the Latin become" (that is the
// glossary/LLM's job; this detector deliberately does not depend on the glossary).
// A field with no non-allowlisted Latin runs is left untouched at ZERO cost — a
// clean field never reaches the LLM. A field that DOES flag is handed downstream
// (p2-glossary-apply / p3-repair-service) for a fail-safe repair attempt.
//
// RUN DEFINITION (documented, predictable):
//   • A "Latin run" is a maximal sequence of >=2 consecutive Latin ASCII letters,
//     i.e. the regex [A-Za-z]{2,} (identical to the RepairQualityTests latin-run
//     semantics, so the guard and the gold scorer agree on what "Latin" is).
//   • A single Latin letter (e.g. an initial "א. B סיפר") is NOT a run and is
//     never flagged — the >=2 rule.
//   • ANY non-letter ends a run: space, digit, punctuation, apostrophe, hyphen,
//     Hebrew, etc. So "face-saving" yields TWO runs ["face", "saving"] and
//     "vs." yields ["vs"]. This is intentionally simple: for a GUARD it is fine
//     that a hyphenated English phrase flags as two runs — the field is flagged
//     either way and the glossary/LLM handles the phrase. We do NOT try to keep
//     internal apostrophes/hyphens inside a single run.
//   • Original casing is PRESERVED in returned runs (callers may want to display
//     or log them); results are in order of appearance and NOT de-duplicated
//     (callers can count / dedupe as needed).
//
// BIAS: this guard biases toward FLAGGING. A missed allowlist entry only costs one
// extra (fail-safe, no-op-on-doubt) repair attempt downstream, whereas an over-
// broad allowlist would let a real leak through. So the allowlist below is kept
// deliberately tiny and grows only from real, observed false positives — mirror
// the glossary/proofread-gold growth habit (see p6-docs).
// ---------------------------------------------------------------------------

/// <summary>
/// Deterministic guard that flags residual Latin runs in Hebrew analysis prose.
/// See the header comment for the run definition (>=2 consecutive [A-Za-z], any
/// non-letter ends a run) and the conservative proper-noun allowlist. This gates
/// the repair LLM pass: clean prose flags nothing and is never sent to the model.
/// </summary>
public static class LatinInHebrewContentDetector
{
    /// <summary>Matches a maximal run of >=2 consecutive Latin ASCII letters. Any
    /// non-[A-Za-z] character (space, digit, punctuation, hyphen, apostrophe,
    /// Hebrew, …) ends a run. Compiled once; matching preserves original casing.</summary>
    private static readonly Regex LatinRunRegex = new("[A-Za-z]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// CONSERVATIVE proper-noun / brand allowlist of Latin runs that are genuinely
    /// legitimate inside Hebrew analysis prose and must NEVER be treated as leaks.
    /// Case-insensitive (<see cref="StringComparer.OrdinalIgnoreCase"/>).
    ///
    /// Seeded with a couple of near-universally-Latin brand names that Hebrew
    /// writers routinely leave untransliterated (so a manuscript referencing them
    /// would legitimately surface them in analysis prose). Each entry is a SINGLE
    /// Latin run (matching the run definition above); a multi-word proper noun such
    /// as "Magic vs. Nature" is three separate runs and would still flag — that is
    /// intended (bias toward flagging). This list grows ONLY from real, observed
    /// false positives; when in doubt, leave a term OUT and let the fail-safe
    /// repair pass handle it. Mirror the glossary growth habit (p6-docs).
    /// </summary>
    private static readonly HashSet<string> ProperNounAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Google",
        "Facebook",
    };

    /// <summary>
    /// Returns each maximal run of >=2 consecutive Latin ASCII letters in
    /// <paramref name="text"/>, EXCLUDING any run that (case-insensitively) matches
    /// the proper-noun allowlist. Original casing is preserved; runs are returned in
    /// order of appearance and are NOT de-duplicated. A null/empty/whitespace input
    /// returns an empty list (never throws).
    /// </summary>
    public static IReadOnlyList<string> DetectLatinRuns(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var runs = new List<string>();
        foreach (Match match in LatinRunRegex.Matches(text))
        {
            if (!ProperNounAllowlist.Contains(match.Value))
            {
                runs.Add(match.Value);
            }
        }

        return runs;
    }

    /// <summary>
    /// Cheap guard-gate: true when <see cref="DetectLatinRuns"/> would return at
    /// least one non-allowlisted Latin run. Use this to decide whether a repairable
    /// field needs the (deterministic glossary / LLM) repair pass at all.
    /// </summary>
    public static bool HasNonAllowlistedLatin(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (Match match in LatinRunRegex.Matches(text))
        {
            if (!ProperNounAllowlist.Contains(match.Value))
            {
                return true;
            }
        }

        return false;
    }
}
