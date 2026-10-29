# Design: compare a live database with the repository scripts

Status: **idea, not built**. This page records the design so it can be built later.

## Problem

The graph knows the database as the repositories describe it: one `CREATE` script per object, patch scripts that
`ALTER` it, inline SQL in code. A real database drifts from that: a hotfix applied by hand, a patch that was never
run, a column added on one site only, an object dropped in the scripts but still there. A tool that edits or browses
a live database (for example an SQL editor that runs `codegraph mcp` for its "used by" panel) wants to say:

- this table exists in the database but no script creates it (unknown to the code, impact unknown);
- this column is in the script but not in the database (a patch was not applied, or code will fail);
- this procedure's text differs from its script (changed by hand);
- this object is in the database and used by code, so dropping it breaks these places.

## Principle: codegraph does not connect to databases

codegraph stays offline and deterministic: no drivers, no credentials, no network, nothing that depends on the
machine it runs on. The live side is the caller's job. The program that already has a connection reads the
catalog and hands codegraph a **snapshot**; codegraph compares that snapshot with the graph.

This keeps the tool AOT-friendly (no ADO.NET provider), keeps secrets out of `codegraph.json`, and makes every
comparison reproducible from a file.

## Snapshot format

A JSON file (or the same object passed to an MCP tool), one per database:

```jsonc
{
  "database": "Sales",            // matched against the "database" setting of repos / folders
  "server": "db01",               // free text, only shown
  "taken": "2026-01-02T03:04:05Z",
  "objects": [
    {
      "schema": "dbo", "name": "Orders", "type": "table",           // table, view, proc, function, trigger, synonym
      "columns": [ { "name": "Id", "type": "int", "nullable": false }, { "name": "Total", "type": "money" } ]
    },
    { "schema": "dbo", "name": "usp_Save", "type": "proc", "definition_hash": "sha256:..." }
  ]
}
```

The caller fills it from the catalog views (`sys.objects`, `sys.columns`, `sys.sql_modules` on SQL Server;
`information_schema` elsewhere). `definition_hash` is a hash of the module text after the same normalisation
codegraph applies (below), so the snapshot does not have to carry whole procedure bodies.

## Matching

- Object keys use `SqlText.ObjectKey` (database and default schema dropped, lower case), the same keys as `db:`
  nodes. A snapshot with `database` is compared only with nodes of that database when the config names databases;
  otherwise with all `db:` nodes.
- Columns: `col:<table>.<column>` nodes against the snapshot's columns; type text compared after normalising case,
  spaces and `[]`.
- Definitions: the script's `CREATE` / `ALTER` body for that object, with comments and whitespace collapsed (the
  `Scrub` pass) and `CREATE OR ALTER` / `ALTER` / `CREATE` treated alike, hashed the same way as the snapshot.
  This needs the build to keep a `definition_hash` attribute on module nodes (procs, views, functions, triggers);
  that is the only graph change.

## Output

A diff with one entry per finding, each pointing at graph nodes so the existing tools apply:

| kind | meaning | links |
|---|---|---|
| `only_in_database` | object or column with no script | none in code; `text_search` may still find users |
| `only_in_scripts` | script creates it, database lacks it | `db_usage` users that would fail |
| `column_type_differs` | same column, different type | the column's users |
| `definition_differs` | proc / view / function text changed | the node, its script file, its callers |
| `same` | counted only | |

Each entry carries the node (structured `node` shape), the script file and line, the snapshot's values, and for the
"breaks" cases the `counts` from `db_usage`, so a UI can show "missing in the database: 3 columns, used in 27 places".

## Interface

- MCP tool `db_compare` with `snapshot` (an object or a path to a snapshot file) and optional `database`;
  `structuredContent` = `{ database, taken, counts: {...}, findings: [...] }`.
- CLI `codegraph db-compare <snapshot.json>` printing the same as text, for CI ("are the scripts in sync with the
  reference database?").

## Open questions

- Several databases with objects of the same name are one `db:` node today; a proper compare per database needs
  database-qualified ids (`db:<database>:<key>`), which changes ids everywhere - decide before building.
- Patch scripts: whether "applied" can be told from a version table in the snapshot (a list of applied patch ids)
  and the patch items the extractors already produce.
- Normalisation of procedure text must match the database's stored text closely enough (SET options, trailing
  `GO`, header comments); start with a hash and fall back to "differs, check" rather than a false "same".
