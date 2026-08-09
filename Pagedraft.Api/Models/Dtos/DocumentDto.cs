namespace Pagedraft.Api.Models.Dtos;

public record ImportResultDto(Guid BookId, int ChaptersCreated, List<ChapterSummaryDto> Chapters);

public record ImportPreviewChapterDto(
    Guid TempId,
    int Order,
    string Title,
    string? PartName,
    int WordCount,
    string Snippet,
    string SfdtJson);

public record ImportPreviewResponseDto(
    Guid BookId,
    string FileName,
    long FileSize,
    int? PageCount,
    List<ImportPreviewChapterDto> Chapters);

public record ImportConfirmationChapterDto(
    Guid TempId,
    string Title,
    string? PartName,
    int Order,
    bool Include,
    string SfdtJson);

public record ImportConfirmationRequest(
    string Mode,
    List<ImportConfirmationChapterDto> Chapters);

public record ImportConfirmationResultDto(
    Guid BookId,
    int ImportedCount,
    int SkippedCount,
    int TotalChapters,
    List<ChapterSummaryDto> Chapters);

/// <summary>
/// Body of the <c>409</c> from an export endpoint: the book exists but there is nothing to assemble.
/// JSON (camelCase): <c>{ "reason": "noChapters" }</c>.
/// </summary>
/// <param name="Reason">
/// A TOKEN, never a sentence. The client renders the Hebrew and English copy for it; a server-side message
/// would ship one language into a bilingual product and would be the wrong place to change the wording.
/// Currently the only value is <see cref="NoChapters"/>; new reasons are added as new tokens rather than by
/// reusing this one with different prose.
/// </param>
public record ExportUnavailableDto(string Reason)
{
    /// <summary>The book has no chapters yet, so stage 1 has not happened and stage 5 cannot.</summary>
    public const string NoChapters = "noChapters";
}
