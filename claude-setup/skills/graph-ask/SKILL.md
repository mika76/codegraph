---
name: graph-ask
description: Answer questions about our codebase (who uses X, what breaks if I change Y, how is A connected to B, which screens call this API / table / proc) using the code graph instead of grepping the repos. Use for impact analysis and cross-repo questions about VB6, C#, SQL, Vue, installers.
allowed-tools: Bash(./publish/codegraph.exe *) Read Grep
---

Question: $ARGUMENTS

Work from the codegraph folder (where `codegraph.json` is):
1. Identify the thing(s) asked about (project, file, routine, table/proc, API route, installer).
2. Use the graph first:
   - `./publish/codegraph.exe show "<name>"` - definition, version, all incoming/outgoing links.
   - `./publish/codegraph.exe impact "<name>" --depth 3` - everything that depends on it, grouped by app.
   - `./publish/codegraph.exe path "<a>" "<b>"` - how two things connect.
   If a name is ambiguous the tool lists candidates; re-run with the path or node id.
   For bigger questions, load `codegraph-out/graph.json` with a short PowerShell script (`ConvertFrom-Json`; nodes/edges, see CLAUDE.md) - Python is not installed.
3. Verify the important links by opening the actual source lines (paths + line numbers are in the output),
   because the parser is pattern-based and can miss late-bound/dynamic calls.
4. Answer concisely: the direct answer, grouped by app/repo, with file:line references, and note any
   blind spots (dynamic SQL, VB6 late binding, URLs built at runtime).
