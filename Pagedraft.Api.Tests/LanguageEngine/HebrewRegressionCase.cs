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
    /// <summary>
    /// Corrections the proofread engine must NOT emit — meaning-changing rewrites / overreach. Each
    /// entry names an erroneous edit (an OriginalText span and the over-reaching SuggestedText) that, if
    /// produced, counts as an overreach false positive even when the case ALSO has a legitimate expected
    /// correction at the same location. This captures the failure class the loose location-only matcher
    /// would otherwise miss: changing the RIGHT word to the WRONG (meaning-changing) replacement.
    /// A forbidden entry whose Suggested is left empty matches ANY produced replacement at that span,
    /// i.e. "this span must NOT be touched at all".
    /// </summary>
    public ProofreadCorrection[]? ForbiddenCorrections { get; set; }
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
