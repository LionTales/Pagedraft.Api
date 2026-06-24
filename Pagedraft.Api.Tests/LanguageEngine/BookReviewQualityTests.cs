using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// PRECISION-GATED whole-book developmental REVIEW eval (wb2-c04). Runs the REAL per-dimension review
/// pipeline over each synthetic mini-book in book-review-gold.json and SCORES:
///   - planted-recall: did the engine flag the planted developmental defect in the RIGHT dimension +
///     verdict + chapter?
///   - dimension/verdict accuracy on the planted hit.
///   - CLEAN false-positive / low-value-note rate (the PRECISION GATE): how many improve/cut findings
///     the engine floods onto the clean-control mini-books, where the correct answer is FEW or NONE.
///   - a composite that rewards planted recall + dimension accuracy and PENALISES clean over-flagging.
///
/// PATH CHOICE - mirror BookReviewService.RunDimensionAsync, NOT the full BookReviewService:
/// BookReviewService needs an AppDbContext + BookContextAssembler + progress tracker + build registry and
/// resolves its book context from persisted ChapterBriefs. That is too heavy (and DB-coupled) to drive
/// over throwaway synthetic mini-books. So this harness drives the SAME underlying machinery directly,
/// exactly as RunDimensionAsync does (see BookReviewService.cs ~:518):
///   1. Build the [BOOK_CONTEXT] block (BookBrief + ChapterBriefs in PromptFactory.FormatBookBrief/
///      FormatChapterBrief shape) and prepend it to PromptFactory.BuildBookReviewPrompt(dimension, lang),
///      identical to BookReviewService.BuildBookContextSection + RunDimensionAsync.
///   2. Call IAiRouter.CompleteAsync with AiRequest { TaskType = AiTaskType.BookReview, Instruction =
///      [BOOK_CONTEXT]+dimensionPrompt, InputText = "", JsonMode = true } - byte-for-byte the request
///      RunDimensionAsync builds.
///   3. Parse the model JSON via the SAME UnifiedAnalysisService.ExtractJson extractor RunDimensionAsync
///      uses, into BookReviewResult, and read its Findings.
/// Only the DB/persistence wrapper (assembler + EF) is bypassed; every model-facing step is production code.
///
/// GATING - SKIP-BY-DEFAULT: probes the Ollama endpoint first; if unreachable, writes a message and
/// returns (passes, does not fail), so CI stays green with no model. Mirrors ProofreadQualityTests /
/// LinguisticQualityTests.
///
/// ENV MODEL KNOB: BOOK_REVIEW_MODEL overrides the model WITHOUT recompiling. Default = the configured
/// Ai:FeatureModels:BookReview model (gemma4:12b - kept in sync with appsettings.json manually).
/// </summary>
public class BookReviewQualityTests
{
    private readonly ITestOutputHelper _output;

    public BookReviewQualityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string OllamaBaseUrl = "http://localhost:11434";

    // Env var to override the scorer model WITHOUT recompiling (mirrors LinguisticQualityTests'
    // single-model env knob). Default = the production Ai:FeatureModels:BookReview model below.
    private const string ModelEnvVar = "BOOK_REVIEW_MODEL";

    // KEEP IN SYNC with Ai:FeatureModels:BookReview:Model in Pagedraft.Api/appsettings.json
    // (see "_comment_BookReview" key there). Active local default is gemma4:12b - the strongest LOCAL
    // structured-review model per the bake-off (same model LinguisticAnalysis uses). NOT read from the
    // same source at test time, so update this constant if appsettings changes.
    // internal so BookReviewServiceTests can reference it in the config-pin assertion (wb2-f01 guard).
    internal const string DefaultBookReviewModel = "gemma4:12b";

    // The six editorial dimensions, identical to the (private) BookReviewService.Dimensions array.
    // Order matches so per-dimension output is deterministic and comparable to the real pipeline.
    private static readonly string[] Dimensions =
        { "plot", "character", "pacing", "tone", "theme", "continuity" };

    private static readonly JsonSerializerOptions GoldOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // Same deserialize options BookReviewService uses to parse each per-dimension BookReviewResult.
    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ─── Composite scoring formula (documented once) ───────────────────────────────────────────────
    //
    // Over the gold set, per run-mode (per-dimension vs single-combined):
    //   plantedRecall   = planted defects flagged in the RIGHT dimension+chapter / total planted defects.
    //   verdictAccuracy = of the recalled planted defects, how many ALSO matched the expected verdict.
    //   cleanFpRate     = (improve/cut findings emitted on clean-control books) / (clean-control books),
    //                     clamped to [0,1] by dividing by an expected-max-per-book of 6 (one per dimension)
    //                     then clamping - so a book that floods all six dimensions reads as a full FP unit.
    //
    // composite = 0.50*plantedRecall + 0.25*verdictAccuracy - 0.25*cleanFpRate   (clamped to >= 0)
    // Recall dominates (catching the planted defect is the point); verdict accuracy is a secondary
    // correctness signal; clean over-flagging is the precision penalty (the failure mode this gate exists
    // to catch). INFORMATIONAL only - the test never fails on model quality.
    private const double RecallWeight = 0.50;
    private const double VerdictWeight = 0.25;
    private const double FpPenaltyWeight = 0.25;

    // Normaliser for cleanFpRate: treat ">= this many improve/cut findings on a clean book" as a full
    // false-positive unit (one per dimension = a complete flood).
    private const double CleanFpFloodPerBook = 6.0;

    private static double Composite(double plantedRecall, double verdictAccuracy, double cleanFpRate)
    {
        var raw = RecallWeight * plantedRecall
                  + VerdictWeight * verdictAccuracy
                  - FpPenaltyWeight * Math.Clamp(cleanFpRate, 0.0, 1.0);
        return Math.Max(0.0, raw);
    }

    // ─── The eval (single live run) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs BOTH the per-dimension pipeline AND a single-combined pass over the gold set ONCE (GPU budget),
    /// prints the per-book + aggregate tables for each, and prints a plain decomposition verdict comparing
    /// planted-recall + precision. Skip-by-default via the Ollama probe.
    /// </summary>
    [Fact]
    public async Task BookReviewQuality_RunGoldCases_PerDimensionVsSingleCombined()
    {
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl}. " +
                              "This whole-book review benchmark needs a live model; skipping so CI stays green.");
            return;
        }

        var books = LoadGold();
        if (books.Length == 0)
        {
            _output.WriteLine("No mini-books in book-review-gold.json.");
            return;
        }

        var model = ResolveModel();
        var router = CreateRouter(model);

        _output.WriteLine($"=== Whole-book REVIEW eval ({books.Length} mini-books, model={model}) ===");
        _output.WriteLine($"Model source: {(Environment.GetEnvironmentVariable(ModelEnvVar) is { Length: > 0 } ? ModelEnvVar + " env var" : "built-in default (Ai:FeatureModels:BookReview)")}");
        var plantedBooks = books.Count(b => !b.ExpectClean && b.PlantedDefects.Count > 0);
        var cleanBooks = books.Count(b => b.ExpectClean);
        var totalPlanted = books.Sum(b => b.PlantedDefects.Count(d => !IsKeep(d)));
        var totalKeep = books.Sum(b => b.PlantedDefects.Count(IsKeep));
        _output.WriteLine($"Gold composition: {plantedBooks} books with planted defects, {cleanBooks} clean-control books; " +
                          $"{totalPlanted} planted improve/cut defects + {totalKeep} planted keep-strengths.");
        _output.WriteLine("");

        // ─── Context-size certification (wb2-c06) ────────────────────────────────────────────────────
        // PREMISE NOTE: this harness builds [BOOK_CONTEXT] via BuildBookContext (NO assembler, NO budget
        // guard) and sends the WHOLE rendered context to the model, so the production assembler DROP path
        // (BookContextAssembler.AssembleStructuredWithFallbackAsync, DB-coupled, budget-guarded) is NOT
        // exercised here - that drop is unit-tested in BookContextAssemblerTests. What the big books DO
        // exercise is the regime that WOULD trip the assembler in production: their rendered context
        // EXCEEDS the derived production budget (NumCtx*BudgetFraction = 16384*0.5 = 8192 tokens for the
        // BookReview task), and in this harness it lands the model under real input+output pressure inside
        // its 16384 window. We print each book's estimated context tokens (SAME chars/4 heuristic the
        // assembler's EstimateTokens uses) and flag the ones over the 8192 production budget, so the eval
        // output itself certifies which books are in the over-budget regime.
        const int ProductionBookReviewBudget = 8192; // 16384 NumCtx * 0.5 BookContextBudgetFraction
        _output.WriteLine($"--- context size per book (assembler EstimateTokens heuristic; production BookReview budget = {ProductionBookReviewBudget} tokens) ---");
        _output.WriteLine($"{"book",-34} {"lang",4} {"chapters",8} {"ctx-chars",10} {"ctx-tokens",10}  over-budget?");
        var overBudget = 0;
        foreach (var book in books)
        {
            var ctx = BuildBookContext(book);
            var tokens = EstimateContextTokens(ctx);
            var over = tokens > ProductionBookReviewBudget;
            if (over) overBudget++;
            _output.WriteLine($"{Truncate(book.Id, 34),-34} {book.Language,4} {book.Chapters.Count,8} {ctx.Length,10} {tokens,10}  " +
                              (over ? $"YES (+{tokens - ProductionBookReviewBudget} over 8192 -> assembler WOULD drop in prod)" : "no"));
        }
        _output.WriteLine($"=> {overBudget}/{books.Length} book(s) exceed the {ProductionBookReviewBudget}-token production budget " +
                          "(the over-budget regime the tiny books never hit).");
        _output.WriteLine("");

        _output.WriteLine("########## RUN A: PER-DIMENSION (the real BookReviewService pipeline) ##########");
        var perDim = await ScoreAsync(router, books, RunMode.PerDimension);

        _output.WriteLine("");
        _output.WriteLine("########## RUN B: SINGLE-COMBINED (one prompt, all six dimensions) ##########");
        var combined = await ScoreAsync(router, books, RunMode.SingleCombined);

        // ─── Decomposition verdict ──────────────────────────────────────────────────────────────────
        _output.WriteLine("");
        _output.WriteLine("================= DECOMPOSITION DECISION (per-dimension vs single-combined) =================");
        _output.WriteLine($"{"metric",-26} {"per-dimension",16} {"single-combined",16}");
        _output.WriteLine(new string('-', 60));
        _output.WriteLine($"{"planted-recall",-26} {Pct(perDim.PlantedRecall),16} {Pct(combined.PlantedRecall),16}");
        _output.WriteLine($"{"verdict-accuracy",-26} {Pct(perDim.VerdictAccuracy),16} {Pct(combined.VerdictAccuracy),16}");
        _output.WriteLine($"{"keep-recall",-26} {Pct(perDim.KeepRecall),16} {Pct(combined.KeepRecall),16}");
        _output.WriteLine($"{"clean improve/cut findings",-26} {perDim.CleanImproveCutFindings,16} {combined.CleanImproveCutFindings,16}");
        _output.WriteLine($"{"clean-FP rate (norm.)",-26} {Pct(perDim.CleanFpRate),16} {Pct(combined.CleanFpRate),16}");
        _output.WriteLine($"{"total findings emitted",-26} {perDim.TotalFindings,16} {combined.TotalFindings,16}");
        _output.WriteLine($"{"errored dimensions/books",-26} {perDim.Errors,16} {combined.Errors,16}");
        _output.WriteLine($"{"composite",-26} {perDim.Composite.ToString("F3", CultureInfo.InvariantCulture),16} {combined.Composite.ToString("F3", CultureInfo.InvariantCulture),16}");
        _output.WriteLine("");
        _output.WriteLine(DecompositionVerdict(perDim, combined));

        _output.WriteLine("");
        _output.WriteLine("HEBREW DRAFT FLAG: every Hebrew mini-book/brief in book-review-gold.json is an AI-authored draft " +
                          "and REQUIRES NATIVE SPEAKER VALIDATION before the Hebrew numbers are trusted.");

        // Reporting benchmark, not a pass/fail gate on model quality - assert only that the run iterated
        // the gold set so the numbers surface without failing CI for model regressions.
        Assert.True(books.Length > 0);
    }

    /// <summary>
    /// Plain-language verdict the plan asked for: does per-dimension actually BEAT single-combined on this
    /// gold set? The plan prefers the cheaper single pass when they TIE. We compare on the two signals that
    /// matter - planted recall (catch the defect) and clean precision (do not flood) - and report which
    /// wins, with the (6x) cost asymmetry stated.
    /// </summary>
    private static string DecompositionVerdict(RunScore perDim, RunScore combined)
    {
        const double eps = 1e-9;
        var sb = new StringBuilder();

        var recallDelta = perDim.PlantedRecall - combined.PlantedRecall;
        var fpDelta = combined.CleanFpRate - perDim.CleanFpRate; // positive => per-dim is cleaner

        string recallWord = Math.Abs(recallDelta) < eps ? "TIES on planted recall"
            : recallDelta > 0 ? "WINS on planted recall" : "LOSES on planted recall";
        string fpWord = Math.Abs(fpDelta) < eps ? "ties on clean precision"
            : fpDelta > 0 ? "is cleaner (fewer clean false positives)" : "floods MORE on clean books";

        sb.AppendLine($"VERDICT: per-dimension {recallWord} and {fpWord} versus single-combined.");
        sb.AppendLine($"  planted-recall  per-dim {Pct(perDim.PlantedRecall)} vs combined {Pct(combined.PlantedRecall)} (delta {recallDelta:+0.0%;-0.0%;0.0%}).");
        sb.AppendLine($"  clean-FP (norm) per-dim {Pct(perDim.CleanFpRate)} vs combined {Pct(combined.CleanFpRate)} (lower is better).");
        sb.AppendLine($"  composite       per-dim {perDim.Composite:F3} vs combined {combined.Composite:F3}.");
        sb.AppendLine("  COST: per-dimension issues 6 model calls per book (one per dimension); single-combined issues 1. " +
                      "Per-dimension must MEANINGFULLY beat single-combined to justify the 6x GPU/latency cost.");

        bool perDimMeaningfullyBetter =
            (recallDelta > 0.05) || (Math.Abs(recallDelta) < 0.05 && fpDelta > 0.05 && perDim.Composite > combined.Composite + 0.02);
        bool roughlyTied =
            Math.Abs(recallDelta) <= 0.05 && Math.Abs(perDim.Composite - combined.Composite) <= 0.02;

        if (perDimMeaningfullyBetter)
            sb.AppendLine("  => PER-DIMENSION JUSTIFIED: it beats single-combined by more than the noise band on this gold set.");
        else if (roughlyTied)
            sb.AppendLine("  => TIE: single-combined matches per-dimension here. Per the plan, PREFER THE CHEAPER SINGLE PASS unless a larger/native-validated gold set later shows a real per-dimension edge.");
        else
            sb.AppendLine("  => SINGLE-COMBINED LOOKS AT LEAST AS GOOD (or better) on this gold set; the 6x per-dimension cost is NOT justified by these numbers.");

        sb.AppendLine("  CAVEAT: this is ONE run over a SMALL synthetic gold set (Hebrew unvalidated); treat as directional, not a final ruling.");
        return sb.ToString();
    }

    private enum RunMode { PerDimension, SingleCombined }

    /// <summary>
    /// Score one run-mode over the gold set. PerDimension fans out the six dimension prompts per book
    /// (exactly like BookReviewService); SingleCombined sends ONE all-dimensions prompt per book. Both
    /// parse into BookReviewResult and union the findings, then score planted recall / verdict / clean FP.
    /// </summary>
    private async Task<RunScore> ScoreAsync(IAiRouter router, GoldBook[] books, RunMode mode)
    {
        var plantedTotal = 0;        // planted improve/cut defects across all books
        var plantedHits = 0;         // planted defects flagged in the right dimension + chapter
        var verdictHits = 0;         // of the hits, those that also matched the expected verdict
        var keepTotal = 0;           // planted keep-strengths
        var keepHits = 0;            // keep-strengths the engine surfaced as a keep in the right dimension
        var cleanBooks = 0;
        var cleanImproveCut = 0;     // improve/cut findings emitted on clean-control books (the FP signal)
        var totalFindings = 0;       // every finding emitted across all books (volume signal)
        var errors = 0;              // dimensions (per-dim) / books (combined) that errored or did not parse

        _output.WriteLine($"{"book",-30} {"lang",4} {"kind",8} {"findings",8}  planted-result");

        foreach (var book in books)
        {
            var context = BuildBookContext(book);
            List<BookFindingItem> findings;
            int bookErrors;

            if (mode == RunMode.PerDimension)
                (findings, bookErrors) = await RunPerDimensionAsync(router, book, context);
            else
                (findings, bookErrors) = await RunSingleCombinedAsync(router, book, context);

            errors += bookErrors;
            totalFindings += findings.Count;

            var kind = book.ExpectClean ? "clean" : (book.PlantedDefects.All(IsKeep) ? "keep" : "planted");

            if (book.ExpectClean)
            {
                cleanBooks++;
                // FALSE-POSITIVE signal: only improve/cut findings count as over-flagging. A 'keep' on a
                // clean book is a legitimate strength callout, not a false positive.
                var fp = findings.Count(f => !IsKeepVerdict(f.Verdict));
                cleanImproveCut += fp;
                var note = fp == 0
                    ? "clean -> no improve/cut findings (good)"
                    : $"OVER-FLAG x{fp}" + (book.MaxFindings is { } mx && fp > mx ? $" (> max {mx})" : "") +
                      " [" + string.Join("; ", findings.Where(f => !IsKeepVerdict(f.Verdict)).Take(3)
                          .Select(f => $"{f.Dimension}/{f.Verdict}@{PrimaryOrder(f)}")) + "]";
                _output.WriteLine($"{Truncate(book.Id, 30),-30} {book.Language,4} {kind,8} {findings.Count,8}  {note}");
                continue;
            }

            // Planted-defect book: check each planted defect against the union of findings.
            var perDefectNotes = new List<string>();
            foreach (var defect in book.PlantedDefects)
            {
                var allowedDims = AllowedDimensions(defect);
                var match = findings.FirstOrDefault(f =>
                    allowedDims.Contains((f.Dimension ?? string.Empty).Trim().ToLowerInvariant())
                    && AnchorsChapter(f, defect.ChapterOrder));

                if (IsKeep(defect))
                {
                    keepTotal++;
                    // keep-recall: a keep finding in an allowed dimension (chapter anchor is lenient for a
                    // book-wide strength - any anchor or none counts).
                    var keepMatch = findings.FirstOrDefault(f =>
                        allowedDims.Contains((f.Dimension ?? string.Empty).Trim().ToLowerInvariant())
                        && IsKeepVerdict(f.Verdict));
                    if (keepMatch != null) { keepHits++; perDefectNotes.Add($"KEEP[{defect.Dimension}] HIT"); }
                    else perDefectNotes.Add($"KEEP[{defect.Dimension}] missed");
                    continue;
                }

                plantedTotal++;
                if (match != null)
                {
                    plantedHits++;
                    var verdictOk = VerdictMatches(defect.Verdict, match.Verdict);
                    if (verdictOk) verdictHits++;
                    perDefectNotes.Add($"{defect.Dimension}@{defect.ChapterOrder} HIT" +
                                       (verdictOk ? $" (verdict {match.Verdict} ok)" : $" (WRONG verdict {match.Verdict}, want {defect.Verdict})"));
                }
                else
                {
                    // Did it flag the right dimension but wrong chapter? Surface that as a near-miss.
                    var dimOnly = findings.FirstOrDefault(f => allowedDims.Contains((f.Dimension ?? string.Empty).Trim().ToLowerInvariant()));
                    perDefectNotes.Add($"{defect.Dimension}@{defect.ChapterOrder} MISSED" +
                                       (dimOnly != null ? $" (dim flagged but @{PrimaryOrder(dimOnly)} not {defect.ChapterOrder})" : " (dimension not flagged)"));
                }
            }

            _output.WriteLine($"{Truncate(book.Id, 30),-30} {book.Language,4} {kind,8} {findings.Count,8}  {string.Join(" | ", perDefectNotes)}");
        }

        var plantedRecall = plantedTotal > 0 ? (double)plantedHits / plantedTotal : 0.0;
        var verdictAccuracy = plantedHits > 0 ? (double)verdictHits / plantedHits : 0.0;
        var keepRecall = keepTotal > 0 ? (double)keepHits / keepTotal : 0.0;
        // Normalise clean FP: average improve/cut findings per clean book, divided by the flood threshold.
        var cleanFpRate = cleanBooks > 0
            ? Math.Clamp(((double)cleanImproveCut / cleanBooks) / CleanFpFloodPerBook, 0.0, 1.0)
            : 0.0;
        var composite = Composite(plantedRecall, verdictAccuracy, cleanFpRate);

        var score = new RunScore(
            plantedTotal, plantedHits, verdictHits, keepTotal, keepHits,
            cleanBooks, cleanImproveCut, totalFindings, errors,
            plantedRecall, verdictAccuracy, keepRecall, cleanFpRate, composite);

        _output.WriteLine("");
        _output.WriteLine($"--- {mode} aggregate ---");
        _output.WriteLine($"Planted defects:       {plantedTotal}");
        _output.WriteLine($"Planted recall:        {Pct(plantedRecall)} ({plantedHits}/{plantedTotal})");
        _output.WriteLine($"Verdict accuracy:      {Pct(verdictAccuracy)} ({verdictHits}/{Math.Max(1, plantedHits)} of recalled)");
        _output.WriteLine($"Keep-strength recall:  {Pct(keepRecall)} ({keepHits}/{keepTotal})");
        _output.WriteLine($"Clean books:           {cleanBooks}");
        _output.WriteLine($"Clean improve/cut FPs: {cleanImproveCut} (PRECISION GATE; lower better)");
        _output.WriteLine($"Clean-FP rate (norm.): {Pct(cleanFpRate)}");
        _output.WriteLine($"Total findings:        {totalFindings}");
        _output.WriteLine($"Errored units:         {errors}");
        _output.WriteLine($"Composite:             {composite.ToString("F3", CultureInfo.InvariantCulture)}");

        return score;
    }

    /// <summary>
    /// Per-dimension run: fan out the six dimension prompts for one book, parse each into a
    /// BookReviewResult, stamp the dimension (as BookReviewService does), and union the findings. A
    /// dimension that errors or does not parse contributes zero findings and increments the error count
    /// (it never aborts the book) - mirroring RunDimensionAsync's null-as-zero behaviour.
    /// </summary>
    private async Task<(List<BookFindingItem> findings, int errors)> RunPerDimensionAsync(
        IAiRouter router, GoldBook book, string bookContext)
    {
        var all = new List<BookFindingItem>();
        var errors = 0;

        foreach (var dimension in Dimensions)
        {
            var instruction = bookContext + _promptFactory.BuildBookReviewPrompt(dimension, book.Language);
            var parsed = await CallAndParseAsync(router, instruction, book.Language);
            if (parsed?.Findings == null)
            {
                errors++;
                continue;
            }
            foreach (var f in parsed.Findings)
                f.Dimension = dimension; // defensive stamp, exactly like BookReviewService.RunDimensionAsync
            all.AddRange(parsed.Findings);
        }

        return (all, errors);
    }

    /// <summary>
    /// Single-combined run: ONE prompt asking for findings across ALL six dimensions at once. Uses the
    /// PRODUCTION combined prompt (PromptFactory.BuildBookReviewCombinedPrompt) - the SAME prompt
    /// BookReviewService.RunCombinedCallAsync ships - so RUN B measures production, not a harness-local copy.
    /// The model self-labels each finding's dimension; we do NOT stamp it here (that is the point of the
    /// combined pass), matching the production combined path.
    /// </summary>
    private async Task<(List<BookFindingItem> findings, int errors)> RunSingleCombinedAsync(
        IAiRouter router, GoldBook book, string bookContext)
    {
        // Use the PRODUCTION combined prompt (PromptFactory.BuildBookReviewCombinedPrompt), exactly as
        // BookReviewService.RunCombinedCallAsync does, so RUN B measures the SAME prompt production ships,
        // not a harness-local copy that could drift from it (wb2-r02 reconciliation).
        var instruction = bookContext + _promptFactory.BuildBookReviewCombinedPrompt(book.Language);
        var parsed = await CallAndParseAsync(router, instruction, book.Language);
        if (parsed?.Findings == null)
            return (new List<BookFindingItem>(), 1);
        return (parsed.Findings, 0);
    }

    /// <summary>
    /// Build the AiRequest exactly as BookReviewService.RunDimensionAsync does (empty InputText; the whole
    /// book lives in the instruction's [BOOK_CONTEXT]; TaskType = BookReview; JsonMode = true), call the
    /// router, and parse via the SAME UnifiedAnalysisService.ExtractJson extractor into BookReviewResult.
    /// Returns null on any model error / unparseable output (caller treats null as zero findings).
    /// </summary>
    private static async Task<BookReviewResult?> CallAndParseAsync(IAiRouter router, string instruction, string language)
    {
        try
        {
            var request = new AiRequest
            {
                InputText = string.Empty,
                Instruction = instruction,
                TaskType = AiTaskType.BookReview,
                Language = language,
                JsonMode = true
            };

            var response = await router.CompleteAsync(request);
            var raw = response.Content;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var json = UnifiedAnalysisService.ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<BookReviewResult>(json, ParseOpts);
        }
        catch
        {
            return null;
        }
    }

    // ─── Prompt + context construction ──────────────────────────────────────────────────────────────

    private static readonly PromptFactory _promptFactory = new();

    /// <summary>
    /// Renders the mini-book into the [BOOK_CONTEXT] block, mirroring BookReviewService.BuildBookContextSection
    /// wrapping a BookBrief + ChapterBriefs body. The body uses the SAME field layout PromptFactory's
    /// FormatBookBrief / FormatChapterBrief produce, so the model sees production-faithful context.
    /// </summary>
    private static string BuildBookContext(GoldBook book)
    {
        var body = new StringBuilder();

        var b = book.BookBrief;
        if (b != null)
        {
            if (!string.IsNullOrWhiteSpace(b.Genre))
                body.AppendLine($"Genre: {b.Genre}{(string.IsNullOrWhiteSpace(b.SubGenre) ? "" : $" / {b.SubGenre}")}");
            if (!string.IsNullOrWhiteSpace(b.TargetAudience))
                body.AppendLine($"Audience: {b.TargetAudience}");
            if (b.LiteratureLevel.HasValue)
                body.AppendLine($"Literature level: {b.LiteratureLevel}/10");
            if (b.Themes is { Count: > 0 })
                body.AppendLine($"Themes: {string.Join(", ", b.Themes)}");
            if (!string.IsNullOrWhiteSpace(b.Synopsis))
                body.AppendLine($"Synopsis: {b.Synopsis}");
            body.AppendLine();
        }

        foreach (var ch in book.Chapters.OrderBy(c => c.Order))
        {
            body.AppendLine($"Chapter {ch.Order}: {ch.Title}");
            if (!string.IsNullOrWhiteSpace(ch.Summary))
                body.AppendLine(ch.Summary);
            body.AppendLine();
        }

        var sb = new StringBuilder();
        sb.Append("[BOOK_CONTEXT]\n");
        sb.Append(body.ToString().Trim());
        sb.Append("\n[/BOOK_CONTEXT]\n\n");
        return sb.ToString();
    }

    // ─── Matching helpers ───────────────────────────────────────────────────────────────────────────

    private static bool IsKeep(PlantedDefect d) => IsKeepVerdict(d.Verdict);
    private static bool IsKeepVerdict(string? verdict) =>
        string.Equals((verdict ?? string.Empty).Trim(), "keep", StringComparison.OrdinalIgnoreCase);

    /// <summary>The set of dimensions a planted defect accepts as correct. Some defects (an abandoned
    /// thread) legitimately read as either of two dimensions, declared in the gold via acceptDimensions.</summary>
    private static HashSet<string> AllowedDimensions(PlantedDefect d)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(d.Dimension)) set.Add(d.Dimension.Trim().ToLowerInvariant());
        if (d.AcceptDimensions != null)
            foreach (var a in d.AcceptDimensions)
                if (!string.IsNullOrWhiteSpace(a)) set.Add(a.Trim().ToLowerInvariant());
        return set;
    }

    /// <summary>The planted verdict plus any sibling verdicts the gold accepts (e.g. a contradiction
    /// accepts improve OR cut). Declared via acceptVerdicts.</summary>
    private static bool VerdictMatches(string expected, string? actual)
    {
        var a = (actual ?? string.Empty).Trim();
        if (string.Equals(a, expected?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        // A contradiction/dropped-thread is correctly handled by either improve or cut; accept both for
        // any non-keep planted defect so we do not punish a legitimate cut-vs-improve choice.
        if (!IsKeepVerdict(expected) &&
            (string.Equals(a, "improve", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(a, "cut", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    /// <summary>Does the finding anchor the expected chapter? Lenient: matches if any chapterAnchor.order
    /// or any evidence.chapterOrder equals the expected order, OR (for a book-wide finding) it has no
    /// anchors at all. Off-by-one neighbours (order +/- 1) also count, since a sag/break spanning two
    /// chapters may be anchored on either.</summary>
    private static bool AnchorsChapter(BookFindingItem f, int expectedOrder)
    {
        var orders = new List<int>();
        if (f.ChapterAnchors != null) orders.AddRange(f.ChapterAnchors.Select(a => a.Order));
        if (f.Evidence != null) orders.AddRange(f.Evidence.Select(e => e.ChapterOrder));
        if (orders.Count == 0) return true; // book-wide finding: do not punish a missing anchor
        return orders.Any(o => Math.Abs(o - expectedOrder) <= 1);
    }

    private static int PrimaryOrder(BookFindingItem f)
    {
        if (f.ChapterAnchors is { Count: > 0 }) return f.ChapterAnchors[0].Order;
        if (f.Evidence is { Count: > 0 }) return f.Evidence[0].ChapterOrder;
        return 0;
    }

    /// <summary>Estimated token cost of the rendered [BOOK_CONTEXT] using the SAME chars/4 heuristic the
    /// production BookContextAssembler.EstimateTokens uses, so the harness's over-budget certification lines
    /// up with the budget the assembler would apply (NumCtx*BudgetFraction). Kept local (not a reference to
    /// the assembler) so this measurement-only harness stays free of the DB-coupled assembler dependency.</summary>
    private static int EstimateContextTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    private static string Pct(double v) => v.ToString("P0", CultureInfo.InvariantCulture);

    private static string Truncate(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private string ResolveModel()
    {
        var raw = Environment.GetEnvironmentVariable(ModelEnvVar);
        return string.IsNullOrWhiteSpace(raw) ? DefaultBookReviewModel : raw.Trim();
    }

    // ─── Gold loading ───────────────────────────────────────────────────────────────────────────────

    private static GoldBook[] LoadGold()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "book-review-gold.json");
        if (!File.Exists(path))
            return Array.Empty<GoldBook>();
        var json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize<GoldBook[]>(json, GoldOpts);
        if (raw == null) return Array.Empty<GoldBook>();
        // The first array element is a {_README/_hebrewValidation} metadata note (no chapters); skip any
        // entry that has no chapters so iteration only sees real mini-books.
        return raw.Where(b => b.Chapters is { Count: > 0 }).ToArray();
    }

    private sealed class GoldBook
    {
        public string Id { get; set; } = "";
        public string Language { get; set; } = "he";
        public bool ExpectClean { get; set; }
        public int? MaxFindings { get; set; }
        public GoldBookBrief? BookBrief { get; set; }
        public List<GoldChapter> Chapters { get; set; } = new();
        public List<PlantedDefect> PlantedDefects { get; set; } = new();
        public string? Notes { get; set; }
    }

    private sealed class GoldBookBrief
    {
        public string? Genre { get; set; }
        public string? SubGenre { get; set; }
        public string? TargetAudience { get; set; }
        public int? LiteratureLevel { get; set; }
        public List<string>? Themes { get; set; }
        public string? Synopsis { get; set; }
    }

    private sealed class GoldChapter
    {
        public int Order { get; set; }
        public string Title { get; set; } = "";
        public string? Summary { get; set; }
    }

    private sealed class PlantedDefect
    {
        public string Dimension { get; set; } = "";
        /// <summary>Optional sibling dimensions the gold also accepts as correct (e.g. an abandoned thread
        /// reads as plot OR continuity).</summary>
        public string[]? AcceptDimensions { get; set; }
        public string Verdict { get; set; } = "";
        public string[]? AcceptVerdicts { get; set; }
        public int ChapterOrder { get; set; }
        public string? Note { get; set; }
    }

    private readonly record struct RunScore(
        int PlantedTotal, int PlantedHits, int VerdictHits, int KeepTotal, int KeepHits,
        int CleanBooks, int CleanImproveCutFindings, int TotalFindings, int Errors,
        double PlantedRecall, double VerdictAccuracy, double KeepRecall, double CleanFpRate, double Composite);

    // ─── Ollama reachability probe (skip-gate) — mirrors ProofreadQualityTests / LinguisticQualityTests ─

    private static async Task<bool> IsOllamaReachableAsync()
    {
        // Probe both the configured host and the explicit IPv4 loopback: .NET's "localhost" can resolve to
        // ::1 (IPv6) while Ollama binds 127.0.0.1, which would otherwise make a reachable server look down.
        if (await ProbeAsync(OllamaBaseUrl)) return true;
        if (!OllamaBaseUrl.Contains("127.0.0.1") &&
            await ProbeAsync(OllamaBaseUrl.Replace("localhost", "127.0.0.1")))
            return true;
        return false;
    }

    private static async Task<bool> ProbeAsync(string baseUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resp = await client.GetAsync($"{baseUrl}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ─── Router DI (mirrors LinguisticQualityTests.CreateRouter, routing the BookReview task) ───────────

    private static IAiRouter CreateRouter(string model)
    {
        const string provider = "Ollama";
        // Override Ai:DefaultProvider/DefaultModel AND Ai:FeatureModels:BookReview in the SAME in-memory
        // builder the router resolves through, so the BookReview task routes to Ollama/`model`. DefaultModel
        // is also set (config + AiOptions) so OllamaProvider's 404-retry-with-default cannot silently fall
        // back to a different working model when `model` is not pulled.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = provider,
                ["Ai:Providers:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["Ai:DefaultModel"] = model,
                ["Ai:FeatureModels:BookReview:Provider"] = provider,
                ["Ai:FeatureModels:BookReview:Model"] = model
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        // Whole-book review prompts are long (full book context + six dimensions) and gemma4:12b may
        // CPU-spill on the 8 GB dev GPU, so a single dimension call can take minutes. Raise the Ollama
        // client timeout to 10 minutes for the harness ONLY (production wiring in Program.cs is untouched).
        services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient(string.Empty, client => client.Timeout = TimeSpan.FromMinutes(10));

        services.Configure<AiOptions>(opts =>
        {
            opts.DefaultProvider = provider;
            opts.DefaultModel = model;
            opts.FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["BookReview"] = new FeatureModelOptions { Provider = provider, Model = model }
            };
        });
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
        {
            var c = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<IOptions<AiOptions>>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama"] = new OllamaProvider(factory, c, opts),
                ["Anthropic"] = new AnthropicProvider(factory, c, opts),
                ["OpenAI"] = new OpenAiProvider(factory, c, opts)
            };
        });
        services.AddSingleton<IAiRouter, AiRouter>();

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IAiRouter>();
    }
}
