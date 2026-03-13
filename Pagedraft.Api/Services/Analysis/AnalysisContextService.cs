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
    private const int ContextEnvelopeMaxWords = 300;

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

        string? precedingContext = null;
        string? followingContext = null;
        if (analysisType is AnalysisType.LineEdit)
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
            or AnalysisType.LiteraryAnalysis)
        {
            styleProfile = await LoadStyleProfileAsync(bookId.Value, ct);
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
            preceding = await ExtractSceneTailAsync(previousScene, ct);
        }
        else
        {
            // First scene in chapter – use chapter opening paragraph as preceding context.
            var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == effectiveChapterId, ct);
            if (chapter != null)
            {
                var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
                var paragraphs = SplitIntoParagraphs(text);
                preceding = paragraphs.FirstOrDefault();
            }
        }

        if (nextScene != null)
        {
            following = await ExtractSceneHeadAsync(nextScene, ct);
        }
        else
        {
            // Last scene in chapter – use chapter closing paragraph as following context.
            var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == effectiveChapterId, ct);
            if (chapter != null)
            {
                var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
                var paragraphs = SplitIntoParagraphs(text);
                following = paragraphs.LastOrDefault();
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
            preceding = paragraphs.LastOrDefault();
        }

        if (nextChapter != null)
        {
            var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(nextChapter.ContentText ?? "");
            var paragraphs = SplitIntoParagraphs(text);
            following = paragraphs.FirstOrDefault();
        }

        return (string.IsNullOrWhiteSpace(preceding) ? null : preceding,
            string.IsNullOrWhiteSpace(following) ? null : following);
    }

    private async Task<string?> ExtractSceneHeadAsync(Scene scene, CancellationToken ct)
    {
        var sfdt = scene.ContentSfdt ?? "{}";
        var (plainText, _) = _sfdtConversion.GetTextFromSfdt(sfdt);
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(plainText);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return TruncateToWords(text, ContextEnvelopeMaxWords);
    }

    private async Task<string?> ExtractSceneTailAsync(Scene scene, CancellationToken ct)
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

