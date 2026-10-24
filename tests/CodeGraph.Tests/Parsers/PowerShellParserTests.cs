using CodeGraph.Parsers;

namespace CodeGraph.Tests.Parsers;

public class PowerShellParserTests
{
    [Fact]
    public void Functions_calls_sql_mentions()
    {
        const string ps = """
function Build-All {
    param($x)
    Invoke-Sql "SELECT * FROM Orders"
    & .\build.bat
}
# Ignore-This
Build-All
$q = "bill___Customer_Get"
""";
        var f = PowerShellParser.Parse("x/build.ps1", ps);
        Assert.Equal("powershell", f.Lang);
        var fn = Assert.Single(f.Symbols);
        Assert.Equal(("Build-All", "function", 1, 5), (fn.Name, fn.Kind, fn.Line, fn.Lines));
        Assert.Contains(f.Refs, r => r is { Kind: "call", Name: "Invoke-Sql", From: "Build-All" });
        Assert.Contains(f.Refs, r => r is { Kind: "call", Name: "Build-All", From: null });
        Assert.DoesNotContain(f.Refs, r => r.Name == "param");
        Assert.DoesNotContain(f.Refs, r => r.Name.Contains("Ignore"));
        Assert.Contains(f.Refs, r => r is { Kind: "sql", Mode: "read", Name: "orders", From: "Build-All" });
        Assert.Contains(f.Refs, r => r is { Kind: "literal", Name: "bill___customer_get", From: null });
        Assert.Contains(f.Refs, r => r is { Kind: "mention", Name: "build.bat" });
    }

    [Fact]
    public void Batch_labels_mentions()
    {
        const string bat = "@echo off\r\nrem :skip\r\n:Start\r\nmsbuild Shop\\App.vbp\r\n:: :nope\r\nsqlcmd -Q \"select * from Foo\"\r\n";
        var f = PowerShellParser.Parse("x/run.cmd", bat);
        Assert.Equal("batch", f.Lang);
        var label = Assert.Single(f.Symbols);
        Assert.Equal(("Start", "label", 3), (label.Name, label.Kind, label.Line));
        Assert.Contains(f.Refs, r => r is { Kind: "mention", Name: "app.vbp" });
        Assert.Contains(f.Refs, r => r is { Kind: "sql", Mode: "read", Name: "foo" });
    }
}
