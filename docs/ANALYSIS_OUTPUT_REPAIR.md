# Analysis Output Repair Layer

A scoped, guard-gated, measured "read-and-fix" pass over AI analysis PROSE (summaries, literary,
linguistic, line-edit, book-overview, character, story, Q&A, and whole-book review) that removes
leaked English terms and garbled words from Hebrew output without touching anchored/structural
fields. Built by plan `src/.cursor/plans/_todo/analysis-output-repair-2026-07-03.plan.md` - read it
for the full phase-by-phase gate history and measurements; this doc is the durable design reference.

The coverage was later extended (BookOverview / CharacterAnalysis / StoryAnalysis / QA / BookReview)
and made model-tier aware by the follow-up plan
`src/.cursor/plans/_todo/analysis-repair-coverage-cloud-tiers-2026-07-06.plan.md` - see section 3's
coverage table, section 4.1's per-environment/tier policy, and section 11's measured leak-by-tier
table. The governing principle of that follow-up: **the English-into-Hebrew leak is a small-model
artifact, so repair coverage should scale INVERSELY with model capability** - the cheap deterministic
defenses ship on the small-local tier and go no-op on a capable cloud tier.

Sibling docs: [./Hebrew-Proofread-Model.md](./Hebrew-Proofread-Model.md) (the model this layer does
NOT touch), [./LINGUISTIC_MODEL_BAKEOFF.md](./LINGUISTIC_MODEL_BAKEOFF.md) (the model this layer's
LLM stage reuses, gemma4:12b).

---

## 1. Overview and motivation

The local models that power PageDraft's analysis features (Summarization, LiteraryAnalysis,
LinguisticAnalysis, LineEdit) return fluent but "a little scrambled" Hebrew: they leak untranslated
English terms into Hebrew prose (`narrator`, `(Action)`, `(Tension)`, `(Face-saving)`, "Magic vs.
Nature", "High Stakes"), occasionally garble a Hebrew word (`המתרחס` for `המתרחש`, `מצייח` - not a
real word), and - separately - sometimes emit structurally corrupted JSON (a misspelled key like
`narriceVoiceDescription`, or a LineEdit repetition loop that duplicates suggestions and truncates
mid-object).

This layer addresses the first two failure classes (English bleed + garbled words) with a
value-scoped, guard-gated repair pass over analysis prose, and the third (structural corruption)
with deterministic post-parse fixes. **Proofread is never touched** - it is the one analysis type
measured clean of leaks, and even if it leaked, the corrected text is a verbatim, offset-anchored
edit that must never be rewritten by a repair layer.

Three non-regression guarantees are enforced end-to-end:

- **Scoping invariant** (deterministic): repair only ever reads/writes through a whitelist of PROSE
  accessors (`RepairableFields`); every non-whitelisted field - JSON keys, enums, metric keys, spans,
  offsets, `original`/`suggested` - is byte-identical after repair, proven by a dedicated invariant
  test.
- **Gold-harness equality** (measured): the existing Proofread and Linguistic gold harnesses must not
  regress before/after each phase (see the phase gate history in section 10).
- **Fail-safe** (deterministic): the LLM repair stage validates its own output and reverts to the
  original value on any doubt - it can only leave a field cleaner or unchanged, never worse.

## 2. Two-stage repair architecture

Two independent stages, run in order, both guard-gated and Hebrew-book-gated:

**Stage 1 - deterministic glossary pass** (`Services/Analysis/GlossaryRepairPass.cs`). For each
repairable prose field that still contains non-allowlisted Latin (per the guard, below), replaces
known English terms with their accepted Hebrew equivalent from a closed glossary
(`Services/Analysis/LiteraryTermGlossary.cs`), at ASCII word boundaries, longest-key-first so a
multi-word phrase ("high stakes") wins over a single-word match, preserving all surrounding
punctuation. A term not in the glossary is left untouched - that residual is Stage 2's job. Pure,
static, no I/O, no model call; fails safe on any parse error (returns the input unchanged).

**Stage 2 - value-scoped LLM repair** (`Services/Analysis/AnalysisRepairService.cs`). For each
repairable field the guard STILL flags after Stage 1, sends **only that field's value** (never the
surrounding JSON) to the repair model (gemma4:12b, routed via `AiTaskType.AnalysisRepair`) with a
verbatim Hebrew instruction: replace non-Hebrew terms with their accepted equivalent, fix
spelling/grammar/fluency, and preserve meaning/insights/structure exactly. The model's output is then
validated before being accepted - **any** failure discards it and keeps the original value:
  - non-empty after trim;
  - still predominantly Hebrew (Hebrew letter count >= Latin letter count);
  - introduces no NEW Latin run beyond what the input already had;
  - length ratio (repaired / original) within `[0.6, 1.6]`.

**The guard** (`Services/Analysis/LatinInHebrewContentDetector.cs`) is what makes both stages cheap
and safe: a "Latin run" is a maximal sequence of >=2 consecutive ASCII letters not in a tiny,
conservative proper-noun allowlist (`Google`, `Facebook`). A repairable field with zero non-
allowlisted Latin runs is left byte-identical and reaches neither stage - **a clean analysis output
makes ZERO model calls.**

**Why raw JSON is never fed to either stage:** the diagnostic that motivated this layer proved that
feeding a whole structured result to a repair model is destructive - gemma translated JSON keys to
Hebrew and Dicta reformatted JSON into markdown, dropping a theme entirely. Both stages are therefore
value-scoped: structure is held by code (`RepairableFields` get/set accessors + re-serialization with
the pipeline's camelCase `JsonOpts`), never by a model.

**The three seams.** Both stages are wired together behind one entry point,
`UnifiedAnalysisService.ApplyAnalysisRepairAsync` (`UnifiedAnalysisService.cs:2558`), called from all
three places a structured analysis result is finalized, AFTER parse/sanitize and BEFORE persistence:
`RunAsync` (`:307`, call at `:435`), `RunWithInputAsync` (`:514`, call at `:560`), and the streaming
path `RunStreamingAsync` (`:631`, call at `:725`). For LineEdit, `ResultText` is re-derived from the
repaired `overallFeedback` after both stages run (mirrors the pre-repair
`MaybeReplaceLineEditResultText` call).

**Model choice:** gemma4:12b is the repair model for Stage 2. The diagnostic's prototype showed
Dicta-3.0 (the Proofread/LineEdit model) over-rewrites when asked to repair prose - it drifted "the
passage is in first person" into an in-character "I am the main character" - so it was rejected as
the repair model even though it is the correct model for Proofread/LineEdit's own primary task.

## 3. Repairable-vs-structural field map

Single source of truth: `Services/Analysis/RepairableFields.cs`. One `For(...)` overload per
structured result type returns an ordered list of `(get, set)` accessors over PROSE fields only;
nothing else is ever exposed to either repair stage.

| Analysis type | AiTaskType | Model | Repairable prose fields | Reached via |
|---|---|---|---|---|
| Summarization | Summarization | qwen3.5:9b | Entire `ResultText` (`RepairableFields.ForPlainText`) | analysis seam |
| LiteraryAnalysis | LinguisticAnalysis | gemma4:12b | `summary`, `tone`, `toneDescription`, `narrativeVoice`(+`Description`), `themes[].name`/`description`, `rhetoricalDevices[].name`/`example`/`effect`, `moodProgression` | analysis seam |
| LinguisticAnalysis | LinguisticAnalysis | gemma4:12b | `summary`, `deviations[].note`, `consistencyIssues[].description` | analysis seam |
| Proofread | Proofread | Dicta-3.0-Nemotron-12B | **None** - `RepairableFields.For(AnalysisSuggestion)` returns an empty list; never repaired | n/a (never repaired) |
| LineEdit | LineEdit | Dicta-3.0-Nemotron-12B | `overallFeedback`, `suggestions[].reason` | analysis seam |
| BookOverview | LinguisticAnalysis | gemma4:12b | `summary` only | analysis seam |
| CharacterAnalysis | LinguisticAnalysis | gemma4:12b | `summary`, `characters[].description`/`arc`, `relationships[].relationship` | analysis seam |
| StoryAnalysis | LinguisticAnalysis | gemma4:12b | `plotStructure.{setup,risingAction,climax,fallingAction,resolution}`, `pacing`, `conflicts[].description`, `summary` | analysis seam |
| QA | GenericChat | qwen3.5:9b | `answer` only | analysis seam (parsed via `TryExtractAndReserialize<QAResult>`) |
| BookReview | BookReview | gemma4:12b | `findings[].rationale`, `findings[].suggestedAction` (on the persisted `BookFinding` ENTITY) | **engine hook** (`BookReviewService.ApplyGlossaryToFindings`) |

"**Analysis seam**" = the three finalize points (`RunAsync` / `RunWithInputAsync` / `RunStreamingAsync`)
that call `UnifiedAnalysisService.ApplyAnalysisRepairAsync` after parse/sanitize, before persistence.
QA reaches the seam with a NON-null structured result because `TryParseStructured` routes
`AnalysisType.QA` through `TryExtractAndReserialize<QAResult>` (`UnifiedAnalysisService.cs:2185`); if the
QA output is not parseable into that shape the structured JSON is null and repair is a fail-safe no-op
for that run.

> **BookReview coverage extension (f5-wire).** BookReview is now wired, but NOT through the analysis
> seam - it flows through the whole-book review ENGINE (`BookReviewService`), a windowed map-reduce path
> that never calls `ApplyAnalysisRepairAsync`. So `GlossaryRepairPass.Apply` and
> `AnalysisRepairService.RepairAnalysisAsync` still deliberately return no-op on `AnalysisType.BookReview`
> (both keep their `default` case + the "flows through a DIFFERENT path" comment). Instead, the engine
> hook `BookReviewService.ApplyGlossaryToFindings` runs the SAME deterministic glossary
> (`GlossaryRepairPass.RepairFields`) directly over the FINALIZED, unioned/deduped `List<BookFinding>`
> ENTITIES right after `UnionAndDedup` and BEFORE `PersistPreservingStatusAsync`, repairing each
> finding's `Rationale` + (non-null) `SuggestedAction` IN PLACE. It is glossary-ONLY (no LLM stage, ever,
> regardless of `GuardOnly`), triple fail-safe (per-finding try/catch inside the walk, an outer walk
> try/catch, and a belt-and-braces try/catch at the call site), and skipped entirely on a total-failure
> build (empty finding set -> no-op). See section 3.1 for why the `DedupKey` is left untouched.
>
> The parsed-DTO overload `RepairableFields.For(BookReviewResult)` (targeting `BookFindingItem`) still
> exists but is **test-only** - the engine projects the model's raw JSON straight to `BookFinding`
> entities before repair, so the live path uses the sibling `RepairableFields.For(BookFinding)` overload,
> not the DTO one.

### 3.1 BookReview: why repairing `Rationale` never disturbs `DedupKey`/`Status`

BookReview findings are deduped and status-preserved across rebuilds by a `DedupKey`
(`BookFinding.ComputeDedupKey(dimension, primaryChapterOrder, rationale)`). The glossary hook is placed
**after** `UnionAndDedup` computes and stamps that key from the **RAW model rationale**, and it mutates
ONLY `Rationale`/`SuggestedAction` - never `DedupKey`. `PersistPreservingStatusAsync` matches incoming
vs cached findings on the STORED `DedupKey`, never on a recomputation from the (now-repaired) rationale.
So on the next rebuild the model re-emits the same (possibly re-leaked) rationale, `UnionAndDedup`
re-derives the identical key, and the user's `Status` (acknowledged/dismissed/done) is preserved. The
repair is therefore a **display-time cleanup only, never a dedup input** - the persisted row's
`Rationale` (cleaned) and `DedupKey` (from raw) are intentionally derived from different strings, and no
code path may recompute the key from the persisted rationale without breaking status preservation
(covered by `BookReviewGlossaryRepairTests`).

**Must-not-touch (enforced by the whitelist + the invariant test, never exposed as an accessor):**

- All JSON property keys (structure is held by code / re-serialization, never renamed).
- Enums: `ThemeEntry.Significance`, `ConsistencyIssue.Type`, `LineEditSuggestion.Category`,
  `BookFindingItem.Dimension`/`Verdict`, `DimensionScore.Score`, `CharacterEntry.Role`,
  `ConflictEntry.Type`/`Status`, `QAResult.Confidence`.
- `StyleDeviation.Metric` (FE label-lookup key) and its numeric `SceneValue`/`ChapterBaseline`.
- `ConsistencyIssue.Span` (manuscript-quote anchor - Hebrew by construction, left verbatim).
- `LineEditSuggestion.Original`/`Suggested` (verbatim anchors).
- `AnalysisSuggestion.OriginalText`/`SuggestedText`/`StartOffset`/`EndOffset` (Proofread's offset
  anchors - the reason Proofread has zero repairable fields at all).
- `BookFindingItem.Severity`/`Evidence`/`ChapterAnchors`; all numeric metrics everywhere.
- **BookOverview:** `Genre`/`SubGenre`/`TargetAudience`/`LanguageRegister` (short label/register fields,
  not free prose), `LiteratureLevel`/`EstimatedReadingTimeMinutes` (numeric).
- **CharacterAnalysis:** `CharacterEntry.Name`, `CharacterRelationship.Character1`/`Character2`
  (proper-noun character references), `CharacterEntry.FirstAppearanceChapter` (numeric).
- **QA:** `ChapterCitation.ChapterNumber` (numeric), `ChapterTitle` (chapter-title reference),
  `RelevantExcerpt` (a manuscript-quote excerpt, left verbatim like `ConsistencyIssue.Span`).
- **BookReview ENTITY path** (`RepairableFields.For(BookFinding)`): `Dimension`/`Verdict` (enum-like
  labels), `Severity` (numeric), `EvidenceJson`/`ChapterAnchorsJson` (manuscript anchors + structural
  JSON), `DedupKey`/`Status`/`BuiltWithModel`/`CreatedAt`/`UpdatedAt` - only `Rationale` +
  (non-null) `SuggestedAction` are ever exposed.

The scoping contract is centralized in the header comment of `RepairableFields.cs` and enforced by
`RepairableFieldsTests` (`Pagedraft.Api.Tests/RepairableFieldsTests.cs`) - a byte-identity invariant
test that runs a transform mutating every prose accessor and asserts every non-whitelisted field
(keys, enums, `metric`, `span`, offsets, `original`/`suggested`) is unchanged.

## 4. Config toggles

`Ai:AnalysisRepair` in `appsettings.json`, mirrored by `AnalysisRepairOptions` in
`Services/Ai/AiOptions.cs`, read by `UnifiedAnalysisService.ApplyAnalysisRepairAsync`:

```jsonc
"AnalysisRepair": {
  "Enabled": true,
  "GuardOnly": true,
  "Model": "gemma4:12b",
  "PerType": {
    "Summarization": true,
    "LiteraryAnalysis": true,
    "LinguisticAnalysis": true,
    "LineEdit": true,
    "BookOverview": true,      // f5-wire (analysis seam)
    "CharacterAnalysis": true, // f5-wire (analysis seam)
    "StoryAnalysis": true,     // f5-wire (analysis seam)
    "QA": true,                // f5-wire (analysis seam)
    "BookReview": true         // f5-wire (engine hook - gates BookReviewService.ApplyGlossaryToFindings)
  }
}
```

> **The 5 f5-wire types MUST be listed here or their wiring is silently dead.** `PerType` is a strict
> allowlist when non-empty (see the paragraph below): a type ABSENT from a populated map is skipped. The
> base map already listed only the four original types, so adding the new-type switch arms + overloads
> WITHOUT adding these five keys would have left the runtime gate closed - the code path present but never
> reached. The `"BookReview"` key gates the engine hook via `BookReviewService.PerTypeAllowsBookReview`,
> which mirrors the seam's `UnifiedAnalysisService.PerTypeAllows`.

Three states:

| `Enabled` | `GuardOnly` | Behavior |
|---|---|---|
| `false` (or block absent) | - | **Full no-op.** Neither Stage 1 (glossary) nor Stage 2 (LLM) runs; inputs returned byte-identical. |
| `true` | `true` | **Deterministic glossary ONLY, no LLM, no model calls.** **This is the shipped default** - the Phase-3 gate measured the LLM stage over-rewriting a mixed leak+prose field (see section 10), so it ships opt-in rather than on. |
| `true` | `false` | Glossary + value-scoped LLM repair. Still guard-gated and fail-safe inside `AnalysisRepairService` - a clean field makes zero model calls even with the LLM stage enabled. |

`PerType` gates repair per `AnalysisType` name. A null/empty map means no restriction (every
repairable type runs); a non-empty map is a strict allowlist - a type absent or mapped to `false` is
skipped. Proofread is never repaired regardless of `PerType`.

**To opt into the LLM stage** (e.g. after validating on your own corpus), set
`Ai:AnalysisRepair.GuardOnly = false`. Keep `Model` in sync with
`Ai:FeatureModels:AnalysisRepair` (currently `{ "Provider": "Ollama", "Model": "gemma4:12b" }`) and
its tuning block `Ai:ProviderSettings:Ollama_AnalysisRepair`
(`{ "Temperature": 0.2, "NumPredict": 2048, "NumCtx": 16384 }`) - those two keys do the actual
routing; `Ai:AnalysisRepair.Model` only documents/asserts the intended model at the config surface.

> **`GuardOnly` asymmetry (intentional).** Flipping `GuardOnly=false` opts the FOUR analysis-seam
> f5-wire types (BookOverview/Character/Story/QA) into the value-scoped LLM Stage-2 alongside the
> original repairable types. **BookReview is the exception:** its engine hook is glossary-ONLY and
> ignores `GuardOnly` entirely - the whole-book path never makes a per-field LLM repair call. This is
> deliberate (no extra billed/latency cost on the already-expensive whole-book pass), so do not expect
> `GuardOnly=false` to add LLM repair to BookReview.

### 4.1 Per-environment / model-tier policy

The repair block is **one value per ASP.NET Core environment**: base `appsettings.json` plus an optional
`appsettings.{Environment}.json` override. The follow-up plan added `appsettings.Production.json`
carrying an `Ai:AnalysisRepair` block whose value ENCODES the policy for that environment's served model
tier. The governing principle (leak = small-model artifact) makes this a clean lever:

| Served tier (via `Ai:FeatureModels`) | Repair policy | How to express it |
|---|---|---|
| **Small-local** (Ollama gemma4:12b / qwen3.5:9b / Dicta-3.0) - a LEAKY tier | guard-only glossary ON | `Enabled:true`, `GuardOnly:true`, PerType glossary-on for every type (the shipped base + current Production value) |
| **Capable cloud** (a tier that does not leak) | true no-op | `Enabled:false` (full no-op), or `GuardOnly:true` with an empty `PerType` |

**Current state:** BOTH `appsettings.json` and `appsettings.Production.json` are glossary-on with a
BYTE-IDENTICAL `PerType` (all nine types `true`) because **prod still serves the Ollama-LOCAL tier** -
where the deterministic guard earns its keep. The Production block carries an explicit KEEP-IN-SYNC note
tying it to `Ai:FeatureModels` and instructs: **flip `Enabled` to `false` ONLY when prod actually moves
`Ai:FeatureModels` to a cloud model that does not leak** - do NOT flip it preemptively; verify the served
model first. Do NOT assume prod is already cloud.

**Documented extension point (NOT implemented).** A per-provider/per-model override ON
`AnalysisRepairOptions` (e.g. a dictionary keyed by provider/model, consulted instead of the flat
`Enabled`/`GuardOnly`/`PerType`) is only warranted if a SINGLE environment ever serves MULTIPLE tiers at
once (e.g. free users -> local, paid -> cloud, in the same deployment). Today each environment serves
exactly one tier, so the per-environment appsettings block is sufficient. This is called out in the
`AnalysisRepairOptions` xmldoc (`Services/Ai/AiOptions.cs`) and the Production block comment - do not
build it speculatively.

## 5. Structural fixes (Phase 4)

Two deterministic, no-LLM fixes for structural corruption observed live, independent of the repair
layer above:

**LineEdit dedupe/no-op/cap** - `UnifiedAnalysisService.NormalizeLineEditSuggestions`
(`UnifiedAnalysisService.cs:1357`), applied to every successful LineEdit parse/salvage path. Three
passes, in order: (1) drop suggestions where `Original == Suggested` after trim/Unicode
normalization, and drop suggestions that differ only by *surrounding* punctuation/whitespace (the
real `"לא,"` -> `"לא"` repetition-loop noise) while keeping internal-punctuation edits; (2) dedupe on
the `(Original, Suggested)` pair, first occurrence wins; (3) cap survivors at
`MaxLineEditSuggestions = 50` (`:1315`) as a backstop against near-identical entries that slip past
exact-pair dedupe. Paired with an appsettings decoding-side fix: `Ollama_LineEdit` now carries
`"RepeatPenalty": 1.3` (up from the 1.1 default / 1.2 used for LinguisticAnalysis) to break the
repetition loop at the source (the loop was a decoding failure, not a parsing one).

**Near-miss JSON key tolerance** - `UnifiedAnalysisService.RepairNearMissKeys<T>`
(`UnifiedAnalysisService.cs:2214`), invoked from `TryExtractAndReserialize<T>` (`:2163`) before
deserialization. For each KNOWN schema key that is absent from the parsed JSON, if exactly ONE
present, not-already-known, not-yet-claimed key is a near-match (bounded Levenshtein distance `<= 3`
AND length difference `<= 2`, via `BoundedLevenshtein` at `:2301`), it is renamed to the known key;
zero or more than one candidate is ambiguous and is left alone. Key names only - never values or enum
fields - and every rename is logged. This fixes the real
`narriceVoiceDescription` -> `narrativeVoiceDescription` silent field drop. Note: the plan text
originally specified Levenshtein `<= 2`; the real fixture is distance 3, so the implemented bound was
deliberately widened to `<= 3` (documented at `UnifiedAnalysisService.cs:2204-2206`) so the actual
observed typo binds.

## 6. Prompt hardening (Phase 5)

An upstream reduction, orthogonal to the repair layer: `PromptFactory.HebrewNoEnglishTermsClause`
(`Services/Ai/PromptFactory.cs:22`) is appended to the shared `HebrewAnalysisSystem` message
(`:26`) used by LiteraryAnalysis, LinguisticAnalysis, Summarization, and BookReview. It is one short,
explicit instruction - respond in Hebrew only, never insert English terms parenthetically - plus a
compact 8-term glossary subset (`narrator=מספר, tone=טון, mood=מצב רוח, foreshadowing=רמיזה מקדימה,
imagery=דימויים, tension=מתח, climax=שיא, action=פעולה`), kept deliberately short to avoid prompt
bloat or a recall regression. `EnglishAnalysisSystem` (`:46`) carries the symmetric English wording
("do not insert non-English terms parenthetically") for English-book analysis.

Measured effect (Phase 5 gate, section 10): CONTENT-value leaks dropped from 4 distinct terms to 0
in the live diagnostic, without lowering linguistic recall/type-accuracy/composite or degrading
Literary/Summarization insight quality. This means the deterministic guard and both repair stages
fire less often in practice - the prompt fix is the cheapest lever and was measured before deciding
the repair-layer default.

### 6.1 QA disposition (f1-prompt-coverage)

QA did NOT originally get the Hebrew-only clause. QA resolves to `AiTaskType.GenericChat`
(`AnalysisTaskMapping.cs:28`), whose neutral system frame (`PromptFactory.cs:98`) is SHARED with
Translation + Custom - which legitimately emit other languages - so the clause could not be appended to
that shared system without wrongly forcing Hebrew on Translation/Custom.

f1 resolves this by appending `HebrewNoEnglishTermsClause` to the QA INSTRUCTION only, Hebrew-gated:
`AnalysisType.QA => isHe ? QAHe + HebrewNoEnglishTermsClause : QAEn` (`PromptFactory.cs:141`). The clause
constant begins with a leading space, so the concatenation is well-formed; English QA (`QAEn`),
Translation, Custom, and the `HebrewSystemBase` Proofreader frame are all untouched. QA is therefore now
covered on BOTH ends: the prompt steer (Hebrew-only, no parenthetical English) AND the repair wiring (it
reaches the analysis seam as a parsed `QAResult`, so its `answer` prose gets the deterministic glossary
safety net - section 3).

## 7. Observability

**Per-field (Debug, high-volume):** `AnalysisRepairService.LogRepairedField` emits one line per field
the guard flagged, with the analysis type, a stable accessor INDEX (never the field name or content),
Latin-run counts before/after, whether the LLM ran, whether the result was accepted or fail-safe-
discarded, and latency in ms. No field value or model output is ever logged.

**Per-run aggregate (INFO when something changed, Debug when clean):**
`UnifiedAnalysisService.ApplyAnalysisRepairAsync` times the whole repair layer and emits one line per
analysis run:

```
AnalysisRepair: type={Type} glossaryChanged={G} llmFlagged={N} llmRepaired={M} llmFailSafe={K} totalMs={Ms}
```

`glossaryChanged` (G) is Stage 1's field-changed count; `llmFlagged`/`llmRepaired`/`llmFailSafe`
(N/M/K) are Stage 2's flagged / accepted-and-changed / rejected-and-kept-original counts (all zero
while `GuardOnly=true`, since Stage 2 never runs). A clean analysis (nothing flagged or changed) logs
a single Debug no-op line instead, so a healthy production run produces no INFO noise from this
layer.

## 8. Validation harnesses

| Harness | Scope | GPU? |
|---|---|---|
| `RepairableFieldsTests` (`Pagedraft.Api.Tests/RepairableFieldsTests.cs`) | Scoping invariant: every structural field byte-identical after a transform that mutates every prose accessor | No |
| `GlossaryRepairPassTests` (`Pagedraft.Api.Tests/GlossaryRepairPassTests.cs`) | Stage 1 deterministic replacement, byte-identity of structural fields, Hebrew/English book gating, Proofread never touched | No |
| `LatinInHebrewContentDetectorTests` | Guard run-detection semantics, allowlist behavior | No |
| `AnalysisRepairServiceTests` (`Pagedraft.Api.Tests/AnalysisRepairServiceTests.cs`) + `AnalysisRepairSmokeTests` (`.../LanguageEngine/`) | Stage 2 fail-safe validation, guard-gating (fake router sees zero calls on clean input), re-serialization fidelity; the new-type (BookOverview/Character/Story/QA) seam cases | No (fake `IAiRouter`) |
| `BookReviewGlossaryRepairTests` (`Pagedraft.Api.Tests/BookReviewGlossaryRepairTests.cs`) | BookReview ENGINE-hook `ApplyGlossaryToFindings`: Rationale/SuggestedAction Hebraised while Dimension/Verdict/Severity/Evidence/ChapterAnchors/**DedupKey**/Status stay byte-identical; layer/PerType/Hebrew gating; null-list, null-element, null-SuggestedAction, faulting-enumerator fail-safes; `For(BookFinding)` scoping | No |
| `RepairQualityTests` (`Pagedraft.Api.Tests/LanguageEngine/RepairQualityTests.cs`) | The repair-gold scorer: latin-removed %, structure-preserved % (must be 100), no-new-latin, length-ratio bound, must-preserve %, clean-control no-op, advisory LLM-judge meaning-preserved | Yes (skip-gated) |
| `OutputQualityDiagnostic` (`.../LanguageEngine/OutputQualityDiagnostic.cs`) | Real-output capture: per-task Latin-leak scan split STRUCTURAL vs CONTENT, re-run at every phase gate | Yes (skip-gated) |
| `ProofreadQualityTests` / `LinguisticQualityTests` | The non-regression yardsticks this layer must not move | Yes (skip-gated) |

**Important path-fidelity caveat:** `ProofreadQualityTests` and `LinguisticQualityTests` drive
`IAiRouter.CompleteAsync` directly (see the PATH-CHOICE header in `LinguisticQualityTests.cs`),
deliberately bypassing `UnifiedAnalysisService` - so they do NOT exercise the wired repair pass at
all. The live proof that the wired pass behaves correctly is the deterministic
`GlossaryRepairPassTests`/`RepairableFieldsTests`/`AnalysisRepairServiceTests` suites (which construct
the real service classes) plus, when re-run under Ollama, `OutputQualityDiagnostic` against production
routing. Treat the gold harnesses as the "did anything else regress" check, not as an end-to-end test
of this layer.

## 9. The repair-gold + glossary growth habit

Mirrors the existing proofread-gold-growth habit (`ProofreadQualityTests.cs` / `proofread-gold.json`
/ memory note `proofread-gold-growth-habit`): every real leak or garble found in production analysis
output should be promoted into the regression corpus, not just fixed ad hoc.

When `OutputQualityDiagnostic`, a `RepairQuality` run, or a live user report surfaces a real leaked
English term, a real garbled Hebrew word, or a clean-Hebrew false positive from the guard:

1. **Add a case** to `Pagedraft.Api.Tests/TestData/repair-gold.json` - a real (not fabricated)
   snippet with `expectedLatinRemoved` (or `expectedGarbleFixed`) and `mustPreserve` substrings, or an
   `isCleanControl: true` entry if it is a false positive. Never fabricate Hebrew from translation -
   use real manuscript/analysis text (see memory note `pagedraft-eval-manuscript`), and every Hebrew
   entry needs eventual native-speaker validation (the file's own `_README` entry documents this, same
   caveat as `linguistic-gold.json` / `proofread-gold.json`).
2. **If it is an unambiguous 1:1 term**, add it to `LiteraryTermGlossary.Terms`
   (`Services/Analysis/LiteraryTermGlossary.cs`) - conservatively; when unsure whether a Hebrew
   rendering is the single accepted equivalent, leave it out rather than guess (an absent term is
   "leave untouched", the safe default, never a bug).
3. **If it is a proper-noun false positive** (a legitimate Latin brand/name that should never flag),
   add it to `LatinInHebrewContentDetector.ProperNounAllowlist` - again conservatively; the guard is
   deliberately biased toward flagging, since a missed allowlist entry only costs one extra (fail-safe)
   repair attempt, while an over-broad allowlist lets a real leak through.
4. **Re-run** `dotnet test --filter "FullyQualifiedName~RepairQuality"` plus the phase-gate suite
   (`~GlossaryRepairPass|~RepairableFields|~LatinInHebrew|~AnalysisRepairService`) and, before
   shipping any change, re-confirm `ProofreadQuality_RunGoldCases` and `LinguisticQuality_RunGoldCases`
   are unchanged (this layer must never move Proofread/Linguistic numbers - see section 8's
   path-fidelity caveat for why the deterministic suites are the real proof).

This keeps the gate reflecting production reality instead of a fixed synthetic snapshot, the same
discipline that took the proofread gold set's recall from 22% to 83% over time.

## 10. Phase gate history

Every phase's gate is recorded in full (with source logs) in the plan file itself
(`## Phase 0 baseline` through `## Phase 5 gate` in
`src/.cursor/plans/_todo/analysis-output-repair-2026-07-03.plan.md`). Condensed here:

| Gate | Verdict | Headline result |
|---|---|---|
| Phase 0 (baseline) | - | Froze the yardstick: Proofread P 23.8% / R 75.0%; Linguistic recall 92% / composite 0.831; 4 distinct CONTENT-value leaks captured (`Magic vs. Nature`, `High Stakes`, `Tension`, `(Action)`) |
| Phase 2 (deterministic glossary) | PASS | Parenthetical-English family gone from CONTENT with structural byte-identity proven; Proofread/Linguistic gold non-regressed (both improved on this run - model-sampling noise, not a Phase-2 effect, since the gold harnesses bypass the wired pass) |
| Phase 3 (value-scoped LLM repair) | PASS | RepairQuality: latin-removed 100%, structure-preserved 100%, no-new-latin 100%; fail-safe demonstrably rejects an out-of-bounds rewrite (2.12x length ratio); decision: ship guard-only - the LLM stage showed an over-rewrite tendency on one mixed leak+prose theme-name field |
| Phase 4 (structural fixes) | PASS | 70/70 deterministic parse tests green; the `narriceVoiceDescription` fixture now binds; the 10-duplicate LineEdit fixture collapses to 1 unique suggestion |
| Phase 5 (prompt hardening) | PASS | CONTENT-value leaks 4 -> 0 in the live diagnostic; Linguistic recall/type/composite improved, not regressed; Literary/Summarization insight quality eyeballed intact |

**Shipped state:** `Ai:AnalysisRepair.Enabled = true`, `GuardOnly = true` - the deterministic glossary
pass runs always-on, the LLM repair stage is built, tested, and gated but ships off by default per the
Phase-3 gate's data-driven decision (section 4).

## 11. Coverage-extension measurement (leak-by-tier)

The follow-up plan (`analysis-repair-coverage-cloud-tiers-2026-07-06`) is **measurement-first**: nothing
was wired without a diagnostic showing it leaks on the tier it serves. The extended `OutputQualityDiagnostic`
(with `DIAG_MODELS` per-tier + `DIAG_INPUTS` multi-passage overrides + a robust QA answer-extractor) was run
against the **local-small tier** on 3 real Hebrew manuscript passages (P1 narrative ~373 w, P2 dialogue
~375 w, P3 descriptive ~382 w). CONTENT-value leak = a Latin run leaked into Hebrew PROSE; STRUCTURAL
(json keys + enum labels) is expected and is NOT a leak.

| Type (local tier) | Model | P1 | P2 | P3 | Verdict |
|---|---|---|---|---|---|
| BookOverview / CharacterAnalysis / StoryAnalysis / QA / BookReview | gemma4:12b (QA: qwen3.5:9b) | clean | clean | clean | CONTENT-clean 3/3 |
| Summarization / LinguisticAnalysis / LineEdit | qwen3.5:9b / gemma4:12b / Dicta-3.0 | clean | clean | clean | structural-only (keys/enums) |
| **LiteraryAnalysis** | gemma4:12b | clean | **LEAK "confusion" in `narrativeVoiceDescription`** | clean | **1 real prose leak / 3** |
| Proofread | Dicta-3.0 | clean | clean | instruction-echo of `[TEXT_TO_CORRECT]` scaffold (prompt-bleed, NOT a leak; never repaired) | n/a |

**Why the 5 new types were wired despite measuring 3/3 clean.** LiteraryAnalysis - a `gemma4:12b`
structured-Hebrew-prose type, the SAME model + output shape as BookOverview/Character/Story/BookReview -
leaked an English word ("confusion") into prose on 1 of 3 samples EVEN WITH the shipped Phase-5 prompt
clause. The leak is therefore **real and stochastic on this tier, not eliminated by prompting**. Three
clean samples for a sibling type is not immunity when a same-model sibling demonstrably leaks, so the
deterministic glossary (cheap, fail-safe, no-op when clean) was wired as a same-tier safety net. QA is
`qwen3.5:9b` (weaker leak evidence - no leak seen on any Summarization/QA sample) but the glossary is a
uniform fail-safe no-op there regardless.

**Mechanism reminder:** only the DETERMINISTIC glossary (Stage 1) is wired for these types; the LLM
Stage 2 stays OFF by default (`GuardOnly=true`), unchanged from the predecessor's decision. A controlled
English-scrambled probe confirmed both that the models CAN leak and that the glossary repairs it to 0 -
and that feeding raw JSON to the LLM Hebraises schema KEYS (`themes`->`נושאים`), which is exactly why the
LLM stage is never on by default and structure is always held by code.

**Cloud tier: NOT measured here.** DNS resolves but all outbound HTTPS egress is blocked in this
environment (HTTP 000), so the configured `AI_OPENROUTER_APIKEY` was unreachable - the cloud columns are
deliberately left unmeasured rather than fabricated. The documented bake-offs stand as the cloud
"best-editing-abilities" reference and confirm the inverse-scaling premise (a bigger tier leaks LESS and
edits BETTER): LinguisticAnalysis cloud `gemma-4-31b-it` **0.900** vs local `gemma4:12b` 0.750
(`docs/LINGUISTIC_MODEL_BAKEOFF.md`); Proofread cloud `gemma-4-31b-it` **88/100 / overreach 0-2**
(`docs/PROOFREAD_LINEEDIT_CLOUD_BAKEOFF.md`). Moving to a cloud tier is the separate quality lever AND
would let repair go no-op (section 4.1) - a hosting decision orthogonal to this local guard.

### 11.1 Residual deferred (type x tier) + follow-ups

- **Cloud-tier leak measurement (every type):** deferred - blocked by no outbound egress in this
  environment. Re-run the extended diagnostic with `DIAG_MODELS` pointed at the cloud tier once a network
  path exists; expected result per inverse-scaling is fewer/zero CONTENT leaks -> cloud repair stays the
  documented no-op (section 4.1). Not fabricated.
- **QA leak evidence is weaker (`qwen3.5:9b`):** QA showed no leak on any sample; it is wired as a uniform
  fail-safe no-op for symmetry, not on measured evidence. If QA is ever re-tiered onto a leakier model,
  re-measure.
- **Editing-quality side-notes surfaced by the sweep (SEPARATE from leak repair, NOT addressed here):**
  1. **Proofread** echoed its `[TEXT_TO_CORRECT]` instruction scaffold + input preamble into the output on
     P3 instead of returning only the corrected text - a prompt/parse-bleed bug worth its own follow-up
     (Proofread is never touched by this repair layer, so it is out of scope here).
  2. **LineEdit** on P3 ballooned to 14282 raw chars / 333 s (possible repetition loop) - watch the
     `NormalizeLineEditSuggestions` cap/dedupe path (section 5) and the `RepeatPenalty` decoding lever.
- **Hebrew glossary/equivalents for the new types** need native-speaker validation (mirror the
  proofread/repair-gold `c04` deferral in section 9).

Full measured detail (both sweeps + the f6 gate blockquote) lives in the plan file's `## f4 leak-by-tier`,
`### f4b multi-sample`, and `## f6 gate` sections.
