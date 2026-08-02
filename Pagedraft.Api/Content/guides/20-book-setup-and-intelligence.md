---
id: book-setup-and-intelligence
stage: book-intelligence
audience: author
updated: 2026-08-02
lang: en
---

# What PageDraft knows about your book

Some of what PageDraft produces belongs to a single chapter, and some of it belongs to the whole
book. This guide covers the book-level material: what it is, what it needs, and what makes it go out
of date.

## The book summary

The book summary is built in layers. For every chapter, PageDraft derives a short structured brief.
Those per-chapter briefs are then composed into one brief for the whole book.

It needs chapters with saved text, and nothing more. It does not need any editing pass to have run
first, and it does not need the Story Bible. You can build it immediately after import.

The build is incremental. A chapter whose brief is already current is skipped, so a rebuild after
editing two chapters re-derives those two and leaves the rest alone.

Two things make the book summary worth building early:

- The whole-book review reads the chapter briefs and will not run without them.
- The Literary pass on a chapter reads the whole-book brief when it exists, so it has more to work
  with once the summary is built.

## When the book summary counts as ready

Ready means all three of these at once:

- every chapter's brief is current,
- every chapter is covered,
- the summary was built under the model that is active now.

If any of the three is false, the dashboard shows it as behind rather than missing, and it reports
how many chapters have changed, which is how much a rebuild would re-derive.

## Editing a chapter summary by hand

You can edit a chapter's summary text yourself. Doing so has one consequence worth knowing in
advance: from that point on, PageDraft stops re-deriving that chapter's summary text automatically,
even if you keep editing the chapter. Your wording is treated as deliberate and is not overwritten
behind your back. Re-deriving it becomes something you ask for explicitly, and when you do, your
wording is what the new version starts from.

## The book profile

Separately from the summary, PageDraft keeps a profile of the book as a whole: its genre and
sub-genre, its intended audience, a synopsis, the characters it found, and the shape of the plot.

It is built from your chapter summaries, so those need to exist first. Refreshing it summarizes any
chapter whose summary is out of date and then rebuilds the profile from those summaries.

It is not a prerequisite for the book summary or for the whole-book review. Both of those run
whether or not the profile has ever been built, and simply use its details when they are there.

One practical difference from the rest of the product: the profile itself has no freshness check.
Every refresh rebuilds all of it from scratch, whether or not anything changed. It is the one place
where asking again always costs a full rebuild, so refresh it when you have actually changed
something.

## What the Story Bible is

The view named Story Bible is not the profile above. It is a second way of reading the whole-book
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
summary.

## What makes book-level work go out of date

- **Editing a chapter** makes that chapter's brief out of date. The next summary build re-derives
  just that chapter.
- **Rebuilding the book summary after a whole-book review** makes that review out of date, because
  the review was built from the older briefs.
- **Changing a tier** changes which model is active. Book-level material records which model built
  it, so a tier change is treated the same way as a content change and asks to be rebuilt.

None of this rebuilds itself. PageDraft marks the state and waits for you.
