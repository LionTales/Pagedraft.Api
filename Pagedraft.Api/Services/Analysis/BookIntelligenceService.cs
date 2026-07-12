using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Book-level intelligence: summarize chapters, build a BookProfile
/// (genre, synopsis, characters, story structure), and answer Q&A.
///
/// Invalidation strategy:
///   - Each ChunkSummary records CreatedAt.
///   - On RefreshProfileAsync, compare Chapter.UpdatedAt vs ChunkSummary.CreatedAt.
///   - Only re-summarize chapters where UpdatedAt > ChunkSummary.CreatedAt (stale).
///   - After re-summarizing stale chapters, rebuild the full BookProfile.
/// </summary>
public class BookIntelligenceService
{
    private readonly AppDbContext _db;
    private readonly UnifiedAnalysisService _analysis;
    private readonly BookContextAssembler _bookContextAssembler;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IBookEntityProvider _bookEntities;
    private readonly ILogger<BookIntelligenceService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BookIntelligenceService(
        AppDbContext db,
        UnifiedAnalysisService analysis,
        BookContextAssembler bookContextAssembler,
        IOptions<AiOptions> aiOptions,
        IBookEntityProvider bookEntities,
        ILogger<BookIntelligenceService> logger)
    {
        _db = db;
        _analysis = analysis;
        _bookContextAssembler = bookContextAssembler;
        _aiOptions = aiOptions;
        _bookEntities = bookEntities;
        _logger = logger;
    }

    // ─── Chapter Summarization ──────────────────────────────────────

    /// <summary>
    /// Summarize all chapters of the book. Skips chapters that already have
    /// a fresh (non-stale) ChunkSummary.
    /// </summary>
    public async Task SummarizeChaptersAsync(Guid bookId, string language, CancellationToken ct = default)
    {
        // Persist the flat summary under the NORMALIZED locale (e.g. "en-US" → "en"), the SAME key the
        // structured path (ChapterBriefService) and the BookContextAssembler read by. Storing the raw request
        // value here is what let the assembler's exact-match selection skip the summary and degrade to raw
        // chapter text. Normalize is idempotent for already-normalized values (e.g. "he" → "he").
        var lang = BaselineLanguageResolver.Normalize(language);

        var chapters = await _db.Chapters
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .ToListAsync(ct);

        if (chapters.Count == 0)
            throw new InvalidOperationException("Book has no chapters.");

        var existingSummaries = await _db.Set<ChunkSummary>()
            .Where(cs => cs.BookId == bookId)
            .ToDictionaryAsync(cs => cs.ChapterId, ct);

        foreach (var chapter in chapters)
        {
            var text = chapter.ContentText?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (existingSummaries.TryGetValue(chapter.Id, out var existing) &&
                existing.CreatedAt >= chapter.UpdatedAt)
            {
                _logger.LogDebug("Chapter {ChapterId} summary is fresh, skipping", chapter.Id);
                continue;
            }

            // wb3-c04 clobber guard: the user has manually edited THIS chapter's flat SummaryText, which is
            // their own authoritative understanding of the chapter. The automatic re-summary must NOT silently
            // overwrite it — even when the chapter text changed and the AI freshness check above would
            // otherwise re-run. Skip the row; a deliberate overwrite is only reachable through the explicit
            // re-derive action the user triggers (which consumes the edit rather than discarding it). This is
            // logged at WARN so a skipped re-summary is never silent.
            if (existing != null && existing.SummaryUserEdited)
            {
                _logger.LogWarning(
                    "Chapter {ChapterId} flat summary is user-edited; skipping automatic re-summary to " +
                    "preserve the user's manual edit (re-derive must be user-triggered).", chapter.Id);
                continue;
            }

            _logger.LogInformation("Summarizing chapter {Title} ({Id})", chapter.Title, chapter.Id);
            // be-c02: pass the REAL bookId — the raw seam feeds it to the repair layer's per-book proper-noun
            // LEAVE set, so this book's own character/place names are spared in the summary we persist below.
            var summaryText = await _analysis.RunRawAsync(
                text, AnalysisType.Summarization, null, language, bookId, ct);

            if (existing != null)
            {
                existing.SummaryText = summaryText;
                existing.Language = lang;
                existing.CreatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _db.Set<ChunkSummary>().Add(new ChunkSummary
                {
                    BookId = bookId,
                    ChapterId = chapter.Id,
                    SummaryText = summaryText,
                    Language = lang
                });
            }

            await _db.SaveChangesAsync(ct);
        }
    }

    // ─── Build / Refresh Profile ────────────────────────────────────

    /// <summary>
    /// Build a complete BookProfile from chapter summaries.
    /// Runs BookOverview, Synopsis, CharacterAnalysis, and StoryAnalysis prompts
    /// against the concatenated summaries.
    /// </summary>
    public async Task<BookProfile> BuildBookProfileAsync(Guid bookId, string language, CancellationToken ct = default)
    {
        // wb1-c03: route through the SHARED budget-aware assembler instead of the unguarded flat-summary
        // concat (GetConcatenatedSummaries appended every chapter summary with no size guard, overflowing
        // the model context on a large book). The assembler prefers the dense structured BookBrief +
        // ChapterBriefs and degrades to a budget-guarded flat-summary concat when no briefs are built yet;
        // either way it stays within the NumCtx-derived budget and logs anything it drops.
        // Budget the assembly to the SMALLEST window across the tasks that consume this SAME text below:
        // BookOverview / CharacterAnalysis / StoryAnalysis route to LinguisticAnalysis and Synopsis to
        // Summarization, so the context must fit whichever has the tighter num_ctx (Bug 3: budgeting against
        // Summarization alone could overflow the LinguisticAnalysis window when it is configured smaller).
        var assembly = await _bookContextAssembler.AssembleAsync(
            bookId, language,
            new[] { AiTaskType.LinguisticAnalysis, AiTaskType.Summarization }, ct);
        var concatenated = assembly.Text;
        if (string.IsNullOrWhiteSpace(concatenated))
            throw new InvalidOperationException("No chapter summaries found. Run SummarizeChaptersAsync first.");

        _logger.LogInformation(
            "Building book profile for {BookId} (structuredBriefs={Used}, dropped={Dropped}/{Budget}t)",
            bookId, assembly.UsedStructuredBriefs, assembly.DroppedCount, assembly.BudgetTokens);

        // be-c02: pass the REAL bookId on every raw run (same seam parity as SummarizeChaptersAsync above).
        var overviewTask = _analysis.RunRawAsync(concatenated, AnalysisType.BookOverview, null, language, bookId, ct);
        var synopsisTask = _analysis.RunRawAsync(concatenated, AnalysisType.Synopsis, null, language, bookId, ct);
        var charsTask = _analysis.RunRawAsync(concatenated, AnalysisType.CharacterAnalysis, null, language, bookId, ct);
        var storyTask = _analysis.RunRawAsync(concatenated, AnalysisType.StoryAnalysis, null, language, bookId, ct);

        await Task.WhenAll(overviewTask, synopsisTask, charsTask, storyTask);

        var overview = TryDeserialize<BookOverviewResult>(overviewTask.Result);
        var profile = await GetOrCreateProfile(bookId, ct);

        profile.Genre = overview?.Genre;
        profile.SubGenre = overview?.SubGenre;
        profile.TargetAudience = overview?.TargetAudience;
        profile.LiteratureLevel = overview?.LiteratureLevel;
        profile.LanguageRegister = overview?.LanguageRegister;
        profile.Synopsis = synopsisTask.Result;

        // f5-wire coverage fix: BuildBookProfileAsync is the ONLY producer of CharacterAnalysis / StoryAnalysis,
        // and it runs them through UnifiedAnalysisService.RunRawAsync(structuredJson: null) — for those types
        // ApplyAnalysisRepairAsync (and GlossaryRepairPass.Apply) is a STRICT NO-OP, so the shipped deterministic
        // glossary that cleans leaked English in Hebrew prose never reached the persisted profile JSON. BookReview
        // has the same "never reaches the RunAsync seam" property and solved it with an ENGINE HOOK
        // (BookReviewService.ApplyGlossaryToFindings); mirror that here. Deserialize the (fence-tolerant) raw model
        // JSON, run the SAME glossary over the whitelisted prose fields IN PLACE, and store CLEAN reserialized JSON.
        // Gated on the repair layer (Enabled + PerType-allows the type) and Hebrew (enforced inside RepairFields);
        // fail-safe — an off gate, an unparseable payload, or ANY repair fault stores the raw string unchanged, so
        // a repair fault can NEVER break the profile build. BookOverview.Summary is not persisted here and Synopsis
        // was not in the f5 wired set, so only CharactersJson + StoryStructureJson are repaired.
        var repairCfg = _aiOptions.Value.AnalysisRepair;
        profile.CharactersJson = RepairStructuredProfileJson<CharacterAnalysisResult>(
            charsTask.Result, language, AnalysisType.CharacterAnalysis, repairCfg, RepairableFields.For, _logger);
        profile.StoryStructureJson = RepairStructuredProfileJson<StoryAnalysisResult>(
            storyTask.Result, language, AnalysisType.StoryAnalysis, repairCfg, RepairableFields.For, _logger);

        profile.Language = language;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        // be-c03: this build just PRODUCED a harvest source for the per-book proper-noun LEAVE set —
        // BookProfile.CharactersJson (a serialized CharacterAnalysisResult). Without this invalidation the
        // ordinary production sequence defeats that source outright: the first chapter analysis on a fresh book
        // builds the entity set from the manuscript alone (no character names exist yet), and the names this
        // build persists would never enter the set. Drop the cached set so the NEXT analysis rebuilds it with
        // this book's character names in it — a name the set fails to spare is a name the repair model rewrites.
        // Invalidate is non-throwing by contract, so it can never break the profile persist above.
        _bookEntities.Invalidate(bookId);

        _logger.LogInformation("Book profile built for {BookId}", bookId);
        return profile;
    }

    /// <summary>
    /// Refresh: re-summarize stale chapters, then rebuild the profile.
    /// </summary>
    public async Task<BookProfile> RefreshProfileAsync(Guid bookId, string language, CancellationToken ct = default)
    {
        await SummarizeChaptersAsync(bookId, language, ct);
        return await BuildBookProfileAsync(bookId, language, ct);
    }

    // ─── Q&A ────────────────────────────────────────────────────────

    /// <summary>
    /// Answer a question about the book using chapter summaries as context.
    /// Persists the result as an AnalysisResult with Scope=Book, Type=QA.
    /// </summary>
    public async Task<AnalysisResult> AskAsync(Guid bookId, string question, string language, CancellationToken ct = default)
    {
        // wb1-c03: budget-aware assembly (shared path) instead of the unguarded summary concat. The question
        // is appended AFTER the budgeted context, so the context can never push total input past the model
        // window on its own (the assembler already capped it). Anything dropped is logged in the assembler.
        // Budget to the QA route's task (GenericChat), whose window can be smaller than Summarization's
        // (Bug 3) — and the appended question + the generated answer must still fit alongside the context.
        var assembly = await _bookContextAssembler.AssembleAsync(
            bookId, language, new[] { AiTaskType.GenericChat }, ct);
        var summaries = assembly.Text;
        if (string.IsNullOrWhiteSpace(summaries))
            throw new InvalidOperationException("No chapter summaries found. Run SummarizeChaptersAsync first.");

        var inputText = $"{summaries}\n\n---\nשאלה / Question:\n{question}";

        return await _analysis.RunWithInputAsync(
            AnalysisScope.Book,
            AnalysisType.QA,
            bookId,
            chapterId: null,
            sceneId: null,
            inputText,
            language,
            ct);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    // NOTE: the former GetConcatenatedSummaries helper (unguarded flat-summary concat) was removed in
    // wb1-c03. Its flat-summary fallback now lives in BookContextAssembler.AssembleFlatFallbackAsync, which
    // applies the same per-chapter "## פרק / Chapter: {Title}" framing but caps the result at the NumCtx
    // budget, so the book-level analyses can no longer overflow the model context.

    private async Task<BookProfile> GetOrCreateProfile(Guid bookId, CancellationToken ct)
    {
        var existing = await _db.Set<BookProfile>().FirstOrDefaultAsync(p => p.BookId == bookId, ct);
        if (existing != null) return existing;

        var profile = new BookProfile { BookId = bookId };
        _db.Set<BookProfile>().Add(profile);
        return profile;
    }

    private static T? TryDeserialize<T>(string content) where T : class
    {
        try
        {
            var json = ExtractJson(content);
            if (json == null) return null;
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Deterministic English -> Hebrew glossary safety net for a book-level structured-Hebrew-prose analysis
    /// produced on the profile path (CharacterAnalysis / StoryAnalysis). These types are generated via
    /// <see cref="UnifiedAnalysisService.RunRawAsync"/> with <c>structuredJson: null</c>, so the shipped repair
    /// layer (<c>ApplyAnalysisRepairAsync</c> -> <see cref="GlossaryRepairPass.Apply"/>) is a strict no-op for
    /// them — this hook applies the SAME glossary to the persisted JSON, mirroring
    /// <c>BookReviewService.ApplyGlossaryToFindings</c> (f5-wire JOB 2).
    ///
    /// Deserializes the raw model output with the SAME fence-tolerant reader <see cref="TryDeserialize{T}"/>
    /// uses (<see cref="ExtractJson"/> brace-matching skips a leading ```json fence), runs
    /// <see cref="GlossaryRepairPass.RepairFields"/> over the whitelisted prose accessors IN PLACE, then
    /// reserializes to CLEAN JSON with the pipeline's camelCase <see cref="JsonOpts"/> (which also strips the
    /// model's markdown fence — the FE parses <c>profile.charactersJson</c> with a bare <c>JSON.parse</c> that a
    /// fenced string would break).
    ///
    /// GATE (mirrors <c>ApplyAnalysisRepairAsync</c> + <c>PerTypeAllows</c>): runs only when the repair layer is
    /// <see cref="AnalysisRepairOptions.Enabled"/> AND <see cref="PerTypeAllows"/> allows the type; the Hebrew
    /// check lives inside <see cref="GlossaryRepairPass.RepairFields"/>. FAIL-SAFE: an off gate, an unparseable
    /// payload, or ANY repair/serialize fault returns the RAW string unchanged, so this can never break the
    /// profile build (identical to the pre-fix behaviour on every non-repaired path). NO new LLM (glossary only).
    /// </summary>
    private static string RepairStructuredProfileJson<T>(
        string rawResult,
        string language,
        AnalysisType type,
        AnalysisRepairOptions? cfg,
        Func<T, IReadOnlyList<RepairableField>> accessorsOf,
        ILogger logger) where T : class
    {
        // Layer gate: a null block, Enabled=false, or a non-empty PerType map that excludes this type is a
        // strict no-op -> store the raw model output verbatim (pre-fix behaviour).
        if (cfg is null || !cfg.Enabled || !PerTypeAllows(cfg, type))
            return rawResult;

        try
        {
            // Fence-tolerant deserialize (ExtractJson skips a leading ```json fence). Unparseable -> keep raw.
            var parsed = TryDeserialize<T>(rawResult);
            if (parsed is null)
                return rawResult;

            // Deterministic glossary over the whitelisted prose fields IN PLACE. RepairFields is itself
            // Hebrew-gated + guard-gated (a clean field is byte-identical at zero cost; a null collection/element
            // is never walked). NO new model call.
            GlossaryRepairPass.RepairFields(accessorsOf(parsed), language);

            // Reserialize to CLEAN JSON (also strips the model's markdown fence so the FE JSON.parse succeeds).
            return JsonSerializer.Serialize(parsed, JsonOpts);
        }
        catch (Exception ex)
        {
            // FAIL-SAFE: a repair/serialize fault must NEVER break the profile build. Keep the raw string.
            logger.LogWarning(ex,
                "Book profile glossary repair threw for type={Type} ({Lang}); storing un-repaired raw JSON (fail-safe).",
                type, language);
            return rawResult;
        }
    }

    /// <summary>
    /// Mirror of <c>UnifiedAnalysisService.PerTypeAllows</c>: a null/empty <see cref="AnalysisRepairOptions.PerType"/>
    /// map means NO per-type restriction (allowed); a non-empty map is a strict allowlist keyed by the
    /// <see cref="AnalysisType"/> name, so the type must be present AND true.
    /// </summary>
    private static bool PerTypeAllows(AnalysisRepairOptions cfg, AnalysisType type)
    {
        if (cfg.PerType is null || cfg.PerType.Count == 0) return true;
        return cfg.PerType.TryGetValue(type.ToString(), out var enabled) && enabled;
    }

    private static string? ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var start = content.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inString = false, escape = false;
        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}') depth--;
            if (depth == 0) return content[start..(i + 1)];
        }
        return null;
    }
}
