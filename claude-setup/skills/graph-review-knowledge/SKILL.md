---
name: graph-review-knowledge
description: Review the code graph's knowledge files (facts and notes in knowledge.json and each repo's .codegraph/knowledge.json) - re-check them against the current code and propose fixes or removals for stale, wrong or redundant entries. Use after big refactors or renames, when the build report lists "Knowledge to review", or as a periodic clean-up.
allowed-tools: mcp__codegraph Bash(./publish/codegraph.exe *) Read Grep Edit
---

Scope: $ARGUMENTS (empty = all knowledge files; or a repo name, a file, or "report only")

Work from the codegraph folder (where `codegraph.json` is):
1. Run `./publish/codegraph.exe build` so the graph matches the code, then read the "Knowledge to review" section of
   `codegraph-out/GRAPH_REPORT.md` (or the MCP `report` tool, section "knowledge"). Those entries come first:
   names that matched a node at the last build and match nothing now (renamed or removed), paths not in the graph,
   and facts the graph now links by itself (usually safe to remove).
2. Unless the scope is "report only", list the knowledge files: the `knowledge` entries in `codegraph.json`, and in
   each repo the `repo_knowledge` names (default `.codegraph/knowledge.json`). Go through their facts and notes,
   oldest first (`remember` writes the source and date as a comment above each entry; entries without a date are
   oldest). For each one, check it against the code with `show` / `source` (or `./publish/codegraph.exe show`) and
   by reading the files it names:
   - **keep**: still true (optionally refresh its comment to `// re-checked <date>`),
   - **fix**: the thing was renamed or moved, or the text is partly out of date - give the corrected entry,
   - **remove**: no longer true, or the graph now shows the same link,
   - **ask**: you can't tell from the code (a business rule, an owner, a reason) - only a person can confirm it.
3. Show the user one table per file (entry, verdict, reason with file:line) and wait for their approval. Do not
   edit anything before that.
4. Apply only the approved changes with Edit, keeping the files' comments and layout (they are JSON with
   comments). Never move entries between repos without asking. Then build again and confirm that the "Knowledge
   to review" section only lists what the user chose to keep.
5. Remind the user to commit the changed knowledge files in their repos.
