---
id: import
stage: import
audience: author
updated: 2026-08-02
lang: en
---

# Importing your manuscript

Import turns one DOCX file into the chapters of a book. It is the first stage, because every other
stage reads the chapter text that import saves.

## What import accepts

A `.docx` file. Other formats are refused, including `.doc`. You can drop the file onto the import
page or browse for it.

## How the manuscript is split into chapters

The split looks for two kinds of signal, and it looks at them in the order below.

**Hebrew section markers on a short standalone line.** A line that begins with `פרק` followed by a
number or a Hebrew word starts a new chapter. A line that begins with `פרולוג` starts a new chapter
titled `פרולוג`. A line that begins with `חלק` followed by a number or a Hebrew word does not start
a chapter; it sets the part name that the following chapters are filed under, until the next part
marker or prologue.

These markers only count on a short line on its own. That gate exists because the same words appear
inside ordinary prose, and without it a sentence in the middle of a paragraph would split your book.

**Heading 1 paragraphs.** A paragraph styled as Heading 1 starts a new chapter, and its text becomes
the chapter title. The style is recognized under its English and Hebrew names, and the match is not
case sensitive.

If neither signal appears anywhere in the document, the whole file is imported as a single chapter.
That is the fallback, not a failure.

## The preview does not change anything yet

After you upload, you get a preview: one row per detected chapter, with its title, its part, its
order, its word count and the first stretch of its text. Nothing has been saved to your book at this
point. Uploading a file and then walking away leaves your book exactly as it was.

In the preview you can:

- correct a chapter title,
- correct or set a part name,
- change the order,
- exclude a chapter from the import, or select and deselect them in bulk.

This is the cheapest place to fix a bad split, because it is text in a form rather than chapters in
a book.

## Append or overwrite

Confirming the import asks you for one of two modes:

- **Append** adds the selected chapters to what the book already has.
- **Overwrite** replaces the book's chapters with the selected ones.

Choose overwrite when you are re-importing a manuscript you have revised outside PageDraft, and
append when you are adding new material to a book that already holds work you want to keep.

## When the split comes out wrong

Two patterns cover most cases.

**Everything landed in one chapter.** The document has no Heading 1 styles and no recognized section
markers. Applying Heading 1 to your chapter titles in the word processor and re-importing is usually
faster than splitting by hand.

**Too many chapters, and some of them start mid-sentence.** A short line in your manuscript begins
with a word that reads as a section marker. Deselect those rows in the preview, or reword the line
in the source document and import again.

## What import does not do

Import does not analyze anything. It does not build a summary, it does not run a proofread, and it
does not need any of them to have run. It saves chapters. Everything else you do afterwards works
from what it saved.
