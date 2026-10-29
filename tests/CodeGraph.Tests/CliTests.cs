using CodeGraph.Tests.Output;

namespace CodeGraph.Tests;

/// <summary>Console output is process-wide: these tests must not run next to others that write to it.</summary>
[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

/// <summary>The command line over a saved graph: search, show filters and clear errors for bad options.</summary>
[Collection("Console")]
public sealed class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cg-cli").FullName;
    private readonly string _config;

    public CliTests()
    {
        _config = Path.Combine(_dir, "codegraph.json");
        File.WriteAllText(_config, """{ "repos": [], "output": "out" }""");
        Directory.CreateDirectory(Path.Combine(_dir, "out"));
        SampleGraph.Create().Save(Path.Combine(_dir, "out", "graph.json"));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string Run(params string[] args)
    {
        var old = Console.Out;
        var w = new StringWriter { NewLine = "\n" };
        Console.SetOut(w);
        try { Assert.Equal(0, Cli.Run(["-c", _config, .. args])); }
        finally { Console.SetOut(old); }
        return w.ToString();
    }

    private string Error(params string[] args) =>
        Assert.Throws<CodeGraphException>(() => Cli.Run(["-c", _config, .. args])).Message;

    [Fact]
    public void Search_lists_matches_with_their_ids()
    {
        string text = Run("search", "sav");
        Assert.StartsWith("2 matches for 'sav':\n", text);
        Assert.Contains($"{{{SampleGraph.Save}}}", text);
        Assert.Contains("{db:usp_Save}", Run("search", "save", "--kind", "db_proc"));
        Assert.Equal("Nothing matches 'zzz'.\n", Run("search", "zzz"));
        Assert.StartsWith("Unknown kind 'widget'.", Error("search", "x", "--kind", "widget"));
        Assert.Contains("1 more", Run("search", "sav", "--limit", "1"));
    }

    [Fact]
    public void Show_filters_by_relation_and_direction_and_prints_ids()
    {
        string both = Run("show", "Orders");
        Assert.Contains("writes", both);
        string outgoing = Run("show", "frmMain.Save", "--direction", "out", "--relation", "calls", "--ids");
        Assert.Contains($"{{{SampleGraph.Load}}}", outgoing);
        Assert.DoesNotContain("writes", outgoing);
        Assert.Contains($"{{{SampleGraph.FDb}}}", Run("impact", "usp_Save", "--ids"));  // impact prints file ids
    }

    [Fact]
    public void Unknown_options_and_bad_values_say_what_is_wrong()
    {
        Assert.StartsWith("Unknown option for show: --dir", Error("show", "Orders", "--dir", "out"));
        Assert.StartsWith("Unknown options for impact: --deep -x", Error("impact", "Orders", "--deep", "-x"));
        Assert.Equal("--direction must be in, out or both, not 'up'.", Error("show", "Orders", "--direction", "up"));
        Assert.Equal("--depth needs a whole number of at least 1, not 'two'.", Error("impact", "Orders", "--depth", "two"));
        Assert.Equal("Expected one name, got 2: \"Order\", \"Entry\" (quote a name with spaces).", Error("show", "Order", "Entry"));
    }
}
