---
id: export
stage: export
audience: author
updated: 2026-08-10
lang: en
---

# Exporting your book

Export produces a DOCX file from what is saved in your chapters. Two forms exist: the whole book,
and a single chapter on its own.

## Where to find it

Export has its own screen, at `/books/<your book>/export`. Two ways lead there:

- the Export stage on the workflow spine, from its "Go to export" action;
- the Export button in the book dashboard header.

The screen lists what it can produce. Today that is the whole book and a single chapter, both as
Word files; pick one, press Download, and the file arrives under the name PageDraft chose for it.
There are no formatting options, on purpose: the export gives you your text back, and the shaping of
it belongs in your word processor.

A book with no chapters yet has nothing to put in a file, and the screen says so instead of handing
you an empty document. The workflow spine says the same thing one step earlier: its Export stage
reads as blocked until something has been imported.

The same holds for a book whose chapters are all still empty, and for a single chapter you have not
written in yet. There is nothing to put in the file, so you are told that rather than handed one.

## What it contains

Exactly what is saved in your chapters, and in their scenes where a chapter has been split into
some, converted back to a Word document. The whole-book export puts the chapters together in order
into one file. The single-chapter export gives you that one chapter, in a file named after the
chapter.

Chapters you have not written in yet are not in the file, because there is nothing in them to put
there. When that happens the screen tells you how many were left out and names them, so a chapter is
never quietly missing from a manuscript you exported.

## What it depends on

Only on having chapters saved. No analysis needs to have run, no summary needs to exist, and no
review needs to be current. Export reads the text; it does not consult anything that was produced
about the text.

That also means the reverse: the analysis work does not travel with the file. Suggestions you never
accepted, review findings, chapter briefs and the book profile all stay in PageDraft. What leaves is
the manuscript.

## Which version of your text the export writes

Export writes out what is stored, so anything you have typed but not saved is not in the file.
Pressing Save puts it there, and starting an editing pass saves the open unit first as well.

A chapter you have split into scenes follows the same rule one level down. Once you have written in
any scene of that chapter, the file is built from its scenes, in their order, with nothing inserted
between them. Before that, the chapter's own saved text is what the file carries, which is the fuller
copy: splitting a chapter works on its plain text, so the scenes it produces do not carry the
chapter's formatting or the break marks between them.

Those two are separate stores and PageDraft does not merge them. If you write into a chapter's
scenes and later write into the chapter itself, the export takes the scenes and the later
chapter-level edit is not in the file. Once a chapter is split, treat its scenes as the place you
write in it.

So if a file looks a version behind, there are two things to check: whether the text was saved, and
whether that chapter is split into scenes with the text you are missing in the other one.

## Exporting changes nothing

Export is independent of everything else and produces no side effects. It does not mark anything out
of date and it does not consume any analysis work, so taking a copy at any point costs you nothing.
