using System.Text;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Hebrew-focused, provider-agnostic prompt templates for both the correction pipeline and the unified
/// analysis system.
///
/// SPLIT ACROSS PARTIALS, ONE PUBLIC SEAM. The template families live in partner files
/// (PromptFactory.AnalysisTemplates.cs, PromptFactory.BookTemplates.cs, PromptFactory.BookReview.cs,
/// PromptFactory.BookReviewMapReduce.cs) as partials of THIS type, not as new classes: the composing
/// methods and the constants they read are mutually private, and every caller and every test keeps
/// calling PromptFactory. This file holds the composition layer - the system messages, the task/type
/// dispatchers, the two per-chunk builders and the context preamble with its Format* helpers.
///
/// EVERY PROMPT STRING IS A MEASURED ARTIFACT: see PromptFactoryByteIdentityPinTests, which pins the
/// composed bytes of every surface below.
/// </summary>
public partial class PromptFactory
{
    private readonly ProofreadPromptOptions _proofreadPrompt;

    /// <summary>
    /// The UNCONFIGURED factory: every <see cref="ProofreadPromptOptions"/> switch at its default (all
    /// OFF). This is the legacy shape - what <c>new PromptFactory()</c> composed before the options
    /// existed - and it is kept as a real constructor rather than deleted because the byte-identity of
    /// the off path is asserted AGAINST it (a test that compared the options path to itself would pass
    /// for free).
    /// </summary>
    public PromptFactory() : this(null) { }

    /// <param name="proofreadPrompt">
    /// The bound "Ai:ProofreadPrompt" block. NULL is a supported value and means "the shipped
    /// defaults", so the DI registration and a bare <c>new PromptFactory()</c> cannot diverge.
    /// </param>
    public PromptFactory(IOptions<ProofreadPromptOptions>? proofreadPrompt)
        => _proofreadPrompt = proofreadPrompt?.Value ?? new ProofreadPromptOptions();

    // ─── System messages ────────────────────────────────────────────

    private const string HebrewSystemBase =
        "אתה עורך לשוני ומגיה טקסטים בעברית. עליך לתקן שגיאות לשון, דקדוק, כתיב ופיסוק בטקסטים ספרותיים בעברית, תוך שמירה על הקול, הסגנון והכוונה של המחבר. השב תמיד בעברית בלבד.";

    // Appended to HebrewAnalysisSystem (the shared system message for LinguisticAnalysis, LiteraryAnalysis,
    // Summarization and BookReview — see AnalysisTaskMapping + GetPrompt) so every Hebrew analysis output is
    // steered away from the real CONTENT-value English leaks the diagnostic captured ("(Action)", "Tension",
    // "High Stakes"). p5-prompts of analysis-output-repair-2026-07-03.plan.md: an UPSTREAM reduction so the
    // deterministic glossary/guard pass fires less often. Kept SHORT (one line + a compact 8-term glossary
    // subset of LiteraryTermGlossary) to avoid prompt bloat / recall regression. NO em-dash (the model echoes
    // punctuation from its system frame).
    private const string HebrewNoEnglishTermsClause =
        " כתוב את כל הפלט בעברית בלבד. אל תשבץ מילים או מונחים באנגלית, לא בסוגריים ולא בכל צורה אחרת; אם דרוש מונח ספרותי או לשוני, השתמש במקבילה העברית שלו. מקבילות מקובלות: narrator=מספר, tone=טון, mood=מצב רוח, foreshadowing=רמיזה מקדימה, imagery=דימויים, tension=מתח, climax=שיא, action=פעולה.";

    private const string HebrewAnalysisSystem =
        "אתה מומחה לניתוח ספרותי ולשוני של טקסטים בעברית. אתה מנתח כתיבה ספרותית, פרוזה ופואטיקה. השב תמיד בעברית בלבד, בסגנון מקצועי ותמציתי." + HebrewNoEnglishTermsClause;

    private const string HebrewBookSystem =
        "אתה מומחה ספרותי המנתח ספרים שלמים. אתה מסוגל לזהות ז'אנרים, דמויות, מבנה עלילתי, ולספק תובנות מעמיקות על יצירה ספרותית. השב תמיד בעברית בלבד.";

    // Neutral assistant system for free-form Custom prompts and QA (AiTaskType.GenericChat) plus Translation.
    // These tasks must NOT reuse HebrewSystemBase: that is a PROOFREADER system ("correct errors, return only
    // the corrected text") and it sabotages a free-form question - the model proofreads the chapter instead of
    // answering, returning a near-empty fragment. A general literary-assistant framing lets the user's
    // instruction drive the response.
    private const string HebrewAssistantSystem =
        "אתה עוזר ספרותי. בצע את ההנחיה שהמשתמש נותן לגבי הטקסט הנתון - ענה על שאלות, סכם או נתח לפי הבקשה, בהתבסס על תוכן הטקסט. השב תמיד בעברית בלבד.";

    private const string EnglishAssistantSystem =
        "You are a literary assistant. Follow the user's instruction about the given text - answer questions, summarize, or analyze as requested, based on the text content. Respond only in the same language as the input.";

    private const string EnglishProofreadSystem =
        "You are an editor and proofreader. Correct spelling, grammar, and punctuation in the given text while preserving the author's voice and style. Respond only in the same language as the input.";

    private const string EnglishAnalysisSystem =
        "You are an expert literary and linguistic analyst. You analyze prose, poetry, and creative writing with depth and precision. Respond in a professional, concise style. Respond in English only; do not insert non-English terms parenthetically.";

    private const string EnglishBookSystem =
        "You are a literary expert who analyzes complete books. You can identify genres, characters, plot structure, and provide deep insights about literary works. Respond in a professional, concise style.";

    private const string HebrewLineEditSystem =
        "אתה עורך ספרותי מומחה. תפקידך לזהות הזדמנויות לשיפור סגנון, בהירות וזרימה בטקסט ספרותי, תוך שמירה על קול המחבר. השב בעברית בפורמט JSON בלבד.";

    private const string EnglishLineEditSystem =
        "You are an expert literary editor. Your role is to identify opportunities for improving style, clarity, and flow in literary text while preserving the author's voice. Always respond in JSON format only.";

    // ─── Pipeline prompts (legacy AiTaskType) ───────────────────────

    /// <summary>Returns (systemMessage, instruction) for the correction pipeline.</summary>
    public (string SystemMessage, string Instruction) GetPrompt(AiTaskType taskType, string language)
    {
        var isHebrew = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);

        if (taskType == AiTaskType.Proofread)
        {
            var system = isHebrew ? HebrewSystemBase : EnglishProofreadSystem;
            var instruction = isHebrew
                ? "קבל קטע טקסט בעברית. תקן כל שגיאת כתיב, דקדוק, ניקוד או פיסוק שאתה מזהה. אל תחליף מילה במילה נרדפת ואל תשנה את המשמעות — תקן את שגיאת הכתיב במילה עצמה (למשל \"עתון\" ל\"עיתון\", לא ל\"עיתונות\") ואל תהפוך צירוף תקין למבע אחר (למשל אל תשנה \"עצמה רגשית\" ל\"עוצמת רגשות\"). אל תחליף מילה במילה הומוגרפית בעלת משמעות שונה כשהמילה תקינה בהקשרה (למשל \"עצמה\" כינוי גוף חוזר אינה \"עוצמה\"). אל תהפוך פועל מסביל לפעיל או מפעיל לסביל כשצורת המקור תקינה (למשל אל תשנה \"הושקעה\" ל\"השקיעה\"). אל תתקן ערבוב רישומים מכוון (למשל שפה מדוברת בדיאלוג). אם אין שגיאות, החזר את הטקסט כפי שהוא. החזר **רק** את הגרסה המתוקנת (או המקורית אם אין שינויים), בלי הסברים או תוספות. אל תשנה את מבנה הפסקאות אלא אם יש טעות ברורה. אל תוסיף תוכן חדש."
                : "Receive a text and return **only** the corrected version, with no explanations or additions. Do not replace a word with a synonym and do not change the meaning — fix the spelling of the word itself and keep the same word and meaning; do not turn a correct phrase into a different expression. Preserve intentional shifts of register (for example colloquial speech in dialogue). Do not change paragraph structure unless there is a clear error. Do not add new content.";
            return (system, instruction);
        }

        if (taskType == AiTaskType.LineEdit)
        {
            var system = isHebrew ? HebrewLineEditSystem : EnglishLineEditSystem;
            var instruction = isHebrew ? LineEditHe : LineEditEn;
            return (system, instruction);
        }

        if (taskType == AiTaskType.LinguisticAnalysis)
        {
            var system = isHebrew ? HebrewAnalysisSystem : EnglishAnalysisSystem;
            var instruction = isHebrew
                ? "נתח את הטקסט מבחינה לשונית: תחביר, בחירת מילים, רישום, זרימה, ועקביות. ציין נקודות לשיפור בצורה תמציתית. השב בעברית ברורה, עם כותרות קצרות ורשימות ממוספרות במידת הצורך."
                : "Analyze the text linguistically: syntax, word choice, register, flow, and consistency. Note improvement points concisely. Respond with clear structure, short headings and numbered lists as needed.";
            return (system, instruction);
        }

        if (taskType == AiTaskType.Summarization)
        {
            var system = isHebrew ? HebrewAnalysisSystem : EnglishAnalysisSystem;
            var instruction = isHebrew
                ? "סכם את הטקסט בעברית, עד שלושה פסקאות קצרות, בלי להוסיף מידע שלא מופיע במקור."
                : "Summarize the text in up to three short paragraphs, without adding information not in the source.";
            return (system, instruction);
        }

        if (taskType == AiTaskType.Translation || taskType == AiTaskType.GenericChat)
        {
            var system = isHebrew ? HebrewAssistantSystem : EnglishAssistantSystem;
            var instruction = isHebrew ? "השב בעברית בלבד לפי ההנחיות שניתנו." : "Respond according to the instructions given.";
            return (system, instruction);
        }

        if (taskType == AiTaskType.ProductChat)
        {
            // Grounded product Q&A over the shipped guides (chatbot phase A, c1). The system message is
            // OWNED by ProductChatPrompt so the grounding wording has exactly one home; this arm exists
            // so the router resolves it rather than falling through to the Hebrew proofreader default at
            // the bottom of this method, which would sabotage the answer the way HebrewSystemBase
            // sabotages any free-form question. The instruction is supplied whole by ProductChatService
            // (grounding rule + selected guides + capped history) and the router sends it verbatim, so
            // there is no pipeline instruction to append here.
            return (Services.Chat.ProductChatPrompt.SystemMessage(isHebrew ? "he" : "en"), "");
        }

        if (taskType == AiTaskType.BookReview)
        {
            // Whole-book developmental review. The complete, structured-JSON instruction is supplied
            // verbatim by BookReviewService (BuildBookReviewPrompt / BuildBookReviewCombinedPrompt), and
            // the router sends it without appending a pipeline instruction, so we return an empty one here.
            // The system message must follow the book language, not default to Hebrew.
            var system = isHebrew ? HebrewAnalysisSystem : EnglishAnalysisSystem;
            return (system, "");
        }

        return (HebrewSystemBase, "השב בעברית בלבד.");
    }

    // ─── Unified Analysis prompts (AnalysisType) ────────────────────

    /// <summary>Returns a complete instruction for the given AnalysisType and language.
    /// The system message is resolved by the router via AiTaskType mapping.</summary>
    public string GetAnalysisPrompt(AnalysisType analysisType, string language)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        return analysisType switch
        {
            AnalysisType.Proofread      => isHe ? ProofreadHe : ProofreadEn,
            AnalysisType.LineEdit       => isHe ? LineEditHe : LineEditEn,
            AnalysisType.LinguisticAnalysis => isHe ? LinguisticHe : LinguisticEn,
            AnalysisType.LiteraryAnalysis   => isHe ? LiteraryHe : LiteraryEn,
            AnalysisType.Summarization  => isHe ? SummarizationHe : SummarizationEn,
            AnalysisType.BookOverview   => isHe ? BookOverviewHe : BookOverviewEn,
            AnalysisType.Synopsis       => isHe ? SynopsisHe : SynopsisEn,
            AnalysisType.CharacterAnalysis => isHe ? CharacterAnalysisHe : CharacterAnalysisEn,
            AnalysisType.StoryAnalysis  => isHe ? StoryAnalysisHe : StoryAnalysisEn,
            // QA routes to AiTaskType.GenericChat, whose system message (HebrewAssistantSystem) is SHARED with
            // Translation + Custom and therefore intentionally lacks HebrewNoEnglishTermsClause (Translation/Custom
            // legitimately emit other languages). QA on a Hebrew book, however, should stay Hebrew-only and avoid
            // parenthetical English term leaks like the analysis frame does. Append the clause to the QA INSTRUCTION
            // (not the shared GenericChat system) so the Hebrew-only steer applies to Hebrew QA alone. f1-prompt-coverage.
            // HebrewNoEnglishTermsClause targets PROSE VALUES only: the QAHe JSON keys (answer/citations/chapterNumber/
            // chapterTitle/relevantExcerpt/confidence) and the confidence enum (high|medium|low) legitimately stay
            // English and must not be Hebraised, so do not "tighten" this clause into forcing a Hebrew confidence value.
            AnalysisType.QA             => isHe ? QAHe + HebrewNoEnglishTermsClause : QAEn,
            AnalysisType.Custom         => isHe ? "השב בעברית בלבד לפי ההנחיות שניתנו." : "Respond according to the instructions given.",
            _ => isHe ? "השב בעברית בלבד." : "Respond in English."
        };
    }

    // ─── Context-aware analysis prompt ──────────────────────────────

    /// <summary>
    /// Returns a context-enriched instruction for the given analysis type.
    /// When context is null or has no relevant optional fields, falls back to the base prompt.
    /// Context sections use [SECTION_NAME]...[/SECTION_NAME] delimiters so the LLM can
    /// distinguish injected context from the analysis instruction itself.
    /// </summary>
    public string GetAnalysisPrompt(AnalysisType analysisType, string language, AnalysisContext? context)
    {
        var basePrompt = GetAnalysisPrompt(analysisType, language);
        if (context == null)
            return basePrompt;

        var preamble = BuildContextPreamble(context, analysisType);
        if (string.IsNullOrEmpty(preamble))
            return basePrompt;

        return preamble + basePrompt;
    }

    // ─── Per-chunk proofread prompt assembly ────────────────────────

    /// <summary>
    /// Builds a complete per-chunk proofread instruction that prepends character register
    /// and overlap context (if available) to the base proofread prompt.
    /// Used by RunProofreadChunkedAsync; the caller wraps InputText with
    /// [TEXT_TO_CORRECT]...[/TEXT_TO_CORRECT] markers.
    /// </summary>
    public string BuildProofreadChunkPrompt(string language, CharacterRegister? characters, string? overlapPrefix)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        var basePrompt = ProofreadChunkBody(isHe);

        var sb = new StringBuilder();

        // NOT a second copy of the register-reading type set: this method has no type switch, because it
        // is Proofread-by-construction (its only callers are inside RunProofreadChunkedAsync). What binds
        // it to <see cref="RendersCharacterRegister"/> is the ARGUMENT: every caller passes
        // AnalysisContext.Characters, which AnalysisContextService populates only when that predicate says
        // Proofread reads the register. Drop ContextField.Characters from the Proofread row and this
        // section stops rendering here too, with no edit to this file.
        if (characters is { Characters.Count: > 0 })
            AppendSection(sb, "CHARACTER_REGISTER", FormatCharacters(characters));

        if (!string.IsNullOrWhiteSpace(overlapPrefix))
            AppendSection(sb, "CONTEXT_BEFORE", overlapPrefix.Trim());

        sb.Append(basePrompt);
        return sb.ToString();
    }

    // ─── Per-chunk LineEdit prompt assembly ─────────────────────────

    /// <summary>
    /// Builds a complete per-chunk LineEdit instruction that injects style profile and
    /// context (global or local overlap) before the base LineEdit prompt, with a
    /// reinforcement that edits must target only [TEXT_TO_EDIT] content.
    /// Used by RunLineEditChunkedAsync; the caller wraps chunk text with
    /// [TEXT_TO_EDIT]...[/TEXT_TO_EDIT] markers in the InputText.
    /// </summary>
    public string BuildLineEditChunkPrompt(
        string language,
        AnalysisContext context,
        string? localOverlapBefore,
        string? localOverlapAfter,
        bool isFirstChunk,
        bool isLastChunk)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        var basePrompt = isHe ? LineEditHe : LineEditEn;

        var sb = new StringBuilder();

        if (context.StyleProfile is { } style)
            AppendSection(sb, "STYLE_PROFILE", FormatStyleProfile(style, forSuggestions: true));

        var precedingText = isFirstChunk ? context.PrecedingContext : localOverlapBefore;
        if (!string.IsNullOrWhiteSpace(precedingText))
            AppendSection(sb, "PRECEDING_CONTEXT", precedingText.Trim());

        var followingText = isLastChunk ? context.FollowingContext : localOverlapAfter;
        if (!string.IsNullOrWhiteSpace(followingText))
            AppendSection(sb, "FOLLOWING_CONTEXT", followingText.Trim());

        sb.Append(basePrompt);

        sb.AppendLine();
        sb.AppendLine();
        sb.Append(isHe
            ? "הטקסט לעריכה מסומן ב-[TEXT_TO_EDIT]...[/TEXT_TO_EDIT]. הצע שינויים רק לטקסט שבתוך הסימון. החזר אך ורק JSON — ללא גדרות markdown, ללא טקסט נוסף."
            : "The text to edit is in [TEXT_TO_EDIT]...[/TEXT_TO_EDIT]. Only suggest edits for text inside those markers. Return ONLY JSON — no markdown fences, no extra text.");

        return sb.ToString();
    }

    // ─── Context preamble builder ───────────────────────────────────

    [Flags]
    private enum ContextField
    {
        None             = 0,
        StyleProfile     = 1 << 0,
        Characters       = 1 << 1,
        ChapterBrief     = 1 << 2,
        BookBrief        = 1 << 3,
        PrecedingContext  = 1 << 4,
        FollowingContext  = 1 << 5,
        ChapterStyleBaseline = 1 << 6,
        BookStyleAverages    = 1 << 7,
    }

    /// <summary>Which optional context fields are relevant for each analysis type.</summary>
    private static ContextField GetRelevantFields(AnalysisType type) => type switch
    {
        AnalysisType.Proofread          => ContextField.StyleProfile | ContextField.PrecedingContext | ContextField.Characters,
        AnalysisType.LineEdit           => ContextField.StyleProfile | ContextField.PrecedingContext | ContextField.FollowingContext,
        AnalysisType.LinguisticAnalysis => ContextField.StyleProfile | ContextField.ChapterStyleBaseline | ContextField.PrecedingContext | ContextField.FollowingContext,
        AnalysisType.LiteraryAnalysis   => ContextField.StyleProfile | ContextField.Characters | ContextField.ChapterBrief | ContextField.BookBrief,
        AnalysisType.Summarization      => ContextField.ChapterBrief | ContextField.PrecedingContext,
        AnalysisType.QA                 => ContextField.BookBrief | ContextField.Characters,
        AnalysisType.StoryAnalysis      => ContextField.BookBrief,
        AnalysisType.Synopsis           => ContextField.Characters,
        // BookOverview, CharacterAnalysis, Custom — no extra context needed.
        //
        // BookReview is here too, and its absence is NOT the statement it used to be: since be-c02 the
        // whole-book review DOES receive the character register, just not through this table. It never
        // builds an AnalysisContext at all - BookReviewService reads the register column itself and
        // BookContextAssembler renders it as a [BOOK_CHARACTERS] block into every window and the
        // synthesis. Adding ContextField.Characters here would load and possibly EXTRACT a register the
        // review path never reads, which is exactly the "loading without rendering" cost the doc on
        // RendersCharacterRegister argues against. Do not add a row for it.
        _ => ContextField.None,
    };

    /// <summary>
    /// THE single source of truth for "does this analysis type use the character register", DERIVED from
    /// the table above rather than restating it. Every gate on that question resolves to the SAME TABLE
    /// ROW, so adding <see cref="ContextField.Characters"/> to a row above moves all three at once and
    /// there is no list to keep in lockstep any more:
    /// <list type="bullet">
    /// <item>the RENDER gate, <see cref="BuildContextPreamble"/>, reads
    /// <c>GetRelevantFields(type).HasFlag(ContextField.Characters)</c> INLINE alongside every other
    /// field it renders, rather than calling this method - it is one flag test in a run of them, and
    /// singling this one out would read as though it were a different question. It is the same
    /// expression this method is defined as;</item>
    /// <item>the LOAD gate in <c>AnalysisContextService.BuildContextAsync</c> calls this method;</item>
    /// <item><c>AnalysisController.ReadsCharacterRegister</c> (which decides whether a result may be
    /// flagged stale against the register stamp) delegates to this method.</item>
    /// </list>
    /// The oracle in <c>CharacterRegisterReadingTypeSetTests</c> checks the three OBSERVABLES, not the
    /// predicate, so the render gate's inline form is pinned to the other two by behaviour.
    /// <para>
    /// WHY THE RENDER GATE IS THE AUTHORITY, and why the load gate has no freedom to differ (c04, argued
    /// from the call graph as it is TODAY, not from taste): <c>AnalysisContext.Characters</c> has exactly
    /// two consumers, <see cref="BuildContextPreamble"/> and
    /// <see cref="BuildProofreadChunkPrompt"/> via <c>UnifiedAnalysisService.RunProofreadChunkedAsync</c>,
    /// both prompt assembly. Nothing loads the register for any other purpose. Rendering without loading is
    /// silently inert (the section condition also requires a non-empty register, so the declaration is
    /// simply dead); loading without rendering costs an LLM extraction pre-pass AND a BookBible write per
    /// analysis for a value the model never sees, and makes the staleness flag a false signal. If a future
    /// consumer genuinely needs the register loaded WITHOUT rendering it, it must state its own need
    /// explicitly rather than widen this predicate; the mechanical oracle in
    /// <c>CharacterRegisterReadingTypeSetTests</c> will make that divergence visible.
    /// </para>
    /// </summary>
    internal static bool RendersCharacterRegister(AnalysisType type) =>
        GetRelevantFields(type).HasFlag(ContextField.Characters);

    private static string BuildContextPreamble(AnalysisContext ctx, AnalysisType type)
    {
        var fields = GetRelevantFields(type);
        if (fields == ContextField.None)
            return string.Empty;

        var sb = new StringBuilder();

        if (fields.HasFlag(ContextField.StyleProfile) && ctx.StyleProfile is { } style)
            AppendSection(sb, "STYLE_PROFILE", FormatStyleProfile(style, forSuggestions: type == AnalysisType.LineEdit));

        // LinguisticAnalysis style-deviation context: render the chapter's own metric baseline so the
        // model can compute `deviations` / `consistencyIssues`. Optional — when the source value is
        // null (or unparseable), AppendSection skips empty content so no markers are emitted (graceful
        // degradation).
        // NOTE: a genuine numeric BOOK_STYLE_AVERAGES section (mean of per-chapter ChapterStyleProfile
        // metrics) plus a book-comparison output field is deferred to Plan 5. The old wiring injected
        // the qualitative book StyleProfile (already rendered as [STYLE_PROFILE]) under this marker,
        // duplicating content, so it has been removed rather than left misleading.
        if (fields.HasFlag(ContextField.ChapterStyleBaseline) && ctx.ChapterStyleBaseline is { } chapterBaseline)
            AppendSection(sb, "CHAPTER_STYLE_BASELINE", FormatChapterStyleBaseline(chapterBaseline, ctx.Scope));

        if (fields.HasFlag(ContextField.Characters) && ctx.Characters is { Characters.Count: > 0 } chars)
            AppendSection(sb, "CHARACTER_REGISTER", FormatCharacters(chars));

        if (fields.HasFlag(ContextField.BookBrief) && ctx.BookBrief is { } book)
            AppendSection(sb, "BOOK_CONTEXT", FormatBookBrief(book));

        if (fields.HasFlag(ContextField.ChapterBrief) && ctx.ChapterBrief is { } chapter)
            AppendSection(sb, "CHAPTER_CONTEXT", FormatChapterBrief(chapter));

        if (fields.HasFlag(ContextField.PrecedingContext) && !string.IsNullOrWhiteSpace(ctx.PrecedingContext))
            AppendSection(sb, "PRECEDING_CONTEXT", ctx.PrecedingContext.Trim());

        if (fields.HasFlag(ContextField.FollowingContext) && !string.IsNullOrWhiteSpace(ctx.FollowingContext))
            AppendSection(sb, "FOLLOWING_CONTEXT", ctx.FollowingContext.Trim());

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string name, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        sb.Append('[').Append(name).Append("]\n");
        sb.Append(content.Trim());
        sb.Append("\n[/").Append(name).Append("]\n\n");
    }

    /// <param name="forSuggestions">When true, includes imperative instructions for LineEdit (flag as consistency, avoid suggesting, etc.). When false, descriptive only for Proofread and analysis types (Linguistic/Literary).</param>
    private static string FormatStyleProfile(StyleProfileData s, bool forSuggestions = true)
    {
        var sb = new StringBuilder();

        if (s.DominantTone != null)
            sb.AppendLine(forSuggestions
                ? $"The author's dominant tone is {s.DominantTone}. Flag passages where a different tone creeps in as 'consistency'."
                : $"The author's dominant tone is {s.DominantTone}.");

        if (s.Pov != null)
            sb.AppendLine(forSuggestions
                ? $"The narrative uses {s.Pov} POV. Flag unintentional POV shifts as 'consistency' issues."
                : $"The narrative uses {s.Pov} POV.");

        if (s.TensePattern != null)
            sb.AppendLine(forSuggestions
                ? $"The narrative tense is {s.TensePattern}. Flag unintentional tense shifts as 'consistency'."
                : $"The narrative tense is {s.TensePattern}.");

        if (s.VocabularyLevel != null)
            sb.AppendLine(forSuggestions
                ? $"Vocabulary level is {s.VocabularyLevel}. Avoid suggesting words outside this register."
                : $"Vocabulary level is {s.VocabularyLevel}.");

        if (s.DialogueStyle != null)
            sb.AppendLine(forSuggestions
                ? $"Dialogue style is {s.DialogueStyle}. Preserve it in any dialogue suggestions."
                : $"Dialogue style is {s.DialogueStyle}.");

        if (s.RecurringMotifs is { Count: > 0 })
            sb.AppendLine(forSuggestions
                ? $"Recurring motifs: {string.Join(", ", s.RecurringMotifs)}. Do not suggest removing these."
                : $"Recurring motifs: {string.Join(", ", s.RecurringMotifs)}.");

        if (s.AverageSentenceLength.HasValue)
            sb.AppendLine(forSuggestions
                ? $"Average sentence length is ~{s.AverageSentenceLength:F0} words. Keep suggestions near this rhythm."
                : $"Average sentence length is ~{s.AverageSentenceLength:F0} words.");

        if (s.FormalityScore.HasValue)
            sb.AppendLine(forSuggestions
                ? $"Formality score: {s.FormalityScore:F2} (0 = very informal, 1 = very formal). Match this level in suggestions."
                : $"Formality score: {s.FormalityScore:F2} (0 = very informal, 1 = very formal).");

        return sb.ToString();
    }

    // Options for reading ChapterStyleProfile.MetricsJson, which is a serialized
    // LinguisticAnalysisResult (camelCase via [JsonPropertyName]); case-insensitive to be lenient.
    private static readonly System.Text.Json.JsonSerializerOptions MetricsReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Renders the cached style baseline (LinguisticAnalysisResult-shaped MetricsJson) as plain
    /// "metric: value" lines so the model can numerically compare the analyzed unit's metrics against
    /// the reference and emit `deviations`. The header is scope-aware so the model frames its free-text
    /// notes correctly: at Scene scope the reference is the CHAPTER and the analyzed unit is the scene;
    /// at Chapter (or Book) scope the analyzed unit is the WHOLE CHAPTER and the injected baseline is the
    /// BOOK AVERAGE (per the Style Baseline feature), so the note must talk about the book, not the chapter.
    /// This mirrors the FE reference logic (Scene -> chapter, Chapter/Book -> book). The section header is
    /// English (like every other context marker/section here — the model reads English context fine and
    /// emits notes in the request language). Returns empty (so the section is omitted) when MetricsJson is
    /// missing or cannot be parsed.
    /// </summary>
    private static string FormatChapterStyleBaseline(ChapterStyleProfile profile, AnalysisScope scope)
    {
        if (string.IsNullOrWhiteSpace(profile.MetricsJson))
            return string.Empty;

        Pagedraft.Api.Services.Analysis.LinguisticAnalysisResult? metrics;
        try
        {
            metrics = System.Text.Json.JsonSerializer
                .Deserialize<Pagedraft.Api.Services.Analysis.LinguisticAnalysisResult>(profile.MetricsJson, MetricsReadOpts);
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }

        if (metrics == null)
            return string.Empty;

        var sb = new StringBuilder();
        // Scene scope: the analyzed unit is the SCENE, the reference is the CHAPTER.
        // Chapter (or Book) scope: the analyzed unit is the WHOLE CHAPTER, the reference is the BOOK AVERAGE.
        sb.AppendLine(scope == AnalysisScope.Scene
            ? "Chapter-wide baseline metrics. Compare the current SCENE against these CHAPTER numbers; in each deviation note describe the divergence from the CHAPTER's usual style."
            : "Book-wide AVERAGE style metrics. Compare the WHOLE CHAPTER below against these BOOK-average numbers; in each deviation note describe the divergence from the BOOK's typical style. Do NOT call the analyzed unit a 'scene' or the reference 'the chapter'.");

        var syntax = metrics.SyntaxMetrics;
        sb.AppendLine($"- sentenceCount: {syntax.SentenceCount}");
        sb.AppendLine($"- averageSentenceLength: {syntax.AverageSentenceLength:F1} words");
        sb.AppendLine($"- complexSentences: {syntax.ComplexSentences}");
        sb.AppendLine($"- shortestSentence: {syntax.ShortestSentence} words");
        sb.AppendLine($"- longestSentence: {syntax.LongestSentence} words");

        var morph = metrics.MorphologyMetrics;
        sb.AppendLine($"- wordCount: {morph.WordCount}");
        sb.AppendLine($"- uniqueWords: {morph.UniqueWords}");
        sb.AppendLine($"- averageWordLength: {morph.AverageWordLength:F2}");
        sb.AppendLine($"- lexicalDensity: {morph.LexicalDensity:F2}");

        var st = metrics.StyleMetrics;
        sb.AppendLine($"- formality: {st.Formality}");
        sb.AppendLine($"- readability: {st.Readability:F1}");
        sb.AppendLine($"- voiceBalance: {st.VoiceBalance}");

        sb.AppendLine($"- grammaticalityScore: {metrics.GrammaticalityScore:F2}");

        return sb.ToString();
    }

    private static string FormatCharacters(CharacterRegister reg)
    {
        var sb = new StringBuilder();
        foreach (var c in reg.Characters)
        {
            sb.Append($"- {c.Name}");
            if (c.Role != null) sb.Append($" ({c.Role})");
            if (c.Gender != null) sb.Append($" [{c.Gender}]");
            if (c.Description != null) sb.Append($": {c.Description}");
            if (c.Aliases.Count > 0) sb.Append($" (aliases: {string.Join(", ", c.Aliases)})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The maximum number of characters the WHOLE-BOOK REVIEW block renders (automatic-coverage plan, d1 §4).
    ///
    /// <para>It exists because the block is repeated in EVERY window and charged to every window's token
    /// budget, so an unbounded ensemble cast would shrink every window until the review's real payload (the
    /// chapters) stopped fitting. The budget is the hazard: over-packing a window overflows num_ctx, Ollama
    /// silently truncates past it, and the resulting empty window has twice been counted as reviewed in this
    /// subsystem.</para>
    ///
    /// <para>MEASURED, AND THE MEASUREMENT MOVED IT (be-c02). The plan proposed 40 as a starting bound and
    /// asked for it to be verified on a large-cast fixture. It was: the SHIPPED per-window budget is 8192
    /// tokens (Ollama_BookReview NumCtx 16384, minus NumPredict 6144, the 1536-token prompt reserve and the
    /// 512-token safety margin), and 40 long Hebrew entries with aliases render at <b>1562 tokens on the dense
    /// 2-chars-per-token estimate Hebrew books use - 19% of EVERY window</b>. Nineteen percent of every window
    /// spent on the cast, repeated across every window of the book, buys minor characters at the price of the
    /// chapters the review is actually reading. At <b>24</b> the same worst-case fixture costs about 940 tokens
    /// (~11%), and a typical register (shorter names, one alias) about 550 (~7%). 24 comfortably covers any
    /// book's principal cast, and <see cref="RolePriority"/> means what a larger cast loses is its MINOR
    /// characters, not an arbitrary tail. Pinned by a test so a future widening is a deliberate act with a
    /// visible cost.</para>
    /// </summary>
    private const int MaxBookReviewCharacters = 24;

    /// <summary>
    /// Aliases rendered per character in the whole-book review block. The entry COUNT is not the only unbounded
    /// axis: <see cref="CharacterRegisterEntry.Aliases"/> is a list an extraction (or an author) can grow
    /// without limit, so a cast well inside <see cref="MaxBookReviewCharacters"/> could still blow the block up
    /// through one entry with thirty surface forms. Three is enough for the block's actual job - letting the
    /// model recognise that a name it meets in the text is a character it already knows.
    /// </summary>
    private const int MaxBookReviewAliasesPerCharacter = 3;

    /// <summary>
    /// Renders the character register for the WHOLE-BOOK REVIEW (automatic-coverage plan, d1 §3/§4). This is a
    /// SEPARATE surface from <see cref="FormatCharacters"/> and must stay separate: that method's output is a
    /// standing measurement subject for the chunked-agreement work, so it is byte-identical by construction
    /// here - the review never calls it, and this method has no caller on the analysis-context path.
    ///
    /// <para>Differences from <see cref="FormatCharacters"/>, each deliberate:</para>
    /// <list type="bullet">
    /// <item>DROPS <c>Description</c>. The review needs IDENTITY (who exists, what to call them, how to
    /// recognise an alias), not the biographical blurb, and Description is typically the longest field - so
    /// dropping it is the cheapest lever against the per-window token cost before resorting to dropping
    /// characters outright.</item>
    /// <item>CAPS at <see cref="MaxBookReviewCharacters"/>, ordered by <see cref="RolePriority"/>
    /// (protagonist, antagonist, supporting, then minor/unlabelled), ties broken by the register's existing
    /// order because <c>OrderBy</c> is a stable sort. A book whose roles are all null or non-English renders
    /// in register order, which is what it did before any ordering existed.</item>
    /// <item>CAPS the per-entry alias list at <see cref="MaxBookReviewAliasesPerCharacter"/>, the register's
    /// OTHER unbounded axis.</item>
    /// <item>Renders no provenance flags - same as <see cref="FormatCharacters"/>, which has never rendered
    /// them either. Provenance decides what the register HOLDS, not what a model is told.</item>
    /// </list>
    ///
    /// <para>The caller passes a register that is ALREADY suppression-filtered
    /// (<c>CharacterRegisterMerge.ForAnalysis</c>), so this does not re-check <c>IsCharacter</c>. It is wrapped
    /// in the distinct <c>[BOOK_CHARACTERS]</c> marker (not <c>[CHARACTER_REGISTER]</c>) by
    /// <c>BookContextAssembler.FormatCharacterRegisterBlock</c>, so the two surfaces can never be confused by
    /// tag alone in a captured prompt.</para>
    /// </summary>
    internal static string FormatCharactersForBookReview(CharacterRegister reg)
    {
        var sb = new StringBuilder();
        var ordered = reg.Characters
            .OrderBy(RolePriority)
            .Take(MaxBookReviewCharacters);
        foreach (var c in ordered)
        {
            sb.Append($"- {c.Name}");
            if (c.Role != null) sb.Append($" ({c.Role})");
            if (c.Gender != null) sb.Append($" [{c.Gender}]");
            if (c.Aliases.Count > 0)
                sb.Append($" (aliases: {string.Join(", ", c.Aliases.Take(MaxBookReviewAliasesPerCharacter))})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sort key for <see cref="FormatCharactersForBookReview"/>'s top-N cut: the roles a whole-book review
    /// most needs named come first, so a cast larger than the cap loses its MINOR characters rather than an
    /// arbitrary tail. <c>Role</c> is an extracted-only free-text field, so anything unrecognised (a Hebrew
    /// role string, a null) shares the last rung with "minor" and keeps its register order.
    /// </summary>
    private static int RolePriority(CharacterRegisterEntry c) =>
        (c.Role ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "protagonist" => 0,
            "antagonist" => 1,
            "supporting" => 2,
            _ => 3,
        };

    private static string FormatBookBrief(BookBrief b)
    {
        var sb = new StringBuilder();
        if (b.Genre != null) sb.AppendLine($"Genre: {b.Genre}{(b.SubGenre != null ? $" / {b.SubGenre}" : "")}");
        if (b.TargetAudience != null) sb.AppendLine($"Audience: {b.TargetAudience}");
        if (b.LiteratureLevel.HasValue) sb.AppendLine($"Literature level: {b.LiteratureLevel}/10");
        if (b.Themes.Count > 0) sb.AppendLine($"Themes: {string.Join(", ", b.Themes)}");
        if (b.Synopsis != null) sb.AppendLine($"Synopsis: {b.Synopsis}");
        return sb.ToString();
    }

    private static string FormatChapterBrief(ChapterBrief ch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Chapter {ch.Order}: {ch.Title}");
        if (ch.Summary != null) sb.AppendLine(ch.Summary);
        if (ch.PlotEvents.Count > 0) sb.AppendLine($"Plot events: {string.Join("; ", ch.PlotEvents)}");
        if (ch.CharacterStates.Count > 0)
        {
            foreach (var cs in ch.CharacterStates)
            {
                sb.Append($"  {cs.Name}");
                if (cs.State != null) sb.Append($" — {cs.State}");
                if (cs.EmotionalArc != null) sb.Append($" ({cs.EmotionalArc})");
                sb.AppendLine();
            }
        }
        if (ch.OpenThreads.Count > 0) sb.AppendLine($"Open threads: {string.Join("; ", ch.OpenThreads)}");
        if (ch.ToneNotes != null) sb.AppendLine($"Tone: {ch.ToneNotes}");
        return sb.ToString();
    }
}
