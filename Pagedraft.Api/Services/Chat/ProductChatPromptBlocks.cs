namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// THE AUTHORED TEXT of the product-chat prompt, lifted out of <see cref="ProductChatPrompt"/> by g1.
/// That class now holds COMPOSITION (which blocks a route assembles, in what order) and this one holds
/// the blocks themselves, with the paragraph explaining each block's wording sitting beside it exactly
/// as it did before the move.
///
/// <para>WHY THE SPLIT, AND WHY HERE. <c>ProductChatPrompt.cs</c> was 594 lines before g1 and the
/// routing seam takes it past the workspace's ~700-line soft ceiling. The line the split is made on is
/// the one that makes it safe to make at all: g1 adds identity tests that pin all four composed
/// messages against literals typed by hand, so a move that changed a character fails the suite instead
/// of shipping. The facts under <c>--filter ~ProductChat</c> are the fence, and g1 MEASURED them at 700
/// pre-existing (the plan's "~370" is stale).</para>
///
/// <para>g2 THEN ADDED THE ROUTED BLOCKS BESIDE THE UNION ONES RATHER THAN EDITING THEM, and that is
/// still the organising rule of this file: <see cref="ChatRoute.Union"/> is the fallback every misroute
/// lands on, so a route that needs different words gets a NEW block and, where only one sentence differs,
/// a compile-time split so the twenty sentences around it are shared rather than re-typed.</para>
///
/// <para>THE RULE USED TO BE ABSOLUTE - "Union is byte-identical to what g4 and g5 measured, so a block
/// Union composes may not move" - AND g3 BROKE IT ONCE, DELIBERATELY AND IN EXACTLY ONE PLACE.
/// <see cref="BookRefusalEn"/>/<see cref="BookRefusalHe"/> told the author that answering about a
/// specific book "is not available yet and is coming", which has been false since phase B taught Show to
/// read the book, and g3 measured it reaching a real user on 5 of 102 turns. Byte-identity is worth
/// having because it makes a misroute harmless; it is not worth having when what it preserves is a false
/// statement about the product. Every other block Union composes is byte-unchanged, and the pins in
/// <c>ProductChatRoutePartitionTests</c>, <c>ProductChatBookPromptTests</c> and
/// <c>ProductChatComposedSystemSlotTests</c> were re-typed by hand rather than regenerated.</para>
///
/// <para>EXACTLY ONE THING CHANGED IN THE TEXT, AND IT CHANGED NO CHARACTER OF IT.
/// <see cref="GroundingEnHead"/> and <see cref="GroundingHeHead"/> are now the COMPILE-TIME
/// concatenation of a <see cref="PersonaEn"/>/<see cref="PersonaHe"/> block and a
/// <see cref="ProductGroundingEn"/>/<see cref="ProductGroundingHe"/> block. Const concatenation is
/// evaluated by the compiler, so the reassembled head is the same string constant it always was; the
/// split exists so a route that wants Show's VOICE without the guides-only grounding rule (g2's General
/// route) has a seam to lift at, instead of a second copy of the persona sentence to keep in step.
/// The split point is the sentence boundary the persona paragraph already described: everything up to
/// and including "you open each reply from what was actually asked. " is register, and everything from
/// "Answer ONLY from the guide content provided below. " is the product-grounding contract.</para>
///
/// <para>No em-dash appears in any string here: these strings reach the user, and the model echoes
/// punctuation from its frame.</para>
/// </summary>
internal static class ProductChatPromptBlocks
{
    // ─── The grounding contract, as instruction text (d1 items 2, 3 and 5) ───────────────────────
    //
    // PHASE B SPLIT THESE STRINGS IN THREE AND CHANGED NO CHARACTER OF THEM. The HEAD carries the
    // persona and the whole product-grounding contract including final-r02's scoped refusal pivot; the
    // TAIL carries the citation line and the language rule; between them sits EXACTLY ONE swappable
    // sentence group. Phase A's system message is head + BookRefusal + tail, byte-for-byte what g4 and
    // g5 measured, and phase B's is head + BookGrounding + tail. The split exists so B can lift the one
    // refusal it is licensed to lift WITHOUT re-typing a single sentence that carries a gate verdict;
    // ProductChatPromptIdentityTests pins the reassembly against a literal so the property is checked
    // rather than trusted.
    //
    // G1 SPLIT THE HEAD ONE FURTHER, INTO PERSONA + PRODUCT GROUNDING, AND AGAIN CHANGED NO CHARACTER.
    // Same reason, one layer down: the routing layer needs to compose a message that keeps Show's voice
    // and drops "answer ONLY from the guide content provided below", and the alternative to a seam is a
    // second copy of the persona sentence. The reassembly below is a const expression, so the compiler
    // guarantees the head is unchanged and ProductChatRoutePartitionTests pins it anyway - a guarantee
    // nobody checks is a guarantee that stops being read.

    /// <summary>
    /// REGISTER ONLY (phase A.2, c2): first person, warm, brief, and opening from what was actually
    /// asked. It states no rule and scopes none, which is what makes it the half a non-guides route can
    /// keep. The Hebrew twin is DESCRIPTIVE ("אתה כותב"), not imperative, because an imperative in this
    /// string has leaked verbatim into user-visible Hebrew answers at two separate clauses.
    /// </summary>
    internal const string PersonaEn =
        "You are Show, the PageDraft product assistant. You write in the first person, warmly and " +
        "briefly, and you open each reply from what was actually asked. ";

    /// <summary>
    /// THE GUIDES-ONLY GROUNDING CONTRACT, and the half a book-scoped or general-craft route has to be
    /// able to drop. Every sentence in it carries a measured verdict:
    ///
    /// <para>NO TERMINOLOGY MAPPING (d1 item 6). The guides still say "book summary" where Wave 3's
    /// reconciled vocabulary says "book briefs". Phase A ships against the guides EXACTLY as they read
    /// today and adds NO vocabulary-substitution instruction, because an answer that says "book briefs"
    /// while citing a guide that says "book summary" is the citation/text mismatch the grounding
    /// contract exists to prevent.</para>
    ///
    /// <para>NO META-CLAIM ABOUT AN ABSENT TOPIC (the g2 HALT). The original rule forbade stating a
    /// setting, button, screen or behavior the guides do not state, and required naming what they DO
    /// cover on a refusal. g2's `b7` run1 obeyed BOTH and still fabricated, by asserting something about
    /// the CORPUS instead of about the product: "the only shortcuts mentioned in the text are related to
    /// saving chapters or dismissing cards", against a corpus with zero occurrences of shortcut,
    /// keyboard, ctrl or their Hebrew equivalents. Characterizing what the guides say about a topic they
    /// never mention was not forbidden anywhere, so both strings now forbid it explicitly, while still
    /// permitting the pivot that works. Both strings also say to frame a gap as a gap in the GUIDES
    /// rather than as a fact about the product: g2's Hebrew `d4` asserted "PageDraft does not support
    /// exporting EPUB", which the guides never say.</para>
    ///
    /// <para>WHY THE PIVOT IS CONDITIONAL, NOT MANDATORY (the g3 HALT). Adding the prohibition above did
    /// not close the class: g3 still saw 2 of 39 adjacent runs fabricate, one of them now quoting
    /// "Cmd/Ctrl+S" as something the guides describe. The cause was a COLLISION, not a missing rule. The
    /// refusal sentence demanded, unconditionally, that a refusal name what the guides DO cover; on the
    /// one question shape "which X does the product have?" where the corpus contains no X at all, every
    /// honest referent is absent, so the only way to satisfy that demand is to report what the guides
    /// supposedly say about X, which is exactly what the new prohibition forbids. The model resolved the
    /// conflict toward the older, more emphatic clause. The fix is to SCOPE the demand rather than add a
    /// fourth prohibition: the pivot is conditioned on the guides actually covering ANOTHER relevant
    /// topic, and a bare refusal is stated to be a complete answer when they do not. It is permitted,
    /// not required, because g3 measured the pivot working and losing it would be a real cost.</para>
    /// </summary>
    internal const string ProductGroundingEn =
        "Answer ONLY from the guide content provided below. " +
        "Do not use outside knowledge about PageDraft, and never state a setting, button, screen or " +
        "behavior that the provided guides do not state. " +
        "If the guides do not address the question, say so plainly. If another topic they DO cover is " +
        "genuinely relevant, name it and its guide id; if none is, a bare refusal is the whole " +
        "answer. Do not assemble a guess out of partially relevant material. " +
        "State it as a gap in the guides, not as a fact about the product: do not say that PageDraft " +
        "lacks the thing or does not support it. And do not describe what the guides say about a topic " +
        "they do not address, not even to report what they mention about it. ";

    /// <summary>Phase A's head, REASSEMBLED AT COMPILE TIME from the two blocks above. Byte-identical
    /// to the single literal it used to be, by construction rather than by care.</summary>
    internal const string GroundingEnHead = PersonaEn + ProductGroundingEn;

    /// <summary>
    /// THE PRODUCT ROUTE'S GROUNDING (g2): <see cref="ProductGroundingEn"/> WITH THE SOURCE-NARRATION
    /// TAKEN OUT, and it exists as a second block rather than as an edit to that one because
    /// <see cref="ChatRoute.Union"/> must stay byte-identical to what g4 and g5 measured. Union is the
    /// fallback for every question the router cannot classify, and the whole routing layer rests on a
    /// misroute being able to return only the status quo; editing the shared block in place would have
    /// moved the status quo.
    ///
    /// <para>WHAT WAS DELETED, AND WHY EACH DELETION IS THE BUG. The owner's report is that Show narrates
    /// where he can and cannot find things, and two sentences of the shared block INSTRUCT exactly that.
    /// "If the guides do not address the question, say so plainly" tells him to tell the reader about the
    /// guides; "State it as a gap in the guides, not as a fact about the product" MANDATES the meta-frame,
    /// and it is the reason he talks about his sources instead of about the world. Both are gone here.
    /// The conditional pivot that rode with the first ("if another topic they DO cover is genuinely
    /// relevant, name it and its guide id") goes with it, because naming a guide id in prose is the same
    /// narration wearing a citation's clothes and the citation line already carries that fact.</para>
    ///
    /// <para>WHAT WAS KEPT, AND WHY DROPPING IT WOULD HAVE BEEN A DIFFERENT CHANGE THAN THE ONE ASKED FOR.
    /// "Answer ONLY from the guide content provided below", "never state a setting, button, screen or
    /// behavior that the provided guides do not state", "do not assemble a guess out of partially relevant
    /// material" and "do not describe what the guides say about a topic they do not address" are
    /// ANTI-FABRICATION rules, not narration, and g4's PASS (0 fabricated product behaviors in 48 adjacent
    /// runs) is a measurement of them. g3's acceptance says refusal appropriateness must not regress, so
    /// they are carried across verbatim.</para>
    ///
    /// <para>g3 MEASURED THE FIRST ATTEMPT AND IT FAILED, AND THE WORDING BELOW IS THE SECOND. The block
    /// g2 wrote deleted the two narrating sentences and replaced them with one that admitted a gap
    /// "without describing where you looked". g3 ran it: on the product-uncovered cell the answers
    /// narrated 16 of 16, in both languages, and on English general craft 4 of 8. The cause was NOT a
    /// missing rule. That block NAMED ITS SOURCE FIVE TIMES over five sentences - "the guide content
    /// provided below", "the provided guides do not state", "what the guides say", "a topic they do not
    /// address" - and then one clause forbade describing where you looked. A prohibition stacked on a
    /// frame that keeps teaching the noun is the shape this file has now recorded failing four times
    /// (g3's fourth prohibition, F-1's two rules, phase A be-c03, and this), and the workspace has the
    /// same finding one layer up: naming an internal token in order to forbid it TEACHES the token.</para>
    ///
    /// <para>SO THE SOURCE IS NO LONGER NAMED, AND NO BAN REPLACES IT. The grounding is "the material
    /// below" and then "there": a place, not a kind of document. Nothing here says the word "guide", and
    /// with the noun gone there is nothing to narrate ABOUT, so the anti-narration clause is not re-worded
    /// - it is deleted, along with the "do not describe what the guides say about a topic they do not
    /// address" sentence, whose entire content was a statement about the source. THE SENTENCE COUNT DROPS
    /// FROM FIVE TO TWO.</para>
    ///
    /// <para>WHAT IS KEPT, BECAUSE IT IS ABOUT THE PRODUCT AND NOT ABOUT THE SOURCE: never state a
    /// setting, button, screen or behavior that is not written there; do not assemble a guess out of
    /// partly relevant parts; and never turn a gap into a claim that PageDraft lacks the thing (g2's
    /// Hebrew `d4` asserted "PageDraft does not support exporting EPUB", which no guide says). Those are
    /// the rules g4's PASS is a measurement of and g3's own 0-fabrication result on the uncovered cell
    /// rests on, so they are carried across in force.</para>
    ///
    /// <para>THE CLAIM THIS BLOCK WAS BUILT ON WAS MEASURED AND IT IS REFUTED (g3b). The paragraph that
    /// stood here said the block could not reach the <c>[GUIDES]</c> marker or the <c>=== GUIDE id=... ===</c>
    /// headers, and framed that as a narrower TESTED CLAIM: that the instructions naming the source, and not
    /// the data carrying it, are what make the answer talk about the source. The second run says otherwise.
    /// Narration on product questions moved 33/102 to 32/102, the product-uncovered cell held at 15 of 16,
    /// and the answers simply narrated with whatever noun was left: the Hebrew came back saying
    /// <c>המדריכים</c>, which this rewritten block does not contain, and the English came back saying "the
    /// material provided", which is this block's OWN grounding phrase read back (13 hits across the run).
    /// Deleting a noun from the instructions does not remove the noun; it elects the next one. The envelope
    /// is fixed in <see cref="ProductChatPrompt.GuidesMarker"/>, where the measurement is written up.</para>
    ///
    /// <para>SO THE GROUNDING IS NOW A PLACE AND NOT A SUBSTANCE. "The material below" is gone in favour of
    /// "what is written below" and "there". That is the construction the GENERAL route already uses
    /// ("Nothing about PageDraft is in front of you on this turn"), and the general route narrated 0 of 16.
    /// A place cannot be reported as failing you; a substance can.</para>
    ///
    /// <para>AND THE GAP SENTENCE IS RE-SHAPED SO IT IS NOT RECITABLE (g3b, 2 of 102 to 4 of 102). Three
    /// answers ended by reciting this block's second sentence at the reader, verbatim and complete: "That is
    /// a complete answer on its own, and it is never a claim that PageDraft lacks the thing or does not
    /// support it." It was phrased as a statement ABOUT WHAT AN ANSWER IS and placed immediately AFTER the
    /// refusal it describes, so a model that had just written the refusal continued straight along the
    /// instruction's own sequence into the commentary about it. Two things change and neither is a new rule.
    /// (1) ORDER: the constraint now comes BEFORE the refusal, so there is nothing left after the refusal to
    /// continue into. (2) SHAPE: the refusal is given as the finished first-person sentence in quotes, which
    /// is the construction <see cref="BookRefusalHe"/> already records as the thing that stopped an echo
    /// ("a sentence to say, not an order to follow"). That pattern is self-healing in a way the old wording
    /// was not - if the model DOES read it back verbatim, what the reader gets is the honest refusal itself
    /// rather than a note about what a refusal is. "That is a complete answer on its own" is deleted
    /// outright: its job was to stop padding, and "and stop" does that without describing anything.</para>
    ///
    /// <para>THE ANTI-FABRICATION CONTENT IS CARRIED ACROSS UNCHANGED IN FORCE, because g4's PASS and g3b's
    /// own 0 fabrications on the uncovered cell are measurements of it: never state a setting, button, screen
    /// or behavior that is not written there, never assemble one out of partly relevant parts, and never turn
    /// a gap into a claim that PageDraft lacks the thing. That last one is the rule holding the coming-soon
    /// class at 0 of 102, and it is kept as an explicit prohibition rather than softened.</para>
    ///
    /// <para>THE ENGLISH EXEMPLAR WAS ONE WORD SHORT OF A SENTENCE, AND THAT IS THE WHOLE OF g3c's ENGLISH
    /// RESIDUAL. The third run split the two languages apart for the first time: Hebrew source-narration on
    /// the product-uncovered cell went 8/8, 7/8, 2/8 while English stayed 8/8 across all three, unmoved by a
    /// re-wording (g3) and by taking the source noun out of the envelope (g3b) alike. Read side by side, the
    /// two blocks are the SAME instruction sentence for sentence - place-grounding, the gap constraint before
    /// the refusal, the refusal quoted as a finished first-person sentence, "and stop" - and they differ in
    /// EXACTLY ONE thing: the Hebrew exemplar <c>אין לי את המידע הזה.</c> is a complete, topic-free sentence
    /// and the English one, "I do not have that.", is not. English "that" needs its noun, so a model copying
    /// the exemplar cannot stop where the exemplar stops; it must supply the missing head, and 8 of 8 answers
    /// supplied the SAME one - every English refusal in g3c opens "I do not have that information about X",
    /// which is the exemplar plus the word the exemplar left out. Having started writing past the quote it
    /// kept going, into the source adjunct the round was called for ("in what was provided", "from what was
    /// written there", "in these guides") and, in six records, into an enumeration of what the corpus DOES
    /// cover. Hebrew, handed a sentence it could copy whole, emitted it verbatim and stopped in 5 of 8.</para>
    ///
    /// <para>SO THE FIX IS TO FINISH THE SENTENCE, AND IT IS DELIBERATELY THE ONLY THING THIS ROUND CHANGES
    /// HERE. "I do not have that information." is the literal English of the Hebrew twin, so the two blocks
    /// are now the same instruction in both languages down to the exemplar, and the gate reads as a
    /// single-variable test of the completeness claim rather than of a fourth re-framing. No prohibition is
    /// added against the adjunct or against the enumeration: this file records a stacked prohibition making a
    /// class measurably worse four times, and both masses are downstream of the same missing word - a reply
    /// too short to be a reply gets padded, and the padding is what narrates. The prediction is Hebrew's own
    /// number, not zero: Hebrew still extended the complete exemplar with a topic and a source in 2 of 8, so
    /// giving English the same shape should buy English the same 2 of 8 and no better.</para>
    /// </summary>
    internal const string ProductGroundingScopedEn =
        "What you say about PageDraft comes only from what is written below, never from outside " +
        "knowledge: do not state a setting, button, screen or behavior that is not written there, and " +
        "do not assemble one out of parts that are only partly relevant. " +
        "A gap in what you were given is never a fact about the product, so never say that PageDraft " +
        "lacks a thing or does not support it. " +
        "Where the answer is not there, say so briefly in your own voice and stop, in the sense of: " +
        "'I do not have that information.' ";

    /// <summary>
    /// THE BOOK ROUTE'S PRODUCT RULE, IN ONE SENTENCE (g2). A book-scoped turn still has to be unable to
    /// invent a screen, but the five sentences of <see cref="ProductGroundingEn"/> are written for a turn
    /// whose ANSWER comes from the guides, and on this route the answer comes from the BOOK section. The
    /// clause of <see cref="BookGroundingRoutedEn"/> that says "the rule above about the guides governs
    /// questions about PageDraft itself" needs a rule above it to point at, and this is it.
    ///
    /// <para>The tokens this frees are the point: they are paid to the book artifacts, alongside g2's drop
    /// from two guides to one on this route.</para>
    ///
    /// <para>g3b LEFT THIS SENTENCE POINTING AT A SECTION THAT NO LONGER EXISTS, AND g3c IS THE ONLY RUN THE
    /// BOOK ROUTE HAS LOST A RECORD IN. The envelope change renamed the product corpus from <c>[GUIDES]</c>
    /// to <c>[PAGEDRAFT]</c> and dropped the class word from every document header, and it was reasoned about
    /// entirely on the PRODUCT route - see <see cref="ProductChatPrompt.GuidesMarker"/>, whose own note says
    /// the Book route's instructions "still say the guides below and now point at a section marked with the
    /// product's name, which is if anything a plainer referent". It is not plainer; it is dangling. After the
    /// rename, nothing this route composes below the instruction carries the word "guide" at all, so "the
    /// guides below" names no section in the data, and <see cref="BookGroundingHeadEn"/>'s back-reference
    /// ("the rule above about the guides") resolves onto a rule whose own referent has gone missing.</para>
    ///
    /// <para>THIS IS THE ONLY CHANGE g3c MADE THAT REACHES THE BOOK ROUTE AT ALL, which is what makes it the
    /// suspect for the one D-cell record that flipped: <c>D|he|2</c> ("מה המהלך הרגשי של תמר בפרק 8?"), answered
    /// correctly in g3 and g3b out of the same <c>chapter-text:7</c>, came back in g3c refusing on the ground
    /// that the chapter "מוצג כחלק ממבנה הרחבה של הספר ולא כתוכן מלא בנפרד" - a claim about how the envelope
    /// presented it, from a model reasoning about the shape of what it was handed. Every other candidate was
    /// ruled out by reading: the routed book rule and the hedge are byte-unchanged since g2, the artifact
    /// blocks and their whole-chapter labels were not touched, and <see cref="ProductChatBudget.Compose"/> is
    /// a DROP loop, so a system message that got SHORTER can only drop less and cannot downgrade a whole
    /// chapter to an excerpt.</para>
    ///
    /// <para>THE FIX POINTS THE SENTENCE AT THE SECTION THAT IS ACTUALLY THERE AND KEEPS THE CLASS WORD, which
    /// is what makes it cost nothing anywhere else. "the guides in the PageDraft section below" resolves
    /// against the marker by the only word the marker carries, and leaving "guides" in place keeps
    /// <see cref="BookGroundingHeadEn"/> - which <see cref="ChatRoute.Union"/> also composes - byte-unchanged
    /// and its back-reference valid on both arms. The marker is named by its WORD and never quoted as the
    /// bracketed literal, for the reason recorded twice in this file: quoting an internal token in order to
    /// instruct about it teaches the token. This does not re-open the narration question, because the Book
    /// route has narrated 0 of 28 in all three runs; it is the product route that elects a source noun, and
    /// nothing here reaches it. ONE RECORD IS ONE RECORD: this is a read defect worth fixing on its own terms,
    /// and whether it is what moved D|he|2 is a hypothesis gate 4 tests, not a finding.</para>
    /// </summary>
    internal const string BookProductRuleEn =
        "What you say about PageDraft itself comes only from the guides in the PageDraft section below, " +
        "never from outside knowledge about the product. ";

    /// <summary>
    /// THE GENERAL ROUTE (g2): a writing or literature question that is about neither the product nor the
    /// author's own manuscript. It is the route the owner's complaint is sharpest on - asked a craft
    /// question, Show reported which of his sources failed to cover it instead of answering.
    ///
    /// <para>POSITIVE, NOT PROHIBITIVE, AND THAT IS THE WHOLE DESIGN. The narration was CAUSED by an
    /// instruction to narrate, so the fix is to replace the instruction rather than to add a rule against
    /// its output; this prompt has three recorded instances of a stacked prohibition making a class worse.
    /// Two sentences: answer from what you know, and say something about PageDraft only where a guide says
    /// it.</para>
    ///
    /// <para>NO CITATION LINE RIDES ON THIS ROUTE, deliberately: an answer out of Show's own knowledge has
    /// no guide to name, and asking for a "Guides:" line under it would manufacture exactly the false
    /// sourcing this route exists to stop. <c>ProductChatService</c> therefore also hands the citation
    /// parser an EMPTY acceptable set here, so its no-line fallback (which returns the whole selection)
    /// cannot decorate a general answer with chips it never used. <c>ProductChatCitations</c> tolerates an
    /// answer with no citation line by contract, pinned in <c>ProductChatCitationContractTests</c>.</para>
    ///
    /// <para>AND SINCE g3 NO GUIDES RIDE ON IT EITHER, WHICH IS A COMPOSITION FIX AND NOT A PROMPT ONE.
    /// g2's version told the model to say something about PageDraft "only where the guides below say it",
    /// and the guides below really were there: this route drops the BOOK but the selection was still
    /// <see cref="GuideSelector.DefaultCount"/> documents of product prose. g3 measured the result on 8
    /// Hebrew craft turns and 3 of them invented a PageDraft behaviour - that Chapter recap detects
    /// repeated dialogue, that the Linguistic pass warns about emotional depth, that PageDraft warns you
    /// when you change narrative person, which no guide mentions at all. The model was improvising around
    /// product material it had been handed and told not to use. The material is now not handed to it
    /// (<c>ProductChatService.GeneralRouteGuideCount</c>), so the rule that governed it has nothing left
    /// to govern and is replaced by a statement of fact plus an offer: there is nothing about PageDraft in
    /// front of you, and a reader who wants that can have it as its own question. That is a scope and a
    /// next step, not a fourth prohibition.</para>
    /// </summary>
    internal const string GeneralGroundingEn =
        "This question is about writing rather than about PageDraft, so answer it from your own " +
        "knowledge of the craft, directly and in your own words. " +
        "Nothing about PageDraft is in front of you on this turn, so if they want to know what it does " +
        "here as well, say you can answer that as its own question. ";

    /// <summary>
    /// THE BOOK-SPECIFIC REFUSAL FOR A TURN WITH NO BOOK OPEN, AND THE ONE PLACE g3 CHANGED
    /// <see cref="ChatRoute.Union"/>.
    ///
    /// <para>WHAT IT USED TO SAY AND WHY THAT IS GONE. Until g3 this block told the model to say that
    /// "answering questions about a specific book is not available yet and is coming". Show has read the
    /// book since phase B, so the sentence had been FALSE for two phases; g2 took it off all three routes
    /// it composes and left it here because Union was defined as byte-identical to what g4 and g5
    /// measured. g3 then measured what that cost: the sentence reached a real user on 5 of 102 turns, and
    /// two of those were plain product questions that had merely missed the product lexicon. A false
    /// sentence is not a safety property. It is DELETED rather than gated behind a flag, because a flag
    /// would leave two versions of the truth in one file, and every byte literal that pinned it was
    /// re-typed by hand in the same commit - never pasted from the composer, which those tests' own
    /// comments forbid.</para>
    ///
    /// <para>WHAT REPLACES IT IS THE SENTENCE THE CODE PATH ALREADY SAYS. <c>ProductChatService</c>'s
    /// deterministic answer for this exact shape is "I can only see a book while it is open", and this
    /// block now instructs the same thing, so the model-mediated path and the model-free path cannot tell
    /// the author two different stories about the same product. The block is NOT deleted outright: it is
    /// the only thing on Union's book-less arm that keeps a book question from being answered out of the
    /// guides, and dropping it would put a manuscript question in front of a corpus that knows nothing
    /// about any manuscript.</para>
    ///
    /// <para>THE SHAPE IS STILL ANSWERED IN CODE FIRST. <c>ProductChatRouter.AsksAboutABookThatIsNotOpen</c>
    /// intercepts it before any model call whenever it can see it; this block governs only the turns that
    /// predicate does not recognise (g3: a book question whose guide top score cleared the strong-match
    /// bar). Two paths, one sentence.</para>
    ///
    /// <para>g3b GAVE IT THE HEBREW TWIN'S SHAPE, WHICH IS THE SHAPE THAT WAS ALREADY KNOWN TO WORK. Two
    /// changes. (1) The refusal is now the finished first-person sentence in quotes, word for word
    /// <c>ProductChatService.OpenTheBookEn</c>, exactly as <see cref="BookRefusalHe"/> has quoted
    /// <c>OpenTheBookHe</c> since g3. The English half had been left as an imperative ("say that you can only
    /// see a book while it is open, and ask them to open it and ask you again"), which is the construction
    /// that docstring records the model reading back verbatim - and the two halves of one rule disagreeing
    /// about their own construction is drift, not a decision. (2) "Do not attempt an answer from the guides
    /// in that case" named the source in an instruction, which is this round's whole subject; it now points
    /// at the place instead. Union composes this block, and this is the second time g3 has changed Union
    /// deliberately, for the same reason as the first: a sentence that is wrong is not a baseline.</para>
    /// </summary>
    internal const string BookRefusalEn =
        "If the question is about the content or state of the user's own book (its characters, its " +
        "plot, what a specific chapter says, what a review found), answer in the first person to this " +
        "effect: 'I can only see a book while it is open. Open the book you are asking about and ask me " +
        "again, and I will look at it.' Do not answer it from what is written below in that case. ";

    // PHASE B'S f2 SPLIT THE TAIL ONE FURTHER, AND CHANGED NO CHARACTER OF PHASE A'S HALF. The tail is
    // now CitationLine + Language, and the book-aware assembly swaps the citation sentence for one that
    // covers BOTH families of reference. That swap is the whole F-3 fix and it is a COLLISION fix, not a
    // new rule: B used to add "also name the book artifacts" in the middle of the message while phase A's
    // tail still ended it with "naming the guide ids you used, and nothing else on that line" - later,
    // unconditional, and narrower. The model resolved that collision toward the tail, which is exactly
    // what 80-85% empty artifactRefs looks like from the outside. There is now exactly ONE sentence about
    // the citation line in any composed message.

    /// <summary>
    /// THE LABEL IS KEPT AND THE DESCRIPTION AROUND IT IS NOT (g3b). This sentence used to say "naming the
    /// guide ids you used", and it is the ONE thing the product route composes that the general route does
    /// not, apart from the documents themselves - which makes it a carrier of the source noun on exactly the
    /// route that narrates. The word is dropped from the description, where it does nothing: on this route
    /// the only things carrying an id are the documents below, so "the ids you used" has one referent.
    ///
    /// <para>THE QUOTED LABEL 'Guides:' DOES NOT MOVE, and that is a deliberate limit on this change rather
    /// than an oversight. It is the one part of the citation mechanism g1 measured working (91.7%), this
    /// file's own note says it is not being bet on, and it is STRUCTURE rather than prose - the gate's
    /// detector strips the citation line before counting narration, so the label cannot contribute to the
    /// number this change is trying to move. Swapping it for the 'Sources:' the book-aware twin uses would
    /// bet a working mechanism for nothing measurable.</para>
    /// </summary>
    internal const string CitationLineEn =
        "End your reply with a line of the form 'Guides: <id>, <id>' naming the ids you used, " +
        "and nothing else on that line. ";

    internal const string LanguageEn =
        "Answer in English, because the question is in English, even where a guide you used is in " +
        "another language.";

    /// <summary>The Hebrew persona. DESCRIPTIVE, not imperative - see <see cref="PersonaEn"/>.</summary>
    internal const string PersonaHe =
        "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת. ";

    /// <summary>The Hebrew twin of <see cref="ProductGroundingEn"/>, carrying the same four
    /// verdicts.</summary>
    internal const string ProductGroundingHe =
        "ענה אך ורק מתוך תוכן המדריכים שמופיע למטה. " +
        "אל תשתמש בידע חיצוני על PageDraft, ולעולם אל תציין הגדרה, כפתור, מסך או התנהגות שאינם כתובים " +
        "במדריכים שניתנו. " +
        "אם המדריכים אינם עונים על השאלה, אמור זאת במפורש. אם יש נושא אחר שהם כן מכסים ורלוונטי " +
        "לשאלה, ציין אותו לפי המזהה שלו; אם אין, די בסירוב בלבד. אל תרכיב ניחוש מתוך חומר שרק חלקית " +
        "רלוונטי. " +
        "נסח זאת כפער במדריכים ולא כעובדה על המוצר: אל תאמר ש-PageDraft אינו תומך בכך. ואל תתאר מה " +
        "המדריכים אומרים על נושא שאינם עוסקים בו, גם לא כדי לציין מה מוזכר בהם לגביו. ";

    /// <summary>Phase A's Hebrew head, REASSEMBLED AT COMPILE TIME. See
    /// <see cref="GroundingEnHead"/>.</summary>
    internal const string GroundingHeHead = PersonaHe + ProductGroundingHe;

    /// <summary>The Hebrew twin of <see cref="ProductGroundingScopedEn"/>, re-framed the same way: the
    /// grounding is a place (<c>ממה שכתוב למטה</c>) rather than a substance, the gap constraint comes BEFORE
    /// the refusal it governs, and the refusal is the finished first-person sentence in quotes. It carries no
    /// occurrence of <c>מדריכים</c> and, since g3b, none of <c>החומר</c> either - that was the noun the
    /// Hebrew answers read back as <c>החומר שבידי</c>. DRAFT Hebrew (recorded in
    /// <c>src/docs/HEBREW_NATIVE_REVIEW.md</c>): the owner reads it.
    ///
    /// <para>The quoted sentence deliberately avoids the bare <c>אין לי מידע</c>, which is what six of g3b's
    /// Hebrew answers produced and what the gate's own detector counts as narration.</para></summary>
    internal const string ProductGroundingScopedHe =
        "מה שאתה אומר על PageDraft מגיע רק ממה שכתוב למטה ולעולם לא מידע חיצוני: אל תציין הגדרה, כפתור, " +
        "מסך או התנהגות שאינם כתובים שם, ואל תרכיב כזו מתוך חלקים שרק חלקית רלוונטיים. " +
        "חוסר במה שניתן לך אינו עובדה על המוצר, ולכן לעולם אל תאמר ש-PageDraft חסר דבר או אינו תומך בו. " +
        "כאשר התשובה אינה שם, אמור זאת בקצרה בקולך שלך ועצור, במשמעות הזו: 'אין לי את המידע הזה.' ";

    /// <summary>The Hebrew twin of <see cref="BookProductRuleEn"/>, pointed at the same section by the same
    /// word. DRAFT Hebrew (recorded in <c>src/docs/HEBREW_NATIVE_REVIEW.md</c>): the owner reads it.</summary>
    internal const string BookProductRuleHe =
        "מה שאתה אומר על PageDraft עצמו מגיע רק מהמדריכים שבמקטע PageDraft שלמטה ולא מידע חיצוני על המוצר. ";

    /// <summary>The Hebrew twin of <see cref="GeneralGroundingEn"/>. DRAFT Hebrew.</summary>
    internal const string GeneralGroundingHe =
        "השאלה הזו עוסקת בכתיבה ולא ב-PageDraft, ולכן ענה עליה מתוך הידע שלך על מלאכת הכתיבה, ישירות " +
        "ובמילים שלך. " +
        "שום דבר על PageDraft אינו מונח לפניך בתור הזה, ולכן אם רוצים לדעת גם מה הוא עושה בעניין, אמור " +
        "שתוכל לענות על כך כשאלה נפרדת. ";

    /// <summary>
    /// THE HEBREW BOOK-SPECIFIC REFUSAL IS A SENTENCE TO SAY, NOT AN ORDER TO FOLLOW. Phrased as an
    /// imperative ("say that ... and offer help with general questions"), the model read it back verbatim
    /// including the imperative: 2 of 18 Hebrew answers in g1, 6 of 6 runs of that question shape in g2.
    /// It is given as the finished first-person sentence, and g3's rewrite KEEPS that shape - the sentence
    /// inside the quotes changed, the construction that stopped the echo did not.
    ///
    /// <para>The quoted sentence is now word for word <c>ProductChatService.OpenTheBookHe</c>, so if the
    /// model does read it back verbatim, what the author sees is the same answer the deterministic path
    /// would have given them. DRAFT Hebrew. See <see cref="BookRefusalEn"/> for why the old one is
    /// gone.</para>
    ///
    /// <para>g3b CHANGED ONE CLAUSE AND NOT THE CONSTRUCTION. The closing sentence said
    /// <c>אל תנסה לענות מתוך המדריכים במקרה כזה</c>, naming the source in an instruction, which is the same
    /// defect g3b fixed in <see cref="BookRefusalEn"/>; it now points at the place. The quoted first-person
    /// sentence, which is the part that stopped the echo, is untouched to the byte.</para>
    /// </summary>
    internal const string BookRefusalHe =
        "אם השאלה נוגעת לתוכן או למצב של הספר הספציפי של המשתמש (הדמויות שבו, העלילה, מה כתוב בפרק " +
        "מסוים, מה סקירה מצאה), ענה בגוף ראשון במשמעות הזו: 'אני יכול לראות ספר רק כשהוא פתוח. פתחו את " +
        "הספר שעליו אתם שואלים ושאלו אותי שוב, ואסתכל בו.' אל תנסה לענות ממה שכתוב למטה במקרה כזה. ";

    /// <summary>The Hebrew twin of <see cref="CitationLineEn"/>. The description no longer says
    /// <c>מזהי המדריכים</c>; the quoted label keeps its <c>מדריכים:</c> for the reason recorded there, so
    /// this is the one occurrence of the noun the Hebrew product message still carries, and it sits inside a
    /// quoted line the detector strips. DRAFT Hebrew.</summary>
    internal const string CitationLineHe =
        "סיים את התשובה בשורה בצורה 'מדריכים: <מזהה>, <מזהה>' שמציינת את המזהים שהשתמשת בהם, " +
        "ובלי דבר נוסף באותה שורה. ";

    internal const string LanguageHe =
        "השב בעברית, כי השאלה נשאלה בעברית, גם אם מדריך שהשתמשת בו כתוב בשפה אחרת.";

    // ─── The book-aware citation sentence (phase B, f2, g1 finding F-3) ──────────────────────────
    //
    // FOUR THINGS IN ONE SENTENCE GROUP, because four prohibitions would collide with each other and
    // with the rule above them. It names the LABEL ("Sources"), which is what the line is: asking for a
    // book artifact under a label that reads "Guides" is a contradiction the model resolved by listing
    // guides. It says a guide is named "by its id alone", which is where g1's invented heading anchors
    // (guide-id#a-heading-that-does-not-exist) came from and, since guide headings are this codebase's
    // retrieval index, an invented anchor points at a retrieval key. It says an artifact is named by the
    // ref "written in that artifact's own header", which is the ONE place the correct ref is visible and
    // is the difference between citing chapter-text:3 and guessing chapter-brief:2 for the same answer.
    // And it says where refs live, which is what keeps a finding's raw guid out of the prose.
    //
    // The parser accepts BOTH labels (see ProductChatCitations), so a model falling back to the phase-A
    // wording out of habit still parses. That is deliberate: the label is the one part of this mechanism
    // that g1 measured working, and it is not being bet on.

    internal const string CitationLineBookAwareEn =
        "End your reply with a line of the form 'Sources: <ref>, <ref>' and nothing else on that line, " +
        "naming what you actually used: a guide by its id alone, and a book artifact by the ref in its " +
        "own header, for example 'Sources: chapter-text:7, status:review'. Refs belong on that line and " +
        "not in your sentences, where a finding is named by its dimension. ";

    internal const string CitationLineBookAwareHe =
        "סיים את התשובה בשורה בצורה 'מקורות: <מזהה>, <מזהה>' ובלי דבר נוסף באותה שורה, שמציינת את מה " +
        "שבאמת השתמשת בו: מדריך לפי המזהה שלו בלבד, ופריט של הספר לפי המזהה שבכותרת שלו, לדוגמה " +
        "'מקורות: chapter-text:7, status:review'. המזהים שייכים לשורה הזו ולא למשפטים שלך, שבהם ממצא " +
        "נקרא לפי הממד שלו. ";

    // ─── The B grounding rule (phase B, d1 section (3)) ──────────────────────────────────────────
    //
    // THE SAME SHAPE THAT CLOSED A'S GATE: it SCOPES what may be asserted and where it may come from.
    // It opens by scoping ITSELF against the head's guides rule ("the rule above ... governs questions
    // about PageDraft itself"), which is why no sentence of the head had to be reworded to make room for
    // it. It deliberately does NOT stack prohibitions: g3 measured a fourth prohibition failing to close
    // a class that a single scoping sentence then closed, because two emphatic rules that collide are
    // resolved by the model, not by the author.
    //
    // Its clauses map one-to-one onto d1: (1) answer from the book artifacts, and who you are answering;
    // (2) the briefs are SUMMARIES, so a gap is a gap in the briefs and never a fact about the book -
    // the phrase the plan names as carrying the whole risk. READ THE NOTE ON BookGroundingRoutedEn BEFORE
    // TREATING (2) AS THE CURRENT RULE: g2 REWROTE that clause for the Book route, because "say that the
    // briefs do not mention it" is the source-narration the owner reported, and the routed twin hedges
    // from Show's own vantage instead. The sentence below is unchanged ONLY because Union must stay
    // byte-identical to what g4 and g5 measured; (3) the whole-chapter vs excerpt label
    // decides which of the two shapes applies, which is why the label exists at all; (4) what the status
    // artifact licenses, and the answer when the briefs are behind, because a refusal there is a worse
    // answer than the truth; (5) the retrieval's own recorded ambiguity.
    //
    // WHAT f2 CHANGED, AND WHY THE CLAUSE COUNT DID NOT GROW. g1 returned four defects that all live in
    // this string, and the temptation each of them creates is a fresh prohibition. That is the move this
    // class has already recorded failing twice (g3's fourth prohibition; F-1's two emphatic rules), so
    // each defect is closed by WIDENING THE SCOPE STATEMENT OF THE CLAUSE THAT ALREADY OWNS IT, and B's
    // own citation sentence moved OUT to the tail, where phase A's already lived. That move is the F-3
    // fix: two sentences about one line, the later and narrower of which said "the guide ids you used",
    // is the same collision F-1 was, and 80-85% of book-scoped runs resolved it toward the tail.
    //
    // (1) gains the READER. g1's `m1` opened by addressing the author as "מירב," - a character out of
    //     their own manuscript. Nothing in the prompt had ever said who is being written to, so the
    //     artifacts' cast was the only roster of names in scope.
    // (3) gains who the LABELS are for. The whole/EXCERPT label is a d1 section (3) safety property and it
    //     passed 12/12, so it is not being removed or softened; what leaked (5 of 6 and 6 of 6 runs) is
    //     the model quoting the raw token at the author. Saying the author never sees it, and that the
    //     distinction therefore has to travel in the sentence, keeps the label doing its job for the model.
    // (4) gains WHAT A STATUS LICENSES, which is the F-5 fix and the one place the wording is lifted from
    //     the transcript rather than invented: g1 recorded "the status artifact indicates that three
    //     chapters are missing or out of date; it does not specify which ones they are" occurring
    //     naturally. A count is not a list, a number is to be given as written (it was restated wrong 2 of
    //     3 runs on a scalar the block states literally), and a reason the status names is this book's
    //     reason (the Hebrew twin of a question English got 3/3 recited the guides' generic list of
    //     causes instead, 0/6).
    // WHAT a1 (AMBIENT CHAPTER, d2) CHANGED, AND WHY THE CLAUSE COUNT STILL DID NOT GROW. Two clauses were
    // WIDENED and no sixth was added, which is the move this file has now recorded working three times.
    // (1) gains what a co-present GUIDE may be used for. d2 section (6), from the defect that opened this
    //     plan: the owner's turn carried 13 book artifacts - the chapter's own brief, their own edited
    //     summary, five findings - and the answer came out of the faq guide citing no book artifact at
    //     all. BookAwareGuideCount = 2 means 1-2 guides ride along on EVERY book-scoped turn, so an
    //     irrelevant alternative source is always sitting beside the book artifacts; clause (1) scoped
    //     which RULE applied to which question but never said what a guide is still good FOR once a
    //     book-scoped answer is being built. The added sentence is PERMISSIVE about process content (a
    //     mixed question still legitimately cites a guide, measured clean 6/6 in g2) and restrictive only
    //     about content the book artifacts already cover. It scopes; it does not prohibit.
    // (5) gains the SECOND note. The BOOK section now carries one of two mutually exclusive notes (see
    //     BookArtifactBlocks.BookSectionNote), and the clause that already owned "a note in the BOOK
    //     section" now says what to do with either, rather than a new clause about asking which chapter.
    //     The asking is only ever the FALLBACK: when a chapter resolved - explicitly or from the chapter
    //     the author has open - no note is emitted, because the flag that emits it is false by
    //     construction, so this sentence cannot fire on a turn that already knows its chapter.
    //
    // WHAT be-c02 (THE CHAPTER-NUMBERING SEAM, review finding #1, the P0) CHANGED. Clause (3) was WIDENED
    // again and no sixth clause was added, which is the fourth time this move has been used here.
    //
    // THE DEFECT. A deictic question on a chapter at order 0 produced an answer opening 'בפרק שנקרא "צל
    // הירח" (שהוא למעשה פרק 0...)' while the citation chip rendered directly beneath it in the same answer
    // card read "הטקסט של פרק 1". Same chapter, two numbers, one card. The server is 0-based everywhere by
    // construction (labels, refs, the brief heading, the history lines) and the client's chapterDisplayNumber
    // is documented as "the ONLY thing a human ever reads (order + 1)", and the two conventions met NOWHERE:
    // neither grounding string said a word about which numbering the author uses. Three gates missed it
    // because every one of their questions named a chapter EXPLICITLY, where the model echoes the author's
    // own number back and the offset is invisible.
    //
    // WHY THIS SEAM AND NOT A RENDERING ONE. The alternative was to render the human-readable half of each
    // block in the author's numbering while the refs kept the wire key, and that is normally the stronger
    // choice because it is verifiable by READING rather than by a model run. It cannot work here, for two
    // reasons found by enumerating every site (the tables are in the plan's investigation section):
    //   1. The chapter-brief block's BODY is BookContextAssembler.FormatChapterBrief's "## Chapter {order}:
    //      {title}", shared verbatim with the whole-book review by a d1 decision. PromptFactory's
    //      ChapterOrderRuleEn/He instructs every BookReview surface to copy the order EXACTLY as it appears
    //      in that heading and states that orders start at 0, and ChapterAnchorResolver resolves the model's
    //      answer against real 0-based orders and DROPS what does not resolve. Renumbering it breaks finding
    //      anchoring; forking a chat-only copy re-creates this very defect one layer down.
    //   2. The REF cannot move (the client parses it), and the model has been OBSERVED quoting a ref into
    //      prose. Rendering "[CHAPTER 1, whole chapter]" directly above "ref=chapter-text:0" would put the
    //      two numbers for one chapter INSIDE the prompt, which is strictly worse than one honest
    //      convention plus a translation rule.
    // So the artifacts keep ONE convention (0-based, the wire's) and the prompt carried ONE translation
    // rule, in the clause that already says those bracketed labels are internal and that only the model's
    // own sentence carries them across to the author.
    //
    // WHAT final-r02 CHANGED, AND WHY THAT SENTENCE IS GONE. g4 measured be-c02's rule and it did not hold:
    // 16 of the 20 answers that named a chapter number disagreed with the chips rendered on the same answer,
    // and the split was sharp - order 0 (the order the rule's own worked example used) scored 4 pass /
    // 3 fail, every order above it scored 0 pass / 9 fail. The model reproduced the ONE worked example and
    // never applied +1 as an operation. The owner's decision was to fix the SEAM rather than the prompt: the
    // author-facing name is now PRE-COMPUTED and rendered on every chapter-scoped block
    // (BookArtifactBlocks.AuthorFacingChapterName), so the model's cheapest correct action is to copy a
    // finished string. Note this does NOT reverse the two reasons above - the refs and the brief heading
    // still may not move, and the block's own internal label is unchanged; a LABELLED author-facing line
    // beside a LABELLED internal one is a different thing from option 2's two unlabelled numbers.
    //
    // The clause therefore stops teaching the offset and points at that line instead. It is a NARROWING,
    // not a fifth prohibition: naming the token in order to keep it internal is what final-r03 believes
    // taught the model to print [CHAPTER n] into the author's prose (g4: 3 of 38), so no literal bracketed
    // label is quoted in either grounding string any more, and none may be re-introduced.
    //
    // THE INVARIANT: no surface the author can read shows two different numbers for one chapter. Pinned by
    // ProductChatChapterNumberingTests, whose cross-stack half names the client spec that pins the other
    // side of it. THE RENDERED LINE'S SHAPE IS VERIFIABLE BY READING; WHETHER THE MODEL COPIES IT IS NOT,
    // so the class stays a hypothesis until the measurement that follows final-r02 runs.
    //
    // WHAT be-c03 (A CARRIED REF IN THE PROSE, review finding #3) CHANGED. Clause (3) was widened ONE MORE
    // sentence, into the same internal-vs-author-facing statement be-c02 had just put there, and again no
    // clause was added.
    //
    // THE DEFECT. A Hebrew answer read '...כפי שמסומן בקובץ chapter-text:0)' - "as marked in the file
    // chapter-text:0" - so the model handed the author a wire key and described it as a FILE they could
    // look at. The RTL consequence made it worse than jargon: an LTR slug inside Hebrew prose drags its
    // closing parenthesis to the wrong end, so it rendered as "chapter-text:0 ),". Nothing downstream
    // removes it either, by an explicit decision in ProductChatCitations (a leaked label mid-answer is
    // cosmetic; a deleted sentence is not).
    //
    // WHY THE CITATION SENTENCE WAS NOT THE PLACE. It already says where refs BELONG ("Refs belong on that
    // line and not in your sentences"), which is a rule the model was given and did not follow. Restating
    // it there, harder, is the prohibition-stacking move this file has recorded failing twice. What was
    // missing is not a place-rule but the IDENTITY of the token: nothing said a ref is an internal key the
    // author never sees. Clause (3) is where that fact already lives for the bracketed labels, and it is
    // one sentence away, so the refs join it. The two clauses now divide the work the way they already
    // did for the labels: (3) says what the token IS, the citation sentence says where it GOES.
    //
    // The old "Their numbers are internal too" was subsumed rather than dropped: the clause after it stated
    // the numbering in full, so be-c02's seam was byte-unchanged in what it asserted. THAT SECOND HALF IS
    // NOW GONE (final-r02, see above) and the identity statement is what survives - the refs and the labels
    // are internal, and their numbers are internal counting. That is the half be-c03 needed; nothing it
    // asserts depended on the offset being spelled out. UNVERIFIABLE BY READING, like every prompt edit
    // here: g4 measured whether the leak rate actually falls, and it did not close (5 of 38).
    //
    // FINDING #7 (the chapter named by the BOOK's title) IS NOT HERE, ON PURPOSE. It was a RENDERING
    // defect - two titles in one section with nothing marking which was which - and it is fixed where it
    // was rendered (the Book line in ProductChatPrompt, and BookArtifactBlocks.BookBrief/ChapterText), for
    // zero prompt tokens and with the result checkable by reading the composed string.
    //
    // (5) was NEW at the time and was the only added clause: at that point the assembler deliberately
    //     grounded BOTH candidates for a bare "chapter N" because Order is 0-based and authors count from
    //     1. g1 confirmed the model did not merge them into one false claim - it silently picked one. The
    //     honest ambiguity existed in the data and was thrown away at the prompt boundary; the note that
    //     carried it was emitted ONLY when both candidates actually rode, so no ordinary chapter answer
    //     acquired a hedge. w9 later replaced the manufactured pair with deterministic resolution and
    //     be-c01 rewrote this clause for the notes w9 now emits - see "THE NOTE CLAUSE" below for what is
    //     true now.
    //
    // EVERY SENTENCE HERE IS PAID FOR TWICE (system slot + instruction head) AND THE HEBREW RATE IS
    // 1.8 chars/token, so the wording above went through a deliberate concision pass after the first
    // draft measured 40 tokens over what the 40-chapter worst case had left. ProductChatBookPromptTests
    // reports the size of both modes in both languages, so the next edit here starts from a number.
    //
    // ─── THE NOTE CLAUSE, LAST SENTENCE OF BOTH STRINGS (be-c01) ────────────────────────────────
    //
    // IT DEFERS TO THE NOTE INSTEAD OF ENUMERATING NOTES, and that is the whole of the fix. It used to
    // hard-code two branches - "where it says a number could have meant two chapters, say which one you
    // answered for and that it could have meant the other; where it says no chapter was identified, ask
    // which chapter they mean". w9 then rewrote what the notes SAY, and every one of those three facts
    // went false at once: the ambiguity note now ASKS rather than licensing an answer for one of the
    // candidates (answering for a chapter picked by sort order is the defect w9 exists to remove), it
    // names up to five candidates rather than two, and two of the three note shapes it must govern (a
    // shared TITLE, and a number the book does not have) match neither branch and reached the model with
    // no rule at all, next to a sentence telling it to answer for one. Observed in the composed prompt in
    // both languages on exactly the input w9 was built for.
    //
    // THE FIX IS A SCOPE, NOT A FOURTH PROHIBITION. This prompt has three recorded instances of being
    // made measurably WORSE by stacking another rule onto a collision (g3's fourth prohibition; F-1's two
    // rules; phase A be-c03, whose added prohibition raised the rate it was meant to lower), and what has
    // worked here every time is scoping an instruction. Two of the three notes now carry their own
    // instruction in their own text, so the clause says to do what the note says; only the flat "no
    // chapter was identified" note is bare, and the clause's second half supplies the ask for it WITHOUT
    // naming it, by keying on the note saying no step rather than on its trigger words. The enumeration
    // is what broke, so nothing here enumerates. It refers to "the note" generically for a second reason:
    // quoting an internal literal in order to instruct about it TEACHES the literal (measured: naming
    // [CHAPTER 0] in this clause's neighbourhood put it in the author's prose 3 of 38; deleting the
    // exemplar took it to 0 of 114).
    //
    // WHAT IS PROVEN AND WHAT IS NOT. The suite pins the ASSEMBLY only: that both languages carry the new
    // clause, that neither carries the answer-for-one wording, and that all three note shapes still reach
    // the composed instruction. IT PROVES NOTHING ABOUT WHAT THE MODEL DOES WITH THE NEW CLAUSE. The
    // behavioural half is UNMEASURED - no GPU gate has been run against this wording in either language,
    // so the class this clause was rewritten for is NOT closed. Do not read the green suite as a verdict.

    // ─── THE BRIEFS FENCE IS ITS OWN BLOCK SINCE g2, AND THE SPLIT CHANGED NO CHARACTER ─────────
    //
    // Same technique, and the same reason, as g1's persona/product-grounding split one screen up: the
    // routed Book message needs to swap EXACTLY ONE sentence of this rule, and the alternative to a seam
    // is a second copy of twenty sentences that carry measured verdicts. Head + Fence + Tail is a const
    // expression, so the compiler guarantees BookGroundingEn is the string it always was, and
    // ProductChatRoutePartitionTests pins it against a hand-typed literal anyway.
    //
    // WHY THE FENCE HAD TO MOVE AT ALL, AND WHY IT COULD NOT SIMPLY BE DELETED (g2, plan item 8d). The
    // sentence tells the model to say "the briefs do not mention it", which is the owner's reported
    // defect stated as an instruction: Show describing his sources to the author instead of answering
    // about the world. But it is also the ONLY thing standing between a 6-brief sample and "X does not
    // happen in your book", which is a false claim about a manuscript the author would act on. So it is
    // REPLACED rather than removed: the hedge is taken from Show's own vantage ("you cannot see it from
    // what is in front of you"), it offers the next step that actually resolves the question (look at the
    // chapter itself), and the ban on asserting absence survives verbatim in force if not in wording.
    // The word "briefs" is gone, and with it the meta-frame; the fence is not.

    internal const string BookGroundingHeadEn =
        "If the question is about the content or state of the user's own book (its characters, its " +
        "plot, what a specific chapter says, what a review found), answer it from the BOOK section " +
        "below and from nothing else; the rule above about the guides governs questions about " +
        "PageDraft itself. A guide may still help explain how the product works, but it does not stand " +
        "in for what the book artifacts themselves say. " +
        "You are writing to the AUTHOR of this book; the names in these artifacts are " +
        "the people in it. " +
        "Every book artifact carries a ref in its header, and what you say about the book is what those " +
        "artifacts say. ";

    /// <summary>UNION'S fence, byte-unchanged since phase B. The narration the owner reported.</summary>
    internal const string BookBriefsFenceEn =
        "The chapter briefs are SUMMARIES of the chapters, so where they do not cover " +
        "something, say that the briefs do not mention it; whether it happens in the book is something " +
        "they cannot tell you. ";

    /// <summary>THE BOOK ROUTE'S fence (g2): the same prohibition, hedged from Show's own vantage and
    /// carrying the offer that resolves the question, with no sentence about what his sources are.</summary>
    internal const string BookBriefsHedgeEn =
        "Where what you have does not cover something, say that you cannot see it from what is in " +
        "front of you and offer to look at the chapter itself; never say that it does not happen in " +
        "the book. ";

    internal const string BookGroundingTailEn =
        "A chapter given to you as 'whole chapter' is that chapter's complete text, so for that one " +
        "chapter you can say what it does and does not contain. A chapter given to you as 'EXCERPT' is " +
        "part of it, so there say what the parts you could read do and do not mention. Each of those " +
        "covers its own chapter and no other. Those bracketed labels are for you and the author never " +
        "sees them, so only your own sentence carries that difference to them. The refs are internal " +
        "too and the author never sees them either, and so are their numbers. Each chapter's block " +
        "carries a line with the name the author has for it; name a chapter by copying that line. " +
        "The status artifact gives counts and states, not lists: a count of chapters that are behind is " +
        "what you know, which chapters they are is something it does not say, and where it names a " +
        "reason, that reason is this book's reason. Give its numbers exactly as written. When what the " +
        "question needs is missing or out of date, the answer is that state plus the next step it calls " +
        "for. " +
        "A note in the BOOK section about what the question could have meant belongs in the answer: do " +
        "what the note says, and where it does not say what to do, ask about what remains unclear " +
        "before answering about a particular chapter. ";

    /// <summary>Phase B's book rule, REASSEMBLED AT COMPILE TIME. Byte-identical to the single literal it
    /// used to be, by construction rather than by care. <see cref="ChatRoute.Union"/> composes this.</summary>
    internal const string BookGroundingEn = BookGroundingHeadEn + BookBriefsFenceEn + BookGroundingTailEn;

    /// <summary>The <see cref="ChatRoute.Book"/> rule: the same twenty sentences with the fence swapped
    /// for the hedge. Exactly one sentence differs from <see cref="BookGroundingEn"/>, by
    /// construction.</summary>
    internal const string BookGroundingRoutedEn = BookGroundingHeadEn + BookBriefsHedgeEn + BookGroundingTailEn;

    internal const string BookGroundingHeadHe =
        "אם השאלה נוגעת לתוכן או למצב של הספר של המשתמש (הדמויות שבו, העלילה, מה כתוב בפרק מסוים, מה " +
        "סקירה מצאה), ענה עליה מתוך מקטע הספר שמופיע למטה ומשום מקור אחר; הכלל שלמעלה לגבי המדריכים " +
        "חל על שאלות על PageDraft עצמו. מדריך עדיין יכול לעזור להסביר איך המוצר עובד, אך הוא אינו " +
        "מחליף את מה שפריטי הספר עצמם אומרים. " +
        "אתה כותב אל המחבר של הספר הזה; השמות שבפריטים האלה הם הדמויות " +
        "שבו. " +
        "לכל פריט של הספר יש מזהה בכותרת שלו, ומה שאתה אומר על הספר הוא מה שהפריטים האלה אומרים. ";

    /// <summary>Union's Hebrew fence, byte-unchanged. See <see cref="BookBriefsFenceEn"/>.</summary>
    internal const string BookBriefsFenceHe =
        "תקצירי הפרקים הם סיכומים של הפרקים, ולכן כאשר הם אינם מכסים משהו, אמור שהתקצירים אינם מזכירים " +
        "זאת; האם זה קורה בספר הוא דבר שהם אינם יכולים לומר לך. ";

    /// <summary>The Book route's Hebrew hedge (g2). DRAFT Hebrew. See
    /// <see cref="BookBriefsHedgeEn"/>.</summary>
    internal const string BookBriefsHedgeHe =
        "כאשר מה שיש לפניך אינו מכסה משהו, אמור שאינך רואה זאת ממה שלפניך והצע להסתכל בפרק עצמו; " +
        "לעולם אל תאמר שזה אינו קורה בספר. ";

    internal const string BookGroundingTailHe =
        "פרק שניתן לך כ'whole chapter' הוא הטקסט המלא של אותו פרק, ולכן לגבי אותו פרק בלבד תוכל לומר " +
        "מה יש בו ומה אין בו. פרק שניתן לך כ'EXCERPT' הוא חלק ממנו, ולכן שם אמור מה החלקים שהצלחת " +
        "לקרוא מזכירים ומה אינם מזכירים. כל אחד מהם חל על הפרק שלו ולא על פרק אחר. התוויות בסוגריים " +
        "נועדו לך והמחבר אינו רואה אותן, ולכן רק המשפט שלך מעביר אליו את ההבחנה הזו. גם המזהים " +
        "פנימיים והמחבר אינו רואה גם אותם, וגם לא את המספרים שבהם. בבלוק של כל פרק יש שורה עם השם " +
        "שהמחבר משתמש בו; ציין פרק בהעתקת השורה הזו. " +
        "פריט הסטטוס נותן מספרים ומצבים ולא רשימות: מספר הפרקים שמפגרים מאחור הוא מה שידוע לך, אילו " +
        "פרקים אלה הוא אינו אומר, וכאשר הוא נוקב בסיבה, הסיבה הזו היא הסיבה של הספר הזה. מסור את " +
        "המספרים שלו בדיוק כפי שהם כתובים. כאשר מה שהשאלה צריכה חסר או אינו מעודכן, התשובה היא המצב " +
        "הזה יחד עם הצעד הבא שהוא מחייב. " +
        "הערה במקטע הספר על מה שהשאלה יכלה להתכוון אליו שייכת לתשובה: עשה מה שההערה אומרת, וכאשר היא " +
        "אינה אומרת מה לעשות, שאל על מה שנותר לא ברור לפני שתענה על פרק מסוים. ";

    /// <summary>Phase B's Hebrew book rule, REASSEMBLED AT COMPILE TIME. See
    /// <see cref="BookGroundingEn"/>.</summary>
    internal const string BookGroundingHe = BookGroundingHeadHe + BookBriefsFenceHe + BookGroundingTailHe;

    /// <summary>The <see cref="ChatRoute.Book"/> Hebrew rule. See
    /// <see cref="BookGroundingRoutedEn"/>.</summary>
    internal const string BookGroundingRoutedHe = BookGroundingHeadHe + BookBriefsHedgeHe + BookGroundingTailHe;
}
