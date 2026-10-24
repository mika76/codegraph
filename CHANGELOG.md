# Changelog

All notable changes to codegraph are listed here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is 0.x, minor versions may change
the config format or `graph.json` layout; such changes are called out below.

Sections: ✨ Added · 🔄 Changed · ⚠️ Deprecated · 🗑️ Removed · 🐛 Fixed · 🔒 Security

## [Unreleased]

### 🐛 Fixed
- 🔧 Visual C++ 6 projects (`.dsp`) take their output file from the linker's `/out:` (Release configuration first), so an
  ActiveX control linked as `.ocx` is an `ocx` project and matches the installers and VB6 references that use it.

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
