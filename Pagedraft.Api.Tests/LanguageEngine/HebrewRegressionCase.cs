namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>Single Hebrew regression test case. Matches hebrew-regression.json schema.</summary>
public class HebrewRegressionCase
{
    public string Id { get; set; } = "";
    public string Input { get; set; } = "";
    public string? ExpectedNormalized { get; set; }
    public string[]? ExpectedIssueCategories { get; set; }
    public string? ExpectedRewriteSnippet { get; set; }
    public string Language { get; set; } = "he-IL";
    /// <summary>If true, expect at least one issue in this category (e.g. grammar).</summary>
    public bool? ExpectAtLeastOneIssue { get; set; }
}
