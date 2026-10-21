# Pagedraft.Api - Claude Code Workspace Context

One line on what this repo is for. Last updated: (date).

Shared conventions: the **ai-knowledge-kit** plugin (repo `LionTales/ai-knowledge-kit`). Install once per machine: `claude plugin marketplace add LionTales/ai-knowledge-kit`, then `/plugin install ai-knowledge-kit@liontales`; start at its `ai-knowledge-kit:kit` skill or `MANIFEST.md`. In a cloud session attach that repo beside this one; the SessionStart hook in `.claude/settings.json` reports where it is checked out. Reference it, don't fork it. New cross-cutting lessons go to the kit's `knowledge/inbox/`; repo-specific ones stay in this repo.

---

## 1. Workspace Layout

One line: the repos of this workspace and their stacks.

## 2. Rules (Always Applied)

One line: the always-on conventions, each a pointer to the kit's `rules/*.mdc`. Docs live in `docs/`; review reports in `docs/reviews/`.

## 3. Cross-Repo / Cross-Cutting Impact Matrix

One line: "if you change X, verify Y".

## 4. Business / Domain Context (Key Rules)

One line: the non-obvious product rules an agent cannot infer from the code.

## 5. Skills Reference Index

One line: the kit skills and the project skills, and when to use each.

## 6. Architecture Quick Reference

One line: the load-bearing files and the "always update both paths" traps.

## 7. MCP Servers

One line: which MCP servers are configured and what each is for.
