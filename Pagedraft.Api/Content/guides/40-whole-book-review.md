---
id: whole-book-review
stage: whole-book-review
audience: author
updated: 2026-08-02
lang: en
---

# The whole-book review

The whole-book review is the developmental pass: it looks at the book as a book, not at the
sentences. It produces findings across six dimensions: plot, character, pacing, tone, theme and
continuity.

This is the only pass that reads the whole book in order to find problems that live between
chapters, such as a thread introduced early and never resolved, or a detail that contradicts an
earlier one. The chapter passes work on one chapter or scene at a time. Some of them do read a
little beyond it, such as a book-level brief or the edge of the neighbouring chapter, but none of
them reads the book, so none of them can find this class of problem.

## It needs the book summary first

The review is built from the chapter briefs. If no usable briefs exist, it stops immediately, tells
you to build the book summary first, and does no work at all. This is a real requirement rather than
advice: there is nothing for the review to read without it.

If you have not built the book summary yet, build it and then run the review. If you built it a
while ago and have edited chapters since, rebuild it first so the review is looking at the current
book.

## How it handles a long book

A whole book does not fit into a single reading. The review works over the book in windows, then
brings the results together, so that findings which span several chapters can still be recognized.
You do not configure any of this.

## Working through the findings

Each finding carries a status you set yourself, so the review doubles as a worklist: you can mark
what you have handled and leave the rest. Nothing is applied to your text by the review. It reports;
you decide and you edit.

Findings are also reachable from the chapter you are editing, so you can work through a chapter's
own findings without leaving it.

## When a review goes out of date

A review counts as ready only when all of these hold:

- the book summary exists,
- the review exists,
- it was built under the model that is active now,
- it is not older than the book summary.

The last one catches people out. Rebuilding the book summary after running a review marks that
review as out of date, because the review was built from the briefs as they were before. If you are
going to rebuild the summary, expect to rebuild the review after it.

The third one matters too: changing a tier changes which model is active, and a review built under a
different model is flagged rather than silently accepted.

## Tier

The whole-book review always runs on the fast tier. The tier control is still shown for it so that
you get a stated reason rather than a control that silently does nothing.

## If the review comes back thin or fails

- **No findings at all.** The build reports the failure and tells you to try again, and that if it
  keeps happening the book may be too large for the model context.
- **Part of the book produced findings and part did not.** The review is saved with what succeeded
  and reports how much of the book it covered and how many parts failed. Building it again retries.
