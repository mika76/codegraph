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
        "writes", "executes", "references", "triggers_on", "depends_on", "uses_code_of", "alters", "opens",
        "implements", "overrides", "routes_to",
    ];

    private static string Low(string? s) => (s ?? "").ToLowerInvariant();

    /// <summary>The texts a name is compared with: name, qualified name, path, id (lower case).</summary>
    private static string[] Keys(Node n) => [Low(n.Str("name")), Low(n.Str("qual")), Low(n.Str("path")), n.Id.ToLowerInvariant()];

    /// <summary>
    /// The result of looking a name up: <see cref="Id"/> when exactly one node is meant; otherwise
    /// <see cref="Matches"/> (several, ambiguous) or <see cref="Near"/> (none - the closest, for the message).
    /// </summary>
    public sealed record NameMatch(string? Id, List<string> Matches, List<string> Near);

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
    /// The node a name means - never a guess. Without '*' only an exact id, name, qualified name or path counts
    /// (ignoring case); with '*' the name is a pattern ('db:order*', '*Order_Line*', 'sym:Api:*#Orders*.Get*').
    /// Several exact matches of different weight pick the heaviest (a project or table over a file or routine of the
    /// same name - the answer's first line says which); otherwise several matches are ambiguous. With no match the
    /// closest names (containing the text) are returned for the message. Only '*' is special: '?' occurs in URLs.
    /// </summary>
    public static NameMatch Resolve(Graph g, string name)
    {
        string n = name.Trim();
        if (g.HasNode(n)) return new(n, [], []);
        bool wildcard = n.Contains('*');
        List<string> hits;
        if (wildcard)
        {
            var re = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(n).Replace(@"\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                | System.Text.RegularExpressions.RegexOptions.Singleline);
            hits = g.Nodes.Where(x => Keys(x).Any(k => k.Length > 0 && re.IsMatch(k))).Select(x => x.Id).ToList();
        }
        else
        {
            string low = n.ToLowerInvariant();
            hits = g.Nodes.Where(x => Keys(x).Contains(low)).Select(x => x.Id).ToList();
        }
        if (hits.Count == 1) return new(hits[0], [], []);
        if (hits.Count > 1)
        {
            var ranked = hits.OrderBy(h => Rank(g.GetNode(h)!)).ThenBy(h => h.Length).ThenBy(h => h, StringComparer.Ordinal).ToList();
            if (!wildcard && Rank(g.GetNode(ranked[0])!) < Rank(g.GetNode(ranked[1])!)) return new(ranked[0], [], []);
            return new(null, ranked, []);
        }
        if (wildcard) return new(null, [], []);
        // closest: names containing the text; ids and paths containing it (every symbol of a matching file) only
        // when no name does
        var near = Search(g, n);
        string lowName = n.ToLowerInvariant();
        var byName = near.Where(h => g.GetNode(h) is { } x && (Low(x.Str("name")).Contains(lowName, StringComparison.Ordinal)
                                                               || Low(x.Str("qual")).Contains(lowName, StringComparison.Ordinal))).ToList();
        return new(null, [], (byName.Count > 0 ? byName : near).Take(5).ToList());
    }

    /// <summary>Why a name picked no node, with the candidates' labels and ids; null when it picked one.</summary>
    public static string? Problem(Graph g, string name, NameMatch m)
    {
        if (m.Id is not null) return null;
        var w = new StringWriter { NewLine = "\n" };
        if (m.Matches.Count > 0)
        {
            w.WriteLine($"'{name}' matches {m.Matches.Count} things. Use one of these ids (or a narrower pattern):");
            foreach (var h in m.Matches.Take(25)) w.WriteLine($"  {Label(g, h)}  {{{h}}}");
            if (m.Matches.Count > 25) w.WriteLine($"  ... {m.Matches.Count - 25} more");
        }
        else
        {
            w.WriteLine(LooksLikeId(name) ? $"No node has the id '{name}'." : $"Nothing is called '{name}'.");
            if (m.Near.Count > 0)
            {
                w.WriteLine("Closest (use an id, or * as a wildcard):");
                foreach (var h in m.Near) w.WriteLine($"  {Label(g, h)}  {{{h}}}");
            }
        }
        return w.ToString();
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
        var m = Resolve(g, name);
        if (Problem(g, name, m) is { } problem) w.Write(problem);
        return m.Id;
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
    /// each by kind rank and id length. When the text as typed matches nothing, its words are matched with case and
    /// separators ignored ("order line" finds OrderLine, order_line and order-lines.vue).
    /// </summary>
    public static List<string> Search(Graph g, string text, string? kind = null, string? repo = null)
    {
        string low = text.Trim().ToLowerInvariant();
        string[] words = low.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Fold).Where(x => x.Length > 0).ToArray();
        string folded = string.Concat(words);
        var hits = new List<(string Id, int Tier, int Rank)>();
        foreach (var n in g.Nodes)
        {
            if (!string.IsNullOrEmpty(kind) && !string.Equals(n.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(repo) && !string.Equals(RepoOf(n), repo, StringComparison.OrdinalIgnoreCase)) continue;
            string name = Low(n.Str("name")), qual = Low(n.Str("qual")), path = Low(n.Str("path")), id = n.Id.ToLowerInvariant();
            // a route is also found by its route name and by its meta values (the permission that guards it)
            string routeName = Low(n.Str("route_name"));
            string meta = n.Data["meta"] is JsonObject mo ? string.Join("\n", mo.Select(kv => Low(kv.Value?.ToString()))) : "";
            int tier =
                name == low || qual == low || path == low || id == low || routeName == low ? 0
                : name.StartsWith(low, StringComparison.Ordinal) ? 1
                : name.Contains(low, StringComparison.Ordinal) || qual.Contains(low, StringComparison.Ordinal)
                  || (routeName.Length > 0 && routeName.Contains(low, StringComparison.Ordinal)) ? 2
                : path.Contains(low, StringComparison.Ordinal) || id.Contains(low, StringComparison.Ordinal)
                  || (meta.Length > 0 && meta.Contains(low, StringComparison.Ordinal)) ? 3
                : -1;
            // the name itself with case and separators ignored is as exact as it gets: "orderline" is Order_Line
            if (tier != 0 && folded.Length > 0 && (Fold(name) == folded || Fold(qual) == folded)) tier = 0;
            if (tier < 0 && words.Length > 0)
            {
                // the words typed, whatever separates them in the name: "order line" finds OrderLine, order_line,
                // order-lines.vue; the whole run of words first, then every word somewhere in the name, then the path
                string fName = Fold(name) + "\n" + Fold(qual) + "\n" + Fold(routeName), fRest = Fold(path) + "\n" + Fold(id) + "\n" + Fold(meta);
                tier = HasWord(fName, folded, words.Length > 1) ? 5
                    : words.All(x => HasWord(fName, x, words.Length > 1)) ? 6
                    : words.All(x => HasWord(fName + "\n" + fRest, x, words.Length > 1)) ? 7
                    : -1;
            }
            if (tier >= 0) hits.Add((n.Id, tier, Rank(n)));
        }
        return hits.OrderBy(h => h.Tier).ThenBy(h => h.Rank).ThenBy(h => h.Id.Length)
            .ThenBy(h => h.Id, StringComparer.Ordinal).Select(h => h.Id).ToList();
    }

    /// <summary>Lower-case letters and digits only: "Order_Line", "order-line" and "OrderLine" all become "orderline".</summary>
    private static string Fold(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c == '\n') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>A typed word in a folded name; in a query of several words a plural also finds the singular ("order lines" finds
    /// OrderLine).</summary>
    private static bool HasWord(string folded, string word, bool plural) =>
        word.Length > 0 && (folded.Contains(word, StringComparison.Ordinal) ||
                            plural && word.Length > 3 && word[^1] == 's' && folded.Contains(word[..^1], StringComparison.Ordinal));

    /// <summary>
    /// <see cref="Search"/> as text: one line per match with its node id, at most <paramref name="limit"/>. An unknown
    /// kind or repo is an error that lists the ones in the graph. Returns all the matches.
    /// </summary>
    public static List<string> WriteSearch(Graph g, string query, TextWriter w, string? kind = null, string? repo = null, int limit = 25)
    {
        if (kind is not null && !g.Nodes.Any(n => string.Equals(n.Kind, kind, StringComparison.OrdinalIgnoreCase)))
            throw new CodeGraphException($"Unknown kind '{kind}'. Kinds in the graph: " +
                                         string.Join(", ", g.Nodes.Select(n => n.Kind).Distinct().Order(StringComparer.Ordinal)));
        if (repo is not null && !g.Nodes.Any(n => n.Kind == "repo" && string.Equals(n.Name, repo, StringComparison.OrdinalIgnoreCase)))
            throw new CodeGraphException($"Unknown repo '{repo}'. Repositories: " +
                                         string.Join(", ", g.Nodes.Where(n => n.Kind == "repo").Select(n => n.Name)));
        var hits = Search(g, query, kind, repo);
        if (hits.Count == 0)
        {
            w.WriteLine($"Nothing matches '{query}'.");
            return hits;
        }
        w.WriteLine($"{hits.Count} match{(hits.Count == 1 ? "" : "es")} for '{query}':");
        foreach (var h in hits.Take(limit)) w.WriteLine($"  {Label(g, h)}  {{{h}}}");
        if (hits.Count > limit) w.WriteLine($"  ... {hits.Count - limit} more (narrow with kind or repo, or raise the limit)");
        return hits;
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
        foreach (var k in new[] { "version", "output", "ptype", "loc", "lines", "signature", "definition", "columns", "projects", "route_name", "meta", "database" })
        {
            var val = n.Data[k];
            if (!CodeGraph.Knowledge.KnowledgeLoader.Truthy(val)) continue;
            string text = val switch
            {
                JsonArray arr => string.Join(", ", arr.Take(30).Select(x =>
                {
                    string s = x?.ToString() ?? "";
                    return g.GetNode(s) is { } xn ? xn.Name : s;
                })),
                JsonObject obj => string.Join(", ", obj.Select(kv => $"{kv.Key}={kv.Value}")), // meta: requiresAccess=X
                _ => val!.ToString(),
            };
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
        var (start, seen) = Reach(g, nid, depth);
        var affected = seen.Keys.Where(s => !start.Contains(s)).ToList();
        w.WriteLine($"Impact of changing {Label(g, nid)}{(ids ? $"  {{{nid}}}" : "")} " +
                    $"(up to {depth} step{(depth == 1 ? "" : "s")}): {affected.Count} thing{(affected.Count == 1 ? "" : "s")}\n");
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
        static string List(IReadOnlyList<string> xs, int max) =>
            string.Join(", ", xs.Take(max)) + (xs.Count > max ? $" (+{xs.Count - max})" : "");
        foreach (var p in byProj.Keys.Order(StringComparer.Ordinal))
        {
            w.WriteLine(p);
            // whole files that depend on it (scripts, patches - no routine names) are folded at the highest folder that
            // holds at least FoldFiles of them: "SQL/ (98 files in 22 folders)"; code files keep their routine names
            var entries = byProj[p].OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            bool Plain(KeyValuePair<string, HashSet<string>> kv) => kv.Key.Contains('/') && kv.Value.All(i => i.Length == 0);
            var under = new Dictionary<string, List<string>>();
            foreach (var kv in entries.Where(Plain))
                foreach (var anc in Ancestors(DirOf(kv.Key)))
                    GetOrAdd(under, anc).Add(kv.Key);
            string? FoldAt(string key) => Ancestors(DirOf(key)).FirstOrDefault(a => under[a].Count >= FoldFiles);
            var printed = new HashSet<string>();
            foreach (var kv in entries)
            {
                if (Plain(kv) && FoldAt(kv.Key) is { } dir)
                {
                    if (printed.Add(dir))
                    {
                        var files = under[dir];
                        int folders = files.Select(DirOf).Distinct().Count();
                        w.WriteLine($"   {dir}/ ({files.Count} files{(folders > 1 ? $" in {folders} folders" : "")}): " +
                                    List(files.Select(FileOf).ToList(), 5));
                    }
                    continue;
                }
                var items = kv.Value.Where(i => i.Length > 0).Order(StringComparer.Ordinal).ToList();
                w.WriteLine($"   {kv.Key}{(items.Count > 0 ? ": " + List(items, 8) : "")}");
            }
        }
    }

    /// <summary>
    /// Who depends on each node, one step back: the sources of dependency relations, plus a form / class for its init
    /// members and a view for the action that renders it.
    /// </summary>
    public static Dictionary<string, List<string>> ReverseDeps(Graph g)
    {
        var inn = new Dictionary<string, List<string>>();
        foreach (var e in g.Edges)
        {
            // a knowledge fact "A <relation> B" is recorded because A depends on B where the code does not show it
            if (DepRels.Contains(e.Relation) || e.Data["custom"] is JsonValue cv && cv.TryGetValue(out bool custom) && custom)
                GetOrAdd(inn, e.Target).Add(e.Source);
            // a form / class runs its init members when opened or created: they affect it, and so who opens it
            if (e.Relation == "contains" && g.GetNode(e.Target) is { } m && IsInit(m))
                GetOrAdd(inn, e.Target).Add(e.Source);
            // a view shows what the action that renders it computed (model, ViewBag): an affected action affects it
            if (e.Relation == "renders")
                GetOrAdd(inn, e.Source).Add(e.Target);
        }
        return inn;
    }

    /// <summary>
    /// What impact follows from a node: <c>Start</c> is the node and everything it contains (a file / type / project
    /// is affected through its symbols too), <c>Seen</c> every node reached within <paramref name="depth"/> steps
    /// with its distance (0 for the start set).
    /// </summary>
    public static (HashSet<string> Start, Dictionary<string, int> Seen) Reach(Graph g, string nid, int depth)
    {
        var inn = ReverseDeps(g);
        var contains = new Dictionary<string, List<string>>();
        foreach (var e in g.Edges)
            if (e.Relation == "contains")
                GetOrAdd(contains, e.Source).Add(e.Target);
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
        return (start, seen);
    }

    /// <summary>Folders with at least this many affected files are folded into one line.</summary>
    private const int FoldFiles = 6;

    // impact keys are "path/to/file.ext" optionally followed by "  {id}" or "  [in N projects: ...]"
    private static string PathOf(string key) => key.Split("  ", 2)[0];
    private static string DirOf(string key) => PathOf(key) is var p && p.LastIndexOf('/') is var i and > 0 ? p[..i] : "";
    private static string FileOf(string key) => PathOf(key) is var p ? p[(p.LastIndexOf('/') + 1)..] : key;

    /// <summary>"a/b/c" -> "a", "a/b", "a/b/c" (shallowest first).</summary>
    private static IEnumerable<string> Ancestors(string dir)
    {
        if (dir.Length == 0) yield break;
        for (int i = dir.IndexOf('/'); i > 0; i = dir.IndexOf('/', i + 1)) yield return dir[..i];
        yield return dir;
    }

    private static List<string> GetOrAdd(Dictionary<string, List<string>> d, string key) =>
        d.TryGetValue(key, out var l) ? l : d[key] = [];

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
        var (steps, mixed) = FindPath(g, s, t, maxLen);
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

    /// <summary>
    /// The chain <see cref="Path"/> prints: each step's relation, direction ("->" or "<-") and the node it reaches;
    /// null when there is none. <c>Mixed</c> says it changes direction.
    /// </summary>
    public static (List<(string Rel, string Dir, string Node)>? Steps, bool Mixed) FindPath(Graph g, string s, string t, int maxLen)
    {
        var fwd =new Dictionary<string, List<(string Next, string Rel, string Dir)>>();
        var back = new Dictionary<string, List<(string Next, string Rel, string Dir)>>();
        foreach (var e in g.Edges)
        {
            if (e.Relation == "contains" && (e.Source.StartsWith("repo:", StringComparison.Ordinal)
                || e.Source.StartsWith("area:", StringComparison.Ordinal) || e.Source.StartsWith("sln:", StringComparison.Ordinal)))
                continue;
            (fwd.TryGetValue(e.Source, out var l) ? l : fwd[e.Source] = []).Add((e.Target, e.Relation, "->"));
            (back.TryGetValue(e.Target, out var l2) ? l2 : back[e.Target] = []).Add((e.Source, e.Relation, "<-"));
        }
        var down = UseChain(g, s, t, fwd, back, maxLen);
        // t uses ... s: the same search from t, read backwards
        var up = UseChain(g, t, s, fwd, back, maxLen) is { } fromT ? Reversed(t, fromT) : null;
        var steps = down is null ? up : up is null || down.Count <= up.Count ? down : up;
        bool mixed = false;
        if (steps is null)
        {
            // a chain that changes direction must not pass through hubs: two things in the same release, patch,
            // installer, project or file/type, or using the same constant, are not connected by that (a hub at either
            // end is fine: rel:2.0 -> what it ships, a file -> its routines)
            bool Hub(string from, (string Next, string Rel, string Dir) e) =>
                from != s && e.Next != t && e.Rel is "released_in" or "defined_in" or "ships" or "contains" or "uses_value";
            steps = Bfs(s, t, n => (fwd.GetValueOrDefault(n) ?? []).Concat(back.GetValueOrDefault(n) ?? []).Where(e => !Hub(n, e)), maxLen);
            mixed = steps is not null;
        }
        return (steps, mixed);
    }

    /// <summary>
    /// The shortest chain by which s uses t. `contains` only counts at the ends: going down into s's own members
    /// first (a file uses what its routines use), or up from a used member to t last (using useOrders.load uses
    /// useOrders) - never in the middle, where it would claim that calling a factory or class uses all its members.
    /// </summary>
    private static List<(string Rel, string Dir, string Node)>? UseChain(Graph g, string s, string t,
        Dictionary<string, List<(string Next, string Rel, string Dir)>> fwd,
        Dictionary<string, List<(string Next, string Rel, string Dir)>> back, int maxLen)
    {
        // phase 0: still inside s; 1: after a use; 2: climbing from a used member to its containers;
        // 3: just opened / created a form or class - its initialisation runs, so its init members may follow
        var start = (s, 0);
        var prev = new Dictionary<(string Node, int Phase), ((string, int) From, string Rel, string Dir, int Depth)> { [start] = (start, "", "", 0) };
        var q = new Queue<(string Node, int Phase)>();
        q.Enqueue(start);
        (string Node, int Phase)? end = null;
        while (q.Count > 0 && end is null)
        {
            var cur = q.Dequeue();
            int depth = prev[cur].Depth;
            if (depth >= maxLen) continue;
            var moves = new List<(string Next, string Rel, string Dir, int Phase)>();
            foreach (var (nxt, rel, _) in fwd.GetValueOrDefault(cur.Node) ?? [])
                if (rel == "contains")
                {
                    if (cur.Phase == 0) moves.Add((nxt, rel, "->", 0));
                    else if (cur.Phase == 3 && g.GetNode(nxt) is { } m && IsInit(m)) moves.Add((nxt, rel, "->", 1));
                }
                else if (cur.Phase is <= 1 or 3) moves.Add((nxt, rel, "->", rel is "opens" or "instantiates" ? 3 : 1));
            if (cur.Phase >= 1)
                foreach (var (nxt, rel, _) in back.GetValueOrDefault(cur.Node) ?? [])
                    if (rel == "contains") moves.Add((nxt, rel, "<-", 2));
            foreach (var (nxt, rel, dir, ph) in moves)
            {
                var key = (nxt, ph);
                if (prev.ContainsKey(key)) continue;
                prev[key] = (cur, rel, dir, depth + 1);
                if (nxt == t) { end = key; break; }
                q.Enqueue(key);
            }
        }
        if (end is null) return null;
        var steps = new List<(string Rel, string Dir, string Node)>();
        for (var k = end.Value; k != start; k = prev[k].From)
            steps.Add((prev[k].Rel, prev[k].Dir, k.Node));
        steps.Reverse();
        return steps;
    }

    /// <summary>VB6 runtime events that run when a form, class or control is loaded or created.</summary>
    private static readonly HashSet<string> InitEvents = new(StringComparer.OrdinalIgnoreCase)
        { "Form_Load", "Form_Initialize", "Form_Activate", "Class_Initialize", "UserControl_Initialize", "UserControl_InitProperties" };

    /// <summary>
    /// A member that runs when its form or class is opened or created: a constructor, or one of the VB6 runtime's
    /// load/initialise events - so opening the form uses what these use, and changing them affects who opens it.
    /// </summary>
    private static bool IsInit(Node n) => n.Kind == "constructor" || InitEvents.Contains(n.Name);

    /// <summary>A chain from t to s, written from s: each step back to the node before it, arrows flipped.</summary>
    private static List<(string Rel, string Dir, string Node)> Reversed(string t, List<(string Rel, string Dir, string Node)> fromT)
    {
        var result = new List<(string Rel, string Dir, string Node)>();
        for (int i = fromT.Count - 1; i >= 0; i--)
            result.Add((fromT[i].Rel, fromT[i].Dir == "->" ? "<-" : "->", i == 0 ? t : fromT[i - 1].Node));
        return result;
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
        var src = ReadSource(g, nid, context, maxLines);
        if (src.Problem is not null)
        {
            w.WriteLine(src.Problem);
            return;
        }
        w.WriteLine($"{Label(g, nid)}\n{src.FullPath}:{src.From}-{src.From + src.Lines.Count - 1}");
        for (int i = 0; i < src.Lines.Count; i++)
            w.WriteLine($"{src.From + i,6}  {src.Lines[i]}");
        if (src.More > 0) w.WriteLine($"   ... {src.More} more lines");
    }

    /// <summary>
    /// Source lines of a node: <see cref="Lines"/> start at line <see cref="From"/>, <see cref="More"/> lines were
    /// left out by the limit. <see cref="Problem"/> says why there is nothing to read.
    /// </summary>
    public sealed record SourceLines(string? FullPath, int From, List<string> Lines, int More, string? Problem);

    public static SourceLines ReadSource(Graph g, string nid, int context = 0, int maxLines = 200)
    {
        var n = g.GetNode(nid)!;
        bool isFile = n.Kind == "file";
        var fn = isFile ? n : g.GetNode(n.Str("file") ?? "");
        string? repoPath = fn is null ? null : g.GetNode("repo:" + fn.Str("repo"))?.Str("path");
        if (fn is null || string.IsNullOrEmpty(repoPath) || string.IsNullOrEmpty(fn.Str("path")))
            return new(null, 0, [], 0, $"{Label(g, nid)} has no source location.");
        string full = System.IO.Path.Combine(repoPath, fn.Str("path")!.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return new(full, 0, [], 0, $"File not found: {full}");
        var lines = CodeGraph.Text.SourceText.Read(full).Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        int start = isFile ? 1 : n.Int("line") ?? 1;
        int end = isFile ? lines.Length : start + Math.Max(1, n.Int("lines") ?? 1) - 1;
        int from = Math.Max(1, start - (isFile ? 0 : context));
        int to = Math.Min(lines.Length, end + (isFile ? 0 : context));
        int shownTo = Math.Min(to, from + maxLines - 1);
        var shown = new List<string>();
        for (int i = from; i <= shownTo; i++) shown.Add(lines[i - 1].TrimEnd('\r'));
        return new(full, from, shown, to - shownTo, null);
    }
}
