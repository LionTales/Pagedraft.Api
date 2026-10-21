# Code Review Report - ARM A over-correction closure + real-prose precision surface

Reviewed: 2026-08-05, branch `api-referent-carry-forward` (off `origin/master` @ 831a7bc), all pending uncommitted work.

## Scope reviewed

- Plans: `referent-carry-forward-2026-08-04.plan.md` (completed, closed negative) and `proofread-overcorrection-arm-a-2026-08-05.plan.md` (completed, closed negative) - both in `PageDraft/src/.cursor/plans/_todo/`.
- Pending files: 12 staged/modified + 4 untracked, all in Pagedraft.Api-repo (~3,200 insertions). Client repo clean.
- Feature purpose: close the twice-re-opened ARM A over-correction lead with a real-prose measurement. Ships the real-prose precision surface (12 embedded twice-proofread manuscript passages + 4 seeded recall variants), the harness that drives it through the production chunked path, the live g1 driver, the measurement record (DO NOT SHIP, p=0.363), and ARM A re-landed behind a default-off `Ai:ProofreadPrompt:OverlapReferentLicence` switch with byte-identity pinned.

Both plans are fully completed; per the skill's edge case, this review verified the pending work matches their intent. It does, with one accepted deviation (below) and the findings here.

Verification performed: `dotnet build` clean; full deterministic suite under the GPU-safe filter (`FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine`): **1547 passed, 0 failed, 1 known skip, ~5s**. Docs (`PAGEDRAFT_ROADMAP.md`, `CHARACTER_CONTEXT_ADHERENCE_GAPS.md`) checked: both correctly carry the CLOSED verdict. Production `new PromptFactory(` grep: no direct construction outside DI. `appsettings.Production.json`: no ProofreadPrompt section - safe, absence binds to the class default (false).

## P0 - Bugs that ship broken behavior

None found. The production diff is small, default-off, byte-identity-pinned at two independent layers (composer-level `ProofreadPromptArmTests` + end-to-end `ProofreadStandingFloorRetiredInterventionTests`), and the anchored insert throws loudly on anchor drift instead of silently no-opping.

## P1 - Code-quality issues

1. **Stale open-lead claim at the two operator-facing config sites** ([appsettings.json:107](Pagedraft.Api/appsettings.json#L107), [ProofreadPromptOptions.cs:30-40](Pagedraft.Api/Services/Ai/ProofreadPromptOptions.cs#L30-L40))
   Both sites still describe the precision lead as AWAITING measurement: appsettings says "It stays OFF until that precision lead is confirmed on real already-proofread prose WITH a recall floor...", and the options doc says "That is a LEAD, not a result, and it stays default-off until it is measured on real, already-proofread prose..." plus "RE-LANDED (default OFF) so its measured side effect can be tested on real prose." The measurement HAPPENED on 2026-08-05 and CLOSED NEGATIVE (5/12 passages, p=0.363, DO NOT SHIP). Every other surface of this shipment - `RetiredInterventions.MeasuredSideEffect`, `RealProseArmMeasurement.Verdict`, both workspace docs, and the tests pinning them - says CLOSED / do not re-open a third time. The two sites an operator actually reads when deciding to flip the switch are the two that still read as an open invitation to "confirm the lead" - which is precisely the re-derivation path this record exists to shut (the lead was re-opened twice from exactly this open-sounding phrasing). These comments were authored by c2 (pre-measurement) and the c3 closure sweep did not reach them - the category-vocabulary sweep failure mode the workspace conventions name.
   Fix: rewrite both to state the closure (measured 2026-08-05 on the real-prose surface, did not reproduce, verdict DO NOT SHIP; the arm is kept composable in-process only so a future ask costs a measurement, not a re-implementation), and pin the closure wording in the shipped comment via the existing `TheShippedConfiguration_BindsTheSection_AndLeavesEveryArmOff` test so the config surface cannot drift open again.

## P2 - Design / structural concerns

1. **Character-hygiene scan is narrower than the claim it asserts** ([RealProsePrecisionFixtureTests.cs:291-339](Pagedraft.Api.Tests/RealProsePrecisionFixtureTests.cs#L291-L339), claim at [RealProsePassages.cs:43-50](Pagedraft.Api.Tests/RealProsePassages.cs#L43-L50))
   The passages header claims "no niqqud, no bidi control characters ... each one is also asserted, not just applied," but `NoPassage_CarriesACharacterClassThatWouldFakeOrVoidAnEdit` scans exactly TWO of the ~15 niqqud marks (sheva U+05B0, dagesh U+05BC) and four format controls (LRM, RLM, ZWSP, ZWNBSP - not RLE/LRE/PDF/RLI/ALM). A future passage edit carrying a qamats, a holam, or an RLE would pass the guard while violating the stated hygiene, and the guard exists precisely to police future corpus edits. This is the invariant-claim class: the comment asserts coverage the code does not have.
   Fix: scan by range/category - any char in U+0591-U+05C7 (Hebrew points + cantillation) and any `UnicodeCategory.Format` char - keeping the named list for message quality; extend the non-vacuity probe with a qamats and an RLE and revert-verify.

## P3 - Nit / polish

1. **`IsHebrewLetter` includes Hebrew punctuation and points** ([RealProseArmMeasurementTests.cs:41](Pagedraft.Api.Tests/RealProseArmMeasurementTests.cs#L41))
   The range U+0590-U+05FF includes maqaf (U+05BE), geresh, gershayim and all niqqud, so the carrier-absence counter excludes a match preceded by a maqaf (a word JOINER-adjacent occurrence a reader would treat as word-initial). On the frozen corpus this changes nothing (the non-vacuity controls pass), but the test's purpose is to catch FUTURE corpora, and a future passage with a maqaf-preceded carrier would quietly inherit the closure. Narrow to actual letters (U+05D0-U+05EA, which includes the final forms).
2. **File-size soft ceiling** ([RealProsePrecisionLiveTests.cs](Pagedraft.Api.Tests/LanguageEngine/RealProsePrecisionLiveTests.cs) - 738 lines vs the ~700 soft ceiling)
   The file is a cohesive live driver whose bulk is load-bearing documentation of the tripwire semantics; splitting it would hurt more than it helps. Record an explicit waiver in the file header rather than splitting.

## Cross-stack contract check

Backend-only shipment; no endpoint, DTO, or client change. The applicable checks:

| Aspect | Status | OK? |
|---|---|---|
| New config section `Ai:ProofreadPrompt` bound in Program.cs, present in appsettings.json, bound-value pinned false by test | verified | OK |
| appsettings.Production.json omits the section | absence binds class default (false) - safe by construction | OK |
| DI ctor selection (two PromptFactory ctors) | DI picks the IOptions ctor; parameterless kept solely as the byte-identity baseline; no production `new PromptFactory()` | OK |
| Advertised chunk thresholds (250 he / 500 en) | asserted against `ProofreadChunkTargetWordsFor` in fixture tests; endpoint untouched | OK |
| Live-model test hygiene (4c item 19) | `[Trait("Category","LiveModel")]` + LanguageEngine namespace + ARMA_ARTIFACT_DIR opt-in BEFORE the Ollama probe; reflection guard covers it | OK |
| Config comment ↔ measured state | **stale at 2 sites** | ⚠ (P1-1) |

## Plan deviations / outstanding manual work

- **ARM A kept in production code despite c3's "IF IT DID NOT WIN: change no production code"** - deliberate and argued in [ProofreadStandingFloor.RetiredInterventions.cs:28-37](Pagedraft.Api.Tests/ProofreadStandingFloor.RetiredInterventions.cs#L28-L37): a second revert would make a third RE-IMPLEMENTATION (not a third measurement) the price of ever asking again, and the OFF path is byte-identity-pinned so production behaviour is unchanged. Accepted; should be named in the PR description.
- The `phenomenon` classifier was audited degenerate (emits only "agreement-repair"/"other") and deliberately NOT fixed - the new surface routes around it with deterministic shape buckets. Out of scope by plan design.
- `UnownedResidue` (gemma4:12b emits Hebrew non-words on clean prose in both arms) is recorded as an open lead with no decision rule - explicitly not this shipment's result; a future plan owns it.
- No live/GPU re-run performed by this review; the live surface is measurement infrastructure whose deterministic gates are the shipped acceptance, and the g1 session's artifacts were already re-derived by c3's record tests.

## Recommended fix order

**Must-fix (P0):** none.
**Should-fix (P1):** P1-1 - close the two stale config-surface claims + pin the closure wording.
**Worth scheduling (P2):** P2-1 - widen the character-hygiene scan to match its claim.
**Nit (P3):** P3-1 - narrow `IsHebrewLetter`; P3-2 - record the file-size waiver.
