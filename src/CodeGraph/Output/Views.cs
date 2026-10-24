using System.Collections;
using CodeGraph.Model;

namespace CodeGraph.Output;

/// <summary>Python's collections.Counter: counts keyed by anything, insertion ordered, stable most_common.</summary>
public sealed class Counter<TKey> : IEnumerable<KeyValuePair<TKey, int>> where TKey : notnull
{
    private readonly Dictionary<TKey, int> _d = [];

    public int this[TKey key]
    {
        get => _d.GetValueOrDefault(key);
        set => _d[key] = value;
    }

    public int Count => _d.Count;

    public void Add(TKey key, int n = 1) => _d[key] = _d.GetValueOrDefault(key) + n;

    public IEnumerable<TKey> Keys => _d.Keys;

    public int Total => _d.Values.Sum();

    public bool ContainsKey(TKey key) => _d.ContainsKey(key);

    /// <summary>Highest counts first, ties in insertion order (like Counter.most_common).</summary>
    public List<KeyValuePair<TKey, int>> MostCommon(int? n = null)
    {
        var l = _d.OrderByDescending(kv => kv.Value).ToList();
        return n is { } k && k < l.Count ? l.GetRange(0, Math.Max(0, k)) : l;
    }

    public IEnumerator<KeyValuePair<TKey, int>> GetEnumerator() => _d.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Derived views over the graph used by the report and the Obsidian notes.</summary>
public sealed class Views
{
    public static readonly string[] CodeRels =
    [
        "calls", "instantiates", "uses_type", "inherits", "calls_native", "includes", "mentions", "imports",
        "uses_component", "calls_api", "renders",
    ];

    public static readonly string[] DbRels = ["reads", "writes", "executes", "calls", "alters", "references", "triggers_on"];

    public static readonly HashSet<string> RoutineKinds =
    [
        "sub", "function", "property", "event_handler", "method", "constructor", "api_declare", "operator", "label",
    ];

    public static readonly HashSet<string> TypeKinds =
    [
        "form", "module", "class", "usercontrol", "designer", "propertypage", "userdocument", "interface",
        "struct", "enum", "record", "component", "type_alias", "installer", "feature", "component_group",
        "package_group", "view",
    ];

    private static readonly List<Edge> NoEdges = [];

    public Graph G { get; }
    public Dictionary<string, List<Edge>> Out { get; }
    public Dictionary<string, List<Edge>> Inn { get; }
    /// <summary>node id -> id of the file it lives in (files map to themselves).</summary>
    public Dictionary<string, string> FileOf { get; } = [];
    /// <summary>code dependencies rolled up to files: file -> (target file -> weight)</summary>
    public Dictionary<string, Counter<string>> FileOut { get; } = [];
    public Dictionary<string, Counter<string>> FileIn { get; } = [];
    /// <summary>file -> (db id, relation, via) -> weight</summary>
    public Dictionary<string, Counter<(string Db, string Rel, string? Via)>> FileDb { get; } = [];
    /// <summary>db id -> (source file id or db id, relation) -> weight</summary>
    public Dictionary<string, Counter<(string Src, string Rel)>> DbUsers { get; } = [];
    public Dictionary<string, List<string>> Children { get; } = [];

    public Views(Graph g)
    {
        G = g;
        Out = g.OutEdges();
        Inn = g.InEdges();
        foreach (var n in g.Nodes)
        {
            if (n.Kind == "file") FileOf[n.Id] = n.Id;
            else if (n.Str("file") is { Length: > 0 } f && !n.Kind.StartsWith("db_", StringComparison.Ordinal))
                FileOf[n.Id] = f;
        }
        foreach (var e in g.Edges)
        {
            string s = e.Source, t = e.Target, rel = e.Relation;
            string tk = g.GetNode(t)?.Kind ?? "", sk = g.GetNode(s)?.Kind ?? "";
            if (tk.StartsWith("db_", StringComparison.Ordinal) && DbRels.Contains(rel))
            {
                bool srcDb = sk.StartsWith("db_", StringComparison.Ordinal);
                // an extractor item (report, patch ...) is a user in its own right, not just its definition file
                bool srcItem = g.GetNode(s)?.Str("extractor") is not null;
                string? src = srcDb || srcItem ? s : FileOf.GetValueOrDefault(s);
                if (!string.IsNullOrEmpty(src))
                {
                    Of(DbUsers, t).Add((src, rel), e.Count);
                    if (!srcDb)
                        Of(FileDb, src).Add((t, rel, e.Str("via")), e.Count);
                }
            }
            else if (CodeRels.Contains(rel))
            {
                string? sf = FileOf.GetValueOrDefault(s), tf = FileOf.GetValueOrDefault(t);
                if (!string.IsNullOrEmpty(sf) && !string.IsNullOrEmpty(tf) && sf != tf)
                {
                    Of(FileOut, sf).Add(tf, e.Count);
                    Of(FileIn, tf).Add(sf, e.Count);
                }
            }
        }
        foreach (var e in g.Edges)
            if (e.Relation == "contains")
                (Children.TryGetValue(e.Source, out var l) ? l : Children[e.Source] = []).Add(e.Target);
    }

    private static Counter<TK> Of<TK>(Dictionary<string, Counter<TK>> d, string key) where TK : notnull =>
        d.TryGetValue(key, out var c) ? c : d[key] = new Counter<TK>();

    public List<Edge> OutOf(string id) => Out.GetValueOrDefault(id) ?? NoEdges;
    public List<Edge> InOf(string id) => Inn.GetValueOrDefault(id) ?? NoEdges;

    public List<string> ChildrenOf(string id) => Children.GetValueOrDefault(id) ?? [];

    public List<string> ProjectFiles(string pid) =>
        ChildrenOf(pid).Where(c => G.GetNode(c)?.Kind == "file").ToList();

    /// <summary>All symbols defined in a file, in line order.</summary>
    public List<string> FileSymbols(string fid)
    {
        var res = new List<string>();
        var stack = new List<string>(ChildrenOf(fid));
        while (stack.Count > 0)
        {
            string s = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (G.GetNode(s) is null || !s.StartsWith("sym:", StringComparison.Ordinal)) continue;
            res.Add(s);
            stack.AddRange(ChildrenOf(s));
        }
        return res.OrderBy(s => G.GetNode(s)!.Int("line") ?? 0).ToList();
    }

    public List<string> ProjectsOf(string fid) =>
        G.GetNode(fid)?.Data["projects"] is System.Text.Json.Nodes.JsonArray a
            ? a.Select(x => x?.ToString() ?? "").ToList()
            : [];

    public Counter<(string Db, string Rel)> ProjectDb(string pid)
    {
        var c = new Counter<(string, string)>();
        foreach (var f in ProjectFiles(pid))
            if (FileDb.TryGetValue(f, out var fd))
                foreach (var (k, n) in fd)
                    c.Add((k.Db, k.Rel), n);
        return c;
    }

    public List<(string Release, string? Version)> ReleasesOf(string nid) =>
        OutOf(nid).Where(e => e.Relation == "released_in").Select(e => (e.Target, e.Str("version"))).ToList();
}
