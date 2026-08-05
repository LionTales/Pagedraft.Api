using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// g1 SCOPE (i): the LIVE consumer of c1's chunked-agreement instrument.
///
/// It drives <see cref="ChunkedAgreementHarness.RunAsync"/> with the real
/// <c>Ollama / gemma4:12b</c> router in the <c>inner</c> slot - the one-argument swap c1 designed for -
/// so the fixtures run through the REAL chunked production path (<c>UnifiedAnalysisService.RunAsync</c>
/// -> <c>RunProofreadChunkedAsync</c>) with every per-chunk prompt captured.
///
/// WHY IT LIVES HERE AND IS SHAPED LIKE THIS.
///  - Namespace <c>Pagedraft.Api.Tests.LanguageEngine</c> + <c>[Trait("Category","LiveModel")]</c>: the
///    standing GPU-safe filter (<c>FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine</c>) excludes
///    it, and <c>LiveHarnessNamespaceGuardTests</c> enforces the trait so it can never become a
///    deterministic test hiding in the excluded namespace.
///  - SKIP-BY-DEFAULT on Ollama reachability, through the SAME probe the gold harness uses.
///  - EVERY result is appended to a JSONL artifact the moment it lands, and every composed per-chunk
///    prompt is written out verbatim. A mid-run death therefore leaves evidence rather than nothing,
///    and the attribution can be AUDITED instead of trusted (the todo requires exactly that).
///
/// DRIVEN BY ENV VARS so the parent can batch repetitions without recompiling:
///   G1_ARTIFACT_DIR   required; where the JSONL + prompt captures are written.
///   G1_FIXTURE_IDS    comma-separated fixture ids (default: all four, in the corpus's order).
///   G1_REPS           repetitions per fixture in THIS invocation (default 1).
///   G1_REP_OFFSET     first repetition number to stamp (default 1), so batched invocations do not
///                     collide in the artifact stream.
/// </summary>
public class ChunkedAgreementLiveTests
{
    private readonly ITestOutputHelper _output;

    public ChunkedAgreementLiveTests(ITestOutputHelper output) => _output = output;

    /// <summary>Hebrew gershayim U+05F4 - the punctuation phenomenon scope (ii) tracks.</summary>
    private const char Gershayim = '״';

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [Fact]
    [Trait("Category", "LiveModel")]
    public async Task ChunkedAgreement_LiveAttributionRun()
    {
        var dir = Environment.GetEnvironmentVariable("G1_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            _output.WriteLine("SKIPPED: G1_ARTIFACT_DIR is not set. This is g1's measurement consumer; " +
                              "it refuses to run without an artifact directory, because a live run whose " +
                              "per-chunk prompts are not published cannot be audited.");
            return;
        }

        if (!await ProofreadQualityTests.IsOllamaReachableAsync())
        {
            _output.WriteLine("SKIPPED: Ollama not reachable. Live measurement needs a live model.");
            return;
        }

        Directory.CreateDirectory(dir);
        var promptDir = Path.Combine(dir, "prompts");
        Directory.CreateDirectory(promptDir);
        var jsonlPath = Path.Combine(dir, "chunked-agreement.jsonl");

        var reps = ParseInt(Environment.GetEnvironmentVariable("G1_REPS"), 1);
        var repOffset = ParseInt(Environment.GetEnvironmentVariable("G1_REP_OFFSET"), 1);
        var fixtures = ResolveFixtures(Environment.GetEnvironmentVariable("G1_FIXTURE_IDS"));

        Assert.True(fixtures.Count > 0, "G1_FIXTURE_IDS selected no fixture; the run would be vacuous.");

        // ONE binding for the provider/model this run actually uses, read by BOTH the router wiring and
        // the floor gate below. Restating "Ollama" at the gate would let the two drift, which is the
        // same failure mode the gold harness avoids by resolving its provider through the parser
        // CreateRouter uses instead of re-declaring it.
        var provider = "Ollama";
        var model = ProofreadQualityTests.ProofreadModel;
        var router = ProofreadQualityTests.CreateRouter(provider, model);
        // THE SAME POSTURE THE HARNESS USES, not a second construction: this diff produces the
        // per-chunk edit rows while ChunkedAgreementHarness produces the merged-result rows on the SAME
        // run, so two independently-defaulted services would publish a row whose halves were computed
        // under different postures. Guard OFF, argued on
        // ChunkedAgreementHarness.MeasurementDiffService.
        var diff = ChunkedAgreementHarness.MeasurementDiffService();

        _output.WriteLine($"=== g1 scope (i): chunked agreement, live {provider}/{model} ===");
        _output.WriteLine($"artifacts: {dir}");
        _output.WriteLine($"fixtures : {string.Join(", ", fixtures.Select(f => f.Id))}");
        _output.WriteLine($"reps     : {reps} starting at {repOffset}");

        // Per-fixture tallies for the STANDING FLOOR verdict at the end of the run. Read back out of
        // the persisted record rather than recomputed, so the number that is gated is the number that
        // was published.
        var tallies = fixtures.ToDictionary(f => f.Id, _ => new RunTally(), StringComparer.Ordinal);

        for (var r = 0; r < reps; r++)
        {
            var rep = repOffset + r;
            foreach (var fixture in fixtures)
            {
                var sw = Stopwatch.StartNew();
                ChunkedAgreementRun? run = null;
                string? fatal = null;
                try
                {
                    run = await ChunkedAgreementHarness.RunAsync(fixture, inner: router);
                }
                catch (Exception ex)
                {
                    // The single-shot control does NOT swallow a router throw (only the chunked path's
                    // per-chunk catch does), so a transport failure there surfaces here. Record it as a
                    // transport error, never as "the model failed to correct".
                    fatal = $"{ex.GetType().Name}: {First(ex.Message, 400)}";
                }
                sw.Stop();

                var record = fatal is not null
                    ? FatalRecord(fixture, rep, model, sw.ElapsedMilliseconds, fatal)
                    : Score(fixture, rep, model, run!, diff, sw.ElapsedMilliseconds);

                // APPEND IMMEDIATELY - a mid-run death must leave evidence.
                File.AppendAllText(jsonlPath, JsonSerializer.Serialize(record, Json) + Environment.NewLine,
                    new UTF8Encoding(false));

                if (run is not null)
                    WritePromptArtifacts(promptDir, fixture, rep, run);

                tallies[fixture.Id].Add(record);
                _output.WriteLine(Summarize(record));
            }
        }

        // Non-vacuity: the run must actually have written its artifact.
        Assert.True(File.Exists(jsonlPath), "no JSONL artifact was written; the run measured nothing");

        ReportAndGateTheStandingFloor(tallies, provider, model);
    }

    // ── the standing floor gate ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// What this invocation measured for one fixture, accumulated from the persisted records.
    /// </summary>
    private sealed class RunTally
    {
        public int Runs { get; private set; }
        public int Hits { get; private set; }
        public int Fatals { get; private set; }
        public int TransportFailures { get; private set; }
        public int WhitespaceOnly { get; private set; }
        public int OverCorrections { get; private set; }

        public void Add(Dictionary<string, object?> record)
        {
            Runs++;
            if (record.ContainsKey("fatal")) { Fatals++; return; }
            if (record.GetValueOrDefault("corrected") is true) Hits++;
            TransportFailures += (record.GetValueOrDefault("transportFailures") as Array)?.Length ?? 0;
            WhitespaceOnly += record.GetValueOrDefault("whitespaceOnlySuggestions") as int? ?? 0;
            OverCorrections += record.GetValueOrDefault("overCorrections") as int? ?? 0;
        }
    }

    /// <summary>
    /// THE GATE. c3 turned g1's one-off numbers into <see cref="ProofreadStandingFloor"/>, so this run
    /// is no longer only a report: every fixture's (hits, runs) is evaluated against the floor and a
    /// failure FAILS the test.
    ///
    /// ORDER MATTERS, and it is the 2026-08-03 lesson. The two instrument tripwires are checked FIRST
    /// and VOID the agreement verdict rather than being folded into it: a per-chunk transport failure
    /// merges the ORIGINAL text, which is byte-identical to "the model declined to correct", so a
    /// concurrency defect would otherwise be scored as a perfectly reproduced known defect. A run with
    /// a transport failure has not measured agreement at all and must say so instead of reporting a
    /// verdict it cannot support.
    ///
    /// Both directions fail: an ExpectedPass that drops, AND a KnownDefect that starts passing. The
    /// second is the point - a fix landing must not be indistinguishable from nothing happening.
    /// Artifacts are already on disk before this runs, so a failure here never costs the evidence.
    ///
    /// ONE SOURCE OF TRUTH FOR EACH TRIPWIRE. Both are evaluated by
    /// <c>ProofreadStandingFloor.EvaluateMetric</c> against their own declared bars
    /// (<c>ChunkedHarnessEvaluatedMetricIds</c>), not by a local comparison that restates the floor's
    /// threshold in a second place. The gold-surface bars have their own consumer in
    /// <c>ProofreadQualityTests</c>; this method owns exactly these two.
    ///
    /// MODEL-CONDITIONAL, LIKE THE GOLD-SURFACE HALF - AND SPLIT, BECAUSE NOT EVERYTHING HERE IS A
    /// MODEL CLAIM. The floor's semantics are that a provider/model swap VOIDS it rather than
    /// regressing it, and this gate used to ignore that: it asserted unconditionally, so the first run
    /// on a new model would have reported the swap as "THE STANDING FLOOR DID NOT HOLD" - a regression
    /// verdict on numbers the floor does not claim to describe. That is latent rather than live today,
    /// because the model is read from <c>ProofreadQualityTests.ProofreadModel</c> and a deterministic
    /// test pins that equal to <c>MeasuredOnModel</c>; it would have fired on precisely the event the
    /// model-conditional design exists to handle.
    ///
    /// The split matters and a blanket gate would have been wrong. The per-fixture AGREEMENT verdict is
    /// a claim about how a MODEL behaves, so it is gated. The two INSTRUMENT TRIPWIRES are not - the
    /// floor's own owner list calls them that. A per-chunk transport failure or a new asymmetric
    /// normalization means the run MEASURED NOTHING, which is equally true of any model, so they stay
    /// hard failures on every run. Voiding them along with the verdict would let a swapped-model
    /// session pass green on a broken instrument, which is the more expensive of the two mistakes.
    /// </summary>
    private void ReportAndGateTheStandingFloor(
        IReadOnlyDictionary<string, RunTally> tallies, string provider, string model)
    {
        var onMeasuredModel =
            string.Equals(provider, ProofreadStandingFloor.MeasuredOnProvider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(model, ProofreadStandingFloor.MeasuredOnModel, StringComparison.Ordinal);

        _output.WriteLine("");
        _output.WriteLine($"=== STANDING FLOOR (measured {ProofreadStandingFloor.MeasuredOn} on " +
                          $"{ProofreadStandingFloor.MeasuredOnProvider} / {ProofreadStandingFloor.MeasuredOnModel}) ===");
        _output.WriteLine($"this run: {provider} / {model}");
        if (!onMeasuredModel)
        {
            _output.WriteLine(
                $"REPORT ONLY for the agreement verdicts - this run is on {provider} / {model}, and a " +
                $"model swap VOIDS the floor rather than regressing it, so the pinned outcomes do not " +
                $"describe these numbers. Re-measure and re-pin. The two INSTRUMENT tripwires below " +
                "still fail hard: a transport failure or a whitespace-only suggestion means the run " +
                "measured nothing, which is true of any model.");
        }

        var failures = new List<string>();
        var evaluated = 0;

        foreach (var (fixtureId, tally) in tallies.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (tally.Runs == 0) continue;
            var entry = ProofreadStandingFloor.ForFixture(fixtureId);

            // tally.Runs > 0 is guaranteed by the guard immediately above, so both divisions below are
            // safe. Each rate goes through ProofreadStandingFloor.EvaluateMetric against its OWN bar
            // rather than being compared to a hand-written "> 0" here: the bars declare Exactly 0 per
            // run, and a second copy of that comparison living in this file is a gate that can drift
            // from the floor it claims to enforce (c3/be-c03 - both tripwires used to do exactly that).
            // The bar supplies the threshold and the unit; this method supplies only the observation.
            var transportBar = ProofreadStandingFloor.Metric("chunked.transportFailures");
            var transportFailuresPerRun = tally.TransportFailures / (double)tally.Runs;
            var transportVerdict = ProofreadStandingFloor.EvaluateMetric(transportBar, transportFailuresPerRun);

            if (tally.Fatals > 0 || transportVerdict != MetricVerdict.Held)
            {
                var voided =
                    $"VERDICT VOID for {fixtureId}: {tally.Fatals} fatal run(s) and " +
                    $"{tally.TransportFailures} per-chunk transport failure(s) over {tally.Runs} run(s) = " +
                    $"{transportFailuresPerRun:F2}/run against {transportBar.Id} " +
                    $"[{transportBar.Bound} {transportBar.Value:F2} {transportBar.Unit}] -> {transportVerdict}. A " +
                    "per-chunk throw is swallowed into a fallback that merges the ORIGINAL text, which " +
                    "reads exactly like 'the model declined to correct', so NO agreement verdict may be " +
                    "drawn from this run. Fix the transport (this is the 2026-08-03 concurrency shape) " +
                    "and re-measure.";
                _output.WriteLine(voided);
                failures.Add(voided);
                continue;
            }

            var whitespaceBar = ProofreadStandingFloor.Metric("chunked.whitespaceOnlySuggestions");
            var whitespaceOnlyPerRun = tally.WhitespaceOnly / (double)tally.Runs;
            var whitespaceVerdict = ProofreadStandingFloor.EvaluateMetric(whitespaceBar, whitespaceOnlyPerRun);

            if (whitespaceVerdict != MetricVerdict.Held)
            {
                var tripped =
                    $"TRIPWIRE for {fixtureId}: {tally.WhitespaceOnly} whitespace-only suggestion(s) over " +
                    $"{tally.Runs} run(s) = {whitespaceOnlyPerRun:F2}/run against {whitespaceBar.Id} " +
                    $"[{whitespaceBar.Bound} {whitespaceBar.Value:F2} {whitespaceBar.Unit}] -> " +
                    $"{whitespaceVerdict}. A new ASYMMETRIC " +
                    "normalization has appeared, so the over-correction column " +
                    "is no longer the model's alone and the punctuation numbers are unreadable.";
                _output.WriteLine(tripped);
                failures.Add(tripped);
            }

            var ev = ProofreadStandingFloor.Evaluate(entry, tally.Hits, tally.Runs);
            evaluated++;
            _output.WriteLine(ev.Message);
            _output.WriteLine(
                $"    over-corrections: {tally.OverCorrections / (double)tally.Runs:F2}/run " +
                $"(characterization, floor recorded {entry.OverCorrectionsPerRunMean:F2}/run - NOT gated)");

            // The agreement verdict is a claim about a MODEL, so it only FAILS when this run is on the
            // model the floor was measured on. Off that model it is still printed - a number nobody
            // prints is a number nobody checks - but it cannot be read as a regression.
            if (ev.IsFailure && onMeasuredModel) failures.Add(ev.Message);
        }

        // failures first: when every fixture voided (fatal or transport-failed), evaluated stays 0 too,
        // but the per-fixture VOID messages already built above name the actual cause and must win over
        // the generic "nothing was evaluated" message below.
        Assert.True(failures.Count == 0,
            $"THE STANDING FLOOR DID NOT HOLD ({failures.Count} finding(s)). Read each one: a regression " +
            "and a fix are BOTH reported here, and the fix is the one that must not be absorbed " +
            "silently.\n\n  " + string.Join("\n\n  ", failures));

        // Non-vacuity: a run with no failures that also evaluated nothing must not report a clean floor.
        Assert.True(evaluated > 0,
            "No fixture was evaluated against the standing floor, so 'no failures' below would mean " +
            "nothing. Either every run was fatal or the tally was never populated.");
    }

    // ── scoring ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One (fixture x repetition) row. Everything the attribution needs, per fixture AND per chunk,
    /// so the plan's matrix is transcribed from data rather than recalled.
    /// </summary>
    private static Dictionary<string, object?> Score(
        ChunkedAgreementFixture fixture, int rep, string model, ChunkedAgreementRun run,
        SuggestionDiffService diff, long ms)
    {
        var result = run.Result.ResultText ?? "";
        var corrected = result.Contains(fixture.ExpectedFix, StringComparison.Ordinal);
        var untouched = result.Contains(fixture.ErrorSpan, StringComparison.Ordinal);
        var nearMiss = result.Contains(fixture.NearMissForbidden, StringComparison.Ordinal);

        var substantive = run.SubstantiveSuggestions
            .Select(s => new { o = s.OriginalText ?? "", s = s.SuggestedText ?? "" })
            .ToArray();

        var classified = substantive
            .Select(x => new Dictionary<string, object?>
            {
                ["original"] = x.o,
                ["suggested"] = x.s,
                ["phenomenon"] = Classify(fixture, x.o, x.s)
            })
            .ToList();

        var chunks = new List<Dictionary<string, object?>>();
        for (var i = 0; i < run.Chunks.Count; i++)
        {
            // BY IDENTITY, never by position. A chunk whose call threw leaves no capture, so a positional
            // read (`i < run.Calls.Count ? run.Calls[i] : null`) does not fail - it silently writes the
            // NEXT chunk's register / overlap / response into this row and reports the last chunk as
            // never called. The row is labelled chunkIndex: i, so it has to be chunk i's capture or none.
            var call = run.Calls.FirstOrDefault(c => c.ChunkIndex == i);
            var chunkText = run.Chunks[i].Text;
            var response = call?.ResponseContent ?? "";
            var cleaned = RecordingChunkRouter.Unwrap(UnifiedAnalysisService.SanitizeResponse(response));
            var perChunkEdits = call is null || string.IsNullOrWhiteSpace(cleaned)
                ? Array.Empty<Dictionary<string, object?>>()
                : diff.ComputeProofreadSuggestions(chunkText, cleaned)
                    .Select(s => new Dictionary<string, object?>
                    {
                        ["original"] = s.OriginalText,
                        ["suggested"] = s.SuggestedText,
                        ["phenomenon"] = Classify(fixture, s.OriginalText ?? "", s.SuggestedText ?? "")
                    })
                    .ToArray();

            chunks.Add(new Dictionary<string, object?>
            {
                ["chunkIndex"] = i,
                ["called"] = call is not null,
                // The router's invocation ordinal for this chunk (null when it was never called), so the
                // JSONL row carries the drift itself rather than hiding it.
                ["callIndex"] = call?.CallIndex,
                ["words"] = ChunkedAgreementHarness.WordCount(chunkText),
                ["carriesErrorSpan"] = chunkText.Contains(fixture.ErrorSpan, StringComparison.Ordinal),
                ["chunkTextMentionsName"] = chunkText.Contains(fixture.CharacterName, StringComparison.Ordinal),
                ["registerBlockPresent"] = call?.HasCharacterRegisterBlock,
                ["registerBlock"] = call?.CharacterRegisterBlock,
                ["overlapPresent"] = call?.OverlapPrefix is not null,
                ["overlapCarriesName"] = call?.OverlapContains(fixture.CharacterName),
                ["overlapPrefix"] = call?.OverlapPrefix,
                ["promptMentionsNameOutsideRegister"] =
                    call is null ? null : MentionsNameOutsideRegister(call, fixture.CharacterName),
                ["responseChars"] = response.Length,
                ["responseEmpty"] = string.IsNullOrWhiteSpace(response),
                ["chunkResponseCarriesFix"] = response.Contains(fixture.ExpectedFix, StringComparison.Ordinal),
                ["chunkResponseCarriesError"] = response.Contains(fixture.ErrorSpan, StringComparison.Ordinal),
                ["chunkResponseCarriesNearMiss"] = response.Contains(fixture.NearMissForbidden, StringComparison.Ordinal),
                ["perChunkEditCount"] = perChunkEdits.Length,
                ["perChunkEdits"] = perChunkEdits
            });
        }

        return new Dictionary<string, object?>
        {
            ["scope"] = "i-chunked-agreement",
            ["fixture"] = fixture.Id,
            ["rep"] = rep,
            ["model"] = model,
            ["surface"] = fixture.Surface.ToString(),
            ["ranChunked"] = run.RanChunked,
            ["expectedChunks"] = fixture.ExpectedChunkCount,
            ["realizedChunks"] = run.Chunks.Count,
            ["modelCalls"] = run.Calls.Count,
            // Captures whose text matched NO chunk. The per-chunk rows below are keyed on ChunkIndex, so
            // an unresolved capture appears in none of them - it must be counted here or it would vanish
            // from the published trail while modelCalls still counted it, which is the same
            // silently-wrong-audit-trail shape the ChunkIndex correlation was added to close. Always 0
            // on a healthy run; a non-zero value means the resolver could not tie a call to a chunk and
            // the per-chunk attribution below is INCOMPLETE, not merely sparse.
            ["unattributedCalls"] =
                run.Calls.Count(c => c.ChunkIndex == RecordingChunkRouter.UnknownChunkIndex),
            ["transportFailures"] = run.Failures
                .Select(f => new Dictionary<string, object?>
                {
                    ["callIndex"] = f.CallIndex,
                    ["chunkIndex"] = f.ChunkIndex,
                    ["type"] = f.ExceptionType,
                    ["message"] = f.Message
                })
                .ToArray(),
            ["corrected"] = corrected,
            ["errorSpanStillPresent"] = untouched,
            ["nearMissProduced"] = nearMiss,
            ["errorSentenceInResult"] = ErrorSentenceWindow(result),
            ["resultUnreliable"] = run.Result.ProofreadResultUnreliable,
            ["noChangesHint"] = run.Result.ProofreadNoChangesHint,
            ["whitespaceOnlySuggestions"] = run.WhitespaceOnlySuggestions.Count,
            ["substantiveSuggestions"] = substantive.Length,
            ["overCorrections"] = classified.Count(c => !string.Equals(c["phenomenon"] as string, "agreement-repair", StringComparison.Ordinal)),
            ["suggestions"] = classified,
            ["ms"] = ms,
            ["chunks"] = chunks
        };
    }

    private static Dictionary<string, object?> FatalRecord(
        ChunkedAgreementFixture fixture, int rep, string model, long ms, string fatal) =>
        new()
        {
            ["scope"] = "i-chunked-agreement",
            ["fixture"] = fixture.Id,
            ["rep"] = rep,
            ["model"] = model,
            ["surface"] = fixture.Surface.ToString(),
            ["fatal"] = fatal,
            ["corrected"] = null,
            ["ms"] = ms
        };

    /// <summary>
    /// Which phenomenon an edit belongs to. The categories are the ones scope (ii)'s decision rule
    /// splits on, so both scopes speak one vocabulary.
    /// </summary>
    private static string Classify(ChunkedAgreementFixture fixture, string original, string suggested)
    {
        if (string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(suggested))
            return "whitespace-only";

        var inError = original.Length > 0 &&
                      (fixture.ErrorSpan.Contains(original, StringComparison.Ordinal) ||
                       original.Contains(fixture.ErrorSpan, StringComparison.Ordinal));
        if (inError)
        {
            if (suggested.Length > 0 && fixture.ExpectedFix.Contains(suggested, StringComparison.Ordinal))
                return "agreement-repair";
            if (suggested.Length > 0 && fixture.NearMissForbidden.Contains(suggested, StringComparison.Ordinal))
                return "agreement-near-miss";
            return "agreement-bearing-other";
        }

        if (original.IndexOf(Gershayim) >= 0 && suggested.IndexOf(Gershayim) < 0 && suggested.Contains('"'))
            return "gershayim-swap";
        if (StripCommas(original) == StripCommas(suggested) && CountCommas(suggested) > CountCommas(original))
            return "comma-insertion";
        if (StripCommas(original) == StripCommas(suggested) && CountCommas(suggested) < CountCommas(original))
            return "comma-deletion";

        return "other";
    }

    private static string StripCommas(string s) => s.Replace(",", "").Replace("،", "").Trim();
    private static int CountCommas(string s) => s.Count(ch => ch == ',' || ch == '،');

    /// <summary>True when the character name occurs anywhere in the composed prompt OUTSIDE the register
    /// block - i.e. the register is APPLICABLE because the prose actually names the referent.</summary>
    private static bool MentionsNameOutsideRegister(ChunkPromptCapture call, string name)
    {
        var prompt = call.Instruction + "\n" + call.WrappedInputText;
        if (call.CharacterRegisterBlock is { } block)
            prompt = prompt.Replace(block, "", StringComparison.Ordinal);
        return prompt.Contains(name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The erroneous sentence AS IT STANDS IN THE RESULT, located by its unchanged tail. Recorded so a
    /// verdict that is neither "corrected" nor "untouched" (a partial or a different rewrite) is
    /// readable rather than a shrug.
    /// </summary>
    private static string? ErrorSentenceWindow(string result)
    {
        const string tail = "לאיש עד סוף הערב";
        var at = result.IndexOf(tail, StringComparison.Ordinal);
        if (at < 0) return null;
        var start = Math.Max(0, at - 30);
        var end = Math.Min(result.Length, at + tail.Length + 2);
        return result[start..end];
    }

    // ── artifacts ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publish the RAW per-chunk composed prompts (and the model's raw response) so the attribution can
    /// be audited rather than trusted. One file per SURVIVING chunk per repetition.
    ///
    /// FILES ARE NAMED BY THE CAPTURE'S OWN <c>ChunkIndex</c>, not by its position in
    /// <c>run.Calls</c>. A chunk whose call threw produces no capture, so an enumeration over the call
    /// LIST shifts every later chunk down one and publishes chunk 1's prompt as <c>chunk0.txt</c> - a
    /// mis-labelled audit trail that reads as authoritative, which is worse than a missing file. With
    /// this naming <c>chunk0.txt</c> is chunk 0's prompt or it does not exist; an unresolvable capture
    /// is named for its call ordinal instead so it is visibly not a chunk claim.
    /// </summary>
    private static void WritePromptArtifacts(
        string promptDir, ChunkedAgreementFixture fixture, int rep, ChunkedAgreementRun run)
    {
        var enc = new UTF8Encoding(false);

        File.WriteAllText(
            Path.Combine(promptDir, $"{fixture.Id}.rep{rep}.RESULT.txt"),
            run.Result.ResultText ?? "", enc);

        foreach (var call in run.Calls)
        {
            var resolved = call.ChunkIndex != RecordingChunkRouter.UnknownChunkIndex;
            var label = resolved
                ? call.ChunkIndex.ToString(CultureInfo.InvariantCulture)
                : $"UNRESOLVED-call{call.CallIndex.ToString(CultureInfo.InvariantCulture)}";

            var sb = new StringBuilder();
            sb.AppendLine($"# fixture      : {fixture.Id}");
            sb.AppendLine($"# repetition   : {rep}");
            sb.AppendLine($"# chunk        : {label} of {run.Chunks.Count}");
            sb.AppendLine($"# call index   : {call.CallIndex} (router invocation ordinal, NOT the chunk)");
            sb.AppendLine($"# surface      : {fixture.Surface}");
            sb.AppendLine($"# register     : {(call.HasCharacterRegisterBlock ? "PRESENT" : "ABSENT")}");
            sb.AppendLine($"# overlap      : {(call.OverlapPrefix is null ? "none" : "present")}");
            sb.AppendLine($"# chunk words  : {ChunkedAgreementHarness.WordCount(call.ChunkText)}");
            sb.AppendLine();
            sb.AppendLine("===== COMPOSED INSTRUCTION (verbatim, as sent to IAiRouter) =====");
            sb.AppendLine(call.Instruction);
            sb.AppendLine();
            sb.AppendLine("===== INPUT TEXT (verbatim) =====");
            sb.AppendLine(call.WrappedInputText);
            sb.AppendLine();
            sb.AppendLine("===== RAW MODEL RESPONSE (verbatim) =====");
            sb.AppendLine(call.ResponseContent);
            File.WriteAllText(
                Path.Combine(promptDir, $"{fixture.Id}.rep{rep}.chunk{label}.txt"), sb.ToString(), enc);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ChunkedAgreementFixture> ResolveFixtures(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ChunkedAgreementFixtures.All;
        var wanted = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ChunkedAgreementFixtures.All.Where(f => wanted.Contains(f.Id)).ToArray();
    }

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    private static string First(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    private static string Summarize(Dictionary<string, object?> r) =>
        $"{r["fixture"]} rep{r["rep"]}: corrected={r["corrected"]} " +
        $"chunks={r.GetValueOrDefault("realizedChunks")} calls={r.GetValueOrDefault("modelCalls")} " +
        $"over={r.GetValueOrDefault("overCorrections")} ws={r.GetValueOrDefault("whitespaceOnlySuggestions")} " +
        $"fail={(r.GetValueOrDefault("transportFailures") as Array)?.Length} " +
        $"{r.GetValueOrDefault("fatal")} {r["ms"]}ms";
}
