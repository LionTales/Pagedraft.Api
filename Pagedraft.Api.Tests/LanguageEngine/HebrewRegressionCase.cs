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
    /// <summary>Full corrected text the proofread engine should produce, if applicable.</summary>
    public string? ExpectedCorrectedText { get; set; }
    /// <summary>Ordered set of individual corrections the proofread engine should emit.</summary>
    public ProofreadCorrection[]? ExpectedCorrections { get; set; }
    /// <summary>If true, expect the proofread engine to return no changes for this input.</summary>
    public bool? ShouldHaveNoChanges { get; set; }
}

/// <summary>A single correction entry emitted by the proofread engine.</summary>
public class ProofreadCorrection
{
    /// <summary>The original (erroneous) text span.</summary>
    public string Original { get; set; } = "";
    /// <summary>The suggested replacement text.</summary>
    public string Suggested { get; set; } = "";
    /// <summary>Optional category label (e.g. "grammar", "spelling", "punctuation").</summary>
    public string? Category { get; set; }
}
