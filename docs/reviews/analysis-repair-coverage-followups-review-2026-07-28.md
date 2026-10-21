# Code Review Report - AnalysisRepair coverage follow-ups (f1 / c1 / c2 / c3 / q2 / f2)

## Scope reviewed

- **Plan:** [analysis-repair-coverage-followups-2026-07-28.plan.md](../../../.cursor/plans/_todo/analysis-repair-coverage-followups-2026-07-28.plan.md), all 6 todos `completed` (`f1`, `c1`, `c2`, `c3`, `q2`, `f2`).
- **Pending files:** 13 modified + 2 untracked in `Pagedraft.Api-repo` (+1570 / -234 across 15 files). `pagedraft-client` is clean, so there is no user-visible FE surface and Phase 4f does not apply.
- **Branch:** `api-analysis-repair-pertype-coverage`, fully pushed (5 commits). The feature under review is the uncommitted working tree on top of it, i.e. the parent review's fix plan is committed and this is its child plan's output.
- **Feature purpose:** close the four items the parent `pertype-coverage-holes` plan left open. `f1` adjudicated the `"BookOverview": true` dead key (KEEP, because the controller path is genuinely live), `c1` measured the real `Custom` corpus (exclusion stands, benefit side empty), `c2` made the book-profile repair hook two-stage (glossary + dynamic) so `BookProfile.CharactersJson` / `StoryStructureJson` stop receiving only the closed 23-term glossary, `c3` added `ForeignRunClassifier` rule (7b) (a Title-Case Latin run at a paragraph head is LEAVE), `q2` measured all three scopes on a live GPU, and `f2` enabled `Synopsis` in `PerType` plus a plain-text dispatch arm in two switches.
- **Suite:** GPU-safe filter run during this review (verbatim from the plan's Verification item 1): **1040 passed / 1 skipped / 0 failed**, exactly matching the number `## f2 outcome` reports. Build clean. Ollama was up throughout and no live-model class executed.
- **Overall:** this is unusually careful work. The measurement discipline (q2's honesty note about the died-and-rerun d5/d6 invocation, the "read the 100% as a GATE property, not a model property" qualifier carried into four separate surfaces, the `git diff -U0` instrument-integrity check, the pre-registered attribution rule for scope (iii)) is the standard the rest of this file measures against. Every finding below is a consequence of `f2` moving `Synopsis` from excluded to shipped and not sweeping every place that fact was recorded.

## P0 - Bugs that ship broken behavior

None. Under the shipped config (`Enabled=true`, `GuardOnly=true`, `Mode=GlossaryThenDynamic`) the behaviour change is exactly what `q2` measured: the profile hook now runs the dynamic stage, and `Synopsis` is repaired through `RunRawAsync`'s existing repair seam. Both are pinned by new deterministic tests that I read for vacuity and that carry their own non-vacuity controls.

## P1 - Code-quality issues

### 1. `Synopsis` was enabled in TWO of the THREE dispatch switches; the third is a silent half-enable with no recorded decision and no oracle that can see it

`f2` added a `Synopsis` arm to [GlossaryRepairPass.cs:137](Pagedraft.Api/Services/Analysis/GlossaryRepairPass.cs#L137) and [DynamicTermRepairService.cs:496](Pagedraft.Api/Services/Analysis/DynamicTermRepairService.cs#L496). There is a **third** per-type dispatch switch: [AnalysisRepairService.cs:236-361](Pagedraft.Api/Services/Analysis/AnalysisRepairService.cs#L236), the value-scoped LLM stage that `ApplyAnalysisRepairAsync` invokes when `GuardOnly=false`. It carries the other eight arms (including `BookOverview` at [:300](Pagedraft.Api/Services/Analysis/AnalysisRepairService.cs#L300)) and has no `Synopsis` arm, so `Synopsis` falls into the `default:` at [:361](Pagedraft.Api/Services/Analysis/AnalysisRepairService.cs#L361) that is documented as "Proofread and everything else: NEVER repaired".

That the layer gate opened for `Synopsis` and the switch then silently declines it is the exact shape this feature exists to eliminate. The evidence it was not a decision:

- The plan file contains **no occurrence** of `AnalysisRepairService`, `GuardOnly`, or "third switch". The word never appears in `## f2 outcome`.
- The docs know there are three: the `BookOverview` row says "a dispatch target in **all three** switches" ([ANALYSIS_OUTPUT_REPAIR.md:273](docs/ANALYSIS_OUTPUT_REPAIR.md#L273)), while the new `Synopsis` row says "a dispatch arm in **both** switches" ([:274](docs/ANALYSIS_OUTPUT_REPAIR.md#L274)) with no note that the third was considered and declined.
- `AnalysisRepairExclusionRegressionTests`' own class doc still states that Proofread's exclusion is restated "inside all three dispatch switches' own default/fallback arms" - so the file that pins these surfaces counts three, while the new `ShippedSynopsisCoverage_IsWiredAtEveryEnablingSurface` ([AnalysisRepairExclusionRegressionTests.cs:288](Pagedraft.Api.Tests/AnalysisRepairExclusionRegressionTests.cs#L288)) checks two and calls itself "every enabling surface".
- The enum oracle cannot catch it: `DispatchCoverage` ([AnalysisRepairConfigParityTests.cs:225-280](Pagedraft.Api.Tests/AnalysisRepairConfigParityTests.cs#L225)) models only `GlossaryRepairPass.Apply` and `DynamicTermRepairService.ApplyAsync`. `BothSwitches` is asserted for `Synopsis` at [:277](Pagedraft.Api.Tests/AnalysisRepairConfigParityTests.cs#L277) under a comment saying "half-enabling (one arm only) would ship half the layer" - true, and the guard is one switch short of being able to say it.

Impact is latent rather than live: `GuardOnly=true` ships, so the stage does not run today. But `GuardOnly=false` is documented state (3) in `_comment_AnalysisRepair`, a one-line config flip, and in that state `Synopsis` would be the only Repaired-via-dispatch type getting glossary + dynamic but not the value-scoped LLM stage, silently. Reachability is not a defence here; the missing artefact is a stated decision and a guard that can enforce it.

### 2. The d5/d6 quality gate's SHIP/HALT verdict now EXCLUDES a shipped repaired type, and its emitted report asserts the opposite of what the same change made true

`be-c01` (committed, correct at the time) scoped the d5/d6 aggregates and verdicts to the "shipped corpus of record" and pushed the `Synopsis` fixtures into a reported-but-not-gating subset, on the stated ground that `Synopsis` is not a repaired type. `f2` made `Synopsis` a repaired type and left that scoping and its prose untouched, which inverts the original defect: the harness's ship/HALT decision for the dynamic stage is now computed over a corpus that omits a type the product does repair.

The rationale comments are at [OutputQualityDiagnostic.cs:496-501](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L496) and [:1183-1189](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1183); the index split at [:502-509](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L502) and [:1190-1200](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1190). Three of the false statements are **emitted into the report an operator reads**, not merely comments:

- [:515-518](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L515) - "the corpus of record for the eight analysis types the dynamic repair layer runs over ... `Synopsis` is not a repaired type, so they are NOT part of the ship/HALT decision."
- [:829-831](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L829) - same claim, on the d5 decision table.
- [:1211](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1211) - same claim, on the d6 aggregate.

Two further comment sites repeat it at [:773](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L773) and [:1188](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1188). Meanwhile [ANALYSIS_OUTPUT_REPAIR.md:257-262](docs/ANALYSIS_OUTPUT_REPAIR.md#L257) now says ten of twelve members are Repaired and names `Synopsis` among them, so the instrument and the document of record disagree on the shipped set.

The consequence is the mirror of the one `be-c01` fixed and it is worse in one respect: a `Synopsis` regression on a later stochastic re-run cannot lower the headline verdict at all, so the harness will emit PASS for a layer that is failing on a type it ships. The residual risk `f2` itself records (an external proper noun at a mid-paragraph sentence head still reaches the repair model, and a synopsis is the value shape most likely to contain one) is precisely the regression this scoping is now blind to.

The structural cause is that shipped-set membership is hardcoded twice as a positional split (`i < shippedSeedCount`, `BookKey == SynopsisBookKey`) rather than derived from the shipped `PerType` map, so the next enable will strand its corpus the same way.

## P2 - Design / structural concerns

### 3. The `RunRawAsync` seam comment still lists `Synopsis` as a strict no-op, on the exact seam `f2` relies on

[UnifiedAnalysisService.cs:968-971](Pagedraft.Api/Services/Analysis/UnifiedAnalysisService.cs#L968): "for the non-target types RunRawAsync also serves (BookOverview / Synopsis / CharacterAnalysis / StoryAnalysis) it is a strict no-op that returns the text byte-identical."

`Synopsis` is no longer in that set, and this is not an incidental mention: `CompleteDeferredRepairAsync` is the only reason `f2` covers the profile-build path, a fact the docs row at [ANALYSIS_OUTPUT_REPAIR.md:274](docs/ANALYSIS_OUTPUT_REPAIR.md#L274) treats as load-bearing ("Because the arm is plain-text, this covers BOTH producer paths") and the new test `BuildBookProfileAsync_UnderTheShippedConfig_RepairsSynopsis` exists to pin. A reader who trusts this comment concludes the persisted `BookProfile.Synopsis` is untouched, which is the opposite of what ships. `BookOverview`'s membership in the list is still correct and is separately pinned by the new `BookOverviewRepairPathAsymmetryTests`, so only the `Synopsis` entry is wrong.

### 4. `c2`'s "do not re-litigate" doc comment states `Synopsis` stays excluded; `f2` un-excluded it in the same uncommitted change

[BookIntelligenceService.cs:699-704](Pagedraft.Api/Services/Analysis/BookIntelligenceService.cs#L699): "NO GPU-SWAP SURFACE IS ADDED BY THIS - and that is the load-bearing difference from Synopsis, so do not re-litigate it ... Synopsis routes to `AiTaskType.Summarization` -> qwen3.5:9b, so repairing IT would force a model swap per build; that (plus q1's measured 83% preservation) is why **Synopsis stays excluded** and these two do not."

The first half is still true and worth keeping (the no-swap argument for CharacterAnalysis / StoryAnalysis is exactly right). The last clause is false, and the "do not re-litigate" framing is what makes it a P2 rather than a nit: it is written to stop the next reader from checking, and the two facts it cites as settled were both overturned within the same working tree (`q1`'s 83% by `q2`'s 100%, and the swap cost from "disqualifying" to "accepted knowingly" per [ANALYSIS_OUTPUT_REPAIR.md:426-437](docs/ANALYSIS_OUTPUT_REPAIR.md#L426)).

### 5. Rule (7b) fires on ANY hard line break, while every argument offered for it is about a PARAGRAPH boundary

[ForeignRunClassifier.cs:631-649](Pagedraft.Api/Services/Analysis/ForeignRunClassifier.cs#L631) returns true as soon as it walks left onto a single `'\n'` or `'\r'`. The rule's justification, at [:196-217](Pagedraft.Api/Services/Analysis/ForeignRunClassifier.cs#L196) and again in [ANALYSIS_OUTPUT_REPAIR.md:398-409](docs/ANALYSIS_OUTPUT_REPAIR.md#L398), is a discourse argument scoped to paragraph openings: "opening a fresh paragraph is a PLANNED move authored in the target language". That argument does not carry to a soft line break inside one paragraph, to a wrapped line, or to a line in a newline-separated list whose marker is a character `IsTransparentOpen` happens not to block.

The implementation is internally consistent (it matches `IsSentenceTerminator`, which already treats `\n` as terminal) and the widening direction is safe for precision, since (7b) only converts REPAIR to LEAVE. The gap is in the evidence: every fixture in `SynopsisCases`, `SynopsisLeakCases` and the d5 corpus uses `\n\n`, so `q2` scope (iii)'s "zero recall cost" and the model-free pin `Rule7b_SparesNoLeakInEitherD5CleaningCorpus` ([ForeignRunClassifierTests.cs:161](Pagedraft.Api.Tests/ForeignRunClassifierTests.cs#L161)) both measure the double-newline shape only. The single-newline shape is unmeasured, undocumented as included, and is a realistic model-output shape for the exact multi-line prose fields this layer repairs. Either the predicate should require a blank line, or the naming and the argument should be restated in terms of a line break with the widened exposure acknowledged and pinned.

### 6. The file-size waiver, written specifically to correct a mis-measured line count, is itself off by exactly its own length

[BookIntelligenceService.cs:21-46](Pagedraft.Api/Services/Analysis/BookIntelligenceService.cs#L21) opens "with the numbers MEASURED rather than quoted, because the c2 report that recommended this waiver quoted them wrong" and states at [:25](Pagedraft.Api/Services/Analysis/BookIntelligenceService.cs#L25) "this file is **831** lines ... the real figures are **734 -> 831** (`git show HEAD:...` and `wc -l`)".

`wc -l` reports **857**. `git diff --numstat` reports `151/28`, not the `125/28` the waiver's arithmetic uses; 734 + 151 - 28 = 857. The 26-line delta is the waiver block itself: the file was measured before the comment was inserted, and the comment then reported the pre-insertion number as the post-change figure. `## f2 outcome` item 1 in the plan carries the same 831.

Nothing downstream depends on the number, but this is the one comment in the diff whose entire warrant is that its figures were measured rather than quoted, and it reproduces the failure it was written to correct. The waiver's substantive conclusions are unaffected: 857 is still over the ~700 ceiling, the file was still over it before `c2` (734), and the named split seam is still the right one.

## P3 - Nit / polish

### 7. The profile-build call-site comment reads as "Synopsis is not repaired"

[BookIntelligenceService.cs:578-579](Pagedraft.Api/Services/Analysis/BookIntelligenceService.cs#L578): "BookOverview.Summary is not persisted here and Synopsis was not in the f5 wired set, so only CharactersJson + StoryStructureJson are repaired." The sentence is literally true about *this hook* and false about the build, since `profile.Synopsis` at [:571](Pagedraft.Api/Services/Analysis/BookIntelligenceService.cs#L571) is now repaired by `RunRawAsync`'s own layer. Sitting directly above the two `RepairStructuredProfileJsonAsync` calls, it reads as a statement about the build.

### 8. Seven stale references to the renamed `RepairStructuredProfileJson`

`c2` renamed it to `RepairStructuredProfileJsonAsync`. Current-tense references to the old name survive at [AiOptions.cs:394](Pagedraft.Api/Services/Ai/AiOptions.cs#L394), [BookReviewService.cs:2228](Pagedraft.Api/Services/Analysis/BookReviewService.cs#L2228), [UnifiedAnalysisService.cs:2925](Pagedraft.Api/Services/Analysis/UnifiedAnalysisService.cs#L2925), [BookIntelligenceRepairGateLoggingTests.cs:18](Pagedraft.Api.Tests/BookIntelligenceRepairGateLoggingTests.cs#L18) and [:46](Pagedraft.Api.Tests/BookIntelligenceRepairGateLoggingTests.cs#L46), and [ANALYSIS_OUTPUT_REPAIR.md:288](docs/ANALYSIS_OUTPUT_REPAIR.md#L288). ([:408](docs/ANALYSIS_OUTPUT_REPAIR.md#L408) is deliberately historical and correct as written.) The gate-logging test file is the sharpest one: its class doc was rewritten by this very change to explain the rename, and two of its own sentences still use the old name.

### 9. `IsParagraphInitial` is private to `ForeignRunClassifier` while its three siblings live in the shared `ScriptTokenPredicates`

`IsSentenceInitial`, `IsTransparentOpen` and `IsSentenceTerminator` were deliberately extracted into [ScriptTokenPredicates.cs:116-143](Pagedraft.Api/Services/Analysis/ScriptTokenPredicates.cs#L116) (commit `b542f46`, "Extract shared script-token predicates"). The new [IsParagraphInitial](Pagedraft.Api/Services/Analysis/ForeignRunClassifier.cs#L631) is its near-twin, is documented entirely by contrast with `IsSentenceInitial`, and lands in the caller instead. The two now differ by one branch across two files, which is the drift shape the extraction existed to prevent.

### 10. `RunProfileBuildAsync` takes a parameter it immediately discards

[OutputQualityDiagnostic.cs:2166-2172](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L2166) declares `string appSettingsPath` and opens with `_ = appSettingsPath;`. Both call sites pass it. Either the graph should read routing from it (it does not; it configures `AiOptions` inline) or the parameter should go.

### 11. The `SynopsisBook_HarvestsEveryLatinNameTheSynopsisFixtureDependsOn` message no longer matches its loop

[BookEntityFixtureSeedTests.cs:360-366](Pagedraft.Api.Tests/BookEntityFixtureSeedTests.cs#L360) now says "The q1 synopsis **case** that can ONLY be spared by the entity lever (the VALUE-initial name ...)", singular, while the loop iterates every name in `SynopsisBookLatinNames`. The assertion is still right (all of them must be harvested to keep the attribution honest); the failure message now under-describes why for every name except `Odessa`.

### 12. Scope (i) has a third honest limit that the docs do not list beside the two they do

`MeasureProfilePathTermRepair_Local` ([OutputQualityDiagnostic.cs:1745](Pagedraft.Api.Tests/LanguageEngine/OutputQualityDiagnostic.cs#L1745)) has no CLOUD arm, unlike its three `_LocalVsCloud` siblings. The method name is honest and the c2 table in [ANALYSIS_OUTPUT_REPAIR.md:439-447](docs/ANALYSIS_OUTPUT_REPAIR.md#L439) does not claim cloud reproduction, but that section explicitly enumerates "two honest limits of scope (i)" and this is a third, in a document whose §4.2 elsewhere states cloud reproduction for every other scope.

## Cross-stack contract check

No FE changes are pending and no endpoint shape changed, so the Phase 5 table is narrow. The contract surfaces this feature does touch:

| Aspect | Backend | Consumer | OK? |
|---|---|---|---|
| `Ai:AnalysisRepair:PerType` base vs Production | 11 keys, `Custom:false` + 10 `true` | `appsettings.Production.json` fully overrides base | OK, verified identical by `PerType_BaseAndProduction_AreEqual` and re-checked by hand |
| `_comment_PerTypeExclusions` byte-identity claim | asserts the two files are byte-identical | the sibling appsettings file | OK, verified byte-identical (4546 chars); this closes the parent review's P3 #5 |
| `PerType` allowlist vs dispatch arms | `Synopsis:true` in both files | `GlossaryRepairPass.Apply`, `DynamicTermRepairService.ApplyAsync` | OK |
| `PerType` allowlist vs the THIRD dispatch switch | `Synopsis:true` opens the gate | `AnalysisRepairService.RepairAnalysisAsync` | Warning, see P1 #1 |
| `BookProfile.charactersJson` FE parseability | reserialized without fence whenever the layer gate is open, including `Mode=Off` | FE `JSON.parse(profile.charactersJson)` | OK, deliberately unconditional and pinned by `BuildBookProfileAsync_ModeOff_AppliesNeitherStage` |
| Enum-completeness oracle vs shipped set | `DecisionFor` covers all 12 members | `Enum.GetValues<AnalysisType>()` | OK |
| Eval harness shipped-set membership | positional / `BookKey` split, hardcoded | shipped `PerType` map | Warning, see P1 #2 |

## Plan deviations / outstanding manual work

- **`f2` shipped real behaviour change against its own prompt.** The plan's `f2` prompt says "Docs/config/guard-messages only; no new behaviour", written on the expectation that `q2` would HALT again. Because `q2` passed, `f2` shipped two dispatch arms and two config flips. The plan flags this itself in `## f2 outcome` item 2 rather than exceeding it silently, which is the right call, but it belongs in the PR description.
- **`c2`'s reported line counts were wrong and `f2` corrected them.** Recorded in `## f2 outcome` item 1. The correction is itself off by 26 (P2 #6).
- **`docs/ANALYSIS_OUTPUT_REPAIR.md` had two `### 4.1` headings**; the per-environment one was renumbered to `4.3`. Every existing "section 4.1" reference was checked and means the decision table.
- **File-size waiver taken, not a split.** `BookIntelligenceService.cs` is 857 lines against CLAUDE.md's ~700 soft ceiling, waived in the file's own class doc with a named split seam (summarization / profile-build / Q&A). This is the convention this repo uses and the waiver is discoverable, so it is a legitimate deviation rather than a finding.
- **Deferred by the plan, still deferred:** enabling `Custom`, a script-ratio gate for `Custom`, changing `Ai:FeatureModels:TermRepair`, raising `OLLAMA_MAX_LOADED_MODELS`.
- **The plan explicitly forbids committing or pushing.** The tree still carries the parent plan's committed work plus this plan's uncommitted work; `.cursor/` is untracked in this repo and must not be swept into a commit.
- **No live GPU re-run performed by this review.** `q2`'s figures are quoted from the plan's recorded artifacts, not re-derived. The deterministic suite was re-run and matches.
- **Phase 4f (browser render gate): not applicable.** `pagedraft-client` has zero pending changes.

## Recommended fix order

**Must-fix (P0):** none.

**Should-fix (P1):**
1. Decide and record the `AnalysisRepairService` third-switch position for `Synopsis`, and extend the dispatch oracle to model all three switches so the next enable cannot repeat it.
2. Re-scope the d5/d6 shipped corpus and verdict to the actual shipped set, derived from the config rather than hardcoded, and fix the emitted prose.

**Worth scheduling (P2):**
3. `RunRawAsync` seam comment.
4. `c2` "Synopsis stays excluded" doc comment.
5. Rule (7b) line-break vs paragraph scope: narrow the predicate or restate and pin the widened shape.
6. Waiver line count.

**Nit (P3):** 7 through 12.
