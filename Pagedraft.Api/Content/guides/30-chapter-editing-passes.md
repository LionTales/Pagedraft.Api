---
id: chapter-editing-passes
stage: chapter-editing
audience: author
updated: 2026-08-11
lang: en
---

# The chapter editing passes

Six passes run against one chapter, or against one scene inside a chapter: Proofread, Line Edit,
Linguistic, Literary, Chapter recap and Custom. They all need the same one thing, saved chapter text,
and they differ in what they read around it and in what they give you back.

Chapter recap was called Summarize until this release. Only the name changed; the pass does the same
work, and older results in the History tab are the same pass.

## They read what is saved

Every pass analyzes the text as it is stored, not the text as it currently sits in the editor.
Starting a pass from the editor saves the open chapter or scene first, so in ordinary use the stored
text and the text in front of you are the same thing. A chapter with no saved text at all stops the
run and asks you to save the chapter first.

## How to run a pass

There is no separate screen for this. A pass is started from the Assistant panel of the editor, and
the sequence is short:

1. Open the book in the editor and select the chapter. Select a scene under it as well if you want
   the pass to run on that scene rather than on the whole chapter.
2. In the Assistant panel, stay on **Edit help** rather than Book review, and on the **Analysis**
   view rather than Language. If the panel has been closed, the Assistant button brings it back.
3. Pick the pass from the analysis picker, which is a row of six buttons carrying the names above.
   That pick is the whole choice of what will run.
4. Press **Run analysis**.

The button is unavailable until a chapter is open, and for Custom until you have written an
instruction in the box that appears with it. While a pass is running the button reads "Running" and
will not start a second run on the same chapter or scene, though you can move to a different chapter
and start one there.

Two things happen between the press and the run:

- **Anything unsaved is saved first.** You do not have to press Save yourself. The run saves the
  open chapter or scene and only then sends it.
- **Pending suggestions are ended, and you are asked first.** If an earlier Proofread or Line Edit
  on this chapter or scene left suggestions you neither accepted nor dismissed, PageDraft counts
  them and asks you to confirm, because a new run ends that session and takes those suggestions with
  it. Declining leaves everything as it was and starts nothing.

## While a pass is running

A progress card opens over the page as soon as you press Run. Until the server has answered at all
it holds the rest of the app, which is worth knowing rather than being surprised by; if the server
never answers, the card gives up on its own and says the run did not start, instead of waiting
forever.

There is no cancel. Nothing stops a pass once it has started, and closing the card does not stop it
either, so the card's dismiss button means "stop watching" and never "stop running".

What closing does depends on how far the run got:

- Once the run is long enough to be tracked as a background job, the button becomes Minimize. The
  run carries on, its progress moves to the Activity Center, and the analysis panel keeps a small
  progress line of its own.
- Before that point the card offers only Close, and the run is still held by the editor page rather
  than by a background job. It survives closing the card, but not leaving the editor.
- Once the run finishes or fails, the card stops holding the page immediately and remains only as a
  notice until you dismiss it.

The result lands on the panel's Run tab. History keeps the earlier results for the same chapter, and
Versions keeps the saved states of the text itself.

## Proofread

Produces suggestions you accept or dismiss one at a time. This is the pass that changes your text,
and it changes it only through your approval of each suggestion.

For a Hebrew book, PageDraft normally also runs a deterministic full-spelling check alongside the
pass and merges its results into the same list of suggestions, so those spelling conventions are
handled by a rule rather than left to judgement.

Long chapters are split into pieces and proofread piece by piece. The pieces are smaller for Hebrew
and Arabic than for languages in Latin script, because the same number of words carries different
weight. You do not have to do anything about this; it is worth knowing only because it explains why
a long chapter takes noticeably longer than a short one.

## Line Edit

Also produces suggestions you accept or dismiss. Where proofreading works at the level of
correctness, this pass works at the level of the sentence, and it reads the paragraphs immediately
before and after the passage it is editing so that its suggestions fit their surroundings.

## Linguistic

Produces a report, not suggestions. You get measurements of the writing plus flagged places where it
departs from the baseline it is being compared against. The flags take you to the passage; they do
not offer a replacement, and there is nothing to accept.

What it compares against depends on the scope you run it at:

- On a **scene**, it compares against the rest of that chapter.
- On a **chapter**, it compares against the average across your book's other chapters.

That is the same reason a scene run and a chapter run of Linguistic can disagree about the same
sentence: they are answering different questions.

## Literary

Produces a critique of the prose. There is nothing to accept or dismiss; you read it and decide what
to do.

Of all the chapter passes, this is the one that gains most from book-level work. It reads the
chapter's own brief and the whole-book brief when they exist, so running it after the book briefs
are built gives it a view of where the chapter sits in the book rather than only what is on the page.

## Chapter recap (previously Summarize)

Produces a recap of that one chapter, which you read like any other result. It summarizes the
chapter for you to read; it does not feed the book briefs.

This is not the same thing as building the book briefs. The book briefs are a separate build that
produces the structured briefs the developmental review consumes. Running the chapter recap on every
chapter in turn does not produce the book briefs.

## Custom

Runs your own instruction against the chapter or scene text. What you get back depends entirely on
what you asked for.

One property is worth knowing before you write the prompt: Custom is given the raw text and nothing
else. No book briefs, no character information, no style context is loaded for it. If your
instruction depends on something outside the passage, put that something in the prompt.

## Chapter or scene

You do not pick a scope separately. A pass runs against the scene you have selected, or against the
whole chapter when no scene is selected. Which one you use is a real choice, not just a size
setting:

- A scene run gives the pass less to hold at once, which helps on long chapters.
- Linguistic changes what it compares against: a scene against the rest of its chapter, a chapter
  against the average of the book's other chapters.
- Line Edit reads the surrounding paragraphs at either scope, so a scene run is not cut off from its
  context.

## Fast and thinking on these passes

The tier is set per pass rather than once for the whole book, so two passes on the same book can sit
on different tiers. There is also a book-level setting, which is the default for passes you have not
decided on yet; it does not override a choice you have already made for a particular pass.

The control lives on the panel's Run tab and follows whichever pass is currently picked, so you
choose the pass first and the tier second. It is a saved setting rather than something you decide
per run: picking a tier stores it and the next run uses it.

Not every pass can actually move to the thinking tier. Proofread and Linguistic can. Line Edit shows
the control but always runs on the fast tier, and it says so rather than pretending otherwise.
Chapter recap and Custom have no tier control at all. Because Literary is routed through the same
underlying task as Linguistic, the two share one setting.

There is one language rule that overrides the tier choice: for an English book, proofreading always
runs on the fast tier. That restriction applies to proofreading only, not to the Linguistic pass.

## Hebrew books

Two behaviours are specific to Hebrew:

- The deterministic full-spelling check merged into proofreading suggestions.
- Long chapters are split into smaller analysis pieces than the same length of text in Latin script,
  so a long Hebrew chapter takes more passes to get through.
