namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Prompt-side switches for the PER-CHUNK proofread instruction
/// (<see cref="PromptFactory.BuildProofreadChunkPrompt"/>). Bound from the "Ai:ProofreadPrompt"
/// config section, mirroring the <c>HebrewStyleOptions</c> / <c>AiOptions</c> IOptions idiom.
///
/// EVERY SWITCH HERE DEFAULTS OFF. The shipped per-chunk prompt is whatever an unconfigured
/// PromptFactory composes, so a deployment that never sets this section gets byte-for-byte the
/// prompt it got before the section existed. That identity is not a convention - it is pinned by a
/// deterministic test (see <c>ProofreadPromptArmTests</c>), because an arm behind a config flag whose
/// OFF path drifted from the legacy path would silently make every standing proofread number
/// unreproducible.
/// </summary>
public class ProofreadPromptOptions
{
    public const string SectionName = "Ai:ProofreadPrompt";

    /// <summary>
    /// ARM A of <c>referent-carry-forward-2026-08-04</c>, RE-LANDED (default OFF) so its measured
    /// side effect could be tested on real prose. That measurement has since run and CLOSED the lead
    /// (see WHY IT IS OFF below).
    ///
    /// WHAT IT DOES: extends the <c>[CONTEXT_BEFORE]</c> line of the base proofread prompt with an
    /// explicit licence to RESOLVE the pronouns in the text-to-correct against the overlap, and to
    /// make verb / adjective / pronoun agreement follow the resolved character. The added text is
    /// <see cref="PromptFactory.OverlapReferentLicenceHe"/> /
    /// <see cref="PromptFactory.OverlapReferentLicenceEn"/> and is VERBATIM the arm that was measured
    /// (a paraphrase of a prompt is not that prompt).
    ///
    /// WHY IT IS OFF. The arm was REJECTED on the axis it was built for: chunked agreement recall did
    /// not move (0/15 on its own acceptance fixture). What it DID move, on an axis its decision rule
    /// did not govern, was over-correction: ~38% fewer spurious edits across three chunked fixtures
    /// while the arm-invariant single-shot control stayed flat. That lead was measured on 2026-08-05
    /// on the real-prose surface (<c>RealProseArmMeasurement</c>) and DID NOT REPRODUCE: better on only
    /// 5 of 12 passages, one-sided p=0.363, verdict DO NOT SHIP. The lead is CLOSED after two openings;
    /// the switch is kept only to make a future measurement cheap (a session, not a re-implementation).
    /// See <c>ProofreadStandingFloor.RetiredInterventions</c>
    /// (id <c>referent-carry-forward.ARM_A.OverlapLicence</c>) for the numbers and the verdict.
    ///
    /// SCOPE, AND IT IS DELIBERATE: this reaches the PER-CHUNK builder ONLY. The single-shot proofread
    /// path composes through <c>GetAnalysisPrompt</c> and is NOT touched, which is exactly where the
    /// original arm rendered and did not render. Widening the scope would measure a different arm.
    /// </summary>
    public bool OverlapReferentLicence { get; set; }
}
