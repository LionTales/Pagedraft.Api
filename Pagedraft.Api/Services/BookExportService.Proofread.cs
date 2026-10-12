using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Export;

namespace Pagedraft.Api.Services;

/// <summary>
/// What an annotated export wrote: <paramref name="Placed"/> pending suggestions became tracked change(s)
/// (a Word comment too only when there was a label or an explanation to add); <paramref name="Unanchored"/> could not be placed (no match, ambiguous, crossing a
/// paragraph, or overlapping a placed one). <c>Placed + Unanchored</c> is the number of pending suggestions
/// on the Active Proofread results of the units in the file, minus those of skipped units:
/// <paramref name="UnreliableSkippedChapters"/> is how many CHAPTERS had at least one unit left unannotated
/// because its Active result is flagged unreliable and the export asked to skip those (always 0 on include).
/// </summary>
public sealed record ExportAnnotationCounts(int Placed, int Unanchored, int UnreliableSkippedChapters = 0);

/// <summary>
/// What an annotated export does with a unit whose Active Proofread result is flagged
/// <see cref="AnalysisResult.ProofreadResultUnreliable"/>. <see cref="Include"/> (the default) annotates it like
/// any other; <see cref="Skip"/> annotates nothing for that unit, which still renders as plain text. The wire
/// form is <c>?unreliable=include|skip</c>, parsed by <see cref="BookExportService.TryParseUnreliableMode"/>.
/// </summary>
public enum UnreliableProofreadMode
{
    Include,
    Skip,
}

/// <summary>
/// One unit of an export, with where it came from: the chapter's own SFDT (<paramref name="SceneId"/> null)
/// or one scene's, when the scenes hold the chapter's current text.
/// </summary>
internal sealed record RenderableUnit(string Sfdt, Guid ChapterId, Guid? SceneId);

/// <summary>
/// The proofread-annotated exports (plan whole-book-proofread-and-annotated-docx-2026-10-11, c4): the same
/// document as the plain export, with every PENDING suggestion of each unit's Active Proofread result written
/// in as a tracked change (with a Word comment only when it carries a label or an explanation the change
/// does not show; see <c>ProofreadDocxAnnotator</c>), ready to hand back to the author.
///
/// <para>WHICH RESULT, PER UNIT. The unit rule is the plain export's (<see cref="RenderableUnitSourcesOf"/>),
/// so a unit is either the chapter row, whose suggestions live on the Active Chapter-scope result keyed by
/// <c>ChapterId</c> with <c>SceneId</c> null, or one scene, whose suggestions live on that scene's Active
/// Scene-scope result. A unit with no Active result annotates nothing and ships exactly as the plain export
/// would. Only <c>Outcome == null</c> suggestions are loaded: an Accepted one is already in the text, a
/// Dismissed / Reverted / Superseded one is not a proposal any more.</para>
///
/// <para>UNRELIABLE RESULTS (owner decision 2026-10-12). Everything is annotated by default, a result flagged
/// <see cref="AnalysisResult.ProofreadResultUnreliable"/> included. The client reads the status's
/// UnreliableChapters first and, when any are relevant, asks the author; <c>?unreliable=skip</c> then leaves
/// each such unit as plain text and reports the chapters in <see cref="UnreliableSkippedCountHeader"/>.</para>
///
/// <para>ANNOTATE BEFORE ASSEMBLY. Each unit's buffer is annotated on its own (its suggestions are anchored
/// in its own text) and then assembled. DocIO's <c>ImportContent</c> carries the hand-written revisions and
/// comments through and renumbers the second buffer's comment ids; <c>ProofreadAnnotatedAssemblyPremiseTests</c>
/// pins that, and if it ever goes red this has to move to annotate-after-assembly.</para>
/// </summary>
public partial class BookExportService
{
    /// <summary>
    /// How many pending suggestions the downloaded file carries as tracked changes (commented or not), as a decimal
    /// integer. Written on every successful ANNOTATED export, <c>"0"</c> included; never on a plain one.
    /// READ BY THE CLIENT (pagedraft-client <c>core/models/export.ts</c> <c>EXPORT_ANNOTATION_COUNT_HEADER</c>)
    /// and therefore listed in the CORS policy's exposed headers.
    /// </summary>
    public const string AnnotationCountHeader = "X-Export-Annotation-Count";

    /// <summary>
    /// How many pending suggestions could NOT be placed in the file, so the author is told the file holds
    /// fewer tracked changes than the chapter view shows suggestions. Same presence rule and CORS note as
    /// <see cref="AnnotationCountHeader"/>.
    /// </summary>
    public const string UnanchoredCountHeader = "X-Export-Unanchored-Count";

    /// <summary>
    /// How many CHAPTERS had at least one unit left unannotated because its Active Proofread result is flagged
    /// unreliable and the request said <c>?unreliable=skip</c>; <c>"0"</c> on include. Same presence rule and
    /// CORS note as <see cref="AnnotationCountHeader"/>.
    /// </summary>
    public const string UnreliableSkippedCountHeader = "X-Export-Unreliable-Skipped-Count";

    /// <summary>
    /// Parses the <c>unreliable</c> query value: absent or blank is <see cref="UnreliableProofreadMode.Include"/>,
    /// otherwise <c>include</c> or <c>skip</c>, case-insensitive. Anything else is false (the controller's 400).
    /// </summary>
    public static bool TryParseUnreliableMode(string? value, out UnreliableProofreadMode mode)
    {
        mode = UnreliableProofreadMode.Include;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "include": return true;
            case "skip": mode = UnreliableProofreadMode.Skip; return true;
            default: return false;
        }
    }

    /// <summary>The whole book as one DOCX with each unit's pending proofread suggestions annotated in.</summary>
    public Task<BookExportResult> ExportBookProofreadAsync(
        Guid bookId, UnreliableProofreadMode unreliable = UnreliableProofreadMode.Include, CancellationToken ct = default)
        => ExportBookCoreAsync(bookId, proofread: true, ct, unreliable);

    /// <summary>One chapter as a DOCX with its pending proofread suggestions annotated in.</summary>
    public Task<BookExportResult> ExportChapterProofreadAsync(
        Guid bookId, Guid chapterId, UnreliableProofreadMode unreliable = UnreliableProofreadMode.Include,
        CancellationToken ct = default)
        => ExportChapterCoreAsync(bookId, chapterId, proofread: true, ct, unreliable);

    /// <summary>One unit's DOCX: the SFDT conversion, then the annotation pass when this is an annotated export.</summary>
    private byte[] Render(RenderableUnit unit, AnnotationSession? annotation)
    {
        var docx = _sfdtConversion.ConvertSfdtToDocx(unit.Sfdt);
        return annotation == null ? docx : annotation.Annotate(unit, docx);
    }

    /// <summary>The chapter path's session: the book's language (the chapter row does not carry one), then the load.</summary>
    private async Task<AnnotationSession> StartChapterAnnotationAsync(
        Guid bookId, Guid chapterId, UnreliableProofreadMode unreliable, CancellationToken ct)
    {
        var language = await _db.Books
            .AsNoTracking()
            .Where(b => b.Id == bookId)
            .Select(b => b.Language)
            .FirstOrDefaultAsync(ct);
        return await StartAnnotationAsync(new[] { chapterId }, language, unreliable, ct);
    }

    /// <summary>
    /// Loads, in ONE query, the pending suggestions of every Active Proofread result (Chapter or Scene scope)
    /// on the given chapters, keyed by the unit they belong to. When a key somehow holds more than one Active
    /// row (archiving is not transactional with the insert), the newest wins, as it does in the chapter view.
    /// </summary>
    private async Task<AnnotationSession> StartAnnotationAsync(
        IReadOnlyCollection<Guid> chapterIds, string? language, UnreliableProofreadMode unreliable, CancellationToken ct)
    {
        var results = await ActiveProofreadUnitsQuery(_db, chapterIds).ToListAsync(ct);

        var byUnit = new Dictionary<(Guid ChapterId, Guid? SceneId), ProofreadUnitRow>();
        foreach (var result in results.OrderBy(r => r.CreatedAt))
        {
            // A Chapter-scope row with a SceneId, or a Scene-scope row without one, is not either unit's.
            var sceneId = result.Scope == AnalysisScope.Scene ? result.SceneId : null;
            if (result.Scope == AnalysisScope.Scene && sceneId == null) continue;
            if (result.Scope == AnalysisScope.Chapter && result.SceneId != null) continue;
            byUnit[(result.ChapterId, sceneId)] = result; // ascending, so the newest is written last
        }

        return new AnnotationSession(this, language ?? "he", byUnit, unreliable);
    }

    /// <summary>
    /// The suggestion load as a query, so <c>BookExportProofreadQueryShapeTests</c> can pin its SQL. It PROJECTS
    /// the few result columns the export reads instead of including whole result rows (plan c12). Including
    /// them put each result's <c>ResultText</c> and <c>SourceTextSnapshot</c> (a whole chapter each) on EVERY
    /// joined suggestion row: 1.33 GB for 'זיכרונות של מכשף להגהה' (4,676 pending suggestions, 1,476 of them on a
    /// 210,000-character chapter), which SqlClient's async reader took 130 to 190 s to drain (13 s synchronously)
    /// before the first chapter rendered. The projection reads ~1 MB. Ordered by result id, the order the
    /// include query had, so the newest-wins tie-break on <c>CreatedAt</c> sees results in the same order.
    /// </summary>
    internal static IQueryable<ProofreadUnitRow> ActiveProofreadUnitsQuery(
        AppDbContext db, IReadOnlyCollection<Guid> chapterIds)
    {
        var ids = chapterIds.ToList();
        return db.AnalysisResults
            .AsNoTracking()
            .Where(a =>
                a.AnalysisType == AnalysisType.Proofread &&
                a.Status == AnalysisStatus.Active &&
                (a.Scope == AnalysisScope.Chapter || a.Scope == AnalysisScope.Scene) &&
                a.ChapterId != null && ids.Contains(a.ChapterId.Value))
            .OrderBy(a => a.Id)
            .Select(a => new ProofreadUnitRow(
                a.ChapterId!.Value,
                a.SceneId,
                a.Scope,
                a.CreatedAt,
                a.ProofreadResultUnreliable,
                a.Suggestions.Where(s => s.Outcome == null).ToList()));
    }

    /// <summary>
    /// What the annotated export needs of one Active Proofread result: which unit it belongs to, its age (newest
    /// wins), its unreliable flag and its PENDING suggestions. Deliberately not the entity: see
    /// <see cref="ActiveProofreadUnitsQuery"/>.
    /// </summary>
    internal sealed record ProofreadUnitRow(
        Guid ChapterId,
        Guid? SceneId,
        AnalysisScope Scope,
        DateTimeOffset CreatedAt,
        bool ProofreadResultUnreliable,
        List<AnalysisSuggestion> Suggestions);

    /// <summary>
    /// One annotated export in progress: the per-unit results, one timestamp for every comment and revision in
    /// the file, and the running counts.
    /// </summary>
    private sealed class AnnotationSession
    {
        private readonly BookExportService _owner;
        private readonly string _language;
        private readonly Dictionary<(Guid ChapterId, Guid? SceneId), ProofreadUnitRow> _byUnit;
        private readonly UnreliableProofreadMode _unreliable;
        private readonly DateTime _stamp = DateTime.UtcNow;
        private readonly HashSet<Guid> _unreliableSkippedChapters = new();
        private int _placed;
        private int _unanchored;
        private int _unitsWithResult;
        private int _unitsSkipped;

        public AnnotationSession(
            BookExportService owner, string language, Dictionary<(Guid ChapterId, Guid? SceneId), ProofreadUnitRow> byUnit,
            UnreliableProofreadMode unreliable)
        {
            _owner = owner;
            _language = language;
            _byUnit = byUnit;
            _unreliable = unreliable;
        }

        public string FileNameSuffix => ProofreadAnnotationStrings.FileNameSuffix(_language);

        public byte[] Annotate(RenderableUnit unit, byte[] docx)
        {
            if (!_byUnit.TryGetValue((unit.ChapterId, unit.SceneId), out var result)) return docx;

            // Skip = the unit ships as plain text and its suggestions are neither placed nor unanchored: the
            // author chose not to receive a proofread the system itself flagged as broken.
            if (_unreliable == UnreliableProofreadMode.Skip && result.ProofreadResultUnreliable)
            {
                _unitsSkipped++;
                _unreliableSkippedChapters.Add(unit.ChapterId);
                return docx;
            }

            _unitsWithResult++;
            var outcome = _owner._proofreadAnnotator.Annotate(docx, result.Suggestions, _language, _stamp);
            _placed += outcome.Placed;
            _unanchored += outcome.Unanchored;
            return outcome.Docx;
        }

        public ExportAnnotationCounts Finish(Guid bookId, Guid? chapterId)
        {
            _owner._logger?.LogInformation(
                "Proofread-annotated export of book {BookId} (chapter {ChapterId}): {Placed} suggestions placed, " +
                "{Unanchored} not placed, from {UnitsWithResult} unit(s) with an Active proofread result; " +
                "unreliable={Unreliable}, {UnitsSkipped} unreliable unit(s) skipped in {ChaptersSkipped} chapter(s).",
                bookId, chapterId?.ToString() ?? "all", _placed, _unanchored, _unitsWithResult,
                _unreliable, _unitsSkipped, _unreliableSkippedChapters.Count);
            return new ExportAnnotationCounts(_placed, _unanchored, _unreliableSkippedChapters.Count);
        }
    }
}
