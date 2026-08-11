using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// QUERY-SIDE synonym tolerance for <see cref="GuideSelector"/> (chatbot phase A.2, c2).
///
/// <para>WHY THIS IS A RETRIEVAL CHANGE AND NOT A GROUNDING CHANGE. Retrieval can only SELECT whole
/// guides that already exist; it can never invent one, and it never reaches the model as instruction
/// text. So widening what a question can match is the one lever that makes the assistant understand a
/// question better without touching the contract the g1-g4 gate measures. What it CAN do is change
/// WHICH four guides reach the model, which is why every bound below is a property of this file rather
/// than of the corpus that happens to ship today, and why the map is pinned by tests that read the
/// real corpus.</para>
///
/// <para>THE PROBLEM IT SOLVES. <see cref="GuideSelector"/> scores whole-token matches against H1/H2
/// headings and the frontmatter id/stage, and reads no body prose at all. English gets NO morphology
/// of any kind, so <c>manuscripts</c> misses the heading <c>Importing your manuscript</c> outright;
/// Hebrew gets only be-c02's single-affix tolerance, which by construction never crosses two different
/// words, so <c>העלאה</c> cannot reach <c>ייבוא</c> and <c>קובץ</c> cannot reach <c>קבצים</c> (the
/// four-letter stem floor rejects <c>קבצ</c>). Both are ordinary ways an author asks the question, and
/// both currently score zero against the guide that answers them.</para>
///
/// <para><b>THE BOUNDS, BY CONSTRUCTION.</b></para>
///
/// <para><b>1. Closed, curated, and grounded in the shipped headings.</b> This is a hand-written table,
/// not a thesaurus and not a stemmer. Every TARGET is a token that occurs verbatim in some shipped
/// guide heading, which
/// <c>ProductChatSelectorTests.EveryExpansionTarget_OccursVerbatimInAShippedGuideHeading</c> proves
/// against the real corpus; a target the corpus does not carry is dead weight that can only mislead a
/// reader, so it fails the build rather than sitting there.</para>
///
/// <para><b>2. Same script on both sides of every entry.</b> A Latin key expands only to Latin terms
/// and a Hebrew key only to Hebrew terms, so an English question can no more reach a Hebrew heading
/// through this table than it could before, and the cross-language behaviour g1-g4 measured (F3, the
/// wrong-language twin taking a filler slot) cannot move through this path. Same bound-1 story
/// <see cref="GuideSelector.InflectionKeys"/> already carries, restated because it has to hold
/// independently.</para>
///
/// <para><b>3. Headings only, and strictly weakest.</b> <see cref="GuideSelector.Score"/> tries the
/// exact match first, then the Hebrew inflection, and only then this table, at
/// <see cref="GuideSelector.SynonymHeadingWeight"/> - below both. It never reaches the frontmatter
/// <c>id</c>/<c>stage</c>, which is the mechanism by which a question reaches a wrong-language twin.
/// So no document can LOSE score here, and a guide whose heading carries the author's actual word
/// still outranks one reachable only by synonym.</para>
///
/// <para>WHAT IS DELIBERATELY ABSENT. No entry maps a word for a capability the guides do not
/// document. In particular there is no entry for <c>shortcut</c>, <c>keyboard</c>, <c>hotkey</c> or
/// <c>קיצור</c>: the corpus contains zero occurrences of that class (live-verified in g3 and again in
/// g4), so an entry could only route the question somewhere that cannot answer it, and the honest
/// refusal that question gets today is the measured-clean outcome the gate protects.</para>
/// </summary>
public static class GuideQueryExpansion
{
    /// <summary>
    /// The table. KEY is a question token as <see cref="GuideSelector.Tokenize"/> produces it
    /// (lower-cased, longer than one character, not a stop word); VALUES are heading vocabulary.
    ///
    /// <para>English carries the plural/singular and the -ing pairs as well as true synonyms, because
    /// the selector has no English morphology at all and those are the same defect wearing a different
    /// hat. Hebrew carries only entries that be-c02's single-affix tolerance genuinely cannot reach:
    /// <c>עברית</c> is absent on purpose, since the heading <c>ספרים בעברית</c> already yields it by
    /// stripping the <c>ב</c>.</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        // ── English: the same act under a different verb ────────────────────────────────────────
        ["upload"]        = new[] { "import" },
        ["uploads"]       = new[] { "import" },
        ["uploading"]     = new[] { "import" },
        ["imports"]       = new[] { "import" },
        ["manuscripts"]   = new[] { "manuscript" },
        ["download"]      = new[] { "export" },
        ["downloads"]     = new[] { "export" },
        ["downloading"]   = new[] { "export" },
        ["exports"]       = new[] { "export" },
        // A file extension is how authors name the two file-shaped stages; both guides are honest
        // candidates for it and the ranking decides between them on the rest of the question.
        ["docx"]          = new[] { "import", "export" },
        // ── English: the passes, asked for by what they do ──────────────────────────────────────
        ["spelling"]      = new[] { "proofread" },
        ["spellcheck"]    = new[] { "proofread" },
        ["typo"]          = new[] { "proofread" },
        ["typos"]         = new[] { "proofread" },
        ["grammar"]       = new[] { "proofread" },
        ["proofreading"]  = new[] { "proofread" },
        ["summarise"]     = new[] { "summarize" },
        ["synopsis"]      = new[] { "summary", "briefs" },
        ["brief"]         = new[] { "briefs" },
        ["structural"]    = new[] { "developmental" },
        ["reviews"]       = new[] { "review" },
        ["finding"]       = new[] { "findings" },
        // ── English: running something, and freshness ───────────────────────────────────────────
        ["start"]         = new[] { "run" },
        ["starts"]        = new[] { "run" },
        ["launch"]        = new[] { "run" },
        ["outdated"]      = new[] { "stale", "date" },
        ["obsolete"]      = new[] { "stale", "date" },
        ["rtl"]           = new[] { "hebrew" },

        // ── Hebrew: the same act under a different verb ─────────────────────────────────────────
        ["העלאה"]         = new[] { "ייבוא" },
        ["להעלות"]        = new[] { "ייבוא" },
        ["לייבא"]         = new[] { "ייבוא" },
        ["הורדה"]         = new[] { "ייצוא" },
        ["להוריד"]        = new[] { "ייצוא" },
        ["לייצא"]         = new[] { "ייצוא" },
        ["קובץ"]          = new[] { "קבצים", "לקובץ" },
        ["מסמך"]          = new[] { "קבצים" },
        // ── Hebrew: the passes, asked for by what they do ───────────────────────────────────────
        ["שגיאות"]        = new[] { "הגהה" },
        ["כתיב"]          = new[] { "הגהה" },
        ["איות"]          = new[] { "הגהה" },
        ["דקדוק"]         = new[] { "הגהה" },
        ["תמצית"]         = new[] { "סיכום", "תקצירי" },
        // The stage vocabulary e1 RETIRED from the corpus. An author who learned the product before
        // that copy-edit still types it, and it now scores zero against the guide that answers it.
        ["סקירה"]         = new[] { "התפתחותית", "ההתפתחותית" },
        ["סקירת"]         = new[] { "התפתחותית", "ההתפתחותית" },
        // ── Hebrew: running something, and freshness ────────────────────────────────────────────
        ["להתחיל"]        = new[] { "מריצים" },
        ["הפעלה"]         = new[] { "מריצים" },
        ["להפעיל"]        = new[] { "מריצים" },
        ["מיושן"]         = new[] { "עדכני", "עדכניים" },
        ["מיושנים"]       = new[] { "עדכני", "עדכניים" },
    };

    /// <summary>
    /// The single-letter clitics Hebrew attaches to the front of a word. The same closed set
    /// <see cref="GuideSelector"/>'s inflection tolerance is stated against, for the same reason: a
    /// table keyed on <c>קובץ</c> must still fire on <c>הקובץ</c>, which is how the question is
    /// actually typed at least as often.
    /// </summary>
    private static readonly char[] HebrewPrefixLetters = { 'ו', 'ה', 'ב', 'כ', 'ל', 'מ', 'ש' };

    /// <summary>
    /// The heading terms <paramref name="token"/> may additionally be compared against, or empty when
    /// the table has no entry for it (which is the overwhelmingly common case).
    ///
    /// <para>A Hebrew token is looked up twice: as typed, and with at most ONE leading clitic removed.
    /// That second lookup cannot reach a short stem, because every Hebrew KEY in the table is at least
    /// four letters (asserted by
    /// <c>ProductChatSelectorTests.EveryHebrewExpansionKey_IsAtLeastTheInflectionStemFloor</c>), so the
    /// stripped form has to agree with a curated four-letter-or-longer word to hit anything at all.
    /// Latin tokens are never stripped: English clitics do not exist and the table's English keys are
    /// whole words.</para>
    /// </summary>
    public static IReadOnlyList<string> Expand(string? token)
    {
        if (token is null) return Array.Empty<string>();
        if (Table.TryGetValue(token, out var terms)) return terms;

        if (token.Length > 1 && IsAllHebrew(token) && Array.IndexOf(HebrewPrefixLetters, token[0]) >= 0
            && Table.TryGetValue(token[1..], out var stripped))
        {
            return stripped;
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// The whole table, exposed so its bounds can be asserted as PROPERTIES of the table rather than
    /// re-listed by a test that would then have to be kept in step with it by hand.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Entries => Table;

    /// <summary>
    /// True when every character of <paramref name="text"/> is a Hebrew letter. The script predicate
    /// the same-script bound is stated against, shared by <see cref="GuideSelector"/>'s inflection
    /// bound so the two cannot drift into disagreeing about what "Hebrew" means.
    /// </summary>
    public static bool IsAllHebrew(string text)
    {
        if (text.Length == 0) return false;

        foreach (var c in text)
        {
            if (!ScriptTokenPredicates.IsHebrewLetter(c)) return false;
        }

        return true;
    }
}
