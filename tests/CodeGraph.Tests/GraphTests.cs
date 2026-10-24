using System.Text.Json.Nodes;
using CodeGraph.Model;
using G = CodeGraph.Model.Graph;

namespace CodeGraph.Tests;

public class GraphTests
{
    [Fact]
    public void Edges_need_both_nodes_and_accumulate_count()
    {
        var g = new G();
        g.Node("a", ("kind", "file"), ("name", "A"));
        g.Node("b", ("kind", "file"), ("name", "B"), ("line", 3));
        Assert.Null(g.AddEdge("a", "x", "calls"));
        Assert.Null(g.AddEdge("a", "a", "calls"));
        g.AddEdge("a", "b", "calls", attrs: ("line", 5));
        var e = g.AddEdge("a", "b", "calls", attrs: ("line", 9))!;
        Assert.Equal(2, e.Count);
        Assert.Equal(5, e.Int("line"));
        Assert.Equal(3, g.GetNode("b")!.Int("line"));
        Assert.Single(g.OutEdges()["a"]);
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var g = new G();
        g.Meta["generated"] = "now";
        g.Node("a", ("kind", "project"), ("name", "Café"), ("files", new JsonArray("x", "y")));
        g.Node("b", ("kind", "file"), ("name", "B"));
        g.AddEdge("a", "b", "contains");
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "graph.json");
        g.Save(path);
        Assert.Contains("Café", File.ReadAllText(path));
        var back = G.Load(path);
        Assert.Equal(["a", "b"], back.Nodes.Select(n => n.Id));
        Assert.Equal("Café", back.GetNode("a")!.Name);
        Assert.Equal(2, back.GetNode("a")!["files"]!.AsArray().Count);
        Assert.Equal("contains", Assert.Single(back.Edges).Relation);
        Assert.Equal("now", back.Meta["generated"]!.ToString());
    }

    [Fact]
    public void RemoveNode_drops_its_edges()
    {
        var g = new G();
        g.Node("a"); g.Node("b"); g.Node("c");
        g.AddEdge("a", "b", "calls");
        g.AddEdge("b", "c", "calls");
        g.AddEdge("a", "c", "calls");
        g.RemoveNode("b");
        Assert.Equal(["a->c"], g.Edges.Select(e => $"{e.Source}->{e.Target}"));
    }
}
