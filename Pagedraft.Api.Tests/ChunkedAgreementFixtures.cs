using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Models;

// Bound through a using ALIAS, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes. Same rule (and same reason) as ProofreadAgreementGoldTests.
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// ChunkedAgreementFixtures — THE EXPERIMENT for the chunked-regime agreement attribution (c1).
//
// WHY IT EXISTS. The 2026-08-02 baseline measured character-agreement by composing the PRODUCTION
// per-chunk prompt (PromptFactory.BuildProofreadChunkPrompt) and then feeding ONE SENTENCE through
// the single-shot IAiRouter seam. Chunking never happened, so nothing in that measurement can speak
// about the regime the original failures came from: a full chapter through
// UnifiedAnalysisService.RunProofreadChunkedAsync. Four causes are consistent with "fails on a
// chapter, passes on a sentence" and they imply DIFFERENT fixes:
//
//   (a) dilution by context length      -> prompt weighting / position
//   (b) antecedent separated by chunking-> overlap or referent carry-forward
//   (c) register missing from a chunk   -> plumbing (settled outright by the prompt capture)
//   (d) long-context inattention        -> obligation rendering
//
// FIXTURES ARE THE EXPERIMENT, so the four below are designed to SEPARATE those, not merely to
// reproduce a failure. Every one carries the SAME error span, the SAME register and the SAME
// near-miss guard; only the REGIME moves:
//
//   chunked-agree-01-separated-and-diluted  3 chunks; name in chunk 0, error in the LAST chunk,
//                                           name NOT in that chunk's overlap  -> (a)+(b) together
//   chunked-agree-02-antecedent-in-overlap  2 chunks; error in chunk 1, name carried in by the
//                                           [CONTEXT_BEFORE] overlap           -> isolates (b)
//   chunked-agree-03-dilution-only          3 chunks; name AND error both in chunk 0, so separation
//                                           cannot apply                       -> isolates (a)
//   chunked-agree-04-single-chunk-control   one chunk; the saturated single-shot condition
//
// PREDICTIONS, stated in advance so g1 cannot rationalise after the fact:
//   separation  => 01 fails, 02 passes
//   dilution    => 03 fails even though its antecedent is local
//   plumbing    => the per-chunk prompt capture shows the register ABSENT (needs no inference)
//   inattention => what REMAINS when the register is present, the antecedent is local, and it fails
//
// -- THE LENGTH IS KEYED OFF THE *HEBREW* TARGET, NOT THE LATIN 500 --------------------------------
// The chunk threshold is LANGUAGE-KEYED: UnifiedAnalysisService.ProofreadChunkTargetWordsFor resolves
// the configured 500-word LATIN ceiling down to ~250 for Hebrew (the char/token density ratio), and
// GET /api/config/analysis-chunk-thresholds must mirror it. Sizing these fixtures against 500 would
// silently collapse every one of them to a SINGLE chunk and the whole experiment would measure
// nothing while passing. ChunkedAgreementFixtureTests asserts the realized chunk count MODEL-FREE by
// driving the real chunker, and fails if any multi-chunk fixture collapses.
//
// -- THE FILLER IS DELIBERATELY IMPERSONAL --------------------------------------------------------
// Every filler paragraph describes a place or an object. It names no person, marks no gender and
// contains no speech verb, so the ONLY gender-bearing evidence anywhere in a fixture is (1) the
// character register block and (2) the erroneous span itself. A filler sentence that agreed with a
// person would hand the model a textual gender cue and destroy the register-only property the whole
// class rests on. It also carries no gershayim (U+05F4) and no ASCII quote, so the punctuation tax
// this plan's OTHER item measures cannot leak into this item's over-correction count.
//
// -- SURFACE TAGGING (PROMOTED BY c3) -------------------------------------------------------------
// proofread-gold.json partitions non-comparable prompt surfaces (LanguageEngine/GoldPromptSurfaces.cs).
// These fixtures ride the per-chunk one. c1 tagged them with a SEPARATE ChunkedAgreementSurface enum
// because GoldPromptSurfaces.Split() then bucketed only TWO surfaces, so a third-surface record would
// have fallen out of both subsets and been counted only in the mixed ALL block - exactly the silent
// mixing the split exists to prevent. c3 widened GoldPromptSurface and Split to THREE buckets (and made
// Split THROW on an unbucketed record), so that duplicate enum is retired and these fixtures now carry
// the SAME surface vocabulary as the gold corpus. They still live in this file rather than in
// proofread-gold.json: a chunked case is a multi-chunk CHAPTER driven through RunAsync, not a
// HebrewRegressionCase with an input and expectedCorrections, and BuildGoldRequest cannot compose a
// per-chunk request for one. See ProofreadStandingFloorTests for the promotion's gates.
//
// AUTHORING PROVENANCE (honesty note). The agreement span is DERIVED from the shipped gold entry
// agree-register-02 ("רוני לא הגיב." -> "רוני לא הגיבה.", near-miss "הגיב" -> "מגיב", register
// רוני[female]) - the register-only probe that the baseline measured as saturated. This corpus keeps
// that verb, that register and that near-miss shape and only re-anchors the subject on the pronoun
// (הוא -> היא) so the erroneous sentence can stand in a chunk that does NOT contain the name, which
// is the separation condition. The surrounding prose is AUTHORED, not lifted from the cleared eval
// manuscript, and is on the same pending native-speaker review list as the authored gold notes.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One chunked-regime agreement case. Everything a per-case verdict needs is declared here, so g1
/// reports a MATRIX (per fixture x per chunk) rather than a subset mean.
/// </summary>
/// <param name="Id">Stable id, <c>chunked-agree-*</c> to mirror the gold's <c>agree-*</c> class.</param>
/// <param name="Hypothesis">Which of the four causes this fixture separates, and how.</param>
/// <param name="Language">Analysis language ("he-IL"); drives the language-keyed chunk target.</param>
/// <param name="Register">The character register injected as <c>[CHARACTER_REGISTER]</c>.</param>
/// <param name="CharacterName">The register name whose gender is the ONLY evidence for the fix.</param>
/// <param name="ErrorSpan">The erroneous span, verbatim, occurring EXACTLY ONCE in <paramref name="Text"/>.</param>
/// <param name="ExpectedFix">The correct replacement for <paramref name="ErrorSpan"/>.</param>
/// <param name="NearMissForbidden">
/// The plausible WRONG form at the same span. Without it a right-span/wrong-form edit scores as a
/// recall HIT under span-only matching - the trap TestData/README.md documents for the gold class.
/// </param>
/// <param name="Surface">Which prompt surface this case is measured on.</param>
/// <param name="ExpectedChunkCount">Realized chunk count, asserted model-free against the real chunker.</param>
/// <param name="ExpectedErrorChunkIndex">Index of the chunk whose TEXT contains <paramref name="ErrorSpan"/>.</param>
/// <param name="ExpectedNameChunkIndexes">Indexes of every chunk whose TEXT contains <paramref name="CharacterName"/>.</param>
/// <param name="NameExpectedInErrorChunkOverlap">
/// Whether the error chunk's <c>OverlapPrefix</c> (rendered as <c>[CONTEXT_BEFORE]</c>) carries the
/// character name. THIS is the separation axis: false = the composed prompt for that chunk contains
/// no occurrence of the name at all.
/// </param>
/// <param name="Text">The seeded chapter.</param>
/// <param name="Note">Authoring note: axes, provenance, and what a failure here would mean.</param>
public sealed record ChunkedAgreementFixture(
    string Id,
    string Hypothesis,
    string Language,
    CharacterRegisterEntry[] Register,
    string CharacterName,
    string ErrorSpan,
    string ExpectedFix,
    string NearMissForbidden,
    GoldPromptSurface Surface,
    int ExpectedChunkCount,
    int ExpectedErrorChunkIndex,
    int[] ExpectedNameChunkIndexes,
    bool NameExpectedInErrorChunkOverlap,
    string Text,
    string Note)
{
    /// <summary>The corrected text this fixture's ONE expected fix produces (g1 scores against it).</summary>
    public string ExpectedCorrectedText => Text.Replace(ErrorSpan, ExpectedFix, StringComparison.Ordinal);
}

/// <summary>
/// The four fixtures, the impersonal filler they are built from, and the two seeded sentences that
/// carry the whole experiment. See the file header for the design.
/// </summary>
public static class ChunkedAgreementFixtures
{
    /// <summary>Analysis language of every fixture. Locale variant of "he"; the chunk sizer collapses it.</summary>
    public const string Language = "he-IL";

    /// <summary>The register-only character. Unisex Hebrew name: nothing in the prose marks its gender.</summary>
    public const string CharacterName = "רוני";

    /// <summary>
    /// The ANTECEDENT sentence: it names the character and marks NO gender (the verb agrees with
    /// "כולם", not with רוני), so the register stays the only gender evidence in the fixture.
    /// </summary>
    public const string AntecedentSentence = "כל הערב חיכו במשרד לרוני.";

    /// <summary>
    /// The ERRONEOUS sentence. Internally consistent masculine, therefore flawless Hebrew unless the
    /// reader knows both that the referent is רוני and that רוני is female.
    /// </summary>
    public const string ErroneousSentence = "הוא לא הגיב לאיש עד סוף הערב.";

    /// <summary>The erroneous span (multi-word for forbidden-span distinctiveness, per the gold rule).</summary>
    public const string ErrorSpan = "הוא לא הגיב";

    /// <summary>The correct form.</summary>
    public const string ExpectedFix = "היא לא הגיבה";

    /// <summary>
    /// The near-miss: present tense, gender STILL masculine. Same shape as agree-register-02's
    /// "הגיב" -> "מגיב". A model that edits the right span into this has NOT repaired the agreement.
    /// </summary>
    public const string NearMissForbidden = "הוא לא מגיב";

    /// <summary>The register block every fixture injects.</summary>
    public static CharacterRegisterEntry[] Register() => new[]
    {
        new CharacterRegisterEntry { Name = CharacterName, Gender = "female" }
    };

    /// <summary>
    /// IMPERSONAL filler paragraphs (see the file header for why they must stay impersonal). No
    /// person, no gendered reference, no speech verb, no quote character of any kind. Each is a
    /// single paragraph; fixtures join them with a blank line, which is what the chunker segments on.
    /// </summary>
    public static readonly IReadOnlyList<string> Filler = new[]
    {
        // 1
        "בניין המשרדים בקצה הרחוב נראה שקט מן החוץ, אך בפנים נמשכה העבודה עד שעה מאוחרת. " +
        "מנורות הפלואורסנט האירו את המסדרון הארוך באור קר ואחיד. " +
        "מן החלונות הגבוהים אפשר היה לראות את אורות העיר נמתחים עד קו האופק.",
        // 2
        "המעלית הישנה עצרה בכל קומה בקול חריקה קצרה, ואחר כך המשיכה בדרכה כלפי מעלה. " +
        "שטיח כחול דהוי כיסה את הרצפה מן הדלת ועד לפינת ההמתנה. " +
        "על הקיר ממול נתלה לוח מודעות ובו כמה דפים ישנים מן החודשים הקודמים.",
        // 3
        "בחדר הישיבות עמד שולחן עץ ארוך ועליו כוסות מים שאיש לא נגע בהן. " +
        "הווילונות הכבדים היו מוסטים הצידה, והאור מבחוץ נשפך פנימה בפסים דקים. " +
        "מזגן ישן פעל ברקע והשמיע רעש מונוטוני שנמשך שעות ארוכות.",
        // 4
        "במטבחון הקטן שבקצה הקומה עמד מיחם חשמלי שכבה מזמן. " +
        "ריח קפה קלוש עוד נותר באוויר, מעורב בריח של חומר ניקוי חריף. " +
        "מדף עליון החזיק ערימת ספלים לבנים, וחלקם היו סדוקים בשוליים.",
        // 5
        "הרחוב שמתחת התרוקן בהדרגה משעה שמונה ואילך. " +
        "מוניות בודדות חלפו לאורך הכביש הרטוב והשאירו אחריהן קווי אור ארוכים. " +
        "חנות הפרחים שבפינה כיבתה את השלט המואר שלה והורידה את התריס.",
        // 6
        "גשם דק החל לרדת סמוך לחצות והספיק להרטיב את המדרכות. " +
        "מרזבי הפח הישנים העבירו את המים אל תוך תעלות הניקוז שבצד הכביש. " +
        "הרוח נשבה בין העצים והעלים רשרשו בשקט לאורך כל השדרה.",
        // 7 — the LAST paragraph of chunk 0 at the shipped Hebrew target (calibrated, see the
        // realized-count test). It is the office paragraph on purpose: fixture 02 appends the
        // antecedent here, so the antecedent reads in context AND lands in the overlap window.
        "משרד ההנהלה שבקומה השלישית היה סגור מאז שעות אחר הצהריים. " +
        "שלט קטן על הדלת ביקש לא להפריע עד להודעה חדשה. " +
        "מבעד לזכוכית החלבית נראה אור עמום שנותר דולק שם בטעות.",
        // 8
        "חדר השרתים נשמר בטמפרטורה קבועה לאורך כל שעות היממה. " +
        "נוריות ירוקות הבהבו בקצב אחיד על גבי המדפים המתכתיים. " +
        "כבלים כחולים נמתחו מקיר אל קיר בסדר מופתי שאיש לא הפר.",
        // 9
        "במסדרון הצדדי נתלו תצלומים ישנים של הבניין בשנות הקמתו. " +
        "מסגרות עץ פשוטות הקיפו אותם, וזכוכית מאובקת כיסתה את הדפים המצהיבים. " +
        "מתחת לכל תצלום הודבקה כתובית קצרה בכתב יד ברור.",
        // 10
        "חדר הצילום שבסוף המסדרון הכיל מכונה גדולה ומיושנת. " +
        "מגש הנייר שלה היה מלא עד סופו, ומחוון קטן הראה שהדיו עומדת להיגמר. " +
        "ערימת דפים מודפסים נחה על השולחן שלצידה בלי שאיש אסף אותה.",
        // 11
        "במחסן שבקומת הקרקע נערמו ארגזי קרטון עד לתקרה הנמוכה. " +
        "תוויות דהויות נדבקו על דופנותיהם וציינו תאריכים משנים קודמות. " +
        "נורה בודדת השתלשלה מן התקרה והאירה רק חלק קטן מן החלל.",
        // 12
        "הכניסה הראשית נשמרה על ידי דלת מסתובבת שהאטה את המעבר פנימה והחוצה. " +
        "מזרן גומי שחור הונח לפניה כדי לספוג את המים מן הנעליים. " +
        "מצלמת אבטחה קטנה נתלתה בפינה וסקרה את הלובי כולו.",
        // 13
        "החניון התת קרקעי היה כמעט ריק בשעה הזאת. " +
        "שתי מכוניות בלבד עמדו בקצה הרחוק ליד עמוד הבטון המסומן. " +
        "תאורת החירום דלקה באור צהוב חלש לאורך כל המעבר.",
        // 14
        "גרם המדרגות האחורי הוביל מן החניון ועד לגג הבניין. " +
        "מעקה מתכת קר נמתח לאורכו, וכל שלב סומן בפס זרחני דק. " +
        "הד הצעדים חזר בין הקירות ונשמע חזק הרבה יותר מן המקור.",
        // 15
        "על הגג הותקנו מזגנים תעשייתיים ששמרו על קצב עבודה קבוע. " +
        "צינורות מבודדים התפתלו ביניהם עד לפתח האוורור המרכזי. " +
        "מן הקצה הצפוני נשקף מגדל המים הישן של השכונה כולה.",
        // 16
        "משרד הקבלה שבקומת הכניסה החזיק יומן מבקרים עבה בכריכה שחורה. " +
        "עט כחול היה קשור אליו בחוט דק כדי שלא יאבד. " +
        "מנורת שולחן קטנה הוסיפה אור חמים לפינה אחת בלבד.",
        // 17
        "פינת ההמתנה כללה שתי כורסאות בד ושולחן נמוך ביניהן. " +
        "עיתונים משבוע שעבר נערמו על השולחן בסדר לא מוקפד. " +
        "עציץ ירוק גדול עמד בצד וקיבל מעט מאוד אור לאורך היום.",
        // 18
        "שעון הקיר שמעל הדלת הראה שעה מאוחרת מן הרגיל. " +
        "המחוג השני נע בקצב אחיד והשמיע תקתוק חלש בחדר הריק. " +
        "מחוץ לחלון פסק הגשם והרחוב נותר רטוב ומבהיק.",
        // 19
        "ארון התיקים שבקצה החדר ננעל במפתח קטן שנשמר במגירה. " +
        "תוויות נייר לבנות סימנו את השנים על גבי המדפים. " +
        "אבק דק כיסה את החלק העליון של הארון ואיש לא ניגב אותו.",
        // 20
        "לוח המחוונים שליד המעלית הראה את מספר הקומה באור אדום. " +
        "כפתור הקריאה נשחק מרוב שימוש ואיבד את הצבע המקורי. " +
        "שלט קטן לצידו הזכיר את מספר הטלפון של מוקד התחזוקה.",
        // 21
        "מאחורי הבניין השתרע מגרש חניה קטן ובו סימוני צבע דהויים. " +
        "גדר רשת נמוכה הפרידה בינו לבין השביל הציבורי. " +
        "פנס יחיד האיר את הפינה הצפונית לאורך כל שעות הלילה.",
        // 22
        "מערכת הכריזה בקומות לא הופעלה מאז הבדיקה האחרונה. " +
        "רמקולים עגולים הותקנו בתקרה במרווחים קבועים לאורך המסדרון. " +
        "חוט דק נמתח ביניהם והוסתר מאחורי לוחות הגבס.",
    };

    /// <summary>Paragraph separator. A blank line is what <c>BuildChunkSegmentsCore</c> segments on.</summary>
    public const string ParagraphSeparator = "\n\n";

    /// <summary>
    /// Compose a body from <paramref name="paragraphCount"/> filler paragraphs, optionally appending
    /// a sentence to one paragraph and prepending one to another. Each operation is a single
    /// (index, text) pair rather than two separately-defaulted parameters, so an index without its
    /// text (or vice versa) cannot be expressed - there is no default-index trap to fall into. The
    /// fixture tests pin which CHUNK each of them lands in, which is the only claim that matters and
    /// the only one a paragraph-count change can break silently.
    /// </summary>
    private static string Compose(
        int paragraphCount,
        (int Index, string Text)? append = null,
        (int Index, string Text)? prepend = null)
    {
        if (paragraphCount > Filler.Count)
            throw new ArgumentOutOfRangeException(nameof(paragraphCount),
                $"only {Filler.Count} filler paragraphs are authored");

        var paragraphs = Filler.Take(paragraphCount).ToArray();

        // Pairing the index with its text removed the default-index trap, but the index itself is still
        // an array subscript: a paragraph count reduced below a seed's index would otherwise surface as
        // a bare IndexOutOfRangeException from a static initializer, which reads as a corrupt type
        // rather than as a fixture that no longer has the paragraph it seeds.
        void Require(string which, int index)
        {
            if (index < 0 || index >= paragraphs.Length)
                throw new ArgumentOutOfRangeException(nameof(paragraphCount), index,
                    $"the {which} seed targets paragraph {index}, but this fixture composes only " +
                    $"{paragraphs.Length} paragraph(s). Raise paragraphCount or move the seed - the " +
                    "fixture tests pin which CHUNK each seed lands in, so the two move together.");
        }

        if (append is { } a)
        {
            Require("append", a.Index);
            paragraphs[a.Index] = paragraphs[a.Index] + " " + a.Text;
        }
        if (prepend is { } p)
        {
            Require("prepend", p.Index);
            paragraphs[p.Index] = p.Text + " " + paragraphs[p.Index];
        }

        return string.Join(ParagraphSeparator, paragraphs);
    }

    // ── the four fixtures ────────────────────────────────────────────────────────────────────────

    /// <summary>Fixture id constants, so tests and (later) g1's report select without string literals.</summary>
    public const string SeparatedAndDilutedId = "chunked-agree-01-separated-and-diluted";
    public const string AntecedentInOverlapId = "chunked-agree-02-antecedent-in-overlap";
    public const string DilutionOnlyId = "chunked-agree-03-dilution-only";
    public const string SingleChunkControlId = "chunked-agree-04-single-chunk-control";

    /// <summary>
    /// The corpus. ORDER IS THE EXPERIMENT'S ORDER, not alphabetical: 01/02 are the separation A/B,
    /// 03 is the dilution arm, 04 is the control that has to reproduce the saturated baseline before
    /// any of the other three numbers may be read.
    /// </summary>
    public static readonly IReadOnlyList<ChunkedAgreementFixture> All = new[]
    {
        new ChunkedAgreementFixture(
            Id: SeparatedAndDilutedId,
            Hypothesis:
                "DILUTION + SEPARATION together. The name appears ONLY in chunk 0; the error sits in the " +
                "LAST chunk, whose [CONTEXT_BEFORE] overlap carries impersonal filler. The register maps " +
                "NAME -> gender, so with no occurrence of the name anywhere in that chunk's composed prompt " +
                "the register is present but INAPPLICABLE. A failure here is consistent with both causes; " +
                "it is fixture 02 that tells them apart.",
            Language: Language,
            Register: Register(),
            CharacterName: CharacterName,
            ErrorSpan: ErrorSpan,
            ExpectedFix: ExpectedFix,
            NearMissForbidden: NearMissForbidden,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 3,
            ExpectedErrorChunkIndex: 2,
            ExpectedNameChunkIndexes: new[] { 0 },
            NameExpectedInErrorChunkOverlap: false,
            Text: Compose(22, append: (0, AntecedentSentence),
                          prepend: (15, ErroneousSentence)),
            Note:
                "AXES: pronoun + past-tense verb / chunk-INITIAL in the last chunk / female referent with a " +
                "masculine pronoun and verb / attribute source = REGISTER-ONLY / antecedent OUT of chunk and " +
                "OUT of overlap. Derived from gold agree-register-02 (see the file header). A PASS here " +
                "would be surprising and would rule out separation as a cause on its own."),

        new ChunkedAgreementFixture(
            Id: AntecedentInOverlapId,
            Hypothesis:
                "ISOLATES SEPARATION. Identical error sentence, identical register, but the antecedent sits " +
                "in the last sentences of chunk 0 and is therefore carried into chunk 1's prompt as the " +
                "[CONTEXT_BEFORE] overlap. If 01 fails and this passes, the cause is antecedent separation " +
                "and the indicated fix is overlap / referent carry-forward - NOT the obligation rendering " +
                "Wave 2 currently plans to build first.",
            Language: Language,
            Register: Register(),
            CharacterName: CharacterName,
            ErrorSpan: ErrorSpan,
            ExpectedFix: ExpectedFix,
            NearMissForbidden: NearMissForbidden,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 2,
            ExpectedErrorChunkIndex: 1,
            ExpectedNameChunkIndexes: new[] { 0 },
            NameExpectedInErrorChunkOverlap: true,
            Text: Compose(14, append: (6, AntecedentSentence),
                          prepend: (7, ErroneousSentence)),
            Note:
                "AXES: as 01, except the antecedent is INSIDE the overlap window (ExtractTrailingSentences " +
                "takes the last 3 sentences of the previous chunk, so the antecedent is authored as the LAST " +
                "sentence of the last paragraph of chunk 0). The name still never occurs in the error " +
                "chunk's own TEXT, so this measures the overlap channel specifically."),

        new ChunkedAgreementFixture(
            Id: DilutionOnlyId,
            Hypothesis:
                "ISOLATES DILUTION / LENGTH. Same 3-chunk body as 01, but the antecedent AND the error are " +
                "both in chunk 0, so separation cannot apply and no overlap is involved (chunk 0 has none). " +
                "The only thing that differs from the saturated single-shot baseline is that the model reads " +
                "a full ~250-word chunk instead of one sentence. A failure here indicts context length; a " +
                "pass here alongside a failure in 01 points at separation.",
            Language: Language,
            Register: Register(),
            CharacterName: CharacterName,
            ErrorSpan: ErrorSpan,
            ExpectedFix: ExpectedFix,
            NearMissForbidden: NearMissForbidden,
            Surface: GoldPromptSurface.ChunkedPerChunk,
            ExpectedChunkCount: 3,
            ExpectedErrorChunkIndex: 0,
            ExpectedNameChunkIndexes: new[] { 0 },
            NameExpectedInErrorChunkOverlap: false,
            Text: Compose(22, append: (0, AntecedentSentence + " " + ErroneousSentence)),
            Note:
                "AXES: as 01, except antecedent and error are ADJACENT and both in the FIRST chunk. " +
                "NameExpectedInErrorChunkOverlap is false here for a different reason than in 01: chunk 0 " +
                "has NO overlap prefix at all (the chunker only builds one for i > 0), not because the " +
                "overlap failed to carry the name. The run is still 3 chunks long, so the ONLY property " +
                "this fixture drops relative to 01 is the separation."),

        new ChunkedAgreementFixture(
            Id: SingleChunkControlId,
            Hypothesis:
                "THE CONTROL. One chunk, the saturated condition. It MUST reproduce the baseline's 5/5 pass; " +
                "if it does not, the harness is wrong and every other number in the run is void. NOTE the " +
                "surface: a one-chunk run is NOT reachable through the chunked path for non-dialogue text " +
                "(see ChunkedAgreementFixtureTests.AOneChunkChunkedRun_IsUnreachable...), so this rides the " +
                "single-shot path - whose composed instruction is byte-identical to a first chunk's when the " +
                "context carries only Characters. That identity is pinned, not assumed.",
            Language: Language,
            Register: Register(),
            CharacterName: CharacterName,
            ErrorSpan: ErrorSpan,
            ExpectedFix: ExpectedFix,
            NearMissForbidden: NearMissForbidden,
            Surface: GoldPromptSurface.ProductionLongPlusShort,
            ExpectedChunkCount: 1,
            ExpectedErrorChunkIndex: 0,
            ExpectedNameChunkIndexes: new[] { 0 },
            NameExpectedInErrorChunkOverlap: false,
            Text: Compose(1, append: (0, AntecedentSentence + " " + ErroneousSentence)),
            Note:
                "AXES: as 03 but WITHOUT the length. Same paragraph, same antecedent, same error, same " +
                "register; the body is one filler paragraph so the word count stays under the Hebrew chunk " +
                "target and RunAsync routes single-shot."),
    };

    /// <summary>
    /// The fixtures tagged with the chunked-per-chunk prompt surface. These are the ones meant to
    /// realize MORE THAN ONE chunk (the windowed-density trap), but that property is a consequence of
    /// how each fixture composes its text, NOT of this selector - the selector keys on
    /// <see cref="ChunkedAgreementFixture.Surface"/>, so the more-than-one-chunk claim is asserted
    /// separately (see <c>EveryFixture_DeclaresItsSurface_AndNoFixtureIdIsAlreadyInTheGoldCorpus</c>).
    /// </summary>
    public static IReadOnlyList<ChunkedAgreementFixture> MultiChunk =>
        All.Where(f => f.Surface == GoldPromptSurface.ChunkedPerChunk).ToArray();

    /// <summary>The control.</summary>
    public static ChunkedAgreementFixture Control =>
        All.Single(f => f.Id == SingleChunkControlId);

    /// <summary>Look a fixture up by id (throws on an unknown id rather than returning null).</summary>
    public static ChunkedAgreementFixture ById(string id) =>
        All.Single(f => string.Equals(f.Id, id, StringComparison.Ordinal));
}
