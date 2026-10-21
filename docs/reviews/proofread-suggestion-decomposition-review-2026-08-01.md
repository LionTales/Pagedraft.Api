# Code Review Report — Proofread Suggestion Decomposition Fix

## Scope reviewed

- Plans: **none** (reactive fix driven by a live proofread test session; the only plan in `.cursor/plans/` is an unrelated June production-service plan). Review ran plan-less, so intent was reconstructed from the diff and the session.
- Pending files: 6 modified, 0 untracked, across 2 repos
  - `Pagedraft.Api-repo` (branch `ignore-cursor-scratch` @ 380726f): 4 files, +428/-66
  - `pagedraft-client` (branch `client-ai-tier-surface` @ 4a9efb0): 2 files, +32/-26
- Feature purpose: proofread suggestions were being emitted in a form that corrupts the document when accepted individually (`"היא היא ידעה"` → `"היא ידעה"` split into `"היא"→"י"` plus `"ידעה"→"דעה"`). The fix verifies that every emitted split can reproduce `resultText`, recovers three classes of previously-invisible correction, and removes a client filter that was hiding valid suggestions.

Lenses applied: **Phase 4c** (AI/LLM — model output parsing, empty/degenerate classification, harness fidelity), **Phase 4d** (FE async/optimistic — apply path), **Phase 4e** (backend heuristic / mirror-path — the FE `proofread-diff.ts` twin), **Phase 5** (cross-stack contract).

---

## P0 — Bugs that ship broken behavior

### 1. A word-insertion suggestion is emitted zero-width and can NEVER be applied

[SuggestionDiffService.cs:396-427](Pagedraft.Api/Services/Analysis/SuggestionDiffService.cs#L396) (`TrimCommonWholeWordAffixes`), consumed at [SuggestionDiffService.cs:119-131](Pagedraft.Api/Services/Analysis/SuggestionDiffService.cs#L119)

The new affix trim can consume the **entire** original span while leaving a non-empty suggestion, producing `StartOffset == EndOffset` and `OriginalText == ""`. Measured on the simplest possible input:

| Input | Result | Emitted suggestion |
|---|---|---|
| `abc def` | `abc xyz def` | `[4-4] "" => "xyz "` |
| `hello world` | `hello brave world` | `[6-6] "" => "brave "` |
| `שלום עולם` | `שלום גדול עולם` | `[5-5] "" => "גדול "` |
| `one two three` | `one two inserted three` | `[8-8] "" => "inserted "` |

**This is a regression introduced by this change.** Before the trim, the same input produced the correct, readable `"def" => "xyz def"`. The trim strips the shared trailing word `"def"` from both sides (both cuts land on a legal word boundary), emptying the original.

**Failure path — the correction is unreachable, silently.** [suggestion-anchor.service.ts:22-24](../../pagedraft-client/src/app/core/services/suggestion-anchor.service.ts#L22) guards an empty needle and returns `stale: true`. The apply handler then takes the stale branch at [editor-page.component.ts:1274-1284](../../pagedraft-client/src/app/features/editor/editor-page.component.ts#L1274), where `normalizedOriginal && currentText` is **falsy for an empty string**, so `idx = -1` and the handler `console.warn`s and `return`s. The user sees the suggestion card, clicks החל (Apply), and **nothing happens** — no error, no change.

Note the shape this hits: an inserted missing word is `מילים חסרות`, one of the error classes this proofread feature is explicitly meant to catch.

**Why the green suite missed it — and this is the more important finding.** Both new invariants hold vacuously for a zero-width span: `ComputeProofreadSuggestions_OriginalTextAlwaysMatchesItsOwnOffsets` compares `""` to `""`, and the reconstruction test successfully rebuilds `resultText` because inserting at `[4,4)` is arithmetically correct. Reconstruction is **necessary but not sufficient** — it does not reject degenerate-but-reconstructible spans. The fix must add a non-degeneracy assertion, or the next trim change re-opens this.

---

## P1 — Code-quality issues

### 2. The de-duplication exemption compares NORMALIZED text against UN-normalized output

[UnifiedAnalysisService.cs:3490-3495](Pagedraft.Api/Services/Analysis/UnifiedAnalysisService.cs#L3490) (`IsDeduplicationNotContentLoss`)

`deletion.OriginalText` is in normalized space (newlines collapsed to spaces by `NormalizeTextForAnalysis`), but `output` is the raw `ResultText`. `output.Contains(deleted)` therefore fails for any duplicated span containing a line break, and the run is flagged `ProofreadResultUnreliable` again.

Measured: a duplicated sentence carrying one `\n` → `raw output contains normalized span: False` → `flagged unreliable: True`.

This is the exact false positive the exemption was added to remove, still live for multi-paragraph duplicates — which is the more likely real-world shape of an accidentally duplicated passage. Fail-safe direction (over-flagging, not data loss), hence P1 rather than P0. Fix: normalize both sides before comparing.

---

## P2 — Design / structural concerns

### 3. The stated correctness contract is enforced only for SPLIT ranges, never for WHOLE ranges — and whole ranges are exactly what the relaxed guards now exempt

[SuggestionDiffService.cs:12-20](Pagedraft.Api/Services/Analysis/SuggestionDiffService.cs#L12) (contract), [SuggestionDiffService.cs:133-143](Pagedraft.Api/Services/Analysis/SuggestionDiffService.cs#L133) (guard exemption), [SuggestionDiffService.cs:432-470](Pagedraft.Api/Services/Analysis/SuggestionDiffService.cs#L432) (`SubRangesReproduceResult`)

The class docstring asserts "the emitted set must be able to reproduce resultText". `SubRangesReproduceResult` only decides *split vs whole* — a whole range's own mapping is never verified. Its correctness rests on a reasoned invariant stated in a comment: "a whole merged range is bounded by unchanged context sitting outside every diff block, so its mapping is exact."

Two guards (`origLen > 40 && sugLen <= 8`, `origLen > 25 && sugLen == 0`) were the previous net under that invariant, and this change removes them for exactly the unverified case. Before, a mis-mapped whole range was dropped; now it is emitted.

**Honest status: I could not construct a failing case, and the invariant is plausible** (outward word-boundary expansion plus `MergeGapThreshold = 1` should keep boundaries out of every block). But per the invariant-claim rule, the reassuring comment is precisely where the hazard would live, and the safety net and the verification no longer overlap. Worth an explicit check rather than an argument.

### 4. Both repos sit on branches carrying unrelated unmerged work

- `Pagedraft.Api-repo` is on `ignore-cursor-scratch`, one unrelated commit ahead of `master` ("Ignore .cursor/ local plan and review scratch").
- `pagedraft-client` is on `client-ai-tier-surface`, **five** commits ahead of `master` — the entire unmerged per-book model-tier feature.

Committing this fix here mixes an unrelated proofread correctness fix into the tier PR and the gitignore branch. The workspace convention is one fresh branch per PR, based on `master`.

### 5. Removing the client filter lets legacy corrupt rows surface

[analysis-panel.component.ts:279-311](../../pagedraft-client/src/app/features/analysis-panel/analysis-panel.component.ts#L279)

Suggestion rows persisted *before* this fix can still carry the corrupt giant-original/tiny-suggestion shape. The `origLen > 60 && sugLen <= 5` filter was hiding them; History views of old runs will now render them. Judged acceptable (the server is now the single source of truth, and re-running regenerates the rows) but it is a real behavior change that should be stated rather than discovered.

---

## P3 — Nit / polish

### 6. Stacked duplicate JSDoc blocks
[analysis-panel.component.ts:277-289](../../pagedraft-client/src/app/features/analysis-panel/analysis-panel.component.ts#L277) — the original one-line docblock was left in place above the new multi-line one. Two adjacent `/** */` blocks; only the second is picked up by tooling.

### 7. Two comments still reference the removed `applyHeuristicFilter` parameter
[analysis-panel.component.ts:1381](../../pagedraft-client/src/app/features/analysis-panel/analysis-panel.component.ts#L1381) ("No heuristic length filter: ...") and [analysis-panel.component.ts:1976](../../pagedraft-client/src/app/features/analysis-panel/analysis-panel.component.ts#L1976) ("keep all suggestions (no heuristic length-based filtering)"). Both explained why `false` was passed to an argument that no longer exists.

### 8. `IsAtWordStart` returns TRUE for an out-of-range index
[SuggestionDiffService.cs:428-430](Pagedraft.Api/Services/Analysis/SuggestionDiffService.cs#L428) — `index > text.Length || ...` reports "at a word start" for an impossible index. Currently unreachable (callers are bounded by `maxSuffix`), but the polarity is a trap if the helper is reused.

---

## Cross-stack contract check

| Aspect | Backend | Frontend | OK? |
|---|---|---|---|
| Suggestion DTO shape | `AnalysisSuggestion` (unchanged) | `AnalysisSuggestionDto` → `AnalysisSuggestion` | ✓ |
| Offset space | normalized (`NormalizeTextForAnalysis`) | normalized (`normalizeTextForAnalysis`) | ✓ |
| `OriginalText` non-empty | **not guaranteed** (P0-1) | `relocateOne` requires non-empty needle | ⚠ |
| Suggestion validity filtering | server verifies split reconstruction | filter removed, server trusted | ✓ (intended) |
| Legacy row shape | pre-fix rows unchanged in DB | no longer filtered | ⚠ (P2-5) |
| Duplicate diff implementation | `SuggestionDiffService` (DiffPlex) | `proofread-diff.ts` (diff-match-patch) | ✓ divergence now documented |
| `ProofreadDroppedContent` input | raw `ResultText` | n/a | ⚠ (P1-2, normalization mismatch) |

---

## Verification performed during review

- Full API suite: 1315 passed, 1 skipped (pre-existing), 0 failed.
- Full client suite: 1069 passed, 0 failed.
- **Live RULE 0 run** through the real API (port 5114) against Ollama on the restored planted-error fixture: 26 suggestions, 26/26 offset integrity exact, suggestion set reproduces `resultText` byte-for-byte, `proofreadResultUnreliable=False`.
- **Live browser check** (Chrome DevTools MCP, `he`): 25 cards rendered (26th correctly suppressed as previously `Dismissed`), including the 64→5 dedup card the removed client filter would have hidden. RTL correct.
- P0-1 and P1-2 were each **reproduced with a throwaway probe test** before being written up; the probe file was deleted afterwards.

Note: the live run and the browser check were performed against the code as it stands, which **contains P0-1**. The fixture's model output happened not to include a whole-word insertion, so the zero-width shape did not occur in that run. The render gate therefore does not clear P0-1 and must be re-run after the fix.

---

## Plan deviations / outstanding manual work

- No plan existed; this was reactive. Review fidelity is correspondingly lower on "was this the intended scope" questions.
- The `מן   הים` whitespace error class is structurally unreachable through the analysis path (the server's SFDT→text extraction collapses whitespace runs before the model sees them). Not a defect in this diff; worth a separate decision.
- The model's gender fix `נעמי קרא → נעמי קראו` is wrong (should be `קראה`). Model quality, out of scope here.

---

## Recommended fix order

**Must-fix (P0):** 1 — zero-width suggestion, plus the non-degeneracy assertion that would have caught it.
**Should-fix (P1):** 2 — normalize both sides of the dedup comparison.
**Worth scheduling (P2):** 3 — verify whole-range mappings, or reinstate a bound for them; 4 — rebase onto fresh branches off `master` before opening PRs; 5 — accept and note the legacy-row behavior change.
**Nit (P3):** 6, 7, 8.
