# codegraph vs graphify

[graphify](https://github.com/safishamsi/graphify) is a popular tool that also turns a codebase into a knowledge
graph for people and AI assistants. Both build a graph locally and answer "how is this connected" questions, but
they aim at different codebases. This page compares them so you can pick the right one, or use both.

*Based on graphify's README and FAQ as of October 2026; graphify changes quickly, so check its docs for the
current state.*

**In short:** graphify is a broad, general tool for code plus documents; codegraph is a specialist for legacy,
mixed-language, multi-repository business systems - VB6, T-SQL, WiX, project files, and the links between front
end, API and database.

## What each one is

**graphify** is a Python package (`graphifyy`) that installs as a skill in Claude Code, Cursor, Copilot and other
assistants. It parses code with tree-sitter (about 36 languages, locally, no LLM), sends documents, PDFs and images
through your assistant's model to extract concepts, and transcribes audio/video locally. It writes an interactive
`graph.html`, `graph.json` and a `GRAPH_REPORT.md`, optionally a wiki or a Neo4j export.

**codegraph** is a single native executable (C#, no runtime dependencies) that never calls an LLM. It has parsers
written for VB6, VB.NET, C#, C/C++, T-SQL, TypeScript/JavaScript/Vue, Razor, PowerShell/batch, Rust and WiX, reads
project and solution files, and links several repositories into one graph. It writes `graph.json`, a report and an
Obsidian vault.

## Side by side

| | graphify | codegraph |
|---|---|---|
| **Parsing** | tree-sitter grammars | hand-written regex/lexer parsers per language |
| **Languages** | ~36 (C#, C/C++, TS/JS, PHP, Go, Rust, ...) | 10: VB6, VB.NET, C#, C/C++, T-SQL, TS/JS/Vue, Razor, PowerShell/batch, Rust, WiX |
| **VB6** | no grammar | forms, modules, classes, controls, `Declare` → your own DLL |
| **SQL** | schemas treated as documents (model-extracted) | tables, views, procs, functions, triggers; reads/writes/executes; foreign keys; SQL inside code strings |
| **Project files** (.vbp/.csproj/.vcxproj/.sln) | not described | which files make up which app, project references, your own OCX/COM/DLL dependencies |
| **Installers (WiX)** | no | what each installer ships, features, services |
| **Front end → API** | not described | `fetch('/orders')` → the C# controller action that serves it |
| **Several repositories** | one graph per repo; `merge-graphs`, or the paid cloud product | one graph across all configured repos |
| **Docs, PDFs, images, video** | yes (docs and images via an LLM) | no - code only, by design |
| **LLM / data leaving the machine** | code: no; documents: yes, via your model | never |
| **Analysis** | communities (Leiden/Louvain), most-connected "god nodes", "surprising connections" | report: unused DB objects, orphan files, missing project members, procs called but not found |
| **Uncertain links** | every edge tagged `EXTRACTED` or `INFERRED` | ambiguous matches are skipped, never guessed |
| **Browsing** | interactive HTML (aggregates above 5,000 nodes), wiki, Neo4j | Obsidian vault where your own notes link into the graph |
| **Queries** | query / path / explain; optional MCP server | show / impact / path; no MCP server yet |
| **Keeping it current** | `--update`, watch mode, git hooks | incremental parse cache (seconds), run on demand |
| **Your own knowledge** | rationale comments (`# WHY:`) and ADR references become nodes | releases (which module shipped in which version), facts and notes |
| **Install** | Python 3.10+, uv or pipx | one executable, nothing else |
| **Maturity** | widely used, many contributors | young project, tested on Windows only |

## Where graphify is the better choice

- **Language breadth and accuracy.** Many more languages, parsed with real syntax trees rather than patterns, so
  fewer constructs are missed in the languages it supports.
- **Documents alongside code.** Design docs, PDFs, diagrams and recordings end up in the same graph.
- **Analysis and visuals.** Community detection, "god nodes", an interactive HTML graph, watch mode, git hooks
  and an MCP server.
- **Ecosystem.** Works with many assistants and has an active community.

## Where codegraph is the better choice

- **Legacy languages.** VB6 and T-SQL objects are first-class; WiX installers and old project formats (`.vbp`,
  `.vbg`, `.vcproj`, `.dsp`) are read. In a codebase built on these, a general tool sees little of the system.
- **The database as part of the graph.** Which form, service, API or procedure reads or writes which table -
  including SQL hidden in string literals and ADO recordsets. In data-centric systems that is usually *the*
  impact question.
- **The whole system across repositories.** Front-end call → API route → repository → stored procedure → table;
  VB6 → your own C++ DLL; installer → the projects it ships.
- **Apps and releases.** Files are grouped by the project that builds them (including files shared between
  apps), impact is grouped by application, and release history comes from branch names and `releases.json`.
- **Nothing leaves the machine, nothing to install.** No LLM, no Python, no network.

## codegraph's limits

- Pattern-based parsing: calls through untyped variables (VB6 late binding, reflection) and URLs or SQL built at
  runtime are not followed. A typical first run resolves around a third of all references - the rest are mostly
  framework calls and local variables, but some real links are missed.
- No clustering, HTML viewer, MCP server or watch mode (yet).

## Using both

They don't get in each other's way. graphify can cover documents and the modern parts of a system with its
visual graph; codegraph covers the legacy code, the database and the cross-repo "what breaks if I change this"
questions. Ideas codegraph may borrow: an MCP server over `graph.json`, and community detection to show app
boundaries in the report.

## Sources

- [graphify on GitHub](https://github.com/safishamsi/graphify)
- [graphify FAQ](https://graphify.com/faq)
- [Augment: graphify knowledge graph codebase skill](https://www.augmentcode.com/learn/graphify-knowledge-graph-codebase-skill)
- [emelia.io: A complete guide to graphify](https://emelia.io/hub/knowledge-graph-graphify-guide)
