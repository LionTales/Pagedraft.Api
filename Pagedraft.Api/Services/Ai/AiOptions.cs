namespace Pagedraft.Api.Services.Ai;

/// <summary>Root AI configuration (mirrors appsettings "Ai" section).</summary>
public class AiOptions
{
    public const string SectionName = "Ai";

    // Chunk-target defaults: both 500 by design. Matches production config and client/server contract (api/config/analysis-chunk-thresholds).
    /// <summary>Default used when ProofreadChunkTargetWords is not set or &lt;= 0. Kept in sync with effective resolution used by server and config API.</summary>
    public const int DefaultProofreadChunkTargetWords = 500;
    /// <summary>Default used when LineEditChunkTargetWords is not set or &lt;= 0. Kept in sync with effective resolution used by server and config API.</summary>
    public const int DefaultLineEditChunkTargetWords = 500;

    public string DefaultProvider { get; set; } = "Ollama";
    public string DefaultModel { get; set; } = "qwen2.5:14b";

    // Provider-specific blocks (Ollama, OpenAI, Azure, Anthropic) are read via IConfiguration["Ai:Providers:{Name}"]
    public Dictionary<string, ProviderTuningOptions>? ProviderSettings { get; set; }
    public Dictionary<string, FeatureModelOptions>? FeatureModels { get; set; }

    /// <summary>
    /// Analysis-output repair layer config (analysis-output-repair plan, p6-config). Gates the repair
    /// stages in <see cref="Analysis.UnifiedAnalysisService.ApplyAnalysisRepairAsync"/>. See
    /// <see cref="AnalysisRepairOptions"/> for the three-way semantics (Enabled / GuardOnly / PerType).
    /// A null block OR Enabled=false is a FULL no-op (neither the deterministic glossary nor the LLM
    /// pass runs). The SHIPPED default (appsettings "Ai:AnalysisRepair") is { Enabled:true,
    /// GuardOnly:true } = deterministic glossary ONLY, LLM off — the p3-gate GUARD-ONLY decision.
    /// KEEP IN SYNC with the appsettings "Ai:AnalysisRepair" block (and its Model with
    /// "Ai:FeatureModels:AnalysisRepair").
    /// </summary>
    public AnalysisRepairOptions? AnalysisRepair { get; set; }

    /// <summary>Proofread chunking: when text exceeds ChunkTargetWords, split and run in parallel.</summary>
    public int ProofreadChunkTargetWords { get; set; } = DefaultProofreadChunkTargetWords;
    /// <summary>Max concurrent LLM requests when proofreading in chunks.</summary>
    public int MaxParallelProofreadChunks { get; set; } = 2;

    /// <summary>LineEdit chunking: when text exceeds ChunkTargetWords, split and run in parallel. Default 500.</summary>
    public int LineEditChunkTargetWords { get; set; } = DefaultLineEditChunkTargetWords;

    /// <summary>Effective proofread chunk target: configured value if &gt; 0, otherwise <see cref="DefaultProofreadChunkTargetWords"/>.</summary>
    public int EffectiveProofreadChunkTargetWords => ProofreadChunkTargetWords > 0 ? ProofreadChunkTargetWords : DefaultProofreadChunkTargetWords;
    /// <summary>Effective line-edit chunk target: configured value if &gt; 0, otherwise <see cref="DefaultLineEditChunkTargetWords"/>.</summary>
    public int EffectiveLineEditChunkTargetWords => LineEditChunkTargetWords > 0 ? LineEditChunkTargetWords : DefaultLineEditChunkTargetWords;
    /// <summary>Max concurrent LLM requests when running LineEdit in chunks.</summary>
    public int MaxParallelLineEditChunks { get; set; } = 2;

    /// <summary>
    /// Max concurrent chapter style-profile (re)builds when building a book-wide style baseline
    /// (StyleBaselineService). Mirrors the proofread/line-edit chunk-parallelism cap idiom; kept small
    /// (default 2) because each build is a full LinguisticAnalysis LLM call and the local model is the
    /// bottleneck.
    /// </summary>
    public int MaxParallelStyleBaselineChapters { get; set; } = 2;

    /// <summary>
    /// Max concurrent per-dimension whole-book review LLM calls (BookReviewService, wb2-c02). The review
    /// fans out 6 single-dimension prompts (plot/character/pacing/tone/theme/continuity) over ONE assembled
    /// book context; this caps how many run at once. Mirrors the
    /// <see cref="MaxParallelStyleBaselineChapters"/> cap idiom (used via <c>Math.Max(1, ...)</c>) but is a
    /// SEPARATE knob: a review call sends the whole book context (a much larger prompt than a single
    /// chapter's style-baseline call), so its safe concurrency differs from the chapter-fanout cap. Default
    /// 3 (half of the 6 dimensions) keeps the local model from thrashing while still overlapping work.
    /// </summary>
    public int MaxParallelBookReviewDimensions { get; set; } = 3;

    /// <summary>
    /// Whole-book review fan-out strategy (BookReviewService, wb2-r02). When TRUE (the DEFAULT) the review
    /// runs ONE combined LLM call that assesses all six dimensions
    /// (plot/character/pacing/tone/theme/continuity) over a single assembled book context; when FALSE it
    /// falls back to the per-dimension fan-out (six calls, capped by
    /// <see cref="MaxParallelBookReviewDimensions"/>).
    ///
    /// WHY single-combined is the default: the wb2-c04 eval measured per-dimension (6 calls/book) as a
    /// QUALITY TIE with single-combined (1 call/book) - same 100% planted-recall, same 0 clean false
    /// positives, identical composite - while per-dimension costs 6x the model calls and emits more noise.
    /// wb2-c06 then showed the per-dimension fan-out (3 parallel calls at the post-c05 NumCtx=16384) CRASHES
    /// the 8 GB dev GPU on big books, while a single combined call completes. So single-combined is cheaper,
    /// no worse on quality, and survives the dev GPU. Per-dimension is KEPT behind this toggle purely so a
    /// future larger-GPU host can re-measure whether the fan-out earns its cost; flip to false to use it.
    /// </summary>
    public bool BookReviewSingleCombined { get; set; } = true;

    /// <summary>
    /// Max tokens the trimmed BookBrief may occupy when it is repeated at the head of EVERY window of the
    /// windowed whole-book review path (BookContextAssembler.AssembleWindowsAsync, wb4-c01). The BookBrief is
    /// the global anchor placed first in each window and charged to that window's budget; a full BookBrief
    /// (especially its Synopsis) can eat a large share of a window and starve it of chapters, so for windows
    /// the Synopsis is CAPPED to fit this budget (the other, short metadata lines are always kept). The FULL,
    /// untrimmed BookBrief is used only by the reduce passes (wb4-c04/c05), never by the per-window fan-out.
    /// Default ~800 tokens. Clamped to a small positive minimum so at least the metadata header survives.
    /// </summary>
    public int BookReviewWindowBriefMaxTokens { get; set; } = 800;

    /// <summary>
    /// How many chapters at the TAIL of window i are repeated at the HEAD of window i+1 in the windowed
    /// whole-book review path (BookContextAssembler.AssembleWindowsAsync, wb4-c01), so an issue that straddles
    /// a window boundary is visible to at least one window intact. Kept small (default 1) because the
    /// continuity-reduce pass is the real cross-window net; a larger K wastes budget re-sending chapters.
    /// The overlap is ADDITIONAL to each chapter's single PRIMARY window: every chapter still lands in exactly
    /// one primary window (no chapter is dropped), and the first K chapters of a window are its overlap tail
    /// from the previous window. Clamped to &gt;= 0 (0 disables overlap).
    /// </summary>
    public int BookReviewWindowOverlapChapters { get; set; } = 1;

    /// <summary>
    /// Hard token budget for the assembled WHOLE-BOOK analysis context (BookContextAssembler): the L2
    /// BookBrief plus as many L1 ChapterBriefs (or, in the degraded path, flat chapter summaries) as fit.
    /// When &lt;= 0 the budget is DERIVED from the active task model's <see cref="ProviderTuningOptions.NumCtx"/>
    /// context window via <see cref="EffectiveBookContextTokenBudget"/> rather than hardcoded, so raising
    /// NumCtx automatically widens the book budget. Set a positive value to override the derivation.
    /// Exists to stop the previously unguarded book-level concat from silently overflowing the model
    /// context (Ollama TRUNCATES anything past num_ctx, yielding broken/empty output).
    /// </summary>
    public int BookContextTokenBudget { get; set; } = 0;

    /// <summary>
    /// Fraction of the model context window (NumCtx) the assembled book context may occupy when the budget
    /// is derived (i.e. <see cref="BookContextTokenBudget"/> &lt;= 0). The remaining headroom is reserved for
    /// the prompt instruction, system message, and the model's generated output. Default 0.5 (half the
    /// window for input context). Clamped to (0, 1].
    /// </summary>
    public double BookContextBudgetFraction { get; set; } = 0.5;

    /// <summary>
    /// Instruction + system-prompt overhead (tokens) added to the assembled book context AFTER budgeting —
    /// e.g. BookReviewService prepends the assembled text to the 6-dimension prompt template. Reserved by
    /// <see cref="EffectiveBookContextTokenBudget"/> so input (context + prompt) + output all fit the window.
    /// Sized for the largest consumer (the whole-book review prompt). Clamped to &gt;= 0.
    /// </summary>
    public int BookContextPromptReserveTokens { get; set; } = 1536;

    /// <summary>Safety margin (tokens) kept free after reserving input + output, to absorb token-estimate
    /// error. Clamped to &gt;= 0.</summary>
    public int BookContextSafetyMarginTokens { get; set; } = 512;

    /// <summary>Output reservation used when the consuming task's NumPredict is unknown (&lt;= 0).</summary>
    private const int DefaultOutputReserveTokens = 2048;

    /// <summary>
    /// Resolves the effective token budget for the whole-book context. When
    /// <see cref="BookContextTokenBudget"/> is positive it wins verbatim; otherwise it is derived to leave
    /// room for the model's OUTPUT (<paramref name="numPredict"/>) plus the prompt/system overhead
    /// (<see cref="BookContextPromptReserveTokens"/>) plus a safety margin
    /// (<see cref="BookContextSafetyMarginTokens"/>), so input + output can never exceed the window — Ollama
    /// silently TRUNCATES past num_ctx, which caused the whole-book review's "no dimension yielded findings"
    /// failure on a large book (the context filled the window and left no room for the findings JSON).
    /// <see cref="BookContextBudgetFraction"/> is kept as an additional UPPER bound. Floored at a small
    /// positive minimum so the BookBrief alone can always be attempted.
    /// </summary>
    /// <param name="numCtx">The active task model's context window (Ollama num_ctx) in tokens.</param>
    /// <param name="numPredict">The consuming task's output reservation (Ollama num_predict); &lt;= 0 uses a default.</param>
    public int EffectiveBookContextTokenBudget(int numCtx, int numPredict = 0)
    {
        if (BookContextTokenBudget > 0)
            return BookContextTokenBudget;

        var ctx = numCtx > 0 ? numCtx : 4096; // mirror ProviderTuningOptions.NumCtx default

        // Reserve OUTPUT + prompt overhead + margin so input (context + prompt) + output fits the window.
        // With the defaults (BookContextBudgetFraction=0.5, BookReview NumCtx=16384, NumPredict=6144,
        // and other book-context consumers also at 16384), byFraction=8192 is <= byReserve for every
        // current consumer, so byReserve is DEFENSE-IN-DEPTH that only activates when numPredict is large
        // enough that byReserve < byFraction (roughly numPredict > ctx*(1-fraction) - promptReserve -
        // safetyMargin, i.e. > ~6144 at these settings). The language-aware token estimate in
        // BookContextAssembler (Hebrew ~2 chars/token) is the load-bearing fix for the "no dimension
        // yielded findings" truncation; this reservation guards future configs.
        var output = numPredict > 0 ? numPredict : DefaultOutputReserveTokens;
        var byReserve = ctx - output - Math.Max(0, BookContextPromptReserveTokens) - Math.Max(0, BookContextSafetyMarginTokens);

        // Additional upper bound: the context never claims more than a configured share of the window.
        var fraction = BookContextBudgetFraction;
        if (fraction <= 0 || double.IsNaN(fraction)) fraction = 0.5;
        if (fraction > 1) fraction = 1;
        var byFraction = (int)Math.Floor(ctx * fraction);

        var derived = Math.Min(byFraction, byReserve);
        return Math.Max(256, derived); // never below a floor so the BookBrief can always be attempted
    }
}

public class OllamaProviderOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string DefaultModel { get; set; } = "qwen2.5:14b";
}

public class OpenAiProviderOptions
{
    public string Model { get; set; } = "gpt-4o";
    public string? ApiKey { get; set; }
}

public class AzureOpenAiProviderOptions
{
    public string Endpoint { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string? ApiKey { get; set; }
}

public class AnthropicProviderOptions
{
    public string Model { get; set; } = "claude-3-5-sonnet-20241022";
    public string? ApiKey { get; set; }
}

public class ProviderTuningOptions
{
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 2048;
    public int NumPredict { get; set; } = 2048; // Ollama
    /// <summary>Ollama repeat_penalty. 1.1 is Ollama's default (no-op); raise for tasks prone to
    /// repetition loops (e.g. structured linguistic output on smaller local models).</summary>
    public double RepeatPenalty { get; set; } = 1.1; // Ollama
    /// <summary>Ollama context window (num_ctx) in tokens. Ollama silently TRUNCATES any prompt
    /// longer than this, which yields broken/empty output (e.g. a lone "{"). 4096 is Ollama's
    /// usual default; raise for tasks that send a whole chapter/book unchunked (e.g.
    /// LinguisticAnalysis) so input + generated output both fit.</summary>
    public int NumCtx { get; set; } = 4096; // Ollama
}

/// <summary>Per-feature (task type) provider/model override.</summary>
public class FeatureModelOptions
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>
/// Repair-layer operating mode (dynamic-term-repair-design plan, d4). Selects WHICH repair stage(s) run at
/// <see cref="Analysis.UnifiedAnalysisService.ApplyAnalysisRepairAsync"/> and the BookReview engine hook
/// (<see cref="Analysis.BookReviewService"/>), layered UNDER the existing <see cref="AnalysisRepairOptions.Enabled"/>
/// / <see cref="AnalysisRepairOptions.PerType"/> gate — both of those still apply FIRST; <see cref="Mode"/> only
/// decides what runs once a type/book has cleared that gate:
///   • <see cref="Off"/> — an ADDITIONAL strict no-op on top of <see cref="AnalysisRepairOptions.Enabled"/>:
///     neither the deterministic glossary nor the dynamic detect-and-repair stage runs.
///   • <see cref="Glossary"/> — the SHIPPED DEFAULT. Only the closed, deterministic English&lt;-&gt;Hebrew
///     glossary (<see cref="Analysis.GlossaryRepairPass"/>) runs; the dynamic span-scoped stage
///     (<see cref="Analysis.DynamicTermRepairService"/>) never runs. Selecting this mode reproduces the EXACT
///     pre-d4 stage-1/stage-2 sequence — introducing this enum changes NOTHING about what ships today.
///   • <see cref="Dynamic"/> — the glossary Stage-1 substitution is SKIPPED entirely; the span-scoped
///     detect-classify-repair pass (d1-d3) runs instead, directly over the original (un-glossaried) prose.
///   • <see cref="GlossaryThenDynamic"/> — the glossary fast-path cache runs FIRST (cheap, deterministic,
///     catches the closed ~35-term vocabulary at zero model cost), THEN the dynamic pass runs over whatever
///     residual foreign text the glossary left — the two stages compose rather than compete.
/// </summary>
public enum AnalysisRepairMode
{
    Off,
    Glossary,
    Dynamic,
    GlossaryThenDynamic
}

/// <summary>
/// The SINGLE source of truth for "which repair stage(s) does this <see cref="AnalysisRepairMode"/> select?"
/// (be-c06). Every stage gate in the codebase MUST call these — do NOT re-write the predicate longhand.
///
/// WHY THIS EXISTS: the two predicates below were previously spelled out at FOUR independent call sites (the
/// glossary + dynamic stages in <see cref="Analysis.UnifiedAnalysisService.ApplyAnalysisRepairAsync"/> and the
/// glossary + dynamic hooks in <see cref="Analysis.BookReviewService"/>). Four copies of a predicate that MUST
/// agree is a replicated gate: add a fifth mode, or change one mode's semantics, and a copy silently diverges
/// while every site still looks locally correct. Centralising them means a mode change is a ONE-line edit here
/// and every seam moves together.
///
/// SEMANTICS (mirrors the <see cref="AnalysisRepairMode"/> xmldoc):
///   • <see cref="AnalysisRepairMode.Off"/> → runs NEITHER stage (both predicates false).
///   • <see cref="AnalysisRepairMode.Glossary"/> → glossary only.
///   • <see cref="AnalysisRepairMode.Dynamic"/> → dynamic only (the glossary substitution is skipped entirely).
///   • <see cref="AnalysisRepairMode.GlossaryThenDynamic"/> → BOTH, glossary first (the two compose).
///
/// NOTE: these predicates answer ONLY the stage-selection question. They are layered UNDER
/// <see cref="AnalysisRepairOptions.Enabled"/> / <see cref="AnalysisRepairOptions.PerType"/>, which every call
/// site must still check FIRST — a true here never overrides a disabled layer or an excluded analysis type.
/// </summary>
public static class AnalysisRepairModeExtensions
{
    /// <summary>
    /// True when <paramref name="mode"/> selects the DETERMINISTIC glossary stage
    /// (<see cref="Analysis.GlossaryRepairPass"/> on the RunAsync seam; <c>ApplyGlossaryToFindings</c> on the
    /// BookReview engine seam). True for <see cref="AnalysisRepairMode.Glossary"/> and
    /// <see cref="AnalysisRepairMode.GlossaryThenDynamic"/>; false for <see cref="AnalysisRepairMode.Off"/>
    /// (which runs neither stage) and <see cref="AnalysisRepairMode.Dynamic"/> (which skips the glossary).
    /// </summary>
    public static bool RunsGlossary(this AnalysisRepairMode mode) =>
        mode is AnalysisRepairMode.Glossary or AnalysisRepairMode.GlossaryThenDynamic;

    /// <summary>
    /// True when <paramref name="mode"/> selects the span-scoped DYNAMIC detect-and-repair stage
    /// (<see cref="Analysis.DynamicTermRepairService"/>). True for <see cref="AnalysisRepairMode.Dynamic"/> and
    /// <see cref="AnalysisRepairMode.GlossaryThenDynamic"/>; false for <see cref="AnalysisRepairMode.Off"/>
    /// (which runs neither stage) and <see cref="AnalysisRepairMode.Glossary"/> (the rollback/kill-switch that
    /// reproduces the pre-d4 glossary-only sequence).
    /// </summary>
    public static bool RunsDynamic(this AnalysisRepairMode mode) =>
        mode is AnalysisRepairMode.Dynamic or AnalysisRepairMode.GlossaryThenDynamic;
}

/// <summary>
/// Config for the analysis-output repair layer (analysis-output-repair plan, p6-config; extended by the
/// dynamic-term-repair-design plan, d4), read by
/// <see cref="Analysis.UnifiedAnalysisService.ApplyAnalysisRepairAsync"/>. There are now THREE repair
/// stages — a DETERMINISTIC glossary substitution (<see cref="Analysis.GlossaryRepairPass"/>), a
/// value-scoped LLM repair (<see cref="Analysis.AnalysisRepairService"/>), and a span-scoped dynamic
/// detect-and-repair pass (<see cref="Analysis.DynamicTermRepairService"/>) — and this block governs them
/// via four knobs:
///
///   • <see cref="Enabled"/> = false (or a null block) → FULL no-op: NO stage runs; inputs returned
///     byte-identical. This is the strict off switch.
///   • <see cref="Enabled"/> = true, <see cref="GuardOnly"/> = true → deterministic glossary ONLY (no
///     LLM, no model calls). This is the SHIPPED DEFAULT (appsettings "Ai:AnalysisRepair") per the
///     p3-gate GUARD-ONLY decision: the glossary is deterministic + fail-safe + validated, while the LLM
///     pass showed an over-rewrite tendency on mixed leak+prose fields, so it stays opt-in.
///   • <see cref="Enabled"/> = true, <see cref="GuardOnly"/> = false → glossary + value-scoped LLM repair
///     (guard-gated + fail-safe inside the service; a clean field still makes ZERO model calls).
///   • <see cref="PerType"/> → gates repair per analysis-type name (see below).
///   • <see cref="Mode"/> → (d4) selects WHICH of the glossary / dynamic stages run, layered under the
///     three knobs above; see <see cref="AnalysisRepairMode"/>. The shipped default
///     (<see cref="AnalysisRepairMode.Glossary"/>) reproduces the pre-d4 behaviour exactly.
///
/// The class-level defaults are the SAFE posture (Enabled=false = off; if turned on, GuardOnly=true so
/// the LLM stays off unless explicitly opted into). Production reads the explicit appsettings block, so
/// these defaults only apply to programmatic/test construction. KEEP IN SYNC with the appsettings
/// "Ai:AnalysisRepair" block.
///
/// PER-ENVIRONMENT POLICY: this block is one value per ASP.NET Core environment (base appsettings.json +
/// an optional appsettings.{Environment}.json override, e.g. appsettings.Production.json). Each override
/// encodes the repair POLICY for that environment's model tier — a cloud-capable tier that does not leak
/// can go no-op (Enabled=false, or GuardOnly=true with an empty PerType), while a small-local tier stays
/// guard-only glossary-on. See appsettings.Production.json for the current prod value and its KEEP-IN-SYNC
/// note against "Ai:FeatureModels" (the model actually served).
///
/// EXTENSION POINT (documented here only — NOT implemented; do not add this until it is actually needed):
/// a per-provider/per-model override ON THIS CLASS (e.g. a Dictionary&lt;string, AnalysisRepairOptions&gt;
/// keyed by provider/model name, consulted instead of the single flat Enabled/GuardOnly/PerType above) is
/// only needed if a SINGLE environment ever serves MULTIPLE model tiers at once (e.g. some requests routed
/// to a cloud model, others to local Ollama, within the same deployment). Today each environment serves
/// exactly one tier, so the flat per-environment block above is sufficient.
/// </summary>
public class AnalysisRepairOptions
{
    /// <summary>Master switch for the whole repair layer. false (the class default) = FULL no-op: neither
    /// the deterministic glossary nor the LLM pass runs. The shipped appsettings value is true.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>When true (the class default and the SHIPPED default), only the deterministic glossary
    /// pass runs — the value-scoped LLM repair is skipped entirely (no model calls). Set false to also run
    /// the LLM repair. Only consulted when <see cref="Enabled"/> is true.</summary>
    public bool GuardOnly { get; set; } = true;

    /// <summary>The model the value-scoped LLM repair routes to. KEEP IN SYNC with
    /// "Ai:FeatureModels:AnalysisRepair" (and "Ai:ProviderSettings:Ollama_AnalysisRepair") — those keys do
    /// the actual routing; this field documents/asserts the intended model at the config surface.</summary>
    public string Model { get; set; } = "gemma4:12b";

    /// <summary>
    /// Which repair stage(s) run (dynamic-term-repair-design plan, d4); see <see cref="AnalysisRepairMode"/>
    /// for the four-way semantics. The class default AND the SHIPPED appsettings default are BOTH
    /// <see cref="AnalysisRepairMode.Glossary"/> — deterministic glossary fast-path ONLY, dynamic span-scoped
    /// repair OFF — so introducing this knob changes NOTHING about today's behaviour. Only consulted once
    /// <see cref="Enabled"/> is true and <see cref="PerType"/> allows the type; <see cref="AnalysisRepairMode.Off"/>
    /// is an ADDITIONAL off-switch scoped to stage selection (on top of <see cref="Enabled"/>=false). KEEP IN
    /// SYNC with the appsettings "Ai:AnalysisRepair.Mode" value (both appsettings.json and
    /// appsettings.Production.json).</summary>
    public AnalysisRepairMode Mode { get; set; } = AnalysisRepairMode.Glossary;

    /// <summary>Per-analysis-type gate, keyed by the <see cref="Contracts.AnalysisType"/> name
    /// ("Summarization", "LiteraryAnalysis", "LinguisticAnalysis", "LineEdit", plus the f5-wire
    /// book-level structured-Hebrew-prose types "BookOverview", "CharacterAnalysis", "StoryAnalysis",
    /// "QA" and the whole-book "BookReview" engine hook — the repairable types).
    /// A type mapped to false, OR absent when the map is non-empty, is SKIPPED. For the RunAsync/streaming
    /// seam this skips both stages; the "BookReview" key gates the deterministic glossary hook in
    /// BookReviewService (engine path; glossary-only, no LLM). A null/empty map means NO per-type
    /// restriction (every repairable type is allowed). Proofread is never repaired regardless of this map.</summary>
    public Dictionary<string, bool>? PerType { get; set; }
}
