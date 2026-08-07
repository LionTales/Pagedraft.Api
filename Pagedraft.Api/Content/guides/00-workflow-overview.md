---
id: workflow-overview
stage: overview
audience: author
updated: 2026-08-06
lang: en
---

# How the work flows

PageDraft has five stages. They are not a rigid pipeline, and most of them can be repeated in any
order. Only a few real dependencies exist, and knowing which ones they are saves you from waiting on
work that was never going to produce anything.

## The five stages

1. **Import.** A DOCX manuscript becomes chapters in your book.
2. **Book briefs.** A short structured brief for every chapter, composed into one brief for the
   whole book.
3. **Developmental review.** Findings across six dimensions: plot, character, pacing, tone, theme
   and continuity.
4. **Chapter editing passes.** Proofread, Line Edit, Linguistic, Literary, Summarize and Custom,
   each run on one chapter or one scene at a time.
5. **Export.** Your chapters back out as a DOCX file.

## What actually depends on what

**Everything starts with import.** Every later stage reads the chapter text that is saved in your
book. If a chapter has no saved text, an analysis on it stops and tells you to save the chapter
first. This is also why the passes work on what is saved, not on what is currently on screen but
unsaved.

**The book briefs need chapters with text, and nothing else.** They do not need any editing pass to
have run. You can build them right after import or after a full editing round; both are valid.

**The developmental review needs the book briefs.** This is the one hard dependency in the product.
The review reads the chapter briefs, so if no usable briefs exist it stops before spending any work
and tells you to build the book briefs first. Building them is not a suggestion here, it is the
input the review is made of.

**Chapter editing passes need only saved chapter text.** They do not require the book briefs. Some
of them will use book-level context when it already exists, and they simply run without it when it
does not, so there is nothing to build first. Building book-level material earlier gives those
passes more to work with, but it is not a gate. Starting one is a control in the editor rather than
a stage-level action; the chapter editing passes guide has the steps.

**Export needs only saved chapters.** It reads whatever is currently saved and converts it back to
DOCX. No analysis has to have run, and no analysis result is included in the file. Export is not yet
wired to a control in the editing interface; see the export guide.

## A practical order

If you want one order that never wastes a step:

1. Import the manuscript and check the chapter split.
2. Build the book briefs. They are the input the developmental review reads, and the review will not
   run without them.
3. Run the developmental review and work through the findings, which are about structure: plot,
   character, pacing, tone, theme and continuity.
4. Do the per-chapter passes for language and line-level work.
5. Export.

The reason for putting structural work before line-level work is not a rule the software enforces.
It is that rewriting a chapter after you have proofread it means proofreading it again, while
proofreading after the structural pass does not need repeating.

## What goes stale, and why

PageDraft does not rebuild your book-level results on its own. Instead they are marked as no longer
current, and you decide when to rebuild.

- Editing a chapter makes that chapter's brief out of date. The next briefs build re-derives just
  that chapter and leaves the rest alone.
- The book briefs count as ready only when every chapter's brief is current, every chapter is
  covered, and they were built under the model that is active now.
- The developmental review counts as ready only when the briefs exist, the review exists, it was
  built under the currently active model, and it is not older than the briefs. Rebuilding the briefs
  after a review therefore marks that review as stale.
- Changing a tier changes which model is active, and that is treated exactly like a content change
  for freshness purposes.

The book dashboard tells "not built" apart from built-but-behind, so you can see whether you are
missing something or merely holding an older version. Briefs that have fallen behind report how many
chapters have changed since they were built, and material built under a different model says so
separately.

Two older names for the same things still turn up: the book briefs are sometimes called the book
summary, and the developmental review the whole-book review.

## Two things that are easy to mix up

**Summarize on a chapter is not the book briefs build.** Running Summarize from the chapter analysis
picker produces a summary result for that one chapter that you read like any other analysis result.
The book briefs are a separate build that produces the structured briefs the developmental review
consumes. Running Summarize on every chapter one by one does not add up to the book briefs.

**Linguistic and Literary share one setting.** They are two different reports, but they are routed
through the same underlying task, so the fast or thinking choice you make for one applies to the
other.
