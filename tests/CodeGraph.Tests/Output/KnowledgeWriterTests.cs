using CodeGraph.Knowledge;

namespace CodeGraph.Tests.Output;

public sealed class KnowledgeWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cg-kw").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private KnowledgeTargets Targets(out string central, out string repo)
    {
        central = Path.Combine(_dir, "knowledge.json");
        repo = Path.Combine(_dir, "Shop", ".codegraph", "knowledge.json");
        return new(central, new Dictionary<string, string> { ["Shop"] = repo });
    }

    [Fact]
    public void Adds_facts_and_notes_keeping_comments_and_layout()
    {
        var t = Targets(out string central, out _);
        File.WriteAllText(central, "// my facts\r\n{\r\n  \"facts\": [\r\n    [\"Orders\", \"is part of\", \"Ordering\"] // no comma\r\n  ],\r\n}\r\n");
        var g = SampleGraph.Create();
        string r = KnowledgeWriter.Remember(g, t, "db:usp_Save", "is slow on", "db:Orders", null, "verified in Save.sql:3", "2026-10-29");
        Assert.Contains("Recorded in", r);
        Assert.Equal(
            "// my facts\r\n{\r\n  \"facts\": [\r\n    [\"Orders\", \"is part of\", \"Ordering\"], // no comma\r\n" +
            "    // verified in Save.sql:3 (2026-10-29)\r\n    [\"usp_Save\", \"is slow on\", \"Orders\"],\r\n  ],\r\n}\r\n",
            File.ReadAllText(central));
        Assert.StartsWith("Already recorded", KnowledgeWriter.Remember(g, t, "usp_Save", "IS SLOW ON", "Orders", null, "again"));

        // a note on a node without a repo: the "notes" section is added
        KnowledgeWriter.Remember(g, t, "Orders", null, null, "Status 1 = placed", "told by the user", "2026-10-29");
        string text = File.ReadAllText(central);
        Assert.EndsWith("  \"notes\": {\r\n    // told by the user (2026-10-29)\r\n    \"Orders\": \"Status 1 = placed\",\r\n  },\r\n}\r\n", text);
        // a second note on the same node extends the first
        KnowledgeWriter.Remember(g, t, "Orders", null, null, "Never delete rows", "told", "2026-10-30");
        Assert.Contains("    // told (2026-10-30)\r\n    \"Orders\": \"Status 1 = placed\\n\\nNever delete rows\",", File.ReadAllText(central));
        Assert.StartsWith("Already recorded", KnowledgeWriter.Remember(g, t, "Orders", null, null, "never delete rows", "x"));

        var warnings = new List<string>();
        KnowledgeLoader.ApplyKnowledge(g, [central], warnings);
        // the sample procedure already writes the table, so that fact is only flagged as possibly redundant
        Assert.All(warnings, w => Assert.Contains("may be redundant", w));
        Assert.NotNull(g.GetEdge("db:usp_Save", "db:Orders", "is_slow_on"));
        Assert.Equal("Status 1 = placed\n\nNever delete rows", (string)g.GetNode("db:Orders")!["knowledge_notes"]![0]!);
    }

    [Fact]
    public void Repo_nodes_go_to_a_new_repo_file()
    {
        var t = Targets(out string central, out string repo);
        var g = SampleGraph.Create();
        // a routine has no repo attribute in the sample graph; its file does
        KnowledgeWriter.Remember(g, t, SampleGraph.Load, "calls at runtime", SampleGraph.FMain, "via CallByName", "verified in modDb.bas:3", "2026-10-29");
        Assert.False(File.Exists(central));
        string text = File.ReadAllText(repo);
        Assert.StartsWith("// Facts and notes for codegraph", text);
        Assert.Contains("    [\"Load\", \"calls at runtime\", \"Sales/frmMain.frm\", \"via CallByName\"],\n", text);
        var warnings = new List<string>();
        KnowledgeLoader.ApplyKnowledge(g, [(repo, (string?)"Shop")], warnings);
        Assert.Empty(warnings);
        Assert.NotNull(g.GetEdge(SampleGraph.Load, SampleGraph.FMain, "calls_at_runtime"));
    }

    [Fact]
    public void Refuses_what_it_cannot_write_safely()
    {
        var t = Targets(out string central, out _);
        var g = SampleGraph.Create();
        Assert.Throws<CodeGraphException>(() => KnowledgeWriter.Remember(g, t, "Orders", "uses", null, null, "x"));
        Assert.Throws<CodeGraphException>(() => KnowledgeWriter.Remember(g, t, "Orders", null, null, null, "x"));
        Assert.Throws<CodeGraphException>(() => KnowledgeWriter.Remember(g, t, "Ord*", null, null, "n", "x"));
        Assert.Throws<CodeGraphException>(() => KnowledgeWriter.Remember(g, new(null, new Dictionary<string, string>()), "Orders", null, null, "n", "x"));
        File.WriteAllText(central, "{ \"facts\": { } }");
        Assert.Throws<CodeGraphException>(() => KnowledgeWriter.Remember(g, t, "Orders", "uses", "usp_Save", null, "x"));
        File.WriteAllText(central, "[ ]");
        Assert.Throws<CodeGraphException>(() => KnowledgeWriter.Remember(g, t, "Orders", "uses", "usp_Save", null, "x"));
        Assert.Equal("[ ]", File.ReadAllText(central));
    }

    [Fact]
    public void Inserts_into_one_line_and_empty_sections()
    {
        Assert.Equal("{ \"facts\": [[\"a\", \"b\", \"c\"],\n  // s\n  [\"x\", \"y\", \"z\"],\n] }",
            KnowledgeWriter.AddToSection("{ \"facts\": [[\"a\", \"b\", \"c\"]] }", "facts", '[', "[\"x\", \"y\", \"z\"]", "// s"));
        Assert.Equal("{\n  \"notes\": {\n    // s\n    \"a\": \"b\",\n  }\n}",
            KnowledgeWriter.AddToSection("{\n  \"notes\": {\n  }\n}", "notes", '{', "\"a\": \"b\"", "// s"));
    }
}
