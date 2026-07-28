namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// ScriptTokenPredicates — the ONE shared definition of "letter" / "Title-Case
// token" for the foreign-run pipeline (be-f02, dynamic-term-repair precision
// follow-up plan). Before this file existed, LatinInHebrewContentDetector (which
// PRODUCES foreign runs), ForeignRunClassifier (which CLASSIFIES them Repair/
// Leave), and BookEntityProvider (which HARVESTS the entity set the classifier
// consults) each carried a private copy of these predicates. If one copy drifted
// from another, the three stages would disagree exactly at the margin that
// matters — invisibly, because each site looks locally correct. Consuming this
// shared class instead makes that class of bug structurally impossible.
//
// PURE REFACTOR: every method here is byte-for-byte the same logic as the
// private copies it replaces (verified by diff, not rewritten). See the plan's
// be-f02 todo for the verification notes.
// ---------------------------------------------------------------------------

/// <summary>
/// Shared character/token classification predicates for the foreign-run detection
/// pipeline (<see cref="LatinInHebrewContentDetector"/> → <see cref="ForeignRunClassifier"/>
/// → <see cref="BookEntityProvider"/>). Single source of truth for what counts as a
/// Latin/Hebrew letter and what shape a "Title-Case token" or "sentence-initial
/// position" is, so the three stages can never quietly disagree at the margin.
/// </summary>
internal static class ScriptTokenPredicates
{
    /// <summary>Latin ASCII uppercase letter test ([A-Z]).</summary>
    public static bool IsLatinUpper(char c) => c >= 'A' && c <= 'Z';

    /// <summary>Latin ASCII lowercase letter test ([a-z]).</summary>
    public static bool IsLatinLower(char c) => c >= 'a' && c <= 'z';

    /// <summary>Latin ASCII letter test ([A-Za-z]); accented Latin-1 letters are
    /// deliberately excluded to match the original regex semantics.</summary>
    public static bool IsLatinLetter(char c) => IsLatinUpper(c) || IsLatinLower(c);

    /// <summary>Hebrew base letter block U+05D0..U+05EA (aleph..tav incl. final forms); niqqud,
    /// geresh/gershayim and other marks are deliberately NOT letters (they are span boundaries / quotes).</summary>
    public static bool IsHebrewLetter(char c) => c >= 'א' && c <= 'ת';

    /// <summary>True when <paramref name="c"/> is a letter of either script (Latin or Hebrew).</summary>
    public static bool IsLetterEitherScript(char c) => IsLatinLetter(c) || IsHebrewLetter(c);

    /// <summary>ASCII digit test ([0-9]).</summary>
    public static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    /// <summary>Title-Case = a leading Latin uppercase letter followed by all Latin lowercase
    /// (length &gt;= 2), e.g. "Kafka", "Sarah", "Jerusalem". Requires the ASCII-Latin run shape
    /// the detector produces.</summary>
    public static bool IsTitleCase(string text)
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

    /// <summary>Index-based, allocation-free twin of <see cref="IsTitleCase"/> over
    /// <c>s[start..end]</c> (inclusive): a leading Latin uppercase letter followed by all Latin
    /// lowercase, length &gt;= 2.</summary>
    public static bool IsTitleCaseToken(string s, int start, int end)
    {
        if (end - start + 1 < 2 || !IsLatinUpper(s[start]))
        {
            return false;
        }

        for (var i = start + 1; i <= end; i++)
        {
            if (!IsLatinLower(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>All-lowercase Latin run (length &gt;= 2), e.g. the name particle "van" / "da" / "de".</summary>
    public static bool IsAllLatinLower(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!IsLatinLower(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the token at <paramref name="start"/> OPENS a sentence in <paramref name="text"/> —
    /// i.e. scanning left of <paramref name="start"/> and skipping whitespace and transparent opening
    /// punctuation (quotes/brackets/parentheses), the first meaningful character is a sentence
    /// terminator ('.', '!', '?', '…') or the string start. Used to DISQUALIFY sentence-initial
    /// capitalization from the mid-sentence proper-noun signal.
    /// <para>EVALUATION ORDER (the difference from <see cref="IsLineInitial"/>, which follows directly
    /// below): the <c>char.IsWhiteSpace</c> skip is tested BEFORE the terminator check, so a '\n' / '\r'
    /// is consumed here as ordinary whitespace and the line-break entries in
    /// <see cref="IsSentenceTerminator"/> are unreachable from THIS scan. A line break therefore does not
    /// by itself make a following token sentence-initial.</para>
    /// </summary>
    public static bool IsSentenceInitial(string text, int start)
    {
        var i = start - 1;
        while (i >= 0)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || IsTransparentOpen(c))
            {
                i--;
                continue;
            }

            return IsSentenceTerminator(c);
        }

        return true; // reached the start of the text
    }

    /// <summary>
    /// True when the token at <paramref name="start"/> is LINE-INITIAL in <paramref name="text"/>: scanning
    /// LEFT and treating whitespace and transparent opening punctuation (quotes / brackets, the same set
    /// <see cref="IsSentenceInitial"/> skips) as transparent, the first meaningful character is a HARD LINE
    /// BREAK ('\n' or '\r'). The break test comes FIRST, so '\n' / '\r' are never consumed by the whitespace
    /// skip; the remaining Unicode line separators (U+2028 / U+2029 / U+0085 / '\v' / '\f') are treated as
    /// ordinary transparent whitespace here, deliberately, because none of them occurs in the persisted prose
    /// this predicate runs over.
    /// <para>ONE break is enough; a BLANK line is deliberately NOT required. The name says "line", not
    /// "paragraph", because that is what the predicate actually tests, and because it is what the data needs:
    /// the real persisted prose this layer repairs separates paragraphs with a SINGLE '\n' (be-c03 measured
    /// the runtime DB: 3 of 3 <c>BookProfiles.Synopsis</c> values and 32 of 33 <c>ChunkSummaries.SummaryText</c>
    /// values carry a lone break and ZERO carry a blank line). A blank-line requirement would make
    /// ForeignRunClassifier rule (7b) dead on every real value.</para>
    /// <para>Reaching the START of the value returns FALSE: value-initial is deliberately NOT a line head
    /// for rule (7b), which is what keeps a short single-sentence field's opening token REPAIRable.</para>
    /// <para>NOT a subset of <see cref="IsSentenceInitial"/>, and neither predicate contains the other. The
    /// whole difference is the EVALUATION ORDER visible in the two bodies above and below: this scan tests the
    /// '\n' / '\r' break FIRST, whereas <see cref="IsSentenceInitial"/> tests <c>char.IsWhiteSpace</c> first
    /// and so skips the break as ordinary whitespace before it can ever reach the line-break entries in
    /// <see cref="IsSentenceTerminator"/>. Hence a soft wrap is line-initial but not sentence-initial, and a
    /// value start is sentence-initial but not line-initial. Rule (7b) is still strictly ADDITIVE over rule
    /// (7), but by evaluation order rather than by a set relation: rule (7) is tested first and already
    /// returns Leave for every run where <see cref="IsSentenceInitial"/> is false.</para>
    /// </summary>
    public static bool IsLineInitial(string text, int start)
    {
        for (var i = start - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is '\n' or '\r')
            {
                return true;
            }

            if (char.IsWhiteSpace(c) || IsTransparentOpen(c))
            {
                continue;
            }

            return false;
        }

        return false; // the start of the VALUE, not a line head (see the remarks above)
    }

    /// <summary>Opening punctuation transparent to sentence-initial detection (quotes / brackets).</summary>
    public static bool IsTransparentOpen(char c)
        => c is '"' or '\'' or '(' or '[' or '{'
            or '«' /* « */ or '‹' /* ‹ */
            or '“' /* “ */ or '‘' /* ‘ */
            or '¿' /* ¿ */ or '¡' /* ¡ */;

    /// <summary>Sentence-ending characters that make a following capital sentence-initial.</summary>
    public static bool IsSentenceTerminator(char c)
        => c is '.' or '!' or '?' or '…' /* … */ or '\n' or '\r';
}
