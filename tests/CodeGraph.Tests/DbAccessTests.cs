using CodeGraph.Model;
using CodeGraph.Scan;

namespace CodeGraph.Tests;

/// <summary>
/// A bare string naming a procedure is only a call where the code around it talks to a database. Detection must
/// work for any language the tool reads and any naming style - nothing about the procedure names themselves.
/// </summary>
public class DbAccessTests
{
    private static FileFacts Parse(string file, string text)
    {
        var facts = ParserRegistry.For(file)!(file, text);
        DbAccess.Mark(file, text, facts);
        return facts;
    }

    private static bool Marked(FileFacts f, string name) =>
        f.Refs.Any(r => r.Kind == "literal" && r.Name == name && r.Extra?["db"] is not null);

    private static bool Unmarked(FileFacts f, string name) =>
        f.Refs.Any(r => r.Kind == "literal" && r.Name == name && r.Extra?["db"] is null);

    [Fact]
    public void CSharp_marks_only_the_method_that_uses_ado_net()
    {
        var f = Parse("Repo.cs", """
            class Repo
            {
                void Load()
                {
                    using var cmd = new SqlCommand("fetchall", conn);
                    cmd.CommandType = CommandType.StoredProcedure;
                }
                string Label() { return "test"; }
                void Run(ICommand c) { c.Execute("cleanup"); }
            }
            """);
        Assert.True(Marked(f, "fetchall"));
        Assert.True(Unmarked(f, "test"));
        Assert.True(Unmarked(f, "cleanup")); // ICommand.Execute is not database access in C#
    }

    [Fact]
    public void Entity_framework_function_imports_are_database_access()
    {
        var f = Parse("Model.Context.cs", """
            public partial class Entities : DbContext
            {
                public virtual ObjectResult<Rate_Result> Rate_List()
                {
                    return ((IObjectContextAdapter)this).ObjectContext.ExecuteFunction<Rate_Result>("Rate_List");
                }
                public int Purge() => Database.ExecuteSqlCommand("cleanup");
            }
            """);
        Assert.True(Marked(f, "rate_list"));
        Assert.True(Marked(f, "cleanup"));
    }

    [Fact]
    public void Vb6_marks_ado_execute_and_recordsets()
    {
        var f = Parse("modData.bas", """
            Attribute VB_Name = "modData"
            Public Sub Purge()
                cn.Execute "cleanup"
            End Sub
            Public Sub Show()
                MsgBox "test"
            End Sub
            Public Function Orders() As ADODB.Recordset
                Set Orders = cn.Execute("ORDERS_GET")
            End Function
            """);
        Assert.True(Marked(f, "cleanup"));
        Assert.True(Marked(f, "orders_get"));
        Assert.True(Unmarked(f, "test"));
    }

    [Fact]
    public void TypeScript_marks_node_drivers_but_not_ui_code()
    {
        var api = Parse("orders.ts", """
            import sql from 'mssql';
            export async function load(pool: any) {
              return pool.request().input('id', 1).execute('get_orders');
            }
            """);
        Assert.True(Marked(api, "get_orders"));
        var ui = Parse("types.ts", "export const kinds = ['test', 'cleanup'];\n");
        Assert.True(Unmarked(ui, "test"));
        Assert.True(Unmarked(ui, "cleanup"));
    }

    [Fact]
    public void PowerShell_and_cpp_mark_their_database_apis()
    {
        var ps = Parse("deploy.ps1", """
            function Reset-Data {
                $cmd = $conn.CreateCommand()
                $cmd.CommandText = "Get2Orders"
            }
            """);
        Assert.True(Marked(ps, "get2orders"));
        var cpp = Parse("db.cpp", """
            void Load(_CommandPtr cmd) {
                cmd->CommandText = "fetchall";
            }
            void Title() { SetWindowText(hwnd, "test"); }
            """);
        Assert.True(Marked(cpp, "fetchall"));
        Assert.True(Unmarked(cpp, "test"));
    }

    [Fact]
    public void Strings_that_read_a_column_are_marked_as_columns()
    {
        var vb = Parse("modCount.bas", """
            Attribute VB_Name = "modCount"
            Public Sub Load(data As ADODB.Recordset)
                If data.Fields("BILL_Count_denom").value = 1 Then x = data.Fields.Item("bill_qty")
                cn.Execute "bill_refresh"
            End Sub
            """);
        Assert.True(vb.Refs.Single(r => r.Name == "bill_count_denom").X.ContainsKey("column"));
        Assert.True(vb.Refs.Single(r => r.Name == "bill_qty").X.ContainsKey("column"));
        Assert.False(vb.Refs.Single(r => r.Name == "bill_refresh").X.ContainsKey("column"));
        var cs = Parse("Repo.cs", """
            class Repo
            {
                void Read(DataRow row, IDataReader rd)
                {
                    var a = row["bill_total"];
                    var b = rd.GetOrdinal("bill_points");
                    var c = row.Field<int>("bill_qty");
                    var cmd = new SqlCommand("bill_refresh");
                }
            }
            """);
        foreach (var col in new[] { "bill_total", "bill_points", "bill_qty" })
            Assert.True(cs.Refs.Single(r => r.Name == col).X.ContainsKey("column"));
        Assert.False(cs.Refs.Single(r => r.Name == "bill_refresh").X.ContainsKey("column"));
    }

    [Fact]
    public void A_column_assigned_through_an_accessor_is_written()
    {
        var vb = Parse("frmEdit.frm", """
            Attribute VB_Name = "frmEdit"
            Private Sub Save(data As ADODB.Recordset)
                data.Fields("bill_value").value = CCur(txtValue)
                If data.Fields("bill_count").value = 1 Then x = data.Fields("bill_qty")
                With data
                    .Fields("bill_note") = txtNote
                End With
                data.Update
            End Sub
            """);
        string? Mode(FileFacts f, string name) => f.Refs.Single(r => r.Name == name && r.Kind == "literal").Mode;
        Assert.Equal("write", Mode(vb, "bill_value"));
        Assert.Equal("write", Mode(vb, "bill_note"));
        Assert.Null(Mode(vb, "bill_count"));  // compared in an If
        Assert.Null(Mode(vb, "bill_qty"));    // read into x
        var cs = Parse("Repo.cs", """
            class Repo
            {
                void Fill(DataRow row)
                {
                    row["bill_total"] = 5;
                    if (row["bill_points"] == null) return;
                    var q = row["bill_qty"];
                }
            }
            """);
        Assert.Equal("write", Mode(cs, "bill_total"));
        Assert.Null(Mode(cs, "bill_points"));
        Assert.Null(Mode(cs, "bill_qty"));
        Assert.True(cs.Refs.Single(r => r.Name == "bill_total").X.ContainsKey("column"));
    }

    [Fact]
    public void A_string_passed_with_a_row_to_your_own_helper_is_a_column()
    {
        var cs = Parse("Item.cs", """
            class Item
            {
                Item(DataRow r)
                {
                    Points = Read<decimal>(r, "bill_points");
                    Other = Run(conn, "bill_refresh");
                }
                T Read<T>(DataRow r, string name) => (T)r[name];
            }
            """);
        Assert.True(cs.Refs.Single(r => r.Name == "bill_points").X.ContainsKey("column"));
        Assert.False(cs.Refs.Single(r => r.Name == "bill_refresh").X.ContainsKey("column")); // conn is no row
        var vb = Parse("modRead.bas", """
            Attribute VB_Name = "modRead"
            Public Function Total(rs As ADODB.Recordset) As Double
                Total = NullToZero(rs, "bill_total")
            End Function
            """);
        Assert.True(vb.Refs.Single(r => r.Name == "bill_total").X.ContainsKey("column"));
    }

    [Fact]
    public void Comments_and_strings_do_not_count_as_database_access()
    {
        var f = Parse("Notes.cs", """
            class Notes
            {
                // uses SqlCommand somewhere else
                string A() { return "test"; }
                string B() { return "see ExecuteReader docs"; }
            }
            """);
        Assert.True(Unmarked(f, "test"));
    }
}
