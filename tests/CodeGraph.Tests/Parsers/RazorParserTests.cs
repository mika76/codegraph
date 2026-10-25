using CodeGraph.Model;
using CodeGraph.Parsers;

namespace CodeGraph.Tests.Parsers;

public class RazorParserTests
{
    [Fact]
    public void MvcWiring()
    {
        const string cshtml = """
            @model IEnumerable<My.Ns.Item>
            @* @Url.Action("Hidden","X") *@
            <a href="@Url.Action("Edit", "Items", new { id = 1 })">e</a>
            @Html.ActionLink("Text", "Details", new { id = 2 })
            @using (Html.BeginForm("Save", "Items")) { }
            @Html.Partial("_Row")
            <partial name="_Foot" />
            <a asp-controller="Reports" asp-action="Show">r</a>
            <a asp-action="Dyn@x">d</a>
            """;
        var f = RazorParser.Parse("WebPortal/Views/Home/Index.cshtml", cshtml);
        Assert.Equal("razor", f.Lang);
        var view = f.Symbols[0];
        Assert.Equal("Index", view.Name);
        Assert.Equal("view", view.Kind);
        Assert.Equal("Home", view.X["controller"]!.GetValue<string>());
        Assert.All(f.Refs, r => Assert.Equal("Index", r.FromParent));
        Assert.Equal(["IEnumerable", "Item"], f.Refs.Where(r => r.Kind == "cs_type").Select(r => r.Name));
        var eps = f.Refs.Where(r => r.Kind == "endpoint").Select(r => r.Name).ToList();
        Assert.Equal(["items/edit", "home/details", "items/save", "reports/show"], eps);
        Assert.True(f.Refs.First(r => r.Kind == "endpoint").X["conventional"]!.GetValue<bool>());
        Assert.Equal(3, f.Refs.First(r => r.Kind == "endpoint").Line);
        Assert.Equal(["_Row", "_Foot"], f.Refs.Where(r => r.Kind == "partial").Select(r => r.Name));
        // <a href>, ActionLink and <a asp-action> navigate; the form posts
        var nav = f.Refs.Where(r => r.Kind == "endpoint" && r.X.ContainsKey("navigation")).Select(r => r.Name).ToList();
        Assert.Equal(["items/edit", "home/details", "reports/show"], nav);
    }

    [Fact]
    public void Ajax_and_script_urls_are_calls_links_are_navigation()
    {
        const string cshtml = """
            <a href="@Url.Action("Index", "Main", new { id = 1 })">Dashboard</a>
            <script>
              $.ajax({ url: '@Url.Action("GetData", "Live")', type: 'POST' });
              fetch('@Url.Action("Stats", "Live")');
              window.location.href = '@Url.Action("Report", "Live")';
              $('#x').attr('href', '@Url.Action("Help", "Main")');
            </script>
            <a href="@Url.Action("GetData", "Live")">raw data</a>
            """;
        var f = RazorParser.Parse("Web/Views/Main/Page.cshtml", cshtml);
        bool Nav(string ep) => f.Refs.Single(r => r.Kind == "endpoint" && r.Name == ep).X.ContainsKey("navigation");
        Assert.True(Nav("main/index"));
        Assert.True(Nav("live/report"));
        Assert.True(Nav("main/help"));
        Assert.False(Nav("live/stats"));
        Assert.False(Nav("live/getdata")); // ajax-called, and also linked: the call wins
    }

    [Fact]
    public void SharedViewHasNoDefaultController()
    {
        var f = RazorParser.Parse("Views/Shared/_Layout.cshtml", "@Url.Action(\"Index\")\n@Url.Action(\"Index\", \"Home\")");
        Assert.Equal(["home/index"], f.Refs.Where(r => r.Kind == "endpoint").Select(r => r.Name));
    }

    [Fact]
    public void EmbeddedScript_IsBlankedAndParentsFilled()
    {
        string? seen = null;
        var old = RazorParser.ScriptParser;
        RazorParser.ScriptParser = (p, t, l) =>
        {
            seen = t;
            var js = new FileFacts { Lang = "javascript" };
            js.Symbols.Add(new Symbol { Name = "go", Kind = "function", Line = 3 });
            js.Refs.Add(new Ref { From = "go", Kind = "endpoint", Name = "/a", Line = 3 });
            return js;
        };
        try
        {
            const string v = "<p>@Model.X</p>\n<script>\nvar a = @Html.Raw(Json.Encode(Model));\nif (@(x)) { go(); }\n</script>\n<script type=\"text/template\">skip()</script>";
            var f = RazorParser.Parse("Views/Home/Index.cshtml", v);
            Assert.NotNull(seen);
            Assert.Equal(v.Length, seen!.Length);
            Assert.DoesNotContain("Html", seen);
            Assert.DoesNotContain("skip", seen);
            Assert.DoesNotContain("Model", seen);
            Assert.Contains("var a =", seen);
            Assert.Contains("go();", seen);
            Assert.Equal("Index", f.Symbols.Single(s => s.Name == "go").Parent);
            Assert.Equal("Index", f.Refs.Single(r => r.Name == "/a").FromParent);
        }
        finally { RazorParser.ScriptParser = old; }
    }
}
