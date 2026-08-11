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

    private const string GroundingEn =
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
        "they do not address, not even to report what they mention about it. " +
        "If the question is about the content or state of the user's own book (its characters, its " +
        "plot, what a specific chapter says, what a review found), say that answering questions about " +
        "a specific book is not available yet and is coming, and offer help with general product and " +
        "workflow questions instead. Do not attempt an answer from the guides in that case. " +
        "End your reply with a line of the form 'Guides: <id>, <id>' naming the guide ids you used, " +
        "and nothing else on that line. " +
        "Answer in English, because the question is in English, even where a guide you used is in " +
        "another language.";

    private const string GroundingHe =
        "אתה שואו, העוזר של PageDraft. אתה כותב בגוף ראשון, בחום ובקצרה, ופותח כל תשובה ממה שנשאלת. " +
        "ענה אך ורק מתוך תוכן המדריכים שמופיע למטה. " +
        "אל תשתמש בידע חיצוני על PageDraft, ולעולם אל תציין הגדרה, כפתור, מסך או התנהגות שאינם כתובים " +
        "במדריכים שניתנו. " +
        "אם המדריכים אינם עונים על השאלה, אמור זאת במפורש. אם יש נושא אחר שהם כן מכסים ורלוונטי " +
        "לשאלה, ציין אותו לפי המזהה שלו; אם אין, די בסירוב בלבד. אל תרכיב ניחוש מתוך חומר שרק חלקית " +
        "רלוונטי. " +
        "נסח זאת כפער במדריכים ולא כעובדה על המוצר: אל תאמר ש-PageDraft אינו תומך בכך. ואל תתאר מה " +
        "המדריכים אומרים על נושא שאינם עוסקים בו, גם לא כדי לציין מה מוזכר בהם לגביו. " +
        "אם השאלה נוגעת לתוכן או למצב של הספר הספציפי של המשתמש (הדמויות שבו, העלילה, מה כתוב בפרק " +
        "מסוים, מה סקירה מצאה), ענה בגוף ראשון במשמעות הזו: 'מענה על שאלות לגבי ספר מסוים עדיין אינו " +
        "זמין, והיכולת בדרך. אשמח לעזור בשאלות כלליות על המוצר ועל תהליך העריכה.' אל תנסה לענות מתוך " +
        "המדריכים במקרה כזה. " +
        "סיים את התשובה בשורה בצורה 'מדריכים: <מזהה>, <מזהה>' שמציינת את מזהי המדריכים שהשתמשת בהם, " +
        "ובלי דבר נוסף באותה שורה. " +
        "השב בעברית, כי השאלה נשאלה בעברית, גם אם מדריך שהשתמשת בו כתוב בשפה אחרת.";

    // ─── Section markers. ASCII and language-independent so a test can assert on them ────────────

    internal const string GuidesMarker = "[GUIDES]";
    internal const string HistoryMarker = "[CONVERSATION]";
    internal const string QuestionMarker = "[QUESTION]";

    /// <summary>
    /// The system message for <c>AiTaskType.ProductChat</c>. <c>PromptFactory</c> returns THIS rather
    /// than keeping a second copy, so the grounding wording has one owner.
    /// </summary>
    public static string SystemMessage(string language)
        => ChatLanguage.IsHebrew(language) ? GroundingHe : GroundingEn;

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
    public static string ComposeInstruction(
        string language,
        IReadOnlyList<GuideDocument> guides,
        IReadOnlyList<ProductChatTurn> history)
    {
        var isHebrew = ChatLanguage.IsHebrew(language);
        var sb = new StringBuilder();

        sb.Append(SystemMessage(language)).Append("\n\n");

        sb.Append(GuidesMarker).Append('\n');
        foreach (var guide in guides)
        {
            sb.Append("=== GUIDE id=").Append(guide.Id)
              .Append(" lang=").Append(guide.Lang)
              .Append(" ===\n")
              .Append(guide.Body)
              .Append("\n\n");
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
