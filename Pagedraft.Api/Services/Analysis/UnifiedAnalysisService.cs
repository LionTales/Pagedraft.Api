using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Single entry-point for all analysis: replaces both AiAnalysisService.RunAsync and the
/// pipeline's IAnalyzeEngine. Handles prompt selection, LLM invocation, structured parsing,
/// and persistence for every (Scope × Type) combination.
/// </summary>
public class UnifiedAnalysisService
{
    private readonly AppDbContext _db;
    private readonly IAiRouter _router;
    private readonly PromptFactory _promptFactory;
    private readonly SfdtConversionService _sfdtConversion;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<UnifiedAnalysisService> _logger;
    private readonly AnalysisProgressTracker _progress;
    private readonly IAnalysisContextService _contextService;
    private readonly SuggestionDiffService _suggestionDiff;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UnifiedAnalysisService(
        AppDbContext db,
        IAiRouter router,
        PromptFactory promptFactory,
        SfdtConversionService sfdtConversion,
        IOptions<AiOptions> aiOptions,
        ILogger<UnifiedAnalysisService> logger,
        AnalysisProgressTracker progress,
        IAnalysisContextService contextService,
        SuggestionDiffService suggestionDiff)
    {
        _db = db;
        _router = router;
        _promptFactory = promptFactory;
        _sfdtConversion = sfdtConversion;
        _aiOptions = aiOptions;
        _logger = logger;
        _progress = progress;
        _contextService = contextService;
        _suggestionDiff = suggestionDiff;
    }

    /// <summary>Max characters for a single proofread request. Longer text often causes the model to truncate or generate new content instead of correcting.</summary>
    private const int MaxProofreadInputLength = 10_000;

    /// <summary>Run an analysis and persist the result.</summary>
    /// <param name="jobId">
    /// Optional analysis job identifier for long-running operations (currently chunked Proofread/LineEdit).
    /// When provided and the run uses chunked proofread/LineEdit, this jobId will be used for progress tracking and persisted on AnalysisResult.
    /// When null, a new jobId is generated internally for chunked proofread/LineEdit.
    /// </param>
    public async Task<AnalysisResult> RunAsync(
        AnalysisScope scope,
        AnalysisType analysisType,
        Guid targetId,
        string? customPrompt,
        string language,
        Guid? jobId = null,
        CancellationToken ct = default)
    {
        var context = await _contextService.BuildContextAsync(scope, targetId, analysisType, ct);
        var inputText = context.TargetText;
        var bookId = context.BookId;
        var chapterId = context.ChapterId;
        var sceneId = context.SceneId;
        if (analysisType == AnalysisType.Proofread)
        {
            var opts = _aiOptions.Value;
            var chunkTargetWords = opts.EffectiveProofreadChunkTargetWords;
            var maxParallel = Math.Max(1, opts.MaxParallelProofreadChunks);
            var wordCount = WordCount(inputText);

            if (wordCount > chunkTargetWords)
            {
                var effectiveJobId = jobId ?? Guid.NewGuid();
                return await RunProofreadChunkedAsync(
                    inputText, bookId, chapterId, sceneId, scope, targetId,
                    customPrompt, language, chunkTargetWords, maxParallel, effectiveJobId, context, ct);
            }
            if (inputText.Length > MaxProofreadInputLength)
                throw new InvalidOperationException($"Proofread text is too long ({inputText.Length} characters). Please select a shorter section (e.g. one scene or a few paragraphs). Maximum is {MaxProofreadInputLength:N0} characters.");
        }

        if (analysisType == AnalysisType.LineEdit)
        {
            var opts = _aiOptions.Value;
            var chunkTargetWords = opts.EffectiveLineEditChunkTargetWords;
            var maxParallel = Math.Max(1, opts.MaxParallelLineEditChunks);
            var wordCount = WordCount(inputText);

            if (wordCount > chunkTargetWords)
            {
                var effectiveJobId = jobId ?? Guid.NewGuid();
                return await RunLineEditChunkedAsync(
                    inputText, bookId, chapterId, sceneId, scope, targetId,
                    customPrompt, language, chunkTargetWords, maxParallel, effectiveJobId, context, ct);
            }
        }

        var taskType = MapToTaskType(analysisType);
        var instruction = customPrompt
            ?? _promptFactory.GetAnalysisPrompt(analysisType, language, context);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = targetId.ToString(),
            JsonMode = analysisType == AnalysisType.LineEdit
        };

        _logger.LogInformation("Running {Scope}/{Type} analysis on {TargetId}", scope, analysisType, targetId);
        if (analysisType == AnalysisType.Proofread)
            _logger.LogInformation("Proofread input length: {Length} characters (~{EstTokens} tokens). Long text may hit model limits.", inputText.Length, EstimateTokenCount(inputText));

        var response = await _router.CompleteAsync(request, ct);

        if (analysisType == AnalysisType.Proofread && _logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _logger.LogDebug("Proofread raw response length={Len} startsWith={Preview}", response.Content?.Length ?? 0, response.Content?.Length > 0 ? response.Content.Substring(0, Math.Min(120, response.Content.Length)) : "");

        if (analysisType == AnalysisType.Proofread)
            _logger.LogInformation("Proofread raw response: length={Len}, preview={Preview}", response.Content?.Length ?? 0, TruncateForAudit(response.Content ?? "", 200));

        var cleanContent = SanitizeResponse(response.Content ?? "");

        // If Proofread ended up empty after stripping (e.g. model put answer only in <think> or hit a stop), use raw or input so we don't persist empty
        if (analysisType == AnalysisType.Proofread && string.IsNullOrWhiteSpace(cleanContent) && !string.IsNullOrEmpty(response.Content))
        {
            _logger.LogWarning("Proofread response was empty after sanitization (raw length={RawLen}). Using raw response or input as fallback.", response.Content.Length);
            var afterThink = ExtractTextAfterThinkBlock(response.Content);
            cleanContent = !string.IsNullOrWhiteSpace(afterThink) ? afterThink : response.Content.Trim();
            if (string.IsNullOrWhiteSpace(cleanContent))
                cleanContent = inputText;
        }

        var structuredJson = TryParseStructured(analysisType, cleanContent);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, analysisType, ct);

        if (analysisType == AnalysisType.Proofread)
        {
            cleanContent = StripTextToCorrectMarkers(cleanContent);
        }

        cleanContent = MaybeReplaceLineEditResultText(analysisType, structuredJson, cleanContent);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = analysisType,
            Type = analysisType.ToString(),
            PromptUsed = TruncateForAudit(instruction),
            ResultText = cleanContent,
            StructuredResult = structuredJson,
            Language = language,
            ModelName = $"{response.Provider}:{response.Model}",
            SourceTextSnapshot = TextNormalization.NormalizeTextForAnalysis(inputText)
        };

        AttachSuggestions(result, inputText, analysisType, structuredJson, cleanContent, isStreaming: false, isRunWithInput: false, applyProofreadHeuristics: true);

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Analysis {Id} persisted ({Scope}/{Type})", result.Id, scope, analysisType);
        return result;
    }

    /// <summary>
    /// Run analysis with explicit input text and persist. Used for Book-scope Q&A where
    /// input is concatenated chapter summaries + question, not resolved from a target.
    /// </summary>
    public async Task<AnalysisResult> RunWithInputAsync(
        AnalysisScope scope,
        AnalysisType analysisType,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        string inputText,
        string language,
        CancellationToken ct = default)
    {
        var taskType = MapToTaskType(analysisType);
        var instruction = _promptFactory.GetAnalysisPrompt(analysisType, language);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = bookId?.ToString() ?? chapterId?.ToString() ?? sceneId?.ToString() ?? "",
            JsonMode = analysisType == AnalysisType.LineEdit
        };

        _logger.LogInformation("Running {Scope}/{Type} with provided input", scope, analysisType);
        var response = await _router.CompleteAsync(request, ct);

        var cleanContent = SanitizeResponse(response.Content);
        var structuredJson = TryParseStructured(analysisType, cleanContent);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, analysisType, ct);

        if (analysisType == AnalysisType.Proofread)
        {
            cleanContent = StripTextToCorrectMarkers(cleanContent);
        }

        cleanContent = MaybeReplaceLineEditResultText(analysisType, structuredJson, cleanContent);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = analysisType,
            Type = analysisType.ToString(),
            PromptUsed = TruncateForAudit(instruction),
            ResultText = cleanContent,
            StructuredResult = structuredJson,
            Language = language,
            ModelName = $"{response.Provider}:{response.Model}",
            SourceTextSnapshot = TextNormalization.NormalizeTextForAnalysis(inputText)
        };
        AttachSuggestions(result, inputText, analysisType, structuredJson, cleanContent, isStreaming: false, isRunWithInput: true, applyProofreadHeuristics: true);

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Analysis {Id} persisted ({Scope}/{Type})", result.Id, scope, analysisType);
        return result;
    }

    /// <summary>Stream an analysis, accumulate tokens, then persist. Chunking is not used; for long chapters use non-streaming proofread (chunked).</summary>
    public async IAsyncEnumerable<string> RunStreamingAsync(
        AnalysisScope scope,
        AnalysisType analysisType,
        Guid targetId,
        string? customPrompt,
        string language,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var context = await _contextService.BuildContextAsync(scope, targetId, analysisType, ct);
        var inputText = context.TargetText;
        var bookId = context.BookId;
        var chapterId = context.ChapterId;
        var sceneId = context.SceneId;
        if (analysisType == AnalysisType.Proofread && inputText.Length > MaxProofreadInputLength)
            throw new InvalidOperationException($"Proofread text is too long ({inputText.Length} characters). Please select a shorter section (e.g. one scene or a few paragraphs). Maximum is {MaxProofreadInputLength:N0} characters.");

        var taskType = MapToTaskType(analysisType);
        var instruction = customPrompt
            ?? _promptFactory.GetAnalysisPrompt(analysisType, language, context);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = targetId.ToString(),
            JsonMode = analysisType == AnalysisType.LineEdit
        };

        var sb = new StringBuilder();
        await foreach (var token in _router.StreamCompleteAsync(request, ct))
        {
            if (ct.IsCancellationRequested) yield break;
            sb.Append(token);
            yield return token;
        }

        var cleanContent = SanitizeResponse(sb.ToString());

        if (analysisType == AnalysisType.Proofread && string.IsNullOrWhiteSpace(cleanContent))
        {
            var raw = sb.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Proofread (streaming) response empty after sanitization (raw length={RawLen}). Using fallback.", raw.Length);
                var afterThink = ExtractTextAfterThinkBlock(raw);
                cleanContent = !string.IsNullOrWhiteSpace(afterThink) ? afterThink : raw.Trim();
                if (string.IsNullOrWhiteSpace(cleanContent))
                    cleanContent = inputText;
            }
        }

        var structuredJson = TryParseStructured(analysisType, cleanContent);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, analysisType, ct);

        if (analysisType == AnalysisType.Proofread)
        {
            cleanContent = StripTextToCorrectMarkers(cleanContent);
        }

        cleanContent = MaybeReplaceLineEditResultText(analysisType, structuredJson, cleanContent);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = analysisType,
            Type = analysisType.ToString(),
            PromptUsed = TruncateForAudit(instruction),
            ResultText = cleanContent,
            StructuredResult = structuredJson,
            Language = language,
            ModelName = "stream",
            SourceTextSnapshot = TextNormalization.NormalizeTextForAnalysis(inputText)
        };

        AttachSuggestions(result, inputText, analysisType, structuredJson, cleanContent, isStreaming: true, isRunWithInput: false, applyProofreadHeuristics: true);

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Run analysis without persistence — used internally by BookIntelligenceService
    /// for chapter summarization where the result feeds into a larger pipeline.
    /// </summary>
    public async Task<string> RunRawAsync(
        string inputText,
        AnalysisType analysisType,
        string? instruction,
        string language,
        CancellationToken ct = default)
    {
        var taskType = MapToTaskType(analysisType);
        var prompt = instruction ?? _promptFactory.GetAnalysisPrompt(analysisType, language);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = prompt,
            TaskType = taskType,
            Language = language,
            JsonMode = analysisType == AnalysisType.LineEdit
        };

        var response = await _router.CompleteAsync(request, ct);
        return SanitizeResponse(response.Content);
    }

    // ─── Proofread chunking (paragraph/sentence aware) ───────────────

    /// <summary>Structured chunk for proofread with merge separator and soft overlap context (prefix only).</summary>
    private sealed record ProofreadChunk(string Text, string SeparatorAfter, string? OverlapPrefix);

    /// <summary>
    /// Structured chunk for LineEdit with merge separator and soft overlap context
    /// (both prefix from previous chunk and suffix from next chunk).
    /// This will be used by the LineEdit chunking pipeline.
    /// </summary>
    private sealed record LineEditChunk(
        string Text,
        string SeparatorAfter,
        string? OverlapPrefix,
        string? OverlapSuffix);

    /// <summary>
    /// Shared core: split text by paragraphs then sentences, group into ~targetWords per chunk
    /// with dialogue-aware grouping. Returns raw (Text, SeparatorAfter) chunks; callers add
    /// overlap prefix/suffix as needed (Proofread: prefix only; LineEdit: prefix + suffix).
    /// </summary>
    private static List<(string Text, string SeparatorAfter)> BuildChunkSegmentsCore(string fullText, int targetWordsPerChunk)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return new List<(string Text, string SeparatorAfter)> { ("", "") };
        if (targetWordsPerChunk <= 0)
            return new List<(string Text, string SeparatorAfter)> { (fullText.Trim(), "") };

        fullText = fullText.TrimEnd();
        var segments = new List<(string Text, string Sep)>();

        // Split by paragraph boundaries, keeping separators
        var paraParts = Regex.Split(fullText, @"(\n\n+)");
        for (var i = 0; i < paraParts.Length; i++)
        {
            var part = paraParts[i];
            if (string.IsNullOrEmpty(part)) continue;
            if (Regex.IsMatch(part, @"^\s*$")) continue;

            if (Regex.IsMatch(part, @"^\n\n+$"))
            {
                if (segments.Count > 0)
                {
                    var (t, s) = segments[^1];
                    segments[^1] = (t, part);
                }
                continue;
            }

            var paragraphSep = (i + 1 < paraParts.Length && Regex.IsMatch(paraParts[i + 1], @"^\n\n+$")) ? paraParts[i + 1] : "";

            if (WordCount(part) <= targetWordsPerChunk)
            {
                segments.Add((part.Trim(), paragraphSep));
            }
            else
            {
                // Split on sentence boundaries (Latin + Hebrew / Devanagari)
                var sentenceParts = Regex.Split(part, @"(?<=[.!?।])\s+");
                var hadAnySentence = false;
                for (var j = 0; j < sentenceParts.Length; j++)
                {
                    var sent = sentenceParts[j].Trim();
                    if (string.IsNullOrEmpty(sent)) continue;
                    hadAnySentence = true;
                    var sentSep = (j < sentenceParts.Length - 1) ? " " : paragraphSep;
                    if (WordCount(sent) <= targetWordsPerChunk)
                        segments.Add((sent, sentSep));
                    else
                    {
                        // One long sentence or no sentence boundaries: split by word count
                        foreach (var (subText, subSep) in SplitByWordCount(sent, targetWordsPerChunk, sentSep, " "))
                            segments.Add((subText, subSep));
                    }
                }
                if (!hadAnySentence)
                {
                    foreach (var (subText, subSep) in SplitByWordCount(part.Trim(), targetWordsPerChunk, paragraphSep, " "))
                        segments.Add((subText, subSep));
                }
            }
        }

        if (segments.Count == 0)
            return new List<(string Text, string SeparatorAfter)> { ("", "") };

        // Group segments into chunks of ~targetWordsPerChunk (dialogue-aware)
        var baseChunks = new List<(string Text, string SeparatorAfter)>();
        var current = new StringBuilder();
        var currentWords = 0;
        var lastSep = "";
        var inDialogueBlock = false;

        foreach (var (text, sep) in segments)
        {
            var w = WordCount(text);
            var belongsToDialogue = BelongsToDialogueBlock(text);

            if (currentWords == 0)
            {
                inDialogueBlock = belongsToDialogue;
            }
            else if (belongsToDialogue)
            {
                inDialogueBlock = true;
            }
            else if (inDialogueBlock && !belongsToDialogue)
            {
                inDialogueBlock = false;
            }

            var limit = targetWordsPerChunk;
            var dialogueLimit = (int)Math.Round(targetWordsPerChunk * DialogueOverflowMultiplier);

            if (currentWords > 0)
            {
                var threshold = inDialogueBlock ? dialogueLimit : limit;
                if (currentWords + w > threshold)
                {
                    baseChunks.Add((current.ToString().TrimEnd(), lastSep));
                    current.Clear();
                    currentWords = 0;
                }
            }

            current.Append(text).Append(sep);
            currentWords += w;
            lastSep = sep;
        }

        if (current.Length > 0)
            baseChunks.Add((current.ToString().TrimEnd(), lastSep));

        return baseChunks;
    }

    /// <summary>
    /// Chunk text for proofread: split by paragraphs then sentences, ~targetWords per chunk,
    /// with dialogue-aware grouping and soft overlaps. Returns chunks with:
    /// - Text: the chunk to correct
    /// - SeparatorAfter: separator to append after this chunk when merging
    /// - OverlapPrefix: trailing sentences from previous chunk (read-only [CONTEXT_BEFORE])
    /// </summary>
    private static List<ProofreadChunk> ChunkForProofread(string fullText, int targetWordsPerChunk)
    {
        var baseChunks = BuildChunkSegmentsCore(fullText, targetWordsPerChunk);
        if (baseChunks.Count == 0)
            return new List<ProofreadChunk> { new("", "", null) };

        var result = new List<ProofreadChunk>(baseChunks.Count);
        for (var i = 0; i < baseChunks.Count; i++)
        {
            var (text, sep) = baseChunks[i];
            string? overlapPrefix = null;
            if (i > 0)
            {
                var trailing = ExtractTrailingSentences(baseChunks[i - 1].Text, 3);
                overlapPrefix = string.IsNullOrWhiteSpace(trailing) ? null : trailing;
            }
            result.Add(new ProofreadChunk(text, sep, overlapPrefix));
        }
        return result;
    }

    /// <summary>
    /// Chunk text for LineEdit: split by paragraphs then sentences, ~targetWords per chunk,
    /// with dialogue-aware grouping and soft overlaps. Returns chunks with:
    /// - Text: the chunk to edit
    /// - SeparatorAfter: separator to append after this chunk when merging
    /// - OverlapPrefix: trailing sentences from previous chunk (read-only [PRECEDING_CONTEXT])
    /// - OverlapSuffix: leading sentences from next chunk (read-only [FOLLOWING_CONTEXT])
    /// </summary>
    private static List<LineEditChunk> ChunkForLineEdit(string fullText, int targetWordsPerChunk)
    {
        var baseChunks = BuildChunkSegmentsCore(fullText, targetWordsPerChunk);
        if (baseChunks.Count == 0)
            return new List<LineEditChunk> { new("", "", null, null) };

        var result = new List<LineEditChunk>(baseChunks.Count);
        for (var i = 0; i < baseChunks.Count; i++)
        {
            var (text, sep) = baseChunks[i];
            string? overlapPrefix = null;
            string? overlapSuffix = null;
            if (i > 0)
            {
                var trailing = ExtractTrailingSentences(baseChunks[i - 1].Text, 3);
                overlapPrefix = string.IsNullOrWhiteSpace(trailing) ? null : trailing;
            }
            if (i < baseChunks.Count - 1)
            {
                var leading = ExtractLeadingSentences(baseChunks[i + 1].Text, 2);
                overlapSuffix = string.IsNullOrWhiteSpace(leading) ? null : leading;
            }
            result.Add(new LineEditChunk(text, sep, overlapPrefix, overlapSuffix));
        }
        return result;
    }

    private static int WordCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return Regex.Split(text.Trim(), @"\s+").Count(s => s.Length > 0);
    }

    /// <summary>Splits text into segments of at most targetWords words (word-boundary). Last segment gets lastSegmentSep, others get betweenSep.</summary>
    private static List<(string Text, string Sep)> SplitByWordCount(string text, int targetWords, string lastSegmentSep, string betweenSep)
    {
        var result = new List<(string Text, string Sep)>();
        if (string.IsNullOrWhiteSpace(text) || targetWords <= 0) return result;
        var words = Regex.Split(text.Trim(), @"\s+").Where(s => s.Length > 0).ToList();
        if (words.Count == 0) return result;
        for (var start = 0; start < words.Count; start += targetWords)
        {
            var take = Math.Min(targetWords, words.Count - start);
            var segment = string.Join(" ", words.Skip(start).Take(take));
            var isLast = start + take >= words.Count;
            result.Add((segment, isLast ? lastSegmentSep : betweenSep));
        }
        return result;
    }

    // ─── Dialogue-aware chunking rules ─────────────────────────────

    /// <summary>When inside a dialogue block and a chunk would exceed the target, allow up to 30% overflow to keep the block intact.</summary>
    internal const double DialogueOverflowMultiplier = 1.3;

    /// <summary>Max word count for a line to qualify as a short attribution/narration between dialogue turns.</summary>
    private const int MaxAttributionWords = 20;

    /// <summary>
    /// Detects opening dialogue markers: standard double quote, Hebrew gershayim (״),
    /// left curly quote (\u201C), em dash (—), and en dash (–).
    /// </summary>
    private static readonly Regex DialogueStartPattern = new(
        "^\\s*[\"\u201C\u05F4\u2014\u2013]",
        RegexOptions.Compiled);

    /// <summary>Hebrew speech-verb attribution after a quoted clause (e.g. "...," אמרה שרה).</summary>
    private static readonly Regex HebrewAttributionPattern = new(
        "(אמר|אמרה|שאל|שאלה|ענה|ענתה|לחש|לחשה|צעק|צעקה|מלמל|מלמלה|קרא|קראה|הוסיף|הוסיפה|סיפר|סיפרה)\\s",
        RegexOptions.Compiled);

    /// <summary>English speech-verb attribution after a quoted clause (e.g. "...," said Sarah).</summary>
    private static readonly Regex EnglishAttributionPattern = new(
        @"[,]\s*(said|asked|replied|answered|whispered|shouted|murmured|exclaimed|called|added|continued)\s",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns true if the segment begins with a dialogue marker
    /// (opening quote, Hebrew gershayim, em dash, en dash, left curly quote).
    /// </summary>
    internal static bool IsDialogueStart(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return DialogueStartPattern.IsMatch(text);
    }

    /// <summary>
    /// Returns true if the segment is a short attribution/narration line that belongs
    /// to the surrounding dialogue block (e.g. "אמרה שרה וחייכה." or "said Sarah quietly.").
    /// Capped at <see cref="MaxAttributionWords"/> words so longer paragraphs aren't mistakenly absorbed.
    /// </summary>
    internal static bool IsDialogueAttribution(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (WordCount(text) > MaxAttributionWords) return false;
        return HebrewAttributionPattern.IsMatch(text) || EnglishAttributionPattern.IsMatch(text);
    }

    /// <summary>
    /// Returns true if the segment is part of an ongoing dialogue block — either it
    /// opens with a dialogue marker or it is a short attribution line.
    /// Used by the dialogue-aware chunking loop to decide whether extending the current
    /// chunk (up to <see cref="DialogueOverflowMultiplier"/>) is preferable to splitting.
    /// </summary>
    internal static bool BelongsToDialogueBlock(string text)
    {
        return IsDialogueStart(text) || IsDialogueAttribution(text);
    }

    private static string ExtractTrailingSentences(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text) || count <= 0) return "";
        var parts = Regex.Split(text.Trim(), @"(?<=[.!?।])\s+")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (parts.Count == 0) return "";
        var start = Math.Max(0, parts.Count - count);
        return string.Join(" ", parts.Skip(start).Take(count)).Trim();
    }

    private static string ExtractLeadingSentences(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text) || count <= 0) return "";
        var parts = Regex.Split(text.Trim(), @"(?<=[.!?।])\s+")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (parts.Count == 0) return "";
        return string.Join(" ", parts.Take(count)).Trim();
    }

    // ─── LineEdit chunk merging ──────────────────────────────────────

    /// <summary>
    /// Merge per-chunk LineEdit results into a single <see cref="LineEditResult"/>.
    /// Concatenates suggestions in chunk order, deduplicates overlap-region duplicates
    /// by normalized <see cref="LineEditSuggestion.Original"/> text, and joins
    /// non-empty <see cref="LineEditResult.OverallFeedback"/> strings.
    /// </summary>
    internal static LineEditResult MergeLineEditResults(List<LineEditResult> chunkResults)
    {
        if (chunkResults is null || chunkResults.Count == 0)
            return new LineEditResult();

        var merged = new List<LineEditSuggestion>();
        var seenOriginals = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chunk in chunkResults)
        {
            if (chunk?.Suggestions is null || chunk.Suggestions.Count == 0)
                continue;

            foreach (var suggestion in chunk.Suggestions)
            {
                if (suggestion is null) continue;

                // Filter no-op suggestions where original == suggested (model padding)
                if (IsNoOpSuggestion(suggestion))
                    continue;

                var normalizedOriginal = TextNormalization.NormalizeTextForAnalysis(
                    suggestion.Original ?? string.Empty);

                if (string.IsNullOrWhiteSpace(normalizedOriginal))
                {
                    merged.Add(suggestion);
                    continue;
                }

                if (seenOriginals.Add(normalizedOriginal))
                    merged.Add(suggestion);
            }
        }

        var feedbackParts = chunkResults
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.OverallFeedback))
            .Select(c => c.OverallFeedback.Trim())
            .ToList();

        var combinedFeedback = feedbackParts.Count switch
        {
            0 => string.Empty,
            1 => feedbackParts[0],
            _ => string.Join("\n\n---\n\n", feedbackParts)
        };

        return new LineEditResult
        {
            Suggestions = merged,
            OverallFeedback = combinedFeedback
        };
    }

    /// <summary>
    /// Returns true when a suggestion is a no-op: original and suggested are identical
    /// after trimming and Unicode normalization. These are padding entries the model
    /// emits with reasons like "אין שינוי דרוש" that carry no value.
    /// </summary>
    internal static bool IsNoOpSuggestion(LineEditSuggestion suggestion)
    {
        var original = (suggestion.Original ?? string.Empty).Trim();
        var suggested = (suggestion.Suggested ?? string.Empty).Trim();

        if (original == suggested)
            return true;

        var normalizedOriginal = TextNormalization.NormalizeTextForAnalysis(original);
        var normalizedSuggested = TextNormalization.NormalizeTextForAnalysis(suggested);

        return !string.IsNullOrEmpty(normalizedOriginal) &&
               normalizedOriginal == normalizedSuggested;
    }

    /// <summary>
    /// Run LineEdit in chunks with limited parallelism, then merge into one AnalysisResult.
    /// Updates AnalysisProgressTracker for live progress polling.
    /// </summary>
    private async Task<AnalysisResult> RunLineEditChunkedAsync(
        string inputText,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        AnalysisScope scope,
        Guid targetId,
        string? customPrompt,
        string language,
        int chunkTargetWords,
        int maxParallel,
        Guid jobId,
        AnalysisContext context,
        CancellationToken ct)
    {
        var taskType = MapToTaskType(AnalysisType.LineEdit);
        var chunks = ChunkForLineEdit(inputText, chunkTargetWords);

        string? representativeInstruction;
        if (customPrompt is not null)
        {
            representativeInstruction = customPrompt;
        }
        else if (chunks.Count > 0)
        {
            var firstChunk = chunks[0];
            representativeInstruction = _promptFactory.BuildLineEditChunkPrompt(
                language,
                context,
                firstChunk.OverlapPrefix,
                firstChunk.OverlapSuffix,
                isFirstChunk: true,
                isLastChunk: chunks.Count == 1);
        }
        else
        {
            representativeInstruction = null;
        }

        _logger.LogInformation(
            "LineEdit chunked: input {WordCount} words, {ChunkCount} chunks, max parallel {MaxParallel}",
            WordCount(inputText), chunks.Count, maxParallel);

        _progress.StartJob(
            jobId,
            scope,
            AnalysisType.LineEdit,
            bookId,
            chapterId,
            sceneId,
            $"Queued {chunks.Count} LineEdit chunks");
        _progress.SetTotalChunks(jobId, chunks.Count, $"Queued {chunks.Count} LineEdit chunks");

        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var chunkResults = new LineEditResult[chunks.Count];

        async Task ProcessChunk(int index)
        {
            var chunk = chunks[index];
            var text = chunk.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                chunkResults[index] = new LineEditResult();
                return;
            }

            await semaphore.WaitAsync(ct);
            try
            {
                var chunkNumber = index + 1;
                _logger.LogDebug("LineEdit chunk {Index}/{Total} starting ({Words} words)", chunkNumber, chunks.Count, WordCount(text));
                _progress.ChunkStarted(jobId, chunkNumber, chunks.Count);

                var instruction = customPrompt
                    ?? _promptFactory.BuildLineEditChunkPrompt(
                        language,
                        context,
                        chunk.OverlapPrefix,
                        chunk.OverlapSuffix,
                        isFirstChunk: index == 0,
                        isLastChunk: index == chunks.Count - 1);

                var wrappedText = $"[TEXT_TO_EDIT]{text}[/TEXT_TO_EDIT]";

                var request = new AiRequest
                {
                    InputText = wrappedText,
                    Instruction = instruction,
                    TaskType = taskType,
                    Language = language,
                    SourceId = targetId.ToString(),
                    JsonMode = true
                };

                var response = await _router.CompleteAsync(request, ct);
                var raw = response.Content ?? string.Empty;
                _logger.LogDebug(
                    "LineEdit chunk {Index}/{Total} raw response: length={Len}, preview={Preview}",
                    chunkNumber, chunks.Count, raw.Length, TruncateForAudit(raw, 200));
                var clean = SanitizeResponse(raw);

                var structuredJson = TryParseStructured(AnalysisType.LineEdit, clean);

                if (structuredJson is null)
                {
                    _logger.LogWarning(
                        "LineEdit chunk {Index}/{Total} produced no structured JSON. rawLen={RawLen}, cleanLen={CleanLen}, cleanPreview={Preview}",
                        chunkNumber, chunks.Count, raw.Length, clean.Length, TruncateForAudit(clean, 200));
                    chunkResults[index] = new LineEditResult();
                }
                else
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<LineEditResult>(structuredJson, JsonOpts);
                        chunkResults[index] = parsed ?? new LineEditResult();
                    }
                    catch (JsonException)
                    {
                        chunkResults[index] = new LineEditResult();
                    }
                }

                _logger.LogDebug("LineEdit chunk {Index}/{Total} finished", chunkNumber, chunks.Count);
                _progress.ChunkCompleted(jobId, chunkNumber, chunks.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var chunkNumber = index + 1;
                _logger.LogWarning(ex, "LineEdit chunk {Index} failed; treating as empty result", chunkNumber);
                chunkResults[index] = new LineEditResult();
                _progress.ChunkCompleted(jobId, chunkNumber, chunks.Count);
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = Enumerable.Range(0, chunks.Count).Select(ProcessChunk).ToArray();
        await Task.WhenAll(tasks);

        var merged = MergeLineEditResults(chunkResults.ToList());
        var mergedJson = JsonSerializer.Serialize(merged, JsonOpts);

        _logger.LogInformation("LineEdit merge complete: {SuggestionCount} suggestions", merged.Suggestions.Count);
        _progress.SetStatus(jobId, AnalysisProgressStatus.Succeeded, "LineEdit finished");

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, AnalysisType.LineEdit, ct);

        var cleanContent = MaybeReplaceLineEditResultText(AnalysisType.LineEdit, mergedJson, merged.OverallFeedback ?? string.Empty);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = AnalysisType.LineEdit,
            Type = nameof(AnalysisType.LineEdit),
            PromptUsed = TruncateForAudit(
                representativeInstruction
                ?? (customPrompt ?? _promptFactory.GetAnalysisPrompt(AnalysisType.LineEdit, language, context))),
            ResultText = cleanContent,
            StructuredResult = mergedJson,
            Language = language,
            ModelName = "chunked",
            JobId = jobId,
            SourceTextSnapshot = TextNormalization.NormalizeTextForAnalysis(inputText)
        };

        AttachSuggestions(result, inputText, AnalysisType.LineEdit, mergedJson, cleanContent, isStreaming: false, isRunWithInput: false, applyProofreadHeuristics: false);

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Analysis {Id} persisted (LineEdit chunked, {Scope})", result.Id, scope);
        return result;
    }

    /// <summary>Run proofread in chunks with limited parallelism, then merge into one AnalysisResult. Updates AnalysisProgressTracker for live progress polling.</summary>
    private async Task<AnalysisResult> RunProofreadChunkedAsync(
        string inputText,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        AnalysisScope scope,
        Guid targetId,
        string? customPrompt,
        string language,
        int chunkTargetWords,
        int maxParallel,
        Guid jobId,
        AnalysisContext context,
        CancellationToken ct)
    {
        var taskType = MapToTaskType(AnalysisType.Proofread);
        var chunks = ChunkForProofread(inputText, chunkTargetWords);

        // Representative instruction for auditing: either the custom prompt (if provided)
        // or the instruction that will be used for the first chunk when using the
        // chunk-aware proofread prompt.
        string? representativeInstruction;
        if (customPrompt is not null)
        {
            representativeInstruction = customPrompt;
        }
        else if (chunks.Count > 0)
        {
            var firstChunk = chunks[0];
            representativeInstruction = _promptFactory.BuildProofreadChunkPrompt(
                language,
                context.Characters,
                firstChunk.OverlapPrefix);
        }
        else
        {
            representativeInstruction = null;
        }

        _logger.LogInformation(
            "Proofread chunked: input {WordCount} words, {ChunkCount} chunks, max parallel {MaxParallel}",
            WordCount(inputText), chunks.Count, maxParallel);

        _progress.StartJob(
            jobId,
            scope,
            AnalysisType.Proofread,
            bookId,
            chapterId,
            sceneId,
            $"Queued {chunks.Count} proofread chunks");
        _progress.SetTotalChunks(jobId, chunks.Count, $"Queued {chunks.Count} proofread chunks");

        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var corrected = new string[chunks.Count];

        async Task ProcessChunk(int index)
        {
            var chunk = chunks[index];
            var text = chunk.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                corrected[index] = text ?? "";
                return;
            }
            await semaphore.WaitAsync(ct);
            try
            {
                var chunkNumber = index + 1;
                _logger.LogDebug("Proofread chunk {Index}/{Total} starting ({Words} words)", chunkNumber, chunks.Count, WordCount(text));
                _progress.ChunkStarted(jobId, chunkNumber, chunks.Count);

                var instruction = customPrompt
                    ?? _promptFactory.BuildProofreadChunkPrompt(language, context.Characters, chunk.OverlapPrefix);
                var wrappedText = customPrompt is null
                    ? $"[TEXT_TO_CORRECT]{text}[/TEXT_TO_CORRECT]"
                    : text;

                var request = new AiRequest
                {
                    InputText = wrappedText,
                    Instruction = instruction,
                    TaskType = taskType,
                    Language = language,
                    SourceId = targetId.ToString()
                };
                var response = await _router.CompleteAsync(request, ct);
                var raw = response.Content ?? "";
                var clean = SanitizeResponse(raw);
                if (string.IsNullOrWhiteSpace(clean) && !string.IsNullOrEmpty(raw))
                {
                    var afterThink = ExtractTextAfterThinkBlock(raw);
                    clean = !string.IsNullOrWhiteSpace(afterThink) ? afterThink : raw.Trim();
                }
                if (string.IsNullOrWhiteSpace(clean))
                    clean = text;
                clean = StripTextToCorrectMarkers(clean);
                var unrelated = IsProofreadResultUnrelated(text, clean);
                if (unrelated)
                {
                    _logger.LogWarning(
                        "Proofread chunk {Index} result may be unrelated (input prefix='{InputPrefix}', result prefix='{ResultPrefix}'). Keeping model output so suggestions are not lost.",
                        index + 1,
                        TruncateForAudit(text, 150),
                        TruncateForAudit(clean, 150));
                }
                corrected[index] = clean;
                _logger.LogDebug("Proofread chunk {Index}/{Total} finished (result length {Len})", chunkNumber, chunks.Count, clean.Length);
                _progress.ChunkCompleted(jobId, chunkNumber, chunks.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var chunkNumber = index + 1;
                _logger.LogWarning(ex, "Proofread chunk {Index} failed; using original text", chunkNumber);
                corrected[index] = text;
                _progress.ChunkCompleted(jobId, chunkNumber, chunks.Count);
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = Enumerable.Range(0, chunks.Count).Select(ProcessChunk).ToArray();
        await Task.WhenAll(tasks);

        var merged = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            merged.Append(corrected[i]);
            if (i < chunks.Count - 1 && !string.IsNullOrEmpty(chunks[i].SeparatorAfter))
                merged.Append(chunks[i].SeparatorAfter);
        }
        var mergedResultText = StripTextToCorrectMarkers(merged.ToString());

        _logger.LogInformation("Proofread merge complete: merged length {Len} chars", mergedResultText.Length);
        _progress.SetStatus(jobId, AnalysisProgressStatus.Succeeded, "Proofread finished");

        var noChangesHint = IsProofreadResultNearlyIdentical(inputText, mergedResultText);
        if (noChangesHint)
            _logger.LogWarning("Proofread (chunked) merged result nearly identical to input (input={InputLen} chars, result={ResultLen} chars).", inputText.Length, mergedResultText.Length);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, AnalysisType.Proofread, ct);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = AnalysisType.Proofread,
            Type = nameof(AnalysisType.Proofread),
            PromptUsed = TruncateForAudit(
                representativeInstruction
                ?? (customPrompt ?? _promptFactory.GetAnalysisPrompt(AnalysisType.Proofread, language, context))),
            ResultText = mergedResultText,
            StructuredResult = null,
            Language = language,
            ModelName = "chunked",
            ProofreadNoChangesHint = noChangesHint,
            JobId = jobId,
            SourceTextSnapshot = TextNormalization.NormalizeTextForAnalysis(inputText)
        };

        AttachSuggestions(result, inputText, AnalysisType.Proofread, structuredJson: null, cleanContent: mergedResultText, isStreaming: false, isRunWithInput: false, applyProofreadHeuristics: false);

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Analysis {Id} persisted (Proofread chunked, {Scope})", result.Id, scope);
        return result;
    }

    // ─── Target Resolution ──────────────────────────────────────────

    private Task<(string InputText, Guid? BookId, Guid? ChapterId, Guid? SceneId)> ResolveTarget(
        AnalysisScope scope, Guid targetId, CancellationToken ct)
    {
        throw new NotSupportedException("ResolveTarget is obsolete. Use IAnalysisContextService.BuildContextAsync instead.");
    }

    private Task<(string, Guid?, Guid?, Guid?)> ResolveChapter(Guid chapterId, CancellationToken ct)
    {
        throw new NotSupportedException("ResolveChapter is obsolete. Use IAnalysisContextService.BuildContextAsync instead.");
    }

    private Task<(string, Guid?, Guid?, Guid?)> ResolveScene(Guid sceneId, CancellationToken ct)
    {
        throw new NotSupportedException("ResolveScene is obsolete. Use IAnalysisContextService.BuildContextAsync instead.");
    }

    private Task<(string, Guid?, Guid?, Guid?)> ResolveBook(Guid bookId, CancellationToken ct)
    {
        throw new NotSupportedException("ResolveBook is obsolete. Use IAnalysisContextService.BuildContextAsync instead.");
    }

    // ─── Structured Output Parsing ──────────────────────────────────

    private string? TryParseStructured(AnalysisType type, string content)
    {
        if (type == AnalysisType.LineEdit)
        {
            var result = TryExtractAndReserializeWithLogging<LineEditResult>(content, AnalysisType.LineEdit);
            if (result != null) return result;

            // Aggressive retry: strip all markdown fences/formatting, bidi, then try direct deserialize
            result = TryLineEditAggressiveParse(content);
            if (result != null)
            {
                _logger.LogInformation("LineEdit aggressive parse fallback succeeded after primary parse failed.");
                return result;
            }

            // Final fallback: salvage truncated JSON by keeping only fully-closed suggestion objects
            result = SalvageTruncatedLineEditJson(content);
            if (result != null)
                _logger.LogInformation("LineEdit truncation salvage succeeded: recovered partial suggestions from truncated JSON.");
            if (result != null)
                return result;

            // XML-like fallback: when the model returns a structured but non-JSON response
            // (e.g. <edit><instruction>...</instruction></edit>), salvage it into a minimal
            // LineEditResult with only OverallFeedback populated so the user still sees
            // high-level feedback instead of an empty result.
            var xmlFallback = TryLineEditXmlFallback(content);
            if (xmlFallback != null)
            {
                _logger.LogInformation(
                    "LineEdit XML fallback produced OverallFeedback from non-JSON structured output.");
                return xmlFallback;
            }

            _logger.LogWarning(
                "LineEdit structured parse: all extraction methods failed (primary, aggressive, salvage, XML). Content length={Len}, preview={Preview}",
                content.Length,
                TruncateForAudit(content, 200));
            return null;
        }

        return type switch
        {
            AnalysisType.LinguisticAnalysis => TryExtractAndReserialize<LinguisticAnalysisResult>(content),
            AnalysisType.LiteraryAnalysis => TryExtractAndReserialize<LiteraryAnalysisResult>(content),
            AnalysisType.BookOverview => TryExtractAndReserialize<BookOverviewResult>(content),
            AnalysisType.CharacterAnalysis => TryExtractAndReserialize<CharacterAnalysisResult>(content),
            AnalysisType.StoryAnalysis => TryExtractAndReserialize<StoryAnalysisResult>(content),
            AnalysisType.QA => TryExtractAndReserialize<QAResult>(content),
            _ => null
        };
    }

    /// <summary>
    /// Aggressive fallback for LineEdit: strip all markdown fences, bidi controls, and
    /// surrounding text, then attempt case-insensitive deserialization directly.
    /// </summary>
    private string? TryLineEditAggressiveParse(string content)
    {
        try
        {
            // Strip markdown fence markers only (marker + optional language tag),
            // not the rest of the line — safe regardless of newline presence.
            var stripped = Regex.Replace(content, @"```[a-zA-Z]*[ \t]*\n?", "");
            stripped = StripBomAndBidiWrapper(stripped);

            stripped = Regex.Replace(stripped, @"^[#*>`~\-]+\s?", "", RegexOptions.Multiline);

            // Remove bidi controls that may be interspersed in JSON structure
            stripped = StripBidiControls(stripped);

            var json = ExtractJsonByBraceMatching(stripped);
            if (json == null) return null;

            var parsed = JsonSerializer.Deserialize<LineEditResult>(json, JsonOpts);
            if (parsed == null) return null;

            return JsonSerializer.Serialize(parsed, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Last-resort salvage for truncated LineEdit JSON. Locates the "suggestions" array,
    /// walks the content tracking bracket/brace depth, finds the last fully-closed suggestion
    /// object, then reconstructs valid JSON keeping only complete suggestions.
    /// Mirrors the frontend trySalvageTruncatedLineEditJson logic.
    /// </summary>
    internal static string? SalvageTruncatedLineEditJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        // Strip markdown fence markers safely (marker + optional language tag only)
        var stripped = Regex.Replace(content, @"```[a-zA-Z]*[ \t]*\n?", "");
        stripped = StripBomAndBidiWrapper(stripped);
        stripped = StripBidiControls(stripped);

        var keyIndex = stripped.IndexOf("\"suggestions\"", StringComparison.Ordinal);
        if (keyIndex < 0) return null;

        var arrayStart = stripped.IndexOf('[', keyIndex);
        if (arrayStart < 0) return null;

        bool inString = false;
        bool escape = false;
        int depthCurly = 0;
        int depthSquare = 0;
        int lastObjectEnd = -1;

        for (int i = arrayStart; i < stripped.Length; i++)
        {
            char c = stripped[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '[') depthSquare++;
            else if (c == ']') depthSquare--;
            else if (c == '{') depthCurly++;
            else if (c == '}')
            {
                depthCurly--;
                if (depthSquare == 1 && depthCurly == 0)
                    lastObjectEnd = i;
            }
        }

        if (lastObjectEnd < 0) return null;

        // Reconstruct: everything up to and including '[', then the closed objects, then close array + root
        var head = stripped[..(arrayStart + 1)];
        var body = stripped[(arrayStart + 1)..(lastObjectEnd + 1)];
        var salvaged = $"{head}{body}]}}";

        try
        {
            var parsed = JsonSerializer.Deserialize<LineEditResult>(salvaged, JsonOpts);
            if (parsed?.Suggestions == null || parsed.Suggestions.Count == 0)
                return null;
            return JsonSerializer.Serialize(parsed, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fallback for clearly non-JSON but still structured LineEdit responses, such as
    /// XML-like wrappers (&lt;edit&gt;&lt;instruction&gt;...&lt;/instruction&gt;&lt;/edit&gt;).
    /// Strips tags and whitespace and returns a minimal LineEditResult JSON with
    /// OverallFeedback populated and an empty suggestions array.
    /// Returns null when the content does not look like a tagged/markup payload.
    /// </summary>
    internal static string? TryLineEditXmlFallback(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var trimmed = content.Trim();
        // Heuristic: require at least one opening and one closing angle bracket so we don't
        // treat plain Hebrew/English prose as XML/HTML.
        if (!trimmed.Contains('<') || !trimmed.Contains('>'))
            return null;

        // Best-effort strip of XML/HTML-ish tags.
        var withoutTags = Regex.Replace(trimmed, "<[^>]+>", " ");
        var normalized = Regex.Replace(withoutTags, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var fallback = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>(),
            OverallFeedback = normalized
        };

        return JsonSerializer.Serialize(fallback, JsonOpts);
    }

    private static string? TryExtractAndReserialize<T>(string content) where T : class
    {
        try
        {
            var json = ExtractJson(content);
            if (json == null) return null;

            var parsed = JsonSerializer.Deserialize<T>(json, JsonOpts);
            if (parsed == null) return null;

            return JsonSerializer.Serialize(parsed, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// LineEdit-specific wrapper that adds diagnostics around JSON extraction and deserialization.
    /// Other analysis types continue to use the generic TryExtractAndReserialize without logging.
    /// </summary>
    private string? TryExtractAndReserializeWithLogging<T>(string content, AnalysisType analysisType) where T : class
    {
        string? preview = null;
        try
        {
            var json = ExtractJson(content);
            if (json == null)
            {
                if (analysisType == AnalysisType.LineEdit)
                {
                    preview = TruncateForAudit(content, 200);
                    // Debug: fallback parsers (aggressive, salvage) will retry
                    _logger.LogDebug(
                        "LineEdit primary parse: no JSON block extracted. Content preview={Preview}",
                        preview);
                }
                return null;
            }

            preview = TruncateForAudit(json, 200);

            var parsed = JsonSerializer.Deserialize<T>(json, JsonOpts);
            if (parsed == null)
            {
                if (analysisType == AnalysisType.LineEdit)
                {
                    _logger.LogDebug(
                        "LineEdit primary parse: deserialized object was null. Json preview={Preview}",
                        preview);
                }
                return null;
            }

            return JsonSerializer.Serialize(parsed, JsonOpts);
        }
        catch (JsonException ex)
        {
            if (analysisType == AnalysisType.LineEdit)
            {
                _logger.LogDebug(
                    ex,
                    "LineEdit primary parse: JsonException. Json/Content preview={Preview}",
                    preview ?? TruncateForAudit(content, 200));
            }
            return null;
        }
    }

    /// <summary>
    /// Extract the first top-level JSON object or array from LLM output,
    /// which may contain markdown fences or surrounding text.
    /// </summary>
    internal static string? ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        content = StripBomAndBidiWrapper(content);

        // Match fenced blocks with any language tag (json, text, etc.); the tag is
        // excluded from the capture so we get only the inner content.
        var fenceMatch = Regex.Match(content, @"```\w*[ \t]*\n?([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success)
        {
            var inner = StripBomAndBidiWrapper(fenceMatch.Groups[1].Value.Trim());
            inner = StripBidiControls(inner);
            if (inner.Length > 0 && (inner[0] == '{' || inner[0] == '['))
                return inner;
        }

        var extracted = ExtractJsonByBraceMatching(content);
        if (extracted != null) return extracted;

        // Second pass: strip markdown formatting (bold, headers) and retry
        var stripped = Regex.Replace(content, @"[*#`~]+", " ");
        return ExtractJsonByBraceMatching(stripped);
    }

    /// <summary>
    /// Strip BOM, leading/trailing whitespace, and Unicode bidi/RTL control characters
    /// that appear outside JSON boundaries (common with Hebrew LLM output).
    /// </summary>
    private static string StripBomAndBidiWrapper(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Strip BOM (U+FEFF)
        text = text.TrimStart('\uFEFF');

        // Strip leading/trailing bidi control characters that may surround the JSON:
        // RLM U+200F, LRM U+200E, RLE U+202B, LRE U+202A, PDF U+202C,
        // RLO U+202E, LRO U+202D, LRI U+2066, RLI U+2067, FSI U+2068, PDI U+2069
        ReadOnlySpan<char> bidiControls = stackalloc char[]
        {
            '\u200E', '\u200F',
            '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
            '\u2066', '\u2067', '\u2068', '\u2069'
        };

        var span = text.AsSpan().Trim();
        while (span.Length > 0 && bidiControls.Contains(span[0]))
            span = span[1..];
        while (span.Length > 0 && bidiControls.Contains(span[^1]))
            span = span[..^1];

        return span.ToString().Trim();
    }

    private static string? ExtractJsonByBraceMatching(string content)
    {
        int searchFrom = 0;
        while (searchFrom < content.Length)
        {
            var start = content.IndexOfAny(new[] { '{', '[' }, searchFrom);
            if (start < 0) return null;

            // For objects, the first non-whitespace/non-bidi char after '{' must be '"' or '}'
            // so we reject Hebrew prose in braces like {רוברט הסיט...}
            if (content[start] == '{')
            {
                var peek = start + 1;
                while (peek < content.Length && (char.IsWhiteSpace(content[peek]) || IsBidiOrZeroWidth(content[peek])))
                    peek++;
                if (peek >= content.Length || (content[peek] != '"' && content[peek] != '}'))
                {
                    searchFrom = start + 1;
                    continue;
                }
            }

            char open = content[start];
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = start; i < content.Length; i++)
            {
                char c = content[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == open) depth++;
                else if (c == close) depth--;
                if (depth == 0)
                    return content.Substring(start, i - start + 1);
            }

            // Unbalanced from this position; try next occurrence
            searchFrom = start + 1;
        }

        return null;
    }

    private static bool IsBidiOrZeroWidth(char c) =>
        c is '\u200E' or '\u200F'
            or (>= '\u202A' and <= '\u202E')
            or (>= '\u2066' and <= '\u2069')
            or '\uFEFF' or '\u200B' or '\u200C' or '\u200D';

    /// <summary>Strip all Unicode bidi/RTL control characters from text.</summary>
    private static string StripBidiControls(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Regex.Replace(text, @"[\u200E\u200F\u200B-\u200D\u202A-\u202E\u2066-\u2069\uFEFF]", "");
    }

    // ─── Mapping helpers ────────────────────────────────────────────

    private static AiTaskType MapToTaskType(AnalysisType analysisType) => analysisType switch
    {
        AnalysisType.Proofread => AiTaskType.Proofread,
        AnalysisType.LineEdit => AiTaskType.LineEdit,
        AnalysisType.LinguisticAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.LiteraryAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.Summarization => AiTaskType.Summarization,
        AnalysisType.BookOverview => AiTaskType.LinguisticAnalysis,
        AnalysisType.Synopsis => AiTaskType.Summarization,
        AnalysisType.CharacterAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.StoryAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.QA => AiTaskType.GenericChat,
        AnalysisType.Custom => AiTaskType.GenericChat,
        _ => AiTaskType.GenericChat
    };

    /// <summary>
    /// For LineEdit, replace resultText with overallFeedback from the structured parse
    /// so the generic text display shows a human-readable summary instead of raw JSON.
    /// Only applies when the structured parse succeeded.
    /// </summary>
    private static string MaybeReplaceLineEditResultText(AnalysisType analysisType, string? structuredJson, string cleanContent)
    {
        if (analysisType != AnalysisType.LineEdit || structuredJson is null) return cleanContent;

        try
        {
            var parsed = JsonSerializer.Deserialize<LineEditResult>(structuredJson, JsonOpts);
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.OverallFeedback))
                return parsed.OverallFeedback;
        }
        catch (JsonException)
        {
            // Structured JSON was already validated; ignore any unlikely failure here
        }
        return cleanContent;
    }

    // ─── Sanitization ───────────────────────────────────────────────

    private static string SanitizeResponse(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = StripThinkBlock(text);
        text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(text);
        text = Regex.Replace(text, @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", " ");
        text = StripCjk(text);
        return text;
    }

    /// <summary>Remove DictaLM/LLM thinking block so only the final answer is used (e.g. for Proofread).</summary>
    private static string StripThinkBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        const string open = "<think>";
        const string close = "</think>";
        var openIdx = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (openIdx < 0) return text;
        var closeIdx = text.IndexOf(close, openIdx + open.Length, StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0) return text;
        var afterClose = closeIdx + close.Length;
        var trimmed = text[afterClose..].TrimStart();
        // Prefer text after the thinking block (the model's final answer)
        if (trimmed.Length > 0) return trimmed;
        // Fallback: some Thinking models put the actual answer inside the block; use that so we don't return raw <think>
        var inner = text.Substring(openIdx + open.Length, closeIdx - openIdx - open.Length).Trim();
        return inner.Length > 0 ? inner : text;
    }

    /// <summary>Get the text after think-close tag without other sanitization. Used when full sanitization leaves Proofread empty.</summary>
    private static string? ExtractTextAfterThinkBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        const string close = "</think>";
        var closeIdx = text.IndexOf(close, StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0) return text.Trim();
        var after = text[(closeIdx + close.Length)..].Trim();
        return after.Length > 0 ? after : null;
    }

    private static string StripCjk(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var stripped = Regex.Replace(text, @"[\u4e00-\u9fff\u3000-\u303f]+", " ");
        // Collapse horizontal whitespace only; preserve line breaks so downstream
        // markdown-fence regexes (```json ... ```) still work correctly.
        stripped = Regex.Replace(stripped, @"[^\S\n]+", " ");
        return Regex.Replace(stripped, @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>
    /// Strip internal proofread wrapper markers such as [TEXT_TO_CORRECT]...[/TEXT_TO_CORRECT]
    /// so they never reach persisted ResultText or diff computation.
    /// </summary>
    private static string StripTextToCorrectMarkers(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("[TEXT_TO_CORRECT]", string.Empty, StringComparison.Ordinal)
            .Replace("[/TEXT_TO_CORRECT]", string.Empty, StringComparison.Ordinal);
    }

    private static string TruncateForAudit(string? prompt, int max = 500) =>
        string.IsNullOrEmpty(prompt) ? "" : prompt.Length <= max ? prompt : prompt[..max] + "…";

    /// <summary>Rough token estimate for logging (chars / 4 is a common heuristic).</summary>
    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
    }

    /// <summary>
    /// Archive the previous Active analysis for the same (BookId, ChapterId, SceneId, Scope, AnalysisType),
    /// and mark any pending suggestions as Superseded.
    /// </summary>
    private async Task ArchivePreviousActiveAsync(
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        AnalysisScope scope,
        AnalysisType analysisType,
        CancellationToken ct)
    {
        if (!chapterId.HasValue)
            return;

        var previous = await _db.AnalysisResults
            .Include(a => a.Suggestions)
            .Where(a =>
                a.ChapterId == chapterId.Value &&
                a.BookId == bookId &&
                a.SceneId == sceneId &&
                a.Scope == scope &&
                a.AnalysisType == analysisType &&
                a.Status == AnalysisStatus.Active)
            .ToListAsync(ct);

        if (previous.Count == 0)
            return;

        foreach (var analysis in previous)
        {
            analysis.Status = AnalysisStatus.Archived;
            foreach (var suggestion in analysis.Suggestions.Where(s => s.Outcome == null))
            {
                suggestion.Outcome = SuggestionOutcome.Superseded;
            }
        }
    }

    /// <summary>True when Proofread result is nearly identical to input (normalize whitespace then compare). Indicates possible truncation or model echo.</summary>
    private static bool IsProofreadResultNearlyIdentical(string input, string result)
    {
        if (string.IsNullOrEmpty(result)) return true;
        var a = Regex.Replace(input.Trim(), @"\s+", " ");
        var b = Regex.Replace(result.Trim(), @"\s+", " ");
        if (a.Length == 0 && b.Length == 0) return true;
        if (a.Length == 0 || b.Length == 0) return false;
        // Consider "nearly identical" if the shorter is a prefix of the longer (truncation) or similarity is very high
        var minLen = Math.Min(a.Length, b.Length);
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen > 0 && (double)minLen / maxLen < 0.95) return false;
        var match = 0;
        for (var i = 0; i < minLen; i++)
            if (a[i] == b[i]) match++;
        return (double)match / minLen >= 0.98;
    }

    /// <summary>True when the proofread result looks like new/continuation content rather than a correction of the input (e.g. model wrote "Chapter 12" or "הנה המשך לסיפור").</summary>
    private static bool IsProofreadResultUnrelated(string input, string result)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(result)) return false;
        var inputStart = Regex.Replace(input.TrimStart(), @"\s+", " ").Trim();
        var resultStart = Regex.Replace(result.TrimStart(), @"\s+", " ").Trim();
        if (inputStart.Length < 30 || resultStart.Length < 30) return false;
        // Result should start with something very similar to the input (allowing small corrections). Take first ~150 chars of each.
        var inputPrefix = inputStart.Length <= 150 ? inputStart : inputStart.Substring(0, 150);
        var resultPrefix = resultStart.Length <= 200 ? resultStart : resultStart.Substring(0, 200);
        // If the first 50 chars of input don't appear in the first 100 chars of result, it's likely unrelated.
        var inputFirst50 = inputPrefix.Length >= 50 ? inputPrefix.Substring(0, 50) : inputPrefix;
        if (!resultPrefix.Contains(inputFirst50, StringComparison.Ordinal))
        {
            // Also treat known "continuation" phrases as invalid proofread output.
            var continuationMarkers = new[] { "הנה המשך לסיפור", "הנה המשך", "פרק 12", "**פרק 12", "Chapter 12" };
            foreach (var marker in continuationMarkers)
                if (resultStart.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            return true;
        }
        return false;
    }

    private void AttachSuggestions(
        AnalysisResult result,
        string inputText,
        AnalysisType analysisType,
        string? structuredJson,
        string cleanContent,
        bool isStreaming,
        bool isRunWithInput,
        bool applyProofreadHeuristics)
    {
        if (analysisType == AnalysisType.Proofread)
        {
            if (applyProofreadHeuristics)
            {
                var noChanges = IsProofreadResultNearlyIdentical(inputText, cleanContent);
                var invalidResult = IsProofreadResultUnrelated(inputText, cleanContent);
                if (invalidResult)
                {
                    var contextLabel = isStreaming
                        ? "Proofread (streaming)"
                        : isRunWithInput
                            ? "Proofread result (RunWithInputAsync)"
                            : "Proofread result";
                    _logger.LogWarning(
                        "{ContextLabel} appears to be unrelated to input (e.g. model wrote new content). Treating as no changes and persisting original text. Input length={InputLen}, result preview={Preview}",
                        contextLabel,
                        inputText.Length,
                        TruncateForAudit(cleanContent, 150));
                    cleanContent = inputText;
                    noChanges = true;
                }

                result.ProofreadNoChangesHint = noChanges;
                result.ResultText = cleanContent;

                if (noChanges)
                {
                    var contextLabel = isStreaming
                        ? "Proofread (streaming)"
                        : isRunWithInput
                            ? "Proofread result (RunWithInputAsync)"
                            : "Proofread result";
                    _logger.LogWarning(
                        "{ContextLabel} is nearly identical to input (input={InputLen} chars, result={ResultLen} chars). Model may have hit a length limit or failed—suggest user try a shorter section.",
                        contextLabel,
                        inputText.Length,
                        cleanContent.Length);
                }
            }

            var suggestions = _suggestionDiff.ComputeProofreadSuggestions(inputText, result.ResultText);
            for (var i = 0; i < suggestions.Count; i++)
            {
                suggestions[i].OrderIndex = i;
                suggestions[i].CreatedAt = DateTimeOffset.UtcNow;
                result.Suggestions.Add(suggestions[i]);
            }
        }
        else if (analysisType == AnalysisType.LineEdit && structuredJson is not null)
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<LineEditResult>(structuredJson, JsonOpts);
                if (parsed is not null)
                {
                    // Strip no-op suggestions before diff computation (safety net)
                    var preFilterCount = parsed.Suggestions.Count;
                    parsed.Suggestions.RemoveAll(s => IsNoOpSuggestion(s));
                    if (preFilterCount > parsed.Suggestions.Count)
                        _logger.LogInformation("LineEdit AttachSuggestions: filtered {Count} no-op suggestions", preFilterCount - parsed.Suggestions.Count);

                    var suggestions = _suggestionDiff.ComputeLineEditSuggestions(parsed, inputText);
                    if (suggestions.Count == 0)
                    {
                        _logger.LogWarning(
                            "LineEdit AttachSuggestions produced zero suggestions after successful structured parse. Input length={InputLength}, structuredResult preview={Preview}",
                            inputText?.Length ?? 0,
                            TruncateForAudit(structuredJson, 200));
                    }

                    for (var i = 0; i < suggestions.Count; i++)
                    {
                        suggestions[i].OrderIndex = i;
                        suggestions[i].CreatedAt = DateTimeOffset.UtcNow;
                        result.Suggestions.Add(suggestions[i]);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "LineEdit AttachSuggestions: structured JSON deserialized to null LineEditResult. structuredResult preview={Preview}",
                        TruncateForAudit(structuredJson, 200));
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "LineEdit AttachSuggestions: failed to deserialize structuredResult into LineEditResult. structuredResult preview={Preview}",
                    TruncateForAudit(structuredJson, 200));
                // Ignore malformed structured result; we still persist raw text.
            }
        }
    }

    /// <summary>
    /// Explain why a specific suggestion was made. Uses LLM with a focused prompt and caches
    /// the explanation on the suggestion row. Centralized here so controllers do not depend
    /// directly on IAiRouter / PromptFactory.
    /// </summary>
    public async Task<string?> ExplainSuggestionAsync(
        Guid bookId,
        Guid chapterId,
        Guid suggestionId,
        CancellationToken ct = default)
    {
        var suggestion = await _db.AnalysisSuggestions
            .Include(s => s.AnalysisResult)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct);

        if (suggestion == null ||
            suggestion.AnalysisResult.ChapterId != chapterId ||
            suggestion.AnalysisResult.BookId != bookId)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(suggestion.Explanation))
        {
            return suggestion.Explanation;
        }

        var language = string.IsNullOrWhiteSpace(suggestion.AnalysisResult.Language)
            ? "he"
            : suggestion.AnalysisResult.Language;

        var prompt = _promptFactory.GetExplainSuggestionPrompt(
            suggestion.OriginalText,
            suggestion.SuggestedText,
            suggestion.Reason,
            language);

        var request = new AiRequest
        {
            InputText = suggestion.OriginalText,
            Instruction = prompt,
            TaskType = AiTaskType.GenericChat,
            Language = language,
            SourceId = suggestion.Id.ToString()
        };

        var response = await _router.CompleteAsync(request, ct);
        var explanation = (response.Content ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(explanation))
        {
            explanation = "No explanation could be generated for this suggestion.";
        }

        suggestion.Explanation = explanation;
        await _db.SaveChangesAsync(ct);

        return explanation;
    }
}
