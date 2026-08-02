using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Records what the resolver said and at which level, so the fail-safe branches can be asserted on their
/// OBSERVABILITY as well as on their return value. Per-task resolution multiplies the number of ways a run can
/// end up local, and "why did this run fast?" has to be answerable from the log alone - which means every
/// doubt-driven branch must name the TASK it was asked about.
/// </summary>
internal sealed class AiTierCapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));

    public IReadOnlyList<string> MessagesAt(LogLevel level) =>
        Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();
}

/// <summary>
/// PER-TASK TIER RESOLUTION (tier-ux-rework plan, c1). The precedence, every fail-safe branch, and the
/// isolation between tasks that the whole feature rests on.
///
/// THE THREE RUNGS: an explicit <c>BookAiTaskTiers</c> override, else <c>Book.AiTier</c> (the book-level
/// default SEED), else <see cref="AiTier.Fast"/>. The direction of the fail-safe is asserted branch by branch
/// rather than as one happy-path check, because "we could not tell" resolving to the cloud is the one outcome
/// that costs money and sends an unpublished manuscript to a third party.
///
/// Class named *AiTier* so the standing deterministic filter picks the file up.
/// </summary>
public class AiTierPerTaskResolverTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-pertask-{Guid.NewGuid()}").Options);

    private static async Task<Guid> SeedAsync(
        AppDbContext db, string? bookDefault, params (AiTaskType Task, string? Tier)[] overrides)
    {
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "T", Language = "he", AiTier = bookDefault });
        foreach (var (task, tier) in overrides)
            db.BookAiTaskTiers.Add(new BookAiTaskTier
            {
                BookId = bookId,
                TaskKey = AiTierPolicy.TaskKeyFor(task),
                Tier = tier
            });
        await db.SaveChangesAsync();
        return bookId;
    }

    private static Task<AiTier> Resolve(AppDbContext db, Guid? bookId, AiTaskType task, ILogger? logger = null)
        => BookAiTierResolver.ResolveAsync(
            db, bookId, task, logger ?? NullLogger.Instance, CancellationToken.None);

    // ── Rung 1: the per-task override ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE OVERRIDE OUTRANKS THE DEFAULT IN BOTH DIRECTIONS. The upward case (fast default, thinking
    /// override) is the feature; the downward case (thinking default, fast override) is the one a user
    /// reaches for when one task is producing worse output on the paid tier, and it must be honoured or the
    /// control is a lie in the direction that costs money.
    /// </summary>
    [Theory]
    [InlineData("fast", "thinking", AiTier.Thinking)]
    [InlineData(null, "thinking", AiTier.Thinking)]
    [InlineData("thinking", "fast", AiTier.Fast)]
    [InlineData("thinking", "thinking", AiTier.Thinking)]
    public async Task AnOverride_OutranksTheBookDefault(string? bookDefault, string stored, AiTier expected)
    {
        using var db = NewDb();
        var bookId = await SeedAsync(db, bookDefault, (AiTaskType.Proofread, stored));

        Assert.Equal(expected, await Resolve(db, bookId, AiTaskType.Proofread));
    }

    /// <summary>
    /// THE ISOLATION PROPERTY, and the reason the storage is keyed per task at all: an override is scoped to
    /// ONE task and every other task keeps following the book default. Asserted over every task rather than
    /// one neighbour, so a lookup that ignored TaskKey would fail here rather than pass by luck.
    /// </summary>
    [Fact]
    public async Task AnOverride_AppliesToItsOwnTaskOnly()
    {
        using var db = NewDb();
        var bookId = await SeedAsync(db, "fast", (AiTaskType.Proofread, "thinking"));

        Assert.Equal(AiTier.Thinking, await Resolve(db, bookId, AiTaskType.Proofread));
        foreach (var task in Enum.GetValues<AiTaskType>().Where(t => t != AiTaskType.Proofread))
            Assert.Equal(AiTier.Fast, await Resolve(db, bookId, task));
    }

    // ── Rung 2: the book default ──────────────────────────────────────────────────────────────────────────

    /// <summary>With no override, every task follows the book-level seed - the pre-c1 behaviour, unchanged.</summary>
    [Theory]
    [InlineData("thinking", AiTier.Thinking)]
    [InlineData("Thinking", AiTier.Thinking)]
    [InlineData("  thinking  ", AiTier.Thinking)]
    [InlineData("fast", AiTier.Fast)]
    [InlineData(null, AiTier.Fast)]
    public async Task WithNoOverride_TheBookDefaultApplies(string? bookDefault, AiTier expected)
    {
        using var db = NewDb();
        var bookId = await SeedAsync(db, bookDefault);

        foreach (var task in Enum.GetValues<AiTaskType>())
            Assert.Equal(expected, await Resolve(db, bookId, task));
    }

    // ── Rung 3 and the fail-safe branches: every doubt falls to Fast, and says so ──────────────────────────

    /// <summary>
    /// THE MOST IMPORTANT FAIL-SAFE BRANCH THIS TODO ADDS. An override row holding a token nobody recognises
    /// stops at Fast; it does NOT fall through to the book default, because the default can be "thinking" and
    /// doubt must never climb. A resolver written as "parse the override, else use the default" would return
    /// Thinking here - the one direction the invariant forbids.
    /// </summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("Thinking2")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AnUnrecognisedOverrideToken_ResolvesToFast_AndNeverClimbsToAThinkingDefault(string? stored)
    {
        using var db = NewDb();
        var logger = new AiTierCapturingLogger();
        var bookId = await SeedAsync(db, "thinking", (AiTaskType.Proofread, stored));

        Assert.Equal(AiTier.Fast, await Resolve(db, bookId, AiTaskType.Proofread, logger));

        // ... and it is visible. The task is named, so the log answers "why did this proofread run fast?".
        var warning = Assert.Single(logger.MessagesAt(LogLevel.Warning));
        Assert.Contains("Proofread", warning, StringComparison.Ordinal);
        Assert.Contains(bookId.ToString(), warning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A hand-edited or newer-build BOOK DEFAULT is the same doubt one rung down: fast, and logged with the
    /// task, because "everything on this book ran fast" and "this one task ran fast" are different
    /// investigations and the log has to distinguish them.
    /// </summary>
    [Theory]
    [InlineData("banana")]
    [InlineData("cloud")]
    public async Task AnUnrecognisedBookDefault_ResolvesToFast_AndNamesTheTask(string bookDefault)
    {
        using var db = NewDb();
        var logger = new AiTierCapturingLogger();
        var bookId = await SeedAsync(db, bookDefault);

        Assert.Equal(AiTier.Fast, await Resolve(db, bookId, AiTaskType.LinguisticAnalysis, logger));

        var warning = Assert.Single(logger.MessagesAt(LogLevel.Warning));
        Assert.Contains("LinguisticAnalysis", warning, StringComparison.Ordinal);
    }

    /// <summary>A NULL (or blank, the legacy shape of the same thing) default is the shipped state of every
    /// book and means fast. It is not doubt, so it must NOT warn - a branch that cries wolf on the default
    /// case trains the reader to ignore the ones that matter.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TheShippedBlankDefault_ResolvesToFast_Silently(string? bookDefault)
    {
        using var db = NewDb();
        var logger = new AiTierCapturingLogger();
        var bookId = await SeedAsync(db, bookDefault);

        Assert.Equal(AiTier.Fast, await Resolve(db, bookId, AiTaskType.Proofread, logger));
        Assert.Empty(logger.MessagesAt(LogLevel.Warning));
    }

    /// <summary>A book row that is not there resolves local, and says which task was being resolved.</summary>
    [Fact]
    public async Task AMissingBook_ResolvesToFast_AndNamesTheTask()
    {
        using var db = NewDb();
        var logger = new AiTierCapturingLogger();

        Assert.Equal(AiTier.Fast, await Resolve(db, Guid.NewGuid(), AiTaskType.Proofread, logger));

        var warning = Assert.Single(logger.MessagesAt(LogLevel.Warning));
        Assert.Contains("Proofread", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// No book id at all - a bare scene/chapter run. Fast, and logged at DEBUG rather than WARNING: this is a
    /// legitimate call shape, and warning on it would bury the branches that are actually bugs.
    /// </summary>
    [Fact]
    public async Task ANullOrEmptyBookId_ResolvesToFast_AndIsLoggedAtDebugWithTheTask()
    {
        using var db = NewDb();

        foreach (var bookId in new Guid?[] { null, Guid.Empty })
        {
            var logger = new AiTierCapturingLogger();
            Assert.Equal(AiTier.Fast, await Resolve(db, bookId, AiTaskType.LineEdit, logger));

            Assert.Empty(logger.MessagesAt(LogLevel.Warning));
            Assert.Contains("LineEdit", Assert.Single(logger.MessagesAt(LogLevel.Debug)), StringComparison.Ordinal);
        }
    }

    /// <summary>A DbContext whose <c>Book</c> set throws, so the catch-all branch is exercised for real
    /// rather than asserted about.</summary>
    private sealed class ThrowingDbContext : AppDbContext
    {
        public ThrowingDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public override DbSet<TEntity> Set<TEntity>()
            => typeof(TEntity) == typeof(Book)
                ? throw new InvalidOperationException("simulated database failure")
                : base.Set<TEntity>();
    }

    /// <summary>
    /// A database read that THROWS resolves local and logs the exception WITH the task. The alternative -
    /// letting it propagate - would fail the analysis, and the alternative to failing safe - defaulting to the
    /// stored intent - would route a manuscript to a third party on the strength of a value nobody could read.
    /// </summary>
    [Fact]
    public async Task ADatabaseFailure_ResolvesToFast_AndNamesTheTask()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-pertask-throw-{Guid.NewGuid()}").Options;
        using var db = new ThrowingDbContext(options);
        var logger = new AiTierCapturingLogger();

        Assert.Equal(AiTier.Fast, await Resolve(db, Guid.NewGuid(), AiTaskType.LinguisticAnalysis, logger));

        var warning = Assert.Single(logger.MessagesAt(LogLevel.Warning));
        Assert.Contains("LinguisticAnalysis", warning, StringComparison.Ordinal);
    }

    /// <summary>Cooperative cancellation is still preserved, so a cancelled analysis stops immediately rather
    /// than being swallowed into a fast-tier answer.</summary>
    [Fact]
    public async Task Cancellation_IsNotSwallowedIntoAFastAnswer()
    {
        using var db = NewDb();
        var bookId = await SeedAsync(db, "thinking");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BookAiTierResolver.ResolveAsync(
            db, bookId, AiTaskType.Proofread, NullLogger.Instance, cts.Token));
    }

    // ── The task key itself ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ARGUMENT FOR KEYING ON AiTaskType, pinned. <c>AnalysisTaskMapping</c> is MANY-TO-ONE, so four
    /// user-facing analysis types share the LinguisticAnalysis routing task. Storing per analysis type would
    /// let those four carry conflicting tiers with no way for the two freshness consumers - which ask about
    /// the routing task - to pick one, which is the "profiles read permanently stale" failure the shared
    /// resolver exists to prevent. Parsing normalizes them onto one key instead.
    /// </summary>
    [Theory]
    [InlineData("LinguisticAnalysis")]
    [InlineData("LiteraryAnalysis")]
    [InlineData("BookOverview")]
    [InlineData("CharacterAnalysis")]
    [InlineData("StoryAnalysis")]
    [InlineData("linguisticanalysis")]
    public void EveryAnalysisTypeThatRoutesToLinguisticAnalysis_NormalizesOntoTheOneTaskKey(string token)
    {
        Assert.True(AiTierPolicy.TryParseTaskKey(token, out var task));
        Assert.Equal(AiTaskType.LinguisticAnalysis, task);
        Assert.Equal("LinguisticAnalysis", AiTierPolicy.TaskKeyFor(task));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Proofreed")]
    [InlineData("0")]      // numeric enum values would otherwise parse and silently mean "Proofread"
    [InlineData("2")]
    [InlineData("-1")]
    public void AnUnrecognisedTaskToken_DoesNotParse(string? token)
        => Assert.False(AiTierPolicy.TryParseTaskKey(token, out _));

    /// <summary>
    /// THE LOOKUP IS STILL ONE STATEMENT, ON THE PROVIDER THAT ACTUALLY SHIPS. Every other test in this file
    /// runs on the in-memory provider, which evaluates LINQ in process and would therefore pass even if the
    /// correlated subquery could not be TRANSLATED to SQL at all - the runtime database is SQL Server, so that
    /// gap is the difference between green tests and a resolver that throws on every analysis in production
    /// (and, being fail-safe, silently ran every book local instead).
    ///
    /// What this pins: the two-table projection translates on SQL Server, and it comes out as ONE statement
    /// reading both tables rather than the book row plus a follow-up. The query is only translated, never
    /// executed, so it needs no database - the connection string is a placeholder.
    /// </summary>
    [Fact]
    public void ThePerTaskLookup_TranslatesToASingleSqlStatement()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\translation-only;Database=none")
            .Options;
        using var db = new AppDbContext(options);

        // THE RESOLVER'S OWN QUERY, not a copy of it - a copy would keep passing after the real one changed.
        var sql = BookAiTierResolver
            .StoredTiersQuery(db, Guid.NewGuid(), AiTierPolicy.TaskKeyFor(AiTaskType.Proofread))
            .ToQueryString();

        // Both tables are read...
        Assert.Contains("[Books]", sql, StringComparison.Ordinal);
        Assert.Contains("[BookAiTaskTiers]", sql, StringComparison.Ordinal);
        // ... by ONE statement. A second round trip would render as a second TOP-LEVEL SELECT (column 0);
        // the correlated subquery's SELECT is indented inside this one, and the parameter DECLAREs EF emits
        // above the query are not statements this resolver pays a round trip for.
        var topLevelSelects = sql
            .Split('\n')
            .Count(line => line.StartsWith("SELECT", StringComparison.Ordinal));
        Assert.True(topLevelSelects == 1,
            $"the per-task tier lookup translated to {topLevelSelects} top-level SELECT statements, so it is " +
            $"no longer one round trip:\n{sql}");
    }

    /// <summary>Every task the surface offers must be storable, or the toggle would advertise a control the
    /// write path rejects.</summary>
    [Fact]
    public void EveryUserFacingTask_RoundTripsThroughTheTaskKey()
    {
        foreach (var task in AiTierPolicy.UserFacingTasks)
        {
            Assert.True(AiTierPolicy.TryParseTaskKey(AiTierPolicy.TaskKeyFor(task), out var parsed));
            Assert.Equal(task, parsed);
        }

        // The two tasks the tier can actually MOVE are a subset of what the surface describes - the wider set
        // is deliberate (a user can launch LineEdit and BookReview and deserves an answer about them), not an
        // accidental widening of p2-4's allowlist.
        Assert.All(AiTierPolicy.TieredTasks, t => Assert.Contains(t, AiTierPolicy.UserFacingTasks));
    }
}

