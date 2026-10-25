using CodeGraph.Parsers;

namespace CodeGraph.Tests.Parsers;

public class SqlParserTests
{
    [Fact]
    public void ObjectKey_DropsDboAndDatabase()
    {
        Assert.Equal("orders", SqlParser.ObjectKey("[dbo].[Orders]"));
        Assert.Equal("orders", SqlParser.ObjectKey("Acme.dbo.Orders"));
        Assert.Equal("rpt.orders", SqlParser.ObjectKey("Acme.rpt.Orders"));
    }

    [Fact]
    public void Table_ColumnsAndForeignKeys()
    {
        const string sql = """
            CREATE TABLE [dbo].[Orders]
            (
            [OrderId] [int] NOT NULL,
            [Amount] [decimal] (18, 2) NULL,
            CONSTRAINT [PK_T] PRIMARY KEY CLUSTERED ([OrderId]),
            CONSTRAINT [FK_T_M] FOREIGN KEY ([ItemId]) REFERENCES [dbo].[Items] ([ItemId])
            )
            GO
            ALTER TABLE [dbo].[Orders] ADD CONSTRAINT [FK_x] FOREIGN KEY ([A]) REFERENCES [dbo].[Other] ([A])
            """;
        var f = SqlParser.Parse("Tables/dbo.Orders.sql", sql);
        var s = Assert.Single(f.Symbols);
        Assert.Equal("Orders", s.Name);
        Assert.Equal("db_table", s.Kind);
        Assert.Equal("orders", s.X["key"]!.GetValue<string>());
        var cols = s.X["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Equal("OrderId int", cols[0]);
        Assert.Equal("Amount decimal(18, 2)", cols[1].Replace("decimal (", "decimal("));
        Assert.DoesNotContain(cols, c => c.StartsWith("CONSTRAINT"));
        var fks = f.Refs.Where(r => r.Kind == "fk").ToList();
        Assert.Equal(2, fks.Count);
        Assert.All(fks, r => Assert.Equal("orders", r.From));
        Assert.Contains(fks, r => r.Name == "items");
        Assert.Contains(fks, r => r.Name == "other");
        Assert.DoesNotContain(f.Refs, r => r.Mode == "alter");
        Assert.DoesNotContain(f.Refs, r => r.Extra?.ContainsKey("owner_hint") == true);
    }

    [Fact]
    public void A_definition_ends_at_go_and_the_rest_belongs_to_the_script()
    {
        const string patch = """
            IF OBJECT_ID('dbo.usp_Close') IS NOT NULL DROP PROCEDURE dbo.usp_Close
            GO
            CREATE PROCEDURE dbo.usp_Close @id int AS
              UPDATE dbo.Orders SET Status = 2 WHERE Id = @id
            GO
            UPDATE dbo.Patch_Level SET Patch_Date = GETDATE()
            go
            CREATE VIEW dbo.v_Open AS SELECT Id FROM dbo.Orders WHERE Status = 1
            """;
        var f = SqlParser.Parse("Patches/Patch12.sql", patch);
        Assert.Contains(f.Refs, r => r is { Kind: "sql", From: "usp_close", Mode: "write", Name: "orders" });
        Assert.DoesNotContain(f.Refs, r => r.From == "usp_close" && r.Name.Contains("patch_level"));
        Assert.Contains(f.Refs, r => r is { Kind: "sql", From: null, Mode: "write", Name: "patch_level" });   // the script
        Assert.Contains(f.Refs, r => r is { Kind: "column", From: null, Mode: "write", Name: "patch_date" });
        Assert.Contains(f.Refs, r => r is { Kind: "sql", From: "v_open", Name: "orders" });
        Assert.DoesNotContain(f.Refs, r => r.From == "v_open" && r.Name.Contains("patch_level"));
    }

    [Fact]
    public void AlterTable_WithoutCreate_KeepsAlterRefAndOwnerHint()
    {
        var f = SqlParser.Parse("p.sql", "ALTER TABLE dbo.Foo ADD CONSTRAINT x FOREIGN KEY (a) REFERENCES dbo.Bar (a)");
        Assert.Empty(f.Symbols);
        Assert.Contains(f.Refs, r => r.Kind == "sql" && r.Mode == "alter" && r.Name == "foo");
        var fk = Assert.Single(f.Refs, r => r.Kind == "fk");
        Assert.Null(fk.From);
        Assert.Equal("foo", fk.X["owner_hint"]!.GetValue<string>());
    }

    [Fact]
    public void Proc_ReadsWritesExec_AndDynamicSql()
    {
        const string sql = """
            -- comment mentioning FROM Ignored
            CREATE PROCEDURE [dbo].[GetStuff] @id int AS
            BEGIN
              SELECT a FROM dbo.Orders t JOIN Items m ON m.id = t.id
              INSERT INTO Log (x) VALUES (1)
              EXEC dbo.Other @id
              EXEC('select * from Dyn')
            END
            """;
        var f = SqlParser.Parse("x.sql", sql);
        var s = Assert.Single(f.Symbols);
        Assert.Equal("GetStuff", s.Name);
        Assert.Equal("db_proc", s.Kind);
        Assert.Equal(7, s.Lines);
        bool Has(string mode, string name) => f.Refs.Any(r => r.From == "getstuff" && r.Mode == mode && r.Name == name);
        Assert.True(Has("read", "orders"));
        Assert.True(Has("read", "items"));
        Assert.True(Has("write", "log"));
        Assert.True(Has("exec", "other"));
        var dyn = Assert.Single(f.Refs, r => r.Name == "dyn");
        Assert.True(dyn.X["dynamic"]!.GetValue<bool>());
        Assert.DoesNotContain(f.Refs, r => r.Name == "ignored");
    }

    [Fact]
    public void Trigger_Synonym_View_And_Script()
    {
        const string sql = """
            CREATE TRIGGER trg_t ON dbo.Orders AFTER INSERT AS UPDATE Counters SET n = n + 1
            GO
            CREATE SYNONYM dbo.Syn FOR Other.dbo.Real
            GO
            CREATE VIEW v1 AS SELECT * FROM Orders
            """;
        var f = SqlParser.Parse("x.sql", sql);
        Assert.Equal(["db_trigger", "db_synonym", "db_view"], f.Symbols.Select(s => s.Kind));
        Assert.Contains(f.Refs, r => r.Kind == "trigger_on" && r.From == "trg_t" && r.Name == "orders");
        Assert.Contains(f.Refs, r => r.From == "trg_t" && r.Mode == "write" && r.Name == "counters");
        Assert.Contains(f.Refs, r => r.From == "syn" && r.Mode == "read" && r.Name == "real");
        Assert.Contains(f.Refs, r => r.From == "v1" && r.Mode == "read" && r.Name == "orders");

        var script = SqlParser.Parse("patch.sql", "UPDATE Foo SET a = 1\nDELETE FROM Bar");
        Assert.Empty(script.Symbols);
        Assert.Contains(script.Refs, r => r.From == null && r.Mode == "write" && r.Name == "foo" && r.Line == 1);
        Assert.Contains(script.Refs, r => r.Mode == "write" && r.Name == "bar");
    }

    [Fact]
    public void Temp_table_inside_procedure_is_not_an_object_and_keeps_the_body()
    {
        var f = SqlParser.Parse("p.sql", "CREATE PROCEDURE dbo.P AS\nCREATE TABLE #tmp (a int)\nSELECT * FROM Orders\nGO");
        var s = Assert.Single(f.Symbols);
        Assert.Equal("db_proc", s.Kind);
        Assert.Contains(f.Refs, r => r.From == "p" && r.Mode == "read" && r.Name == "orders");
    }
}
