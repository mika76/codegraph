using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>Port of codegraph/parsers/cpp.py: C and C++ source/header files, plus .def export files.</summary>
public static partial class CppParser
{
    private const RegexOptions O = RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^\s*#\s*include\s*[<""]([^>""]+)[>""]", O | RegexOptions.Multiline)] private static partial Regex IncludeRe();
    [GeneratedRegex(@"(?<![\w])(class|struct|union)\s+(?:__declspec\([^)]*\)\s*)?([A-Za-z_]\w*)\s*(?:final\s*)?(?::\s*([^{;]+?))?\s*\{", O)] private static partial Regex TypeRe();
    [GeneratedRegex(@"(?<![\w.>])((?:[A-Za-z_]\w*\s*::\s*)*~?[A-Za-z_]\w*)\s*\(", O)] private static partial Regex FuncRe();
    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)\(", RegexOptions.Multiline)] private static partial Regex MacroDefRe();
    [GeneratedRegex(@"\b(?:public|private|protected)\s*:|\b(?:explicit|inline|constexpr|virtual)\b")] private static partial Regex AccessLabelRe();
    [GeneratedRegex(@"(?<![\w])(?:([A-Za-z_]\w*)\s*(\.|->|::)\s*)?([A-Za-z_]\w*)\s*\(", O)] private static partial Regex CallRe();
    [GeneratedRegex(@"\bnew\s+([A-Za-z_][\w:]*)", O)] private static partial Regex NewRe();
    [GeneratedRegex(@"^\s*#.*$", O | RegexOptions.Multiline)] private static partial Regex PpLine();
    [GeneratedRegex(@"\bnamespace\s+\w*\s*\{", O)] private static partial Regex NamespaceOpen();
    // in the scrubbed code the "C" of extern "C" is blanked, so match any (blanked) string
    [GeneratedRegex(@"\bextern\s+""\s*""\s*\{", O)] private static partial Regex ExternOpen();
    [GeneratedRegex(@"\G\s*(?:const|noexcept|override|final|volatile|throw\s*\([^)]*\)|\s)*(?::[^{;]*)?\{", O)] private static partial Regex FuncTailRe();
    [GeneratedRegex(@"^(public|protected|private|virtual)\s+", O)] private static partial Regex BaseModifierRe();
    [GeneratedRegex(@"\s+", O)] private static partial Regex WsRe();
    [GeneratedRegex(@"^EXPORTS\b", O | RegexOptions.IgnoreCase)] private static partial Regex ExportsRe();
    [GeneratedRegex(@"^(LIBRARY|DESCRIPTION|SECTIONS|HEAPSIZE|STACKSIZE|VERSION)\b", O | RegexOptions.IgnoreCase)] private static partial Regex DefSectionRe();
    [GeneratedRegex(@"[\s=@]", O)] private static partial Regex DefSplitRe();

    private static readonly HashSet<string> NotFuncs =
    [
        "if", "for", "while", "switch", "catch", "return", "sizeof", "defined", "decltype", "alignof",
        "static_assert", "new", "delete", "throw", "do", "else", "case", "operator", "typeid", "__declspec",
        "__attribute__", "assert",
    ];

    private sealed class TypeInfo
    {
        public string Name = "";
        public int Open, Close, Depth, Start;
    }

    private sealed class FuncInfo
    {
        public string Name = "";
        public string? Parent;
        public int Start, BodyOpen, BodyClose;
        public string Sig = "";
        public bool Exported;
    }

    private static string Sub(string s, int a, int b)
    {
        a = Math.Max(0, a);
        b = Math.Min(s.Length, b);
        return b <= a ? "" : s.Substring(a, b - a);
    }

    private static string BaseName(string path)
    {
        int i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>Module-definition file: list of exported functions.</summary>
    public static FileFacts ParseDef(string path, string text)
    {
        var exports = new JsonArray();
        bool inExports = false;
        foreach (string ln in text.ReplaceLineEndings("\n").Split('\n'))
        {
            int semi = ln.IndexOf(';');
            string s = (semi >= 0 ? ln[..semi] : ln).Trim();
            if (s.Length == 0) continue;
            if (ExportsRe().IsMatch(s)) { inExports = true; continue; }
            if (DefSectionRe().IsMatch(s)) { inExports = false; continue; }
            if (inExports) exports.Add((JsonNode?)JsonValue.Create(DefSplitRe().Split(s)[0]));
        }
        var facts = new FileFacts { Lang = "cpp", Loc = SourceText.CountLines(text) };
        facts.X["exports"] = exports;
        return facts;
    }

    public static FileFacts Parse(string path, string text)
    {
        if (path.EndsWith(".def", StringComparison.OrdinalIgnoreCase)) return ParseDef(path, text);
        var (code0, strings) = Scrub.CLike(text, verbatimAt: false);
        var includes = IncludeRe().Matches(text).Select(m => m.Groups[1].Value.Replace('\\', '/')).ToList();
        // drop preprocessor lines (keep newlines: the Python replaced them too, which shifted later line numbers)
        string code = PpLine().Replace(code0, m =>
        {
            var sb = new StringBuilder(m.Length);
            foreach (char c in m.Value) sb.Append(c == '\n' ? '\n' : ' ');
            return sb.ToString();
        });
        var li = new LineIndex(code);
        int[] depths = Scrub.DepthMap(code);
        var facts = new FileFacts { Lang = "cpp" };
        var symbols = facts.Symbols;
        var refs = facts.Refs;

        var types = new List<TypeInfo>();
        foreach (Match m in TypeRe().Matches(code))
        {
            int openPos = m.Index + m.Length - 1;
            int close = Scrub.MatchBrace(code, openPos);
            var bases = new List<string>();
            if (m.Groups[3].Success)
                foreach (string b in m.Groups[3].Value.Split(','))
                    if (b.Trim().Length > 0)
                        bases.Add(BaseModifierRe().Replace(b.Trim(), "").Split('<')[0].Trim());
            string name = m.Groups[2].Value;
            types.Add(new TypeInfo { Name = name, Open = openPos, Close = close, Depth = depths[openPos] + 1, Start = m.Index });
            var sym = new Symbol
            {
                Name = name, Kind = m.Groups[1].Value == "class" ? "class" : "struct", Line = li.Line(m.Index),
                Parent = null, Visibility = "public", Lines = li.Line(close) - li.Line(m.Index) + 1,
            };
            var barr = new JsonArray();
            foreach (var b in bases) barr.Add((JsonNode?)JsonValue.Create(b));
            sym.X["bases"] = barr;
            symbols.Add(sym);
            foreach (var b in bases)
                refs.Add(new Ref { From = null, FromParent = name, Kind = "inherit", Name = b, Line = li.Line(m.Index) });
        }

        TypeInfo? TypeAt(int pos)
        {
            TypeInfo? best = null;
            foreach (var t in types)
                if (t.Open < pos && pos < t.Close && (best == null || t.Open > best.Open)) best = t;
            return best;
        }

        // namespaces don't change depth semantics for us: treat `namespace x {` bodies as top level
        var nsOpens = NamespaceOpen().Matches(code).Select(m => m.Index + m.Length - 1).ToHashSet();
        foreach (Match m in ExternOpen().Matches(code)) nsOpens.Add(m.Index + m.Length - 1);
        int[] shift = new int[code.Length + 1];
        var stack = new Stack<bool>();
        int s = 0;
        for (int i = 0; i < code.Length; i++)
        {
            shift[i] = s;
            char ch = code[i];
            if (ch == '{')
            {
                bool transparent = nsOpens.Contains(i);
                stack.Push(transparent);
                if (transparent) s++;
            }
            else if (ch == '}' && stack.Count > 0)
            {
                if (stack.Pop()) s--;
            }
        }
        shift[code.Length] = s;
        int Eff(int i) => i < code.Length ? depths[i] - shift[i] : 0;

        // function-like macros defined here; their uses look like function definitions
        var macros = MacroDefRe().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var funcs = new List<FuncInfo>();
        foreach (Match m in FuncRe().Matches(code))
        {
            string full = WsRe().Replace(m.Groups[1].Value, "");
            int sepAt = full.LastIndexOf("::", StringComparison.Ordinal);
            string name = sepAt >= 0 ? full[(sepAt + 2)..] : full;
            if (NotFuncs.Contains(name) || macros.Contains(name))
                continue;
            int pos = m.Groups[1].Index;
            var t = TypeAt(pos);
            int expected = t != null ? t.Depth - shift[Math.Min(t.Open + 1, code.Length)] : 0;
            if (Eff(pos) != expected) continue;
            int k = pos - 1;
            while (k > 0 && ";{}".IndexOf(code[k]) < 0) k--;
            string prefix = Sub(code, k + 1, pos).Trim();
            // nothing before the name (no return type): at file scope a macro use like TEST_F(a, b) {; in a class
            // only a constructor or destructor looks like that
            string typePart = AccessLabelRe().Replace(prefix, "").Trim();
            if (prefix.Contains('=') || prefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("return")
                || (prefix.Length == 0 && !full.Contains("::") && t == null)
                || (typePart.Length == 0 && !full.Contains("::") && t != null && name != t.Name && !name.StartsWith('~')))
                continue;
            int pOpen = code.IndexOf('(', m.Index + m.Length - 1);
            int pClose = Scrub.MatchBrace(code, pOpen, '(', ')');
            string rest = Sub(code, pClose + 1, pClose + 300);
            var hm = FuncTailRe().Match(rest);
            if (!hm.Success) continue;
            int bOpen = pClose + 1 + hm.Length - 1;
            int bClose = Scrub.MatchBrace(code, bOpen);
            var segs = full.Split("::");
            string? parent = string.Join("::", segs.Take(segs.Length - 1));
            if (parent.Length == 0) parent = t?.Name;
            if (!string.IsNullOrEmpty(parent)) parent = parent.Split("::")[^1];
            string sig = WsRe().Replace(prefix + " " + full + Sub(text, pOpen, pClose + 1), " ");
            if (sig.Length > 200) sig = sig[..200];
            funcs.Add(new FuncInfo
            {
                Name = name, Parent = string.IsNullOrEmpty(parent) ? null : parent, Start = pos, BodyOpen = bOpen, BodyClose = bClose,
                Sig = sig, Exported = prefix.Contains("dllexport") || prefix.Contains("WINAPI") || prefix.Contains("__stdcall"),
            });
        }

        var seen = new HashSet<(string?, string)>();
        foreach (var f in funcs)
        {
            if (!seen.Add((f.Parent, f.Name))) continue;
            var sym = new Symbol
            {
                Name = f.Name, Kind = f.Parent != null ? "method" : "function", Line = li.Line(f.Start), Parent = f.Parent,
                Visibility = "public", Signature = f.Sig, Lines = li.Line(f.BodyClose) - li.Line(f.Start) + 1,
            };
            sym.X["exported"] = f.Exported;
            symbols.Add(sym);
            int a = f.BodyOpen, b = f.BodyClose;
            string seg = Sub(code, a, b + 1);
            var local = new Dictionary<(string, string?, string), Ref>();
            var order = new List<(string, string?, string)>();
            foreach (Match cm in CallRe().Matches(seg))
            {
                string? qual = cm.Groups[1].Success ? cm.Groups[1].Value : null;
                string name = cm.Groups[3].Value;
                if (NotFuncs.Contains(name)) continue;
                var key2 = (qual != null ? "member" : "call", qual, name);
                if (local.ContainsKey(key2)) continue;
                var r = new Ref { From = f.Name, FromParent = f.Parent, Kind = key2.Item1, Name = name, Line = li.Line(a + cm.Index) };
                if (qual != null)
                {
                    r.Qual = qual;
                    // Type::method names the type, whatever its casing; obj.method / obj->method is an object
                    r.QualType = cm.Groups[2].Value == "::" ? qual : null;
                }
                local[key2] = r;
                order.Add(key2);
            }
            foreach (Match nm in NewRe().Matches(seg))
            {
                var key3 = ("new", (string?)null, nm.Groups[1].Value);
                string nn = nm.Groups[1].Value;
                int ci = nn.LastIndexOf("::", StringComparison.Ordinal);
                var r = new Ref { From = f.Name, FromParent = f.Parent, Kind = "new", Name = ci >= 0 ? nn[(ci + 2)..] : nn, Line = li.Line(a) };
                if (!local.ContainsKey(key3)) order.Add(key3);
                local[key3] = r;
            }
            foreach (var key in order) refs.Add(local[key]);
        }

        foreach (string inc in includes)
        {
            var r = new Ref { From = null, Kind = "include", Name = BaseName(inc), Line = 1 };
            r.X["path"] = inc;
            refs.Add(r);
        }
        string joined = string.Join(" ", strings.Select(x => x.Value));
        if (SqlText.LooksLikeSql(joined))
            foreach (var (mode, nm) in SqlText.Refs(joined).OrderBy(r => r.Mode, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal))
                refs.Add(new Ref { From = null, Kind = "sql", Mode = mode, Name = nm, Line = 1 });
        foreach (var str in strings)
        {
            string? lit = SqlText.LiteralIdentifier(str.Value);
            if (lit != null)
                refs.Add(new Ref { From = null, Kind = "literal", Name = lit, Line = li.Line(str.Offset) });
        }
        facts.Loc = SourceText.CountLines(text);
        return facts;
    }
}
