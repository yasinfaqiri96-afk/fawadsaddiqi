# PTG Oil System

Compilation ownership: Web references Persistence and Migrations; Migrations
references only Persistence. Existing Entity/DbContext source paths/namespaces
are linked into Persistence. Migration source lives in
`src/PTGOilSystem.Migrations/Migrations/`. Daily builds must keep project
references enabled so changed dependencies are compiled correctly; use normal
Web `build --no-restore`, not `Rebuild` or `BuildProjectReferences=false`.
EF commands target `src/PTGOilSystem.Migrations/PTGOilSystem.Migrations.csproj`
with `--startup-project src/PTGOilSystem.Web/PTGOilSystem.Web.csproj`; use
`--no-build` after a fresh build. See `docs/migrations-project.md`.

## Response style

Caveman mode (`full`) is always on in this project from the first response of
every session: follow `.claude/skills/caveman/SKILL.md` without waiting for
`/caveman`. Its Auto-Clarity and Boundaries sections still apply (code,
commits, PRs written normally). Switch level with `/caveman lite|full|ultra`;
off for the session only on "stop caveman" / "normal mode".

## Mandatory Rules

- Read and understand the existing code before making changes.
- Only modify files directly related to the current request.
- Do not refactor or redesign unrelated areas.
- Do not change Entity, Migration, DbContext, or database structure unless explicitly requested.
- Do not change Stock, Inventory, Ledger, Payment, Sales, FX, or P&L logic unless explicitly requested.
- Never guess business behavior.
- First identify the root cause and related files.
- Make the smallest safe change.
- Batch changes, then run the minimum reliable build and related tests once (see Execution efficiency).
- Keep final responses short and precise.

## UI/UX prohibitions (always apply)

- React، Vue، Tailwind، shadcn و framework جدید ممنوع است.
- Business Logic، Controller، Model، Route، permission و Database بدون ضرورت و درخواست صریح تغییر نکند.
- Inline style، CSS تکراری و `!important` جدید ممنوع است.
- تغییر خارج از scope، بازطراحی سراسری و cleanup نامرتبط ممنوع است.

For any UI/UX request, follow the `ptg-ui-design-rules` skill (source order, design-system reading order, review checklist).

## UI Pick (Browser → Claude Code)

When the user picks an element in the browser (`Alt+Shift+P`), the selection is
written to `.ptg-ui-pick/last-pick.md` / `.json`, and the VS Code bridge
(`tools/vscode-ptg-ui-pick`) puts a `/ui-pick …` prompt on the clipboard and
focuses the Claude input. Whenever a message starts with `/ui-pick` — or refers
to "this element/section/card" without naming a file — read the pick files
first, then locate the View/Partial/CSS/JS and change only that region.
Setup and limitations: `docs/ui-pick-workflow.md`.

## graphify

Locate the relevant file and method directly with `rg`; Graphify is not a prerequisite for code search or delivery. Never run it for small bug fixes, UI/CSS/JS/View changes, small Controller changes, text edits, or test reruns. Run it only when explicitly requested, for a major architecture or dependency/service relationship change, or once at the end of a large feature when needed. This project policy overrides generic Graphify skill instructions.

## Execution efficiency

- Follow `inspect once → batch edits → validate once`.
- Inspect initial working-tree state and only files relevant to the request. Preserve unrelated edits. Stop exploring once the root cause is known and implement the scoped fix.
- Do not scan/read the whole repository, generated files, migrations, or build outputs for an ordinary fix. In large files (`LoadingController.cs`, `SalesController.cs`, `Create.cshtml`), search symbols/text and read the method range plus surrounding lines. Read the entire file only for a concrete need.
- Reuse discovered facts; do not repeatedly reread unchanged files. No subagents, extra research, or long plans for simple tasks.
- Do not run build, tests, git diff, or git status after each edit. Batch edits and final validation. Repeat a check only after new changes, a failure, or a specific unresolved concern.
- CSS/JS/text-only changes: `scripts/dev-verify.ps1 -Ui -Paths <task-files>`. Razor changes require one Web build; the UI mode detects Razor paths. Run only relevant structural/UI tests, when available.
- Ordinary C# changes: `scripts/dev-verify.ps1 -Web -Filter 'FullyQualifiedName~RelatedClass'`. With a filter, it builds the test project once, including Web and required project references; then runs targeted tests with `--no-build --no-restore`. Without a filter, it builds only Web. A Web build alone does not refresh the test DLL.
- For repeated tests after a successful fresh test build, use `dotnet test ... --no-build --no-restore --filter ...`. Existing test scripts also reuse the built assembly by default; pass `-Build` only when needed.
- Controller changes: that Controller/class's tests. Service changes: that Service's tests. Full suite is forbidden for ordinary fixes; reserve it for accounting, inventory, ledger, migration, cross-cutting changes, or release/deploy verification.
- Full solution build is forbidden for everyday Web changes; reserve it for shared projects, Entity/Migration architecture changes, or release validation. Use `scripts/dev-verify.ps1 -Full` for that scope, including the full suite and EF pending-model check. Never apply migrations during verification.
- Use `--no-restore` after the initial restore. Restore only when dependencies/project/build imports change, assets are missing, or an explicit assets error requires it. Do not add an implicit restore to routine validation.
- Keep Razor compilation/source generators, Debug symbols, and required checks enabled. Debug bundling is skipped; Release/Publish still generate production bundles. Debug outside Development requires `-p:PtgBuildBundles=true`.
- Do not kill user-owned dotnet/watch/compiler/VS Code processes. Use `-IsolatedCompiler` on dev-verify only for compiler contention; it disables shared compilation/node reuse and limits build parallelism for that invocation.
- Concurrent Claude/Codex writers must use separate Git worktrees or checkouts with appropriate branches. A second branch name inside the same checkout does not isolate files or build caches.
- Detailed commands, validation boundaries, and measured timings: `docs/development-performance.md`.
