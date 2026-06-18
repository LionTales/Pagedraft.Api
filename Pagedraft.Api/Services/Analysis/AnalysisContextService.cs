using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Default implementation of <see cref="IAnalysisContextService"/> for Plan 0.
/// Focuses on resolving the target text and basic scope metadata, while leaving
/// hooks for richer context loading (BookBible, ChunkSummary, StyleProfile) in
/// later plans.
/// </summary>
public class AnalysisContextService : IAnalysisContextService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Serializer for ChapterStyleProfile.MetricsJson. [JsonPropertyName] attributes on the
    // StructuredResults types are honoured by System.Text.Json, so the persisted shape matches
    // what LinguisticAnalysisResult emits for the FE/parse layer.
    private static readonly JsonSerializerOptions MetricsJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const int CharacterPrepassMaxWords = 2000;
    private const int ContextEnvelopeMaxWords = 300;

    private readonly AppDbContext _db;
    private readonly SfdtConversionService _sfdtConversion;
    private readonly IAiRouter _router;
    private readonly PromptFactory _promptFactory;
    private readonly ILogger<AnalysisContextService> _logger;

    public AnalysisContextService(
        AppDbContext db,
        SfdtConversionService sfdtConversion,
        IAiRouter router,
        PromptFactory promptFactory,
        ILogger<AnalysisContextService> logger)
    {
        _db = db;
        _sfdtConversion = sfdtConversion;
        _router = router;
        _promptFactory = promptFactory;
        _logger = logger;
    }

    public async Task<AnalysisContext> BuildContextAsync(
        AnalysisScope scope,
        Guid targetId,
        AnalysisType analysisType,
        string language,
        CancellationToken ct = default)
    {
        var (text, bookId, chapterId, sceneId) = scope switch
        {
            AnalysisScope.Chapter => await ResolveChapterAsync(targetId, ct),
            AnalysisScope.Scene   => await ResolveSceneAsync(targetId, ct),
            AnalysisScope.Book    => await ResolveBookAsync(targetId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported analysis scope")
        };

        // LineEdit and LinguisticAnalysis both use the surrounding paragraphs: LineEdit for
        // contextual suggestions, LinguisticAnalysis to detect cross-paragraph consistency
        // breaks (register/tense/POV) at scene boundaries.
        string? precedingContext = null;
        string? followingContext = null;
        if (analysisType is AnalysisType.LineEdit or AnalysisType.LinguisticAnalysis)
        {
            (precedingContext, followingContext) =
                await ResolveContextEnvelopeAsync(scope, bookId, chapterId, sceneId, ct);
        }

        CharacterRegister? characters = null;
        if (bookId.HasValue && analysisType is AnalysisType.Proofread
            or AnalysisType.LiteraryAnalysis
            or AnalysisType.QA
            or AnalysisType.Synopsis)
        {
            characters = await LoadCharacterRegisterAsync(bookId.Value, text, ct);
        }

        StyleProfileData? styleProfile = null;
        if (bookId.HasValue && analysisType is AnalysisType.LineEdit
            or AnalysisType.LinguisticAnalysis
            or AnalysisType.LiteraryAnalysis
            or AnalysisType.Proofread)
        {
            styleProfile = await LoadStyleProfileAsync(bookId.Value, ct);
        }

        // LinguisticAnalysis compares a SCENE's metrics against its chapter baseline, so the baseline
        // is only meaningful at Scene scope. Skip it for Chapter scope: there the analysed text IS the
        // whole chapter, so comparing the chapter against its own (separately, stochastically computed)
        // metrics would surface spurious `deviations` even when nothing changed. Book scope has no
        // single chapter. The chapter-vs-book reference is deferred to Plan 5.
        // NOTE: BookStyleAverages is intentionally left null here. The previous wiring reused the
        // qualitative book StyleProfileData (already injected as [STYLE_PROFILE]), which duplicated
        // identical content under a [BOOK_STYLE_AVERAGES] marker that promised numeric metrics.
        // Real numeric book-average style metrics (mean of per-chapter ChapterStyleProfile metrics)
        // plus a book-comparison output field are deferred to Plan 5.
        ChapterStyleProfile? chapterStyleBaseline = null;
        if (scope == AnalysisScope.Scene && analysisType is AnalysisType.LinguisticAnalysis
            && bookId.HasValue && chapterId.HasValue)
        {
            // Use the SAME language the user-facing analysis runs with (request override or normalized
            // code) so the baseline cache key, its build prompt, and [CHAPTER_STYLE_BASELINE] all agree
            // with the analysis language. Fall back to the book language only when none was supplied.
            var baselineLanguage = string.IsNullOrWhiteSpace(language)
                ? await ResolveLanguageAsync(bookId.Value, ct)
                : language;
            chapterStyleBaseline = await LoadOrBuildChapterStyleProfileAsync(
                bookId.Value, chapterId.Value, baselineLanguage, ct);
        }

        return new AnalysisContext
        {
            TargetText = text,

            // Optional context fields – populated in later plans
            PrecedingContext = precedingContext,
            FollowingContext = followingContext,
            Characters = characters,
            StyleProfile = styleProfile,
            ChapterBrief = null,
            BookBrief = null,
            ChapterStyleBaseline = chapterStyleBaseline,
            // Deferred to Plan 5: real numeric book-average style metrics + book-comparison output.
            BookStyleAverages = null,

            Scope = scope,
            AnalysisType = analysisType,
            BookId = bookId,
            ChapterId = chapterId,
            SceneId = sceneId
        };
    }

    /// <summary>
    /// Loads the cached <see cref="ChapterStyleProfile"/> for (chapterId, language), or builds and
    /// persists one if absent. Mirrors the ChunkSummary cache-read-or-build idiom: look up by the
    /// unique (ChapterId, Language) key first; on a miss, load the chapter text via the existing
    /// resolver, compute the chapter-level linguistic metrics by reusing the same LLM-backed
    /// computation that produces <see cref="LinguisticAnalysisResult"/>, serialize those metrics to
    /// <see cref="ChapterStyleProfile.MetricsJson"/>, persist, and return the new row.
    /// Degrades gracefully (returns null) when the chapter has no analysable text or the LLM call fails.
    /// </summary>
    public async Task<ChapterStyleProfile?> LoadOrBuildChapterStyleProfileAsync(
        Guid bookId,
        Guid chapterId,
        string language,
        CancellationToken ct = default)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "he" : language;

        try
        {
            // 1. Cache read: existing profile for this chapter+language (may be STALE - see step 3).
            var existing = await _db.ChapterStyleProfiles
                .FirstOrDefaultAsync(p => p.ChapterId == chapterId && p.Language == lang, ct);

            // 2. Load the chapter (current text + last-edit timestamp). Reused for both the staleness
            // check and the (re)build, so the baseline always reflects the CURRENT chapter content.
            var chapter = await _db.Chapters
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == chapterId, ct);
            if (chapter == null)
                return existing; // no chapter -> return whatever is cached (possibly null)

            var chapterText = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
            if (string.IsNullOrWhiteSpace(chapterText))
            {
                // Chapter has no analysable content now (cleared or replaced with empty text). We cannot
                // rebuild a baseline from empty text, and a profile built from the PREVIOUS content is
                // outdated once the chapter changed. Apply the same freshness check as step 3: return the
                // cached profile only when it is NOT older than the chapter's last edit; otherwise return
                // null so scene analysis does not inject a stale [CHAPTER_STYLE_BASELINE] from the old
                // full chapter.
                if (existing != null && existing.UpdatedAt >= chapter.UpdatedAt)
                    return existing;
                return null;
            }

            // 3. Freshness check: there is NO cache invalidation on chapter edit, so compare
            // timestamps. A profile built at/after the chapter's last edit is fresh; one built
            // before it is STALE (the chapter changed since) and must be rebuilt - otherwise the
            // deviations compare the current scene against an out-of-date snapshot of the chapter.
            if (existing != null && existing.UpdatedAt >= chapter.UpdatedAt)
                return existing;

            // 4. Cache miss OR stale: (re)compute chapter-level metrics from the CURRENT text.
            var metrics = await ComputeChapterLinguisticMetricsAsync(chapterText, lang, ct);
            if (metrics == null)
                // Rebuild failed. We only reach here on a cache miss (existing == null) or a STALE
                // profile (step 3 already returned a fresh one), so `existing` is never current. Return
                // null rather than the stale row: injecting an outdated [CHAPTER_STYLE_BASELINE] would
                // produce spurious deviations. Mirrors the empty-content stale handling above.
                return null;

            // 5. Serialize metrics honouring StructuredResults' [JsonPropertyName] conventions so
            // the FE/parse layer reads the same shape it expects from LinguisticAnalysisResult.
            var metricsJson = JsonSerializer.Serialize(metrics, MetricsJsonOpts);

            if (existing != null)
            {
                // Refresh the stale row in place (UpdatedAt re-stamped by SaveChanges override).
                existing.MetricsJson = metricsJson;
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex)
                {
                    // Persisting the refreshed baseline failed. Detach the entity so its now-Modified
                    // state is NOT retried by a later SaveChangesAsync on this shared scoped DbContext
                    // (which would fail the whole analysis save). Return the freshly computed metrics so
                    // THIS analysis still uses an up-to-date baseline, just uncached.
                    _logger.LogWarning(ex, "Failed to refresh stale ChapterStyleProfile for chapter {ChapterId}", chapterId);
                    _db.Entry(existing).State = EntityState.Detached;
                }
                return existing;
            }

            var profile = new ChapterStyleProfile
            {
                BookId = bookId,
                ChapterId = chapterId,
                Language = lang,
                MetricsJson = metricsJson
                // CreatedAt/UpdatedAt are stamped by AppDbContext.SaveChanges override.
            };

            try
            {
                // Two concurrent analyses for the same chapter can both reach this insert path
                // before either has committed, violating IX_ChapterStyleProfiles_ChapterId_Language.
                // On a unique-constraint collision, reload the row the winning insert just wrote.
                _db.ChapterStyleProfiles.Add(profile);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Detach the failed entity so it is not retried by a later SaveChangesAsync
                // call on this same scoped DbContext (e.g. from UnifiedAnalysisService).
                _db.Entry(profile).State = EntityState.Detached;
                return await _db.ChapterStyleProfiles
                    .FirstOrDefaultAsync(p => p.ChapterId == chapterId && p.Language == lang, ct);
            }

            return profile;
        }
        catch (OperationCanceledException)
        {
            // Preserve cooperative cancellation so the outer analysis can stop immediately.
            throw;
        }
        catch (Exception ex)
        {
            // Any non-cancellation failure should degrade gracefully – analysis still runs
            // without a chapter style baseline.
            _logger.LogWarning(ex, "Failed to load/build ChapterStyleProfile for chapter {ChapterId}", chapterId);
            return null;
        }
    }

    /// <summary>
    /// Runs the same LLM-backed linguistic analysis that produces <see cref="LinguisticAnalysisResult"/>
    /// (the canonical prompt from <see cref="PromptFactory.GetAnalysisPrompt(AnalysisType,string)"/> driven
    /// through <see cref="IAiRouter"/>, exactly as LinguisticAnalysisEngine / UnifiedAnalysisService do),
    /// then parses the response into the typed metrics. Returns null on any LLM/parse failure.
    /// </summary>
    private async Task<LinguisticAnalysisResult?> ComputeChapterLinguisticMetricsAsync(
        string text,
        string language,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var instruction = _promptFactory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, language);
        var request = new AiRequest
        {
            InputText = text,
            Instruction = instruction,
            TaskType = AiTaskType.LinguisticAnalysis,
            Language = language,
            // JsonMode = true mirrors the main LinguisticAnalysis path (UnifiedAnalysisService line 357)
            // so the baseline build uses the same Ollama format=json path for reliable JSON parsing.
            JsonMode = true
        };

        AiResponse response;
        try
        {
            response = await _router.CompleteAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chapter linguistic metrics computation failed for LinguisticAnalysis baseline build");
            return null;
        }

        var raw = response.Content;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Reuse the SAME extractor the user-facing LinguisticAnalysis path uses
        // (UnifiedAnalysisService.ExtractJson): BOM/bidi stripping, balanced-brace matching,
        // any-tag fences, and a markdown-strip retry. A local first-'{'-to-last-'}' parser
        // would reject Hebrew/fenced/prose-wrapped JSON that the main path accepts, leaving the
        // ChapterStyleProfile baseline unbuilt and scene deviations without [CHAPTER_STYLE_BASELINE].
        var json = UnifiedAnalysisService.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LinguisticAnalysisResult>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Resolves the analysis language for a book, defaulting to Hebrew.</summary>
    private async Task<string> ResolveLanguageAsync(Guid bookId, CancellationToken ct)
    {
        var book = await _db.Books
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bookId, ct);
        return string.IsNullOrWhiteSpace(book?.Language) ? "he" : book.Language;
    }

    // ─── Target resolution (mirrors UnifiedAnalysisService.ResolveTarget) ─────

    private async Task<(string Text, Guid? BookId, Guid? ChapterId, Guid? SceneId)> ResolveChapterAsync(
        Guid chapterId,
        CancellationToken ct)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct)
            ?? throw new InvalidOperationException("Chapter not found");

        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No chapter text to analyze. Save the chapter first so the analysis has content.");

        return (text, chapter.BookId, chapterId, null);
    }

    private async Task<CharacterRegister?> LoadCharacterRegisterAsync(
        Guid bookId,
        string fullText,
        CancellationToken ct)
    {
        try
        {
            // 1. Prefer explicit register from BookBible when available
            var bible = await _db.BookBibles
                .FirstOrDefaultAsync(b => b.BookId == bookId, ct);

            if (!string.IsNullOrWhiteSpace(bible?.CharacterRegisterJson))
            {
                var fromBible = JsonSerializer.Deserialize<CharacterRegister>(
                    bible.CharacterRegisterJson,
                    JsonOpts);
                if (fromBible is { Characters.Count: > 0 })
                    return fromBible;
            }

            // 2. Fallback: cheap LLM pre-pass on first ~2000 words,
            // and persist the extracted register back to BookBible so
            // subsequent analyses can reuse it without another LLM call.
            var book = await _db.Books
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == bookId, ct);
            if (book == null) return null;

            var language = string.IsNullOrWhiteSpace(book.Language) ? "he" : book.Language;
            var extracted = await ExtractCharacterRegisterAsync(fullText, language, ct);
            if (extracted is { Characters.Count: > 0 })
            {
                var now = DateTimeOffset.UtcNow;
                if (bible == null)
                {
                    bible = new BookBible
                    {
                        BookId = bookId,
                        CreatedAt = now,
                        UpdatedAt = now,
                        CharacterRegisterJson = JsonSerializer.Serialize(extracted, JsonOpts)
                    };
                    _db.BookBibles.Add(bible);
                }
                else
                {
                    bible.CharacterRegisterJson = JsonSerializer.Serialize(extracted, JsonOpts);
                    bible.UpdatedAt = now;
                }

                await _db.SaveChangesAsync(ct);
            }

            return extracted;
        }
        catch (OperationCanceledException)
        {
            // Preserve cooperative cancellation so the outer analysis can stop immediately.
            throw;
        }
        catch (Exception)
        {
            // Any non-cancellation failure should degrade gracefully – proofread still runs without character info.
            return null;
        }
    }

    private async Task<StyleProfileData?> LoadStyleProfileAsync(
        Guid bookId,
        CancellationToken ct)
    {
        try
        {
            var bible = await _db.BookBibles
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.BookId == bookId, ct);

            var json = bible?.StyleProfileJson;
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<StyleProfileData>(json, JsonOpts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any non-cancellation failure should degrade gracefully – analyses still run without style info.
            return null;
        }
    }

    private async Task<CharacterRegister?> ExtractCharacterRegisterAsync(
        string fullText,
        string language,
        CancellationToken ct)
    {
        var truncated = TruncateToWords(fullText, CharacterPrepassMaxWords);
        if (string.IsNullOrWhiteSpace(truncated))
            return null;

        var instruction = _promptFactory.GetCharacterExtractionPrompt(language);
        var request = new AiRequest
        {
            InputText = truncated,
            Instruction = instruction,
            TaskType = AiTaskType.LinguisticAnalysis,
            Language = language
        };

        AiResponse response;
        try
        {
            response = await _router.CompleteAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            // Let cancellation propagate to the caller.
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        var raw = response.Content;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var json = ExtractJsonArray(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var entries = JsonSerializer.Deserialize<List<CharacterRegisterEntry>>(json, JsonOpts);
            if (entries is not { Count: > 0 })
                return null;

            return new CharacterRegister
            {
                Characters = entries
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(string? Preceding, string? Following)> ResolveContextEnvelopeAsync(
        AnalysisScope scope,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        CancellationToken ct)
    {
        try
        {
            return scope switch
            {
                AnalysisScope.Scene   => await ResolveSceneEnvelopeAsync(chapterId, sceneId, ct),
                AnalysisScope.Chapter => await ResolveChapterEnvelopeAsync(chapterId, ct),
                AnalysisScope.Book    => (null, null),
                _                     => (null, null)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any failure to load envelope context should degrade gracefully.
            return (null, null);
        }
    }

    private static string TruncateToWords(string text, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWords <= 0) return "";
        var matches = Regex.Split(text.Trim(), @"\s+");
        if (matches.Length <= maxWords) return text.Trim();
        var sb = new StringBuilder();
        var count = Math.Min(maxWords, matches.Length);
        for (var i = 0; i < count; i++)
        {
            if (matches[i].Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(matches[i]);
        }
        return sb.ToString();
    }

    private static string? ExtractJsonArray(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var text = content.Trim();

        // Handle fenced JSON blocks ```json ... ```
        var fenceMatch = Regex.Match(text, @"```(?:json)?\s*\n?([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success)
            text = fenceMatch.Groups[1].Value.Trim();

        // Find first '[' which should start the array
        var start = text.IndexOf('[');
        if (start < 0) return null;
        var end = text.LastIndexOf(']');
        if (end <= start) return null;

        return text.Substring(start, end - start + 1).Trim();
    }

    private async Task<(string? Preceding, string? Following)> ResolveSceneEnvelopeAsync(
        Guid? chapterId,
        Guid? sceneId,
        CancellationToken ct)
    {
        if (!sceneId.HasValue)
            return (null, null);

        // Load the target scene with its chapter so we can resolve siblings.
        var scene = await _db.Scenes
            .Include(s => s.Chapter)
            .FirstOrDefaultAsync(s => s.Id == sceneId.Value, ct);
        if (scene == null)
            return (null, null);

        var effectiveChapterId = chapterId ?? scene.ChapterId;

        var siblings = await _db.Scenes
            .Where(s => s.ChapterId == effectiveChapterId)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

        if (siblings.Count == 0)
            return (null, null);

        var index = siblings.FindIndex(s => s.Id == scene.Id);
        if (index < 0)
            return (null, null);

        var previousScene = index > 0 ? siblings[index - 1] : null;
        var nextScene = index < siblings.Count - 1 ? siblings[index + 1] : null;

        string? preceding = null;
        string? following = null;

        if (previousScene != null)
        {
            preceding = ExtractSceneTail(previousScene);
        }
        else
        {
            // First scene in chapter – use chapter opening paragraph as preceding context.
            var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == effectiveChapterId, ct);
            if (chapter != null)
            {
                var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
                var paragraphs = SplitIntoParagraphs(text);
                var firstPara = paragraphs.FirstOrDefault();
                preceding = string.IsNullOrWhiteSpace(firstPara) ? null : TruncateToWords(firstPara, ContextEnvelopeMaxWords);
            }
        }

        if (nextScene != null)
        {
            following = ExtractSceneHead(nextScene);
        }
        else
        {
            // Last scene in chapter – use chapter closing paragraph as following context.
            var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == effectiveChapterId, ct);
            if (chapter != null)
            {
                var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
                var paragraphs = SplitIntoParagraphs(text);
                var lastPara = paragraphs.LastOrDefault();
                following = string.IsNullOrWhiteSpace(lastPara) ? null : TakeLastWords(lastPara, ContextEnvelopeMaxWords);
            }
        }

        return (string.IsNullOrWhiteSpace(preceding) ? null : preceding,
            string.IsNullOrWhiteSpace(following) ? null : following);
    }

    private async Task<(string? Preceding, string? Following)> ResolveChapterEnvelopeAsync(
        Guid? chapterId,
        CancellationToken ct)
    {
        if (!chapterId.HasValue)
            return (null, null);

        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId.Value, ct);
        if (chapter == null)
            return (null, null);

        var previousChapter = await _db.Chapters
            .Where(c => c.BookId == chapter.BookId && c.Order < chapter.Order)
            .OrderByDescending(c => c.Order)
            .FirstOrDefaultAsync(ct);

        var nextChapter = await _db.Chapters
            .Where(c => c.BookId == chapter.BookId && c.Order > chapter.Order)
            .OrderBy(c => c.Order)
            .FirstOrDefaultAsync(ct);

        string? preceding = null;
        string? following = null;

        if (previousChapter != null)
        {
            var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(previousChapter.ContentText ?? "");
            var paragraphs = SplitIntoParagraphs(text);
            var lastPara = paragraphs.LastOrDefault();
            preceding = string.IsNullOrWhiteSpace(lastPara) ? null : TakeLastWords(lastPara, ContextEnvelopeMaxWords);
        }

        if (nextChapter != null)
        {
            var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(nextChapter.ContentText ?? "");
            var paragraphs = SplitIntoParagraphs(text);
            var firstPara = paragraphs.FirstOrDefault();
            following = string.IsNullOrWhiteSpace(firstPara) ? null : TruncateToWords(firstPara, ContextEnvelopeMaxWords);
        }

        return (string.IsNullOrWhiteSpace(preceding) ? null : preceding,
            string.IsNullOrWhiteSpace(following) ? null : following);
    }

    private string? ExtractSceneHead(Scene scene)
    {
        var sfdt = scene.ContentSfdt ?? "{}";
        var (plainText, _) = _sfdtConversion.GetTextFromSfdt(sfdt);
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(plainText);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return TruncateToWords(text, ContextEnvelopeMaxWords);
    }

    private string? ExtractSceneTail(Scene scene)
    {
        var sfdt = scene.ContentSfdt ?? "{}";
        var (plainText, _) = _sfdtConversion.GetTextFromSfdt(sfdt);
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(plainText);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return TakeLastWords(text, ContextEnvelopeMaxWords);
    }

    private static IReadOnlyList<string> SplitIntoParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var normalized = text.Replace("\r\n", "\n");
        var rawParagraphs = Regex.Split(normalized, @"\n\s*\n+");
        return rawParagraphs
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
    }

    private static string TakeLastWords(string text, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWords <= 0) return "";
        var words = Regex.Split(text.Trim(), @"\s+");
        if (words.Length <= maxWords) return text.Trim();
        var start = Math.Max(0, words.Length - maxWords);
        var span = words.AsSpan(start);
        return string.Join(" ", span.ToArray());
    }

    private async Task<(string Text, Guid? BookId, Guid? ChapterId, Guid? SceneId)> ResolveSceneAsync(
        Guid sceneId,
        CancellationToken ct)
    {
        var scene = await _db.Scenes
            .Include(s => s.Chapter)
            .FirstOrDefaultAsync(s => s.Id == sceneId, ct)
            ?? throw new InvalidOperationException("Scene not found");

        var sfdt = scene.ContentSfdt ?? "{}";
        var (plainText, _) = _sfdtConversion.GetTextFromSfdt(sfdt);
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(plainText);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Scene has no content to analyze. Edit the scene and save first.");

        return (text, scene.Chapter.BookId, scene.ChapterId, sceneId);
    }

    private async Task<(string Text, Guid? BookId, Guid? ChapterId, Guid? SceneId)> ResolveBookAsync(
        Guid bookId,
        CancellationToken ct)
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

}

