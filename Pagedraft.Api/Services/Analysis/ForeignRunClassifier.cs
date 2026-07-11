namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// ForeignRunClassifier — the DETERMINISTIC skip-gate between the bidirectional
// detector (d1) and the span-scoped repair model (d3).
//
// Plan: src/.cursor/plans/_todo/dynamic-term-repair-design-2026-07-10.plan.md
//       (todo d2-skip-classifier).
//
//   [d1] LatinInHebrewContentDetector.DetectForeignRuns  -> foreign-script runs
//         |                                                  (text + Start + Length)
//   [d2] ForeignRunClassifier.Classify                   -> per run: REPAIR | LEAVE
//         |                                                  (this file)
//   [d3] DynamicTermRepairService                        -> one span-scoped IAiRouter
//                                                            call per REPAIR run
//
// It answers ONE question per run: "is this foreign token a genuine vocabulary
// LEAK the model should translate (REPAIR), or a legitimately-foreign token that
// must be left byte-identical (LEAVE)?" It is a CHEAP, allocation-light, no-I/O,
// no-model heuristic gate whose only job is to keep obvious non-leaks (proper
// nouns, acronyms, URLs/emails/code, numbers+units) away from the d3 model — the
// d3 model is the SEMANTIC backstop (its prompt returns the token unchanged when
// it is a proper noun / has no equivalent), so a false REPAIR here costs at most
// one extra model call that the model then no-ops, whereas a false LEAVE lets a
// leak through untouched.
//
// BIAS (mirrors the d1 detector's bias-to-flag): err toward REPAIR for AMBIGUOUS
// plain lowercase words (the leak shape — "confusion", "claustrophobia"), and
// toward LEAVE only for the CLEAR proper-noun / acronym / url / unit signals.
//
// SCRIPT AWARENESS: case-based signals (Title-Case, ALL-CAPS) apply ONLY to Latin
// runs (Hebrew-expected book); the Hebrew block has no letter case, so a foreign
// Hebrew run (Latin-expected book) can only be spared by the book-entity list or
// by a URL/number border — otherwise it REPAIRs, which is correct (bias to flag)
// and the d3 model handles a Hebrew proper noun.
//
// UPSTREAM CONTRACT: it only ever sees runs sourced from PROSE values (the
// RepairableFields whitelist already excludes JSON keys, enums, numeric metrics,
// and quoted-source / offset anchors). It does NOT re-derive that whitelist and
// must not be handed anchor/quote runs.
// ---------------------------------------------------------------------------

/// <summary>Per-run verdict from <see cref="ForeignRunClassifier"/>: whether a
/// foreign-script run should be handed to the d3 repair model (<see cref="Repair"/>)
/// or left byte-identical (<see cref="Leave"/>).</summary>
public enum ForeignRunDecision
{
    /// <summary>A genuine foreign-vocabulary leak — send the run to the d3 span-scoped repair model.</summary>
    Repair,

    /// <summary>Legitimately foreign (proper noun / acronym / URL / email / code / number+unit) — never touch it.</summary>
    Leave,
}

/// <summary>A <see cref="ForeignRun"/> paired with its <see cref="ForeignRunDecision"/>.
/// Value type with value equality so tests / callers can compare directly.</summary>
public readonly record struct ClassifiedForeignRun(ForeignRun Run, ForeignRunDecision Decision);

/// <summary>
/// Deterministic REPAIR|LEAVE gate over the foreign-script runs produced by
/// <see cref="LatinInHebrewContentDetector.DetectForeignRuns"/>. Pure and static:
/// no state, no I/O, no model call. See the header comment for the bias and the
/// script-awareness contract. Heuristics that route a run to LEAVE (first match wins):
/// <list type="number">
///   <item>a single-letter run (defensive; the detector already requires &gt;=2);</item>
///   <item>a known character/place name when a <c>bookEntities</c> set is supplied (case-insensitive);</item>
///   <item>part of a URL / email / path / code identifier (bordering '@' '/' '\' '_' , a dotted host <c>word.word</c>, or a following <c>://</c> scheme);</item>
///   <item>a number+unit token (bordered by a digit, or a unit token adjacent to a digit across one space);</item>
///   <item>an ALL-CAPS acronym (Latin only, e.g. NASA / FBI);</item>
///   <item>a Title-Case, MID-sentence Latin word (a proper noun — sentence-initial capitalization does NOT count).</item>
/// </list>
/// Everything else — the plain lowercase foreign word in prose — is <see cref="ForeignRunDecision.Repair"/>.
/// </summary>
public static class ForeignRunClassifier
{
    /// <summary>
    /// Short unit-like tokens that mark a number+unit when they sit next to a digit
    /// (either directly, "5kg", or across one space, "5 kg"). Case-insensitive. A unit
    /// only spares a run when a digit is adjacent, so an English word that happens to be
    /// on this list ("in", "min") still REPAIRs in plain prose.
    /// </summary>
    private static readonly HashSet<string> UnitTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "kg", "km", "cm", "mm", "nm", "um", "dm", "mg", "ng",
        "kb", "mb", "gb", "tb", "pb", "kib", "mib", "gib",
        "px", "dpi", "ppi", "hz", "khz", "mhz", "ghz",
        "ml", "cl", "dl", "oz", "lb", "lbs", "ft", "yd", "mi", "in",
        "hr", "hrs", "min", "sec", "ms", "us", "ns",
        "kw", "mw", "kwh", "wh", "rpm", "bpm", "fps", "mph", "kmh",
        "am", "pm",
    };

    /// <summary>
    /// Classifies a single foreign-script <paramref name="run"/> in the context of its full
    /// prose value. <paramref name="fullValue"/> is the WHOLE value the run was detected in, so the
    /// classifier can inspect the characters around <see cref="ForeignRun.Start"/> /
    /// <see cref="ForeignRun.Length"/> (Title-Case is only proper-noun-ish MID-sentence; a unit needs an
    /// adjacent digit; a URL/email needs bordering punctuation). <paramref name="expected"/> is the book's
    /// native script (a Latin run when Hebrew is expected; a Hebrew run when Latin is expected); it gates
    /// the case-based heuristics to Latin runs. <paramref name="bookEntities"/> is an OPTIONAL set of known
    /// character/place names — a case-insensitive member is always LEAVE; when null the entity check is
    /// simply skipped. Never throws; a run whose offsets fall outside <paramref name="fullValue"/> falls
    /// back to text-only heuristics.
    /// </summary>
    public static ForeignRunDecision Classify(
        ForeignRun run,
        string fullValue,
        ExpectedScript expected,
        IReadOnlySet<string>? bookEntities = null)
    {
        var text = run.Text;

        // (1) Single-letter guard. The detector already enforces >=2, but never let a lone
        // letter reach the model — a single foreign letter is an initial, not a leak.
        if (string.IsNullOrEmpty(text) || text.Length < 2)
        {
            return ForeignRunDecision.Leave;
        }

        // (2) Known character / place name (optional, case-insensitive). A book entity is
        // legitimately foreign regardless of script — this is the one lever that can spare a
        // foreign HEBREW run (which has no case signal) in a Latin-script book.
        if (bookEntities is not null && ContainsIgnoreCase(bookEntities, text))
        {
            return ForeignRunDecision.Leave;
        }

        // Context-bearing heuristics need the run to sit inside fullValue. If the offsets don't
        // line up (defensive), fall through to the text-only case heuristics below.
        var hasContext = fullValue is not null
            && run.Start >= 0
            && run.Start + run.Length <= fullValue.Length;

        if (hasContext)
        {
            // (3) URL / email / path / code identifier — a token wired into machine text.
            if (IsUrlEmailOrCode(run, fullValue!))
            {
                return ForeignRunDecision.Leave;
            }

            // (4) Number + unit — a measurement token, never translated.
            if (IsNumberOrUnit(run, fullValue!))
            {
                return ForeignRunDecision.Leave;
            }
        }

        // Case-based signals only mean anything for Latin runs (Hebrew has no letter case).
        if (expected == ExpectedScript.Hebrew && IsAllLatin(text))
        {
            // (5) ALL-CAPS acronym (NASA, FBI, UK).
            if (IsAllCaps(text))
            {
                return ForeignRunDecision.Leave;
            }

            // (6) Title-Case MID-sentence => proper noun. Sentence-initial capitalization is
            // just orthography and does NOT signal a name, so a Title-Case word that opens a
            // sentence (or the value) still REPAIRs — it is likely a leaked common noun the model
            // capitalized. This deliberately LEAVES Title-Case mid-sentence tokens; a Title-Case
            // literary term ("Tension") is therefore left for the d3 model to NOT reach, which is
            // the accepted precision/recall trade of a cheap gate (see header BIAS note).
            if (IsTitleCase(text) && hasContext && !IsSentenceInitial(fullValue!, run.Start))
            {
                return ForeignRunDecision.Leave;
            }
        }

        // Everything else: a plain foreign word in prose — the leak case. Send it to d3.
        return ForeignRunDecision.Repair;
    }

    /// <summary>
    /// Convenience: classifies a whole ordered list of runs against the same value / expected
    /// script / entity set, preserving order. Returns each run paired with its decision.
    /// </summary>
    public static IReadOnlyList<ClassifiedForeignRun> Classify(
        IReadOnlyList<ForeignRun> runs,
        string fullValue,
        ExpectedScript expected,
        IReadOnlySet<string>? bookEntities = null)
    {
        if (runs is null || runs.Count == 0)
        {
            return Array.Empty<ClassifiedForeignRun>();
        }

        var result = new ClassifiedForeignRun[runs.Count];
        for (var i = 0; i < runs.Count; i++)
        {
            result[i] = new ClassifiedForeignRun(runs[i], Classify(runs[i], fullValue, expected, bookEntities));
        }

        return result;
    }

    /// <summary>
    /// Convenience for d3: the subset of <paramref name="runs"/> classified
    /// <see cref="ForeignRunDecision.Repair"/>, in order. An empty result means every run was
    /// legitimately foreign and no model call is needed for this value.
    /// </summary>
    public static IReadOnlyList<ForeignRun> RunsToRepair(
        IReadOnlyList<ForeignRun> runs,
        string fullValue,
        ExpectedScript expected,
        IReadOnlySet<string>? bookEntities = null)
    {
        if (runs is null || runs.Count == 0)
        {
            return Array.Empty<ForeignRun>();
        }

        List<ForeignRun>? repair = null;
        foreach (var run in runs)
        {
            if (Classify(run, fullValue, expected, bookEntities) == ForeignRunDecision.Repair)
            {
                (repair ??= new List<ForeignRun>()).Add(run);
            }
        }

        return (IReadOnlyList<ForeignRun>?)repair ?? Array.Empty<ForeignRun>();
    }

    // ── Heuristics ──────────────────────────────────────────────────────────

    /// <summary>
    /// True when the run is part of a URL, email address, file path, or code identifier —
    /// detected purely from the characters bordering the run in <paramref name="fullValue"/>:
    /// an adjacent '@' (email), '/'/'\' (path/URL), '_' (snake_case identifier), a following
    /// "://" scheme, or a dotted host / dotted member (<c>word.word</c>). Conservative by
    /// design: a lone hyphen is NOT treated as a code border (hyphenation is ordinary prose),
    /// and a trailing sentence period (no letter after it) is not a dotted host. FORWARD dot
    /// check only: the char right after the dot must be LOWERCASE or a digit (never uppercase)
    /// — a real host/member segment is lowercase- or digit-led ("example.com", "file2.txt"),
    /// while an UPPERCASE letter right after a dot with no space is a missing-space sentence
    /// boundary typo ("confusion.Then"), not a dotted host; the classifier's bias favors REPAIR
    /// there so the d3 model (which no-ops on a genuine term) can arbitrate.
    /// </summary>
    private static bool IsUrlEmailOrCode(ForeignRun run, string fullValue)
    {
        var beforeIdx = run.Start - 1;
        var afterIdx = run.Start + run.Length;
        var b = beforeIdx >= 0 ? fullValue[beforeIdx] : '\0';
        var a = afterIdx < fullValue.Length ? fullValue[afterIdx] : '\0';

        // Email / path / snake_case borders.
        if (b == '@' || a == '@') return true;
        if (b == '/' || a == '/') return true;
        if (b == '\\' || a == '\\') return true;
        if (b == '_' || a == '_') return true;

        // Scheme right after the run: the "https"/"http" of "https://host".
        if (StartsWithAt(fullValue, afterIdx, "://")) return true;

        // Dotted host / dotted member: word.word. FORWARD (run.dot): the char right after the
        // dot must be lowercase or a digit, NOT uppercase — "example.com" / "file2.txt" are
        // lowercase/digit-led, but ".Then" (no space) is a missing-space sentence boundary, not
        // a host, so it must fall through and REPAIR. BACKWARD (dot.run) is unchanged: any host
        // char before the dot still counts (the run itself carries the uppercase-vs-lowercase
        // signal for that side via the Title-Case / ALL-CAPS heuristics below).
        if (a == '.' && afterIdx + 1 < fullValue.Length
            && (IsLatinLower(fullValue[afterIdx + 1]) || IsAsciiDigit(fullValue[afterIdx + 1]))) return true;
        if (b == '.' && beforeIdx - 1 >= 0 && IsHostChar(fullValue[beforeIdx - 1])) return true;

        return false;
    }

    /// <summary>
    /// True when the run is a measurement token: it is directly bordered by a digit ("5km",
    /// "km5", "100px"), or it is a known short unit token sitting one space away from a digit
    /// ("5 kg"). The unit list only fires next to a digit, so an ordinary English word that
    /// happens to be a unit ("in", "min") still REPAIRs in plain prose.
    /// </summary>
    private static bool IsNumberOrUnit(ForeignRun run, string fullValue)
    {
        var beforeIdx = run.Start - 1;
        var afterIdx = run.Start + run.Length;
        var b = beforeIdx >= 0 ? fullValue[beforeIdx] : '\0';
        var a = afterIdx < fullValue.Length ? fullValue[afterIdx] : '\0';

        // Digit directly touching the run.
        if (IsAsciiDigit(b) || IsAsciiDigit(a)) return true;

        // "<digit> <unit>" or "<unit> <digit>" across a single space.
        if (UnitTokens.Contains(run.Text))
        {
            if (b == ' ' && beforeIdx - 1 >= 0 && IsAsciiDigit(fullValue[beforeIdx - 1])) return true;
            if (a == ' ' && afterIdx + 1 < fullValue.Length && IsAsciiDigit(fullValue[afterIdx + 1])) return true;
        }

        return false;
    }

    /// <summary>
    /// True when the run OPENS a sentence in <paramref name="fullValue"/> — i.e. scanning left of
    /// <paramref name="start"/> and skipping whitespace and transparent opening punctuation
    /// (quotes/brackets/parentheses), the first meaningful character is a sentence terminator
    /// ('.', '!', '?', '…', or a line break) or the string start. Used to DISQUALIFY sentence-initial
    /// capitalization from the proper-noun signal.
    /// </summary>
    private static bool IsSentenceInitial(string fullValue, int start)
    {
        var i = start - 1;
        while (i >= 0)
        {
            var c = fullValue[i];
            if (char.IsWhiteSpace(c) || IsTransparentOpen(c))
            {
                i--;
                continue;
            }

            return IsSentenceTerminator(c);
        }

        return true; // reached the start of the value
    }

    /// <summary>Title-Case = a leading Latin uppercase letter followed by all Latin lowercase
    /// (e.g. "Sarah", "Jerusalem"). Requires the ASCII-Latin run shape the detector produces.</summary>
    private static bool IsTitleCase(string text)
    {
        if (text.Length < 2 || !IsLatinUpper(text[0]))
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!IsLatinLower(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>ALL-CAPS = every character is a Latin uppercase letter (length &gt;=2), e.g. "NASA".</summary>
    private static bool IsAllCaps(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!IsLatinUpper(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllLatin(string text)
    {
        foreach (var c in text)
        {
            if (!IsLatinUpper(c) && !IsLatinLower(c))
            {
                return false;
            }
        }

        return text.Length > 0;
    }

    private static bool ContainsIgnoreCase(IReadOnlySet<string> set, string value)
    {
        // Fast path: honours the set's own comparer (callers SHOULD pass an OrdinalIgnoreCase set).
        if (set.Contains(value))
        {
            return true;
        }

        // If the set's own comparer is already case-insensitive, Contains above was authoritative
        // and a miss is a real miss: skip the redundant linear scan. Only a set with a case-sensitive
        // (e.g. ordinal) comparer still needs the explicit case-insensitive fallback scan below.
        if (set is HashSet<string> hashSet &&
            (ReferenceEquals(hashSet.Comparer, StringComparer.OrdinalIgnoreCase) ||
             ReferenceEquals(hashSet.Comparer, StringComparer.InvariantCultureIgnoreCase) ||
             ReferenceEquals(hashSet.Comparer, StringComparer.CurrentCultureIgnoreCase)))
        {
            return false;
        }

        // Fall back to an explicit case-insensitive scan so membership is case-insensitive even
        // when the caller supplied a case-sensitive (e.g. ordinal) set. Entity lists are small;
        // runs per value are few.
        foreach (var entity in set)
        {
            if (string.Equals(entity, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithAt(string s, int index, string token)
    {
        if (index < 0 || index + token.Length > s.Length)
        {
            return false;
        }

        for (var i = 0; i < token.Length; i++)
        {
            if (s[index + i] != token[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLatinUpper(char c) => c >= 'A' && c <= 'Z';
    private static bool IsLatinLower(char c) => c >= 'a' && c <= 'z';
    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    /// <summary>A dotted-host neighbour char: Latin letter or digit (a segment of example.com / file2.txt).</summary>
    private static bool IsHostChar(char c) => IsLatinUpper(c) || IsLatinLower(c) || IsAsciiDigit(c);

    /// <summary>Opening punctuation transparent to sentence-initial detection (quotes / brackets).</summary>
    private static bool IsTransparentOpen(char c)
        => c is '"' or '\'' or '(' or '[' or '{'
            or '«' /* « */ or '‹' /* ‹ */
            or '“' /* “ */ or '‘' /* ‘ */
            or '¿' /* ¿ */ or '¡' /* ¡ */;

    /// <summary>Sentence-ending characters that make a following capital sentence-initial.</summary>
    private static bool IsSentenceTerminator(char c)
        => c is '.' or '!' or '?' or '…' /* … */ or '\n' or '\r';
}
