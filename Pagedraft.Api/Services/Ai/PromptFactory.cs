using System.Text;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>Hebrew-focused, provider-agnostic prompt templates for both the correction pipeline and the unified analysis system.</summary>
public class PromptFactory
{
    // ─── System messages ────────────────────────────────────────────

    private const string HebrewSystemBase =
        "אתה עורך לשוני ומגיה טקסטים בעברית. עליך לתקן שגיאות לשון, דקדוק, כתיב ופיסוק בטקסטים ספרותיים בעברית, תוך שמירה על הקול, הסגנון והכוונה של המחבר. השב תמיד בעברית בלבד.";

    private const string HebrewAnalysisSystem =
        "אתה מומחה לניתוח ספרותי ולשוני של טקסטים בעברית. אתה מנתח כתיבה ספרותית, פרוזה ופואטיקה. השב תמיד בעברית בלבד, בסגנון מקצועי ותמציתי.";

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
        "You are an expert literary and linguistic analyst. You analyze prose, poetry, and creative writing with depth and precision. Respond in a professional, concise style.";

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
                ? "קבל קטע טקסט בעברית. תקן כל שגיאת כתיב, דקדוק, ניקוד או פיסוק שאתה מזהה. אל תחליף מילה במילה נרדפת ואל תשנה את המשמעות — תקן את שגיאת הכתיב במילה עצמה (למשל \"עתון\" ל\"עיתון\", לא ל\"עיתונות\") ואל תהפוך צירוף תקין למבע אחר (למשל אל תשנה \"עצמה רגשית\" ל\"עוצמת רגשות\"). אל תתקן ערבוב רישומים מכוון (למשל שפה מדוברת בדיאלוג). אם אין שגיאות, החזר את הטקסט כפי שהוא. החזר **רק** את הגרסה המתוקנת (או המקורית אם אין שינויים), בלי הסברים או תוספות. אל תשנה את מבנה הפסקאות אלא אם יש טעות ברורה. אל תוסיף תוכן חדש."
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
            AnalysisType.QA             => isHe ? QAHe : QAEn,
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
        var basePrompt = isHe ? ProofreadHe : ProofreadEn;

        var sb = new StringBuilder();

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
        // BookOverview, CharacterAnalysis, Custom — no extra context needed
        _ => ContextField.None,
    };

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

        מלא ערכים מדויקים ככל האפשר. הציון grammaticalityScore הוא בין 0 ל-1.

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

        Fill in values as accurately as possible. The grammaticalityScore is between 0 and 1.

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

    // The six dimensions, listed once in each language so the prompt names them consistently.
    private static readonly string BookReviewCombinedHe =
        """
        אתה עורך ספרותי פיתוחי (developmental editor) המעריך ספר שלם בבת אחת על פני שישה ממדים: plot (עלילה), character (דמויות), pacing (קצב), tone (טון/קול), theme (נושא), continuity (רציפות).
        הקשר הספר מסופק בתוך הסימון [BOOK_CONTEXT]: סקירת הספר (BookBrief) ותקצירי הפרקים שנכללו, לפי הסדר. קרא את כל הספר וזהה ממצאים עריכתיים על פני כל ששת הממדים בקריאה אחת.

        החזר אך ורק JSON תקין במבנה הבא, בלי טקסט לפני או אחרי וללא גדרות markdown:
        {
          "dimensionSignals": [
            { "dimension": "plot", "chapterOrder": 1, "title": "כותרת הפרק", "observation": "תצפית קצרה על הממד בפרק זה" }
          ],
          "findings": [
            {
              "dimension": "plot|character|pacing|tone|theme|continuity",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "משפט אחד או שניים שמסבירים את הממצא, בשפת הספר וללא מונחים טכניים.",
              "chapterAnchors": [
                { "order": 1, "title": "כותרת הפרק" }
              ],
              "evidence": [
                { "chapterOrder": 1, "excerpt": "ציטוט קצר או פרפראזה מתוך התקצירים" }
              ],
              "suggestedAction": "פעולה עריכתית מומלצת אחת (אופציונלי)"
            }
          ]
        }

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
        """
        You are a developmental editor assessing a complete book in ONE pass across all SIX dimensions: plot, character, pacing, tone (tone/voice), theme, continuity.
        The book context is provided inside the [BOOK_CONTEXT] marker: the book overview (BookBrief) and the included chapter briefs, in order. Read the whole book and identify editorial findings across all six dimensions in a single read.

        Return ONLY valid JSON in the exact shape below, with no text before or after and no markdown fences:
        {
          "dimensionSignals": [
            { "dimension": "plot", "chapterOrder": 1, "title": "chapter title", "observation": "a short observation about this dimension in this chapter" }
          ],
          "findings": [
            {
              "dimension": "plot|character|pacing|tone|theme|continuity",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "One or two sentences explaining the finding, in the book's language and free of technical jargon.",
              "chapterAnchors": [
                { "order": 1, "title": "chapter title" }
              ],
              "evidence": [
                { "chapterOrder": 1, "excerpt": "a short excerpt or paraphrase drawn from the briefs" }
              ],
              "suggestedAction": "one recommended editorial action (optional)"
            }
          ]
        }

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
            { "chapterOrder": 1, "title": "כותרת הפרק", "observation": "תצפית קצרה על {{dimensionKey}} בפרק זה לאורך הספר" }
          ],
          "findings": [
            {
              "dimension": "{{dimensionKey}}",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "משפט אחד או שניים שמסבירים את הממצא, בשפת הספר וללא מונחים טכניים.",
              "chapterAnchors": [
                { "order": 1, "title": "כותרת הפרק" }
              ],
              "evidence": [
                { "chapterOrder": 1, "excerpt": "ציטוט קצר או פרפראזה מתוך התקצירים" }
              ],
              "suggestedAction": "פעולה עריכתית מומלצת אחת (אופציונלי)"
            }
          ]
        }

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
            { "chapterOrder": 1, "title": "chapter title", "observation": "a short observation about {{dimensionKey}} in this chapter across the book" }
          ],
          "findings": [
            {
              "dimension": "{{dimensionKey}}",
              "verdict": "keep|improve|cut",
              "severity": 1,
              "rationale": "One or two sentences explaining the finding, in the book's language and free of technical jargon.",
              "chapterAnchors": [
                { "order": 1, "title": "chapter title" }
              ],
              "evidence": [
                { "chapterOrder": 1, "excerpt": "a short excerpt or paraphrase drawn from the briefs" }
              ],
              "suggestedAction": "one recommended editorial action (optional)"
            }
          ]
        }

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
