using System.Globalization;
using CodeGraph.Model;
using CodeGraph.Scan;

namespace CodeGraph.Output;

/// <summary>Markdown health report: counts, hubs, most-used DB objects and things that look wrong.</summary>
public static class Report
{
    public static readonly string[] DbKinds = ["db_table", "db_view", "db_proc", "db_function", "db_trigger", "db_type", "db_synonym"];

    private static string Cell(object? c) =>
        Convert.ToString(c, CultureInfo.InvariantCulture)!.Replace("|", "\\|").Replace("\n", " ");

    private static List<string> Table(string[] headers, IReadOnlyCollection<object?[]> rows)
    {
        if (rows.Count == 0) return ["_none_", ""];
        var res = new List<string>
        {
            "| " + string.Join(" | ", headers) + " |",
            "|" + string.Join("|", headers.Select(_ => "---")) + "|",
        };
        res.AddRange(rows.Select(r => "| " + string.Join(" | ", r.Select(Cell)) + " |"));
        res.Add("");
        return res;
    }

    private static List<object?[]> Rows<T>(IEnumerable<T> items, Func<T, object?[]> f) => items.Select(f).ToList();

    private static string Fmt(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string BuildReport(Graph g, IReadOnlyDictionary<string, int> stats, IReadOnlyDictionary<string, int> unresolved,
        IReadOnlyList<ParseError> errors, IReadOnlyList<string> warnings)
    {
        var v = new Views(g);
        var nodes = g.Nodes;
        stats ??= new Dictionary<string, int>();
        var o = StringComparer.Ordinal;
        var outp = new List<string> { "# Codegraph Report", "" };

        void Sec(string title, List<string> lines, string? intro = null)
        {
            outp.Add($"## {title}");
            outp.Add("");
            if (intro is not null) { outp.Add(intro); outp.Add(""); }
            outp.AddRange(lines);
        }

        // ---- summary
        var kinds = new Counter<string>();
        foreach (var n in nodes) kinds.Add(n.Kind);
        var rels = new Counter<string>();
        foreach (var e in g.Edges) rels.Add(e.Relation);
        var langs = stats.Where(kv => kv.Key.StartsWith("files_", StringComparison.Ordinal))
            .Select(kv => (Name: kv.Key[6..], Count: kv.Value)).OrderByDescending(x => x.Count).ToList();
        int tot = stats.GetValueOrDefault("refs_total"), res = stats.GetValueOrDefault("refs_resolved");
        double pct = tot > 0 ? 100.0 * res / tot : 0;
        var lines = new List<string>
        {
            $"- Source files: {langs.Sum(x => x.Count)}  |  nodes: {nodes.Count}  |  edges: {g.Edges.Count}",
            $"- References resolved: {Fmt(res)} of {Fmt(tot)} ({pct.ToString("F1", CultureInfo.InvariantCulture)}%) - the rest are mostly " +
            "framework/library calls and local variables that have no target in the scanned code",
            "",
            "**Files per language**",
            "",
        };
        lines.AddRange(Table(["Language", "Files"], Rows(langs, x => [x.Name, x.Count])));
        lines.AddRange(["**Nodes per kind**", ""]);
        lines.AddRange(Table(["Kind", "Count"], Rows(kinds.MostCommon(), kv => [kv.Key, kv.Value])));
        lines.AddRange(["**Edges per relation**", ""]);
        lines.AddRange(Table(["Relation", "Count"], Rows(rels.MostCommon(), kv => [kv.Key, kv.Value])));
        var extra = stats.Where(kv => !kv.Key.StartsWith("files_", StringComparison.Ordinal) && !kv.Key.StartsWith("refs_", StringComparison.Ordinal)
                                      && !kinds.ContainsKey(kv.Key))
            .OrderBy(kv => kv.Key, o).ToList();
        if (extra.Count > 0)
        {
            lines.AddRange(["**Other counters**", ""]);
            lines.AddRange(Table(["Counter", "Value"], Rows(extra, kv => [kv.Key, kv.Value])));
        }
        Sec("Summary", lines);

        // ---- projects
        var prows = new List<object?[]>();
        foreach (var p in nodes.Where(n => n.Kind == "project")
                     .OrderBy(n => n.Str("repo") ?? "", o).ThenBy(n => (n.Str("area") ?? "None").ToLowerInvariant(), o)
                     .ThenBy(n => n.Name.ToLowerInvariant(), o))
        {
            var fs = v.ProjectFiles(p.Id);
            prows.Add([p.Name, p.Str("ptype") ?? "", p.Str("lang") ?? "", p.Str("version") ?? "", p.Str("area") ?? "",
                fs.Count, fs.Sum(f => g.GetNode(f)!.Int("loc") ?? 0), MissingMembers(p).Count]);
        }
        Sec("Projects", Table(["Project", "Type", "Lang", "Version", "Area", "Files", "LOC", "Missing members"], prows));

        // ---- most used DB objects
        var use = new List<(int Nf, int Nobj, string Kind, string Name)>();
        foreach (var n in nodes)
        {
            if (!DbKinds.Contains(n.Kind)) continue;
            var srcs = v.DbUsers.TryGetValue(n.Id, out var du) ? du.Keys.Select(k => k.Src).ToHashSet() : [];
            int nf = srcs.Count(s => s.StartsWith("file:", StringComparison.Ordinal));
            use.Add((nf, srcs.Count - nf, n.Kind[3..], n.Name));
        }
        use = use.OrderBy(x => -x.Nf).ThenBy(x => -x.Nobj).ThenBy(x => x.Name.ToLowerInvariant(), o).ToList();
        Sec("Most used database objects (top 25)",
            Table(["Object", "Kind", "Using files", "Using DB objects"], Rows(use.Take(25), u => [u.Name, u.Kind, u.Nf, u.Nobj])));

        // ---- hubs
        var hubs = v.FileIn.Select(kv => (N: kv.Value.Count, F: kv.Key)).OrderBy(x => -x.N).ThenBy(x => x.F, o).Take(25).ToList();
        Sec("Hub code files (top 25 by distinct files using them)",
            Table(["File", "Repo", "Used by files", "LOC"],
                Rows(hubs, h => [g.GetNode(h.F)!.Str("path"), g.GetNode(h.F)!.Str("repo"), h.N, g.GetNode(h.F)!.Int("loc") ?? 0])));

        // ---- possibly unused DB objects
        var unused = nodes.Where(n => DbKinds.Contains(n.Kind) && !v.InOf(n.Id).Any(e => e.Relation is not ("defines" or "alters" or "contains")))
            .Select(n => (Kind: n.Kind[3..], n.Name)).OrderBy(x => x.Kind, o).ThenBy(x => x.Name, o).ToList();
        var ul = Table(["Kind", "Object"], Rows(unused.Take(200), x => [x.Kind, x.Name]));
        if (unused.Count > 200) { ul.Add($"_... and {unused.Count - 200} more_"); ul.Add(""); }
        Sec($"Possibly unused database objects ({unused.Count})", ul,
            "Nothing in the scanned code reads, writes, executes or references these. Treat as candidates only: dynamic SQL, " +
            "other repositories, reports, jobs and external applications may still use them.");

        // ---- missing procs
        var ex = (unresolved ?? new Dictionary<string, int>()).Where(kv => kv.Key.StartsWith("exec:", StringComparison.Ordinal))
            .Select(kv => (Name: kv.Key[5..], Count: kv.Value)).OrderBy(x => -x.Count).ThenBy(x => x.Name, o).ToList();
        if (ex.Count > 0)
            Sec($"Stored procedures called from code but not found ({ex.Count})",
                Table(["Procedure", "Calls"], Rows(ex.Take(200), x => [x.Name, x.Count])),
                "These names are executed in code but no CREATE statement was found in the scanned repositories.");

        // ---- orphans
        var orph = nodes.Where(n => n.Kind == "file" && n.Bool("orphan"))
            .Select(n => (Repo: n.Str("repo") ?? "", Path: n.Str("path") ?? "", Loc: n.Int("loc") ?? 0))
            .OrderBy(x => x.Repo, o).ThenBy(x => x.Path, o).ThenBy(x => x.Loc).ToList();
        var ol = Table(["Repo", "File", "LOC"], Rows(orph.Take(200), x => [x.Repo, x.Path, x.Loc]));
        if (orph.Count > 200) { ol.Add($"_... and {orph.Count - 200} more_"); ol.Add(""); }
        Sec($"Orphan source files ({orph.Count})", ol, "Source files that no project file lists as a member.");

        // ---- missing members
        var mp = nodes.Where(n => n.Kind == "project" && MissingMembers(n).Count > 0)
            .OrderBy(n => -MissingMembers(n).Count).ToList();
        if (mp.Count > 0)
        {
            var ml = new List<string>();
            foreach (var p in mp.Take(50))
            {
                var m = MissingMembers(p);
                ml.Add($"### {p.Name} ({m.Count})");
                ml.Add("");
                ml.AddRange(m.Take(50).Select(x => $"- `{x}`"));
                if (m.Count > 50) ml.Add($"- ... and {m.Count - 50} more");
                ml.Add("");
            }
            if (mp.Count > 50) ml.Add($"_... and {mp.Count - 50} more projects_");
            Sec($"Projects with missing members ({mp.Count})", ml,
                "Files listed in the project file that were not found on disk (not checked out, deleted or excluded).");
        }

        // ---- errors & warnings
        errors ??= [];
        warnings ??= [];
        if (errors.Count > 0)
        {
            var el = errors.Take(200).Select(e => $"- `{e.Repo}` `{e.Rel}`: {e.Message}").ToList();
            el.Add("");
            Sec($"Parse errors ({errors.Count})", el);
        }
        else Sec("Parse errors (0)", ["_none_", ""]);
        if (warnings.Count > 0)
        {
            var wl = warnings.Select(w => $"- {w}").ToList();
            wl.Add("");
            Sec($"Warnings ({warnings.Count})", wl);
        }
        else Sec("Warnings (0)", ["_none_", ""]);
        return string.Join("\n", outp).TrimEnd() + "\n";
    }

    private static List<string> MissingMembers(Node p) =>
        p.Data["missing_members"] is System.Text.Json.Nodes.JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : [];
}
