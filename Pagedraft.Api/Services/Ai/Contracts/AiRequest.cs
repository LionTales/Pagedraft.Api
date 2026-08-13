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

    /// <summary>
    /// The system message to send INSTEAD of the one <c>PromptFactory</c> derives from
    /// <see cref="TaskType"/> alone. Null - every call site but one - keeps the factory's message
    /// byte-for-byte, so this property is inert for every task that does not set it.
    ///
    /// <para>WHY THE SEAM IS A STRING AND NOT A FLAG. <c>PromptFactory</c> sees only the task type, and
    /// phase B's ProductChat prompt turns on something a task type cannot express: whether THIS turn
    /// carries a book. With no way to say so, the factory returned the BOOK-LESS system message on every
    /// turn, so a book-scoped request shipped phase A's "answering questions about a specific book is not
    /// available yet" in the SYSTEM slot while phase B's book-grounding rule sat in the user message. Two
    /// contradictory rules in one prompt are resolved by the model rather than by the author, which is the
    /// exact lesson <c>ProductChatPrompt</c>'s own g3 paragraph records, reintroduced one layer up. See
    /// '## g1 book grounding results' F-1.</para>
    ///
    /// <para>The alternative was to widen <c>PromptFactory.GetPrompt</c> with a ProductChat-only
    /// parameter every other task would ignore. That was REJECTED: it makes a uniform factory
    /// non-uniform, and it does not remove the second owner, it entrenches it. This shape is instead the
    /// symmetric completion of an asymmetry the contract already had - <see cref="Instruction"/> is
    /// ALREADY supplied whole by the caller for exactly these tasks (see
    /// <c>AiRouter.ShouldUseUnifiedInstructionVerbatim</c>, which sends ProductChat's instruction
    /// verbatim), so a caller that owns the whole user message could not own the system message beside
    /// it. Now it can, and both halves of one prompt have ONE author.</para>
    ///
    /// <para>THE CONTRACT ON A CALLER THAT SETS IT: the value must come from the SAME type that owns that
    /// task's prompt wording (for ProductChat, <c>ProductChatPrompt.SystemMessage</c>, which is also what
    /// the factory arm calls), never from a string composed at the call site. That is what keeps "the
    /// wording has one home" true while the DECISION of which wording moves to the caller that has the
    /// facts. <c>ProductChatComposedSystemSlotTests</c> pins both directions: the composed slot literally,
    /// and that a null override is a no-op for every other task type.</para>
    /// </summary>
    public string? SystemMessageOverride { get; init; }
}
