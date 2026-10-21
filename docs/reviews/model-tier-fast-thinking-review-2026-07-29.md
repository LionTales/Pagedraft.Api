# Code Review Report - Fast/thinking model tier

## Scope reviewed

- **Plan:** `src/.cursor/plans/_todo/model-tier-fast-thinking-2026-07-28.plan.md` (13 todos, all `completed`).
- **Pending files:** API `Pagedraft.Api-repo` 32 modified + 12 untracked (1323 insertions), branch `api-provider-agnostic-per-task-tuning`. Client `pagedraft-client` 12 modified + 5 untracked (189 insertions), branch `client-ai-tier-surface`. Nothing committed on either.
- **Feature purpose:** a per-book, opt-in model tier (fast = local Ollama, thinking = cloud OpenRouter) for exactly two measured tasks (LinguisticAnalysis, Hebrew Proofread), on top of a phase-1 fix that made per-task provider tuning provider-agnostic so a cloud-routed whole-book task can no longer silently collapse its context window to 4096.
- **Lenses applied:** 4c (AI/LLM - the defining class here), 4d (FE async/optimistic), 4e (mirror-path), 4f (live browser render gate), 4g (migration delivery), Phase 5 (cross-stack contract).

**Verification I re-ran myself, not relayed from the plan:**

| suite | result |
|---|---|
| API deterministic `--filter "FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine"` | 1221 passed, 0 failed, 1 skipped (pre-existing DOCX fixture) |
| API standing narrow filter (9 terms incl. `~AiTier`) | 285 passed, 0 failed |
| Client `npm run test:ci` (ChromeHeadless, full suite) | 1057 SUCCESS, 0 failed |
| Phase 4f browser gate (live API :5114 + UI :4201, real dev DB) | **PASSED** - see below |

**Browser gate detail (what I actually looked at, not what the plan reported).** Book `3cbc3f26` (Hebrew), editor review mode:

- he/RTL at 1440x1000: `dir="rtl"` resolved (computed, not just bound), chip "מהיר", both copy blocks and the privacy paragraph present, routes `Ollama / gemma4:12b` x2 tagged מקומי, `scrollWidth === clientWidth` (301/301), no em-dash anywhere in the card.
- Opt-in flow driven live: clicking "חשיבה" wrote NOTHING and opened the consent panel; confirming flipped the chip to "חשיבה" and both routes to `OpenRouter / google/gemma-4-31b-it` tagged ענן.
- **Inverse verb driven** (the verb a render pass usually skips): "מהיר" wrote back with no consent step; server `GET` re-read confirms `tier: "fast"`. Book left as found.
- 390x844: the two options stack (277px each, tops 427/504), card contributes no horizontal overflow.
- Console: one 404 from `GET /api/books/{id}/profile`, which is documented to 404 when no profile exists. Nothing from the tier surface.
- Runtime contract checked against the live API: `GET /api/books` carries `aiTier: "fast"`; `GET /api/books/{id}/ai-tier` returns `{bookId, tier, thinkingReadiness, fallbackActive, routes[]}` matching the TS interfaces exactly.

This is a strong, unusually well-evidenced feature: RULE 0 was genuinely closed on both a single-shot and a chunked run, every todo carries a mutation check, and several premises were corrected rather than worked around. **No P0.** The findings below are real but none of them ship broken user-visible behaviour today.

---

## P0 - Bugs that ship broken behavior

None found.

---

## P1 - Code-quality issues

### 1. Five deterministic guard tests, including the one the plan calls "THE GA GATE", run in NEITHER verification path

[LinguisticGoldSchemaTests.cs:9](Pagedraft.Api.Tests/LanguageEngine/LinguisticGoldSchemaTests.cs#L9) declares `namespace Pagedraft.Api.Tests.LanguageEngine`. The class's own xmldoc says "DETERMINISTIC (no model, no GPU, no network)", but the namespace is the one the project's two standing filters exclude or miss. Measured, not inferred:

| filter | result |
|---|---|
| `FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine & ~LinguisticGoldSchema` | **`No test matches the given testcase filter`** |
| the plan's 9-term standing narrow filter | 285 tests |
| the same filter **plus** `FullyQualifiedName~LinguisticGoldSchema` | 290 tests |
| `~LinguisticGoldSchema` alone | 5 tests, all pass |

So the 5 tests execute only under a bare `dotnet test`, which this project forbids (the GPU filter trap, memory `pagedraft-bookreview-test-filter-gpu-trap`, restated as Verification item 2 of the plan itself). Among them is [`Gold_EveryHebrewCase_DeclaresItsNativeSpeakerValidationState`](Pagedraft.Api.Tests/LanguageEngine/LinguisticGoldSchemaTests.cs#L113), written expressly so a new AI-authored Hebrew case cannot be added without declaring itself a draft - a standing GA gate. A guard that cannot fire is not a guard, and this one was added by this very feature to close a hole it now cannot see.

### 2. "The tier is resolved ONCE for the whole build" is false at two of the four sites the comment names

[StyleBaselineService.cs:307](Pagedraft.Api/Services/Analysis/StyleBaselineService.cs#L307) states the build resolves the tier once "so the pre-status, the per-chapter rebuilds, the post-status and the BookStyleBaseline stamp all agree on which model is active", and [:489-491](Pagedraft.Api/Services/Analysis/StyleBaselineService.cs#L489) repeats the rationale ("re-reading it here could observe a tier flipped mid-build and report every just-built profile as stale"). The tier really is threaded to pre-status, post-status and the stamp. It is **not** threaded to the other two:

- [StyleBaselineService.cs:454](Pagedraft.Api/Services/Analysis/StyleBaselineService.cs#L454) calls `LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, lang, ct)`, which re-reads `Book.AiTier` for itself at [AnalysisContextService.cs:222](Pagedraft.Api/Services/Analysis/AnalysisContextService.cs#L222);
- [StyleBaselineService.cs:487](Pagedraft.Api/Services/Analysis/StyleBaselineService.cs#L487) calls `BuildBookStyleAverageProfileAsync(bookId, lang, ct)`, which re-reads it at [AnalysisContextService.cs:389](Pagedraft.Api/Services/Analysis/AnalysisContextService.cs#L389).

A tier flip during a multi-minute build (the feature's own consent screen quotes ~3 minutes for 5 chapters) therefore produces exactly the state the comment claims to prevent: chapters stamped under two different models, then an average computed at the NEW tier that **excludes** every row built at the old one. The plan's own p3-3 section 3 names that exclusion as "the RISKIEST of the three ... silent null, no error, no log": `average == null` short-circuits `RunBuildAsync` and the baseline is never persisted, while `postStatus` - computed at the OLD tier - reports the freshly-built chapters as stale and inflates `FailedChapters`.

No test covers it: `AiTierStalenessTests` has `FlippingOneBooksTier_MakesThatBookStale_AndLeavesEveryOtherBookFresh` (flip, then read) but nothing that flips DURING a build.

The comment is also a load-bearing invariant claim that disarms the next reader, which is the failure mode this workspace's conventions single out.

---

## P2 - Design / structural concerns

### 3. One extra `Books` query per chapter per baseline build

A direct consequence of P1-2's shape: [BookAiTierResolver.ResolveAsync](Pagedraft.Api/Services/Analysis/BookAiTierResolver.cs#L34) is now called once inside every `LoadOrBuildChapterStyleProfileAsync` and once inside `BuildBookStyleAverageProfileAsync`, so a 40-chapter build adds 41+ round trips that the "resolve once" design was supposed to have eliminated. Threading the tier (P1-2's fix) removes them.

### 4. `RunAsync` and the chunked runners each resolve the tier separately, and they decide different things

[UnifiedAnalysisService.cs:347](Pagedraft.Api/Services/Analysis/UnifiedAnalysisService.cs#L347) resolves the tier and uses it to pick the chunk SIZE; [:2070](Pagedraft.Api/Services/Analysis/UnifiedAnalysisService.cs#L2070) (`RunProofreadChunkedAsync`) and [:1791](Pagedraft.Api/Services/Analysis/UnifiedAnalysisService.cs#L1791) (`RunLineEditChunkedAsync`) resolve it again and use it to pick the ROUTE. A flip between the two reads sizes chunks for one provider and sends them to another. Inert at shipped values (both tiers resolve Proofread at num_ctx 4096, pinned by `ChunkThresholdTierParityTests`), so this is structural rather than live - but the two reads should be one value passed down, exactly as `StyleBaselineService` intended.

### 5. The whole-book unsized-window WARNING evaluates the FAST route, so it would miss the very misconfiguration it exists to catch

`ResolveBudgetTokens` defaults `tier = AiTier.Fast` ([BookContextAssembler.cs:232](Pagedraft.Api/Services/Analysis/BookContextAssembler.cs#L232)) and both real call sites - [:392](Pagedraft.Api/Services/Analysis/BookContextAssembler.cs#L392) and [:442](Pagedraft.Api/Services/Analysis/BookContextAssembler.cs#L442) - take that default. That means [`WarnIfWholeBookWindowIsUnsized`](Pagedraft.Api/Services/Analysis/BookContextAssembler.cs#L290) resolves the FAST route's provider before deciding whether to warn. p3-3 correction 4 and p3-4 correction 6 both record the BUDGET half of this as a deliberate, pinned no-op (`TheWholeBookBudget_IsUnmovedByTheTier_AtTheShippedValues`). Neither mentions the WARNING half, and nothing pins it. If a whole-book task ever joins `TieredTasks`, the observability added specifically to make the 4096-collapse loud would check the wrong provider and stay silent. This is the emitter-reachability trap (Phase 4c item 22a) on a brand-new emitter.

### 6. `ProofreadQualityTests.ProofreadModel` stays a hand-maintained duplicate of shipped config, in a file the guards cannot reach

[ProofreadQualityTests.cs:77](Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs#L77) now reads `gemma4:12b` under a comment stating outright that it and `Ai:FeatureModels:Proofread:Model` "are NOT read from the same source at test time". This session both changed that value AND built the machinery that would bind it mechanically (`AiTierConfigParityTests` binds the real `appsettings.json`). The pin was not added - and per P1-1 it could not have lived in this file anyway. I verified the current values do agree (`Proofread => Ollama / gemma4:12b`), so this is drift risk, not drift.

### 7. The client caches chunk thresholds by language only and never sends `?tier=`

[analysis.service.ts:31](src/app/core/services/analysis.service.ts#L31) gained the `tier` parameter and documents the exact condition under which it must start being sent, and the server pins the no-movement result. Recorded as an accepted deviation (p3-4 correction 5) rather than a defect, but note the guarantee rests on two config values happening to agree (`OpenRouter_Proofread` declares NumCtx 4096, equal to the local effective 4096) and there is no CLIENT-side test that would go red - only a server one.

### 8. Same-key request supersession in the tier control

[book-ai-tier.component.ts:71](src/app/features/book-dashboard/book-ai-tier.component.ts#L71) and the post-failure re-read at [:271](src/app/features/book-dashboard/book-ai-tier.component.ts#L271) guard only on `this.bookId !== bookId`, which is a context-CHANGE guard. Two GETs for the SAME book (a `bookLanguage` change landing while a read is in flight, or a `reload()` racing the post-409 re-read) can resolve out of order and let the older snapshot win - Phase 4d item 10(a), which explicitly notes a context guard does not cover this. Blast radius is a stale readiness/routes display until the next change, so it is P2, not P1.

---

## P3 - Nit / polish

### 9. The radiogroup disables its own checked radio

[book-ai-tier.component.html:32](src/app/features/book-dashboard/book-ai-tier.component.html#L32) and [:44](src/app/features/book-dashboard/book-ai-tier.component.html#L44) put `[disabled]` on the currently-selected option. `aria-checked` is correct (verified in the browser), but a disabled control is not focusable, so a keyboard user can never land on the checked radio - and in the not-ready case BOTH options are disabled and the whole `role="radiogroup"` has no reachable tabstop at all. Prefer `aria-disabled` plus a no-op handler, or real `<input type="radio">` elements.

### 10. `_comment_Proofread_en` does not say the key is now load-bearing

Since p2-3, `Proofread_en` and `Proofread` are byte-identical (`Ollama / gemma4:12b`), so the key now reads as a redundant duplicate a cleanup would delete. Its only remaining job is to be the E3 fence that stops an English book reaching `Proofread_thinking`. `_comment_ModelTier` explains the precedence and `AiTierConfigParityTests.TheShippedProofreadEnEntry_IsFullyConfigured_...` pins it (it does run - it is inside the 285), so the risk is bounded. But the comment a reader edits when touching that key is silent about it.

### 11. `ProviderCredentials.Clean` returns the untrimmed value

[ProviderCredentials.cs:63](Pagedraft.Api/Services/Ai/ProviderCredentials.cs#L63) returns `raw` while the placeholder check one line above operates on `trimmed`. A key pasted with a trailing newline (a common user-secrets artifact) is therefore sent verbatim as a Bearer token and fails as a malformed header rather than as a bad key. The two halves of the method disagree about what the value is.

### 12. `usesTier` compares provider names case-sensitively

[AiTierStatusService.cs:116](Pagedraft.Api/Services/Ai/AiTierStatusService.cs#L116) uses `StringComparison.Ordinal` for the provider comparison while the provider registry, `_providers.ContainsKey` at [:158](Pagedraft.Api/Services/Ai/AiTierStatusService.cs#L158) and `ProviderCredentials` are all case-insensitive. A config spelling one key's provider `ollama` and the other `Ollama` would paint a phantom "Cloud" tag on a route that did not move.

### 13. No `ngOnDestroy` on the tier control - verified NOT a defect, recorded anyway

[book-ai-tier.component.ts:50](src/app/features/book-dashboard/book-ai-tier.component.ts#L50) holds no subscriptions and calls `cdr.detectChanges()` from async callbacks, and the component IS `@if`-destroyed in normal use (it is mounted inside the editor's review mode). I drove exactly that in the live browser - delayed the `ai-tier` GET by 3s, unmounted mid-flight - and Angular 18 did not throw and logged nothing. So this is robustness polish, not a bug. Recording it because the component's own comment describes its read lifecycle without saying what happens on teardown, and because a future Angular upgrade may make a destroyed-view `detectChanges()` throw.

---

## Cross-stack contract check

| Aspect | Backend | Frontend | OK? |
|---|---|---|---|
| `GET /api/books/{bookId}/ai-tier` | [BooksController.cs:106](Pagedraft.Api/Controllers/BooksController.cs#L106) -> `BookAiTierDto` | `AiTierService.get` -> `BookAiTierDto` | ✓ verified live |
| `PUT /api/books/{bookId}/ai-tier` | body `UpdateBookAiTierRequest { Tier }` | `{ tier }` | ✓ verified live |
| Readiness enum values | `AiTierReadiness` camelCased in `BuildAiTierDto` | TS union `'ready' \| 'routeNotConfigured' \| 'providerNotRegistered' \| 'providerCredentialsMissing'` | ✓ all four match |
| `BookDto.aiTier` | normalized `ToDto` / `BookDetailDto` | required `AiTierValue` | ✓ verified live on `GET /api/books` |
| `400` unrecognized tier / `409` unroutable | `{error, allowed}` / `{error, reason}` | reads `err.status === 409` only | ✓ (body unused, acceptable) |
| `GET /api/config/analysis-chunk-thresholds` `?tier=` | accepted, defensively parsed | parameter exists, **not sent** | ⚠ P2-7 (accepted deviation) |
| `PUT /api/books/{bookId}` vs `AiTier` | field-by-field update, does not touch `AiTier` | n/a | ✓ verified at [:1094-1104](Pagedraft.Api/Controllers/BooksController.cs#L1094) |
| Property casing | `JsonNamingPolicy.CamelCase` | camelCase interfaces | ✓ verified live |
| Harness tuning mirrors shipped config | `Ollama_Proofread {0.2, NumPredict 4096}`, `OpenRouter_Proofread {0.2, MaxTokens 4096, NumCtx 4096}` | hand-copied in `ProofreadQualityTests` / `LinguisticQualityTests` | ✓ values agree today; unpinned (P2-6) |

---

## Plan deviations / outstanding manual work

All of these are recorded in the plan and accepted; they belong in the PR description.

- **Client does not send `?tier=`** to the chunk-thresholds endpoint (p3-4 correction 5). Condition for wiring it is written on the method.
- **`AssembleAsync` sizes the whole-book budget at Fast** (p3-3 correction 4, p3-4 correction 6). Pinned inert; the warning half is not (P2-5).
- **All Hebrew user-facing copy is an AI-authored DRAFT** pending native-speaker review - the tier control's 26 labels and the new `consentPaidNote`. Standing GA gate, unchanged.
- **18 of the 32 linguistic gold cases are AI-authored Hebrew drafts.** The p2-4 GO margin (0.125) must not be presented as validated on Hebrew.
- **The 0.125 GO margin is a LOWER BOUND** - cloud hit the scoring formula's 0.900 ceiling in 2 of 3 runs, so the desaturated gold is already saturated for the cloud model.
- **Chunked runs cannot name their model in the UI** (`ModelName = "chunked"` sentinel). Pre-existing; RULE 0's chunked half rests on outbound-request evidence.
- **BookReview's cloud path has never been run** - p1-3's RULE 0 hand-off is still open; the bake-off harness cannot route OpenRouter.
- **`RepeatPenalty` has no cloud counterpart**; latent because LineEdit is not tiered.
- **Migration `20260729103358_AddBookAiTier` was applied to the dev DB, and accidentally also to the stale `(localdb)\MSSQLLocalDB`** during a mis-launched Production-environment start (p3-4 section 8). Additive nullable column, harmless, but it should be stated in the PR.
- **`OpenAiProvider` / `AzureOpenAiProvider` / `AnthropicProvider` still do not strip the committed `__PLACEHOLDER__` API key** on their own paths - a latent 401-that-reads-as-a-bad-key for those three. Explicitly out of scope (p3-4 section 10).

---

## Recommended fix order

**Must-fix (P1):** 1 (the guard tests that never run), 2 (the false resolve-once invariant, plus the comment it rests on).

**Should-fix (P2):** 4 (single tier read per run), 5 (tier the warning, or pin its inertness), 6 (mechanical pin for the harness model constant), 8 (supersede same-key reads); 3 falls out of fixing 2; 7 stays a documented deviation.

**Worth scheduling (P3):** 9 (radiogroup focusability), 11 (trim the key), 12 (case-insensitive provider compare).

**Nit (P3):** 10 (comment), 13 (teardown polish).
