using CodeGraph.Parsers;

namespace CodeGraph.Tests.Parsers;

public class JavaScriptParserTests
{
    [Fact]
    public void Imports_names_reexports_and_lazy()
    {
        const string src = """
            import Foo, { a, b as c, type T } from './foo'
            import * as ns from "../ns"
            import './side'
            export { default as X, Y } from './y'
            export * from './z'
            const Lazy = () => import('./lazy')
            const lib = require('lib')
            import { a } from './foo'
            export { Foo as Bar }
            """;
        var f = JavaScriptParser.Parse("src/main.ts", src);
        Assert.Equal("typescript", f.Lang);
        var imps = f.X["imports"]!.AsArray();
        var first = imps[0]!;
        Assert.Equal("./foo", (string)first["spec"]!);
        Assert.Equal("default", (string)first["names"]!["Foo"]!);
        Assert.Equal("b", (string)first["names"]!["c"]!);
        Assert.Equal("a", (string)first["names"]!["a"]!);
        Assert.False((bool)first["reexport"]!);
        Assert.Equal("*", (string)imps[1]!["names"]!["ns"]!);
        Assert.Contains(imps, i => (string)i!["spec"]! == "./side" && i["lazy"] != null && !(bool)i["lazy"]!);
        Assert.Contains(imps, i => (string)i!["spec"]! == "./lazy" && (bool)i["lazy"]!);
        Assert.Contains(imps, i => (string)i!["spec"]! == "lib" && (string)i["names"]!["lib"]! == "default");
        var re = f.X["reexports"]!;
        Assert.Equal("./y", (string)re["X"]![0]!);
        Assert.Equal("default", (string)re["X"]![1]!);
        Assert.Equal("Y", (string)re["Y"]![1]!);
        Assert.Equal("./foo", (string)re["Bar"]![0]!);
        Assert.Equal("default", (string)re["Bar"]![1]!);
        Assert.Equal(["./z"], f.X["star_exports"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Class_methods_inheritance_and_exports()
    {
        const string src = """
            export default class Svc extends Base implements IA, IB<T> {
              private count = 0
              async load(id: number): Promise<void> {
                this.helper(id)
                api.get('/inventory/items')
                new Widget()
              }
              static make = (x) => { return 1 }
              helper(x) { return x }
            }
            export interface IA extends IB, IC<T> { a: number }
            export enum Color { Red }
            export type Id = string
            """;
        var f = JavaScriptParser.Parse("svc.ts", src);
        var cls = f.Symbols.Single(s => s.Kind == "class");
        Assert.Equal("Svc", cls.Name);
        Assert.Equal(["Base", "IA", "IB"], cls.X["bases"]!.AsArray().Select(n => (string)n!));
        Assert.True((bool)cls.X["exported"]!);
        Assert.Equal("Svc", (string)f.X["default_export"]!);
        Assert.Contains(f.Symbols, s => s.Name == "load" && s.Kind == "method" && s.Parent == "Svc");
        Assert.Contains(f.Symbols, s => s.Name == "make" && s.Parent == "Svc");
        Assert.Contains(f.Symbols, s => s.Name == "helper" && s.Parent == "Svc");
        Assert.Contains(f.Symbols, s => s.Name == "IA" && s.Kind == "interface");
        Assert.Contains(f.Symbols, s => s.Name == "Color" && s.Kind == "enum");
        Assert.Contains(f.Symbols, s => s.Name == "Id" && s.Kind == "type_alias");
        Assert.Contains(f.Refs, r => r.Kind == "inherit" && r.FromParent == "Svc" && r.Name == "Base");
        Assert.Contains(f.Refs, r => r.Kind == "inherit" && r.FromParent == "IA" && r.Name == "IC");
        Assert.Contains(f.Refs, r => r.Kind == "call" && r.Name == "helper" && r.From == "load" && r.FromParent == "Svc");
        Assert.Contains(f.Refs, r => r.Kind == "new" && r.Name == "Widget" && r.From == "load");
        Assert.Contains(f.Refs, r => r.Kind == "member" && r.Qual == "api" && r.Name == "get");
        Assert.Contains(f.Refs, r => r.Kind == "type" && r.Name == "Promise");
    }

    [Fact]
    public void Functions_arrows_and_export_flag()
    {
        const string src = """
            export function a() { b() }
            export const c = async (x) => x + 1
            const d = function () { }
            function* gen() {}
            """;
        var f = JavaScriptParser.Parse("m.js", src);
        Assert.Equal("javascript", f.Lang);
        Assert.True((bool)f.Symbols.Single(s => s.Name == "a").X["exported"]!);
        Assert.True((bool)f.Symbols.Single(s => s.Name == "c").X["exported"]!);
        Assert.False((bool)f.Symbols.Single(s => s.Name == "d").X["exported"]!);
        Assert.Contains(f.Symbols, s => s.Name == "gen" && s.Kind == "function");
        Assert.Contains(f.Refs, r => r.Kind == "call" && r.Name == "b" && r.From == "a");
        Assert.Equal(1, f.Symbols.Single(s => s.Name == "a").Line);
    }

    [Fact]
    public void Http_endpoints()
    {
        const string src = """
            const API_URL = 'x';
            export async function go(id) {
              await api.get('/inventory/items')
              await api.post(`/orders/${id}/redeem`, {})
              fetch(`${API_URL}/accounts/${id}`)
              $.ajax({ url: '/Home/Save', type: 'POST' })
              import('./view.vue')
              const css = load('/assets/site.css')
              const other = foo('/not/http')
            }
            """;
        var f = JavaScriptParser.Parse("a.js", src);
        var eps = f.Refs.Where(r => r.Kind == "endpoint").ToList();
        var m = eps.Single(r => r.Name == "inventory/items");
        Assert.Equal("get", (string)m.X["verb"]!);
        Assert.Equal("go", m.From);
        Assert.Equal("post", (string)eps.Single(r => r.Name == "orders/{}/redeem").X["verb"]!);
        Assert.False(eps.Single(r => r.Name == "accounts/{}").X.ContainsKey("verb"));
        Assert.Equal("post", (string)eps.Single(r => r.Name == "home/save").X["verb"]!);
        Assert.DoesNotContain(eps, r => r.Name.Contains("site.css") || r.Name.Contains("view.vue"));
        Assert.DoesNotContain(eps, r => r.Name == "not/http");
    }

    [Fact]
    public void Inline_sql_and_literal_identifiers()
    {
        const string src = "const q = 'select * from Orders'; const sp = 'bill_Customer_Get';";
        var f = JavaScriptParser.Parse("q.js", src);
        Assert.Contains(f.Refs, r => r.Kind == "sql" && r.Mode == "read" && r.Name == "orders" && r.Line == 1);
        Assert.Contains(f.Refs, r => r.Kind == "literal" && r.Name == "bill_customer_get");
    }

    [Fact]
    public void Vue_sfc_component_methods_and_template_refs()
    {
        const string src = """
            <template>
              <div>
                <AppButton @click="save" />
                <my-widget></my-widget>
                <router-view />
              </div>
            </template>
            <script lang="ts">
            import { defineComponent } from 'vue'
            import AppButton from '@/components/AppButton.vue'
            export default defineComponent({
              name: 'my-comp',
              methods: {
                save() {
                  api.put('/inventory/save')
                  this.refresh()
                },
                async refresh() {}
              }
            })
            </script>
            """;
        var f = JavaScriptParser.Parse("src/MyComp.vue", src);
        Assert.Equal("vue", f.Lang);
        Assert.Equal("typescript", (string)f.X["script_lang"]!);
        Assert.Equal("MyComp", (string)f.X["default_export"]!);
        var comp = f.Symbols[0];
        Assert.Equal("component", comp.Kind);
        Assert.Equal("MyComp", comp.Name);
        Assert.False(comp.X.ContainsKey("alias")); // 'my-comp' pascal-cases to the file name
        Assert.Equal("fancy",(string)JavaScriptParser.Parse("A.vue", "<script>export default { name: 'fancy' }</script>").Symbols[0].X["alias"]!);
        Assert.Contains(f.Symbols, s => s.Name == "save" && s.Kind == "method" && s.Parent == "MyComp");
        Assert.Contains(f.Symbols, s => s.Name == "refresh" && s.Parent == "MyComp");
        Assert.Contains(f.Refs, r => r.Kind == "call" && r.Name == "refresh" && r.From == "save" && r.FromParent == "MyComp");
        Assert.Contains(f.Refs, r => r.Kind == "endpoint" && r.Name == "inventory/save" && r.X["verb"]!.ToString() == "put");
        Assert.Contains(f.Refs, r => r.Kind == "component" && r.Name == "AppButton" && r.Line == 3);
        Assert.Contains(f.Refs, r => r.Kind == "component" && r.Name == "MyWidget" && r.FromParent == "MyComp");
        Assert.DoesNotContain(f.Refs, r => r.Kind == "component" && r.Name.Contains("RouterView"));
        Assert.Equal(src.Split('\n').Length, f.Loc);
    }

    [Fact]
    public void Package_json_becomes_project()
    {
        const string src = """
            { "name": "@acme/lib", "version": "1.2.3",
              "scripts": { "build": "x", "dev": "y" },
              "dependencies": { "vue": "^3.0.0" }, "devDependencies": { "vite": "^5" }, "peerDependencies": { "pinia": "*" } }
            """;
        var f = JavaScriptParser.Parse("pkg/package.json", src);
        Assert.Equal("project", f.Lang);
        var p = f.Project!;
        Assert.Equal("npm", p.Format);
        Assert.Equal("@acme/lib", p.Name);
        Assert.Equal("@acme/lib", p.Output);
        Assert.Equal("1.2.3", p.Version);
        Assert.Equal("npm package", p.Type);
        Assert.True(p.SdkStyle);
        Assert.Equal([("npm", "vue"), ("npm-dev", "vite"), ("npm", "pinia")], p.Libs.Select(l => (l.Kind, l.Name)));
        Assert.Equal(["build", "dev"], p.X["scripts"]!.AsArray().Select(n => (string)n!));

        var bad = JavaScriptParser.Parse("pkg/package.json", "{ nope");
        Assert.Equal("invalid json", bad.Project!.Error);
        Assert.Equal("pkg", bad.Project.Name);
        Assert.Equal("pkg", JavaScriptParser.Parse("pkg/package.json", "{}").Project!.Name);
    }

    [Fact]
    public void Tsconfig_with_comments_and_trailing_commas()
    {
        const string src = """
            {
              // comment
              "extends": "./base.json",
              "compilerOptions": {
                "baseUrl": ".", /* block */
                "paths": { "@/*": ["./src/*"], },
              },
            }
            """;
        foreach (var name in new[] { "tsconfig.json", "x/tsconfig.app.json", "jsconfig.json" })
        {
            var f = JavaScriptParser.Parse(name, src);
            Assert.Equal("tsconfig", f.Lang);
            Assert.Equal(".", (string)f.X["base_url"]!);
            Assert.Equal("./base.json", (string)f.X["extends"]!);
            Assert.Equal("./src/*", (string)f.X["paths"]!["@/*"]![0]!);
        }
        var bad = JavaScriptParser.Parse("tsconfig.json", "{ nope");
        Assert.Equal("tsconfig", bad.Lang);
        Assert.Empty(bad.X["paths"]!.AsObject());
    }
}
