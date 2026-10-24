# Changelog

All notable changes to codegraph are listed here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is 0.x, minor versions may change
the config format or `graph.json` layout; such changes are called out below.

Sections: ✨ Added · 🔄 Changed · ⚠️ Deprecated · 🗑️ Removed · 🐛 Fixed · 🔒 Security

## [Unreleased]

### ✨ Added
- 🤖 Guide for using codegraph with AI coding agents (`docs/agents.md`).
- 📝 This changelog; release notes are now taken from it.

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

[Unreleased]: https://github.com/mika76/codegraph/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/mika76/codegraph/releases/tag/v0.1.0
