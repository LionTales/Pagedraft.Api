namespace Pagedraft.Api.Services.Analysis.Hebrew;

/// <summary>
/// House-style toggles for the Hebrew copyedit layer. Bound from the "Ai:HebrewStyle"
/// config section, mirroring the AiOptions / LanguageToolOptions IOptions idiom.
///
/// Ktiv-male (full-spelling) adoption is uneven and many publishers keep their own house
/// style, so the enforcement check is opt-in/reversible. Default is ON because PageDraft is a
/// Hebrew-first tool, but a house that does not normalize to ktiv-male can set it to false and
/// no ktiv-male suggestions will surface.
/// </summary>
public class HebrewStyleOptions
{
    public const string SectionName = "Ai:HebrewStyle";

    /// <summary>
    /// When true (default), the deterministic ktiv-male checker appends haser→male spelling
    /// suggestions to Hebrew proofread results. When false, the check is suppressed entirely.
    /// </summary>
    public bool EnforceKtivMale { get; set; } = true;
}
