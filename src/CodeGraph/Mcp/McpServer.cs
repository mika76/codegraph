using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Model;
using CodeGraph.Query;

namespace CodeGraph.Mcp;

/// <summary>
/// A Model Context Protocol server over stdio: JSON-RPC 2.0, one message per line. It exposes the graph queries as
/// read-only tools. graph.json is loaded on first use and loaded again when a build has rewritten it.
/// </summary>
public sealed class McpServer(string graphPath, string reportPath)
{
    /// <summary>Longest tool result; longer output is cut at a line end with a hint to narrow the query.</summary>
    public const int MaxChars = 40_000;

    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private const string Instructions =
        "A code knowledge graph of the configured repositories: projects, files, routines, database tables and " +
        "procedures, API routes, installers and releases, and how they use each other. Start with `search` to find " +
        "a node, then use `show` (its connections; filter with relation/direction), `impact` (what depends on it), " +
        "`path` (how two things are connected) and `source` (read the code). Every tool taking a name also takes a " +
        "node id; ids are printed in {braces}. Links are deterministic but not complete (e.g. URLs built at runtime " +
        "and late binding are not followed), so check important answers against the source.";

    private Graph? _graph;
    private DateTime _stamp;
    private TextWriter _log = TextWriter.Null;

    /// <summary>Read requests until the input ends, writing one response line per request.</summary>
    public void Run(TextReader input, TextWriter output, TextWriter log)
    {
        _log = log;
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            var reply = Handle(line);
            if (reply is null) continue;
            output.Write(ToLine(reply));
            output.Write('\n');
            output.Flush();
        }
    }

    /// <summary>Compact JSON without escaping quotes, backticks or non-ASCII text (control characters still are).</summary>
    internal static string ToLine(JsonNode node)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(w);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>The response to one message, or null for notifications and responses.</summary>
    public JsonObject? Handle(string line)
    {
        JsonObject msg;
        try
        {
            msg = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("expected a JSON object");
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, "Parse error: " + ex.Message);
        }
        var id = msg["id"]?.DeepClone();
        string? method = msg["method"] is JsonValue mv && mv.TryGetValue(out string? m) ? m : null;
        if (method is null)
            return id is null ? null : Error(id, -32600, "Invalid request: no method");
        if (id is null) return null; // notifications (initialized, cancelled, ...) need no answer
        var p = msg["params"] as JsonObject;
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(p),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools() },
                "tools/call" => Call(p),
                _ => throw new RpcException(-32601, $"Method not found: {method}"),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (RpcException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    /// <summary>A tool failure the caller should see as the tool's (error) result.</summary>
    private sealed class ToolException(string message) : Exception(message);

    private static JsonObject Initialize(JsonObject? p)
    {
        string? asked = p?["protocolVersion"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "codegraph", ["version"] = Cli.Version },
            ["instructions"] = Instructions,
        };
    }

    // ---- tool definitions ----

    private static JsonArray Tools() => new(
    [
        Tool("search",
            "Find nodes whose name, qualified name, path or id contains the text (exact matches first). Returns " +
            "labels with node ids. Use it first when you don't know the exact name.",
            new JsonObject
            {
                ["query"] = Str("Text to look for, e.g. a table, routine, file, project or route name."),
                ["kind"] = Str("Only this node kind, e.g. project, file, class, method, sub, function, form, " +
                               "db_table, db_proc, db_view, db_function, route, item."),
                ["repo"] = Str("Only nodes from this repository."),
                ["limit"] = Int("Most results to return.", 25),
            }, "query"),
        Tool("show",
            "A node with its attributes and its connections grouped by relation, each with the neighbour's id. " +
            "Filter with relation and direction, e.g. who writes a table: relation=writes, direction=in. Relations: " +
            "contains, calls, instantiates, uses_type, inherits, calls_native, includes, imports, uses_component, " +
            "calls_api, renders, mentions, uses_value, reads, writes, executes, references, triggers_on, defines, " +
            "alters, depends_on, uses_library, uses_code_of, ships, released_in, opens.",
            new JsonObject
            {
                ["name"] = Str("Node name, path or id."),
                ["relation"] = Str("Only connections with this relation."),
                ["direction"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("in", "out", "both"),
                    ["description"] = "in = things pointing at this node, out = things it points at.",
                    ["default"] = "both",
                },
                ["limit"] = Int("Most connections listed per relation.", 40),
            }, "name"),
        Tool("impact",
            "Everything that depends on a node, directly or indirectly (callers, readers and writers, importers, " +
            "installers that ship it ...), grouped by project and file. Use before changing a table, procedure, " +
            "routine, file or project.",
            new JsonObject
            {
                ["name"] = Str("Node name, path or id."),
                ["depth"] = Int("How many steps to follow.", 3),
            }, "name"),
        Tool("path",
            "The shortest chain of dependencies connecting two nodes, in either direction.",
            new JsonObject
            {
                ["from"] = Str("First node: name, path or id."),
                ["to"] = Str("Second node: name, path or id."),
            }, "from", "to"),
        Tool("source",
            "The source lines of a routine, type, database object or file, read from the repository on disk, with " +
            "line numbers.",
            new JsonObject
            {
                ["name"] = Str("Node name, path or id."),
                ["context"] = Int("Extra lines before and after a definition.", 0),
                ["max_lines"] = Int("Most lines to return.", 200),
            }, "name"),
        Tool("report",
            "The build report: repositories, counts, how many references were resolved, unmatched API calls, " +
            "orphan files and parse errors.",
            new JsonObject()),
    ]);

    private static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode?)r).ToArray()),
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
    };

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Int(string description, int def) =>
        new() { ["type"] = "integer", ["description"] = description, ["default"] = def };

    // ---- tool calls ----

    private JsonObject Call(JsonObject? p)
    {
        string name = p?["name"] is JsonValue v && v.TryGetValue(out string? s) ? s : "";
        var args = p?["arguments"] as JsonObject ?? [];
        Func<JsonObject, string> run = name switch
        {
            "search" => Search,
            "show" => a => Query(w => GraphQuery.Show(Current(), Pick(a, "name"), w, Num(a, "limit", 40, 1, 1000),
                Text(a, "relation"), Direction(a), ids: true)),
            "impact" => a => Query(w => GraphQuery.Impact(Current(), Pick(a, "name"), w, Num(a, "depth", 3, 1, 10))),
            "path" => a => Query(w => GraphQuery.Path(Current(), Pick(a, "from"), Pick(a, "to"), w)),
            "source" => a => Query(w => GraphQuery.Source(Current(), Pick(a, "name"), w, Num(a, "context", 0, 0, 200),
                Num(a, "max_lines", 200, 1, 2000))),
            "report" => _ => File.Exists(reportPath)
                ? File.ReadAllText(reportPath)
                : throw new ToolException($"No report yet at {reportPath}. Run:  codegraph build"),
            _ => throw new RpcException(-32602, $"Unknown tool: {name}"),
        };
        string text;
        bool isError = false;
        try
        {
            text = run(args);
        }
        catch (ToolException ex)
        {
            (text, isError) = (ex.Message, true);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            _log.WriteLine($"codegraph mcp: {name} failed: {ex}");
            (text, isError) = ($"{name} failed: {ex.Message}", true);
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Cut(text) }),
            ["isError"] = isError,
        };
    }

    private static string Query(Action<StringWriter> query)
    {
        var w = new StringWriter { NewLine = "\n" };
        query(w);
        return w.ToString();
    }

    private string Search(JsonObject a)
    {
        var g = Current();
        string query = Need(a, "query");
        string? kind = Text(a, "kind"), repo = Text(a, "repo");
        int limit = Num(a, "limit", 25, 1, 500);
        var hits = GraphQuery.Search(g, query, kind, repo);
        var w = new StringWriter { NewLine = "\n" };
        if (hits.Count == 0)
        {
            w.WriteLine($"Nothing matches '{query}'.");
            if (!string.IsNullOrEmpty(kind) && !g.Nodes.Any(n => string.Equals(n.Kind, kind, StringComparison.OrdinalIgnoreCase)))
                w.WriteLine("Kinds in the graph: " + string.Join(", ", g.Nodes.Select(n => n.Kind).Distinct().Order(StringComparer.Ordinal)));
            if (!string.IsNullOrEmpty(repo) && !g.Nodes.Any(n => n.Kind == "repo" && string.Equals(n.Name, repo, StringComparison.OrdinalIgnoreCase)))
                w.WriteLine("Repositories: " + string.Join(", ", g.Nodes.Where(n => n.Kind == "repo").Select(n => n.Name)));
            return w.ToString();
        }
        w.WriteLine($"{hits.Count} match{(hits.Count == 1 ? "" : "es")} for '{query}':");
        foreach (var h in hits.Take(limit)) w.WriteLine($"  {GraphQuery.Label(g, h)}  {{{h}}}");
        if (hits.Count > limit) w.WriteLine($"  ... {hits.Count - limit} more (narrow with kind or repo, or raise limit)");
        return w.ToString();
    }

    /// <summary>The node id a name argument means; an ambiguous or unknown name ends the call with a hint.</summary>
    private string Pick(JsonObject a, string key)
    {
        var g = Current();
        string name = Need(a, key);
        var nid = GraphQuery.Resolve(g, name, out var ambiguous);
        if (nid is null) throw new ToolException($"Nothing matches '{name}'. Try the search tool.");
        if (ambiguous.Count == 0) return nid;
        var w = new StringWriter { NewLine = "\n" };
        w.WriteLine($"'{name}' matches several things. Call again with one of these ids as {key}:");
        foreach (var h in ambiguous.Take(25)) w.WriteLine($"  {GraphQuery.Label(g, h)}  {{{h}}}");
        if (ambiguous.Count > 25) w.WriteLine($"  ... {ambiguous.Count - 25} more");
        throw new ToolException(w.ToString());
    }

    private static string? Text(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue(out string? s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static string Need(JsonObject a, string key) =>
        Text(a, key) ?? throw new ToolException($"Missing argument: {key}");

    private static int Num(JsonObject a, string key, int def, int min, int max)
    {
        int n = def;
        if (a[key] is JsonValue v && !v.TryGetValue(out n) && !(v.TryGetValue(out string? s) && int.TryParse(s, out n)))
            n = def;
        return Math.Clamp(n, min, max);
    }

    private static string Direction(JsonObject a) => Text(a, "direction")?.ToLowerInvariant() switch
    {
        null or "both" => "both",
        "in" or "incoming" => "in",
        "out" or "outgoing" => "out",
        var d => throw new ToolException($"direction must be in, out or both (got '{d}')"),
    };

    internal static string Cut(string text)
    {
        if (text.Length <= MaxChars) return text;
        int end = text.LastIndexOf('\n', MaxChars);
        if (end < MaxChars / 2) end = MaxChars;
        return text[..end] + $"\n... output cut at {end:N0} of {text.Length:N0} characters; narrow the query " +
               "(relation, direction, depth, limit).";
    }

    /// <summary>The graph, loaded again when graph.json has changed since the last call.</summary>
    private Graph Current()
    {
        if (!File.Exists(graphPath)) throw new ToolException($"No graph yet at {graphPath}. Run:  codegraph build");
        var stamp = File.GetLastWriteTimeUtc(graphPath);
        if (_graph is null || stamp != _stamp)
        {
            try
            {
                _graph = Model.Graph.Load(graphPath);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                throw new ToolException($"Could not read {graphPath} (is a build writing it?): {ex.Message}");
            }
            _stamp = stamp;
            _log.WriteLine($"codegraph mcp: loaded {graphPath} ({_graph.Nodes.Count:N0} nodes, {_graph.Edges.Count:N0} edges)");
        }
        return _graph;
    }
}
