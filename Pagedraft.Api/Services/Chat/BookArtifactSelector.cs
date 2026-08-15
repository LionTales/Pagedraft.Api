using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// The retrieval half of chatbot phase B (c1), implementing d1 section (2)'s question-driven keyed
/// selection over BOOK artifacts.
///
/// <para>PURE AND DETERMINISTIC, for the same reason <see cref="GuideSelector"/> is: the same
/// (question, book shape) pair always produces the same keys, so the one part of phase B that can
/// regress silently - which chapter the answer was built from - is the part pinned cheapest. No model
/// call, no clock, no database, no randomness. Everything that touches the database lives in
/// <see cref="BookChatContextReader"/>, on the other side of this class.</para>
///
/// <para>A SEPARATE MECHANISM FROM <see cref="GuideSelector"/>, REUSING ITS HEBREW UTILITIES ONLY
/// (d1 section (2)). <c>GuideSelector.Score</c> scores question tokens against H1/H2 HEADINGS and the
/// frontmatter <c>id</c>/<c>stage</c>; book artifacts have neither, so that scorer has nothing to
/// score against. What IS reused is <see cref="GuideSelector.Tokenize"/> and
/// <see cref="GuideSelector.InflectionKeys"/>, because the author who writes
/// "מריצים עריכה ספרותית" about the product writes the same morphology about their own book, and a
/// second inflection implementation is a second place for the be-c02 fix to go stale.</para>
///
/// <para>WHY CHAPTER NUMBERS ARE READ OFF THE RAW QUESTION AND NOT OFF THE TOKENS.
/// <see cref="GuideSelector.Tokenize"/> drops single-character tokens, so "chapter 7" tokenizes to
/// <c>{chapter}</c> and the 7 is gone. Number detection therefore scans the raw text. This is stated
/// rather than discovered, because a reviewer reading "reuse the tokenizer" would reasonably assume
/// the numbers came through it.</para>
///
/// <para>ESCALATION IS SELECTION, NOT GENERATION (d1 section (1)). The same keys that pick briefs pick
/// which chapter's RAW text rides along, and a question that names no location never escalates - the
/// briefs stay the default so the budget is spent only where the question earns it.</para>
///
/// <para>THE AMBIENT OPEN CHAPTER (d2). An author asking about the chapter in front of them says "this
/// chapter", not "chapter 12", and none of d1's key types matched that: the owner's first real question
/// against the live API selected no chapter, escalated nothing, and was answered out of a guide while
/// the chapter's own brief sat in the prompt. The client now sends which chapter is open, and a deictic
/// or bare reference resolves against it - subject to three rules that are enforced by the SHAPE of the
/// code rather than by care: explicit beats ambient (the ambient branch is unreachable once an explicit
/// reference resolved), only a chapter-shaped question consults it at all, and the tier that resolves it
/// decides whether raw text may be spent. See <see cref="Select"/>.</para>
///
/// <para>FILE-SIZE CEILING, WAIVED DELIBERATELY (a1, re-waived by x2): this file is ~16% over the ~700-line
/// soft ceiling and is not split, because the overage is rationale rather than logic - the executable
/// body is under 200 lines - and the two candidate splits both make the code worse. Moving the
/// vocabularies out separates each word list from the paragraph explaining why that word is in it, and
/// moving the ambient rules out puts "explicit beats ambient" in a different file from the explicit
/// resolution it has to be read against, which is precisely the relationship a reviewer needs to see in
/// one place.</para>
/// </summary>
public static class BookArtifactSelector
{
    // ─── Vocabulary ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The words that introduce a chapter number, in both languages, including the Hebrew single-letter
    /// clitics that attach to the front of <c>פרק</c> (be/ke/le/me/she/vav + the definite article). A
    /// CLOSED set: this is a location vocabulary, not a stemmer, and it is matched against the raw
    /// question rather than against tokens so the number beside it survives (see the class doc).
    /// </summary>
    private static readonly string[] ChapterWords =
    {
        "chapter", "chap", "ch",
        "פרק", "בפרק", "מפרק", "לפרק", "הפרק", "ופרק", "שפרק", "פרקים"
    };

    /// <summary>Scene-reference words. A scene reference is a LOCATION CUE that resolves to its parent
    /// chapter - see <see cref="Select"/> for why it can never resolve to the scene's own text.</summary>
    private static readonly string[] SceneWords = { "scene", "סצנה", "בסצנה", "הסצנה", "תמונה" };

    /// <summary>
    /// PLURAL location words, excluded from every ambient rule (d2 section (2)). "כמה פרקים יש בספר" is a
    /// question about the whole book, so narrowing it to the one chapter that happens to be open would
    /// both answer the wrong question and spend an escalation on it. English needs no twin: "chapters" is
    /// not in <see cref="ChapterWords"/> at all, and <see cref="MatchesWordAt"/>'s trailing-letter check
    /// already refuses to match "chapter" inside "chapters".
    /// </summary>
    private static readonly string[] PluralLocationWords = { "פרקים" };

    /// <summary>
    /// The SINGULAR location vocabulary the ambient rules read: <see cref="ChapterWords"/> plus
    /// <see cref="SceneWords"/>, minus <see cref="PluralLocationWords"/>. DERIVED rather than re-typed, so
    /// a word added to the location vocabulary reaches the ambient rules automatically instead of leaving
    /// a second list to go stale.
    /// </summary>
    private static readonly string[] AmbientLocationWords = ChapterWords
        .Concat(SceneWords)
        .Where(w => !PluralLocationWords.Contains(w, StringComparer.Ordinal))
        .ToArray();

    /// <summary>
    /// DEICTIC MARKER WORDS (d2 section (2)): the closed grammatical vocabulary that turns a location word
    /// into a reference to the chapter the author is looking at right now.
    ///
    /// <para>MATCHED BY THE RAW-TEXT WHOLE-WORD SCAN, NEVER THROUGH <see cref="GuideSelector.Tokenize"/>,
    /// and that is not a style preference. <c>GuideSelector</c>'s stop list drops English "this" and
    /// Hebrew "זה"/"זו" outright, so <c>Tokenize("this chapter")</c> and <c>Tokenize("the chapter")</c>
    /// produce the SAME set, <c>{chapter}</c>, and "זה פרק" collapses to <c>{פרק}</c>. A token-set match
    /// therefore cannot tell a deictic reference from a bare mention - the same information loss this
    /// class already documents for chapter numbers, one layer up.</para>
    ///
    /// <para>NO INFLECTION TOLERANCE IS APPLIED TO THESE, deliberately: a marker is a closed grammatical
    /// vocabulary, not a content word that needs stemming, so the surface forms are enumerated instead.
    /// Inflection tolerance continues to apply to the LOCATION words and to titles/names, through the one
    /// shared <see cref="GuideSelector.InflectionKeys"/>.</para>
    ///
    /// <para>TWO FORMS BEYOND d2's TABLE, both stated rather than smuggled in: bare "זה" (d2's own prose
    /// requires it - the owner's driving question is "זה פרק שעבר עריכה...", and the table listing only
    /// "הזה" would have left exactly that question unresolved) and "הזו" (the feminine twin of "הזה";
    /// without it "הסצנה הזו" carries no marker at all, since the scene words are feminine).</para>
    /// </summary>
    private static readonly string[] DeicticMarkers =
    {
        "this", "current",
        "זה", "הזה", "זו", "הזו", "נוכחי", "הנוכחי", "שאני"
    };

    /// <summary>
    /// Positional words that name a place in the book without naming a chapter ("the first chapter",
    /// "how does it end"). They are a LOCATION CUE only: paired with a register-resolved character name
    /// they license escalation (d1 section (1)), and alone they do not.
    /// </summary>
    private static readonly (string Word, bool IsStart)[] PositionalWords =
    {
        ("first", true), ("opening", true), ("beginning", true), ("start", true), ("starts", true),
        ("last", false), ("final", false), ("ending", false), ("ends", false), ("end", false),
        ("ראשון", true), ("הראשון", true), ("ראשונה", true), ("פתיחה", true), ("בפתיחה", true),
        ("התחלה", true), ("בהתחלה", true), ("מתחיל", true),
        ("אחרון", false), ("האחרון", false), ("אחרונה", false), ("סיום", false), ("בסיום", false),
        ("סוף", false), ("בסוף", false), ("מסתיים", false)
    };

    /// <summary>
    /// The subset of <see cref="PositionalWords"/> that names WHICH CHAPTER rather than a place inside
    /// one. "the first chapter" / "בפרק האחרון" identifies a chapter as surely as a number does, so it
    /// BLOCKS the ambient substitution exactly as an out-of-range explicit number does (d2 section (3.3)):
    /// naming a chapter by position is naming a chapter, and the open one must not answer in its place.
    ///
    /// <para>DELIBERATELY THE NARROW HALF OF THE VOCABULARY. Only words that cannot mean a position INSIDE
    /// a unit are here. "opening"/"beginning"/"start"/"end" and "פתיחה"/"התחלה"/"סוף"/"סיום" plus the verb
    /// forms are left OUT, because each reads as "the end OF the chapter" at least as often as "the last
    /// chapter", and the two mistakes are not symmetric: leaving one out costs tier 3 (the open chapter's
    /// brief is preferred for ranking, no raw text is spent, nothing is asked), while putting one in
    /// wrongly costs the owner's own named failure - Show asking "which chapter?" about the chapter open on
    /// screen.</para>
    ///
    /// <para>READ ONLY BY THE AMBIENT TIERS. <see cref="PositionalCue"/> is unchanged and still reads the
    /// WHOLE vocabulary, so the character-name-plus-position escalation d1 shipped and g1/g2 measured
    /// identical 6/6 (bucket (f), which licenses Wave 3's w7) resolves exactly as before: "how does
    /// Miriam's story end" still pairs to the last chapter.</para>
    /// </summary>
    private static readonly string[] ChapterOrdinalWords =
    {
        "first", "last", "final",
        "ראשון", "הראשון", "ראשונה", "אחרון", "האחרון", "אחרונה"
    };

    /// <summary>
    /// The Hebrew surface forms of the six review dimensions, keyed to the SAME canonical slugs
    /// <see cref="BookReviewService.Dimensions"/> declares (d1 section (2) item 3). Reused rather than
    /// re-invented: a dimension question must never be able to name a seventh dimension that exists
    /// nowhere else in the product. English forms are the slugs themselves plus the inflections the
    /// tokenizer produces.
    /// </summary>
    private static readonly (string Canonical, string[] Surfaces)[] DimensionVocabulary =
    {
        ("plot",       new[] { "plot", "plots", "storyline", "עלילה", "בעלילה", "העלילה", "עלילתי" }),
        ("character",  new[] { "character", "characters", "cast", "דמות", "הדמות", "דמויות", "הדמויות", "אופי" }),
        ("pacing",     new[] { "pacing", "pace", "rhythm", "קצב", "הקצב", "בקצב", "קצביות" }),
        ("tone",       new[] { "tone", "voice", "mood", "טון", "הטון", "נימה", "הנימה", "אווירה" }),
        ("theme",      new[] { "theme", "themes", "thematic", "motif", "motifs", "נושא", "הנושא", "נושאים", "מוטיב", "מוטיבים", "תמה" }),
        ("continuity", new[] { "continuity", "consistency", "consistent", "רציפות", "עקביות", "המשכיות", "סתירה", "סתירות" })
    };

    /// <summary>
    /// Title tokens too generic to identify a chapter on their own. A book whose chapter titles are
    /// literally "פרק 3" tokenizes to <c>{פרק}</c>, and without this guard ANY question containing the
    /// word "chapter" would title-match EVERY chapter in the book - selecting the whole manuscript as
    /// the answer's grounding, which is exactly the arithmetic d1 ruled out.
    /// </summary>
    private static readonly HashSet<string> GenericTitleTokens = new(StringComparer.Ordinal)
    {
        "chapter", "part", "prologue", "epilogue", "section", "scene", "book",
        "פרק", "פרקים", "חלק", "פרולוג", "אפילוג", "מבוא", "סצנה"
    };

    // ─── Result ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One reference the question made that names more than one chapter of THIS book. Two shapes reach it,
    /// and they are the same defect through two doors:
    /// <list type="bullet">
    ///   <item>A NUMBER: two chapters both titled "פרק 8", which is what a manuscript that restarts its
    ///     numbering inside a part produces.</item>
    ///   <item>A TITLE: a book whose chapters are named for their POV character has 32 chapters titled
    ///     "רוני", and this corpus really contains one. Before w9 that question selected 19 chapters and
    ///     spent the raw-text budget on the first two of them, silently - an answer about chapters the
    ///     author did not ask about, with no hedge and no question, which is the exact failure the number
    ///     half of w9 exists to remove.</item>
    /// </list>
    /// </summary>
    /// <param name="Reference">What the author called it, ALREADY author-facing and ready to quote:
    /// "chapter 8", or a quoted title. Rendered rather than reconstructed downstream so the note cannot
    /// describe the reference differently from the way it was resolved.</param>
    /// <param name="CandidateOrders">Every chapter that claims it, ascending. Always two or more: one
    /// candidate is not an ambiguity, it is an answer.</param>
    /// <param name="ChapterNumber">The number the author WROTE, when the reference was a number; null when
    /// it was a title. It rides beside <paramref name="Reference"/> rather than replacing it because the
    /// two answer different questions and only one of them is language-free (be-c04): this selector has no
    /// language and never will - it is retrieval, keyed on the BOOK's language - so its "chapter 8" is an
    /// English literal whatever language the turn is in, and a Hebrew note quoting it raw would drop a
    /// Latin fragment into Hebrew prose. Handing the renderer the NUMBER lets it say "פרק 8" in a Hebrew
    /// answer and "chapter 8" in an English one off the same selection. A TITLE needs nothing of the kind:
    /// it is the book's own data and re-rendering it would be inventing.</param>
    public sealed record ChapterReferenceAmbiguity(
        string Reference, IReadOnlyList<int> CandidateOrders, int? ChapterNumber = null);

    /// <summary>
    /// What one question asked for, in book terms. Every list is deduped and in a DETERMINISTIC order
    /// (chapter orders ascending, character names in register order, dimensions in
    /// <see cref="BookReviewService.Dimensions"/> declaration order), so a caller can log it and a test
    /// can assert it without sorting first.
    /// </summary>
    /// <param name="ChapterOrders">Chapters the question NAMED, by <c>Chapter.Order</c> - explicitly by
    /// number, by a distinctive title match, or via a scene reference resolved to its parent chapter.</param>
    /// <param name="CharacterNames">Canonical register names (never aliases, never suppressed entries)
    /// the question resolved to.</param>
    /// <param name="Dimensions">Canonical review-dimension slugs the question named.</param>
    /// <param name="HasLocationCue">True when the question carries ANY location shape: a named chapter,
    /// a scene reference, or a positional word. This is the flag the escalation rule reads.</param>
    /// <param name="EscalationChapterOrders">The chapters whose RAW TEXT the question earned, ascending.
    /// Empty for every question that names no location - the default, by design.</param>
    public sealed record BookQuestionKeys(
        IReadOnlyList<int> ChapterOrders,
        IReadOnlyList<string> CharacterNames,
        IReadOnlyList<string> Dimensions,
        bool HasLocationCue,
        IReadOnlyList<int> EscalationChapterOrders)
    {
        public static BookQuestionKeys Empty { get; } = new(
            Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<int>());

        /// <summary>
        /// The numbers the QUESTION wrote that named MORE THAN ONE real chapter, with the candidates.
        /// Empty for every question whose number resolved to exactly one chapter, which after w9 is the
        /// overwhelmingly common case - see <see cref="Select"/> for why.
        ///
        /// <para>WHAT IT USED TO MEAN, AND WHY THE MEANING CHANGED (w9). It used to hold every number that
        /// resolved to two candidates under the OFFSET reading: a bare "chapter 5" grounded orders 4 AND 5,
        /// because <c>Chapter.Order</c> is 0-based here and authors count from 1, and the selector kept both
        /// rather than guessing. That ambiguity was manufactured by the selector, not by the author: the
        /// author reads a 1-based number on every surface in the product (<c>chapterDisplayNumber</c>) and a
        /// chapter TITLE that usually names its own number, so "chapter 5" was never two chapters to them.
        /// <see cref="Select"/> now resolves the number deterministically and this field records only
        /// ambiguity the BOOK really has - the same number naming two chapters, which happens when a
        /// manuscript restarts its numbering inside a part.</para>
        ///
        /// <para>THE CANDIDATES RIDE WITH THE REFERENCE because the note built from this has to ASK, and
        /// "did you mean chapter 8 or chapter 8" is not a question. The orders let the note name them by
        /// the one thing that separates them - where they sit in the book.</para>
        /// </summary>
        public IReadOnlyList<ChapterReferenceAmbiguity> AmbiguousChapterNumbers { get; init; }
            = Array.Empty<ChapterReferenceAmbiguity>();

        /// <summary>How many chapters the book has, so a note can say what the author's numbers RANGE over
        /// ("this book has 32 chapters") instead of only that their number missed. Zero when the selection
        /// ran against no chapters at all.</summary>
        public int ChapterCount { get; init; }

        /// <summary>
        /// Chapter numbers the question NAMED that the book does not have ("chapter 40" on a 10-chapter
        /// book), ascending. Empty in the ordinary case.
        ///
        /// <para>WHY THIS IS RECORDED RATHER THAN DISCARDED (d2 section (5)'s flagged recommendation,
        /// TAKEN). Before ambient resolution existed this state was a silent no-op: nothing entered
        /// <see cref="ChapterOrders"/> and no signal survived anywhere. With an ambient chapter in play
        /// that silence becomes dangerous, because "orders is empty" is exactly the guard the ambient
        /// branch runs under - so an author who explicitly named chapter 40 would have been answered,
        /// confidently, about whichever chapter happened to be open. It is recorded so it can do two
        /// things: BLOCK the ambient substitution (an explicit reference that failed to resolve is still
        /// an explicit reference, and explicit beats ambient), and make the question chapter-shaped for
        /// <see cref="NeedsChapterClarification"/>.</para>
        /// </summary>
        public IReadOnlyList<int> UnresolvedChapterNumbers { get; init; } = Array.Empty<int>();

        /// <summary>Which of d2 section (2)'s three tiers resolved the AMBIENT chapter, if any.
        /// <see cref="AmbientChapterMatch.None"/> whenever no ambient chapter was supplied, the question
        /// was not chapter-shaped, or an explicit reference already answered the question.</summary>
        public AmbientChapterMatch AmbientMatch { get; init; } = AmbientChapterMatch.None;

        /// <summary>The ambient chapter's order when it was actually USED, null otherwise. It is also in
        /// <see cref="ChapterOrders"/>; carried separately so a caller can log and a test can assert
        /// WHETHER the ambient key was consulted, which a list of orders cannot say.</summary>
        public int? AmbientChapterOrder { get; init; }

        /// <summary>
        /// True when Show should ASK which chapter the question is about (d2 section (5)). Computed purely
        /// from this selection, with NO model call anywhere in its derivation.
        ///
        /// <para>THE ANTI-RULE IS THIS FIELD'S OWN SHAPE, not a second rule beside it. Its condition
        /// requires BOTH <c>ChapterOrders.Count == 0</c> and <c>EscalationChapterOrders.Count == 0</c> -
        /// the two sets that carry everything a turn can be grounded in - so "a chapter resolved or its
        /// prose was carried" and "Show asks which chapter" are mutually exclusive states of ONE boolean
        /// rather than rules that can drift apart. The owner's rule - Show asking "which chapter?" while
        /// the chapter is open on screen is a failure and not a safe default - is therefore false BY
        /// CONSTRUCTION.</para>
        /// </summary>
        public bool NeedsChapterClarification { get; init; }

        /// <summary>True when the question resolved to nothing book-specific at all. The book-level
        /// brief and the statuses still ride along (they are the backbone), but no chapter, character or
        /// dimension key narrowed the selection.</summary>
        public bool IsEmpty =>
            ChapterOrders.Count == 0 && CharacterNames.Count == 0 && Dimensions.Count == 0;
    }

    /// <summary>
    /// Which of d2 section (2)'s three tiers resolved the ambient open chapter. The tier decides the
    /// SPEND: tiers 1 and 2 are escalation-eligible on the same terms as an explicit reference (d2 section
    /// (4)), tier 3 grounds and ranks only.
    /// </summary>
    public enum AmbientChapterMatch
    {
        /// <summary>No ambient resolution happened.</summary>
        None = 0,

        /// <summary>A deictic marker beside a location word: "this chapter", "בפרק הזה", "הפרק שאני
        /// עורך". Escalation-eligible.</summary>
        Deictic = 1,

        /// <summary>A bare location word plus another book-content signal - a resolved character name or a
        /// review dimension. The co-occurring signal is itself the evidence that this is a book-content
        /// question. Escalation-eligible.
        ///
        /// <para>d2 section (2) also listed a POSITIONAL word as a qualifying signal; section (3.3)
        /// forbids it, and g3 measured section (2)'s reading escalating the OPEN chapter for a question
        /// about the first/last one, 18 runs of 18. Section (3.3) wins - see <see cref="Select"/>.</para>
        /// </summary>
        BareNounWithSignal = 2,

        /// <summary>
        /// A bare location word ALONE ("what happens in the chapter", "איך מפצלים פרק"). It grounds - the
        /// ambient chapter's brief is preferred over the book-order fallback - but it does NOT escalate.
        /// The most generic phrasing is also the one most likely to be a product question, so it is the
        /// one tier whose cost is capped at ranking rather than at the raw-text budget.
        /// </summary>
        BareNounAlone = 3
    }

    /// <summary>One chapter's identity as the selector needs it. Deliberately not the EF entity: the
    /// selector must stay pure, and Order + Title is everything a key can match against.</summary>
    /// <param name="SceneTitles">The chapter's scene titles, EMPTY for the common case. Scenes are
    /// created only on demand (a user-triggered auto-split or a hand-added scene), so most chapters have
    /// none - see <see cref="Select"/>.</param>
    public sealed record ChapterRef(int Order, string? Title, IReadOnlyList<string> SceneTitles)
    {
        public ChapterRef(int order, string? title) : this(order, title, Array.Empty<string>()) { }
    }

    // ─── Selection ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts d1's three key types from <paramref name="question"/> and decides escalation.
    /// </summary>
    /// <param name="chapters">Every chapter of the book, in any order. Only Order/Title/scene titles are
    /// read.</param>
    /// <param name="register">The book's character register, ALREADY suppression-filtered by
    /// <c>CharacterRegisterMerge.ForAnalysis</c>. Passing an unfiltered register would let a name the
    /// author explicitly marked "not a character" ground an answer, which d1 forbids; this method
    /// re-checks <see cref="CharacterRegisterEntry.IsCharacter"/> anyway rather than trusting the caller,
    /// because the cost of the check is nothing and the cost of the mistake is a fabricated character.
    /// </param>
    /// <param name="ambientChapterOrder">
    /// The order of the chapter the author has OPEN on screen, or null when none is (d2 section (1)).
    ///
    /// <para>AN ORDER, NOT AN ID, BECAUSE THIS CLASS IS ORDER-KEYED THROUGHOUT: <see cref="ChapterRef"/>
    /// carries no id, the escalation set is a list of orders, and every citation ref the client parses is
    /// order-keyed. The client sends BOTH, and reconciling the id it sent against freshly-read chapter
    /// rows - so the CURRENT order is what arrives here, never the client's possibly-stale number - is
    /// <c>BookChatContextReader</c>'s job, on the impure side of this seam.</para>
    ///
    /// <para>NULL IS A FIRST-CLASS ANSWER, not an omission: "no chapter is open" is a state the client
    /// states explicitly, and it is the state that makes <see cref="BookQuestionKeys.NeedsChapterClarification"/>
    /// reachable at all.</para>
    /// </param>
    public static BookQuestionKeys Select(
        string? question,
        IReadOnlyList<ChapterRef> chapters,
        CharacterRegister? register,
        int? ambientChapterOrder = null)
    {
        if (string.IsNullOrWhiteSpace(question)) return BookQuestionKeys.Empty;

        var tokens = GuideSelector.Tokenize(question);
        var questionKeys = InflectionKeysOf(tokens);

        var orders = new SortedSet<int>();
        var validOrders = new HashSet<int>(chapters.Select(c => c.Order));

        // Chapters this question IDENTIFIED - by number, by a scene, or by a title only one chapter bears.
        // Everything in `orders` is grounded; only these are pinned down well enough to spend raw text on,
        // which is what lets a shared title be subtracted from the escalation set without also stripping a
        // chapter the same question named properly some other way.
        var identified = new HashSet<int>();
        void Identify(int order)
        {
            orders.Add(order);
            identified.Add(order);
        }

        // (1) Explicit chapter numbers, read off the RAW question (see the class doc).
        //
        // ─── THE NUMBER RESOLVES TO ONE CHAPTER, DETERMINISTICALLY (w9) ─────────────────────────
        //
        // THE DEFECT THIS REPLACES. A bare "פרק 8" used to ground BOTH order 7 and order 8, because
        // Chapter.Order is 0-based here and authors count from 1, and the selector kept both rather than
        // guessing. On the owner's real 32-chapter book that produced: two chapters retrieved for one
        // question, the ONE 3,500-token raw-text slice split between them, both of their briefs withheld
        // (raw text replaces the brief), and a hedged answer that named a chapter the author had not asked
        // about and then asked them which chapter they meant - about a chapter whose title is literally
        // "פרק 8". Retrieval was honest, but the ambiguity was MANUFACTURED HERE and the author never had
        // it: every surface in the product shows them a 1-based number (the client's single
        // chapterDisplayNumber), and their chapters carry titles that name their own number.
        //
        // SO THE TITLE THE AUTHOR READS DECIDES, AND ONLY THEN DOES COUNTING. Both rules are total and
        // neither consults the other's result, so the same (question, book) pair always resolves the same
        // way - no scoring, no tie-break, nothing a ranking change can move.
        //
        //   (a) A CHAPTER WHOSE TITLE NAMES THAT NUMBER IS THAT CHAPTER. "פרק 8" at order 7 is the
        //       author's chapter 8 no matter where it sits, which is also the only rule that survives a
        //       book whose numbering does not start at its first chapter - the prologue this corpus really
        //       has (order 31 in the owner's book), and the single-chapter imports whose one chapter is
        //       titled "פרק 24" at order 0. The old rule could not resolve that import AT ALL: neither
        //       order 23 nor order 24 exists, so it asked the author which chapter they meant about a book
        //       with exactly one.
        //   (b) OTHERWISE THE AUTHOR IS COUNTING, AND THEY COUNT FROM 1. A book whose titles are prose
        //       ("האי הנעלם") gives the number nothing to match, so it means the Nth chapter - order N-1,
        //       the client's chapterDisplayNumber read backwards. The 0-based reading is NOT kept as a
        //       second candidate, because no surface in the product ever shows an author a 0-based number;
        //       it is only reachable as a fallback when N-1 is out of range and N is not, which is the
        //       "chapter 0" edge the tests pin.
        //
        // WHAT REMAINS AMBIGUOUS IS AMBIGUOUS IN THE BOOK, NOT IN THE READING: two chapters really titled
        // "פרק 8". Then both ride and the ambiguity is recorded, so the answer ASKS which one - the one
        // case where asking is the honest move rather than a substitute for resolving.
        var ambiguous = new List<ChapterReferenceAmbiguity>();
        var unresolvedNumbers = new SortedSet<int>();
        foreach (var number in ChapterNumbersIn(question).Distinct())
        {
            var titled = chapters
                .Where(c => TitleNamesChapterNumber(c.Title, number))
                .Select(c => c.Order)
                .Distinct()
                .OrderBy(o => o)
                .ToList();

            if (titled.Count > 0)
            {
                foreach (var order in titled) Identify(order);
                if (titled.Count > 1)
                    ambiguous.Add(new ChapterReferenceAmbiguity($"chapter {number}", titled, number));
                continue;
            }

            if (validOrders.Contains(number - 1)) Identify(number - 1);
            else if (validOrders.Contains(number)) Identify(number);

            // NEITHER resolved: the author named a chapter this book does not have. Recorded rather than
            // dropped, because an explicit reference that failed is still an explicit reference and must
            // not let the ambient chapter answer in its place (see UnresolvedChapterNumbers).
            else unresolvedNumbers.Add(number);
        }

        // (2) Distinctive title matches, and scene titles resolved to the PARENT chapter.
        //
        // A TITLE THAT NAMES MANY CHAPTERS NAMES NONE OF THEM (w9). Grouping by the title STRING is what
        // separates the two cases, and they must not be confused: two DIFFERENT titles matching one
        // question ("compare The Arrival and Low Tide") is a two-chapter question and resolves to two
        // chapters, while ONE title borne by many chapters identifies nothing. A book whose chapters are
        // named for their POV character is the real instance - 32 chapters titled "רוני" - and it used to
        // select 19 of them and spend the raw-text slice on whichever two sorted first, with no hedge and
        // no question. This is the number rule's twin, keyed on a title instead of a number.
        var byTitle = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters)
        {
            if (MatchesTitle(chapter.Title, tokens, questionKeys))
            {
                var key = chapter.Title!.Trim();
                if (!byTitle.TryGetValue(key, out var sharing)) byTitle[key] = sharing = new List<int>();
                sharing.Add(chapter.Order);
            }

            foreach (var sceneTitle in chapter.SceneTitles)
            {
                if (MatchesTitle(sceneTitle, tokens, questionKeys)) Identify(chapter.Order);
            }
        }

        // Chapters reached ONLY through a title many chapters share. They still ground and rank - their
        // briefs are the best answer available to a question about "רוני" - but they must not spend the
        // raw-text budget, on the same terms as the ambient tier-3 rule below: the most generic reference
        // is the one whose cost is capped at ranking. Held separately and subtracted from the escalation
        // set AFTER it is built, so a chapter the question ALSO named by number or by a unique title keeps
        // its escalation.
        var sharedTitleOnly = new SortedSet<int>();
        foreach (var (title, sharing) in byTitle)
        {
            if (sharing.Count == 1)
            {
                Identify(sharing[0]);          // a title only one chapter bears IS an identification
                continue;
            }

            sharing.Sort();
            foreach (var order in sharing)
            {
                orders.Add(order);             // grounds and ranks, but is not an identification
                sharedTitleOnly.Add(order);
            }

            ambiguous.Add(new ChapterReferenceAmbiguity($"\"{title}\"", sharing));
        }

        var characters = ResolveCharacters(register, tokens, questionKeys);
        var dimensions = ResolveDimensions(tokens);

        var hasChapterWord = ContainsAnyWord(question, ChapterWords);
        var hasSceneWord = ContainsAnyWord(question, SceneWords);
        var positional = PositionalCue(tokens);
        var namesChapterByPosition = NamesAChapterByPosition(tokens);

        // ─── AMBIENT RESOLUTION (d2 sections (2) and (3)) ───────────────────────────────────────
        //
        // EXPLICIT BEATS AMBIENT STRUCTURALLY, NOT BY SCORE. The guard below is the whole of that rule:
        // the ambient branch is UNREACHABLE once an explicit reference resolved, so "naming chapter 5
        // while chapter 3 is open must answer about 5" needs no tie-break and cannot be lost to a ranking
        // change. The two cases never co-occur, which is also why d2 could answer "where does an ambient
        // chapter sit in the escalation drop order relative to an explicit one" with "they are mutually
        // exclusive" rather than by inventing a priority tier.
        //
        // AND AN EXPLICIT REFERENCE THAT FAILED TO RESOLVE STILL BEATS AMBIENT (see
        // UnresolvedChapterNumbers): "what happens in chapter 40" on a 10-chapter book must not be
        // answered about whichever chapter is open. That is the same rule, applied to the one state where
        // an explicit reference leaves ChapterOrders empty.
        var hasAmbientLocationWord = ContainsAnyWord(question, AmbientLocationWords);
        var hasDeicticMarker = ContainsAnyWord(question, DeicticMarkers);

        // THE POSITIONAL PAIRING IS RESOLVED BEFORE THE AMBIENT KEY IS CONSULTED, AND OUTRANKS IT
        // (d2 section (3.3)). "Does Miriam appear in the FIRST chapter?" names a chapter as surely as a
        // number does - it just names it by position - so letting the open chapter answer instead would be
        // the same wrong-chapter answer explicit-beats-ambient exists to prevent, and it would silently
        // change the one escalation shape g1 and g2 both measured identical 6/6 (bucket (f), which
        // licenses Wave 3's w7). A positional word ALONE still resolves nothing, here as before: it is a
        // location cue, not a location.
        int? positionalPairTarget = null;
        if (orders.Count == 0 && characters.Count > 0 && positional.HasValue && chapters.Count > 0)
        {
            positionalPairTarget = positional.Value
                ? chapters.Min(c => c.Order)
                : chapters.Max(c => c.Order);
        }

        var ambientMatch = AmbientChapterMatch.None;
        int? ambientUsed = null;

        if (orders.Count == 0
            && unresolvedNumbers.Count == 0
            && positionalPairTarget == null
            && ambientChapterOrder is int ambient
            && validOrders.Contains(ambient))
        {
            // The three tiers, in order. Tier 1 and tier 2 escalate; tier 3 grounds only (d2 section (2)).
            //
            // A CHAPTER ORDINAL TAKES THE QUESTION OUT OF TIERS 2 AND 3 ENTIRELY, and this is the one place
            // d2 contradicts itself, resolved in section (3.3)'s favour. Section (2)'s tier-2 list offered
            // "or a positional word" as a qualifying signal; section (3.3) - stated later, more
            // specifically, and as part of the PRECEDENCE order - says positional words are not wired to
            // the ambient key at all, "must not silently pull whichever chapter is open". g3 measured
            // section (2)'s reading live: "מה קורה בפרק האחרון?" with chapter 3 open logged
            // `sel[3] BareNounWithSignal whole[3]`, 18 runs of 18, and only the model declining kept a
            // wrong-chapter answer off the screen. Naming a chapter by position IS naming a chapter, so it
            // blocks the substitution exactly as an out-of-range explicit number does - and leaves the
            // clarifying question free to fire, which is the second half of what the author lost.
            //
            // DROPPING IT FROM TIER 2 ALONE WOULD NOT HAVE BEEN ENOUGH: the question still carries a
            // location word, so it would have fallen through to tier 3 and grounded on the open chapter
            // anyway, silently and without asking. The escalation slice stops being wasted; the wrong
            // chapter stops being preferred; the clarify fires.
            //
            // A DEICTIC MARKER STILL WINS, because tier 1 is above this: "is this the last chapter?" is a
            // question about the chapter on screen, and the marker is what says so.
            if (hasAmbientLocationWord && hasDeicticMarker)
            {
                ambientMatch = AmbientChapterMatch.Deictic;
            }
            else if (hasAmbientLocationWord && !namesChapterByPosition)
            {
                ambientMatch = characters.Count > 0 || dimensions.Count > 0
                    ? AmbientChapterMatch.BareNounWithSignal   // tier 2: another book-content signal
                    : AmbientChapterMatch.BareNounAlone;       // tier 3: bare noun, grounds without spending
            }

            if (ambientMatch != AmbientChapterMatch.None)
            {
                orders.Add(ambient);
                ambientUsed = ambient;
            }
        }

        // A question that names no location and carries no deictic marker never reaches any tier above,
        // so a chapter merely BEING open changes nothing about what is selected or what is spent. That is
        // d1's "the budget is spent only where the question earns it", surviving as a structural gate.
        var hasLocationCue = orders.Count > 0 || hasChapterWord || hasSceneWord || positional.HasValue;

        // ─── ESCALATION (d1 section (1)) ────────────────────────────────────────────────────────
        //
        // A NAMED chapter (number, title, or a scene resolved to its parent) escalates that chapter.
        // A register-resolved character name escalates ONLY when paired with a location cue, and then
        // only to the chapter that cue resolves to. A bare character question - "who is Sarah?" - does
        // NOT escalate: it answers from the register and the briefs that mention her, exactly as the
        // todo specifies.
        //
        // A SCENE REFERENCE CANNOT ESCALATE TO THE SCENE'S OWN TEXT, and d1 assumed it could. Scene
        // carries ContentSfdt and no plain-text column at all (Models/Scene.cs), so reading a scene as
        // text would mean a SECOND SFDT-to-text path, which this todo explicitly forbids. Resolving the
        // scene to its parent chapter keeps d1's intent (a scene reference is a location) at the cost of
        // a wider excerpt window, which the excerpt selector then narrows lexically anyway.
        var escalation = new SortedSet<int>(orders);

        // A SHARED TITLE GROUNDS BUT DOES NOT SPEND (w9), on the same terms as tier 3 below. "רוני" names
        // 32 chapters, so raw text for two of them is not an answer to the question - it is an answer
        // about two chapters chosen by sort order. Their briefs still ride and still rank, and the note
        // asks which one was meant. A chapter the question ALSO identified (by number, by a scene, or by a
        // title it alone bears) is exempt, which is what `identified` is for.
        foreach (var order in sharedTitleOnly)
        {
            if (!identified.Contains(order)) escalation.Remove(order);
        }

        // TIER 3 GROUNDS BUT DOES NOT SPEND. The bare singular location word alone is the highest-recall
        // and most product-question-prone trigger, so it keeps its place in `orders` (the ambient
        // chapter's brief is preferred over the book-order fallback) and is taken back out of the
        // escalation set before the raw-text budget can be committed to it.
        if (ambientMatch == AmbientChapterMatch.BareNounAlone && ambientUsed is int rankOnly)
            escalation.Remove(rankOnly);

        if (escalation.Count == 0 && positionalPairTarget is int positionalTarget)
            escalation.Add(positionalTarget);

        return new BookQuestionKeys(
            orders.ToList(), characters, dimensions, hasLocationCue, escalation.ToList())
        {
            AmbiguousChapterNumbers = ambiguous,
            UnresolvedChapterNumbers = unresolvedNumbers.ToList(),
            ChapterCount = chapters.Count,
            AmbientMatch = ambientMatch,
            AmbientChapterOrder = ambientUsed,
            // The escalation set is FINAL by this line (it is built, trimmed for tier 3, and topped up
            // with the positional pair above), so the clarify predicate can read it without moving
            // anything: no ordering changes, and f1's "escalation runs before ranking" is untouched.
            NeedsChapterClarification = NeedsClarification(
                orders.Count, escalation.Count, hasAmbientLocationWord, unresolvedNumbers.Count,
                chapters.Count)
        };
    }

    /// <summary>
    /// d2 section (5)'s clarify condition, as a named predicate so its ONE definition is testable on its
    /// own and cannot be restated differently at a second call site.
    ///
    /// <para>TWO NARROWINGS AND ONE WIDENING against d2 section (5)'s literal wording, all deliberate and
    /// all in the direction the plan's first rule points (asking is a fallback, never a substitute for
    /// answering):</para>
    /// <list type="number">
    /// <item>A MARKER WORD ALONE DOES NOT MAKE A QUESTION CHAPTER-SHAPED. d2 offered "a location word
    /// fired OR a marker word fired"; "this" and "current" are among the commonest English words, so
    /// "how do I export this?" would have produced "which chapter do you mean?" on a question that names
    /// no location at all. A marker with no location word resolves nothing, and a question that resolves
    /// nothing because it asked about nothing is not a question to interrogate.</item>
    /// <item>THE LOCATION VOCABULARY IS THE SINGULAR ONE (<see cref="AmbientLocationWords"/>), the same
    /// set the ambient tiers use, so "כמה פרקים יש בספר" cannot ask the author which chapter they mean
    /// about a question that is about all of them.</item>
    /// <item>AN OUT-OF-RANGE EXPLICIT NUMBER IS CHAPTER-SHAPED (d2's own flagged recommendation, TAKEN):
    /// "chapter 40" on a 10-chapter book is a state that used to be absorbed in silence, and asking is
    /// strictly more honest than answering as though no chapter had been named.</item>
    /// </list>
    ///
    /// <para>AND A BOOK WITH AT MOST ONE CHAPTER NEVER ASKS. There is nothing to disambiguate, the
    /// book-order fallback already grounds in the only chapter there is, and a clarifying question on a
    /// one-chapter book - the owner's real book is exactly that shape - would be absurd. Enforced here
    /// rather than left to the client, so it is impossible on both halves rather than hidden on one.</para>
    ///
    /// <para>AND RAW TEXT THAT WAS CARRIED SILENCES IT TOO (g3's finding (g-2)). The predicate used to
    /// read the RESOLVED set alone, and there is exactly one state where the two disagree: the positional
    /// pair ("does Miriam appear in the first chapter?") escalates a chapter without ever entering it into
    /// <c>ChapterOrders</c>. g3 measured the consequence - a complete, correct answer built from that
    /// chapter's own prose, with "which chapter did you mean?" chips rendered underneath it, 3 runs of 3.
    /// A question answered from a chapter's own prose is not a question that needs asking, so the
    /// escalation set silences the flag on the same terms the resolved set does. This ADDS a condition
    /// that makes the flag false; it relaxes none, so the anti-rule ("never ask when anything resolved")
    /// stays true by construction and gets strictly harder to violate.</para>
    /// </summary>
    /// <param name="escalatedChapterCount">Chapters whose RAW TEXT this question earned - the INTENT to
    /// escalate, which is the same thing <c>BookChatContextReader</c> keys its own budget on.</param>
    internal static bool NeedsClarification(
        int resolvedChapterCount, int escalatedChapterCount, bool hasAmbientLocationWord,
        int unresolvedNumberCount, int chapterCount)
        => resolvedChapterCount == 0
           && escalatedChapterCount == 0
           && chapterCount > 1
           && (hasAmbientLocationWord || unresolvedNumberCount > 0);

    // ─── Key extraction helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every number that follows (or immediately precedes) a chapter word in the raw question. Scanning
    /// the raw text is deliberate: the tokenizer drops single-character tokens, so "chapter 7" would
    /// otherwise lose its 7.
    /// </summary>
    internal static IReadOnlyList<int> ChapterNumbersIn(string question)
    {
        var found = new List<int>();

        for (var i = 0; i < question.Length; i++)
        {
            if (!IsWordStart(question, i)) continue;

            foreach (var word in ChapterWords)
            {
                if (!MatchesWordAt(question, i, word)) continue;

                // "chapter 7" / "פרק 7" - the number after the word.
                if (TryReadNumberNear(question, i + word.Length, forward: true, out var after)) found.Add(after);

                // "7 chapter" is not English, but "בפרק 7" already matched above and a leading number
                // ("3rd chapter") is worth catching too; reading BACKWARD costs one scan and no ambiguity.
                if (TryReadNumberNear(question, i - 1, forward: false, out var before)) found.Add(before);

                break;   // one chapter word, one match: "בפרק" must not also match "פרק"
            }
        }

        return found;
    }

    /// <summary>
    /// True when <paramref name="title"/> CLAIMS chapter number <paramref name="number"/> - "פרק 8",
    /// "Chapter 8", "פרק 1 - חזרה לנווה־חול", or a title that is nothing but the number.
    ///
    /// <para>IT READS THE TITLE WITH <see cref="ChapterNumbersIn"/>, THE SAME SCANNER THAT READS THE
    /// QUESTION, and that symmetry is the point: the author types the name they see, so whatever counts as
    /// "names chapter 8" on one side has to count on the other, and one scanner cannot drift from itself.
    /// It is also why this cannot be done through <see cref="MatchesTitle"/>, which is the reason the
    /// defect survived: <c>GuideSelector.Tokenize</c> drops single-character tokens, so "פרק 8" tokenizes
    /// to <c>{פרק}</c> with the 8 gone, and <c>פרק</c> is in <see cref="GenericTitleTokens"/> - the title
    /// was not merely unmatched, it was correctly refused as generic. The number is the distinctive part
    /// of a title like this, and it is the part the tokenizer cannot carry.</para>
    ///
    /// <para>A BARE NUMERIC TITLE ("8") COUNTS TOO. It is what a manuscript whose headings are just digits
    /// imports as, and it can only be read as a chapter number. A year used as a title ("1948") is
    /// technically reachable by the same line, and harmlessly: it can only match a question that asked
    /// about chapter 1948.</para>
    /// </summary>
    internal static bool TitleNamesChapterNumber(string? title, int number)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        var trimmed = title.Trim();
        if (int.TryParse(trimmed, out var bare) && bare == number) return true;

        return ChapterNumbersIn(trimmed).Contains(number);
    }

    /// <summary>
    /// True when <paramref name="title"/> names a chapter number AT ALL - the same reading as
    /// <see cref="TitleNamesChapterNumber"/> with the specific number taken out of the question.
    ///
    /// <para>IT EXISTS BECAUSE TWO DIFFERENT QUESTIONS WERE BEING ASKED WITH ONE PREDICATE. The selector
    /// asks "does this title claim chapter N", which needs the number. A RENDERER deciding whether to
    /// append a chapter's position to its title asks "has the author already numbered this chapter", which
    /// does not, and answering it with the numbered form silently keys the decision on the position: a
    /// bare-digit title whose value happened to differ from its own position ("8" at order 8, or the "24"
    /// of a single-chapter re-import) failed the numbered check and got the position appended -
    /// <c>המחבר קורא לפרק הזה: 24 (פרק 1)</c>, on the chapter the selector had retrieved BECAUSE the author
    /// called it 24. Both halves are covered here: a title that is nothing but digits, and a title carrying
    /// a chapter word with a number near it.</para>
    ///
    /// <para>THE BARE BRANCH CARRIES <see cref="TryReadNumberNear"/>'s PLAUSIBILITY CLAMP, WHICH IS WHAT
    /// MAKES THE SENTENCE ABOVE TRUE (final-r01). Without it the two predicates were not the same reading:
    /// the numbered form can only ever be handed a value <see cref="ChapterNumbersIn"/> produced, and that
    /// scanner refuses anything outside 0..9999 as "a number a book could not plausibly have", while a bare
    /// <c>int.TryParse</c> accepts <c>-5</c>, <c>99999</c> and a date used as a heading (<c>20260815</c>).
    /// A caller keyed on this predicate must not treat those as "the author numbered this chapter" when the
    /// selector would never resolve them by title - that asymmetry silently costs the chapter its
    /// author-facing position in the one line the model is told to copy. An in-range year (<c>1948</c>) is
    /// still indistinguishable from a chapter number here and is read as one; separating those needs the
    /// book's chapter count, which this predicate deliberately does not take.</para>
    /// </summary>
    internal static bool TitleNamesAnyChapterNumber(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        var trimmed = title.Trim();
        if (int.TryParse(trimmed, out var bare) && bare is >= 0 and <= 9999) return true;

        return ChapterNumbersIn(trimmed).Count > 0;
    }

    /// <summary>
    /// True when <paramref name="title"/> is DISTINCTIVE (it carries at least one token that is not a
    /// generic structural word) and every one of its tokens is present in the question, exactly or by
    /// Hebrew inflection.
    ///
    /// <para>Requiring ALL of the title's tokens, not some, is what keeps this from selecting half the
    /// book: a two-word question sharing one word with a six-word title is not a chapter reference.</para>
    /// </summary>
    internal static bool MatchesTitle(
        string? title, IReadOnlySet<string> questionTokens, IReadOnlySet<string> questionKeys)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        var titleTokens = GuideSelector.Tokenize(title);
        if (titleTokens.Count == 0) return false;

        var distinctive = false;
        foreach (var token in titleTokens)
        {
            if (!GenericTitleTokens.Contains(token)) distinctive = true;

            if (questionTokens.Contains(token)) continue;
            if (GuideSelector.MatchesByInflection(token, questionKeys)) continue;

            return false;
        }

        return distinctive;
    }

    /// <summary>
    /// The canonical names of register entries the question named, by name or by alias, in REGISTER
    /// order. Suppressed entries (<c>IsCharacter == false</c>) are excluded from matching entirely: the
    /// author already said "this is not a character", so the name must not even partially ground an
    /// answer.
    /// </summary>
    internal static IReadOnlyList<string> ResolveCharacters(
        CharacterRegister? register, IReadOnlySet<string> questionTokens, IReadOnlySet<string> questionKeys)
    {
        if (register == null || register.Characters.Count == 0 || questionTokens.Count == 0)
            return Array.Empty<string>();

        var matched = new List<string>();

        foreach (var entry in register.Characters)
        {
            if (!entry.IsCharacter) continue;                       // permanent suppression (Issue 3)
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            if (NameMatches(entry.Name, questionTokens, questionKeys)
                || entry.Aliases.Any(a => NameMatches(a, questionTokens, questionKeys)))
            {
                matched.Add(entry.Name);
            }
        }

        return matched;
    }

    /// <summary>
    /// A name matches when EVERY token of it appears in the question (a two-word name needs both words,
    /// so "Sarah Cohen" is not matched by a question about a different Sarah... and IS matched by a
    /// question naming her in full). Single-token names match on that token alone.
    /// </summary>
    private static bool NameMatches(
        string? name, IReadOnlySet<string> questionTokens, IReadOnlySet<string> questionKeys)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var nameTokens = GuideSelector.Tokenize(name);
        if (nameTokens.Count == 0) return false;

        foreach (var token in nameTokens)
        {
            if (questionTokens.Contains(token)) continue;
            if (GuideSelector.MatchesByInflection(token, questionKeys)) continue;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The canonical dimension slugs the question named, in <see cref="BookReviewService.Dimensions"/>
    /// declaration order so the result is stable regardless of word order in the question.
    /// </summary>
    internal static IReadOnlyList<string> ResolveDimensions(IReadOnlySet<string> questionTokens)
    {
        if (questionTokens.Count == 0) return Array.Empty<string>();

        var matched = new List<string>();

        foreach (var canonical in BookReviewService.Dimensions)
        {
            var entry = DimensionVocabulary.FirstOrDefault(d => d.Canonical == canonical);
            if (entry.Surfaces == null) continue;

            if (entry.Surfaces.Any(questionTokens.Contains)) matched.Add(canonical);
        }

        return matched;
    }

    /// <summary>
    /// The surface forms of one canonical dimension slug, in BOTH languages, or just the slug itself for
    /// a canonical this vocabulary does not carry.
    ///
    /// <para>WHY A CONSUMER NEEDS THE SURFACES AND NOT THE SLUG. A dimension resolved from a question is
    /// CANONICAL (<c>pacing</c>), because that is what <see cref="BookReviewService.Dimensions"/> declares
    /// and what a <c>BookFinding.Dimension</c> is stamped with. But a chapter brief's
    /// <c>ThematicMarkers</c> are MODEL-WRITTEN PROSE in the BOOK's language, so matching them against the
    /// canonical slug can only ever succeed on an English book: <c>"קצב".Contains("pacing")</c> is false,
    /// and every Hebrew book therefore ranked its chapter briefs as though no dimension had been named.
    /// Comparing against the surfaces is what makes the two sides speak the same language.</para>
    ///
    /// <para>Its sibling comparison is correct as it stands and must not be "fixed" to match: a finding's
    /// <c>Dimension</c> is a canonical slug written by the review, not prose, so it compares slug to slug.
    /// The distinction is whether the other side of the comparison is CONTENT or a KEY.</para>
    /// </summary>
    internal static IReadOnlyList<string> SurfacesOf(string canonical)
    {
        var entry = DimensionVocabulary.FirstOrDefault(d => d.Canonical == canonical);
        return entry.Surfaces ?? new[] { canonical };
    }

    /// <summary>
    /// Whether a chapter brief's thematic marker NAMES <paramref name="canonical"/>, matched as WHOLE
    /// TOKENS and never as a substring.
    ///
    /// <para>THE SUBSTRING VERSION OF THIS WAS A CR BOT FINDING AND IT DESERVED TO BE. The vocabulary
    /// carries short stems - <c>pace</c>, <c>cast</c>, <c>mood</c>, <c>tone</c>, <c>consistent</c> - and
    /// a marker is ordinary manuscript prose, so <c>Contains</c> matched <c>space</c>, <c>outcast</c>,
    /// <c>inconsistent</c> and <c>stone</c>. The cost is not noise, it is INVERTED SELECTION: one false
    /// hit keys that brief, and <c>RankChapterBriefs</c> then drops every chapter that keyed nothing, so
    /// a pacing question would ground in the one chapter that happened to mention a space station and in
    /// no chapter that is actually about pacing.</para>
    ///
    /// <para>Tokens make this SYMMETRIC with how the question side already resolves a dimension
    /// (<see cref="ResolveDimensions"/> tests surfaces against question tokens), so both ends of the
    /// comparison now obey one rule instead of two. Hebrew keeps its inflection tolerance through the
    /// one shared <see cref="GuideSelector.InflectionKeys"/>, so a marker written <c>הקצב</c> still
    /// names <c>pacing</c>.</para>
    /// </summary>
    internal static bool MarkerNamesDimension(string? marker, string canonical)
    {
        if (string.IsNullOrWhiteSpace(marker)) return false;

        var tokens = GuideSelector.Tokenize(marker);
        if (tokens.Count == 0) return false;

        var keys = InflectionKeysOf(tokens);

        foreach (var surface in SurfacesOf(canonical))
        {
            if (tokens.Contains(surface)) return true;
            if (GuideSelector.MatchesByInflection(surface, keys)) return true;
        }

        return false;
    }

    /// <summary>The positional cue the question carries, if any: true = start of the book, false = end.
    /// Null when it names no position. The FIRST cue in vocabulary order wins, so a question carrying
    /// both is resolved deterministically rather than by word order.</summary>
    internal static bool? PositionalCue(IReadOnlySet<string> questionTokens)
    {
        foreach (var (word, isStart) in PositionalWords)
        {
            if (questionTokens.Contains(word)) return isStart;
        }

        return null;
    }

    /// <summary>
    /// True when the question names a chapter BY POSITION ("the first chapter", "בפרק האחרון") rather than
    /// a place inside one ("how does the chapter end"). Token-matched, exactly as
    /// <see cref="PositionalCue"/> reads the same question, so the two cannot disagree about what the
    /// question said - only about what it means. See <see cref="ChapterOrdinalWords"/> for why the
    /// vocabulary is the narrow half.
    /// </summary>
    internal static bool NamesAChapterByPosition(IReadOnlySet<string> questionTokens)
        => ChapterOrdinalWords.Any(questionTokens.Contains);

    // ─── Small primitives ───────────────────────────────────────────────────────────────────────

    private static IReadOnlySet<string> InflectionKeysOf(IReadOnlySet<string> tokens)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            foreach (var key in GuideSelector.InflectionKeys(token)) keys.Add(key);
        }

        return keys;
    }

    private static bool ContainsAnyWord(string text, IReadOnlyList<string> words)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsWordStart(text, i)) continue;

            foreach (var word in words)
            {
                if (MatchesWordAt(text, i, word)) return true;
            }
        }

        return false;
    }

    private static bool IsWordStart(string text, int index)
        => index == 0 || !char.IsLetterOrDigit(text[index - 1]);

    /// <summary>Case-insensitive whole-word match of <paramref name="word"/> at
    /// <paramref name="index"/>. Whole-word on BOTH sides, so "ch" does not match inside "chapter"
    /// and "פרק" does not match inside "פרקליט".</summary>
    private static bool MatchesWordAt(string text, int index, string word)
    {
        if (index + word.Length > text.Length) return false;

        for (var i = 0; i < word.Length; i++)
        {
            if (char.ToLowerInvariant(text[index + i]) != char.ToLowerInvariant(word[i])) return false;
        }

        var after = index + word.Length;

        // A trailing '.' ("ch.") is scaffolding, not a letter, so it does not break the word boundary.
        return after >= text.Length || !char.IsLetter(text[after]);
    }

    /// <summary>
    /// Reads the first integer within a few characters of <paramref name="from"/>, skipping only
    /// separators (space, punctuation) - never letters, so "chapter about Sarah 3" does not read the 3.
    /// </summary>
    private static bool TryReadNumberNear(string text, int from, bool forward, out int value)
    {
        value = 0;
        const int maxSkip = 3;

        var i = from;
        var skipped = 0;
        while (i >= 0 && i < text.Length && !char.IsDigit(text[i]))
        {
            if (char.IsLetter(text[i])) return false;
            if (++skipped > maxSkip) return false;
            i += forward ? 1 : -1;
        }

        if (i < 0 || i >= text.Length) return false;

        // Walk to the START of the digit run either way, then read it forward.
        var start = i;
        while (start > 0 && char.IsDigit(text[start - 1])) start--;

        var end = start;
        while (end < text.Length && char.IsDigit(text[end])) end++;

        // A number a book could not plausibly have is not a chapter reference (a year, an ISBN).
        return int.TryParse(text.AsSpan(start, end - start), out value) && value is >= 0 and <= 9999;
    }
}
