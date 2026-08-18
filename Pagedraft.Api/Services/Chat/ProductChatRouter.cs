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
    /// EVERYTHING ELSE, AND THE SAFETY PROPERTY OF THE WHOLE LAYER. Union composes BYTE-IDENTICALLY to
    /// what shipped before routing existed, so a misroute can only ever return the status quo. Anything
    /// mixed, anything unmatched, and anything at all while the flag is off resolves here.
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
    /// answer to this question rather than the top of a weak field. Two exact heading-token matches in
    /// the question's own language (<see cref="GuideSelector.HeadingWeight"/> is 3.0), which is the
    /// cheapest description of "the corpus has a document ABOUT this".
    ///
    /// <para>UNMEASURED, AND DELIBERATELY ONLY A POSITIVE SIGNAL. A score at or above this adds Product;
    /// a score below it removes nothing, because the selector is contracted never to decide "no coverage"
    /// (<see cref="GuideSelector"/>, "THE SELECTOR NEVER DECIDES NO COVERAGE") and a router that read a
    /// weak score as a refusal would be making exactly the decision that contract reserves for the model.
    /// g3 calibrates this number against real questions; until then it is inert behind the flag.</para>
    /// </summary>
    public const double StrongGuideTopScore = 6.0;

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
    /// </summary>
    private static readonly string[] ProductSurfaceWords =
    {
        // English
        "pagedraft", "import", "importing", "imported", "export", "exporting", "exported", "exports",
        "docx", "epub", "pdf", "upload", "download", "button", "buttons", "screen", "panel", "dashboard",
        "settings", "setting", "app", "editor", "toolbar", "menu", "shortcut", "shortcuts", "keyboard",
        "login", "account", "subscription", "proofread", "sidebar", "tab", "tabs",
        // Hebrew
        "ייבוא", "לייבא", "מייבא", "מייבאים", "ייצוא", "לייצא", "מייצא", "מייצאים",
        "כפתור", "הכפתור", "כפתורים", "מסך", "המסך", "מסכים", "חלונית", "החלונית", "פאנל",
        "הגדרות", "ההגדרות", "הגדרה", "אפליקציה", "האפליקציה", "עורך", "העורך", "סרגל",
        "תפריט", "התפריט", "קיצור", "קיצורים", "מקלדת", "התחברות", "חשבון", "מנוי",
        "מערכת", "המערכת", "תוכנה", "לשונית", "הלשונית", "הגהה", "ההגהה",
    };

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
    public readonly record struct RouteSignals(
        bool BookContent,
        bool BookDeictic,
        bool ProductSurface,
        bool WritingCraft,
        bool StrongGuideMatch);

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
            StrongGuideMatch: guideTopScore >= StrongGuideTopScore);
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
    ///     signal is a lexicon hit OR a strong guide top score, because "the corpus has a document about
    ///     this" is evidence the noun list does not carry.</item>
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

    /// <summary>The rules, over already-read signals. Split out so a test can drive a signal
    /// combination directly instead of reverse-engineering a question that produces it.</summary>
    public static ChatRoute Resolve(RouteSignals signals, bool hasBookId)
    {
        var product = signals.ProductSurface || signals.StrongGuideMatch;

        // A deictic marker only points at the book when there IS a book: "this" in a book-less turn is
        // ordinary English, not a reference to a manuscript.
        var book = signals.BookContent || (signals.BookDeictic && hasBookId);

        if (product && !book) return ChatRoute.Product;
        if (hasBookId && book && !product) return ChatRoute.Book;
        if (signals.WritingCraft && !hasBookId && !book && !product) return ChatRoute.General;

        return ChatRoute.Union;
    }
}
