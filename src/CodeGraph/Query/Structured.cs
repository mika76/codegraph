using System.Text.Json.Nodes;
using CodeGraph.Model;

namespace CodeGraph.Query;

/// <summary>
/// The queries' results as JSON for programs (MCP <c>structuredContent</c>): the same facts the text answers print,
/// with every node in one shape (<see cref="NodeJson"/>) and every connection in another (<see cref="Connection"/>).
/// </summary>
public static class Structured
{
    /// <summary>Most items in one structured list (impact can reach thousands); the rest is counted, not listed.</summary>
    public const int MaxItems = 2000;

    /// <summary>
    /// A node: id, kind, name, plus qual, repo, file (repository-relative path) and line when it has them. A file's
    /// file is its own path.
    /// </summary>
    public static JsonObject NodeJson(Graph g, string id)
    {
        var n = g.GetNode(id);
        var o = new JsonObject { ["id"] = id, ["kind"] = n?.Kind ?? "", ["name"] = n?.Name ?? "" };
        if (n is null) return o;
        if (!string.IsNullOrEmpty(n.Str("qual"))) o["qual"] = n.Str("qual");
        if (GraphQuery.RepoOf(n) is { } repo) o["repo"] = repo;
        var fn = n.Kind == "file" ? n : g.GetNode(n.Str("file") ?? "");
        if (fn is not null && !string.IsNullOrEmpty(fn.Str("path")))
        {
            o["file"] = fn.Str("path");
            o["file_id"] = fn.Id;
            o["repo"] ??= fn.Str("repo");
        }
        else if (!string.IsNullOrEmpty(n.Str("path")) && n.Kind != "repo")
            o["path"] = n.Str("path");
        if (n.Int("line") is { } line && n.Kind != "file") o["line"] = line;
        return o;
    }

    /// <summary>The access mode a database relation stands for: read, write, exec, alter, call; null for others.</summary>
    public static string? Mode(string relation) => relation switch
    {
        "reads" => "read",
        "writes" => "write",
        "executes" => "exec",
        "alters" => "alter",
        "defines" => "define",
        "references" => "reference",
        "triggers_on" => "trigger",
        _ => null,
    };

    /// <summary>
    /// One connection seen from <paramref name="from"/>: relation, direction (out = it points at the neighbour, in =
    /// the neighbour points at it), the neighbour, and the edge's count, mode, line, via and dynamic flag.
    /// </summary>
    public static JsonObject Connection(Graph g, Edge e, string from)
    {
        bool outgoing = e.Source == from;
        var o = new JsonObject
        {
            ["relation"] = e.Relation,
            ["direction"] = outgoing ? "out" : "in",
            ["id"] = outgoing ? e.Target : e.Source,
            ["node"] = NodeJson(g, outgoing ? e.Target : e.Source),
            ["count"] = e.Count,
        };
        if (Mode(e.Relation) is { } mode && g.GetNode(e.Target)?.Kind is { } tk && (tk.StartsWith("db_", StringComparison.Ordinal) || tk == "column"))
            o["mode"] = mode;
        if (e.Int("line") is { } line) o["line"] = line;
        if (!string.IsNullOrEmpty(e.Str("via"))) o["via"] = e.Str("via");
        if (e.Data["dynamic"] is JsonValue dv && dv.TryGetValue(out bool dyn) && dyn) o["dynamic"] = true;
        if (!string.IsNullOrEmpty(e.Str("version"))) o["version"] = e.Str("version");
        return o;
    }

    public static JsonObject Search(Graph g, string query, List<string> hits, int limit) => new()
    {
        ["query"] = query,
        ["total"] = hits.Count,
        ["results"] = Arr(hits.Take(limit).Select(h => NodeJson(g, h))),
        ["truncated"] = hits.Count > limit,
    };

    /// <summary>The node with its attributes and connections, filtered and limited per relation like <c>show</c>.</summary>
    public static JsonObject Show(Graph g, string nid, int limit, string? relation, string direction)
    {
        var n = g.GetNode(nid)!;
        var node = NodeJson(g, nid);
        var attrs = new JsonObject();
        foreach (var k in new[] { "version", "output", "ptype", "loc", "lines", "signature", "definition", "columns", "projects", "route_name", "meta", "routes" })
            if (CodeGraph.Knowledge.KnowledgeLoader.Truthy(n.Data[k]))
                attrs[k] = n.Data[k]!.DeepClone();
        bool wantOut = direction is "both" or "out", wantIn = direction is "both" or "in";
        var groups = new Dictionary<(string Rel, bool Out), List<Edge>>();
        foreach (var e in g.Edges)
        {
            if (!string.IsNullOrEmpty(relation) && !string.Equals(e.Relation, relation, StringComparison.OrdinalIgnoreCase)) continue;
            bool isOut = e.Source == nid;
            if (isOut ? !wantOut : e.Target != nid || !wantIn) continue;
            (groups.TryGetValue((e.Relation, isOut), out var l) ? l : groups[(e.Relation, isOut)] = []).Add(e);
        }
        var conns = new JsonArray();
        var summary = new JsonArray();
        foreach (var ((rel, isOut), es0) in groups.OrderBy(kv => kv.Key.Out ? 0 : 1).ThenBy(kv => kv.Key.Rel, StringComparer.Ordinal))
        {
            var es = es0.OrderByDescending(e => e.Count).ToList();
            summary.Add((JsonNode?)new JsonObject
            {
                ["relation"] = rel, ["direction"] = isOut ? "out" : "in", ["total"] = es.Count,
                ["listed"] = Math.Min(es.Count, limit),
            });
            foreach (var e in es.Take(limit)) conns.Add((JsonNode?)Connection(g, e, nid));
        }
        return new JsonObject
        {
            ["node"] = node,
            ["attributes"] = attrs,
            ["connections"] = conns,
            ["groups"] = summary,
        };
    }

    /// <summary>
    /// Everything that depends on the node within <paramref name="depth"/> steps, nearest first, each with its
    /// distance and the projects its file is built into.
    /// </summary>
    public static JsonObject Impact(Graph g, string nid, int depth)
    {
        var (start, seen) = GraphQuery.Reach(g, nid, depth);
        var affected = seen.Where(kv => !start.Contains(kv.Key) && g.HasNode(kv.Key))
            .OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var items = new JsonArray();
        foreach (var (id, dist) in affected.Take(MaxItems))
        {
            var o = NodeJson(g, id);
            o["distance"] = dist;
            if (Projects(g, id) is { Count: > 0 } projs) o["projects"] = Arr(projs.Select(p => (JsonNode?)p));
            items.Add((JsonNode?)o);
        }
        return new JsonObject
        {
            ["node"] = NodeJson(g, nid),
            ["depth"] = depth,
            ["total"] = affected.Count,
            ["affected"] = items,
            ["truncated"] = affected.Count > MaxItems,
        };
    }

    /// <summary>Names of the projects the node's file is built into (a project is its own).</summary>
    public static List<string> Projects(Graph g, string id)
    {
        var n = g.GetNode(id);
        if (n is null) return [];
        if (n.Kind == "project") return [n.Name];
        var fn = n.Kind == "file" ? n : g.GetNode(n.Str("file") ?? "");
        return (fn?.Data["projects"] as JsonArray)?.Select(p => g.GetNode(p?.ToString() ?? "")?.Name ?? p?.ToString() ?? "")
            .Order(StringComparer.Ordinal).ToList() ?? [];
    }

    public static JsonObject Path(Graph g, string s, string t, int maxLen)
    {
        var (steps, mixed) = GraphQuery.FindPath(g, s, t, maxLen);
        return new JsonObject
        {
            ["from"] = NodeJson(g, s),
            ["to"] = NodeJson(g, t),
            ["found"] = steps is not null,
            ["one_way"] = steps is not null && !mixed,
            ["steps"] = Arr((steps ?? []).Select(st => (JsonNode?)new JsonObject
            {
                ["relation"] = st.Rel,
                ["direction"] = st.Dir == "->" ? "out" : "in",
                ["id"] = st.Node,
                ["node"] = NodeJson(g, st.Node),
            })),
        };
    }

    public static JsonObject Source(Graph g, string nid, GraphQuery.SourceLines src)
    {
        var o = new JsonObject { ["node"] = NodeJson(g, nid) };
        if (src.FullPath is not null) o["full_path"] = src.FullPath;
        if (src.Problem is not null)
        {
            o["problem"] = src.Problem;
            return o;
        }
        o["start_line"] = src.From;
        o["end_line"] = src.From + src.Lines.Count - 1;
        o["lines"] = Arr(src.Lines.Select(l => (JsonNode?)l));
        o["more_lines"] = src.More;
        return o;
    }

    public static JsonArray Arr(IEnumerable<JsonNode?> items) => new(items.ToArray());
}
