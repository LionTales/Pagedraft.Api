using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE KILL-SWITCH GATE for the per-chunk proofread prompt arms (<c>Ai:ProofreadPrompt</c>).
///
/// WHY A BYTE-IDENTITY TEST AND NOT A BEHAVIOUR TEST. An arm behind a config flag is only safe if its
/// OFF path is the path that was there before the flag existed. "Off means off" is not observable from
/// a model run - two prompts that differ by a space produce different outputs and nobody would notice -
/// so it has to be pinned on the COMPOSED STRING. Every standing proofread number in this corpus was
/// measured on the unconfigured prompt; if the off path drifted by one character, all of them would
/// silently become unreproducible while every other test stayed green.
///
/// THE OTHER DIRECTION MATTERS JUST AS MUCH. An arm that is switched ON but renders NOTHING produces an
/// OFF measurement under an ON label - a null result that looks like a real one. The previous session
/// had to rule that out by hand, reading 120 published prompt artifacts. It is pinned here instead.
///
/// ARM A IS COPIED, NOT RE-AUTHORED. Its rendered text is preserved verbatim in
/// <c>ProofreadStandingFloor.RetiredInterventions</c>, and the production constants are asserted to be
/// CONTAINED IN that record. A re-implementation that paraphrases a prompt is not a re-run of it, and
/// the 38% over-correction cut belongs to the exact string, not to its meaning.
/// </summary>
public class ProofreadPromptArmTests
{
    /// <summary>Languages the per-chunk builder branches on: one Hebrew locale, one Latin.</summary>
    private static readonly string[] Languages = { "he-IL", "he", "en-US", "en" };

    /// <summary>
    /// The register / overlap combinations the builder composes. Both optional sections, both states -
    /// so the off-mode identity is asserted across the whole product of the builder's branches rather
    /// than on the one shape a caller happened to think of.
    /// </summary>
    private static IEnumerable<(string Label, CharacterRegister? Characters, string? Overlap)> Shapes()
    {
        var register = new CharacterRegister
        {
            Characters = new[]
            {
                new CharacterRegisterEntry { Name = "רוני", Gender = "female" },
                new CharacterRegisterEntry { Name = "תומר", Gender = "male" }
            }
        };

        yield return ("bare", null, null);
        yield return ("register-only", register, null);
        yield return ("overlap-only", null, "המשפט הקודם נגמר כאן. וזה המשפט שלפניו.");
        yield return ("register+overlap", register, "המשפט הקודם נגמר כאן. וזה המשפט שלפניו.");
        yield return ("empty-register", new CharacterRegister { Characters = Array.Empty<CharacterRegisterEntry>() }, null);
        yield return ("whitespace-overlap", null, "   ");
    }

    // ── the off path IS the legacy path ──────────────────────────────────────────────────────────

    /// <summary>
    /// OFF MODE COMPOSES A BYTE-IDENTICAL PROMPT TO THE LEGACY CALL. Three factories are compared for
    /// every (language x shape) pair:
    ///   - <c>new PromptFactory()</c>          - the pre-options constructor, i.e. the legacy path;
    ///   - options EXPLICITLY defaulted        - a bound section with nothing set;
    ///   - options explicitly set to FALSE     - a deployment that names the switch and disables it.
    /// All three must produce the same string, character for character.
    ///
    /// NON-VACUITY: the comparison is only worth something if it examined a non-empty set of prompts
    /// AND if those prompts are non-trivial, so the count is asserted and each composed prompt is
    /// required to actually contain the proofread body.
    ///
    /// THE ONE MUTATION THIS TEST CANNOT CATCH, verified by actually performing it on 2026-08-05 rather
    /// than reasoned about: if <c>ProofreadChunkBody</c> stopped consulting the switch and rendered the
    /// arm UNCONDITIONALLY, all three factories here would compose the same string and this test would
    /// still pass - because its "legacy" baseline is built from the same code path it is checking. That
    /// hole is closed elsewhere, by assertions that compare the default path against text rather than
    /// against itself: <c>ProofreadStandingFloorRetiredInterventionTests.ADefaultRun_ComposesTheThree
    /// ArgumentInstruction_ForEveryChunkOfEveryFixture</c> and
    /// <c>.NoRetiredArmsText_OccursInAnyComposedPromptOnTheDefaultPath</c>, and
    /// <c>RealProseArmMeasurementTests.TheDefaultArm_IsOff_AndNoArmsTextReachesTheRealProsePrompt</c>
    /// (which is also the only one of the three that covers the EN branch). The mutation was caught by
    /// five tests; none of them was this one. Do not delete those in the belief that this file covers
    /// them.
    /// </summary>
    [Fact]
    public void OffMode_ComposesAByteIdenticalPromptToTheLegacyThreeArgumentCall()
    {
        var legacy = new PromptFactory();
        var defaulted = new PromptFactory(Options.Create(new ProofreadPromptOptions()));
        var explicitlyOff = new PromptFactory(
            Options.Create(new ProofreadPromptOptions { OverlapReferentLicence = false }));

        var compared = 0;
        var offenders = new List<string>();

        foreach (var lang in Languages)
        foreach (var (label, characters, overlap) in Shapes())
        {
            var baseline = legacy.BuildProofreadChunkPrompt(lang, characters, overlap);
            Assert.False(string.IsNullOrWhiteSpace(baseline));
            Assert.Contains("[TEXT_TO_CORRECT]", baseline, StringComparison.Ordinal);

            foreach (var (name, factory) in new[]
                     {
                         ("options-defaulted", defaulted),
                         ("options-explicitly-false", explicitlyOff)
                     })
            {
                compared++;
                var actual = factory.BuildProofreadChunkPrompt(lang, characters, overlap);
                if (!string.Equals(baseline, actual, StringComparison.Ordinal))
                    offenders.Add($"{lang}/{label}/{name}: composed prompt differs from the legacy call " +
                                  $"(baseline {baseline.Length} chars, actual {actual.Length} chars)");
            }
        }

        Assert.Equal(Languages.Length * Shapes().Count() * 2, compared);
        Assert.True(compared > 0, "no prompt was compared, so this test proved nothing");
        Assert.True(offenders.Count == 0,
            "The DEFAULT-OFF per-chunk prompt is no longer byte-identical to the pre-arm one. Every " +
            "standing proofread number in this corpus was measured on that string, so this is not a " +
            "cosmetic drift - it makes them unreproducible:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The switch reaches the PER-CHUNK builder ONLY. The single-shot proofread path composes through
    /// <c>GetAnalysisPrompt</c>, and the measured arm did NOT reach it - that is recorded on the retired
    /// intervention's Applicability, and it is what makes the single-shot fixture a control for the arm
    /// rather than evidence about it. Widening the scope here would silently measure a different arm.
    /// </summary>
    [Fact]
    public void TheArm_DoesNotReachTheSingleShotProofreadPrompt()
    {
        var off = new PromptFactory();
        var on = new PromptFactory(
            Options.Create(new ProofreadPromptOptions { OverlapReferentLicence = true }));

        foreach (var lang in Languages)
        {
            var offPrompt = off.GetAnalysisPrompt(AnalysisType.Proofread, lang);
            var onPrompt = on.GetAnalysisPrompt(AnalysisType.Proofread, lang);

            Assert.Equal(offPrompt, onPrompt);
            Assert.DoesNotContain(PromptFactory.OverlapReferentLicenceHe, onPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(PromptFactory.OverlapReferentLicenceEn, onPrompt, StringComparison.Ordinal);
        }
    }

    // ── the on path renders EXACTLY the recorded arm, once ───────────────────────────────────────

    /// <summary>
    /// ON MODE ADDS THE RECORDED TEXT, EXACTLY ONCE, IN THE RIGHT PLACE, AND CHANGES NOTHING ELSE.
    ///
    /// The composed ON prompt must equal the OFF prompt with the licence inserted immediately after the
    /// <c>[CONTEXT_BEFORE]</c> instruction line - stated as a string reconstruction rather than as
    /// "contains the licence", because "contains" would pass if the arm were appended at the end of the
    /// prompt, which is a different intervention with a different position in the model's attention.
    /// </summary>
    [Fact]
    public void OnMode_InsertsTheRecordedLicence_ExactlyOnce_DirectlyAfterTheContextBeforeLine()
    {
        var off = new PromptFactory();
        var on = new PromptFactory(
            Options.Create(new ProofreadPromptOptions { OverlapReferentLicence = true }));

        var checkedShapes = 0;
        foreach (var lang in Languages)
        {
            var isHe = lang.StartsWith("he", StringComparison.OrdinalIgnoreCase);
            var anchor = isHe
                ? PromptFactory.ProofreadHeContextBeforeLine
                : PromptFactory.ProofreadEnContextBeforeLine;
            var licence = isHe
                ? PromptFactory.OverlapReferentLicenceHe
                : PromptFactory.OverlapReferentLicenceEn;

            foreach (var (label, characters, overlap) in Shapes())
            {
                checkedShapes++;
                var offPrompt = off.BuildProofreadChunkPrompt(lang, characters, overlap);
                var onPrompt = on.BuildProofreadChunkPrompt(lang, characters, overlap);

                Assert.True(Occurrences(offPrompt, anchor) == 1,
                    $"{lang}/{label}: the [CONTEXT_BEFORE] anchor line occurs " +
                    $"{Occurrences(offPrompt, anchor)} time(s) in the composed prompt; the arm's " +
                    "insertion point is only well defined when it occurs exactly once");

                Assert.Equal(0, Occurrences(offPrompt, licence));
                Assert.Equal(1, Occurrences(onPrompt, licence));

                var at = offPrompt.IndexOf(anchor, StringComparison.Ordinal);
                var reconstructed = offPrompt.Insert(at + anchor.Length, licence);
                Assert.True(string.Equals(reconstructed, onPrompt, StringComparison.Ordinal),
                    $"{lang}/{label}: the ON prompt is not the OFF prompt with the licence inserted " +
                    "directly after the [CONTEXT_BEFORE] line - the arm moved, and its position in the " +
                    "prompt is part of what was measured");

                // The arm renders whether or not an overlap is present. That is not incidental: it is
                // the recorded Applicability of the measured arm ("rendered on EVERY chunked per-chunk
                // prompt, INCLUDING chunks carrying no overlap, because the line it extends is itself
                // conditional prose"). An arm made conditional on the overlap would be a NEW arm.
                if (string.IsNullOrWhiteSpace(overlap))
                {
                    Assert.DoesNotContain("[CONTEXT_BEFORE]\n", onPrompt, StringComparison.Ordinal);
                    Assert.Equal(1, Occurrences(onPrompt, licence));
                }
            }
        }

        Assert.Equal(Languages.Length * Shapes().Count(), checkedShapes);
    }

    /// <summary>
    /// THE ARM IS THE ONE THAT WAS MEASURED. Its production constants must be CONTAINED IN the verbatim
    /// <c>RenderedChange</c> the retired-intervention record preserves - the only surviving statement of
    /// what the model was actually shown, since the original code was reverted.
    ///
    /// A containment check rather than an equality check because the record's field is prose ABOUT the
    /// change (where it was appended, plus the he and en forms quoted). What must not drift is the
    /// quoted strings, and containment is exactly that claim.
    /// </summary>
    [Fact]
    public void TheReLandedArm_IsVerbatimTheTextTheRetiredInterventionRecordPreserves()
    {
        var retired = ProofreadStandingFloor.RetiredInterventionById(
            "referent-carry-forward.ARM_A.OverlapLicence");

        Assert.False(string.IsNullOrWhiteSpace(retired.RenderedChange));

        Assert.Contains(PromptFactory.OverlapReferentLicenceHe, retired.RenderedChange, StringComparison.Ordinal);
        Assert.Contains(PromptFactory.OverlapReferentLicenceEn, retired.RenderedChange, StringComparison.Ordinal);

        // The leading space is load-bearing: it is what JOINS the licence to the end of the
        // [CONTEXT_BEFORE] sentence instead of starting a new one. A trimmed copy would render a
        // different string and would not be the measured arm.
        Assert.StartsWith(" ", PromptFactory.OverlapReferentLicenceHe, StringComparison.Ordinal);
        Assert.StartsWith(" ", PromptFactory.OverlapReferentLicenceEn, StringComparison.Ordinal);

        // ONE ARM ONLY. ARM B (the [RESOLVED_REFERENT] section) was refuted on the same run and bought
        // nothing on either axis; it must not be re-landed alongside A. Its section marker is asserted
        // absent from every composed prompt, in both arm states.
        foreach (var arm in new[] { false, true })
        {
            var factory = new PromptFactory(
                Options.Create(new ProofreadPromptOptions { OverlapReferentLicence = arm }));
            foreach (var lang in Languages)
                Assert.DoesNotContain(
                    "[RESOLVED_REFERENT]",
                    factory.BuildProofreadChunkPrompt(lang, null, "הקשר קודם."),
                    StringComparison.Ordinal);
        }
    }

    // ── the switch is bound, and it is bound OFF ─────────────────────────────────────────────────

    /// <summary>
    /// THE SHIPPED CONFIGURATION LEAVES THE ARM OFF, and the section name the code binds is the section
    /// name appsettings writes. A class default of false is not the same claim as a shipped default of
    /// false: the section could be present and true, and the class default would never be consulted.
    /// </summary>
    [Fact]
    public void TheShippedConfiguration_BindsTheSection_AndLeavesEveryArmOff()
    {
        Assert.Equal("Ai:ProofreadPrompt", ProofreadPromptOptions.SectionName);

        // The class default, on its own.
        Assert.False(new ProofreadPromptOptions().OverlapReferentLicence);

        // The shipped appsettings value, bound through the real configuration binder.
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var section = config.GetSection(ProofreadPromptOptions.SectionName);
        Assert.True(section.Exists(),
            "the Ai:ProofreadPrompt section is missing from the shipped appsettings.json, so the switch " +
            "is undocumented where an operator would look for it");

        var bound = new ProofreadPromptOptions();
        section.Bind(bound);
        Assert.False(bound.OverlapReferentLicence,
            "the SHIPPED configuration enables an arm that failed its own decision rule. It may only be " +
            "turned on by a plan that re-measured it on the real-prose surface WITH a recall floor.");

        // THE CLOSURE IS PINNED AT THE OPERATOR-FACING SITE. The ARM A precision lead was re-opened
        // twice from open-sounding config phrasing ("stays OFF until that lead is confirmed"); the
        // 2026-08-05 real-prose measurement then closed it NEGATIVE (5/12, p=0.363, DO NOT SHIP - see
        // RealProseArmMeasurement). This asserts the shipped comment states that closure and cannot
        // silently drift back to the old awaiting-measurement wording that invites a third opening.
        var rawJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

        // Non-vacuity guard: the key must exist in the raw text, so a renamed or deleted comment key
        // cannot make the wording checks below pass over nothing.
        Assert.Contains("\"_comment_ProofreadPrompt\"", rawJson, StringComparison.Ordinal);

        var comment = config["Ai:_comment_ProofreadPrompt"];
        Assert.False(string.IsNullOrWhiteSpace(comment),
            "the _comment_ProofreadPrompt value is empty; the operator-facing closure record is gone");

        Assert.Contains("CLOSED", comment, StringComparison.Ordinal);
        Assert.Contains("DO NOT", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("until that precision lead is confirmed", comment, StringComparison.Ordinal);
    }

    /// <summary>
    /// The anchor the arm extends must occur EXACTLY ONCE in each proofread body, and the arm must
    /// throw rather than silently no-op if it ever stops matching.
    ///
    /// This is the failure this whole file exists to make loud: an arm that renders nothing while the
    /// switch reports ON produces an OFF measurement under an ON label, which is indistinguishable from
    /// a genuine null result and would retire the lead for the wrong reason.
    /// </summary>
    [Fact]
    public void TheAnchorLine_OccursExactlyOnceInEachProofreadBody()
    {
        var off = new PromptFactory();

        foreach (var (lang, anchor) in new[]
                 {
                     ("he-IL", PromptFactory.ProofreadHeContextBeforeLine),
                     ("en-US", PromptFactory.ProofreadEnContextBeforeLine)
                 })
        {
            var body = off.BuildProofreadChunkPrompt(lang, null, null);
            Assert.Equal(1, Occurrences(body, anchor));

            // ...and it is not the OTHER language's anchor, i.e. the two constants are not swapped.
            var other = lang.StartsWith("he", StringComparison.OrdinalIgnoreCase)
                ? PromptFactory.ProofreadEnContextBeforeLine
                : PromptFactory.ProofreadHeContextBeforeLine;
            Assert.Equal(0, Occurrences(body, other));
        }
    }

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
}
