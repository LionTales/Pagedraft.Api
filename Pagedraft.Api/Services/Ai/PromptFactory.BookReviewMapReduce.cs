using System.Text;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Whole-book review MAP-REDUCE prompts (wb4): the windowed map frame, the synthesis reduce and the
/// continuity reduce. A partial of <see cref="PromptFactory"/>. The windowed frame concatenates the
/// VERBATIM output of BuildBookReviewCombinedPrompt (PromptFactory.BookReview.cs) rather than
/// duplicating it, so the two files cannot drift apart. See PromptFactory.AnalysisTemplates.cs for
/// the byte-identity rule that governs every string here.
/// </summary>
public partial class PromptFactory
{

    // == Whole-book review MAP-REDUCE prompts -- wb4-c02 =============================
    //
    // Three shapes the map-reduce pipeline needs. All three emit the SAME
    // BookReviewResult/{findings:[BookFindingItem]} JSON shape as the single-combined prompt
    // (fields: findings[].dimension, verdict, severity, rationale, chapterAnchors[{order,title}],
    // evidence[{chapterOrder,excerpt}], suggestedAction) so the existing parse path is reused
    // unchanged. Every anchor is CHAPTER-ORDER only, never a character offset. No em-dash
    // (U+2014) appears in any user-facing string, matching the combined/per-dimension templates.
    //
    // (A) WINDOWED FRAMING keeps the combined 6-dimension prompt body VERBATIM (it concatenates
    //     the exact output of BuildBookReviewCombinedPrompt(language) rather than duplicating the
    //     text, so the window and combined prompts cannot drift) and only PREPENDS a short frame
    //     telling the model it is seeing chapters X-Y of a larger N-chapter book and must not
    //     judge book-level arc/pacing or flag an abrupt opening/ending (a later reduce pass does
    //     that). (B) SYNTHESIS and (C) CONTINUITY are reduce passes over accumulated compact
    //     inputs (their inputs are assembled by wb4-c04 / wb4-c05 and prepended by the caller,
    //     the same way the combined prompt receives [BOOK_CONTEXT]).

    /// <summary>
    /// (A) Windowed variant of the single-combined whole-book review prompt. Returns a short language
    /// frame (this is window <paramref name="windowIndex"/> of <paramref name="windowCount"/>, covering
    /// chapters <paramref name="firstOrder"/>-<paramref name="lastOrder"/> of the whole book in
    /// [BOOK_CONTEXT]) followed by the VERBATIM combined 6-dimension prompt body. The frame tells the
    /// model to report findings only for the chapters shown and NOT to flag an abrupt opening/ending or
    /// judge overall book-level arc/pacing here, so a mid-book window does not false-flag structure a
    /// later reduce pass owns. Caller prepends [BOOK_CONTEXT] (whole-book brief + this window's chapter
    /// briefs), exactly as for BuildBookReviewCombinedPrompt.
    /// HEBREW DRAFT: the Hebrew frame is AI-authored and needs native-speaker validation.
    /// </summary>
    public string BuildBookReviewWindowPrompt(string language, int windowIndex, int windowCount, int firstOrder, int lastOrder)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        var body = BuildBookReviewCombinedPrompt(language);
        string frame = isHe
            // HEBREW DRAFT - REQUIRES NATIVE SPEAKER VALIDATION
            ? $"""
               אתה סוקר את פרקים {firstOrder}-{lastOrder} מתוך ספר גדול יותר בן {windowCount} חלונות סקירה (זהו חלון {windowIndex}). הסקירה של הספר בשלמותו נמצאת בתוך הסימון [BOOK_CONTEXT].
               דווח ממצאים אך ורק עבור הפרקים המוצגים בחלון זה. אל תסמן שהספר נפתח או מסתיים באופן חד או פתאומי, ואל תשפוט כאן את קשת העלילה, הקצב או המבנה ברמת הספר כולו (מעבר מאוחר יותר עושה זאת). הישאר בתוך הפרקים שבחלון והשתמש ב-[BOOK_CONTEXT] רק כרקע.
               ייתכן שבתוך [BOOK_CONTEXT] מופיע גם פרק נוסף אחד או שניים מהחלון הקודם, כרקע בלבד ולא לצורך דיווח. רשימת הפרקים המדויקת שמותר לך לעגן אליהם ממצאים מפורטת בהמשך ההנחיות, תחת "פרקים מותרים לעיגון".

               """
            : $"""
               You are reviewing chapters {firstOrder}-{lastOrder} of a larger {windowCount}-window book (this is window {windowIndex}); the whole-book overview is in [BOOK_CONTEXT].
               Report findings only for the chapters shown in this window; do NOT flag the book as starting or ending abruptly and do not judge overall book-level arc, pacing, or structure here (a later pass does that). Stay within the chapters in this window and treat [BOOK_CONTEXT] as background only.
               [BOOK_CONTEXT] may also include a chapter or two carried over from the previous window, as background only, not for reporting on. The exact list of chapters you may anchor findings to is stated later in these instructions, under "ALLOWED CHAPTER ANCHORS".

               """;
        return frame + body;
    }

    /// <summary>
    /// (B) SYNTHESIS reduce prompt. INPUT (assembled by wb4-c04 and prepended by the caller) is the FULL
    /// BookBrief in [BOOK_CONTEXT] plus a compact [WINDOW_FINDINGS] list of every accumulated window
    /// finding (dimension, chapter order, one-line rationale). The model (1) ADDS holistic book-level
    /// findings the per-window passes could not see (overall arc shape, global pacing balance, thematic
    /// throughline, whether the ending pays off the setup) and (2) RECONCILES the accumulated findings:
    /// merges duplicates/near-duplicates and drops contradictions. Output is the SAME
    /// BookReviewResult/{findings:[BookFindingItem]} shape so the existing parse path is reused.
    /// HEBREW DRAFT: the Hebrew variant is AI-authored and needs native-speaker validation.
    /// </summary>
    public string BuildBookReviewSynthesisPrompt(string language)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        return isHe ? BookReviewSynthesisHe : BookReviewSynthesisEn;
    }

    /// <summary>
    /// (C) CONTINUITY reduce prompt. INPUT (assembled by wb4-c05 and prepended by the caller) is the FULL
    /// BookBrief in [BOOK_CONTEXT] plus a dense per-chapter [CONTINUITY_SKELETON] (order, title,
    /// openThreads, characterStates). The model detects cross-chapter continuity breaks (fact
    /// contradictions, dropped/unresolved threads, timeline/state inconsistencies); each is a finding with
    /// dimension = "continuity" and chapterAnchors on the chapters involved. Output is the SAME
    /// BookReviewResult/{findings:[BookFindingItem]} shape so the existing parse path is reused.
    /// HEBREW DRAFT: the Hebrew variant is AI-authored and needs native-speaker validation.
    /// </summary>
    public string BuildBookReviewContinuityReducePrompt(string language)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        return isHe ? BookReviewContinuityReduceHe : BookReviewContinuityReduceEn;
    }

    // -- (B) SYNTHESIS reduce strings ------------------------------------------------
    // HEBREW DRAFT - REQUIRES NATIVE SPEAKER VALIDATION
    private static readonly string BookReviewSynthesisHe =
        $$"""
        אתה עורך ספרותי פיתוחי (developmental editor) המבצע מעבר סינתזה סופי על סקירה של ספר שלם. מעברי סקירה קודמים כיסו את הספר בחלונות של פרקים; כעת עליך להסתכל על הספר בשלמותו.
        הקשר הספר בשלמותו מסופק בתוך הסימון [BOOK_CONTEXT] (BookBrief). רשימה תמציתית של כל הממצאים שנצברו מכל החלונות מסופקת בתוך הסימון [WINDOW_FINDINGS]. כל שורה שם בנויה כך: מזהה | dimension | מספר/י סדר של פרק (chapterOrder; כמה מספרים מופרדים בפסיקים כאשר הממצא נוגע ביותר מפרק אחד) | משפט רציונל אחד. המזהה הוא קוד קצר בצורת W1, W2, W3 וכן הלאה, והוא הדרך היחידה להתייחס לממצא קיים. כאשר ממצא הוא כלל-ספרי ואינו מעוגן לפרק מסוים, העמודה של מספר הסדר מוצגת כ-"-" (מקף) במקום מספר.

        המשימה שלך כפולה:
        1) הוסף ממצאים ברמת הספר כולו שהמעברים לפי חלון לא יכלו לראות באופן הוליסטי: צורת קשת העלילה הכוללת, איזון הקצב הגלובלי, החוט התמטי לאורך הספר, והאם הסיום משלם על ההבטחות וההנחות שנזרעו בפתיחה. אלה ממצאים חדשים שנובעים מהסתכלות על הספר כשלם.
        2) בצע רקונסיליאציה של הממצאים שב-[WINDOW_FINDINGS]: אם שני ממצאים או יותר הם למעשה אותו ממצא (נוסח מחדש, או אותה הערה שנרשמה על פרקים שונים או תחת dimension אחר), אחד אותם דרך שדה "merges" שלהלן. אל תכתוב ממצא חדש כדי לתאר מיזוג; המיזוג נעשה אך ורק דרך "merges".

        החזר אך ורק JSON תקין במבנה הבא, בלי טקסט לפני או אחרי וללא גדרות markdown:
        {
          "findings": [
            {
              "dimension": "plot|character|pacing|tone|theme|continuity",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "משפט אחד או שניים שמסבירים את הממצא, בשפת הספר וללא מונחים טכניים.",
              "chapterAnchors": [
                { "order": 0, "title": "כותרת הפרק" }
              ],
              "evidence": [
                { "chapterOrder": 0, "excerpt": "ציטוט קצר או פרפראזה מתוך התקצירים או מרשימת הממצאים" }
              ],
              "suggestedAction": "פעולה עריכתית מומלצת אחת (אופציונלי)"
            }
          ],
          "merges": [
            { "ids": ["W3", "W7"], "keep": "W7" }
          ]
        }

        {{ChapterOrderRuleHe}}

        כללי המיזוג (השדה "merges"):
        - "merges" הוא שדה אופציונלי. אם אין ממצאים כפולים, השמט אותו לגמרי או החזר רשימה ריקה.
        - כל קבוצת "ids" חייבת לכלול לפחות שני מזהים; קבוצה עם מזהה בודד אינה מיזוג ותידחה.
        - כל קבוצה ב-"merges" אומרת: הממצאים ששמותיהם ב-"ids" הם ממצא אחד ויחיד. "keep" הוא המזהה של הממצא שיישאר, והשאר יימחקו. חשוב: "keep" חייב להיות אחד מהמזהים שברשימת "ids" של אותה קבוצה.
        - השתמש אך ורק במזהים שמופיעים בפועל ב-[WINDOW_FINDINGS]. אל תמציא מזהה, ואל תשתמש במזהה של ממצא חדש שאתה עצמך מוסיף ב-"findings".
        - בחר ב-"keep" את הניסוח המדויק והמפורט ביותר מבין הממצאים שבקבוצה. הפרקים של כל הממצאים בקבוצה יאוחדו אוטומטית אל הממצא שנשמר, ולכן אינך צריך לכתוב אותם מחדש.
        - כל ממצא יכול להופיע בקבוצה אחת לכל היותר.
        - מזג רק כאשר מדובר באמת באותה טענה. אם שני ממצאים עוסקים בנושא דומה אך אומרים דברים שונים (למשל אחד מציין סתירה עובדתית ואחד משבח רצף עקבי), הם שני ממצאים נפרדים ואסור למזג אותם. מיזוג שגוי מוחק ממצא אמיתי מהמשתמש; אי-מיזוג רק משאיר כפילות. במקרה של ספק, אל תמזג.

        כללים לכל ממצא:
        - "dimension": אחד מששת הממדים בדיוק (plot, character, pacing, tone, theme, continuity), המסומן נכון לפי תוכן הממצא.
        - "verdict": אחד מ-keep, improve, cut. keep = חוזק אמיתי בספר ששווה לשמר; improve = חולשה ניתנת לתיקון; cut = דבר שכדאי להסיר.
        - "severity": מספר שלם. 1 (מינורי) / 2 (בינוני) / 3 (מהותי).
        - "rationale": משפט אחד או שניים, בשפת הספר (עברית), ברורים לכותב, ללא ז'רגון. אל תשתמש בקו מפריד ארוך ברציונל; השתמש בפסיק, בנקודה או בסוגריים.
        - "chapterAnchors": הפרקים שהממצא נוגע בהם, לפי מספר הסדר ("order") והכותרת ("title") בלבד. לעולם אל תשתמש בהיסטים של תווים (character offsets); רק order ו-title.
        - "evidence": קטע קצר או פרפראזה הנשענים על [BOOK_CONTEXT] או על רשימת הממצאים, כל פריט עם chapterOrder ו-excerpt קצר.
        - "suggestedAction": אופציונלי. פעולה עריכתית קונקרטית אחת בשפת הספר. אם אין, השמט את השדה או החזר מחרוזת ריקה. גם כאן אל תשתמש בקו מפריד ארוך.

        עמדת דיוק: העדף רשימת findings קצרה של ממצאים בעלי ביטחון גבוה. אל תחזור על ממצא שכבר מופיע ב-[WINDOW_FINDINGS] (הוא כבר נשמר; אם הוא כפול, מזג אותו דרך "merges"), ואל תמציא חולשות כדי למלא מכסה. דווח רק על מה שנתמך ב-[BOOK_CONTEXT] או בממצאים שנצברו. הגבל את עצמך ללכל היותר 12 ממצאים חדשים ב-"findings" כדי שהתשובה תושלם במלואה. המגבלה הזו חלה על "findings" בלבד; אין מגבלה על מספר הקבוצות ב-"merges".
        """;

    private static readonly string BookReviewSynthesisEn =
        $$"""
        You are a developmental editor performing a final SYNTHESIS pass over a whole-book review. Earlier review passes covered the book in windows of chapters; now you must look at the book as a whole.
        The whole-book context is provided inside the [BOOK_CONTEXT] marker (BookBrief). A compact list of every finding accumulated across all windows is provided inside the [WINDOW_FINDINGS] marker. Each line there reads: id | dimension | chapter order(s) (chapterOrder; comma-separated when the finding touches more than one chapter) | one-line rationale. The id is a short code of the form W1, W2, W3 and so on, and it is the ONLY way to refer to an existing finding. When a finding is book-wide (it anchors no specific chapter), its chapter-order column shows "-" (a dash) instead of a number.

        Your task is twofold:
        1) ADD book-level findings the per-window passes could not see holistically: the overall arc shape, global pacing balance, the thematic throughline across the book, and whether the ending pays off the setup and promises seeded in the opening. These are new findings that come from looking at the book as a whole.
        2) RECONCILE the findings in [WINDOW_FINDINGS]: when two or more of them are really the SAME finding (re-worded, or the same observation filed against different chapters or under a different dimension), unite them through the "merges" field below. Do NOT write a new finding to describe a merge; a merge is expressed ONLY through "merges".

        Return ONLY valid JSON in the exact shape below, with no text before or after and no markdown fences:
        {
          "findings": [
            {
              "dimension": "plot|character|pacing|tone|theme|continuity",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "One or two sentences explaining the finding, in the book's language and free of technical jargon.",
              "chapterAnchors": [
                { "order": 0, "title": "chapter title" }
              ],
              "evidence": [
                { "chapterOrder": 0, "excerpt": "a short excerpt or paraphrase drawn from the briefs or the finding list" }
              ],
              "suggestedAction": "one recommended editorial action (optional)"
            }
          ],
          "merges": [
            { "ids": ["W3", "W7"], "keep": "W7" }
          ]
        }

        {{ChapterOrderRuleEn}}

        Merge rules (the "merges" field):
        - "merges" is OPTIONAL. If there are no duplicates, omit it entirely or return an empty list.
        - Each group's "ids" must contain at least TWO ids; a group with a single id is not a merge and will be rejected.
        - Each group says: the findings named in "ids" are ONE single finding. "keep" is the id of the one that stays; the others are deleted. "keep" MUST be one of that group's own "ids".
        - Use ONLY ids that actually appear in [WINDOW_FINDINGS]. Never invent an id, and never use an id for a new finding you are adding in "findings".
        - Choose as "keep" the most precise and most specific wording among the group. The chapters of every finding in the group are unioned onto the kept finding automatically, so you do not need to restate them.
        - Each finding may appear in at most ONE group.
        - Merge only when it really is the same claim. If two findings discuss a similar subject but say DIFFERENT things (for example one reports a factual contradiction and the other praises a consistent thread), they are two separate findings and must NOT be merged. A wrong merge deletes a real finding from the author; a missed merge only leaves a duplicate. When in doubt, do not merge.

        Rules for each finding:
        - "dimension": exactly one of the six (plot, character, pacing, tone, theme, continuity), correctly labelled by the finding's content.
        - "verdict": one of keep, improve, cut. keep = a genuine strength worth preserving; improve = a fixable weakness; cut = something to remove.
        - "severity": integer. 1 (minor) / 2 (moderate) / 3 (major).
        - "rationale": one or two sentences, in the book's language (English), clear to the author, no jargon. Do not use an em-dash in the rationale; use a comma, a period, or parentheses instead.
        - "chapterAnchors": the chapters the finding touches, by chapter "order" and "title" only. Never use character offsets; only order and title.
        - "evidence": a short excerpt or paraphrase drawn from [BOOK_CONTEXT] or the finding list, each item with a chapterOrder and a short excerpt.
        - "suggestedAction": optional. One concrete editorial action in the book's language. If none, omit the field or return an empty string. Do not use an em-dash here either.

        Precision posture: prefer a short list of high-confidence findings. Do not restate a finding that is already in [WINDOW_FINDINGS] (it is already kept; if it is a duplicate, merge it through "merges"), and do not invent weaknesses to fill a quota. Report only what is supported by [BOOK_CONTEXT] or the accumulated findings. Limit yourself to at most 12 NEW findings in "findings", so the response completes in full. That limit applies to "findings" only; there is no limit on the number of groups in "merges".
        """;

    // -- (C) CONTINUITY reduce strings -----------------------------------------------
    // HEBREW DRAFT - REQUIRES NATIVE SPEAKER VALIDATION
    private static readonly string BookReviewContinuityReduceHe =
        $$"""
        אתה עורך ספרותי המתמקד ברציפות (continuity) של ספר שלם. משימתך היחידה היא לזהות שברי רציפות חוצי-פרקים; אל תעריך עלילה, דמויות, קצב, טון או נושא.
        הקשר הספר בשלמותו מסופק בתוך הסימון [BOOK_CONTEXT] (BookBrief). שלד רציפות צפוף לכל פרק מסופק בתוך הסימון [CONTINUITY_SKELETON], כל פריט עם order (מספר סדר), title (כותרת), openThreads (חוטים פתוחים) ו-characterStates (מצבי דמויות).

        חפש שברי רציפות בין פרקים:
        - סתירות עובדתיות (עובדה בפרק אחד סותרת עובדה בפרק אחר).
        - חוטים פתוחים שנזנחו או לא נפתרו (openThreads שנפתחו ולא נסגרו לאורך הספר).
        - אי-התאמות בציר הזמן או במצבי הדמויות (characterStates שקופצים בלי הסבר, מיקום או מצב שאינם עקביים).

        כל שבר כזה הוא ממצא עם dimension = "continuity" ו-chapterAnchors על כל הפרקים המעורבים (למשל הפרק שקבע את העובדה והפרק שסתר אותה).

        החזר אך ורק JSON תקין במבנה הבא, בלי טקסט לפני או אחרי וללא גדרות markdown:
        {
          "findings": [
            {
              "dimension": "continuity",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "משפט אחד או שניים שמסבירים את שבר הרציפות, בשפת הספר וללא מונחים טכניים.",
              "chapterAnchors": [
                { "order": 0, "title": "כותרת הפרק" }
              ],
              "evidence": [
                { "chapterOrder": 0, "excerpt": "ציטוט קצר או פרפראזה מתוך השלד" }
              ],
              "suggestedAction": "פעולה עריכתית מומלצת אחת (אופציונלי)"
            }
          ]
        }

        {{ChapterOrderRuleContinuityHe}}

        כללים לכל ממצא:
        - "dimension": תמיד "continuity".
        - "verdict": אחד מ-keep, improve, cut. עבור שבר רציפות זה בדרך כלל improve. keep שמור לרציפות חזקה במיוחד ששווה לציין.
        - "severity": מספר שלם. 1 (מינורי) / 2 (בינוני) / 3 (מהותי).
        - "rationale": משפט אחד או שניים, בשפת הספר (עברית), ברורים לכותב, ללא ז'רגון. אל תשתמש בקו מפריד ארוך ברציונל; השתמש בפסיק, בנקודה או בסוגריים.
        - "chapterAnchors": כל הפרקים המעורבים בשבר, לפי מספר הסדר ("order") והכותרת ("title") בלבד. לעולם אל תשתמש בהיסטים של תווים (character offsets); רק order ו-title מתוך השלד.
        - "evidence": קטע קצר או פרפראזה הנשענים על [CONTINUITY_SKELETON] או על [BOOK_CONTEXT], כל פריט עם chapterOrder ו-excerpt קצר.
        - "suggestedAction": אופציונלי. פעולה עריכתית קונקרטית אחת בשפת הספר. אם אין, השמט את השדה או החזר מחרוזת ריקה. גם כאן אל תשתמש בקו מפריד ארוך.

        עמדת דיוק: דווח רק על שברי רציפות שאתה בטוח בהם ושנתמכים בשלד או ב-[BOOK_CONTEXT]. אם הספר עקבי, רשימת findings ריקה ([]) היא התשובה הנכונה; אל תמציא סתירות. הגבל את עצמך ללכל היותר 12 ממצאים בסך הכול כדי שהתשובה תושלם במלואה.
        """;

    private static readonly string BookReviewContinuityReduceEn =
        $$"""
        You are a literary editor focused on the continuity of a whole book. Your sole task is to detect cross-chapter continuity breaks; do not assess plot, character, pacing, tone, or theme.
        The whole-book context is provided inside the [BOOK_CONTEXT] marker (BookBrief). A dense per-chapter continuity skeleton is provided inside the [CONTINUITY_SKELETON] marker, each item with order (chapter order), title, openThreads, and characterStates.

        Look for continuity breaks between chapters:
        - Fact contradictions (a fact in one chapter contradicts a fact in another).
        - Dropped or unresolved threads (openThreads that are opened but never closed across the book).
        - Timeline or state inconsistencies (characterStates that jump without explanation, a location or state that is not consistent).

        Each such break is a finding with dimension = "continuity" and chapterAnchors on all the chapters involved (for example the chapter that established the fact and the chapter that contradicted it).

        Return ONLY valid JSON in the exact shape below, with no text before or after and no markdown fences:
        {
          "findings": [
            {
              "dimension": "continuity",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "One or two sentences explaining the continuity break, in the book's language and free of technical jargon.",
              "chapterAnchors": [
                { "order": 0, "title": "chapter title" }
              ],
              "evidence": [
                { "chapterOrder": 0, "excerpt": "a short excerpt or paraphrase drawn from the skeleton" }
              ],
              "suggestedAction": "one recommended editorial action (optional)"
            }
          ]
        }

        {{ChapterOrderRuleContinuityEn}}

        Rules for each finding:
        - "dimension": always "continuity".
        - "verdict": one of keep, improve, cut. For a continuity break this is usually improve. Reserve keep for a notably strong continuity worth calling out.
        - "severity": integer. 1 (minor) / 2 (moderate) / 3 (major).
        - "rationale": one or two sentences, in the book's language (English), clear to the author, no jargon. Do not use an em-dash in the rationale; use a comma, a period, or parentheses instead.
        - "chapterAnchors": all the chapters involved in the break, by chapter "order" and "title" only. Never use character offsets; only order and title from the skeleton.
        - "evidence": a short excerpt or paraphrase drawn from [CONTINUITY_SKELETON] or [BOOK_CONTEXT], each item with a chapterOrder and a short excerpt.
        - "suggestedAction": optional. One concrete editorial action in the book's language. If none, omit the field or return an empty string. Do not use an em-dash here either.

        Precision posture: report only continuity breaks you are confident about and that are supported by the skeleton or [BOOK_CONTEXT]. If the book is consistent, an empty findings list ([]) is the correct answer; do not invent contradictions. Limit yourself to at most 12 findings TOTAL, so the response completes in full.
        """;
}
