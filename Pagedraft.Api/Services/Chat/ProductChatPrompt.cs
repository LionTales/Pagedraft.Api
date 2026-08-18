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
    ///
    /// <para>THIS OVERLOAD IS <see cref="ChatRoute.Union"/>, and it is kept rather than migrated because
    /// it is what ~370 facts and three production call sites already ask for. g1 generalized the
    /// <paramref name="bookAware"/> BOOL to a route without deleting the bool: the routing layer ships
    /// behind <c>ProductChat:RoutingEnabled</c> defaulting to false, so Union is the only route any
    /// deployment can reach, and Union is defined to be exactly this.</para>
    /// </summary>
    public static string SystemMessage(string language, bool bookAware = false)
        => SystemMessage(language, ChatRoute.Union, bookAware);

    /// <summary>
    /// The system message for one <see cref="ChatRoute"/>. THE ROUTE DECIDES WHICH SENTENCE GROUPS RIDE;
    /// <paramref name="bookAware"/> still decides whether there is a BOOK section for a book rule to
    /// govern, which is a fact about what SURVIVED the budget trim and not about the question (see
    /// <c>ProductChatBudget.Composition.SystemMessage</c>).
    ///
    /// <para>WHAT g1 DID AND DID NOT DO HERE. It built the SEAM and changed no behavior. Every arm below
    /// composes one of the two messages that already shipped:</para>
    /// <list type="bullet">
    ///   <item><see cref="ChatRoute.Union"/> is the status quo predicate, byte for byte: book-aware when
    ///     book blocks survived, phase A's book refusal when they did not. THIS IS THE SAFETY PROPERTY of
    ///     the whole routing layer - a misroute can only ever return what today returns - and
    ///     <c>ProductChatRoutePartitionTests</c> pins it against hand-typed literals in both
    ///     languages.</item>
    ///   <item><see cref="ChatRoute.Book"/> composes today's book-aware message, and only when a book
    ///     section actually survived. A book grounding rule with no BOOK section below it is a rule about
    ///     nothing, so the route defers to <paramref name="bookAware"/> there rather than asserting over
    ///     it.</item>
    ///   <item><see cref="ChatRoute.Product"/> and <see cref="ChatRoute.General"/> compose today's
    ///     book-less message. They are PLACEHOLDERS with a deliberate shape: g2 replaces exactly these two
    ///     arms, and until it does, flipping the flag on by accident cannot introduce a sentence that has
    ///     never been measured. It can still change WHICH measured message a turn gets, which is why the
    ///     flag defaults to false and g3 is the gate.</item>
    /// </list>
    /// </summary>
    public static string SystemMessage(string language, ChatRoute route, bool bookAware)
    {
        var hebrew = ChatLanguage.IsHebrew(language);

        // Book grounding rides exactly when the route asks for it AND a BOOK section survived. Union asks
        // for it on the bookAware predicate alone, which is phase B's shipped behaviour.
        var groundBook = route switch
        {
            ChatRoute.Book => bookAware,
            ChatRoute.Union => bookAware,
            _ => false,
        };

        var head = hebrew ? ProductChatPromptBlocks.GroundingHeHead : ProductChatPromptBlocks.GroundingEnHead;
        var middle = groundBook
            ? (hebrew ? ProductChatPromptBlocks.BookGroundingHe : ProductChatPromptBlocks.BookGroundingEn)
            : (hebrew ? ProductChatPromptBlocks.BookRefusalHe : ProductChatPromptBlocks.BookRefusalEn);
        var citation = groundBook
            ? (hebrew ? ProductChatPromptBlocks.CitationLineBookAwareHe : ProductChatPromptBlocks.CitationLineBookAwareEn)
            : (hebrew ? ProductChatPromptBlocks.CitationLineHe : ProductChatPromptBlocks.CitationLineEn);
        var languageRule = hebrew ? ProductChatPromptBlocks.LanguageHe : ProductChatPromptBlocks.LanguageEn;

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

        sb.Append(SystemMessage(language, route, bookAware: bookBlocks.Count > 0)).Append("\n\n");

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
