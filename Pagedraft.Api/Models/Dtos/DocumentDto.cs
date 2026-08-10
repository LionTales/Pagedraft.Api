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
/// The values are <see cref="NoChapters"/> and <see cref="NothingWritten"/>; new reasons are added as new
/// tokens rather than by reusing an existing one with different prose. A client that meets a token it does
/// not know must say something truthful about not knowing, never guess at one it does.
/// </param>
public record ExportUnavailableDto(string Reason)
{
    /// <summary>The book has no chapters yet, so stage 1 has not happened and stage 5 cannot.</summary>
    public const string NoChapters = "noChapters";

    /// <summary>
    /// There is something to export in principle - the book has chapters, or the requested chapter exists -
    /// but nothing in it has been written yet, so the assembled document would be blank. Distinct from
    /// <see cref="NoChapters"/> because the author's next action is different: write, not import.
    ///
    /// Answered by BOTH export paths: the whole book when every one of its chapters is unwritten, and a single
    /// chapter when that chapter is. It replaces a 200 carrying a valid, correctly-named, empty .docx.
    /// </summary>
    public const string NothingWritten = "nothingWritten";
}
