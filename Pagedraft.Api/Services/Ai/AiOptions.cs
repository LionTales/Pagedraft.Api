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
    /// Resolves the effective token budget for the whole-book context. When
    /// <see cref="BookContextTokenBudget"/> is positive it wins verbatim; otherwise it is derived as
    /// <c>numCtx * <see cref="BookContextBudgetFraction"/></c> (fraction clamped to (0,1], result floored at
    /// a small positive minimum so the BookBrief alone can always be attempted).
    /// </summary>
    /// <param name="numCtx">The active task model's context window (Ollama num_ctx) in tokens.</param>
    public int EffectiveBookContextTokenBudget(int numCtx)
    {
        if (BookContextTokenBudget > 0)
            return BookContextTokenBudget;

        var fraction = BookContextBudgetFraction;
        if (fraction <= 0 || double.IsNaN(fraction)) fraction = 0.5;
        if (fraction > 1) fraction = 1;

        var ctx = numCtx > 0 ? numCtx : 4096; // mirror ProviderTuningOptions.NumCtx default
        var derived = (int)Math.Floor(ctx * fraction);
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
