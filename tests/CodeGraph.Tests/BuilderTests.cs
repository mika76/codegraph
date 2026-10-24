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
