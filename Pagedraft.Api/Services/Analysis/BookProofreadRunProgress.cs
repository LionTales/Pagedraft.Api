namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// The whole-book proofread PARENT's word-weighted percent and server-side ETA (plan todo c10, reworked by be-c07).
///
/// <para>WHY THE SERVER. The parent's chunks are chapters, and chapters are wildly uneven (a 38,209-word first
/// chapter followed by 54 chapters of ~1-2.7k). A client clock of "observed time per finished chapter x chapters
/// left" multiplied the giant by 54 and read '~16 hours' at 1/55. Only the server knows each planned unit's words
/// and the in-flight child's chunk progress, so it publishes both numbers through
/// <see cref="AnalysisProgressTracker.SetRunEstimate"/> and the client prefers them.</para>
///
/// <para>THE WORDS.
/// processed = words of FINISHED units (succeeded, unreliable or failed: none of them will run again in this run)
///           + the in-flight units' words x (child CompletedChunks / child TotalChunks, 0 while the child has no chunk
///             total yet), never lower than the last value published. remaining = W (all planned words) - processed.
/// evidence  = processed - the words of units that FAILED (threw, no result) or came back UNRELIABLE. A failure's
///             time says nothing about the speed of real work (a fast failure took ~0 s for all its words and used to
///             collapse the ETA toward 0; many of them made it read 0), so its words leave the RATE's denominator. Its
///             elapsed time stays in the clock: the time was spent. An unreliable result counts as a failure here
///             (final-r02): since be-c01 a chunked chapter whose chunks all failed fast does not throw, it persists an
///             unreliable result in ~0 s, and the model never read the chunks that fell back. The flag cannot tell
///             which chunks those were, so an unreliable unit whose model did run (unrelated output, dropped content)
///             leaves the rate too: the ETA then errs long, never toward 0.
/// Percent: floor(100 x processed / W), at most 99 while running, 100 on Succeeded. Failed words COUNT toward it:
/// the percent is the run's word-weighted progress, and a failed unit is behind the run for good.</para>
///
/// <para>THE ETA. P = the configured prior, exactly what the start response returned (<see cref="BookProofreadService.Estimate"/>:
/// units x ProofreadSecondsPerUnitOverhead + chunks x ProofreadSecondsPerChunkEstimate), e = seconds since the run
/// started on the ONE injectable clock, r0 = P / W (the prior's seconds per word).
/// While evidence is 0, the prior COUNTS DOWN: min(P - e, r0 x remaining). A single-shot first unit (no chunk total,
/// so no word lands until it ends) used to hold P for its whole duration; the second term drops a fast failure's
/// words from the prior.
/// Once evidence &gt; 0: a BLEND of the prior and the observed pace, weighted by the evidence fraction f = evidence / W:
/// rate = (1 - f) x r0 + f x (e / evidence); ETA = rate x remaining. Replacing the prior wholesale at the first word
/// was a clock substitution: a cold first unit over a tiny denominator swung the ETA 8x (gate c10: 160 s -&gt; 20 s at
/// unit 1); with the blend one unit moves it only by its share of the book, and at the end the observed pace rules.
/// Both phases agree: if the observed pace equals r0 then e = r0 x evidence and the blend is exactly P - e, the
/// countdown; the step at the first word is the evidence that the pace differs from the prior.
/// FLOOR: while any word remains, never below min(<see cref="RunningFloorSeconds"/>, P), because 0 reads "nothing
/// left" (the client then shows no line) while a unit is still running; with P = 0 (ETA disabled) there is no
/// floor. Every word processed and not yet terminal: 0, there is nothing left to wait for.</para>
///
/// <para>MONOTONE. The percent never decreases. At one instant, more processed words never raise the ETA (rate and
/// remaining both fall), with ONE exception: a unit that showed chunk progress and then FAILED (or came back
/// unreliable) takes that partial share out of the evidence, which can raise it. Between word updates the ETA moves with the clock: down one
/// second per second in the countdown (to the floor), and UP by at most remaining / W seconds per second once there
/// is evidence (no new words while time passes is evidence of a slower pace); it falls again when words land.</para>
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
    /// <summary>
    /// The lowest ETA published while any word remains (capped by the prior): a value the client renders as "less
    /// than a minute", never 0, which it reads as "nothing left" and shows no line for.
    /// </summary>
    internal const int RunningFloorSeconds = 10;

    private readonly object _gate = new();
    private readonly AnalysisProgressTracker _tracker;
    private readonly Guid _parentId;
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _startedAt;
    private readonly int _configuredSeconds;
    private readonly long[][] _unitWords;
    private readonly Dictionary<(int Chapter, int Unit), Guid> _inFlight = new();
    private long _finishedWords;
    private long _failedWords;
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

    /// <summary>
    /// A unit ended, on ANY outcome: its words are processed (they count toward the percent). <paramref name="failed"/>
    /// = it produced no result (threw) or an unreliable one: its words then also leave the pace's evidence (see the
    /// class remarks).
    /// </summary>
    public void UnitFinished(int chapterIndex, int unitIndex, bool failed)
    {
        lock (_gate)
        {
            if (_inFlight.Remove((chapterIndex, unitIndex)))
            {
                var words = WordsOf(chapterIndex, unitIndex);
                _finishedWords += words;
                if (failed) _failedWords += words;
            }
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

    /// <summary>The ETA rule of the class remarks: countdown without evidence, prior/pace blend with it, floored.</summary>
    private int RemainingSecondsOf(double processed)
    {
        var remainingWords = Math.Max(0, TotalWords - processed);
        if (remainingWords <= 0) return 0;

        var elapsed = Math.Max(0, (_clock.GetUtcNow() - _startedAt).TotalSeconds);
        var priorRate = _configuredSeconds / (double)TotalWords;
        var evidence = Math.Max(0, processed - _failedWords);

        double seconds;
        if (evidence <= 0)
        {
            seconds = Math.Min(_configuredSeconds - elapsed, priorRate * remainingWords);
        }
        else
        {
            var weight = Math.Min(1, evidence / TotalWords);
            var rate = (1 - weight) * priorRate + weight * (elapsed / evidence);
            seconds = rate * remainingWords;
        }

        seconds = Math.Max(Math.Min(RunningFloorSeconds, _configuredSeconds), seconds);
        return seconds >= int.MaxValue ? int.MaxValue : (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
    }
}
