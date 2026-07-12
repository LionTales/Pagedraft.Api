using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Tests.LanguageEngine;

// ---------------------------------------------------------------------------
// PreservationFixtureBooks — the d6 PRESERVATION gate's two synthetic books, and the LEGIT-TERM
// fixture that runs against them (be-c07).
//
// WHY THIS EXISTS. The d6 gate (OutputQualityDiagnostic.MeasureLegitimateTermPreservation_LocalVsCloud)
// is the false-positive gate whose PASS justified flipping the production default
// Ai:AnalysisRepair.Mode to GlossaryThenDynamic. Until be-c07 it HAND-AUTHORED its per-book entity
// sets as literal HashSets and described them as "exactly what a deterministic BookEntityProvider
// WOULD surface". That was an ASSUMPTION, not a measurement: the harness never constructed
// BookEntityProvider, never threaded a bookId, and never touched the DB path that ships — so the two
// things the feature actually added (the SCRIPT-AWARE harvest and the bookId threading) were precisely
// the parts the gate did NOT measure.
//
// Now the entity sets come from the SHIPPED BookEntityProvider reading a REAL DbContext:
//
//   PreservationFixtureBooks.CreateAsync()
//        -> in-memory AppDbContext, two seeded books
//        -> BookEntityProvider (registered exactly as production: singleton over IServiceScopeFactory)
//        -> GetEntitiesAsync(bookId, c.Language) : BookEntitySet   [declared tier + manuscript tier]
//        -> ForeignRunClassifier.RunsToRepair(runs, value, expected, entitySet)
//
// so a REGRESSION in the harvest (or in the bookId threading) shows up as a GATE FAILURE here and in
// BookEntityFixtureSeedTests, instead of being masked by a hard-coded set that can never regress.
//
// THE LANGUAGE PASSED IS THE CASE'S OWN ANALYSIS LANGUAGE (final-r02), not the book's stored language —
// exactly as the production seams do. The provider resolves its HARVEST direction from it through the same
// ExpectedScriptForLanguage helper the repair layer resolves the classifier's `expected` from, so the script
// harvested is BY CONSTRUCTION the script the classifier looks up. (Harvesting off Books.Language instead
// meant an English-language analysis of a Hebrew book harvested LATIN while the classifier looked up HEBREW,
// leaving the entity lever inert.) Note the cases use LOCALE VARIANTS ("he-IL" / "en-US") of the seeded
// Books.Language ("he" / "en") — they collapse to the same canonical ExpectedScript, which is the cache key.
//
// THE TWO BOOKS, and WHY THEY ARE SEEDED THE WAY THEY ARE (BookEntityProvider's harvest contract):
//
//   HEBREW-NATIVE BOOK (Books.Language = "he"; ExpectedScript.Hebrew; FOREIGN script = Latin).
//     The provider harvests Latin TITLE-CASE tokens that RECUR across >= 2 chapters OR appear
//     MID-SENTENCE at least once. So the chapters carry the fixture's Latin names in Hebrew prose,
//     each one MID-SENTENCE (never right after a '.', which would read as sentence-initial
//     orthography, not a name) and in the EXACT CASE the fixture cases use — the manuscript tier is
//     matched CASE-SENSITIVELY (be-c04).
//     Its stored CharacterAnalysis declares the book's own HEBREW protagonists. Those land in the
//     DECLARED tier and are INERT for gating (a Hebrew token is the NATIVE script here, so it is never
//     a foreign run) — which is the honest picture: for a Hebrew-native book every entity that can
//     gate anything comes from the MANUSCRIPT tier.
//
//   LATIN-NATIVE BOOK (Books.Language = "en"; ExpectedScript.Latin; FOREIGN script = Hebrew).
//     Hebrew has NO letter case, so there is no Title-Case / ALL-CAPS / name-particle signal at all:
//     CROSS-CHAPTER RECURRENCE is the WHOLE manuscript gate, and this entity set is the ONLY lever
//     that can spare a legitimate Hebrew run in an English book. So each Hebrew name is seeded in TWO
//     chapters (a name appearing in only ONE chapter of an English book is NOT harvested).
//     Its stored CharacterAnalysis declares שרה / דוד — the NATURAL source for character names, and
//     the DECLARED tier (case-insensitive). ירושלים is a PLACE, not a character, so it has no declared
//     source and must come from the manuscript recurrence rule — which is exactly the lever be-c03
//     added, now under measurement instead of under assumption.
//
//   ADVERSARIAL BOOK (be-c08; Books.Language = "he"; ExpectedScript.Hebrew; FOREIGN script = Latin).
//     The book that points the entity lever AT the leak set. Its ch0 carries an English EPIGRAPH whose
//     Title-Case words are four of the d5 LEAK words (Confusion / Nostalgia / Tension / Catharsis), each
//     ONCE, MID-SENTENCE — so the provider harvests all four into the MANUSCRIPT tier. This is the ONLY
//     configuration in which the entity lever could SPARE A REAL LEAK, and it is precisely the one the d5
//     CLEANING gate never ran (its recorded table is labelled "entity-free"). ARM B of the d5 gate now runs
//     the real leak set against THIS book's real provider set; be-c04's case-SENSITIVE manuscript tier is
//     what must keep every lowercase leak REPAIRable. Deterministically pinned in BookEntityFixtureSeedTests.
//
// PLAIN TEXT, NOT SFDT (be-c04 lesson, do not undo): chapter text is seeded as plain ContentText.
// Extracting it through SfdtConversionService in the test environment injects the Syncfusion
// TRIAL-VERSION BANNER into every chapter, which then harvests as "Created" / "Syncfusion" / "Word"
// and pollutes the entity set with test-environment artifacts.
//
// NO MODEL, NO GPU, NO NETWORK: seeding + harvesting are pure DB reads and text scans. The live model
// only ever sees the values the classifier does NOT gate.
// ---------------------------------------------------------------------------

/// <summary>
/// A single legitimate-term case for the d6 PRESERVATION gate: the class it stresses, the token that must
/// survive byte-identical, the book's expected script + language, the prose value, an authoring note
/// (gate expectation), and — when the case can ONLY be spared by the per-book entity lever — the entity
/// the provider is REQUIRED to have harvested (<paramref name="GatingEntity"/>). A case whose
/// <c>GatingEntity</c> is not in the provider's set is a FINDING, not something to paper over with a
/// hand-fed fallback.
/// </summary>
public sealed record LegitCase(
    string Cls,
    string Token,
    ExpectedScript Expected,
    string Language,
    string Value,
    string Note,
    string? GatingEntity = null);

/// <summary>
/// A single CLEANING (recall) case for the d5 gate: realistic Hebrew literary-analysis prose leaking EXACTLY
/// ONE lowercase English abstract noun. The expected outcome is the MIRROR of a <see cref="LegitCase"/>: the
/// run MUST be classified REPAIR and the value MUST come back with no Latin residual.
///
/// Lives here (rather than inline in OutputQualityDiagnostic) so the LIVE d5 gate and the DETERMINISTIC
/// <c>BookEntityFixtureSeedTests</c> measure the SAME leak prose against the SAME adversarial book — the
/// offline half of the be-c08 arm is then a real pin on the live half, not a parallel re-authoring of it.
/// </summary>
public sealed record LeakCase(string Label, string Leak, string Value);

/// <summary>How a single legit case is gated, computed OFF-LINE (d1 detect + d2 classify, ZERO model calls)
/// so the report can attribute WHERE the safety comes from — and, crucially, whether the entity that gated it
/// was actually HARVESTED by the real provider.</summary>
public sealed record GateAttribution(
    int Runs,
    int RepairRuns,
    string Gate,
    bool ReachesModel,
    IReadOnlyList<string> EntitySparedRuns,
    IReadOnlyList<string> EntitySparedTiers,
    bool RequiredEntityHarvested,
    string? RequiredEntity)
{
    /// <summary>True when the entity set is LOAD-BEARING for this case: at least one run is LEAVE ONLY
    /// because the entity set spared it (it would REPAIR with no entity set). This — not mere membership —
    /// is what a provider regression would break.</summary>
    public bool EntityLoadBearing => EntitySparedRuns.Count > 0;

    /// <summary>A case that DECLARES a required entity the provider did NOT produce. The be-c07 FINDING
    /// condition: the case could only ever have passed with a hand-fed entity.</summary>
    public bool RequiredEntityMissing => RequiredEntity is not null && !RequiredEntityHarvested;
}

/// <summary>
/// The two synthetic books behind the d6 preservation gate, seeded into an in-memory <see cref="AppDbContext"/>
/// and read back through the SHIPPED <see cref="BookEntityProvider"/>. See the file header for the seeding
/// contract. Deterministic: no model, no GPU, no network.
/// </summary>
public sealed class PreservationFixtureBooks : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ServiceProvider _services;

    private PreservationFixtureBooks(
        ServiceProvider services, Guid hebrewBookId, Guid englishBookId, Guid adversarialBookId)
    {
        _services = services;
        HebrewBookId = hebrewBookId;
        EnglishBookId = englishBookId;
        AdversarialBookId = adversarialBookId;
        Provider = services.GetRequiredService<IBookEntityProvider>();
    }

    /// <summary>The HEBREW-native book (Books.Language = "he"). Its FOREIGN script is Latin.</summary>
    public Guid HebrewBookId { get; }

    /// <summary>The LATIN-native (English) book (Books.Language = "en"). Its FOREIGN script is Hebrew.</summary>
    public Guid EnglishBookId { get; }

    /// <summary>The ADVERSARIAL Hebrew-native book (be-c08): its manuscript carries a CAPITALIZED occurrence of
    /// four d5 leak words, so the provider harvests them as MANUSCRIPT-tier entities. See the file header.</summary>
    public Guid AdversarialBookId { get; }

    /// <summary>The REAL, production-registered provider (singleton over IServiceScopeFactory).</summary>
    public IBookEntityProvider Provider { get; }

    // ── the ANALYSIS languages the fixture harvests for (final-r02) ────────────────────────────────────

    /// <summary>The ANALYSIS language of every Hebrew-expected case (<see cref="LegitCases"/> uses "he-IL"), which
    /// is what the provider is now asked to harvest FOR. Note it is a LOCALE VARIANT of the seeded
    /// <c>Books.Language = "he"</c>: the provider keys its cache on the canonical <see cref="ExpectedScript"/>
    /// (ExpectedScriptForLanguage collapses "he" / "he-IL" / "HE" alike), so the variant cannot land in a second
    /// cache slot — and the harvest direction follows the ANALYSIS language, not the stored one.</summary>
    public const string HebrewBookLanguage = "he-IL";

    /// <summary>The ANALYSIS language of every Latin-expected case ("en-US"; a locale variant of the seeded
    /// <c>Books.Language = "en"</c> — see <see cref="HebrewBookLanguage"/>).</summary>
    public const string EnglishBookLanguage = "en-US";

    // ── the names the fixture DEPENDS on (asserted by BookEntityFixtureSeedTests) ─────────────────────

    /// <summary>Latin names the HEBREW-native cases reference, seeded Title-Case + mid-sentence into that
    /// book's chapters. Exact case matters: the manuscript tier matches CASE-SENSITIVELY (be-c04).</summary>
    public static readonly IReadOnlyList<string> HebrewBookLatinNames = new[]
    {
        "Kafka", "Paris", "Orwell",
        "Vincent", "Gogh", "Leonardo", "Vinci", "Simone", "Beauvoir",
        "Kindle", "Photoshop", "Google",
        "Brave", "New", "World",
    };

    /// <summary>ALL-CAPS acronyms that appear in the Hebrew book's manuscript but are NOT harvestable: the
    /// manuscript scan only records TITLE-CASE Latin tokens, so an acronym can never enter the set from the
    /// prose. They do not need it — classifier rule (6) (ALL-CAPS) gates them with zero model calls — but the
    /// asymmetry is recorded rather than hidden (be-c07 report).</summary>
    public static readonly IReadOnlyList<string> HebrewBookNonHarvestableAcronyms = new[] { "NASA", "PDF" };

    /// <summary>Lowercase Latin name PARTICLES present in the Hebrew book's prose that the harvester must NOT
    /// pick up (they are not Title-Case). The classifier's name-particle rule (8) owns them; harvesting them
    /// would MASK whether that rule works.</summary>
    public static readonly IReadOnlyList<string> HebrewBookParticles = new[] { "van", "da", "de" };

    // ── the be-c01 P0 shapes: TWO ADJACENT lowercase particles in ONE Title-Case name span (be-c08) ─────

    /// <summary>
    /// The CLASS labels of the three be-c01 P0 cases. They are the shapes the ORIGINAL name-particle rule got
    /// WRONG: it recognized only a SINGLE lowercase particle between two Title-Case Latin names, so when TWO
    /// lowercase runs sat side by side each DISQUALIFIED the other and BOTH were sent to the repair model —
    /// which then spliced Hebrew into a surname or a book title span-scoped. (Validation-by-re-detect could not
    /// catch it either: substituting Hebrew for "of" REDUCES the Latin-run count, so the corruption read as a
    /// successful repair.) Confirmed against the un-patched code: <c>The Lord of the Rings</c> sent "of"+"the",
    /// <c>Mies van der Rohe</c> sent "van"+"der", <c>Charles de la Rue</c> sent "de"+"la". be-c01 generalized the
    /// rule to a bounded walk WITHIN a Title-Case Latin name span.
    /// </summary>
    public static readonly IReadOnlyList<string> MultiParticleClasses = new[]
    {
        "proper-noun (multi-particle name span)",
        "intentional phrase (title with lowercase particles)",
    };

    /// <summary>
    /// EVERY Latin token of the three be-c01 P0 shapes — and NONE of them is seeded into ANY book's manuscript,
    /// ON PURPOSE. If they were, the Title-Case ones (Lord / Rings / Mies / Rohe / Charles / Rue) would harvest
    /// into the MANUSCRIPT tier and the report would attribute those cases to the ENTITY lever, hiding the only
    /// thing they exist to show: that the DETERMINISTIC classifier rule (8) carries them with NO per-book entity
    /// help at all. Pinned by <c>BookEntityFixtureSeedTests.MultiParticleNameSpanTokens_AreNeverHarvested</c> and
    /// by the no-entity-set-at-all classification test beside it.
    /// </summary>
    public static readonly IReadOnlyList<string> MultiParticleNameSpanTokens = new[]
    {
        "The", "Lord", "of", "the", "Rings",
        "Mies", "van", "der", "Rohe",
        "Charles", "de", "la", "Rue",
    };

    /// <summary>Hebrew names the LATIN-native cases reference. All three must be harvested — they are the ONLY
    /// lever that can spare a Hebrew run in an English book.</summary>
    public static readonly IReadOnlyList<string> EnglishBookHebrewNames = new[] { "שרה", "דוד", "ירושלים" };

    /// <summary>Of those, the ones DECLARED by the English book's stored CharacterAnalysis (the natural source
    /// for character names) — they land in the case-INSENSITIVE declared tier.</summary>
    public static readonly IReadOnlyList<string> EnglishBookDeclaredNames = new[] { "שרה", "דוד" };

    /// <summary>Of those, the ones that can ONLY come from the MANUSCRIPT recurrence rule (a place is not a
    /// character, so it has no declared source) — the case-SENSITIVE manuscript tier, and the exact lever
    /// be-c03 added for the Latin-native direction.</summary>
    public static readonly IReadOnlyList<string> EnglishBookManuscriptOnlyNames = new[] { "ירושלים" };

    // ── the ADVERSARIAL book (be-c08): the entity lever pointed AT the leak set ─────────────────────────

    /// <summary>
    /// The four d5 leak words the ADVERSARIAL book's manuscript shows CAPITALIZED, mid-sentence, exactly once
    /// (an English epigraph line) — the be-c04 scenario, reproduced verbatim. The provider MUST harvest all four
    /// into the MANUSCRIPT tier; that is what makes the book adversarial rather than hypothetical.
    ///
    /// Three of them (<c>Confusion</c>, <c>Nostalgia</c>, <c>Catharsis</c>) are also d5 LEAK seeds, so under the
    /// PRE-be-c04 case-INSENSITIVE matching each harvested token would have spared its LOWERCASE twin in the
    /// analysis prose — 3 of 10 leaks flipped REPAIR -> LEAVE, a 30% recall regression bought with one sentence.
    /// The fourth (<c>Tension</c>) is a <c>LiteraryTermGlossary</c> key rather than a d5 seed; it is seeded to
    /// show the same collision on the glossary surface.
    /// </summary>
    public static readonly IReadOnlyList<string> AdversarialHarvestedLeakWords = new[]
    {
        "Confusion", "Nostalgia", "Tension", "Catharsis",
    };

    /// <summary>Of those, the ones that are ALSO d5 leak seeds — the exact tokens whose lowercase forms must
    /// STILL be classified REPAIR (that is the be-c04 fix, asserted end to end through the real provider).</summary>
    public static readonly IReadOnlyList<string> AdversarialLeakWordsThatAreD5Seeds = new[]
    {
        "Confusion", "Nostalgia", "Catharsis",
    };

    /// <summary>The English epigraph line the adversarial book's ch0 carries — the be-c04 probe sentence, verbatim.
    /// Each Latin word occurs EXACTLY ONCE in the whole book, mid-sentence.</summary>
    public const string AdversarialEpigraph =
        "הוא ציטט את הפתגם האנגלי: \"A story of Confusion and Nostalgia, of Tension without Catharsis.\"";

    // ── the CLEANING (d5) fixture — the REAL out-of-glossary leak set ──────────────────────────────────

    /// <summary>
    /// The d5 CLEANING (recall) set: 10 Hebrew prose values, each leaking ONE lowercase English abstract noun
    /// the d2 classifier must route to REPAIR. The first two are the KNOWN real leaks (confusion /
    /// claustrophobia); the rest are the SEEDED OUT-OF-GLOSSARY set — abstract nouns absent from the ~35-term
    /// literary glossary, so the glossary fast-path cannot catch them and only the dynamic pass can.
    ///
    /// UNCHANGED from the e4/d5 fixture (be-c08 changed only WHERE the entity set comes from — see the two ARMS
    /// in <c>OutputQualityDiagnostic.MeasureDynamicTermRepair_LocalVsCloud</c>).
    /// </summary>
    public static readonly IReadOnlyList<LeakCase> LeakCases = new[]
    {
        new LeakCase("known-leak-confusion",      "confusion",      "הדמות הראשית שקעה בתחושת confusion עמוקה כשהתגלתה לה האמת על אביה."),
        new LeakCase("known-leak-claustrophobia", "claustrophobia", "תיאור החדר האטום מעורר claustrophobia חונקת שאין ממנה מנוס לגיבור."),
        new LeakCase("oog-ambivalence",           "ambivalence",    "יחסה של הגיבורה אל אמה מלא ambivalence, בין אהבה עזה לכעס מר."),
        new LeakCase("oog-nostalgia",             "nostalgia",      "הפרק כולו ספוג nostalgia אל ימי הילדות בכפר הגלילי הישן."),
        new LeakCase("oog-alienation",            "alienation",     "המהגר חש alienation מתמדת בעיר הזרה והקרה שסביבו."),
        new LeakCase("oog-catharsis",             "catharsis",      "הסצנה האחרונה מביאה את הקורא אל catharsis רגשי משחרר וצלול."),
        new LeakCase("oog-disorientation",        "disorientation", "היקיצה הפתאומית הותירה בו disorientation מוחלטת למשך רגע ארוך."),
        new LeakCase("oog-vulnerability",         "vulnerability",  "הווידוי הכן חושף vulnerability נדירה של הגיבור הקשוח."),
        new LeakCase("oog-melancholy",            "melancholy",     "אווירת הסתיו בסיפור טעונה melancholy שקטה ומהורהרת לאורך כל הפרק."),
        new LeakCase("oog-foreboding",            "foreboding",     "הרמזים המוקדמים יוצרים תחושת foreboding המלווה את הקורא עד הסוף."),
    };

    // ── the LEGIT-TERM fixture (shared by the live d6 gate and the deterministic seed test) ────────────

    /// <summary>
    /// The d6 legitimate-term set: every value MUST come back byte-identical. REPAIR/LEAVE expectations are
    /// UNCHANGED from the e4 fixture (be-c07 changed only WHERE the entity set comes from). The Note records
    /// the expected gate; the last field names the entity the case REQUIRES the provider to have harvested.
    /// </summary>
    public static readonly IReadOnlyList<LegitCase> Cases = new[]
    {
        // ── Foreign PROPER NOUNS, Title-Case mid-sentence → classifier rule (7) LEAVEs (gated) ──
        new LegitCase("proper-noun (Title-Case)", "Kafka", ExpectedScript.Hebrew, "he-IL",
            "הרומן מזכיר את סגנונו של Kafka במבנה הסיוטי שלו.", "classifier LEAVE (Title-Case mid-sentence)"),
        new LegitCase("proper-noun (Title-Case)", "Paris", ExpectedScript.Hebrew, "he-IL",
            "העלילה מתרחשת ברובע ההיסטורי של Paris בשלהי המאה.", "classifier LEAVE (Title-Case mid-sentence)"),
        new LegitCase("proper-noun (Title-Case)", "Orwell", ExpectedScript.Hebrew, "he-IL",
            "הביקורת השוותה את הדיסטופיה לזו של Orwell בספרו הידוע.", "classifier LEAVE (Title-Case mid-sentence)"),

        // ── Foreign PROPER NOUNS with a LOWERCASE particle → classifier rule (8), the name-span walk ──
        new LegitCase("proper-noun (lowercase particle)", "van", ExpectedScript.Hebrew, "he-IL",
            "הצייר Vincent van Gogh מוזכר כמקור השראה חזותי לפרק.", "classifier LEAVE (name-particle inside a Title-Case name span)"),
        new LegitCase("proper-noun (lowercase particle)", "da", ExpectedScript.Hebrew, "he-IL",
            "יצירתו של Leonardo da Vinci משמשת דימוי מרכזי בסצנה.", "classifier LEAVE (name-particle inside a Title-Case name span)"),
        new LegitCase("proper-noun (lowercase particle)", "de", ExpectedScript.Hebrew, "he-IL",
            "הדמות מצטטת את Simone de Beauvoir בעניין החירות.", "classifier LEAVE (name-particle inside a Title-Case name span)"),

        // ── The be-c01 P0 shapes: TWO ADJACENT lowercase particles in ONE name span (added be-c08) ──
        // The exact three values that CORRUPTED under the un-patched rule (each disqualifying particle pair went
        // to the repair model, which spliced Hebrew into a surname / a book title). They are the shapes the whole
        // rollout decision rests on, so the ARTIFACT OF RECORD must show them GATED with ZERO model calls.
        //
        // GATED BY THE CLASSIFIER, NOT BY THE ENTITY SET — by construction: NONE of their tokens
        // (MultiParticleNameSpanTokens) is seeded into ANY book's manuscript, so the provider cannot possibly
        // harvest them and the entity lever is INERT here. Rule (8)'s bounded name-span walk is the only thing
        // that can spare them; the prose is verbatim the same as the ForeignRunClassifierTests pins.
        new LegitCase("intentional phrase (title with lowercase particles)", "The Lord of the Rings", ExpectedScript.Hebrew, "he-IL",
            "הוא קרא את The Lord of the Rings בשנית.",
            "classifier LEAVE (name-span walk over the 'of the' chain — be-c01 P0)"),
        new LegitCase("proper-noun (multi-particle name span)", "Mies van der Rohe", ExpectedScript.Hebrew, "he-IL",
            "האדריכל Mies van der Rohe עיצב את הביתן.",
            "classifier LEAVE (name-span walk over the 'van der' chain — be-c01 P0)"),
        new LegitCase("proper-noun (multi-particle name span)", "Charles de la Rue", ExpectedScript.Hebrew, "he-IL",
            "הסופר Charles de la Rue פרסם ספר חדש.",
            "classifier LEAVE (name-span walk over the 'de la' chain — be-c01 P0)"),

        // ── BRANDS / products ──
        new LegitCase("brand", "Kindle", ExpectedScript.Hebrew, "he-IL",
            "היא קראה את הרומן במכשיר Kindle במהלך הטיסה הארוכה.", "classifier LEAVE (Title-Case mid-sentence)"),
        new LegitCase("brand", "Photoshop", ExpectedScript.Hebrew, "he-IL",
            "העורך עיבד את התמונה בתוכנת Photoshop לפני ההדפסה.", "classifier LEAVE (Title-Case mid-sentence)"),
        new LegitCase("brand", "Google", ExpectedScript.Hebrew, "he-IL",
            "הגיבור חיפש את התשובה במנוע החיפוש Google בלילה ההוא.", "detector allowlist (never even a run)"),

        // ── ALL-CAPS acronyms ──
        new LegitCase("acronym", "NASA", ExpectedScript.Hebrew, "he-IL",
            "הסוכנת עבדה שנים בסוכנות NASA לפני שפרשה לכתיבה.", "classifier LEAVE (ALL-CAPS)"),
        new LegitCase("acronym", "PDF", ExpectedScript.Hebrew, "he-IL",
            "הקובץ הופץ בפורמט PDF כדי לשמור על העימוד.", "classifier LEAVE (ALL-CAPS)"),

        // ── INTENTIONAL English phrase inside Hebrew ──
        new LegitCase("intentional phrase (Title-Case title)", "Brave New World", ExpectedScript.Hebrew, "he-IL",
            "הסופר קרא לספרו \"Brave New World\" כמחווה עתידנית.", "classifier LEAVE (quoted multi-word span / Title-Case mid-sentence)"),
        new LegitCase("intentional phrase (lowercase code-switch)", "carpe diem", ExpectedScript.Hebrew, "he-IL",
            "הדמות לוחשת \"carpe diem\" ברגע המכריע של הפרק.", "classifier LEAVE (quoted multi-word foreign span, be-c05 matched pair)"),

        // ── URL / email ──
        new LegitCase("url", "example.com", ExpectedScript.Hebrew, "he-IL",
            "רשימת המקורות המלאה זמינה באתר example.com של המחבר.", "classifier LEAVE (dotted host)"),
        new LegitCase("email", "info@publisher.com", ExpectedScript.Hebrew, "he-IL",
            "לשאלות ניתן לפנות אל הכתובת info@publisher.com בכל עת.", "classifier LEAVE (email borders)"),

        // ── HEBREW-IN-ENGLISH-BOOK (ExpectedScript.Latin) — the entity lever is the ONLY gate here ──
        // Hebrew has no case, so there is no Title-Case / ALL-CAPS / name-particle signal: without a
        // provider-harvested entity these three REACH THE MODEL. Each therefore names the entity the
        // provider MUST have produced (declared tier for the two characters, manuscript recurrence for
        // the place) — if it did not, the harness reports a FINDING instead of silently still passing.
        new LegitCase("hebrew-in-english (name)", "שרה", ExpectedScript.Latin, "en-US",
            "The protagonist's name, שרה, deliberately echoes the biblical matriarch.",
            "classifier LEAVE (book-entity — DECLARED character name)", "שרה"),
        new LegitCase("hebrew-in-english (name)", "דוד", ExpectedScript.Latin, "en-US",
            "The character דוד serves as the moral center of the third act.",
            "classifier LEAVE (book-entity — DECLARED character name)", "דוד"),
        new LegitCase("hebrew-in-english (entity)", "ירושלים", ExpectedScript.Latin, "en-US",
            "The city of ירושלים anchors the entire narrative arc.",
            "classifier LEAVE (book-entity — MANUSCRIPT cross-chapter recurrence)", "ירושלים"),
    };

    // ── construction ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Seeds both books into a fresh in-memory store and wires the REAL provider over it (registered
    /// exactly as production: <c>AddSingleton&lt;IBookEntityProvider, BookEntityProvider&gt;</c> reading the
    /// DbContext through <c>IServiceScopeFactory</c>).</summary>
    public static async Task<PreservationFixtureBooks> CreateAsync()
    {
        // The store name is computed ONCE, OUTSIDE the options lambda. The lambda runs on EVERY DbContext
        // construction, and the provider reads through its OWN scope (a second DbContext) — so a `Guid.NewGuid()`
        // inside the lambda would hand each context a DIFFERENT in-memory database and the provider would see an
        // empty book while the seeding "succeeded".
        var dbName = "preservation-fixture-" + Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
        var sp = services.BuildServiceProvider();

        var db = sp.GetRequiredService<AppDbContext>();
        var hebrewBookId = Guid.NewGuid();
        var englishBookId = Guid.NewGuid();
        var adversarialBookId = Guid.NewGuid();
        SeedHebrewNativeBook(db, hebrewBookId);
        SeedLatinNativeBook(db, englishBookId);
        SeedAdversarialLeakBook(db, adversarialBookId);
        await db.SaveChangesAsync();

        return new PreservationFixtureBooks(sp, hebrewBookId, englishBookId, adversarialBookId);
    }

    /// <summary>The entity set for a case's BOOK, straight from the real provider — harvested for the case's OWN
    /// ANALYSIS LANGUAGE (final-r02). This is the production contract in miniature: the language handed to the
    /// provider is the SAME <c>c.Language</c> the repair layer resolves <c>c.Expected</c> from, so the script the
    /// provider harvests is by construction the script the classifier looks up. Hebrew-expected cases read the
    /// Hebrew-native book; Latin-expected cases read the English-native book.</summary>
    public Task<IReadOnlySet<string>> EntitiesForAsync(LegitCase c)
        => c.Expected == ExpectedScript.Hebrew
            ? Provider.GetEntitiesAsync(HebrewBookId, c.Language)
            : Provider.GetEntitiesAsync(EnglishBookId, c.Language);

    /// <summary>The HEBREW-native book harvested for a HEBREW-language analysis (its native direction: foreign =
    /// Latin).</summary>
    public Task<IReadOnlySet<string>> HebrewBookEntitiesAsync() => Provider.GetEntitiesAsync(HebrewBookId, HebrewBookLanguage);

    /// <summary>The LATIN-native book harvested for an ENGLISH-language analysis (its native direction: foreign =
    /// Hebrew).</summary>
    public Task<IReadOnlySet<string>> EnglishBookEntitiesAsync() => Provider.GetEntitiesAsync(EnglishBookId, EnglishBookLanguage);

    /// <summary>The ADVERSARIAL book's entity set, straight from the real provider — the set that CONTAINS the
    /// capitalized leak words. This is the set the d5 gate's ARM B runs with (be-c08). Hebrew-native, harvested in
    /// its native direction (foreign = Latin), which is what puts the Latin leak words in the set.</summary>
    public Task<IReadOnlySet<string>> AdversarialBookEntitiesAsync() => Provider.GetEntitiesAsync(AdversarialBookId, HebrewBookLanguage);

    public void Dispose() => _services.Dispose();

    // ── gate attribution (OFF-LINE: d1 detect + d2 classify, ZERO model calls) ─────────────────────────

    /// <summary>
    /// Attributes ONE legit case's safety, with no model call: runs the shipped detector and the shipped
    /// classifier TWICE — once WITH the provider's entity set and once with NO entity set — so the report can
    /// separate the runs the ENTITY LEVER spared (they would REPAIR without it) from the runs a CLASSIFIER
    /// RULE spared (Title-Case / ALL-CAPS / name-span / quote / URL). That difference is the whole point of
    /// be-c07: only the entity-spared runs actually exercise BookEntityProvider, and only they regress if the
    /// harvest breaks.
    /// </summary>
    public static GateAttribution AttributeGate(LegitCase c, IReadOnlySet<string> entities)
    {
        var runs = LatinInHebrewContentDetector.DetectForeignRuns(c.Value, c.Expected);
        var withEntities = ForeignRunClassifier.RunsToRepair(runs, c.Value, c.Expected, entities);
        var withoutEntities = ForeignRunClassifier.RunsToRepair(runs, c.Value, c.Expected, null);

        // Runs the ENTITY SET (and only it) spared: REPAIR with no entities, LEAVE with them. ForeignRun is a
        // readonly record struct, so value equality is exact.
        var entitySpared = withoutEntities.Where(r => !withEntities.Contains(r)).Select(r => r.Text).ToList();
        var entityTiers = entitySpared.Select(t => TierOf(entities, t)).ToList();

        var requiredHarvested = c.GatingEntity is null || entities.Contains(c.GatingEntity);

        string gate;
        if (runs.Count == 0)
        {
            gate = "detector-gated (allowlist/none)";
        }
        else if (withEntities.Count == 0)
        {
            gate = entitySpared.Count > 0
                ? $"entity-gated (provider-harvested: {string.Join(", ", entitySpared.Distinct())})"
                : "classifier-gated (LEAVE)";
        }
        else
        {
            gate = $"reaches model ({withEntities.Count} run)";
        }

        return new GateAttribution(
            runs.Count, withEntities.Count, gate, withEntities.Count > 0,
            entitySpared, entityTiers, requiredHarvested, c.GatingEntity);
    }

    /// <summary>Which TIER of the provider's two-tier <see cref="BookEntitySet"/> a token came from — DECLARED
    /// (stored analysis, case-insensitive) or MANUSCRIPT (prose scan, case-SENSITIVE). "hand-fed" would mean
    /// the caller did not pass a real provider set at all, which be-c07 exists to prevent.</summary>
    public static string TierOf(IReadOnlySet<string> entities, string token)
    {
        if (entities is not BookEntitySet set)
        {
            return "hand-fed";
        }

        if (set.DeclaredNames.Contains(token)) return "declared";
        if (set.ManuscriptTokens.Contains(token)) return "manuscript";
        return "not-in-set";
    }

    // ── seeding ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The HEBREW-native book. FOREIGN script = Latin, so the provider harvests Latin TITLE-CASE tokens that
    /// RECUR across chapters OR appear MID-SENTENCE at least once. Every Latin name below sits MID-SENTENCE
    /// (preceded by a Hebrew word, never by a sentence terminator — a sentence-initial capital is orthography,
    /// not a name signal) and in the EXACT case the fixture uses.
    /// Also present ON PURPOSE and NOT harvestable: the ALL-CAPS acronyms (NASA / PDF — the scan takes only
    /// Title-Case) and the lowercase particles (van / da / de — the classifier's name-particle rule owns them,
    /// and harvesting them would mask whether that rule works).
    ///
    /// DELIBERATELY ABSENT (be-c08, do not "fix" this): every token of the three be-c01 P0 shapes —
    /// <c>The Lord of the Rings</c>, <c>Mies van der Rohe</c>, <c>Charles de la Rue</c>
    /// (see <see cref="MultiParticleNameSpanTokens"/>). Their Title-Case tokens WOULD harvest if written into
    /// this prose (one mid-sentence mention is enough), and the report would then credit the ENTITY lever for
    /// gating them — hiding the one thing they are in the fixture to prove: that the classifier's name-span walk
    /// carries them ALONE, with no per-book entity help.
    /// </summary>
    private static void SeedHebrewNativeBook(AppDbContext db, Guid bookId)
    {
        db.Books.Add(new Book { Id = bookId, Title = "הרומן העברי", Language = "he" });

        // ch0 — Kafka / Paris / Orwell, and the quoted title "Brave New World" (Brave, New, World).
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Order = 0,
            Title = "פרק 0",
            ContentText =
                "הרומן מזכיר את סגנונו של Kafka במבנה הסיוטי שלו, והעלילה מתרחשת ברובע ההיסטורי של Paris " +
                "בשלהי המאה. הביקורת השוותה את הדיסטופיה לזו של Orwell בספרו הידוע, והסופר קרא לספרו " +
                "\"Brave New World\" כמחווה עתידנית.",
        });

        // ch1 — Kafka RECURS (a second chapter), plus the three name spans whose lowercase particles the
        // classifier's rule (8) must own: Vincent van Gogh / Leonardo da Vinci / Simone de Beauvoir.
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Order = 1,
            Title = "פרק 1",
            ContentText =
                "שוב הופיע Kafka בסיפור, והצייר Vincent van Gogh מוזכר כמקור השראה חזותי לפרק. יצירתו של " +
                "Leonardo da Vinci משמשת דימוי מרכזי בסצנה, והדמות מצטטת את Simone de Beauvoir בעניין החירות.",
        });

        // ch2 — the brands (Kindle / Photoshop / Google) and the two ALL-CAPS acronyms (NASA / PDF), which the
        // Title-Case-only scan CANNOT harvest — they are gated by classifier rule (6) instead.
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Order = 2,
            Title = "פרק 2",
            ContentText =
                "היא קראה את הרומן במכשיר Kindle במהלך הטיסה הארוכה, והעורך עיבד את התמונה בתוכנת Photoshop " +
                "לפני ההדפסה. הגיבור חיפש את התשובה במנוע החיפוש Google בלילה ההוא, הסוכנת עבדה שנים בסוכנות " +
                "NASA לפני שפרשה לכתיבה, והקובץ הופץ בפורמט PDF כדי לשמור על העימוד.",
        });

        // Stored analysis: the book's own HEBREW protagonists. They land in the DECLARED tier and are INERT for
        // gating (Hebrew is this book's NATIVE script, so a Hebrew token is never a foreign run here). Seeded
        // anyway because it is what a real Hebrew book HAS — and it makes the report's tier split honest: every
        // entity that gates anything for a Hebrew-native book comes from the MANUSCRIPT tier.
        db.AnalysisResults.Add(new AnalysisResult
        {
            BookId = bookId,
            AnalysisType = AnalysisType.CharacterAnalysis,
            Scope = AnalysisScope.Book,
            Status = AnalysisStatus.Active,
            StructuredResult = SerializeCharacters(
                new[] { "מרים", "אליהו" },
                ("מרים", "אליהו")),
        });
    }

    /// <summary>
    /// The LATIN-native (English) book. FOREIGN script = Hebrew, which has NO letter case — so CROSS-CHAPTER
    /// RECURRENCE is the WHOLE manuscript gate, and each Hebrew name must appear in at least TWO chapters (a
    /// name in only ONE chapter of an English book is NOT harvested). The stored CharacterAnalysis declares the
    /// two CHARACTERS (שרה / דוד) — the natural source, and the case-insensitive DECLARED tier; the PLACE
    /// (ירושלים) has no declared source and can only arrive via the manuscript recurrence rule.
    /// </summary>
    private static void SeedLatinNativeBook(AppDbContext db, Guid bookId)
    {
        db.Books.Add(new Book { Id = bookId, Title = "The Letters", Language = "en" });

        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Order = 0,
            Title = "Chapter 1",
            ContentText = "Daniel met שרה outside the gates of ירושלים, where דוד had waited since dawn.",
        });
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Order = 1,
            Title = "Chapter 2",
            ContentText = "Years later שרה wrote from ירושלים again, and דוד answered her without hesitation.",
        });

        db.AnalysisResults.Add(new AnalysisResult
        {
            BookId = bookId,
            AnalysisType = AnalysisType.CharacterAnalysis,
            Scope = AnalysisScope.Book,
            Status = AnalysisStatus.Active,
            StructuredResult = SerializeCharacters(
                new[] { "שרה", "דוד" },
                ("שרה", "דוד")),
        });
    }

    /// <summary>
    /// The ADVERSARIAL Hebrew-native book (be-c08). Same shape as a real manuscript — Hebrew prose, Hebrew
    /// declared characters — with ONE English epigraph line whose Title-Case words are, deliberately, four of
    /// the d5 LEAK words (<see cref="AdversarialHarvestedLeakWords"/>). Each occurs EXACTLY ONCE, MID-SENTENCE,
    /// in the whole book, which is all the harvest condition needs (<c>recursAcrossChapters || appearsMidSentence</c>,
    /// and <c>appearsMidSentence</c> is satisfied by a single occurrence).
    ///
    /// WHY: this is the ONE scenario in which the entity lever can SPARE A REAL LEAK, and it is the scenario the
    /// d5 gate never ran (its recorded table is labelled "entity-free"). Under the PRE-be-c04 case-INSENSITIVE
    /// membership, the harvested `Confusion` spared every lowercase `confusion` in analysis output. be-c04 made
    /// the MANUSCRIPT tier match CASE-SENSITIVELY (BookEntitySet) precisely to sever that link. This book is what
    /// puts that fix under measurement instead of under assumption: it is the entity set ARM B of the d5 gate
    /// runs with, and the deterministic BookEntityFixtureSeedTests pin its offline half.
    ///
    /// The declared CharacterAnalysis carries only HEBREW names, on purpose: a DECLARED name matches
    /// case-INSENSITIVELY, so declaring an English word here would spare its lowercase form legitimately and
    /// would confound what this book is measuring (the MANUSCRIPT tier).
    ///
    /// PLAIN TEXT, NOT SFDT — see the file header (the Syncfusion trial banner harvests as Created/Syncfusion/Word).
    /// </summary>
    private static void SeedAdversarialLeakBook(AppDbContext db, Guid bookId)
    {
        db.Books.Add(new Book { Id = bookId, Title = "הספר היריב", Language = "he" });

        // ch0 — ordinary Hebrew prose + THE EPIGRAPH. Confusion / Nostalgia / Tension / Catharsis each appear
        // once, Title-Case, mid-sentence (preceded by a lowercase Latin word, never by a sentence terminator).
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Order = 0,
            Title = "פרק 0",
            ContentText =
                "הפרק נפתח בתיאור הבית הישן שבקצה הכפר, ובו גדלה הגיבורה בשנים שלפני המלחמה. " +
                AdversarialEpigraph +
                " ואז שב אל שתיקתו הארוכה בפינת החדר.",
        });

        // ch1 — pure Hebrew, no Latin at all: the epigraph words stay at ONE occurrence each, so the harvest can
        // only fire through `appearsMidSentence` (MidSentenceCount == 1) — the exact, minimal, measured trigger.
        db.Chapters.Add(new Chapter
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Order = 1,
            Title = "פרק 1",
            ContentText =
                "בפרק השני חוזרת הגיבורה אל הבית הנטוש ומגלה את המכתבים שהוסתרו מתחת לרצפה, " +
                "וכל מה שנותר ממנו הוא ריח הנייר הישן.",
        });

        db.AnalysisResults.Add(new AnalysisResult
        {
            BookId = bookId,
            AnalysisType = AnalysisType.CharacterAnalysis,
            Scope = AnalysisScope.Book,
            Status = AnalysisStatus.Active,
            StructuredResult = SerializeCharacters(
                new[] { "מרים", "אליהו" },
                ("מרים", "אליהו")),
        });
    }

    /// <summary>Which of <see cref="AdversarialHarvestedLeakWords"/> the REAL provider actually put in the set, and
    /// in which TIER — the be-c08 report line ("Confusion IS a manuscript-tier entity, and lowercase `confusion`
    /// still cleaned" is the be-c04 fix working end to end).</summary>
    public static IReadOnlyList<(string Word, bool Harvested, string Tier)> AdversarialHarvestReport(
        IReadOnlySet<string> adversarialEntities)
        => AdversarialHarvestedLeakWords
            .Select(w => (w, adversarialEntities.Contains(w), TierOf(adversarialEntities, w)))
            .ToList();

    private static string SerializeCharacters(string[] names, (string a, string b)? rel)
    {
        var result = new CharacterAnalysisResult { Summary = "fixture" };
        foreach (var name in names)
        {
            result.Characters.Add(new CharacterEntry { Name = name, Role = "supporting", Description = "d", Arc = "a" });
        }

        if (rel is { } r)
        {
            result.Relationships.Add(new CharacterRelationship { Character1 = r.a, Character2 = r.b, Relationship = "knows" });
        }

        return JsonSerializer.Serialize(result, CamelCase);
    }
}
