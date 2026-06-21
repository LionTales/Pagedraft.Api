using Xunit;

namespace Pagedraft.Api.Tests.LanguageEngine;

// SCOPE: This class unit-tests the OVERREACH-SCORING primitive used by the proofread bake-off —
// ProofreadQualityTests.ForbiddenMatch (the precision gate that decides whether a meaning-changing
// rewrite of the right word counts as overreach) together with the production normalization it is
// fed, ProofreadQualityTests.NormalizeForMatch. These are pure/deterministic and run with NO Ollama
// and NO cloud, unlike the skip-by-default live bake-off in ProofreadQualityTests.
//
// Each argument is run through NormalizeForMatch first, exactly as the live accounting loop in
// ProofreadQualityTests.ScoreModelAsync does (see lines ~418-431): it normalizes both the produced
// correction's original/suggested spans AND the forbidden entry's original/suggested before calling
// ForbiddenMatch.
//
// NOT directly unit-tested here (covered by code-read in the review): the surrounding accounting
// INVARIANT in the private ScoreModelAsync loop — a forbidden edit is counted in `produced` (so it
// lowers precision) but removed from the produced pool BEFORE expected-matching (so recall is not
// falsely inflated). That invariant needs a live model / fake provider to drive end-to-end and is
// intentionally out of scope here; this file pins ForbiddenMatch + NormalizeForMatch semantics only.
// Real Hebrew strings are taken from gold cases overreach-ms-01 and overreach-ms-02.
public class ProofreadOverreachScorerTests
{
    // Mirror the production accounting loop: normalize every span before ForbiddenMatch.
    private static bool Forbidden(string origP, string sugP, string origF, string sugF) =>
        ProofreadQualityTests.ForbiddenMatch(
            ProofreadQualityTests.NormalizeForMatch(origP),
            ProofreadQualityTests.NormalizeForMatch(sugP),
            ProofreadQualityTests.NormalizeForMatch(origF),
            ProofreadQualityTests.NormalizeForMatch(sugF));

    // (a) OVERREACH TRIPS — the meaning-changing rewrite of the RIGHT word. overreach-ms-02:
    // produced עתון→עתונות (the press, a noun) matches forbidden עתון→עתונות → counted as overreach.
    [Fact]
    public void MeaningChangingRewrite_OfRightWord_TripsOverreach()
    {
        Assert.True(Forbidden("עתון", "עתונות", "עתון", "עתונות"));
    }

    // (b) CORRECT FIX NOT FLAGGED — the real ktiv-male fix at the same span must NOT trip, so recall
    // for the genuine correction is preserved. overreach-ms-02: produced עתון→עיתון (right fix) vs
    // forbidden עתון→עתונות → false.
    [Fact]
    public void CorrectKtivFix_AtForbiddenSpan_NotFlagged()
    {
        Assert.False(Forbidden("עתון", "עיתון", "עתון", "עתונות"));
    }

    // (c) EMPTY FORBIDDEN SUGGESTED = MUST-NOT-TOUCH. overreach-ms-01 forbids ANY edit to רגשית
    // (empty suggested). A produced rewrite of that span (e.g. רגשית→רגשות) trips overreach...
    [Fact]
    public void EmptyForbiddenSuggested_AnyEditAtSpan_TripsOverreach()
    {
        Assert.True(Forbidden("רגשית", "רגשות", "רגשית", ""));
    }

    // ...but the legitimate ktiv fix at a DIFFERENT span (עצמה→עוצמה, the only correct fix in
    // overreach-ms-01) is NOT pulled out by that same empty-suggested רגשית forbidden entry → false.
    [Fact]
    public void EmptyForbiddenSuggested_DifferentSpanLegitFix_NotFlagged()
    {
        Assert.False(Forbidden("עצמה", "עוצמה", "רגשית", ""));
    }

    // (d) UNRELATED SPAN — a produced edit whose original is unrelated to the forbidden span is never
    // flagged, for both a non-empty-suggested forbidden entry and an empty-suggested (must-not-touch)
    // one.
    [Theory]
    [InlineData("חלון", "החלון", "עתון", "עתונות")] // unrelated span vs specific forbidden rewrite
    [InlineData("חלון", "החלון", "רגשית", "")]       // unrelated span vs must-not-touch span
    public void UnrelatedSpan_NeverFlagged(string origP, string sugP, string origF, string sugF)
    {
        Assert.False(Forbidden(origP, sugP, origF, sugF));
    }
}
