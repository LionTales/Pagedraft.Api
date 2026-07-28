using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Book-level intelligence: summarize chapters, build a BookProfile
/// (genre, synopsis, characters, story structure), and answer Q&A.
///
/// Invalidation strategy:
///   - Each ChunkSummary records CreatedAt.
///   - On RefreshProfileAsync, compare Chapter.UpdatedAt vs ChunkSummary.CreatedAt.
///   - Only re-summarize chapters where UpdatedAt > ChunkSummary.CreatedAt (stale).
///   - After re-summarizing stale chapters, rebuild the full BookProfile.
///
/// FILE-SIZE WAIVER (CLAUDE.md's ~700-line soft ceiling) - recorded by f2 (2026-07-28), with the numbers
/// MEASURED rather than quoted, because the c2 report that recommended this waiver quoted them wrong. A first
/// pass at this very correction made the identical mistake: it counted the file before this waiver block was
/// fully written, so it under-reported the total by the length of the block being added.
///
/// MEASURED, not asserted: this file is <b>874</b> lines (re-measured by the CLOSING review, after be-f04 also
/// edited this file; be-f03's own count of 868 went stale the same way c2's did). c2 (the two-stage profile
/// repair hook) reported the change as "668 -> 760"; the real figures are <b>734 -> 874</b> (`git show
/// HEAD:...` and `wc -l`; `git diff --numstat` shows +170/-30 against HEAD; 734 + 170 - 30 = 874), so c2
/// understated both ends. A line count written INSIDE the file it counts is stale the moment anything else in
/// the file moves - read it as "over the ceiling by roughly this much", never as a current figure. Most of the
/// lines added since HEAD are
/// doc comment, not code - c2's repair-hook doc comment plus this waiver paragraph itself (which grew again
/// when it had to correct its own earlier miscount). The correction matters for one reason beyond arithmetic:
/// <b>the file was ALREADY 34 lines over the ceiling BEFORE c2</b>, so this is not a c2-created breach that a
/// c2-sized extraction would fix - the file was already past the ceiling before c2 touched it.
///
/// WHY IT IS NOT SPLIT ANYWAY. The single caller of the repair hook is <c>BuildBookProfileAsync</c>, in this
/// file, and most of the lines added since HEAD are DOC COMMENT, not code - the no-swap rationale
/// (CharacterAnalysis / StoryAnalysis route to LinguisticAnalysis -> gemma4:12b, the same model TermRepair
/// resolves, so this hook adds no cross-model GPU-swap surface), the two gates (layer gate then Mode gate),
/// and the fail-safe contract. Extracting a one-caller helper to move prose out of a line count would separate
/// that rationale from the build it explains, which is the exact failure mode this subsystem has shipped
/// before (a rule stated far from the code it governs stops being read).
///
/// WHAT A REAL SPLIT WOULD TAKE, if the ceiling is enforced later: the natural seam is CHAPTER SUMMARIZATION
/// (<c>SummarizeChaptersAsync</c> / <c>SummarizeChaptersCoreAsync</c> and the be-c03 checkpoint-window logic)
/// versus PROFILE BUILD (<c>BuildBookProfileAsync</c> + the repair hook) versus Q&amp;A. Those three share only
/// the injected state, not control flow - unlike <see cref="BookReviewService"/>, whose remaining bulk is ONE
/// sequenced pipeline. That makes this file the EASIER of the two to split, and it is deferred on cost, not on
/// impossibility. Do not record a "pre-existing waiver" for it without pointing at this paragraph: a claimed
/// waiver that nobody can find is the false-invariant-comment class be-c09 already had to clean up in
/// <see cref="BookReviewService"/>.
/// </summary>
public class BookIntelligenceService
{
    private readonly AppDbContext _db;
    private readonly UnifiedAnalysisService _analysis;
    private readonly BookContextAssembler _bookContextAssembler;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IBookEntityProvider _bookEntities;
    private readonly DynamicTermRepairService _dynamicTermRepair;
    private readonly ILogger<BookIntelligenceService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BookIntelligenceService(
        AppDbContext db,
        UnifiedAnalysisService analysis,
        BookContextAssembler bookContextAssembler,
        IOptions<AiOptions> aiOptions,
        IBookEntityProvider bookEntities,
        DynamicTermRepairService dynamicTermRepair,
        ILogger<BookIntelligenceService> logger)
    {
        _db = db;
        _analysis = analysis;
        _bookContextAssembler = bookContextAssembler;
        _aiOptions = aiOptions;
        _bookEntities = bookEntities;
        _dynamicTermRepair = dynamicTermRepair;
        _logger = logger;
    }

    // ─── Chapter Summarization ──────────────────────────────────────

    /// <summary>
    /// Summarize all chapters of the book. Skips chapters that already have
    /// a fresh (non-stale) ChunkSummary.
    /// </summary>
    public Task SummarizeChaptersAsync(Guid bookId, string language, CancellationToken ct = default)
        => SummarizeChaptersCoreAsync(bookId, language, ct);

    /// <summary>
    /// What ONE chapter's batched summarization produced. The pre-repair / post-repair PAIR is the point:
    /// the repair layer is span-scoped, so the only way to assert "over-rewrite 0" (everything OUTSIDE the
    /// repaired spans is byte-identical) is to hold both halves of the same run at once — and until this
    /// batch flow existed, the pre-repair value was observable NOWHERE (DynamicTermRepairService.LogSpan
    /// deliberately logs offsets/latency only: "NO run text / replacement / value is ever logged").
    ///
    /// This seam exists so a DETERMINISTIC TEST can make that assertion. It is a return value, NOT a log
    /// line: the no-content-in-logs rule stands, and nothing here is written to any log at any level.
    /// </summary>
    /// <param name="RepairFaulted">
    /// True when the repair pass threw for THIS chapter and the un-repaired text was persisted as the
    /// fail-safe (non-negotiable (v)); the other chapters are unaffected.
    /// </param>
    /// <param name="SummarizedAt">
    /// When THIS chapter's own summary was produced - captured in phase 1 immediately BEFORE its model call,
    /// and written to the row's <see cref="ChunkSummary.CreatedAt"/> in phase 3 (be-c01). Deliberately NOT the
    /// persist time: phase 3 runs only after every chapter IN THIS CHAPTER'S CHECKPOINT WINDOW has been
    /// summarized and repaired (be-c03 bounded that to K chapters, which is still minutes of real time on a
    /// full window), so a persist-time stamp would mark a chapter that the user EDITED DURING THE PASS as
    /// fresh (CreatedAt > UpdatedAt) and strand it forever with a summary of its pre-edit text - no automatic
    /// pass would ever rebuild it. Captured BEFORE the model call rather than after, so the stamp is
    /// conservatively STALE with respect to any edit that lands mid-call and correctly re-triggers a
    /// re-summary; a stamp taken after the call could still swallow such an edit.
    /// </param>
    internal sealed record ChapterSummaryOutcome(
        Guid ChapterId,
        string PreRepairSummary,
        string PersistedSummary,
        bool RepairFaulted,
        DateTimeOffset SummarizedAt);

    /// <summary>Why a chapter was skipped in phase 1, before any model call. Three structurally different
    /// guards feed the same list, and a test asserting on the list alone cannot tell which one fired - this
    /// carries the reason so it can.</summary>
    internal enum ChapterSkipReason
    {
        /// <summary>The chapter's ContentText is empty or whitespace-only.</summary>
        NoContent,

        /// <summary>The existing summary row is fresh: CreatedAt >= Chapter.UpdatedAt.</summary>
        Fresh,

        /// <summary>wb3-c04 clobber guard: the existing row's SummaryUserEdited is set.</summary>
        UserEdited,

        /// <summary>
        /// be-c02: the wb3-c04 clobber guard fired at the PHASE 3 RE-CHECK, not in phase 1 - the row was
        /// clean when this chapter was picked up and the user edited it (PUT .../summary) WHILE the batch was
        /// running. Deliberately a DISTINCT reason from <see cref="UserEdited"/>, because the two differ in
        /// cost and in what they say about the pass: a phase-1 skip paid for NO model call, whereas this
        /// chapter was fully summarized AND repaired and then deliberately not persisted. Folding them into
        /// one value would make "skipped == cost nothing" (asserted by the batching tests) ambiguous.
        /// </summary>
        UserEditedDuringPass
    }

    /// <summary>One chapter that this pass left alone, and why. Most reasons are decided in phase 1 before
    /// any model call; <see cref="ChapterSkipReason.UserEditedDuringPass"/> is decided at the phase 3
    /// re-check, after the work was already done.</summary>
    internal sealed record SkippedChapter(Guid ChapterId, ChapterSkipReason Reason);

    /// <summary>Outcome of one batched <see cref="SummarizeChaptersAsync"/> pass. Test seam; see
    /// <see cref="ChapterSummaryOutcome"/>.</summary>
    /// <param name="Summarized">
    /// The chapters whose summary this pass actually PERSISTED. A chapter that was summarized and repaired
    /// but then withheld by the phase 3 re-check (be-c02) is NOT here - it is in <paramref name="Skipped"/>
    /// as <see cref="ChapterSkipReason.UserEditedDuringPass"/>. Every member carries a
    /// <see cref="ChapterSummaryOutcome.PersistedSummary"/>, so listing a withheld chapter here would make
    /// that field a lie, and every consumer reads this list as "what this pass wrote".
    /// </param>
    internal sealed record ChapterSummaryBatchOutcome(
        IReadOnlyList<ChapterSummaryOutcome> Summarized,
        IReadOnlyList<SkippedChapter> Skipped);

    /// <summary>
    /// The real body of <see cref="SummarizeChaptersAsync"/>. The book's chapters are split into CHECKPOINT
    /// WINDOWS of K (Ai:AnalysisRepair.SummaryBatchWindowChapters, default 10), and each window runs the same
    /// THREE PHASES so the repair model loads at most ONCE PER WINDOW instead of once per leaking chapter.
    ///
    ///   Phase 1 - summarize every eligible chapter IN THIS WINDOW with the repair DEFERRED, so the
    ///             Summarization model (qwen3.5:9b) stays resident across the window.
    ///   Phase 2 - ONE repair pass over the window's buffered summaries, so the TermRepair model
    ///             (gemma4:12b) loads at most once for the window. The calls are DEFERRED, never MERGED:
    ///             each foreign run still gets its own isolated span-scoped model call with the identical
    ///             prompt and the identical validation-by-re-detect (see
    ///             UnifiedAnalysisService.CompleteDeferredRepairAsync), so repair quality is unchanged BY
    ///             CONSTRUCTION. Concatenating several chapters' prose into one call would be a different
    ///             feature with a content-loss / cross-contamination blast radius the span-scope design
    ///             currently makes impossible.
    ///   Phase 3 - persist the window, in ONE SaveChanges.
    ///
    /// WHY the batching matters: the dynamic repair routes to Ai:FeatureModels:TermRepair (gemma4:12b) while
    /// Summarization routes to qwen3.5:9b. On a single-GPU host with OLLAMA_MAX_LOADED_MODELS=1 every
    /// leaking chapter used to EVICT the summarization model and cold-load the ~7 GB repair model (measured
    /// ~21-23 s), then the next chapter evicted it back. K leaking chapters cost K swaps; now a window costs
    /// at most one, and a window whose chapters all come back CLEAN costs none at all (a clean analysis makes
    /// no repair model call - docs/ANALYSIS_OUTPUT_REPAIR.md section 19.1).
    ///
    /// PERSIST BOUNDARY, and WHY IT IS WINDOWED (be-c03). Within a window nothing is written until every one
    /// of its summaries has been through the repair pass, because persisting raw prose first and repairing it
    /// later would leave leaked English persisted forever if the process died in between. That invariant is
    /// absolute and the window does not weaken it: a window persists only AFTER its own phase 2.
    ///
    /// What the window DOES bound is LIVENESS, which the original single-commit boundary did not. That
    /// boundary was priced as "acceptable because the freshness guard makes a re-run idempotent, so it is
    /// never a correctness loss, only repeated work". That is true of CORRECTNESS and false of PROGRESS:
    /// repeated work that is aborted at the SAME POINT every time never converges. This method is awaited
    /// INLINE on the request thread with the REQUEST token (BooksController.Summarize / RefreshProfile), the
    /// measured cost is ~18-27 s per chapter (section 19), and this project's own corpus contains an
    /// 80-chapter book - so a first pass over it is a 24-37 minute single request. Under one commit, a client
    /// reload, a gateway idle ceiling, or the OOM-wedged Ollama runner this host has actually produced
    /// (HTTP 500 after ~30 minutes) discarded ALL 80 chapters, repeatably. With a window of K, at most K
    /// chapters' work is ever at risk and every completed window is durable, so the retry makes real progress.
    ///
    /// Setting K at or above the chapter count reproduces the original single-commit behaviour exactly.
    ///
    /// CORRELATION: every repaired summary returns to its own chapter by ENTITY IDENTITY (the ChapterId
    /// carried on each buffered item and used to key the existing-row lookup), never by list index and never
    /// by content match - the repair REWRITES the content, so a content match would be unsound.
    /// </summary>
    internal async Task<ChapterSummaryBatchOutcome> SummarizeChaptersCoreAsync(
        Guid bookId, string language, CancellationToken ct = default)
    {
        // Persist the flat summary under the NORMALIZED locale (e.g. "en-US" → "en"), the SAME key the
        // structured path (ChapterBriefService) and the BookContextAssembler read by. Storing the raw request
        // value here is what let the assembler's exact-match selection skip the summary and degrade to raw
        // chapter text. Normalize is idempotent for already-normalized values (e.g. "he" → "he").
        var lang = BaselineLanguageResolver.Normalize(language);

        var chapters = await _db.Chapters
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .ToListAsync(ct);

        if (chapters.Count == 0)
            throw new InvalidOperationException("Book has no chapters.");

        var existingSummaries = await _db.Set<ChunkSummary>()
            .Where(cs => cs.BookId == bookId)
            .ToDictionaryAsync(cs => cs.ChapterId, ct);

        // ── Checkpoint windows (be-c03) ───────────────────────────────────────────────────────────────────
        // The chapters are already ordered by Order, and Chunk PARTITIONS them, so no chapter appears in two
        // windows and the whole book is still covered exactly once. Each window is an independent
        // summarize -> repair -> persist unit, run in order; a fault in window N leaves windows 0..N-1
        // committed and the freshness guard then makes the retry skip them.
        var windowSize = ResolveSummaryBatchWindow();
        var persistedAll = new List<ChapterSummaryOutcome>(chapters.Count);
        var skipped = new List<SkippedChapter>();

        foreach (var window in chapters.Chunk(windowSize))
        {
            var windowPersisted = await SummarizeOneWindowAsync(
                bookId, lang, language, window, existingSummaries, skipped, ct);
            persistedAll.AddRange(windowPersisted);
        }

        return new ChapterSummaryBatchOutcome(persistedAll, skipped);
    }

    /// <summary>
    /// How many chapters form ONE checkpoint window. Reads
    /// <see cref="AnalysisRepairOptions.SummaryBatchWindowChapters"/>, falling back to the class default when
    /// the whole <c>Ai:AnalysisRepair</c> section is absent (which is the shape every programmatic/test
    /// construction takes). A non-positive configured value is CLAMPED to the default rather than treated as
    /// "no windowing" - mirroring the Ollama TimeoutMinutes idiom in Program.cs - because "unwindowed" is
    /// expressible as a large number, whereas a stray 0 must not silently restore the all-or-nothing persist
    /// this setting exists to bound.
    /// </summary>
    private int ResolveSummaryBatchWindow()
    {
        var configured = _aiOptions.Value.AnalysisRepair?.SummaryBatchWindowChapters
                         ?? AnalysisRepairOptions.DefaultSummaryBatchWindowChapters;
        return configured > 0 ? configured : AnalysisRepairOptions.DefaultSummaryBatchWindowChapters;
    }

    /// <summary>
    /// The three phases for ONE checkpoint window. This is the unit that satisfies the non-negotiable
    /// invariant: the window's phase 3 persist runs only AFTER the window's own phase 2 repair pass, so
    /// un-repaired prose is never written, not even transiently.
    /// </summary>
    /// <param name="lang">The NORMALIZED locale actually written to the row.</param>
    /// <param name="language">The RAW caller-supplied locale, passed on to the analysis layer unchanged
    /// (it resolves the prompt/expected-script from it); do not substitute <paramref name="lang"/> here.</param>
    /// <param name="existingSummaries">The pass-wide phase-1 snapshot of the book's ChunkSummary rows, shared
    /// by every window. Windows PARTITION the chapters, so no two windows read the same entry.</param>
    /// <param name="skipped">The pass-wide skip list, appended to by every window.</param>
    /// <returns>The chapters this window actually PERSISTED.</returns>
    private async Task<List<ChapterSummaryOutcome>> SummarizeOneWindowAsync(
        Guid bookId,
        string lang,
        string language,
        IReadOnlyList<Chapter> chapters,
        Dictionary<Guid, ChunkSummary> existingSummaries,
        List<SkippedChapter> skipped,
        CancellationToken ct)
    {
        // ── Phase 1: summarize every eligible chapter in this window, repair DEFERRED ─────────────────────
        // Both skip guards stay exactly where they were: evaluated BEFORE the chapter is summarized, in the
        // same order, so a fresh or user-edited row still costs zero model calls.
        var pending = new List<(
            Guid ChapterId, UnifiedAnalysisService.DeferredRepairRawRun Raw, DateTimeOffset SummarizedAt)>();

        foreach (var chapter in chapters)
        {
            var text = chapter.ContentText?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                skipped.Add(new SkippedChapter(chapter.Id, ChapterSkipReason.NoContent));
                continue;
            }

            if (existingSummaries.TryGetValue(chapter.Id, out var existing) &&
                existing.CreatedAt >= chapter.UpdatedAt)
            {
                _logger.LogDebug("Chapter {ChapterId} summary is fresh, skipping", chapter.Id);
                skipped.Add(new SkippedChapter(chapter.Id, ChapterSkipReason.Fresh));
                continue;
            }

            // wb3-c04 clobber guard: the user has manually edited THIS chapter's flat SummaryText, which is
            // their own authoritative understanding of the chapter. The automatic re-summary must NOT silently
            // overwrite it — even when the chapter text changed and the AI freshness check above would
            // otherwise re-run. Skip the row; a deliberate overwrite is only reachable through the explicit
            // re-derive action the user triggers (which consumes the edit rather than discarding it). This is
            // logged at WARN so a skipped re-summary is never silent.
            if (existing != null && existing.SummaryUserEdited)
            {
                _logger.LogWarning(
                    "Chapter {ChapterId} flat summary is user-edited; skipping automatic re-summary to " +
                    "preserve the user's manual edit (re-derive must be user-triggered).", chapter.Id);
                skipped.Add(new SkippedChapter(chapter.Id, ChapterSkipReason.UserEdited));
                continue;
            }

            _logger.LogInformation("Summarizing chapter {Title} ({Id})", chapter.Title, chapter.Id);

            // be-c01: the FRESHNESS STAMP is taken HERE - anchored to this chapter's own summarize time, not
            // to the batch's persist time - and carried to phase 3 below. Taken BEFORE the model call on
            // purpose: an edit that lands while the call is in flight must leave the stamp STALE relative to
            // Chapter.UpdatedAt so the next pass re-summarizes. A stamp taken after the call (or, worse, at
            // persist time) would be NEWER than that edit and would silently classify the chapter fresh
            // forever, pinning it to a summary of text the user has already replaced.
            var summarizedAt = DateTimeOffset.UtcNow;

            // be-c02: pass the REAL bookId — the raw seam feeds it to the repair layer's per-book proper-noun
            // LEAVE set, so this book's own character/place names are spared in the summary we persist below.
            var raw = await _analysis.RunRawDeferredRepairAsync(
                text, AnalysisType.Summarization, null, language, bookId, ct);

            // Buffered in memory, keyed by the chapter's own id. Summaries are ~1 KB, so even an 80-chapter
            // book is trivial to hold.
            pending.Add((chapter.Id, raw, summarizedAt));
        }

        // ── Phase 2: ONE repair pass ──────────────────────────────────────────────────────────────────────
        var outcomes = new List<ChapterSummaryOutcome>(pending.Count);
        foreach (var (chapterId, raw, summarizedAt) in pending)
        {
            string repaired;
            var faulted = false;
            try
            {
                repaired = await _analysis.CompleteDeferredRepairAsync(raw, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // PER-CHAPTER FAIL-SAFE (non-negotiable (v)). The invariant is load-bearing and specific to
                // batching: a fault on ONE chapter must leave THAT chapter un-repaired and keep going, and
                // must never cost the OTHER chapters of this window their summaries. Before batching a throw
                // here lost only the current chapter because earlier ones were already persisted; now the
                // whole window is still in flight, so swallowing per chapter is what preserves that.
                //
                // WHAT ACTUALLY REACHES THIS CATCH (be-c04 - the comment that used to sit here was WRONG
                // about it, so it is spelled out rather than restated). Every layer below is catch-all
                // fail-safe, and `catch (Exception)` INCLUDES OperationCanceledException: the per-span router
                // call (DynamicTermRepairService, "keeping original span (fail-safe)") and its two outer
                // wraps, AnalysisRepairService.RepairAnalysisAsync, and BOTH stage try/catch blocks in
                // UnifiedAnalysisService.ApplyAnalysisRepairAsync. So a timeout - or even a genuine cancel -
                // raised inside a repair model call never arrives here; it is absorbed down there and the
                // chapter simply comes back un-repaired. What can arrive here is a fault from the seams
                // OUTSIDE those blocks, which today is the `_aiOptions.Value` read at the top of
                // ApplyAnalysisRepairAsync (and, for LineEdit only, its ResultText re-derivation).
                //
                // WHY THE FILTER IS NOT A BARE `ex is not OperationCanceledException` (be-c04). HttpClient
                // surfaces its OWN timeout as TaskCanceledException, which IS an OperationCanceledException.
                // A bare type test therefore classifies a repair-side TIMEOUT as a caller cancellation and
                // aborts the whole window - the exact opposite of what this fail-safe exists to do. The TOKEN
                // is the discriminator: if OUR ct is not cancelled then nobody asked us to stop, so the
                // OperationCanceledException is a repair fault and degrades this ONE chapter to un-repaired.
                // If our ct IS cancelled it propagates untouched, and the window in flight persists nothing
                // because its phase 3 never runs. Windows already committed stay committed - that is be-c03's
                // checkpoint guarantee, and it is never a leak of un-repaired prose (each window persists
                // only after its OWN repair pass).
                //
                // RESIDUAL, deliberately left in place: because the layers below swallow cancellation too, a
                // caller cancel landing INSIDE a repair call is not seen here at all - the window runs to its
                // end emitting one fail-safe WARN per remaining chapter and only aborts at the phase-3
                // SaveChangesAsync(ct). Narrowing those catches is out of scope here: they are the "the repair
                // layer can NEVER throw into RunAsync" invariant that RunAsync, streaming and chunked LineEdit
                // all rely on (recorded as a follow-up in the be-c04 findings note on the plan).
                _logger.LogWarning(ex,
                    "Batched summary repair threw for chapter {ChapterId}; persisting the un-repaired " +
                    "summary for this chapter and continuing with the rest (fail-safe).", chapterId);
                repaired = raw.UnrepairedText;
                faulted = true;
            }

            outcomes.Add(new ChapterSummaryOutcome(
                chapterId, raw.UnrepairedText, repaired, faulted, summarizedAt));
        }

        // ── Phase 3: persist THIS WINDOW ──────────────────────────────────────────────────────────────────
        // ChunkSummary DUAL-SURFACE contract: this row is shared with the structured writer
        // (ChapterBriefService, which owns StructuredJson / StructuredBuiltAt / BuiltWithModel) and with the
        // user-edit writer (which owns SummaryUserEdited / SummaryUserEditedAt). The flat path writes exactly
        // three columns — SummaryText, Language, CreatedAt — and touches no structured or user-edit column, so
        // neither companion surface's locale or freshness is orphaned.
        //
        // CreatedAt IS NOT "now" (be-c01). Batching moved this write minutes away from the model call that
        // produced the text, so the stamp is the chapter's OWN summarize time (outcome.SummarizedAt, captured
        // in phase 1 before its model call), NOT the persist time. Stamping "now" here would hand a chapter
        // that the user edited DURING the pass a CreatedAt later than its Chapter.UpdatedAt, and the freshness
        // guard above (CreatedAt >= UpdatedAt) would then classify it fresh PERMANENTLY, leaving it pinned to
        // a summary of the pre-edit text that no automatic pass would ever rebuild. An earlier stamp is the
        // conservative direction: at worst it costs one redundant re-summary, and it is safe for every other
        // reader of this column (the structured surfaces gate on StructuredBuiltAt, never on CreatedAt).
        //
        // be-c02 RE-CHECK AGAINST DATABASE TRUTH. The wb3-c04 clobber guard is READ in phase 1 and the write
        // it guards happens HERE, so batching stretched its check-to-act window from one chapter's model call
        // to a whole WINDOW (be-c03 bounded it to K chapters; before that it was the whole book's pass, and
        // it is still minutes of real time on a full window). ChunkSummary carries NO concurrency token, so the
        // tracked phase-1 entity would be written back over whatever the PUT-summary path
        // (BooksController.UpdateChapterSummary) committed in between - and TWO things would be lost at once:
        // the user's manual text, AND the truth of the SummaryUserEdited flag, because this path never writes
        // that column. The row would then CLAIM to hold a manual edit while holding machine text, after which
        // the guard protects the MACHINE text forever and the user's edit is unrecoverable, with no signal
        // anywhere. So the guard is re-evaluated against the DB immediately before the write loop.
        //
        // The re-read is AsNoTracking on purpose: a TRACKING query would hand back the already-tracked
        // phase-1 instances unchanged (identity resolution keeps the tracked values), i.e. exactly the stale
        // snapshot this re-check exists to bypass. The concurrent writer runs in its own request-scoped
        // DbContext, so the DB - never our tracker - is the only truth available here.
        //
        // WHY PREFERRING THE DB IS SAFE, the non-obvious part: at this line the phase-1 entities carry NO
        // pending modifications - the first assignment to any of them is inside the loop below - so nothing
        // of OURS can be discarded by deciding on the DB's answer. And the write below touches only the three
        // flat columns, so the stale values the tracked instance still holds for every other column are never
        // sent (EF issues an UPDATE for modified properties only) and cannot clobber the concurrent writer's
        // structured or user-edit columns.
        var persisted = new List<ChapterSummaryOutcome>(outcomes.Count);
        if (outcomes.Count > 0)
        {
            var outcomeChapterIds = outcomes.Select(o => o.ChapterId).ToList();
            var liveRows = await _db.Set<ChunkSummary>()
                .AsNoTracking()
                .Where(cs => cs.BookId == bookId && outcomeChapterIds.Contains(cs.ChapterId))
                .ToDictionaryAsync(cs => cs.ChapterId, ct);

            foreach (var outcome in outcomes)
            {
                // Correlation by ENTITY IDENTITY: the buffered ChapterId keys the existing-row lookup. No
                // index, no ordering, no content match.
                liveRows.TryGetValue(outcome.ChapterId, out var live);

                if (live is { SummaryUserEdited: true })
                {
                    // Same guard, same WARN as phase 1 - the user edited this chapter DURING the pass. The
                    // summary + repair are discarded: they describe text the user has since spoken for, and
                    // their cost is already sunk. Never silent (this is the branch that was losing content).
                    _logger.LogWarning(
                        "Chapter {ChapterId} flat summary was user-edited WHILE this batch was running; " +
                        "discarding the batch's summary for it to preserve the user's manual edit " +
                        "(re-derive must be user-triggered).", outcome.ChapterId);
                    skipped.Add(new SkippedChapter(outcome.ChapterId, ChapterSkipReason.UserEditedDuringPass));
                    continue;
                }

                // THE INSERT-BRANCH MIRROR. A chapter with no row in phase 1 can have one by now: the
                // PUT-summary path INSERTS a row (BooksController.UpdateChapterSummary, the row == null
                // branch) for a chapter that was never summarized. ChunkSummary has a UNIQUE index on
                // (BookId, ChapterId) - AppDbContext.cs, modelBuilder.Entity<ChunkSummary> - so a blind Add
                // here would fail the whole SaveChanges and lose the ENTIRE batch, not just this chapter.
                // A user-created row is caught by the guard above; a row created by the STRUCTURED writer
                // (ChapterBriefService) is not user-edited and correctly takes the update branch below, since
                // the flat surface it left empty is exactly what this pass produces.
                var existing = existingSummaries.GetValueOrDefault(outcome.ChapterId);
                if (existing == null && live != null)
                    existing = await _db.Set<ChunkSummary>().FindAsync(new object?[] { live.Id }, ct);

                if (existing != null)
                {
                    existing.SummaryText = outcome.PersistedSummary;
                    existing.Language = lang;
                    existing.CreatedAt = outcome.SummarizedAt;
                }
                else
                {
                    _db.Set<ChunkSummary>().Add(new ChunkSummary
                    {
                        BookId = bookId,
                        ChapterId = outcome.ChapterId,
                        SummaryText = outcome.PersistedSummary,
                        Language = lang,
                        // EXPLICIT on the insert too. Left unset, AppDbContext.SaveChangesAsync stamps an
                        // Added ChunkSummary with UtcNow at PERSIST time - the exact defect be-c01 fixes,
                        // just via the default instead of an assignment. The override now defers to a
                        // caller-supplied value, so a brand-new row gets the same summarize-time anchor an
                        // updated row does.
                        CreatedAt = outcome.SummarizedAt
                    });
                }

                persisted.Add(outcome);
            }
        }

        // Gated on what was actually WRITTEN, not on what was summarized: a window whose every outcome was
        // withheld by the re-check above must issue no SaveChanges at all, exactly like an all-skipped window.
        //
        // THIS IS THE CHECKPOINT (be-c03). It runs per window, AFTER the window's own phase 2, so the two
        // properties hold together: nothing un-repaired is ever persisted, AND the chapters completed so far
        // survive an abort of a later window. Deliberately NOT deferred to a single save at the end of the
        // pass - that is exactly the all-or-nothing boundary this todo removed.
        if (persisted.Count > 0)
            await _db.SaveChangesAsync(ct);

        return persisted;
    }

    // ─── Build / Refresh Profile ────────────────────────────────────

    /// <summary>
    /// Build a complete BookProfile from chapter summaries.
    /// Runs BookOverview, Synopsis, CharacterAnalysis, and StoryAnalysis prompts
    /// against the concatenated summaries.
    /// </summary>
    public async Task<BookProfile> BuildBookProfileAsync(Guid bookId, string language, CancellationToken ct = default)
    {
        // wb1-c03: route through the SHARED budget-aware assembler instead of the unguarded flat-summary
        // concat (GetConcatenatedSummaries appended every chapter summary with no size guard, overflowing
        // the model context on a large book). The assembler prefers the dense structured BookBrief +
        // ChapterBriefs and degrades to a budget-guarded flat-summary concat when no briefs are built yet;
        // either way it stays within the NumCtx-derived budget and logs anything it drops.
        // Budget the assembly to the SMALLEST window across the tasks that consume this SAME text below:
        // BookOverview / CharacterAnalysis / StoryAnalysis route to LinguisticAnalysis and Synopsis to
        // Summarization, so the context must fit whichever has the tighter num_ctx (Bug 3: budgeting against
        // Summarization alone could overflow the LinguisticAnalysis window when it is configured smaller).
        var assembly = await _bookContextAssembler.AssembleAsync(
            bookId, language,
            new[] { AiTaskType.LinguisticAnalysis, AiTaskType.Summarization }, ct);
        var concatenated = assembly.Text;
        if (string.IsNullOrWhiteSpace(concatenated))
            throw new InvalidOperationException("No chapter summaries found. Run SummarizeChaptersAsync first.");

        _logger.LogInformation(
            "Building book profile for {BookId} (structuredBriefs={Used}, dropped={Dropped}/{Budget}t)",
            bookId, assembly.UsedStructuredBriefs, assembly.DroppedCount, assembly.BudgetTokens);

        // be-c02: pass the REAL bookId on every raw run (same seam parity as SummarizeChaptersAsync above).
        var overviewTask = _analysis.RunRawAsync(concatenated, AnalysisType.BookOverview, null, language, bookId, ct);
        var synopsisTask = _analysis.RunRawAsync(concatenated, AnalysisType.Synopsis, null, language, bookId, ct);
        var charsTask = _analysis.RunRawAsync(concatenated, AnalysisType.CharacterAnalysis, null, language, bookId, ct);
        var storyTask = _analysis.RunRawAsync(concatenated, AnalysisType.StoryAnalysis, null, language, bookId, ct);

        await Task.WhenAll(overviewTask, synopsisTask, charsTask, storyTask);

        var overview = TryDeserialize<BookOverviewResult>(overviewTask.Result);
        var profile = await GetOrCreateProfile(bookId, ct);

        profile.Genre = overview?.Genre;
        profile.SubGenre = overview?.SubGenre;
        profile.TargetAudience = overview?.TargetAudience;
        profile.LiteratureLevel = overview?.LiteratureLevel;
        profile.LanguageRegister = overview?.LanguageRegister;
        profile.Synopsis = synopsisTask.Result;

        // f5-wire coverage fix: BuildBookProfileAsync is the ONLY producer of CharacterAnalysis / StoryAnalysis,
        // and it runs them through UnifiedAnalysisService.RunRawAsync(structuredJson: null) — for those types
        // ApplyAnalysisRepairAsync (and GlossaryRepairPass.Apply) is a STRICT NO-OP, so the shipped deterministic
        // glossary that cleans leaked English in Hebrew prose never reached the persisted profile JSON. BookReview
        // has the same "never reaches the RunAsync seam" property and solved it with an ENGINE HOOK
        // (BookReviewService.ApplyGlossaryToFindings); mirror that here. Deserialize the (fence-tolerant) raw model
        // JSON, run the SAME glossary over the whitelisted prose fields IN PLACE, and store CLEAN reserialized JSON.
        // Gated on the repair layer (Enabled + PerType-allows the type) and Hebrew (enforced inside RepairFields);
        // fail-safe — an off gate, an unparseable payload, or ANY repair fault stores the raw string unchanged, so
        // a repair fault can NEVER break the profile build. This hook only needs to repair CharactersJson +
        // StoryStructureJson: Synopsis is already covered upstream, by the plain-text dispatch arm on the
        // RunRawAsync seam that produced it (f2), and BookOverview.Summary is discarded before persistence
        // (only the five label fields are persisted, see the assignments above).
        //
        // c2: the hook is now TWO-stage (glossary THEN dynamic), the same shape BookReviewService's engine hook
        // has (glossary 5b / dynamic 5c) — see RepairStructuredProfileJsonAsync for the full rationale. Awaited
        // sequentially rather than via Task.WhenAll: the dynamic stage makes span-scoped model calls, and the
        // single-GPU host serves them one at a time anyway.
        var repairCfg = _aiOptions.Value.AnalysisRepair;
        profile.CharactersJson = await RepairStructuredProfileJsonAsync<CharacterAnalysisResult>(
            charsTask.Result, language, AnalysisType.CharacterAnalysis, repairCfg, RepairableFields.For,
            bookId, _logger, ct);
        profile.StoryStructureJson = await RepairStructuredProfileJsonAsync<StoryAnalysisResult>(
            storyTask.Result, language, AnalysisType.StoryAnalysis, repairCfg, RepairableFields.For,
            bookId, _logger, ct);

        profile.Language = language;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        // be-c03: this build just PRODUCED a harvest source for the per-book proper-noun LEAVE set —
        // BookProfile.CharactersJson (a serialized CharacterAnalysisResult). Without this invalidation the
        // ordinary production sequence defeats that source outright: the first chapter analysis on a fresh book
        // builds the entity set from the manuscript alone (no character names exist yet), and the names this
        // build persists would never enter the set. Drop the cached set so the NEXT analysis rebuilds it with
        // this book's character names in it — a name the set fails to spare is a name the repair model rewrites.
        // Invalidate is non-throwing by contract, so it can never break the profile persist above.
        _bookEntities.Invalidate(bookId);

        _logger.LogInformation("Book profile built for {BookId}", bookId);
        return profile;
    }

    /// <summary>
    /// Refresh: re-summarize stale chapters, then rebuild the profile.
    /// </summary>
    public async Task<BookProfile> RefreshProfileAsync(Guid bookId, string language, CancellationToken ct = default)
    {
        await SummarizeChaptersAsync(bookId, language, ct);
        return await BuildBookProfileAsync(bookId, language, ct);
    }

    // ─── Q&A ────────────────────────────────────────────────────────

    /// <summary>
    /// Answer a question about the book using chapter summaries as context.
    /// Persists the result as an AnalysisResult with Scope=Book, Type=QA.
    /// </summary>
    public async Task<AnalysisResult> AskAsync(Guid bookId, string question, string language, CancellationToken ct = default)
    {
        // wb1-c03: budget-aware assembly (shared path) instead of the unguarded summary concat. The question
        // is appended AFTER the budgeted context, so the context can never push total input past the model
        // window on its own (the assembler already capped it). Anything dropped is logged in the assembler.
        // Budget to the QA route's task (GenericChat), whose window can be smaller than Summarization's
        // (Bug 3) — and the appended question + the generated answer must still fit alongside the context.
        var assembly = await _bookContextAssembler.AssembleAsync(
            bookId, language, new[] { AiTaskType.GenericChat }, ct);
        var summaries = assembly.Text;
        if (string.IsNullOrWhiteSpace(summaries))
            throw new InvalidOperationException("No chapter summaries found. Run SummarizeChaptersAsync first.");

        var inputText = $"{summaries}\n\n---\nשאלה / Question:\n{question}";

        return await _analysis.RunWithInputAsync(
            AnalysisScope.Book,
            AnalysisType.QA,
            bookId,
            chapterId: null,
            sceneId: null,
            inputText,
            language,
            ct);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    // NOTE: the former GetConcatenatedSummaries helper (unguarded flat-summary concat) was removed in
    // wb1-c03. Its flat-summary fallback now lives in BookContextAssembler.AssembleFlatFallbackAsync, which
    // applies the same per-chapter "## פרק / Chapter: {Title}" framing but caps the result at the NumCtx
    // budget, so the book-level analyses can no longer overflow the model context.

    private async Task<BookProfile> GetOrCreateProfile(Guid bookId, CancellationToken ct)
    {
        var existing = await _db.Set<BookProfile>().FirstOrDefaultAsync(p => p.BookId == bookId, ct);
        if (existing != null) return existing;

        var profile = new BookProfile { BookId = bookId };
        _db.Set<BookProfile>().Add(profile);
        return profile;
    }

    private static T? TryDeserialize<T>(string content) where T : class
    {
        try
        {
            var json = ExtractJson(content);
            if (json == null) return null;
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// TWO-STAGE repair safety net for a book-level structured-Hebrew-prose analysis produced on the profile
    /// path (CharacterAnalysis / StoryAnalysis). These types are generated via
    /// <see cref="UnifiedAnalysisService.RunRawAsync"/> with <c>structuredJson: null</c>, so the shipped repair
    /// layer (<c>ApplyAnalysisRepairAsync</c> -> <see cref="GlossaryRepairPass.Apply"/> /
    /// <see cref="DynamicTermRepairService.ApplyAsync"/>) is a strict no-op for them — this hook applies the SAME
    /// two stages to the persisted JSON, mirroring <c>BookReviewService</c>'s engine hook (glossary stage 5b via
    /// <c>ApplyGlossaryToFindings</c>, dynamic stage 5c via <c>GetEntitiesAsync</c> +
    /// <c>RepairFindingsAsync</c>) rather than inventing a third shape.
    ///
    /// c2 — WHY THE SECOND STAGE EXISTS. Until c2 this hook called <see cref="GlossaryRepairPass.RepairFields"/>
    /// and NOTHING else, so the persisted <c>CharactersJson</c> / <c>StoryStructureJson</c> received only the
    /// CLOSED 23-term en->he map (<see cref="LiteraryTermGlossary.Terms"/>) and 0% of the span-scoped dynamic
    /// cleaning that is the other half of the shipped <c>Mode=GlossaryThenDynamic</c>. On the d5 out-of-glossary
    /// corpus (10 real leaks) the glossary cleans 0/10 by construction and the dynamic stage cleans 10/10, so
    /// this path was missing the whole measured capability even though both types ARE allowlisted and DO have
    /// real dispatch arms in <see cref="DynamicTermRepairService.ApplyAsync"/>
    /// (<c>ApplyStructuredAsync&lt;CharacterAnalysisResult&gt;</c> / <c>&lt;StoryAnalysisResult&gt;</c>).
    ///
    /// NO GPU-SWAP SURFACE IS ADDED BY THIS. CharacterAnalysis and StoryAnalysis both route to
    /// <see cref="AiTaskType.LinguisticAnalysis"/> -> gemma4:12b, which is the SAME model
    /// <c>Ai:FeatureModels:TermRepair</c> resolves to, so a repair here costs no cross-model load on an
    /// <c>OLLAMA_MAX_LOADED_MODELS=1</c> host. Do not re-litigate that half; it is still true and load-bearing.
    ///
    /// f2 (2026-07-28) — Synopsis is NOT excluded anymore; the rest of this paragraph as it read before f2 is
    /// stale, do not trust old copies of it. Synopsis routes to <see cref="AiTaskType.Summarization"/> ->
    /// qwen3.5:9b, a genuine cross-model swap versus TermRepair's gemma4:12b, but q2 re-measured 100% (6/6)
    /// preservation, 0 false positives, over-rewrite 0 on q1's own fixtures once c3's ForeignRunClassifier rule
    /// (7b) closed the paragraph-head false positive that produced q1's 83% number. The swap cost is now an
    /// ACCEPTED, bounded cost (genuine leaks only), not a disqualifying one. See
    /// <c>docs/ANALYSIS_OUTPUT_REPAIR.md</c> sections 4.2 and 19.4. Net: this hook adds no swap surface for
    /// either type; Synopsis's own repair path does, and that cost was accepted knowingly, not overlooked.
    ///
    /// Deserializes the raw model output with the SAME fence-tolerant reader <see cref="TryDeserialize{T}"/>
    /// uses (<see cref="ExtractJson"/> brace-matching skips a leading ```json fence), runs the selected stage(s)
    /// over the whitelisted prose accessors IN PLACE, then reserializes to CLEAN JSON with the pipeline's
    /// camelCase <see cref="JsonOpts"/> (which also strips the model's markdown fence — the FE parses
    /// <c>profile.charactersJson</c> with a bare <c>JSON.parse</c> that a fenced string would break).
    ///
    /// GATES (mirrors <c>ApplyAnalysisRepairAsync</c> and BookReviewService exactly, two independent knobs):
    ///   • LAYER gate — <see cref="AnalysisRepairGate.Evaluate"/> (null block / Enabled=false / PerType
    ///     exclusion). Closed => neither stage runs and the RAW string is stored verbatim.
    ///   • MODE gate — the SHARED predicates <see cref="AnalysisRepairModeExtensions.RunsGlossary"/> /
    ///     <see cref="AnalysisRepairModeExtensions.RunsDynamic"/>, never a longhand copy. <c>Mode=Glossary</c>
    ///     is the rollback that reproduces the pre-c2 sequence byte-for-byte; <c>Mode=Off</c> applies NO repair
    ///     at all (before c2 this hook had no Mode gate whatsoever and ran the glossary even under Mode=Off/
    ///     Dynamic — the same Mode-contract violation be-c06 fixed in BookReviewService).
    /// The Hebrew check lives inside <see cref="GlossaryRepairPass.RepairFields"/>; the dynamic stage is
    /// bidirectional and derives its expected script from the language.
    ///
    /// FAIL-SAFE (unchanged contract): an off gate, an unparseable payload, or ANY repair/serialize fault
    /// returns the RAW string unchanged, so this can never break the profile build. The dynamic stage NEVER
    /// throws by construction — it returns its swallowed exception as <c>fault</c> precisely so an inner catch
    /// cannot blind this layer's logger — so that fault is LOGGED here rather than discarded; the value it
    /// returns alongside is already the fail-safe (original) prose.
    /// </summary>
    private async Task<string> RepairStructuredProfileJsonAsync<T>(
        string rawResult,
        string language,
        AnalysisType type,
        AnalysisRepairOptions? cfg,
        Func<T, IReadOnlyList<RepairableField>> accessorsOf,
        Guid bookId,
        ILogger logger,
        CancellationToken ct) where T : class
    {
        // Layer gate: a null block, Enabled=false, or a non-empty PerType map that excludes this type is a
        // strict no-op -> store the raw model output verbatim (pre-fix behaviour).
        //
        // h1-observable-gate-skip: name WHICH of the three reasons closed the gate via the shared
        // AnalysisRepairGate predicate (also consulted by UnifiedAnalysisService.ApplyAnalysisRepairAsync
        // and BookReviewService's glossary/dynamic hooks), Debug-only — a gated-out type here is a normal
        // steady state (e.g. Mode/PerType excluding it), never INFO/WARN noise.
        // be-c02 idiom: ONE line for the WHOLE layer, naming BOTH stages, so a closed gate never reads as
        // "only the dynamic stage was skipped".
        var gateReason = AnalysisRepairGate.Evaluate(cfg, type.ToString());
        if (gateReason != AnalysisRepairGateReason.Allowed)
        {
            logger.LogDebug(
                "AnalysisRepair: type={Type} gate closed ({Reason}); skipping BOTH the glossary and the dynamic " +
                "(span-scoped) stage for book {BookId} ({Lang}) and storing un-repaired raw JSON",
                type, gateReason, bookId, language);
            return rawResult;
        }

        // repairMode defaults to Off when the block is null (unreachable here — a null block already closed the
        // gate above — but kept identical to BookReviewService so the two hooks read the same).
        var repairMode = cfg?.Mode ?? AnalysisRepairMode.Off;

        try
        {
            // Fence-tolerant deserialize (ExtractJson skips a leading ```json fence). Unparseable -> keep raw.
            var parsed = TryDeserialize<T>(rawResult);
            if (parsed is null)
                return rawResult;

            // The whitelisted prose accessors, built ONCE and shared by both stages: every RepairableField
            // Get/Set closes over `parsed`, so the dynamic stage below reads whatever the glossary wrote.
            var fields = accessorsOf(parsed);

            // ── STAGE 1: deterministic glossary ──────────────────────────────────────────────────────────
            // Mirrors BookReviewService 5b. RepairFields is itself Hebrew-gated + guard-gated (a clean field is
            // byte-identical at zero cost; a null collection/element is never walked). NO model call.
            if (repairMode.RunsGlossary())
            {
                GlossaryRepairPass.RepairFields(fields, language);
            }

            // ── STAGE 2: dynamic span-scoped repair ──────────────────────────────────────────────────────
            // Mirrors BookReviewService 5c. The per-book proper-noun LEAVE set is fetched LAZILY, ONLY inside
            // this gate, so the rollback Mode=Glossary/Off never touches the provider. Passing the analysis
            // `language` (not the book's stored language) keeps harvest direction and classifier script in
            // agreement BY CONSTRUCTION — the same final-r02 property BookReviewService relies on.
            if (repairMode.RunsDynamic())
            {
                var bookEntities = await _bookEntities.GetEntitiesAsync(bookId, language, ct).ConfigureAwait(false);
                var dynamicResult = await _dynamicTermRepair.RepairFieldsAsync(
                    fields,
                    DynamicTermRepairService.ExpectedScriptForLanguage(language),
                    language,
                    bookEntities,
                    ct).ConfigureAwait(false);

                // SURFACE the fault instead of discarding it: RepairFieldsAsync is non-throwing by contract and
                // reports what it swallowed through Fault, so dropping it here would make an always-on layer ship
                // failures silently (the fail-safe-swallow defect class). The prose it returned is already the
                // fail-safe original, so this is observability only — never a reason to abandon the repaired JSON.
                if (dynamicResult.Fault is not null)
                {
                    logger.LogWarning(dynamicResult.Fault,
                        "Book profile dynamic repair reported a fault for type={Type} book {BookId} ({Lang}); " +
                        "the affected span(s) kept their ORIGINAL text (fail-safe). fieldsScanned={Scanned} " +
                        "fieldsChanged={Changed} runsFlagged={Flagged} runsRepaired={Repaired} runsReverted={Reverted}",
                        type, bookId, language, dynamicResult.FieldsScanned, dynamicResult.FieldsChanged,
                        dynamicResult.RunsFlagged, dynamicResult.RunsRepaired, dynamicResult.RunsReverted);
                }

                if (dynamicResult.FieldsChanged > 0)
                {
                    logger.LogInformation(
                        "Book profile dynamic repair: cleaned foreign-script leaks in {Changed} of {Scanned} " +
                        "prose field(s) for type={Type} book {BookId} ({Lang}).",
                        dynamicResult.FieldsChanged, dynamicResult.FieldsScanned, type, bookId, language);
                }
            }

            // Reserialize to CLEAN JSON (also strips the model's markdown fence so the FE JSON.parse succeeds).
            // Done whenever the LAYER gate is open, including under Mode=Off — the fence strip is what keeps the
            // FE's bare JSON.parse working, and making it conditional on a stage having run would turn the
            // documented Mode=Off kill-switch into an FE-breaking change.
            return JsonSerializer.Serialize(parsed, JsonOpts);
        }
        catch (Exception ex)
        {
            // FAIL-SAFE: a repair/serialize fault must NEVER break the profile build. Keep the raw string.
            // Covers the glossary stage, the entity fetch, the reserialize — and, belt-and-braces, an
            // unforeseen throw out of the dynamic stage (which is non-throwing by contract).
            logger.LogWarning(ex,
                "Book profile repair threw for type={Type} ({Lang}); storing un-repaired raw JSON (fail-safe).",
                type, language);
            return rawResult;
        }
    }

    private static string? ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var start = content.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inString = false, escape = false;
        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}') depth--;
            if (depth == 0) return content[start..(i + 1)];
        }
        return null;
    }
}
