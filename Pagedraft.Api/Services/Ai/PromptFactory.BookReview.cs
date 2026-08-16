using System.Text;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Whole-book developmental review (wb2): the six per-dimension lenses, the SINGLE-COMBINED default
/// prompt, the shared chapter-order contract and the b7 anchor allowlist. A partial of
/// <see cref="PromptFactory"/>; see PromptFactory.AnalysisTemplates.cs for the byte-identity rule
/// that governs every string here.
/// </summary>
public partial class PromptFactory
{

    // -- Whole-book review (per-dimension) -- wb2-c01 -----------------
    //
    // One focused prompt per editorial dimension. wb2-c02 assembles the token-budgeted
    // [BOOK_CONTEXT] block (BookBrief + included ChapterBriefs) via AppendSection and
    // prepends it before the instruction returned by BuildBookReviewPrompt; the model
    // sees the whole book through a single dimension's lens and returns findings ONLY
    // for that dimension. The output JSON is aligned to BookReviewResult/BookFindingItem
    // so wb2-c02 can deserialize each per-dimension response with the existing parse path
    // and union the findings + roll up scores across dimensions.
    //
    // The "dimensionSignals" field mirrors the linguistic paragraphAnnotations idiom: it is
    // an internal annotate-then-decide scratch field that has NO matching property on
    // BookReviewResult/BookFindingItem, so it is silently dropped on parse. Instructing the
    // model to produce it first grounds the findings in observed evidence.
    //
    // NO em-dash (U+2014) appears in any of these strings: rationale/suggestedAction are
    // user-facing, and keeping the prompt itself em-dash-free avoids the model echoing that
    // punctuation into the output (see CLAUDE.md frontend conventions + wb2-c01 hard rule).

    /// <summary>
    /// Returns the per-dimension whole-book review instruction for the given dimension and language.
    /// Caller (wb2-c02) is responsible for prepending the [BOOK_CONTEXT] section (BookBrief +
    /// included ChapterBriefs) assembled via AppendSection, exactly as GetAnalysisPrompt(type, lang,
    /// context) prepends its preamble. <paramref name="dimension"/> is one of:
    /// plot | character | pacing | tone | theme | continuity (case-insensitive). An unknown
    /// dimension falls back to the plot lens.
    /// </summary>
    public string BuildBookReviewPrompt(string dimension, string language)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        return (dimension?.Trim().ToLowerInvariant()) switch
        {
            "character"  => isHe ? BookReviewCharacterHe  : BookReviewCharacterEn,
            "pacing"     => isHe ? BookReviewPacingHe     : BookReviewPacingEn,
            "tone"       => isHe ? BookReviewToneHe       : BookReviewToneEn,
            "theme"      => isHe ? BookReviewThemeHe      : BookReviewThemeEn,
            "continuity" => isHe ? BookReviewContinuityHe : BookReviewContinuityEn,
            "plot"       => isHe ? BookReviewPlotHe       : BookReviewPlotEn,
            _            => isHe ? BookReviewPlotHe       : BookReviewPlotEn,
        };
    }

    // -- Whole-book review (SINGLE-COMBINED) -- wb2-r02 ---------------
    //
    // ONE prompt that reviews ALL SIX dimensions in a single pass over the shared [BOOK_CONTEXT], the
    // DEFAULT path (AiOptions.BookReviewSingleCombined = true). It keeps the per-dimension prompt's posture
    // verbatim (annotate-then-decide via a BOUNDED dimensionSignals scratch block, the precision posture of
    // preferring few high-confidence findings / [] over low-value, CHAPTER-order anchors only, the em-dash
    // ban) but folds the six dimensions into one request and asks the model to SELF-LABEL each finding's
    // dimension (one of the six) instead of stamping a single dimension server-side.
    //
    // OUTPUT-BUDGET CAP: a single response carries findings for all six dimensions, so it must fit the
    // BookReview NumPredict=6144 output budget (appsettings Ollama_BookReview). The prompt therefore caps
    // dimensionSignals to <=12 terse observations TOTAL (NOT scaled per chapter) and caps findings to ~8-12
    // HIGH-VALUE findings TOTAL across all dimensions, so the combined output stays well within NumPredict.
    //
    // NO em-dash (U+2014) appears in either string (user-facing rationale/suggestedAction plus the em-dash
    // echo risk, identical to the per-dimension templates). The Hebrew variant is an AI-authored DRAFT and
    // requires native-speaker validation before the Hebrew review numbers are trusted.

    /// <summary>
    /// Returns the SINGLE-COMBINED whole-book review instruction for the given language: one prompt that
    /// reviews all six editorial dimensions (plot, character, pacing, tone, theme, continuity) in a single
    /// pass over the [BOOK_CONTEXT] the caller prepends. Each finding is self-labelled with its dimension.
    /// Caps dimensionSignals (<=12) and findings (~8-12) so the combined response fits NumPredict=6144.
    /// HEBREW DRAFT: the Hebrew variant is AI-authored and needs native-speaker validation.
    /// </summary>
    public string BuildBookReviewCombinedPrompt(string language)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        return isHe ? BookReviewCombinedHe : BookReviewCombinedEn;
    }

    // ── THE CHAPTER ORDER CONTRACT (single-sourced into EVERY BookReview prompt surface) ──────────────
    //
    // Chapter Order is 0-BASED in the DB (every book runs 0..N-1) and the assembled [BOOK_CONTEXT] prints that
    // exact number in each chapter heading ("## Chapter {order}: {title}"). The prompts USED to say only "use
    // the order and title from [BOOK_CONTEXT]" while showing "order": 1 in the JSON example, which reads as
    // 1-based; and the DEGRADED (flat) context path printed no order at all. So the model guessed: a one-chapter
    // book (real order 0) whose only chapter is titled "פרק 16" came back with anchors claiming orders 1 and 16,
    // the latter read straight out of the TITLE. Neither existed, and every anchor was persisted with an empty
    // chapterId. The prompt and the parser must AGREE, so the contract is stated explicitly here, the JSON
    // examples start at 0, and ChapterAnchorResolver enforces it server-side (resolve by order, then by title,
    // else DROP the anchor). Keep all three in step.
    // HEBREW DRAFT - REQUIRES NATIVE SPEAKER VALIDATION
    private const string ChapterOrderRuleHe =
        "כלל מספרי הפרקים (חובה): העתק את הערך של \"order\" ו-\"chapterOrder\" בדיוק כפי שהוא מופיע בכותרת הפרק " +
        "בתוך [BOOK_CONTEXT] (\"## Chapter <order>: <title>\"). מספרי הסדר מתחילים ב-0: הפרק הראשון הוא 0, השני 1, " +
        "וכן הלאה. אל תסיק מספר סדר מתוך כותרת הפרק (פרק שכותרתו \"פרק 16\" אינו בהכרח מספר סדר 16), אל תמספר מחדש " +
        "החל מ-1, ואל תמציא מספר. אם אינך בטוח במספר הסדר של פרק, אל תוסיף אותו כעוגן במקום לנחש.";

    private const string ChapterOrderRuleEn =
        "CHAPTER ORDER RULE (mandatory): copy the \"order\" and \"chapterOrder\" values EXACTLY as they appear in " +
        "the chapter heading inside [BOOK_CONTEXT] (\"## Chapter <order>: <title>\"). Orders start at 0: the first " +
        "chapter is 0, the second is 1, and so on. Never infer an order from the chapter TITLE (a chapter titled " +
        "\"Chapter 16\" does not mean its order is 16), never renumber from 1, and never invent a number. If you " +
        "are unsure of a chapter's order, leave that chapter out of the anchors rather than guessing.";

    // The CONTINUITY reduce pass reads its chapters from the dense [CONTINUITY_SKELETON] (one line per chapter,
    // "#<order> <title> | threads: … | states: …"), not from the [BOOK_CONTEXT] chapter headings, so the same
    // contract points at the skeleton instead. Same 0-based rule, same ban on guessing.
    // HEBREW DRAFT - REQUIRES NATIVE SPEAKER VALIDATION
    private const string ChapterOrderRuleContinuityHe =
        "כלל מספרי הפרקים (חובה): העתק את הערך של \"order\" ו-\"chapterOrder\" בדיוק כפי שהוא מופיע ב-" +
        "[CONTINUITY_SKELETON] (כל שורה מתחילה ב-#<order>). מספרי הסדר מתחילים ב-0. אל תסיק מספר סדר מתוך כותרת " +
        "הפרק, אל תמספר מחדש החל מ-1, ואל תמציא מספר. אם אינך בטוח במספר הסדר של פרק, אל תוסיף אותו כעוגן.";

    private const string ChapterOrderRuleContinuityEn =
        "CHAPTER ORDER RULE (mandatory): copy the \"order\" and \"chapterOrder\" values EXACTLY as they appear in " +
        "[CONTINUITY_SKELETON] (each line starts with #<order>). Orders start at 0. Never infer an order from the " +
        "chapter title, never renumber from 1, and never invent a number. If you are unsure of a chapter's order, " +
        "leave that chapter out of the anchors rather than guessing.";

    // ── THE ANCHOR ALLOWLIST (b7) ─────────────────────────────────────────────────────────────────────
    //
    // The ChapterOrderRule above says "copy the order, do not invent it" — and the model obeys it, in the sense
    // that it copies a REAL number. What it does NOT obey is the boundary of its own context: the whole-book
    // review is a MAP-REDUCE, so each pass is shown only a SLICE of the book (one window's chapters, a findings
    // digest, a skeleton group). Shown chapters 11-16, the model still anchored a finding to chapters 2 and 5 —
    // real chapters, but ones it had never read. "Do not invent an order" cannot catch that, because 2 and 5 are
    // not invented; they are just not THIS pass's chapters. So each pass now states its allowed set EXPLICITLY,
    // and ChapterAnchorResolver enforces the same set server-side (an anchor outside it is dropped as UNSEEN).
    // The prompt makes the model right more often; the resolver makes a wrong one HARMLESS. Both, not either.
    //
    // Placed LAST in the instruction by the caller (after the prompt body) for recency salience, and rendered
    // from the SAME set the resolver gates on, so the prompt and the parser cannot drift.

    /// <summary>
    /// b7: the ALLOWLIST clause naming the exact chapter orders this pass may anchor to — the orders its context
    /// actually SHOWS. Appended after the prompt body by every whole-book review pass (window map, synthesis
    /// reduce, continuity reduce), each supplying the orders IT displayed.
    ///
    /// An EMPTY <paramref name="allowedOrders"/> is a real state, not a bug: a pass can show no chapter orders at
    /// all (e.g. a synthesis digest in which every accumulated finding is book-wide). Then the honest instruction
    /// is "you can see no chapter numbers, so anchor nothing" — which is what it emits, rather than a nonsensical
    /// empty list. Orders are rendered ascending and de-duplicated so the clause is deterministic.
    /// </summary>
    public string BuildChapterAnchorAllowlistRule(string language, IReadOnlyCollection<int> allowedOrders)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        var orders = (allowedOrders ?? Array.Empty<int>()).Distinct().OrderBy(o => o).ToList();

        if (orders.Count == 0)
        {
            return isHe
                // HEBREW DRAFT - REQUIRES NATIVE SPEAKER VALIDATION
                ? "פרקים מותרים לעיגון (חובה): במעבר הזה לא מוצג לפניך אף מספר סדר של פרק. לכן החזר \"chapterAnchors\": [] " +
                  "ריק בכל ממצא ואל תוסיף \"evidence\" עם chapterOrder. אל תנחש מספר פרק. עיגון לפרק שלא הוצג לך יימחק."
                : "ALLOWED CHAPTER ANCHORS (mandatory): this pass shows you NO chapter order at all. Return an empty " +
                  "\"chapterAnchors\": [] on every finding and do not attach \"evidence\" with a chapterOrder. Do not " +
                  "guess a chapter number. An anchor to a chapter you were not shown will be DISCARDED.";
        }

        var list = string.Join(", ", orders);
        return isHe
            // HEBREW DRAFT - REQUIRES NATIVE SPEAKER VALIDATION
            ? $"פרקים מותרים לעיגון (חובה): במעבר הזה מותר לך לעגן ממצאים אך ורק למספרי הסדר הבאים: {list}. אלה הפרקים " +
              "היחידים שהוצגו לפניך. כל מספר סדר אחר אסור, גם אם הוא קיים בספר, כי לא קראת את הפרק ההוא במעבר הזה. " +
              "אם ממצא נוגע לפרק שאינו ברשימה, או שאינך בטוח, החזר \"chapterAnchors\": [] ריק (ממצא כלל-ספרי) במקום לנחש. " +
              "גם \"chapterOrder\" ב-\"evidence\" חייב להיות מתוך הרשימה הזו. עיגון לפרק שמחוץ לרשימה יימחק."
            : $"ALLOWED CHAPTER ANCHORS (mandatory): in THIS pass you may anchor findings ONLY to these chapter " +
              $"orders: {list}. They are the only chapters you were shown. Any other order is forbidden even if it " +
              "exists in the book, because you did not read that chapter in this pass. If a finding is about a " +
              "chapter that is not in this list, or you are unsure, return an empty \"chapterAnchors\": [] (a " +
              "book-wide finding) instead of guessing. Every \"chapterOrder\" in \"evidence\" must also come from " +
              "this list. An anchor outside the list will be DISCARDED.";
    }

    // The six dimensions, listed once in each language so the prompt names them consistently.
    private static readonly string BookReviewCombinedHe =
        $$"""
        אתה עורך ספרותי פיתוחי (developmental editor) המעריך ספר שלם בבת אחת על פני שישה ממדים: plot (עלילה), character (דמויות), pacing (קצב), tone (טון/קול), theme (נושא), continuity (רציפות).
        הקשר הספר מסופק בתוך הסימון [BOOK_CONTEXT]: סקירת הספר (BookBrief) ותקצירי הפרקים שנכללו, לפי הסדר. קרא את כל הספר וזהה ממצאים עריכתיים על פני כל ששת הממדים בקריאה אחת.

        החזר אך ורק JSON תקין במבנה הבא, בלי טקסט לפני או אחרי וללא גדרות markdown:
        {
          "dimensionSignals": [
            { "dimension": "plot", "chapterOrder": 0, "title": "כותרת הפרק", "observation": "תצפית קצרה על הממד בפרק זה" }
          ],
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
                { "chapterOrder": 0, "excerpt": "ציטוט קצר או פרפראזה מתוך התקצירים" }
              ],
              "suggestedAction": "פעולה עריכתית מומלצת אחת (אופציונלי)"
            }
          ]
        }

        {{ChapterOrderRuleHe}}

        dimensionSignals: שדה עזר פנימי למחשבה (אינו מוצג למשתמש). לפני שתחליט על findings, רשום תצפיות קצרות על פני הממדים השונים. רשום תצפית רק עבור צמדי ממד-פרק הרלוונטיים באמת, ולא עבור כל פרק וכל ממד; הגבל את עצמך ללכל היותר 12 תצפיות בסך הכול (לא לפי פרק), כל אחת משפט קצר אחד (dimension, chapterOrder, title, ותצפית תמציתית). בחר את התצפיות החשובות ביותר לאורך הספר. השתמש בתצפיות האלה כדי לגזור את הממצאים.

        findings: גזור מתוך dimensionSignals מספר קטן של ממצאים בעלי ביטחון גבוה בלבד, על פני כל ששת הממדים, לכל היותר 8 עד 12 ממצאים בסך הכול (לא לפי ממד). כל ממצא:
        - "dimension": אחד מששת הממדים בדיוק (plot, character, pacing, tone, theme, continuity), המסומן נכון לפי תוכן הממצא.
        - "verdict": אחד מ-keep, improve, cut. keep = חוזק אמיתי בספר ששווה לשמר; improve = חולשה ניתנת לתיקון; cut = דבר שכדאי להסיר.
        - "severity": מספר שלם. 1 (מינורי) / 2 (בינוני) / 3 (מהותי).
        - "rationale": משפט אחד או שניים, בשפת הספר (עברית), ברורים לכותב, ללא ז'רגון. אל תשתמש בקו מפריד ארוך ברציונל; השתמש בפסיק, בנקודה או בסוגריים.
        - "chapterAnchors": הפרקים שהממצא נוגע בהם, לפי מספר הסדר ("order") והכותרת ("title") בלבד. לעולם אל תשתמש בהיסטים של תווים (character offsets) או בטווחי מיקום; רק order ו-title מתוך [BOOK_CONTEXT].
        - "evidence": קטע קצר או פרפראזה הנשענים על התקצירים שב-[BOOK_CONTEXT], כל פריט עם chapterOrder ו-excerpt קצר.
        - "suggestedAction": אופציונלי. פעולה עריכתית קונקרטית אחת בשפת הספר. אם אין, השמט את השדה או החזר מחרוזת ריקה. גם כאן אל תשתמש בקו מפריד ארוך.

        עמדת דיוק: העדף רשימת findings קצרה של ממצאים בעלי ביטחון גבוה על פני רשימה ארוכה של ממצאים חלשים. אם הספר חזק בממד מסוים, אפס ממצאים בו היא התשובה הנכונה; אל תמציא חולשות כדי לכסות את כל הממדים או למלא מכסה. דווח רק על מה שאתה בטוח בו ושנתמך בראיות מהתקצירים. אל תחרוג מ-12 ממצאים בסך הכול כדי שהתשובה תושלם במלואה.
        """;

    private static readonly string BookReviewCombinedEn =
        $$"""
        You are a developmental editor assessing a complete book in ONE pass across all SIX dimensions: plot, character, pacing, tone (tone/voice), theme, continuity.
        The book context is provided inside the [BOOK_CONTEXT] marker: the book overview (BookBrief) and the included chapter briefs, in order. Read the whole book and identify editorial findings across all six dimensions in a single read.

        Return ONLY valid JSON in the exact shape below, with no text before or after and no markdown fences:
        {
          "dimensionSignals": [
            { "dimension": "plot", "chapterOrder": 0, "title": "chapter title", "observation": "a short observation about this dimension in this chapter" }
          ],
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
                { "chapterOrder": 0, "excerpt": "a short excerpt or paraphrase drawn from the briefs" }
              ],
              "suggestedAction": "one recommended editorial action (optional)"
            }
          ]
        }

        {{ChapterOrderRuleEn}}

        dimensionSignals: an internal scratch field for your own reasoning (not shown to the user). Before deciding on findings, note short observations across the dimensions. Write an observation ONLY for the dimension+chapter pairs that are genuinely relevant, not for every chapter and every dimension; limit yourself to at most 12 observations TOTAL (not per chapter), each a single short sentence (dimension, chapterOrder, title, and a terse observation). Pick the observations that matter most across the book. Use these notes to derive the findings.

        findings: derive from dimensionSignals a SMALL number of HIGH-CONFIDENCE findings only, spread across the six dimensions, at most 8 to 12 findings TOTAL (not per dimension). Each finding:
        - "dimension": exactly one of the six (plot, character, pacing, tone, theme, continuity), correctly labelled by the finding's content.
        - "verdict": one of keep, improve, cut. keep = a genuine strength worth preserving; improve = a fixable weakness; cut = something to remove.
        - "severity": integer. 1 (minor) / 2 (moderate) / 3 (major).
        - "rationale": one or two sentences, in the book's language (English), clear to the author, no jargon. Do not use an em-dash in the rationale; use a comma, a period, or parentheses instead.
        - "chapterAnchors": the chapters the finding touches, by chapter "order" and "title" only. Never use character offsets or position spans; only order and title from [BOOK_CONTEXT].
        - "evidence": a short excerpt or paraphrase drawn from the briefs in [BOOK_CONTEXT], each item with a chapterOrder and a short excerpt.
        - "suggestedAction": optional. One concrete editorial action in the book's language. If none, omit the field or return an empty string. Do not use an em-dash here either.

        Precision posture: prefer a short list of high-confidence findings over a long list of weak ones. If the book is strong on a dimension, zero findings there is the correct answer; do not invent weaknesses to cover every dimension or fill a quota. Report only what you are confident about and what is supported by evidence from the briefs. Do not exceed 12 findings TOTAL, so the response completes in full.
        """;

    // Shared JSON-shape + rules, parameterised per dimension. dimensionKey is the key the model
    // must stamp on every finding; lens is the dimension-specific lens guidance; signalGuidance is
    // the dimension-specific scratch-note guidance. No em-dash anywhere in these templates.
    private static string BuildBookReviewHe(string dimensionKey, string lens, string signalGuidance) =>
        $$"""
        אתה עורך ספרותי פיתוחי (developmental editor) המעריך ספר שלם דרך עדשה אחת בלבד: {{lens}}
        הקשר הספר מסופק בתוך הסימון [BOOK_CONTEXT]: סקירת הספר (BookBrief) ותקצירי הפרקים שנכללו, לפי הסדר. קרא את כל הספר דרך העדשה הזו בלבד; אל תעריך ממדים אחרים (עלילה, דמויות, קצב, טון, נושא, רציפות) למעט הממד שהוקצה לך כאן.

        החזר אך ורק JSON תקין במבנה הבא, בלי טקסט לפני או אחרי וללא גדרות markdown:
        {
          "dimensionSignals": [
            { "chapterOrder": 0, "title": "כותרת הפרק", "observation": "תצפית קצרה על {{dimensionKey}} בפרק זה לאורך הספר" }
          ],
          "findings": [
            {
              "dimension": "{{dimensionKey}}",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "משפט אחד או שניים שמסבירים את הממצא, בשפת הספר וללא מונחים טכניים.",
              "chapterAnchors": [
                { "order": 0, "title": "כותרת הפרק" }
              ],
              "evidence": [
                { "chapterOrder": 0, "excerpt": "ציטוט קצר או פרפראזה מתוך התקצירים" }
              ],
              "suggestedAction": "פעולה עריכתית מומלצת אחת (אופציונלי)"
            }
          ]
        }

        {{ChapterOrderRuleHe}}

        dimensionSignals: שדה עזר פנימי למחשבה (אינו מוצג למשתמש). לפני שתחליט על findings, רשום תצפיות קצרות. {{signalGuidance}} רשום תצפית רק עבור הפרקים הרלוונטיים באמת לממד שהוקצה לך, ולא עבור כל פרק; הגבל את עצמך ללכל היותר 12 תצפיות, כל אחת משפט קצר אחד (chapterOrder, title, ותצפית תמציתית). אם בספר יותר מ-12 פרקים, בחר את הפרקים החשובים ביותר לממד זה בלבד. השתמש בתצפיות האלה כדי לגזור את הממצאים.

        findings: גזור מתוך dimensionSignals מספר קטן של ממצאים בעלי ביטחון גבוה בלבד (לכל היותר 3 עד 6). כל ממצא:
        - "dimension": תמיד "{{dimensionKey}}".
        - "verdict": אחד מ-keep, improve, cut. keep = חוזק אמיתי בספר ששווה לשמר; improve = חולשה ניתנת לתיקון; cut = דבר שכדאי להסיר.
        - "severity": מספר שלם. 1 (מינורי) / 2 (בינוני) / 3 (מהותי).
        - "rationale": משפט אחד או שניים, בשפת הספר (עברית), ברורים לכותב, ללא ז'רגון. אל תשתמש בקו מפריד ארוך ברציונל; השתמש בפסיק, בנקודה או בסוגריים.
        - "chapterAnchors": הפרקים שהממצא נוגע בהם, לפי מספר הסדר ("order") והכותרת ("title") בלבד. לעולם אל תשתמש בהיסטים של תווים (character offsets) או בטווחי מיקום; רק order ו-title מתוך [BOOK_CONTEXT].
        - "evidence": קטע קצר או פרפראזה הנשענים על התקצירים שב-[BOOK_CONTEXT], כל פריט עם chapterOrder ו-excerpt קצר.
        - "suggestedAction": אופציונלי. פעולה עריכתית קונקרטית אחת בשפת הספר. אם אין, השמט את השדה או החזר מחרוזת ריקה. גם כאן אל תשתמש בקו מפריד ארוך.

        עמדת דיוק: העדף רשימת findings ריקה ([]) על פני ממצאים חלשים או שוליים. אם הספר חזק בממד הזה, מעט ממצאים או אפס ממצאים היא התשובה הנכונה; אל תמציא חולשות כדי למלא מכסה. דווח רק על מה שאתה בטוח בו ושנתמך בראיות מהתקצירים.
        """;

    private static string BuildBookReviewEn(string dimensionKey, string lens, string signalGuidance) =>
        $$"""
        You are a developmental editor assessing a complete book through ONE lens only: {{lens}}
        The book context is provided inside the [BOOK_CONTEXT] marker: the book overview (BookBrief) and the included chapter briefs, in order. Read the whole book through this single lens; do not assess other dimensions (plot, character, pacing, tone, theme, continuity) other than the one assigned here.

        Return ONLY valid JSON in the exact shape below, with no text before or after and no markdown fences:
        {
          "dimensionSignals": [
            { "chapterOrder": 0, "title": "chapter title", "observation": "a short observation about {{dimensionKey}} in this chapter across the book" }
          ],
          "findings": [
            {
              "dimension": "{{dimensionKey}}",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "One or two sentences explaining the finding, in the book's language and free of technical jargon.",
              "chapterAnchors": [
                { "order": 0, "title": "chapter title" }
              ],
              "evidence": [
                { "chapterOrder": 0, "excerpt": "a short excerpt or paraphrase drawn from the briefs" }
              ],
              "suggestedAction": "one recommended editorial action (optional)"
            }
          ]
        }

        {{ChapterOrderRuleEn}}

        dimensionSignals: an internal scratch field for your own reasoning (not shown to the user). Before deciding on findings, note short observations. {{signalGuidance}} Write an observation ONLY for the chapters that are genuinely relevant to your assigned dimension, not for every chapter; limit yourself to at most 12 observations, each a single short sentence (chapterOrder, title, and a terse observation). If the book has more than 12 chapters, pick only the chapters that matter most for this dimension. Use these notes to derive the findings.

        findings: derive from dimensionSignals a SMALL number of HIGH-CONFIDENCE findings only (at most 3 to 6). Each finding:
        - "dimension": always "{{dimensionKey}}".
        - "verdict": one of keep, improve, cut. keep = a genuine strength worth preserving; improve = a fixable weakness; cut = something to remove.
        - "severity": integer. 1 (minor) / 2 (moderate) / 3 (major).
        - "rationale": one or two sentences, in the book's language (English), clear to the author, no jargon. Do not use an em-dash in the rationale; use a comma, a period, or parentheses instead.
        - "chapterAnchors": the chapters the finding touches, by chapter "order" and "title" only. Never use character offsets or position spans; only order and title from [BOOK_CONTEXT].
        - "evidence": a short excerpt or paraphrase drawn from the briefs in [BOOK_CONTEXT], each item with a chapterOrder and a short excerpt.
        - "suggestedAction": optional. One concrete editorial action in the book's language. If none, omit the field or return an empty string. Do not use an em-dash here either.

        Precision posture: prefer an empty findings list ([]) over weak or marginal findings. If the book is strong on this dimension, returning few or zero findings is the correct answer; do not invent weaknesses to fill a quota. Report only what you are confident about and what is supported by evidence from the briefs.
        """;

    // - Plot -
    private static readonly string BookReviewPlotHe = BuildBookReviewHe(
        "plot",
        "מבנה העלילה של הספר השלם: קשת הסיפור, סיבתיות, הסלמה, מתחים פתוחים וסגירתם, ומבנה ההתרה.",
        "האם כל פרק מקדם את העלילה? היכן יש קפיצות, חורים סיבתיים, חוטים עלילתיים שנפתחו ולא נסגרו, או הסלמה שטוחה?");

    private static readonly string BookReviewPlotEn = BuildBookReviewEn(
        "plot",
        "the plot architecture of the whole book: story arc, causality, escalation, open threads and their payoff, and the structure of the resolution.",
        "Does each chapter advance the plot? Where are there leaps, causal gaps, threads opened but never closed, or flat escalation?");

    // - Character -
    private static readonly string BookReviewCharacterHe = BuildBookReviewHe(
        "character",
        "הדמויות לאורך הספר השלם: קשתות התפתחות, מוטיבציה, עקביות אופי, ויחסים ביניהן.",
        "האם הדמויות המרכזיות מתפתחות לאורך הספר? היכן המוטיבציה לא ברורה, האופי לא עקבי, או דמות נעלמת בלי הסבר?");

    private static readonly string BookReviewCharacterEn = BuildBookReviewEn(
        "character",
        "the characters across the whole book: development arcs, motivation, consistency of characterisation, and relationships.",
        "Do the main characters develop across the book? Where is motivation unclear, characterisation inconsistent, or a character dropped without explanation?");

    // - Pacing -
    private static readonly string BookReviewPacingHe = BuildBookReviewHe(
        "pacing",
        "קצב הספר השלם: איזון בין סצנות מהירות לאיטיות, מקומות שנמתחים יתר על המידה או נדחסים מדי, וזרימת המתח לאורך הפרקים.",
        "היכן הספר נגרר או נדחס? אילו פרקים מאטים את התנופה, ואילו חולפים מהר מדי ביחס לחשיבותם?");

    private static readonly string BookReviewPacingEn = BuildBookReviewEn(
        "pacing",
        "the pacing of the whole book: balance between fast and slow scenes, places that drag or feel rushed, and the flow of tension across chapters.",
        "Where does the book drag or feel rushed? Which chapters slow the momentum, and which pass too quickly relative to their importance?");

    // - Tone / voice -
    private static readonly string BookReviewToneHe = BuildBookReviewHe(
        "tone",
        "הטון והקול של הספר השלם: עקביות האווירה, הקול הנרטיבי, והרישום הלשוני לאורך הפרקים.",
        "האם הטון עקבי לאורך הספר? היכן האווירה או הקול הנרטיבי קופצים בלי כוונה, או הרישום הלשוני משתנה באופן צורם?");

    private static readonly string BookReviewToneEn = BuildBookReviewEn(
        "tone",
        "the tone and voice of the whole book: consistency of atmosphere, narrative voice, and language register across chapters.",
        "Is the tone consistent across the book? Where does the atmosphere or narrative voice shift unintentionally, or the register change jarringly?");

    // - Theme -
    private static readonly string BookReviewThemeHe = BuildBookReviewHe(
        "theme",
        "הנושאים והמוטיבים של הספר השלם: אחדות תמטית, פיתוח הרעיונות המרכזיים, ועקביות המסר לאורך הפרקים.",
        "אילו נושאים מרכזיים נושא הספר? היכן הם מפותחים היטב, היכן הם נזנחים, ואיפה יש מסר סותר או מוטיב שלא ממומש?");

    private static readonly string BookReviewThemeEn = BuildBookReviewEn(
        "theme",
        "the themes and motifs of the whole book: thematic unity, development of the central ideas, and consistency of message across chapters.",
        "What central themes does the book carry? Where are they well developed, where are they dropped, and where is there a contradictory message or an unrealised motif?");

    // - Continuity -
    private static readonly string BookReviewContinuityHe = BuildBookReviewHe(
        "continuity",
        "הרציפות של הספר השלם: עקביות עובדות, ציר זמן, פרטי עולם ושמות, וסתירות בין פרקים.",
        "היכן יש סתירות עובדתיות בין פרקים, אי-התאמות בציר הזמן, שינויי שמות או פרטי עולם, או אירועים שמתייחסים למשהו שלא הוצג קודם?");

    private static readonly string BookReviewContinuityEn = BuildBookReviewEn(
        "continuity",
        "the continuity of the whole book: consistency of facts, timeline, world and name details, and contradictions between chapters.",
        "Where are there factual contradictions between chapters, timeline mismatches, changed names or world details, or events that reference something never introduced?");
}
