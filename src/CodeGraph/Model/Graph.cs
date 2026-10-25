using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeGraph.Model;

/// <summary>
/// A graph node: id, kind, name plus free-form attributes (repo, path, line, version, ...), kept as a JSON object
/// so graph.json can carry whatever the builder and knowledge files attach.
/// </summary>
public sealed class Node(JsonObject data)
{
    public JsonObject Data { get; } = data;

    public string Id => Str("id")!;
    public string Kind => Str("kind") ?? "";
    public string Name => Str("name") ?? "";

    public JsonNode? this[string key]
    {
        get => Data[key];
        set => Data[key] = value;
    }

    public bool Has(string key) => Data[key] is not null;

    public string? Str(string key) => Data[key] is JsonValue v && v.TryGetValue(out string? s) ? s : Data[key]?.ToString();

    public int? Int(string key) => Data[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    public bool Bool(string key) => Data[key] is JsonValue v && v.TryGetValue(out bool b) && b;

    public override string ToString() => Id;
}

/// <summary>A directed edge; (source, target, relation) is unique and repeats add to count.</summary>
public sealed class Edge(JsonObject data)
{
    public JsonObject Data { get; } = data;

    public string Source => (string)Data["source"]!;
    public string Target => (string)Data["target"]!;
    public string Relation => (string)Data["relation"]!;
    public int Count
    {
        get => (int)Data["count"]!;
        set => Data["count"] = value;
    }

    public JsonNode? this[string key]
    {
        get => Data[key];
        set => Data[key] = value;
    }

    public string? Str(string key) => Data[key] is JsonValue v && v.TryGetValue(out string? s) ? s : Data[key]?.ToString();

    public int? Int(string key) => Data[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    public override string ToString() => $"{Source} -{Relation}-> {Target}";
}

/// <summary>In-memory graph with JSON (de)serialisation. Insertion order of nodes and edges is kept.</summary>
public sealed class Graph
{
    private readonly Dictionary<string, Node> _nodes = [];
    private readonly List<Node> _nodeOrder = [];
    private readonly Dictionary<(string, string, string), Edge> _edges = [];
    private readonly List<Edge> _edgeOrder = [];

    public JsonObject Meta { get; set; } = [];

    public IReadOnlyList<Node> Nodes => _nodeOrder;
    public IReadOnlyList<Edge> Edges => _edgeOrder;

    public Node? GetNode(string id) => _nodes.GetValueOrDefault(id);

    public bool HasNode(string id) => _nodes.ContainsKey(id);

    /// <summary>Get or create a node and set the given attributes (null values are skipped).</summary>
    public Node Node(string id, params (string Key, JsonNode? Value)[] attrs)
    {
        if (!_nodes.TryGetValue(id, out var n))
        {
            n = new Node(new JsonObject { ["id"] = id });
            _nodes[id] = n;
            _nodeOrder.Add(n);
        }
        foreach (var (k, v) in attrs)
            if (v is not null)
                n.Data[k] = v.Parent is null ? v : v.DeepClone();
        return n;
    }

    /// <summary>
    /// Add an edge between existing nodes (self-loops and unknown ends are ignored). An existing edge gets its
    /// count increased; attributes are only set when not already there.
    /// </summary>
    public Edge? AddEdge(string source, string target, string relation, int count = 1, params (string Key, JsonNode? Value)[] attrs)
    {
        if (source == target || !_nodes.ContainsKey(source) || !_nodes.ContainsKey(target)) return null;
        var key = (source, target, relation);
        if (!_edges.TryGetValue(key, out var e))
        {
            e = new Edge(new JsonObject { ["source"] = source, ["target"] = target, ["relation"] = relation, ["count"] = 0 });
            _edges[key] = e;
            _edgeOrder.Add(e);
        }
        e.Count += count;
        foreach (var (k, v) in attrs)
            if (v is not null && e.Data[k] is null)
                e.Data[k] = v.Parent is null ? v : v.DeepClone();
        return e;
    }

    public Edge? GetEdge(string source, string target, string relation) => _edges.GetValueOrDefault((source, target, relation));

    public bool RemoveNode(string id)
    {
        if (!_nodes.Remove(id, out var n)) return false;
        _nodeOrder.Remove(n);
        foreach (var e in _edgeOrder.Where(e => e.Source == id || e.Target == id).ToList())
            RemoveEdge(e);
        return true;
    }

    public void RemoveEdge(Edge e)
    {
        if (_edges.Remove((e.Source, e.Target, e.Relation)))
            _edgeOrder.Remove(e);
    }

    public Dictionary<string, List<Edge>> OutEdges()
    {
        var idx = new Dictionary<string, List<Edge>>();
        foreach (var e in _edgeOrder)
            (idx.TryGetValue(e.Source, out var l) ? l : idx[e.Source] = []).Add(e);
        return idx;
    }

    public Dictionary<string, List<Edge>> InEdges()
    {
        var idx = new Dictionary<string, List<Edge>>();
        foreach (var e in _edgeOrder)
            (idx.TryGetValue(e.Target, out var l) ? l : idx[e.Target] = []).Add(e);
        return idx;
    }

    /// <summary>
    /// Write graph.json to a temporary file and swap it in, so a reader (e.g. a running MCP server) never sees a
    /// half-written graph. A target locked by another program is retried briefly, then reported.
    /// </summary>
    public void Save(string path)
    {
        string tmp = path + ".tmp", old = path + ".old";
        WriteTo(tmp);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // Windows can't overwrite a file that is open, even for a reader that allows it, but it can rename it
                if (File.Exists(path)) File.Move(path, old, overwrite: true);
                File.Move(tmp, path);
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10)
                {
                    File.Delete(tmp);
                    throw new CodeGraphException($"Could not replace {path}: {ex.Message} Close the program that has it open and build again.");
                }
                Thread.Sleep(200 * attempt);
            }
        }
    }

    private void WriteTo(string path)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WritePropertyName("meta");
        Meta.WriteTo(w);
        w.WriteStartArray("nodes");
        foreach (var n in _nodeOrder) n.Data.WriteTo(w);
        w.WriteEndArray();
        w.WriteStartArray("edges");
        foreach (var e in _edgeOrder) e.Data.WriteTo(w);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static Graph Load(string path)
    {
        // let a build swap in a new graph.json while this one is being read
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var root = JsonNode.Parse(fs)!.AsObject();
        var g = new Graph { Meta = root["meta"]?.AsObject() is { } m ? (JsonObject)m.DeepClone() : [] };
        foreach (var item in root["nodes"]!.AsArray())
        {
            var n = new Node((JsonObject)item!.DeepClone());
            g._nodes[n.Id] = n;
            g._nodeOrder.Add(n);
        }
        foreach (var item in root["edges"]!.AsArray())
        {
            var e = new Edge((JsonObject)item!.DeepClone());
            g._edges[(e.Source, e.Target, e.Relation)] = e;
            g._edgeOrder.Add(e);
        }
        return g;
    }
}
