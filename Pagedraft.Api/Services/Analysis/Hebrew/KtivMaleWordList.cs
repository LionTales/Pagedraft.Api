using System.Collections.Generic;

namespace Pagedraft.Api.Services.Analysis.Hebrew;

/// <summary>
/// Curated seed word-list for Hebrew ktiv-male (כתיב מלא, "full spelling") enforcement.
///
/// Rule source: the Academy of the Hebrew Language's 2017 ktiv-male (כללי הכתיב חסר הניקוד)
/// rules - see https://hebrew-academy.org.il (the rules add a vav ו for the /o/ and /u/ sounds
/// and a yod י for the /i/ and /e/ sounds, on CLOSED, ENUMERABLE word lists).
///
/// SCOPE / HONESTY NOTE: this is a *seed* list of the most common, unambiguous haser→male
/// corrections, NOT full Academy coverage. The Academy's complete data is large and not
/// shipped here as a machine-readable resource (no licensable embeddable source was found),
/// so we deliberately scope this to high-confidence, frequently-occurring closed-list pairs
/// plus a handful of well-known prefixed/declined forms. It is intentionally EXTENSIBLE:
/// add pairs below as they are verified. Each entry maps a defective (haser) spelling to its
/// normative full (male) spelling.
///
/// CONSERVATISM: only forms whose male spelling is unambiguous and not also a legitimate
/// distinct word are included. We do NOT add open-ended rule guessing here - entries are a
/// vetted lookup so the checker never "corrects" an intentional/colloquial spelling that is
/// not on the list (see KtivMaleChecker for the human-in-the-loop, suggestion-only contract).
/// </summary>
public static class KtivMaleWordList
{
    /// <summary>
    /// Closed haser→male lookup. Key = defective spelling (without niqqud), Value = full spelling.
    /// Keys are matched as whole standalone words by <see cref="KtivMaleChecker"/>; common Hebrew
    /// one-letter prefixes (ו, ה, ב, כ, ל, מ, ש) are handled by the checker, so list only base forms.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> HaserToMale = new Dictionary<string, string>
    {
        // ── vav (ו) for the /o/ and /u/ sounds ──────────────────────────
        ["תכנית"]   = "תוכנית",    // plan / program (canonical Academy example)
        ["תכניות"]  = "תוכניות",   // plans (plural)
        // עצמה, חכמה, אמן, אמנות, משמרת intentionally NOT listed here - they are AMBIGUOUS
        // HOMOGRAPHS whose haser (short) form is itself a common independent word with a DIFFERENT
        // meaning, so a context-blind checker cannot safely flag them. See
        // AmbiguousHomographPairsExcluded below for the pairs and the rationale.
        ["תכנה"]    = "תוכנה",     // software (haser תכנה collides only with the rare verb "she planned"; kept - low FP risk)
        ["אמנם"]    = "אומנם",     // indeed / admittedly
        ["דאר"]     = "דואר",      // mail / post
        ["כתנת"]    = "כותנת",     // tunic / shirt

        // ── yod (י) for the /i/ and /e/ sounds ──────────────────────────
        ["אמתי"]    = "אמיתי",     // real / true (canonical Academy example)
        ["אמתית"]   = "אמיתית",    // real / true (feminine)
        ["אמתיים"]  = "אמיתיים",   // real / true (masc. plural)
        ["אמתיות"]  = "אמיתיות",   // real / true (fem. plural)
        ["עתים"]    = "עיתים",     // times (as in לעיתים - at times)
        ["עתון"]    = "עיתון",     // newspaper
        ["עתונאי"]  = "עיתונאי",   // journalist
        ["עתונות"]  = "עיתונות",   // the press / journalism
        ["מאוזן"]   = "מאוזן",     // balanced (already male - guard sentinel, never flagged)
        ["דיוק"]    = "דיוק",      // accuracy (already male - guard sentinel, never flagged)
        ["גרסה"]    = "גרסה",      // version (already male - guard sentinel, never flagged)
        ["נסיון"]   = "ניסיון",    // experience / attempt
        ["נסיונות"] = "ניסיונות",  // experiences / attempts
        ["בטחון"]   = "ביטחון",    // security / confidence
        // SHVA-NACH EXCEPTION (Academy-cited): when a letter carrying a shva nach follows the /i/
        // vowel, the /i/ is NOT marked with a yod. דמיון and צמצום are normative ktiv-male AS-IS
        // (no added yod) - דמיון is the Academy's own cited example. They are kept as already-male
        // sentinels (key == value) so the checker can NEVER flag them as "should be דימיון/צימצום".
        // Do NOT re-add a דמיון→דימיון or צמצום→צימצום pair: that would tell the author to introduce
        // an error. See https://hebrew-academy.org.il.
        ["דמיון"]   = "דמיון",     // imagination / resemblance (already male per shva-nach rule - never flag)
        // NOTE: גלוי→גילוי intentionally NOT listed - see homograph note below. גָּלוּי (adjective:
        // "visible/open/revealed", שם תואר) is a very common word and is NOT a haser form of the
        // noun גילוי ("revelation/discovery"); suggesting the change would be a meaning-changing
        // miscorrection on ordinary prose, so this high-frequency homograph is excluded by design.
        ["צמצום"]   = "צמצום",     // reduction (already male per shva-nach rule - never flag)
        ["ספור"]    = "סיפור",     // story (haser ספור collides only with the rare passive participle "counted"; kept - low FP risk)
        // ספורים intentionally NOT listed - it is an AMBIGUOUS HOMOGRAPH: סְפוּרִים / "numbered, few"
        // (the very common idiom ימים ספורים = "a few / numbered days") is a distinct everyday word,
        // not the haser of סיפורים "stories". See AmbiguousHomographPairsExcluded below.
        ["דבור"]    = "דיבור",     // speech (haser דבור collides only with the rare passive participle "spoken"; kept - low FP risk)
        ["חבור"]    = "חיבור",     // composition / connection (haser חבור collides only with the rarer passive participle "connected"; kept - low FP risk)
        ["חבורים"]  = "חיבורים",   // compositions
        ["שעור"]    = "שיעור",     // lesson / rate (haser שעור is not a common distinct word; kept)
        // שעורים intentionally NOT listed - it is an AMBIGUOUS HOMOGRAPH: שְׂעוֹרִים / "barley" is a
        // common everyday noun, not the haser of שיעורים "lessons". See AmbiguousHomographPairsExcluded.
        ["צור"]     = "ציור",      // drawing (noun) - note: distinct from צוּר "rock"; see note below
        ["מלון"]    = "מילון",     // dictionary
        ["גבור"]    = "גיבור",     // hero
        ["גבורים"]  = "גיבורים",   // heroes
    };

    // NOTE on ambiguous keys (e.g. "צור", "מלון", "גלוי"): a few defective forms collide with a
    // legitimate distinct word (צוּר "rock"; מָלוֹן "hotel/lodging"; גָּלוּי "visible/open/revealed",
    // a שם תואר). Because PageDraft is human-in-the-loop and only SURFACES a suggestion (never
    // auto-fixes), surfacing the lower-frequency collisions (צור, מלון) is acceptable - the author
    // accepts or rejects. גלוי is DELIBERATELY EXCLUDED from the list above (no גלוי→גילוי pair):
    // the adjective גָּלוּי is very common in ordinary prose, so its practical false-positive rate is
    // materially higher than צור/מלון, and suggesting the unrelated noun גילוי would be a
    // meaning-changing miscorrection. If false-positive noise from צור/מלון proves annoying in
    // practice, move them to a separate "low-confidence" tier rather than deleting the whole list.
    // They are kept here so the seed demonstrates both vav and yod families.

    /// <summary>
    /// Haser→male pairs DELIBERATELY EXCLUDED from the active <see cref="HaserToMale"/> auto-flag list
    /// because the KEY (the haser / short spelling) is itself a COMMON independent Hebrew word with a
    /// DIFFERENT meaning than the VALUE (the male / full spelling). These are AMBIGUOUS HOMOGRAPHS.
    ///
    /// WHY excluded, not listed: <see cref="KtivMaleChecker"/> is CONTEXT-BLIND - it flags every
    /// whole-word occurrence of a key (optionally behind one prefix letter) with no ability to tell
    /// which sense the author meant. For these words the short form is, in ordinary prose,
    /// OVERWHELMINGLY the OTHER (valid, on-purpose) word - so auto-flagging them would deterministically
    /// produce a MEANING-CHANGING wrong suggestion, violating the checker's conservative
    /// "never touch an intentional spelling" contract. A ktiv-male auto-flag is only safe when the haser
    /// form is NOT also a frequent standalone word with an unrelated sense.
    ///
    /// This set is kept (a) so the knowledge is preserved and auditable rather than silently dropped,
    /// and (b) so a test can assert these keys are never flagged. It is NOT consulted by the checker
    /// (an entry simply absent from <see cref="HaserToMale"/> is already never flagged); it exists to
    /// DOCUMENT the exclusions. Do NOT move any of these back into HaserToMale without a
    /// context-aware disambiguation step (POS / surrounding words), which this deterministic checker
    /// does not have.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AmbiguousHomographPairsExcluded = new Dictionary<string, string>
    {
        // KEY = common standalone word (its usual sense) ; VALUE = the male spelling we would WRONGLY
        // suggest ; trailing comment = the everyday meaning of the KEY that makes the flag unsafe.
        ["עצמה"]   = "עוצמה",   // עַצְמָהּ = "herself / itself / its own / by itself" (reflexive/possessive) - hugely common; עוצמה = "power/intensity". The confirmed root-cause miscorrection.
        ["חכמה"]   = "חוכמה",   // חֲכָמָה = "wise" (feminine adjective, e.g. אישה חכמה "a wise woman") - very common; חוכמה = "wisdom".
        ["אמן"]    = "אומן",    // אָמָּן = "artist" and אָמֵן = "amen" - both common; אומן = "craftsman/artisan". Different words.
        ["אמנות"]  = "אומנות",  // אָמָּנוּת = "art" (painting/music/the arts) - very common; אומנות = "craftsmanship/trade". Different words.
        ["משמרת"]  = "משמורת",  // מִשְׁמֶרֶת = "(work) shift / watch" - very common; משמורת = "custody" (legal). Different words.
        ["ספורים"] = "סיפורים", // סְפוּרִים = "numbered / few" (idiom ימים ספורים "a few days") - common; סיפורים = "stories".
        ["שעורים"] = "שיעורים", // שְׂעוֹרִים = "barley" - common everyday noun; שיעורים = "lessons".
    };

    /// <summary>
    /// Sentinel entries above whose key == value are already-male spellings included only to
    /// make the "do not flag already-correct words" guarantee explicit and testable. The checker
    /// skips any pair where key equals value, so these never produce a suggestion.
    /// </summary>
    public static bool IsAlreadyMale(string word) =>
        HaserToMale.TryGetValue(word, out var male) && string.Equals(word, male, System.StringComparison.Ordinal);
}
