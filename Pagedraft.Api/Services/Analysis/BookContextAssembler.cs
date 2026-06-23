using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Identifies a chapter (or summary unit) that did NOT fit the whole-book token budget. Surfaced by
/// <see cref="BookContextAssembly.DroppedUnits"/> and logged, so a truncation is never silent
/// ("No silent truncation" workspace rule).
/// </summary>
public sealed record DroppedBookUnit
{
    public int Order { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>Estimated token cost of the unit that was skipped (for diagnostics).</summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>
/// Result of assembling the whole-book analysis context under a token budget. Carries the assembled text
/// (ready to feed the book-level analysis prompts as InputText), plus structured provenance: which units
/// were included, which were dropped (and why budget), and whether the dense structured-brief path or the
/// degraded flat-summary fallback was used.
/// </summary>
public sealed record BookContextAssembly
{
    /// <summary>The assembled context text, within <see cref="BudgetTokens"/>.</summary>
    public required string Text { get; init; }

    /// <summary>The L2 BookBrief that was placed FIRST (always included when one exists). Null only when
    /// no rollup could be composed (e.g. no chapters), in which case the fallback path supplies Text.</summary>
    public BookBrief? BookBrief { get; init; }

    /// <summary>The L1 ChapterBriefs that fit the budget, in inclusion (narrative) order.</summary>
    public IReadOnlyList<ChapterBrief> IncludedChapterBriefs { get; init; } = Array.Empty<ChapterBrief>();

    /// <summary>Units (chapters) that did NOT fit the budget. Empty when everything fit.</summary>
    public IReadOnlyList<DroppedBookUnit> DroppedUnits { get; init; } = Array.Empty<DroppedBookUnit>();

    /// <summary>True when the dense structured ChapterBrief path was used; false when it degraded to the
    /// flat-summary fallback (no usable structured briefs yet).</summary>
    public bool UsedStructuredBriefs { get; init; }

    /// <summary>The token budget the assembly was held within.</summary>
    public int BudgetTokens { get; init; }

    /// <summary>The estimated token size of <see cref="Text"/> (always &lt;= <see cref="BudgetTokens"/>
    /// once at least the BookBrief alone fits; may exceed only in the pathological case where even the
    /// BookBrief alone is larger than the budget, which is logged).</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Convenience: how many units were dropped.</summary>
    public int DroppedCount => DroppedUnits.Count;
}

/// <summary>
/// The SINGLE budgeted assembler for whole-book analysis context. Both the book-scope path in
/// <see cref="AnalysisContextService"/> (ResolveBookAsync) and the book-level analyses in
/// <see cref="BookIntelligenceService"/> (BookOverview / Synopsis / CharacterAnalysis / StoryAnalysis / QA)
/// route through here, so there is ONE budgeted path rather than two divergent concats.
///
/// WHY: the previous book paths concatenated EVERY chapter's full text (ResolveBookAsync) or EVERY flat
/// chapter summary (BookIntelligenceService.GetConcatenatedSummaries) with NO size guard. A large book
/// overflows the model context window; Ollama silently TRUNCATES anything past num_ctx, yielding broken or
/// empty output. This assembler caps assembly at a token budget derived from the active model's NumCtx.
///
/// COMPOSITION (preferred path):
///   1. ALWAYS include the L2 <see cref="BookBrief"/> first (genre/themes/synopsis rollup) — the cheapest,
///      densest, most globally-relevant block.
///   2. Then add the most-relevant L1 <see cref="ChapterBrief"/>s until the budget is hit. "Most relevant"
///      = NARRATIVE ORDER (chapter Order ascending): a book analysis reads the story front-to-back, so
///      preserving order keeps the earliest (setup/premise) chapters when later ones must be dropped, and
///      the included prefix is always a contiguous, coherent run rather than a scattered sample. Structured
///      briefs are far DENSER than flat summaries (a few plot events + states vs. prose), which is the lever
///      that lets a whole book fit a fixed budget.
///   3. Any chapter brief that does not fit is DROPPED, recorded in <see cref="BookContextAssembly.DroppedUnits"/>
///      AND logged with the count — no silent truncation.
///
/// GRACEFUL DEGRADATION (fallback path): when NO usable structured briefs exist yet (the book summary has
/// not been built), fall back to the existing flat per-chapter summary concat — but STILL apply the same
/// budget guard so the fallback cannot overflow either. When even flat summaries are absent, fall back to a
/// budget-trimmed concat of the raw chapter text (so book analysis still has something to chew on).
/// </summary>
public class BookContextAssembler
{
    // Rough chars-per-token estimate. Hebrew/English mixed prose lands around 3-4 chars/token for typical
    // tokenizers; 4 is the common conservative English heuristic. We deliberately bias LOW (more tokens per
    // text => smaller assembly) so the estimate over- rather than under-counts and we stay safely under the
    // hard num_ctx wall where Ollama truncates. Centralized so the estimate is identical everywhere.
    private const double CharsPerToken = 4.0;

    // Separator written between the BookBrief and every ChapterBrief in the assembled Text. It is part of the
    // emitted string, so it MUST be charged against the budget: fold it into each block before estimating
    // (exactly as the flat-fallback path does with its "{header}\n{body}\n\n" block) rather than appending it
    // uncounted, or the separators inflate Text beyond what the running total accounts for.
    private const string BlockSeparator = "\n\n";

    private readonly AppDbContext _db;
    private readonly BookSummaryService _bookSummary;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<BookContextAssembler> _logger;

    public BookContextAssembler(
        AppDbContext db,
        BookSummaryService bookSummary,
        IOptions<AiOptions> aiOptions,
        ILogger<BookContextAssembler> logger)
    {
        _db = db;
        _bookSummary = bookSummary;
        _aiOptions = aiOptions;
        _logger = logger;
    }

    /// <summary>Estimated token cost of a text blob (shared heuristic).</summary>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>
    /// Resolves the effective token budget for the whole-book context. Honours an explicit
    /// <see cref="AiOptions.BookContextTokenBudget"/>, otherwise derives it from the NumCtx of the task(s)
    /// that will actually CONSUME the assembled text.
    ///
    /// WHY the consuming task and not always Summarization: the assembled text is fed to BookOverview /
    /// CharacterAnalysis / StoryAnalysis (<see cref="AiTaskType.LinguisticAnalysis"/>), Synopsis
    /// (<see cref="AiTaskType.Summarization"/>) and QA (<see cref="AiTaskType.GenericChat"/>), each of which
    /// can have a SMALLER per-task num_ctx than Summarization. Budgeting against Summarization alone would
    /// let the context overflow the consumer's window, where Ollama silently truncates. When several tasks
    /// share one assembly (BookIntelligenceService reuses the same text across routes) we budget to the
    /// SMALLEST window so the context fits the tightest consumer. With no task supplied we fall back to
    /// Summarization (the task the briefs were built under). The NumCtx lookup mirrors
    /// <see cref="OllamaProvider"/>'s tuning precedence: provider+task key, then provider key, then the
    /// ProviderTuningOptions default.
    /// </summary>
    public int ResolveBudgetTokens(IReadOnlyCollection<AiTaskType>? consumingTasks = null)
    {
        var opt = _aiOptions.Value;
        var tasks = consumingTasks is { Count: > 0 }
            ? consumingTasks
            : new[] { AiTaskType.Summarization };
        var numCtx = tasks.Min(t => ResolveNumCtxForTask(opt, t));
        return opt.EffectiveBookContextTokenBudget(numCtx);
    }

    /// <summary>
    /// Active-model context window (num_ctx) for a task, mirroring OllamaProvider.GetTuning's key precedence:
    /// "{provider}_{task}" → "{provider}" → ProviderTuningOptions default. The tuning key uses the provider
    /// NAME (resolved via the shared resolver), exactly as the provider does at request time.
    /// </summary>
    private static int ResolveNumCtxForTask(AiOptions opt, AiTaskType task)
    {
        var (provider, _) = LinguisticModelResolver.ResolveForTask(opt, task);
        var settings = opt.ProviderSettings;
        if (settings != null)
        {
            if (settings.TryGetValue($"{provider}_{task}", out var taskTuning) && taskTuning.NumCtx > 0)
                return taskTuning.NumCtx;
            if (settings.TryGetValue(provider, out var providerTuning) && providerTuning.NumCtx > 0)
                return providerTuning.NumCtx;
        }
        return new ProviderTuningOptions().NumCtx; // 4096, same fallback the provider uses
    }

    /// <summary>
    /// Assembles the budgeted whole-book context for (bookId, language). Uses the dense structured brief path
    /// whenever ANY chapter has a fresh structured brief (BookBrief + ordered ChapterBriefs); chapters that
    /// lack a fresh brief are NOT silently omitted — they are filled PER CHAPTER from their flat summary, then
    /// raw text, so a partially-built book still carries every chapter's content (dense where available,
    /// degraded elsewhere). Degrades to the whole-book flat-summary (then raw-text) fallback only when NO
    /// structured briefs exist at all. Anything that does not fit the budget is recorded in DroppedUnits and
    /// logged. Never overflows the resolved budget beyond the unavoidable BookBrief-alone case, which is logged.
    /// </summary>
    public async Task<BookContextAssembly> AssembleAsync(
        Guid bookId,
        string language,
        IReadOnlyCollection<AiTaskType>? consumingTasks = null,
        CancellationToken ct = default)
    {
        var budget = ResolveBudgetTokens(consumingTasks);

        // Prefer the dense structured path: L2 BookBrief + ordered L1 ChapterBriefs. Chapters without a fresh
        // brief are back-filled from their flat summary / raw text inside the structured assembly (no silent
        // truncation under partial coverage).
        var chapterBriefs = await _bookSummary.ComposeChapterBriefsAsync(bookId, language, ct);
        if (chapterBriefs.Count > 0)
        {
            var bookBrief = await _bookSummary.ComposeBookBriefAsync(bookId, chapterBriefs, ct);
            return await AssembleStructuredWithFallbackAsync(bookId, language, bookBrief, chapterBriefs, budget, ct);
        }

        // No usable structured briefs yet → degrade to the flat-summary fallback, still budget-guarded.
        _logger.LogInformation(
            "BookContextAssembler: no structured chapter briefs for book {BookId} ({Lang}); " +
            "falling back to budget-guarded flat summaries.", bookId, language);
        return await AssembleFlatFallbackAsync(bookId, language, budget, ct);
    }

    // ─── Shared read helper ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the flat per-chapter summaries for the book whose stored <see cref="ChunkSummary.Language"/>
    /// NORMALIZES to <paramref name="lang"/> (already normalized), keyed by ChapterId, excluding blanks.
    ///
    /// Matches on the NORMALIZED locale rather than an exact string because the legacy flat path
    /// (<see cref="BookIntelligenceService.SummarizeChaptersAsync"/>) can persist the RAW request value
    /// (e.g. "en-US") while the assembler keys on the normalized locale (e.g. "en"); an exact match would
    /// SKIP those rows and degrade to raw chapter text despite a usable summary existing. A genuinely
    /// different language (e.g. "he") still normalizes differently and is excluded, so this does not
    /// reintroduce a cross-language leak. Normalization runs in memory (it does not translate to SQL); there
    /// is at most one ChunkSummary per chapter, so the scan is bounded by the chapter count.
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadFlatSummariesByNormalizedLanguageAsync(
        Guid bookId, string lang, CancellationToken ct)
    {
        var rows = await _db.ChunkSummaries
            .AsNoTracking()
            .Where(cs => cs.BookId == bookId)
            .Select(cs => new { cs.ChapterId, cs.SummaryText, cs.Language })
            .ToListAsync(ct);

        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x.SummaryText)
                && BaselineLanguageResolver.Normalize(x.Language) == lang)
            .ToDictionary(x => x.ChapterId, x => x.SummaryText!);
    }

    // ─── Structured path (dense briefs + per-chapter flat/raw back-fill for uncovered chapters) ────────

    /// <summary>
    /// Assembles the structured whole-book context: the L2 BookBrief first, then EVERY chapter in narrative
    /// order. A chapter with a fresh L1 brief contributes its dense structured block; a chapter WITHOUT one
    /// is back-filled from its flat summary, then its raw text, so partial structured coverage never silently
    /// omits a chapter (the prior behaviour iterated only the fresh briefs, dropping uncovered chapters from
    /// both the context AND DroppedUnits). Anything that does not fit the budget — structured or flat — is
    /// recorded in DroppedUnits exactly like a token-budget drop. A genuinely empty chapter (no brief, no
    /// summary, no text) has nothing to contribute and is not recorded (it is not a truncation).
    /// </summary>
    private async Task<BookContextAssembly> AssembleStructuredWithFallbackAsync(
        Guid bookId,
        string language,
        BookBrief bookBrief,
        IReadOnlyList<ChapterBrief> chapterBriefs,
        int budget,
        CancellationToken ct)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        // The FULL chapter set in narrative order, so chapters without a fresh structured brief can be
        // back-filled rather than silently omitted. Carries raw ContentText for the last-resort fill.
        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => new { c.Id, c.Order, c.Title, c.ContentText })
            .ToListAsync(ct);

        // Flat summaries for (book, language), keyed by ChapterId — the degraded fill for an uncovered chapter
        // (preferred over raw text). Matched by NORMALIZED locale so a legacy summary stored under the raw
        // request value (e.g. "en-US") is still found, while a different language is excluded (no leak).
        var flatByChapterId = await LoadFlatSummariesByNormalizedLanguageAsync(bookId, lang, ct);

        // Fresh structured briefs keyed by chapter Order (ChapterBrief carries Order, not ChapterId).
        var freshBriefByOrder = chapterBriefs.ToDictionary(b => b.Order);

        var sb = new StringBuilder();
        var bookBriefText = FormatBookBrief(bookBrief);

        // 1. BookBrief ALWAYS first. Charge the trailing separator too (it is part of the emitted Text):
        //    estimate the block WITH its separator, mirroring the flat path, so `used` never under-counts.
        var used = 0;
        if (!string.IsNullOrWhiteSpace(bookBriefText))
        {
            var block = bookBriefText + BlockSeparator;
            sb.Append(block);
            used += EstimateTokens(block);
        }

        if (used > budget)
        {
            // Pathological: the BookBrief alone exceeds the budget. We still include it (the BookBrief is the
            // single most valuable block and must always be present), but log it so the overflow is visible.
            _logger.LogWarning(
                "BookContextAssembler: BookBrief for book {BookId} ({Tokens} tokens) alone exceeds the " +
                "context budget ({Budget} tokens); including it anyway. ALL {Count} chapters dropped.",
                bookId, used, budget, chapters.Count);
        }

        // 2. Walk EVERY chapter in narrative order. Each contributes its dense brief if fresh, else a flat/raw
        //    block. Once the budget is hit we drop the rest to keep a contiguous narrative prefix; every drop
        //    is recorded (no silent truncation).
        var included = new List<ChapterBrief>();
        var dropped = new List<DroppedBookUnit>();
        var flatFilledCount = 0;
        var budgetExhausted = used > budget;

        foreach (var chapter in chapters)
        {
            var title = chapter.Title ?? string.Empty;

            // Best available representation for this chapter: fresh structured brief > flat summary > raw text.
            string block;
            ChapterBrief? structuredBrief = null;
            if (freshBriefByOrder.TryGetValue(chapter.Order, out var brief))
            {
                structuredBrief = brief;
                block = FormatChapterBrief(brief) + BlockSeparator;
            }
            else
            {
                var body = flatByChapterId.TryGetValue(chapter.Id, out var summary) && !string.IsNullOrWhiteSpace(summary)
                    ? summary
                    : SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
                if (string.IsNullOrWhiteSpace(body))
                    continue; // genuinely empty chapter: nothing to include and nothing to truncate
                block = FormatFlatChapterBlock(title, body);
            }

            var blockTokens = EstimateTokens(block);
            if (budgetExhausted || used + blockTokens > budget)
            {
                budgetExhausted = true; // once we drop one, drop the rest to keep a contiguous prefix
                dropped.Add(new DroppedBookUnit { Order = chapter.Order, Title = title, EstimatedTokens = blockTokens });
                continue;
            }

            sb.Append(block);
            used += blockTokens;
            if (structuredBrief != null)
                included.Add(structuredBrief);
            else
                flatFilledCount++;
        }

        // Partial coverage is a degraded (but complete) assembly: surface it so it is never silent.
        if (flatFilledCount > 0)
            _logger.LogInformation(
                "BookContextAssembler: book {BookId} has partial structured coverage — {Structured} chapter(s) " +
                "from structured briefs + {Flat} chapter(s) back-filled from flat/raw text.",
                bookId, included.Count, flatFilledCount);

        if (dropped.Count > 0)
            // No silent truncation: log which units and how many did not fit.
            _logger.LogWarning(
                "BookContextAssembler: book {BookId} context budget ({Budget} tokens) reached. Included " +
                "{IncludedCount}/{TotalCount} chapters ({Structured} structured, {Flat} flat); DROPPED " +
                "{DroppedCount}: {DroppedTitles}.",
                bookId, budget, included.Count + flatFilledCount, chapters.Count, included.Count, flatFilledCount,
                dropped.Count, string.Join(", ", dropped.Select(d => $"#{d.Order} '{d.Title}'")));

        return new BookContextAssembly
        {
            Text = sb.ToString().TrimEnd(),
            BookBrief = bookBrief,
            IncludedChapterBriefs = included,
            DroppedUnits = dropped,
            UsedStructuredBriefs = true,
            BudgetTokens = budget,
            EstimatedTokens = used
        };
    }

    // ─── Flat / raw fallback path (still budget-guarded) ──────────────────────────────────────────────

    private async Task<BookContextAssembly> AssembleFlatFallbackAsync(
        Guid bookId,
        string language,
        int budget,
        CancellationToken ct)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        // Walk the FULL chapter set in narrative order so NO chapter is silently omitted: each chapter is
        // represented by its flat summary if present, else its raw text. The prior version built the unit
        // list ONLY from chapters that already had a non-blank flat summary, and fell back to raw text for
        // the WHOLE book only when there were ZERO summaries — so under PARTIAL flat coverage (some chapters
        // with a summary, others with a blank/empty summary, only a stale structured brief, or no row at all)
        // the uncovered chapters were dropped from the context AND from DroppedUnits. Carry raw ContentText
        // for the per-chapter last-resort fill.
        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => new { c.Id, c.Order, c.Title, c.ContentText })
            .ToListAsync(ct);

        // Flat summaries for (book, language), keyed by ChapterId. Matched by NORMALIZED locale so a legacy
        // summary stored under the raw request value (e.g. "en-US") is still found, while a genuinely
        // different language (normalizing differently) is excluded — no cross-language leak into this context.
        var flatByChapterId = await LoadFlatSummariesByNormalizedLanguageAsync(bookId, lang, ct);

        var sb = new StringBuilder();
        var included = new List<ChapterBrief>(); // flat path carries no structured briefs
        var dropped = new List<DroppedBookUnit>();
        var used = 0;
        var budgetExhausted = false;
        var flatCount = 0;
        var rawCount = 0;

        foreach (var chapter in chapters)
        {
            var title = chapter.Title ?? string.Empty;

            // Best available representation for this chapter: flat summary > raw text.
            bool fromFlat;
            string body;
            if (flatByChapterId.TryGetValue(chapter.Id, out var summary) && !string.IsNullOrWhiteSpace(summary))
            {
                body = summary;
                fromFlat = true;
            }
            else
            {
                body = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
                fromFlat = false;
            }

            if (string.IsNullOrWhiteSpace(body))
                continue; // genuinely empty chapter: nothing to include and nothing to truncate

            var block = FormatFlatChapterBlock(title, body);
            var blockTokens = EstimateTokens(block);

            if (budgetExhausted || used + blockTokens > budget)
            {
                budgetExhausted = true;
                dropped.Add(new DroppedBookUnit { Order = chapter.Order, Title = title, EstimatedTokens = blockTokens });
                continue;
            }

            sb.Append(block);
            used += blockTokens;
            if (fromFlat) flatCount++; else rawCount++;
        }

        if (dropped.Count > 0)
        {
            _logger.LogWarning(
                "BookContextAssembler (flat fallback): book {BookId} context budget ({Budget} tokens) reached. " +
                "Included {IncludedCount}/{TotalCount} chapters ({Flat} flat, {Raw} raw); DROPPED {DroppedCount}: {DroppedTitles}.",
                bookId, budget, flatCount + rawCount, chapters.Count, flatCount, rawCount,
                dropped.Count, string.Join(", ", dropped.Select(d => $"#{d.Order} '{d.Title}'")));
        }

        return new BookContextAssembly
        {
            Text = sb.ToString().TrimEnd(),
            BookBrief = null,
            IncludedChapterBriefs = included,
            DroppedUnits = dropped,
            UsedStructuredBriefs = false,
            BudgetTokens = budget,
            EstimatedTokens = used
        };
    }

    // ─── Rendering (mirrors PromptFactory's brief formatting so the model reads a familiar shape) ──────

    /// <summary>
    /// Flat (degraded) per-chapter block: the "## פרק / Chapter: {title}\n{body}\n\n" framing used by the
    /// flat fallback, shared so a chapter back-filled from its summary/raw text in the structured path reads
    /// identically. The trailing separator is part of the block (and is charged like every other block).
    /// </summary>
    private static string FormatFlatChapterBlock(string title, string body) =>
        $"## פרק / Chapter: {title}\n{body}{BlockSeparator}";

    private static string FormatBookBrief(BookBrief b)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[BOOK_CONTEXT]");
        if (b.Genre != null) sb.AppendLine($"Genre: {b.Genre}{(b.SubGenre != null ? $" / {b.SubGenre}" : "")}");
        if (b.TargetAudience != null) sb.AppendLine($"Audience: {b.TargetAudience}");
        if (b.LiteratureLevel.HasValue) sb.AppendLine($"Literature level: {b.LiteratureLevel}/10");
        if (b.Themes.Count > 0) sb.AppendLine($"Themes: {string.Join(", ", b.Themes)}");
        if (b.Synopsis != null) sb.AppendLine($"Synopsis: {b.Synopsis}");
        sb.Append("[/BOOK_CONTEXT]");
        return sb.ToString();
    }

    private static string FormatChapterBrief(ChapterBrief ch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Chapter {ch.Order}: {ch.Title}");
        if (!string.IsNullOrWhiteSpace(ch.Summary)) sb.AppendLine(ch.Summary);
        if (ch.PlotEvents.Count > 0) sb.AppendLine($"Plot events: {string.Join("; ", ch.PlotEvents)}");
        if (ch.CharacterStates.Count > 0)
        {
            foreach (var cs in ch.CharacterStates)
            {
                sb.Append($"  {cs.Name}");
                if (cs.State != null) sb.Append($" - {cs.State}");
                if (cs.EmotionalArc != null) sb.Append($" ({cs.EmotionalArc})");
                sb.AppendLine();
            }
        }
        if (ch.ThematicMarkers.Count > 0) sb.AppendLine($"Themes: {string.Join(", ", ch.ThematicMarkers)}");
        if (ch.OpenThreads.Count > 0) sb.AppendLine($"Open threads: {string.Join("; ", ch.OpenThreads)}");
        if (!string.IsNullOrWhiteSpace(ch.ToneNotes)) sb.AppendLine($"Tone: {ch.ToneNotes}");
        return sb.ToString().TrimEnd();
    }
}
