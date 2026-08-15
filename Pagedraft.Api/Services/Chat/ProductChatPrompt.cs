using System.Text;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE prompt composition for chatbot phase A (c1), carrying d1's grounding contract (item 2), its
/// book-specific refusal (item 5) and its language rule (item 3).
///
/// <para>WHY THE RULE IS STATED TWICE. <see cref="SystemMessage"/> is what
/// <c>PromptFactory.GetPrompt(AiTaskType.ProductChat, ...)</c> returns and what a provider puts in
/// its system slot; <see cref="ComposeInstruction"/> restates the same three rules at the head of the
/// user message, immediately above the guide text they govern. That is not redundancy for its own
/// sake: the local provider concatenates system + instruction + input into one prompt and Ollama
/// truncates from the START when a prompt overruns the window, so a rule that lived only in the
/// system slot is the first thing lost in exactly the situation where losing it is worst.</para>
///
/// <para>NO TERMINOLOGY MAPPING (d1 item 6). The guides still say "book summary" where Wave 3's
/// reconciled vocabulary says "book briefs". Phase A ships against the guides EXACTLY as they read
/// today and adds NO vocabulary-substitution instruction, because an answer that says "book briefs"
/// while citing a guide that says "book summary" is the citation/text mismatch the grounding contract
/// exists to prevent. The guides copy-edit is a separate prerequisite that has not run.</para>
///
/// <para>NO META-CLAIM ABOUT AN ABSENT TOPIC (the g2 HALT). The original rule forbade stating a
/// setting, button, screen or behavior the guides do not state, and required naming what they DO
/// cover on a refusal. g2's `b7` run1 obeyed BOTH and still fabricated, by asserting something about
/// the CORPUS instead of about the product: "the only shortcuts mentioned in the text are related to
/// saving chapters or dismissing cards", against a corpus with zero occurrences of shortcut, keyboard,
/// ctrl or their Hebrew equivalents. Characterizing what the guides say about a topic they never
/// mention was not forbidden anywhere, so both strings now forbid it explicitly, while still
/// permitting the pivot that works (naming, and quoting, a topic the guides DO cover). Both strings
/// also now say to frame a gap as a gap in the GUIDES rather than as a fact about the product: g2's
/// Hebrew `d4` asserted "PageDraft does not support exporting EPUB", which the guides never say. Same
/// family as the HALT, so the two clauses sit together and reinforce each other.</para>
///
/// <para>WHY THE PIVOT IS CONDITIONAL, NOT MANDATORY (the g3 HALT). Adding the prohibition above did
/// not close the class: g3 still saw 2 of 39 adjacent runs fabricate, one of them now quoting
/// "Cmd/Ctrl+S" as something the guides describe. The cause was a COLLISION, not a missing rule. The
/// refusal sentence demanded, unconditionally, that a refusal name what the guides DO cover; on the
/// one question shape "which X does the product have?" where the corpus contains no X at all, every
/// honest referent is absent, so the only way to satisfy that demand is to report what the guides
/// supposedly say about X, which is exactly what the new prohibition forbids. The model resolved the
/// conflict toward the older, more emphatic clause. The fix is to SCOPE the demand rather than add a
/// fourth prohibition: the pivot is now conditioned on the guides actually covering ANOTHER relevant
/// topic, and a bare refusal is stated to be a complete answer when they do not. It is permitted, not
/// required, because g3 measured the pivot working (`b1` refuses EPUB and then correctly quotes what
/// export does produce; `b2`, `b5`, `b8` likewise), and losing it would be a real cost. The positive
/// restatement that followed the prohibition ("describe their contents only for topics they DO
/// address") is dropped: the scoped sentence now states the same thing more precisely, and the Hebrew
/// budget has only 274 tokens of headroom with this string counted twice.</para>
///
/// <para>THE HEBREW BOOK-SPECIFIC REFUSAL IS A SENTENCE TO SAY, NOT AN ORDER TO FOLLOW. Phrased as an
/// imperative ("say that ... and offer help with general questions"), the model read it back verbatim
/// including the imperative: 2 of 18 Hebrew answers in g1, 6 of 6 runs of that question shape in g2.
/// It is now given as the finished first-person sentence. The English twin never echoed (0 of 18) and
/// is deliberately left alone, so the change carries no risk to a measured-clean bucket.</para>
///
/// <para>VOICE, AND WHAT IT MAY NOT BUY (phase A.2, c2). The assistant is named Show, and the persona
/// sentence that now opens both strings is REGISTER ONLY: first person, warm, brief, and opening from
/// what was actually asked. It states no rule and scopes none. Everything g4 measured is byte-identical
/// underneath it - the grounding contract, both refusal rules, and final-r02's scoped instruction 1 -
/// because g4's PASS (0 fabricated product behaviors in 48 adjacent runs, 48 of 48 pivots intact) is a
/// measurement of those exact sentences and of nothing else.</para>
///
/// <para>Two things were deliberately NOT written here, and both are the temptation this change had to
/// walk past. (1) Nothing asks for varied or non-formulaic openings. Every clean refusal g4 recorded
/// opens with the same honest formula ("The provided guides do not state which keyboard shortcut runs a
/// pass such as Proofread"), and a demand to vary it applies pressure precisely on the question shape
/// where the g2 and g3 fabrications lived. Variation is left to come out of "open from what was asked",
/// which produces it per question without asking the model to leave that groove. (2) Nothing prefers
/// paraphrase over quoting a guide. g4's pivots are clean because they are verbatim corpus lines, so
/// "less guide-recitation" is answered in the assistant's VOICE and never in its sourcing.
/// Friendliness comes out of voice, never out of facts.</para>
///
/// <para>The Hebrew persona sentence is DESCRIPTIVE ("אתה כותב"), not imperative, for the reason the
/// paragraph below records twice over: an imperative in this string has leaked verbatim into
/// user-visible Hebrew answers at two separate clauses (g1/g2 F4, and again at g4's new `e1` locus).
/// A self-description gives the model a voice to speak in rather than an order to read back.</para>
///
/// <para>No em-dash appears in any string here: these strings reach the user, and the model echoes
/// punctuation from its frame.</para>
/// </summary>
public static class ProductChatPrompt
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

    private const string GroundingEnHead =
        "You are Show, the PageDraft product assistant. You write in the first person, warmly and " +
        "briefly, and you open each reply from what was actually asked. " +
        "Answer ONLY from the guide content provided below. " +
        "Do not use outside knowledge about PageDraft, and never state a setting, button, screen or " +
        "behavior that the provided guides do not state. " +
        "If the guides do not address the question, say so plainly. If another topic they DO cover is " +
        "genuinely relevant, name it and its guide id; if none is, a bare refusal is the whole " +
        "answer. Do not assemble a guess out of partially relevant material. " +
        "State it as a gap in the guides, not as a fact about the product: do not say that PageDraft " +
        "lacks the thing or does not support it. And do not describe what the guides say about a topic " +
        "they do not address, not even to report what they mention about it. ";

    private const string BookRefusalEn =
        "If the question is about the content or state of the user's own book (its characters, its " +
        "plot, what a specific chapter says, what a review found), say that answering questions about " +
        "a specific book is not available yet and is coming, and offer help with general product and " +
        "workflow questions instead. Do not attempt an answer from the guides in that case. ";

    // PHASE B'S f2 SPLIT THE TAIL ONE FURTHER, AND CHANGED NO CHARACTER OF PHASE A'S HALF. The tail is
    // now CitationLine + Language, and the book-aware assembly swaps the citation sentence for one that
    // covers BOTH families of reference. That swap is the whole F-3 fix and it is a COLLISION fix, not a
    // new rule: B used to add "also name the book artifacts" in the middle of the message while phase A's
    // tail still ended it with "naming the guide ids you used, and nothing else on that line" - later,
    // unconditional, and narrower. The model resolved that collision toward the tail, which is exactly
    // what 80-85% empty artifactRefs looks like from the outside. There is now exactly ONE sentence about
    // the citation line in any composed message.

    private const string CitationLineEn =
        "End your reply with a line of the form 'Guides: <id>, <id>' naming the guide ids you used, " +
        "and nothing else on that line. ";

    private const string LanguageEn =
        "Answer in English, because the question is in English, even where a guide you used is in " +
        "another language.";

    private const string GroundingHeHead =
        "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת. " +
        "ענה אך ורק מתוך תוכן המדריכים שמופיע למטה. " +
        "אל תשתמש בידע חיצוני על PageDraft, ולעולם אל תציין הגדרה, כפתור, מסך או התנהגות שאינם כתובים " +
        "במדריכים שניתנו. " +
        "אם המדריכים אינם עונים על השאלה, אמור זאת במפורש. אם יש נושא אחר שהם כן מכסים ורלוונטי " +
        "לשאלה, ציין אותו לפי המזהה שלו; אם אין, די בסירוב בלבד. אל תרכיב ניחוש מתוך חומר שרק חלקית " +
        "רלוונטי. " +
        "נסח זאת כפער במדריכים ולא כעובדה על המוצר: אל תאמר ש-PageDraft אינו תומך בכך. ואל תתאר מה " +
        "המדריכים אומרים על נושא שאינם עוסקים בו, גם לא כדי לציין מה מוזכר בהם לגביו. ";

    private const string BookRefusalHe =
        "אם השאלה נוגעת לתוכן או למצב של הספר הספציפי של המשתמש (הדמויות שבו, העלילה, מה כתוב בפרק " +
        "מסוים, מה סקירה מצאה), ענה בגוף ראשון במשמעות הזו: 'מענה על שאלות לגבי ספר מסוים עדיין אינו " +
        "זמין, והיכולת בדרך. אשמח לעזור בשאלות כלליות על המוצר ועל תהליך העריכה.' אל תנסה לענות מתוך " +
        "המדריכים במקרה כזה. ";

    private const string CitationLineHe =
        "סיים את התשובה בשורה בצורה 'מדריכים: <מזהה>, <מזהה>' שמציינת את מזהי המדריכים שהשתמשת בהם, " +
        "ובלי דבר נוסף באותה שורה. ";

    private const string LanguageHe =
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

    private const string CitationLineBookAwareEn =
        "End your reply with a line of the form 'Sources: <ref>, <ref>' and nothing else on that line, " +
        "naming what you actually used: a guide by its id alone, and a book artifact by the ref in its " +
        "own header, for example 'Sources: chapter-text:7, status:review'. Refs belong on that line and " +
        "not in your sentences, where a finding is named by its dimension. ";

    private const string CitationLineBookAwareHe =
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
    // the phrase the plan names as carrying the whole risk; (3) the whole-chapter vs excerpt label
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
    // was rendered (the Book line below, and BookArtifactBlocks.BookBrief/ChapterText), for zero prompt
    // tokens and with the result checkable by reading the composed string.
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

    private const string BookGroundingEn =
        "If the question is about the content or state of the user's own book (its characters, its " +
        "plot, what a specific chapter says, what a review found), answer it from the BOOK section " +
        "below and from nothing else; the rule above about the guides governs questions about " +
        "PageDraft itself. A guide may still help explain how the product works, but it does not stand " +
        "in for what the book artifacts themselves say. " +
        "You are writing to the AUTHOR of this book; the names in these artifacts are " +
        "the people in it. " +
        "Every book artifact carries a ref in its header, and what you say about the book is what those " +
        "artifacts say. The chapter briefs are SUMMARIES of the chapters, so where they do not cover " +
        "something, say that the briefs do not mention it; whether it happens in the book is something " +
        "they cannot tell you. " +
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

    private const string BookGroundingHe =
        "אם השאלה נוגעת לתוכן או למצב של הספר של המשתמש (הדמויות שבו, העלילה, מה כתוב בפרק מסוים, מה " +
        "סקירה מצאה), ענה עליה מתוך מקטע הספר שמופיע למטה ומשום מקור אחר; הכלל שלמעלה לגבי המדריכים " +
        "חל על שאלות על PageDraft עצמו. מדריך עדיין יכול לעזור להסביר איך המוצר עובד, אך הוא אינו " +
        "מחליף את מה שפריטי הספר עצמם אומרים. " +
        "אתה כותב אל המחבר של הספר הזה; השמות שבפריטים האלה הם הדמויות " +
        "שבו. " +
        "לכל פריט של הספר יש מזהה בכותרת שלו, ומה שאתה אומר על הספר הוא מה שהפריטים האלה אומרים. " +
        "תקצירי הפרקים הם סיכומים של הפרקים, ולכן כאשר הם אינם מכסים משהו, אמור שהתקצירים אינם מזכירים " +
        "זאת; האם זה קורה בספר הוא דבר שהם אינם יכולים לומר לך. " +
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

    // ─── Section markers. ASCII and language-independent so a test can assert on them ────────────

    internal const string GuidesMarker = "[GUIDES]";
    internal const string BookMarker = "[BOOK]";

    /// <summary>
    /// THE BOOK'S OWN TITLE, SAID TO BE THE BOOK'S (be-c03, review finding #7). It used to render as a
    /// bare <c>Book: &lt;title&gt;</c> at the head of the BOOK section, where the only other titles in
    /// scope are CHAPTER titles (the <c>ChapterText</c> heading and the brief's "## Chapter N: title"),
    /// and an answer was OBSERVED naming the open chapter by the book's title: 'בפרק שנקרא "צל הירח"',
    /// where צל הירח is the book and the chapter is הנמל האפל. Two titles in one section with nothing
    /// marking which was which.
    ///
    /// <para>IT IS A RENDERING FIX ON PURPOSE: it costs no prompt clause, it is paid ONCE per request
    /// rather than twice like every sentence in the system message, and unlike a prompt rule it can be
    /// checked by reading the composed string. The parenthesis is the load-bearing half - "Book title:"
    /// alone still sits above a chapter's title with nothing contrasting them.</para>
    ///
    /// <para>IT IS NOT THE WHOLE FIX, AND SAYING SO WAS WRONG (final-r01). This docstring used to open
    /// "THIS IS THE WHOLE FIX FOR THAT FINDING". <c>g4</c> then measured the class and it REPRODUCED: an
    /// answer named the chapter by the book's title in 5 of 38 book-scoped runs, and 4 of 4 on the
    /// review's own question. What this line provably buys is that the COMPOSED PROMPT now says whose
    /// each title is; whether the model stops reaching for the wrong one is a separate, measured,
    /// still-OPEN question. Note the shape of the residual, because it points at where the next attempt
    /// goes: asking about the same chapter BY TITLE named it correctly 2 of 2, so the confusion is
    /// specific to the DEICTIC path, where the question supplies no title and the model reaches for the
    /// nearest one it was shown. Do not read a rendering fix's readability as the class being closed.</para>
    ///
    /// <para>IT IS DELIBERATELY NOT SHAPED LIKE <c>=== ARTIFACT ref=... ===</c>. This line carries no ref
    /// and is not citable, and be-f01 had just finished removing a header that advertised a ref the parser
    /// rejects (<c>ref=status</c>, which g2 measured the model writing out verbatim). A second uncitable
    /// thing wearing the artifact costume would re-create that defect on the one line that is never
    /// dropped.</para>
    ///
    /// <para>English, like MOST of the BOOK section. That used to read "like every other line of the BOOK
    /// section: none of that section is user-facing", and be-c04 found the claim false in two places at
    /// once: <c>BookArtifactBlocks.AuthorFacingChapterName</c> has been written in the answer's language
    /// since final-r05, and the section's note is written in it too, because the grounding clause puts a
    /// note's content in the author's answer. THIS line is genuinely machine-facing - nothing instructs the
    /// model to say it, and its job is done the moment the model can tell a book title from a chapter
    /// title - so it stays English; what changed is that "the whole section is machine-facing" is no longer
    /// available as a reason for anything.</para>
    /// </summary>
    internal const string BookTitleLabel = "Book title (not a chapter title): ";
    internal const string HistoryMarker = "[CONVERSATION]";
    internal const string QuestionMarker = "[QUESTION]";

    /// <summary>
    /// The system message for <c>AiTaskType.ProductChat</c>. <c>PromptFactory</c> returns THIS rather
    /// than keeping a second copy, so the grounding wording has one owner.
    ///
    /// <para>With <paramref name="bookAware"/> false (the default, and every request that carries no
    /// bookId) this is BYTE-IDENTICAL to what phase A shipped. That is not an accident of construction:
    /// A's gate verdict is a measurement of these exact sentences, so B is only allowed to change the
    /// prompt in the situation A never measured.</para>
    /// </summary>
    public static string SystemMessage(string language, bool bookAware = false)
    {
        var hebrew = ChatLanguage.IsHebrew(language);

        var head = hebrew ? GroundingHeHead : GroundingEnHead;
        var middle = bookAware
            ? (hebrew ? BookGroundingHe : BookGroundingEn)
            : (hebrew ? BookRefusalHe : BookRefusalEn);
        var citation = bookAware
            ? (hebrew ? CitationLineBookAwareHe : CitationLineBookAwareEn)
            : (hebrew ? CitationLineHe : CitationLineEn);
        var languageRule = hebrew ? LanguageHe : LanguageEn;

        return head + middle + citation + languageRule;
    }

    /// <summary>
    /// Composes the complete user-message instruction: the grounding rule, then the selected guides
    /// WHOLE (each under a header naming its <c>id</c> and <c>lang</c>, so the model can cite an id
    /// rather than a title), then the capped conversation history.
    ///
    /// <para>The QUESTION is deliberately NOT part of this string: it travels as
    /// <c>AiRequest.InputText</c> and providers append it after the instruction, which puts it last in
    /// the prompt. The marker line for it is emitted here so the boundary is explicit.</para>
    /// </summary>
    /// <param name="history">
    /// Already capped by <c>ProductChatService</c>. This method does no capping of its own on purpose:
    /// a budget rule enforced in two places is a budget rule that will disagree with itself.
    /// </param>
    /// <param name="book">
    /// The retrieved book artifacts, already ordered and already trimmed by
    /// <see cref="ProductChatBudget"/>. EMPTY means no bookId was supplied, and the composed instruction
    /// is then byte-identical to phase A's: no <see cref="BookMarker"/>, no book-aware system message,
    /// no book context line. That identity is what keeps A's gate verdict valid through B.
    /// </param>
    /// <param name="bookTitle">
    /// Stated so the assistant can name WHICH book it is looking at. Facts about the title are not
    /// inferable from it; it is a label, and the artifacts are the grounding. Rendered under
    /// <see cref="BookTitleLabel"/>, which says whose title it is - see that constant for the answer
    /// that named a chapter with it.
    /// </param>
    /// <param name="bookNote">
    /// What the RETRIEVAL knew and the prompt used to throw away: a short note about how the question
    /// resolved, emitted only when there is genuinely something to say. Before w9 the one shape that fired
    /// this was a bare "chapter N" that grounded both the 0-based and the 1-based candidate; w9 replaced
    /// that with deterministic resolution, so today the note fires only for ambiguity the book really has
    /// (the same number or title naming more than one chapter) or a named chapter the book does not have
    /// (<see cref="BookArtifactBlocks.BookSectionNote"/>). IT ARRIVES ALREADY IN THE ANSWER'S LANGUAGE
    /// (be-c04) - the caller resolves it before this method ever sees it - and it is user-facing by
    /// contract: the grounding clause instructs the model to act on it and relay its facts into the
    /// answer, so "nothing in that section is user-facing" no longer holds for this one line. The RULE
    /// that governs what the model does with it is in both grounding strings below.
    ///
    /// <para>Null or blank emits nothing at all, so the ordinary chapter answer is unchanged and does not
    /// acquire a hedge it has no reason for.</para>
    /// </param>
    public static string ComposeInstruction(
        string language,
        IReadOnlyList<GuideDocument> guides,
        IReadOnlyList<ProductChatTurn> history,
        IReadOnlyList<BookArtifactBlock>? book = null,
        string? bookTitle = null,
        string? bookNote = null)
    {
        var isHebrew = ChatLanguage.IsHebrew(language);
        var bookBlocks = book ?? Array.Empty<BookArtifactBlock>();
        var sb = new StringBuilder();

        sb.Append(SystemMessage(language, bookAware: bookBlocks.Count > 0)).Append("\n\n");

        sb.Append(GuidesMarker).Append('\n');
        foreach (var guide in guides)
        {
            sb.Append("=== GUIDE id=").Append(guide.Id)
              .Append(" lang=").Append(guide.Lang)
              .Append(" ===\n")
              .Append(guide.Body)
              .Append("\n\n");
        }

        if (bookBlocks.Count > 0)
        {
            sb.Append(BookMarker).Append('\n');
            if (!string.IsNullOrWhiteSpace(bookTitle))
                sb.Append(BookTitleLabel).Append(bookTitle!.Trim()).Append('\n');

            if (!string.IsNullOrWhiteSpace(bookNote))
                sb.Append("Note: ").Append(bookNote!.Trim()).Append('\n');

            foreach (var block in bookBlocks)
            {
                sb.Append(block.Text).Append("\n\n");
            }
        }

        if (history.Count > 0)
        {
            sb.Append(HistoryMarker).Append('\n');
            foreach (var turn in history)
            {
                sb.Append(turn.IsUser
                        ? (isHebrew ? "משתמש: " : "user: ")
                        : (isHebrew ? "עוזר: " : "assistant: "))
                  .Append(turn.Content)
                  .Append('\n');
            }

            sb.Append('\n');
        }

        sb.Append(QuestionMarker);
        return sb.ToString();
    }
}

/// <summary>
/// One prior conversation turn as the server forwards it. Phase A keeps NO server-side conversation
/// state: the client holds the transcript and sends the part it wants carried, and the server caps
/// it (see <c>ProductChatService.MaxHistoryTurns</c>). Persistence belongs with phase C's history and
/// quota surface, which needs a user model that does not exist yet.
/// </summary>
public sealed record ProductChatTurn(bool IsUser, string Content);
