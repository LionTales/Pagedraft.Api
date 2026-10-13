using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Export;

namespace Pagedraft.Api.Services;

/// <summary>
/// What an annotated export wrote: <paramref name="Placed"/> pending suggestions became tracked change(s)
/// (a Word comment too only when there was a label or an explanation to add); <paramref name="Unanchored"/> could
/// not be placed (no match, ambiguous, crossing a paragraph, or overlapping a placed one), every non-whitespace one
/// of a unit the annotator threw on included (that unit ships unannotated; be-c06);
/// <paramref name="WhitespaceSkipped"/> changed whitespace only and were deliberately not written (plan c16, the
/// client diff's own hide rule). <c>Placed + Unanchored + WhitespaceSkipped</c> is the number of pending
/// suggestions on the Active Proofread results of the units in the file, minus those of skipped units:
/// <paramref name="UnreliableSkippedChapters"/> is how many CHAPTERS had at least one unit left unannotated
/// because its Active result is flagged unreliable and the export asked to skip those (always 0 on include).
/// </summary>
public sealed record ExportAnnotationCounts(
    int Placed, int Unanchored, int WhitespaceSkipped = 0, int UnreliableSkippedChapters = 0);

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
/// Scene-scope result, in the BOOK'S language (be-c05; why: <see cref="StartAnnotationAsync"/>). Which result is
/// a unit's, and whether it is unreliable, is <see cref="ProofreadUnitResults"/>, the rule the whole-book
/// proofread status also reads. A unit with no such result annotates nothing and ships exactly as the plain
/// export would. Only <c>Outcome == null</c> suggestions are loaded: an Accepted one is already in the text, a
/// Dismissed / Reverted / Superseded one is not a proposal any more.</para>
///
/// <para>UNRELIABLE RESULTS (owner decision 2026-10-12). Everything is annotated by default, a result flagged
/// <see cref="AnalysisResult.ProofreadResultUnreliable"/> included. The client reads the status's
/// UnreliableChapters first and, when any are relevant, asks the author; <c>?unreliable=skip</c> then leaves
/// each such unit as plain text and reports the chapters in <see cref="UnreliableSkippedCountHeader"/>. Both
/// sides decide per exported unit through the one rule, so for the whole book that header equals the status's
/// UnreliableChapters count in the book's language.</para>
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
    /// How many pending suggestions changed whitespace only (or nothing) and were therefore not written into the
    /// file (plan c16): <c>Placed + Unanchored + WhitespaceSkipped</c> = pending, so a caller can reconcile the
    /// file against the stored rows. Same presence rule and CORS note as <see cref="AnnotationCountHeader"/>.
    /// </summary>
    public const string WhitespaceSkippedCountHeader = "X-Export-Whitespace-Skipped-Count";

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

    /// <summary>
    /// One unit's DOCX: the SFDT conversion, then the annotation pass when this is an annotated export. The
    /// conversion ALWAYS gets the book's language, plain or annotated: it decides the Hebrew export fidelity
    /// (he-IL on runs without a complex-script language, right-to-left as the default paragraph direction; see
    /// <see cref="SfdtConversionService.ConvertSfdtToDocx"/>), and an annotated export converted without it
    /// was laid out left-to-right. Pinned by <c>ExportAnnotatedFidelityTests</c>.
    /// </summary>
    private byte[] Render(RenderableUnit unit, string bookLanguage, AnnotationSession? annotation)
    {
        var docx = _sfdtConversion.ConvertSfdtToDocx(unit.Sfdt, bookLanguage);
        return annotation == null ? docx : annotation.Annotate(unit, docx);
    }

    /// <summary>
    /// Loads, in ONE query, the pending suggestions of every Active Proofread result (Chapter or Scene scope)
    /// on the given chapters, and keeps each unit's CURRENT result through <see cref="ProofreadUnitResults"/>,
    /// the rule the whole-book proofread status also calls (be-c05): keyed to the unit, in the book's language,
    /// the newest when a key somehow holds more than one Active row (archiving is not transactional with the
    /// insert).
    ///
    /// <para>WHY THE EXPORT FILTERS BY LANGUAGE (be-c05). The status the client reads before the dialog is keyed
    /// by a language (the client sends the book's), so a unit whose result is in another language is "not
    /// proofread" there and is never listed as unreliable. Before be-c05 the export read every language, so it
    /// skipped such a flagged unit without the dialog ever naming it, and annotated a proofread the status said
    /// did not exist. This endpoint has no language parameter, so it uses the book's language, normalized by the
    /// same <c>BaselineLanguageResolver.Normalize</c> the status call applies: the file now holds exactly the
    /// proofread the status describes. The cost: a unit whose only Active result predates a change of the book's
    /// language ships unannotated, although the (language-blind) chapter view still shows those suggestions;
    /// the status reports that chapter as not proofread, and the next whole-book run replaces the result.</para>
    /// </summary>
    private async Task<AnnotationSession> StartAnnotationAsync(
        IReadOnlyCollection<Guid> chapterIds, string language, UnreliableProofreadMode unreliable, CancellationToken ct)
    {
        var results = await ActiveProofreadUnitsQuery(_db, chapterIds).ToListAsync(ct);
        var byUnit = ProofreadUnitResults.CurrentByUnit(results, language, r => r.Facts);
        return new AnnotationSession(this, language, byUnit, unreliable);
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
                a.Language,
                a.ProofreadResultUnreliable,
                a.Suggestions.Where(s => s.Outcome == null).ToList()));
    }

    /// <summary>
    /// What the annotated export needs of one Active Proofread result: which unit it belongs to, its age (newest
    /// wins), its language and unreliable flag (both read by <see cref="ProofreadUnitResults"/>) and its PENDING
    /// suggestions. Deliberately not the entity: see <see cref="ActiveProofreadUnitsQuery"/>.
    /// </summary>
    internal sealed record ProofreadUnitRow(
        Guid ChapterId,
        Guid? SceneId,
        AnalysisScope Scope,
        DateTimeOffset CreatedAt,
        string? Language,
        bool ProofreadResultUnreliable,
        List<AnalysisSuggestion> Suggestions)
    {
        /// <summary>This row as the shared unit rule reads it.</summary>
        public ProofreadUnitResults.ResultFacts Facts =>
            new(ChapterId, SceneId, Scope, CreatedAt, Language, ProofreadResultUnreliable);
    }

    /// <summary>
    /// One annotated export in progress: the per-unit results, one timestamp for every comment and revision in
    /// the file, and the running counts.
    /// </summary>
    private sealed class AnnotationSession
    {
        private readonly BookExportService _owner;
        private readonly string _language;
        private readonly Dictionary<ProofreadUnitResults.UnitKey, ProofreadUnitRow> _byUnit;
        private readonly UnreliableProofreadMode _unreliable;
        private readonly DateTime _stamp = DateTime.UtcNow;
        private readonly HashSet<Guid> _unreliableSkippedChapters = new();
        private int _placed;
        private int _unanchored;
        private int _whitespaceSkipped;
        private int _unitsWithResult;
        private int _unitsSkipped;
        private int _unitsFailed;

        public AnnotationSession(
            BookExportService owner, string language, Dictionary<ProofreadUnitResults.UnitKey, ProofreadUnitRow> byUnit,
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
            if (!_byUnit.TryGetValue(new ProofreadUnitResults.UnitKey(unit.ChapterId, unit.SceneId), out var result))
                return docx;

            // Skip = the unit ships as plain text and its suggestions are neither placed nor unanchored: the
            // author chose not to receive a proofread the system itself flagged as broken. The verdict is the
            // status's own (ProofreadUnitResults), so the skipped chapters are the status's UnreliableChapters.
            if (_unreliable == UnreliableProofreadMode.Skip
                && ProofreadUnitResults.VerdictOf(result.Facts) == ProofreadUnitResults.Verdict.Unreliable)
            {
                _unitsSkipped++;
                _unreliableSkippedChapters.Add(unit.ChapterId);
                return docx;
            }

            _unitsWithResult++;
            ProofreadAnnotationResult outcome;
            try
            {
                outcome = _owner._proofreadAnnotator.Annotate(docx, result.Suggestions, _language, _stamp);
            }
            catch (Exception ex)
            {
                // ISOLATED PER UNIT (be-c06): one unit the annotator cannot write (an XML-illegal character in
                // model text 500ed the whole export before XmlSafeText) must not cost the author the other
                // chapters. The unit ships as its plain buffer (Annotate writes into a copy, so docx is untouched)
                // and its pending suggestions are counted by the annotator's own split, so
                // Placed + Unanchored + WhitespaceSkipped = pending still holds: the whitespace-only ones would
                // never have been written, every other one is "could not be placed". Ids only in the log, never
                // text: the exception message can quote the author's prose or the model's.
                var pending = result.Suggestions.Where(s => s.Outcome == null).ToList();
                var whitespace = pending.Count(s => ProofreadWhitespace.IsWhitespaceOnlyChange(s.OriginalText, s.SuggestedText));
                _unitsFailed++;
                _unanchored += pending.Count - whitespace;
                _whitespaceSkipped += whitespace;
                _owner._logger?.LogError(
                    "Proofread annotation failed for chapter {ChapterId} scene {SceneId} ({ExceptionType}); the unit " +
                    "ships unannotated and its {Pending} pending suggestion(s) count as not placed " +
                    "({WhitespaceSkipped} whitespace-only). {StackTrace}",
                    unit.ChapterId, unit.SceneId?.ToString() ?? "none", ex.GetType().FullName, pending.Count,
                    whitespace, ex.StackTrace);
                return docx;
            }
            _placed += outcome.Placed;
            _unanchored += outcome.Unanchored;
            _whitespaceSkipped += outcome.WhitespaceSkipped;
            return outcome.Docx;
        }

        public ExportAnnotationCounts Finish(Guid bookId, Guid? chapterId)
        {
            _owner._logger?.LogInformation(
                "Proofread-annotated export of book {BookId} (chapter {ChapterId}): {Placed} suggestions placed, " +
                "{Unanchored} not placed, {WhitespaceSkipped} whitespace-only skipped, from {UnitsWithResult} unit(s) " +
                "with an Active proofread result; " +
                "unreliable={Unreliable}, {UnitsSkipped} unreliable unit(s) skipped in {ChaptersSkipped} chapter(s); " +
                "{UnitsFailed} unit(s) the annotator failed on shipped unannotated.",
                bookId, chapterId?.ToString() ?? "all", _placed, _unanchored, _whitespaceSkipped, _unitsWithResult,
                _unreliable, _unitsSkipped, _unreliableSkippedChapters.Count, _unitsFailed);
            return new ExportAnnotationCounts(_placed, _unanchored, _whitespaceSkipped, _unreliableSkippedChapters.Count);
        }
    }
}
