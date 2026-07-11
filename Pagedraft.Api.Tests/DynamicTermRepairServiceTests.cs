using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic tests for <see cref="DynamicTermRepairService"/> (dynamic-term-repair-design plan, todo
/// d3): the SPAN-SCOPED LLM repair stage. A hand-written FAKE <see cref="IAiRouter"/> returns canned
/// {"replacement":"…"} payloads keyed by the MARKED run in the request input, so the call count and every
/// per-call response (including a throw) are fully under test control — NO Ollama, NO GPU, always-CI.
///
/// Coverage: correct-span substitution, multi-run offsets, unchanged-on-proper-noun (model echoes the
/// foreign token -> validation keeps original), malformed model output -> original, validation revert on a
/// still-foreign token, never-throws on a faulting router, the ZERO-call gate (clean prose / all-LEAVE
/// runs), the Hebrew-in-English direction, and the RepairableField-list convenience (prose repaired,
/// structural fields untouched). The class name matches the <c>~DynamicTermRepair</c> test filter.
/// </summary>
public class DynamicTermRepairServiceTests
{
    // ─── Fake IAiRouter ─────────────────────────────────────────────────────

    /// <summary>
    /// Records how many times <see cref="CompleteAsync"/> was invoked and returns a caller-supplied string as
    /// <see cref="AiResponse.Content"/>. The responder may THROW to simulate a router error/timeout (the call
    /// is still counted — it WAS made — before the throw propagates into the service's fail-safe catch).
    /// </summary>
    private sealed class FakeAiRouter : IAiRouter
    {
        private readonly Func<AiRequest, string> _respond;

        public int CallCount { get; private set; }
        public List<AiRequest> Requests { get; } = new();

        public FakeAiRouter(Func<AiRequest, string> respond) => _respond = respond;

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            var content = _respond(request); // may throw -> propagates into the per-span fail-safe catch
            return Task.FromResult(new AiResponse { Content = content, Provider = "fake", Model = "fake-model" });
        }

        public IAsyncEnumerable<string> StreamCompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("DynamicTermRepairService never streams.");
    }

    /// <summary>The token the service MARKED between «…» in the request input; the fake keys its canned
    /// replacement on this so a test controls the response per span.</summary>
    private static string MarkedToken(AiRequest req)
    {
        var s = req.InputText;
        var i = s.IndexOf('«');
        var j = s.IndexOf('»');
        return i >= 0 && j > i ? s.Substring(i + 1, j - i - 1) : string.Empty;
    }

    /// <summary>Router that returns a canned <c>{"replacement":"…"}</c> for each marked token found in
    /// <paramref name="map"/>; for an unmapped token it ECHOES the token unchanged (simulating the model
    /// declining a proper noun / no-equivalent term).</summary>
    private static FakeAiRouter KeyedRouter(IReadOnlyDictionary<string, string> map)
        => new(req =>
        {
            var token = MarkedToken(req);
            var repl = map.TryGetValue(token, out var mapped) ? mapped : token;
            return $"{{\"replacement\":\"{repl}\"}}";
        });

    /// <summary>Router that must never be invoked; throws loudly if it is (belt-and-braces alongside the
    /// CallCount==0 assertions in the zero-call-gate tests).</summary>
    private static FakeAiRouter NeverCalledRouter()
        => new(_ => throw new InvalidOperationException("router must not be called"));

    /// <summary>Router that THROWS when the MARKED token equals <paramref name="throwOnToken"/> (simulating a
    /// per-span router error/timeout on exactly ONE span) and otherwise behaves like <see cref="KeyedRouter"/>
    /// (canned replacement from <paramref name="map"/>, or an echo of the token if unmapped). Lets a test drive
    /// two-or-more-run values where one span errors and the others must still be processed independently.</summary>
    private static FakeAiRouter ThrowingKeyedRouter(string throwOnToken, IReadOnlyDictionary<string, string> map)
        => new(req =>
        {
            var token = MarkedToken(req);
            if (token == throwOnToken)
            {
                throw new InvalidOperationException($"router boom for span '{token}'");
            }

            var repl = map.TryGetValue(token, out var mapped) ? mapped : token;
            return $"{{\"replacement\":\"{repl}\"}}";
        });

    private static DynamicTermRepairService NewService(IAiRouter router)
        => new(router, NullLogger<DynamicTermRepairService>.Instance);

    // ─── (a) correct-span substitution ──────────────────────────────────────

    [Fact]
    public async Task Repair_SingleLatinLeakInHebrew_SubstitutesAtRightOffset_RestByteIdentical()
    {
        const string value = "הדמות שקעה במצב של confusion מוחלט.";
        var router = KeyedRouter(new Dictionary<string, string> { ["confusion"] = "בלבול" });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);                          // exactly one flagged run => one model call
        Assert.Equal(1, result.Flagged);
        Assert.Equal(1, result.Repaired);
        Assert.Equal(0, result.Reverted);
        // Offset-exact splice: everything but the run is byte-identical (a single-occurrence Replace matches).
        Assert.Equal(value.Replace("confusion", "בלבול"), result.Value);
        Assert.DoesNotContain("confusion", result.Value);
    }

    // ─── (b) multi-run in one value ─────────────────────────────────────────

    [Fact]
    public async Task Repair_TwoLatinLeaksInOneValue_BothSubstituted_OffsetsCorrect()
    {
        const string value = "היה שם confusion ואחריו panic גדול.";
        var router = KeyedRouter(new Dictionary<string, string>
        {
            ["confusion"] = "בלבול",
            ["panic"] = "בהלה",
        });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(2, router.CallCount);
        Assert.Equal(2, result.Flagged);
        Assert.Equal(2, result.Repaired);
        // Both runs replaced at their correct offsets (right-to-left splicing keeps earlier offsets valid).
        Assert.Equal(value.Replace("confusion", "בלבול").Replace("panic", "בהלה"), result.Value);
        Assert.DoesNotContain("confusion", result.Value);
        Assert.DoesNotContain("panic", result.Value);
    }

    // ─── (c) unchanged-on-proper-noun: model echoes the token -> keep original ─

    [Fact]
    public async Task Repair_ModelReturnsTokenUnchanged_ProperNounOrBrand_KeepsOriginalSpan()
    {
        // "spotify" is a REPAIR-classified lowercase run, but the model (empty map -> echo) returns it
        // unchanged (a brand / no Hebrew equivalent). The echoed token is still Latin -> validation rejects
        // it and the ORIGINAL span is kept, so the value is byte-identical.
        const string value = "הוא הוריד את spotify אתמול בלילה.";
        var router = KeyedRouter(new Dictionary<string, string>()); // echoes every marked token

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount); // the run WAS sent (classifier can't know it has no equivalent)
        Assert.Equal(1, result.Flagged);
        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.Reverted);
        Assert.Equal(value, result.Value); // unchanged
    }

    // ─── (d) malformed / empty / missing / null model output -> keep original ─

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"foo\":\"bar\"}")]          // missing replacement key
    [InlineData("{\"replacement\":null}")]      // null value
    [InlineData("{\"replacement\":\"   \"}")]  // whitespace value
    [InlineData("{\"replacement\":\"בלבול")]  // truncated / malformed JSON
    public async Task Repair_MalformedModelOutput_KeepsOriginalSpan_NeverThrows(string rawPayload)
    {
        const string value = "הדמות שקעה במצב של confusion מוחלט.";
        var router = new FakeAiRouter(_ => rawPayload);

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);
        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.Reverted);
        Assert.Equal(value, result.Value); // original kept verbatim
    }

    // ─── (e) validation revert: model returns a still-foreign token ──────────

    [Fact]
    public async Task Repair_ModelReturnsStillForeignJunk_ValidationReverts_KeepsOriginalSpan()
    {
        // The replacement is predominantly-Latin junk (a NEW foreign run) -> the re-detect validation rejects
        // it and the ORIGINAL span is kept. Never ship a value with foreign runs the model just re-introduced.
        const string value = "הדמות שקעה במצב של confusion מוחלט.";
        var router = KeyedRouter(new Dictionary<string, string> { ["confusion"] = "stillEnglish" });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);
        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.Reverted);
        Assert.Equal(value, result.Value);
    }

    // ─── (e2) whole-sentence NATIVE-script echo -> length/terminator guard reverts (P2) ─
    //
    // THE GAP this guard closes: a misbehaving model echoes the WHOLE marked sentence (or a long paraphrase)
    // in the NATIVE script (Hebrew) for a single marked run. It carries NO foreign run, so the foreign-run
    // validation passes — yet spliced into that ONE run's offset it DUPLICATES the surrounding prose, silently
    // corrupting the value. The whole-value backstop does not fire either (a native echo does not INCREASE the
    // foreign-run count). The length/word/terminator guard in IsAcceptableReplacement REVERTS it, keeping the
    // ORIGINAL span byte-identical. (Remove that guard and this test FAILS — the echo is spliced, Repaired=1.)
    [Fact]
    public async Task Repair_ModelEchoesWholeSentenceInHebrew_LengthGuardReverts_KeepsOriginalSpan()
    {
        const string value = "הדמות שקעה במצב של confusion מוחלט לפני שהתעשתה.";
        // 8-word Hebrew paraphrase of the WHOLE marked sentence (no Latin run) — a classic verbose echo.
        const string wholeSentenceEcho = "הדמות שקעה במצב של בלבול מוחלט לפני שהתעשתה";
        var router = KeyedRouter(new Dictionary<string, string> { ["confusion"] = wholeSentenceEcho });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);
        Assert.Equal(1, result.Flagged);
        Assert.Equal(0, result.Repaired);   // the verbose echo was rejected...
        Assert.Equal(1, result.Reverted);   // ...and the ORIGINAL span kept
        Assert.Equal(value, result.Value);  // byte-identical: no duplicated prose spliced in
        Assert.DoesNotContain(wholeSentenceEcho, result.Value);
    }

    // ─── (e3) legit SHORT multi-word Hebrew equivalent -> accepted (guard not too strict) ─
    //
    // Guards against the whole-sentence guard being over-eager: a real term legitimately maps to a 2-3 word
    // Hebrew phrase (claustrophobia -> "פחד ממקומות סגורים", 3 words / 18 chars for a 14-char run). It carries
    // no terminator and sits within the word/length bounds, so it MUST be accepted and spliced by offset.
    [Fact]
    public async Task Repair_LegitShortMultiWordHebrewEquivalent_IsAccepted_SplicedByOffset()
    {
        const string value = "תיאור החדר האטום מעורר claustrophobia חונקת לגיבור.";
        const string legitPhrase = "פחד ממקומות סגורים"; // 3 words, 18 chars — a legitimate equivalent
        var router = KeyedRouter(new Dictionary<string, string> { ["claustrophobia"] = legitPhrase });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);
        Assert.Equal(1, result.Flagged);
        Assert.Equal(1, result.Repaired);   // legit multi-word phrase accepted, not over-rejected
        Assert.Equal(0, result.Reverted);
        Assert.Equal(value.Replace("claustrophobia", legitPhrase), result.Value);
        Assert.DoesNotContain("claustrophobia", result.Value);
        Assert.Contains(legitPhrase, result.Value);
    }

    // ─── (f) never-throws: a faulting router keeps the original + surfaces a Fault ─

    [Fact]
    public async Task Repair_RouterThrows_ReturnsOriginal_SurfacesFault_NeverThrowsOut()
    {
        const string value = "הדמות שקעה במצב של confusion מוחלט.";
        var router = new FakeAiRouter(_ => throw new InvalidOperationException("router boom"));

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);        // the call WAS made (counted before the throw)
        Assert.Equal(value, result.Value);        // original kept
        Assert.Equal(1, result.Reverted);
        Assert.NotNull(result.Fault);             // fault surfaced, not silently swallowed
    }

    // ─── (f2) per-span isolation: ONE span's router error must not abort the OTHER span (be-f04) ─
    //
    // RepairRunsCoreAsync (DynamicTermRepairService.cs ~297-309) catches a router exception PER SPAN,
    // reverts only that span, records the fault, and CONTINUES the foreach to the remaining runs. The
    // (f) test above proves the single-run case; this proves the multi-run isolation claim itself: with
    // TWO REPAIR runs in one value, one span's router call throws while the other succeeds, and BOTH are
    // still attempted. Parametrized over WHICH token throws so the assertions do not depend on the
    // per-span loop's right-to-left (descending Start) processing order — whichever run throws must keep
    // its original text, and the OTHER run's Hebrew replacement must be present, regardless of whether
    // the throwing span was processed first or second.
    [Theory]
    [InlineData("confusion", "panic", "בהלה")]
    [InlineData("panic", "confusion", "בלבול")]
    public async Task Repair_OneSpanRouterThrows_OtherSpanRepairedIndependently_FaultSurfaced_NeverThrowsOut(
        string throwingToken, string goodToken, string goodReplacement)
    {
        const string value = "הדמות חשה confusion אבל גם panic עמוק.";
        var router = ThrowingKeyedRouter(throwingToken, new Dictionary<string, string> { [goodToken] = goodReplacement });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(2, router.CallCount);   // both spans were attempted despite one throwing
        Assert.Equal(2, result.Flagged);
        Assert.Equal(1, result.Repaired);    // the good span was repaired
        Assert.True(result.Reverted >= 1);   // the throwing span kept its original text
        Assert.NotNull(result.Fault);        // the swallowed router exception is surfaced

        // Order-agnostic: the THROWING run's original text survives unchanged, and the OTHER run's Hebrew
        // replacement is present, whichever one physically throws.
        Assert.Contains(throwingToken, result.Value);
        Assert.Contains(goodReplacement, result.Value);
        Assert.DoesNotContain(goodToken, result.Value);
    }

    // ─── (g) zero-call gate: clean prose / all-LEAVE runs -> no model calls ──

    [Fact]
    public async Task Repair_CleanHebrewValue_MakesZeroModelCalls_ByteIdentical()
    {
        const string value = "הדמות שקעה במצב של בלבול מוחלט לאורך כל הפרק.";
        var router = NeverCalledRouter();

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(0, router.CallCount); // no foreign run => detector gates the call
        Assert.Equal(0, result.Flagged);
        Assert.Equal(value, result.Value);
    }

    [Fact]
    public async Task Repair_OnlyProperNounRun_ClassifierLeaves_MakesZeroModelCalls()
    {
        // "Jerusalem" is Title-Case mid-sentence => the classifier LEAVEs it, so RunsToRepair is empty and no
        // model call is made — proving the d2 gate short-circuits before d3.
        const string value = "הם נסעו אל Jerusalem בבוקר קר וצלול.";
        var router = NeverCalledRouter();

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(0, router.CallCount);
        Assert.Equal(0, result.Flagged);
        Assert.Equal(value, result.Value);
    }

    // ─── (h) Hebrew-in-English direction (ExpectedScript.Latin) ─────────────

    [Fact]
    public async Task Repair_HebrewLeakInEnglish_LatinExpected_SubstitutesToLatin()
    {
        const string value = "The hero felt a deep בלבול that cold morning.";
        var router = KeyedRouter(new Dictionary<string, string> { ["בלבול"] = "confusion" });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Latin, "en");

        Assert.Equal(1, router.CallCount);
        Assert.Equal(1, result.Repaired);
        Assert.Equal(value.Replace("בלבול", "confusion"), result.Value);
        Assert.DoesNotContain("בלבול", result.Value);
    }

    // ─── (i) shared extractor (be-f01): fenced / trailing-prose payloads ────

    [Fact]
    public async Task Repair_ModelReturnsJsonFencedPayload_SharedExtractor_SubstitutesCorrectly()
    {
        // A reasoning model wraps the object in a ```json fence. ExtractReplacement now delegates to
        // UnifiedAnalysisService.ExtractJson (the shared extractor also used by BookReviewService /
        // ChapterBriefService / BookIntelligenceService), which strips the fence before parsing.
        const string value = "הדמות שקעה במצב של confusion מוחלט.";
        var router = new FakeAiRouter(req =>
        {
            var token = MarkedToken(req);
            var repl = token == "confusion" ? "בלבול" : token;
            return $"```json\n{{\"replacement\":\"{repl}\"}}\n```";
        });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);
        Assert.Equal(1, result.Repaired);
        Assert.Equal(0, result.Reverted);
        Assert.Equal(value.Replace("confusion", "בלבול"), result.Value);
        Assert.DoesNotContain("confusion", result.Value);
    }

    [Fact]
    public async Task Repair_ModelReturnsTrailingProseWithStrayBrace_SharedExtractor_SubstitutesCorrectly()
    {
        // A stray '}' AFTER the JSON object in trailing prose defeats the old bespoke
        // IndexOf('{')/LastIndexOf('}') slice: it would grab the trailing prose too, producing
        // malformed JSON that JsonDocument.Parse rejects, so the run reverts to the original span. The
        // shared UnifiedAnalysisService.ExtractJson does balanced-brace matching, so it stops at the
        // object's OWN closing brace and ignores the stray one, and the replacement IS applied.
        const string value = "הדמות שקעה במצב של confusion מוחלט.";
        var router = new FakeAiRouter(req =>
        {
            var token = MarkedToken(req);
            var repl = token == "confusion" ? "בלבול" : token;
            return $"{{\"replacement\":\"{repl}\"}} note: done}}";
        });

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);
        Assert.Equal(1, result.Repaired);
        Assert.Equal(0, result.Reverted);
        Assert.Equal(value.Replace("confusion", "בלבול"), result.Value);
        Assert.DoesNotContain("confusion", result.Value);
    }

    [Fact]
    public void ExpectedScriptForLanguage_MapsHebrewToHebrew_ElseLatin()
    {
        Assert.Equal(ExpectedScript.Hebrew, DynamicTermRepairService.ExpectedScriptForLanguage("he-IL"));
        Assert.Equal(ExpectedScript.Hebrew, DynamicTermRepairService.ExpectedScriptForLanguage("he"));
        Assert.Equal(ExpectedScript.Latin, DynamicTermRepairService.ExpectedScriptForLanguage("en"));
        Assert.Equal(ExpectedScript.Latin, DynamicTermRepairService.ExpectedScriptForLanguage(null));
        Assert.Equal(ExpectedScript.Latin, DynamicTermRepairService.ExpectedScriptForLanguage(""));
    }

    // ─── RepairableField-list convenience (the d4 hook): prose repaired, structure untouched ─

    [Fact]
    public async Task RepairFields_LiteraryResult_RepairsFlaggedProse_LeavesCleanAndStructuralFields()
    {
        var result = new LiteraryAnalysisResult
        {
            Summary = "תקציר עם confusion שנשאר.",   // the ONLY field with a leak -> the ONLY flagged field
            Tone = "נוגה",                              // clean Hebrew -> guard skips, no model call
            ToneDescription = "טון מתוח לאורך הפרק",
            NarrativeVoice = "גוף שלישי",
            NarrativeVoiceDescription = "מספר יודע כול",
            MoodProgression = "עולה בהדרגה",
            Themes = { new ThemeEntry { Name = "כוח", Description = "מוטיב חוזר", Significance = "major" } },
            RhetoricalDevices = { new RhetoricalDevice { Name = "מטאפורה", Example = "האור שבר את החושך", Effect = "מדגיש תקווה" } },
        };

        var fields = RepairableFields.For(result);
        var router = KeyedRouter(new Dictionary<string, string> { ["confusion"] = "בלבול" });

        var agg = await NewService(router).RepairFieldsAsync(fields, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(1, router.CallCount);      // only the leaking Summary reached the model
        Assert.Equal(1, agg.FieldsChanged);
        Assert.Equal(1, agg.RunsRepaired);

        // Prose repaired in place through the setter...
        Assert.DoesNotContain("confusion", result.Summary);
        Assert.Contains("בלבול", result.Summary);
        // ...while clean prose and the structural enum are byte-identical.
        Assert.Equal("נוגה", result.Tone);
        Assert.Equal("major", result.Themes[0].Significance);
        Assert.Equal("מטאפורה", result.RhetoricalDevices[0].Name);
    }

    [Fact]
    public async Task RepairFields_CleanHebrew_MakesZeroModelCalls_NothingChanged()
    {
        var result = new LiteraryAnalysisResult
        {
            Summary = "הפרק בונה מתח הדרגתי ומגיע לשיא רגשי מרשים.",
            Tone = "נוגה",
        };

        var fields = RepairableFields.For(result);
        var router = NeverCalledRouter();

        var agg = await NewService(router).RepairFieldsAsync(fields, ExpectedScript.Hebrew, "he-IL");

        Assert.Equal(0, router.CallCount);
        Assert.Equal(0, agg.FieldsChanged);
        Assert.Equal(0, agg.RunsFlagged);
    }

    // ─── (j) whole-value backstop invariant (be-f03) ─────────────────────────
    //
    // REACHABILITY ANALYSIS: RepairRunsCoreAsync's final "WHOLE-VALUE BACKSTOP" (revert the ENTIRE
    // value when afterCount > beforeCount, DynamicTermRepairService.cs ~330-348) is DEFENSIVE-ONLY and
    // is NOT reachable through the public repair path against the REAL LatinInHebrewContentDetector +
    // IsAcceptableReplacement, for two composed reasons:
    //   1. IsAcceptableReplacement only ACCEPTS a replacement that is non-empty AND carries NO run
    //      (>=2 consecutive letters) of the foreign script (HasForeignRuns(trimmed, expected) must be
    //      false). So every SPLICED-IN replacement is, by construction, free of any 2+-letter foreign
    //      run of its own.
    //   2. DetectForeignRuns's maximal-run scan guarantees that for every run it returns, the
    //      character immediately BEFORE run.Start and immediately AFTER run.Start+run.Length (when in
    //      bounds) is NON-foreign in the ORIGINAL value — otherwise the scan would have swallowed that
    //      character into the SAME run instead of stopping. Those two boundary characters are never
    //      touched by any splice (each splice rewrites only its own run's span, and distinct runs are
    //      always separated by >=1 untouched character by the same maximal-run argument), so they stay
    //      non-foreign in the final "working" string too.
    //   Combining (1) and (2): even a single STRAY foreign letter at the very edge of an accepted
    //   replacement (which alone is not a "run" — the >=2 rule) can never combine across the untouched,
    //   guaranteed-non-foreign boundary character to FORM a new run. So splicing accepted replacements
    //   can only REMOVE a whole foreign run (the one it replaced) or leave the count unchanged (a
    //   REJECTED span keeps its original run); it can never INCREASE the foreign-run count. The
    //   afterCount > beforeCount check therefore guards an edge this analysis shows is unreachable via
    //   any accepted-replacement combination against the real detector — a fail-safe belt-and-braces for
    //   a future detector/validation change, not a path this test suite can drive today without mocking
    //   or weakening production code (which the task explicitly disallows). So instead of asserting the
    //   (unreachable) revert branch directly, this test pins the INVARIANT the backstop exists to guard:
    //   the foreign-run count of the repaired value is never GREATER than the input's, across MULTIPLE
    //   flagged runs in one value, and the repair is actually APPLIED (not whole-value-reverted).
    [Fact]
    public async Task Repair_MultipleLatinLeaksInHebrew_ForeignRunCountNeverIncreases_BackstopInvariantHolds()
    {
        const string value = "היה שם confusion ואחריו panic וגם anxiety גדול.";
        var router = KeyedRouter(new Dictionary<string, string>
        {
            ["confusion"] = "בלבול",
            ["panic"] = "בהלה",
            ["anxiety"] = "חרדה",
        });

        var beforeCount = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew).Count;
        Assert.Equal(3, beforeCount); // sanity: three distinct Latin runs precede repair

        var result = await NewService(router).RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL");

        var afterCount = LatinInHebrewContentDetector.DetectForeignRuns(result.Value, ExpectedScript.Hebrew).Count;

        // The monotonic-non-increase property the backstop guards.
        Assert.True(afterCount <= beforeCount,
            $"foreign-run count must never increase: before={beforeCount}, after={afterCount}");
        Assert.Equal(0, afterCount); // all three runs were repaired with foreign-free Hebrew replacements
        Assert.Equal(3, result.Repaired);
        Assert.True(result.Repaired > 0);
        // The repair was APPLIED, not whole-value-reverted (Value == input would mean the backstop fired).
        Assert.NotEqual(value, result.Value);
        Assert.Equal(
            value.Replace("confusion", "בלבול").Replace("panic", "בהלה").Replace("anxiety", "חרדה"),
            result.Value);
    }
}
