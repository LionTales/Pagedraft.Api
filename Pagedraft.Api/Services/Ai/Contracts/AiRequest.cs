namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Provider-agnostic AI request (input to the router).</summary>
public record AiRequest
{
    public required string InputText { get; init; }
    public string? Instruction { get; init; }
    public required AiTaskType TaskType { get; init; }
    public string Language { get; init; } = "he-IL";
    public string? UserId { get; init; }
    public string? SourceId { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    /// <summary>When true, providers that support it will enforce structured JSON output (e.g. Ollama format:"json").</summary>
    public bool JsonMode { get; init; }

    /// <summary>
    /// The BOOK's model tier (model-tier-fast-thinking plan, p3-2), stamped by the CALLER that knows which
    /// book this request belongs to. Null means <see cref="AiTier.Fast"/>, i.e. resolution identical to the
    /// pre-tier behaviour - so an unstamped call site can never accidentally route to paid cloud.
    ///
    /// It is a stamped VALUE rather than something the router derives, because <see cref="SourceId"/> is NOT
    /// a book id: across its assignment sites it is a chapterId, a sceneId, a bookId, a suggestion id, or
    /// the literals "repair" / "term-repair". Deriving a book from it is impossible; keeping the tier on the
    /// request also keeps <see cref="AiRouter"/> free of any database dependency.
    ///
    /// Only the tasks in <see cref="AiTierPolicy.TieredTasks"/> read it - for every other task the value is
    /// inert, which is why stamping a call site that may run several task types is safe.
    /// </summary>
    public AiTier? Tier { get; init; }
}
