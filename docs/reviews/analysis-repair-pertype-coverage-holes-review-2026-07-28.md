# Code Review Report - AnalysisRepair PerType coverage holes

## Scope reviewed

- **Plan:** [analysis-repair-pertype-coverage-holes-2026-07-28.plan.md](../../.cursor/plans/_todo/analysis-repair-pertype-coverage-holes-2026-07-28.plan.md) (all 8 todos `completed`). Its child plan `analysis-repair-coverage-followups-2026-07-28.plan.md` is entirely `pending` and is NOT in scope.
- **Pending files:** 15 modified + 2 untracked, all in `Pagedraft.Api-repo` (+1365 / -76). `pagedraft-client` is clean, so there is no user-visible FE surface in this change.
- **Branch:** `api-termrepair-batching`, already an ancestor of `origin/master`; the entire feature is the uncommitted working tree.
- **Feature purpose:** the analysis output-repair layer is gated by a strict `PerType` allowlist whose skip was completely silent. This change makes the skip observable at all four gate call sites behind one shared predicate, adjudicates the two absent types on evidence (`Custom` excluded by decision, `Synopsis` excluded by measurement), and adds a durable enum-completeness guard so a newly added `AnalysisType` cannot slip through undecided.
- **Suite:** GPU-safe filter run during this review: **1024 passed / 1 skipped / 0 failed**, matching the baseline the plan claims. Ollama was up throughout, so the filter was used verbatim and no live-model class was executed.

## P0 - Bugs that ship broken behavior

None. The production change is logging-only and the gate semantics are preserved exactly: `AnalysisRepairGate.Evaluate` ([AiOptions.cs:412-421](Pagedraft.Api/Services/Ai/AiOptions.cs#L412)) reproduces the three deleted `cfg is null || !cfg.Enabled || !PerTypeAllows(...)` copies term for term, and the two new explicit `"Custom": false` / `"Synopsis": false` keys are semantically identical to the absence they replace. `grep` confirms no fifth copy of the predicate survives anywhere in `Pagedraft.Api`.

## P1 - Code-quality issues

### 1. The d5/d6 quality gate's SHIP/HALT verdict is now computed over a corpus that includes a deliberately-unrepaired type

[OutputQualityDiagnostic.cs:1074-1077](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1074) appends `PreservationFixtureBooks.SynopsisCases` to the shipped `Cases` list unconditionally, and [:1283-1285](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1283) computes `tierPreservationPct` / `tierOverRewrite` over that merged list. Those two values are exactly what drives the harness's own decision table and its emitted verdict at [:1452-1488](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1452) (`**d6 VERDICT: PASS/HALT**`, and with it "SHIP LOCAL + Mode=GlossaryThenDynamic" vs "HALT - keep the SHIPPED default Mode=Glossary"). The d5 side has the same shape at [:489-497](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L489).

So the ship-or-roll-back gate for the dynamic repair stage across the **eight repaired types** now folds in six preservation values (and the cleaning values) belonging to `Synopsis`, a type this very change decided the product will never repair, and which scored 83% with one false positive. Today the mixed aggregate still clears the bar (roughly 26/27), so nothing flipped and no number in the docs is currently wrong. That is precisely what makes it fragile rather than visible: the model is stochastic, and one more Synopsis-shaped false positive on a re-run drops the aggregate below 90% and makes the harness emit a HALT recommending rollback of the shipped default for all eight types, on the strength of failures in a type that is not in the layer.

Two further consequences:

- The be-c08 figures of record (`21/21` preservation, `10/10` cleaning), pinned by docs sections 18 and the new 4.2, can no longer be read off the headline aggregate at all. Only the new subset section carries them.
- The child plan's `q2` scope (iii) requires re-running "the shipped eight types' d5 CLEANING and d6 PRESERVATION subsets ... shown UNCHANGED against the be-c08 figures". With the fixtures permanently merged into the corpus of record, that comparison is no longer available from the instrument's primary output.

The residual-weakness denominators at [:1494](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1494) and [:1500](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1500) are mixed the same way.

The subset reporting the change adds is the right idea applied one level too late: it separates the *report*, but not the *verdict*.

## P2 - Design / structural concerns

### 2. The enum oracle records `BookOverview` as plainly `Repaired`, contradicting the same plan's own finding

[AnalysisRepairConfigParityTests.cs:104](Pagedraft.Api.Tests/AnalysisRepairConfigParityTests.cs#L104) has `AnalysisType.BookOverview => Repaired()` and [:225](Pagedraft.Api.Tests/AnalysisRepairConfigParityTests.cs#L225) has it as `DispatchCoverage.BothSwitches`, both entirely unannotated. The plan's own `i1` investigation classified `"BookOverview": true` as a **dead allowlist key** (its only producer calls `RunRawAsync(structuredJson: null)`, both stages blank-guard to a no-op, and its one repairable field is discarded before persistence), and docs section 4.1 records that nuance in full.

`DecisionFor` is the artifact a future engineer reads when adding an `AnalysisType`; every other verdict in it carries its reasoning, and the single entry known to be anomalous carries none. That is the shape that produced this defect in the first place. The verdict itself should not change here (adjudicating the key is the child plan's `f1`), but the table must not read as an unqualified endorsement of a key the same commit documents as unreachable.

### 3. The two sibling gate-skip logs in `BookReviewService` report the same closed gate inconsistently

The glossary hook is called from inside `if (repairMode.RunsGlossary())` at [BookReviewService.cs:1135](Pagedraft.Api/Services/Analysis/BookReviewService.cs#L1135), so under `Mode=Off` or `Mode=Dynamic` [`ApplyGlossaryToFindings`](Pagedraft.Api/Services/Analysis/BookReviewService.cs#L2205) is never invoked and its new gate-closed line is never emitted. The dynamic gate at [:1175-1181](Pagedraft.Api/Services/Analysis/BookReviewService.cs#L1175) evaluates and logs **outside** any Mode check, by explicit design ("a Mode that simply does not select the dynamic stage is NOT logged here").

The design is defensible, but it is applied to one of the two sibling hooks and not the other, and the asymmetry is not stated anywhere. Concretely, with `Enabled=false` and `Mode=Off` an operator now sees exactly one line, `... gate closed (Disabled); skipping dynamic (span-scoped) repair`, and nothing at all about the glossary stage, which reads as "only the dynamic stage was gated out" when in fact the whole layer was. For a change whose entire purpose is that a gated-out type must never again be invisible, the glossary half of the BookReview path remains invisible under two of the four modes. This is the replicated-gate hazard `h1` set out to close, reappearing one layer up in the observability rather than in the predicate.

## P3 - Nit / polish

### 4. An XML doc comment was silently re-parented onto the wrong method

`FirstDiffContext` was inserted between `ChangeConfinedToRepairSpans`'s `<summary>` and its declaration, so [OutputQualityDiagnostic.cs:1562-1577](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1562) now gives `FirstDiffContext` two consecutive `<summary>` elements, the first of which describes over-rewrite span checking, and leaves [`ChangeConfinedToRepairSpans`](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1597) undocumented. Neither csproj sets `GenerateDocumentationFile`, so CS1571 never fires and nothing catches it.

### 5. A comment asserting byte-identity is itself the only thing that differs

[appsettings.Production.json:14](Pagedraft.Api/appsettings.Production.json#L14)'s `_comment_PerTypeExclusions` closes with "KEEP THIS IN SYNC with base appsettings.json's own `_comment_PerTypeExclusions` (byte-identical)". Its counterpart at [appsettings.json:95](Pagedraft.Api/appsettings.json#L95) lacks that closing sentence, so the two comments are not byte-identical at the moment the claim is written. The same sentence notes that `PerType_BaseAndProduction_AreEqual` compares only the boolean values, so nothing guards the claim either.

### 6. The d5 Synopsis subset section hardcodes the LOCAL tier

[OutputQualityDiagnostic.cs:857](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L857) iterates a literal `{ ("LOCAL", "A"), ("LOCAL", "B") }`, so when the CLOUD tier is measured its arms are omitted from the subset breakdown entirely, with no "not reported" row. The sibling d6 subset emitter at [:1341](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1341) does iterate `tiers` and prints an explicit `BLOCKED` line, so the two halves of the same new reporting feature disagree. q1's recorded cloud reproduction came from the d6 half only.

### 7. `cfg!` relies on a cross-file invariant with nothing binding it

[UnifiedAnalysisService.cs:2948-2950](Pagedraft.Api/Services/Analysis/UnifiedAnalysisService.cs#L2948) suppresses nullability with the comment "cfg is guaranteed non-null here: `AnalysisRepairGate.Evaluate` only returns Allowed when cfg is not null". Verified true against [AiOptions.cs:414](Pagedraft.Api/Services/Ai/AiOptions.cs#L414). The hazard is that the guarantee lives in a different file from the code that depends on it, with no compile-time link and no reciprocal note at the definition, so a future edit to `Evaluate` has nothing telling it that a caller dereferences on the strength of its return value.

## Cross-stack contract check

| Aspect | Backend | Frontend | OK? |
|---|---|---|---|
| Endpoints touched | none | none | n/a |
| DTO / response shape | unchanged | unchanged | n/a |
| `Ai:AnalysisRepair:PerType` base vs Production | 11 keys, byte-mirrored | n/a | ✓ guarded by `PerType_BaseAndProduction_AreEqual` |
| `Ai:AnalysisRepair:Mode` base vs Production | `GlossaryThenDynamic` both | n/a | ✓ guarded by `Mode_BaseAndProduction_AreEqual` |
| `AnalysisType` enum vs shipped allowlist | 12 members, all decided | n/a | ✓ new `EveryAnalysisType_HasAnExplicitRepairCoverageDecision` |
| Allowlist vs the two dispatch switches | 8 arms + BookReview via engine hook | n/a | ✓ new `EveryRepairedType_HasAnExplicitDispatchCoverageDecision`, but see P2 #2 |
| `_comment_PerTypeExclusions` base vs Production | differ by one sentence | n/a | ⚠ P3 #5 |
| Gate predicate copies | 1 (`AnalysisRepairGate.Evaluate`), 4 call sites | n/a | ✓ no surviving duplicate |

This is a server-only change with no HTTP surface, so Phase 5's endpoint / casing / enum-string checks do not apply and the Phase 4f live-browser render gate is **n/a** (no `pagedraft-client` file is touched).

## Plan deviations / outstanding manual work

- **`e1`'s enable-set came out EMPTY, so the plan's headline goal was not met by enabling anything.** Both coverage holes were closed by decision (`d1` EXCLUDE for `Custom`) and by measurement (`q1` HALT for `Synopsis`) rather than by wiring. This is an accepted and well-evidenced outcome, recorded in the plan, the config comments, the docs, and 17 regression pins. Worth carrying into the PR description, because "closed two coverage holes" and "decided two coverage holes stay closed" read very differently to a reviewer.
- **Four items were deliberately deferred to the child plan** `analysis-repair-coverage-followups-2026-07-28.plan.md`, all still `pending`: adjudicating the `BookOverview` dead key (`f1`), running `d1`'s own falsifier against real `Custom` rows (`c1`), closing the `CharacterAnalysis` / `StoryAnalysis` profile-path dynamic half (`c2`), and attacking the `Synopsis` structural false-positive class (`c3`), plus the `q2` GPU re-measure. P2 finding #2 above touches `f1`'s territory and is deliberately scoped to annotation only, not adjudication.
- **`Custom` was excluded before measurement, not by it.** Docs section 4.2 states this plainly, which is the right call; it remains the single largest un-measured assumption in the shipped decision set.
- **No FE work and no rendered-output artifact.** RULE 0 has no applicable surface here; the equivalent close for this feature class is the live-model quality gate, which `q1` performed and recorded.
- **The working tree is intentionally uncommitted** per the plan's Verification step 7.

## Recommended fix order

**Must-fix (P0):** none.

**Should-fix (P1):** #1 - scope the d5/d6 aggregate and verdict to the shipped corpus so the harness's SHIP/HALT decision stops depending on a type the product does not repair.

**Worth scheduling (P2):** #2 - annotate the `BookOverview` oracle entries with the dead-key finding and point at the child plan's `f1`. #3 - make the two BookReview gate-skip logs symmetric, or state the asymmetry where a reader will hit it.

**Nit (P3):** #4 re-parent the orphaned doc comment. #5 make the two `_comment_PerTypeExclusions` actually match. #6 iterate `tiers` in the d5 subset emitter. #7 add the reciprocal note at `AnalysisRepairGate.Evaluate`.
