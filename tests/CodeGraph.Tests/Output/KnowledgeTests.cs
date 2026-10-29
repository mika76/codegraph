using System.Text.Json.Nodes;
using CodeGraph.Knowledge;
using CodeGraph.Model;

namespace CodeGraph.Tests.Output;

public sealed class KnowledgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-know-" + Guid.NewGuid().ToString("N"));

    public KnowledgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Write(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void Releases_link_projects_areas_and_unknown_modules()
    {
        var g = SampleGraph.Create();
        string p = Write("releases.json", """
            // comment
            {
              "releases": [
                { "version": "2.3", "date": "2026-10-20", "status": "released", "notes": "Fix",
                  "modules": { "Order Entry": "2.3.0", "OrderEntry.exe": "dup", "Sales": "x", "Nope": "1", "Orders": "7", },
                },
                { "name": "2.1", "modules": ["Order Entry", { "frmMain.frm": "3" }] },
                { "notes": "no version" },
              ]
            }
            """);
        var warnings = new List<string>();
        KnowledgeLoader.ApplyReleases(g, p, warnings);
        var rel = g.GetNode("rel:2.3")!;
        Assert.Equal("release", rel.Kind);
        Assert.Equal("2026-10-20", rel.Str("date"));
        Assert.Equal("Fix", rel.Str("notes"));
        Assert.Equal("2.3.0", g.GetEdge(SampleGraph.Proj, "rel:2.3", "released_in")!.Str("version"));
        // "Sales" matches both the area and nothing else -> area; "Orders" -> the table
        Assert.NotNull(g.GetEdge("area:Shop:Sales", "rel:2.3", "released_in"));
        Assert.Equal("7", g.GetEdge("db:Orders", "rel:2.3", "released_in")!.Str("version"));
        // unknown module kept as plain name and reported
        Assert.Equal("module_ref", g.GetNode("module:nope")!.Kind);
        Assert.Contains(warnings, w => w.Contains("releases 2.3: module 'Nope' did not match"));
        // list-style modules
        Assert.NotNull(g.GetEdge(SampleGraph.Proj, "rel:2.1", "released_in"));
        Assert.Null(g.GetEdge(SampleGraph.Proj, "rel:2.1", "released_in")!.Str("version"));
        Assert.Equal("3", g.GetEdge(SampleGraph.FMain, "rel:2.1", "released_in")!.Str("version"));
        Assert.Contains(warnings, w => w.Contains("without 'version'"));
    }

    [Fact]
    public void Missing_or_empty_releases_file_is_fine()
    {
        var g = SampleGraph.Create();
        int n = g.Nodes.Count;
        KnowledgeLoader.ApplyReleases(g, Path.Combine(_dir, "none.json"), []);
        KnowledgeLoader.ApplyReleases(g, Write("empty.json", "  "), []);
        KnowledgeLoader.ApplyReleases(g, Write("only-comments.json", "// nothing\n{ \"releases\": [] }"), []);
        Assert.Equal(n, g.Nodes.Count);
    }

    [Fact]
    public void Bad_json_names_the_file()
    {
        var ex = Assert.Throws<CodeGraphException>(() => KnowledgeLoader.ApplyReleases(SampleGraph.Create(), Write("bad.json", "{ nope"), []));
        Assert.Contains("bad.json", ex.Message);
    }

    [Fact]
    public void Knowledge_facts_links_and_notes()
    {
        var g = SampleGraph.Create();
        Write("k1.json", """
            { "facts": [
                ["Order Entry", "owned by", "Team Sales"],
                ["Orders", "is part of!", "Ordering", "since 2020"],
                ["Billing", "documented at", "https://wiki.example.com/billing"],
                { "subject": "Orders", "relation": "Uses", "object": "usp_Save", "note": "n" },
                ["Orders", "same", "orders"],
                ["only", "two"],
                ["", "x", "y"],
                "garbage"
              ],
              "notes": { "Orders": "  Status: 1 = issued  ", "Brand New": "hello" } }
            """);
        Write("k2.json", """{ "notes": { "Orders": "second" } }""");
        var warnings = new List<string>();
        KnowledgeLoader.ApplyKnowledge(g, [Path.Combine(_dir, "k*.json"), Path.Combine(_dir, "missing.json")], warnings);

        var e = g.GetEdge(SampleGraph.Proj, "concept:team sales", "owned_by")!;
        Assert.True(e.Data["custom"]!.GetValue<bool>());
        Assert.Equal("owned by", e.Str("label"));
        Assert.Equal("concept", g.GetNode("concept:team sales")!.Kind);
        Assert.Equal("Team Sales", g.GetNode("concept:team sales")!.Name);
        var e2 = g.GetEdge("db:Orders", "concept:ordering", "is_part_of")!;
        Assert.Equal("since 2020", e2.Str("note"));
        Assert.NotNull(g.GetEdge("db:Orders", "db:usp_Save", "uses"));

        var links = (JsonArray)g.GetNode("concept:billing")!["links"]!;
        Assert.Equal("https://wiki.example.com/billing", (string)links[0]!["url"]!);
        Assert.Equal("documented at", (string)links[0]!["relation"]!);

        var notes = ((JsonArray)g.GetNode("db:Orders")!["knowledge_notes"]!).Select(x => (string)x!).ToArray();
        Assert.Equal(["Status: 1 = issued", "second"], notes);
        Assert.Equal("concept", g.GetNode("concept:brand new")!.Kind);
        Assert.Contains(warnings, w => w.Contains("k1.json: could not read fact"));
        Assert.Contains(warnings, w => w.Contains("links a node to itself"));
    }

    [Fact]
    public void Repo_knowledge_files_prefer_their_own_repo()
    {
        var g = SampleGraph.Create();
        const string webMain = "file:Web:Sales/frmMain.frm";
        g.Node("repo:Web", ("kind", "repo"), ("name", "Web"));
        g.Node(webMain, ("kind", "file"), ("name", "frmMain.frm"), ("path", "Sales/frmMain.frm"), ("repo", "Web"));
        string shop = Path.Combine(_dir, "Shop"), web = Path.Combine(_dir, "Web");
        Directory.CreateDirectory(Path.Combine(shop, ".codegraph"));
        Directory.CreateDirectory(Path.Combine(web, ".codegraph"));
        File.WriteAllText(Path.Combine(web, ".codegraph", "knowledge.json"), """
            { "facts": [
                ["Sales/frmMain.frm", "shows", "Orders"],
                ["Sales/Gone.frm", "shows", "Orders"],
                ["db:usp_Save", "is slow on", "Orders"],
              ],
              "notes": { "Sales/frmMain.frm": "web copy" } }
            """);
        File.WriteAllText(Path.Combine(shop, ".codegraph", "team.json"), """{ "notes": { "Sales/frmMain.frm": "shop copy" } }""");
        var cfg = new Config
        {
            Repos = [new RepoConfig { Name = "Shop", Path = shop }, new RepoConfig { Name = "Web", Path = web }],
            RepoKnowledge = [".codegraph/knowledge.json", ".codegraph/t*.json"],
        };
        var files = KnowledgeLoader.KnowledgeFiles(cfg);
        Assert.Equal(["Shop", "Web"], files.Select(f => f.Repo!).Order().ToArray());

        var warnings = new List<string>();
        KnowledgeLoader.ApplyKnowledge(g, files, warnings);
        Assert.NotNull(g.GetEdge(webMain, "db:Orders", "shows"));
        Assert.Null(g.GetEdge(SampleGraph.FMain, "db:Orders", "shows"));
        Assert.Equal("web copy", (string)g.GetNode(webMain)!["knowledge_notes"]![0]!);
        Assert.Equal("shop copy", (string)g.GetNode(SampleGraph.FMain)!["knowledge_notes"]![0]!);
        // node ids work as names; an unknown path is skipped, not turned into a concept
        Assert.NotNull(g.GetEdge("db:usp_Save", "db:Orders", "is_slow_on"));
        Assert.DoesNotContain(g.Nodes, n => n.Kind == "concept");
        Assert.Contains(warnings, w => w.Contains("Web: knowledge.json: 'Sales/Gone.frm' is not in the graph"));

        Assert.Equal(Path.Combine(web, ".codegraph/knowledge.json"), KnowledgeLoader.RepoKnowledgeFile(cfg, web));
        Assert.Empty(KnowledgeLoader.KnowledgeFiles(new Config { Repos = cfg.Repos, RepoKnowledge = [] }));
    }

    [Fact]
    public void Name_index_prefers_projects_and_matches_output_names()
    {
        var idx = new NameIndex(SampleGraph.Create());
        Assert.Equal(SampleGraph.Proj, idx.Find(" order entry "));
        Assert.Equal(SampleGraph.Proj, idx.Find("OrderEntry.exe"));
        Assert.Equal(SampleGraph.Proj, idx.Find("OrderEntry"));
        Assert.Equal(SampleGraph.FMain, idx.Find("Sales/frmMain.frm"));
        Assert.Equal("area:Shop:Sales", idx.Find("Sales"));
        Assert.Null(idx.Find("zzz"));
    }

    [Fact]
    public void Release_snapshot_appends_and_keeps_existing_entries()
    {
        var g = SampleGraph.Create();
        g.Node("proj:Shop:b", ("kind", "project"), ("name", "alpha"), ("repo", "Shop"));
        string p = Write("releases.json", "// my comment\n{ \"releases\": [ { \"version\": \"1.0\", \"modules\": {} } ] }");
        int n = KnowledgeLoader.ReleaseSnapshot(g, "2.0", "2026-01-02", p);
        Assert.Equal(2, n);
        string text = File.ReadAllText(p);
        Assert.Contains("\n  \"releases\": [", text); // indented
        var root = JsonNode.Parse(text)!;
        var rels = root["releases"]!.AsArray();
        Assert.Equal(2, rels.Count);
        Assert.Equal("1.0", (string)rels[0]!["version"]!);
        Assert.Equal("2.0", (string)rels[1]!["version"]!);
        Assert.Equal("2026-01-02", (string)rels[1]!["date"]!);
        Assert.Equal("1.2.3", (string)rels[1]!["modules"]!["Order Entry"]!);
        Assert.Equal("", (string)rels[1]!["modules"]!["alpha"]!);
        // modules sorted by name, case-insensitively
        Assert.Equal(["alpha", "Order Entry"], rels[1]!["modules"]!.AsObject().Select(kv => kv.Key));

        // and the result loads again
        var g2 = SampleGraph.Create();
        KnowledgeLoader.ApplyReleases(g2, p, []);
        Assert.Equal("1.2.3", g2.GetEdge(SampleGraph.Proj, "rel:2.0", "released_in")!.Str("version"));
    }

    [Fact]
    public void Release_snapshot_creates_the_file_and_defaults_the_date()
    {
        string p = Path.Combine(_dir, "new", "releases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        KnowledgeLoader.ReleaseSnapshot(SampleGraph.Create(), "3.0", null, p);
        var e = JsonNode.Parse(File.ReadAllText(p))!["releases"]![0]!;
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), (string)e["date"]!);
    }

    [Fact]
    public void Branch_releases()
    {
        var g = SampleGraph.Create();
        g.GetNode(SampleGraph.Proj)!["version"] = "1.2.3";
        g.Node("proj:Shop:b", ("kind", "project"), ("name", "alpha"), ("repo", "Shop"));
        g.Node("repo:Other", ("kind", "repo"), ("name", "Other"), ("branch", "main"));
        g.Node("proj:Other:p", ("kind", "project"), ("name", "p"), ("repo", "Other"));
        KnowledgeLoader.ApplyBranchReleases(g);
        var rel = g.GetNode("rel:2.4.0")!;
        Assert.Equal("from branch R2.4.0", rel.Str("status"));
        var e = g.GetEdge(SampleGraph.Proj, "rel:2.4.0", "released_in")!;
        Assert.Equal("1.2.3", e.Str("version"));
        Assert.Equal("branch", e.Str("via"));
        Assert.Null(g.GetEdge("proj:Shop:b", "rel:2.4.0", "released_in")!.Str("version"));
        Assert.Null(g.GetEdge("proj:Other:p", "rel:2.4.0", "released_in"));
    }

    [Fact]
    public void Test_projects_are_not_released_by_branch()
    {
        var g = SampleGraph.Create();
        g.Node("proj:Shop:Sales.Tests/Sales.Tests.csproj", ("kind", "project"), ("name", "Sales.Tests"), ("repo", "Shop"), ("test", true));
        KnowledgeLoader.ApplyBranchReleases(g);
        Assert.NotNull(g.GetEdge(SampleGraph.Proj, "rel:2.4.0", "released_in"));
        Assert.Null(g.GetEdge("proj:Shop:Sales.Tests/Sales.Tests.csproj", "rel:2.4.0", "released_in"));
    }

    [Fact]
    public void A_release_named_by_branch_and_releases_json_is_counted_once()
    {
        var g = SampleGraph.Create();
        g.GetNode(SampleGraph.Proj)!["version"] = "1.2.3";
        KnowledgeLoader.ApplyBranchReleases(g);
        string path = Path.Combine(_dir, "releases.json");
        File.WriteAllText(path, """{ "releases": [ { "version": "2.4.0", "modules": [ { "Order Entry": "1.2.4" } ] } ] }""");
        var warnings = new List<string>();
        KnowledgeLoader.ApplyReleases(g, path, warnings);
        var e = g.GetEdge(SampleGraph.Proj, "rel:2.4.0", "released_in")!;
        Assert.Equal(1, e.Count);
        Assert.Equal("1.2.4", e.Str("version"));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("R2.4.0", "2.4.0")]
    [InlineData("release/2.4", "2.4")]
    [InlineData("rel-1.2", "1.2")]
    [InlineData("v2.0.1", "2.0.1")]
    [InlineData("3.1", "3.1")]
    [InlineData("releases/v2.4.1", "2.4.1")]
    [InlineData("hotfix/2.4.1-rc1", "2.4.1")]
    [InlineData("release/2.4.0-hotfix", "2.4.0")]
    [InlineData("R2_4_0", "2.4.0")]
    [InlineData("release/2024-10", "2024.10")]
    [InlineData("release-35", "35")]
    [InlineData("support/3.x", null)]
    [InlineData("main", null)]
    [InlineData("R2", null)]
    [InlineData("v2", null)]
    [InlineData("feature/R2.4.0", null)]
    [InlineData("bugfix/123-crash", null)]
    public void Branch_names(string branch, string? version)
    {
        var g = new Graph();
        g.Node("repo:X", ("kind", "repo"), ("name", "X"), ("branch", branch));
        KnowledgeLoader.ApplyBranchReleases(g);
        Assert.Equal(version, g.Nodes.FirstOrDefault(n => n.Kind == "release")?.Name);
    }

    [Fact]
    public void Git_branch_follows_a_gitdir_file()
    {
        string repo = Path.Combine(_dir, "wt");
        string real = Path.Combine(_dir, "main-repo", ".git", "worktrees", "wt");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "HEAD"), "ref: refs/heads/release/3.1\n");
        File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: ../main-repo/.git/worktrees/wt\n");
        Assert.Equal("release/3.1", KnowledgeLoader.GitBranch(repo));
    }

    [Fact]
    public void Git_branch_is_read_from_head_file()
    {
        Assert.Null(KnowledgeLoader.GitBranch(_dir));
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        File.WriteAllText(Path.Combine(_dir, ".git", "HEAD"), "ref: refs/heads/R2.4.0\n");
        Assert.Equal("R2.4.0", KnowledgeLoader.GitBranch(_dir));
        File.WriteAllText(Path.Combine(_dir, ".git", "HEAD"), "ref: refs/heads/feature/x\n");
        Assert.Equal("feature/x", KnowledgeLoader.GitBranch(_dir));
        File.WriteAllText(Path.Combine(_dir, ".git", "HEAD"), "0123456789abcdef0123\n");
        Assert.Equal("0123456789ab", KnowledgeLoader.GitBranch(_dir));
    }

    [Fact]
    public void Shipped_example_files_load()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Examples");
        if (!Directory.Exists(dir)) return; // examples are copied by the main project
        var g = SampleGraph.Create();
        var warnings = new List<string>();
        KnowledgeLoader.ApplyReleases(g, Path.Combine(dir, "releases.json"), warnings);
        KnowledgeLoader.ApplyKnowledge(g, [Path.Combine(dir, "knowledge.json")], warnings);
        Assert.Empty(warnings);
    }
}
