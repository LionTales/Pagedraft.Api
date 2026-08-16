using System.Text;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// The BOOK-LEVEL analysis templates: the structured chapter brief (plain and user-summary-seeded),
/// book overview, synopsis, character analysis, story analysis and chapter QA. A partial of
/// <see cref="PromptFactory"/>; see PromptFactory.AnalysisTemplates.cs for the byte-identity rule
/// that governs every string here.
/// </summary>
public partial class PromptFactory
{

    // ── Structured Chapter Brief (wb1-c01) ──────────────────────────
    // Parallels the flat Summarization prompt but asks for a machine-readable structured brief that
    // deserializes into StructuredChunkSummaryData (camelCase keys; no [JsonPropertyName] on that record,
    // so the property names must be the camelCase of its fields). Used by ChapterBriefService to populate
    // ChunkSummary.StructuredJson; the flat SummaryText prompt is kept for back-compat.

    /// <summary>
    /// Instruction asking the model to emit a STRUCTURED chapter brief as JSON matching
    /// <see cref="Pagedraft.Api.Models.StructuredChunkSummaryData"/>. Keep the flat
    /// <see cref="AiTaskType.Summarization"/> prompt for the natural-language summary; this is the
    /// parallel structured surface.
    /// </summary>
    public string GetStructuredChapterBriefPrompt(string language)
    {
        return language.StartsWith("he", StringComparison.OrdinalIgnoreCase)
            ? StructuredChapterBriefHe
            : StructuredChapterBriefEn;
    }

    /// <summary>
    /// wb3-c04: a STRUCTURED chapter-brief instruction SEEDED with the user's own edited flat summary as the
    /// AUTHORITATIVE understanding of the chapter. The chapter text is still supplied as <c>InputText</c> for
    /// detail, but the model is told to treat the user's summary as the source of truth for what the chapter
    /// is about — so the re-derived structured brief (and hence the whole-book review, which reads the
    /// structured brief) reflects the user's manual edit rather than re-deriving purely from the raw text.
    /// Returns the base structured-brief instruction with the user-summary block prepended. The summary is
    /// trimmed; a blank summary falls back to the plain structured prompt (no empty seed block).
    /// </summary>
    public string GetStructuredChapterBriefPromptSeededWithUserSummary(string language, string userSummary)
    {
        var basePrompt = GetStructuredChapterBriefPrompt(language);
        var trimmed = (userSummary ?? string.Empty).Trim();
        if (trimmed.Length == 0) return basePrompt;

        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        var seedBlock = isHe
            ? $"""
               להלן סיכום הפרק כפי שכתב אותו המחבר. זהו ההבנה הסמכותית של הפרק — התבסס עליו כמקור האמת לגבי
               עלילת הפרק, הדמויות והנושאים, והשתמש בטקסט הפרק רק להשלמת פרטים. אל תסתור את סיכום המחבר.

               סיכום המחבר:
               {trimmed}

               ---

               """
            : $"""
               The following is the chapter summary as written by the author. This is the AUTHORITATIVE
               understanding of the chapter — treat it as the source of truth for the chapter's plot,
               characters, and themes, and use the chapter text only to fill in supporting detail. Do not
               contradict the author's summary.

               Author's summary:
               {trimmed}

               ---

               """;

        return seedBlock + basePrompt;
    }

    private const string StructuredChapterBriefHe =
        """
        נתח את הפרק הבא והפק תקציר מובנה שלו. החזר אך ורק JSON תקין במבנה הבא, בלי טקסט נוסף לפני או אחרי:
        {
          "plotEvents": ["אירוע עלילתי מרכזי 1", "אירוע 2"],
          "characterStates": [
            { "name": "שם הדמות", "state": "מצבה בפרק", "emotionalArc": "הקשת הרגשית בפרק" }
          ],
          "thematicMarkers": ["מוטיב או נושא שמופיע בפרק"],
          "toneNotes": "תיאור קצר של הטון והאווירה של הפרק",
          "openThreads": ["שאלה או חוט עלילתי שנותר פתוח בסוף הפרק"]
        }

        השתמש אך ורק במידע שמופיע בפרק עצמו, אל תמציא פרטים. אם שדה אינו רלוונטי, החזר רשימה ריקה או מחרוזת ריקה. השב בעברית.
        """;

    private const string StructuredChapterBriefEn =
        """
        Analyze the following chapter and produce a structured brief of it. Return ONLY valid JSON in the exact shape below, with no text before or after:
        {
          "plotEvents": ["key plot event 1", "event 2"],
          "characterStates": [
            { "name": "character name", "state": "their situation in this chapter", "emotionalArc": "their emotional arc in this chapter" }
          ],
          "thematicMarkers": ["a motif or theme present in this chapter"],
          "toneNotes": "a short description of the chapter's tone and mood",
          "openThreads": ["a question or plot thread left open at the end of the chapter"]
        }

        Use only information present in the chapter itself; do not invent details. If a field is not applicable, return an empty list or empty string. Respond in English.
        """;

    // ── Book Overview ───────────────────────────────────────────────

    private const string BookOverviewHe =
        """
        אתה מומחה ספרותי. בהינתן סיכומי הפרקים הבאים של ספר, זהה:
        
        החזר את התוצאה בפורמט JSON:
        {
          "genre": "הז'אנר הראשי",
          "subGenre": "תת-ז'אנר (אם רלוונטי)",
          "targetAudience": "קהל היעד (למשל: מבוגרים, נוער, ילדים)",
          "literatureLevel": 7,
          "estimatedReadingTimeMinutes": 0,
          "languageRegister": "הרישום הלשוני (גבוה/בינוני/נמוך/משתנה)",
          "summary": "סיכום כולל בשני-שלושה משפטים על אופי הספר."
        }
        
        literatureLevel הוא בין 1 (פשוט מאוד) ל-10 (ספרות גבוהה). השתמש רק במידע שבסיכומים.
        """;

    private const string BookOverviewEn =
        """
        You are a literary expert. Given the following chapter summaries of a book, identify:
        
        Return the result in JSON format:
        {
          "genre": "primary genre",
          "subGenre": "sub-genre (if applicable)",
          "targetAudience": "target audience (e.g., adults, young adults, children)",
          "literatureLevel": 7,
          "estimatedReadingTimeMinutes": 0,
          "languageRegister": "language register (high/medium/low/varied)",
          "summary": "Overall summary in two to three sentences about the book's nature."
        }
        
        literatureLevel is between 1 (very simple) and 10 (high literature). Use only information from the summaries.
        """;

    // ── Synopsis ────────────────────────────────────────────────────

    private const string SynopsisHe =
        "בהינתן סיכומי הפרקים הבאים של ספר, כתוב תקציר מרתק בן 3-5 פסקאות. " +
        "התקציר צריך ללכוד את העלילה המרכזית, הדמויות העיקריות, והמוטיבציות שלהן, " +
        "מבלי לחשוף את הסיום (אלא אם כן הספר כולו מסוכם). כתוב בגוף שלישי, בסגנון מקצועי כמו של עורך ספרים.";

    private const string SynopsisEn =
        "Given the following chapter summaries of a book, write a compelling synopsis of 3-5 paragraphs. " +
        "The synopsis should capture the main plot, key characters, and their motivations, " +
        "without revealing the ending (unless the entire book is summarized). Write in third person, " +
        "in a professional style similar to a book editor.";

    // ── Character Analysis ──────────────────────────────────────────

    private const string CharacterAnalysisHe =
        """
        בהינתן סיכומי הפרקים הבאים של ספר, נתח את הדמויות.
        
        החזר את התוצאה בפורמט JSON:
        {
          "characters": [
            {
              "name": "שם הדמות",
              "role": "protagonist|antagonist|supporting|minor",
              "description": "תיאור קצר",
              "arc": "תיאור מסע/התפתחות הדמות",
              "firstAppearanceChapter": 1
            }
          ],
          "relationships": [
            {
              "character1": "שם דמות 1",
              "character2": "שם דמות 2",
              "relationship": "תיאור היחסים"
            }
          ],
          "summary": "סיכום כולל על מערך הדמויות והדינמיקה ביניהן."
        }
        
        מיין דמויות לפי חשיבות. אל תמציא דמויות שלא מופיעות בסיכומים.
        """;

    private const string CharacterAnalysisEn =
        """
        Given the following chapter summaries of a book, analyze the characters.
        
        Return the result in JSON format:
        {
          "characters": [
            {
              "name": "character name",
              "role": "protagonist|antagonist|supporting|minor",
              "description": "brief description",
              "arc": "description of the character's journey/development",
              "firstAppearanceChapter": 1
            }
          ],
          "relationships": [
            {
              "character1": "character name 1",
              "character2": "character name 2",
              "relationship": "description of their relationship"
            }
          ],
          "summary": "Overall summary of the cast and dynamics between characters."
        }
        
        Sort characters by importance. Do not invent characters not present in the summaries.
        """;

    // ── Story Analysis ──────────────────────────────────────────────

    private const string StoryAnalysisHe =
        """
        בהינתן סיכומי הפרקים הבאים של ספר, נתח את מבנה העלילה.
        
        החזר את התוצאה בפורמט JSON:
        {
          "plotStructure": {
            "setup": "הצגת המצב ההתחלתי והדמויות",
            "risingAction": "אירועי העלייה והסיבוכים",
            "climax": "שיא העלילה",
            "fallingAction": "אירועים לאחר השיא",
            "resolution": "הפתרון/הסיום"
          },
          "pacing": "תיאור קצב הסיפור — מהיר, איטי, משתנה, וכו'",
          "conflicts": [
            {
              "type": "internal|external|person-vs-person|person-vs-society|person-vs-nature|person-vs-self",
              "description": "תיאור הקונפליקט",
              "status": "resolved|unresolved|ongoing"
            }
          ],
          "summary": "סיכום כולל של מבנה הסיפור — חוזקות וחולשות."
        }
        
        אם הסיפור לא שלם, ציין זאת. אל תמלא חלקים שלא ניתן לזהות מהסיכומים.
        """;

    private const string StoryAnalysisEn =
        """
        Given the following chapter summaries of a book, analyze the story structure.
        
        Return the result in JSON format:
        {
          "plotStructure": {
            "setup": "introduction of the initial situation and characters",
            "risingAction": "escalating events and complications",
            "climax": "the story's climax",
            "fallingAction": "events after the climax",
            "resolution": "the resolution/ending"
          },
          "pacing": "description of story pacing — fast, slow, varied, etc.",
          "conflicts": [
            {
              "type": "internal|external|person-vs-person|person-vs-society|person-vs-nature|person-vs-self",
              "description": "description of the conflict",
              "status": "resolved|unresolved|ongoing"
            }
          ],
          "summary": "Overall assessment of story structure — strengths and weaknesses."
        }
        
        If the story is incomplete, note this. Do not fill in parts that cannot be identified from the summaries.
        """;

    // ── Q&A ─────────────────────────────────────────────────────────

    private const string QAHe =
        """
        אתה קורא מומחה של הספר הזה. בהינתן סיכומי הפרקים הבאים, ענה על שאלת המשתמש בדיוק.
        ציין מאילו פרקים המידע מגיע. אם התשובה לא נמצאת בסיכומים, אמור זאת בגלוי.
        
        החזר את התוצאה בפורמט JSON:
        {
          "answer": "התשובה המלאה לשאלה",
          "citations": [
            {
              "chapterNumber": 1,
              "chapterTitle": "כותרת הפרק",
              "relevantExcerpt": "משפט רלוונטי מהסיכום"
            }
          ],
          "confidence": "high|medium|low"
        }
        
        confidence צריך לשקף עד כמה הסיכומים מספקים תשובה מלאה.
        """;

    private const string QAEn =
        """
        You are an expert reader of this book. Given the following chapter summaries, answer the user's question accurately.
        Cite which chapter(s) the information comes from. If the answer is not in the summaries, say so honestly.
        
        Return the result in JSON format:
        {
          "answer": "the complete answer to the question",
          "citations": [
            {
              "chapterNumber": 1,
              "chapterTitle": "chapter title",
              "relevantExcerpt": "relevant sentence from the summary"
            }
          ],
          "confidence": "high|medium|low"
        }
        
        confidence should reflect how fully the summaries provide an answer.
        """;
}
