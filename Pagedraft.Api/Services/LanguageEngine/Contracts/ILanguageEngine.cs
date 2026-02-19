namespace Pagedraft.Api.Services.LanguageEngine.Contracts;

/// <summary>Main orchestrator for the language engine pipeline.</summary>
public interface ILanguageEngine
{
    Task<LanguageEngineResult> ProcessAsync(LanguageEngineRequest request, CancellationToken cancellationToken = default);
}
