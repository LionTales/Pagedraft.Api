using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// THE ONE READING OF THE SCAN LEDGER (automatic-coverage plan, be-c03 / d1 §1).
///
/// <para>Both sides of coverage come from here: the SCAN PATH
/// (<c>AnalysisContextService.LoadCharacterRegisterAsync</c>) asks <see cref="IsCoveredAndFresh"/>
/// whether a chapter still needs scanning, and the REPORT the author sees
/// (<c>CharacterRegisterService.GetAsync</c>) asks <see cref="Summarize"/>, which asks the same
/// method. There is deliberately no second walk and no stored count: this workspace has shipped a
/// status count and the builder it described disagreeing more than once, and the only structural
/// guard is that the number reported and the decision taken are computed by the same code from the
/// same persisted <see cref="CharacterRegister.ScannedChapters"/> list.</para>
///
/// <para>Nothing here reads or counts <see cref="CharacterRegister.Characters"/>. Coverage is a fact
/// about which chapters have been READ, not about how many characters came out: a chapter with no
/// characters in it is fully covered once it has been scanned.</para>
/// </summary>
public static class CharacterRegisterCoverage
{
    /// <summary>
    /// The chapter facts <see cref="Summarize"/> classifies on, so callers project exactly these
    /// columns instead of loading tracked <c>Chapter</c> entities.
    /// </summary>
    /// <param name="ChapterId">The ledger key.</param>
    /// <param name="UpdatedAt">The chapter's current <c>Chapter.UpdatedAt</c> — the re-scan key.</param>
    /// <param name="ContentText">
    /// The chapter's raw content, when it was loaded. It is needed because "can the pipeline scan
    /// this at all" is answered by the SAME expression the analysis path uses (see
    /// <see cref="IsScannable"/>), not by a cheaper proxy such as WordCount that could disagree
    /// with it.
    ///
    /// <para>IT IS LEGITIMATELY NULL FOR A COVERED-AND-FRESH CHAPTER, and null here does NOT mean
    /// "this chapter is empty". <see cref="SummarizeAsync"/> deliberately does not fetch the text of
    /// a chapter whose ledger line is fresh, because <see cref="Summarize"/>'s first precedence rule
    /// answers such a chapter before <see cref="IsScannable"/> is ever reached. Do not add a reader
    /// of this field outside that branch without also widening what
    /// <see cref="SummarizeAsync"/> loads.</para>
    /// </param>
    public readonly record struct ChapterScanState(Guid ChapterId, DateTimeOffset UpdatedAt, string? ContentText);

    /// <summary>
    /// The two chapter facts that are ALWAYS needed — the cheap projection every register read starts
    /// from, before <see cref="SummarizeAsync"/> decides whose text it actually has to fetch.
    /// </summary>
    /// <param name="ChapterId">The ledger key.</param>
    /// <param name="UpdatedAt">The chapter's current <c>Chapter.UpdatedAt</c> — the re-scan key.</param>
    public readonly record struct ChapterVersion(Guid ChapterId, DateTimeOffset UpdatedAt);

    /// <summary>This chapter's ledger line, or null when it has never contributed.</summary>
    public static ScannedChapterEntry? FindEntry(CharacterRegister? register, Guid chapterId)
        => register?.ScannedChapters.FirstOrDefault(e => e.ChapterId == chapterId);

    /// <summary>
    /// THE COVERED-AND-FRESH PREDICATE (d1 §1), and the single definition of it in the API. A chapter
    /// counts iff it has a ledger line AND that line's <see cref="ScannedChapterEntry.SourceStamp"/>
    /// still equals the chapter's current <c>UpdatedAt</c>; any other state (no line, or an older
    /// stamp because the author edited the chapter) is a miss and the next analysis re-contributes it.
    ///
    /// <para><see cref="ScannedChapterEntry.ScannedAt"/> is deliberately NOT consulted: it is the wall
    /// clock of the scan, for reporting only. Freshness is a comparison against the chapter's text
    /// version, never against elapsed time.</para>
    /// </summary>
    public static bool IsCoveredAndFresh(ScannedChapterEntry? entry, DateTimeOffset chapterUpdatedAt)
        => entry is not null && entry.SourceStamp == chapterUpdatedAt;

    /// <summary>
    /// Whether the pipeline could scan this chapter at all. The expression is the one
    /// <c>AnalysisContextService.ResolveChapterAsync</c> already refuses on ("No chapter text to
    /// analyze") and that <c>ExtractCharacterRegisterAsync</c> returns null for: strip the Syncfusion
    /// trial watermark, then ask whether anything is left.
    ///
    /// <para>WHY IT IS THE STRIPPED TEXT AND NOT <c>IsNullOrWhiteSpace(ContentText)</c>: a chapter
    /// holding nothing but a trial watermark is non-blank in the column and blank to every analysis.
    /// Reporting it as merely "pending" would leave it pending forever, which is exactly the
    /// unsatisfiable-coverage failure the unscannable bucket exists to prevent.</para>
    ///
    /// <para>WHO ASKS, AND WHEN. Inside <see cref="Summarize"/> this is reached ONLY for a chapter
    /// that is not covered-and-fresh — it is the second rule of the precedence chain, behind an
    /// <c>else</c>. That is not an incidental detail: <see cref="SummarizeAsync"/> relies on it to
    /// skip loading the text of covered chapters entirely, so a covered chapter's
    /// <c>ContentText</c> is null by the time it gets here rather than merely unread.</para>
    /// </summary>
    public static bool IsScannable(string? contentText)
        => !string.IsNullOrWhiteSpace(SyncfusionWatermarkStripper.StripSyncfusionWatermark(contentText ?? ""));

    /// <summary>
    /// Classify every chapter of a book into exactly one bucket and count them. The four buckets are
    /// mutually exclusive and exhaustive: covered + pending + stale + unscannable == total, always.
    ///
    /// <para>PRECEDENCE, in order, because a chapter can satisfy more than one description:</para>
    /// <list type="number">
    /// <item>COVERED — a fresh ledger line. Wins over everything, including blank content: the line
    /// says this chapter's current text HAS been read, and only a real scan writes one. (The scan path
    /// cannot mint one for a chapter with nothing to read: an empty extraction SOURCE yields no answer
    /// at all, which is a failed scan and writes nothing - see
    /// <c>AnalysisContextService.ExtractCharacterRegisterAsync</c>'s null contract. So in practice a
    /// chapter is not both covered and unscannable unless its text was emptied AFTER a real scan, and
    /// that case moves it to UNSCANNABLE below because emptying it also moves its UpdatedAt.)</item>
    /// <item>UNSCANNABLE — no text the pipeline could read (see <see cref="IsScannable"/>). Wins over
    /// stale and pending, and that is the load-bearing part: an emptied chapter that once contributed
    /// would otherwise sit in STALE forever and 'complete' could never be reached on that book.</item>
    /// <item>STALE — it contributed, then the author edited it, so its line no longer matches.</item>
    /// <item>PENDING — never contributed.</item>
    /// </list>
    ///
    /// <para>Unscannable chapters are their OWN bucket and are NOT counted as covered: nothing was
    /// read, so claiming a contribution would be a lie. They are excluded from what
    /// <see cref="CharacterRegisterCoverageDto.IsComplete"/> waits for, which is what keeps that flag
    /// reachable — the full argument is on the DTO.</para>
    ///
    /// <para>A ledger line naming a chapter that no longer exists is ignored here rather than counted:
    /// this walks the BOOK'S chapters and looks each one up in the ledger, never the other way round,
    /// so a deleted chapter's orphaned line cannot inflate the covered count.</para>
    ///
    /// <para>THE ORDER OF THE FIRST TWO RULES IS NOW LOAD-BEARING BEYOND CLASSIFICATION (be-c01).
    /// Because COVERED is answered before <see cref="IsScannable"/> is reached,
    /// <see cref="SummarizeAsync"/> does not fetch <c>ContentText</c> for covered-and-fresh chapters
    /// at all. Swapping rules 1 and 2 would therefore not merely reclassify one edge case, it would
    /// reclassify it against text that was never loaded. If you reorder these branches, change
    /// <see cref="SummarizeAsync"/> in the same edit.</para>
    /// </summary>
    public static CharacterRegisterCoverageDto Summarize(
        CharacterRegister? register,
        IReadOnlyCollection<ChapterScanState> chapters)
    {
        var covered = 0;
        var pending = 0;
        var stale = 0;
        var unscannable = 0;
        DateTimeOffset? lastScannedAt = null;

        foreach (var chapter in chapters)
        {
            var entry = FindEntry(register, chapter.ChapterId);

            if (entry is not null && (lastScannedAt is null || entry.ScannedAt > lastScannedAt))
                lastScannedAt = entry.ScannedAt;

            if (IsCoveredAndFresh(entry, chapter.UpdatedAt)) covered++;
            else if (!IsScannable(chapter.ContentText)) unscannable++;
            else if (entry is not null) stale++;
            else pending++;
        }

        return new CharacterRegisterCoverageDto(
            TotalChapters: chapters.Count,
            CoveredChapters: covered,
            PendingChapters: pending,
            StaleChapters: stale,
            UnscannableChapters: unscannable,
            IsComplete: chapters.Count > 0 && pending == 0 && stale == 0,
            LastScannedAt: lastScannedAt);
    }

    /// <summary>No chapter needed its text, so nothing was fetched.</summary>
    private static readonly IReadOnlyDictionary<Guid, string?> NoContentText =
        new Dictionary<Guid, string?>();

    /// <summary>
    /// THE TWO-PHASE READ (fix-plan be-c01), and the entry point any caller backed by a database
    /// should use. Same answer as <see cref="Summarize"/>, without loading the book's prose to
    /// produce it.
    ///
    /// <para>Phase 1 takes the cheap projection — <see cref="ChapterVersion"/>, i.e. <c>{Id,
    /// UpdatedAt}</c> for every chapter — and asks which chapters' text <see cref="Summarize"/> can
    /// actually consult. That is exactly the chapters that are NOT covered-and-fresh, because
    /// <see cref="IsScannable"/> sits behind an <c>else</c> in the precedence chain. Phase 2 calls
    /// <paramref name="loadContentText"/> once for that set only, and it is not called at all when
    /// the set is empty. What is saved scales with COVERAGE, not with book size: a fully covered book
    /// reads no chapter text at all, a half-covered one reads half. State that precisely, because the
    /// 80-chapter book that motivated this fix reports ZERO covered chapters today (the scan ledger is
    /// newer than the register, which is the same fact the client's pre-ledger sentence exists for), so
    /// every one of its chapters still misses phase 1 and its whole manuscript is still fetched, now
    /// behind one extra round trip. This is a saving that ARRIVES as the ledger fills, not one that
    /// landed on the day it shipped, and nothing here should be read as claiming otherwise.</para>
    ///
    /// <para>WHAT THIS IS NOT: a second classification. Nothing here counts anything or assigns a
    /// bucket. It answers one question — whose text has to be fetched — using the SAME
    /// <see cref="FindEntry"/> + <see cref="IsCoveredAndFresh"/> pair the classification and the scan
    /// path use, never a cheaper restatement of it, and then hands ONE collection to
    /// <see cref="Summarize"/>, which decides all four buckets exactly as before. The single-source
    /// guarantee this class exists for is a guarantee about where buckets are DECIDED, and that is
    /// still one place.</para>
    ///
    /// <para>Chapters skipped in phase 1 reach <see cref="Summarize"/> with a null
    /// <see cref="ChapterScanState.ContentText"/>. That is safe only while COVERED wins over
    /// UNSCANNABLE; see the precedence note on <see cref="Summarize"/>, and the regression test that
    /// pins it.</para>
    /// </summary>
    /// <param name="register">The persisted register, or null when it has never been built.</param>
    /// <param name="chapters">Every chapter of the book, as <c>{Id, UpdatedAt}</c>.</param>
    /// <param name="loadContentText">
    /// Fetches <c>ContentText</c> for the given chapter ids in ONE round trip. A chapter absent from
    /// the returned map is treated as having no text, which is also what happens if it was deleted
    /// between the two phases — the honest answer, since a chapter nobody can read is unscannable.
    /// </param>
    /// <param name="ct">Cancellation for the phase-2 fetch.</param>
    public static async Task<CharacterRegisterCoverageDto> SummarizeAsync(
        CharacterRegister? register,
        IReadOnlyCollection<ChapterVersion> chapters,
        Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string?>>> loadContentText,
        CancellationToken ct)
    {
        var needContentText = new List<Guid>();
        foreach (var chapter in chapters)
        {
            if (!IsCoveredAndFresh(FindEntry(register, chapter.ChapterId), chapter.UpdatedAt))
                needContentText.Add(chapter.ChapterId);
        }

        var contentText = needContentText.Count == 0
            ? NoContentText
            : await loadContentText(needContentText, ct);

        return Summarize(
            register,
            chapters
                .Select(c => new ChapterScanState(
                    c.ChapterId,
                    c.UpdatedAt,
                    contentText.TryGetValue(c.ChapterId, out var text) ? text : null))
                .ToList());
    }
}
