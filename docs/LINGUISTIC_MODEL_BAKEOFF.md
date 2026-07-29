# Linguistic Analysis - Model Bake-off

> **[2026-07-29] THE GOLD WAS DESATURATED (p2-1). Everything below, INCLUDING the 2026-07-27 banner,
> is now a superseded-gold result.** `linguistic-gold.json` grew from 18 cases (5 clean / 13 planted)
> to **32 cases (11 clean / 21 planted)**. See "Gold desaturation 2026-07-29" near the bottom of this
> file for what was added, why, and the first measurements on the new set. No score measured before
> 2026-07-29 is comparable to a score measured after it.

> **[2026-07-27] THE SCORES BELOW ARE ON SUPERSEDED GOLDS - DO NOT COMPARE THEM TO CURRENT NUMBERS.**
> This document is not one gold size: the original round (2026-06-14) scored on a 10-case gold, and
> the gold grew to 11 cases on 2026-06-17 for the Gemma-4 round below - including the widely-cited
> local `gemma4:12b` 0.750 vs cloud `google/gemma-4-31b-it` 0.900 comparison, which is entirely an
> 11-case-gold result (both numbers). `linguistic-gold.json` has since grown to **18 cases** (5 clean,
> 13 planted) and the prompt has changed, so every composite in this document has a different
> denominator and a different prompt behind it than anything measured today.
>
> Re-measured on the CURRENT 18-case gold: local **`gemma4:12b` = composite 0.900, recall 100%,
> type-accuracy 100%, 0 clean false positives**, reproduced identically across 3 runs. That is the
> same headline number this document records for cloud `google/gemma-4-31b-it`, but the two were
> measured on DIFFERENT gold sets and are **not** comparable.
>
> Consequences: (1) the "local 0.750 vs cloud 0.900" gap this document is most often cited for
> **no longer has a basis in measured data**; (2) cloud has NOT been run on the current gold, so it
> is an unmeasured candidate, not a known upgrade; (3) the current gold is **saturated** for strong
> models (100/100/0 leaves no headroom), so it cannot discriminate at the top - harder cases are
> needed before it can justify any model or tier change. Treat the tables below as history.

The **Linguistic** analysis type emits `consistencyIssues`: cross-paragraph register/tense/POV
shifts. On the old prompt running on local **qwen3.5:9b**, these were false-positive grammar
nitpicks, all mislabeled `register`. Plan 4 tightened the prompt (cross-paragraph shifts only,
precise issue type, prefer an empty array) and ran this objective bake-off, scoring each model on a
10-case gold set.

## Run metadata

- **Date:** 2026-06-14 (initial round); updated 2026-06-17 (Gemma 4 round - see section below).
- **Gold set:** 10 cases at initial run (4 clean, 6 planted - one register/tense/POV shift each, in
  Hebrew and English); grew to **11 cases** on 2026-06-17 (see Update section). Source:
  `Pagedraft.Api.Tests/TestData/linguistic-gold.json`.
- **Harness:** `Pagedraft.Api.Tests/LanguageEngine/LinguisticQualityTests.cs`
  (`LinguisticQuality_ModelBakeoff_ReportTable`), driving the real `PromptFactory` structured prompt
  and the `IAiRouter` path with `JsonMode=true`, exactly as `UnifiedAnalysisService` does in
  production.
- **Local provider:** Ollama on an RTX 4070 laptop (~8 GB VRAM).
- **Cloud provider:** OpenRouter (generic `OpenAiCompatibleProvider`, `response_format=json_object`).

> **CAVEAT - read first.** The 6 Hebrew planted entries are AI-authored DRAFTS pending final
> native-speaker validation, and N is small (each planted case is roughly 17% of recall at 10 cases,
> ~14% at 11 cases), so treat exact rankings as indicative, not definitive. The robust conclusion is
> the **tier separation** between model classes (cloud instruct/gemma >> local) plus Gemma's
> speed+quality combination, not 1-case differences between adjacent models.

## Scoring formula

```
cleanFalsePositives = total consistencyIssues returned across all expectClean cases (lower better).
plantedRecall  = (planted cases with >=1 issue returned) / (planted cases).
typeAccuracy   = (planted cases where some returned issue type is in the expected set) / (planted cases).
composite = 0.45*plantedRecall + 0.45*typeAccuracy - 0.10*cleanFalsePositiveRate, clamped to >= 0,
where cleanFalsePositiveRate = cleanFalsePositives / max(1, cleanCases), clamped to [0,1].
(Informational only; the test never fails on model quality.)
```

## Local results (Ollama, RTX 4070 laptop ~8 GB VRAM)

> **Updated leaderboard (2026-06-17):** see "Local Gemma 4 on consumer GPU" subsection below. After
> adding `gemma4:12b`, the local ranking is: gemma4:12b (0.750) > qwen3.5:4b (0.525) > DictaLM-12B
> (0.500) > qwen3.5:9b (0.300). Table below is the original 10-case round.

| Model | clean-FP | recall | type-acc | composite | Notes |
|---|---|---|---|---|---|
| **qwen3.5:4b** | 0 | 67% | 50% | **0.525** | Best local (original round); runtime ~1m19s |
| hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest | 4 | 67% | 67% | 0.500 | Runtime ~7m29s; the 4 false positives were partly on the DRAFT Hebrew clean cases |
| qwen3.5:9b (current production default) | 0 | 33% | 33% | 0.300 | Runtime ~2m47s |

## Cloud results (OpenRouter, `response_format=json_object`)

| Model | clean-FP | recall | type-acc | composite | Notes |
|---|---|---|---|---|---|
| **qwen/qwen3.7-plus** | 0 | 100% | 100% | **0.900** | Reasoning model; slow (~1 min/case); ~1000 completion tokens/call |
| deepseek/deepseek-chat | 0 | 83% | 83% | 0.750 | Fast; ~100 completion tokens/call |
| google/gemini-2.5-flash | 0 | 83% | 83% | 0.750 | Fast; ~150 tokens/call |
| qwen/qwen3.5-122b-a10b | 0 | 83% | 83% | 0.750 | Light reasoning; ~2300 tokens/call - close to the 2048 harness cap |
| meta-llama/llama-3.3-70b-instruct | 1 | 67% | 67% | 0.575 | Fast |
| qwen/qwen3.5-plus-20260420 | 0 | 50% | 50% | 0.450 | Light reasoning; ~1900 tokens/call |
| qwen/qwen3-235b-a22b-2507 | 0 | 33% | 33% | 0.300 | Instruct, conservative; very few tokens |
| qwen/qwen3.5-9b | EXCLUDED | - | - | - | See note below |

**Excluded - qwen/qwen3.5-9b:** this is a reasoning/thinking model. It spends the entire token
budget on hidden reasoning and returns EMPTY content (`finish_reason=length`) under the strict-JSON
path, so it scores 0/0/0 spuriously. This also explains why the local-vs-cloud same-weights sanity
check fails: OpenRouter serves qwen3.5-9b in thinking mode while local Ollama runs it non-thinking.

## Analysis

1. **The tightened prompt alone fixed the original false-positive problem.** Every top model - and
   even local qwen3.5:9b - now returns 0 false positives on clean cases. The Plan 4 motivating bug
   (grammar nitpicks mislabeled `register`) is resolved by the prompt, not by the model.
2. **The remaining differentiator is recall plus type-accuracy on genuine planted shifts.** Cloud
   instruct models roughly DOUBLE local recall.
3. **Best overall quality: qwen/qwen3.7-plus** (composite 0.900, perfect recall and type). But it is
   a reasoning model: high latency (~1 min/case) and higher token cost, and it needs an adequate
   `max_tokens` budget. Production `ProviderSettings:OpenRouter:MaxTokens` is 5120, comfortably above
   its ~1000-token usage; the test harness default of 2048 is what truncated qwen3.5-9b thinking.
4. **Best quality-per-cost-per-latency: deepseek/deepseek-chat and google/gemini-2.5-flash** -
   composite 0.750, 0 false positives, ~100-150 tokens/call (fast, cheap, no truncation risk).
5. **Best local model: qwen3.5:4b** (0.525). Notably it BEATS the current production default
   qwen3.5:9b (0.300) on this set. DictaLM-12B (0.500) is close but slow and noisier on clean text.
   qwen3.5:4b is useful as an offline fallback.
6. **Thinking-model trap.** Any "thinking" variant (for example qwen3.5-9b on OpenRouter) is
   unsuitable for the strict-JSON path unless reasoning is disabled or given a much larger token
   budget. Prefer INSTRUCT variants.

## Update 2026-06-17 - Gemma 4 round (11-case gold)

### Gold set change

`linguistic-gold.json` grew from 10 to **11 cases** (5 clean + 6 planted). The new case is
`clean-he-04`: a real-world Hebrew passage from `src/docs/test-text.txt` - a consistent first-person
past-tense battle scene with dialogue. It is a deliberate **false-positive trap**: the local
`qwen3.5:4b` model wrongly flagged a dialogue line as a "register" shift and emitted a
within-sentence tense nitpick mislabeled as "pov". Every evaluated cloud model correctly returned an
empty `consistencyIssues` array on this case.

### New model: google/gemma-4-31b-it

`google/gemma-4-31b-it` was added and scored on the full 11-case gold set via the same harness
(OpenRouter, `response_format=json_object`).

| Model | clean-FP | recall | type-acc | composite | Notes |
|---|---|---|---|---|---|
| **google/gemma-4-31b-it** | 0 | 100% | 100% | **0.900** | 0 errors; ~1m33s for 11 cases (~8s/case); fast AND top quality |
| deepseek/deepseek-chat | 0 | 83% | 83% | 0.750 | recall/type from the 10-case round (unchanged by the added clean case); 11-case re-run on 2026-06-17 could not complete due to OpenRouter rate-limiting (transient, not a quality result) |
| qwen/qwen3.7-plus | 0 | 100% | 100% | 0.900 | from the 10-case round; quality-equal to Gemma but a slow reasoning model |

### Single-text spot check (test-text.txt, no chapter baseline)

All three cloud models returned 0 consistency issues on the raw `test-text.txt` passage (correct).
Grammaticality / latency / completion-tokens:

- `deepseek/deepseek-chat`: grammaticality 0.95 / 27s / 242 tok
- `google/gemma-4-31b-it`: grammaticality 0.98 / 6s / 259 tok
- `qwen/qwen3.7-plus`: grammaticality 0.92 / 299s / 9728 tok

`qwen3.7-plus` spent 9728 tokens over roughly 5 minutes to reach the same answer as the other two.
That is too slow and costly for production use - it is a quality twin, not a latency-competitive
alternative.

The local `qwen3.5:4b` produced **2 false positives** on the same text.

### Note on google/gemma-4-31b-it:free

A `google/gemma-4-31b-it:free` variant exists on OpenRouter. The free tier is heavily
rate-limited (HTTP 429) and is **not suitable for production**. Only the paid model id was scored
above.

### Local Gemma 4 on consumer GPU

**Ollama upgrade:** gemma4 architecture requires Ollama >= 0.30. Ollama was updated from v0.24.0 to
v0.30.9 before pulling the model.

**Model pulled:** `gemma4:12b` (dense 12B, ~7.6 GB). Fits on an RTX 4070 laptop (~8 GB VRAM) with
partial CPU offload (VRAM fills, remainder spills to RAM).

**Result on the same 11-case gold set (provider=Ollama):**

| Model | clean-FP | recall | type-acc | composite | Notes |
|---|---|---|---|---|---|
| **gemma4:12b** | 0 | 83% | 83% | **0.750** | 0 errors; ~5m8s for 11 cases (~28s/case; partial VRAM offload) |

**Updated local leaderboard** (11-case gold where re-run; prior 10-case recall/type otherwise - adding
the clean case does not affect those scores):

| Model | clean-FP | recall | type-acc | composite |
|---|---|---|---|---|
| **gemma4:12b** | 0 | 83% | 83% | **0.750** |
| qwen3.5:4b | 0 | 67% | 50% | 0.525 |
| DictaLM-12B | 4 | 67% | 67% | 0.500 |
| qwen3.5:9b | 0 | 33% | 33% | 0.300 |

**Key takeaway:** local `gemma4:12b` (composite 0.750) equals cloud `deepseek-chat` (0.750) and
provides a viable **free / offline / private** path. Trade-offs vs the cloud options:

- vs cloud `google/gemma-4-31b-it` (0.900): quality gap of 0.150, and ~28s/case locally vs ~8s/case
  cloud. The cloud model is the clear production pick when connectivity and cost permit.
- vs cloud `deepseek/deepseek-chat` (0.750): equal composite score but ~28s/case locally vs
  negligible cloud latency. Choose local when offline, privacy-sensitive, or zero cloud cost is
  required.

`appsettings.json` active wired value has been set to `Ollama/gemma4:12b` for free/offline/private
use. The cloud max-quality target remains `OpenRouter/google/gemma-4-31b-it` (0.900).

## Recommendation

- **Primary production pick** to wire as `Ai:FeatureModels:LinguisticAnalysis`:
  **OpenRouter / google/gemma-4-31b-it** - matches the best quality on the gold set (composite
  0.900, perfect recall and type-accuracy, 0 false positives including the hard real-world clean
  passage `clean-he-04`) AND is fast (~8s/case). This supersedes `deepseek/deepseek-chat` as the
  primary. `appsettings.json` has been updated: the `_comment_LinguisticAnalysis` breadcrumb names
  `google/gemma-4-31b-it` as the cloud max-quality target; the **active wired value is
  `Ollama/gemma4:12b`** for free/offline/private use (requires Ollama >= 0.30).
- **Fallback alternative:** **OpenRouter / deepseek/deepseek-chat** (composite 0.750, 0 false
  positives, fast and cheap) - use when Gemma is unavailable or cost sensitivity is paramount.
- **Slow quality-twin:** **OpenRouter / qwen/qwen3.7-plus** (0.900) - same gold-set score as Gemma
  but ~5 min/case and ~9700 tokens/call. Only prefer it if a reasoning model is specifically
  required.
- **Local fallback:** **Ollama / gemma4:12b** (composite 0.750 - best local model by a wide margin,
  equals cloud deepseek-chat). Requires Ollama >= 0.30 and ~7.6 GB VRAM (RTX 4070 laptop partially
  offloads to CPU; functional but ~28s/case vs ~8s/case cloud). Supersedes the previous local
  recommendation of qwen3.5:4b (0.525).
- **Gold set caveat:** the set is 11 cases, so exact composite rankings are indicative. The robust
  conclusions are: (a) the tier separation (cloud instruct/gemma >> local, though local gemma4:12b
  now closes the gap significantly vs the 0.750 cloud tier); (b) Gemma's combination of top recall,
  0 false positives, and fast latency makes it the clear production choice.
- **Secrets and cost note:** the OpenRouter key lives in env or user-secrets only
  (env `AI_OPENROUTER_APIKEY`), never committed. Weigh the cloud cost/latency trade against the
  local fallback when wiring the default. Do NOT use the `:free` Gemma variant in production.

## Gold desaturation 2026-07-29 (plan todo p2-1)

### Why

The 18-case gold (5 clean / 13 planted) was **saturated**. Local `gemma4:12b` scored composite
**0.900**, which is the formula's ceiling (`0.45*1 + 0.45*1 - 0.10*0`), with recall 100%,
type-accuracy 100% and 0 clean false positives, reproduced identically across 3 runs on 2026-07-27.
A gold at its ceiling cannot show a better model winning: a strictly superior model would tie. That
made every "model X is better" claim on this gold unfalsifiable, which is why desaturating it was a
hard prerequisite for the fast/thinking tier decision rather than polish.

### What was added

**14 new cases (7 English, 7 Hebrew), taking the gold to 32 cases (11 clean / 21 planted).** None of
them was tuned against any model's output: they were authored for linguistic difficulty, then
measured once. Three families, plus a fourth kind aimed at type-accuracy:

| family | ids | what makes it hard |
|---|---|---|
| Subtler planted shift | `planted-pov-freeindirect-{en,he}` | the head-hop is free indirect discourse with NO thought-verb ("felt"/"thought"/"wondered"), so the prompt's paragraph-annotation step has no lexical cue to key on |
| Subtler planted shift | `planted-register-mild-{en,he}` | the intruding register is colloquial narration sharing the passage's everyday vocabulary, instead of the bureaucratic boilerplate the older cases use |
| Shift buried in a longer passage | `planted-tense-buried-{en,he}` | a short present-tense paragraph at position 4 of 6, past narration on both sides, written to read like an intentional lyric interlude |
| Type trap | `planted-register-typetrap-{en,he}` | a genuine register shift wrapped in a POV decoy (a second character with no interiority) and a tense decoy (a gnomic-present aphorism); answering "pov" or "tense" is a recall hit and a type MISS |
| Near-miss clean FP trap | `clean-trap-flashback-en` | past perfect + habitual "would" inside simple past |
| Near-miss clean FP trap | `clean-trap-gnomic-he` | proverbial present inside past narration, twice |
| Near-miss clean FP trap | `clean-trap-external-pov-{en,he}` | a second character owns most of two paragraphs but is given no interiority at all - per the prompt that is `povHolder: none` and must NOT be a pov issue |
| Near-miss clean FP trap | `clean-trap-dialogue-register-{en,he}` | heavy officialese confined to spoken lines, which the prompt explicitly exempts |

### Hebrew validation state - machine-readable now

Every Hebrew entry in the gold is an **AI-authored draft pending native-speaker review**. That was
prose-only before (the file's `_README` plus each entry's `notes`), so a new Hebrew case could be
added without the caveat and be indistinguishable from a validated one. Each entry whose `language`
starts with `he` now carries an explicit field:

```jsonc
"hebrewValidationStatus": "ai-authored-draft-pending-native-review"  // 17 of 18 Hebrew cases
"hebrewValidationStatus": "user-validated-2026-06-17"                // clean-he-04 only
```

`clean-he-04` is the single Hebrew case a native speaker has actually signed off (it is a real
passage from `src/docs/test-text.txt`). English entries carry no such field. The convention is
enforced by `Pagedraft.Api.Tests/LanguageEngine/LinguisticGoldSchemaTests.cs`, which fails if a
Hebrew case omits the field, if a draft's `notes` lose the human-readable banner, if an English case
carries the field, or if the `_README` pending-validation id list drifts from the field values.
**This remains a standing GA gate: do not present a Hebrew score on this gold as validated.**

### First measurement on the desaturated gold (local, n=3 per model)

Harness `LinguisticQuality_ModelBakeoff_ReportTable`, provider Ollama, RTX 4070 laptop (~8 GB VRAM),
production tuning (temp 0.2, num_ctx 16384, num_predict 5120, repeat_penalty 1.2). ~34 min per run
for both models over 32 cases. 0 errors and 0 timeouts in all six model-runs.

| model | metric | min | median | max |
|---|---|---|---|---|
| **gemma4:12b** | composite | 0.689 | **0.732** | 0.732 |
| | planted recall | 81% (17/21) | 86% (18/21) | 86% (18/21) |
| | type accuracy | 76% (16/21) | 81% (17/21) | 81% (17/21) |
| | clean false positives | 2 | 2 | 2 |
| **qwen3.5:9b** | composite | 0.306 | **0.353** | 0.435 |
| | planted recall | 48% (10/21) | 48% (10/21) | 57% (12/21) |
| | type accuracy | 29% (6/21) | 43% (9/21) | 48% (10/21) |
| | clean false positives | 4 | 4 | 6 |

**The gold now discriminates.** `gemma4:12b` fell from the 0.900 ceiling to a 0.689-0.732 band, so
there is roughly 0.17-0.21 of headroom for a better model to win into. The two models' composite
ranges are **completely disjoint** - gemma4's worst run (0.689) beats qwen's best (0.435) by 0.254,
against a within-model spread of 0.043 (gemma4) and 0.129 (qwen). Separation therefore exceeds the
larger within-model spread by about 2x.

**All of the discrimination comes from the new cases.** `gemma4:12b` still scored a recall AND type
hit on every one of the 13 pre-existing planted cases in all 3 runs, and 0 false positives on all 5
pre-existing clean cases. Its entire residual gap is five new cases:

| case | gemma4:12b outcome | stable? |
|---|---|---|
| `planted-pov-freeindirect-en` | MISSED - returned `[]` | 3/3 runs |
| `planted-register-typetrap-en` | MISSED - returned `[]` | 3/3 runs |
| `planted-register-mild-he` | MISSED - returned `[]` | 3/3 runs |
| `planted-pov-freeindirect-he` | WRONG TYPE - detected, labelled `tense` instead of `pov` | 3/3 runs |
| `planted-register-typetrap-he` | MISSED in run 1, hit in runs 2 and 3 | the only unstable case |
| `clean-trap-dialogue-register-en` | 2 false positives, both labelled `tense` | 3/3 runs |

That last row is a **prompt finding, not a gold error**, and it is actionable independently of any
model choice: the shipped prompt exempts dialogue from *register* reporting ("Dialogue is naturally
written in a more colloquial, simpler register than narration") but says nothing equivalent for
*tense*. `gemma4:12b` reproducibly quotes the clerk's present-tense spoken lines and reports them as
a narration tense shift. The Hebrew mirror `clean-trap-dialogue-register-he` did not fire for gemma4
(it did for qwen3.5:9b, twice), so the gap is currently English-visible.

**Caveats.** (a) The Hebrew half of every new case is an AI-authored draft, so any Hebrew-specific
reading above is provisional. (b) `qwen3.5:9b` is markedly less stable than `gemma4:12b` on this
gold (composite spread 0.129 vs 0.043, and it emits invented type strings such as
`style_register_shift` and `grammar_error_in_span` that score as type misses) - run-to-run stability
is a selection criterion in its own right. (c) No cloud model has been measured on this gold; that
is plan todo p2-2. **(c) IS NOW ANSWERED - see "Cloud measured on the 32-case gold" below.**

### Reproducing

```powershell
$env:LINGUISTIC_BAKEOFF_PROVIDER = "Ollama"
$env:LINGUISTIC_BAKEOFF_MODELS   = "gemma4:12b,qwen3.5:9b"
$env:LINGUISTIC_BAKEOFF_PER_CASE = "1"   # per-case table on top of the aggregate row (added 2026-07-29)
dotnet test Pagedraft.Api.Tests/Pagedraft.Api.Tests.csproj --no-build -c Debug `
  --filter "FullyQualifiedName=Pagedraft.Api.Tests.LanguageEngine.LinguisticQualityTests.LinguisticQuality_ModelBakeoff_ReportTable" `
  --logger "console;verbosity=detailed"
```

Use that exact fully-qualified filter. A broad `~Linguistic` / `~Proofread` / `~BookReview` filter
sweeps the other live-GPU harnesses (40+ min) - several of those classes carry no `Category` trait,
so excluding `LiveDiagnostic` / `LiveModel` is not sufficient.

## Cloud measured on the 32-case gold 2026-07-29 (plan todo p2-2)

**This supersedes the 2026-07-27 banner's claim that "cloud has NOT been run on the current gold".**
It has now, on the 32-case desaturated gold, at n=3, under the production prompt and tuning.

| model | metric | min | median | max | spread |
|---|---|---|---|---|---|
| OpenRouter `google/gemma-4-31b-it` | composite | 0.857 | **0.900** | 0.900 | 0.043 |
| | planted recall | 95% (20/21) | 100% (21/21) | 100% (21/21) | 5 pts |
| | type accuracy | 95% (20/21) | 100% (21/21) | 100% (21/21) | 5 pts |
| | clean false positives | 0 | 0 | 0 | 0 |
| Ollama `gemma4:12b` (shipped) | composite | 0.689 | **0.732** | 0.732 | 0.043 |
| | planted recall | 81% | 86% | 86% | 5 pts |
| | type accuracy | 76% | 81% | 81% | 5 pts |
| | clean false positives | 2 | 2 | 2 | 0 |

Per-run cloud composites 0.857 / 0.900 / 0.900; 0 errors, 0 timeouts, 32/32 cases every run.
Realized cost **$0.0396** for all three runs ($0.01357 / $0.01261 / $0.01344 from the OpenRouter
credits ledger). Wall clock 10m11s / 6m55s / 8m19s.

**Result: cloud wins, ranges disjoint.** Cloud's worst run (0.857) beats local's best (0.732) by
0.125, against a 0.043 within-model spread on both sides (~2.9x). Recall +9 pts, type accuracy
+14 pts, clean false positives 2 -> 0, all at the worst-case comparison.

**Two caveats that must travel with this table.**

1. **0.900 is the composite formula's CEILING**, and cloud hit it in 2 of 3 runs. So this gold is
   now saturated for this model: the measured margin is a LOWER bound and the gold cannot rank
   anything at or above `gemma-4-31b-it`. Harder cases are needed before the next tier comparison.
2. **~14% of the margin is a PROMPT artifact, not the model.** `clean-trap-dialogue-register-en`
   costs local `gemma4:12b` 2 false positives in 3/3 runs (both labelled `tense`) because the prompt
   exempts dialogue from *register* reporting but says nothing equivalent for *tense*. Cloud returns
   0 issues there in 3/3 runs (and 0 on the Hebrew twin), i.e. it infers the missing rule. That
   accounts for ~0.018 of the 0.125 gap. Fixing the prompt would narrow the gap slightly; it would
   not close it or change its direction.

Cloud's only miss across 63 planted-case evaluations was `planted-register-typetrap-en` in run 1
(returned `[]`); runs 2-3 labelled it correctly. Type accuracy never diverged from recall on any
cloud run - whenever cloud detected an issue, it labelled it correctly.

**HARNESS FIX REQUIRED BEFORE THIS COULD BE MEASURED FAIRLY.** `LinguisticQualityTests.CreateRouter`
wired only `Ollama_LinguisticAnalysis` (NumPredict 5120), so a non-Ollama sweep resolved no tuning
entry and fell through to the `ProviderTuningOptions` class default `MaxTokens` **2048** - a 2.5x
smaller output budget than the local rows it is compared against, which would truncate the JSON and
score as a miss. `CreateRouter` now mirrors the same budget onto `{Provider}_LinguisticAnalysis`
using the cloud family's own knob, matching what appsettings ships
(`OpenRouter_LinguisticAnalysis` = `{ Temperature 0.2, MaxTokens 5120, NumCtx 16384 }`). The Ollama
path is byte-identical to before, so the local baselines above remain comparable.

**The Hebrew half is still an AI-authored draft** pending native-speaker validation (18 of the 32
cases). Do not present a Hebrew score on this gold as validated.

### Reproducing the cloud run

```powershell
$env:LINGUISTIC_BAKEOFF_PROVIDER  = "OpenRouter"
$env:LINGUISTIC_BAKEOFF_MODELS    = "google/gemma-4-31b-it"
$env:LINGUISTIC_BAKEOFF_PER_CASE  = "1"
# requires AI_OPENROUTER_APIKEY; the sweep is skip-gated on it
dotnet test Pagedraft.Api.Tests/Pagedraft.Api.Tests.csproj --no-build -c Debug `
  --filter "FullyQualifiedName=Pagedraft.Api.Tests.LanguageEngine.LinguisticQualityTests.LinguisticQuality_ModelBakeoff_ReportTable" `
  --logger "console;verbosity=detailed"
```
