using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Builds the STRUCTURED per-chapter brief that lands in <see cref="ChunkSummary.StructuredJson"/>
/// (schema <see cref="StructuredChunkSummaryData"/>: plot events, character states, thematic markers,
/// tone notes, open threads). The flat <see cref="ChunkSummary.SummaryText"/> is left to
/// <see cref="BookIntelligenceService"/> and kept for back-compat; this service only fills the
/// structured surface.
///
/// REUSE: this deliberately mirrors the shipped read-or-build pattern of
/// <see cref="AnalysisContextService.LoadOrBuildChapterStyleProfileAsync"/> /
/// <see cref="StyleBaselineService"/> rather than copying
/// <see cref="BookIntelligenceService.SummarizeChaptersAsync"/>'s full-rebuild idiom:
///   • (ChapterId, Language) cache key (the unique index on ChunkSummary), per-chapter idempotent
///     read-or-build;
///   • <see cref="ChunkSummary.BuiltWithModel"/> stamping + the SHARED freshness predicate
///     <see cref="ChapterStyleProfileFreshness.IsFresh"/>, so a changed chapter (timestamp) OR a changed
///     Summarization model (cross-model) invalidates the cached brief;
///   • graceful per-chapter null on empty/parse-fail — a single bad chapter never aborts the book job.
/// </summary>
public class ChapterBriefService
{
    private readonly AppDbContext _db;
    private readonly IAiRouter _router;
    private readonly PromptFactory _promptFactory;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChapterBriefService> _logger;

    // Deserialization of the persisted StructuredJson lives in the shared StructuredChunkSummaryParser
    // (case-insensitive + camelCase), so the freshness gate, status count, and composition all parse
    // identically. This service only serializes the brief it builds.

    // Serialize the brief to StructuredJson with the SAME camelCase policy so the round-trip
    // (build → persist → read → deserialize) is stable and ChapterBrief assembly reads the same shape.
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ChapterBriefService(
        AppDbContext db,
        IAiRouter router,
        PromptFactory promptFactory,
        IOptions<AiOptions> aiOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<ChapterBriefService> logger)
    {
        _db = db;
        _router = router;
        _promptFactory = promptFactory;
        _aiOptions = aiOptions;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// The resolved active Summarization model id from config (same resolution AiRouter uses for the
    /// Summarization task, via the shared <see cref="LinguisticModelResolver.ResolveModelForTask"/>). Used
    /// as the cross-model staleness comparison target and as a fallback stamp value.
    /// </summary>
    internal string? ActiveSummarizationModel =>
        LinguisticModelResolver.ResolveModelForTask(_aiOptions.Value, AiTaskType.Summarization);

    /// <summary>
    /// Loads the cached STRUCTURED brief for (chapterId, language), or builds and caches one if absent or
    /// stale. Idempotent read-or-build mirroring
    /// <see cref="AnalysisContextService.LoadOrBuildChapterStyleProfileAsync"/>:
    ///   1. read the (ChapterId, Language) ChunkSummary row (may be stale);
    ///   2. load the chapter (current text + last-edit timestamp);
    ///   3. freshness gate (timestamp AND model) via the SHARED predicate — a brief is fresh only when it
    ///      was built at/after the chapter's last edit AND under the active Summarization model AND it
    ///      actually carries StructuredJson; otherwise rebuild;
    ///   4. on miss/stale, call the structured-brief prompt, parse, persist StructuredJson + BuiltWithModel.
    /// Degrades GRACEFULLY to null (never throws to the caller for a per-chapter failure) when the chapter
    /// has no analysable text, the LLM call fails, or the response does not parse.
    /// </summary>
    public async Task<StructuredChunkSummaryData?> LoadOrBuildChapterBriefAsync(
        Guid bookId,
        Guid chapterId,
        string language,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);
        var activeModel = ActiveSummarizationModel;

        try
        {
            // 1. Cache read: existing summary row for this chapter (keyed by the unique BookId+ChapterId
            // index; Language is part of the brief's identity, so a row in a different language is rebuilt).
            var existing = await _db.ChunkSummaries
                .FirstOrDefaultAsync(cs => cs.ChapterId == chapterId, ct);

            // 2. Load the chapter (current text + last-edit timestamp). Reused for staleness AND the build.
            var chapter = await _db.Chapters
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == chapterId, ct);
            if (chapter == null)
                // No chapter → return whatever structured brief is cached (possibly null).
                return Parse(existing?.StructuredJson);

            var chapterText = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
            if (string.IsNullOrWhiteSpace(chapterText))
            {
                // No analysable text now (cleared chapter). We cannot rebuild from empty text, and a brief
                // built from the PREVIOUS content is outdated once the chapter changed. Serve the cached
                // brief ONLY when it is fresh (timestamp AND model AND same language); otherwise null.
                if (existing != null && IsFresh(existing, chapter, lang, activeModel))
                    return Parse(existing.StructuredJson);
                return null;
            }

            // 3. Freshness gate. Fresh only when the cached row already HAS a structured brief, was built
            //    at/after the chapter's last edit, under the active model, in the same language.
            if (existing != null && IsFresh(existing, chapter, lang, activeModel))
                return Parse(existing.StructuredJson);

            // 4. Cache miss OR stale: (re)compute the structured brief from the CURRENT text.
            var built = await ComputeChapterBriefAsync(chapterText, lang, ct);
            if (built == null)
                // Build failed (empty/unparseable). Return null rather than a stale brief.
                return null;

            var (brief, builtModel) = built.Value;
            var structuredJson = JsonSerializer.Serialize(brief, SerializeOpts);

            if (existing != null)
            {
                // Refresh the existing row in place. wb1-r02: StructuredBuiltAt is the build timestamp the
                // structured freshness gate reads (CreatedAt is the SHARED row stamp the flat re-summary path
                // also bumps; keying the structured gate on it would let a flat re-summary mask a stale
                // structured brief). Stamp StructuredBuiltAt to NOW so this freshly built brief reads fresh.
                // We do NOT touch CreatedAt here: this row may already carry a flat SummaryText whose
                // CreatedAt is its own (flat) freshness stamp, owned by BookIntelligenceService.
                //
                // The row's Language is the SINGLE identity for BOTH surfaces (StructuredJson AND the flat
                // SummaryText). If this structured (re)build switches the row to a DIFFERENT language than it
                // currently holds, the existing flat SummaryText is in the OLD language; once Language flips to
                // `lang` it would masquerade as the new locale's prose. BookContextAssembler selects flat
                // fallbacks by Language only, so it would assemble that mismatched-language prose into the
                // requested book context. Clear the now-stale flat summary so it cannot leak; the flat path
                // (SummarizeChaptersAsync) rebuilds it for the new locale when needed.
                if (!string.Equals(existing.Language, lang, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(existing.SummaryText))
                {
                    existing.SummaryText = string.Empty;
                    // The flat surface's user-edit clobber guard (SummaryUserEdited/At) protected the prose we
                    // just cleared as stale old-locale text; once the prose is gone the guard is meaningless
                    // and actively harmful if left set. Leaving SummaryUserEdited true makes the automatic
                    // re-summary (SummarizeChaptersAsync) SKIP this row to "preserve" an edit that is now an
                    // empty string, so the new-locale flat prose never regenerates; and it makes the re-derive
                    // endpoint return 409 ("no user-edited summary to seed from") on an empty SummaryText.
                    // Reset the guard so the flat path can rebuild for the new locale, mirroring the re-derive
                    // path's flip handling (RederiveChapterBriefFromUserSummaryAsync).
                    existing.SummaryUserEdited = false;
                    existing.SummaryUserEditedAt = null;
                }
                existing.StructuredJson = structuredJson;
                existing.BuiltWithModel = builtModel;
                existing.Language = lang;
                existing.StructuredBuiltAt = DateTimeOffset.UtcNow;
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex)
                {
                    // Detach so the now-Modified state is not retried by a later SaveChanges on this scoped
                    // DbContext; the freshly computed brief is still returned (just uncached this round).
                    _logger.LogWarning(ex, "Failed to refresh structured brief for chapter {ChapterId}", chapterId);
                    _db.Entry(existing).State = EntityState.Detached;
                }
                return brief;
            }

            var row = new ChunkSummary
            {
                BookId = bookId,
                ChapterId = chapterId,
                Language = lang,
                // Flat SummaryText stays empty here; BookIntelligenceService owns the natural-language
                // summary. This service only fills the structured surface (back-compat preserved).
                StructuredJson = structuredJson,
                BuiltWithModel = builtModel,
                // wb1-r02: stamp the STRUCTURED build time explicitly. The SaveChanges override only stamps
                // CreatedAt on Add (the shared/flat surface stamp); the structured freshness gate reads
                // StructuredBuiltAt, so it must be set here too.
                StructuredBuiltAt = DateTimeOffset.UtcNow
                // CreatedAt stamped by the SaveChanges override on Add.
            };

            try
            {
                _db.ChunkSummaries.Add(row);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two concurrent builds for the same chapter can both reach the insert before either commits,
                // violating the unique (BookId, ChapterId) index. Detach the failed insert and return the
                // brief we computed (a later read will pick up the winning row).
                _db.Entry(row).State = EntityState.Detached;
                return brief;
            }

            return brief;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any non-cancellation failure degrades gracefully → no structured brief for this chapter.
            _logger.LogWarning(ex, "Failed to load/build structured brief for chapter {ChapterId}", chapterId);
            return null;
        }
    }

    /// <summary>
    /// Builds (or refreshes) the structured brief for EVERY chapter of the book. Mirrors
    /// <see cref="StyleBaselineService.BuildBookStyleBaselineAsync"/>'s shape: enumerate chapters,
    /// (re)build only stale/missing ones via the idempotent per-chapter primitive with LIMITED parallelism,
    /// and NEVER abort the job for a single bad chapter (each per-chapter build degrades to null). Returns
    /// the number of chapters that ended with a usable structured brief.
    ///
    /// IDEMPOTENT: a chapter whose cached brief is fresh (timestamp AND active model) is skipped without an
    /// LLM call — exactly the incremental behaviour the style-baseline builder uses.
    /// </summary>
    public async Task<ChapterBriefBuildResult> BuildBookChapterBriefsAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (chapters.Count == 0)
            return new ChapterBriefBuildResult { TotalChapters = 0, BuiltChapters = 0, FailedChapters = 0 };

        // (Re)build with LIMITED parallelism. LoadOrBuildChapterBriefAsync is idempotent (fresh chapters
        // return cached without an LLM call) and degrades to null on failure, so one bad chapter never
        // aborts the job. Each build runs on its OWN DI scope (fresh DbContext + scoped service) because
        // EF Core's DbContext is not thread-safe and these run concurrently. Mirrors StyleBaselineService.
        var maxParallel = Math.Max(1, _aiOptions.Value.MaxParallelStyleBaselineChapters);
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var built = 0;

        async Task ProcessChapter(Guid chapterId)
        {
            await semaphore.WaitAsync(ct);
            try
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<ChapterBriefService>();
                    var brief = await service.LoadOrBuildChapterBriefAsync(bookId, chapterId, lang, ct);
                    if (brief != null)
                        Interlocked.Increment(ref built);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Defence against an unexpected throw escaping the per-chapter graceful-null contract;
                    // log + continue so the whole book job still completes.
                    _logger.LogWarning(ex,
                        "Structured brief: chapter {ChapterId} of book {BookId} threw during build; skipping",
                        chapterId, bookId);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = chapters.Select(ProcessChapter).ToList();
        await Task.WhenAll(tasks);

        ct.ThrowIfCancellationRequested();

        return new ChapterBriefBuildResult
        {
            TotalChapters = chapters.Count,
            BuiltChapters = built,
            FailedChapters = chapters.Count - built
        };
    }

    /// <summary>
    /// wb3-c04 (user-triggered RE-DERIVE): rebuilds the STRUCTURED brief for one chapter, SEEDED with the
    /// user's own edited flat <see cref="ChunkSummary.SummaryText"/> as the authoritative chapter
    /// understanding, so the whole-book review (which reads the structured brief, NOT the flat text) reflects
    /// the user's manual edit. UNLIKE <see cref="LoadOrBuildChapterBriefAsync"/> this is NOT freshness-gated:
    /// the seed (the user's edit) changed even when the chapter text did not, so the brief is ALWAYS rebuilt.
    ///
    /// DUAL-SURFACE: this writes ONLY the structured surface (StructuredJson + BuiltWithModel +
    /// StructuredBuiltAt) and the shared Language. It does NOT touch the flat SummaryText, its CreatedAt /
    /// SummaryUserEditedAt stamps, or the <see cref="ChunkSummary.SummaryUserEdited"/> flag — the user's edit
    /// remains authoritative and clobber-guarded. Returns the rebuilt brief, or null when the row/chapter is
    /// missing, the flat summary is blank, the LLM call/parse fails, OR the persist fails (a non-null return
    /// therefore means the structured brief was actually saved, so the caller's "rederived" signal is honest).
    /// </summary>
    public async Task<StructuredChunkSummaryData?> RederiveChapterBriefFromUserSummaryAsync(
        Guid bookId,
        Guid chapterId,
        string language,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);
        var activeModel = ActiveSummarizationModel;

        try
        {
            var existing = await _db.ChunkSummaries
                .FirstOrDefaultAsync(cs => cs.ChapterId == chapterId, ct);
            if (existing == null || string.IsNullOrWhiteSpace(existing.SummaryText))
                // Nothing to seed from: re-derive is meaningless without the user's authoritative summary.
                return existing == null ? null : Parse(existing.StructuredJson);

            var chapter = await _db.Chapters
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == chapterId, ct);
            // Use the current chapter text for supporting detail; the user summary is the source of truth, so
            // an empty chapter (cleared text) still re-derives from the summary alone.
            var chapterText = chapter == null
                ? string.Empty
                : SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");

            // The model needs SOMETHING to read: the user summary is always present here. Feed the chapter
            // text as InputText when available, else the summary itself, so ComputeChapterBriefAsync's
            // empty-text guard does not short-circuit a summary-only re-derive.
            var inputText = string.IsNullOrWhiteSpace(chapterText) ? existing.SummaryText : chapterText;

            var built = await ComputeChapterBriefAsync(inputText, lang, ct, userSummarySeed: existing.SummaryText);
            if (built == null)
                return null;

            var (brief, builtModel) = built.Value;

            // be-c02 (language-flip guard): the row's Language is the SINGLE identity for BOTH surfaces.
            // If this re-derive switches the row to a DIFFERENT language than it currently holds, the flat
            // SummaryText we seeded from is in the OLD language; once Language flips to `lang` it would
            // masquerade as the new locale's prose, and BookContextAssembler selects flat fallbacks by
            // Language only — so it would assemble that mismatched-language prose into the requested context.
            // Mirror LoadOrBuildChapterBriefAsync's flip handling: clear the now-stale flat summary (and its
            // user-edit clobber guard, which is meaningless once the prose no longer matches the locale) so it
            // cannot leak. The new structured brief we just built IS in `lang`, so the surfaces stay coherent.
            // NOTE: this branch is normally unreachable in practice (the FE always passes the book language,
            // never a per-chapter flip); it exists so a book-language change followed by a re-derive cannot
            // leak old-locale prose, consistent with the load path.
            if (!string.Equals(existing.Language, lang, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(existing.SummaryText))
            {
                existing.SummaryText = string.Empty;
                existing.SummaryUserEdited = false;
                existing.SummaryUserEditedAt = null;
            }

            existing.StructuredJson = JsonSerializer.Serialize(brief, SerializeOpts);
            existing.BuiltWithModel = builtModel;
            existing.Language = lang;
            existing.StructuredBuiltAt = DateTimeOffset.UtcNow;
            // Deliberately DO NOT touch SummaryText / CreatedAt / SummaryUserEdited / SummaryUserEditedAt
            // on the SAME-language path: the flat surface (the user's authoritative edit) is preserved and
            // stays clobber-guarded. They are reconciled ABOVE only on a language flip (stale old-locale prose).

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to persist re-derived structured brief for chapter {ChapterId}", chapterId);
                // Detach so the now-Modified state is not retried by a later SaveChanges on this scoped
                // DbContext. UNLIKE the load path (which returns the in-memory brief because the caller can
                // still use it this request), the re-derive's whole purpose is to PERSIST the structured brief
                // so the whole-book review reflects the user's edit. An unpersisted brief did not achieve that,
                // so report failure (null) — otherwise the controller's rederived flag (brief != null) would
                // claim success while the database row was never updated.
                _db.Entry(existing).State = EntityState.Detached;
                return null;
            }

            return brief;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-derive structured brief for chapter {ChapterId}", chapterId);
            return null;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Runs the structured-brief prompt through <see cref="IAiRouter"/> (Summarization task, JSON mode like
    /// the LinguisticAnalysis baseline build) and parses the response into
    /// <see cref="StructuredChunkSummaryData"/>. Returns null on any LLM/parse failure. Surfaces the model
    /// the request was ACTUALLY routed to so the caller stamps <see cref="ChunkSummary.BuiltWithModel"/>
    /// with the real model (falling back to the config-resolved active model when a provider left it blank).
    /// </summary>
    private async Task<(StructuredChunkSummaryData Brief, string? Model)?> ComputeChapterBriefAsync(
        string text,
        string language,
        CancellationToken ct,
        string? userSummarySeed = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // wb3-c04: when a user-edited summary is supplied, seed the derivation with it as the AUTHORITATIVE
        // chapter understanding (the chapter text still flows as InputText for detail). Otherwise use the
        // plain structured-brief instruction (the normal AI-only derivation).
        var instruction = string.IsNullOrWhiteSpace(userSummarySeed)
            ? _promptFactory.GetStructuredChapterBriefPrompt(language)
            : _promptFactory.GetStructuredChapterBriefPromptSeededWithUserSummary(language, userSummarySeed);
        var request = new AiRequest
        {
            InputText = text,
            Instruction = instruction,
            TaskType = AiTaskType.Summarization,
            Language = language,
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
            _logger.LogWarning(ex, "Structured chapter-brief computation failed");
            return null;
        }

        var raw = response.Content;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Reuse the SAME extractor the user-facing analysis paths use (BOM/bidi stripping, balanced-brace
        // matching, fenced/prose-wrapped JSON) so a Hebrew prose-wrapped reply still parses.
        var json = UnifiedAnalysisService.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var brief = Parse(json);
        if (brief == null)
            return null;

        // Phase 4c-16: a parseable-but-DEGENERATE brief (e.g. "{}" → all lists empty + blank toneNotes) is a
        // FAILURE, not a success. Parse returns a non-null record for "{}", so absent this guard a zero-content
        // brief would be treated as a built result — and the callers would then PERSIST it: the re-derive path
        // would overwrite a previously-good StructuredJson with an empty one (reporting rederived=true), and
        // LoadOrBuildChapterBriefAsync would replace/insert an empty brief. This is the documented
        // num_ctx-truncation / empty-payload failure on the 8GB GPU. Return null so both callers take their
        // existing graceful-miss branch (no destructive write; the prior brief is preserved; rederived=false).
        if (StructuredChunkSummaryParser.IsDegenerate(brief))
        {
            _logger.LogWarning(
                "Structured chapter-brief rejected as degenerate (no plot/character/thematic/open-thread content and blank tone notes) for a {Language} build; treating as a build failure so it cannot overwrite an existing brief",
                language);
            return null;
        }

        var model = string.IsNullOrWhiteSpace(response.Model) ? ActiveSummarizationModel : response.Model;
        return (brief, model);
    }

    /// <summary>Defensive parse of a StructuredJson blob into the typed brief; null on null/blank/invalid.
    /// Delegates to the shared <see cref="StructuredChunkSummaryParser"/> so this and the freshness/status/
    /// composition paths cannot drift into different parse semantics.</summary>
    private static StructuredChunkSummaryData? Parse(string? json) => StructuredChunkSummaryParser.Parse(json);

    /// <summary>
    /// Freshness gate for a cached structured brief against the current chapter + active model. A brief is
    /// fresh only when it ACTUALLY carries StructuredJson, is in the requested language, and passes the
    /// SHARED <see cref="ChapterStyleProfileFreshness.IsFresh"/> predicate (timestamp AND model) — so this
    /// and the style-baseline gate share ONE staleness definition.
    ///
    /// wb1-r02: the structured build timestamp is <see cref="ChunkSummary.StructuredBuiltAt"/>, NOT the
    /// shared <see cref="ChunkSummary.CreatedAt"/>. The flat re-summary path
    /// (BookIntelligenceService.SummarizeChaptersAsync) bumps CreatedAt on the SAME row, so keying the
    /// structured gate on CreatedAt would let a flat re-summary mask a stale structured brief (a brief
    /// built before the chapter's last edit would falsely read fresh). StructuredBuiltAt is stamped only by
    /// THIS service when it writes StructuredJson, so it tracks the structured surface alone. A null
    /// StructuredBuiltAt (legacy structured row built before the column existed) is treated as stale → the
    /// brief self-heals on next access, matching the rest of Phase 1.
    /// </summary>
    private static bool IsFresh(ChunkSummary summary, Chapter chapter, string lang, string? activeModel)
    {
        if (!StructuredChunkSummaryParser.IsUsable(summary.StructuredJson))
            // Never built (flat-only legacy row) OR a non-empty but UNPARSEABLE brief → not usable, must
            // (re)build. Testing only for non-empty here let an unparseable brief read fresh forever (returned
            // as null without rebuilding) while status counted it built and composition skipped it, so the
            // rollup could never cover it. IsUsable parses, exactly as ComposeChapterBriefsAsync does.
            return false;
        if (!string.Equals(summary.Language, lang, StringComparison.Ordinal))
            return false; // cached in a different language → rebuild for the requested one
        if (summary.StructuredBuiltAt is not { } structuredBuiltAt)
            return false; // legacy structured row with no structured build stamp → rebuild once (self-heal)
        return ChapterStyleProfileFreshness.IsFresh(
            structuredBuiltAt, summary.BuiltWithModel, chapter.UpdatedAt, activeModel);
    }
}

/// <summary>Outcome of a book-wide structured chapter-brief build.</summary>
public sealed class ChapterBriefBuildResult
{
    public int TotalChapters { get; init; }

    /// <summary>Chapters that ended with a usable structured brief.</summary>
    public int BuiltChapters { get; init; }

    /// <summary>Chapters that did NOT end with a structured brief (empty/parse-fail/throw). Never aborts.</summary>
    public int FailedChapters { get; init; }
}
