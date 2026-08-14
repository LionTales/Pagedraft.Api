---
id: faq
stage: faq
audience: author
updated: 2026-08-13
lang: en
---

# Questions the work raises

## How do I run a pass on a chapter?

With the chapter open in the editor, use the Assistant panel: stay on Edit help, choose the Analysis
view, pick the pass you want from the row of five, and press Run analysis. The pass runs on the scene
you have selected, or on the whole chapter when none is selected, so there is no separate scope
control to set. The chapter editing passes guide has the full sequence, including the confirmation
you get when a previous run left pending suggestions, and why a run cannot be canceled once started.

## What do "fast" and "thinking" mean?

Fast uses a smaller model: quicker, and it uses fewer tokens. Thinking uses a larger model: it goes
deeper, and it costs more tokens.

It helps to read the choice as a cost decision rather than a quality dial. Thinking is opt-in, and
in a setup where the fast tier runs on your own machine, choosing thinking means the chapter text is
processed by an outside provider, so it leaves that machine. In that case the switch asks you to
confirm rather than simply flipping. Where both tiers already run off your machine, there is nothing
extra to confirm and the switch commits directly.

## Which passes can actually use the thinking tier?

Proofread and Linguistic can. Line Edit and the developmental review show the control but always run
on the fast tier, and they say so instead of leaving you guessing. Chapter recap has no tier control
at all.

There is also a language rule that outranks your choice: for an English book, proofreading always
runs on the fast tier. That applies to proofreading only, not to Linguistic.

## It says the setting is thinking, but the run was fast

That is the product being honest rather than a bug. The setting is what you asked for; the tier
shown for a pass is what will actually run. When the two differ, you get a line saying so, and where
there is a reason it is stated with it. Common reasons are that the pass is not eligible for the thinking tier, that the book's
language forces fast, or that the thinking route is not available on this server.

## Why can I not select "thinking" at all here?

Because the server would refuse the change, and offering the option would be an invitation to a
refusal. The reason is stated on the control itself: the pass is not eligible, the book language
forces fast, the route is not enabled here, or there is no access configured for it.

## Why are my briefs or review marked out of date?

Something they were built from moved. Specifically:

- A chapter brief is out of date once you edit that chapter.
- The book briefs are ready only when every chapter's brief is current, every chapter is covered,
  and they were built under the model that is active now.
- The developmental review is ready only when the briefs exist, the review exists, it was built
  under the currently active model, and it is not older than the briefs.

Nothing is rebuilt for you in the background. PageDraft marks the state and waits, and "not built"
and "out of date" are shown as different states so you can tell which one you have.

## Why did changing a tier mark things out of date?

Because book-level material records which model produced it, and a tier change changes which model
is active. Results built under one model and results built under another are not interchangeable, so
a tier change is treated the same way as editing a chapter: the affected material is flagged for
rebuilding rather than quietly kept.

## Why do two runs of the same analysis give different answers?

Partly for reasons you can see, and partly because the engine does not guarantee an identical answer
twice.

The visible reasons are worth checking first, because they are usually the real explanation:

- **The tier changed.** A different tier means a different model, and a different model writes a
  different report.
- **The available context changed.** Several passes read book-level material when it exists and run
  without it when it does not. The same pass on the same chapter has more to work with after the
  book briefs are built than before.
- **The scope changed.** Linguistic compares a scene against the rest of its chapter, and a chapter
  against the average of the book's other chapters, so a scene run and a chapter run are answering
  different questions about the same words.
- **The text changed.** Passes read what is saved. An intervening save, including one made by
  accepting a suggestion, changes the input.

Beyond that, expect some variation between two runs with everything held equal. This matters most
for reports that you read, such as Linguistic and Literary. It matters less for Proofread and Line
Edit, where you see every proposed change individually and nothing is applied without you.

## Which analyses change my text, and which only report?

**Change text, through suggestions you approve one at a time:** Proofread and Line Edit.

**Report only, with nothing to accept:** Linguistic (measurements and flags that navigate you to the
passage), Literary (a critique of the prose), Chapter recap (a recap of the chapter), and the
developmental review (findings with a status you set).

**Custom** used to run your own instruction against the text. It has been retired; ask Show instead,
and see the chapter editing passes guide for what changed with the swap. Custom results you already
have are still in the History tab.

In no case is your manuscript rewritten without you. Even the passes that produce edits produce them
as proposals.

## What is the difference between running Summarize, now the Chapter recap, on a chapter and building the book briefs?

The chapter recap produces a recap of that one chapter that you read like any other result. It is
the pass that used to be called Summarize; only the name changed. The book briefs are a separate
build that derives a structured brief for every chapter and composes them into one brief for the
whole book, and that structure is what the developmental review reads.

Running the chapter recap on every chapter in turn does not add up to the book briefs.

## The developmental review would not run

It needs the chapter briefs, so if no usable briefs exist it stops before doing any work and tells
you to build the book briefs first. Build the briefs, then run the review.

If you have edited chapters since the briefs were built, rebuild them first, otherwise the review
will be reading the book as it used to be.

## Why does a long Hebrew chapter take longer, or get split into more pieces?

Proofread and Line Edit split long chapters into pieces and work through them, and the pieces are
smaller for Hebrew and Arabic than for text in Latin script. More pieces means more passes over the
same chapter. There is nothing to configure; running a single scene instead of a whole chapter is
the usual way to get a quicker answer.

## I edited a chapter's summary myself and now it does not update

That is deliberate. Once you have written a chapter summary yourself, PageDraft stops re-deriving
that summary text automatically, even when the chapter changes again, so your wording is not
overwritten behind your back. Re-deriving it becomes an explicit action, and when you ask for it,
your wording is what the new version starts from.

## Why do Linguistic and Literary share one tier setting?

They are two different reports, but they run through the same underlying task, so the tier you set
for one is the tier the other uses.

## Import put my whole manuscript into one chapter

The split looks for Heading 1 paragraphs and for Hebrew section markers on a short standalone line.
If a document has neither, everything is imported as a single chapter, which is the deliberate
fallback. Applying Heading 1 to your chapter titles in the word processor and importing again is
usually the quickest fix.

## Import made too many chapters, some starting mid-sentence

A short line in your manuscript begins with a word that reads as a section marker. Fix it in the
import preview, which is a form and has not changed your book yet, or reword the line in the source
document and import again.

## Does anything I do here send my book somewhere?

The fast tier and the thinking tier are not the same in this respect. Where the fast tier runs
locally, choosing the thinking tier means the chapter text is processed by an outside provider and
leaves that machine. Because a manuscript is unpublished work, that choice is explicit opt-in only:
it is never the default, and in that setup PageDraft asks you to confirm it before switching.
