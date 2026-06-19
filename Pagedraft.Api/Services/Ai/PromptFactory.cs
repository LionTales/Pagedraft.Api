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
                ? "קבל קטע טקסט בעברית. תקן כל שגיאת כתיב, דקדוק, ניקוד או פיסוק שאתה מזהה. אם אין שגיאות, החזר את הטקסט כפי שהוא. החזר **רק** את הגרסה המתוקנת (או המקורית אם אין שינויים), בלי הסברים או תוספות. אל תשנה את מבנה הפסקאות אלא אם יש טעות ברורה. אל תוסיף תוכן חדש."
                : "Receive a text and return **only** the corrected version, with no explanations or additions. Do not change paragraph structure unless there is a clear error. Do not add new content.";
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
            var system = isHebrew ? HebrewSystemBase : EnglishProofreadSystem;
            var instruction = isHebrew ? "השב בעברית בלבד לפי ההנחיות שניתנו." : "Respond according to the instructions given.";
            return (system, instruction);
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
            AppendSection(sb, "CHAPTER_STYLE_BASELINE", FormatChapterStyleBaseline(chapterBaseline));

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
    /// Renders a chapter's cached style baseline (LinguisticAnalysisResult-shaped MetricsJson)
    /// as plain "metric: value" lines so the model can numerically compare the current scene's
    /// metrics against the chapter baseline and emit `deviations`. Returns empty (so the section
    /// is omitted) when MetricsJson is missing or cannot be parsed.
    /// </summary>
    private static string FormatChapterStyleBaseline(ChapterStyleProfile profile)
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
        sb.AppendLine("Chapter-wide baseline metrics (compare the current scene against these numbers):");

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
        - [CHAPTER_STYLE_BASELINE] — מדדי הסגנון הבסיסיים של הפרק כולו (קו ייחוס של הפרק עצמו). השווה את מדדי הסצנה שלפניך אל הקו הזה.
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
            { "metric": "averageSentenceLength", "sceneValue": 0.0, "chapterBaseline": 0.0, "note": "הסבר קצר על משמעות החריגה מהקו הבסיסי של הפרק." }
          ],
          "consistencyIssues": [
            { "type": "register", "span": "8-15 מילים המועתקות מילה-במילה מהטקסט (מחרוזת מדויקת, ללא שלוש נקודות, ללא מירכאות נוספות)", "description": "תיאור הבעיה בקצרה." }
          ]
        }

        מלא ערכים מדויקים ככל האפשר. הציון grammaticalityScore הוא בין 0 ל-1.

        deviations — מערך שבו כל פריט משווה מדד אחד של הסצנה אל הקו הבסיסי של הפרק ([CHAPTER_STYLE_BASELINE]): "metric" = שם המדד (כפי שהוא מופיע במדדים שלמעלה, למשל "averageSentenceLength" או "lexicalDensity"), "sceneValue" = ערך המדד בסצנה הנוכחית, "chapterBaseline" = ערך אותו מדד בקו הבסיסי של הפרק, "note" = משפט אחד קצר וברור בשפה ידידותית לכותב (ללא מונחים טכניים), שמסביר מה המשמעות המעשית של הפער עבור הסצנה (למשל קצב, קריאוּת או חיוּת) ולא רק חוזר על המספרים. כלול רק חריגות בעלות משמעות. אם אין קו בסיסי או אין חריגות — החזר מערך ריק [].

        consistencyIssues — דווח כאן אך ורק על שינויים שחוצים פסקאות בין חלקים שונים של הטקסט. שגיאות דקדוק, כתיב או תחביר אינן שייכות לכאן - הן משפיעות על grammaticalityScore בלבד ואסור לדווח עליהן כאן. בחר את "type" במדויק:
        - "register" — שינוי ברמת הפורמליות/הטון של הקריינות בין חלקים (למשל: קריינות יומיומית ופשוטה שהופכת לרשמית או ספרותית-מליצית, או להפך).
        - "tense" — שינוי בזמן הנרטיבי בין חלקים (למשל: מעבר מזמן עבר לזמן הווה באמצע הנרטיב).
        - "pov" — שינוי בנקודת המבט בין חלקים (למשל: מעבר מגוף ראשון לגוף שלישי, או "קפיצה בין ראשים").
        "span" = מחרוזת מתוך הטקסט הנתון, מועתקת מילה-במילה ותו-בתו, מהמקום שבו השינוי מופיע לראשונה. קריטי: ה-span חייב להיות ציטוט מילה-במילה מהטקסט הנתון לניתוח בלבד — לעולם לא מ-[PRECEDING_CONTEXT] או מ-[FOLLOWING_CONTEXT]. כאשר השינוי הוא ביחס להקשר הסובב, צטט את המשפט שבתוך הטקסט הנתון שבו השינוי מתבטא. קריטי: ה-span חייב להימצא בטקסט הנתון בחיפוש מחרוזת מדויק. העתק רצף רציף של כ-8 עד 15 מילים ישירות מהטקסט. אל תנסח מחדש, אל תסכם, אל תתקן ואל תשנה כתיב. אל תוסיף מירכאות, שלוש נקודות ("..." או "…"), סוגריים, או מילים שאינן בטקסט. אל תדלג על מילים ואל תחבר קטעים שאינם צמודים זה לזה. אם הקטע הרלוונטי ארוך - העתק רק את 8-15 המילים הראשונות שלו מילה-במילה במקום לקצר עם שלוש נקודות. "description" = משפט אחד קצר המתאר את השינוי (ממה למה). דווח לכל היותר על 3-4 הבעיות המשמעותיות ביותר; אם אינך בטוח - העדף מערך ריק [] על פני דיווח שגוי. לדוגמה שלילית: אל תדווח על "משפט עם שני פעלים", "משפט חסר נושא", או כל תצפית דקדוקית ברמת המשפט הבודד. דיאלוג נכתב באופן טבעי בשפה מדוברת ופשוטה יותר מהקריינות - אל תדווח על ההבדל הטבעי בין דברי הדמויות לבין שפת המספר כשינוי רישום; דווח רק על שינוי טון בתוך הקריינות עצמה (או בתוך הדיאלוג) בין חלקים. אם [PRECEDING_CONTEXT]/[FOLLOWING_CONTEXT] סופקו, ניתן להיעזר בהם רק כדי לזהות שינוי, אך ה-span שאתה מצטט חייב תמיד להגיע מהטקסט הנתון לניתוח עצמו. אם אין בעיות - החזר מערך ריק [].

        חשוב: כל "note" וכל "description" חייבים להיות משפט אחד קצר בלבד (עד כ-25 מילים). אל תחזור על אותו ניסוח או רעיון יותר מפעם אחת.
        """;

    private const string LinguisticEn =
        """
        You are a linguistic analysis expert. Perform a deep linguistic analysis of the following text.

        You may be given additional context inside markers:
        - [CHAPTER_STYLE_BASELINE] — the baseline style metrics of the whole chapter (the chapter's own reference line). Compare the metrics of the scene in front of you against this baseline.
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
            { "metric": "averageSentenceLength", "sceneValue": 0.0, "chapterBaseline": 0.0, "note": "Short explanation of what the divergence from the chapter baseline means." }
          ],
          "consistencyIssues": [
            { "type": "register", "span": "8-15 words copied verbatim from the text (exact substring, no ellipsis, no added quotes)", "description": "Brief description of the issue." }
          ]
        }

        Fill in values as accurately as possible. The grammaticalityScore is between 0 and 1.

        deviations — an array where each item compares ONE scene metric against the chapter baseline ([CHAPTER_STYLE_BASELINE]): "metric" = the metric name (as it appears in the metrics above, e.g. "averageSentenceLength" or "lexicalDensity"), "sceneValue" = the metric's value in the current scene, "chapterBaseline" = the same metric's value in the chapter baseline, "note" = one short, clear sentence in writer-friendly language (no technical jargon) explaining the practical effect of the difference on the scene (e.g. pace, readability, or vividness), not just restating the numbers. Include only meaningful divergences. If there is no baseline or no divergences, return an empty array [].

        consistencyIssues — report ONLY cross-paragraph shifts between different parts of the text. Grammar, spelling, and syntax errors do NOT belong here - they affect grammaticalityScore only and must NOT be reported here. Choose "type" precisely:
        - "register" — a formality/tone shift in the NARRATION between parts (e.g. plain, casual narration shifting to formal or ornate prose, or vice versa).
        - "tense" — a narration-tense shift between parts (e.g. past tense shifting to present tense mid-narrative).
        - "pov" — a perspective shift between parts (e.g. first-person shifting to third-person, or head-hopping).
        "span" = an EXACT, VERBATIM substring copied character-for-character from the analyzed text where the shift first becomes visible. CRITICAL: the span MUST be a verbatim quote from the ANALYZED text only — NEVER from [PRECEDING_CONTEXT] or [FOLLOWING_CONTEXT]. When the shift is relative to the surrounding context, quote the sentence WITHIN the analyzed text that exhibits the shift. CRITICAL: the span MUST be findable in the analyzed text by an exact-substring search. Copy a contiguous run of about 8 to 15 words straight from the text. Do NOT paraphrase, summarize, fix, or re-spell it. Do NOT add quotation marks, ellipsis ("..." or "…"), brackets, or any words that are not in the text. Do NOT skip words or join non-adjacent fragments. If the relevant passage is long, copy only its first ~8-15 words verbatim rather than truncating with an ellipsis. "description" = one short sentence stating the shift (from what to what). Report at most 3-4 of the most significant issues; when in doubt, prefer an empty array [] over a false positive. Negative examples: do NOT report things like "sentence has two verbs", "sentence lacks a subject", or any per-sentence grammatical observation. Dialogue is naturally written in a more colloquial, simpler register than narration - do NOT report the natural difference between characters' spoken lines and the narrator's prose as a register shift; only flag a tone shift WITHIN the narration itself (or within dialogue) between parts. If [PRECEDING_CONTEXT]/[FOLLOWING_CONTEXT] are provided, you may use them only to DETECT a shift, but the span you quote must always come from the analyzed text itself. If none are found, return an empty array [].

        Important: every "note" and "description" must be a single short sentence (max ~25 words). Do not repeat the same phrasing or idea more than once.
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
