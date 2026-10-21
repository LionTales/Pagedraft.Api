---
extends: feature-review
---

# Pagedraft.Api-repo overlay for feature-review

This repo is the PageDraft server side (.NET 8 Web API). Its contract partner is the client repo `../pagedraft-client` (Angular 18), which has its own overlay.

## Workspace

- Repo: this checkout (`Pagedraft.Api` and `Pagedraft.Api.Tests`, xUnit). Remote `LionTales/Pagedraft.Api`. PR base: `master` (the remote HEAD; `main` is a stub). The PageDraft root `..` is not a git repo. A legacy SQLite copy `../Pagedraft.Api` is superseded and untracked: always edit this checkout.
- Plans: the workspace-level `../.cursor/plans/` (`_todo/` active, `_archive/` history only). Reviews go to the primary repo's `.cursor/reviews/` (the one with the most pending changes). `.cursor/` is gitignored here: artifacts are never committed.
- Tests: `dotnet build Pagedraft.Api/Pagedraft.Api.csproj`, then `dotnet test Pagedraft.Api.Tests/Pagedraft.Api.Tests.csproj --filter "FullyQualifiedName~<Class>"`. CI: job `Backend tests` (restore, build `-c Release`, test) in `.github/workflows/ci.yml`. Run: `dotnet run` in `Pagedraft.Api` (https://localhost:5001).
- A `dotnet test --filter` OR group is parenthesised before `&`-ing on the GPU exclusion: `(~A|~B)&FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine`. Unparenthesised, `&` binds to the last alternative and `~A` sweeps the live-GPU harness. Tell: a targeted run jumping from seconds to minutes.
- Cross-stack: the API serializes camelCase; DTO changes are reviewed against the client interfaces in `../pagedraft-client`. No mobile repo; no rendered-output baseline fixtures.

## Lenses

1. **Layering**: `Controllers/` thin, `Services/` logic, `Data/` (EF Core), `Models/`. SignalR `Hubs/BookSyncHub` at `/hubs/booksync` (clients join `book:{bookId}`).
2. **AI calls route through `IAiRouter`** to `AiRouter` to a provider (`OllamaProvider` default; OpenAI/Azure/Anthropic opt-in); never reference a provider directly. `UnifiedAnalysisService` is the single analysis entry point; `PromptFactory` builds prompts. Config keys: `Ai:DefaultProvider`, `Ai:DefaultModel`, `Ai:Providers:Ollama:BaseUrl`, `Ai:FeatureModels`.
3. **Two document paths stay in sync**: book-level export vs single-chapter export, and import vs edit/save; a formatting change to one usually needs the other. DOCX import (`DocxParserService`, Heading 1 plus Hebrew markers; `SfdtConversionService`) classifies by content, never by position.
4. **A new persisted setting is an end-to-end checklist**: entity, save-copy line, controller, tests; EF does not auto-map from the DTO. An EF migration ships with its `*.Designer.cs` and `Migrations/AppDbContextModelSnapshot.cs`.
5. **The chat answer passes through three post-model layers** in `Services/Chat/` (`ProductChatInternalLabels` strip, `ProductChatPunctuation` repair, `ProductChatCitations` chips), judged by what they leave. A rule built for one states its policy on the constructs the others own (code span, bracket pair, bidi control). Every layer keeps the never-empty contract (floored on "holds no letter or digit"). Build each rule only from measured leaks.
6. **A guide's headings are a retrieval index**: `Services/Chat/GuideSelector` scores question tokens against H1/H2 headings (weight 3.0) and frontmatter `id`/`stage` (1.0), not body prose. A heading rename re-ranks which guides reach the model: keep the topic token and re-run `--filter "FullyQualifiedName~ProductChat"`.
7. **The model copies what you render and gets what you ask it to derive wrong**: chapter orders are 0-based on the wire (`Chapter.Order`, `ref=chapter-text:0`, `## Chapter {Order}:`, parsed back by `ChapterAnchorResolver`) and 1-based where a human reads. Pre-compute the human-facing value in the answer's language (`ChatLanguage.Detect`), same for dates, units and locales. Naming an internal token to forbid it teaches the token; never stack a prohibition on the grounding clause.
8. **GPU gates**: validate the evidence channel (the API log is UTF-16LE; a UTF-8 slice silently empties every record), the denominator and the prose detector before quoting a rate; re-run a cell at n>=8 before calling a flip between two n=2 runs a regression.
9. **Render-driven acceptance** (RULE 0) for exported documents: re-produce the artifact under real params and inspect it as the user sees it.

## Review framework

- Repo files to read, by the area the diff touches: `Pagedraft.Api/Services`, `Pagedraft.Api.Tests`, `docs/`.
- Contract reference lives in the workspace: `../docs/PAGEDRAFT_DESIGN.md` (API contracts, DTOs, SignalR events, config).
- No FormatBookWebApi `code-review-lessons` skill applies here. Use the kit's cross-stack review lenses and the kit's engineering lessons and validation discipline; PageDraft's defining bug class is the AI/LLM lens, not the formatting-pipeline or billing lenses.
