using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and the whole point of this file's location is to be outside it.
// Same rule (and same reason) as ProofreadEnglishGoldTests.
using ProofreadQualityTests = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;
using HebrewRegressionCase = Pagedraft.Api.Tests.LanguageEngine.HebrewRegressionCase;
using ProofreadCorrection = Pagedraft.Api.Tests.LanguageEngine.ProofreadCorrection;
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;
using GoldPromptSurfaces = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurfaces;
using GoldCaseScore = Pagedraft.Api.Tests.LanguageEngine.GoldCaseScore;
using ModelScore = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests.ModelScore;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC, NO-MODEL guard for the character-AGREEMENT gold class (`agree-*` in
/// proofread-gold.json) and for the per-case character-register injection it introduced.
/// The consumer of this data is a live-GPU benchmark that is skip-by-default, so nothing else in a
/// standing gate would notice a malformed agreement entry, a register that stops rendering, or —
/// the expensive one — the register path silently changing the prompt surface of the PRE-EXISTING
/// gold cases, which would invalidate every historical number in that file. (No count here on purpose:
/// the exact split is asserted, once, by PromptSurfaceSplit_OfTheHebrewGold_*.)
///
/// WHAT IT PINS
///  1. The class exists and is a population, not an anecdote: the three id-prefix buckets are present
///     with the axes the plan requires (name-evident recall / register-only recall / preservation).
///  2. Register entries compose the PRODUCTION prompt: the [CHARACTER_REGISTER] block is byte-identical
///     to what PromptFactory emits, it is followed by the ProofreadHe body that explains what the block
///     is FOR, and the router's short pipeline instruction is appended after both.
///  3. Register-less entries are byte-for-byte unchanged (short pipeline instruction ALONE), and no
///     pre-existing case has acquired a register.
///  4. The near-miss forbidden entries can never fire on the CORRECT fix. Agreement fixes are
///     single-letter, and both ForbiddenMatch and CorrectionsMatch use substring-tolerant span
///     matching, so a badly chosen forbidden `suggested` (e.g. the bare masculine form, which is a
///     substring of the feminine one) would silently convert every correct fix into an overreach and
///     zero out that entry's recall. This asserts through the SHIPPED matcher, not a reimplementation.
///  5. The SPAN + CONTENT invariants ForbiddenMatch's substring tolerance rests on (section 4b), over
///     EVERY forbidden entry of the file rather than the agree-* buckets alone, because the matcher is
///     shared: a forbidden span must occur in its own input, be a whole word at every occurrence, and
///     (when its `suggested` is empty) not contain a word the input uses elsewhere; a near-miss
///     `suggested` must additionally not be a no-op, not be the correct fix, and be in the case's script.
///  6. The PROMPT-SURFACE partition the two live reports are scoped by (section 5): that it splits the
///     file the way it does today, that it agrees case-for-case with the real BuildGoldRequest (proved
///     against a synthetic probe that holds the id axis and the register axis apart), and that its
///     degenerate paths (empty run, single-surface run, English gold) aggregate finitely.
///
/// The composed-prompt assertions drive the REAL AiRouter with a capturing provider — the same seam
/// AiRouterTests uses — so what is asserted is the string a provider would actually receive.
///
/// FILE SIZE: this class is past the workspace's ~700-line soft ceiling, WAIVED ON PURPOSE rather than
/// split. Everything here guards ONE artifact (proofread-gold.json) through ONE set of shared span
/// helpers, and four of these tests were rewired in a single pass; splitting the class would duplicate
/// those helpers or export them, which is a larger correctness risk than the length. Revisit if a
/// second gold file needs the same guards, at which point the helpers, not the tests, are what move.
///
/// See TestData/README.md ("How to add a character-agreement case") for the schema, the
/// attribute-driven authoring rule, the near-miss forbiddenCorrections trap, and the prompt-surface
/// split before adding a new agree-* entry.
/// </summary>
public class ProofreadAgreementGoldTests
{
    private const string NamePrefix = "agree-name-";
    private const string RegisterPrefix = "agree-register-";
    private const string PreservePrefix = "agree-preserve-";
    private const string AgreementPrefix = "agree-";

    private static HebrewRegressionCase[] LoadGold() => ProofreadQualityTests.LoadProofreadGold();

    private static HebrewRegressionCase[] Bucket(string prefix) =>
        LoadGold().Where(c => c.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    private static HebrewRegressionCase Case(string id) =>
        LoadGold().Single(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    // ── 1. the class is loadable and is a population ──────────────────────────────────────────────

    [Fact]
    public void AgreementClass_LoadsFromTheGold_WithAllThreeBuckets()
    {
        var cases = LoadGold();
        Assert.NotEmpty(cases);

        // Ids stay unique across the WHOLE file (the bake-off subsets by id).
        var ids = cases.Select(c => c.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        Assert.NotEmpty(Bucket(NamePrefix));
        Assert.NotEmpty(Bucket(RegisterPrefix));
        Assert.NotEmpty(Bucket(PreservePrefix));

        // A population, not a handful of anecdotes: the plan's target is roughly 15-25 entries.
        var agreement = Bucket(AgreementPrefix);
        Assert.InRange(agreement.Length, 15, 25);
        Assert.All(agreement, c => Assert.StartsWith("he", c.Language, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AgreementRecallEntries_AreWellFormed_AndCarryARegister()
    {
        var recall = Bucket(NamePrefix).Concat(Bucket(RegisterPrefix)).ToArray();
        Assert.NotEmpty(recall);

        Assert.All(recall, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Input), $"{c.Id}: empty input");

            // Every recall entry states what must be fixed...
            Assert.True((c.ExpectedCorrections?.Length ?? 0) > 0, $"{c.Id}: no expectedCorrections");
            Assert.All(c.ExpectedCorrections!, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Original), $"{c.Id}: expected correction with empty original");
                Assert.False(string.IsNullOrWhiteSpace(e.Suggested), $"{c.Id}: expected correction with empty suggested");
                Assert.Contains(e.Original, c.Input);
            });

            // ...and is NOT a shouldHaveNoChanges case.
            Assert.NotEqual(true, c.ShouldHaveNoChanges);

            // ...and carries the register, because the whole class is measured on the register surface.
            Assert.True((c.CharacterRegister?.Length ?? 0) > 0, $"{c.Id}: recall entry without a characterRegister");
            Assert.All(c.CharacterRegister!, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Name), $"{c.Id}: register entry without a name");
                Assert.True(e.Gender is "male" or "female" or "unknown",
                    $"{c.Id}: register gender '{e.Gender}' is not one of the production literals");
            });

            // ...and names the plausible WRONG form at EVERY span it expects fixed, so a
            // right-span/wrong-form "fix" shows up in the overreach column instead of being credited
            // as recall by the span-only matcher.
            AssertEveryExpectedFixCarriesANearMissGuard(c);

            // ...and every forbidden span it declares is really a WORD of its own input. Preservation
            // entries asserted a weaker version of this from the start; recall entries asserted nothing,
            // so a forbidden naming a span that is absent (inert) or an infix of another word (aligned on
            // a fragment) would have gone unnoticed on exactly the entries whose recall it guards.
            var strayForbiddens = NonWordAlignedForbiddens(c);
            Assert.True(strayForbiddens.Count == 0, string.Join("\n  ", strayForbiddens));
        });
    }

    /// <summary>
    /// THE NEAR-MISS COVERAGE RULE, enforced per EXPECTED CORRECTION rather than per case.
    ///
    /// TestData/README.md ("The near-miss trap") states the rule per expected fix: every recall entry
    /// must pair ITS EXPECTED FIX with a non-empty <c>forbiddenCorrections</c> entry naming the plausible
    /// wrong form AT THE SAME SPAN. The reason is in the scorer: <c>CorrectionsMatch</c> credits a recall
    /// hit on span alignment ALONE (right erroneous span, ANY replacement), and Hebrew agreement repairs
    /// are single-letter, so an unguarded expected span silently scores a wrong-gender "fix" as a HIT.
    /// A per-CASE check ("this entry lists SOME wrong form") does not cover a multi-span entry: it passed
    /// for agree-register-03, which declares three expected fixes and used to guard only one of them,
    /// leaving 2 of the 10 expected corrections in the register-only recall bucket unguarded.
    ///
    /// SELF-SUFFICIENT ON PURPOSE. Both the non-emptiness floor and the per-element requirement live
    /// HERE, not in the caller. Expressed in the caller as an <c>Assert.All</c> over the expected
    /// corrections, the rule would evaporate the moment a caller's input stopped producing elements
    /// (an empty or absent <c>expectedCorrections</c> array asserts nothing at all).
    ///
    /// "Same span" is measured positionally in the input via <see cref="SpansOverlapInInput"/>, the SAME
    /// notion <see cref="ForbiddenNearMissEntries_DoTripOnAWrongFormEditAtTheRightSpan"/> pairs on, so
    /// the guard this demands to EXIST is exactly the guard that test then proves FIRES. Deriving it from
    /// ForbiddenMatch's own containment test instead would accept a forbidden the matcher aligns by
    /// accident, which is the tautology that test was rewritten to escape.
    /// </summary>
    private static void AssertEveryExpectedFixCarriesANearMissGuard(HebrewRegressionCase c)
    {
        var expected = c.ExpectedCorrections ?? Array.Empty<ProofreadCorrection>();
        Assert.True(expected.Length > 0,
            $"{c.Id}: a recall entry with no expectedCorrections cannot be guarded, and would make the " +
            "per-expected-correction near-miss rule below vacuous for this case.");

        // Only the near-miss shape counts as a guard. A forbidden with an EMPTY suggested means "must
        // not touch this span at all"; on a span that also carries a correct expected fix it would
        // swallow the correct fix itself (README: "Do NOT leave Suggested empty on a forbidden that
        // targets the RECALL span"), so it can never be the guard for an expected fix's own span.
        var nearMiss = (c.ForbiddenCorrections ?? Array.Empty<ProofreadCorrection>())
            .Where(f => !string.IsNullOrWhiteSpace(f.Suggested))
            .ToArray();

        var unguarded = expected
            .Where(e => !nearMiss.Any(f => SpansOverlapInInput(c.Input, e.Original, f.Original)))
            .Select(e => $"[{e.Original} -> {e.Suggested}]")
            .ToArray();

        Assert.True(unguarded.Length == 0,
            $"{c.Id}: {unguarded.Length} of {expected.Length} expected correction(s) have NO non-empty " +
            "forbiddenCorrections entry at their own span, so a model that finds the span and writes the " +
            "WRONG form there is credited as a recall HIT by the span-only CorrectionsMatch. Add a " +
            "near-miss forbidden naming a plausible wrong form for each of: " + string.Join(", ", unguarded));
    }

    [Fact]
    public void AgreementPreservationEntries_ExpectNoChanges_AndNameTheOverreach()
    {
        var preserve = Bucket(PreservePrefix);
        Assert.NotEmpty(preserve);

        Assert.All(preserve, c =>
        {
            Assert.Equal(true, c.ShouldHaveNoChanges);
            Assert.Null(c.ExpectedCorrections);
            Assert.True((c.ForbiddenCorrections?.Length ?? 0) > 0, $"{c.Id}: preservation entry names no overreach");
            // Upgraded from a plain Assert.Contains: containment also passes for a span that is an
            // arbitrary INFIX of some other word, which is not what "the forbidden span is in the input"
            // is ever meant to claim. Same word-level check the recall entries now carry.
            var strayForbiddens = NonWordAlignedForbiddens(c);
            Assert.True(strayForbiddens.Count == 0, string.Join("\n  ", strayForbiddens));
        });

        // The three preservation shapes the plan requires, keyed on the notes-free structural facts:
        // (a) correct agreement adjacent to a register name — an entry whose forbidden edit would
        //     de-gender an already-correct verb (empty suggested = must not touch that span at all);
        Assert.Contains(preserve, c => c.ForbiddenCorrections!.Any(f => string.IsNullOrEmpty(f.Suggested)));
        // (b) the analytic-object -> synthetic-suffixed register shift, generalized over 3 verbs:
        //     a forbidden whose ORIGINAL contains the analytic object pronoun and whose SUGGESTED does not.
        var registerShift = preserve
            .Where(c => c.ForbiddenCorrections!.Any(f =>
                (f.Original.Contains(" אותה", StringComparison.Ordinal) || f.Original.Contains(" אותו", StringComparison.Ordinal)) &&
                !string.IsNullOrEmpty(f.Suggested) &&
                !f.Suggested.Contains("אות", StringComparison.Ordinal)))
            .ToArray();
        Assert.True(registerShift.Length >= 3,
            $"expected >= 3 analytic-object register-shift guards, found {registerShift.Length}");
        // (c) at least two unnamed-referent cases whose register names NOBODY appearing in the input,
        //     so any agreement rewrite there is pure over-correction.
        var unnamed = preserve
            .Where(c => (c.CharacterRegister?.Length ?? 0) > 0 &&
                        c.CharacterRegister!.All(r => !c.Input.Contains(r.Name, StringComparison.Ordinal)))
            .ToArray();
        Assert.True(unnamed.Length >= 2,
            $"expected >= 2 unnamed-referent (register mentions no one in the text) cases, found {unnamed.Length}");
    }

    /// <summary>
    /// The axes the plan requires the class to span, asserted on structure rather than on prose notes.
    /// Symmetry matters more than volume here: a class with only "female subject, masculine verb"
    /// entries cannot distinguish a model that reads the register from a model that always feminizes.
    /// </summary>
    [Fact]
    public void AgreementRecall_SpansBothErrorDirections_AndBothAttributeSources()
    {
        var recall = Bucket(NamePrefix).Concat(Bucket(RegisterPrefix)).ToArray();

        // Attribute source: both subsets are populated, and g1 can slice them by prefix alone.
        Assert.True(Bucket(NamePrefix).Length >= 5);
        Assert.True(Bucket(RegisterPrefix).Length >= 5);

        // The symmetric error: a female subject taking a masculine form AND a male subject taking a
        // feminine one. Derived from the MORPHOLOGY of the expected fix, not from the notes: Hebrew
        // marks the feminine by a final ה/ת, so masculine -> feminine and feminine -> masculine repairs
        // are distinguishable structurally (pairs where both sides are feminine-marked, e.g. ענה->ענתה,
        // are simply not counted by either side).
        var toFeminine = recall.Where(c => c.ExpectedCorrections!.Any(e => !FeminineMarked(e.Original) && FeminineMarked(e.Suggested))).ToArray();
        var toMasculine = recall.Where(c => c.ExpectedCorrections!.Any(e => FeminineMarked(e.Original) && !FeminineMarked(e.Suggested))).ToArray();
        Assert.True(toFeminine.Length >= 3, $"female-subject (masculine form -> feminine) entries: {toFeminine.Length}");
        Assert.True(toMasculine.Length >= 3, $"male-subject (feminine form -> masculine) entries: {toMasculine.Length}");

        // Two-character sentences where only ONE referent is wrong: the register holds two characters,
        // both are named in the text, and a span is pinned must-not-touch.
        var twoCharacterSingleError = recall
            .Where(c => (c.CharacterRegister?.Length ?? 0) >= 2 &&
                        c.CharacterRegister!.All(r => c.Input.Contains(r.Name, StringComparison.Ordinal)) &&
                        (c.ForbiddenCorrections ?? Array.Empty<ProofreadCorrection>())
                            .Any(f => string.IsNullOrEmpty(f.Suggested)))
            .ToArray();
        Assert.True(twoCharacterSingleError.Length >= 2,
            $"expected >= 2 two-character/one-error entries, found {twoCharacterSingleError.Length}");

        // Dialogue-attribution position (the quote marks are the structural tell).
        Assert.Contains(recall, c => c.Input.Contains('״'));
    }

    /// <summary>
    /// True when the LAST token of a span carries a Hebrew feminine ending (ה / ת). A deliberately
    /// crude morphological test, used only to prove the class spans both error directions.
    /// </summary>
    private static bool FeminineMarked(string span)
    {
        var last = span.Trim().Split(' ').LastOrDefault() ?? string.Empty;
        return last.EndsWith("ה", StringComparison.Ordinal) || last.EndsWith("ת", StringComparison.Ordinal);
    }

    // ── 2/3. the composed prompt ──────────────────────────────────────────────────────────────────

    private sealed class CapturingProvider : IAiAnalysisProvider
    {
        public ResolvedAiRequest? Captured { get; private set; }

        public Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult(new AiResponse { Content = request.InputText, Provider = "Fake", Model = request.Selection.Model });
        }
    }

    private static async Task<ResolvedAiRequest> ComposeAsync(HebrewRegressionCase c)
    {
        var provider = new CapturingProvider();
        var router = new AiRouter(
            Options.Create(new AiOptions { DefaultProvider = "Fake", DefaultModel = "m" }),
            new PromptFactory(),
            new Dictionary<string, IAiAnalysisProvider> { ["Fake"] = provider });

        await router.CompleteAsync(ProofreadQualityTests.BuildGoldRequest(c));
        Assert.NotNull(provider.Captured);
        return provider.Captured!;
    }

    [Fact]
    public async Task RegisterCase_ComposedPrompt_CarriesTheProductionCharacterRegisterBlock()
    {
        var c = Case("agree-name-01");
        var resolved = await ComposeAsync(c);

        // The EXACT byte format PromptFactory.AppendSection + FormatCharacters emit: the marker lines
        // use "\n", while the line break BETWEEN character rows comes from StringBuilder.AppendLine,
        // i.e. Environment.NewLine. Pinned literally (not rebuilt from the same helpers) so a change to
        // the production format shows up here as a decision to make, rather than silently moving the
        // surface every agreement number was measured on.
        var expectedBlock =
            "[CHARACTER_REGISTER]\n" +
            "- סבסטיאן [male]" + Environment.NewLine +
            "- נעמי [female]\n" +
            "[/CHARACTER_REGISTER]\n\n";
        Assert.Contains(expectedBlock, resolved.Instruction);
        Assert.StartsWith(expectedBlock, resolved.Instruction);

        // The block must be followed by the body that TELLS the model what the block is for (ProofreadHe).
        // Without it the register is uninterpretable and a register-only miss would say nothing about
        // the model — the legacy short instruction never mentions CHARACTER_REGISTER at all.
        const string registerUsageSentence = "אם מופיע [CHARACTER_REGISTER] — השתמש בו לאימות התאמת מין";
        Assert.Contains(registerUsageSentence, resolved.Instruction);

        // ...and the router appends the SHORT pipeline instruction after both, which is the same
        // long+short concatenation every production Proofread call produces.
        var shortPipeline = new PromptFactory().GetPrompt(AiTaskType.Proofread, c.Language).Instruction;
        Assert.Contains(shortPipeline, resolved.Instruction);

        var blockAt = resolved.Instruction.IndexOf(expectedBlock, StringComparison.Ordinal);
        var bodyAt = resolved.Instruction.IndexOf(registerUsageSentence, StringComparison.Ordinal);
        var shortAt = resolved.Instruction.IndexOf(shortPipeline, StringComparison.Ordinal);
        Assert.True(blockAt < bodyAt && bodyAt < shortAt,
            $"expected order register-block ({blockAt}) < ProofreadHe body ({bodyAt}) < short pipeline ({shortAt})");

        Assert.Equal(c.Input, resolved.InputText);
    }

    [Fact]
    public async Task EveryRegisterCase_RendersEveryDeclaredCharacterIntoThePrompt()
    {
        foreach (var c in Bucket(AgreementPrefix))
        {
            var resolved = await ComposeAsync(c);
            Assert.Contains("[CHARACTER_REGISTER]", resolved.Instruction);
            foreach (var r in c.CharacterRegister!)
                Assert.Contains($"- {r.Name} [{r.Gender}]", resolved.Instruction);
        }
    }

    /// <summary>
    /// THE REGRESSION THIS FILE EXISTS FOR. Every gold case authored before the agreement class keeps
    /// the historical prompt EXACTLY: caller Instruction null, so the router sends the short pipeline
    /// instruction alone, with no register block and no ProofreadHe body. If this ever fails, every
    /// precision/recall/fp number previously recorded for this gold file has been silently invalidated.
    /// </summary>
    [Fact]
    public async Task NonRegisterCase_ComposedPrompt_IsByteForByteTheLegacyShortInstruction()
    {
        var c = Case("clean-ms-01");
        Assert.Null(c.CharacterRegister);
        Assert.Null(ProofreadQualityTests.BuildGoldRequest(c).Instruction);

        var resolved = await ComposeAsync(c);

        var shortPipeline = new PromptFactory().GetPrompt(AiTaskType.Proofread, c.Language).Instruction;
        Assert.Equal(shortPipeline, resolved.Instruction);
        Assert.DoesNotContain("CHARACTER_REGISTER", resolved.Instruction);
    }

    [Fact]
    public void NoPreExistingGoldCase_HasAcquiredARegister()
    {
        var moved = LoadGold()
            .Where(c => !c.Id.StartsWith(AgreementPrefix, StringComparison.Ordinal))
            .Where(c => (c.CharacterRegister?.Length ?? 0) > 0)
            .Select(c => c.Id)
            .ToArray();

        Assert.True(moved.Length == 0,
            "These pre-agreement gold cases now carry a characterRegister, which moves them off the " +
            "short-prompt surface every historical number for this file was measured on: " +
            string.Join(", ", moved));
    }

    // ── 4. the near-miss forbidden entries cannot fire on the correct fix ─────────────────────────

    /// <summary>
    /// Runs the SHIPPED ForbiddenMatch over each recall entry's (expected fix x forbidden edit) pairs.
    /// Both spans and both replacements are matched substring-tolerantly, and Hebrew agreement forms are
    /// built by suffixation (the masculine past IS a prefix of the feminine past), so it is easy to
    /// author a forbidden `suggested` that swallows the correct answer. If that happened, the scorer
    /// would pull the CORRECT correction out of the pool as an overreach BEFORE recall matching — the
    /// entry would score 0 recall + 1 overreach no matter how well the model performed, and nothing
    /// else would ever say so.
    /// </summary>
    [Fact]
    public void ForbiddenNearMissEntries_NeverMatchTheCorrectFix()
    {
        var recall = Bucket(NamePrefix).Concat(Bucket(RegisterPrefix)).ToArray();
        Assert.NotEmpty(recall);

        var collisions = new List<string>();
        foreach (var c in recall)
        {
            foreach (var e in c.ExpectedCorrections!)
            foreach (var f in c.ForbiddenCorrections ?? Array.Empty<ProofreadCorrection>())
            {
                if (ProofreadQualityTests.ForbiddenMatch(
                        ProofreadQualityTests.NormalizeForMatch(e.Original),
                        ProofreadQualityTests.NormalizeForMatch(e.Suggested),
                        ProofreadQualityTests.NormalizeForMatch(f.Original),
                        ProofreadQualityTests.NormalizeForMatch(f.Suggested)))
                {
                    collisions.Add($"{c.Id}: correct fix [{e.Original} -> {e.Suggested}] trips forbidden " +
                                   $"[{f.Original} -> {f.Suggested}]");
                }
            }
        }

        Assert.True(collisions.Count == 0,
            "A forbidden entry swallows the CORRECT fix, so the case can never score recall:\n  " +
            string.Join("\n  ", collisions));
    }

    /// <summary>
    /// NON-VACUITY for the test above. Proving the forbidden entries never fire is worthless if they can
    /// never fire at all: ForbiddenMatch returns false whenever the two ORIGIN spans do not align, so an
    /// entry whose forbidden span misses the erroneous span makes the sibling test pass for free.
    ///
    /// WHAT THIS ASSERTS. For every (expected fix, near-miss forbidden) pair that covers the SAME SPAN OF
    /// THE INPUT, it builds the correction a near-missing model actually emits (original = the EXPECTED
    /// fix's erroneous span, suggested = the forbidden, still wrong, form) and asserts the shipped
    /// ForbiddenMatch DOES trip on it, so that output lands in the overreach column instead of being
    /// credited as recall by the span-only CorrectionsMatch.
    ///
    /// WHY "SAME SPAN" IS MEASURED POSITIONALLY, IN THE INPUT. Pairing on the two originals being EQUAL
    /// (or one containing the other) would restate ForbiddenMatch's own origin test and hand back the
    /// tautology this test used to be: it would only ever select pairs the matcher already aligns. The
    /// pairing here is independent of the matcher: the two spans are located in the (normalized) input
    /// and paired when their character ranges OVERLAP, so a forbidden authored one word off, on a span
    /// that overlaps the erroneous one without containing it or being contained by it, is selected as a
    /// pair and then fails the assertion instead of silently disappearing from it.
    ///
    /// WHICH ENDPOINT IT ACTUALLY EXERCISES, said plainly because the version of this test that shipped
    /// before was a tautology. The simulated output's replacement IS the forbidden's own `suggested`
    /// (that is what "the model wrote the wrong form the gold names" MEANS), so ForbiddenMatch's
    /// replacement test is satisfied by construction and only its ORIGIN test can fail here. That is the
    /// half worth pinning: it is the half a mis-authored span breaks. It is also why this test cannot
    /// catch a forbidden `suggested` that is a placeholder rather than a plausible wrong form, which is
    /// <see cref="NearMissForbiddenSuggestions_MeetTheNecessaryConditionsForAPlausibleWrongForm"/>'s job.
    /// Note that with word-alignment separately enforced by
    /// <see cref="ForbiddenSpans_OccurAsAWordOfTheirOwnInput_AllowingOnlyAProcliticPrefix"/>, two
    /// overlapping SINGLE-word spans must end at the same word boundary and so must contain one another;
    /// the origin test therefore fails here only for a partially-overlapping MULTI-word span, which the
    /// class does contain.
    ///
    /// WHAT IT DOES NOT ASSERT. Coverage. An expected correction with NO same-span forbidden at all
    /// contributes no pair and is simply not exercised here; the floor below only pins that every recall
    /// CASE contributes at least one exercised pair. Coverage is
    /// <see cref="AssertEveryExpectedFixCarriesANearMissGuard"/>'s job, and it pairs on the SAME
    /// <see cref="SpansOverlapInInput"/> notion of "same span", so what it demands to exist is exactly
    /// what this test then proves fires. Between the two, every expected fix in the class is an exercised
    /// pair here, however many of them a single entry declares.
    /// </summary>
    [Fact]
    public void ForbiddenNearMissEntries_DoTripOnAWrongFormEditAtTheRightSpan()
    {
        var recall = Bucket(NamePrefix).Concat(Bucket(RegisterPrefix)).ToArray();
        Assert.NotEmpty(recall);

        var silent = new List<string>();
        var pairs = 0;
        var casesWithAPair = 0;

        foreach (var c in recall)
        {
            // Only the near-miss shape: a forbidden with a NON-EMPTY suggested names a specific wrong
            // form. An empty suggested is the other guard shape ("do not touch this span at all"), which
            // is authored on a DIFFERENT span from the erroneous one and trips on any edit by definition.
            var nearMiss = (c.ForbiddenCorrections ?? Array.Empty<ProofreadCorrection>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Suggested))
                .ToArray();

            var casePairs = 0;
            foreach (var e in c.ExpectedCorrections!)
            foreach (var f in nearMiss)
            {
                if (!SpansOverlapInInput(c.Input, e.Original, f.Original))
                    continue; // a guard on some other span, not this fix's near miss

                casePairs++;
                pairs++;

                // The correction a near-missing model emits: it found the erroneous span the gold names,
                // and wrote the plausible WRONG form the gold names instead of the right one.
                var tripped = ProofreadQualityTests.ForbiddenMatch(
                    ProofreadQualityTests.NormalizeForMatch(e.Original),
                    ProofreadQualityTests.NormalizeForMatch(f.Suggested),
                    ProofreadQualityTests.NormalizeForMatch(f.Original),
                    ProofreadQualityTests.NormalizeForMatch(f.Suggested));
                if (!tripped)
                    silent.Add($"{c.Id}: a model editing [{e.Original}] into the wrong form [{f.Suggested}] " +
                               $"is NOT caught by forbidden [{f.Original} -> {f.Suggested}], although that " +
                               "forbidden covers the same span of the input");
            }

            if (casePairs > 0)
                casesWithAPair++;
        }

        Assert.True(silent.Count == 0,
            "A right-span/wrong-form edit slips past the near-miss guard that was authored for that span, " +
            "so the span-only recall matcher would credit it as a HIT:\n  " + string.Join("\n  ", silent));

        // Non-vacuity of THIS test: if no pair were selected, everything above would pass by iterating
        // nothing. (Per-EXPECTED-CORRECTION coverage belongs to
        // AssertEveryExpectedFixCarriesANearMissGuard, not to this floor.)
        Assert.True(pairs > 0, "no (expected fix, same-span forbidden) pair was exercised");
        Assert.True(casesWithAPair == recall.Length,
            $"only {casesWithAPair} of {recall.Length} recall entries contribute a near-miss pair at an " +
            "expected fix's own span, so the rest of the class is not exercised by this test at all");
    }

    /// <summary>
    /// True when two spans occupy OVERLAPPING character ranges of the same input. Both the spans and the
    /// input go through the shipped NormalizeForMatch first, so vocalization and whitespace differences
    /// do not move the ranges. Deliberately positional: it is the one notion of "same span" that does not
    /// borrow ForbiddenMatch's own containment test (see the caller's remarks).
    /// </summary>
    private static bool SpansOverlapInInput(string input, string a, string b)
    {
        var normalizedInput = ProofreadQualityTests.NormalizeForMatch(input);
        var rangesA = OccurrenceRanges(normalizedInput, ProofreadQualityTests.NormalizeForMatch(a));
        var rangesB = OccurrenceRanges(normalizedInput, ProofreadQualityTests.NormalizeForMatch(b));
        return rangesA.Any(x => rangesB.Any(y => x.Start < y.End && y.Start < x.End));
    }

    /// <summary>Every [start, end) range at which <paramref name="needle"/> occurs in <paramref name="haystack"/>.</summary>
    private static List<(int Start, int End)> OccurrenceRanges(string haystack, string needle)
    {
        var ranges = new List<(int Start, int End)>();
        if (needle.Length == 0) return ranges;

        var from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0) break;
            ranges.Add((at, at + needle.Length));
            from = at + 1;
        }

        return ranges;
    }

    // ── 4b. the span + content invariants ForbiddenMatch's substring tolerance rests on ───────────
    //
    // ProofreadQualityTests.ForbiddenMatch aligns BOTH endpoints substring-tolerantly, and its remarks
    // block imposes a distinctiveness requirement on forbidden spans in exchange. That requirement was
    // documentation only: it carried a population count that was wrong when it was written and wronger
    // after the agreement class landed, and nothing checked it. These three tests make it mechanical,
    // over EVERY forbidden entry in the file rather than over the agree-* buckets alone, because the
    // matcher they protect is shared by every case.

    /// <summary>
    /// Hebrew PROCLITICS: the conjunction/definite/prepositional letters (ו ה ב כ ל מ ש) that Hebrew
    /// writes attached to the FRONT of the following word. They are clitics, not separate words, so a
    /// gold span naming a word (<c>עתון</c>, <c>התקדם</c>) legitimately appears in the input inside the
    /// clitic-carrying orthographic token (<c>בעתון</c>, <c>והתקדם</c>). Only the LEFT edge tolerates
    /// them; the right edge does not, because Hebrew builds the feminine by SUFFIXATION and a right-edge
    /// substring match is exactly the <c>קם</c>-inside-<c>קמה</c> trap the near-miss design exists for.
    /// </summary>
    private const string HebrewProclitics = "והבכלמש";

    /// <summary>
    /// Every case in the file that declares a forbidden edit, and the NON-VACUITY FLOOR the three
    /// invariant tests below share. Each of them asserts "no offender was found", which an EMPTY
    /// population satisfies for free, and an empty population is a shape that really happens:
    /// <c>LoadProofreadGold</c> returns an empty array rather than throwing when the JSON is missing
    /// from the output directory, which is exactly what a copy-to-output regression looks like. So the
    /// floor is asserted here once, at the source, rather than trusted at three call sites.
    /// </summary>
    private static HebrewRegressionCase[] CasesDeclaringForbiddens()
    {
        var cases = LoadGold().Where(c => (c.ForbiddenCorrections?.Length ?? 0) > 0).ToArray();
        Assert.True(cases.Length > 0,
            "No gold case declares a forbiddenCorrections entry, so every forbidden-span invariant below " +
            "would pass by iterating nothing. Either the gold failed to load (LoadProofreadGold returns an " +
            "empty array for a missing TestData file) or the forbidden entries were removed.");
        return cases;
    }

    /// <summary>
    /// Clauses (1) and (2) of the invariant, and P2-5: RECALL entries never asserted that their forbidden
    /// originals exist in the input at all, while preservation entries asserted it with a plain
    /// <c>Assert.Contains</c>. Both are covered here, at WORD level, because plain containment is not
    /// the claim anyone means.
    ///
    ///  (1) PRESENT. A forbidden span absent from its own input is worse than wrong, it is inert:
    ///      nothing the model can produce will ever trip it, so the entry silently guards nothing.
    ///  (2) A WORD, AT EVERY OCCURRENCE, not merely at one. This is the distinctiveness invariant's
    ///      first direction, and "every" is what makes it that: an occurrence sitting INSIDE a longer
    ///      word means some other word of the input contains the forbidden span, so a legitimate
    ///      correction of THAT word aligns ForbiddenMatch's origin test and is pulled out as overreach
    ///      before recall matching. (Stating it as "some occurrence is a word" instead would be
    ///      unfalsifiable in the direction that matters: a word containing the span always contains an
    ///      occurrence OF the span, so a position-disjointness test can never see it.)
    ///
    /// A Hebrew proclitic prefix is allowed at the left edge and nothing is allowed at the right edge:
    /// see <see cref="HebrewProclitics"/>.
    /// </summary>
    [Fact]
    public void ForbiddenSpans_OccurAsAWordOfTheirOwnInput_AllowingOnlyAProcliticPrefix()
    {
        var offenders = new List<string>();
        var spansChecked = 0;
        foreach (var c in CasesDeclaringForbiddens())
        {
            spansChecked += c.ForbiddenCorrections!.Length;
            offenders.AddRange(NonWordAlignedForbiddens(c));
        }

        Assert.True(offenders.Count == 0,
            "A forbiddenCorrections entry does not name a WORD of its own case's input, so it either " +
            "measures nothing (absent) or aligns on a fragment of some other word (infix):\n  " +
            string.Join("\n  ", offenders));
        Assert.True(spansChecked > 0, "no forbidden span was checked");
    }

    /// <summary>Per-case worker, so a caller that already iterates one bucket can report locally.</summary>
    private static List<string> NonWordAlignedForbiddens(HebrewRegressionCase c)
    {
        var offenders = new List<string>();
        var input = ProofreadQualityTests.NormalizeForMatch(c.Input);

        foreach (var f in c.ForbiddenCorrections ?? Array.Empty<ProofreadCorrection>())
        {
            var span = ProofreadQualityTests.NormalizeForMatch(f.Original);
            if (span.Length == 0)
            {
                offenders.Add($"{c.Id}: a forbiddenCorrections entry has an EMPTY original");
                continue;
            }

            var occurrences = OccurrenceRanges(input, span);
            if (occurrences.Count == 0)
            {
                offenders.Add($"{c.Id}: forbidden original [{f.Original}] does not occur in this case's " +
                              "input at all, so no model output can ever trip it");
                continue;
            }

            foreach (var r in occurrences.Where(r => !IsWordAligned(input, r)))
            {
                offenders.Add($"{c.Id}: forbidden original [{f.Original}] occurs at index {r.Start} of the " +
                              $"normalized input as an infix of the longer word '{EnclosingWord(input, r)}' " +
                              "(it does not both start a word, allowing a Hebrew proclitic prefix, and end " +
                              "one). A legitimate correction of that word would align ForbiddenMatch's " +
                              "origin test and be scored as overreach");
            }
        }

        return offenders;
    }

    /// <summary>The maximal letter/digit run around <paramref name="range"/>, for the failure message.</summary>
    private static string EnclosingWord(string input, (int Start, int End) range)
    {
        var start = range.Start;
        while (start > 0 && char.IsLetterOrDigit(input[start - 1])) start--;
        var end = range.End;
        while (end < input.Length && char.IsLetterOrDigit(input[end])) end++;
        return input.Substring(start, end - start);
    }

    /// <summary>
    /// Clause (3) of the invariant, the direction the word-alignment test above cannot reach.
    ///
    /// The two containment directions are NOT symmetric. A forbidden span sitting inside another word is
    /// always a defect and is caught above. The reverse (a forbidden span CONTAINING a shorter word that
    /// also occurs elsewhere) is unavoidable for a MULTI-WORD span in Hebrew: agree-preserve-04's
    /// <c>מצאתי אותה</c> contains <c>את</c>, which that same input also uses as a standalone object
    /// marker, and no choice of span removes that. It is normally bounded by the OTHER endpoint instead,
    /// since the produced replacement must also align with the forbidden `suggested`.
    ///
    /// Except when there is no other endpoint. An EMPTY forbidden `suggested` means "do not touch this
    /// span at all" and trips on ANY produced replacement at an aligning span, so for those entries the
    /// reverse direction is unbounded and IS required.
    ///
    /// Positions come from <see cref="OccurrenceRanges"/> on the normalized input, the same positional
    /// notion of "same span" the near-miss tests pair on, so a word that merely hugs punctuation
    /// (רגשית. around רגשית) is correctly the SAME span rather than a collision.
    /// </summary>
    [Fact]
    public void ForbiddenSpansThatForbidAnyEdit_DoNotContainAWordUsedElsewhereInTheInput()
    {
        var collisions = new List<string>();
        var spansChecked = 0;

        foreach (var c in CasesDeclaringForbiddens())
        {
            var input = ProofreadQualityTests.NormalizeForMatch(c.Input);
            var words = WordTokens(input);

            foreach (var f in c.ForbiddenCorrections!)
            {
                if (!string.IsNullOrWhiteSpace(f.Suggested)) continue; // the suggested endpoint bounds it
                var span = ProofreadQualityTests.NormalizeForMatch(f.Original);
                if (span.Length == 0) continue; // reported by the word-alignment test
                spansChecked++;
                var spanRanges = OccurrenceRanges(input, span);

                foreach (var w in words)
                {
                    if (w.Text == span) continue;
                    if (spanRanges.Any(r => w.Start < r.End && r.Start < w.End)) continue; // same span
                    if (!span.Contains(w.Text, StringComparison.Ordinal)) continue;

                    collisions.Add($"{c.Id}: forbidden span [{f.Original}] has an EMPTY suggested (it forbids " +
                                   $"ANY edit at its span) and contains the word '{w.Text}', which the input " +
                                   $"also uses at index {w.Start}. With no suggested endpoint to lock on, any " +
                                   "edit to that other word trips this entry and is scored as overreach.");
                }
            }
        }

        Assert.True(collisions.Count == 0,
            "SPAN DISTINCTIVENESS INVARIANT VIOLATED (see the remarks on ProofreadQualityTests." +
            "ForbiddenMatch). ForbiddenMatch aligns spans substring-tolerantly, so these entries can " +
            "fire on an edit made somewhere else in the same input:\n  " + string.Join("\n  ", collisions));
        // The pre-filter above skips every non-empty `suggested`, so "no collisions" is only meaningful
        // while the file still has empty-suggested forbiddens for it to look at.
        Assert.True(spansChecked > 0,
            "no forbidden entry with an EMPTY suggested was checked, so this invariant measured nothing");
    }

    /// <summary>
    /// THE NEAR-MISS `suggested` MUST NAME A WRONG FORM, not just be non-empty. Measured while the
    /// near-miss tests above were being rewritten: replacing a real wrong form with the Latin
    /// placeholder "zzz" passes every other guard in this class while measuring nothing (the sibling
    /// trip test feeds that same placeholder in as the model's output, so its replacement test aligns by
    /// construction). The whole near-miss design rests on that string naming a form the model might
    /// PLAUSIBLY emit, and plausibility is not mechanically decidable, so this asserts the cheap
    /// NECESSARY conditions instead. They are NECESSARY AND NOT
    /// SUFFICIENT: passing here does not make a forbidden a good near-miss, it only rules out the
    /// authoring accidents that would make it a vacuous one.
    ///
    ///  - differs from its own `original` (otherwise it forbids a NO-OP and can only fire on a model
    ///    that "corrects" a word to itself; this is the shape agree-register-03's קם span walked into,
    ///    since the present-tense masculine of קם is קם);
    ///  - differs from the EXPECTED fix at the same span (otherwise the guard forbids the correct
    ///    answer; ForbiddenNearMissEntries_NeverMatchTheCorrectFix proves the same thing through the
    ///    matcher and substring-tolerantly, this states it directly and readably);
    ///  - is written in the script of the case's language (what actually catches a placeholder).
    /// </summary>
    [Fact]
    public void NearMissForbiddenSuggestions_MeetTheNecessaryConditionsForAPlausibleWrongForm()
    {
        var defects = new List<string>();
        var nearMissChecked = 0;

        foreach (var c in CasesDeclaringForbiddens())
        {
            var hebrew = c.Language?.StartsWith("he", StringComparison.OrdinalIgnoreCase) ?? false;

            foreach (var f in c.ForbiddenCorrections!)
            {
                // An EMPTY suggested is the other guard shape ("do not touch this span at all"), which
                // deliberately names no form; only the near-miss shape is in scope here.
                if (string.IsNullOrWhiteSpace(f.Suggested)) continue;

                nearMissChecked++;
                var suggested = ProofreadQualityTests.NormalizeForMatch(f.Suggested);
                var original = ProofreadQualityTests.NormalizeForMatch(f.Original);

                if (suggested == original)
                    defects.Add($"{c.Id}: forbidden [{f.Original} -> {f.Suggested}] forbids a NO-OP (its " +
                                "suggested equals its original), so it can never name a wrong form");

                if (hebrew && !suggested.Any(IsHebrewLetter))
                    defects.Add($"{c.Id}: forbidden suggested [{f.Suggested}] contains no Hebrew letter on " +
                                $"a '{c.Language}' case, so it cannot be a form the model would emit");

                if (hebrew && suggested.Any(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                    defects.Add($"{c.Id}: forbidden suggested [{f.Suggested}] mixes Latin letters into a " +
                                $"'{c.Language}' case, which is the shape of a placeholder, not of a wrong form");

                foreach (var e in c.ExpectedCorrections ?? Array.Empty<ProofreadCorrection>())
                {
                    if (!SpansOverlapInInput(c.Input, e.Original, f.Original)) continue;
                    if (ProofreadQualityTests.NormalizeForMatch(e.Suggested) == suggested)
                        defects.Add($"{c.Id}: forbidden [{f.Original} -> {f.Suggested}] names the CORRECT " +
                                    $"fix at that span ([{e.Original} -> {e.Suggested}]), so it would score " +
                                    "the right answer as overreach");
                }
            }
        }

        Assert.True(defects.Count == 0,
            "A near-miss forbidden `suggested` fails a NECESSARY (not sufficient) condition for naming a " +
            "plausible wrong form. These checks cannot tell whether the form is one the model would really " +
            "produce; they only rule out the authoring accidents that make the guard vacuous:\n  " +
            string.Join("\n  ", defects));
        // Same floor as the sibling invariants: the pre-filter above skips every empty `suggested`, so
        // "no defects" says nothing unless at least one near-miss entry was actually inspected.
        Assert.True(nearMissChecked > 0, "no near-miss forbidden `suggested` was checked");
    }

    private static bool IsHebrewLetter(char ch) => ch is >= 'א' and <= 'ת';

    /// <summary>
    /// True when <paramref name="range"/> covers a whole word of <paramref name="input"/>: nothing may
    /// follow it inside the word, and nothing may precede it except <see cref="HebrewProclitics"/>
    /// (up to two, e.g. וב / כש), themselves at a word start.
    /// </summary>
    private static bool IsWordAligned(string input, (int Start, int End) range)
    {
        if (range.End < input.Length && char.IsLetterOrDigit(input[range.End]))
            return false;
        if (range.Start == 0 || !char.IsLetterOrDigit(input[range.Start - 1]))
            return true;

        for (var len = 1; len <= 2 && range.Start - len >= 0; len++)
        {
            var prefix = input.Substring(range.Start - len, len);
            if (!prefix.All(ch => HebrewProclitics.Contains(ch, StringComparison.Ordinal)))
                break;
            var before = range.Start - len;
            if (before == 0 || !char.IsLetterOrDigit(input[before - 1]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every maximal run of letter/digit characters, with its range. Punctuation (including the Hebrew
    /// gershayim ״ and geresh ׳) is a boundary, so a quoted word yields the word without its quote and a
    /// sentence-final word without its full stop.
    /// </summary>
    private static List<(string Text, int Start, int End)> WordTokens(string text)
    {
        var words = new List<(string Text, int Start, int End)>();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            words.Add((text.Substring(start, i - start), start, i));
        }

        return words;
    }

    // ── 5. the prompt-surface partition the two reports are scoped by ─────────────────────────────
    //
    // The gold file holds cases on TWO prompt surfaces, whose numbers are not comparable, so both
    // consumers (the single-model Fact and the bake-off table) report per surface and rank the Winner
    // hint on the short-only subset. Those consumers are live-GPU Facts in the filter-EXCLUDED
    // LanguageEngine namespace, so what they PRINT cannot be exercised here. What CAN be exercised, and
    // is what the reports' correctness actually rests on, is the partition + per-subset aggregation,
    // which live in the pure helper GoldPromptSurfaces. These tests cover it.

    private const string EnglishGoldFile = "proofread-gold-en.json";

    /// <summary>
    /// The population on each side of the split today. A change here is not necessarily a bug, but it
    /// IS a change to the corpus every reported proofread number is scoped by, so it must be a decision.
    /// </summary>
    [Fact]
    public void PromptSurfaceSplit_OfTheHebrewGold_Partitions93ShortOnly_And23Production()
    {
        var cases = LoadGold();
        var shortOnly = GoldPromptSurfaces.OnSurface(cases, GoldPromptSurface.ShortPipelineOnly);
        var production = GoldPromptSurfaces.OnSurface(cases, GoldPromptSurface.ProductionLongPlusShort);

        Assert.Equal(116, cases.Length);
        Assert.Equal(93, shortOnly.Length);
        Assert.Equal(23, production.Length);
        // A partition, not two filters: every case lands on exactly one side. NO gold row rides the
        // THIRD (chunked) surface and none can - SurfaceOf derives only the two single-shot surfaces
        // from a HebrewRegressionCase, and a chunked case is a multi-chunk chapter rather than a gold
        // row. The chunked corpus is ChunkedAgreementFixtures; see ProofreadStandingFloorTests.
        Assert.Empty(GoldPromptSurfaces.OnSurface(cases, GoldPromptSurface.ChunkedPerChunk));
        Assert.Equal(cases.Length, shortOnly.Length + production.Length);

        // The production side is exactly the register-carrying population, which today is the agreement
        // class. Asserted through the register (the thing the surface is DERIVED from), plus the
        // today-true observation that the two coincide, so that a future register-carrying case with a
        // different id prefix fails this second line loudly rather than drifting silently.
        Assert.All(production, c => Assert.True((c.CharacterRegister?.Length ?? 0) > 0));
        Assert.All(shortOnly, c => Assert.True((c.CharacterRegister?.Length ?? 0) == 0));
        Assert.Equal(Bucket(AgreementPrefix).Length, production.Length);
    }

    /// <summary>
    /// THE ANTI-DRIFT ASSERTION. The partition exists to scope reported numbers by the prompt each case
    /// was actually sent on, so it is only correct while it agrees with the REQUEST BUILDER. This calls
    /// BOTH real functions for every case and asserts they agree case for case; it deliberately does not
    /// restate either one's condition, because a restatement would keep passing after the builder changed.
    ///
    /// IT RUNS OVER THE GOLD FILES *AND* A SYNTHETIC DERIVATION PROBE, and the probe is what gives the
    /// assertion teeth. In today's data the register-carrying cases happen to be exactly the `agree-*`
    /// ids, so a partition wrongly written as an id-prefix match would agree with the builder on every
    /// real case and this test would pass while being wrong. The probe separates the two axes: a
    /// register-carrying case whose id is NOT `agree-*` (which must land on the production surface) and
    /// an `agree-*`-named case with no register (which must land on the short-only surface). Any
    /// id-shaped, positional or fixture-keyed re-implementation of the partition fails on those two.
    /// </summary>
    [Fact]
    public void PromptSurfacePredicate_AgreesWithBuildGoldRequest_ForEveryGoldCase()
    {
        var disagreements = new List<string>();
        var checkedCases = 0;

        var corpora = new (string Source, HebrewRegressionCase[] Cases)[]
        {
            ("proofread-gold.json", ProofreadQualityTests.LoadProofreadGold()),
            (EnglishGoldFile, ProofreadQualityTests.LoadProofreadGold(EnglishGoldFile)),
            ("synthetic derivation probe", DerivationProbeCases())
        };

        foreach (var (source, cases) in corpora)
        {
            Assert.NotEmpty(cases);
            foreach (var c in cases)
            {
                checkedCases++;
                var surface = GoldPromptSurfaces.SurfaceOf(c);
                var instruction = ProofreadQualityTests.BuildGoldRequest(c).Instruction;

                var partitionSaysProduction = surface == GoldPromptSurface.ProductionLongPlusShort;
                var builderSendsProduction = instruction is not null;
                if (partitionSaysProduction != builderSendsProduction)
                {
                    disagreements.Add(
                        $"{source}/{c.Id}: GoldPromptSurfaces.SurfaceOf says {surface}, but " +
                        $"BuildGoldRequest.Instruction is {(builderSendsProduction ? "NON-NULL (production long+short)" : "NULL (short pipeline alone)")}");
                }
            }
        }

        Assert.True(checkedCases > 0, "no gold cases were checked");
        Assert.True(disagreements.Count == 0,
            "The prompt-surface partition predicate has DRIFTED from ProofreadQualityTests.BuildGoldRequest, " +
            "so the per-surface aggregates would be scoped by a corpus that is not the one the model was " +
            "actually prompted with. Predicate disagreements:\n  " + string.Join("\n  ", disagreements));
    }

    /// <summary>
    /// Two cases the real gold cannot currently supply, because register-carrying and `agree-*`-named
    /// coincide there: they hold the id axis and the register axis apart so the drift check measures the
    /// DERIVATION rather than today's population. Both are fed to the real BuildGoldRequest.
    /// </summary>
    private static HebrewRegressionCase[] DerivationProbeCases() => new[]
    {
        new HebrewRegressionCase
        {
            // A future register-carrying case outside the agree-* prefix: production surface.
            Id = "probe-registered-case-outside-the-agree-prefix",
            Input = "טקסט בדיקה.",
            Language = "he-IL",
            CharacterRegister = new[]
            {
                new Pagedraft.Api.Models.CharacterRegisterEntry { Name = "נעמי", Gender = "female" }
            }
        },
        new HebrewRegressionCase
        {
            // An agree-*-named case with no register: short-pipeline-only surface.
            Id = "agree-probe-without-a-register",
            Input = "טקסט בדיקה.",
            Language = "he-IL",
            CharacterRegister = null
        }
    };

    /// <summary>
    /// The per-subset aggregation partitions ONE scored pass: each of the THREE surfaces sums only its
    /// own cases, and the mixed ALL block sums everything. This is what makes a second (GPU-costing)
    /// scoring pass per subset unnecessary.
    ///
    /// Widened by c3 from two buckets to three. The third record is what the two-bucket version would
    /// have dropped: it would have appeared ONLY in <c>All</c>, so the mixed block would have grown
    /// while neither per-surface block moved.
    /// </summary>
    [Fact]
    public void PerSurfaceAggregation_SumsEachSurfaceSeparately_AndAllIsTheWholeSet()
    {
        var records = new[]
        {
            Record("short-a", GoldPromptSurface.ShortPipelineOnly, expected: 2, produced: 3, matched: 1),
            Record("short-b", GoldPromptSurface.ShortPipelineOnly, expected: 4, produced: 4, matched: 3),
            Record("prod-a", GoldPromptSurface.ProductionLongPlusShort, expected: 10, produced: 5, matched: 5),
            Record("chunked-a", GoldPromptSurface.ChunkedPerChunk, expected: 8, produced: 2, matched: 2)
        };

        var split = GoldPromptSurfaces.Split(records);

        Assert.Equal(2, split.ShortOnlyCases);
        Assert.Equal(1, split.ProductionCases);
        Assert.Equal(1, split.ChunkedCases);
        Assert.Equal(4, split.AllCases);
        Assert.Equal(3, split.PopulatedSurfaces);
        Assert.False(split.IsSingleSurface);

        // A PARTITION: the three per-surface case counts sum to the whole, so no record can be counted
        // twice and none can fall out of every bucket.
        Assert.Equal(split.AllCases, split.ShortOnlyCases + split.ProductionCases + split.ChunkedCases);

        Assert.Equal(6, split.ShortOnly.TotalExpected);
        Assert.Equal(4, split.ShortOnly.TotalMatched);
        Assert.Equal(10, split.Production.TotalExpected);
        Assert.Equal(5, split.Production.TotalMatched);
        Assert.Equal(8, split.Chunked.TotalExpected);
        Assert.Equal(2, split.Chunked.TotalMatched);
        Assert.Equal(24, split.All.TotalExpected);
        Assert.Equal(11, split.All.TotalMatched);

        // The blended figure really is a blend: none of the three subsets' recall.
        Assert.Equal(4.0 / 6, split.ShortOnly.Recall, 6);
        Assert.Equal(0.5, split.Production.Recall, 6);
        Assert.Equal(0.25, split.Chunked.Recall, 6);
        Assert.Equal(11.0 / 24, split.All.Recall, 6);

        // ...and the indexed accessor agrees with the named properties, so a report that LOOPS over
        // GoldPromptSurfaces.AllSurfaces reads the same numbers as one that branches.
        foreach (var (surface, expectedCases) in new[]
                 {
                     (GoldPromptSurface.ShortPipelineOnly, split.ShortOnlyCases),
                     (GoldPromptSurface.ProductionLongPlusShort, split.ProductionCases),
                     (GoldPromptSurface.ChunkedPerChunk, split.ChunkedCases)
                 })
        {
            Assert.Equal(expectedCases, split.On(surface).Cases);
        }
    }

    /// <summary>
    /// THE SILENT-MIXING HAZARD, made unreachable rather than merely tested. c1 declined to fold its
    /// chunked corpus into the then-two-bucket split for exactly this reason: a record whose surface
    /// matches no bucket is still summed into <c>All</c>, so the mixed block grows while every
    /// per-surface block stays put - a corpus change wearing the costume of a model change. Since c3
    /// widened the enum, <c>Split</c> THROWS on such a record instead of dropping it.
    ///
    /// Probed with an out-of-range enum value, which is the only way to build one: every declared value
    /// has a bucket, so this fails the day a fourth surface is added to <c>GoldPromptSurface</c> without
    /// being added to <c>Split</c>.
    ///
    /// TWO distinct scenarios can produce the "no bucket claims this record" throw, and this test
    /// covers BOTH, because a review of the offender-list derivation (<c>GoldPromptSurfaces.Split</c>)
    /// found it only correct for one of them:
    ///   1. An OUT-OF-RANGE cast value (<c>(GoldPromptSurface)999</c>, below) - not a member of the
    ///      enum's declared values at all. The probe just below this comment covers it.
    ///   2. A value that IS a declared enum member but has no bucket in <c>Split</c> - the scenario the
    ///      method's own docstring warns about ("fails the day a fourth surface is added ... without
    ///      being added to Split"). <c>GoldPromptSurface</c> currently has exactly three members, all
    ///      three bucketed, so the state cannot be reached by adding a real fourth member without
    ///      widening every switch that pattern-matches the enum across this corpus (<c>SurfaceOf</c>,
    ///      <c>On</c>, <c>Describe</c>, the loop at the bottom of this test) - out of scope here.
    ///      Instead the OFFENDER DERIVATION is exercised directly through
    ///      <c>GoldPromptSurfaces.OrphanLabels</c>, which takes its bucket set as a parameter: the probe
    ///      passes a set that deliberately omits <c>ChunkedPerChunk</c>, a REAL declared member, which is
    ///      precisely "declared but unbucketed". The buggy derivation asks "is this surface in
    ///      <c>AllSurfaces</c>?" (yes - so the offender is wrongly dropped and the throw reports an empty
    ///      list); the fixed one asks "is it in the bucket set?" (no - so it keeps naming the offender).
    ///      An earlier version of this test instead swapped a setter on <c>AllSurfaces</c> and restored it
    ///      in a <c>finally</c>. That was removed as a RACE, not as a style preference: xUnit runs test
    ///      classes in parallel and two <c>ProofreadStandingFloorTests</c> cases read <c>AllSurfaces</c>;
    ///      widening the window made both fail. Scenario 1 and the closing loop still drive the real
    ///      <c>Split</c>, so the derivation is not tested in isolation from its caller.
    /// </summary>
    [Fact]
    public void ARecordOnNoKnownSurface_ThrowsFromTheSplit_RatherThanLandingOnlyInTheMixedBlock()
    {
        var unknown = (GoldPromptSurface)999;
        Assert.DoesNotContain(unknown, GoldPromptSurfaces.AllSurfaces);

        // Scenario 1: an OUT-OF-RANGE cast value. Both the buggy and the fixed offender derivation
        // handle this correctly (it is in neither AllSurfaces nor BucketedSurfaces), which is exactly
        // why this probe alone did not catch the bug - see scenario 2 below for the one that would.
        var ex = Assert.Throws<InvalidOperationException>(() => GoldPromptSurfaces.Split(new[]
        {
            Record("short-a", GoldPromptSurface.ShortPipelineOnly, expected: 1, produced: 1, matched: 1),
            Record("orphan", unknown, expected: 1, produced: 1, matched: 1)
        }));
        Assert.Contains("orphan", ex.Message, StringComparison.Ordinal);

        // The offender list itself must be non-empty and name the record - not just "some throw
        // happened". This is the shape of assertion that catches an offender-derivation regression;
        // scenario 2 is what actually exercises it, because scenario 1 passes under both the buggy and
        // the fixed code (see comment above).
        var offendersLabel = "Offenders: ";
        var offendersIndex = ex.Message.IndexOf(offendersLabel, StringComparison.Ordinal);
        Assert.True(offendersIndex >= 0, "Expected the exception message to contain an 'Offenders: ' section.");
        var offendersPortion = ex.Message[(offendersIndex + offendersLabel.Length)..];
        Assert.NotEmpty(offendersPortion);
        Assert.Contains("orphan", offendersPortion, StringComparison.Ordinal);

        // NON-VACUITY for the throw: the SAME call shape with a known surface does not throw, so what
        // the assertion above caught is the orphan and not the call itself.
        var ok = GoldPromptSurfaces.Split(new[]
        {
            Record("short-a", GoldPromptSurface.ShortPipelineOnly, expected: 1, produced: 1, matched: 1),
            Record("chunked-a", GoldPromptSurface.ChunkedPerChunk, expected: 1, produced: 1, matched: 1)
        });
        Assert.Equal(2, ok.AllCases);

        // Scenario 2: a surface that IS a declared enum member but has NO bucket. This is the case the
        // buggy `!AllSurfaces.Contains(r.Surface)` filter got wrong - a declared member passes that
        // filter, so the offender was dropped from the list even though the record landed in no bucket
        // and the throw still fired, leaving "Offenders: " empty in exactly the scenario Split's own
        // docstring names.
        //
        // EXERCISED ON THE DERIVATION, NOT BY MUTATING A GLOBAL. The obvious probe - widen
        // GoldPromptSurfaces.AllSurfaces for the duration of one Split call and restore it in a finally
        // - is a genuine flake: xUnit runs test CLASSES in parallel, and ProofreadStandingFloorTests and
        // ChunkedAgreementFixtureTests both READ AllSurfaces. MEASURED: holding the widened window open
        // made TheStandingCorpus_SpansAllThreeSurfaces_* throw from Describe(999) and
        // EveryMetricFloor_StatesItsSurfaceAndSubset_* fail on "NO standing metric bar sits on 999". A
        // finally does not close that window, so AllSurfaces is immutable again and Split's offender
        // derivation is parameterized by its bucket set instead. That also makes the probe STRONGER: it
        // uses a REAL declared member rather than a cast sentinel, which is the actual shape of "a
        // fourth surface was added to the enum but not to Split".
        var narrowedBuckets = new HashSet<GoldPromptSurface>
        {
            GoldPromptSurface.ShortPipelineOnly,
            GoldPromptSurface.ProductionLongPlusShort
        };
        Assert.Contains(GoldPromptSurface.ChunkedPerChunk, GoldPromptSurfaces.AllSurfaces);
        Assert.DoesNotContain(GoldPromptSurface.ChunkedPerChunk, narrowedBuckets);

        var declaredButUnbucketedRecords = new[]
        {
            Record("short-a", GoldPromptSurface.ShortPipelineOnly, expected: 1, produced: 1, matched: 1),
            Record("orphan-declared", GoldPromptSurface.ChunkedPerChunk, expected: 1, produced: 1, matched: 1)
        };

        // THE ASSERTION THAT CATCHES THE REGRESSION: under the buggy AllSurfaces-based derivation this
        // comes back EMPTY, because ChunkedPerChunk IS declared.
        var declaredOffenders = GoldPromptSurfaces.OrphanLabels(declaredButUnbucketedRecords, narrowedBuckets);
        Assert.NotEmpty(declaredOffenders);
        Assert.Contains(declaredOffenders, o => o.StartsWith("orphan-declared", StringComparison.Ordinal));
        Assert.DoesNotContain(declaredOffenders, o => o.StartsWith("short-a", StringComparison.Ordinal));

        // NON-VACUITY for scenario 2: the SAME records against the FULL bucket set yield no offender at
        // all, so what was named above is the unbucketed surface and not every record indiscriminately.
        Assert.Empty(GoldPromptSurfaces.OrphanLabels(
            declaredButUnbucketedRecords, GoldPromptSurfaces.AllSurfaces.ToHashSet()));

        // Every DECLARED surface really is bucketed by the REAL Split (this is what breaks when a fourth
        // enum member is added without a bucket, and it is what ties scenario 2's derivation-level probe
        // back to the method under test).
        foreach (var surface in GoldPromptSurfaces.AllSurfaces)
        {
            var single = GoldPromptSurfaces.Split(new[]
            {
                Record("probe", surface, expected: 1, produced: 1, matched: 1)
            });
            Assert.Equal(1, single.AllCases);
            Assert.Equal(1, single.PopulatedSurfaces);
            Assert.Equal(1, single.On(surface).Cases);
        }
    }

    /// <summary>
    /// Degenerate path 1: a run whose scored set is EMPTY (every model errored, or an id subset selected
    /// nothing). Must aggregate to all-zero with no divide-by-zero, so the report can skip the block
    /// instead of printing NaN.
    /// </summary>
    [Fact]
    public void PerSurfaceAggregation_OfAnEmptyRun_IsAllZero_AndNeverNaN()
    {
        var split = GoldPromptSurfaces.Split(Array.Empty<GoldCaseScore>());

        Assert.Equal(0, split.AllCases);
        Assert.Equal(0, split.ShortOnlyCases);
        Assert.Equal(0, split.ProductionCases);
        Assert.Equal(0, split.ChunkedCases);
        Assert.Equal(0, split.PopulatedSurfaces);
        Assert.True(split.IsSingleSurface);

        foreach (var score in new[] { split.ShortOnly, split.Production, split.Chunked, split.All })
        {
            AssertRatesAreFinite(score);
            Assert.Equal(0, score.TotalExpected);
            Assert.Equal("n/a", score.PrecisionDisplay("P1"));
        }
    }

    /// <summary>
    /// Degenerate path 2: a single-surface run. Either half of the gold can be selected alone by
    /// PROOFREAD_BAKEOFF_CASE_IDS, so BOTH one-sided shapes must be honest, not just the register-less
    /// one the English gold produces.
    /// </summary>
    [Fact]
    public void PerSurfaceAggregation_OfASingleSurfaceRun_LeavesTheOtherSubsetEmptyAndFinite()
    {
        var registerOnly = Bucket(AgreementPrefix).Select(ScoredRecordFor).ToArray();
        var registerLessOnly = LoadGold()
            .Where(c => GoldPromptSurfaces.SurfaceOf(c) == GoldPromptSurface.ShortPipelineOnly)
            .Select(ScoredRecordFor)
            .ToArray();

        // Non-vacuity floor: an EMPTY subset satisfies every "the other side is 0" assertion below for
        // free, and both subsets are read from the gold file, which loads to an empty array rather than
        // throwing when it is missing from the output directory.
        Assert.NotEmpty(registerOnly);
        Assert.NotEmpty(registerLessOnly);

        var productionSplit = GoldPromptSurfaces.Split(registerOnly);
        Assert.Equal(0, productionSplit.ShortOnlyCases);
        Assert.Equal(0, productionSplit.ChunkedCases);
        Assert.Equal(registerOnly.Length, productionSplit.ProductionCases);
        Assert.True(productionSplit.IsSingleSurface);
        AssertRatesAreFinite(productionSplit.ShortOnly);
        AssertRatesAreFinite(productionSplit.Production);
        AssertRatesAreFinite(productionSplit.Chunked);
        AssertRatesAreFinite(productionSplit.All);

        var shortSplit = GoldPromptSurfaces.Split(registerLessOnly);
        Assert.Equal(0, shortSplit.ProductionCases);
        Assert.Equal(0, shortSplit.ChunkedCases);
        Assert.Equal(registerLessOnly.Length, shortSplit.ShortOnlyCases);
        Assert.True(shortSplit.IsSingleSurface);
        AssertRatesAreFinite(shortSplit.ShortOnly);
        AssertRatesAreFinite(shortSplit.Production);
        AssertRatesAreFinite(shortSplit.Chunked);
        AssertRatesAreFinite(shortSplit.All);
    }

    /// <summary>
    /// Degenerate path 3: PROOFREAD_BAKEOFF_GOLD=proofread-gold-en.json. NO case in the English gold
    /// carries a register, so that whole run is short-pipeline-only and the report must degrade to a
    /// single honest block rather than printing an empty "production" one.
    /// </summary>
    [Fact]
    public void EnglishGold_RidesOneSurfaceOnly_AndItsSplitIsFinite()
    {
        var english = ProofreadQualityTests.LoadProofreadGold(EnglishGoldFile);
        Assert.NotEmpty(english);
        Assert.Empty(GoldPromptSurfaces.OnSurface(english, GoldPromptSurface.ProductionLongPlusShort));

        var split = GoldPromptSurfaces.Split(english.Select(ScoredRecordFor).ToArray());
        Assert.Equal(english.Length, split.ShortOnlyCases);
        Assert.Equal(0, split.ProductionCases);
        Assert.Equal(0, split.ChunkedCases);
        Assert.True(split.IsSingleSurface);
        Assert.Equal("n/a", split.Production.PrecisionDisplay("P1"));
        Assert.Equal("n/a", split.Chunked.PrecisionDisplay("P1"));
        foreach (var score in new[] { split.ShortOnly, split.Production, split.Chunked, split.All })
            AssertRatesAreFinite(score);
    }

    /// <summary>A model-free stand-in for one scored case: the surface is real, the metrics are zero.</summary>
    private static GoldCaseScore ScoredRecordFor(HebrewRegressionCase c) =>
        Record(c.Id, GoldPromptSurfaces.SurfaceOf(c), expected: 0, produced: 0, matched: 0);

    private static GoldCaseScore Record(string id, GoldPromptSurface surface, int expected, int produced, int matched) =>
        new(id, surface, expected, produced, matched,
            NoChangeCase: false, NoChangeWithCorrection: false, Errored: false,
            InputTokens: 0, OutputTokens: 0,
            OverreachEdits: 0, DeclaresForbidden: false, OverreachHit: false);

    private static void AssertRatesAreFinite(ModelScore score)
    {
        foreach (var (name, value) in new[]
                 {
                     ("precision", score.Precision),
                     ("recall", score.Recall),
                     ("false-positive rate", score.FalsePositiveRate),
                     ("overreach rate", score.OverreachRate),
                     ("f0.5", score.F0Point5)
                 })
        {
            Assert.False(double.IsNaN(value), $"{name} is NaN (a divide-by-zero on an empty subset)");
            Assert.False(double.IsInfinity(value), $"{name} is infinite");
        }
    }
}
