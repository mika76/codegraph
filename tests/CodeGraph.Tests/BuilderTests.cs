using System.Text.Json.Nodes;
using CodeGraph.Build;
using CodeGraph.Model;
using CodeGraph.Scan;

namespace CodeGraph.Tests;

public class PathsTests
{
    [Theory]
    [InlineData(@"C:\A\b\..\C.cs", "c:/a/c.cs")]
    [InlineData("C:/a/./b//c", "c:/a/b/c")]
    [InlineData("a/../../b", "../b")]
    public void Key(string input, string expected) => Assert.Equal(expected, Paths.Key(input));

    [Theory]
    [InlineData(@"C:\x\Orders.dll", "orders")]
    [InlineData("a/b.c.vbp", "b.c")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("Makefile", "makefile")]
    [InlineData(null, "")]
    public void Stem(string? input, string expected) => Assert.Equal(expected, Paths.Stem(input));

    [Fact]
    public void Join_and_dir()
    {
        Assert.Equal("C:/r/x/../Shared/a.bas", Paths.Join("C:/r/x", @"..\Shared\a.bas"));
        Assert.Equal("D:/abs.cs", Paths.Join("C:/r", "D:/abs.cs"));
        Assert.Equal("a/b", Paths.Dir("a/b/c.txt"));
        Assert.Equal("", Paths.Dir("c.txt"));
    }
}

public class BuilderTests
{
    private const string Root = "C:/repos/Shop";

    private static FileRecord Rec(string rel, FileFacts facts, string repo = "Shop", string root = Root) =>
        new(repo, root, rel, $"{root}/{rel}", facts);

    private static Config Cfg(params string[] repos) =>
        new() { Repos = repos.Select(r => new RepoConfig { Name = r, Path = $"C:/repos/{r}" }).ToList() };

    private static FileFacts Vb(string lang = "vb6", params Symbol[] syms) => new() { Lang = lang, Symbols = [.. syms], Loc = 10 };

    private static Symbol Sym(string name, string kind, string? parent = null, string? vis = null, int line = 1) =>
        new() { Name = name, Kind = kind, Parent = parent, Visibility = vis, Line = line };

    private static FileFacts Project(string name, string type, params string[] members) => new()
    {
        Lang = "project",
        Project = new ProjectInfo { Format = "vbp", Lang = "vb6", Name = name, Type = type, Output = $"{name}.{type}", Members = [.. members] },
    };

    private static Graph Build(Config cfg, params FileRecord[] recs) => Builder.BuildGraph(cfg, recs).Graph;

    private static bool HasEdge(Graph g, string s, string t, string rel) => g.GetEdge(s, t, rel) is not null;

    [Fact]
    public void Project_members_including_shared_files_and_orphans()
    {
        var shared = Vb(syms: Sym("modUtils", "module"));
        shared.X["component"] = "modUtils";
        var g = Build(Cfg("Shop"),
            Rec("OrderEntry/OrderEntry.vbp", Project("OrderEntry", "exe", "frmMain.frm", @"..\Shared\modUtils.bas", "Missing.bas")),
            Rec("OrderEntry/frmMain.frm", Vb(syms: Sym("frmMain", "form"))),
            Rec("Shared/modUtils.bas", shared),
            Rec("Loose/Stray.cls", Vb(syms: Sym("Stray", "class"))));

        const string pid = "proj:Shop:OrderEntry/OrderEntry.vbp";
        Assert.Equal("OrderEntry", g.GetNode(pid)!.Name);
        Assert.True(HasEdge(g, pid, "file:Shop:OrderEntry/frmMain.frm", "contains"));
        Assert.True(HasEdge(g, pid, "file:Shop:Shared/modUtils.bas", "contains"));
        Assert.Equal(["Missing.bas"], g.GetNode(pid)!["missing_members"]!.AsArray().Select(x => x!.ToString()));
        Assert.True(g.GetNode("file:Shop:Loose/Stray.cls")!.Bool("orphan"));
        Assert.True(HasEdge(g, "area:Shop:Loose", "file:Shop:Loose/Stray.cls", "contains"));
    }

    [Fact]
    public void Vb_calls_resolve_to_public_module_routines_of_visible_projects_only()
    {
        var mod = Vb(syms: [Sym("modUtils", "module"), Sym("FormatMoney", "function", "modUtils", "public")]);
        mod.X["component"] = "modUtils";
        var other = Vb(syms: [Sym("modOther", "module"), Sym("FormatMoney", "function", "modOther", "public")]);
        other.X["component"] = "modOther";
        var form = Vb(syms: [Sym("frmMain", "form"), Sym("cmdOk_Click", "sub", "frmMain")]);
        form.Refs.Add(new Ref { Kind = "id", Name = "FormatMoney", From = "cmdOk_Click", FromParent = "frmMain", Line = 7 });

        var g = Build(Cfg("Shop"),
            Rec("A/A.vbp", Project("A", "exe", "frmMain.frm", "modUtils.bas")),
            Rec("A/frmMain.frm", form),
            Rec("A/modUtils.bas", mod),
            Rec("B/B.vbp", Project("B", "exe", "modOther.bas")),
            Rec("B/modOther.bas", other));

        var e = g.GetEdge("sym:Shop:A/frmMain.frm#frmMain.cmdOk_Click", "sym:Shop:A/modUtils.bas#modUtils.FormatMoney", "calls");
        Assert.NotNull(e);
        Assert.Equal(7, e!.Int("line"));
        Assert.DoesNotContain(g.Edges, x => x.Relation == "calls" && x.Target.EndsWith("modOther.FormatMoney"));
    }

    [Fact]
    public void Ambiguous_matches_are_skipped()
    {
        Symbol[] Mod(string m) => [Sym(m, "module"), Sym("Helper", "function", m, "public")];
        var a = Vb(syms: Mod("modA")); a.X["component"] = "modA";
        var b = Vb(syms: Mod("modB")); b.X["component"] = "modB";
        var caller = Vb(syms: [Sym("modC", "module"), Sym("Go", "sub", "modC")]);
        caller.Refs.Add(new Ref { Kind = "id", Name = "Helper", From = "Go", FromParent = "modC" });
        // all loose files in different areas of the same repo -> rank 3 each -> ambiguous
        var result = Builder.BuildGraph(Cfg("Shop"), [Rec("X/modA.bas", a), Rec("Y/modB.bas", b), Rec("Z/modC.bas", caller)]);
        Assert.DoesNotContain(result.Graph.Edges, e => e.Relation == "calls");
    }

    [Fact]
    public void Sql_definitions_and_refs_from_code()
    {
        var table = new FileFacts { Lang = "sql", Symbols = [new Symbol { Name = "Orders", Kind = "db_table", Line = 1 }] };
        table.Symbols[0].X["key"] = "orders";
        table.Symbols[0].X["verb"] = "create";
        var proc = new FileFacts { Lang = "sql", Symbols = [new Symbol { Name = "Order_Get", Kind = "db_proc", Line = 1 }] };
        proc.Symbols[0].X["key"] = "order_get";
        proc.Refs.Add(new Ref { Kind = "sql", Name = "dbo.Orders", Mode = "read", From = "order_get" });
        var cs = new FileFacts { Lang = "csharp", Symbols = [Sym("Repo", "class"), Sym("Load", "method", "Repo")] };
        cs.Refs.Add(new Ref { Kind = "sql", Name = "[dbo].[Order_Get]", Mode = "exec", From = "Load", FromParent = "Repo" });
        cs.Refs.Add(new Ref { Kind = "sql", Name = "NoSuchTable", Mode = "read", From = "Load", FromParent = "Repo" });

        var r = Builder.BuildGraph(Cfg("Shop"),
            [Rec("Database/Tables/dbo.Orders.sql", table), Rec("Database/Procs/dbo.Order_Get.sql", proc), Rec("Svc/Repo.cs", cs)]);
        var g = r.Graph;
        Assert.True(HasEdge(g, "file:Shop:Database/Tables/dbo.Orders.sql", "db:orders", "defines"));
        Assert.True(HasEdge(g, "db:order_get", "db:orders", "reads"));
        Assert.True(HasEdge(g, "sym:Shop:Svc/Repo.cs#Repo.Load", "db:order_get", "executes"));
        Assert.Equal(1, r.Unresolved["db:NoSuchTable"]);
        Assert.Equal(1, r.Stats["db_table"]);
    }

    /// <summary>Procedure names in every naming style; none of them is special.</summary>
    private static readonly string[] AnyStyle = ["test", "cleanup", "fetchall", "GetOrders", "get_orders", "ORDERS_GET", "Get2Orders"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Procedure_names_in_strings_link_only_next_to_database_access(bool dbContext)
    {
        var procs = new FileFacts { Lang = "sql" };
        foreach (var name in AnyStyle)
        {
            var s = new Symbol { Name = name, Kind = "db_proc", Line = 1 };
            s.X["key"] = name.ToLowerInvariant();
            procs.Symbols.Add(s);
        }
        var code = new FileFacts { Lang = "csharp", Symbols = [Sym("Repo", "class"), Sym("Load", "method", "Repo")] };
        foreach (var lit in AnyStyle)
        {
            var r = new Ref { Kind = "literal", Name = lit.ToLowerInvariant(), From = "Load", FromParent = "Repo" };
            if (dbContext) r.X["db"] = true;
            code.Refs.Add(r);
        }
        var cfg = Cfg("Shop");
        cfg.ProcPrefixes = [];
        var g = Build(cfg, Rec("Database/procs.sql", procs), Rec("Svc/Repo.cs", code));
        foreach (var name in AnyStyle)
            Assert.Equal(dbContext, HasEdge(g, "sym:Shop:Svc/Repo.cs#Repo.Load", "db:" + name.ToLowerInvariant(), "executes"));
    }

    [Fact]
    public void Missing_procedures_are_reported_with_where_they_were_first_called()
    {
        var code = new FileFacts { Lang = "csharp", Symbols = [Sym("Repo", "class"), Sym("Load", "method", "Repo")] };
        code.Refs.Add(new Ref { Kind = "sql", Mode = "exec", Name = "missing_proc", From = "Load", FromParent = "Repo", Line = 7 });
        code.Refs.Add(new Ref { Kind = "sql", Mode = "exec", Name = "missing_proc", From = "Load", FromParent = "Repo", Line = 9 });
        var r = Builder.BuildGraph(Cfg("Shop"), [Rec("Svc/Repo.cs", code)]);
        Assert.Equal("Shop: Svc/Repo.cs:7", r.Graph.Meta["unresolved_at"]!["exec:missing_proc"]!.ToString());
        var md = CodeGraph.Output.Report.BuildReport(r.Graph, r.Stats, r.Unresolved, [], []);
        Assert.Contains("| missing_proc | 2 | Shop: Svc/Repo.cs:7 |", md);
    }

    [Fact]
    public void Unlinked_candidates_do_not_count_as_unresolved_references()
    {
        var proc = new FileFacts { Lang = "sql", Symbols = [new Symbol { Name = "fetchall", Kind = "db_proc", Line = 1 }] };
        proc.Symbols[0].X["key"] = "fetchall";
        proc.Refs.Add(new Ref { Kind = "sql", Mode = "call", Name = "x.value", From = "fetchall" });  // XML method
        proc.Refs.Add(new Ref { Kind = "sql", Mode = "read", Name = "missing_table", From = "fetchall" });
        var code = new FileFacts { Lang = "csharp", Symbols = [Sym("Repo", "class"), Sym("Load", "method", "Repo")] };
        code.Refs.Add(new Ref { Kind = "literal", Name = "hello", From = "Load", FromParent = "Repo" });
        var linked = new Ref { Kind = "literal", Name = "fetchall", From = "Load", FromParent = "Repo" };
        linked.X["db"] = true;
        code.Refs.Add(linked);
        var r = Builder.BuildGraph(Cfg("Shop"), [Rec("Database/fetchall.sql", proc), Rec("Svc/Repo.cs", code)]);
        Assert.Equal(2, r.Stats["refs_total"]);     // the missing table and the linked string
        Assert.Equal(1, r.Stats["refs_resolved"]);
        Assert.False(r.Unresolved.ContainsKey("db:x.value"));
    }

    [Fact]
    public void Schema_qualified_strings_must_match_exactly()
    {
        var procs = new FileFacts { Lang = "sql" };
        foreach (var key in new[] { "save", "sales.get_orders" })
        {
            var s = new Symbol { Name = key, Kind = "db_proc", Line = 1 };
            s.X["key"] = key;
            procs.Symbols.Add(s);
        }
        var code = new FileFacts { Lang = "csharp", Symbols = [Sym("Repo", "class"), Sym("Load", "method", "Repo")] };
        foreach (var lit in new[] { "config.save", "sales.get_orders" })
        {
            var r = new Ref { Kind = "literal", Name = lit, From = "Load", FromParent = "Repo" };
            r.X["db"] = true;
            code.Refs.Add(r);
        }
        var g = Build(Cfg("Shop"), Rec("Database/procs.sql", procs), Rec("Svc/Repo.cs", code));
        Assert.True(HasEdge(g, "sym:Shop:Svc/Repo.cs#Repo.Load", "db:sales.get_orders", "executes"));
        Assert.False(HasEdge(g, "sym:Shop:Svc/Repo.cs#Repo.Load", "db:save", "executes"));
    }

    [Fact]
    public void A_configured_prefix_links_without_database_access()
    {
        var procs = new FileFacts { Lang = "sql" };
        foreach (var name in new[] { "bill_total", "total" })
        {
            var s = new Symbol { Name = name, Kind = "db_proc", Line = 1 };
            s.X["key"] = name;
            procs.Symbols.Add(s);
        }
        var consts = new FileFacts { Lang = "vb6", Symbols = [Sym("modNames", "module")] };
        consts.Refs.Add(new Ref { Kind = "literal", Name = "bill_total", From = null });
        consts.Refs.Add(new Ref { Kind = "literal", Name = "total", From = null });
        var cfg = Cfg("Shop");
        cfg.ProcPrefixes = ["bill"];
        var g = Build(cfg, Rec("Database/procs.sql", procs), Rec("App/modNames.bas", consts));
        Assert.True(HasEdge(g, "file:Shop:App/modNames.bas", "db:bill_total", "executes"));
        Assert.False(HasEdge(g, "file:Shop:App/modNames.bas", "db:total", "executes"));

        // the prefix on its own (name.StartsWith("bill_")) is no missing procedure
        consts.Refs.Add(new Ref { Kind = "literal", Name = "bill_", From = null });
        // nor is a column: one read through Fields("x") / row["x"], or the name of a known table column
        var col = new Ref { Kind = "literal", Name = "bill_count", From = null };
        col.X["column"] = true;
        consts.Refs.Add(col);
        consts.Refs.Add(new Ref { Kind = "literal", Name = "bill_points", From = null });
        consts.Refs.Add(new Ref { Kind = "literal", Name = "bill_purge", From = null });
        consts.Refs.Add(new Ref { Kind = "literal", Name = "bill_custom_", From = null }); // "bill_custom_" + x
        var table = new FileFacts { Lang = "sql", Symbols = [new Symbol { Name = "Accounts", Kind = "db_table", Line = 1 }] };
        table.Symbols[0].X["key"] = "accounts";
        table.Symbols[0].X["columns"] = new JsonArray("Id int", "[BILL_Points] int");
        var r = Builder.BuildGraph(cfg, [Rec("Database/procs.sql", procs), Rec("Database/accounts.sql", table), Rec("App/modNames.bas", consts)]);
        Assert.False(r.Unresolved.ContainsKey("exec:bill_"));
        Assert.False(r.Unresolved.ContainsKey("exec:bill_count"));
        Assert.False(r.Unresolved.ContainsKey("exec:bill_points"));
        Assert.False(r.Unresolved.ContainsKey("exec:bill_custom_"));
        Assert.True(r.Unresolved.ContainsKey("exec:bill_purge"));   // still reported: no column of that name
    }

    [Fact]
    public void Front_end_endpoint_calls_match_controller_routes()
    {
        var ctl = new FileFacts { Lang = "csharp", Symbols = [Sym("InventoryController", "class"), Sym("Items", "method", "InventoryController")] };
        ctl.Symbols[1].X["routes"] = new JsonArray(new JsonObject { ["verb"] = "GET", ["path"] = "inventory/items/{}" });
        var ts = new FileFacts { Lang = "typescript" };
        var call = new Ref { Kind = "endpoint", Name = "inventory/items/{}", Line = 3 };
        call.X["verb"] = "GET";
        call.X["raw"] = "/inventory/items/${id}";
        var miss = new Ref { Kind = "endpoint", Name = "nothing/here" };
        ts.Refs.AddRange([call, miss]);

        var r = Builder.BuildGraph(Cfg("Api", "StaffApp"),
            [Rec("Api/InventoryController.cs", ctl, "Api", "C:/repos/Api"), Rec("src/api.ts", ts, "StaffApp", "C:/repos/StaffApp")]);
        var e = r.Graph.GetEdge("file:StaffApp:src/api.ts", "sym:Api:Api/InventoryController.cs#InventoryController.Items", "calls_api");
        Assert.NotNull(e);
        Assert.Equal("/inventory/items/${id}", e!.Str("via"));
        Assert.Equal(["GET /inventory/items/{}"], r.Graph.GetNode("sym:Api:Api/InventoryController.cs#InventoryController.Items")!["routes"]!.AsArray().Select(x => x!.ToString()));
        Assert.Equal(1, r.Unresolved["api:/nothing/here"]);
    }

    [Theory]
    [InlineData("api/inventory/items/{}", "inventory/items/{}")]     // client adds /api
    [InlineData("v2/inventory/items/{}", "inventory/items/{}")]      // client adds a version
    [InlineData("backend/api/inventory/items", "inventory/items")]   // two segments
    [InlineData("inventory/items/{}", "svc/v1/inventory/items/{}")]  // the server has the base path
    [InlineData("inventory/items", "inventory/items")]
    public void Api_calls_match_routes_across_base_paths(string called, string route)
    {
        var r = MatchOne(called, route);
        Assert.NotNull(r.Graph.GetEdge("file:StaffApp:src/api.ts", "sym:Api:Api/InventoryController.cs#InventoryController.Items", "calls_api"));
    }

    [Theory]
    [InlineData("orders/{}", "{}")]              // only a parameter left after dropping 'orders'
    [InlineData("api/orders/{}", "stock/{}")]
    [InlineData("a/b/c/d/orders", "orders")]     // more than three extra segments
    [InlineData("orders", "orders/{}")]          // dropping the route's 'orders' leaves only a parameter
    [InlineData("getparameters", "reports/getparameters")] // 'reports' is one controller's name, not a base path
    public void Api_calls_do_not_match_on_parameters_or_unrelated_paths(string called, string route)
    {
        var r = MatchOne(called, route);
        Assert.Null(r.Graph.GetEdge("file:StaffApp:src/api.ts", "sym:Api:Api/InventoryController.cs#InventoryController.Items", "calls_api"));
    }

    [Fact]
    public void Candidate_api_calls_link_on_a_route_match_and_are_otherwise_silent()
    {
        var ctl = new FileFacts { Lang = "csharp", Symbols = [Sym("InventoryController", "class"), Sym("Items", "method", "InventoryController")] };
        ctl.Symbols[1].X["routes"] = new JsonArray(new JsonObject { ["verb"] = "GET", ["path"] = "inventory/items" });
        var ts = new FileFacts { Lang = "typescript" };
        foreach (var path in new[] { "inventory/items", "home/page" })
        {
            var r = new Ref { Kind = "endpoint", Name = path, Line = 3 };
            r.X["candidate"] = true;
            ts.Refs.Add(r);
        }
        var res = Builder.BuildGraph(Cfg("Api", "StaffApp"),
            [Rec("Api/InventoryController.cs", ctl, "Api", "C:/repos/Api"), Rec("src/api.ts", ts, "StaffApp", "C:/repos/StaffApp")]);
        Assert.NotNull(res.Graph.GetEdge("file:StaffApp:src/api.ts", "sym:Api:Api/InventoryController.cs#InventoryController.Items", "calls_api"));
        Assert.DoesNotContain(res.Unresolved.Keys, k => k.StartsWith("api:", StringComparison.Ordinal));
        Assert.Equal(1, res.Stats["refs_total"]);
    }

    [Fact]
    public void Url_action_links_to_the_named_controller_action_whatever_its_route()
    {
        var sched = new FileFacts { Lang = "csharp", Symbols = [Sym("SchedulerController", "class"), Sym("SelectReport", "method", "SchedulerController")] };
        sched.Symbols[1].X["routes"] = new JsonArray(new JsonObject { ["verb"] = "GET", ["path"] = "scheduling/site/{}/selectreport" });
        var settings = new FileFacts { Lang = "csharp", Symbols = [Sym("SettingsController", "class"), Sym("Edit", "method", "SettingsController")] };
        settings.Symbols[1].X["routes"] = new JsonArray(new JsonObject { ["verb"] = "GET", ["path"] = "settings/sites/{}/edit" });
        var view = new FileFacts { Lang = "razor", Symbols = [Sym("add", "view")] };
        foreach (var path in new[] { "scheduler/selectreport", "scheduler/edit" })
        {
            var r = new Ref { Kind = "endpoint", Name = path, FromParent = "add", Line = 1 };
            r.X["conventional"] = true;
            view.Refs.Add(r);
        }
        var g = Build(Cfg("Web"), Rec("Controllers/SchedulerController.cs", sched, "Web", "C:/repos/Web"),
            Rec("Controllers/SettingsController.cs", settings, "Web", "C:/repos/Web"), Rec("Views/Scheduler/add.cshtml", view, "Web", "C:/repos/Web"));
        Assert.True(HasEdge(g, "sym:Web:Views/Scheduler/add.cshtml#add", "sym:Web:Controllers/SchedulerController.cs#SchedulerController.SelectReport", "calls_api"));
        Assert.False(HasEdge(g, "sym:Web:Views/Scheduler/add.cshtml#add", "sym:Web:Controllers/SettingsController.cs#SettingsController.Edit", "calls_api"));
    }

    [Fact]
    public void Impact_on_an_action_includes_the_view_it_renders()
    {
        var g = new Graph();
        g.Node("db:invoices", ("kind", "db_table"), ("name", "Invoices"));
        g.Node("sym:W:Repo.cs#Repo.Total", ("kind", "method"), ("name", "Total"), ("qual", "Repo.Total"));
        g.Node("sym:W:MainController.cs#MainController.Index", ("kind", "method"), ("name", "Index"), ("qual", "MainController.Index"));
        g.Node("sym:W:Views/Main/Index.cshtml#Index", ("kind", "view"), ("name", "Index"), ("qual", "Main/Index view"));
        g.Node("sym:W:Views/Main/Other.cshtml#Other", ("kind", "view"), ("name", "Other"), ("qual", "Main/Other view"));
        g.AddEdge("sym:W:Repo.cs#Repo.Total", "db:invoices", "reads");
        g.AddEdge("sym:W:MainController.cs#MainController.Index", "sym:W:Repo.cs#Repo.Total", "calls");
        g.AddEdge("sym:W:MainController.cs#MainController.Index", "sym:W:Views/Main/Index.cshtml#Index", "renders");
        var w = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, "Invoices", w, depth: 3);
        Assert.Contains("3 things", w.ToString());   // the repo method, the action, and the page that shows its data
        // a view still affects the action rendering it (the existing direction)
        var w2 = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, "Main/Index view", w2, depth: 1);
        Assert.Contains("1 thing", w2.ToString());
    }

    [Fact]
    public void A_navigation_link_is_links_to_and_impact_does_not_follow_it()
    {
        var ctl = new FileFacts { Lang = "csharp", Symbols = [Sym("MainController", "class"), Sym("Index", "method", "MainController")] };
        var view = new FileFacts { Lang = "razor", Symbols = [Sym("Settings", "view")] };
        var link = new Ref { Kind = "endpoint", Name = "main/index", FromParent = "Settings", Line = 1 };
        link.X["conventional"] = true;
        link.X["navigation"] = true;
        view.Refs.Add(link);
        var g = Build(Cfg("Web"), Rec("Controllers/MainController.cs", ctl, "Web", "C:/repos/Web"), Rec("Views/Settings/Settings.cshtml", view, "Web", "C:/repos/Web"));
        Assert.True(HasEdge(g, "sym:Web:Views/Settings/Settings.cshtml#Settings", "sym:Web:Controllers/MainController.cs#MainController.Index", "links_to"));
        Assert.DoesNotContain(g.Edges, e => e.Relation == "calls_api");
        var w = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, "MainController.Index", w, depth: 3);
        Assert.Contains("0 things", w.ToString());
    }

    [Fact]
    public void Url_action_never_links_to_another_apps_controller()
    {
        var api = new FileFacts { Lang = "csharp", Symbols = [Sym("ReportsController", "class"), Sym("Run", "method", "ReportsController")] };
        api.Symbols[1].X["routes"] = new JsonArray(new JsonObject { ["verb"] = "GET", ["path"] = "reports/run" });
        var view = new FileFacts { Lang = "razor", Symbols = [Sym("Index", "view")] };
        var r = new Ref { Kind = "endpoint", Name = "reports/run", FromParent = "Index", Line = 1 };
        r.X["conventional"] = true;
        view.Refs.Add(r);
        var g = Build(Cfg("Api", "Web"), Rec("Controllers/ReportsController.cs", api, "Api", "C:/repos/Api"),
            Rec("Views/Home/Index.cshtml", view, "Web", "C:/repos/Web"));
        Assert.DoesNotContain(g.Edges, e => e.Relation == "calls_api");
    }

    [Fact]
    public void Client_routes_link_navigation_to_screens()
    {
        static FileFacts Js(string rel, string text) => CodeGraph.Parsers.JavaScriptParser.Parse(rel, text);
        var router = Js("src/router/index.ts", """
            import { createRouter } from 'vue-router'
            import Shell from '../components/shell.vue'
            import Orders from '../views/orders.vue'
            export default createRouter({ routes: [
              { path: '/', component: Shell, redirect: { name: 'orders' }, children: [
                { path: 'orders', name: 'orders', component: Orders, meta: { requiresAccess: 'Mod_Orders' } },
                { path: 'orders/:id/lines', component: () => import('../views/lines.vue') },
              ] },
            ] })
            """);
        var shell = Js("src/components/shell.vue", "<template><router-link :to=\"{ name: 'orders' }\">Orders</router-link></template>\n<script setup lang=\"ts\"></script>");
        var orders = Js("src/views/orders.vue", """
            <template><div /></template>
            <script setup lang="ts">
            import { useRouter } from 'vue-router'
            const router = useRouter()
            function open(id: number) { router.push(`/orders/${id}/lines`) }
            </script>
            """);
        var lines = Js("src/views/lines.vue", "<template><div /></template>\n<script setup lang=\"ts\"></script>");
        var g = Build(Cfg("Web"), Rec("src/router/index.ts", router, "Web", "C:/repos/Web"), Rec("src/components/shell.vue", shell, "Web", "C:/repos/Web"),
            Rec("src/views/orders.vue", orders, "Web", "C:/repos/Web"), Rec("src/views/lines.vue", lines, "Web", "C:/repos/Web"));

        const string Root = "route:Web:/", OrdersRoute = "route:Web:/orders", LinesRoute = "route:Web:/orders/:id/lines";
        Assert.Equal("Mod_Orders", g.GetNode(OrdersRoute)!["meta"]!["requiresAccess"]!.ToString());
        Assert.True(HasEdge(g, OrdersRoute, "sym:Web:src/views/orders.vue#orders", "routes_to"));
        Assert.True(HasEdge(g, LinesRoute, "sym:Web:src/views/lines.vue#lines", "routes_to"));   // lazy import
        Assert.True(HasEdge(g, Root, OrdersRoute, "contains"));
        Assert.True(HasEdge(g, Root, OrdersRoute, "redirects_to"));
        Assert.True(HasEdge(g, "sym:Web:src/views/orders.vue#orders.open", LinesRoute, "links_to")); // `/orders/${id}/lines`
        Assert.True(HasEdge(g, "sym:Web:src/components/shell.vue#shell", OrdersRoute, "links_to"));   // { name: 'orders' }

        // "how does the user get to the lines screen?"
        var w = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Path(g, "orders.open", "sym:Web:src/views/lines.vue#lines", w);
        Assert.Contains("--links_to--> /orders/:id/lines (route)", w.ToString());
        Assert.Contains("--routes_to--> lines (component)", w.ToString());
        // impact on the lines screen: its route, but not the screens that merely link to it
        var imp = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, "sym:Web:src/views/lines.vue#lines", imp, depth: 3);
        Assert.DoesNotContain("orders.vue", imp.ToString());
    }

    [Fact]
    public void Screen_to_api_through_a_store_and_a_composable()
    {
        var svc = CodeGraph.Parsers.JavaScriptParser.Parse("src/svc.ts", """
            export function useOrderService() {
              const loadOrders = async () => api.get('/orders/open')
              return { loadOrders }
            }
            """);
        var store = CodeGraph.Parsers.JavaScriptParser.Parse("src/store.ts", """
            import { defineStore } from 'pinia'
            import { useOrderService } from './svc'
            export const useShopStore = defineStore('shop', {
              actions: { async refresh() { const svc = useOrderService(); return svc.loadOrders() } },
            })
            """);
        var view = CodeGraph.Parsers.JavaScriptParser.Parse("src/Orders.vue", """
            <script setup lang="ts">
            import { useShopStore } from './store'
            const store = useShopStore()
            function reload() { store.refresh() }
            </script>
            """);
        var ctl = new FileFacts { Lang = "csharp", Symbols = [Sym("OrdersController", "class"), Sym("Open", "method", "OrdersController")] };
        ctl.Symbols[1].X["routes"] = new JsonArray(new JsonObject { ["verb"] = "GET", ["path"] = "orders/open" });
        var g = Build(Cfg("Web", "Api"), Rec("src/svc.ts", svc, "Web", "C:/repos/Web"), Rec("src/store.ts", store, "Web", "C:/repos/Web"),
            Rec("src/Orders.vue", view, "Web", "C:/repos/Web"), Rec("OrdersController.cs", ctl, "Api", "C:/repos/Api"));
        const string Load = "sym:Web:src/svc.ts#useOrderService.loadOrders", Refresh = "sym:Web:src/store.ts#useShopStore.refresh";
        Assert.True(HasEdge(g, Load, "sym:Api:OrdersController.cs#OrdersController.Open", "calls_api"));
        Assert.True(HasEdge(g, Refresh, Load, "calls"));
        Assert.Contains(g.Edges, e => e.Target == Refresh && e.Relation == "calls" && e.Source.StartsWith("sym:Web:src/Orders.vue#", StringComparison.Ordinal));
        // impact on the API action now reaches the screen
        var w = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, "OrdersController.Open", w, depth: 4);
        Assert.Contains("src/Orders.vue", w.ToString());
    }

    [Fact]
    public void A_vb6_form_shown_from_code_is_opened_by_it()
    {
        var main = Vb("vb6", Sym("frmMain", "form"), Sym("cmdOrder_Click", "sub", "frmMain"));
        main.Refs.Add(new Ref { Kind = "member", Qual = "frmOrder", Name = "Show", From = "cmdOrder_Click", FromParent = "frmMain", Line = 5 });
        main.Refs.Add(new Ref { Kind = "member", Qual = "frmOrder", Name = "Caption", From = "cmdOrder_Click", FromParent = "frmMain", Line = 6 });
        var order = Vb("vb6", Sym("frmOrder", "form"));
        var g = Build(Cfg("Shop"), Rec("App/frmMain.frm", main), Rec("App/frmOrder.frm", order));
        const string Click = "sym:Shop:App/frmMain.frm#frmMain.cmdOrder_Click", Order = "sym:Shop:App/frmOrder.frm#frmOrder";
        Assert.True(HasEdge(g, Click, Order, "opens"));
        var w = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, Order, w, depth: 1);
        Assert.Contains("cmdOrder_Click", w.ToString()); // where is this form opened from
    }

    [Fact]
    public void Assembly_references_only_reach_dotnet_projects_and_never_the_framework()
    {
        FileFacts Net(string name, params LibRef[] libs) => new()
        {
            Lang = "project",
            Project = new ProjectInfo { Format = "csproj", Lang = "csharp", Name = name, Assembly = name, Type = "dll", Output = name + ".dll", Libs = [.. libs] },
        };
        var api = Net("Shop.Api",
            new LibRef { Kind = "assembly", Name = "System", File = "System.dll" },
            new LibRef { Kind = "assembly", Name = "Shop.Data", File = "Shop.Data.dll" },
            new LibRef { Kind = "assembly", Name = "Billing", File = "Billing.dll" });
        var g = Build(Cfg("Shop"),
            Rec("Api/Shop.Api.csproj", api), Rec("Data/Shop.Data.csproj", Net("Shop.Data")),
            Rec("System/System.vbp", Project("System", "exe")), Rec("Billing/Billing.vbp", Project("Billing", "exe")));
        Assert.True(HasEdge(g, "proj:Shop:Api/Shop.Api.csproj", "proj:Shop:Data/Shop.Data.csproj", "depends_on"));
        Assert.False(HasEdge(g, "proj:Shop:Api/Shop.Api.csproj", "proj:Shop:System/System.vbp", "depends_on"));   // framework
        Assert.False(HasEdge(g, "proj:Shop:Api/Shop.Api.csproj", "proj:Shop:Billing/Billing.vbp", "depends_on")); // a VB6 exe
        Assert.False(g.GetNode("proj:Shop:Api/Shop.Api.csproj")!.Bool("test"));

        var tests = Net("Shop.Api.Tests", new LibRef { Kind = "package", Name = "Microsoft.NET.Test.Sdk" });
        var g2 = Build(Cfg("Shop"), Rec("Tests/Shop.Api.Tests.csproj", tests));
        Assert.True(g2.GetNode("proj:Shop:Tests/Shop.Api.Tests.csproj")!.Bool("test"));
    }

    [Fact]
    public void Implementations_and_overrides_are_linked_and_followed_by_impact()
    {
        var code = CodeGraph.Parsers.CSharpParser.Parse("Repo.cs", """
            public interface IOrderRepo { Order Get(int id); void Save(Order o); }
            public class OrderRepo : IOrderRepo
            {
                public OrderRepo() { }
                public Order Get(int id) => null;
                public void Save(Order o) { }
                public void Helper() { }
            }
            public abstract class Job { public abstract void Run(); }
            public class NightlyJob : Job { public override void Run() { } }
            """);
        var g = Build(Cfg("Shop"), Rec("Svc/Repo.cs", code));
        const string F = "sym:Shop:Svc/Repo.cs#";
        Assert.True(HasEdge(g, F + "OrderRepo.Get", F + "IOrderRepo.Get", "implements"));
        Assert.True(HasEdge(g, F + "OrderRepo.Save", F + "IOrderRepo.Save", "implements"));
        Assert.True(HasEdge(g, F + "NightlyJob.Run", F + "Job.Run", "overrides"));
        Assert.DoesNotContain(g.Edges, e => e.Source == F + "OrderRepo.Helper" && e.Relation == "implements");
        var w = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, "IOrderRepo.Get", w, depth: 1);
        Assert.Contains("Get", w.ToString().Split('\n').Last(l => l.Contains("Repo.cs")));
        Assert.Contains("1 thing", w.ToString());
    }

    [Fact]
    public void Db_objects_and_columns_carry_the_configured_database()
    {
        static FileFacts Sql(string rel, string text) => CodeGraph.Parsers.SqlParser.Parse(rel, text);
        var cfg = Cfg("Shop");
        cfg.Repos[0].Database = "Sales";
        cfg.Repos[0].Databases = new() { ["Billing/Db"] = "Billing", ["Billing/Db/Archive"] = "BillingArchive" };
        var g = Build(cfg,
            Rec("Db/dbo.Orders.sql", Sql("Db/dbo.Orders.sql", "CREATE TABLE dbo.Orders (Id int, Total money)")),
            Rec("Billing/Db/dbo.Invoices.sql", Sql("Billing/Db/dbo.Invoices.sql", "CREATE TABLE dbo.Invoices (Id int)")),
            Rec("Billing/Db/Archive/dbo.Old.sql", Sql("Billing/Db/Archive/dbo.Old.sql", "CREATE TABLE dbo.Old (Id int)")),
            Rec("Billing/DbTools/dbo.Tool.sql", Sql("Billing/DbTools/dbo.Tool.sql", "CREATE TABLE dbo.Tool (Id int)")));
        Assert.Equal("Sales", g.GetNode("db:orders")!.Str("database"));
        Assert.Equal("Sales", g.GetNode("col:orders.total")!.Str("database"));
        Assert.Equal("Billing", g.GetNode("db:invoices")!.Str("database"));
        Assert.Equal("BillingArchive", g.GetNode("db:old")!.Str("database"));
        Assert.Equal("Sales", g.GetNode("db:tool")!.Str("database"));  // a folder name that only starts the same
        Assert.Equal("Billing", CodeGraph.Query.Structured.NodeJson(g, "db:invoices")["database"]!.ToString());
        Assert.Null(Build(Cfg("Shop"), Rec("Db/dbo.Orders.sql", Sql("Db/dbo.Orders.sql", "CREATE TABLE dbo.Orders (Id int)")))
            .GetNode("db:orders")!.Str("database"));
    }

    [Fact]
    public void Columns_are_linked_to_the_procs_views_code_and_patches_that_use_them()
    {
        static FileFacts Sql(string rel, string text) => CodeGraph.Parsers.SqlParser.Parse(rel, text);
        var orders = Sql("Db/Tables/dbo.Orders.sql", "CREATE TABLE dbo.Orders (Id int NOT NULL, Total money NULL, Status int NULL)");
        var lines = Sql("Db/Tables/dbo.Lines.sql", "CREATE TABLE dbo.Lines (Id int NOT NULL, OrderId int NOT NULL, Qty int NULL)");
        var proc = Sql("Db/Procs/dbo.usp_Close.sql", """
            CREATE PROCEDURE dbo.usp_Close @id int AS
            BEGIN
              UPDATE dbo.Orders SET Status = 2 WHERE Id = @id
              SELECT Qty FROM dbo.Lines WHERE OrderId = @id
            END
            """);
        var view = Sql("Db/Views/dbo.v_Open.sql", """
            CREATE VIEW dbo.v_Open AS
            SELECT o.Total, l.Qty, Id FROM dbo.Orders o JOIN dbo.Lines l ON l.OrderId = o.Id
            """);
        var patch = Sql("Db/Patches/Patch12.sql", "ALTER TABLE dbo.Orders ALTER COLUMN Total decimal(10, 2) NOT NULL\nGO\n");
        var repo = CodeGraph.Parsers.CSharpParser.Parse("Svc/OrderRepo.cs", """
            class OrderRepo
            {
                decimal Total(int id) => conn.QuerySingle<decimal>("SELECT Total FROM Orders WHERE Id = @id", new { id });
            }
            """);
        var form = CodeGraph.Parsers.Vb6Parser.Parse("App/frmOrder.frm", """
            VERSION 5.00
            Begin VB.Form frmOrder
            End
            Attribute VB_Name = "frmOrder"
            Private Sub Form_Load()
                Set rs = cn.Execute("SELECT Status FROM Orders WHERE Id = " & mId)
            End Sub
            """);
        var g = Build(Cfg("Shop"), Rec("Db/Tables/dbo.Orders.sql", orders), Rec("Db/Tables/dbo.Lines.sql", lines),
            Rec("Db/Procs/dbo.usp_Close.sql", proc), Rec("Db/Views/dbo.v_Open.sql", view), Rec("Db/Patches/Patch12.sql", patch),
            Rec("Svc/OrderRepo.cs", repo), Rec("App/frmOrder.frm", form));

        Assert.Equal("Orders.Total", g.GetNode("col:orders.total")!.Name);
        Assert.True(HasEdge(g, "db:orders", "col:orders.total", "contains"));
        Assert.True(HasEdge(g, "db:usp_close", "col:orders.status", "writes"));
        Assert.True(HasEdge(g, "db:usp_close", "col:lines.qty", "reads"));
        Assert.True(HasEdge(g, "db:v_open", "col:orders.total", "reads"));
        Assert.True(HasEdge(g, "db:v_open", "col:lines.orderid", "reads"));
        Assert.True(HasEdge(g, "db:v_open", "col:orders.id", "reads")); // o.Id is qualified
        Assert.True(HasEdge(g, "sym:Shop:Svc/OrderRepo.cs#OrderRepo.Total", "col:orders.total", "reads"));
        Assert.True(HasEdge(g, "sym:Shop:App/frmOrder.frm#frmOrder.Form_Load", "col:orders.status", "reads"));
        Assert.True(HasEdge(g, "file:Shop:Db/Patches/Patch12.sql", "col:orders.total", "alters"));
        Assert.DoesNotContain(g.Edges, e => e.Source == "db:orders" && e.Target.StartsWith("col:", StringComparison.Ordinal) && e.Relation != "contains");

        var w = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, "Orders.Total", w, depth: 1);
        string impact = w.ToString();
        Assert.Contains("v_Open", impact);
        Assert.Contains("Svc/OrderRepo.cs", impact);
        Assert.Contains("Patch12.sql", impact);
        Assert.DoesNotContain("usp_Close", impact); // it writes Status, not Total
    }

    [Fact]
    public void A_prepared_ado_command_is_run_by_the_routine_that_executes_it()
    {
        static FileFacts Vb6(string rel, string text)
        {
            var f = CodeGraph.Parsers.Vb6Parser.Parse(rel, text);
            DbAccess.Mark(rel, text, f);
            return f;
        }
        var proc = CodeGraph.Parsers.SqlParser.Parse("p.sql", "CREATE PROCEDURE dbo.close_order @id int AS UPDATE Orders SET Status = 2 WHERE Id = @id");
        var other = CodeGraph.Parsers.SqlParser.Parse("q.sql", "CREATE PROCEDURE dbo.list_orders AS SELECT Id FROM Orders");
        var orders = CodeGraph.Parsers.SqlParser.Parse("Orders.sql", "CREATE TABLE dbo.Orders (Id int, Status int)");
        var procs = Vb6("Common/modProcs.bas", """
            Attribute VB_Name = "modProcs"
            Global Close_Order As New ADODB.Command
            Public List_Orders As ADODB.Command
            Public Sub Init_Parameters()
                Close_Order.CommandText = "close_order"
                Close_Order.CommandType = adCmdStoredProc
                Close_Order.Parameters.Append Close_Order.CreateParameter("id", adInteger, adParamInput)
                Set List_Orders = New ADODB.Command
                With List_Orders
                    .CommandText = "list_orders"
                End With
            End Sub
            """);
        var form = Vb6("App/frmOrder.frm", """
            VERSION 5.00
            Begin VB.Form frmOrder
            End
            Attribute VB_Name = "frmOrder"
            Private Sub cmdClose_Click()
                Close_Order.Parameters("id").Value = mId
                Close_Order.Execute
            End Sub
            Private Sub Form_Load()
                rs.Open List_Orders
            End Sub
            """);
        var g = Build(Cfg("Shop"), Rec("Db/p.sql", proc), Rec("Db/q.sql", other), Rec("Db/Orders.sql", orders),
            Rec("Common/modProcs.bas", procs), Rec("App/frmOrder.frm", form));

        const string cmd = "sym:Shop:Common/modProcs.bas#modProcs.Close_Order";
        const string list = "sym:Shop:Common/modProcs.bas#modProcs.List_Orders";
        const string init = "sym:Shop:Common/modProcs.bas#modProcs.Init_Parameters";
        const string click = "sym:Shop:App/frmOrder.frm#frmOrder.cmdClose_Click";
        const string load = "sym:Shop:App/frmOrder.frm#frmOrder.Form_Load";
        Assert.Equal("command", g.GetNode(cmd)!.Kind);
        Assert.True(HasEdge(g, cmd, "db:close_order", "executes"));
        Assert.True(HasEdge(g, list, "db:list_orders", "executes"));
        // the setup routine only prepares the commands
        Assert.True(HasEdge(g, init, cmd, "prepares"));
        Assert.True(HasEdge(g, init, list, "prepares"));
        Assert.False(HasEdge(g, init, "db:close_order", "executes"));
        // the routines that run them execute the procedures, via the command
        Assert.True(HasEdge(g, click, cmd, "executes"));
        Assert.Equal("Close_Order", g.GetEdge(click, "db:close_order", "executes")!.Str("via"));
        Assert.True(HasEdge(g, click, cmd, "prepares")); // setting a parameter value
        Assert.True(HasEdge(g, load, "db:list_orders", "executes"));

        var w = new StringWriter { NewLine = "\n" };
        CodeGraph.Query.GraphQuery.Impact(g, "close_order", w, depth: 2);
        string impact = w.ToString();
        Assert.Contains("cmdClose_Click", impact);
        Assert.DoesNotContain("Init_Parameters", impact);
    }

    [Fact]
    public void Columns_in_dynamic_sql_and_three_part_names_are_linked()
    {
        var orders = CodeGraph.Parsers.SqlParser.Parse("Orders.sql", "CREATE TABLE dbo.Orders (Id int, Total money, Status int)");
        var proc = CodeGraph.Parsers.SqlParser.Parse("p.sql", """
            CREATE PROCEDURE dbo.usp_Report @from int AS
            BEGIN
              DECLARE @sql nvarchar(max)
              SET @sql = 'SELECT dbo.Orders.Total, o.Status FROM dbo.Orders o WHERE dbo.Orders.Id > ' + CAST(@from AS varchar)
              EXEC (@sql)
            END
            """);
        var g = Build(Cfg("Shop"), Rec("Orders.sql", orders), Rec("p.sql", proc));
        Assert.True(HasEdge(g, "db:usp_report", "col:orders.total", "reads"));   // dbo.Orders.Total (schema.table.column)
        Assert.True(HasEdge(g, "db:usp_report", "col:orders.status", "reads"));  // o.Status (alias)
        Assert.True(HasEdge(g, "db:usp_report", "col:orders.id", "reads"));
        Assert.Equal("true", g.GetEdge("db:usp_report", "col:orders.total", "reads")!.Str("dynamic"));
    }

    [Fact]
    public void An_unqualified_column_in_two_joined_tables_is_not_guessed()
    {
        var orders = CodeGraph.Parsers.SqlParser.Parse("Orders.sql", "CREATE TABLE Orders (Id int, Total money)");
        var lines = CodeGraph.Parsers.SqlParser.Parse("Lines.sql", "CREATE TABLE Lines (Id int, Qty int)");
        var view = CodeGraph.Parsers.SqlParser.Parse("v.sql", "CREATE VIEW v AS SELECT Id, Qty FROM Orders o JOIN Lines l ON l.Id = o.Id");
        var g = Build(Cfg("Shop"), Rec("Orders.sql", orders), Rec("Lines.sql", lines), Rec("v.sql", view));
        Assert.True(HasEdge(g, "db:v", "col:lines.qty", "reads"));     // only Lines has Qty
        Assert.True(HasEdge(g, "db:v", "col:orders.id", "reads"));     // o.Id is qualified
        Assert.True(HasEdge(g, "db:v", "col:lines.id", "reads"));      // l.Id is qualified
        var bare = CodeGraph.Parsers.SqlParser.Parse("w.sql", "CREATE VIEW w AS SELECT Id FROM Orders, Lines");
        var g2 = Build(Cfg("Shop"), Rec("Orders.sql", orders), Rec("Lines.sql", lines), Rec("w.sql", bare));
        Assert.DoesNotContain(g2.Edges, e => e.Source == "db:w" && e.Target.EndsWith(".id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_csharp_http_client_reaches_the_api_action()
    {
        var api = CodeGraph.Parsers.CSharpParser.Parse("Api/SalesController.cs", """
            [Route("api/[controller]")]
            public class SalesController : Controller
            {
                [HttpGet("MonthTotal/{site}")] public decimal MonthTotal(int site) => 0;
            }
            """);
        var client = CodeGraph.Parsers.CSharpParser.Parse("Web/ApiClient.cs", """
            class ApiClient
            {
                async Task<decimal> GetMonthTotal(int site)
                {
                    string query = $"api/Sales/MonthTotal/{site}";
                    var response = await client.GetAsync(query);
                    return 0;
                }
            }
            """);
        var g = Build(Cfg("Api", "Web"), Rec("Api/SalesController.cs", api, "Api", "C:/repos/Api"), Rec("Web/ApiClient.cs", client, "Web", "C:/repos/Web"));
        Assert.True(HasEdge(g, "sym:Web:Web/ApiClient.cs#ApiClient.GetMonthTotal", "sym:Api:Api/SalesController.cs#SalesController.MonthTotal", "calls_api"));
    }

    [Fact]
    public void A_csharp_dllimport_reaches_the_cpp_project_and_its_exported_function()
    {
        var cppProj = new FileFacts
        {
            Lang = "project",
            Project = new ProjectInfo { Format = "dsp", Lang = "cpp", Name = "Rand", Type = "dll", Output = "Rand.dll", Members = ["rand.cpp"] },
        };
        var cpp = new FileFacts { Lang = "cpp", Symbols = [Sym("getNextNumber", "function")] };
        var csProj = new FileFacts   // a .NET wrapper project with the same name
        {
            Lang = "project",
            Project = new ProjectInfo { Format = "csproj", Lang = "csharp", Name = "Rand", Assembly = "Rand", Type = "dll", Output = "Rand.dll" },
        };
        var cs = CodeGraph.Parsers.CSharpParser.Parse("Wrapper/Native.cs", """
            static class Native
            {
                [DllImport("Rand.dll")] public static extern uint getNextNumber();
                [DllImport("Other.dll")] public static extern int Ping();
            }
            """);
        var g = Build(Cfg("Shop"), Rec("Rand/Rand.dsp", cppProj), Rec("Rand/rand.cpp", cpp), Rec("Wrapper/Rand.csproj", csProj), Rec("Wrapper/Native.cs", cs));
        const string Extern = "sym:Shop:Wrapper/Native.cs#Native.getNextNumber";
        Assert.True(HasEdge(g, Extern, "sym:Shop:Rand/rand.cpp#getNextNumber", "calls_native"));
        Assert.DoesNotContain(g.Edges, e => e.Source == Extern && e.Target == "proj:Shop:Wrapper/Rand.csproj");
        Assert.True(HasEdge(g, "sym:Shop:Wrapper/Native.cs#Native.Ping", "ext:other.dll", "calls_native"));
    }

    private static BuildResult MatchOne(string called, string route)
    {
        var ctl = new FileFacts { Lang = "csharp", Symbols = [Sym("InventoryController", "class"), Sym("Items", "method", "InventoryController")] };
        ctl.Symbols[1].X["routes"] = new JsonArray(new JsonObject { ["verb"] = "GET", ["path"] = route });
        var ts = new FileFacts { Lang = "typescript" };
        var call = new Ref { Kind = "endpoint", Name = called, Line = 3 };
        call.X["verb"] = "GET";
        ts.Refs.Add(call);
        // a second controller under the same base path (svc/v1/...), so svc/v1 counts as a server base path
        var other = new FileFacts { Lang = "csharp", Symbols = [Sym("StockController", "class"), Sym("List", "method", "StockController")] };
        other.Symbols[1].X["routes"] = new JsonArray(new JsonObject { ["verb"] = "GET", ["path"] = "svc/v1/stock/list" });
        return Builder.BuildGraph(Cfg("Api", "StaffApp"),
            [Rec("Api/InventoryController.cs", ctl, "Api", "C:/repos/Api"), Rec("Api/StockController.cs", other, "Api", "C:/repos/Api"),
             Rec("src/api.ts", ts, "StaffApp", "C:/repos/StaffApp")]);
    }

    [Fact]
    public void Js_imports_resolve_relative_alias_and_package_entry()
    {
        const string am = "C:/repos/StaffApp", lib = "C:/repos/UiComponents";
        var pkg = new FileFacts { Lang = "project", Project = new ProjectInfo { Format = "npm", Lang = "javascript", Name = "@acme/ui-components", Output = "@acme/ui-components", SdkStyle = true } };
        var main = new FileFacts { Lang = "typescript" };
        main.X["imports"] = new JsonArray(new JsonObject { ["spec"] = "./components/Panel.vue", ["line"] = 1, ["names"] = new JsonObject { ["Panel"] = "default" } });
        main.X["reexports"] = new JsonObject { ["panelComponent"] = new JsonArray(null, "Panel") };
        var panel = new FileFacts { Lang = "vue", Symbols = [Sym("Panel", "component")] };
        panel.X["default_export"] = "Panel";
        var app = new FileFacts { Lang = "vue", Symbols = [Sym("App", "component")] };
        app.X["imports"] = new JsonArray(
            new JsonObject { ["spec"] = "@acme/ui-components", ["line"] = 2, ["names"] = new JsonObject { ["panelComponent"] = "panelComponent" } },
            new JsonObject { ["spec"] = "@/util/format", ["line"] = 3, ["names"] = new JsonObject { ["fmt"] = "fmt" } },
            new JsonObject { ["spec"] = "axios", ["line"] = 4 });
        app.Refs.Add(new Ref { Kind = "component", Name = "panelComponent", From = null, FromParent = "App", Line = 9 });
        app.Refs.Add(new Ref { Kind = "call", Name = "fmt", FromParent = "App", Line = 10 });
        var util = new FileFacts { Lang = "typescript", Symbols = [Sym("fmt", "function")] };

        var g = Build(Cfg("StaffApp", "UiComponents"),
            Rec("package.json", pkg, "UiComponents", lib),
            Rec("src/main.ts", main, "UiComponents", lib),
            Rec("src/components/Panel.vue", panel, "UiComponents", lib),
            Rec("src/App.vue", app, "StaffApp", am),
            Rec("src/util/format.ts", util, "StaffApp", am));

        Assert.True(HasEdge(g, "file:StaffApp:src/App.vue", "file:UiComponents:src/main.ts", "imports"));
        Assert.True(HasEdge(g, "file:StaffApp:src/App.vue", "file:StaffApp:src/util/format.ts", "imports"));
        Assert.True(HasEdge(g, "file:StaffApp:src/App.vue", "ext:axios", "imports"));
        Assert.True(HasEdge(g, "sym:StaffApp:src/App.vue#App", "sym:UiComponents:src/components/Panel.vue#Panel", "uses_component"));
        Assert.True(HasEdge(g, "sym:StaffApp:src/App.vue#App", "sym:StaffApp:src/util/format.ts#fmt", "calls"));
    }

    [Fact]
    public void Library_outputs_become_project_dependencies_and_rollups()
    {
        var p1 = Project("Sales", "exe", "frmMain.frm");
        p1.Project!.Libs.Add(new LibRef { Kind = "ocx", Name = "AcmeGrid", File = "AcmeGrid.ocx" });
        p1.Project.Libs.Add(new LibRef { Kind = "com", Name = "Microsoft ActiveX Data Objects", File = "msado15.dll" });
        var p2 = Project("AcmeGrid", "ocx", "Grid.ctl");
        var form = Vb(syms: [Sym("frmMain", "form"), Sym("Form_Load", "sub", "frmMain")]);
        form.Refs.Add(new Ref { Kind = "new", Name = "Grid", From = "Form_Load", FromParent = "frmMain" });
        var grid = Vb(syms: Sym("Grid", "usercontrol"));

        var g = Build(Cfg("Shop"),
            Rec("Sales/Sales.vbp", p1), Rec("Sales/frmMain.frm", form), Rec("Grid/AcmeGrid.vbp", p2), Rec("Grid/Grid.ctl", grid));

        Assert.True(HasEdge(g, "proj:Shop:Sales/Sales.vbp", "proj:Shop:Grid/AcmeGrid.vbp", "depends_on"));
        Assert.True(HasEdge(g, "proj:Shop:Sales/Sales.vbp", "ext:microsoft activex data objects", "uses_library"));
        Assert.True(HasEdge(g, "sym:Shop:Sales/frmMain.frm#frmMain.Form_Load", "sym:Shop:Grid/Grid.ctl#Grid", "instantiates"));
        Assert.True(HasEdge(g, "proj:Shop:Sales/Sales.vbp", "proj:Shop:Grid/AcmeGrid.vbp", "uses_code_of"));
    }

    [Fact]
    public void Duplicate_display_names_get_their_folder()
    {
        var g = Build(Cfg("Shop"), Rec("A/Tool.vbp", Project("Tool", "exe")), Rec("B/Tool.vbp", Project("Tool", "dll")));
        Assert.Equal("Tool (A)", g.GetNode("proj:Shop:A/Tool.vbp")!.Name);
        Assert.Equal("Tool (B)", g.GetNode("proj:Shop:B/Tool.vbp")!.Name);
    }
}
