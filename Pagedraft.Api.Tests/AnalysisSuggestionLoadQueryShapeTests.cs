using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Pins the SQL of the analysis reads that used to <c>Include(a =&gt; a.Suggestions)</c> (plan
/// whole-book-proofread-and-annotated-docx-2026-10-11, c14): the chapter history list, the read by id, the read by
/// job id and the archive step of every re-run. The include joined each suggestion row to its result row, so the
/// result's <c>ResultText</c> and <c>SourceTextSnapshot</c> (a whole chapter each) were sent once PER SUGGESTION:
/// ~1.2 GB and 120 to 150 s per read on the owner's 1,476-suggestion chapter. Now the result query selects no
/// suggestion columns and the suggestion query selects no result columns. InMemory cannot see this (it moves no
/// bytes), so these compile the real queries against the SqlServer provider with
/// <see cref="EntityFrameworkQueryableExtensions.ToQueryString"/>; no connection is opened. Same technique as c12's
/// <see cref="BookExportProofreadQueryShapeTests"/>.
/// </summary>
public class AnalysisSuggestionLoadQueryShapeTests
{
    private static AppDbContext BuildSqlServerContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=(local);Database=AnalysisSuggestionLoadQueryShapeTests;Trusted_Connection=True;TrustServerCertificate=True;")
        .Options);

    [Fact]
    public void The_history_query_reads_result_columns_and_no_suggestion_rows()
    {
        using var db = BuildSqlServerContext();

        var sql = AnalysisController.ActiveAnalysesQuery(db, Guid.NewGuid(), Guid.NewGuid(), AnalysisType.Proofread).ToQueryString();

        // Non-vacuity floor: the result side is still read, once.
        Assert.Contains("FROM [AnalysisResults]", sql);
        Assert.Contains("[ResultText]", sql);
        // The regression: a join to the suggestions repeats the columns above per suggestion.
        Assert.DoesNotContain("[AnalysisSuggestions]", sql);
        // The order the include query had (see The_include_it_replaced_ordered_results_by_id).
        Assert.Contains("ORDER BY [a].[Id]", sql);
    }

    [Fact]
    public void The_suggestion_query_selects_no_result_column()
    {
        using var db = BuildSqlServerContext();

        var sql = AnalysisSuggestionLoad.OfResults(db, new[] { Guid.NewGuid(), Guid.NewGuid() }).ToQueryString();

        Assert.Contains("FROM [AnalysisSuggestions]", sql);
        Assert.Contains("[OriginalText]", sql);
        Assert.Contains("[ContextBefore]", sql);

        Assert.DoesNotContain("[AnalysisResults]", sql);
        Assert.DoesNotContain("[ResultText]", sql);
        Assert.DoesNotContain("[SourceTextSnapshot]", sql);
        Assert.DoesNotContain("[PromptUsed]", sql);
        Assert.DoesNotContain("[StructuredResult]", sql);
    }

    [Fact]
    public void The_archive_query_reads_no_suggestion_rows()
    {
        using var db = BuildSqlServerContext();

        var sql = UnifiedAnalysisService.PreviousActiveQuery(
            db, Guid.NewGuid(), Guid.NewGuid(), null, AnalysisScope.Chapter, AnalysisType.Proofread).ToQueryString();

        Assert.Contains("FROM [AnalysisResults]", sql);
        Assert.DoesNotContain("[AnalysisSuggestions]", sql);
    }

    /// <summary>
    /// Premise of the new history query's <c>OrderBy(a =&gt; a.Id)</c>: the include it replaced ordered results by id
    /// (EF's single-query include orders by the parent key), and the controller's stable sort on <c>CreatedAt</c>
    /// breaks ties in the order it receives. If this ever stops holding, the equality tests' reference is wrong too.
    /// </summary>
    [Fact]
    public void The_include_it_replaced_ordered_results_by_id()
    {
        using var db = BuildSqlServerContext();

        var sql = db.AnalysisResults.AsNoTracking()
            .Include(a => a.Suggestions)
            .Where(a => a.ChapterId == Guid.Empty && a.Status == AnalysisStatus.Active)
            .ToQueryString();

        Assert.Contains("ORDER BY [a].[Id]", sql);
    }

    /// <summary>
    /// No read in the API includes the suggestions on a result query any more (the four sites c14 found are the only
    /// ones there were). A new one would bring the per-suggestion repetition back; load them with
    /// <see cref="AnalysisSuggestionLoad"/> instead, or project, and pin the SQL here.
    /// </summary>
    [Fact]
    public void No_result_query_in_the_api_includes_its_suggestions()
    {
        var apiDir = FindApiProjectDirectory();
        var include = new Regex(@"\.Include\(\s*\w+\s*=>\s*\w+\.Suggestions\b");

        var files = Directory.EnumerateFiles(apiDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .ToList();
        Assert.True(files.Count > 50, $"Only {files.Count} source files found under {apiDir}; the scan would be vacuous.");

        var offenders = files.Where(f => include.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        Assert.Empty(offenders);
    }

    private static string FindApiProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Pagedraft.Api", "Pagedraft.Api.csproj");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate Pagedraft.Api/Pagedraft.Api.csproj above " + AppContext.BaseDirectory);
    }
}
