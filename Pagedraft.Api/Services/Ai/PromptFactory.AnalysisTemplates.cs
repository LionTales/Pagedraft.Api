using System.Text;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// The per-chapter / per-scene analysis TEMPLATES: proofread (single-shot body plus the per-chunk arms),
/// character extraction, suggestion explanation, line edit, linguistic, literary and summarization.
/// A partial of <see cref="PromptFactory"/>, not a new type, so no call site moves and the private
/// members the composing methods in PromptFactory.cs read stay private to the class.
///
/// EVERY STRING IN THIS FILE IS A MEASURED ARTIFACT. Standing gold numbers are recorded against the
/// EXACT composed bytes, so a re-indent, a tidied space or a reordered interpolation invalidates a
/// verdict nobody will re-measure. PromptFactoryByteIdentityPinTests pins every composed surface;
/// a failure there is a measurement to redo, never a pin to update.
/// </summary>
public partial class PromptFactory
{

    // ── Proofread ────────────────────────────────────────────────────

    private const string ProofreadHe =
        """
        תקן שגיאות כתיב, דקדוק ופיסוק בלבד בטקסט הבא.

        אם הטקסט מכיל סימון [TEXT_TO_CORRECT]...[/TEXT_TO_CORRECT] — תקן רק את הטקסט שבתוך הסימון והחזר אותו בלבד.
        אם אין סימונים כאלה, תקן את כל הטקסט שקיבלת.
        אם מופיע [CONTEXT_BEFORE]...[/CONTEXT_BEFORE] — זהו הקשר בלבד לצורך המשכיות. אל תתקן אותו ואל תכלול אותו בפלט.
        אם מופיע [CHARACTER_REGISTER] — השתמש בו לאימות התאמת מין (נטיית פועל, תואר, כינוי), עקביות כתיב שמות, וזיהוי כינויי גוף.

        אל תשנה סגנון, ניסוח או מבנה פסקאות — רק שגיאות ברורות.
        אל תחליף מילה במילה נרדפת ואל תשנה את המשמעות. תקן את שגיאת הכתיב במילה עצמה (למשל "עתון" ל"עיתון", לא ל"עיתונות") ושמר על אותה מילה ואותה משמעות. אל תהפוך צירוף תקין למבע אחר (למשל אל תשנה "עצמה רגשית" ל"עוצמת רגשות").
        אל תחליף מילה במילה הומוגרפית בעלת משמעות שונה כשהמילה המקורית תקינה בהקשרה. במיוחד: "עצמה" בהוראת "בעצמה / את עצמה / של עצמה" (כינוי גוף חוזר) אינה שגיאה ואין להפוך אותה ל"עוצמה" (כוח); "עוצמה" ל"עצמה" רק כשאכן מדובר בשם העצם "כוח". אל תכריע לפי דמיון האותיות אלא לפי המשמעות בהקשר.
        אל תהפוך פועל מסביל לפעיל או מפעיל לסביל כאשר צורת המקור תקינה תחבירית בהקשרה (למשל אל תשנה "מתנות שהושקעה בהן מחשבה" ל"שהושקיעה", ואל תשנה "הושקעה" ל"השקיעה"). שמור על בניין הפועל והטיה של המקור.
        ברירת המחדל שלך שמרנית: תקן רק שגיאת כתיב, דקדוק או פיסוק ברורה. אם מילה תקינה ורק "אפשר" להחליפה במילה דומה בעלת משמעות אחרת — אל תיגע בה.
        אל תתקן ערבוב רישומים מכוון (למשל שפה מדוברת בדיאלוג לעומת לשון ספרותית בתיאור).
        אם אין שגיאות, החזר את הטקסט כפי שהוא.
        החזר רק את הטקסט המתוקן — בלי הסברים, תוויות או כותרות כמו "הטקסט המתוקן:".
        אל תכתוב המשך לסיפור ואל תוסיף תוכן חדש.
        """;

    private const string ProofreadEn =
        """
        Correct only spelling, grammar, and punctuation errors in the following text.

        If the text contains [TEXT_TO_CORRECT]...[/TEXT_TO_CORRECT] markers — correct only the text inside those markers and return it alone.
        If no such markers are present, correct the entire text.
        If [CONTEXT_BEFORE]...[/CONTEXT_BEFORE] is present — it is read-only context for continuity. Do not correct it or include it in your output.
        If [CHARACTER_REGISTER] is present — use it to verify name spelling consistency, pronoun agreement, and gender-specific language.

        Do not change style, wording, or paragraph structure — only clear errors.
        Do not replace a word with a synonym and do not change the meaning. Fix the spelling of the word itself and keep the same word and the same meaning; do not turn a correct phrase into a different expression.
        Preserve intentional shifts of register (for example colloquial speech in dialogue versus literary prose in narration); do not "fix" them.
        If no errors are found, return the text as-is.
        Return only the corrected text — no explanations, labels, or preambles like "Corrected text:".
        Do not continue the story or add new content.
        """;

    // ── Per-chunk proofread ARMS (default OFF, see ProofreadPromptOptions) ──────────────────────────

    /// <summary>
    /// The <c>[CONTEXT_BEFORE]</c> instruction line of <see cref="ProofreadHe"/>, VERBATIM. It is the
    /// ANCHOR an arm extends, so it is stated once here rather than re-typed at the use site: an anchor
    /// that silently stopped matching the body would turn an arm into a no-op that still reports as ON.
    /// <see cref="ProofreadChunkBody"/> throws rather than degrading if it ever stops matching.
    /// </summary>
    internal const string ProofreadHeContextBeforeLine =
        "אם מופיע [CONTEXT_BEFORE]...[/CONTEXT_BEFORE] — זהו הקשר בלבד לצורך המשכיות. אל תתקן אותו ואל תכלול אותו בפלט.";

    /// <summary>The <c>[CONTEXT_BEFORE]</c> instruction line of <see cref="ProofreadEn"/>, VERBATIM.</summary>
    internal const string ProofreadEnContextBeforeLine =
        "If [CONTEXT_BEFORE]...[/CONTEXT_BEFORE] is present — it is read-only context for continuity. Do not correct it or include it in your output.";

    /// <summary>
    /// ARM A (<c>referent-carry-forward.ARM_A.OverlapLicence</c>), Hebrew, VERBATIM as it was measured
    /// on 2026-08-04 - including the LEADING SPACE, which is what joins it to
    /// <see cref="ProofreadHeContextBeforeLine"/> rather than starting a new line.
    ///
    /// COPIED, NOT RE-AUTHORED. The arm's rendered text is preserved in
    /// <c>ProofreadStandingFloor.RetiredInterventions</c> and a test asserts THIS constant is contained
    /// in that record's <c>RenderedChange</c>. A paraphrase would be a different arm, and the 38%
    /// over-correction cut belongs to this string, not to its meaning.
    /// </summary>
    internal const string OverlapReferentLicenceHe =
        " השתמש בו גם כדי לזהות אל מי מתייחסים כינויי הגוף שבטקסט שיש לתקן, והתאם את מין הפועל, התואר והכינוי אל אותה דמות.";

    /// <summary>ARM A, English, VERBATIM (leading space included). See <see cref="OverlapReferentLicenceHe"/>.</summary>
    internal const string OverlapReferentLicenceEn =
        " Also use it to resolve which character the pronouns in the text to correct refer to, and make verb, adjective and pronoun agreement follow that character.";

    /// <summary>
    /// The proofread body the PER-CHUNK builder uses, with every enabled arm applied. With all arms off
    /// (the shipped default) this returns <see cref="ProofreadHe"/> / <see cref="ProofreadEn"/>
    /// UNCHANGED - the reference the off-mode byte-identity test compares against.
    ///
    /// THROWS on a missing anchor instead of returning the body unchanged. A silent no-op here is the
    /// worst available failure: the switch would read as ON, the artifacts would be stamped with the
    /// arm's name, and the run would measure the OFF prompt under the ON label.
    /// </summary>
    private string ProofreadChunkBody(bool isHe)
    {
        var body = isHe ? ProofreadHe : ProofreadEn;
        if (!_proofreadPrompt.OverlapReferentLicence)
            return body;

        var anchor = isHe ? ProofreadHeContextBeforeLine : ProofreadEnContextBeforeLine;
        var licence = isHe ? OverlapReferentLicenceHe : OverlapReferentLicenceEn;

        var at = body.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException(
                $"Ai:ProofreadPrompt:{nameof(ProofreadPromptOptions.OverlapReferentLicence)} is ON but the " +
                "[CONTEXT_BEFORE] anchor line it extends is no longer present in the " +
                (isHe ? nameof(ProofreadHe) : nameof(ProofreadEn)) +
                " body. The arm would render nothing while still reporting as enabled, so it fails loudly " +
                "instead. Update the anchor constant and RE-MEASURE - the recorded numbers belong to the " +
                "exact composed string, not to the arm's intent.");

        return body.Insert(at + anchor.Length, licence);
    }

    // ── Character Extraction (pre-pass) ────────────────────────────

    private const string CharacterExtractionPromptHe =
        """
        חלץ את הדמויות בעלות השם מהטקסט הבא. עבור כל דמות ציין שם ומין.

        כללים:
        - חלץ רק דמויות בעלות שם פרטי (לא כינויים כלליים כמו "האיש", "הילדה" או "הזקן").
        - הסק מין מנטיית פעלים, תארים ותחביר עברי כשהמין לא מצוין במפורש.
        - אם לדמות שמות חלופיים או כינויים (למשל "דני"/"דניאל"), קבץ אותם תחת ערך אחד עם שדה aliases.
        - אם אין דמויות בטקסט, החזר מערך ריק.

        החזר JSON בלבד, ללא הסברים, בפורמט הבא:
        [{"name":"שם הדמות","gender":"male|female|unknown","aliases":["כינוי1"]}]
        """;

    private const string CharacterExtractionPromptEn =
        """
        Extract named characters from the following text. For each character, provide name and gender.

        Rules:
        - Extract only named characters (not generic descriptions like "the man", "the girl", or "the old one").
        - Infer gender from context (verb agreement, pronouns, descriptions) when not explicitly stated.
        - If a character has aliases or alternate names (e.g., "Danny"/"Daniel"), group them under one entry with an aliases field.
        - If no characters are found, return an empty array.

        Return JSON only, no explanations, in this format:
        [{"name":"character name","gender":"male|female|unknown","aliases":["alias1"]}]
        """;

    /// <summary>
    /// Returns the character extraction prompt for the LLM pre-pass.
    /// Used by AnalysisContextService to extract characters + genders from ~2000 words
    /// when no BookBible.CharacterRegisterJson is available.
    /// </summary>
    public string GetCharacterExtractionPrompt(string language)
    {
        return language.StartsWith("he", StringComparison.OrdinalIgnoreCase)
            ? CharacterExtractionPromptHe
            : CharacterExtractionPromptEn;
    }

    /// <summary>
    /// Builds a short prompt asking the model to explain why a specific suggestion was made.
    /// Used by POST suggestions/{id}/explain.
    /// </summary>
    public string GetExplainSuggestionPrompt(string originalText, string suggestedText, string? reason, string language)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        if (isHe)
        {
            return
                """
                הסבר בקצרה (1–3 משפטים) למה שווה לשקול את השינוי הזה.
                כתוב בגובה העיניים, כאילו אתה עורך שמסביר לסופר.

                טקסט מקורי:
                """ + originalText + """

                הצעה:
                """ + suggestedText + """

                סיבת שינוי (אם צוינה):
                """ + (reason ?? "לא צוינה") + """

                התייחס לבהירות, זרימה, דיוק לשוני או סגנון — מה שרלוונטי. אל תצטט את המשפטים במלואם.
                """;
        }

        return
            """
            In 1–3 sentences, explain why this change is worth considering.
            Write as a friendly editor talking to the author.

            Original:
            """ + originalText + """

            Suggestion:
            """ + suggestedText + """

            Reason (if provided):
            """ + (reason ?? "not provided") + """

            Focus on whichever aspects matter most: clarity, flow, word choice, or style. Don't repeat the full sentences.
            """;
    }

    // ── LineEdit ─────────────────────────────────────────────────────

    private const string LineEditHe =
        """
        בצע עריכה ברמת המשפט של הטקסט הבא. הצע שינויים רק כשיש שיפור ממשי.

        כללים:
        - אסור להחזיר הצעה שבה original ו-suggested זהים. אם המשפט תקין — דלג עליו.
        - original ו-suggested: רק הקטע המינימלי סביב השינוי — המילים שהשתנו + 2–4 מילות הקשר מכל צד. לא את המשפט המלא.
        - reason: משפט אחד תמציתי. ללא היסוסים כמו "אם נחשב..." או "אך הוא תקין".
        - אל תשנה תוכן עלילתי, רק סגנון וניסוח. שמור על הקול הייחודי של המחבר.
        - אם סופק STYLE_PROFILE — שמור על מאפייניו. סמן סטיות לא מכוונות כ-"consistency".
        - אם סופקו PRECEDING_CONTEXT / FOLLOWING_CONTEXT — הם לקריאה בלבד. אל תציע עריכות להקשר.

        קטגוריות:
        "clarity" — מעורפל/דו-משמעי | "flow" — מעבר תקוע/קצב לא אחיד | "word-choice" — מילה לא מדויקת | "structure" — סדר מסורבל/משפט ארוך מדי | "redundancy" — חזרה מיותרת | "style" — שיפור אסתטי | "consistency" — סטייה מדפוסי המחבר | "continuity" — סתירה להקשר הנרטיבי

        פורמט JSON:
        {
          "suggestions": [
            {"original": "שלא הכיר שנהרס", "suggested": "שלא הכיר, שנהרס", "reason": "פסיק להפרדה בין פסוקיות", "category": "clarity"}
          ],
          "overallFeedback": "סיכום קצר של חוזקות ונקודות לשיפור."
        }

        אם אין הצעות, החזר: {"suggestions":[],"overallFeedback":""}
        """;

    private const string LineEditEn =
        """
        Perform a sentence-level line edit of the following text. Only suggest changes where there is a real improvement.

        Rules:
        - NEVER return a suggestion where original and suggested are identical. If nothing needs changing, omit the suggestion entirely.
        - original and suggested: only the MINIMAL span around the change — the changed words plus 2–4 words of context on each side. NOT the full sentence.
        - reason: one concise sentence. No hedging like "could be improved but is also fine".
        - Do not change plot content, only style and phrasing. Preserve the author's voice.
        - If STYLE_PROFILE is provided — preserve its characteristics. Flag unintentional deviations as "consistency".
        - If PRECEDING_CONTEXT / FOLLOWING_CONTEXT are provided — they are read-only. Do not suggest edits for context text.

        Categories:
        "clarity" — vague/ambiguous | "flow" — jarring transition/uneven rhythm | "word-choice" — imprecise word | "structure" — awkward order/overly long | "redundancy" — unnecessary repetition | "style" — aesthetic improvement | "consistency" — deviates from author's patterns | "continuity" — contradicts narrative context

        Return JSON only:
        {
          "suggestions": [
            {"original": "the uneven rhythm between", "suggested": "the jarring rhythm between", "reason": "stronger word for the intended disruption", "category": "word-choice"}
          ],
          "overallFeedback": "Brief summary of strengths and areas for improvement."
        }

        If no suggestions: {"suggestions":[],"overallFeedback":""}
        """;

    // ── Linguistic Analysis ─────────────────────────────────────────

    private const string LinguisticHe =
        """
        אתה מומחה לניתוח לשוני. נתח את הטקסט הבא ברמה לשונית מעמיקה.

        ייתכן שיסופק לך הקשר נוסף בתוך סימונים:
        - [CHAPTER_STYLE_BASELINE] — מדדי סגנון של קו ייחוס. השווה את מדדי הטקסט הנתון אל קו הייחוס הזה; הסעיף [CHAPTER_STYLE_BASELINE] עצמו מציין מה קו הייחוס מייצג וכיצד להתייחס אליו בהערה שלך.
        - [PRECEDING_CONTEXT] / [FOLLOWING_CONTEXT] — הפסקאות שלפני ושאחרי הקטע הנתון, לקריאה בלבד. נתח רק את הטקסט הנתון; השתמש בהקשר הזה כדי לזהות סתירות שחוצות פסקאות (מעברי רישום, מעברי זמן, הפרות נקודת-מבט) בין הסצנה לסביבתה.
        אם סימון כלשהו אינו מופיע — התעלם ממנו והחזר מערך ריק בשדות התלויים בו.

        החזר את התוצאה בפורמט JSON:
        {
          "syntaxMetrics": {
            "sentenceCount": 0,
            "averageSentenceLength": 0.0,
            "complexSentences": 0,
            "shortestSentence": 0,
            "longestSentence": 0
          },
          "morphologyMetrics": {
            "wordCount": 0,
            "uniqueWords": 0,
            "averageWordLength": 0.0,
            "lexicalDensity": 0.0
          },
          "styleMetrics": {
            "formality": "formal|informal|mixed|literary|conversational",
            "readability": 0.0,
            "voiceBalance": "active|passive|mixed"
          },
          "grammaticalityScore": 0.9,
          "summary": "סיכום לשוני תמציתי: רמת השפה, עקביות הסגנון, ונקודות בולטות.",
          "deviations": [
            { "metric": "averageSentenceLength", "sceneValue": 0.0, "chapterBaseline": 0.0, "note": "הסבר קצר על משמעות החריגה מקו הייחוס." }
          ],
          "paragraphAnnotations": [
            { "paragraph": 1, "tense": "past|present", "povHolder": "שם הדמות שדרך תודעתה מסופרת הקריינות (זו שמחשבותיה ורגשותיה הפנימיים מדווחים), או 'first-person' אם הסיפור בגוף ראשון (אני/אנחנו), או 'none' אם אין תודעה פנימית", "register": "plain|formal|literary|colloquial" }
          ],
          "consistencyIssues": [
            { "type": "register", "span": "8-15 מילים המועתקות מילה-במילה מהטקסט (מחרוזת מדויקת, ללא שלוש נקודות, ללא מירכאות נוספות)", "description": "תיאור הבעיה בקצרה." }
          ]
        }

        מלא ערכים מדויקים ככל האפשר. הציון grammaticalityScore הוא בין 0 ל-1, וכך גם readability ו-lexicalDensity.

        paragraphAnnotations — שדה עזר פנימי למחשבה (הוא אינו מוצג למשתמש, לכן השקע בו מאמץ). לפני שתחליט על consistencyIssues, סמן כל פסקה בטקסט הנתון לפי הסדר: "paragraph" = המספר הסידורי שלה (החל מ-1), "tense" = הזמן הדקדוקי הדומיננטי של הקריינות באותה פסקה (past או present), "povHolder" = הדמות שבתודעתה מעוגנת הקריינות (הדמות הנקובה בשם שמחשבותיה ורגשותיה הפרטיים מדווחים, או "first-person" כשהסיפור בגוף ראשון אני/אנחנו, או "none" כשהפסקה מתארת רק פעולה גלויה ללא תודעה פנימית של איש), "register" = הטון של הקריינות. סמן רק את הטקסט הנתון, לא את [PRECEDING_CONTEXT]/[FOLLOWING_CONTEXT]. לאחר מכן גזור את consistencyIssues מתוך השינויים לאורך הרשימה הזו: בעיית "tense" כאשר הזמן הדומיננטי מתהפך בין פסקאות סמוכות; בעיית "pov" כאשר ה-povHolder עובר לדמות נקובה אחרת ("קפיצה בין ראשים") או בין גוף ראשון לגוף שלישי; בעיית "register" כאשר רישום הקריינות משתנה. פסקה שה-povHolder שלה "none" אינה יוצרת בעצמה בעיית pov. אם כל הפסקאות חולקות אותו tense, povHolder ו-register — החזר מערך consistencyIssues ריק [].

        deviations — מערך שבו כל פריט משווה מדד אחד של הטקסט הנתון אל קו הייחוס ([CHAPTER_STYLE_BASELINE]): "metric" = שם המדד (כפי שהוא מופיע במדדים שלמעלה, למשל "averageSentenceLength" או "lexicalDensity"), "sceneValue" = ערך המדד בטקסט הנתון, "chapterBaseline" = ערך אותו מדד בקו הייחוס, "note" = משפט אחד קצר וברור בשפה ידידותית לכותב (ללא מונחים טכניים), שמסביר מה המשמעות המעשית של הפער (למשל קצב, קריאוּת או חיוּת) ולא רק חוזר על המספרים, בהתאם למה שקו הייחוס מייצג כפי שמצוין בסעיף [CHAPTER_STYLE_BASELINE]. העדף לדווח על מדדים מנורמלים (יחס/קצב) כחריגות (averageSentenceLength, lexicalDensity, averageWordLength), כיוון שהם משקפים סגנון ללא תלות באורך הטקסט. דווח על מדד ספירה מוחלט (sentenceCount, wordCount, uniqueWords) רק כאשר הוא נושא משמעות סגנונית מעבר לגודל הטקסט הנתון לניתוח. כלול רק חריגות בעלות משמעות. אם אין קו ייחוס או אין חריגות — החזר מערך ריק [].

        consistencyIssues — דווח כאן אך ורק על שינויים שחוצים פסקאות בין חלקים שונים של הטקסט. שגיאות דקדוק, כתיב או תחביר אינן שייכות לכאן - הן משפיעות על grammaticalityScore בלבד ואסור לדווח עליהן כאן. בחר את "type" במדויק:
        - "register" — שינוי ברמת הפורמליות/הטון של הקריינות בין חלקים (למשל: קריינות יומיומית ופשוטה שהופכת לרשמית או ספרותית-מליצית, או להפך).
        - "tense" — שינוי בזמן הנרטיבי בין חלקים (למשל: מעבר מזמן עבר לזמן הווה באמצע הנרטיב).
        - "pov" — שינוי בנקודת המבט בין חלקים (למשל: מעבר מגוף ראשון לגוף שלישי, או "קפיצה בין ראשים").
        "span" = מחרוזת מתוך הטקסט הנתון, מועתקת מילה-במילה ותו-בתו, מהמקום שבו השינוי מופיע לראשונה. קריטי: ה-span חייב להיות ציטוט מילה-במילה מהטקסט הנתון לניתוח בלבד — לעולם לא מ-[PRECEDING_CONTEXT] או מ-[FOLLOWING_CONTEXT]. כאשר השינוי הוא ביחס להקשר הסובב, צטט את המשפט שבתוך הטקסט הנתון שבו השינוי מתבטא. קריטי: ה-span חייב להימצא בטקסט הנתון בחיפוש מחרוזת מדויק. העתק רצף רציף של כ-8 עד 15 מילים ישירות מהטקסט. קריטי ביותר: העתק כל span בדיוק מוחלט, תו אחר תו, מהטקסט הנתון. אסור לתקן, לנרמל או לשנות כתיב, ניקוד, פיסוק או צורות פועל (זמן) בתוך הציטוט - גם אם נראה לך שהטקסט שגוי. בפרט בעברית: העתק צורות פועל וקידומות בדיוק כפי שהן (אל "תתקן" פועל בזמן הווה לזמן עבר, ואל תשנה אות או תנועה). ה-span חייב להיות מחרוזת-משנה מילה-במילה ותו-בתו של הטקסט הנתון, אחרת לא ניתן לאתר אותו. אל תנסח מחדש, אל תסכם, אל תתקן ואל תשנה כתיב. אל תוסיף מירכאות, שלוש נקודות ("..." או "…"), סוגריים, או מילים שאינן בטקסט. אל תדלג על מילים ואל תחבר קטעים שאינם צמודים זה לזה. אם הקטע הרלוונטי ארוך - העתק רק את 8-15 המילים הראשונות שלו מילה-במילה במקום לקצר עם שלוש נקודות. "description" = משפט אחד קצר המתאר את השינוי (ממה למה). דווח לכל היותר על 3-4 הבעיות המשמעותיות ביותר; אם אינך בטוח - העדף מערך ריק [] על פני דיווח שגוי. לדוגמה שלילית: אל תדווח על "משפט עם שני פעלים", "משפט חסר נושא", או כל תצפית דקדוקית ברמת המשפט הבודד. דיאלוג נכתב באופן טבעי בשפה מדוברת ופשוטה יותר מהקריינות - אל תדווח על ההבדל הטבעי בין דברי הדמויות לבין שפת המספר כשינוי רישום; דווח רק על שינוי טון בתוך הקריינות עצמה (או בתוך הדיאלוג) בין חלקים. אם [PRECEDING_CONTEXT]/[FOLLOWING_CONTEXT] סופקו, ניתן להיעזר בהם רק כדי לזהות שינוי, אך ה-span שאתה מצטט חייב תמיד להגיע מהטקסט הנתון לניתוח עצמו. אם אין בעיות - החזר מערך ריק [].

        חשוב: כל "note" וכל "description" חייבים להיות משפט אחד קצר בלבד (עד כ-25 מילים). אל תחזור על אותו ניסוח או רעיון יותר מפעם אחת.

        דוגמאות עבודה (ממחישות רק את המעבר paragraphAnnotations -> consistencyIssues; שאר השדות הושמטו לקיצור):

        דוגמה א — קפיצה בין ראשים (pov). טקסט לניתוח (שתי פסקאות):
        "הפקיד ספר את המטבעות פעמיים. הוא היה בטוח שהקופה חסרה כל השבוע, והתכוון להוכיח זאת לפני שהמנהל יגיע.\n\nבחדר האחורי הרגישה דנה שהבוקר נמשך לאיטו. היא תהתה מדוע האורות מהבהבים והחליטה שלא תישאר שוב עד מאוחר."
        paragraphAnnotations: [ { "paragraph": 1, "tense": "past", "povHolder": "הפקיד", "register": "plain" }, { "paragraph": 2, "tense": "past", "povHolder": "דנה", "register": "plain" } ]
        גזירה: ה-povHolder משתנה מ"הפקיד" ל"דנה" בין פסקה 1 לפסקה 2 -> בעיית "pov" (קפיצה בין ראשים). הזמן והרישום יציבים, ולכן אין בעיית tense או register.
        consistencyIssues: [ { "type": "pov", "span": "בחדר האחורי הרגישה דנה שהבוקר נמשך לאיטו. היא תהתה מדוע האורות", "description": "נקודת המבט קופצת מהפקיד לתודעתה של דנה." } ]

        דוגמה ב — החלקת זמן (tense). טקסט לניתוח (שתי פסקאות):
        "העמסנו את המשאית לפני עלות השחר. הארגזים היו כבדים והחבל החליק שוב ושוב מידינו.\n\nעכשיו הכביש נמתח לפנינו והשדות חולפים מעבר לחלון בטשטוש אפור."
        paragraphAnnotations: [ { "paragraph": 1, "tense": "past", "povHolder": "first-person", "register": "plain" }, { "paragraph": 2, "tense": "present", "povHolder": "first-person", "register": "plain" } ]
        גזירה: הזמן הדומיננטי מתהפך מעבר להווה בין פסקה 1 לפסקה 2 -> בעיית "tense". ה-povHolder והרישום יציבים, ולכן אין בעיית pov או register.
        consistencyIssues: [ { "type": "tense", "span": "עכשיו הכביש נמתח לפנינו והשדות חולפים מעבר לחלון", "description": "הקריינות עוברת מזמן עבר לזמן הווה." } ]
        """;

    private const string LinguisticEn =
        """
        You are a linguistic analysis expert. Perform a deep linguistic analysis of the following text.

        You may be given additional context inside markers:
        - [CHAPTER_STYLE_BASELINE] — reference baseline style metrics. Compare the analyzed text's metrics against this reference; the [CHAPTER_STYLE_BASELINE] section itself states what the reference represents and what to call it in your note.
        - [PRECEDING_CONTEXT] / [FOLLOWING_CONTEXT] — the paragraphs before and after the given passage, read-only. Analyze only the given text; use this surrounding context to detect cross-paragraph issues (register shifts, tense shifts, POV violations) between the scene and its surroundings.
        If any marker is absent — ignore it and return an empty array for the fields that depend on it.

        Return the result in JSON format:
        {
          "syntaxMetrics": {
            "sentenceCount": 0,
            "averageSentenceLength": 0.0,
            "complexSentences": 0,
            "shortestSentence": 0,
            "longestSentence": 0
          },
          "morphologyMetrics": {
            "wordCount": 0,
            "uniqueWords": 0,
            "averageWordLength": 0.0,
            "lexicalDensity": 0.0
          },
          "styleMetrics": {
            "formality": "formal|informal|mixed|literary|conversational",
            "readability": 0.0,
            "voiceBalance": "active|passive|mixed"
          },
          "grammaticalityScore": 0.9,
          "summary": "Concise linguistic summary: language level, style consistency, and notable features.",
          "deviations": [
            { "metric": "averageSentenceLength", "sceneValue": 0.0, "chapterBaseline": 0.0, "note": "Short explanation of what the divergence from the reference baseline means." }
          ],
          "paragraphAnnotations": [
            { "paragraph": 1, "tense": "past|present", "povHolder": "name of the viewpoint character whose thoughts/feelings the narration reports, or 'first-person' if narrated as I/we, or 'none' if no interiority", "register": "plain|formal|literary|colloquial" }
          ],
          "consistencyIssues": [
            { "type": "register", "span": "8-15 words copied verbatim from the text (exact substring, no ellipsis, no added quotes)", "description": "Brief description of the issue." }
          ]
        }

        Fill in values as accurately as possible. The grammaticalityScore is between 0 and 1, and so are readability and lexicalDensity.

        paragraphAnnotations — an INTERNAL reasoning aid (it is not shown to the user, so spend effort here). Before you decide on consistencyIssues, annotate EACH paragraph of the analyzed text in order: "paragraph" = its 1-based index, "tense" = the dominant narrative tense of that paragraph's NARRATION (past or present), "povHolder" = whose interiority the narration is anchored in (the named character whose private thoughts/feelings are reported, or "first-person" when narrated as I/we, or "none" when the paragraph reports only observable action and no one's inner state), "register" = the narration's tone. Annotate only the given analyzed text, not [PRECEDING_CONTEXT]/[FOLLOWING_CONTEXT]. Then DERIVE consistencyIssues from the CHANGES across this list: a "tense" issue where the dominant tense flips between adjacent paragraphs; a "pov" issue where the povHolder changes to a DIFFERENT named character (head-hopping) or between first-person and third-person; a "register" issue where the narration's register shifts. A paragraph whose povHolder is "none" does not by itself create a pov issue. If every paragraph shares the same tense, povHolder, and register, return an empty consistencyIssues array [].

        deviations — an array where each item compares ONE metric of the analyzed text against the reference baseline ([CHAPTER_STYLE_BASELINE]): "metric" = the metric name (as it appears in the metrics above, e.g. "averageSentenceLength" or "lexicalDensity"), "sceneValue" = the metric's value in the analyzed text, "chapterBaseline" = the same metric's value in the reference baseline, "note" = one short, clear sentence in writer-friendly language (no technical jargon) explaining the practical effect of the difference (e.g. pace, readability, or vividness), not just restating the numbers, framed according to what the reference represents as stated in the [CHAPTER_STYLE_BASELINE] section. Prefer reporting NORMALIZED/rate metrics (averageSentenceLength, lexicalDensity, averageWordLength) as deviations, since they reflect style independent of length. Report a raw absolute count (sentenceCount, wordCount, uniqueWords) ONLY when it carries a stylistic signal beyond the size of the text being analyzed. Include only meaningful divergences. If there is no baseline or no divergences, return an empty array [].

        consistencyIssues — report ONLY cross-paragraph shifts between different parts of the text. Grammar, spelling, and syntax errors do NOT belong here - they affect grammaticalityScore only and must NOT be reported here. Choose "type" precisely:
        - "register" — a formality/tone shift in the NARRATION between parts (e.g. plain, casual narration shifting to formal or ornate prose, or vice versa).
        - "tense" — a narration-tense shift between parts (e.g. past tense shifting to present tense mid-narrative).
        - "pov" — a perspective shift between parts (e.g. first-person shifting to third-person, or head-hopping).
        "span" = an EXACT, VERBATIM substring copied character-for-character from the analyzed text where the shift first becomes visible. CRITICAL: the span MUST be a verbatim quote from the ANALYZED text only — NEVER from [PRECEDING_CONTEXT] or [FOLLOWING_CONTEXT]. When the shift is relative to the surrounding context, quote the sentence WITHIN the analyzed text that exhibits the shift. CRITICAL: the span MUST be findable in the analyzed text by an exact-substring search. Copy a contiguous run of about 8 to 15 words straight from the text. MOST CRITICAL: copy each span EXACTLY, character for character, from the analyzed text. Do NOT fix, normalize, or change spelling, punctuation, or verb forms (tense) inside the quote - even if the text looks wrong. In particular, copy verb forms and prefixes exactly as written (do NOT "correct" a present-tense verb to past, and do not alter a single letter). The span must be a verbatim, character-for-character substring of the analyzed text, otherwise it cannot be located. Do NOT paraphrase, summarize, fix, or re-spell it. Do NOT add quotation marks, ellipsis ("..." or "…"), brackets, or any words that are not in the text. Do NOT skip words or join non-adjacent fragments. If the relevant passage is long, copy only its first ~8-15 words verbatim rather than truncating with an ellipsis. "description" = one short sentence stating the shift (from what to what). Report at most 3-4 of the most significant issues; when in doubt, prefer an empty array [] over a false positive. Negative examples: do NOT report things like "sentence has two verbs", "sentence lacks a subject", or any per-sentence grammatical observation. Dialogue is naturally written in a more colloquial, simpler register than narration - do NOT report the natural difference between characters' spoken lines and the narrator's prose as a register shift; only flag a tone shift WITHIN the narration itself (or within dialogue) between parts. If [PRECEDING_CONTEXT]/[FOLLOWING_CONTEXT] are provided, you may use them only to DETECT a shift, but the span you quote must always come from the analyzed text itself. If none are found, return an empty array [].

        Important: every "note" and "description" must be a single short sentence (max ~25 words). Do not repeat the same phrasing or idea more than once.

        Worked examples (illustrate ONLY paragraphAnnotations -> consistencyIssues; other fields omitted for brevity):

        Example A — POV head-hop. Analyzed text (two paragraphs):
        "The clerk counted the coins twice. He was sure the drawer had been short all week, and he meant to prove it before the manager arrived.\n\nIn the back room, Hannah felt the morning drag. She wondered why the lights flickered and decided she would not stay late again."
        paragraphAnnotations: [ { "paragraph": 1, "tense": "past", "povHolder": "the clerk", "register": "plain" }, { "paragraph": 2, "tense": "past", "povHolder": "Hannah", "register": "plain" } ]
        Derivation: povHolder changes from "the clerk" to "Hannah" between paragraphs 1 and 2 -> a "pov" head-hop. Tense and register are stable, so no tense/register issue.
        consistencyIssues: [ { "type": "pov", "span": "In the back room, Hannah felt the morning drag. She wondered why the lights", "description": "POV head-hops from the clerk to Hannah's interiority." } ]

        Example B — tense slip. Analyzed text (two paragraphs):
        "We loaded the truck before dawn. The crates were heavy and the rope kept slipping from our hands.\n\nNow the highway stretches ahead of us and the fields slide past the window in a grey blur."
        paragraphAnnotations: [ { "paragraph": 1, "tense": "past", "povHolder": "first-person", "register": "plain" }, { "paragraph": 2, "tense": "present", "povHolder": "first-person", "register": "plain" } ]
        Derivation: dominant tense flips from past to present between paragraphs 1 and 2 -> a "tense" issue. povHolder and register are stable, so no pov/register issue.
        consistencyIssues: [ { "type": "tense", "span": "Now the highway stretches ahead of us and the fields slide past", "description": "Narration shifts from past tense to present tense." } ]
        """;

    // ── Literary Analysis ───────────────────────────────────────────

    private const string LiteraryHe =
        """
        אתה מומחה לניתוח ספרותי. נתח את הטקסט הבא מבחינה ספרותית.
        
        החזר את התוצאה בפורמט JSON:
        {
          "themes": [
            { "name": "שם הנושא", "description": "תיאור קצר", "significance": "major|minor" }
          ],
          "tone": "טון הטקסט (למשל: אפל, אירוני, נוסטלגי)",
          "toneDescription": "הסבר קצר על הטון ואיך הוא נוצר",
          "narrativeVoice": "סוג הקול המספר (גוף ראשון/שלישי, מוגבל/כל-יודע)",
          "narrativeVoiceDescription": "ניתוח קצר של השפעת הקול המספר",
          "rhetoricalDevices": [
            { "name": "שם האמצעי", "example": "דוגמה מהטקסט", "effect": "ההשפעה על הקורא" }
          ],
          "moodProgression": "תיאור קצר של שינוי האווירה לאורך הטקסט",
          "summary": "סיכום ספרותי כולל: חוזקות, מאפיינים בולטים, והרושם הכללי."
        }
        
        התמקד באיכות הניתוח — העדף עומק על כמות. אל תמציא דוגמאות שלא קיימות בטקסט.
        """;

    private const string LiteraryEn =
        """
        You are a literary analysis expert. Analyze the following text from a literary perspective.
        
        Return the result in JSON format:
        {
          "themes": [
            { "name": "theme name", "description": "brief description", "significance": "major|minor" }
          ],
          "tone": "the text's tone (e.g., dark, ironic, nostalgic)",
          "toneDescription": "brief explanation of the tone and how it is created",
          "narrativeVoice": "type of narrative voice (first/third person, limited/omniscient)",
          "narrativeVoiceDescription": "brief analysis of the narrative voice's effect",
          "rhetoricalDevices": [
            { "name": "device name", "example": "example from the text", "effect": "effect on the reader" }
          ],
          "moodProgression": "brief description of mood changes throughout the text",
          "summary": "Overall literary assessment: strengths, notable features, and general impression."
        }
        
        Focus on quality of analysis — prefer depth over quantity. Do not invent examples not present in the text.
        """;

    // ── Summarization ───────────────────────────────────────────────

    private const string SummarizationHe =
        "סכם את הטקסט בעברית, עד שלושה פסקאות קצרות, בלי להוסיף מידע שלא מופיע במקור. " +
        "שמור על הנקודות העיקריות ועל הטון הכללי של הטקסט המקורי.";

    private const string SummarizationEn =
        "Summarize the text in up to three short paragraphs, without adding information not in the source. " +
        "Preserve the main points and overall tone of the original text.";
}
