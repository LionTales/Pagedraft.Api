using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and the whole point of this file's location is to be outside it.
using ProofreadQualityTests = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;
using GoldPromptSurface = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurface;
using GoldPromptSurfaces = Pagedraft.Api.Tests.LanguageEngine.GoldPromptSurfaces;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC, NO-MODEL, NO-GPU gate on the real-prose precision surface. Every assertion here
/// describes a way this surface can silently measure NOTHING while every test stays green:
///
///  1. THE DENSITY TRAP. A passage that does not realize the chunk count it claims measures a
///     different regime than the one it names. Asserted model-free against the REAL chunker.
///  2. THE OVERLAP. The arm under test extends the <c>[CONTEXT_BEFORE]</c> line, so a surface whose
///     chunks carried no overlap would be measuring the arm's inert case. Every passage's chunk 1 must
///     carry a real overlap.
///  3. THE MONOCULTURE. The synthetic measurement this surface replaces was carried by four instances
///     of ONE construction. The composition is therefore asserted for SPREAD, not merely reported.
///  4. CONFOUND HYGIENE. A deterministic ktiv-male suggestion, an NBSP whitespace-only edit, a digit or
///     a Latin letter would each add edits that are not the model's.
///  5. THE SEEDS. A transplant that is not present, not unique, or not where it says it is turns the
///     recall guard into a denominator nobody can read.
///
/// Every "no offenders" assertion proves its population is NON-EMPTY first. That is not ceremony: this
/// corpus has been burned by an empty gold loader and by a sweep whose pattern matched everything.
/// </summary>
public class RealProsePrecisionFixtureTests
{
    private static IReadOnlyList<RealProsePassage> All => RealProsePrecisionFixtures.All;

    // ── 0. the corpus exists and is wired ────────────────────────────────────────────────────────

    /// <summary>
    /// THE POPULATION every other test in this file quantifies over. Stated first and on its own, so a
    /// corpus that silently shrank to one passage (or to none) fails HERE rather than making every
    /// "no offenders" assertion below pass for free.
    /// </summary>
    [Fact]
    public void TheCorpus_HasTwelvePassages_FourOfThemSeeded_AndEveryAuthoredPassageIsWiredIn()
    {
        Assert.Equal(12, All.Count);
        Assert.Equal(12, All.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12, RealProsePrecisionFixtures.PrecisionAxis.Count);

        Assert.Equal(4, RealProsePrecisionFixtures.Seeded.Count);
        Assert.Equal(8, RealProsePrecisionFixtures.Seeded.Sum(p => p.Seeds.Count));

        // 12 clean units + 4 seeded units. This is the row set a session drives, and it is the number
        // any run-cost estimate has to be built on.
        Assert.Equal(16, RealProsePrecisionFixtures.RunUnits.Count);

        // Every authored paragraph array is actually used by a passage. An orphaned array is prose that
        // looks like corpus and is measured by nothing.
        Assert.Equal(RealProsePassages.All.Count, All.Count);
        foreach (var authored in RealProsePassages.All)
            Assert.Contains(All, p => ReferenceEquals(p.Paragraphs, authored));

        // Every passage has prose in it, and enough of it to be worth a model call.
        Assert.All(All, p => Assert.True(p.Paragraphs.Count >= 5,
            $"{p.Id}: only {p.Paragraphs.Count} paragraph(s)"));
        Assert.All(All, p => Assert.All(p.Paragraphs,
            para => Assert.False(string.IsNullOrWhiteSpace(para), $"{p.Id} has a blank paragraph")));
    }

    // ── 1. realized chunk counts, model-free ─────────────────────────────────────────────────────

    /// <summary>
    /// THE DENSITY-TRAP GATE. Drives the REAL chunker over every passage (both variants) at the real,
    /// language-keyed target and fails if any of them realizes a count other than the one it declares.
    /// The Hebrew target is asserted here too, because sizing these passages against the LATIN 500
    /// would collapse every one of them to a single chunk and the surface would measure the
    /// single-shot regime while claiming to measure the chunked one.
    /// </summary>
    [Fact]
    public void EveryPassage_RealizesExactlyTheChunkCountItDeclares_OnBothVariants()
    {
        var opts = RealProseHarness.HarnessOptions();
        Assert.Equal(250, UnifiedAnalysisService.ProofreadChunkTargetWordsFor(
            opts, RealProsePrecisionFixtures.Language));
        Assert.Equal(500, UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, "en-US"));
        // The harness must not be quietly lowering the shipped ceiling to force chunking.
        Assert.Equal(AiOptions.DefaultProofreadChunkTargetWords, opts.EffectiveProofreadChunkTargetWords);

        var target = RealProseHarness.ChunkTargetWords();
        var offenders = new List<string>();
        var checked_ = 0;

        foreach (var p in All)
        foreach (var v in p.Variants)
        {
            checked_++;
            var chunks = RealProseHarness.Chunk(p, v);
            var words = RealProsePrecisionFixtures.WordCount(RealProseHarness.ProductionTargetText(p, v));

            if (chunks.Count != p.ExpectedChunkCount)
                offenders.Add($"{p.Id}/{v}: declares {p.ExpectedChunkCount} chunk(s) but the real chunker " +
                              $"produced {chunks.Count} at target {target} ({words} words)");

            // RunAsync's own branch condition (UnifiedAnalysisService.cs:401), restated on the passage:
            // at or below the target the input goes out SINGLE-SHOT and never reaches the per-chunk
            // builder the arm lives in.
            if (words <= target)
                offenders.Add($"{p.Id}/{v}: {words} words does not exceed the {target}-word target, so " +
                              "RunAsync would route it single-shot and the arm could never render");
        }

        Assert.True(checked_ == 16, $"expected 16 passage-variant units, examined {checked_}");
        Assert.True(offenders.Count == 0,
            "A passage does not realize the chunk shape it is designed around, so it measures a " +
            "different regime than the one it claims (the density trap):\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// THE OVERLAP GATE, and it is the reason this surface exists rather than the gold corpus. The arm
    /// under test extends the <c>[CONTEXT_BEFORE]</c> instruction line; a chunk with no overlap prefix
    /// renders no such section. Chunk 0 never has one (the chunker only builds one for i > 0), so every
    /// passage must produce at least a chunk 1 AND that chunk must carry a non-empty overlap.
    /// </summary>
    [Fact]
    public void EveryPassage_ProducesARealContextBeforeOverlap_OnEveryChunkAfterTheFirst()
    {
        var offenders = new List<string>();
        var overlapsChecked = 0;

        foreach (var p in All)
        foreach (var v in p.Variants)
        {
            var chunks = RealProseHarness.Chunk(p, v);
            if (chunks.Count < 2)
            {
                offenders.Add($"{p.Id}/{v}: {chunks.Count} chunk(s), so no chunk carries an overlap");
                continue;
            }

            Assert.Null(chunks[0].OverlapPrefix); // the chunker's own contract, restated

            for (var i = 1; i < chunks.Count; i++)
            {
                overlapsChecked++;
                var overlap = chunks[i].OverlapPrefix;
                if (string.IsNullOrWhiteSpace(overlap))
                    offenders.Add($"{p.Id}/{v}: chunk {i} carries no [CONTEXT_BEFORE] overlap");
                else if (overlap.Length < 40)
                    offenders.Add($"{p.Id}/{v}: chunk {i}'s overlap is only {overlap.Length} chars, which " +
                                  "is too little context for a referent-resolution licence to act on");
            }
        }

        Assert.True(overlapsChecked >= 16,
            $"only {overlapsChecked} overlap(s) were examined; the surface is supposed to carry one per " +
            "passage-variant, so a smaller number means most of it was never checked");
        Assert.True(offenders.Count == 0,
            "A passage produces no usable [CONTEXT_BEFORE] overlap, so the arm this surface was built " +
            "to measure would render its licence over nothing:\n  " + string.Join("\n  ", offenders));
    }

    // ── 2. composition: the monoculture guard caveat 1 demands ───────────────────────────────────

    /// <summary>
    /// THE MONOCULTURE GUARD. On the synthetic fixtures this surface replaces, FOUR instances of ONE
    /// construction (<c>מן ה...</c>) carried 62% of all over-corrections and 93% of the gross drop. A
    /// passage set that repeats that shape produces a number that reads exactly like a result, so the
    /// composition is asserted here rather than merely printed:
    ///
    ///  - that construction appears at most ONCE in any passage, so it cannot carry this measurement;
    ///  - the DIALOGUE gradient is real and spans from zero to dense, with at least two passages
    ///    carrying NO quote character at all (the control for a quote-normalization result) and at
    ///    least two above forty;
    ///  - the rarer edit-candidate families (en-dash, maqaf, ellipsis, apostrophe, exclamation) are each
    ///    present in more than one passage, so no single family is a sample of one;
    ///  - punctuation density is non-trivial in every passage.
    /// </summary>
    [Fact]
    public void TheCompositionOfTheSet_IsDiverse_AndNoSingleConstructionCanCarryTheResult()
    {
        var comps = RealProsePrecisionFixtures.Compositions;
        Assert.Equal(All.Count, comps.Count);

        // (a) the construction that dominated the synthetic run cannot dominate this one.
        var worst = comps.Max(c => c.MinHaDefinite);
        Assert.True(worst <= 1,
            $"a passage carries {worst} instances of '{RealProsePrecisionFixtures.SyntheticDominantConstruction}'. " +
            "That construction carried 62% of the synthetic measurement's over-corrections on four " +
            "instances; letting it cluster here reproduces the monoculture this set exists to avoid.");
        // ...and it is PRESENT, otherwise the set is blind to the one construction already known to move.
        Assert.True(comps.Count(c => c.MinHaDefinite > 0) >= 5,
            "fewer than five passages contain the construction at all, so the set cannot see whether it " +
            "behaves here the way it behaved on the synthetic fixtures");

        // (b) the dialogue gradient, which is this manuscript's largest edit-candidate family.
        Assert.True(comps.Count(c => c.Quotes == 0) >= 2,
            "fewer than two passages are quote-free, so a quote-normalization result could not be told " +
            "apart from a precision result");
        Assert.True(comps.Count(c => c.AsciiDoubleQuotes >= 40) >= 2,
            "fewer than two passages are quote-dense, so the top of the gradient is a sample of one");
        Assert.True(comps.Max(c => c.AsciiDoubleQuotes) >= 40,
            "the set never reaches a dialogue-dense passage");

        // (c) the rarer families, each in more than one passage.
        var families = new (string Name, Func<RealProseComposition, int> Get)[]
        {
            ("en-dash", c => c.EnDashes),
            ("maqaf", c => c.Maqafs),
            ("three-dot run", c => c.ThreeDotRuns),
            ("apostrophe", c => c.AsciiApostrophes),
            ("exclamation", c => c.ExclamationMarks),
            ("question mark", c => c.QuestionMarks),
        };
        var thin = families
            .Select(f => (f.Name, Count: comps.Count(c => f.Get(c) > 0)))
            .Where(x => x.Count < 2)
            .Select(x => $"{x.Name} appears in {x.Count} passage(s)")
            .ToList();
        Assert.True(thin.Count == 0,
            "An edit-candidate family is present in fewer than two passages, so any movement in it " +
            "would be a sample of one: " + string.Join("; ", thin));

        // (d) every passage has real punctuation surface to over-correct on.
        var flat = comps.Where(c => c.PunctuationPer100Words < 8).Select(c => c.PassageId).ToList();
        Assert.True(flat.Count == 0,
            "A passage carries less than 8 non-quote punctuation marks per 100 words, so it offers the " +
            "model almost nothing to over-correct and would dilute the per-passage matrix: " +
            string.Join(", ", flat));
    }

    // ── 3. confound hygiene ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The DETERMINISTIC ktiv-male checker must find nothing in any passage, clean or seeded. It runs
    /// with production defaults on every Hebrew proofread result, so a haser spelling anywhere would add
    /// a suggestion that is not the model's - identical in both arms, and therefore a constant nobody
    /// would remember to subtract from a per-passage matrix.
    ///
    /// NON-VACUITY: the checker is proved LIVE first, on a known haser word, because
    /// <c>FindSuggestions</c> returns an empty list for a non-Hebrew language or a disabled toggle -
    /// i.e. "no offenders" is exactly what a mis-wired checker also returns.
    /// </summary>
    [Fact]
    public void NoPassage_TripsTheDeterministicKtivMaleChecker()
    {
        var checker = new KtivMaleChecker(new HebrewStyleOptions());

        var probe = checker.FindSuggestions("הוא קרא את העתון בבוקר.", RealProsePrecisionFixtures.Language);
        Assert.True(probe.Count > 0,
            "the ktiv-male checker produced nothing on a known haser word (עתון), so it is not actually " +
            "running and every per-passage assertion below would pass for free");

        var offenders = new List<string>();
        foreach (var p in All)
        foreach (var v in p.Variants)
        {
            var found = checker.FindSuggestions(p.TextFor(v), RealProsePrecisionFixtures.Language);
            if (found.Count > 0)
                offenders.Add($"{p.Id}/{v}: {found.Count} ktiv-male suggestion(s), e.g. " +
                              $"[{found[0].OriginalText} -> {found[0].SuggestedText}]");
        }

        Assert.True(offenders.Count == 0,
            "A passage trips the deterministic ktiv-male checker. Those suggestions are appended to the " +
            "proofread result and would be counted as model over-correction:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// CHARACTER HYGIENE, one class per reason:
    ///  - NO-BREAK SPACE (U+00A0): an NBSP -> space edit is whitespace-only on BOTH sides, so it lands
    ///    in the <c>WhitespaceOnlySuggestions</c> TRIPWIRE a run is voided on. A false void is as
    ///    expensive as a false result.
    ///  - Latin letters / digits / niqqud / bidi controls: each is an edit-candidate family with no
    ///    counterpart elsewhere in the set, so a single occurrence would be a sample of one. Niqqud and
    ///    cantillation are caught as a CLASS (every such mark is <c>UnicodeCategory.NonSpacingMark</c>,
    ///    and no legitimate passage character is - letters are Lo, maqaf is Pd, geresh/gershayim are Po);
    ///    bidi and zero-width controls likewise (<c>UnicodeCategory.Format</c> covers
    ///    LRM/RLM/RLE/LRE/PDF/RLI/LRI/FSI/PDI/ALM and the zero-width family), so no hand-listed subset
    ///    can under-cover the header claim.
    ///  - Brackets and colons: the manuscript uses them a handful of times in the whole book; excluding
    ///    them keeps the passages comparable to each other.
    ///
    /// NON-VACUITY: the scanner is proved on a probe string that contains one of every banned character
    /// plus representatives of both banned categories before the passages are scanned, because "found
    /// nothing" is also what a scanner with an empty character set returns.
    /// </summary>
    [Fact]
    public void NoPassage_CarriesACharacterClassThatWouldFakeOrVoidAnEdit()
    {
        var banned = new (char Ch, string Why)[]
        {
            (' ', "NO-BREAK SPACE (would produce a whitespace-only edit and VOID the run)"),
            ('(', "parenthesis"),
            (')', "parenthesis"),
            (':', "colon"),
            (';', "semicolon"),
        };

        // NON-VACUITY: a probe carrying every named banned character, plus representatives of both
        // category-banned classes, must be caught by this exact scan. The representatives include the
        // marks the scan once named individually (sheva, dagesh, LRM, RLM, ZWSP, ZWNBSP) AND members
        // the named list never covered (qamats, RLE), so narrowing the scan back to a hand-list of
        // those six characters fails here.
        const string categoryProbes =
            "\u05B0\u05BC\u05B8" +          // sheva, dagesh, qamats - all NonSpacingMark
            "\u200E\u200F\u200B\uFEFF\u202B"; // LRM, RLM, ZWSP, ZWNBSP, RLE - all Format
        var probe = new string(banned.Select(b => b.Ch).ToArray()) + categoryProbes + "A7";
        var probeHits = Scan(probe, banned);
        Assert.Equal(banned.Length + categoryProbes.Length + 2, probeHits.Count);

        var offenders = new List<string>();
        foreach (var p in All)
        foreach (var v in p.Variants)
        {
            var hits = Scan(p.TextFor(v), banned);
            if (hits.Count > 0)
                offenders.Add($"{p.Id}/{v}: {string.Join(", ", hits.Distinct())}");
        }

        Assert.True(offenders.Count == 0,
            "A passage carries a character class that would fabricate or void an edit:\n  " +
            string.Join("\n  ", offenders));
    }

    private static List<string> Scan(string text, (char Ch, string Why)[] banned)
    {
        var hits = new List<string>();
        foreach (var (ch, why) in banned)
            for (var i = 0; i < text.Length; i++)
                if (text[i] == ch) hits.Add($"U+{(int)ch:X4} {why}");
        foreach (var c in text)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') hits.Add("Latin letter");
            if (char.IsAsciiDigit(c)) hits.Add("digit");

            // CLASS checks, so the scan is as wide as the header claim. Every Hebrew niqqud and
            // cantillation mark is NonSpacingMark, and every bidi / zero-width control is Format;
            // no legitimate passage character is either (letters are Lo, maqaf U+05BE is Pd, the
            // geresh U+05F3 / gershayim U+05F4 are Po, NBSP is Zs and stays a named entry above).
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.NonSpacingMark:
                    hits.Add($"U+{(int)c:X4} niqqud/cantillation (NonSpacingMark)");
                    break;
                case UnicodeCategory.Format:
                    hits.Add($"U+{(int)c:X4} bidi or zero-width control (Format)");
                    break;
            }
        }
        return hits;
    }

    // ── 4. the seeds: the recall guard's denominator ─────────────────────────────────────────────

    /// <summary>
    /// THE RECALL GUARD'S NECESSARY CONDITIONS. A transplant that is absent, ambiguous or in the wrong
    /// chunk turns the recall denominator into a number nobody can read - and, because the guard exists
    /// precisely to catch an arm that buys precision by editing less, a broken guard is worse than none:
    /// it would report full recall on defects that were never injected.
    ///
    /// For every seed:
    ///  - its CLEAN span occurs EXACTLY ONCE in the clean passage (the transplant is unambiguous);
    ///  - its SEEDED span occurs ZERO times in the clean passage and EXACTLY ONCE in the seeded one
    ///    (a repair can never be confused with prose that was always there);
    ///  - the seeded text really differs from the clean text;
    ///  - the seed lands in the chunk it declares, driven through the real chunker;
    ///  - <c>RepairedIn</c> is FALSE on the seeded text and TRUE on the clean text - i.e. the detector
    ///    actually detects, in both directions. Without this the recall column could be all-zero (or
    ///    all-one) for a reason that has nothing to do with the model.
    /// </summary>
    [Fact]
    public void EverySeed_IsUnique_LandsInItsDeclaredChunk_AndItsDetectorWorksInBothDirections()
    {
        var seeded = RealProsePrecisionFixtures.Seeded;
        Assert.NotEmpty(seeded);
        var seedsChecked = 0;
        var offenders = new List<string>();

        foreach (var p in seeded)
        {
            var clean = p.CleanText;
            var seededText = p.SeededText;

            if (string.Equals(clean, seededText, StringComparison.Ordinal))
                offenders.Add($"{p.Id}: the seeded text is IDENTICAL to the clean text");

            var chunks = RealProseHarness.Chunk(p, RealProseVariant.Seeded);

            foreach (var s in p.Seeds)
            {
                seedsChecked++;
                var cleanOcc = RealProsePrecisionFixtures.Occurrences(clean, s.CleanSpan);
                if (cleanOcc != 1)
                    offenders.Add($"{p.Id}/{s.GoldCaseId}: clean span [{s.CleanSpan}] occurs {cleanOcc} " +
                                  "time(s) in the clean passage; the transplant must be unambiguous");

                if (RealProsePrecisionFixtures.Occurrences(clean, s.SeededSpan) != 0)
                    offenders.Add($"{p.Id}/{s.GoldCaseId}: seeded span [{s.SeededSpan}] ALREADY occurs in " +
                                  "the clean passage, so a scorer cannot tell a repair from untouched prose");

                if (RealProsePrecisionFixtures.Occurrences(seededText, s.SeededSpan) != 1)
                    offenders.Add($"{p.Id}/{s.GoldCaseId}: seeded span [{s.SeededSpan}] occurs " +
                                  $"{RealProsePrecisionFixtures.Occurrences(seededText, s.SeededSpan)} " +
                                  "time(s) in the seeded passage; expected exactly one");

                var landed = chunks
                    .Select((c, i) => (c, i))
                    .Where(x => x.c.Text.Contains(s.SeededSpan, StringComparison.Ordinal))
                    .Select(x => x.i)
                    .ToArray();
                if (!landed.SequenceEqual(new[] { s.ExpectedChunkIndex }))
                    offenders.Add($"{p.Id}/{s.GoldCaseId}: lands in chunk(s) [{string.Join(",", landed)}] " +
                                  $"but declares chunk {s.ExpectedChunkIndex}");

                // The detector, in BOTH directions.
                if (s.RepairedIn(seededText))
                    offenders.Add($"{p.Id}/{s.GoldCaseId}: RepairedIn() reports the SEEDED text as already " +
                                  "repaired, so this seed would score a recall hit with no model involved");
                if (!s.RepairedIn(clean))
                    offenders.Add($"{p.Id}/{s.GoldCaseId}: RepairedIn() does not recognise the CLEAN text " +
                                  "as repaired, so a perfect model would score a recall MISS");
            }
        }

        Assert.Equal(8, seedsChecked);
        Assert.True(offenders.Count == 0,
            "A transplanted defect fails a NECESSARY condition for the recall guard to measure " +
            "anything:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// THE SEEDS REALLY COME FROM THE GOLD CORPUS, and the recall guard is SPREAD rather than clustered.
    ///
    /// Every seed cites a case id that exists in <c>proofread-gold.json</c>, and the category it records
    /// is the category that case declares - so "transplanted from the gold corpus" is a checkable claim
    /// rather than a comment. NON-VACUITY: the gold file is asserted to have loaded, because
    /// <c>LoadProofreadGold</c> returns an EMPTY array rather than throwing when the JSON is missing
    /// from the output directory, and an empty gold satisfies every id lookup below for free... by
    /// failing it, which is why the assertion is stated as a hard <c>NotEmpty</c> first.
    ///
    /// SPREAD: the seeds must cover more than one gold case, more than one category, and BOTH chunk
    /// positions - a recall guard whose defects all sat in chunk 0 would never exercise the chunk the
    /// arm's overlap licence actually acts on.
    /// </summary>
    [Fact]
    public void EverySeed_CitesARealGoldCase_AndTheGuardSpansCategoriesAndChunkPositions()
    {
        var gold = ProofreadQualityTests.LoadProofreadGold();
        Assert.NotEmpty(gold);
        var byId = gold.ToDictionary(c => c.Id, StringComparer.Ordinal);

        var seeds = RealProsePrecisionFixtures.Seeded.SelectMany(p => p.Seeds).ToArray();
        Assert.Equal(8, seeds.Length);

        var offenders = new List<string>();
        foreach (var s in seeds)
        {
            if (!byId.TryGetValue(s.GoldCaseId, out var c))
            {
                offenders.Add($"{s.GoldCaseId}: no such case in proofread-gold.json");
                continue;
            }

            var categories = (c.ExpectedCorrections ?? Array.Empty<LanguageEngine.ProofreadCorrection>())
                .Select(x => x.Category)
                .Where(x => x is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (!categories.Contains(s.Category, StringComparer.Ordinal))
                offenders.Add($"{s.GoldCaseId}: seed records category '{s.Category}' but the gold case " +
                              $"declares [{string.Join(",", categories)}]");
        }

        Assert.True(offenders.Count == 0,
            "A seed's provenance does not check out against the gold corpus it claims to come from:\n  " +
            string.Join("\n  ", offenders));

        // SPREAD.
        Assert.Equal(8, seeds.Select(s => s.GoldCaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.True(seeds.Select(s => s.Category).Distinct(StringComparer.Ordinal).Count() >= 3,
            "the recall guard covers fewer than three gold categories, so a category-shaped recall drop " +
            "(exactly what a scope-narrowing arm would produce) could hide in it");
        Assert.Contains(seeds, s => s.ExpectedChunkIndex == 0);
        Assert.Contains(seeds, s => s.ExpectedChunkIndex == 1);
        Assert.Equal(4, seeds.Count(s => s.ExpectedChunkIndex == 1));

        // ...and the seeded passages themselves span the dialogue gradient, so a recall drop cannot be
        // written off as a register effect.
        var seededComps = RealProsePrecisionFixtures.Seeded
            .Select(RealProsePrecisionFixtures.Describe).ToArray();
        Assert.Contains(seededComps, c => c.Quotes == 0);
        Assert.Contains(seededComps, c => c.AsciiDoubleQuotes >= 40);
    }

    /// <summary>
    /// Seeding must not move the CHUNK BOUNDARY. Two of the transplants change the word count by one
    /// (a doubled word, a split prefix) and one changes it by minus one (two words glued), so a
    /// boundary shift is a real possibility rather than a theoretical one. If it happened, the seeded
    /// run and the clean run of the same passage would be chunked differently and their numbers would
    /// stop being comparable - silently.
    /// </summary>
    [Fact]
    public void SeedingAPassage_DoesNotMoveItsChunkBoundaries()
    {
        var offenders = new List<string>();
        var comparisons = 0;

        foreach (var p in RealProsePrecisionFixtures.Seeded)
        {
            comparisons++;
            var clean = RealProseHarness.Chunk(p, RealProseVariant.Clean);
            var seeded = RealProseHarness.Chunk(p, RealProseVariant.Seeded);

            if (clean.Count != seeded.Count)
            {
                offenders.Add($"{p.Id}: clean chunks to {clean.Count}, seeded to {seeded.Count}");
                continue;
            }

            // Same boundaries: each chunk's word count moves by at most the seeds landing in it.
            for (var i = 0; i < clean.Count; i++)
            {
                var delta = Math.Abs(
                    RealProsePrecisionFixtures.WordCount(seeded[i].Text) -
                    RealProsePrecisionFixtures.WordCount(clean[i].Text));
                var seedsHere = p.Seeds.Count(s => s.ExpectedChunkIndex == i);
                if (delta > seedsHere)
                    offenders.Add($"{p.Id}: chunk {i} word count moved by {delta} but only {seedsHere} " +
                                  "seed(s) land there, so the boundary shifted");
            }
        }

        Assert.Equal(4, comparisons);
        Assert.True(offenders.Count == 0,
            "Seeding moved a chunk boundary, so the clean and seeded runs of the same passage are no " +
            "longer chunked the same way and their numbers are not comparable:\n  " +
            string.Join("\n  ", offenders));
    }

    // ── 5. the surface partition ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// SURFACE TAGGING. Every passage declares the per-chunk surface, that tag agrees with the surface
    /// DERIVED from the routing rule, and no passage id collides with the gold corpus - so an id-keyed
    /// report cannot conflate the two even though they share one surface vocabulary.
    ///
    /// The declared tag is checked against a DERIVATION rather than restated, for the same reason
    /// <c>GoldPromptSurfaces.DerivedSurfaceOf</c> exists: nothing a passage declares enters into the
    /// routing decision, so a passage that grew or shrank past the threshold would change surface
    /// silently and its tag would stop meaning what it says.
    /// </summary>
    [Fact]
    public void EveryPassage_IsOnThePerChunkSurface_AndNoIdCollidesWithTheGoldCorpus()
    {
        var gold = ProofreadQualityTests.LoadProofreadGold();
        Assert.NotEmpty(gold);

        var goldIds = gold.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var collisions = All.Where(p => goldIds.Contains(p.Id)).Select(p => p.Id).ToList();
        Assert.True(collisions.Count == 0,
            "A real-prose passage id already exists in proofread-gold.json: " + string.Join(", ", collisions));

        var chunkedIds = ChunkedAgreementFixtures.All.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var chunkedCollisions = All.Where(p => chunkedIds.Contains(p.Id)).Select(p => p.Id).ToList();
        Assert.True(chunkedCollisions.Count == 0,
            "A real-prose passage id collides with a chunked-agreement fixture id: " +
            string.Join(", ", chunkedCollisions));

        // DERIVED, not declared: the routing rule decides the surface, so the tag is checked against it.
        var target = RealProseHarness.ChunkTargetWords();
        foreach (var p in All)
        {
            var words = RealProsePrecisionFixtures.WordCount(
                RealProseHarness.ProductionTargetText(p, RealProseVariant.Clean));
            var derived = words > target
                ? GoldPromptSurface.ChunkedPerChunk
                : GoldPromptSurface.ProductionLongPlusShort;
            Assert.True(p.Surface == derived,
                $"{p.Id} declares {p.Surface} but the routing rule ({words} words vs a {target}-word " +
                $"target) puts it on {derived}");
        }

        // A partition over the WHOLE surface vocabulary, not over the one value this corpus uses today.
        var perSurface = GoldPromptSurfaces.AllSurfaces.Sum(s => All.Count(p => p.Surface == s));
        Assert.Equal(All.Count, perSurface);
    }
}
