using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.LanguageEngine;

/// <summary>Analysis stage: linguistic and literary analysis of the text.</summary>
public interface IAnalyzeEngine
{
    Task<LanguageAnalysis> AnalyzeAsync(string text, string language, CancellationToken cancellationToken = default);
}
