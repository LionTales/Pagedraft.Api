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
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<AnalysisContextService> _logger;

    public AnalysisContextService(
        AppDbContext db,
        SfdtConversionService sfdtConversion,
        IAiRouter router,
        PromptFactory promptFactory,
        IOptions<AiOptions> aiOptions,
        ILogger<AnalysisContextService> logger)
    {
        _db = db;
        _sfdtConversion = sfdtConversion;
        _router = router;
        _promptFactory = promptFactory;
        _aiOptions = aiOptions;
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

        // LineEdit uses the surrounding paragraphs at any scope for contextual suggestions.
        // LinguisticAnalysis pulls the envelope ONLY at Scene scope, to detect cross-paragraph
        // consistency breaks (register/tense/POV) at scene boundaries WITHIN a chapter. At Chapter
        // scope the chapter is self-contained, so adjacent-chapter context is intentionally omitted
        // for LinguisticAnalysis: pulling it would surface cross-chapter issues that are not
        // navigable in this unit (the model could quote a span from a neighboring chapter). The
        // context-reading infrastructure (ResolveContextEnvelopeAsync / ContextField wiring) stays
        // in place for future cross-chapter features.
        string? precedingContext = null;
        string? followingContext = null;
        if (analysisType is AnalysisType.LineEdit
            || (analysisType is AnalysisType.LinguisticAnalysis && scope == AnalysisScope.Scene))
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

        // LinguisticAnalysis compares the analysed unit against a [CHAPTER_STYLE_BASELINE] reference,
        // but the reference DIFFERS by scope:
        //   • Scene scope  → the unit's OWN chapter baseline. We compare the scene against the chapter
        //     it lives in, so a register/length/density shift WITHIN the chapter surfaces as a deviation.
        //   • Chapter scope → the BOOK-WIDE average (mean of every chapter's metrics). Comparing the
        //     chapter against ITSELF would surface spurious `deviations` from stochastic recomputation,
        //     so instead we hand the model the book average: a chapter that diverges from the book's
        //     typical style is the meaningful signal. The book average is synthesised from the
        //     already-built per-chapter ChapterStyleProfile rows (no extra LLM call here).
        //   • Book scope   → no single chapter to compare; baseline stays null.
        // Fallback at every scope: a null baseline omits [CHAPTER_STYLE_BASELINE] and the model returns
        // `deviations: []`, which is the correct degraded behaviour.
        // NOTE: BookStyleAverages (the separate StyleProfileData field) is intentionally left null. The
        // numeric book reference is now delivered via ChapterStyleBaseline at Chapter scope; the
        // StyleProfileData-shaped BookStyleAverages slot is unused.
        ChapterStyleProfile? chapterStyleBaseline = null;
        if (analysisType is AnalysisType.LinguisticAnalysis && bookId.HasValue)
        {
            // Use the SAME language the user-facing analysis runs with (request override or normalized
            // code) so the baseline cache key, its build prompt, and [CHAPTER_STYLE_BASELINE] all agree
            // with the analysis language. Fall back to the book language only when none was supplied.
            var baselineLanguage = string.IsNullOrWhiteSpace(language)
                ? await ResolveLanguageAsync(bookId.Value, ct)
                : language;

            if (scope == AnalysisScope.Scene && chapterId.HasValue)
            {
                chapterStyleBaseline = await LoadOrBuildChapterStyleProfileAsync(
                    bookId.Value, chapterId.Value, baselineLanguage, ct);
            }
            else if (scope == AnalysisScope.Chapter)
            {
                chapterStyleBaseline = await BuildBookStyleAverageProfileAsync(
                    bookId.Value, baselineLanguage, ct);
            }
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

        // The active LinguisticAnalysis model the deviations would be compared against (config-resolved,
        // same resolution AiRouter uses). A profile built under a DIFFERENT model must not be served as a
        // baseline: comparing metrics across models is apples-to-oranges, so a model mismatch is treated
        // exactly like timestamp staleness (rebuild, or null when a rebuild is impossible).
        var activeModel = ActiveLinguisticModel;

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
                // outdated once the chapter changed. Apply the same freshness check as step 3 (timestamp
                // AND model): return the cached profile only when it is fresh; otherwise return null so
                // scene analysis does not inject a stale [CHAPTER_STYLE_BASELINE]. For a MODEL mismatch a
                // rebuild is impossible here (no text), so we return null rather than serve a cross-model
                // profile - same safety rationale as the timestamp-stale empty branch.
                if (existing != null && IsFresh(existing, chapter, activeModel))
                    return existing;
                return null;
            }

            // 3. Freshness check: there is NO cache invalidation on chapter edit or model change, so
            // compare BOTH the timestamp AND the model. A profile is fresh only when it was built at/after
            // the chapter's last edit AND under the active LinguisticAnalysis model; otherwise it is STALE
            // (the chapter changed, or the configured model changed) and must be rebuilt - otherwise the
            // deviations compare the current scene against an out-of-date OR cross-model snapshot.
            // NOTE: a pre-existing legacy row has BuiltWithModel == null, which never equals the active
            // model, so it is treated as stale and rebuilt ONCE on next access (expected one-time
            // self-heal that stamps the row with the active model).
            if (existing != null && IsFresh(existing, chapter, activeModel))
                return existing;

            // 4. Cache miss OR stale (timestamp or model): (re)compute chapter-level metrics from the
            // CURRENT text. The build also reports the model actually used, which we stamp below.
            var built = await ComputeChapterLinguisticMetricsAsync(chapterText, lang, ct);
            if (built == null)
                // Rebuild failed. We only reach here on a cache miss (existing == null) or a STALE
                // profile (step 3 already returned a fresh one), so `existing` is never current. Return
                // null rather than the stale row: injecting an outdated [CHAPTER_STYLE_BASELINE] would
                // produce spurious deviations. Mirrors the empty-content stale handling above.
                return null;

            var (metrics, builtModel) = built.Value;

            // 5. Serialize metrics honouring StructuredResults' [JsonPropertyName] conventions so
            // the FE/parse layer reads the same shape it expects from LinguisticAnalysisResult.
            var metricsJson = JsonSerializer.Serialize(metrics, MetricsJsonOpts);

            if (existing != null)
            {
                // Refresh the stale row in place (UpdatedAt re-stamped by SaveChanges override) and
                // restamp the model it was built with so the next freshness check passes.
                existing.MetricsJson = metricsJson;
                existing.BuiltWithModel = builtModel;
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
                MetricsJson = metricsJson,
                BuiltWithModel = builtModel
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
    /// Builds a SYNTHETIC, book-wide style baseline: the per-metric MEAN of the already-FRESH, same-model
    /// <see cref="ChapterStyleProfile"/> rows for (bookId, language). Used as the [CHAPTER_STYLE_BASELINE]
    /// reference at Chapter scope so a chapter is compared against the book's typical style, not itself.
    ///
    /// READ-ONLY, NO BUILD/REFRESH:
    /// This is the cheap, inline path invoked during a single Chapter-scope analysis. It performs ONLY DB
    /// reads and triggers NO LLM/rebuild work. It aggregates ONLY chapter profiles that are already FRESH
    /// per the single source of truth <see cref="ChapterStyleProfileFreshness.IsFresh"/> (timestamp AND
    /// active model). Timestamp-stale OR cross-model profiles (including legacy null-model rows) are
    /// EXCLUDED from the mean - never rebuilt and never served - which keeps the average cross-model-safe.
    /// The heavy work of (re)building chapter profiles belongs ONLY to the explicit Build/Refresh baseline
    /// job (<see cref="StyleBaselineService"/>); doing it here would fan a single Chapter analysis out to N
    /// sequential model calls (the regression this method was hardened against).
    ///
    /// Returns null when fewer than two FRESH same-model profiles are usable (a single chapter's "average"
    /// is just that chapter, which is not a meaningful book reference; null omits the section -> deviations
    /// []). After a model change or migration that leaves every profile model-stale, this returns null and
    /// the user must run the explicit Build/Refresh baseline job to repopulate - the intended consented
    /// degradation, not an inline rebuild storm.
    /// </summary>
    public async Task<ChapterStyleProfile?> BuildBookStyleAverageProfileAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "he" : language;

        try
        {
            // The active LinguisticAnalysis model the deviations are compared against. A profile built under
            // a DIFFERENT model is excluded (cross-model metrics are apples-to-oranges); this is the same
            // active-model resolution the (re)build gate uses.
            var activeModel = ActiveLinguisticModel;

            // 1. Read the persisted profile rows for (bookId, lang) WITH the fields needed to judge
            // freshness. AsNoTracking: this is a pure read; we never mutate or persist anything here.
            var profiles = await _db.ChapterStyleProfiles
                .AsNoTracking()
                .Where(p => p.BookId == bookId && p.Language == lang)
                .Select(p => new { p.ChapterId, p.UpdatedAt, p.BuiltWithModel, p.MetricsJson })
                .ToListAsync(ct);

            if (profiles.Count == 0)
                return null;

            // 2. Load the corresponding chapters' UpdatedAt so we can apply the timestamp half of freshness.
            // Joined in memory by ChapterId. AsNoTracking, projected to just the timestamp we need.
            var chapterIds = profiles.Select(p => p.ChapterId).Distinct().ToList();
            var chapterUpdatedAt = await _db.Chapters
                .AsNoTracking()
                .Where(c => chapterIds.Contains(c.Id))
                .Select(c => new { c.Id, c.UpdatedAt })
                .ToDictionaryAsync(c => c.Id, c => c.UpdatedAt, ct);

            // 3. Include a profile in the average ONLY when it is FRESH (timestamp AND model) per the single
            // shared definition. Excludes timestamp-stale AND cross-model (incl. legacy null-model) profiles
            // instead of rebuilding them. A profile whose chapter row is missing cannot be timestamp-judged,
            // so it is excluded as well.
            var parsed = new List<LinguisticAnalysisResult>();
            foreach (var profile in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.MetricsJson))
                    continue;

                if (!chapterUpdatedAt.TryGetValue(profile.ChapterId, out var chapterUpdated))
                    continue; // chapter gone → cannot judge freshness → exclude

                if (!ChapterStyleProfileFreshness.IsFresh(
                        profile.UpdatedAt, profile.BuiltWithModel, chapterUpdated, activeModel))
                    continue; // timestamp-stale OR cross-model → exclude (no rebuild)

                LinguisticAnalysisResult? metrics;
                try
                {
                    metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(profile.MetricsJson, JsonOpts);
                }
                catch (JsonException)
                {
                    metrics = null;
                }

                // A profile whose JSON carried an explicit "syntaxMetrics": null (or
                // "morphologyMetrics": null) deserializes with that sub-object NULL, overriding the
                // type's non-null default. The Average(...) lambdas below dereference both, so a single
                // such profile would NRE and the outer catch would degrade the ENTIRE book average to
                // null. Treat it like an unparseable profile: skip it and keep aggregating the rest.
                if (metrics != null && metrics.SyntaxMetrics != null && metrics.MorphologyMetrics != null)
                    parsed.Add(metrics);
            }

            // Fewer than two FRESH same-model profiles → the "average" is not a meaningful book reference.
            if (parsed.Count < 2)
                return null;

            // 4. Per-metric MEAN of the numeric syntax + morphology fields only. Non-numeric/text fields
            // (styleMetrics.formality, summary, deviations, ...) have no meaningful average and are left
            // at their defaults on the synthetic result.
            var averaged = new LinguisticAnalysisResult
            {
                SyntaxMetrics = new SyntaxMetrics
                {
                    SentenceCount = (int)Math.Round(parsed.Average(m => m.SyntaxMetrics.SentenceCount)),
                    AverageSentenceLength = parsed.Average(m => m.SyntaxMetrics.AverageSentenceLength),
                    ComplexSentences = (int)Math.Round(parsed.Average(m => m.SyntaxMetrics.ComplexSentences)),
                    ShortestSentence = (int)Math.Round(parsed.Average(m => m.SyntaxMetrics.ShortestSentence)),
                    LongestSentence = (int)Math.Round(parsed.Average(m => m.SyntaxMetrics.LongestSentence))
                },
                MorphologyMetrics = new MorphologyMetrics
                {
                    WordCount = (int)Math.Round(parsed.Average(m => m.MorphologyMetrics.WordCount)),
                    UniqueWords = (int)Math.Round(parsed.Average(m => m.MorphologyMetrics.UniqueWords)),
                    AverageWordLength = parsed.Average(m => m.MorphologyMetrics.AverageWordLength),
                    LexicalDensity = parsed.Average(m => m.MorphologyMetrics.LexicalDensity)
                },
                // grammaticalityScore is numeric and meaningfully averageable across chapters.
                GrammaticalityScore = parsed.Average(m => m.GrammaticalityScore)
                // StyleMetrics (text) / Summary / Deviations / ConsistencyIssues: left at defaults.
            };

            // 5. Return a SYNTHETIC ChapterStyleProfile carrying only the averaged MetricsJson. It is NOT
            // persisted (it is an aggregate, not a per-chapter cache row) and has no ChapterId; the prompt
            // renderer (FormatChapterStyleBaseline) reads only MetricsJson, so this is sufficient.
            return new ChapterStyleProfile
            {
                BookId = bookId,
                Language = lang,
                MetricsJson = JsonSerializer.Serialize(averaged, MetricsJsonOpts)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Degrade gracefully: Chapter-scope analysis still runs without a book-average baseline.
            _logger.LogWarning(ex, "Failed to build book-average style profile for book {BookId}", bookId);
            return null;
        }
    }

    /// <summary>
    /// Runs the same LLM-backed linguistic analysis that produces <see cref="LinguisticAnalysisResult"/>
    /// (the canonical prompt from <see cref="PromptFactory.GetAnalysisPrompt(AnalysisType,string)"/> driven
    /// through <see cref="IAiRouter"/>, exactly as LinguisticAnalysisEngine / UnifiedAnalysisService do),
    /// then parses the response into the typed metrics. Returns null on any LLM/parse failure.
    /// <para>
    /// Also surfaces <see cref="AiResponse.Model"/> (the model the request was ACTUALLY routed to) so the
    /// caller can stamp <see cref="ChapterStyleProfile.BuiltWithModel"/> with the real model rather than
    /// re-resolving it from config. Under normal config these agree (the provider sets Model from the
    /// same resolved selection), but the router-reported value is the most accurate.
    /// </para>
    /// </summary>
    private async Task<(LinguisticAnalysisResult Metrics, string? Model)?> ComputeChapterLinguisticMetricsAsync(
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
            var metrics = JsonSerializer.Deserialize<LinguisticAnalysisResult>(json, JsonOpts);
            if (metrics == null)
                return null;
            // Prefer the router-reported model (the model actually used); fall back to the config-resolved
            // active model if a provider left it blank, so BuiltWithModel is never null when we DID build.
            var model = string.IsNullOrWhiteSpace(response.Model) ? ActiveLinguisticModel : response.Model;
            return (metrics, model);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The resolved active LinguisticAnalysis model id from config (same resolution AiRouter uses, via the
    /// shared <see cref="LinguisticModelResolver"/>). Used as the cross-model staleness comparison target
    /// and as a fallback stamp value. May be null only when DefaultModel itself is null/empty.
    /// </summary>
    private string? ActiveLinguisticModel => LinguisticModelResolver.ResolveModel(_aiOptions.Value);

    /// <summary>
    /// Freshness gate for a cached profile against the current chapter + active model. Delegates to the
    /// shared <see cref="ChapterStyleProfileFreshness.IsFresh"/> so this gate and
    /// <see cref="StyleBaselineService.GetStatusAsync"/> use ONE staleness definition (timestamp AND model).
    /// </summary>
    private static bool IsFresh(ChapterStyleProfile profile, Chapter chapter, string? activeModel) =>
        ChapterStyleProfileFreshness.IsFresh(profile.UpdatedAt, profile.BuiltWithModel, chapter.UpdatedAt, activeModel);

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

