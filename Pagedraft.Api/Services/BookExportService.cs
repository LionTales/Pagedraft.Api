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

    /// <summary>
    /// The characters a download filename may not carry, spelled out EXPLICITLY rather than read from
    /// <see cref="Path.GetInvalidFileNameChars"/>.
    ///
    /// The contract is "a name the CLIENT can write to disk", not "a name this host can". The name travels in
    /// a Content-Disposition header to a browser that saves it on the reader's machine, and that machine has
    /// nothing to do with the machine that produced it. <c>Path.GetInvalidFileNameChars()</c> answers the
    /// other question: it returns 40+ characters on Windows and only <c>{'\0', '/'}</c> on Unix, so a
    /// Linux-hosted API would pass <c>\ : * ? " &lt; &gt; |</c> straight through to a Windows reader, and the
    /// same book title would download under two different names depending on which host happened to answer.
    /// The w1 test for this asserted the Windows result and went red on the Linux CI runner - the same defect
    /// seen from the other side.
    ///
    /// The set is the UNION of what Windows forbids (these nine) and what Unix and macOS forbid (<c>/</c> and
    /// NUL, both already covered). NUL and the other control characters are rejected separately by
    /// <see cref="char.IsControl(char)"/>, which is wider than any platform's list. The double quote earns its
    /// place twice: it also terminates the <c>filename</c> parameter of the header that carries it.
    /// </summary>
    private const string InvalidFileNameChars = "<>:\"/\\|?*";

    /// <summary>
    /// Names Windows reserves for devices. <c>NUL.docx</c> is not a file on Windows, it is the null device, so
    /// a chapter titled "Nul" would produce a download that silently goes nowhere. The extension does not
    /// exempt it - the reservation applies to the stem before the FIRST dot - and the match is
    /// case-insensitive, so a title of "con", "Con" or "CON.v2" is the same reserved name.
    ///
    /// Listed explicitly for the same reason as <see cref="InvalidFileNameChars"/>: this is a property of the
    /// reader's machine, and nothing on a Linux host would tell us about it.
    /// </summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

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
    /// <paramref name="fallbackStem"/> when the title is blank, consists entirely of characters a filename
    /// cannot carry, or would land on a Windows device name.
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
    /// Strips what a filename may not carry - <see cref="InvalidFileNameChars"/> plus control characters -
    /// collapses runs of whitespace, trims, and caps the length. Returns an empty string when nothing usable
    /// survives, which is the caller's cue to use its fallback.
    ///
    /// The result is IDENTICAL on every host, by construction: nothing here asks the operating system what it
    /// thinks a filename is. The question being answered is what the reader's browser can save, and the API
    /// host is not the reader's machine (see <see cref="InvalidFileNameChars"/>).
    ///
    /// Trailing dots and spaces are trimmed too: Windows silently drops them, so "Chapter 1." would be saved
    /// as a different name than the one the header claimed. A name that would land on a Windows device
    /// (<see cref="ReservedDeviceNames"/>) is refused outright rather than mangled, because the fallback name
    /// is honest while "NUL_.docx" would be a name the author never chose.
    /// </summary>
    internal static string SanitizeFileNameStem(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var builder = new System.Text.StringBuilder(title.Length);
        var pendingSpace = false;

        for (var i = 0; i < title.Length; i++)
        {
            var ch = title[i];
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (char.IsControl(ch) || InvalidFileNameChars.IndexOf(ch) >= 0) continue;

            // An emoji or a rare CJK glyph is ONE character to the author and TWO UTF-16 code units here. The
            // pair is appended and counted as a unit so the cap can never cut between its halves - a lone
            // surrogate is not text, and encoding one into the header's filename* parameter yields a
            // replacement character in the name the reader sees. A surrogate with no partner is dropped for
            // the same reason.
            var isPair = char.IsHighSurrogate(ch) && i + 1 < title.Length && char.IsLowSurrogate(title[i + 1]);
            if (char.IsSurrogate(ch) && !isPair) continue;

            var separator = pendingSpace ? 1 : 0;
            if (builder.Length + separator + (isPair ? 2 : 1) > MaxFileNameStemLength) break;

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
            if (isPair) builder.Append(title[++i]);
        }

        var stem = builder.ToString().TrimEnd(' ', '.');
        return IsReservedDeviceName(stem) ? string.Empty : stem;
    }

    /// <summary>
    /// Whether a sanitized stem would resolve to a Windows device. The reservation is on the segment before
    /// the first dot, so "CON", "con.docx" and "Com1.v2" are all the same reserved name.
    /// </summary>
    private static bool IsReservedDeviceName(string stem)
    {
        if (stem.Length == 0) return false;

        var dot = stem.IndexOf('.');
        var baseName = (dot < 0 ? stem : stem[..dot]).TrimEnd(' ');
        return ReservedDeviceNames.Contains(baseName);
    }
}
