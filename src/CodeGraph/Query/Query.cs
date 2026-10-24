using System.Text.Json.Nodes;
using CodeGraph.Model;

namespace CodeGraph.Query;

/// <summary>Command-line queries over graph.json: show, impact, path. All output goes to the given writer.</summary>
public static class GraphQuery
{
    public static readonly HashSet<string> DepRels =
    [
        "calls", "instantiates", "uses_type", "inherits", "calls_native", "includes", "mentions", "reads",
        "imports", "uses_component", "ships", "calls_api", "renders",
        "writes", "executes", "references", "triggers_on", "depends_on", "uses_code_of", "alters",
    ];

    private static string Low(string? s) => (s ?? "").ToLowerInvariant();

    /// <summary>Node ids whose name/qual/path/id equals the text (case-insensitive), else those containing it.</summary>
    public static List<string> Find(Graph g, string name)
    {
        string low = name.Trim().ToLowerInvariant();
        List<string> exact = [], partial = [];
        foreach (var n in g.Nodes)
        {
            string[] cands = [Low(n.Str("name")), Low(n.Str("qual")), Low(n.Str("path")), n.Id.ToLowerInvariant()];
            if (cands.Contains(low)) exact.Add(n.Id);
            else if (cands.Any(c => c.Length > 0 && c.Contains(low, StringComparison.Ordinal))) partial.Add(n.Id);
        }
        return exact.Count > 0 ? exact : partial;
    }

    public static string Label(Graph g, string nid)
    {
        var n = g.GetNode(nid)!;
        string where = "";
        string? file = n.Str("file");
        if (!string.IsNullOrEmpty(file) && g.GetNode(file) is { } fnode)
            where = $"  [{fnode.Str("path")}:{n.Str("line") ?? ""}]";
        else if (!string.IsNullOrEmpty(n.Str("path")))
            where = $"  [{n.Str("path")}]";
        string q = !string.IsNullOrEmpty(n.Str("qual")) ? n.Str("qual")! : n.Str("name") ?? "";
        return $"{q} ({n.Kind}){where}";
    }

    private static int Rank(Node n) => n.Kind switch
    {
        "project" => 0,
        "db_table" or "db_proc" or "db_view" or "db_function" => 1,
        "file" => 2,
        _ => 5,
    };

    private static string? Choose(Graph g, string name, TextWriter w)
    {
        var hits = Find(g, name);
        if (hits.Count == 0)
        {
            w.WriteLine($"Nothing matches '{name}'.");
            return null;
        }
        if (hits.Count > 1)
        {
            hits = hits.OrderBy(h => Rank(g.GetNode(h)!)).ThenBy(h => h.Length).ToList();
            var a = g.GetNode(hits[0])!;
            var b = g.GetNode(hits[1])!;
            if (Rank(a) == Rank(b) && Low(a.Str("name")) == Low(b.Str("name")))
            {
                w.WriteLine($"'{name}' matches several things, using the first. Be more specific (use a path or id):");
                foreach (var h in hits.Take(15)) w.WriteLine("    " + h);
            }
        }
        return hits[0];
    }

    public static void Show(Graph g, string name, TextWriter w, int limit = 40)
    {
        var nid = Choose(g, name, w);
        if (nid is null) return;
        var n = g.GetNode(nid)!;
        w.WriteLine(Label(g, nid));
        foreach (var k in new[] { "version", "output", "ptype", "loc", "lines", "signature", "columns", "projects" })
        {
            var val = n.Data[k];
            if (!CodeGraph.Knowledge.KnowledgeLoader.Truthy(val)) continue;
            string text = val is JsonArray arr
                ? string.Join(", ", arr.Take(30).Select(x =>
                {
                    string s = x?.ToString() ?? "";
                    return g.GetNode(s) is { } xn ? xn.Name : s;
                }))
                : val!.ToString();
            w.WriteLine($"  {k}: {text}");
        }
        var outg = new Dictionary<string, List<Edge>>();
        var inn = new Dictionary<string, List<Edge>>();
        foreach (var e in g.Edges)
        {
            if (e.Source == nid) (outg.TryGetValue(e.Relation, out var l) ? l : outg[e.Relation] = []).Add(e);
            else if (e.Target == nid) (inn.TryGetValue(e.Relation, out var l2) ? l2 : inn[e.Relation] = []).Add(e);
        }
        foreach (var (title, groups, takeSource) in new[] { ("Outgoing", outg, false), ("Incoming", inn, true) })
        {
            if (groups.Count > 0) w.WriteLine($"\n{title}:");
            foreach (var (rel, es0) in groups.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var es = es0.OrderByDescending(e => e.Count).ToList();
                w.WriteLine($"  {rel} ({es.Count}):");
                foreach (var e in es.Take(limit))
                {
                    string extra = e.Count > 1 ? $"  x{e.Count}" : "";
                    if (!string.IsNullOrEmpty(e.Str("via"))) extra += $"  via {e.Str("via")}";
                    if (!string.IsNullOrEmpty(e.Str("version"))) extra += $"  v{e.Str("version")}";
                    w.WriteLine($"     {Label(g, takeSource ? e.Source : e.Target)}{extra}");
                }
                if (es.Count > limit) w.WriteLine($"     ... {es.Count - limit} more");
            }
        }
    }

    /// <summary>Everything that (directly or indirectly) depends on the given node.</summary>
    public static void Impact(Graph g, string name, TextWriter w, int depth = 3)
    {
        var nid = Choose(g, name, w);
        if (nid is null) return;
        var inn = new Dictionary<string, List<string>>();
        var contains = new Dictionary<string, List<string>>();
        foreach (var e in g.Edges)
        {
            if (DepRels.Contains(e.Relation))
                (inn.TryGetValue(e.Target, out var l) ? l : inn[e.Target] = []).Add(e.Source);
            if (e.Relation == "contains")
                (contains.TryGetValue(e.Source, out var c) ? c : contains[e.Source] = []).Add(e.Target);
        }
        // a file / type / project is affected through its contained symbols too
        var start = new HashSet<string> { nid };
        var stack = new Stack<string>();
        stack.Push(nid);
        while (stack.Count > 0)
            foreach (var c in contains.GetValueOrDefault(stack.Pop()) ?? [])
                if (start.Add(c)) stack.Push(c);
        var seen = new Dictionary<string, int>();
        foreach (var s in start) seen[s] = 0;
        var q = new Queue<string>(start);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (seen[cur] >= depth) continue;
            foreach (var src in inn.GetValueOrDefault(cur) ?? [])
                if (!seen.ContainsKey(src))
                {
                    seen[src] = seen[cur] + 1;
                    q.Enqueue(src);
                }
        }
        var affected = seen.Keys.Where(s => !start.Contains(s)).ToList();
        w.WriteLine($"Impact of changing {Label(g, nid)} (up to {depth} steps): {affected.Count} things\n");
        var byProj = new Dictionary<string, Dictionary<string, HashSet<string>>>();
        HashSet<string> Bucket(string proj, string key)
        {
            if (!byProj.TryGetValue(proj, out var d)) byProj[proj] = d = [];
            if (!d.TryGetValue(key, out var hs)) d[key] = hs = [];
            return hs;
        }
        foreach (var s in affected)
        {
            var n = g.GetNode(s);
            if (n is null) continue;
            if (n.Kind.StartsWith("db_", StringComparison.Ordinal))
            {
                Bucket("(database)", n.Kind).Add(n.Name);
                continue;
            }
            bool isSym = s.StartsWith("sym:", StringComparison.Ordinal);
            string? f = isSym ? n.Str("file") : s.StartsWith("file:", StringComparison.Ordinal) ? s : null;
            var fn = !string.IsNullOrEmpty(f) ? g.GetNode(f) : null;
            var projs = (fn?.Data["projects"] as JsonArray)?.Select(p => g.GetNode(p?.ToString() ?? "")?.Name ?? p?.ToString() ?? "").ToList() ?? [];
            if (projs.Count == 0) projs = [$"(folder {fn?.Str("area") ?? "?"})"];
            if (s.StartsWith("proj:", StringComparison.Ordinal))
            {
                Bucket(n.Name, "project").Add("(whole project)");
                continue;
            }
            foreach (var p in projs)
                Bucket(p, fn?.Str("path") ?? n.Str("name") ?? "").Add(isSym ? n.Str("name") ?? "" : "");
        }
        foreach (var p in byProj.Keys.Order(StringComparer.Ordinal))
        {
            w.WriteLine(p);
            foreach (var (f, set) in byProj[p].OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var items = set.Where(i => i.Length > 0).Order(StringComparer.Ordinal).ToList();
                string shown = string.Join(", ", items.Take(8)) + (items.Count > 8 ? $" (+{items.Count - 8})" : "");
                w.WriteLine($"   {f}{(shown.Length > 0 ? ": " + shown : "")}");
            }
        }
    }

    /// <summary>Shortest dependency path between two things.</summary>
    public static void Path(Graph g, string a, string b, TextWriter w, int maxLen = 8)
    {
        var s = Choose(g, a, w);
        var t = Choose(g, b, w);
        if (s is null || t is null) return;
        var adj = new Dictionary<string, List<(string Next, string Rel, string Dir)>>();
        void AddAdj(string from, string to, string rel, string d) =>
            (adj.TryGetValue(from, out var l) ? l : adj[from] = []).Add((to, rel, d));
        foreach (var e in g.Edges)
        {
            string rel = e.Relation;
            if (DepRels.Contains(rel) || (rel == "contains" && (e.Source.StartsWith("file:", StringComparison.Ordinal) || e.Source.StartsWith("sym:", StringComparison.Ordinal))))
            {
                AddAdj(e.Source, e.Target, rel, "->");
                AddAdj(e.Target, e.Source, rel, "<-");
            }
        }
        var prev = new Dictionary<string, (string From, string Rel, string Dir)?> { [s] = null };
        var q = new Queue<string>();
        q.Enqueue(s);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur == t) break;
            foreach (var (nxt, rel, d) in adj.GetValueOrDefault(cur) ?? [])
                if (!prev.ContainsKey(nxt))
                {
                    prev[nxt] = (cur, rel, d);
                    q.Enqueue(nxt);
                }
        }
        if (!prev.ContainsKey(t))
        {
            w.WriteLine("No connection found.");
            return;
        }
        var steps = new List<(string P, string Rel, string Dir, string C)>();
        var node = t;
        while (prev[node] is { } pv)
        {
            steps.Add((pv.From, pv.Rel, pv.Dir, node));
            node = pv.From;
        }
        steps.Reverse();
        w.WriteLine(Label(g, s));
        foreach (var (_, rel, d, c) in steps.Take(maxLen * 3))
        {
            string arrow = d == "->" ? $"--{rel}-->" : $"<--{rel}--";
            w.WriteLine($"  {arrow} {Label(g, c)}");
        }
    }
}
