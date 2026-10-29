using System.Text.Json.Nodes;
using CodeGraph.Mcp;

namespace CodeGraph.Tests.Output;

public sealed class McpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cg-mcp").FullName;
    private string GraphPath => Path.Combine(_dir, "graph.json");
    private string ReportPath => Path.Combine(_dir, "GRAPH_REPORT.md");

    public McpTests() => SampleGraph.Create().Save(GraphPath);

    public void Dispose() => Directory.Delete(_dir, true);

    private McpServer Server() => new(GraphPath, ReportPath);

    private static JsonObject Request(McpServer s, string method, JsonObject? p = null, int id = 1) =>
        s.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p }.ToJsonString())!;

    private static (string Text, bool IsError) Call(McpServer s, string tool, JsonObject? args = null)
    {
        var r = Request(s, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] })["result"]!;
        return ((string)r["content"]![0]!["text"]!, (bool)r["isError"]!);
    }

    [Fact]
    public void Initialize_negotiates_the_protocol_version()
    {
        var s = Server();
        var r = Request(s, "initialize", new JsonObject { ["protocolVersion"] = "2024-11-05" });
        Assert.Equal("2.0", (string)r["jsonrpc"]!);
        Assert.Equal(1, (int)r["id"]!);
        Assert.Equal("2024-11-05", (string)r["result"]!["protocolVersion"]!);
        Assert.Equal("codegraph", (string)r["result"]!["serverInfo"]!["name"]!);
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        Assert.Contains("search", (string)r["result"]!["instructions"]!);
        var newer = Request(s, "initialize", new JsonObject { ["protocolVersion"] = "2099-01-01" });
        Assert.Equal("2025-11-25", (string)newer["result"]!["protocolVersion"]!);
    }

    [Fact]
    public void Run_answers_requests_but_not_notifications()
    {
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{}}""" + "\n" +
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n\n" +
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""" + "\n");
        var output = new StringWriter();
        Server().Run(input, output, TextWriter.Null);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("a", (string)JsonNode.Parse(lines[0])!["id"]!);
        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":{}}""", lines[1]);
    }

    [Fact]
    public void Output_lines_keep_quotes_and_unicode_readable()
    {
        var line = McpServer.ToLine(new JsonObject { ["text"] = "'a' `b` <c> é\n\"d\"" });
        Assert.Equal("""{"text":"'a' `b` <c> é\n\"d\""}""", line);
    }

    [Fact]
    public void Protocol_errors()
    {
        var s = Server();
        Assert.Equal(-32700, (int)s.Handle("{not json")!["error"]!["code"]!);
        Assert.Equal(-32600, (int)s.Handle("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""")!["error"]!["code"]!); // batch
        Assert.Equal(-32600, (int)s.Handle("42")!["error"]!["code"]!);
        Assert.Equal(-32601, (int)Request(s, "resources/list")["error"]!["code"]!);
        var unknown = Request(s, "tools/call", new JsonObject { ["name"] = "nope" });
        Assert.Equal(-32602, (int)unknown["error"]!["code"]!);
        Assert.Null(s.Handle("""{"jsonrpc":"2.0","result":{}}"""));
    }

    [Fact]
    public void Lists_read_only_tools_with_schemas()
    {
        var tools = Request(Server(), "tools/list")["result"]!["tools"]!.AsArray();
        Assert.Equal(["search", "show", "impact", "path", "source", "db_usage", "report", "text_search", "status"], tools.Select(t => (string)t!["name"]!));
        foreach (var t in tools)
        {
            Assert.Equal("object", (string)t!["inputSchema"]!["type"]!);
            Assert.True((bool)t["annotations"]!["readOnlyHint"]!);
        }
        Assert.Equal(["from", "to"], tools[3]!["inputSchema"]!["required"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public void Search_returns_labels_with_ids()
    {
        var (text, err) = Call(Server(), "search", new JsonObject { ["query"] = "save" });
        Assert.False(err);
        Assert.StartsWith("2 matches for 'save':\n", text);
        Assert.Contains($"frmMain.Save (sub)  [Sales/frmMain.frm:10]  {{{SampleGraph.Save}}}", text);
        var limited = Call(Server(), "search", new JsonObject { ["query"] = "save", ["limit"] = 1 }).Text;
        Assert.Contains("... 1 more", limited);
        var (badKind, kindErr) = Call(Server(), "search", new JsonObject { ["query"] = "save", ["kind"] = "widget" });
        Assert.True(kindErr);
        Assert.StartsWith("Unknown kind 'widget'. Kinds in the graph: area, db_proc, db_table, file", badKind);
        var (badRepo, repoErr) = Call(Server(), "search", new JsonObject { ["query"] = "save", ["repo"] = "Nope" });
        Assert.True(repoErr);
        Assert.Equal("Unknown repo 'Nope'. Repositories: Shop", badRepo);
    }

    [Fact]
    public void Show_impact_path_and_report()
    {
        var s = Server();
        var show = Call(s, "show", new JsonObject { ["name"] = "Orders", ["relation"] = "writes", ["direction"] = "in" }).Text;
        Assert.Contains("writes (2):", show);
        Assert.Contains($"{{{SampleGraph.Save}}}", show);
        Assert.Contains($"Sales/modDb.bas  {{{SampleGraph.FDb}}}: Load", Call(s, "impact", new JsonObject { ["name"] = "db:Orders" }).Text);
        Assert.Contains("--executes--> usp_Save", Call(s, "path", new JsonObject { ["from"] = "frmMain.Save", ["to"] = "usp_Save" }).Text);
        Assert.True(Call(s, "report").IsError);
    }

    [Fact]
    public void Report_returns_the_summary_or_one_section()
    {
        var s = Server();
        File.WriteAllText(ReportPath,
            "# Report\n\n## Summary\n\n- files: 3\n\n## Orphan source files (1)\n\n- Old.bas\n\n## Projects with missing members (1)\n\n### Order Entry (1)\n- Gone.bas\n");
        Assert.Equal(
            "# Report\n\n## Summary\n\n- files: 3\n\nSections (call again with section):\n  Summary\n" +
            "  Orphan source files (1)\n  Projects with missing members (1)\n", Call(s, "report").Text);
        Assert.Equal("## Projects with missing members (1)\n\n### Order Entry (1)\n- Gone.bas\n",
            Call(s, "report", new JsonObject { ["section"] = "missing" }).Text);
        var (text, err) = Call(s, "report", new JsonObject { ["section"] = "nope" });
        Assert.True(err);
        Assert.StartsWith("No section matches 'nope'. Sections:\n  Summary\n", text);
    }

    [Fact]
    public void Bad_relations_and_numbers_are_tool_errors()
    {
        var s = Server();
        var (text, err) = Call(s, "show", new JsonObject { ["name"] = "Orders", ["relation"] = "bogus" });
        Assert.True(err);
        Assert.Equal("Unknown relation 'bogus'. Relations in the graph: calls, contains, executes, writes", text);
        Assert.False(Call(s, "show", new JsonObject { ["name"] = "Orders", ["relation"] = "WRITES" }).IsError);
        Assert.Equal(("depth must be at least 1 (got -1)", true), Call(s, "impact", new JsonObject { ["name"] = "Orders", ["depth"] = -1 }));
        Assert.Equal(("limit must be a whole number", true), Call(s, "search", new JsonObject { ["query"] = "x", ["limit"] = "many" }));
        var capped = Call(s, "impact", new JsonObject { ["name"] = "Orders", ["depth"] = 99 }).Text;
        Assert.Contains("(up to 10 steps)", capped);
        Assert.EndsWith("\n(depth 99 was capped at 10.)", capped);
        Assert.Contains("within 2 steps", Call(s, "path", new JsonObject { ["from"] = "Unused", ["to"] = "Orders", ["max_len"] = 2 }).Text);
    }

    [Fact]
    public void Ambiguous_unknown_and_missing_names_are_tool_errors()
    {
        var g = SampleGraph.Create();
        g.Node("db:other.Orders", ("kind", "db_table"), ("name", "Orders"));
        g.Save(GraphPath);
        var s = Server();
        var (text, err) = Call(s, "show", new JsonObject { ["name"] = "Orders" });
        Assert.True(err);
        Assert.Equal("'Orders' matches 2 things. Use one of these ids (or a narrower pattern):\n" +
                     "  Orders (db_table)  {db:Orders}\n  Orders (db_table)  {db:other.Orders}\n" +
                     "Call again with an id as name, or use the search tool.", text);
        Assert.False(Call(s, "show", new JsonObject { ["name"] = "db:other.Orders" }).IsError);
        Assert.Equal(("Nothing is called 'zzz'.\nCall again with an id as name, or use the search tool.", true),
            Call(s, "impact", new JsonObject { ["name"] = "zzz" }));
        // an id that doesn't exist is not answered with whatever contains it; the near misses are offered
        var (missing, missingErr) = Call(s, "show", new JsonObject { ["name"] = "db:Ord" });
        Assert.True(missingErr);
        Assert.StartsWith("No node has the id 'db:Ord'.\nClosest (use an id, or * as a wildcard):\n  Orders (db_table)  {db:Orders}\n", missing);
        // a wildcard picks the one match, or lists several
        Assert.False(Call(s, "show", new JsonObject { ["name"] = "db:usp*" }).IsError);
        Assert.StartsWith("'db:*' matches", Call(s, "show", new JsonObject { ["name"] = "db:*" }).Text);
        Assert.False(Call(s, "show", new JsonObject { ["name"] = "DB:USP_SAVE" }).IsError); // ids ignore case
        Assert.Equal(("Missing argument: to", true), Call(s, "path", new JsonObject { ["from"] = "usp_Save" }));
        Assert.True(Call(s, "show", new JsonObject { ["name"] = "Orders", ["direction"] = "up" }).IsError);
    }

    [Fact]
    public void Reloads_the_graph_after_a_build_and_reports_a_missing_one()
    {
        var s = Server();
        Assert.Contains("Nothing matches", Call(s, "search", new JsonObject { ["query"] = "Invoices" }).Text);
        var g = SampleGraph.Create();
        g.Node("db:Invoices", ("kind", "db_table"), ("name", "Invoices"));
        g.Save(GraphPath);
        File.SetLastWriteTimeUtc(GraphPath, DateTime.UtcNow.AddMinutes(1));
        Assert.Contains("{db:Invoices}", Call(s, "search", new JsonObject { ["query"] = "Invoices" }).Text);
        File.Delete(GraphPath);
        var (text, err) = Call(s, "search", new JsonObject { ["query"] = "Invoices" });
        Assert.True(err);
        Assert.StartsWith("No graph yet at ", text);
    }

    [Fact]
    public void Says_once_when_the_executable_was_updated_under_it()
    {
        string exe = Path.Combine(_dir, "codegraph.exe");
        File.WriteAllText(exe, "old");
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddHours(-1));
        var s = new McpServer(GraphPath, ReportPath, exe);
        Assert.DoesNotContain("restart", Call(s, "search", new JsonObject { ["query"] = "Orders" }).Text);
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow);  // publish.ps1 put a new build there
        Assert.Contains("restart the codegraph MCP server", Call(s, "search", new JsonObject { ["query"] = "Orders" }).Text);
        Assert.DoesNotContain("restart", Call(s, "search", new JsonObject { ["query"] = "Orders" }).Text);
    }

    private static JsonObject Data(McpServer s, string tool, JsonObject? args = null) =>
        Request(s, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] })["result"]!["structuredContent"]!.AsObject();

    [Fact]
    public void Every_tool_declares_an_output_schema()
    {
        foreach (var t in Request(Server(), "tools/list")["result"]!["tools"]!.AsArray())
        {
            var schema = t!["outputSchema"]!;
            Assert.Equal("object", (string)schema["type"]!);
            Assert.NotNull(schema["properties"]!["error"]);
        }
    }

    [Fact]
    public void Structured_search_and_show()
    {
        var s = Server();
        var search = Data(s, "search", new JsonObject { ["query"] = "save", ["limit"] = 1 });
        Assert.Equal(2, (int)search["total"]!);
        Assert.True((bool)search["truncated"]!);
        var first = search["results"]![0]!;
        Assert.Equal(SampleGraph.Save, (string)first["id"]!);
        Assert.Equal("sub", (string)first["kind"]!);
        Assert.Equal("Shop", (string)first["repo"]!);
        Assert.Equal("Sales/frmMain.frm", (string)first["file"]!);
        Assert.Equal(10, (int)first["line"]!);

        var show = Data(s, "show", new JsonObject { ["name"] = "Orders", ["direction"] = "in" });
        Assert.Equal("db:Orders", (string)show["node"]!["id"]!);
        Assert.Equal(2, show["attributes"]!["columns"]!.AsArray().Count);
        var writes = show["connections"]!.AsArray().Where(c => (string)c!["relation"]! == "writes").ToList();
        Assert.Equal(2, writes.Count);
        var save = writes.Single(c => (string)c!["id"]! == SampleGraph.Save)!;
        Assert.Equal("in", (string)save["direction"]!);
        Assert.Equal("write", (string)save["mode"]!);
        Assert.Equal(2, (int)save["count"]!);
        Assert.Equal("Save", (string)save["node"]!["name"]!);
        Assert.Equal(2, (int)show["groups"]![0]!["total"]!);
    }

    [Fact]
    public void Structured_impact_path_source_and_errors()
    {
        var s = Server();
        var impact = Data(s, "impact", new JsonObject { ["name"] = "db:Orders" });
        var affected = impact["affected"]!.AsArray();
        Assert.Equal((int)impact["total"]!, affected.Count);
        var load = affected.Single(x => (string)x!["id"]! == SampleGraph.Load)!;
        Assert.Equal(2, (int)load["distance"]!);  // Load executes usp_Save, which writes Orders
        Assert.Equal("Order Entry", (string)load["projects"]![0]!);

        var path = Data(s, "path", new JsonObject { ["from"] = "frmMain.Save", ["to"] = "usp_Save" });
        Assert.True((bool)path["found"]!);
        Assert.True((bool)path["one_way"]!);
        Assert.Equal(["calls", "executes"], path["steps"]!.AsArray().Select(x => (string)x!["relation"]!));
        Assert.Equal("db:usp_Save", (string)path["steps"]![1]!["id"]!);
        Assert.Equal("out", (string)path["steps"]![1]!["direction"]!);

        var noSource = Data(s, "source", new JsonObject { ["name"] = "frmMain.Save" });
        Assert.Contains("File not found", (string)noSource["problem"]!);

        var r = Request(s, "tools/call", new JsonObject { ["name"] = "show", ["arguments"] = new JsonObject { ["name"] = "db:Ord" } })["result"]!;
        Assert.True((bool)r["isError"]!);
        Assert.StartsWith("No node has the id", (string)r["structuredContent"]!["error"]!);
        Assert.Equal("db:Orders", (string)r["structuredContent"]!["candidates"]![0]!["id"]!);
        // the text answer is the same as before
        Assert.Equal((string)r["content"]![0]!["text"]!, (string)r["structuredContent"]!["error"]!);
    }

    [Fact]
    public void Structured_source_lines()
    {
        string repo = Path.Combine(_dir, "Shop");
        Directory.CreateDirectory(Path.Combine(repo, "Sales"));
        File.WriteAllLines(Path.Combine(repo, "Sales", "frmMain.frm"), Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var g = SampleGraph.Create();
        g.Node("repo:Shop", ("path", repo));
        g.Save(GraphPath);
        var src = Data(Server(), "source", new JsonObject { ["name"] = "frmMain.Save", ["max_lines"] = 3 });
        Assert.Equal(10, (int)src["start_line"]!);
        Assert.Equal(12, (int)src["end_line"]!);
        Assert.Equal(["line 10", "line 11", "line 12"], src["lines"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(2, (int)src["more_lines"]!);
    }

    /// <summary>A parse cache next to graph.json with these references in Sales/Old.bas and Sales/modDb.bas.</summary>
    private void WriteCache(params (string File, CodeGraph.Model.Ref Ref)[] refs)
    {
        var cache = new CodeGraph.Scan.CacheFile { Version = CodeGraph.Scan.Scanner.ParserVersion };
        foreach (var (file, rf) in refs)
        {
            string abs = "C:/x/Shop/" + file;
            if (!cache.Files.TryGetValue(abs, out var entry))
                cache.Files[abs] = entry = new CodeGraph.Scan.CacheEntry { Facts = new() { Lang = "vb6" } };
            entry.Facts.Refs.Add(rf);
        }
        File.WriteAllText(Path.Combine(_dir, CodeGraph.Scan.Scanner.CacheFileName),
            System.Text.Json.JsonSerializer.Serialize(cache, CodeGraph.Scan.CacheJson.Default.CacheFile));
    }

    [Fact]
    public void Db_usage_lists_users_possible_matches_and_entry_points()
    {
        var g = SampleGraph.Create();
        // an API endpoint that runs the procedure, and a column read by a statement with two tables
        g.Node("sym:Shop:Api/OrdersController.cs#OrdersController.Post", ("kind", "method"), ("name", "Post"),
            ("qual", "OrdersController.Post"), ("routes", new JsonArray("POST /orders")));
        g.AddEdge("sym:Shop:Api/OrdersController.cs#OrdersController.Post", "db:usp_Save", "executes", attrs: ("line", (JsonNode?)7));
        g.Save(GraphPath);
        WriteCache(
            ("Sales/Old.bas", new CodeGraph.Model.Ref { Kind = "literal", Name = "Orders", Line = 4 }),
            ("Sales/modDb.bas", new CodeGraph.Model.Ref { Kind = "literal", Name = "dbo.Orders", Line = 5, From = "Load", FromParent = "modDb" }),
            ("Sales/modDb.bas", new CodeGraph.Model.Ref { Kind = "literal", Name = "other.Orders", Line = 6 }),
            ("Sales/frmMain.frm", new CodeGraph.Model.Ref { Kind = "literal", Name = "Orders", Line = 12, From = "Save", FromParent = "frmMain" }));
        var s = Server();
        var d = Data(s, "db_usage", new JsonObject { ["name"] = "Orders" });
        Assert.Equal("db:Orders", (string)d["target"]!["id"]!);
        Assert.True((bool)d["possible_checked"]!);
        var users = d["users"]!.AsArray();
        var save = users.Single(u => (string)u!["id"]! == SampleGraph.Save)!;
        Assert.Equal(("confirmed", "app", "write", "writes"),
            ((string)save["confidence"]!, (string)save["layer"]!, (string)save["mode"]!, (string)save["relation"]!));
        Assert.Equal("database", (string)users.Single(u => (string)u!["id"]! == "db:usp_Save")!["layer"]!);
        // possible, by file and line: the strings in Old.bas, in modDb.Load and 'other.Orders' (no such table is
        // defined, so it may mean this one); not the one in frmMain.Save, which is already linked
        var possible = users.Where(u => (string)u!["confidence"]! == "possible").ToList();
        Assert.Equal([SampleGraph.FOrphan, SampleGraph.Load, SampleGraph.FDb], possible.Select(u => (string)u!["id"]!));
        var old = possible.First(u => (string)u!["id"]! == SampleGraph.FOrphan)!;
        Assert.Equal(("Sales/Old.bas", 4, "Orders", "name in a string"),
            ((string)old["file"]!, (int)old["line"]!, (string)old["text"]!, (string)old["reason"]!));
        Assert.Equal(2, (int)d["counts"]!["confirmed"]!);
        Assert.Equal(3, (int)d["counts"]!["possible"]!);
        Assert.Equal(1, (int)d["counts"]!["confirmed_app"]!);

        var entries = d["entry_points"]!.AsArray();
        var form = entries.Single(e => (string)e!["id"]! == SampleGraph.TForm)!;
        Assert.Equal(("form", 0, "confirmed"), ((string)form["kind"]!, (int)form["distance"]!, (string)form["confidence"]!));
        var api = entries.Single(e => (string)e!["kind"]! == "api")!;
        Assert.Equal(1, (int)api["distance"]!);  // runs usp_Save, which writes Orders
        Assert.Equal("POST /orders", (string)api["routes"]![0]!);
        Assert.Equal("db:usp_Save", (string)api["through"]![0]!);

        var text = Call(s, "db_usage", new JsonObject { ["name"] = "db:Orders" }).Text;
        Assert.Contains("Direct users (2, linked):", text);
        Assert.Contains($"write     frmMain.Save (sub)  [Sales/frmMain.frm:10]  {{{SampleGraph.Save}}}", text);
        Assert.Contains("Sales/Old.bas:4  \"Orders\"  name in a string", text);
        Assert.Contains("api       OrdersController.Post (method)  POST /orders  (1 step from usp_Save)", text);

        // with another table of that name defined, other.Orders is that one
        g.Node("db:other.orders", ("kind", "db_table"), ("name", "Orders"));
        g.Save(GraphPath);
        File.SetLastWriteTimeUtc(GraphPath, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(2, (int)Data(s, "db_usage", new JsonObject { ["name"] = "db:Orders" })["counts"]!["possible"]!);
    }

    [Fact]
    public void Db_usage_of_a_column_and_bad_targets()
    {
        var g = SampleGraph.Create();
        g.Node("col:orders.status", ("kind", "column"), ("name", "Orders.Status"), ("table", "db:Orders"));
        g.AddEdge("db:Orders", "col:orders.status", "contains");
        g.AddEdge(SampleGraph.Save, "col:orders.status", "writes", attrs: ("line", (JsonNode?)11));
        // a patch embedded in a file of patches (an extractor item) that reads it
        g.Node("item:patch:7", ("kind", "patch"), ("name", "7"), ("file", SampleGraph.FOrphan));
        g.AddEdge("item:patch:7", "col:orders.status", "reads", attrs: ("line", (JsonNode?)8));
        g.Save(GraphPath);
        var two = new JsonArray("orders", "customers");
        WriteCache(
            ("Sales/Old.bas", new CodeGraph.Model.Ref { Kind = "column", Name = "Status", Line = 2, Extra = new JsonObject { ["tables"] = two } }),
            ("Sales/Old.bas", new CodeGraph.Model.Ref { Kind = "column", Name = "Status", Line = 3, Extra = new JsonObject { ["tables"] = new JsonArray("customers") } }),
            ("Sales/Old.bas", new CodeGraph.Model.Ref { Kind = "literal", Name = "Status", Line = 5, Extra = new JsonObject { ["column"] = true } }),
            ("Sales/Old.bas", new CodeGraph.Model.Ref { Kind = "literal", Name = "Status", Line = 6 }),
            ("Sales/Old.bas", new CodeGraph.Model.Ref { Kind = "column", Name = "Status", Line = 9, Extra = new JsonObject { ["tables"] = two.DeepClone(), ["item"] = "item:patch:7" } }));
        var s = Server();
        var d = Data(s, "db_usage", new JsonObject { ["name"] = "Orders.Status" });
        var users = d["users"]!.AsArray();
        Assert.Equal("write", (string)users[0]!["mode"]!);
        Assert.Equal(11, (int)users[0]!["line"]!);
        Assert.Equal("read", (string)users[1]!["mode"]!);  // the patch
        // the column of another table, the plain string and the patch's own statement are not possible users
        Assert.Equal(["column in SQL with several tables", "column read by name"],
            users.Skip(2).Select(u => (string)u!["reason"]!));
        // without the cache: no possible users, and it says so
        var noPossible = Data(s, "db_usage", new JsonObject { ["name"] = "Orders.Status", ["possible"] = false });
        Assert.False((bool)noPossible["possible_checked"]!);
        Assert.Equal(2, noPossible["users"]!.AsArray().Count);

        var (text, err) = Call(s, "db_usage", new JsonObject { ["name"] = "frmMain.Save" });
        Assert.True(err);
        Assert.Contains("is not a database object or column", text);
    }

    [Fact]
    public void Text_search_finds_whole_words_in_scanned_files_and_says_what_is_linked()
    {
        string repo = Path.Combine(_dir, "Shop");
        Directory.CreateDirectory(Path.Combine(repo, "Sales"));
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        var lines = Enumerable.Range(1, 20).Select(i => $"' line {i}").ToArray();
        lines[11] = "    sql = \"UPDATE \" & \"Orders SET Status = 1\"";  // line 12, inside Save (10-14)
        lines[15] = "    ' OrdersArchive is another table";
        File.WriteAllLines(Path.Combine(repo, "Sales", "frmMain.frm"), lines);
        File.WriteAllText(Path.Combine(repo, "Sales", "Old.bas"), "Attribute VB_Name = \"Old\"\nx = \"orders\"\n");
        File.WriteAllText(Path.Combine(repo, "Sales", "notes.bin"), "Orders");  // not a file the build reads
        var g = SampleGraph.Create();
        g.Node("repo:Shop", ("path", repo));
        g.Save(GraphPath);
        var cfg = new Config { Repos = [new RepoConfig { Name = "Shop", Path = repo }] };
        var s = new McpServer(GraphPath, ReportPath, config: cfg);
        var d = Data(s, "text_search", new JsonObject { ["text"] = "Orders" });
        var hits = d["hits"]!.AsArray();
        Assert.Equal(2, (int)d["total"]!);
        Assert.Equal(("Sales/frmMain.frm", 12, "text"), ((string)hits[0]!["file"]!, (int)hits[0]!["line"]!, (string)hits[0]!["confidence"]!));
        Assert.Equal(SampleGraph.Save, (string)hits[0]!["node"]!["id"]!);
        Assert.True((bool)hits[0]!["linked"]!);  // Save writes Orders
        Assert.Equal(("Sales/Old.bas", 2, false), ((string)hits[1]!["file"]!, (int)hits[1]!["line"]!, (bool)hits[1]!["linked"]!));
        Assert.Equal(3, (int)Data(s, "text_search", new JsonObject { ["text"] = "orders", ["whole_word"] = false })["total"]!);
        Assert.Contains("Shop/Sales/frmMain.frm:12  (in frmMain.Save)  [linked]  sql = ", Call(s, "text_search", new JsonObject { ["text"] = "Orders" }).Text);
        Assert.True(Call(Server(), "text_search", new JsonObject { ["text"] = "Orders" }).IsError); // no config
    }

    [Fact]
    public void Git_commit_is_read_from_refs_packed_refs_or_a_detached_head()
    {
        string repo = Path.Combine(_dir, "Shop"), git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        string a = new('a', 40), b = new('b', 40), c = new('c', 40);
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), a + "\n");
        Assert.Equal(a, CodeGraph.Knowledge.KnowledgeLoader.GitCommit(repo));
        File.Delete(Path.Combine(git, "refs", "heads", "main"));
        File.WriteAllText(Path.Combine(git, "packed-refs"), $"# pack-refs\n{b} refs/heads/main\n");
        Assert.Equal(b, CodeGraph.Knowledge.KnowledgeLoader.GitCommit(repo));
        File.WriteAllText(Path.Combine(git, "HEAD"), c + "\n");
        Assert.Equal(c, CodeGraph.Knowledge.KnowledgeLoader.GitCommit(repo));
        Assert.Null(CodeGraph.Knowledge.KnowledgeLoader.GitCommit(_dir));
    }

    [Fact]
    public void Status_says_when_and_from_which_commits_the_graph_was_built()
    {
        string repo = Path.Combine(_dir, "Shop"), git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/R2.4.0\n");
        File.WriteAllText(Path.Combine(git, "refs", "heads", "R2.4.0"), new string('a', 40));
        var g = SampleGraph.Create();
        g.Node("repo:Shop", ("path", repo), ("commit", new string('a', 40)));
        g.Meta["generated"] = "2026-01-02 03:04";
        g.Save(GraphPath);
        var s = Server();
        var d = Data(s, "status");
        Assert.Equal("2026-01-02 03:04", (string)d["graph"]!["generated"]!);
        Assert.Equal(g.Nodes.Count, (int)d["graph"]!["nodes"]!);
        var r = d["repos"]![0]!;
        Assert.Equal(("Shop", "R2.4.0", false), ((string)r["name"]!, (string)r["current_branch"]!, (bool)r["changed"]!));
        File.WriteAllText(Path.Combine(git, "refs", "heads", "R2.4.0"), new string('b', 40));
        Assert.True((bool)Data(s, "status")["repos"]![0]!["changed"]!);
        Assert.Contains("Shop: R2.4.0 aaaaaaaaaaaa  - now R2.4.0 bbbbbbbbbbbb: rebuild to catch up", Call(s, "status").Text);
    }

    [Fact]
    public void Long_output_is_cut_at_a_line_end()
    {
        string text = string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i:D5}\n"));
        string cut = McpServer.Cut(text);
        Assert.True(cut.Length < McpServer.MaxChars + 200);
        Assert.Contains("\n... output cut at ", cut);
        Assert.Equal(text, McpServer.Cut(text[..1000]) + text[1000..]);
    }
}
