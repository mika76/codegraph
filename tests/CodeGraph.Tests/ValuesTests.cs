using CodeGraph.Build;
using CodeGraph.Model;
using CodeGraph.Parsers;
using CodeGraph.Scan;

namespace CodeGraph.Tests;

public class ValuesTests
{
    private const string VbModule = """
        Attribute VB_Name = "modConsts"
        Option Explicit
        Public Const MAX_ITEMS As Integer = 50, MIN_ITEMS = 1 ' limits
        Private Const SQL_OPEN = "SELECT * FROM Orders WHERE Status = 1"
        Public Enum OrderStatus
            osPlaced = 1
            osShipped
            osCancelled = 9
        End Enum

        Public Function OpenOrders() As Long
            OpenOrders = Count(SQL_OPEN)
        End Function
        """;

    [Fact]
    public void Vb6_enum_members_constants_and_values()
    {
        var f = Vb6Parser.Parse("Shared/modConsts.bas", VbModule);
        var e = Assert.Single(f.Symbols, s => s.Kind == "enum");
        Assert.Equal("OrderStatus", e.Name);
        var members = f.Symbols.Where(s => s.Kind == "enum_member").Select(s => $"{s.Parent}.{s.Name}={s.Extra!["value"]}").ToList();
        Assert.Equal(["OrderStatus.osPlaced=1", "OrderStatus.osShipped=2", "OrderStatus.osCancelled=9"], members);
        var consts = f.Symbols.Where(s => s.Kind == "constant").Select(s => $"{s.Visibility} {s.Name}={s.Extra!["value"]}").ToList();
        Assert.Equal(["public MAX_ITEMS=50", "public MIN_ITEMS=1", "private SQL_OPEN=\"SELECT * FROM Orders WHERE Status = 1\""], consts);
        // the SQL in the constant is the constant's, not the module's
        Assert.Contains(f.Refs, r => r.Kind == "sql" && r.Name == "orders" && r.From == "SQL_OPEN" && r.FromParent == "modConsts");
    }

    [Fact]
    public void CSharp_enum_members_and_const_fields()
    {
        const string cs = """
            namespace Shop {
              public enum OrderStatus { Placed = 1, [Obsolete] Shipped, Cancelled = 9 }
              public static class Limits {
                public const int MaxItems = 50;
                private const string OpenSql = "SELECT * FROM Orders";
                public static int Twice() { const int local = 2; return MaxItems * local; }
              }
            }
            """;
        var f = CSharpParser.Parse("Limits.cs", cs);
        var members = f.Symbols.Where(s => s.Kind == "enum_member").Select(s => $"{s.Name}={s.Extra!["value"]}").ToList();
        Assert.Equal(["Placed=1", "Shipped=2", "Cancelled=9"], members);
        var consts = f.Symbols.Where(s => s.Kind == "constant").Select(s => $"{s.Parent}.{s.Name}={s.Extra!["value"]}").ToList();
        Assert.Equal(["Limits.MaxItems=50", "Limits.OpenSql=\"SELECT * FROM Orders\""], consts);   // the method-local const is not one
        Assert.Contains(f.Refs, r => r.Kind == "sql" && r.Name == "orders" && r.From == "OpenSql");
    }

    [Fact]
    public void TypeScript_enum_members()
    {
        var f = JavaScriptParser.Parse("src/status.ts", "export enum OrderStatus { Placed = 1, Shipped, Label = 'x' }");
        Assert.Equal(["Placed=1", "Shipped=2", "Label='x'"],
            f.Symbols.Where(s => s.Kind == "enum_member").Select(s => $"{s.Name}={s.Extra!["value"]}"));
    }

    [Fact]
    public void Builder_links_uses_of_values_across_files()
    {
        const string form = """
            Attribute VB_Name = "frmOrder"
            Private Sub cmdShip_Click()
                If Status = osShipped Then MsgBox MAX_ITEMS
                Status = OrderStatus.osCancelled
                n = OpenOrders()
            End Sub
            """;
        var cfg = new Config { Repos = [new RepoConfig { Name = "Shop", Path = "C:/r/Shop" }] };
        FileRecord Rec(string rel, FileFacts f) => new("Shop", "C:/r/Shop", rel, $"C:/r/Shop/{rel}", f);
        var table = new FileFacts { Lang = "sql", Symbols = [new Symbol { Name = "Orders", Kind = "db_table", Line = 1 }] };
        table.Symbols[0].X["key"] = "orders";
        var proj = new FileFacts
        {
            Lang = "project",
            Project = new ProjectInfo { Format = "vbp", Lang = "vb6", Name = "Shop", Type = "exe", Output = "Shop.exe", Members = ["frmOrder.frm", "modConsts.bas"] },
        };
        var g = Builder.BuildGraph(cfg, [
            Rec("App/Shop.vbp", proj),
            Rec("App/frmOrder.frm", Vb6Parser.Parse("App/frmOrder.frm", form)),
            Rec("App/modConsts.bas", Vb6Parser.Parse("App/modConsts.bas", VbModule)),
            Rec("Database/dbo.Orders.sql", table),
        ]).Graph;

        const string click = "sym:Shop:App/frmOrder.frm#frmOrder.cmdShip_Click";
        Assert.NotNull(g.GetEdge(click, "sym:Shop:App/modConsts.bas#OrderStatus.osShipped", "uses_value"));     // bare VB6 name
        Assert.NotNull(g.GetEdge(click, "sym:Shop:App/modConsts.bas#OrderStatus.osCancelled", "uses_value"));   // Enum.Member
        Assert.NotNull(g.GetEdge(click, "sym:Shop:App/modConsts.bas#modConsts.MAX_ITEMS", "uses_value"));       // public module const
        Assert.Equal("2", g.GetNode("sym:Shop:App/modConsts.bas#OrderStatus.osShipped")!.Str("value"));
        // SQL kept in a constant: constant -> table, routine -> constant
        Assert.NotNull(g.GetEdge("sym:Shop:App/modConsts.bas#modConsts.SQL_OPEN", "db:orders", "reads"));
        Assert.NotNull(g.GetEdge("sym:Shop:App/modConsts.bas#modConsts.OpenOrders", "sym:Shop:App/modConsts.bas#modConsts.SQL_OPEN", "uses_value"));
    }

    [Fact]
    public void Ambiguous_bare_values_are_skipped()
    {
        var cfg = new Config { Repos = [new RepoConfig { Name = "Shop", Path = "C:/r/Shop" }] };
        FileRecord Rec(string rel, string text) => new("Shop", "C:/r/Shop", rel, $"C:/r/Shop/{rel}", Vb6Parser.Parse(rel, text));
        var g = Builder.BuildGraph(cfg, [
            Rec("A/modA.bas", "Attribute VB_Name = \"modA\"\nPublic Const LIMIT = 1\n"),
            Rec("B/modB.bas", "Attribute VB_Name = \"modB\"\nPublic Const LIMIT = 2\n"),
            Rec("C/modC.bas", "Attribute VB_Name = \"modC\"\nPublic Sub Go()\n x = LIMIT\nEnd Sub\n"),
        ]).Graph;
        Assert.DoesNotContain(g.Edges, e => e.Relation == "uses_value");
    }
}
