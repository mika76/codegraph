using CodeGraph.Model;
using CodeGraph.Parsers;

namespace CodeGraph.Tests;

public class RustParserTests
{
    private const string Src = """
        use std::collections::{HashMap, hash_map::Entry as E};
        use crate::util::*;

        #[wasm_bindgen]
        pub struct Counter { n: u32 }

        pub trait Speak: Debug + Send { fn say(&self); }

        #[wasm_bindgen]
        impl Counter {
            pub fn new() -> Counter { Counter { n: 0 } }
            pub fn bump(&mut self) { self.helper(); util::run(); Other::make(); self.n.checked_add(1); }
            fn helper(&self) {}
        }

        impl Speak for Counter {
            fn say(&self) { let q = "SELECT * FROM Orders"; let l: &'static str = r#"x"#; }
        }

        pub(crate) mod inner { pub fn deep() {} }

        enum Mode { A, B }
        fn free<'a>(x: &'a str) -> impl Fn() { run(); }
        """;

    private static Symbol Sym(FileFacts f, string name, string? parent = null) =>
        f.Symbols.Single(s => s.Name == name && s.Parent == parent);

    [Fact]
    public void Symbols_and_visibility()
    {
        var f = RustParser.Parse("lib.rs", Src);
        Assert.Equal("rust", f.Lang);
        var c = Sym(f, "Counter");
        Assert.Equal("struct", c.Kind);
        Assert.True(c.X["exported"]!.GetValue<bool>());
        Assert.Equal("interface", Sym(f, "Speak").Kind);
        Assert.Equal(["Debug", "Send"], Sym(f, "Speak").X["bases"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("enum", Sym(f, "Mode").Kind);
        var inner = Sym(f, "inner");
        Assert.Equal("module", inner.Kind);
        Assert.Equal("internal", inner.Visibility);

        var bump = Sym(f, "bump", "Counter");
        Assert.Equal("method", bump.Kind);
        Assert.Equal("public", bump.Visibility);
        Assert.True(bump.X["exported"]!.GetValue<bool>());
        Assert.Equal("private", Sym(f, "helper", "Counter").Visibility);
        Assert.False(Sym(f, "helper", "Counter").X["exported"]!.GetValue<bool>());
        Assert.Equal("function", Sym(f, "free").Kind);
        Assert.Equal("public", Sym(f, "say", "Counter").Visibility);
        Assert.Equal("function", Sym(f, "deep").Kind);
    }

    [Fact]
    public void Refs_calls_inherit_sql_imports()
    {
        var f = RustParser.Parse("lib.rs", Src);
        Assert.Contains(f.Refs, r => r is { Kind: "inherit", FromParent: "Counter", Name: "Speak" });
        Assert.Contains(f.Refs, r => r is { Kind: "call", Name: "helper", From: "bump", FromParent: "Counter" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "util", Name: "run", From: "bump" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "Other", QualType: "Other", Name: "make" });
        Assert.Contains(f.Refs, r => r is { Kind: "member", Qual: "n", Name: "checked_add" });
        Assert.Contains(f.Refs, r => r is { Kind: "call", Name: "run", From: "free" });
        Assert.Contains(f.Refs, r => r is { Kind: "sql", Mode: "read", Name: "orders", From: "say", FromParent: "Counter" });

        var imports = f.Refs.Where(r => r.Kind == "import").ToList();
        Assert.Equal(["std::collections::HashMap", "std::collections::hash_map::Entry", "crate::util::*"], imports.Select(r => r.Name));
        Assert.Equal("E", imports[1].X["alias"]!.GetValue<string>());
        Assert.Equal(1, imports[0].Line);
    }
}
