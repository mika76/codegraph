---
name: graph-release
description: Record or review which modules ship in which release (releases.json), e.g. "add release 2.5", "what changed between releases", "which version of X is in release Y".
allowed-tools: Bash(./publish/codegraph.exe *) Read Edit
---

Request: $ARGUMENTS

- Releases come from: git branches named like `R2.4.0` (automatic), and `releases.json` (manual).
- To add a release: `./publish/codegraph.exe release-snapshot <version>` appends every project with its current source
  version to `releases.json`; then edit that entry with the user (remove modules that didn't ship, add notes/date),
  and run `./publish/codegraph.exe build`.
- Module names in releases.json match project names (VB6 Title/Name, .NET assembly, exe/dll name, npm package
  name), top-level folders (e.g. Database) or DB objects; unknown names produce a warning in the build output.
- To review: `./publish/codegraph.exe show "<version>"` lists the modules of a release; `show "<project>"` lists its releases.
  Installers: `show "<installer project>"` lists what it ships.
