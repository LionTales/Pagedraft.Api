namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// Single Hebrew regression test case. Matches hebrew-regression.json schema.
/// See TestData/README.md for which fields the proofread-gold consumers actually read (several of
/// these are dead for that consumer), the id-prefix classification convention, and the
/// "how to add a character-agreement case" guidance.
/// </summary>
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

    /// <summary>
    /// Optional characters to inject as a <c>[CHARACTER_REGISTER]</c> block ahead of the proofread
    /// instruction, for cases whose expected fix depends on knowing a character's gender (the
    /// agreement class, ids <c>agree-*</c>). Reuses the production
    /// <see cref="Pagedraft.Api.Models.CharacterRegisterEntry"/> shape so the block is rendered by the
    /// PRODUCTION builder (<c>PromptFactory.BuildProofreadChunkPrompt</c>) rather than a look-alike —
    /// see <c>ProofreadQualityTests.BuildGoldRequest</c>.
    ///
    /// OPT-IN, PER CASE: when this is null/empty the harness leaves <c>AiRequest.Instruction</c> unset,
    /// which is byte-for-byte the pre-2026-08-02 behavior (short pipeline instruction alone). Every gold
    /// number measured before the agreement class was added therefore remains comparable. Cases that DO
    /// carry a register are measured on a DIFFERENT (and more production-like) prompt surface:
    /// <c>[CHARACTER_REGISTER] + ProofreadHe/En + "\n\n" + short pipeline instruction</c>. Do not add a
    /// register to a pre-existing case without saying so — it silently moves that case's surface.
    ///
    /// <c>gender</c> uses the English literals "male" | "female" | "unknown" even for Hebrew books
    /// (PromptFactory's character-extraction vocabulary).
    /// </summary>
    public Pagedraft.Api.Models.CharacterRegisterEntry[]? CharacterRegister { get; set; }
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
