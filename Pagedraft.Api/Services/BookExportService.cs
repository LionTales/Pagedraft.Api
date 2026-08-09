using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;

namespace Pagedraft.Api.Services;

/// <summary>Why an export could not be produced, or that it was.</summary>
public enum BookExportOutcome
{
    /// <summary>A DOCX was produced. <see cref="BookExportResult.Content"/> and <see cref="BookExportResult.FileName"/> are set.</summary>
    Ok,

    /// <summary>No such book. The caller turns this into a 404.</summary>
    BookNotFound,

    /// <summary>No such chapter in that book. The caller turns this into a 404.</summary>
    ChapterNotFound,

    /// <summary>
    /// The book exists but has no chapters, so there is no manuscript to assemble. Deliberately NOT an empty
    /// DOCX: handing the author a valid, empty, correctly-named file in answer to "export my book" is the
    /// same class of dishonesty as a stage that reports done without computing anything.
    /// </summary>
    NothingToExport
}

/// <summary>The assembled document, or the reason there isn't one.</summary>
public sealed record BookExportResult(BookExportOutcome Outcome, byte[]? Content, string? FileName)
{
    public static BookExportResult Ok(byte[] content, string fileName) => new(BookExportOutcome.Ok, content, fileName);
    public static BookExportResult BookNotFound() => new(BookExportOutcome.BookNotFound, null, null);
    public static BookExportResult ChapterNotFound() => new(BookExportOutcome.ChapterNotFound, null, null);
    public static BookExportResult NothingToExport() => new(BookExportOutcome.NothingToExport, null, null);
}

/// <summary>
/// Stage 5 of the reconciled stage model: turning saved SFDT back into a .docx the author can download,
/// book-wide or one chapter at a time.
///
/// Extracted from <see cref="Pagedraft.Api.Controllers.DocumentController"/> in Wave 3 / w1 so that the two
/// export paths are one seam with one filename rule and one error vocabulary. THE TWO PATHS DRIFT: the
/// standing architecture note about this codebase is that book-level export and single-chapter export are
/// separate document paths that historically diverge, and the export screen w4 builds exposes BOTH, so they
/// need a shared place to be tested against each other rather than two inline controller bodies.
///
/// Deliberately synchronous and unmetered: assembling saved SFDT is CPU work in-process, spends no model
/// calls, and needs no job/progress contract. If a very long book ever makes that untrue, the fix is a job
/// registry like the book-level builds have, not a background thread here.
/// </summary>
public class BookExportService
{
    /// <summary>The DOCX media type, spelled once. Both endpoints and their tests read it from here.</summary>
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>
    /// Cap on the title-derived part of a download filename. Long enough for any real book or chapter title,
    /// short enough that the Content-Disposition header stays comfortably inside what proxies and browsers
    /// handle - a Hebrew title percent-encodes to roughly six bytes per character in <c>filename*</c>.
    /// </summary>
    private const int MaxFileNameStemLength = 80;

    private readonly AppDbContext _db;
    private readonly ChapterService _chapters;
    private readonly SfdtConversionService _sfdtConversion;
    private readonly BookAssemblyService _bookAssembly;

    public BookExportService(
        AppDbContext db,
        ChapterService chapters,
        SfdtConversionService sfdtConversion,
        BookAssemblyService bookAssembly)
    {
        _db = db;
        _chapters = chapters;
        _sfdtConversion = sfdtConversion;
        _bookAssembly = bookAssembly;
    }

    /// <summary>
    /// The whole book as one DOCX, chapters in <c>Order</c>, named after the book.
    ///
    /// The book row is looked up FIRST, which it never used to be. Before w1 an unknown bookId fell straight
    /// through to the chapter query, found nothing, and reached the assembler's zero-buffer path - which threw
    /// (see <see cref="BookAssemblyService.AssembleDocx"/>), so "export a book that does not exist" and
    /// "export a book you have not imported yet" both answered 500 with no way for a client to tell which had
    /// happened, or that either was its own fault. They are now 404 and 409 with a reason.
    ///
    /// The filename was also a single hard-coded constant, so every book on the installation downloaded as
    /// "book.docx" and exporting three of them left the author with three identical names in one folder.
    /// </summary>
    public async Task<BookExportResult> ExportBookAsync(Guid bookId, CancellationToken ct = default)
    {
        var title = await _db.Books
            .AsNoTracking()
            .Where(b => b.Id == bookId)
            .Select(b => b.Title)
            .FirstOrDefaultAsync(ct);

        if (title == null) return BookExportResult.BookNotFound();

        var chapters = await _chapters.GetAllByBookAsync(bookId, ct);
        if (chapters.Count == 0) return BookExportResult.NothingToExport();

        var buffers = new List<byte[]>(chapters.Count);
        foreach (var chapter in chapters)
        {
            if (!HasRenderableContent(chapter.ContentSfdt)) continue;
            buffers.Add(_sfdtConversion.ConvertSfdtToDocx(chapter.ContentSfdt));
        }

        var docx = _bookAssembly.AssembleDocx(buffers);
        return BookExportResult.Ok(docx, BuildFileName(title, fallbackStem: "book"));
    }

    /// <summary>
    /// One chapter as a DOCX, named after the chapter. The chapter lookup is already book-scoped, so an
    /// unknown book and an unknown chapter both land on <see cref="BookExportOutcome.ChapterNotFound"/>.
    /// </summary>
    public async Task<BookExportResult> ExportChapterAsync(Guid bookId, Guid chapterId, CancellationToken ct = default)
    {
        var chapter = await _chapters.GetByIdAsync(bookId, chapterId, ct);
        if (chapter == null) return BookExportResult.ChapterNotFound();

        var docx = HasRenderableContent(chapter.ContentSfdt)
            ? _sfdtConversion.ConvertSfdtToDocx(chapter.ContentSfdt)
            // An untouched chapter has nothing to convert; the assembler's zero-buffer path is the one
            // existing producer of a valid empty DOCX, so it is reused rather than hand-rolling a second.
            : _bookAssembly.AssembleDocx(Array.Empty<byte[]>());

        return BookExportResult.Ok(docx, BuildFileName(chapter.Title, fallbackStem: "chapter"));
    }

    /// <summary>
    /// Whether a chapter's stored SFDT is something the converter can turn into a document at all.
    ///
    /// <c>Chapter.ContentSfdt</c> DEFAULTS to the literal <c>"{}"</c> - what a chapter created through
    /// <c>POST /chapters</c> carries until the editor first saves it. Syncfusion cannot load that (it throws
    /// "There are no sections present in the document"), so before this guard ONE untouched chapter turned
    /// the whole book's export into a 500: a book-level failure caused by a chapter the author had not
    /// started writing. On the book path such a chapter is simply skipped, which is also what it contributes
    /// to the manuscript: nothing.
    ///
    /// Deliberately NARROW - blank and the empty-object default only. Any other stored value is passed to the
    /// converter and allowed to throw, because a genuinely corrupt chapter is a fault worth surfacing, not
    /// one worth silently dropping from the author's exported book.
    /// </summary>
    internal static bool HasRenderableContent(string? contentSfdt)
    {
        if (string.IsNullOrWhiteSpace(contentSfdt)) return false;
        return contentSfdt.Trim() != "{}";
    }

    /// <summary>
    /// A download filename from an author-supplied title: <c>{sanitized title}.docx</c>, falling back to
    /// <paramref name="fallbackStem"/> when the title is blank or consists entirely of characters a filename
    /// cannot carry.
    ///
    /// Hebrew is preserved, which is the point - the primary language is Hebrew and stripping to ASCII would
    /// hand every Hebrew-titled book the fallback name. ASP.NET Core's <c>File(..., fileName)</c> emits both a
    /// <c>filename</c> (ASCII approximation) and a <c>filename*=UTF-8''...</c> parameter, and every browser
    /// this product targets reads the latter.
    /// </summary>
    internal static string BuildFileName(string? title, string fallbackStem)
    {
        var stem = SanitizeFileNameStem(title);
        if (stem.Length == 0) stem = fallbackStem;
        return stem + ".docx";
    }

    /// <summary>
    /// Strips what a filename may not carry - the platform's invalid characters plus control characters -
    /// collapses runs of whitespace, trims, and caps the length. Returns an empty string when nothing usable
    /// survives, which is the caller's cue to use its fallback.
    ///
    /// Trailing dots and spaces are trimmed too: Windows silently drops them, so "Chapter 1." would be saved
    /// as a different name than the one the header claimed.
    /// </summary>
    internal static string SanitizeFileNameStem(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(title.Length);
        var pendingSpace = false;

        foreach (var ch in title)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0) continue;

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
            if (builder.Length >= MaxFileNameStemLength) break;
        }

        return builder.ToString().TrimEnd(' ', '.');
    }
}
