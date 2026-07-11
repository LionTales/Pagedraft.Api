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

A THIRD follow-up plan (`src/.cursor/plans/_todo/dynamic-term-repair-design-2026-07-10.plan.md`, todos
d1-d7) then added a **span-scoped dynamic detect-and-repair stage** that generalises the closed glossary
to the open-ended tail of foreign-vocabulary leaks the ~35-term glossary cannot reach - see sections 12
(architecture), 13 (Mode config + TermRepair routing), 14 (measured decision + precision gates), 15
(rollout + kill-switch), and 16 (residual deferrals + review-retro candidates). It is **shipped wired but
OFF by default** (`Ai:AnalysisRepair.Mode = Glossary`): the d6 precision gate HALTED the flip because
neither the local nor the cloud tier reliably preserves legitimate foreign terms at the agreed >= 90% bar.

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

> **A THIRD stage was added later (sections 12-16), shipped OFF.** The dynamic-term-repair follow-up added
> a span-scoped detect-classify-repair stage (`DynamicTermRepairService`) selectable via the
> `Ai:AnalysisRepair.Mode` knob. Under the SHIPPED default (`Mode=Glossary`) it never runs, so the two
> stages below are exactly what ships today; the dynamic stage is documented in section 12 onward.

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

**`Mode` (added by the dynamic-term-repair follow-up).** A fourth knob, `Ai:AnalysisRepair.Mode`
(`Off` | `Glossary` | `Dynamic` | `GlossaryThenDynamic`), selects WHICH repair stage(s) run once
`Enabled`/`PerType` have allowed the type. The shipped default is `Glossary` - the deterministic glossary
only, reproducing the exact pre-follow-up behaviour. See section 13 for the full semantics and section 15
for the rollout / kill-switch.

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

## 12. Dynamic detect-and-repair layer (dynamic-term-repair-design plan)

The closed glossary (Stage 1) only cleans its ~35 curated craft terms; real leaks are open-ended general
vocabulary (`confusion`, `claustrophobia`, `ambivalence`, `nostalgia`, ...) that the glossary can never
reach. The dynamic-term-repair follow-up (plan `dynamic-term-repair-design-2026-07-10`, todos d1-d3) adds a
**bidirectional, span-scoped, fail-safe LLM stage** that handles that open tail, selectable via
`Ai:AnalysisRepair.Mode` (section 13). It is **shipped wired but OFF** (`Mode=Glossary`); the measured
decision that kept it off is section 14.

### 12.1 The d1 -> d2 -> d3 pipeline

```
[d1] LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript)  -> foreign-script runs
      |                                                                        (Text + Start + Length)
[d2] ForeignRunClassifier.RunsToRepair                                     -> the REPAIR subset
      |                                                                        (proper nouns / acronyms / urls -> LEAVE)
[d3] DynamicTermRepairService                                              -> one span-scoped IAiRouter
                                                                             TermRepair call per REPAIR run
```

- **d1 - `LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript)`.** The original count-only
  guard was generalised to a **bidirectional, span-returning** detector. It returns a
  `ForeignRun(Text, Start, Length)` for each maximal run of >= 2 consecutive same-script FOREIGN letters:
  Latin runs when Hebrew is expected (the original case) and Hebrew runs (U+05D0..U+05EA) when Latin is
  expected. Original casing is preserved; runs carry their exact UTF-16 offset + length so d3 can splice
  precisely. The old string API (`DetectLatinRuns` / `HasNonAllowlistedLatin`) is preserved as a thin
  Hebrew-expected wrapper, so every Stage-1/Stage-2 caller compiles untouched. `HasForeignRuns` is the cheap
  short-circuit gate.
- **d2 - `ForeignRunClassifier.Classify` / `RunsToRepair`.** A deterministic REPAIR|LEAVE verdict per run
  (pure, no I/O, no model). It LEAVEs the clear non-leak signals - Title-Case MID-sentence proper nouns
  (sentence-initial capitalization does NOT count), ALL-CAPS acronyms, URL/email/path/code tokens,
  number+unit tokens, and members of an optional `bookEntities` set - and REPAIRs the plain lowercase
  foreign word (the leak shape). First match wins; case-based signals apply only to Latin runs (Hebrew has
  no letter case), so a foreign Hebrew run is only spared by the entity list or a URL/number border - which
  is correct (bias to flag). The d3 model is the semantic backstop, so a false REPAIR costs at most one
  model call the model then no-ops, whereas a false LEAVE lets a leak through.
- **d3 - `DynamicTermRepairService`.** One span-scoped `IAiRouter` `TermRepair` call PER repair run: it marks
  exactly ONE run with guillemets (`«…»`), asks the model to replace only the marked token with its
  idiomatic equivalent and to return a proper-noun / no-equivalent token UNCHANGED, then defensively parses
  a tiny `{"replacement":"..."}` JSON and substitutes the token back by offset. Spans are applied
  right-to-left (descending Start) so earlier offsets stay valid; the marked prompt is always built from the
  ORIGINAL value.

### 12.2 Validate + fail-safe (the load-bearing property)

`DynamicTermRepairService` can only leave a value cleaner or byte-identical, never worse:

- **No REPAIR runs => ZERO model calls**, the value is returned byte-identical.
- **Each replacement is VALIDATED before it is spliced in:** non-empty after trim AND carrying NO run of the
  FOREIGN script (checked with the SAME d1 detector). That single check rejects BOTH the proper-noun echo
  (the model returned the still-foreign token) AND any junk still in the foreign script - on rejection the
  ORIGINAL span is kept.
- **A malformed / missing / null model payload** yields no replacement => the original span is kept.
- **Whole-value backstop:** the candidate may never have MORE foreign runs than the input; if it somehow
  does, the WHOLE value reverts to the original.
- **Any router error / timeout / exception** is caught and logged; the affected span (or, at the outer
  level, the whole value) keeps the original. No method here EVER throws to the caller.
- **Structure is held by code, never by the model:** `RepairFieldsAsync` only reads/writes through the
  `RepairableFields` whitelist accessors + re-serialization with the pipeline's `JsonOpts`, so every
  non-prose field (JSON keys, enums, numeric metrics, quoted-source / offset anchors) stays byte-identical -
  the same scoping contract as Stage 1/2 (section 3).

### 12.3 Bidirectional detection

d1/d2/d3 all take an `ExpectedScript` (`Hebrew` | `Latin`) derived from the book language by
`DynamicTermRepairService.ExpectedScriptForLanguage` (a `he*` language expects Hebrew, everything else
expects Latin). Hebrew-expected repairs **Latin-in-Hebrew** (the original leak direction); Latin-expected
repairs **Hebrew-in-Latin** (an English / Latin-script book leaking Hebrew). Because the dynamic stage is
bidirectional (unlike the Hebrew-only, English->Hebrew glossary), its per-type dispatch has no Hebrew-only
gate of its own - `Mode` gating alone decides whether it ever runs.

### 12.4 Why span-scope is safe where the field / whole-JSON Stage-2 was not

The value / whole-JSON-scoped Stage-2 LLM repair (section 2) ships OFF because handing a model a whole field
lets it re-flow text OUTSIDE the leak (and Hebraise JSON keys). The dynamic stage marks exactly ONE run and
asks for one token; the prefix and suffix around the marked span are spliced back verbatim by offset, so the
blast radius is a single token. The Stage-2 lesson was "wrong granularity", not "no LLM".

The d5 field-value-scope contrast (LOCAL gemma4:12b, whole value handed to the model with NO span marking)
makes the difference concrete - the model gets the leak right but ALSO rewrites unrelated prose outside it:

- `הדמות הראשית שקעה בתחושת confusion עמוקה כשהתגלתה לה האמת על אביה.`
  -> field-scope output `הדמות הראשית שקעה בתחושת בלבול עמוקה כאשר התגלתה לה האמת על אביה.`
  The leak `confusion` -> `בלבול` was correct, but the model ALSO reflowed `כשהתגלתה` into `כאשר התגלתה`
  OUTSIDE the leak (len 66 -> 65). Span-scope structurally cannot do this: only the marked run is replaced.
- `יחסה של הגיבורה אל אמה מלא ambivalence, בין אהבה עזה לכעס מר.`
  -> field-scope output `יחסה של הגיבורה כלפי אמה מאופיינת במתלבטות, בין אהבה עזה לכעס מר.`
  `אל אמה מלא` reflowed to `כלפי אמה מאופיינת`, again outside the leak (len 61 -> 65).

### 12.5 Routing, DI, and observability

- **Routing:** `AiTaskType.TermRepair` "routes to itself" (there is no `AnalysisType.TermRepair`, so
  `AnalysisTaskMapping` is untouched). `AiRouter.ShouldUseUnifiedInstructionVerbatim` includes `TermRepair`,
  so the marked-span instruction is sent VERBATIM under the analysis frame.
- **DI:** `builder.Services.AddScoped<DynamicTermRepairService>()`; injected into `UnifiedAnalysisService`
  and `BookReviewService`.
- **Integration entry points:** `RepairFieldsAsync` (walk a `RepairableField` list), `ApplyAsync` (per-type
  dispatch mirroring `GlossaryRepairPass.Apply` exactly - same repairable types, Proofread + BookReview left
  to their own paths - wired into `UnifiedAnalysisService.ApplyAnalysisRepairAsync`), and
  `RepairFindingsAsync` (the BookReview ENTITY path, mirroring `BookReviewService.ApplyGlossaryToFindings`).
  `RepairValueAsync` / `RepairRunsAsync` are the per-value primitives.
- **Observability:** a per-span Debug line (provider/model, offset, length, accepted/reverted, latency - NO
  run text / replacement / value is ever logged), and the per-run aggregate line now carries `mode=` and a
  `dynamicChanged=` counter alongside the glossary/LLM counters. Every fault is surfaced through
  `TermRepairValueResult.Fault` / `TermRepairResult.Fault` and logged, never silently swallowed.

## 13. Mode config + TermRepair routing

`Ai:AnalysisRepair.Mode` (enum `AnalysisRepairMode` in `Services/Ai/AiOptions.cs`) is layered UNDER the
existing `Enabled` / `PerType` gate (both still gate FIRST); `Mode` only decides WHICH stage(s) run once a
type has cleared that gate:

| `Mode` | Behavior |
|---|---|
| `Off` | An ADDITIONAL strict no-op on top of `Enabled` - neither the glossary nor the dynamic stage runs. |
| `Glossary` (**shipped default**) | The deterministic closed English<->Hebrew glossary ONLY; the dynamic span-scoped stage never runs. Reproduces the EXACT pre-follow-up behaviour - introducing the knob changes nothing about what ships today. |
| `Dynamic` | The glossary substitution is SKIPPED entirely; the span-scoped detect-classify-repair pass (d1-d3) runs over the original (un-glossaried) prose. |
| `GlossaryThenDynamic` | The glossary fast-path cache runs FIRST (cheap, deterministic, catches the closed ~35-term vocabulary at zero model cost), THEN the dynamic pass runs over whatever residual foreign text the glossary left - the two stages compose rather than compete. |

**Glossary demoted to a fast-path cache.** With the dynamic stage available, the closed glossary's role
under `GlossaryThenDynamic` is a zero-cost deterministic CACHE for its ~35 known 1:1 terms (`narrator`,
`tension`, `irony`, ...); the dynamic pass handles the open-ended tail the glossary cannot reach. The d6
non-regression check (section 14) confirmed the glossary still cleans its terms under `GlossaryThenDynamic`
and the dynamic stage does not undo them.

**TermRepair routing.**

- `Ai:FeatureModels:TermRepair` = `Ollama / gemma4:12b` (local; mirrors `AnalysisRepair`'s proven local
  repair model, same value-only "never invent, only substitute" contract, now span-scoped). A documented
  CLOUD override `OpenRouter / google/gemma-4-31b-it` is carried in the appsettings comment (set
  `Provider=OpenRouter`, `Model=google/gemma-4-31b-it` to use it), mirroring the Proofread / LinguisticAnalysis
  local+cloud idiom.
- `Ai:ProviderSettings:Ollama_TermRepair` = `{ Temperature 0.2, NumPredict 256, NumCtx 16384 }` - a tiny
  `NumPredict` because the output is one `{"replacement":"..."}` token; a large `NumCtx` comfortably fits the
  whole marked value + the verbatim instruction; the low temperature keeps the substitution conservative.
- **KEEP IN SYNC:** both the FeatureModel and the tuning block; both `appsettings.json` and
  `appsettings.Production.json` carry `Mode: Glossary`. Production inherits `FeatureModels` /
  `ProviderSettings` from the base by convention, so only the `Ai:AnalysisRepair` block is duplicated across
  the two files.

## 14. Measured decision + precision gates (d5, d6)

Both gates drove the SHIPPED `DynamicTermRepairService` (d1 detect -> d2 classify -> d3 span-scoped
TermRepair) on real GPU (local) AND real cloud, via `OutputQualityDiagnostic.MeasureDynamicTermRepair_LocalVsCloud`
(d5) and `.MeasureLegitimateTermPreservation_LocalVsCloud` (d6). All numbers below are transcribed verbatim
from the two run artifacts.

**Agreed bar: legitimate-term preservation >= 90% AND over-rewrite == 0 on every measured tier.**

### 14.1 d5 - out-of-glossary leak cleaning (recall + over-rewrite)

Measurement set: 10 Hebrew prose values (2 known real leaks + 8 seeded out-of-glossary), each leaking one
Latin run. `cleaned?` = the leak run is gone after repair (re-run the d1 detector); `over-rewrite?` = any
byte changed OUTSIDE the marked span.

| tier | model | cleaned % | over-rewrite (bar=0) | latency median / p90 (ms) | status |
|---|---|---|---|---|---|
| LOCAL | Ollama / gemma4:12b | 100% (10/10) | 0 | 2364 / 2527 | over-rewrite gate PASS |
| CLOUD | OpenRouter / google/gemma-4-31b-it | 100% (10/10) | 0 | 2314 / 14265 | over-rewrite gate PASS |

Both tiers cleaned 10/10 out-of-glossary leaks with over-rewrite 0. The span-scope-vs-field-scope contrast
that makes the 0 over-rewrite STRUCTURAL rather than lucky is in section 12.4.

### 14.2 d6 - legitimate-term preservation (precision / false positives)

Legitimate-term set: 18 values (15 Hebrew-native + 3 English-native) that MUST come back byte-identical; any
byte changed = a false positive (FP). "gated" = the detector allowlist or the d2 classifier LEAVEs the run
so it never reaches the model.

| # | class | token | runs / repair | predicted gate | LOCAL | CLOUD |
|---|---|---|---|---|---|---|
| 1 | proper-noun (Title-Case) | `Kafka` | 1 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 2 | proper-noun (Title-Case) | `Paris` | 1 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 3 | proper-noun (Title-Case) | `Orwell` | 1 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 4 | proper-noun (lowercase particle) | `van` | 3 / 1 | reaches model (1 run) | FP | preserved |
| 5 | proper-noun (lowercase particle) | `da` | 3 / 1 | reaches model (1 run) | FP | preserved |
| 6 | proper-noun (lowercase particle) | `de` | 3 / 1 | reaches model (1 run) | FP | FP |
| 7 | brand | `Kindle` | 1 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 8 | brand | `Photoshop` | 1 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 9 | brand | `Google` | 0 / 0 | detector-gated (allowlist/none) | preserved | preserved |
| 10 | acronym | `NASA` | 1 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 11 | acronym | `PDF` | 1 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 12 | intentional phrase (Title-Case title) | `Brave New World` | 3 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 13 | intentional phrase (lowercase code-switch) | `carpe diem` | 2 / 2 | reaches model (2 run) | FP | FP |
| 14 | url | `example.com` | 2 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 15 | email | `info@publisher.com` | 3 / 0 | classifier-gated (LEAVE) | preserved | preserved |
| 16 | hebrew-in-english (name) | `שרה` | 1 / 1 | reaches model (1 run) | preserved | preserved |
| 17 | hebrew-in-english (name) | `דוד` | 1 / 1 | reaches model (1 run) | preserved | preserved |
| 18 | hebrew-in-english (entity) | `ירושלים` | 1 / 0 | classifier-gated (LEAVE) | preserved | preserved |

Single-run decision vs the bar:

| tier | model | preservation % | over-rewrite (bar=0) | meets bar? |
|---|---|---|---|---|
| LOCAL | Ollama / gemma4:12b | 78% (14/18) | 0 | no |
| CLOUD | OpenRouter / google/gemma-4-31b-it | 89% (16/18) | 0 | no |

**Safety comes overwhelmingly from the deterministic GATE:** 12/18 values never reached the model (detector
allowlist + d2 classifier LEAVE) and were preserved identically on BOTH tiers. The tier choice only affects
the 6 model-reached values - the classifier carries the precision load. LOCAL's misses are exactly the d5
caveat, confirmed: it transliterated the lowercase name particles (`van` -> וואן, `da` -> דא,
`de` -> סימון דה בובואר) and translated the quoted idiom (`carpe diem` -> נצל הנא); CLOUD preserved `van` /
`da` but still misses `de` and `carpe diem`. Non-regression check (deterministic): the shipped glossary
still cleans `narrator` / `tension` / `irony` under `GlossaryThenDynamic` and the dynamic stage does not undo
them.

### 14.3 d6 - 5-run variance (precision discipline)

Because the reached-model tokens are decoded stochastically and the decision straddles the 90% bar, d6
characterised it over 5 back-to-back runs at the production TermRepair config rather than a single lucky
draw (mirror the proofread bake-off: report raw counts, never cherry-pick):

| run | LOCAL preserved | CLOUD preserved | CLOUD FPs | CLOUD verdict |
|---|---|---|---|---|
| 1 | 78% (14/18) | 94% (17/18) | carpe diem | PASS |
| 2 | 78% (14/18) | 89% (16/18) | de, carpe diem | HALT |
| 3 | 78% (14/18) | 89% (16/18) | de, carpe diem | HALT |
| 4 | 78% (14/18) | 94% (17/18) | carpe diem | PASS |
| 5 | 78% (14/18) | 89% (16/18) | de, carpe diem | HALT |

- **LOCAL** = 78% in ALL 5 runs (stable, decisively BELOW the bar). FPs every run: `van` -> וואן,
  `da` -> דא, `de` -> סימון דה בובואר / דה, `carpe diem` -> נצל הנא.
- **CLOUD** = {94, 89, 89, 94, 89}% -> **median 89%, fails the >= 90% bar in 3 of 5 runs**; its best draw is
  94% (17/18) and it NEVER reaches the high-90s. Two recurring FPs cap it: the Latin idiom `carpe diem` (FP
  every single run, both tiers) and the lowercase name-particle `de` in "Simone de Beauvoir" (FP 3/5 runs).
- **Over-rewrite = 0 on every tier in every run** - the span-scoped design's hard gate held throughout.

### 14.4 Verdict: HALT

Neither tier RELIABLY meets the agreed bar (LOCAL 78% stable FAIL; CLOUD median 89%, fails in the majority
(3/5) of runs and cannot be certified >= 90%). d5 had recommended default engine LOCAL + `Mode` =
`GlossaryThenDynamic`, but that was SUBJECT to d6, and the d6 precision gate overturned it. **=> Keep the
shipped default `Ai:AnalysisRepair.Mode = Glossary`** (deterministic glossary fast-path only). The dynamic
span-scoped stage stays wired and available (`Dynamic` / `GlossaryThenDynamic`) but OFF by default. The
precision floor is two narrow, addressable cases (section 16): (1) quoted lowercase foreign IDIOMS
(`carpe diem`) that are shape-indistinguishable from a leak, and (2) lowercase name PARTICLES (`de` / `da` /
`van`). 12/18 legit cases were preserved by the deterministic gate.

## 15. Rollout + kill-switch

**Default (the HALT outcome):** `Ai:AnalysisRepair.Mode = Glossary` in BOTH `appsettings.json` and
`appsettings.Production.json`. The dynamic span-scoped stage is shipped, DI-registered, wired at both seams
(the analysis seam via `ApplyAnalysisRepairAsync`, and the BookReview engine hook), and fully tested - but it
never executes at the default, so today's runtime behaviour is byte-identical to before the follow-up.

**To ENABLE dynamic repair per environment / tier** (only after the section-16 deferrals close, or on a tier
you have yourself measured at >= 90% preservation):

1. Set `Ai:AnalysisRepair.Mode` = `GlossaryThenDynamic` (recommended - keep the zero-cost glossary cache in
   front) or `Dynamic` (glossary skipped) in that environment's appsettings.
2. Point `Ai:FeatureModels:TermRepair` at the tier you validated (local `Ollama / gemma4:12b`, or the cloud
   `OpenRouter / google/gemma-4-31b-it`) and keep `Ai:ProviderSettings:Ollama_TermRepair` (or the cloud
   tuning) in sync.
3. Keep `Enabled = true` and the type allowed in `PerType`. BookReview is repaired via the engine hook
   (bidirectional glossary + dynamic; still NO value-scoped LLM regardless of `GuardOnly`).

**Kill-switch (fastest to broadest):**

- Set `Mode = Glossary` (or `Off`) - drops back to the deterministic glossary (or no stage) with zero model
  calls. This is the shipped posture.
- Set `Enabled = false` (or remove the `Ai:AnalysisRepair` block) - FULL no-op: neither glossary nor dynamic
  nor LLM runs; inputs byte-identical.
- Narrow the blast radius: remove a type from `PerType` to disable repair for just that analysis type.

Because the dynamic stage is fail-safe by construction (it can only leave a value cleaner or byte-identical,
never worse) AND is OFF at the default, enabling it is low-risk to trial and instantly reversible via `Mode`.

## 16. Residual deferrals + review-retro candidates

### 16.1 Deferrals (from the d6 gate - close these before flipping the default)

- **Quote-aware / do-not-translate gating for intentional foreign idioms.** A deliberately-quoted lowercase
  Latin idiom (`carpe diem`) is shape-indistinguishable from a lowercase out-of-glossary leak, so neither the
  d2 classifier nor the model reliably spares it (FP on BOTH tiers every run). Mitigation: quote-aware gating
  or a book-scoped do-not-translate / foreign-phrase allowlist. Not solved here - it is an inherent precision
  floor of the dynamic pass, not a tier defect.
- **Name-particle context rule in d2.** Lowercase name particles (`de` / `da` / `van`) sandwiched between two
  Title-Case runs ("Simone de Beauvoir", "Vincent van Gogh") reach the model and get transliterated /
  translated. A d2 rule to LEAVE a lowercase run that sits BETWEEN two Title-Case runs (a name-context
  signal) would spare them.
- **Book-scoped entity list.** The classifier already accepts an optional `bookEntities` set (always LEAVE)
  but no live entity list is wired yet; supplying real character / place names would sharpen the proper-noun
  skip and is the one lever that can spare a foreign HEBREW run (which has no case signal).
- **English-book (Hebrew-in-English) direction is under-measured.** Only 3 Latin-native values (2 reach the
  model); both model-reached Hebrew names (שרה, דוד) were preserved on both tiers, but the direction was not
  stress-tested with Hebrew common-concept words (where "translate" is arguably correct). Treat the high
  preservation as indicative, not proven; re-measure before enabling on English books.
- **Dynamic-repair determinism / caching.** The reached-model token is stochastic near the bar; a lower
  temperature and/or an optional per-(term, context) cache would make the pass more deterministic and cheaper
  on repeats.
- **Native-speaker validation of the Hebrew equivalents on the FP set** (mirror the proofread / repair-gold
  `c04` deferral in section 9): the measured equivalents were not native-speaker validated.

### 16.2 review-retro candidates (durable lessons worth feeding back to the reviewer / kit)

- **GPU-filter trap (naming collision).** The two new live-GPU + cloud diagnostics are `[Fact]`s named
  `MeasureDynamicTermRepair_LocalVsCloud` / `MeasureLegitimateTermPreservation_LocalVsCloud`, so a
  `dotnet test --filter "FullyQualifiedName~DynamicTermRepair"` intended to run the DETERMINISTIC
  `DynamicTermRepairServiceTests` ALSO matches the live diagnostic. Scope the deterministic filter to
  `~DynamicTermRepairServiceTests|~ForeignRunClassifier|~LatinInHebrew` (mirrors the existing BookReview
  test-filter GPU trap). The diagnostics self-skip when Ollama is unreachable and report BLOCKED (never fake)
  when the cloud tier is unreachable, so they are safe in CI - but the filter naming is the trap.
- **Precision-gate-with-multi-run-variance discipline.** When an LLM-backed decision straddles the
  acceptance bar, a single run is not evidence - report raw counts over N back-to-back runs and certify on the
  DISTRIBUTION, not one draw. d6 caught a 94%-looking "PASS" draw that was really a median-89% FAIL.
- **Span-scope vs field-scope = the reusable "granularity makes an LLM repair safe" lesson.** Turning off an
  over-rewriting LLM stage was the wrong final conclusion; shrinking its blast radius to one marked token
  (structure held by code, prefix/suffix spliced verbatim by offset) was the right one. Generalise: when an
  LLM edit over-reaches, narrow the scope before abandoning the model.
