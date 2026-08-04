using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
// ChunkedAgreementHarness — THE ATTRIBUTION INSTRUMENT for the chunked proofread path (c1).
//
// WHAT IT IS. A recording IAiRouter plus a runner that drives the REAL production entry point,
// UnifiedAnalysisService.RunAsync (UnifiedAnalysisService.cs:370), which routes into the private
// RunProofreadChunkedAsync (UnifiedAnalysisService.cs:2110) at :404 when
//     WordCount(inputText) > ProofreadChunkTargetWordsFor(opts, language, tier)
// i.e. > ~250 for Hebrew. NOTHING here reflects into the private method: the four existing tests
// that DO (AnalysisRunLogTests.RunProofreadChunkedAsync_*) pin per-chunk RUN-LOG behaviour on
// degenerate inputs; what this harness needs is the ROUTING DECISION as well as the chunking, and
// the routing decision lives in RunAsync. Reflecting past it would measure a regime the product
// cannot actually enter.
//
// WHAT IT CAPTURES, PER CHUNK AND IN ORDER (this capture IS the attribution - without it a failed
// run tells you only that it failed):
//   - the fully composed instruction the service sent for that chunk
//   - whether the [CHARACTER_REGISTER] block is present in it, and its exact rendered content
//   - the overlapPrefix it carried, read back out of the [CONTEXT_BEFORE] section
//   - the chunk text, unwrapped from [TEXT_TO_CORRECT]...[/TEXT_TO_CORRECT]
//   - the model's response for that chunk
//
// HOW g1 SWAPS IN THE LIVE ROUTER. RecordingChunkRouter takes an OPTIONAL inner IAiRouter:
//     new RecordingChunkRouter(inner: liveAiRouter)          // g1: real model, still recorded
//     new RecordingChunkRouter(replay: c => cannedText)      // c1: deterministic, no model
// With an inner router it DELEGATES and records both sides; with none it replays. Nothing else in
// the harness changes, so the live swap is one constructor argument. That is deliberate: the whole
// point of building the instrument in a no-GPU todo is that g1 spends its single Ollama session on
// measurement, not on plumbing.
//
// ORDERING. Chunks run with limited parallelism, so a recorded call ORDER is only meaningful if the
// harness pins it. MaxParallelProofreadChunks is forced to 1 (see HarnessOptions) AND every test
// independently proves the recorded chunk texts equal the real chunker's chunk texts IN ORDER, so a
// future parallelism change fails loudly instead of silently permuting the matrix.
//
// NO GPU, NO NETWORK, NO MODEL in c1. The default constructor never leaves the process.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Everything one per-chunk model call carried, captured at the <see cref="IAiRouter"/> seam - i.e.
/// exactly what <c>RunProofreadChunkedAsync</c> composed, before <c>AiRouter</c> appends its system
/// message and short pipeline instruction. That is the seam the production code owns and the one a
/// plumbing defect would show up at.
/// </summary>
/// <param name="CallIndex">
/// Order in which the router was INVOKED (0-based). THIS IS NOT THE CHUNK INDEX and must never be used
/// as one - that conflation was a real defect. <c>RunProofreadChunkedAsync</c> catches every per-chunk
/// exception (UnifiedAnalysisService.cs:2293), records a <c>FallbackError</c>, merges the ORIGINAL chunk
/// text and CARRIES ON, so a failed chunk produces no capture at all and every later chunk's invocation
/// ordinal is one lower than its chunk index. Correlate on <paramref name="ChunkIndex"/>.
/// </param>
/// <param name="ChunkIndex">
/// The index of the chunk this call actually carried, resolved BY IDENTITY (the unwrapped chunk text
/// matched against the chunker's own output) rather than by position. <see cref="RecordingChunkRouter.UnknownChunkIndex"/>
/// (-1) when the text matched no chunk - never a guess.
/// </param>
/// <param name="Instruction">The composed instruction, verbatim.</param>
/// <param name="WrappedInputText">The request's InputText, verbatim (wrapped on the chunked path).</param>
/// <param name="ChunkText">The chunk text with the [TEXT_TO_CORRECT] markers removed.</param>
/// <param name="HasCharacterRegisterBlock">Whether a [CHARACTER_REGISTER] section is present.</param>
/// <param name="CharacterRegisterBlock">Its rendered content, or null when absent.</param>
/// <param name="OverlapPrefix">The [CONTEXT_BEFORE] content, or null when the chunk carried none.</param>
/// <param name="Language">The request language.</param>
/// <param name="Tier">The tier stamped on the request.</param>
/// <param name="TaskType">The routed task type.</param>
/// <param name="ResponseContent">What the router returned for this chunk (replayed or live).</param>
public sealed record ChunkPromptCapture(
    int CallIndex,
    int ChunkIndex,
    string Instruction,
    string WrappedInputText,
    string ChunkText,
    bool HasCharacterRegisterBlock,
    string? CharacterRegisterBlock,
    string? OverlapPrefix,
    string Language,
    AiTier? Tier,
    AiTaskType TaskType,
    string ResponseContent)
{
    // NOTE: there is deliberately no "does the whole prompt mention this name" helper here. One existed
    // and claimed "the separation axis is measured on this", but nothing called it: the separation axis
    // is measured on the prompt with the [CHARACTER_REGISTER] block REMOVED (the register always renders
    // the name, so an un-stripped mention proves nothing), which both consumers do for themselves -
    // ChunkedAgreementLiveTests.MentionsNameOutsideRegister and
    // ChunkedAgreementHarnessTests.WithoutRegisterBlock. A helper whose docstring names a measurement it
    // does not perform is the exact defect class this corpus keeps finding.

    /// <summary>True when the chunk's own TEXT contains <paramref name="span"/>.</summary>
    public bool ChunkContains(string span) => ChunkText.Contains(span, StringComparison.Ordinal);

    /// <summary>True when the OVERLAP the chunk carried contains <paramref name="span"/>.</summary>
    public bool OverlapContains(string span) =>
        OverlapPrefix is not null && OverlapPrefix.Contains(span, StringComparison.Ordinal);
}

/// <summary>
/// A per-chunk model call that THREW instead of returning. See
/// <see cref="RecordingChunkRouter.Failures"/> for why this is captured separately.
/// </summary>
/// <param name="CallIndex">
/// Order in which the router was invoked (0-based, counting failed calls). NOT the chunk index. It used
/// to be read off the SUCCESSFUL-call list, which made a failure's index collide with a later successful
/// call's so the correlation could not be reconstructed at all; it is now a dedicated invocation counter,
/// and the chunk is reported by <paramref name="ChunkIndex"/>.
/// </param>
/// <param name="ChunkIndex">
/// The index of the chunk whose text the failed call carried, resolved by IDENTITY against the
/// chunker's own output. <see cref="RecordingChunkRouter.UnknownChunkIndex"/> (-1) when unmatched.
/// </param>
/// <param name="ChunkText">The chunk text the failed call carried.</param>
/// <param name="ExceptionType">The exception's type name.</param>
/// <param name="Message">The exception's message (first 400 chars).</param>
public sealed record ChunkCallFailure(
    int CallIndex, int ChunkIndex, string ChunkText, string ExceptionType, string Message);

/// <summary>
/// The capture instrument: an <see cref="IAiRouter"/> that records every request/response pair and
/// either REPLAYS a canned per-chunk response (deterministic, c1) or DELEGATES to a real inner
/// router (live, g1). See the file header for the swap.
/// </summary>
public sealed class RecordingChunkRouter : IAiRouter
{
    /// <summary>The markers <c>RunProofreadChunkedAsync</c> wraps each chunk's input in.</summary>
    public const string TextToCorrectOpen = "[TEXT_TO_CORRECT]";
    public const string TextToCorrectClose = "[/TEXT_TO_CORRECT]";

    /// <summary>Sentinel for "this call's text matched no chunk", stamped instead of guessing an index.</summary>
    public const int UnknownChunkIndex = -1;

    private readonly object _gate = new();
    private readonly List<ChunkPromptCapture> _calls = new();
    private readonly List<ChunkCallFailure> _failures = new();
    private readonly Func<ChunkPromptCapture, string>? _replay;
    private readonly IAiRouter? _inner;

    /// <summary>The chunker's chunk texts in order, or empty when the caller supplied none.</summary>
    private readonly string[] _chunkTexts;

    /// <summary>Which chunk indexes have already been claimed by a call. See <see cref="ResolveChunkIndex"/>.</summary>
    private readonly bool[] _claimed;

    /// <summary>Invocation counter for <see cref="ChunkPromptCapture.CallIndex"/>: every call, failed or not.</summary>
    private int _invocations;

    /// <param name="replay">
    /// Canned response for a chunk, given everything captured about it EXCEPT the response (the
    /// <c>ResponseContent</c> passed in is empty). Null = echo the request's InputText verbatim,
    /// which the chunked path's marker stripping turns back into the untouched chunk.
    /// </param>
    /// <param name="inner">
    /// When non-null the call is DELEGATED to this router (g1 passes the live <c>AiRouter</c>) and the
    /// real response is recorded. <paramref name="replay"/> is then ignored.
    /// </param>
    /// <param name="chunkTexts">
    /// The chunker's own chunk texts IN ORDER (<c>ChunkedAgreementHarness.Chunk</c>), used to stamp each
    /// capture with the index of the chunk it actually carries. Omitting it leaves every
    /// <c>ChunkIndex</c> at <see cref="UnknownChunkIndex"/> rather than silently falling back to the
    /// call ordinal - a wrong index is worse than an admitted unknown.
    /// </param>
    public RecordingChunkRouter(
        Func<ChunkPromptCapture, string>? replay = null,
        IAiRouter? inner = null,
        IReadOnlyList<string>? chunkTexts = null)
    {
        _replay = replay;
        _inner = inner;
        _chunkTexts = chunkTexts?.ToArray() ?? Array.Empty<string>();
        _claimed = new bool[_chunkTexts.Length];
    }

    /// <summary>
    /// WHICH CHUNK this call carries, resolved by IDENTITY rather than by position - the whole point of
    /// the fix. <c>RunProofreadChunkedAsync</c> swallows a per-chunk throw and carries on, so the call
    /// ordinal drifts away from the chunk index the moment any chunk fails, and every positional
    /// consumer then attributes a capture to the wrong chunk.
    ///
    /// Matching is exact-ordinal first, then trimmed-ordinal (the SINGLE-SHOT control's input is the
    /// whole un-wrapped target text while the chunker trims every segment, so the two can differ by
    /// surrounding whitespace and nothing else).
    ///
    /// DUPLICATE CHUNK TEXTS: the fixtures' chunks are distinct, but uniqueness is not assumed. A
    /// matched index is CLAIMED, so identical chunk texts are consumed in the order the calls arrive -
    /// which, with <c>MaxParallelProofreadChunks = 1</c>, is chunk order. A second call carrying an
    /// already-claimed text therefore takes the NEXT identical chunk rather than re-reporting the first,
    /// and a call whose text matches nothing left gets <see cref="UnknownChunkIndex"/>.
    ///
    /// Callers must hold <c>_gate</c>.
    /// </summary>
    private int ResolveChunkIndex(string chunkText)
    {
        for (var i = 0; i < _chunkTexts.Length; i++)
            if (!_claimed[i] && string.Equals(_chunkTexts[i], chunkText, StringComparison.Ordinal))
            {
                _claimed[i] = true;
                return i;
            }

        for (var i = 0; i < _chunkTexts.Length; i++)
            if (!_claimed[i] && string.Equals(_chunkTexts[i].Trim(), chunkText.Trim(), StringComparison.Ordinal))
            {
                _claimed[i] = true;
                return i;
            }

        return UnknownChunkIndex;
    }

    /// <summary>Every captured call, in the order the router was invoked.</summary>
    public IReadOnlyList<ChunkPromptCapture> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    /// <summary>
    /// Per-chunk calls that THREW. Added by g1 because the failure it was warned about is otherwise
    /// INVISIBLE and reads as a null agreement result.
    ///
    /// <c>RunProofreadChunkedAsync</c> catches every per-chunk exception
    /// (UnifiedAnalysisService.cs:2290), logs it, records a <c>FallbackError</c> outcome and merges the
    /// ORIGINAL chunk text. So a chunk that 500s produces a merged result identical to a chunk the model
    /// declined to change - i.e. "the agreement error was not corrected" - and <see cref="Calls"/> would
    /// simply be one shorter, since the recorded probe is only appended AFTER a successful await. On
    /// 2026-08-03 exactly this failure mode (every chunk 500ing while the single-shot path succeeded) was
    /// diagnosed as a CONCURRENCY defect, not a model result. A run that cannot tell the two apart cannot
    /// honestly report either, so the throw is captured here and rethrown unchanged.
    /// </summary>
    public IReadOnlyList<ChunkCallFailure> Failures
    {
        get { lock (_gate) return _failures.ToArray(); }
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var instruction = request.Instruction ?? "";
        var registerBlock = Section(instruction, "CHARACTER_REGISTER");
        var overlap = Section(instruction, "CONTEXT_BEFORE");

        var chunkText = Unwrap(request.InputText);

        int index;
        int chunkIndex;
        lock (_gate)
        {
            // A dedicated counter, NOT _calls.Count: the old reading shared the successful-call counter,
            // so a failed call's index collided with a later successful one's and neither the failure nor
            // the capture could be told apart afterwards.
            index = _invocations++;
            chunkIndex = ResolveChunkIndex(chunkText);
        }

        var probe = new ChunkPromptCapture(
            CallIndex: index,
            ChunkIndex: chunkIndex,
            Instruction: instruction,
            WrappedInputText: request.InputText,
            ChunkText: chunkText,
            HasCharacterRegisterBlock: registerBlock is not null,
            CharacterRegisterBlock: registerBlock,
            OverlapPrefix: overlap,
            Language: request.Language,
            Tier: request.Tier,
            TaskType: request.TaskType,
            ResponseContent: "");

        string content;
        string provider;
        string model;
        if (_inner is not null)
        {
            AiResponse live;
            try
            {
                live = await _inner.CompleteAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                // Record, then RETHROW unchanged: production's own per-chunk catch still runs and still
                // falls back to the original text, so the pipeline behaves exactly as it does in the
                // product. All this adds is that the failure is no longer silent. See Failures.
                lock (_gate)
                {
                    _failures.Add(new ChunkCallFailure(
                        index,
                        chunkIndex,
                        probe.ChunkText,
                        ex.GetType().Name,
                        ex.Message.Length > 400 ? ex.Message[..400] : ex.Message));
                }
                throw;
            }
            content = live.Content ?? "";
            provider = live.Provider;
            model = live.Model;
        }
        else
        {
            content = _replay?.Invoke(probe) ?? request.InputText;
            provider = "recording-chunk-router";
            model = "replay";
        }

        lock (_gate) _calls.Add(probe with { ResponseContent = content });

        return new AiResponse { Content = content, Provider = provider, Model = model };
    }

    /// <summary>
    /// Neither proofread path streams (both call <see cref="CompleteAsync"/>), so this exists only to
    /// satisfy the interface. It throws rather than returning empty: a silent empty stream would let a
    /// future caller believe it measured something.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompleteAsync(
        AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_inner is not null)
        {
            await foreach (var token in _inner.StreamCompleteAsync(request, cancellationToken))
                yield return token;
            yield break;
        }

        throw new NotSupportedException(
            "RecordingChunkRouter does not synthesize a stream. The proofread paths (single-shot and " +
            "chunked) both call CompleteAsync; a test reaching this has taken a different route than " +
            "the one this harness measures.");
    }

    /// <summary>The chunk text with the [TEXT_TO_CORRECT] wrapper removed (single-shot input is unwrapped).</summary>
    public static string Unwrap(string inputText)
    {
        var start = inputText.IndexOf(TextToCorrectOpen, StringComparison.Ordinal);
        if (start < 0) return inputText;
        start += TextToCorrectOpen.Length;
        var end = inputText.IndexOf(TextToCorrectClose, start, StringComparison.Ordinal);
        return end > start ? inputText.Substring(start, end - start) : inputText[start..];
    }

    /// <summary>
    /// The content of a <c>[NAME]...[/NAME]</c> section, or null when the section is absent.
    ///
    /// READS ONLY THE *LEADING* SECTION BLOCK, and that is load-bearing rather than fussy. The
    /// ProofreadHe body itself NAMES these markers in its prose - "אם מופיע
    /// [CONTEXT_BEFORE]...[/CONTEXT_BEFORE] — זהו הקשר בלבד" - so a naive first-occurrence scan
    /// finds the marker pair inside the INSTRUCTIONS and reports a [CONTEXT_BEFORE] section on a
    /// chunk that carried no overlap at all. That is the difference between "chunk 0 carried no
    /// overlap" and "chunk 0 carried the literal string '...'", i.e. between a correct attribution
    /// and a fabricated one. <c>PromptFactory.AppendSection</c> emits every section BEFORE the body
    /// in the exact shape <c>[NAME]\n{content}\n[/NAME]\n\n</c>, so the parse walks that prefix and
    /// stops at the first thing that is not a section.
    /// </summary>
    public static string? Section(string instruction, string name) =>
        LeadingSections(instruction).TryGetValue(name, out var content) ? content : null;

    /// <summary>Every section in the instruction's leading section block, in order. See <see cref="Section"/>.</summary>
    public static IReadOnlyDictionary<string, string> LeadingSections(string instruction)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(instruction)) return sections;

        var pos = 0;
        while (pos < instruction.Length && instruction[pos] == '[')
        {
            var nameEnd = instruction.IndexOf("]\n", pos, StringComparison.Ordinal);
            if (nameEnd < 0) break;

            var name = instruction[(pos + 1)..nameEnd];
            if (name.Length == 0 || name.Contains('\n') || name.StartsWith('/')) break;

            var close = $"\n[/{name}]\n\n";
            var closeAt = instruction.IndexOf(close, nameEnd, StringComparison.Ordinal);
            if (closeAt < 0) break;

            sections[name] = instruction[(nameEnd + 2)..closeAt];
            pos = closeAt + close.Length;
        }

        return sections;
    }
}

/// <summary>One harness run: the persisted result plus the per-chunk capture that explains it.</summary>
/// <param name="Fixture">The fixture that was run.</param>
/// <param name="Result">The AnalysisResult the production path persisted.</param>
/// <param name="Calls">
/// Per-chunk captures, in call order. NOT one per chunk and NOT positionally aligned with
/// <paramref name="Chunks"/>: a chunk whose model call threw produces no capture, so this list is one
/// shorter per failure. Correlate through <see cref="ChunkPromptCapture.ChunkIndex"/>.
/// </param>
/// <param name="ChunkTargetWords">The language-keyed target the run was actually chunked at.</param>
/// <param name="Chunks">
/// The chunker's own output for this fixture, computed MODEL-FREE through the production test seam
/// <c>UnifiedAnalysisService.ChunkForProofreadForTest</c>. Kept beside the captures so every test can
/// prove the captures line up with the real chunking rather than assuming it.
/// </param>
public sealed record ChunkedAgreementRun(
    ChunkedAgreementFixture Fixture,
    AnalysisResult Result,
    IReadOnlyList<ChunkPromptCapture> Calls,
    int ChunkTargetWords,
    IReadOnlyList<(string Text, string SeparatorAfter, string? OverlapPrefix)> Chunks)
{
    /// <summary>
    /// Per-chunk model calls that threw (empty on every deterministic run). See
    /// <see cref="RecordingChunkRouter.Failures"/>: a non-empty list means the run's "not corrected"
    /// verdicts are TRANSPORT failures, not model results, and no agreement conclusion may be drawn
    /// from it.
    /// </summary>
    public IReadOnlyList<ChunkCallFailure> Failures { get; init; } = Array.Empty<ChunkCallFailure>();

    /// <summary>True when the production path took the CHUNKED route (the "chunked" ModelName sentinel).</summary>
    public bool RanChunked => string.Equals(Result.ModelName, "chunked", StringComparison.Ordinal);

    /// <summary>
    /// The capture for the chunk that carries the fixture's error span, found BY
    /// <see cref="ChunkPromptCapture.ChunkIndex"/> - never by list position.
    ///
    /// NULL when that chunk produced no capture, which happens whenever its model call THREW: production
    /// swallows the throw, merges the original text and carries on, so <see cref="Calls"/> is simply one
    /// shorter and a positional read (<c>Calls[ExpectedErrorChunkIndex]</c>, what this used to be) either
    /// throws <c>ArgumentOutOfRangeException</c> or - worse - silently returns a NEIGHBOUR chunk's
    /// capture and reports it as the error chunk's. The absence is signalled explicitly instead; callers
    /// must decide what it means rather than being handed a plausible wrong answer.
    /// </summary>
    public ChunkPromptCapture? ErrorChunkCall =>
        Calls.FirstOrDefault(c => c.ChunkIndex == Fixture.ExpectedErrorChunkIndex);

    /// <summary>
    /// Suggestions whose ORIGINAL and SUGGESTED are both whitespace-only - i.e. an edit that changes no
    /// text at all.
    ///
    /// c1 introduced this because the harness was then producing ~19 of them per multi-paragraph
    /// fixture and g1 had to subtract them. c2 found the cause and removed it: the count came from
    /// feeding the diff an UN-stripped original while the response went through the stripper, which the
    /// mock did and production does not (see <see cref="ChunkedAgreementHarness.ProductionTargetText"/>
    /// and <c>ChunkedAgreementSanitizerArtifactTests</c>). On the production regime this list is now
    /// EMPTY for an untouched round trip, which the sanitizer tests assert outright.
    ///
    /// It is kept as a standing TRIPWIRE rather than deleted: if a future change reintroduces an
    /// asymmetric normalization, g1's over-correction column would silently fill with edits no model
    /// made, and this split is what makes that visible instead of plausible.
    /// </summary>
    public IReadOnlyList<AnalysisSuggestion> WhitespaceOnlySuggestions =>
        Result.Suggestions.Where(IsWhitespaceOnly).ToArray();

    /// <summary>Suggestions that actually touch text - the only ones an agreement verdict may read.</summary>
    public IReadOnlyList<AnalysisSuggestion> SubstantiveSuggestions =>
        Result.Suggestions.Where(s => !IsWhitespaceOnly(s)).ToArray();

    private static bool IsWhitespaceOnly(AnalysisSuggestion s) =>
        string.IsNullOrWhiteSpace(s.OriginalText) && string.IsNullOrWhiteSpace(s.SuggestedText);

    /// <summary>
    /// What the merged result MUST be for a given per-chunk model output: each chunk's output with its
    /// blank lines collapsed (what the sanitizer does to every response), rejoined by the chunker's own
    /// <c>SeparatorAfter</c> - which is appended AFTER sanitization and therefore survives. Stated
    /// independently of the production sanitizer so it is a CLAIM about the pipeline rather than a
    /// restatement of it.
    /// </summary>
    public string ExpectedMergedResult(Func<string, string>? perChunkEdit = null)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Chunks.Count; i++)
        {
            var text = perChunkEdit is null ? Chunks[i].Text : perChunkEdit(Chunks[i].Text);
            sb.Append(ChunkedAgreementHarness.CollapseBlankLines(text));
            if (i < Chunks.Count - 1)
                sb.Append(Chunks[i].SeparatorAfter);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Drives a <see cref="ChunkedAgreementFixture"/> through the production entry point with a
/// <see cref="RecordingChunkRouter"/> in place of the model.
/// </summary>
public static class ChunkedAgreementHarness
{
    /// <summary>
    /// The AiOptions every run uses. PRODUCTION DEFAULTS except for one forced value:
    /// <c>MaxParallelProofreadChunks = 1</c>, so the recorded call order IS the chunk order. Nothing
    /// else is touched - in particular <c>ProofreadChunkTargetWords</c> keeps its shipped 500 LATIN
    /// ceiling, which the language-keyed sizer resolves to ~250 for Hebrew. Lowering it (as the
    /// pre-existing chunked tests do, to force chunking on a short input) would size the fixtures
    /// against a threshold the product never uses.
    /// </summary>
    public static AiOptions HarnessOptions() => new() { MaxParallelProofreadChunks = 1 };

    /// <summary>The per-chunk word target THIS harness will chunk a fixture at (production accessor).</summary>
    public static int ChunkTargetWordsFor(ChunkedAgreementFixture fixture) =>
        UnifiedAnalysisService.ProofreadChunkTargetWordsFor(HarnessOptions(), fixture.Language);

    /// <summary>
    /// The fixture text EXACTLY as production would hand it to <c>RunAsync</c>, i.e. after the
    /// stripper every real target-resolution path applies.
    ///
    /// WHY THIS EXISTS (c2). <c>RunAsync</c> reads <c>context.TargetText</c>
    /// (UnifiedAnalysisService.cs:380), and every path that produces it runs the chapter/scene plain
    /// text through <c>SyncfusionWatermarkStripper.StripSyncfusionWatermark</c> first -
    /// <c>ResolveChapterAsync</c> (AnalysisContextService.cs:739), <c>ResolveSceneAsync</c>
    /// (AnalysisContextService.cs:1082/1093), <c>ResolveBookAsync</c>. There is NO production route by
    /// which un-stripped text reaches the diff's ORIGINAL side.
    ///
    /// That matters because the same stripper runs again on the model's RESPONSE
    /// (<c>SanitizeResponse</c> -> UnifiedAnalysisService.cs:3252) and it collapses <c>[\r\n]+</c> to a
    /// single <c>\n</c>. Applying it to only ONE of the two sides makes the collapse ASYMMETRIC, and an
    /// echoing model then comes back with a page of whitespace-only "corrections" nobody made. c1's mock
    /// handed the raw fixture text straight through and measured exactly that; it is an artifact of the
    /// mock, not of the product (see <c>ChunkedAgreementSanitizerArtifactTests</c> for the proof of both
    /// halves). Mirroring production here keeps g1 measuring a regime the product can actually enter -
    /// the same reason c1 drove <c>RunAsync</c> rather than reflecting into the private chunked method.
    ///
    /// It does NOT move the chunk matrix: <c>BuildChunkSegmentsCore</c> splits on <c>(\n+)</c> and trims
    /// every segment, so collapsing <c>\n\n</c> to <c>\n</c> changes only each chunk's
    /// <c>SeparatorAfter</c>, never the chunk texts, the word counts or the realized chunk count.
    /// </summary>
    public static string ProductionTargetText(ChunkedAgreementFixture fixture) =>
        SyncfusionWatermarkStripper.StripSyncfusionWatermark(fixture.Text);

    /// <summary>
    /// The chunker's output for a fixture, computed MODEL-FREE through the production test seam. This
    /// is the "assert the realized count by driving the chunker directly" step, and it is what the
    /// fixture tests gate on BEFORE any GPU is spent.
    /// </summary>
    public static IReadOnlyList<(string Text, string SeparatorAfter, string? OverlapPrefix)> Chunk(
        ChunkedAgreementFixture fixture) =>
        UnifiedAnalysisService.ChunkForProofreadForTest(
            ProductionTargetText(fixture), ChunkTargetWordsFor(fixture));

    /// <summary>
    /// Run one fixture through <c>UnifiedAnalysisService.RunAsync</c> - the real public entry point,
    /// which decides chunked-vs-single-shot itself - with the recording router standing in for the
    /// model.
    /// </summary>
    /// <param name="fixture">The fixture to run.</param>
    /// <param name="replay">Canned per-chunk response; null echoes the chunk back unchanged.</param>
    /// <param name="inner">A live router for g1; null keeps the run deterministic and offline.</param>
    public static async Task<ChunkedAgreementRun> RunAsync(
        ChunkedAgreementFixture fixture,
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

        // Pre-computed (model-free) so the router can stamp each capture with the index of the chunk it
        // actually carries. Without this the only correlation available is the call ORDINAL, which drifts
        // the moment production swallows a per-chunk failure and carries on.
        var chunks = Chunk(fixture);

        var router = new RecordingChunkRouter(replay, inner, chunks.Select(c => c.Text).ToArray());

        // The context service is the seam the register arrives through in production too: RunAsync
        // reads context.Characters and hands it to BuildProofreadChunkPrompt for every chunk. Nothing
        // else is populated - no StyleProfile, no PrecedingContext - which is what makes the
        // single-shot control's instruction byte-identical to a first chunk's.
        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, AnalysisType.Proofread,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                // Production parity, not the raw fixture: see ProductionTargetText.
                TargetText = ProductionTargetText(fixture),
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null,
                Characters = new CharacterRegister { Characters = fixture.Register }
            });

        var svc = new UnifiedAnalysisService(
            db,
            router,
            new PromptFactory(),
            new SfdtConversionService(),
            Options.Create(HarnessOptions()),
            NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(),
            contextMock.Object,
            new SuggestionDiffService(),
            // PRODUCTION DEFAULT (EnforceKtivMale = true), not a convenience "off". A ktiv-male
            // suggestion the deterministic checker adds would land in g1's over-correction column and
            // be misread as model overreach, so the fixtures are gated ktiv-clean instead
            // (ChunkedAgreementFixtureTests.NoFixture_TripsTheDeterministicKtivMaleChecker).
            new KtivMaleChecker(new HebrewStyleOptions()),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        var result = await svc.RunAsync(
            AnalysisScope.Chapter,
            AnalysisType.Proofread,
            chapterId,
            customPrompt: null,
            language: fixture.Language,
            jobId: null,
            ct: ct);

        return new ChunkedAgreementRun(
            fixture, result, router.Calls, ChunkTargetWordsFor(fixture), chunks)
        {
            Failures = router.Failures
        };
    }

    /// <summary>
    /// A replay that applies the fixture's expected fix INSIDE the chunk that carries the error and
    /// echoes every other chunk unchanged - i.e. "a model that got it right". Used to prove the
    /// replay seam and the merge carry a per-chunk edit through to the persisted result, which is what
    /// g1 will score off.
    /// </summary>
    public static Func<ChunkPromptCapture, string> ReplayCorrectFix(ChunkedAgreementFixture fixture) =>
        capture => capture.ChunkContains(fixture.ErrorSpan)
            ? capture.WrappedInputText.Replace(fixture.ErrorSpan, fixture.ExpectedFix, StringComparison.Ordinal)
            : capture.WrappedInputText;

    /// <summary>
    /// Every run of line breaks collapsed to ONE, plus the surrounding trim - what the production
    /// response sanitizer does to every proofread output (SyncfusionWatermarkStripper.cs:38).
    ///
    /// Since c2 made the harness feed <see cref="ProductionTargetText"/>, the chunk texts this is
    /// applied to no longer CONTAIN a blank line, so it is the identity on them. It stays because it
    /// states independently what the sanitizer does, and <c>ExpectedMergedResult</c> is a claim about
    /// the pipeline rather than a restatement of it.
    /// </summary>
    public static string CollapseBlankLines(string text) =>
        Regex.Replace(text, @"[\r\n]+", "\n").Trim();

    /// <summary>Word count on the same rule the chunker uses (whitespace runs), for fixture calibration.</summary>
    public static int WordCount(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : Regex.Split(text.Trim(), @"\s+").Count(s => s.Length > 0);
}
