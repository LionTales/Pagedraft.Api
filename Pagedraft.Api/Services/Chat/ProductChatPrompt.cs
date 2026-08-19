using System.Text;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE prompt composition for chatbot phase A (c1), carrying d1's grounding contract (item 2), its
/// book-specific refusal (item 5) and its language rule (item 3).
///
/// <para>THE TEXT LIVES IN <see cref="ProductChatPromptBlocks"/> SINCE g1, AND THIS CLASS IS THE
/// COMPOSER. The split was made when the routing seam took this file past the ~700-line soft ceiling,
/// and it is safe to have made because g1's identity tests pin all four composed messages against
/// hand-typed literals. Read that class for why each sentence is worded the way it is; read this one
/// for which sentences a given route assembles.</para>
///
/// <para>WHY THE RULE IS STATED TWICE. <see cref="SystemMessage(string, bool)"/> is what
/// <c>PromptFactory.GetPrompt(AiTaskType.ProductChat, ...)</c> returns and what a provider puts in
/// its system slot; <see cref="ComposeInstruction"/> restates the same three rules at the head of the
/// user message, immediately above the guide text they govern. That is not redundancy for its own
/// sake: the local provider concatenates system + instruction + input into one prompt and Ollama
/// truncates from the START when a prompt overruns the window, so a rule that lived only in the
/// system slot is the first thing lost in exactly the situation where losing it is worst.</para>
///
/// <para>VOICE, AND WHAT IT MAY NOT BUY (phase A.2, c2). The assistant is named Show, and the persona
/// sentence that opens both strings is REGISTER ONLY: first person, warm, brief, and opening from what
/// was actually asked. It states no rule and scopes none. Everything g4 measured is byte-identical
/// underneath it - the grounding contract, both refusal rules, and final-r02's scoped instruction 1 -
/// because g4's PASS (0 fabricated product behaviors in 48 adjacent runs, 48 of 48 pivots intact) is a
/// measurement of those exact sentences and of nothing else.</para>
///
/// <para>Two things were deliberately NOT written into the persona, and both are the temptation this
/// change had to walk past. (1) Nothing asks for varied or non-formulaic openings. Every clean refusal
/// g4 recorded opens with the same honest formula, and a demand to vary it applies pressure precisely on
/// the question shape where the g2 and g3 fabrications lived. Variation is left to come out of "open
/// from what was asked". (2) Nothing prefers paraphrase over quoting a guide. g4's pivots are clean
/// because they are verbatim corpus lines, so "less guide-recitation" is answered in the assistant's
/// VOICE and never in its sourcing. Friendliness comes out of voice, never out of facts.</para>
///
/// <para>No em-dash appears in any string here: these strings reach the user, and the model echoes
/// punctuation from its frame.</para>
/// </summary>
public static class ProductChatPrompt
{
    // ─── Section markers. ASCII and language-independent so a test can assert on them ────────────

    /// <summary>
    /// THE PRODUCT-CORPUS SECTION MARKER, AND SINCE g3b IT NAMES THE SUBJECT RATHER THAN THE SOURCE.
    ///
    /// <para>WHAT IT USED TO BE AND WHY THAT IS GONE. This read <c>[GUIDES]</c>, and each document below
    /// it carried a <c>=== GUIDE id=... ===</c> header. g3's fix deleted the source noun from the PRODUCT
    /// route's instructions (<see cref="ProductChatPromptBlocks.ProductGroundingScopedEn"/>) on the tested
    /// claim, written into that block's own docstring, that "it is the INSTRUCTIONS naming the source, not
    /// the data carrying it, that make the answer talk about the source". g3b MEASURED that claim and it is
    /// REFUTED: narration on product questions went 33/102 to 32/102, the product-uncovered cell stayed at
    /// 15 of 16, and the Hebrew answers narrate with <c>המדריכים</c>, a word that appears nowhere in the
    /// rewritten Hebrew block. Deleting the noun from the instructions only moved the narration onto
    /// whatever noun was left: the new grounding said "the material below" and the answers came back saying
    /// "the material provided" and "החומר שבידי" (13 hits across the run).</para>
    ///
    /// <para>WHY THE DATA IS WHERE THE NOUN COMES FROM, MEASURED RATHER THAN ARGUED. The GENERAL route
    /// composes the SAME closing language sentence as the product route, "even where a guide you used is in
    /// another language", and it narrated 0 of 16 in g3b. It differs from the product route in exactly two
    /// things that carry a source noun: it is handed no guide documents at all
    /// (<c>ProductChatService.GeneralRouteGuideCount</c> is 0, so no marker and no headers ride) and it
    /// composes no citation sentence. The product route, carrying both, narrated in 26 of its records. A
    /// noun that survives in the instructions of a route that never narrates is not the carrier; the
    /// envelope the other route adds is. That comparison is why <see cref="ProductChatPromptBlocks.LanguageEn"/>
    /// is deliberately NOT touched by this change.</para>
    ///
    /// <para>SO THE FRAME NAMES THE SUBJECT, WHICH IS NOT A THING TO NARRATE ABOUT. "PageDraft" is what the
    /// answer is already about, so repeating it back is the answer's topic and not a report on where the
    /// assistant looked; there is no document class here for a model to say it could not find something in.
    /// It stays ASCII and language-independent for the same reason it always was: the tests assert on it and
    /// it must read the same to a Hebrew turn and an English one.</para>
    ///
    /// <para>THE BOOK SECTION IS DELIBERATELY UNTOUCHED. <see cref="BookMarker"/> and the
    /// <c>=== ARTIFACT ref=... ===</c> headers under it kept the book cell at 0 narration in BOTH g3 runs,
    /// and this codebase's rule for that is not to touch what is working. The Book route's own instructions
    /// still say "the guides below" and now point at a section marked with the product's name, which is if
    /// anything a plainer referent than the one they had.</para>
    /// </summary>
    internal const string GuidesMarker = "[PAGEDRAFT]";
    internal const string BookMarker = "[BOOK]";

    /// <summary>
    /// The per-document header, carrying the <c>id</c> the citation line is built out of and NO document
    /// class. It was <c>"=== GUIDE id="</c>; see <see cref="GuidesMarker"/> for the measurement.
    ///
    /// <para>THE ID IS THE LOAD-BEARING HALF AND IT DID NOT MOVE. <see cref="ProductChatCitations"/> never
    /// reads this prompt: it parses the ids out of the MODEL'S OWN closing line and intersects them with the
    /// selection the service passed it, so the only way an id reaches the reader's citation chips is by the
    /// model copying it from here. Dropping the word GUIDE costs the model nothing it cites with; dropping
    /// <c>id=</c> would silently degrade every chip to the fall-back "here is everything this turn was
    /// given". <c>ProductChatCitationEnvelopeTests</c> pins the round trip end to end.</para>
    /// </summary>
    internal const string GuideHeaderPrefix = "=== id=";

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
    /// bookId) this was byte-identical to what phase A shipped until g3, which replaced the one sentence
    /// of the book refusal that had gone false (see <c>ProductChatPromptBlocks.BookRefusalEn</c>).
    /// Everything A's gate verdict was a measurement of is otherwise untouched.</para>
    ///
    /// <para>THIS OVERLOAD IS <see cref="ChatRoute.Union"/>, and it is kept rather than migrated because
    /// it is what the suite's byte-literal pins and three production call sites already ask for (the
    /// ProductChat slice was MEASURED at 700 pre-existing facts in g1; the plan's "~370" is stale and is
    /// corrected here so the next reader does not re-derive it). g1 generalized the
    /// <paramref name="bookAware"/> BOOL to a route without deleting the bool.</para>
    /// </summary>
    public static string SystemMessage(string language, bool bookAware = false)
        => SystemMessage(language, ChatRoute.Union, bookAware);

    /// <summary>
    /// The system message for one <see cref="ChatRoute"/>. THE ROUTE DECIDES WHICH SENTENCE GROUPS RIDE;
    /// <paramref name="bookAware"/> still decides whether there is a BOOK section for a book rule to
    /// govern, which is a fact about what SURVIVED the budget trim and not about the question (see
    /// <c>ProductChatBudget.Composition.SystemMessage</c>).
    ///
    /// <para>WHAT EACH ARM COMPOSES (g1 built the seam and changed nothing; g2 filled it in):</para>
    /// <list type="bullet">
    ///   <item><see cref="ChatRoute.Union"/> is the fallback every misroute lands on: book-aware when book
    ///     blocks survived, the book refusal when they did not. It was byte-identical to the pre-routing
    ///     message until g3, which replaced ONE sentence of the book-refusal arm - the false "not available
    ///     yet and is coming" - with the same "I can only see a book while it is open" the deterministic
    ///     path answers with; see <c>ProductChatPromptBlocks.BookRefusalEn</c> for the measurement behind
    ///     that. Everything else it composes is unchanged, and
    ///     <c>ProductChatRoutePartitionTests</c> pins all four cells against hand-typed literals in both
    ///     languages.</item>
    ///   <item><see cref="ChatRoute.Product"/> keeps the guides-only contract with the SOURCE-NARRATION
    ///     removed (<c>ProductGroundingScoped</c>) and carries no book sentence at all: the deterministic
    ///     path in <c>ProductChatService</c> owns the one shape the old refusal governed.</item>
    ///   <item><see cref="ChatRoute.Book"/> composes a ONE-SENTENCE product rule plus the book rule with
    ///     its briefs fence hedged (<c>BookGroundingRouted</c>), and only when a book section actually
    ///     survived. A book grounding rule with no BOOK section below it is a rule about nothing, so with
    ///     nothing surviving the route falls back to the PRODUCT arm rather than to Union's - falling back
    ///     to Union there would reintroduce the false refusal on a turn that genuinely carried a
    ///     bookId, which is g1's own F-1 collision in a new costume.</item>
    ///   <item><see cref="ChatRoute.General"/> keeps only the persona, adds the general block, and ends
    ///     with the language rule. NO CITATION SENTENCE: an answer from Show's own knowledge has no guide
    ///     to name.</item>
    /// </list>
    ///
    /// <para>THE WORDING IS STATED TWICE PER LANGUAGE AND THAT IS HANDLED BY CONSTRUCTION, not by care:
    /// <see cref="ComposeInstruction"/> restates the head of the user message by CALLING this method, so
    /// one edit here moves both surfaces and they cannot drift.</para>
    /// </summary>
    /// <param name="guidesCarried">
    /// Whether any guide document actually rides in this turn's prompt. THE CITATION SENTENCE RIDES EXACTLY
    /// WHEN SOMETHING CITABLE DOES, which is the same invariant <see cref="ComposeInstruction"/> already
    /// applies to <see cref="GuidesMarker"/> one layer down ("the marker rides only when there is something
    /// under it") and which <see cref="ChatRoute.General"/> has satisfied since g2 by composing no citation
    /// sentence at all.
    ///
    /// <para>IT IS REACHED BY EXACTLY ONE NEW CONFIGURATION (g3d/gate 4), AND THAT CONFIGURATION IS OFF AS
    /// SHIPPED, so nothing in production composes this arm today: an English
    /// <see cref="ChatRoute.Product"/> turn whose guide top score fell below
    /// <see cref="ProductChatRouter.EnglishProductDocumentsFloor"/> - a floor gate run 5 rolled back to 0 -
    /// which <c>ProductChatService</c> hands zero guides. Asking such a turn to "end your reply with a line of the
    /// form 'Guides: id, id' naming the ids you used" would be asking it to name one of no ids, which is an
    /// invitation to invent one; and the citation sentence is, with the documents themselves, one of the two
    /// things the narrating product route composes that the never-narrating general route does not
    /// (<see cref="GuidesMarker"/> writes that comparison up). Withholding the documents and keeping the
    /// sentence would ship neither configuration.</para>
    ///
    /// <para>DEFAULTED TRUE so every caller written before this composes byte-identically. The other way to
    /// reach false is the Union book-less arm with an empty selection, which the service fail-safes before
    /// composing, so no shipped composition moves.</para>
    /// </param>
    public static string SystemMessage(
        string language, ChatRoute route, bool bookAware, bool guidesCarried = true)
    {
        var hebrew = ChatLanguage.IsHebrew(language);
        var languageRule = hebrew ? ProductChatPromptBlocks.LanguageHe : ProductChatPromptBlocks.LanguageEn;

        // ─── UNION: THE STATUS QUO, MINUS ONE SENTENCE THAT HAD GONE FALSE (g3) ──────────────────
        if (route == ChatRoute.Union)
        {
            var unionHead = hebrew
                ? ProductChatPromptBlocks.GroundingHeHead
                : ProductChatPromptBlocks.GroundingEnHead;
            var unionMiddle = bookAware
                ? (hebrew ? ProductChatPromptBlocks.BookGroundingHe : ProductChatPromptBlocks.BookGroundingEn)
                : (hebrew ? ProductChatPromptBlocks.BookRefusalHe : ProductChatPromptBlocks.BookRefusalEn);
            var unionCitation = bookAware
                ? (hebrew ? ProductChatPromptBlocks.CitationLineBookAwareHe : ProductChatPromptBlocks.CitationLineBookAwareEn)
                : (hebrew ? ProductChatPromptBlocks.CitationLineHe : ProductChatPromptBlocks.CitationLineEn);

            // UNION IS DELIBERATELY NOT GIVEN THE guidesCarried INVARIANT. The only way to reach this arm
            // with no guides is an empty selection, which ProductChatService fail-safes on before it ever
            // composes, so the branch would be dead code on the one route whose whole contract is that a
            // misroute returns the status quo. Adding an untestable arm to it buys nothing and puts a second
            // version of the fallback in the file.
            return unionHead + unionMiddle + unionCitation + languageRule;
        }

        var persona = hebrew ? ProductChatPromptBlocks.PersonaHe : ProductChatPromptBlocks.PersonaEn;

        // ─── GENERAL: Show's own knowledge, and nothing about where an answer came from ──────────
        if (route == ChatRoute.General)
        {
            var general = hebrew
                ? ProductChatPromptBlocks.GeneralGroundingHe
                : ProductChatPromptBlocks.GeneralGroundingEn;

            return persona + general + languageRule;
        }

        // ─── BOOK, when a BOOK section actually survived the trim ────────────────────────────────
        if (route == ChatRoute.Book && bookAware)
        {
            var productRule = hebrew
                ? ProductChatPromptBlocks.BookProductRuleHe
                : ProductChatPromptBlocks.BookProductRuleEn;
            var bookRule = hebrew
                ? ProductChatPromptBlocks.BookGroundingRoutedHe
                : ProductChatPromptBlocks.BookGroundingRoutedEn;
            var bookCitation = hebrew
                ? ProductChatPromptBlocks.CitationLineBookAwareHe
                : ProductChatPromptBlocks.CitationLineBookAwareEn;

            return persona + productRule + bookRule + bookCitation + languageRule;
        }

        // ─── PRODUCT, and BOOK with nothing left to ground a book rule on ────────────────────────
        //
        // THE ANTI-FABRICATION HALF IS WHAT THE WITHHELD TURN KEEPS, and that is the whole reason this arm
        // is reused rather than the General one (g3d/gate 4). Gate 4's lever is "the General route's
        // treatment" of the DOCUMENTS, not of the contract: GeneralGrounding licenses an answer out of
        // Show's own knowledge, which on a product question is the definition of an invented product
        // behaviour, and holding the C cell at 16/16 appropriate refusals with 0 fabrications is a floor
        // this round may not trade. ProductGroundingScoped carries all three rules g4's PASS is a
        // measurement of - never state a setting, button, screen or behavior that is not written there,
        // never assemble one out of partly relevant parts, never turn a gap into a claim that PageDraft
        // lacks a thing - plus the finished refusal exemplar to say instead. With nothing written below,
        // "what you say about PageDraft comes only from what is written below" is not a dangling referent
        // but the tightest form of the rule: there is nothing, so there is nothing to say, so the block's
        // own last sentence is the only move left. ProductChatRoutePartitionTests pins the four sentences.
        var productGrounding = hebrew
            ? ProductChatPromptBlocks.ProductGroundingScopedHe
            : ProductChatPromptBlocks.ProductGroundingScopedEn;
        var citation = guidesCarried
            ? (hebrew ? ProductChatPromptBlocks.CitationLineHe : ProductChatPromptBlocks.CitationLineEn)
            : string.Empty;

        return persona + productGrounding + citation + languageRule;
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
    /// that governs what the model does with it is in both grounding strings.
    ///
    /// <para>Null or blank emits nothing at all, so the ordinary chapter answer is unchanged and does not
    /// acquire a hedge it has no reason for.</para>
    /// </param>
    /// <param name="route">
    /// The resolved <see cref="ChatRoute"/> for this turn, defaulted to <see cref="ChatRoute.Union"/> so
    /// every existing caller composes byte-identically to before g1. It is threaded rather than re-derived
    /// here for the reason <c>ProductChatBudget.Composition.SystemMessage</c> records: the head of this
    /// instruction and the request's system slot must be the SAME string, and two derivations of one
    /// answer is two chances to disagree.
    /// </param>
    public static string ComposeInstruction(
        string language,
        IReadOnlyList<GuideDocument> guides,
        IReadOnlyList<ProductChatTurn> history,
        IReadOnlyList<BookArtifactBlock>? book = null,
        string? bookTitle = null,
        string? bookNote = null,
        ChatRoute route = ChatRoute.Union)
    {
        var isHebrew = ChatLanguage.IsHebrew(language);
        var bookBlocks = book ?? Array.Empty<BookArtifactBlock>();
        var sb = new StringBuilder();

        // guidesCarried IS DERIVED FROM THE SAME LIST THIS METHOD IS ABOUT TO RENDER, never passed in, so
        // the rule at the head of the user message and the section under it cannot disagree about whether
        // there are documents. ProductChatBudget derives it from the same trimmed list for the system slot,
        // on the same iteration - the single-derivation discipline Composition.SystemMessage records.
        sb.Append(SystemMessage(
              language, route, bookAware: bookBlocks.Count > 0, guidesCarried: guides.Count > 0))
          .Append("\n\n");

        // THE MARKER RIDES ONLY WHEN THERE IS SOMETHING UNDER IT (g3). Until the General route stopped
        // carrying guides this was unreachable - the service fail-safes before composing when the selector
        // returns nothing - so no existing composition moves. An empty section is worse than no section:
        // it is a labelled place where the answer's grounding is supposed to be, and this round's whole
        // subject is a model that talks about where it looked.
        if (guides.Count > 0)
        {
            sb.Append(GuidesMarker).Append('\n');
            foreach (var guide in guides)
            {
                sb.Append(GuideHeaderPrefix).Append(guide.Id)
                  .Append(" lang=").Append(guide.Lang)
                  .Append(" ===\n")
                  .Append(guide.Body)
                  .Append("\n\n");
            }
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
