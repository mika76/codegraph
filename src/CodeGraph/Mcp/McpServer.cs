using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Knowledge;
using CodeGraph.Model;
using CodeGraph.Query;

namespace CodeGraph.Mcp;

/// <summary>
/// A Model Context Protocol server over stdio: JSON-RPC 2.0, one message per line. It exposes the graph queries as
/// read-only tools, plus <c>remember</c>, which adds a fact or note to a knowledge file. graph.json is loaded on
/// first use and loaded again when a build has rewritten it.
/// </summary>
public sealed class McpServer(string graphPath, string reportPath, string? exePath = null, KnowledgeTargets? knowledge = null,
    Config? config = null)
{
    // the executable this server runs from and its time stamp at start, to notice a newer build on disk
    private readonly string? _exe = exePath ?? Environment.ProcessPath;
    private readonly DateTime _exeStamp = StampOf(exePath ?? Environment.ProcessPath);
    private bool _restartNoted;

    private static DateTime StampOf(string? path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    private bool ExeUpdated() => _exeStamp != DateTime.MinValue && StampOf(_exe) > _exeStamp;

    /// <summary>Longest tool result; longer output is cut at a line end with a hint to narrow the query.</summary>
    public const int MaxChars = 40_000;

    private static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private const string Instructions =
        "A code knowledge graph of the configured repositories: projects, files, routines, database tables and " +
        "procedures, API routes, installers and releases, and how they use each other. Start with `search` to find " +
        "a node, then use `show` (its connections; filter with relation/direction), `impact` (what depends on it), " +
        "`path` (how two things are connected), `source` (read the code) and `db_usage` (who reads or writes a table, " +
        "column or procedure, up to the screens and API endpoints). Those tools need an exact node id, name " +
        "or path (ids are printed in {braces}), or a pattern with * as wildcard; they never guess. Links are " +
        "deterministic but not complete (e.g. URLs built at runtime " +
        "and late binding are not followed), so check important answers against the source. When you establish " +
        "something the graph does not show - a link it misses, what a table, setting or screen is for, a rule the " +
        "user tells you - offer to record it with `remember`, so later sessions know it too. Record only what you " +
        "checked in the source or were told, never links the graph already has; if the same kind of link is missing " +
        "again and again, say so (a parser gap) instead of recording each one.";

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
            var node = JsonNode.Parse(line);
            if (node is not JsonObject obj)
                // valid JSON, but not one request (batches are not part of MCP since 2025-06-18)
                return Error(null, -32600, node is JsonArray ? "Invalid request: batches are not supported" : "Invalid request: expected a JSON object");
            msg = obj;
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
    private sealed class ToolException(string message, List<string>? candidates = null) : Exception(message)
    {
        public List<string>? Candidates { get; } = candidates;
    }

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

    private const string NameHelp =
        "Node id (from search, shown in {braces}), exact name, qualified name or path; use * as a wildcard " +
        "(e.g. db:order*, *OrderLine*). Several matches or none return the candidates with their ids.";

    private static JsonArray Tools() => new(
    [
        Tool("search",
            "Find nodes whose name, qualified name, path or id contains the text (exact matches first). Returns " +
            "labels with node ids. Use it first when you don't know the exact name.",
            new JsonObject
            {
                ["query"] = Str("Text to look for, e.g. a table, routine, file, project or route name."),
                ["kind"] = Str("Only this node kind, e.g. project, file, class, method, sub, function, form, " +
                               "db_table, db_proc, db_view, column, route, installer, release, or a kind from your own file " +
                               "formats (e.g. report). An unknown kind lists the kinds in the graph."),
                ["repo"] = Str("Only nodes from this repository."),
                ["limit"] = Int("Most results to return.", 25),
            }, "query"),
        Tool("show",
            "A node with its attributes and its connections grouped by relation, each with the neighbour's id. " +
            "Filter with relation and direction, e.g. who writes a table: relation=writes, direction=in. Common " +
            "relations: contains, calls, instantiates, uses_type, inherits, imports, calls_api, reads, writes, " +
            "executes, references, depends_on, uses_library, ships, released_in. Your own file formats add more; an " +
            "unknown relation returns every relation in this graph.",
            new JsonObject
            {
                ["name"] = Str(NameHelp),
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
                ["name"] = Str(NameHelp),
                ["depth"] = Int("How many steps to follow.", 3),
            }, "name"),
        Tool("path",
            "The shortest chain connecting two nodes. A one-way chain (from uses ... to, or to uses ... from) is " +
            "preferred; a chain that changes direction (both use the same thing) is only returned when there is " +
            "none, and is marked as such.",
            new JsonObject
            {
                ["from"] = Str("First node. " + NameHelp),
                ["to"] = Str("Second node. " + NameHelp),
                ["max_len"] = Int("Longest chain to look for, in steps.", 8),
            }, "from", "to"),
        Tool("source",
            "The source lines of a routine, type, database object or file, read from the repository on disk, with " +
            "line numbers.",
            new JsonObject
            {
                ["name"] = Str(NameHelp),
                ["context"] = Int("Extra lines before and after a definition.", 0),
                ["max_lines"] = Int("Most lines to return.", 200),
            }, "name"),
        Tool("db_usage",
            "Who uses a database table, view, procedure, function or column, in one answer: the direct users with " +
            "read / write / exec and file:line (confirmed: resolved links), possible users (strings and SQL that name " +
            "it but could not be linked, e.g. a column of a statement with several tables; check them in the source), " +
            "and the entry points above them - forms, components, views, client routes and API endpoints - found by " +
            "walking up the callers. Use before renaming or dropping a table, column or procedure.",
            new JsonObject
            {
                ["name"] = Str("A db: or col: id, or the name of a database object or column (e.g. Orders, Orders.Total). " + NameHelp),
                ["depth"] = Int("How many caller steps to walk up from each user to find entry points.", 4),
                ["possible"] = new JsonObject
                {
                    ["type"] = "boolean", ["default"] = true,
                    ["description"] = "Also list possible users (unlinked strings and SQL names) from the parse cache.",
                },
            }, "name"),
        Tool("report",
            "The build report. Without a section: the summary (counts, languages, how many references were " +
            "resolved) and the list of sections, e.g. projects, unused database objects, procedures called but " +
            "not found, orphan files, projects with missing members.",
            new JsonObject
            {
                ["section"] = Str("Section to return: any part of its heading, e.g. \"orphan\" or \"not found\"."),
            }),
        Tool("text_search",
            "Plain text search for a name in the configured repositories (the files a build reads, ignore rules " +
            "respected), for what the graph can't link: SQL built at runtime, names in config files, late binding. " +
            "Each hit is a text match only, with the routine it is in; linked = the graph already links that routine " +
            "to something of that name, so unlinked hits are the ones the graph may have missed.",
            new JsonObject
            {
                ["text"] = Str("The name to look for, e.g. a table, column or procedure name (case is ignored)."),
                ["repo"] = Str("Only this repository."),
                ["whole_word"] = new JsonObject
                {
                    ["type"] = "boolean", ["default"] = true,
                    ["description"] = "Only matches not inside a longer identifier (Orders, not OrdersArchive).",
                },
                ["limit"] = Int("Most hits to return.", 100),
            }, "text"),
        Tool("status",
            "When the graph was built, its size, and the branch and commit of each repository then and now (changed " +
            "= the graph may be out of date; run codegraph build).",
            new JsonObject()),
        WriteTool(Tool("remember",
            "Record a fact (subject, relation, object) or a note about a node in the knowledge files, which every " +
            "build adds to the graph and its notes. A node of a repository goes to that repository's own knowledge " +
            "file (committed with its code), anything else to the central one. Existing comments and entries are " +
            "kept. Ask the user before recording; it shows up after the next `codegraph build`.",
            new JsonObject
            {
                ["subject"] = Str("What the fact or note is about: node id (from search, shown in {braces}), exact name, " +
                                  "qualified name or path. A name that matches nothing becomes a concept (e.g. a team or business process)."),
                ["relation"] = Str("For a fact: how the subject relates to the object, in a few words, e.g. \"calls at runtime\", \"is filled by\", \"owned by\"."),
                ["object"] = Str("For a fact: the other node (same rules as subject), a concept, or a URL."),
                ["note"] = Str("Text for the subject's note; with a relation, a remark on the fact."),
                ["source"] = Str("How you know, e.g. \"verified in Orders/Save.cs:120\" or \"told by the user\". Written as a comment."),
            }, "subject", "source")),
    ]);

    private static JsonObject WriteTool(JsonObject tool)
    {
        tool["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false };
        return tool;
    }

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
        ["outputSchema"] = OutputSchema(name),
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
    };

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Int(string description, int def) =>
        new() { ["type"] = "integer", ["description"] = description, ["default"] = def };

    // ---- output schemas (structuredContent) ----

    private static JsonObject T(string type) => new() { ["type"] = type };

    private static JsonObject Obj(JsonObject props, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = props,
        ["required"] = new JsonArray(required.Select(r => (JsonNode?)r).ToArray()),
    };

    private static JsonObject ArrOf(JsonObject item) => new() { ["type"] = "array", ["items"] = item };

    /// <summary>The shape of every node in structured results (<see cref="Structured.NodeJson"/>).</summary>
    private static JsonObject NodeSchema() => Obj(new JsonObject
    {
        ["id"] = T("string"), ["kind"] = T("string"), ["name"] = T("string"), ["qual"] = T("string"),
        ["repo"] = T("string"), ["file"] = Str("Repository-relative path of the node's file."), ["file_id"] = T("string"),
        ["path"] = T("string"), ["line"] = T("integer"),
        ["database"] = Str("The database a db: or col: node belongs to, when the config names one."),
    }, "id", "kind", "name");

    private static JsonObject Direction() => new() { ["type"] = "string", ["enum"] = new JsonArray("in", "out") };

    /// <summary>The shape of every connection (<see cref="Structured.Connection"/>).</summary>
    private static JsonObject ConnectionSchema() => Obj(new JsonObject
    {
        ["relation"] = T("string"), ["direction"] = Direction(), ["id"] = Str("The neighbour's node id."),
        ["node"] = NodeSchema(), ["count"] = T("integer"),
        ["mode"] = Str("read, write, exec, alter, define, reference or trigger, for a link to a database object or column."),
        ["line"] = T("integer"), ["via"] = T("string"), ["dynamic"] = T("boolean"), ["version"] = T("string"),
    }, "relation", "direction", "id", "node");

    /// <summary>
    /// A tool's structuredContent schema. Nothing is required at the top: a failed call (isError) returns
    /// <c>{error, candidates}</c> instead.
    /// </summary>
    private static JsonObject OutputSchema(string tool)
    {
        var props = tool switch
        {
            "search" => new JsonObject
            {
                ["query"] = T("string"), ["total"] = T("integer"), ["results"] = ArrOf(NodeSchema()), ["truncated"] = T("boolean"),
            },
            "show" => new JsonObject
            {
                ["node"] = NodeSchema(), ["attributes"] = T("object"), ["connections"] = ArrOf(ConnectionSchema()),
                ["groups"] = ArrOf(Obj(new JsonObject
                {
                    ["relation"] = T("string"), ["direction"] = Direction(), ["total"] = T("integer"), ["listed"] = T("integer"),
                }, "relation", "direction", "total", "listed")),
            },
            "impact" => new JsonObject
            {
                ["node"] = NodeSchema(), ["depth"] = T("integer"), ["total"] = T("integer"), ["truncated"] = T("boolean"),
                ["affected"] = ArrOf(With(NodeSchema(), ("distance", T("integer")), ("projects", ArrOf(T("string"))))),
            },
            "path" => new JsonObject
            {
                ["from"] = NodeSchema(), ["to"] = NodeSchema(), ["found"] = T("boolean"), ["one_way"] = T("boolean"),
                ["steps"] = ArrOf(Obj(new JsonObject
                {
                    ["relation"] = T("string"), ["direction"] = Direction(), ["id"] = T("string"), ["node"] = NodeSchema(),
                }, "relation", "direction", "id", "node")),
            },
            "source" => new JsonObject
            {
                ["node"] = NodeSchema(), ["full_path"] = T("string"), ["start_line"] = T("integer"), ["end_line"] = T("integer"),
                ["lines"] = ArrOf(T("string")), ["more_lines"] = T("integer"), ["problem"] = T("string"),
            },
            "db_usage" => DbUsageSchema(),
            "remember" => new JsonObject
            {
                ["recorded"] = T("boolean"), ["message"] = Str("What was written, and to which knowledge file."),
            },
            "text_search" => new JsonObject
            {
                ["query"] = T("string"), ["total"] = T("integer"), ["files_searched"] = T("integer"),
                ["files_with_hits"] = T("integer"), ["truncated"] = T("boolean"),
                ["hits"] = ArrOf(Obj(new JsonObject
                {
                    ["repo"] = T("string"), ["file"] = T("string"), ["file_id"] = T("string"), ["line"] = T("integer"),
                    ["column"] = T("integer"), ["text"] = Str("The line, trimmed."),
                    ["confidence"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("text") },
                    ["node"] = NodeSchema(),
                    ["linked"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "The graph already links the routine (or file) to something of that name.",
                    },
                }, "repo", "file", "file_id", "line", "column", "text", "confidence")),
            },
            "status" => new JsonObject
            {
                ["version"] = T("string"),
                ["graph"] = Obj(new JsonObject
                {
                    ["path"] = T("string"), ["generated"] = Str("Local build time, yyyy-MM-dd HH:mm."),
                    ["modified_utc"] = T("string"), ["nodes"] = T("integer"), ["edges"] = T("integer"),
                }),
                ["report"] = T("boolean"), ["cache"] = T("boolean"),
                ["repos"] = ArrOf(Obj(new JsonObject
                {
                    ["name"] = T("string"), ["path"] = T("string"), ["exists"] = T("boolean"),
                    ["branch"] = Str("Branch at build time."), ["commit"] = Str("Commit id at build time."),
                    ["current_branch"] = T("string"), ["current_commit"] = T("string"),
                    ["changed"] = new JsonObject { ["type"] = "boolean", ["description"] = "The branch or commit has moved since the build." },
                }, "name", "path", "changed")),
            },
            "report" => new JsonObject
            {
                ["section"] = T("string"), ["text"] = T("string"), ["sections"] = ArrOf(T("string")),
            },
            _ => new JsonObject(),
        };
        props["error"] = Str("Set when the call failed (isError).");
        props["candidates"] = With(ArrOf(NodeSchema()), ("description", "On a failed name lookup: the matching or closest nodes."));
        return Obj(props);
    }

    private static JsonObject DbUsageSchema()
    {
        var confidence = new JsonObject
        {
            ["type"] = "string", ["enum"] = new JsonArray("confirmed", "possible"),
            ["description"] = "confirmed: a resolved link in the graph; possible: a name match that was not linked.",
        };
        var user = Obj(new JsonObject
        {
            ["id"] = T("string"), ["node"] = NodeSchema(), ["confidence"] = confidence.DeepClone(),
            ["layer"] = new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray("app", "script", "database"),
                ["description"] = "app: application code; script: a SQL file (patch, deploy script); database: a DB object.",
            },
            ["relation"] = T("string"),
            ["mode"] = Str("read, write, exec, alter, define, reference or trigger."),
            ["line"] = T("integer"), ["via"] = T("string"), ["dynamic"] = T("boolean"), ["count"] = T("integer"),
            ["file"] = Str("Possible users: the file of the match."), ["file_id"] = T("string"),
            ["text"] = Str("Possible users: the name as written in the code."), ["reason"] = T("string"),
            ["projects"] = ArrOf(T("string")),
        }, "id", "node", "confidence", "layer");
        var entry = Obj(new JsonObject
        {
            ["id"] = T("string"), ["node"] = NodeSchema(),
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("form", "component", "view", "route", "api") },
            ["distance"] = Str("Caller steps from the nearest user (0: the user itself)."),
            ["confidence"] = confidence.DeepClone(), ["through"] = ArrOf(T("string")), ["routes"] = ArrOf(T("string")),
            ["projects"] = ArrOf(T("string")),
        }, "id", "node", "kind", "distance", "confidence");
        entry["properties"]!["distance"]!["type"] = "integer";
        return new JsonObject
        {
            ["target"] = NodeSchema(), ["depth"] = T("integer"), ["possible_checked"] = T("boolean"),
            ["counts"] = Obj(new JsonObject
            {
                ["confirmed"] = T("integer"), ["possible"] = T("integer"), ["confirmed_app"] = T("integer"),
                ["confirmed_database"] = T("integer"), ["confirmed_script"] = T("integer"), ["entry_points"] = T("integer"),
            }),
            ["users"] = ArrOf(user), ["entry_points"] = ArrOf(entry),
            ["entry_points_by_project"] = ArrOf(Obj(new JsonObject { ["project"] = T("string"), ["count"] = T("integer") }, "project", "count")),
            ["truncated"] = T("boolean"),
        };
    }

    private static JsonObject With(JsonObject o, params (string Key, JsonNode? Value)[] extra)
    {
        foreach (var (k, v) in extra)
            if (k == "description" || o["properties"] is not JsonObject p) o[k] = v;
            else p[k] = v;
        return o;
    }

    // ---- tool calls ----

    private JsonObject Call(JsonObject? p)
    {
        string name = p?["name"] is JsonValue v && v.TryGetValue(out string? s) ? s : "";
        var args = p?["arguments"] as JsonObject ?? [];
        Func<JsonObject, (string Text, JsonObject Data)> run = name switch
        {
            "search" => Search,
            "show" => Show,
            "impact" => Impact,
            "path" => PathTool,
            "source" => SourceTool,
            "db_usage" => DbUsageTool,
            "report" => Report,
            "text_search" => TextSearchTool,
            "status" => Status,
            "remember" => Remember,
            _ => throw new RpcException(-32602, $"Unknown tool: {name}"),
        };
        string text;
        JsonObject data;
        _notes.Clear();
        bool isError = false;
        try
        {
            (text, data) = run(args);
        }
        catch (ToolException ex)
        {
            (text, isError) = (ex.Message, true);
            data = new JsonObject { ["error"] = ex.Message };
            if (ex.Candidates is { Count: > 0 } cands && _graph is { } g)
                data["candidates"] = Structured.Arr(cands.Take(25).Select(c => (JsonNode?)Structured.NodeJson(g, c)));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            _log.WriteLine($"codegraph mcp: {name} failed: {ex}");
            (text, isError) = ($"{name} failed: {ex.Message}", true);
            data = new JsonObject { ["error"] = text };
        }
        text = Cut(text);
        if (_notes.Count > 0)
        {
            text += "\n(" + string.Join(" ", _notes) + ")";
            data["notes"] = Structured.Arr(_notes.Select(n => (JsonNode?)n));
        }
        if (!_restartNoted && ExeUpdated())
        {
            // graph.json is reloaded on its own, but new code needs a new process
            _restartNoted = true;
            const string note = "codegraph.exe has been updated since this server started; restart the codegraph MCP " +
                                "server (or the session) to use the new version.";
            _log.WriteLine("codegraph mcp: " + note);
            text += "\n\n(Note: " + note + ")";
            (data["notes"] as JsonArray ?? (JsonArray)(data["notes"] = new JsonArray())).Add((JsonNode?)note);
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = data,
            ["isError"] = isError,
        };
    }

    private static string Query(Action<StringWriter> query)
    {
        var w = new StringWriter { NewLine = "\n" };
        query(w);
        return w.ToString();
    }

    private (string, JsonObject) Show(JsonObject a)
    {
        var g = Current();
        string id = Pick(a, "name");
        int limit = Num(a, "limit", 40, 1, 1000);
        string? rel = Relation(a);
        string dir = Direction(a);
        return (Query(w => GraphQuery.Show(g, id, w, limit, rel, dir, ids: true)), Structured.Show(g, id, limit, rel, dir));
    }

    private (string, JsonObject) Impact(JsonObject a)
    {
        var g = Current();
        string id = Pick(a, "name");
        int depth = Num(a, "depth", 3, 1, 10);
        return (Query(w => GraphQuery.Impact(g, id, w, depth, ids: true)), Structured.Impact(g, id, depth));
    }

    private (string, JsonObject) PathTool(JsonObject a)
    {
        var g = Current();
        string from = Pick(a, "from"), to = Pick(a, "to");
        int maxLen = Num(a, "max_len", 8, 1, 20);
        return (Query(w => GraphQuery.Path(g, from, to, w, maxLen, ids: true)), Structured.Path(g, from, to, maxLen));
    }

    private (string, JsonObject) SourceTool(JsonObject a)
    {
        var g = Current();
        string id = Pick(a, "name");
        int context = Num(a, "context", 0, 0, 200), maxLines = Num(a, "max_lines", 200, 1, 2000);
        return (Query(w => GraphQuery.Source(g, id, w, context, maxLines)),
            Structured.Source(g, id, GraphQuery.ReadSource(g, id, context, maxLines)));
    }

    private (string, JsonObject) DbUsageTool(JsonObject a)
    {
        var g = Current();
        string id = Pick(a, "name");
        var n = g.GetNode(id)!;
        if (!DbUsage.IsDbNode(n))
            throw new ToolException($"{GraphQuery.Label(g, id)} is not a database object or column; db_usage takes a " +
                                    "table, view, procedure, function or column. Use impact for other things.");
        int depth = Num(a, "depth", 4, 0, 8);
        bool possible = a["possible"] is not JsonValue pv || !pv.TryGetValue(out bool b) || b;
        var res = DbUsage.Find(g, id, depth, possible ? Refs(g) : null);
        return (Query(w => DbUsage.Write(g, res, w, ids: true)), DbUsage.ToJson(g, res));
    }

    private (string, JsonObject) TextSearchTool(JsonObject a)
    {
        if (config is null) throw new ToolException("text_search needs the config: run the server as  codegraph mcp --config <file>");
        string text = Need(a, "text");
        if (text.Length < 2) throw new ToolException("text must be at least 2 characters");
        string? repo = Text(a, "repo");
        if (repo is not null && !config.Repos.Any(r => string.Equals(r.Name, repo, StringComparison.OrdinalIgnoreCase)))
            throw new ToolException($"Unknown repo '{repo}'. Repositories: " + string.Join(", ", config.Repos.Select(r => r.Name)));
        bool wholeWord = a["whole_word"] is not JsonValue wv || !wv.TryGetValue(out bool ww) || ww;
        int limit = Num(a, "limit", 100, 1, 2000);
        Graph? g = File.Exists(graphPath) ? Current() : null;
        var res = TextSearch.Find(config, text, repo, wholeWord);
        return (Query(w => TextSearch.Write(g, res, w, limit)), TextSearch.ToJson(g, res, limit));
    }

    /// <summary>When the graph was built, from which branch and commit of each repo, and whether those have moved since.</summary>
    private (string, JsonObject) Status(JsonObject a)
    {
        var g = Current();
        var w = new StringWriter { NewLine = "\n" };
        string modified = File.GetLastWriteTimeUtc(graphPath).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        string cache = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(graphPath))!,
            CodeGraph.Scan.Scanner.CacheFileName);
        w.WriteLine($"codegraph {Cli.Version}; graph built {g.Meta["generated"]} ({modified}): " +
                    $"{g.Nodes.Count:N0} nodes, {g.Edges.Count:N0} edges\n  {graphPath}");
        var repos = new JsonArray();
        foreach (var r in g.Nodes.Where(n => n.Kind == "repo"))
        {
            string path = r.Str("path") ?? "";
            string? branch = r.Str("branch"), commit = r.Str("commit");
            string? nowBranch = Knowledge.KnowledgeLoader.GitBranch(path), nowCommit = Knowledge.KnowledgeLoader.GitCommit(path);
            bool changed = branch != nowBranch || (commit is not null && commit != nowCommit);
            var o = new JsonObject { ["name"] = r.Name, ["path"] = path, ["exists"] = Directory.Exists(path) };
            if (branch is not null) o["branch"] = branch;
            if (commit is not null) o["commit"] = commit;
            if (nowBranch is not null) o["current_branch"] = nowBranch;
            if (nowCommit is not null) o["current_commit"] = nowCommit;
            o["changed"] = changed;
            repos.Add((JsonNode?)o);
            string Short(string? c) => c is null ? "?" : c[..Math.Min(12, c.Length)];
            w.WriteLine($"  {r.Name}: {(branch ?? "(no git)")} {(commit is null ? "" : Short(commit))}" +
                        (changed ? $"  - now {nowBranch ?? "(no git)"} {Short(nowCommit)}: rebuild to catch up" : ""));
        }
        return (w.ToString(), new JsonObject
        {
            ["version"] = Cli.Version,
            ["graph"] = new JsonObject
            {
                ["path"] = System.IO.Path.GetFullPath(graphPath),
                ["generated"] = g.Meta["generated"]?.DeepClone(),
                ["modified_utc"] = modified,
                ["nodes"] = g.Nodes.Count,
                ["edges"] = g.Edges.Count,
            },
            ["report"] = File.Exists(reportPath),
            ["cache"] = File.Exists(cache),
            ["repos"] = repos,
        });
    }

    private RefIndex? _refs;
    private (Graph Graph, DateTime Stamp)? _refsOf;

    /// <summary>The parse cache's references, loaded again with a new graph or cache; null without a cache.</summary>
    private RefIndex? Refs(Graph g)
    {
        string cache = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(graphPath))!,
            CodeGraph.Scan.Scanner.CacheFileName);
        var stamp = File.Exists(cache) ? File.GetLastWriteTimeUtc(cache) : DateTime.MinValue;
        if (_refsOf is not { } of || of.Graph != g || of.Stamp != stamp)
        {
            _refs = stamp == DateTime.MinValue ? null : RefIndex.Load(g, cache);
            _refsOf = (g, stamp);
            if (_refs is not null) _log.WriteLine($"codegraph mcp: loaded references from {cache}");
        }
        return _refs;
    }

    private (string, JsonObject) Search(JsonObject a)
    {
        var g = Current();
        string query = Need(a, "query");
        string? kind = Text(a, "kind"), repo = Text(a, "repo");
        int limit = Num(a, "limit", 25, 1, 500);
        var w = new StringWriter { NewLine = "\n" };
        List<string> hits;
        try { hits = GraphQuery.WriteSearch(g, query, w, kind, repo, limit); }
        catch (CodeGraphException e) { throw new ToolException(e.Message); }
        return (w.ToString(), Structured.Search(g, query, hits, limit));
    }

    /// <summary>The node id a name argument means; an ambiguous or unknown name ends the call with a hint.</summary>
    private string Pick(JsonObject a, string key)
    {
        var g = Current();
        string name = Need(a, key);
        var m = GraphQuery.Resolve(g, name);
        return m.Id ?? throw new ToolException(GraphQuery.Problem(g, name, m) + $"Call again with an id as {key}, or use the search tool.",
            m.Matches.Count > 0 ? m.Matches : m.Near);
    }

    private static string? Text(JsonObject a, string key) =>
        a[key] is JsonValue v && v.TryGetValue(out string? s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static string Need(JsonObject a, string key) =>
        Text(a, key) ?? throw new ToolException($"Missing argument: {key}");

    /// <summary>Notes for the current call's answer (e.g. a capped argument).</summary>
    private readonly List<string> _notes = [];

    /// <summary>An integer argument; below <paramref name="min"/> is an error, above <paramref name="max"/> is capped (and said).</summary>
    private int Num(JsonObject a, string key, int def, int min, int max)
    {
        if (a[key] is null) return def;
        if (a[key] is not JsonValue v || !(v.TryGetValue(out int n) || (v.TryGetValue(out string? s) && int.TryParse(s, out n))))
            throw new ToolException($"{key} must be a whole number");
        if (n < min) throw new ToolException($"{key} must be at least {min} (got {n})");
        if (n > max) _notes.Add($"{key} {n} was capped at {max}.");
        return Math.Min(n, max);
    }

    /// <summary>The relation argument, checked against the relations in the graph.</summary>
    private string? Relation(JsonObject a)
    {
        string? rel = Text(a, "relation");
        if (rel is null) return null;
        var g = Current();
        if (_relations is null || _relationsOf != g)
        {
            _relations = g.Edges.Select(e => e.Relation).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _relationsOf = g;
        }
        return _relations.Contains(rel)
            ? rel
            : throw new ToolException($"Unknown relation '{rel}'. Relations in the graph: " +
                                      string.Join(", ", _relations.Order(StringComparer.Ordinal)));
    }

    private HashSet<string>? _relations;
    private Graph? _relationsOf;

    /// <summary>The report's summary plus a list of sections, or the one section whose heading contains the text.</summary>
    private (string, JsonObject) Report(JsonObject a)
    {
        if (!File.Exists(reportPath)) throw new ToolException($"No report yet at {reportPath}. Run:  codegraph build");
        var lines = File.ReadAllLines(reportPath);
        var heads = Enumerable.Range(0, lines.Length).Where(i => lines[i].StartsWith("## ", StringComparison.Ordinal)).ToList();
        string Section(int k) => string.Join("\n", lines[heads[k]..(k + 1 < heads.Count ? heads[k + 1] : lines.Length)]).TrimEnd() + "\n";
        string Titles() => string.Join("\n", heads.Select(h => "  " + lines[h][3..]));
        string? want = Text(a, "section");
        var titles = Structured.Arr(heads.Select(h => (JsonNode?)lines[h][3..]));
        if (want is null)
        {
            string head = string.Join("\n", lines[..(heads.Count > 0 ? heads[0] : lines.Length)]).TrimEnd() + "\n\n";
            string summary = head + (heads.Count > 0 ? Section(0) : "");
            return (summary + "\nSections (call again with section):\n" + Titles() + "\n",
                new JsonObject { ["text"] = summary, ["sections"] = titles });
        }
        int found = heads.FindIndex(h => lines[h].Contains(want, StringComparison.OrdinalIgnoreCase));
        return found >= 0
            ? (Section(found), new JsonObject { ["section"] = lines[heads[found]][3..], ["text"] = Section(found), ["sections"] = titles })
            : throw new ToolException($"No section matches '{want}'. Sections:\n{Titles()}");
    }

    private (string, JsonObject) Remember(JsonObject a)
    {
        if (knowledge is null) throw new ToolException("remember is not available: this server was started without a config.");
        try
        {
            string done = KnowledgeWriter.Remember(Current(), knowledge, Need(a, "subject"), Text(a, "relation"), Text(a, "object"),
                Text(a, "note"), Need(a, "source"));
            return (done, new JsonObject { ["recorded"] = true, ["message"] = done });
        }
        catch (CodeGraphException ex)
        {
            throw new ToolException(ex.Message);
        }
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
               "with the tool's parameters.";
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
