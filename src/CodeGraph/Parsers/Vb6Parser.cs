using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>Port of codegraph/parsers/vb6.py: VB6 (.frm .bas .cls .ctl .dsr .pag .dob) and best-effort VB.NET (.vb).</summary>
public static partial class Vb6Parser
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Dictionary<string, string> KindByExt = new()
    {
        [".frm"] = "form", [".bas"] = "module", [".cls"] = "class", [".ctl"] = "usercontrol",
        [".dsr"] = "designer", [".pag"] = "propertypage", [".dob"] = "userdocument", [".vb"] = "module",
    };

    [GeneratedRegex(@"^\s*(?:(Public|Private|Friend|Global|Protected)\s+)?(?:(?:Static|Shared|Overrides|Overridable|Overloads|MustOverride|NotOverridable|Shadows|Async|Iterator|ReadOnly|WriteOnly|Default)\s+)*(Sub|Function|Property\s+Get|Property\s+Let|Property\s+Set|Property|Operator|Event)\s+([A-Za-z_]\w*)", O)]
    private static partial Regex RoutineStart();
    [GeneratedRegex(@"^\s*End\s+(Sub|Function|Property|Operator|Event)\b", O)]
    private static partial Regex RoutineEnd();
    [GeneratedRegex(@"^\s*(?:(Public|Private)\s+)?Declare\s+(?:PtrSafe\s+)?(Sub|Function)\s+(\w+)\s+Lib\s+""", O)]
    private static partial Regex Declare();
    [GeneratedRegex(@"\bLib\s+""([^""]+)""(?:\s+Alias\s+""([^""]+)"")?", O)]
    private static partial Regex DeclareFull();
    [GeneratedRegex(@"^\s*(Dim|Private|Public|Global|Static|Friend|Protected|Shared|Const)\s+(.*)$", O)]
    private static partial Regex VarDecl();
    [GeneratedRegex(@"(?:WithEvents\s+)?([A-Za-z_]\w*)\s*(?:\([^)]*\))?\s+As\s+(New\s+)?([A-Za-z_][\w.]*)", O)]
    private static partial Regex VarItem();
    [GeneratedRegex(@"(?:Optional\s+)?(?:ByVal\s+|ByRef\s+)?(?:ParamArray\s+)?([A-Za-z_]\w*)\s*(?:\(\s*\))?\s+As\s+([A-Za-z_][\w.]*)", O)]
    private static partial Regex ParamItem();
    [GeneratedRegex(@"\bNew\s+([A-Za-z_][\w.]*)", O)]
    private static partial Regex NewRe();
    [GeneratedRegex(@"\bAs\s+(?:New\s+)?([A-Za-z_][\w.]*)", O)]
    private static partial Regex AsRe();
    [GeneratedRegex(@"(?<![\w.!])([A-Za-z_]\w*)(?:\s*\.\s*([A-Za-z_]\w*))?", RegexOptions.CultureInvariant)]
    private static partial Regex IdentRe();
    [GeneratedRegex(@"^\s*With\s+([A-Za-z_]\w*)\s*$", O)]
    private static partial Regex WithRe();
    [GeneratedRegex(@"^\s*End\s+With\b", O)]
    private static partial Regex EndWithRe();
    [GeneratedRegex(@"(?<![\w)\]])\.\s*([A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMemberRe();
    [GeneratedRegex(@"^\s*(?:(?:Public|Private|Friend|Protected|Partial|MustInherit|NotInheritable|Shared)\s+)*(Class|Module|Interface|Structure|Enum)\s+([A-Za-z_]\w*)", O)]
    private static partial Regex VbNetType();
    [GeneratedRegex(@"^\s*(Inherits|Implements)\s+([\w.,\s]+)$", O)]
    private static partial Regex VbNetInherits();
    [GeneratedRegex(@"^\s*End\s+(Class|Module|Interface|Structure|Enum)\b", O)]
    private static partial Regex VbNetEnd();
    [GeneratedRegex(@"^\s*Attribute\s+VB_Name\s*=\s*""([^""]+)""", O | RegexOptions.Multiline)]
    private static partial Regex VbNameRe();
    [GeneratedRegex(@"^\s*Attribute\s+VB_", O)]
    private static partial Regex AttrVb();
    [GeneratedRegex(@"^\s*Attribute\s+", O)]
    private static partial Regex AttrAny();
    [GeneratedRegex(@"^\s*Begin\s+([\w]+)\.([\w]+)\s+(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex BeginRe();
    [GeneratedRegex(@"^(Form|UserControl|Class|MDIForm)_\w+$", O)]
    private static partial Regex HandlerRe();
    [GeneratedRegex(@"\bMustOverride\b", O)]
    private static partial Regex MustOverrideRe();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WsRe();
    [GeneratedRegex(@"\((.*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ParenRe();
    [GeneratedRegex(@"\.\s*(AddNew|Update|UpdateBatch|Delete)\b(?!\s*=)", RegexOptions.CultureInvariant)]
    private static partial Regex RsWriteRe();

    private static readonly HashSet<string> VbKeywords = new(
        ("and as boolean byref byte byval call case const currency date debug declare dim do double each " +
         "else elseif empty end enum eqv erase error event exit false for friend function get global gosub goto if " +
         "imp in integer is let lib like long loop lset me mod new next not nothing null object on option optional " +
         "or paramarray preserve private property public raiseevent redim rem resume return rset select set single " +
         "static step stop string sub then to true type typeof until variant wend while with withevents xor " +
         "explicit compare binary text base attribute begin version")
        .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private sealed class Routine
    {
        public string Name = "", Kind = "", Vis = "", Sig = "";
        public int Start, End;
    }

    private sealed class VbType
    {
        public string Name = "", Kind = "";
        public int Start;
        public int? End;
        public List<string> Bases = [];
    }

    public static FileFacts Parse(string path, string text)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string lang = ext == ".vb" ? "vbnet" : "vb6";
        string compKind = KindByExt.GetValueOrDefault(ext, "module");

        var nameM = VbNameRe().Match(text);
        string compName = nameM.Success ? nameM.Groups[1].Value : Path.GetFileNameWithoutExtension(path);

        var lines = text.Split('\n');
        var controls = new List<string>();
        var controlTypes = new SortedSet<string>(StringComparer.Ordinal);
        int codeStart = 0;
        if (ext is ".frm" or ".ctl" or ".dsr" or ".pag" or ".dob" or ".cls" or ".bas")
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (AttrVb().IsMatch(ln)) codeStart = i + 1;
                else if (codeStart != 0 && ln.Trim().Length > 0 && !AttrAny().IsMatch(ln)) break;
            }
            for (int i = 0; i < codeStart && i < lines.Length; i++)
            {
                var m = BeginRe().Match(lines[i]);
                if (!m.Success) continue;
                string typ = m.Groups[2].Value;
                if (!typ.Equals("form", StringComparison.OrdinalIgnoreCase) &&
                    !typ.Equals("usercontrol", StringComparison.OrdinalIgnoreCase) &&
                    !typ.Equals("propertypage", StringComparison.OrdinalIgnoreCase))
                {
                    controls.Add(m.Groups[3].Value);
                    controlTypes.Add(m.Groups[1].Value + "." + typ);
                }
            }
        }
        var codeLinesIn = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++) codeLinesIn[i] = i < codeStart ? "" : lines[i];
        string codeText = string.Join("\n", codeLinesIn);
        var (code, strings) = Scrub.Vb(codeText);
        var li = new LineIndex(code);
        var codeLines = code.Split('\n');

        string LogicalLine(int idx)
        {
            string s = codeLines[idx];
            int j = idx;
            while (s.TrimEnd().EndsWith(" _", StringComparison.Ordinal) && j + 1 < codeLines.Length)
            {
                j++;
                string t = s.TrimEnd();
                s = t[..^1] + " " + codeLines[j].Trim();
            }
            return s;
        }

        var facts = new FileFacts { Lang = lang };
        var symbols = facts.Symbols;
        var refs = facts.Refs;
        var moduleVars = new Dictionary<string, (string Type, string Vis)>();

        var vbnetTypes = new List<VbType>();
        if (lang == "vbnet")
        {
            var stack = new List<VbType>();
            for (int i = 0; i < codeLines.Length; i++)
            {
                string ln = codeLines[i];
                var m = VbNetType().Match(ln);
                if (m.Success)
                {
                    stack.Add(new VbType { Name = m.Groups[2].Value, Kind = m.Groups[1].Value.ToLowerInvariant(), Start = i + 1 });
                    continue;
                }
                m = VbNetInherits().Match(ln);
                if (m.Success && stack.Count > 0)
                {
                    foreach (var x in m.Groups[2].Value.Split(','))
                        if (x.Trim().Length > 0) stack[^1].Bases.Add(x.Trim());
                    continue;
                }
                m = VbNetEnd().Match(ln);
                if (m.Success && stack.Count > 0)
                {
                    var t = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    t.End = i + 1;
                    vbnetTypes.Add(t);
                }
            }
        }

        Func<int, string> containerFor;
        if (lang == "vb6" || vbnetTypes.Count == 0)
        {
            var comp = new Symbol { Name = compName, Kind = compKind, Line = 1, Parent = null, Visibility = "public" };
            comp.X["controls"] = controls.Count;
            var ct = new JsonArray();
            foreach (var c in controlTypes) ct.Add((JsonNode?)c);
            comp.X["control_types"] = ct;
            symbols.Add(comp);
            containerFor = _ => compName;
        }
        else
        {
            foreach (var t in vbnetTypes)
            {
                var sym = new Symbol
                {
                    Name = t.Name, Kind = t.Kind != "structure" ? t.Kind : "struct", Line = t.Start,
                    Parent = null, Visibility = "public",
                };
                sym.X["end"] = t.End;
                var ba = new JsonArray();
                foreach (var b in t.Bases) ba.Add((JsonNode?)b);
                sym.X["bases"] = ba;
                symbols.Add(sym);
                foreach (var b in t.Bases)
                    refs.Add(new Ref { From = t.Name, Kind = "inherit", Name = b.Split('(')[0].Trim(), Line = t.Start });
            }
            containerFor = line =>
            {
                VbType? best = null;
                foreach (var t in vbnetTypes)
                    if (t.Start <= line && line <= (t.End ?? 1_000_000_000) && (best is null || t.Start >= best.Start))
                        best = t;
                return best?.Name ?? compName;
            };
        }

        // ---- find routines
        var routines = new List<Routine>();
        Routine? openR = null;
        for (int i = 0; i < codeLines.Length; i++)
        {
            string ln = codeLines[i];
            if (openR is null)
            {
                var m = Declare().Match(ln);
                if (m.Success)
                {
                    string vis = (m.Groups[1].Success ? m.Groups[1].Value : "public").ToLowerInvariant();
                    var dm = DeclareFull().Match(lines[i]);
                    string? lib = dm.Success ? dm.Groups[1].Value : null;
                    string alias = dm.Success && dm.Groups[2].Success && dm.Groups[2].Value.Length > 0 ? dm.Groups[2].Value : m.Groups[3].Value;
                    symbols.Add(new Symbol { Name = m.Groups[3].Value, Kind = "api_declare", Line = i + 1, Parent = containerFor(i + 1), Visibility = vis });
                    if (lib is not null)
                    {
                        var r = new Ref { From = m.Groups[3].Value, FromParent = containerFor(i + 1), Kind = "native", Name = alias, Line = i + 1 };
                        r.X["lib"] = lib;
                        refs.Add(r);
                    }
                    continue;
                }
                m = RoutineStart().Match(ln);
                if (m.Success)
                {
                    string vis = (m.Groups[1].Success ? m.Groups[1].Value : "public").ToLowerInvariant();
                    string kind = WsRe().Replace(m.Groups[2].Value.ToLowerInvariant(), "_");
                    if (kind == "event")
                    {
                        symbols.Add(new Symbol { Name = m.Groups[3].Value, Kind = "event", Line = i + 1, Parent = containerFor(i + 1), Visibility = vis });
                        continue;
                    }
                    if (lang == "vbnet" && MustOverrideRe().IsMatch(ln)) continue;
                    openR = new Routine { Name = m.Groups[3].Value, Kind = kind, Vis = vis, Start = i, Sig = LogicalLine(i) };
                }
                else
                {
                    m = VarDecl().Match(ln);
                    if (m.Success)
                    {
                        string vis = m.Groups[1].Value.ToLowerInvariant();
                        foreach (Match vm in VarItem().Matches(m.Groups[2].Value))
                            moduleVars[vm.Groups[1].Value.ToLowerInvariant()] = (vm.Groups[3].Value, vis);
                    }
                }
            }
            else if (RoutineEnd().IsMatch(ln))
            {
                openR.End = i;
                routines.Add(openR);
                openR = null;
            }
        }
        if (openR is not null)
        {
            openR.End = codeLines.Length - 1;
            routines.Add(openR);
        }

        // property get/let/set share a name -> merge into one symbol
        var seen = new Dictionary<(string, string), Symbol>();
        foreach (var r in routines)
        {
            string parent = containerFor(r.Start + 1);
            var key = (parent, r.Name.ToLowerInvariant());
            string kind = r.Kind.StartsWith("property", StringComparison.Ordinal) ? "property" : r.Kind;
            bool isHandler = HandlerRe().IsMatch(r.Name) ||
                controls.Any(c => r.Name.StartsWith(c + "_", StringComparison.OrdinalIgnoreCase));
            if (seen.TryGetValue(key, out var prev))
            {
                prev.Lines += r.End - r.Start + 1;
                continue;
            }
            string sig = WsRe().Replace(r.Sig.Trim(), " ");
            var sym = new Symbol
            {
                Name = r.Name, Kind = isHandler ? "event_handler" : kind, Line = r.Start + 1, Parent = parent,
                Visibility = r.Vis, Signature = sig.Length > 200 ? sig[..200] : sig, Lines = r.End - r.Start + 1,
            };
            seen[key] = sym;
            symbols.Add(sym);
        }

        // ---- references
        var ownerOfLine = new int[codeLines.Length + 1];
        Array.Fill(ownerOfLine, -1);
        for (int ri = 0; ri < routines.Count; ri++)
            for (int k = routines[ri].Start; k <= routines[ri].End && k < ownerOfLine.Length; k++)
                ownerOfLine[k] = ri;

        string? ModuleVarType(string head) =>
            moduleVars.TryGetValue(head.ToLowerInvariant(), out var mv) ? mv.Type : null;

        List<Ref> Collect(int lineFrom, int lineTo, string? ownerName, Dictionary<string, string> localTypes, string? ownerParent)
        {
            var localRefs = new List<Ref>();
            var keys = new HashSet<(string, string?, string?, string?)>();
            void Add(Ref r)
            {
                if (!keys.Add((r.Kind, r.Name, r.Qual, r.Mode))) return;
                r.From = ownerName;
                r.FromParent = ownerParent;
                localRefs.Add(r);
            }
            string? TypeOf(string head) =>
                localTypes.TryGetValue(head.ToLowerInvariant(), out var t) && !string.IsNullOrEmpty(t) ? t : ModuleVarType(head);

            var withStack = new List<string>();
            for (int k = lineFrom; k <= lineTo; k++)
            {
                string ln = codeLines[k];
                if (ln.Trim().Length == 0) continue;
                if (k == lineFrom && ownerName is not null)
                {
                    foreach (Match m0 in AsRe().Matches(ln))
                        Add(new Ref { Kind = "type", Name = m0.Groups[1].Value, Line = k + 1 });
                    continue;
                }
                var m = WithRe().Match(ln);
                if (m.Success) withStack.Add(m.Groups[1].Value);
                else if (EndWithRe().IsMatch(ln) && withStack.Count > 0) withStack.RemoveAt(withStack.Count - 1);
                foreach (Match nm in NewRe().Matches(ln))
                    Add(new Ref { Kind = "new", Name = nm.Groups[1].Value, Line = k + 1 });
                foreach (Match am in AsRe().Matches(ln))
                    Add(new Ref { Kind = "type", Name = am.Groups[1].Value, Line = k + 1 });
                foreach (Match im in IdentRe().Matches(ln))
                {
                    string head = im.Groups[1].Value;
                    string lh = head.ToLowerInvariant();
                    bool hasMember = im.Groups[2].Success;
                    if (VbKeywords.Contains(lh) && lh != "me") continue;
                    if (hasMember)
                    {
                        string? qt = TypeOf(head);
                        if (lh == "me") qt = ownerParent;
                        Add(new Ref { Kind = "member", Qual = head, QualType = qt, Name = im.Groups[2].Value, Line = k + 1 });
                    }
                    else Add(new Ref { Kind = "id", Name = head, Line = k + 1 });
                }
                if (withStack.Count > 0)
                    foreach (Match lm in LeadingMemberRe().Matches(ln))
                    {
                        string head = withStack[^1];
                        Add(new Ref { Kind = "member", Qual = head, QualType = TypeOf(head), Name = lm.Groups[1].Value, Line = k + 1 });
                    }
            }
            return localRefs;
        }

        foreach (var r in routines)
        {
            var localTypes = new Dictionary<string, string>();
            var pm = ParenRe().Match(r.Sig);
            if (pm.Success)
                foreach (Match p in ParamItem().Matches(pm.Groups[1].Value))
                    localTypes[p.Groups[1].Value.ToLowerInvariant()] = p.Groups[2].Value;
            for (int k = r.Start; k <= r.End; k++)
            {
                var m = VarDecl().Match(codeLines[k]);
                if (m.Success)
                    foreach (Match vm in VarItem().Matches(m.Groups[2].Value))
                        localTypes[vm.Groups[1].Value.ToLowerInvariant()] = vm.Groups[3].Value;
            }
            refs.AddRange(Collect(r.Start, r.End, r.Name, localTypes, containerFor(r.Start + 1)));
        }

        // declarations / statements outside routines
        for (int k = 0; k < codeLines.Length; k++)
        {
            if (ownerOfLine[k] != -1 || codeLines[k].Trim().Length == 0) continue;
            foreach (var r in Collect(k, k, null, [], null))
                if (r.Kind is "new" or "type") refs.Add(r);
        }

        // ---- strings -> SQL / literals, attributed to the routine they sit in
        var order = new List<int>();
        var byOwner = new Dictionary<int, List<(int Line, string S)>>();
        foreach (var (pos, s) in strings)
        {
            int line = li.Line(pos);
            int ri = line - 1 >= 0 && line - 1 < ownerOfLine.Length ? ownerOfLine[line - 1] : -1;
            if (!byOwner.TryGetValue(ri, out var list))
            {
                byOwner[ri] = list = [];
                order.Add(ri);
            }
            list.Add((line, s));
        }
        foreach (int ri in order)
        {
            var items = byOwner[ri];
            var r = ri >= 0 ? routines[ri] : null;
            string? owner = r?.Name;
            string? parent = r is not null ? containerFor(r.Start + 1) : null;
            string joined = string.Join(" ", items.Select(i => i.S));
            if (SqlText.LooksLikeSql(joined))
            {
                var found = SqlText.Refs(joined).OrderBy(f => f.Mode, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal);
                string body = r is not null ? string.Join("\n", codeLines[r.Start..(r.End + 1)]) : "";
                bool rsWrite = RsWriteRe().IsMatch(body);
                foreach (var (mode, nm) in found)
                {
                    refs.Add(new Ref { From = owner, FromParent = parent, Kind = "sql", Mode = mode, Name = nm, Line = items[0].Line });
                    if (rsWrite && mode == "read")
                        refs.Add(new Ref { From = owner, FromParent = parent, Kind = "sql", Mode = "write", Name = nm, Line = items[0].Line, Via = "recordset" });
                }
            }
            foreach (var (line, s) in items)
            {
                string? lit = SqlText.LiteralIdentifier(s);
                if (lit is not null)
                    refs.Add(new Ref { From = owner, FromParent = parent, Kind = "literal", Name = lit, Line = line });
            }
        }

        var globals = new JsonArray();
        foreach (var (k, (t, v)) in moduleVars)
            if ((v is "public" or "global") && compKind == "module")
                globals.Add((JsonNode)new JsonObject { ["name"] = k, ["type"] = t, ["visibility"] = v });
        facts.X["component"] = compName;
        facts.X["globals"] = globals;
        facts.Loc = SourceText.CountLines(text);
        return facts;
    }
}
