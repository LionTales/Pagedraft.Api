using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Analysis.Hebrew;

// Bound through a using ALIAS, not a namespace import, for the same reason as
// RealProsePrecisionFixtures: this file must NOT pull Pagedraft.Api.Tests.LanguageEngine into scope,
// because that is the namespace the standing deterministic test filter excludes.
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// RealProseNonWordResidue - THE HEBREW NON-WORD CLASS, AS A GOLD CLASS.
//
// WHAT THIS IS. On 2026-08-05 the real-prose arm measurement recorded, as an open lead it did not
// own, that gemma4:12b proposes character-level Hebrew NON-WORDS on clean, twice-proofread manuscript
// prose. Ten of them under the SHIPPED DEFAULT prompt and five under ARM A. The standing habit for
// this corpus is that every real proofread failure becomes a gold case; this file is that promotion.
//
// WHY THIS SURFACE AND NOT proofread-gold.json, stated plainly because the todo required a choice:
//   1. All fifteen instances were produced on GoldPromptSurface.ChunkedPerChunk - one model call per
//      chunk, each input wrapped in [TEXT_TO_CORRECT], each chunk after the first carrying a
//      [CONTEXT_BEFORE] overlap. The gold corpus CANNOT reach that regime: ProofreadQualityTests
//      .BuildGoldRequest composes through the 3-argument BuildProofreadChunkPrompt with
//      overlapPrefix: null, which is recorded as a STRUCTURAL fact at
//      ProofreadStandingFloor.GoldSurfaceCannotReachAPerChunkIntervention. A gold case carrying one
//      of these would ride ShortPipelineOnly (no character register) - the LEAST production-like of
//      the three surfaces and the furthest from where the failure was seen.
//   2. The CARRIER MATTERS. Each instance is a word inside a 250-word manuscript excerpt. Lifting the
//      sentence around it into an authored gold case would change the prose, the length, the chunk
//      count and the surface all at once, so a non-reproduction there would be uninterpretable: it
//      could mean the model is fine, or merely that four things changed.
//   3. The real-prose surface already exists, already holds this prose, already holds the arm
//      measurement these instances fell out of, and is already gated deterministically.
// So the class lives HERE, anchored to the passages, and proofread-gold.json is deliberately left
// alone. IF the class is ever shown to be chunk-INDEPENDENT (a single-shot run reproducing a
// non-word on a short passage), that is the moment to also promote it into proofread-gold.json - and
// that finding, not the habit alone, is what would justify it.
//
// WHAT IS ASSERTED AND WHAT IS ONLY RECORDED. The Hebrew READINGS below were flagged for the user by
// c1 and are UNCONFIRMED. They are therefore DATA, never a predicate: RealProseNonWordResidueTests
// asserts structural properties (each instance anchors in the prose it claims, the suggested token is
// not prose the manuscript already contained, the family counts reconcile with the arm measurement,
// the shape guard's reach) and never asserts a reading. Correcting a reading later is a data edit.
//
// THE ONE NUMBER MOST LIKELY TO BE MISQUOTED. RealProseArmMeasurement.UnownedResidue USED TO cite
// צמצם -> צמץם as one of "10 of the OFF arm's 47 edits"; that note has since been corrected and now
// says so itself. IT IS NOT AN OFF EDIT: it appears once in the whole 128-suggestion corpus, in ARM
// A, and it is the corpus's ONLY mechanically illegal suggestion. It is recorded below on the ARM A
// side, where it belongs, and RealProseNonWordResidueTests
// .TheOnlyMechanicallyIllegalInstance_IsRecordedUnderArmA_NotUnderTheShippedDefault asserts on the
// note itself, so the old wording cannot come back silently.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// How a proposed replacement fails. Both kinds are equally valid PRECISION gold: the assertion is
/// "the model should not have proposed this edit on clean, twice-proofread text", which does not
/// depend on whether the output happens to be a word.
/// </summary>
public enum NonWordKind
{
    /// <summary>The replacement is not a Hebrew token at all in any context.</summary>
    NotAToken,

    /// <summary>
    /// The replacement IS a legal Hebrew token, and is wrong only in this context. Recorded
    /// separately because no shape-based or lexicon-based check can ever reach these, so counting
    /// them among "detectable non-words" would overstate what any deterministic guard can do.
    /// </summary>
    LegalTokenWrongInContext
}

/// <summary>
/// One recorded non-word instance: which passage it came from, which arm produced it, the edit
/// verbatim, and the (UNCONFIRMED) reading.
/// </summary>
/// <param name="Ordinal">Position in its arm's list. Diagnostics only.</param>
/// <param name="PassageId">A <see cref="RealProsePrecisionFixtures"/> passage id.</param>
/// <param name="Arm">The prompt arm whose run produced it.</param>
/// <param name="Original">The manuscript span, verbatim. Occurs in the passage's clean text.</param>
/// <param name="Suggested">The replacement the model proposed, verbatim.</param>
/// <param name="RawChunkIndex">
/// Which chunk of the two-chunk passage carried it in the recorded raw response. Every passage is
/// exactly two chunks, so 1 means the instance arose on a chunk that carried a [CONTEXT_BEFORE]
/// overlap and 0 means it did not.
/// </param>
/// <param name="Kind">See <see cref="NonWordKind"/>.</param>
/// <param name="Reading">
/// c1's letter-level reading. UNCONFIRMED by a Hebrew speaker and deliberately not asserted on.
/// </param>
public sealed record RealProseNonWordInstance(
    int Ordinal,
    string PassageId,
    ProofreadPromptArm Arm,
    string Original,
    string Suggested,
    int RawChunkIndex,
    NonWordKind Kind,
    string Reading)
{
    /// <summary>
    /// Whether the shipped deterministic shape guard would withhold this suggestion. COMPUTED by
    /// calling <see cref="HebrewOrthographyShapeGuard"/> itself rather than declared, so the recorded
    /// reach of the guard cannot drift away from the guard's actual behaviour: widen the rule and this
    /// number moves on its own, and the test that pins it turns red.
    /// </summary>
    public bool ShapeGuardWouldDrop =>
        HebrewOrthographyShapeGuard.WouldDrop(Original, Suggested, out _);

    /// <summary>The passage this instance is anchored to. Throws on an id no fixture carries.</summary>
    public RealProsePassage Passage => RealProsePrecisionFixtures.ById(PassageId);
}

/// <summary>The fifteen recorded instances and the readings that go with them. See the file header.</summary>
public static class RealProseNonWordResidue
{
    /// <summary>The session these instances were extracted from.</summary>
    public const string MeasuredOn = RealProseArmMeasurement.MeasuredOn;

    /// <summary>Model. The same one every other number in this corpus was measured on.</summary>
    public const string MeasuredOnModel = RealProseArmMeasurement.MeasuredOnModel;

    /// <summary>
    /// WHERE THEY CAME FROM, proved rather than assumed. c1 diffed the raw model responses recorded at
    /// the IAiRouter seam against the merged results and found every one of the 125 non-empty
    /// suggestions in the corpus present, as a whole TOKEN, in a raw response - and zero tokens in any
    /// merged result that no raw response emitted. The post-model passes were cleared individually:
    /// AnalysisRepairService and DynamicTermRepairService are never INVOKED on the chunked proofread
    /// path (ApplyAnalysisRepairAsync has no call site inside RunProofreadChunkedAsync), and
    /// KtivMaleChecker DID run under the production default and authored nothing - 0 hits on 11,612
    /// Hebrew words of recorded input.
    /// </summary>
    public const string Origin =
        "RAW MODEL. Every instance is present as a whole token in the raw model response recorded at " +
        "the IAiRouter seam. No post-model pass authored, mutated or manufactured any of them; the " +
        "two repair services are never invoked on the chunked proofread path and the ktiv-male " +
        "checker ran and produced nothing. Attribution artifacts: " +
        "docs/measurements/arm-a-real-prose-2026-08-05/c1-attribution/ORIGIN-TABLE.md.";

    /// <summary>
    /// THE SIZING THAT SCOPES ANY DETERMINISTIC FIX, and the sentence most worth re-reading before
    /// funding one. A zero-lexicon shape rule reaches a CORNER of this class, not the class.
    /// </summary>
    public const string WhatADeterministicGuardCanReach =
        "A zero-lexicon shape rule (a Hebrew final form in a non-final position) reaches 0 of the ten " +
        "instances the SHIPPED DEFAULT produced and 1 of the five ARM A produced - one suggestion in " +
        "128 across the whole corpus. Two of the ten (הזעת, שהמרור) are legal Hebrew tokens that are " +
        "wrong only in context, so no shape rule and no lexicon could reach them either. The shipped " +
        "guard (HebrewOrthographyShapeGuard) is a safety net for one impossible shape and is NOT a " +
        "remedy for this class; anything that claims otherwise is resting on a premise this data " +
        "refutes.";

    /// <summary>
    /// THE TEN, under the shipped default prompt (ProofreadPromptArm.Off). Reconstructed by c1 from
    /// the recorded raw responses and forced from both directions against the arm measurement's own
    /// family counts; see the ORIGIN-TABLE artifact for that reconciliation.
    /// </summary>
    public static readonly IReadOnlyList<RealProseNonWordInstance> Ten = new[]
    {
        new RealProseNonWordInstance(1, RealProsePrecisionFixtures.NarrationNoQuotesId,
            ProofreadPromptArm.Off, "להירדם", "להירד", 0, NonWordKind.NotAToken,
            "the word-final mem is chopped off the infinitive; להירד is not a Hebrew infinitive " +
            "(the verb for 'to go down' is לרדת)."),

        new RealProseNonWordInstance(2, RealProsePrecisionFixtures.DialogueMidId,
            ProofreadPromptArm.Off, "להתחיל", "להתים", 0, NonWordKind.NotAToken,
            "het+lamed replaced by a final mem; in context ('why can't we start directly') the " +
            "clause becomes gibberish."),

        new RealProseNonWordInstance(3, RealProsePrecisionFixtures.DialogueMidId,
            ProofreadPromptArm.Off, "יישמע", "יישבמע", 1, NonWordKind.NotAToken,
            "a bet inserted mid-word; no such string exists."),

        new RealProseNonWordInstance(4, RealProsePrecisionFixtures.NarrationNoQuotesTwoId,
            ProofreadPromptArm.Off, "הקטנה", "הקטענה", 0, NonWordKind.NotAToken,
            "an ayin inserted into 'the small (one)'."),

        new RealProseNonWordInstance(5, RealProsePrecisionFixtures.DialogueMidTwoId,
            ProofreadPromptArm.Off, "הסתמיות", "הסתמיתות", 0, NonWordKind.NotAToken,
            "a tav inserted into the plural."),

        new RealProseNonWordInstance(6, RealProsePrecisionFixtures.DialogueVeryHighId,
            ProofreadPromptArm.Off, "שהאימון הזה", "שהאימנוג", 0, NonWordKind.NotAToken,
            "two words collapsed into one non-word ending in gimel."),

        new RealProseNonWordInstance(7, RealProsePrecisionFixtures.DialogueVeryHighId,
            ProofreadPromptArm.Off, "עכשיו", "עכשור", 0, NonWordKind.NotAToken,
            "yod+vav replaced by vav+resh."),

        new RealProseNonWordInstance(8, RealProsePrecisionFixtures.DialogueVeryHighId,
            ProofreadPromptArm.Off, "הזאת", "הזעת", 1, NonWordKind.LegalTokenWrongInContext,
            "alef -> ayin. The RESULT is a legal token (הזעת = sweating / 'you sweated') but absurd " +
            "for 'this (feeling)'."),

        new RealProseNonWordInstance(9, RealProsePrecisionFixtures.InteriorLowId,
            ProofreadPromptArm.Off, "שהמרמור", "שהמרור", 0, NonWordKind.LegalTokenWrongInContext,
            "a mem deleted. The RESULT is a legal token (המרור = the bitter herb) but absurd for " +
            "'his bitterness'."),

        new RealProseNonWordInstance(10, RealProsePrecisionFixtures.BanterVeryHighId,
            ProofreadPromptArm.Off, "נכריח", "נכריץ", 0, NonWordKind.NotAToken,
            "het -> final tsadi. The SHAPE is legal (a final form at word end) but there is no verb " +
            "נכריץ. This is the instance that shows a shape rule and a word check are different " +
            "questions."),
    };

    /// <summary>
    /// ARM A's five, recorded beside the ten because the class is ARM-INVARIANT IN KIND: ARM A stopped
    /// nine of the ten and manufactured four of its own, so nothing about it is a property of the arm
    /// that was under test. One instance (נכריח -> נכריץ) is shared and appears in BOTH lists.
    /// </summary>
    public static readonly IReadOnlyList<RealProseNonWordInstance> ArmAFive = new[]
    {
        new RealProseNonWordInstance(1, RealProsePrecisionFixtures.ArgumentMidId,
            ProofreadPromptArm.OverlapReferentLicence, "מחפש", "מחבש", 0, NonWordKind.NotAToken,
            "pe -> bet."),

        new RealProseNonWordInstance(2, RealProsePrecisionFixtures.DialogueVeryHighId,
            ProofreadPromptArm.OverlapReferentLicence, "והושטתי", "והושטי", 0, NonWordKind.NotAToken,
            "a tav dropped out of the first-person past form."),

        new RealProseNonWordInstance(3, RealProsePrecisionFixtures.BanterVeryHighId,
            ProofreadPromptArm.OverlapReferentLicence, "נכריח", "נכריץ", 0, NonWordKind.NotAToken,
            "the one instance SHARED with the shipped default's ten; see Ten[9]."),

        new RealProseNonWordInstance(4, RealProsePrecisionFixtures.BanterVeryHighId,
            ProofreadPromptArm.OverlapReferentLicence, "צמצם", "צמץם", 0, NonWordKind.NotAToken,
            "a final tsadi placed mid-word. The corpus's ONLY mechanically illegal suggestion, 1 of " +
            "128 - and it is ARM A's, not the shipped default's, which is the misattribution " +
            "RealProseArmMeasurement.UnownedResidue used to carry and no longer does."),

        new RealProseNonWordInstance(5, RealProsePrecisionFixtures.BanterVeryHighId,
            ProofreadPromptArm.OverlapReferentLicence, "הוא נאנח", "האנה", 0, NonWordKind.NotAToken,
            "two words collapsed."),
    };

    /// <summary>Both arms' instances, in report order.</summary>
    public static IReadOnlyList<RealProseNonWordInstance> All =>
        Ten.Concat(ArmAFive).ToArray();

    /// <summary>
    /// The surface every instance was produced on. Single-sourced from the shared surface vocabulary
    /// rather than re-declared, so this class cannot drift out of the corpus's one partition.
    /// </summary>
    public const GoldPromptSurface Surface = GoldPromptSurface.ChunkedPerChunk;

    /// <summary>
    /// THE REGRESSION QUERY. True when <paramref name="passageId"/> saw exactly this edit again, which
    /// is what a future run on this surface scores itself against. Exposed as a method rather than
    /// leaving callers to hand-match strings so a re-measurement cannot quietly compare a normalized
    /// string against a raw one.
    /// </summary>
    public static bool IsRecordedInstance(string passageId, string original, string suggested) =>
        All.Any(i =>
            string.Equals(i.PassageId, passageId, StringComparison.Ordinal) &&
            string.Equals(i.Original, original, StringComparison.Ordinal) &&
            string.Equals(i.Suggested, suggested, StringComparison.Ordinal));

    /// <summary>Instances the shipped shape guard would withhold. COMPUTED; see the instance property.</summary>
    public static IReadOnlyList<RealProseNonWordInstance> ShapeGuardReaches =>
        All.Where(i => i.ShapeGuardWouldDrop).ToArray();

    // ── the tokenizer the tests anchor with ──────────────────────────────────────────────────────

    /// <summary>
    /// Maximal runs of Hebrew base letters in <paramref name="text"/>. The tests anchor on WHOLE
    /// TOKENS rather than substrings for a concrete reason: להירד is a substring of the manuscript's
    /// own להירדם, so a substring test would report the model's non-word as prose that was always
    /// there and the anchoring assertion would silently invert.
    /// </summary>
    public static IReadOnlyList<string> HebrewTokens(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] < 'א' || text[i] > 'ת')
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && text[i] >= 'א' && text[i] <= 'ת')
                i++;
            tokens.Add(text[start..i]);
        }

        return tokens;
    }
}
