using System.Text;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// The retrieval half of chatbot phase A, implementing d1's selection algorithm EXACTLY (d1 item 1).
///
/// <para>PURE AND DETERMINISTIC BY DESIGN. No model call, no clock, no filesystem, no randomness -
/// the same (question, corpus) pair always produces the same ordered selection. That is what makes
/// retrieval the one part of this feature that can be pinned cheaply, and retrieval is the part most
/// likely to regress silently: a ranking change shows up as a subtly worse answer, never as a
/// failure.</para>
///
/// <para>NO VECTOR STORE, deliberately (d1 item 1, restating the plan's simplicity decision):
/// 15 files / ~73 KB is not a vector-database problem, whole-file worst case already fits the
/// configured 16k window with margin, and <c>IEmbeddingService</c>/<c>IEmbeddingStore</c> exist only
/// as stubs that THROW. If retrieval quality measures poorly in g1, tune the weights below first.</para>
///
/// <para>THE SELECTOR NEVER DECIDES "NO COVERAGE" (d1 item 1 step 5). On a weak match it still
/// returns its top N. Judging whether the guides actually answer the question is the model's job
/// under the grounding rule, and a selector that refused early would turn a coverage question into a
/// retrieval question. The ONLY thing that stops an answer here is a genuinely empty corpus, which
/// is <see cref="GuidesCorpus.Fault"/>'s business, not this class's.</para>
/// </summary>
public static class GuideSelector
{
    /// <summary>How many whole guides go into a prompt (d1: N = 4, token math done against it).</summary>
    public const int DefaultCount = 4;

    /// <summary>
    /// Cross-language score multiplier (d1 item 1 step 2). A PREFERENCE, not an exclusion: an
    /// English-only guide (<c>README.md</c> has no Hebrew sibling at all) can still win for a Hebrew
    /// question if it is the only relevant one, it just needs a clearer lexical match to overcome
    /// this. That is what makes d1 item 3 work without a second code path.
    /// </summary>
    public const double CrossLanguagePenalty = 0.5;

    /// <summary>Weight of a question token found in an H1/H2 heading. Headings outrank frontmatter
    /// (d1 item 1 step 1) because they describe what the document ANSWERS.</summary>
    public const double HeadingWeight = 3.0;

    /// <summary>Weight of a question token found in the frontmatter <c>id</c> or <c>stage</c>.</summary>
    public const double FrontmatterWeight = 1.0;

    /// <summary>
    /// Weight of a question token that reaches an H1/H2 heading only as a Hebrew INFLECTION of a
    /// heading word rather than as the word itself (be-c02). Strictly between
    /// <see cref="FrontmatterWeight"/> and <see cref="HeadingWeight"/>: a guide whose heading carries
    /// the author's actual word must still outrank one that carries only a related form, so the
    /// tolerance can add a document to a selection without ever re-ordering two documents that both
    /// match exactly. Exact and inflected never both count for the same question token - the exact
    /// match wins and the inflected weight is not added on top.
    /// </summary>
    public const double InflectedHeadingWeight = 2.0;

    /// <summary>
    /// Tokens carried by nearly every question or heading in either language. Excluded so the ranking
    /// is driven by topic words rather than by which guide happens to contain more prose in its
    /// headings. Deliberately SHORT: an aggressive stop list would start discarding real product
    /// vocabulary, and the corpus is small enough that a few noise matches do not decide a ranking.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // English
        "the", "and", "for", "you", "your", "with", "what", "when", "where", "which", "how", "does",
        "did", "can", "are", "was", "were", "this", "that", "these", "those", "from", "into", "out",
        "not", "but", "all", "any", "its", "it", "is", "do", "of", "in", "on", "to", "an", "at",
        "my", "me", "we", "us", "get", "got", "has", "have", "will", "would", "should",
        // Hebrew
        "את", "של", "עם", "על", "מה", "איך", "אני", "אתה", "הוא", "היא", "הם", "זה", "זו", "יש",
        "אין", "כל", "לא", "כן", "או", "אם", "כי", "גם", "רק", "אבל", "כדי", "לפי", "בין", "אחרי",
        "לפני", "עוד", "כמו", "היה", "היו", "להיות", "צריך", "אפשר", "שלי", "שלך"
    };

    /// <summary>
    /// Ranks the whole corpus against <paramref name="question"/> and returns the top
    /// <paramref name="count"/> whole documents, best first.
    ///
    /// <para>ORDER (d1 item 1 step 3, extended where d1 is silent): descending penalized score, then
    /// SAME-LANGUAGE first, then ascending filename numeric prefix, then filename ordinal. The
    /// language step is an ADDITION to d1's stated tie-break rather than a re-decision of it, and it
    /// is required: an en/he twin pair shares the same numeric prefix (both <c>50-export</c> files
    /// are prefix 50), so "tie-break by numeric prefix" does not by itself order a pair. Breaking
    /// that tie by language is the reading consistent with d1's own language PREFERENCE, and it is
    /// what makes "a Hebrew question never selects the English twin" true for the zero-scoring filler
    /// documents as well as for the matched ones.</para>
    /// </summary>
    /// <param name="questionLanguage">"he" or "en" - see <see cref="ChatLanguage"/>.</param>
    public static IReadOnlyList<GuideDocument> Select(
        string question,
        IReadOnlyList<GuideDocument> corpus,
        string questionLanguage,
        int count = DefaultCount)
    {
        if (corpus.Count == 0 || count <= 0) return Array.Empty<GuideDocument>();

        var questionTokens = Tokenize(question);

        return corpus
            .Select(doc => (doc, score: Score(questionTokens, doc, questionLanguage)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => IsSameLanguage(x.doc, questionLanguage) ? 0 : 1)
            .ThenBy(x => x.doc.NumericPrefix)
            .ThenBy(x => x.doc.FileName, StringComparer.Ordinal)
            .Take(count)
            .Select(x => x.doc)
            .ToList();
    }

    /// <summary>
    /// The penalized score for one document. Exposed so a test can assert the RANKING RULE rather
    /// than only its outcome, and so a future weight change is visible as a number.
    /// </summary>
    public static double Score(IReadOnlySet<string> questionTokens, GuideDocument doc, string questionLanguage)
    {
        if (questionTokens.Count == 0) return 0.0;

        var headingTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in doc.Headings)
        {
            foreach (var token in Tokenize(heading)) headingTokens.Add(token);
        }

        // The inflection keys of the HEADINGS only - see InflectionKeys for why frontmatter is
        // deliberately excluded.
        var headingStems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in headingTokens)
        {
            foreach (var stem in InflectionKeys(token)) headingStems.Add(stem);
        }

        var frontmatterTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokenize(doc.Id)) frontmatterTokens.Add(token);
        foreach (var token in Tokenize(doc.Stage)) frontmatterTokens.Add(token);

        var raw = 0.0;
        foreach (var token in questionTokens)
        {
            if (headingTokens.Contains(token)) raw += HeadingWeight;
            else if (MatchesByInflection(token, headingStems)) raw += InflectedHeadingWeight;

            if (frontmatterTokens.Contains(token)) raw += FrontmatterWeight;
        }

        return IsSameLanguage(doc, questionLanguage) ? raw : raw * CrossLanguagePenalty;
    }

    /// <summary>
    /// True when <paramref name="questionToken"/> shares an inflection key with some heading token of
    /// the document (<paramref name="headingStems"/> is the union of their
    /// <see cref="InflectionKeys"/>). Every key is at least
    /// <see cref="MinInflectionStemLength"/> Hebrew letters by construction, so a hit here always
    /// means the two tokens agree on that many consecutive letters.
    /// </summary>
    public static bool MatchesByInflection(string questionToken, IReadOnlySet<string> headingStems)
    {
        if (headingStems.Count == 0) return false;

        foreach (var key in InflectionKeys(questionToken))
        {
            if (headingStems.Contains(key)) return true;
        }

        return false;
    }

    /// <summary>
    /// Question/heading tokenizer: lower-cased, split on anything that is not a letter or digit, with
    /// single characters and stop words dropped. Hebrew and Latin are SPLIT by the same rule, so
    /// nothing about tokenization depends on the script.
    ///
    /// <para>MATCHING is not script-neutral, and has not been since be-c02: <see cref="Score"/> falls
    /// back to <see cref="InflectionKeys"/> for all-Hebrew tokens, which Latin tokens never have. See
    /// that method for why, and for the bound.</para>
    /// </summary>
    public static IReadOnlySet<string> Tokenize(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            Flush(current, tokens);
        }

        Flush(current, tokens);
        return tokens;
    }

    private static void Flush(StringBuilder current, HashSet<string> tokens)
    {
        if (current.Length > 1)
        {
            var token = current.ToString();
            if (!StopWords.Contains(token)) tokens.Add(token);
        }

        current.Clear();
    }

    // ─── Hebrew inflection tolerance (be-c02) ───────────────────────────────────────────────────

    /// <summary>Shortest stem an inflection key may be. See <see cref="InflectionKeys"/> bound 2.</summary>
    public const int MinInflectionStemLength = 4;

    /// <summary>Most letters that may be stripped from one token to reach a key (prefix + suffix).</summary>
    public const int MaxInflectionLettersRemoved = 2;

    /// <summary>
    /// The single-letter clitics Hebrew attaches to the FRONT of a word (vav, the definite article,
    /// and the be/ke/le/me/she prepositions). A CLOSED set, deliberately: the bound below is stated
    /// against these seven letters and nothing else.
    /// </summary>
    private static readonly char[] HebrewPrefixLetters = { 'ו', 'ה', 'ב', 'כ', 'ל', 'מ', 'ש' };

    /// <summary>
    /// The inflectional endings this tolerance recognises, longest first so the longest applicable
    /// one is tried. A CLOSED set, and every entry is at most
    /// <see cref="MaxInflectionLettersRemoved"/> letters long.
    /// </summary>
    private static readonly string[] HebrewSuffixes = { "ים", "ות", "ית", "ה", "ת", "י" };

    /// <summary>
    /// The inflection keys of one token: the forms it may be compared against so that a question's
    /// <c>ספרותית</c> can reach a guide heading's <c>ספרותי</c>, its <c>מריץ</c> can reach
    /// <c>מריצים</c>, and its <c>עריכה</c> can reach <c>העריכה</c> / <c>עריכת</c>. g2 measured the
    /// owner's own question (<c>איך אני מריץ עריכה ספרותית?</c>) selecting the answering guide ZERO
    /// times out of three because exact whole-token matching reaches none of those.
    ///
    /// <para>THE BOUND, BY CONSTRUCTION - three independent limits, each of them a property of this
    /// method rather than of the corpus that happens to be shipped today:</para>
    ///
    /// <para><b>1. Hebrew script only.</b> A token containing anything that is not a Hebrew letter
    /// (a Latin letter, a digit) has NO keys, so it can only ever be matched exactly, exactly as
    /// before. An English question therefore scores identically to before this change, against every
    /// document in either language, and a Hebrew question still reaches an English guide only through
    /// exact matching. Combined with the fact that <see cref="Score"/> applies this to HEADINGS ONLY
    /// and never to the frontmatter <c>id</c>/<c>stage</c> - which are English slugs on BOTH halves of
    /// an en/he pair, and are the mechanism by which an English question reaches the Hebrew twin at
    /// all - the cross-language behaviour g1 and g2 measured cannot move.</para>
    ///
    /// <para><b>2. At least <see cref="MinInflectionStemLength"/> letters, at most
    /// <see cref="MaxInflectionLettersRemoved"/> removed.</b> A key is the token with at most one
    /// leading letter from <see cref="HebrewPrefixLetters"/> and at most one ending from
    /// <see cref="HebrewSuffixes"/> removed, TOTALLING at most two letters, and never shorter than
    /// four. So two tokens can only match here if they agree on four or more consecutive letters and
    /// differ by at most two letters at their edges. A token of four letters or fewer is never
    /// stripped at all (every strip would fall below the floor), which is what stops this from
    /// colliding across the short stems Hebrew is full of: <c>הגהה</c> does not reach <c>הגה</c>,
    /// <c>שכבה</c> does not reach <c>כבה</c>, <c>הספר</c> does not reach <c>ספר</c>.</para>
    ///
    /// <para><b>3. Strictly additive.</b> <see cref="Score"/> tries the exact match FIRST and only
    /// falls back to this, and the fallback is worth less (<see cref="InflectedHeadingWeight"/> vs
    /// <see cref="HeadingWeight"/>). No document can lose score, and no pair of documents that both
    /// match a token exactly can change order relative to each other because of this.</para>
    ///
    /// <para>Deliberately NOT a stemmer. It does not know binyanim, so <c>לרוץ</c> still does not
    /// reach <c>מריצים</c> and <c>אפשרויות</c> still does not reach <c>אפשרות</c>. The general
    /// Hebrew-morphology gap is open by design; what is closed is the single-affix case, which is the
    /// one the measurements actually hit.</para>
    /// </summary>
    public static IReadOnlySet<string> InflectionKeys(string? token)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(token)) return keys;

        foreach (var c in token)
        {
            if (!ScriptTokenPredicates.IsHebrewLetter(c)) return keys;   // bound 1
        }

        // The suffixes are written in ordinary Hebrew orthography and END in a final form
        // (mem sofit in "ים"), so they are matched against the RAW token; the final-form folding is
        // applied to each resulting stem instead. Folding first would rewrite "מריצים" to "מריצימ"
        // and the suffix would no longer be there to strip - the exact way this method failed its
        // first measurement.
        var word = token;
        AddKey(keys, word, removed: 0);

        var hasPrefix = Array.IndexOf(HebrewPrefixLetters, word[0]) >= 0;
        if (hasPrefix) AddKey(keys, word[1..], removed: 1);

        foreach (var suffix in HebrewSuffixes)
        {
            if (word.Length <= suffix.Length || !word.EndsWith(suffix, StringComparison.Ordinal)) continue;

            AddKey(keys, word[..^suffix.Length], removed: suffix.Length);
            if (hasPrefix) AddKey(keys, word[1..^suffix.Length], removed: suffix.Length + 1);
        }

        return keys;
    }

    private static void AddKey(HashSet<string> keys, string stem, int removed)
    {
        if (removed <= MaxInflectionLettersRemoved && stem.Length >= MinInflectionStemLength)
            keys.Add(NormalizeFinalForms(stem));
    }

    /// <summary>
    /// Folds the five Hebrew final forms to their medial twins, so a word ending in one can be
    /// compared with the same word carrying a suffix (<c>מריץ</c> against <c>מריצים</c>). Final forms
    /// only ever occur word-finally, so this is a normalization rather than a conflation.
    /// </summary>
    private static string NormalizeFinalForms(string word)
    {
        Span<char> buffer = stackalloc char[word.Length];
        word.AsSpan().CopyTo(buffer);
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = buffer[i] switch
            {
                'ך' => 'כ',
                'ם' => 'מ',
                'ן' => 'נ',
                'ף' => 'פ',
                'ץ' => 'צ',
                _ => buffer[i]
            };
        }

        return new string(buffer);
    }

    private static bool IsSameLanguage(GuideDocument doc, string questionLanguage)
        => string.Equals(doc.Lang, questionLanguage, StringComparison.OrdinalIgnoreCase);
}
