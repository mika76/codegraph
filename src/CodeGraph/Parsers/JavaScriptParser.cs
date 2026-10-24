using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>
/// Port of codegraph/parsers/javascript.py: JavaScript / TypeScript / Vue single-file components,
/// plus package.json and tsconfig.json / jsconfig.json.
/// Extra keys (FileFacts.X): script/vue -> imports, default_export, reexports, star_exports, (vue) script_lang;
/// tsconfig -> base_url, paths, extends. Symbol.X: bases, exported, alias. Ref.X (endpoint): verb, raw.
/// </summary>
public static partial class JavaScriptParser
{
    private const string Id = @"[A-Za-z_$][\w$]*";
    private const RegexOptions O = RegexOptions.CultureInvariant;
    private const RegexOptions OI = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    [GeneratedRegex($$"""(?<![\w$.])(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+({{Id}})?\s*(?:<[^{]*?>)?(?:\s+extends\s+([\w$.]+)(?:<[^{]*?>)?)?(?:\s+implements\s+([^{]+))?\s*\{""", O)]
    private static partial Regex ClassRe();

    [GeneratedRegex($$"""(?<![\w$.])(?:export\s+)?(?:declare\s+)?interface\s+({{Id}})(?:\s*<[^{]*?>)?(?:\s+extends\s+([^{]+))?\s*\{""", O)]
    private static partial Regex IfaceRe();

    [GeneratedRegex($$"""(?<![\w$.])(?:export\s+)?type\s+({{Id}})\s*(?:<[^=]*?>)?\s*=""", O)]
    private static partial Regex TypeAliasRe();

    [GeneratedRegex($$"""(?<![\w$.])(?:export\s+)?(?:const\s+)?enum\s+({{Id}})\s*\{""", O)]
    private static partial Regex EnumRe();

    [GeneratedRegex($$"""(?<![\w$.])(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*({{Id}})\s*(?:<[^(]*?>)?\s*\(""", O)]
    private static partial Regex FuncRe();

    [GeneratedRegex($$"""(?<![\w$.])(?:export\s+)?(?:const|let|var)\s+({{Id}})\s*(?::[^=]+?)?=\s*(?:async\s+)?(?:function\b[^(]*\(|(?:<[^>]*>\s*)?\([^)]*\)\s*(?::\s*[^=]+?)?=>|{{Id}}\s*=>)""", O)]
    private static partial Regex ArrowRe();

    [GeneratedRegex($$"""(?<![\w$.])({{Id}})\s*:\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*=>|{{Id}}\s*=>)""", O)]
    private static partial Regex PropFuncRe();

    [GeneratedRegex($$"""^[ \t]*(?:@\w+(?:\([^)]*\))?\s*)*(?:(?:public|private|protected|static|async|readonly|override|abstract|get|set)\s+)*\*?({{Id}})\s*(?:<[^(]*?>)?\s*\([^)]*\)\s*(?::\s*[^{;]+?)?\{""", O | RegexOptions.Multiline)]
    private static partial Regex MethodRe();

    [GeneratedRegex($$"""^[ \t]*(?:(?:public|private|protected|static|readonly)\s+)*({{Id}})\s*(?::[^=]+?)?=\s*(?:async\s+)?\([^)]*\)\s*(?::\s*[^=]+?)?=>""", O | RegexOptions.Multiline)]
    private static partial Regex ClassArrowRe();

    [GeneratedRegex($$"""(?<![\w$])(?:({{Id}})\s*\??\.\s*)?({{Id}})\s*(?:<[^<>();{}=]*>)?\s*\(""", O)]
    private static partial Regex CallRe();

    [GeneratedRegex($$"""\bnew\s+({{Id}})""", O)]
    private static partial Regex NewRe();

    [GeneratedRegex(@"(?:\:\s*|<\s*|\bas\s+|\bimplements\s+|\bextends\s+)([A-Z][\w$]*)", O)]
    private static partial Regex TypeUseRe();

    private static readonly HashSet<string> NotFuncs =
    [
        "if", "for", "while", "switch", "catch", "return", "function", "typeof", "new", "super", "await",
        "import", "require", "constructor", "with", "do", "else", "case", "delete", "void", "in", "of",
        "instanceof", "yield", "throw", "export", "default",
    ];

    [GeneratedRegex("""\b(import|export)\s+(type\s+)?([^;'"`]*?)\s*\bfrom\s*(["'`])""", O)]
    private static partial Regex ImportFrom();

    [GeneratedRegex("""(?<![\w$.])import\s*(["'])""", O)]
    private static partial Regex ImportBare();

    [GeneratedRegex("""(?<![\w$.])(?:import|require)\s*\(\s*(["'`])""", O)]
    private static partial Regex ImportDyn();

    [GeneratedRegex($$"""(?:const|let|var)\s+({{Id}}|\{[^}]*\})\s*=\s*require\s*\(\s*(["'])""", O)]
    private static partial Regex RequireRe();

    [GeneratedRegex(@"\bexport\s*\{([^}]*)\}(?!\s*from)", O)]
    private static partial Regex ExportListRe();

    [GeneratedRegex($$"""\bexport\s+default\s+(?:class|function|async\s+function)?\s*({{Id}})""", O)]
    private static partial Regex ExportDefaultRe();

    [GeneratedRegex($$"""\bexport\s+default\s+new\s+({{Id}})""", O)]
    private static partial Regex ExportDefaultNewRe();

    [GeneratedRegex($$"""^({{Id}})\s*(?:,|$)""", O)]
    private static partial Regex ClauseDefaultRe();

    [GeneratedRegex($$"""\*\s+as\s+({{Id}})""", O)]
    private static partial Regex ClauseStarRe();

    [GeneratedRegex(@"\{([^}]*)\}", O)]
    private static partial Regex ClauseBracesRe();

    [GeneratedRegex(@"^\s*type\s+", O)]
    private static partial Regex LeadingTypeRe();

    [GeneratedRegex(@"\s+as\s+", O)]
    private static partial Regex AsSplitRe();

    [GeneratedRegex(@"<\s*([A-Z][\w]*|[a-z][\w]*(?:-[\w]+)+)(?=[\s/>])", O)]
    private static partial Regex TemplateTag();

    private static readonly HashSet<string> HtmlCustomSkip =
    [
        "router-view", "router-link", "transition", "transition-group", "keep-alive", "component",
        "slot", "teleport", "suspense", "template",
    ];

    [GeneratedRegex(@"^(?:\$\{[^}]*\})+", O)]
    private static partial Regex LeadingTemplateExpr();

    [GeneratedRegex(@"^(?:https?://[^/\s]+)?/?[A-Za-z_][\w.\-]*/[\w.\-${}:/]*(?:\?.*)?$|^/[A-Za-z_][\w.\-]*$", O)]
    private static partial Regex UrlLike();

    [GeneratedRegex(@"\.(vue|js|ts|css|scss|png|svg|jpe?g|gif|html?|json|ico|woff2?|map)$", OI)]
    private static partial Regex AssetExt();

    [GeneratedRegex(@"([\w$]+)\s*(?:<[^<>()]*>)?\s*\(\s*$", O)]
    private static partial Regex CalleeBefore();

    [GeneratedRegex("get|post|put|delete|fetch|request|ajax|json|api|http|load|send|call", O)]
    private static partial Regex CalleeWords();

    [GeneratedRegex(@"\burl\s*:\s*$", OI)]
    private static partial Regex UrlColon();

    [GeneratedRegex("""\btype\s*:\s*['"](post|put|delete)""", OI)]
    private static partial Regex TypeVerb();

    [GeneratedRegex("""method\s*:\s*['"](post|put|delete|patch)""", OI)]
    private static partial Regex MethodVerb();

    [GeneratedRegex("""(?:type|method)\s*:\s*['"](\w+)""", OI)]
    private static partial Regex AnyVerb();

    [GeneratedRegex(@"^export\b", O)]
    private static partial Regex ExportWord();

    [GeneratedRegex(@"<script\b([^>]*)>(.*?)</script\s*>", OI | RegexOptions.Singleline)]
    private static partial Regex ScriptBlock();

    [GeneratedRegex("""lang\s*=\s*["']ts""", O)]
    private static partial Regex LangTs();

    [GeneratedRegex("""\bname\s*:\s*["']([\w-]+)["']""", O)]
    private static partial Regex ComponentName();

    [GeneratedRegex(@"<template\b[^>]*>(.*)</template\s*>", OI | RegexOptions.Singleline)]
    private static partial Regex TemplateBlock();

    [GeneratedRegex(@"^tsconfig\..*\.json$", O)]
    private static partial Regex TsconfigName();

    // ------------------------------------------------------------------ helpers

    /// <summary>Insertion-ordered string-keyed map (Python dict semantics: re-setting a key keeps its position).</summary>
    private sealed class OMap<T>
    {
        private readonly List<string> _keys = [];
        private readonly Dictionary<string, T> _d = new(StringComparer.Ordinal);
        public IReadOnlyList<string> Keys => _keys;
        public T this[string k]
        {
            get => _d[k];
            set { if (!_d.ContainsKey(k)) _keys.Add(k); _d[k] = value; }
        }
        public bool ContainsKey(string k) => _d.ContainsKey(k);
    }

    private sealed class Imp
    {
        public string Spec = "";
        public OMap<string> Names = new();
        public int Line;
        public bool? Reexport;
        public bool? Lazy;
    }

    private static string Sub(string s, int a, int b)
    {
        a = Math.Max(0, Math.Min(a, s.Length));
        b = Math.Max(a, Math.Min(b, s.Length));
        return s.Substring(a, b - a);
    }

    private static string BaseName(string path)
    {
        int i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i < 0 ? path : path[(i + 1)..];
    }

    private static string DirBaseName(string path)
    {
        int i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i < 0 ? "" : BaseName(path[..i]);
    }

    private static string StripExt(string name)
    {
        int i = name.LastIndexOf('.');
        return i > 0 ? name[..i] : name;
    }

    private static string PyPascal(string tag)
    {
        if (!tag.Contains('-')) return tag;
        var sb = new StringBuilder();
        foreach (var p in tag.Split('-'))
            if (p.Length > 0) sb.Append(char.ToUpperInvariant(p[0])).Append(p, 1, p.Length - 1);
        return sb.ToString();
    }

    /// <summary>'Foo, { a, b as c }' -> {'Foo': 'default', 'a': 'a', 'c': 'b'};  '* as X' -> {'X': '*'}</summary>
    private static OMap<string> ParseImportClause(string clause)
    {
        var names = new OMap<string>();
        clause = clause.Trim();
        var m = ClauseDefaultRe().Match(clause);
        if (m.Success && m.Groups[1].Value != "type") names[m.Groups[1].Value] = "default";
        m = ClauseStarRe().Match(clause);
        if (m.Success) names[m.Groups[1].Value] = "*";
        m = ClauseBracesRe().Match(clause);
        if (m.Success)
        {
            foreach (var raw in m.Groups[1].Value.Split(','))
            {
                string part = LeadingTypeRe().Replace(raw, "").Trim();
                if (part.Length == 0) continue;
                var bits = AsSplitRe().Split(part);
                names[bits[^1].Trim()] = bits[0].Trim();
            }
        }
        return names;
    }

    private static JsonObject NamesJson(OMap<string> names)
    {
        var o = new JsonObject();
        foreach (var k in names.Keys) o[k] = names[k];
        return o;
    }

    private static JsonArray StrArray(IEnumerable<string> items) =>
        new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    // ------------------------------------------------------------------ entry point

    public static FileFacts Parse(string path, string text)
    {
        string baseName = BaseName(path).ToLowerInvariant();
        if (baseName == "package.json") return ParsePackageJson(path, text);
        if (baseName is "tsconfig.json" or "jsconfig.json" || TsconfigName().IsMatch(baseName))
            return ParseTsconfig(path, text);
        string lower = path.ToLowerInvariant();
        if (lower.EndsWith(".vue", StringComparison.Ordinal)) return ParseVue(path, text);
        bool ts = lower.EndsWith(".ts", StringComparison.Ordinal) || lower.EndsWith(".tsx", StringComparison.Ordinal) ||
                  lower.EndsWith(".mts", StringComparison.Ordinal) || lower.EndsWith(".cts", StringComparison.Ordinal);
        return ParseScript(path, text, ts ? "typescript" : "javascript", null);
    }

    // ------------------------------------------------------------------ scripts

    private readonly record struct Span(string Name, string? Parent, int Start, int End, string Kind);

    public static FileFacts ParseScript(string path, string text, string lang, string? component = null)
    {
        var (code, strings) = Scrub.CLike(text, verbatimAt: false, backtick: true);
        var strAt = new Dictionary<int, string>();
        foreach (var s in strings) strAt[s.Offset] = s.Value;
        var li = new LineIndex(code);
        var depths = Scrub.DepthMap(code);
        var symbols = new List<Symbol>();
        var refs = new List<Ref>();
        var spans = new List<Span>();
        var imports = new List<Imp>();
        var importPos = new HashSet<int>();
        var reexports = new OMap<JsonArray>();
        var starExports = new List<string>();
        bool hasComponent = !string.IsNullOrEmpty(component);

        // ---- imports / re-exports / dynamic imports
        foreach (Match m in ImportFrom().Matches(code))
        {
            if (!strAt.TryGetValue(m.Groups[4].Index, out var spec)) continue;
            importPos.Add(m.Groups[4].Index);
            bool isImport = m.Groups[1].Value == "import";
            OMap<string> names;
            if (isImport) names = ParseImportClause(m.Groups[3].Value);
            else // export { default as X, Y } from './y'   |   export * from './z'
            {
                names = new OMap<string>();
                string clause = m.Groups[3].Value.Trim();
                if (clause.StartsWith('*')) starExports.Add(spec);
                var parsed = ParseImportClause(clause);
                foreach (var exported in parsed.Keys)
                    reexports[exported] = new JsonArray(JsonValue.Create(spec), JsonValue.Create(parsed[exported]));
            }
            imports.Add(new Imp { Spec = spec, Names = names, Line = li.Line(m.Index), Reexport = !isImport });
        }
        foreach (var (rx, lazy) in new[] { (ImportBare(), false), (ImportDyn(), true) })
        {
            foreach (Match m in rx.Matches(code))
            {
                strAt.TryGetValue(m.Groups[1].Index, out var spec);
                importPos.Add(m.Groups[1].Index);
                if (!string.IsNullOrEmpty(spec) && !imports.Any(i => i.Spec == spec))
                    imports.Add(new Imp { Spec = spec, Line = li.Line(m.Index), Lazy = lazy });
            }
        }
        foreach (Match m in RequireRe().Matches(code))
        {
            if (!strAt.TryGetValue(m.Groups[2].Index, out var spec) || spec.Length == 0) continue;
            string tgt = m.Groups[1].Value;
            OMap<string> names;
            if (!tgt.StartsWith('{')) { names = new OMap<string>(); names[tgt] = "default"; }
            else names = ParseImportClause(tgt);
            foreach (var i in imports)
                if (i.Spec == spec)
                    foreach (var k in names.Keys) i.Names[k] = names[k];
        }

        foreach (Match m in ExportListRe().Matches(code))
        {
            var exportedNames = ParseImportClause("{" + m.Groups[1].Value + "}");
            foreach (var exported in exportedNames.Keys)
            {
                string loc = exportedNames[exported];
                var src = imports.FirstOrDefault(i => i.Names.ContainsKey(loc));
                reexports[exported] = src != null
                    ? new JsonArray(JsonValue.Create(src.Spec), JsonValue.Create(src.Names[loc]))
                    : new JsonArray(null, JsonValue.Create(loc));
            }
        }

        string? exportedDefault = null;
        var md = ExportDefaultRe().Match(code);
        if (md.Success && md.Groups[1].Value is not ("class" or "function" or "async" or "defineComponent" or "new"))
            exportedDefault = md.Groups[1].Value;
        var mn = ExportDefaultNewRe().Match(code);
        if (mn.Success) exportedDefault = mn.Groups[1].Value;

        // ---- types
        var types = new List<(string Name, int Open, int Close)>();
        foreach (Match m in ClassRe().Matches(code))
        {
            string name = m.Groups[1].Success ? m.Groups[1].Value
                : hasComponent ? component!
                : StripExt(BaseName(path));
            int open = m.Index + m.Length - 1;
            int close = Scrub.MatchBrace(code, open);
            var bases = new List<string>();
            if (m.Groups[2].Success) bases.Add(m.Groups[2].Value);
            if (m.Groups[3].Success)
                foreach (var b in m.Groups[3].Value.Split(','))
                    if (b.Trim().Length > 0) bases.Add(b.Trim().Split('<')[0]);
            types.Add((name, open, close));
            var sym = new Symbol
            {
                Name = name, Kind = "class", Line = li.Line(m.Index), Parent = null, Visibility = "public",
                Lines = li.Line(close) - li.Line(m.Index) + 1,
            };
            sym.X["bases"] = StrArray(bases);
            sym.X["exported"] = Sub(code, m.Index - 1, m.Index + 15).Contains("export", StringComparison.Ordinal);
            symbols.Add(sym);
            foreach (var b in bases)
                refs.Add(new Ref { From = null, FromParent = name, Kind = "inherit", Name = b.Split('.')[^1], Line = li.Line(m.Index) });
            if (Sub(code, m.Index, m.Index + 25).Contains("default", StringComparison.Ordinal) && string.IsNullOrEmpty(exportedDefault))
                exportedDefault = name;
            int bodyDepth = depths[open] + 1;
            string sub = code[..Math.Min(close, code.Length)]; // Python endpos=close
            if (m.Index + m.Length <= sub.Length)
            {
                foreach (var (rx, isMethod) in new[] { (MethodRe(), true), (ClassArrowRe(), false) })
                {
                    foreach (Match mm in rx.Matches(sub, m.Index + m.Length))
                    {
                        string nm = mm.Groups[1].Value;
                        if (NotFuncs.Contains(nm) || depths[mm.Groups[1].Index] != bodyDepth) continue;
                        int end = isMethod
                            ? Scrub.MatchBrace(code, code.IndexOf('{', mm.Index + mm.Length - 1))
                            : ExprEnd(code, mm.Index + mm.Length);
                        spans.Add(new Span(nm, name, mm.Groups[1].Index, end, "method"));
                    }
                }
            }
        }
        foreach (var (rx, kind) in new[] { (IfaceRe(), "interface"), (EnumRe(), "enum") })
        {
            foreach (Match m in rx.Matches(code))
            {
                int close = Scrub.MatchBrace(code, m.Index + m.Length - 1);
                symbols.Add(new Symbol
                {
                    Name = m.Groups[1].Value, Kind = kind, Line = li.Line(m.Index), Parent = null, Visibility = "public",
                    Lines = li.Line(close) - li.Line(m.Index) + 1,
                });
                if (kind == "interface" && m.Groups[2].Success)
                    foreach (var b in m.Groups[2].Value.Split(','))
                        if (b.Trim().Length > 0)
                            refs.Add(new Ref
                            {
                                From = null, FromParent = m.Groups[1].Value, Kind = "inherit",
                                Name = b.Trim().Split('<')[0], Line = li.Line(m.Index),
                            });
            }
        }
        foreach (Match m in TypeAliasRe().Matches(code))
            if (depths[m.Index] == 0)
                symbols.Add(new Symbol { Name = m.Groups[1].Value, Kind = "type_alias", Line = li.Line(m.Index), Parent = null, Visibility = "public" });

        bool InClass(int pos)
        {
            foreach (var t in types)
                if (t.Open < pos && pos < t.Close) return true;
            return false;
        }

        // ---- functions (top level and object-literal members like Vue `methods: { save() {} }`)
        foreach (Match m in FuncRe().Matches(code))
        {
            if (InClass(m.Index)) continue;
            int pClose = Scrub.MatchBrace(code, code.IndexOf('(', m.Index + m.Length - 1), '(', ')');
            int brace = code.IndexOf('{', Math.Min(pClose, code.Length));
            spans.Add(new Span(m.Groups[1].Value, null, m.Groups[1].Index, brace >= 0 ? Scrub.MatchBrace(code, brace) : pClose, "function"));
        }
        foreach (Match m in ArrowRe().Matches(code))
        {
            if (InClass(m.Index)) continue;
            spans.Add(new Span(m.Groups[1].Value, null, m.Groups[1].Index, ExprEnd(code, m.Index + m.Length), "function"));
        }
        foreach (Match m in PropFuncRe().Matches(code))
        {
            if (InClass(m.Index) || NotFuncs.Contains(m.Groups[1].Value)) continue;
            spans.Add(new Span(m.Groups[1].Value, component, m.Groups[1].Index, ExprEnd(code, m.Index + m.Length), hasComponent ? "method" : "function"));
        }
        if (hasComponent) // Vue options API shorthand methods:  save() { ... }  inside methods/computed/watch
        {
            foreach (Match m in MethodRe().Matches(code))
            {
                if (InClass(m.Index) || NotFuncs.Contains(m.Groups[1].Value) || depths[m.Groups[1].Index] < 2) continue;
                spans.Add(new Span(m.Groups[1].Value, component, m.Groups[1].Index,
                    Scrub.MatchBrace(code, code.IndexOf('{', m.Index + m.Length - 1)), "method"));
            }
        }

        var seen = new HashSet<(string?, string)>();
        var kept = new List<(string Name, string? Parent, int A, int B)>();
        foreach (var s in spans.OrderBy(s => s.Start)) // OrderBy is stable, like Python's sort
        {
            if (!seen.Add((s.Parent, s.Name))) continue;
            kept.Add((s.Name, s.Parent, s.Start, s.End));
            var sym = new Symbol
            {
                Name = s.Name, Kind = s.Kind, Line = li.Line(s.Start), Parent = s.Parent, Visibility = "public",
                Lines = li.Line(s.End) - li.Line(s.Start) + 1,
            };
            string lastLine = Sub(code, s.Start - 40, s.Start).Split('\n')[^1].Trim();
            sym.X["exported"] = ExportWord().IsMatch(lastLine);
            symbols.Add(sym);
        }

        // ---- references, attributed to the innermost function span
        (string Name, string? Parent)? Owner(int pos)
        {
            (string, string?, int)? best = null;
            foreach (var (name, parent, a, b) in kept)
                if (a <= pos && pos <= b && (best == null || a > best.Value.Item3)) best = (name, parent, a);
            return best == null ? null : (best.Value.Item1, best.Value.Item2);
        }

        var local = new HashSet<(string?, string?, string, string?, string)>();
        var localOrder = new List<Ref>();

        Ref? Add(int pos, string kind, string name, string? qual = null)
        {
            var o = Owner(pos);
            string? from = o?.Name, fromParent = o != null ? o.Value.Parent : component;
            if (!local.Add((from, fromParent, kind, qual, name))) return null;
            var r = new Ref { From = from, FromParent = fromParent, Kind = kind, Name = name, Qual = qual, Line = li.Line(pos) };
            localOrder.Add(r);
            return r;
        }

        foreach (Match cm in CallRe().Matches(code))
        {
            string? q = cm.Groups[1].Success ? cm.Groups[1].Value : null;
            string n = cm.Groups[2].Value;
            if (NotFuncs.Contains(n) || (q == null && Sub(code, cm.Index - 9, cm.Index).TrimEnd().EndsWith("function", StringComparison.Ordinal)))
                continue;
            if (q is "this" or "self" or "vm") Add(cm.Index, "call", n);
            else if (q != null) Add(cm.Index, "member", n, q);
            else Add(cm.Index, "call", n);
        }
        foreach (Match nm in NewRe().Matches(code)) Add(nm.Index, "new", nm.Groups[1].Value);
        if (lang == "typescript")
            foreach (Match tm in TypeUseRe().Matches(code)) Add(tm.Index, "type", tm.Groups[1].Value);
        refs.AddRange(localOrder);

        // ---- HTTP calls: api.get('/inventory/items'), fetch(`${API_URL}/x/${id}`), $.ajax({url: '/C/A'})
        foreach (var lit in strings)
        {
            int pos = lit.Offset;
            string s_ = lit.Value;
            if (importPos.Contains(pos)) continue;
            string raw = LeadingTemplateExpr().Replace(s_.Trim(), "");
            if (!UrlLike().IsMatch(raw)) continue;
            if (AssetExt().IsMatch(raw.Split('?')[0])) continue;
            string before = Sub(code, pos - 80, pos);
            var cm = CalleeBefore().Match(before);
            string callee = cm.Success ? cm.Groups[1].Value.ToLowerInvariant() : "";
            bool isHttp = cm.Success && (callee is "get" or "post" or "put" or "delete" or "patch" or "fetch" or "ajax" or "axios"
                                         or "request" or "jsonp" or "getjson" or "head" or "options" || CalleeWords().IsMatch(callee));
            if (!isHttp && !UrlColon().IsMatch(before)) continue;
            string? verb = callee is "get" or "post" or "put" or "delete" or "patch" ? callee : null;
            // The Python tested `type:` against the scrubbed code (strings blanked, so it never matched); use the
            // original text, but only up to the end of the current call so a later call's `type:` doesn't leak in.
            string ahead = Sub(text, pos, pos + 300);
            string typeAhead = Sub(text, pos, pos + 200);
            int paren = typeAhead.IndexOf(')');
            if (paren >= 0) typeAhead = typeAhead[..paren];
            if (TypeVerb().IsMatch(typeAhead))
            {
                var mm = AnyVerb().Match(typeAhead);
                verb = mm.Success ? mm.Groups[1].Value.ToLowerInvariant() : verb;
            }
            else if (MethodVerb().IsMatch(ahead))
            {
                var mm = AnyVerb().Match(ahead);
                verb = mm.Success ? mm.Groups[1].Value.ToLowerInvariant() : verb;
            }
            var r = Add(pos, "endpoint", Routes.Normalize(raw));
            if (r != null)
            {
                if (verb != null) r.X["verb"] = verb;
                r.X["raw"] = s_.Length > 120 ? s_[..120] : s_;
                refs.Add(r);
            }
        }

        string joined = string.Join(" ", strings.Select(s => s.Value));
        if (SqlText.LooksLikeSql(joined))
        {
            // Python iterates a set (arbitrary order); sort for determinism.
            foreach (var (mode, nm) in SqlText.Refs(joined).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Mode, StringComparer.Ordinal))
                refs.Add(new Ref { From = null, Kind = "sql", Mode = mode, Name = nm, Line = 1 });
        }
        foreach (var s in strings)
        {
            var id = SqlText.LiteralIdentifier(s.Value);
            if (id != null) refs.Add(new Ref { From = null, Kind = "literal", Name = id, Line = li.Line(s.Offset) });
        }

        var facts = new FileFacts { Lang = lang, Symbols = symbols, Refs = refs, Loc = SourceText.CountLines(text) };
        facts.X["imports"] = new JsonArray(imports.Select(i =>
        {
            var o = new JsonObject { ["spec"] = i.Spec, ["names"] = NamesJson(i.Names), ["line"] = i.Line };
            if (i.Reexport != null) o["reexport"] = i.Reexport.Value;
            if (i.Lazy != null) o["lazy"] = i.Lazy.Value;
            return (JsonNode?)o;
        }).ToArray());
        if (exportedDefault != null) facts.X["default_export"] = exportedDefault;
        var re = new JsonObject();
        foreach (var k in reexports.Keys) re[k] = reexports[k];
        facts.X["reexports"] = re;
        facts.X["star_exports"] = StrArray(starExports);
        return facts;
    }

    /// <summary>End of an arrow-function body / expression starting at pos.</summary>
    private static int ExprEnd(string code, int pos)
    {
        int j = pos;
        while (j < code.Length && (code[j] == ' ' || code[j] == '\t' || code[j] == '\r' || code[j] == '\n')) j++;
        if (j < code.Length && code[j] == '{') return Scrub.MatchBrace(code, j);
        int depth = 0;
        for (int k = j; k < code.Length; k++)
        {
            char ch = code[k];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}')
            {
                if (depth == 0) return k;
                depth--;
            }
            else if ((ch is ',' or ';' or '\n') && depth == 0) return k;
        }
        return code.Length - 1;
    }

    // ------------------------------------------------------------------ Vue SFC

    public static FileFacts ParseVue(string path, string text)
    {
        string comp = StripExt(BaseName(path));
        // keep only <script> contents (same offsets/line numbers), blank everything else
        var chars = new char[text.Length];
        for (int i = 0; i < text.Length; i++) chars[i] = text[i] == '\n' ? '\n' : ' ';
        string lang = "javascript";
        foreach (Match m in ScriptBlock().Matches(text))
        {
            if (LangTs().IsMatch(m.Groups[1].Value)) lang = "typescript";
            int a = m.Groups[2].Index, len = m.Groups[2].Length;
            text.CopyTo(a, chars, a, len);
        }
        string script = new(chars);
        var facts = ParseScript(path, script, lang, comp);
        var nm = ComponentName().Match(script);
        string compName = nm.Success ? PyPascal(nm.Groups[1].Value) : comp;
        var cs = new Symbol { Name = comp, Kind = "component", Line = 1, Parent = null, Visibility = "public", Lines = SourceText.CountLines(text) };
        if (compName != comp) cs.X["alias"] = compName;
        facts.Symbols.Insert(0, cs);
        for (int i = 1; i < facts.Symbols.Count; i++)
            if (facts.Symbols[i].Parent == null && facts.Symbols[i].Kind == "function") facts.Symbols[i].Parent = comp;
        foreach (var r in facts.Refs)
            if (!string.IsNullOrEmpty(r.From) && string.IsNullOrEmpty(r.FromParent)) r.FromParent = comp;
        facts.X["default_export"] = comp;
        var tm = TemplateBlock().Match(text);
        if (tm.Success)
        {
            var li = new LineIndex(text);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in TemplateTag().Matches(tm.Groups[1].Value))
            {
                string tag = m.Groups[1].Value;
                if (HtmlCustomSkip.Contains(tag.ToLowerInvariant()) || !seen.Add(tag)) continue;
                facts.Refs.Add(new Ref
                {
                    From = null, FromParent = comp, Kind = "component", Name = PyPascal(tag),
                    Line = li.Line(tm.Groups[1].Index + m.Index),
                });
            }
        }
        facts.Lang = "vue";
        facts.X["script_lang"] = lang;
        return facts;
    }

    // ------------------------------------------------------------------ package.json / tsconfig

    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static FileFacts ParsePackageJson(string path, string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException)
        {
            return new FileFacts
            {
                Lang = "project",
                Project = new ProjectInfo { Format = "npm", Name = DirBaseName(path), Members = [], Error = "invalid json" },
            };
        }
        var data = node as JsonObject ?? new JsonObject();
        var libs = new List<LibRef>();
        foreach (var (sect, kind) in new[] { ("dependencies", "npm"), ("devDependencies", "npm-dev"), ("peerDependencies", "npm") })
            if (data[sect] is JsonObject deps)
                foreach (var kv in deps)
                    libs.Add(new LibRef { Kind = kind, Name = kv.Key, Version = AsText(kv.Value) ?? "None" });
        string name = AsText(data["name"]) is { Length: > 0 } n ? n : DirBaseName(path) is { Length: > 0 } d ? d : "package";
        var proj = new ProjectInfo
        {
            Format = "npm", Lang = "javascript", Name = name, Title = null, Type = "npm package", Output = name,
            Version = AsText(data["version"]), Members = [], Libs = libs, ProjectRefs = [], SdkStyle = true,
        };
        var scripts = (data["scripts"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? [];
        scripts.Sort(StringComparer.Ordinal);
        proj.X["scripts"] = StrArray(scripts);
        return new FileFacts { Lang = "project", Project = proj };
    }

    private static string? AsText(JsonNode? n) =>
        n == null ? null : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();

    public static FileFacts ParseTsconfig(string path, string text)
    {
        JsonObject data;
        try { data = JsonNode.Parse(text, null, Jsonc) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { data = new JsonObject(); }
        var co = data["compilerOptions"] as JsonObject;
        var facts = new FileFacts { Lang = "tsconfig", Loc = 0 };
        if (co?["baseUrl"] is { } bu) facts.X["base_url"] = bu.DeepClone();
        facts.X["paths"] = co?["paths"] is JsonObject p ? p.DeepClone() : new JsonObject();
        if (data["extends"] is { } ex) facts.X["extends"] = ex.DeepClone();
        return facts;
    }
}
