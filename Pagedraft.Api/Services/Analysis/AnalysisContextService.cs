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
    private readonly BookContextAssembler _bookContextAssembler;
    private readonly BookSummaryService _bookSummary;
    private readonly ILogger<AnalysisContextService> _logger;

    public AnalysisContextService(
        AppDbContext db,
        SfdtConversionService sfdtConversion,
        IAiRouter router,
        PromptFactory promptFactory,
        IOptions<AiOptions> aiOptions,
        BookContextAssembler bookContextAssembler,
        BookSummaryService bookSummary,
        ILogger<AnalysisContextService> logger)
    {
        _db = db;
        _sfdtConversion = sfdtConversion;
        _router = router;
        _promptFactory = promptFactory;
        _aiOptions = aiOptions;
        _bookContextAssembler = bookContextAssembler;
        _bookSummary = bookSummary;
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
            AnalysisScope.Book    => await ResolveBookAsync(targetId, analysisType, ct),
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

        // The LOAD gate asks PromptFactory the same question the RENDER gate answers, instead of carrying
        // its own copy of the type list (c04). The field table in PromptFactory.GetRelevantFields is the
        // single source of truth; this call is the whole binding, so adding ContextField.Characters to a
        // new row starts loading the register for that type with no edit here.
        // Not merely a de-duplication: loading is expensive and writing (LoadCharacterRegisterAsync can
        // fire an LLM extraction pre-pass and persist the merged register), and nothing but prompt
        // assembly consumes AnalysisContext.Characters, so a type that loads without rendering pays for a
        // value the model never sees. See the rationale on PromptFactory.RendersCharacterRegister.
        CharacterRegister? characters = null;
        if (bookId.HasValue && PromptFactory.RendersCharacterRegister(analysisType))
        {
            // `chapterId` is the ledger key (be-c01): it drives WHICH chapter contributes to the
            // register on this request. It is null only at Book scope, which is deliberately outside
            // the ledger and keeps the old one-shot bootstrap - see LoadCharacterRegisterAsync.
            characters = await LoadCharacterRegisterAsync(bookId.Value, text, chapterId, ct);
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
            // Normalize through the shared resolver (e.g. "en-US" → "en") so the inline profile/book-average
            // cache slot matches the one the background StyleBaselineService builder and status endpoints use;
            // otherwise a built baseline would look missing and chapter analyses would omit it.
            var baselineLanguage = BaselineLanguageResolver.Normalize(
                string.IsNullOrWhiteSpace(language)
                    ? await ResolveLanguageAsync(bookId.Value, ct)
                    : language);

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

        // wb1-c03: populate the structured narrative briefs (wb1-c02 made these read-able). These feed the
        // [BOOK_CONTEXT]/[CHAPTER_CONTEXT] prompt sections (PromptFactory.GetRelevantFields) for the
        // analysis types that consume them. Both are READ-ONLY projections from already-built L0/rollup data
        // (no LLM call here) and degrade to null when the book summary has not been built yet.
        ChapterBrief? chapterBrief = null;
        BookBrief? bookBrief = null;
        if (bookId.HasValue)
        {
            var briefLanguage = BaselineLanguageResolver.Normalize(
                string.IsNullOrWhiteSpace(language)
                    ? await ResolveLanguageAsync(bookId.Value, ct)
                    : language);

            (chapterBrief, bookBrief) = await LoadNarrativeBriefsAsync(
                bookId.Value, chapterId, analysisType, briefLanguage, ct);
        }

        return new AnalysisContext
        {
            TargetText = text,

            // Optional context fields – populated in later plans
            PrecedingContext = precedingContext,
            FollowingContext = followingContext,
            Characters = characters,
            StyleProfile = styleProfile,
            ChapterBrief = chapterBrief,
            BookBrief = bookBrief,
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
    /// <param name="tier">
    /// be-c02: the book's already-resolved tier, or null to resolve it from the database (the default,
    /// and the pre-be-c02 behaviour). See <see cref="IAnalysisContextService"/> for why a multi-chapter
    /// caller must pass its own value rather than let each call re-read the column.
    /// </param>
    public async Task<ChapterStyleProfile?> LoadOrBuildChapterStyleProfileAsync(
        Guid bookId,
        Guid chapterId,
        string language,
        CancellationToken ct = default,
        AiTier? tier = null)
    {
        // Normalize to the canonical cache key (e.g. "en-US" → "en") so this row lands in the SAME slot
        // whether built inline here or by the background StyleBaselineService.
        var lang = BaselineLanguageResolver.Normalize(language);

        // p3-3: the BOOK's model tier. Used for BOTH halves of the atomic pairing below - the request the
        // rebuild sends, and the active model the freshness gate compares against. See
        // ActiveLinguisticModelFor for why the two must move together.
        // be-c02: PREFER the caller's already-resolved value. A caller that spans several chapters
        // (StyleBaselineService's build) resolves the tier once and threads it here, so a flip that lands
        // mid-build cannot stamp its chapters under two different models. Only when no tier is supplied do
        // we read the column ourselves - today that is the inline BuildContextAsync path, which builds ONE
        // profile per call.
        // final-r02, stated so the next reader does not over-read the line above: "no tier supplied" is NOT
        // the same as "only one tier read in the whole operation". BuildContextAsync is reached from
        // UnifiedAnalysisService.RunAsync, which resolves the book's tier again for its own request, so a
        // Scene/Chapter-scope LinguisticAnalysis run still reads Book.AiTier twice and a flip between the two
        // gates this baseline at one tier while the analysis request routes at the other. That is BOUNDED -
        // the only consequence is that this chapter's profile reads stale on the next run and rebuilds once,
        // which is the freshness gate working - and threading it would mean widening BuildContextAsync, which
        // p3-3 deferred deliberately. What is NOT bounded, and is what this parameter exists for, is the
        // multi-chapter build above.
        // tier-ux-rework c1: the task asked about is LinguisticAnalysis, because the model this gate compares
        // BuiltWithModel against is the LinguisticAnalysis one (ActiveLinguisticModelFor, just below). Asking
        // about any other task would gate these profiles on a tier that never built them - and a per-task flip
        // on, say, Proofread would then invalidate every chapter profile for nothing.
        var effectiveTier = tier
            ?? await BookAiTierResolver.ResolveAsync(_db, bookId, AiTaskType.LinguisticAnalysis, _logger, ct);

        // The active LinguisticAnalysis model the deviations would be compared against (config-resolved,
        // same resolution AiRouter uses, at THIS BOOK's tier). A profile built under a DIFFERENT model must
        // not be served as a baseline: comparing metrics across models is apples-to-oranges, so a model
        // mismatch is treated exactly like timestamp staleness (rebuild, or null when a rebuild is
        // impossible). A tier change therefore invalidates this book's profiles through the EXISTING gate -
        // no separate invalidation pass exists or is needed.
        var activeModel = ActiveLinguisticModelFor(effectiveTier);

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
            var built = await ComputeChapterLinguisticMetricsAsync(chapterText, lang, effectiveTier, ct);
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
    /// <param name="tier">
    /// be-c02: the book's already-resolved tier, or null to resolve it from the database (the default,
    /// and the pre-be-c02 behaviour). A caller that has JUST built the profiles it is about to average
    /// must pass the tier it built them under - see the note on the resolve below.
    /// </param>
    public async Task<ChapterStyleProfile?> BuildBookStyleAverageProfileAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default,
        AiTier? tier = null)
    {
        // Normalize to the canonical cache key (e.g. "en-US" → "en") so the rows aggregated here are the
        // SAME ones the builder and status endpoints key, keeping inline and background paths in one slot.
        var lang = BaselineLanguageResolver.Normalize(language);

        try
        {
            // The active LinguisticAnalysis model the deviations are compared against. A profile built under
            // a DIFFERENT model is excluded (cross-model metrics are apples-to-oranges); this is the same
            // active-model resolution the (re)build gate uses, at the SAME book tier (p3-3) - resolving it
            // untiered here would exclude every profile a thinking-tier book just built.
            // be-c02: and for the same reason it must prefer the CALLER's tier when one is supplied. This
            // aggregator EXCLUDES rather than rebuilds, so re-reading Book.AiTier here after a mid-build
            // flip drops every row that build just wrote and returns null - no exception, no log, and the
            // caller's baseline is simply never persisted. Only a caller with no tier of its own (the
            // inline Chapter-scope read) falls through to the database read.
            // tier-ux-rework c1: LinguisticAnalysis for the same reason as the builder above - this
            // aggregator's include/exclude test is the LinguisticAnalysis active model.
            var effectiveTier = tier
                ?? await BookAiTierResolver.ResolveAsync(_db, bookId, AiTaskType.LinguisticAnalysis, _logger, ct);
            var activeModel = ActiveLinguisticModelFor(effectiveTier);

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
    /// <para>
    /// TIER (p3-3), and this is HALF OF AN ATOMIC PAIRING - do not change one half without the other. The
    /// request is stamped with the book's <see cref="AiTier"/> so the rebuild runs on the model the book is
    /// actually on; the model it comes back with is stamped into
    /// <c>ChapterStyleProfile.BuiltWithModel</c>, which
    /// <see cref="ChapterStyleProfileFreshness.IsFresh"/> compares against
    /// <see cref="ActiveLinguisticModelFor"/>. Stamping the request WITHOUT making the gate tier-aware would
    /// make every thinking-tier book's profiles permanently stale (the cloud stamp never equals the local
    /// active model) - one extra LLM call per chapter per analysis, forever. Making the gate tier-aware
    /// WITHOUT stamping the request is the same failure mirrored. p3-2 deliberately deferred this stamp to
    /// p3-3 for exactly that reason.
    /// </para>
    /// </summary>
    private async Task<(LinguisticAnalysisResult Metrics, string? Model)?> ComputeChapterLinguisticMetricsAsync(
        string text,
        string language,
        AiTier tier,
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
            JsonMode = true,
            // p3-3: the book's tier, so this baseline build routes to the SAME model the user-facing
            // LinguisticAnalysis run routes to. See the atomic-pairing note on this method.
            Tier = tier
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
            // active model AT THIS BOOK'S TIER if a provider left it blank, so BuiltWithModel is never null
            // when we DID build - and so the fallback stamp still satisfies the tier-aware freshness gate.
            var model = string.IsNullOrWhiteSpace(response.Model) ? ActiveLinguisticModelFor(tier) : response.Model;
            return (metrics, model);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The resolved active LinguisticAnalysis model id from config for a given BOOK TIER (same resolution
    /// AiRouter uses, via the shared <see cref="LinguisticModelResolver"/>). Used as the cross-model
    /// staleness comparison target and as a fallback stamp value. May be null only when DefaultModel itself
    /// is null/empty.
    /// <para>
    /// p3-3 made this TIER-AWARE, and that is what performs the tier's cache invalidation - there is no
    /// separate invalidation pass. Because the shared gate
    /// (<see cref="ChapterStyleProfileFreshness.IsFresh"/>) is a per-row
    /// <c>BuiltWithModel == activeModel</c> comparison, moving a book to the thinking tier changes the
    /// right-hand side for THAT BOOK ONLY: its profiles and its <c>BookStyleBaseline</c> read STALE and
    /// rebuild once, every other book's rows are untouched, and the FE's existing
    /// <c>builtWithDifferentModel</c> Refresh affordance renders it. On <see cref="AiTier.Fast"/> this
    /// resolves byte-identically to the pre-tier behaviour.
    /// </para>
    /// <para>
    /// PAIRED WITH the request stamp in <see cref="ComputeChapterLinguisticMetricsAsync"/> - see the atomic
    /// pairing note there; the two must never move separately.
    /// </para>
    /// </summary>
    private string? ActiveLinguisticModelFor(AiTier tier) =>
        LinguisticModelResolver.ResolveModelForTask(_aiOptions.Value, AiTaskType.LinguisticAnalysis, tier);

    /// <summary>
    /// Freshness gate for a cached profile against the current chapter + active model. Delegates to the
    /// shared <see cref="ChapterStyleProfileFreshness.IsFresh"/> so this gate and
    /// <see cref="StyleBaselineService.GetStatusAsync"/> use ONE staleness definition (timestamp AND model).
    /// </summary>
    private static bool IsFresh(ChapterStyleProfile profile, Chapter chapter, string? activeModel) =>
        ChapterStyleProfileFreshness.IsFresh(profile.UpdatedAt, profile.BuiltWithModel, chapter.UpdatedAt, activeModel);

    /// <summary>
    /// Loads the structured narrative briefs (wb1-c02 read path) for the analysis context: the single
    /// <see cref="ChapterBrief"/> for the chapter under analysis (Chapter/Scene scope) and the L2
    /// <see cref="BookBrief"/> rollup. Only the briefs the analysis type actually consumes are fetched
    /// (PromptFactory.GetRelevantFields decides what is rendered, so loading the rest is wasted work).
    /// Read-only projections from already-built data — no LLM call — and degrade to null on any failure or
    /// when the book summary has not been built yet.
    /// </summary>
    private async Task<(ChapterBrief? Chapter, BookBrief? Book)> LoadNarrativeBriefsAsync(
        Guid bookId,
        Guid? chapterId,
        AnalysisType analysisType,
        string language,
        CancellationToken ct)
    {
        // Which structured briefs are relevant for this analysis type. Mirrors PromptFactory.GetRelevantFields
        // so we never load a brief the prompt would not render.
        var wantsChapterBrief = analysisType is AnalysisType.LiteraryAnalysis or AnalysisType.Summarization;
        var wantsBookBrief = analysisType is AnalysisType.LiteraryAnalysis
            or AnalysisType.QA or AnalysisType.StoryAnalysis;

        if (!wantsChapterBrief && !wantsBookBrief)
            return (null, null);

        try
        {
            // Compose the L1 briefs once; both the per-chapter pick and the L2 rollup project from them.
            IReadOnlyList<ChapterBrief> chapterBriefs = await _bookSummary.ComposeChapterBriefsAsync(bookId, language, ct);

            ChapterBrief? chapterBrief = null;
            if (wantsChapterBrief && chapterId.HasValue)
            {
                var target = await _db.Chapters
                    .AsNoTracking()
                    .Where(c => c.Id == chapterId.Value)
                    .Select(c => new { c.Order })
                    .FirstOrDefaultAsync(ct);
                if (target != null)
                    chapterBrief = chapterBriefs.FirstOrDefault(b => b.Order == target.Order);
            }

            BookBrief? bookBrief = null;
            if (wantsBookBrief)
                bookBrief = await _bookSummary.ComposeBookBriefAsync(bookId, chapterBriefs, ct);

            return (chapterBrief, bookBrief);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Narrative briefs are supporting context; degrade gracefully so analysis still runs without them.
            _logger.LogWarning(ex, "Failed to load narrative briefs for book {BookId}", bookId);
            return (null, null);
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

    /// <summary>
    /// Resolve the book's character register for analysis context.
    ///
    /// <para>FAIL-SAFE CONTRACT (unchanged, and the reason for the shape of this method): any
    /// non-cancellation failure degrades to a null register so the analysis still runs WITHOUT
    /// character info, and <see cref="OperationCanceledException"/> still propagates so cooperative
    /// cancellation works. Nothing added for provenance may turn a register problem into a failed
    /// analysis.</para>
    ///
    /// <para>OBSERVABILITY: that swallowing catch used to be silent, which blinds every outer logger —
    /// a broken merge or an unreadable register would ship as "this book just has no characters",
    /// forever. Every degradation path here now logs with the bookId. Register CONTENT is never
    /// logged: character names are the user's manuscript.</para>
    ///
    /// <para>COVERAGE (automatic-coverage plan, be-c01 / d1 §2). This method used to return early on
    /// ANY non-empty stored register, so the extraction pre-pass fired exactly ONCE in a book's life,
    /// against whichever unit happened to be analysed first — a character introduced in chapter 33
    /// never entered the register at all, and what the register held depended on which chapter the
    /// author opened first. That gate is replaced by <see cref="CharacterRegister.ScannedChapters"/>:
    /// a CHAPTER-scoped (or scene-scoped, which is chapter-keyed) analysis of a chapter that has not
    /// contributed — or whose <c>Chapter.UpdatedAt</c> has moved since it did — scans THAT chapter and
    /// merges the result in. BOOK scope (<paramref name="chapterId"/> null) is deliberately outside
    /// the ledger and keeps the old behaviour exactly.</para>
    ///
    /// <para>BOUND: at most ONE extraction call per request, no matter how many chapters are
    /// unscanned. Coverage grows one chapter at a time, at the pace the author runs analyses
    /// <c>PromptFactory.RendersCharacterRegister</c> admits (Proofread, LiteraryAnalysis, QA,
    /// Synopsis) — this method is never reached for any other analysis type; reporting that gap
    /// honestly is be-c03's job, not this method's.</para>
    ///
    /// <para>This is what makes <see cref="CharacterRegisterMerge"/> a LIVE path (it was correct but
    /// dormant): every guarantee it carries — author-confirmed fields win, suppressed entries are
    /// never resurrected, unmatched local entries are never deleted, and a no-op merge does not bump
    /// <see cref="CharacterRegister.UpdatedAt"/> — now has to hold in production.</para>
    /// </summary>
    /// <param name="chapterId">
    /// The ledger key. Non-null for Chapter and Scene scope; null at Book scope, where the assembled,
    /// budget-capped multi-chapter text is not something a per-chapter ledger entry could honestly
    /// describe as "chapter N scanned".
    /// </param>
    private async Task<CharacterRegister?> LoadCharacterRegisterAsync(
        Guid bookId,
        string fullText,
        Guid? chapterId,
        CancellationToken ct)
    {
        try
        {
            // 1. Prefer explicit register from BookBible when available
            var bible = await _db.BookBibles
                .FirstOrDefaultAsync(b => b.BookId == bookId, ct);

            // Captured as a STRING, not as the tracked property, so the re-read comparison below is
            // against what this request actually saw and cannot be silently updated underneath us.
            var jsonAtFirstRead = bible?.CharacterRegisterJson;

            CharacterRegister? stored = null;
            if (!string.IsNullOrWhiteSpace(bible?.CharacterRegisterJson))
            {
                if (!CharacterRegisterService.TryDeserialize(bible.CharacterRegisterJson, out stored, out var parseFault)
                    && parseFault != null)
                {
                    // An unreadable register would otherwise fall through to re-extraction and
                    // OVERWRITE the column - taking any author edits it held with it. Refuse to
                    // clobber what we could not read, and say why.
                    _logger.LogError(
                        parseFault,
                        "Character register for book {BookId} is unreadable ({JsonLength} chars). Skipping re-extraction so the stored value is not overwritten; this analysis runs without character context.",
                        bookId,
                        bible.CharacterRegisterJson.Length);
                    return null;
                }
            }

            // 2. BOOK SCOPE keeps the ONE-SHOT gate, unchanged (d1 §2). `fullText` here is the
            // assembled, budget-capped multi-chapter context from BookContextAssembler, not any one
            // chapter's prose, so no ledger entry could truthfully be written for it. A non-empty
            // register is served as-is; an empty one still gets today's single bootstrap extraction
            // below. Book scope therefore neither advances chapter coverage nor regresses it.
            if (chapterId is null && stored is { Characters.Count: > 0 })
                return CharacterRegisterMerge.ForAnalysis(stored);

            // 3. CHAPTER/SCENE SCOPE: consult the ledger, and scan the CHAPTER (never the triggering
            // scene alone — the ledger is keyed by ChapterId, so "scanned" has to mean the chapter's
            // own content was read, or a five-scene chapter would read as covered after one scene).
            var extractionSource = fullText;
            var chapterStamp = default(DateTimeOffset);
            if (chapterId.HasValue)
            {
                var chapter = await _db.Chapters
                    .AsNoTracking()
                    .Where(c => c.Id == chapterId.Value)
                    .Select(c => new { c.ContentText, c.UpdatedAt })
                    .FirstOrDefaultAsync(ct);

                if (chapter == null)
                {
                    // Deleted between target resolution and here. Nothing to scan and nothing the
                    // ledger could key on, so degrade to whatever is already stored.
                    _logger.LogWarning(
                        "Character register coverage skipped: chapter {ChapterId} of book {BookId} no longer exists; serving the stored register.",
                        chapterId,
                        bookId);
                    return CharacterRegisterMerge.ForAnalysis(stored);
                }

                // COVERED-AND-FRESH is checked regardless of how many characters the register holds.
                // A chapter that genuinely contains no characters (a foreword, a title page) leaves
                // the register empty but IS scanned, and must not be re-extracted on every single
                // analysis for the rest of the book's life.
                //
                // THE PREDICATE IS SHARED, NOT RESTATED (be-c03). The author-facing coverage report
                // (CharacterRegisterService.GetAsync) counts a chapter as covered by calling exactly
                // this method over exactly this list. Inlining `entry.SourceStamp == chapter.UpdatedAt`
                // here would leave two definitions of "already scanned" free to drift, and the visible
                // symptom of drift is the worst possible one: a book reported complete that the scan
                // path keeps re-scanning, or reported incomplete while nothing will ever scan again.
                var ledgerEntry = CharacterRegisterCoverage.FindEntry(stored, chapterId.Value);
                if (CharacterRegisterCoverage.IsCoveredAndFresh(ledgerEntry, chapter.UpdatedAt))
                    return CharacterRegisterMerge.ForAnalysis(stored);

                chapterStamp = chapter.UpdatedAt;
                extractionSource = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
            }

            // 4. Cheap LLM pre-pass over the unit being scanned (still capped at
            // CharacterPrepassMaxWords), merged and persisted back to BookBible so this chapter does
            // not pay for the call again until it is edited.
            var book = await _db.Books
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == bookId, ct);
            if (book == null)
            {
                _logger.LogWarning("Character register skipped: book {BookId} not found.", bookId);
                return null;
            }

            var language = string.IsNullOrWhiteSpace(book.Language) ? "he" : book.Language;
            var extracted = await ExtractCharacterRegisterAsync(extractionSource, language, ct);

            // TWO DIFFERENT EXITS THAT LOOK LIKE ONE (final-r01). `extracted` is NULL only when there
            // was no answer to read (the router threw, the response was blank or carried no parsable
            // JSON); it is an EMPTY register when the model answered and named nobody.
            //
            //  - NULL, at ANY scope: write NOTHING. A chapter-keyed scan must NOT record a ledger entry
            //    for a call that failed, or one wedged Ollama run marks every chapter analysed during
            //    the outage permanently covered while having read none of them - and neither the
            //    register (it looks like a chapter with no characters) nor the coverage report (it says
            //    covered) can tell anyone. Nothing re-scans it until the author edits that chapter.
            //  - EMPTY at BOOK scope: today's bootstrap behaviour, unchanged - no ledger exists to
            //    write and there is nothing to merge.
            //  - EMPTY at CHAPTER scope: fall through and record the ledger entry, so a chapter that
            //    genuinely has no characters in it (a foreword, a title page) does not burn one LLM
            //    call every time it is analysed, forever.
            if (extracted is null || (chapterId is null && extracted.Characters.Count == 0))
            {
                if (extracted is null)
                {
                    // TWO DIFFERENT REASONS `extracted` CAN BE NULL, and only one of them is transient
                    // (P3-7). `ExtractCharacterRegisterAsync` returns null with NO model call when its
                    // truncated input is blank (`extractionSource` was already empty/whitespace here -
                    // an empty or watermark-only chapter). That is exactly `IsScannable` returning false,
                    // the same UNSCANNABLE state the coverage report already classifies correctly, and it
                    // can never be resolved by retrying: every future scene analysis of this chapter hits
                    // the same empty source and logs the same thing again. The genuine transient case -
                    // the model was asked and gave no parsable answer - has a non-empty extractionSource
                    // and stays a WARNING, because THAT retry can succeed once the model behaves.
                    //
                    // final-r01: the blank-source branch is NOT chapter-only. At BOOK scope
                    // `extractionSource` is the assembled multi-chapter text, which is blank when the
                    // whole book has nothing readable in it. The level and the "permanent, no model was
                    // called" claim are right there too, but there is no chapter and the coverage report
                    // made no classification about one, so the message names the SOURCE rather than
                    // asserting a per-chapter verdict, and the UNSCANNABLE cross-reference is scoped to
                    // the chapter case where it is actually true. `{ChapterId}` is null at book scope.
                    if (string.IsNullOrWhiteSpace(extractionSource))
                        _logger.LogInformation(
                            "Character register pre-pass skipped for book {BookId} (chapter {ChapterId}, null at book scope): the text handed to the pre-pass has no scannable content (empty or watermark-only), so no model call was made. This is a permanent state, not a transient failure - nothing is recorded as scanned and retrying will not resolve it. At chapter scope this is exactly the UNSCANNABLE bucket the coverage report already reports.",
                            bookId,
                            chapterId);
                    else
                        _logger.LogWarning(
                            "Character register pre-pass returned NO USABLE ANSWER for book {BookId} (chapter {ChapterId}); the analysis runs without character context and the chapter is deliberately NOT recorded as scanned, so it is retried on its next analysis.",
                            bookId,
                            chapterId);
                }
                else
                    _logger.LogInformation(
                        "Character register pre-pass produced no characters for book {BookId}; analysis runs without character context.",
                        bookId);

                // Deliberately returns the PRE-CALL snapshot without re-reading: this branch writes
                // NOTHING, so a concurrent author edit cannot be lost here. The worst case is that
                // this one analysis runs against a register a few seconds out of date, which is a
                // stale read, not data loss. The re-read below guards the branch that WRITES.
                return CharacterRegisterMerge.ForAnalysis(stored);
            }

            // ── RE-READ THE REGISTER BEFORE MERGING (c01) ──────────────────────────────────────
            // `stored` above was read BEFORE ExtractCharacterRegisterAsync, a multi-second local-model
            // call. An author PATCH (CharacterRegisterService.ApplyEditsAsync, a different request on
            // its own scoped DbContext) can land inside that window. Merging the PRE-CALL snapshot and
            // then writing the whole column back erases that edit with no error and no log line:
            // BookBible has a unique index on BookId and NO concurrency token / RowVersion, so EF
            // issues a plain UPDATE and nothing detects the loss. That window is not exotic, and it
            // stopped being merely defensive when coverage shipped (be-c01): the register used to be
            // written about ONCE PER BOOK and is now written once per chapter scanned, so this
            // re-read is on the hot path rather than the edge of one. Do not weaken it.
            //
            // It is ALSO what makes two DIFFERENT chapters scanning concurrently safe for free:
            // whichever request writes second re-reads immediately before merging, so it sees the
            // first one's characters AND its ledger entry and merges on top of them instead of
            // overwriting the column with its own pre-call snapshot.
            //
            // ReloadAsync, not a re-query, for the row already tracked: a TRACKING re-query resolves
            // to the instance already in the change tracker and leaves its stale property values in
            // place, so it would hand back the very snapshot we are trying to replace; an AsNoTracking
            // re-query would read current values onto an entity we then could not write through.
            // Reload refreshes the tracked instance from the store in place, so the merge input and
            // the write target are the same current row.
            if (bible != null)
            {
                await _db.Entry(bible).ReloadAsync(ct);

                // A concurrent DELETE leaves the entity Detached with no row behind it. Treat that as
                // "no row" so the create branch below runs instead of writing through a dead entity.
                if (_db.Entry(bible).State == EntityState.Detached) bible = null;
            }
            else
            {
                // Nothing was tracked, so there is nothing to reload: query. If a concurrent request
                // CREATED the row while the pre-pass ran we ADOPT it here, which is also what stops
                // the create branch below from inserting a SECOND row and turning the unique index on
                // BookId into an unhandled DbUpdateException (a 500 on the whole analysis).
                bible = await _db.BookBibles.FirstOrDefaultAsync(b => b.BookId == bookId, ct);
            }

            var jsonAtReRead = bible?.CharacterRegisterJson;
            if (!string.Equals(jsonAtReRead, jsonAtFirstRead, StringComparison.Ordinal))
            {
                // A clobber that was just prevented. A silently-rescued race nobody can see is the
                // same blindness one layer up, and this codebase has already paid for that. Book id
                // and payload LENGTHS only: register content is the user's manuscript and is never
                // logged.
                _logger.LogWarning(
                    "Character register for book {BookId} CHANGED while the extraction pre-pass ran ({BeforeLength} -> {AfterLength} chars). Merging against the CURRENT stored value; the pre-call snapshot would have overwritten a concurrent author edit.",
                    bookId,
                    jsonAtFirstRead?.Length ?? 0,
                    jsonAtReRead?.Length ?? 0);
            }

            stored = null;
            if (!string.IsNullOrWhiteSpace(jsonAtReRead)
                && !CharacterRegisterService.TryDeserialize(jsonAtReRead, out stored, out var reReadFault)
                && reReadFault != null)
            {
                // The same refusal the gate above makes, applied to whichever read found it
                // unreadable: never overwrite a register we could not read, because whatever author
                // edits it held would go with it.
                _logger.LogError(
                    reReadFault,
                    "Character register for book {BookId} became unreadable while the extraction pre-pass ran ({JsonLength} chars). Skipping the merge write so the stored value is not overwritten; this analysis runs without character context.",
                    bookId,
                    jsonAtReRead.Length);
                return null;
            }

            // MERGE, never overwrite (d1 §3). This is the ONLY production re-extraction write, and it
            // is the reason provenance exists: `stored` may hold author-confirmed genders, hand-added
            // characters and permanently-suppressed entries, and a straight Serialize(extracted) would
            // erase all three silently.
            //
            // REACHABILITY - re-measured 2026-08-06 for be-c01, and it CHANGED. This merge used to be
            // correct-but-DORMANT: the old one-shot gate returned early on any non-empty stored
            // register, so the only way here was a register that was absent or held zero entries. It
            // is now the ordinary path. Every Chapter- or Scene-scoped analysis of a chapter that has
            // not contributed to the ledger (or whose text has changed since it did) arrives here
            // with a fully populated `stored`, which is exactly the input the merge was written for
            // and had never actually been given in production. Its guarantees - confirmed fields win,
            // suppressed entries are never resurrected, unmatched locals are never deleted, a no-op
            // does not bump the stamp - are load-bearing from here on, not insurance.
            //
            // `stored` is the RE-READ value (c01), never the pre-call snapshot, so a concurrent
            // author edit (or a sibling chapter's scan) is merged over rather than overwritten.
            var now = DateTimeOffset.UtcNow;
            var merged = CharacterRegisterMerge.Merge(stored, extracted, now);

            // THE LEDGER IS GRAFTED ON AFTER THE MERGE, and this order is not incidental.
            // CharacterRegisterMerge decides `changed`/UpdatedAt from Characters ONLY; it does not
            // know ScannedChapters exists and must not be taught to. A `with` expression cannot move
            // UpdatedAt, so a chapter joining the ledger while contributing no new or changed
            // character leaves the stamp alone - which is required, or improving coverage would mark
            // every prior AnalysisResult on the book stale without one character fact having changed.
            //
            // Merge builds a FRESH record, so the existing ledger has to be carried across
            // explicitly on BOTH branches; letting the book-scope bootstrap fall through with the
            // merge's own empty list would silently erase coverage a chapter scan had already earned.
            var ledger = stored?.ScannedChapters ?? Array.Empty<ScannedChapterEntry>();
            if (chapterId.HasValue)
            {
                // REPLACE this chapter's entry rather than append, so the ledger stays one line per
                // chapter however many times the chapter is re-scanned.
                ledger = ledger
                    .Where(e => e.ChapterId != chapterId.Value)
                    .Append(new ScannedChapterEntry
                    {
                        ChapterId = chapterId.Value,
                        ScannedAt = now,
                        SourceStamp = chapterStamp
                    })
                    .ToList();
            }

            var final = merged with { ScannedChapters = ledger };
            var json = CharacterRegisterService.Serialize(final);

            if (bible == null)
            {
                bible = new BookBible
                {
                    BookId = bookId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CharacterRegisterJson = json
                };
                _db.BookBibles.Add(bible);
            }
            else
            {
                bible.CharacterRegisterJson = json;
                bible.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(ct);

            // `extracted` is non-null by the guard above, but MAY hold zero characters: a chapter-keyed
            // scan reaches this line on an empty answer too, because the ledger entry still has to be
            // recorded. `{ExtractedCount} = 0` here is that case, and it is not a failure.
            _logger.LogInformation(
                "Character register scanned chapter {ChapterId} of book {BookId}: {ExtractedCount} extracted, {LocalCount} already stored, {MergedCount} after merge ({SuppressedCount} suppressed), {ScannedChapterCount} chapters in the ledger.",
                chapterId,
                bookId,
                extracted?.Characters.Count ?? 0,
                stored?.Characters.Count ?? 0,
                merged.Characters.Count,
                merged.Characters.Count(CharacterRegisterMerge.IsSuppressed),
                final.ScannedChapters.Count);

            return CharacterRegisterMerge.ForAnalysis(final);
        }
        catch (OperationCanceledException)
        {
            // Preserve cooperative cancellation so the outer analysis can stop immediately.
            throw;
        }
        catch (Exception ex)
        {
            // Any non-cancellation failure should degrade gracefully – proofread still runs without
            // character info. LOG IT: this catch is the last one on the path, so a fault swallowed
            // here is invisible to every outer handler.
            _logger.LogWarning(
                ex,
                "Character register could not be loaded for book {BookId}; analysis continues without character context.",
                bookId);
            return null;
        }
    }

    // The register-as-an-analysis-sees-it projection (NORMALIZE, then drop author-suppressed entries) now
    // lives on CharacterRegisterMerge.ForAnalysis. It was PROMOTED there by the automatic-coverage plan's
    // d1 §3 so BookReviewService can share ONE projection instead of re-deriving the suppression rule for
    // the whole-book review. See that method for the full argument: why it normalizes first, why it writes
    // nothing and never moves UpdatedAt, and why it is not applied inside PromptFactory.FormatCharacters.

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

    /// <summary>
    /// The character pre-pass over one unit's text.
    ///
    /// <para>NULL MEANS FAILED, NOT "FOUND NOTHING", and the distinction is load-bearing since be-c01
    /// (final-r01). A chapter-keyed scan records a ledger entry on ANY answer it gets, so that a chapter
    /// which genuinely has no characters in it (a foreword, a title page) is not re-extracted on every
    /// analysis for the rest of the book's life. If a FAILED call were indistinguishable from that, one
    /// wedged Ollama run would mark every chapter analysed during the outage permanently covered, having
    /// read none of them - invisible in the register (it looks like a chapter with no characters) and
    /// invisible in the coverage report (it says covered), with no way back short of an author edit.</para>
    ///
    /// <para>So: an EMPTY register is returned when the model answered and named nobody; NULL is returned
    /// only when there was no answer to read - no text to send, the router threw, the response was blank,
    /// or it carried no parsable JSON array.</para>
    /// </summary>
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
        catch (Exception ex)
        {
            // LOG IT. This catch is the reason `null` exists, and since be-c01 `null` is what stops a
            // chapter being recorded as scanned - so a fault swallowed silently here is a coverage
            // decision nobody can see the cause of. The caller logs that the scan was skipped; this is
            // the only place that knows WHY. No prose is logged, only the exception.
            _logger.LogWarning(ex, "Character extraction pre-pass call failed; no register was extracted.");
            return null;
        }

        var raw = response.Content;
        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning("Character extraction pre-pass returned an EMPTY response; no register was extracted.");
            return null;
        }

        var json = ExtractJsonArray(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogWarning(
                "Character extraction pre-pass response carried no JSON array ({Length} chars); no register was extracted.",
                raw.Length);
            return null;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<CharacterRegisterEntry>>(json, JsonOpts);
            if (entries is null)
                return null;

            // An EMPTY list is an ANSWER ("nobody appears in this unit"), not a failure - see the null
            // contract on this method. Collapsing it to null here is what would let a wedged model be
            // recorded as a clean scan.
            return new CharacterRegister
            {
                Characters = entries
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Character extraction pre-pass returned unparsable JSON; no register was extracted.");
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
        AnalysisType analysisType,
        CancellationToken ct)
    {
        var hasChapters = await _db.Chapters.AnyAsync(c => c.BookId == bookId, ct);
        if (!hasChapters)
            throw new InvalidOperationException("Book has no chapters to analyze.");

        // Budget-aware whole-book context: the shared BookContextAssembler caps assembly at a token budget
        // derived from the active model's NumCtx so a large book can no longer silently overflow the model
        // context (the previous unguarded concat of every chapter's full text). It prefers the dense
        // structured BookBrief + ChapterBriefs and degrades to a budget-guarded flat-summary/raw-text
        // concat when no briefs are built yet. Anything dropped is logged inside the assembler (no silent
        // truncation).
        var language = await ResolveLanguageAsync(bookId, ct);
        // Budget the assembled context to the window of the task this analysis actually routes to (Bug 3):
        // a book-scope LinguisticAnalysis/GenericChat consumer can have a smaller num_ctx than Summarization,
        // and sizing against Summarization alone would let the context overflow that consumer's window.
        var consumingTask = AnalysisTaskMapping.ToAiTaskType(analysisType);
        var assembly = await _bookContextAssembler.AssembleAsync(
            bookId, language, new[] { consumingTask }, ct);

        if (string.IsNullOrWhiteSpace(assembly.Text))
            throw new InvalidOperationException("No book text to analyze. Save the chapters first so the analysis has content.");

        return (assembly.Text, bookId, null, null);
    }

}

