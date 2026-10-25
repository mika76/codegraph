using CodeGraph.Model;
using CodeGraph.Parsers;

namespace CodeGraph.Tests;

public class CppParserTests
{
    [Fact]
    public void Def_file_exports()
    {
        const string def = "LIBRARY RandLib\r\n; comment\r\nEXPORTS\r\n  RandInit @1\r\n  RandNext=Impl_Next ; x\r\n\r\nSECTIONS\r\n  .data READ\r\n";
        var f = CppParser.Parse("RandLib.def", def);
        Assert.Equal("cpp", f.Lang);
        Assert.Equal(["RandInit", "RandNext"], f.X["exports"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    private const string Src = """

        #include <windows.h>
        #include "sub\helper.h"

        class Base { };
        class Rng : public Base, private Other<int>
        {
        public:
            int Next(int x) const { return Helper::Calc(x) + Step(); }
        };

        int Rng::Reset() { Rng r; new Foo(); return 0; }

        extern "C" {
        __declspec(dllexport) int __stdcall RandInit(int seed)
        {
            const char* q = "SELECT * FROM Seeds";
            return seed;
        }
        }

        namespace acme {
        void Free() { if (x) { Other(); } }
        }

        #define MAKE_THING(x) x
        MAKE_THING(foo)
        """;

    [Fact]
    public void Types_functions_and_exports()
    {
        var f = CppParser.Parse("rng.cpp", Src);
        var rng = f.Symbols.Single(s => s.Name == "Rng" && s.Kind == "class");
        Assert.Equal(["Base", "Other"], rng.X["bases"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(f.Refs, r => r is { Kind: "inherit", Name: "Other", FromParent: "Rng" });

        var next = f.Symbols.Single(s => s.Name == "Next");
        Assert.Equal("method", next.Kind);
        Assert.Equal("Rng", next.Parent);
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "Helper", Name: "Calc", QualType: "Helper", From: "Next" });
        Assert.Contains(f.Refs, r => r is { Kind: "call", Name: "Step", From: "Next" });

        var reset = f.Symbols.Single(s => s.Name == "Reset");
        Assert.Equal("Rng", reset.Parent);
        Assert.Contains(f.Refs, r => r is { Kind: "new", Name: "Foo", From: "Reset" });

        var init = f.Symbols.Single(s => s.Name == "RandInit");
        Assert.Equal("function", init.Kind);
        Assert.True(init.X["exported"]!.GetValue<bool>());
        Assert.Equal(15, init.Line);

        Assert.Equal("function", f.Symbols.Single(s => s.Name == "Free").Kind);
        Assert.DoesNotContain(f.Symbols, s => s.Name == "MAKE_THING");
    }

    [Fact]
    public void Upper_case_functions_are_functions_and_macro_uses_are_not()
    {
        var f = CppParser.Parse("dev.c", """
            #define WRAP_CALL(x) do_call(x)
            void INIT_DEVICE(void) { WRAP_CALL(1); }
            static int OS_START(int n) { return n; }
            static WRAP_CALL(2) { }
            TEST_F(DeviceSuite, Starts) { INIT_DEVICE(); }
            class Port {
                Port() { }
                ~Port() { }
                DECLARE_HANDLERS() { }
                int Read() { return 0; }
            };
            """);
        var names = f.Symbols.Select(s => s.Name).ToList();
        Assert.Contains("INIT_DEVICE", names);
        Assert.Contains("OS_START", names);
        Assert.Contains("Read", names);
        Assert.Contains("Port", names);
        Assert.DoesNotContain("WRAP_CALL", names);
        Assert.DoesNotContain("TEST_F", names);
        Assert.DoesNotContain("DECLARE_HANDLERS", names);
    }

    [Fact]
    public void Scope_qualified_calls_name_the_type_whatever_its_casing()
    {
        var f = CppParser.Parse("x.cpp", """
            void Run(Widget Panel, widget* w) {
                util::helper(1);
                Helper::Calc(2);
                Panel.Draw();
                w->draw();
            }
            """);
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "util", QualType: "util", Name: "helper" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "Helper", QualType: "Helper", Name: "Calc" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "Panel", QualType: null, Name: "Draw" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "w", QualType: null, Name: "draw" });
    }

    [Fact]
    public void Includes_and_sql()
    {
        var f = CppParser.Parse("rng.cpp", Src);
        var inc = f.Refs.Where(r => r.Kind == "include").ToList();
        Assert.Equal(["windows.h", "helper.h"], inc.Select(r => r.Name));
        Assert.Equal("sub/helper.h", inc[1].X["path"]!.GetValue<string>());
        Assert.Contains(f.Refs, r => r is { Kind: "sql", Mode: "read", Name: "seeds" });
    }
}
