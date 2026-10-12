# Annotated proofread DOCX

What the whole-book proofread download gives an author, and where it stops. Design and endpoint
contracts: `PAGEDRAFT_DESIGN.md` section 6.2.1.

## What the author receives

`GET /api/document/export/book/{bookId}/proofread` (or the single-chapter variant) returns a normal DOCX
of the book, named `<title> - הגהה.docx` (Hebrew books) or `<title> - proofread.docx` (others). Every
pending Proofread suggestion that could be placed in the text appears as:

- a tracked change: the original words struck through (a Word deletion) followed by the suggested words
  (a Word insertion); a pure deletion has only the strike-through, a pure insertion only the new words;
- a Word comment on the same span, authored `PageDraft הגהה` (Hebrew) or `PageDraft Proofreader`
  (initials PD), saying what is suggested instead of what, then the category when it is the Academy full
  spelling rule, then the model's explanation when there is one.

The author's own comments and tracked changes already in the chapters pass through untouched.

## Accept everything in Word

Review tab, Accept, Accept All Changes in Document. Every suggestion is applied and the corrected text
results. To refuse one, Reject that change and delete its comment. Comments can be removed in bulk with
Delete, Delete All Comments in Document.

## Limits

- Pending suggestions only. A suggestion already accepted is already in the chapter text; dismissed,
  reverted and superseded ones are left out.
- Proofread only. Line edit and linguistic suggestions are not written (the annotator is type-agnostic, so
  adding them later is a filter change).
- Suggestions come from the chapter's current Active Proofread result. A chapter with no result, or only
  results from before its last edit, annotates what the result holds or nothing; run the whole-book
  proofread first so every chapter is fresh.
- Some suggestions cannot be placed: the original text is not found, is ambiguous even with its
  context, crosses a paragraph boundary, or overlaps a suggestion already placed. They are not guessed at.
  The count is returned in the `X-Export-Unanchored-Count` header and shown in the download notice;
  placed plus unanchored equals the pending total.
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
because DocIO's `TrackChanges = true` records no revisions for API edits on the shipped 32.1.24. Anchoring
is by text plus context into a normalized paragraph view, never by raw offset. Annotation happens on each
unit's buffer before book assembly, which was premise-tested to keep all revisions and comments.
