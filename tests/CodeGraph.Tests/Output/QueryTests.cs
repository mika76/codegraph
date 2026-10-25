using CodeGraph.Query;

namespace CodeGraph.Tests.Output;

public class QueryTests
{
    private static string Run(Action<StringWriter> a)
    {
        var w = new StringWriter { NewLine = "\n" };
        a(w);
        return w.ToString();
    }

    [Fact]
    public void Find_prefers_exact_matches()
    {
        var g = SampleGraph.Create();
        Assert.Equal(["db:Orders"], GraphQuery.Find(g, "orders"));
        Assert.Contains("db:Unused", GraphQuery.Find(g, "unus"));
        Assert.Empty(GraphQuery.Find(g, "zzz"));
    }

    [Fact]
    public void Show_lists_outgoing_and_incoming_by_relation()
    {
        var text = Run(w => GraphQuery.Show(SampleGraph.Create(), "Orders", w));
        Assert.Equal(
            "Orders (db_table)\n" +
            "  columns: Id int not null, Status int\n" +
            "\nIncoming:\n" +
            "  writes (2):\n" +
            "     frmMain.Save (sub)  [Sales/frmMain.frm:10]  x2\n" +
            "     usp_Save (db_proc)\n", text);
    }

    [Fact]
    public void Show_project_includes_attributes_and_edges()
    {
        var text = Run(w => GraphQuery.Show(SampleGraph.Create(), "Order Entry", w));
        Assert.StartsWith("Order Entry (project)\n  version: 1.2.3\n  output: OrderEntry.exe\n  ptype: exe\n", text);
        Assert.Contains("\nOutgoing:\n  contains (2):\n     frmMain.frm (file)  [Sales/frmMain.frm]\n", text);
    }

    [Fact]
    public void Show_truncates_long_groups()
    {
        var text = Run(w => GraphQuery.Show(SampleGraph.Create(), "Orders", w, limit: 1));
        Assert.Contains("     ... 1 more", text);
    }

    [Fact]
    public void Nothing_matches()
    {
        Assert.Equal("Nothing matches 'zzz'.\n", Run(w => GraphQuery.Show(SampleGraph.Create(), "zzz", w)));
        Assert.Equal("Nothing matches 'zzz'.\n", Run(w => GraphQuery.Impact(SampleGraph.Create(), "zzz", w)));
        Assert.Equal("Nothing matches 'zzz'.\n", Run(w => GraphQuery.Path(SampleGraph.Create(), "Orders", "zzz", w)));
    }

    [Fact]
    public void Ambiguous_names_are_listed()
    {
        var g = SampleGraph.Create();
        g.Node("db:other.Orders", ("kind", "db_table"), ("name", "Orders"));
        var text = Run(w => GraphQuery.Show(g, "Orders", w));
        Assert.StartsWith("'Orders' matches several things, using the first. Be more specific (use a path or id):\n    db:Orders\n    db:other.Orders\n", text);
    }

    [Fact]
    public void Exact_ids_win_and_ambiguity_is_reported()
    {
        var g = SampleGraph.Create();
        g.Node("db:other.Orders", ("kind", "db_table"), ("name", "Orders"));
        Assert.Equal("db:other.Orders", GraphQuery.Resolve(g, "db:other.Orders", out var none));
        Assert.Empty(none);
        Assert.Equal("db:Orders", GraphQuery.Resolve(g, "Orders", out var amb));
        Assert.Equal(["db:Orders", "db:other.Orders"], amb);
        Assert.Null(GraphQuery.Resolve(g, "zzz", out _));
        // id-shaped: exact id (any case) or nothing - never a substring match on another node
        Assert.Equal("db:Orders", GraphQuery.Resolve(g, "DB:orders", out _));
        Assert.Null(GraphQuery.Resolve(g, "db:Ord", out _));
        Assert.Equal("No node has the id 'db:Ord'.\n", Run(w => GraphQuery.Show(g, "db:Ord", w)));
        Assert.Equal("frmMain.frm (file)  [Sales/frmMain.frm]",
            Run(w => GraphQuery.Show(g, "Sales/frmMain.frm", w)).Split('\n')[0]); // a path with no id prefix still matches
    }

    [Fact]
    public void Search_puts_exact_matches_first_and_filters_kind_and_repo()
    {
        var g = SampleGraph.Create();
        Assert.Equal([SampleGraph.Save], GraphQuery.Search(g, "save", kind: "sub"));
        Assert.Equal("db:usp_Save", GraphQuery.Search(g, "usp_save")[0]);
        Assert.Equal([SampleGraph.TMod, SampleGraph.FDb, SampleGraph.Load],
            GraphQuery.Search(g, "moddb", repo: "shop"));
        Assert.Empty(GraphQuery.Search(g, "orders", repo: "Shop"));
        // a name starting with the text beats one that only contains it
        g.Node("db:BorderStyles", ("kind", "db_table"), ("name", "BorderStyles"));
        g.Node("db:OrdersArchive", ("kind", "db_table"), ("name", "OrdersArchive"));
        Assert.Equal(["db:Orders", "db:OrdersArchive", "db:BorderStyles"], GraphQuery.Search(g, "orders"));
    }

    [Fact]
    public void Show_filters_by_relation_and_direction_and_prints_ids()
    {
        var g = SampleGraph.Create();
        var text = Run(w => GraphQuery.Show(g, "frmMain.Save", w, relation: "writes", direction: "out", ids: true));
        Assert.Equal(
            $"frmMain.Save (sub)  [Sales/frmMain.frm:10]  {{{SampleGraph.Save}}}\n" +
            "  lines: 5\n  signature: Private Sub Save()\n" +
            "\nOutgoing:\n" +
            "  writes (1):\n" +
            "     Orders (db_table)  x2  {db:Orders}\n", text);
        Assert.Contains("No calls connections (in).", Run(w => GraphQuery.Show(g, "frmMain.Save", w, relation: "calls", direction: "in")));
    }

    [Fact]
    public void Source_prints_a_definition_with_context()
    {
        string dir = Directory.CreateTempSubdirectory("cg-src").FullName;
        try
        {
            Directory.CreateDirectory(System.IO.Path.Combine(dir, "Sales"));
            File.WriteAllText(System.IO.Path.Combine(dir, "Sales", "frmMain.frm"),
                string.Join("\r\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\r\n");
            var g = SampleGraph.Create();
            g.Node("repo:Shop", ("path", dir));
            var text = Run(w => GraphQuery.Source(g, SampleGraph.Save, w, context: 1));
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.EndsWith(":9-15", lines[1]);
            Assert.Equal(["     9  line 9", "    10  line 10", "    15  line 15"], [lines[2], lines[3], lines[^1]]);
            var file = Run(w => GraphQuery.Source(g, "Sales/frmMain.frm", w, maxLines: 3));
            Assert.Contains("     3  line 3\n   ... 17 more lines\n", file);
            Assert.Contains("has no source location", Run(w => GraphQuery.Source(g, "usp_Save", w)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Impact_groups_affected_things_by_project()
    {
        var text = Run(w => GraphQuery.Impact(SampleGraph.Create(), "Orders", w));
        Assert.Equal(
            "Impact of changing Orders (db_table) (up to 3 steps): 3 things\n\n" +
            "(database)\n" +
            "   db_proc: usp_Save\n" +
            "Order Entry\n" +
            "   Sales/frmMain.frm: Save\n" +
            "   Sales/modDb.bas: Load\n", text);
    }

    [Fact]
    public void Impact_respects_depth_and_includes_contained_symbols()
    {
        var g = SampleGraph.Create();
        var text = Run(w => GraphQuery.Impact(g, "Orders", w, depth: 1));
        Assert.Contains("2 things", text);
        Assert.DoesNotContain("modDb.bas", text);
        // a file is affected through its symbols; the start set itself is not reported
        var file = Run(w => GraphQuery.Impact(g, "Sales/modDb.bas", w));
        Assert.Contains("Sales/frmMain.frm: Save", file);
        Assert.DoesNotContain("\n   Sales/modDb.bas", file);
    }

    [Fact]
    public void Path_prints_the_shortest_chain()
    {
        var text = Run(w => GraphQuery.Path(SampleGraph.Create(), "frmMain.Save", "usp_Save", w));
        Assert.Equal(
            "frmMain.Save (sub)  [Sales/frmMain.frm:10]\n" +
            "  --calls--> modDb.Load (function)  [Sales/modDb.bas:3]\n" +
            "  --executes--> usp_Save (db_proc)\n", text);
    }

    [Fact]
    public void Path_follows_edges_backwards_and_reports_no_connection()
    {
        var g = SampleGraph.Create();
        var back = Run(w => GraphQuery.Path(g, "usp_Save", "modDb.Load", w));
        Assert.Contains("<--executes-- modDb.Load (function)", back);
        Assert.Equal("No connection found within 8 steps.\n", Run(w => GraphQuery.Path(g, "Unused", "frmMain.Save", w)));
    }

    /// <summary>a.ts -> m1 -> m2 -> b.ts one way; a.ts and b.ts also both import lib (a shorter "valley").</summary>
    private static Model.Graph PathGraph()
    {
        var g = new Model.Graph();
        foreach (var n in new[] { "a", "m1", "m2", "b", "lib", "c" })
            g.Node($"file:R:{n}.ts", ("kind", "file"), ("name", $"{n}.ts"), ("path", $"{n}.ts"));
        g.Node("repo:R", ("kind", "repo"), ("name", "R"));
        g.Node("rel:1.0", ("kind", "release"), ("name", "1.0"));
        g.Node("proj:R:p", ("kind", "project"), ("name", "p"));
        g.AddEdge("file:R:a.ts", "file:R:m1.ts", "imports");
        g.AddEdge("file:R:m1.ts", "file:R:m2.ts", "imports");
        g.AddEdge("file:R:m2.ts", "file:R:b.ts", "imports");
        g.AddEdge("file:R:a.ts", "file:R:lib.ts", "imports");
        g.AddEdge("file:R:b.ts", "file:R:lib.ts", "imports");
        g.AddEdge("file:R:c.ts", "file:R:lib.ts", "imports");
        g.AddEdge("repo:R", "file:R:a.ts", "contains");
        g.AddEdge("repo:R", "file:R:c.ts", "contains");
        g.AddEdge("proj:R:p", "rel:1.0", "released_in");
        g.AddEdge("proj:R:p", "file:R:lib.ts", "uses_library");
        return g;
    }

    [Fact]
    public void Path_prefers_one_way_chains_over_shorter_valleys()
    {
        var g = PathGraph();
        Assert.Equal(
            "a.ts (file)  [a.ts]\n  --imports--> m1.ts (file)  [m1.ts]\n  --imports--> m2.ts (file)  [m2.ts]\n" +
            "  --imports--> b.ts (file)  [b.ts]\n", Run(w => GraphQuery.Path(g, "a.ts", "b.ts", w)));
        Assert.StartsWith("b.ts (file)  [b.ts]\n  <--imports-- m2.ts", Run(w => GraphQuery.Path(g, "b.ts", "a.ts", w)));
    }

    [Fact]
    public void Path_marks_mixed_chains_and_skips_repo_membership()
    {
        var g = PathGraph();
        var text = Run(w => GraphQuery.Path(g, "c.ts", "a.ts", w, ids: true));
        Assert.Equal(
            "c.ts (file)  [c.ts]  {file:R:c.ts}\n" +
            "  --imports--> lib.ts (file)  [lib.ts]  {file:R:lib.ts}\n" +
            "  <--imports-- a.ts (file)  [a.ts]  {file:R:a.ts}\n" +
            "(No one-way chain: this path changes direction, e.g. both ends use the same thing or are used by the " +
            "same thing, so it may not be a real dependency.)\n", text);
        // released_in and uses_library are followed too
        Assert.Contains("<--released_in-- p (project)\n  --uses_library--> lib.ts", Run(w => GraphQuery.Path(g, "1.0", "lib.ts", w)));
    }

    [Fact]
    public void Path_respects_max_length()
    {
        var g = PathGraph();
        Assert.Equal("No connection found within 2 steps.\n", Run(w => GraphQuery.Path(g, "m1.ts", "c.ts", w, maxLen: 2)));
        Assert.Contains("--imports--> lib.ts", Run(w => GraphQuery.Path(g, "m1.ts", "c.ts", w, maxLen: 4)));
    }

    [Fact]
    public void Impact_groups_items_by_kind_and_lists_shared_files_once()
    {
        var g = SampleGraph.Create();
        foreach (var p in new[] { "P2", "P3" })
        {
            g.Node($"proj:Shop:{p}", ("kind", "project"), ("name", p));
            ((System.Text.Json.Nodes.JsonArray)g.GetNode(SampleGraph.FDb)!["projects"]!).Add((System.Text.Json.Nodes.JsonNode?)$"proj:Shop:{p}");
        }
        g.Node("item:report:sales", ("kind", "report"), ("name", "Sales Summary"), ("file", SampleGraph.FMain));
        g.AddEdge("item:report:sales", "db:Orders", "reads");
        var text = Run(w => GraphQuery.Impact(g, "Orders", w, depth: 2));
        Assert.Contains("(up to 2 steps): 4 things", text);
        Assert.Contains("(other)\n   report: Sales Summary\n", text);
        Assert.Contains("(shared files)\n   Sales/modDb.bas  [in 3 projects: Order Entry, P2, P3]: Load\n", text);
        Assert.DoesNotContain("Order Entry\n   Sales/modDb.bas", text);
        Assert.Contains("(up to 1 step):", Run(w => GraphQuery.Impact(g, "Orders", w, depth: 1)));
    }
}
