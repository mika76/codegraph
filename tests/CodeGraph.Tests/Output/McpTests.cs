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
        Assert.Equal(["search", "show", "impact", "path", "source", "report"], tools.Select(t => (string)t!["name"]!));
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
