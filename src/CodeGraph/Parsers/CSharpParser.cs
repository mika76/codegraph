using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>Port of codegraph/parsers/csharp.py: C# types, methods, references, attribute routes.</summary>
public static partial class CSharpParser
{
    private const RegexOptions O = RegexOptions.CultureInvariant;

    [GeneratedRegex(@"(?<![\w.])(class|interface|struct|enum|record(?:\s+class|\s+struct)?)\s+([A-Za-z_]\w*)\s*(<[^{;()]*?>)?\s*(\([^)]*\))?\s*(?::\s*([^{;]+?))?\s*(?:where\s[^{;]*)?\{", O)]
    private static partial Regex TypeRe();
    [GeneratedRegex(@"\bnamespace\s+([\w.]+)\s*[{;]", O)] private static partial Regex NsRe();
    [GeneratedRegex(@"^\s*using\s+(?:static\s+)?(?:\w+\s*=\s*)?([\w.]+)\s*;", O | RegexOptions.Multiline)] private static partial Regex UsingRe();
    [GeneratedRegex(@"(?<![\w.])([A-Za-z_]\w*)\s*(<[^<>(){};=]*(?:<[^<>(){};=]*>[^<>(){};=]*)*>)?\s*\(", O)] private static partial Regex MethodNameRe();
    [GeneratedRegex(@"(?:^|(?<=[;{}\]]))\s*(?:(?:public|private|protected|internal|static|readonly|const|volatile|new|virtual|override|abstract|sealed|event)\s+)*([A-Za-z_][\w.]*(?:<[^;{}()=]*?>)?(?:\[\s*\])?\??)\s+([A-Za-z_]\w*)\s*(?==|;|\{|=>)", O)]
    private static partial Regex FieldRe();
    [GeneratedRegex(@"(?<![\w.])([A-Za-z_][\w.]*(?:<[^;(){}=]*?>)?(?:\[\s*\])?\??)\s+([A-Za-z_]\w*)\s*(?==[^=>]|;|,|\)|\bin\b)", O)] private static partial Regex LocalRe();
    [GeneratedRegex(@"\bvar\s+([A-Za-z_]\w*)\s*=\s*new\s+([A-Za-z_][\w.]*)", O)] private static partial Regex VarNewRe();
    [GeneratedRegex(@"\bnew\s+([A-Za-z_][\w.]*)", O)] private static partial Regex NewRe();
    [GeneratedRegex(@"(?<![\w])(?:([A-Za-z_]\w*)\s*\??\.\s*)?([A-Za-z_]\w*)\s*(?:<[^<>();{}=]*>)?\s*\(", O)] private static partial Regex CallRe();
    [GeneratedRegex(@"(?<![\w.])([A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)", O)] private static partial Regex MemberRe();
    // typeof(T), x is T, x as T, and a cast (T)x - a parenthesised name followed by an operand and not preceded by a
    // name or keyword (that would be a call or if (x) ...)
    [GeneratedRegex(@"\b(?:typeof\s*\(|is\s+|as\s+)([A-Za-z_][\w.]*)|(?<![\w)\]]\s*)\(\s*([A-Za-z_][\w.]*)\s*\)\s*(?=[A-Za-z_@(])", O)] private static partial Regex TypeofRe();
    [GeneratedRegex(@"([A-Za-z_][\w.]*(?:<[^,()]*?>)?(?:\[\s*\])?\??)\s+([A-Za-z_]\w*)\s*(?:=[^,]*)?(?:,|$)", O)] private static partial Regex ParamRe();
    [GeneratedRegex(@"\[\s*assembly\s*:\s*(Assembly\w*Version)\s*\(\s*""([^""]+)""", O | RegexOptions.IgnoreCase)] private static partial Regex AssemblyAttr();
    [GeneratedRegex(@"\[\s*([^\[\]]*)\]", O)] private static partial Regex AttrBracketRe();
    [GeneratedRegex(@"([\w.]+?)(?:Attribute)?\s*(?:\(\s*(""|@"")?|(?=[,\]\s]|$))", O)] private static partial Regex AttrPartRe();
    [GeneratedRegex(@"\G\s*(?::\s*(?:base|this)\s*\((?:[^()]|\([^()]*\))*\)\s*)?(?:where\s[^{;=]*)?(\{|=>|;)", O)] private static partial Regex MethodHeaderRe();
    [GeneratedRegex(@"[A-Za-z_][\w.<>\[\],?]*", O)] private static partial Regex WordRe();
    [GeneratedRegex(@"^((\w+)\s+)*[\w.]+(\s*<[^;{}()]*>)?(\[\s*\])?\??\z", O)] private static partial Regex DeclPrefixRe();
    [GeneratedRegex(@"<.*>", O)] private static partial Regex GenericRe();
    [GeneratedRegex(@"\s+", O)] private static partial Regex WsRe();
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*([A-Za-z_]\w*)\s*(?:=\s*(\S[^,]*?))?\s*$", O)] private static partial Regex EnumMemberRe();
    [GeneratedRegex(@"(?:\b(public|private|protected|internal)\s+)?(?:(?:new|static|unsafe|protected|internal)\s+)*\bconst\s+[\w.<>?\[\]]+\s+([A-Za-z_]\w*)\s*=\s*([^;]+);", O)] private static partial Regex ConstRe();
    [GeneratedRegex(@"\[area\]/?", O)] private static partial Regex AreaRe();

    private static readonly HashSet<string> Modifiers =
    [
        "public", "private", "protected", "internal", "static", "virtual", "override", "abstract", "sealed", "async",
        "extern", "unsafe", "new", "partial", "readonly", "volatile", "const", "implicit", "explicit", "operator",
    ];

    private static readonly HashSet<string> NotMethods =
    [
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "nameof", "typeof", "sizeof",
        "default", "checked", "unchecked", "fixed", "base", "this", "new", "throw", "await", "when", "get", "set",
        "add", "remove", "where", "in", "is", "as", "out", "ref", "params", "yield", "else", "do", "case", "var",
    ];

    private static readonly HashSet<string> BuiltinTypes =
    [
        "int", "long", "short", "byte", "bool", "string", "object", "decimal", "double", "float", "char", "void",
        "var", "dynamic", "uint", "ulong", "ushort", "sbyte", "return", "new", "else", "throw", "await", "case",
        "in", "out", "ref", "params", "using", "goto", "yield", "const", "static", "readonly", "public", "private",
        "nint", "nuint",
    ];

    /// <summary>C#'s own keywords and built-in types - never a type or object of the codebase, whatever its casing.</summary>
    private static bool IsKeyword(string name)
    {
        string l = name.ToLowerInvariant();
        return BuiltinTypes.Contains(l) || NotMethods.Contains(l) || l is "null" or "true" or "false" or "not" or "and" or "or" or "value";
    }

    private static readonly Dictionary<string, string> HttpVerbs = new()
    {
        ["httpget"] = "get", ["httppost"] = "post", ["httpput"] = "put", ["httpdelete"] = "delete",
        ["httppatch"] = "patch", ["httphead"] = "head", ["httpoptions"] = "options",
    };

    private sealed class TypeInfo
    {
        public string Name = "";
        public string Kind = "";
        public int Start, Open, Close, Depth;
        public List<string> Bases = [];
        public List<(string Name, string? Arg)> Attrs = [];
        public string Qual = "";
    }

    private sealed class MethodInfo
    {
        public string Name = "";
        public TypeInfo Type = null!;
        public int Start;
        public List<(string Name, string? Arg)> Attrs = [];
        public string Params = "";
        public int BodyStart, BodyEnd;
        public string Vis = "";
        public string Kind = "";
        public string Sig = "";
    }

    private static string Sub(string s, int a, int b)
    {
        a = Math.Max(0, a);
        b = Math.Min(s.Length, b);
        return b <= a ? "" : s.Substring(a, b - a);
    }

    private static string StripGeneric(string t)
    {
        t = GenericRe().Replace(t, "").Replace("?", "").Replace("[]", "").Trim();
        if (t.Length == 0) return t;
        int i = t.LastIndexOf('.');
        return i < 0 ? t : t[(i + 1)..];
    }

    private static string? Or(string? a, string? b)
    {
        string? r = !string.IsNullOrEmpty(a) ? a : b;
        return string.IsNullOrEmpty(r) ? null : r;
    }

    private static JsonArray StrArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add((JsonNode?)JsonValue.Create(s));
        return a;
    }

    public static FileFacts Parse(string path, string text)
    {
        var (code, strings) = Scrub.CLike(text);
        var li = new LineIndex(code);
        int[] depths = Scrub.DepthMap(code);
        var facts = new FileFacts { Lang = "csharp" };
        var symbols = facts.Symbols;
        var refs = facts.Refs;
        var fileAttrs = new JsonObject();

        foreach (Match m in AssemblyAttr().Matches(text))
            fileAttrs[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;

        var namespaces = NsRe().Matches(code).Select(m => (Pos: m.Index, Name: m.Groups[1].Value)).ToList();

        // string literal lookup by the position of its opening quote (for attribute arguments)
        var strAt = new Dictionary<int, string>();
        foreach (var s in strings)
        {
            int q = text.IndexOf('"', s.Offset);
            if (q >= 0) strAt[q] = s.Value;
            strAt[s.Offset] = s.Value;
        }

        List<(string Name, string? Arg)> AttrsBefore(int pos)
        {
            int k = pos - 1;
            while (k > 0 && ";{}".IndexOf(code[k]) < 0) k--;
            int regionStart = k + 1;
            var found = new List<(string, string?)>();
            string region = Sub(code, regionStart, pos);
            foreach (Match am in AttrBracketRe().Matches(region))
            {
                int innerStart = regionStart + am.Groups[1].Index;
                string inner = am.Groups[1].Value;
                foreach (Match part in AttrPartRe().Matches(inner))
                {
                    string name = part.Groups[1].Value;
                    int dot = name.LastIndexOf('.');
                    if (dot >= 0) name = name[(dot + 1)..];
                    string? arg = null;
                    if (part.Groups[2].Success)
                        strAt.TryGetValue(innerStart + part.Groups[2].Index + (part.Groups[2].Value == "@\"" ? 1 : 0), out arg);
                    found.Add((name, arg));
                }
            }
            return found;
        }

        string? NsAt(int pos)
        {
            string? cur = null;
            foreach (var (p, n) in namespaces)
                if (p <= pos) cur = n;
            return cur;
        }

        var usings = UsingRe().Matches(code).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();

        // ---- types
        var types = new List<TypeInfo>();
        foreach (Match m in TypeRe().Matches(code))
        {
            int openPos = m.Index + m.Length - 1;
            int closePos = Scrub.MatchBrace(code, openPos);
            string kind = m.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            var bases = new List<string>();
            if (m.Groups[5].Success && m.Groups[5].Length > 0)
            {
                int depth = 0;
                var buf = new StringBuilder();
                foreach (char ch in m.Groups[5].Value)
                {
                    if (ch == '<') depth++;
                    else if (ch == '>') depth--;
                    if (ch == ',' && depth == 0)
                    {
                        bases.Add(buf.ToString().Trim());
                        buf.Clear();
                    }
                    else buf.Append(ch);
                }
                if (buf.ToString().Trim().Length > 0) bases.Add(buf.ToString().Trim());
            }
            types.Add(new TypeInfo
            {
                Name = m.Groups[2].Value, Kind = kind, Start = m.Index, Open = openPos, Close = closePos,
                Bases = bases.Where(b => b.Length > 0).Select(StripGeneric).ToList(),
                Depth = depths[openPos] + 1, Attrs = AttrsBefore(m.Index),
            });
        }

        TypeInfo? InnermostType(int pos)
        {
            TypeInfo? best = null;
            foreach (var t in types)
                if (t.Open < pos && pos < t.Close && (best == null || t.Open > best.Open)) best = t;
            return best;
        }

        foreach (var t in types)
        {
            var parent = InnermostType(t.Start);
            t.Qual = (parent != null ? parent.Qual + "." : "") + t.Name;
            var sym = new Symbol
            {
                Name = t.Qual, Kind = t.Kind, Line = li.Line(t.Start), Parent = null, Visibility = "public",
                Lines = li.Line(t.Close) - li.Line(t.Start) + 1,
            };
            string? ns = NsAt(t.Start);
            if (ns != null) sym.X["namespace"] = ns;
            sym.X["bases"] = StrArray(t.Bases);
            symbols.Add(sym);
            foreach (var b in t.Bases)
                refs.Add(new Ref { From = null, FromParent = t.Qual, Kind = "inherit", Name = b, Line = li.Line(t.Start) });
        }

        // ---- fields / properties per type (name -> type) for member-call resolution
        var fieldTypes = new Dictionary<string, Dictionary<string, string>>();
        var fieldOrder = new List<string>();
        foreach (var t in types)
            if (!fieldTypes.ContainsKey(t.Qual)) { fieldTypes[t.Qual] = []; fieldOrder.Add(t.Qual); }
        foreach (var t in types)
        {
            string body = Sub(code, t.Open + 1, t.Close);
            foreach (Match fm in FieldRe().Matches(body))
            {
                int pos = t.Open + 1 + fm.Groups[1].Index;
                if (pos >= depths.Length || depths[pos] != t.Depth) continue;
                string typ = fm.Groups[1].Value;
                if (Modifiers.Contains(typ) || NotMethods.Contains(typ) || typ is "return" or "using" or "namespace" or "class") continue;
                fieldTypes[t.Qual][fm.Groups[2].Value.ToLowerInvariant()] = StripGeneric(typ);
            }
        }

        // ---- methods
        var methods = new List<MethodInfo>();
        foreach (var t in types)
        {
            if (t.Kind == "enum") continue;
            int bodyStart = t.Open + 1, bodyEnd = Math.Min(t.Close, code.Length);
            if (bodyEnd <= bodyStart) continue;
            for (Match mm = MethodNameRe().Match(code, bodyStart, bodyEnd - bodyStart); mm.Success; mm = mm.NextMatch())
            {
                string name = mm.Groups[1].Value;
                int pos = mm.Groups[1].Index;
                if (depths[pos] != t.Depth || NotMethods.Contains(name) || Modifiers.Contains(name)) continue;
                // statement prefix must look like a declaration: [attributes] modifiers ReturnType
                int k = pos - 1;
                while (k > bodyStart && ";{}]".IndexOf(code[k]) < 0) k--;
                string prefix = Sub(code, k + 1, pos).Trim();
                if (prefix.Contains('=') || prefix.Contains('(')) continue;
                var words = WordRe().Matches(prefix).Select(w => w.Value).ToList();
                if (words.Count == 0 && name != t.Name) continue;
                if (words.Count > 0 && words[^1] is "return" or "new" or "await" or "throw" or "else" or "case" or "goto" or "yield") continue;
                if (words.Take(words.Count - 1).Any(w => !Modifiers.Contains(w)) && !DeclPrefixRe().IsMatch(prefix)) continue;
                int parenOpen = code.IndexOf('(', mm.Index + mm.Length - 1);
                int parenClose = Scrub.MatchBrace(code, parenOpen, '(', ')');
                string rest = Sub(code, parenClose + 1, parenClose + 400);
                var hm = MethodHeaderRe().Match(rest);
                if (!hm.Success || hm.Index != 0) continue;
                string ender = hm.Groups[1].Value;
                int hdrEnd = parenClose + 1 + hm.Length - 1;
                int bclose;
                if (ender == "{") bclose = Scrub.MatchBrace(code, hdrEnd);
                else if (ender == "=>")
                {
                    bclose = code.IndexOf(';', hdrEnd);
                    if (bclose < 0) bclose = code.Length - 1;
                }
                else bclose = hdrEnd;
                string vis = words.FirstOrDefault(w => w is "public" or "private" or "protected" or "internal")
                             ?? (t.Kind == "interface" ? "public" : "private");
                string kind = name == t.Name ? "constructor" : "method";
                string sig = WsRe().Replace((prefix + " " + name + "(" + Sub(text, parenOpen + 1, parenClose) + ")").Trim(), " ");
                if (sig.Length > 200) sig = sig[..200];
                methods.Add(new MethodInfo
                {
                    Name = name, Type = t, Start = pos, Attrs = AttrsBefore(pos), Params = Sub(code, parenOpen + 1, parenClose),
                    BodyStart = hdrEnd, BodyEnd = bclose, Vis = vis, Kind = kind, Sig = sig,
                });
            }
        }

        var seen = new Dictionary<(string, string), Symbol>();
        foreach (var me in methods)
        {
            var key = (me.Type.Qual, me.Name);
            int lines = li.Line(me.BodyEnd) - li.Line(me.Start) + 1;
            var routes = RoutesFor(me, me.Type);
            if (seen.TryGetValue(key, out var prev)) // overloads merged into one node
            {
                prev.X["overloads"] = (prev.X["overloads"]?.GetValue<int>() ?? 1) + 1;
                prev.Lines += lines;
                if (routes.Count > 0)
                {
                    if (prev.X["routes"] is not JsonArray ra) prev.X["routes"] = ra = new JsonArray();
                    foreach (var r in routes) ra.Add((JsonNode?)r);
                }
                continue;
            }
            var sym = new Symbol
            {
                Name = me.Name, Kind = me.Kind, Line = li.Line(me.Start), Parent = me.Type.Qual,
                Visibility = me.Vis, Signature = me.Sig, Lines = lines,
            };
            if (routes.Count > 0)
            {
                var ra = new JsonArray();
                foreach (var r in routes) ra.Add((JsonNode?)r);
                sym.X["routes"] = ra;
            }
            seen[key] = sym;
            symbols.Add(sym);
        }

        // ---- enum members and constants (with their values; SQL in a constant belongs to the constant)
        foreach (var t in types)
        {
            int bs = t.Open + 1, be = Math.Min(t.Close, code.Length);
            if (be <= bs) continue;
            if (t.Kind == "enum")
            {
                long next = 0;
                int start = bs;
                for (int i = bs; i <= be; i++)
                {
                    if (i < be && (code[i] != ',' || depths[i] != t.Depth)) continue;
                    int partAt = start;
                    start = i + 1;
                    var em = EnumMemberRe().Match(code[partAt..i]);
                    if (!em.Success) continue;
                    string value = em.Groups[2].Success ? WsRe().Replace(text.Substring(partAt + em.Groups[2].Index, em.Groups[2].Length).Trim(), " ") : next.ToString();
                    next = long.TryParse(value, out long nv) ? nv + 1 : next + 1;
                    var ms = new Symbol { Name = em.Groups[1].Value, Kind = "enum_member", Line = li.Line(partAt + em.Groups[1].Index), Parent = t.Qual, Visibility = "public" };
                    ms.X["value"] = value.Length > 120 ? value[..117] + "..." : value;
                    symbols.Add(ms);
                }
                continue;
            }
            for (Match cm = ConstRe().Match(code, bs, be - bs); cm.Success; cm = cm.NextMatch())
            {
                if (depths[cm.Index] != t.Depth) continue;
                string raw = text.Substring(cm.Groups[3].Index, cm.Groups[3].Length).Trim();
                var cs = new Symbol
                {
                    Name = cm.Groups[2].Value, Kind = "constant", Line = li.Line(cm.Groups[2].Index), Parent = t.Qual,
                    Visibility = cm.Groups[1].Success ? cm.Groups[1].Value : "private",
                };
                string shown = WsRe().Replace(raw, " ");
                cs.X["value"] = shown.Length > 120 ? shown[..117] + "..." : shown;
                symbols.Add(cs);
                string sql = string.Join(SqlText.PieceSeparator, Scrub.CLike(raw).Strings.Select(s => s.Value));
                if (SqlText.LooksLikeSql(sql))
                    foreach (var (mode, nm) in SqlText.Refs(sql).OrderBy(f => f.Mode, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal))
                        refs.Add(new Ref { From = cs.Name, FromParent = t.Qual, Kind = "sql", Mode = mode, Name = nm, Line = cs.Line });
            }
        }

        // ---- references inside method bodies
        List<Ref> BodyRefs(string owner, string parentQual, int a, int b, string pars)
        {
            var local = new Dictionary<string, string>();
            var localOrder = new List<string>();
            void SetLocal(string k, string v, bool overwrite)
            {
                if (!local.ContainsKey(k)) { localOrder.Add(k); local[k] = v; }
                else if (overwrite) local[k] = v;
            }
            foreach (Match pm in ParamRe().Matches(pars.Trim()))
                SetLocal(pm.Groups[2].Value.ToLowerInvariant(), StripGeneric(pm.Groups[1].Value), true);
            string seg = Sub(code, a, b + 1);
            foreach (Match lm in LocalRe().Matches(seg))
            {
                string typ = lm.Groups[1].Value;
                string last = typ.Substring(typ.LastIndexOf('.') + 1).ToLowerInvariant();
                if (BuiltinTypes.Contains(last) || NotMethods.Contains(typ)) continue;
                SetLocal(lm.Groups[2].Value.ToLowerInvariant(), StripGeneric(typ), false);
            }
            foreach (Match vm in VarNewRe().Matches(seg))
                SetLocal(vm.Groups[1].Value.ToLowerInvariant(), StripGeneric(vm.Groups[2].Value), true);
            fieldTypes.TryGetValue(parentQual, out var fields);
            string? Field(string k) => fields != null && fields.TryGetValue(k, out var v) ? v : null;
            string? Local(string k) => local.TryGetValue(k, out var v) ? v : null;

            var outList = new List<Ref>();
            var outKeys = new HashSet<(string, string?, string)>();
            void Add(Ref r)
            {
                var key = (r.Kind, r.Qual, r.Name);
                if (outKeys.Add(key))
                {
                    r.From = owner;
                    r.FromParent = parentQual;
                    outList.Add(r);
                }
            }

            foreach (Match nm in NewRe().Matches(seg))
                Add(new Ref { Kind = "new", Name = StripGeneric(nm.Groups[1].Value), Line = li.Line(a + nm.Index) });
            foreach (Match cm in CallRe().Matches(seg))
            {
                string? qual = cm.Groups[1].Success ? cm.Groups[1].Value : null;
                string name = cm.Groups[2].Value;
                if (NotMethods.Contains(name) && qual == null) continue;
                int line = li.Line(a + cm.Index);
                if (qual != null)
                {
                    string ql = qual.ToLowerInvariant();
                    string? qtype = ql == "this" ? parentQual : Or(Local(ql), Field(ql));
                    Add(new Ref { Kind = "member", Qual = qual, QualType = qtype, Name = name, Line = line });
                }
                else Add(new Ref { Kind = "call", Name = name, Line = line });
            }
            foreach (Match mm in MemberRe().Matches(seg))
            {
                string q = mm.Groups[1].Value, ql = q.ToLowerInvariant();
                if (ql != "this" && IsKeyword(q)) continue;
                string? qtype = ql == "this" ? parentQual : Or(Local(ql), Field(ql));
                Add(new Ref { Kind = "member", Qual = q, QualType = qtype, Name = mm.Groups[2].Value, Line = li.Line(a + mm.Index) });
            }
            foreach (Match tm in TypeofRe().Matches(seg))
            {
                string t = tm.Groups[1].Success ? tm.Groups[1].Value : tm.Groups[2].Value;
                // (x) where x is a local or parameter is a parenthesised value, not a cast
                if (IsKeyword(t) || (tm.Groups[2].Success && Local(t.ToLowerInvariant()) is not null)) continue;
                Add(new Ref { Kind = "type", Name = StripGeneric(t), Line = li.Line(a + tm.Index) });
            }
            foreach (var v in localOrder)
            {
                string t = local[v];
                if (t.Length > 0 && !IsKeyword(t))
                    Add(new Ref { Kind = "type", Name = t, Line = li.Line(a) });
            }
            return outList;
        }

        foreach (var me in methods)
            refs.AddRange(BodyRefs(me.Name, me.Type.Qual, me.BodyStart, me.BodyEnd, me.Params));
        foreach (var tq in fieldOrder)
            foreach (var t in fieldTypes[tq].Values.Distinct())
                if (t.Length > 0 && !IsKeyword(t))
                    refs.Add(new Ref { From = null, FromParent = tq, Kind = "type", Name = t, Line = 1 });

        // ---- strings: SQL text and object-name literals
        MethodInfo? OwnerAt(int pos)
        {
            MethodInfo? best = null;
            foreach (var me in methods)
                if (me.Start <= pos && pos <= me.BodyEnd && (best == null || me.Start > best.Start)) best = me;
            return best;
        }

        var grouped = new Dictionary<(bool IsMethod, string? Parent, string? Owner), List<StringLit>>();
        var groupOrder = new List<(bool IsMethod, string? Parent, string? Owner)>();
        foreach (var s in strings)
        {
            var me = OwnerAt(s.Offset);
            (bool IsMethod, string? Parent, string? Owner) key;
            if (me == null)
            {
                var t = InnermostType(s.Offset);
                key = (false, t?.Qual, null);
            }
            else key = (true, me.Type.Qual, me.Name);
            if (!grouped.TryGetValue(key, out var list)) { grouped[key] = list = []; groupOrder.Add(key); }
            list.Add(s);
        }
        foreach (var key in groupOrder)
        {
            var items = grouped[key];
            string? owner = key.IsMethod ? key.Owner : null;
            string? parent = key.Parent;
            string joined = string.Join(SqlText.PieceSeparator, items.Select(i => i.Value));
            int line = li.Line(items[0].Offset);
            if (SqlText.LooksLikeSql(joined))
                foreach (var (mode, nm) in SqlText.Refs(joined).OrderBy(r => r.Mode, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal))
                    refs.Add(new Ref { From = owner, FromParent = parent, Kind = "sql", Mode = mode, Name = nm, Line = line });
            foreach (var it in items)
            {
                string? lit = SqlText.LiteralIdentifier(it.Value);
                if (lit != null)
                    refs.Add(new Ref { From = owner, FromParent = parent, Kind = "literal", Name = lit, Line = li.Line(it.Offset) });
            }
        }

        if (namespaces.Count > 0) facts.X["namespace"] = namespaces[0].Name;
        facts.X["usings"] = StrArray(usings);
        facts.X["attrs"] = fileAttrs;
        facts.Loc = SourceText.CountLines(text);
        return facts;
    }

    /// <summary>HTTP routes served by a controller action (attribute routing, or MVC convention /Controller/Action).</summary>
    private static List<JsonObject> RoutesFor(MethodInfo me, TypeInfo t)
    {
        var none = new List<JsonObject>();
        if (me.Kind == "constructor" || me.Vis != "public") return none;
        bool isController = t.Name.EndsWith("Controller", StringComparison.Ordinal);
        string ctrl = isController ? t.Name[..^10] : t.Name;
        var prefixes = t.Attrs.Where(a => (a.Name is "Route" or "RoutePrefix") && a.Arg != null).Select(a => a.Arg!).ToList();
        var verbs = new List<string>();
        var templates = new List<string>();
        bool hasRouteAttr = false;
        foreach (var (name, arg) in me.Attrs)
        {
            string low = name.ToLowerInvariant();
            if (HttpVerbs.TryGetValue(low, out var verb))
            {
                verbs.Add(verb);
                if (arg != null) { templates.Add(arg); hasRouteAttr = true; }
            }
            else if (low == "route" && arg != null)
            {
                templates.Add(arg);
                hasRouteAttr = true;
            }
            else if (low == "nonaction") return none;
        }
        if (!isController && !hasRouteAttr) return none;
        var paths = new List<string>();
        if (templates.Count > 0)
        {
            foreach (var tp in templates)
            {
                if (tp.StartsWith('/') || tp.StartsWith("~/", StringComparison.Ordinal) || prefixes.Count == 0) paths.Add(tp);
                else paths.AddRange(prefixes.Select(p => p.TrimEnd('/') + "/" + tp));
            }
        }
        else if (prefixes.Count > 0 && verbs.Count > 0) paths.AddRange(prefixes);
        else if (prefixes.Count == 0) paths.Add($"/{ctrl}/{me.Name}"); // conventional MVC / Web API route
        var result = new List<JsonObject>();
        foreach (var p0 in paths)
        {
            string p = p0.Replace("[controller]", ctrl).Replace("[action]", me.Name);
            p = AreaRe().Replace(p, "");
            foreach (var v in verbs.Count > 0 ? verbs.Select(x => (string?)x) : [null])
                result.Add(new JsonObject
                {
                    ["verb"] = v,
                    ["path"] = Routes.Normalize(p),
                    ["conventional"] = !hasRouteAttr && prefixes.Count == 0,
                });
        }
        return result;
    }
}
