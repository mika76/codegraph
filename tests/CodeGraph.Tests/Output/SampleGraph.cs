using System.Text.Json.Nodes;
using CodeGraph.Model;
using G = CodeGraph.Model.Graph;

namespace CodeGraph.Tests.Output;

/// <summary>A small hand-built graph in the shape the builder produces.</summary>
internal static class SampleGraph
{
    public const string Proj = "proj:Shop:Sales/Sales.vbp";
    public const string FMain = "file:Shop:Sales/frmMain.frm";
    public const string FDb = "file:Shop:Sales/modDb.bas";
    public const string FOrphan = "file:Shop:Sales/Old.bas";
    public const string TForm = "sym:Shop:Sales/frmMain.frm#frmMain";
    public const string Save = "sym:Shop:Sales/frmMain.frm#frmMain.Save";
    public const string TMod = "sym:Shop:Sales/modDb.bas#modDb";
    public const string Load = "sym:Shop:Sales/modDb.bas#modDb.Load";

    public static G Create()
    {
        var g = new G();
        g.Node("repo:Shop", ("kind", "repo"), ("name", "Shop"), ("path", "C:/x/Shop"), ("branch", "R2.4.0"));
        g.Node("area:Shop:Sales", ("kind", "area"), ("name", "Sales"), ("repo", "Shop"));
        g.Node(Proj, ("kind", "project"), ("name", "Order Entry"), ("repo", "Shop"), ("area", "Sales"), ("ptype", "exe"),
            ("lang", "vb6"), ("version", "1.2.3"), ("output", "OrderEntry.exe"),
            ("missing_members", new JsonArray("Gone.bas")));
        g.Node(FMain, ("kind", "file"), ("name", "frmMain.frm"), ("path", "Sales/frmMain.frm"), ("repo", "Shop"),
            ("area", "Sales"), ("lang", "vb6"), ("loc", 100), ("projects", new JsonArray(Proj)));
        g.Node(FDb, ("kind", "file"), ("name", "modDb.bas"), ("path", "Sales/modDb.bas"), ("repo", "Shop"),
            ("area", "Sales"), ("lang", "vb6"), ("loc", 50), ("projects", new JsonArray(Proj)));
        g.Node(FOrphan, ("kind", "file"), ("name", "Old.bas"), ("path", "Sales/Old.bas"), ("repo", "Shop"),
            ("area", "Sales"), ("lang", "vb6"), ("loc", 7), ("orphan", true));
        g.Node(TForm, ("kind", "form"), ("name", "frmMain"), ("file", FMain), ("line", 1));
        g.Node(Save, ("kind", "sub"), ("name", "Save"), ("qual", "frmMain.Save"), ("file", FMain), ("line", 10),
            ("lines", 5), ("signature", "Private Sub Save()"));
        g.Node(TMod, ("kind", "module"), ("name", "modDb"), ("file", FDb), ("line", 1));
        g.Node(Load, ("kind", "function"), ("name", "Load"), ("qual", "modDb.Load"), ("file", FDb), ("line", 3));
        g.Node("db:Orders", ("kind", "db_table"), ("name", "Orders"),
            ("columns", new JsonArray("Id int not null", "Status int")));
        g.Node("db:usp_Save", ("kind", "db_proc"), ("name", "usp_Save"));
        g.Node("db:Unused", ("kind", "db_table"), ("name", "Unused"));
        g.AddEdge(Proj, FMain, "contains");
        g.AddEdge(Proj, FDb, "contains");
        g.AddEdge(FMain, TForm, "contains");
        g.AddEdge(TForm, Save, "contains");
        g.AddEdge(FDb, TMod, "contains");
        g.AddEdge(TMod, Load, "contains");
        g.AddEdge(Save, Load, "calls");
        g.AddEdge(Save, "db:Orders", "writes", count: 2);
        g.AddEdge(Load, "db:usp_Save", "executes");
        g.AddEdge("db:usp_Save", "db:Orders", "writes");
        return g;
    }
}
