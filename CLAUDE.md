# codegraph

A C# tool (.NET, compiled to one native exe with Native AOT, no NuGet packages at runtime) that parses several
repositories into one cross-repo knowledge graph (`codegraph-out/graph.json`), a report
(`codegraph-out/GRAPH_REPORT.md`) and an Obsidian vault. No LLM calls; deterministic; parse cache in
`codegraph-out/cache.json`. Made for legacy-heavy codebases that tree-sitter tools can't read (VB6, T-SQL, WiX, ...).
Machine- and company-specific context is in `CLAUDE.local.md` (git-ignored) when present.

Build the exe with `.\publish.ps1` (-> `publish\codegraph.exe`; the native link step needs the VS C++ workload and
the script puts `vswhere.exe` on PATH). Run: `.\publish\codegraph.exe build [--full] [--no-vault] | show <name> |
impact <name> [--depth N] | path <a> <b> | release-snapshot <ver> | init`. Without the native toolchain:
`dotnet run --project src\CodeGraph -c Release -- <command>`.
Config: `codegraph.json` (JSON with comments: repos, ignores, vault, `releases.json`, `knowledge.json`,
`proc_prefixes`). These three files are the user's own and git-ignored; `codegraph init` writes the examples from
`src/CodeGraph/Examples`. Projects target `net10.0`.

## How to work here
- **Git, commit often**: prefer small, working commits (one logical change each, tests pass, tool still runs).
- **Changelog**: every user-visible change gets a line under `## [Unreleased]` in `CHANGELOG.md` (✨ Added / 🔄 Changed /
  🐛 Fixed / 🗑️ Removed headings; start each line with a fitting emoji). To release: rename it to `## [x.y.z] - date`, add a new empty `[Unreleased]`, update the links at
  the bottom, commit, then tag `vx.y.z` and push - the Release workflow uses that section as the release notes.
- **Save tokens**: hand self-contained subtasks (searching/reading many files, checking parser output, reviewing
  the report) to a subagent on a cheaper model when that is good enough; keep design work here.
  Parallel agents that write code should use worktrees (builds lock `obj/`), then merge their branches.
- **Public repo**: never put real company/product/table/folder names into tracked files (code, tests, docs);
  use neutral examples.

## Architecture (`src/CodeGraph`, tests in `tests/CodeGraph.Tests`)
- `Cli.cs` (argument parsing) -> `Commands.cs` (build / show / impact / path / release-snapshot). `Config.cs` loads
  `codegraph.json` (source-generated System.Text.Json, snake_case keys, paths relative to the config file).
- `Scan/` - `Scanner` walks repos with `IgnoreRules` (built-in + configured, gitignore-ish, case-insensitive, `**/`,
  `!`), picks a parser via `ParserRegistry` (by extension/file name), parses in parallel, caches by size+mtime.
  **Bump `Scanner.ParserVersion` whenever a parser's output changes**, otherwise stale cache is used.
- `Parsers/*Parser.cs` - each `static FileFacts Parse(string relPath, string text)`. `Model/Facts.cs`: `FileFacts`
  (lang, symbols, refs, loc, project, solution), `Symbol` (name, kind, line, parent, visibility, lines, signature),
  `Ref` (from = routine, from_parent = type, kind, name, line, qual/qual_type for member calls, mode for SQL, via),
  `ProjectInfo`/`LibRef`/`SolutionInfo`. Language-specific extras live in `X` (a `JsonObject`, snake_case keys,
  e.g. symbol `routes`/`bases`/`key`, file `imports`/`exports`/`component`/`globals`, ref `verb`/`lib`/`raw`).
  Parsers: Vb6 (frm/bas/cls/ctl, also VB.NET), CSharp (types, methods, routes from attributes), Cpp (+ .def exports),
  Sql (objects, columns, FKs, read/write/exec), JavaScript (TS/JS/Vue SFC, imports, re-exports, HTTP endpoints,
  package.json -> project, tsconfig paths), PowerShell/batch, Project (vbp vbg sln slnx csproj vbproj vcxproj vcproj
  dsp sqlproj wixproj), Wix (installer, features, shipped files, services), Razor (cshtml), Rust.
  `Text/`: `Scrub` (blank comments/strings, same length, returns string literals), `SqlText` (SQL refs in strings,
  `ObjectKey`), `Routes.Normalize`, `LineIndex`, `SourceText.Read` (UTF-8/UTF-16/1252).
  Inline SQL in strings and stored-proc names in strings become DB refs everywhere.
  `ExtractorParser` reads files matched by an `extractors` rule (XPath / JSON paths -> `X["items"]`, embedded SQL via
  the SQL parser); `Scan/CodeStrings` keeps short code string literals (`X["strings"]`) for `opens` links.
- `Build/Builder*.cs` - files -> projects (member lists, SDK-style globbing, project refs, merged duplicate project
  files) -> symbols & indexes -> routes -> libraries (own OCX/COM/DLL/npm outputs become project->project
  `depends_on`) -> reachability -> extractor items (`Builder.Items.cs`: item nodes `item:<kind>:<name>`, their links,
  `fileref:` nodes, releases) -> ref resolution -> `opens` links from code strings -> rollups. `Build/Paths.cs`: case-insensitive path keys
  (`Paths.Key`), Windows semantics everywhere.
  Resolution rule: candidates must be visible (caller's project + referenced projects); prefer same file, then
  same project; ambiguous matches are skipped, never guessed. DB objects are global (`db:<name>`).
  JS: imports resolved to files (relative, tsconfig paths, `@/`->src, internal npm package entry, re-exports).
  HTTP: `endpoint` refs matched to controller routes (`calls_api`). C# interface calls also link to implementations.
- `Model/Graph.cs` - nodes/edges whose attributes are `JsonObject`s (written as-is to graph.json).
- `Knowledge/` (releases.json, releases from git branch names, knowledge.json facts/notes, release-snapshot),
  `Output/Views.cs` (rollups for output), `Output/Obsidian.cs` (vault; manifest-based sync, only rewrites changed
  notes, never touches user notes), `Output/Report.cs`, `Query/` (show/impact/path).

Node ids: `repo:`, `area:Repo:TopFolder`, `proj:Repo:relpath`, `sln:`, `file:Repo:rel`, `sym:Repo:rel#Type.Member`,
`db:<object>`, `ext:<lib>`, `rel:<version>`, `concept:`, `item:<kind>:<name>`, `fileref:<name>`. Main edge relations: contains, calls, instantiates,
uses_type, inherits, calls_native, includes, imports, uses_component, calls_api, renders, mentions, uses_value, reads, writes,
executes, references (FK), triggers_on, defines/alters, depends_on, uses_library, uses_code_of, ships, released_in.

## Working on the tool
- No runtime NuGet packages; everything must work under Native AOT. JSON: source-generated contexts or
  `JsonNode`; never `JsonArray.Add<T>`/`JsonValue.Create<T>` with non-JsonNode values (cast to `(JsonNode?)`).
  IL2026/IL3050 are errors in the app, and tests run with JSON reflection disabled to catch this.
- Regex via `[GeneratedRegex]`. Parsers are regex/lexer based on purpose (no compiler). Precision over recall:
  link only to things that exist.
- After changing anything: `dotnet test CodeGraph.slnx`, then `.\publish.ps1; .\publish\codegraph.exe build` and check
  `GRAPH_REPORT.md` (resolved %, unmatched API calls, orphan files, missing project members, parse errors) and
  spot-check a few notes / `show` results against the source.

## Known gaps / ideas (not done yet)
- URLs built at runtime (string concatenation, jsonp helpers) are not matched to API routes. VB6 late binding
  (Object/Variant) is not followed. CI runs the tests on Windows, Linux and macOS, but real use is on Windows.
- Possible next steps: MCP server over graph.json; compare two releases as a diff report; per-release change lists
  from git; HTML viewer.
