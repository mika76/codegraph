using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CodeGraph.Model;
using CodeGraph.Scan;
using CodeGraph.Text;

namespace CodeGraph.Query;

/// <summary>
/// Plain text search for a name across the configured repositories - the files a build reads, with the same ignore
/// rules - for what the graph cannot link: SQL built at runtime, names in config files, late binding. Every hit is a
/// text match, nothing more; <c>linked</c> says the graph already links the routine it is in to something of that name.
/// </summary>
public static class TextSearch
{
    public sealed record Hit(string Repo, string Rel, int Line, int Column, string Text);

    public sealed record Result(string Query, List<Hit> Hits, int Files, int FilesWithHits);

    public static Result Find(Config cfg, string text, string? repo = null, bool wholeWord = true)
    {
        var files = Scanner.Discover(cfg)
            .Where(f => repo is null || string.Equals(f.Repo, repo, StringComparison.OrdinalIgnoreCase)).ToList();
        var hits = new ConcurrentBag<Hit>();
        Parallel.ForEach(files, f =>
        {
            string content;
            try { content = SourceText.Read(f.Abs); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            if (content.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) return;
            var lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                for (int at = line.IndexOf(text, StringComparison.OrdinalIgnoreCase); at >= 0;
                     at = line.IndexOf(text, at + 1, StringComparison.OrdinalIgnoreCase))
                {
                    if (wholeWord && (Word(line, at - 1) || Word(line, at + text.Length))) continue;
                    string shown = line.TrimEnd('\r').Trim();
                    hits.Add(new Hit(f.Repo, f.Rel, i + 1, at + 1, shown.Length > 300 ? shown[..300] + " ..." : shown));
                    break; // one hit per line
                }
            }
        });
        var sorted = hits.OrderBy(h => h.Repo, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Rel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Line).ToList();
        return new Result(text, sorted, files.Count, sorted.Select(h => (h.Repo, h.Rel)).Distinct().Count());
    }

    private static bool Word(string s, int i) => i >= 0 && i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_');

    /// <summary>The innermost graph symbol around each hit, and whether it already links to a node of that name.</summary>
    private sealed class Places(Graph g, string query)
    {
        private readonly Dictionary<string, List<(int From, int To, string Id)>> _syms = Index(g);
        private readonly HashSet<string> _linkers = Linkers(g, query);

        private static Dictionary<string, List<(int, int, string)>> Index(Graph g)
        {
            var d = new Dictionary<string, List<(int, int, string)>>();
            foreach (var n in g.Nodes)
                if (n.Str("file") is { } f && n.Int("line") is { } line && n.Id.StartsWith("sym:", StringComparison.Ordinal))
                    (d.TryGetValue(f, out var l) ? l : d[f] = []).Add((line, line + Math.Max(1, n.Int("lines") ?? 1) - 1, n.Id));
            return d;
        }

        /// <summary>Nodes with an edge to something named like the query (a table, a procedure, a column).</summary>
        private static HashSet<string> Linkers(Graph g, string query)
        {
            string low = query.ToLowerInvariant();
            var named = g.Nodes.Where(n => n.Kind != "file" && (SqlText.ShortName(n.Name).Equals(low, StringComparison.OrdinalIgnoreCase)
                                                               || n.Name.Equals(low, StringComparison.OrdinalIgnoreCase)))
                .Select(n => n.Id).ToHashSet();
            return g.Edges.Where(e => named.Contains(e.Target) && e.Relation != "contains").Select(e => e.Source).ToHashSet();
        }

        public string? Symbol(string fileId, int line) =>
            _syms.GetValueOrDefault(fileId)?.Where(s => s.From <= line && line <= s.To)
                .OrderByDescending(s => s.From).ThenBy(s => s.To).Select(s => s.Id).FirstOrDefault();

        public bool Linked(string fileId, string? sym) =>
            _linkers.Contains(fileId) || (sym is not null && _linkers.Contains(sym));
    }

    public static JsonObject ToJson(Graph? g, Result res, int limit)
    {
        var places = g is null ? null : new Places(g, res.Query);
        var items = new JsonArray();
        foreach (var h in res.Hits.Take(limit))
        {
            string fid = $"file:{h.Repo}:{h.Rel}";
            var o = new JsonObject
            {
                ["repo"] = h.Repo, ["file"] = h.Rel, ["file_id"] = fid, ["line"] = h.Line, ["column"] = h.Column,
                ["text"] = h.Text, ["confidence"] = "text",
            };
            if (places is not null)
            {
                string? sym = places.Symbol(fid, h.Line);
                if (sym is not null) o["node"] = Structured.NodeJson(g!, sym);
                o["linked"] = places.Linked(fid, sym);
            }
            items.Add((JsonNode?)o);
        }
        return new JsonObject
        {
            ["query"] = res.Query,
            ["total"] = res.Hits.Count,
            ["files_searched"] = res.Files,
            ["files_with_hits"] = res.FilesWithHits,
            ["hits"] = items,
            ["truncated"] = res.Hits.Count > limit,
        };
    }

    public static void Write(Graph? g, Result res, TextWriter w, int limit)
    {
        var places = g is null ? null : new Places(g, res.Query);
        w.WriteLine($"{res.Hits.Count} text match{(res.Hits.Count == 1 ? "" : "es")} for '{res.Query}' in {res.FilesWithHits} " +
                    $"file{(res.FilesWithHits == 1 ? "" : "s")} ({res.Files:N0} searched). Text matches only: check each one." +
                    (places is null ? "" : " [linked] = the graph already links that routine to something of that name."));
        foreach (var h in res.Hits.Take(limit))
        {
            string fid = $"file:{h.Repo}:{h.Rel}";
            string? sym = places?.Symbol(fid, h.Line);
            string inside = sym is not null ? $"  (in {g!.GetNode(sym)?.Str("qual") ?? g.GetNode(sym)?.Name})" : "";
            string linked = places?.Linked(fid, sym) == true ? "  [linked]" : "";
            w.WriteLine($"  {h.Repo}/{h.Rel}:{h.Line}{inside}{linked}  {h.Text}");
        }
        if (res.Hits.Count > limit) w.WriteLine($"  ... {res.Hits.Count - limit} more (narrow with repo, or raise limit)");
    }
}
