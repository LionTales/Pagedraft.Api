using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Tests;

/// <summary>
/// A trivial in-memory <see cref="IBookEntityProvider"/> test double (dynamic-term-repair precision follow-up,
/// todo e3). Returns a FIXED entity set for every book without touching a DbContext, so the direct-construction
/// <see cref="UnifiedAnalysisService"/> tests can satisfy the new constructor dependency without standing up a
/// scope factory. Default is the EMPTY set, which is exactly the pre-e3 behaviour (a null/empty entity set means
/// the classifier's entity check is skipped) — and under the shipped Mode=Glossary default the provider is never
/// even consulted. Seed a non-empty case-insensitive set to prove the entity LEAVE lever threads through a seam.
///
/// It is also a SPY (be-c02): every <see cref="GetEntitiesAsync"/> call records its bookId in
/// <see cref="RequestedBookIds"/>, and <see cref="For"/> seeds the entities for ONE specific book (any other id —
/// including the Guid.Empty a seam that DROPPED the bookId would pass — gets the empty set). That makes
/// "the real bookId reached the repair layer" assertable end to end: a seam that loses the id sees no entities,
/// so the book's own names get repaired instead of spared.
///
/// It spies the LANGUAGE too (final-r02): the harvest direction is resolved from the ANALYSIS language, so a seam
/// that passed the wrong language (e.g. the book's stored one) would harvest the wrong script and silently disarm
/// the entity lever. <see cref="RequestedLanguages"/> makes "the seam passed the SAME language it hands the repair
/// layer" assertable.
/// </summary>
internal sealed class StubBookEntityProvider : IBookEntityProvider
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlySet<string> _entities;

    /// <summary>When set, <see cref="_entities"/> is returned ONLY for this book; every other id gets the
    /// empty set. Null = the legacy "same set for every book" behaviour.</summary>
    private readonly Guid? _onlyForBookId;

    /// <summary>Every bookId the provider was asked for, in call order (spy).</summary>
    public List<Guid> RequestedBookIds { get; } = new();

    /// <summary>Every ANALYSIS LANGUAGE the provider was asked for, in call order (spy, final-r02). The provider
    /// resolves its harvest DIRECTION from this, so a seam that passes the wrong value harvests the wrong script.</summary>
    public List<string?> RequestedLanguages { get; } = new();

    /// <summary>Every bookId <see cref="Invalidate"/> was called for, in call order (spy, be-c03). The real
    /// provider caches a book's harvested set, so a producer that CHANGES a harvest source (a persisted
    /// CharacterAnalysis / BookProfile, a chapter content write) must invalidate or the names never arrive.</summary>
    public List<Guid> InvalidatedBookIds { get; } = new();

    public StubBookEntityProvider(IReadOnlySet<string>? entities = null, Guid? onlyForBookId = null)
    {
        _entities = entities ?? Empty;
        _onlyForBookId = onlyForBookId;
    }

    /// <summary>Convenience factory: a case-insensitive set from the given names, returned for EVERY book.</summary>
    public static StubBookEntityProvider With(params string[] names)
        => new(new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));

    /// <summary>Convenience factory: the given names are returned ONLY for <paramref name="bookId"/>; any other
    /// id (notably Guid.Empty) gets the empty set — so an assertion on the names proves the REAL id was passed.</summary>
    public static StubBookEntityProvider For(Guid bookId, params string[] names)
        => new(new HashSet<string>(names, StringComparer.OrdinalIgnoreCase), bookId);

    public Task<IReadOnlySet<string>> GetEntitiesAsync(Guid bookId, string? language, CancellationToken ct = default)
    {
        RequestedBookIds.Add(bookId);
        RequestedLanguages.Add(language);
        var entities = _onlyForBookId is null || _onlyForBookId == bookId ? _entities : Empty;
        return Task.FromResult(entities);
    }

    public void Invalidate(Guid bookId) => InvalidatedBookIds.Add(bookId);
}
