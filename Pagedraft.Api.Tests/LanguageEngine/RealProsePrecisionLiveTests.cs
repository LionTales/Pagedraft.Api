using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// g1 of <c>proofread-overcorrection-arm-a-2026-08-05</c>: the LIVE consumer of c2's real-prose
/// precision surface. It drives <see cref="RealProseHarness.RunAsync"/> with the real
/// <c>Ollama / gemma4:12b</c> router in the <c>inner</c> slot, so every run goes through the REAL
/// chunked production path with every per-chunk prompt captured, under ONE of exactly TWO arms:
/// <see cref="ProofreadPromptArm.Off"/> and <see cref="ProofreadPromptArm.OverlapReferentLicence"/>
/// (ARM A). ARM B is refuted and is deliberately absent.
///
/// WHY IT LIVES HERE AND IS SHAPED LIKE THIS - identical discipline to its direct ancestor
/// <see cref="ChunkedAgreementLiveTests"/>, whose JSONL is what made a months-later analysis possible:
///  - Namespace <c>Pagedraft.Api.Tests.LanguageEngine</c> + <c>[Trait("Category","LiveModel")]</c>: the
///    standing GPU-safe filter (<c>FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine</c>) excludes
///    it from every ordinary run, and <c>LiveHarnessNamespaceGuardTests</c> enforces the trait so it can
///    never degenerate into a deterministic test hiding in the excluded namespace.
///  - SKIP-BY-DEFAULT on the artifact directory AND on Ollama reachability, through the SAME probe the
///    gold harness uses.
///  - EVERY row is appended to the JSONL the moment it lands and every composed per-chunk prompt and raw
///    response is written out verbatim, so a session cut short at the budget still leaves a complete,
///    self-describing record instead of nothing.
///
/// REP-MAJOR ORDERING, AND IT IS THE MOST IMPORTANT STRUCTURAL PROPERTY OF THIS FILE. The plan sets a
/// HARD ~70 minute budget and the full n=3 matrix is 192 chunk calls (16 run units x 2 arms x 2 chunks x
/// 3 reps), not the ~72 the plan's arithmetic assumed. So the loop is REP-OUTERMOST: rep 1 completes a
/// FULLY BALANCED sweep of every run unit under BOTH arms and flushes, then rep 2, then rep 3. If the
/// parent stops at the budget, what is on disk is a complete balanced matrix at whatever n was reached -
/// never a truncated matrix where one arm or one passage got more runs than another, which is the one
/// shape that would silently bias a paired comparison. n is a parameter (<c>ARMA_REPS</c>) precisely so
/// that nobody has to edit this file to spend less.
///
/// THE TRIPWIRE THIS SURFACE EXISTS TO SURVIVE, and it is specific to a PRECISION metric.
/// <c>RunProofreadChunkedAsync</c> swallows a per-chunk throw, merges the ORIGINAL chunk text and
/// carries on. On a recall metric that reads as a miss; on a PRECISION metric it is byte-identical to
/// "the model proposed no edits", i.e. a PERFECT score. A transport failure would therefore be scored as
/// a spectacular win for whichever arm happened to fail. The same is true of an empty or whitespace-only
/// model response. Both are recorded PER ROW and both set <c>void: true</c>, and the precision reading
/// itself (<c>precisionEditCount</c>) is emitted as NULL on a void row, so a reader of the JSONL cannot
/// average a failed chunk into a mean even by accident. The raw count survives as
/// <c>editCountRaw</c> for diagnosis only, and its name says so.
///
/// DRIVEN BY ENV VARS so the parent can pick n and a passage subset without recompiling:
///   ARMA_ARTIFACT_DIR   required; where the JSONL, the prompt captures and the progress log are written.
///   ARMA_REPS           repetitions in THIS invocation (default 3). Each rep is independently analysable.
///   ARMA_REP_OFFSET     first repetition number to stamp (default 1), so batched invocations do not
///                       collide in the artifact stream.
///   ARMA_PASSAGES       comma-separated passage ids or id fragments (default: all 12 passages, i.e. all
///                       16 run units). The CALIBRATION knob: one passage x both arms is ~4 chunk calls.
///
/// FILE-SIZE WAIVER (recorded) - this file deliberately exceeds the workspace's ~700-line soft ceiling
/// because the tripwire and artifact documentation above is load-bearing for auditing a live session;
/// splitting the driver from its gate would separate the numbers from the reasons they can be trusted.
/// </summary>
public class RealProsePrecisionLiveTests
{
    private readonly ITestOutputHelper _output;

    public RealProsePrecisionLiveTests(ITestOutputHelper output) => _output = output;

    /// <summary>Bumped whenever a field's MEANING changes, so an old row is never read under new rules.</summary>
    private const string SchemaVersion = "arm-a-real-prose-v1";

    private const string Scope = "g1-arm-a-real-prose-precision";

    /// <summary>The two arms this plan measures. ARM B is refuted; adding a third arm re-opens it.</summary>
    private static readonly ProofreadPromptArm[] Arms =
    {
        ProofreadPromptArm.Off,
        ProofreadPromptArm.OverlapReferentLicence
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    // ── the driver ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "LiveModel")]
    public async Task RealProsePrecision_LiveArmMatrix()
    {
        var dir = Environment.GetEnvironmentVariable("ARMA_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            _output.WriteLine("SKIPPED: ARMA_ARTIFACT_DIR is not set. This is g1's measurement driver; it " +
                              "refuses to run without an artifact directory, because a live run whose " +
                              "per-chunk prompts are not published cannot be audited afterwards.");
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
        var jsonlPath = Path.Combine(dir, "arm-a-real-prose.jsonl");
        var progressPath = Path.Combine(dir, "progress.log");
        var aggregatePath = Path.Combine(dir, "aggregate.txt");

        var reps = ParseInt(Environment.GetEnvironmentVariable("ARMA_REPS"), 3);
        var repOffset = ParseInt(Environment.GetEnvironmentVariable("ARMA_REP_OFFSET"), 1);
        var units = ResolveUnits(Environment.GetEnvironmentVariable("ARMA_PASSAGES"));

        Assert.True(units.Count > 0,
            "ARMA_PASSAGES selected no run unit; the session would be vacuous. Pass passage ids or id " +
            "fragments (e.g. 'real-prose-01'), or leave it unset for the whole 16-unit matrix.");

        // ONE binding for the provider/model, read by the router wiring AND stamped on every row, so the
        // published numbers can never be attributed to a model the run did not use.
        var provider = "Ollama";
        var model = ProofreadQualityTests.ProofreadModel;
        var router = ProofreadQualityTests.CreateRouter(provider, model);
        var diff = new SuggestionDiffService();

        var expectedCallsPerRep = units.Sum(u => u.Passage.ExpectedChunkCount) * Arms.Length;

        void Log(string line)
        {
            _output.WriteLine(line);
            Console.WriteLine(line);
            // ITestOutputHelper only flushes when the test METHOD ends, and Console capture behaves the
            // same way, so a session the parent stops at its budget would lose every line above. The
            // progress log is the copy that survives.
            File.AppendAllText(progressPath, line + Environment.NewLine, Utf8NoBom);
        }

        Log($"=== g1: ARM A real-prose precision matrix, live {provider}/{model} ===");
        Log($"artifacts : {dir}");
        Log($"units     : {units.Count} of {RealProsePrecisionFixtures.RunUnits.Count} " +
            $"({units.Count(u => u.Variant == RealProseVariant.Clean)} clean, " +
            $"{units.Count(u => u.Variant == RealProseVariant.Seeded)} seeded)");
        Log($"arms      : {string.Join(", ", Arms)}");
        Log($"reps      : {reps} starting at {repOffset} (REP-MAJOR: every rep is a complete balanced sweep)");
        Log($"per rep   : {expectedCallsPerRep} chunk call(s) expected");
        Log($"started   : {DateTimeOffset.Now:O}");

        var allRows = new List<Dictionary<string, object?>>();
        var totalSw = Stopwatch.StartNew();
        var callsSoFar = 0;

        for (var r = 0; r < reps; r++)
        {
            var rep = repOffset + r;
            var repRows = new List<Dictionary<string, object?>>();
            var repSw = Stopwatch.StartNew();

            // BALANCED BY CONSTRUCTION: every unit is run under EVERY arm before the rep is considered
            // done, and the two arms of a unit run back to back so a drifting server affects both alike.
            foreach (var (passage, variant) in units)
            {
                foreach (var arm in Arms)
                {
                    var sw = Stopwatch.StartNew();
                    RealProseRun? run = null;
                    string? fatal = null;
                    try
                    {
                        run = await RealProseHarness.RunAsync(passage, variant, arm, replay: null, inner: router);
                    }
                    catch (Exception ex)
                    {
                        // The chunked path swallows PER-CHUNK throws, so anything surfacing here is a
                        // failure of the run as a whole (wiring, chunker, persistence). Recorded as a
                        // fatal row, never as "the model proposed no edits".
                        fatal = $"{ex.GetType().Name}: {First(ex.Message, 400)}";
                    }
                    sw.Stop();

                    var record = fatal is not null
                        ? FatalRecord(passage, variant, arm, rep, provider, model, sw.ElapsedMilliseconds, fatal)
                        : Score(passage, variant, arm, rep, provider, model, run!, diff, sw.ElapsedMilliseconds);

                    // APPEND IMMEDIATELY - a mid-run death must leave evidence.
                    File.AppendAllText(jsonlPath, JsonSerializer.Serialize(record, Json) + Environment.NewLine,
                        Utf8NoBom);

                    if (run is not null)
                    {
                        WritePromptArtifacts(promptDir, passage, variant, arm, rep, run);
                        callsSoFar += run.Calls.Count;
                    }

                    repRows.Add(record);
                    allRows.Add(record);
                    Log(Summarize(record));
                }
            }

            repSw.Stop();

            // FLUSHED PER REP, not at the end: the aggregate for rep N is on disk before rep N+1 starts.
            var block = RepAggregate(repRows, rep, r + 1, reps, repSw.Elapsed, totalSw.Elapsed, callsSoFar,
                allRows);
            File.AppendAllText(aggregatePath, block + Environment.NewLine, Utf8NoBom);
            foreach (var line in block.Split('\n')) Log(line.TrimEnd('\r'));
        }

        Assert.True(File.Exists(jsonlPath), "no JSONL artifact was written; the run measured nothing");

        GateTheInstrument(allRows, Log);
    }

    // ── the gate ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE HARD TRIPWIRE. Everything here is a claim about the INSTRUMENT, never about the arms, so it
    /// fails on every model and is not model-conditional the way an outcome floor would be.
    ///
    /// Ordering is deliberate: the ARM-RENDER check runs first, because a run whose arm did not render
    /// measured the OFF prompt under an ON label and every other number in it is mislabelled rather than
    /// merely missing. Then the VOID check, which is the plan's specific inversion: a per-chunk transport
    /// failure or an empty response merges the ORIGINAL text, which on a precision metric scores as a
    /// perfect run. Then non-vacuity, so "no failures" cannot be reported by a run that measured nothing.
    ///
    /// The artifacts are already on disk before any of this executes, so a red gate never costs evidence -
    /// it only stops the numbers being read as a verdict.
    /// </summary>
    private static void GateTheInstrument(
        IReadOnlyList<Dictionary<string, object?>> rows, Action<string> log)
    {
        var failures = new List<string>();

        var armMismatch = rows.Where(r => r.GetValueOrDefault("armRenderOk") is not true).ToList();
        if (armMismatch.Count > 0)
        {
            failures.Add(
                $"ARM RENDER MISMATCH on {armMismatch.Count} row(s). The arm's Hebrew line must appear in " +
                "100% of an ON row's chunk prompts and 0% of an OFF row's, VERIFIED AGAINST THE CAPTURED " +
                "PROMPT TEXT rather than against the options object. A mismatch means the run is labelled " +
                "with an arm it did not compose under, which is not a weak measurement but a wrong one:\n    " +
                string.Join("\n    ", armMismatch.Select(r =>
                    $"{r["passage"]} {r["variant"]} {r["arm"]} rep{r["rep"]}: " +
                    $"{r.GetValueOrDefault("promptsWithArmText")} of {r.GetValueOrDefault("promptCount")} " +
                    $"prompt(s) carried the arm text, expected " +
                    $"{r.GetValueOrDefault("promptsWithArmTextExpected")}")));
        }

        var voided = rows.Where(r => r.GetValueOrDefault("void") is true).ToList();
        if (voided.Count > 0)
        {
            failures.Add(
                $"{voided.Count} VOID row(s) of {rows.Count}. A per-chunk transport failure or an empty " +
                "model response merges the ORIGINAL chunk text, which is byte-identical to 'the model " +
                "proposed no edits' - i.e. a PERFECT precision score. These rows have measured NOTHING " +
                "and their precisionEditCount is null in the JSONL so they cannot be averaged in. Fix the " +
                "transport (this is the 2026-08-03 concurrency shape) and re-measure:\n    " +
                string.Join("\n    ", voided.Select(r =>
                    $"{r["passage"]} {r["variant"]} {r["arm"]} rep{r["rep"]}: " +
                    $"{string.Join("; ", (r.GetValueOrDefault("voidReasons") as string[]) ?? Array.Empty<string>())}")));
        }

        var usable = rows.Count(r => r.GetValueOrDefault("precisionEditCount") is not null);
        if (usable == 0)
            failures.Add(
                "NO usable precision row was produced, so a clean gate below would mean nothing. Either " +
                "every run voided or the matrix selected only seeded units.");

        log("");
        log(failures.Count == 0
            ? $"=== INSTRUMENT OK === {rows.Count} row(s), {usable} usable precision row(s), 0 void."
            : $"=== INSTRUMENT NOT OK === {failures.Count} finding(s).");

        Assert.True(failures.Count == 0,
            "THE MEASUREMENT INSTRUMENT DID NOT HOLD, so no ARM verdict may be drawn from this session " +
            $"({failures.Count} finding(s)):\n\n  " + string.Join("\n\n  ", failures));
    }

    // ── scoring ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One (passage, variant, arm, rep) row - the unit the plan's per-passage sign test is computed over.
    /// A pooled mean alone does not satisfy the decision rule, so the row is per PASSAGE and never
    /// pre-aggregated.
    /// </summary>
    private static Dictionary<string, object?> Score(
        RealProsePassage passage, RealProseVariant variant, ProofreadPromptArm arm, int rep,
        string provider, string model, RealProseRun run, SuggestionDiffService diff, long ms)
    {
        // ARM VERIFICATION FROM THE CAPTURED PROMPT TEXT, per call. Not from the options object: an arm
        // reported ON that rendered nothing would publish an OFF measurement under an ON label, and the
        // whole session would be unfalsifiable afterwards.
        var armText = RealProseHarness.ArmMarkerHe(ProofreadPromptArm.OverlapReferentLicence);
        var promptCount = run.Calls.Count;
        var promptsWithArmText = run.Calls.Count(c => c.Instruction.Contains(armText, StringComparison.Ordinal));
        var promptsWithArmTextExpected = arm == ProofreadPromptArm.OverlapReferentLicence ? promptCount : 0;
        var armRenderOk =
            promptCount > 0 &&
            promptsWithArmText == promptsWithArmTextExpected &&
            run.ArmRenderedInEveryCall;

        var calledChunkIndexes = run.Calls.Select(c => c.ChunkIndex).ToHashSet();
        var uncalled = Enumerable.Range(0, run.Chunks.Count).Where(i => !calledChunkIndexes.Contains(i)).ToArray();

        var chunks = new List<Dictionary<string, object?>>();
        var emptyResponses = new List<int>();
        for (var i = 0; i < run.Chunks.Count; i++)
        {
            // BY IDENTITY, never by position: a chunk whose call threw leaves no capture, so a positional
            // read would silently write the NEXT chunk's prompt and response into this row.
            var call = run.Calls.FirstOrDefault(c => c.ChunkIndex == i);
            var chunkText = run.Chunks[i].Text;
            var response = call?.ResponseContent ?? "";
            var cleaned = RecordingChunkRouter.Unwrap(UnifiedAnalysisService.SanitizeResponse(response));

            // AN EMPTY RESPONSE IS THE SAME INVERSION AS A THROW. The merge falls back to the original
            // chunk text, which reads as "no edits proposed" = a perfect precision score. Recorded per
            // chunk here and voided on at the row level below.
            var responseEmpty = call is not null && string.IsNullOrWhiteSpace(cleaned);
            if (responseEmpty) emptyResponses.Add(i);

            var perChunkEdits = call is null || string.IsNullOrWhiteSpace(cleaned)
                ? Array.Empty<Dictionary<string, object?>>()
                : diff.ComputeProofreadSuggestions(chunkText, cleaned).Select(EditRow).ToArray();

            chunks.Add(new Dictionary<string, object?>
            {
                ["chunkIndex"] = i,
                ["called"] = call is not null,
                ["callIndex"] = call?.CallIndex,
                ["words"] = RealProsePrecisionFixtures.WordCount(chunkText),
                ["overlapPresent"] = call?.OverlapPrefix is not null,
                ["overlapChars"] = call?.OverlapPrefix?.Length,
                ["armInPrompt"] = call is null
                    ? null
                    : call.Instruction.Contains(armText, StringComparison.Ordinal),
                ["promptChars"] = call?.Instruction.Length,
                ["responseChars"] = response.Length,
                ["responseEmpty"] = responseEmpty,
                ["perChunkEditCount"] = perChunkEdits.Length,
                ["perChunkEdits"] = perChunkEdits
            });
        }

        var transportFailures = run.Failures
            .Select(f => new Dictionary<string, object?>
            {
                ["callIndex"] = f.CallIndex,
                ["chunkIndex"] = f.ChunkIndex,
                ["type"] = f.ExceptionType,
                ["message"] = f.Message
            })
            .ToArray();

        var unattributed = run.Calls.Count(c => c.ChunkIndex == RecordingChunkRouter.UnknownChunkIndex);
        var whitespaceOnly = run.WhitespaceOnlySuggestions.Count;

        // EVERY REASON A ROW CANNOT BE READ. Each one either makes the merged text partly the ORIGINAL
        // (which a precision count scores as perfect) or makes the attribution itself unreliable.
        var voidReasons = new List<string>();
        if (!run.RanChunked)
            voidReasons.Add("not-chunked: the run took the single-shot route, which this arm never reaches");
        if (transportFailures.Length > 0)
            voidReasons.Add($"transport-failures={transportFailures.Length}: the failed chunk(s) merged their " +
                            "ORIGINAL text, which scores as a perfect precision run");
        if (uncalled.Length > 0)
            voidReasons.Add($"uncalled-chunks=[{string.Join(",", uncalled)}]: no model call carried these chunks");
        if (emptyResponses.Count > 0)
            voidReasons.Add($"empty-responses=[{string.Join(",", emptyResponses)}]: an empty response falls back " +
                            "to the original chunk text, same inversion as a throw");
        if (whitespaceOnly > 0)
            voidReasons.Add($"whitespace-only-suggestions={whitespaceOnly}: a new ASYMMETRIC normalization has " +
                            "appeared, so the edit count is no longer the model's alone");
        if (unattributed > 0)
            voidReasons.Add($"unattributed-calls={unattributed}: a capture matched no chunk, so the per-chunk " +
                            "attribution below is INCOMPLETE rather than merely sparse");
        if (!armRenderOk)
            voidReasons.Add($"arm-render-mismatch: {promptsWithArmText}/{promptCount} prompt(s) carried the arm " +
                            $"text, expected {promptsWithArmTextExpected}");
        if (run.Chunks.Count != passage.ExpectedChunkCount)
            voidReasons.Add($"chunk-count-drift: realized {run.Chunks.Count}, fixture pins " +
                            $"{passage.ExpectedChunkCount}");

        var isVoid = voidReasons.Count > 0;
        var isPrecisionRow = variant == RealProseVariant.Clean;
        var isRecallRow = variant == RealProseVariant.Seeded;

        return new Dictionary<string, object?>
        {
            ["schema"] = SchemaVersion,
            ["scope"] = Scope,
            ["passage"] = passage.Id,
            ["variant"] = variant.ToString(),
            ["arm"] = arm.ToString(),
            ["armLabel"] = ArmLabel(arm),
            ["rep"] = rep,
            ["provider"] = provider,
            ["model"] = model,
            ["utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),

            // ── validity ────────────────────────────────────────────────────────────────────────
            ["void"] = isVoid,
            ["voidReasons"] = voidReasons.ToArray(),
            ["ranChunked"] = run.RanChunked,
            ["expectedChunks"] = passage.ExpectedChunkCount,
            ["realizedChunks"] = run.Chunks.Count,
            ["modelCalls"] = run.Calls.Count,
            ["unattributedCalls"] = unattributed,
            ["uncalledChunkIndexes"] = uncalled,
            ["transportFailures"] = transportFailures,
            ["emptyResponseChunkIndexes"] = emptyResponses.ToArray(),
            ["whitespaceOnlySuggestions"] = whitespaceOnly,

            // ── arm attribution, verified against the captured prompts ──────────────────────────
            ["promptCount"] = promptCount,
            ["promptsWithArmText"] = promptsWithArmText,
            ["promptsWithArmTextExpected"] = promptsWithArmTextExpected,
            ["armRenderedInEveryCall"] = run.ArmRenderedInEveryCall,
            ["armRenderOk"] = armRenderOk,

            // ── precision ───────────────────────────────────────────────────────────────────────
            // editCountRaw is DIAGNOSTIC ONLY and its name says so: on a void row it is the count a
            // failed chunk produced, which is exactly the number that must not enter a mean.
            // precisionEditCount is the ONLY field a precision mean may be computed from, and it is null
            // unless the row is a clean, non-void row.
            ["editCountRaw"] = run.EditCount,
            ["precisionEditCount"] = isPrecisionRow && !isVoid ? run.EditCount : (int?)null,
            ["isPrecisionRow"] = isPrecisionRow,
            ["editComposition"] = run.EditComposition.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            ["suggestions"] = run.SubstantiveSuggestions.Select(EditRow).ToArray(),

            // ── recall ──────────────────────────────────────────────────────────────────────────
            ["isRecallRow"] = isRecallRow,
            ["seedTotal"] = passage.Seeds.Count,
            ["seedsRepaired"] = run.RepairedSeeds.Select(s => s.GoldCaseId).ToArray(),
            ["seedsMissed"] = run.MissedSeeds.Select(s => s.GoldCaseId).ToArray(),
            ["recallRepaired"] = isRecallRow && !isVoid ? run.RepairedSeeds.Count : (int?)null,
            ["recallTotal"] = isRecallRow && !isVoid ? passage.Seeds.Count : (int?)null,

            // ── pipeline hints + per-chunk detail ───────────────────────────────────────────────
            ["resultUnreliable"] = run.Result.ProofreadResultUnreliable,
            ["noChangesHint"] = run.Result.ProofreadNoChangesHint,
            ["ms"] = ms,
            ["chunks"] = chunks
        };
    }

    private static Dictionary<string, object?> EditRow(Pagedraft.Api.Models.AnalysisSuggestion s)
    {
        var original = s.OriginalText ?? "";
        var suggested = s.SuggestedText ?? "";
        return new Dictionary<string, object?>
        {
            ["original"] = original,
            ["suggested"] = suggested,
            // The DETERMINISTIC shape bucket, not the shipped phenomenon classifier (audited 2026-08-05
            // and found degenerate here). c1 found ARM A introduces a NEW spurious family as well as
            // removing others, and a NET count hides a swap completely.
            ["bucket"] = RealProseHarness.BucketOf(original, suggested).ToString()
        };
    }

    private static Dictionary<string, object?> FatalRecord(
        RealProsePassage passage, RealProseVariant variant, ProofreadPromptArm arm, int rep,
        string provider, string model, long ms, string fatal) =>
        new()
        {
            ["schema"] = SchemaVersion,
            ["scope"] = Scope,
            ["passage"] = passage.Id,
            ["variant"] = variant.ToString(),
            ["arm"] = arm.ToString(),
            ["armLabel"] = ArmLabel(arm),
            ["rep"] = rep,
            ["provider"] = provider,
            ["model"] = model,
            ["utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["void"] = true,
            ["voidReasons"] = new[] { "fatal: " + fatal },
            ["fatal"] = fatal,
            // Explicitly null so a consumer that reads precisionEditCount finds nothing to average, and
            // explicitly false so armRenderOk's gate counts this row as a finding rather than skipping it.
            ["precisionEditCount"] = (int?)null,
            ["recallRepaired"] = (int?)null,
            ["recallTotal"] = (int?)null,
            ["armRenderOk"] = false,
            ["promptCount"] = 0,
            ["promptsWithArmText"] = 0,
            ["promptsWithArmTextExpected"] = arm == ProofreadPromptArm.OverlapReferentLicence ? -1 : 0,
            ["ms"] = ms
        };

    // ── the per-rep aggregate block ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>=== Aggregate ===</c> block printed AND flushed to disk at the end of every rep, so the
    /// parent can read progress, latency and the state of the effect straight from the log while the
    /// session is still running.
    ///
    /// Everything is computed from the PERSISTED rows rather than from live objects, so the numbers
    /// reported are the numbers published. Void rows are excluded from every mean and listed separately;
    /// they are never folded in.
    /// </summary>
    private static string RepAggregate(
        IReadOnlyList<Dictionary<string, object?>> repRows, int rep, int repOrdinal, int repTotal,
        TimeSpan repElapsed, TimeSpan totalElapsed, int callsSoFar,
        IReadOnlyList<Dictionary<string, object?>> allRows)
    {
        var sb = new StringBuilder();
        var repCalls = repRows.Sum(r => r.GetValueOrDefault("modelCalls") as int? ?? 0);

        sb.AppendLine();
        sb.AppendLine($"=== Aggregate === rep {rep} ({repOrdinal} of {repTotal} in this invocation)");
        sb.AppendLine(
            $"elapsed: {repElapsed.TotalSeconds:F1}s this rep, {totalElapsed.TotalSeconds:F1}s total | " +
            $"calls: {repCalls} this rep, {callsSoFar} cumulative | " +
            $"{(callsSoFar > 0 ? totalElapsed.TotalSeconds / callsSoFar : 0):F1}s per chunk call");

        // PRECISION, PER PASSAGE. The plan's decision rule requires the effect to be visible per passage
        // (>=10 of 12 by sign test), so the per-passage table is the primary reading and the mean is a
        // footnote to it, never a substitute.
        var precisionPassages = repRows
            .Where(r => r.GetValueOrDefault("isPrecisionRow") is true)
            .Select(r => (string)r["passage"]!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (precisionPassages.Count > 0)
        {
            sb.AppendLine("PRECISION (clean passages, usable rows only; lower is better)");
            sb.AppendLine("  passage                                    off    armA   delta");
            var deltas = new List<int>();
            foreach (var p in precisionPassages)
            {
                var off = Precision(repRows, p, ProofreadPromptArm.Off);
                var armA = Precision(repRows, p, ProofreadPromptArm.OverlapReferentLicence);
                var delta = off is not null && armA is not null ? armA - off : null;
                if (delta is not null) deltas.Add(delta.Value);
                sb.AppendLine($"  {p,-40} {Cell(off),6} {Cell(armA),6} {Cell(delta),7}");
            }

            var offMean = MeanPrecision(repRows, ProofreadPromptArm.Off);
            var armMean = MeanPrecision(repRows, ProofreadPromptArm.OverlapReferentLicence);
            sb.AppendLine($"  {"mean (paired passages only)",-40} {Fmt(offMean),6} {Fmt(armMean),6} " +
                          $"{Fmt(deltas.Count > 0 ? deltas.Average() : (double?)null),7}");
            sb.AppendLine($"  passages where armA < off: {deltas.Count(d => d < 0)}/{deltas.Count} " +
                          $"(ties: {deltas.Count(d => d == 0)}, worse: {deltas.Count(d => d > 0)})");

            // COMPOSITION, so a SWAP is visible as a swap. A family removed and a family introduced
            // cancel in a net count, and c1 found ARM A does exactly that (number truncation).
            sb.AppendLine("COMPOSITION (clean usable rows, summed) bucket: off -> armA");
            foreach (var bucket in Enum.GetValues<RealProseEditBucket>())
            {
                var off = CompositionSum(repRows, ProofreadPromptArm.Off, bucket);
                var armA = CompositionSum(repRows, ProofreadPromptArm.OverlapReferentLicence, bucket);
                if (off == 0 && armA == 0) continue;
                sb.AppendLine($"  {bucket,-24} {off,5} -> {armA,-5} ({armA - off:+0;-0;0})");
            }
        }

        // RECALL. The condition that kills a "lazier model" win, so it is printed every rep even though
        // n=3 cannot settle it (the standing corpus rule wants n>=15 for a single-case recall claim).
        var recallRows = repRows.Where(r => r.GetValueOrDefault("isRecallRow") is true).ToList();
        if (recallRows.Count > 0)
        {
            sb.AppendLine("RECALL (seeded passages, usable rows only; repaired/total, higher is better)");
            foreach (var p in recallRows.Select(r => (string)r["passage"]!).Distinct(StringComparer.Ordinal)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {p,-40} off {Recall(recallRows, p, ProofreadPromptArm.Off)}   " +
                              $"armA {Recall(recallRows, p, ProofreadPromptArm.OverlapReferentLicence)}");
            }
        }

        var voided = repRows.Where(r => r.GetValueOrDefault("void") is true).ToList();
        sb.AppendLine($"VOID ROWS this rep: {voided.Count} of {repRows.Count} " +
                      $"(cumulative {allRows.Count(r => r.GetValueOrDefault("void") is true)} of {allRows.Count})");
        foreach (var r in voided)
            sb.AppendLine($"  VOID {r["passage"]} {r["variant"]} {r["arm"]}: " +
                          string.Join("; ", (r.GetValueOrDefault("voidReasons") as string[]) ?? Array.Empty<string>()));
        if (voided.Count > 0)
            sb.AppendLine("  A VOID row measured NOTHING: its precisionEditCount is null in the JSONL and it " +
                          "must not be folded into any mean. Non-zero here VOIDS the verdict.");

        return sb.ToString();
    }

    private static int? Precision(
        IReadOnlyList<Dictionary<string, object?>> rows, string passage, ProofreadPromptArm arm) =>
        rows.FirstOrDefault(r =>
                string.Equals(r.GetValueOrDefault("passage") as string, passage, StringComparison.Ordinal) &&
                string.Equals(r.GetValueOrDefault("arm") as string, arm.ToString(), StringComparison.Ordinal))
            ?.GetValueOrDefault("precisionEditCount") as int?;

    private static double? MeanPrecision(
        IReadOnlyList<Dictionary<string, object?>> rows, ProofreadPromptArm arm)
    {
        var values = rows
            .Where(r => string.Equals(r.GetValueOrDefault("arm") as string, arm.ToString(), StringComparison.Ordinal))
            .Select(r => r.GetValueOrDefault("precisionEditCount") as int?)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Average();
    }

    private static int CompositionSum(
        IReadOnlyList<Dictionary<string, object?>> rows, ProofreadPromptArm arm, RealProseEditBucket bucket) =>
        rows
            .Where(r => string.Equals(r.GetValueOrDefault("arm") as string, arm.ToString(), StringComparison.Ordinal))
            .Where(r => r.GetValueOrDefault("precisionEditCount") is not null)
            .Select(r => r.GetValueOrDefault("editComposition") as Dictionary<string, int>)
            .Where(d => d is not null)
            .Sum(d => d!.GetValueOrDefault(bucket.ToString()));

    private static string Recall(
        IReadOnlyList<Dictionary<string, object?>> rows, string passage, ProofreadPromptArm arm)
    {
        var row = rows.FirstOrDefault(r =>
            string.Equals(r.GetValueOrDefault("passage") as string, passage, StringComparison.Ordinal) &&
            string.Equals(r.GetValueOrDefault("arm") as string, arm.ToString(), StringComparison.Ordinal));
        var repaired = row?.GetValueOrDefault("recallRepaired") as int?;
        var total = row?.GetValueOrDefault("recallTotal") as int?;
        return repaired is null || total is null ? "VOID" : $"{repaired}/{total}";
    }

    private static string Cell(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "VOID";
    private static string Fmt(double? v) => v is null ? "-" : v.Value.ToString("F2", CultureInfo.InvariantCulture);

    // ── artifacts ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publish the RAW per-chunk composed prompt and the model's raw response, so the attribution can be
    /// AUDITED instead of trusted - which is exactly what let c1 re-analyse the previous session months
    /// later off nothing but the files.
    ///
    /// FILES ARE NAMED BY THE CAPTURE'S OWN <c>ChunkIndex</c>, never by its position in the call list. A
    /// chunk whose call threw produces no capture, so enumerating the LIST shifts every later chunk down
    /// one and publishes chunk 1's prompt as chunk0.txt: a mis-labelled audit trail that reads as
    /// authoritative, which is worse than a missing file.
    /// </summary>
    private static void WritePromptArtifacts(
        string promptDir, RealProsePassage passage, RealProseVariant variant, ProofreadPromptArm arm,
        int rep, RealProseRun run)
    {
        var stem = $"{passage.Id}.{variant}.{ArmLabel(arm)}.rep{rep.ToString(CultureInfo.InvariantCulture)}";
        var armText = RealProseHarness.ArmMarkerHe(ProofreadPromptArm.OverlapReferentLicence);

        File.WriteAllText(Path.Combine(promptDir, $"{stem}.RESULT.txt"), run.Result.ResultText ?? "", Utf8NoBom);

        foreach (var call in run.Calls)
        {
            var resolved = call.ChunkIndex != RecordingChunkRouter.UnknownChunkIndex;
            var label = resolved
                ? call.ChunkIndex.ToString(CultureInfo.InvariantCulture)
                : $"UNRESOLVED-call{call.CallIndex.ToString(CultureInfo.InvariantCulture)}";

            var sb = new StringBuilder();
            sb.AppendLine($"# passage      : {passage.Id}");
            sb.AppendLine($"# variant      : {variant}");
            sb.AppendLine($"# arm          : {arm} ({ArmLabel(arm)})");
            sb.AppendLine($"# repetition   : {rep}");
            sb.AppendLine($"# chunk        : {label} of {run.Chunks.Count}");
            sb.AppendLine($"# call index   : {call.CallIndex} (router invocation ordinal, NOT the chunk)");
            sb.AppendLine($"# arm text in prompt: {call.Instruction.Contains(armText, StringComparison.Ordinal)}");
            sb.AppendLine($"# overlap      : {(call.OverlapPrefix is null ? "none" : "present")}");
            sb.AppendLine($"# register     : {(call.HasCharacterRegisterBlock ? "PRESENT" : "ABSENT")}");
            sb.AppendLine($"# chunk words  : {RealProsePrecisionFixtures.WordCount(call.ChunkText)}");
            sb.AppendLine($"# response len : {call.ResponseContent.Length}");
            sb.AppendLine();
            sb.AppendLine("===== COMPOSED INSTRUCTION (verbatim, as sent to IAiRouter) =====");
            sb.AppendLine(call.Instruction);
            sb.AppendLine();
            sb.AppendLine("===== INPUT TEXT (verbatim) =====");
            sb.AppendLine(call.WrappedInputText);
            sb.AppendLine();
            sb.AppendLine("===== RAW MODEL RESPONSE (verbatim) =====");
            sb.AppendLine(call.ResponseContent);
            File.WriteAllText(Path.Combine(promptDir, $"{stem}.chunk{label}.txt"), sb.ToString(), Utf8NoBom);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Short, filename-safe arm label. Deliberately short: the artifact directory is a deep scratchpad
    /// path and a long arm name pushes the per-chunk file names towards the Windows path ceiling. The
    /// FULL arm name is stamped inside every file and in every JSONL row, so nothing is lost.
    /// </summary>
    private static string ArmLabel(ProofreadPromptArm arm) => arm switch
    {
        ProofreadPromptArm.Off => "off",
        ProofreadPromptArm.OverlapReferentLicence => "armA",
        _ => throw new ArgumentOutOfRangeException(nameof(arm), arm, "unknown proofread prompt arm")
    };

    /// <summary>
    /// The run units this invocation drives. Unfiltered this is the whole 16-unit matrix (12 clean + 4
    /// seeded). A filter entry matches a passage id exactly or as a fragment, so 'real-prose-01' selects
    /// the calibration passage without spelling out its full id.
    /// </summary>
    private static IReadOnlyList<(RealProsePassage Passage, RealProseVariant Variant)> ResolveUnits(string? raw)
    {
        var all = RealProsePrecisionFixtures.RunUnits;
        if (string.IsNullOrWhiteSpace(raw)) return all;

        var wanted = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all
            .Where(u => wanted.Any(w => u.Passage.Id.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    private static string First(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    private static string Summarize(Dictionary<string, object?> r)
    {
        var voidReasons = (r.GetValueOrDefault("voidReasons") as string[]) ?? Array.Empty<string>();
        return
            $"{r["passage"]} {r["variant"]} {r["armLabel"]} rep{r["rep"]}: " +
            $"edits={r.GetValueOrDefault("editCountRaw")} " +
            $"precision={(r.GetValueOrDefault("precisionEditCount") as int?)?.ToString() ?? "n/a"} " +
            $"recall={(r.GetValueOrDefault("recallRepaired") as int?)?.ToString() ?? "n/a"}/" +
            $"{(r.GetValueOrDefault("recallTotal") as int?)?.ToString() ?? "n/a"} " +
            $"calls={r.GetValueOrDefault("modelCalls")} " +
            $"arm={r.GetValueOrDefault("promptsWithArmText")}/{r.GetValueOrDefault("promptCount")} " +
            $"void={r.GetValueOrDefault("void")} " +
            (voidReasons.Length > 0 ? "[" + string.Join(" | ", voidReasons) + "] " : "") +
            $"{r["ms"]}ms";
    }
}
