---
name: graph-ask
description: Answer questions about our codebase (who uses X, what breaks if I change Y, how is A connected to B, which screens call this API / table / proc) using the code graph instead of grepping the repos. Use for impact analysis and cross-repo questions about VB6, C#, SQL, Vue, installers.
allowed-tools: mcp__codegraph Bash(./publish/codegraph.exe *) Read Grep Edit
---

Question: $ARGUMENTS

Work from the codegraph folder (where `codegraph.json` is):
1. Identify the thing(s) asked about (project, file, routine, table/proc, column like `Orders.Total`, API route,
   client route, installer).
2. Use the graph first. If the `codegraph` MCP tools are available, use them:
   `search` (find the node and its id), `show` (connections; `relation` + `direction` to filter, e.g. who writes
   a table), `impact` (what depends on it), `path` (how two things connect), `source` (read the code),
   `db_usage` (who reads / writes a table, column or proc, its possible unlinked uses, and the screens and API
   endpoints above them), `text_search` (plain text matches, for SQL built at runtime; unlinked hits are what the
   graph missed). When a name matches several things or nothing they return the candidate ids - call again with
   the right id (names are exact; use * as a wildcard). Otherwise use the executable:
   - `./publish/codegraph.exe show "<name>"` - definition, version, all incoming/outgoing links.
   - `./publish/codegraph.exe impact "<name>" --depth 3` - everything that depends on it, grouped by app.
   - `./publish/codegraph.exe path "<a>" "<b>"` - how two things connect.
   - `./publish/codegraph.exe db-usage "<table, column or proc>"` - its users, possible users and entry points.
   Names are exact (use * as a wildcard); for several matches or none the tool lists candidates - re-run with the
   path or node id.
   For bigger questions, load `codegraph-out/graph.json` with a short PowerShell script (`ConvertFrom-Json`; nodes/edges, see CLAUDE.md) - Python is not installed.
3. Verify the important links by opening the actual source lines (paths + line numbers are in the output),
   because the parser is pattern-based and can miss late-bound/dynamic calls.
4. Answer concisely: the direct answer, grouped by app/repo, with file:line references, and note any
   blind spots (dynamic SQL, VB6 late binding, URLs built at runtime).
5. Record what the graph didn't know. If you established something it doesn't show (a link it misses, what a
   table, setting or screen is for, a rule the user told you), propose it as a fact or note at the end of your
   answer. On yes, call the MCP `remember` tool (subject; relation + object, or note; source = where you verified
   it). Without MCP, add it by hand to the repo's `.codegraph/knowledge.json` (or the central knowledge file),
   keeping its comments. Record only what you checked or were told, never links the graph already has; a kind of
   link that is missing again and again is a parser gap to mention, not facts to record one by one.
