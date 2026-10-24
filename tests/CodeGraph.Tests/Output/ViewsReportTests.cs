using CodeGraph.Output;
using CodeGraph.Scan;

namespace CodeGraph.Tests.Output;

public class ViewsReportTests
{
    [Fact]
    public void Views_roll_up_files_and_db_use()
    {
        var v = new Views(SampleGraph.Create());
        Assert.Equal(SampleGraph.FMain, v.FileOf[SampleGraph.Save]);
        Assert.Equal(1, v.FileOut[SampleGraph.FMain][SampleGraph.FDb]);
        Assert.Equal(1, v.FileIn[SampleGraph.FDb][SampleGraph.FMain]);
        Assert.Equal(2, v.FileDb[SampleGraph.FMain][("db:Orders", "writes", null)]);
        Assert.Equal(1, v.DbUsers["db:Orders"][("db:usp_Save", "writes")]);
        Assert.Equal(2, v.DbUsers["db:Orders"][(SampleGraph.FMain, "writes")]);
        Assert.Equal([SampleGraph.FMain, SampleGraph.FDb], v.ProjectFiles(SampleGraph.Proj));
        Assert.Equal([SampleGraph.TForm, SampleGraph.Save], v.FileSymbols(SampleGraph.FMain));
        Assert.Equal(2, v.ProjectDb(SampleGraph.Proj)[("db:Orders", "writes")]);
    }

    [Fact]
    public void Counter_most_common_is_stable()
    {
        var c = new Counter<string>();
        c.Add("a"); c.Add("b", 2); c.Add("c");
        Assert.Equal(["b", "a", "c"], c.MostCommon().Select(kv => kv.Key));
        Assert.Equal(["b"], c.MostCommon(1).Select(kv => kv.Key));
    }

    [Fact]
    public void Report_has_all_sections()
    {
        var stats = new Dictionary<string, int> { ["files_vb6"] = 3, ["refs_total"] = 4000, ["refs_resolved"] = 1000, ["odd"] = 5 };
        var unresolved = new Dictionary<string, int> { ["exec:usp_Missing"] = 3, ["call:x"] = 9 };
        var errors = new[] { new ParseError("Shop", "Sales/bad.frm", "boom | pipe") };
        var md = Report.BuildReport(SampleGraph.Create(), stats, unresolved, errors, ["something odd"]);
        Assert.StartsWith("# Codegraph Report\n\n## Summary", md);
        Assert.Contains("- Source files: 3  |  nodes: 13  |  edges: 10", md);
        Assert.Contains("1,000 of 4,000 (25.0%)", md);
        Assert.Contains("| vb6 | 3 |", md);
        Assert.Contains("**Other counters**", md);
        Assert.Contains("| Order Entry | exe | vb6 | 1.2.3 | Sales | 2 | 150 | 1 |", md);
        Assert.Contains("| Orders | table | 1 | 1 |", md);
        Assert.Contains("## Possibly unused database objects (1)", md);
        Assert.Contains("| table | Unused |", md);
        Assert.Contains("## Stored procedures called from code but not found (1)", md);
        Assert.Contains("| usp_Missing | 3 |", md);
        Assert.Contains("## Orphan source files (1)", md);
        Assert.Contains("| Shop | Sales/Old.bas | 7 |", md);
        Assert.Contains("### Order Entry (1)", md);
        Assert.Contains("- `Gone.bas`", md);
        Assert.Contains("## Parse errors (1)", md);
        Assert.Contains("- `Shop` `Sales/bad.frm`: boom | pipe", md);
        Assert.Contains("## Warnings (1)", md);
        Assert.Contains("- something odd", md);
        Assert.EndsWith("\n", md);
    }

    [Fact]
    public void Report_without_problems_says_none()
    {
        var md = Report.BuildReport(new CodeGraph.Model.Graph(), new Dictionary<string, int>(), new Dictionary<string, int>(), [], []);
        Assert.Contains("## Parse errors (0)\n\n_none_", md);
        Assert.Contains("## Warnings (0)\n\n_none_", md);
        Assert.DoesNotContain("Stored procedures called", md);
    }
}
