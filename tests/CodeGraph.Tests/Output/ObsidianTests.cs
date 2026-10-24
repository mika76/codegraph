using CodeGraph.Output;

namespace CodeGraph.Tests.Output;

public sealed class ObsidianTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-vault-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Config Cfg(string folder = "") => new() { Vault = _dir, Obsidian = new ObsidianConfig { Folder = folder } };

    [Fact]
    public void Safe_names()
    {
        Assert.Equal("a_b_c", Obsidian.SafeName("a/b:c"));
        Assert.Equal("_CON", Obsidian.SafeName("CON"));
        Assert.Equal("_nul.txt", Obsidian.SafeName("nul.txt"));
        Assert.Equal("x", Obsidian.SafeName("x. "));
        Assert.Equal("_", Obsidian.SafeName("..."));
        Assert.Equal(100, Obsidian.SafeName(new string('a', 300)).Length);
        Assert.Equal("a b", Obsidian.LabelOf("a|b [] "));
    }

    [Fact]
    public void Writes_notes_and_is_idempotent()
    {
        var g = SampleGraph.Create();
        var r1 = Obsidian.WriteVault(g, Cfg(), "# Codegraph Report\n");
        Assert.True(r1.Written > 0);
        Assert.Equal(0, r1.Unchanged);
        Assert.True(File.Exists(Path.Combine(_dir, "Home.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "Codegraph Report.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "Projects", "Order Entry.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "Code", "Shop", "Sales", "frmMain.frm.md")));
        Assert.True(File.Exists(Path.Combine(_dir, "Database", "Tables", "Orders.md")));
        Assert.True(File.Exists(Path.Combine(_dir, ".codegraph-manifest.json")));
        Assert.True(File.Exists(Path.Combine(_dir, ".obsidian", "graph.json")));

        var proj = File.ReadAllText(Path.Combine(_dir, "Projects", "Order Entry.md"));
        Assert.StartsWith("---\ntype: \"project\"\nrepo: \"Shop\"\nlang: \"vb6\"\nversion: \"1.2.3\"\nptype: \"exe\"\noutput: \"OrderEntry.exe\"\nloc: 150\n" +
                          "tags: [codegraph/project, lang/vb6]\n---\n\n# Order Entry\n", proj);
        Assert.Contains("[[Code/Shop/Sales/frmMain.frm|frmMain.frm]] - 100 LOC", proj);
        Assert.Contains("> [!warning] 1 listed member file(s) were not found on disk:", proj);
        Assert.Contains(@"| [[Database/Tables/Orders\|Orders]] | table | 2 |", proj);
        Assert.Contains("[[Home|Shop]]", proj);

        var table = File.ReadAllText(Path.Combine(_dir, "Database", "Tables", "Orders.md"));
        Assert.Contains("| Id | int not null |", table);
        Assert.Contains("### [[Projects/Order Entry|Order Entry]]", table);
        Assert.Contains("- [[Code/Shop/Sales/frmMain.frm|frmMain.frm]] - writes x2", table);
        Assert.Contains("- [[Database/Procedures/usp_Save|usp_Save]] - writes", table);
        Assert.Contains("SQL table with 2 columns, used by 1 file(s) and 1 database object(s).", table);
        Assert.Contains("tags: [codegraph/table, lang/sql]", table);

        var frm = File.ReadAllText(Path.Combine(_dir, "Code", "Shop", "Sales", "frmMain.frm.md"));
        Assert.Contains("- **frmMain.Save** (sub, L10, 5 lines) `Private Sub Save()` - calls: [[Code/Shop/Sales/modDb.bas|modDb.Load]]; writes: [[Database/Tables/Orders|Orders]]", frm);
        Assert.Contains("**Writes**\n\n- [[Database/Tables/Orders|Orders]] x2", frm);

        var orphan = File.ReadAllText(Path.Combine(_dir, "Code", "Shop", "Sales", "Old.bas.md"));
        Assert.Contains("orphan: true", orphan);
        Assert.Contains("- Not in any project (orphan source file)", orphan);

        var home = File.ReadAllText(Path.Combine(_dir, "Home.md"));
        Assert.Contains("# Codebase map", home);
        Assert.Contains("- **Shop** - 3 files (`C:/x/Shop`)", home);
        Assert.Contains("Most used tables (by distinct files):", home);
        Assert.EndsWith("`[[Orders]]`.\n", home);

        var r2 = Obsidian.WriteVault(g, Cfg(), "# Codegraph Report\n");
        Assert.Equal(0, r2.Written);
        Assert.Equal(0, r2.Deleted);
        Assert.Equal(r1.Notes, r2.Unchanged);
        Assert.Equal($"{{'written': 0, 'unchanged': {r1.Notes}, 'deleted': 0, 'notes': {r1.Notes}}}", r2.ToString());
    }

    [Fact]
    public void Rewrites_only_changed_notes_and_keeps_user_notes()
    {
        var g = SampleGraph.Create();
        Obsidian.WriteVault(g, Cfg(), null);
        Directory.CreateDirectory(Path.Combine(_dir, "Notes"));
        File.WriteAllText(Path.Combine(_dir, "Notes", "mine.md"), "my own thoughts");
        File.WriteAllText(Path.Combine(_dir, "Home2.md"), "also mine");
        // a user's own graph colours are kept
        File.WriteAllText(Path.Combine(_dir, ".obsidian", "graph.json"), "{}");

        g.GetNode(SampleGraph.Proj)!["version"] = "9.9.9";
        var r = Obsidian.WriteVault(g, Cfg(), null);
        Assert.Equal(3, r.Written); // project note, its area note and Home (project table)
        Assert.Equal(0, r.Deleted);
        Assert.Equal("my own thoughts", File.ReadAllText(Path.Combine(_dir, "Notes", "mine.md")));
        Assert.Equal("also mine", File.ReadAllText(Path.Combine(_dir, "Home2.md")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dir, ".obsidian", "graph.json")));
        Assert.Contains("version: \"9.9.9\"", File.ReadAllText(Path.Combine(_dir, "Projects", "Order Entry.md")));
    }

    [Fact]
    public void Removes_notes_of_deleted_nodes_and_empty_folders_only()
    {
        var g = SampleGraph.Create();
        Obsidian.WriteVault(g, Cfg(), null);
        string orphan = Path.Combine(_dir, "Code", "Shop", "Sales", "Old.bas.md");
        Assert.True(File.Exists(orphan));
        g.RemoveNode(SampleGraph.FOrphan);
        // a user note placed next to generated ones in the same folder must stay, with its folder
        File.WriteAllText(Path.Combine(_dir, "Code", "Shop", "Sales", "my notes.md"), "keep");
        var r = Obsidian.WriteVault(g, Cfg(), null);
        Assert.Equal(1, r.Deleted);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(Path.Combine(_dir, "Code", "Shop", "Sales", "my notes.md")));

        // removing the whole database leaves no empty Database folder behind
        foreach (var id in new[] { "db:Orders", "db:usp_Save", "db:Unused" }) g.RemoveNode(id);
        Obsidian.WriteVault(g, Cfg(), null);
        Assert.False(Directory.Exists(Path.Combine(_dir, "Database")));
        Assert.True(File.Exists(Path.Combine(_dir, "Home.md")));
    }

    [Fact]
    public void Never_deletes_outside_the_vault_even_with_a_bad_manifest()
    {
        string outside = Path.Combine(Path.GetTempPath(), "cg-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "precious");
        try
        {
            Directory.CreateDirectory(_dir);
            string rel = "../" + Path.GetFileName(outside);
            File.WriteAllText(Path.Combine(_dir, ".codegraph-manifest.json"), "{\"files\": [\"" + rel + "\"]}");
            Obsidian.WriteVault(SampleGraph.Create(), Cfg(), null);
            Assert.True(File.Exists(outside));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Folder_option_puts_everything_in_a_subfolder_and_links_include_it()
    {
        Obsidian.WriteVault(SampleGraph.Create(), Cfg("Generated/code"), null);
        Assert.True(File.Exists(Path.Combine(_dir, "Generated", "code", "Home.md")));
        var proj = File.ReadAllText(Path.Combine(_dir, "Generated", "code", "Projects", "Order Entry.md"));
        Assert.Contains("[[Generated/code/Code/Shop/Sales/frmMain.frm|frmMain.frm]]", proj);
        Assert.False(File.Exists(Path.Combine(_dir, "Home.md")));
    }

    [Fact]
    public void Colliding_note_names_get_a_number_and_custom_relations_show_up()
    {
        var g = SampleGraph.Create();
        g.Node("concept:orders", ("kind", "concept"), ("name", "Orders"));
        g.AddEdge("concept:orders", "db:Orders", "is_part_of", attrs: [("custom", System.Text.Json.Nodes.JsonValue.Create(true)), ("label", "is part of"), ("note", "why")]);
        var notes = Obsidian.Render(g, Cfg());
        Assert.Contains("Concepts/Orders.md", notes.Keys);
        Assert.Contains("- **is part of** -> [[Database/Tables/Orders|Orders]] - why", notes["Concepts/Orders.md"]);
        Assert.Contains("- [[Concepts/Orders|Orders]] **is part of** -> this - why", notes["Database/Tables/Orders.md"]);

        g.Node("db:orders2", ("kind", "db_table"), ("name", "orders"));
        notes = Obsidian.Render(g, Cfg());
        Assert.Contains("Database/Tables/orders (2).md", notes.Keys);
    }

    [Fact]
    public void No_vault_is_an_error()
    {
        Assert.Throws<CodeGraphException>(() => Obsidian.WriteVault(SampleGraph.Create(), new Config(), null));
    }
}
