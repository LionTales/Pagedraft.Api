using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Identifies a chapter (or summary unit) that did NOT fit the whole-book token budget. Surfaced by
/// <see cref="BookContextAssembly.DroppedUnits"/> and logged, so a truncation is never silent
/// ("No silent truncation" workspace rule).
/// </summary>
public sealed record DroppedBookUnit
{
    public int Order { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>Estimated token cost of the unit that was skipped (for diagnostics).</summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>
/// Result of assembling the whole-book analysis context under a token budget. Carries the assembled text
/// (ready to feed the book-level analysis prompts as InputText), plus structured provenance: which units
/// were included, which were dropped (and why budget), and whether the dense structured-brief path or the
/// degraded flat-summary fallback was used.
/// </summary>
public sealed record BookContextAssembly
{
    /// <summary>The assembled context text, within <see cref="BudgetTokens"/>.</summary>
    public required string Text { get; init; }

    /// <summary>The L2 BookBrief that was placed FIRST (always included when one exists). Null only when
    /// no rollup could be composed (e.g. no chapters), in which case the fallback path supplies Text.</summary>
    public BookBrief? BookBrief { get; init; }

    /// <summary>The L1 ChapterBriefs that fit the budget, in inclusion (narrative) order.</summary>
    public IReadOnlyList<ChapterBrief> IncludedChapterBriefs { get; init; } = Array.Empty<ChapterBrief>();

    /// <summary>Units (chapters) that did NOT fit the budget. Empty when everything fit.</summary>
    public IReadOnlyList<DroppedBookUnit> DroppedUnits { get; init; } = Array.Empty<DroppedBookUnit>();

    /// <summary>True when the dense structured ChapterBrief path was used; false when it degraded to the
    /// flat-summary fallback (no usable structured briefs yet).</summary>
    public bool UsedStructuredBriefs { get; init; }

    /// <summary>The token budget the assembly was held within.</summary>
    public int BudgetTokens { get; init; }

    /// <summary>The estimated token size of <see cref="Text"/> (always &lt;= <see cref="BudgetTokens"/>
    /// once at least the BookBrief alone fits; may exceed only in the pathological case where even the
    /// BookBrief alone is larger than the budget, which is logged).</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Convenience: how many units were dropped.</summary>
    public int DroppedCount => DroppedUnits.Count;

    // ─── Windowed-path metadata (AssembleWindowsAsync, wb4-c01) ────────────────────────────────────────
    // These are populated ONLY by the windowed whole-book review path so wb4-c02 can label progress and tag
    // findings by window. The single AssembleAsync path leaves them at their defaults (null / empty), so a
    // single assembly is indistinguishable from its prior shape — the window fields simply do not apply.

    /// <summary>Zero-based index of this window within the ordered window list, or null for a single
    /// (non-windowed) assembly produced by <see cref="BookContextAssembler.AssembleAsync"/>.</summary>
    public int? WindowIndex { get; init; }

    /// <summary>The chapter Orders included in this window, in narrative order — INCLUDING any leading
    /// overlap chapters repeated from the previous window (see
    /// <see cref="BookContextAssembler.AssembleWindowsAsync"/>). Empty for a single (non-windowed) assembly.</summary>
    public IReadOnlyList<int> IncludedChapterOrders { get; init; } = Array.Empty<int>();

    /// <summary>The subset of <see cref="IncludedChapterOrders"/> that are OVERLAP chapters repeated from the
    /// previous window (not this window's primary chapters). Empty for the first window and for single
    /// assemblies. Lets wb4-c02 attribute a finding to a chapter's PRIMARY window when it also appears as
    /// overlap elsewhere.</summary>
    public IReadOnlyList<int> OverlapChapterOrders { get; init; } = Array.Empty<int>();

    /// <summary>True when this window's assembled Text alone exceeds <see cref="BudgetTokens"/> because a
    /// SINGLE chapter's block did not fit any window (rule c: it becomes its own over-budget window rather
    /// than being dropped). Logged when set. False for every in-budget window and for single assemblies.</summary>
    public bool WindowExceedsBudget { get; init; }

    /// <summary>
    /// SINGLE source of truth for "does this assembly carry usable structured briefs?" — the dense path the
    /// whole-book review reads. True only when the structured-brief path was taken AND there is at least one
    /// usable brief (a BookBrief OR at least one included chapter brief). Defined once and called from both:
    ///   • <see cref="BookReviewService"/>'s briefs-absent build gate (as the negation), and
    ///   • <see cref="BookReviewService"/>'s status probe (HasUsableBriefsAsync),
    /// so the build gate and the surfaced status cannot drift into opposite truth values.
    /// </summary>
    public static bool HasUsableBriefs(BookContextAssembly assembly) =>
        assembly.UsedStructuredBriefs
        && (assembly.BookBrief != null || assembly.IncludedChapterBriefs.Count > 0);
}

/// <summary>
/// The SINGLE budgeted assembler for whole-book analysis context. Both the book-scope path in
/// <see cref="AnalysisContextService"/> (ResolveBookAsync) and the book-level analyses in
/// <see cref="BookIntelligenceService"/> (BookOverview / Synopsis / CharacterAnalysis / StoryAnalysis / QA)
/// route through here, so there is ONE budgeted path rather than two divergent concats.
///
/// WHY: the previous book paths concatenated EVERY chapter's full text (ResolveBookAsync) or EVERY flat
/// chapter summary (BookIntelligenceService.GetConcatenatedSummaries) with NO size guard. A large book
/// overflows the model context window; Ollama silently TRUNCATES anything past num_ctx, yielding broken or
/// empty output. This assembler caps assembly at a token budget derived from the active model's NumCtx.
///
/// COMPOSITION (preferred path):
///   1. ALWAYS include the L2 <see cref="BookBrief"/> first (genre/themes/synopsis rollup) — the cheapest,
///      densest, most globally-relevant block.
///   2. Then add the most-relevant L1 <see cref="ChapterBrief"/>s until the budget is hit. "Most relevant"
///      = NARRATIVE ORDER (chapter Order ascending): a book analysis reads the story front-to-back, so
///      preserving order keeps the earliest (setup/premise) chapters when later ones must be dropped, and
///      the included prefix is always a contiguous, coherent run rather than a scattered sample. Structured
///      briefs are far DENSER than flat summaries (a few plot events + states vs. prose), which is the lever
///      that lets a whole book fit a fixed budget.
///   3. Any chapter brief that does not fit is DROPPED, recorded in <see cref="BookContextAssembly.DroppedUnits"/>
///      AND logged with the count — no silent truncation.
///
/// GRACEFUL DEGRADATION (fallback path): when NO usable structured briefs exist yet (the book summary has
/// not been built), fall back to the existing flat per-chapter summary concat — but STILL apply the same
/// budget guard so the fallback cannot overflow either. When even flat summaries are absent, fall back to a
/// budget-trimmed concat of the raw chapter text (so book analysis still has something to chew on).
/// </summary>
public class BookContextAssembler
{
    // Chars-per-token estimate, LANGUAGE-AWARE. Token density differs sharply by script: Latin prose is
    // ~4 chars/token, but Hebrew/Arabic are FAR denser (~2 chars/token) because those scripts fragment into
    // many more sub-word tokens. Using the Latin 4.0 for Hebrew UNDER-counts tokens ~2x, so the assembler
    // packed ~2x too much and the whole-book review overflowed num_ctx (the model then truncated its output
    // to nothing -> "no dimension yielded findings"). We pick the estimate by the assembly's language so the
    // budget cap corresponds to REAL tokens. Centralized so the estimate is identical everywhere.
    private const double CharsPerTokenLatin = 4.0;
    private const double CharsPerTokenDense = 2.0; // Hebrew / Arabic and other token-dense scripts

    // Separator written between the BookBrief and every ChapterBrief in the assembled Text. It is part of the
    // emitted string, so it MUST be charged against the budget: fold it into each block before estimating
    // (exactly as the flat-fallback path does with its "{header}\n{body}\n\n" block) rather than appending it
    // uncounted, or the separators inflate Text beyond what the running total accounts for.
    private const string BlockSeparator = "\n\n";

    private readonly AppDbContext _db;
    private readonly BookSummaryService _bookSummary;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<BookContextAssembler> _logger;

    public BookContextAssembler(
        AppDbContext db,
        BookSummaryService bookSummary,
        IOptions<AiOptions> aiOptions,
        ILogger<BookContextAssembler> logger)
    {
        _db = db;
        _bookSummary = bookSummary;
        _aiOptions = aiOptions;
        _logger = logger;
    }

    /// <summary>Chars-per-token estimate for a language (normalized or raw).
    ///
    /// Dense scripts (he / iw / ar) → <see cref="CharsPerTokenDense"/> (2.0).
    /// Recognised Latin-script languages → <see cref="CharsPerTokenLatin"/> (4.0).
    ///
    /// EVERYTHING ELSE — null, empty, whitespace, or any code not in either set — falls back to
    /// <see cref="CharsPerTokenDense"/> (2.0), the CONSERVATIVE direction. Under-counting tokens
    /// over-fills the context window and silently truncates model output ("no dimension yielded
    /// findings"); over-counting drops a few extra chapters at the tail, which is always safe.
    /// A Hebrew book whose Language field is unset or non-standard must NOT silently revert to the
    /// Latin (lenient) estimate and recreate the whole-book review truncation this fix addresses.
    ///
    /// Latin allowlist (StartsWith match on the normalised code, so "en-US" → "en"):
    /// en, fr, es, de, it, pt, nl, ca, ro, sv, da, no, fi</summary>
    public static double CharsPerTokenForLanguage(string? language)
    {
        var lang = (language ?? string.Empty).Trim().ToLowerInvariant();

        // Dense scripts: Hebrew (he / iw legacy code) and Arabic.
        if (lang.StartsWith("he") || lang.StartsWith("iw") || lang.StartsWith("ar"))
            return CharsPerTokenDense;

        // Recognised Latin-script languages. Extend this allowlist when a new script is explicitly
        // validated — never add an unknown code here (unknown → conservative dense default below).
        if (lang.StartsWith("en") || lang.StartsWith("fr") || lang.StartsWith("es") ||
            lang.StartsWith("de") || lang.StartsWith("it") || lang.StartsWith("pt") ||
            lang.StartsWith("nl") || lang.StartsWith("ca") || lang.StartsWith("ro") ||
            lang.StartsWith("sv") || lang.StartsWith("da") || lang.StartsWith("no") ||
            lang.StartsWith("fi"))
            return CharsPerTokenLatin;

        // Unknown, empty, or whitespace-only: default to DENSE (conservative).
        // Under-counting is the dangerous direction (overflows num_ctx); over-counting is safe.
        return CharsPerTokenDense;
    }

    /// <summary>Estimated token cost of a text blob using the Latin heuristic (~4 chars/token). For
    /// language-aware budgeting use the <see cref="EstimateTokens(string?, double)"/> overload with
    /// <see cref="CharsPerTokenForLanguage"/>.</summary>
    public static int EstimateTokens(string? text) => EstimateTokens(text, CharsPerTokenLatin);

    /// <summary>Estimated token cost of a text blob at a given chars-per-token density.</summary>
    public static int EstimateTokens(string? text, double charsPerToken) =>
        string.IsNullOrEmpty(text)
            ? 0
            : (int)Math.Ceiling(text.Length / (charsPerToken > 0 ? charsPerToken : CharsPerTokenLatin));

    /// <summary>
    /// Resolves the effective token budget for the whole-book context. Honours an explicit
    /// <see cref="AiOptions.BookContextTokenBudget"/>, otherwise derives it from the NumCtx of the task(s)
    /// that will actually CONSUME the assembled text.
    ///
    /// WHY the consuming task and not always Summarization: the assembled text is fed to BookOverview /
    /// CharacterAnalysis / StoryAnalysis (<see cref="AiTaskType.LinguisticAnalysis"/>), Synopsis
    /// (<see cref="AiTaskType.Summarization"/>) and QA (<see cref="AiTaskType.GenericChat"/>), each of which
    /// can have a SMALLER per-task num_ctx than Summarization. Budgeting against Summarization alone would
    /// let the context overflow the consumer's window, where Ollama silently truncates. When several tasks
    /// share one assembly (BookIntelligenceService reuses the same text across routes) we budget to the
    /// SMALLEST window so the context fits the tightest consumer. With no task supplied we fall back to
    /// Summarization (the task the briefs were built under). The NumCtx lookup goes through
    /// <see cref="ProviderTuningResolver"/> — the SHARED implementation of the provider+task key → provider
    /// key → ProviderTuningOptions-default precedence that every provider also uses at request time.
    /// </summary>
    /// <param name="tier">
    /// The BOOK's model tier (p3-2). Load-bearing rather than cosmetic: the tuning key is
    /// <c>{Provider}_{TaskType}</c> and the PROVIDER comes from
    /// <see cref="LinguisticModelResolver.ResolveForTask(AiOptions, AiTaskType, string?, AiTier)"/>,
    /// so a tier that moves a task to another provider moves that task's window and output reservation with
    /// it. Defaults to <see cref="AiTier.Fast"/>, which resolves exactly as before the tier existed.
    ///
    /// EVERY caller takes that default today - <see cref="AssembleAsync"/>, <see cref="AssembleWindowsAsync"/>,
    /// both <c>BookReviewDigests</c> budget derivations and <c>BookReviewService.PlanContinuityReduce</c> - so
    /// the BUDGET is sized on the Fast route regardless of the book's tier. That is a DELIBERATE, pinned no-op
    /// (parent plan p3-3 correction 4 / p3-4 correction 6, pinned by
    /// <c>TheWholeBookBudget_IsUnmovedByTheTier_AtTheShippedValues</c>): threading the tier here would reach
    /// BookIntelligenceService and the BookReview windowed path, both outside the tier's GO'd scope. The
    /// OBSERVABILITY half does NOT share that limitation - see
    /// <see cref="WarnIfWholeBookWindowIsUnsized"/>, which evaluates every tier rather than this one.
    /// </param>
    public int ResolveBudgetTokens(
        IReadOnlyCollection<AiTaskType>? consumingTasks = null,
        AiTier tier = AiTier.Fast)
    {
        var opt = _aiOptions.Value;
        var tasks = consumingTasks is { Count: > 0 }
            ? consumingTasks
            : new[] { AiTaskType.Summarization };
        var numCtx = tasks.Min(t => ResolveNumCtxForTask(opt, t, language: null, tier));
        // Reserve the LARGEST output among the consuming tasks so the tightest window still leaves room for
        // that task's generated output (input + output must fit num_ctx, else the model's answer truncates).
        // The reservation is PROVIDER-AWARE (p1-2): num_predict on Ollama, max_tokens on the cloud families.
        var outputReserve = tasks.Max(t => ResolveOutputReserveForTask(opt, t, language: null, tier));
        var budget = opt.EffectiveBookContextTokenBudget(numCtx, outputReserve);
        WarnIfWholeBookWindowIsUnsized(opt, tasks, budget, tier);
        return budget;
    }

    /// <summary>
    /// A whole-book context window at or below this is treated as UNSIZED rather than chosen: it is the bare
    /// <see cref="ProviderTuningOptions"/> class default, which a bound tuning entry supplies whenever it
    /// simply OMITS NumCtx. Bound options cannot distinguish "nobody set it" from "somebody set 4096", and on
    /// the whole-book path the two are equally wrong, so the value itself is the signal.
    /// </summary>
    private static readonly int UnsizedWholeBookNumCtx = new ProviderTuningOptions().NumCtx;

    /// <summary>
    /// De-duplication keys for <see cref="WarnIfWholeBookWindowIsUnsized"/>, so a windowed review that derives
    /// the budget once per window logs the misconfiguration ONCE rather than once per call. Instance-scoped
    /// (the assembler is AddScoped and one instance serves a whole review build) and concurrent because the
    /// windowed path fans out.
    ///
    /// The key is the ROUTE - <c>{provider}|{task}|{numCtx}</c> - and deliberately carries NO tier, even though
    /// the emitter now evaluates every tier. Two tiers that resolve the SAME provider for a task (which is every
    /// task outside <see cref="AiTierPolicy.TieredTasks"/>, and any tiered task whose tier key names the same
    /// provider) describe ONE misconfiguration with ONE fix - the same <c>Ai:ProviderSettings:{Provider}_{Task}</c>
    /// entry - so keying by tier would print the identical remedy twice. The message names the tier(s) that
    /// reach the route instead.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warnedUnsizedWindows = new();

    /// <summary>
    /// EVERY tier the emitter checks. Read off the enum rather than hand-listed, so a third tier is covered the
    /// day it is declared instead of the day somebody remembers this method.
    /// </summary>
    private static readonly AiTier[] AllTiers = Enum.GetValues<AiTier>();

    /// <summary>
    /// RUNTIME OBSERVABILITY for the silent-default hole (model-tier plan, p1-3). p1-3 adds the per-task cloud
    /// tuning entries and a build-time guard test that fails if one goes missing, but a guard test cannot see a
    /// MISCONFIGURED DEPLOYMENT: an appsettings.Production override, an environment variable, or a provider
    /// wired at runtime can still route a whole-book task at a provider with no sized window, and the resulting
    /// failure is invisible (prompt truncated -> unparseable model output -> job reports success with nothing
    /// found). This makes that state loud instead.
    ///
    /// WHY HERE AND NOT INSIDE THE RESOLVER. <see cref="ProviderTuningResolver"/> is called for EVERY task on
    /// every request, and 4096 is a perfectly legitimate resolved window for the CHUNKED tasks - the shipped
    /// Ollama_Proofread / Ollama_LineEdit entries resolve exactly that on purpose. Warning there would fire
    /// constantly on a correct config and train everyone to ignore it. <see cref="ResolveBudgetTokens"/> is by
    /// construction the WHOLE-BOOK path, where a class-default window is always a misconfiguration, so the
    /// warning is scoped to it and de-duplicated per (provider, task, num_ctx) rather than emitted per lookup.
    /// Non-throwing and cheap by design: this is observability, not a gate.
    ///
    /// WHY IT EVALUATES EVERY TIER AND NOT <paramref name="sizingTier"/> (fixes review P2-5, the
    /// emitter-reachability trap). <see cref="ResolveBudgetTokens"/>'s tier parameter defaults to
    /// <see cref="AiTier.Fast"/> and EVERY caller takes that default, deliberately (see the parameter's
    /// doc). If the emitter checked only the tier it was handed, then the day a whole-book task joins
    /// <see cref="AiTierPolicy.TieredTasks"/>, the signal added specifically to make the silent 4096-collapse
    /// loud would resolve the FAST provider and stay silent about the THINKING route - i.e. it would miss
    /// exactly the misconfiguration it exists to catch. Because this is OBSERVABILITY, the two error
    /// directions are not symmetric: an extra warning about a route no book uses yet costs a log line, while a
    /// missing one costs a green-looking job with 0 findings. So the emitter is TIER-INDEPENDENT: it checks
    /// every <see cref="AiTier"/> and warns for any route that is unsized, whichever tier reaches it. That
    /// also makes the emitter correct without threading the tier through <see cref="AssembleAsync"/> /
    /// <see cref="AssembleWindowsAsync"/>, which is out of the tier feature's GO'd scope.
    /// <paramref name="sizingTier"/> is still named in the message, because the reported budget was derived on
    /// THAT route and a reader must not read it as the budget of the route being warned about.
    /// </summary>
    private void WarnIfWholeBookWindowIsUnsized(
        AiOptions opt, IReadOnlyCollection<AiTaskType> tasks, int budget, AiTier sizingTier)
    {
        foreach (var task in tasks)
        {
            // Group by the ROUTE (provider + resolved window), which is exactly the de-duplication key, so a
            // task whose tiers share a provider yields ONE warning naming both tiers rather than two identical
            // ones, and a task the tier really moves yields one warning per unsized route.
            var unsizedRoutes = AllTiers
                .Select(t => (
                    Tier: t,
                    NumCtx: ResolveNumCtxForTask(opt, task, language: null, t),
                    Route: LinguisticModelResolver.ResolveForTask(opt, task, language: null, t)))
                .Where(x => x.NumCtx <= UnsizedWholeBookNumCtx)
                .GroupBy(x => (x.Route.provider, x.NumCtx));

            foreach (var route in unsizedRoutes)
            {
                var (provider, taskNumCtx) = route.Key;
                if (!_warnedUnsizedWindows.TryAdd($"{provider}|{task}|{taskNumCtx}", 0)) continue;

                var tiers = string.Join("/", route.Select(x => x.Tier.ToString()));
                var models = string.Join("/", route.Select(x => x.Route.model ?? "(default)").Distinct());

                _logger.LogWarning(
                    "BookContextAssembler: whole-book task {Task} routes to provider {Provider} (model {Model}) on the " +
                    "{Tiers} tier(s) with a context window of {NumCtx} tokens, which is at or below the " +
                    "ProviderTuningOptions class default ({ClassDefault}) - i.e. no Ai:ProviderSettings entry sized " +
                    "this task's window for this provider, so the lookup bound the class default instead of falling " +
                    "through. This build capped its assembled book context at {Budget} tokens (derived on the " +
                    "{SizingTier} route). On a multi-chapter book that overflows the prompt, the provider truncates " +
                    "it, the model returns unparseable output, and the job still reports success with 0 findings. Add " +
                    "an Ai:ProviderSettings:{ExpectedKey} entry with an explicit NumCtx sized to that model's real " +
                    "window.",
                    task, provider, models, tiers, taskNumCtx, UnsizedWholeBookNumCtx, budget, sizingTier,
                    ProviderTuningResolver.TaskKey(provider, task));
            }
        }
    }

    /// <summary>
    /// Active-model context window (num_ctx) for a task, resolved through <see cref="ProviderTuningResolver"/>
    /// — the SINGLE implementation of the "{provider}_{task}" → "{provider}" → ProviderTuningOptions-default
    /// precedence, shared with every provider's own request-time lookup (p1-1; it used to be spelled out
    /// longhand here AND in OllamaProvider.GetTuning). The tuning key uses the provider NAME resolved via
    /// <see cref="LinguisticModelResolver"/>, exactly as the router routes the task. Public so other
    /// budget-aware sizers (e.g. the language-aware proofread/LineEdit chunker in
    /// <see cref="UnifiedAnalysisService"/>) resolve a task's context window through the SAME precedence
    /// rather than duplicating it.
    ///
    /// NOTE the FIELD-level rung semantics (<see cref="ProviderTuningResolver.ResolvePositiveInt"/>): unlike a
    /// provider's whole-entry lookup, a rung whose NumCtx is unset (&lt;= 0) FALLS THROUGH here.
    /// </summary>
    public static int ResolveNumCtxForTask(AiOptions opt, AiTaskType task)
        => ResolveNumCtxForTask(opt, task, language: null, AiTier.Fast);

    /// <summary>
    /// LANGUAGE- AND TIER-AWARE overload (p3-2). Both arguments matter for the same reason: the tuning key
    /// is built from the RESOLVED PROVIDER, and the provider is chosen by a precedence whose first two rungs
    /// are the language key (<c>Proofread_en</c>) and the tier key (<c>Proofread_thinking</c>). Passing
    /// neither - the 2-arg overload above - resolves the bare task key, which is what every whole-book
    /// consumer wants and what the pre-p3-2 code did.
    ///
    /// The LANGUAGE argument also closes a divergence p1-4 found and pinned as harmless-today
    /// (<c>ChunkThresholdEndpointParityTests.ChunkSizerAndRouter_ResolveTheSameWindow_ForAnEnglishProofreadAndLineEdit</c>):
    /// the chunk sizer used to size English Proofread/LineEdit against the BARE key's provider while the
    /// router ran the <c>_en</c> entry's provider. They agree today only because both name Ollama. Now the
    /// sizer resolves through the same precedence, so they agree by construction.
    /// </summary>
    public static int ResolveNumCtxForTask(AiOptions opt, AiTaskType task, string? language, AiTier tier)
    {
        var (provider, _) = LinguisticModelResolver.ResolveForTask(opt, task, language, tier);
        // FIELD-level precedence (ProviderTuningResolver.ResolvePositiveInt): a rung whose NumCtx is <= 0
        // falls through instead of winning. Note this fall-through is DORMANT at today's class defaults —
        // NumCtx defaults to 4096, which is > 0, so an entry that merely OMITS NumCtx (e.g. Ollama_Proofread,
        // which sets only NumPredict) still resolves 4096 here, exactly matching the num_ctx OllamaProvider
        // sends for that task. The guard only bites on an explicit `"NumCtx": 0`. Final fallback is 4096.
        return ProviderTuningResolver.ResolvePositiveInt(opt.ProviderSettings, provider, task, t => t.NumCtx);
    }

    /// <summary>
    /// Active-model OUTPUT RESERVATION for a task — the number of tokens the provider that will actually run
    /// this task may generate — through the same <see cref="ProviderTuningResolver"/> precedence as the NumCtx
    /// sibling: "{provider}_{task}" → "{provider}" → the field's ProviderTuningOptions default. Used to reserve
    /// output headroom in the book-context budget so input + output fit the window.
    ///
    /// PROVIDER-AWARE SINCE p1-2, and that is the whole point of this method. The reservation must equal what
    /// the provider will REQUEST, and the two provider families name that number differently: Ollama sends
    /// <c>num_predict</c> (ProviderTuningOptions.NumPredict), the cloud families send <c>max_tokens</c>
    /// (ProviderTuningOptions.MaxTokens). Each appsettings entry sets only its own family's field, so reading
    /// NumPredict unconditionally — as this method did before p1-2 — silently returned the 2048 CLASS DEFAULT
    /// for a cloud-routed task whose entry said MaxTokens 5120, under-reserving output headroom by 3072 tokens
    /// on every cloud call. <see cref="ProviderTuningResolver.ResolveOutputTokens"/> picks the right field;
    /// do NOT name a field here. Public so other budget sizers resolve the reservation through the SAME
    /// accessor instead of re-deriving it.
    /// </summary>
    public static int ResolveOutputReserveForTask(AiOptions opt, AiTaskType task)
        => ResolveOutputReserveForTask(opt, task, language: null, AiTier.Fast);

    /// <summary>
    /// Language- and tier-aware counterpart of <see cref="ResolveNumCtxForTask(AiOptions, AiTaskType, string?, AiTier)"/>,
    /// for the same reason: the output knob is read off the entry the RESOLVED PROVIDER owns, and a tier can
    /// change that provider (and with it the family, hence which of NumPredict / MaxTokens is the knob).
    /// </summary>
    public static int ResolveOutputReserveForTask(
        AiOptions opt, AiTaskType task, string? language, AiTier tier)
    {
        var (provider, _) = LinguisticModelResolver.ResolveForTask(opt, task, language, tier);
        return ProviderTuningResolver.ResolveOutputTokens(opt.ProviderSettings, provider, task);
    }

    /// <summary>
    /// Assembles the budgeted whole-book context for (bookId, language). Uses the dense structured brief path
    /// whenever ANY chapter has a fresh structured brief (BookBrief + ordered ChapterBriefs); chapters that
    /// lack a fresh brief are NOT silently omitted — they are filled PER CHAPTER from their flat summary, then
    /// raw text, so a partially-built book still carries every chapter's content (dense where available,
    /// degraded elsewhere). Degrades to the whole-book flat-summary (then raw-text) fallback only when NO
    /// structured briefs exist at all. Anything that does not fit the budget is recorded in DroppedUnits and
    /// logged. Never overflows the resolved budget beyond the unavoidable BookBrief-alone case, which is logged.
    /// </summary>
    public async Task<BookContextAssembly> AssembleAsync(
        Guid bookId,
        string language,
        IReadOnlyCollection<AiTaskType>? consumingTasks = null,
        CancellationToken ct = default)
    {
        var budget = ResolveBudgetTokens(consumingTasks);

        // Prefer the dense structured path: L2 BookBrief + ordered L1 ChapterBriefs. Chapters without a fresh
        // brief are back-filled from their flat summary / raw text inside the structured assembly (no silent
        // truncation under partial coverage).
        var chapterBriefs = await _bookSummary.ComposeChapterBriefsAsync(bookId, language, ct);
        if (chapterBriefs.Count > 0)
        {
            var bookBrief = await _bookSummary.ComposeBookBriefAsync(bookId, chapterBriefs, ct);
            return await AssembleStructuredWithFallbackAsync(bookId, language, bookBrief, chapterBriefs, budget, ct);
        }

        // No usable structured briefs yet → degrade to the flat-summary fallback, still budget-guarded.
        _logger.LogInformation(
            "BookContextAssembler: no structured chapter briefs for book {BookId} ({Lang}); " +
            "falling back to budget-guarded flat summaries.", bookId, language);
        return await AssembleFlatFallbackAsync(bookId, language, budget, ct);
    }

    // ─── Windowed path (wb4-c01): partition the WHOLE book into ordered windows, dropping NOTHING ────────

    /// <summary>
    /// Assembles the whole-book context as an ORDERED list of WINDOWS for the whole-book review fan-out
    /// (wb4). Unlike <see cref="AssembleAsync"/> — which keeps ONE budgeted assembly and DROPS the chapters
    /// that do not fit — this partitions EVERY chapter into exactly one PRIMARY window: when adding a chapter
    /// would exceed a window's budget, the current window is CLOSED and a new one is STARTED, so no chapter is
    /// ever dropped for budget. The single <see cref="AssembleAsync"/> path and its other consumers
    /// (BookIntelligenceService, AnalysisContextService) are untouched.
    ///
    /// Per the todo rules:
    ///  (a) the BookBrief is placed FIRST in EVERY window (global anchor) and charged to that window's budget,
    ///      TRIMMED to <see cref="AiOptions.BookReviewWindowBriefMaxTokens"/> (Synopsis capped) so it leaves
    ///      room for chapters; the FULL brief is used only by the reduce passes;
    ///  (b) <see cref="AiOptions.BookReviewWindowOverlapChapters"/> (K) repeats the last K PRIMARY chapters of
    ///      window i at the head of window i+1 so a boundary-straddling issue is visible to one window intact;
    ///  (c) a single chapter whose block alone exceeds the window budget becomes its OWN over-budget window
    ///      (logged, <see cref="BookContextAssembly.WindowExceedsBudget"/> = true), never dropped;
    ///  (d) EVERY chapter lands in exactly one PRIMARY window — asserted, so nothing is silently omitted.
    ///
    /// When NO fresh structured briefs exist the per-chapter selection still degrades PER CHAPTER (fresh brief
    /// &gt; flat summary &gt; raw text), identical to the single path, so a partially-built book still windows.
    /// Returns one <see cref="BookContextAssembly"/> per window carrying the trimmed BookBrief, the included
    /// structured briefs, WindowIndex, IncludedChapterOrders and OverlapChapterOrders for wb4-c02.
    /// </summary>
    public async Task<IReadOnlyList<BookContextAssembly>> AssembleWindowsAsync(
        Guid bookId,
        string language,
        IReadOnlyCollection<AiTaskType>? consumingTasks = null,
        CancellationToken ct = default)
    {
        var budget = ResolveBudgetTokens(consumingTasks);
        var lang = BaselineLanguageResolver.Normalize(language);
        var charsPerToken = CharsPerTokenForLanguage(lang);

        // Reuse the SAME structured composition the single path uses (fresh L1 briefs + L2 rollup).
        var chapterBriefs = await _bookSummary.ComposeChapterBriefsAsync(bookId, language, ct);
        BookBrief? bookBrief = chapterBriefs.Count > 0
            ? await _bookSummary.ComposeBookBriefAsync(bookId, chapterBriefs, ct)
            : null;
        var freshBriefByOrder = chapterBriefs.ToDictionary(b => b.Order);

        // FULL chapter set in narrative order (so every chapter is windowed, dense or degraded).
        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => new { c.Id, c.Order, c.Title, c.ContentText })
            .ToListAsync(ct);

        var flatByChapterId = await LoadFlatSummariesByNormalizedLanguageAsync(bookId, lang, ct);

        // Trimmed BookBrief repeated at the head of every window (rule a). Charged with its separator, exactly
        // like the single structured path charges the BookBrief block.
        var briefMaxTokens = Math.Max(64, _aiOptions.Value.BookReviewWindowBriefMaxTokens);
        string briefBlock = string.Empty;
        int briefBlockTokens = 0;
        if (bookBrief != null)
        {
            var (briefText, trimmed) = FormatBookBriefTrimmed(bookBrief, briefMaxTokens, charsPerToken);
            if (!string.IsNullOrWhiteSpace(briefText))
            {
                briefBlock = briefText + BlockSeparator;
                briefBlockTokens = EstimateTokens(briefBlock, charsPerToken);
            }
            if (trimmed)
                _logger.LogInformation(
                    "BookContextAssembler (windows): BookBrief for book {BookId} trimmed to fit the per-window " +
                    "brief budget ({BriefMaxTokens} tokens) so each window keeps room for chapters.",
                    bookId, briefMaxTokens);
        }

        // Precompute each chapter's block + cost ONCE (narrative order), skipping genuinely empty chapters.
        var units = new List<(int Order, string Title, string Block, int Tokens, ChapterBrief? Brief)>();
        foreach (var chapter in chapters)
        {
            var title = chapter.Title ?? string.Empty;
            var (block, brief) = BuildChapterBlock(
                chapter.Order, title, chapter.Id, chapter.ContentText, freshBriefByOrder, flatByChapterId);
            if (block == null)
                continue; // genuinely empty chapter: nothing to window and nothing to truncate
            units.Add((chapter.Order, title, block, EstimateTokens(block, charsPerToken), brief));
        }

        var overlapK = Math.Max(0, _aiOptions.Value.BookReviewWindowOverlapChapters);

        // Greedy narrative-order partition: fill a window until the next chapter would exceed the budget, then
        // CLOSE it and START a new one (never drop). A single chapter that alone exceeds the budget becomes its
        // own over-budget window (rule c). Track PRIMARY membership per window so overlap is additive and every
        // chapter has exactly one primary window (rule d).
        var windows = new List<BookContextAssembly>();
        var prevPrimary = new List<(int Order, string Block, int Tokens)>(); // previous window's primary units
        var i = 0;
        while (i < units.Count)
        {
            var windowIndex = windows.Count;

            // Overlap prefix: repeat the last K PRIMARY chapters of the previous window (rule b). Charged to
            // this window's budget just like any chapter; if the overlap itself would blow the budget we still
            // include it (it is bounded by K, small) so the boundary context is intact.
            var overlapOrders = new List<int>();
            var overlapBlocks = new List<string>();
            var overlapTokens = 0;
            if (overlapK > 0 && windowIndex > 0)
            {
                var take = Math.Min(overlapK, prevPrimary.Count);
                for (var k = prevPrimary.Count - take; k < prevPrimary.Count; k++)
                {
                    overlapOrders.Add(prevPrimary[k].Order);
                    overlapBlocks.Add(prevPrimary[k].Block);
                    overlapTokens += prevPrimary[k].Tokens;
                }
            }

            var sb = new StringBuilder();
            sb.Append(briefBlock);
            foreach (var ob in overlapBlocks) sb.Append(ob);
            var used = briefBlockTokens + overlapTokens;

            var includedBriefs = new List<ChapterBrief>();
            var includedOrders = new List<int>(overlapOrders); // overlap first, then primaries
            var primaryUnitsThisWindow = new List<(int Order, string Block, int Tokens)>();
            var windowExceedsBudget = false;

            // Add primary chapters until the budget is hit. The window ALWAYS takes at least ONE primary
            // chapter so it makes progress even if that chapter alone is over budget (rule c).
            while (i < units.Count)
            {
                var u = units[i];
                var isFirstPrimary = primaryUnitsThisWindow.Count == 0;
                var wouldExceed = used + u.Tokens > budget;

                if (wouldExceed && !isFirstPrimary)
                    break; // close this window, start a new one with the next chapter (never drop)

                sb.Append(u.Block);
                used += u.Tokens;
                includedOrders.Add(u.Order);
                if (u.Brief != null) includedBriefs.Add(u.Brief);
                primaryUnitsThisWindow.Add((u.Order, u.Block, u.Tokens));
                i++;

                if (wouldExceed && isFirstPrimary)
                {
                    // Rule c: a single chapter whose block alone exceeds the window budget is its own window.
                    windowExceedsBudget = true;
                    _logger.LogWarning(
                        "BookContextAssembler (windows): chapter #{Order} '{Title}' ({Tokens} tokens, plus " +
                        "brief {BriefTokens}) alone exceeds the window budget ({Budget} tokens) for book " +
                        "{BookId}; emitting it as its own over-budget window (never dropped).",
                        u.Order, u.Title, u.Tokens, briefBlockTokens, budget, bookId);
                    break; // an over-budget solo chapter closes the window immediately
                }
            }

            prevPrimary = primaryUnitsThisWindow;

            windows.Add(new BookContextAssembly
            {
                Text = sb.ToString().TrimEnd(),
                BookBrief = bookBrief,
                IncludedChapterBriefs = includedBriefs,
                DroppedUnits = Array.Empty<DroppedBookUnit>(), // windows drop NOTHING
                UsedStructuredBriefs = bookBrief != null || includedBriefs.Count > 0,
                BudgetTokens = budget,
                EstimatedTokens = used,
                WindowIndex = windowIndex,
                IncludedChapterOrders = includedOrders,
                OverlapChapterOrders = overlapOrders,
                WindowExceedsBudget = windowExceedsBudget
            });
        }

        // Rule d: assert EVERY non-empty chapter landed in exactly one PRIMARY window (overlap excluded).
        var primaryOrders = windows
            .SelectMany(w => w.IncludedChapterOrders.Except(w.OverlapChapterOrders))
            .OrderBy(o => o)
            .ToList();
        var expectedOrders = units.Select(u => u.Order).OrderBy(o => o).ToList();
        if (!primaryOrders.SequenceEqual(expectedOrders))
        {
            // This is a programming invariant, not a user condition; loud so a regression cannot pass silently.
            var missing = expectedOrders.Except(primaryOrders).ToList();
            var extra = primaryOrders.Except(expectedOrders).ToList();
            _logger.LogError(
                "BookContextAssembler (windows): PRIMARY-window partition dropped/duplicated chapters for book " +
                "{BookId}. Missing: [{Missing}]; unexpected: [{Extra}].",
                bookId, string.Join(",", missing), string.Join(",", extra));
            throw new InvalidOperationException(
                $"AssembleWindowsAsync partition invariant violated for book {bookId}: " +
                $"missing primary chapters [{string.Join(",", missing)}], unexpected [{string.Join(",", extra)}].");
        }

        _logger.LogInformation(
            "BookContextAssembler (windows): book {BookId} ({Lang}) partitioned into {WindowCount} window(s) " +
            "over {ChapterCount} chapter(s); budget {Budget}t/window, overlap K={OverlapK}, {StructuredMode}.",
            bookId, lang, windows.Count, units.Count, budget, overlapK,
            bookBrief != null ? "structured briefs" : "degraded flat/raw fill");

        return windows;
    }

    /// <summary>
    /// Counts the REVIEWABLE (non-empty) chapters of (bookId, language): those that carry a fresh structured
    /// brief, a flat summary, or raw text — i.e. exactly the chapters that ENTER a window in
    /// <see cref="AssembleWindowsAsync"/> as a PRIMARY. A genuinely empty chapter (no brief, no summary, no text)
    /// is skipped by <see cref="BuildChapterBlock"/> and is NEVER windowed, so it is NOT counted. This is the
    /// SAME denominator a windowed build persists as <c>BookReviewCoverage.ChaptersTotal</c> (the distinct
    /// primaries across all windows), derived through the SHARED per-chapter selection (<see cref="BuildChapterBlock"/>)
    /// so the count cannot DRIFT from what the windowed build considers reviewable.
    ///
    /// WHY: the whole-book review's STATUS probe falls back to a chapter count when no persisted coverage row
    /// exists yet (before the first build). Using the RAW <c>Chapters</c> row count there made the coverage
    /// denominator JUMP after the first build (raw count → reviewable primaries) whenever the book had any empty
    /// chapters; this probe gives the status fallback the SAME reviewable denominator the build will persist, so
    /// it stays stable. LLM-free: composition reads only cached briefs (no summarization model call), mirroring
    /// the assemble paths.
    /// </summary>
    public async Task<int> CountReviewableChaptersAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        // Same three inputs BuildChapterBlock consumes in AssembleWindowsAsync: fresh structured briefs by Order,
        // the chapters (Id/Order/Title/ContentText), and the flat per-chapter summaries. Reusing BuildChapterBlock
        // keeps the empty-chapter decision single-sourced with the windowed partition.
        var chapterBriefs = await _bookSummary.ComposeChapterBriefsAsync(bookId, language, ct);
        var freshBriefByOrder = chapterBriefs.ToDictionary(b => b.Order);

        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .Select(c => new { c.Id, c.Order, c.Title, c.ContentText })
            .ToListAsync(ct);

        var flatByChapterId = await LoadFlatSummariesByNormalizedLanguageAsync(bookId, lang, ct);

        var count = 0;
        foreach (var chapter in chapters)
        {
            var (block, _) = BuildChapterBlock(
                chapter.Order, chapter.Title ?? string.Empty, chapter.Id, chapter.ContentText,
                freshBriefByOrder, flatByChapterId);
            if (block != null)
                count++; // genuinely empty chapters return a null block and are not reviewable
        }
        return count;
    }

    // ─── Shared read helper ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the flat per-chapter summaries for the book whose stored <see cref="ChunkSummary.Language"/>
    /// NORMALIZES to <paramref name="lang"/> (already normalized), keyed by ChapterId, excluding blanks.
    ///
    /// Matches on the NORMALIZED locale rather than an exact string because the legacy flat path
    /// (<see cref="BookIntelligenceService.SummarizeChaptersAsync"/>) can persist the RAW request value
    /// (e.g. "en-US") while the assembler keys on the normalized locale (e.g. "en"); an exact match would
    /// SKIP those rows and degrade to raw chapter text despite a usable summary existing. A genuinely
    /// different language (e.g. "he") still normalizes differently and is excluded, so this does not
    /// reintroduce a cross-language leak. Normalization runs in memory (it does not translate to SQL); there
    /// is at most one ChunkSummary per chapter, so the scan is bounded by the chapter count.
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadFlatSummariesByNormalizedLanguageAsync(
        Guid bookId, string lang, CancellationToken ct)
    {
        var rows = await _db.ChunkSummaries
            .AsNoTracking()
            .Where(cs => cs.BookId == bookId)
            .Select(cs => new { cs.ChapterId, cs.SummaryText, cs.Language })
            .ToListAsync(ct);

        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x.SummaryText)
                && BaselineLanguageResolver.Normalize(x.Language) == lang)
            .ToDictionary(x => x.ChapterId, x => x.SummaryText!);
    }

    // ─── Structured path (dense briefs + per-chapter flat/raw back-fill for uncovered chapters) ────────

    /// <summary>
    /// Assembles the structured whole-book context: the L2 BookBrief first, then EVERY chapter in narrative
    /// order. A chapter with a fresh L1 brief contributes its dense structured block; a chapter WITHOUT one
    /// is back-filled from its flat summary, then its raw text, so partial structured coverage never silently
    /// omits a chapter (the prior behaviour iterated only the fresh briefs, dropping uncovered chapters from
    /// both the context AND DroppedUnits). Anything that does not fit the budget — structured or flat — is
    /// recorded in DroppedUnits exactly like a token-budget drop. A genuinely empty chapter (no brief, no
    /// summary, no text) has nothing to contribute and is not recorded (it is not a truncation).
    /// </summary>
    private async Task<BookContextAssembly> AssembleStructuredWithFallbackAsync(
        Guid bookId,
        string language,
        BookBrief bookBrief,
        IReadOnlyList<ChapterBrief> chapterBriefs,
        int budget,
        CancellationToken ct)
    {
        var lang = BaselineLanguageResolver.Normalize(language);
        var charsPerToken = CharsPerTokenForLanguage(lang);

        // The FULL chapter set in narrative order, so chapters without a fresh structured brief can be
        // back-filled rather than silently omitted. Carries raw ContentText for the last-resort fill.
        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => new { c.Id, c.Order, c.Title, c.ContentText })
            .ToListAsync(ct);

        // Flat summaries for (book, language), keyed by ChapterId — the degraded fill for an uncovered chapter
        // (preferred over raw text). Matched by NORMALIZED locale so a legacy summary stored under the raw
        // request value (e.g. "en-US") is still found, while a different language is excluded (no leak).
        var flatByChapterId = await LoadFlatSummariesByNormalizedLanguageAsync(bookId, lang, ct);

        // Fresh structured briefs keyed by chapter Order (ChapterBrief carries Order, not ChapterId).
        var freshBriefByOrder = chapterBriefs.ToDictionary(b => b.Order);

        var sb = new StringBuilder();
        var bookBriefText = FormatBookBrief(bookBrief);

        // 1. BookBrief ALWAYS first. Charge the trailing separator too (it is part of the emitted Text):
        //    estimate the block WITH its separator, mirroring the flat path, so `used` never under-counts.
        var used = 0;
        if (!string.IsNullOrWhiteSpace(bookBriefText))
        {
            var block = bookBriefText + BlockSeparator;
            sb.Append(block);
            used += EstimateTokens(block, charsPerToken);
        }

        if (used > budget)
        {
            // Pathological: the BookBrief alone exceeds the budget. We still include it (the BookBrief is the
            // single most valuable block and must always be present), but log it so the overflow is visible.
            _logger.LogWarning(
                "BookContextAssembler: BookBrief for book {BookId} ({Tokens} tokens) alone exceeds the " +
                "context budget ({Budget} tokens); including it anyway. ALL {Count} chapters dropped.",
                bookId, used, budget, chapters.Count);
        }

        // 2. Walk EVERY chapter in narrative order. Each contributes its dense brief if fresh, else a flat/raw
        //    block. Once the budget is hit we drop the rest to keep a contiguous narrative prefix; every drop
        //    is recorded (no silent truncation).
        var included = new List<ChapterBrief>();
        var dropped = new List<DroppedBookUnit>();
        var flatFilledCount = 0;
        var budgetExhausted = used > budget;

        foreach (var chapter in chapters)
        {
            var title = chapter.Title ?? string.Empty;

            // Best available representation for this chapter: fresh structured brief > flat summary > raw text.
            var (block, structuredBrief) = BuildChapterBlock(
                chapter.Order, title, chapter.Id, chapter.ContentText, freshBriefByOrder, flatByChapterId);
            if (block == null)
                continue; // genuinely empty chapter: nothing to include and nothing to truncate

            var blockTokens = EstimateTokens(block, charsPerToken);
            if (budgetExhausted || used + blockTokens > budget)
            {
                budgetExhausted = true; // once we drop one, drop the rest to keep a contiguous prefix
                dropped.Add(new DroppedBookUnit { Order = chapter.Order, Title = title, EstimatedTokens = blockTokens });
                continue;
            }

            sb.Append(block);
            used += blockTokens;
            if (structuredBrief != null)
                included.Add(structuredBrief);
            else
                flatFilledCount++;
        }

        // Partial coverage is a degraded (but complete) assembly: surface it so it is never silent.
        if (flatFilledCount > 0)
            _logger.LogInformation(
                "BookContextAssembler: book {BookId} has partial structured coverage — {Structured} chapter(s) " +
                "from structured briefs + {Flat} chapter(s) back-filled from flat/raw text.",
                bookId, included.Count, flatFilledCount);

        if (dropped.Count > 0)
            // No silent truncation: log which units and how many did not fit.
            _logger.LogWarning(
                "BookContextAssembler: book {BookId} context budget ({Budget} tokens) reached. Included " +
                "{IncludedCount}/{TotalCount} chapters ({Structured} structured, {Flat} flat); DROPPED " +
                "{DroppedCount}: {DroppedTitles}.",
                bookId, budget, included.Count + flatFilledCount, chapters.Count, included.Count, flatFilledCount,
                dropped.Count, string.Join(", ", dropped.Select(d => $"#{d.Order} '{d.Title}'")));

        return new BookContextAssembly
        {
            Text = sb.ToString().TrimEnd(),
            BookBrief = bookBrief,
            IncludedChapterBriefs = included,
            DroppedUnits = dropped,
            UsedStructuredBriefs = true,
            BudgetTokens = budget,
            EstimatedTokens = used
        };
    }

    // ─── Flat / raw fallback path (still budget-guarded) ──────────────────────────────────────────────

    private async Task<BookContextAssembly> AssembleFlatFallbackAsync(
        Guid bookId,
        string language,
        int budget,
        CancellationToken ct)
    {
        var lang = BaselineLanguageResolver.Normalize(language);
        var charsPerToken = CharsPerTokenForLanguage(lang);

        // Walk the FULL chapter set in narrative order so NO chapter is silently omitted: each chapter is
        // represented by its flat summary if present, else its raw text. The prior version built the unit
        // list ONLY from chapters that already had a non-blank flat summary, and fell back to raw text for
        // the WHOLE book only when there were ZERO summaries — so under PARTIAL flat coverage (some chapters
        // with a summary, others with a blank/empty summary, only a stale structured brief, or no row at all)
        // the uncovered chapters were dropped from the context AND from DroppedUnits. Carry raw ContentText
        // for the per-chapter last-resort fill.
        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => new { c.Id, c.Order, c.Title, c.ContentText })
            .ToListAsync(ct);

        // Flat summaries for (book, language), keyed by ChapterId. Matched by NORMALIZED locale so a legacy
        // summary stored under the raw request value (e.g. "en-US") is still found, while a genuinely
        // different language (normalizing differently) is excluded — no cross-language leak into this context.
        var flatByChapterId = await LoadFlatSummariesByNormalizedLanguageAsync(bookId, lang, ct);

        var sb = new StringBuilder();
        var included = new List<ChapterBrief>(); // flat path carries no structured briefs
        var dropped = new List<DroppedBookUnit>();
        var used = 0;
        var budgetExhausted = false;
        var flatCount = 0;
        var rawCount = 0;

        foreach (var chapter in chapters)
        {
            var title = chapter.Title ?? string.Empty;

            // Best available representation for this chapter: flat summary > raw text.
            bool fromFlat;
            string body;
            if (flatByChapterId.TryGetValue(chapter.Id, out var summary) && !string.IsNullOrWhiteSpace(summary))
            {
                body = summary;
                fromFlat = true;
            }
            else
            {
                body = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
                fromFlat = false;
            }

            if (string.IsNullOrWhiteSpace(body))
                continue; // genuinely empty chapter: nothing to include and nothing to truncate

            var block = FormatFlatChapterBlock(chapter.Order, title, body);
            var blockTokens = EstimateTokens(block, charsPerToken);

            if (budgetExhausted || used + blockTokens > budget)
            {
                budgetExhausted = true;
                dropped.Add(new DroppedBookUnit { Order = chapter.Order, Title = title, EstimatedTokens = blockTokens });
                continue;
            }

            sb.Append(block);
            used += blockTokens;
            if (fromFlat) flatCount++; else rawCount++;
        }

        if (dropped.Count > 0)
        {
            _logger.LogWarning(
                "BookContextAssembler (flat fallback): book {BookId} context budget ({Budget} tokens) reached. " +
                "Included {IncludedCount}/{TotalCount} chapters ({Flat} flat, {Raw} raw); DROPPED {DroppedCount}: {DroppedTitles}.",
                bookId, budget, flatCount + rawCount, chapters.Count, flatCount, rawCount,
                dropped.Count, string.Join(", ", dropped.Select(d => $"#{d.Order} '{d.Title}'")));
        }

        return new BookContextAssembly
        {
            Text = sb.ToString().TrimEnd(),
            BookBrief = null,
            IncludedChapterBriefs = included,
            DroppedUnits = dropped,
            UsedStructuredBriefs = false,
            BudgetTokens = budget,
            EstimatedTokens = used
        };
    }

    // ─── Shared per-chapter block selection (used by the single path AND the windowed path) ────────────

    /// <summary>
    /// The SINGLE source of a chapter's best-representation block: fresh structured brief &gt; flat summary &gt;
    /// raw text. Returns the rendered block (already carrying its trailing <see cref="BlockSeparator"/>) plus
    /// the structured brief when one was used (null otherwise), or (null, null) for a genuinely empty chapter
    /// (no brief, no summary, no text) that has nothing to contribute and is not a truncation. Both
    /// <see cref="AssembleStructuredWithFallbackAsync"/> and <see cref="AssembleWindowsAsync"/> call this so
    /// their per-chapter selection and rendering cannot drift.
    /// </summary>
    private static (string? Block, ChapterBrief? StructuredBrief) BuildChapterBlock(
        int order,
        string title,
        Guid chapterId,
        string? contentText,
        IReadOnlyDictionary<int, ChapterBrief> freshBriefByOrder,
        IReadOnlyDictionary<Guid, string> flatByChapterId)
    {
        if (freshBriefByOrder.TryGetValue(order, out var brief))
            return (FormatChapterBrief(brief) + BlockSeparator, brief);

        var body = flatByChapterId.TryGetValue(chapterId, out var summary) && !string.IsNullOrWhiteSpace(summary)
            ? summary
            : SyncfusionWatermarkStripper.StripSyncfusionWatermark(contentText ?? "");
        if (string.IsNullOrWhiteSpace(body))
            return (null, null); // genuinely empty chapter: nothing to include and nothing to truncate
        return (FormatFlatChapterBlock(order, title, body), null);
    }

    // ─── Rendering (mirrors PromptFactory's brief formatting so the model reads a familiar shape) ──────

    /// <summary>
    /// Flat (degraded) per-chapter block: the "## פרק / Chapter {order}: {title}\n{body}\n\n" framing used by the
    /// flat fallback, shared so a chapter back-filled from its summary/raw text in the structured path reads
    /// identically. The trailing separator is part of the block (and is charged like every other block).
    ///
    /// The ORDER is part of the heading, exactly as in <see cref="FormatChapterBrief"/> ("## Chapter {order}: …").
    /// It used to be OMITTED here, which left a book on the degraded path showing the model chapter TITLES and no
    /// orders at all — while the review prompt asks for findings anchored BY ORDER. The model duly invented them:
    /// a one-chapter book (real order 0) whose only chapter is titled "פרק 16" came back with anchors claiming
    /// orders 1 and 16, read straight out of the title. Both paths must show the SAME 0-based order the parser
    /// resolves against (see ChapterAnchorResolver), or the model is being asked to guess.
    /// </summary>
    private static string FormatFlatChapterBlock(int order, string title, string body) =>
        $"## פרק / Chapter {order}: {title}\n{body}{BlockSeparator}";

    /// <summary>
    /// Renders the FULL (untrimmed) BookBrief into a self-contained <c>[BOOK_CONTEXT]…[/BOOK_CONTEXT]</c>
    /// block — the same markers every consumer of the assembled context reads. Public so the whole-book
    /// review REDUCE passes (wb4-c04 synthesis / wb4-c05 continuity) can reuse the SAME formatter to prepend
    /// the full brief to their reduce prompts rather than hand-rolling a divergent formatter (the windowed
    /// MAP charges a TRIMMED brief per window via <see cref="FormatBookBriefTrimmed"/>; the reduce passes get
    /// the whole brief once, so this untrimmed formatter is the right one for them).
    /// </summary>
    public static string FormatBookBrief(BookBrief b)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[BOOK_CONTEXT]");
        if (b.Genre != null) sb.AppendLine($"Genre: {b.Genre}{(b.SubGenre != null ? $" / {b.SubGenre}" : "")}");
        if (b.TargetAudience != null) sb.AppendLine($"Audience: {b.TargetAudience}");
        if (b.LiteratureLevel.HasValue) sb.AppendLine($"Literature level: {b.LiteratureLevel}/10");
        if (b.Themes.Count > 0) sb.AppendLine($"Themes: {string.Join(", ", b.Themes)}");
        if (b.Synopsis != null) sb.AppendLine($"Synopsis: {b.Synopsis}");
        sb.Append("[/BOOK_CONTEXT]");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a BookBrief trimmed to fit <paramref name="maxTokens"/> for the windowed review path, where the
    /// brief is repeated at the head of EVERY window and charged to each window's budget. The tiny fixed
    /// metadata lines (genre / audience / literature level) are ALWAYS kept — they are cheap and globally
    /// load-bearing. The two UNBOUNDED fields are then capped to fit the budget: the Themes list (a union of
    /// every chapter's markers, so it grows with book length) is truncated to as many entries as fit, then the
    /// Synopsis is capped to whatever room remains (Synopsis is the first thing sacrificed since Themes are the
    /// densest global signal). Returns the rendered text and whether ANYTHING was shortened (so the caller can
    /// log it: no silent truncation). The FULL untrimmed brief is still available via
    /// <see cref="FormatBookBrief"/> for the reduce passes. Uses the language-aware char/token density so the
    /// cap corresponds to real tokens.
    /// </summary>
    private static (string Text, bool Trimmed) FormatBookBriefTrimmed(BookBrief b, int maxTokens, double charsPerToken)
    {
        var full = FormatBookBrief(b);
        if (EstimateTokens(full, charsPerToken) <= maxTokens)
            return (full, false);

        const string closer = "[/BOOK_CONTEXT]";
        const string ellipsis = "…";

        // Irreducible metadata header (always kept): the cheap fixed lines.
        var head = new StringBuilder();
        head.AppendLine("[BOOK_CONTEXT]");
        if (b.Genre != null) head.AppendLine($"Genre: {b.Genre}{(b.SubGenre != null ? $" / {b.SubGenre}" : "")}");
        if (b.TargetAudience != null) head.AppendLine($"Audience: {b.TargetAudience}");
        if (b.LiteratureLevel.HasValue) head.AppendLine($"Literature level: {b.LiteratureLevel}/10");
        var headTokens = EstimateTokens(head.ToString() + closer, charsPerToken);

        // Fit as many Themes as the remaining room allows (Themes are the densest global signal; keep them
        // before the Synopsis). Build up entry-by-entry so we never exceed the cap.
        var themesLine = new StringBuilder();
        var themesTrimmed = false;
        if (b.Themes.Count > 0)
        {
            var kept = new List<string>();
            foreach (var theme in b.Themes)
            {
                var candidate = kept.Count == 0 ? theme : $"{string.Join(", ", kept)}, {theme}";
                var candidateTokens = EstimateTokens($"Themes: {candidate}\n", charsPerToken);
                // Keep at least one theme; stop once adding the next would break the cap (reserve a little
                // room for the Synopsis label too, but Themes get first claim after the header).
                if (kept.Count > 0 && headTokens + candidateTokens > maxTokens)
                {
                    themesTrimmed = true;
                    break;
                }
                kept.Add(theme);
            }
            if (kept.Count < b.Themes.Count) themesTrimmed = true;
            if (kept.Count > 0) themesLine.Append($"Themes: {string.Join(", ", kept)}\n");
        }
        var afterThemesTokens = headTokens + EstimateTokens(themesLine.ToString(), charsPerToken);

        // Fit the Synopsis into whatever room remains after header + (possibly-trimmed) themes.
        var synopsisLine = new StringBuilder();
        var synopsisTrimmed = false;
        if (!string.IsNullOrWhiteSpace(b.Synopsis))
        {
            var labelTokens = EstimateTokens("Synopsis: \n", charsPerToken);
            var remainingTokens = maxTokens - afterThemesTokens - labelTokens;
            if (remainingTokens > 0)
            {
                var maxSynopsisChars = Math.Max(0, (int)(remainingTokens * charsPerToken) - ellipsis.Length);
                if (b.Synopsis.Length > maxSynopsisChars)
                {
                    synopsisLine.Append($"Synopsis: {b.Synopsis.Substring(0, maxSynopsisChars).TrimEnd()}{ellipsis}\n");
                    synopsisTrimmed = true;
                }
                else
                {
                    synopsisLine.Append($"Synopsis: {b.Synopsis}\n");
                }
            }
            else
            {
                synopsisTrimmed = true; // no room at all → Synopsis dropped
            }
        }

        head.Append(themesLine);
        head.Append(synopsisLine);
        head.Append(closer);
        return (head.ToString(), themesTrimmed || synopsisTrimmed);
    }

    private static string FormatChapterBrief(ChapterBrief ch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Chapter {ch.Order}: {ch.Title}");
        if (!string.IsNullOrWhiteSpace(ch.Summary)) sb.AppendLine(ch.Summary);
        if (ch.PlotEvents.Count > 0) sb.AppendLine($"Plot events: {string.Join("; ", ch.PlotEvents)}");
        if (ch.CharacterStates.Count > 0)
        {
            foreach (var cs in ch.CharacterStates)
            {
                sb.Append($"  {cs.Name}");
                if (cs.State != null) sb.Append($" - {cs.State}");
                if (cs.EmotionalArc != null) sb.Append($" ({cs.EmotionalArc})");
                sb.AppendLine();
            }
        }
        if (ch.ThematicMarkers.Count > 0) sb.AppendLine($"Themes: {string.Join(", ", ch.ThematicMarkers)}");
        if (ch.OpenThreads.Count > 0) sb.AppendLine($"Open threads: {string.Join("; ", ch.OpenThreads)}");
        if (!string.IsNullOrWhiteSpace(ch.ToneNotes)) sb.AppendLine($"Tone: {ch.ToneNotes}");
        return sb.ToString().TrimEnd();
    }

    // ─── Continuity skeleton (wb4-c05 hierarchical continuity reduce) ──────────────────────────────────

    /// <summary>Open/close markers wrapping the dense continuity skeleton block. The wb4-c05 continuity reduce
    /// prompt reads chapters from between these, and the test mock switch distinguishes the continuity pass
    /// from the synthesis pass (which carries [WINDOW_FINDINGS]) on this marker.</summary>
    public const string ContinuitySkeletonOpen = "[CONTINUITY_SKELETON]";
    public const string ContinuitySkeletonClose = "[/CONTINUITY_SKELETON]";

    /// <summary>
    /// DETERMINISTIC (no model call) dense per-chapter continuity line — MUCH denser than a full
    /// <see cref="FormatChapterBrief"/> block: plot prose, tone and summary are DROPPED, keeping only the two
    /// signals a cross-chapter continuity pass needs — the chapter's OPEN THREADS and its CHARACTER STATES.
    /// Format: <c>#&lt;order&gt; &lt;title&gt; | threads: &lt;openThreads joined '; '&gt; | states: &lt;name:state; ...&gt;</c>.
    /// A missing state renders the bare name; an empty threads/states list renders an empty segment (kept so
    /// every line has the same fixed shape). Mirrors <see cref="FormatChapterBrief"/>'s CharacterStates /
    /// OpenThreads iteration so the two renderers cannot drift on which fields count.
    /// </summary>
    public static string FormatContinuitySkeletonLine(ChapterBrief ch)
    {
        var threads = ch.OpenThreads.Count > 0
            ? string.Join("; ", ch.OpenThreads.Select(OneLine))
            : string.Empty;
        var states = ch.CharacterStates.Count > 0
            ? string.Join("; ", ch.CharacterStates.Select(cs =>
                string.IsNullOrWhiteSpace(cs.State) ? OneLine(cs.Name) : $"{OneLine(cs.Name)}:{OneLine(cs.State)}"))
            : string.Empty;
        return $"#{ch.Order} {OneLine(ch.Title)} | threads: {threads} | states: {states}";
    }

    /// <summary>
    /// Collapses embedded CR/LF to a single space so no field value can break the strict
    /// one-line-per-chapter shape the continuity-reduce prompt relies on.
    /// </summary>
    private static string OneLine(string? s) => (s ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// Builds the full <c>[CONTINUITY_SKELETON]…[/CONTINUITY_SKELETON]</c> block over the supplied chapter
    /// briefs (narrative order) via <see cref="FormatContinuitySkeletonLine"/> — one dense line per chapter.
    /// DETERMINISTIC: no model call, so wb4-c05 can compute the grouping (and thus the reduce call count) from
    /// the already-composed briefs BEFORE reserving progress chunks. Public so the review service + its tests
    /// can reach it.
    /// </summary>
    public static string FormatContinuitySkeleton(IReadOnlyList<ChapterBrief> briefs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ContinuitySkeletonOpen);
        foreach (var ch in briefs)
            sb.AppendLine(FormatContinuitySkeletonLine(ch));
        sb.Append(ContinuitySkeletonClose);
        return sb.ToString();
    }
}
