# Code Review Report — Chatbot w9: deterministic chapter-reference resolution

**Date:** 2026-08-15
**Repo/branch:** `Pagedraft.Api-repo` @ `api-final-r03-and-wave3-close`, HEAD `b59ad8e`
**Reviewer instrument:** full GPU-safe suite (2563 passed / 0 failed / 1 skipped), plus four throwaway
probes run against the pending tree and deleted (their measurements are quoted below and labelled).

## Scope reviewed

- **Plans:** none. This was a fix-bugs session with no plan (confirmed by the owner), so the diff's own
  docblocks are the statement of intent and there is no ticket to diff the scope against.
- **Pending files:** 13 modified, 0 untracked, all in `Pagedraft.Api-repo` (+661 / -150). The client repo
  is clean. 6 production files in `Services/Chat/`, 7 test classes.
- **Feature purpose:** stop the selector manufacturing a chapter ambiguity the author never had. A bare
  "פרק 8" used to ground both order 7 and order 8; w9 resolves the number deterministically (a title that
  names the number wins, else the author is counting from 1), makes a title many chapters share ground
  without spending raw text, suppresses the book-order brief fallback once a question has reached a
  chapter, and raises the escalation slice from 3,500 to 7,200 tokens.

This is squarely the AI/LLM lens (Phase 4c) and the backend-heuristic lens (Phase 4e). No user-visible
frontend change, so the Phase 4f render gate does not apply.

---

## P0 — Bugs that ship broken behavior

### 1. The grounding clause tells the model to ANSWER-for-one while the new note tells it to ASK — in both languages

[ProductChatPrompt.cs:356-359](Pagedraft.Api/Services/Chat/ProductChatPrompt.cs#L356-L359) (En),
[ProductChatPrompt.cs:381-383](Pagedraft.Api/Services/Chat/ProductChatPrompt.cs#L381-L383) (He)

These are live prompt **strings**, not comments, and they were not touched by w9. The clause reads:

> "...where it says a number could have meant two chapters, **say which one you answered for and that it
> could have meant the other**; where it says no chapter was identified, ask which chapter they mean..."

The note w9 now emits ends: "...which one was meant is not known, **so ask the author which of them they
mean**."

**OBSERVED, not inferred.** A probe composed the real instruction for the shared-title case in both
languages and found both strings present in the same prompt, with the clause's trigger phrase also
present: `[en] ANSWER-FOR-ONE clause: True / ASK note: True`, and identically for `he`.

Three distinct faults, all pointing the same way:

- **(a) Direct contradiction.** Answer-for-one versus ask. This is the collision shape recorded as having
  burned this prompt three times, and it points *against* w9's entire purpose: the shared-title half exists
  precisely because answering about a chapter chosen by sort order is the defect.
- **(b) The arity is wrong.** The clause says "could have meant **two** chapters". The new note names up
  to five and then "and N more" — the 32-way case the cap was written for. The clause has no reading for it.
- **(c) Two of the three new note shapes match no branch at all.** The clause is scoped to "where it says a
  **number** could have meant two chapters". The title ambiguity says `more than one chapter of this book
  is named "רוני"` — no number — and `OutOfRangeChapterNote` says "the question names chapter 40, which
  this book does not have", which is neither branch. So the half of w9 whose measured defect was *"selected
  19 chapters, escalated 2, clarify FALSE, a confident answer about two chapters chosen by sort order"*
  reaches the model with no governing instruction, next to a sentence telling it to answer for one.

Per the recorded lesson for this class: fix by **scoping/rewriting the clause**, never by adding a fourth
prohibition. And it is unverifiable by reading, so the fix costs a gate run — price that in.

---

## P1 — Code-quality issues

### 2. `AuthorFacingChapterName` re-emits the exact contradiction it was changed to remove, for bare-numeric titles

[BookArtifactBlocks.cs:172-175](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L172-L175)

The guard is `TitleNamesChapterNumber(trimmed, number) || ChapterNumbersIn(trimmed).Count > 0`, where
`number` is the **position** (`order + 1`). Clause 1 catches a bare digit only when it equals the position;
clause 2 catches only titles containing a chapter WORD. A bare-digit title at a position whose `+1`
differs falls through both.

**MEASURED** (probe, deleted):

| input | selector resolves | renderer emits |
|---|---|---|
| `"פרק 8"`, chapters `[פרולוג, "1".."8"]` | order 8 | `המחבר קורא לפרק הזה: 8 (פרק 9)` |
| `"פרק 24"`, one chapter titled `"24"` | order 0 | `המחבר קורא לפרק הזה: 24 (פרק 1)` |
| *(contrast)* title `"פרק 8"` at order 20 | — | `המחבר קורא לפרק הזה: פרק 8` ✓ |

The second row is character-for-character the `"פרק 24 (פרק 1)"` string this change's own comment says it
exists to delete. The contrast row isolates the defect to the bare-digit cell. `TitleNamesChapterNumber`'s
own docblock names bare-digit titles as a real corpus shape ("what a manuscript whose headings are just
digits imports as"), and the single-chapter re-import is named in the diff as a real shape too — so both
halves of the reachability argument are the change's own.

### 3. `ChapterNumberNote`'s docblock still asserts, in the present tense, the rule w9 deleted

[BookArtifactBlocks.cs:196-225](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L196-L225)

Opens: *"a bare 'chapter 5' grounds BOTH order 4 and order 5 ... the selector keeps both rather than
guessing"* — false as of this diff. Also still describes the note's deleted wording (*"their chapter N or
their chapter N+1, since orders N-1 and N are what rode"*). The `<param>` tag was updated; the `<summary>`
directly above it was not, so the method's own documentation now describes a different method.

### 4. The stale claim has a population of 8 live sites, and two are in the authoritative design doc

Grepping the **identifier** the claim is about rather than its wording (each site phrases it differently):

| site | what it still asserts |
|---|---|
| [BookArtifactBlocks.cs:196-198](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L196-L198) | finding 3 above |
| [BookArtifactBlocks.cs:59-67](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L59-L67) | quotes the deleted note text and a `[CHAPTER 4]`/`[CHAPTER 5]` example that is now unreachable |
| [ProductChatPrompt.cs:321-325](Pagedraft.Api/Services/Chat/ProductChatPrompt.cs#L321-L325) | "the assembler deliberately grounds BOTH candidates" — the comment for the P0 clause |
| [ProductChatBookScopingTests.cs:23-24](Pagedraft.Api.Tests/ProductChatBookScopingTests.cs#L23-L24) | class doc; the file's method docs were updated, its class doc was not |
| [ProductChatBookFortyChapterFixtureTests.cs:173-174](Pagedraft.Api.Tests/ProductChatBookFortyChapterFixtureTests.cs#L173-L174) | "the usual 0-based/1-based dual match" **and** a stale "3,500-token escalation slice" |
| [ProductChatBookRetrievalTests.cs:388-389](Pagedraft.Api.Tests/ProductChatBookRetrievalTests.cs#L388-L389) | same dual-match premise |
| `src/docs/PAGEDRAFT_DESIGN.md:904-907` | "a bare 'chapter 5' resolves to BOTH order 4 and order 5 ... ONLY when both candidates survived the trim" |
| `src/docs/PAGEDRAFT_DESIGN.md:1070-1071` | "the `Note:` reaches the prompt every time both candidates ride" |

Note the last two: `src/docs/` sits in **no git repo**, so a `git grep` sweep structurally cannot see them.
That is the recorded blind spot, hit again — enumerate the sweep by PATH, and say which paths were searched.

### 5. `OutOfRangeChapterNote` ships with zero tests

[BookArtifactBlocks.cs:295-304](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L295-L304)

New model-facing prose with no test reference anywhere in the repo (`grep -rn OutOfRangeChapterNote`
returns only its definition and its single call site). It **is** reachable — probe measured
`"מה קורה בפרק 40?"` on an 8-chapter book producing `unresolved=[40] clarify=True chapterCount=8` and the
note emitted verbatim — so this is a coverage gap, not dead code.

### 6. The `RankChapterBriefs` fallback suppression has no regression test

[BookChatContextReader.cs:691-695](Pagedraft.Api/Services/Chat/BookChatContextReader.cs#L691-L695)

This is the change with the largest measured budget impact (its own docblock cites ~4,500 tokens of
unrelated briefs, six of them, on the owner's real book). The fallback's **positive** case is covered
([ProductChatBookServiceTests.cs:431](Pagedraft.Api.Tests/ProductChatBookServiceTests.cs#L431) — "what
happens in my book" grounds in the opening chapters), but nothing pins the new **negative** case: a
question that reaches one chapter must not drag six briefs it never named. A revert-verify is available
and cheap here.

---

## P2 — Design / structural concerns

### 7. The ambiguity note asks a question the client gives the author no way to answer

The note ends "ask the author which of them they mean", but `NeedsChapterClarification` is false whenever
that note fires — that disjointness is the design's own load-bearing argument
([BookArtifactBlocks.cs:309-318](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L309-L318)). The client
renders its one-click chapter chips only on that flag
([product-chat-entries.ts:83](../../pagedraft-client/src/app/shared/product-chat/product-chat-entries.ts#L83)),
so the model asks in prose and the author gets no affordance. Partly self-mitigating — the note lists the
positions, and a number now resolves deterministically — but the ask is new in w9 and the gap came with it.

### 8. The overflow path is untested and its prose is malformed

[BookArtifactBlocks.cs:258-266](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L258-L266)

**MEASURED** (probe, 8 chapters sharing a title):

> `...(counting from the start of the book they are chapters 1 and 2 and 3 and 4 and 5 and 3 more); ...`

The all-`" and "` join reads as a broken list, and "5 and 3 more" invites parsing "5 and 3" as a pair —
in the one line the grounding rule instructs the model to carry into the author's answer. The 32-way case
`MaxNamedAmbiguityCandidates` exists for is never exercised by a test.

### 9. The two new notes are English-framed on the Hebrew answer path

`ChapterNumberNote` and `OutOfRangeChapterNote` are English, and their `Reference` for a title is a raw
quoted Hebrew title inside English prose. The sibling line on the same contract —
`AuthorFacingChapterName` — was moved to the answer's language *because this exact class was measured at
16% leak* (`g5`: Latin "chapter N" surviving untranslated inside Hebrew prose in 7 of 45 runs), and its
docblock at [BookArtifactBlocks.cs:117-133](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L117-L133)
argues at length that a line the model is told to copy must be in the reader's language. The new notes are
told to be copied and did not get that treatment. **INFERRED** — no gate run was performed; the leak rate
for these specific strings is unmeasured.

---

## P3 — Nit / polish

10. **Malformed XML doc: three unclosed `<para>` tags** —
    [BookChatExcerpts.cs:30](Pagedraft.Api/Services/Chat/BookChatExcerpts.cs#L30),
    [:40](Pagedraft.Api/Services/Chat/BookChatExcerpts.cs#L40),
    [:46](Pagedraft.Api/Services/Chat/BookChatExcerpts.cs#L46). No build impact
    (`GenerateDocumentationFile` is off, verified), but tooltips and any future doc generation break.
11. **Unreachable branch.** `OutOfRangeChapterNote`'s `"this book has no chapters yet"` cannot be reached
    from its only caller: `NeedsClarification` requires `chapterCount > 1`.
    [BookArtifactBlocks.cs:298-300](Pagedraft.Api/Services/Chat/BookArtifactBlocks.cs#L298-L300)
12. **Em-dash in new prose** — [BookArtifactSelector.cs:739](Pagedraft.Api/Services/Chat/BookArtifactSelector.cs#L739)
    (`"פרק 1 — חזרה לנווה־חול"`). Inside a sample title rather than product copy, but the workspace rule is
    stated without that exception.
13. **The escalation constant's upper bound is prose-enforced.** The docblock's claim was **verified
    exactly**: flipping the constant to 8,000 fails the cited test with `Not found: "register"` and dropped
    refs `[finding, history, register]`. But nothing pins the boundary between 7,200 and 8,000, so a later
    drift to 7,500 passes silently while the docblock says it must not.

---

## Cross-stack contract check

No wire contract changed. `BookQuestionKeys` is server-internal; `AmbiguousChapterNumbers` changed type
but is never serialized. The one client-visible field, `needsChapterClarification`, keeps its meaning and
its derivation.

| Aspect | Backend | Frontend | OK? |
|---|---|---|---|
| `needsChapterClarification` (response flag) | `BookQuestionKeys.NeedsChapterClarification`, unchanged predicate | `product-chat.ts:168`, unchanged | ✓ |
| Clarify chips affordance | flag false while the new note asks | chips gated on the flag | ⚠ (finding 7) |
| `AmbiguousChapterNumbers` shape | `int[]` → `ChapterReferenceAmbiguity[]` | not serialized | ✓ |
| `ChapterCount` (new) | server-internal only | n/a | ✓ |
| Chapter ref/order convention | raw 0-based on the wire | `chapterDisplayNumber` = order + 1 | ✓ |
| Author-facing chapter name | `AuthorFacingChapterName` | n/a (prompt-only) | ⚠ (finding 2) |

## Plan deviations / outstanding manual work

- **No plan and no ticket exists** for this work (owner-confirmed fix-bugs session), so there is no
  acceptance example to diff the implementation against. The scope statement in this report is
  reconstructed from the diff's own docblocks — which is the lowest-fidelity input this review had, and
  the reason findings 3 and 4 matter more than usual: the docblocks are the only specification.
- **No live-model gate was run.** Every prompt-facing claim in this diff (and the P0 fix) is unverifiable
  by reading. The suite is green and proves the assembly; it proves nothing about what the model does.
- **The Hebrew wording of both new notes is unread by a native speaker**, like the rest of the open Hebrew
  read (`hebrew-native-read-2026-08-14`).

## Recommended fix order

- **Must-fix (P0):** 1 — scope the grounding clause to what the notes now say, in both languages, covering
  all three note shapes; re-measure on a gate.
- **Should-fix (P1):** 2 (predicate asymmetry, with a revert-verified test), 3 + 4 (one sweep, by
  identifier, across both repos **and** `src/docs/`), 5 and 6 (the two missing tests).
- **Worth scheduling (P2):** 7 (chips affordance — a design call), 8 (list prose + overflow test),
  9 (note language — needs a gate to price).
- **Nit (P3):** 10, 11, 12, 13.

## What was checked and found clean

Recorded so the next reader does not repeat it:

- **The escalation constant's evidence claim is true.** Verified by flipping it to 8,000 and watching the
  cited test fail in exactly the way the docblock describes.
- **No CRLF/CI-portability exposure.** `BookContextAssembler` renders briefs with 19 `AppendLine` calls
  (`Environment.NewLine`), CI is `ubuntu-latest` while local is Windows with `core.autocrlf=true`, and the
  40-chapter fixture composes at 14,019 of 14,080 — the shape of the recorded PR #53 trap. Measured
  anyway: the composed prompt carries **8 CR characters = 2 tokens** against 61 tokens of headroom and a
  4-block drop margin. Not a risk; not filed.
- **The two BOOK-section notes really are disjoint** after the change (the ambiguity note's own
  precondition forces `ChapterOrders.Count > 0`; the clarify flag requires it to be 0), so the shared
  channel still has no ordering to decide.
- **Full GPU-safe suite green:** 2563 passed / 0 failed / 1 skipped, 2564 total, ~8s.
