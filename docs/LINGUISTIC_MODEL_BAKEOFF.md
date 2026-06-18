# Linguistic Analysis - Model Bake-off

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
