# Changelog

All notable changes to codegraph are listed here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is 0.x, minor versions may change
the config format or `graph.json` layout; such changes are called out below.

Sections: ✨ Added · 🔄 Changed · ⚠️ Deprecated · 🗑️ Removed · 🐛 Fixed · 🔒 Security

## [Unreleased]

### ✨ Added
- 🤖 **MCP server**: `codegraph mcp` serves the graph to AI assistants (Claude Code, Cursor, VS Code, ...) over
  stdio, with read-only tools `search`, `show` (filter by relation and direction), `impact`, `path`, `source`
  (a definition's code with line numbers) and `report`. Results carry node ids. An ambiguous name returns its
  candidates instead of a guess, an unknown relation or kind lists the valid ones, `report` returns the summary or
  one section, and the graph is reloaded after each `codegraph build`. Setup is in
  `docs/agents.md`.

### 🔄 Changed
- 🎯 `show`, `impact`, `path` (and the MCP tools, `source` too) never guess which node you mean. A name must match
  exactly - node id, name, qualified name or path, ignoring case - or be a pattern with `*` as wildcard
  (`db:order*`, `*OrderLine*`). Several matches, or none, answer nothing and list the candidates with their ids
  (for none: the closest names). Before, a partial name silently picked the first thing that contained it, and an
  ambiguous name was answered for the first match. Use `search` (MCP) to find things by part of their name.
- 📊 "References resolved" in the report only counts guesses - any string that looks like an object name, any
  `x.y(` in SQL - when they link to something, so the rate reflects real references. It is higher than before
  for the same code.
- 📂 The built-in ignores no longer contain `rest-examples`; add it to your own `ignore` if you need it.
- 🧭 `path` follows every relation (also `uses_library`, `released_in`, `opens`, `uses_value`, ...) and prefers a
  one-way chain (A uses ... B, or B uses ... A). A chain that changes direction - two files importing the same
  library - is only shown when there is no one-way chain, and is marked as such. The search stops at `--max-len`
  steps (default 8), not just the output.
- 📦 `impact` lists a file built into more than two projects once, under "(shared files)" with its projects,
  instead of under every project. Reports, patches and other items are grouped by kind under "(other)" instead of
  "(folder ?)".

### 🐛 Fixed
- 🔗 A .NET reference is only linked to a project that can produce it: `<Reference Include="System" />` (a framework
  assembly, no HintPath) no longer makes every .NET project depend on a VB6 app that happens to be called `System`,
  and assembly/package references only reach .NET projects, native libraries only C/C++ projects.
- 🧭 `path` no longer connects two things through a hub when it has to change direction: two screens in the same
  release, patch, installer or project are not "connected" by that. A chain also never goes down `contains` after a
  call: calling a factory or class does not mean using all of its members (`contains` counts only at the ends - a
  file's own routines first, or a used member's container last). Opening a form or creating an object does run its
  initialisation: after `opens` / `instantiates` a chain continues into the constructor or the VB6 `Form_Load` /
  `Form_Initialize` / `Class_Initialize` events, and `impact` on what those use reaches the code that opens the form.
  A constant used in many places is no hub for mixed chains either.
- 📨 `codegraph mcp` answers a JSON-RPC batch or other non-object with "Invalid request" (-32600), not "Parse error".
- 🆔 A name shaped like a node id (`db:orders`, `sym:...`) is looked up as an id only, ignoring case: one that
  doesn't exist gives "No node has the id ..." instead of silently answering about another node that contains the
  text. MCP `search` reports an unknown `kind` or `repo` as an error with the valid values.
- 🔄 `codegraph mcp` tells you (once, in its next answer) when `codegraph.exe` was updated after the server started,
  since only a restart loads the new code.
- 🗣️ Text such as "Failed to execute telnet command" or "Execute the command" no longer counts as a call to a
  procedure named `telnet` or `the`: `EXEC name` must be followed by nothing, `;`, a parameter, a comment or the
  next statement. The report's "called but not found" list now shows where each name was first seen.
- 🧮 Column names are no longer reported as missing procedures because they share a procedure prefix: a string read
  through `Fields("x")`, `Item("x")`, `GetOrdinal("x")`, `Field<T>("x")` or `row["x"]` is a column, and so is a
  name that matches a column of a known table, or one passed to a call together with a row, reader or recordset
  variable (your own `GetValue(row, "x")` helper). A string ending in `_` (`"Prefix_" + x`) is not a name.
- 💾 `build` no longer crashes when another program has `graph.json` open (e.g. a running `codegraph mcp`): the
  new graph is written next to it and swapped in, so readers never see a half-written file. If the file is
  locked for good, the build says so instead of printing a stack trace.
- 📂 Built-in ignores no longer skip your own code that merely looks like build output or a library:
  `bin`/`obj` are only skipped next to a project file, `Debug`/`Release`/`x64`/`x86` next to a C++ project or
  solution, and `packages` next to a solution (a JS monorepo's `packages/` is read now). Library copies are matched
  as `jquery.js`, `jquery-*.js`, `jquery.*.js`, so `reactions.js` or `bootstrapper.js` are read, and
  `Reference.cs` is only skipped inside WCF `Service References` folders.
- 🔠 Types and members are found whatever their casing. C#: member access through any field, local or type
  (`orders.Total`, `order.Empty`), casts recognised by their shape `(T)x`, and only C#'s own keywords are skipped.
  C++: `ns::fn()` names its scope whatever the case. TypeScript: lower-case type names link when such a type is
  declared.
- 🔤 Upper-case C functions (`INIT_DEVICE`) are found; macros are recognised by their `#define` or by having no
  return type, not by their casing.
- 🧬 A method that implements an interface method links to it (`implements`), an override to the base method
  (`overrides`): `impact` on an interface method lists the implementations that must change with it.
- 📁 `impact` folds whole files that depend on something (scripts, old patches - no routine names) at the highest
  folder holding 6 or more of them - one line with a count and a few names ("SQL/ (98 files in 22 folders)"); code
  files keep their routine names. MCP says when it capped `depth`, `limit` or another number; "1 thing" instead of
  "1 things".
- 🪟 VB6: `frmOrder.Show` links the caller to the form as `opens` (instead of only "uses type"), and `impact` on a
  form lists where it is opened from.
- 🧭 JS/TS composables and stores: functions defined inside a function (`useOrders() { const load = ... }`) or in a
  store (`defineStore('shop', { actions: { refresh() {} } })`, also setup-style) are its members
  (`useOrders.load`), and `const svc = useOrders(); svc.load()`, `const { load } = useOrders()` and
  `useShop().refresh()` link to them. "Which screens call this API?" now reaches the views through stores and
  services. Shorthand methods in any object literal inside a function are found.
- 🌐 Front-end API calls no longer depend on how functions are named. Calls through well-known HTTP APIs (`fetch`,
  axios/jQuery verbs, `$.ajax`, `url:` options) count as before. A URL passed to any other function - your own
  wrapper such as `orders.create('/orders')` - links when it matches a server route, and is not listed as an
  unmatched call when it doesn't. Router navigation (`router.push`, `navigateTo`, ...) is never an API call, and
  paths like `/2fa/verify` or `/:id/edit` are recognised.
- 🧩 `Url.Action("Edit", "Orders")` in Razor views links to `OrdersController.Edit` by name, also when the action
  has an attribute route that looks different, and never to a controller in another repository.
- 🔗 Front-end calls match API routes whatever the base path (`/api`, `/v1`, `/backend/api`, or a prefix only the
  server has - one that routes of several controllers share), not just a leading `/api`.
- 🌿 More release branch names are understood - `releases/v2.4.1`, `hotfix/2.4.1-rc1`, `R2_4_0`,
  `release/2024-10`, `release-35` - and the branch is also read in git worktrees and submodules (`.git` file).
- 🗃️ SQL functions are found under any schema (`sales.net_total(...)`), not just `dbo`, and a string naming a
  schema-qualified object (`"sales.GetOrders"`) links to it.
- 🎯 A string that only names a stored procedure (`"GetOrders"`, `"cleanup"`) counts as a call only where the
  surrounding routine uses a database API - ADO, ADO.NET, ODBC, Entity Framework, LINQ to SQL, `sqlcmd`/`Invoke-Sqlcmd`, node
  `mssql`/`oracledb` - or when it has one of your `proc_prefixes`. A word like `"test"` in UI code no longer links
  to a procedure that happens to share its name. Works for any naming style.
- 🏷️ A module that both a release branch and `releases.json` put in the same release is counted once (it showed
  as `x2`). A version from `releases.json` replaces the project version taken from the branch.
- 🔓 `publish.ps1` no longer fails while `codegraph mcp` is running: the locked exe is renamed to
  `codegraph.old-*.exe` (removed on a later run) and the new one is copied next to it.
- 🔧 C++ projects take their output file from the linker settings - `/out:` in `.dsp`, the linker's `OutputFile` in
  `.vcproj` and `.vcxproj` (or `TargetName` + `TargetExt`), Release configuration first. An ActiveX control linked as
  `.ocx` is now an `ocx` project and matches the installers and VB6 references that use it.

## [0.2.0] - 2026-10-25

🧱 Your own file formats, enums and constants.

### ✨ Added
- 🧱 **Extractors for your own file formats**: rules in `codegraph.json` (`extractors`) read in-house XML/JSON files -
  report definitions, patch lists, mappings - into graph nodes and links. XPath / JSON paths pick the values; links
  go to existing DB objects, projects, files, symbols or releases, or create nodes; embedded SQL is parsed as T-SQL;
  `pattern`/`replace` rewrite values (e.g. `240100` → release `24.1.0`). Item notes go to `Items/` in the vault and
  the report gets a "Your own file formats" section.
- 🔗 `opens` links from code to extractor items: a string literal naming an item's file (or name) links the routine
  that contains it (`referenced_by`).
- 🏷️ Releases can come from your own files (`"to": "release"`), merged with `releases.json` and release branches.
- 📋 Extractor `lists`: repeated entries shown as a table on an item's note, with the field names as columns - e.g.
  the columns and rows of a lookup table.
- 🔢 **Enums and constants**: VB6 `Enum` blocks; enum members (VB6, VB.NET, C#, TypeScript) and constants (VB6
  `Const`, C# `const`) are nodes with their values. Code that uses them gets a `uses_value` link - including VB6's
  unqualified enum members and public module constants (ambiguous names are skipped). SQL kept in a constant is
  linked from the constant. File notes list their constants and enum values with who uses them.
- 🤖 Guide for using codegraph with AI coding agents (`docs/agents.md`).
- 📝 This changelog; release notes are now taken from it.

### 🔄 Changed
- 📦 Code files now keep their short string literals in the parse cache (for `opens` links); the first build after
  upgrading re-reads every file once.

## [0.1.0] - 2026-10-24

🎉 First public release.

### ✨ Added
- 🔍 **Scanning** of several repositories into one graph, with gitignore-style ignore rules (built-in, per config,
  per repo via `.codegraphignore`), a parse cache keyed by file size and time, and parallel parsing.
- 🧩 **Parsers** for VB6 (`.frm .bas .cls .ctl ...`) and VB.NET, C#, C/C++ (including `.def` exports), T-SQL,
  TypeScript/JavaScript/Vue, Razor, PowerShell/batch, Rust and WiX, plus project and solution files
  (`.vbp .vbg .sln .slnx .csproj .vbproj .vcxproj .vcproj .dsp .sqlproj .wixproj`, `package.json`, `tsconfig`).
- 🔗 **Linking** of calls, instantiation, inheritance and type use; SQL in code strings and stored-procedure names to
  DB objects; procedures/views/triggers to the tables they read and write; foreign keys; VB6 `Declare` to your own
  DLLs; JS/TS imports (relative, `@/`, tsconfig paths, re-exports, your own npm packages); front-end HTTP calls to
  C# controller routes; C# interface calls to implementations; MVC views; installers to what they ship;
  project-to-project dependencies and rollups.
- 📊 **Outputs**: `graph.json`, `GRAPH_REPORT.md` (unused DB objects, orphan files, missing project members,
  procedures called but not found, parse errors) and an Obsidian vault that only rewrites changed notes and
  never touches your own.
- ⌨️ **Commands** `build` (`--full`, `--no-vault`), `show`, `impact` (`--depth`), `path`, `release-snapshot`,
  `init` and `--version`.
- 📚 **Your own knowledge**: `releases.json` (which module versions shipped in which release, also taken from
  release branch names such as `R2.4.0`) and `knowledge.json` (facts and notes shown on generated notes).
- ⚙️ **Configuration** in `codegraph.json` (JSON with comments), including `proc_prefixes` for your stored
  procedure naming.
- 🤖 **Claude Code skills** `/graph-ask`, `/graph-build` and `/graph-release` (`claude-setup/install.ps1`).
- 📦 **Native executables** for Windows, Linux and macOS (x64 and ARM64) on every release; CI runs the tests on all
  three systems.

[Unreleased]: https://github.com/mika76/codegraph/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/mika76/codegraph/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/mika76/codegraph/releases/tag/v0.1.0
