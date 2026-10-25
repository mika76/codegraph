using CodeGraph.Parsers;

namespace CodeGraph.Tests.Parsers;

public class Vb6ParserTests
{
    private const string Frm = """
VERSION 5.00
Begin VB.Form frmMain
   Caption         =   "Main"
   Begin VB.CommandButton cmdOk
      Caption         =   "OK"
   End
   Begin MSComctlLib.ListView lvwItems
   End
End
Attribute VB_Name = "frmMain"
Attribute VB_Exposed = False
Option Explicit
Private Declare Function GetTickCount Lib "kernel32" Alias "GetTickCountEx" () As Long
Private m_Db As clsDatabase
Public Gx As Long
Private Sub cmdOk_Click()
    Dim rs As ADODB.Recordset
    Dim obj As New COrder
    Set rs = New ADODB.Recordset
    rs.Open "SELECT * FROM Orders WHERE id = 1", conn
    rs.Update
    obj.Save
    m_Db.Run "bill___Customer_Get"
    With obj
        .Name = "x" ' comment Foo.Bar
    End With
    Me.Caption = "hi"
End Sub

Public Property Get Value() As Long
End Property
Public Property Let Value(ByVal v As Long)
End Property

Public Function Calc(ByVal a As Long, _
                     b As COrder) As Long
    Calc = b.Total
End Function
Public Event Changed(x As Long)
""";

    [Fact]
    public void Form_component_controls_and_routines()
    {
        var f = Vb6Parser.Parse("App/frmMain.frm", Frm);
        Assert.Equal("vb6", f.Lang);
        var comp = f.Symbols[0];
        Assert.Equal(("frmMain", "form"), (comp.Name, comp.Kind));
        Assert.Equal(2, (int)comp.X["controls"]!);
        Assert.Equal(["MSComctlLib.ListView", "VB.CommandButton"], comp.X["control_types"]!.AsArray().Select(x => (string)x!));

        var click = f.Symbols.Single(s => s.Name == "cmdOk_Click");
        Assert.Equal("event_handler", click.Kind);
        Assert.Equal("frmMain", click.Parent);
        Assert.Equal("private", click.Visibility);

        var prop = f.Symbols.Single(s => s.Name == "Value");
        Assert.Equal("property", prop.Kind);
        Assert.Equal(2, f.Symbols.Count(s => s.Name == "Value") + 1);
        Assert.Equal(4, prop.Lines);

        var calc = f.Symbols.Single(s => s.Name == "Calc");
        Assert.Equal("function", calc.Kind);
        Assert.Contains("b As COrder", calc.Signature);
        Assert.Equal(("event", "frmMain"), (f.Symbols.Single(s => s.Name == "Changed").Kind, "frmMain"));
        Assert.Equal("api_declare", f.Symbols.Single(s => s.Name == "GetTickCount").Kind);
    }

    [Fact]
    public void Refs_native_new_type_member_sql()
    {
        var f = Vb6Parser.Parse("App/frmMain.frm", Frm);
        var native = f.Refs.Single(r => r.Kind == "native");
        Assert.Equal("GetTickCountEx", native.Name);
        Assert.Equal("kernel32", (string)native.X["lib"]!);

        Assert.Contains(f.Refs, r => r is { Kind: "new", Name: "COrder", From: "cmdOk_Click" });
        Assert.Contains(f.Refs, r => r is { Kind: "type", Name: "clsDatabase", From: null });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "obj", QualType: "COrder", Name: "Save" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "m_Db", QualType: "clsDatabase", Name: "Run" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "obj", Name: "Name" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "Me", QualType: "frmMain", Name: "Caption" });
        Assert.DoesNotContain(f.Refs, r => r.Name == "Bar");

        Assert.Contains(f.Refs, r => r is { Kind: "sql", Mode: "read", Name: "orders" } && r.Via is null);
        Assert.Contains(f.Refs, r => r is { Kind: "sql", Mode: "write", Name: "orders", Via: "recordset" });
        Assert.Contains(f.Refs, r => r is { Kind: "literal", Name: "bill___customer_get", From: "cmdOk_Click" });
    }

    [Fact]
    public void Module_globals_and_vb_name()
    {
        const string bas = "Attribute VB_Name = \"modUtils\"\nPublic gCount As Long\nPrivate p As Long\nPublic Sub Go()\nEnd Sub\n";
        var f = Vb6Parser.Parse("Shared/x.bas", bas);
        Assert.Equal("modUtils", f.Symbols[0].Name);
        Assert.Equal("module", f.Symbols[0].Kind);
        var g = Assert.Single(f.X["globals"]!.AsArray());
        Assert.Equal("gcount", (string)g!["name"]!);
        Assert.Equal("modUtils", (string)f.X["component"]!);
        Assert.Equal(6, f.Loc);
    }

    [Fact]
    public void Vbnet_types_and_inherits()
    {
        const string vb = "Public Class Foo\n  Inherits Bar\n  Public Sub Run()\n  End Sub\nEnd Class\nStructure S\nEnd Structure\n";
        var f = Vb6Parser.Parse("a/Foo.vb", vb);
        Assert.Equal("vbnet", f.Lang);
        Assert.Equal(["Foo", "S"], f.Symbols.Where(s => s.Parent is null && s.Kind != "sub").Select(s => s.Name));
        Assert.Equal("struct", f.Symbols.Single(s => s.Name == "S").Kind);
        Assert.Equal("Foo", f.Symbols.Single(s => s.Name == "Run").Parent);
        Assert.Contains(f.Refs, r => r is { Kind: "inherit", From: "Foo", Name: "Bar" });
    }

    [Fact]
    public void Vbnet_routines_after_region_are_found()
    {
        const string vb = "Public Class Foo\n#Region \"Today's revenue\"\n  Public Function GetToday() As Decimal\n  End Function\n#End Region\n" +
                          "#Region \"Old\"\n  Private Sub Old()\n  End Sub\n#End Region\nEnd Class\n";
        var f = Vb6Parser.Parse("a/Foo.vb", vb);
        Assert.Equal(["GetToday", "Old"], f.Symbols.Where(s => s.Parent == "Foo").Select(s => s.Name));
    }
}
