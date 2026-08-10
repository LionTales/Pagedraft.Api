using System.Linq;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Binds the "one query, SQL-side aggregates" claim in <see cref="BooksController.WithCounts"/> (used by
/// both <c>GetAll</c> and <c>Update</c> - NIT 65 of the wave-3 review) to the actual generated SQL, via
/// <see cref="EntityFrameworkQueryableExtensions.ToQueryString"/>.
///
/// This CANNOT run on the EF InMemory provider the rest of <see cref="Wave3StageSignalContractTests"/> uses:
/// InMemory executes every query client-side and does not implement <c>ToQueryString</c>, so it would
/// silently accept a future regression to a per-row fetch (NIT 66). This test builds a real
/// <c>AppDbContext</c> against the SqlServer provider instead - no connection is ever opened, since
/// <c>ToQueryString</c> only compiles the LINQ expression tree to SQL text, so this is safe to run without a
/// database.
/// </summary>
public class BooksControllerQueryShapeTests
{
    private static AppDbContext BuildSqlServerContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=BooksControllerQueryShapeTests;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void WithCounts_IsOneQuery_WithBothCountsAsSqlAggregates_NotAChapterRowFetch()
    {
        using var db = BuildSqlServerContext();

        var sql = BooksController.WithCounts(db.Books.AsNoTracking().OrderBy(b => b.UpdatedAt)).ToQueryString();

        // Non-vacuity floor: a translation failure or an empty/whitespace query string would make every
        // assertion below pass for the wrong reason.
        Assert.False(string.IsNullOrWhiteSpace(sql));

        // Exactly one top-level SELECT against Books - not one query per book (an N+1 regression would
        // produce a single query TEXT here too, since ToQueryString only prints what THIS IQueryable
        // compiles to; the shape assertions below are what actually catch that class of regression).
        Assert.Equal(1, CountOccurrences(sql, "FROM [Books]"));

        // Both counts must be aggregates computed by SQL Server, not values pulled into .NET and counted in
        // memory. A per-row-fetch regression (e.g. projecting the chapter rows and counting them in C#)
        // would either drop these aggregate functions entirely or select individual chapter columns instead.
        Assert.Equal(2, CountOccurrences(sql, "COUNT(*)"));

        // The regression this test exists to catch: pulling whole Chapter rows into the result set instead
        // of aggregating them. ContentSfdt/ContentText only appear in the SQL if a chapter's full row (or
        // this heavy column specifically) is being selected rather than counted.
        Assert.DoesNotContain("ContentSfdt", sql);
        Assert.DoesNotContain("ContentText", sql);
    }

    [Fact]
    public void WithCounts_FilteredToOneBook_IsStillOneQuery_MirroringGetAll()
    {
        // Update's shape must match GetAll's exactly (NIT 65: symmetry) - same helper, filtered instead of
        // ordered. Prove that filtering to a single book does not change the query count or its aggregate
        // shape.
        using var db = BuildSqlServerContext();
        var bookId = System.Guid.NewGuid();

        var sql = BooksController.WithCounts(db.Books.AsNoTracking().Where(b => b.Id == bookId)).ToQueryString();

        Assert.False(string.IsNullOrWhiteSpace(sql));
        Assert.Equal(1, CountOccurrences(sql, "FROM [Books]"));
        Assert.Equal(2, CountOccurrences(sql, "COUNT(*)"));
        Assert.DoesNotContain("ContentSfdt", sql);
        Assert.DoesNotContain("ContentText", sql);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
