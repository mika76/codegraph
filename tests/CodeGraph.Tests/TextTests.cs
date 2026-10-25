using CodeGraph.Text;

namespace CodeGraph.Tests;

public class ScrubTests
{
    [Fact]
    public void CLike_blanks_comments_and_strings_keeping_length()
    {
        const string src = "int a = 1; // note\nvar s = \"select * from Orders\"; /* x\ny */ char c = 'q';";
        var (code, strings) = Scrub.CLike(src);
        Assert.Equal(src.Length, code.Length);
        Assert.DoesNotContain("note", code);
        Assert.DoesNotContain("Orders", code);
        Assert.Equal(src.Split('\n').Length, code.Split('\n').Length);
        Assert.Equal(["select * from Orders"], strings.Select(s => s.Value));
    }

    [Fact]
    public void CLike_verbatim_string_unescapes_doubled_quotes()
    {
        var (_, strings) = Scrub.CLike("x = @\"say \"\"hi\"\"\";");
        Assert.Equal("say \"hi\"", Assert.Single(strings).Value);
    }

    [Fact]
    public void Vb_handles_apostrophe_rem_and_doubled_quotes()
    {
        const string src = "x = \"a \"\"b\"\"\" ' comment\nRem whole line\ny = 1";
        var (code, strings) = Scrub.Vb(src);
        Assert.Equal(src.Length, code.Length);
        Assert.DoesNotContain("comment", code);
        Assert.DoesNotContain("whole", code);
        Assert.Contains("y = 1", code);
        Assert.Equal("a \"b\"", Assert.Single(strings).Value);
    }

    [Fact]
    public void Vb_does_not_treat_Remove_as_comment()
    {
        var (code, _) = Scrub.Vb("Remove x");
        Assert.Equal("Remove x", code);
    }

    [Fact]
    public void PowerShell_here_string_close_does_not_open_new_string()
    {
        const string src = "$q = @'\nselect 1\n'@\n$x = 'a'";
        var (code, strings) = Scrub.PowerShell(src);
        Assert.Equal(src.Length, code.Length);
        Assert.Equal(["select 1", "a"], strings.Select(s => s.Value));
    }

    [Fact]
    public void Sql_nested_comments()
    {
        var (code, _) = Scrub.Sql("select /* a /* b */ c */ 1");
        Assert.Equal("select                   1", code);
    }

    [Fact]
    public void LineIndex_maps_offsets()
    {
        var li = new LineIndex("ab\ncd\n\nef");
        Assert.Equal(1, li.Line(0));
        Assert.Equal(1, li.Line(2));
        Assert.Equal(2, li.Line(3));
        Assert.Equal(4, li.Line(7));
    }
}

public class SqlTextTests
{
    [Fact]
    public void Refs_finds_reads_writes_execs()
    {
        var refs = SqlText.Refs("insert into dbo.Orders (a) select a from [dbo].[Accounts] c join Suppliers p on 1=1; exec bill_Customer_Get @x");
        Assert.Contains(("write", "dbo.orders"), refs);
        Assert.Contains(("read", "dbo.accounts"), refs);
        Assert.Contains(("read", "suppliers"), refs);
        Assert.Contains(("exec", "bill_customer_get"), refs);
    }

    [Fact]
    public void Delete_from_is_not_also_a_read()
    {
        var refs = SqlText.Refs("delete from Orders where id = 1");
        Assert.Equal([("write", "orders")], refs);
    }

    [Fact]
    public void Temp_tables_and_variables_are_skipped()
    {
        Assert.Empty(SqlText.Refs("select * from #tmp join @t on 1=1"));
    }

    [Theory]
    [InlineData("exec get_orders", "get_orders")]
    [InlineData("EXEC dbo.GetOrders @id = 1", "dbo.getorders")]
    [InlineData("EXEC cleanup;", "cleanup")]
    [InlineData("exec fetchall 'x', 2", "fetchall")]
    [InlineData("EXEC archive NULL\nGO", "archive")]
    [InlineData("EXEC master.dbo.xp_info", "master.dbo.xp_info")]
    [InlineData("EXEC orders_purge -- nightly", "orders_purge")]
    [InlineData("EXEC GetOrders \nFailed to load orders", "getorders")]  // the next piece of a concatenation
    [InlineData("Failed to execute telnet command", null)]
    [InlineData("execute report : ", null)]
    [InlineData("Execute the command", null)]
    [InlineData("Execute Stored Procedure responsible for it", null)]
    [InlineData("EXEC sp_", null)]                                        // "EXEC sp_" + name
    public void Exec_needs_a_call_shape(string sql, string? proc) =>
        Assert.Equal(proc, SqlText.Refs(sql).Where(r => r.Mode == "exec").Select(r => r.Name).SingleOrDefault());

    [Fact]
    public void Function_calls_under_any_schema()
    {
        var refs = SqlText.Refs("select dbo.TotalOf(1), sales.net_total(2), [app].[Fx](3), x.value('a', 'int') from t");
        Assert.Contains(("call", "dbo.totalof"), refs);
        Assert.Contains(("call", "sales.net_total"), refs);
        Assert.Contains(("call", "app.fx"), refs);
        Assert.DoesNotContain(refs, r => r.Name == "totalof" || r.Name.StartsWith("1"));
    }

    [Theory]
    [InlineData("bill___Customer_Get", "bill___customer_get")]
    [InlineData("getorders", "getorders")]
    [InlineData("ORDERS_GET", "orders_get")]
    [InlineData("[dbo].[Orders]", "orders")]
    [InlineData("sales.GetOrders", "sales.getorders")]
    [InlineData("[app].[Order Lines]", "app.order lines")]
    [InlineData("select 1", null)]
    [InlineData("two words", null)]
    [InlineData("a.b.c.d", null)]
    public void LiteralIdentifier(string s, string? expected) => Assert.Equal(expected, SqlText.LiteralIdentifier(s));

    [Theory]
    [InlineData("~/api/Orders/{id:int}/?x=1", "api/orders/{}")]
    [InlineData("`/m/${n}/h`", "m/{}/h")]
    [InlineData("https://host:5000/inventory/items", "inventory/items")]
    [InlineData("/users/:id", "users/{}")]
    [InlineData(":view/:id", "{}/{}")]
    public void Route_normalisation(string input, string expected) => Assert.Equal(expected, Routes.Normalize(input));
}

public class ConfigTests
{
    [Fact]
    public void Loads_json_with_comments_and_resolves_paths()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "codegraph.json");
        File.WriteAllText(path, """
            // comment
            {
              "output": "./out",
              "repos": [ { "path": "C:/x/Shop" }, { "name": "B", "path": "rel/b", "ignore": ["Docs"] }, ],
              "external_ignore_extra": ["Foo"],
              "obsidian": { "max_routines_per_note": 10 },
            }
            """);
        var cfg = Config.Load(path);
        Assert.Equal(Path.Combine(dir, "out"), cfg.Output);
        Assert.Equal("Shop", cfg.Repos[0].Name);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "rel/b")), cfg.Repos[1].Path);
        Assert.Equal(["Docs"], cfg.Repos[1].Ignore);
        Assert.Contains("Foo", cfg.ExternalIgnore);
        Assert.Contains("mscorlib", cfg.ExternalIgnore);
        Assert.Equal(10, cfg.Obsidian.MaxRoutinesPerNote);
        Assert.True(cfg.Obsidian.GraphColors);
    }

    [Fact]
    public void Duplicate_repo_names_are_rejected()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "c.json");
        File.WriteAllText(path, """{ "repos": [ { "name": "A", "path": "x" }, { "name": "a", "path": "y" } ] }""");
        Assert.Throws<CodeGraphException>(() => Config.Load(path));
    }

    [Fact]
    public void Example_config_written_by_init_loads()
    {
        var cfg = Config.Load(Path.Combine(AppContext.BaseDirectory, "Examples", "codegraph.json"));
        Assert.Equal("MyApp", Assert.Single(cfg.Repos).Name);
    }
}
