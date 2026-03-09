namespace Pagedraft.Api.Models.Dtos;

/// <summary>Response for async analysis job start. The jobId can be used with analysis-progress and analysis-jobs endpoints.</summary>
public record StartAnalysisJobResponse(
    Guid JobId,
    string AnalysisType,
    string Scope);

