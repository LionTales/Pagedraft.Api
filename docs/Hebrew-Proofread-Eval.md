# Hebrew Proofread Quality Eval Harness

This document explains how to run the proofread-quality evaluation harness and the model bake-off
built in Phase A of the proofread-quality plan.

---

## 1. Purpose

The harness measures how well the proofread pipeline corrects real Hebrew text errors. Before this
work, proofread quality was entirely unmeasured -- CI only validated that the pipeline returned a
response, not that the corrections were accurate.

The harness drives the **production code path** end-to-end:

1. Builds the proofread prompt via `PromptFactory.GetPrompt(AiTaskType.Proofread, lang)` -- the
   exact prompt `UnifiedAnalysisService` sends.
2. Calls `IAiRouter.CompleteAsync` with `TaskType = AiTaskType.Proofread` -- the same router call
   production uses.
3. Extracts corrections from the model output via `SuggestionDiffService.ComputeProofreadSuggestions`
   -- the same diff used to produce in-editor suggestions.

Only the DB/persistence wrapper is bypassed. Everything else is production code.

**Metrics reported:**

| Metric | Definition |
|---|---|
| Precision | Corrections matched / corrections produced (how many produced corrections were correct) |
| Recall | Corrections matched / corrections expected (how many gold corrections the model found) |
| False-positive rate | Clean-control cases that got a correction / total clean-control cases |

**Clean controls** are cases marked `shouldHaveNoChanges: true` -- they contain no intentional
errors. Any correction emitted on a clean control is a false positive.

---

## 2. Prerequisites

- **Local Ollama** running at `http://localhost:11434`. Install from https://ollama.com.
- The model(s) you want to test must be pulled: `ollama pull qwen2.5:14b`.
- Hardware note: the default bake-off shortlist is sized for an RTX 4070 laptop (~8 GB VRAM).
  24B+ models are intentionally excluded from the default list because they will not fit.

**If Ollama is unreachable**, both tests skip automatically (they return, passing without fail).
The probe checks both `localhost` and `127.0.0.1` because .NET sometimes resolves `localhost`
to `::1` (IPv6) while Ollama binds only `127.0.0.1`. This skip-by-default behavior mirrors the
existing `HebrewRegressionTests.BenchmarkRegression` pattern and keeps CI green on machines
without Ollama.

---

## 3. Running the single-model scorer

This test scores the gold set against a single hardcoded model (`qwen2.5:14b`) and prints
per-case and aggregate results.

**Test method:** `ProofreadQuality_RunGoldCases_ReportPrecisionRecallFalsePositive`

From the Tests project directory:

```powershell
cd C:\Users\tomer\source\repos\PageDraft\src\Pagedraft.Api-repo\Pagedraft.Api.Tests
dotnet test --filter "FullyQualifiedName~ProofreadQuality_RunGoldCases_ReportPrecisionRecallFalsePositive" --logger "console;verbosity=detailed"
```

The `--logger "console;verbosity=detailed"` flag is required to see the `ITestOutputHelper` output
in the terminal. Without it, xUnit suppresses the table. The output includes a per-case table
(id, expected, produced, matched, note) followed by an aggregate block:

```
=== Aggregate ===
Cases:                 87
Expected corrections:  ...
Produced corrections:  ...
Matched corrections:   ...
Precision:             ...
Recall:                ...
No-change cases:       ...
  with a correction:   ...
False-positive rate:   ...
```

---

## 4. Running the model bake-off

This test scores every model in a configurable list and emits a single comparison table. If a
model is not pulled or errors, its row shows `NA` and the loop continues -- one missing model
does not abort the whole run.

**Test method:** `ProofreadQuality_ModelBakeoff_ReportTable`

### Default run (built-in shortlist)

```powershell
cd C:\Users\tomer\source\repos\PageDraft\src\Pagedraft.Api-repo\Pagedraft.Api.Tests
dotnet test --filter "FullyQualifiedName~ProofreadQuality_ModelBakeoff_ReportTable" --logger "console;verbosity=detailed"
```

Default models (when `PROOFREAD_BAKEOFF_MODELS` is not set):

- `qwen2.5:14b`
- `hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:Q4_K_M`
- `gemma3:12b`

### Custom model list via env var

Set `PROOFREAD_BAKEOFF_MODELS` to a comma-separated list of Ollama model tags before running:

```powershell
$env:PROOFREAD_BAKEOFF_MODELS = "qwen2.5:14b,gemma3:12b"
cd C:\Users\tomer\source\repos\PageDraft\src\Pagedraft.Api-repo\Pagedraft.Api.Tests
dotnet test --filter "FullyQualifiedName~ProofreadQuality_ModelBakeoff_ReportTable" --logger "console;verbosity=detailed"
```

The env var is read at runtime; no recompile is needed. Duplicate entries are removed
(case-insensitive). If the env var is set but parses to zero models, the default shortlist is
used as a fallback.

---

## 5. Reading the bake-off table

Sample output:

```
=== Proofread model bake-off (87 gold cases, 3 models) ===
Model list source: built-in default shortlist

model                                                            prec   recall  fp-rate    total ms   ms/case  status
--------------------------------------------------------------------------- ...
qwen2.5:14b                                                       72%      65%      12%      184320      2119  ok
hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:Q4_K_M    -        -         -        1200         -  NA: ...
gemma3:12b                                                        58%      50%      20%      210000      2414  ok

[informational] Winner hint (highest recall): qwen2.5:14b -- recall 65%, precision 72%, 2119 ms/case. Verify against the full table; not a gate.
```

**Column reference:**

| Column | Meaning |
|---|---|
| model | Ollama model tag (truncated at 64 chars if long) |
| prec | Precision -- matched corrections / produced corrections |
| recall | Recall -- matched corrections / expected corrections |
| fp-rate | False-positive rate on clean-control cases |
| total ms | Wall-clock milliseconds for the whole gold set on this model |
| ms/case | Average milliseconds per case (total ms / case count) |
| status | `ok` if the model completed; `NA: <error first line>` if the model errored or is not pulled |

**Winner hint:** the informational line at the bottom names the model with the highest recall
among models that produced at least one match. Ties are broken by precision then lower latency.
This is not a pass/fail gate -- it is a convenience to direct attention.

**NA rows:** a model tag that is not pulled, runs out of VRAM, or times out gets a `-` in the
metric columns and an `NA: ...` status. The bake-off continues with the remaining models.

---

## 6. The gold dataset

**Location:**
`C:\Users\tomer\source\repos\PageDraft\src\Pagedraft.Api-repo\Pagedraft.Api.Tests\TestData\proofread-gold.json`

**Current size:** 87 cases.

**Case types:**

- **Error-injection cases** -- input contains deliberate errors (spelling, grammar, punctuation,
  whitespace); `expectedCorrections` lists each expected fix; `shouldHaveNoChanges` is absent or
  false.
- **Clean-control cases** -- input is grammatically correct Hebrew; `shouldHaveNoChanges: true`;
  `expectedCorrections` is empty or absent. These drive the false-positive rate metric.

**Schema** (`HebrewRegressionCase` + `ProofreadCorrection` in
`LanguageEngine/HebrewRegressionCase.cs`):

| Field | Type | Purpose |
|---|---|---|
| `id` | string | Unique case identifier (e.g. `"norm-1"`) |
| `input` | string | Raw Hebrew text sent to the model |
| `language` | string | BCP-47 tag (e.g. `"he-IL"`) |
| `expectedCorrections` | `ProofreadCorrection[]` | Ordered set of expected corrections |
| `shouldHaveNoChanges` | bool? | When `true`, no corrections should be produced (clean control) |
| `expectedCorrectedText` | string? | Full corrected text (not currently used by the eval harness) |
| `expectedNormalized` | string? | Legacy normalization field (not used by the eval harness) |

Each `ProofreadCorrection`:

| Field | Type | Purpose |
|---|---|---|
| `original` | string | The erroneous span in the input |
| `suggested` | string | The expected replacement text |
| `category` | string? | Optional label: `"grammar"`, `"spelling"`, `"punctuation"`, `"whitespace"` |

**Adding cases:** new gold cases are appended to `proofread-gold.json` as plain JSON objects.
Match the schema above. Assign a unique `id`. Suite growth is tracked in plan todo `c4-suite-growth`.

---

## 7. Interpreting results

**Precision and recall are relative to the gold annotations**, not to some absolute ground truth.
The gold set was hand-annotated and is a work-in-progress -- some annotations may not match what
the production model currently emits, which naturally depresses both metrics. Phase C of the plan
refines the prompt and diff logic to improve alignment.

- **High precision, low recall** -- the model is conservative: the corrections it does emit are
  mostly right, but it misses many expected fixes. A reasonable starting position.
- **Low precision, high recall** -- the model is aggressive: it catches most expected fixes but
  also generates many spurious corrections.
- **High false-positive rate** -- the model is changing clean text it should leave alone. Strongly
  penalize this in model selection.
- **NA across all models** -- Ollama is likely unreachable or all models are un-pulled. Re-run
  `ollama list` to confirm pulled models.

Current gold-vs-live-model alignment is acknowledged as a known work-in-progress. The numbers
establish a baseline; Phase C refines the prompt, diff, and suite so the metrics become a
reliable quality gate.

---

## 8. Cross-links

- `Hebrew-Proofread-Model.md` -- model selection rationale and configuration reference.
- Proofread quality plan -- `src/docs/` or `.cursor/plans/` (search for `proofread-quality`).
- Test class: `Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs`
- Gold data: `Pagedraft.Api.Tests/TestData/proofread-gold.json`
- Schema: `Pagedraft.Api.Tests/LanguageEngine/HebrewRegressionCase.cs`

---

## Maintaining the gold set (ongoing -- c4)

**Trigger:** whenever the single-model scorer surfaces a real miss or false positive that was not
previously captured, or a user reports a genuine Hebrew proofread mistake in the editor, add a new
case to `Pagedraft.Api.Tests/TestData/proofread-gold.json` and re-run the scorer as the gate before
treating any prompt or diff change as done.

### Two case types

**Error-injection case** (the model should detect and correct an error):

```json
{
  "id": "inj-ms-09",
  "input": "...",
  "language": "he-IL",
  "expectedCorrections": [
    {
      "original": "...",
      "suggested": "...",
      "category": "spelling"
    }
  ],
  "expectedCorrectedText": "...",
  "_note": "Provenance: source sentence + what error was injected and why."
}
```

Fields: `id` (unique, follow the `inj-ms-NN` sequence), `input` (the erroneous text), `language`
(`"he-IL"`), `expectedCorrections` (array of objects with `original`, `suggested`, and optional
`category`), `expectedCorrectedText` (the fully corrected sentence), and `_note` describing
provenance. The `_note` key is ignored by `System.Text.Json` on deserialization -- it is for human
readers only.

**Clean-control case** (the model should produce no corrections):

```json
{
  "id": "clean-ms-66",
  "input": "...",
  "language": "he-IL",
  "shouldHaveNoChanges": true
}
```

Use the real clean sentence from the manuscript. These cases drive the false-positive rate metric.
Add one whenever the model is observed over-correcting already-correct text.

### Source discipline

Never fabricate Hebrew text from translation. Use real manuscript sentences or a realistic
noise-injection of a real sentence (drop a letter, double a word, remove a space). Before
committing, visually inspect the RTL rendering of the `input` value -- if the extracted text looks
garbled or has mixed directionality that does not reflect the original source, discard the case.

### Gate

After adding one or more cases, re-run the single-model scorer:

```powershell
cd C:\Users\tomer\source\repos\PageDraft\src\Pagedraft.Api-repo\Pagedraft.Api.Tests
dotnet test --filter "FullyQualifiedName~ProofreadQuality_RunGoldCases_ReportPrecisionRecallFalsePositive" --logger "console;verbosity=detailed"
```

Requires local Ollama with `qwen2.5:14b` pulled (RTX 4070 or equivalent). Confirm that precision
and recall are equal to or better than the previous run, and that the false-positive rate has not
increased. A new injection case that the model currently misses is expected to lower recall
temporarily -- that is acceptable and documents the known gap. A new clean control that the model
over-corrects raises the false-positive rate and should prompt a prompt-engineering investigation
before merging.

### Known standing failures in the current gold set

Two failures are already captured and are intentionally left unresolved pending prompt work:

- `inj-ms-08` -- the model misses the ktiv-haser correction `נגשו` -> `ניגשו` (dropped yod).
  Recall is depressed by this case until a prompt or diff change fixes it.
- Clean-control over-corrections -- several `clean-ms-*` cases show the model changing
  already-correct words. These inflate the false-positive rate and are the primary target for
  prompt refinement in Phase C.

New cases of the same kind (ktiv-haser misses, clean-text over-corrections) should be added as
they are discovered, so the suite accumulates evidence before any fix is attempted.
