using CodeGraph.Build;
using CodeGraph.Model;
using CodeGraph.Output;
using CodeGraph.Scan;

namespace CodeGraph.Tests.EndToEnd;

/// <summary>
/// Runs the real pipeline (discover, parse, link, report, vault) over the invented repos in tests/Samples/e2e.
/// </summary>
public sealed class SampleCodebaseTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "cg-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly Config _cfg;
    private readonly Graph _g;
    private readonly string _report;
    private readonly List<ParseError> _errors;

    public SampleCodebaseTests()
    {
        string samples = Path.Combine(AppContext.BaseDirectory, "Samples", "e2e");
        _cfg = new Config
        {
            Output = Path.Combine(_tmp, "out"),
            Vault = Path.Combine(_tmp, "vault"),
            Repos = new[] { "Shop", "Api", "Web" }.Select(n => new RepoConfig { Name = n, Path = Path.Combine(samples, n) }).ToList(),
            Extractors = [ExtractorTests.Reports("name", "uses_file")],
        };
        var files = Scanner.Discover(_cfg);
        var (records, errors) = Scanner.ParseAll(_cfg, files, verbose: false);
        _errors = errors;
        var (g, stats, unresolved) = Builder.BuildGraph(_cfg, records);
        _g = g;
        _report = Report.BuildReport(g, stats, unresolved, errors, new List<string>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private bool Has(string s, string t, string rel) => _g.GetEdge(s, t, rel) is not null;

    private void AssertEdge(string s, string t, string rel)
    {
        Assert.True(_g.HasNode(s), $"missing node {s}");
        Assert.True(_g.HasNode(t), $"missing node {t}");
        Assert.True(Has(s, t, rel), $"missing edge {s} -{rel}-> {t}");
    }

    private const string Vbp = "proj:Shop:OrderEntry/OrderEntry.vbp";
    private const string Frm = "file:Shop:OrderEntry/frmOrder.frm";
    private const string Bas = "file:Shop:Shared/modDb.bas";

    [Fact]
    public void Discovers_all_sample_files_without_parse_errors()
    {
        Assert.Empty(_errors);
        foreach (var id in new[] { Vbp, Frm, Bas, "file:Shop:Database/Tables/dbo.Orders.sql",
                     "file:Api:Shop.Api/Controllers/OrdersController.cs", "file:Web:src/api.ts",
                     "file:Web:src/components/OrderList.vue", "file:Shop:Package/Shop.wxs" })
            Assert.True(_g.HasNode(id), id);
    }

    [Fact]
    public void Vb6_project_contains_members_including_shared_file()
    {
        AssertEdge(Vbp, Frm, "contains");
        AssertEdge(Vbp, Bas, "contains");
        Assert.Null(_g.GetNode(Vbp)!["missing_members"]);
    }

    [Fact]
    public void Form_calls_module_routines_across_directories()
    {
        const string click = "sym:Shop:OrderEntry/frmOrder.frm#frmOrder.cmdPlace_Click";
        AssertEdge(click, "sym:Shop:Shared/modDb.bas#modDb.OpenRecordset", "calls");
        AssertEdge(click, "sym:Shop:Shared/modDb.bas#modDb.ExecuteSql", "calls");
    }

    [Fact]
    public void Form_inline_sql_reads_and_executes_db_objects()
    {
        const string click = "sym:Shop:OrderEntry/frmOrder.frm#frmOrder.cmdPlace_Click";
        AssertEdge(click, "db:orders", "reads");
        AssertEdge(click, "db:sp_order_place", "executes");
    }

    [Fact]
    public void Procedure_writes_orders_reads_customers_and_foreign_key_references()
    {
        AssertEdge("db:sp_order_place", "db:orders", "writes");
        AssertEdge("db:sp_order_place", "db:customers", "reads");
        AssertEdge("db:orders", "db:customers", "references");
        Assert.Equal("db_table", _g.GetNode("db:orders")!.Kind);
        Assert.Equal("db_proc", _g.GetNode("db:sp_order_place")!.Kind);
    }

    [Fact]
    public void Controller_route_interface_and_implementation_are_linked()
    {
        const string ctl = "sym:Api:Shop.Api/Controllers/OrdersController.cs#OrdersController";
        const string get = ctl + ".GetOrder";
        const string iface = "sym:Api:Shop.Api/Repositories/IOrderRepository.cs#IOrderRepository.GetById";
        const string impl = "sym:Api:Shop.Api/Repositories/OrderRepository.cs#OrderRepository.GetById";
        AssertEdge(get, iface, "calls");
        AssertEdge(get, impl, "calls");
        AssertEdge(impl, "db:orders", "reads");
        AssertEdge("sym:Api:Shop.Api/Repositories/OrderRepository.cs#OrderRepository.Place", "db:sp_order_place", "executes");
        AssertEdge("proj:Api:Shop.Api/Shop.Api.csproj", "file:Api:Shop.Api/Controllers/OrdersController.cs", "contains");
    }

    [Fact]
    public void Frontend_template_string_call_reaches_the_controller_action()
    {
        // fetch(`/orders/${id}`) in getOrder -> GET /orders/{id}
        AssertEdge("sym:Web:src/api.ts#getOrder",
            "sym:Api:Shop.Api/Controllers/OrdersController.cs#OrdersController.GetOrder", "calls_api");
        AssertEdge("proj:Web:package.json", "file:Web:src/api.ts", "contains");
        // not asserted (known gap): api.get('/orders/' + id) in loadOrder - URLs built by concatenation are not matched
    }

    [Fact]
    public void Vue_component_imports_the_api_module()
    {
        AssertEdge("file:Web:src/components/OrderList.vue", "file:Web:src/api.ts", "imports");
    }

    [Fact]
    public void Installer_ships_the_vb6_project()
    {
        var shipped = _g.Edges.Where(e => e.Relation == "ships" && e.Target == Vbp).ToList();
        Assert.Single(shipped);
    }

    [Fact]
    public void Report_mentions_the_main_findings()
    {
        Assert.StartsWith("# Codegraph Report", _report);
        Assert.Contains("Orders", _report);
        Assert.Contains("OrderEntry", _report);
        Assert.Contains("Shop.Api", _report);
        Assert.Contains("shop-web", _report);
        Assert.Contains("## Most used database objects", _report);
        Assert.Contains("| sp_Order_Place | proc |", _report);
        Assert.Contains("| Orders | table |", _report);
        Assert.Contains("| calls_api | 1 |", _report);
        Assert.Contains("| ships | 1 |", _report);
    }

    [Fact]
    public void Report_definition_extractor_links_report_parameters_sql_and_the_screen_that_opens_it()
    {
        const string rep = "item:report:order summary";
        Assert.Equal("report", _g.GetNode(rep)!.Kind);
        Assert.NotNull(_g.GetEdge(rep, "file:Shop:Reports/Order Summary.xml", "defined_in"));
        Assert.NotNull(_g.GetEdge(rep, "fileref:order summary.rpt", "uses_file"));
        Assert.NotNull(_g.GetEdge(rep, "item:parameter:customerid", "has_parameter"));
        Assert.NotNull(_g.GetEdge(rep, "db:sp_rep_ordersummary", "defines"));
        Assert.NotNull(_g.GetEdge("db:sp_rep_ordersummary", "db:orders", "reads"));
        Assert.NotNull(_g.GetEdge("db:sp_rep_ordersummary", "db:customers", "reads"));
        Assert.NotNull(_g.GetEdge("sym:Shop:OrderEntry/frmOrder.frm#frmOrder.cmdReport_Click", rep, "opens"));
        Assert.Contains("## Your own file formats", _report);
    }

    [Fact]
    public void Enum_values_and_constants_are_linked_from_the_form()
    {
        const string ship = "sym:Shop:OrderEntry/frmOrder.frm#frmOrder.cmdShip_Click";
        Assert.NotNull(_g.GetEdge(ship, "sym:Shop:Shared/modDb.bas#OrderStatus.osShipped", "uses_value"));
        Assert.NotNull(_g.GetEdge(ship, "sym:Shop:Shared/modDb.bas#modDb.MAX_ORDER_LINES", "uses_value"));
        Assert.Equal("2", _g.GetNode("sym:Shop:Shared/modDb.bas#OrderStatus.osShipped")!.Str("value"));
    }

    [Fact]
    public void Vault_has_notes_and_second_run_writes_nothing()
    {
        var first = Obsidian.WriteVault(_g, _cfg, _report);
        Assert.True(first.Written > 0);
        string vault = _cfg.Vault!;
        Assert.True(File.Exists(Path.Combine(vault, "Home.md")));
        Assert.True(File.Exists(Path.Combine(vault, "Projects", "OrderEntry.md")));
        Assert.True(File.Exists(Path.Combine(vault, "Database", "Tables", "Orders.md")));
        Assert.Contains("Customers", File.ReadAllText(Path.Combine(vault, "Database", "Tables", "Orders.md")));

        var second = Obsidian.WriteVault(_g, _cfg, _report);
        Assert.Equal(0, second.Written);
        Assert.Equal(0, second.Deleted);
    }
}
