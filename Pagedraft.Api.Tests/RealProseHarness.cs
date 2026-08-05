using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;

namespace Pagedraft.Api.Tests;

// ---------------------------------------------------------------------------------------------
// RealProseHarness — drives a RealProsePassage through the PRODUCTION entry point under a chosen
// per-chunk prompt ARM, and reports the run as an EDIT COMPOSITION rather than as a single total.
//
// IT REUSES THE EXISTING CAPTURE INSTRUMENT. RecordingChunkRouter (ChunkedAgreementHarness.cs) already
// records every per-chunk request/response at the IAiRouter seam, resolves each call to its chunk BY
// IDENTITY, and captures a per-chunk THROW separately from a per-chunk response. All three matter
// here, the last one most: RunProofreadChunkedAsync swallows a per-chunk exception and merges the
// ORIGINAL chunk text, which on a PRECISION metric is byte-identical to "the model proposed no edits"
// and therefore scores as a perfect run. That inversion is the single most likely way to get a wrong
// POSITIVE out of this surface, so Failures is surfaced on every run and a consumer must void on it.
//
// THE ARM IS A CONSTRUCTOR ARGUMENT, NOT AN AMBIENT SWITCH. PromptFactory now takes
// IOptions<ProofreadPromptOptions>, so both arms exist in ONE process and a session can alternate them
// without a restart or a config file. ArmOf() is the only place an arm is turned into options, so
// "which arm did this run use" is answerable from the run record rather than from the environment.
//
// WHY THE COMPOSITION AND NOT JUST A COUNT. The synthetic measurement showed the arm does not only
// REMOVE edits: it also introduces a new spurious family (number truncation on nouns, wrong verb and
// adjective edits) that a NET total hides completely, because a removal and an addition cancel. Every
// run therefore reports edits bucketed by SHAPE, deterministically, with no model and no dependence on
// the shipped phenomenon classifier (which was audited on 2026-08-05 and found degenerate here: it
// emits only "agreement-repair" and "other").
// ---------------------------------------------------------------------------------------------

/// <summary>Which per-chunk prompt arm a run composes its instruction under.</summary>
public enum ProofreadPromptArm
{
    /// <summary>The shipped prompt: every <c>ProofreadPromptOptions</c> switch at its default.</summary>
    Off,

    /// <summary>
    /// ARM A of <c>referent-carry-forward-2026-08-04</c>: the <c>[CONTEXT_BEFORE]</c> line extended with
    /// an explicit pronoun-resolution licence. See <c>ProofreadPromptOptions.OverlapReferentLicence</c>.
    /// </summary>
    OverlapReferentLicence
}

/// <summary>
/// The SHAPE of one edit, decided deterministically from the two texts alone. Buckets are ordered from
/// most specific to least; <see cref="RealProseHarness.BucketOf"/> returns the first that matches, so
/// the set is a partition and every edit lands in exactly one.
/// </summary>
public enum RealProseEditBucket
{
    /// <summary>Both sides whitespace-only. A TRIPWIRE, not a real edit - see the run's own property.</summary>
    WhitespaceOnly,

    /// <summary>Differs only in which quote characters are used (ASCII vs gershayim/geresh vs curly).</summary>
    QuoteNormalization,

    /// <summary>Identical once ALL punctuation and whitespace are removed, and not a quote swap.</summary>
    PunctuationOnly,

    /// <summary>One word on each side, and they differ. The lexical-substitution family.</summary>
    SingleWordSubstitution,

    /// <summary>The suggested side is the original's words plus one or more (an in-order supersequence).</summary>
    WordInsertion,

    /// <summary>The suggested side is the original's words minus one or more (an in-order subsequence).</summary>
    WordDeletion,

    /// <summary>Anything else: a multi-word rewrite. The most invasive family.</summary>
    MultiWordRewrite
}

/// <summary>
/// One passage-variant-arm run: the persisted result, the per-chunk capture that explains it, and the
/// derived readings both axes need.
/// </summary>
/// <param name="Passage">The passage that was run.</param>
/// <param name="Variant">Which text of it (clean = precision, seeded = recall).</param>
/// <param name="Arm">The prompt arm the per-chunk instruction was composed under.</param>
/// <param name="Result">The AnalysisResult the production path persisted.</param>
/// <param name="Calls">
/// Per-chunk captures in CALL order. NOT one per chunk: a chunk whose model call threw produces no
/// capture. Correlate through <see cref="ChunkPromptCapture.ChunkIndex"/>, never by position.
/// </param>
/// <param name="ChunkTargetWords">The language-keyed target the run was actually chunked at.</param>
/// <param name="Chunks">The chunker's own output, computed model-free through the production seam.</param>
public sealed record RealProseRun(
    RealProsePassage Passage,
    RealProseVariant Variant,
    ProofreadPromptArm Arm,
    AnalysisResult Result,
    IReadOnlyList<ChunkPromptCapture> Calls,
    int ChunkTargetWords,
    IReadOnlyList<(string Text, string SeparatorAfter, string? OverlapPrefix)> Chunks)
{
    /// <summary>
    /// Per-chunk model calls that THREW. MUST be empty for the run's numbers to mean anything: a failed
    /// chunk is merged as its ORIGINAL text, which reads as "the model made no edits" - a perfect
    /// precision score and a total recall miss at the same time.
    /// </summary>
    public IReadOnlyList<ChunkCallFailure> Failures { get; init; } = Array.Empty<ChunkCallFailure>();

    /// <summary>True when the production path took the CHUNKED route (the "chunked" ModelName sentinel).</summary>
    public bool RanChunked => string.Equals(Result.ModelName, "chunked", StringComparison.Ordinal);

    /// <summary>Suggestions whose ORIGINAL and SUGGESTED are both whitespace-only. Standing tripwire; expected 0.</summary>
    public IReadOnlyList<AnalysisSuggestion> WhitespaceOnlySuggestions =>
        Result.Suggestions.Where(IsWhitespaceOnly).ToArray();

    /// <summary>Suggestions that actually touch text - the only ones either axis may read.</summary>
    public IReadOnlyList<AnalysisSuggestion> SubstantiveSuggestions =>
        Result.Suggestions.Where(s => !IsWhitespaceOnly(s)).ToArray();

    /// <summary>
    /// THE PRECISION READING for this run: how many substantive edits the model proposed. On a CLEAN
    /// passage of twice-proofread prose every one of these is presumed spurious, so lower is better.
    /// Meaningless on a <see cref="RealProseVariant.Seeded"/> run, where some edits are the repairs.
    /// </summary>
    public int EditCount => SubstantiveSuggestions.Count;

    /// <summary>
    /// THE COMPOSITION READING: how many substantive edits fell in each bucket. Reported alongside
    /// <see cref="EditCount"/> because an arm that swaps one family for another moves the composition
    /// while leaving the total flat, and the total alone would call that "no effect".
    /// </summary>
    public IReadOnlyDictionary<RealProseEditBucket, int> EditComposition =>
        Enum.GetValues<RealProseEditBucket>().ToDictionary(
            b => b,
            b => SubstantiveSuggestions.Count(s =>
                RealProseHarness.BucketOf(s.OriginalText ?? "", s.SuggestedText ?? "") == b));

    /// <summary>
    /// THE RECALL READING: which transplanted defects the merged result actually repaired. Empty (and
    /// meaningless) on a clean run - the passage's seeds were never injected into the text it drove.
    /// </summary>
    public IReadOnlyList<RealProseSeed> RepairedSeeds =>
        Variant != RealProseVariant.Seeded
            ? Array.Empty<RealProseSeed>()
            : Passage.Seeds.Where(s => s.RepairedIn(Result.ResultText)).ToArray();

    /// <summary>Transplanted defects the merged result did NOT repair. See <see cref="RepairedSeeds"/>.</summary>
    public IReadOnlyList<RealProseSeed> MissedSeeds =>
        Variant != RealProseVariant.Seeded
            ? Array.Empty<RealProseSeed>()
            : Passage.Seeds.Where(s => !s.RepairedIn(Result.ResultText)).ToArray();

    /// <summary>
    /// Whether the arm's added text ACTUALLY RENDERED in every per-chunk instruction of this run.
    /// Verified against the captured prompts, not inferred from the options: an arm reported as ON that
    /// rendered nothing would produce an OFF measurement under an ON label, which is the exact shape
    /// this corpus already had to rule out once by hand.
    /// </summary>
    public bool ArmRenderedInEveryCall =>
        Calls.Count > 0 && Calls.All(c => RealProseHarness.ArmIsPresent(Arm, c.Instruction));

    private static bool IsWhitespaceOnly(AnalysisSuggestion s) =>
        string.IsNullOrWhiteSpace(s.OriginalText) && string.IsNullOrWhiteSpace(s.SuggestedText);
}

/// <summary>
/// Drives a <see cref="RealProsePassage"/> through <c>UnifiedAnalysisService.RunAsync</c> with a
/// <see cref="RecordingChunkRouter"/> in place of the model. See the file header.
/// </summary>
public static class RealProseHarness
{
    /// <summary>
    /// The AiOptions every run uses. PRODUCTION DEFAULTS except <c>MaxParallelProofreadChunks = 1</c>,
    /// so the recorded call order IS the chunk order. In particular the shipped 500-word LATIN ceiling
    /// is kept; the language-keyed sizer resolves it to 250 for Hebrew, which is the threshold the
    /// passages were sized against.
    /// </summary>
    public static AiOptions HarnessOptions() => new() { MaxParallelProofreadChunks = 1 };

    /// <summary>
    /// THE SUGGESTION DIFF EVERY MEASUREMENT ON THIS SURFACE READS, and the ONE place its shape-guard
    /// posture is decided. Deliberately NOT <c>new SuggestionDiffService()</c>: that takes the
    /// <see cref="HebrewStyleOptions"/> CLASS DEFAULT, which ships
    /// <c>DropOrthographicallyImpossibleSuggestions = true</c>.
    ///
    /// GUARD OFF, ON PURPOSE - this surface measures the MODEL, not the product. Every number this
    /// harness feeds (<c>RealProseRun.EditCount</c>, <c>EditComposition</c>, and through them
    /// <see cref="RealProseArmMeasurement"/>'s per-passage precision matrix and semantic families) is a
    /// count of what the MODEL proposed. A layer that DELETES model output would subtract silently from
    /// exactly those counts, and it would subtract unequally between arms: the corpus records one
    /// suggestion the shipped guard reaches (צמצם -> צמץם) and it belongs to ARM A, so a guard-ON
    /// harness would shrink ARM A's CORRUPTION family and leave the OFF column untouched, i.e. it would
    /// move an arm COMPARISON by an amount that has nothing to do with the arm.
    ///
    /// Contrast the ktiv-male line in <c>RunAsync</c> below, which keeps the PRODUCTION default: that
    /// layer only ADDS suggestions and the passages are gated ktiv-clean, so it contributes nothing to
    /// either arm. This one removes them, and nothing gates the corpus against it.
    ///
    /// The guard's effect on this corpus is not lost by switching it off here - it is measured
    /// separately and deterministically by <c>RealProseNonWordResidue.ShapeGuardReaches</c>, which
    /// computes the reach from the guard itself. Posture pinned by
    /// <c>MeasurementHarnessGuardPostureTests</c>.
    /// </summary>
    public static SuggestionDiffService MeasurementDiffService() =>
        new(new HebrewStyleOptions { DropOrthographicallyImpossibleSuggestions = false });

    /// <summary>The per-chunk word target this harness chunks at (production accessor). 250 for Hebrew.</summary>
    public static int ChunkTargetWords() =>
        UnifiedAnalysisService.ProofreadChunkTargetWordsFor(
            HarnessOptions(), RealProsePrecisionFixtures.Language);

    /// <summary>
    /// The passage text EXACTLY as production would hand it to <c>RunAsync</c>, i.e. after the stripper
    /// every real target-resolution path applies (<c>AnalysisContextService.ResolveChapterAsync</c> and
    /// friends). Applying it to only ONE side of the diff is what produced a page of phantom
    /// whitespace-only "corrections" in an earlier harness, so it is applied here exactly where
    /// production applies it.
    /// </summary>
    public static string ProductionTargetText(RealProsePassage passage, RealProseVariant variant) =>
        SyncfusionWatermarkStripper.StripSyncfusionWatermark(passage.TextFor(variant));

    /// <summary>
    /// The chunker's output for a passage variant, computed MODEL-FREE through the production test seam.
    /// This is the "assert the realized count by driving the real chunker" step the fixture tests gate
    /// on before any GPU is spent.
    /// </summary>
    public static IReadOnlyList<(string Text, string SeparatorAfter, string? OverlapPrefix)> Chunk(
        RealProsePassage passage, RealProseVariant variant = RealProseVariant.Clean) =>
        UnifiedAnalysisService.ChunkForProofreadForTest(
            ProductionTargetText(passage, variant), ChunkTargetWords());

    /// <summary>The options that realize an arm. The ONLY place an arm becomes configuration.</summary>
    public static ProofreadPromptOptions OptionsFor(ProofreadPromptArm arm) => arm switch
    {
        ProofreadPromptArm.Off => new ProofreadPromptOptions(),
        ProofreadPromptArm.OverlapReferentLicence =>
            new ProofreadPromptOptions { OverlapReferentLicence = true },
        _ => throw new ArgumentOutOfRangeException(nameof(arm), arm, "unknown proofread prompt arm")
    };

    /// <summary>The prompt factory for an arm.</summary>
    public static PromptFactory PromptFactoryFor(ProofreadPromptArm arm) =>
        new(Options.Create(OptionsFor(arm)));

    /// <summary>
    /// The text an arm ADDS to a composed per-chunk instruction, or the empty string for
    /// <see cref="ProofreadPromptArm.Off"/>. Hebrew only: the corpus is Hebrew, and returning the
    /// English form here would let a Hebrew run "verify" against a string it can never contain.
    /// </summary>
    public static string ArmMarkerHe(ProofreadPromptArm arm) => arm switch
    {
        ProofreadPromptArm.Off => "",
        ProofreadPromptArm.OverlapReferentLicence => PromptFactory.OverlapReferentLicenceHe,
        _ => throw new ArgumentOutOfRangeException(nameof(arm), arm, "unknown proofread prompt arm")
    };

    /// <summary>
    /// Whether an instruction carries what <paramref name="arm"/> is supposed to add - and, for
    /// <see cref="ProofreadPromptArm.Off"/>, that it carries NO arm's text. The negative direction is
    /// the load-bearing one: an OFF run that silently picked up the arm would look like a null result.
    /// </summary>
    public static bool ArmIsPresent(ProofreadPromptArm arm, string instruction) =>
        arm == ProofreadPromptArm.Off
            ? !instruction.Contains(PromptFactory.OverlapReferentLicenceHe, StringComparison.Ordinal) &&
              !instruction.Contains(PromptFactory.OverlapReferentLicenceEn, StringComparison.Ordinal)
            : instruction.Contains(ArmMarkerHe(arm), StringComparison.Ordinal);

    /// <summary>
    /// Which bucket an edit falls in. Deterministic, text-only, first-match-wins over
    /// <see cref="RealProseEditBucket"/>'s declaration order (most specific first), so the buckets
    /// partition the edits and no edit is counted twice or dropped.
    /// </summary>
    public static RealProseEditBucket BucketOf(string original, string suggested)
    {
        if (string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(suggested))
            return RealProseEditBucket.WhitespaceOnly;

        if (!string.Equals(original, suggested, StringComparison.Ordinal) &&
            string.Equals(NormalizeQuotes(original), NormalizeQuotes(suggested), StringComparison.Ordinal))
            return RealProseEditBucket.QuoteNormalization;

        if (string.Equals(StripPunctuation(original), StripPunctuation(suggested), StringComparison.Ordinal))
            return RealProseEditBucket.PunctuationOnly;

        var o = Words(original);
        var s = Words(suggested);

        if (o.Length == 1 && s.Length == 1)
            return RealProseEditBucket.SingleWordSubstitution;

        if (s.Length > o.Length && IsInOrderSubsequence(o, s))
            return RealProseEditBucket.WordInsertion;

        if (o.Length > s.Length && IsInOrderSubsequence(s, o))
            return RealProseEditBucket.WordDeletion;

        return RealProseEditBucket.MultiWordRewrite;
    }

    /// <summary>Every quote character mapped to a single representative, so a quote SWAP normalizes away.</summary>
    private static string NormalizeQuotes(string text) =>
        Regex.Replace(text, "[\"'׳״‘’“”]", "\"");

    /// <summary>All punctuation and whitespace removed - what is left is the words' letters alone.</summary>
    private static string StripPunctuation(string text) =>
        new(text.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c) && !char.IsSymbol(c)).ToArray());

    private static string[] Words(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : Regex.Split(text.Trim(), @"\s+").Where(w => w.Length > 0).ToArray();

    /// <summary>True when every element of <paramref name="inner"/> appears in <paramref name="outer"/>, in order.</summary>
    private static bool IsInOrderSubsequence(string[] inner, string[] outer)
    {
        var i = 0;
        foreach (var w in outer)
        {
            if (i < inner.Length && string.Equals(inner[i], w, StringComparison.Ordinal)) i++;
        }
        return i == inner.Length;
    }

    /// <summary>
    /// Run one passage variant through <c>UnifiedAnalysisService.RunAsync</c> - the real public entry
    /// point, which decides chunked-vs-single-shot itself - under one prompt arm.
    ///
    /// THIS IS THE ENTRY POINT A LIVE SESSION CALLS. Pass <paramref name="inner"/> = the live
    /// <c>AiRouter</c> for a model run; leave it null and the recording router replays, which keeps the
    /// deterministic tests offline and GPU-free.
    /// </summary>
    /// <param name="passage">The passage.</param>
    /// <param name="variant">Clean (precision) or Seeded (recall).</param>
    /// <param name="arm">Which per-chunk prompt arm to compose under.</param>
    /// <param name="replay">Canned per-chunk response; null echoes the chunk back unchanged.</param>
    /// <param name="inner">A live router; null keeps the run deterministic and offline.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<RealProseRun> RunAsync(
        RealProsePassage passage,
        RealProseVariant variant = RealProseVariant.Clean,
        ProofreadPromptArm arm = ProofreadPromptArm.Off,
        Func<ChunkPromptCapture, string>? replay = null,
        IAiRouter? inner = null,
        CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        // Pre-computed model-free so the router can stamp each capture with the chunk it actually
        // carries. Without it the only correlation available is the call ordinal, which drifts the
        // moment production swallows a per-chunk failure and carries on.
        var chunks = Chunk(passage, variant);
        var router = new RecordingChunkRouter(replay, inner, chunks.Select(c => c.Text).ToArray());

        // NO [CHARACTER_REGISTER], ON PURPOSE. The arm under test extends the [CONTEXT_BEFORE] line and
        // renders whether or not a register is present, so a register is not needed to reach it - while
        // AUTHORING one would put unverified character genders into the prompt on a manuscript whose
        // cast this corpus has not vetted, and a wrong gender would generate real agreement errors that
        // the precision count would then charge to the model. A register-less book is also a shape
        // production genuinely produces (character extraction is optional).
        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, AnalysisType.Proofread,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = ProductionTargetText(passage, variant),
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null,
                Characters = null
            });

        var svc = new UnifiedAnalysisService(
            db,
            router,
            PromptFactoryFor(arm),
            new SfdtConversionService(),
            Options.Create(HarnessOptions()),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            contextMock.Object,
            // GUARD OFF, not the production default: this surface measures the MODEL, and a layer that
            // deletes model output would subtract from the very counts the arm measurement is made of.
            // Argued in full on MeasurementDiffService.
            MeasurementDiffService(),
            // PRODUCTION DEFAULT (EnforceKtivMale = true), not a convenience "off": the passages are
            // gated ktiv-clean instead, so this layer contributes nothing to either arm rather than
            // contributing an equal constant nobody remembered to subtract.
            new KtivMaleChecker(new HebrewStyleOptions()),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: RealProsePrecisionFixtures.Language,
            jobId: null,
            ct: ct);

        return new RealProseRun(passage, variant, arm, result, router.Calls, ChunkTargetWords(), chunks)
        {
            Failures = router.Failures
        };
    }
}
