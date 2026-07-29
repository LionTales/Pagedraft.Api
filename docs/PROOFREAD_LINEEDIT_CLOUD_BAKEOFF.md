# Proofread + LineEdit — Cloud Model Bake-off (big-model / future-hosting ceiling)

Cost-efficient cloud evaluation of the **Proofread (הגהה)** and **LineEdit** tasks, mirroring the cloud
path the Linguistic harness already has (`docs/LINGUISTIC_MODEL_BAKEOFF.md`). The cloud model here is a
**proxy for the bigger models we cannot run on the 8 GB dev laptop but could host on a future GPU
server** for customers — i.e. it measures the quality ceiling beyond the local hardware budget, not a
model intended to replace the local-first default today.

> **[2026-07-29] RESOLVED - THE LOCAL HEBREW PROOFREAD MODEL IS NOW `gemma4:12b`, NOT `DictaLM-3.0`.**
> The 2026-07-27 banner (kept below for history) left the question OPEN pending an n>=3 re-run. That
> re-run is done at **n=5 per candidate**, all 93 gold cases, 0 errors, nothing capped, under
> production-faithful tuning (`Ollama_Proofread` NumPredict 4096). Results (min/median/max):
>
> | model | F0.5 | precision | recall | fp-rate | overreach |
> |---|---|---|---|---|---|
> | **gemma4:12b** | 0.332 / **0.353** / 0.387 | 30 / **32** / 35% | 65 / **65** / 65% | 15 / **18** / 22% | 0/4 all 5 |
> | DictaLM-3.0 | 0.261 / **0.287** / 0.337 | 23 / **25** / 29% | 70 / **70** / 85% | 31 / **32** / 33% | 0/4 all 5 |
>
> Precision and false-positive rate are FULLY DISJOINT in `gemma4:12b`'s favour; recall is FULLY
> DISJOINT in Dicta's favour. F0.5 - the pre-registered precision-weighted ranking metric - favours
> `gemma4:12b` 24/25 pairwise. Stability also favours it: recall spread 0 vs Dicta's 15 points.
> **VERDICT: SWITCHED to `gemma4:12b` for Hebrew Proofread.** Honest limits, the recall regression and
> the chunking caveat are recorded in `Ai:FeatureModels:_comment_ProofreadModel`
> (`Pagedraft.Api/appsettings.json`) and in the `## p2 measurement results` -> `p2-3` section of
> `src/.cursor/plans/_todo/model-tier-fast-thinking-2026-07-28.plan.md`. **LineEdit still routes to
> DictaLM-3.0 and is NOT covered by this gold** - it needs its own bake-off.

> **[2026-07-27, SUPERSEDED BY THE 2026-07-29 BANNER ABOVE] STABILITY EVIDENCE SINCE THIS BAKE-OFF.**
> Two identical full-gold runs (93 cases, same prompt/settings/
> machine, production temp 0.2, no seed, 2026-07-27) gave local `gemma4:12b` a STABLE F0.5 (0.353 ->
> 0.361, recall 65% both) but the then-wired local `DictaLM-3.0` a WIDE swing (F0.5 0.262 -> 0.360,
> recall 65% -> 85%, precision 23% -> 31%). Run 1 alone reads "gemma4 beats Dicta by 35% on F0.5"; run
> 2 reads a dead tie with Dicta 20 points BETTER on recall. Full methodology warning:
> `Ai:FeatureModels:_comment_ProofreadModel` in `Pagedraft.Api/appsettings.json`.
>
> VERDICT AT THE TIME: KEEP DictaLM-3.0 for Hebrew Proofread pending an n>=3 re-run of both candidates.
> That re-run has now happened and reversed this - see the 2026-07-29 banner above.

## Run metadata

- **Date:** 2026-06-20.
- **Branch:** `api-proofread-lineedit-cloud-eval` (off master, builds on the unmerged
  `api-proofread-lineedit-model-quality` substrate: Dicta-3.0 switch, 90-case gold with overreach
  scoring, tightened ProofreadHe/En prompt). Review-ready, **not committed/merged**.
- **Cloud provider:** OpenRouter (generic `OpenAiCompatibleProvider`).
- **Cloud model:** `google/gemma-4-31b-it` — the Linguistic cloud winner; a ~31B model that does NOT
  fit 8 GB VRAM (heavy CPU offload locally), so it is only practical via cloud / a GPU server.
- **Proofread harness:** `Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs`
  (`ProofreadQuality_ModelBakeoff_ReportTable`), now with OpenRouter support
  (`PROOFREAD_BAKEOFF_PROVIDER=OpenRouter`, gated on `AI_OPENROUTER_APIKEY`) and a cost-control subset
  (`PROOFREAD_BAKEOFF_CASE_IDS` / `PROOFREAD_BAKEOFF_MAX_CASES`).
- **LineEdit harness:** `Pagedraft.Api.Tests/LanguageEngine/LineEditCloudSpotCheckTests.cs` — a
  qualitative rubric spot-check (no scored gold; building one stays deferred).
- **Cost discipline:** single run, 1 cloud model, a 13-case representative subset (NOT all 90), plus a
  3-passage LineEdit spot-check. **Total spend ≈ $0.0016** (Proofread $0.0010 + LineEdit ~$0.0006).
- **Secrets:** OpenRouter key lives in env `AI_OPENROUTER_APIKEY` only, never committed.

## Scoring formula (Proofread)

```
precision    = matchedCorrections / producedCorrections
recall       = matchedCorrections / expectedCorrections
fp-rate      = no-change cases that got >=1 correction / no-change cases
overreach    = forbidden-edit cases tripped / cases that declare a forbidden edit  (PRECISION GATE)
```

`overreach` is the precision gate: it counts a meaning-changing rewrite of the *right* word (e.g.
`עתון`→`עתונות` "the press" instead of the ktiv fix `עיתון`). Forbidden edits are pulled OUT before
expected-match, so a model that overreaches cannot have that edit silently credited as a correct fix.

## Local results (already recorded — full 90-case gold, RTX 4070 laptop ~8 GB VRAM, 2026-06-20)

Local numbers are NOT re-run here (per the no-local-rerun guidance). From the precision-gated bake-off
on the full 90-case `proofread-gold.json` (short `GetPrompt(Proofread)` prompt — see the harness-fidelity
note below):

| Model | precision | recall | fp-rate | overreach | Notes |
|---|---|---|---|---|---|
| qwen3.5:9b (prior live-failure model) | 13% | 75% | 70% | overreached | WORST; the live ktiv smoke test caught it meaning-changing (עצמה→עוצמת רגשות, עתון→עתונות) |
| gemma4:12b | 24% | 70% | 33% | — | |
| **DictaLM-3.0-Nemotron-12B (current local default)** | **26%** | **75%** | **33%** | 0 on forbidden cases (post prompt-tighten) | WINNER of the local set; fastest; wired as `Ai:FeatureModels:Proofread` |

> Post switch+tighten single-model verify (full gold, local Dicta-3.0): fp-rate 70%→29%, recall held
> 75%, 0 overreach on the forbidden-edit cases.

## Cloud bake-off findings (NEW — 2026-06-20)

Cloud run on a **13-case representative subset** (the 2 overreach-guarded cases + 1 clean-overreach +
5 real-error `inj-*` + 5 clean controls incl. the `עצמה` homograph trap `clean-ms-24`). Selected via
`PROOFREAD_BAKEOFF_CASE_IDS`. Composition: 6 no-change, 2 overreach-guarded, 7 with expected corrections.

| Model | precision | recall | fp-rate | overreach | latency | tokens (in/out) | $cost |
|---|---|---|---|---|---|---|---|
| **OpenRouter / google/gemma-4-31b-it** | **88%** | **100%** | **17%** | **0/2** | ~2–3 s/case | 4582 / 188 | **$0.0010** |

**Read.** On the subset the cloud big model is decisively stronger than every local model: precision
**88%** (vs local best 26%), recall **100%**, and — critically for the gate — **overreach 0/2**: it made
*neither* forbidden meaning-changing edit. Because the scorer removes forbidden edits before matching,
the perfect recall confirms both overreach cases (`עצמה`→`עוצמה`, `עתון`→`עיתון`) were fixed with the
*correct* ktiv edit, not a meaning change. fp-rate 17% = 1 of 6 no-change cases got a single correction.

**Methodology caveats (read before trusting exact deltas):**
1. **Different denominators.** Cloud is on 13 curated cases; local is on the full 90. So treat the
   precision/fp magnitudes as *indicative*, not a like-for-like delta. The subset deliberately
   over-weights the overreach/precision cases (the gate) — and the **overreach denominator is 2 for
   both** (the same 2 forbidden cases), so `overreach 0/2` IS directly comparable, and it is the
   metric the gate turns on.
2. **Harness-fidelity (relative, not absolute).** Both local and cloud Proofread scores reflect the
   SHORT `GetPrompt(Proofread)` prompt only (the scorer sends an empty `Instruction`); production
   concatenates the long `ProofreadHe`/`En` + short. So these are a faithful *relative* comparison
   across models on the same surface, not the exact production prompt. (Linguistic does not have this
   gap.) See the bake-off-prompt-fidelity note.

## LineEdit rubric spot-check (NEW — 2026-06-20, qualitative)

No scored gold (deferred). Ran the production LineEditHe prompt (sent verbatim via the router, as
`UnifiedAnalysisService` does for LineEdit) over 3 short clean Hebrew passages reused from
`linguistic-gold.json` (`clean-he-01/02/03`). Rubric: overreach / preserve-meaning / valid-Hebrew /
respect-voice. ~6 s/passage, ~$0.0006 total.

| Passage | Suggestion | Category | Rubric |
|---|---|---|---|
| clean-he-01 | `מסובכים` → `סבוכים` (complicated → intricate) | word-choice | meaning preserved, valid Hebrew, literary voice kept |
| clean-he-02 | `כמו כל ערב אחר` → `ככל ערב` (tighten) | redundancy | meaning preserved, natural Hebrew |
| clean-he-03 | `בסדר."` → `זה בסדר."` (soften the cut) | flow | minimal, natural dialogue, voice preserved |

**Verdict:** one minimal-span suggestion per passage, **no meaning change, no plot invention, valid and
natural Hebrew, author voice respected** — low overreach. This is on par with the recorded local
**Dicta-3.0** baseline (minimal, voice-preserving) and far better than the recorded local **qwen**
(plot hallucination + garbled Hebrew). The cloud big model is a **safe quality-ceiling** for LineEdit
with no regression vs local Dicta-3.0 — but it does not show a *clear win* over Dicta-3.0 that would
justify changing the local default.

> Re-run a live local side-by-side any time with `LINEEDIT_SPOTCHECK_PROVIDER=Ollama` +
> `LINEEDIT_SPOTCHECK_MODEL=hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest`. It was not
> auto-run here (no-local-rerun + ask-before-GPU; Dicta-12B CPU-spills to minutes/case locally).

## Recommendation (each tied to the precision gate)

| Task | Recommendation | Why |
|---|---|---|
| **Proofread (he)** | **KEEP local Dicta-3.0 active; document `OpenRouter/google/gemma-4-31b-it` as the cloud max-quality target** (the GPU-server ceiling). | Cloud is clearly better AND passes the gate (overreach 0/2, precision 88% vs 26%). But the active default stays local/free/offline — exactly the Linguistic precedent. Flip to cloud when GPU-server hosting exists. |
| **Proofread_en** | **KEEP `gemma4:12b`; now VALIDATED by an English gold (2026-06-21).** | **MEASURED** by `proofread-gold-en.json` (30 cases) - gemma4:12b vs qwen3.5:9b: precision **82% vs 31%**, recall **69% vs 69%** (tied), overreach **0/3 vs 1/3**, F0.5 **0.789 vs 0.349**. Passes the same precision gate as the Hebrew flow: far higher precision and lower overreach at equal recall (cost ~2.2x latency, acceptable for proofread). The cloud big model (`gemma-4-31b-it`) stays the future-GPU upgrade candidate. |
| **LineEdit** | **KEEP local Dicta-3.0; cloud validated as a safe future-hosting option (no regression).** | Cloud is on par with Dicta-3.0 on the rubric (minimal, meaning/voice-preserving), not a clear win. The gate is satisfied (no overreach), but parity ≠ switch. |

### Wiring done (config + test only, reversible)

Mirrors the `_comment_LinguisticAnalysis` pattern: the **active** Proofread value stays
`Ollama / DictaLM-3.0-Nemotron-12B`; a breadcrumb in `appsettings.json` documents the cloud
max-quality target. **No model was switched**, so `ProofreadQualityTests.ProofreadModel` (the
keep-in-sync const) is unchanged. To ship the cloud winner later, set
`Ai:FeatureModels:Proofread = { Provider: "OpenRouter", Model: "google/gemma-4-31b-it" }` and update
that const to match.

## Deployment note

The active local Proofread model (DictaLM-3.0-Nemotron-12B) and gemma4:12b (used for Proofread_en and LinguisticAnalysis) must be pulled on every Ollama host before starting the API. If either model is absent, OllamaProvider's 404-retry-with-default silently falls back to the `DefaultModel` (`qwen3.5:9b`) with no error surfaced. For Proofread, `qwen3.5:9b` is the overreaching model this work replaced (precision 13%, known meaning-changing edits such as `עצמה` to `עוצמת רגשות`). To avoid this silent regression, pull both models on every deployment host:

```powershell
ollama pull hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest
ollama pull gemma4:12b
```

## How to reproduce

```powershell
# Cloud Proofread bake-off (subset) — needs env AI_OPENROUTER_APIKEY
$env:PROOFREAD_BAKEOFF_PROVIDER = "OpenRouter"
$env:PROOFREAD_BAKEOFF_MODELS   = "google/gemma-4-31b-it"
$env:PROOFREAD_BAKEOFF_CASE_IDS = "overreach-ms-01,overreach-ms-02,clean-overreach-ms-03,inj-ms-01,inj-ms-03,inj-ms-04,inj-ms-09,inj-ms-10,clean-ms-02,clean-ms-24,clean-ms-36,norm-5,detect-1"
dotnet test --filter "FullyQualifiedName~ProofreadQuality_ModelBakeoff" --logger "console;verbosity=detailed"

# LineEdit cloud rubric spot-check
dotnet test --filter "FullyQualifiedName~LineEditCloudSpotCheck" --logger "console;verbosity=detailed"
```

Both are **skip-by-default**: with no `AI_OPENROUTER_APIKEY` they print a SKIPPED message and pass, so
CI stays green. `PROOFREAD_BAKEOFF_MAX_CASES=<n>` is an extra numeric cap on top of the id subset.
