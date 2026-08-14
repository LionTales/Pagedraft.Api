using System.Linq.Expressions;

namespace Pagedraft.Api.Models;

public class Chapter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public string? PartName { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Order { get; set; }
    public string ContentSfdt { get; set; } = "{}";
    public string ContentText { get; set; } = string.Empty;
    public int WordCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
    public ICollection<Scene> Scenes { get; set; } = new List<Scene>();
    public ICollection<AnalysisResult> AnalysisResults { get; set; } = new List<AnalysisResult>();
}

/// <summary>
/// The ONE definition of "this chapter has text", as opposed to an empty row a hand-add or a title-only
/// import produced (see <see cref="Pagedraft.Api.Models.Dtos.BookDto.ChaptersWithTextCount"/>). Both call
/// sites that count chapters-with-text - <c>BooksController.GetAll</c> and <c>BooksController.Update</c>,
/// via the shared <c>BooksController.WithCounts</c> query - compose this SAME expression instead of
/// re-spelling <c>WordCount &gt; 0</c>, so the two can never quietly drift apart.
///
/// This is deliberately a different question from <c>BookExportService.HasRenderableContent</c>, which asks
/// whether Syncfusion can render the chapter's SFDT for EXPORT. "Has a manuscript" and "can this be rendered"
/// answer differently in BOTH directions (an image-only chapter has a text-less WordCount but is still
/// exportable; a chapter whose scenes were written and then emptied keeps a stale chapter-level word count
/// with nothing renderable left to export), so they are two predicates and must stay two. The DOCX import is
/// NOT an example of the second direction, though it reads like one: <c>ChapterService</c> derives every
/// WordCount it writes FROM the SFDT it is storing, so an imported chapter has both or neither.
///
/// WHAT THAT DOES NOT LICENSE, and what w8 / F2 corrected. Two predicates may not answer ONE claim. The stage
/// spine's Export stage used to say `ready` off this predicate while the export endpoint refused with 409
/// <c>nothingWritten</c> off the other one, on the same book, on the same screen. A surface that speaks for
/// the exporter now asks <c>BookExportService.RenderableUnitsOf</c>, the exporter's own rendering rule, via
/// <c>BookExportService.CountExportableChaptersAsync</c>, carried to the client as
/// <c>BookDetailDto.ExportableChapterCount</c>. This predicate keeps its own question - stage 1's
/// "is there a manuscript here", and the whole-book builders' precondition, which read chapter TEXT.
///
/// An <c>Expression</c>, not a compiled <c>Func</c>, so EF Core can translate it into SQL at both call sites
/// rather than pulling chapter rows into memory to evaluate it client-side.
/// </summary>
public static class ChapterTextPredicate
{
    public static readonly Expression<Func<Chapter, bool>> HasText = c => c.WordCount > 0;
}
