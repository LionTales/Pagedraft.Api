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
