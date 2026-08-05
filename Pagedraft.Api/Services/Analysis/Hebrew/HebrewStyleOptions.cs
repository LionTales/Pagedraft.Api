namespace Pagedraft.Api.Services.Analysis.Hebrew;

/// <summary>
/// Deterministic Hebrew-orthography switches for the copyedit layer. Bound from the "Ai:HebrewStyle"
/// config section, mirroring the AiOptions / LanguageToolOptions IOptions idiom.
///
/// TWO KINDS OF SWITCH LIVE HERE and they are not interchangeable, so read each one's own remarks
/// rather than the section name: <see cref="EnforceKtivMale"/> is a HOUSE-STYLE preference (a
/// publisher may legitimately want it off), while
/// <see cref="DropOrthographicallyImpossibleSuggestions"/> is a correctness safety net whose only
/// reason to be a switch at all is to be a kill switch.
///
/// NOTE FOR DEPLOYMENTS: appsettings.Production.json declares NO "Ai:HebrewStyle" block, so the
/// values in the base appsettings.json survive the Production layer unchanged and are what a hosted
/// deployment runs. The CLASS defaults below therefore govern only programmatic and test
/// construction - which is exactly why they are kept identical to the shipped values, so a test
/// constructing this type by hand exercises the posture production actually runs.
/// <c>HebrewStyleConfigParityTests</c> pins both halves.
/// </summary>
public class HebrewStyleOptions
{
    public const string SectionName = "Ai:HebrewStyle";

    /// <summary>
    /// HOUSE STYLE. When true (default), the deterministic ktiv-male checker appends haser→male
    /// spelling suggestions to Hebrew proofread results. When false, the check is suppressed entirely.
    ///
    /// Ktiv-male (full-spelling) adoption is uneven and many publishers keep their own house style, so
    /// the enforcement check is opt-in/reversible. Default is ON because PageDraft is a Hebrew-first
    /// tool, but a house that does not normalize to ktiv-male can set it to false.
    /// </summary>
    public bool EnforceKtivMale { get; set; } = true;

    /// <summary>
    /// CORRECTNESS SAFETY NET, NOT HOUSE STYLE. When true (default), a proofread suggestion whose
    /// replacement text introduces a mechanically impossible Hebrew word - a final-form letter
    /// (ך ם ן ף ץ) in a non-final position - into a clean original span is withheld from the
    /// suggestion list. See <see cref="HebrewOrthographyShapeGuard"/> for the rule, its bound, and its
    /// measured reach.
    ///
    /// DEFAULT ON, and the reason is deliberately narrow. A suggestion of this shape is wrong in every
    /// register, dialect and house style, so there is no reader for whom showing it is the better
    /// outcome; the guard is bounded so that it can only remove a replacement that INTRODUCES the
    /// impossible word, which means it structurally cannot suppress a legal correction; and it makes
    /// no model call, so ON costs nothing per run. That is a different posture from
    /// <c>Ai:ProofreadPrompt:OverlapReferentLicence</c>, which defaults OFF because it CHANGES THE
    /// PROMPT and would therefore make every standing proofread number unreproducible. This switch
    /// changes no prompt and no model input; it only filters an output the model already produced.
    ///
    /// WHAT IT DOES NOT DO, so the default is not read as a fix for something it does not touch: on
    /// the corpus that motivated it, the rule reaches 1 of 128 suggestions and 0 of the ten Hebrew
    /// non-words the shipped prompt produced. It is a safety net for one impossible shape, not a
    /// remedy for model-authored non-words.
    ///
    /// KILL SWITCH: set false to restore the pre-guard suggestion list byte for byte. Every drop is
    /// logged at Warning and counted on <c>AnalysisRunLog.SuppressedSuggestionCount</c>, so a run that
    /// dropped three is distinguishable from a run that dropped none without turning the switch off.
    /// </summary>
    public bool DropOrthographicallyImpossibleSuggestions { get; set; } = true;
}
