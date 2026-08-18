namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// WHAT KIND OF QUESTION THIS TURN IS. Today the answer is always <see cref="Union"/>, because
/// <c>ProductChat:RoutingEnabled</c> defaults to false and <c>ProductChatService</c> forces the route
/// while it is off; the other three exist so g2 has somewhere to hang a per-route prompt.
/// </summary>
public enum ChatRoute
{
    /// <summary>A question about PageDraft itself: its screens, its passes, its import and export. The
    /// guides are the grounding and the guides-only contract applies.</summary>
    Product,

    /// <summary>A question about the author's own manuscript. The BOOK section is the grounding.</summary>
    Book,

    /// <summary>A writing or literature question that is about neither. g2 gives it a block that lets
    /// Show answer from his own knowledge instead of narrating which source failed him.</summary>
    General,

    /// <summary>
    /// EVERYTHING ELSE, AND THE FALLBACK OF THE WHOLE LAYER. Anything mixed, anything unmatched, and
    /// anything at all while the flag is off resolves here.
    ///
    /// <para>UNION USED TO BE DEFINED AS "BYTE-IDENTICAL TO WHAT SHIPPED BEFORE ROUTING EXISTED", AND IT IS
    /// NOT ANY MORE. g3 measured its book-less arm telling five real turns that "answering questions about
    /// a specific book is not available yet and is coming" - a sentence that stopped being true when Show
    /// learned to read the book in phase B, on two of them a plain product question. A false sentence
    /// cannot be a safety property, so g3 replaced it with the same "I can only see a book while it is
    /// open" that the deterministic path already answers with, and the byte-identity claim is retired
    /// rather than quietly narrowed. Everything else Union composes is unchanged, and it is still the
    /// route a misroute lands on.</para>
    /// </summary>
    Union,
}

/// <summary>
/// The deterministic question router for Show (g1, plan item 8d stage one).
///
/// <para>DETERMINISTIC AND NOT AN LLM PRE-CALL, for two reasons that are both measurements in this
/// workspace. With <c>OLLAMA_NUM_PARALLEL=1</c> on an 8GB GPU a classifier call SERIALIZES in front of
/// every answer, so the router would cost the author a second model round-trip on every turn. And a
/// deterministic router is testable to the byte, which is what lets the owner's real failures below be
/// named regression cases rather than anecdotes.</para>
///
/// <para>THE BOOK-CONTENT LEXICON IS NOT WRITTEN HERE. It is
/// <see cref="BookArtifactSelector.BookContentWords"/>, DERIVED from the vocabularies that already drive
/// book retrieval and escalation, and it is matched with
/// <see cref="BookArtifactSelector.ContainsAnyWord"/>, the same whole-word raw-text scanner the selector
/// uses. Two copies of a question vocabulary WILL drift, and the drift would be invisible: the router
/// would route Book while the selector retrieved nothing, or the reverse. The product and craft
/// lexicons below are new, because nothing in the codebase owned them.</para>
///
/// <para>WHAT THIS ROUTER CANNOT SEE, STATED SO IT IS NOT DISCOVERED LATER. It has no character
/// register, so a question that names the manuscript's cast and nothing else ("how do I strengthen the
/// romance between Ruby and Flo?") carries NO book-content token and does not route Book. That is the
/// owner's own first failure in conversation 6802c061, and it resolves to <see cref="ChatRoute.Union"/>
/// here - the status quo, which is the honest answer for a signal the router does not have. It is pinned
/// as a named case in <c>ProductChatRouterTests</c> so the blind spot is a fact of record rather than a
/// surprise; closing it needs the register, which lives behind a database read and would make this class
/// impure.</para>
///
/// <para>PURE. No model, no clock, no filesystem, no database, no randomness. Same inputs, same route,
/// always.</para>
/// </summary>
public static class ProductChatRouter
{
    /// <summary>
    /// The <see cref="GuideSelector"/> top score at or above which the guides are taken to be a real
    /// answer to this question rather than the top of a weak field.
    ///
    /// <para>CALIBRATED BY g3, AND THE ONE NUMBER IN THIS FILE THAT IS. It shipped at 6.0 - two exact
    /// heading-token matches (<see cref="GuideSelector.HeadingWeight"/> is 3.0), which was a description of
    /// the SCORING and not a measurement of any question. g3 then ran 102 real turns and 6.0 turned out to
    /// be exactly the score a general CRAFT question reaches off the guides' incidental vocabulary: four of
    /// the eight English craft questions scored 6.0 to the digit, routed <see cref="ChatRoute.Product"/>,
    /// and were all four refused as product questions the guides do not cover. No question in the run
    /// scored between 6.0 and 7.0, and every product question in the run hit
    /// <see cref="ProductSurfaceWords"/> independently of its score, so raising the bar to 7.0 moves those
    /// four turns to <see cref="ChatRoute.General"/> and moves nothing else at all.</para>
    ///
    /// <para>DELIBERATELY ONLY A POSITIVE SIGNAL, unchanged. A score at or above this adds Product; a score
    /// below it removes nothing, because the selector is contracted never to decide "no coverage"
    /// (<see cref="GuideSelector"/>, "THE SELECTOR NEVER DECIDES NO COVERAGE") and a router that read a
    /// weak score as a refusal would be making exactly the decision that contract reserves for the
    /// model.</para>
    /// </summary>
    public const double StrongGuideTopScore = 7.0;

    /// <summary>
    /// PRODUCT-SURFACE VOCABULARY: the things PageDraft has, as an author would name them. NEW, because
    /// no existing class owned a product lexicon - <see cref="GuideSelector"/> scores against guide
    /// headings rather than against a word list.
    ///
    /// <para>"guide"/"מדריך" IS DELIBERATELY ABSENT. The owner's third turn in conversation 6802c061 ends
    /// "לא במדריכים" - "not in the guides" - which is a question ABOUT THE BOOK that happens to name the
    /// guides in order to rule them out. A lexicon containing the word would route that turn Product,
    /// which is the exact inversion of what the author asked for. Whether the guides can answer is what
    /// <see cref="StrongGuideTopScore"/> is for, and it reads the corpus rather than the noun.</para>
    ///
    /// <para>Matched WHOLE-WORD against the raw question, so Hebrew clitics are enumerated as surfaces
    /// (the same convention <see cref="BookArtifactSelector"/> uses for <c>פרק</c>/<c>בפרק</c>) rather
    /// than stemmed. A closed list, not a stemmer.</para>
    ///
    /// <para>THE LIST ITSELF IS ASSEMBLED FOUR PIECES DOWN, at <see cref="ProductSurfaceWords"/>: the
    /// English literals below, the Hebrew verbs, and the Hebrew nouns crossed with their clitics minus the
    /// two cells that are ordinary Hebrew. Read those four for what is in it and why.</para>
    /// </summary>
    private static readonly string[] ProductSurfaceWordsEnglish =
    {
        "pagedraft", "import", "importing", "imported", "export", "exporting", "exported", "exports",
        "docx", "epub", "pdf", "upload", "download", "button", "buttons", "screen", "panel", "dashboard",
        "settings", "setting", "app", "editor", "toolbar", "menu", "shortcut", "shortcuts", "keyboard",
        "login", "account", "subscription", "proofread", "sidebar", "tab", "tabs",
    };

    /// <summary>
    /// The Hebrew product NOUNS, bare. Every clitic-prefixed surface an author writes is generated from
    /// these by <see cref="HebrewProductClitics"/>; nothing here carries a prefix of its own.
    /// </summary>
    private static readonly string[] HebrewProductNouns =
    {
        "ייבוא", "ייצוא", "כפתור", "כפתורים", "מסך", "מסכים", "חלונית", "פאנל",
        "הגדרות", "הגדרה", "אפליקציה", "עורך", "סרגל", "תפריט", "קיצור", "קיצורים",
        "מקלדת", "התחברות", "חשבון", "מנוי", "מערכת", "תוכנה", "לשונית", "הגהה",
    };

    /// <summary>
    /// The Hebrew VERB surfaces, which take none of these clitics and are therefore listed literally.
    /// </summary>
    private static readonly string[] HebrewProductVerbs =
    {
        "לייבא", "מייבא", "מייבאים", "לייצא", "מייצא", "מייצאים",
    };

    /// <summary>
    /// THE SINGLE-LETTER CLITICS THAT ATTACH TO THE FRONT OF A HEBREW NOUN, and the g3 defect they close.
    /// Whole-word matching is exact, so <c>מסך</c> does NOT match inside <c>במסך</c> and <c>מערכת</c> does
    /// not match inside <c>במערכת</c>; g3 measured two plain product questions
    /// ("איפה במסך רואים את סטטוס העריכה?", "איך מתחילים ספר חדש במערכת?") carrying no product signal at
    /// all for exactly that reason and falling to <see cref="ChatRoute.Union"/>, where they were answered
    /// with a refusal.
    ///
    /// <para>The convention is <see cref="BookArtifactSelector"/>'s own for <c>פרק</c>/<c>בפרק</c>: a
    /// CLOSED enumeration of surfaces, not a stemmer. It is generated rather than typed out because the
    /// noun list is 24 long and a hand-typed cross product is a list that goes stale one noun at a
    /// time.</para>
    /// </summary>
    private static readonly string[] HebrewProductClitics = { "", "ה", "ב", "ל", "מ", "מה", "ש" };

    /// <summary>
    /// EXPANDED SURFACES THAT ARE ORDINARY HEBREW, REMOVED BY HAND. The cross product above is mechanical
    /// and two of its cells are common words that have nothing to do with the product, so they are excluded
    /// explicitly - listed rather than folded into the generator, so the exclusion is auditable the way the
    /// generator is not.
    ///
    /// <list type="bullet">
    ///   <item><c>בחשבון</c> is the idiom "לקחת בחשבון" (to take into account), which occurs in ordinary
    ///     craft prose and would route a craft question Product.</item>
    ///   <item><c>מחשבון</c> is the noun "calculator".</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> HebrewProductSurfaceExclusions =
        new(StringComparer.Ordinal) { "בחשבון", "מחשבון" };

    /// <summary>THE PRODUCT LEXICON AS MATCHED. See <see cref="ProductSurfaceWordsEnglish"/> for what this
    /// vocabulary is for and what is deliberately absent from it.</summary>
    private static readonly string[] ProductSurfaceWords = ProductSurfaceWordsEnglish
        .Concat(HebrewProductVerbs)
        .Concat(
            from noun in HebrewProductNouns
            from clitic in HebrewProductClitics
            select clitic + noun)
        .Where(w => !HebrewProductSurfaceExclusions.Contains(w))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// WRITING-CRAFT VOCABULARY: the general questions about writing that Show should be allowed to
    /// answer from his own knowledge, rather than reporting which of his sources failed to cover them.
    ///
    /// <para>IT SHARES NO WORD WITH THE BOOK LEXICON, ON PURPOSE. The six review dimensions ("plot",
    /// "character", "pacing", "tone", "theme", "continuity" and their Hebrew surfaces) are craft words
    /// too, and they are already in <see cref="BookArtifactSelector.BookContentWords"/> because they
    /// drive retrieval. Repeating them here would make every dimension question ambiguous between two
    /// routes; leaving them out means a dimension question with a book open routes Book, which is where
    /// the artifacts that answer it actually are.</para>
    ///
    /// <para>It shares no word with <see cref="ProductSurfaceWords"/> either, which is why "style" and
    /// "סגנון" appear in neither: PageDraft has a Style Baseline, so the word names a product surface and
    /// a craft topic at once, and a token that means two routes is worth less than the two routes.</para>
    /// </summary>
    private static readonly string[] WritingCraftWords =
    {
        // English
        "write", "writing", "writes", "rewrite", "dialogue", "dialog", "metaphor", "metaphors", "simile",
        "prose", "narrator", "narration", "foreshadowing", "exposition", "subtext", "backstory", "genre",
        "novel", "fiction", "storytelling", "craft", "romance", "romantic", "tension", "conflict",
        "climax", "suspense", "imagery", "adjective", "adjectives", "adverb", "adverbs", "sentence",
        "sentences", "paragraph", "paragraphs",
        // Hebrew
        "כתיבה", "הכתיבה", "לכתוב", "כותבים", "כותב", "לשכתב", "דיאלוג", "הדיאלוג", "דיאלוגים",
        "מטאפורה", "מטאפורות", "דימוי", "דימויים", "פרוזה", "נרטיב", "רמיזה", "ז'אנר",
        "רומן", "רומנטי", "הרומנטי", "רומנטית", "הרומנטית", "מתח", "המתח", "קונפליקט", "שיא",
        "תיאור", "התיאור", "תיאורים", "משפט", "משפטים", "פסקה", "פסקאות",
        "ספרות", "ספרותי", "ספרותית", "ספרותיים",
    };

    /// <summary>
    /// VERBS THAT ACT ON A CHAPTER AS AN OBJECT OF THE APP rather than as a place with contents (g3):
    /// adding one, deleting one, splitting one. Paired with a
    /// <see cref="BookArtifactSelector.BookLocationWords"/> hit they are the shape g1 named as a residual
    /// and g3 then measured failing 4 of 6 - "How do I add a chapter?" and "איך מוחקים פרק?" were answered
    /// "open the book you are asking about", because a location word with no other family is what
    /// <see cref="AsksAboutABookThatIsNotOpen"/> keys on and nothing distinguished the two uses of the
    /// word.
    ///
    /// <para>THE CONJUNCTION IS THE WHOLE DESIGN, AND WITHOUT IT THIS LIST WOULD BE A NEW DEFECT. A
    /// manipulation verb ALONE is not a product signal: "how do I create a convincing villain?" carries
    /// "create" and is a craft question, and routing it Product is the exact failure this round exists to
    /// remove. It is only a product signal when it is applied to a manuscript LOCATION, because that is the
    /// combination that can only mean the app's own chapter list.</para>
    ///
    /// <para>WHAT IS DELIBERATELY OUT. English "move"/"moving" and Hebrew "משנים" are generic enough to
    /// appear in ordinary craft prose. The singular participles "מוחק"/"מוסיף" are out because they are the
    /// form a CHARACTER takes in a book question ("מה אדם מוחק בפרק 3?"); the impersonal plural and the
    /// infinitive are the how-to forms. "export"/"import" are out for a different reason: they are already
    /// in <see cref="ProductSurfaceWords"/>, so "How do I export chapter 3?" is product AND book, which is
    /// mixed, which is Union - a row <c>ProductChatRouterTests</c> pins.</para>
    /// </summary>
    private static readonly string[] StructureVerbWords =
    {
        // English
        "add", "adding", "create", "creating", "delete", "deleting", "remove", "removing",
        "rename", "renaming", "reorder", "reordering", "rearrange", "rearranging",
        "split", "splitting", "merge", "merging", "duplicate", "duplicating", "insert", "inserting",
        // Hebrew
        "להוסיף", "מוסיפים", "ליצור", "יוצרים", "למחוק", "מוחקים", "להסיר", "מסירים",
        "לשכפל", "משכפלים", "לפצל", "מפצלים", "לאחד", "מאחדים", "לסדר", "מסדרים",
    };

    /// <summary>
    /// What the question said, before any decision is taken about it. EXPOSED so g2 can build on the
    /// signals rather than re-deriving them - in particular the "book question with no book open" shape,
    /// which <see cref="Resolve"/> deliberately does NOT report as <see cref="ChatRoute.Book"/> (that
    /// route means "answer from the BOOK section", and there is no BOOK section without a bookId) but
    /// which g2 owes a deterministic "open the book first so I can see it".
    /// </summary>
    /// <param name="BookContent">A book-content word from the shared lexicon occurs in the question.</param>
    /// <param name="BookDeictic">A deictic marker occurs ("this", "current", "זה", "הנוכחי"). Only
    /// meaningful when a book is open, which is why <see cref="Resolve"/> reads it under that
    /// condition.</param>
    /// <param name="ProductSurface">A product-surface word occurs.</param>
    /// <param name="WritingCraft">A writing-craft word occurs.</param>
    /// <param name="StrongGuideMatch">The guide corpus scored at or above
    /// <see cref="StrongGuideTopScore"/> for this question.</param>
    /// <param name="ProductHowTo">A <see cref="StructureVerbWords"/> hit AND a manuscript-LOCATION hit:
    /// the question is about doing something to a chapter, not about what is in one. See that list for why
    /// neither half alone is enough.</param>
    public readonly record struct RouteSignals(
        bool BookContent,
        bool BookDeictic,
        bool ProductSurface,
        bool WritingCraft,
        bool StrongGuideMatch,
        bool ProductHowTo = false);

    /// <summary>Reads every signal off one question. Pure; see <see cref="Resolve"/> for what is done
    /// with them.</summary>
    public static RouteSignals Analyze(string? question, double guideTopScore = 0.0)
    {
        var text = question ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return new RouteSignals(false, false, false, false, false);

        return new RouteSignals(
            BookContent: BookArtifactSelector.ContainsAnyWord(text, BookArtifactSelector.BookContentWords),
            BookDeictic: BookArtifactSelector.ContainsAnyWord(text, BookArtifactSelector.BookDeicticWords),
            ProductSurface: BookArtifactSelector.ContainsAnyWord(text, ProductSurfaceWords),
            WritingCraft: BookArtifactSelector.ContainsAnyWord(text, WritingCraftWords),
            StrongGuideMatch: guideTopScore >= StrongGuideTopScore,
            ProductHowTo: BookArtifactSelector.ContainsAnyWord(text, StructureVerbWords)
                          && BookArtifactSelector.ContainsAnyWord(
                              text, BookArtifactSelector.BookLocationWords));
    }

    /// <summary>
    /// The route for one turn.
    ///
    /// <para>THE RULES, IN ORDER. Each one is a CONJUNCTION that requires the other families to be
    /// absent, so anything carrying two families at once falls through to <see cref="ChatRoute.Union"/>
    /// by construction rather than by a tie-break someone has to maintain:</para>
    /// <list type="number">
    ///   <item>A blank question is Union.</item>
    ///   <item>A product signal with no book signal is <see cref="ChatRoute.Product"/>. The product
    ///     signal is a lexicon hit OR a strong guide top score OR a
    ///     <see cref="RouteSignals.ProductHowTo"/>, because "the corpus has a document about this" and
    ///     "this verb acts on a chapter" are both evidence the noun list does not carry.</item>
    ///   <item>A book signal with a book OPEN and no product signal is <see cref="ChatRoute.Book"/>.
    ///     Both halves are required: the route means "answer from the BOOK section", and without a bookId
    ///     that section does not exist. A book-shaped question with no book open therefore lands on Union
    ///     here, and g2 reads <see cref="Analyze"/> for the shape it owes an answer to.</item>
    ///   <item>A craft signal with NO book open and no other family is
    ///     <see cref="ChatRoute.General"/>.</item>
    ///   <item>Everything else is Union.</item>
    /// </list>
    ///
    /// <para>WHY RULE 4 REQUIRES NO BOOK OPEN, WHICH THE PLAN'S ONE-LINE SIGNAL DESCRIPTION DID NOT SAY.
    /// The plan describes General as "a writing-craft hit with no product and no book-deictic hit", and
    /// that reading routes the owner's own first question ("how do I strengthen the romance between Ruby
    /// and Flo?", conversation 6802c061 turn 0) to General while their manuscript is open on screen -
    /// which would DROP the chapter briefs, the register and the findings that the answer needed. The
    /// craft lexicon cannot tell a craft question about writing in general from a craft question about
    /// THIS book, because the difference is carried by proper nouns this router cannot resolve. So a
    /// craft question asked with a book open is MIXED, and mixed is Union, which is the plan's own safety
    /// property applied to the one case where the plan's shorthand would have broken it. A craft question
    /// asked with no book open has nothing to lose and routes General.</para>
    /// </summary>
    /// <param name="question">The author's raw question. Null or blank routes Union.</param>
    /// <param name="hasBookId">Whether the request carried a bookId, i.e. whether there is a BOOK section
    /// for a book-scoped answer to come out of.</param>
    /// <param name="language">
    /// The answer's language. ACCEPTED AND DELIBERATELY NOT CONSULTED: every lexicon here is bilingual by
    /// construction and is matched against the raw question in whatever script it was typed, so the route
    /// is a function of the question and not of the language it was detected as. That is a PROPERTY, not
    /// an omission, and <c>ProductChatRouterTests</c> pins it by resolving the whole table twice, once
    /// per language, and asserting the two agree. Detection is a heuristic; a mixed-script question must
    /// not route differently depending on which way it fell.
    /// </param>
    /// <param name="guideTopScore">The best <see cref="GuideSelector"/> score for this question, which
    /// the caller already computed when it selected the guides. Passed in rather than recomputed, so the
    /// router and the retrieval can never disagree about how well the corpus matched.</param>
    public static ChatRoute Resolve(
        string? question, bool hasBookId, string language, double guideTopScore = 0.0)
    {
        _ = language;   // see the parameter doc: pinned as language-independent, not forgotten.

        if (string.IsNullOrWhiteSpace(question)) return ChatRoute.Union;

        var signals = Analyze(question, guideTopScore);
        return Resolve(signals, hasBookId);
    }

    /// <summary>
    /// THE ONE SHAPE g2 ANSWERS IN CODE: the author is asking about a place inside a manuscript and no
    /// book is open, so nothing that could answer them is in scope. It is deliberately NOT a route -
    /// there is no prompt to compose, because there is no model call: <c>ProductChatService</c> returns a
    /// fixed localized sentence. "Show never claims the book feature is coming" has to be a property of
    /// the code path, exactly as the fail-safes' "never from priors" is; a prompt sentence would make it a
    /// property of the model's compliance, and the sentence it replaces was measured being read back
    /// verbatim 6 of 6 runs.
    ///
    /// <para>KEYED ON <see cref="BookArtifactSelector.BookLocationWords"/>, NOT ON THE FULL BOOK LEXICON,
    /// and that narrowing is load-bearing. <see cref="BookArtifactSelector.BookContentWords"/> also
    /// carries the six review DIMENSIONS, so "how do I improve the pacing?" asked with no book open is a
    /// book-content hit and a general craft question at the same time; answering it with "open a book
    /// first" would take a real question and give the author nothing. A LOCATION - a chapter, a scene - is
    /// what cannot be discussed without a manuscript.</para>
    ///
    /// <para>THE OTHER FAMILIES ALL VETO, on the same conjunction discipline
    /// <see cref="Resolve(RouteSignals, bool)"/> uses: a product signal (a lexicon hit or a strong guide
    /// top score) means the guides may well answer it, and a craft signal means the question is at least
    /// partly about writing in general. Mixed is never answered deterministically; it falls through to the
    /// router and, in practice, to Union.</para>
    ///
    /// <para>THE RESIDUAL g1 NAMED IS NOW A VETO (g3). g1 recorded that a product HOW-TO naming a chapter
    /// without a product word ("how do I add a chapter?") was caught here unless the guide corpus happened
    /// to score above <see cref="StrongGuideTopScore"/>, and left it for g3 to measure. g3 measured it: 4
    /// of the 6 how-tos in its residual cell were answered "open the book you are asking about", in both
    /// languages, which is a non-answer to a question that has nothing to do with any particular
    /// manuscript. <see cref="RouteSignals.ProductHowTo"/> vetoes alongside the other three families, and
    /// the route those turns fall to is <see cref="ChatRoute.Product"/> rather than Union - see
    /// <see cref="Resolve(RouteSignals, bool)"/> for why the location the verb names is withdrawn.</para>
    /// </summary>
    public static bool AsksAboutABookThatIsNotOpen(
        string? question, bool hasBookId, double guideTopScore = 0.0)
    {
        if (hasBookId || string.IsNullOrWhiteSpace(question)) return false;

        var signals = Analyze(question, guideTopScore);
        if (signals.ProductSurface || signals.StrongGuideMatch
            || signals.WritingCraft || signals.ProductHowTo) return false;

        return BookArtifactSelector.ContainsAnyWord(question, BookArtifactSelector.BookLocationWords);
    }

    /// <summary>The rules, over already-read signals. Split out so a test can drive a signal
    /// combination directly instead of reverse-engineering a question that produces it.</summary>
    public static ChatRoute Resolve(RouteSignals signals, bool hasBookId)
    {
        var product = signals.ProductSurface || signals.StrongGuideMatch || signals.ProductHowTo;

        // A deictic marker only points at the book when there IS a book: "this" in a book-less turn is
        // ordinary English, not a reference to a manuscript.
        //
        // A PRODUCT HOW-TO SUPPRESSES THE LOCATION IT NAMES (g3), and that is the point of the signal
        // rather than a side effect of it. "How do I delete a chapter?" names a chapter, so the location
        // half of the book lexicon fires and the question is book-and-product at once, which is mixed,
        // which is Union - and Union answers a product how-to from the guides only by luck. The verb says
        // the word is being used for the app's chapter LIST and not for a chapter's contents, so the book
        // signal it raised is withdrawn and the turn is a product question, which is what it is.
        var book = (signals.BookContent && !signals.ProductHowTo)
                   || (signals.BookDeictic && hasBookId);

        if (product && !book) return ChatRoute.Product;
        if (hasBookId && book && !product) return ChatRoute.Book;
        if (signals.WritingCraft && !hasBookId && !book && !product) return ChatRoute.General;

        return ChatRoute.Union;
    }
}
