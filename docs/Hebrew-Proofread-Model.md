# Hebrew Proofread (Analysis) - Model Notes

The **Proofread** analysis type for Hebrew uses an Ollama model configured in
`Ai:FeatureModels:Proofread` (appsettings.json). As of June 2026 the production default is
**qwen3.5:9b**, chosen by a structured bake-off on the development hardware (see below).

## Bake-off results (June 2026)

Hardware: RTX 4070 laptop, ~8 GB VRAM.
Gold set: 87 Hebrew cases.

| Model | Precision | Recall | False-positive | Latency |
|---|---|---|---|---|
| **qwen3.5:9b** | 20% | 22% | 12% | ~1694 ms/case |
| qwen3.5:4b | 7% | 17% | 24% | ~1167 ms/case |

**Winner: qwen3.5:9b** - best precision, best recall, lowest false-positive rate, and runs fully
on the GPU (no CPU spill).

### Disqualified models (latency)

Models that exceed the ~8 GB VRAM budget spill to CPU and take minutes per case:

- **DictaLM-3.0-Nemotron-12B-Instruct** - CPU spill on this GPU; quality unmeasured (possible
  future work if run on a larger GPU or via a quantized config that fits in 8 GB).
- **qwen2.5:14b** - CPU spill on this GPU; quality unmeasured for Hebrew proofread.

Neither is viable as the interactive local default on an 8 GB laptop. Note: `NumPredict=4096`
(configured in `Ai:ProviderSettings:Ollama_Proofread`) is a significant latency amplifier for
CPU-spill models - lowering it would reduce quality before it reduces latency meaningfully.

## Current appsettings (Ai:FeatureModels)

```json
"Proofread":    { "Provider": "Ollama", "Model": "qwen3.5:9b" }
"Proofread_en": { "Provider": "Ollama", "Model": "qwen3.5:9b" }
```

`Proofread_en` is set to qwen3.5:9b for consistency and GPU-resident speed. **No English bake-off
has been run yet** - a dedicated English eval is pending. If English quality proves insufficient,
replace with a larger model once an English gold set is available.

## How the bake-off is run

See `Hebrew-Proofread-Eval.md` for the evaluation harness: how cases are structured, how
precision/recall/false-positive are computed, and how to add new gold cases.

## Config reference

Config key: `Ai:FeatureModels:Proofread` (Hebrew) and `Ai:FeatureModels:Proofread_en` (English)
in `Pagedraft.Api/appsettings.json`. Restart the API after changing.

Debug: set `Logging:LogLevel:Default` to `Debug` in appsettings to see a short preview of the
raw model response for Proofread in the console.
