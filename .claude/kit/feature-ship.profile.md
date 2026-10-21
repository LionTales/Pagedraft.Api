---
extends: feature-ship
---

# Pagedraft.Api-repo overlay for feature-ship

This repo is the PageDraft server side. Its contract partner is `../pagedraft-client` (the client), which has its own profile.

## Workspace

- Repo: this checkout, remote `LionTales/Pagedraft.Api`, PR base `master` (`master` is the integration branch; `main` is a stub: trust the remote HEAD). The PageDraft root `..` is not a git repo; the superseded legacy copy `../Pagedraft.Api` is untracked and never edited.
- Branch: one FRESH branch per PR off `origin/master`, slug `api-<feature>` (the client's parallel slug is `<feature>`). Branch reuse was retired 2026-06-18.
- Plans: the workspace-level `../.cursor/plans/`; reviews in the primary repo's `.cursor/reviews/`. `.cursor/` is gitignored here, so artifacts are never staged.
- Tests: `dotnet build Pagedraft.Api/Pagedraft.Api.csproj` and `dotnet test Pagedraft.Api.Tests/Pagedraft.Api.Tests.csproj --filter "FullyQualifiedName~<Class>"`, with an OR group parenthesised before the GPU exclusion `&FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine`. CI: job `Backend tests` (restore, build `-c Release`, test) in `.github/workflows/ci.yml`. Discover the checks per PR with `gh pr checks <pr>`.
- Commit-message style: plain imperative, no conventional-commit prefix. No ticket convention is recorded for PageDraft: leave a tracker status alone.

## Commit batches

- Generated-artifact rule: an EF migration ships in ONE commit with its `*.Designer.cs` AND `Migrations/AppDbContextModelSnapshot.cs`. Before batching, confirm both are present and consistent, then `dotnet build` and the relevant `dotnet test --filter` to confirm no model drift (this is the repo's whole stale-fixture preflight).
- Group the rest by concern within the single PR so that each commit builds and its tests pass.
- Ship order when the feature spans both repos: this server PR first (it is the contract source of truth), the client PR second with a cross-link in its body.
- The FormatBookWebApi baseline-bucket preflight does not apply: PageDraft has no rendered-output baseline fixtures.

## Ship pass

- Before the PR, check that a DTO change is mirrored by the client interfaces (camelCase on the wire), and that a new persisted setting went through entity, save-copy line, controller and tests.
- After the PR is green, run `review-retro` as the kit's feature-ship closes the cycle; one-off lessons go to memory, durable ones to the kit.
