# Using codegraph with AI coding agents

AI coding agents (Claude Code, Cursor, Copilot, Codex, Gemini CLI, Aider, ...) normally learn a codebase by
searching it file by file. That works inside one project, but falls apart for questions like "what breaks if I
change this stored procedure?" when the answer is spread over VB6 forms, a C# API, a Vue front end and SQL scripts
in several repositories - the agent greps for a name, misses SQL inside strings, can't see which app a shared file
is compiled into, and burns a lot of context doing it.

codegraph gives the agent the answer in one command: every caller, reader, writer and app, with file and line
numbers to check. The agent then reads only the few files that matter.

## How an agent should use it

1. **Ask the graph first** (from the folder with `codegraph.json`):

   | Question | Command |
   |---|---|
   | What is X, where is it defined, what does it use, what uses it? | `codegraph show "X"` |
   | What breaks if I change X? (grouped by application) | `codegraph impact "X" --depth 3` |
   | How does A reach B? | `codegraph path "A" "B"` |

   `X` can be a project, file, routine (`frmOrder.cmdPlace_Click`, `OrderRepository.GetById`), table, stored
   procedure, view, API route or installer. If a name matches several things (many apps have a `frmMain`), the
   tool lists the candidates with their ids and answers for the first one - so check which one it used, and
   repeat the command with the right path or id.
2. **Verify in the source.** Every result has `path:line`. Open the important ones before answering or editing:
   the parsers are pattern-based, so a link can occasionally be wrong, and some links are missing (see below).
3. **Mention the blind spots** when they matter: calls through untyped variables (VB6 `Object`/`Variant`, late
   binding, reflection), SQL or URLs assembled at runtime, and anything ambiguous (skipped, never guessed).
4. **Rebuild after changing code**: `codegraph build` takes a few seconds when little changed.

## Claude Code: the bundled skills

`claude-setup/` contains three skills. Install them into the codegraph folder's `.claude/`:

```powershell
.\claude-setup\install.ps1
```

| Skill | What it does |
|---|---|
| `/graph-ask <question>` | answers "who uses / what breaks / how is it connected" questions with `show` / `impact` / `path`, checks the source lines, and answers grouped by app with `file:line` references and blind spots |
| `/graph-build` | rebuilds the graph and summarises `GRAPH_REPORT.md`: resolved %, parse errors, unmatched API calls, orphan files; proposes ignore patterns for vendored or generated folders |
| `/graph-release <request>` | records or reviews releases: `release-snapshot`, editing `releases.json`, "which version of X is in release Y" |

The installer also adds a `settings.json` that allows `./publish/codegraph.exe *` without a permission prompt.
The skills expect the executable at `publish/codegraph.exe` inside the codegraph folder - where `publish.ps1` puts
it. If you downloaded a release, unzip it into `publish/` (or change the path in the skill files).

Skills only load in sessions started in the codegraph folder. To use the graph from sessions in your code
repositories too, add instructions as described next.

## Any agent: add instructions to the workspace

Agents read an instructions file at the start of a session: `CLAUDE.md` (Claude Code), `AGENTS.md` (Codex,
Cursor, Copilot, Gemini CLI and others), `.cursor/rules/*.mdc` (Cursor), `.github/copilot-instructions.md`
(Copilot). Put a short block like this in the file at the root of the folder that holds your repositories, so
every session opened there knows about the graph:

```markdown
## Code graph
A cross-repo code graph of these repositories is maintained with codegraph in `C:\src\codegraph`.
For "who uses / what breaks / how is X connected" questions, run from `C:\src\codegraph`:
`.\publish\codegraph.exe show "<name>"`, `.\publish\codegraph.exe impact "<name>"`,
`.\publish\codegraph.exe path "<a>" "<b>"` - then verify the important results in the source before answering.
After code changes, rebuild with `.\publish\codegraph.exe build`.
Blind spots: late binding / untyped variables, SQL and URLs built at runtime.
```

Give the agent permission to run the executable without asking (in Claude Code, an `allow` rule such as
`Bash(./publish/codegraph.exe *)` in `.claude/settings.json`), since it only reads code and writes to its own
output folder and vault.

## Reading `graph.json` directly

For questions the commands don't cover, an agent can load `codegraph-out/graph.json` with a short script.
It has three parts:

```jsonc
{
  "meta":  { "generated": "...", "repos": [...], "stats": {...}, "unresolved": {...} },
  "nodes": [ { "id": "db:orders", "kind": "db_table", "name": "Orders", "file": "file:Shop:Database/Tables/dbo.Orders.sql", "line": 1, ... } ],
  "edges": [ { "source": "sym:Shop:OrderEntry/frmOrder.frm#frmOrder.cmdPlace_Click", "target": "db:orders", "relation": "reads", "count": 2, "line": 41 } ]
}
```

- **Node ids**: `repo:<name>`, `area:<repo>:<top folder>`, `proj:<repo>:<project file>`, `sln:<repo>:<file>`,
  `file:<repo>:<path>`, `sym:<repo>:<path>#<Type>.<Member>`, `db:<object>` (lower case, without `dbo.`),
  `ext:<library>`, `rel:<version>`, `concept:<name>`.
- **Common node fields**: `kind`, `name`, `repo`, `path` (files, projects), `file` + `line` (symbols, DB objects),
  `lang`, `signature`, `projects` (files: the projects that build them), `routes` (controller actions).
- **Edge relations**: `contains`, `calls`, `instantiates`, `uses_type`, `inherits`, `calls_native`, `includes`,
  `imports`, `uses_component`, `calls_api`, `renders`, `mentions`, `uses_value` (enum members, constants),
  `reads`, `writes`, `executes`, `references` (foreign key), `triggers_on`, `defines`, `alters`, `depends_on`,
  `uses_library`, `uses_code_of`, `ships`, `released_in`, and for your own formats `defined_in`, `opens` and
  whatever relations your extractors name. `count` is how often it occurs; `line` the first place; `via` extra detail (e.g. `recordset`,
  `implementation`, the raw URL of an API call).

Example - every routine that writes to the `Orders` table, in PowerShell:

```powershell
$g = Get-Content codegraph-out\graph.json -Raw | ConvertFrom-Json
$g.edges | Where-Object { $_.target -eq 'db:orders' -and $_.relation -eq 'writes' } | Select-Object source, line
```

or with `jq`:

```bash
jq -r '.edges[] | select(.target == "db:orders" and .relation == "writes") | .source' codegraph-out/graph.json
```

## Coming next

An MCP server over the graph is on the [roadmap](ROADMAP.md): agents will then be able to query it as a tool
without shelling out to the executable.
