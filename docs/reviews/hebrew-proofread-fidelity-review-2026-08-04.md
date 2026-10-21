# Code Review Report — Hebrew proofread fidelity (chunked agreement attribution + punctuation tax)

## Scope reviewed

- **Plans:** [hebrew-proofread-fidelity-2026-08-02.plan.md](../../../.cursor/plans/_todo/hebrew-proofread-fidelity-2026-08-02.plan.md) (all 5 todos `completed`). Successor plan [referent-carry-forward-2026-08-04.plan.md](../../../.cursor/plans/_todo/referent-carry-forward-2026-08-04.plan.md) read for downstream-consumer context only; it is not in scope.
- **Pending files:** 3 modified + 9 untracked, all in `Pagedraft.Api-repo/Pagedraft.Api.Tests/`. ~4,275 new lines, 226 changed. `pagedraft-client` is clean.
- **No production file is modified.** Verified: `git status` lists nothing under `Pagedraft.Api/`.
- **Feature purpose:** reproduce the chunked-regime Hebrew gender-agreement failure on the real chunked path and *attribute* it (c1 instrument, g1 measurement), split and rule on the punctuation over-correction tax (c2), and turn both results into a standing no-regression floor (c3).
- **Deterministic suite re-run during this review:** `dotnet test --filter "FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine"` -> **Failed: 0, Passed: 1500, Skipped: 1, Total: 1501 (5 s)**. Matches the plan's claim exactly.

**Overall:** this is unusually disciplined work. The attribution is carried by data rather than argument, the honesty requirements were paid in full (the single-chunk control, the n>=15 rule, the rejected arm B, the "no fix to ship" verdict on c2), and the vacuity guards are real. Every finding below is in the *instrument*, not in the conclusions it produced. None of them invalidates g1's attribution, because the failure modes they describe did not occur in that session (0 transport failures, 0 whitespace-only, all four fixtures at zero spread). They matter because this instrument is now a standing gate that the referent-carry-forward plan will drive next.

## P0 — Bugs that ship broken behavior

**None.** The diff is test-only; no product code path changes.

## P1 — Correctness issues in the instrument and the gate

### 1. A per-chunk transport failure silently misaligns the published audit trail

[ChunkedAgreementHarness.cs:336](../../Pagedraft.Api.Tests/ChunkedAgreementHarness.cs#L336), [ChunkedAgreementLiveTests.cs:271](../../Pagedraft.Api.Tests/LanguageEngine/ChunkedAgreementLiveTests.cs#L271), [ChunkedAgreementLiveTests.cs:431](../../Pagedraft.Api.Tests/LanguageEngine/ChunkedAgreementLiveTests.cs#L431)

`RecordingChunkRouter` appends to `_calls` only *after* a successful await, and production catches every per-chunk throw and carries on ([UnifiedAnalysisService.cs:2290](../../Pagedraft.Api/Services/Analysis/UnifiedAnalysisService.cs#L2290)). So one failed chunk leaves `Calls` shorter than `Chunks`, and every consumer that correlates them **positionally** attributes each capture to the wrong chunk.

Measured directly during this review, with a router throwing on the first chunk only, against fixture 01:

```
chunks=3 calls=2 failures=1
  failure CallIndex=0
  Calls[0].CallIndex=0 positionallyMatchesChunk0=False
  Calls[1].CallIndex=1 positionallyMatchesChunk1=False
```

Three distinct consequences:
- `ChunkedAgreementRun.ErrorChunkCall` indexes `Calls[ExpectedErrorChunkIndex]` and **threw `ArgumentOutOfRangeException`** (it is read by `ChunkedAgreementHarnessTests`).
- `Score`'s per-chunk loop uses `i < run.Calls.Count ? run.Calls[i] : null`, so it does **not** throw: it writes chunk 1's register / overlap / response columns into the JSONL row labelled `chunkIndex: 0`, and reports the real chunk 2 as `called: false`.
- `WritePromptArtifacts` writes `…chunk0.txt` containing chunk 1's composed prompt.

The transport tripwire in `ReportAndGateTheStandingFloor` voids the *agreement verdict*, which is right, but it runs after the artifacts are already on disk — and the artifacts are the deliverable the todo demanded ("published so the attribution can be audited rather than trusted"). A voided run therefore leaves a mis-labelled audit trail that reads as authoritative. `ChunkCallFailure.CallIndex` shares the successful-call counter (`0` for both the failure and the first surviving call), so the correlation cannot be reconstructed after the fact either.

This did not affect g1: `Failures` was empty in all 60 fixture-runs. It matters because the 2026-08-03 concurrency defect this column exists to catch is exactly the condition that triggers it.

### 2. The scalar half of the standing floor has no measuring consumer

[ProofreadStandingFloor.cs:323](../../Pagedraft.Api.Tests/ProofreadStandingFloor.cs#L323), [ProofreadStandingFloor.cs:529](../../Pagedraft.Api.Tests/ProofreadStandingFloor.cs#L529)

`Metrics`, `EvaluateMetric`, `PunctuationPhenomena`, `PhenomenonEditsPerRun` and `OffendingGoldCaseIds` are referenced **only** by `ProofreadStandingFloorTests` — their own self-tests. Grepped across the whole test project; there is no other caller.

Specifically:
- `ChunkedAgreementLiveTests` consumes only `ForFixture` / `Evaluate`, and **hand-rolls** the two chunked tripwires as `tally.WhitespaceOnly > 0` / `tally.TransportFailures > 0` rather than routing them through `EvaluateMetric` against `chunked.whitespaceOnlySuggestions` / `chunked.transportFailures`. Two copies of one gate, free to drift.
- The eight gold-surface bars (`agree-name.*`, `agree-register.recall`, `agree-preserve.*`, `legacy93.recall`) are measured by `ProofreadQualityTests.ProofreadQuality_RunGoldCases_…`, which does not reference `ProofreadStandingFloor` at all. Nothing compares a gold run to its floor.

The file header states the floor is "THE NO-REGRESSION BAR Wave 2 changes are measured against" and explains that the decision lives here "so the live test only supplies hits and runs". That is accurate for `Evaluate`; it is not true of the scalar half, which has no supplier. The successor plan's decision rule condition (4) — "no over-correction regression on the `agree-preserve` bars in `ProofreadStandingFloor.Metrics`" — currently has no code path that can evaluate it.

### 3. The one assertion claiming to catch a wrong bound direction is a tautology

[ProofreadStandingFloorTests.cs:761-764](../../Pagedraft.Api.Tests/ProofreadStandingFloorTests.cs#L761)

```csharp
// EVERY bar is exercised at its own measured value and must hold there, so a bar authored with
// the wrong bound direction (an AtLeast on a tax) fails here rather than at the next live run.
Assert.All(ProofreadStandingFloor.Metrics,
    m => Assert.Equal(MetricVerdict.Held, ProofreadStandingFloor.EvaluateMetric(m, m.Value)));
```

`EvaluateMetric` returns `Held` for `observed == Value` under **every** bound, so evaluating a bar at its own value cannot discriminate. Measured during this review by flipping each bar's bound to its opposite and re-evaluating at the same value:

```
agree-name.recall:                bound=AtLeast -> Held;  FLIPPED to AtMost -> Held
agree-preserve.overCorrectionRate: bound=AtMost  -> Held;  FLIPPED to AtLeast -> Held
agree-preserve.overreach:          bound=Exactly -> Held;  FLIPPED to AtMost  -> Held
… (all 10 bars identical)
```

The comment asserts an invariant the code cannot hold. This is the shape the closing-review gate is written to hunt: an assertion true by construction, reading as a real simulation in its own docstring. To actually bind, the check needs to probe each bar *off* its value in the bad direction and assert `Regressed` — which requires knowing the intended direction, i.e. declaring it per bar rather than deriving it.

## P2 — Design / structural concerns

### 4. `Split`'s orphan message is empty in exactly the scenario its docstring names

[GoldPromptSurfaces.cs:216-227](../../Pagedraft.Api.Tests/LanguageEngine/GoldPromptSurfaces.cs#L216)

`orphans` filters `!AllSurfaces.Contains(r.Surface)`, and `AllSurfaces` is `Enum.GetValues<GoldPromptSurface>()` — every declared value. Confirmed by probe: `AllSurfaces.Contains(x)` is `true` for all three, so the filter excludes them all.

The throw itself is correct in both scenarios (`bucketed != all.Count` fires either way), so nothing silently passes. But the two scenarios diverge on the message:
- a cast out-of-range value (what [ProofreadAgreementGoldTests.cs:1050](../../Pagedraft.Api.Tests/ProofreadAgreementGoldTests.cs#L1050) probes) -> offenders listed correctly;
- **a fourth enum member added without a bucket** — the case the docstring says this "fails the day" it happens — -> `Offenders: ` with nothing after it.

The offender set should be derived from what was *bucketed* (records not in `shortOnly ∪ production ∪ chunked`), not from what is *declared*.

### 5. `agree-preserve.agreementBearingOverCorrection` pre-fires `ImprovedUpdateTheFloor` forever

[ProofreadStandingFloor.cs:346-351](../../Pagedraft.Api.Tests/ProofreadStandingFloor.cs#L346)

The bar is `AtMost 1` while g1 measured `0`, deliberately taking "the WORSE of the two so the floor is not accidentally tightened onto a single flap". The consequence is that any evaluation at today's measured value returns `ImprovedUpdateTheFloor` — the verdict whose stated purpose is that "a genuine improvement gets recorded instead of being absorbed as slack". It is pre-fired on day one, so when a real improvement lands the signal is indistinguishable from the standing state. Either carry the deliberate slack as an explicit field (measured value + tolerated ceiling) or accept `Held` across a declared band.

### 6. Live tripwire tallies sum across repetitions but compare against a per-run bar

[ChunkedAgreementLiveTests.cs:161-163](../../Pagedraft.Api.Tests/LanguageEngine/ChunkedAgreementLiveTests.cs#L161), [ChunkedAgreementLiveTests.cs:211](../../Pagedraft.Api.Tests/LanguageEngine/ChunkedAgreementLiveTests.cs#L211)

`RunTally` accumulates `WhitespaceOnly` and `TransportFailures` over every rep, and the gate compares the running total against floors whose declared unit is "suggestions per run" / "failures per run". `OverCorrections` on the very next line is correctly divided by `tally.Runs`. The two coincide only because both bars are `0`; the units diverge the moment either becomes non-zero, or the moment a per-run threshold above zero is introduced.

### 7. A "no offenders" assertion that is true by construction

[ChunkedAgreementFixtureTests.cs:493-494](../../Pagedraft.Api.Tests/ChunkedAgreementFixtureTests.cs#L493)

```csharp
Assert.All(ChunkedAgreementFixtures.MultiChunk,
    f => Assert.Equal(GoldPromptSurface.ChunkedPerChunk, f.Surface));
```

`MultiChunk` is *defined* as `All.Where(f => f.Surface == GoldPromptSurface.ChunkedPerChunk)` ([ChunkedAgreementFixtures.cs:427](../../Pagedraft.Api.Tests/ChunkedAgreementFixtures.cs#L427)), so the assertion restates its own selector. In a file whose header states "Every 'nothing offends' assertion below first proves its population is non-empty", this is the one that proves nothing. The real content is already carried by `EveryChunkedFixture_DeclaresTheSurfaceTheRoutingRuleActuallyGivesIt`; the line should assert something the selector does not (e.g. `ExpectedChunkCount > 1` for every member) or go.

### 8. Routing-threshold assertions read the RAW text; production routes on the STRIPPED text

[ChunkedAgreementFixtureTests.cs:93](../../Pagedraft.Api.Tests/ChunkedAgreementFixtureTests.cs#L93), [:120](../../Pagedraft.Api.Tests/ChunkedAgreementFixtureTests.cs#L120), [:258](../../Pagedraft.Api.Tests/ChunkedAgreementFixtureTests.cs#L258)

These restate `RunAsync`'s branch condition as `WordCount(f.Text) > target`, but production evaluates it on `context.TargetText`, which every resolution path produces via the stripper — which is precisely why `ChunkedAgreementHarness.ProductionTargetText` was introduced by c2. `GoldPromptSurfaces.DerivedSurfaceOf` gets this right and uses `ProductionTargetText`; these three do not.

Equal today (the stripper only collapses whitespace runs, which `WordCount` already ignores), so this is a latent divergence rather than a live bug. It is the same mirror-path class c2 spent a todo closing, re-opened one file over, and the sibling that already does it correctly is the tell.

## P3 — Nit / polish

9. [ChunkedAgreementFixtures.cs:426](../../Pagedraft.Api.Tests/ChunkedAgreementFixtures.cs#L426) — `MultiChunk`'s summary reads "The three fixtures that must realize MORE THAN ONE chunk", but the selector keys on `Surface`, not on chunk count. The doc describes an invariant the code does not express (see finding 7).

10. [ChunkedAgreementFixtures.cs:293-296](../../Pagedraft.Api.Tests/ChunkedAgreementFixtures.cs#L293) — `Compose` throws `IndexOutOfRangeException` if `appended`/`prepended` is passed with the default `-1` index. Unreachable from the four call sites; a guard or a paired `(int index, string text)?` parameter removes the trap.

11. [ChunkedAgreementLiveTests.cs:136](../../Pagedraft.Api.Tests/LanguageEngine/ChunkedAgreementLiveTests.cs#L136) — `Assert.True(reps > 0 && fixtures.Count > 0)` is a tautology: `ParseInt` only returns a parsed value when `v > 0`, and `fixtures.Count > 0` was already asserted at :82.

12. [ChunkedAgreementLiveTests.cs:231](../../Pagedraft.Api.Tests/LanguageEngine/ChunkedAgreementLiveTests.cs#L231) — when every fixture is voided, `Assert.True(evaluated > 0, …)` fires before the `failures` assertion, so the operator sees "Either every run was fatal or the tally was never populated" instead of the per-fixture VOID messages the method already built and holds in `failures`.

## Cross-stack contract check

| Aspect | Backend | Frontend | OK? |
|---|---|---|---|
| Endpoints added / changed | none | none | ✓ |
| DTOs / response shapes | none | none | ✓ |
| Config keys | none | none | ✓ |
| Shared constants | `ProofreadStandingFloor.MeasuredOnModel` pinned against `ProofreadQualityTests.ProofreadModel` by a deterministic test | n/a | ✓ |
| Chunk-threshold contract (`/api/config/analysis-chunk-thresholds` ↔ `EffectiveChunkTargetWords`) | read-only; `ProofreadChunkTargetWordsFor` used via the production accessor, ceiling not lowered | unchanged | ✓ |
| Visibility widening | `GoldPromptSurface` internal -> public; `CreateRouter` / `IsOllamaReachableAsync` private -> internal | n/a | ✓ (test assembly only; rationale documented at each site) |

No ⚠ rows. This shipment does not cross the stack.

## Phase-lens results

- **4c (AI/LLM):** items 9 (harness fidelity), 14 (replicated gate), 16 (empty/degenerate classification) and 19 (live-test hygiene) all apply and are, on the whole, handled unusually well — the harness drives the real `RunAsync` rather than reflecting past the routing decision, the live class carries both the namespace and the `[Trait("Category","LiveModel")]` gate plus an artifact-dir refusal, and `LiveHarnessNamespaceGuardTests` enforces it reflectively. Item 14's "replicated gate" is the one that bit: findings 2 and 6.
- **4e (heuristic / mirror-path):** item 1 produced finding 8. Item 3's degenerate-branch interrogation produced finding 1 (the failure branch nobody walked).
- **4f (FE render gate):** **n/a** — no user-visible frontend change; `pagedraft-client` is clean.
- **4b / 4d / 4g / 4h:** not triggered.

## Plan deviations / outstanding manual work

- **c2 shipped no production fix, by design and correctly.** The todo said "re-measure with c2's fix in place"; c2 concluded there is nothing to fix at a pipeline layer and g1 restated the scope as a baseline re-measure plus an A/B. The deviation is stated explicitly in the plan and is the right call — carry it into the PR body.
- **g1's arm B was measured and reverted**, per the decision rule. `PromptFactory.cs` is confirmed clean in `git status`.
- **f1's deliverables are outside every git repo.** `src/docs/PAGEDRAFT_ROADMAP.md` and `src/docs/CHARACTER_CONTEXT_ADHERENCE_GAPS.md` were both updated today (15:58 / 15:59) but `src/docs/` sits in no repository, so the verdicts this shipment exists to record do not ship with the PR. Known workspace constraint, not a defect of this work — but it means the PR body is the only place a reviewer will see the attribution.
- **The Hebrew prose in the fixtures is authored, not native-reviewed.** `ChunkedAgreementFixtures.cs:78-80` records this honestly and puts it on the same pending review list as the authored gold notes. Still outstanding.
- **Arm B's two harm rows (`agree-register` recall, OLD recall) rest on one case at n=3** and are reported as suggestive rather than established. The rejection does not depend on them. No action needed; noted so it is not later quoted as measured.
- The two unscoped model corruptions (`כמעט`→`כמעת`, `המסדרון`→`המסדור`) are recorded as characterization with no owner. Correct handling; they need a roadmap line, not a todo here.

## Recommended fix order

**Must-fix (P1):** 1 (chunk↔call correlation), 3 (tautological bound assertion), 2 (wire the scalar floor to a consumer, or say plainly in the floor's own header that the scalar half is a recorded bar with no automated evaluator yet).

**Should-fix (P2):** 4 (orphan derivation), 7 (tautological surface assertion), 8 (mirror-path text), 6 (tripwire units), 5 (pre-fired improvement verdict).

**Nit (P3):** 9, 10, 11, 12.
