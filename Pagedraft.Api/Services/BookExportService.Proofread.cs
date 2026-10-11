using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Export;

namespace Pagedraft.Api.Services;

/// <summary>
/// What an annotated export wrote: <paramref name="Placed"/> pending suggestions became a Word comment plus
/// tracked change(s); <paramref name="Unanchored"/> could not be placed (no match, ambiguous, crossing a
/// paragraph, or overlapping a placed one). <c>Placed + Unanchored</c> is the number of pending suggestions
/// on the Active Proofread results of the units in the file.
/// </summary>
public sealed record ExportAnnotationCounts(int Placed, int Unanchored);

/// <summary>
/// One unit of an export, with where it came from: the chapter's own SFDT (<paramref name="SceneId"/> null)
/// or one scene's, when the scenes hold the chapter's current text.
/// </summary>
internal sealed record RenderableUnit(string Sfdt, Guid ChapterId, Guid? SceneId);

/// <summary>
/// The proofread-annotated exports (plan whole-book-proofread-and-annotated-docx-2026-10-11, c4): the same
/// document as the plain export, with every PENDING suggestion of each unit's Active Proofread result written
/// in as a Word comment and a tracked change, ready to hand back to the author.
///
/// <para>WHICH RESULT, PER UNIT. The unit rule is the plain export's (<see cref="RenderableUnitSourcesOf"/>),
/// so a unit is either the chapter row, whose suggestions live on the Active Chapter-scope result keyed by
/// <c>ChapterId</c> with <c>SceneId</c> null, or one scene, whose suggestions live on that scene's Active
/// Scene-scope result. A unit with no Active result annotates nothing and ships exactly as the plain export
/// would. Only <c>Outcome == null</c> suggestions are loaded: an Accepted one is already in the text, a
/// Dismissed / Reverted / Superseded one is not a proposal any more.</para>
///
/// <para>ANNOTATE BEFORE ASSEMBLY. Each unit's buffer is annotated on its own (its suggestions are anchored
/// in its own text) and then assembled. DocIO's <c>ImportContent</c> carries the hand-written revisions and
/// comments through and renumbers the second buffer's comment ids; <c>ProofreadAnnotatedAssemblyPremiseTests</c>
/// pins that, and if it ever goes red this has to move to annotate-after-assembly.</para>
/// </summary>
public partial class BookExportService
{
    /// <summary>
    /// How many pending suggestions the downloaded file carries as comments + tracked changes, as a decimal
    /// integer. Written on every successful ANNOTATED export, <c>"0"</c> included; never on a plain one.
    /// READ BY THE CLIENT (pagedraft-client <c>core/models/export.ts</c> <c>EXPORT_ANNOTATION_COUNT_HEADER</c>)
    /// and therefore listed in the CORS policy's exposed headers.
    /// </summary>
    public const string AnnotationCountHeader = "X-Export-Annotation-Count";

    /// <summary>
    /// How many pending suggestions could NOT be placed in the file, so the author is told the file holds
    /// fewer comments than the chapter view shows. Same presence rule and CORS note as
    /// <see cref="AnnotationCountHeader"/>.
    /// </summary>
    public const string UnanchoredCountHeader = "X-Export-Unanchored-Count";

    /// <summary>The whole book as one DOCX with each unit's pending proofread suggestions annotated in.</summary>
    public Task<BookExportResult> ExportBookProofreadAsync(Guid bookId, CancellationToken ct = default)
        => ExportBookCoreAsync(bookId, proofread: true, ct);

    /// <summary>One chapter as a DOCX with its pending proofread suggestions annotated in.</summary>
    public Task<BookExportResult> ExportChapterProofreadAsync(Guid bookId, Guid chapterId, CancellationToken ct = default)
        => ExportChapterCoreAsync(bookId, chapterId, proofread: true, ct);

    /// <summary>One unit's DOCX: the SFDT conversion, then the annotation pass when this is an annotated export.</summary>
    private byte[] Render(RenderableUnit unit, AnnotationSession? annotation)
    {
        var docx = _sfdtConversion.ConvertSfdtToDocx(unit.Sfdt);
        return annotation == null ? docx : annotation.Annotate(unit, docx);
    }

    /// <summary>The chapter path's session: the book's language (the chapter row does not carry one), then the load.</summary>
    private async Task<AnnotationSession> StartChapterAnnotationAsync(Guid bookId, Guid chapterId, CancellationToken ct)
    {
        var language = await _db.Books
            .AsNoTracking()
            .Where(b => b.Id == bookId)
            .Select(b => b.Language)
            .FirstOrDefaultAsync(ct);
        return await StartAnnotationAsync(new[] { chapterId }, language, ct);
    }

    /// <summary>
    /// Loads, in ONE query, the pending suggestions of every Active Proofread result (Chapter or Scene scope)
    /// on the given chapters, keyed by the unit they belong to. When a key somehow holds more than one Active
    /// row (archiving is not transactional with the insert), the newest wins, as it does in the chapter view.
    /// </summary>
    private async Task<AnnotationSession> StartAnnotationAsync(
        IReadOnlyCollection<Guid> chapterIds, string? language, CancellationToken ct)
    {
        var ids = chapterIds.ToList();
        var results = await _db.AnalysisResults
            .AsNoTracking()
            .Where(a =>
                a.AnalysisType == AnalysisType.Proofread &&
                a.Status == AnalysisStatus.Active &&
                (a.Scope == AnalysisScope.Chapter || a.Scope == AnalysisScope.Scene) &&
                a.ChapterId != null && ids.Contains(a.ChapterId.Value))
            .Include(a => a.Suggestions.Where(s => s.Outcome == null))
            .ToListAsync(ct);

        var byUnit = new Dictionary<(Guid ChapterId, Guid? SceneId), AnalysisResult>();
        foreach (var result in results.OrderBy(r => r.CreatedAt))
        {
            // A Chapter-scope row with a SceneId, or a Scene-scope row without one, is not either unit's.
            var sceneId = result.Scope == AnalysisScope.Scene ? result.SceneId : null;
            if (result.Scope == AnalysisScope.Scene && sceneId == null) continue;
            if (result.Scope == AnalysisScope.Chapter && result.SceneId != null) continue;
            byUnit[(result.ChapterId!.Value, sceneId)] = result; // ascending, so the newest is written last
        }

        return new AnnotationSession(this, language ?? "he", byUnit);
    }

    /// <summary>
    /// One annotated export in progress: the per-unit results, one timestamp for every comment and revision in
    /// the file, and the running counts.
    /// </summary>
    private sealed class AnnotationSession
    {
        private readonly BookExportService _owner;
        private readonly string _language;
        private readonly Dictionary<(Guid ChapterId, Guid? SceneId), AnalysisResult> _byUnit;
        private readonly DateTime _stamp = DateTime.UtcNow;
        private int _placed;
        private int _unanchored;
        private int _unitsWithResult;

        public AnnotationSession(
            BookExportService owner, string language, Dictionary<(Guid ChapterId, Guid? SceneId), AnalysisResult> byUnit)
        {
            _owner = owner;
            _language = language;
            _byUnit = byUnit;
        }

        public string FileNameSuffix => ProofreadAnnotationStrings.FileNameSuffix(_language);

        public byte[] Annotate(RenderableUnit unit, byte[] docx)
        {
            if (!_byUnit.TryGetValue((unit.ChapterId, unit.SceneId), out var result)) return docx;
            _unitsWithResult++;

            var outcome = _owner._proofreadAnnotator.Annotate(docx, result.Suggestions.ToList(), _language, _stamp);
            _placed += outcome.Placed;
            _unanchored += outcome.Unanchored;
            return outcome.Docx;
        }

        public ExportAnnotationCounts Finish(Guid bookId, Guid? chapterId)
        {
            _owner._logger?.LogInformation(
                "Proofread-annotated export of book {BookId} (chapter {ChapterId}): {Placed} suggestions placed, " +
                "{Unanchored} not placed, from {UnitsWithResult} unit(s) with an Active proofread result.",
                bookId, chapterId?.ToString() ?? "all", _placed, _unanchored, _unitsWithResult);
            return new ExportAnnotationCounts(_placed, _unanchored);
        }
    }
}
