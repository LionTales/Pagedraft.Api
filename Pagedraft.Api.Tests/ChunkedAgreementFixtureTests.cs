using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and the whole point of this file's location is to be outside it.
// Same rule (and same reason) as ProofreadAgreementGoldTests.
using ProofreadQualityTests = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;
using GoldPromptSurfaces = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurfaces;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC, NO-MODEL, NO-GPU gate on the chunked-agreement FIXTURES (c1). These are the
/// assertions that must hold BEFORE g1 spends its single Ollama session, because every one of them
/// describes a way a fixture can silently measure nothing:
///
///  1. THE WINDOWED-FIXTURE DENSITY TRAP. A synthetic chapter that does not actually realize the
///     chunk COUNT it claims collapses the whole experiment while every test stays green. The counts
///     here are asserted MODEL-FREE by driving the real chunker
///     (<c>UnifiedAnalysisService.ChunkForProofreadForTest</c>) at the real, language-keyed target.
///  2. THE LANGUAGE-KEYED THRESHOLD. The target is ~250 for Hebrew and 500 for Latin. Sizing these
///     fixtures against 500 would put every one of them in ONE chunk.
///  3. PLACEMENT, not just count. "Three chunks" says nothing; what the experiment rests on is WHICH
///     chunk holds the error, WHICH holds the name, and whether the error chunk's overlap carries it.
///  4. CONFOUND HYGIENE. A ktiv-male suggestion or a gershayim in the prose would land in g1's
///     over-correction column and be misread as model overreach.
///  5. SURFACE PARTITION. These cases ride a THIRD prompt surface; they must not be mixed into the
///     gold corpus's two-way split.
///
/// Every "nothing offends" assertion below first proves its population is non-empty - the vacuity
/// class that has bitten this corpus four times.
/// </summary>
public class ChunkedAgreementFixtureTests
{
    private static IReadOnlyList<ChunkedAgreementFixture> All => ChunkedAgreementFixtures.All;

    // ── 1. the language-keyed threshold the fixtures are sized against ───────────────────────────

    /// <summary>
    /// The fixtures are keyed off the HEBREW target, and the Hebrew target is roughly half the Latin
    /// one. This is not a restatement of the sizer: it is the premise the fixture LENGTHS were chosen
    /// under, so if the language keying were ever removed (or the ceiling raised) every multi-chunk
    /// fixture here would silently collapse to a single chunk and the experiment would measure the
    /// single-shot regime while claiming to measure the chunked one.
    /// </summary>
    [Fact]
    public void TheChunkTarget_IsLanguageKeyed_AndTheFixturesAreSizedAgainstTheHebrewOne()
    {
        var opts = ChunkedAgreementHarness.HarnessOptions();
        var hebrew = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, ChunkedAgreementFixtures.Language);
        var latin = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, "en-US");

        Assert.Equal(250, hebrew);
        Assert.Equal(500, latin);
        Assert.True(hebrew < latin,
            $"the Hebrew per-chunk target ({hebrew}) must stay below the Latin one ({latin}); these " +
            "fixtures are authored to cross the Hebrew threshold, not the Latin one");

        // The harness must not be quietly lowering the ceiling the way the pre-existing chunked tests
        // do to force chunking on a short input. It keeps the shipped 500-word LATIN ceiling.
        Assert.Equal(AiOptions.DefaultProofreadChunkTargetWords, opts.EffectiveProofreadChunkTargetWords);
    }

    // ── 2. realized chunk counts, model-free ─────────────────────────────────────────────────────

    /// <summary>
    /// THE DENSITY-TRAP GATE. Drives the REAL chunker over every fixture and fails if a multi-chunk
    /// fixture collapses to one, or if any fixture realizes a count other than the one it declares.
    /// </summary>
    [Fact]
    public void EveryFixture_RealizesExactlyTheChunkCountItDeclares()
    {
        // Non-vacuity: the corpus is a population with the four arms the experiment needs.
        Assert.Equal(4, All.Count);
        Assert.Equal(3, ChunkedAgreementFixtures.MultiChunk.Count);
        Assert.Equal(4, All.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count());

        var offenders = new List<string>();
        foreach (var f in All)
        {
            var target = ChunkedAgreementHarness.ChunkTargetWordsFor(f);
            var chunks = ChunkedAgreementHarness.Chunk(f);
            if (chunks.Count != f.ExpectedChunkCount)
                offenders.Add($"{f.Id}: declares {f.ExpectedChunkCount} chunk(s) but the real chunker " +
                              $"produced {chunks.Count} at target {target} " +
                              $"({ChunkedAgreementHarness.WordCount(ChunkedAgreementHarness.ProductionTargetText(f))} words)");
        }

        Assert.True(offenders.Count == 0,
            "A fixture does not realize the chunk count it is designed around, so it measures a " +
            "different regime than the one it claims (the windowed-fixture density trap):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The stronger half of the same gate, stated separately so it cannot be satisfied by a fixture
    /// whose DECLARED count is 1: every fixture on the CHUNKED surface must genuinely produce more
    /// than one chunk, and must exceed the routing threshold so <c>RunAsync</c> takes the chunked
    /// branch at all.
    /// </summary>
    [Fact]
    public void EveryChunkedSurfaceFixture_ExceedsTheRoutingThreshold_AndProducesMoreThanOneChunk()
    {
        var chunked = ChunkedAgreementFixtures.MultiChunk;
        Assert.NotEmpty(chunked);

        foreach (var f in chunked)
        {
            var target = ChunkedAgreementHarness.ChunkTargetWordsFor(f);
            // RunAsync's own branch condition (UnifiedAnalysisService.cs:401), restated on the fixture.
            // Production evaluates WordCount(context.TargetText), and context.TargetText is always the
            // STRIPPED text (SyncfusionWatermarkStripper.StripSyncfusionWatermark runs first on every
            // real target-resolution path) - so this must count words on ProductionTargetText, the same
            // string the product actually branches on, not the raw fixture text.
            var words = ChunkedAgreementHarness.WordCount(ChunkedAgreementHarness.ProductionTargetText(f));

            Assert.True(words > target,
                $"{f.Id}: {words} words does not exceed the {target}-word target, so RunAsync would " +
                "route it to the SINGLE-SHOT path and this fixture would never reach the chunked one");

            Assert.True(ChunkedAgreementHarness.Chunk(f).Count > 1,
                $"{f.Id}: collapsed to a single chunk");
        }
    }

    // ── 3. placement: which chunk holds what ─────────────────────────────────────────────────────

    /// <summary>
    /// The experiment's real content. For each fixture: the error span occurs EXACTLY ONCE in the
    /// text, it lands in the declared chunk, the character name lands in exactly the declared chunks,
    /// and the error chunk's overlap prefix carries the name if and only if the fixture says so. A
    /// count-only assertion would pass with the error in the wrong chunk, which is precisely the
    /// difference between fixture 01 and fixture 03.
    /// </summary>
    [Fact]
    public void EveryFixture_PlacesItsErrorSpanAndItsAntecedentInTheDeclaredChunks()
    {
        var offenders = new List<string>();
        var placementsChecked = 0;

        foreach (var f in All)
        {
            var chunks = ChunkedAgreementHarness.Chunk(f);
            placementsChecked++;

            var occurrences = Occurrences(f.Text, f.ErrorSpan);
            if (occurrences != 1)
                offenders.Add($"{f.Id}: the error span [{f.ErrorSpan}] occurs {occurrences} time(s) in the " +
                              "text; the whole matrix keys on a single, unambiguous erroneous span");

            var errorChunks = ChunkIndexesContaining(chunks, f.ErrorSpan);
            if (!errorChunks.SequenceEqual(new[] { f.ExpectedErrorChunkIndex }))
                offenders.Add($"{f.Id}: the error span sits in chunk(s) [{string.Join(",", errorChunks)}] " +
                              $"but the fixture declares chunk {f.ExpectedErrorChunkIndex}");

            var nameChunks = ChunkIndexesContaining(chunks, f.CharacterName);
            if (!nameChunks.SequenceEqual(f.ExpectedNameChunkIndexes))
                offenders.Add($"{f.Id}: the character name sits in chunk(s) [{string.Join(",", nameChunks)}] " +
                              $"but the fixture declares [{string.Join(",", f.ExpectedNameChunkIndexes)}]");

            var overlap = chunks[f.ExpectedErrorChunkIndex].OverlapPrefix;
            var overlapCarriesName = overlap is not null &&
                                     overlap.Contains(f.CharacterName, StringComparison.Ordinal);
            if (overlapCarriesName != f.NameExpectedInErrorChunkOverlap)
                offenders.Add($"{f.Id}: the error chunk's overlap prefix " +
                              $"{(overlapCarriesName ? "DOES" : "does NOT")} carry the character name, but " +
                              $"the fixture declares {f.NameExpectedInErrorChunkOverlap}. This is the " +
                              "separation axis; getting it backwards inverts the attribution.");
        }

        Assert.True(placementsChecked > 0, "no fixture placement was checked");
        Assert.True(offenders.Count == 0,
            "A fixture's seeded sentences did not land where its hypothesis requires:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// THE SEPARATION A/B, stated as its own claim. 01 and 02 must be identical in every respect the
    /// model can see EXCEPT whether the antecedent reaches the error chunk. If they ever differ in
    /// span, fix, near-miss or register, "01 failed and 02 passed" stops meaning "separation".
    /// </summary>
    [Fact]
    public void TheSeparationPair_IsIdenticalExceptForWhereTheAntecedentSits()
    {
        var separated = ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.SeparatedAndDilutedId);
        var inOverlap = ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.AntecedentInOverlapId);

        Assert.Equal(separated.ErrorSpan, inOverlap.ErrorSpan);
        Assert.Equal(separated.ExpectedFix, inOverlap.ExpectedFix);
        Assert.Equal(separated.NearMissForbidden, inOverlap.NearMissForbidden);
        Assert.Equal(separated.CharacterName, inOverlap.CharacterName);
        Assert.Equal(
            separated.Register.Select(r => $"{r.Name}|{r.Gender}"),
            inOverlap.Register.Select(r => $"{r.Name}|{r.Gender}"));

        // The one thing that differs.
        Assert.False(separated.NameExpectedInErrorChunkOverlap);
        Assert.True(inOverlap.NameExpectedInErrorChunkOverlap);

        // ...and in BOTH the error chunk's own text lacks the name, so the overlap really is the only
        // channel that can carry it. Without this the pair would differ in two things at once.
        foreach (var f in new[] { separated, inOverlap })
        {
            var errorChunk = ChunkedAgreementHarness.Chunk(f)[f.ExpectedErrorChunkIndex];
            Assert.DoesNotContain(f.CharacterName, errorChunk.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE DILUTION A/B. 01 and 03 are built from the SAME body and the SAME two seeded sentences;
    /// only the position of the erroneous sentence moves. Proved by stripping both seeded sentences
    /// out of each text and asserting the remainders are byte-identical, so a future edit to one
    /// fixture's prose cannot silently make the pair non-comparable.
    /// </summary>
    [Fact]
    public void TheDilutionPair_SharesOneBody_AndMovesOnlyTheErroneousSentence()
    {
        var separated = ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.SeparatedAndDilutedId);
        var dilutionOnly = ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.DilutionOnlyId);

        Assert.Equal(StripSeeds(separated.Text), StripSeeds(dilutionOnly.Text));
        Assert.Equal(separated.ExpectedChunkCount, dilutionOnly.ExpectedChunkCount);

        // The positions really are different, otherwise the pair is one fixture written twice.
        Assert.NotEqual(separated.ExpectedErrorChunkIndex, dilutionOnly.ExpectedErrorChunkIndex);
        Assert.Equal(0, dilutionOnly.ExpectedErrorChunkIndex);
        Assert.Equal(separated.ExpectedChunkCount - 1, separated.ExpectedErrorChunkIndex);
    }

    /// <summary>
    /// The fixture text with both seeded sentences removed and every whitespace run flattened. The
    /// flattening is what makes the comparison meaningful: the two fixtures attach the erroneous
    /// sentence at DIFFERENT paragraph positions, so removing it leaves the surrounding whitespace in
    /// different shapes even when the prose is identical. Paragraph STRUCTURE is not what this test
    /// claims - that is pinned by the realized-chunk-count tests, on both fixtures, separately.
    /// </summary>
    private static string StripSeeds(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text.Replace(ChunkedAgreementFixtures.AntecedentSentence, "", StringComparison.Ordinal)
                .Replace(ChunkedAgreementFixtures.ErroneousSentence, "", StringComparison.Ordinal),
            @"\s+", " ").Trim();

    // ── 4. the control, and why a ONE-CHUNK CHUNKED run cannot be reached publicly ────────────────

    /// <summary>
    /// The control stays under the routing threshold, so <c>RunAsync</c> sends it single-shot. That is
    /// deliberate and is documented on the fixture; see the sibling test below for why the alternative
    /// (a one-chunk run of the CHUNKED path) is not reachable through the public entry point at all.
    /// </summary>
    [Fact]
    public void TheControl_StaysUnderTheRoutingThreshold_AndIsOneChunk()
    {
        var control = ChunkedAgreementFixtures.Control;
        var target = ChunkedAgreementHarness.ChunkTargetWordsFor(control);
        var words = ChunkedAgreementHarness.WordCount(ChunkedAgreementHarness.ProductionTargetText(control));

        Assert.True(words <= target,
            $"the control has {words} words against a {target}-word target; above it RunAsync would " +
            "chunk it and it would stop being the single-shot baseline condition");
        Assert.Single(ChunkedAgreementHarness.Chunk(control));
        Assert.Equal(GoldPromptSurface.ProductionLongPlusShort, control.Surface);
    }

    /// <summary>
    /// A PREMISE THE PLAN DID NOT STATE, pinned here because it decides what "a single-chunk control"
    /// can even mean.
    ///
    /// <c>RunAsync</c> enters the chunked path only when <c>WordCount(input) &gt; chunkTargetWords</c>
    /// (UnifiedAnalysisService.cs:401) and then chunks at the SAME target. For non-dialogue prose the
    /// grouping threshold in <c>BuildChunkSegmentsCore</c> is exactly that target, so any input long
    /// enough to be routed to the chunked path is by construction split into at least two chunks.
    /// A one-chunk CHUNKED run is therefore unreachable through the public entry point for ordinary
    /// narrative text. (The one exception is the dialogue-overflow branch, which raises the grouping
    /// threshold to 1.3x the target for a segment run that is entirely dialogue - so a 251-325 word
    /// all-dialogue passage could produce one chunk. The control deliberately does NOT use that
    /// shape: it would confound the control with a dialogue/narration difference the other three
    /// fixtures do not have.)
    ///
    /// The consequence for g1: the control rides the SINGLE-SHOT path, and the claim that makes it a
    /// valid control is that its composed instruction is byte-identical to a first chunk's - which is
    /// pinned by <c>ChunkedAgreementHarnessTests</c>, not assumed here.
    /// </summary>
    [Fact]
    public void AOneChunkChunkedRun_IsUnreachableThroughThePublicEntryPoint_ForNonDialogueProse()
    {
        var target = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(
            ChunkedAgreementHarness.HarnessOptions(), ChunkedAgreementFixtures.Language);

        var routable = 0;
        var offenders = new List<string>();

        // Every paragraph prefix of the corpus body. The first one to cross the target is the SHORTEST
        // input of this shape RunAsync would route to the chunked path.
        for (var n = 1; n <= ChunkedAgreementFixtures.Filler.Count; n++)
        {
            var prefix = string.Join(
                ChunkedAgreementFixtures.ParagraphSeparator,
                ChunkedAgreementFixtures.Filler.Take(n));

            if (ChunkedAgreementHarness.WordCount(prefix) <= target)
                continue; // RunAsync would send this single-shot, not chunked

            routable++;
            var chunks = UnifiedAnalysisService.ChunkForProofreadForTest(prefix, target);
            if (chunks.Count < 2)
                offenders.Add($"a {ChunkedAgreementHarness.WordCount(prefix)}-word non-dialogue body " +
                              $"({n} paragraphs) routes to the chunked path but produced {chunks.Count} chunk(s)");
        }

        // Non-vacuity: if NO prefix crossed the target, the loop asserted nothing at all.
        Assert.True(routable > 0,
            "no paragraph prefix of the corpus exceeded the chunk target, so this test examined no " +
            "chunk-routable input and proves nothing about reachability");
        Assert.True(offenders.Count == 0,
            "A non-dialogue input long enough to be ROUTED to the chunked path produced a single " +
            "chunk, which would make a one-chunk chunked control reachable after all and would mean " +
            "the control's documented rationale is stale:\n  " + string.Join("\n  ", offenders));
    }

    // ── 5. span hygiene (the near-miss rules the gold class already enforces) ─────────────────────

    /// <summary>
    /// The NECESSARY conditions for the fixture's error span / expected fix / near-miss to measure
    /// anything, mirrored from <c>ProofreadAgreementGoldTests</c>:
    ///  - the error span is a WORD-ALIGNED occurrence of its own text (not an infix of a longer word);
    ///  - the expected fix differs from the error span and does not ALREADY occur in the text (it
    ///    would then be indistinguishable from prose the model never touched);
    ///  - the near-miss differs from BOTH the error span and the expected fix, and is written in
    ///    Hebrew with no Latin letters (the placeholder shape).
    /// </summary>
    [Fact]
    public void EveryFixture_DeclaresAWordAlignedErrorSpan_AndAPlausibleNearMiss()
    {
        var defects = new List<string>();
        var spansChecked = 0;

        foreach (var f in All)
        {
            spansChecked++;

            var at = f.Text.IndexOf(f.ErrorSpan, StringComparison.Ordinal);
            if (at < 0)
            {
                defects.Add($"{f.Id}: the error span [{f.ErrorSpan}] does not occur in the text at all");
                continue;
            }

            var before = at == 0 ? ' ' : f.Text[at - 1];
            var afterIndex = at + f.ErrorSpan.Length;
            var after = afterIndex >= f.Text.Length ? ' ' : f.Text[afterIndex];
            if (char.IsLetterOrDigit(before) || char.IsLetterOrDigit(after))
                defects.Add($"{f.Id}: the error span [{f.ErrorSpan}] is an INFIX of a longer word " +
                            "(a legitimate correction elsewhere would align with it)");

            if (string.Equals(f.ExpectedFix, f.ErrorSpan, StringComparison.Ordinal))
                defects.Add($"{f.Id}: the expected fix equals the error span, i.e. it forbids a no-op");
            if (f.Text.Contains(f.ExpectedFix, StringComparison.Ordinal))
                defects.Add($"{f.Id}: the expected fix [{f.ExpectedFix}] ALREADY occurs in the input, so " +
                            "a scorer cannot tell a real repair from untouched prose");

            if (string.Equals(f.NearMissForbidden, f.ErrorSpan, StringComparison.Ordinal))
                defects.Add($"{f.Id}: the near-miss equals the error span (forbids a no-op)");
            if (string.Equals(f.NearMissForbidden, f.ExpectedFix, StringComparison.Ordinal))
                defects.Add($"{f.Id}: the near-miss names the CORRECT fix, so the right answer would " +
                            "score as overreach");
            if (!f.NearMissForbidden.Any(IsHebrewLetter))
                defects.Add($"{f.Id}: the near-miss [{f.NearMissForbidden}] contains no Hebrew letter");
            if (f.NearMissForbidden.Any(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                defects.Add($"{f.Id}: the near-miss [{f.NearMissForbidden}] mixes Latin letters in, which " +
                            "is the shape of a placeholder rather than a form the model would emit");
        }

        Assert.True(spansChecked > 0, "no fixture span was checked");
        Assert.True(defects.Count == 0,
            "A fixture's agreement span fails a NECESSARY condition for measuring anything:\n  " +
            string.Join("\n  ", defects));
    }

    private static bool IsHebrewLetter(char ch) => ch is >= 'א' and <= 'ת';

    // ── 6. confound hygiene ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The filler must stay IMPERSONAL: it may not contain the character name, the error span, or the
    /// expected fix. If a filler paragraph named the character, the "name appears only in chunk 0"
    /// property of fixture 01 would quietly become false wherever that paragraph landed.
    /// </summary>
    [Fact]
    public void TheFiller_NamesNoCharacter_AndCarriesNeitherTheErrorSpanNorItsFix()
    {
        Assert.NotEmpty(ChunkedAgreementFixtures.Filler);

        var offenders = ChunkedAgreementFixtures.Filler
            .Select((p, i) => (Paragraph: p, Index: i))
            .Where(x => x.Paragraph.Contains(ChunkedAgreementFixtures.CharacterName, StringComparison.Ordinal) ||
                        x.Paragraph.Contains(ChunkedAgreementFixtures.ErrorSpan, StringComparison.Ordinal) ||
                        x.Paragraph.Contains(ChunkedAgreementFixtures.ExpectedFix, StringComparison.Ordinal))
            .Select(x => $"filler[{x.Index}]")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A filler paragraph carries the character name or an agreement span, so the fixtures' " +
            "placement guarantees are no longer true wherever that paragraph lands: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// NO QUOTE CHARACTER of any kind in any fixture. The other half of this plan measures a
    /// punctuation tax whose largest component is the Hebrew gershayim (U+05F4) being rewritten to an
    /// ASCII quote. If a fixture here carried one, that rewrite would show up in THIS item's
    /// over-correction count and the two verdicts - which the plan deliberately keeps separate -
    /// would contaminate each other.
    /// </summary>
    [Fact]
    public void NoFixture_CarriesAQuoteCharacter_SoThePunctuationItemCannotContaminateThisOne()
    {
        char[] quotes = { '״', '׳', '"', '“', '”', '\'', '‘', '’' };

        var offenders = All
            .Where(f => f.Text.IndexOfAny(quotes) >= 0)
            .Select(f => $"{f.Id} (first at index {f.Text.IndexOfAny(quotes)})")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A fixture carries a quote character, so the gershayim/ASCII-quote phenomenon the OTHER " +
            "half of this plan measures could be counted as an agreement over-correction here: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// The DETERMINISTIC ktiv-male checker must find nothing in any fixture. It runs with production
    /// defaults on every Hebrew proofread result, so a haser spelling anywhere in the filler would add
    /// a suggestion that is not the model's and would inflate g1's over-correction column.
    ///
    /// NON-VACUITY: the checker is proved LIVE first, on a known haser word, because
    /// <c>FindSuggestions</c> returns an empty list for a non-Hebrew language or a disabled toggle -
    /// i.e. "no offenders" is exactly what a mis-wired checker also returns.
    /// </summary>
    [Fact]
    public void NoFixture_TripsTheDeterministicKtivMaleChecker()
    {
        var checker = new KtivMaleChecker(new HebrewStyleOptions());

        // The checker is on and does fire (otherwise everything below is free).
        var probe = checker.FindSuggestions("הוא קרא את העתון בבוקר.", ChunkedAgreementFixtures.Language);
        Assert.True(probe.Count > 0,
            "the ktiv-male checker produced nothing on a known haser word (עתון), so it is not " +
            "actually running and the per-fixture assertions below would pass for free");

        var offenders = new List<string>();
        foreach (var f in All)
        {
            var found = checker.FindSuggestions(f.Text, f.Language);
            if (found.Count > 0)
                offenders.Add($"{f.Id}: {found.Count} ktiv-male suggestion(s), e.g. " +
                              $"[{found[0].OriginalText} -> {found[0].SuggestedText}]");
        }

        Assert.True(offenders.Count == 0,
            "A fixture's prose trips the deterministic ktiv-male checker. Those suggestions are " +
            "appended to the proofread result and would be scored as model over-correction:\n  " +
            string.Join("\n  ", offenders));
    }

    // ── 7. the surface partition ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// SURFACE TAGGING. Every fixture declares which prompt surface it is measured on, the three
    /// chunked ones really are on the chunked surface, and NONE of these ids exists in
    /// proofread-gold.json - so the two corpora cannot be conflated by an id-keyed report even though
    /// they now share ONE surface vocabulary (c3 widened <c>GoldPromptSurface</c> to three buckets and
    /// retired the duplicate enum c1 had used to keep them apart).
    ///
    /// NON-VACUITY: the gold file is asserted to have loaded, because <c>LoadProofreadGold</c> returns
    /// an EMPTY array rather than throwing when the JSON is missing from the output directory - and an
    /// empty gold satisfies "no id collides" for free.
    /// </summary>
    [Fact]
    public void EveryFixture_DeclaresItsSurface_AndNoFixtureIdIsAlreadyInTheGoldCorpus()
    {
        var gold = ProofreadQualityTests.LoadProofreadGold();
        Assert.NotEmpty(gold);

        var goldIds = gold.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var collisions = All.Where(f => goldIds.Contains(f.Id)).Select(f => f.Id).ToList();
        Assert.True(collisions.Count == 0,
            "A chunked fixture id already exists in proofread-gold.json, so the two corpora would be " +
            "conflated by any id-keyed report: " + string.Join(", ", collisions));

        // NON-VACUITY: MultiChunk is selected by Surface (see its own summary), so this assertion must
        // check a property the selector does NOT already guarantee - that every member really does
        // realize more than one chunk. A tautological Surface re-check would pass even if a fixture's
        // text stopped requiring more than one chunk.
        Assert.NotEmpty(ChunkedAgreementFixtures.MultiChunk);
        Assert.All(ChunkedAgreementFixtures.MultiChunk,
            f => Assert.True(f.ExpectedChunkCount > 1,
                $"Fixture '{f.Id}' is on the chunked-per-chunk surface but declares " +
                $"ExpectedChunkCount={f.ExpectedChunkCount}, so it does not realize more than one chunk."));
        Assert.Equal(
            GoldPromptSurface.ProductionLongPlusShort,
            ChunkedAgreementFixtures.Control.Surface);

        // A partition over the WHOLE surface vocabulary, not over the two values this corpus happens to
        // use today: every fixture lands on exactly one DECLARED surface. Counting only the two known
        // values would keep passing if a fixture were tagged with something else entirely.
        var perSurface = GoldPromptSurfaces.AllSurfaces.Sum(s => All.Count(f => f.Surface == s));
        Assert.Equal(All.Count, perSurface);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var from = 0;
        while (true)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0) return count;
            count++;
            from = at + 1;
        }
    }

    private static int[] ChunkIndexesContaining(
        IReadOnlyList<(string Text, string SeparatorAfter, string? OverlapPrefix)> chunks, string needle) =>
        chunks
            .Select((c, i) => (Chunk: c, Index: i))
            .Where(x => x.Chunk.Text.Contains(needle, StringComparison.Ordinal))
            .Select(x => x.Index)
            .ToArray();
}
