using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;

namespace CodeGraph.Output;

/// <summary>Counts returned by <see cref="Obsidian.WriteVault"/>; prints like the Python dict.</summary>
public sealed record VaultResult(int Written, int Unchanged, int Deleted, int Notes)
{
    public override string ToString() => $"{{'written': {Written}, 'unchanged': {Unchanged}, 'deleted': {Deleted}, 'notes': {Notes}}}";
}

/// <summary>Write the graph as an Obsidian vault of linked markdown notes (one note per file/project/DB object...).</summary>
public static partial class Obsidian
{
    public static readonly string[] DbDirKinds = ["db_table", "db_view", "db_proc", "db_function", "db_trigger", "db_type", "db_synonym"];

    public static readonly Dictionary<string, string> DbDirs = new()
    {
        ["db_table"] = "Tables", ["db_view"] = "Views", ["db_proc"] = "Procedures", ["db_function"] = "Functions",
        ["db_trigger"] = "Triggers", ["db_type"] = "Types", ["db_synonym"] = "Synonyms",
    };

    public static readonly Dictionary<string, string> DbLabel = new()
    {
        ["db_table"] = "table", ["db_view"] = "view", ["db_proc"] = "stored procedure", ["db_function"] = "function",
        ["db_trigger"] = "trigger", ["db_type"] = "type", ["db_synonym"] = "synonym",
    };

    public static readonly Dictionary<string, string> Tags = new()
    {
        ["project"] = "project", ["file"] = "file", ["area"] = "area", ["solution"] = "solution", ["library"] = "library",
        ["release"] = "release", ["concept"] = "concept", ["db_table"] = "table", ["db_view"] = "view", ["db_proc"] = "proc",
        ["db_function"] = "function", ["db_trigger"] = "trigger", ["db_type"] = "type", ["db_synonym"] = "synonym",
    };

    public static readonly (string Tag, string Rgb)[] Colors =
    [
        ("project", "E67E22"), ("file", "3498DB"), ("table", "2ECC71"), ("proc", "9B59B6"),
        ("function", "F1C40F"), ("view", "1ABC9C"), ("release", "E74C3C"), ("library", "95A5A6"),
        ("concept", "E91E63"),
    ];

    private static readonly HashSet<string> Reserved =
        new(["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).Select(i => $"COM{i}"), .. Enumerable.Range(1, 9).Select(i => $"LPT{i}")]);

    public const string Manifest = ".codegraph-manifest.json";

    public const string HomeText = "## Adding your own notes\n\nPut your own notes anywhere outside the generated folders " +
        "(`Projects`, `Areas`, `Code`, `Database`, `Libraries`, `Releases`, `Concepts`, `Solutions`) - or in a " +
        "`Notes/` folder. They are never touched when the vault is regenerated. Link to generated notes by " +
        "name, e.g. `[[frmMain.frm]]` or `[[Orders]]`.";

    [GeneratedRegex("""[\\/:*?"<>|#^\[\]\x00-\x1f]""", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeChars();

    [GeneratedRegex(@"[|\[\]\n]", RegexOptions.CultureInvariant)]
    private static partial Regex LabelChars();

    [GeneratedRegex(@"[\\/]+", RegexOptions.CultureInvariant)]
    private static partial Regex Slashes();

    public static string SafeName(string s, int cap = 100)
    {
        s = UnsafeChars().Replace(s, "_").Trim();
        if (s.Length > cap) s = s[..cap];
        s = s.Trim().TrimEnd('.', ' ').Trim();
        if (s.Length == 0) s = "_";
        string head = s.Split('.')[0].ToUpperInvariant();
        return Reserved.Contains(head) ? "_" + s : s;
    }

    public static string LabelOf(string? s)
    {
        var r = LabelChars().Replace(s ?? "", " ").Trim();
        return r.Length == 0 ? "?" : r;
    }

    private static string DumpString(string s, bool asciiOnly)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (asciiOnly && c > 0x7e)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>YAML front-matter value: JSON for strings/lists/bools (like json.dumps), plain text for numbers.</summary>
    public static string YamlVal(JsonNode? v)
    {
        switch (v)
        {
            case JsonArray a:
                return "[" + string.Join(", ", a.Select(YamlVal)) + "]";
            case JsonValue jv:
                if (jv.TryGetValue(out string? s)) return DumpString(s, false);
                if (jv.TryGetValue(out bool b)) return b ? "true" : "false";
                return jv.ToString();
            case null:
                return "null";
            default:
                return v.ToJsonString();
        }
    }

    private static string Cell(object? s) =>
        Convert.ToString(s, CultureInfo.InvariantCulture)!.Replace("|", "\\|").Replace("\n", " ");

    public static List<string> Table(string[] headers, IReadOnlyCollection<object?[]> rows)
    {
        if (rows.Count == 0) return [];
        var res = new List<string>
        {
            "| " + string.Join(" | ", headers) + " |",
            "|" + string.Join("|", headers.Select(_ => "---")) + "|",
        };
        res.AddRange(rows.Select(r => "| " + string.Join(" | ", r.Select(Cell)) + " |"));
        return res;
    }

    private static string Times(int n) => n > 1 ? $" x{n}" : "";

    internal static string Fmt(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ vault
    private sealed class Vault
    {
        private readonly Graph _g;
        private readonly Views _v;
        private readonly int _maxRoutines;
        private readonly string _prefix;
        public string Root { get; }
        private readonly Dictionary<string, List<string>> _fsyms = [];
        private readonly Dictionary<string, string> _paths;
        private readonly Dictionary<string, List<string>> _areaFiles = [];
        private readonly Dictionary<string, List<string>> _areaProjects = [];

        private static readonly HashSet<string> Routines = [.. Views.RoutineKinds, "event"];
        private static readonly StringComparer Ord = StringComparer.Ordinal;

        public Vault(Graph g, Config cfg)
        {
            _g = g;
            _v = new Views(g);
            var ob = cfg.Obsidian;
            _maxRoutines = ob.MaxRoutinesPerNote > 0 ? ob.MaxRoutinesPerNote : 400;
            var folder = Slashes().Split(ob.Folder ?? "").Where(p => p.Trim().Length > 0).Select(p => SafeName(p)).ToList();
            _prefix = folder.Count > 0 ? string.Join("/", folder) + "/" : "";
            Root = Path.Combine([cfg.Vault!, .. folder]);
            _paths = AssignPaths();
            foreach (var n in g.Nodes)
            {
                if (n.Kind == "file")
                    Of(_areaFiles, $"area:{n.Str("repo")}:{AreaStr(n)}").Add(n.Id);
                else if (n.Kind == "project")
                    Of(_areaProjects, $"area:{n.Str("repo")}:{AreaStr(n)}").Add(n.Id);
            }
        }

        private static string AreaStr(Node n) => n.Str("area") ?? "None";

        private static List<string> Of(Dictionary<string, List<string>> d, string k) => d.TryGetValue(k, out var l) ? l : d[k] = [];

        private Node N(string id) => _g.GetNode(id)!;

        private static string S(Node n, string key) => n.Str(key) ?? "";

        private static string? First(params string?[] xs) => xs.FirstOrDefault(x => !string.IsNullOrEmpty(x));

        private static List<string> StrList(Node n, string key) =>
            n.Data[key] is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : [];

        // ------------------------------------------------------------------ paths & links
        private static string[]? Place(Node n)
        {
            string k = n.Kind;
            string name = First(n.Str("name"), n.Id)!;
            return k switch
            {
                "project" => ["Projects", name],
                "area" => ["Areas", S(n, "repo"), name],
                "file" => ["Code", S(n, "repo"), .. S(n, "path").Split('/')],
                "solution" => ["Solutions", S(n, "repo"), .. S(n, "path").Split('/')],
                "library" => ["Libraries", name],
                "release" => ["Releases", name],
                "concept" => ["Concepts", name],
                _ when DbDirs.TryGetValue(k, out var dir) => ["Database", dir, name],
                _ => null,
            };
        }

        private Dictionary<string, string> AssignPaths()
        {
            var paths = new Dictionary<string, string>();
            var seen = new HashSet<string> { "home", "codegraph report" };
            foreach (var nid in _g.Nodes.Select(n => n.Id).Order(Ord))
            {
                var raw = Place(N(nid));
                if (raw is null) continue;
                var parts = raw.Select(p => SafeName(p)).ToArray();
                string bas = parts[^1];
                int i = 1;
                while (seen.Contains(string.Join("/", parts).ToLowerInvariant()))
                {
                    i++;
                    parts[^1] = $"{bas} ({i})";
                }
                seen.Add(string.Join("/", parts).ToLowerInvariant());
                paths[nid] = string.Join("/", parts);
            }
            return paths;
        }

        public string Link(string nid, string? label = null)
        {
            var n = _g.GetNode(nid);
            if (n is null) return LabelOf(First(label, nid));
            if (nid.StartsWith("sym:", StringComparison.Ordinal))
            {
                label = First(label, n.Str("qual"), n.Str("name"));
                nid = _v.FileOf.GetValueOrDefault(nid, nid);
            }
            else if (n.Kind == "repo")
                return $"[[{_prefix}Home|{LabelOf(First(label, n.Str("name")))}]]";
            label = First(label, _g.GetNode(nid)?.Str("name"), nid);
            return _paths.TryGetValue(nid, out var p) ? $"[[{_prefix}{p}|{LabelOf(label)}]]" : LabelOf(label);
        }

        private List<string> FSyms(string fid) =>
            _fsyms.TryGetValue(fid, out var l) ? l : _fsyms[fid] = _v.FileSymbols(fid);

        // ------------------------------------------------------------------ shared pieces
        private static JsonNode? J(object? v) => v switch
        {
            null => null,
            string s => JsonValue.Create(s),
            int i => JsonValue.Create(i),
            bool b => JsonValue.Create(b),
            JsonNode jn => jn,
            IEnumerable<string> l => new JsonArray(l.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            _ => JsonValue.Create(v.ToString()),
        };

        private sealed class Extra : List<(string Key, JsonNode? Val)>
        {
            public Extra Set(string key, object? val)
            {
                var jv = J(val);
                int i = FindIndex(x => x.Key == key);
                if (i >= 0) this[i] = (key, jv); else Add((key, jv));
                return this;
            }
        }

        private List<string> Frontmatter(Node n, Extra? extra = null, IEnumerable<string>? tags = null)
        {
            string k = n.Kind;
            var d = new Extra();
            d.Set("type", DbDirs.ContainsKey(k) ? DbLabel[k].Replace(' ', '_') : k)
                .Set("repo", n.Data["repo"]).Set("path", n.Data["path"]).Set("lang", n.Data["lang"]).Set("version", n.Data["version"]);
            if (extra is not null) foreach (var (key, val) in extra) d.Set(key, val);
            var tg = new List<string> { $"codegraph/{Tags.GetValueOrDefault(k, k)}" };
            if (tags is not null) tg.AddRange(tags);
            if (!string.IsNullOrEmpty(n.Str("lang"))) tg.Add($"lang/{n.Str("lang")}");
            var lines = new List<string> { "---" };
            foreach (var (key, val) in d)
            {
                if (val is null || (val is JsonValue sv && sv.TryGetValue(out string? s) && s.Length == 0) || (val is JsonArray ar && ar.Count == 0))
                    continue;
                lines.Add($"{key}: {YamlVal(val)}");
            }
            lines.Add("tags: [" + string.Join(", ", tg.Distinct()) + "]");
            lines.Add("---");
            lines.Add("");
            return lines;
        }

        private string Note(Node n, string title, string summary, List<(string Head, List<string> Lines)> sections,
            Extra? extra = null, IEnumerable<string>? tags = null)
        {
            var o = Frontmatter(n, extra, tags);
            o.AddRange([$"# {title}", "", summary, ""]);
            foreach (var (head, lines) in sections)
                if (lines.Count > 0)
                {
                    o.AddRange([$"## {head}", ""]);
                    o.AddRange(lines);
                    o.Add("");
                }
            o.AddRange(Generic(n.Id));
            return string.Join("\n", o).TrimEnd() + "\n";
        }

        private static bool Truthy(JsonNode? n) => CodeGraph.Knowledge.KnowledgeLoader.Truthy(n);

        private static string NoteSuffix(Edge e) => Truthy(e.Data["note"]) ? $" - {e.Data["note"]}" : "";

        private static string EdgeLabel(Edge e) => First(e.Str("label"), e.Relation)!;

        private List<string> Generic(string nid)
        {
            var n = N(nid);
            var o = new List<string>();
            var rel = new List<string>();
            foreach (var e in _v.OutOf(nid))
                if (Truthy(e.Data["custom"]))
                    rel.Add($"- **{EdgeLabel(e)}** -> {Link(e.Target)}{NoteSuffix(e)}");
            foreach (var e in _v.InOf(nid))
                if (Truthy(e.Data["custom"]))
                    rel.Add($"- {Link(e.Source)} **{EdgeLabel(e)}** -> this{NoteSuffix(e)}");
            if (rel.Count > 0) { o.AddRange(["## Other relations", ""]); o.AddRange(rel); o.Add(""); }
            if (n.Data["links"] is JsonArray la)
            {
                var links = la.OfType<JsonObject>().Select(l =>
                    $"- {First(l["relation"]?.ToString(), "link")}: <{l["url"]}>" + (Truthy(l["note"]) ? $" - {l["note"]}" : "")).ToList();
                if (links.Count > 0) { o.AddRange(["## Links", ""]); o.AddRange(links); o.Add(""); }
            }
            if (Truthy(n.Data["knowledge_notes"]) && n.Data["knowledge_notes"] is JsonArray kn)
            {
                o.AddRange(["## Notes", ""]);
                foreach (var t in kn) { o.Add(t?.ToString() ?? ""); o.Add(""); }
            }
            return o;
        }

        private List<object?[]> Released(string nid)
        {
            var rows = new List<(string Date, string Name, string Ver)>();
            foreach (var e in _v.OutOf(nid))
                if (e.Relation == "released_in")
                {
                    var r = N(e.Target);
                    rows.Add((S(r, "date"), r.Name, e.Str("version") ?? ""));
                }
            return rows.OrderByDescending(r => r.Date, Ord).ThenByDescending(r => r.Name, Ord).ThenByDescending(r => r.Ver, Ord)
                .Select(r => new object?[] { Link($"rel:{r.Name}"), r.Date, r.Ver }).ToList();
        }

        private List<string> Counted(Counter<string> counter, int cap = 50, string word = "refs") =>
            counter.MostCommon(cap).Select(kv => $"- {Link(kv.Key)} - {kv.Value} {word}").ToList();

        private static List<string> Sorted(IEnumerable<string> l) => l.Order(Ord).ToList();

        // ------------------------------------------------------------------ project
        private string Project(string nid, Node n)
        {
            var v = _v;
            var files = v.ProjectFiles(nid);
            int loc = files.Sum(f => N(f).Int("loc") ?? 0);
            string repo = S(n, "repo");
            string tp = string.Join(" / ", new[] { n.Str("ptype"), n.Str("lang") }.Where(p => !string.IsNullOrEmpty(p)));
            string summary = $"{(tp.Length > 0 ? tp : "Project")} in {Link("repo:" + repo)}, {files.Count} files, {Fmt(loc)} lines of code.";
            var pf = StrList(n, "project_files");
            var det = new List<string>
            {
                !string.IsNullOrEmpty(n.Str("ptype")) ? $"- Type: {n.Str("ptype")}" : "",
                !string.IsNullOrEmpty(n.Str("output")) ? $"- Output: `{n.Str("output")}`" : "",
                !string.IsNullOrEmpty(n.Str("version")) ? $"- Version: {n.Str("version")}" : "",
                !string.IsNullOrEmpty(n.Str("framework")) ? $"- Framework: {n.Str("framework")}" : "",
                $"- Area: {Link($"area:{repo}:{AreaStr(n)}")}",
                pf.Count > 0 ? "- Project files: " + string.Join(", ", pf.Select(p => $"`{p}`")) : "",
            };
            var rel = Released(nid);
            var dep = v.OutOf(nid).Where(e => e.Relation == "depends_on")
                .Select(e => $"- {Link(e.Target)}" + (!string.IsNullOrEmpty(e.Str("via")) ? $" (via {e.Str("via")})" : "")).ToList();
            dep.AddRange(v.OutOf(nid).Where(e => e.Relation == "uses_code_of").Select(e => $"- {Link(e.Target)} - code used in {e.Count} places"));
            var used = v.InOf(nid).Where(e => e.Relation == "depends_on")
                .Select(e => $"- {Link(e.Source)}" + (!string.IsNullOrEmpty(e.Str("via")) ? $" (via {e.Str("via")})" : "")).ToList();
            used.AddRange(v.InOf(nid).Where(e => e.Relation == "uses_code_of").Select(e => $"- {Link(e.Source)} - code used in {e.Count} places"));
            var libs = v.OutOf(nid).Where(e => e.Relation == "uses_library").Select(e => $"- {Link(e.Target)}").ToList();
            var ships = Sorted(v.OutOf(nid).Where(e => e.Relation == "ships" && N(e.Target).Kind == "project").Select(e => $"- {Link(e.Target)}"));
            ships.AddRange(Sorted(v.OutOf(nid).Where(e => e.Relation == "ships" && N(e.Target).Kind != "project").Select(e => $"- {Link(e.Target)} (external)")));
            var shippedBy = v.InOf(nid).Where(e => e.Relation == "ships" && N(e.Source).Kind == "project").Select(e => $"- {Link(e.Source)}").ToList();
            var groups = new Dictionary<string, List<string>>();
            foreach (var f in files)
            {
                string? main = FSyms(f).Select(s => N(s).Kind).FirstOrDefault(k => Views.TypeKinds.Contains(k));
                string key = First(main, N(f).Str("lang")) ?? "other";
                (groups.TryGetValue(key, out var l) ? l : groups[key] = []).Add(f);
            }
            var fl = new List<string>();
            foreach (var (gk, fs) in groups.OrderBy(kv => -kv.Value.Count).ThenBy(kv => kv.Key, Ord))
            {
                fl.AddRange([$"### {gk} ({fs.Count})", ""]);
                fl.AddRange(fs.OrderBy(x => S(N(x), "path").ToLowerInvariant(), Ord).Select(f => $"- {Link(f)} - {N(f).Int("loc") ?? 0} LOC"));
                fl.Add("");
            }
            var db = new Dictionary<string, List<object?[]>>();
            foreach (var (k, c) in v.ProjectDb(nid).MostCommon())
                (db.TryGetValue(k.Rel, out var l2) ? l2 : db[k.Rel] = []).Add([Link(k.Db), DbLabel.GetValueOrDefault(N(k.Db).Kind, ""), c]);
            var dbl = new List<string>();
            foreach (var r in new[] { "writes", "reads", "executes", "calls" })
                if (db.TryGetValue(r, out var rows) && rows.Count > 0)
                {
                    dbl.AddRange([$"### {char.ToUpperInvariant(r[0])}{r[1..]}", ""]);
                    dbl.AddRange(Table(["Object", "Kind", "Count"], rows.Take(200).ToList()));
                    dbl.Add("");
                }
            var miss = StrList(n, "missing_members");
            var warn = new List<string>();
            if (miss.Count > 0)
            {
                warn.Add($"> [!warning] {miss.Count} listed member file(s) were not found on disk:");
                warn.Add(">");
                warn.AddRange(miss.Take(30).Select(m => $"> - `{m}`"));
                if (miss.Count > 30) warn.Add($"> - ... and {miss.Count - 30} more");
            }
            var slns = v.InOf(nid).Where(e => e.Relation == "contains" && N(e.Source).Kind == "solution").Select(e => $"- {Link(e.Source)}").ToList();
            var extra = new Extra().Set("ptype", n.Data["ptype"]).Set("output", n.Data["output"]).Set("framework", n.Data["framework"]).Set("loc", loc);
            return Note(n, n.Name, summary,
            [
                ("Details", det.Where(d => d.Length > 0).ToList()),
                ("Releases", Table(["Release", "Date", "Module version"], rel)),
                ("Shipped by installers", Sorted(shippedBy)), ("Installer ships", ships),
                ("Depends on", dep), ("Used by", used), ("Libraries", libs), ("Missing members", warn),
                ("Solutions", slns), ("Files", DropLast(fl)), ("Database access", DropLast(dbl)),
            ], extra);
        }

        private static List<string> DropLast(List<string> l) => l.Count > 0 ? l.GetRange(0, l.Count - 1) : l;

        // ------------------------------------------------------------------ file
        private string RoutineLine(string fid, string sid)
        {
            var s = N(sid);
            var meta = new List<string> { s.Kind.Replace('_', ' ') };
            if ((s.Int("line") ?? 0) != 0) meta.Add($"L{s.Str("line")}");
            if ((s.Int("lines") ?? 0) != 0) meta.Add($"{s.Str("lines")} lines");
            string line = $"- **{LabelOf(First(s.Str("qual"), s.Name))}** ({string.Join(", ", meta)})";
            if (!string.IsNullOrEmpty(s.Str("signature")))
            {
                string sig = s.Str("signature")!.Replace('`', '\'').Replace('\n', ' ');
                line += " `" + (sig.Length > 120 ? sig[..120] : sig) + "`";
            }
            if (s.Data["routes"] is JsonArray ra && ra.Count > 0)
                line += " - serves " + string.Join(", ", ra.Take(4).Select(r => $"`{r}`"));
            var callersApi = Sorted(_v.InOf(sid).Where(e => e.Relation == "calls_api")
                .Select(e => Link(_v.FileOf.GetValueOrDefault(e.Source, e.Source))).ToHashSet());
            var calls = new List<string>();
            var db = new Dictionary<string, List<string>>();
            var seen = new HashSet<string>();
            var apis = new List<string>();
            foreach (var e in _v.OutOf(sid).OrderByDescending(e => e.Count))
            {
                string t = e.Target, r = e.Relation;
                var tn = _g.GetNode(t);
                if (tn is null || seen.Contains(t)) continue;
                if (tn.Kind.StartsWith("db_", StringComparison.Ordinal))
                {
                    if (r is "reads" or "writes" or "executes" or "calls")
                    {
                        seen.Add(t);
                        string key = r == "calls" ? "executes" : r;
                        (db.TryGetValue(key, out var dl) ? dl : db[key] = []).Add(Link(t));
                    }
                }
                else if (r == "calls_api")
                {
                    seen.Add(t);
                    apis.Add(Link(t, First(tn.Str("qual"), tn.Name)));
                }
                else if (r is "calls" or "calls_native" or "instantiates")
                {
                    seen.Add(t);
                    string tf = _v.FileOf.GetValueOrDefault(t, t);
                    string nm = First(tn.Str("qual"), tn.Name)!;
                    calls.Add(tf == fid ? LabelOf(nm) : Link(t, nm));
                }
            }
            var parts = new List<string>();
            if (calls.Count > 0)
                parts.Add("calls: " + string.Join(", ", calls.Take(12)) + (calls.Count > 12 ? $" (+{calls.Count - 12})" : ""));
            if (apis.Count > 0)
                parts.Add("calls API: " + string.Join(", ", apis.Take(8)));
            if (callersApi.Count > 0)
                parts.Add("called from: " + string.Join(", ", callersApi.Take(8)) + (callersApi.Count > 8 ? $" (+{callersApi.Count - 8})" : ""));
            foreach (var r in new[] { "reads", "writes", "executes" })
                if (db.TryGetValue(r, out var d) && d.Count > 0)
                    parts.Add($"{r}: " + string.Join(", ", d.Take(8)) + (d.Count > 8 ? $" (+{d.Count - 8})" : ""));
            return line + (parts.Count > 0 ? " - " + string.Join("; ", parts) : "");
        }

        private string File(string fid, Node n)
        {
            var v = _v;
            var syms = FSyms(fid);
            var types = syms.Where(s => Views.TypeKinds.Contains(N(s).Kind)).ToList();
            var routines = syms.Where(s => Routines.Contains(N(s).Kind)).ToList();
            var pj = v.ProjectsOf(fid);
            string repo = S(n, "repo");
            string area = $"area:{repo}:{AreaStr(n)}";
            string lang = First(n.Str("lang")) ?? "Source";
            string summary = $"{lang} file in {Link(area)}, {n.Int("loc") ?? 0} lines of code"
                             + (types.Count > 0 ? $", defines {types.Count} type(s)" : "") + $". Path: `{S(n, "path")}`.";
            var proj = pj.Select(p => $"- {Link(p)}").ToList();
            if (proj.Count == 0 && n.Bool("orphan")) proj = ["- Not in any project (orphan source file)"];
            var tl = types.Take(100).Select(s => $"- `{N(s).Kind}` {LabelOf(First(N(s).Str("qual"), N(s).Name))}").ToList();
            var byRel = new Dictionary<string, List<string>>();
            if (v.FileDb.TryGetValue(fid, out var fdb))
                foreach (var (k, c) in fdb.MostCommon())
                    (byRel.TryGetValue(k.Rel, out var l) ? l : byRel[k.Rel] = [])
                        .Add($"- {Link(k.Db)}{Times(c)}" + (!string.IsNullOrEmpty(k.Via) ? $" (via {k.Via})" : ""));
            var db = new List<string>();
            var order = new[] { "writes", "reads", "executes" };
            foreach (var r in byRel.Keys.OrderBy(r => { int i = Array.IndexOf(order, r); return i >= 0 ? i : 9; }).ThenBy(r => r, Ord))
            {
                db.AddRange([$"**{char.ToUpperInvariant(r[0])}{r[1..]}**", ""]);
                db.AddRange(byRel[r]);
                db.Add("");
            }
            var rl = routines.Take(_maxRoutines).Select(s => RoutineLine(fid, s)).ToList();
            if (routines.Count > _maxRoutines) rl.Add($"- ... {routines.Count - _maxRoutines} more routines not listed");
            var extra = new Extra().Set("loc", n.Data["loc"]).Set("area", n.Data["area"]).Set("projects", pj.Select(p => N(p).Name).ToList());
            if (n.Data["attrs"] is JsonObject attrs)
                foreach (var key in new[] { "assemblyversion", "fileversion", "version" })
                    if (attrs[key] is JsonValue av && av.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
                        extra.Set(key, av);
            if (n.Bool("orphan")) extra.Set("orphan", true);
            var services = StrList(n, "services");
            var det = new List<string>
            {
                !string.IsNullOrEmpty(n.Str("namespace")) ? $"- Namespace: `{n.Str("namespace")}`" : "",
                !string.IsNullOrEmpty(n.Str("component")) ? $"- Component: {n.Str("component")}" : "",
                !string.IsNullOrEmpty(n.Str("script_lang")) ? $"- Script language: {n.Str("script_lang")}" : "",
                services.Count > 0 ? "- Installs services: " + string.Join(", ", services.Select(x => $"`{x}`")) : "",
            };
            if (n.Data["custom_actions"] is JsonArray ca && ca.Count > 0)
                det.Add("- Custom actions: " + string.Join(", ", ca.Take(30).Select(c =>
                {
                    var co = c as JsonObject;
                    string? entry = First(co?["DllEntry"]?.ToString(), co?["ExeCommand"]?.ToString());
                    return $"`{co?["Id"]}`" + (entry is not null ? $" ({entry})" : "");
                })));
            var shipped = new SortedSet<string>(Ord);
            foreach (var s_ in new[] { fid }.Concat(syms))
                foreach (var e in v.OutOf(s_))
                    if (e.Relation == "ships")
                        shipped.Add(Link(e.Target) + (!string.IsNullOrEmpty(e.Str("via")) ? $" ({e.Str("via")})" : ""));
            var shipsL = shipped.Take(500).Select(x => $"- {x}").ToList();
            return Note(n, n.Name, summary,
            [
                ("Projects", proj), ("Details", det.Where(d => d.Length > 0).ToList()), ("Types defined", tl),
                ("Ships", shipsL), ("Uses code in", v.FileOut.TryGetValue(fid, out var fo) ? Counted(fo) : []),
                ("Used by", v.FileIn.TryGetValue(fid, out var fi) ? Counted(fi) : []),
                ("Database", DropLast(db)), ("Routines", rl),
                ("Released in", Table(["Release", "Date", "Version"], Released(fid))),
            ], extra);
        }

        // ------------------------------------------------------------------ database objects
        private static string RelCounts(Counter<string> rc) => string.Join(", ", rc.MostCommon().Select(kv => $"{kv.Key}{Times(kv.Value)}"));

        private string DbObj(string nid, Node n)
        {
            var v = _v;
            string k = n.Kind;
            string label = DbLabel[k];
            var users = new Dictionary<(string Name, string Id), Dictionary<string, Counter<string>>>();
            var objs = new Dictionary<string, Counter<string>>();
            if (v.DbUsers.TryGetValue(nid, out var du))
                foreach (var (key, c) in du)
                {
                    var (src, r) = key;
                    if (r is "alters" or "references" or "triggers_on") continue;
                    if (src.StartsWith("db:", StringComparison.Ordinal))
                    {
                        (objs.TryGetValue(src, out var oc) ? oc : objs[src] = new Counter<string>()).Add(r, c);
                        continue;
                    }
                    var sn = N(src);
                    var pjs = StrList(sn, "projects");
                    var groups = pjs.Select(p => (N(p).Name, p)).ToList();
                    if (groups.Count == 0) groups = [($"Area {AreaStr(sn)} (no project)", $"area:{S(sn, "repo")}:{AreaStr(sn)}")];
                    foreach (var grp in groups)
                    {
                        if (!users.TryGetValue(grp, out var fs)) users[grp] = fs = [];
                        if (!fs.TryGetValue(src, out var rc)) fs[src] = rc = new Counter<string>();
                        rc.Add(r, c);
                    }
                }
            var ul = new List<string>();
            int shown = 0;
            foreach (var ((gname, gid), fs) in users.OrderBy(kv => -kv.Value.Values.Sum(c => c.Total)))
            {
                if (shown >= 300)
                {
                    ul.Add($"... {users.Values.Sum(f => f.Count) - shown} more file(s) not listed");
                    break;
                }
                ul.AddRange([$"### {Link(gid, gname)}", ""]);
                foreach (var (src, rc) in fs.OrderBy(kv => -kv.Value.Total).ThenBy(kv => kv.Key, Ord))
                {
                    ul.Add($"- {Link(src)} - " + RelCounts(rc));
                    shown++;
                }
                ul.Add("");
            }
            if (ul.Count > 0 && ul[^1] == "") ul.RemoveAt(ul.Count - 1);
            var ol = objs.OrderBy(kv => -kv.Value.Total).Select(kv => $"- {Link(kv.Key)} - " + RelCounts(kv.Value)).ToList();
            var uses = new Dictionary<string, List<string>>();
            foreach (var e in v.OutOf(nid))
                if (e.Relation is "reads" or "writes" or "executes" or "calls" && N(e.Target).Kind.StartsWith("db_", StringComparison.Ordinal))
                    (uses.TryGetValue(e.Relation, out var ul2) ? ul2 : uses[e.Relation] = []).Add($"- {Link(e.Target)}{Times(e.Count)}");
            var ql = new List<string>();
            foreach (var r in new[] { "writes", "reads", "executes", "calls" })
                if (uses.TryGetValue(r, out var q) && q.Count > 0)
                {
                    ql.AddRange([$"**{char.ToUpperInvariant(r[0])}{r[1..]}**", ""]);
                    ql.AddRange(q);
                    ql.Add("");
                }
            var colRows = new List<object?[]>();
            foreach (var c in StrList(n, "columns"))
            {
                var t = c.TrimStart();
                int sp = t.IndexOfAny([' ', '\t', '\n', '\r']);
                if (sp < 0) colRows.Add([t, ""]);
                else colRows.Add([t[..sp], t[sp..].TrimStart()]);
            }
            var col = Table(["Column", "Definition"], colRows);
            var df = new List<string>();
            string? file = n.Str("file");
            if (file is not null && _g.HasNode(file))
                df.Add($"- {Link(file)}" + ((n.Int("line") ?? 0) != 0 ? $" (line {n.Str("line")})" : ""));
            var also = StrList(n, "also_defined_in").Where(f => _g.HasNode(f)).Select(f => $"- {Link(f)}").ToList();
            var fk = v.OutOf(nid).Where(e => e.Relation == "references").Select(e => $"- {Link(e.Target)}").ToList();
            var fki = v.InOf(nid).Where(e => e.Relation == "references").Select(e => $"- {Link(e.Source)}").ToList();
            var trg = v.InOf(nid).Where(e => e.Relation == "triggers_on").Select(e => $"- {Link(e.Source)}").ToList();
            var on = v.OutOf(nid).Where(e => e.Relation == "triggers_on").Select(e => $"- {Link(e.Target)}").ToList();
            int nfiles = du is null ? 0 : du.Keys.Select(x => x.Src).Where(s => s.StartsWith("file:", StringComparison.Ordinal)).ToHashSet().Count;
            string summary = $"SQL {label}" + (colRows.Count > 0 ? $" with {colRows.Count} columns" : "") +
                             $", used by {nfiles} file(s) and {objs.Count} database object(s).";
            return Note(n, n.Name, summary,
            [
                ("Columns", col), ("Defined in", df), ("Also defined / altered in", also), ("Fires on", on),
                ("Triggers", trg), ("References (foreign keys)", fk), ("Referenced by", fki),
                ("Uses", DropLast(ql)), ("Used by database objects", ol), ("Used by", ul),
                ("Released in", Table(["Release", "Date", "Version"], Released(nid))),
            ], tags: ["lang/sql"]);
        }

        // ------------------------------------------------------------------ small kinds
        private string Library(string nid, Node n)
        {
            var ps = _v.InOf(nid).Where(e => e.Relation == "uses_library").Select(e => $"- {Link(e.Source)}").ToList();
            var imp = Sorted(_v.InOf(nid).Where(e => e.Relation == "imports").Select(e => $"- {Link(_v.FileOf.GetValueOrDefault(e.Source, e.Source))}").ToHashSet());
            var shp = Sorted(_v.InOf(nid).Where(e => e.Relation == "ships").Select(e => $"- {Link(_v.FileOf.GetValueOrDefault(e.Source, e.Source))}").ToHashSet());
            return Note(n, n.Name, $"{First(n.Str("lib_kind")) ?? "External"} library, used by {ps.Count} project(s).",
                [("Used by projects", Sorted(ps)), ("Imported by files", imp.Take(300).ToList()), ("Shipped by installer files", shp)],
                new Extra().Set("lib_kind", n.Data["lib_kind"]));
        }

        private string Release(string nid, Node n)
        {
            var mods = _v.InOf(nid).Where(e => e.Relation == "released_in").Select(e => (M: N(e.Source), E: e))
                .OrderBy(x => x.M.Name.ToLowerInvariant(), Ord).ToList();
            var rows = mods.Select(x => new object?[] { Link(x.M.Id), x.M.Kind.Replace("db_", ""), x.E.Str("version") ?? "" }).ToList();
            var head = new List<string>
            {
                !string.IsNullOrEmpty(n.Str("date")) ? $"- Date: {n.Str("date")}" : "",
                !string.IsNullOrEmpty(n.Str("status")) ? $"- Status: {n.Str("status")}" : "",
            };
            List<string> notes = !string.IsNullOrEmpty(n.Str("notes")) ? [n.Str("notes")!.Trim()] : [];
            return Note(n, $"Release {n.Name}",
                $"Release {n.Name}" + (!string.IsNullOrEmpty(n.Str("date")) ? $" ({n.Str("date")})" : "") + $" with {rows.Count} module(s).",
                [("Details", head.Where(h => h.Length > 0).ToList()), ("Notes", notes), ("Modules", Table(["Module", "Kind", "Version"], rows))],
                new Extra().Set("date", n.Data["date"]));
        }

        private string Area(string nid, Node n)
        {
            var files = _areaFiles.GetValueOrDefault(nid, []).OrderBy(f => S(N(f), "path").ToLowerInvariant(), Ord).ToList();
            var projs = _areaProjects.GetValueOrDefault(nid, []).OrderBy(p => N(p).Name.ToLowerInvariant(), Ord).ToList();
            int loc = files.Sum(f => N(f).Int("loc") ?? 0);
            var prow = projs.Select(p => new object?[] { Link(p), N(p).Str("ptype") ?? "", N(p).Str("version") ?? "", _v.ProjectFiles(p).Count }).ToList();
            var frow = files.Take(300).Select(f => new object?[] { Link(f, N(f).Str("path")), N(f).Str("lang") ?? "", N(f).Int("loc") ?? 0 }).ToList();
            var langs = new Counter<string>();
            foreach (var f in files) langs.Add(N(f).Str("lang") ?? "None");
            var fl = Table(["File", "Language", "LOC"], frow);
            if (files.Count > 300) fl.AddRange(["", $"... and {files.Count - 300} more files"]);
            return Note(n, n.Name,
                $"Top-level folder of {Link("repo:" + S(n, "repo"))}: {files.Count} files, {Fmt(loc)} lines of code " +
                $"({string.Join(", ", langs.MostCommon(4).Select(kv => $"{kv.Key} {kv.Value}"))}).",
                [("Projects", Table(["Project", "Type", "Version", "Files"], prow)), ("Files", fl),
                 ("Released in", Table(["Release", "Date", "Version"], Released(nid)))]);
        }

        private string Solution(string nid, Node n)
        {
            var ps = _v.OutOf(nid).Where(e => e.Relation == "contains").Select(e => $"- {Link(e.Target)}").ToList();
            return Note(n, n.Name, $"Solution in {Link("repo:" + S(n, "repo"))} with {ps.Count} project(s). Path: `{S(n, "path")}`.",
                [("Projects", Sorted(ps))]);
        }

        private string Concept(string nid, Node n) => Note(n, n.Name, "Concept (from knowledge files).", []);

        // ------------------------------------------------------------------ home
        private string Home()
        {
            var v = _v;
            var nodes = _g.Nodes;
            var kinds = new Counter<string>();
            foreach (var n in nodes) kinds.Add(n.Kind);
            var projs = nodes.Where(n => n.Kind == "project")
                .OrderBy(n => S(n, "repo"), Ord).ThenBy(n => AreaStr(n).ToLowerInvariant(), Ord).ThenBy(n => n.Name.ToLowerInvariant(), Ord).ToList();
            var prow = new List<object?[]>();
            foreach (var p in projs)
            {
                var fs = v.ProjectFiles(p.Id);
                prow.Add([Link(p.Id), p.Str("ptype") ?? "", p.Str("version") ?? "", p.Str("area") ?? "", fs.Count, Fmt(fs.Sum(f => N(f).Int("loc") ?? 0))]);
            }
            var repoIds = nodes.Where(n => n.Kind == "repo").Select(n => n.Id).Order(Ord).ToList();
            var repos = new List<string>();
            foreach (var r in repoIds)
            {
                var rn = N(r);
                int nf = nodes.Count(n => n.Kind == "file" && n.Str("repo") == rn.Name);
                repos.Add($"- **{rn.Name}** - {nf} files" + (!string.IsNullOrEmpty(rn.Str("path")) ? $" (`{rn.Str("path")}`)" : ""));
            }
            var areas = new List<string>();
            foreach (var r in repoIds)
            {
                string rname = N(r).Name;
                var al = nodes.Where(a => a.Kind == "area" && a.Str("repo") == rname).Select(a => a.Id)
                    .OrderBy(a => N(a).Name.ToLowerInvariant(), Ord).ToList();
                areas.AddRange(al.Select(a => $"- {Link(a)} - {_areaFiles.GetValueOrDefault(a, []).Count} files"));
            }
            var dbk = DbDirKinds.Where(k => kinds[k] > 0).Select(k => new object?[] { DbLabel[k], kinds[k] }).ToList();
            var top = nodes.Where(n => n.Kind == "db_table")
                .Select(n => (C: v.DbUsers.TryGetValue(n.Id, out var du) ? du.Keys.Select(x => x.Src).Where(s => s.StartsWith("file:", StringComparison.Ordinal)).ToHashSet().Count : 0, T: n.Id))
                .OrderByDescending(x => x.C).ThenByDescending(x => x.T, Ord).Take(15).ToList();
            var rels = nodes.Where(n => n.Kind == "release")
                .OrderByDescending(n => S(n, "date"), Ord).ThenByDescending(n => n.Name, Ord).ToList();
            var dbSec = Table(["Object type", "Count"], dbk);
            if (top.Count > 0) dbSec.AddRange(["", "Most used tables (by distinct files):", ""]);
            dbSec.AddRange(top.Where(x => x.C > 0).Select(x => $"- {Link(x.T)} - {x.C} files"));
            var sections = new List<(string, List<string>)>
            {
                ("Totals", Table(["Kind", "Count"], kinds.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, Ord).Select(kv => new object?[] { kv.Key, kv.Value }).ToList())),
                ("Repositories", repos),
                ("Projects", Table(["Project", "Type", "Version", "Area", "Files", "LOC"], prow)),
                ("Areas", areas),
                ("Database", dbSec),
                ("Releases", rels.Select(r => $"- {Link(r.Id)}" + (!string.IsNullOrEmpty(r.Str("date")) ? $" - {r.Str("date")}" : "")).ToList()),
                ("Reports", [$"- [[{_prefix}Codegraph Report|Codegraph Report]]"]),
            };
            var o = new List<string>
            {
                "---", "type: \"home\"", "tags: [codegraph/home]", "---", "", "# Codebase map", "",
                $"Generated by codegraph: {kinds["file"]} source files, {projs.Count} projects, " +
                $"{DbDirKinds.Sum(k => kinds[k])} database objects.", "",
            };
            foreach (var (head, lines) in sections)
                if (lines.Count > 0)
                {
                    o.AddRange([$"## {head}", ""]);
                    o.AddRange(lines);
                    o.Add("");
                }
            o.Add(HomeText);
            return string.Join("\n", o) + "\n";
        }

        // ------------------------------------------------------------------ all
        public Dictionary<string, string> RenderAll(string? reportMd)
        {
            var notes = new Dictionary<string, string> { ["Home.md"] = Home() };
            if (!string.IsNullOrEmpty(reportMd))
                notes["Codegraph Report.md"] = "---\ntype: \"report\"\ntags: [codegraph/report]\n---\n\n" + reportMd.TrimEnd() + "\n";
            foreach (var (nid, p) in _paths)
            {
                var n = N(nid);
                string? text = n.Kind switch
                {
                    "project" => Project(nid, n),
                    "file" => File(nid, n),
                    "area" => Area(nid, n),
                    "solution" => Solution(nid, n),
                    "library" => Library(nid, n),
                    "release" => Release(nid, n),
                    "concept" => Concept(nid, n),
                    _ when DbDirs.ContainsKey(n.Kind) => DbObj(nid, n),
                    _ => null,
                };
                if (text is not null) notes[p + ".md"] = text;
            }
            return notes;
        }

        public VaultResult Sync(Dictionary<string, string> notes)
        {
            string root = Root;
            string mpath = Path.Combine(root, Manifest);
            var old = new List<string>();
            if (System.IO.File.Exists(mpath))
            {
                try
                {
                    if (JsonNode.Parse(System.IO.File.ReadAllText(mpath, Encoding.UTF8)) is JsonObject mo && mo["files"] is JsonArray fa)
                        old = fa.Select(x => x?.ToString() ?? "").ToList();
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            int written = 0, unchanged = 0, deleted = 0;
            foreach (var (rel, text) in notes)
            {
                if (WriteIfChanged(Path.Combine(root, Path.Combine(rel.Split('/'))), text)) written++; else unchanged++;
            }
            string normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var rel in old.Except(notes.Keys).Order(Ord))
            {
                string p;
                try { p = Path.GetFullPath(Path.Combine(root, Path.Combine(rel.Split('/')))); }
                catch (ArgumentException) { continue; }
                if (!p.StartsWith(normRoot + Path.DirectorySeparatorChar, cmp) || !System.IO.File.Exists(p)) continue;
                System.IO.File.Delete(p);
                deleted++;
                string? d = Path.GetDirectoryName(p);
                while (d is not null && !string.Equals(d, normRoot, cmp) && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                {
                    Directory.Delete(d);
                    d = Path.GetDirectoryName(d);
                }
            }
            WriteIfChanged(mpath, ManifestJson(notes.Keys.Order(Ord)));
            return new VaultResult(written, unchanged, deleted, notes.Count);
        }
    }

    /// <summary>json.dumps({"files": [...]}, indent=1) (ASCII-escaped like Python's default).</summary>
    private static string ManifestJson(IEnumerable<string> files)
    {
        var l = files.Select(f => "  " + DumpString(f, true)).ToList();
        return l.Count == 0 ? "{\n \"files\": []\n}" : "{\n \"files\": [\n" + string.Join(",\n", l) + "\n ]\n}";
    }

    public static bool WriteIfChanged(string path, string text)
    {
        byte[] data = new UTF8Encoding(false).GetBytes(text);
        try
        {
            if (System.IO.File.Exists(path) && System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return false;
        }
        catch (IOException) { }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        System.IO.File.WriteAllBytes(path, data);
        return true;
    }

    /// <summary>Writes .obsidian/graph.json colour groups, only when the user has no graph settings yet.</summary>
    public static void WriteGraphColors(string vault)
    {
        string path = Path.Combine(vault, ".obsidian", "graph.json");
        if (System.IO.File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = System.IO.File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, NewLine = "\n" });
        w.WriteStartObject();
        w.WriteStartArray("colorGroups");
        foreach (var (tag, rgb) in Colors)
        {
            w.WriteStartObject();
            w.WriteString("query", $"tag:#codegraph/{tag}");
            w.WriteStartObject("color");
            w.WriteNumber("a", 1);
            w.WriteNumber("rgb", int.Parse(rgb, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Render all notes and sync them into the vault folder (only changed notes are written).</summary>
    public static VaultResult WriteVault(Graph g, Config cfg, string? reportMd = null)
    {
        if (string.IsNullOrEmpty(cfg.Vault)) throw new CodeGraphException("No 'vault' path configured");
        var vault = new Vault(g, cfg);
        var res = vault.Sync(vault.RenderAll(reportMd));
        if (cfg.Obsidian.GraphColors) WriteGraphColors(cfg.Vault);
        return res;
    }

    /// <summary>All note texts keyed by vault-relative path (for tests and dry runs); nothing is written.</summary>
    public static IReadOnlyDictionary<string, string> Render(Graph g, Config cfg, string? reportMd = null)
    {
        var vault = new Vault(g, cfg);
        return vault.RenderAll(reportMd);
    }
}
