using System.Text.Json.Nodes;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Query;

/// <summary>
/// Who uses a database object or column, in one answer: the direct users (confirmed: resolved links, with read /
/// write / exec and file:line), possible users (references the builder could not pin to it, from the parse cache), and
/// the entry points above them - forms, components, views, client routes and API endpoints - found by walking up the
/// callers a few steps.
/// </summary>
public static class DbUsage
{
    /// <summary>Kinds of node a user reaches the code through: a screen, a page, a route or an API endpoint.</summary>
    private static readonly HashSet<string> ScreenKinds = ["form", "component", "view", "route"];

    public sealed record User(string Id, Edge? Edge, RefIndex.Entry? Ref, string Confidence, string Layer, string? Reason);

    public sealed record EntryPoint(string Id, string Kind, int Distance, List<string> Through, string Confidence);

    public sealed record Result(string Target, List<User> Users, List<EntryPoint> Entries, int Depth, bool PossibleChecked);

    /// <summary>Whether the node is something <see cref="Find"/> answers for.</summary>
    public static bool IsDbNode(Node n) => n.Kind.StartsWith("db_", StringComparison.Ordinal) || n.Kind == "column";

    public static Result Find(Graph g, string target, int depth, RefIndex? refs)
    {
        var users = new List<User>();
        // files an extractor read items from (a list of patches): what they do to the database comes from their SQL
        var itemFiles = g.Nodes.Where(n => n.Id.StartsWith("item:", StringComparison.Ordinal)).Select(n => n.Str("file")).OfType<string>().ToHashSet();
        foreach (var e in g.Edges)
            if (e.Target == target && e.Relation != "contains")
                users.Add(new User(e.Source, e, null, "confirmed", Layer(g, e.Source, itemFiles), null));
        if (refs is not null) users.AddRange(Possible(g, target, refs, users, itemFiles));

        var inn = GraphQuery.ReverseDeps(g);
        // a file's screen / page: the form, component or view it defines
        var screenOfFile = new Dictionary<string, string>();
        foreach (var n in g.Nodes)
            if (n.Kind is "form" or "component" or "view" && n.Str("file") is { } f)
                screenOfFile.TryAdd(f, n.Id);
        var entries = new Dictionary<string, EntryPoint>();
        foreach (var u in users)
        {
            var seen = new Dictionary<string, int> { [u.Id] = 0 };
            var q = new Queue<string>(new[] { u.Id });
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                int d = seen[cur];
                foreach (var (eid, kind) in EntriesOf(g, cur, screenOfFile))
                {
                    if (!entries.TryGetValue(eid, out var ep))
                        entries[eid] = ep = new EntryPoint(eid, kind, d, [], u.Confidence);
                    else if (d < ep.Distance || (u.Confidence == "confirmed" && ep.Confidence != "confirmed"))
                        entries[eid] = ep = ep with
                        {
                            Distance = Math.Min(d, ep.Distance),
                            Confidence = u.Confidence == "confirmed" ? "confirmed" : ep.Confidence,
                        };
                    if (!ep.Through.Contains(u.Id)) ep.Through.Add(u.Id);
                }
                if (d >= depth) continue;
                foreach (var src in inn.GetValueOrDefault(cur) ?? [])
                    if (seen.TryAdd(src, d + 1)) q.Enqueue(src);
            }
        }
        var ordered = entries.Values.OrderBy(e => e.Confidence == "confirmed" ? 0 : 1).ThenBy(e => e.Distance)
            .ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        return new Result(target, users, ordered, depth, refs is not null);
    }

    /// <summary>The entry points a node stands for: itself (an API endpoint or client route) and its file's screen.</summary>
    private static IEnumerable<(string Id, string Kind)> EntriesOf(Graph g, string id, Dictionary<string, string> screenOfFile)
    {
        var n = g.GetNode(id);
        if (n is null) yield break;
        if (n.Data["routes"] is JsonArray { Count: > 0 }) yield return (id, "api");
        if (ScreenKinds.Contains(n.Kind)) yield return (id, n.Kind);
        string? file = n.Kind == "file" ? id : n.Str("file");
        if (file is not null && screenOfFile.GetValueOrDefault(file) is { } screen && screen != id)
            yield return (screen, g.GetNode(screen)!.Kind);
    }

    /// <summary>database (a DB object), script (a SQL file or an item with SQL) or app (any other code).</summary>
    private static string Layer(Graph g, string id, HashSet<string> itemFiles)
    {
        var n = g.GetNode(id);
        if (n is null) return "app";
        if (n.Kind.StartsWith("db_", StringComparison.Ordinal) || n.Kind == "column") return "database";
        // an extractor item (a patch, a report definition) uses the database through the SQL it carries
        if (id.StartsWith("item:", StringComparison.Ordinal)) return "script";
        if (n.Kind == "file" && itemFiles.Contains(id)) return "script";
        var fn = n.Kind == "file" ? n : g.GetNode(n.Str("file") ?? "");
        return fn?.Str("lang") == "sql" ? "script" : "app";
    }

    /// <summary>
    /// References that name the target but were not linked to it, outside routines that already have a confirmed
    /// link: a string with the object's name, SQL the builder could not pin to this object, a column of a statement
    /// whose tables are unknown or several of which have such a column, a column read by name from a row.
    /// </summary>
    private static List<User> Possible(Graph g, string target, RefIndex refs, List<User> confirmed, HashSet<string> itemFiles)
    {
        var t = g.GetNode(target)!;
        bool isColumn = t.Kind == "column";
        string? tableId = isColumn ? t.Str("table") : target;
        string tableKey = tableId is not null && tableId.StartsWith("db:", StringComparison.Ordinal) ? tableId[3..].ToLowerInvariant() : "";
        string name = isColumn ? target[(target.LastIndexOf('.') + 1)..] : SqlText.ShortName(tableKey);
        var done = confirmed.Select(u => u.Id).ToHashSet();
        var found = new List<User>();
        var seen = new HashSet<(string, int, string)>();
        foreach (var r in refs.Named(name))
        {
            string? reason = isColumn ? ColumnReason(r, tableKey) : ObjectReason(g, r, tableKey);
            if (reason is null) continue;
            string caller = Caller(g, r);
            if (done.Contains(caller) || !seen.Add((r.FileId, r.Line, r.Name.ToLowerInvariant()))) continue;
            found.Add(new User(caller, null, r, "possible", Layer(g, caller, itemFiles), reason));
        }
        return found.OrderBy(u => u.Ref!.FileId, StringComparer.Ordinal).ThenBy(u => u.Ref!.Line).ToList();
    }

    private static string? ObjectReason(Graph g, RefIndex.Entry r, string key)
    {
        if (r.Kind == "column" || r.ColumnRead) return null;
        // 'other.Orders' is another object when one is defined under that name
        string refKey = SqlText.ObjectKey(r.Name);
        if (refKey != key && r.Name.Contains('.') && g.HasNode("db:" + refKey)) return null;
        return r.Kind == "literal" ? "name in a string" : "named in SQL text, not linked";
    }

    private static string? ColumnReason(RefIndex.Entry r, string tableKey)
    {
        if (r.Kind == "literal") return !r.ColumnRead ? null : r.Mode == "write" ? "column written by name" : "column read by name";
        if (r.Kind != "column") return null;
        // qualified with this table: the builder links those; with another table: not this column
        if (r.Table is not null) return null;
        if (r.Tables.Length == 0) return "column name in SQL, table unknown";
        return r.Tables.Any(x => SqlText.ObjectKey(x) == tableKey || SqlText.ShortName(SqlText.ObjectKey(x)) == SqlText.ShortName(tableKey))
            ? "column in SQL with several tables" : null;
    }

    /// <summary>
    /// The node a reference is in, as the builder picks it: the SQL object it is inside, an extractor item (a patch,
    /// a report), its routine (or type), else the file.
    /// </summary>
    private static string Caller(Graph g, RefIndex.Entry r)
    {
        if (r.Item is not null && g.HasNode(r.Item))
            return !string.IsNullOrEmpty(r.From) && g.HasNode("db:" + SqlText.ObjectKey(r.From)) ? "db:" + SqlText.ObjectKey(r.From) : r.Item;
        if (!string.IsNullOrEmpty(r.From))
        {
            string member = (!string.IsNullOrEmpty(r.FromParent) ? $"{r.FromParent}.{r.From}" : r.From);
            string fileSym = r.FileId.Replace("file:", "sym:") + "#" + member;
            if (g.HasNode(fileSym)) return fileSym;
            if (g.HasNode("db:" + SqlText.ObjectKey(r.From))) return "db:" + SqlText.ObjectKey(r.From);
        }
        if (!string.IsNullOrEmpty(r.FromParent) && g.HasNode(r.FileId.Replace("file:", "sym:") + "#" + r.FromParent))
            return r.FileId.Replace("file:", "sym:") + "#" + r.FromParent;
        return r.FileId;
    }

    // ---- output ----

    public static JsonObject ToJson(Graph g, Result res)
    {
        var users = new JsonArray();
        foreach (var u in res.Users.Take(Structured.MaxItems))
        {
            var o = new JsonObject { ["id"] = u.Id, ["node"] = Structured.NodeJson(g, u.Id), ["confidence"] = u.Confidence, ["layer"] = u.Layer };
            if (u.Edge is { } e)
            {
                var c = Structured.Connection(g, e, res.Target);
                o["relation"] = e.Relation;
                foreach (var k in new[] { "mode", "line", "via", "dynamic", "count" })
                    if (c[k] is { } v) o[k] = v.DeepClone();
            }
            if (u.Ref is { } r)
            {
                if (r.Mode is { } m) o["mode"] = m;
                o["line"] = r.Line;
                o["file"] = g.GetNode(r.FileId)?.Str("path");
                o["file_id"] = r.FileId;
                o["text"] = r.Name;
                o["reason"] = u.Reason;
            }
            if (Structured.Projects(g, u.Id) is { Count: > 0 } projs) o["projects"] = Structured.Arr(projs.Select(p => (JsonNode?)p));
            users.Add((JsonNode?)o);
        }
        var entries = new JsonArray();
        foreach (var ep in res.Entries.Take(Structured.MaxItems))
        {
            var o = new JsonObject
            {
                ["id"] = ep.Id, ["node"] = Structured.NodeJson(g, ep.Id), ["kind"] = ep.Kind, ["distance"] = ep.Distance,
                ["confidence"] = ep.Confidence, ["through"] = Structured.Arr(ep.Through.Take(20).Select(x => (JsonNode?)x)),
            };
            if (g.GetNode(ep.Id)?.Data["routes"] is JsonArray routes) o["routes"] = routes.DeepClone();
            if (Structured.Projects(g, ep.Id) is { Count: > 0 } projs) o["projects"] = Structured.Arr(projs.Select(p => (JsonNode?)p));
            entries.Add((JsonNode?)o);
        }
        int Count(Func<User, bool> f) => res.Users.Count(f);
        return new JsonObject
        {
            ["target"] = Structured.NodeJson(g, res.Target),
            ["depth"] = res.Depth,
            ["possible_checked"] = res.PossibleChecked,
            ["counts"] = new JsonObject
            {
                ["confirmed"] = Count(u => u.Confidence == "confirmed"),
                ["possible"] = Count(u => u.Confidence == "possible"),
                ["confirmed_app"] = Count(u => u.Confidence == "confirmed" && u.Layer == "app"),
                ["confirmed_database"] = Count(u => u.Confidence == "confirmed" && u.Layer == "database"),
                ["confirmed_script"] = Count(u => u.Confidence == "confirmed" && u.Layer == "script"),
                ["entry_points"] = res.Entries.Count,
            },
            ["users"] = users,
            ["entry_points"] = entries,
            ["entry_points_by_project"] = Structured.Arr(ByProject(g, res.Entries).Select(p =>
                (JsonNode?)new JsonObject { ["project"] = p.Project, ["count"] = p.Entries.Count })),
            ["truncated"] = res.Users.Count > Structured.MaxItems || res.Entries.Count > Structured.MaxItems,
        };
    }

    public static void Write(Graph g, Result res, TextWriter w, bool ids = false)
    {
        string Id(string id) => ids ? $"  {{{id}}}" : "";
        string Mode(User u) => u.Edge is { } e ? Structured.Mode(e.Relation) ?? e.Relation : u.Ref?.Mode ?? "uses";
        var conf = res.Users.Where(u => u.Confidence == "confirmed").ToList();
        var poss = res.Users.Where(u => u.Confidence == "possible").ToList();
        w.WriteLine($"Usage of {GraphQuery.Label(g, res.Target)}{Id(res.Target)}");
        w.WriteLine($"\nDirect users ({conf.Count}, linked):");
        if (conf.Count == 0) w.WriteLine("  (none)");
        foreach (var layer in new[] { "app", "script", "database" })
        {
            var inLayer = conf.Where(u => u.Layer == layer).OrderBy(u => GraphQuery.Label(g, u.Id), StringComparer.Ordinal).ToList();
            if (inLayer.Count == 0) continue;
            w.WriteLine($"  {layer switch { "app" => "application code", "script" => "SQL scripts", _ => "database objects" }} ({inLayer.Count}):");
            foreach (var u in inLayer)
            {
                string extra = u.Edge?.Int("line") is { } l ? $"  line {l}" : "";
                if (u.Edge?.Str("via") is { Length: > 0 } via) extra += $"  via {via}";
                if (u.Edge?.Data["dynamic"] is JsonValue dv && dv.TryGetValue(out bool dyn) && dyn) extra += "  (dynamic SQL)";
                w.WriteLine($"    {Mode(u),-9} {GraphQuery.Label(g, u.Id)}{extra}{Id(u.Id)}");
            }
        }
        if (!res.PossibleChecked)
            w.WriteLine("\nPossible users: not checked (no parse cache next to graph.json; run codegraph build).");
        else
        {
            w.WriteLine($"\nPossible users ({poss.Count}, not linked - check the source):");
            if (poss.Count == 0) w.WriteLine("  (none)");
            foreach (var u in poss.Take(200))
            {
                var r = u.Ref!;
                string where = $"{g.GetNode(r.FileId)?.Str("path")}:{r.Line}";
                string inside = u.Id != r.FileId ? $"  in {g.GetNode(u.Id)?.Str("qual") ?? g.GetNode(u.Id)?.Name}" : "";
                w.WriteLine($"    {where}  \"{r.Name}\"  {u.Reason}{inside}{Id(u.Id)}");
            }
            if (poss.Count > 200) w.WriteLine($"    ... {poss.Count - 200} more");
        }
        w.Write($"\nEntry points (screens, routes and API endpoints within {res.Depth} step{(res.Depth == 1 ? "" : "s")}): {res.Entries.Count}");
        if (res.Entries.Count > FoldEntries)
        {
            // a busy table is reached from screens of many apps: one line per project instead of one per screen
            var byProject = ByProject(g, res.Entries);
            w.WriteLine($" in {byProject.Count} project{(byProject.Count == 1 ? "" : "s")} (folded; the JSON result lists each)");
            foreach (var (project, eps) in byProject)
            {
                var names = eps.Select(ep => GraphQuery.Label(g, ep.Id).Split("  [")[0] + (ep.Confidence == "possible" ? " [possible]" : "")).ToList();
                w.WriteLine($"    {project} ({eps.Count}): {string.Join(", ", names.Take(4))}{(names.Count > 4 ? $" (+{names.Count - 4})" : "")}");
            }
            return;
        }
        w.WriteLine();
        foreach (var ep in res.Entries.Take(200))
        {
            string routes = g.GetNode(ep.Id)?.Data["routes"] is JsonArray rs ? "  " + string.Join(", ", rs.Select(x => x?.ToString())) : "";
            string through = string.Join(", ", ep.Through.Take(3).Select(x => g.GetNode(x)?.Name ?? x)) + (ep.Through.Count > 3 ? $" (+{ep.Through.Count - 3})" : "");
            w.WriteLine($"    {ep.Kind,-9} {GraphQuery.Label(g, ep.Id)}{routes}  ({ep.Distance} step{(ep.Distance == 1 ? "" : "s")} from {through})" +
                        $"{(ep.Confidence == "possible" ? "  [possible]" : "")}{Id(ep.Id)}");
        }
        if (res.Entries.Count > 200) w.WriteLine($"    ... {res.Entries.Count - 200} more");
    }

    /// <summary>More entry points than this are folded per project in the text answer.</summary>
    public const int FoldEntries = 20;

    /// <summary>Entry points per project (most first; an entry in a file of several projects counts in each).</summary>
    public static List<(string Project, List<EntryPoint> Entries)> ByProject(Graph g, IEnumerable<EntryPoint> entries)
    {
        var map = new Dictionary<string, List<EntryPoint>>();
        foreach (var ep in entries)
            foreach (var p in Structured.Projects(g, ep.Id) is { Count: > 0 } ps ? ps : ["(no project)"])
                (map.TryGetValue(p, out var l) ? l : map[p] = []).Add(ep);
        return map.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
