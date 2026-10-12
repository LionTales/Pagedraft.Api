# Annotated proofread DOCX

What the whole-book proofread download gives an author, and where it stops. Design and endpoint
contracts: `PAGEDRAFT_DESIGN.md` section 6.2.1.

## What the author receives

`GET /api/document/export/book/{bookId}/proofread` (or the single-chapter variant) returns a normal DOCX
of the book, named `<title> - הגהה.docx` (Hebrew books) or `<title> - proofread.docx` (others). Every
pending Proofread suggestion that could be placed in the text appears as a **tracked change**: the
original words struck through (a Word deletion) followed by the suggested words (a Word insertion); a pure
deletion has only the strike-through, a pure insertion only the new words. Revisions are authored
`PageDraft הגהה` (Hebrew) or `PageDraft Proofreader`.

**A Word comment appears only when it adds something the change does not show** (owner decision
2026-10-12): the label `כתיב מלא לפי האקדמיה` / `Academy full spelling` on a suggestion from the Academy
full-spelling rule, and/or the suggestion's explanation when one was generated. The comment, initials PD,
is ranged over that suggestion's change and holds only those lines; it never restates "X instead of Y",
which the change itself already shows. Most suggestions therefore carry no comment at all.

An explanation is model output, so before it is written it is trimmed, every whitespace run (line breaks
included) becomes one space, control characters are dropped, and it is capped at 500 characters plus an
ellipsis. One that holds no letter or digit, or is the stored "No explanation could be generated"
placeholder, counts as absent.

The author's own comments and tracked changes already in the chapters pass through untouched.

## Reviewing in Word

- **One suggestion:** select it (or put the cursor in it) and press Review, Accept or Reject once. A
  replacement's deletion and insertion are one selection, so a single Reject restores the original word.
  A commented suggestion also needs its comment deleted (Word's Accept and Reject never remove a comment),
  which is why comments are kept to the ones that carry information.
- **Everything:** Review, Accept, Accept All Changes in Document applies every suggestion. Any remaining
  comments can be removed with Delete, Delete All Comments in Document.

## Limits

- Pending suggestions only. A suggestion already accepted is already in the chapter text; dismissed,
  reverted and superseded ones are left out.
- Proofread only. Line edit and linguistic suggestions are not written (the annotator is type-agnostic, so
  adding them later is a filter change).
- Suggestions come from the chapter's current Active Proofread result. A chapter with no result, or only
  results from before its last edit, annotates what the result holds or nothing; run the whole-book
  proofread first so every chapter is fresh.
- Whitespace-only suggestions are left out. A suggestion whose original and suggested text are equal
  once leading and trailing whitespace is trimmed (the client's own hide rule in `proofread-diff.ts`, with
  JavaScript's whitespace set: it includes the no-break space U+00A0) changes nothing an author can act
  on, so it is never written as a tracked change. The count is returned in
  `X-Export-Whitespace-Skipped-Count`; the download notice does not mention it. Measured on a 55-chapter
  book (2026-10-12): 4,009 of 4,676 pending suggestions were whitespace-only, 4,008 of them a space or
  no-break space at a paragraph edge that the proofread chunker had trimmed and reported back as a
  deletion. One was a real spacing fix (a space before a closing quote) and is lost to this rule.
- A real edit that the diff fused with paragraph-edge whitespace (`" כול"` to `"כל"`, the space being the
  break between two paragraphs) is placed on its words. The edge whitespace both sides share is taken off
  first; only when that still cannot be placed is all of the original's edge whitespace taken off, so a
  word deleted together with its following space inside a paragraph still takes the space with it.
- Some suggestions cannot be placed: the original text is not found, is ambiguous even with its
  context, crosses a paragraph boundary, or overlaps a suggestion already placed. They are not guessed at.
  The count is returned in the `X-Export-Unanchored-Count` header and shown in the download notice.
  `X-Export-Annotation-Count` counts placed suggestions (tracked changes), not comments. Placed plus
  unanchored plus whitespace-skipped equals the pending total; on the book above that was 666 + 1 + 4,009
  (the one unplaced suggestion is a real edit spanning two paragraphs).
- Unreliable results are exported by default. When the system flagged a chapter's Active Proofread result
  as unreliable (the model output looked broken), the export page asks first: export everything, skip
  those chapters, or cancel. The choice travels as `?unreliable=include|skip` (default `include`,
  case-insensitive; any other value is a 400). With `skip`, such a chapter is still in the file as plain
  text, its suggestions are neither placed nor counted as unanchored, and the
  `X-Export-Unreliable-Skipped-Count` header reports how many chapters were left unannotated (0 on
  include). The chapters to ask about come from `GET /api/books/{bookId}/proofread/status`
  (`unreliableChapters`). A non-forced re-run of the whole-book proofread also replaces an unreliable
  result, since it counts as stale.
- Pasting the annotated file back into the editor drops comments and revisions (the editor import has no
  comments part today); use the file for the author's review in Word, not as the next manuscript.

## For maintainers

Comments and tracked changes are hand-written Open XML (`Services/Export/ProofreadDocxAnnotator.cs`),
because DocIO's `TrackChanges = true` records no revisions for API edits on the shipped 32.1.24. The
comment rule and the explanation floor live in `ProofreadAnnotationStrings.CommentLines` /
`SanitizeExplanation`; a file whose suggestions all lack information has no comments part at all. Anchoring
is by text plus context into a normalized paragraph view, never by raw offset. The whitespace rules
(whitespace-only skip, common-edge trim, boundary-edge fallback) live in `ProofreadWhitespace`, whose
whitespace set is pinned code unit by code unit against JavaScript's `trim` (it is not .NET's
`char.IsWhiteSpace`: U+FEFF is whitespace there, U+0085 is not). Note the client applies its hide rule
only to suggestions it diffs itself; stored server suggestions reach the chapter view unfiltered. Annotation happens on each
unit's buffer before book assembly, which was premise-tested to keep all revisions and comments.
