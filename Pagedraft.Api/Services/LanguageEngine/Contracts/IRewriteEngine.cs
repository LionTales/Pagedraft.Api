namespace Pagedraft.Api.Services.LanguageEngine;

/// <summary>Text rewriting stage: generates corrected text based on detected issues.</summary>
public interface IRewriteEngine
{
    Task<string> RewriteAsync(string normalizedText, List<Contracts.LanguageIssue> issues, string language, CancellationToken cancellationToken = default);
}
