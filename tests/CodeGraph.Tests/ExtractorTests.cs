using System.Text.Json.Nodes;
using CodeGraph.Build;
using CodeGraph.Model;
using CodeGraph.Parsers;
using CodeGraph.Scan;

namespace CodeGraph.Tests;

public class ExtractorTests
{
    private const string ReportXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ReportDefinition xmlns="urn:acme:reports">
          <Name>Sales by Period</Name>
          <FileName>Sales by Period.rpt</FileName>
          <Group>Finance</Group>
          <Parameters>
            <Parameter Label="Business day" Field="BusinessDay" />
            <Parameter Label="Site" Field="SiteName" />
          </Parameters>
          <DataSource Procedure="sp_REP_Sales" />
          <InstallScripts>
            <InstallScript><![CDATA[
        CREATE PROCEDURE dbo.sp_REP_Sales @day int AS
        SELECT * FROM Orders WHERE day = @day
        ]]></InstallScript>
          </InstallScripts>
        </ReportDefinition>
        """;

    internal static ExtractorConfig Reports(params string[] referencedBy) => new()
    {
        Name = "report definitions",
        Files = ["Reports/*.xml"],
        Node = new ExtractorNode { Kind = "report", Name = "Name", Attrs = { ["group"] = "Group" } },
        Links =
        [
            new ExtractorLink { Relation = "uses_file", Value = "FileName" },
            new ExtractorLink { Relation = "has_parameter", Value = "Parameters/Parameter/@Field", Kind = "parameter", To = "new" },
            new ExtractorLink { Relation = "executes", Value = "DataSource/@Procedure" },
            new ExtractorLink { Sql = "InstallScripts/InstallScript" },
        ],
        ReferencedBy = [.. referencedBy],
    };

    [Fact]
    public void Xml_item_with_attrs_links_and_sql()
    {
        var f = ExtractorParser.Parse("Reports/Sales.xml", ReportXml, Reports());
        Assert.Equal("xml", f.Lang);
        var item = Assert.Single(f.Extra!["items"]!.AsArray())!.AsObject();
        Assert.Equal("item:report:sales by period", item["id"]!.ToString());
        Assert.Equal(3, (int)item["line"]!);
        Assert.Equal("Finance", item["attrs"]!["group"]!.ToString());
        var links = item["links"]!.AsArray().Select(l => $"{l!["relation"]}={l["value"]}").ToList();
        Assert.Equal(["uses_file=Sales by Period.rpt", "has_parameter=BusinessDay", "has_parameter=SiteName", "executes=sp_REP_Sales"], links);
        Assert.Equal(["Sales by Period.rpt"], item["files"]!.AsArray().Select(x => x!.ToString()));
        var proc = Assert.Single(f.Symbols);
        Assert.Equal("db_proc", proc.Kind);
        Assert.Equal("item:report:sales by period", proc.Extra!["item"]!.ToString());
        Assert.True(proc.Line >= 13, $"line {proc.Line} should be inside the CDATA block");
        Assert.Contains(f.Refs, r => r.Mode == "read" && r.Name == "orders");
    }

    [Fact]
    public void Lists_turn_repeated_elements_into_lines()
    {
        const string xml = """
            <Tables>
              <Table Name="Order_Status">
                <Data><Row Id="1" Description="Placed" /><Row Id="2" Description="Shipped" /></Data>
              </Table>
              <Table Name="Ui_Strings"><Data><Row Id="1" Text="Hello" /></Data></Table>
            </Tables>
            """;
        var x = new ExtractorConfig
        {
            Name = "lookup data", Files = ["data.xml"], Items = "/Tables/Table[@Name != 'Ui_Strings']",
            Node = new ExtractorNode { Kind = "lookup_table", Name = "@Name", Lists = [new ExtractorList { Label = "Rows", Items = "Data/Row" }] },
            Links = [new ExtractorLink { Relation = "seeds", Value = "@Name", To = "db" }],
        };
        var f = ExtractorParser.Parse("data.xml", xml, x);
        var item = Assert.Single(f.Extra!["items"]!.AsArray())!;
        Assert.Equal("Order_Status", item["name"]!.ToString());
        var rows = item["lists"]!["Rows"]!.AsArray();
        Assert.Equal(["1|Placed", "2|Shipped"], rows.Select(r => $"{r!["Id"]}|{r["Description"]}"));
    }

    [Fact]
    public void Missing_name_is_an_error()
    {
        Assert.Throws<FormatException>(() => ExtractorParser.Parse("x.xml", "<Other/>", Reports()));
    }

    [Fact]
    public void Json_items_and_pattern_rewrite()
    {
        const string json = """
            // patches
            { "patches": [ { "version": "240100", "sql": "ALTER TABLE Orders ADD x int" },
                           { "version": "250000" } ] }
            """;
        var x = new ExtractorConfig
        {
            Name = "patches", Files = ["patches.json"], Items = "patches[*]",
            Node = new ExtractorNode { Kind = "patch", Name = "version" },
            Links = [new ExtractorLink { Relation = "released_in", Value = "version", To = "release",
                                         Pattern = @"^(\d\d)(\d\d)(\d\d)$", Replace = "$1.$2.$3" },
                     new ExtractorLink { Sql = "sql" }],
        };
        var f = ExtractorParser.Parse("patches.json", json, x);
        var items = f.Extra!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal("24.01.00", items[0]!["links"]![0]!["value"]!.ToString());
        Assert.Equal(2, (int)items[0]!["line"]!);
        Assert.Contains(f.Refs, r => r.Mode == "alter" && r.Name == "orders" && r.Extra!["item"]!.ToString() == "item:patch:240100");
    }

    [Fact]
    public void Builder_links_items_releases_and_code_strings()
    {
        var cfg = new Config { Repos = [new RepoConfig { Name = "Shop", Path = "C:/r/Shop" }] };
        var report = ExtractorParser.Parse("Reports/Sales.xml", ReportXml, Reports("name", "uses_file"));
        var patches = ExtractorParser.Parse("patches.json", """{ "version": "240100" }""", new ExtractorConfig
        {
            Name = "patches", Files = ["patches.json"],
            Node = new ExtractorNode { Kind = "patch", Name = "version" },
            Links = [new ExtractorLink { Relation = "released_in", Value = "version", To = "release", Pattern = @"^(\d\d)(\d\d)(\d\d)$", Replace = "$1.$2.$3" }],
        });
        var table = new FileFacts { Lang = "sql", Symbols = [new Symbol { Name = "Orders", Kind = "db_table", Line = 1 }] };
        table.Symbols[0].X["key"] = "orders";
        var form = new FileFacts { Lang = "vb6", Symbols =
        [
            new Symbol { Name = "frmMenu", Kind = "form", Line = 1 },
            new Symbol { Name = "mnuSales_Click", Kind = "event_handler", Parent = "frmMenu", Line = 10, Lines = 5 },
        ] };
        CodeStrings.Add("frmMenu.frm", string.Concat(Enumerable.Repeat("\n", 11)) + "ShowReport \"Sales by Period\"\n", form);

        FileRecord Rec(string rel, FileFacts f) => new("Shop", "C:/r/Shop", rel, $"C:/r/Shop/{rel}", f);
        var g = Builder.BuildGraph(cfg, [Rec("Reports/Sales.xml", report), Rec("patches.json", patches),
            Rec("Database/Tables/dbo.Orders.sql", table), Rec("Menu/frmMenu.frm", form)]).Graph;

        const string rep = "item:report:sales by period";
        Assert.Equal("report", g.GetNode(rep)!.Kind);
        Assert.NotNull(g.GetEdge(rep, "file:Shop:Reports/Sales.xml", "defined_in"));
        Assert.NotNull(g.GetEdge(rep, "fileref:sales by period.rpt", "uses_file"));
        Assert.NotNull(g.GetEdge(rep, "item:parameter:businessday", "has_parameter"));
        Assert.NotNull(g.GetEdge(rep, "db:sp_rep_sales", "executes"));
        Assert.NotNull(g.GetEdge(rep, "db:sp_rep_sales", "defines"));
        Assert.NotNull(g.GetEdge("db:sp_rep_sales", "db:orders", "reads"));
        Assert.NotNull(g.GetEdge("sym:Shop:Menu/frmMenu.frm#frmMenu.mnuSales_Click", rep, "opens"));
        Assert.Equal("release", g.GetNode("rel:24.1.0")!.Kind);
        Assert.NotNull(g.GetEdge("item:patch:240100", "rel:24.1.0", "released_in"));

        var notes = CodeGraph.Output.Obsidian.Render(g, new Config { Vault = Path.GetTempPath() });
        string note = notes["Items/Reports/Sales by Period.md"];
        Assert.Contains("**group:** Finance", note);
        Assert.Contains("**has_parameter** [[Items/Parameters/BusinessDay|BusinessDay]]", note);
        Assert.Contains("**opens**", note);
        Assert.True(notes.ContainsKey("Items/Referenced files/Sales by Period.rpt.md"));
        Assert.True(notes.ContainsKey("Items/Patches/240100.md"));
    }

    [Fact]
    public void Scanner_reads_extractor_files_regardless_of_parser_and_size()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "Reports"));
        File.WriteAllText(Path.Combine(root, "Reports", "Sales.xml"), ReportXml + new string(' ', 3000));
        File.WriteAllText(Path.Combine(root, "other.xml"), "<x/>");
        var cfg = new Config
        {
            Output = Path.Combine(root, "out"), MaxFileKb = 1,
            Repos = [new RepoConfig { Name = "Shop", Path = root }], Extractors = [Reports()],
        };
        var files = Scanner.Discover(cfg);
        var f = Assert.Single(files);
        Assert.Equal("Reports/Sales.xml", f.Rel);
        var (records, errors) = Scanner.ParseAll(cfg, files, verbose: false);
        Assert.Empty(errors);
        Assert.Single(records[0].Facts.Extra!["items"]!.AsArray());

        // a changed rule re-reads the file instead of using the cache
        cfg.Extractors[0].Node.Kind = "rpt";
        var (again, _) = Scanner.ParseAll(cfg, Scanner.Discover(cfg), verbose: false);
        Assert.Equal("rpt", again[0].Facts.Extra!["items"]![0]!["kind"]!.ToString());
    }

    [Fact]
    public void Code_strings_keep_short_non_sql_literals_with_lines()
    {
        var f = new FileFacts();
        CodeStrings.Add("a.cs", "var a = \"Sales by Period\";\nvar b = \"select * from x\";\nvar c = \"ab\";", f);
        var s = Assert.Single(f.Extra!["strings"]!.AsArray())!.AsArray();
        Assert.Equal(1, (int)s[0]!);
        Assert.Equal("Sales by Period", s[1]!.ToString());
    }
}
