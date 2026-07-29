using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Which <see cref="ProviderTuningOptions"/> field a provider family sends as its OUTPUT cap. The two fields
/// are semantically the same thing under two names, one per family, and only ever one of them is set on any
/// given appsettings entry — see <see cref="ProviderTuningResolver.KnownOutputKnobs"/> for why that matters.
/// </summary>
public enum ProviderOutputTokenKnob
{
    /// <summary>Ollama's <c>options.num_predict</c> (<see cref="ProviderTuningOptions.NumPredict"/>).</summary>
    NumPredict,

    /// <summary>The OpenAI/Azure/Anthropic/OpenAI-compatible <c>max_tokens</c> (or, for the GPT-5 family,
    /// <c>max_completion_tokens</c> — a different WIRE NAME for the same
    /// <see cref="ProviderTuningOptions.MaxTokens"/> value).</summary>
    MaxTokens
}

/// <summary>
/// THE single source of truth for the <c>Ai:ProviderSettings</c> key precedence:
/// <c>"{Provider}_{TaskType}"</c> → <c>"{Provider}"</c> → the <see cref="ProviderTuningOptions"/> class
/// default. Every provider and every budget-sizer that needs a task's tuning MUST resolve through here.
///
/// WHY THIS EXISTS (model-tier-fast-thinking plan, p1-1). The precedence was written out longhand in
/// THREE places — <see cref="OllamaProvider"/>'s private <c>GetTuning</c>,
/// <see cref="Analysis.BookContextAssembler.ResolveNumCtxForTask"/> and its NumPredict sibling — plus FOUR
/// more copies in the cloud providers (<see cref="OpenAiCompatibleProvider"/>,
/// <see cref="OpenAiProvider"/>, <see cref="AzureOpenAiProvider"/>, <see cref="AnthropicProvider"/>) that
/// had silently DROPPED the per-task rung and looked up the FLAT provider key only. That asymmetry is not
/// cosmetic: it meant no cloud task could raise its own limits, so a cloud-routed BookReview ran against
/// the flat <c>OpenRouter</c> entry while the local path reserved the far larger <c>Ollama_BookReview</c>
/// window. Seven hand-written copies of one precedence is a replicated lookup: the moment one copy is
/// extended (per-task, per-language, per-tier …) the others silently disagree while each still looks
/// locally correct.
///
/// TWO RUNGS, TWO SEMANTICS, plus ONE provider-aware accessor — pick the right one:
///   • <see cref="Resolve"/> is OBJECT-level. A matching key WINS OUTRIGHT and the whole bound entry is
///     returned, fields and all. This is what a provider needs: it sends every field of one entry.
///   • <see cref="ResolvePositiveInt"/> is FIELD-level. A rung whose SELECTED field is &lt;= 0 (i.e. the
///     entry exists but does not set it, so it bound the class default) FALLS THROUGH to the next rung.
///     This is what the budget sizers need: <c>Ollama_Proofread</c> sets NumPredict but not NumCtx, and
///     must inherit NumCtx from the flat <c>Ollama</c> entry rather than collapsing to 4096.
///   • <see cref="ResolveOutputTokens"/> is the FIELD-level rung applied to the OUTPUT cap, choosing NumPredict
///     vs MaxTokens by provider family (p1-2). Any budget sizer reserving output headroom must use THIS rather
///     than naming a field itself, or it reserves one family's knob for the other family's request.
/// The first two are NOT interchangeable and the difference is load-bearing; see the trap note below.
///
/// THE SILENT-DEFAULT TRAP. Under <see cref="Resolve"/> a provider entry that exists but omits a field
/// binds that field's CLASS default and therefore WINS the lookup rather than falling through — e.g. the
/// flat <c>OpenRouter</c> entry sets only Temperature/MaxTokens, so it supplies NumCtx=4096. Nothing logs
/// a warning. That is why per-task cloud entries have to exist rather than being assumed to inherit
/// (p1-3), and why <see cref="ResolvePositiveInt"/> exists at all.
///
/// FALLBACK VALUE. All seven pre-existing copies fell back to an options object that is field-for-field
/// identical to <c>new ProviderTuningOptions()</c> (Temperature 0.2, MaxTokens 2048, NumPredict 2048,
/// RepeatPenalty 1.1, NumCtx 4096) — the Ollama copy spelled it <c>{ Temperature = 0.2, NumPredict = 2048 }</c>
/// and the cloud copies <c>{ Temperature = 0.2, MaxTokens = 2048 }</c>, both of which merely restate
/// defaults. Using the plain class default here is therefore VALUE-PRESERVING, not a behaviour change;
/// ProviderTuningResolverTests pins that equivalence so a future default edit cannot quietly move it.
/// </summary>
public static class ProviderTuningResolver
{
    /// <summary>The tuning-key precedence, spelled once: task-specific key, then flat provider key.</summary>
    public static string TaskKey(string providerName, AiTaskType taskType) => providerName + "_" + taskType;

    /// <summary>
    /// WHICH tuning field each provider family actually sends as its output cap (p1-2). Keyed by the provider
    /// NAME as it appears in <c>Ai:ProviderSettings</c> / <c>Ai:FeatureModels:*:Provider</c>, and case-insensitive
    /// to match the provider registry (<see cref="AiProviderRegistry"/> builds its map with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>).
    ///
    /// This exists because <see cref="ProviderTuningOptions"/> carries TWO independent properties that mean the
    /// same thing — <c>NumPredict</c> (Ollama's <c>num_predict</c>) and <c>MaxTokens</c> (the cloud families'
    /// <c>max_tokens</c> / <c>max_completion_tokens</c>) — and each appsettings entry only sets the one its own
    /// family reads. A reader that hard-codes ONE of them therefore reads a CLASS DEFAULT for the other family:
    /// the shipped <c>OpenRouter</c> entry sets <c>MaxTokens 5120</c> and no NumPredict, so the pre-p1-2 budget
    /// sizer reserved the 2048 NumPredict default while the provider went on to request 5120 — a silent 3072-token
    /// under-reservation of output headroom on every cloud call.
    ///
    /// KEEP IN SYNC with <see cref="AiProviderRegistry"/>: every registered provider name must appear here.
    /// <c>ProviderTuningOutputKnobTests.EveryRegisteredProvider_HasAnExplicitOutputKnobClassification</c> fails if
    /// one does not, and the wire test drives the REAL provider classes to prove each classification matches the
    /// field that provider actually puts on the wire.
    /// </summary>
    private static readonly Dictionary<string, ProviderOutputTokenKnob> OutputKnobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ollama"] = ProviderOutputTokenKnob.NumPredict,
        ["OpenAI"] = ProviderOutputTokenKnob.MaxTokens,
        ["Azure"] = ProviderOutputTokenKnob.MaxTokens,
        ["Anthropic"] = ProviderOutputTokenKnob.MaxTokens,
        ["OpenRouter"] = ProviderOutputTokenKnob.MaxTokens
    };

    /// <summary>The classification table above, for tests and diagnostics.</summary>
    public static IReadOnlyDictionary<string, ProviderOutputTokenKnob> KnownOutputKnobs => OutputKnobs;

    /// <summary>
    /// The output knob a provider family sends, or <c>null</c> when the name is not classified (an unregistered
    /// or newly added provider). Callers must treat null as "unknown", not as a default —
    /// <see cref="ResolveOutputTokens"/> handles it conservatively.
    /// </summary>
    public static ProviderOutputTokenKnob? OutputKnobFor(string? providerName)
        => providerName != null && OutputKnobs.TryGetValue(providerName, out var knob) ? knob : null;

    /// <summary>
    /// PROVIDER-AWARE OUTPUT RESERVATION (p1-2). Returns the number of output tokens the given provider will
    /// actually request for this task — <c>NumPredict</c> for the Ollama family, <c>MaxTokens</c> for the cloud
    /// families — resolved along the same FIELD-level rungs as <see cref="ResolvePositiveInt"/>. This is the
    /// number a budget sizer must reserve, and it must EQUAL what the provider puts on the wire; anything less
    /// lets the input claim room the model then needs for its answer, and the output truncates.
    ///
    /// UNKNOWN PROVIDER: falls back to the LARGER of the two knobs. Over-reserving only shrinks the input budget
    /// (safe); under-reserving truncates generated output (the failure this whole phase exists to prevent), so the
    /// conservative direction is deliberate. It is a safety net, not a substitute for classifying the provider —
    /// the completeness test above makes a missing classification loud.
    /// </summary>
    public static int ResolveOutputTokens(
        IReadOnlyDictionary<string, ProviderTuningOptions>? settings,
        string providerName,
        AiTaskType taskType)
        => OutputKnobFor(providerName) switch
        {
            ProviderOutputTokenKnob.NumPredict => ResolvePositiveInt(settings, providerName, taskType, t => t.NumPredict),
            ProviderOutputTokenKnob.MaxTokens => ResolvePositiveInt(settings, providerName, taskType, t => t.MaxTokens),
            _ => Math.Max(
                ResolvePositiveInt(settings, providerName, taskType, t => t.NumPredict),
                ResolvePositiveInt(settings, providerName, taskType, t => t.MaxTokens))
        };

    /// <summary>
    /// OBJECT-level resolution: returns the WHOLE tuning entry bound at the first matching rung
    /// (<c>"{provider}_{task}"</c> → <c>"{provider}"</c>), else the <see cref="ProviderTuningOptions"/>
    /// class default. A matching entry wins outright — its unset fields keep their class defaults, they do
    /// NOT fall through to the next rung (see the silent-default trap on the class doc).
    /// </summary>
    /// <param name="settings">The bound <c>Ai:ProviderSettings</c> map, or null when the section is absent.</param>
    /// <param name="providerName">The provider name as it appears in the config key (e.g. "Ollama", "OpenRouter").</param>
    /// <param name="taskType">The task the request is for. Defaults to
    /// <see cref="AiTaskType.GenericChat"/> to match the pre-refactor OllamaProvider signature.</param>
    public static ProviderTuningOptions Resolve(
        IReadOnlyDictionary<string, ProviderTuningOptions>? settings,
        string providerName,
        AiTaskType taskType = AiTaskType.GenericChat)
    {
        if (settings != null)
        {
            if (settings.TryGetValue(TaskKey(providerName, taskType), out var taskTuning))
                return taskTuning;
            if (settings.TryGetValue(providerName, out var providerTuning))
                return providerTuning;
        }
        return new ProviderTuningOptions();
    }

    /// <summary>
    /// FIELD-level resolution: returns the first POSITIVE value of <paramref name="selector"/> along the
    /// same rungs (<c>"{provider}_{task}"</c> → <c>"{provider}"</c>), else the field's
    /// <see cref="ProviderTuningOptions"/> class default. Unlike <see cref="Resolve"/>, a rung whose
    /// selected field is &lt;= 0 does NOT win — an entry that simply omits the field inherits the next
    /// rung's value instead of pinning the class default.
    /// </summary>
    public static int ResolvePositiveInt(
        IReadOnlyDictionary<string, ProviderTuningOptions>? settings,
        string providerName,
        AiTaskType taskType,
        Func<ProviderTuningOptions, int> selector)
    {
        if (settings != null)
        {
            if (settings.TryGetValue(TaskKey(providerName, taskType), out var taskTuning) && selector(taskTuning) > 0)
                return selector(taskTuning);
            if (settings.TryGetValue(providerName, out var providerTuning) && selector(providerTuning) > 0)
                return selector(providerTuning);
        }
        return selector(new ProviderTuningOptions());
    }
}
