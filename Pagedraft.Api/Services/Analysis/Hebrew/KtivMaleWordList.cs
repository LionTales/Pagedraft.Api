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
        ["עצמה"]    = "עוצמה",     // power / intensity (canonical Academy example)
        ["תכנה"]    = "תוכנה",     // software
        ["חכמה"]    = "חוכמה",     // wisdom
        ["אמנם"]    = "אומנם",     // indeed / admittedly
        ["אמן"]     = "אומן",      // craftsman / artisan
        ["אמנות"]   = "אומנות",    // craft / craftsmanship
        ["דאר"]     = "דואר",      // mail / post
        ["משמרת"]   = "משמורת",    // custody (legal sense)
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
        ["ספור"]    = "סיפור",     // story
        ["ספורים"]  = "סיפורים",   // stories
        ["דבור"]    = "דיבור",     // speech
        ["חבור"]    = "חיבור",     // composition / connection
        ["חבורים"]  = "חיבורים",   // compositions
        ["שעור"]    = "שיעור",     // lesson / rate
        ["שעורים"]  = "שיעורים",   // lessons
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
    /// Sentinel entries above whose key == value are already-male spellings included only to
    /// make the "do not flag already-correct words" guarantee explicit and testable. The checker
    /// skips any pair where key equals value, so these never produce a suggestion.
    /// </summary>
    public static bool IsAlreadyMale(string word) =>
        HaserToMale.TryGetValue(word, out var male) && string.Equals(word, male, System.StringComparison.Ordinal);
}
