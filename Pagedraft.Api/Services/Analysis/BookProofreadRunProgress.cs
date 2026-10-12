namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// The whole-book proofread PARENT's word-weighted percent and server-side ETA (plan todo c10).
///
/// <para>WHY THE SERVER. The parent's chunks are chapters, and chapters are wildly uneven (a 38,209-word first
/// chapter followed by 54 chapters of ~1-2.7k). A client clock of "observed time per finished chapter x chapters
/// left" multiplied the giant by 54 and read '~16 hours' at 1/55. Only the server knows each planned unit's words
/// and the in-flight child's chunk progress, so it publishes both numbers through
/// <see cref="AnalysisProgressTracker.SetRunEstimate"/> and the client prefers them.</para>
///
/// <para>THE FORMULA.
/// processedWords = words of FINISHED units (succeeded, failed or unreliable alike: the time was spent)
///                + the in-flight units' words x (child CompletedChunks / child TotalChunks, 0 while the child has no
///                  chunk total yet), never lower than the last value published.
/// remainingWords = totalPlannedWords - processedWords.
/// ETA: while processedWords is 0, the configured estimate the start response carried (<see cref="BookProofreadService.Estimate"/>:
/// chunks via ProofreadChunkTargetWordsFor x ProofreadSecondsPerChunkEstimate), so the first value the author sees
/// is the one they consented to; after that, observed seconds per word since the run started x remainingWords.
/// Percent: floor(100 x processedWords / totalPlannedWords), at most 99 while running, 100 on Succeeded.</para>
///
/// <para>WORDS PER UNIT. A chapter row is the chapter's WordCount. Scenes carry no word count, so a scene-split
/// chapter's words are shared evenly across its scenes. Every unit weighs at least one word, so the total is never
/// zero.</para>
///
/// <para>WHEN. At every unit start and finish, and on the run's heartbeat tick while a unit runs
/// (<see cref="BookProofreadService.ParentHeartbeatInterval"/>), never from the HTTP poll. Compute + publish run
/// under one lock, so two publishers cannot write a lower percent after a higher one.</para>
/// </summary>
internal sealed class BookProofreadRunProgress
{
    private readonly object _gate = new();
    private readonly AnalysisProgressTracker _tracker;
    private readonly Guid _parentId;
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _startedAt;
    private readonly int _configuredSeconds;
    private readonly long[][] _unitWords;
    private readonly Dictionary<(int Chapter, int Unit), Guid> _inFlight = new();
    private long _finishedWords;
    private double _processedHighWater;
    private int _lastPercent;

    /// <param name="planned">The run's chapters in the order the run indexes them.</param>
    /// <param name="configuredSeconds">The plan's EstimatedSeconds, exactly what the start response returned.</param>
    public BookProofreadRunProgress(
        AnalysisProgressTracker tracker,
        Guid parentId,
        IReadOnlyList<BookProofreadService.ChapterProofreadState> planned,
        int configuredSeconds,
        TimeProvider clock)
    {
        _tracker = tracker;
        _parentId = parentId;
        _clock = clock;
        _startedAt = clock.GetUtcNow();
        _configuredSeconds = Math.Max(0, configuredSeconds);
        _unitWords = planned.Select(UnitWords).ToArray();
        TotalWords = _unitWords.Sum(units => units.Sum());
    }

    /// <summary>The whole-book parent job this progress publishes to.</summary>
    public Guid ParentId => _parentId;

    /// <summary>The words the whole run covers (at least one per unit).</summary>
    public long TotalWords { get; }

    /// <summary>
    /// The chapter's words shared across its units: the chapter row takes them all; scenes split them evenly, the
    /// remainder going to the first ones. Never below one word per unit.
    /// </summary>
    internal static long[] UnitWords(BookProofreadService.ChapterProofreadState chapter)
    {
        var count = Math.Max(1, chapter.Units.Count);
        var words = Math.Max(0, chapter.WordCount);
        var share = words / count;
        var remainder = words % count;
        return Enumerable.Range(0, count)
            .Select(i => Math.Max(1L, share + (i < remainder ? 1 : 0)))
            .ToArray();
    }

    /// <summary>A unit's child job is registered and running; its chunk progress now counts.</summary>
    public void UnitStarted(int chapterIndex, int unitIndex, Guid childJobId)
    {
        lock (_gate)
        {
            _inFlight[(chapterIndex, unitIndex)] = childJobId;
            PublishLocked();
        }
    }

    /// <summary>A unit ended, on ANY outcome (succeeded, failed, unreliable): its words are processed.</summary>
    public void UnitFinished(int chapterIndex, int unitIndex)
    {
        lock (_gate)
        {
            if (_inFlight.Remove((chapterIndex, unitIndex)))
                _finishedWords += WordsOf(chapterIndex, unitIndex);
            PublishLocked();
        }
    }

    /// <summary>The periodic tick: re-reads the in-flight children's chunk progress and republishes.</summary>
    public void Refresh()
    {
        lock (_gate) PublishLocked();
    }

    /// <summary>
    /// The terminal publish: Succeeded (including a partial run, where every unit was processed) is 100 %; Canceled
    /// and Failed keep the last percent. Nothing is left to wait for either way, so the ETA is 0.
    /// </summary>
    public void Finish(AnalysisProgressStatus status)
    {
        lock (_gate)
        {
            if (status == AnalysisProgressStatus.Succeeded) _lastPercent = 100;
            _tracker.SetRunEstimate(_parentId, _lastPercent, 0);
        }
    }

    /// <summary>The current (percent, remaining seconds) without publishing; a test seam.</summary>
    internal (int Percent, int RemainingSeconds) Peek()
    {
        lock (_gate)
        {
            var processed = Math.Max(_processedHighWater, ProcessedWordsLocked());
            return (Math.Max(_lastPercent, PercentOf(processed)), RemainingSecondsOf(processed));
        }
    }

    private void PublishLocked()
    {
        _processedHighWater = Math.Max(_processedHighWater, ProcessedWordsLocked());
        _lastPercent = Math.Max(_lastPercent, PercentOf(_processedHighWater));
        _tracker.SetRunEstimate(_parentId, _lastPercent, RemainingSecondsOf(_processedHighWater));
    }

    private double ProcessedWordsLocked()
    {
        double processed = _finishedWords;
        foreach (var ((chapter, unit), childId) in _inFlight)
            processed += WordsOf(chapter, unit) * ChildFraction(childId);
        return Math.Min(processed, TotalWords);
    }

    /// <summary>The child's finished share of its chunks; 0 until it has a chunk total (a single-shot run never does).</summary>
    private double ChildFraction(Guid childId)
    {
        if (!_tracker.TryGet(childId, out var snap) || snap is null || snap.TotalChunks <= 0) return 0;
        return Math.Clamp(snap.CompletedChunks / (double)snap.TotalChunks, 0, 1);
    }

    private long WordsOf(int chapterIndex, int unitIndex) =>
        chapterIndex >= 0 && chapterIndex < _unitWords.Length
        && unitIndex >= 0 && unitIndex < _unitWords[chapterIndex].Length
            ? _unitWords[chapterIndex][unitIndex]
            : 0;

    private int PercentOf(double processed) =>
        Math.Min(99, (int)Math.Floor(100.0 * processed / TotalWords));

    private int RemainingSecondsOf(double processed)
    {
        if (processed <= 0) return _configuredSeconds;
        var remainingWords = Math.Max(0, TotalWords - processed);
        var elapsed = Math.Max(0, (_clock.GetUtcNow() - _startedAt).TotalSeconds);
        var seconds = elapsed / processed * remainingWords;
        return seconds >= int.MaxValue ? int.MaxValue : (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
    }
}
