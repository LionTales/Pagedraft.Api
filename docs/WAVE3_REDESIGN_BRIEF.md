# PageDraft Wave 3 redesign brief

Prepared 2026-08-02 for the design session. Audience: a designer who will not open the codebase.

> ## Decisions taken - owner session, 2026-08-09
>
> All thirteen questions in section 8 are DECIDED. Section 8 remains below as the options record;
> this block is the authoritative answer sheet, and the implementation plan is written from it. That
> plan is not part of this repo - it lives in the PageDraft workspace's plan tree, which is not a git
> repo itself, at `src/.cursor/plans/_archive/wave3-implementation-2026-08-09.plan.md` relative to the
> workspace root that also holds this repo's checkout (`src/Pagedraft.Api-repo`) and the client's
> (`src/pagedraft-client`) as siblings. A reader of only this repo will not have that path on disk;
> ask the owner for the plan file directly.
>
> **Wave 3 is SHIPPED as of 2026-08-13.** Twelve of the thirteen decisions shipped; Q12 split and its
> second half did not.
>
> **THREE THINGS ARE STILL OPEN, not one.** This headline used to say "the one thing still open across
> the whole wave is a human reading, not code", which its own table below contradicted on the same
> screen. Corrected 2026-08-14. What is actually open:
>
> 1. **Q12's scope-statement half is NOT SHIPPED, and it is code.** `editor-page.component.ts`'s
>    `reviewScopeLabel` getter still reads "This chapter" / "פרק נוכחי" regardless of scene selection,
>    so the exact contradiction Q12 was written to resolve is still reproducible today. See the Q12 row.
> 2. **The Q10 sub-decision is undecided.** Whether the first-run overlay eventually defers to the
>    chatbot, embeds it, or stays static is open; the static overlay is what ships. See the Q10 row.
> 3. **The Hebrew native-speaker sweep**, which is the human reading: the guides corpus, the strings the
>    `w7` removals added, and three export failure strings reworded 2026-08-14. Tracked at
>    `HEBREW_NATIVE_REVIEW.md` (see the pointer at the foot of this block).
>
> | Q | Decision | Status as of 2026-08-13 |
> |---|---|---|
> | Q1 | **D - route-adaptive spine**: compact in app chrome, full on book surfaces | SHIPPED (w2/w3) |
> | Q2 | **A - stage 4 renders per-chapter**, no book-level state; never a hardcoded done | SHIPPED (w2) |
> | Q3 | **B - build a minimal export surface** this wave; stage 5 becomes real | SHIPPED (w4) |
> | Q4 | **A - fold the bare-arrow build into the formal build row**; one build, one ceremony | SHIPPED (w5) |
> | Q5 | **REMOVE BOTH free-form prompt surfaces** (beyond any listed option): the chapter Custom prompt block and the dashboard ask-about-the-book. The chatbot (Show) is the ask surface. Removal is sequenced AFTER chatbot phase B ships, so the product never has zero whole-book ask surfaces | **SHIPPED (w7, 2026-08-13). BOTH halves of the gate were met and the equivalence half was VERIFIED, not assumed.** Bucket f passed with identical selector output in both of B's own gates and then held 53 of 53 chapter-resolving runs at B's final re-gate, asserted from the API log's selector line and never from answer wording; B is merged. Removed: the dashboard ask-about-the-book card, the per-chapter Custom free-form block (Custom also left the pass picker) and save-as-template. **Scope fence held and was verified live:** `AnalysisType.Custom`, its persisted rows and its analysis-repair entry all stay, and a real persisted Custom result still renders and is still filterable by type. **Fence correction:** the dashboard card was `AnalysisType.QA`, not Custom, and `QA` stays. **THE EQUIVALENCE DELTA, accepted by the owner rather than papered over, and stated to authors in the guides in both languages:** Show reads at most ~3,500 estimated tokens of manuscript per question across at most 2 chapters and degrades to labeled excerpts above that, where a Custom run put a whole chapter in a ~14,336-token window; scene scope has NO successor, since Show resolves chapters and Custom could run on one scene; and a Custom run produced a persisted, revisitable analysis result, while Show's answers are conversational and land nowhere. Both vacated slots keep a pointer to Show for one release |
> | Q6 | **A - style baseline moves to the book dashboard** beside the other builds, WITH a new global directive: **dashboard elements become collapsible** - the big parts and the parts inside them, where it makes sense and does not complicate | SHIPPED (w5) |
> | Q7 | **A - remove "Save as template"** (falls out of Q5 anyway; Phase C personalization is the real version) | **SHIPPED (w7, 2026-08-13).** It went with the Custom prompt box it sat beside. The client's template read/write methods and DTO went too; the API's `TemplatesController` and `PromptTemplate` entity are untouched and still serve `/api/templates` |
> | Q8 | **C - reframe the chapter-brief editing card as "the inputs to this build"**, visibly part of stage 2 | SHIPPED (w5) |
> | Q9 | **C - rename the Summarize pass AND state on the surface** what it does and does not feed | **SHIPPED (w6).** As "Chapter recap" / "תמצית פרק", with the relationship statement on the run tab and on the book-briefs row. **The Hebrew is CLEARED, not draft:** the owner read it on 2026-08-11 and the load-bearing check passed, `תמצית` reading as distinct from `תקציר` (`תקצירי ספר`) to a native ear. Shipped on `client-wave3-orientation` / `api-wave3-orientation` |
> | Q10 | **D - self-explaining build rows as the permanent mechanism + a first-run overlay** pointing at them | **SHIPPED (w2 + w6).** The rows explain themselves, state their prerequisite and offer the next action; the first-run overlay renders from the served `workflow-overview` guide, dismisses permanently per book and re-opens from a named affordance, all re-verified live 2026-08-13. **ONE SUB-DECISION STAYS OPEN as of 2026-08-13:** the chatbot can now tutor from this book's real build status, so whether the overlay eventually DEFERS to it, EMBEDS it, or stays static is undecided - see the update under Q10 below. The static overlay is what ships today |
> | Q11 | **A - the tier control stays at the point of use**; the two passes where it vanishes get a disabled-with-reason state instead of absence | SHIPPED (w5) |
> | Q12 | One scene-aware scope statement replaces the label+subtitle pair; book-level running state moves into the spine | **SPLIT.** The running-state half SHIPPED (w3, moved into the spine). The scope-statement half is **NOT SHIPPED**: `editor-page.component.ts`'s `reviewScopeLabel` getter still reads "This chapter" / "פרק נוכחי" regardless of whether a scene is selected, while the adjacent `reviewContextMeta` getter correctly distinguishes "scene" / "chapter" - the exact contradiction this question was meant to resolve is still reproducible. Found during the f1 docs pass; not tracked by any prior w1-w8 todo |
> | Q13 | **A - first-run orientation is driven from the served guides** (`stage`/`id` frontmatter); the serving path is built by chatbot phase A.2, so this rides an existing dependency | **SHIPPED (w6)**, consuming the serving path chatbot phase A.2 built. All ten stage-to-guide links were re-verified landing on the right guide, in the right language, 2026-08-13. **A SECOND content source now exists (2026-08-12)**: the chatbot answers the same orientation questions from THIS book's real status rather than from generic guide prose - see the update under Q10 below. **A constraint this decision created and that the w8 gate then had to enforce:** the guides are a retrieval index as well as content, so a stage renamed in the app cannot simply be renamed in the guide headings - see the note under the deliberately-did-not-do list below |
>
> **What Wave 3 deliberately did not do:** no book-level rollup for stage 4 (Q2-A declined it, not
> merely deferred it), no template library (Q7 removed the feature instead of building one), no
> restyle of the editor canvas, and no AI behavior change anywhere in the wave - including `w7`,
> which removed two client entry points and changed no prompt and no model route.
>
> **Four more things the w7/w8 close deliberately did not do, added 2026-08-13:** there is no
> successor for scene-scoped free-form asking (Custom could run on one scene; Show resolves
> chapters); there is no persistence for Show's answers, so a free-form answer worth keeping must be
> copied out; `AnalysisType.Custom` was not deleted, only its UI entry point, and existing Custom
> results still render and still filter; and **the guide H1s were NOT renamed to the app's canonical
> stage-4 name.** That last one is a constraint rather than an oversight: `Services/Chat/GuideSelector`
> scores question tokens against H1/H2 headings at weight 3.0 and the frontmatter `id`/`stage` at 1.0,
> and reads no body prose at all, so a guide heading IS the chatbot's retrieval index and a copy edit
> to one silently re-ranks which guides reach the model. The w8 gate found four Hebrew names for stage
> 4, two of them rendering in a single viewport, and renamed only the two that carry no retrieval
> weight. Stage 4 therefore ships under two names on purpose, and whether that is one name too many is
> an open naming decision for the owner.
>
> Full per-question detail and the roadmap-level record live in the workspace docs (outside this
> repo): `PAGEDRAFT_ROADMAP.md` (Wave 3 section) and `HEBREW_NATIVE_REVIEW.md`, both at `src/docs/`
> relative to the workspace root described above. The implementation plan is archived at
> `src/.cursor/plans/_archive/wave3-implementation-2026-08-09.plan.md`.

Everything below is written in product terms. Where a claim rests on code, the code lives in
Appendix B and in the phase 0 plan referenced there. You do not need either to do the work.

---

## 0. What you are being asked to design

PageDraft is a book editing tool for authors. An author imports a manuscript, the product builds
book-level understanding of it, runs an AI developmental review across the whole book, runs
line-level editing passes chapter by chapter, and exports a finished file. It works in Hebrew and
English, and Hebrew is the primary language.

The reported problem is one sentence from the product owner: it is still too complicated for a user
to understand the stages.

This brief gives you the diagnosis, the corrected stage model to design against, the concrete
reorganization list, the constraints you cannot discover from screenshots, the scope fence, and the
decisions the session has to make.

---

## 1. The problem, in user terms

### 1.1 The product tells the user two different stories about its own workflow

This is the headline finding and it should shape the session before any visual question is asked.

PageDraft currently ships **two stage models that do not agree with each other**.

| Source the user sees | The stages it names |
|---|---|
| The stage strip inside the app | Structure, Assess, Revise, Polish |
| The written guides the same product ships | Import, book setup and intelligence, chapter editing passes, whole-book review, export |

Different count, different names, different boundaries. A user who reads the guides and then looks
at the app is being told two incompatible things about what the work even consists of.

It gets worse when you look at what the in-app strip can actually say:

- **The first stage is always shown as complete.** It is not calculated. Open a book with no
  chapters at all and the strip still reports that stage as done.
- **The last stage is permanently greyed out.** It advertises a feature that has never been built,
  and it will never light up, so a fifth of the strip's visual weight is spent saying "nothing here".
- **The two stages in the middle are driven by the same small set of flags**, so in practice the
  strip is a two-state indicator wearing four labels.
- **It cannot express the most common real situation**, which is "you built this, then you changed
  the book underneath it, so it is now out of date". That state is the single thing the written
  guides spend the most words on, and the strip has no way to show it.

So the product's visible answer to "what stage am I in" is weak, and it contradicts the product's own
documentation.

### 1.2 How confident are we in this diagnosis

**Honest framing: this is the strongest available explanation, not a measured one.** No usability
study was run. Nobody watched a user get lost and traced it to the stage strip. What we have is a
verified structural contradiction inside the product plus a report of confusion, and the
contradiction is large enough and central enough that it would be strange if it were not a major
cause.

**What we did do: we opened the running app and looked.** Sections 1.1 and 3 were originally derived
by reading source code. They were then re-checked against the live product on a real book and on an
empty one. Everything held, and two of the claims turned out to be understated. On a book with no
chapters at all, the strip reports `Structure: Done` and `Revise: Available` and offers a prominent
`Build review` button, while the same panel one scroll below says `Book briefs: Not built` and
"The developmental review requires book briefs." **The spine contradicts the panel beneath it inside
a single screen, on a book that contains nothing.** That is the clearest available demonstration of
why this wave exists, and it is worth reproducing in front of the session rather than describing.

Treat it as the leading hypothesis with strong supporting evidence. The practical consequence is the
same either way: **a redesign that restyles both models without reconciling them ships the confusion
in nicer type.** Reconciling them is cheap, and it is a prerequisite for everything else in this
wave, including the planned chatbot, which will otherwise learn a third vocabulary from whichever
source it happens to read.

### 1.3 Three smaller problems that compound it

1. **Two of the product's terms collide.** There is a per-chapter pass called "Summarize" and a
   book-level artifact commonly called "the book summary". They are unrelated: running Summarize on
   every chapter in the book produces nothing that the book-level build reads. The written guides
   burn two full sections and an FAQ entry keeping them apart. A vocabulary that needs an FAQ entry
   to separate two of its own terms is not describing the confusion, it is the confusion.
2. **The product has two free-form "ask it anything" surfaces that do not know about each other.**
   One sits in the per-chapter tabs and can see only the passage in front of it. The other sits on
   the book dashboard and can see the whole book. Different names, different places, nothing on
   either mentions the other. A user who wants to ask about the book will very reasonably find the
   chapter one first and conclude the feature is bad.
3. **One whole-book build is hidden inside a per-chapter screen.** See section 3.

### 1.4 What the audit found that the brief-writer did not expect

A full sweep of all 53 user-facing surfaces found that **the per-chapter tab set is mostly correctly
scoped.** "Reorganize the edit tabs" turns out to be a three-item list, not a rewrite. That is a
useful result, because it means the session's time belongs on the stage spine and the guided flows,
not on relocating things that are already in the right place.

---

## 2. The spine: one reconciled stage model

This replaces both existing models. Everything else in the redesign hangs off it.

### 2.1 The five stages, in canonical order

**Import → Book briefs → Developmental review → Chapter editing passes → Export**

| # | Stage | The user does | They get | Cannot start until |
|---|---|---|---|---|
| 1 | **Import** | Uploads a manuscript file, checks how it was split into chapters, confirms | Their book, as chapters | A book exists |
| 2 | **Book briefs** | Presses build | A short brief per chapter, composed into one book-level brief | Stage 1 |
| 3 | **Developmental review** | Presses build, then works through the findings and marks each one | Findings across plot, character, pacing, tone, theme and continuity, each with a status the user sets | **Stage 2. This is the product's one hard prerequisite** |
| 4 | **Chapter editing passes** | Opens a chapter or selects a scene, runs one of six passes, accepts or dismisses what comes back | Suggested text changes to approve, or reports to read | Stage 1 only |
| 5 | **Export** | Downloads the book, or one chapter, as a file | A document file | Stage 1 only |

Three things about this order that matter for the design:

- **Stage 3 genuinely cannot run before stage 2.** The system refuses it. This is the single most
  common wasted action in the product, and the current strip is built so that it structurally cannot
  warn about it, because it fuses those two stages into one box. **Making that one dependency visible
  is the highest-value thing a stage spine can do here.**
- **Stages 4 and 5 are not gated on 2 and 3.** They only need chapters. The spine must not imply
  that an author has to run AI analysis before they are allowed to edit or export.
- **Stage 4 comes after 3 by advice, not by rule.** Rewriting a chapter after proofreading it means
  proofreading it again, so the sensible order is to settle the big structural questions first. The
  spine should communicate a recommended order without locking anything.

### 2.2 One state vocabulary for all five stages

The current strip invents a different vocabulary per step. Replace it with one set, used identically
everywhere:

| State | Means | Design note |
|---|---|---|
| `blocked` | A named prerequisite is missing | Must say *which* one and offer the way to fix it. Today only stage 3 can be blocked |
| `not-started` | Nothing built yet | The inviting state, this is where a first-run user lives |
| `running` | A build is in flight | |
| `behind` | Built, but what it was built from has moved | **Do not treat this as decoration.** It is the state users hit most, and the current strip cannot express it at all. It also has a magnitude (how many chapters moved) and a reason |
| `ready` | Built and current | |
| `unavailable` | No surface exists for this stage yet | Honest greying, with the reason. Applies to Export today |

`behind` deserves specific design attention. It is not an error. The user did nothing wrong. They
edited their book, which is the entire point of the product, and now a derived artifact lags. The
tone has to be "this is out of date, rebuild when convenient", never "something failed".

### 2.3 What the app can and cannot honestly report today

This is the constraint that decides what you can design for the near term versus what needs backend
work first. **Anything the app cannot compute becomes another hardcoded lie, which is the exact
defect this wave exists to fix.**

| Stage | Can the app show its state today? |
|---|---|
| 1 Import | **Partly.** The app can tell whether a book has chapters, but only once you are inside the book. On the books list, where importing is actually the next action, it cannot. Also, import runs with no progress reporting, so there is no `running` state to show |
| 2 Book briefs | **Fully.** Every state including `behind`, its magnitude, and its reason, is already available and is currently being thrown away by the strip |
| 3 Developmental review | **Fully**, including `blocked`. Progress through the findings is available but requires loading the full findings list |
| 4 Chapter editing passes | **No, not at book level.** `running` is visible book-wide, but "how far through the book is the line-level work" cannot be answered without asking once per chapter. **Design stage 4 as a per-chapter picture, not a single book-level tick**, unless the session decides to fund the backend work |
| 5 Export | **No, because the stage is not reachable.** The capability exists in the system but has no screen at all. Show it as `unavailable` with the honest reason |

Five specific gaps were identified and are recorded as work items in Appendix B. Four of the five are
small projections of data the product already stores. None requires AI work.

### 2.4 What happened to the old stage names

- **Structure** was never calculated. Replaced by **Import**, which can be calculated.
- **Assess** fused two stages together across the product's only real dependency. Split into
  **Book briefs** and **Developmental review**.
- **Revise** was not a stage. Working through review findings is the second half of stage 3, and the
  text changes it leads to happen in stage 4. Its stated reason for existing as a fake step is also
  out of date: the per-finding progress data it claimed was unavailable does in fact ship.
- **Polish** does not survive. It was a placeholder for a whole-book proofread run in one queue. That
  feature has never been built and nothing in the system could back it. When it is built, it belongs
  inside stage 4 as a book-wide run mode of an existing pass, not as a permanently grey fifth column.
  **Do not carry a disabled Polish step into the new design.**

### 2.5 Naming, and the Hebrew

The product's shipped labels win over the guides' wording, for one substantive reason: "book summary"
collides head-on with the per-chapter "Summarize" pass, and "Book briefs" does not.

| Stage | English | Hebrew |
|---|---|---|
| 1 | Import | ייבוא |
| 2 | Book briefs | תקצירי ספר |
| 3 | Developmental review | עריכה התפתחותית |
| 4 | Chapter editing passes | מעברי עריכה על פרק |
| 5 | Export | ייצוא |

**All five Hebrew names were DRAFT and pending native-speaker review when this table was written.**
Two of them were already visible in the shipped product, but they were flagged DRAFT when they shipped
and were not exempt then. One specific thing a native reader had to confirm: stages 3 and 4 both
contain the word עריכה, and the pair must not read as one concept.

> **Update 2026-08-14 (the table above is the design-time record and is left as written).** All five
> names were READ AND ADJUDICATED by the owner, a native speaker, on 2026-08-11, and they are CLEARED.
> Stage 4 was renamed in that sweep: `מעברי עריכה על פרק` became **`עריכת פרק`**, because `מעברים` reads
> as "transitions" rather than "passes". The owner also cleared the עריכה overlap between stages 3 and 4
> knowingly. What is still open is the wider `מעבר` terminology question, deliberately deferred by the
> owner on 2026-08-12, and it is why the guides' own H1 still says `מעברי העריכה על פרק`: guide headings
> are the chatbot's retrieval index, so a rename there re-ranks retrieval. Record:
> `HEBREW_NATIVE_REVIEW.md` in the workspace docs.

Do not treat the Hebrew column as settled copy. Design so the labels can change length and wording
without breaking layout.

Consequence to note: the written guides currently lead with "book summary" and will need editing to
lead with the label above. The chatbot must be given the same primary terms.

### 2.6 A hard width constraint: today's strip cannot render its own four labels

This is not a style preference, it is a measured failure of the current component, and it directly
constrains the shape of anything that replaces it.

The existing strip lives in a side panel whose default width is 380 pixels and which the user can
narrow to 300. At that default width its four labels already clip, in **both** languages:
`Structure` renders as "Struc...", `Assess` as "Asse...", `Polish` as "P..." with its status text cut
mid-word. In Hebrew, ליטוש renders as "לי...". **The one component whose entire job is to name the
stages cannot currently display its own names.**

The reconciled model has **five** stages, not four, and three of the five names are two words long
(`Book briefs`, `Developmental review`, `Chapter editing passes`, and in Hebrew `עריכה התפתחותית`,
`מעברי עריכה על פרק`). A horizontal strip of five equal columns in a 300 to 380 pixel panel is not a
viable form for this content. Anything that keeps the horizontal-strip shape must earn it, by
showing how five labels of that length fit at 300 pixels in Hebrew and in English.

This interacts with **Q1**: if the spine moves somewhere wider than the side panel, the constraint
relaxes. If it stays in the panel, the form has to change. Do not solve this with truncation or a
tooltip. A stage name the user cannot read does not orient anyone, which is the whole point of the
spine.

---

## 3. The reorganization list

The owner's instinct was that whole-book concerns are sitting in the per-chapter tabs. The audit
tested that against all 53 surfaces. Here is what it actually found.

### 3.1 Confirmed moves: three items

| # | What | Where it is now | Why it must move |
|---|---|---|---|
| MOVE-1 | **The book-wide style baseline build**, its status and its rebuild action | Buried inside the per-chapter analysis screen, and only visible after you select one specific pass type from a picker | It builds a book-wide artifact, spends book-wide cost, and sits under a label that literally reads "This chapter". It is the clearest mis-scoping in the product |
| MOVE-2 | **The consent prompt, cost estimate and paid-tier note** for that baseline | Same place | Consent for a whole-book spend must be asked where the whole-book action lives |
| MOVE-3 | **"Save as template"**, or delete it | In the free-form prompt block of the per-chapter screen | It saves a template that is global to the whole installation, minted from a single chapter's screen, and **no screen in the product ever displays the saved templates.** The user is offered a save action whose output they can never see again. Move it or remove it is itself a decision, see Q7 |

The style baseline deserves a callout beyond its move. It is a real book-level artifact that
**appears in none of the written guides**, and the only way to discover it is to open a chapter, open
the assistant panel, switch modes, and pick one particular pass. It needs a home, a name a user can
understand, and a guide section.

### 3.2 The two items the owner named are real problems, but they are not scope problems

Both need a different fix from moving.

**"Summarize"** genuinely is about the single chapter, and it belongs in the chapter tabs. Two things
are wrong with it. Its name collides with the book-level artifact, as described in 1.3. And its
output is a **dead end**: it saves a result the user can read, and nothing else in the product ever
consumes it. A user who runs it on all forty chapters expecting to have built the book summary has
built nothing of the kind. **Fix by renaming and by stating the relationship on the surface itself,
not in a guide. Do not move it out of the chapter tabs; that would make the collision worse.**

**"Custom"** is the *most* chapter-scoped thing in the entire product. It is the only pass given no
surrounding context at all. The belief that it is a whole-book concern is not a description of what
it does; it is most likely a user reaching for the thing they actually wanted, which already exists
somewhere else under a different name and can see the whole book. **Fix by relating the two prompt
surfaces to each other, in both directions, and making the difference legible at the point of use.**

### 3.3 Surfaces where the product contradicts itself

Twelve surfaces were flagged ambiguous, meaning the product itself gives two different answers about
what they belong to. These are the redesign's real targets. Beyond the ones already covered:

- **A whole-book build with no ceremony.** A bare circular-arrow icon on the book dashboard triggers
  an expensive whole-book AI run. It has no status row, no consent, no cost estimate and no entry in
  the activity list, and it sits three lines above two other builds that have all four. It also
  writes over the same underlying data one of those builds depends on. See Q4.
- **A per-chapter editing card inside the whole-book tab.** The owner's constraint in reverse. It has
  a defensible reason, so it is a decision rather than a move. See Q8.
- **A "review running" indicator hung on two unrelated controls.** Book-level build state is
  currently rendered as a dot on a layout toggle and on a panel reopen button, purely because those
  are the only things still visible when the dashboard is not on screen. Not wrong today, but the
  redesign will have a proper spine to hang it on.
- **A scope label that contradicts the line next to it.** One strip says "This chapter" while the
  text two elements away says "scene". Small, but it is on the one surface whose entire job is to
  tell the user what scope they are in.
- **A tier control whose blast radius is wider than its position implies.** It sits on a per-chapter
  screen but the setting it writes applies to the whole book, so changing it while chapter 3 is open
  silently changes every chapter. It also vanishes entirely for two of the six pass types. This was a
  deliberate recent decision, so it is flagged, not pre-judged. See Q11.
- **A vocabulary entry in the activity list that nothing ever produces**, and a single generic icon
  used for every kind of chapter run, so an in-flight Summarize shows a proofreading icon.

### 3.4 Staying put on purpose

So that nothing gets "reorganized" by reflex: the Summarize and Custom buttons themselves, the hint
inside one pass's results that points at the style baseline (a legitimate cross-scope pointer that
only needs to point at the baseline's new home), the per-chapter findings checklist (a
chapter-filtered view of book-level findings, and exactly what the chapter tab should contain), and
the app-level activity list.

---

## 4. The owner's three constraints, verbatim

Quoted exactly as recorded when the wave was decided on 2026-08-01.

> some visuals stay and the edit tabs mainly need REORGANIZING (whole-book concerns such as Custom
> and full-book Summary do not belong in the per-chapter tab set)

> the SUMMARIZE and ANALYZE-BOOK flows are the confusing core and are candidates to become guided
> steps

> a first-run orientation surface should tutor the real stage order

Two notes on reading these, from the audit rather than from the owner:

- On the first: the instinct was right that something whole-book is hiding in the chapter tabs, but
  it is the style baseline rather than the two items named. See 3.1 and 3.2.
- On the second: "candidates to become guided steps" is a candidacy, not a decision. Whether they
  become guided steps, and what shape a guided step takes, is Q10.

---

## 5. Hard constraints you cannot discover on your own

Every item here has already cost this codebase real work. Violating any of them produces something
that looks correct in a mockup and breaks in the product.

### 5.1 Hebrew right-to-left is the default, not an afterthought

The primary language is Hebrew. Design right-to-left first and check left-to-right second, not the
reverse. Assume every layout will be mirrored.

**And do not assume mirroring is automatic.** This is the part that has repeatedly gone wrong here.
Some elements must mirror with the language and some must stay physically fixed, and getting the two
categories mixed up is the recurring bug:

- The **draggable divider** between the document and the side panel must sit on a **physically fixed
  edge**, because the panel's own contents flip direction with the Hebrew text inside them. When the
  divider was allowed to follow content direction it ended up on the wrong side of the panel.
- The **activity bell sits at the inline-start corner**, which is **physically top-right in Hebrew and
  top-left in English**. Anything that animates toward it, points at it, or reserves space near it
  flips sides per language. If your design has an element flying to a notification target, it has two
  mirror-image versions.
- The **document editor's own text direction is a separate axis** from the app chrome's direction.
  They are not the same setting and they can legitimately disagree.

Practical ask: for any layout you produce that involves a fixed edge, an anchored overlay, or motion
toward a corner, state explicitly whether it mirrors or stays put.

### 5.2 Two different language rules apply in two different places

This is not a bug and it must be preserved:

- **Book-scoped chrome follows the language of the book.** Open an English book and the review
  surfaces, stage rows and their direction render in English, left-to-right, even for a
  Hebrew-speaking user.
- **App-level chrome is Hebrew by default**, independent of any book. The books list, the create-book
  form and the activity list are Hebrew and right-to-left.

Consequence for the spine: **if the spine is app-level chrome it is Hebrew-default; if it is
book-scoped it follows the book.** Since a spine that mounts in both places is one of the live
decisions (Q1), your answer to Q1 determines which rule applies, and a spine that appears in both
places may need to switch rules as the user moves. Design for that rather than being surprised by it.

Present limitation to know about: the activity list cannot currently determine the per-row book
language, so it uses the app language for every row. If your design puts per-book text in app-level
chrome, that is a new requirement, not a free assumption.

**A note on how solid this rule was.** When this brief was first written the rule above was stated as
settled fact. Opening an English book showed it was not: the book dashboard's own profile card was
Hebrew-only and locked to right-to-left, so an English book rendered a half-Hebrew, half-English page
with a Hebrew title, a Hebrew refresh tooltip and Hebrew section headings sitting beside English
stage labels. Every child component on that page had always honored the rule; only the dashboard's
own card had never been translated, and no test caught it because every existing test set the book
language to Hebrew.

The chrome part of that has since been fixed and locked with tests: the card's roughly 36 hardcoded
strings moved into Hebrew and English label maps, and the root container's hardcoded dir="rtl" became
a binding that follows the book language, both confirmed by tests in both languages. What was still
outstanding when this brief was written is the content the card generates, not just the chrome around
it: the same card's own server calls, refreshProfile and ask, omitted the language argument, so it
defaulted to Hebrew all the way through the API. An English book's profile refresh generated Hebrew
briefs and stamped the language-keyed cache rows as Hebrew. That second gap has since been closed too,
in the same working session: both calls now pass the book language, and tests assert the argument in
Hebrew, in English and on an unset language. The history is recorded here rather than deleted, for two
reasons. First, so nobody reads section 5.2 as
describing a mature, uniformly enforced system: it is a rule the codebase agrees with in principle and
has violated in practice. Second, because the same class of gap is easy to reintroduce. Any new surface
this redesign produces has to take the book language as an input rather than assume Hebrew, and needs a
test in both languages, or the next English book will look exactly like the last one.

### 5.3 No em-dash, and no en-dash, in any user-facing text

Plain hyphens, or restructure the sentence. This applies to English and Hebrew copy alike, to
placeholder copy in mockups, and to anything a copywriter hands over. (This brief follows the same
rule.)

### 5.4 A design token system already exists and is already in use

There is a shipped `--pd-*` token set: a blue-forward primary ramp, a teal secondary, cool-tinted
neutrals, semantic surface and text aliases, a verdict palette (green keep, amber improve, red cut),
severity colors, spacing, and a font stack (Roboto for UI, Source Serif for reading, Roboto Mono for
metadata), all self-hosted with no external font CDN. It is used by roughly twenty component
stylesheets, **including the stage strip you are replacing.**

Work with these tokens. Extending the set is fine and expected. Introducing a parallel palette is
not, because the surfaces that stay (section 6) are painted with these and will not be repainted.

### 5.5 The theme wraps around the document editor, it does not restyle it

The manuscript editing surface is a third-party component (Syncfusion) with its own Material theme.
The app's styles load after it and deliberately **do not touch its internals.** That boundary is
load-bearing and it holds today.

What this means for you: **the document editing canvas and its formatting toolbar are effectively
out of your reach.** You can change the frame around them, the panel beside them, the header above
them and the spacing between them. You cannot restyle the toolbar buttons or the page canvas, and
proposing that would be proposing a project with a very different cost.

### 5.6 No model or provider identity may appear anywhere, ever

Which AI model or vendor runs any given task is confidential IP. It used to leak into visible labels
and it was deliberately stripped out. **A redesign must not reintroduce it.** No model names, no
vendor names, no version numbers, no "powered by" line, no hints in tooltips, error messages, empty
states or activity rows.

The approved public vocabulary is capability language only: two tiers named **fast** and **thinking**
(Hebrew מהיר and מעמיק), described in terms of speed and depth, plus one honest disclosure that the
thinking tier may process text at a third-party provider so the text leaves the machine. That
disclosure names no provider and must not gain one.

If a mockup needs to show "what is running", the answer is the task name and the tier, never an
engine.

---

## 6. What is explicitly NOT being redesigned

Grounded in the owner's "some visuals stay" and in the 41 surfaces the audit marked as correctly
placed. Treat this as a fence.

**Not touched at all:**

- The manuscript editing canvas and its formatting toolbar. Out of reach by construction, see 5.5.
- The chapter tree: selecting, reordering, renaming, deleting chapters, splitting and clearing
  scenes. It works and it is correctly scoped.
- The editor's working chrome: save state text, save button, the scene badge, the direction buttons,
  back-to-books.
- The books list, the create-book form and the per-book row actions.
- The import screen itself, including its split preview and append-versus-overwrite choice. The
  handoff card that appears after import is in scope as content, but the import flow is not being
  rebuilt.

**Kept as-is in content, open only to visual refresh:**

- The six analysis pass types and what each one does. Renaming Summarize is in scope; changing what
  the passes are is not.
- The chapter-level results renderers: proofreading suggestion cards, line-edit categories, the
  linguistic and literary reports, the history and versions tabs.
- The book review content cards: findings ledger and its statuses, story bible, overview, synopsis,
  characters, plot structure, ask-about-the-book.
- The per-chapter findings checklist.

**Structural things to keep and lean on rather than replace:**

- **The two-mode switch between chapter work and whole-book work.** The panel already tells the user
  which scope they are in, on screen, in words. Every ambiguity in section 3 is a surface
  contradicting a statement the product is already making correctly. **That switch is an asset. Keep
  it and make it more load-bearing, do not replace it with something implicit.**
- **The activity list stays app-level.** It is correctly app-level by design. It needs two content
  fixes (a dead label removed, per-kind icons) and no relocation.
- The token system and font stack (5.4).

**Explicitly out of scope for this wave:** the AI behavior itself, tier routing rules, the analysis
engine, and the planned chatbot's own interface. The chatbot's *vocabulary* is in scope only in the
sense that it must be given the reconciled stage names.

---

## 7. First-run orientation must be driven by the shipped guides, not hardcoded

The owner asked for a first-run surface that tutors the real stage order. **Build it as a reader over
existing content, not as hand-written tutorial copy.**

The reason is concrete. A chatbot is planned as the next wave, and it will take over tutoring. Any
tutorial prose hardcoded into components now is throwaway work that will then have to be kept in sync
with the chatbot's answers, which is exactly how a product ends up with a third contradictory stage
model.

**The content already exists.** A corpus of guides shipped on 2026-08-02: seven guides, each in
English and Hebrew, fifteen files including an index. Every file carries structured frontmatter, so
it can be indexed and addressed rather than parsed:

| Frontmatter field | Values present | What it can drive |
|---|---|---|
| `id` | `workflow-overview`, `import`, `book-setup-and-intelligence`, `chapter-editing-passes`, `whole-book-review`, `export`, `faq`, `guides-index` | Stable addressing of a guide from a UI element |
| `stage` | `overview`, `import`, `book-intelligence`, `chapter-editing`, `whole-book-review`, `export`, `faq`, `index` | **The join key from a spine stage to its guide.** Four of the five stages map one-to-one; stage 2 (book briefs) lives inside `book-intelligence` |
| `lang` | `en`, `he` | Language pairing, honoring the book-scoped versus app-level rule in 5.2 |
| `audience` | `author` | Reserved for later audience splits |
| `updated` | date | Freshness display, and a signal when a guide lags the product |

Beyond frontmatter, the corpus is written to be excerpted: sections are self-contained by convention,
and the section headings are already the questions a first-run user has. Examples that map directly
onto the design work in this brief:

- `00-workflow-overview.md`: "The five stages", "What actually depends on what", "A practical order",
  "What goes stale, and why". That last one is the copy source for the `behind` state in 2.2.
- `10-import.md`: "How the manuscript is split into chapters", "When the split comes out wrong".
- `40-whole-book-review.md`: "It needs the book summary first". That is the copy source for the one
  hard dependency in 2.1.
- `50-export.md`: "Availability". The honest reason for the `unavailable` state in 2.3.
- `90-faq.md`: "What is the difference between running Summarize on a chapter and building the book
  summary?" and "Why is my summary or review marked out of date?".

The corpus also already enforces two of the constraints in section 5: it contains no model or
provider names anywhere, and it is written against the workflow rather than against the screens, so
it survives this redesign.

**Two honest caveats the session must account for:**

1. **Update, 2026-08-11: the guides are now reachable from the app.** Chatbot phase A.2 built the
   serving path this section called for and a reader on top of it: `/help` (index, grouped by stage)
   and `/help/:guideId` (single guide, optional `?lang=he|en`), backed by `GET /api/guides?language=`
   and `GET /api/guides/{id}?language=`. This is the same path Q13 in section 8 anticipated needing;
   Wave 3 now wires orientation against an existing route instead of building one. (It was built on API
   `api-chatbot-a2-guides` and client `client-chatbot-a2-show`, and it is **on master since 2026-08-11,
   shipped as `Pagedraft.Api#56` + `pagedraft-client#39`** - the "check that both are on master before
   depending on the path" caveat this line used to carry is discharged. Corrected 2026-08-14.)
2. **The guides currently carry the old vocabulary in two places.** They lead with "book summary"
   rather than "Book briefs", and their numbered stage list disagrees with their own recommended
   order section. Both need a copy edit as part of this wave, before anything is driven from them.

**Design implication:** treat first-run orientation as a *view* of the guides, not as a separate
artifact. A stage in the spine should be able to say "here is what this stage is" by pointing at
content, and the same pointer should later be what the chatbot cites.

---

## 8. The decisions this session must make

Each of these is a genuine fork surfaced by the audit. Each is stated as a decision with named
options and the tradeoff, not as an invitation to explore. None is pre-decided.

### Q1. Where does the stage spine live?

Today the strip mounts **only inside the editor's side panel**, which means it is invisible until the
user is already two stages in, on the two routes where stages 1 and 5 actually happen (the books list
and the import screen) there is no stage indicator at all, and a first-run user cannot see it.

| Option | Tradeoff |
|---|---|
| **A. Book dashboard only** | Book-scoped, so the language rule is simple. But it is still invisible from the books list and the import screen, so it still cannot orient a first-run user |
| **B. Editor side panel only (status quo placement)** | Zero placement work. Fails the stated wave goal outright |
| **C. Persistent app-level spine, always visible** | Orients from the very first screen. Costs permanent vertical or horizontal space in an app whose main surface is a document, and puts it under the Hebrew-default app-level language rule even when a book is English (see 5.2) |
| **D. Route-adaptive: a compact spine in app chrome, expanding to a full spine on book surfaces** | Best coverage. Most design work, two states to specify, and it has to switch language rules as the user moves between app-level and book-scoped context |

This is the load-bearing decision. Almost everything else in the redesign depends on the answer.

**Carry the width constraint from 2.6 into this decision.** The side panel is 300 to 380 pixels
wide, and the current four-label strip already clips there. Option B therefore is not merely the
status quo, it is the status quo plus a fifth label in a container that could not hold four. Options
A, C and D each buy more horizontal room, and how much they buy should be part of how they are
judged, not discovered afterwards.

### Q2. How does stage 4 render, given the app cannot report it at book level?

Chapter editing passes cannot be summarized book-wide today (2.3). The backend work to fix it is
small and well understood, but it is work.

| Option | Tradeoff |
|---|---|
| **A. Present stage 4 as a per-chapter picture only, no book-level state** | Honest and shippable now. The spine has one stage that reads differently from the other four |
| **B. Fund the backend rollup first, then show a real book-level state** | A uniform spine. Adds a backend dependency to the critical path of the redesign |
| **C. Show a partial aggregate assembled from per-chapter requests** | Looks uniform, is slow and fragile on a long book, and risks looking authoritative when it is approximate |

**Hard rule regardless of choice: stage 4 must never show a hardcoded "done".** That is the exact
defect this wave exists to remove.

### Q3. What does the spine do about Export, which has no screen at all?

The capability exists in the system and has zero user surface.

| Option | Tradeoff |
|---|---|
| **A. Show it as `unavailable` with the honest reason** | Truthful, cheap, matches what the written guides already say. The spine ends on a dead stage, which is uncomfortably close to the "permanently grey Polish column" defect we are removing |
| **B. Build a minimal export surface as part of this wave** | The spine has five real stages and the product gains an obvious missing feature. Scope growth on a wave that was meant to be design-led |
| **C. Drop export from the spine until it exists** | Cleanest-looking spine. Hides a capability the product genuinely has, and the guides describe |

### Q4. What happens to the two overlapping whole-book build entry points?

The book dashboard has a formal build row with a status, consent, a cost estimate and an activity
entry. Three lines above it sits a bare circular-arrow icon that triggers a comparable whole-book AI
run with none of those four things, and writes over overlapping data.

| Option | Tradeoff |
|---|---|
| **A. Fold the arrow into the formal build row, one action** | Simplest mental model, one whole-book build with one ceremony. Loses the ability to refresh one part without the other, if that distinction turns out to matter to users |
| **B. Keep both, give the arrow equal ceremony** | Preserves any real difference between them. Means designing status, consent and cost for two adjacent builds that most users will not be able to tell apart |
| **C. Remove the arrow** | Removes an unmetered expensive action behind an unlabeled icon. Requires confirming nothing depends on refreshing that artifact alone |

Whatever the spine says about stage 2 has to account for both, or one of them has to go.

### Q5. What happens to the two free-form prompt surfaces?

One is in the chapter tabs and can see only the passage. One is on the book dashboard and can see the
whole book. Different names, different places, mutually unaware.

| Option | Tradeoff |
|---|---|
| **A. Keep both, cross-link them, state the scope difference at the point of use** | Cheapest, preserves both capabilities. Two names for what a user perceives as one feature persists |
| **B. Merge into one prompt surface with an explicit scope selector (this passage / this chapter / whole book)** | One feature, one mental model, scope becomes a visible choice instead of a consequence of where you clicked. Most work, and the whole-book path is much more expensive to run, so the selector must carry a cost signal |
| **C. Keep both but rename so scope is in the name** | Cheap and clearer. Does not solve discoverability: the user still has to already know the other one exists |

### Q6. Where does the style baseline live?

It is the only confirmed mis-scoped build (MOVE-1 and MOVE-2). It also has no guide coverage and no
user-facing explanation of what it is for.

| Option | Tradeoff |
|---|---|
| **A. Onto the book dashboard beside the other whole-book builds** | Consistent with the two builds that already have status, consent and estimate. Adds a third build row to a dashboard that Q4 may already be consolidating |
| **B. A distinct book-materials area that the spine points to from stage 4** | Reflects the truth that it is book-level input to a chapter-level pass. A new area is a new concept for the user to learn |
| **C. Inside whichever guided step Q10 produces** | Ties it to the moment it is needed. Buries a rebuildable artifact inside a flow the user may only see once |

Also decide where the existing inline pointer to the baseline (which sits in one pass's results and
is legitimate) should point after the move.

### Q7. Move "Save as template", or remove it?

It writes an installation-global template from a single chapter's screen, and no screen in the
product ever shows the saved templates.

| Option | Tradeoff |
|---|---|
| **A. Remove it** | Deletes a feature that today can only disappoint. Loses any saved templates users already made |
| **B. Keep it and build the library screen it needs** | Makes a half-built feature whole and reusable prompts are genuinely useful. Real scope, and it needs a scope decision of its own: per book or per installation |
| **C. Keep it, scope templates to the book, defer the library** | Half a fix, and it keeps the "saved where I can never find it" problem |

Moving it before deciding whether the library exists would touch the same files twice, so this must
be answered, not deferred.

### Q8. Does the "whole-book concerns leave the chapter tabs" rule have a converse?

A per-chapter editing card (editing each chapter's brief by hand) lives inside the whole-book tab
set. It has a defensible reason: those briefs are the input to the book-level build.

| Option | Tradeoff |
|---|---|
| **A. Symmetric rule: chapter surfaces leave the book tabs too, move it** | One rule, easy to explain and to police. Separates the editing action from the build it feeds, which is where it makes sense |
| **B. Asymmetric: a chapter surface may live in the book tabs when it is an input to a book-level build** | Keeps a genuinely useful adjacency. The rule now has an exception, and exceptions are how the current mess accumulated |
| **C. Reframe it as "the inputs to this build", visibly part of stage 2 rather than a chapter surface** | Resolves the tension by renaming the concept instead of moving the box. Requires the copy to carry the whole explanation |

This is the case that tests whatever rule the session adopts, so decide the rule here and not later.

### Q9. How is the Summarize name collision resolved?

The book-level name is already settled as **Book briefs / תקצירי ספר** (2.5). The remaining question
is the per-chapter pass.

| Option | Tradeoff |
|---|---|
| **A. Rename the chapter pass so it cannot read as the book summary** | Kills the collision at the source. Changes a label users may recognize, and needs a Hebrew name that does not collide either, which is the harder half |
| **B. Keep the name, add an on-surface statement of what it is and is not** | No relearning. Relies on users reading a note, and the collision is exactly the kind that survives notes |
| **C. Both** | Most likely to work. Two changes to specify and translate |

Whatever is chosen must be stated **on the surface**, not only in a guide. The guides already try
explaining it in three places and the confusion survived.

### Q10. What shape do the guided steps take, and which flows get them?

The owner named the summarize and analyze-book flows as candidates. "Candidate" is not "decided".

| Option | Tradeoff |
|---|---|
| **A. A guided sequence that owns stage 2 into stage 3** | Directly addresses the one hard dependency, which is the product's most common wasted action. A wizard over expensive AI runs must handle "come back in ten minutes" gracefully, so it cannot be a modal that blocks |
| **B. Progressive disclosure on the existing build rows: each row explains itself, states its prerequisite and offers the next action** | Much less new surface, works for repeat users as well as first-timers, and reuses rows that already implement the right states. Less of a felt "guide me" experience |
| **C. A first-run-only guided path that stops appearing once the user has been through it** | Best first impression. Worst for the user who returns after three weeks, and undiscoverable when they want it back |
| **D. B as the permanent mechanism, plus a first-run overlay that points at it** | Covers both audiences. Two things to design and keep consistent |

Constraint on all options: **the guided steps trigger long-running AI builds** (minutes, not seconds).
Any flow that assumes the user waits in place will fail in practice. Whatever is chosen must survive
the user navigating away and coming back, which is also why the activity list matters.

#### Update, 2026-08-12: the tutoring prototype is REAL, and it changes the shape of this question

**Read this before the next design pass touches orientation.** Q10-D and Q13-A were decided when the
only orientation content that could exist was guide prose - the same words for every author, whatever
state their book was in. That is no longer the only option. **Chatbot phase B is built and has passed its
gate, and it answers process-state questions from the book's own real build status**, not from generic
copy. Decide WITH the prototype, not around it: it exists, it can be run, and its behaviour is measured.

**What B actually does that is relevant here, with the measurement rather than the claim.** The three
status DTOs the Wave 3 spine reads (`BookSummaryStatus`, `BookReviewStatus`, `BookStyleBaselineStatus`)
are always in the assistant's prompt when a book is open, and they are the ONE thing the budget trimmer
may never drop - so "what should I run next", "why is my review out of date" and "can I export yet" are
answered from this book's real state, including the `behind` state this brief calls the state that
matters most.

- **36 of 36 runs across the seeded status permutations (fresh, behind-by-N, absent, review-blocked)
  asserted no wrong status**, and 33 of 36 also named the right next action. A wrong status counts as
  fabrication under that gate's own rule, so this is a hard number, not an impression.
- The tutoring floor held under real budget pressure: on a 40-chapter book at 99.8% of the context
  budget, the trimmer gave up four low-ranked review findings and kept **all three status blocks**.
- The assistant speaks the reconciled five-stage vocabulary this brief defines rather than a third stage
  model. Be precise about what was checked: that cross-check was run against the product-question
  assistant in 2026-08-06 and PASSED, and it has NOT been re-run against the book-aware prompt. If an
  option below puts the assistant's words on the orientation surface, re-run it.

**What B does NOT do, so no option below can quietly assume it.** No cross-book or series answers. No
server-side conversation history (the client holds the transcript and resends it; close the drawer and
the thread is the client's problem). No token or quota display. No personalization. And it is a MODEL
call: **with no streaming at all** - the citation is part of the contract and is only known once the
answer is complete, so the user sees nothing until the whole answer lands. Order of magnitude for the
wait, derived from the gate's own totals rather than timed directly: 282 answers consumed 40.3 GPU
minutes on the dev machine, which averages roughly 8 to 9 seconds each. Treat that as a floor for
planning, not a target: it is one local model on one machine, and the longest prompts in that set were
the slowest. A first-run surface that puts a model call on the critical path of someone's first
sixty seconds is making a real bet.

**The three options, and the evidence each one would need before it is chosen.**

| Option | What it means | Tradeoff | Evidence it needs first |
|---|---|---|---|
| **1. DEFER.** The orientation surface stops carrying tutoring content and points at the assistant | One tutoring voice, always current with the book's real state, no second copy to maintain. The orientation panel shrinks to an introduction plus a way in | Puts a model call, its latency and its residual defects on the first-run path, in a product whose primary language is Hebrew. Also couples first-run orientation to the assistant being reachable at all | A first-run latency budget (is 8 to 9 seconds acceptable with no streaming?), a decision on what the panel shows when the model is unreachable, and a Hebrew native reading of the assistant's answers - which does not exist yet |
| **2. EMBED.** The orientation surface asks the assistant on the user's behalf and renders the answer inline | The strongest version of "the product explains itself": the first thing a new author reads is about THEIR book. Reuses one grounded answer path instead of authoring a second body of copy | Every failure mode of the assistant becomes a failure mode of first-run orientation, and the surface has to render a fail-safe, a latency state and a citation chip set that a first-time user has no context for. It also spends a GPU call before the user has asked for anything | A rendered prototype at the product's narrowest supported width in Hebrew, the fail-safe copy actually rendered (it never has been), and a decision on whether a first-run user should be spending model time at all |
| **3. STAY STATIC-WITH-LINKS.** The overlay keeps its guide-driven content and simply links to the assistant | Zero new risk, and it is what already ships on `client-wave3-orientation`. Two tutoring voices coexist, which is honest: guides explain the MECHANISM, the assistant explains THIS BOOK | The generic-copy problem Q13 was trying to solve stays half-solved: the panel can say what a stage is, never where this author actually stands | Nothing new. This is the do-nothing option and it is defensible; it should be chosen deliberately rather than by default |

**Two facts that should move the decision.** First, the assistant is the only surface that can say
"you are behind by three chapters, rebuild before trusting this", and that is precisely the state this
brief's section 1 identifies as the one the old design could not express. Second, B is **gate-passed and
MERGED**, as `Pagedraft.Api#58` + `pagedraft-client#41`, off branches `api-chatbot-phase-b` /
`client-chatbot-phase-b` - so option 1 or 2 no longer takes a dependency on a merge that has not
happened. (This paragraph read "gate-passed but uncommitted ... confirm it is on master before building
on it" until 2026-08-14, which would have discouraged an option that is in fact available.)

**Known rough edges, stated so an option is not chosen on a flattering summary.** The assistant's gate
(`g2`) returned no blocking defect and explicitly did not return a clean bill of health. Relevant to a
first-run surface: it sometimes states specific chapter numbers the status block never gave it (a count
is not a list), and that failure gets WORSE as a book gets longer; an internal `[EXCERPT]` label
occasionally leaks into Hebrew prose; and none of its Hebrew has been read by a native speaker. Full
rates: `PAGEDRAFT_DESIGN.md` §2.8.1 and the gate section named there.

### Q11. Where does the per-task tier control belong?

It sits on a per-chapter screen, its value applies to the whole book, it collapses several pass types
onto one shared setting, and it disappears entirely for two of the six passes.

| Option | Tradeoff |
|---|---|
| **A. Leave it at the point of use** | Decision at the moment it matters. Preserves the surprise that a change made in one chapter applies to all of them |
| **B. Move it to a book-level settings surface** | Matches its real blast radius. Removes it from the moment the user is deciding whether to spend on this run |
| **C. Book-level as the source of truth, with a read-only indicator plus a link at the point of use** | Honest in both places. Two surfaces to keep in sync, and one more control on an already busy run screen |

Constraint on all options: whatever is shown may name tiers and their speed-versus-depth character
and must never name an engine (5.6). The behavior where the control vanishes for two pass types needs
an answer too: explain the absence, or show a disabled state with a reason.

### Q12. Two smaller calls that still need an answer

- **The scope label that contradicts its own subtitle** (says "This chapter" while the adjacent text
  says "scene"). Options: make the label scene-aware, drop the subtitle, or replace both with one
  scope statement. This is on the surface whose only job is to state scope, so it should not be
  carried forward as-is.
- **Where book-level "running" state is shown when the book dashboard is not on screen.** Today it is
  a dot on two unrelated controls. Options: keep it on chrome, move it into the new spine, or defer
  entirely to the activity list. Depends on Q1.

### Q13. How does first-run orientation reach the guides?

The corpus exists, is structured and is already constraint-compliant, but nothing serves it to the
app yet (section 7).

| Option | Tradeoff |
|---|---|
| **A. Build the serving path this wave, and drive orientation from `stage` and `id`** | Orientation is real content from day one, and the chatbot inherits the same path. Adds a backend and client dependency to this wave |
| **B. Ship the spine with stage names and short state copy only, no tutorial prose, and add guide-driven content when the serving path lands** | Smallest safe step, and the short state copy is not throwaway because the spine needs it regardless. First-run orientation is thinner than the owner asked for, for one wave |
| **C. Hardcode tutorial copy now** | Fastest to a demo. **Explicitly recommended against:** it is the throwaway path, and it is how a third contradictory stage model gets born |

Either A or B is defensible. C should be ruled out at the session rather than drifted into.

**Update, 2026-08-11: option A's dependency is now built, not merely planned.** Chatbot phase A.2
(`chatbot-phase-a2-show-2026-08-09`, gate `g5` PASS) built the general guides serving path as
infrastructure for its own citation chips: `GET /api/guides?language=` (list) and
`GET /api/guides/{id}?language=` (single guide body), plus a client reader mounted at `/help`
(index, grouped by stage) and `/help/:guideId` (single guide, with a `?lang=he|en` toggle). Wave 3
does not need to build this path; it needs to consume it. It was built on API `api-chatbot-a2-guides`
and client `client-chatbot-a2-show` and is on master since 2026-08-11 (`Pagedraft.Api#56` +
`pagedraft-client#39`), so the "check both are on master first" caveat this line used to carry is
discharged. Corrected 2026-08-14.

**Update, 2026-08-12: guide prose is no longer the only content orientation could be driven from.**
Chatbot phase B is built and gate-passed, and it answers the same "where am I / what do I run next"
questions from THIS book's real build status rather than from generic copy, measured 36 of 36 with no
wrong status. That does not reverse Q13-A - the guides are still the right source for explaining the
MECHANISM - but it means the surface now has two possible content sources rather than one, and the
choice between them is the sub-decision written up under **Q10 above**. Read that before designing the
orientation panel's content.

---

## Appendix A. One-page summary to hold in your head

1. The app and its own documentation describe **two different workflows**. That is the diagnosis.
2. The replacement is **five stages**: Import, Book briefs, Developmental review, Chapter editing
   passes, Export.
3. **One state vocabulary** across all five: blocked, not-started, running, behind, ready,
   unavailable. **`behind` is the state that matters most and the current design cannot say it.**
4. **One hard dependency exists** (review needs briefs) and it must be visible.
5. **Nothing may be hardcoded as done.** Stage 4 cannot be evaluated book-wide today and Export has
   no screen, so both must be presented honestly.
6. The reorganization is **three items**, not a tab rewrite. The per-chapter tabs are mostly right.
7. **Hebrew right-to-left is the default**, some elements mirror and some must not, and two different
   language rules apply in two different parts of the app.
8. **Never name a model or a vendor**, anywhere.
9. **Do not restyle the document editor.** Design around it.
10. First-run orientation is a **view over the shipped guides**, not new tutorial copy.

## Appendix B. For engineers: where the evidence lives

This brief lifts its conclusions from two completed audit sections. Both cite specific files and line
numbers and were verified by reading the code, not by recall.

- **Plan file:** `src/.cursor/plans/_archive/wave3-ia-audit-and-design-brief-2026-08-02.plan.md`
  - `## c1 reconciled stage model` - the canonical stage list with per-stage signal derivation, the
    eight conflicts between the two models and which wins in each, the verdict on `Polish`, and the
    five missing signals **M1** through **M5** with a proposed source for each. Sections 1, 2 and 5
    of this brief come from there.
  - `## c2 surface scope audit` - all 53 surfaces classified book, chapter, scene, chrome or
    ambiguous, in six tables, plus a per-analysis-type scope-versus-concern table, the twelve
    ambiguity write-ups and the three-item move list. Section 3 of this brief comes from there.
- **Guides corpus:** `Pagedraft.Api/Content/guides/` on `origin/master`, 15 files. Not present in
  every branch's working tree.
- **Design constraints in section 5 were re-verified against the client working tree
  (`pagedraft-client`, branch `tier-ux-rework`) while writing this brief:** the `--pd-*` token file
  and its use across 21 stylesheets including the stage strip; the activity bell's
  `inset-inline-start` placement and its documented mirroring; the panel resize gutter's documented
  physical-left pinning; the app-level Hebrew-default language key versus the book-scoped
  `bookLanguage` inputs and direction getters; the global stylesheet's ordering comment stating that
  app styles load after the third-party theme and do not touch its selectors; and the absence of any
  model or vendor name in user-facing strings (the single source hit is a code comment recording the
  removal). **All claims in section 5 hold in current code.** The one qualification is in 5.2: the
  activity list documents that it cannot resolve per-row book language today and falls back to the
  app language.
- **Backend work items implied by this brief:** M1 (chapter count on the books list), M2 (book-level
  rollup of chapter-pass state, the only one that gates a design option, see Q2), M3 (finding
  outcome counts on the review status payload, low severity), M4 (an export surface, see Q3), and a
  guides serving path (see Q13). M5 was ruled out deliberately: the product should not invent a "did
  you check the chapter split?" flag it cannot verify.
