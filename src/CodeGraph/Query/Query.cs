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

    /// <summary>
    /// The node meant by a name: an exact node id wins, else the best-ranked match. <paramref name="ambiguous"/> gets
    /// all candidates (best first) when the top two look alike (same rank and name), else stays empty.
    /// </summary>
    public static string? Resolve(Graph g, string name, out List<string> ambiguous)
    {
        ambiguous = [];
        if (g.HasNode(name.Trim())) return name.Trim();
        // an id that doesn't exist must not fall back to "anything containing it" (db:a -> Audit_Trail)
        if (LooksLikeId(name))
            return g.Nodes.FirstOrDefault(n => string.Equals(n.Id, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
        var hits = Find(g, name);
        if (hits.Count == 0) return null;
        if (hits.Count > 1)
        {
            hits = hits.OrderBy(h => Rank(g.GetNode(h)!)).ThenBy(h => h.Length).ToList();
            var a = g.GetNode(hits[0])!;
            var b = g.GetNode(hits[1])!;
            if (Rank(a) == Rank(b) && Low(a.Str("name")) == Low(b.Str("name")))
                ambiguous = hits;
        }
        return hits[0];
    }

    private static readonly string[] IdPrefixes =
        ["repo", "area", "proj", "sln", "file", "sym", "db", "ext", "rel", "concept", "item", "fileref", "module"];

    /// <summary>Whether the text is shaped like a node id ("db:orders", "sym:Repo:path#Type.Member").</summary>
    public static bool LooksLikeId(string name)
    {
        int colon = name.IndexOf(':');
        return colon > 0 && IdPrefixes.Contains(name.Trim()[..colon].ToLowerInvariant());
    }

    private static string? Choose(Graph g, string name, TextWriter w)
    {
        var nid = Resolve(g, name, out var ambiguous);
        if (nid is null)
        {
            w.WriteLine(LooksLikeId(name) ? $"No node has the id '{name}'." : $"Nothing matches '{name}'.");
            return null;
        }
        if (ambiguous.Count > 0)
        {
            w.WriteLine($"'{name}' matches several things, using the first. Be more specific (use a path or id):");
            foreach (var h in ambiguous.Take(15)) w.WriteLine("    " + h);
        }
        return nid;
    }

    /// <summary>The repository a node belongs to: its "repo" attribute, else the second part of its id.</summary>
    public static string? RepoOf(Node n)
    {
        if (!string.IsNullOrEmpty(n.Str("repo"))) return n.Str("repo");
        var parts = n.Id.Split(':', 3);
        return parts.Length == 3 && parts[0] is "sym" or "file" or "proj" or "area" ? parts[1] : null;
    }

    /// <summary>
    /// Nodes whose name/qual/path/id contains the text (case-insensitive), optionally of one kind and repo. Exact
    /// matches come first, then names starting with the text, names containing it, paths/ids containing it; within
    /// each by kind rank and id length.
    /// </summary>
    public static List<string> Search(Graph g, string text, string? kind = null, string? repo = null)
    {
        string low = text.Trim().ToLowerInvariant();
        var hits = new List<(string Id, int Tier, int Rank)>();
        foreach (var n in g.Nodes)
        {
            if (!string.IsNullOrEmpty(kind) && !string.Equals(n.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(repo) && !string.Equals(RepoOf(n), repo, StringComparison.OrdinalIgnoreCase)) continue;
            string name = Low(n.Str("name")), qual = Low(n.Str("qual")), path = Low(n.Str("path")), id = n.Id.ToLowerInvariant();
            int tier =
                name == low || qual == low || path == low || id == low ? 0
                : name.StartsWith(low, StringComparison.Ordinal) ? 1
                : name.Contains(low, StringComparison.Ordinal) || qual.Contains(low, StringComparison.Ordinal) ? 2
                : path.Contains(low, StringComparison.Ordinal) || id.Contains(low, StringComparison.Ordinal) ? 3
                : -1;
            if (tier >= 0) hits.Add((n.Id, tier, Rank(n)));
        }
        return hits.OrderBy(h => h.Tier).ThenBy(h => h.Rank).ThenBy(h => h.Id.Length)
            .ThenBy(h => h.Id, StringComparer.Ordinal).Select(h => h.Id).ToList();
    }

    /// <summary>
    /// A node, its attributes and its connections grouped by relation. <paramref name="relation"/> and
    /// <paramref name="direction"/> ("in", "out" or "both") filter the connections; <paramref name="ids"/> adds each
    /// neighbour's node id, so a caller can follow it exactly.
    /// </summary>
    public static void Show(Graph g, string name, TextWriter w, int limit = 40, string? relation = null,
        string direction = "both", bool ids = false)
    {
        var nid = Choose(g, name, w);
        if (nid is null) return;
        var n = g.GetNode(nid)!;
        w.WriteLine(Label(g, nid) + (ids ? $"  {{{nid}}}" : ""));
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
        bool wantOut = direction is "both" or "out", wantIn = direction is "both" or "in";
        foreach (var e in g.Edges)
        {
            if (!string.IsNullOrEmpty(relation) && !string.Equals(e.Relation, relation, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Source == nid) { if (wantOut) (outg.TryGetValue(e.Relation, out var l) ? l : outg[e.Relation] = []).Add(e); }
            else if (e.Target == nid && wantIn) (inn.TryGetValue(e.Relation, out var l2) ? l2 : inn[e.Relation] = []).Add(e);
        }
        if (outg.Count == 0 && inn.Count == 0 && (!string.IsNullOrEmpty(relation) || direction != "both"))
            w.WriteLine($"\nNo {(string.IsNullOrEmpty(relation) ? "" : relation + " ")}connections" +
                        (direction != "both" ? $" ({direction})." : "."));
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
                    string other = takeSource ? e.Source : e.Target;
                    if (ids) extra += $"  {{{other}}}";
                    w.WriteLine($"     {Label(g, other)}{extra}");
                }
                if (es.Count > limit) w.WriteLine($"     ... {es.Count - limit} more");
            }
        }
    }

    /// <summary>
    /// Everything that (directly or indirectly) depends on the given node, grouped by project. Files built into more
    /// than two projects are listed once under "(shared files)"; DB objects and other nodes (reports, patches, ...)
    /// by kind. <paramref name="ids"/> adds each file's node id.
    /// </summary>
    public static void Impact(Graph g, string name, TextWriter w, int depth = 3, bool ids = false)
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
        w.WriteLine($"Impact of changing {Label(g, nid)}{(ids ? $"  {{{nid}}}" : "")} " +
                    $"(up to {depth} step{(depth == 1 ? "" : "s")}): {affected.Count} things\n");
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
            if (s.StartsWith("proj:", StringComparison.Ordinal))
            {
                Bucket(n.Name, "project").Add("(whole project)");
                continue;
            }
            bool isSym = s.StartsWith("sym:", StringComparison.Ordinal);
            string? f = isSym ? n.Str("file") : s.StartsWith("file:", StringComparison.Ordinal) ? s : null;
            var fn = !string.IsNullOrEmpty(f) ? g.GetNode(f) : null;
            if (fn is null)
            {
                // reports, patches, releases, libraries ...: by kind
                Bucket("(other)", n.Kind).Add(n.Name);
                continue;
            }
            var projs = (fn.Data["projects"] as JsonArray)?.Select(p => g.GetNode(p?.ToString() ?? "")?.Name ?? p?.ToString() ?? "").ToList() ?? [];
            string key = (fn.Str("path") ?? fn.Name) + (ids ? $"  {{{fn.Id}}}" : "");
            string member = isSym ? n.Str("name") ?? "" : "";
            if (projs.Count > 2)
            {
                var sorted = projs.Order(StringComparer.Ordinal).ToList();
                Bucket("(shared files)", $"{key}  [in {projs.Count} projects: {string.Join(", ", sorted.Take(5))}" +
                                         $"{(projs.Count > 5 ? $" (+{projs.Count - 5})" : "")}]").Add(member);
                continue;
            }
            if (projs.Count == 0) projs = [$"(folder {fn.Str("area") ?? "?"})"];
            foreach (var p in projs)
                Bucket(p, key).Add(member);
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

    /// <summary>
    /// Shortest chain (at most <paramref name="maxLen"/> steps) between two things over all relations. A one-way
    /// chain (a uses ... b, or b uses ... a) is preferred; only when there is none does it fall back to a chain that
    /// changes direction (e.g. two files importing the same library), and says so. Repo, area and solution
    /// membership is not followed: it would connect everything in a repository.
    /// </summary>
    public static void Path(Graph g, string a, string b, TextWriter w, int maxLen = 8, bool ids = false)
    {
        var s = Choose(g, a, w);
        var t = Choose(g, b, w);
        if (s is null || t is null) return;
        var fwd = new Dictionary<string, List<(string Next, string Rel, string Dir)>>();
        var back = new Dictionary<string, List<(string Next, string Rel, string Dir)>>();
        foreach (var e in g.Edges)
        {
            if (e.Relation == "contains" && (e.Source.StartsWith("repo:", StringComparison.Ordinal)
                || e.Source.StartsWith("area:", StringComparison.Ordinal) || e.Source.StartsWith("sln:", StringComparison.Ordinal)))
                continue;
            (fwd.TryGetValue(e.Source, out var l) ? l : fwd[e.Source] = []).Add((e.Target, e.Relation, "->"));
            (back.TryGetValue(e.Target, out var l2) ? l2 : back[e.Target] = []).Add((e.Source, e.Relation, "<-"));
        }
        var down = Bfs(s, t, n => fwd.GetValueOrDefault(n) ?? [], maxLen);
        var up = Bfs(s, t, n => back.GetValueOrDefault(n) ?? [], maxLen);
        var steps = down is null ? up : up is null || down.Count <= up.Count ? down : up;
        bool mixed = false;
        if (steps is null)
        {
            steps = Bfs(s, t, n => (fwd.GetValueOrDefault(n) ?? []).Concat(back.GetValueOrDefault(n) ?? []), maxLen);
            mixed = steps is not null;
        }
        if (steps is null)
        {
            w.WriteLine($"No connection found within {maxLen} step{(maxLen == 1 ? "" : "s")}.");
            return;
        }
        string Id(string nid) => ids ? $"  {{{nid}}}" : "";
        w.WriteLine(Label(g, s) + Id(s));
        foreach (var (rel, d, c) in steps)
        {
            string arrow = d == "->" ? $"--{rel}-->" : $"<--{rel}--";
            w.WriteLine($"  {arrow} {Label(g, c)}{Id(c)}");
        }
        if (mixed)
            w.WriteLine("(No one-way chain: this path changes direction, e.g. both ends use the same thing or are used " +
                        "by the same thing, so it may not be a real dependency.)");
    }

    private static List<(string Rel, string Dir, string Node)>? Bfs(string s, string t,
        Func<string, IEnumerable<(string Next, string Rel, string Dir)>> next, int maxLen)
    {
        var prev = new Dictionary<string, (string From, string Rel, string Dir, int Depth)> { [s] = ("", "", "", 0) };
        var q = new Queue<string>();
        q.Enqueue(s);
        while (q.Count > 0 && !prev.ContainsKey(t))
        {
            var cur = q.Dequeue();
            int depth = prev[cur].Depth;
            if (depth >= maxLen) continue;
            foreach (var (nxt, rel, d) in next(cur))
                if (!prev.ContainsKey(nxt))
                {
                    prev[nxt] = (cur, rel, d, depth + 1);
                    q.Enqueue(nxt);
                }
        }
        if (!prev.ContainsKey(t)) return null;
        var steps = new List<(string Rel, string Dir, string Node)>();
        for (var node = t; node != s; node = prev[node].From)
            steps.Add((prev[node].Rel, prev[node].Dir, node));
        steps.Reverse();
        return steps;
    }

    /// <summary>
    /// The source lines of a symbol, DB object or file (read from the repo on disk), with
    /// <paramref name="context"/> extra lines around a definition and at most <paramref name="maxLines"/> lines.
    /// </summary>
    public static void Source(Graph g, string name, TextWriter w, int context = 0, int maxLines = 200)
    {
        var nid = Choose(g, name, w);
        if (nid is null) return;
        var n = g.GetNode(nid)!;
        bool isFile = n.Kind == "file";
        var fn = isFile ? n : g.GetNode(n.Str("file") ?? "");
        string? repoPath = fn is null ? null : g.GetNode("repo:" + fn.Str("repo"))?.Str("path");
        if (fn is null || string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(fn.Str("path")))
        {
            w.WriteLine($"{Label(g, nid)} has no source location.");
            return;
        }
        string full = System.IO.Path.Combine(repoPath, fn.Str("path")!.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            w.WriteLine($"File not found: {full}");
            return;
        }
        var lines = CodeGraph.Text.SourceText.Read(full).Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        int start = isFile ? 1 : n.Int("line") ?? 1;
        int end = isFile ? lines.Length : start + Math.Max(1, n.Int("lines") ?? 1) - 1;
        int from = Math.Max(1, start - (isFile ? 0 : context));
        int to = Math.Min(lines.Length, end + (isFile ? 0 : context));
        int shownTo = Math.Min(to, from + maxLines - 1);
        w.WriteLine($"{Label(g, nid)}\n{full}:{from}-{shownTo}");
        for (int i = from; i <= shownTo; i++)
            w.WriteLine($"{i,6}  {lines[i - 1].TrimEnd('\r')}");
        if (shownTo < to) w.WriteLine($"   ... {to - shownTo} more lines");
    }
}
