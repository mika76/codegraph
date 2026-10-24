using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeGraph.Build;
using CodeGraph.Knowledge;
using CodeGraph.Model;
using CodeGraph.Output;
using CodeGraph.Query;
using CodeGraph.Scan;

namespace CodeGraph;

/// <summary>The work behind each CLI command.</summary>
public static class Commands
{
    private static string GraphPath(Config cfg) => System.IO.Path.Combine(cfg.Output, "graph.json");

    public static int Build(Config cfg, bool full, bool noVault)
    {
        var sw = Stopwatch.StartNew();
        if (full) File.Delete(System.IO.Path.Combine(cfg.Output, Scanner.CacheFileName));
        Console.WriteLine($"Scanning {cfg.Repos.Count} repo(s)...");
        var files = Scanner.Discover(cfg);
        var (records, errors) = Scanner.ParseAll(cfg, files);
        Console.WriteLine("Linking...");
        var (g, stats, unresolved) = Builder.BuildGraph(cfg, records);
        var warnings = new List<string>();
        if (cfg.ReleasesFromBranches) KnowledgeLoader.ApplyBranchReleases(g);
        KnowledgeLoader.ApplyReleases(g, cfg.Releases, warnings);
        KnowledgeLoader.ApplyKnowledge(g, cfg.Knowledge, warnings);
        g.Meta["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        g.Meta["repos"] = new JsonArray(cfg.Repos
            .Select(r => (JsonNode?)new JsonObject { ["name"] = r.Name, ["path"] = r.Path }).ToArray());
        Directory.CreateDirectory(cfg.Output);
        g.Save(GraphPath(cfg));

        string reportPath = System.IO.Path.Combine(cfg.Output, "GRAPH_REPORT.md");
        string md = Report.BuildReport(g, stats, unresolved, errors, warnings);
        File.WriteAllText(reportPath, md);
        if (!string.IsNullOrEmpty(cfg.Vault) && !noVault)
        {
            var res = Obsidian.WriteVault(g, cfg, md);
            Console.WriteLine($"Obsidian vault: {cfg.Vault}  {res}");
        }

        var kinds = g.Nodes.GroupBy(n => n.Kind).ToDictionary(x => x.Key, x => x.Count());
        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:0}s: {g.Nodes.Count} nodes, {g.Edges.Count} edges " +
                          $"({kinds.GetValueOrDefault("project")} projects, {kinds.GetValueOrDefault("file")} files, " +
                          $"{kinds.Where(k => k.Key.StartsWith("db_")).Sum(k => k.Value)} DB objects)");
        Console.WriteLine($"Graph: {GraphPath(cfg)}\nReport: {reportPath}");
        foreach (var w in warnings.Take(20)) Console.WriteLine($"  warning: {w}");
        if (errors.Count > 0)
            Console.WriteLine($"  {errors.Count} file(s) could not be parsed - see the report.");
        return 0;
    }

    private static Graph LoadGraph(Config cfg)
    {
        string p = GraphPath(cfg);
        if (!File.Exists(p)) throw new CodeGraphException($"No graph yet at {p}. Run:  codegraph build");
        return Graph.Load(p);
    }

    public static int Show(Config cfg, string name)
    {
        GraphQuery.Show(LoadGraph(cfg), name, Console.Out);
        return 0;
    }

    public static int Impact(Config cfg, string name, int depth)
    {
        GraphQuery.Impact(LoadGraph(cfg), name, Console.Out, depth);
        return 0;
    }

    public static int Path(Config cfg, string a, string b)
    {
        GraphQuery.Path(LoadGraph(cfg), a, b, Console.Out);
        return 0;
    }

    public static int ReleaseSnapshot(Config cfg, string version, string? date)
    {
        if (string.IsNullOrEmpty(cfg.Releases))
            throw new CodeGraphException("Set \"releases\": \"./releases.json\" in the config first.");
        int n = KnowledgeLoader.ReleaseSnapshot(LoadGraph(cfg), version, date, cfg.Releases);
        Console.WriteLine($"Added release {version} with {n} projects to {cfg.Releases}. " +
                          "Edit it (remove modules not shipped, fill notes), then run build again. " +
                          "Note: the file was rewritten, so any comments in it were lost.");
        return 0;
    }
}
