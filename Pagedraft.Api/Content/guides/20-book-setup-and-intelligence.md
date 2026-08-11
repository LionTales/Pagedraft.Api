---
id: book-setup-and-intelligence
stage: book-intelligence
audience: author
updated: 2026-08-11
lang: en
---

# What PageDraft knows about your book

Some of what PageDraft produces belongs to a single chapter, and some of it belongs to the whole
book. This guide covers the book-level material: what it is, what it needs, and what makes it go out
of date.

## The book briefs

The book briefs are built in layers. For every chapter, PageDraft derives a short structured brief.
Those per-chapter briefs are then composed into one brief for the whole book.

They need chapters with saved text, and nothing more. They do not need any editing pass to have run
first, and they do not need the Story Bible. You can build them immediately after import.

The build is incremental. A chapter whose brief is already current is skipped, so a rebuild after
editing two chapters re-derives those two and leaves the rest alone.

Two things make the book briefs worth building early:

- The developmental review reads the chapter briefs and will not run without them.
- The Literary pass on a chapter reads the whole-book brief when it exists, so it has more to work
  with once the briefs are built.

## When the book briefs count as ready

Ready means all three of these at once:

- every chapter's brief is current,
- every chapter is covered,
- they were built under the model that is active now.

If any of the three is false, the dashboard shows it as behind rather than missing, and it reports
how many chapters have changed, which is how much a rebuild would re-derive.

## Editing a chapter summary by hand

You can edit a chapter's summary text yourself. Doing so has one consequence worth knowing in
advance: from that point on, PageDraft stops re-deriving that chapter's summary text automatically,
even if you keep editing the chapter. Your wording is treated as deliberate and is not overwritten
behind your back. Re-deriving it becomes something you ask for explicitly, and when you do, your
wording is what the new version starts from.

## The book profile

Separately from the briefs, PageDraft keeps a profile of the book as a whole: its genre and
sub-genre, its intended audience, a synopsis, the characters it found, and the shape of the plot.

It is built from your chapter summaries, so those need to exist first. Refreshing it summarizes any
chapter whose summary is out of date and then rebuilds the profile from those summaries.

It is not a prerequisite for the book briefs or for the developmental review. Both of those run
whether or not the profile has ever been built, and simply use its details when they are there.

One practical difference from the rest of the product: the profile itself has no freshness check.
Every refresh rebuilds all of it from scratch, whether or not anything changed. It is the one place
where asking again always costs a full rebuild, so refresh it when you have actually changed
something.

## Your book's writing style

This is the third book-level build, beside the book briefs and the developmental review, and it sits
with them on the book dashboard. It is a measurement of how this book usually reads: sentence
length, vocabulary range, dialogue density and the other numbers a Linguistic pass compares one
chapter against.

It exists for one consumer. The Linguistic pass on a chapter reports deviations, and a deviation is
only meaningful against a baseline, so without this measurement that pass can report what it found
in the chapter but cannot say the chapter drifts from the rest of the book. When it is missing or
out of date, the Linguistic result says so and points at the row that builds it.

Like the other book-level builds it needs chapters with saved text, and nothing else. It is a
whole-book run, so it asks for consent and shows an estimate before it starts, and if the book is on
the thinking tier it also says that the chapter text leaves this machine.

It goes out of date the same way everything else book-level does. Editing chapters after it was
measured leaves it behind, and the row reports how many chapters have changed; building it under a
different tier records that too, and the row asks to be refreshed. Nothing rebuilds it for you.

The name is the same everywhere: the dashboard row, the activity list entry and the pointer in the
Linguistic result all call it your book's writing style. It used to be reachable only from inside
one chapter's analysis screen, which is why an older habit of looking for it there is worth
unlearning.

## What the Story Bible is

The view named Story Bible is not the profile above. It is a second way of reading the developmental
review: the same findings arranged as characters, story threads and a timeline instead of as a flat
list. It sits beside the findings and appears only once a review has been built.

## Asking questions about the book

You can ask a question about the book and get an answer written against your own manuscript rather
than against general knowledge. The answer is grounded in your chapter summaries together with the
book brief and what is known about the characters.

Because it reads the chapter summaries, it cannot be answered until they exist. Produce chapter
summaries first.

## Book-level context that chapter passes use

Some per-chapter passes read book-level material when it is present, and run without it when it is
not. Nothing is blocked by its absence; the passes just have less context. This is why the same
pass, run on the same chapter, can produce a different result before and after you build the book
briefs.

## What makes book-level work go out of date

- **Editing a chapter** makes that chapter's brief out of date. The next briefs build re-derives
  just that chapter.
- **Rebuilding the book briefs after a developmental review** makes that review out of date, because
  the review was built from the older briefs.
- **Changing a tier** changes which model is active. Book-level material records which model built
  it, so a tier change is treated the same way as a content change and asks to be rebuilt.
- **Your book's writing style** follows the same two rules: edited chapters leave the measurement
  behind, and a tier change asks it to be refreshed.

None of this rebuilds itself. PageDraft marks the state and waits for you.
