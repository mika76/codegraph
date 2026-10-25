using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>Port of codegraph/parsers/rust.py: structs, enums, traits, impl blocks, functions, modules, calls, `use` imports.</summary>
public static partial class RustParser
{
    private const RegexOptions O = RegexOptions.CultureInvariant;
    private const string Id = @"[A-Za-z_]\w*";
    private const string Vis = @"(?:pub(?:\s*\([^)]*\))?\s+)?";

    // One pass over the raw text that (a) turns every string literal (normal, byte, raw, multi-line) into `"   "` with
    // no newlines inside, remembering its content, and (b) blanks lifetimes / labels ('a, 'static) so they are not
    // mistaken for char literals. Comments are matched first only so quotes inside them are ignored; Scrub.CLike then
    // blanks them. Length is preserved, so offsets of the original text stay valid.
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/|(?<![\w])b?r(?<h>#*)""(?<raw>.*?)""\k<h>|(?<![\w])b?""(?<str>(?:\\.|[^""\\])*)""|'(?:\\[^'\n]*|[^'\\\n])'|(?<life>'[A-Za-z_]\w*)", O | RegexOptions.Singleline)]
    private static partial Regex PreRe();

    [GeneratedRegex(@"(?<![\w])(" + Vis + @")(struct|enum|union|trait)\s+(" + Id + ")", O)] private static partial Regex StructRe();
    [GeneratedRegex(@"(?<![\w])(" + Vis + @")mod\s+(" + Id + @")\s*\{", O)] private static partial Regex ModRe();
    [GeneratedRegex(@"(?<![\w])(" + Vis + @"(?:(?:const|async|unsafe|default)\s+)*(?:extern\b\s*(?:""[^""]*"")?\s*)?)fn\s+(" + Id + ")", O)] private static partial Regex FnRe();
    [GeneratedRegex(@"#\s*\[\s*(?:\w+::)?wasm_bindgen\b[^\]]*\]", O)] private static partial Regex WasmRe();
    [GeneratedRegex(@"^(?:\s|#\s*!?\s*\[[^\]]*\])*\z", O)] private static partial Regex AttrsOnly();
    [GeneratedRegex(@"(?<![\w])use\s+([^;{}]*(?:\{[^;]*\})?[^;]*);", O)] private static partial Regex UseRe();
    [GeneratedRegex(@"(?<![\w.])(" + Id + @"(?:\s*::\s*(?:<[^<>]*(?:<[^<>]*>[^<>]*)*>\s*::\s*)?" + Id + @")*)\s*(?:::\s*<[^()]*?>\s*)?\(", O)] private static partial Regex PathCall();
    [GeneratedRegex(@"::\s*<[^<>]*(?:<[^<>]*>[^<>]*)*>", O)] private static partial Regex Turbofish();
    [GeneratedRegex(@"\.\s*(" + Id + @")\s*(?:::\s*<[^()]*?>\s*)?\(", O)] private static partial Regex MethodCall();
    [GeneratedRegex(@"(?<![\w])impl\b", O)] private static partial Regex ImplRe();
    [GeneratedRegex(@"unsafe\s*$", O)] private static partial Regex UnsafeTail();
    [GeneratedRegex(@"\bwhere\b", O)] private static partial Regex WhereRe();
    [GeneratedRegex(@"\bfor\b", O)] private static partial Regex ForRe();
    [GeneratedRegex(@"\G[\s!&]*(?:mut\s+)?(?:dyn\s+)?(?:" + Id + @"\s*::\s*)*(" + Id + ")", O)] private static partial Regex LastIdentRe();
    [GeneratedRegex(@"(?<![\w])fn\s*$", O)] private static partial Regex FnTail();
    [GeneratedRegex(Id, O)] private static partial Regex IdRe();
    [GeneratedRegex(@"(" + Id + @")\s*\??\s*$", O)] private static partial Regex RecvRe();
    [GeneratedRegex(@"\s+", O)] private static partial Regex WsRe();
    [GeneratedRegex(@"\s*(::|[{},])\s*", O)] private static partial Regex UseSepRe();
    [GeneratedRegex(@"^(.*?)(?:\s+as\s+(\w+))?$", O)] private static partial Regex UseAliasRe();
    [GeneratedRegex(@"[{;]", O)] private static partial Regex BraceOrSemi();

    private static readonly HashSet<string> NotFuncs =
    [
        "if", "while", "for", "match", "return", "loop", "fn", "let", "in", "as", "else", "mut", "ref", "move",
        "where", "impl", "dyn", "unsafe", "async", "await", "break", "continue", "pub", "use", "mod", "struct",
        "enum", "trait", "type", "const", "static", "super", "crate", "self", "Self",
    ];

    private sealed class Container
    {
        public string Kind = "";
        public string? Name;
        public int Open, Close;
        public bool Wasm, TraitImpl;
    }

    private static string Sub(string s, int a, int b)
    {
        a = Math.Max(0, a);
        b = Math.Min(s.Length, b);
        return b <= a ? "" : s.Substring(a, b - a);
    }

    private static (string Code, List<StringLit> Strings) Prep(string text)
    {
        var strings = new List<StringLit>();
        string res = PreRe().Replace(text, m =>
        {
            if (m.Groups["life"].Success) return new string(' ', m.Length);
            if (m.Groups["raw"].Success || m.Groups["str"].Success)
            {
                string body = m.Groups["raw"].Success ? m.Groups["raw"].Value : m.Groups["str"].Value;
                strings.Add(new StringLit(m.Index, body));
                return "\"" + new string(' ', m.Length - 2) + "\"";
            }
            return m.Value;
        });
        return (res, strings);
    }

    /// <summary>code[i] == '&lt;' -> index just after the matching '&gt;' ('-&gt;' and '=&gt;' are not closers).</summary>
    private static int SkipAngle(string code, int i)
    {
        int depth = 0, n = code.Length;
        while (i < n)
        {
            char ch = code[i];
            if (ch == '<') depth++;
            else if (ch == '>' && (i > 0 ? code[i - 1] : code[^1]) is not ('-' or '='))
            {
                depth--;
                if (depth == 0) return i + 1;
            }
            else if (ch is '{' or ';') return i;
            i++;
        }
        return n;
    }

    private static int AfterGenerics(string code, int i)
    {
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        if (i < code.Length && code[i] == '<')
        {
            i = SkipAngle(code, i);
            while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        }
        return i;
    }

    private static string? LastIdent(string s)
    {
        var m = LastIdentRe().Match(s);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Visibility_(string prefix)
    {
        string p = prefix.Trim();
        if (!p.StartsWith("pub", StringComparison.Ordinal)) return "private";
        return p.Contains('(') ? "internal" : "public";
    }

    private static char PrevChar(string code, int pos)
    {
        int j = pos - 1;
        while (j >= 0 && char.IsWhiteSpace(code[j])) j--;
        return j >= 0 ? code[j] : '\0';
    }

    /// <summary>'a::b::{c, d as e, f::*}' -> [('a::b::c', null), ('a::b::d', 'e'), ('a::b::f::*', null)]</summary>
    private static List<(string Full, string? Alias)> ExpandUse(string spec, string prefix = "")
    {
        spec = WsRe().Replace(spec, " ").Trim();
        spec = UseSepRe().Replace(spec, "$1");
        int b = spec.IndexOf('{');
        var result = new List<(string, string?)>();
        if (b < 0)
        {
            var m = UseAliasRe().Match(spec);
            string path = m.Groups[1].Value.Trim();
            string? alias = m.Groups[2].Success ? m.Groups[2].Value : null;
            string full = path == "self" ? prefix.TrimEnd(':') : prefix + path;
            if (full.Length > 0) result.Add((full, alias));
            return result;
        }
        string pre = spec[..b];
        int close = Scrub.MatchBrace(spec, b);
        string inner = Sub(spec, b + 1, close);
        var parts = new List<string>();
        int depth = 0;
        var cur = new StringBuilder();
        foreach (char ch in inner)
        {
            if (ch == '{') depth++;
            else if (ch == '}') depth--;
            if (ch == ',' && depth == 0)
            {
                parts.Add(cur.ToString());
                cur.Clear();
            }
            else cur.Append(ch);
        }
        parts.Add(cur.ToString());
        foreach (var p in parts)
            if (p.Trim().Length > 0)
                result.AddRange(ExpandUse(p.Trim(), prefix + pre));
        return result;
    }

    public static FileFacts Parse(string path, string text)
    {
        var (prepped, strings) = Prep(text);
        var (code, _) = Scrub.CLike(prepped, verbatimAt: false);
        var li = new LineIndex(text);
        int[] depths = Scrub.DepthMap(code);
        int n = code.Length;
        var facts = new FileFacts { Lang = "rust" };
        var symbols = facts.Symbols;
        var refs = facts.Refs;

        var wasmEnds = WasmRe().Matches(code).Select(m => m.Index + m.Length).ToList();

        bool IsWasm(int start) =>
            wasmEnds.Any(e => start - e < 600 && e <= start && AttrsOnly().IsMatch(Sub(code, e, start)));

        var containers = new List<Container>(); // impl / trait bodies: methods found inside belong to them

        // ---- impl blocks
        foreach (Match m in ImplRe().Matches(code))
        {
            int start = m.Index;
            int before = start;
            var pm = UnsafeTail().Match(Sub(code, start - 12, start));
            if (pm.Success) before = start - pm.Length;
            char pc = PrevChar(code, before);
            if (pc is not ('\0' or ';' or '{' or '}' or ']')) continue; // `-> impl Trait`, `x: impl Fn()` ...
            int i = AfterGenerics(code, m.Index + m.Length);
            int b = code.IndexOf('{', Math.Min(i, n));
            if (b < 0) continue;
            string header = WhereRe().Split(Sub(code, i, b))[0];
            var parts = ForRe().Split(header, 2);
            string? trait = parts.Length == 2 ? LastIdent(parts[0]) : null;
            string? tname = LastIdent(parts[^1]);
            int close = Scrub.MatchBrace(code, b);
            containers.Add(new Container { Kind = "impl", Name = tname, Open = b, Close = close, Wasm = IsWasm(before), TraitImpl = !string.IsNullOrEmpty(trait) });
            if (!string.IsNullOrEmpty(trait) && tname != null)
                refs.Add(new Ref { From = null, FromParent = tname, Kind = "inherit", Name = trait, Line = li.Line(start) });
        }

        // ---- structs / enums / traits
        foreach (Match m in StructRe().Matches(code))
        {
            string kw = m.Groups[2].Value, name = m.Groups[3].Value;
            int start = m.Index;
            int i = AfterGenerics(code, m.Index + m.Length);
            int end = start;
            var bases = new List<string>();
            string kind;
            if (kw == "trait")
            {
                int b = code.IndexOf('{', Math.Min(i, n));
                if (b < 0) continue;
                string header = WhereRe().Split(Sub(code, i, b))[0];
                int colon = header.IndexOf(':');
                if (colon >= 0)
                    foreach (string part in header[(colon + 1)..].Split('+'))
                    {
                        string? bn = LastIdent(part.Replace('?', ' '));
                        if (!string.IsNullOrEmpty(bn) && !bases.Contains(bn)) bases.Add(bn);
                    }
                end = Scrub.MatchBrace(code, b);
                containers.Add(new Container { Kind = "trait", Name = name, Open = b, Close = end, Wasm = false });
                kind = "interface";
            }
            else if (kw == "enum")
            {
                int b = code.IndexOf('{', Math.Min(i, n));
                end = b >= 0 ? Scrub.MatchBrace(code, b) : i;
                kind = "enum";
            }
            else
            {
                if (i < n && code[i] == '(')
                {
                    end = Scrub.MatchBrace(code, i, '(', ')');
                    int semi = code.IndexOf(';', Math.Min(end, n));
                    end = semi >= 0 ? semi : end;
                }
                else
                {
                    var mm = BraceOrSemi().Match(code, Math.Min(i, n));
                    if (mm.Success && mm.Value == "{") end = Scrub.MatchBrace(code, mm.Index);
                    else end = mm.Success ? mm.Index : i;
                }
                kind = "struct";
            }
            var sym = new Symbol
            {
                Name = name, Kind = kind, Line = li.Line(start), Parent = null, Visibility = Visibility_(m.Groups[1].Value),
                Lines = li.Line(end) - li.Line(start) + 1,
            };
            sym.X["exported"] = IsWasm(start);
            if (bases.Count > 0)
            {
                var ba = new JsonArray();
                foreach (var x in bases) ba.Add((JsonNode?)JsonValue.Create(x));
                sym.X["bases"] = ba;
            }
            symbols.Add(sym);
            foreach (var b_ in bases)
                refs.Add(new Ref { From = null, FromParent = name, Kind = "inherit", Name = b_, Line = li.Line(start) });
        }

        // ---- modules
        foreach (Match m in ModRe().Matches(code))
        {
            int close = Scrub.MatchBrace(code, m.Index + m.Length - 1);
            symbols.Add(new Symbol
            {
                Name = m.Groups[2].Value, Kind = "module", Line = li.Line(m.Index), Parent = null,
                Visibility = Visibility_(m.Groups[1].Value), Lines = li.Line(close) - li.Line(m.Index) + 1,
            });
        }

        // ---- functions and methods
        var spans = new List<(string Name, string? Parent, int Open, int Close)>();
        var seen = new HashSet<(string?, string)>();
        foreach (Match m in FnRe().Matches(code))
        {
            int start = m.Index;
            string name = m.Groups[2].Value;
            int i = AfterGenerics(code, m.Index + m.Length);
            if (i >= n || code[i] != '(') continue;
            int pClose = Scrub.MatchBrace(code, i, '(', ')');
            int j = pClose + 1, depth = 0;
            while (j < n)
            {
                char ch = code[j];
                if (ch is '(' or '[') depth++;
                else if (ch is ')' or ']') depth--;
                else if (depth <= 0 && ch is '{' or ';') break;
                j++;
            }
            bool hasBody = j < n && code[j] == '{';
            Container? owner = null;
            foreach (var c in containers)
                if (c.Open < start && start < c.Close && depths[start] == depths[c.Open] + 1)
                    if (owner == null || c.Open > owner.Open) owner = c;
            if (!hasBody && !(owner != null && owner.Kind == "trait")) continue; // extern "C" declarations
            string? parent = owner?.Name;
            string vis = Visibility_(m.Groups[1].Value);
            if (owner != null && (owner.Kind == "trait" || owner.TraitImpl)) vis = "public";
            int end = hasBody ? Scrub.MatchBrace(code, j) : j;
            bool exported = IsWasm(start) || (owner != null && owner.Kind == "impl" && owner.Wasm && vis == "public");
            if (seen.Add((parent, name)))
            {
                string sig = WsRe().Replace(Sub(text, start, j), " ").Trim();
                if (sig.Length > 200) sig = sig[..200];
                var sym = new Symbol
                {
                    Name = name, Kind = parent != null ? "method" : "function", Line = li.Line(start), Parent = parent,
                    Visibility = vis, Lines = li.Line(end) - li.Line(start) + 1, Signature = sig,
                };
                sym.X["exported"] = exported;
                symbols.Add(sym);
            }
            if (hasBody) spans.Add((name, parent, j, end));
        }

        (string Name, string? Parent, int Open, int Close)? OwnerOf(int pos)
        {
            (string Name, string? Parent, int Open, int Close)? best = null;
            foreach (var s in spans)
                if (s.Open <= pos && pos <= s.Close && (best == null || s.Open > best.Value.Open)) best = s;
            return best;
        }

        var local = new List<Ref>();
        var localKeys = new HashSet<(string?, string?, string, string?, string)>();

        void Add(int pos, Ref r)
        {
            var o = OwnerOf(pos);
            if (o == null && r.Kind != "sql") return;
            r.From = o?.Name;
            r.FromParent = o?.Parent;
            var key = (r.From, r.FromParent, r.Kind, r.Qual, r.Name);
            if (localKeys.Add(key))
            {
                r.Line = li.Line(pos);
                local.Add(r);
            }
        }

        // ---- calls inside fn bodies
        foreach (Match cm in PathCall().Matches(code))
        {
            if (OwnerOf(cm.Index) == null) continue;
            if (FnTail().IsMatch(Sub(code, cm.Index - 6, cm.Index))) continue;
            var segs = IdRe().Matches(Turbofish().Replace(cm.Groups[1].Value, "")).Select(x => x.Value).ToList();
            if (segs.Count == 0) continue;
            string name = segs[^1];
            if (NotFuncs.Contains(name) || char.IsUpper(name[0])) continue;
            if (segs.Count == 1)
                Add(cm.Index, new Ref { Kind = "call", Name = name });
            else
            {
                string qual = segs[^2];
                if (qual is "self" or "Self" or "super" or "crate" && segs.Count == 2)
                    Add(cm.Index, new Ref { Kind = "call", Name = name });
                else
                    Add(cm.Index, new Ref { Kind = "member", Qual = qual, QualType = char.IsUpper(qual[0]) ? qual : null, Name = name });
            }
        }
        foreach (Match cm in MethodCall().Matches(code))
        {
            if (OwnerOf(cm.Index) == null) continue;
            string name = cm.Groups[1].Value;
            var pm = RecvRe().Match(Sub(code, cm.Index - 80, cm.Index));
            string? recv = pm.Success ? pm.Groups[1].Value : null;
            if (recv == "self")
                Add(cm.Index, new Ref { Kind = "call", Name = name });
            else
                Add(cm.Index, new Ref { Kind = "member", Qual = recv, QualType = recv != null && char.IsUpper(recv[0]) ? recv : null, Name = name });
        }
        refs.AddRange(local);

        // ---- SQL in string literals
        var sqlSeen = new HashSet<(string?, string?, string, string)>();
        foreach (var sl in strings)
        {
            if (!SqlText.LooksLikeSql(sl.Value)) continue;
            var owner = OwnerOf(sl.Offset);
            refs.AddRange(SqlText.ColumnRefs(sl.Value, owner?.Name, owner?.Parent, _ => li.Line(sl.Offset)));
            foreach (var (mode, nm) in SqlText.Refs(sl.Value).OrderBy(r => r.Mode, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal))
            {
                var o = OwnerOf(sl.Offset);
                string? frm = o?.Name, fp = o?.Parent;
                if (sqlSeen.Add((frm, fp, mode, nm)))
                    refs.Add(new Ref { From = frm, FromParent = fp, Kind = "sql", Mode = mode, Name = nm, Line = li.Line(sl.Offset) });
            }
        }

        // ---- use declarations (file level)
        var imported = new HashSet<string>();
        foreach (Match um in UseRe().Matches(code))
            foreach (var (full, alias) in ExpandUse(um.Groups[1].Value))
            {
                if (full.Length > 0 && imported.Add(full))
                {
                    var r = new Ref { From = null, FromParent = null, Kind = "import", Name = full, Line = li.Line(um.Index) };
                    if (!string.IsNullOrEmpty(alias)) r.X["alias"] = alias;
                    refs.Add(r);
                }
            }

        facts.Loc = SourceText.CountLines(text);
        return facts;
    }
}
