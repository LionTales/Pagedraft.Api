using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
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

    private const int CharacterPrepassMaxWords = 2000;

    private readonly AppDbContext _db;
    private readonly SfdtConversionService _sfdtConversion;
    private readonly IAiRouter _router;
    private readonly PromptFactory _promptFactory;

    public AnalysisContextService(
        AppDbContext db,
        SfdtConversionService sfdtConversion,
        IAiRouter router,
        PromptFactory promptFactory)
    {
        _db = db;
        _sfdtConversion = sfdtConversion;
        _router = router;
        _promptFactory = promptFactory;
    }

    public async Task<AnalysisContext> BuildContextAsync(
        AnalysisScope scope,
        Guid targetId,
        AnalysisType analysisType,
        CancellationToken ct = default)
    {
        var (text, bookId, chapterId, sceneId) = scope switch
        {
            AnalysisScope.Chapter => await ResolveChapterAsync(targetId, ct),
            AnalysisScope.Scene   => await ResolveSceneAsync(targetId, ct),
            AnalysisScope.Book    => await ResolveBookAsync(targetId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported analysis scope")
        };

        CharacterRegister? characters = null;
        if (bookId.HasValue && analysisType is AnalysisType.Proofread
            or AnalysisType.LiteraryAnalysis
            or AnalysisType.QA
            or AnalysisType.Synopsis)
        {
            characters = await LoadCharacterRegisterAsync(bookId.Value, text, ct);
        }

        return new AnalysisContext
        {
            TargetText = text,

            // Optional context fields – populated in later plans
            PrecedingContext = null,
            FollowingContext = null,
            Characters = characters,
            StyleProfile = null,
            ChapterBrief = null,
            BookBrief = null,

            Scope = scope,
            AnalysisType = analysisType,
            BookId = bookId,
            ChapterId = chapterId,
            SceneId = sceneId
        };
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
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.BookId == bookId, ct);

            if (!string.IsNullOrWhiteSpace(bible?.CharacterRegisterJson))
            {
                var fromBible = JsonSerializer.Deserialize<CharacterRegister>(
                    bible.CharacterRegisterJson,
                    JsonOpts);
                if (fromBible is { Characters.Count: > 0 })
                    return fromBible;
            }

            // 2. Fallback: cheap LLM pre-pass on first ~2000 words
            var book = await _db.Books
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == bookId, ct);
            if (book == null) return null;

            var language = string.IsNullOrWhiteSpace(book.Language) ? "he" : book.Language;
            return await ExtractCharacterRegisterAsync(fullText, language, ct);
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

