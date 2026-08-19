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
    /// THE SHIPPED ENGLISH PRODUCT DOCUMENT FLOOR, AND IT IS 0.0, WHICH MEANS THE WITHHOLDING IS OFF ON
    /// EVERY TURN. Gate run 5 measured the lever in place and it FAILED HARMFULLY; this is the rollback,
    /// and the record of it is the paragraphs below rather than a deleted mechanism. The mechanism,
    /// <see cref="WithholdsProductDocuments"/>, its tests and the whole derivation on
    /// <see cref="RolledBackEnglishProductDocumentsFloor"/> are kept ON PURPOSE - they are what was
    /// measured, and the owner may want to revisit the class with a different instrument.
    ///
    /// <para>READ THIS BEFORE RE-ENABLING IT. The derivation next door is intact and reads persuasively;
    /// it is the rationale for a value that has since been measured IN PLACE and rejected. A reader who
    /// takes only that paragraph away from this file will re-enable a lever that made the answers worse.
    /// Re-enabling means moving this constant, the class default in
    /// <see cref="ProductChatOptions.EnglishProductDocumentsFloor"/> and the shipped
    /// <c>ProductChat:EnglishProductDocumentsFloor</c> key together, AND changing
    /// <c>ProductChatRouterTests.TheShippedFloor_WithholdsOnNoTurn</c> and
    /// <c>ProductChatRoutedAnswerTests.TheShippedConfiguration_SendsDocumentsOnEveryEnglishProductTurn</c>,
    /// which exist so that turning it back on is a deliberate act and not a config typo.</para>
    ///
    /// <para>WHAT RUN 5 MEASURED (evidence <c>g3e/results.jsonl</c>, 102 records, the lever at 4.0 on
    /// commit <c>bf6088f</c>). The lever FIRED correctly: exactly the 10 intended records were withheld,
    /// the whole English product-uncovered cell plus <c>B|en|4</c> and <c>B|en|7</c>. It DID NOT MOVE THE
    /// TARGET METRIC: under the blindness-corrected detector (<c>g3e/detect4.js</c>) the English
    /// product-uncovered cell went 7/8 to 6/8 source-narrating, the same draw the four prior re-wordings
    /// produced. The apparent 7/8 to 2/8 was the OLD detector going blind to vocabulary the withheld
    /// turns INVENTED for the thing they had been handed - "the details provided", "what I have been
    /// given", "our materials", "what I have access to" - none of which the old pattern list knew. The
    /// metric only appeared to move.</para>
    ///
    /// <para>AND IT BROKE THE ONE FLOOR THAT HAD HELD IN ALL FOUR PRIOR RUNS: 0 of 408 records had ever
    /// asserted a PageDraft behaviour that does not exist, and this run produced 4 of 102, every one of
    /// them a WITHHELD turn. <c>C|en|6</c> sent the author to "the settings menu where appearance options
    /// are usually found"; <c>C|en|0</c> to "the settings screen related to security"; <c>C|en|3</c> to
    /// "the official documentation or support resources"; <c>B|en|7</c> to "your specific instructions or
    /// documentation". Grep-verified against the shipped corpus: the English guides contain NO occurrence
    /// of "settings" at all. Two more withheld turns (<c>C|en|1</c>, <c>C|en|5</c>) rendered a gap as a
    /// FACT about the product ("No, I don't have any information about whether there is a mobile app"),
    /// which is the one thing <c>ProductGroundingScoped</c> forbids by name, and 5 lost Show's persona for
    /// a generic assistant voice. Handing the model nothing did not make it quiet; it made it fall back on
    /// what a generic assistant knows about apps in general, which is the failure the documents were
    /// preventing.</para>
    ///
    /// <para>SO THE CLASS IS STILL OPEN AND THIS WAS NOT ITS FIX. Do not read the rollback as "the
    /// narration class is closed" - it is exactly where the four prior runs left it, minus one instrument
    /// bug. The next attempt needs a detector validated before the run, not another lever measured with a
    /// detector that cannot see what the lever invents.</para>
    /// </summary>
    public const double EnglishProductDocumentsFloor = 0.0;

    /// <summary>
    /// THE VALUE GATE 4 FITTED AND GATE RUN 5 ROLLED BACK, kept named so the tests that exercise the
    /// mechanism can ask for it explicitly instead of inheriting it from a shipped default that no longer
    /// turns it on. NOTHING IN THE APPLICATION READS THIS. Read
    /// <see cref="EnglishProductDocumentsFloor"/> FIRST: it carries the measurement that disabled the
    /// lever, and everything below is the reasoning that shipped it, preserved unedited.
    ///
    /// <para>THE ORIGINAL DERIVATION FOLLOWS, AND IT IS SUPERSEDED. Below this guide top score an English
    /// product turn was handed NO DOCUMENTS AT ALL, leaving the model the product grounding rule and
    /// nothing to read - which was expected to be the configuration that can only produce a refusal, and
    /// measured as the configuration that produces an invented settings menu.</para>
    ///
    /// <para>THIS IS A STRUCTURAL LEVER AND IT EXISTS BECAUSE FOUR RE-WORDINGS DID NOT MOVE THE CLASS. The
    /// English product-uncovered cell read 8/8, 8/8, 8/8, 7/8 source-narrating across four gate runs, under
    /// four different attempts at the exemplar and its frame: a re-word (g3), a data-envelope rename (g3b),
    /// place-grounding (g3c) and sentence-completion (g3d). One record of movement across four runs is a
    /// draw, and gate 4 fired the standing stop-condition. The residual is not the exemplar. The one
    /// configuration measured at 0 of 16 narration in EVERY one of those four runs is the General route's,
    /// and the two things General has that Product does not are that it is handed no documents and composes
    /// no citation sentence (<see cref="ProductChatPrompt.GuidesMarker"/> writes that comparison up). This
    /// gives an English product turn the same treatment when the corpus scored too low to be worth reading.
    /// It changes NO prompt string: every byte the model sees here already shipped.</para>
    ///
    /// <para>THE CUT POINT COMES FROM g3d'S OWN SCORES AND NOTHING ELSE. Re-derived off that run's 102
    /// records before this was written: on the English product route the UNCOVERED cell scores
    /// {0,0,0,0,3,3,3,3} (max 3) and the COVERED cell scores {3,3,4,6,7,7,7,9}. A cut at 4.0 therefore moves
    /// all eight uncovered turns off the documents and takes exactly two covered turns with them,
    /// <c>B|en|4</c> ("what settings can I change?") and <c>B|en|7</c> ("how do I open the editor?"), both of
    /// which already answered with a refusal in all four runs. The two other English product-route records
    /// below 4 do not exist: <c>F|en|2</c> scored 9 and <c>R|en|0</c>/<c>R|en|1</c> scored 4, so nothing in
    /// the residual or book-less cells moves either.</para>
    ///
    /// <para>IT IS A VALUE TO BE MEASURED AND NOT A VALIDATED CONSTANT, stated plainly because the number
    /// LOOKS like the calibrated one above it and is not. <see cref="StrongGuideTopScore"/> was moved after a
    /// run showed a whole cell sitting exactly on the old value; this one is fitted to a single question set
    /// at n=8 per cell, on one manuscript, in one language, and no run has yet been taken with it in place.
    /// The next gate measures THE THRESHOLD as much as it measures the fix: the numbers that would say it is
    /// wrong are a rise in the English product route's refusal count (the cut bought the drop by refusing
    /// questions the corpus does cover) or an English product answer that invents a behaviour (the withheld
    /// turn fabricated instead of refusing). Do not promote it to "calibrated" on one green run.</para>
    ///
    /// <para>THAT RUN WAS TAKEN, AND THE SECOND OF THOSE TWO NUMBERS CAME BACK: gate run 5 produced four
    /// English product answers that invent a behaviour, all four on withheld turns, against 0 in 408 prior
    /// records. The stop-condition this paragraph wrote in advance is what disabled the lever. See
    /// <see cref="EnglishProductDocumentsFloor"/>.</para>
    ///
    /// <para>HEBREW IS EXCLUDED BY CONSTRUCTION AND THAT ASYMMETRY IS THE POINT, NOT AN OVERSIGHT. The same
    /// cut applied to Hebrew would break the one route that is already working: Hebrew's product-uncovered
    /// cell is the win of these four runs, having gone 8/8 to 3/8, and its covered cell answers WELL at
    /// score 3. <c>B|he|0</c> ("איך מייבאים כתב יד?") and <c>B|he|2</c> ("מה קורה כשלוחצים על הגהה?") both
    /// score 3 and both returned a correct, substantive, cited answer in all four runs; a floor of 4 would
    /// have replaced both with "I do not have that information". A later reader who notices the asymmetry and
    /// "fixes" it will delete two measured good answers to close a class that Hebrew does not have.
    /// <c>ProductChatRouterTests</c> pins both halves.</para>
    ///
    /// <para>THE DEFAULT LIVES HERE AND THE OPERATIONAL VALUE LIVES IN
    /// <see cref="ProductChatOptions.EnglishProductDocumentsFloor"/>, for the reason
    /// <see cref="ProductChatOptions.RoutingEnabled"/> records: a value the next gate is going to argue with
    /// must be changeable without a deploy. A floor of 0 or less turns the withholding OFF entirely and is
    /// the kill switch, which is why <see cref="WithholdsProductDocuments"/> tests for it rather than letting
    /// a mis-typed 0 read as "withhold from everything". That kill switch is what ships today: the default
    /// next door is 0.0 and this value is reachable only from a test or a deliberate config edit.</para>
    /// </summary>
    public const double RolledBackEnglishProductDocumentsFloor = 4.0;

    /// <summary>
    /// Whether this turn's documents are WITHHELD: the English product route, below
    /// <paramref name="documentsFloor"/>. See <see cref="EnglishProductDocumentsFloor"/> for the
    /// measurement that turned this OFF, and <see cref="RolledBackEnglishProductDocumentsFloor"/> for why
    /// the cut had been 4.0 and for why Hebrew was never in it.
    ///
    /// <para>IT RETURNS FALSE ON EVERY TURN AS SHIPPED, because the floor defaults to
    /// <see cref="EnglishProductDocumentsFloor"/> and that is 0.0. The predicate is kept whole rather than
    /// deleted: it is the mechanism gate run 5 measured, and its tests are the record of what it did. A
    /// caller who wants the withholding behaviour must pass a positive floor explicitly, which is what the
    /// tests below do.</para>
    ///
    /// <para>IT IS DELIBERATELY NOT PART OF <see cref="Resolve"/>, AND THAT IS A PROPERTY THIS FILE ALREADY
    /// OWES. <see cref="Resolve"/>'s <c>language</c> parameter is documented as accepted and never consulted,
    /// and <c>ProductChatRouterTests</c> pins that by resolving the whole table twice, once per language, and
    /// asserting the two agree. This decision IS language-dependent, so folding it into the route would
    /// silently retire that pin. A route says what KIND of question the turn is; this says what grounding the
    /// turn gets, which is the same layer <c>ProductChatService.GeneralRouteGuideCount</c> lives at.</para>
    ///
    /// <para>THE LANGUAGE TEST IS POSITIVE ON PURPOSE. It asks for <see cref="ChatLanguage.English"/> rather
    /// than for "not Hebrew", so a language this layer does not serve keeps its documents - the status quo -
    /// instead of inheriting an English-only lever by being unrecognised.</para>
    /// </summary>
    /// <param name="route">The APPLIED route for this turn, not the resolved one: with routing off the
    /// applied route is <see cref="ChatRoute.Union"/> and nothing here fires, which is what keeps the flag a
    /// true rollback for this lever too.</param>
    /// <param name="language">The answer's language, already resolved by <see cref="ChatLanguage.Detect"/>.</param>
    /// <param name="guideTopScore">The best <see cref="GuideSelector"/> score for this question, the same
    /// value <see cref="Resolve"/> was given.</param>
    /// <param name="documentsFloor">The floor to apply. Zero or less means the lever is off, and zero is
    /// what the default resolves to today.</param>
    public static bool WithholdsProductDocuments(
        ChatRoute route, string? language, double guideTopScore,
        double documentsFloor = EnglishProductDocumentsFloor)
    {
        if (route != ChatRoute.Product) return false;
        if (!string.Equals(language, ChatLanguage.English, StringComparison.OrdinalIgnoreCase)) return false;
        if (documentsFloor <= 0.0) return false;

        return guideTopScore < documentsFloor;
    }

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
