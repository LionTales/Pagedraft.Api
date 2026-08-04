using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and the whole point of this file's location is to be outside it.
// Same rule (and same reason) as ProofreadAgreementGoldTests.
using ProofreadQualityTests = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;
using HebrewRegressionCase = Pagedraft.Api.Tests.LanguageEngine.HebrewRegressionCase;
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;
using GoldPromptSurfaces = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurfaces;
using GoldCaseScore = Pagedraft.Api.Tests.LanguageEngine.GoldCaseScore;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC, NO-MODEL, NO-GPU gate on the STANDING FLOOR (c3) - the no-regression bar Wave 2
/// changes are measured against, and the three-surface partition that makes the bar readable.
///
/// WHY IT EXISTS. g1 measured the corpus once, on 2026-08-04, on Ollama / gemma4:12b. Those numbers
/// are the bar, and their only consumers are live-GPU tests that are skip-by-default: nothing in a
/// standing gate would notice the floor going stale, losing a fixture, naming a gold row that no
/// longer exists, or - the expensive one - quietly following a model swap so that a new model's
/// numbers are compared against an old model's bar. Everything below is what CAN be checked without a
/// model, which is more than it sounds: the floor's internal arithmetic ties to the gold corpus in two
/// independent places, and the decision function itself is pure.
///
/// WHAT IT PINS
///  1. DATA VALIDITY. The floor covers every fixture and only real fixtures, states its model, n and
///     provider, carries both outcome classes, and its per-chunk-prompt claim is derivable from the
///     real chunker rather than remembered.
///  2. THE THREE-SURFACE PARTITION. The corpus now spans THREE non-comparable prompt surfaces. Every
///     standing case lands in exactly one bucket, no case falls out of all of them, and each chunked
///     fixture's DECLARED surface agrees with the routing rule that actually decides it - the same
///     anti-drift shape ProofreadAgreementGoldTests uses against BuildGoldRequest.
///  3. THE COMPOSED PROMPT, per surface. Three surfaces is a claim about three DIFFERENT prompts, not
///     a labelling convention, so the distinguishing features of each are asserted on real composed
///     requests.
///  4. THE FLOOR'S TIES TO THE GOLD. The punctuation split names gold rows; those rows must exist,
///     the gershayim offenders must be exactly the gershayim-bearing rows, and two independent
///     arithmetic identities (the 37.5% case rate and the 70% precision ceiling) must reproduce from
///     the corpus. Edit the gold and the floor stops being measurable - loudly, here.
///  5. THE DECISION FUNCTION, in both directions. An expected-pass that stops passing and a
///     known-defect that starts passing are BOTH failures and are distinguishable from each other and
///     from a floor that held.
///
/// Every "no offenders" assertion first proves its population is non-empty - the vacuity class that
/// has bitten this corpus four times, sharpened by the fact that <c>LoadProofreadGold</c> returns an
/// EMPTY array (rather than throwing) when the JSON is missing from the output directory.
///
/// FILE SIZE: this class is past the workspace's ~700-line soft ceiling, WAIVED ON PURPOSE rather than
/// split, on the same grounds as <see cref="ProofreadAgreementGoldTests"/>. Everything here guards ONE
/// artifact (ProofreadStandingFloor) through one shared, vacuity-guarded gold loader, and the split
/// that suggests itself - "data validity" vs "the decision function" - would separate the tests that
/// prove the floor's numbers are real from the tests that prove those same numbers are acted on, which
/// is the seam a reader most needs to see whole. Revisit if a SECOND floor needs the same guards, at
/// which point the loader and the corpus enumerator, not the tests, are what move.
/// </summary>
public class ProofreadStandingFloorTests
{
    private const string NamePrefix = "agree-name-";
    private const string RegisterPrefix = "agree-register-";
    private const string PreservePrefix = "agree-preserve-";

    private static HebrewRegressionCase[] LoadGold()
    {
        var gold = ProofreadQualityTests.LoadProofreadGold();
        Assert.True(gold.Length > 0,
            "proofread-gold.json loaded as an EMPTY array. LoadProofreadGold returns Array.Empty rather " +
            "than throwing when the file is missing from the output directory, so every gold-keyed " +
            "assertion in this class would pass by iterating nothing.");
        return gold;
    }

    private static HebrewRegressionCase[] Bucket(string prefix) =>
        LoadGold().Where(c => c.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    // ── 1. data validity ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FLOOR COVERS THE CORPUS, IN BOTH DIRECTIONS. A fixture with no floor entry is measured
    /// against nothing; a floor entry naming no fixture is a bar nobody can cross. Both are silent.
    /// </summary>
    [Fact]
    public void TheFloor_CoversEveryChunkedFixture_AndNamesNoFixtureThatDoesNotExist()
    {
        var fixtures = ChunkedAgreementFixtures.All;
        Assert.NotEmpty(fixtures);
        Assert.NotEmpty(ProofreadStandingFloor.ChunkedAgreement);

        var fixtureIds = fixtures.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var floorIds = ProofreadStandingFloor.ChunkedAgreement.Select(e => e.FixtureId).ToArray();

        Assert.Equal(floorIds.Length, floorIds.Distinct(StringComparer.Ordinal).Count());

        var unmeasured = fixtureIds.Except(floorIds, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.True(unmeasured.Length == 0,
            "These chunked fixtures have NO standing floor entry, so a run of them is measured against " +
            "nothing and neither a regression nor a fix would be visible: " + string.Join(", ", unmeasured));

        var orphans = floorIds.Except(fixtureIds, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.True(orphans.Length == 0,
            "These floor entries name a fixture that no longer exists, so the bar they set can never be " +
            "measured: " + string.Join(", ", orphans));

        // ...and ForFixture resolves every one of them (it throws rather than returning null).
        Assert.All(fixtures, f => Assert.Equal(f.Id, ProofreadStandingFloor.ForFixture(f.Id).FixtureId));
    }

    /// <summary>
    /// EVERY ENTRY IS WELL-FORMED, AND BOTH OUTCOME CLASSES ARE POPULATED. The second half is the
    /// non-vacuity floor for the whole two-sided design: if every entry were an ExpectedPass, the
    /// KnownDefect semantics below would be untested against real data and the corpus would have
    /// quietly stopped pinning the reproduced defect at all.
    ///
    /// The outcome must also AGREE with the measured numbers. g1 measured zero spread on all four
    /// fixtures (0/15 twice, 15/15 twice), so a partial floor (say 9/15) is not a shape this corpus
    /// has; allowing one would mean the bar's meaning ("passes" / "fails") had silently become
    /// "passes often enough", which is not a claim n=15 on one case can support.
    /// </summary>
    [Fact]
    public void EveryFloorEntry_IsWellFormed_AndBothOutcomeClassesArePopulated()
    {
        var entries = ProofreadStandingFloor.ChunkedAgreement;
        Assert.NotEmpty(entries);

        var defects = new List<string>();
        foreach (var e in entries)
        {
            if (e.MeasuredRuns < ProofreadStandingFloor.SingleCaseMinimumRuns)
                defects.Add($"{e.FixtureId}: measured over {e.MeasuredRuns} run(s), below the " +
                            $"n>={ProofreadStandingFloor.SingleCaseMinimumRuns} single-case rule this corpus " +
                            "inherits (each fixture IS one case)");
            if (e.MeasuredHits < 0 || e.MeasuredHits > e.MeasuredRuns)
                defects.Add($"{e.FixtureId}: {e.MeasuredHits} hits over {e.MeasuredRuns} runs is outside " +
                            "its own denominator");
            if (e.Outcome == FloorOutcome.ExpectedPass && e.MeasuredHits != e.MeasuredRuns)
                defects.Add($"{e.FixtureId}: tagged ExpectedPass but measured {e.MeasuredHits}/{e.MeasuredRuns}");
            if (e.Outcome == FloorOutcome.KnownDefect && e.MeasuredHits != 0)
                defects.Add($"{e.FixtureId}: tagged KnownDefect but measured {e.MeasuredHits}/{e.MeasuredRuns}");
            if (e.OverCorrectionsPerRunMean < 0)
                defects.Add($"{e.FixtureId}: negative over-correction mean");
            if (e.Meaning.Trim().Length < 60)
                defects.Add($"{e.FixtureId}: the Meaning is too short to say what a movement would mean; " +
                            "a bar without a reason is a number nobody can act on");
        }

        Assert.True(defects.Count == 0, string.Join("\n  ", defects));

        // NON-VACUITY for the two-sided semantics: the corpus really does hold both classes today.
        Assert.Contains(entries, e => e.Outcome == FloorOutcome.ExpectedPass);
        Assert.Contains(entries, e => e.Outcome == FloorOutcome.KnownDefect);
        Assert.Equal(2, entries.Count(e => e.Outcome == FloorOutcome.KnownDefect));
        Assert.Equal(2, entries.Count(e => e.Outcome == FloorOutcome.ExpectedPass));
    }

    /// <summary>
    /// THE FLOOR IS MODEL-CONDITIONAL AND MUST SAY WHICH MODEL. It records a LITERAL rather than
    /// aliasing the shipped constant, precisely so that this test can fail when they diverge: a model
    /// swap does not regress the floor, it VOIDS it, and comparing a new model's numbers against an old
    /// model's bar is the most expensive silent mistake available here.
    ///
    /// If this fails, the fix is to RE-MEASURE (g1's commands are in the plan) and rewrite the floor
    /// with the new model's numbers - never to edit this constant to make the test green.
    /// </summary>
    [Fact]
    public void TheFloor_NamesTheShippedProofreadModel_AndItsProviderAndDate()
    {
        Assert.Equal(ProofreadQualityTests.ProofreadModel, ProofreadStandingFloor.MeasuredOnModel);
        Assert.Equal("Ollama", ProofreadStandingFloor.MeasuredOnProvider);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", ProofreadStandingFloor.MeasuredOn);
        Assert.Equal(15, ProofreadStandingFloor.SingleCaseMinimumRuns);
        Assert.Equal(5, ProofreadStandingFloor.PunctuationMeasuredRuns);
    }

    /// <summary>
    /// THE PUBLISHED PROMPT-CAPTURE COUNT IS DERIVABLE, not remembered. g1's attribution rests on
    /// "135/135 per-chunk prompts carried a byte-correct [CHARACTER_REGISTER]", and that denominator is
    /// sum(realized chunk count) x n. Driving the REAL chunker to reproduce it ties the claim to the
    /// corpus: a fixture that changes length (and therefore chunk count) makes the published figure
    /// wrong, and this is what says so.
    /// </summary>
    [Fact]
    public void ThePublishedPerChunkPromptCount_IsWhatTheRealChunkerImplies()
    {
        var chunksPerRepetition = ChunkedAgreementFixtures.All
            .Sum(f => ChunkedAgreementHarness.Chunk(f).Count);
        Assert.True(chunksPerRepetition > 0, "the chunker produced no chunks at all");

        Assert.Equal(
            ProofreadStandingFloor.PerChunkPromptsCaptured,
            chunksPerRepetition * ProofreadStandingFloor.SingleCaseMinimumRuns);
    }

    // ── 2. the three-surface partition ───────────────────────────────────────────────────────────

    /// <summary>
    /// One standing case: its id, the surface it rides, and which corpus it came from. The two corpora
    /// are stored separately (gold rows are single-shot cases in JSON; chunked cases are multi-chunk
    /// chapters driven through <c>RunAsync</c>, which <c>BuildGoldRequest</c> cannot compose), but they
    /// are now tagged from ONE surface vocabulary, so they can be partitioned together.
    /// </summary>
    private static IReadOnlyList<(string Id, GoldPromptSurface Surface, string Corpus)> StandingCorpus() =>
        LoadGold()
            .Select(c => (c.Id, GoldPromptSurfaces.SurfaceOf(c), "proofread-gold.json"))
            .Concat(ChunkedAgreementFixtures.All
                .Select(f => (f.Id, f.Surface, "ChunkedAgreementFixtures")))
            .ToArray();

    /// <summary>
    /// THE PROMOTION, and the hazard it had to clear. The standing corpus now spans THREE
    /// non-comparable prompt surfaces; every case is tagged, every tag is one of the three, all three
    /// are populated, and the ids stay unique ACROSS the two corpora so an id-keyed report cannot
    /// conflate them.
    ///
    /// The third assertion is the one c1 wrote this promotion around: aggregating the standing corpus
    /// through the real <c>Split</c> must account for every record in exactly one per-surface bucket.
    /// With only two buckets the chunked records would have appeared ONLY in the mixed ALL block - the
    /// silent mixing the split exists to prevent - so this is the test that fails if a fourth surface
    /// is ever added to the enum without being added to the split.
    /// </summary>
    [Fact]
    public void TheStandingCorpus_SpansAllThreeSurfaces_AndEveryCaseLandsInExactlyOneBucket()
    {
        var corpus = StandingCorpus();
        Assert.NotEmpty(corpus);

        // Ids unique across BOTH corpora (the chunked ids mirror the gold's agree-* naming on purpose).
        var ids = corpus.Select(c => c.Id).ToArray();
        var collisions = ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToArray();
        Assert.True(collisions.Length == 0,
            "An id occurs in BOTH standing corpora, so any id-keyed report would conflate a gold row " +
            "with a chunked fixture: " + string.Join(", ", collisions));

        // All three surfaces populated - otherwise "three non-comparable surfaces" is aspirational.
        foreach (var surface in GoldPromptSurfaces.AllSurfaces)
        {
            Assert.True(corpus.Any(c => c.Surface == surface),
                $"NO standing case rides {surface}, so every per-surface claim about it is vacuous " +
                $"({GoldPromptSurfaces.Describe(surface)})");
        }
        Assert.Equal(3, GoldPromptSurfaces.AllSurfaces.Count);

        // A PARTITION: the per-surface counts sum to the whole corpus, so nothing is double-counted and
        // nothing falls out of every bucket.
        Assert.Equal(corpus.Count, GoldPromptSurfaces.AllSurfaces.Sum(s => corpus.Count(c => c.Surface == s)));

        // ...and the REAL aggregator agrees, which is what an actual report would read.
        var split = GoldPromptSurfaces.Split(corpus.Select(c => SurfaceOnlyRecord(c.Id, c.Surface)).ToArray());
        Assert.Equal(corpus.Count, split.AllCases);
        Assert.Equal(corpus.Count, split.ShortOnlyCases + split.ProductionCases + split.ChunkedCases);
        Assert.Equal(3, split.PopulatedSurfaces);
        Assert.False(split.IsSingleSurface);
        foreach (var surface in GoldPromptSurfaces.AllSurfaces)
            Assert.Equal(corpus.Count(c => c.Surface == surface), split.On(surface).Cases);
    }

    /// <summary>
    /// THE ANTI-DRIFT ASSERTION FOR THE CHUNKED SURFACE, mirroring
    /// <c>ProofreadAgreementGoldTests.PromptSurfacePredicate_AgreesWithBuildGoldRequest_ForEveryGoldCase</c>.
    ///
    /// A gold row's surface is DERIVED from the request builder. A chunked fixture's surface is
    /// DECLARED - so what makes the declaration true is the ROUTING RULE, which depends only on the
    /// fixture's word count against the language-keyed chunk target. The two are computed independently
    /// here and compared, so a fixture that grows or shrinks past the threshold fails loudly instead of
    /// changing regime while keeping its old label - and its floor with it.
    /// </summary>
    [Fact]
    public void EveryChunkedFixture_DeclaresTheSurfaceTheRoutingRuleActuallyGivesIt()
    {
        var disagreements = new List<string>();
        var checkedFixtures = 0;

        foreach (var f in ChunkedAgreementFixtures.All)
        {
            checkedFixtures++;
            var derived = GoldPromptSurfaces.DerivedSurfaceOf(f);
            if (derived != f.Surface)
            {
                disagreements.Add(
                    $"{f.Id}: declares {f.Surface} but the routing rule gives {derived} " +
                    $"({ChunkedAgreementHarness.WordCount(ChunkedAgreementHarness.ProductionTargetText(f))} " +
                    $"words against a {ChunkedAgreementHarness.ChunkTargetWordsFor(f)}-word target)");
            }
        }

        Assert.True(checkedFixtures > 0, "no fixture was checked");
        Assert.True(disagreements.Count == 0,
            "A chunked fixture's DECLARED prompt surface has drifted from the surface the routing rule " +
            "actually gives it, so its standing floor is a bar for a regime it no longer rides:\n  " +
            string.Join("\n  ", disagreements));

        // BOTH derived values occur, otherwise the derivation is only exercised in one direction.
        Assert.Contains(ChunkedAgreementFixtures.All, f => GoldPromptSurfaces.DerivedSurfaceOf(f) == GoldPromptSurface.ChunkedPerChunk);
        Assert.Contains(ChunkedAgreementFixtures.All, f => GoldPromptSurfaces.DerivedSurfaceOf(f) == GoldPromptSurface.ProductionLongPlusShort);

        // ...and every floor entry inherits its fixture's surface rather than restating one.
        Assert.All(ProofreadStandingFloor.ChunkedAgreement,
            e => Assert.Equal(ChunkedAgreementFixtures.ById(e.FixtureId).Surface, e.Surface));
    }

    // ── 3. the composed prompt, per surface ──────────────────────────────────────────────────────

    /// <summary>
    /// THREE SURFACES MEANS THREE DIFFERENT PROMPTS, asserted on real composed requests rather than
    /// taken as a labelling convention. Each surface's distinguishing feature:
    ///
    ///  - SHORT PIPELINE ONLY: the caller leaves <c>Instruction</c> null, so the router sends the legacy
    ///    short instruction alone. No register block exists to be read.
    ///  - PRODUCTION LONG+SHORT: one call, a <c>[CHARACTER_REGISTER]</c> block, NO <c>[CONTEXT_BEFORE]</c>
    ///    section, and the input NOT wrapped.
    ///  - CHUNKED PER-CHUNK: several calls, a register block on every one, a <c>[CONTEXT_BEFORE]</c>
    ///    overlap on every call after the first, and the input wrapped in <c>[TEXT_TO_CORRECT]</c>.
    ///
    /// The overlap and the wrapping are exactly what makes the chunked numbers non-comparable to the
    /// single-shot ones: the model is being shown a fragment of a document plus context it is told not
    /// to correct, and that is the regime g1's attribution is about.
    /// </summary>
    [Fact]
    public async Task TheThreeSurfaces_ComposeThreeDistinguishablePrompts()
    {
        // --- short pipeline only (a gold row with no register) ---
        var shortOnly = LoadGold()
            .Where(c => GoldPromptSurfaces.SurfaceOf(c) == GoldPromptSurface.ShortPipelineOnly)
            .ToArray();
        Assert.NotEmpty(shortOnly);
        Assert.All(shortOnly, c => Assert.Null(ProofreadQualityTests.BuildGoldRequest(c).Instruction));

        // --- production long+short (a gold row with a register, and the chunked corpus's control) ---
        var production = LoadGold()
            .Where(c => GoldPromptSurfaces.SurfaceOf(c) == GoldPromptSurface.ProductionLongPlusShort)
            .ToArray();
        Assert.NotEmpty(production);
        Assert.All(production, c =>
        {
            var request = ProofreadQualityTests.BuildGoldRequest(c);
            Assert.NotNull(request.Instruction);
            Assert.Contains("[CHARACTER_REGISTER]", request.Instruction!, StringComparison.Ordinal);
            // The wrapping is a property of the INPUT, never of the instruction: the ProofreadHe body
            // NAMES [TEXT_TO_CORRECT] in its own prose ("if the text contains a [TEXT_TO_CORRECT]
            // marker..."), so asserting the marker's absence from the instruction would be asserting
            // that the body stopped explaining itself. Same trap RecordingChunkRouter.Section was
            // written around.
            Assert.Equal(c.Input, request.InputText);
            Assert.DoesNotContain(RecordingChunkRouter.TextToCorrectOpen, request.InputText, StringComparison.Ordinal);
        });

        var control = ChunkedAgreementFixtures.Control;
        Assert.Equal(GoldPromptSurface.ProductionLongPlusShort, control.Surface);
        var controlRun = await ChunkedAgreementHarness.RunAsync(control);
        Assert.False(controlRun.RanChunked);
        var controlCall = Assert.Single(controlRun.Calls);
        Assert.True(controlCall.HasCharacterRegisterBlock);
        Assert.Null(controlCall.OverlapPrefix);
        Assert.DoesNotContain(RecordingChunkRouter.TextToCorrectOpen, controlCall.WrappedInputText,
            StringComparison.Ordinal);

        // --- chunked per-chunk ---
        var chunkedFixtures = ChunkedAgreementFixtures.All
            .Where(f => f.Surface == GoldPromptSurface.ChunkedPerChunk)
            .ToArray();
        Assert.NotEmpty(chunkedFixtures);

        var overlapCarryingCalls = 0;
        foreach (var f in chunkedFixtures)
        {
            var run = await ChunkedAgreementHarness.RunAsync(f);
            Assert.True(run.RanChunked, $"{f.Id}: did not take the chunked route");
            Assert.True(run.Calls.Count > 1, $"{f.Id}: a one-call run is not the chunked surface");

            for (var i = 0; i < run.Calls.Count; i++)
            {
                var call = run.Calls[i];
                Assert.True(call.HasCharacterRegisterBlock, $"{f.Id} chunk {i}: no register block");
                Assert.Contains(RecordingChunkRouter.TextToCorrectOpen, call.WrappedInputText,
                    StringComparison.Ordinal);

                // The overlap is what a first chunk cannot have and every later chunk does.
                if (i == 0)
                    Assert.Null(call.OverlapPrefix);
                else
                {
                    Assert.NotNull(call.OverlapPrefix);
                    overlapCarryingCalls++;
                }
            }
        }

        // NON-VACUITY for the overlap claim: at least one call actually carried one, so "every later
        // chunk has an overlap" was not satisfied by there being no later chunks.
        Assert.True(overlapCarryingCalls > 0, "no chunk carried a [CONTEXT_BEFORE] overlap");
    }

    // ── 4. the punctuation floor's ties to the gold corpus ───────────────────────────────────────

    /// <summary>
    /// EVERY GOLD ROW THE PUNCTUATION SPLIT NAMES STILL EXISTS, and the split's own shape is
    /// consistent: a phenomenon row with edits names at least one row, a row with zero edits names
    /// none, and every offender on the preservation subset is a <c>shouldHaveNoChanges</c> case (which
    /// is what makes any edit there spurious BY DEFINITION rather than by judgement).
    /// </summary>
    [Fact]
    public void ThePunctuationSplit_NamesOnlyRealGoldRows_AndItsZeroRowsNameNone()
    {
        var gold = LoadGold();
        var byId = gold.ToDictionary(c => c.Id, StringComparer.Ordinal);
        Assert.NotEmpty(ProofreadStandingFloor.PunctuationPhenomena);

        var defects = new List<string>();
        var namedRows = 0;

        foreach (var p in ProofreadStandingFloor.PunctuationPhenomena)
        {
            if (p.EditsPerRun < 0)
                defects.Add($"{p.Subset}/{p.Phenomenon}: negative edits per run");
            if (p.EditsPerRun == 0 && p.GoldCaseIds.Count > 0)
                defects.Add($"{p.Subset}/{p.Phenomenon}: measured 0 edits per run but names " +
                            $"{p.GoldCaseIds.Count} gold row(s); a zero row must name none");
            if (p.EditsPerRun > 0 && p.GoldCaseIds.Count == 0)
                defects.Add($"{p.Subset}/{p.Phenomenon}: measured {p.EditsPerRun} edit(s) per run but " +
                            "names no gold row, so the claim cannot be audited or re-measured");
            if (p.Surface != GoldPromptSurface.ProductionLongPlusShort)
                defects.Add($"{p.Subset}/{p.Phenomenon}: the punctuation split was measured on the " +
                            $"production long+short surface, not on {p.Surface}");

            foreach (var id in p.GoldCaseIds)
            {
                namedRows++;
                if (!byId.TryGetValue(id, out var c))
                {
                    defects.Add($"{p.Subset}/{p.Phenomenon}: names gold row '{id}', which no longer exists");
                    continue;
                }
                if (!c.Id.StartsWith(p.Subset, StringComparison.Ordinal))
                    defects.Add($"{p.Subset}/{p.Phenomenon}: names '{id}', which is not in that subset");
                if (p.Subset == PreservePrefix && c.ShouldHaveNoChanges != true)
                    defects.Add($"'{id}' is named as a preservation-subset offender but is not a " +
                                "shouldHaveNoChanges case, so an edit there is not spurious by definition");
            }
        }

        Assert.True(namedRows > 0,
            "the punctuation split names NO gold row at all, so every check above iterated nothing");
        Assert.True(defects.Count == 0, string.Join("\n  ", defects));
    }

    /// <summary>
    /// THE GERSHAYIM OFFENDERS ARE EXACTLY THE GERSHAYIM-BEARING GOLD ROWS. This is the strongest tie
    /// the floor has to the corpus, and it binds in both directions: a named offender that lost its
    /// gershayim can no longer produce the swap (the floor overstates the tax), and a gold row that
    /// GAINS one becomes a new swap site nobody measured (the floor understates it). Either way the
    /// 37.5% / 70% figures stop meaning what they say, and this is what refuses to let that happen
    /// quietly.
    ///
    /// NON-VACUITY: the gershayim-bearing subset is proved non-empty before the sets are compared, so a
    /// gold file that lost ALL its gershayim cannot satisfy "the two sets are equal" with two empties.
    /// </summary>
    [Fact]
    public void TheGershayimOffenders_AreExactlyTheGoldRowsThatCarryAGershayim()
    {
        const string gershayim = "״";

        var bearing = LoadGold()
            .Where(c => c.Input.Contains(gershayim, StringComparison.Ordinal))
            .Select(c => c.Id)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(bearing);

        var named = ProofreadStandingFloor.PunctuationPhenomena
            .Where(p => p.Phenomenon == "gershayim-swap")
            .SelectMany(p => p.GoldCaseIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(named);

        Assert.Equal(bearing, named);

        // The OPPOSITE direction is still declared by the corpus, which is what makes the swap
        // indefensible as "the model normalizes quotes": it converts ״ to " AND fails to convert " to ״
        // when the gold explicitly asks (missed in every run of both g1 arms).
        var opposite = LoadGold().Single(c => c.Id == ProofreadStandingFloor.AsciiToGershayimGoldCaseId);
        Assert.NotNull(opposite.ExpectedCorrections);
        Assert.Contains(opposite.ExpectedCorrections!, e =>
            e.Original.Contains('"') && e.Suggested.Contains(gershayim, StringComparison.Ordinal));
    }

    /// <summary>
    /// THE FLOOR'S TWO ARITHMETIC IDENTITIES, recomputed from the corpus. Neither is a restatement:
    /// both derive a metric bar from the phenomenon split PLUS the gold corpus, so editing either side
    /// alone breaks them.
    ///
    ///  (1) <c>agree-preserve.overCorrectionRate</c> = distinct offending rows / preservation cases.
    ///      3 of 8 = 37.5%. Add a preservation case, or a ninth offender, and the rate the floor quotes
    ///      is no longer the rate the corpus would produce.
    ///  (2) <c>agree-name.precision</c> = expected / (expected + spurious). The subset declares 7
    ///      expected corrections and recall is floored at 100%, so every one is matched; the split
    ///      attributes 3 spurious edits per run, all gershayim; 7/10 = 70%. That is the entire content
    ///      of "100% of agree-name's precision loss is the gershayim swap" - and it is now checkable.
    /// </summary>
    [Fact]
    public void TheMetricFloors_ReproduceFromTheCorpusAndThePhenomenonSplit()
    {
        // (1) the preservation over-correction case rate
        var preserve = Bucket(PreservePrefix);
        Assert.NotEmpty(preserve);
        var offenders = ProofreadStandingFloor.OffendingGoldCaseIds(PreservePrefix);
        Assert.NotEmpty(offenders);
        Assert.All(offenders, id => Assert.Contains(preserve, c => c.Id == id));

        var impliedRate = offenders.Count / (double)preserve.Length;
        Assert.Equal(ProofreadStandingFloor.Metric("agree-preserve.overCorrectionRate").Value, impliedRate, 9);

        // (2) the agree-name precision ceiling
        var name = Bucket(NamePrefix);
        Assert.NotEmpty(name);
        var expectedCorrections = name.Sum(c => c.ExpectedCorrections?.Length ?? 0);
        Assert.True(expectedCorrections > 0, "the agree-name subset declares no expected correction");

        // The derivation assumes saturated recall, which the floor itself asserts - state the dependency
        // rather than letting it hide inside the arithmetic.
        Assert.Equal(1.00, ProofreadStandingFloor.Metric("agree-name.recall").Value, 9);

        var spurious = ProofreadStandingFloor.PhenomenonEditsPerRun("gershayim-swap", NamePrefix);
        Assert.True(spurious > 0, "the split attributes no spurious edit to agree-name, so the precision " +
                                  "ceiling would be 100% and there would be nothing to floor");
        Assert.Equal(ProofreadStandingFloor.Metric("agree-name.spuriousEdits").Value, (double)spurious, 9);

        // ...and NOTHING but the gershayim swap contributes to that subset's spurious edits, which is
        // the claim "remove the swap and this subset is 100% precise" rests on.
        Assert.Equal(spurious, ProofreadStandingFloor.PunctuationPhenomena
            .Where(p => p.Subset == NamePrefix).Sum(p => p.EditsPerRun));

        var impliedPrecision = expectedCorrections / (double)(expectedCorrections + spurious);
        Assert.Equal(ProofreadStandingFloor.Metric("agree-name.precision").Value, impliedPrecision, 9);
    }

    /// <summary>
    /// THE SCALAR BARS ARE WELL-FORMED AND EVERY ONE STATES ITS SURFACE. Ids unique, meanings written,
    /// gold-prefixed subsets resolving to a real non-empty bucket, and - the bit that keeps the corpus
    /// honest about being three-surfaced - at least one bar on each of the three surfaces, since a
    /// floor whose bars all sit on one surface would leave the others unmeasured while looking complete.
    /// </summary>
    [Fact]
    public void EveryMetricFloor_StatesItsSurfaceAndSubset_AndAllThreeSurfacesAreFloored()
    {
        var metrics = ProofreadStandingFloor.Metrics;
        Assert.NotEmpty(metrics);

        var ids = metrics.Select(m => m.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        var defects = new List<string>();
        var goldSubsetsChecked = 0;
        foreach (var m in metrics)
        {
            if (string.IsNullOrWhiteSpace(m.Subset)) defects.Add($"{m.Id}: no subset");
            if (string.IsNullOrWhiteSpace(m.Unit)) defects.Add($"{m.Id}: no unit");
            if (m.Meaning.Trim().Length < 60) defects.Add($"{m.Id}: the Meaning is too short to act on");
            if (!GoldPromptSurfaces.AllSurfaces.Contains(m.Surface)) defects.Add($"{m.Id}: unknown surface");
            if (double.IsNaN(m.Value) || double.IsInfinity(m.Value)) defects.Add($"{m.Id}: non-finite value");

            if (m.Subset.StartsWith("agree-", StringComparison.Ordinal))
            {
                goldSubsetsChecked++;
                if (Bucket(m.Subset).Length == 0)
                    defects.Add($"{m.Id}: subset '{m.Subset}' matches no gold row, so the bar is unmeasurable");
            }
        }

        Assert.True(goldSubsetsChecked > 0, "no metric floor named a gold subset");
        Assert.True(defects.Count == 0, string.Join("\n  ", defects));

        foreach (var surface in GoldPromptSurfaces.AllSurfaces)
        {
            Assert.True(metrics.Any(m => m.Surface == surface),
                $"NO standing metric bar sits on {surface}, so that surface is unfloored while the floor " +
                "as a whole looks complete");
        }

        // The two instrument tripwires must remain zeros: they are what makes the over-correction column
        // the model's alone (whitespace) and what stops a transport failure reading as a model result.
        Assert.Equal(0d, ProofreadStandingFloor.Metric("chunked.whitespaceOnlySuggestions").Value, 9);
        Assert.Equal(0d, ProofreadStandingFloor.Metric("chunked.transportFailures").Value, 9);
        Assert.Equal(FloorBound.Exactly, ProofreadStandingFloor.Metric("chunked.whitespaceOnlySuggestions").Bound);
        Assert.Equal(FloorBound.Exactly, ProofreadStandingFloor.Metric("chunked.transportFailures").Bound);
    }

    /// <summary>
    /// EXACTLY ONE BAR DECLARES A BAND, AND EVERY OTHER MEASURES WHAT IT TOLERATES (be-c04).
    ///
    /// A band is the gap between what g1 MEASURED and what the floor TOLERATES, and it is deliberate
    /// slack: <c>agree-preserve.agreementBearingOverCorrection</c> measured 0 but is barred at the older
    /// baseline's 1, so the floor is not tightened onto a single flap. Slack is defensible; slack nobody
    /// declared is not. Widening a bar is the cheapest way to make a regression stop failing, and with
    /// <c>MeasuredValue</c> defaulting to <c>Value</c> a second widened bar would otherwise appear with
    /// no ceremony at all - so the inventory is pinned by NAME here, and a future author has to come and
    /// change this list rather than quietly adding a row to it.
    ///
    /// The coherence half is the other direction: a band must run from the measurement to the bar on the
    /// GOOD side, and an <c>Exactly</c> tripwire may not have one, because "tolerate worse than the
    /// tripwire" is a contradiction rather than slack.
    /// </summary>
    [Fact]
    public void ExactlyOneBar_DeclaresABand_AndEveryOtherMeasuresWhatItTolerates()
    {
        var metrics = ProofreadStandingFloor.Metrics;
        Assert.NotEmpty(metrics);

        var banded = metrics.Where(m => m.HasBand).Select(m => m.Id)
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Assert.True(
            banded.SequenceEqual(new[] { "agree-preserve.agreementBearingOverCorrection" }, StringComparer.Ordinal),
            "The set of bars that TOLERATE something other than what they MEASURED has changed. It is " +
            "supposed to be exactly agree-preserve.agreementBearingOverCorrection (measured 0, barred at " +
            "the 2026-08-02 baseline's 1); it is now [" + string.Join(", ", banded) + "]. Widening a bar " +
            "is how a regression stops failing without anyone deciding it should, so if the new band is " +
            "intended, say so HERE and in that row's Meaning - both numbers and why they differ.");

        var defects = new List<string>();
        foreach (var m in metrics)
        {
            if (double.IsNaN(m.MeasuredValue) || double.IsInfinity(m.MeasuredValue))
                defects.Add($"{m.Id}: non-finite MeasuredValue");

            if (!m.HasBand)
            {
                // Stated explicitly rather than left implied by HasBand: this is the property the nine
                // unbanded bars are relied on for everywhere else in this file.
                if (m.MeasuredValue != m.Value)
                    defects.Add($"{m.Id}: measured {m.MeasuredValue} against a bar of {m.Value}");
                continue;
            }

            var coherent = m.Bound switch
            {
                FloorBound.AtMost => m.MeasuredValue < m.Value,
                FloorBound.AtLeast => m.MeasuredValue > m.Value,
                _ => false
            };
            if (!coherent)
                defects.Add($"{m.Id}: measured {m.MeasuredValue} but bounded {m.Bound} {m.Value}. A band " +
                            "runs from the measurement to the bar on the GOOD side, and an Exactly " +
                            "tripwire may not carry one at all.");
        }

        Assert.True(defects.Count == 0, string.Join("\n  ", defects));

        // The banded row's own prose must carry BOTH numbers, or a reader has the bar without the
        // measurement and is back to being unable to tell today's standing state from a gain.
        var bar = ProofreadStandingFloor.Metric("agree-preserve.agreementBearingOverCorrection");
        Assert.Equal(1d, bar.Value, 9);
        Assert.Equal(0d, bar.MeasuredValue, 9);
        Assert.Contains("MEASURED 0", bar.Meaning, StringComparison.Ordinal);
        Assert.Contains("TOLERATED 1", bar.Meaning, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY BAR HAS AN OWNER, AND THE ONES WITH NO EVALUATOR SAY SO IN THEIR OWN PROSE (be-c03).
    ///
    /// The defect this replaces was not a wrong number - it was a TRUE-SOUNDING CLAIM. The floor's
    /// header said it is "the no-regression bar Wave 2 changes are measured against" while the scalar
    /// half had no measuring consumer at all: <c>Metrics</c> / <c>EvaluateMetric</c> /
    /// <c>PunctuationPhenomena</c> were read only by this file. Wiring most of them fixed most of it;
    /// this test is what stops the gap re-opening, and it binds in three independent directions:
    ///
    ///  (1) PARTITION. The three owner lists are disjoint and cover <c>Metrics</c> exactly. A bar added
    ///      to the table without being assigned an owner fails HERE - in the standing deterministic
    ///      suite - rather than joining the unevaluated set silently. Both live consumers are
    ///      skip-by-default and filter-excluded, so this is the only gate that can catch it.
    ///  (2) PROSE. Every unevaluated bar's <c>Meaning</c> carries <c>UnevaluatedMarker</c>, and no
    ///      EVALUATED bar does. Half of that is the one that matters: rewording an unevaluated row
    ///      without the marker fails, and so does leaving the marker on a row that has since been wired.
    ///  (3) NON-VACUITY. The evaluated sets are non-empty (or "everything is owned" would be satisfied
    ///      by owning nothing), and the unevaluated set is a PROPER subset (or the floor as a whole
    ///      would be unevaluated while this test stayed green).
    /// </summary>
    [Fact]
    public void EveryMetricBar_HasExactlyOneOwner_AndUnevaluatedBarsSayThatInTheirMeaning()
    {
        var metrics = ProofreadStandingFloor.Metrics;
        Assert.NotEmpty(metrics);

        var gold = ProofreadStandingFloor.MetricEvaluators.GoldHarnessEvaluatedMetricIds;
        var chunked = ProofreadStandingFloor.MetricEvaluators.ChunkedHarnessEvaluatedMetricIds;
        var unevaluated = ProofreadStandingFloor.MetricEvaluators.UnevaluatedMetricIds;

        // NON-VACUITY first: an empty owner list would satisfy every set operation below.
        Assert.NotEmpty(gold);
        Assert.NotEmpty(chunked);
        Assert.NotEmpty(unevaluated);
        Assert.True(unevaluated.Count < metrics.Count,
            "EVERY bar is unevaluated, so the floor as a whole is a recorded figure and nothing " +
            "'measures against' it - which is precisely the claim this test exists to keep honest.");

        var owned = gold.Concat(chunked).Concat(unevaluated).ToArray();
        var duplicates = owned.GroupBy(id => id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.True(duplicates.Length == 0,
            "These bars appear in more than one owner list, so 'who observes this' has two answers and " +
            "the partition proves nothing: " + string.Join(", ", duplicates));

        var barIds = metrics.Select(m => m.Id).ToArray();
        var unowned = barIds.Except(owned, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.True(unowned.Length == 0,
            "These bars are assigned to NO harness, so nothing states whether anything can observe them. " +
            "Add each to GoldHarnessEvaluatedMetricIds / ChunkedHarnessEvaluatedMetricIds if a harness " +
            "supplies an observation, or to UnevaluatedMetricIds (and say so in its Meaning) if none " +
            "does: " + string.Join(", ", unowned));

        var phantom = owned.Except(barIds, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.True(phantom.Length == 0,
            "These owner-list entries name no bar in Metrics, so a harness believes it is observing " +
            "something that does not exist: " + string.Join(", ", phantom));

        // ...and every named id really resolves through the accessor the harnesses call.
        Assert.All(owned, id => Assert.Equal(id, ProofreadStandingFloor.Metric(id).Id));

        // (2) the prose and the list agree, in BOTH directions.
        const string marker = ProofreadStandingFloor.MetricEvaluators.UnevaluatedMarker;
        var prose = new List<string>();
        foreach (var m in metrics)
        {
            var owns = ProofreadStandingFloor.MetricEvaluators.HasAutomatedEvaluator(m.Id);
            var says = m.Meaning.Contains(marker, StringComparison.Ordinal);
            if (!owns && !says)
                prose.Add($"{m.Id}: has no automated evaluator but its Meaning never says so. A reader " +
                          $"quoting this row as a gate would be quoting a number nobody measures; the " +
                          $"Meaning must carry '{marker}' and name what a future run has to build.");
            if (owns && says)
                prose.Add($"{m.Id}: its Meaning still claims '{marker}' but the bar IS owned by a " +
                          "harness now. Stale prose on a wired bar is the same defect pointing the " +
                          "other way - remove the marker.");
        }
        Assert.True(prose.Count == 0, string.Join("\n  ", prose));

        // NON-VACUITY for (2): the marker really occurs, so the loop above was not comparing two empties.
        Assert.Contains(metrics, m => m.Meaning.Contains(marker, StringComparison.Ordinal));

        // (4) THE CHUNKED OWNER LIST IS PINNED BY NAME, because its consumer cannot check itself.
        //
        // The gold consumer LOOPS GoldHarnessEvaluatedMetricIds and asserts it evaluated all of them, so
        // an id added to that list without an observation wired for it fails there. The chunked consumer
        // (ChunkedAgreementLiveTests.ReportAndGateTheStandingFloor) cannot do the same: its two tripwires
        // read DIFFERENT observations and gate at different points (transport VOIDS the fixture before
        // whitespace is looked at), so it names its two bars individually rather than iterating. That
        // leaves exactly one gap - a third id added to the list would be claimed and never read, in a
        // file that is skip-by-default and filter-excluded and so would never say so. Pinning the list
        // here, in a gate that actually runs, is what forces that author to come and change the live
        // consumer too.
        Assert.True(
            chunked.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(
                new[] { "chunked.transportFailures", "chunked.whitespaceOnlySuggestions" },
                StringComparer.Ordinal),
            "ChunkedHarnessEvaluatedMetricIds has changed. Its consumer, " +
            "ChunkedAgreementLiveTests.ReportAndGateTheStandingFloor, evaluates its two bars BY NAME " +
            "rather than by looping this list, so a bar added here is claimed but never observed - and " +
            "that consumer is skip-by-default and filter-excluded, so nothing else would notice. Wire " +
            "the new bar into that method (or move it to another owner list) and update this pin. It is " +
            "now [" + string.Join(", ", chunked) + "].");
    }

    /// <summary>
    /// THE legacy93 BAR IS A SHARE OVER A NAMED CORPUS, AND THE CORPUS IS STILL THAT SIZE.
    ///
    /// <c>legacy93.recall</c> floors recall at 65.0% over "the 93 register-less gold cases". That is a
    /// RATE, so adding or removing a register-less row changes what 65.0% is a bar for while leaving the
    /// number - and every test of the number - untouched. Deriving the count from the same predicate the
    /// harness partitions on (<c>GoldPromptSurfaces.SurfaceOf</c>) is what makes the corpus move visible.
    ///
    /// This runs deterministically, with no model, so unlike the live gold gate it actually executes.
    /// </summary>
    [Fact]
    public void TheLegacyRecallBar_StillDescribesTheCorpusItWasMeasuredOn()
    {
        var shortOnly = LoadGold()
            .Where(c => GoldPromptSurfaces.SurfaceOf(c) == GoldPromptSurface.ShortPipelineOnly)
            .ToArray();

        Assert.True(shortOnly.Length == ProofreadStandingFloor.LegacyShortOnlyGoldCases,
            $"legacy93.recall is a recall SHARE over the register-less gold rows, and there are now " +
            $"{shortOnly.Length} of them rather than {ProofreadStandingFloor.LegacyShortOnlyGoldCases}. " +
            "The bar's value has not changed but the corpus it bounds has, so 65.0% no longer means what " +
            "it meant when g1 measured it. Re-measure and re-pin both, or revert the corpus change.");

        // The bar really is the one this corpus claim belongs to (and its subset string still names it).
        var bar = ProofreadStandingFloor.Metric("legacy93.recall");
        Assert.Equal(GoldPromptSurface.ShortPipelineOnly, bar.Surface);
        Assert.Contains(
            ProofreadStandingFloor.LegacyShortOnlyGoldCases.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bar.Subset, StringComparison.Ordinal);
    }

    // ── 5. the decision function, in both directions ─────────────────────────────────────────────

    /// <summary>
    /// THE FLOOR AGREES WITH ITSELF. Feeding each entry the very numbers it was set from must produce a
    /// non-failing verdict - and, for the two pinned defects, the verdict must be
    /// <see cref="FloorVerdict.KnownDefectReproduced"/> rather than <see cref="FloorVerdict.Held"/>, so
    /// a run report can never say "everything passed" while half the corpus is a pinned failure.
    /// </summary>
    [Fact]
    public void ReplayingTheFloorsOwnNumbers_HoldsEverywhere_AndNamesTheTwoPinnedDefectsAsDefects()
    {
        var evaluations = ProofreadStandingFloor.ChunkedAgreement
            .Select(e => ProofreadStandingFloor.Evaluate(e, e.MeasuredHits, e.MeasuredRuns))
            .ToArray();
        Assert.NotEmpty(evaluations);

        Assert.All(evaluations, ev => Assert.False(ev.IsFailure, ev.Message));
        Assert.All(evaluations, ev => Assert.True(ev.MeetsSingleCaseN, ev.Message));

        Assert.Equal(2, evaluations.Count(ev => ev.Verdict == FloorVerdict.Held));
        Assert.Equal(2, evaluations.Count(ev => ev.Verdict == FloorVerdict.KnownDefectReproduced));

        // The two arms the plan singles out, by name rather than by count.
        Assert.Equal(FloorVerdict.KnownDefectReproduced, Verdict(ChunkedAgreementFixtures.AntecedentInOverlapId, 0, 15));
        Assert.Equal(FloorVerdict.KnownDefectReproduced, Verdict(ChunkedAgreementFixtures.SeparatedAndDilutedId, 0, 15));
        Assert.Equal(FloorVerdict.Held, Verdict(ChunkedAgreementFixtures.DilutionOnlyId, 15, 15));
        Assert.Equal(FloorVerdict.Held, Verdict(ChunkedAgreementFixtures.SingleChunkControlId, 15, 15));
    }

    /// <summary>
    /// REQUIREMENT (a): AN EXPECTED PASS THAT STOPS PASSING FAILS LOUDLY. One miss is enough - the
    /// entries were measured at zero spread, so "14 of 15" is a movement, not noise, and rounding it
    /// away would hide exactly the kind of partial degradation a prompt change produces.
    /// </summary>
    [Fact]
    public void AnExpectedPassThatDrops_EvenByOneRun_IsAFailure()
    {
        var entry = ProofreadStandingFloor.ForFixture(ChunkedAgreementFixtures.DilutionOnlyId);
        Assert.Equal(FloorOutcome.ExpectedPass, entry.Outcome);

        var held = ProofreadStandingFloor.Evaluate(entry, 15, 15);
        Assert.Equal(FloorVerdict.Held, held.Verdict);
        Assert.False(held.IsFailure);

        foreach (var hits in new[] { 14, 8, 0 })
        {
            var ev = ProofreadStandingFloor.Evaluate(entry, hits, 15);
            Assert.Equal(FloorVerdict.Regressed, ev.Verdict);
            Assert.True(ev.IsFailure);
            Assert.Contains("FLOOR REGRESSED", ev.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// REQUIREMENT (b), AND THE CRUX OF THE WHOLE DESIGN: A KNOWN DEFECT THAT STARTS PASSING MUST NOT
    /// BE INDISTINGUISHABLE FROM ONE THAT STILL FAILS.
    ///
    /// A floor written as "this fixture is allowed to fail" would green identically whether a Wave 2
    /// referent-carry-forward fix landed or nothing happened at all - which would make the fix
    /// invisible to the only instrument built to see it. So a hit on a pinned defect is a FAILURE with
    /// its own verdict and its own message: the defect moved, re-measure and rewrite the floor.
    ///
    /// The three things asserted are the three ways this could go wrong: the verdicts must DIFFER, the
    /// failure flags must differ, and the messages must differ (a shared message would defeat the point
    /// even with distinct enum values, because the enum is not what a human reads at 2am).
    /// </summary>
    [Fact]
    public void AKnownDefectThatStartsPassing_IsAFailure_AndIsDistinguishableFromOneThatStillFails()
    {
        var entry = ProofreadStandingFloor.ForFixture(ChunkedAgreementFixtures.AntecedentInOverlapId);
        Assert.Equal(FloorOutcome.KnownDefect, entry.Outcome);

        var stillFailing = ProofreadStandingFloor.Evaluate(entry, 0, 15);
        var fullyFixed = ProofreadStandingFloor.Evaluate(entry, 15, 15);
        var partlyFixed = ProofreadStandingFloor.Evaluate(entry, 1, 15);

        Assert.Equal(FloorVerdict.KnownDefectReproduced, stillFailing.Verdict);
        Assert.Equal(FloorVerdict.KnownDefectMoved, fullyFixed.Verdict);
        Assert.Equal(FloorVerdict.KnownDefectMoved, partlyFixed.Verdict);

        // (i) distinct verdicts, (ii) distinct failure flags, (iii) distinct messages.
        Assert.NotEqual(stillFailing.Verdict, fullyFixed.Verdict);
        Assert.False(stillFailing.IsFailure);
        Assert.True(fullyFixed.IsFailure);
        Assert.True(partlyFixed.IsFailure);
        Assert.NotEqual(stillFailing.Message, fullyFixed.Message);
        Assert.Contains("UPDATE THE FLOOR", fullyFixed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE THE FLOOR", stillFailing.Message, StringComparison.Ordinal);

        // ...and "reproduced" is not reported as a plain pass either, or a run summary would read as
        // green while the pinned defect is still there.
        Assert.NotEqual(FloorVerdict.Held, stillFailing.Verdict);
        Assert.Contains("NOT a pass", stillFailing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A VERDICT FROM TOO FEW RUNS IS REAL BUT PROVISIONAL. The baseline's rule (a claim resting on one
    /// case flipping needs n &gt;= 15) binds here because every fixture IS one case. A short run still
    /// produces a verdict - suppressing it would be the silent green this file exists to prevent - but
    /// it is flagged, so nobody rewrites the floor off a single repetition.
    /// </summary>
    [Fact]
    public void AVerdictBelowTheSingleCaseN_IsStillGiven_ButIsFlaggedProvisional()
    {
        var entry = ProofreadStandingFloor.ForFixture(ChunkedAgreementFixtures.SeparatedAndDilutedId);

        var oneRun = ProofreadStandingFloor.Evaluate(entry, 1, 1);
        Assert.Equal(FloorVerdict.KnownDefectMoved, oneRun.Verdict);
        Assert.True(oneRun.IsFailure);
        Assert.False(oneRun.MeetsSingleCaseN);
        Assert.Contains("PROVISIONAL", oneRun.Message, StringComparison.Ordinal);

        var fullRun = ProofreadStandingFloor.Evaluate(entry, 0, ProofreadStandingFloor.SingleCaseMinimumRuns);
        Assert.True(fullRun.MeetsSingleCaseN);
        Assert.DoesNotContain("PROVISIONAL", fullRun.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN UNMEASURED FLOOR HAS NO VERDICT. Zero runs is the shape a skipped or aborted live run
    /// produces, and returning "held" for it is precisely how an unmeasured bar greens a gate. A count
    /// outside its own denominator is a scoring bug and is refused for the same reason.
    /// </summary>
    [Fact]
    public void AZeroRunOrOutOfRangeMeasurement_Throws_RatherThanReportingAVerdict()
    {
        var entry = ProofreadStandingFloor.ForFixture(ChunkedAgreementFixtures.DilutionOnlyId);

        Assert.Throws<ArgumentOutOfRangeException>(() => ProofreadStandingFloor.Evaluate(entry, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProofreadStandingFloor.Evaluate(entry, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProofreadStandingFloor.Evaluate(entry, 16, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProofreadStandingFloor.Evaluate(entry, -1, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProofreadStandingFloor.ForFixture("no-such-fixture"));

        // NON-VACUITY: the same call shape with valid arguments does not throw.
        Assert.Equal(FloorVerdict.Held, ProofreadStandingFloor.Evaluate(entry, 15, 15).Verdict);
    }

    /// <summary>
    /// THE SCALAR BARS' DECISION FUNCTION, all three bounds and all three verdicts. The
    /// <see cref="MetricVerdict.ImprovedUpdateTheFloor"/> arm is the metric-side counterpart of
    /// "a known defect started passing": an improvement is not a failure, but it IS reported, because a
    /// bar that silently absorbs slack is a bar the next regression can hide inside.
    /// </summary>
    [Fact]
    public void TheMetricDecisionFunction_CoversBothDirectionsOfEveryBound()
    {
        var tax = ProofreadStandingFloor.Metric("agree-preserve.overCorrectionRate"); // AtMost 0.375
        Assert.Equal(FloorBound.AtMost, tax.Bound);
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(tax, 0.375));
        Assert.Equal(MetricVerdict.Regressed, ProofreadStandingFloor.EvaluateMetric(tax, 0.50));
        Assert.Equal(MetricVerdict.ImprovedUpdateTheFloor, ProofreadStandingFloor.EvaluateMetric(tax, 0.25));

        var recall = ProofreadStandingFloor.Metric("legacy93.recall"); // AtLeast 0.65
        Assert.Equal(FloorBound.AtLeast, recall.Bound);
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(recall, 0.65));
        Assert.Equal(MetricVerdict.Regressed, ProofreadStandingFloor.EvaluateMetric(recall, 0.60));
        Assert.Equal(MetricVerdict.ImprovedUpdateTheFloor, ProofreadStandingFloor.EvaluateMetric(recall, 0.70));

        var tripwire = ProofreadStandingFloor.Metric("chunked.whitespaceOnlySuggestions"); // Exactly 0
        Assert.Equal(FloorBound.Exactly, tripwire.Bound);
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(tripwire, 0));
        Assert.Equal(MetricVerdict.Regressed, ProofreadStandingFloor.EvaluateMetric(tripwire, 4));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProofreadStandingFloor.EvaluateMetric(recall, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProofreadStandingFloor.Metric("no.such.metric"));

        // ── EVERY bar, not just the three spot-checked above ────────────────────────────────────
        //
        // WHAT USED TO BE HERE, AND WHY IT WAS NOT A CHECK. One line evaluated every bar AT ITS OWN
        // VALUE and claimed that caught "a bar authored with the wrong bound direction". It cannot:
        // EvaluateMetric returns Held for observed == Value under ALL THREE bounds (every comparison
        // guard falls through to the shared Held arm), so flipping any bar's Bound to its opposite left
        // it green - measured on all 10 bars during review. It was an assertion true by construction
        // wearing a real check's description.
        //
        // THE TWO ASSERTIONS THAT REPLACE IT catch different failures, and neither subsumes the other:
        //
        //  (1) DIRECTION vs BOUND (the review's option (a)). Taken because it is the ONLY one of the two
        //      that can catch a wrong bound at all: a probe-based check reads each bar's DECLARED bound
        //      and asserts the evaluator agrees with it, so it stays green whichever direction that
        //      bound names. Catching the wrong direction needs a second, independent statement of which
        //      way the metric is good, so ProofreadMetricFloor now carries MetricDirection.
        //      DOES NOT CATCH: a wrong Direction. Two columns authored wrong the same way agree, and
        //      nothing here knows from the world which way recall is good. It compares two authored
        //      claims; it does not verify either against a measurement.
        //
        //  (2) BOUND vs BEHAVIOUR (the review's option (b), kept as well because it is what actually
        //      exercises EvaluateMetric on all 10 bars rather than on the 3 literal cases above).
        //      DOES NOT CATCH: authorial intent. It verifies that each bound BEHAVES the way its enum
        //      says - that is (1)'s job, not this one's.
        AssertEveryBoundEncodesItsMetricsDirection();
        AssertEveryBoundBehavesTheWayItsEnumSays();
    }

    /// <summary>
    /// (1) EVERY BAR'S BOUND ENCODES ITS METRIC'S OWN DIRECTION.
    ///
    /// The permitted encodings, and why <c>Exactly</c> is restricted rather than free:
    ///  - <see cref="MetricDirection.HigherIsBetter"/> -> <see cref="FloorBound.AtLeast"/>. A quality
    ///    figure bounded <c>AtMost</c> would call an improvement a regression.
    ///  - <see cref="MetricDirection.LowerIsBetter"/> -> <see cref="FloorBound.AtMost"/>, or
    ///    <see cref="FloorBound.Exactly"/> ONLY at 0. At the domain floor "at most 0" and "exactly 0"
    ///    describe the same set and <c>Exactly</c> is the louder label, which is why the three tripwires
    ///    use it. An <c>Exactly</c> on a NON-zero tax would report a genuine improvement as
    ///    <see cref="MetricVerdict.Regressed"/> - the same class of authoring error, one bound over.
    /// </summary>
    private static void AssertEveryBoundEncodesItsMetricsDirection()
    {
        var metrics = ProofreadStandingFloor.Metrics;
        Assert.NotEmpty(metrics);

        var defects = new List<string>();
        foreach (var m in metrics)
        {
            var permitted = m.Direction switch
            {
                MetricDirection.HigherIsBetter => m.Bound == FloorBound.AtLeast,
                MetricDirection.LowerIsBetter => m.Bound == FloorBound.AtMost ||
                                                 (m.Bound == FloorBound.Exactly && m.Value == 0),
                _ => false
            };

            if (!permitted)
            {
                var expected = m.Direction == MetricDirection.HigherIsBetter
                    ? "AtLeast"
                    : m.Value == 0 ? "AtMost or Exactly" : "AtMost";
                defects.Add(
                    $"{m.Id} ({m.Metric}, {m.Unit}): declared {m.Direction} but bounded " +
                    $"{m.Bound} at {m.Value} - expected {expected}. The bar's bound contradicts the " +
                    "metric's own direction, so this floor would report a movement the wrong way round " +
                    "(an improvement as a regression, or a regression as slack to absorb). Fix whichever " +
                    "of the two columns is wrong; do NOT make them agree by copying one into the other.");
            }
        }

        Assert.True(defects.Count == 0, string.Join("\n  ", defects));

        // NON-VACUITY: both directions really occur, so neither arm of the rule above is untested.
        Assert.Contains(metrics, m => m.Direction == MetricDirection.HigherIsBetter);
        Assert.Contains(metrics, m => m.Direction == MetricDirection.LowerIsBetter);
    }

    /// <summary>
    /// (2) EVERY BAR'S BOUND BEHAVES THE WAY ITS ENUM SAYS, probed OFF ITS BAND in both directions.
    ///
    /// THE PROBES ARE TAKEN FROM DIFFERENT ENDS, which is the be-c04 correction. A bar declares two
    /// numbers - what it TOLERATES (<c>Value</c>) and what it MEASURED (<c>MeasuredValue</c>) - and the
    /// two verdicts hang off different ends of the band between them:
    ///  - the BAD probe steps past the TOLERATED bar and must be <see cref="MetricVerdict.Regressed"/>;
    ///  - the GOOD probe steps past the MEASURED value and must be
    ///    <see cref="MetricVerdict.ImprovedUpdateTheFloor"/>;
    ///  - BOTH ENDS OF THE BAND are inside it - <see cref="MetricVerdict.Held"/>.
    /// For the nine bars that measure what they tolerate the two ends coincide and this is the old probe
    /// unchanged. For the one BANDED bar, stepping down from the bar lands ON the measured value, which
    /// is the standing state and not a gain; probing it as an improvement is exactly the pre-fired
    /// verdict be-c04 removed, so the probe is taken from the measured end instead of the bar being
    /// special-cased out of the loop.
    ///
    /// <c>Exactly</c> HAS NO GOOD SIDE. A tripwire tolerates precisely what it measured, so BOTH
    /// directions off it are regressions and there is no improvement probe to take.
    ///
    /// PROBES ARE BOUNDED TO OBSERVATIONS THE METRIC CAN ACTUALLY TAKE, which costs several bars a
    /// direction each and it is worth saying which and why:
    ///  - the counts floored at 0 (agree-preserve.overreach, the two chunked tripwires, and the banded
    ///    bar whose MEASURED value is 0) have no downward probe - a negative count is not a measurement;
    ///  - the two recall bars floored at 1.00 have no upward probe - a share above 1 is not one either.
    /// A one-directional probe is weaker on purpose: at its domain floor an <c>Exactly</c> bar and an
    /// <c>AtMost</c> bar are BEHAVIOURALLY IDENTICAL, so this cannot tell them apart. That is exactly the
    /// gap (1) closes by permitting <c>Exactly</c> only at 0.
    /// </summary>
    private static void AssertEveryBoundBehavesTheWayItsEnumSays()
    {
        var metrics = ProofreadStandingFloor.Metrics;
        Assert.NotEmpty(metrics);

        var defects = new List<string>();
        var badProbes = 0;
        var goodProbes = 0;
        var bandedBarsProbed = 0;

        foreach (var m in metrics)
        {
            // Both ends of the declared band are INSIDE it. On an unbanded bar these are one and the
            // same observation, and the claim is only that the boundary is inclusive.
            Check(m, m.Value, MetricVerdict.Held, "the tolerated bar (inclusive)");
            if (m.HasBand)
            {
                bandedBarsProbed++;
                Check(m, m.MeasuredValue, MetricVerdict.Held,
                    "the measured value (the standing state, NOT a gain)");
            }

            // A share lives in [0, 1]; every other bar here is a non-negative count. Both limits are
            // real, and probing past either would assert a verdict for a measurement nobody can report.
            var isShare = m.Unit.StartsWith("share", StringComparison.Ordinal);
            var delta = isShare ? 0.05 : 1.0;

            bool InDomain(double x) => x >= 0.0 && (!isShare || x <= 1.0);

            // PAST THE TOLERATED BAR - a regression under every bound. Exactly has two bad sides.
            var bad = m.Bound switch
            {
                FloorBound.AtMost => new[] { m.Value + delta },
                FloorBound.AtLeast => new[] { m.Value - delta },
                FloorBound.Exactly => new[] { m.Value + delta, m.Value - delta },
                _ => Array.Empty<double>()
            };

            // PAST THE MEASURED VALUE - the only place a gain exists, and only where a good side does.
            var good = m.Bound switch
            {
                FloorBound.AtMost => (double?)(m.MeasuredValue - delta),
                FloorBound.AtLeast => m.MeasuredValue + delta,
                _ => null
            };

            var exercised = false;
            foreach (var probe in bad.Where(InDomain))
            {
                badProbes++;
                exercised = true;
                Check(m, probe, MetricVerdict.Regressed, "past the tolerated bar");
            }

            if (good is { } g && InDomain(g))
            {
                goodProbes++;
                exercised = true;
                Check(m, g, MetricVerdict.ImprovedUpdateTheFloor, "past the measured value");
            }

            if (!exercised)
                defects.Add($"{m.Id}: no probe off this bar is an observation the metric can take, so its " +
                            "bound is never exercised anywhere except INSIDE its band, where all three " +
                            "bounds return Held and nothing is distinguished");
        }

        Assert.True(defects.Count == 0, string.Join("\n  ", defects));

        // NON-VACUITY for the two probe directions: a corpus that could only be probed one way would
        // satisfy every loop above while leaving one half of each bound's behaviour untouched.
        Assert.True(badProbes > 0, "no bar was probed past its tolerated bar");
        Assert.True(goodProbes > 0, "no bar was probed past its measured value");
        Assert.True(bandedBarsProbed > 0,
            "no bar carries a band, so the Held-inside-the-band arm above was never exercised and this " +
            "helper is back to probing one number from both ends");

        void Check(ProofreadMetricFloor m, double observed, MetricVerdict expected, string where)
        {
            var actual = ProofreadStandingFloor.EvaluateMetric(m, observed);
            if (actual != expected)
                defects.Add($"{m.Id} [{m.Bound} {m.Value} {m.Unit}, measured {m.MeasuredValue}]: " +
                            $"{observed} ({where}) evaluated {actual}, expected {expected}");
        }
    }

    /// <summary>
    /// A BANDED BAR HOLDS ACROSS ITS WHOLE BAND, AND ONLY IMPROVES PAST WHAT WAS MEASURED (be-c04).
    ///
    /// THE DEFECT THIS PINS. <c>agree-preserve.agreementBearingOverCorrection</c> is barred at 1 while g1
    /// measured 0. With one number, <c>EvaluateMetric</c> called anything strictly better than the bar
    /// <see cref="MetricVerdict.ImprovedUpdateTheFloor"/> - so a run REPRODUCING the measured 0 reported
    /// "improved, update the floor" on the day the floor was written, and would have reported it forever.
    /// A verdict that fires on the standing state cannot distinguish the standing state from a gain,
    /// which is the whole and only thing it exists to do. It is the metric-side twin of the failure
    /// <see cref="FloorVerdict.KnownDefectMoved"/> was designed to avoid on the fixture side.
    ///
    /// THE IMPROVEMENT ARM IS NOT REACHABLE ON THIS BAR and is proved elsewhere rather than faked: the
    /// unit is a COUNT of case-runs per 40 and the measurement is 0, so no observation better than the
    /// measured value exists in the metric's domain - a negative count is not a measurement. The arm is
    /// therefore exercised on synthetic banded bars whose measured value sits off the domain floor, in
    /// both bound directions.
    /// </summary>
    [Fact]
    public void ABandedBar_HoldsAcrossItsBand_AndOnlyImprovesPastTheMeasuredValue()
    {
        var banded = ProofreadStandingFloor.Metric("agree-preserve.agreementBearingOverCorrection");
        Assert.Equal(FloorBound.AtMost, banded.Bound);
        Assert.True(banded.HasBand, "this bar no longer declares a band, so every case below is vacuous");

        // 0 is WHAT WAS MEASURED - the standing state, not news. This is the assertion that was red
        // before be-c04 (it evaluated ImprovedUpdateTheFloor).
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(banded, 0));
        // 1 is the TOLERATED bar - the far end of the band, inclusive.
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(banded, 1));
        // 2 is past it.
        Assert.Equal(MetricVerdict.Regressed, ProofreadStandingFloor.EvaluateMetric(banded, 2));

        // ── the improvement arm, on bands whose good side is inside the domain ───────────────────
        var syntheticAtMost = SyntheticBar(
            "synthetic.bandedAtMost", MetricDirection.LowerIsBetter, FloorBound.AtMost,
            value: 5, unit: "edits per run", measured: 2);
        Assert.Equal(MetricVerdict.Regressed, ProofreadStandingFloor.EvaluateMetric(syntheticAtMost, 6));
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(syntheticAtMost, 5));
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(syntheticAtMost, 3));
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(syntheticAtMost, 2));
        Assert.Equal(MetricVerdict.ImprovedUpdateTheFloor,
            ProofreadStandingFloor.EvaluateMetric(syntheticAtMost, 1));

        var syntheticAtLeast = SyntheticBar(
            "synthetic.bandedAtLeast", MetricDirection.HigherIsBetter, FloorBound.AtLeast,
            value: 0.60, unit: "share of expected corrections", measured: 0.80);
        Assert.Equal(MetricVerdict.Regressed, ProofreadStandingFloor.EvaluateMetric(syntheticAtLeast, 0.55));
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(syntheticAtLeast, 0.60));
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(syntheticAtLeast, 0.70));
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(syntheticAtLeast, 0.80));
        Assert.Equal(MetricVerdict.ImprovedUpdateTheFloor,
            ProofreadStandingFloor.EvaluateMetric(syntheticAtLeast, 0.85));

        // ── an incoherent band is refused, not silently resolved to one end ──────────────────────
        // (a) a tripwire cannot tolerate worse than it measured;
        var bandedTripwire = SyntheticBar(
            "synthetic.bandedExactly", MetricDirection.LowerIsBetter, FloorBound.Exactly,
            value: 0, unit: "failures per run", measured: 1);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProofreadStandingFloor.EvaluateMetric(bandedTripwire, 0));

        // (b) nor can a band run to the BAD side of the bar - that is a broken bar, not slack.
        var invertedBand = SyntheticBar(
            "synthetic.invertedBand", MetricDirection.LowerIsBetter, FloorBound.AtMost,
            value: 1, unit: "edits per run", measured: 3);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProofreadStandingFloor.EvaluateMetric(invertedBand, 0));

        // NON-VACUITY for both throws: the same shapes with a coherent band do not throw.
        Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(
            SyntheticBar("synthetic.exactly", MetricDirection.LowerIsBetter, FloorBound.Exactly,
                value: 0, unit: "failures per run", measured: 0), 0));
    }

    // ── 6. characterization, recorded but deliberately not gated ─────────────────────────────────

    /// <summary>
    /// THE TWO REAL-WORD-TO-NON-WORD CORRUPTIONS g1 saw recurring, kept as a CHARACTERIZATION so a
    /// later change surfaces movement rather than rediscovering them. No todo owns them and nothing
    /// here asserts the model's behaviour - only that the corpus keeps them OBSERVABLE: the source word
    /// must still occur in a fixture (or the phenomenon can never appear again), and the corrupted form
    /// must NOT occur (or an observation would be the corpus's own text rather than the model's).
    /// </summary>
    [Fact]
    public void TheRecordedCorruptions_StayObservable_InTheFixtureCorpus()
    {
        Assert.NotEmpty(ProofreadStandingFloor.KnownCorruptions);
        var corpus = string.Join("\n", ChunkedAgreementFixtures.All.Select(f => f.Text));
        Assert.False(string.IsNullOrWhiteSpace(corpus), "the fixture corpus is empty");

        var defects = new List<string>();
        foreach (var k in ProofreadStandingFloor.KnownCorruptions)
        {
            if (!corpus.Contains(k.Source, StringComparison.Ordinal))
                defects.Add($"'{k.Source}' no longer occurs in any fixture, so the {k.Source}->{k.Corrupted} " +
                            "corruption can never be observed again and this record is dead");
            if (corpus.Contains(k.Corrupted, StringComparison.Ordinal))
                defects.Add($"'{k.Corrupted}' occurs in the fixture corpus itself, so an occurrence in a " +
                            "result would not be attributable to the model");
            if (string.Equals(k.Source, k.Corrupted, StringComparison.Ordinal))
                defects.Add($"'{k.Source}': source and corrupted form are identical");
        }

        Assert.True(defects.Count == 0, string.Join("\n  ", defects));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static FloorVerdict Verdict(string fixtureId, int hits, int runs) =>
        ProofreadStandingFloor.Evaluate(ProofreadStandingFloor.ForFixture(fixtureId), hits, runs).Verdict;

    /// <summary>
    /// A bar that is NOT on the standing floor, used only to exercise decision-function arms the real
    /// floor's own domains cannot reach (see <c>ABandedBar_HoldsAcrossItsBand_*</c>). It is deliberately
    /// never added to <c>Metrics</c>: inventing a floor row to make a test reachable would put a number
    /// nobody measured into the artifact this whole file exists to keep honest.
    /// </summary>
    private static ProofreadMetricFloor SyntheticBar(
        string id, MetricDirection direction, FloorBound bound, double value, string unit, double measured) =>
        new(id, GoldPromptSurface.ProductionLongPlusShort, "(synthetic - not a standing bar)",
            "a test-only metric", direction, bound, value, unit,
            "SYNTHETIC. Exists only inside ProofreadStandingFloorTests to probe an EvaluateMetric arm the " +
            "real floor's domains cannot reach.")
        {
            MeasuredValue = measured
        };

    /// <summary>
    /// A model-free stand-in for one scored case: the SURFACE is real, every metric is zero. The
    /// partition is what is under test here, not the arithmetic (that is
    /// <c>ProofreadAgreementGoldTests.PerSurfaceAggregation_*</c>'s job), so zeroing the metrics keeps
    /// this test from passing or failing for a reason that has nothing to do with bucketing.
    /// </summary>
    private static GoldCaseScore SurfaceOnlyRecord(string id, GoldPromptSurface surface) =>
        new(id, surface, Expected: 0, Produced: 0, Matched: 0,
            NoChangeCase: false, NoChangeWithCorrection: false, Errored: false,
            InputTokens: 0, OutputTokens: 0,
            OverreachEdits: 0, DeclaresForbidden: false, OverreachHit: false);
}
