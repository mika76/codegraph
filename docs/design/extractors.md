# Design: extractors for your own file formats

Status: **proposal** - not built yet. Comments welcome.

## Problem

Most codebases have in-house files that tie things together but are no programming language: report definitions
(report name → report file → parameters → the procedures behind it), import/export mappings, menu and permission
definitions, workflow or ETL descriptions. They hold exactly the links people ask about ("which reports break if I
change this procedure?", "which reports take a `BusinessDay` parameter?"), but every company's format is
different, so the tool can't ship a parser for each.

## Idea

Describe the format with a few rules in your own `codegraph.json`. The tool stays generic; your format stays in your
(private) config. Each rule set is an **extractor**: which files, what node each file (or each element) becomes,
and which values link to what.

```jsonc
"extractors": [
  {
    "name": "report definitions",
    "files": ["Reports/*.xml"],              // globs relative to each repo root (optional "repos": ["Shop"])
    "items": "/ReportDefinition",            // one node per match (default: the document root)
    "node": {
      "kind": "report",                      // any kind name; becomes a vault folder "Reports"
      "name": "Name",                        // relative to the item
      "attrs": { "group": "Group", "type": "ReportType" }   // shown on the note
    },
    "links": [
      { "relation": "uses_file",     "value": "FileName" },                         // Billing.rpt / Billing.repx
      { "relation": "has_parameter", "value": "Parameters/Parameter/@Field", "kind": "parameter" },
      { "relation": "executes",      "value": "DataSource/@Procedure" },             // a procedure name
      { "sql": "InstallScripts/InstallScript" }                                      // text parsed as T-SQL
    ]
  }
]
```

Example file this would read:

```xml
<ReportDefinition>
  <Name>Sales by Period</Name>
  <FileName>Sales by Period.rpt</FileName>
  <Group>Finance</Group>
  <Parameters>
    <Parameter Label="Business day" Field="BusinessDay" />
  </Parameters>
  <InstallScripts>
    <InstallScript><![CDATA[ CREATE PROCEDURE dbo.sp_REP_Sales ... SELECT ... FROM Orders ... ]]></InstallScript>
  </InstallScripts>
</ReportDefinition>
```

Result in the graph:

```text
report:sales by period ─defined_in→ file:Shop:Reports/Sales by Period.xml
   ├─uses_file→      Sales by Period.rpt
   ├─has_parameter→  parameter:businessday   (shared by every report that uses it)
   └─defines→        db:sp_rep_sales ─reads→ db:orders
```

so `impact Orders` lists the report, `show BusinessDay` lists every report that takes it, and the vault gets a
`Reports` folder.

## Rules

- **Paths**: XPath 1.0 for XML (`/a/b`, `//c`, `@attr`, `text()`; element names match without namespaces unless a
  `"namespaces": { "x": "uri" }` map is given). For JSON a small path syntax: `a.b`, `a[*].c`, `a.*`. A path may
  return several values; each gives a link.
- **Node ids**: `item:<kind>:<name>` (lower case), global like DB objects - two files describing the same report
  merge into one node, and a `parameter` used by many reports is one node.
- **Link targets** (`"to"`, default `auto`): `auto` tries, in order, an existing DB object, a project (name or
  output), a file of that name (the usual visibility/ambiguity rules; ambiguous = skipped), a symbol, then an
  existing extractor node. If nothing matches and the link has a `kind`, a node of that kind is created;
  otherwise it counts as unresolved in the report. `"to": "db" | "project" | "file" | "symbol" | "new"` forces one.
- **`uses_file` to files that aren't scanned** (binary `.rpt`, images): a lightweight `file_ref` node with the name,
  so the report file still shows up and two reports using the same file meet there.
- **`sql`**: each value is run through the T-SQL parser: `CREATE`/`ALTER` statements become DB objects (if the same
  object is also in your database scripts, those stay the main definition and this one shows as `alters`), other
  statements become reads/writes/executes from the extractor node.
- **Relations** are free-form names, but the built-in ones (`executes`, `reads`, `writes`, `calls`, `uses_type`,
  `mentions`, ...) keep their meaning in `impact` and the vault.
- **Cache**: an extractor's settings are part of the cache key of the files it matches, so changing a rule
  re-reads just those files.
- **Errors**: a file that doesn't match the expected shape is listed in the report under the extractor's name, not
  fatal.

## Open questions

1. **Linking from code.** UI code often opens a report by name (`ShowReport("Sales by Period")`) or file name.
   Code parsers already keep string literals; a `"referenced_by": "string"` option could link any code string equal
   to the node's name (or `uses_file` value) to the node. Precise, but only for exact strings - is that the link
   that matters most for you?
2. **One node per file or per element?** Covered by `items`; is there a case where one file holds many reports?
3. **Other formats**: plain text / INI / CSV rules (regex) - needed, or are XML and JSON enough?
4. **Escape hatch**: an external command that prints nodes and links as JSON, for formats rules can't describe -
   later, if needed.

## Size

About 2-3 days: config model, XPath/JSON-path evaluation, the builder step, vault folder per kind, report section,
tests (unit + a report-definition sample in the end-to-end codebase), docs and changelog.
