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
to the open-ended tail of foreign-vocabulary leaks the 23-term glossary cannot reach - see sections 12
(architecture), 13 (Mode config + TermRepair routing), 14 (the original measured decision + precision
gates), 15 (rollout + kill-switch), and 16 (residual deferrals + review-retro candidates). That stage
first shipped wired but OFF because the d6 precision gate HALTED the flip (neither the local nor the cloud
tier reliably preserved legitimate foreign terms at the agreed >= 90% bar).

A FOURTH follow-up (`src/.cursor/plans/_todo/dynamic-term-repair-precision-followup-2026-07-11.plan.md`,
todos e1-e6) then sharpened the DETERMINISTIC skip-gate (quote-aware + name-particle LEAVE rules and an
auto per-book entity list), re-measured both tiers at 100% legitimate-term preservation, 100% out-of-glossary
cleaning, and 0 over-rewrite, and **flipped the shipped default to
`Ai:AnalysisRepair.Mode = GlossaryThenDynamic` on the LOCAL tier** (`Ollama / gemma4:12b`), so the dynamic
stage now runs by default. See **section 17** for the sharpened gate, the re-measured tables, and the rollout
decision; sections 14-16 are retained as the original HALT record.

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

> **A THIRD stage was added later (sections 12-17).** The dynamic-term-repair follow-up added a span-scoped
> detect-classify-repair stage (`DynamicTermRepairService`) selectable via the `Ai:AnalysisRepair.Mode` knob.
> It first shipped OFF (`Mode=Glossary`), but the precision follow-up (section 17) flipped the shipped default
> to `Mode=GlossaryThenDynamic` on the LOCAL tier, so the dynamic stage now runs AFTER the glossary by default.
> The two deterministic stages below are still the glossary fast-path; the dynamic stage is documented in
> section 12 onward.

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
    "Custom": false,           // DELIBERATELY excluded (d1, narrowed by c1) - see 4.1 / 4.2
    "Synopsis": true,          // f2 (2026-07-28) - enabled after q2 cleared the 18.2 bar; see 4.1 / 4.2
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

### 4.1 The `PerType` decision table - every `AnalysisType`, not just the ones that ship

`PerType` is a decision table over the WHOLE `AnalysisType` enum (12 members), not merely a switch that
happens to list the keys that ship. Every member is either **Repaired** (present-and-`true` in `PerType`, in
BOTH `appsettings.json` and `appsettings.Production.json`, AND a dispatch target in the **three** switches
below, or covered by a recorded, reasoned exception) or
**DeliberatelyExcluded** (absent, or present-and-`false`, with a stated reason). This table was established
by the `analysis-repair-pertype-coverage-holes-2026-07-28` plan's `i1` investigation, `d1` decision and `q1`
quality gate, which found the coverage picture is **four unrepaired types plus one dead allowlist key**, not
the two (`Synopsis`, `Custom`) it started from - and then **revised on 2026-07-28** by the follow-up plan
`analysis-repair-coverage-followups-2026-07-28` (`f1` adjudicated `BookOverview`, `c1` measured `Custom`, `c2`
closed the profile path's dynamic half, `c3` added classifier rule (7b), `q2` measured all of it and `f2`
enabled `Synopsis`). **Ten of the twelve members are now Repaired** - nine of them through the dispatch
switches, plus `BookReview` through its own engine hook - **and two are DeliberatelyExcluded (`Custom`,
`Proofread`).**

> **There are THREE per-type dispatch switches, not two** (`be-c01`, 2026-07-28). `GlossaryRepairPass.Apply`
> (deterministic glossary stage) and `DynamicTermRepairService.ApplyAsync` (span-scoped dynamic stage) both
> run under the shipped `GuardOnly=true`; `AnalysisRepairService.RepairAnalysisAsync` (value-scoped LLM
> stage) is the third and runs only in state (3), `GuardOnly=false`. All three sit behind the SAME
> `AnalysisRepairGate.Evaluate` allowlist, so one `PerType` key opens the gate onto all three. Eight of the
> nine dispatch-repaired types are arms in all three; **`Synopsis` is deliberately in the two deterministic
> ones only** - see its row below.

| `AnalysisType` | verdict | reason |
|---|---|---|
| `Proofread` | DeliberatelyExcluded, by design | Output quotes verbatim manuscript spans (`original`/`suggested`/`span`); repairing them would corrupt the suggestion diff. Stays ABSENT from `PerType` rather than explicit-`false` - see the note at the end of `4.2`. |
| `LineEdit` | Repaired | dispatch arm in all three switches; real producer seam confirmed |
| `LinguisticAnalysis` | Repaired | dispatch arm in all three switches; real producer seam confirmed |
| `LiteraryAnalysis` | Repaired | dispatch arm in all three switches; real producer seam confirmed |
| `BookOverview` | Repaired, but a NO-OP on the profile path (f1, KEEP + document) | `"BookOverview": true` is a dispatch target in all three switches, but its only real producer (`BuildBookProfileAsync` via `RunRawAsync(structuredJson: null)`) blank-guards BOTH stages to a no-op, and its one repairable field (`Summary`) is discarded before persistence anyway - the key is not wrong, just unreachable on the product path. It is NOT dead on every path, which is why the key is KEPT rather than removed: `AnalysisController.TryParseAnalysisType` (`AnalysisController.cs:151-156`) is a bare `Enum.TryParse`, so a request carrying `analysisType:"BookOverview"` resolves at `ResolveAnalysisParamsAsync` (:131-132) and reaches `UnifiedAnalysisService.RunAsync` (`AnalysisController.cs:66`) - the PERSISTED, structured-JSON-producing seam, where `TryParseStructured` (`UnifiedAnalysisService.cs:2322`) parses a real `BookOverviewResult` and the SAME glossary arm genuinely repairs `Summary`. Removing the key would silently disable that reachable (if unadvertised) path, which is strictly worse than a documented no-op on the profile path - see the plan's `## f1 decision`. **Dead config on the profile path only**, pinned at both seams by `Pagedraft.Api.Tests/BookOverviewRepairPathAsymmetryTests.cs` (`RunAsync_BookOverview_UnderTheShippedConfig_RepairsSummary` / `RunRawAsync_BookOverview_UnderTheShippedConfig_LeavesLeakUntouched`), not a hole - a different defect with a different fix (none, since there is nothing left to repair once `Summary` is discarded). |
| `Synopsis` | **Repaired since `f2` (2026-07-28)** - was a measured HALT, now a measured PASS | `q1` HALTED it at 83% preservation (5/6) with 1 false positive (the repair model TRANSLITERATED `Chekhov` -> `צ'כוב` at a paragraph head), against the conjunction bar (preservation >= 90% AND over-rewrite exactly 0). `c3` then landed **classifier rule (7b)** - a Title-Case Latin run at a LINE HEAD (any hard line break, blank line NOT required - see `be-c03` in `4.2`) is LEAVE, additive by construction - and `q2` re-measured **on q1's own fixtures**: preservation **100% (6/6)**, false positives **0**, over-rewrite **0**, cleaning **100% (3/3)**, LOCAL `Ollama \| gemma4:12b` with cloud `gemma-4-31b-it` identical, and **zero** recall cost on the shipped eight. `f2` wired it: `"Synopsis": true` in both files plus a **PLAIN-TEXT** dispatch arm in the two DETERMINISTIC switches (`TryParseStructured` returns null for Synopsis, so the whole prose value is the repairable surface - the same shape as `Summarization`, NOT a structured DTO). Because the arm is plain-text, this covers BOTH producer paths: the profile build (`BuildBookProfileAsync` -> `RunRawAsync(structuredJson: null)` -> `CompleteDeferredRepairAsync`) and a direct `POST /analyze`. **It is the one Repaired type whose routed model differs from `TermRepair`'s** (`Summarization` -> qwen3.5:9b vs gemma4:12b), so a LEAKING synopsis costs a cross-model GPU swap - accepted knowingly, see `4.2` and `19.4`. **DELIBERATELY NOT an arm in the THIRD switch** (`AnalysisRepairService.RepairAnalysisAsync`, the value-scoped LLM stage behind `GuardOnly=false`) - `be-c01`, 2026-07-28: that stage never consults `ForeignRunClassifier`, so rule (7b), the deterministic LEAVE that q2's 100% preservation is a property of, cannot protect it; it hands the WHOLE value to the model, and its validator (no NEW Latin run, length ratio 0.6-1.6) does not catch a transliteration, which REMOVES a Latin run and barely moves the length. The only measurement of a repair model rewriting Synopsis prose is q1's 83% / 1-FP HALT, so an arm there would put the failing configuration one config line away. The exclusion is NAMED at that switch's `default:` arm (with its reversal condition: re-run the 18.2 gate through that service, or give it a rule-(7b) equivalent) and pinned by `AnalysisRepairExclusionRegressionTests.ShippedSynopsis_IsDeliberatelyOutsideTheValueScopedLlmStage`. This makes Synopsis the SECOND "Repaired but outside the value-scoped stage" case after `BookReview` - see the `GuardOnly` asymmetry note in `4.2`. |
| `CharacterAnalysis` | Repaired, and **FULLY covered on the profile path since `c2`** | was glossary-only on `BookProfile.CharactersJson`; `c2` (2026-07-28) made `RepairStructuredProfileJson` **two-stage + instance + async** (`RepairStructuredProfileJsonAsync<T>`), mirroring `BookReviewService`'s engine hook one-for-one (glossary gated by `RunsGlossary()`, dynamic by `RunsDynamic()`, entity set from `IBookEntityProvider`, per-stage fail-safe). Measured by `q2` scope (i): persisted-JSON cleaning **10/10** (pre-`c2` **0/10**), preservation **18/18**, over-rewrite **0**. **NO GPU-swap surface added** - see `4.2`. |
| `StoryAnalysis` | Repaired, and **FULLY covered on the profile path since `c2`** | identical shape to `CharacterAnalysis`; same hook, same measurement (both types are in the same `q2` scope (i) corpus) |
| `BookReview` | Repaired | via its own engine hooks (glossary + dynamic), NOT a dispatch-switch arm - it is deliberately in none of the three switches |
| `Summarization` | Repaired | plain-text dispatch arm in all three switches; real producer seam confirmed |
| `QA` | Repaired | dispatch arm in all three switches; real producer seam confirmed |
| `Custom` | DeliberatelyExcluded, by decision (`d1`), **reasoning NARROWED by `c1`'s measurement** | its instruction is user-authored, so its output is legitimately English / bilingual / quoted / tabular, and the layer makes one uncapped sequential model call per foreign WORD. `c1` then ran `d1`'s own falsifier over the real `AnalysisResults` corpus: **n = 16 rows / 6 distinct instructions / 1 user / 2 books**, Hebrew script fraction **1.0000 on every row**, max offline REPAIR-classified run count **0**, rows with >= 1 repair run **0/16 (Wilson 95% CI 0.0-19.4%)**. So the BENEFIT side is empty on real data, not just the cost side unsampled. **The exclusion is enforced STRUCTURALLY, not by this key:** `Custom` has no dispatch arm in ANY of the three switches - `GlossaryRepairPass.Apply`, `DynamicTermRepairService.ApplyAsync` or `AnalysisRepairService.RepairAnalysisAsync` (all three drop it into their fallback arm), so flipping `"Custom": false` -> `true` today would change nothing at all. Explicit `"Custom": false` in `PerType` in both files documents the intent. See `4.2` below. |

**The outer `PerType` key is necessary but NOT sufficient.** A type absent from a non-empty `PerType` map is
skipped at the FIRST gate - `UnifiedAnalysisService.ApplyAnalysisRepairAsync`'s call into
`AnalysisRepairGate.Evaluate` (`Services/Ai/AiOptions.cs`) - and until 2026-07-28 this skip was completely
SILENT: no log line at all, so a missing key was indistinguishable from a clean run. It is now Debug-logged,
naming the type AND the closing sub-condition (`NullConfig` / `Disabled` / `PerTypeExcluded`). A production
run emits at most ONE such line per repair site: `UnifiedAnalysisService.ApplyAnalysisRepairAsync`,
`BookIntelligenceService.RepairStructuredProfileJsonAsync`, and `BookReviewService.BuildBookReviewAsync`'s single
per-build LAYER gate, which covers its glossary AND dynamic hooks in one line naming both stages. (There is a
fourth `Evaluate` call - `ApplyGlossaryToFindings`' own internal gate - kept as defence-in-depth for direct
callers; the layer gate short-circuits ahead of it on the engine path, so it logs only when driven directly.)

> The BookReview line was two lines until be-c02, and they were asymmetric: the glossary one lived INSIDE
> `ApplyGlossaryToFindings`, which `Mode=Off` / `Mode=Dynamic` never call, so under two of the four Modes a
> closed gate emitted exactly ONE line naming only the DYNAMIC stage - an operator read "the dynamic stage
> was gated out" when the whole layer was, and `Mode=GlossaryThenDynamic` logged the same reason TWICE.
> `Enabled`/`PerType` is a whole-LAYER knob, so it is now evaluated and logged ONCE, above the Mode check,
> mirroring `ApplyAnalysisRepairAsync`. `ApplyGlossaryToFindings` KEEPS its own identical internal gate as
> defence-in-depth for direct callers/tests; on the engine path the hoist short-circuits ahead of it.

Beyond that gate, THREE services EACH carry their OWN per-type dispatch switch:

| # | switch | stage | runs when |
|---|---|---|---|
| 1 | `GlossaryRepairPass.Apply` | deterministic glossary | `Mode` is `Glossary` / `GlossaryThenDynamic` |
| 2 | `DynamicTermRepairService.ApplyAsync` | span-scoped dynamic | `Mode` is `Dynamic` / `GlossaryThenDynamic` |
| 3 | `AnalysisRepairService.RepairAnalysisAsync` | value-scoped LLM | `GuardOnly=false` only (state (3), NOT shipped) |

Switches 1 and 2 carry **the same NINE arms** since `f2`: Summarization, **Synopsis**, LiteraryAnalysis,
LinguisticAnalysis, LineEdit, BookOverview, CharacterAnalysis, StoryAnalysis, QA. Switch 3 carries **EIGHT** -
the same nine minus `Synopsis`, which is a NAMED exclusion there (`be-c01`; the argument and its reversal
condition are stated at that switch's `default:` arm, summarised in the `Synopsis` row above). `BookReview` is
in none of the three, by design, since it runs on its own engine-hook path. **Summarization and Synopsis are
the two PLAIN-TEXT arms** (`RepairPlainText` / `ApplyPlainTextAsync` over `RepairableFields.ForPlainText`);
the other seven are structured (`RepairStructured<T>` / `ApplyStructuredAsync<T>` over the type's
`RepairableFields.For` overload).
That distinction is load-bearing, not cosmetic: a plain-text arm still runs when `structuredJson` is null,
which is exactly what makes `Synopsis` repairable on the profile-build path where `BookOverview` is a no-op.

A `PerType` key with no matching arm in one or more of the three switches is dead config that opens a gate
onto a no-op
(`"BookOverview": true` on the profile path is the shipped example above, and `"Custom"` would be a second one
if it were ever flipped without adding arms). Conversely, a dispatch arm with no `PerType` key is unreachable
code. Getting either half wrong reproduces this defect class. A type wired into SOME switches and not others
is the same defect in partial form: it is acceptable only as a RECORDED decision, which is why
`AnalysisRepairConfigParityTests.DispatchCoverageFor` now models all three switches and demands a stated
reason for any uneven coverage.

**The guard against this whole class of hole returning silently:**
`AnalysisRepairConfigParityTests.EveryAnalysisType_HasAnExplicitRepairCoverageDecision` (h2's
enum-completeness oracle - a hand-authored decision table over `Enum.GetValues<AnalysisType>()` that THROWS
for a new enum member with no decision, so it cannot pass vacuously by deriving its expectation from the
shipped map), its sibling `EveryRepairedType_HasAnExplicitDispatchCoverageDecision` (which since `be-c01`
models ALL THREE switches and, rather than only forcing a hand-written verdict, PARSES the real
`AnalysisType` arm labels out of each of the three switch sources and fails when the table and the code
disagree - the half-enable `f2` shipped was invisible to the two-switch version), and
`AnalysisRepairExclusionRegressionTests` (e1's per-type pins, which drive the real config plus the real
dispatch switches plus `Custom`'s actual producer seam, and go RED the moment an exclusion is silently
enabled). Those pins are **symmetric**: when `f2` enabled `Synopsis`, the three "Synopsis is untouched"
assertions were FLIPPED rather than deleted, into `ShippedSynopsisCoverage_IsWiredAtEveryDeterministicEnablingSurface`
(allowlist + gate predicate + both DETERMINISTIC dispatch arms, so a half-enable or a silent un-wiring is
red), `ShippedSynopsis_IsDeliberatelyOutsideTheValueScopedLlmStage` (the opposite direction: the third
switch must NOT repair Synopsis, with a Summarization control on the same fixture proving that stage was
live) and
`ShippedSynopsis_ParagraphHeadProperNoun_NeverReachesTheRepairModel` (the rule-(7b) property that made the
enable safe, with a value-initial non-vacuity control that DOES reach the model). The profile-path pin
`BookIntelligenceProfileRepairTests.BuildBookProfileAsync_UnderTheShippedConfig_RepairsSynopsis` was flipped
the same way.

### 4.2 What has been measured, and what has not

The bar throughout is section 18.2's: **preservation >= 90% AND over-rewrite exactly 0**, on the shipped LOCAL
tier (`Ollama | gemma4:12b`), measured with `OutputQualityDiagnostic.MeasureLegitimateTermPreservation_LocalVsCloud`
(d6) / `.MeasureDynamicTermRepair_LocalVsCloud` (d5) and, for the profile path, `.MeasureProfilePathTermRepair_Local`,
with the entity set obtained by CALLING `BookEntityProvider.GetEntitiesAsync` over seeded books - never
hand-authored. It is a CONJUNCTION: a single over-rewrite is a HALT regardless of preservation.

#### `Synopsis`: HALT (q1), then PASS (q2). Both runs, in order.

| run | tier | preservation | FPs | over-rewrite (bar 0) | cleaning | reached model | sample size | verdict |
|---|---|---|---|---|---|---|---|---|
| `q1` 2026-07-28 | LOCAL `Ollama \| gemma4:12b` | 83% (5/6) | 1 | 0 | 100% (3/3) | 1 of 6 | 6 preservation + 3 cleaning values | **HALT** - fails the >= 90% half |
| `q1` 2026-07-28 | CLOUD `gemma-4-31b-it` | 83% (5/6), identical | 1 | 0 | - | 1 of 6 | same fixtures | same HALT, reproduced |
| **`q2` 2026-07-28** (after `c3`) | **LOCAL `Ollama \| gemma4:12b`** | **100% (6/6)** | **0** | **0** | **100% (3/3)** | **0 of 6** | **the SAME 6 + 3 values, byte-identical** | **PASS** |
| `q2` 2026-07-28 | CLOUD `gemma-4-31b-it` | 100% (6/6) | 0 | 0 | 100% (3/3) | 0 of 6 | same fixtures | PASS, reproduced |

**q2 measures the CHANGE, not a friendlier corpus.** It re-ran `PreservationFixtureBooks.SynopsisCases` /
`.SynopsisLeakCases` / `SeedSynopsisBook` - the fixtures q1 itself built - with **not one VALUE character
changed** (only `GatingEntity` metadata and comments, which feed the report's findings line and never a
preservation / FP / over-rewrite / attribution count).

**What changed between the two runs was one classifier rule.** q1's single false positive was a legitimate
proper noun TRANSLITERATED at a paragraph head (`Chekhov` -> `צ'כוב`). `ForeignRunClassifier`'s Title-Case
LEAVE rule (7) is **mid-sentence-only by design** - in English orthography a sentence-initial capital is
carried by every word, so it is no evidence of a proper noun - and `BookEntityProvider` harvests from the
manuscript ONLY, so the external authors/works/places a synopsis legitimately names can never enter the
per-book LEAVE set. `c3` added **rule (7b)**: in a Hebrew-expected value, a Title-Case Latin run whose only
left-neighbours back to a HARD LINE BREAK are horizontal whitespace or transparent opening punctuation is
LEAVE. Its argument is deliberately a different KIND from rule (7)'s - not orthographic but **discourse**: a
completed sentence followed by a deliberate hard line break opens a new discourse unit, which is a planned
move authored in the target language, and what appears there is a topic-fronted named entity, whereas a
vocabulary LEAK is an in-flight lexical failure inside a clause the model has already begun
(`תחושת confusion`). Two positions are deliberately NOT claimed and each has its own
boundary test: **value-initial** (a short field can plausibly open with a capitalized leak, and there is no
line boundary to argue from) and **a sentence head mid-line** (rule (7)'s case, unchanged).

> **A LINE head, not a PARAGRAPH head - and the measured corpus does not cover the difference (`be-c03`).**
> The predicate (`ScriptTokenPredicates.IsLineInitial`) fires on ONE hard line break; it does NOT require a
> blank line. It was originally described in paragraph terms, which the corpus made easy to miss: **every**
> value in `PreservationFixtureBooks.SynopsisCases` / `.SynopsisLeakCases` uses `\n\n`, and the d5/d6 corpora
> (`.LeakCases`, `.Cases`) carry no line break at all - so `q2` scope (iii)'s "zero recall cost" and the
> model-free pin `Rule7b_SparesNoLeakInEitherD5CleaningCorpus` speak to the **blank-line shape only**. The
> breadth is nonetheless the right call, because it is what the DATA is: read-only against the runtime DB,
> **3 of 3** `BookProfiles.Synopsis` values (each ~1.2-1.6k chars with exactly 2 LF and 0 CR) and **32 of 33**
> `ChunkSummaries.SummaryText` values separate their paragraphs with a **single `\n`** and **ZERO** carry a
> blank line. Requiring a blank line would make rule (7b) dead on every real Synopsis and Summary value and
> re-open q1's false positive on exactly the production shape. The honest residual is a **soft wrap that falls
> right after a sentence terminator**; it is bounded to Title-Case Latin tokens, it can only send FEWER runs
> to the model, and it is now pinned by
> `ForeignRunClassifierTests.TitleCaseWord_AfterSingleLineBreakFollowingSentenceEnd_IsLeave` rather than left
> accidental. Note also that the shapes this breadth is most often suspected of - a soft wrap mid-sentence, a
> wrapped line, a newline-separated list line - are **already LEAVE without (7b), via rule (7)**, because in
> each of them the previous line does not end on a sentence terminator, so `IsSentenceInitial` is false and
> rule (7) fires first. Narrowing (7b) would not recover any of them. (Related correction: rule (7b) is
> additive over rule (7) by EVALUATION ORDER, not because `IsLineInitial` is a subset of `IsSentenceInitial` -
> neither predicate contains the other, since `IsSentenceInitial` tests `char.IsWhiteSpace` first and
> therefore never reaches the `'\n'`/`'\r'` entries in `IsSentenceTerminator`.)

**Rule (7b) is ADDITIVE by construction, and its measured recall cost is ZERO.** It is a new `return Leave`
placed AFTER rule (7), on a path that previously fell through to `return Repair`, so it converts REPAIR ->
LEAVE and never the reverse: no run that is LEAVE today becomes REPAIR. Preservation and over-rewrite
therefore cannot get worse for ANY type (a spared run is never sent to the model, and only a model call can
rewrite a byte), and the entire blast radius is d5 CLEANING (recall). `q2`'s regression scope measured that
radius directly and it is empty - see the table below.

> **HOW TO READ `Synopsis`'s 100%.** **0 of the 6 legitimate values reach the model** (attribution: 1
> entity-gated, 5 classifier-rule-gated, 0 model), so this is a property of the **DETERMINISTIC GATE**, not of
> `gemma4:12b`. Rule (7b) does not make the model better at preserving proper nouns - **it stops asking it.**
> The number clears the shipped bar legitimately (the bar is about the OUTPUT, not about who earned it), but
> *"Synopsis is safe because the model handles it"* would be a FALSE statement. The correct statement is
> *"Synopsis is safe because the classifier no longer hands these values to the model."* This is the same
> qualifier section 18.2 already carries for the shipped 21.

**Enabling `Synopsis` was a deliberate cost decision, not a config flip (`f2`).** Precision was never the only
objection. `Synopsis` routes to `AiTaskType.Summarization` -> **qwen3.5:9b** while `Ai:FeatureModels:TermRepair`
is **gemma4:12b**, so on this `OLLAMA_MAX_LOADED_MODELS=1` host every LEAKING synopsis costs a cross-model cold
load (~21.5-34.3 s, asymmetric - section 19.2) plus a load back on the next Summarization-family call. It was
accepted on four grounds, all measured rather than argued: (1) the cost is bounded to **genuine leaks** - a
clean synopsis makes ZERO model calls (section 19.1) and q2 measured **0 of 6** legitimate values reaching the
model, while the 3/3 cleaning cases did; (2) `Synopsis` is produced **once per profile build**, not once per
chapter like `Summarization` - on this project's 80-chapter corpus that is ~1/80th the production frequency;
(3) `BuildBookProfileAsync` already interleaves qwen3.5:9b (Synopsis) and gemma4:12b (BookOverview /
CharacterAnalysis / StoryAnalysis) **generation** in one `Task.WhenAll`, so that path already swaps with zero
repair involvement; (4) it is the same trade `s4` already ACCEPTED for Line Edit and Summarize (section 19.4),
re-confirmed by `s5r`. **Kill-switch:** set `"Synopsis": false` in BOTH appsettings files (the dispatch arms
may stay - the `PerType` gate closes ahead of them).

**Residual risk, stated so it is not forgotten.** Rule (7b) narrows q1's failure class, it does not eliminate
it: an external proper noun at a **mid-line sentence head** still reaches the repair model, and a synopsis
is the value shape most likely to contain one. Closing that needs lexical knowledge the layer does not have -
an English common-noun lexicon (a CLOSED list is the exact failure `LiteraryTermGlossary`'s 23 terms already
demonstrate at 0/10 on the d5 out-of-glossary corpus), a cross-value entity source harvested from analysis
OUTPUT rather than the manuscript (non-additive; an over-eager harvest spares real leaks - the be-c04 failure
mode), or a transliteration-aware validator with a domain exemption (also a closed list). None is cheap and
none is additive; `c3` took the one position where a purely structural argument was available.

#### `c2`: the persisted profile path (q2 scope (i))

`CharacterAnalysis` / `StoryAnalysis` were allowlisted and dispatchable, yet their PERSISTED profile JSON got
only the deterministic glossary: `RepairStructuredProfileJson` called `GlossaryRepairPass.RepairFields` and
nothing else, so the span-scoped dynamic stage - half of the shipped `Mode=GlossaryThenDynamic` - never reached
`BookProfile.CharactersJson` / `StoryStructureJson`. Since the glossary is a CLOSED 23-term map and the d5
corpus is real out-of-glossary leaks, that path received **0%** of the cleaning capability the be-c08 gate
measured. `c2` made the hook **two-stage + instance + async**, mirroring `BookReviewService`'s engine hook
one-for-one (`RunsGlossary()` / `RunsDynamic()` predicates, `IBookEntityProvider` for the LEAVE set, per-stage
fail-safe) rather than inventing a third shape.

| sub-scope | corpus | result | over-rewrite (bar 0) | reached model | n |
|---|---|---|---|---|---|
| (i-a) CLEANING, persisted JSON | 10 out-of-glossary leaks planted in whitelisted prose fields | **cleaned 10/10 (100%)** - pre-`c2` baseline **0/10** | **0** | 10/10 | 10 |
| (i-b) PRESERVATION, persisted JSON | 18 Hebrew-direction legitimate values | **18/18 (100%)**, FPs **0** | **0** | **0/18** | 18 |

Read off `BookProfile.CharactersJson` / `StoryStructureJson` after a REAL `BuildBookProfileAsync`, through the
real `RepairableFields` accessors and the real reserialize. Gate attribution for (i-b): **0 entity-gated, 18
classifier/detector-rule-gated, 0 model** - so this 100% is a GATE property too.

**NO GPU-SWAP SURFACE IS ADDED BY `c2`, and that is the load-bearing difference from `Synopsis`.** Both
`CharacterAnalysis` and `StoryAnalysis` route to `AiTaskType.LinguisticAnalysis` -> **gemma4:12b**, the SAME
model `Ai:FeatureModels:TermRepair` resolves, so a repair there costs no cross-model load at all. This is
stated in the code (`BookIntelligenceService.RepairStructuredProfileJsonAsync`'s doc comment) so it is not
re-litigated.

**Three honest limits of scope (i)**, none of which moves the verdict: (1) the 3 **Latin-expected**
(Hebrew-in-English) shipped cases are STRUCTURALLY unreachable on this path - the hook derives `ExpectedScript`
from the ANALYSIS language, so a Hebrew profile build cannot exercise that direction - which is why the
preservation denominator is **18, not 21**, and those 3 are precisely the only entity-load-bearing cases in the
shipped set. The entity lever is measured as PRESENT and correctly harvested here, but **not load-bearing on
this path**. (2) The entity-set counts printed in the scope (i) artifact were read AFTER the build, so they
include names the build itself persisted (be-c03 invalidation + re-harvest); the set the hook USED was the
smaller pre-build one. That cannot have flattered any number - the extra entries are Hebrew tokens, which
cannot gate a Latin run in a Hebrew-expected value, and all 10 leaks reached the model anyway. (3) The scope (i)
instrument, `OutputQualityDiagnostic.MeasureProfilePathTermRepair_Local`, has NO CLOUD arm, unlike its
`_LocalVsCloud` siblings (`MeasureDynamicTermRepair_LocalVsCloud`, `MeasureLegitimateTermPreservation_LocalVsCloud`,
and `DumpRealAnalysisOutputs_AndPrototypeRepairPass`): its figures are LOCAL-only (`Ollama | gemma4:12b`) and were
never reproduced on cloud `gemma-4-31b-it`, unlike scopes (ii) and (iii), for which this section states cloud
reproduction. The method name is honest and the `c2` table makes no cloud claim, but every other scope in this
section records cloud reproduction, so the silence reads as an omission rather than a stated limit. It does not
move the verdict because scope (i)'s cleaning half is the only stochastic component in the whole gate, and it
came back 10/10; the preservation half is fully deterministically gated, 0 of 18 values reaching the model, so a
second tier could not have disagreed with it.

**One deliberate behaviour change beyond "add a stage."** Before `c2` the profile hook had **no `Mode` gate at
all**: with the layer gate open it ran the glossary even under `Mode=Off` and `Mode=Dynamic` - the same
Mode-contract violation `be-c06` fixed in `BookReviewService`. `Mode=Off` (with `Enabled=true`) is now a strict
**repair** no-op. The `JsonSerializer.Serialize` reserialize still runs whenever the LAYER gate is open,
deliberately: it is what strips the model's ```` ```json ```` fence, and the FE parses `profile.charactersJson`
with a bare `JSON.parse`. A strict BYTE no-op is what a CLOSED layer gate gives (raw string, fence intact),
unchanged.

#### `c3` regression scope (q2 scope (iii)): the shipped eight, unchanged

`c3` edits SHARED classifier code that every repaired type flows through, so the shipped eight's d5/d6 subsets
were re-run against the section 18.2 figures of record. The decision rule was fixed in advance: **a regression
here reverts `c3` regardless of how well `Synopsis` scored.**

| gate | 18.2 figure of record | q2 measured (LOCAL) | delta |
|---|---|---|---|
| d5 CLEANING, ARM A (entity-free) | 10/10, over-rewrite 0, **10 model calls** | 10/10, over-rewrite 0, **10 model calls** | **UNCHANGED** |
| d5 CLEANING, ARM B (real provider set, adversarial book) | 10/10, over-rewrite 0, **10 model calls** | 10/10, over-rewrite 0, **10 model calls** | **UNCHANGED** |
| d5 leaks SPARED by the entity lever | 0 | **0** | **UNCHANGED** |
| d6 PRESERVATION | 100% (21/21), FPs 0, over-rewrite 0, 0 reaching the model | 100% (21/21), FPs 0, over-rewrite 0, 0 reaching the model | **UNCHANGED** |
| d6 gate attribution | 3 entity-gated, 18 rule-gated | 3 entity-gated, 18 rule-gated | **UNCHANGED** |

CLOUD reproduced every row identically. **How the regression signal was attributed, pre-registered before the
numbers were read:** `c3` is a DETERMINISTIC classifier change, so the only thing it can do to this scope is
change WHICH runs are sent to the model. The c3-attributable signal is therefore the **model-call / repair-run
count**, not the stochastic `cleaned` count - model calls held at exactly **10** on both arms and both tiers and
**0** leaks were spared, so **`c3` cost exactly zero recall on the shipped corpus.** (Had `cleaned` moved while
model calls held at 10, that would have been model variance, not a `c3` regression.) It is also pinned
model-free by `ForeignRunClassifierTests.Rule7b_SparesNoLeakInEitherD5CleaningCorpus`.

#### `Custom`: measured on real data, still EXCLUDED

`d1` excluded `Custom` on premise and named its own falsifier; `c1` ran it - **no GPU, no model call**, driving
the REAL `LatinInHebrewContentDetector.DetectForeignRuns` + `ForeignRunClassifier.RunsToRepair` over the stored
`ResultText` of every `Custom` row in the runtime DB.

| quantity | count | n | p-hat | Wilson 95% CI |
|---|---|---|---|---|
| Custom rows with **>= 1 REPAIR run** (would cost >= 1 model call) | 0 | 16 | 0.0% | **0.0 - 19.4%** |
| ...restricted to SUBSTANTIVE rows (>= 100 chars) | 0 | 7 | 0.0% | 0.0 - 35.4% |
| ...per DISTINCT instruction rather than per row | 0 | 6 | 0.0% | 0.0 - 39.0% |
| Custom rows whose **instruction** was written in English | 3 | 16 | 18.8% | 6.6 - 43.0% |
| Custom rows whose **output** was foreign-script (the case `d1` protects) | 0 | 16 | 0.0% | **0.0 - 19.4%** |

Hebrew script fraction was a **point mass at 1.0000** - min = max = mean = median, not one Latin letter in
~10,700 characters across the 16 rows - and the max REPAIR-classified run count was **0**, so the deterministic
gate would hand the model zero spans on every row. `bookEntities: null` was used deliberately: an entity set can
only move runs from REPAIR to LEAVE, so these counts are the **upper bound**.

**Verdict: EXCLUDE stands, reasoning NARROWED.** Literally, the data meets `d1`'s stated falsifier trigger
("overwhelmingly Hebrew with single-digit run counts"), and that is recorded rather than hidden. But meeting the
trigger does not make a script-ratio gate worth building: with max REPAIR-run count 0, an enabled `Custom`
repair would clean **nothing** on this corpus, so the measured benefit is exactly zero, while a gate placed in
front of the SHARED repair would change behaviour for the nine already-measured types and re-open the 18.2
tables. `d1`'s ground (a) also came back with a result it did not predict: **3 of 16 rows carried an
English-language instruction and all three still produced Hebrew output**, so the legitimate-foreign-output case
the exclusion exists to protect occurred 0/16. Ground (b) is CONFIRMED in code (`RepairRunsCoreAsync` awaits one
router call per REPAIR run, sequentially, with no cap) but **UNSAMPLED in data**.

**A script-ratio gate for `Custom` is an OPEN QUESTION, not a plan.** If it is ever taken up it needs: (1) a
corpus containing at least one Custom row with a REPAIR-run count >= 1 (none exists, so the gate has no positive
case); (2) evidence about the English-ANSWER case specifically, since the ratio it would key on has never been
observed away from 1.0 and its threshold cannot be calibrated; (3) a decision on WHERE it sits - anywhere shared
re-opens 18.2 for every repaired type and needs the full d5/d6 re-run, while a Custom-only gate is really "a
Custom dispatch arm with a precondition" and should be scoped and named that way; (4) the two dispatch arms plus
a plain-text `RepairableFields` surface, since without them the config key is inert.

#### What remains UNMEASURED (read this before quoting anything above)

This list is the fact most likely to rot, so it is stated as a list rather than left implicit.

- **Only THREE of the ten Repaired types have their own per-type measured scope:** `Synopsis` (q1 then q2)
  and `CharacterAnalysis` / `StoryAnalysis` (q2 scope (i), through the real `BuildBookProfileAsync`). The other
  **seven - `Summarization`, `LineEdit`, `LinguisticAnalysis`, `LiteraryAnalysis`, `BookOverview`, `QA`,
  `BookReview` - have NEVER been run through d5/d6 as their own scope.** The 18.2 figures are measured on a
  **shared fixture corpus of Hebrew prose values** driven through `DynamicTermRepairService`, not per-type
  through each type's own producer seam: they are evidence that the span-scoped ENGINE preserves and cleans,
  and they are NOT a per-type PASS for those seven. (`Summarization` has a separate ~3% leak-RATE figure -
  n = 32, Wilson 95% CI 0.6-15.8%, section 19.4 - which is a different quantity from preservation.)
- **`Custom`'s pick-frequency remains un-measured.** c1's n = 16 rows / 6 prompts / 1 user / 2 books is one
  developer's dev-time usage. `Custom` is 10.1% of that DB's stored analyses, but that percentage **must not be
  quoted as a usage rate**. Nothing licenses "Custom output is always Hebrew" - a true ">= 1 repair run" rate up
  to ~19% is still consistent with observing zero.
- **`Custom`'s cost tail is un-sampled.** The "hundreds of sequential calls" scenario is confirmed as a CODE
  property but has never been reproduced on real data; the ~400-call projection for a 2,433-character English
  answer is arithmetic from observed row lengths, not a measurement.
- **The Hebrew-in-English (Latin-expected) direction is unmeasured ON THE PROFILE PATH**, structurally - see
  scope (i) limit 1 above. It IS measured at the `RunAsync`/`BookReview` seams (the 3 entity-gated cases in
  18.2).
- **The `Synopsis` residual class** - an external proper noun at a mid-paragraph sentence head - is a KNOWN
  live gap, not a measured-clean one. q1's own lesson applies: more fixture values would RAISE the exposed
  count for that class, not lower it.
- **Every figure on this page is a single-run point estimate** unless it says otherwise. The methodology warning
  on `Ai:FeatureModels:_comment_ProofreadModel` applies here too: n >= 3 runs per candidate before any figure is
  used to justify a MODEL change, and run-to-run stability is a selection criterion in its own right. (The q2
  rows above are the partial exception in one specific sense: scopes (ii) and (iii) are fully deterministic -
  0 values reach the model - which is why LOCAL and CLOUD agree byte-for-byte there. The only stochastic
  component in the whole gate is scope (i)'s cleaning half, 10 real model calls, which came back 10/10.)

**`Mode` (added by the dynamic-term-repair follow-up).** A fourth knob, `Ai:AnalysisRepair.Mode`
(`Off` | `Glossary` | `Dynamic` | `GlossaryThenDynamic`), selects WHICH repair stage(s) run once
`Enabled`/`PerType` have allowed the type. **The shipped default is `GlossaryThenDynamic`** (both
`appsettings.json` and `appsettings.Production.json`) - the glossary fast-path cache runs first, then the
dynamic span-scoped stage runs over whatever residual foreign text the glossary left. `Glossary`
(deterministic-only, reproducing the exact pre-follow-up behaviour) was the ORIGINAL shipped default before
the precision follow-up (section 17) re-measured a PASS and flipped it; `Glossary`/`Off` remain the rollback
/ kill-switch. See section 13 for the full semantics and section 15 for the rollout / kill-switch.

**To opt into the LLM stage** (e.g. after validating on your own corpus), set
`Ai:AnalysisRepair.GuardOnly = false`. Keep `Model` in sync with
`Ai:FeatureModels:AnalysisRepair` (currently `{ "Provider": "Ollama", "Model": "gemma4:12b" }`) and
its tuning block `Ai:ProviderSettings:Ollama_AnalysisRepair`
(`{ "Temperature": 0.2, "NumPredict": 2048, "NumCtx": 16384 }`) - those two keys do the actual
routing; `Ai:AnalysisRepair.Model` only documents/asserts the intended model at the config surface.

> **`GuardOnly` asymmetry (intentional).** Flipping `GuardOnly=false` opts the FOUR analysis-seam
> f5-wire types (BookOverview/Character/Story/QA) into the value-scoped LLM Stage-2 alongside the
> original repairable types. **There are TWO exceptions, both deliberate:**
>
> - **`BookReview`:** its engine hook is glossary-ONLY and ignores `GuardOnly` entirely - the whole-book
>   path never makes a per-field LLM repair call. No extra billed/latency cost on the already-expensive
>   whole-book pass.
> - **`Synopsis`** (`be-c01`, 2026-07-28): it is Repaired and wired into both DETERMINISTIC dispatch
>   switches, but has NO arm in `AnalysisRepairService.RepairAnalysisAsync`, so `GuardOnly=false` adds
>   nothing for it. Stage 2 never consults `ForeignRunClassifier`, so rule (7b) - the deterministic
>   line-head LEAVE that q2's 100% preservation is a property of, and the entire reason Synopsis
>   shipped - cannot protect it; it hands the WHOLE value to the model, and its validator does not catch a
>   transliteration (which removes a Latin run and barely moves the length). The only measurement of a
>   repair model rewriting Synopsis prose is q1's 83% / 1-FP HALT. See section 4.1's `Synopsis` row, that
>   switch's `default:` arm for the reversal condition, and the pin
>   `AnalysisRepairExclusionRegressionTests.ShippedSynopsis_IsDeliberatelyOutsideTheValueScopedLlmStage`.
>
> So do not expect `GuardOnly=false` to add LLM repair to BookReview or to Synopsis.

### 4.3 Per-environment / model-tier policy

> This heading was numbered `4.1` until 2026-07-28, colliding with the `PerType` decision table above.
> Every "section 4.1" reference in this document and in the appsettings comments means the DECISION TABLE.

The repair block is **one value per ASP.NET Core environment**: base `appsettings.json` plus an optional
`appsettings.{Environment}.json` override. The follow-up plan added `appsettings.Production.json`
carrying an `Ai:AnalysisRepair` block whose value ENCODES the policy for that environment's served model
tier. The governing principle (leak = small-model artifact) makes this a clean lever:

| Served tier (via `Ai:FeatureModels`) | Repair policy | How to express it |
|---|---|---|
| **Small-local** (Ollama gemma4:12b / qwen3.5:9b / Dicta-3.0) - a LEAKY tier | guard-only glossary ON | `Enabled:true`, `GuardOnly:true`, PerType glossary-on for every type (the shipped base + current Production value) |
| **Capable cloud** (a tier that does not leak) | true no-op | `Enabled:false` (full no-op), or `GuardOnly:true` with an empty `PerType` |

**Current state:** BOTH `appsettings.json` and `appsettings.Production.json` are glossary-on with a
BYTE-IDENTICAL `PerType` (11 keys: 10 `true`, plus the explicit `"Custom": false`; `Proofread` is deliberately
ABSENT - see `4.1`) because **prod still serves the Ollama-LOCAL tier** -
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
"best-editing-abilities" reference for the inverse-scaling premise (a bigger tier leaks LESS and
edits BETTER) - but see the correction below before relying on the LinguisticAnalysis half of it;
Proofread cloud `gemma-4-31b-it` **88/100 / overreach 0-2**
(`docs/PROOFREAD_LINEEDIT_CLOUD_BAKEOFF.md`). Moving to a cloud tier is the separate quality lever AND
would let repair go no-op (section 4.1) - a hosting decision orthogonal to this local guard.

> **[2026-07-27] CORRECTION - the LinguisticAnalysis leg of that premise no longer holds.** It used to
> read "cloud `gemma-4-31b-it` 0.900 vs local `gemma4:12b` 0.750". Both figures came from a superseded
> **11-case** gold; `linguistic-gold.json` is now **18 cases** with a changed prompt. Re-measured on the
> current gold, LOCAL `gemma4:12b` scores **0.900 (recall 100%, type-acc 100%, 0 clean FP)** across 3
> identical runs, and cloud has NOT been run on it at all. So there is currently **no measured
> local-vs-cloud quality gap for LinguisticAnalysis**, and the inverse-scaling premise rests on the
> Proofread evidence alone until cloud is re-measured on the current gold. Do not cite a
> LinguisticAnalysis cloud advantage in a tier or hosting decision without that re-run.

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

The closed glossary (Stage 1) only cleans its 23 curated craft terms (`LiteraryTermGlossary.Terms`); real leaks are open-ended general
vocabulary (`confusion`, `claustrophobia`, `ambivalence`, `nostalgia`, ...) that the glossary can never
reach. The dynamic-term-repair follow-up (plan `dynamic-term-repair-design-2026-07-10`, todos d1-d3) adds a
**bidirectional, span-scoped, fail-safe LLM stage** that handles that open tail, selectable via
`Ai:AnalysisRepair.Mode` (section 13). It first shipped OFF (`Mode=Glossary`) per the original section-14
measurement, but the precision follow-up re-measured a PASS and flipped the shipped default to
`Mode=GlossaryThenDynamic` on the LOCAL tier (section 17), so it now runs by default.

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
| `Glossary` | The deterministic closed English<->Hebrew glossary ONLY; the dynamic span-scoped stage never runs. Reproduces the EXACT pre-follow-up behaviour. Was the original shipped default; the precision follow-up (section 17) moved the default to `GlossaryThenDynamic`. |
| `Dynamic` | The glossary substitution is SKIPPED entirely; the span-scoped detect-classify-repair pass (d1-d3) runs over the original (un-glossaried) prose. |
| `GlossaryThenDynamic` (**shipped default**) | The glossary fast-path cache runs FIRST (cheap, deterministic, catches the closed 23-term vocabulary at zero model cost), THEN the dynamic pass runs over whatever residual foreign text the glossary left - the two stages compose rather than compete. This is the shipped default on the LOCAL tier after the section-17 precision follow-up cleared the bar. |

**Glossary demoted to a fast-path cache.** With the dynamic stage available, the closed glossary's role
under `GlossaryThenDynamic` is a zero-cost deterministic CACHE for its 23 known 1:1 terms (`narrator`,
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
  `appsettings.Production.json` carry `Mode: GlossaryThenDynamic` (flipped from `Glossary` by the section-17
  precision follow-up). Production inherits `FeatureModels` / `ProviderSettings` from the base by convention,
  so only the `Ai:AnalysisRepair` block is duplicated across the two files. `AnalysisRepairConfigParityTests`
  now asserts BOTH the `Mode` value and the `PerType` map are identical across the two files.

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

### 14.4 Verdict: HALT (later OVERTURNED by the section-17 precision follow-up)

> **SUPERSEDED.** This HALT was the ORIGINAL d5/d6 outcome. The section-17 precision follow-up sharpened the
> deterministic gate so the four failing cases (`van` / `da` / `de` / `carpe diem`) no longer reach the model,
> re-measured both tiers at 100% preservation, and flipped the shipped default to `GlossaryThenDynamic`. Read
> 14.1-14.4 as the historical record that MOTIVATED the follow-up, not the current shipped state.

Neither tier RELIABLY meets the agreed bar (LOCAL 78% stable FAIL; CLOUD median 89%, fails in the majority
(3/5) of runs and cannot be certified >= 90%). d5 had recommended default engine LOCAL + `Mode` =
`GlossaryThenDynamic`, but that was SUBJECT to d6, and the d6 precision gate overturned it. At the time this
kept the shipped default `Ai:AnalysisRepair.Mode = Glossary` (deterministic glossary fast-path only), with the
dynamic span-scoped stage wired and available (`Dynamic` / `GlossaryThenDynamic`) but OFF. The precision floor
was two narrow, addressable cases: (1) quoted lowercase foreign IDIOMS (`carpe diem`) that are
shape-indistinguishable from a leak, and (2) lowercase name PARTICLES (`de` / `da` / `van`). 12/18 legit cases
were preserved by the deterministic gate; the follow-up (section 17) closed the remaining 6 by sharpening that
gate rather than by trusting the model.

## 15. Rollout + kill-switch

**Default (updated by the section-17 precision follow-up):** `Ai:AnalysisRepair.Mode = GlossaryThenDynamic`
in BOTH `appsettings.json` and `appsettings.Production.json`, with `Ai:FeatureModels:TermRepair` on the LOCAL
tier (`Ollama / gemma4:12b`, already the shipped local TermRepair model, so no FeatureModels change was
needed). The glossary fast-path cache runs first, then the span-scoped dynamic stage runs over whatever
residual foreign text it left. This REPLACES the original HALT default (`Mode=Glossary`) after the sharpened
deterministic gate cleared the precision bar on both tiers (section 17). The value-scoped LLM Stage-2 stays
OFF (`GuardOnly=true`); the dynamic stage is span-scoped and fail-safe, structurally distinct from the
field-scoped Stage-2 that ships off. `Enabled/GuardOnly/PerType` and the kill-switch below are all unchanged.

**To ENABLE dynamic repair on ANOTHER environment / tier** (LOCAL is already on by default; use this for a
tier you have yourself measured at >= 90% preservation, e.g. the cloud `google/gemma-4-31b-it` fallback):

1. Set `Ai:AnalysisRepair.Mode` = `GlossaryThenDynamic` (recommended - keep the zero-cost glossary cache in
   front) or `Dynamic` (glossary skipped) in that environment's appsettings.
2. Point `Ai:FeatureModels:TermRepair` at the tier you validated (local `Ollama / gemma4:12b`, or the cloud
   `OpenRouter / google/gemma-4-31b-it`) and keep `Ai:ProviderSettings:Ollama_TermRepair` (or the cloud
   tuning) in sync.
3. Keep `Enabled = true` and the type allowed in `PerType`. BookReview is repaired via the engine hook
   (bidirectional glossary + dynamic; still NO value-scoped LLM regardless of `GuardOnly`).

**Kill-switch (fastest to broadest):**

- Set `Mode = Glossary` (or `Off`) - drops back to the deterministic glossary (or no stage) with zero model
  calls. This reverts to the pre-follow-up deterministic-glossary-only posture (the original HALT default).
- Set `Enabled = false` (or remove the `Ai:AnalysisRepair` block) - FULL no-op: neither glossary nor dynamic
  nor LLM runs; inputs byte-identical.
- Narrow the blast radius: remove a type from `PerType` to disable repair for just that analysis type.

Because the dynamic stage is fail-safe by construction (it can only leave a value cleaner or byte-identical,
never worse), the flip to it as the default is low-risk and instantly reversible via `Mode` (set it back to
`Glossary` or `Off`).

## 16. Residual deferrals + review-retro candidates

### 16.1 Deferrals (from the d6 gate)

The first three deferrals below were **CLOSED by the section-17 precision follow-up** (retained here as the
gap they addressed); the rest remain open.

- **[CLOSED - section 17] Quote-aware / do-not-translate gating for intentional foreign idioms.** A
  deliberately-quoted lowercase Latin idiom (`carpe diem`) is shape-indistinguishable from a lowercase
  out-of-glossary leak, so neither the d2 classifier nor the model reliably spared it (FP on BOTH tiers every
  run). e1 added a deterministic quote-aware LEAVE rule (multi-word span bordered by quote characters) that
  now gates it.
- **[CLOSED - section 17] Name-particle context rule in d2.** Lowercase name particles (`de` / `da` / `van`)
  sandwiched between two Title-Case runs ("Simone de Beauvoir", "Vincent van Gogh") reached the model and got
  transliterated / translated. e1 added a deterministic name-particle LEAVE rule (a lowercase Latin run
  between two Title-Case Latin runs) that now spares them.
- **[CLOSED - section 17] Book-scoped entity list.** The classifier accepted an optional `bookEntities` set
  but no live list was wired. e2 added `BookEntityProvider` (deterministic harvest of stored CharacterAnalysis
  names + a manuscript Title-Case / cross-chapter scan) and e3 threaded it through both repair seams; it is
  the one lever that can spare a foreign HEBREW run (which has no case signal).
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

## 17. Precision follow-up: sharpened deterministic skip-gate + rollout (dynamic-term-repair-precision-followup plan)

The section-14 HALT diagnosed the problem precisely: over-rewrite was 0 on both tiers (the span-scope design is
structurally safe), so precision is a SKIP decision, not a replacement. 12 of 18 legitimate cases were already
preserved by the deterministic gate with ZERO model calls; the six failures were two narrow, known classes -
lowercase name PARTICLES (`van` / `da` / `de`) and a quoted foreign IDIOM (`carpe diem`). The precision
follow-up (plan `dynamic-term-repair-precision-followup-2026-07-11`, todos e1-e6) sharpened that DETERMINISTIC
gate with two cheap, NO-NEW-MODEL-COST levers, re-measured both tiers, and flipped the default. The result:
all 18 legitimate cases are now gated deterministically (0 model calls), so preservation is 100% and
tier-independent, and the LOCAL tier now clears the bar the original d6 HALTed on.

### 17.1 The sharpened deterministic skip-gate

Two levers, both in the `d1 -> d2 -> d3` pipeline (section 12.1), both adding ZERO model calls:

- **e1 - two deterministic LEAVE rules in `ForeignRunClassifier` (`Services/Analysis/ForeignRunClassifier.cs`),
  both context-derived from the surrounding value, never a word list:**
  - **Quote-aware LEAVE.** A run inside a MULTI-word quoted span (an opening-like quote reachable to the left
    and a closing-like quote to the right, with at least one other word inside) is a do-not-translate citation
    (`"carpe diem"`) and is LEFT. Guarded tightly: a LONE scare-quoted word does NOT qualify (the span must be
    multi-word), so a single quoted leak still REPAIRs and a stray apostrophe / abbreviation geresh in ordinary
    prose cannot spare a leak. The quote set is ASCII, guillemets, curly typographic quotes, and the Hebrew
    gershayim / geresh; the scan is script-agnostic and bounded (64-char window, stops at any sentence
    terminator / other punctuation).
  - **Name-particle LEAVE.** An all-lowercase Latin run sandwiched between two Title-Case Latin runs (the "van"
    of "Vincent van Gogh", "da" of "Leonardo da Vinci", "de" of "Simone de Beauvoir") is a name connective and
    is LEFT. It requires the IMMEDIATELY-adjacent token on BOTH sides across a single space to be a Title-Case
    Latin run; a lowercase or non-Latin neighbour disqualifies it, so an ordinary lowercase leak (`confusion`
    flanked by Hebrew) still REPAIRs.
    > **SUPERSEDED by section 18.1 (2026-07-12).** The immediate-adjacency requirement described here is a P0
    > gate hole: two ADJACENT lowercase particles disqualify each other, so `of the` / `van der` / `de la` BOTH
    > reached the repair model. The rule is now a bounded "within a Title-Case Latin name span" walk.
- **e2 - the auto per-book entity list (`Services/Analysis/BookEntityProvider.cs`).** The classifier already
  accepted an optional `bookEntities` set (always LEAVE); e2 supplies it deterministically, with no NER model
  call, from two sources: (a) the book's already-stored `CharacterAnalysis` character + relationship names
  (from `AnalysisResult.StructuredResult` and the cached `BookProfile.CharactersJson`), and (b) a manuscript
  scan of the chapter prose for Latin Title-Case tokens that either recur across >= 2 chapters OR appear
  mid-sentence at least once (both proper-noun signals a leaked common word does not exhibit). A tiny stop-list
  of capitalized common words is the only word list, applied to the manuscript scan only (a declared name is
  authoritative even when it looks like a common word). Case-insensitive; cached per book (a stale set only
  changes which tokens are spared, never correctness). This is the one lever that can spare a foreign HEBREW
  run in a Latin-script book, which has no letter-case signal. **Fail-safe:** no book context / any fault
  yields an EMPTY set, which is exactly the pre-follow-up behaviour; the swallowed fault is logged (never
  silently hidden), per the fail-safe-swallow-observability lesson.
- **e3 - threading the set to the classifier.** `UnifiedAnalysisService.ApplyAnalysisRepairAsync` gained a
  `bookId` param (all callers pass it; the raw seam passes `Guid.Empty` = empty set), fetches the entity set
  LAZILY only on the dynamic path (so a run that never reaches the dynamic stage never hits the DbContext), and
  threads it through `DynamicTermRepairService.ApplyAsync` -> `RepairFieldsAsync` -> `RepairValueAsync` ->
  `ForeignRunClassifier`. The BookReview engine hook (`BookReviewService`) is wired the same way via
  `RepairFindingsAsync`. `BookEntityProvider` is a singleton (its per-book cache persists across requests) that
  reads the DbContext through a short-lived scope, so it never captures a scoped DbContext.

### 17.2 Re-measured preservation + cleaning (both tiers, e4; RULE 0, real outputs)

> **SUPERSEDED by section 18.2 (be-c08, 2026-07-12). The tables below are KEPT as the historical record, not as
> the current gate.** They are retained deliberately (an overwritten measurement hides the reason the second one
> was needed), but they must not be cited as the evidence for the shipped default. Two reasons, both structural:
> **(1)** e4 HAND-AUTHORED its entity sets and described them as what a deterministic `BookEntityProvider` "would
> surface". That was an assumption, never a measurement: the e4 harness never constructed the provider, never
> threaded a `bookId`, and never touched the DB path that ships, so the harvest logic and the `bookId` threading
> (the entire e2/e3 contribution) are exactly the parts of the feature its gate did NOT exercise. **(2)** e4's
> CLEANING table (below) was measured ENTITY-FREE, so the one regression the entity lever was known to risk (an
> over-harvested entity SPARING a real leak) was never measured at all. Section 18.2 re-runs both gates through
> the REAL provider, with an ADVERSARIAL book chosen to trigger exactly that regression.

Re-run via the same instruments as d5/d6 (`OutputQualityDiagnostic.MeasureLegitimateTermPreservation_LocalVsCloud`
+ `.MeasureDynamicTermRepair_LocalVsCloud`) with the sharpened gate + a representative per-book entity set
active. The lowercase name particles (`van` / `da` / `de`) were DELIBERATELY EXCLUDED from the entity set so
the e1 name-particle rule is still exercised rather than masked.

**Preservation (false-positive gate) - legitimate-term set, 18 values that must come back byte-identical:**

| tier | preservation | over-rewrite | model calls | meets bar (>= 90% & over-rewrite 0) |
|---|---|---|---|---|
| LOCAL (`Ollama / gemma4:12b`) | **100% (18/18)** | **0** | 0 (all gated) | **YES** |
| CLOUD (`OpenRouter / google/gemma-4-31b-it`) | **100% (18/18)** | **0** | 0 (all gated) | **YES** |

All 18 legitimate cases were preserved by the DETERMINISTIC gate with ZERO model calls: `van` / `da` / `de`
by the e1 name-particle rule (3 runs / 0 repair), `carpe diem` by the e1 quote rule (2 / 0),
Kafka / Paris / Orwell / Kindle / Photoshop / Google / NASA / PDF / "Brave New World" by the entity /
Title-Case / allowlist gates, `example.com` / `info@publisher.com` by the URL / email gates, and the
Hebrew-in-English names שרה / דוד / ירושלים by the supplied book-entity set. Because gating is pure and
deterministic, **preservation is tier-independent** - LOCAL is 100% precisely because those four
previously-failing tokens no longer reach the model, which retires the original d6 HALT cause (LOCAL 78% was
BECAUSE `van` / `da` / `de` / `carpe diem` reached the model). This is why LOCAL now clears the >= 90% bar it
stably failed before (78% -> 100%).

**Cleaning (recall gate) - 10 real out-of-glossary leaks, entity-free:**

| tier | cleaned | over-rewrite | latency median / p90 (ms) |
|---|---|---|---|
| LOCAL (`Ollama / gemma4:12b`) | **100% (10/10)** | **0** | 2646 / 3245 |
| CLOUD (`OpenRouter / google/gemma-4-31b-it`) | **100% (10/10)** | **0** | 972 / 4926 |

Both tiers cleaned all 10 leaks, span-scoped, with over-rewrite 0 (the HARD gate HELD): `confusion` -> `בלבול`,
`nostalgia` -> `נוסטלגיה` (CLOUD `געגועים`), `alienation` -> `ניכור`, `catharsis` -> `קתריסיס` (CLOUD
`קתארזיס`), `vulnerability` -> `פגיעות`, `melancholy` -> `מלנכוליה`, and so on. The new LEAVE rules did NOT
regress cleaning - the leak set stayed 100% cleaned. **Non-regression (deterministic):** the shipped glossary
still cleans `narrator` / `tension` / `irony` under `GlossaryThenDynamic` and the dynamic stage does not undo
them.

> LOCAL produced two non-standard transliteration spellings (`claustrophobia` -> `קפוסטרופוביה`,
> `catharsis` -> `קתריסיס`); both were still CLEANED (no Latin residual) and span-scoped, so they are a
> Hebrew-spelling-quality note for native-speaker validation (section 16.1 / repair-gold `c04`), NOT a gate
> failure.

> **SCOPE OF THE 100% CLEANING NUMBER (read this before trusting it).** All 10 leaks in the d5 set are
> CONTENT NOUNS (`confusion`, `nostalgia`, `catharsis`, `melancholy`, ...), i.e. abstract nouns with a clean
> 1:1 Hebrew equivalent. The set contains **ZERO function words**. A leaked FUNCTION word (`the`, `of`, `and`,
> `to`) has NO standalone Hebrew equivalent (Hebrew's definite article is the PREFIX ה־, not a word), so the
> span-scoped pass structurally CANNOT fix it and it survives - section 17.5 reproduces exactly that on a real
> end-to-end run. So "100% cleaned" means 100% of the CONTENT-noun class: the class the closed glossary could
> not reach, and the class this feature was built for. It is NOT 100% of all conceivable leaks, and the gold
> set should not be read as if it were.

### 17.3 Rollout decision (user-chosen 2026-07-11): SHIP LOCAL

Both tiers cleared the bar (>= 90% preservation + 100% cleaning + over-rewrite 0 on REAL outputs), so the
conditional model-judge escalation (e5) was CANCELLED. **The shipped default `Ai:AnalysisRepair.Mode` was
flipped from `Glossary` to `GlossaryThenDynamic`** in BOTH `appsettings.json` and `appsettings.Production.json`,
with `TermRepair` on the **LOCAL** tier (`Ollama / gemma4:12b`, already the shipped local TermRepair model, so
no `Ai:FeatureModels:TermRepair` change was needed - only the `Mode` flip). LOCAL was chosen because it is the
cheapest, offline, and private option and it meets the bar; CLOUD (`OpenRouter / google/gemma-4-31b-it`)
remains the measured fallback (section 15). The precision win is carried by the DETERMINISTIC gate (all 18
legit cases gated, 0 model calls, tier-independent), not by trusting the LOCAL model on the reached-model
tokens - which is why LOCAL clears the bar it originally HALTed on. The kill-switch (`Mode=Glossary` / `Off`,
`Enabled=false`, or narrowing `PerType`) is unchanged (section 15).

### 17.4 Residual deferrals + review-retro candidates (this follow-up)

- **Item 5 (its own later plan):** the self-adjusting feedback loop - surface repairs in the editor as
  accept/reject, feed rejections into a per-book do-not-touch store the gate consults; integrate with editor
  roles / the base editor character. Highest build cost, out of scope here (section 16 / the design plan's
  Deferred section).
- **English-book (Hebrew-in-English) direction is still under-measured.** Only 3 Latin-native cases, and all
  three are now entity-gated, so the reached-model behaviour of that direction was not stress-tested (section
  16.1). Re-measure with Hebrew common-concept words before enabling on English books.
- **Native-speaker spot-validation of the LOCAL Hebrew transliteration spellings** (`claustrophobia` ->
  `קפוסטרופוביה`, `catharsis` -> `קתריסיס` were cleaned but spelled non-standard), mirroring repair-gold `c04`.
- **FUNCTION-WORD leaks are structurally out of reach of span-scope (an ACCEPTED LIMIT, not a bug).** A leaked
  closed-class function word cannot be repaired by swapping ONE token: fixing `מתוך the שמיים` requires
  DELETING the token AND prefixing ה־ to the next word, i.e. a phrase RESTRUCTURE - precisely the field-scope
  blast radius the span-scope design forbids in order to hold over-rewrite at 0 (section 12.4). The fail-safe
  behaves correctly here: it leaves the text untouched rather than corrupting the sentence. Observed live in
  section 17.5. **FOLLOW-UP (cheap, ZERO recall cost):** add a FUNCTION-WORD LEAVE rule to
  `ForeignRunClassifier` so a closed-class English function word (`the`, `of`, `and`, `to`, `in`, ...) is gated
  deterministically. Today every leaked `the` costs a WASTED TermRepair model call (~2.6s LOCAL) and still
  fails; gating it up front costs nothing in recall (it was never fixable) and saves the call. It also belongs
  in the d5 gold set as an EXPECTED-SURVIVOR case, so the taxonomy gap above cannot silently reappear.
- **The d5 leak taxonomy is incomplete (measurement gap).** The gold set is all content nouns; extend it with
  a function-word case (expected: LEAVE / survive) and ideally a multi-word phrase leak, so the headline
  cleaning number states WHICH class it covers.
- **review-retro candidate - config-parity coverage gap.** `AnalysisRepairConfigParityTests` originally
  guarded only the `PerType` map between base and Production; the `Mode` value could drift silently (the exact
  value this follow-up had to hand-sync across both files). The test now also asserts `Mode` parity. Lesson: a
  parity test should cover EVERY independently-overridable key in a duplicated config block, not just the map
  that motivated it.
- **review-retro candidate - default-flip comment drift.** Flipping a single shipped-default value
  (`Mode: Glossary` -> `GlossaryThenDynamic`) leaves a trail of now-stale "shipped default = Glossary" /
  "never runs by default" inline comments and xmldoc across the codebase (appsettings comments, `Program.cs`,
  the `AnalysisRepairMode` / `AnalysisRepairOptions` xmldoc in `Services/Ai/AiOptions.cs`, several
  `UnifiedAnalysisService` / `BookReviewService` / `DynamicTermRepairService` comments). A default flip should
  be paired with a sweep of the "shipped default" assertions that describe it.
- **review-retro candidate - a gold set that only contains the class you expected.** The d5 leak set was all
  content nouns, so "100% cleaned" read as complete coverage until a real end-to-end run surfaced a class the
  set never contained (function words). A recall gold set should enumerate the TAXONOMY of the failure it
  measures (and include expected-SURVIVOR cases), not just the instances that motivated the feature.

### 17.5 End-to-end validation through the real API (2026-07-12)

The d5/d6 instruments drive `DynamicTermRepairService.RepairValueAsync` **directly**; they never exercise the
shipped seam (`UnifiedAnalysisService.RunAsync` -> `ApplyAnalysisRepairAsync` -> `BookEntityProvider` ->
repair). That seam was covered by deterministic tests ONLY, so it was validated once against the RUNNING API
and the REAL database before the PR (RULE 0: inspect the artifact the user actually receives).

Two live `LiteraryAnalysis` runs (Hebrew, LOCAL `gemma4:12b`, shipped default `Mode=GlossaryThenDynamic`):

| run | input | result |
|---|---|---|
| 1 | Hebrew book, Hebrew chapter (the NORMAL flow) | 116s, OK. Every prose value pure Hebrew, ZERO leaks. `themes[].significance` = `major` / `minor` preserved BYTE-IDENTICAL (the enum / structural-field invariant held). |
| 2 | Hebrew analysis of an ENGLISH chapter (deliberate stress case) | 114s, OK. Character names `Daniel` / `Mara` NOT corrupted. ONE leaked FUNCTION word survived: `rhetoricalDevices[0].example` = `"...מתוך the שמיים"`. |

**On the names (run 2).** The output reads `דניאל` / `מרה`, but that is the MODEL's own Hebrew transliteration,
not a repair. The repair provably never saw those runs: `Daniel` is Title-Case mid-sentence AND a harvested
book entity, so the classifier gates it LEAVE on two independent rules. A repair-induced name corruption would
have been a P0; it did not happen.

**On the survivor (run 2), root-caused rather than assumed:**
- `RhetoricalDevice.Example` IS in the `RepairableFields` whitelist, so the field WAS in scope for repair.
- Entity over-harvest is RULED OUT: `the` sits in `BookEntityProvider.CommonWordStopList` (case-insensitive)
  and is therefore never harvested, so the entity gate did not spare it.
- The classifier therefore correctly routed `the` to REPAIR; the MODEL declined (there is no standalone Hebrew
  word for the English definite article) and the fail-safe kept the original value.
- => a STRUCTURAL limit of span-scope, NOT a defect, and NOT a regression (under the previous default
  `Mode=Glossary` the same `the` survived, since the closed glossary holds 23 craft terms). Follow-up in 17.4.

**What the seam validation confirms:** `bookId` threading, the singleton `BookEntityProvider` reading the
DbContext through a short-lived scope, and the dynamic stage all ran in production config without fault, and no
structural field was altered. Note the two runs exercised DIFFERENT provider paths: run 1's book has no Latin
manuscript tokens and no stored `CharacterAnalysis`, so the provider legitimately returned an EMPTY set (its
FAIL-SAFE path, identical to pre-follow-up behaviour); run 2's book supplied REAL harvested entities
(`Daniel` / `Mara`, via Title-Case + cross-chapter recurrence).

## 18. Precision fixes + re-measure through the REAL provider path (dynamic-term-repair-precision-fixes plan, 2026-07-12)

The pre-PR review of the section-17 follow-up found that the gate itself had a hole, and that the measurement
which justified the rollout had not run the code that ships. The shipped default was therefore REVERTED to
`Mode=Glossary` first (a safety measure, nothing was committed yet), every precision fix was landed, both gates
were RE-MEASURED on the LOCAL tier through the real `BookEntityProvider`, and only then was the default
re-flipped to `GlossaryThenDynamic`. This section records that cycle. Where it disagrees with section 17, THIS
section is current.

### 18.1 The P0 gate hole: two adjacent lowercase particles disqualified each other

The e1 name-particle LEAVE rule (section 17.1) recognised only a SINGLE lowercase particle between two
Title-Case Latin names: it required the IMMEDIATELY adjacent token on BOTH sides, across exactly one space, to
be a Title-Case Latin run. Runs are word-level (a space ends a run), so the moment TWO lowercase runs sit side
by side, each one is the other's disqualifying neighbour and BOTH are classified REPAIR. Confirmed empirically
against the un-patched rule:

| value | runs sent to the repair model (un-patched) |
|---|---|
| `The Lord of the Rings` | `of`, `the` |
| `Mies van der Rohe` | `van`, `der` |
| `Charles de la Rue` | `de`, `la` |

The single-particle cases the fixture DID contain (`Vincent van Gogh`, `A Tale of Two Cities`) gated correctly,
which is why the hole survived e4: the fixture only ever exercised the shape the rule handled.

**Why this is a P0 and not a cosmetic miss.** Once a fragment reaches the model, `DynamicTermRepairService`
splices the Hebrew substitution back SPAN-SCOPED, and validation-by-re-detect CANNOT catch it: substituting
Hebrew for `of` REDUCES the Latin-run count in the value, so the repair validates as SUCCESSFUL. The layer's
whole safety story (fail-safe, revert-on-doubt) is blind here by construction. The output is a corrupted book
title or surname in persisted analysis prose, exactly the class of damage the gate exists to prevent.

**The fix (be-c01).** The rule is generalised from "immediately sandwiched between two Title-Case Latin runs" to
"lies WITHIN a Title-Case Latin name span": scanning OUTWARD from the run across space-separated Latin tokens,
a Title-Case Latin token must exist on BOTH sides with only all-lowercase Latin tokens in between. The walk is
BOUNDED (`ForeignRunClassifier.MaxNameSpanLowercaseTokens = 3`, and it crosses no non-Latin / non-space
character), so it has a defined found-nothing answer and cannot run away over a whole paragraph. The tight
negatives still hold: a plain lowercase leak flanked by Hebrew (`confusion`) still REPAIRs, and `van` preceded
by a lowercase `the` with NO Title-Case anchor beyond it (`היא נכנסה אל the van בחניון האחורי`) still REPAIRs.
All three shapes above are now LEAVE-for-every-run regression tests, each proven RED against the un-patched
rule before the fix was accepted.

### 18.2 The be-c08 re-measure (SUPERSEDES the e4 tables in section 17.2)

Same instruments as d5/d6 (`OutputQualityDiagnostic.MeasureDynamicTermRepair_LocalVsCloud` and
`.MeasureLegitimateTermPreservation_LocalVsCloud`), live run 2026-07-12, LOCAL tier (`Ollama / gemma4:12b`),
every fix in this plan active. The ONE thing that changed about the harness is the thing that matters: the
entity set is now obtained by CALLING `BookEntityProvider.GetEntitiesAsync(bookId)` against a real
`AppDbContext` over seeded books (be-c07), instead of being hand-authored in the test file.

**Why the e4 tables cannot stand.** e4 hand-built its entity sets and only ASSUMED they were what the provider
would surface. The harness never constructed the provider, never threaded a `bookId`, and never touched the DB
path that ships, so the harvest logic and the `bookId` threading (all of e2/e3) were never on the measured path
that flipped the production default ON. Separately, e4's d5 CLEANING gate ran ENTITY-FREE, so the entity
lever's recall risk (an over-harvested entity SPARING a real leak) was not measured at all. Both gaps are
closed below. The e4 tables are kept in section 17.2 as the historical record, marked superseded.

**d5 CLEANING (recall gate), two arms.** ARM A is e4's entity-free control. ARM B is the production path: the
REAL provider set for an ADVERSARIAL Hebrew book whose manuscript carries ONE English epigraph line
(`הוא ציטט את הפתגם האנגלי: "A story of Confusion and Nostalgia, of Tension without Catharsis."`), which makes
the provider harvest `Confusion`, `Nostalgia`, `Tension` and `Catharsis` as manuscript-tier entities. Those are
the leak words themselves. Under the pre-be-c04 case-INSENSITIVE membership, each would have spared its
lowercase twin.

| arm | entity source | cleaned | over-rewrite | model calls | latency median / p90 (ms) |
|---|---|---|---|---|---|
| ARM A (control) | entity-free (e4's setup) | **10/10 (100%)** | **0** | 10 | 2410 / 2665 |
| ARM B (production path) | REAL provider set, adversarial book | **10/10 (100%)** | **0** | 10 | 2358 / 2494 |

- delta (B minus A): **0 percentage points**. The two arms agree on EVERY case (same cleaned, same
  over-rewrite, same model-call count).
- leaks SPARED by the entity lever (deterministic, 0 model calls): **0**.
- The lever is genuinely armed (4 leak words ARE in the provider's set) and still spares nothing, because
  be-c04 made manuscript-harvested tokens match CASE-SENSITIVELY. The regression it prevents is quantified in
  the plan's investigation: with case-insensitive membership, 3 of the 10 leaks (30%) flip REPAIR to LEAVE,
  bought with a single sentence of English in an 80-chapter manuscript.

**d6 PRESERVATION (false-positive gate), 21 legitimate values that must come back byte-identical:**

| tier | preservation | false positives | over-rewrite | values reaching the model | meets bar (>= 90% and over-rewrite 0) |
|---|---|---|---|---|---|
| LOCAL (`Ollama / gemma4:12b`) | **100% (21/21)** | **0** | **0** | **0 (all gated)** | **YES** |

- Gate attribution: **3** cases gated by a PROVIDER-HARVESTED entity, **18** by a classifier or detector rule.
  The 3 entity-gated cases are the Hebrew-in-English direction (`שרה`, `דוד`, `ירושלים`), where no case signal
  exists and the entity set is the ONLY possible lever. That is precisely the place e4's hand-authored set hid.
- The three be-c01 P0 shapes are in the fixture and are CLASSIFIER-gated with ZERO repair runs:
  `The Lord of the Rings` (5 runs / 0 repair), `Mies van der Rohe` (4 / 0), `Charles de la Rue` (4 / 0). None
  of their tokens is seeded into any book's manuscript, so the entity lever is inert for them BY CONSTRUCTION
  and they can ONLY be gated by the name-span rule. That invariant is what the rollout rests on, and it is
  pinned deterministically in `BookEntityFixtureSeedTests`.
- Non-regression (deterministic): the shipped glossary still cleans `narrator` / `tension` / `irony` under
  `GlossaryThenDynamic`, and the dynamic stage does not undo it.

> **HOW TO READ THE 100% PRESERVATION NUMBER.** Because 0 of the 21 values reach the model, this figure is now
> a property of the DETERMINISTIC GATE, not of the model tier. It no longer discriminates LOCAL from CLOUD, and
> it must not be read as a model-quality result: 100% means the gate catches everything, not that the model
> preserves well. It also means d6 has stopped stressing the model's preserve-a-proper-noun behaviour, which is
> the intended design (a token that never reaches the model cannot be corrupted by it) but is worth stating.

### 18.3 The cross-script harvest and the cache-refresh contract (be-c03, be-c04)

The entity lever was largely inert in production before these fixes. Two independent causes, both fixed:

**Script-aware harvest.** The manuscript scan was LATIN-ONLY, so it could never emit a Hebrew token, even
though the provider's own reason for existing is that in a Latin-script book the entity check is the ONLY lever
that can spare a Hebrew run (Hebrew has no case, so no Title-Case, ALL-CAPS or name-particle signal is
available there). The scan is now SCRIPT-AWARE: it harvests the FOREIGN script relative to the book's language,
so a Hebrew-native book harvests recurring Latin Title-Case tokens (as before) and a Latin-native book harvests
recurring HEBREW tokens. In the Hebrew direction there is no case signal, so cross-chapter recurrence
(`MinChaptersForRecurrence = 2`) is the whole gate, backed by a small Hebrew function-word stop-list so the
recurrence rule does not harvest ordinary prose.

**Two-tier, case-asymmetric membership (be-c04).** Manuscript-harvested tokens match CASE-SENSITIVELY; declared
`CharacterAnalysis` names match case-insensitively. The asymmetry is deliberate and is the fix for the recall
regression measured above: a leak is LOWERCASE by construction, while a name's manuscript evidence is
CAPITALIZED by construction. Matching the manuscript tier case-sensitively spares `Confusion` (the exact
surface form the book showed) without sparing the lowercase `confusion` that is a leak. A declared name is
authoritative, so it keeps the looser match. The carrier type is `Services/Analysis/BookEntitySet.cs`, an
`IReadOnlySet<string>` so the classifier signature is unchanged; `ForeignRunClassifier` treats its `Contains`
as authoritative rather than widening a miss back into a case-insensitive scan.

**Cache-refresh contract.** The per-book cache was a process-lifetime dictionary that also cached the EMPTY
set, and `Invalidate` had NO callers. The ordinary production sequence therefore defeated the stored-names
source outright: the first chapter analysis on a fresh book cached an empty set (no `CharacterAnalysis` exists
yet), `BuildBookProfileAsync` later PRODUCED the `CharacterAnalysis` the provider wanted, nothing invalidated,
and the character names never entered the set for the life of the process. The contract is now:

- **BOUNDED.** A private `MemoryCache` (owned, not the app-wide `IMemoryCache`, so a `SizeLimit` here does not
  force every other cache entry in the process to declare a `Size`), `SizeLimit = 128` books at 1 entry each,
  a 30-minute sliding expiry, and a 2-hour absolute expiry as the backstop behind the explicit invalidations.
- **NEVER CACHES THE EMPTY SET.** An empty build means "no harvest source exists yet", which is the state of a
  fresh book. Rebuilding it is three indexed reads that return nothing, so it is cheaper to retry than to pin.
- **INVALIDATED BY EVERY PRODUCER OF A HARVEST SOURCE.** `BookIntelligenceService.BuildBookProfileAsync` (the
  `CharacterAnalysis` / `BookProfile` producer), `UnifiedAnalysisService`'s persisting seam, and
  `ChapterService`'s content writes (save, create, delete, and DOCX import, since `Chapter.ContentText` is a
  harvest source).
- **Staleness is NOT correctness-neutral.** The old header claimed a stale set "only changes which tokens are
  spared, never correctness". Under this feature's governing principle that is FALSE: a name the gate fails to
  spare is a name the repair model corrupts. The header now says so.

### 18.4 Rollout decision and the kill-switch (unchanged)

**Both gates PASS on LOCAL, so the shipped default is re-flipped to `GlossaryThenDynamic`** in BOTH
`appsettings.json` and `appsettings.Production.json` (they must move together;
`AnalysisRepairConfigParityTests.Mode_BaseAndProduction_AreEqual` guards exactly this). The tier is LOCAL
(`Ollama / gemma4:12b`), already the shipped `Ai:FeatureModels:TermRepair` model, so the `Mode` flip is the
only config change. LOCAL is chosen because it is free, offline and private, and because precision is now
carried by the deterministic gate rather than by the model tier (18.2), so the cheapest tier meets the bar.
CLOUD (`OpenRouter / google/gemma-4-31b-it`) stays ROUTING-ONLY by decision, not because it fails a bar.

The kill-switch is UNCHANGED from section 15:

- **`Ai:AnalysisRepair.Mode = "Glossary"`** rolls back to the deterministic glossary only, reproducing the exact
  pre-d4 sequence. This is the one-knob way to disable the dynamic stage while KEEPING the glossary guard.
- **`Mode = "Off"`** additionally skips the glossary.
- **`Enabled = false`** remains the MASTER off-switch for the whole repair layer (every stage, a strict no-op).
- **`PerType`** is unchanged and still gates repair per analysis type; `Proofread` is never repaired regardless.

Note that the CLASS default on `AnalysisRepairOptions.Mode` (in `Services/Ai/AiOptions.cs`) deliberately stays
`Glossary`. That is the safe posture for programmatic and test construction (a hand-built options object never
silently starts calling the repair model); it is NOT a drift from the shipped value. The gap is covered by
`AnalysisRepairConfigParityTests.ShippedMode_BindsIntoAiOptions_AndDrivesTheStageSelection`, which binds the
REAL `appsettings.json` and asserts the bound `Mode` drives the stage predicates (`RunsGlossary()` /
`RunsDynamic()`, the single shared pair every gate now calls).

### 18.5 Residual deferrals (stated honestly)

- **Native-speaker validation of the emitted Hebrew is still OPEN.** LOCAL produces non-standard
  transliterations and paraphrases: `catharsis` becomes `קתרזיס`, and `claustrophobia` came back as
  `חרדת מרחב סגור` in one arm and `פחד מסביב` in the other. Every one was CLEANED (no Latin residual) and
  span-scoped (over-rewrite 0), so this is not a gate failure, but the SPELLING and idiomatic quality of the
  Hebrew is unvalidated. Mirrors the repair-gold `c04` deferral.
- **The Hebrew-in-English direction now HARVESTS (be-c03), but is still measured on only 3 synthetic
  Latin-native cases.** All three are entity-gated, and the direction was not stress-tested with Hebrew
  common-concept words (where translating is arguably correct). Treat its preservation as indicative, not
  proven, before enabling on English books at scale.
- **The entity set measured in be-c08 is SYNTHETIC-book-sourced.** It is the real provider over a real
  `DbContext`, but the books are seeded fixtures. What is now measured is the harvest LOGIC and the two-tier
  matching; the harvest DENSITY of a real book is still unmeasured. The one real data point comes from be-c04's
  investigation, which ran the harvest over the real 80-chapter Hebrew manuscript fixture: it yields **0**
  tokens as-is (the manuscript is effectively pure Hebrew), and **4** once a single English epigraph line is
  added. So on a real Hebrew book the harvest is driven by INCIDENTAL Latin (epigraphs, quoted lines, brand
  mentions), not by the recurrence signal.
- **An UNQUOTED lowercase foreign idiom is shape-indistinguishable from a leak.** be-c05's matched-quote-pair
  rule spares a QUOTED `carpe diem`, but an unquoted lowercase idiom looks EXACTLY like the lowercase
  out-of-glossary leaks d5 cleans, and neither the classifier nor the model reliably spares it. This is an
  inherent precision FLOOR of the dynamic pass, not a bug to be fixed at the margin. A do-not-translate
  allowlist for intentional foreign phrases remains a deferral.
- **Item 5 (the accept/reject feedback loop) remains its own plan.** Surface repairs in the editor as
  accept/reject and feed rejections into a per-book do-not-touch store the gate consults. Highest build cost,
  out of scope here.
- **Function-word leaks stay structurally out of reach of span-scope** (section 17.4), unchanged by this plan.

## 19. Operational cost: cross-model GPU swap on a single-GPU host (termrepair-model-swap-thrash plan, 2026-07-27)

TermRepair routes to `Ai:FeatureModels:TermRepair` (gemma4:12b, section 13), the same model LinguisticAnalysis
and BookReview already use. Three of the OTHER repairable task types route elsewhere (section 3's table):
Summarization/QA/GenericChat -> qwen3.5:9b, LineEdit -> DictaLM-3.0-Nemotron-12B. On a single-GPU host with
`OLLAMA_MAX_LOADED_MODELS=1` (the standing tuning, memory `pagedraft-ollama-8gb-tuning`), a repair that fires
on one of those three EVICTS the task model and LOADS gemma4:12b, and the user's next same-type action then
evicts it back. This section records what was measured, not guessed (plan
`src/.cursor/plans/_todo/termrepair-model-swap-thrash-2026-07-12.plan.md`, todos s1-s4) - it SUPERSEDES the
plan's original "~25 s cold load" estimate that motivated the investigation.

### 19.1 The no-op cost (read this first, so nobody over-corrects)

**A clean analysis makes ZERO model calls and costs 0-127 ms of repair-layer overhead** - measured across 27
real-content chapter summaries plus the LiteraryAnalysis runs (s1, s3). Everything below applies only when a
value actually leaks, and leaks are measured rare (19.4). The repair layer is free in the common case; nothing
in this section is a reason to disable it pre-emptively.

### 19.2 Measured swap cost - asymmetric, not a single number

Measured directly against the Ollama API, `num_ctx=16384`, inference cost held negligible (s1, s4):

| leg | measured cold load (wall ms) | notes |
|---|---|---|
| -> gemma4:12b | 21,489 / 22,930 / 23,423 | 3 samples, tight range |
| -> qwen3.5:9b | 17,785 / 17,886 / **34,296** | page-cache-warm ~17.8 s; ONE first-touch (cold page cache) sample nearly 2x worse |
| -> DictaLM-3.0-Nemotron-12B | 16,423 | 1 sample |
| warm span (same model resident, no swap) | gemma 2.2-3.0 s, qwen 1.3-1.9 s | |

- **The swap is asymmetric, not the single "~25 s" the plan started from.** qwen -> gemma costs ~21.5-22.9 s;
  gemma -> qwen costs ~17.8 s page-cache-warm, but a first-touch load (OS page cache cold) was measured at
  34.3 s - read "~25 s" as the page-cache-warm best case, not the worst.
- **Eviction itself is free.** The entire cost is the incoming model's load; it is statistically identical
  whether the GPU was empty or another model was resident.
- **One full out-and-back** (a leaking repair swaps out to gemma; the user's next same-type action swaps back)
  costs roughly **40 s** on the page-cache-warm numbers, and can be worse on a first touch.

### 19.3 The N-swap loop is fixed (batching)

The one place this was genuinely brutal was `BookIntelligenceService.SummarizeChaptersAsync`, which used to
repair INSIDE the per-chapter loop, so a book with K leaking chapters paid K swaps. It was restructured (now
`SummarizeChaptersCoreAsync`) into summarize-all -> ONE repair pass -> persist, so the repair model loads at
most ONCE PER CHECKPOINT WINDOW no matter how many of that window's chapters leak (as first shipped the window
was the whole book, i.e. once per `/summarize` call; the paragraph below explains why it is now bounded, and
why bounding it does not cost swaps in practice). Verified live against the real API:
**88,033 ms / 2 swaps (interleaved) -> 80,417 ms / 1 swap (batched)**, with no content lost, no cross-chapter
mix-up, and no dropped chapter (full per-assertion evidence in the plan's `## s3 quality-gate results`).

**Residual, worth knowing:** batching moves the swap-back OUT of the `/summarize` operation, it does not
eliminate it globally. gemma4:12b is left resident when the call returns, so the user's NEXT qwen/Dicta-routed
action (not this one) pays that load. That residual single-swap-per-analysis cost is what 19.4 addresses.

**The batch is WINDOWED, not whole-book (be-c03, 2026-07-28).** As first shipped, the restructure had a single
`SaveChanges` at the very end of the pass, which removed monotonic progress: an abort, a client disconnect, or
a wedged Ollama runner during the summarize phase discarded EVERY chapter, where the old per-chapter persist
kept the completed ones. That was priced as "never a correctness loss, only repeated work", which is true of
correctness and false of progress - work aborted at the SAME point every time never converges. The pass now
processes chapters in CHECKPOINT WINDOWS of `Ai:AnalysisRepair:SummaryBatchWindowChapters` (default 10,
mirrored into `appsettings.Production.json` and guarded by `AnalysisRepairConfigParityTests`), each window
running its own summarize -> repair -> persist. The non-negotiable invariant is unchanged: a window persists
only AFTER its own repair pass, so un-repaired prose is still never written, not even transiently.

Sizing, from the numbers above rather than by feel. Decomposing the 80,417 ms 2-chapter batched run into fixed
model-load cost plus marginal cost gives **~18-27 s per chapter**, so a pass is roughly `21.5 s + N x (18-27 s)`.
That crosses 2 minutes at 4-6 chapters, 5 minutes at 11-17, and 30 minutes at 66-101; the real 80-chapter
Hebrew manuscript in this project's corpus is a **24-37 minute** single request on its first pass. A window of
10 bounds the work a single abort can discard to ~3-4.5 minutes. **Lowering the window does NOT cost one swap
per window on a healthy corpus:** a window whose chapters all come back clean makes no repair model call at all
(19.1), and the measured leak rate is ~3% (19.4), so the expected swap count is `min(windows, leaking chapters)`
either way - about 2-3 on an 80-chapter book regardless of the window size. Setting the window at or above a
book's chapter count reproduces the original single-commit behaviour exactly.

### 19.4 Why the remaining single-swap cost was left alone (s4 decision: ACCEPT)

s4 evaluated four options for the remaining "one swap per leaking Line Edit or Summarize" cost and recommended
**(a) ACCEPT - no routing change, nothing built.** Full evidence and the three rejected alternatives are in the
plan's `## s4 decision`; the load-bearing reasons:

- **Only 2 of the 6 editor analysis types can trigger a TermRepair swap at all - Line Edit (Dicta) and
  Summarize (qwen).** This count is UNCHANGED, and it was explicitly RE-EXAMINED rather than merely
  re-asserted by the `analysis-repair-pertype-coverage-holes-2026-07-28` plan's `## s5r re-examination` (its
  ACCEPT verdict stands with no caveat). What changed is the WARRANT for two of the six, not the count:
  Proofread is excluded BY DESIGN (its output quotes verbatim manuscript spans; repairing them would corrupt
  the suggestion diff - section 4) and is never repaired. Custom is now a DELIBERATE, ARGUED exclusion
  (`## d1 decision`, with `c1`'s measurement behind it - section 4.2) rather than an accidental omission from
  `PerType` - its instruction is user-authored, so its output is legitimately foreign in an unbounded fraction
  of runs, and the repair layer's cost model has no cap on foreign-run count per value; `PerType` carries an
  explicit `"Custom": false` so the decision is visible at the config surface rather than inferred from a
  missing line (section 4.1). Linguistic and Literary already route to gemma4:12b (Literary indirectly - see
  19.5) so a repair there is same-model and swap-free.
- **`Synopsis` ADDS ONE swap-capable path outside the editor picker, knowingly (`f2`, 2026-07-28) - this
  supersedes the earlier "`Synopsis` is a MEASURED HALT with `"Synopsis": false`" text that stood here.** q1
  HALTED it on precision (83% preservation, 1 structural false positive); `c3`'s classifier rule (7b) closed
  that failure mode and `q2` re-measured **100% (6/6) preservation, 0 false positives, over-rewrite 0** on the
  same fixtures, so `f2` set `"Synopsis": true` in both files and added the plain-text dispatch arms. Synopsis
  routes to `Summarization` -> **qwen3.5:9b** against TermRepair's **gemma4:12b**, so a LEAKING synopsis costs
  one cold load in (~21.5-23.4 s) plus one back out (~17.8-34.3 s) on the next Summarization-family call - the
  same asymmetric cost priced in 19.2. It stays inside this section's ACCEPT for three measured reasons:
  a clean synopsis makes **zero** model calls (19.1) and q2 measured **0 of 6** legitimate values reaching the
  model, so the cost is bounded to genuine leaks; `Synopsis` is produced **once per profile build**, not once
  per chapter (roughly 1/80th of `Summarization`'s frequency on this project's largest book); and
  `BuildBookProfileAsync` already interleaves qwen3.5:9b and gemma4:12b **generation** in a single
  `Task.WhenAll` with zero repair involvement, so the marginal swap surface added is small. Reversible in one
  config line per file (`"Synopsis": false`). Full reasoning: section 4.2 and the plan's `## f2 outcome`.
- **In a mixed editing session, task routing itself already dominates the swap budget.** An editor session
  alternating across analysis types already swaps models on every type change, independent of the repair
  layer - measured at roughly 6 minutes of load time across a 20-action mixed session, with ZERO involvement
  from TermRepair. TermRepair's marginal cost in that shape is ~0: a leaking Line Edit swaps to gemma, which
  the session's next Linguistic/Literary action needed anyway, so the repair pre-warms it.
- **The measured leak rate is low.** ~3% Summarization leak-and-repair rate (n=32 real-content runs, 1 repair;
  Wilson 95% CI 0.6-15.8%). Even in the worst realistic shape modeled (20 consecutive Line Edits on one
  chapter), the projected marginal cost is tens of seconds, not minutes, and is reversible in one config line
  (`Ai:AnalysisRepair.Mode=Glossary` or `Off`).
- **The two build-something options were disqualified by measurement, not taste.** A small co-resident repair
  model (so no swap is ever needed) is arithmetically ruled out on this card - see 19.6. Routing TermRepair to
  each task's own model would avoid the swap entirely but is unvalidated for repair quality on
  qwen3.5:9b/DictaLM (the d5/d6 gates in sections 14/18 measured gemma4:12b only), and Dicta is already known
  to over-rewrite prose in this role (see the model-choice rationale in section 2 / `_comment_TermRepair`).

### 19.5 What already routes to gemma4:12b and never swaps

| task | -> AiTaskType | model | swaps vs TermRepair? |
|---|---|---|---|
| LinguisticAnalysis | LinguisticAnalysis | gemma4:12b | no |
| LiteraryAnalysis | LinguisticAnalysis (indirect - no dedicated `FeatureModels:LiteraryAnalysis` key) | gemma4:12b | no |
| BookOverview / CharacterAnalysis / StoryAnalysis | LinguisticAnalysis (same indirection) | gemma4:12b | no |
| BookReview | BookReview | gemma4:12b | no |

**LiteraryAnalysis has no `FeatureModels:LiteraryAnalysis` key** - do not add one expecting it to change
anything, and do not describe LiteraryAnalysis as "routing to gemma4:12b" without this caveat. It reaches
gemma4:12b only because `AnalysisTaskMapping` maps `AnalysisType.LiteraryAnalysis` onto
`AiTaskType.LinguisticAnalysis`, which resolves `FeatureModels:LinguisticAnalysis`. The same indirection is
what puts BookOverview/CharacterAnalysis/StoryAnalysis on gemma4:12b.

### 19.6 VRAM: why a small co-resident repair model does not fit this card

For anyone tempted to eliminate the swap by loading a tiny repair model ALONGSIDE the task model: the card is
8,188 MiB total, and `nvidia-smi` FREE VRAM measured with a task model actually resident is only **465-823 MiB**
(gemma4:12b 592, qwen3.5:9b 465, DictaLM-3.0-Nemotron-12B 823) - most of the card is already spent on the
resident model's weights, KV cache, and the Windows desktop's own ~1.1 GB baseline. The smallest model present
on this host, `DictaLM-3.0-1.7B`, needs **1,056 MiB of weights alone**, before any KV cache - it does not fit
in the largest measured headroom. Co-residency of any two current models is arithmetically impossible on this
card; it was not tested further because 1,056 MiB does not fit in 823 MiB by arithmetic, not by hypothesis.

**Do NOT raise `OLLAMA_MAX_LOADED_MODELS`** to try anyway - it has previously OOM-wedged this GPU (memory
`pagedraft-ollama-8gb-tuning`; HTTP 500 after ~30 min with the GPU idle). If a >=16 GB GPU host ever exists, a
small co-resident repair model becomes the right shape of answer and should be revisited then (see the plan's
`## s4 decision`, option (c)) - not attempted on this hardware.

### 19.7 Field tripwire

No new instrumentation was added; the existing `TermRepair.span ... latencyMs=` Debug line (section 12.5) is
already the field signal. **A `TermRepair.span` `latencyMs` above roughly 10 seconds IS a cold model load**,
not slow inference - the warm marginal cost tops out under 3 seconds on this hardware (19.2). That threshold is
cheap to grep for in production logs if the swap rate ever needs to be watched in the field.

Full investigation, measurement, and decision detail: `src/.cursor/plans/_todo/termrepair-model-swap-thrash-2026-07-12.plan.md`
(`## Investigation findings`, `## s3 quality-gate results`, `## s4 decision`).
