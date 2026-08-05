using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Models;

public class AnalysisResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning chapter. NULL for book-scoped results (e.g. QA / ask), which have no chapter —
    /// mirrors the already-nullable <see cref="BookId"/>/<see cref="SceneId"/>. A non-null value must be a
    /// real Chapter (FK); persisting Guid.Empty here caused the book-scoped QA insert to fail the FK.</summary>
    public Guid? ChapterId { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? JobId { get; set; }

    /// <summary>Legacy display label — kept for backward compatibility until UI migrates.</summary>
    public string Type { get; set; } = string.Empty;

    public string PromptUsed { get; set; } = string.Empty;
    public string ResultText { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    // ── New unified columns ──
    public AnalysisScope Scope { get; set; } = AnalysisScope.Chapter;
    public AnalysisType AnalysisType { get; set; } = AnalysisType.Custom;
    public AnalysisStatus Status { get; set; } = AnalysisStatus.Active;
    public Guid? SceneId { get; set; }
    public Guid? BookId { get; set; }

    /// <summary>Parsed structured JSON (LineEditResult, LinguisticAnalysisResult, etc.).</summary>
    public string? StructuredResult { get; set; }

    /// <summary>Normalized text at analysis time, used as the baseline for server-side suggestion offsets.</summary>
    public string? SourceTextSnapshot { get; set; }

    public string Language { get; set; } = "he";

    /// <summary>Set by UnifiedAnalysisService for Proofread when result is nearly identical to input (possible truncation or model failure).</summary>
    public bool ProofreadNoChangesHint { get; set; }

    /// <summary>
    /// True when a Proofread result must NOT be trusted as a clean pass: the model returned empty/blank
    /// output, or content unrelated to the input (so it was discarded and the input echoed back). Distinct
    /// from <see cref="ProofreadNoChangesHint"/>, which is true for a genuinely-clean no-change result too.
    /// Now PERSISTED (mapped column, default false) so the History tab reflects whether a reloaded run was
    /// unreliable, consistent with the live run.
    /// </summary>
    public bool ProofreadResultUnreliable { get; set; }

    /// <summary>
    /// How many proofread suggestions the orthographic-impossibility safety net withheld on THIS run
    /// (<c>HebrewOrthographyShapeGuard</c>, gated by
    /// <c>Ai:HebrewStyle:DropOrthographicallyImpossibleSuggestions</c>). Almost always 0.
    ///
    /// TRANSIENT ON PURPOSE, and it is <see cref="NotMappedAttribute"/> rather than a column: it is the
    /// in-memory carrier from AttachSuggestions to the run-log writers, which is where the count is
    /// PERSISTED (<see cref="AnalysisRunLog.SuppressedSuggestionCount"/>, written on every Proofread run,
    /// chunked or not). Reading this off a row loaded from history yields 0 and means nothing; the run
    /// log is the durable answer to "did this run drop anything".
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int SuppressedImpossibleSuggestionCount { get; set; }

    // ── Navigation ──
    /// <summary>Null for book-scoped results (see <see cref="ChapterId"/>).</summary>
    public Chapter? Chapter { get; set; }
    public PromptTemplate? Template { get; set; }
    public ICollection<AnalysisSuggestion> Suggestions { get; set; } = new List<AnalysisSuggestion>();
}

public enum AnalysisStatus
{
    Active,
    Archived
}
