using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using static Pagedraft.Api.Services.Analysis.ScriptTokenPredicates;

namespace Pagedraft.Api.Services.Analysis;

// ---------------------------------------------------------------------------
// BookEntityProvider — the per-book PROPER-NOUN list feeding the classifier's
// already-present bookEntities LEAVE lever (dynamic-term-repair precision
// follow-up plan, todo e2). This is the ONE lever that can spare the book's OWN
// names (Kafka, Paris, Gogh, brand names) that a global list never could.
//
//   BookEntityProvider.GetEntitiesAsync(bookId, language)  ->  BookEntitySet : IReadOnlySet<string>
//         |                                            (two matching tiers — see below)
//   [d2] ForeignRunClassifier.Classify(..., bookEntities)  ->  a member is LEAVE
//
// TWO MATCHING TIERS (be-c04). DECLARED names (source (a)) are matched CASE-INSENSITIVELY — they are
// authoritative proper nouns, so every casing of them is the name. MANUSCRIPT-harvested tokens (source (b))
// are matched CASE-SENSITIVELY — the only evidence for them is the CAPITALIZED surface form the scan saw,
// while a vocabulary leak is LOWERCASE by construction. Matching them case-insensitively meant ONE
// capitalized "Confusion" anywhere in the manuscript (an epigraph, a quoted line, a brand) spared EVERY
// lowercase "confusion" in the analysis output: measured on the real Hebrew manuscript fixture, a single
// added English epigraph line flipped 3 of the 10 d5 leak seeds from REPAIR to LEAVE. See BookEntitySet.
//
// GOVERNING PRINCIPLE (from the plan): BIAS HARD TOWARD LEAVE. This set exists to
// keep the classifier from repairing the book's own names. A false ADD (a common
// word harvested by mistake) merely spares a leak (a cosmetic miss); a MISSED name
// lets the model corrupt a proper noun (a real error). So harvest generously among
// PROPER-NOUN-SHAPED signals, but guard the manuscript scan tightly enough that
// ordinary common words are NOT harvested (the e2 test asserts both).
//
// DETERMINISTIC, NO MODEL, NO GPU: sources are (a) the book's already-stored
// analysis entity names and (b) a pure text scan of the chapter prose — never an
// NER model call.
//
// TWO SOURCES:
//   (a) STORED ANALYSIS names — character names from the latest stored
//       CharacterAnalysis results (AnalysisResult.StructuredResult) and the cached
//       BookProfile.CharactersJson (a serialized CharacterAnalysisResult):
//       CharacterEntry.Name + CharacterRelationship.Character1/Character2. These are
//       proper nouns by construction (the RepairableFields must-not-touch list marks
//       them so). Script-agnostic: a Hebrew character name spares a foreign HEBREW
//       run in a Latin-script book (the classifier's entity check is the only lever
//       that can). NOTE: BookOverviewResult carries NO character/place-name field
//       (only genre/audience/register/summary), so there is nothing to harvest there
//       — the plan's "BookOverview place names" premise does not hold for this schema.
//   (b) MANUSCRIPT SCAN — SCRIPT-AWARE (be-c03), keyed on the ANALYSIS LANGUAGE (final-r02).
//       The scan harvests the script that is FOREIGN for the run being repaired, because that
//       is the only script whose runs the classifier will ever look up in this set.
//
//       THE DIRECTION COMES FROM THE CALLER'S `language`, NOT FROM Books.Language — and that is
//       the whole point (final-r02). GetEntitiesAsync takes the ANALYSIS language: the very same
//       value UnifiedAnalysisService.ApplyAnalysisRepairAsync / BookReviewService thread into
//       DynamicTermRepairService, which resolves the classifier's `expected` from it through
//       DynamicTermRepairService.ExpectedScriptForLanguage. This provider resolves the harvest
//       direction by calling THAT SAME HELPER ON THAT SAME VALUE, so the harvest direction and the
//       repair direction agree BY CONSTRUCTION.
//
//       KEYING ON Books.Language WAS THE BUG (the pre-final-r02 header claimed the two "can never
//       disagree" because they share a helper — same helper, DIFFERENT input, so the claim was
//       false). The analysis language is CALLER-OVERRIDABLE: AnalysisController prefers
//       RunAnalysisRequest.Language over the book's stored language. So an English-language analysis
//       of a Hebrew book made the classifier look up HEBREW runs while the manuscript tier held only
//       LATIN tokens — the entity lever, the ONLY lever that can spare a Hebrew run in that
//       direction, was SILENTLY INERT, and an undeclared Hebrew name reached the repair model and
//       was rewritten. Safe by CALLER (today's FE always sends the book language), never safe by
//       CONSTRUCTION. Do not reintroduce a Books.Language read here.
//
//         HEBREW-expected analysis (ExpectedScript.Hebrew, foreign = Latin):
//             harvest Latin TITLE-CASE tokens that either RECUR across >= 2 chapters OR
//             appear MID-sentence at least once. A capitalized Latin token in a Hebrew book
//             that recurs or sits mid-sentence is very likely a name/brand, not a leak (a
//             leaked common word is lowercase / one-off / sentence-initial). A tiny stop-list
//             of common English words is the backstop so a recurring sentence-opener ("The",
//             "Then") is not mistaken for a name; everything else is structural.
//
//         LATIN-expected analysis (ExpectedScript.Latin, foreign = Hebrew):
//             harvest HEBREW tokens that RECUR across >= 2 chapters. Hebrew has NO letter
//             case, so there is no Title-Case signal, no all-caps signal and no name-particle
//             signal — cross-chapter RECURRENCE is the WHOLE gate, and this entity set is the
//             ONLY lever that can spare a legitimate Hebrew run in an English book. That makes
//             the Hebrew stop-list load-bearing (see CommonHebrewWordStopList): without it the
//             recurrence rule would harvest ordinary prose. NOTE the benign asymmetry: a Hebrew
//             token recurring across chapters of an ENGLISH manuscript is author-intended Hebrew
//             (a name, a place, a term, a quoted phrase) almost by definition — English prose does
//             not contain incidental Hebrew — so an over-harvest here is usually the RIGHT answer
//             anyway. The stop-list mainly protects the CROSS-DIRECTION case: an ENGLISH-language
//             analysis of a HEBREW manuscript (a caller-overridable language, and now the case this
//             provider handles correctly rather than silently mis-harvesting). There an unfiltered
//             scan would harvest ordinary Hebrew prose wholesale. Note that even the fully
//             over-harvested outcome is the FAIL-SAFE one: the classifier treats every Hebrew run as
//             foreign, the set LEAVEs them all, and the dynamic stage degrades to a no-op — the
//             pre-feature behaviour — rather than corrupting the book's Hebrew names.
//
//       Only the FOREIGN script (relative to the ANALYSIS language) is scanned: a token of the
//       EXPECTED script can never be a foreign run, so harvesting it would be inert noise that only
//       eats into MaxEntitySetSize.
//
// CACHE + INVALIDATION (be-c03; keyed per DIRECTION by final-r02): the set is derived from
// slow-changing data (chapter prose + stored analysis), so a successful NON-EMPTY build is cached —
// but the cache is now BOUNDED, REFRESHED and DIRECTION-KEYED, because the earlier "cache forever,
// cache the empty set, never invalidate, one entry per book" posture made the stored-names source
// effectively dead in production AND could not represent two directions of the same book at once:
//
//   0. KEYED BY (bookId, ExpectedScript) — NOT by bookId alone (final-r02). The same book analysed in
//      two languages needs two DIFFERENT sets (Latin tokens for a Hebrew-expected run, Hebrew tokens
//      for a Latin-expected one), and a bookId-only key would serve whichever direction happened to
//      build first to BOTH. ExpectedScript is also the CANONICAL NORMALIZER of the language string:
//      ExpectedScriptForLanguage collapses "he" / "he-IL" / "HE" to Hebrew and "en" / "en-US" / "fr"
//      to Latin, so a locale variant can never land in a second slot. It is resolved ONCE, at the top
//      of GetEntitiesAsync, and that one resolved value is what both the cache lookup, the cache write
//      and the harvest use — read and write cannot disagree.
//   1. BOUNDED: a private MemoryCache with a SizeLimit (MaxCachedBooks entries, 1 per book+direction), a
//      sliding expiry (a book under active analysis stays hot) and an ABSOLUTE expiry ceiling, so
//      the cache can neither grow without bound across books nor serve an arbitrarily old set.
//      The cache instance is OWNED here rather than injected: giving the app-wide IMemoryCache a
//      SizeLimit would force every future cache entry app-wide to declare a Size, so a private
//      instance is the only way to bound this cache without that side effect.
//   2. AN EMPTY BUILD IS NOT CACHED. An empty set means "no harvest source exists YET" — the exact
//      state a fresh book is in on its FIRST analysis, moments before BuildBookProfileAsync produces
//      the CharacterAnalysis this provider wants. Caching it is what made the ordinary production
//      sequence (analyse -> profile-build -> analyse) never see a single character name for the
//      lifetime of the process. Rebuilding an empty book costs three indexed reads that return
//      nothing; that is cheap, and it self-heals.
//   3. Invalidate(bookId) IS CALLED by every producer that changes a harvest source:
//        - BookIntelligenceService.BuildBookProfileAsync   (writes BookProfile.CharactersJson)
//        - UnifiedAnalysisService's three persisting seams  (write an Active CharacterAnalysis
//          AnalysisResult — and archive the previous one)
//        - ChapterService create / save / delete / import   (write Chapter.ContentText)
//      It drops EVERY DIRECTION's entry for the book (it enumerates ExpectedScript), because a
//      producer changes the underlying prose / names, which feeds both directions. Dropping only one
//      would leave the other serving a set built from the pre-write manuscript.
//      NOTE (final-r02): a write to Books.Language is NOT a producer any more. The harvest direction
//      is now resolved from the ANALYSIS language, so the stored book language is not an input to
//      the build at all — BooksController can change it without poisoning a cached set.
//      The absolute expiry above is the backstop for anything those miss.
//
// STALENESS IS A CORRECTNESS PROBLEM, NOT A COSMETIC ONE. (The pre-be-c03 header claimed a stale set
// "only changes which tokens are spared ... never correctness". That is FALSE under this feature's
// governing principle: a name this gate fails to spare is handed to the repair model, which rewrites
// it — a CORRUPTED NAME in persisted analysis prose. Staleness in the LEAVE direction is cosmetic;
// staleness in the MISSING direction is a real error, which is why the set now refreshes.)
//
// FAIL-SAFE (never throws): any fault — no book, no chapters, a DbContext error, a
// malformed stored JSON — yields an EMPTY set, which makes the classifier behave
// EXACTLY as it does today (bookEntities check simply skipped). Following the
// fail-safe-swallow-observability lesson, a swallowed fault is LOGGED (warning, with
// the bookId) rather than silently hidden; a malformed single JSON source is skipped
// without failing the whole build. Invalidate is likewise non-throwing — it runs inside
// producers' save paths, where a cache fault must never break a persist.
// ---------------------------------------------------------------------------

/// <summary>
/// Supplies a deterministic, TWO-TIER set of a book's own proper nouns (declared character names +
/// manuscript-harvested names/brands) for the <see cref="ForeignRunClassifier"/> bookEntities LEAVE lever.
/// Never throws; a fault or a book with no context yields an empty set (current behavior).
/// </summary>
public interface IBookEntityProvider
{
    /// <summary>
    /// Returns the book's proper-noun set as a <see cref="BookEntitySet"/>: DECLARED names (from stored
    /// analysis) match CASE-INSENSITIVELY; MANUSCRIPT-harvested tokens match CASE-SENSITIVELY (be-c04 — the
    /// only evidence for a harvested token is the capitalized surface form the scan saw, and a leak is
    /// lowercase by construction, so a case-insensitive match let one capitalized "Confusion" in the prose
    /// spare every lowercase "confusion" in the analysis output).
    /// <para>
    /// <paramref name="language"/> is the ANALYSIS language — the SAME value the caller threads into
    /// <c>DynamicTermRepairService</c>, which resolves the classifier's expected script from it. This provider
    /// resolves the harvest direction from that same value through the same
    /// <see cref="DynamicTermRepairService.ExpectedScriptForLanguage"/> helper, so the script the manuscript scan
    /// HARVESTS is by construction the script the classifier will LOOK UP (final-r02: keying the harvest on the
    /// book's STORED language instead let an English-language analysis of a Hebrew book harvest Latin tokens while
    /// the classifier looked up Hebrew runs, silently disarming the lever). PASS THE ANALYSIS LANGUAGE, never the
    /// book's stored language, unless they are the same thing at that seam.
    /// </para>
    /// A non-empty result is cached per (<paramref name="bookId"/>, direction) for a bounded time; an EMPTY result
    /// is never cached (it means "no harvest source exists yet", so the next call rebuilds). An empty set means "no
    /// per-book entities" and is the fail-safe on any missing context / fault — the classifier then behaves
    /// exactly as it does without this lever.
    /// </summary>
    Task<IReadOnlySet<string>> GetEntitiesAsync(Guid bookId, string? language, CancellationToken ct = default);

    /// <summary>Drops the cached set for <paramref name="bookId"/> — in EVERY language direction — so the next call
    /// rebuilds it. Called by every producer that changes a harvest source (a persisted CharacterAnalysis /
    /// BookProfile, a chapter content write); such a write changes the prose/names behind BOTH directions, so both
    /// must go. Never throws — it runs inside those producers' save paths.</summary>
    void Invalidate(Guid bookId);
}

/// <inheritdoc cref="IBookEntityProvider"/>
public sealed class BookEntityProvider : IBookEntityProvider, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BookEntityProvider> _logger;

    // Per-book cache of the derived set. Singleton lifetime + an OWNED MemoryCache so the cache actually
    // persists across analysis requests (a Scoped provider's per-instance cache would be rebuilt every
    // request) while staying BOUNDED in both size and age; the DbContext is read through a short-lived scope
    // per build so the singleton never captures a scoped DbContext. Owned rather than injected: a SizeLimit on
    // the app-wide IMemoryCache would force every other cache entry in the process to declare a Size.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxCachedBooks });

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The shared fail-safe / empty result. Empty, so its comparer is immaterial (no lookup succeeds);
    /// callers still get a non-null <see cref="IReadOnlySet{T}"/>.</summary>
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Minimum length of a manuscript-harvested token. Shorter Latin runs ("de", "van") are name
    /// particles handled by the classifier's own name-particle rule, or common function words — never harvested
    /// here. The same floor applies to the Hebrew direction, where 2-letter tokens are overwhelmingly function
    /// words (של / את / על / לא / זה ...) and a 2-letter NAME (דן) is better recovered from the declared-name
    /// source, whose floor is the lower <see cref="MinNameTokenLength"/>.
    ///
    /// This floor is DELIBERATELY HIGHER than <see cref="MinNameTokenLength"/> — do not "fix" the asymmetry by
    /// aligning them. This is the HEURISTIC harvest source (inferred from untrusted prose, not declared by
    /// anyone), so it needs a tighter floor to keep noise out: a bare 2-letter Latin token is far more likely to
    /// be an initial, an abbreviation fragment, or stray capitalization than a genuine name, and the foreign-run
    /// detector itself (<see cref="LatinInHebrewContentDetector"/>) only ever emits runs of length &gt;= 2 — a
    /// floor of 2 here would therefore admit EVERY stray 2-letter run it produces. The cost of the tighter floor:
    /// a genuine 2-letter manuscript name is missed and never enters the entity set from this source. Under the
    /// governing bias-to-LEAVE principle that is a real, if rare, cost — such a name is not spared and can reach
    /// the repair model — but it is the accepted trade-off for keeping the heuristic harvest clean.</summary>
    private const int MinManuscriptTokenLength = 3;

    /// <summary>A manuscript token recurring across at least this many distinct chapters is a name/brand signal.
    /// For the HEBREW harvest direction (a Latin-native book) this is the WHOLE gate — Hebrew has no case, so no
    /// other structural signal exists.</summary>
    private const int MinChaptersForRecurrence = 2;

    /// <summary>Minimum length of a name TOKEN taken from stored analysis. Runs shorter than 2 are already LEAVE
    /// in the classifier, so a 1-char token would never be consulted.
    ///
    /// This floor is DELIBERATELY LOWER than <see cref="MinManuscriptTokenLength"/> — the two are not supposed
    /// to match. A name here comes from a DECLARED source (a stored CharacterAnalysis result): a human or model
    /// already asserted it is an entity, so it is authoritative rather than inferred, and the noise argument
    /// that justifies the tighter manuscript floor simply does not apply. The classifier's own LEAVE rule for
    /// runs shorter than 2 (see above) makes 2 the effective minimum anyway, so this floor costs nothing — it
    /// exists only to document the intent, not to filter out reachable cases.</summary>
    private const int MinNameTokenLength = 2;

    /// <summary>Defensive upper bound on the set size. A Hebrew book has few Latin tokens, but a Latin-native
    /// book (Hebrew is the foreign script there) could otherwise harvest thousands of tokens from a manuscript
    /// whose language is mislabelled; the cap bounds memory. An over-large set is fail-safe anyway (it only ever
    /// spares MORE runs, i.e. degrades toward "no dynamic repair" = the pre-feature behaviour).</summary>
    private const int MaxEntitySetSize = 5000;

    /// <summary>Cache ceiling: how many books' sets may be held at once (each entry has Size = 1). Bounds the
    /// process-wide memory of a singleton that would otherwise accumulate an entry per book forever.</summary>
    private const int MaxCachedBooks = 128;

    /// <summary>How long an unread cached set survives. A book under active analysis keeps its set hot.</summary>
    private static readonly TimeSpan CacheSlidingExpiry = TimeSpan.FromMinutes(30);

    /// <summary>Hard staleness ceiling, even for a continuously-read book. This is the BACKSTOP behind the
    /// explicit <see cref="Invalidate"/> calls: if a future producer of a harvest source forgets to invalidate,
    /// the set is still no more than this stale (and a stale set can MISS a name, which the repair model then
    /// corrupts — see the header).</summary>
    private static readonly TimeSpan CacheAbsoluteExpiry = TimeSpan.FromHours(2);

    /// <summary>
    /// Manuscript stop-list for a HEBREW-native book (the Latin harvest direction): a tiny list of common
    /// English words (length &gt;= 3; shorter ones are already excluded by <see cref="MinManuscriptTokenLength"/>)
    /// that can appear capitalized — a backstop so the cross-chapter recurrence rule does not mistake a recurring
    /// sentence-opener ("The", "Then", "When") for a name. Applied ONLY to manuscript-harvested tokens, never to
    /// stored declared names (those are authoritative proper nouns, even when they look like a common word such
    /// as "River" or "Hope"). Case-insensitive.
    /// </summary>
    private static readonly HashSet<string> CommonEnglishWordStopList = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "but", "nor", "for", "yet", "this", "that", "these", "those",
        "there", "their", "they", "them", "then", "than", "when", "where", "which",
        "while", "with", "who", "whom", "whose", "what", "why", "how", "here",
        "she", "his", "her", "him", "its", "our", "your", "you", "not", "yes",
        "all", "any", "some", "one", "two", "three", "chapter", "part", "book",
        "said", "was", "were", "are", "been", "being", "have", "has", "had",
        "will", "would", "could", "should", "into", "from", "over", "under",
    };

    /// <summary>
    /// Manuscript stop-list for a LATIN-native book (the HEBREW harvest direction). This one is LOAD-BEARING:
    /// Hebrew has no letter case, so the only harvest signal is cross-chapter recurrence, and without a stop-list
    /// that rule would harvest ordinary prose (of a book whose language is mislabelled, or of a Hebrew passage
    /// quoted at length) and gut the cleaning gate entirely — every Hebrew run would then be LEAVE.
    ///
    /// Kept deliberately TIGHT and CLOSED-CLASS: pronouns, copulas, prepositions, conjunctions, determiners,
    /// question words and the heading words (פרק / חלק / ספר). Nothing here can plausibly be a person or place
    /// name, which matters because a stop-listed token is NOT spared — it stays repairable, and stop-listing a
    /// real name would corrupt it. Content words (nouns, verbs, adjectives) are deliberately ABSENT: harvesting
    /// one by mistake merely spares a leak (cosmetic), which is the side this feature is required to err on.
    ///
    /// Entries shorter than <see cref="MinManuscriptTokenLength"/> are inert under the current floor; they are
    /// listed anyway so the list stays correct if that floor ever changes. Common PREFIXED forms (ו/ש/כש + a
    /// function word) are included explicitly rather than by stripping prefixes — a morphological strip would
    /// mis-fire on real names (שלי -> לי).
    /// </summary>
    private static readonly HashSet<string> CommonHebrewWordStopList = new(StringComparer.OrdinalIgnoreCase)
    {
        // particles / prepositions / conjunctions (mostly 2 letters: inert under the floor, kept for correctness)
        "של", "את", "על", "אל", "עם", "כי", "גם", "אך", "או", "אם", "כל", "יש", "לא", "כן",
        "זה", "זו", "הם", "הן", "מה", "מי", "רק", "אז", "כך", "אף", "בו", "בה", "לו", "לה",
        // pronouns / copulas
        "הוא", "היא", "אני", "אתה", "אתם", "אתן", "אנחנו", "אנו", "הזה", "הזאת", "האלה",
        "אותו", "אותה", "אותם", "אותן", "להם", "להן", "שלו", "שלה", "שלהם", "שלנו",
        "היה", "היתה", "הייתה", "היו", "יהיה", "אין", "אינו", "אינה", "להיות",
        // demonstratives / quantifiers / degree
        "זאת", "אלה", "אלו", "הרבה", "מעט", "כמעט", "מאוד", "יותר", "פחות", "כלל", "עוד",
        // connectives / subordinators
        "אבל", "אשר", "כמו", "כאשר", "כדי", "לכן", "אולי", "כבר", "שוב", "ואז", "אפילו",
        "למרות", "בגלל", "כלומר", "כמובן", "תמיד", "עדיין", "בעוד", "אחרי", "לפני", "בין",
        "אצל", "מעל", "מתחת", "בתוך", "מתוך", "בלי", "ללא", "איך", "למה", "מדוע", "איפה",
        // common prefixed function words (a prefix strip would mis-fire on real names, so list them)
        "וגם", "ולא", "וכל", "וכן", "ואם", "וזה", "שלא", "שכל", "שזה", "שהוא", "שהיא",
        "כשהוא", "כשהיא", "מכל", "בכל", "ובין", "ולכן",
        // heading / structural words (the Hebrew twin of "chapter" / "part" / "book" above)
        "פרק", "חלק", "ספר", "פרולוג", "אפילוג",
    };

    public BookEntityProvider(IServiceScopeFactory scopeFactory, ILogger<BookEntityProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> GetEntitiesAsync(Guid bookId, string? language, CancellationToken ct = default)
    {
        if (bookId == Guid.Empty)
        {
            return Empty;
        }

        // THE ONE NORMALIZATION POINT (final-r02). The caller's raw analysis language is collapsed to the
        // canonical direction HERE, once, through the SAME helper the repair layer uses — and that single
        // resolved value is what the cache lookup, the cache write and the harvest all key on, so a lookup can
        // never miss a slot the write filled ("en-US" and "en" are the same entry) and the script harvested is
        // by construction the script the classifier will look up.
        var expected = DynamicTermRepairService.ExpectedScriptForLanguage(language);
        var key = CacheKey(bookId, expected);

        try
        {
            if (_cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null)
            {
                return cached;
            }

            var set = await BuildAsync(bookId, expected, ct);

            // Cache a NON-EMPTY successful build only. An EMPTY build means "no harvest source exists yet" —
            // the state of a fresh book whose CharacterAnalysis / BookProfile has not been produced yet — so
            // caching it is exactly what kept the stored-names source permanently empty in production. Rebuilding
            // an empty book is three indexed reads that return nothing.
            if (set.Count > 0)
            {
                _cache.Set(key, set, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    SlidingExpiration = CacheSlidingExpiry,
                    AbsoluteExpirationRelativeToNow = CacheAbsoluteExpiry,
                });
            }

            return set;
        }
        catch (Exception ex)
        {
            // FAIL-SAFE + observability: never throw out of the provider, but surface the swallowed fault
            // (do not blind the caller). Nothing is cached here — a transient DB fault must not poison the
            // cache; the next call retries the build.
            _logger.LogWarning(ex,
                "BookEntityProvider: failed to build the entity set for book {BookId} (expected script {Expected}); " +
                "returning an empty set (the classifier proceeds with no per-book entities = current behavior).",
                bookId, expected);
            return Empty;
        }
    }

    /// <inheritdoc/>
    public void Invalidate(Guid bookId)
    {
        if (bookId == Guid.Empty)
        {
            return;
        }

        try
        {
            // Drop EVERY direction's entry, not just one (final-r02). A producer changes the prose / the stored
            // names, which feed BOTH directions of this book; invalidating a single direction would leave the
            // other serving a set built from the pre-write manuscript. Enumerating the enum keeps this COMPLETE
            // by construction — a third ExpectedScript value would be covered without touching this method.
            foreach (var expected in Enum.GetValues<ExpectedScript>())
            {
                _cache.Remove(CacheKey(bookId, expected));
            }
        }
        catch (Exception ex)
        {
            // Non-throwing by contract: Invalidate runs inside producers' persist paths (a profile build, an
            // analysis save, a chapter save). A cache fault there must never break the save — the worst case is
            // a set that stays stale until its absolute expiry.
            _logger.LogWarning(ex, "BookEntityProvider: failed to invalidate the entity set for book {BookId}.", bookId);
        }
    }

    /// <summary>The cache key: a book's set is scoped to the DIRECTION it was harvested for. <see cref="ExpectedScript"/>
    /// (not the raw language string) is the key component precisely because it is the CANONICAL form — every locale
    /// variant of a language collapses into it, so one entry serves them all.</summary>
    private static (Guid BookId, ExpectedScript Expected) CacheKey(Guid bookId, ExpectedScript expected)
        => (bookId, expected);

    public void Dispose() => _cache.Dispose();

    // ── Build ──────────────────────────────────────────────────────────────

    /// <summary>Builds the set for ONE direction. <paramref name="expected"/> is the script the CLASSIFIER expects
    /// (resolved from the ANALYSIS language by the caller), so the manuscript scan harvests the OTHER one.
    /// <para>
    /// NOTE (final-r02): this build deliberately does NOT read <c>Books.Language</c>. The direction is an argument,
    /// not a stored property — which is what makes the harvest agree with the repair layer by construction, and
    /// also why a write to <c>Books.Language</c> (BooksController) cannot poison a cached set: the stored language
    /// is not an input to this build at all.
    /// </para></summary>
    private async Task<IReadOnlySet<string>> BuildAsync(Guid bookId, ExpectedScript expected, CancellationToken ct)
    {
        // Read through a SHORT-LIVED scope so this singleton never captures a scoped DbContext, and so the
        // read runs on its own DbContext independent of whatever scoped DbContext the caller is mid-analysis on.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var acc = new EntityAccumulator();

        // (a) stored analysis entity names — character names from AnalysisResult + BookProfile. Script-agnostic:
        // a declared name is a proper noun in WHATEVER script it is written in. DECLARED tier => matched
        // case-INSENSITIVELY (authoritative proper nouns; every casing of them is the name).
        await HarvestStoredAnalysisNamesAsync(db, bookId, acc, ct);

        // (b) manuscript scan — SCRIPT-AWARE: harvest the script that is FOREIGN for THIS ANALYSIS DIRECTION
        // (the only script whose runs the classifier will look up in this set). MANUSCRIPT tier => matched
        // case-SENSITIVELY (be-c04; the evidence for these is the capitalized surface form).
        await HarvestManuscriptTokensAsync(db, bookId, expected, acc, ct);

        return acc.Count == 0 ? Empty : new BookEntitySet(acc.Declared, acc.Manuscript);
    }

    // ── (a) stored analysis names ────────────────────────────────────────────

    private async Task HarvestStoredAnalysisNamesAsync(
        AppDbContext db, Guid bookId, EntityAccumulator acc, CancellationToken ct)
    {
        // All ACTIVE CharacterAnalysis results for the book (union across languages / runs — every name is a
        // legitimate entity, and unioning biases toward LEAVE). Newest first is immaterial to a union but keeps
        // the most recent names first if the cap ever bites.
        var characterJsons = await db.AnalysisResults
            .AsNoTracking()
            .Where(r => r.BookId == bookId
                && r.AnalysisType == AnalysisType.CharacterAnalysis
                && r.Status == AnalysisStatus.Active
                && r.StructuredResult != null)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.StructuredResult)
            .ToListAsync(ct);

        foreach (var json in characterJsons)
        {
            HarvestCharacterAnalysisJson(json, acc);
        }

        // The cached BookProfile also stores a serialized CharacterAnalysisResult (CharactersJson).
        var profileCharactersJson = await db.BookProfiles
            .AsNoTracking()
            .Where(p => p.BookId == bookId && p.CharactersJson != null)
            .Select(p => p.CharactersJson)
            .FirstOrDefaultAsync(ct);

        HarvestCharacterAnalysisJson(profileCharactersJson, acc);
    }

    /// <summary>Parses one serialized <see cref="CharacterAnalysisResult"/> and harvests the character +
    /// relationship names. Malformed JSON is skipped (fail-safe), never thrown. Collections are null-guarded:
    /// System.Text.Json nulls out a <c>= new()</c> collection on an explicit JSON null, so every list is
    /// <c>?? Empty</c>-guarded and each element null-checked before use (RepairableFields null-guard convention).</summary>
    private static void HarvestCharacterAnalysisJson(string? json, EntityAccumulator acc)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        CharacterAnalysisResult? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CharacterAnalysisResult>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return; // malformed source — skip it, do not fail the whole build
        }

        if (parsed is null)
        {
            return;
        }

        foreach (var character in parsed.Characters ?? Enumerable.Empty<CharacterEntry>())
        {
            if (character is null)
            {
                continue;
            }

            AddName(acc, character.Name);
        }

        foreach (var relationship in parsed.Relationships ?? Enumerable.Empty<CharacterRelationship>())
        {
            if (relationship is null)
            {
                continue;
            }

            AddName(acc, relationship.Character1);
            AddName(acc, relationship.Character2);
        }
    }

    /// <summary>Adds a declared proper-noun name to the DECLARED tier as its individual letter tokens (either
    /// script), so a single foreign run matching ONE token of a multi-word name ("Gogh" of "Vincent van Gogh")
    /// is spared. No stop-list is applied — a declared character name is authoritative even when it looks like a
    /// common word — and the declared tier keeps CASE-INSENSITIVE matching (be-c04): a declared name is a name in
    /// every casing the model might write it in.</summary>
    private static void AddName(EntityAccumulator acc, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        foreach (var token in TokenizeLetters(name))
        {
            if (token.Length < MinNameTokenLength)
            {
                continue;
            }

            // Skip a pure-lowercase-Latin name token ("van"/"de"/"da" of "Vincent van Gogh") — a name PARTICLE
            // the classifier already handles by its name-particle context rule; adding it as a standalone entity
            // would over-broadly LEAVE the same word outside a name. A Hebrew token (no case) or a Title-Case
            // token ("Vincent"/"Gogh") is kept.
            if (IsAllLatinLower(token))
            {
                continue;
            }

            acc.AddDeclared(token);
        }
    }

    // ── (b) manuscript scan ──────────────────────────────────────────────────

    private async Task HarvestManuscriptTokensAsync(
        AppDbContext db, Guid bookId, ExpectedScript expected, EntityAccumulator acc, CancellationToken ct)
    {
        var chapters = await db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId && c.ContentText != null && c.ContentText != "")
            .OrderBy(c => c.Order)
            .Select(c => new { c.Order, c.ContentText })
            .ToListAsync(ct);

        if (chapters.Count == 0)
        {
            return;
        }

        // token (case-insensitive key) -> aggregated proper-noun signals across the whole book.
        var stats = new Dictionary<string, TokenStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters)
        {
            ScanChapterForeignTokens(chapter.ContentText, chapter.Order, expected, stats);
        }

        // The stop-list belongs to the script being HARVESTED (the FOREIGN one for this analysis direction),
        // not to the book's own stored language.
        var stopList = expected == ExpectedScript.Hebrew ? CommonEnglishWordStopList : CommonHebrewWordStopList;

        foreach (var stat in stats.Values)
        {
            var token = stat.CanonicalForm;
            if (token.Length < MinManuscriptTokenLength)
            {
                continue;
            }

            if (stopList.Contains(token))
            {
                continue; // a recurring / mid-sentence common word — not a name
            }

            // Harvest condition:
            //   LATIN direction (Hebrew-native book): Title-Case (guaranteed — only Title-Case tokens enter
            //     stats) AND (recurs across >= 2 chapters OR appears mid-sentence at least once). Both are
            //     proper-noun signals a leaked common word (lowercase / one-off / sentence-initial only) lacks.
            //   HEBREW direction (Latin-native book): there is NO case in Hebrew, so MidSentenceCount is always 0
            //     and the expression collapses to cross-chapter RECURRENCE — the whole gate (see the header).
            //
            // be-c04 — WHY THIS CONDITION IS UNCHANGED, AND WHAT CHANGED INSTEAD.
            // The measured exposure (real 80-chapter Hebrew manuscript fixture; numbers in the plan's
            // "Investigation findings"): the fixture itself harvests 0 tokens (it is 445,727 Hebrew letters and
            // 3 Latin ones), but ONE English epigraph line appended to ONE chapter —
            //     "A story of Confusion and Nostalgia, of Tension without Catharsis."
            // — harvests 4 tokens, ALL of them via `appearsMidSentence` with MidSentenceCount == 1, and with
            // CASE-INSENSITIVE membership that flipped 3 of the 10 d5 leak seeds (confusion / nostalgia /
            // catharsis) from REPAIR to LEAVE. A 30% recall regression on the leak class this feature exists to
            // clean, bought with one sentence.
            //
            // The bug is NOT the loose harvest — it is that a CAPITALIZED observation was allowed to spare the
            // LOWERCASE form. A leak is lowercase by construction; a name's evidence is uppercase by
            // construction. So the fix is in the MATCHING (see BookEntitySet): manuscript-harvested tokens are
            // matched case-SENSITIVELY, which reduces that 3/10 regression to 0/10 while STILL sparing the
            // capitalized form the manuscript actually showed. Tightening the condition here instead was
            // rejected for two reasons:
            //   * `recurs && midSentence` is actively WRONG: MidSentenceCount is structurally 0 in the Hebrew
            //     direction (no case), so ANDing would harvest NOTHING for a Latin-native book and silently
            //     delete the entire lever be-c03 just added — the ONLY lever that can spare a Hebrew run there.
            //   * `MidSentenceCount >= 2` would drop genuine one-mention names (the "Berlin" case) for no recall
            //     gain that the case-sensitive match does not already deliver.
            var recursAcrossChapters = stat.Chapters.Count >= MinChaptersForRecurrence;
            var appearsMidSentence = stat.MidSentenceCount >= 1;
            if (recursAcrossChapters || appearsMidSentence)
            {
                acc.AddManuscript(token);
            }
        }
    }

    /// <summary>Scans one chapter's prose for the tokens of the script that is FOREIGN for this analysis direction,
    /// recording per token the distinct chapters it recurs in (and, for Latin only, whether it ever appears
    /// mid-sentence). Dispatches on <paramref name="expected"/> — the script the CLASSIFIER expects, resolved from
    /// the ANALYSIS language — so a Hebrew-expected analysis harvests Latin and a Latin-expected analysis harvests
    /// Hebrew, whatever the book's stored language happens to say.</summary>
    private static void ScanChapterForeignTokens(
        string text, int order, ExpectedScript expected, Dictionary<string, TokenStat> stats)
    {
        if (expected == ExpectedScript.Hebrew)
        {
            ScanChapterLatinTitleCaseTokens(text, order, stats);
        }
        else
        {
            ScanChapterHebrewTokens(text, order, stats);
        }
    }

    /// <summary>Scans one chapter's prose for maximal Latin letter runs that are Title-Case (first upper, rest
    /// lower — the proper-noun shape) and records, per token, the distinct chapters it recurs in and whether it
    /// ever appears mid-sentence (capitalized NOT at a sentence start = a name, not orthography). The FOREIGN
    /// direction of a HEBREW-EXPECTED analysis.</summary>
    private static void ScanChapterLatinTitleCaseTokens(string text, int order, Dictionary<string, TokenStat> stats)
    {
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            if (!IsLatinLetter(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < n && IsLatinLetter(text[i]))
            {
                i++;
            }

            var token = text.Substring(start, i - start);
            if (!IsTitleCase(token))
            {
                continue; // only proper-noun-shaped tokens are candidates
            }

            var stat = StatFor(stats, token);
            stat.Chapters.Add(order);
            if (!IsSentenceInitial(text, start))
            {
                stat.MidSentenceCount++;
            }
        }
    }

    /// <summary>Scans one chapter's prose for maximal HEBREW letter runs — the FOREIGN direction of a
    /// LATIN-EXPECTED (e.g. English) analysis. Hebrew has NO letter case, so there is no Title-Case / all-caps /
    /// sentence-initial signal to shape the candidate set: EVERY Hebrew run is a candidate and the gate is
    /// applied afterwards (cross-chapter recurrence + the Hebrew stop-list). Uses the SAME Hebrew-letter
    /// definition as <see cref="LatinInHebrewContentDetector"/>, so a harvested token is exactly a run the
    /// detector would produce (an entity that never matched a detected run would be dead weight).</summary>
    private static void ScanChapterHebrewTokens(string text, int order, Dictionary<string, TokenStat> stats)
    {
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            if (!IsHebrewLetter(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < n && IsHebrewLetter(text[i]))
            {
                i++;
            }

            var token = text.Substring(start, i - start);

            // No case signal in Hebrew: record the chapter only. MidSentenceCount stays 0, so the harvest
            // condition collapses to cross-chapter recurrence.
            StatFor(stats, token).Chapters.Add(order);
        }
    }

    private static TokenStat StatFor(Dictionary<string, TokenStat> stats, string token)
    {
        if (!stats.TryGetValue(token, out var stat))
        {
            stat = new TokenStat(token);
            stats[token] = stat;
        }

        return stat;
    }

    /// <summary>Aggregated per-token proper-noun signals from the manuscript scan.</summary>
    private sealed class TokenStat
    {
        public TokenStat(string canonicalForm) => CanonicalForm = canonicalForm;

        /// <summary>The first-seen surface form, e.g. "Kafka" / "ירושלים" (the value added to the set).</summary>
        public string CanonicalForm { get; }

        /// <summary>Distinct chapter orders the token appears in (&gt;= 2 = cross-chapter recurrence). For the
        /// Latin direction only Title-Case occurrences are counted (only they enter the scan at all).</summary>
        public HashSet<int> Chapters { get; } = new();

        /// <summary>How many times the token appears Title-Case NOT at a sentence start (&gt;= 1 = proper noun).
        /// LATIN ONLY — always 0 for a Hebrew token, which has no case and therefore no such signal.</summary>
        public int MidSentenceCount { get; set; }
    }

    // ── shared helpers ───────────────────────────────────────────────────────

    /// <summary>Collects the two MATCHING TIERS of the entity set as it is built (be-c04). Declared names are
    /// matched case-insensitively, manuscript-harvested tokens case-sensitively — see <see cref="BookEntitySet"/>
    /// for why. <see cref="MaxEntitySetSize"/> caps the COMBINED size.</summary>
    private sealed class EntityAccumulator
    {
        /// <summary>Authoritative proper nouns from stored analysis. Case-INSENSITIVE tier.</summary>
        public HashSet<string> Declared { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tokens inferred from the prose scan. Case-SENSITIVE tier — so the surface form the scan
        /// actually observed is the only one spared.</summary>
        public HashSet<string> Manuscript { get; } = new(StringComparer.Ordinal);

        public int Count => Declared.Count + Manuscript.Count;

        public void AddDeclared(string token) => Add(Declared, token);

        public void AddManuscript(string token) => Add(Manuscript, token);

        private void Add(HashSet<string> tier, string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < MinNameTokenLength)
            {
                return;
            }

            // Defensive cap on the COMBINED size: stop admitting NEW tokens past the ceiling (an already-present
            // token is a no-op).
            if (Count >= MaxEntitySetSize && !tier.Contains(token))
            {
                return;
            }

            tier.Add(token);
        }
    }

    /// <summary>Splits <paramref name="s"/> into maximal runs of letters of ANY script (Latin, Hebrew, …), so a
    /// name in either script is tokenized.</summary>
    private static IEnumerable<string> TokenizeLetters(string s)
    {
        var i = 0;
        var n = s.Length;
        while (i < n)
        {
            if (!char.IsLetter(s[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < n && char.IsLetter(s[i]))
            {
                i++;
            }

            yield return s.Substring(start, i - start);
        }
    }

}
