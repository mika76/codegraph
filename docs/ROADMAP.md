# Roadmap

Where codegraph is heading, roughly in order. Each item says why it matters; sizes are rough
(S = hours, M = a day or two, L = a week or more). Ideas and pull requests are welcome.

## Now - first public release

- [x] **Publish on GitHub** with a licence (MIT), CI on Windows, Linux and macOS.
- [x] **Ready-built binaries.** A tag (`v*`) builds native executables for Windows, Linux and macOS (x64 and
  ARM64) and attaches them to a GitHub release, so nobody needs the .NET SDK or C++ build tools to use it.
- [x] **End-to-end test** over a small made-up codebase (VB6 app, SQL scripts, C# API, Vue front end, installer)
  that checks the important cross-language links. Unit tests alone can't catch a parser change that quietly
  breaks linking.

## Next - better answers

- [x] **MCP server over `graph.json`** (built, unreleased). `codegraph mcp` lets Claude, Cursor and other
  assistants query the graph directly (`search`, `show`, `impact`, `path`, `source`) instead of shelling out and
  reading text. See the [agents guide](agents.md#mcp-server-the-graph-as-tools).
- **Link more of what's there** (M-L). A typical first run resolves about a third of all references. Targets:
  - URLs built at runtime: follow simple string concatenation and constants
    (`baseUrl + '/orders/' + id`, jsonp helpers), so more front-end calls reach their API action.
  - VB6 late binding: infer an `Object` variable's type from `CreateObject("Lib.Class")` and `Set x = New Class`.
  - A "why didn't this link?" section in the report: the most common unresolved names per kind, to show which
    parser to improve next.
- **`packages.config`** (S). Read the NuGet packages and versions of old-style .NET projects and attach them to
  the project in the same folder, so "which apps use library X, and which version?" works for them too.
- [x] **Your own file formats** (built, 0.2.0). Declarative extractors in `codegraph.json` read
  in-house XML/JSON files (report definitions, patch lists, mappings) into nodes and links - see
  [extractors](design/extractors.md). Not built: an "external command" escape hatch, only if a format turns up that
  rules can't describe.
- [x] **Enums and constants** (built, 0.2.0). VB6 `Enum` blocks; enum members (VB6, VB.NET, C#, TS) and constants (VB6 `Const`,
  C# `const`) as nodes with their values, linked from the code that uses them (`uses_value`) - including VB6's
  unqualified enum members - so "who uses `OrderStatus.Shipped`?" works, and SQL kept in a constant links its
  tables to the code that uses the constant.
- **Release diff** (M). `codegraph diff 2.3 2.4` (two releases, or two branches/graphs): modules, tables,
  procedures and API routes added, removed or changed. Builds on the release tracking that already exists.

## Later

- **Watch mode / git hooks** (S): rebuild automatically after pull, commit or branch switch.
- **Community detection** (M): find clusters in the graph and show in the report where application boundaries
  really are, compared with where the project files say they are.
- **HTML viewer** (M): a single-file interactive graph for people who don't use Obsidian.
- **Cross-platform polish** (S-M): path and case-sensitivity edge cases on Linux/macOS, beyond what CI covers.

## Maybe

- **More legacy languages** when someone needs them - Delphi/Pascal, COBOL, PL/SQL, Classic ASP - in the spirit of
  "code general tools can't read". Mainstream languages are already well served by tree-sitter based tools
  (see [codegraph vs graphify](codegraph-vs-graphify.md)).
- **Export** to other graph tools (GraphML, Neo4j).
- **More formats for your own extractors** - INI, YAML, CSV, plain text with regex rules - next to XML and JSON.

## Not planned

- Sending code to an LLM, or any network access. codegraph stays local and deterministic.
- Reading documents, images or video - other tools do that well.
