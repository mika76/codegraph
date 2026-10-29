# codegraph

**A map of how your code fits together - across repositories, languages and the database.**

codegraph reads all your repositories and builds one graph of who calls what: which screens use which tables,
which front-end calls hit which API action, which installer ships which DLL, which apps break if you change a
stored procedure. You browse it as an [Obsidian](https://obsidian.md) vault, ask it from the command line, or let an
AI assistant such as Claude Code use it to answer questions about your code.

It was built for the kind of codebase most tools give up on: VB6 forms next to C# services, T-SQL scripts, C++ DLLs,
WiX installers, a Vue front end and an ASP.NET API, spread over several repos.

```text
> codegraph impact sp_Get_Customer

Impact of changing sp_Get_Customer (db_proc)  [Database/Procedures/dbo.sp_Get_Customer.sql:5] (up to 3 steps): 41 things

(database)
   db_proc: sp_Order_Place
Order Entry
   ORDER ENTRY/frmCustomer.frm
   Shared/modUtils.bas
Shop.Api.Customers
   Shop.Api.Customers/Repositories/CustomerRepository.cs
   Shop.Api.Customers/Controllers/CustomerController.cs
StaffApp
   src/api/customers.ts
...
```

## Why use it

- **Know what breaks before you change it.** `impact` lists every routine, screen, API, report and app that
  depends on a table, procedure, class or file - directly or a few steps away - grouped by application.
- **See across language boundaries.** SQL embedded in VB6 or C# strings is linked to the real tables; a
  `fetch('/orders/42')` in a Vue component is linked to the C# controller action that serves it; a VB6 `Declare`
  is linked to the function in your own C++ DLL; an installer is linked to the projects whose output it ships.
- **Get new people up to speed.** Every project, file, table and procedure gets a note with what it contains, what
  it uses and what uses it. Obsidian's graph view and backlinks make the structure explorable.
- **Find dead weight.** The report lists tables and procedures nothing seems to use, source files that are in no
  project, project files that reference missing files, and procedures called from code that don't exist.
- **Track releases.** Record which module versions shipped in which release (or let release branch names do it),
  and every project note shows its history.
- **Stays on your machine.** No AI calls, no uploads, no server. One native executable; a full scan of a few
  thousand files takes well under a minute, and later runs only re-read changed files (seconds).
- **Deterministic and honest.** The same code always gives the same graph. When a name could mean two things the
  link is skipped rather than guessed, so what you see is there.

How does it compare with general tools such as graphify? See [codegraph vs graphify](docs/codegraph-vs-graphify.md).
What's coming next: [roadmap](docs/ROADMAP.md).

## Getting started

1. **Get the tool**

   **Download** the archive for your system from the
   [latest release](https://github.com/mika76/codegraph/releases/latest) (Windows, Linux and macOS, x64 and ARM64)
   and unzip it anywhere. It's a single executable with nothing else to install. The examples below use
   `.\publish\codegraph.exe`; use the path you unzipped to, or put the folder on your `PATH` and just type `codegraph`.

   **Or build it** with the [.NET SDK 10](https://dotnet.microsoft.com/download) or newer (on Windows the native
   build also needs Visual Studio or the Build Tools with the **Desktop development with C++** workload):

   ```powershell
   git clone https://github.com/mika76/codegraph.git
   cd codegraph
   .\publish.ps1            # -> publish\codegraph.exe (x64 or ARM64, whichever this PC is)
   ```

   No C++ workload? Run it through .NET instead: `dotnet run --project src\CodeGraph -c Release -- <command>`.

2. **Create your config**

   ```powershell
   .\publish\codegraph.exe init     # writes codegraph.json, releases.json, knowledge.json
   ```

   Edit `codegraph.json`: list your repositories and where the Obsidian vault should go.

   ```jsonc
   {
     "vault": "C:/src/CodeGraphVault",
     "repos": [
       { "name": "Shop", "path": "C:/src/Shop" },
       { "name": "Api",  "path": "C:/src/Api" },
       { "name": "Web",  "path": "C:/src/Web" },
     ],
   }
   ```

   These three files are yours and are not committed (they're in `.gitignore`).

3. **Build the graph**

   ```powershell
   .\publish\codegraph.exe build
   ```

   It prints what it found and writes `codegraph-out\graph.json`, `codegraph-out\GRAPH_REPORT.md` and the vault.

4. **Look around.** In Obsidian choose *Open folder as vault*, pick the vault folder and start at **Home**.
   Skim the **Codegraph Report** first: it shows how much was linked and anything that looks wrong (e.g. a
   generated or vendored folder that should be ignored - add it to `"ignore"` in `codegraph.json` and build again).

5. **Ask questions**

   ```powershell
   .\publish\codegraph.exe show Orders              # a table/project/file/routine and all its connections
   .\publish\codegraph.exe impact Orders            # everything that depends on it, grouped by app
   .\publish\codegraph.exe path frmMain Orders      # how two things are connected
   ```

   A name must match exactly (id, name, qualified name or path, any case); use `*` as a wildcard (`"Order*"`,
   `"*Repository.GetById"`). When it matches several things (there are often many `frmMain`) or nothing, the tool
   lists the candidates with their ids instead of guessing; repeat with the path or id it shows.

## Everyday use

| When | Run |
|---|---|
| After pulling or changing code | `codegraph build` (only changed files are re-read) |
| Something looks stale or a parser changed | `codegraph build --full` |
| Only the graph and report, no vault | `codegraph build --no-vault` |
| "What uses X?" / "What breaks if I change X?" | `codegraph show X` / `codegraph impact X [--depth N]` |
| "How does A reach B?" | `codegraph path A B` |
| Let an AI assistant query the graph | `codegraph mcp` (started by the MCP client, see the [agents guide](docs/agents.md)) |
| Cutting a release | `codegraph release-snapshot 2.5` (then edit `releases.json`) |

### With AI coding agents

This is where codegraph shines: instead of grepping repo by repo, an agent (Claude Code, Cursor, Copilot, Codex,
...) asks the graph "what breaks if I change X?" and gets every caller and app with `file:line` to check.

- **Any MCP client** (Claude Code, Cursor, VS Code, ...): `codegraph mcp` serves the graph as tools - `search`,
  `show`, `impact`, `path`, `source`, `db_usage`, `text_search`, `report`, `status` (text for the agent plus `structuredContent` for
  programs). Register it once, e.g.
  `claude mcp add codegraph --scope user -- C:\src\codegraph\publish\codegraph.exe mcp --config C:\src\codegraph\codegraph.json`.
- **Claude Code:** `claude-setup\install.ps1` installs three skills: `/graph-ask` (answer "who uses / what breaks"
  questions from the graph, then check the source), `/graph-build` (rebuild and review the report) and
  `/graph-release` (record releases).
- **Any agent:** add a few lines to your workspace's `CLAUDE.md` / `AGENTS.md` telling it to run `show`, `impact`
  and `path` and verify the results; agents can also read `graph.json` directly.

The [agents guide](docs/agents.md) has the instructions snippet, the permissions to grant, and the `graph.json`
format.

## Your own file formats

Most codebases have in-house files that tie things together: report definitions (report → report file →
parameters → the procedures behind it), database patch lists, import mappings, menu or permission definitions.
Describe yours with a few rules in `codegraph.json` and they join the graph:

```jsonc
"extractors": [
  {
    "name": "report definitions",
    "files": ["Reports/*.xml"],
    "node": { "kind": "report", "name": "/ReportDefinition/Name" },
    "links": [
      { "relation": "uses_file",     "value": "/ReportDefinition/FileName" },                 // Sales.rpt
      { "relation": "has_parameter", "value": "//Parameter/@Field", "kind": "parameter", "to": "new" },
      { "relation": "reads",         "value": "//Table/@Name", "to": "db" },
      { "sql": "//InstallScript" },                                                         // parsed as T-SQL
    ],
    "referenced_by": ["uses_file"],   // code that names "Sales.rpt" gets an "opens" link to the report
  },
]
```

Values are picked with XPath (XML) or a simple path (JSON); links go to existing DB objects, projects, files,
symbols or releases, or create nodes of their own. Then `impact Orders` lists the reports reading it, `show
BusinessDay` the reports taking that parameter, and the vault gets an `Items/Reports` folder. The rules stay in your
own config, so the tool knows nothing about your format. Full reference and a second example (database patches
linked to releases): [extractors](docs/design/extractors.md).

## Adding your own knowledge

**Releases** - `releases.json`. Repos checked out on a release branch like `R2.4.0` are added automatically.

```jsonc
{
  "releases": [
    {
      "version": "2.3",
      "date": "2026-10-20",
      "notes": "Order export fix",
      "modules": { "Order Entry": "2.3.0", "BillingService": "2.3.0.0", "Database": "20300" },
    },
  ],
}
```

Module names match project names (VB6 Title/Name, .NET assembly, exe/dll name, npm package), top-level folders or
DB objects. `codegraph release-snapshot 2.4` appends an entry with every project's *current* version; delete the
modules that didn't ship. (It rewrites the file, so comments in it are lost.)

**Facts and notes** - `knowledge.json`. Shown on the generated notes:

```jsonc
{
  "facts": [
    ["Order Entry", "owned by", "Team Sales"],                       // "Team Sales" becomes a concept note
    ["BillingService", "documented at", "https://wiki.example.com/billing"],
  ],
  "notes": { "Orders": "Order_Status 1 = placed, 2 = shipped." },
}
```

**Your own notes in the vault** - write them anywhere outside the generated folders (e.g. `Notes/`) and link with
normal Obsidian links. The build never touches them.

- Generated (don't edit, rewritten on every build): the folders `Areas`, `Code`, `Database`, `Libraries`,
  `Projects`, `Releases`, `Solutions` and the notes `Home` and `Codegraph Report`. A build only rewrites or deletes
  notes listed in `.codegraph-manifest.json`.
- Note names: projects by name (`[[Order Entry]]`), DB objects by name (`[[Orders]]`, `[[sp_Get_Customer]]`), code
  files by file name (`[[modUtils.bas]]`). When a file name repeats, link with its path:
  `[[Code/Shop/ORDER ENTRY/frmMain.frm|Order Entry main form]]`. Typing `[[` in Obsidian autocompletes.
- Routines have no note of their own; they're listed in their file's note, so link the file and name the routine.
- Your links show up as **Backlinks** on the generated notes and in the graph view. Example `Notes/Order lifecycle.md`:

  ```markdown
  # Order lifecycle
  Orders live in [[Orders]]. Placing goes through [[sp_Order_Place]], shipping through
  [[sp_Order_Ship]]. Order Entry saves manual orders in [[modUtils.bas]] (SaveOrder). See also [[Order Entry]].
  ```

## What it understands

**Languages:** VB6 (`.vbp .vbg .frm .bas .cls .ctl`), VB.NET, C# (`.cs .csproj .sln .slnx`), C/C++ (`.cpp .h
.vcxproj .vcproj .dsp .def`), T-SQL (`.sql`, one or many objects per file, `.sqlproj`), PowerShell / batch,
TypeScript / JavaScript (`.ts .tsx .js`, `package.json` as a project, `tsconfig` path aliases), Vue single-file
components, Razor views (`.cshtml`), Rust, WiX installers (`.wixproj .wxs .wxi`).

**What it links:**
- app/project → the files it is built from (`.vbp` / `.csproj` / `.vcxproj` member lists, shared `..\Shared` files,
  SDK-style folders)
- project → project (project references, your own OCX/COM/DLL components, code that crosses projects) and →
  external libraries and packages
- routine → routine calls, `New` / instantiation, inheritance, type usage; C# calls through an interface also link
  to the implementing class, and a member links to the interface member it `implements` or the base member it
  `overrides` (so `impact` on an interface method reaches every implementation)
- code → database: SQL inline in VB6/C#/PowerShell/JS strings, stored-procedure names in strings (where the code
  uses a database API - ADO, ADO.NET, Entity Framework, ODBC, `sqlcmd` - or the name has one of your
  `proc_prefixes`; column names like `rs.Fields("x")` are not calls), ADO recordsets that `.Update`/`.AddNew`
- stored procedure / view / function / trigger → the tables they read, write and execute; table → table foreign keys
- columns: every column of a table is a node (`Orders.Total`) linked to the procedures, views, code, reports and
  patch scripts that read, write or alter it - `impact Orders.Total` before renaming or changing a column. A name is
  linked when it is qualified (`o.Total`) or only one table of the statement has such a column; never guessed.
- VB6 `Declare ... Lib "X.dll"` and C# `[DllImport("X.dll")]` → the function in your own X.dll project
- TS/JS/Vue: imports (relative, `@/`, tsconfig `paths`, lazy `import()`, re-exports), calls through imports,
  components used in `<template>`, imports of your own npm packages into their source
- front end → back end: `api.get('/orders')`, `fetch`, `$.ajax({url})`, or a URL passed to your own wrapper
  (linked when it matches a route) in TS/JS/Vue/Razor → the C# controller action serving that route
  (`[Route]`/`[HttpGet]` attributes; a base path like `/api` or `/v1` on either side is allowed); Razor
  `Url.Action("Edit", "Orders")` → `OrdersController.Edit`; URLs passed to `HttpClient` / `WebClient` / RestSharp in
  C# too
- client-side routes (Vue Router style tables): each route is a node that `routes_to` its component and
  `redirects_to` another route; `router.push`, `<router-link to>` and page links `links_to` the route or action
- MVC: controller action → its view, `@model` types, partials
- installers: which projects/files and third-party binaries each installer ships, features, services, custom actions
- releases from git branch names and from `releases.json`

## Configuration

`codegraph.json` is JSON with `//` comments and trailing commas allowed; paths are relative to the file.

| Setting | Meaning |
|---|---|
| `repos` | `{ "name", "path", "ignore": [...] }` for each repository |
| `vault` | Obsidian vault folder (leave out to skip the vault) |
| `output` | where `graph.json`, the report and the parse cache go (default `./codegraph-out`) |
| `ignore` | extra ignore patterns for all repos |
| `releases`, `releases_from_branches`, `knowledge` | your own knowledge files (see above) |
| `proc_prefixes` | optional stored procedure name prefixes, e.g. `["sp", "usp"]`: a string with one counts as a procedure name even away from database code (a module of constants), and is reported when no such procedure exists |
| `extractors` | rules for your own XML/JSON formats (see [Your own file formats](#your-own-file-formats)) |
| `external_ignore_extra` | library names not to show as external dependencies |
| `obsidian` | `folder` (sub-folder for generated notes), `max_routines_per_note`, `graph_colors` |

**Ignoring things.** Built in: `node_modules`, `.vs`, `dist`, `Vendor`/`ThirdParty` folders, `*.Designer.cs`,
generated WCF `Reference.cs`, `*.min.js` and copies of well-known libraries (`jquery-3.4.1.js`, `vue.runtime.js`, ...).
Build output is skipped only where a build puts it: `bin`/`obj` next to a project file, `Debug`/`Release`/`x64`/`x86`
next to a C++ project or solution, NuGet's `packages` next to a solution - so a source folder that happens to be
called `Release` or a JS monorepo's `packages/` is read. Only code file types are read at all (no images, docs or
binaries). Add patterns in `"ignore"` or in a `.codegraphignore` file in a repo root (an existing
`.graphifyignore` is also read). A bare name (`Documentation`) matches any folder/file with that name; a path with
`/` is matched from the repo root; `**/x` matches anywhere; `!pattern` re-includes.

## Limits

Parsing uses text patterns, not a compiler - that's what makes it fast and able to read VB6 and SQL scripts, but:
- Calls through untyped variables (VB6 `Object`/`Variant`, late binding, reflection) aren't followed.
- SQL or URLs assembled from many runtime pieces may be missed. Only names that exist in your code are linked.
- Same-named routines: matching prefers the same file, then the same project, then the projects it references.
  Matches that remain ambiguous are skipped rather than guessed (the report counts them).
- Developed and used on Windows. The tests also run on Linux and macOS in CI, but real-world use there is limited so far.

## Development

```powershell
dotnet test CodeGraph.slnx       # unit tests
.\publish.ps1                    # native exe
```

The code is in `src/CodeGraph` (C#, no runtime dependencies, Native AOT). `CLAUDE.md` describes the architecture:
scanning, the parsers, how references are resolved, and the outputs.

Releases: add a `## [x.y.z] - date` section to [CHANGELOG.md](CHANGELOG.md) (moving the entries from
`[Unreleased]`), then push a tag like `v0.2.0`. The Release workflow builds the native binaries for all platforms
and creates a GitHub release with that changelog section as its notes; it fails if the section is missing.
See the [roadmap](docs/ROADMAP.md) for what's planned.

## License

[MIT](LICENSE)
