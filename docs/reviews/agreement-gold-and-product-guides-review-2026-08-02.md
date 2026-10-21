# Code Review Report — Character-agreement gold class + Product guides

## Scope reviewed

- **Plans:**
  - `src/.cursor/plans/_todo/character-agreement-gold-2026-08-01.plan.md` (4/4 todos completed)
  - `src/.cursor/plans/_todo/product-guides-2026-08-01.plan.md` (4/4 todos completed)
- **Pending files:** 4 modified + 17 untracked, all in `Pagedraft.Api-repo` (`api-tier-ux-rework`). `pagedraft-client` is clean.
  - 624 tracked insertions, ~554 untracked lines of C#/MD, 15 guide files.
- **Feature purpose:** (a) add a 23-entry Hebrew character-agreement class to the proofread gold, extend the harness with per-case `[CHARACTER_REGISTER]` injection, and measure an n=5 baseline; (b) author 15 bilingual product guides and ship them as API runtime content.

**Verified green before review:** `dotnet build` clean; deterministic suite `--filter "FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine"` = **1433 passed / 0 failed / 1 skipped, 5s**. `dotnet publish -c Release` places all 15 guides at `Content/guides/` in the publish output (RULE 0 for the packaging claim: re-produced and inspected, not inferred).

---

## P0 — Bugs that ship broken behavior

### 1. The model bake-off now ranks models over a corpus where 20% of cases ride a different prompt surface, and nothing in its output says so

[ProofreadQualityTests.cs:226-250](Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs#L226-L250), [ProofreadQualityTests.cs:316-322](Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs#L316-L322), [ProofreadQualityTests.cs:159-180](Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs#L159-L180)

`ProofreadQuality_ModelBakeoff_ReportTable` loads the full gold and calls `ApplyCaseSubset`, which is a no-op unless `PROOFREAD_BAKEOFF_CASE_IDS` / `_MAX_CASES` is set. By default it therefore scores **all 116 cases** for every candidate model, emits a per-model precision/recall/fp/overreach/F0.5 table, and prints a **"Winner hint"**. `ScoreModelAsync` routes every case through the new `BuildGoldRequest`, so 23 of those 116 (all `agree-*`) are sent as `[CHARACTER_REGISTER] + ProofreadHe (long) + short pipeline`, while the other 93 are sent as the short pipeline instruction alone.

This is a model-ship decision instrument. Per `appsettings.json:67`, the production Proofread model was chosen off it twice (the 2026-07-29 n=5 run that reversed the DictaLM-3.0 pick on F0.5 24/25 pairwise). A model whose behavior differs between a short bare instruction and a long structured one is now systematically mis-ranked, and the effect is invisible: the harness's `Gold composition:` line reports no-change / overreach-guarded / with-corrections counts but **not the prompt-surface split**.

Two consequences, both of which land on a future run rather than this one:

- **The figures of record are no longer reproducible from the headline.** `appsettings.json:67` pins its verdict to "all 93 gold cases". The next bake-off prints a 116-case aggregate that is not comparable to it, with no line saying why.
- **The corpus was widened past the decision it emits.** The agreement class exists to measure register-driven agreement; it now also silently re-scopes the general model-quality verdict.

The design intent is documented and correct everywhere *except* the aggregate: `HebrewRegressionCase.cs:35-54`, `BuildGoldRequest`'s docstring, `TestData/README.md` §prompt-surface split and the plan's `## g1 baseline` all state loudly that the two subsets are not comparable. g1 itself only produced its per-subset numbers by way of PowerShell scripts in a machine-local temp directory. The instrument that an operator actually reads emits one blended number.

The membership predicate must be derived, not encoded: a case is on the production surface exactly when `CharacterRegister is { Length: > 0 }` — the same condition `BuildGoldRequest` branches on. One shared helper, called by the composition line, the aggregate and any subsetting, so the split cannot drift from the request builder.

---

## P1 — Code-quality issues

### 2. The near-miss guard's own non-vacuity test is a tautology

[ProofreadAgreementGoldTests.cs:375-403](Pagedraft.Api.Tests/ProofreadAgreementGoldTests.cs#L375-L403)

`ForbiddenNearMissEntries_DoTripOnAWrongFormEditAtTheRightSpan` calls:

```
ForbiddenMatch(f.Original, f.Suggested, f.Original, f.Suggested)
```

`ForbiddenMatch(a, b, a, b)` is `true` by construction for any non-empty `a`, `b`: the origin test passes on `origP == origF`, and the non-empty-`sugF` branch passes on `sugP == sugF`. The test pre-filters to non-empty `Suggested`, so the only input that could fail it is an empty `Original`. It never touches the case's actual erroneous span (`e.Original`) or the input text.

Its docstring claims it "simulates the exact failure the near-miss design targets — the model edits the RIGHT span but writes the wrong form — and asserts the shipped `ForbiddenMatch` DOES trip on it". It asserts the reflexivity of `ForbiddenMatch` and nothing else. This is the one test standing behind the whole near-miss design, and it is the test that certifies its sibling `..._NeverMatchTheCorrectFix` is not vacuous.

The real simulation is `ForbiddenMatch(e.Original, f.Suggested, f.Original, f.Suggested)` — the produced correction a near-missing model emits. I ran it against the current data: it trips for 15 of 17 expected corrections (the two misses are finding 3 below), so the guard genuinely works. The test just does not prove it.

### 3. `agree-register-03` has 3 expected fixes and 1 near-miss guard; the well-formedness test cannot see the gap

[ProofreadAgreementGoldTests.cs:118-126](Pagedraft.Api.Tests/ProofreadAgreementGoldTests.cs#L118-L126), [proofread-gold.json](Pagedraft.Api.Tests/TestData/proofread-gold.json) (`agree-register-03`)

`TestData/README.md:97-100` states the rule as "**Every RECALL entry must pair its expected fix with a NON-EMPTY `forbiddenCorrections` entry naming the plausible wrong form** at the same span". `AgreementRecallEntries_AreWellFormed_AndCarryARegister` enforces it per **case** (`wrongForm.Length > 0`), not per **expected correction**.

`agree-register-03` (`רוני קם לבדו והתקדם לכיוון היציאה.`) declares three expected corrections — `קם`→`קמה`, `לבדו`→`לבדה`, `התקדם`→`התקדמה` — and one forbidden, on `התקדם` only. Its own `_note` records this ("Near-miss forbidden on one span only") without treating it as a gap. So a model that edits `קם` or `לבדו` at the right span with the wrong form is credited as a recall **hit**.

This lands where it costs most. That single entry is, in its own words, "the heaviest single contributor to the register-only recall denominator": 3 of the bucket's 10 expected corrections. **2 of the 10 (20%) of the register-only recall denominator has no near-miss guard** — and register-only recall (mean 94%) is the headline number the whole plan produced and the number Wave 2's no-regression floor is set from.

### 4. `ForbiddenMatch`'s SPAN DISTINCTIVENESS INVARIANT was falsified in place and not re-verified

[ProofreadQualityTests.cs:752-766](Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs#L752-L766)

The remarks block states the substring-tolerant matching "is correct for today's two tiny single-token forbidden cases (`overreach-ms-01` רגשית, `overreach-ms-02` עתון→עתונות), but it is a latent risk", and imposes a requirement: "forbidden-correction spans MUST be distinctive enough that no legitimate correction at another span is a substring of (or contains) them."

The diff takes the forbidden population from 4 cases to **27** (measured), with spans as short as 3 Hebrew characters (`קרא`, `ענה`, `אמר`, `קמה`). The comment was not updated and the invariant was not verified for the new entries. The count in it was already wrong before this change (it says two; there were four), so the diff compounds a stale claim rather than introducing one — but it is the claim a future author reads before adding a forbidden entry.

Measured evidence that the new data already leans on substring tolerance rather than distinctiveness: `agree-register-03`'s forbidden original `התקדם` does not appear as a standalone token in its own input (the input has `והתקדם`). It matches only because `origP.Contains(origF)`.

The new deterministic guard covers a narrower thing than the invariant states: it checks (expected × forbidden) pairs **within one case**, while the invariant is about "a legitimate correction at another span", which includes corrections the model produces that are not in `expectedCorrections` (the punctuation and gershayim edits g1 measured on almost every dialogue case).

---

## P2 — Design / structural concerns

### 5. Forbidden originals on RECALL entries are never asserted to exist in the input

[ProofreadAgreementGoldTests.cs:99-127](Pagedraft.Api.Tests/ProofreadAgreementGoldTests.cs#L99-L127) vs [ProofreadAgreementGoldTests.cs:141](Pagedraft.Api.Tests/ProofreadAgreementGoldTests.cs#L141)

Preservation entries assert `Assert.All(c.ForbiddenCorrections!, f => Assert.Contains(f.Original, c.Input))`. Recall entries assert the same only for `ExpectedCorrections`, never for `ForbiddenCorrections`. A forbidden whose original does not correspond to a real span measures nothing and fails silently; `agree-register-03` is one substring-tolerance step away from being that case already (finding 4).

### 6. The csproj comment asserts consumers that do not exist

[Pagedraft.Api.csproj:14-17](Pagedraft.Api/Pagedraft.Api.csproj#L14-L17)

> "Product guides are runtime content, not documentation: the guides endpoint and the chatbot retrieval corpus read these files from disk, so they must reach the deployed app."

Written in the present tense as the justification for the glob. A grep for any consumer of `Content/guides` across the API returns zero hits, there is no guides endpoint, and there is no `UseStaticFiles` in the app at all. The glob itself is right and the relocation decision is sound (the guides needed version control and a path to production, which `src/docs/` could not give them). The justification should say what it is: the corpus is placed where those future consumers will read it.

### 7. The bake-off's default shortlist does not contain the shipped model, under a comment that misnames it

[ProofreadQualityTests.cs:118-128](Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs#L118-L128)

`DefaultBakeoffModels` is `{qwen3.5:9b, qwen3.5:4b, DictaLM-3.0-Nemotron-12B, qwen2.5:14b}` under a comment stating DictaLM-3.0-12B "is the production default (`Ai:FeatureModels:Proofread` in appsettings)". That has been false since 2026-07-29: the shipped model is `gemma4:12b`, pinned three lines further down at `ProofreadQualityTests.cs:91` and bound by `HarnessConfigParityTests`. A default bake-off run therefore never scores the incumbent, so no row in the table is the baseline the others should be read against.

Pre-existing rather than introduced, but it sits in the file this diff modifies, in the same instrument P0-1 widened, and it compounds that finding: the table is now both mis-scoped and missing its reference row.

---

## P3 — Nit / polish

### 8. Stale count in the cost-control comment

[ProofreadQualityTests.cs:105](Pagedraft.Api.Tests/LanguageEngine/ProofreadQualityTests.cs#L105) — "instead of all 90 cases". The file holds 116.

### 9. The baseline's derivation is not reproducible from the repository

The per-subset numbers in `## g1 baseline` were produced by `parse-run.ps1` / `aggregate.ps1` living only in `%TEMP%\pagedraft-g1-agreement-baseline\`. The harness emits no per-subset breakdown, so re-deriving the headline requires scripts that are not committed. Fixing P0-1 (per-surface reporting in the harness itself) mostly closes this.

### 10. Hebrew wording is unreviewed in both features

Carried forward from both plans, deferred to the user, not a code fix:
- Gold entries `agree-register-07`, `agree-preserve-03`, `agree-preserve-04`, `agree-preserve-08` were authored rather than lifted from the manuscript.
- All 7 `.he.md` guides are native-phrased but unreviewed, and the UI strings they align with are themselves marked `DRAFT he - needs native-speaker review`.

---

## Cross-stack contract check

No FE change in this diff (`pagedraft-client` is clean) and no controller, DTO or endpoint touched. The contract table is not applicable to this feature pair. The one cross-boundary artifact is the csproj content glob, verified by re-producing the artifact:

| Aspect | Backend | Consumer | OK? |
|---|---|---|---|
| `Content\guides\**\*.md` glob | `Pagedraft.Api.csproj:16` | Release publish output | ✓ 15/15 files present |
| Guides read at runtime | no reader exists | — | ⚠ see P2-6 (comment asserts one) |
| `ProofreadModel` const ↔ `Ai:FeatureModels:Proofread` | `ProofreadQualityTests.cs:91` = `gemma4:12b` | `appsettings.json:67` | ✓ bound by `HarnessConfigParityTests` |
| Bake-off default shortlist ↔ shipped model | `ProofreadQualityTests.cs:122-128` | `appsettings.json` | ⚠ see P2-7 (incumbent absent) |
| Gold prefix table ↔ actual data | `TestData/README.md:33-44` | `proofread-gold.json` | ✓ all 12 counts verified, total 116 |
| Guide he/en structural parity | 7 file pairs | — | ✓ heading counts match in all 7 |

---

## What the review could not break

Worth recording, because several of these are where the failure would have been expensive:

- **The near-miss guard genuinely works.** Simulating the real failure (right span, forbidden form) trips `ForbiddenMatch` for 15 of 17 expected corrections. The two misses are finding 3, not a broken mechanism.
- **`NoPreExistingGoldCase_HasAcquiredARegister` and the byte-identical-prompt assertion do their job.** 93 pre-existing cases carry no register and compose to the legacy short instruction exactly. The OLD-subset numbers really are comparable to July's.
- **The register block is built by the production builder**, not a look-alike, so its byte format cannot drift; and the `ProofreadHe` body that explains the block travels with it, which retires the "the model was never told what the block is for" confound.
- **Every README count is correct** (12 prefixes, total 116) — measured against the JSON, not read.
- **The guides' export-availability caveat is accurate**: no client code references `export/book` or `export/chapter`.
- **Zero em-dashes or en-dashes in any guide**; frontmatter valid and consistent in all 15.

---

## Plan deviations / outstanding manual work

- **Both plans' headline conclusion inverted their own prior**, which is a finding to carry into the PR description rather than a defect: the agreement class is **saturated** (register-only recall 90-100%, both triggering live failures pass 5/5), so Wave 2 gets a no-regression floor, not a recall target, plus a mandate to extend the class into the chunked path before Issue 1 is attempted.
- **Guides relocated after the plan closed** from `src/docs/guides/` to `Pagedraft.Api/Content/guides/`. Correct call (`src/docs/` is outside both git repos), and the plan records it, but it is a post-hoc structural change the plan did not authorize up front.
- **Deliberately not driven by c2:** no analysis / summary / review / profile build was started, so the tier 409 path and the consent panel were verified from the DTO and the component rather than by clicking through.
- **English guide strings verified from label maps, not rendered** — every book on this machine is Hebrew.
- **Open product questions from c2** (user's call, not code): whether to document export at all while it has no control; the "Story Bible" name collision; hardcoded Hebrew profile labels shown to English-language books.

---

## Recommended fix order

**Must-fix (P0):** 1 — partition the harness aggregate and the bake-off by prompt surface, deriving membership from `CharacterRegister` presence via one shared helper.

**Should-fix (P1):** 2 (rewrite the tautological non-vacuity test to simulate the real near-miss) · 3 (assert one near-miss guard per expected correction, and fill `agree-register-03`'s two gaps) · 4 (re-verify and restate the span-distinctiveness invariant against the 27-case population).

**Worth scheduling (P2):** 5 (assert forbidden originals exist in the input on recall entries) · 6 (correct the csproj justification) · 7 (add the incumbent to the bake-off shortlist and fix its comment).

**Nit (P3):** 8 (stale count) · 9 (commit the derivation, largely absorbed by P0-1) · 10 (Hebrew review — user, not agent).
