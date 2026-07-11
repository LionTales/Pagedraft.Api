namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// LatinInHebrewContentDetector — the DETERMINISTIC guard that gates the
// analysis-output repair layer's LLM pass (guard-before-LLM).
//
// Plan: src/.cursor/plans/_todo/analysis-output-repair-2026-07-03.plan.md
//       (todo p2-detector). Seeded against the "Phase 0 baseline" real CONTENT-
//       value leaks: "(Action)", "Tension", "Magic vs. Nature", "High Stakes".
//       Generalised to BIDIRECTIONAL span detection (todo d1-bidirectional-detector):
//       it now flags the FOREIGN script relative to an EXPECTED script derived from
//       the book language — Latin runs when Hebrew is expected (the original case),
//       and Hebrew runs when Latin is expected (an English/Latin-script book).
//
// Its only question is "does this repairable prose string still contain residual
// FOREIGN-script text that is NOT legitimate?" — NOT "what should it become" (that
// is the glossary/LLM's job; this detector deliberately does not depend on the
// glossary). A field with no non-allowlisted foreign runs is left untouched at ZERO
// cost — a clean field never reaches the LLM. A field that DOES flag is handed
// downstream (p2-glossary-apply / p3-repair-service) for a fail-safe repair attempt.
//
// RUN DEFINITION (documented, predictable):
//   • A "run" is a maximal sequence of >=2 consecutive letters of ONE script.
//     For the LATIN script that is [A-Za-z]{2,} (ASCII letters only — accented
//     Latin-1 letters such as 'é' are NOT counted, identical to the original regex
//     semantics and to the RepairQualityTests latin-run definition, so the guard
//     and the gold scorer agree on what "Latin" is). For the HEBREW script it is
//     the Hebrew letter block U+05D0..U+05EA (aleph..tav, including the five final
//     forms), >=2 in a row.
//   • A single letter (e.g. an initial "א. B סיפר") is NOT a run and is never
//     flagged — the >=2 rule.
//   • ANY character that is not a letter of the run's script ends the run: space,
//     digit, punctuation, apostrophe, hyphen, and letters of the OTHER script all
//     act as boundaries and never, by themselves, create a foreign run. So (Latin-
//     expected-Hebrew) "face-saving" yields TWO runs ["face", "saving"] and "vs."
//     yields ["vs"]. This is intentionally simple: for a GUARD it is fine that a
//     hyphenated foreign phrase flags as two runs — the field is flagged either way
//     and the glossary/LLM handles the phrase. We do NOT try to keep internal
//     apostrophes/hyphens inside a single run.
//   • Original casing is PRESERVED in returned runs (callers may want to display or
//     log them); results are in order of appearance and NOT de-duplicated (callers
//     can count / dedupe as needed). Each run also carries its exact character
//     OFFSET (Start, in UTF-16 code units) and LENGTH so a downstream classifier /
//     repair engine can locate or splice it precisely.
//
// BIAS: this guard biases toward FLAGGING. A missed allowlist entry only costs one
// extra (fail-safe, no-op-on-doubt) repair attempt downstream, whereas an over-
// broad allowlist would let a real leak through. So the allowlist below is kept
// deliberately tiny and grows only from real, observed false positives — mirror
// the glossary/proofread-gold growth habit (see p6-docs). The allowlist holds Latin
// brand names, so it only ever suppresses Latin runs (the Hebrew-expected
// direction); a Hebrew run is never allowlisted, which is correct — bias to flag.
// ---------------------------------------------------------------------------

/// <summary>
/// Which script the prose is EXPECTED to be written in (derived from the book
/// language). The detector flags the OTHER (foreign) script: <see cref="Hebrew"/>
/// flags Latin runs (a Hebrew book leaking English); <see cref="Latin"/> flags
/// Hebrew runs (a Latin-script book leaking Hebrew).
/// </summary>
public enum ExpectedScript
{
    /// <summary>Hebrew is native; Latin ASCII runs are foreign (the original guard case).</summary>
    Hebrew,

    /// <summary>Latin is native; Hebrew (U+05D0..U+05EA) runs are foreign.</summary>
    Latin,
}

/// <summary>
/// A single maximal foreign-script run found in a prose string: its <see cref="Text"/>
/// (original casing preserved), its <see cref="Start"/> character offset (UTF-16 code
/// unit index into the source string), and its <see cref="Length"/> in characters.
/// Value type with value equality so tests / callers can compare runs directly.
/// </summary>
public readonly record struct ForeignRun(string Text, int Start, int Length);

/// <summary>
/// Deterministic guard that flags residual FOREIGN-script runs in analysis prose,
/// relative to an <see cref="ExpectedScript"/> derived from the book language. See
/// the header comment for the run definition (>=2 consecutive same-script letters,
/// any other character ends a run) and the conservative proper-noun allowlist. This
/// gates the repair LLM pass: clean prose flags nothing and is never sent to the
/// model. The original Latin-only string API (<see cref="DetectLatinRuns"/> /
/// <see cref="HasNonAllowlistedLatin"/>) is preserved as a thin wrapper over the new
/// span API (<see cref="DetectForeignRuns"/> / <see cref="HasForeignRuns"/>) with
/// <see cref="ExpectedScript.Hebrew"/>, so existing callers compile untouched.
/// </summary>
public static class LatinInHebrewContentDetector
{
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

    /// <summary>Latin ASCII letter test ([A-Za-z]); accented Latin-1 letters are
    /// deliberately excluded to match the original regex semantics.</summary>
    private static bool IsLatinLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>Hebrew letter test — the base letter block U+05D0..U+05EA (aleph..tav,
    /// including the five final forms). Niqqud / cantillation / punctuation are NOT
    /// letters and therefore act as run boundaries.</summary>
    private static bool IsHebrewLetter(char c) => c >= 'א' && c <= 'ת';

    private static bool IsForeign(char c, bool foreignIsLatin)
        => foreignIsLatin ? IsLatinLetter(c) : IsHebrewLetter(c);

    /// <summary>
    /// Returns each maximal run of >=2 consecutive letters of the FOREIGN script
    /// (relative to <paramref name="expected"/>) in <paramref name="text"/>, EXCLUDING
    /// any run that (case-insensitively) matches the proper-noun allowlist. Each run
    /// carries its exact <see cref="ForeignRun.Start"/> offset and
    /// <see cref="ForeignRun.Length"/>; original casing is preserved; runs are returned
    /// in order of appearance and are NOT de-duplicated. A null/empty/whitespace input
    /// returns an empty list (never throws). Single deterministic char scan, no regex.
    /// </summary>
    public static IReadOnlyList<ForeignRun> DetectForeignRuns(string? text, ExpectedScript expected)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<ForeignRun>();
        }

        var foreignIsLatin = expected == ExpectedScript.Hebrew;
        List<ForeignRun>? runs = null;
        var length = text.Length;
        var i = 0;
        while (i < length)
        {
            if (!IsForeign(text[i], foreignIsLatin))
            {
                i++;
                continue;
            }

            var start = i;
            do
            {
                i++;
            }
            while (i < length && IsForeign(text[i], foreignIsLatin));

            var runLength = i - start;
            if (runLength < 2)
            {
                continue; // the >=2 rule: a lone foreign letter is never a run
            }

            var runText = text.Substring(start, runLength);
            if (ProperNounAllowlist.Contains(runText))
            {
                continue; // legitimate brand / proper noun — never a leak
            }

            (runs ??= new List<ForeignRun>()).Add(new ForeignRun(runText, start, runLength));
        }

        return (IReadOnlyList<ForeignRun>?)runs ?? Array.Empty<ForeignRun>();
    }

    /// <summary>
    /// Cheap guard-gate: true when <see cref="DetectForeignRuns"/> would return at
    /// least one non-allowlisted foreign-script run for <paramref name="expected"/>.
    /// Allocates only a per-run substring when a candidate run must be allowlist-checked
    /// (none on clean single-script prose), and short-circuits on the first real run.
    /// </summary>
    public static bool HasForeignRuns(string? text, ExpectedScript expected)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var foreignIsLatin = expected == ExpectedScript.Hebrew;
        var length = text.Length;
        var i = 0;
        while (i < length)
        {
            if (!IsForeign(text[i], foreignIsLatin))
            {
                i++;
                continue;
            }

            var start = i;
            do
            {
                i++;
            }
            while (i < length && IsForeign(text[i], foreignIsLatin));

            var runLength = i - start;
            if (runLength < 2)
            {
                continue;
            }

            if (!ProperNounAllowlist.Contains(text.Substring(start, runLength)))
            {
                return true;
            }
        }

        return false;
    }

    // ── Backward-compatible Latin-only string API (Hebrew-expected direction) ──
    // Preserved verbatim in signature/semantics so GlossaryRepairPass,
    // AnalysisRepairService, and the smoke tests compile and behave unchanged.

    /// <summary>
    /// Returns each maximal run of >=2 consecutive Latin ASCII letters in
    /// <paramref name="text"/> (i.e. <see cref="DetectForeignRuns"/> with
    /// <see cref="ExpectedScript.Hebrew"/>), EXCLUDING allowlisted proper nouns.
    /// Original casing is preserved; runs are returned in order of appearance and are
    /// NOT de-duplicated. A null/empty/whitespace input returns an empty list.
    /// Thin wrapper over the span API — projects each <see cref="ForeignRun.Text"/>.
    /// </summary>
    public static IReadOnlyList<string> DetectLatinRuns(string? text)
    {
        var runs = DetectForeignRuns(text, ExpectedScript.Hebrew);
        if (runs.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new string[runs.Count];
        for (var i = 0; i < runs.Count; i++)
        {
            result[i] = runs[i].Text;
        }

        return result;
    }

    /// <summary>
    /// Cheap guard-gate: true when <see cref="DetectLatinRuns"/> would return at
    /// least one non-allowlisted Latin run (i.e. <see cref="HasForeignRuns"/> with
    /// <see cref="ExpectedScript.Hebrew"/>). Use this to decide whether a repairable
    /// field needs the (deterministic glossary / LLM) repair pass at all.
    /// </summary>
    public static bool HasNonAllowlistedLatin(string? text)
        => HasForeignRuns(text, ExpectedScript.Hebrew);
}
