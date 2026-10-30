using System.Text.Json.Nodes;
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
    public void Names_match_exactly_ignoring_case_and_never_partially()
    {
        var g = SampleGraph.Create();
        Assert.Equal("db:Orders", GraphQuery.Resolve(g, "orders").Id);                       // name
        Assert.Equal(SampleGraph.Save, GraphQuery.Resolve(g, "FRMMAIN.SAVE").Id);           // qualified name
        Assert.Equal(SampleGraph.FDb, GraphQuery.Resolve(g, "sales/moddb.bas").Id);         // path
        Assert.Equal("db:Orders", GraphQuery.Resolve(g, "DB:orders").Id);                   // id
        var partial = GraphQuery.Resolve(g, "unus");
        Assert.Null(partial.Id);
        Assert.Equal(["db:Unused"], partial.Near);                                          // offered, not picked
        Assert.Empty(GraphQuery.Resolve(g, "zzz").Near);
        // things whose name contains the text are offered; one that only has it in its id (a symbol of that file) is not
        g.Node("sym:Shop:Sales/frmMain.frm#frmMain.Helper", ("kind", "sub"), ("name", "Helper"), ("qual", "Helper"));
        var near = GraphQuery.Resolve(g, "frmMai").Near;
        Assert.Contains(SampleGraph.TForm, near);
        Assert.Contains(SampleGraph.FMain, near);
        Assert.DoesNotContain("sym:Shop:Sales/frmMain.frm#frmMain.Helper", near);
    }

    [Fact]
    public void A_wildcard_matches_a_pattern()
    {
        var g = SampleGraph.Create();
        Assert.Equal("db:Unused", GraphQuery.Resolve(g, "unus*").Id);
        Assert.Equal(SampleGraph.Load, GraphQuery.Resolve(g, "sym:Shop:*#modDb.L*").Id);
        var several = GraphQuery.Resolve(g, "*save*");
        Assert.Null(several.Id);
        Assert.Equal(["db:usp_Save", SampleGraph.Save], several.Matches.Order(StringComparer.Ordinal));
        Assert.Null(GraphQuery.Resolve(g, "zz*").Id);
        // '?' is an ordinary character (URLs), not a wildcard
        Assert.Null(GraphQuery.Resolve(g, "Order?").Id);
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
        Assert.Equal("Nothing is called 'zzz'.\n", Run(w => GraphQuery.Show(SampleGraph.Create(), "zzz", w)));
        Assert.Equal("Nothing is called 'zzz'.\n", Run(w => GraphQuery.Impact(SampleGraph.Create(), "zzz", w)));
        Assert.Equal("Nothing is called 'zzz'.\n", Run(w => GraphQuery.Path(SampleGraph.Create(), "Orders", "zzz", w)));
        // a partial name answers nothing but offers the candidates with their ids
        Assert.Equal("Nothing is called 'unus'.\nClosest (use an id, or * as a wildcard):\n  Unused (db_table)  {db:Unused}\n",
            Run(w => GraphQuery.Show(SampleGraph.Create(), "unus", w)));
        Assert.StartsWith("No node has the id 'db:Ord'.\nClosest (use an id, or * as a wildcard):\n  Orders (db_table)  {db:Orders}\n",
            Run(w => GraphQuery.Show(SampleGraph.Create(), "db:Ord", w)));
    }

    [Fact]
    public void Ambiguous_names_answer_nothing_and_list_the_ids()
    {
        var g = SampleGraph.Create();
        g.Node("db:other.Orders", ("kind", "db_table"), ("name", "Orders"));
        Assert.Equal("'Orders' matches 2 things. Use one of these ids (or a narrower pattern):\n" +
                     "  Orders (db_table)  {db:Orders}\n  Orders (db_table)  {db:other.Orders}\n", Run(w => GraphQuery.Show(g, "Orders", w)));
        Assert.Equal("db:other.Orders", GraphQuery.Resolve(g, "db:other.Orders").Id);
        // same name, different weight: the table beats a routine called Orders (the answer's label says which)
        var h = SampleGraph.Create();
        h.Node("sym:Shop:Sales/modDb.bas#modDb.Orders", ("kind", "function"), ("name", "Orders"), ("qual", "modDb.Orders"));
        Assert.Equal("db:Orders", GraphQuery.Resolve(h, "Orders").Id);
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
    public void Search_matches_typed_words_whatever_separates_them_in_names()
    {
        var g = SampleGraph.Create();
        g.Node("db:Order_Line", ("kind", "db_table"), ("name", "Order_Line"));
        g.Node("sym:Shop:web/orderLineService.ts#orderLineService", ("kind", "module"), ("name", "orderLineService"));
        g.Node("file:Shop:web/order-lines.vue", ("kind", "file"), ("name", "order-lines.vue"), ("path", "web/order-lines.vue"), ("repo", "Shop"));
        g.Node("db:LineOrder", ("kind", "db_table"), ("name", "LineOrder"));
        // the whole run of words first (exactly, then inside a name), then every word anywhere in the name
        var hits = GraphQuery.Search(g, "order line");
        Assert.Equal("db:Order_Line", hits[0]);
        Assert.Equal(["file:Shop:web/order-lines.vue", "sym:Shop:web/orderLineService.ts#orderLineService"], hits[1..3]);
        Assert.Equal("db:LineOrder", hits[^1]);
        // a plural word finds the singular; one word without separators finds the underscored name
        Assert.Contains("db:Order_Line", GraphQuery.Search(g, "order lines"));
        Assert.Equal("db:Order_Line", GraphQuery.Search(g, "orderline")[0]);
        // the text as typed still wins: no word matching when it already matches
        Assert.Equal(["db:Orders"], GraphQuery.Search(g, "orders", kind: "db_table"));
    }

    [Fact]
    public void Routes_show_name_and_meta_and_are_found_by_them()
    {
        var g = SampleGraph.Create();
        var meta = new JsonObject { ["requiresAuth"] = "true", ["requiresAccess"] = "Mod_Orders" };
        g.Node("route:Shop:/orders/:id", ("kind", "route"), ("name", "/orders/:id"), ("route_name", "orderDetail"), ("meta", meta));
        Assert.Contains("  route_name: orderDetail\n  meta: requiresAuth=true, requiresAccess=Mod_Orders\n",
            Run(w => GraphQuery.Show(g, "route:Shop:/orders/:id", w)));
        Assert.Equal("route:Shop:/orders/:id", GraphQuery.Search(g, "orderdetail")[0]);   // by route name
        Assert.Equal(["route:Shop:/orders/:id"], GraphQuery.Search(g, "Mod_Orders"));      // by permission in meta
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
    public void Impact_follows_knowledge_facts_but_not_other_unknown_relations()
    {
        var g = SampleGraph.Create();
        g.Node("db:Audit", ("kind", "db_proc"), ("name", "Audit"));
        g.AddEdge(SampleGraph.Save, "db:Audit", "mentions_in_docs");
        Assert.Contains("0 things", Run(w => GraphQuery.Impact(g, "Audit", w)));
        // a fact fills a gap the code does not show: the subject depends on the object
        g.AddEdge(SampleGraph.Load, "db:Audit", "executes_at_runtime", attrs: [("custom", JsonValue.Create(true))]);
        var text = Run(w => GraphQuery.Impact(g, "Audit", w));
        Assert.Contains("Sales/modDb.bas: Load", text);
        Assert.Contains("Sales/frmMain.frm: Save", text);
    }

    [Fact]
    public void Db_usage_folds_many_entry_points_per_project()
    {
        var g = SampleGraph.Create();
        g.Node("proj:Shop:Till/Till.vbp", ("kind", "project"), ("name", "Till"), ("repo", "Shop"));
        for (int i = 0; i < 25; i++)
        {
            string proj = i < 15 ? SampleGraph.Proj : "proj:Shop:Till/Till.vbp";
            string file = $"file:Shop:F/frm{i}.frm", form = $"sym:Shop:F/frm{i}.frm#frm{i}";
            g.Node(file, ("kind", "file"), ("name", $"frm{i}.frm"), ("path", $"F/frm{i}.frm"), ("repo", "Shop"), ("projects", new JsonArray(proj)));
            g.Node(form, ("kind", "form"), ("name", $"frm{i}"), ("file", file), ("line", 1));
            g.AddEdge(form, "db:Orders", "reads");
        }
        var res = CodeGraph.Query.DbUsage.Find(g, "db:Orders", 3, null);
        Assert.True(res.Entries.Count > CodeGraph.Query.DbUsage.FoldEntries);
        var text = Run(w => CodeGraph.Query.DbUsage.Write(g, res, w));
        Assert.Contains("in 2 projects (folded; the JSON result lists each)", text);
        Assert.Contains("\n    Order Entry (16): ", text);  // 15 forms and frmMain, which writes it
        Assert.Contains("\n    Till (10): frm15 (form), frm16 (form), frm17 (form), frm18 (form) (+6)\n", text);
        var json = CodeGraph.Query.DbUsage.ToJson(g, res);
        Assert.Equal("Order Entry", (string)json["entry_points_by_project"]![0]!["project"]!);
        Assert.Equal(10, (int)json["entry_points_by_project"]![1]!["count"]!);
        Assert.Equal(res.Entries.Count, json["entry_points"]!.AsArray().Count);  // the JSON keeps each one
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
    public void Impact_folds_folders_with_many_files()
    {
        var g = SampleGraph.Create();
        // 7 patch scripts in small folders of their own: one line for the folder above them
        for (int i = 1; i <= 7; i++)
        {
            string rel = $"Database/Patches {i % 3}/Patch{i:D2}.sql";
            g.Node("file:Shop:" + rel, ("kind", "file"), ("name", $"Patch{i:D2}.sql"), ("path", rel), ("area", "Database"));
            g.AddEdge("file:Shop:" + rel, "db:usp_Save", "alters");
        }
        var text = Run(w => GraphQuery.Impact(g, "usp_Save", w, depth: 1));
        Assert.Contains("   Database/ (7 files in 3 folders): ", text);
        Assert.Equal(1, text.Split('\n').Count(l => l.Contains("Patch")));
        // code files keep their own line with the routines
        Assert.Contains("Sales/modDb.bas: Load", text);
        Assert.Contains("(up to 1 step): 8 things", text);
        Assert.Contains("(up to 1 step): 1 thing\n", Run(w => GraphQuery.Impact(SampleGraph.Create(), "modDb.Load", w, depth: 1)));
    }

    [Fact]
    public void A_chain_never_goes_down_contains_after_a_call()
    {
        var g = new Model.Graph();
        foreach (var id in new[] { "sym:W:v.vue#View", "sym:W:v.vue#View.refresh", "sym:W:s.ts#useSvc", "sym:W:s.ts#useSvc.load",
                     "sym:W:s.ts#useSvc.remove", "db:orders" })
            g.Node(id, ("kind", "function"), ("name", id.Split('#')[^1].Split(':')[^1]), ("qual", id.Split('#')[^1]));
        g.AddEdge("sym:W:v.vue#View", "sym:W:v.vue#View.refresh", "contains");
        g.AddEdge("sym:W:v.vue#View.refresh", "sym:W:s.ts#useSvc", "calls");      // calls the factory ...
        g.AddEdge("sym:W:v.vue#View.refresh", "sym:W:s.ts#useSvc.load", "calls"); // ... and uses load
        g.AddEdge("sym:W:s.ts#useSvc", "sym:W:s.ts#useSvc.load", "contains");
        g.AddEdge("sym:W:s.ts#useSvc", "sym:W:s.ts#useSvc.remove", "contains");
        g.AddEdge("sym:W:s.ts#useSvc.remove", "db:orders", "writes");
        // remove writes orders, but the view never calls remove: no chain at all, not even a "mixed" one
        Assert.Equal("No connection found within 8 steps.\n", Run(w => GraphQuery.Path(g, "View", "db:orders", w)));
        // the start's own members first, and from a used member up to its container last, are fine
        g.Node("file:W:s.ts", ("kind", "file"), ("name", "s.ts"), ("path", "s.ts"));
        g.AddEdge("file:W:s.ts", "sym:W:s.ts#useSvc", "contains");
        Assert.Equal("View (function)\n  --contains--> View.refresh (function)\n  --calls--> useSvc (function)\n" +
                     "  <--contains-- s.ts (file)  [s.ts]\n", Run(w => GraphQuery.Path(g, "View", "s.ts", w)));
        // and read the other way round
        Assert.StartsWith("s.ts (file)  [s.ts]\n  --contains--> useSvc (function)\n  <--calls-- View.refresh (function)\n",
            Run(w => GraphQuery.Path(g, "s.ts", "View", w)));
    }

    [Fact]
    public void Opening_a_form_runs_its_load_event()
    {
        var g = new Model.Graph();
        void Sym(string id, string kind) => g.Node(id, ("kind", kind), ("name", id.Split('.')[^1].Split('#')[^1]), ("qual", id.Split('#')[^1]));
        Sym("sym:A:frmMain.frm#frmMain.cmdGo_Click", "event_handler");
        Sym("sym:A:frmGo.frm#frmGo", "form");
        Sym("sym:A:frmGo.frm#frmGo.Form_Load", "event_handler");
        Sym("sym:A:frmGo.frm#frmGo.Fill", "sub");
        Sym("sym:A:frmGo.frm#frmGo.cmdDelete_Click", "event_handler");
        g.Node("db:orders", ("kind", "db_table"), ("name", "Orders"));
        g.Node("db:audit", ("kind", "db_table"), ("name", "Audit"));
        g.AddEdge("sym:A:frmMain.frm#frmMain.cmdGo_Click", "sym:A:frmGo.frm#frmGo", "opens");
        g.AddEdge("sym:A:frmGo.frm#frmGo", "sym:A:frmGo.frm#frmGo.Form_Load", "contains");
        g.AddEdge("sym:A:frmGo.frm#frmGo", "sym:A:frmGo.frm#frmGo.Fill", "contains");
        g.AddEdge("sym:A:frmGo.frm#frmGo", "sym:A:frmGo.frm#frmGo.cmdDelete_Click", "contains");
        g.AddEdge("sym:A:frmGo.frm#frmGo.Form_Load", "sym:A:frmGo.frm#frmGo.Fill", "calls");
        g.AddEdge("sym:A:frmGo.frm#frmGo.Fill", "db:orders", "reads");
        g.AddEdge("sym:A:frmGo.frm#frmGo.cmdDelete_Click", "db:audit", "writes");
        // opening the form runs Form_Load, which reads Orders
        var steps = Run(w => GraphQuery.Path(g, "frmMain.cmdGo_Click", "Orders", w)).Split('\n')
            .Where(l => l.StartsWith("  ", StringComparison.Ordinal)).Select(l => l.Trim().Split(' ')[0]).ToList();
        Assert.Equal(["--opens-->", "--contains-->", "--calls-->", "--reads-->"], steps);
        // a button on the opened form is not run by opening it
        Assert.DoesNotContain("--opens-->", Run(w => GraphQuery.Path(g, "frmMain.cmdGo_Click", "Audit", w)));
        // and impact on Orders reaches the button that opens the form
        Assert.Contains("cmdGo_Click", Run(w => GraphQuery.Impact(g, "Orders", w, depth: 5)));
        Assert.DoesNotContain("cmdGo_Click", Run(w => GraphQuery.Impact(g, "Audit", w, depth: 5)));
    }

    [Fact]
    public void Mixed_paths_do_not_go_through_releases_or_projects()
    {
        var g = PathGraph();
        g.Node("file:R:x.ts", ("kind", "file"), ("name", "x.ts"), ("path", "x.ts"));
        g.Node("proj:R:q", ("kind", "project"), ("name", "q"));
        g.AddEdge("proj:R:q", "rel:1.0", "released_in");
        g.AddEdge("proj:R:q", "file:R:x.ts", "contains");
        g.AddEdge("proj:R:p", "file:R:c.ts", "contains");
        // c.ts and x.ts only share a release (p and q are both in 1.0): no connection
        Assert.Equal("No connection found within 8 steps.\n", Run(w => GraphQuery.Path(g, "c.ts", "x.ts", w)));
        // a one-way chain may still go through a project
        Assert.Contains("--contains--> c.ts", Run(w => GraphQuery.Path(g, "p", "c.ts", w)));
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
