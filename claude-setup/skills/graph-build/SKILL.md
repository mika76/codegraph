---
name: graph-build
description: Rebuild the code knowledge graph and Obsidian vault for the configured repos and review the report for problems. Use after code changes, after pulling, or when the vault looks stale.
allowed-tools: Bash(./publish/codegraph.exe *)
---

1. Run `./publish/codegraph.exe build` from the codegraph folder (add `--full` if parsers were changed or results look stale).
2. Read `codegraph-out/GRAPH_REPORT.md` and summarise briefly for the user:
   - counts (files per language, projects, DB objects) and references resolved %,
   - parse errors, unmatched API calls, projects with missing members, orphan source files,
   - "Knowledge to review": stale or redundant facts/notes - say how many and offer `/graph-review-knowledge`,
   - anything that looks wrong (e.g. vendored/third-party code that should be ignored, duplicate repos).
3. If something should be ignored, propose the exact pattern for `"ignore"` in `codegraph.json` (ask before editing).
4. Mention the vault path (`vault` in `codegraph.json`) and that Obsidian picks up changes automatically.
