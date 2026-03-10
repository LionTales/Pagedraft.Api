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
        IAnalysisContextService contextService)
    {
        _db = db;
        _router = router;
        _promptFactory = promptFactory;
        _sfdtConversion = sfdtConversion;
        _aiOptions = aiOptions;
        _logger = logger;
        _progress = progress;
        _contextService = contextService;
    }

    /// <summary>Max characters for a single proofread request. Longer text often causes the model to truncate or generate new content instead of correcting.</summary>
    private const int MaxProofreadInputLength = 10_000;

    /// <summary>Run an analysis and persist the result.</summary>
    /// <param name="jobId">
    /// Optional analysis job identifier for long-running operations (currently chunked Proofread).
    /// When provided and the run uses chunked proofread, this jobId will be used for progress tracking and persisted on AnalysisResult.
    /// When null, a new jobId is generated internally for chunked proofread.
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
            var chunkTargetWords = opts.ProofreadChunkTargetWords > 0 ? opts.ProofreadChunkTargetWords : 500;
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

        var taskType = MapToTaskType(analysisType);
        var instruction = customPrompt
            ?? _promptFactory.GetAnalysisPrompt(analysisType, language, context);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = targetId.ToString()
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
            ModelName = $"{response.Provider}:{response.Model}"
        };

        if (analysisType == AnalysisType.Proofread)
        {
            var noChanges = IsProofreadResultNearlyIdentical(inputText, cleanContent);
            var invalidResult = IsProofreadResultUnrelated(inputText, cleanContent);
            if (invalidResult)
            {
                _logger.LogWarning("Proofread result appears to be unrelated to input (e.g. model wrote new content). Treating as no changes and persisting original text. Input length={InputLen}, result preview={Preview}", inputText.Length, TruncateForAudit(cleanContent, 150));
                cleanContent = inputText;
                noChanges = true;
            }
            result.ProofreadNoChangesHint = noChanges;
            result.ResultText = cleanContent;
            if (noChanges)
                _logger.LogWarning("Proofread result is nearly identical to input (input={InputLen} chars, result={ResultLen} chars). Model may have hit a length limit or failed—suggest user try a shorter section.", inputText.Length, cleanContent.Length);
        }

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
            SourceId = bookId?.ToString() ?? chapterId?.ToString() ?? sceneId?.ToString() ?? ""
        };

        _logger.LogInformation("Running {Scope}/{Type} with provided input", scope, analysisType);
        var response = await _router.CompleteAsync(request, ct);

        var cleanContent = SanitizeResponse(response.Content);
        var structuredJson = TryParseStructured(analysisType, cleanContent);

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
            ModelName = $"{response.Provider}:{response.Model}"
        };

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
            SourceId = targetId.ToString()
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
            ModelName = "stream"
        };

        if (analysisType == AnalysisType.Proofread)
        {
            var noChanges = IsProofreadResultNearlyIdentical(inputText, cleanContent);
            var invalidResult = IsProofreadResultUnrelated(inputText, cleanContent);
            if (invalidResult)
            {
                _logger.LogWarning("Proofread (streaming) result appears unrelated to input. Persisting original text.");
                cleanContent = inputText;
                noChanges = true;
            }
            result.ProofreadNoChangesHint = noChanges;
            result.ResultText = cleanContent;
            if (noChanges)
                _logger.LogWarning("Proofread (streaming) result nearly identical to input (input={InputLen} chars, result={ResultLen} chars). Model may have hit a length limit.", inputText.Length, cleanContent.Length);
        }

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
            Language = language
        };

        var response = await _router.CompleteAsync(request, ct);
        return SanitizeResponse(response.Content);
    }

    // ─── Proofread chunking (paragraph/sentence aware) ───────────────

    /// <summary>Structured chunk for proofread with merge separator and soft overlap context.</summary>
    private sealed record ProofreadChunk(string Text, string SeparatorAfter, string? OverlapPrefix, string? OverlapSuffix);

    /// <summary>
    /// Chunk text for proofread: split by paragraphs then sentences, ~targetWords per chunk,
    /// with dialogue-aware grouping and soft overlaps. Returns chunks with:
    /// - Text: the chunk to correct
    /// - SeparatorAfter: separator to append after this chunk when merging
    /// - OverlapPrefix: trailing sentences from previous chunk (read-only [CONTEXT_BEFORE])
    /// - OverlapSuffix: leading sentences from next chunk (reserved for future use)
    /// </summary>
    private static List<ProofreadChunk> ChunkForProofread(string fullText, int targetWordsPerChunk)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return new List<ProofreadChunk> { new("", "", null, null) };
        if (targetWordsPerChunk <= 0)
            return new List<ProofreadChunk> { new(fullText.Trim(), "", null, null) };

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
            return new List<ProofreadChunk> { new("", "", null, null) };

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
                // End of dialogue block – subsequent non-dialogue segments start a new block
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

        // Post-process: compute soft overlaps from neighboring chunks
        var result = new List<ProofreadChunk>(baseChunks.Count);
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
                var leading = ExtractLeadingSentences(baseChunks[i + 1].Text, 3);
                overlapSuffix = string.IsNullOrWhiteSpace(leading) ? null : leading;
            }

            result.Add(new ProofreadChunk(text, sep, overlapPrefix, overlapSuffix));
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
        var baseInstruction = customPrompt ?? _promptFactory.GetAnalysisPrompt(AnalysisType.Proofread, language);
        var taskType = MapToTaskType(AnalysisType.Proofread);
        var chunks = ChunkForProofread(inputText, chunkTargetWords);

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
                var wrappedText = $"[TEXT_TO_CORRECT]{text}[/TEXT_TO_CORRECT]";

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
        var mergedResultText = merged.ToString();

        _logger.LogInformation("Proofread merge complete: merged length {Len} chars", mergedResultText.Length);
        _progress.SetStatus(jobId, AnalysisProgressStatus.Succeeded, "Proofread finished");

        var noChangesHint = IsProofreadResultNearlyIdentical(inputText, mergedResultText);
        if (noChangesHint)
            _logger.LogWarning("Proofread (chunked) merged result nearly identical to input (input={InputLen} chars, result={ResultLen} chars).", inputText.Length, mergedResultText.Length);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = AnalysisType.Proofread,
            Type = nameof(AnalysisType.Proofread),
            PromptUsed = TruncateForAudit(baseInstruction),
            ResultText = mergedResultText,
            StructuredResult = null,
            Language = language,
            ModelName = "chunked",
            ProofreadNoChangesHint = noChangesHint,
            JobId = jobId
        };

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Analysis {Id} persisted (Proofread chunked, {Scope})", result.Id, scope);
        return result;
    }

    // ─── Target Resolution ──────────────────────────────────────────

    private async Task<(string InputText, Guid? BookId, Guid? ChapterId, Guid? SceneId)> ResolveTarget(
        AnalysisScope scope, Guid targetId, CancellationToken ct)
    {
        return scope switch
        {
            AnalysisScope.Chapter => await ResolveChapter(targetId, ct),
            AnalysisScope.Scene => await ResolveScene(targetId, ct),
            AnalysisScope.Book => await ResolveBook(targetId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
    }

    private async Task<(string, Guid?, Guid?, Guid?)> ResolveChapter(Guid chapterId, CancellationToken ct)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct)
            ?? throw new InvalidOperationException("Chapter not found");

        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No chapter text to analyze. Save the chapter first so the analysis has content.");

        return (text, chapter.BookId, chapterId, null);
    }

    private async Task<(string, Guid?, Guid?, Guid?)> ResolveScene(Guid sceneId, CancellationToken ct)
    {
        var scene = await _db.Scenes.Include(s => s.Chapter).FirstOrDefaultAsync(s => s.Id == sceneId, ct)
            ?? throw new InvalidOperationException("Scene not found");

        var sfdt = scene.ContentSfdt ?? "{}";
        var (plainText, _) = _sfdtConversion.GetTextFromSfdt(sfdt);
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(plainText);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Scene has no content to analyze. Edit the scene and save first.");

        return (text, scene.Chapter.BookId, scene.ChapterId, sceneId);
    }

    private async Task<(string, Guid?, Guid?, Guid?)> ResolveBook(Guid bookId, CancellationToken ct)
    {
        var chapters = await _db.Chapters
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .ToListAsync(ct);

        if (chapters.Count == 0)
            throw new InvalidOperationException("Book has no chapters to analyze.");

        var sb = new StringBuilder();
        foreach (var ch in chapters)
        {
            var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(ch.ContentText ?? "");
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine($"## {ch.Title}");
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }

        return (sb.ToString(), bookId, null, null);
    }

    // ─── Structured Output Parsing ──────────────────────────────────

    private static string? TryParseStructured(AnalysisType type, string content)
    {
        return type switch
        {
            AnalysisType.LineEdit => TryExtractAndReserialize<LineEditResult>(content),
            AnalysisType.LinguisticAnalysis => TryExtractAndReserialize<LinguisticAnalysisResult>(content),
            AnalysisType.LiteraryAnalysis => TryExtractAndReserialize<LiteraryAnalysisResult>(content),
            AnalysisType.BookOverview => TryExtractAndReserialize<BookOverviewResult>(content),
            AnalysisType.CharacterAnalysis => TryExtractAndReserialize<CharacterAnalysisResult>(content),
            AnalysisType.StoryAnalysis => TryExtractAndReserialize<StoryAnalysisResult>(content),
            AnalysisType.QA => TryExtractAndReserialize<QAResult>(content),
            _ => null
        };
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
    /// Extract the first top-level JSON object or array from LLM output,
    /// which may contain markdown fences or surrounding text.
    /// </summary>
    private static string? ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var fenceMatch = Regex.Match(content, @"```(?:json)?\s*\n?([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success)
        {
            var inner = fenceMatch.Groups[1].Value.Trim();
            if (inner.Length > 0 && (inner[0] == '{' || inner[0] == '['))
                return inner;
        }

        var start = content.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return null;

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

        return null;
    }

    // ─── Mapping helpers ────────────────────────────────────────────

    private static AiTaskType MapToTaskType(AnalysisType analysisType) => analysisType switch
    {
        AnalysisType.Proofread => AiTaskType.Proofread,
        AnalysisType.LineEdit => AiTaskType.Proofread,
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

    // ─── Sanitization ───────────────────────────────────────────────

    private static string SanitizeResponse(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = StripThinkBlock(text);
        text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(text);
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
        return Regex.Replace(stripped, @"[ \t\r\n]+", " ").Trim();
    }

    private static string TruncateForAudit(string? prompt, int max = 500) =>
        string.IsNullOrEmpty(prompt) ? "" : prompt.Length <= max ? prompt : prompt[..max] + "…";

    /// <summary>Rough token estimate for logging (chars / 4 is a common heuristic).</summary>
    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
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
}
