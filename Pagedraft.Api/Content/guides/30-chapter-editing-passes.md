---
id: chapter-editing-passes
stage: chapter-editing
audience: author
updated: 2026-08-02
lang: en
---

# The chapter editing passes

Six passes run against one chapter, or against one scene inside a chapter: Proofread, Line Edit,
Linguistic, Literary, Summarize and Custom. They all need the same one thing, saved chapter text,
and they differ in what they read around it and in what they give you back.

## They read what is saved

Every pass analyzes the text as it is stored, not the text as it currently sits in the editor. Save
before you run, or you will get an analysis of the previous version. A chapter with no saved text
stops the run and asks you to save the chapter first.

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
chapter's own brief and the whole-book brief when they exist, so running it after the book summary
is built gives it a view of where the chapter sits in the book rather than only what is on the page.

## Summarize

Produces a summary of that one chapter, which you read like any other result.

This is not the same thing as building the book summary. The book summary is a separate build that
produces the structured briefs the whole-book review consumes. Running Summarize on every chapter in
turn does not produce a book summary.

## Custom

Runs your own instruction against the chapter or scene text. What you get back depends entirely on
what you asked for.

One property is worth knowing before you write the prompt: Custom is given the raw text and nothing
else. No book summary, no character information, no style context is loaded for it. If your
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

Not every pass can actually move to the thinking tier. Proofread and Linguistic can. Line Edit shows
the control but always runs on the fast tier, and it says so rather than pretending otherwise.
Summarize and Custom have no tier control at all. Because Literary is routed through the same
underlying task as Linguistic, the two share one setting.

There is one language rule that overrides the tier choice: for an English book, proofreading always
runs on the fast tier. That restriction applies to proofreading only, not to the Linguistic pass.

## Hebrew books

Two behaviours are specific to Hebrew:

- The deterministic full-spelling check merged into proofreading suggestions.
- Long chapters are split into smaller analysis pieces than the same length of text in Latin script,
  so a long Hebrew chapter takes more passes to get through.
