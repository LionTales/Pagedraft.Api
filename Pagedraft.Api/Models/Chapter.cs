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
/// whether Syncfusion can render the chapter's SFDT for EXPORT. The spine's "has a manuscript" and the
/// export's "can this be rendered" disagree on purpose (an image-only chapter has text-less WordCount but is
/// still exportable) - do not unify them.
///
/// An <c>Expression</c>, not a compiled <c>Func</c>, so EF Core can translate it into SQL at both call sites
/// rather than pulling chapter rows into memory to evaluate it client-side.
/// </summary>
public static class ChapterTextPredicate
{
    public static readonly Expression<Func<Chapter, bool>> HasText = c => c.WordCount > 0;
}
