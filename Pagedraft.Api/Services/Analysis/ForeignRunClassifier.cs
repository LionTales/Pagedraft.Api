using static Pagedraft.Api.Services.Analysis.ScriptTokenPredicates;

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
///   <item>a known character/place name when a <c>bookEntities</c> set is supplied (a plain set matches case-insensitively; a <see cref="BookEntitySet"/> applies its own two-tier match — declared names case-insensitive, manuscript-harvested tokens case-SENSITIVE, see be-c04);</item>
///   <item>part of a URL / email / path / code identifier (bordering '@' '/' '\' '_' , a dotted host <c>word.word</c>, or a following <c>://</c> scheme);</item>
///   <item>a number+unit token (bordered by a digit, or a unit token adjacent to a digit across one space);</item>
///   <item>inside a QUOTED, MULTI-word foreign span — an OPENING quote to the left and its MATCHING CLOSING quote to the right (<c>"…"</c>, <c>'…'</c>, <c>«…»</c>, <c>“…”</c>, <c>‘…’</c>, <c>״…״</c>), with another word inside — a do-not-translate citation such as "carpe diem". A LONE scare-quoted word does NOT qualify, and a MISMATCHED pair (e.g. an opening <c>"</c> "closed" by a possessive apostrophe or a Hebrew abbreviation geresh) does not either (be-c05);</item>
///   <item>an ALL-CAPS acronym (Latin only, e.g. NASA / FBI);</item>
///   <item>a Title-Case, MID-sentence Latin word (a proper noun — sentence-initial capitalization does NOT count);</item>
///   <item>a NAME-PARTICLE: an all-lowercase Latin run that lies WITHIN a Title-Case Latin name span — scanning outward across space-separated Latin tokens there is a Title-Case Latin token on BOTH sides with only all-lowercase Latin tokens in between (the "van" of "Vincent van Gogh", but also the "van der" of "Mies van der Rohe" and the "of the" of "The Lord of the Rings") — a name connective, not a leak.</item>
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
    /// character/place names — a member is always LEAVE; when null the entity check is simply skipped. A plain
    /// set is matched case-INSENSITIVELY; a <see cref="BookEntitySet"/> supplies its own two-tier match
    /// (declared names case-insensitive, manuscript-harvested tokens case-SENSITIVE — be-c04) and that match is
    /// authoritative. Never throws; a run whose offsets fall outside <paramref name="fullValue"/> falls
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

            // (5) Inside a QUOTED, MULTI-word foreign span — a do-not-translate citation such as
            // "carpe diem". Context-derived from the quote characters bordering the span (an OPENING
            // quote on the LEFT and its MATCHING CLOSING quote on the RIGHT, another word inside),
            // NOT a word list, and script-agnostic. A LONE scare-quoted word does NOT qualify (the
            // span must be multi-word), so a single quoted leak still REPAIRs; and the bounds must
            // PAIR (be-c05), so neither a possessive apostrophe nor a Hebrew abbreviation geresh can
            // masquerade as the closing bound of a span opened by a real quote.
            if (IsInsideQuotedForeignSpan(fullValue!, run.Start, run.Length))
            {
                return ForeignRunDecision.Leave;
            }
        }

        // Case-based signals only mean anything for Latin runs (Hebrew has no letter case).
        if (expected == ExpectedScript.Hebrew && IsAllLatin(text))
        {
            // (6) ALL-CAPS acronym (NASA, FBI, UK).
            if (IsAllCaps(text))
            {
                return ForeignRunDecision.Leave;
            }

            // (7) Title-Case MID-sentence => proper noun. Sentence-initial capitalization is
            // just orthography and does NOT signal a name, so a Title-Case word that opens a
            // sentence (or the value) still REPAIRs — it is likely a leaked common noun the model
            // capitalized. This deliberately LEAVES Title-Case mid-sentence tokens; a Title-Case
            // literary term ("Tension") is therefore left for the d3 model to NOT reach, which is
            // the accepted precision/recall trade of a cheap gate (see header BIAS note).
            if (IsTitleCase(text) && hasContext && !IsSentenceInitial(fullValue!, run.Start))
            {
                return ForeignRunDecision.Leave;
            }

            // (8) NAME-PARTICLE: an all-lowercase Latin run that lies WITHIN a Title-Case Latin
            // NAME SPAN — a name connective, not a leaked common word. NOT merely "immediately
            // sandwiched": runs are word-level (a space ends a run), so an immediate-adjacency test
            // makes two ADJACENT lowercase particles disqualify each OTHER and both then REPAIR —
            // "The Lord [of the] Rings", "Mies [van der] Rohe", "Charles [de la] Rue" — after which
            // d3 splices Hebrew into a book title / surname and validation-by-re-detect cannot see
            // it (substituting Hebrew for "of" REDUCES the Latin-run count, so it reads as a
            // successful repair). See WithinTitleCaseLatinNameSpan for the bounded outward scan.
            if (hasContext
                && IsAllLatinLower(text)
                && WithinTitleCaseLatinNameSpan(fullValue!, run.Start, run.Start + run.Length))
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
    /// True when the run at [<paramref name="start"/>, <paramref name="start"/>+<paramref name="length"/>)
    /// sits inside a MULTI-word quoted span in <paramref name="fullValue"/>. THREE conditions, all
    /// required — the gate is deliberately SAFE BY CONSTRUCTION, not merely safe against the current
    /// corpus (be-c05):
    /// <list type="number">
    ///   <item>an OPENING quote is reachable to the LEFT and a CLOSING quote to the RIGHT (crossing
    ///     only letters of either script / digits / spaces / hyphens, never a sentence terminator or
    ///     other punctuation, within a bounded window), each "boundary-like" on its outer side;</item>
    ///   <item>the two bounds are a MATCHING quote PAIR (see <see cref="IsMatchingQuotePair"/>) — an
    ///     ASCII <c>"</c> closes a <c>"</c>, a <c>»</c> closes a <c>«</c>, a <c>”</c> closes a <c>“</c>,
    ///     and so on. Without this, ANY delimiter-ish character on the right could close a span opened
    ///     by a real quote: a trailing possessive apostrophe (<c>the girls' faces</c>) or — before this
    ///     rule — a Hebrew abbreviation geresh (<c>וכו׳</c>, <c>עמ׳</c>, now dropped from the delimiter
    ///     set entirely, see <see cref="IsQuoteChar"/>) is followed by a space and is therefore
    ///     "boundary-like", so it could serve as a CLOSING bound and spare a real leak;</item>
    ///   <item>at least one OTHER word lies inside the span — a LONE scare-quoted word
    ///     ("Confusion") is NOT spared, only a genuine quoted phrase / foreign idiom ("carpe diem").</item>
    /// </list>
    /// Purely context-derived; no word list.
    /// </summary>
    private static bool IsInsideQuotedForeignSpan(string fullValue, int start, int length)
    {
        var (leftFound, leftQuote, leftCrossedWord) = ScanForBoundingQuote(fullValue, start - 1, -1, opening: true);
        if (!leftFound)
        {
            return false;
        }

        var (rightFound, rightQuote, rightCrossedWord) = ScanForBoundingQuote(fullValue, start + length, +1, opening: false);
        if (!rightFound)
        {
            return false;
        }

        // The two bounds must be a real quote PAIR, not just "a quote char on each side". This is the
        // by-construction guarantee: a legitimate non-quote delimiter (possessive apostrophe) can never
        // close a span opened by a different quote character.
        if (!IsMatchingQuotePair(leftQuote, rightQuote))
        {
            return false;
        }

        // Require the quoted span to hold at least one word BESIDES this run (a phrase, not a lone
        // scare-quoted word). A word on either side of the run inside the quotes is enough.
        return leftCrossedWord || rightCrossedWord;
    }

    /// <summary>
    /// Scans <paramref name="s"/> from <paramref name="from"/> in direction <paramref name="step"/>
    /// (-1 left, +1 right) for a bounding quote character, crossing only letters (either script),
    /// digits, spaces and hyphens. Returns <c>(found, quote, crossedWord)</c>.
    /// <para><c>found</c> is true only when a quote of the WANTED DIRECTION is reached — an
    /// <see cref="IsOpeningQuoteChar"/> when <paramref name="opening"/>, an
    /// <see cref="IsClosingQuoteChar"/> otherwise — AND it is "boundary-like" on its OUTER side (its
    /// outward neighbour is a non-word char or the string edge), so an OPENING quote is preceded by a
    /// boundary and a CLOSING quote is followed by one. A quote hugged by a letter on the outside is a
    /// mid-word / adjacent-span quote and does NOT count, which defuses a run wedged between two
    /// SEPARATE quoted spans (and the Hebrew gershayim of <c>צה״ל</c>, which sits between two letters).</para>
    /// <para><c>quote</c> is the character that bounded the span, so the caller can require the two
    /// bounds to PAIR. A delimiter of the WRONG direction (a <c>»</c> found while scanning left) ABORTS
    /// the scan rather than being skipped — it is punctuation, and walking past it would let a span
    /// reach across an unrelated quotation.</para>
    /// <c>crossedWord</c> records whether a letter was crossed before the quote (another word lies
    /// between the run and the quote). A sentence terminator, other punctuation (a Hebrew geresh
    /// included — it is no longer a delimiter, so it TERMINATES the candidate span, which is the
    /// conservative / bias-to-REPAIR direction), the string edge, or the bounded window ends the scan
    /// with <c>found=false</c>.
    /// </summary>
    private static (bool found, char quote, bool crossedWord) ScanForBoundingQuote(string s, int from, int step, bool opening)
    {
        const int window = 64;
        var crossedWord = false;
        var i = from;
        var steps = 0;
        while (i >= 0 && i < s.Length && steps < window)
        {
            var c = s[i];
            if (IsQuoteChar(c))
            {
                // A delimiter of the wrong direction is not a bound — and it is punctuation, so the
                // candidate span ends here rather than scanning past it.
                if (opening ? !IsOpeningQuoteChar(c) : !IsClosingQuoteChar(c))
                {
                    return (false, '\0', crossedWord);
                }

                var outerIdx = opening ? i - 1 : i + 1;
                var boundaryLike = outerIdx < 0 || outerIdx >= s.Length || !IsWordChar(s[outerIdx]);
                return (boundaryLike, boundaryLike ? c : '\0', crossedWord);
            }

            if (IsLetterEitherScript(c))
            {
                crossedWord = true;
            }
            else if (!(IsAsciiDigit(c) || c == ' ' || c == '-'))
            {
                // Sentence terminator / comma / geresh / other punctuation ends the candidate span.
                return (false, '\0', crossedWord);
            }

            i += step;
            steps++;
        }

        return (false, '\0', crossedWord);
    }

    /// <summary>
    /// The BOUND on the name-span walk: the most all-lowercase Latin tokens a Title-Case name span
    /// may contain, COUNTING the run being classified. Chosen as 3 because the longest lowercase
    /// particle chains that occur in real names and titles are TWO tokens — "van der" (Mies van der
    /// Rohe), "de la" (Charles de la Rue), "of the" (The Lord of the Rings) — so 3 covers every
    /// observed shape with exactly one token of headroom, while a leaked English clause (the thing
    /// that must still REPAIR) is materially longer. Counting the TOTAL chain rather than capping
    /// each side independently keeps the verdict CHAIN-UNIFORM: every lowercase run between the same
    /// two anchors sees the same total and therefore gets the same decision, so a chain can never be
    /// half-LEAVE / half-REPAIR (which is how a partial splice would corrupt a name).
    /// </summary>
    private const int MaxNameSpanLowercaseTokens = 3;

    /// <summary>
    /// True when the run at [<paramref name="start"/>, <paramref name="end"/>) lies WITHIN a
    /// Title-Case Latin name span: scanning OUTWARD from the run across space-separated Latin tokens
    /// there is a Title-Case Latin token on BOTH sides, with only all-lowercase Latin tokens in
    /// between, and the whole lowercase chain (both sides plus the run itself) is at most
    /// <see cref="MaxNameSpanLowercaseTokens"/> tokens. This generalizes the old immediate-adjacency
    /// rule, which recognized only a SINGLE particle ("Vincent van Gogh") and mis-classified two
    /// adjacent particles ("Mies van der Rohe") as leaks because each disqualified the other.
    /// The walk is bounded on every axis — at most a handful of token hops per side, exactly one
    /// space between tokens, and it aborts the moment it crosses a non-Latin character, a non-space
    /// separator, punctuation, an ALL-CAPS / mixed-case / single-letter token, or the string edge —
    /// so it has a defined found-nothing answer (false) and cannot run away.
    /// </summary>
    private static bool WithinTitleCaseLatinNameSpan(string fullValue, int start, int end)
    {
        if (!ScanOutwardForTitleCaseAnchor(fullValue, start, step: -1, out var leftLowercase))
        {
            return false;
        }

        if (!ScanOutwardForTitleCaseAnchor(fullValue, end, step: +1, out var rightLowercase))
        {
            return false;
        }

        // +1 for the run itself: it is the middle link of the chain it sits in.
        return leftLowercase + 1 + rightLowercase <= MaxNameSpanLowercaseTokens;
    }

    /// <summary>
    /// Walks outward from <paramref name="boundary"/> (the run's first index when
    /// <paramref name="step"/> is -1; the index just PAST the run when it is +1) across
    /// space-separated Latin tokens, looking for a Title-Case Latin ANCHOR. Returns true the moment
    /// one is found, with <paramref name="lowercaseCrossed"/> = how many all-lowercase Latin tokens
    /// were crossed to reach it. Returns false — the defined found-nothing answer — when the very
    /// next thing outward is not "one space then a Latin letter" (a Hebrew neighbour, punctuation, a
    /// digit, a double space, the string edge), when a crossed token is not all-lowercase Latin
    /// (ALL-CAPS, mixed case, a single letter), or when the hop bound is exhausted without an anchor.
    /// At most <see cref="MaxNameSpanLowercaseTokens"/> tokens are examined per side, so at most
    /// <c>MaxNameSpanLowercaseTokens - 1</c> lowercase tokens can be crossed before the anchor.
    /// </summary>
    private static bool ScanOutwardForTitleCaseAnchor(string s, int boundary, int step, out int lowercaseCrossed)
    {
        lowercaseCrossed = 0;

        // Left walk: cursor = first index of the token we last stood on. Right walk: cursor = index
        // just past it. Either way the next separator to inspect sits immediately outward of it.
        var cursor = boundary;

        for (var hop = 0; hop < MaxNameSpanLowercaseTokens; hop++)
        {
            int tokStart, tokEnd;

            if (step < 0)
            {
                var spaceIdx = cursor - 1;
                if (spaceIdx < 0 || s[spaceIdx] != ' ')
                {
                    return false; // string edge, or a non-space separator / punctuation
                }

                tokEnd = spaceIdx - 1; // last char of the outward token — must sit DIRECTLY on the space
                if (tokEnd < 0 || !IsLatinLetter(s[tokEnd]))
                {
                    return false; // double space, Hebrew / digit / punctuation neighbour, string edge
                }

                tokStart = tokEnd;
                while (tokStart - 1 >= 0 && IsLatinLetter(s[tokStart - 1]))
                {
                    tokStart--;
                }

                cursor = tokStart;
            }
            else
            {
                var spaceIdx = cursor;
                if (spaceIdx >= s.Length || s[spaceIdx] != ' ')
                {
                    return false;
                }

                tokStart = spaceIdx + 1; // first char of the outward token — must sit DIRECTLY on the space
                if (tokStart >= s.Length || !IsLatinLetter(s[tokStart]))
                {
                    return false;
                }

                tokEnd = tokStart;
                while (tokEnd + 1 < s.Length && IsLatinLetter(s[tokEnd + 1]))
                {
                    tokEnd++;
                }

                cursor = tokEnd + 1;
            }

            if (IsTitleCaseToken(s, tokStart, tokEnd))
            {
                return true; // the anchor: a Title-Case Latin name token
            }

            if (!IsAllLowercaseToken(s, tokStart, tokEnd))
            {
                return false; // ALL-CAPS / mixed-case / single-letter token: not a name-span link
            }

            lowercaseCrossed++; // another particle in the chain — keep walking outward
        }

        return false; // bound exhausted with no anchor
    }

    /// <summary>Index-based, allocation-free twin of <see cref="IsAllLatinLower"/> over
    /// <c>s[start..end]</c> (inclusive): every char a Latin lowercase letter, length &gt;= 2.</summary>
    private static bool IsAllLowercaseToken(string s, int start, int end)
    {
        if (end - start + 1 < 2)
        {
            return false;
        }

        for (var i = start; i <= end; i++)
        {
            if (!IsLatinLower(s[i]))
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
        // A BookEntitySet carries its OWN two-tier matching semantics (be-c04): DECLARED names (from stored
        // analysis) match case-insensitively, MANUSCRIPT-harvested tokens match case-SENSITIVELY, because the
        // only evidence for a harvested token is the CAPITALIZED form that was observed — and a leak is
        // lowercase by construction. Its Contains is therefore AUTHORITATIVE: a miss is a real miss and must
        // NOT be widened by the case-insensitive fallback scan below, which is precisely the widening that let
        // one capitalized "Confusion" in a manuscript spare every lowercase "confusion" in the analysis output.
        if (set is BookEntitySet bookEntitySet)
        {
            return bookEntitySet.Contains(value);
        }

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

    /// <summary>A "word" char for quote-boundary tests: a letter of either script or an ASCII digit.
    /// A quote hugged by one of these on its OUTER side is a mid-word quote, not a span boundary.</summary>
    private static bool IsWordChar(char c) => IsLetterEitherScript(c) || IsAsciiDigit(c);

    /// <summary>
    /// The quote / do-not-translate delimiter set: ASCII <c>"</c> / <c>'</c>, guillemets, curly
    /// typographic quotes, and the Hebrew gershayim <c>״</c> (U+05F4).
    /// <para>The Hebrew GERESH <c>׳</c> (U+05F3) is DELIBERATELY NOT a delimiter (be-c05). It is an
    /// ABBREVIATION mark far more often than a quote — <c>וכו׳</c> (etc.), <c>עמ׳</c> (page) — and it
    /// occurs there as a TRAILING character followed by a space, i.e. "boundary-like" on its outer
    /// side, so admitting it let an ordinary Hebrew abbreviation serve as the CLOSING bound of a
    /// "quoted span" opened by a real quote and thereby spare a genuine leak. (The gershayim needs no
    /// such treatment: in <c>צה״ל</c> / <c>ארה״ב</c> it sits BETWEEN two Hebrew letters, so its outer
    /// neighbour is a word char and it is rejected by the boundary-like test — defused by construction.
    /// Hebrew that genuinely quotes uses <c>"</c>, <c>״</c> or the curly quotes.) Dropping it does not
    /// make it transparent: an unlisted geresh is "other punctuation" and TERMINATES the candidate span
    /// in <see cref="ScanForBoundingQuote"/>, which is the conservative / bias-to-REPAIR direction.</para>
    /// </summary>
    private static bool IsQuoteChar(char c) => IsOpeningQuoteChar(c) || IsClosingQuoteChar(c);

    /// <summary>Delimiters that can OPEN a quoted span. Directional forms appear only here
    /// (<c>«</c>, <c>“</c>, <c>‘</c>); the symmetric forms (<c>"</c>, <c>'</c>, <c>״</c>) are in both sets.</summary>
    private static bool IsOpeningQuoteChar(char c)
        => c is '"' or '\''
            or '«' /* « */
            or '“' /* “ */
            or '‘' /* ‘ */
            or '״' /* ״ gershayim */;

    /// <summary>Delimiters that can CLOSE a quoted span. Directional forms appear only here
    /// (<c>»</c>, <c>”</c>, <c>’</c>); the symmetric forms (<c>"</c>, <c>'</c>, <c>״</c>) are in both sets.</summary>
    private static bool IsClosingQuoteChar(char c)
        => c is '"' or '\''
            or '»' /* » */
            or '”' /* ” */
            or '’' /* ’ */
            or '״' /* ״ gershayim */;

    /// <summary>
    /// The PAIRING table: true when <paramref name="close"/> is the legitimate closing partner of
    /// <paramref name="open"/>. Requiring a matching pair (rather than "any delimiter on each side")
    /// is what makes the quote gate safe BY CONSTRUCTION — a trailing possessive apostrophe
    /// (<c>the girls' faces</c>) is boundary-like and therefore reachable as a right-hand bound, but it
    /// can no longer CLOSE a span opened by a <c>"</c> or a <c>«</c>.
    /// <para>The symmetric delimiters (<c>"</c>, <c>'</c>, <c>״</c>) pair with themselves, so for those
    /// the boundary-like test remains the only opening/closing signal — inherent to the characters, not
    /// a weakness of the rule (an apostrophe-delimited <c>'carpe diem'</c> and a possessive
    /// <c>'</c>-pair are typographically identical). The directional pairs are exact.</para>
    /// </summary>
    private static bool IsMatchingQuotePair(char open, char close) => (open, close) switch
    {
        ('"', '"') => true,
        ('\'', '\'') => true,
        ('«', '»') => true,
        ('“', '”') => true,
        ('‘', '’') => true,
        ('״', '״') => true,
        _ => false,
    };

    /// <summary>A dotted-host neighbour char: Latin letter or digit (a segment of example.com / file2.txt).</summary>
    private static bool IsHostChar(char c) => IsLatinUpper(c) || IsLatinLower(c) || IsAsciiDigit(c);
}
