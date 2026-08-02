# TestData/README.md

Gold and regression fixtures for the LanguageEngine test suite. This file documents the two things
that are NOT self-evident from the JSON alone: (1) how gold cases are classified (there is no schema
`Category` field), and (2) how to add a new character-agreement case without re-deriving the design
from scratch.

## Files (proofread-related)

- `proofread-gold.json` - Hebrew proofread gold, consumed by `LanguageEngine/ProofreadQualityTests.cs`
  (live-model, skip-by-default) and by the deterministic `ProofreadAgreementGoldTests.cs` (no model).
- `proofread-gold-en.json` - English proofread gold, consumed by the same harness plus the
  deterministic `ProofreadEnglishGoldTests.cs`.
- `hebrew-regression.json` - the original regression fixture `HebrewRegressionCase` was named after;
  a separate, older harness (`HebrewRegressionTests`).
- `book-review-gold.json`, `book-review-gold-large.json`, `linguistic-gold.json`,
  `linguistic-long-probes.json`, `repair-gold.json` - gold sets for other analysis types, out of
  scope for this note.

## Classification: id-prefix convention (no schema `Category` field)

`HebrewRegressionCase` (`LanguageEngine/HebrewRegressionCase.cs`) has no `Category`/`Tag`/`Class`
property. Every gold file classifies its entries by an **id-prefix convention** only, and every test
that needs a subset (a bucket-presence smoke test, a bake-off subset, a baseline slice) filters on
`Id.StartsWith(...)`. This is the only registry that exists, so a new class of case is "registered"
by picking a new prefix and, ideally, adding one deterministic test that asserts the bucket is
present (see `ProofreadAgreementGoldTests.cs` and `ProofreadEnglishGoldTests.cs` for the pattern).

Prefixes in `proofread-gold.json` today:

| prefix | n | what it is |
|---|---|---|
| `norm-` | 6 | normalization-only cases (punctuation/whitespace, no grammar) |
| `detect-` | 2 | detection-focused cases |
| `rewrite-` | 1 | rewrite-shape case |
| `full-` | 1 | full-text case |
| `clean-ms-` | 65 | clean (no-change) cases sourced from the cleared eval manuscript |
| `clean-overreach-ms-` | 1 | clean manuscript text paired with a named forbidden overreach |
| `inj-ms-` | 12 | injected-error cases sourced from the manuscript |
| `overreach-ms-` | 4 | manuscript cases guarding a specific meaning-changing overreach |
| `longtext-clean-ms-` | 1 | longer clean manuscript passage |
| `agree-name-*` | 7 | character-agreement RECALL, gender evident from the character's name |
| `agree-register-*` | 8 | character-agreement RECALL, gender knowable only/directionally from the `[CHARACTER_REGISTER]` block (unisex names) |
| `agree-preserve-*` | 8 | character-agreement PRESERVATION (`shouldHaveNoChanges` + a named forbidden overreach) |

Prefixes in `proofread-gold-en.json` (asserted by `ProofreadEnglishGoldTests.EnglishGold_HasAllFourBuckets`):
`en-inj-`, `en-clean-`, `en-overreach-`, `en-dialect-`.

## How to add a character-agreement case

### Schema (fields this consumer actually reads)

`HebrewRegressionCase` fields read by `ProofreadQualityTests.ScoreModelAsync` /
`ProofreadAgreementGoldTests`:

- `Id`, `Input`, `Language` (default `"he-IL"`).
- `ExpectedCorrections[]` (`{Original, Suggested, Category?}`) - drives recall. Every entry a RECALL
  case must fix.
- `ShouldHaveNoChanges` - drives false-positive/over-correction. Set `true` on PRESERVATION cases;
  omit `ExpectedCorrections` when this is set.
- `ForbiddenCorrections[]` (`{Original, Suggested, Category?}`) - edits that must NOT appear. An empty
  `Suggested` forbids ANY edit at that span ("must not touch this span at all"); a non-empty
  `Suggested` forbids that specific wrong replacement while other edits at the span are still allowed.
- `CharacterRegister[]` (`{Name, Gender}`, `Gender` is the English literal `"male" | "female" |
  "unknown"` even for Hebrew text) - opt-in, per case. See "prompt-surface split" below.

**Dead for this consumer, do not bother filling in:** `ExpectedCorrectedText`, `ExpectedNormalized`,
`ExpectedIssueCategories`, `ExpectedRewriteSnippet`, `ExpectAtLeastOneIssue`. They exist on the shared
`HebrewRegressionCase` class (used by the older `hebrew-regression.json` harness) but
`ProofreadQualityTests.ScoreModelAsync` never reads them. `ExpectedCorrectedText` is sometimes filled
in anyway as documentation for a human reader, which is fine, but it drives nothing.

An entry may also carry a free-text `_note` field (see any `agree-*` entry). It is not part of the
schema and is ignored by the deserializer, it is there purely so the next reader does not have to
reconstruct why the case exists.

### The attribute-driven rule

Every entry encodes `attribute + language => obligation` (e.g. "gender=female + he-IL => feminine
past-tense agreement"), never a specific character instance. When a new live failure shows up:

- It joins the class as ONE member, varied like its siblings (grammatical category, position,
  attribute source, error direction), not as a bespoke entry shaped around the character's name.
- Do not add a case whose only point is "this exact sentence, with this exact character, must be
  fixed." That is an anecdote, not a class member. Ask what attribute the failure is really testing
  and whether an existing sub-bucket (`agree-name-*` / `agree-register-*` / `agree-preserve-*`) already
  covers that shape with a different character; if so, consider whether the class already has enough
  coverage before adding another entry.

### The near-miss trap (read this before writing a RECALL entry)

`CorrectionsMatch` (`ProofreadQualityTests.cs`) credits a recall hit on span alignment alone (right
erroneous span, ANY suggested replacement) as well as on exact match. Hebrew agreement errors are
single-letter edits, so a model that finds the right word but writes the WRONG form (e.g. present
tense instead of the correct feminine past) scores as a recall hit unless you guard against it.

**Every RECALL entry must pair its expected fix with a NON-EMPTY `forbiddenCorrections` entry naming
the plausible wrong form** at the same span (see `agree-name-01`: expected `קרא` -> `קראה`, forbidden
`קרא` -> `קורא`). This makes a right-span/wrong-form model output show up as an overreach instead of
silently passing as recall.

**Do NOT leave `Suggested` empty on a forbidden that targets the RECALL span.** An empty-`Suggested`
forbidden means "must not touch this span at all," and `ForbiddenCorrections` are pulled out of the
scoring pool BEFORE recall matching runs. On a span that also has a correct expected fix, an
empty-`Suggested` forbidden there will swallow the CORRECT fix too and force that entry to 0 recall +
1 overreach regardless of what the model does. (This exact mistake was made and caught while authoring
this class; `ProofreadAgreementGoldTests.ForbiddenNearMissEntries_NeverMatchTheCorrectFix` guards
against it happening again, and `..._DoTripOnAWrongFormEditAtTheRightSpan` proves the guard is not
vacuous.) Empty-`Suggested` forbiddens belong on PRESERVATION spans that must never be touched at all,
not on a RECALL span's near-miss guard.

### The forbidden-span rules (enforced, not advisory)

`ForbiddenMatch` aligns BOTH endpoints substring-tolerantly (equal, or one containing the other). That
is what lets a forbidden written as `עתון` catch a model that returns the whole orthographic token
`בעתון`. The price is that a badly chosen span can also fire on an edit the model made SOMEWHERE ELSE
in the same input, which on a recall entry pulls a legitimate correction out of the pool as overreach
before recall matching runs. So a forbidden `original` must:

1. **Occur in that case's own input.** An absent span is inert: nothing the model produces can trip it,
   so the entry guards nothing while looking like it does.
2. **Be a WORD at every occurrence, not just at one.** A Hebrew proclitic (ו/ה/ב/כ/ל/מ/ש) may precede
   it, so `עתון` for the input's `בעתון` and `התקדם` for `והתקדם` are both legitimate; nothing may
   follow it inside the word, because Hebrew builds the feminine by suffixation and a right-edge
   substring match is the `קם`-inside-`קמה` trap itself. "Every occurrence" is the operative part: a
   span that also sits inside a longer word elsewhere means that longer word CONTAINS it, and a
   legitimate correction of that word aligns. The proclitic exemption is a deliberate hole in that
   same reasoning, not an oversight: `בעתון` *is* a longer word containing `עתון`, so an edit to
   `בעתון` does align. It is allowed because catching a model that rewrites the whole
   clitic-carrying token is why the matcher is substring-tolerant at all. Keep it to a clitic: an
   arbitrary infix buys nothing and reopens the hole to any width.
3. **Not contain a word the input uses elsewhere, IF its `suggested` is empty.** An empty `suggested`
   forbids any edit at the span, so there is no second endpoint to lock on. With a non-empty
   `suggested` this direction is bounded by the replacement test instead, which is why the multi-word
   `מצאתי אותה` in `agree-preserve-04` may contain that input's standalone `את`.

A near-miss `suggested` (the non-empty shape) must additionally:

4. **Differ from its own `original`.** Otherwise it forbids a no-op and can only fire on a model that
   "corrects" a word to itself. This is why `agree-register-03`'s `קם` guard is the synonym `נעמד`
   rather than the present tense: the present-tense masculine of `קם` is `קם`.
5. **Differ from the expected fix at that span.** Otherwise the guard forbids the correct answer and
   the entry scores 0 recall no matter what the model does.
6. **Be written in the case's own script.** A Latin placeholder passes every other check in the class
   while measuring nothing.

Rules 4-6 are NECESSARY, not sufficient. Nothing can mechanically decide whether the wrong form you
wrote is one the model would plausibly emit, which is the entire point of a near miss. That judgement
is yours, and the Hebrew-authoring rule below applies to it.

All six are asserted by `ProofreadAgreementGoldTests` over EVERY forbidden entry in the file (not only
the `agree-*` ones) and run in the standing deterministic suite, so a violation is a red test rather
than a number that quietly drifts. The reasoning behind 1-3, and the one residual case they
deliberately do not cover, is in the `<remarks>` on `ProofreadQualityTests.ForbiddenMatch`.

### The prompt-surface split

A case with a non-empty `CharacterRegister` is measured on the PRODUCTION long+short prompt surface
(`PromptFactory.BuildProofreadChunkPrompt`, via `ProofreadQualityTests.BuildGoldRequest`): the
`[CHARACTER_REGISTER]` block, the `ProofreadHe`/`En` body that explains what the block means, then the
short pipeline instruction. A case with no `CharacterRegister` rides the short pipeline instruction
ALONE, unchanged since before the agreement class existed.

These two surfaces are **not comparable** and every number reported against this file must say which
surface it measured (see the plan's `## g1 baseline` for why). The harness does this for you: both
consumers score each case ONCE and then partition the per-case records through the shared helper
`GoldPromptSurfaces` (whose membership predicate is derived from `CharacterRegister`, the same
condition `BuildGoldRequest` branches on, never from the id prefix). The single-model Fact prints an
aggregate block per surface plus a mixed all-cases block labelled as not comparable, and the bake-off
names the surface split on its `Gold composition:` line and computes its Winner hint on the
short-only subset (the surface every prior Proofread model verdict was taken on), saying so in the
hint. Concretely:

- Adding a `CharacterRegister` to a PRE-EXISTING (non-`agree-*`) case silently moves it onto the
  other surface and invalidates its history. Don't do this without saying so loudly (and updating
  `ProofreadAgreementGoldTests.NoPreExistingGoldCase_HasAcquiredARegister`, which asserts against it).
- A new `agree-*` RECALL entry needs a `CharacterRegister`, both because the register is what makes
  the case testable (see next section) and because it keeps the whole `agree-*` bucket on one surface.
- A new `agree-*` PRESERVATION entry only needs a `CharacterRegister` when the case is specifically
  testing register-adjacent preservation (correct agreement near a named register entry, or an
  unnamed-referent ambiguity case). A preservation entry that is not about the register does not need
  one, but adding an unnecessary one still moves it onto the long+short surface, so decide on purpose.

### The Hebrew-authoring rule

Author or source Hebrew text against attested manuscript conventions, do not self-translate from
English. Prefer lifting a sentence (or a minimally-flipped variant, e.g. swapping which character is
grammatically wrong) from an attested source: the `clean-ms-*` entries, or the cleared eval manuscript
they were drawn from. If a sentence had to be authored rather than lifted verbatim, say so in the
entry's `_note` and flag it for user review rather than treating your own Hebrew as ground truth (this
is exactly what happened for `agree-register-07`, `agree-preserve-03`, `agree-preserve-04` and
`agree-preserve-08` when the class was first authored).

### Where the tests live

- Deterministic, no-model structural checks: `Pagedraft.Api.Tests/ProofreadAgreementGoldTests.cs`
  (assembly root, on purpose, so the standing filter `FullyQualifiedName!~Pagedraft.Api.Tests.
  LanguageEngine` still reaches it; do not move it into `LanguageEngine/`).
- Live-model scoring: `LanguageEngine/ProofreadQualityTests.cs`
  (`ProofreadQuality_RunGoldCases_ReportPrecisionRecallFalsePositive`), skip-by-default without a
  reachable Ollama, GPU/live only.
