using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
    private readonly Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker _ktivMaleChecker;
    private readonly AnalysisRepairService _analysisRepair;
    private readonly DynamicTermRepairService _dynamicTermRepair;
    private readonly IBookEntityProvider _bookEntityProvider;

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
        SuggestionDiffService suggestionDiff,
        Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker ktivMaleChecker,
        AnalysisRepairService analysisRepair,
        DynamicTermRepairService dynamicTermRepair,
        IBookEntityProvider bookEntityProvider)
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
        _ktivMaleChecker = ktivMaleChecker;
        _analysisRepair = analysisRepair;
        _dynamicTermRepair = dynamicTermRepair;
        _bookEntityProvider = bookEntityProvider;
    }

    /// <summary>Max characters for a single proofread request. Longer text often causes the model to truncate or generate new content instead of correcting.</summary>
    private const int MaxProofreadInputLength = 10_000;

    /// <summary>
    /// THE BOOK -> TIER LOOKUP (model-tier-fast-thinking plan, p3-2). The tier is stored per BOOK, and
    /// <see cref="AiRequest.Tier"/> is a stamped value rather than something the router derives, so exactly
    /// one place in this service turns a book id into a tier and every AiRequest it builds carries the
    /// result.
    ///
    /// p3-3 moved the implementation to the shared <see cref="BookAiTierResolver"/>, because
    /// <see cref="AnalysisContextService"/> and <see cref="StyleBaselineService"/> now need the SAME answer
    /// for the same book: they resolve the ACTIVE model that <c>BuiltWithModel</c> is compared against, and a
    /// disagreement between "the tier that ran" and "the tier the freshness gate assumes" makes every profile
    /// read permanently stale. Fail-safe posture (everything unknown -> Fast) is documented there.
    ///
    /// Resolved ONCE per run rather than per request, so a 40-chunk proofread costs one query, not forty.
    /// be-c03 made that literally true for the CHUNKED runs: <see cref="RunAsync"/> reads the tier once and
    /// PASSES it to <c>RunProofreadChunkedAsync</c> / <c>RunLineEditChunkedAsync</c>, which are private and
    /// have no other caller. Before that each chunked runner re-read it, and the two reads decided different
    /// things - the first the chunk SIZE (whose bound reads the routed provider's num_ctx), the second the
    /// ROUTE - so a flip between them could size for one provider and send to another. That was INERT at the
    /// shipped values (both tiers resolve Proofread at num_ctx 4096) and the fix is structural.
    ///
    /// The other entry points - <c>RunWithInputAsync</c> and <c>RunStreamingAsync</c> - each build a single
    /// request and so read once by construction.
    ///
    /// tier-ux-rework c1: the lookup is PER TASK, and the task passed here is the same
    /// <see cref="AiTaskType"/> the request is stamped with (<c>MapToTaskType</c>), never the user-facing
    /// <c>AnalysisType</c> - so the tier that sized the chunks, the tier stamped on the request, and the tier
    /// the router keys its <c>{task}_{tier}</c> rung on are one value about one task.
    /// </summary>
    private Task<AiTier> ResolveBookTierAsync(Guid? bookId, AiTaskType task, CancellationToken ct)
        => BookAiTierResolver.ResolveAsync(_db, bookId, task, _logger, ct);

    private static (string Outcome, string? Note, double? WordSimilarity) ResolveSingleRunOutcome(
        AnalysisType analysisType,
        string inputText,
        string llmResultText,
        AnalysisResult result,
        string? structuredJson,
        bool? proofreadUnrelatedOverride = null,
        double? proofreadWordSimilarityOverride = null)
    {
        var outcome = "Succeeded";
        string? note = null;
        double? wordSimilarity = null;

        if (analysisType == AnalysisType.Proofread)
        {
            bool unrelated;
            double similarity;

            if (proofreadUnrelatedOverride.HasValue)
            {
                unrelated = proofreadUnrelatedOverride.Value;

                if (proofreadWordSimilarityOverride.HasValue)
                {
                    similarity = proofreadWordSimilarityOverride.Value;
                }
                else if (unrelated)
                {
                    // Caller usually provides similarity together with the unrelated flag.
                    // Fall back to computing it only when we still need it for logging.
                    _ = IsProofreadResultUnrelated(inputText, llmResultText, out similarity);
                }
                else
                {
                    similarity = 0.0;
                }
            }
            else
            {
                unrelated = IsProofreadResultUnrelated(inputText, llmResultText, out similarity);
            }

            if (unrelated)
            {
                outcome = "FallbackUnrelated";
                wordSimilarity = similarity;
                note = $"similarity={similarity:F2}";
            }
            else if (result.ProofreadNoChangesHint)
            {
                // "ProofreadNoChangesHint" means the output is nearly identical (echo/truncation),
                // which is distinct from the chunked repetition-loop heuristic.
                outcome = string.IsNullOrWhiteSpace(llmResultText) ? "FallbackEmpty" : "FallbackNoChanges";
            }
        }
        else
        {
            // For normal LineEdit, StructuredResult existence is our best proxy
            // for whether the model produced valid JSON (chunked uses the same heuristic).
            if (structuredJson is null)
                outcome = string.IsNullOrWhiteSpace(llmResultText) ? "FallbackEmpty" : "FallbackError";
        }

        return (outcome, note, wordSimilarity);
    }

    private static AnalysisChunkOutcome CreateChunkOutcome(
        int chunkIndex,
        string inputText,
        string outputText,
        long durationMs,
        string outcome,
        double? wordSimilarity = null,
        string? note = null)
    {
        return new AnalysisChunkOutcome
        {
            ChunkIndex = chunkIndex,
            InputCharCount = inputText.Length,
            InputWordCount = WordCount(inputText),
            OutputCharCount = outputText.Length,
            DurationMs = durationMs,
            Outcome = outcome,
            WordSimilarity = wordSimilarity,
            Note = note
        };
    }

    private void PersistSingleChunkRunLog(
        Guid jobId,
        AnalysisResult result,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        AnalysisScope scope,
        AnalysisType analysisType,
        string language,
        long durationMs,
        string outcome,
        AnalysisChunkOutcome chunkOutcome)
    {
        var runLog = new AnalysisRunLog
        {
            JobId = jobId,
            AnalysisResultId = result.Id,
            PromptTemplateId = result.TemplateId,
            BookId = bookId,
            ChapterId = chapterId,
            SceneId = sceneId,
            Scope = scope.ToString(),
            AnalysisType = analysisType.ToString(),
            ModelName = result.ModelName,
            Language = language,
            TotalChunks = 1,
            SucceededChunks = outcome == "Succeeded" ? 1 : 0,
            FallbackChunks = outcome == "Succeeded" ? 0 : 1,
            InputWordCount = chunkOutcome.InputWordCount,
            InputCharCount = chunkOutcome.InputCharCount,
            OutputCharCount = chunkOutcome.OutputCharCount,
            SuggestionCount = result.Suggestions.Count,
            TotalDurationMs = durationMs,
            NoChangesHint = analysisType == AnalysisType.Proofread && result.ProofreadNoChangesHint,
            ChunkDetailsJson = JsonSerializer.Serialize(
                new List<AnalysisChunkOutcome> { chunkOutcome }, JsonOpts),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.AnalysisRunLogs.Add(runLog);
    }

    private void PersistChunkedRunLog(
        Guid jobId,
        AnalysisResult result,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        AnalysisScope scope,
        string analysisTypeString,
        string language,
        int totalChunks,
        IEnumerable<AnalysisChunkOutcome> chunkOutcomes,
        string inputText,
        string outputText,
        long durationMs,
        bool noChangesHint)
    {
        var outcomesList = chunkOutcomes
            .OrderBy(c => c.ChunkIndex)
            .ToList();

        var runLog = new AnalysisRunLog
        {
            JobId = jobId,
            AnalysisResultId = result.Id,
            PromptTemplateId = result.TemplateId,
            BookId = bookId,
            ChapterId = chapterId,
            SceneId = sceneId,
            Scope = scope.ToString(),
            AnalysisType = analysisTypeString,
            ModelName = result.ModelName,
            Language = language,
            TotalChunks = totalChunks,
            SucceededChunks = outcomesList.Count(c => c.Outcome == "Succeeded"),
            FallbackChunks = outcomesList.Count(c => c.Outcome != "Succeeded"),
            InputWordCount = WordCount(inputText),
            InputCharCount = inputText.Length,
            OutputCharCount = outputText.Length,
            SuggestionCount = result.Suggestions.Count,
            TotalDurationMs = durationMs,
            NoChangesHint = noChangesHint,
            ChunkDetailsJson = JsonSerializer.Serialize(outcomesList, JsonOpts),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.AnalysisRunLogs.Add(runLog);
    }

    /// <summary>
    /// PERSIST, THEN SIGNAL — the read-after-write contract for every async-job seam, as ONE call so it
    /// cannot be split again (be-c01).
    ///
    /// The FE polls <c>analysis-progress</c> and, the moment it sees <see cref="AnalysisProgressStatus.Succeeded"/>,
    /// GETs <c>analysis-jobs/{jobId}</c> — <c>AnalysisController.GetAnalysisByJobId</c>, which queries
    /// <c>AnalysisResults</c> by (ChapterId, BookId, JobId) on a DIFFERENT DbContext. So the row must be
    /// COMMITTED before the status flips, or that GET 404s and the run is reported as failed even though it
    /// succeeded. That is not theoretical: both chunked paths used to signal Succeeded and only then run
    /// <c>ArchivePreviousActiveAsync</c> + <c>ApplyAnalysisRepairAsync</c> (LLM-backed) before saving, a window
    /// SECONDS wide, and a user hit the 404 on a 10-chunk Hebrew Proofread.
    ///
    /// The previous defence was a comment on the generic path stating the ordering; the two chunked paths
    /// broke it anyway. Hence a mechanism: call THIS instead of <c>SaveChangesAsync</c> at any seam that
    /// stamps <see cref="AnalysisResult.JobId"/>. No-op on the signal half when <paramref name="jobId"/> is
    /// null (the synchronous /analyze path, which returns the row directly and never polls).
    /// </summary>
    private async Task PersistThenMarkJobSucceededAsync(
        Guid? jobId, string succeededMessage, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);

        if (jobId.HasValue)
            _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Succeeded, succeededMessage);
    }

    private void PersistSingleRunLog(
        Guid jobId,
        AnalysisResult result,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        AnalysisScope scope,
        AnalysisType analysisType,
        string language,
        string inputText,
        string llmOutputText,
        string? structuredJson,
        long durationMs,
        bool? proofreadUnrelated,
        double? proofreadWordSimilarity)
    {
        var (outcome, note, wordSimilarity) = ResolveSingleRunOutcome(
            analysisType,
            inputText,
            llmOutputText,
            result,
            structuredJson,
            proofreadUnrelatedOverride: proofreadUnrelated,
            proofreadWordSimilarityOverride: proofreadWordSimilarity);

        var chunkOutcome = CreateChunkOutcome(
            chunkIndex: 0,
            inputText: inputText,
            outputText: llmOutputText,
            durationMs: durationMs,
            outcome: outcome,
            wordSimilarity: wordSimilarity,
            note: note);

        PersistSingleChunkRunLog(
            jobId: jobId,
            result: result,
            bookId: bookId,
            chapterId: chapterId,
            sceneId: sceneId,
            scope: scope,
            analysisType: analysisType,
            language: language,
            durationMs: durationMs,
            outcome: outcome,
            chunkOutcome: chunkOutcome);
    }

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
        var context = await _contextService.BuildContextAsync(scope, targetId, analysisType, language, ct);
        var inputText = context.TargetText;
        var bookId = context.BookId;
        var chapterId = context.ChapterId;
        var sceneId = context.SceneId;
        // The BOOK's model tier for THIS task (p3-2; per-task since tier-ux-rework c1), resolved ONCE and used
        // for both the chunk sizing below (the target depends on the routed provider's window) and the
        // AiRequest stamp further down.
        var tier = await ResolveBookTierAsync(bookId, MapToTaskType(analysisType), ct);
        if (analysisType == AnalysisType.Proofread)
        {
            var opts = _aiOptions.Value;
            // Language-aware chunk sizing: derive the per-chunk WORD target from an estimated-TOKEN budget so a
            // dense-script (Hebrew) chunk gets fewer words than an English one for the same token footprint and
            // stays inside the model's reliable window. The configured EffectiveProofreadChunkTargetWords is the
            // CEILING (English keeps today's 500); dense scripts shrink from the token math (see helper).
            // Goes through the SHARED accessor so /api/config/analysis-chunk-thresholds cannot drift from what
            // is chunked here — the client picks async-vs-sync off that endpoint (p1-4).
            var chunkTargetWords = ProofreadChunkTargetWordsFor(opts, language, tier);
            var maxParallel = Math.Max(1, opts.MaxParallelProofreadChunks);
            var wordCount = WordCount(inputText);

            if (wordCount > chunkTargetWords)
            {
                var effectiveJobId = jobId ?? Guid.NewGuid();
                return await RunProofreadChunkedAsync(
                    inputText, bookId, chapterId, sceneId, scope, targetId,
                    customPrompt, language, chunkTargetWords, maxParallel, tier, effectiveJobId, context, ct);
            }
            if (inputText.Length > MaxProofreadInputLength)
                throw new InvalidOperationException($"Proofread text is too long ({inputText.Length} characters). Please select a shorter section (e.g. one scene or a few paragraphs). Maximum is {MaxProofreadInputLength:N0} characters.");
        }

        if (analysisType == AnalysisType.LineEdit)
        {
            var opts = _aiOptions.Value;
            // Same language-aware sizing as Proofread: EffectiveLineEditChunkTargetWords is the Latin ceiling,
            // dense scripts shrink from the token math so a Hebrew chunk stays within the model window. Same
            // shared accessor, same lockstep with the client-facing thresholds endpoint (p1-4).
            var chunkTargetWords = LineEditChunkTargetWordsFor(opts, language, tier);
            var maxParallel = Math.Max(1, opts.MaxParallelLineEditChunks);
            var wordCount = WordCount(inputText);

            if (wordCount > chunkTargetWords)
            {
                var effectiveJobId = jobId ?? Guid.NewGuid();
                return await RunLineEditChunkedAsync(
                    inputText, bookId, chapterId, sceneId, scope, targetId,
                    customPrompt, language, chunkTargetWords, maxParallel, tier, effectiveJobId, context, ct);
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
            Tier = tier,
            JsonMode = analysisType is AnalysisType.LineEdit or AnalysisType.LinguisticAnalysis
        };

        _logger.LogInformation("Running {Scope}/{Type} analysis on {TargetId}", scope, analysisType, targetId);
        if (analysisType == AnalysisType.Proofread)
            _logger.LogInformation("Proofread input length: {Length} characters (~{EstTokens} tokens). Long text may hit model limits.", inputText.Length, EstimateTokenCount(inputText));

        // Async-job (background) dispatch of a single-shot type: the controller already StartJob'd this jobId,
        // so move it out of "queued" into "running" for the duration of the (possibly multi-minute) LLM call.
        // Chunked Proofread/LineEdit drive their own progress; this covers the non-chunked types
        // (Linguistic/Literary/Summarization/Custom) now allowed on the async path. No-op when jobId is null
        // (synchronous /analyze) or the job is untracked.
        if (jobId.HasValue)
            _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Running, $"Running {analysisType}…");

        var llmSw = Stopwatch.StartNew();
        var response = await _router.CompleteAsync(request, ct);
        llmSw.Stop();

        if (analysisType == AnalysisType.Proofread && _logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _logger.LogDebug("Proofread raw response length={Len} startsWith={Preview}", response.Content?.Length ?? 0, response.Content?.Length > 0 ? response.Content.Substring(0, Math.Min(120, response.Content.Length)) : "");

        if (analysisType == AnalysisType.Proofread)
            _logger.LogInformation("Proofread raw response: length={Len}, preview={Preview}", response.Content?.Length ?? 0, TruncateForAudit(response.Content ?? "", 200));

        var cleanContent = SanitizeResponse(response.Content ?? "");

        // "No usable proofread output" reliability signal. A non-blank raw payload that SANITIZES to nothing
        // - e.g. only a <think> block, or pure watermark/CJK noise - means the model produced no usable
        // output, even though response.Content itself is not whitespace. But this MUST be re-evaluated after
        // the recovery fallback below: that fallback can legitimately restore real corrected text the model
        // placed after a <think> block, and a successful recovery must NOT read as unreliable. So we seed the
        // signal from the sanitized result and overwrite it once the fallback decides the final content.
        var proofreadSanitizedBlank = analysisType == AnalysisType.Proofread && string.IsNullOrWhiteSpace(cleanContent);

        // If Proofread ended up empty after stripping (e.g. model put answer only in <think> or hit a stop), use raw or input so we don't persist empty
        if (analysisType == AnalysisType.Proofread && string.IsNullOrWhiteSpace(cleanContent) && !string.IsNullOrEmpty(response.Content))
        {
            _logger.LogWarning("Proofread response was empty after sanitization (raw length={RawLen}). Using raw response or input as fallback.", response.Content.Length);
            var afterThink = ExtractTextAfterThinkBlock(response.Content);
            cleanContent = !string.IsNullOrWhiteSpace(afterThink) ? afterThink : response.Content.Trim();
            if (string.IsNullOrWhiteSpace(cleanContent))
            {
                cleanContent = inputText;
                proofreadSanitizedBlank = true; // nothing recoverable: echoed the input => no real proofread
            }
            else
            {
                // The fallback recovered text. It is real output only if it still has substance once
                // sanitized; junk the sanitizer rejects (CJK/watermark/think noise) stays unreliable. This
                // re-check is what stops a successful think-block recovery from being wrongly flagged.
                proofreadSanitizedBlank = string.IsNullOrWhiteSpace(SanitizeResponse(cleanContent));
            }
        }

        var structuredJson = TryParseStructured(analysisType, cleanContent);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, analysisType, ct);

        if (analysisType == AnalysisType.Proofread)
        {
            cleanContent = StripTextToCorrectMarkers(cleanContent);
        }

        var llmOutputText = cleanContent;
        cleanContent = MaybeReplaceLineEditResultText(analysisType, structuredJson, cleanContent);

        // Analysis-output repair (RunAsync seam), all gated by Ai:AnalysisRepair. Shipped default
        // { Enabled:true, GuardOnly:true, Mode:GlossaryThenDynamic } = glossary fast-path THEN dynamic span-scoped repair, value-scoped LLM off; never for Proofread.
        (structuredJson, cleanContent) = await ApplyAnalysisRepairAsync(structuredJson, cleanContent, analysisType, language, bookId ?? Guid.Empty, ct);

        var result = new AnalysisResult
        {
            ChapterId = chapterId,
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
            // Stamp the async job id when this single-shot run was dispatched as a background job, so
            // GetAnalysisByJobId can locate the persisted row exactly like the chunked paths do (lines
            // ModelName="chunked" set JobId there). Null for the synchronous /analyze path (jobId == null),
            // which returns the row directly and never polls by job id.
            JobId = jobId,
            SourceTextSnapshot = TextNormalization.NormalizeTextForAnalysis(inputText)
        };

        bool? proofreadUnrelated = null;
        double? proofreadWordSimilarity = null;
        if (analysisType == AnalysisType.Proofread)
        {
            proofreadUnrelated = IsProofreadResultUnrelated(inputText, llmOutputText, out var similarity);
            proofreadWordSimilarity = similarity;
        }

        AttachSuggestions(
            result,
            inputText,
            analysisType,
            structuredJson,
            cleanContent,
            isStreaming: false,
            isRunWithInput: false,
            applyProofreadHeuristics: true,
            proofreadUnrelatedOverride: proofreadUnrelated,
            language: language);

        // Additive, transient signal: a Proofread result is untrustworthy only when the model produced no
        // usable output (blank AFTER sanitization) OR content unrelated to the input OR it dropped a span of
        // the input (omission). Clean text (non-empty, near-identical) yields false even though
        // ProofreadNoChangesHint is true.
        if (analysisType == AnalysisType.Proofread)
            result.ProofreadResultUnreliable = (proofreadUnrelated ?? false) || proofreadSanitizedBlank || ProofreadDroppedContent(inputText, result.ResultText, result.Suggestions);

        _db.AnalysisResults.Add(result);

        // Persist an AnalysisRunLog for normal (non-chunked) proofread/line-edit too.
        // Chunked runs already persist per-chunk outcomes in RunProofreadChunkedAsync / RunLineEditChunkedAsync.
        if (analysisType == AnalysisType.Proofread || analysisType == AnalysisType.LineEdit)
        {
            var effectiveJobId = jobId ?? Guid.NewGuid();
            PersistSingleRunLog(
                jobId: effectiveJobId,
                result: result,
                bookId: bookId,
                chapterId: chapterId,
                sceneId: sceneId,
                scope: scope,
                analysisType: analysisType,
                language: language,
                inputText: inputText,
                llmOutputText: llmOutputText,
                structuredJson: structuredJson,
                durationMs: llmSw.ElapsedMilliseconds,
                proofreadUnrelated: proofreadUnrelated,
                proofreadWordSimilarity: proofreadWordSimilarity);
        }

        // Persist, THEN signal Succeeded — see PersistThenMarkJobSucceededAsync for why the two are one call.
        await PersistThenMarkJobSucceededAsync(jobId, $"{analysisType} finished", ct);

        // be-c03: a persisted CharacterAnalysis is a harvest source for the per-book proper-noun LEAVE set.
        // Deliberately AFTER the signal: it feeds the repair layer of LATER runs, not this row's readability,
        // and CharacterAnalysis is not an async-dispatchable type (see AnalysisController's asyncSupported
        // allowlist), so on any run that signals a job this call is already a no-op.
        InvalidateBookEntitiesIfNameSource(analysisType, bookId);

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
            Tier = await ResolveBookTierAsync(bookId, taskType, ct),
            JsonMode = analysisType is AnalysisType.LineEdit or AnalysisType.LinguisticAnalysis
        };

        _logger.LogInformation("Running {Scope}/{Type} with provided input", scope, analysisType);
        var llmSw = Stopwatch.StartNew();
        var response = await _router.CompleteAsync(request, ct);
        llmSw.Stop();

        var cleanContent = SanitizeResponse(response.Content);
        // Blank AFTER sanitization => no usable proofread output (see the RunAsync path for the rationale);
        // this is the reliable failure signal, not whether the raw response.Content was whitespace.
        var proofreadSanitizedBlank = analysisType == AnalysisType.Proofread && string.IsNullOrWhiteSpace(cleanContent);
        var structuredJson = TryParseStructured(analysisType, cleanContent);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, analysisType, ct);

        if (analysisType == AnalysisType.Proofread)
        {
            cleanContent = StripTextToCorrectMarkers(cleanContent);
        }

        var llmOutputText = cleanContent;
        cleanContent = MaybeReplaceLineEditResultText(analysisType, structuredJson, cleanContent);

        // Analysis-output repair (RunWithInputAsync seam), all gated by Ai:AnalysisRepair. Shipped default
        // { Enabled:true, GuardOnly:true, Mode:GlossaryThenDynamic } = glossary fast-path THEN dynamic span-scoped repair, value-scoped LLM off; never for Proofread.
        (structuredJson, cleanContent) = await ApplyAnalysisRepairAsync(structuredJson, cleanContent, analysisType, language, bookId ?? Guid.Empty, ct);

        // QA is answer-primary: the model returns an {answer, citations, confidence} JSON envelope, but the UI
        // binds ResultText directly (book-dashboard "ask" card). Surface the parsed (and glossary-repaired)
        // `answer` prose as ResultText so the ask box shows the answer, not the raw JSON. StructuredResult keeps
        // the full envelope (its citations drive the "cited chapters" line). Fail-safe: an unparsable or empty
        // answer leaves the raw text as-is (mirrors MaybeReplaceLineEditResultText's answer-primary handling).
        if (analysisType == AnalysisType.QA && !string.IsNullOrWhiteSpace(structuredJson))
        {
            var qaAnswer = TryExtractQaAnswer(structuredJson);
            if (!string.IsNullOrWhiteSpace(qaAnswer))
                cleanContent = qaAnswer;
        }

        var result = new AnalysisResult
        {
            ChapterId = chapterId,
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
        bool? proofreadUnrelated = null;
        double? proofreadWordSimilarity = null;
        if (analysisType == AnalysisType.Proofread)
        {
            proofreadUnrelated = IsProofreadResultUnrelated(inputText, llmOutputText, out var similarity);
            proofreadWordSimilarity = similarity;
        }

        AttachSuggestions(
            result,
            inputText,
            analysisType,
            structuredJson,
            cleanContent,
            isStreaming: false,
            isRunWithInput: true,
            applyProofreadHeuristics: true,
            proofreadUnrelatedOverride: proofreadUnrelated,
            language: language);

        // Additive, transient signal: untrustworthy only when the model produced no usable output (blank
        // AFTER sanitization) OR content unrelated to the input OR it dropped a span of the input (omission).
        if (analysisType == AnalysisType.Proofread)
            result.ProofreadResultUnreliable = (proofreadUnrelated ?? false) || proofreadSanitizedBlank || ProofreadDroppedContent(inputText, result.ResultText, result.Suggestions);

        _db.AnalysisResults.Add(result);

        if (analysisType == AnalysisType.Proofread || analysisType == AnalysisType.LineEdit)
        {
            var effectiveJobId = Guid.NewGuid();
            PersistSingleRunLog(
                jobId: effectiveJobId,
                result: result,
                bookId: bookId,
                chapterId: chapterId,
                sceneId: sceneId,
                scope: scope,
                analysisType: analysisType,
                language: language,
                inputText: inputText,
                llmOutputText: llmOutputText,
                structuredJson: structuredJson,
                durationMs: llmSw.ElapsedMilliseconds,
                proofreadUnrelated: proofreadUnrelated,
                proofreadWordSimilarity: proofreadWordSimilarity);
        }

        await _db.SaveChangesAsync(ct);

        // be-c03: a persisted CharacterAnalysis is a harvest source for the per-book proper-noun LEAVE set.
        InvalidateBookEntitiesIfNameSource(analysisType, bookId);

        _logger.LogInformation("Analysis {Id} persisted ({Scope}/{Type})", result.Id, scope, analysisType);
        return result;
    }

    /// <summary>
    /// be-c03 cache-refresh trigger: <see cref="IBookEntityProvider"/> harvests its per-book proper-noun LEAVE
    /// set partly from the book's stored ACTIVE <see cref="AnalysisType.CharacterAnalysis"/> results, so every
    /// seam that PERSISTS one (and archives the previous one) has just changed a harvest source. Drop the cached
    /// set here so the next analysis rebuilds it with the new names — a stale set that MISSES a name is not
    /// cosmetic: the repair model rewrites the name it was supposed to spare.
    ///
    /// A no-op for every other analysis type and for a run with no book (Guid.Empty / null is never cached).
    /// <see cref="IBookEntityProvider.Invalidate"/> is non-throwing by contract, so this can never break a save.
    /// </summary>
    private void InvalidateBookEntitiesIfNameSource(AnalysisType analysisType, Guid? bookId)
    {
        if (analysisType != AnalysisType.CharacterAnalysis || bookId is not { } id || id == Guid.Empty)
        {
            return;
        }

        _bookEntityProvider.Invalidate(id);
    }

    /// <summary>Extract the QA <c>answer</c> prose from a parsed <see cref="QAResult"/> JSON envelope, so the
    /// book-dashboard ask card can render the answer directly instead of the raw JSON. Returns null on any parse
    /// failure or a missing answer, in which case the caller keeps the raw ResultText (fail-safe).</summary>
    private string? TryExtractQaAnswer(string structuredJson)
    {
        try
        {
            return JsonSerializer.Deserialize<QAResult>(structuredJson, JsonOpts)?.Answer;
        }
        catch (JsonException)
        {
            return null;
        }
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
        var context = await _contextService.BuildContextAsync(scope, targetId, analysisType, language, ct);
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
            Tier = await ResolveBookTierAsync(bookId, taskType, ct),
            JsonMode = analysisType is AnalysisType.LineEdit or AnalysisType.LinguisticAnalysis
        };

        var sb = new StringBuilder();
        var streamSw = new Stopwatch();
        await using (var streamEnumerator = _router.StreamCompleteAsync(request, ct).GetAsyncEnumerator(ct))
        {
            while (true)
            {
                streamSw.Start();
                var hasNext = await streamEnumerator.MoveNextAsync();
                streamSw.Stop();

                if (!hasNext)
                    break;

                if (ct.IsCancellationRequested)
                    yield break;

                var token = streamEnumerator.Current;
                sb.Append(token);
                yield return token;
            }
        }

        var cleanContent = SanitizeResponse(sb.ToString());

        // "No usable proofread output" signal, seeded from the sanitized stream and re-evaluated after the
        // recovery fallback below (see RunAsync for the rationale): a successful think-block recovery of real
        // corrected text must NOT be flagged just because the FIRST sanitize pass came back blank.
        var proofreadSanitizedBlank = analysisType == AnalysisType.Proofread && string.IsNullOrWhiteSpace(cleanContent);

        if (analysisType == AnalysisType.Proofread && string.IsNullOrWhiteSpace(cleanContent))
        {
            var raw = sb.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Proofread (streaming) response empty after sanitization (raw length={RawLen}). Using fallback.", raw.Length);
                var afterThink = ExtractTextAfterThinkBlock(raw);
                cleanContent = !string.IsNullOrWhiteSpace(afterThink) ? afterThink : raw.Trim();
                if (string.IsNullOrWhiteSpace(cleanContent))
                {
                    cleanContent = inputText;
                    proofreadSanitizedBlank = true; // nothing recoverable: echoed the input => no real proofread
                }
                else
                {
                    // Recovered text counts as real output only if it survives sanitization; junk does not.
                    proofreadSanitizedBlank = string.IsNullOrWhiteSpace(SanitizeResponse(cleanContent));
                }
            }
        }

        var structuredJson = TryParseStructured(analysisType, cleanContent);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, analysisType, ct);

        if (analysisType == AnalysisType.Proofread)
        {
            cleanContent = StripTextToCorrectMarkers(cleanContent);
        }

        var llmOutputText = cleanContent;
        cleanContent = MaybeReplaceLineEditResultText(analysisType, structuredJson, cleanContent);

        // Analysis-output repair (streaming seam), all gated by Ai:AnalysisRepair. Shipped default
        // { Enabled:true, GuardOnly:true, Mode:GlossaryThenDynamic } = glossary fast-path THEN dynamic span-scoped repair, value-scoped LLM off; never for Proofread.
        (structuredJson, cleanContent) = await ApplyAnalysisRepairAsync(structuredJson, cleanContent, analysisType, language, bookId ?? Guid.Empty, ct);

        var result = new AnalysisResult
        {
            ChapterId = chapterId,
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

        bool? proofreadUnrelated = null;
        double? proofreadWordSimilarity = null;
        if (analysisType == AnalysisType.Proofread)
        {
            proofreadUnrelated = IsProofreadResultUnrelated(inputText, llmOutputText, out var similarity);
            proofreadWordSimilarity = similarity;
        }

        AttachSuggestions(
            result,
            inputText,
            analysisType,
            structuredJson,
            cleanContent,
            isStreaming: true,
            isRunWithInput: false,
            applyProofreadHeuristics: true,
            proofreadUnrelatedOverride: proofreadUnrelated,
            language: language);

        // Additive, transient signal: untrustworthy only when the model produced no usable output (blank
        // AFTER sanitization of the accumulated stream) OR content unrelated to the input OR it dropped a
        // span of the input (omission).
        if (analysisType == AnalysisType.Proofread)
            result.ProofreadResultUnreliable = (proofreadUnrelated ?? false) || proofreadSanitizedBlank || ProofreadDroppedContent(inputText, result.ResultText, result.Suggestions);

        _db.AnalysisResults.Add(result);

        if (analysisType == AnalysisType.Proofread || analysisType == AnalysisType.LineEdit)
        {
            var effectiveJobId = Guid.NewGuid();
            PersistSingleRunLog(
                jobId: effectiveJobId,
                result: result,
                bookId: bookId,
                chapterId: chapterId,
                sceneId: sceneId,
                scope: scope,
                analysisType: analysisType,
                language: language,
                inputText: inputText,
                llmOutputText: llmOutputText,
                structuredJson: structuredJson,
                durationMs: streamSw.ElapsedMilliseconds,
                proofreadUnrelated: proofreadUnrelated,
                proofreadWordSimilarity: proofreadWordSimilarity);
        }

        await _db.SaveChangesAsync(ct);

        // be-c03: a persisted CharacterAnalysis is a harvest source for the per-book proper-noun LEAVE set.
        InvalidateBookEntitiesIfNameSource(analysisType, bookId);
    }

    /// <summary>
    /// Run analysis without persistence - used internally by BookIntelligenceService
    /// for chapter summarization where the result feeds into a larger pipeline.
    /// </summary>
    /// <param name="bookId">
    /// The book this raw run belongs to, threaded from the caller (BookIntelligenceService knows it) so the
    /// repair layer can fetch the per-book proper-noun LEAVE set — the SAME id the persisted seams pass. It is
    /// optional ONLY as the fail-safe for a caller with genuinely no book context: null degrades to Guid.Empty,
    /// i.e. an empty entity set (the pre-e3 behaviour), never a crash.
    /// </param>
    public async Task<string> RunRawAsync(
        string inputText,
        AnalysisType analysisType,
        string? instruction,
        string language,
        Guid? bookId = null,
        CancellationToken ct = default)
    {
        // COMPOSITION, not a copy: the public seam is literally "generate, then repair". Splitting the body
        // into the two internal halves below is what lets the ONE batching caller
        // (BookIntelligenceService.SummarizeChaptersAsync) defer the repair half without any second code path
        // — so this seam and the batched seam cannot drift apart (the exact bug class that shipped twice on
        // this feature: the glossary skipped RunRawAsync, then the entity lever skipped it).
        var pending = await RunRawDeferredRepairAsync(inputText, analysisType, instruction, language, bookId, ct);
        return await CompleteDeferredRepairAsync(pending, ct);
    }

    /// <summary>
    /// An UN-REPAIRED raw model run: the sanitized model text plus everything
    /// <see cref="CompleteDeferredRepairAsync"/> needs to finish it. Deliberately NOT a <c>string</c> —
    /// a value of this type cannot be assigned to, persisted as, or returned as a finished analysis
    /// result without first being handed back to <see cref="CompleteDeferredRepairAsync"/>, so "I forgot
    /// to repair" is a compile error rather than a silent leak of English into persisted Hebrew prose.
    /// <c>internal</c> + a name that states the contract; there is no public repair-less raw seam.
    /// </summary>
    internal readonly record struct DeferredRepairRawRun(
        string UnrepairedText,
        AnalysisType AnalysisType,
        string Language,
        Guid BookId);

    /// <summary>
    /// FIRST HALF of <see cref="RunRawAsync"/>: run the model and sanitize, WITHOUT the repair layer.
    ///
    /// This is NOT a general "raw without repair" seam and must not become one. It exists for exactly one
    /// caller — <c>BookIntelligenceService.SummarizeChaptersAsync</c>, which summarizes every chapter first
    /// so the Summarization model stays resident, then runs ONE repair pass so the TermRepair model loads
    /// once instead of once per leaking chapter (an ~21 s cold model load per swap on a single-GPU host with
    /// <c>OLLAMA_MAX_LOADED_MODELS=1</c>). Its result MUST be completed through
    /// <see cref="CompleteDeferredRepairAsync"/> before it is persisted or returned; the
    /// <see cref="DeferredRepairRawRun"/> return type is what enforces that.
    /// </summary>
    internal async Task<DeferredRepairRawRun> RunRawDeferredRepairAsync(
        string inputText,
        AnalysisType analysisType,
        string? instruction,
        string language,
        Guid? bookId = null,
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
            JsonMode = analysisType is AnalysisType.LineEdit or AnalysisType.LinguisticAnalysis
        };

        var response = await _router.CompleteAsync(request, ct);
        var sanitized = SanitizeResponse(response.Content);

        // A null bookId degrades to Guid.Empty here, exactly as the un-split seam did (see the fail-safe note
        // on CompleteDeferredRepairAsync) — the resolution happens ONCE, at the producer.
        return new DeferredRepairRawRun(sanitized, analysisType, language, bookId ?? Guid.Empty);
    }

    /// <summary>
    /// SECOND HALF of <see cref="RunRawAsync"/>: apply the repair layer to a
    /// <see cref="DeferredRepairRawRun"/>. Identical prompt, identical span-scoped engine, identical
    /// validation-by-re-detect as the un-deferred call — the ONLY thing a batching caller changes is WHEN
    /// this runs, never WHAT it does, so repair quality is unchanged by construction.
    /// </summary>
    internal async Task<string> CompleteDeferredRepairAsync(
        DeferredRepairRawRun pending,
        CancellationToken ct = default)
    {
        // Apply the SAME analysis-output repair layer the persisted seams (RunAsync / RunWithInputAsync /
        // streaming / chunked LineEdit) run. Without this, a Hebrew Summarization routed through this raw
        // path — which BookIntelligenceService.SummarizeChaptersAsync persists into ChunkSummary.SummaryText
        // — would skip the shipped glossary repair and leak English that every other summarization run has
        // cleaned. The repair layer is type-aware and fail-safe: for the non-target types RunRawAsync also
        // serves (BookOverview / CharacterAnalysis / StoryAnalysis) it is a strict no-op that returns the
        // text byte-identical. Summarization and Synopsis are the two plain-text types this seam repairs
        // (structuredJson null for both), while BookOverview / CharacterAnalysis / StoryAnalysis stay strict
        // no-ops here because their structured arms blank-guard on a null structuredJson.
        // SEAM PARITY (be-c02): the bookId is THREADED FROM THE CALLER, exactly like the persisted seams
        // (RunAsync / RunWithInputAsync / streaming / chunked LineEdit) do — RunRawAsync takes raw inputText
        // rather than a book/chapter target, but every real caller (BookIntelligenceService's
        // SummarizeChaptersAsync + BuildBookProfileAsync) HAS the book in scope and passes it. That gives the
        // dynamic stage the per-book proper-noun LEAVE set, so a character/place name of THIS book is spared
        // here too. It matters most for Summarization — the one type this seam actually repairs, whose output
        // is persisted to ChunkSummary.SummaryText and names characters constantly (a sentence-initial Latin
        // name is NOT spared by the classifier's Title-Case-mid-sentence proper-noun rule, so the entity set is
        // the only thing standing between it and a rewrite).
        // A null bookId is the FAIL-SAFE for a caller with no book context: it degrades to Guid.Empty, and
        // BookEntityProvider.GetEntitiesAsync(Guid.Empty) returns an empty set — the pre-e3 behaviour, byte-
        // identical to what this seam did before. Under the rollback Mode=Glossary/Off the dynamic stage does
        // not run here at all (and the provider is never consulted).
        var (_, repaired) = await ApplyAnalysisRepairAsync(
            structuredJson: null, cleanContent: pending.UnrepairedText, pending.AnalysisType,
            pending.Language, pending.BookId, ct);
        return repaired;
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

        // Split by paragraph boundaries (single or double \n), keeping separators
        var paraParts = Regex.Split(fullText, @"(\n+)");
        for (var i = 0; i < paraParts.Length; i++)
        {
            var part = paraParts[i];
            if (string.IsNullOrEmpty(part)) continue;
            if (Regex.IsMatch(part, @"^\s*$")) continue;

            if (Regex.IsMatch(part, @"^\n+$"))
            {
                if (segments.Count > 0)
                {
                    var (t, s) = segments[^1];
                    segments[^1] = (t, part);
                }
                continue;
            }

            var paragraphSep = (i + 1 < paraParts.Length && Regex.IsMatch(paraParts[i + 1], @"^\n+$")) ? paraParts[i + 1] : "";

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

    // ─── Test seams (InternalsVisibleTo Pagedraft.Api.Tests) ──────────────────────────────────────────────
    // The chunk records above are private, so tests observe the chunker through these plain-tuple projections
    // rather than reflecting on the private return types. They call the real chunkers, so a test on chunk count
    // / boundaries / OverlapPrefix exercises the production path exactly.

    /// <summary>Test seam: proofread chunks as (Text, SeparatorAfter, OverlapPrefix) tuples.</summary>
    internal static List<(string Text, string SeparatorAfter, string? OverlapPrefix)> ChunkForProofreadForTest(
        string fullText, int targetWordsPerChunk) =>
        ChunkForProofread(fullText, targetWordsPerChunk)
            .Select(c => (c.Text, c.SeparatorAfter, c.OverlapPrefix))
            .ToList();

    /// <summary>Test seam: LineEdit chunks as (Text, SeparatorAfter, OverlapPrefix, OverlapSuffix) tuples.</summary>
    internal static List<(string Text, string SeparatorAfter, string? OverlapPrefix, string? OverlapSuffix)> ChunkForLineEditForTest(
        string fullText, int targetWordsPerChunk) =>
        ChunkForLineEdit(fullText, targetWordsPerChunk)
            .Select(c => (c.Text, c.SeparatorAfter, c.OverlapPrefix, c.OverlapSuffix))
            .ToList();

    private static int WordCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return Regex.Split(text.Trim(), @"\s+").Count(s => s.Length > 0);
    }

    /// <summary>
    /// Average characters per word (a whole word PLUS its one trailing separator) used to convert BETWEEN a
    /// word count and its estimated token footprint. It is the ONE bridge constant between the word-based
    /// chunker and the token math; the script-density difference (Hebrew vs. Latin) is carried ENTIRELY by
    /// <see cref="BookContextAssembler.CharsPerTokenForLanguage"/>, NOT by this value, so the Hebrew shrink is
    /// DERIVED (half the chars/token → half the words for the same token footprint), never a magic "half".
    /// 6.0 (~5-char word + 1 separator) matches the estimator's assumptions; because it appears symmetrically
    /// in the word→token and token→word conversions it cancels in the language-ratio, so the Latin/Hebrew ratio
    /// depends only on the char/token densities.
    /// </summary>
    private const double AvgCharsPerWord = 6.0;

    /// <summary>
    /// Language-aware effective per-chunk WORD target for the proofread / LineEdit chunker. Sizes each chunk by
    /// an ESTIMATED-TOKEN footprint rather than a raw word count, so a dense-script (Hebrew/Arabic) chunk gets
    /// FEWER words than an English chunk for the SAME token footprint and therefore stays inside the model's
    /// reliable context window (Ollama silently TRUNCATES past num_ctx, which is what makes an over-long Hebrew
    /// chunk trip the proofread reliability guard).
    ///
    /// Two bounds, both derived from the SHARED estimator helpers (no duplicated char/token constants):
    ///
    ///  (A) LANGUAGE-NORMALIZED CEILING. The configured ceiling (<paramref name="configuredCeiling"/>, the
    ///      effective 500) is defined in LATIN words; its token footprint is
    ///      <c>ceiling * AvgCharsPerWord / charsPerToken("en")</c>. Re-expressing that SAME token footprint in
    ///      THIS language's words gives
    ///          <c>ceiling * charsPerToken(lang) / charsPerToken("en")</c>
    ///      (AvgCharsPerWord cancels). charsPerToken is 4.0 for Latin and 2.0 for Hebrew/Arabic
    ///      (<see cref="BookContextAssembler.CharsPerTokenForLanguage"/>), so Latin keeps the full 500 while
    ///      Hebrew resolves to ~250 — the "~half" falls out of the density ratio, it is not hardcoded.
    ///
    ///  (B) WINDOW FIT. Independently, the chunk INPUT plus its generated OUTPUT plus the prompt/system overhead
    ///      plus a safety margin must fit the model's context window (num_ctx). Unlike the whole-book review
    ///      (where a small generation coexists with a large context, the model
    ///      <see cref="AiOptions.EffectiveBookContextTokenBudget"/> assumes), a proofread/LineEdit response is a
    ///      CORRECTED COPY of the input, so OUTPUT ≈ INPUT. We therefore split the generation window in HALF for
    ///      input vs output, after subtracting the SAME prompt-overhead + safety-margin reserves the book-context
    ///      budget uses (reused from <see cref="AiOptions"/> so the accounting is single-sourced):
    ///          generationWindow = numCtx - BookContextPromptReserveTokens - BookContextSafetyMarginTokens
    ///          availableInputTokens = generationWindow / 2   (output ≈ input)
    ///      num_ctx is resolved through the SAME provider/task tuning precedence the model uses at request time
    ///      (<see cref="BookContextAssembler.ResolveNumCtxForTask"/>). Converting to words for this language:
    ///          wordsThatFitWindow = availableInputTokens * charsPerToken(lang) / AvgCharsPerWord.
    ///      This only bites when the window is tighter than the language ceiling footprint.
    ///
    /// The result is <c>min(A, B)</c>, floored at 1 so a chunk is always made. Latin is unchanged at the
    /// production config (500); Hebrew halves via (A) and shrinks further via (B) on a tight window. This only
    /// changes HOW OFTEN the reliability guard LEGITIMATELY trips; the <c>ProofreadResultUnreliable</c> semantics
    /// are untouched.
    /// </summary>
    internal static int EffectiveChunkTargetWords(
        AiOptions opts, AiTaskType task, string? language, int configuredCeiling, AiTier tier = AiTier.Fast)
    {
        var ceiling = configuredCeiling > 0
            ? configuredCeiling
            : task == AiTaskType.LineEdit
                ? AiOptions.DefaultLineEditChunkTargetWords
                : AiOptions.DefaultProofreadChunkTargetWords;

        // Shared, language-aware char/token densities (single source of truth — no duplicated constants).
        var charsPerTokenLang = BookContextAssembler.CharsPerTokenForLanguage(language);
        var charsPerTokenLatin = BookContextAssembler.CharsPerTokenForLanguage("en");

        // (A) The ceiling is a LATIN word count; re-express its token footprint in this language's words so a
        //     dense script gets proportionally fewer words (AvgCharsPerWord cancels → pure density ratio).
        var languageCeiling = (int)Math.Floor(ceiling * charsPerTokenLang / charsPerTokenLatin);

        // (B) Words that fit the model window when INPUT and its ~equal-sized OUTPUT must both fit num_ctx.
        // Note: the per-chunk OverlapPrefix is prepended at request time and is NOT charged against this
        // budget. This is safe today because bound (A) — the language ceiling — dominates with comfortable
        // margin, so overlap tokens do not push any chunk past the window. It is a latent coupling: if the
        // proofread num_ctx were ever dropped well below the current 4096, bound (B) could tighten enough
        // that the uncharged overlap causes actual window overflow.
        // LANGUAGE- and TIER-aware since p3-2: the window belongs to the provider this (task, language, tier)
        // actually ROUTES to, not to the bare task key. Before p3-2 the sizer passed neither, so an English
        // Proofread was sized against the bare "Proofread" entry while the router ran "Proofread_en" - a
        // divergence that was harmless only because both name the same provider (pinned by p1-4's
        // ChunkSizerAndRouter_ResolveTheSameWindow_ForAnEnglishProofreadAndLineEdit).
        var numCtx = BookContextAssembler.ResolveNumCtxForTask(opts, task, language, tier);
        var generationWindow = numCtx
            - Math.Max(0, opts.BookContextPromptReserveTokens)
            - Math.Max(0, opts.BookContextSafetyMarginTokens);
        // Split the remaining window in half (output ≈ input for a proofread/LineEdit rewrite). Floor at a small
        // positive minimum so a pathologically tiny window still yields a workable (if tiny) chunk.
        var availableInputTokens = Math.Max(64, generationWindow / 2);
        var wordsThatFitWindow = (int)Math.Floor(availableInputTokens * charsPerTokenLang / AvgCharsPerWord);

        // Take the tighter of the two bounds; always make at least one (small) chunk.
        return Math.Max(1, Math.Min(languageCeiling, wordsThatFitWindow));
    }

    /// <summary>
    /// THE per-chunk word target Proofread will actually be chunked at, for this language and the CURRENTLY
    /// ROUTED model's window. Call this rather than <see cref="EffectiveChunkTargetWords"/> directly.
    ///
    /// WHY IT EXISTS (model-tier plan, p1-4). Exactly two surfaces must produce this number and they must
    /// never disagree: <see cref="RunAsync"/>, which decides whether to chunk and at what size, and
    /// <c>GET /api/config/analysis-chunk-thresholds</c>
    /// (<see cref="Controllers.ConfigController.GetAnalysisChunkThresholds"/>), which the CLIENT uses to pick
    /// the async analysis-jobs flow over sync <c>/analyze</c>. If the endpoint returns a larger target than
    /// RunAsync sizes at, the client picks sync for a chapter the server then chunks, and a long chapter
    /// mis-routes. Both used to spell out the same three arguments (task + language + the task's configured
    /// ceiling) at their own call site, so the two could drift apart one argument at a time; now the tuple
    /// exists once and "in lockstep" is structural rather than a convention.
    ///
    /// TIER-SENSITIVE: the target's bound (B) reads
    /// <see cref="BookContextAssembler.ResolveNumCtxForTask"/>, so anything that changes which provider (and
    /// therefore which <c>Ai:ProviderSettings</c> entry) Proofread routes to can move this number — see the
    /// crossover pinned in <c>ChunkThresholdBoundDominanceTests</c>.
    /// </summary>
    internal static int ProofreadChunkTargetWordsFor(AiOptions opts, string? language, AiTier tier = AiTier.Fast)
        => EffectiveChunkTargetWords(opts, AiTaskType.Proofread, language, opts.EffectiveProofreadChunkTargetWords, tier);

    /// <summary>
    /// THE per-chunk word target LineEdit will actually be chunked at, for this language and the currently
    /// routed model's window. Same contract, same two surfaces and same tier sensitivity as
    /// <see cref="ProofreadChunkTargetWordsFor"/> — note it carries its OWN configured ceiling
    /// (<see cref="AiOptions.EffectiveLineEditChunkTargetWords"/>), which is why this is a separate accessor
    /// and not one shared "chunk target" helper taking a task.
    /// </summary>
    internal static int LineEditChunkTargetWordsFor(AiOptions opts, string? language, AiTier tier = AiTier.Fast)
        => EffectiveChunkTargetWords(opts, AiTaskType.LineEdit, language, opts.EffectiveLineEditChunkTargetWords, tier);

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
    /// left curly quote (\u201C), em dash (-), and en dash (-).
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
    /// Returns true if the segment is part of an ongoing dialogue block - either it
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
    /// Backstop cap on the number of LineEdit suggestions that survive
    /// <see cref="NormalizeLineEditSuggestions"/>. The observed repetition-loop failure produced
    /// ~10 identical suggestions; a legitimate per-chapter line-edit is comfortably under this, so
    /// 50 only ever truncates a pathological run whose duplicates were near-identical (and thus
    /// slipped past the exact-pair dedupe) rather than byte-identical.
    /// </summary>
    private const int MaxLineEditSuggestions = 50;

    /// <summary>
    /// A LineEdit chunk whose (sanitized) model output balloons far past its input is in a decoding
    /// repetition loop — the failure the Ollama_LineEdit RepeatPenalty tuning targets but does not always
    /// break. Such a chunk both burns minutes of generation AND yields garbage suggestions, so its output is
    /// discarded rather than parsed. LineEdit output is structured JSON (each suggestion repeats a snippet of
    /// Original + Suggested + reason), so it is legitimately a small multiple of the input; only a runaway is
    /// several times larger. Ratio 4 + a 500-char floor clears the busiest legitimate chunk (observed live:
    /// clean chunks ~0.3-1.4x input) while catching the loops (observed live: ~10-11x input).
    /// </summary>
    private const double LineEditRepetitionLoopRatio = 4.0;

    /// <summary>
    /// True when a LineEdit chunk's sanitized output length indicates a decoding repetition loop relative to
    /// its input length (see <see cref="LineEditRepetitionLoopRatio"/>). Pure function so it is unit-testable
    /// without a live model.
    /// </summary>
    internal static bool IsLikelyLineEditRepetitionLoop(int inputLength, int outputLength)
        => inputLength > 0 && outputLength > inputLength * LineEditRepetitionLoopRatio + 500;

    /// <summary>
    /// Deserialize a serialized <see cref="LineEditResult"/>, run it through
    /// <see cref="NormalizeLineEditSuggestions"/>, and re-serialize. Applied to EVERY successful
    /// LineEdit parse/salvage path in <see cref="TryParseStructured"/> so the persisted
    /// StructuredResult (read directly by the FE, not only the derived AnalysisSuggestion cards) is
    /// clean. FAIL-SAFE: if the input cannot be re-parsed for any reason the ORIGINAL json is
    /// returned unchanged so a normalization hiccup can never drop a hard-won salvaged result.
    /// <paramref name="logger"/> is optional (static helper, no instance logger) and is forwarded to
    /// <see cref="NormalizeLineEditSuggestions"/> purely so it can warn when the cap actually truncates.
    /// </summary>
    internal static string? NormalizeLineEditResultJson(string? json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            var parsed = JsonSerializer.Deserialize<LineEditResult>(json, JsonOpts);
            if (parsed is null) return json;
            var normalized = NormalizeLineEditSuggestions(parsed, logger);
            return JsonSerializer.Serialize(normalized, JsonOpts);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>
    /// Post-parse normalization for LineEdit suggestions, applied to every successful parse/salvage
    /// path so the persisted StructuredResult is clean — not only the derived cards. Three
    /// deterministic passes, in order (OverallFeedback and every surviving suggestion's fields —
    /// Reason, Category, ... — are preserved verbatim):
    ///  1. DROP no-op suggestions. Two cases are treated as no-ops: (a) Original == Suggested after
    ///     trimming + Unicode normalization (reuses <see cref="IsNoOpSuggestion"/>); and (b)
    ///     suggestions that differ ONLY by SURROUNDING (leading/trailing) punctuation or whitespace,
    ///     e.g. the real "לא," -> "לא" repetition-loop noise the diagnostic flagged. "Surrounding"
    ///     is deliberately conservative — internal punctuation changes (e.g. "טוב, מאוד" -> "טוב מאוד")
    ///     are a REAL edit and are kept.
    ///  2. DEDUPE on the (Original, Suggested) pair (trimmed + normalized, ordinal), keeping the
    ///     FIRST occurrence and preserving order — the live run emitted ~10 identical cards.
    ///  3. CAP the survivors at <see cref="MaxLineEditSuggestions"/> as a backstop against a
    ///     pathological run whose near-identical entries slipped past the exact-pair dedupe.
    /// An optional <paramref name="logger"/> gets a single LogWarning ONLY when the cap actually
    /// truncates (i.e. raw suggestions beyond the cap were left unevaluated) — not on every call.
    /// </summary>
    internal static LineEditResult NormalizeLineEditSuggestions(LineEditResult result, ILogger? logger = null)
    {
        if (result?.Suggestions is null || result.Suggestions.Count == 0)
            return result ?? new LineEditResult();

        var suggestions = result.Suggestions;
        var deduped = new List<LineEditSuggestion>();
        var seen = new HashSet<(string Original, string Suggested)>();

        for (var i = 0; i < suggestions.Count; i++)
        {
            var suggestion = suggestions[i];
            if (suggestion is null) continue;

            // Pass 1: drop exact no-ops, surrounding-punctuation-only "noise" edits, unanchorable/leaked-
            // scaffolding entries, and incoherent clause->punctuation collapses (the "change a comma but
            // remove a full line" garbage a repetition loop produces).
            if (IsNoOpSuggestion(suggestion)
                || IsSurroundingPunctuationOnlyDiff(suggestion)
                || IsUnanchorableOrScaffoldingSuggestion(suggestion)
                || IsIncoherentCollapseSuggestion(suggestion))
                continue;

            // Pass 2: dedupe identical (Original, Suggested) pairs, first occurrence wins.
            var key = (
                TextNormalization.NormalizeTextForAnalysis((suggestion.Original ?? string.Empty).Trim()),
                TextNormalization.NormalizeTextForAnalysis((suggestion.Suggested ?? string.Empty).Trim()));
            if (!seen.Add(key)) continue;

            deduped.Add(suggestion);

            // Pass 3: cap.
            if (deduped.Count >= MaxLineEditSuggestions)
            {
                var unprocessed = suggestions.Count - (i + 1);
                if (unprocessed > 0)
                {
                    logger?.LogWarning(
                        "NormalizeLineEditSuggestions: hit the {Cap}-suggestion cap with {Unprocessed} raw suggestion(s) beyond it left unevaluated (dropped).",
                        MaxLineEditSuggestions, unprocessed);
                }
                break;
            }
        }

        result.Suggestions = deduped;
        return result;
    }

    /// <summary>
    /// True when a suggestion's Original and Suggested are identical once SURROUNDING
    /// (leading/trailing) punctuation and whitespace are stripped after Unicode normalization —
    /// e.g. "לא," vs "לא". Conservative by construction: only the outer edges are trimmed, so any
    /// change to the inner text (word order, internal punctuation, added/removed words) is a real
    /// edit and returns false. If BOTH sides trim to an empty core they were pure punctuation on
    /// each side (e.g. "?" vs "!"); those are left for <see cref="IsNoOpSuggestion"/>/dedupe to
    /// judge rather than collapsed here, so distinct punctuation-only edits are not falsely merged.
    /// </summary>
    private static bool IsSurroundingPunctuationOnlyDiff(LineEditSuggestion suggestion)
    {
        var original = TextNormalization.NormalizeTextForAnalysis((suggestion.Original ?? string.Empty).Trim());
        var suggested = TextNormalization.NormalizeTextForAnalysis((suggestion.Suggested ?? string.Empty).Trim());

        var originalCore = TrimSurroundingPunctuation(original);
        var suggestedCore = TrimSurroundingPunctuation(suggested);

        if (originalCore.Length == 0 && suggestedCore.Length == 0)
            return false;

        return string.Equals(originalCore, suggestedCore, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when a LineEdit suggestion cannot be safely anchored or carries leaked prompt scaffolding.
    /// A blank <see cref="LineEditSuggestion.Original"/> has no text to locate in the document, so
    /// <see cref="SuggestionDiffService.ComputeLineEditSuggestions"/> would anchor it via <c>IndexOf("")</c>
    /// as a zero-width insertion at an arbitrary cursor — the exact vector by which a leaked few-shot
    /// template fragment (bracketed JSON scaffolding) was inserted into the manuscript. Also drops any
    /// suggestion whose Original or Suggested still contains our wrapping markers, which should never survive
    /// into edit text.
    /// </summary>
    private static bool IsUnanchorableOrScaffoldingSuggestion(LineEditSuggestion suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion.Original))
            return true;
        return ContainsScaffoldingMarker(suggestion.Original) || ContainsScaffoldingMarker(suggestion.Suggested);
    }

    private static bool ContainsScaffoldingMarker(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("[TEXT_TO_EDIT]", StringComparison.Ordinal)
            || text.Contains("[/TEXT_TO_EDIT]", StringComparison.Ordinal)
            || text.Contains("[TEXT_TO_CORRECT]", StringComparison.Ordinal)
            || text.Contains("[/TEXT_TO_CORRECT]", StringComparison.Ordinal);
    }

    private static readonly char[] WhitespaceSeparators = [' ', '\t', '\n', '\r', ' '];

    /// <summary>
    /// True when a suggestion collapses a MULTI-WORD span down to bare, NON-EMPTY punctuation — e.g. a whole
    /// clause "-> ,". Anchored verbatim, this deletes the entire clause while presenting as a trivial
    /// punctuation change ("change a comma but remove a full line"), the signature garbage a repetition loop
    /// emits (observed live as an over-broad "replace a clause with a comma" edit, never as an empty Suggested).
    /// A coherent line edit that shortens text still leaves real words, so a Suggested containing ANY letter or
    /// digit is always kept. Only a Suggested that is non-empty yet has no alphanumeric content is suspect, and
    /// even then only when the Original is a multi-word span (&gt;= 3 whitespace-separated tokens) — a small
    /// stray-word or doubled-word deletion is legitimate copyediting and is preserved.
    /// <para>
    /// An EMPTY Suggested (a clean full-clause DELETION, e.g. Original "בסופו של דבר" -> "") is deliberately
    /// PRESERVED: it is a legitimate conciseness edit, not the "clause -> comma" garbage, and no observed loop
    /// emits an empty Suggested for a multi-word Original. A genuinely runaway deletion is already backstopped
    /// upstream by the chunk-level <see cref="IsLikelyLineEditRepetitionLoop"/> guard, which discards the whole
    /// ballooned chunk before its suggestions are ever parsed — so this secondary guard need not (and must not)
    /// catch empty deletions.
    /// </para>
    /// </summary>
    private static bool IsIncoherentCollapseSuggestion(LineEditSuggestion suggestion)
    {
        var suggested = (suggestion.Suggested ?? string.Empty).Trim();

        // An empty Suggested is a clean full deletion (a legitimate conciseness edit); preserve it.
        // Runaway deletions are backstopped by the chunk-level IsLikelyLineEditRepetitionLoop guard.
        if (suggested.Length == 0)
            return false;

        if (suggested.Any(char.IsLetterOrDigit))
            return false; // a real rewrite with actual content

        var original = (suggestion.Original ?? string.Empty).Trim();
        var tokenCount = original.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries).Length;
        return tokenCount >= 3;
    }

    /// <summary>
    /// Strip leading and trailing punctuation and whitespace, preserving the inner text verbatim.
    /// </summary>
    private static string TrimSurroundingPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        int start = 0;
        int end = text.Length - 1;
        while (start <= end && (char.IsWhiteSpace(text[start]) || char.IsPunctuation(text[start])))
            start++;
        while (end >= start && (char.IsWhiteSpace(text[end]) || char.IsPunctuation(text[end])))
            end--;

        return start > end ? string.Empty : text[start..(end + 1)];
    }

    /// <summary>
    /// Run LineEdit in chunks with limited parallelism, then merge into one AnalysisResult.
    /// Updates AnalysisProgressTracker for live progress polling.
    /// </summary>
    /// <param name="tier">
    /// The book's tier, PASSED IN rather than resolved here (be-c03). Private, and <see cref="RunAsync"/> is
    /// its only PRODUCTION caller, so the value is always the same one that sized the chunks - see the note
    /// below.
    /// BEFORE CHANGING THIS SIGNATURE AGAIN (final-r02): it also has REFLECTIVE callers -
    /// <c>AnalysisRunLogTests.RunLineEditChunkedAsync_*</c> builds the argument array by POSITION through
    /// <c>GetMethod(...).Invoke(...)</c>, so a reordered or inserted parameter does NOT fail the build; it
    /// fails at run time as an argument-count/type mismatch, or worse, binds silently. Grep the test
    /// assembly for the method name whenever this list changes.
    /// </param>
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
        AiTier tier,
        Guid jobId,
        AnalysisContext context,
        CancellationToken ct)
    {
        var taskType = MapToTaskType(AnalysisType.LineEdit);
        // The book's tier arrives from RunAsync's single read (be-c03) and is stamped on every chunk below.
        // A long chapter never reaches RunAsync's single-shot request, so a tier stamped only there would
        // leave every chunked run on the local tier while the UI said otherwise. LineEdit is NOT in
        // AiTierPolicy.TieredTasks, so the value is inert here today - stamped anyway so the two chunked
        // paths stay symmetrical and adding LineEdit to the allowlist is a one-line change rather than a hunt
        // for an unstamped call site.
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

        var overallSw = Stopwatch.StartNew();
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var chunkResults = new LineEditResult[chunks.Count];
        var chunkOutcomes = new ConcurrentBag<AnalysisChunkOutcome>();

        async Task ProcessChunk(int index)
        {
            var chunk = chunks[index];
            var text = chunk.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                chunkResults[index] = new LineEditResult();
                chunkOutcomes.Add(CreateChunkOutcome(
                    chunkIndex: index,
                    inputText: text ?? "",
                    outputText: text ?? "",
                    durationMs: 0,
                    outcome: "Succeeded"));

                var chunkNumber = index + 1;
                _progress.ChunkStarted(jobId, chunkNumber, chunks.Count);
                _progress.ChunkCompleted(jobId, chunkNumber, chunks.Count);
                return;
            }

            await semaphore.WaitAsync(ct);
            var chunkSw = Stopwatch.StartNew();
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
                    Tier = tier,
                    JsonMode = true
                };

                var response = await _router.CompleteAsync(request, ct);
                var raw = response.Content ?? string.Empty;
                _logger.LogDebug(
                    "LineEdit chunk {Index}/{Total} raw response: length={Len}, preview={Preview}",
                    chunkNumber, chunks.Count, raw.Length, TruncateForAudit(raw, 200));
                var clean = SanitizeResponse(raw);

                // Repetition-loop guard: a chunk whose output ballooned far past its input is looping and its
                // parsed suggestions are garbage (over-broad "replace a clause with a comma" edits, leaked
                // scaffolding). Discard the chunk entirely rather than feed the loop's output into the merge.
                if (IsLikelyLineEditRepetitionLoop(text.Length, clean.Length))
                {
                    _logger.LogWarning(
                        "LineEdit chunk {Index}/{Total} output is {OutLen} chars from a {InLen}-char input (~{Ratio:F1}x); likely repetition loop, discarding chunk.",
                        chunkNumber, chunks.Count, clean.Length, text.Length, (double)clean.Length / text.Length);
                    chunkResults[index] = new LineEditResult();
                    chunkOutcomes.Add(CreateChunkOutcome(
                        chunkIndex: index,
                        inputText: text,
                        outputText: clean,
                        durationMs: chunkSw.ElapsedMilliseconds,
                        outcome: "FallbackRepetition",
                        note: $"{(double)clean.Length / text.Length:F1}x longer than input"));
                    _progress.ChunkCompleted(jobId, chunkNumber, chunks.Count);
                    return;
                }

                var structuredJson = TryParseStructured(AnalysisType.LineEdit, clean);

                string outcome;
                if (structuredJson is null)
                {
                    _logger.LogWarning(
                        "LineEdit chunk {Index}/{Total} produced no structured JSON. rawLen={RawLen}, cleanLen={CleanLen}, cleanPreview={Preview}",
                        chunkNumber, chunks.Count, raw.Length, clean.Length, TruncateForAudit(clean, 200));
                    chunkResults[index] = new LineEditResult();
                    outcome = string.IsNullOrWhiteSpace(clean) ? "FallbackEmpty" : "FallbackError";
                }
                else
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<LineEditResult>(structuredJson, JsonOpts);
                        chunkResults[index] = parsed ?? new LineEditResult();
                        outcome = "Succeeded";
                    }
                    catch (JsonException)
                    {
                        chunkResults[index] = new LineEditResult();
                        outcome = "FallbackError";
                    }
                }

                chunkOutcomes.Add(CreateChunkOutcome(
                    chunkIndex: index,
                    inputText: text,
                    outputText: clean,
                    durationMs: chunkSw.ElapsedMilliseconds,
                    outcome: outcome));

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
                chunkOutcomes.Add(CreateChunkOutcome(
                    chunkIndex: index,
                    inputText: text,
                    outputText: "",
                    durationMs: chunkSw.ElapsedMilliseconds,
                    outcome: "FallbackError",
                    note: ex.Message.Length > 200 ? ex.Message[..200] : ex.Message));
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
        overallSw.Stop();

        // Merge across chunks (cross-chunk Original-dedupe + combined OverallFeedback), then run the
        // SAME post-parse normalization the per-chunk parse path applies (NormalizeLineEditSuggestions):
        // surrounding-punctuation-only noise drop + MaxLineEditSuggestions (50) cap. MergeLineEditResults
        // enforces neither, so a repetition loop spread across many chunks could otherwise accumulate
        // >50 suggestions / punctuation-noise into the merged, persisted StructuredResult. Normalization
        // only touches the suggestions list; OverallFeedback and the cross-chunk dedupe are preserved.
        var merged = NormalizeLineEditSuggestions(MergeLineEditResults(chunkResults.ToList()), _logger);
        var mergedJson = JsonSerializer.Serialize(merged, JsonOpts);

        _logger.LogInformation("LineEdit merge complete: {SuggestionCount} suggestions", merged.Suggestions.Count);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, AnalysisType.LineEdit, ct);

        var cleanContent = MaybeReplaceLineEditResultText(AnalysisType.LineEdit, mergedJson, merged.OverallFeedback ?? string.Empty);

        // Analysis-output repair (chunked LineEdit seam) — mirror the three non-chunked seams (:435, :560,
        // :725) so LONG Hebrew chapters (which route here) get the same glossary/repair layer as short ones.
        // Gated by Ai:AnalysisRepair (shipped default { Enabled:true, GuardOnly:true, Mode:GlossaryThenDynamic }
        // = glossary fast-path THEN dynamic span-scoped repair, value-scoped LLM off). ApplyAnalysisRepairAsync re-derives ResultText from the repaired
        // overallFeedback for LineEdit, preserving the MaybeReplaceLineEditResultText behaviour above.
        var (repairedJson, repairedText) = await ApplyAnalysisRepairAsync(
            mergedJson, cleanContent, AnalysisType.LineEdit, language, bookId ?? Guid.Empty, ct);
        mergedJson = repairedJson ?? mergedJson;
        cleanContent = repairedText;

        var result = new AnalysisResult
        {
            ChapterId = chapterId,
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
            // "chunked" is an internal sentinel: the run fanned out over many per-chunk model calls so
            // there is no single model to surface. The FE suppresses this exact token in result headings
            // (visibleModelName in visible-model-name.ts); a rename here must be mirrored FE-side.
            ModelName = "chunked",
            JobId = jobId,
            SourceTextSnapshot = TextNormalization.NormalizeTextForAnalysis(inputText)
        };

        AttachSuggestions(result, inputText, AnalysisType.LineEdit, mergedJson, cleanContent, isStreaming: false, isRunWithInput: false, applyProofreadHeuristics: false);

        _db.AnalysisResults.Add(result);

        PersistChunkedRunLog(
            jobId: jobId,
            result: result,
            bookId: bookId,
            chapterId: chapterId,
            sceneId: sceneId,
            scope: scope,
            analysisTypeString: AnalysisType.LineEdit.ToString(),
            language: language,
            totalChunks: chunks.Count,
            chunkOutcomes: chunkOutcomes,
            inputText: inputText,
            outputText: cleanContent,
            durationMs: overallSw.ElapsedMilliseconds,
            noChangesHint: false);

        // Persist, THEN signal Succeeded — the FE GETs analysis-jobs/{jobId} the moment it sees Succeeded, so
        // the row has to be committed first. See PersistThenMarkJobSucceededAsync.
        await PersistThenMarkJobSucceededAsync(jobId, "LineEdit finished", ct);

        _logger.LogInformation("Analysis {Id} persisted (LineEdit chunked, {Scope})", result.Id, scope);
        return result;
    }

    /// <summary>Run proofread in chunks with limited parallelism, then merge into one AnalysisResult. Updates AnalysisProgressTracker for live progress polling.</summary>
    /// <param name="tier">
    /// The book's tier, PASSED IN rather than resolved here (be-c03). Private, and <see cref="RunAsync"/> is
    /// its only PRODUCTION caller, so the value is always the same one that sized the chunks - see the note
    /// below.
    /// BEFORE CHANGING THIS SIGNATURE AGAIN (final-r02): it also has REFLECTIVE callers -
    /// <c>AnalysisRunLogTests.RunProofreadChunkedAsync_*</c> builds the argument array by POSITION through
    /// <c>GetMethod(...).Invoke(...)</c>, so a reordered or inserted parameter does NOT fail the build; it
    /// fails at run time as an argument-count/type mismatch, or worse, binds silently. Grep the test
    /// assembly for the method name whenever this list changes.
    /// </param>
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
        AiTier tier,
        Guid jobId,
        AnalysisContext context,
        CancellationToken ct)
    {
        var taskType = MapToTaskType(AnalysisType.Proofread);
        // The book's tier arrives from RunAsync's single read (be-c03) and is stamped on every chunk below.
        // THE load-bearing stamp of the two chunked paths: Proofread IS allowlisted, and a long chapter routes
        // here instead of through RunAsync's single-shot request, so without this a "thinking" book would
        // silently proofread on the local model for exactly the chapters long enough to matter.
        //
        // WHY IT IS A PARAMETER AND NOT A SECOND READ (be-c03). chunkTargetWords was computed from the tier
        // too - the sizing's bound (B) reads the ROUTED provider's num_ctx - so a re-read here could observe a
        // tier flipped between the two and size the chunks for one provider while sending them to another.
        // INERT AT THE SHIPPED VALUES: both tiers resolve Proofread at num_ctx 4096, so the chunk target does
        // not move with the tier at all (pinned by ChunkThresholdTierParityTests
        // .TheShippedThinkingTier_DoesNotMoveTheClientFacingThresholds). This is a structural guarantee that
        // the two decisions cannot disagree, not the repair of a live defect.
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

        var overallSw = Stopwatch.StartNew();
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var corrected = new string[chunks.Count];
        var chunkOutcomes = new ConcurrentBag<AnalysisChunkOutcome>();

        async Task ProcessChunk(int index)
        {
            var chunk = chunks[index];
            var text = chunk.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                var emptyOrWhitespace = text ?? "";
                corrected[index] = emptyOrWhitespace;
                chunkOutcomes.Add(CreateChunkOutcome(
                    chunkIndex: index,
                    inputText: emptyOrWhitespace,
                    outputText: emptyOrWhitespace,
                    durationMs: 0,
                    outcome: "Succeeded"));

                var chunkNumber = index + 1;
                _progress.ChunkStarted(jobId, chunkNumber, chunks.Count);
                _progress.ChunkCompleted(jobId, chunkNumber, chunks.Count);
                return;
            }
            await semaphore.WaitAsync(ct);
            var chunkSw = Stopwatch.StartNew();
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
                    SourceId = targetId.ToString(),
                    Tier = tier
                };
                var response = await _router.CompleteAsync(request, ct);
                var raw = response.Content ?? "";
                var clean = SanitizeResponse(raw);
                if (string.IsNullOrWhiteSpace(clean) && !string.IsNullOrEmpty(raw))
                {
                    var afterThink = ExtractTextAfterThinkBlock(raw);
                    clean = !string.IsNullOrWhiteSpace(afterThink) ? afterThink : raw.Trim();
                }
                var chunkOutcomeOutcome = "Succeeded";
                var chunkOutcomeOutputText = clean;
                if (string.IsNullOrWhiteSpace(clean))
                {
                    chunkOutcomeOutcome = "FallbackEmpty";
                    // Preserve prior semantics: the fallback outcome stores empty output,
                    // while merging still uses the original chunk text.
                    chunkOutcomeOutputText = "";
                    clean = text;
                }
                clean = StripTextToCorrectMarkers(clean);
                var chunkOutcome = CreateChunkOutcome(
                    chunkIndex: index,
                    inputText: text,
                    outputText: chunkOutcomeOutputText,
                    durationMs: chunkSw.ElapsedMilliseconds,
                    outcome: chunkOutcomeOutcome);

                // Only run unrelated/repetition heuristics when we actually have a "Succeeded" output.
                // For FallbackEmpty we keep the original chunk for merging and avoid mutating the fallback outcome.
                if (chunkOutcomeOutcome == "Succeeded")
                {
                    var unrelated = IsProofreadResultUnrelated(text, clean, out var similarity);
                    chunkOutcome.WordSimilarity = similarity;
                    if (unrelated)
                    {
                        _logger.LogWarning(
                            "Proofread chunk {Index} result may be unrelated (input prefix='{InputPrefix}', result prefix='{ResultPrefix}'). Falling back to original text.",
                            index + 1,
                            TruncateForAudit(text, 150),
                            TruncateForAudit(clean, 150));
                        chunkOutcome.Outcome = "FallbackUnrelated";
                        chunkOutcome.Note = $"similarity={similarity:F2}";
                        clean = text;
                    }
                    else if (clean.Length > text.Length * 1.3 + 200)
                    {
                        _logger.LogWarning(
                            "Proofread chunk {Index} result is {Ratio:P0} longer than input (input={InputLen}, result={ResultLen}). Likely AI repetition loop; falling back to original text.",
                            index + 1,
                            (double)clean.Length / text.Length - 1,
                            text.Length,
                            clean.Length);
                        chunkOutcome.Outcome = "FallbackRepetition";
                        chunkOutcome.Note = $"{(double)clean.Length / text.Length - 1:P0} longer than input";
                        clean = text;
                    }
                }

                chunkOutcomes.Add(chunkOutcome);
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
                chunkOutcomes.Add(CreateChunkOutcome(
                    chunkIndex: index,
                    inputText: text,
                    outputText: "",
                    durationMs: chunkSw.ElapsedMilliseconds,
                    outcome: "FallbackError",
                    note: ex.Message.Length > 200 ? ex.Message[..200] : ex.Message));
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
        overallSw.Stop();

        var merged = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            merged.Append(corrected[i]);
            if (i < chunks.Count - 1 && !string.IsNullOrEmpty(chunks[i].SeparatorAfter))
                merged.Append(chunks[i].SeparatorAfter);
        }
        var mergedResultText = StripTextToCorrectMarkers(merged.ToString());

        _logger.LogInformation("Proofread merge complete: merged length {Len} chars", mergedResultText.Length);

        var noChangesHint = IsProofreadResultNearlyIdentical(inputText, mergedResultText);
        if (noChangesHint)
            _logger.LogWarning("Proofread (chunked) merged result nearly identical to input (input={InputLen} chars, result={ResultLen} chars).", inputText.Length, mergedResultText.Length);

        await ArchivePreviousActiveAsync(bookId, chapterId, sceneId, scope, AnalysisType.Proofread, ct);

        var result = new AnalysisResult
        {
            ChapterId = chapterId,
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
            // "chunked" is an internal sentinel: the run fanned out over many per-chunk model calls so
            // there is no single model to surface. The FE suppresses this exact token in result headings
            // (visibleModelName in visible-model-name.ts); a rename here must be mirrored FE-side.
            ModelName = "chunked",
            ProofreadNoChangesHint = noChangesHint,
            // ProofreadResultUnreliable is set below, AFTER AttachSuggestions populates result.Suggestions,
            // from the only meaningful merged-level failure signal: dropped content. It is deliberately NOT
            // driven by noChangesHint - a nearly-identical merged output is the EXPECTED shape of a genuinely
            // clean long chapter, not a failure. (Per-chunk unrelated/empty chunks already fall back to the
            // original chunk text, so they never surface as a merged-level anomaly; a repetition loop yields
            // LONGER/garbled output, which is not nearly-identical, so noChangesHint would not catch it anyway.)
            JobId = jobId,
            SourceTextSnapshot = TextNormalization.NormalizeTextForAnalysis(inputText)
        };

        AttachSuggestions(result, inputText, AnalysisType.Proofread, structuredJson: null, cleanContent: mergedResultText, isStreaming: false, isRunWithInput: false, applyProofreadHeuristics: false, language: language);

        // Chunked-path unreliable signal: the model DROPPED a span of the input (an omission). Suggestions
        // are now populated (AttachSuggestions ran), so the merged input/output/suggestions are all in scope.
        // A nearly-identical / clean merged output is NOT unreliable (it is the normal shape of a clean long
        // chapter); only dropped content is. Empty/unrelated chunks already fell back to original text per
        // chunk, so the meaningful merged-level failure is dropped content.
        result.ProofreadResultUnreliable = ProofreadDroppedContent(inputText, mergedResultText, result.Suggestions);

        _db.AnalysisResults.Add(result);

        PersistChunkedRunLog(
            jobId: jobId,
            result: result,
            bookId: bookId,
            chapterId: chapterId,
            sceneId: sceneId,
            scope: scope,
            analysisTypeString: AnalysisType.Proofread.ToString(),
            language: language,
            totalChunks: chunks.Count,
            chunkOutcomes: chunkOutcomes,
            inputText: inputText,
            outputText: mergedResultText,
            durationMs: overallSw.ElapsedMilliseconds,
            noChangesHint: result.ProofreadNoChangesHint);

        // Persist, THEN signal Succeeded — the FE GETs analysis-jobs/{jobId} the moment it sees Succeeded, so
        // the row has to be committed first. See PersistThenMarkJobSucceededAsync.
        await PersistThenMarkJobSucceededAsync(jobId, "Proofread finished", ct);

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
            // Every successful parse/salvage path is routed through NormalizeLineEditResultJson so the
            // persisted StructuredResult (which the FE reads directly, not just the derived cards) is
            // deduped + no-op-stripped + capped. See NormalizeLineEditSuggestions for the exact rules.
            var result = TryExtractAndReserializeWithLogging<LineEditResult>(content, AnalysisType.LineEdit);
            if (result != null) return NormalizeLineEditResultJson(result, _logger);

            // Aggressive retry: strip all markdown fences/formatting, bidi, then try direct deserialize
            result = TryLineEditAggressiveParse(content);
            if (result != null)
            {
                _logger.LogInformation("LineEdit aggressive parse fallback succeeded after primary parse failed.");
                return NormalizeLineEditResultJson(result, _logger);
            }

            // Final fallback: salvage truncated JSON by keeping only fully-closed suggestion objects
            result = SalvageTruncatedLineEditJson(content);
            if (result != null)
            {
                _logger.LogInformation("LineEdit truncation salvage succeeded: recovered partial suggestions from truncated JSON.");
                return NormalizeLineEditResultJson(result, _logger);
            }

            // XML-like fallback: when the model returns a structured but non-JSON response
            // (e.g. <edit><instruction>...</instruction></edit>), salvage it into a minimal
            // LineEditResult with only OverallFeedback populated so the user still sees
            // high-level feedback instead of an empty result.
            var xmlFallback = TryLineEditXmlFallback(content);
            if (xmlFallback != null)
            {
                _logger.LogInformation(
                    "LineEdit XML fallback produced OverallFeedback from non-JSON structured output.");
                return NormalizeLineEditResultJson(xmlFallback, _logger);
            }

            _logger.LogWarning(
                "LineEdit structured parse: all extraction methods failed (primary, aggressive, salvage, XML). Content length={Len}, preview={Preview}",
                content.Length,
                TruncateForAudit(content, 200));
            return null;
        }

        return type switch
        {
            AnalysisType.LinguisticAnalysis => TryExtractAndReserialize<LinguisticAnalysisResult>(content, _logger),
            AnalysisType.LiteraryAnalysis => TryExtractAndReserialize<LiteraryAnalysisResult>(content, _logger),
            AnalysisType.BookOverview => TryExtractAndReserialize<BookOverviewResult>(content, _logger),
            AnalysisType.CharacterAnalysis => TryExtractAndReserialize<CharacterAnalysisResult>(content, _logger),
            AnalysisType.StoryAnalysis => TryExtractAndReserialize<StoryAnalysisResult>(content, _logger),
            AnalysisType.QA => TryExtractAndReserialize<QAResult>(content, _logger),
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
            // not the rest of the line - safe regardless of newline presence.
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

    internal static string? TryExtractAndReserialize<T>(string content, ILogger? logger = null) where T : class
    {
        try
        {
            var json = ExtractJson(content);
            if (json == null) return null;

            // Tolerate a single model typo in a KNOWN top-level key (e.g. "narriceVoiceDescription"
            // -> "narrativeVoiceDescription") so a field is not silently dropped in the FE.
            // KEY NAMES only, never values/enum values; fail-safe (bad input -> unchanged). See
            // p4-key-tolerance in analysis-output-repair-2026-07-03.plan.md.
            json = RepairNearMissKeys<T>(json, logger);

            var parsed = JsonSerializer.Deserialize<T>(json, JsonOpts);
            if (parsed == null) return null;

            return JsonSerializer.Serialize(parsed, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Per-type cache of the KNOWN top-level JSON key names (from each property's
    // [JsonPropertyName], falling back to the camelCase property name to match JsonOpts).
    private static readonly ConcurrentDictionary<Type, string[]> KnownJsonKeyCache = new();

    private static string[] GetKnownJsonKeys<T>()
    {
        return KnownJsonKeyCache.GetOrAdd(typeof(T), static t =>
        {
            var keys = new List<string>();
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
                var name = attr?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
                if (!string.IsNullOrEmpty(name))
                    keys.Add(name);
            }
            return keys.ToArray();
        });
    }

    /// <summary>
    /// Bounded, fail-safe repair of a single model typo in a KNOWN top-level JSON key so a field is
    /// not silently lost (real case: "narriceVoiceDescription" -> "narrativeVoiceDescription", which
    /// left the FE reading blank). For each known schema key that is ABSENT from the top-level object,
    /// if EXACTLY ONE present, not-already-known, not-yet-claimed key is a near-match
    /// (Levenshtein &lt;= 3 AND length within 2), it is renamed to the known key. Zero or more-than-one
    /// candidate = ambiguous = left untouched. Symmetrically, a present typo key that is a near-miss to
    /// TWO OR MORE absent known keys is ALSO ambiguous and left untouched (else it would silently bind to
    /// whichever known key is first in reflection order). A key already present (case-insensitively,
    /// matching the deserializer) is never overwritten (no clobber).
    /// NOTE: the real "narriceVoiceDescription" vs "narrativeVoiceDescription" typo is Levenshtein
    /// distance 3 (length diff 2), NOT 2 as the p4-key-tolerance plan text states; the bound is 3 so the
    /// documented real fixture actually binds. Still tiny/bounded, paired with the length window +
    /// ambiguity + no-clobber + not-a-known-key guards.
    /// Only KEY NAMES are touched — never values, never enum values (e.g. significance "majr" is left
    /// as-is). Nested objects/arrays are OUT OF SCOPE (top-level keys only); the real failure was a
    /// top-level key, and nested-key repair is future work. Any parse issue / non-object JSON returns
    /// the input unchanged (never throws). Re-serializes only when a rename actually fires, so clean
    /// JSON passes through byte-identical.
    /// </summary>
    internal static string RepairNearMissKeys<T>(string json, ILogger? logger = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return json; // not parseable -> leave unchanged (deserialize will fail the same way it did before)
        }

        if (obj == null) return json; // top-level array/primitive -> out of scope

        var knownKeys = GetKnownJsonKeys<T>();
        var knownSet = new HashSet<string>(knownKeys, StringComparer.OrdinalIgnoreCase);

        // Present top-level keys, compared case-insensitively to mirror JsonOpts.PropertyNameCaseInsensitive.
        var presentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in obj)
            presentKeys.Add(kvp.Key);

        var renames = new List<(string From, string To)>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Symmetric ambiguity guard (be-c03 / P3-4): the matchCount>1 bail in the loop below covers
        // "one ABSENT known key matched by MULTIPLE present typos". This guards the MIRROR case — ONE
        // present non-known key that is a near-miss (Levenshtein <=3 AND length window <=2, the SAME
        // bounds as below) to TWO OR MORE ABSENT known keys. Without it such a key silently binds to
        // whichever known key is first in reflection order (GetKnownJsonKeys -> GetProperties), which
        // could land the value under the WRONG field. A candidate ambiguous across >1 absent known key
        // is excluded up-front and left untouched. A key that is a near-miss to EXACTLY ONE absent
        // known key still renames (the real narriceVoiceDescription -> narrativeVoiceDescription case).
        var ambiguousCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in obj)
        {
            var key = kvp.Key;
            if (knownSet.Contains(key)) continue; // only present NON-known keys can be candidates
            var nearAbsentKnownCount = 0;
            foreach (var known in knownKeys)
            {
                if (presentKeys.Contains(known)) continue; // only ABSENT known keys are rename targets
                if (Math.Abs(key.Length - known.Length) > 2) continue; // length window (<= 2), mirrors the loop below
                var d = BoundedLevenshtein(key, known, 3);
                if (d >= 1 && d <= 3)
                {
                    nearAbsentKnownCount++;
                    if (nearAbsentKnownCount > 1) break; // ambiguous across >1 absent known key
                }
            }
            if (nearAbsentKnownCount > 1)
                ambiguousCandidates.Add(key);
        }

        foreach (var known in knownKeys)
        {
            // Only fill in an ABSENT known key; never overwrite one already present (no clobber).
            if (presentKeys.Contains(known)) continue;

            string? candidate = null;
            var matchCount = 0;
            foreach (var kvp in obj)
            {
                var key = kvp.Key;
                if (knownSet.Contains(key)) continue;   // a candidate must not itself be a known key
                if (claimed.Contains(key)) continue;    // already used by an earlier rename
                if (ambiguousCandidates.Contains(key)) continue; // symmetric guard: near-miss to >1 absent known key
                if (Math.Abs(key.Length - known.Length) > 2) continue; // length window (<= 2)
                var dist = BoundedLevenshtein(key, known, 3);
                if (dist >= 1 && dist <= 3)
                {
                    candidate = key;
                    matchCount++;
                    if (matchCount > 1) break; // ambiguous -> bail, leave untouched
                }
            }

            if (matchCount == 1 && candidate != null)
            {
                renames.Add((candidate, known));
                claimed.Add(candidate);
            }
            // zero or >1 candidate -> leave alone (bounded / safe)
        }

        if (renames.Count == 0) return json; // no near-miss -> pass through byte-identical

        foreach (var (from, to) in renames)
        {
            if (obj.ContainsKey(to)) continue; // defensive no-clobber (should not happen: 'to' was absent)
            var value = obj[from];
            // DeepClone detaches the node from its current parent so it can be re-added under the new key.
            var moved = value?.DeepClone();
            obj.Remove(from);
            obj[to] = moved;
            logger?.LogInformation(
                "Structured parse key repair ({Type}): renamed near-miss JSON key '{WrongKey}' -> '{CorrectKey}' (Levenshtein<=3, length window<=2).",
                typeof(T).Name, from, to);
        }

        try
        {
            return obj.ToJsonString();
        }
        catch
        {
            return json;
        }
    }

    /// <summary>
    /// Levenshtein edit distance between <paramref name="a"/> and <paramref name="b"/>, short-circuited
    /// at <paramref name="max"/>: as soon as an entire DP row exceeds max it returns max+1 (the exact value
    /// past the bound does not matter to the caller). Case-sensitive. No external dependency.
    /// </summary>
    private static int BoundedLevenshtein(string a, string b, int max)
    {
        if (a == b) return 0;
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > max) return max + 1;
        if (la == 0) return lb;
        if (lb == 0) return la;

        var prev = new int[lb + 1];
        var curr = new int[lb + 1];
        for (var j = 0; j <= lb; j++) prev[j] = j;

        for (var i = 1; i <= la; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            var ca = a[i - 1];
            for (var j = 1; j <= lb; j++)
            {
                var cost = ca == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                curr[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > max) return max + 1; // no cell can drop below max on later rows
            (prev, curr) = (curr, prev);
        }

        return prev[lb];
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

    // Delegates to the shared AnalysisTaskMapping so this and the budget-aware BookContextAssembler resolve
    // the same AnalysisType → AiTaskType routing (single source of truth).
    private static AiTaskType MapToTaskType(AnalysisType analysisType) =>
        AnalysisTaskMapping.ToAiTaskType(analysisType);

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

    /// <summary>
    /// The single, shared insertion point for the analysis-output repair layer. Called at all three
    /// non-Proofread seams (RunAsync, RunWithInputAsync, streaming) right after structuredJson +
    /// cleanContent are finalised and before the <see cref="AnalysisResult"/> is built. Governed end-to-end
    /// by <c>Ai:AnalysisRepair</c> (<see cref="Ai.AnalysisRepairOptions"/>):
    ///
    ///   • block null OR <c>Enabled=false</c> → FULL no-op: NO stage runs; inputs returned unchanged.
    ///   • <c>Enabled=true</c> + a <c>PerType</c> gate that excludes this type → skipped (every stage).
    ///   • <c>Enabled=true</c> + type allowed → <see cref="Ai.AnalysisRepairOptions.Mode"/> (d4) then selects
    ///     WHICH of the glossary / dynamic stages run (see <see cref="Ai.AnalysisRepairMode"/>); the
    ///     value-scoped LLM stage below is a further, orthogonal knob (<c>GuardOnly</c>).
    ///
    ///   Glossary stage — DETERMINISTIC glossary pass (<see cref="GlossaryRepairPass"/>): English-&gt;Hebrew
    ///   substitution over the whitelisted PROSE fields, itself guard-gated + fail-safe (a no-op for
    ///   Proofread, a non-Hebrew book, or any term the closed glossary does not cover). Runs when
    ///   Mode is <c>Glossary</c> or <c>GlossaryThenDynamic</c>.
    ///   Dynamic stage (d4) — SPAN-SCOPED dynamic repair (<see cref="DynamicTermRepairService.ApplyAsync"/>):
    ///   bidirectional detect-classify-repair, itself fail-safe. Runs when Mode is <c>Dynamic</c> or
    ///   <c>GlossaryThenDynamic</c> (in which case it runs AFTER the glossary, over whatever residual it left).
    ///   Value-scoped LLM stage — <see cref="AnalysisRepairService.RepairAnalysisAsync"/>: runs ONLY when
    ///   <c>GuardOnly=false</c>, independent of Mode. Guard-gated inside the service (a clean field makes
    ///   ZERO model calls) and fail-safe (can only ever leave a field cleaner). Never runs for Proofread.
    ///
    /// The SHIPPED appsettings default is { Enabled:true, GuardOnly:true, Mode:GlossaryThenDynamic } - the
    /// glossary fast-path THEN the dynamic span-scoped stage (the p3-gate GUARD-ONLY decision keeps the
    /// value-scoped LLM off; the d4 Mode default now layers the dynamic stage on). Mode=Glossary (or Off) is
    /// the rollback/kill-switch that reproduces the pre-d4/pre-p6 glossary-only behaviour. Setting Enabled=false, or Mode=Off,
    /// makes the whole layer (or just the glossary/dynamic half) a strict no-op per the plan.
    ///
    /// For LineEdit the prose-primary ResultText is refreshed from the repaired overallFeedback AFTER every
    /// stage; for Literary/Linguistic only StructuredResult changes (the FE renders it); for Summarization
    /// the passes return the repaired whole text.
    /// </summary>
    private async Task<(string? structuredJson, string cleanContent)> ApplyAnalysisRepairAsync(
        string? structuredJson,
        string cleanContent,
        AnalysisType analysisType,
        string language,
        Guid bookId,
        CancellationToken ct)
    {
        var cfg = _aiOptions.Value.AnalysisRepair;

        // FULL no-op when the layer is disabled. A null block (no Ai:AnalysisRepair in config) or
        // Enabled=false BOTH mean off — no stage runs, and the inputs are returned byte-identical. The
        // shipped appsettings block sets Enabled=true, so production runs the Mode-selected stage(s)
        // (glossary + dynamic under the shipped Mode=GlossaryThenDynamic). Also skip when a non-empty PerType map excludes this
        // analysis type (a type absent/false is skipped).
        //
        // h1-observable-gate-skip: those three reasons previously funnelled into the SAME silent return —
        // an operator staring at a skipped type could not tell which of them (each has a different fix:
        // bind the section / flip Enabled / add the type to PerType) closed the gate. AnalysisRepairGate is
        // the shared predicate (also consulted by BookIntelligenceService.RepairStructuredProfileJsonAsync and
        // BookReviewService's glossary/dynamic hooks) so this can name the reason without a divergent copy.
        // Debug ONLY: a gated-out type is a normal steady state (Proofread is skipped on every proofread
        // run) and must never produce INFO/WARN noise, mirroring the aggregate line's own no-noise-when-
        // healthy convention below.
        var gateReason = AnalysisRepairGate.Evaluate(cfg, analysisType.ToString());
        if (gateReason != AnalysisRepairGateReason.Allowed)
        {
            _logger.LogDebug(
                "AnalysisRepair: type={Type} gate closed ({Reason}); skipping repair layer",
                analysisType, gateReason);
            return (structuredJson, cleanContent);
        }

        // d4: Mode gates WHICH of the glossary/dynamic stages run, layered UNDER the Enabled/PerType gate
        // above. Off is an ADDITIONAL strict no-op scoped to stage selection. The shipped default
        // (GlossaryThenDynamic) runs the glossary fast-path THEN the dynamic stage; Mode=Glossary (or Off) is
        // the rollback that takes the IDENTICAL code path the pre-d4 layer always took.
        //
        // be-c06: expressed via the SHARED stage predicates rather than a longhand `mode == Off`, so this
        // early-out means exactly "no stage is selected -> strict no-op" and stays correct for any future
        // mode (and for a config-bound value outside the enum, which selects nothing). For Mode=Off this is
        // byte-identical to the previous check.
        // cfg is guaranteed non-null here: AnalysisRepairGate.Evaluate only returns Allowed when cfg is not
        // null (NullConfig otherwise), so the early-return above already ruled out the null case.
        var mode = cfg!.Mode;
        if (!mode.RunsGlossary() && !mode.RunsDynamic())
        {
            return (structuredJson, cleanContent);
        }

        // Observability (p6-observability, extended d4): time the whole repair layer and tally what each
        // stage did, then emit ONE aggregate line per run. It goes out at INFO only when the layer actually
        // flagged/changed something; a clean analysis logs a single Debug no-op line so a healthy run
        // produces no INFO noise. The Stopwatch/logging is wrapped so it can never throw or alter control flow.
        var repairSw = Stopwatch.StartNew();

        // Glossary stage — deterministic glossary pass. Runs when Mode is Glossary or GlossaryThenDynamic
        // (the shipped default is GlossaryThenDynamic, so this glossary stage runs FIRST, then the dynamic stage below); fail-safe
        // (a no-op for Proofread, a non-Hebrew book, or an out-of-glossary term). GlossaryRepairPass is
        // itself catch-all fail-safe, but the layer's load-bearing invariant is "the repair layer can
        // NEVER throw into RunAsync", so wrap this seam too (belt-and-braces): on ANY exception log and
        // keep the un-repaired inputs. Without this, a fault here (e.g. a model-emitted null JSON array
        // walked unguarded) would propagate through RunAsync and crash the entire analysis.
        // be-c06: the stage predicate lives ONCE, on the enum (AnalysisRepairModeExtensions.RunsGlossary) — the
        // BookReview engine hook calls the SAME predicate, so the two seams cannot drift apart.
        var glossaryChanged = 0;
        if (mode.RunsGlossary())
        {
            try
            {
                var repair = GlossaryRepairPass.Apply(analysisType, structuredJson, cleanContent, language, JsonOpts);
                structuredJson = repair.StructuredJson;
                cleanContent = repair.CleanContent;
                glossaryChanged = repair.FieldsChanged;

                // The glossary pass is itself fail-safe: an accessor-walk / re-serialize fault is CAUGHT
                // INSIDE Apply, which returns the inputs unchanged rather than throwing — so it never reaches
                // the catch below. Surface that swallowed fault here (via repair.Fault) and log it, otherwise
                // a repair that silently no-op'd would leave leaked English in the output with no warning.
                if (repair.Fault is not null)
                {
                    _logger.LogWarning(repair.Fault,
                        "AnalysisRepair Stage 1 (glossary) swallowed a fault for type={Type}; keeping un-repaired inputs (fail-safe)",
                        analysisType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AnalysisRepair Stage 1 (glossary) threw for type={Type}; keeping un-repaired inputs (fail-safe)",
                    analysisType);
            }
        }

        // Dynamic stage (d4) — span-scoped dynamic detect-and-repair. Runs when Mode is Dynamic (replacing
        // the glossary substitution entirely) or GlossaryThenDynamic (running AFTER the glossary, over
        // whatever residual foreign text it left). Runs under the shipped default (Mode=GlossaryThenDynamic), after the glossary; Mode=Glossary/Off is the rollback that skips it.
        // DynamicTermRepairService.ApplyAsync is itself catch-all fail-safe; wrap again here (belt-and-braces,
        // mirrors the glossary stage above) so a fault here can never propagate into RunAsync.
        // be-c06: shared stage predicate (AnalysisRepairModeExtensions.RunsDynamic) — same predicate the
        // BookReview engine hook's dynamic gate calls.
        var dynamicChanged = 0;
        if (mode.RunsDynamic())
        {
            try
            {
                // e3: fetch the per-book proper-noun LEAVE set LAZILY, ONLY on this dynamic path — under the
                // rollback Mode=Glossary/Off the dynamic path does not run, so it never hits the DbContext (the
                // lazy fetch avoids a needless per-analysis read on the non-dynamic modes). BookEntityProvider is deterministic and
                // returns an empty set on any fault / missing book / Guid.Empty (fail-safe = current behavior);
                // it is awaited INSIDE this try/catch, so even an unforeseen throw keeps the un-repaired inputs.
                //
                // final-r02: pass the SAME `language` that is handed to _dynamicTermRepair.ApplyAsync one line
                // below. The repair layer resolves the classifier's expected script from it, and the provider
                // resolves its HARVEST direction from it through the same helper — so the script harvested is
                // by construction the script the classifier looks up. (Keying the harvest on the book's STORED
                // language instead let an English-language analysis of a Hebrew book harvest LATIN tokens while
                // the classifier looked up HEBREW runs, silently disarming the entity lever; `language` here is
                // caller-overridable via RunAnalysisRequest.Language, so the two really can differ.)
                var bookEntities = await _bookEntityProvider.GetEntitiesAsync(bookId, language, ct).ConfigureAwait(false);
                var dynamicResult = await _dynamicTermRepair.ApplyAsync(
                    analysisType, structuredJson, cleanContent, language, JsonOpts, bookEntities, ct).ConfigureAwait(false);
                structuredJson = dynamicResult.structuredJson;
                cleanContent = dynamicResult.cleanContent;
                dynamicChanged = dynamicResult.fieldsChanged;

                if (dynamicResult.fault is not null)
                {
                    _logger.LogWarning(dynamicResult.fault,
                        "AnalysisRepair Stage dynamic (span-scoped) swallowed a fault for type={Type}; keeping un-repaired inputs (fail-safe)",
                        analysisType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AnalysisRepair Stage dynamic (span-scoped) threw for type={Type}; keeping un-repaired inputs (fail-safe)",
                    analysisType);
            }
        }

        // Value-scoped LLM stage — runs ONLY when NOT GuardOnly, independent of Mode. The shipped default is
        // GuardOnly=true, so this is skipped by default (no model calls beyond whatever Mode already ran).
        // Guard-gated + fail-safe inside the service (a clean field makes ZERO model calls; Proofread is
        // never routed). Counters (N/M/K) stay 0 while this stage is skipped.
        var llmFlagged = 0;
        var llmRepaired = 0;
        var llmFailSafe = 0;
        if (!cfg.GuardOnly)
        {
            var repairResult = await _analysisRepair.RepairAnalysisAsync(
                analysisType, structuredJson, cleanContent, language, JsonOpts, ct);
            structuredJson = repairResult.StructuredJson;
            cleanContent = repairResult.CleanContent;
            llmFlagged = repairResult.LlmFlagged;
            llmRepaired = repairResult.LlmRepaired;
            llmFailSafe = repairResult.LlmFailSafe;
        }

        // LineEdit is prose-primary via overallFeedback: re-derive ResultText from the repaired structured
        // feedback AFTER every stage (mirrors the pre-repair MaybeReplaceLineEditResultText call above).
        if (analysisType == AnalysisType.LineEdit)
        {
            cleanContent = MaybeReplaceLineEditResultText(analysisType, structuredJson, cleanContent);
        }

        repairSw.Stop();

        // One aggregate line per run. G = glossary fields changed; D = dynamic fields changed; N/M/K = LLM
        // fields flagged / accepted-and-changed / fail-safe-discarded. Structured placeholders (no
        // interpolation) so the fields are queryable.
        if (glossaryChanged > 0 || dynamicChanged > 0 || llmFlagged > 0)
        {
            _logger.LogInformation(
                "AnalysisRepair: type={Type} mode={Mode} glossaryChanged={G} dynamicChanged={D} llmFlagged={N} llmRepaired={M} llmFailSafe={K} totalMs={Ms}",
                analysisType, mode, glossaryChanged, dynamicChanged, llmFlagged, llmRepaired, llmFailSafe, repairSw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogDebug(
                "AnalysisRepair: type={Type} mode={Mode} no-op (glossaryChanged=0 dynamicChanged=0 llmFlagged=0) totalMs={Ms}",
                analysisType, mode, repairSw.ElapsedMilliseconds);
        }

        return (structuredJson, cleanContent);
    }

    // ─── Sanitization ───────────────────────────────────────────────

    /// <summary>
    /// Cleans a raw model response: strips think-blocks, the Syncfusion watermark, control
    /// characters, and CJK noise. Pure string-in/string-out (no instance state). Exposed as
    /// <c>internal</c> so the proofread quality eval measures the SAME corrected text production
    /// feeds to the diff, instead of a divergent test-only reimplementation.
    /// </summary>
    internal static string SanitizeResponse(string text)
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
    /// Merge the deterministic ktiv-male (full-spelling) suggestions into the LLM proofread suggestion
    /// list, resolving same-word conflicts so the NORMATIVE deterministic spelling wins the word.
    ///
    /// For each ktiv suggestion <c>km</c> (a single-token haser→male hit), against the proofread
    /// suggestions that overlap its span:
    ///  (a) no overlap  → add km (it is a new, independent suggestion).
    ///  (b) an overlapping proofread suggestion already agrees (same SuggestedText as km) → keep the
    ///      proofread one, drop km (no duplicate).
    ///  (c) the overlapping proofread suggestion(s) DIFFER from km and every one is contained within or
    ///      equal to km's token span (same-word competition) → remove those competing proofread
    ///      suggestions and add km (the deterministic normative spelling wins the word).
    ///  (d) an overlapping proofread suggestion extends BEYOND km's token span (a broader multi-word
    ///      LLM rewrite that happens to include this word) → keep the proofread suggestion, drop km (do
    ///      not fragment a broader fix).
    ///
    /// Offsets are nullable; suggestions without both offsets cannot overlap and are treated as
    /// non-overlapping. Order-stable: the returned list preserves the proofread order with any
    /// surviving ktiv suggestions appended in their original order. The caller re-assigns OrderIndex.
    /// </summary>
    internal static List<AnalysisSuggestion> MergeKtivMaleIntoProofread(
        List<AnalysisSuggestion> proofread,
        IReadOnlyList<AnalysisSuggestion> ktiv)
    {
        if (ktiv == null || ktiv.Count == 0)
            return proofread;

        foreach (var km in ktiv)
        {
            // A ktiv hit without a fully-anchored span cannot reason about overlaps; append it as-is
            // (matches the conservative "no overlap → add" path).
            if (!km.StartOffset.HasValue || !km.EndOffset.HasValue)
            {
                proofread.Add(km);
                continue;
            }

            var kmStart = km.StartOffset.Value;
            var kmEnd = km.EndOffset.Value;

            var overlapping = proofread
                .Where(s => s.StartOffset.HasValue && s.EndOffset.HasValue &&
                            kmStart < s.EndOffset.Value && s.StartOffset.Value < kmEnd)
                .ToList();

            // (a) No overlapping proofread suggestion → add km unchanged.
            if (overlapping.Count == 0)
            {
                proofread.Add(km);
                continue;
            }

            // (b) An overlapping proofread suggestion already agrees on the male form → keep it, drop km.
            if (overlapping.Exists(s => string.Equals(s.SuggestedText, km.SuggestedText, StringComparison.Ordinal)))
                continue;

            // (d) Any overlapping proofread suggestion extends BEYOND km's token span → it is a broader
            //     multi-word rewrite; keep it and drop km so we never fragment a wider fix.
            var anyExtendsBeyond = overlapping.Exists(s =>
                s.StartOffset!.Value < kmStart || s.EndOffset!.Value > kmEnd);
            if (anyExtendsBeyond)
                continue;

            // (c) Every overlapping proofread suggestion differs from km and is contained within/equal to
            //     km's token span → same-word competition; the deterministic spelling wins the word.
            proofread.RemoveAll(s => overlapping.Contains(s));
            proofread.Add(km);
        }

        return proofread;
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
        // Chapter/scene-scoped runs archive the prior active run for the SAME chapter; a book-scoped run
        // (QA / ask, chapterId == null) archives the prior active run for the SAME book+scope+type. The
        // nullable equality below is null-safe (EF translates a null chapterId to `ChapterId IS NULL`), so
        // book-scoped runs no longer accumulate unbounded Active rows (they used to early-return here).
        var previous = await _db.AnalysisResults
            .Include(a => a.Suggestions)
            .Where(a =>
                a.ChapterId == chapterId &&
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

    /// <summary>
    /// True when the proofread result looks like new/continuation content rather than
    /// a correction of the input (e.g. model wrote "Chapter 12" or "הנה המשך לסיפור").
    /// Uses word-overlap similarity after stripping scene/chapter break markers so that
    /// legitimate proofreading corrections (punctuation, spelling) are not falsely rejected.
    /// Any result whose prefix has low word-overlap similarity with the input is treated
    /// as unrelated, even if it does not contain explicit continuation marker phrases.
    /// </summary>
    private static bool IsProofreadResultUnrelated(string input, string result, out double wordSimilarity)
    {
        wordSimilarity = 0.0;
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(result)) return false;

        var inputClean = SceneAutoSplitRules.StripLeadingBreakMarkers(input);
        var resultClean = SceneAutoSplitRules.StripLeadingBreakMarkers(result);

        var inputStart = Regex.Replace(inputClean.TrimStart(), @"\s+", " ").Trim();
        var resultStart = Regex.Replace(resultClean.TrimStart(), @"\s+", " ").Trim();
        if (inputStart.Length < 30 || resultStart.Length < 30) return false;

        var inputPrefix = inputStart.Length <= 120 ? inputStart : inputStart[..120];
        var resultPrefix = resultStart.Length <= 200 ? resultStart : resultStart[..200];

        wordSimilarity = WordOverlapSimilarity(inputPrefix, resultPrefix);
        if (wordSimilarity >= 0.7)
            return false;

        var continuationMarkers = new[] { "הנה המשך לסיפור", "הנה המשך", "פרק 12", "**פרק 12", "Chapter 12" };
        foreach (var marker in continuationMarkers)
        {
            if (resultStart.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return true;
    }

    // Detects a proofread result where the model DROPPED a span of the input (an omission). Two signals:
    //   (a) the output is substantially shorter than the input (content vanished outright), OR
    //   (b) the diff produced a long CONTIGUOUS run of pure-deletion suggestions.
    // Signal (b) replaces the old "deletions dominate the diff" ratio, which false-positived on a heavily
    // edited but legitimate draft: many SCATTERED single-word deletions (e.g. doubled-word fixes spread
    // across the text) could exceed a count/ratio threshold without any span actually being dropped. A real
    // omission, by contrast, removes a CONTIGUOUS span, so its per-word deletion suggestions are
    // offset-adjacent in the input. We therefore flag only the longest run of consecutive, offset-adjacent
    // pure deletions - scattered legit deletions never form such a run. Thresholds are conservative to avoid
    // flagging normal proofreads (which are mostly replacements, similar length).
    private const double ProofreadShortOutputRatio = 0.9;        // output < 90% of input length => possible omission
    private const int    ProofreadMinDroppedSpanChars = 60;      // a run spanning >= 60 input chars => a span was dropped
    // The DELETED-CHARACTER floor is what distinguishes a dropped passage from normal copyediting. Hebrew
    // proofreading (ktiv-male full-spelling + punctuation) legitimately produces many adjacent single-char
    // deletions that together remove only a handful of characters - that is not an omission. A genuinely
    // dropped clause deletes well past this floor. (Observed false positive: 8 adjacent deletions removing
    // 19 chars total.)
    //
    // This is deliberately NOT also gated on a minimum deletion COUNT. SuggestionDiffService emits one
    // suggestion per contiguous edit, so a dropped clause can arrive as a SINGLE wide pure deletion rather
    // than the per-word run this check originally assumed; requiring 6+ separate deletions made an omission
    // invisible precisely when the diff described it most cleanly. The character floor holds either way.
    private const int    ProofreadMinRunDeletedChars = 35;
    // Two pure deletions are "contiguous" when only a small gap (a space/comma) separates them in the input.
    private const int    ProofreadDeletionContiguityGap = 3;
    // Signal (a) fires only when reviewable suggestions account for LESS than this fraction of the missing
    // characters - i.e. the text vanished SILENTLY. Scattered legit deletions each surface as a suggestion
    // that accounts for the chars it removed, so however many there are they never trip the length backstop.
    private const double ProofreadAccountedShrinkRatio = 0.5;

    // Removing an accidentally DUPLICATED sentence is a legitimate proofreading correction, not content
    // loss - and it looks identical to a dropped passage by size alone: one wide, contiguous pure deletion.
    // The two are distinguishable by whether the text SURVIVES: a de-duplication leaves its twin in the
    // output, a genuine omission leaves nothing. Only spans long enough to actually trip the dropped-span
    // thresholds are exempted, so a short common word deleted elsewhere in the text cannot excuse itself.
    private const int ProofreadMinDedupExemptChars = 20;

    // NOTE on the comparison axis: a suggestion's OriginalText comes off the diff, which runs on
    // NORMALIZED text (TextNormalization.NormalizeTextForAnalysis collapses each \r/\n to a space and
    // drops bidi controls), whereas the model's raw output still carries its line breaks. Comparing the
    // two directly made the Contains fail for any duplicated span spanning a line break - i.e. exactly
    // the multi-paragraph duplicate this exemption exists for - so the run was still flagged unreliable.
    // Both sides must therefore be on the normalized axis. The caller passes the ALREADY-normalized
    // output so the long string is normalized once per run rather than once per suggestion.
    private static bool IsDeduplicationNotContentLoss(AnalysisSuggestion deletion, string normalizedOutput)
    {
        var deleted = TextNormalization.NormalizeTextForAnalysis(deletion.OriginalText ?? string.Empty).Trim();
        if (deleted.Length < ProofreadMinDedupExemptChars) return false;
        return !string.IsNullOrEmpty(normalizedOutput) && normalizedOutput.Contains(deleted, StringComparison.Ordinal);
    }

    internal static bool ProofreadDroppedContent(string input, string output, ICollection<AnalysisSuggestion> suggestions)
    {
        if (string.IsNullOrEmpty(input)) return false;

        // (a) length backstop: the output is substantially shorter than the input. A shorter output is only a
        // RELIABILITY problem when the missing text vanished SILENTLY - i.e. it is NOT surfaced as reviewable
        // suggestions the user can see and revert. Legitimate scattered single-word deletions (signal (b)'s
        // allowed case) also shrink the output, but each is a suggestion that ACCOUNTS for the characters it
        // removed, so they must not trip this backstop merely by summing past 10%. We therefore fire (a) only
        // when the suggestions explain less than half of the missing characters - the signature of a dropped
        // span the diff failed to surface (e.g. one oversized pure-deletion that SuggestionDiffService
        // rejects, or a suggestion list that overflowed and was discarded). A real omission whose per-word
        // deletions ARE surfaced still gets caught by the contiguity check (b) below.
        if (!string.IsNullOrEmpty(output) && output.Length < input.Length * ProofreadShortOutputRatio)
        {
            var missingChars = input.Length - output.Length;
            var accountedShrink = suggestions == null ? 0 : suggestions.Sum(s =>
            {
                var origLen = (s.EndOffset ?? 0) - (s.StartOffset ?? 0);
                var sugLen = s.SuggestedText?.Length ?? 0;
                return Math.Max(0, origLen - sugLen);
            });
            if (accountedShrink < missingChars * ProofreadAccountedShrinkRatio)
                return true;
        }

        if (suggestions == null || suggestions.Count == 0) return false;

        // (b) contiguity check: order the pure-deletion suggestions (SuggestedText blank) by StartOffset and
        // walk them, accumulating a run while each deletion is offset-adjacent to the previous one. A null
        // StartOffset/EndOffset cannot be placed on the input axis, so such a suggestion BREAKS the current
        // run (it is skipped, and the next deletion starts a fresh run). Track the run's DELETED-CHARACTER
        // total and its covered character span (lastEnd - firstStart); the deletion count is only a
        // "has a run started" flag, not a criterion (see the ProofreadMinRunDeletedChars note above).
        // Hoisted out of the predicate below: the de-duplication exemption matches against the normalized
        // output, and normalizing a whole chapter once per suggestion inside the .Where would rescan the
        // string N times. Computed once per call so every suggestion sees the same normalized text. It
        // sits after the cheap early returns, but note it IS paid whenever any suggestion survives them,
        // including when none of them turns out to be a pure deletion.
        var normalizedOutput = TextNormalization.NormalizeTextForAnalysis(output ?? string.Empty);

        var deletions = suggestions
            .Where(s => string.IsNullOrWhiteSpace(s.SuggestedText))
            .Where(s => s.StartOffset.HasValue && s.EndOffset.HasValue)
            .Where(s => !IsDeduplicationNotContentLoss(s, normalizedOutput))
            .OrderBy(s => s.StartOffset!.Value)
            .ToList();
        if (deletions.Count == 0) return false;

        // Walk the ordered deletions, maintaining the CURRENT contiguous run's covered span (lastEnd -
        // firstStart) and the total characters it actually deletes (sum of each deletion's length). A run
        // signals a dropped passage when EITHER (i) it removes enough characters - many tiny micro-edits
        // that together delete almost nothing do NOT qualify, however many of them there are - OR (ii) it
        // covers a wide enough span, however few deletions make it up. Both are checked per-run so the two
        // totals always describe the SAME run. runCount is NOT a criterion; it only marks that a run is
        // open (a one-deletion run is a legitimate dropped clause, which is why the old 6-deletion gate
        // was removed).
        var runCount = 0;
        var runFirstStart = 0;
        var runDeletedChars = 0;
        var prevEnd = int.MinValue;
        foreach (var d in deletions)
        {
            var start = d.StartOffset!.Value;
            var end = d.EndOffset!.Value;
            var deletedLen = Math.Max(0, end - start);
            if (runCount > 0 && start - prevEnd <= ProofreadDeletionContiguityGap)
            {
                // contiguous with the previous deletion => extend the current run.
                runCount++;
                runDeletedChars += deletedLen;
            }
            else
            {
                // gap too large (or first deletion) => start a fresh run.
                runCount = 1;
                runFirstStart = start;
                runDeletedChars = deletedLen;
            }

            var runSpanChars = end - runFirstStart;
            if (runDeletedChars >= ProofreadMinRunDeletedChars
                || runSpanChars >= ProofreadMinDroppedSpanChars)
                return true;

            prevEnd = end;
        }

        return false;
    }

    private static readonly char[] WordSplitSeparators =
        [' ', '\t', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '-', '\u2013', '\u2014', '\n', '\r'];

    /// <summary>
    /// Fraction of words from <paramref name="a"/> that also appear in <paramref name="b"/>.
    /// Single-char words are ignored to avoid noise from punctuation remnants.
    /// </summary>
    private static double WordOverlapSimilarity(string a, string b)
    {
        var wordsA = a.Split(WordSplitSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1).ToArray();
        if (wordsA.Length == 0) return 1.0;

        var wordsB = new HashSet<string>(
            b.Split(WordSplitSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 1));

        int matches = wordsA.Count(w => wordsB.Contains(w));
        return (double)matches / wordsA.Length;
    }

    private void AttachSuggestions(
        AnalysisResult result,
        string inputText,
        AnalysisType analysisType,
        string? structuredJson,
        string cleanContent,
        bool isStreaming,
        bool isRunWithInput,
        bool applyProofreadHeuristics,
        bool? proofreadUnrelatedOverride = null,
        string? language = null)
    {
        if (analysisType == AnalysisType.Proofread)
        {
            if (applyProofreadHeuristics)
            {
                var noChanges = IsProofreadResultNearlyIdentical(inputText, cleanContent);
                // Similarity is computed once by the caller for normal (non-chunked) proofread runs.
                // AttachSuggestions only needs the boolean to decide whether to fall back to the original text.
                var invalidResult = proofreadUnrelatedOverride ??
                                     IsProofreadResultUnrelated(inputText, cleanContent, out _);
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

            // Deterministic Hebrew ktiv-male (full-spelling) sub-check. Appends haser→male spelling
            // suggestions for Hebrew text when the house-style toggle is on (Ai:HebrewStyle:EnforceKtivMale,
            // default ON). Suggestion-only (never auto-fix); gated to Hebrew so the English path is untouched.
            // The deterministic ktiv normative spelling WINS a same-word conflict (see
            // MergeKtivMaleIntoProofread for the per-suggestion conflict-resolution rules) so a (possibly
            // wrong) LLM rewrite touching the same token never silently suppresses the correct spelling.
            var ktivMaleSuggestions = _ktivMaleChecker.FindSuggestions(inputText, language ?? result.Language ?? string.Empty);
            suggestions = MergeKtivMaleIntoProofread(suggestions, ktivMaleSuggestions);

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
        else if (analysisType == AnalysisType.LinguisticAnalysis && structuredJson is not null)
        {
            // Additive: consistency issues from the structured linguistic result are also persisted
            // as navigate-only AnalysisSuggestions (with offsets where the span anchors). The
            // deviations/summary/consistency chips still read StructuredResult as before.
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<LinguisticAnalysisResult>(structuredJson, JsonOpts);
                if (parsed is not null)
                {
                    var suggestions = _suggestionDiff.ComputeConsistencyIssueSuggestions(parsed.ConsistencyIssues, inputText);
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
                        "LinguisticAnalysis AttachSuggestions: structured JSON deserialized to null LinguisticAnalysisResult. structuredResult preview={Preview}",
                        TruncateForAudit(structuredJson, 200));
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "LinguisticAnalysis AttachSuggestions: failed to deserialize structuredResult into LinguisticAnalysisResult. structuredResult preview={Preview}",
                    TruncateForAudit(structuredJson, 200));
                // Ignore malformed structured result; we still persist raw text + structured chips.
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
