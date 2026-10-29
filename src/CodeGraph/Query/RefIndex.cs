using System.Text.Json.Nodes;
using CodeGraph.Build;
using CodeGraph.Model;
using CodeGraph.Scan;
using CodeGraph.Text;

namespace CodeGraph.Query;

/// <summary>
/// The name-like references the parsers found (strings, SQL names, column names), from the parse cache, by lower-case
/// short name. The graph keeps only references that resolved; these are what <see cref="DbUsage"/> reports as
/// possible uses: a string naming a table that is not next to database access, a column of a statement with several
/// tables, a column read by name from a recordset or row.
/// </summary>
public sealed class RefIndex
{
    /// <summary>One reference: the file's node id, the routine and type it is in, and what the parser saw.</summary>
    public sealed record Entry(string FileId, string? From, string? FromParent, string Kind, string Name, string? Mode,
        int Line, string? Table, string[] Tables, bool ColumnRead, string? Item);

    private readonly Dictionary<string, List<Entry>> _byName = [];

    public IReadOnlyList<Entry> Named(string lowerShortName) => _byName.GetValueOrDefault(lowerShortName) ?? [];

    /// <summary>The cache's references of the files in the graph; null when there is no cache for this parser version.</summary>
    public static RefIndex? Load(Graph g, string cachePath)
    {
        var cache = Scanner.LoadCache(cachePath);
        if (cache.Count == 0) return null;
        // absolute path of each file node -> its id
        var fileIds = new Dictionary<string, string>();
        foreach (var n in g.Nodes)
            if (n.Kind == "file" && g.GetNode("repo:" + n.Str("repo"))?.Str("path") is { Length: > 0 } root && n.Str("path") is { } rel)
                fileIds[Paths.Key(Paths.Join(root, rel))] = n.Id;
        var index = new RefIndex();
        foreach (var (abs, entry) in cache)
        {
            if (!fileIds.TryGetValue(Paths.Key(abs), out var fid)) continue;
            foreach (var rf in entry.Facts.Refs)
            {
                if (rf.Kind is not ("literal" or "sql" or "column") || string.IsNullOrEmpty(rf.Name)) continue;
                string key = (rf.Kind == "column" ? rf.Name : SqlText.ShortName(rf.Name)).Trim('[', ']').ToLowerInvariant();
                var tables = (rf.Extra?["tables"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToArray() ?? [];
                var e = new Entry(fid, rf.From, rf.FromParent, rf.Kind, rf.Name, rf.Mode, rf.Line,
                    rf.Extra?["table"]?.ToString(), tables, rf.Extra?["column"] is not null, rf.Extra?["item"]?.ToString());
                (index._byName.TryGetValue(key, out var l) ? l : index._byName[key] = []).Add(e);
            }
        }
        return index;
    }
}
