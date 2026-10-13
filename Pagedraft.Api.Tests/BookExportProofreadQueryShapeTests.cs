using System;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Pins the SQL of the annotated export's suggestion load (plan whole-book-proofread-and-annotated-docx-2026-10-11,
/// c12). The shipped load INCLUDED whole Active Proofread result rows, so each result's <c>ResultText</c> and
/// <c>SourceTextSnapshot</c> (a whole chapter each) rode on every joined pending-suggestion row: 1.33 GB for the
/// owner's 55-chapter book, minutes of SqlClient async reads before the first chapter rendered, while the
/// annotation itself took seconds. No EF InMemory test can see that (InMemory runs the query client-side and
/// never moves a byte), so this compiles the real query against the SqlServer provider with
/// <see cref="EntityFrameworkQueryableExtensions.ToQueryString"/>; no connection is opened. Same technique as
/// <see cref="BooksControllerQueryShapeTests"/>.
/// </summary>
public class BookExportProofreadQueryShapeTests
{
    [Fact]
    public void The_suggestion_load_selects_no_chapter_sized_result_column()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=BookExportProofreadQueryShapeTests;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options);

        var sql = BookExportService.ActiveProofreadUnitsQuery(db, new[] { Guid.NewGuid(), Guid.NewGuid() }).ToQueryString();

        // Non-vacuity floor: an empty or untranslated query would pass every DoesNotContain below.
        Assert.False(string.IsNullOrWhiteSpace(sql));
        Assert.Contains("FROM [AnalysisResults]", sql);
        Assert.Contains("[AnalysisSuggestions]", sql);
        Assert.Contains("[OriginalText]", sql); // the suggestions themselves are still loaded

        // The regression: any of these on the result side is a whole chapter (or a whole prompt) per joined row.
        Assert.DoesNotContain("[ResultText]", sql);
        Assert.DoesNotContain("[SourceTextSnapshot]", sql);
        Assert.DoesNotContain("[PromptUsed]", sql);
        Assert.DoesNotContain("[StructuredResult]", sql);
    }
}
