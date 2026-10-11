using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>Whole-book proofread run (one Proofread analysis per chapter). Filled in by plan todo c5.</summary>
public interface IBookProofreadService
{
    Task<StartBookProofreadResponse> RunAsync(Guid bookId, string language, bool force, CancellationToken ct);
}

/// <summary>Scaffold: DI shape of <see cref="StyleBaselineService"/>; RunAsync is implemented by todo c5.</summary>
public sealed class BookProofreadService : IBookProofreadService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisProgressTracker _progress;
    private readonly BookProofreadBuildRegistry _registry;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<BookProofreadService> _logger;

    public BookProofreadService(
        IServiceScopeFactory scopeFactory,
        AnalysisProgressTracker progress,
        BookProofreadBuildRegistry registry,
        IOptions<AiOptions> aiOptions,
        ILogger<BookProofreadService> logger)
    {
        _scopeFactory = scopeFactory;
        _progress = progress;
        _registry = registry;
        _aiOptions = aiOptions;
        _logger = logger;
    }

    public Task<StartBookProofreadResponse> RunAsync(Guid bookId, string language, bool force, CancellationToken ct) =>
        throw new NotImplementedException("Filled in by plan todo c5-book-proofread-job.");
}
