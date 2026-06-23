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
    /// Assembles the budgeted whole-book context for (bookId, language). Always tries the dense structured
    /// brief path first (BookBrief + ordered ChapterBriefs); degrades to a budget-guarded flat-summary (and
    /// finally raw-text) concat when no structured briefs exist. Never overflows the resolved budget beyond
    /// the unavoidable BookBrief-alone case, which is logged.
    /// </summary>
    public async Task<BookContextAssembly> AssembleAsync(
        Guid bookId,
        string language,
        IReadOnlyCollection<AiTaskType>? consumingTasks = null,
        CancellationToken ct = default)
    {
        var budget = ResolveBudgetTokens(consumingTasks);

        // Prefer the dense structured path: L2 BookBrief + ordered L1 ChapterBriefs.
        var chapterBriefs = await _bookSummary.ComposeChapterBriefsAsync(bookId, language, ct);
        if (chapterBriefs.Count > 0)
        {
            var bookBrief = await _bookSummary.ComposeBookBriefAsync(bookId, chapterBriefs, ct);
            return AssembleFromBriefs(bookId, bookBrief, chapterBriefs, budget);
        }

        // No usable structured briefs yet → degrade to the flat-summary fallback, still budget-guarded.
        _logger.LogInformation(
            "BookContextAssembler: no structured chapter briefs for book {BookId} ({Lang}); " +
            "falling back to budget-guarded flat summaries.", bookId, language);
        return await AssembleFlatFallbackAsync(bookId, language, budget, ct);
    }

    // ─── Structured path ────────────────────────────────────────────────────────────────────────────

    private BookContextAssembly AssembleFromBriefs(
        Guid bookId,
        BookBrief bookBrief,
        IReadOnlyList<ChapterBrief> chapterBriefs,
        int budget)
    {
        var sb = new StringBuilder();
        var bookBriefText = FormatBookBrief(bookBrief);

        // 1. BookBrief ALWAYS first.
        var used = 0;
        if (!string.IsNullOrWhiteSpace(bookBriefText))
        {
            sb.Append(bookBriefText).Append("\n\n");
            used += EstimateTokens(bookBriefText);
        }

        if (used > budget)
        {
            // Pathological: the BookBrief alone exceeds the budget. We still include it (the BookBrief is the
            // single most valuable block and must always be present), but log it so the overflow is visible.
            _logger.LogWarning(
                "BookContextAssembler: BookBrief for book {BookId} ({Tokens} tokens) alone exceeds the " +
                "context budget ({Budget} tokens); including it anyway. ALL {Count} chapter briefs dropped.",
                bookId, used, budget, chapterBriefs.Count);
        }

        // 2. Add ChapterBriefs in narrative order until the budget is hit. A brief that does not fit is
        //    dropped (and every later one too, since order matters) and recorded.
        var included = new List<ChapterBrief>();
        var dropped = new List<DroppedBookUnit>();
        // Narrative order: project assumes Order ascending; sort defensively in case the caller's list is not.
        var ordered = chapterBriefs.OrderBy(b => b.Order).ToList();
        var budgetExhausted = used > budget;

        foreach (var brief in ordered)
        {
            var briefText = FormatChapterBrief(brief);
            var briefTokens = EstimateTokens(briefText);

            if (budgetExhausted || used + briefTokens > budget)
            {
                budgetExhausted = true; // once we drop one, drop the rest to keep a contiguous prefix
                dropped.Add(new DroppedBookUnit { Order = brief.Order, Title = brief.Title, EstimatedTokens = briefTokens });
                continue;
            }

            sb.Append(briefText).Append("\n\n");
            used += briefTokens;
            included.Add(brief);
        }

        if (dropped.Count > 0)
        {
            // No silent truncation: log which units and how many did not fit.
            _logger.LogWarning(
                "BookContextAssembler: book {BookId} context budget ({Budget} tokens) reached. Included " +
                "{IncludedCount}/{TotalCount} chapter briefs; DROPPED {DroppedCount}: {DroppedTitles}.",
                bookId, budget, included.Count, ordered.Count, dropped.Count,
                string.Join(", ", dropped.Select(d => $"#{d.Order} '{d.Title}'")));
        }

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
        // Flat per-chapter summaries (the legacy GetConcatenatedSummaries source), in narrative order.
        // Filter by the requested language exactly as the structured path does (ComposeChapterBriefsAsync):
        // a ChunkSummary row for ANOTHER language must NOT be concatenated into this locale's book context,
        // or a book with summaries only in a different language would inject foreign-language prose here.
        var lang = BaselineLanguageResolver.Normalize(language);
        var summaries = await _db.ChunkSummaries
            .AsNoTracking()
            .Where(cs => cs.BookId == bookId && cs.Language == lang)
            .Join(_db.Chapters, cs => cs.ChapterId, c => c.Id,
                (cs, c) => new { c.Order, c.Title, cs.SummaryText })
            .OrderBy(x => x.Order)
            .ToListAsync(ct);

        var units = summaries
            .Where(s => !string.IsNullOrWhiteSpace(s.SummaryText))
            .Select(s => (s.Order, Title: s.Title ?? string.Empty, Body: s.SummaryText))
            .ToList();

        var usedRawText = false;
        if (units.Count == 0)
        {
            // No flat summaries either → last-resort raw chapter text, still budget-trimmed so the book
            // analysis has SOMETHING rather than nothing (and still cannot overflow).
            usedRawText = true;
            var chapters = await _db.Chapters
                .AsNoTracking()
                .Where(c => c.BookId == bookId)
                .OrderBy(c => c.Order)
                .Select(c => new { c.Order, c.Title, c.ContentText })
                .ToListAsync(ct);

            units = chapters
                .Select(c => (c.Order, Title: c.Title ?? string.Empty,
                    Body: SyncfusionWatermarkStripper.StripSyncfusionWatermark(c.ContentText ?? "")))
                .Where(c => !string.IsNullOrWhiteSpace(c.Body))
                .ToList();
        }

        var sb = new StringBuilder();
        var included = new List<ChapterBrief>(); // flat path carries no structured briefs
        var dropped = new List<DroppedBookUnit>();
        var used = 0;
        var budgetExhausted = false;
        var includedCount = 0;

        foreach (var (order, title, body) in units)
        {
            var header = $"## פרק / Chapter: {title}";
            var block = $"{header}\n{body}\n\n";
            var blockTokens = EstimateTokens(block);

            if (budgetExhausted || used + blockTokens > budget)
            {
                budgetExhausted = true;
                dropped.Add(new DroppedBookUnit { Order = order, Title = title, EstimatedTokens = blockTokens });
                continue;
            }

            sb.Append(block);
            used += blockTokens;
            includedCount++;
        }

        if (dropped.Count > 0)
        {
            _logger.LogWarning(
                "BookContextAssembler (flat fallback): book {BookId} context budget ({Budget} tokens) " +
                "reached. Included {IncludedCount}/{TotalCount} {UnitKind}; DROPPED {DroppedCount}: {DroppedTitles}.",
                bookId, budget, includedCount, units.Count, usedRawText ? "raw chapter texts" : "flat summaries",
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
