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
        Assert.Equal("No connection found.\n", Run(w => GraphQuery.Path(g, "Unused", "frmMain.Save", w)));
    }
}
