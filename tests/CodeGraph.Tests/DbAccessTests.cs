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
