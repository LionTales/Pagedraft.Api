using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Models;

public class AnalysisResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChapterId { get; set; }
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
    public Guid? SceneId { get; set; }
    public Guid? BookId { get; set; }

    /// <summary>Parsed structured JSON (LineEditResult, LinguisticAnalysisResult, etc.).</summary>
    public string? StructuredResult { get; set; }

    public string Language { get; set; } = "he";

    /// <summary>Set by UnifiedAnalysisService for Proofread when result is nearly identical to input (possible truncation or model failure). Not persisted.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool ProofreadNoChangesHint { get; set; }

    // ── Navigation ──
    public Chapter Chapter { get; set; } = null!;
    public PromptTemplate? Template { get; set; }
}
