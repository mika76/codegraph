using System.Text.Json.Nodes;
using CodeGraph.Model;
using CodeGraph.Parsers;

namespace CodeGraph.Tests;

public class CSharpParserTests
{
    private const string Controller = """
        using System;
        using static Foo.Bar;
        using Alias = Baz.Qux;
        [assembly: AssemblyVersion("1.2.3")]
        namespace Shop.Api.Controllers
        {
            [Route("/inventory")]
            public class SlotController : ControllerBase, ISlot<int>
            {
                private readonly IItemRepository _repo;
                public SlotController(IItemRepository repo) { _repo = repo; }

                [HttpGet][Route("/inventory/items")]
                public IActionResult Items(int id)
                {
                    var data = _repo.GetAll(id);
                    var r = new Result();
                    return Ok(data);
                }

                [HttpPost("save")]
                public void Save() { }

                [HttpPost("save")]
                public void Save(int x) { }

                [NonAction]
                public void Hidden() { }

                private void Helper() { Items(1); }

                public string Sql() => "SELECT * FROM Orders WHERE Id = 1";
            }
        }
        """;

    private static Symbol Sym(FileFacts f, string name) => f.Symbols.First(s => s.Name == name);

    [Fact]
    public void Types_namespace_usings_attrs()
    {
        var f = CSharpParser.Parse("a.cs", Controller);
        Assert.Equal("csharp", f.Lang);
        Assert.Equal("Shop.Api.Controllers", f.X["namespace"]!.GetValue<string>());
        Assert.Equal(["Baz.Qux", "Foo.Bar", "System"], f.X["usings"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("1.2.3", f.X["attrs"]!["assemblyversion"]!.GetValue<string>());
        var t = Sym(f, "SlotController");
        Assert.Equal("class", t.Kind);
        Assert.Equal(["ControllerBase", "ISlot"], t.X["bases"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(f.Refs, r => r is { Kind: "inherit", Name: "ISlot", FromParent: "SlotController" });
    }

    [Fact]
    public void Attribute_routes()
    {
        var f = CSharpParser.Parse("a.cs", Controller);
        var m = Sym(f, "Items");
        Assert.Equal("method", m.Kind);
        Assert.Equal("SlotController", m.Parent);
        var route = m.X["routes"]!.AsArray().Single()!;
        Assert.Equal("get", route["verb"]!.GetValue<string>());
        Assert.Equal("inventory/items", route["path"]!.GetValue<string>());
        Assert.False(route["conventional"]!.GetValue<bool>());

        var save = Sym(f, "Save");
        Assert.Equal(2, save.X["overloads"]!.GetValue<int>());
        var routes = save.X["routes"]!.AsArray();
        Assert.Equal(2, routes.Count);
        Assert.Equal("inventory/save", routes[0]!["path"]!.GetValue<string>());

        Assert.Null(Sym(f, "Hidden").X["routes"]);
        Assert.Null(Sym(f, "Helper").X["routes"]);
        Assert.Equal("SlotController", f.Symbols.Single(s => s.Kind == "constructor").Parent);
    }

    [Fact]
    public void Conventional_route_for_mvc_controller()
    {
        var f = CSharpParser.Parse("h.cs", "public class HomeController : Controller { public ActionResult Index() { return View(); } }");
        var r = Sym(f, "Index").X["routes"]!.AsArray().Single()!;
        Assert.Equal("home/index", r["path"]!.GetValue<string>());
        Assert.True(r["conventional"]!.GetValue<bool>());
        Assert.Null(r["verb"]);
    }

    [Fact]
    public void Member_call_types_and_sql()
    {
        var f = CSharpParser.Parse("a.cs", Controller);
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "_repo", Name: "GetAll", QualType: "IItemRepository", From: "Items" });
        Assert.Contains(f.Refs, r => r is { Kind: "new", Name: "Result", From: "Items" });
        Assert.Contains(f.Refs, r => r is { Kind: "call", Name: "Items", From: "Helper" });
        Assert.Contains(f.Refs, r => r is { Kind: "sql", Mode: "read", Name: "orders", From: "Sql" });
        Assert.Contains(f.Refs, r => r is { Kind: "type", Name: "IItemRepository", FromParent: "SlotController", From: null });
    }

    [Fact]
    public void Nested_types_and_enum_and_interface()
    {
        const string src = """
            namespace N;
            public interface IFoo { void Do(); }
            public class Outer { public class Inner { public void M() {} } }
            public enum Color { Red, Green }
            public record Point(int X, int Y);
            public record class Rec(int A) : Base { }
            """;
        var f = CSharpParser.Parse("a.cs", src);
        Assert.Equal("N", f.X["namespace"]!.GetValue<string>());
        Assert.Equal("interface", Sym(f, "IFoo").Kind);
        Assert.Equal("public", Sym(f, "Do").Visibility);
        Assert.Equal("Outer.Inner", Sym(f, "Outer.Inner").Name);
        Assert.Equal("Outer.Inner", Sym(f, "M").Parent);
        Assert.Equal("enum", Sym(f, "Color").Kind);
        Assert.Equal("record", Sym(f, "Rec").Kind);
        Assert.Contains(f.Refs, r => r is { Kind: "inherit", Name: "Base" });
    }

    [Fact]
    public void Literal_identifier_and_line_numbers()
    {
        const string src = "class A\n{\n    void M()\n    {\n        Run(\"bill___Customer_Get\");\n    }\n}\n";
        var f = CSharpParser.Parse("a.cs", src);
        var m = Sym(f, "M");
        Assert.Equal(3, m.Line);
        Assert.Equal(4, m.Lines);
        Assert.Contains(f.Refs, r => r is { Kind: "literal", Name: "bill___customer_get", Line: 5, From: "M" });
        Assert.Equal(8, f.Loc);
    }

    [Fact]
    public void Methods_after_region_and_other_directives_are_found()
    {
        var f = CSharpParser.Parse("Client.cs", """
            public class Client
            {
                #region Today Revenue
                public async Task<decimal> GetToday() { return await Get("api/today"); }
                #endregion
                #region Yesterday's revenue (old API) - v2.1
                public decimal GetYesterday() => 0;
                #endregion
                #endregion
                #region Last week
                private static int LastWeek(int n) { return n; }
                #endregion
            #if DEBUG
                public void Dump() { }
            #endif
                #pragma warning disable CS0618
                public void Old() { }
            }
            """);
        var names = f.Symbols.Where(s => s.Parent == "Client").Select(s => s.Name).ToList();
        Assert.Equal(["GetToday", "GetYesterday", "LastWeek", "Dump", "Old"], names);
        Assert.Equal(4, f.Symbols.Single(s => s.Name == "GetToday").Line);
    }

    [Fact]
    public void HttpClient_calls_are_endpoint_candidates()
    {
        var f = CSharpParser.Parse("ApiClient.cs", """
            class ApiClient
            {
                async Task<decimal> GetMonthRevenue(int site)
                {
                    string query = $"api/Stats/MonthRevenue/{site}";
                    var response = await client.GetAsync(query);
                    return 0;
                }
                async Task Save(Order o) { await client.PostAsJsonAsync("/orders", o); }
                string Title() { return "Reports/Monthly"; }                       // no HTTP call here
                async Task Send() { var c = new StringContent(x, Encoding.UTF8, "application/json"); await client.SendAsync(req); }
            }
            """);
        var eps = f.Refs.Where(r => r.Kind == "endpoint").ToList();
        var month = eps.Single(r => r.From == "GetMonthRevenue");
        Assert.Equal("api/stats/monthrevenue/{}", month.Name);
        Assert.Equal("get", (string)month.X["verb"]!);
        Assert.True(month.X.ContainsKey("candidate"));
        Assert.Equal("post", (string)eps.Single(r => r.From == "Save").X["verb"]!);
        Assert.DoesNotContain(eps, r => r.From == "Title");
        Assert.All(eps.Where(r => r.From == "Send"), r => Assert.False(r.X.ContainsKey("verb"))); // SendAsync: verb unknown
    }

    [Fact]
    public void DllImport_externs_are_native_calls()
    {
        var f = CSharpParser.Parse("Native.cs", """
            using System.Runtime.InteropServices;
            static class Native
            {
                [DllImport("Rand.dll")]
                public static extern uint getNextNumber();
                [DllImport("Licence.dll", EntryPoint = "CheckLic", CharSet = CharSet.Ansi)]
                internal static extern int Check(string key);
                [System.Runtime.InteropServices.DllImport(@"kernel32.dll")] static extern int GetTickCount();
                [DllImport("Licence.dll", EntryPoint = "HashEx", CharSet = CharSet.Ansi)]
                [return: MarshalAs(UnmanagedType.AnsiBStr)]
                private static extern unsafe string Hash([MarshalAs(UnmanagedType.AnsiBStr)] string data, int len);
            }
            """);
        Assert.Contains(f.Refs, r => r is { Kind: "native", From: "Hash", Name: "HashEx" });
        // a commented-out import is no call, and does not borrow the next real declaration
        var c = CSharpParser.Parse("C.cs", "class C {\n // [DllImport(\"Old.dll\")]\n [DllImport(\"New.dll\")] static extern int Go();\n}");
        Assert.Equal(["New.dll"], c.Refs.Where(r => r.Kind == "native").Select(r => (string)r.X["lib"]!));
        var natives = f.Refs.Where(r => r.Kind == "native").ToList();
        Assert.Contains(natives, r => r is { From: "getNextNumber", FromParent: "Native", Name: "getNextNumber" } && (string)r.X["lib"]! == "Rand.dll");
        Assert.Contains(natives, r => r is { From: "Check", Name: "CheckLic" } && (string)r.X["lib"]! == "Licence.dll");
        Assert.Contains(natives, r => r is { From: "GetTickCount" } && (string)r.X["lib"]! == "kernel32.dll");
        Assert.Contains(f.Symbols, s => s.Name == "getNextNumber" && s.Parent == "Native");
    }

    [Fact]
    public void Generic_call_arguments_are_type_uses()
    {
        var f = CSharpParser.Parse("Startup.cs", """
            class Startup
            {
                void Register(IServiceCollection services, int a, int b, int c)
                {
                    services.AddScoped<IOrderRepo, OrderRepo>();
                    var list = conn.Query<OrderRow>(sql);
                    if (a < b && c > (a)) { }
                    var n = Get<int>();
                }
            }
            """);
        var types = f.Refs.Where(r => r is { Kind: "type", From: "Register" }).Select(r => r.Name).ToList();
        Assert.Contains("IOrderRepo", types);
        Assert.Contains("OrderRepo", types);
        Assert.Contains("OrderRow", types);
        Assert.DoesNotContain("int", types);
        Assert.DoesNotContain(types, t => t.Contains("&&") || t == "b" || t == "c");
    }

    [Theory]
    [InlineData("Order", "orders")]   // the usual style
    [InlineData("order", "Orders")]   // lower-case type, capitalised field
    [InlineData("order", "_orders")]  // any naming
    public void Types_and_members_are_found_whatever_the_casing(string type, string field)
    {
        string src = $$"""
            class {{type}} { public int Total; public static {{type}} Empty; }
            class Repo
            {
                {{type}} {{field}};
                int Sum(object o, bool ready)
                {
                    {{type}} x = new {{type}}();
                    var y = ({{type}})o;
                    if (ready) Log(x);
                    var t = typeof({{type}});
                    var e = {{type}}.Empty;
                    return {{field}}.Total + x.Total;
                }
            }
            """;
        var f = CSharpParser.Parse("repo.cs", src);
        var sum = f.Refs.Where(r => r.From == "Sum").ToList();
        Assert.Contains(sum, r => r is { Kind: "type" } && r.Name == type);
        Assert.Contains(sum, r => r is { Kind: "member", Name: "Empty" } && r.Qual == type);
        Assert.Contains(sum, r => r is { Kind: "member", Name: "Total" } && r.Qual == field && r.QualType == type);
        Assert.Contains(sum, r => r is { Kind: "member", Name: "Total", Qual: "x" } && r.QualType == type);
        Assert.Contains(f.Refs, r => r is { Kind: "type", FromParent: "Repo", From: null } && r.Name == type);
        // keywords and parenthesised values are not types
        Assert.DoesNotContain(f.Refs, r => r.Kind == "type" && (r.Name is "ready" or "o" or "int" or "object" or "bool" or "var"));
        Assert.DoesNotContain(f.Refs, r => r.Kind == "member" && r.Qual is "int" or "string");
    }
}
