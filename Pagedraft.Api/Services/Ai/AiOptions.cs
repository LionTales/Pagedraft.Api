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
}

/// <summary>Per-feature (task type) provider/model override.</summary>
public class FeatureModelOptions
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
}
