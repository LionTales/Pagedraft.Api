namespace Pagedraft.Api.Models;

public class AnalysisRunLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid? AnalysisResultId { get; set; }
    public Guid? PromptTemplateId { get; set; }
    public Guid? BookId { get; set; }
    public Guid? ChapterId { get; set; }
    public Guid? SceneId { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string AnalysisType { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string Language { get; set; } = "he";
    public int TotalChunks { get; set; }
    public int SucceededChunks { get; set; }
    public int FallbackChunks { get; set; }
    public int InputWordCount { get; set; }
    public int InputCharCount { get; set; }
    public int OutputCharCount { get; set; }
    public int SuggestionCount { get; set; }

    /// <summary>
    /// Proofread suggestions the orthographic-impossibility safety net WITHHELD on this run
    /// (<c>HebrewOrthographyShapeGuard</c>; switch <c>Ai:HebrewStyle:DropOrthographicallyImpossibleSuggestions</c>).
    /// Sits next to <see cref="SuggestionCount"/> deliberately: the pair is what lets an operator tell a
    /// run that dropped three from a run that dropped none, which a guard that only logged would not.
    /// 0 on every non-Proofread run, on every English run, and on the overwhelming majority of Hebrew
    /// ones. Each drop is ALSO logged at Warning with the offending word.
    /// </summary>
    public int SuppressedSuggestionCount { get; set; }
    public long TotalDurationMs { get; set; }
    public bool NoChangesHint { get; set; }
    public string? ChunkDetailsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Navigation
    public AnalysisResult? AnalysisResult { get; set; }
    public PromptTemplate? PromptTemplate { get; set; }
}

/// <summary>Per-chunk outcome serialized into AnalysisRunLog.ChunkDetailsJson (not an EF entity).</summary>
public class AnalysisChunkOutcome
{
    public int ChunkIndex { get; set; }
    public int InputCharCount { get; set; }
    public int InputWordCount { get; set; }
    public int OutputCharCount { get; set; }
    public long DurationMs { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public double? WordSimilarity { get; set; }
    public string? Note { get; set; }
}
