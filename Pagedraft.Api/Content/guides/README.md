---
id: guides-index
stage: index
audience: author
updated: 2026-08-14
lang: en
---

# PageDraft guides

Guides to the editing workflow, written for the author using PageDraft. Each guide covers one stage:
what you do, what you get, and what has to exist before it.

These are written against the workflow rather than against the current screens, so they stay true
when the interface changes. They name surfaces where that helps, and they avoid describing layout.

## Contents

| Guide | Covers | Hebrew |
|---|---|---|
| [00 Workflow overview](00-workflow-overview.md) | The five stages, what depends on what, and what goes out of date | [עברית](00-workflow-overview.he.md) |
| [10 Import](10-import.md) | Turning a DOCX manuscript into chapters, and fixing a bad split | [עברית](10-import.he.md) |
| [20 Book setup and intelligence](20-book-setup-and-intelligence.md) | The book briefs, the book profile, your book's writing style, and asking questions about the book | [עברית](20-book-setup-and-intelligence.he.md) |
| [30 Chapter editing passes](30-chapter-editing-passes.md) | Proofread, Line Edit, Linguistic, Literary and Chapter recap | [עברית](30-chapter-editing-passes.he.md) |
| [40 Developmental review](40-whole-book-review.md) | Findings across plot, character, pacing, tone, theme and continuity | [עברית](40-whole-book-review.he.md) |
| [50 Export](50-export.md) | Getting your manuscript back out as DOCX | [עברית](50-export.he.md) |
| [90 FAQ](90-faq.md) | Tiers, staleness, run-to-run variation, and what changes text | [עברית](90-faq.he.md) |

## Conventions

- English is the canonical text. The Hebrew sibling of each file carries the same content with the
  same section structure, so a reader in either language finds the same answers in the same places.
- Every file starts with light frontmatter (`id`, `stage`, `audience`, `updated`, `lang`) so the
  content can be indexed and cited later without rewriting it.
- Sections stand on their own. A section lifted out of its guide should still make sense.
- No model or provider names appear anywhere in these guides.
- This corpus is a live retrieval source: the in-app product chat answers questions by selecting and
  quoting these files, and shows the guide it used as a citation. Editing a guide edits what the
  assistant says, not just what a reader sees here.
- These files are also USER-VISIBLE PAGES. The app serves them read-only over `GET /api/guides` and
  `GET /api/guides/{id}?language=...`, and the client renders them at the `/help` route, where the
  assistant's citation chips link to. So editing a guide edits what an author reads directly, word for
  word, on a page in the product. There is no separate "public" copy to edit instead.
- The reader takes a guide's TITLE from its first H1, because the frontmatter has no `title` field and
  deliberately never gained one (see the next bullet).
- HEADINGS ARE A RETRIEVAL INDEX, so a copy edit to one is a change to the chatbot. `GuideSelector`
  scores a question's tokens against each file's H1/H2 headings (weight 3.0) and its frontmatter
  `id`/`stage` (1.0), and reads no body prose at all, so renaming a heading silently re-ranks which
  guides reach the model. Keep the topic word in any new heading, and re-run
  `dotnet test --filter "FullyQualifiedName~ProductChat"` after any heading edit. Renaming an H1 also
  breaks the client's citation-title map: `ProductChatCorpusTests` will fail and name the file to fix.

מדריכים לתהליך העריכה ב־PageDraft. גרסת העברית של כל מדריך נמצאת בקובץ עם הסיומת `.he.md`.
