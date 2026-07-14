using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// be-c05 — the TWO-STAGE parse of an untrusted BookReview model response. It exists for one reason: THE OPTIONAL,
/// MODEL-SUPPLIED <c>merges</c> KEY MUST NEVER BE ABLE TO DESTROY THE FINDINGS.
///
/// THE BUG (P1-5). Every BookReview pass used to read its response with ONE strict
/// <c>JsonSerializer.Deserialize&lt;BookReviewResult&gt;</c>. <see cref="BookReviewResult.Merges"/> is typed
/// <c>List&lt;SynthesisMergeItem&gt;?</c>, so any shape the model gets wrong there throws <see cref="JsonException"/>
/// on the WHOLE document — and the caller's outer catch then discards the ENTIRE response, findings and all
/// ("treating as zero synthesis findings"). Three shapes do it, and none is exotic:
///   • <c>"merges": { "ids": [...], "keep": "..." }</c> — an OBJECT where an array belongs. The single most likely
///     mistake there is, because the prompt's schema block shows exactly ONE example group.
///   • <c>"ids": "W3,W7"</c> — the ids as one comma-joined string.
///   • <c>"ids": [3, 7]</c> — the ids as numbers.
/// The blast radius is the whole point: the synthesis pass's OWN book-level findings — the holistic observations no
/// window could see — die because of a formatting slip in an OPTIONAL, ADDITIVE side-channel. And the b8 kill-switch
/// does NOT protect against it, because THE PROMPT ASKS FOR <c>merges</c> REGARDLESS OF THE SWITCH: a malformed merge
/// map can therefore delete real findings on a build where the merge feature is supposedly OFF.
///
/// THE PARSE, AND WHY IT IS SHAPED THIS WAY. <c>findings</c> and <c>merges</c> are read from the root object
/// SEPARATELY, out of a <see cref="JsonDocument"/>:
///   • STAGE 1 — <c>findings</c> is deserialized on its own. Its contract (below) is UNCHANGED, byte for byte. A
///     malformed <c>findings</c> still throws, and the callers still treat that as a failed pass. That direction is
///     deliberate: the merge map is a DELETE channel, and a response whose findings are garbage is not a response
///     whose delete instructions we should trust.
///   • STAGE 2 — <c>merges</c> is read inside its OWN try/catch and can only ever degrade to NOTHING. It cannot throw
///     out of this method, so it cannot take stage 1 down with it. WORST CASE FOR A MALFORMED MERGE MAP IS ZERO MERGE
///     GROUPS — never a lost finding. That is the fail-closed direction the whole subsystem is built on (a merge we do
///     not make is a visible duplicate; a merge we make wrongly is a deleted finding).
///
/// THE FINDINGS TRI-STATE IS LOAD-BEARING (be-c01) AND IS REPRODUCED EXACTLY. <see cref="WindowOutcomes.Classify"/>
/// distinguishes "the call failed" (null) from "it parsed and reported nothing" (empty), and only the latter is a
/// SUSPECTED TRUNCATION whose chapters are withheld from the destructive delete pass. That distinction is currently an
/// artefact of System.Text.Json's treatment of the <c>= new()</c> initialiser, so it is restated here explicitly
/// rather than left to be re-derived:
///   • key ABSENT              → an EMPTY list  (STJ leaves the <c>= new()</c> initialiser alone)
///   • <c>"findings": null</c> → NULL           (STJ WRITES the null OVER the initialiser — the RepairableFields lesson)
///   • an array                → that array, with any null ELEMENT dropped (mirrors RepairableFields' per-element skip)
/// Do not "simplify" the first two into one: <c>{}</c> is a suspected truncation, an explicit null is a failure.
///
/// TOLERANCE, AND ITS LIMIT. Stage 2 repairs only what is LEXICALLY unambiguous — an object wrapped as a one-group
/// array, a delimited id string split on its delimiters, a numeric id rendered as its own digits. It NEVER invents
/// SEMANTICS: a bare <c>3</c> becomes the id <c>"3"</c>, NOT <c>"W3"</c>, because the digest prints chapter orders in
/// the very next column and a model that has confused the two columns is exactly the confused model this contract
/// refuses to half-trust. Every coerced group is then handed to <see cref="SynthesisMergeMap.Resolve"/> UNCHANGED and
/// faces its full all-or-nothing validation (unknown id / duplicate id / blank id / fewer than 2 ids / a <c>keep</c>
/// outside the group / a finding already claimed by another group → the WHOLE group is rejected). This parser widens
/// what the model may TYPE; it does not widen what the model may DO.
///
/// AND IT IS LOUD. Every coercion and every discarded payload increments a fault counter and produces ONE WARNING per
/// pass. A fail-safe that silently swallows a malformed payload ships its failures invisibly — this codebase has
/// re-shipped that exact class often enough to make the log part of the fix, not a nicety.
/// </summary>
internal static class BookReviewResponseParser
{
    /// <summary>The two channels of a BookReview response, parsed independently.</summary>
    /// <param name="Findings">NULL = the response had an explicit <c>"findings": null</c> (a FAILURE for every
    /// caller). EMPTY = the key was absent or the array was empty (a suspected truncation on the window path; a
    /// legitimate merges-only answer on the synthesis path). Non-empty = a real result.</param>
    /// <param name="Merges">The model's raw merge groups after LEXICAL coercion, or NULL when it emitted none / the
    /// payload was uninterpretable. Still fully UNTRUSTED: <see cref="SynthesisMergeMap.Resolve"/> is the only thing
    /// allowed to turn any of this into an action.</param>
    internal sealed record Response(
        List<BookFindingItem>? Findings,
        List<SynthesisMergeItem>? Merges);

    /// <summary>
    /// Parses one BookReview model response. THROWS (<see cref="JsonException"/>) only on a root that is not a JSON
    /// object or on a malformed <c>findings</c> — exactly the cases that threw before, so every caller's
    /// treat-as-failure catch keeps its meaning. A malformed <c>merges</c> NEVER throws.
    /// </summary>
    /// <param name="json">The extracted JSON body (post <see cref="UnifiedAnalysisService.ExtractJson"/>).</param>
    /// <param name="findingsOpts">The findings' deserialize options (camelCase + case-insensitive), passed in so this
    /// parser does not become a FOURTH copy of the same JsonSerializerOptions.</param>
    /// <param name="scope">The pass, for the log line ("window 2/3", "synthesis", "continuity").</param>
    internal static Response Parse(
        string json,
        JsonSerializerOptions findingsOpts,
        string scope,
        ILogger? logger = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Book review ({scope}): the response root is a {root.ValueKind}, not an object.");

        var findings = ParseFindings(root, findingsOpts);
        var merges = ParseMerges(root, scope, logger);
        return new Response(findings, merges);
    }

    // ─── STAGE 1: findings (strict; the contract is unchanged) ────────────────────────────────────────

    private static List<BookFindingItem>? ParseFindings(JsonElement root, JsonSerializerOptions opts)
    {
        if (!root.TryGetProperty(FindingsKey, out var el))
            return new List<BookFindingItem>(); // ABSENT: the `= new()` initialiser survives → empty, NOT null.

        if (el.ValueKind == JsonValueKind.Null)
            return null; // EXPLICIT null: STJ writes it OVER the initialiser. A failure for every caller.

        // Any other wrong shape (an object, a string, a number) throws here exactly as it threw before.
        var list = el.Deserialize<List<BookFindingItem>>(opts) ?? new List<BookFindingItem>();

        // `"findings": [null]` is valid JSON and yields a list holding a null. Drop those elements — the canonical
        // per-DTO null-guard (RepairableFields.For(BookReviewResult)) does the same, and every consumer downstream
        // of here dereferences the items unconditionally.
        list.RemoveAll(f => f is null);
        return list;
    }

    // ─── STAGE 2: merges (defensive; degrades to nothing, never throws) ───────────────────────────────

    private static List<SynthesisMergeItem>? ParseMerges(JsonElement root, string scope, ILogger? logger)
    {
        if (!root.TryGetProperty(MergesKey, out var el) || el.ValueKind == JsonValueKind.Null)
            return null; // absent or null: the key is OPTIONAL and the prompt says so. Nothing to report.

        var faults = new Dictionary<string, int>(StringComparer.Ordinal);
        List<SynthesisMergeItem>? merges;
        try
        {
            merges = CoerceMerges(el, faults);
        }
        catch (Exception ex)
        {
            // The floor. Whatever the model did, the findings above are already parsed and are going home.
            logger?.LogWarning(ex,
                "Book review ({Scope}): the `merges` payload could not be read AT ALL; NO merge is applied this build " +
                "(fail-closed). The findings in this same response are UNAFFECTED — a malformed merge map can no " +
                "longer take them down with it (be-c05).",
                scope);
            return null;
        }

        if (faults.Count > 0)
        {
            // A schema violation the model committed. It is not an Error (the build completes correctly, with zero or
            // with fewer merge groups — the fail-closed direction), but it is not routine either: it means the model
            // is not honouring the output contract, which is a PROMPT defect a human has to see and fix. Warning is
            // the level that says "this build is sound, and something upstream of it is not".
            logger?.LogWarning(
                "Book review ({Scope}): the `merges` payload did not match the contract and was COERCED/REJECTED " +
                "({Faults}). {Groups} group(s) were recovered and still face SynthesisMergeMap's full validation; the " +
                "findings in this response are UNAFFECTED (be-c05: a malformed merge map degrades to fewer merges, " +
                "never to a lost finding).",
                scope, Describe(faults), merges?.Count ?? 0);
        }

        return merges;
    }

    /// <summary>Coerces the raw <c>merges</c> element into merge groups. Returns ONE item per group the model
    /// emitted — including the ones it botched — so <see cref="SynthesisMergeMap.Resolution.ProposedGroups"/> stays an
    /// HONEST count of what was proposed, and every rejection is attributed by
    /// <see cref="SynthesisMergeMap.Resolve"/>'s own reason counters rather than vanishing here.</summary>
    private static List<SynthesisMergeItem>? CoerceMerges(JsonElement el, IDictionary<string, int> faults)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                return el.EnumerateArray().Select(e => CoerceGroup(e, faults)).ToList();

            case JsonValueKind.Object:
                // THE NATURAL MISTAKE: the prompt's schema block shows ONE example group, so the model emits that one
                // group as a bare object. It is lexically unambiguous — one group — and the group it names still has
                // to survive Resolve intact, so wrapping it costs nothing and recovers a merge the model really did
                // propose. (A dict-shaped object with no `ids` simply becomes a group with no ids → "no-ids" reject.)
                Fault(faults, "merges-was-an-object-not-an-array");
                return new List<SynthesisMergeItem> { CoerceGroup(el, faults) };

            default:
                // A string / number / bool where the array belongs. Nothing to interpret; take the floor.
                Fault(faults, "merges-was-a-" + el.ValueKind.ToString().ToLowerInvariant());
                return null;
        }
    }

    private static SynthesisMergeItem CoerceGroup(JsonElement el, IDictionary<string, int> faults)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            // Not a group at all (e.g. `"merges": ["W3","W7"]` — the model flattened the groups away). Emit an
            // ids-less group so it is COUNTED as proposed and then rejected by Resolve ("no-ids"), rather than
            // silently disappearing from the funnel the coverage log reports.
            Fault(faults, "group-was-not-an-object");
            return new SynthesisMergeItem();
        }

        return new SynthesisMergeItem
        {
            Ids = el.TryGetProperty(IdsKey, out var ids) ? CoerceIds(ids, faults) : null,
            Keep = el.TryGetProperty(KeepKey, out var keep) ? CoerceId(keep, faults) : null,
        };
    }

    /// <summary>The ids of ONE group. An array is the contract; a delimited STRING is split (a lexical repair — the
    /// tokens still have to be ids the digest actually printed, or Resolve rejects the group). Anything else yields
    /// null → Resolve's "no-ids".</summary>
    private static List<string>? CoerceIds(JsonElement el, IDictionary<string, int> faults)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                return el.EnumerateArray().Select(e => CoerceId(e, faults) ?? string.Empty).ToList();

            case JsonValueKind.String:
                // `"ids": "W3,W7"`. Splitting is safe BECAUSE it is only lexical: each token must still be an id this
                // build's digest printed, and the group must still pass every one of Resolve's fences. A token that is
                // not a real id makes the whole group an "unknown-id" reject, exactly as if it had been typed in an
                // array.
                Fault(faults, "ids-was-a-string-not-an-array");
                return (el.GetString() ?? string.Empty)
                    .Split(IdSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

            case JsonValueKind.Null:
                return null;

            default:
                Fault(faults, "ids-was-a-" + el.ValueKind.ToString().ToLowerInvariant());
                return null;
        }
    }

    /// <summary>One id token. A STRING is the contract. A NUMBER is rendered as its OWN DIGITS and nothing more:
    /// <c>3</c> becomes <c>"3"</c>, NOT <c>"W3"</c>. Guessing the prefix would be inventing semantics for a model that
    /// has just demonstrated it is not reading the id column — and the column immediately to its right in the digest
    /// is the CHAPTER ORDER, which is also a small integer. So a numeric id resolves to nothing and its group is
    /// rejected as "unknown-id": zero merges, zero lost findings. Anything else yields null → "blank-id".</summary>
    private static string? CoerceId(JsonElement el, IDictionary<string, int> faults)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString();

            case JsonValueKind.Number:
                Fault(faults, "id-was-a-number");
                return el.GetRawText();

            case JsonValueKind.Null:
                return null;

            default:
                Fault(faults, "id-was-a-" + el.ValueKind.ToString().ToLowerInvariant());
                return null;
        }
    }

    private const string FindingsKey = "findings";
    private const string MergesKey = "merges";
    private const string IdsKey = "ids";
    private const string KeepKey = "keep";

    /// <summary>What a model uses to join ids inside one string: a comma, a semicolon, a slash, a plus (the digest's
    /// own log format writes "W3+W7"), or plain whitespace.</summary>
    private static readonly char[] IdSeparators = { ',', ';', '/', '+', ' ', '\t', '\n', '\r' };

    private static void Fault(IDictionary<string, int> faults, string reason) =>
        faults[reason] = faults.TryGetValue(reason, out var n) ? n + 1 : 1;

    private static string Describe(IReadOnlyDictionary<string, int> faults) =>
        string.Join(", ", faults.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
}
