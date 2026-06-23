using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
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
        ILogger<BookIntelligenceService> logger)
    {
        _db = db;
        _analysis = analysis;
        _bookContextAssembler = bookContextAssembler;
        _logger = logger;
    }

    // ─── Chapter Summarization ──────────────────────────────────────

    /// <summary>
    /// Summarize all chapters of the book. Skips chapters that already have
    /// a fresh (non-stale) ChunkSummary.
    /// </summary>
    public async Task SummarizeChaptersAsync(Guid bookId, string language, CancellationToken ct = default)
    {
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

            _logger.LogInformation("Summarizing chapter {Title} ({Id})", chapter.Title, chapter.Id);
            var summaryText = await _analysis.RunRawAsync(
                text, AnalysisType.Summarization, null, language, ct);

            if (existing != null)
            {
                existing.SummaryText = summaryText;
                existing.Language = language;
                existing.CreatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _db.Set<ChunkSummary>().Add(new ChunkSummary
                {
                    BookId = bookId,
                    ChapterId = chapter.Id,
                    SummaryText = summaryText,
                    Language = language
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
        var assembly = await _bookContextAssembler.AssembleAsync(bookId, language, ct);
        var concatenated = assembly.Text;
        if (string.IsNullOrWhiteSpace(concatenated))
            throw new InvalidOperationException("No chapter summaries found. Run SummarizeChaptersAsync first.");

        _logger.LogInformation(
            "Building book profile for {BookId} (structuredBriefs={Used}, dropped={Dropped}/{Budget}t)",
            bookId, assembly.UsedStructuredBriefs, assembly.DroppedCount, assembly.BudgetTokens);

        var overviewTask = _analysis.RunRawAsync(concatenated, AnalysisType.BookOverview, null, language, ct);
        var synopsisTask = _analysis.RunRawAsync(concatenated, AnalysisType.Synopsis, null, language, ct);
        var charsTask = _analysis.RunRawAsync(concatenated, AnalysisType.CharacterAnalysis, null, language, ct);
        var storyTask = _analysis.RunRawAsync(concatenated, AnalysisType.StoryAnalysis, null, language, ct);

        await Task.WhenAll(overviewTask, synopsisTask, charsTask, storyTask);

        var overview = TryDeserialize<BookOverviewResult>(overviewTask.Result);
        var profile = await GetOrCreateProfile(bookId, ct);

        profile.Genre = overview?.Genre;
        profile.SubGenre = overview?.SubGenre;
        profile.TargetAudience = overview?.TargetAudience;
        profile.LiteratureLevel = overview?.LiteratureLevel;
        profile.LanguageRegister = overview?.LanguageRegister;
        profile.Synopsis = synopsisTask.Result;
        profile.CharactersJson = charsTask.Result;
        profile.StoryStructureJson = storyTask.Result;
        profile.Language = language;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
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
        var assembly = await _bookContextAssembler.AssembleAsync(bookId, language, ct);
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
