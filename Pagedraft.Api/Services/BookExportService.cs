using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;

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
    NothingToExport,

    /// <summary>
    /// There ARE chapters, but not one of them has anything renderable in it, so the assembled document would
    /// be blank. The SAME invariant as <see cref="NothingToExport"/>, one situation over, and it is a separate
    /// member because the two need different copy and lead to different next actions: "import a manuscript"
    /// versus "write something first". The caller turns both into a 409 with its own reason token.
    ///
    /// Until w1+w4 this case produced a valid, correctly-named, EMPTY .docx with HTTP 200, and a test pinned
    /// that as desired behaviour. In a book-editing product a chapter missing from an exported manuscript is
    /// indistinguishable from data loss, so an export that would contain nothing must say so instead.
    /// </summary>
    NothingWritten
}

/// <summary>
/// A chapter that was left OUT of an assembled export because it has nothing renderable in it.
///
/// This travels to the client on a SUCCESSFUL export (see <see cref="BookExportService.SkippedChaptersHeader"/>)
/// so the author is told which chapters are not in the file they just downloaded. Silence here is the defect:
/// a 10-chapter book with 3 unwritten chapters used to export 7 with no count, no header and nothing on any
/// surface saying so.
/// </summary>
/// <param name="Order">The chapter's zero-based <c>Order</c>, exactly as stored - the client owns display numbering.</param>
/// <param name="Title">The chapter title as stored, so the sentence the client renders names chapters the author recognizes.</param>
public sealed record ExportSkippedChapter(int Order, string Title);

/// <summary>The assembled document, or the reason there isn't one.</summary>
/// <param name="SkippedChapters">
/// Never null. Empty on every outcome except <see cref="BookExportOutcome.Ok"/>, where it names the chapters
/// the assembled document does NOT contain, in <c>Order</c>. Empty on the chapter path by construction: a
/// single unrenderable chapter is <see cref="BookExportOutcome.NothingWritten"/>, not a skip.
/// </param>
public sealed record BookExportResult(
    BookExportOutcome Outcome,
    byte[]? Content,
    string? FileName,
    IReadOnlyList<ExportSkippedChapter> SkippedChapters)
{
    private static readonly IReadOnlyList<ExportSkippedChapter> NoneSkipped = Array.Empty<ExportSkippedChapter>();

    public static BookExportResult Ok(byte[] content, string fileName, IReadOnlyList<ExportSkippedChapter>? skipped = null)
        => new(BookExportOutcome.Ok, content, fileName, skipped ?? NoneSkipped);
    public static BookExportResult BookNotFound() => new(BookExportOutcome.BookNotFound, null, null, NoneSkipped);
    public static BookExportResult ChapterNotFound() => new(BookExportOutcome.ChapterNotFound, null, null, NoneSkipped);
    public static BookExportResult NothingToExport() => new(BookExportOutcome.NothingToExport, null, null, NoneSkipped);
    public static BookExportResult NothingWritten() => new(BookExportOutcome.NothingWritten, null, null, NoneSkipped);
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
    /// <summary>
    /// The DOCX media type, spelled once. Both endpoints and their tests read it from here.
    ///
    /// MIRRORED IN THE CLIENT as <c>DOCX_CONTENT_TYPE</c> (pagedraft-client
    /// <c>src/app/core/models/export.ts</c>); the two must stay byte-identical.
    /// </summary>
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>
    /// How many chapters the downloaded document does NOT contain, as a decimal integer. Present on EVERY
    /// successful export from EITHER path, <c>"0"</c> included - so a client that reads no header at all knows
    /// it is looking at an old server or a proxy that stripped it, rather than concluding "nothing was
    /// skipped" from an absence.
    ///
    /// READ BY THE CLIENT in <c>src/app/core/services/export.service.ts</c>, which is also the only other
    /// response-header read in the product. Like <c>Content-Disposition</c>, this header is NOT
    /// CORS-safelisted, so it is listed in the CORS policy's <c>WithExposedHeaders</c> in <c>Program.cs</c>;
    /// without that entry the read returns null cross-origin and the author is silently never told.
    /// </summary>
    public const string SkippedCountHeader = "X-Export-Skipped-Count";

    /// <summary>
    /// WHICH chapters the downloaded document does not contain, so the client can name them rather than
    /// render a bare number.
    ///
    /// Wire format: <c>Uri.EscapeDataString</c> of a UTF-8 JSON array of
    /// <c>{ "order": &lt;int&gt;, "title": &lt;string&gt; }</c>, in <c>Order</c>. Percent-encoded because a
    /// header value is bytes and every chapter title in this product may be Hebrew; the client reads it as
    /// <c>JSON.parse(decodeURIComponent(value))</c>.
    ///
    /// Absent when nothing was skipped. It may name FEWER chapters than
    /// <see cref="SkippedCountHeader"/> reports, because the header is bounded so a long book cannot blow a
    /// proxy's response-header limit: THE COUNT IS AUTHORITATIVE, the list is a courtesy.
    /// </summary>
    public const string SkippedChaptersHeader = "X-Export-Skipped-Chapters";

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

    /// <summary>
    /// Optional so the direct-construction tests keep compiling; DI always supplies the real logger. Its one
    /// job is <see cref="WarnIfBothCopiesLookWritten"/>: a chapter and its scenes are two independently
    /// writable stores for one chapter's prose (see <see cref="RenderableUnitsOf"/>), and when both have been
    /// written the export has to pick one. Picking one silently is the class of dishonesty this whole wave is
    /// about, and there is no channel to tell the author on a DOCX download, so at minimum the server says so.
    /// </summary>
    private readonly ILogger<BookExportService>? _logger;

    public BookExportService(
        AppDbContext db,
        ChapterService chapters,
        SfdtConversionService sfdtConversion,
        BookAssemblyService bookAssembly,
        ILogger<BookExportService>? logger = null)
    {
        _db = db;
        _chapters = chapters;
        _sfdtConversion = sfdtConversion;
        _bookAssembly = bookAssembly;
        _logger = logger;
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
    ///
    /// AN EXPORT NEVER LIES ABOUT WHAT IS IN IT. A chapter with nothing renderable in it cannot be assembled
    /// and is left out, but it is never left out SILENTLY: every skipped chapter is named on the result, and
    /// if that leaves nothing at all the answer is <see cref="BookExportOutcome.NothingWritten"/> rather than
    /// a valid, correctly-named, empty .docx. Both halves matter, because the partial case is the worse one -
    /// a file that looks like the author's manuscript with three chapters quietly missing from it.
    ///
    /// AND IT NEVER SHIPS A DRAFT THE AUTHOR HAS ALREADY REPLACED: a chapter the author has split into scenes
    /// is assembled from those scenes when they hold the writing, because <c>chapter.ContentSfdt</c> is then a
    /// frozen pre-split copy. See <see cref="RenderableUnitsOf"/> for the rule and why it is conditional.
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

        var scenesByChapter = await LoadScenesAsync(chapters.Select(c => c.Id).ToList(), ct);

        var buffers = new List<byte[]>(chapters.Count);
        var skipped = new List<ExportSkippedChapter>();
        foreach (var chapter in chapters)
        {
            scenesByChapter.TryGetValue(chapter.Id, out var scenes);
            var units = ResolveUnitsFor(chapter, scenes);
            if (units.Count == 0)
            {
                skipped.Add(new ExportSkippedChapter(chapter.Order, chapter.Title));
                continue;
            }
            // A scene-composed chapter contributes SEVERAL buffers to the same list, in scene order, with
            // nothing inserted between them: scenes are contiguous prose inside one chapter, and the split
            // deleted whatever break marker the author had written, so inventing one would be writing into
            // their manuscript.
            foreach (var unit in units) buffers.Add(_sfdtConversion.ConvertSfdtToDocx(unit));
        }

        // Every chapter skipped: the assembler WOULD produce a valid empty document here, and that is exactly
        // the answer this must not give. NothingToExport's own doc comment states the invariant.
        if (buffers.Count == 0) return BookExportResult.NothingWritten();

        var docx = _bookAssembly.AssembleDocx(buffers);
        return BookExportResult.Ok(docx, BuildFileName(title, fallbackStem: "book"), skipped);
    }

    /// <summary>
    /// One chapter as a DOCX, named after the chapter. The chapter lookup is already book-scoped, so an
    /// unknown book and an unknown chapter both land on <see cref="BookExportOutcome.ChapterNotFound"/>.
    ///
    /// THE SAME HONESTY RULE AS THE BOOK PATH, which is the whole reason these two live in one service: a
    /// chapter with nothing renderable in it used to download as a valid, correctly-named, EMPTY .docx, which
    /// is the book path's all-unwritten case seen through a one-chapter window. It answers
    /// <see cref="BookExportOutcome.NothingWritten"/> instead. Nothing can be "skipped" on this path - the
    /// unit asked for either exports or it does not - so the result's skipped list is always empty here.
    ///
    /// THE SCENE RULE IS ALSO SHARED, and it had drifted here in the other direction: this path read
    /// <c>chapter.ContentSfdt</c> and never looked at the chapter's scenes, so "export this chapter" on a
    /// scene-split chapter handed back the pre-split draft under the chapter's own name. Both paths now route
    /// the decision through the one <see cref="RenderableUnitsOf"/>, so they cannot answer differently about
    /// the same chapter.
    /// </summary>
    public async Task<BookExportResult> ExportChapterAsync(Guid bookId, Guid chapterId, CancellationToken ct = default)
    {
        var chapter = await _chapters.GetByIdAsync(bookId, chapterId, ct);
        if (chapter == null) return BookExportResult.ChapterNotFound();

        var scenesByChapter = await LoadScenesAsync(new[] { chapter.Id }, ct);
        scenesByChapter.TryGetValue(chapter.Id, out var scenes);

        var units = ResolveUnitsFor(chapter, scenes);
        if (units.Count == 0) return BookExportResult.NothingWritten();

        // AssembleDocx returns a single buffer untouched, so an unsplit chapter produces byte-for-byte what
        // it always did and only a scene-composed one goes through the assembler.
        var docx = _bookAssembly.AssembleDocx(units.Select(_sfdtConversion.ConvertSfdtToDocx).ToList());
        return BookExportResult.Ok(docx, BuildFileName(chapter.Title, fallbackStem: "chapter"));
    }

    /// <summary>
    /// The scenes of the given chapters, keyed by chapter, in the order they would be read.
    ///
    /// One query per export, and it returns NO ROWS for a book nobody has split - which is nearly every book,
    /// so the common path costs a round trip and nothing else. Untracked because this is a read: the chapters
    /// are already in the change tracker (see the note on <see cref="ChapterService.GetAllByBookAsync"/>) and
    /// nothing here mutates a scene.
    /// </summary>
    private async Task<Dictionary<Guid, List<Scene>>> LoadScenesAsync(IReadOnlyCollection<Guid> chapterIds, CancellationToken ct)
    {
        var scenes = await _db.Scenes
            .AsNoTracking()
            .Where(s => chapterIds.Contains(s.ChapterId))
            .ToListAsync(ct);

        return scenes
            .GroupBy(s => s.ChapterId)
            // Order, then CreatedAt as a deterministic tiebreak: Order is what the split assigns, what the
            // editor's chapter tree renders, and what SceneService.GetAllByChapterAsync already sorts by.
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Order).ThenBy(s => s.CreatedAt).ToList());
    }

    /// <summary>
    /// Whether a chapter's SCENES hold its current text, rather than the chapter row.
    ///
    /// A chapter and its scenes are two independently writable stores for one chapter's prose, and NOTHING in
    /// this product copies between them in either direction: <c>SplitScenesFromChapterAsync</c> reads
    /// <c>Chapter.ContentText</c> and writes scenes, and from that moment the chapter row is a frozen pre-split
    /// copy while the editor happily saves into either one. So an export has to decide which is the author's
    /// manuscript.
    ///
    /// THE ANSWER IS NOT "THE SCENES, WHENEVER THERE ARE SCENES", and it is worth saying why, because that is
    /// the rule this looks like it should be. The split is LOSSY: it slices plain text (so the chapter's
    /// formatting is gone), it deletes the scene-break markers themselves, it DISCARDS any segment shorter
    /// than <see cref="Analysis.SceneAutoSplitRules.MinSceneContentLength"/>, and it caps at
    /// <see cref="Analysis.SceneAutoSplitRules.MaxScenesPerChapter"/>. Right after a split the scenes are
    /// therefore a strictly poorer copy of the same prose, and preferring them would strip formatting and drop
    /// text out of the manuscript of an author who never touched a scene - data loss introduced as the cure
    /// for data loss.
    ///
    /// So the question asked is the narrower one that actually matters: HAS THE AUTHOR WRITTEN INTO THE SCENE
    /// LAYER? <c>UpdatedAt > CreatedAt</c> answers it exactly in this product. The <c>SaveChanges</c> override
    /// stamps a Scene's two timestamps equal on Add and bumps only <c>UpdatedAt</c> on Modify, the split Adds
    /// every scene in one <c>SaveChanges</c>, and the only client call that modifies a scene is the editor's
    /// content save. A scene created directly through the API with content and never touched again is the one
    /// case this reads as "not written" - it is unreachable from the client, and reading it that way keeps the
    /// pre-existing behaviour rather than inventing a new one.
    ///
    /// WHAT THIS DELIBERATELY DOES NOT DECIDE: the case where BOTH copies have been written (chapter, split,
    /// scene, then the chapter node again). Neither copy is a superset, nothing records which the author
    /// intended, and the honest resolutions all cost a persistence or product decision - see the be-c06
    /// section of the wave 3 fixes plan. This takes the scenes and
    /// <see cref="WarnIfBothCopiesLookWritten"/> makes it visible.
    /// </summary>
    internal static bool ScenesHoldTheChaptersCurrentText(IReadOnlyList<Scene>? scenes)
        => scenes is { Count: > 0 } && scenes.Any(s => s.UpdatedAt > s.CreatedAt);

    /// <summary>
    /// <see cref="RenderableUnitsOf"/> plus the one thing a pure function cannot do: say out loud when the
    /// export had to choose between two written copies of the same chapter. Both export paths go through this
    /// rather than calling the resolver directly, so the warning cannot exist on one path only.
    /// </summary>
    private IReadOnlyList<string> ResolveUnitsFor(Chapter chapter, IReadOnlyList<Scene>? scenes)
    {
        WarnIfBothCopiesLookWritten(chapter, scenes);
        return RenderableUnitsOf(chapter, scenes);
    }

    /// <summary>
    /// Logs when a chapter's scenes hold the writing AND the chapter row was itself written afterwards. The
    /// export takes the scenes; the chapter-level edit that landed later is not in the file, and nothing on
    /// the wire can tell the author so (a DOCX download has no room for it and be-c02's headers are about
    /// chapters left OUT, not about which copy of a chapter was taken).
    ///
    /// Not fatal, and deliberately not a skip: the scenes are still real text the author wrote, so shipping
    /// them beats shipping nothing. This is the observability half of a defect whose real fix is a decision
    /// about where the composite lives.
    ///
    /// The comparison is loose on purpose - <c>Chapter.UpdatedAt</c> is bumped by a title or order change too
    /// (<c>ChapterService.SaveAsync</c>), so this can warn about a rename. A false warning costs a log line; a
    /// missing one costs the author an explanation of where their paragraph went.
    /// </summary>
    private void WarnIfBothCopiesLookWritten(Chapter chapter, IReadOnlyList<Scene>? scenes)
    {
        if (_logger == null || !ScenesHoldTheChaptersCurrentText(scenes)) return;

        var written = scenes!;
        var newestScene = written.Max(s => s.UpdatedAt);
        if (chapter.UpdatedAt <= newestScene) return;

        _logger.LogWarning(
            "Export of chapter {ChapterId} ('{ChapterTitle}', book {BookId}) took its {SceneCount} SCENES, " +
            "whose newest write is {SceneUpdatedAt:o}, but the chapter row was itself written at " +
            "{ChapterUpdatedAt:o}. A chapter and its scenes are separate stores and PageDraft does not merge " +
            "them, so any chapter-level edit made after that scene write is not in the exported file.",
            chapter.Id, chapter.Title, chapter.BookId, written.Count, newestScene, chapter.UpdatedAt);
    }

    /// <summary>
    /// The SFDT documents a chapter contributes to an export, in reading order: its scenes when they hold the
    /// writing (<see cref="ScenesHoldTheChaptersCurrentText"/>), otherwise the chapter's own stored document.
    /// Empty means the chapter has nothing renderable, which the book path reports as a skipped chapter and
    /// the chapter path answers as <see cref="BookExportOutcome.NothingWritten"/>.
    ///
    /// Unrenderable units are dropped by the same <see cref="HasRenderableContent"/> guard the chapter row
    /// goes through - which already knows <see cref="SceneService"/>'s empty default. A chapter whose scenes
    /// hold the writing but are ALL blank therefore contributes nothing and is reported, rather than silently
    /// falling back to a pre-split draft the author has replaced with nothing.
    /// </summary>
    internal static IReadOnlyList<string> RenderableUnitsOf(Chapter chapter, IReadOnlyList<Scene>? scenes)
    {
        if (ScenesHoldTheChaptersCurrentText(scenes))
        {
            return scenes!
                .Select(s => s.ContentSfdt)
                .Where(HasRenderableContent)
                .Select(sfdt => sfdt!)
                .ToList();
        }

        return HasRenderableContent(chapter.ContentSfdt)
            ? new[] { chapter.ContentSfdt }
            : Array.Empty<string>();
    }

    /// <summary>
    /// Whether a chapter's stored SFDT is something the converter can turn into a document at all.
    ///
    /// <c>Chapter.ContentSfdt</c> DEFAULTS to the literal <c>"{}"</c> - what a chapter created through
    /// <c>POST /chapters</c> carries until the editor first saves it. Syncfusion cannot load that, so before
    /// this guard ONE untouched chapter turned the whole book's export into a 500: a book-level failure
    /// caused by a chapter the author had not started writing.
    ///
    /// THE EMPTY-DOCUMENT FAMILY, NOT ONE LITERAL. Matching only <c>"{}"</c> closed one member of a family
    /// and left its neighbours open: <c>{"sections":[]}</c> and <c>{"sections":[{"blocks":[]}]}</c> - the
    /// latter being the shape <see cref="SceneService"/> writes as its own empty default - both reach
    /// Syncfusion and throw <c>KeyNotFoundException("sfdt")</c>, which is the identical whole-book 500 the
    /// guard was written to close, one shape over. So the question asked here is structural: does this
    /// document contain a single block anywhere?
    ///
    /// STILL DELIBERATELY NARROW, because the failure modes are not symmetrical. Dropping a chapter that DOES
    /// have content is data loss in the author's manuscript; throwing on a corrupt one is a fault they need to
    /// hear about. So anything that is not recognizably an empty SFDT document is reported as renderable and
    /// allowed to throw: a value that is not JSON, a JSON root that is not an object, a document with no
    /// <c>sections</c> property at all, or a <c>sections</c>/<c>blocks</c> that is not an array. Only a
    /// well-formed document whose every section is blockless is treated as empty.
    /// </summary>
    internal static bool HasRenderableContent(string? contentSfdt)
    {
        if (string.IsNullOrWhiteSpace(contentSfdt)) return false;

        var trimmed = contentSfdt.Trim();
        // The entity default, kept as its own case: it is a JSON object with no "sections" property, which the
        // structural walk below deliberately treats as unrecognized-and-therefore-loud.
        if (trimmed == "{}") return false;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return true; // Not JSON at all: a fault to surface, not a chapter to drop.
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return true;
            if (!root.TryGetProperty("sections", out var sections)) return true;
            if (sections.ValueKind != JsonValueKind.Array) return true;

            foreach (var section in sections.EnumerateArray())
            {
                if (section.ValueKind != JsonValueKind.Object) return true;
                // A section with no "blocks" at all is the same empty shape one level down, not a fault.
                if (!section.TryGetProperty("blocks", out var blocks)) continue;
                if (blocks.ValueKind != JsonValueKind.Array) return true;
                if (blocks.GetArrayLength() > 0) return true;
            }

            return false;
        }
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
