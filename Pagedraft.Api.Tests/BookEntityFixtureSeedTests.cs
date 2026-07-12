using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Tests.LanguageEngine;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC coverage of the d6 PRESERVATION gate's seeding (be-c07). NO model, NO GPU, NO network, no
/// [Trait("Category","LiveDiagnostic")], no env-var opt-in — this runs in an ordinary suite.
///
/// WHY IT EXISTS. The d6 gate (<c>OutputQualityDiagnostic.MeasureLegitimateTermPreservation_LocalVsCloud</c>)
/// is the false-positive gate whose PASS justified turning the dynamic term repair ON in production. It used to
/// HAND-AUTHOR its per-book entity sets, so the two things the feature actually added — the SCRIPT-AWARE
/// manuscript harvest and the bookId threading — were never on the measured path. It now sources those sets
/// from the REAL <see cref="BookEntityProvider"/> over a REAL DbContext
/// (<see cref="PreservationFixtureBooks"/>).
///
/// That makes the SEEDING load-bearing: if a chapter's prose stops carrying a name in the right script/case, or
/// the provider's harvest regresses, the live gate would quietly stop exercising the entity lever. These tests
/// pin the contract so that regression is a RED TEST here, in seconds, instead of a silently-still-passing GPU
/// run an hour later.
///
/// Class name deliberately matches the <c>~BookEntity</c> filter used by the plan's deterministic verification.
/// </summary>
public class BookEntityFixtureSeedTests
{
    // ── the HEBREW-native book: the Latin names must be HARVESTED, in the exact fixture case ────────────

    [Fact]
    public async Task HebrewNativeBook_HarvestsEveryLatinNameTheFixtureDependsOn()
    {
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.HebrewBookEntitiesAsync();

        foreach (var name in PreservationFixtureBooks.HebrewBookLatinNames)
        {
            Assert.True(entities.Contains(name),
                $"The REAL BookEntityProvider did not harvest '{name}' from the Hebrew-native book's manuscript. " +
                "The d6 preservation gate's entity lever is only exercised if the provider actually produces these " +
                "(the harvest needs a TITLE-CASE Latin token that recurs across >= 2 chapters OR appears " +
                "mid-sentence at least once — check the seeded chapter prose in PreservationFixtureBooks).");
        }
    }

    [Fact]
    public async Task HebrewNativeBook_ManuscriptTokensAreCaseSensitive_SoALowercaseLeakIsNotSpared()
    {
        // be-c04's two-tier contract, exercised through the fixture's own seeding: the harvested surface form
        // is spared, its lowercase form is NOT (a vocabulary leak is lowercase by construction).
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.HebrewBookEntitiesAsync();

        Assert.True(entities.Contains("Paris"));
        Assert.False(entities.Contains("paris"));

        var set = Assert.IsType<BookEntitySet>(entities);
        Assert.Contains("Paris", set.ManuscriptTokens);
    }

    [Fact]
    public async Task HebrewNativeBook_DoesNotHarvestParticlesOrAcronyms()
    {
        // Two deliberate NON-harvests, both recorded rather than hidden (be-c07 report):
        //   • lowercase particles (van/da/de) — not Title-Case, so the scan skips them, and the classifier's
        //     name-particle rule (8) owns them. Harvesting them would MASK whether that rule works.
        //   • ALL-CAPS acronyms (NASA/PDF) — the manuscript scan records only TITLE-CASE tokens, so an acronym
        //     can NEVER enter the set from the prose. They do not need it: classifier rule (6) gates them.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.HebrewBookEntitiesAsync();

        foreach (var particle in PreservationFixtureBooks.HebrewBookParticles)
        {
            Assert.False(entities.Contains(particle), $"'{particle}' must not be harvested as a standalone entity.");
        }

        foreach (var acronym in PreservationFixtureBooks.HebrewBookNonHarvestableAcronyms)
        {
            Assert.False(entities.Contains(acronym),
                $"'{acronym}' is ALL-CAPS: the manuscript scan takes only Title-Case tokens, so it is NOT " +
                "harvestable. If this ever starts passing, the harvest shape changed — re-check the recall side.");
        }
    }

    // ── the LATIN-native book: the Hebrew names are the ONLY lever that can gate a Hebrew run ───────────

    [Fact]
    public async Task LatinNativeBook_HarvestsEveryHebrewNameTheFixtureDependsOn_InTheRightTier()
    {
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.EnglishBookEntitiesAsync();

        foreach (var name in PreservationFixtureBooks.EnglishBookHebrewNames)
        {
            Assert.True(entities.Contains(name),
                $"The REAL BookEntityProvider did not harvest '{name}' from the Latin-native book. Hebrew has no " +
                "letter case, so this entity set is the ONLY lever that can spare a Hebrew run in an English book " +
                "— without it the d6 hebrew-in-english cases reach the repair model.");
        }

        var set = Assert.IsType<BookEntitySet>(entities);

        // The CHARACTERS come from the stored CharacterAnalysis => the case-INSENSITIVE DECLARED tier.
        foreach (var declared in PreservationFixtureBooks.EnglishBookDeclaredNames)
        {
            Assert.Contains(declared, set.DeclaredNames);
        }

        // The PLACE has no declared source: it can ONLY arrive via the cross-chapter recurrence rule be-c03
        // added — the exact lever the hand-built set used to fake.
        foreach (var manuscriptOnly in PreservationFixtureBooks.EnglishBookManuscriptOnlyNames)
        {
            Assert.Contains(manuscriptOnly, set.ManuscriptTokens);
            Assert.DoesNotContain(manuscriptOnly, set.DeclaredNames);
        }
    }

    // ── the whole fixture, through the shipped detector + classifier (ZERO model calls) ─────────────────

    [Fact]
    public async Task EveryLegitCase_IsDeterministicallyGated_WithTheRealProviderSet()
    {
        // The invariant the whole rollout decision rests on: with the shipped gate + the REAL provider's entity
        // set, no legitimate value reaches the repair model at all. A case that starts reaching the model is a
        // precision regression, and it is caught HERE (deterministically) rather than in a GPU run.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();

        foreach (var c in PreservationFixtureBooks.Cases)
        {
            var entities = await fixtureBooks.EntitiesForAsync(c);
            var gate = PreservationFixtureBooks.AttributeGate(c, entities);

            Assert.False(gate.ReachesModel,
                $"[{c.Cls}] '{c.Token}' now reaches the repair model ({gate.RepairRuns} run(s)) — it used to be " +
                $"deterministically gated. Expected gate: {c.Note}.");
        }
    }

    [Fact]
    public async Task EveryCaseThatRequiresAnEntity_IsGatedByAnEntityTheProviderActuallyHarvested()
    {
        // THE be-c07 ASSERTION. A case that declares a GatingEntity can ONLY be spared by the per-book entity
        // lever. Two things must hold, and the hand-built set proved NEITHER:
        //   1. the provider actually HARVESTED that entity (not "a hand-fed set contained it");
        //   2. the entity is LOAD-BEARING — the run REPAIRs without the entity set and LEAVEs with it.
        // If (1) ever fails, the case could only pass with a hand-fed entity — a FINDING, not something to
        // paper over.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();

        var required = PreservationFixtureBooks.Cases.Where(c => c.GatingEntity is not null).ToList();
        Assert.NotEmpty(required); // the fixture must keep exercising the entity lever at all

        foreach (var c in required)
        {
            var entities = await fixtureBooks.EntitiesForAsync(c);
            var gate = PreservationFixtureBooks.AttributeGate(c, entities);

            Assert.False(gate.RequiredEntityMissing,
                $"FINDING: [{c.Cls}] '{c.Token}' requires the book-entity '{c.GatingEntity}', which the REAL " +
                "BookEntityProvider did NOT produce. The case can only pass with a hand-fed entity.");

            Assert.True(gate.EntityLoadBearing,
                $"[{c.Cls}] '{c.Token}' is no longer gated BY THE ENTITY SET (it is spared by some classifier " +
                "rule instead), so this case no longer exercises BookEntityProvider — a provider regression " +
                "would go unnoticed here.");

            Assert.Contains(c.GatingEntity!, gate.EntitySparedRuns);
        }
    }

    [Fact]
    public async Task WithoutTheProviderSet_TheHebrewInEnglishCasesReachTheModel()
    {
        // The REVERT-VERIFY twin of the test above: it proves the entity lever is what does the work, not some
        // other rule quietly passing the case. Drop the entity set and the three Hebrew-in-English values go
        // straight to the repair model (no case signal exists in Hebrew) — which is exactly why the provider
        // must be measured rather than assumed.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();

        foreach (var c in PreservationFixtureBooks.Cases.Where(x => x.GatingEntity is not null))
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(c.Value, c.Expected);
            var withoutEntities = ForeignRunClassifier.RunsToRepair(runs, c.Value, c.Expected, null);

            Assert.NotEmpty(withoutEntities);
            Assert.Contains(withoutEntities, r => r.Text == c.GatingEntity);
        }
    }

    // ── the ADVERSARIAL book (be-c08): the entity lever pointed AT the leak set ─────────────────────────
    //
    // THE OPEN QUESTION be-c08 exists to close: can the entity lever SPARE A REAL LEAK? be-c04 proved on
    // synthetic input that it could — one capitalized `Confusion` in a manuscript harvested the token, and with
    // CASE-INSENSITIVE membership every lowercase `confusion` in the analysis output was then LEFT instead of
    // cleaned (3 of 10 leaks flipped REPAIR -> LEAVE, a 30% recall regression). be-c04 fixed it by matching
    // MANUSCRIPT-harvested tokens CASE-SENSITIVELY (BookEntitySet) while stored DECLARED names stay
    // case-insensitive.
    //
    // These three tests are the OFFLINE half of the live d5 ARM B, and they are what makes that GPU run worth
    // doing: they prove (1) the harvest really fires on the adversarial book — the setup is real, not
    // hypothetical; (2) the lowercase leaks are STILL classified REPAIR through the REAL provider set, with zero
    // model calls; and (3) the test is NOT VACUOUS — the same harvested tokens, matched case-INSENSITIVELY,
    // DO eat the leaks. If (2) ever fails, the GPU run is pointless: the lever is eating leaks on the
    // production path.

    [Fact]
    public async Task AdversarialBook_HarvestsTheCapitalizedLeakWords_AsManuscriptTierEntities()
    {
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.AdversarialBookEntitiesAsync();
        var set = Assert.IsType<BookEntitySet>(entities);

        foreach (var word in PreservationFixtureBooks.AdversarialHarvestedLeakWords)
        {
            Assert.True(entities.Contains(word),
                $"The REAL BookEntityProvider did not harvest '{word}' from the adversarial book's epigraph. " +
                "Without it the d5 ARM B is NOT adversarial and proves nothing about the entity lever — the " +
                "harvest needs a TITLE-CASE Latin token appearing mid-sentence at least once (the epigraph in " +
                "PreservationFixtureBooks.SeedAdversarialLeakBook).");

            Assert.Contains(word, set.ManuscriptTokens);   // inferred from prose => case-SENSITIVE tier
            Assert.DoesNotContain(word, set.DeclaredNames); // NOT declared by any stored analysis
        }
    }

    [Fact]
    public async Task AdversarialBook_HarvestedLeakWords_StillRepairInTheirLowercaseLeakProse()
    {
        // THE be-c04 FIX, asserted end to end through the REAL provider, with ZERO model calls. The book's
        // manuscript contains `Confusion` / `Nostalgia` / `Catharsis` (harvested, per the test above), and yet
        // every lowercase leak in the d5 prose must STILL be classified REPAIR — including those three.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.AdversarialBookEntitiesAsync();

        foreach (var leak in PreservationFixtureBooks.LeakCases)
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(leak.Value, ExpectedScript.Hebrew);
            var repairRuns = ForeignRunClassifier.RunsToRepair(runs, leak.Value, ExpectedScript.Hebrew, entities);

            Assert.True(repairRuns.Count > 0,
                $"[{leak.Label}] the leak '{leak.Leak}' is classified LEAVE with the adversarial book's REAL entity " +
                "set — the per-book entity lever is EATING A REAL LEAK. This is the be-c04 recall regression " +
                "(a capitalized manuscript occurrence sparing the lowercase leak); the manuscript tier must match " +
                "CASE-SENSITIVELY. The d5 GPU run would measure a gate that is already broken.");

            Assert.Contains(repairRuns, r => r.Text == leak.Leak);
        }
    }

    [Fact]
    public async Task AdversarialBook_IsGenuinelyAdversarial_CaseInsensitiveMatchingWouldEatTheLeaks()
    {
        // NON-VACUITY / REVERT-VERIFY. The test above passes — but does it pass because the fix WORKS, or because
        // the fixture cannot bite? Take the SAME tokens the REAL provider harvested and match them the way
        // be-c04 replaced (a plain case-INSENSITIVE HashSet, which is exactly what the classifier's bookEntities
        // lever did before BookEntitySet): the three leak words that are also d5 seeds flip to LEAVE. The fixture
        // bites; the case-SENSITIVE manuscript tier is what disarms it.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.AdversarialBookEntitiesAsync();

        var caseInsensitive = new HashSet<string>(entities, StringComparer.OrdinalIgnoreCase);

        var eaten = new List<string>();
        foreach (var leak in PreservationFixtureBooks.LeakCases)
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(leak.Value, ExpectedScript.Hebrew);
            var repairRuns = ForeignRunClassifier.RunsToRepair(runs, leak.Value, ExpectedScript.Hebrew, caseInsensitive);
            if (repairRuns.Count == 0)
            {
                eaten.Add(leak.Leak);
            }
        }

        // confusion / nostalgia / catharsis — the three harvested words that are also d5 leak seeds.
        foreach (var word in PreservationFixtureBooks.AdversarialLeakWordsThatAreD5Seeds)
        {
            Assert.Contains(word.ToLowerInvariant(), eaten);
        }

        Assert.Equal(PreservationFixtureBooks.AdversarialLeakWordsThatAreD5Seeds.Count, eaten.Count);
    }

    [Fact]
    public async Task AcronymAndParticleCases_AreGatedByAClassifierRule_NotByTheEntitySet()
    {
        // The honest attribution the hand-built set obscured: NASA / PDF / van / da / de — and the three be-c01
        // MULTI-particle shapes — are spared by CLASSIFIER RULES (ALL-CAPS, the name-span walk), not by the
        // entity lever, and the provider cannot even produce NASA / PDF from the manuscript. Pinning this keeps
        // a future reader from "fixing" the harvest to include acronyms on the false belief that the gate
        // depends on it.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.HebrewBookEntitiesAsync();

        foreach (var c in PreservationFixtureBooks.Cases.Where(x =>
                     x.Cls is "acronym" or "proper-noun (lowercase particle)"
                     || PreservationFixtureBooks.MultiParticleClasses.Contains(x.Cls)))
        {
            var gate = PreservationFixtureBooks.AttributeGate(c, entities);

            Assert.False(gate.ReachesModel);
            Assert.False(gate.EntityLoadBearing,
                $"[{c.Cls}] '{c.Token}' is being spared by the ENTITY SET; it is supposed to be spared by a " +
                "classifier rule, and letting the entity set mask that would hide a rule regression.");
        }
    }

    // ── the be-c01 P0 shapes (be-c08): the invariant the whole rollout decision rests on ─────────────────
    //
    // THE P0. The name-particle rule originally recognised only a SINGLE lowercase particle between two
    // Title-Case Latin names. When TWO lowercase runs sit side by side each DISQUALIFIED the other, so BOTH
    // went to the repair model, which spliced Hebrew into the name/title span-scoped — and re-detect could not
    // see it (substituting Hebrew for "of" REDUCES the Latin-run count, so the corruption read as a clean
    // repair). Confirmed against the un-patched code: `The Lord of the Rings` sent "of"+"the", `Mies van der
    // Rohe` sent "van"+"der", `Charles de la Rue` sent "de"+"la".
    //
    // The three shapes are now IN the d6 preservation fixture, so the report of record shows them GATED with
    // ZERO model calls. These two tests pin WHAT gates them: the CLASSIFIER's bounded name-span walk (rule 8)
    // ALONE — not the per-book entity set, which is inert here BY CONSTRUCTION because none of their tokens is
    // seeded into any manuscript.

    [Fact]
    public async Task MultiParticleNameSpanTokens_AreNeverHarvested_SoTheEntityLeverCannotMaskTheClassifierRule()
    {
        // The construction that makes the d6 attribution honest. If ANY of Lord / Rings / Mies / Rohe /
        // Charles / Rue were written into the seeded Hebrew prose, the provider would harvest it (Title-Case +
        // one mid-sentence mention is enough — be-c04) and the report would credit the ENTITY lever for gating
        // the P0 shapes, hiding the fact that the deterministic classifier rule carries them on its own.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var entities = await fixtureBooks.HebrewBookEntitiesAsync();

        foreach (var token in PreservationFixtureBooks.MultiParticleNameSpanTokens)
        {
            Assert.False(entities.Contains(token),
                $"'{token}' belongs to a be-c01 P0 name span and was HARVESTED from the seeded manuscript. Those " +
                "shapes must be gated by the CLASSIFIER's name-span rule with no entity help — remove the token " +
                "from the seeded prose (PreservationFixtureBooks.SeedHebrewNativeBook), do not let the entity " +
                "lever mask the rule the d6 report exists to evidence.");
        }
    }

    [Fact]
    public void MultiParticleNameSpans_ClassifyEveryRunLeave_WithNoEntitySetAtAll()
    {
        // THE be-c08 ASSERTION: the classifier rule ALONE carries the P0 shapes. No book, no provider, no entity
        // set (`bookEntities: null`) — and still ZERO runs to repair, i.e. ZERO model calls. If this ever goes
        // red, the name-span walk regressed and the repair model is back to splicing Hebrew into book titles and
        // surnames.
        var p0 = PreservationFixtureBooks.Cases
            .Where(c => PreservationFixtureBooks.MultiParticleClasses.Contains(c.Cls))
            .ToList();

        Assert.Equal(3, p0.Count); // The Lord of the Rings / Mies van der Rohe / Charles de la Rue

        foreach (var c in p0)
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(c.Value, c.Expected);

            // NON-VACUITY: the detector really does produce the multi-token name span (>= 4 word-level Latin
            // runs, two of them the adjacent lowercase particles). Without this the test could pass on a value
            // the detector never even looked at.
            Assert.True(runs.Count >= 4,
                $"[{c.Cls}] '{c.Token}': the detector produced only {runs.Count} run(s) — the P0 shape is not " +
                "being exercised at all.");
            Assert.True(
                runs.Count(r => r.Text.All(char.IsLower)) >= 2,
                $"[{c.Cls}] '{c.Token}': fewer than TWO all-lowercase runs — the ADJACENT-particle shape (the " +
                "thing be-c01 fixed) is not present in this value.");

            var repairRuns = ForeignRunClassifier.RunsToRepair(runs, c.Value, c.Expected, bookEntities: null);

            Assert.True(repairRuns.Count == 0,
                $"[{c.Cls}] '{c.Token}': {repairRuns.Count} run(s) ({string.Join(", ", repairRuns.Select(r => $"'{r.Text}'"))}) " +
                "are classified REPAIR with NO entity set. The be-c01 name-span walk must LEAVE every run of a " +
                "Title-Case Latin name span, including two ADJACENT lowercase particles — otherwise the d3 model " +
                "splices Hebrew into the name/title and validation-by-re-detect cannot see it.");
        }
    }
}
