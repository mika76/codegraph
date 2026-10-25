using CodeGraph.Model;
using CodeGraph.Scan;

namespace CodeGraph.Tests;

public class IgnoreTests
{
    [Theory]
    [InlineData("bin", "src/bin/x.cs", true)]
    [InlineData("*.Designer.cs", "src/Form1.Designer.cs", true)]
    [InlineData("Documentation", "a/documentation/x.sql", true)]
    [InlineData("Package/Help File CHM", "Package/Help File CHM/x.js", true)]
    [InlineData("Package/Help File CHM", "Other/Package/Help File CHM/x.js", false)]
    [InlineData("**/assets/pkg", "web/src/assets/pkg/a.js", true)]
    [InlineData("jquery*.js", "Scripts/jquery-3.1.js", true)]
    [InlineData("jquery*.js", "Scripts/app.js", false)]
    public void Matches(string pattern, string path, bool ignored) =>
        Assert.Equal(ignored, new IgnoreRules([pattern]).IsIgnored(path));

    [Theory]
    [InlineData("Scripts/jquery-3.4.1.js", true)]
    [InlineData("Scripts/jquery.validate.js", true)]
    [InlineData("js/react-dom.development.js", true)]
    [InlineData("js/vue.js", true)]
    [InlineData("Svc/Service References/Billing/Reference.cs", true)]
    [InlineData("src/reactions.js", false)]
    [InlineData("src/vuexStore.js", false)]
    [InlineData("src/momentum.js", false)]
    [InlineData("src/bootstrapper.js", false)]
    [InlineData("Models/Reference.cs", false)]
    [InlineData("rest-examples/orders.js", false)]
    public void Built_in_rules_skip_vendored_and_generated_files_only(string path, bool ignored) =>
        Assert.Equal(ignored, new IgnoreRules(IgnoreRules.BuiltIn).IsIgnored(path));

    [Theory]
    [InlineData("bin", "Orders.csproj", true)]
    [InlineData("obj", "Orders.vbproj", true)]
    [InlineData("Debug", "Native.vcxproj", true)]
    [InlineData("x64", "All.sln", true)]
    [InlineData("packages", "All.sln", true)]
    [InlineData("bin", "package.json", false)]
    [InlineData("Release", "README.md", false)]
    [InlineData("Release", "Orders.csproj", false)]
    [InlineData("packages", "package.json", false)]
    [InlineData("src", "Orders.csproj", false)]
    public void Build_output_only_next_to_what_builds_it(string dir, string sibling, bool output) =>
        Assert.Equal(output, IgnoreRules.IsBuildOutput(dir, [sibling]));

    [Fact]
    public void Negation_reincludes()
    {
        var rules = new IgnoreRules(["Vendor", "!Vendor"]);
        Assert.False(rules.IsIgnored("Vendor/x.js"));
    }

    [Theory]
    [InlineData("abc", "a?c", true)]
    [InlineData("abc", "a[bx]c", true)]
    [InlineData("abc", "a[!b]c", false)]
    [InlineData("a/b/c", "a*c", true)]
    [InlineData("x.bak", "*.orig", false)]
    public void Glob_matches(string s, string pattern, bool expected) => Assert.Equal(expected, Glob.Match(s, pattern));
}

public class ScannerTests
{
    [Fact]
    public void Source_folders_named_like_build_output_are_read()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            void Put(string rel, string text = "x")
            {
                string p = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, text);
            }
            Put("packages/ui/src/button.ts", "export const a = 1;");   // JS monorepo
            Put("Billing/Release/Schedule.cs", "class Schedule {}");   // a domain folder
            Put("tools/bin/run.ps1", "Write-Host 1");                  // scripts, no project here
            Put("Api/Api.csproj", "<Project />");
            Put("Api/bin/Debug/Api.cs", "class Old {}");               // build output
            Put("Native/Native.vcxproj", "<Project />");
            Put("Native/Release/gen.cpp", "int x;");                    // build output
            var cfg = new Config { Output = Path.Combine(root, "out"), Repos = [new RepoConfig { Name = "R", Path = root }] };
            var rels = Scanner.Discover(cfg).Select(f => f.Rel).ToList();
            Assert.Contains("packages/ui/src/button.ts", rels);
            Assert.Contains("Billing/Release/Schedule.cs", rels);
            Assert.Contains("tools/bin/run.ps1", rels);
            Assert.DoesNotContain("Api/bin/Debug/Api.cs", rels);
            Assert.DoesNotContain("Native/Release/gen.cpp", rels);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Discovers_parseable_files_and_caches_results()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "src", "bin"));
        Directory.CreateDirectory(Path.Combine(root, "Docs"));
        File.WriteAllText(Path.Combine(root, "src", "a.sql"), "select 1");
        File.WriteAllText(Path.Combine(root, "src", "app.sqlproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "src", "readme.txt"), "x");
        File.WriteAllText(Path.Combine(root, "src", "bin", "b.cs"), "class B {}");
        File.WriteAllText(Path.Combine(root, "Docs", "c.cs"), "class C {}");
        File.WriteAllText(Path.Combine(root, ".codegraphignore"), "Docs\n");
        var cfg = new Config { Output = Path.Combine(root, "out"), Repos = [new RepoConfig { Name = "R", Path = root }] };

        var files = Scanner.Discover(cfg);
        Assert.Equal(["src/a.sql", "src/app.sqlproj"], files.Select(f => f.Rel));

        var (records, errors) = Scanner.ParseAll(cfg, files, verbose: false);
        Assert.Empty(errors);
        Assert.Equal(2, records.Count);
        Assert.True(File.Exists(Path.Combine(cfg.Output, Scanner.CacheFileName)));

        var (again, _) = Scanner.ParseAll(cfg, files, verbose: false);
        Assert.Equal(records[0].Facts.Lang, again[0].Facts.Lang);
    }

    [Fact]
    public void Facts_extra_fields_round_trip_through_cache_json()
    {
        var facts = new FileFacts { Lang = "csharp", Loc = 3 };
        facts.X["usings"] = new System.Text.Json.Nodes.JsonArray("System", "System.Linq");
        facts.Symbols.Add(new Symbol { Name = "A", Kind = "class", Line = 1, Parent = null });
        facts.Refs.Add(new Ref { Kind = "call", Name = "Foo", FromParent = "A", Line = 2 });
        var file = new CacheFile { Version = 1, Files = { ["x"] = new CacheEntry { Facts = facts } } };
        string json = System.Text.Json.JsonSerializer.Serialize(file, CacheJson.Default.CacheFile);
        Assert.Contains("\"from_parent\":\"A\"", json);
        Assert.Contains("\"usings\":[\"System\",\"System.Linq\"]", json);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, CacheJson.Default.CacheFile)!;
        var f = back.Files["x"].Facts;
        Assert.Equal("Foo", f.Refs[0].Name);
        Assert.Equal(2, f.Extra!["usings"]!.AsArray().Count);
    }
}
