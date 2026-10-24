# Design: extractors for your own file formats

Status: **built** (in the next release). This page is the reference for the `extractors` setting.

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

so `impact Orders` lists the report, `show BusinessDay` lists every report that takes it, and the vault gets an
`Items/Reports` folder (item notes live under `Items/<Kind>s`, so they never mix with your own folders).

## Rules

- **Paths**: XPath 1.0 for XML (`/a/b`, `//c`, `@attr`, `text()`; element names match without namespaces unless a
  `"namespaces": { "x": "uri" }` map is given). For JSON a small path syntax: `a.b`, `a[*].c`, `a.*`. A path may
  return several values; each gives a link.
- **Node ids**: `item:<kind>:<name>` (lower case), global like DB objects - two files describing the same report
  merge into one node, and a `parameter` used by many reports is one node.
- **Link targets** (`"to"`, default `auto`): `auto` tries, in order, an existing DB object, a project (name or
  output), a file of that name (the usual visibility/ambiguity rules; ambiguous = skipped), a symbol, then an
  existing extractor node. If nothing matches and the link has a `kind`, a node of that kind is created;
  otherwise it counts as unresolved in the report. `"to": "db" | "project" | "file" | "symbol" | "release" | "new"` forces one. `release` links to the release
  node of that version (`24.00.00` becomes `24.0.0`), creating it if no release file or branch has it yet.
- **`pattern` / `replace`** on a link: a regex each value must match (others are skipped), optionally rewriting
  it - e.g. `"^(\d\d)(\d\d)(\d\d)$"` → `"$1.$2.$3"` turns `353800` into release `35.38.0`, `"^([^;]+)(;\d+)?$"` → `"$1"`
  strips a `;1` suffix.
- **`files`** patterns work like the ignore rules (bare name anywhere, `/` from the repo root, `!` excludes).
  Files an extractor reads are not subject to `max_file_kb`.
- **`uses_file` to files that aren't scanned** (binary `.rpt`, images): a lightweight `file_ref` node with the name,
  so the report file still shows up and two reports using the same file meet there.
- **`sql`**: each value is run through the T-SQL parser: `CREATE`/`ALTER` statements become DB objects (if the same
  object is also in your database scripts, those stay the main definition and this one shows as `alters`), other
  statements become reads/writes/executes from the extractor node.
- **Relations** are free-form names, but the built-in ones (`executes`, `reads`, `writes`, `calls`, `uses_type`,
  `mentions`, ...) keep their meaning in `impact` and the vault.
- **Cache**: an extractor's settings are part of the cache key of the files it matches, so changing a rule
  re-reads just those files.
- **Errors**: a file that doesn't match the expected shape (e.g. no value for the node name) is listed under "Parse
  errors" in the report with the extractor's name - not fatal. Exclude such files with a `!` pattern in `files`.
- **Report**: the section "Your own file formats" counts items per extractor and kind, and lists link values that
  matched nothing.

## Decisions

1. **Linking from code: yes, as an option.** Application reports are source code too, so a screen that opens a
   report by name or file name (`ShowReport("Sales by Period")`, `"Sales by Period.rpt"`) should link to it.
   An extractor may set `"referenced_by": ["name", "uses_file"]`: code string literals exactly equal (ignoring
   case) to the node's name or one of its `uses_file` values become `opens` edges from the routine containing
   the string. Exact matches only - no guessing from partial strings.
   Tip: `uses_file` (file names such as `Sales.rpt`) is precise; `name` also matches captions and labels that
   happen to read like a report name, so check the results before relying on it.
2. **One node per file** is the normal case (one definition file per report file, e.g. Crystal `.rpt` or
   DevExpress `.repx`); `items` stays for formats that hold several things per file, defaulting to the root.
3. **XML and JSON only** for now. Other structured formats (INI, YAML, CSV, plain text with regex rules) are an
   optional later item on the roadmap.
4. **No external-command escape hatch** until a format turns up that rules can't describe.

Note: report files that are themselves XML (DevExpress `.repx`) can be read by an extractor of their own (data
sources, parameters, SQL); binary ones (Crystal `.rpt`) appear as `file_ref` nodes through `uses_file`.

## Size

About 2-3 days: config model, XPath/JSON-path evaluation, the builder step, vault folder per kind, report section,
tests (unit + a report-definition sample in the end-to-end codebase), docs and changelog.

## Example 2: database patches as releases

A patch file that lists every database upgrade with its version and SQL:

```xml
<PatchRepository>
  <Patches>
    <Patch version="240100" created="2025-03-01T10:00:00"><![CDATA[ ALTER TABLE dbo.Orders ADD Notes nvarchar(200) ]]></Patch>
  </Patches>
</PatchRepository>
```

```jsonc
{
  "name": "db patches",
  "files": ["Database/patches.xml"],
  "items": "/PatchRepository/Patches/Patch",          // one node per patch
  "node": { "kind": "patch", "name": "@version", "attrs": { "created": "@created" } },
  "links": [
    // 240100 -> release 24.1.0 (created when missing, merged with releases.json / release branches)
    { "relation": "released_in", "value": "@version", "to": "release", "pattern": "^(\d\d)(\d\d)(\d\d)$", "replace": "$1.$2.$3" },
    { "sql": "." },                                     // the patch's own SQL
  ],
}
```

Each table and procedure note then lists the patches that created or altered it, and each release note lists its
patches.
