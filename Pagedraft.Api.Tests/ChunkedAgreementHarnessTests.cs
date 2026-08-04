using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC, NO-MODEL, NO-GPU gate on the chunked-agreement HARNESS (c1). The fixtures are
/// gated next door; this file gates the INSTRUMENT that will carry g1's attribution.
///
/// WHAT IT PINS, and why each of these is the difference between an attribution and a shrug:
///  1. The harness reaches the REAL chunked path through the REAL public entry point
///     (<c>UnifiedAnalysisService.RunAsync</c>), not by reflecting into the private
///     <c>RunProofreadChunkedAsync</c>, and it captures exactly one prompt per chunk IN CHUNK ORDER
///     (proved against the chunker's own output, not against the recording order alone).
///  2. Every per-chunk prompt carries the <c>[CHARACTER_REGISTER]</c> block AND the ProofreadHe body
///     that tells the model what the block is for. This settles the "register missing from some
///     chunks" hypothesis OUTRIGHT - it needs no model and no inference.
///  3. The captured <c>[CONTEXT_BEFORE]</c> content is exactly the chunker's <c>OverlapPrefix</c>, so
///     "what did the overlap carry" is a measurement rather than a guess.
///  4. The separation A/B is visible IN THE COMPOSED PROMPT: fixture 01's error chunk mentions the
///     character nowhere at all, fixture 02's carries it through the overlap.
///  5. The replay seam works, and a per-chunk correction survives the merge into the persisted
///     result - which is what g1 scores off.
///  6. The single-chunk control rides the single-shot path on a BYTE-IDENTICAL register surface to a
///     first chunk's, which is what makes it a control rather than a different experiment.
///
/// HOW g1 SWAPS IN THE LIVE ROUTER: <c>ChunkedAgreementHarness.RunAsync(fixture, inner: liveRouter)</c>.
/// One argument. See ChunkedAgreementHarness.cs's header.
/// </summary>
public class ChunkedAgreementHarnessTests
{
    // ── 1. the harness really drives the chunked path, and the capture is per chunk in order ─────

    [Fact]
    public async Task TheHarness_DrivesTheRealChunkedPath_AndCapturesOnePromptPerChunkInOrder()
    {
        var offenders = new List<string>();
        var fixturesChecked = 0;

        foreach (var fixture in ChunkedAgreementFixtures.MultiChunk)
        {
            fixturesChecked++;
            var run = await ChunkedAgreementHarness.RunAsync(fixture);

            if (!run.RanChunked)
                offenders.Add($"{fixture.Id}: ModelName is '{run.Result.ModelName}', not the 'chunked' " +
                              "sentinel, so RunAsync took the SINGLE-SHOT branch and this fixture never " +
                              "exercised the regime it exists to measure");

            if (run.Calls.Count != run.Chunks.Count)
                offenders.Add($"{fixture.Id}: {run.Calls.Count} model call(s) for {run.Chunks.Count} chunk(s)");

            // ORDER, proved against the chunker rather than assumed from the recording order.
            var capturedTexts = run.Calls.Select(c => c.ChunkText).ToArray();
            var chunkerTexts = run.Chunks.Select(c => c.Text).ToArray();
            if (!capturedTexts.SequenceEqual(chunkerTexts, StringComparer.Ordinal))
                offenders.Add($"{fixture.Id}: the captured chunk texts are not the chunker's chunk texts " +
                              "in order, so the per-chunk matrix would be attributed to the wrong chunks");

            // ...and every capture SAYS which chunk it is, agreeing with the text it carries. On a clean
            // run the identity resolution and the position coincide; the point is that the claim is
            // recorded, so the failure case (next test) has something to be wrong about.
            foreach (var call in run.Calls)
                if (call.ChunkIndex < 0 || call.ChunkIndex >= run.Chunks.Count ||
                    !string.Equals(run.Chunks[call.ChunkIndex].Text, call.ChunkText, StringComparison.Ordinal))
                    offenders.Add($"{fixture.Id}: the capture at CallIndex={call.CallIndex} reports " +
                                  $"ChunkIndex={call.ChunkIndex}, which is not the chunk whose text it carries");
        }

        Assert.True(fixturesChecked > 0, "no chunked fixture was run");
        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    // ── 2. the register is present in EVERY chunk (settles the plumbing hypothesis) ──────────────

    /// <summary>
    /// THE PLUMBING HYPOTHESIS, settled with no model. If <c>[CHARACTER_REGISTER]</c> were missing
    /// from some chunk's composed prompt, that chunk could not possibly resolve gender and the cause
    /// would be plumbing rather than anything about the model. It is present in every chunk of every
    /// fixture, rendered by the production builder, and followed by the ProofreadHe sentence that
    /// tells the model what the block is FOR - without which a register-only miss would say nothing
    /// about the model at all.
    /// </summary>
    [Fact]
    public async Task EveryPerChunkPrompt_CarriesTheRegisterBlock_AndTheBodyThatExplainsIt()
    {
        const string registerUsageSentence = "אם מופיע [CHARACTER_REGISTER] — השתמש בו לאימות התאמת מין";

        var offenders = new List<string>();
        var promptsChecked = 0;

        foreach (var fixture in ChunkedAgreementFixtures.MultiChunk)
        {
            var run = await ChunkedAgreementHarness.RunAsync(fixture);
            Assert.NotEmpty(run.Calls);

            foreach (var call in run.Calls)
            {
                promptsChecked++;

                if (!call.HasCharacterRegisterBlock)
                {
                    offenders.Add($"{fixture.Id} chunk {call.CallIndex}: NO [CHARACTER_REGISTER] block");
                    continue;
                }

                foreach (var entry in fixture.Register)
                {
                    var line = $"- {entry.Name} [{entry.Gender}]";
                    if (!call.CharacterRegisterBlock!.Contains(line, StringComparison.Ordinal))
                        offenders.Add($"{fixture.Id} chunk {call.CallIndex}: the register block does not " +
                                      $"render '{line}'");
                }

                if (!call.Instruction.Contains(registerUsageSentence, StringComparison.Ordinal))
                    offenders.Add($"{fixture.Id} chunk {call.CallIndex}: the ProofreadHe body that explains " +
                                  "what [CHARACTER_REGISTER] is for is missing, so the block is " +
                                  "uninterpretable and a register-only miss would not be attributable");

                // The block leads the instruction, exactly as PromptFactory.BuildProofreadChunkPrompt
                // composes it (register, then optional overlap, then the body).
                if (!call.Instruction.StartsWith("[CHARACTER_REGISTER]", StringComparison.Ordinal))
                    offenders.Add($"{fixture.Id} chunk {call.CallIndex}: the instruction does not START with " +
                                  "the register block; the composed order moved");
            }
        }

        Assert.True(promptsChecked > 0, "no per-chunk prompt was inspected");
        Assert.True(offenders.Count == 0,
            "The character register is not reaching every chunk's composed prompt. That is the " +
            "'register missing from some chunks' cause, and it is a PLUMBING defect:\n  " +
            string.Join("\n  ", offenders));
    }

    // ── 3. the captured overlap is the chunker's overlap ─────────────────────────────────────────

    /// <summary>
    /// "What did the overlap carry" is one of the four things g1 reports per chunk, so the captured
    /// <c>[CONTEXT_BEFORE]</c> content has to be the chunker's own <c>OverlapPrefix</c> and nothing
    /// else. Chunk 0 must carry none (the chunker builds one only for i &gt; 0), which is also why the
    /// section is ABSENT rather than empty there - <c>AppendSection</c> skips blank content.
    /// </summary>
    [Fact]
    public async Task EveryCapturedOverlapPrefix_IsExactlyTheChunkersOwnOverlapPrefix()
    {
        var offenders = new List<string>();
        var withOverlap = 0;
        var withoutOverlap = 0;

        foreach (var fixture in ChunkedAgreementFixtures.MultiChunk)
        {
            var run = await ChunkedAgreementHarness.RunAsync(fixture);

            for (var i = 0; i < run.Chunks.Count; i++)
            {
                var expected = run.Chunks[i].OverlapPrefix?.Trim();
                var captured = run.Calls[i].OverlapPrefix;

                if (string.IsNullOrEmpty(expected))
                {
                    withoutOverlap++;
                    if (captured is not null)
                        offenders.Add($"{fixture.Id} chunk {i}: carried a [CONTEXT_BEFORE] section the " +
                                      "chunker did not produce");
                    continue;
                }

                withOverlap++;
                if (!string.Equals(expected, captured, StringComparison.Ordinal))
                    offenders.Add($"{fixture.Id} chunk {i}: the captured overlap differs from the chunker's " +
                                  $"OverlapPrefix\n    chunker : {expected}\n    captured: {captured ?? "(none)"}");
            }
        }

        // Non-vacuity, both directions: a run where nothing carried an overlap would satisfy the
        // equality loop for free, and so would one where everything did.
        Assert.True(withOverlap > 0, "no chunk carried an overlap prefix, so the equality proved nothing");
        Assert.True(withoutOverlap > 0, "no chunk lacked an overlap prefix, so the absence case is untested");
        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    // ── 4. the separation A/B, measured on the composed prompt ───────────────────────────────────

    /// <summary>
    /// THE ATTRIBUTION INSTRUMENT, working. For the two separation fixtures this asserts what the
    /// model can actually SEE:
    ///
    ///  - fixture 01's error chunk: the character name occurs NOWHERE in its composed prompt - not in
    ///    the chunk text, not in the overlap. The register block is present but names someone the
    ///    chunk never mentions, so it is inapplicable by construction. A failure there is a
    ///    SEPARATION failure and cannot be read as long-context inattention.
    ///  - fixture 02's error chunk: the same span, the same register, but the name arrives through the
    ///    [CONTEXT_BEFORE] overlap. If this one passes while 01 fails, the indicated fix is referent
    ///    carry-forward, NOT the obligation rendering Wave 2 plans first.
    ///
    /// This is the claim the plan's attribution table's second row is decided on, and it is measured
    /// here with zero model calls.
    /// </summary>
    [Fact]
    public async Task TheSeparationPair_DiffersInTheComposedPrompt_ExactlyWhereTheHypothesisSaysItShould()
    {
        var separated = await ChunkedAgreementHarness.RunAsync(
            ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.SeparatedAndDilutedId));
        var inOverlap = await ChunkedAgreementHarness.RunAsync(
            ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.AntecedentInOverlapId));

        var name = ChunkedAgreementFixtures.CharacterName;

        // ErrorChunkCall is NULLABLE by design (a chunk whose call threw produces no capture). On these
        // deterministic runs nothing throws, so an absence here is a harness defect and is named as one
        // rather than surfacing later as a NullReferenceException on an unrelated line.
        var separatedErrorCall = RequireErrorChunkCall(separated);
        var inOverlapErrorCall = RequireErrorChunkCall(inOverlap);

        // ...and it really is the declared error chunk, resolved by identity rather than by position.
        Assert.Equal(separated.Fixture.ExpectedErrorChunkIndex, separatedErrorCall.ChunkIndex);
        Assert.Equal(inOverlap.Fixture.ExpectedErrorChunkIndex, inOverlapErrorCall.ChunkIndex);

        // Both error chunks really do hold the error (otherwise the rest is about the wrong chunk).
        Assert.True(separatedErrorCall.ChunkContains(separated.Fixture.ErrorSpan));
        Assert.True(inOverlapErrorCall.ChunkContains(inOverlap.Fixture.ErrorSpan));

        // 01: the name is nowhere in the chunk's own text, nowhere in its overlap.
        Assert.False(separatedErrorCall.ChunkContains(name));
        Assert.False(separatedErrorCall.OverlapContains(name));
        // ...and the ONLY occurrence anywhere in that prompt would have to be the register block, so
        // strip the block and assert the rest of the prompt never mentions the character.
        Assert.DoesNotContain(name, WithoutRegisterBlock(separatedErrorCall), StringComparison.Ordinal);
        Assert.DoesNotContain(name, separatedErrorCall.ChunkText, StringComparison.Ordinal);
        // The register IS there - "inapplicable", not "missing".
        Assert.Contains(name, separatedErrorCall.CharacterRegisterBlock!, StringComparison.Ordinal);

        // 02: the name arrives through the overlap and nowhere else in the prose.
        Assert.True(inOverlapErrorCall.OverlapContains(name));
        Assert.False(inOverlapErrorCall.ChunkContains(name));
        Assert.Contains(name, WithoutRegisterBlock(inOverlapErrorCall), StringComparison.Ordinal);
    }

    /// <summary>The run's error-chunk capture, failing with a readable message when it is absent.</summary>
    private static ChunkPromptCapture RequireErrorChunkCall(ChunkedAgreementRun run)
    {
        var call = run.ErrorChunkCall;
        Assert.True(call is not null,
            $"{run.Fixture.Id}: no capture reports ChunkIndex={run.Fixture.ExpectedErrorChunkIndex}, so the " +
            $"error chunk was never recorded. Captured chunk indexes: " +
            $"[{string.Join(", ", run.Calls.Select(c => c.ChunkIndex))}]; failures: " +
            $"[{string.Join(", ", run.Failures.Select(f => f.ChunkIndex))}].");
        return call!;
    }

    /// <summary>The composed prompt with the [CHARACTER_REGISTER] section removed, so a mention in the
    /// PROSE (overlap or chunk) can be distinguished from the register's own rendering of the name.</summary>
    private static string WithoutRegisterBlock(ChunkPromptCapture call)
    {
        var block = call.CharacterRegisterBlock;
        var prompt = call.Instruction + "\n" + call.WrappedInputText;
        return block is null ? prompt : prompt.Replace(block, "", StringComparison.Ordinal);
    }

    // ── 5. the replay seam, and the merge that g1 scores off ─────────────────────────────────────

    /// <summary>
    /// The replay seam is what makes the harness testable with no model, and it is the same seam g1
    /// replaces with the live router. This drives "a model that got it right": the chunk carrying the
    /// error comes back with the fix applied, every other chunk is echoed. The persisted result must
    /// then contain the corrected sentence, must NOT contain the erroneous one, and must surface the
    /// change as EXACTLY the agreement suggestions - i.e. a per-chunk edit really does survive the
    /// merge into the artifact g1 will score.
    ///
    /// The result is compared against <c>ExpectedMergedResult</c> rather than the fixture's own
    /// <c>ExpectedCorrectedText</c> because the pipeline ALSO collapses blank lines on the way out
    /// (see <see cref="ChunkedAgreementSanitizerArtifactTests"/>). Comparing against the raw expected
    /// text would fail for a reason that has nothing to do with the agreement repair.
    /// </summary>
    [Fact]
    public async Task TheReplaySeam_CarriesAPerChunkCorrection_IntoThePersistedMergedResult()
    {
        foreach (var fixture in ChunkedAgreementFixtures.All)
        {
            var run = await ChunkedAgreementHarness.RunAsync(
                fixture, replay: ChunkedAgreementHarness.ReplayCorrectFix(fixture));

            Assert.Contains(fixture.ExpectedFix, run.Result.ResultText, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.ErrorSpan, run.Result.ResultText, StringComparison.Ordinal);
            Assert.Equal(
                run.ExpectedMergedResult(chunk =>
                    chunk.Replace(fixture.ErrorSpan, fixture.ExpectedFix, StringComparison.Ordinal)),
                run.Result.ResultText);

            // EXACTLY the agreement repair, once the pipeline's whitespace artifact is set aside:
            // the pronoun and the verb. Not "at least one" - a floor would pass while the diff
            // fragmented or duplicated the repair.
            var substantive = run.SubstantiveSuggestions
                .Select(s => $"{s.OriginalText}->{s.SuggestedText}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "הגיב->הגיבה", "הוא->היא" }, substantive);

            Assert.False(run.Result.ProofreadResultUnreliable,
                $"{fixture.Id}: a correctly-repaired run was flagged unreliable");
        }
    }

    /// <summary>
    /// The harness's OWN no-op control, and the reason a non-zero over-correction number in g1 can be
    /// attributed to the model at all: with the echo replay the model changes nothing, so the merged
    /// result must carry NO substantive suggestion whatsoever.
    ///
    /// It is NOT byte-identical to the input, and that is a PRODUCTION fact rather than a harness
    /// artifact - the response sanitizer collapses every blank line. What is asserted instead is the
    /// exact reconstruction: per-chunk blank-line collapse, rejoined by the chunker's own separators.
    /// Anything beyond that would be an edit nobody made.
    /// </summary>
    [Fact]
    public async Task TheEchoReplay_MakesNoSubstantiveEdit_AndDiffersFromTheInputOnlyByTheSanitizersBlankLineCollapse()
    {
        foreach (var fixture in ChunkedAgreementFixtures.All)
        {
            var run = await ChunkedAgreementHarness.RunAsync(fixture);

            Assert.Equal(run.ExpectedMergedResult(), run.Result.ResultText);
            Assert.Empty(run.SubstantiveSuggestions);
            Assert.True(run.Result.ProofreadNoChangesHint,
                $"{fixture.Id}: an untouched round trip did not raise the no-changes hint");
        }
    }

    // ── 6. the control's surface ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CONTROL IS A CONTROL ONLY IF ITS PROMPT SURFACE MATCHES. A one-chunk run of the chunked
    /// path is not reachable through the public entry point for non-dialogue prose (pinned in
    /// <c>ChunkedAgreementFixtureTests</c>), so the control rides the SINGLE-SHOT path. That is
    /// legitimate here for one specific reason, asserted rather than assumed: with a context carrying
    /// ONLY <c>Characters</c>, <c>GetAnalysisPrompt(Proofread, lang, context)</c> and
    /// <c>BuildProofreadChunkPrompt(lang, characters, overlapPrefix: null)</c> compose the SAME
    /// string - register block, then the ProofreadHe body.
    ///
    /// The one difference that remains is the INPUT: the chunked path wraps each chunk in
    /// <c>[TEXT_TO_CORRECT]</c> markers and the single-shot path does not. ProofreadHe handles both
    /// explicitly ("if there are no such markers, correct all the text"), and it is recorded here so
    /// g1 states it rather than discovering it.
    /// </summary>
    [Fact]
    public async Task TheSingleChunkControl_RidesTheSingleShotPath_OnTheSameRegisterSurfaceAsAFirstChunk()
    {
        var control = ChunkedAgreementFixtures.Control;
        var run = await ChunkedAgreementHarness.RunAsync(control);

        Assert.False(run.RanChunked,
            "the control routed to the chunked path; it is sized to stay under the Hebrew target");
        var call = Assert.Single(run.Calls);

        var chunkSurface = new PromptFactory().BuildProofreadChunkPrompt(
            control.Language,
            new CharacterRegister { Characters = control.Register },
            overlapPrefix: null);

        Assert.Equal(chunkSurface, call.Instruction);
        Assert.True(call.HasCharacterRegisterBlock);
        Assert.Equal(AiTaskType.Proofread, call.TaskType);

        // The identity-based chunk resolution covers the single-shot case too: the control's input is the
        // WHOLE un-wrapped target text, which the chunker returns as its one (trimmed) chunk.
        Assert.Equal(0, call.ChunkIndex);
        Assert.Single(run.Chunks);

        // The documented difference: the single-shot input is NOT wrapped.
        Assert.Equal(control.Text, call.WrappedInputText);
        Assert.DoesNotContain(RecordingChunkRouter.TextToCorrectOpen, call.WrappedInputText, StringComparison.Ordinal);

        // ...and a chunked fixture's first chunk IS wrapped, so the difference is real and one-sided.
        var chunked = await ChunkedAgreementHarness.RunAsync(
            ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.DilutionOnlyId));
        Assert.Contains(RecordingChunkRouter.TextToCorrectOpen, chunked.Calls[0].WrappedInputText, StringComparison.Ordinal);
        Assert.Equal(chunked.Chunks[0].Text, chunked.Calls[0].ChunkText);
    }

    // ── 7. the recording router itself ───────────────────────────────────────────────────────────

    /// <summary>
    /// The capture helpers are the instrument's own reading glasses, so they get their own pin: a
    /// section that is absent reads as null (not as an empty string, which would be reported as "the
    /// overlap carried nothing" instead of "there was no overlap"), and the unwrapper returns the
    /// chunk without its markers - and returns unwrapped input untouched.
    /// </summary>
    [Fact]
    public void TheCaptureHelpers_DistinguishAnAbsentSectionFromAnEmptyOne_AndUnwrapOnlyWhenWrapped()
    {
        const string withBoth =
            "[CHARACTER_REGISTER]\n- רוני [female]\n[/CHARACTER_REGISTER]\n\n" +
            "[CONTEXT_BEFORE]\nמשפט קודם.\n[/CONTEXT_BEFORE]\n\n" +
            "גוף ההוראה.";

        Assert.Equal("- רוני [female]", RecordingChunkRouter.Section(withBoth, "CHARACTER_REGISTER"));
        Assert.Equal("משפט קודם.", RecordingChunkRouter.Section(withBoth, "CONTEXT_BEFORE"));
        Assert.Null(RecordingChunkRouter.Section(withBoth, "STYLE_PROFILE"));
        Assert.Null(RecordingChunkRouter.Section("no sections at all", "CONTEXT_BEFORE"));

        Assert.Equal("טקסט", RecordingChunkRouter.Unwrap("[TEXT_TO_CORRECT]טקסט[/TEXT_TO_CORRECT]"));
        Assert.Equal("טקסט לא עטוף", RecordingChunkRouter.Unwrap("טקסט לא עטוף"));
    }

    /// <summary>
    /// THE BUG THIS INSTRUMENT ACTUALLY HAD, kept as a regression pin. ProofreadHe's own prose names
    /// the section markers ("אם מופיע [CONTEXT_BEFORE]...[/CONTEXT_BEFORE] — זהו הקשר בלבד"), so a
    /// first-occurrence scan reported a CONTEXT_BEFORE section - with the content "..." - on chunk 0,
    /// which carries no overlap at all. An attribution instrument that invents an overlap is worse
    /// than none: it would have told g1 that the first chunk received context it never received.
    /// </summary>
    [Fact]
    public void TheSectionReader_IgnoresTheMarkerNamesTheProofreadBodyMentionsInItsOwnProse()
    {
        var bodyOnly = new PromptFactory().BuildProofreadChunkPrompt("he-IL", characters: null, overlapPrefix: null);

        // The body really does mention both markers - otherwise this test guards nothing.
        Assert.Contains("[CONTEXT_BEFORE]", bodyOnly, StringComparison.Ordinal);
        Assert.Contains("[CHARACTER_REGISTER]", bodyOnly, StringComparison.Ordinal);

        Assert.Null(RecordingChunkRouter.Section(bodyOnly, "CONTEXT_BEFORE"));
        Assert.Null(RecordingChunkRouter.Section(bodyOnly, "CHARACTER_REGISTER"));
        Assert.Empty(RecordingChunkRouter.LeadingSections(bodyOnly));

        // ...and a REAL leading section is still read, in front of that same body.
        var withOverlap = new PromptFactory().BuildProofreadChunkPrompt(
            "he-IL", characters: null, overlapPrefix: "משפט הקשר.");
        Assert.Equal("משפט הקשר.", RecordingChunkRouter.Section(withOverlap, "CONTEXT_BEFORE"));
        Assert.Single(RecordingChunkRouter.LeadingSections(withOverlap));
    }

    // ── 8. a FAILED chunk must not shift the attribution of the surviving ones ───────────────────

    /// <summary>
    /// THE CORRELATION BUG, pinned. <c>RunProofreadChunkedAsync</c> catches every per-chunk exception
    /// (UnifiedAnalysisService.cs:2293 - "Proofread chunk {Index} failed; using original text"), records
    /// a <c>FallbackError</c>, merges the ORIGINAL chunk text and CARRIES ON to the other chunks. So one
    /// failed chunk leaves <c>Calls</c> STRICTLY SHORTER than <c>Chunks</c>, and every consumer that
    /// correlated the two POSITIONALLY attributed a capture to the wrong chunk: the per-chunk JSONL row
    /// labelled <c>chunkIndex: 0</c> carried chunk 1's register / overlap / response, the published
    /// <c>chunk0.txt</c> artifact held chunk 1's composed prompt, and <c>ErrorChunkCall</c> either threw
    /// <c>ArgumentOutOfRangeException</c> or returned a neighbour.
    ///
    /// That mis-labelled evidence is published BEFORE the live run's transport tripwire voids the
    /// agreement verdict, so voiding does not save it: the audit trail is on disk and reads as
    /// authoritative. Correlation is therefore by IDENTITY (<see cref="ChunkPromptCapture.ChunkIndex"/>,
    /// resolved from the chunk text) and this test drives the exact regime - first call throws, the rest
    /// echo - that makes position and identity disagree.
    /// </summary>
    [Fact]
    public async Task AFailedChunk_DoesNotShiftTheChunkAttribution_OfTheSurvivingCaptures()
    {
        // Error in the LAST chunk, so the error chunk SURVIVES the first-call failure - case (iii).
        var fixture = ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.SeparatedAndDilutedId);
        var run = await ChunkedAgreementHarness.RunAsync(fixture, inner: new ThrowOnFirstChunkRouter());

        Assert.True(run.RanChunked, "the failure regime must be measured on the chunked path");
        Assert.True(run.Chunks.Count > 2, $"{fixture.Id} realized {run.Chunks.Count} chunk(s); the " +
                                          "positional/identity disagreement needs a chunk AFTER the failed one");

        // The regime itself: production swallowed the throw and carried on, so there is exactly one
        // capture FEWER than there are chunks. Without this the rest could pass for free.
        Assert.Equal(run.Chunks.Count - 1, run.Calls.Count);

        // (i) exactly one failure, naming the chunk that ACTUALLY failed - not a call ordinal.
        var failure = Assert.Single(run.Failures);
        Assert.Equal(0, failure.ChunkIndex);
        Assert.Equal(run.Chunks[0].Text, failure.ChunkText);
        Assert.Equal(nameof(InvalidOperationException), failure.ExceptionType);

        // (ii) every SURVIVING capture reports the chunk index of the text it actually carries.
        foreach (var call in run.Calls)
        {
            var carries = IndexOfChunkText(run, call.ChunkText);
            Assert.True(
                call.ChunkIndex == carries &&
                call.ChunkIndex >= 0 && call.ChunkIndex < run.Chunks.Count &&
                string.Equals(run.Chunks[call.ChunkIndex].Text, call.ChunkText, StringComparison.Ordinal),
                $"MIS-ATTRIBUTED CAPTURE: the capture at CallIndex={call.CallIndex} reports " +
                $"ChunkIndex={call.ChunkIndex} but it carries the text of chunk {carries}. Chunk 0's call " +
                "threw, so production merged the original text and carried on and this capture list is " +
                $"{run.Calls.Count} long for {run.Chunks.Count} chunks. Correlating the two by POSITION " +
                "shifts every surviving chunk down one, and the per-chunk row / prompt artifact / " +
                "ErrorChunkCall then all describe the wrong chunk.");
        }

        // ...and position really does disagree here, so (ii) is not satisfiable by an identity mapping.
        Assert.NotEqual(run.Chunks[0].Text, run.Calls[0].ChunkText);

        // (iii) the error chunk SURVIVED, so ErrorChunkCall still resolves - to the right chunk.
        var errorCall = run.ErrorChunkCall;
        Assert.True(errorCall is not null,
            $"{fixture.Id}: chunk {fixture.ExpectedErrorChunkIndex} succeeded, yet ErrorChunkCall found no " +
            $"capture for it. Captured chunk indexes: [{string.Join(", ", run.Calls.Select(c => c.ChunkIndex))}]");
        Assert.Equal(fixture.ExpectedErrorChunkIndex, errorCall!.ChunkIndex);
        Assert.Equal(run.Chunks[fixture.ExpectedErrorChunkIndex].Text, errorCall.ChunkText);
        Assert.True(errorCall.ChunkContains(fixture.ErrorSpan),
            "ErrorChunkCall resolved to a chunk that does not carry the fixture's error span");

        // (iv) when the error chunk is the one that FAILED, the absence is reported EXPLICITLY - not as a
        // neighbour's capture wearing the error chunk's label.
        var errorInFirstChunk = ChunkedAgreementFixtures.ById(ChunkedAgreementFixtures.DilutionOnlyId);
        Assert.Equal(0, errorInFirstChunk.ExpectedErrorChunkIndex);

        var failedErrorChunk = await ChunkedAgreementHarness.RunAsync(
            errorInFirstChunk, inner: new ThrowOnFirstChunkRouter());

        Assert.Equal(0, Assert.Single(failedErrorChunk.Failures).ChunkIndex);
        Assert.NotEmpty(failedErrorChunk.Calls);
        Assert.True(failedErrorChunk.ErrorChunkCall is null,
            "the error chunk's own call THREW, so there is no capture for it; ErrorChunkCall returned " +
            $"a capture for chunk {failedErrorChunk.ErrorChunkCall?.ChunkIndex} instead of signalling the " +
            "absence, which is exactly the neighbour-borrowing the positional lookup did.");

        // The run still produced a result (production's fallback merged the original text), so the
        // absence above is an ATTRIBUTION gap and not a dead run.
        Assert.False(string.IsNullOrEmpty(failedErrorChunk.Result.ResultText));
    }

    /// <summary>The index of the chunk whose text is <paramref name="run"/>'s, or -1. Deliberately
    /// independent of the router's own resolution so the assertion is a check, not a restatement.</summary>
    private static int IndexOfChunkText(ChunkedAgreementRun run, string chunkText)
    {
        for (var i = 0; i < run.Chunks.Count; i++)
            if (string.Equals(run.Chunks[i].Text, chunkText, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>
    /// Throws on the FIRST call and echoes the input afterwards - the minimal reproduction of the
    /// 2026-08-03 shape (a chunk 500s while the others succeed). It sits in the <c>inner</c> slot, so the
    /// recording router records the throw and RETHROWS it unchanged and production's own per-chunk catch
    /// still runs: the pipeline behaves exactly as the product does.
    /// </summary>
    private sealed class ThrowOnFirstChunkRouter : IAiRouter
    {
        private int _n;

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _n);
            if (n == 1) throw new InvalidOperationException("simulated chunk 500");
            return Task.FromResult(new AiResponse
            {
                Content = request.InputText,
                Provider = "probe",
                Model = "probe"
            });
        }

        public async IAsyncEnumerable<string> StreamCompleteAsync(
            AiRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
